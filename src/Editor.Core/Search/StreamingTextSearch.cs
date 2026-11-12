namespace Editor.Core;

/// <summary>
/// Plain search over a document that is decoded a window at a time.
/// The window overlap is <c>pattern.Length - 1</c>, so a match that crosses a
/// window boundary is still reported, and matches do not overlap.
/// </summary>
internal static class StreamingTextSearch
{
    internal readonly record struct DecodedWindow(string Text, int BytesConsumed);

    public static List<SearchMatch> FindAll(
        Func<long, int, DecodedWindow> read,
        long byteLength,
        string pattern,
        StringComparison comparison,
        int windowChars,
        CancellationToken cancellation)
    {
        var matches = new List<SearchMatch>();
        Scan(read, byteLength, pattern, comparison, windowChars, cancellation, match =>
        {
            matches.Add(match);
            return false;
        });
        return matches;
    }

    public static SearchMatch? FindNext(
        Func<long, int, DecodedWindow> read,
        long byteLength,
        string pattern,
        StringComparison comparison,
        int windowChars,
        int fromUtf16,
        CancellationToken cancellation)
    {
        SearchMatch? first = null;
        SearchMatch? atOrAfter = null;
        Scan(read, byteLength, pattern, comparison, windowChars, cancellation, match =>
        {
            first ??= match;
            if (match.Start >= fromUtf16)
            {
                atOrAfter = match;
                return true;
            }
            return false;
        });
        return atOrAfter ?? first;
    }

    public static int Replace(
        Func<long, int, DecodedWindow> read,
        long byteLength,
        string pattern,
        string replacement,
        StringComparison comparison,
        int windowChars,
        Stream utf8Output,
        CancellationToken cancellation)
    {
        replacement ??= string.Empty;
        var count = 0;
        var carry = string.Empty;
        var produced = 0;
        var emitted = 0;
        long bytePos = 0;
        var keep = Math.Max(0, pattern.Length - 1);

        while (bytePos < byteLength)
        {
            cancellation.ThrowIfCancellationRequested();
            var window = read(bytePos, Math.Max(1, windowChars));
            if (window.BytesConsumed <= 0 || window.Text.Length == 0)
                break;

            var data = carry.Length == 0 ? window.Text : carry + window.Text;
            var origin = produced - carry.Length;
            var last = bytePos + window.BytesConsumed >= byteLength;
            var tail = last ? 0 : Math.Min(keep, data.Length);
            var safeEnd = origin + data.Length - tail;
            var from = Math.Max(0, emitted - origin);

            while (from <= data.Length - pattern.Length)
            {
                var index = data.IndexOf(pattern, from, comparison);
                if (index < 0)
                    break;
                var matchStart = origin + index;
                var matchEnd = matchStart + pattern.Length;
                if (matchEnd > origin + carry.Length && matchStart >= emitted && (last || matchStart < safeEnd))
                {
                    Write(utf8Output, data, emitted - origin, matchStart - emitted);
                    WriteText(utf8Output, replacement);
                    emitted = matchEnd;
                    count++;
                }
                from = index + Math.Max(1, pattern.Length);
            }

            if (emitted < safeEnd)
            {
                Write(utf8Output, data, emitted - origin, safeEnd - emitted);
                emitted = safeEnd;
            }

            carry = tail == 0 ? string.Empty : data[^tail..];
            produced += window.Text.Length;
            bytePos += window.BytesConsumed;
        }

        return count;
    }

    private static void Scan(
        Func<long, int, DecodedWindow> read,
        long byteLength,
        string pattern,
        StringComparison comparison,
        int windowChars,
        CancellationToken cancellation,
        Func<SearchMatch, bool> onMatch)
    {
        var carry = string.Empty;
        var produced = 0;
        var nextStart = 0;
        long bytePos = 0;
        var keep = Math.Max(0, pattern.Length - 1);

        while (bytePos < byteLength)
        {
            cancellation.ThrowIfCancellationRequested();
            var window = read(bytePos, Math.Max(1, windowChars));
            if (window.BytesConsumed <= 0 || window.Text.Length == 0)
                break;

            var data = carry.Length == 0 ? window.Text : carry + window.Text;
            var origin = produced - carry.Length;
            var last = bytePos + window.BytesConsumed >= byteLength;
            var tail = last ? 0 : Math.Min(keep, data.Length);
            var safeEnd = origin + data.Length - tail;
            var from = Math.Max(0, nextStart - origin);

            while (from <= data.Length - pattern.Length)
            {
                var index = data.IndexOf(pattern, from, comparison);
                if (index < 0)
                    break;
                var matchStart = origin + index;
                var matchEnd = matchStart + pattern.Length;
                if (matchEnd > origin + carry.Length && matchStart >= nextStart && (last || matchStart < safeEnd))
                {
                    nextStart = matchEnd;
                    if (onMatch(new SearchMatch(matchStart, pattern.Length)))
                        return;
                }
                from = index + Math.Max(1, pattern.Length);
            }

            carry = tail == 0 ? string.Empty : data[^tail..];
            produced += window.Text.Length;
            bytePos += window.BytesConsumed;
        }
    }

    private static void Write(Stream stream, string data, int start, int length)
    {
        if (length <= 0)
            return;
        if (start < 0)
            start = 0;
        if (start + length > data.Length)
            length = data.Length - start;
        if (length <= 0)
            return;
        WriteText(stream, data.Substring(start, length));
    }

    private static void WriteText(Stream stream, string text)
    {
        if (text.Length == 0)
            return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
    }
}
