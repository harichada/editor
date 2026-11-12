using System.Buffers;
using System.Text;

namespace Editor.Core;

/// <summary>One line of a laid-out page. Text is trimmed to the requested column budget on a grapheme boundary.</summary>
public sealed class PageLine
{
    public required int LineNumber { get; init; }
    public required string Text { get; init; }
    public int DisplayColumns { get; init; }
}

/// <summary>The first visible page. Producing it must not index the rest of the file.</summary>
public sealed class FirstPage
{
    public required IReadOnlyList<PageLine> Lines { get; init; }
    public long BytesScanned { get; init; }
    public bool IndexComplete { get; init; }
}

public readonly record struct DocumentStatus(int Line, int Column, string Encoding, string LineEnding, bool Modified);

/// <summary>
/// A single plain-text document. Storage is a piece table: the original bytes stay
/// immutable and edits are appended. An unmodified save copies those bytes exactly.
/// </summary>
public sealed class TextDocument : IDisposable
{
    public const long MapThreshold = 1_048_576;
    public const long MaterializeLimit = 64L * 1024 * 1024;

    private readonly EditorSettings _settings;
    private readonly PieceTable _table = new();
    private readonly AddBuffer _add = new();
    private readonly LineIndex _index = new();
    private readonly Stack<HistoryEntry> _undo = new();
    private readonly Stack<HistoryEntry> _redo = new();

    private IByteSource _source;
    private int _revision;
    private int _savedRevision;
    private long _logicalAdd;
    private bool _disposed;

    private TextDocument(EditorSettings settings, IByteSource source, string? path)
    {
        _settings = settings;
        _source = source;
        FilePath = path is null ? null : Path.GetFullPath(path);
        Encoding = TextEncodingProfile.Utf8(settings.AnsiCodePage);
        LineEndings = LineEndingProfile.Single(settings.DefaultLineEnding);
    }

    public EditorSettings Settings => _settings;
    public TextEncodingProfile Encoding { get; private set; }
    public LineEndingProfile LineEndings { get; private set; }
    public string? FilePath { get; private set; }
    public long ByteLength => _table.Length;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsLineIndexComplete => _index.IsComplete && _index.ScannedTo >= ByteLength;
    public long ScannedBytes => _index.ScannedTo;
    internal int PieceCount => _table.PieceCount;

    /// <summary>
    /// True when the buffer differs from the bytes it was loaded or last saved from.
    /// A new empty document is clean. Undo back to that baseline clears the flag.
    /// </summary>
    public bool IsModified => _revision != _savedRevision;

    public string DisplayName => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);

    public int CompleteLineCount
    {
        get
        {
            var count = _index.CompleteLineCount(ByteLength);
            if (count == 0 && ByteLength == 0)
                return 1;
            return count;
        }
    }

    public static TextDocument CreateNew(EditorSettings? settings = null)
    {
        settings ??= EditorSettings.AllEnabled();
        return new TextDocument(settings, EmptyByteSource.Instance, path: null);
    }

    public static TextDocument FromBytes(ReadOnlyMemory<byte> bytes, EditorSettings? settings = null)
    {
        settings ??= EditorSettings.AllEnabled();
        var copy = bytes.ToArray();
        var document = new TextDocument(settings, new OwnedByteSource(copy), path: null);
        document.LoadOriginal(copy);
        return document;
    }

    public static TextDocument FromText(string text, EditorSettings? settings = null, TextEncodingProfile? encoding = null)
    {
        settings ??= EditorSettings.AllEnabled();
        var profile = encoding ?? TextEncodingProfile.Utf8(settings.AnsiCodePage);
        return FromBytes(TextCodec.Encode(text, profile), settings);
    }

    public static TextDocument Open(string path, EditorSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        settings ??= EditorSettings.AllEnabled();
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The file does not exist.", path);

        IByteSource source;
        if (info.Length >= MapThreshold)
            source = MappedByteSource.Open(path);
        else
        {
            var owned = File.ReadAllBytes(path);
            source = new OwnedByteSource(owned);
        }

        var document = new TextDocument(settings, source, path);
        try
        {
            document.LoadOriginal(ReadPrefix(source));
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source.Dispose();
        _source = EmptyByteSource.Instance;
    }

    public string GetText()
    {
        if (ByteLength > MaterializeLimit)
            throw new NotSupportedException($"Materializing a document larger than {MaterializeLimit} bytes is not supported.");
        return TextDecoder.Decode(_table, _source, _add, 0, ByteLength, Encoding.AnsiCodePage);
    }

    public void EnsureLines(int count) => _index.Ensure(count, ByteLength, ByteLength, FindBreak);

    public string GetLineContent(int line)
    {
        EnsureLines(line + 1);
        if (line < 0 || line >= CompleteLineCount)
            throw new ArgumentOutOfRangeException(nameof(line));
        var start = _index.LineStart(line);
        var boundary = _index.LineBoundary(line, ByteLength);
        var breakBytes = BreakBytes(line, boundary);
        var contentBytes = Math.Max(0, boundary - start - breakBytes);
        return TextDecoder.Decode(_table, _source, _add, start, contentBytes, Encoding.AnsiCodePage);
    }

    public LineEndingKind GetLineEnding(int line)
    {
        EnsureLines(line + 1);
        if (line < 0 || line >= CompleteLineCount)
            return LineEndingKind.None;
        var boundary = _index.LineBoundary(line, ByteLength);
        var bytes = BreakBytes(line, boundary);
        if (bytes <= 0)
            return LineEndingKind.None;
        return ClassifyBreak(boundary - bytes, bytes);
    }

    public long GetByteOffset(int line, int utf16Column)
    {
        EnsureLines(line + 1);
        if (line < 0 || line >= CompleteLineCount)
            return ByteLength;
        var start = _index.LineStart(line);
        var boundary = _index.LineBoundary(line, ByteLength);
        var contentBytes = Math.Max(0, boundary - start - BreakBytes(line, boundary));
        if (utf16Column <= 0)
            return start;
        var position = start;
        var end = start + contentBytes;
        var chars = 0;
        while (position < end && chars < utf16Column)
        {
            var unit = TextDecoder.Read(_table, _source, _add, position, Encoding.AnsiCodePage);
            if (unit.Bytes <= 0)
                break;
            if (chars + unit.Chars > utf16Column)
                break;
            position += unit.Bytes;
            chars += unit.Chars;
        }
        return position;
    }

    public long ByteOffsetFromUtf16(int utf16Offset)
    {
        if (utf16Offset <= 0)
            return 0;
        var remaining = utf16Offset;
        var line = 0;
        var guard = 0;
        while (guard++ < 8_000_000)
        {
            EnsureLines(line + 1);
            if (line >= CompleteLineCount)
                return ByteLength;
            var content = GetLineContent(line);
            if (remaining <= content.Length)
                return GetByteOffset(line, remaining);
            remaining -= content.Length;
            var endingChars = EndingChars(GetLineEnding(line));
            if (endingChars == 0)
                return GetByteOffset(line, content.Length);
            if (remaining < endingChars)
            {
                var boundary = _index.LineBoundary(line, ByteLength);
                var breakBytes = BreakBytes(line, boundary);
                return boundary - breakBytes + (breakBytes / 2);
            }
            remaining -= endingChars;
            if (remaining == 0)
                return _index.LineBoundary(line, ByteLength);
            line++;
        }
        return ByteLength;
    }

    public void InsertAtByteOffset(long byteOffset, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return;
        var offset = SnapBack(byteOffset);
        Mutate(() =>
        {
            InsertRaw(offset, text);
            NoteInsertedEndings(text);
        });
    }

    public void InsertAtUtf16(int utf16Offset, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return;
        InsertAtByteOffset(ByteOffsetFromUtf16(utf16Offset), text);
    }

    public void DeleteByteRange(long byteOffset, long byteLength)
    {
        if (byteLength <= 0)
            return;
        var start = SnapBack(byteOffset);
        var end = SnapForward(byteOffset + byteLength);
        if (end <= start)
            return;
        Mutate(() => DeleteRaw(start, end - start));
    }

    public void DeleteUtf16(int utf16Offset, int charCount)
    {
        if (charCount <= 0)
            return;
        var start = ByteOffsetFromUtf16(utf16Offset);
        var end = ByteOffsetFromUtf16(utf16Offset + charCount);
        if (end <= start)
            return;
        Mutate(() => DeleteRaw(start, end - start));
    }

    public void ReplaceUtf16(int utf16Offset, int charCount, string text)
    {
        text ??= string.Empty;
        if (charCount <= 0 && text.Length == 0)
            return;
        var start = ByteOffsetFromUtf16(utf16Offset);
        var end = ByteOffsetFromUtf16(utf16Offset + Math.Max(0, charCount));
        Mutate(() =>
        {
            if (end > start)
                DeleteRaw(start, end - start);
            if (text.Length > 0)
            {
                InsertRaw(start, text);
                NoteInsertedEndings(text);
            }
        });
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        var current = Capture();
        Restore(_undo.Pop());
        _redo.Push(current);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        var current = Capture();
        Restore(_redo.Pop());
        _undo.Push(current);
        return true;
    }

    public void ConvertLineEndings(LineEndingKind target)
    {
        if (ByteLength > MaterializeLimit)
            throw new NotSupportedException("Converting line endings in a document this large is not supported in M1.");
        if (!LineEndings.IsMixed && LineEndings.Preferred == target && !ContainsForeignEnding(GetText(), target))
        {
            LineEndings = LineEndingProfile.Single(target);
            return;
        }

        var converted = NormalizeEndings(GetText(), target);
        Mutate(() =>
        {
            ReplaceAll(converted);
            LineEndings = LineEndingProfile.Single(target);
        });
    }

    public IReadOnlyList<SearchMatch> Find(string pattern, FindOptions options)
    {
        if (!_settings.Features.FindReplace)
            return [];
        if (string.IsNullOrEmpty(pattern))
            return [];
        return TextSearcherFactory.Create(options).Find(GetText(), pattern, options);
    }

    public int ReplaceAll(string pattern, string replacement, FindOptions options)
    {
        if (!_settings.Features.FindReplace)
            return 0;
        if (string.IsNullOrEmpty(pattern))
            return 0;
        var current = GetText();
        var updated = TextSearcherFactory.Create(options).Replace(current, pattern, replacement ?? string.Empty, options, out var count);
        if (count == 0)
            return 0;
        Mutate(() =>
        {
            ReplaceAll(updated);
            RefreshEndings(ByteLength <= 256 * 1024);
        });
        return count;
    }

    public bool TryGoToLine(int lineNumber, out int zeroBasedLine)
    {
        zeroBasedLine = 0;
        if (!_settings.Features.GoToLine)
            return false;
        if (lineNumber < 1)
            lineNumber = 1;
        EnsureLines(lineNumber);
        var count = Math.Max(CompleteLineCount, 1);
        zeroBasedLine = Math.Min(lineNumber, count) - 1;
        return true;
    }

    public DocumentStatus? TryGetStatus(int line, int utf16Column)
    {
        if (!_settings.Features.StatusBar)
            return null;
        if (line < 0)
            line = 0;
        EnsureLines(line + 1);
        var last = Math.Max(0, CompleteLineCount - 1);
        if (line > last)
            line = last;
        var content = line < CompleteLineCount ? GetLineContent(line) : string.Empty;
        utf16Column = Math.Clamp(utf16Column, 0, content.Length);
        return new DocumentStatus(
            line + 1,
            DisplayColumn.Get(content, utf16Column) + 1,
            Encoding.Label,
            LineEndings.Label,
            IsModified);
    }

    public FirstPage LayoutFirstPage(int maxLines, int maxColumns)
    {
        if (maxLines < 1)
            maxLines = 1;
        if (maxColumns < 1)
            maxColumns = 1;
        var window = Math.Min(ByteLength, (long)maxLines * (maxColumns * 4L + 2L) + 64L);
        _index.Ensure(maxLines, ByteLength, window, FindBreak);

        var available = _index.CompleteLineCount(ByteLength);
        if (available == 0)
        {
            var sample = Math.Min(ByteLength, maxColumns * 4L);
            var text = GraphemeNavigator.Trim(TextDecoder.Decode(_table, _source, _add, 0, sample, Encoding.AnsiCodePage), maxColumns);
            return new FirstPage
            {
                Lines = [new PageLine { LineNumber = 0, Text = text, DisplayColumns = DisplayColumn.Count(text) }],
                BytesScanned = sample,
                IndexComplete = false
            };
        }

        var lines = new List<PageLine>(Math.Min(maxLines, available));
        var count = Math.Min(maxLines, available);
        for (var i = 0; i < count; i++)
        {
            var text = GraphemeNavigator.Trim(GetLineContent(i), maxColumns);
            lines.Add(new PageLine { LineNumber = i, Text = text, DisplayColumns = DisplayColumn.Count(text) });
        }

        return new FirstPage
        {
            Lines = lines,
            BytesScanned = _index.ScannedTo,
            IndexComplete = IsLineIndexComplete
        };
    }

    public void Save(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var same = FilePath is not null && PathsEqual(FilePath, full);
        if (!IsModified && same)
            return;

        var atomic = _settings.Features.AtomicSave;
        if (_revision == 0 && _source.Length >= 0 && FilePath is not null)
            CopyOriginal(full, atomic);
        else if (_revision == 0 && _source is OwnedByteSource && FilePath is null && _undo.Count == 0)
            CopyOriginal(full, atomic);
        else if (_source is MappedByteSource && same)
            SaveMappedInPlace(full);
        else
            AtomicFileWriter.Write(full, atomic, stream => TextCodec.Write(stream, _table, _source, _add, Encoding));

        if (_source is MappedByteSource && same && _revision != 0)
            return;

        FilePath = full;
        _savedRevision = _revision;
    }

    private void LoadOriginal(byte[] prefix)
    {
        var entire = _source.Length <= prefix.Length;
        Encoding = EncodingDetector.Detect(prefix, entire, _settings.Features.EncodingDetection, _settings.AnsiCodePage);
        var bom = Math.Min(Encoding.BomLength, _source.Length);
        var length = _source.Length - bom;
        if (length > 0)
            _table.Insert(0, new Piece(PieceSource.Original, bom, length, Encoding.PieceEncoding));
        RefreshEndings(ByteLength <= 256 * 1024);
    }

    private static byte[] ReadPrefix(IByteSource source)
    {
        var length = (int)Math.Min(source.Length, 64 * 1024);
        var prefix = new byte[length];
        if (length > 0)
            source.CopyTo(0, prefix);
        return prefix;
    }

    private void Mutate(Action change)
    {
        var entry = Capture();
        _redo.Clear();
        // Drop bytes that belonged only to the redo branch, then append the new edit.
        _add.Truncate(_logicalAdd);
        change();
        _undo.Push(entry);
        _revision++;
    }

    private void InsertRaw(long offset, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var start = _add.Append(bytes);
        _logicalAdd = _add.Length;
        _index.NoteInsert(offset);
        _table.Insert(offset, new Piece(PieceSource.Add, start, bytes.Length, BufferEncoding.Utf8));
    }

    private void DeleteRaw(long offset, long length)
    {
        _index.NoteDelete(offset);
        _table.Delete(offset, length);
        if (ByteLength <= 256 * 1024)
            RefreshEndings(full: true);
    }

    private void ReplaceAll(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var start = _add.Append(bytes);
        _logicalAdd = _add.Length;
        _table.Clear();
        if (bytes.Length > 0)
            _table.Insert(0, new Piece(PieceSource.Add, start, bytes.Length, BufferEncoding.Utf8));
        _index.Reset();
    }

    private HistoryEntry Capture() => new()
    {
        Pieces = _table.ToArray(),
        AddLength = _logicalAdd,
        Revision = _revision,
        Endings = LineEndings
    };

    private void Restore(HistoryEntry entry)
    {
        _table.Load(entry.Pieces);
        // Keep the add-buffer bytes so redo can see edits that were undone.
        // A later new edit truncates back to the logical length.
        _logicalAdd = entry.AddLength;
        _revision = entry.Revision;
        LineEndings = entry.Endings;
        _index.Reset();
    }

    private void NoteInsertedEndings(string text)
    {
        if (ByteLength <= 256 * 1024)
        {
            RefreshEndings(full: true);
            return;
        }

        if (LineScanner.HasMultipleKinds(text))
        {
            LineEndings = LineEndings with { IsMixed = true, IsProvisional = true };
            return;
        }

        var kind = LineScanner.Classify(text);
        if (kind is null || LineEndings.IsMixed || kind == LineEndings.Preferred)
            return;
        LineEndings = LineEndings with { IsMixed = true, IsProvisional = true };
    }

    private void RefreshEndings(bool full)
    {
        var limit = full ? ByteLength : Math.Min(ByteLength, 64 * 1024);
        var detected = LineScanner.Detect(_table, _source, _add, limit, _settings.DefaultLineEnding);
        if (!full && limit < ByteLength)
            detected = detected with { IsProvisional = true };
        LineEndings = detected;
    }

    private bool FindBreak(long from, long end, out LineBreak found) =>
        LineScanner.TryFindNext(_table, _source, _add, from, end, out found);

    private int BreakBytes(int line, long boundary)
    {
        if (line + 1 >= _index.StartCount)
            return 0;
        var start = _index.LineStart(line);
        var look = (int)Math.Min(4, boundary - start);
        if (look <= 0)
            return 0;
        Span<byte> tail = stackalloc byte[4];
        _table.CopyTo(boundary - look, tail[..look], _source, _add);
        if (!_table.TryGetSpan(boundary - 1, out var span))
            return 0;
        return ClassifyTail(tail[..look], span.Piece.Encoding);
    }

    private LineEndingKind ClassifyBreak(long start, int bytes)
    {
        Span<byte> buffer = stackalloc byte[4];
        _table.CopyTo(start, buffer[..bytes], _source, _add);
        if (!_table.TryGetSpan(start, out var span))
            return LineEndingKind.Lf;
        return ClassifyTail(buffer[..bytes], span.Piece.Encoding) switch
        {
            4 => LineEndingKind.Crlf,
            2 when span.Piece.Encoding is BufferEncoding.Utf16Le or BufferEncoding.Utf16Be => UnitKind(buffer[..bytes], span.Piece.Encoding),
            2 => LineEndingKind.Crlf,
            1 when buffer[0] == 0x0D => LineEndingKind.Cr,
            _ => LineEndingKind.Lf
        };
    }

    private static int ClassifyTail(ReadOnlySpan<byte> tail, BufferEncoding encoding)
    {
        if (encoding is BufferEncoding.Utf16Le or BufferEncoding.Utf16Be)
        {
            var little = encoding == BufferEncoding.Utf16Le;
            if (tail.Length >= 4
                && Unit(tail[^4], tail[^3], little) == '\r'
                && Unit(tail[^2], tail[^1], little) == '\n')
                return 4;
            if (tail.Length >= 2)
            {
                var unit = Unit(tail[^2], tail[^1], little);
                if (unit is '\n' or '\r')
                    return 2;
            }
            return 0;
        }

        if (tail.Length >= 2 && tail[^2] == 0x0D && tail[^1] == 0x0A)
            return 2;
        if (tail.Length >= 1 && tail[^1] is 0x0A or 0x0D)
            return 1;
        return 0;
    }

    private static LineEndingKind UnitKind(ReadOnlySpan<byte> bytes, BufferEncoding encoding)
    {
        var little = encoding == BufferEncoding.Utf16Le;
        var unit = Unit(bytes[0], bytes[1], little);
        return unit == '\r' ? LineEndingKind.Cr : LineEndingKind.Lf;
    }

    private static int Unit(byte a, byte b, bool little) => little ? a | (b << 8) : (a << 8) | b;

    private static int EndingChars(LineEndingKind kind) => kind switch
    {
        LineEndingKind.Crlf => 2,
        LineEndingKind.Lf => 1,
        LineEndingKind.Cr => 1,
        _ => 0
    };

    private long SnapBack(long offset)
    {
        if (offset <= 0)
            return 0;
        if (offset >= ByteLength)
            return ByteLength;
        if (!_table.TryGetSpan(offset, out var span))
            return offset;
        var into = span.SourceOffset - span.Piece.Start;
        if (into == 0)
            return offset;

        if (span.Piece.Encoding is BufferEncoding.Utf16Le or BufferEncoding.Utf16Be)
            return (into & 1) == 1 ? offset - 1 : offset;
        if (span.Piece.Encoding == BufferEncoding.Ansi)
            return offset;

        Span<byte> one = stackalloc byte[1];
        _table.CopyTo(offset, one, _source, _add);
        if ((one[0] & 0xC0) != 0x80)
            return offset;
        var pieceDoc = offset - into;
        for (var back = 1; back <= 3 && offset - back >= pieceDoc; back++)
        {
            _table.CopyTo(offset - back, one, _source, _add);
            if ((one[0] & 0xC0) != 0x80)
                return offset - back;
        }
        return pieceDoc;
    }

    private long SnapForward(long offset)
    {
        var back = SnapBack(offset);
        if (back == offset || back >= ByteLength)
            return Math.Clamp(offset, 0, ByteLength);
        var unit = TextDecoder.Read(_table, _source, _add, back, Encoding.AnsiCodePage);
        return back + Math.Max(1, unit.Bytes);
    }

    private void CopyOriginal(string path, bool atomic)
    {
        AtomicFileWriter.Write(path, atomic, stream =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long position = 0;
                while (position < _source.Length)
                {
                    var count = (int)Math.Min(buffer.Length, _source.Length - position);
                    _source.CopyTo(position, buffer.AsSpan(0, count));
                    stream.Write(buffer, 0, count);
                    position += count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });
    }

    private void SaveMappedInPlace(string full)
    {
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            throw new IOException($"Cannot save. The file is read-only: {full}");

        var directory = Path.GetDirectoryName(full) ?? ".";
        var temp = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("n") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                TextCodec.Write(stream, _table, _source, _add, Encoding);
                stream.Flush(flushToDisk: true);
            }

            DisposeSource();
            File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch
        {
            TryDelete(temp);
            if (_source is EmptyByteSource)
                _source = MappedByteSource.Open(full);
            throw;
        }

        ReopenClean(full);
    }

    private void ReopenClean(string full)
    {
        _undo.Clear();
        _redo.Clear();
        DisposeSource();
        _source = MappedByteSource.Open(full);
        _add.Truncate(0);
        _table.Clear();
        _index.Reset();
        LoadOriginal(ReadPrefix(_source));
        _revision = 0;
        _savedRevision = 0;
        FilePath = full;
    }

    private void DisposeSource()
    {
        var source = _source;
        _source = EmptyByteSource.Instance;
        if (!ReferenceEquals(source, EmptyByteSource.Instance))
            source.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static string NormalizeEndings(string text, LineEndingKind target)
    {
        var newline = target switch
        {
            LineEndingKind.Lf => "\n",
            LineEndingKind.Cr => "\r",
            _ => "\r\n"
        };
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                builder.Append(newline);
            }
            else if (text[i] == '\n')
            {
                builder.Append(newline);
            }
            else
            {
                builder.Append(text[i]);
            }
        }
        return builder.ToString();
    }

    private static bool ContainsForeignEnding(string text, LineEndingKind target)
    {
        var kind = LineScanner.Classify(text);
        return kind is not null && (kind != target || LineScanner.HasMultipleKinds(text));
    }

    private struct HistoryEntry
    {
        public Piece[] Pieces;
        public long AddLength;
        public int Revision;
        public LineEndingProfile Endings;
    }
}
