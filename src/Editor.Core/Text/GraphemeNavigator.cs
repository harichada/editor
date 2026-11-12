using System.Globalization;
using System.Text;

namespace Editor.Core;

/// <summary>
/// Caret motion over extended grapheme clusters (emoji ZWJ sequences, combining marks, surrogate pairs).
/// Boundaries come from <see cref="StringInfo"/>, which follows the platform Unicode text-segmentation rules.
/// </summary>
public static class GraphemeNavigator
{
    public static int ClusterCount(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            index += StringInfo.GetNextTextElementLength(text, index);
            count++;
        }
        return count;
    }

    public static int Next(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index >= text.Length)
            return text?.Length ?? 0;
        if (index < 0)
            index = 0;
        index = SnapBack(text, index);
        if (index >= text.Length)
            return text.Length;
        return index + StringInfo.GetNextTextElementLength(text, index);
    }

    public static int Previous(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index <= 0)
            return 0;
        if (index > text.Length)
            index = text.Length;
        var bounds = StringInfo.ParseCombiningCharacters(text);
        var answer = 0;
        var lo = 0;
        var hi = bounds.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (bounds[mid] < index)
            {
                answer = bounds[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return answer;
    }

    public static int SnapBack(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index <= 0)
            return 0;
        if (index >= text.Length)
            return text.Length;
        var bounds = StringInfo.ParseCombiningCharacters(text);
        var answer = 0;
        var lo = 0;
        var hi = bounds.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (bounds[mid] <= index)
            {
                answer = bounds[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return answer;
    }

    public static int SnapNearest(string text, int index)
    {
        var back = SnapBack(text, index);
        if (back >= text.Length || back == index)
            return back;
        var next = back + StringInfo.GetNextTextElementLength(text, back);
        return index - back <= next - index ? back : next;
    }

    public static int ClusterIndex(string text, int utf16Index)
    {
        if (string.IsNullOrEmpty(text) || utf16Index <= 0)
            return 0;
        var bounds = StringInfo.ParseCombiningCharacters(text);
        var count = 0;
        for (var i = 0; i < bounds.Length; i++)
        {
            if (bounds[i] >= utf16Index)
                break;
            count++;
        }
        return count;
    }

    public static string Trim(string text, int maxClusters)
    {
        if (maxClusters < 0)
            throw new ArgumentOutOfRangeException(nameof(maxClusters));
        if (string.IsNullOrEmpty(text) || maxClusters == 0)
            return string.Empty;
        var index = 0;
        var count = 0;
        while (index < text.Length && count < maxClusters)
        {
            index += StringInfo.GetNextTextElementLength(text, index);
            count++;
        }
        return index >= text.Length ? text : text[..index];
    }

    public static int NextWord(string text, int index)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var i = SnapBack(text, Math.Clamp(index, 0, text.Length));
        if (i >= text.Length)
            return text.Length;
        if (IsWord(text, i))
        {
            while (i < text.Length && IsWord(text, i))
                i = Next(text, i);
        }
        while (i < text.Length && !IsWord(text, i))
            i = Next(text, i);
        return i;
    }

    public static int PreviousWord(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index <= 0)
            return 0;
        var i = Previous(text, Math.Clamp(index, 0, text.Length));
        while (i > 0 && !IsWord(text, i))
            i = Previous(text, i);
        while (i > 0 && IsWord(text, Previous(text, i)))
            i = Previous(text, i);
        return i;
    }

    public static bool IsWord(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length)
            return false;
        index = SnapBack(text, index);
        if (index >= text.Length || char.IsSurrogate(text[index]) && !char.IsHighSurrogate(text[index]))
            return false;
        var rune = Rune.GetRuneAt(text, index);
        return Rune.IsLetterOrDigit(rune) || rune.Value == '_';
    }
}

/// <summary>Monospace display columns. A tab advances to the next multiple of <see cref="TabWidth"/>.</summary>
public static class DisplayColumn
{
    public const int TabWidth = 8;

    public static int Get(string text, int utf16Index)
    {
        if (string.IsNullOrEmpty(text) || utf16Index <= 0)
            return 0;
        utf16Index = Math.Clamp(utf16Index, 0, text.Length);
        var column = 0;
        var index = 0;
        while (index < utf16Index)
        {
            var next = GraphemeNavigator.Next(text, index);
            if (next <= index)
                break;
            column += Width(text, index, next, column);
            index = next;
        }
        return column;
    }

    public static int Utf16At(string text, int displayColumn)
    {
        if (string.IsNullOrEmpty(text) || displayColumn <= 0)
            return 0;
        var column = 0;
        var index = 0;
        while (index < text.Length && column < displayColumn)
        {
            var next = GraphemeNavigator.Next(text, index);
            if (next <= index)
                break;
            var width = Width(text, index, next, column);
            if (column + width > displayColumn && text[index] != '\t')
                break;
            column += width;
            index = next;
        }
        return index;
    }

    public static int Count(string text) => Get(text, text?.Length ?? 0);

    private static int Width(string text, int start, int next, int column)
    {
        if (next == start + 1 && text[start] == '\t')
        {
            var stop = TabWidth - (column % TabWidth);
            return stop == 0 ? TabWidth : stop;
        }
        return 1;
    }
}
