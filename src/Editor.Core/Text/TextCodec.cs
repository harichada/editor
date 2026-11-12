using System.Text;

namespace Editor.Core;

internal static class TextCodec
{
    public static Encoding Create(TextEncodingProfile profile)
    {
        EncodingBootstrap.Ensure();
        return profile.Kind switch
        {
            TextEncodingKind.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
            TextEncodingKind.Utf16Be => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
            TextEncodingKind.Ansi => AnsiEncodings.Get(profile.AnsiCodePage),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
        };
    }

    public static byte[] Encode(string text, TextEncodingProfile profile)
    {
        var encoding = Create(profile);
        var body = encoding.GetBytes(text);
        var preamble = profile.Preamble;
        if (preamble.Length == 0)
            return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result);
        body.CopyTo(result.AsSpan(preamble.Length));
        return result;
    }

    public static void Write(Stream stream, PieceTable table, IByteSource original, AddBuffer add, TextEncodingProfile profile)
    {
        var preamble = profile.Preamble;
        if (!preamble.IsEmpty)
            stream.Write(preamble);

        var encoding = Create(profile);
        var encoder = encoding.GetEncoder();
        var chars = System.Buffers.ArrayPool<char>.Shared.Rent(4096);
        var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(encoding.GetMaxByteCount(4096));
        try
        {
            var pending = 0;
            var position = 0L;
            var length = table.Length;
            while (position < length)
            {
                var unit = TextDecoder.Read(table, original, add, position, profile.AnsiCodePage);
                if (unit.Bytes <= 0)
                    break;
                chars[pending++] = unit.First;
                if (unit.HasSecond)
                    chars[pending++] = unit.Second;
                position += unit.Bytes;
                if (pending >= 4000)
                {
                    Flush(encoder, chars.AsSpan(0, pending), bytes, stream, flush: false);
                    pending = 0;
                }
            }
            Flush(encoder, chars.AsSpan(0, pending), bytes, stream, flush: true);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(chars);
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static void Flush(Encoder encoder, ReadOnlySpan<char> chars, byte[] bytes, Stream stream, bool flush)
    {
        var inUsed = 0;
        var completed = false;
        while (!completed)
        {
            encoder.Convert(chars[inUsed..], bytes, flush, out var charsUsed, out var bytesUsed, out completed);
            if (bytesUsed > 0)
                stream.Write(bytes, 0, bytesUsed);
            inUsed += charsUsed;
            if (charsUsed == 0 && bytesUsed == 0)
                break;
        }
    }
}
