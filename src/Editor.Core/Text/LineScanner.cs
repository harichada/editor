namespace Editor.Core;

internal readonly record struct LineBreak(long Start, int ByteLength, LineEndingKind Kind);

internal static class LineScanner
{
    public static bool TryFindNext(
        PieceTable table,
        IByteSource original,
        AddBuffer add,
        long from,
        long end,
        out LineBreak found)
    {
        var position = from;
        while (position < end)
        {
            if (!table.TryGetSpan(position, out var span))
                break;

            var limit = Math.Min(span.DocumentOffset + span.Length, end);
            var length = (int)Math.Min(limit - position, 64 * 1024);
            if (length <= 0)
                break;

            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var buffer = rented.AsSpan(0, length);
                if (span.Piece.Source == PieceSource.Original)
                    original.CopyTo(span.SourceOffset, buffer);
                else
                    add.CopyTo(span.SourceOffset, buffer);

                if (span.Piece.Encoding is BufferEncoding.Utf16Le or BufferEncoding.Utf16Be)
                {
                    if (TryFindUtf16(buffer, span.Piece.Encoding == BufferEncoding.Utf16Le, position, end, table, original, add, out found))
                        return true;
                    var consumed = buffer.Length - (buffer.Length & 1);
                    if (consumed == 0)
                        break;
                    position += consumed;
                }
                else if (TryFindBytes(buffer, position, end, table, original, add, out found, out var consumed))
                {
                    return true;
                }
                else
                {
                    if (consumed <= 0)
                        break;
                    position += consumed;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }

        found = default;
        return false;
    }

    public static LineEndingProfile Detect(
        PieceTable table,
        IByteSource original,
        AddBuffer add,
        long end,
        LineEndingKind fallback)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        var position = 0L;
        while (position < end && TryFindNext(table, original, add, position, end, out var lineBreak))
        {
            switch (lineBreak.Kind)
            {
                case LineEndingKind.Crlf:
                    crlf++;
                    break;
                case LineEndingKind.Lf:
                    lf++;
                    break;
                default:
                    cr++;
                    break;
            }
            position = lineBreak.Start + lineBreak.ByteLength;
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        LineEndingKind preferred;
        if (crlf >= lf && crlf >= cr && crlf > 0)
            preferred = LineEndingKind.Crlf;
        else if (lf >= cr && lf > 0)
            preferred = LineEndingKind.Lf;
        else if (cr > 0)
            preferred = LineEndingKind.Cr;
        else
            preferred = fallback;

        return new LineEndingProfile
        {
            Preferred = preferred,
            IsMixed = kinds > 1,
            IsProvisional = false
        };
    }

    public static LineEndingKind? Classify(string text)
    {
        var crlf = false;
        var lf = false;
        var cr = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf = true;
                    i++;
                }
                else
                {
                    cr = true;
                }
            }
            else if (text[i] == '\n')
            {
                lf = true;
            }
        }

        var kinds = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0);
        if (kinds == 0)
            return null;
        if (kinds > 1)
            return LineEndingKind.Crlf;
        if (crlf)
            return LineEndingKind.Crlf;
        if (lf)
            return LineEndingKind.Lf;
        return LineEndingKind.Cr;
    }

    public static bool HasMultipleKinds(string text)
    {
        var crlf = false;
        var lf = false;
        var cr = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf = true;
                    i++;
                }
                else
                {
                    cr = true;
                }
            }
            else if (text[i] == '\n')
            {
                lf = true;
            }
        }
        return (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1;
    }

    private static bool TryFindBytes(
        ReadOnlySpan<byte> data,
        long documentOffset,
        long end,
        PieceTable table,
        IByteSource original,
        AddBuffer add,
        out LineBreak found,
        out int consumed)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == 0x0A)
            {
                found = new LineBreak(documentOffset + i, 1, LineEndingKind.Lf);
                consumed = i + 1;
                return true;
            }

            if (data[i] != 0x0D)
                continue;

            var next = documentOffset + i + 1;
            byte? peek = i + 1 < data.Length ? data[i + 1] : Peek(table, original, add, next);
            if (peek == 0x0A)
            {
                found = new LineBreak(documentOffset + i, 2, LineEndingKind.Crlf);
                consumed = i + (i + 1 < data.Length ? 2 : 1);
                return true;
            }

            found = new LineBreak(documentOffset + i, 1, LineEndingKind.Cr);
            consumed = i + 1;
            return true;
        }

        found = default;
        consumed = data.Length;
        return false;
    }

    private static bool TryFindUtf16(
        ReadOnlySpan<byte> data,
        bool littleEndian,
        long documentOffset,
        long end,
        PieceTable table,
        IByteSource original,
        AddBuffer add,
        out LineBreak found)
    {
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            var unit = Unit(data[i], data[i + 1], littleEndian);
            if (unit == '\n')
            {
                found = new LineBreak(documentOffset + i, 2, LineEndingKind.Lf);
                return true;
            }

            if (unit != '\r')
                continue;

            var nextPos = documentOffset + i + 2;
            int? next = null;
            if (i + 3 < data.Length)
                next = Unit(data[i + 2], data[i + 3], littleEndian);
            else if (TryPeekUnit(table, original, add, nextPos, littleEndian, out var peeked))
                next = peeked;

            if (next == '\n')
            {
                found = new LineBreak(documentOffset + i, 4, LineEndingKind.Crlf);
                return true;
            }

            found = new LineBreak(documentOffset + i, 2, LineEndingKind.Cr);
            return true;
        }

        found = default;
        return false;
    }

    private static byte? Peek(PieceTable table, IByteSource original, AddBuffer add, long position)
    {
        if (position < 0 || position >= table.Length)
            return null;
        Span<byte> one = stackalloc byte[1];
        table.CopyTo(position, one, original, add);
        return one[0];
    }

    private static bool TryPeekUnit(
        PieceTable table,
        IByteSource original,
        AddBuffer add,
        long position,
        bool littleEndian,
        out char unit)
    {
        unit = default;
        if (position < 0 || position + 1 >= table.Length)
            return false;
        Span<byte> two = stackalloc byte[2];
        table.CopyTo(position, two, original, add);
        unit = (char)Unit(two[0], two[1], littleEndian);
        return true;
    }

    private static int Unit(byte a, byte b, bool littleEndian) =>
        littleEndian ? a | (b << 8) : (a << 8) | b;
}
