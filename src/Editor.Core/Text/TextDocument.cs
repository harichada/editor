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
public sealed partial class TextDocument : IDisposable
{
    public const long MapThreshold = 1_048_576;
    public const long MaterializeLimit = 64L * 1024 * 1024;
    internal const int DefaultSearchWindowBytes = 1024 * 1024;
    private const long ExactJumpLimit = 256 * 1024;

    private readonly object _gate = new();
    private readonly EditorSettings _settings;
    private readonly PieceTable _table = new();
    private readonly AddBuffer _add = new();
    private readonly LineIndex _index = new();
    private readonly Stack<HistoryEntry> _undo = new();
    private readonly Stack<HistoryEntry> _redo = new();

    private IByteSource _source;
    private string? _sourcePath;
    private string? _backingPath;
    private int _revision;
    private int _savedRevision;
    private long _logicalAdd;
    private bool _disposed;
    private bool _transcodeAll;
    private bool _coalesce;

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
    public bool IsModified => _revision != _savedRevision || _transcodeAll;

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
        if (source is MappedByteSource)
            document._sourcePath = document.FilePath;
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
        string? backing;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            backing = _backingPath;
            _backingPath = null;
            _sourcePath = null;
            _source.Dispose();
            _source = EmptyByteSource.Instance;
        }
        if (backing is not null)
            TryDelete(backing);
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
        lock (_gate)
        {
            if (_undo.Count == 0)
                return false;
            var current = Capture();
            Restore(_undo.Pop());
            _redo.Push(current);
            return true;
        }
    }

    public bool Redo()
    {
        lock (_gate)
        {
            if (_redo.Count == 0)
                return false;
            var current = Capture();
            Restore(_redo.Pop());
            _undo.Push(current);
            return true;
        }
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

    public IReadOnlyList<SearchMatch> Find(string pattern, FindOptions options, CancellationToken cancellationToken = default) =>
        Find(pattern, options, cancellationToken, DefaultSearchWindowBytes);

    /// <summary>
    /// The next non-overlapping match at or after <paramref name="fromUtf16"/>.
    /// If none remains, the first match in the document, so Find Next wraps.
    /// The scan covers the whole document. A match that crosses a decode window is kept.
    /// </summary>
    public SearchMatch? FindNext(string pattern, FindOptions options, int fromUtf16, CancellationToken cancellationToken = default) =>
        FindNext(pattern, options, fromUtf16, cancellationToken, DefaultSearchWindowBytes);

    internal IReadOnlyList<SearchMatch> Find(string pattern, FindOptions options, CancellationToken cancellationToken, int windowChars)
    {
        if (!CanSearch(pattern, options))
            return [];
        var (expected, length) = CaptureSearch();
        return StreamingTextSearch.FindAll(
            (position, count) => ReadSearchWindow(position, count, expected),
            length,
            pattern,
            Comparison(options),
            windowChars,
            cancellationToken);
    }

    internal SearchMatch? FindNext(string pattern, FindOptions options, int fromUtf16, CancellationToken cancellationToken, int windowChars)
    {
        if (!CanSearch(pattern, options))
            return null;
        if (fromUtf16 < 0)
            fromUtf16 = 0;
        var (expected, length) = CaptureSearch();
        return StreamingTextSearch.FindNext(
            (position, count) => ReadSearchWindow(position, count, expected),
            length,
            pattern,
            Comparison(options),
            windowChars,
            fromUtf16,
            cancellationToken);
    }

    public int ReplaceAll(string pattern, string replacement, FindOptions options, CancellationToken cancellationToken = default) =>
        ReplaceAll(pattern, replacement, options, cancellationToken, DefaultSearchWindowBytes);

    internal int ReplaceAll(string pattern, string replacement, FindOptions options, CancellationToken cancellationToken, int windowChars)
    {
        if (!CanSearch(pattern, options))
            return 0;
        replacement ??= string.Empty;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var temp = Path.Combine(Path.GetTempPath(), "editor-replace-" + Guid.NewGuid().ToString("n"));
            int count;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                {
                    count = StreamingTextSearch.Replace(
                        (position, chars) =>
                        {
                            var window = DecodeByteWindow(position, chars);
                            return new StreamingTextSearch.DecodedWindow(window.Text, window.BytesConsumed);
                        },
                        _table.Length,
                        pattern,
                        replacement,
                        Comparison(options),
                        windowChars,
                        stream,
                        cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                if (count == 0)
                    return 0;

                MutateUnlocked(() =>
                {
                    AdoptReplacement(temp);
                    RefreshEndings(_table.Length <= 256 * 1024);
                });
                return count;
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    private bool CanSearch(string pattern, FindOptions options)
    {
        if (!_settings.Features.FindReplace || string.IsNullOrEmpty(pattern))
            return false;
        if (options.Mode == SearchMode.Regex)
            throw new NotSupportedException("Regular expression search is not available until M2.");
        return true;
    }

    private static StringComparison Comparison(FindOptions options) =>
        options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private (int Revision, long Length) CaptureSearch()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (_revision, _table.Length);
        }
    }

    private StreamingTextSearch.DecodedWindow ReadSearchWindow(long position, int maxChars, int expectedRevision)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_revision != expectedRevision)
                throw new OperationCanceledException("The document changed while it was being searched.");
            var window = DecodeByteWindow(position, maxChars);
            return new StreamingTextSearch.DecodedWindow(window.Text, window.BytesConsumed);
        }
    }

    /// <summary>
    /// Decode up to <paramref name="maxBytes"/> document bytes, stopping on a character boundary.
    /// A requested window that ends inside a multibyte character does not consume that character;
    /// the next window starts at its first byte, so the character is reported once.
    /// </summary>
    private (string Text, int BytesConsumed) DecodeByteWindow(long bytePos, int maxBytes)
    {
        if (maxBytes < 1)
            maxBytes = 1;
        if (bytePos >= _table.Length)
            return (string.Empty, 0);

        var builder = new StringBuilder();
        var consumed = 0;
        var position = bytePos;
        var limit = Math.Min(_table.Length, bytePos + maxBytes);
        while (position < limit)
        {
            if (!_table.TryGetSpan(position, out var span) || span.Length <= 0)
                break;
            var room = (int)Math.Min(span.Length, limit - position);
            if (room <= 0)
                break;
            var atEnd = position + room >= _table.Length;
            var take = TakeComplete(position, span, room, atEnd);
            if (take <= 0)
            {
                if (consumed > 0)
                    break;
                var wider = (int)Math.Min(span.Length, 8);
                take = TakeComplete(position, span, wider, position + wider >= _table.Length);
                if (take <= 0)
                    take = 1;
            }

            AppendDecoded(builder, position, span, take);
            consumed += take;
            position += take;
            if (position > limit && consumed > take)
                break;
            if (take < room)
                break;
        }

        return (builder.ToString(), consumed);
    }

    private int TakeComplete(long position, PieceSpan span, int room, bool atEnd)
    {
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(room);
        try
        {
            var buffer = rented.AsSpan(0, room);
            CopySpan(span, buffer);
            return span.Piece.Encoding switch
            {
                BufferEncoding.Utf16Le => CompleteUtf16(buffer, atEnd, littleEndian: true),
                BufferEncoding.Utf16Be => CompleteUtf16(buffer, atEnd, littleEndian: false),
                BufferEncoding.Ansi => room,
                _ => CompleteUtf8(buffer, atEnd)
            };
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void AppendDecoded(StringBuilder builder, long position, PieceSpan span, int take)
    {
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(take);
        try
        {
            var buffer = rented.AsSpan(0, take);
            CopySpan(span, buffer);
            builder.Append(span.Piece.Encoding switch
            {
                BufferEncoding.Utf16Le => System.Text.Encoding.Unicode.GetString(buffer),
                BufferEncoding.Utf16Be => System.Text.Encoding.BigEndianUnicode.GetString(buffer),
                BufferEncoding.Ansi => AnsiEncodings.Get(Encoding.AnsiCodePage).GetString(buffer),
                _ => System.Text.Encoding.UTF8.GetString(buffer)
            });
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }

        _ = position;
    }

    private void CopySpan(PieceSpan span, Span<byte> destination)
    {
        if (span.Piece.Source == PieceSource.Original)
            _source.CopyTo(span.SourceOffset, destination);
        else
            _add.CopyTo(span.SourceOffset, destination);
    }

    private static int CompleteUtf8(ReadOnlySpan<byte> data, bool atEnd)
    {
        var index = 0;
        var good = 0;
        while (index < data.Length)
        {
            var lead = data[index];
            var size = 1;
            if (lead >= 0xC2 && lead <= 0xDF)
                size = 2;
            else if (lead >= 0xE0 && lead <= 0xEF)
                size = 3;
            else if (lead >= 0xF0 && lead <= 0xF4)
                size = 4;
            if (index + size > data.Length)
                return atEnd ? data.Length : good;
            good = index + size;
            index += size;
        }
        return good;
    }

    private static int CompleteUtf16(ReadOnlySpan<byte> data, bool atEnd, bool littleEndian)
    {
        if (data.Length < 2)
            return atEnd ? data.Length : 0;
        var even = data.Length & ~1;
        if (!atEnd && even >= 2)
        {
            var unit = littleEndian
                ? (char)(data[even - 2] | (data[even - 1] << 8))
                : (char)((data[even - 2] << 8) | data[even - 1]);
            if (char.IsHighSurrogate(unit))
                even -= 2;
        }
        return even;
    }

    private void AdoptReplacement(string tempPath)
    {
        long start = _add.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                _add.Append(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        _logicalAdd = _add.Length;
        _table.Clear();
        var length = _add.Length - start;
        if (length > 0)
            _table.Insert(0, new Piece(PieceSource.Add, start, length, BufferEncoding.Utf8));
        _index.Reset();
    }

    public bool TryGoToLine(int lineNumber, out int zeroBasedLine)
    {
        zeroBasedLine = 0;
        if (!_settings.Features.GoToLine)
            return false;
        if (lineNumber < 1)
            lineNumber = 1;
        LastJump = JumpToLine(lineNumber - 1);
        zeroBasedLine = LastJump.Line;
        return true;
    }

    public DocumentStatus? TryGetStatus(int line, int utf16Column)
    {
        if (!_settings.Features.StatusBar)
            return null;
        if (line < 0)
            line = 0;
        if (line > CompleteLineCount + 8 && ByteLength > ExactJumpLimit)
        {
            return new DocumentStatus(
                line + 1,
                utf16Column + 1,
                Encoding.Label,
                LineEndings.Label,
                IsModified);
        }
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
            var text = ReadLinePrefix(i, maxColumns);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var full = Path.GetFullPath(path);
            var same = FilePath is not null && PathsEqual(FilePath, full);
            if (!IsModified && same)
                return;

            var atomic = _settings.Features.AtomicSave;
            if (_revision == 0 && !_transcodeAll && _source.Length >= 0 && FilePath is not null)
                CopyOriginal(full, atomic);
            else if (_revision == 0 && !_transcodeAll && _source is OwnedByteSource && FilePath is null && _undo.Count == 0)
                CopyOriginal(full, atomic);
            else if (IsMapping(full))
                SaveOverMappedFile(full, atomic);
            else
                AtomicFileWriter.Write(full, atomic, WriteDocument);

            FilePath = full;
            _savedRevision = _revision;
            _transcodeAll = false;
        }
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
        lock (_gate)
        {
            _coalesce = false;
            MutateUnlocked(change);
        }
    }

    private void MutateUnlocked(Action change)
    {
        var entry = Capture();
        _redo.Clear();
        // Drop bytes that belonged only to the redo branch, then append the new edit.
        _add.Truncate(_logicalAdd);
        try
        {
            change();
        }
        catch
        {
            Restore(entry);
            throw;
        }
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

    private bool IsMapping(string full) =>
        _source is MappedByteSource && _sourcePath is not null && PathsEqual(_sourcePath, full);

    /// <summary>
    /// The destination is the file currently mapped as the original byte source.
    /// Piece offsets in the live table and in the undo stack name bytes in that
    /// file, so those bytes are copied to a private backing file before the
    /// destination is replaced. Undo keeps working, and the replace stays atomic
    /// when that flag is on.
    /// </summary>
    private void SaveOverMappedFile(string full, bool atomic)
    {
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            throw new IOException($"Cannot save. The file is read-only: {full}");

        var directory = Path.GetDirectoryName(full) ?? ".";
        var temp = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("n") + ".tmp");
        string? backing = null;
        var unmapped = false;
        var replaced = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                WriteDocument(stream);
                stream.Flush(flushToDisk: true);
            }

            if (HistoryReferencesOriginal())
                backing = CopySourceToBackingFile();

            DisposeSource();
            unmapped = true;

            if (!atomic)
            {
                File.Copy(temp, full, overwrite: true);
                TryDelete(temp);
            }
            else if (File.Exists(full))
            {
                File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, full);
            }

            replaced = true;
            AdoptBacking(backing);
            backing = null;
        }
        catch
        {
            TryDelete(temp);
            if (!replaced && unmapped && _source is EmptyByteSource)
                RemapAfterFailedSave(full, ref backing);
            if (!replaced && backing is not null)
                TryDelete(backing);
            else if (replaced && backing is not null)
                _backingPath = backing;
            throw;
        }
    }

    private void AdoptBacking(string? backing)
    {
        if (backing is null)
        {
            _sourcePath = null;
            return;
        }
        _source = MappedByteSource.Open(backing);
        _sourcePath = backing;
        _backingPath = backing;
    }

    private void RemapAfterFailedSave(string full, ref string? backing)
    {
        try
        {
            _source = MappedByteSource.Open(full);
            _sourcePath = full;
            return;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (backing is null)
            return;
        try
        {
            _source = MappedByteSource.Open(backing);
            _sourcePath = backing;
            _backingPath = backing;
            backing = null;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool HistoryReferencesOriginal()
    {
        if (PiecesReferenceOriginal(_table.ToArray()))
            return true;
        foreach (var entry in _undo)
        {
            if (PiecesReferenceOriginal(entry.Pieces))
                return true;
        }
        foreach (var entry in _redo)
        {
            if (PiecesReferenceOriginal(entry.Pieces))
                return true;
        }
        return false;
    }

    private static bool PiecesReferenceOriginal(Piece[] pieces)
    {
        foreach (var piece in pieces)
        {
            if (piece.Source == PieceSource.Original && piece.Length > 0)
                return true;
        }
        return false;
    }

    private string CopySourceToBackingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-backing-" + Guid.NewGuid().ToString("n"));
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
            long position = 0;
            while (position < _source.Length)
            {
                var count = (int)Math.Min(buffer.Length, _source.Length - position);
                _source.CopyTo(position, buffer.AsSpan(0, count));
                stream.Write(buffer, 0, count);
                position += count;
            }
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return path;
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
