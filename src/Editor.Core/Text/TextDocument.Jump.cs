using System.Buffers;
using System.Text;

namespace Editor.Core;

/// <summary>
/// Where a large-file jump landed. <see cref="Exact"/> is false when the line number
/// is estimated from the average line length instead of a scan from the start of the file.
/// </summary>
public readonly record struct JumpTarget(
    long LineStart,
    long CaretByte,
    int Line,
    int Column,
    bool Exact,
    long BytesTouched,
    string Preview);

public sealed partial class TextDocument
{
    private const int JumpWindow = 8192;

    public JumpTarget LastJump { get; private set; }

    public void SetEncoding(TextEncodingKind kind, bool bom)
    {
        var profile = kind switch
        {
            TextEncodingKind.Utf16Le => TextEncodingProfile.Utf16Le(Encoding.AnsiCodePage),
            TextEncodingKind.Utf16Be => TextEncodingProfile.Utf16Be(Encoding.AnsiCodePage),
            TextEncodingKind.Ansi => TextEncodingProfile.Ansi(Encoding.AnsiCodePage),
            _ => TextEncodingProfile.Utf8(Encoding.AnsiCodePage, bom)
        };
        if (profile.Kind == Encoding.Kind && profile.HasBom == Encoding.HasBom)
            return;
        Encoding = profile with { IsProvisional = false };
        _transcodeAll = true;
    }

    public JumpTarget JumpToEnd()
    {
        if (ByteLength <= ExactJumpLimit)
            return ExactEnd();
        return JumpNear(ByteLength, preferEnd: true);
    }

    public JumpTarget JumpToLine(int line)
    {
        if (line < 0)
            line = 0;
        if (ByteLength <= ExactJumpLimit || line <= CompleteLineCount + 32)
        {
            EnsureLines(line + 1);
            if (line < CompleteLineCount)
            {
                var content = ReadLinePrefix(line, 4096);
                return new JumpTarget(_index.LineStart(line), _index.LineStart(line), line, 0, true, _index.ScannedTo, content);
            }
            if (IsLineIndexComplete)
                return ExactEnd();
        }

        var average = AverageLineBytes();
        var guess = Math.Min(ByteLength, (long)line * average);
        var jumped = JumpNear(guess, preferEnd: false);
        return jumped with { Line = line, Column = 0, Exact = false, CaretByte = jumped.LineStart };
    }

    public JumpTarget JumpToFraction(double fraction)
    {
        if (double.IsNaN(fraction) || double.IsInfinity(fraction))
            fraction = 0;
        fraction = Math.Clamp(fraction, 0, 1);
        if (ByteLength <= ExactJumpLimit)
        {
            EnsureLines(int.MaxValue);
            var last = Math.Max(0, CompleteLineCount - 1);
            var line = (int)Math.Round(fraction * last);
            line = Math.Clamp(line, 0, last);
            var content = ReadLinePrefix(line, 4096);
            return new JumpTarget(_index.LineStart(line), _index.LineStart(line), line, 0, true, _index.ScannedTo, content);
        }

        var guess = (long)(fraction * ByteLength);
        return JumpNear(fraction >= 0.999 ? ByteLength : guess, preferEnd: fraction >= 0.999);
    }

    public FirstPage LayoutAround(long byteOffset, int maxLines, int maxColumns)
    {
        if (maxLines < 1)
            maxLines = 1;
        if (maxColumns < 1)
            maxColumns = 1;
        if (byteOffset < 0)
            byteOffset = 0;
        if (byteOffset > ByteLength)
            byteOffset = ByteLength;

        var origin = JumpNear(byteOffset, preferEnd: false);
        var lines = new List<PageLine>(maxLines);
        var position = origin.LineStart;
        long touched = origin.BytesTouched;
        for (var row = 0; row < maxLines && position <= ByteLength; row++)
        {
            var budget = Math.Min(ByteLength, position + (maxColumns * 4L) + 8L);
            var sample = TextDecoder.Decode(_table, _source, _add, position, Math.Max(0, budget - position), Encoding.AnsiCodePage);
            var cut = sample.IndexOfAny(['\r', '\n']);
            var body = GraphemeNavigator.Trim(cut < 0 ? sample : sample[..cut], maxColumns);
            lines.Add(new PageLine
            {
                LineNumber = origin.Line + row,
                Text = body,
                DisplayColumns = DisplayColumn.Count(body)
            });
            touched += Math.Max(0, budget - position);
            if (cut < 0 || !FindBreak(position, ByteLength, out var lineBreak) || lineBreak.Start < position)
                break;
            var next = lineBreak.Start + lineBreak.ByteLength;
            if (next <= position)
                break;
            position = next;
        }

        if (lines.Count == 0)
        {
            lines.Add(new PageLine { LineNumber = origin.Line, Text = string.Empty, DisplayColumns = 0 });
        }

        return new FirstPage
        {
            Lines = lines,
            BytesScanned = touched,
            IndexComplete = false
        };
    }

    internal void CoalescedReplaceUtf16(int utf16Offset, int charCount, string text)
    {
        text ??= string.Empty;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_coalesce && _undo.Count > 0)
            {
                var before = _undo.Pop();
                Restore(before);
            }

            if (charCount <= 0 && text.Length == 0)
            {
                _coalesce = true;
                return;
            }

            var start = ByteOffsetFromUtf16(utf16Offset);
            var end = ByteOffsetFromUtf16(utf16Offset + Math.Max(0, charCount));
            MutateUnlocked(() =>
            {
                if (end > start)
                    DeleteRaw(start, end - start);
                if (text.Length > 0)
                {
                    InsertRaw(start, text);
                    NoteInsertedEndings(text);
                }
            });
            _coalesce = true;
        }
    }

    internal void EndCoalescedEdit() => _coalesce = false;

    internal void AbortCoalescedEdit()
    {
        lock (_gate)
        {
            if (!_coalesce || _undo.Count == 0)
            {
                _coalesce = false;
                return;
            }

            var before = _undo.Pop();
            Restore(before);
            _coalesce = false;
        }
    }

    private void WriteDocument(Stream stream)
    {
        ConfirmEncoding();
        if (_transcodeAll)
        {
            TextCodec.Write(stream, _table, _source, _add, Encoding);
            return;
        }

        var preamble = Encoding.Preamble;
        if (!preamble.IsEmpty)
            stream.Write(preamble);

        long offset = 0;
        foreach (var piece in _table.ToArray())
        {
            if (piece.Length == 0)
            {
                continue;
            }

            if (piece.Source == PieceSource.Original || piece.Encoding == Encoding.PieceEncoding)
                CopyPiece(piece, stream);
            else
                TranscodeRange(offset, piece.Length, stream);
            offset += piece.Length;
        }
    }

    private void ConfirmEncoding()
    {
        if (!Encoding.IsProvisional)
            return;

        var utf8 = SourceIsStrictUtf8();
        var confirmed = utf8
            ? Encoding with { IsProvisional = false }
            : TextEncodingProfile.Ansi(Encoding.AnsiCodePage);
        if (confirmed.PieceEncoding != Encoding.PieceEncoding)
        {
            _table.RetagOriginal(confirmed.PieceEncoding);
            RetagHistory(confirmed.PieceEncoding);
        }

        Encoding = confirmed with { IsProvisional = false };
    }

    private bool SourceIsStrictUtf8()
    {
        var start = (long)Encoding.BomLength;
        if (start > _source.Length)
            start = _source.Length;
        var rented = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        var carry = new byte[3];
        var carryLength = 0;
        try
        {
            var position = start;
            while (position < _source.Length)
            {
                var count = (int)Math.Min(rented.Length - carryLength, _source.Length - position);
                if (carryLength > 0)
                    carry.AsSpan(0, carryLength).CopyTo(rented);
                _source.CopyTo(position, rented.AsSpan(carryLength, count));
                var total = carryLength + count;
                var atEnd = position + count >= _source.Length;
                var complete = CompleteUtf8(rented.AsSpan(0, total), atEnd);
                var check = complete == 0 ? total : complete;
                if (!Utf8Validator.IsValid(rented.AsSpan(0, check)))
                    return false;
                if (!atEnd && complete < total)
                {
                    var rest = total - complete;
                    if (rest > carry.Length)
                        return false;
                    rented.AsSpan(complete, rest).CopyTo(carry);
                    carryLength = rest;
                }
                else
                {
                    carryLength = 0;
                }

                position += count;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void RetagHistory(BufferEncoding encoding)
    {
        Retag(_undo, encoding);
        Retag(_redo, encoding);
    }

    private static void Retag(Stack<HistoryEntry> stack, BufferEncoding encoding)
    {
        foreach (var entry in stack)
        {
            var pieces = entry.Pieces;
            for (var i = 0; i < pieces.Length; i++)
            {
                if (pieces[i].Source == PieceSource.Original)
                    pieces[i] = pieces[i] with { Encoding = encoding };
            }
        }
    }

    private void CopyPiece(Piece piece, Stream stream)
    {
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long done = 0;
            while (done < piece.Length)
            {
                var count = (int)Math.Min(rented.Length, piece.Length - done);
                if (piece.Source == PieceSource.Original)
                    _source.CopyTo(piece.Start + done, rented.AsSpan(0, count));
                else
                    _add.CopyTo(piece.Start + done, rented.AsSpan(0, count));
                stream.Write(rented, 0, count);
                done += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void TranscodeRange(long offset, long length, Stream stream)
    {
        var encoder = TextCodec.Create(Encoding).GetEncoder();
        var raw = ArrayPool<byte>.Shared.Rent(8192);
        var encoded = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            var position = offset;
            var end = offset + length;
            while (position < end)
            {
                var count = (int)Math.Min(raw.Length, end - position);
                _table.CopyTo(position, raw.AsSpan(0, count), _source, _add);
                var atEnd = position + count >= end;
                var kind = EncodingAt(position);
                var complete = kind switch
                {
                    BufferEncoding.Utf16Le => CompleteUtf16(raw.AsSpan(0, count), atEnd, true),
                    BufferEncoding.Utf16Be => CompleteUtf16(raw.AsSpan(0, count), atEnd, false),
                    BufferEncoding.Ansi => count,
                    _ => CompleteUtf8(raw.AsSpan(0, count), atEnd)
                };
                if (complete <= 0)
                    complete = Math.Min(count, 1);
                var text = kind switch
                {
                    BufferEncoding.Utf16Le => System.Text.Encoding.Unicode.GetString(raw, 0, complete),
                    BufferEncoding.Utf16Be => System.Text.Encoding.BigEndianUnicode.GetString(raw, 0, complete),
                    BufferEncoding.Ansi => AnsiEncodings.Get(Encoding.AnsiCodePage).GetString(raw, 0, complete),
                    _ => System.Text.Encoding.UTF8.GetString(raw, 0, complete)
                };
                FlushEncoder(encoder, text, encoded, stream, flush: false);
                position += complete;
            }

            FlushEncoder(encoder, string.Empty, encoded, stream, flush: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(encoded);
        }
    }

    private static void FlushEncoder(Encoder encoder, string text, byte[] bytes, Stream stream, bool flush)
    {
        var chars = text.AsSpan();
        var used = 0;
        var completed = false;
        while (!completed)
        {
            encoder.Convert(chars[used..], bytes, flush, out var charsUsed, out var bytesUsed, out completed);
            if (bytesUsed > 0)
                stream.Write(bytes, 0, bytesUsed);
            used += charsUsed;
            if (charsUsed == 0 && bytesUsed == 0)
                break;
        }
    }

    private JumpTarget ExactEnd()
    {
        EnsureLines(int.MaxValue);
        var line = Math.Max(0, CompleteLineCount - 1);
        var content = line < CompleteLineCount ? GetLineContent(line) : string.Empty;
        var start = _index.StartCount > line ? _index.LineStart(line) : 0;
        return new JumpTarget(start, ByteLength, line, content.Length, true, _index.ScannedTo, content);
    }

    private JumpTarget JumpNear(long guess, bool preferEnd)
    {
        if (ByteLength <= 0)
            return new JumpTarget(0, 0, 0, 0, true, 0, string.Empty);

        var window = (int)Math.Min(JumpWindow, ByteLength);
        long origin;
        if (preferEnd)
            origin = Math.Max(0, ByteLength - window);
        else
            origin = Math.Max(0, Math.Min(guess, ByteLength) - (window / 2));
        if (origin + window > ByteLength)
            window = (int)(ByteLength - origin);

        var rented = ArrayPool<byte>.Shared.Rent(window);
        try
        {
            _table.CopyTo(origin, rented.AsSpan(0, window), _source, _add);
            var from = preferEnd ? origin + window : Math.Clamp(guess, origin, origin + window);
            var lineStart = FindLineStart(rented.AsSpan(0, window), origin, from);
            var caret = preferEnd ? ByteLength : lineStart;
            var previewBytes = (int)Math.Min(JumpWindow, ByteLength - lineStart);
            var preview = previewBytes <= 0
                ? string.Empty
                : TextDecoder.Decode(_table, _source, _add, lineStart, previewBytes, Encoding.AnsiCodePage);
            var cut = preview.IndexOfAny(['\r', '\n']);
            if (cut >= 0)
                preview = preview[..cut];
            if (preview.Length > 4096)
                preview = preview[..4096];
            var column = preferEnd ? preview.Length : 0;
            var line = (int)Math.Min(int.MaxValue / 4, lineStart / AverageLineBytes());
            return new JumpTarget(lineStart, caret, line, column, false, window + previewBytes, preview);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private long FindLineStart(ReadOnlySpan<byte> buffer, long origin, long from)
    {
        var local = (int)Math.Clamp(from - origin, 0, buffer.Length);
        var utf16 = EncodingAt(origin) is BufferEncoding.Utf16Le or BufferEncoding.Utf16Be;
        var little = EncodingAt(origin) == BufferEncoding.Utf16Le;
        if (utf16)
        {
            local &= ~1;
            for (var i = local - 2; i >= 0; i -= 2)
            {
                var unit = little
                    ? (char)(buffer[i] | (buffer[i + 1] << 8))
                    : (char)((buffer[i] << 8) | buffer[i + 1]);
                if (unit is '\n' or '\r')
                    return origin + i + 2;
            }
        }
        else
        {
            for (var i = local - 1; i >= 0; i--)
            {
                if (buffer[i] is 0x0A or 0x0D)
                    return origin + i + 1;
            }
        }

        return origin == 0 ? 0 : origin;
    }

    private long AverageLineBytes()
    {
        var lines = _index.CompleteLineCount(_table.Length);
        if (lines >= 2 && _index.ScannedTo > 0)
            return Math.Max(1, _index.ScannedTo / lines);
        return 40;
    }

    private BufferEncoding EncodingAt(long offset)
    {
        if (offset < _table.Length && _table.TryGetSpan(offset, out var span))
            return span.Piece.Encoding;
        return Encoding.PieceEncoding;
    }

    private string ReadLinePrefix(int line, int maxChars)
    {
        if (maxChars < 1)
            maxChars = 1;
        EnsureLines(line + 1);
        if (line < 0 || line >= CompleteLineCount)
            return string.Empty;
        var start = _index.LineStart(line);
        var boundary = _index.LineBoundary(line, ByteLength);
        var contentBytes = Math.Max(0, boundary - start - BreakBytes(line, boundary));
        var cap = Math.Min(contentBytes, (maxChars * 4L) + 16L);
        return GraphemeNavigator.Trim(
            TextDecoder.Decode(_table, _source, _add, start, cap, Encoding.AnsiCodePage),
            maxChars);
    }
}
