namespace Editor.Core;

public readonly record struct FindOptions(bool MatchCase, SearchMode Mode = SearchMode.Plain)
{
    public static FindOptions Plain(bool matchCase = false) => new(matchCase, SearchMode.Plain);
}

public readonly record struct SearchMatch(int Start, int Length);

/// <summary>
/// Search implementation. Plain text is M1. Regex is a separate implementation selected by
/// <see cref="SearchMode"/> so M2 can replace the body without changing callers.
/// </summary>
public interface ITextSearcher
{
    string Kind { get; }
    IReadOnlyList<SearchMatch> Find(string text, string pattern, FindOptions options);
    string Replace(string text, string pattern, string replacement, FindOptions options, out int count);
}

public static class TextSearcherFactory
{
    public static ITextSearcher Create(FindOptions options) =>
        options.Mode == SearchMode.Regex ? new RegexTextSearcher() : new PlainTextSearcher();
}

public sealed class PlainTextSearcher : ITextSearcher
{
    public string Kind => "plain";

    public IReadOnlyList<SearchMatch> Find(string text, string pattern, FindOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(pattern))
            return [];

        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<SearchMatch>();
        var start = 0;
        while (start <= text.Length - pattern.Length)
        {
            var index = text.IndexOf(pattern, start, comparison);
            if (index < 0)
                break;
            matches.Add(new SearchMatch(index, pattern.Length));
            start = index + Math.Max(1, pattern.Length);
        }
        return matches;
    }

    public string Replace(string text, string pattern, string replacement, FindOptions options, out int count)
    {
        var matches = Find(text, pattern, options);
        count = matches.Count;
        if (count == 0)
            return text;
        replacement ??= string.Empty;
        var builder = new System.Text.StringBuilder(text.Length);
        var cursor = 0;
        foreach (var match in matches)
        {
            builder.Append(text, cursor, match.Start - cursor);
            builder.Append(replacement);
            cursor = match.Start + match.Length;
        }
        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }
}

/// <summary>M2 placeholder. The type exists so callers can take a dependency on the regex mode now.</summary>
public sealed class RegexTextSearcher : ITextSearcher
{
    public string Kind => "regex";

    public IReadOnlyList<SearchMatch> Find(string text, string pattern, FindOptions options) =>
        throw NotAvailable();

    public string Replace(string text, string pattern, string replacement, FindOptions options, out int count)
    {
        count = 0;
        throw NotAvailable();
    }

    private static NotSupportedException NotAvailable() =>
        new("Regular expression search is not available until M2.");
}
