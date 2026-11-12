using System.Collections.Concurrent;
using System.Text;

namespace Editor.Core;

internal readonly struct CodeUnit
{
    public char First { get; init; }
    public char Second { get; init; }
    public bool HasSecond { get; init; }
    public int Bytes { get; init; }
    public int Chars => HasSecond ? 2 : 1;
}

internal static class EncodingBootstrap
{
    static EncodingBootstrap()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static void Ensure()
    {
        // Touch the static constructor.
        _ = typeof(EncodingBootstrap);
    }
}

internal static class AnsiEncodings
{
    private static readonly ConcurrentDictionary<int, Encoding> Cache = new();

    public static Encoding Get(int codePage)
    {
        EncodingBootstrap.Ensure();
        return Cache.GetOrAdd(codePage, static page =>
        {
            try
            {
                return Encoding.GetEncoding(page, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            }
            catch (ArgumentException)
            {
                return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            }
        });
    }
}

internal static class TextDecoder
{
    public static CodeUnit Read(PieceTable table, IByteSource original, AddBuffer add, long position, int ansiCodePage)
    {
        if (!table.TryGetSpan(position, out var span) || span.Length <= 0)
            return Replacement(1);

        Span<byte> buffer = stackalloc byte[4];
        var count = (int)Math.Min(4, span.Length);
        Read(original, add, span, buffer[..count]);
        return span.Piece.Encoding switch
        {
            BufferEncoding.Utf16Le => ReadUtf16(buffer[..count], littleEndian: true),
            BufferEncoding.Utf16Be => ReadUtf16(buffer[..count], littleEndian: false),
            BufferEncoding.Ansi => ReadAnsi(buffer[..count], ansiCodePage),
            _ => ReadUtf8(buffer[..count])
        };
    }

    public static string Decode(PieceTable table, IByteSource original, AddBuffer add, long start, long byteLength, int ansiCodePage)
    {
        if (byteLength <= 0)
            return string.Empty;
        var builder = new StringBuilder();
        var position = start;
        var end = start + byteLength;
        while (position < end)
        {
            var unit = Read(table, original, add, position, ansiCodePage);
            if (unit.Bytes <= 0 || position + unit.Bytes > end)
                break;
            builder.Append(unit.First);
            if (unit.HasSecond)
                builder.Append(unit.Second);
            position += unit.Bytes;
        }
        return builder.ToString();
    }

    private static void Read(IByteSource original, AddBuffer add, PieceSpan span, Span<byte> destination)
    {
        if (span.Piece.Source == PieceSource.Original)
            original.CopyTo(span.SourceOffset, destination);
        else
            add.CopyTo(span.SourceOffset, destination);
    }

    private static CodeUnit ReadUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return Replacement(1);
        var lead = bytes[0];
        if (lead <= 0x7F)
            return Single((char)lead, 1);
        if (lead < 0xC2 || lead > 0xF4)
            return Replacement(1);

        if (lead <= 0xDF)
        {
            if (bytes.Length < 2 || !IsContinuation(bytes[1]))
                return Replacement(1);
            var codePoint = ((lead & 0x1F) << 6) | (bytes[1] & 0x3F);
            return Single((char)codePoint, 2);
        }

        if (lead <= 0xEF)
        {
            if (bytes.Length < 3 || !IsContinuation(bytes[1]) || !IsContinuation(bytes[2]))
                return Replacement(1);
            if (lead == 0xE0 && bytes[1] < 0xA0)
                return Replacement(1);
            if (lead == 0xED && bytes[1] >= 0xA0)
                return Replacement(1);
            var codePoint = ((lead & 0x0F) << 12) | ((bytes[1] & 0x3F) << 6) | (bytes[2] & 0x3F);
            return Single((char)codePoint, 3);
        }

        if (bytes.Length < 4 || !IsContinuation(bytes[1]) || !IsContinuation(bytes[2]) || !IsContinuation(bytes[3]))
            return Replacement(1);
        if (lead == 0xF0 && bytes[1] < 0x90)
            return Replacement(1);
        if (lead == 0xF4 && bytes[1] >= 0x90)
            return Replacement(1);
        var value = ((lead & 0x07) << 18) | ((bytes[1] & 0x3F) << 12) | ((bytes[2] & 0x3F) << 6) | (bytes[3] & 0x3F);
        if (value is < 0x10000 or > 0x10FFFF)
            return Replacement(1);
        value -= 0x10000;
        return new CodeUnit
        {
            First = (char)(0xD800 + (value >> 10)),
            Second = (char)(0xDC00 + (value & 0x3FF)),
            HasSecond = true,
            Bytes = 4
        };
    }

    private static CodeUnit ReadUtf16(ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        if (bytes.Length < 2)
            return Replacement(Math.Max(1, bytes.Length));
        var first = DecodeUnit(bytes[0], bytes[1], littleEndian);
        if (char.IsHighSurrogate(first) && bytes.Length >= 4)
        {
            var second = DecodeUnit(bytes[2], bytes[3], littleEndian);
            if (char.IsLowSurrogate(second))
            {
                return new CodeUnit { First = first, Second = second, HasSecond = true, Bytes = 4 };
            }
        }
        return Single(first, 2);
    }

    private static CodeUnit ReadAnsi(ReadOnlySpan<byte> bytes, int codePage)
    {
        var encoding = AnsiEncodings.Get(codePage);
        if (bytes.Length == 0)
            return Replacement(1);
        if (encoding.IsSingleByte || bytes.Length == 1)
        {
            Span<char> chars = stackalloc char[2];
            var written = encoding.GetChars(bytes[..1], chars);
            return Single(written > 0 ? chars[0] : '\uFFFD', 1);
        }

        Span<char> pair = stackalloc char[2];
        var pairCount = encoding.GetChars(bytes[..2], pair);
        if (pairCount == 1)
            return Single(pair[0], 2);

        Span<char> one = stackalloc char[2];
        var oneCount = encoding.GetChars(bytes[..1], one);
        return Single(oneCount > 0 ? one[0] : '\uFFFD', 1);
    }

    private static char DecodeUnit(byte a, byte b, bool littleEndian) =>
        littleEndian ? (char)(a | (b << 8)) : (char)((a << 8) | b);

    private static bool IsContinuation(byte value) => (value & 0xC0) == 0x80;

    private static CodeUnit Single(char value, int bytes) => new() { First = value, Bytes = bytes };

    private static CodeUnit Replacement(int bytes) => new() { First = '\uFFFD', Bytes = Math.Max(1, bytes) };
}

internal static class Utf8Validator
{
    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        var index = 0;
        while (index < data.Length)
        {
            var lead = data[index];
            if (lead <= 0x7F)
            {
                index++;
                continue;
            }

            int needed;
            if (lead is >= 0xC2 and <= 0xDF)
                needed = 1;
            else if (lead is >= 0xE0 and <= 0xEF)
                needed = 2;
            else if (lead is >= 0xF0 and <= 0xF4)
                needed = 3;
            else
                return false;

            if (index + needed >= data.Length)
                return false;

            for (var extra = 1; extra <= needed; extra++)
            {
                if ((data[index + extra] & 0xC0) != 0x80)
                    return false;
            }

            if (needed == 2)
            {
                if (lead == 0xE0 && data[index + 1] < 0xA0)
                    return false;
                if (lead == 0xED && data[index + 1] >= 0xA0)
                    return false;
            }
            else if (needed == 3)
            {
                if (lead == 0xF0 && data[index + 1] < 0x90)
                    return false;
                if (lead == 0xF4 && data[index + 1] >= 0x90)
                    return false;
            }

            index += needed + 1;
        }

        return true;
    }
}
