namespace Editor.Core;

public enum TextEncodingKind
{
    Utf8,
    Utf16Le,
    Utf16Be,
    Ansi
}

public enum LineEndingKind
{
    /// <summary>A line that does not end in a break, or a file that has no breaks yet.</summary>
    None,
    Crlf,
    Lf,
    Cr
}

public enum BufferEncoding : byte
{
    Utf8,
    Utf16Le,
    Utf16Be,
    Ansi
}

public enum PieceSource : byte
{
    Original,
    Add
}

public enum CaretMove
{
    Left,
    Right,
    Up,
    Down,
    LineStart,
    LineEnd,
    WordLeft,
    WordRight,
    DocumentStart,
    DocumentEnd,
    PageUp,
    PageDown
}

public enum SearchMode
{
    Plain,
    /// <summary>Reserved for M2. The regex searcher rejects this mode until then.</summary>
    Regex
}

public enum PatternUnit
{
    Character,
    Word,
    Line,
    Paragraph,
    Document
}
