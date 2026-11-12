using System.Text;

namespace Editor.Core;

/// <summary>Which lines a viewport should ask the document to materialize.</summary>
public readonly record struct ViewportPlan(int FirstLine, int LineCount, long EstimatedTotalLines);

public static class ViewportLayout
{
    public static ViewportPlan Plan(int scrollLine, int viewportLines, int completeLines, bool indexComplete, long estimatedTotal)
    {
        if (viewportLines < 1)
            viewportLines = 1;
        if (scrollLine < 0)
            scrollLine = 0;
        var total = indexComplete ? Math.Max(completeLines, 1) : Math.Max(Math.Max(completeLines, 1), estimatedTotal);
        if (scrollLine > total - 1)
            scrollLine = (int)Math.Max(0, total - 1);
        return new ViewportPlan(scrollLine, viewportLines, total);
    }

    public static long EstimateLineCount(long byteLength, long scannedBytes, int completeLines)
    {
        if (byteLength <= 0)
            return 1;
        if (scannedBytes <= 0 || completeLines <= 1)
            return Math.Max(1, byteLength / 40);
        var average = Math.Max(1, scannedBytes / completeLines);
        return Math.Max(completeLines, (byteLength + average - 1) / average);
    }
}

public readonly record struct GlyphCell(int Utf16Start, int Utf16Length, string Text, int Column, int Width);

/// <summary>
/// Monospace layout used by the Win2D surface. Only cells inside the horizontal window are returned,
/// so a very long line does not allocate a cell per character outside the viewport.
/// </summary>
public static class MonospaceLayout
{
    public static IReadOnlyList<GlyphCell> VisibleCells(string line, int firstColumn, int columnCount)
    {
        if (string.IsNullOrEmpty(line) || columnCount <= 0)
            return [];
        if (firstColumn < 0)
            firstColumn = 0;

        var cells = new List<GlyphCell>();
        var index = 0;
        var column = 0;
        var endColumn = firstColumn + columnCount;
        while (index < line.Length && column < endColumn)
        {
            var next = GraphemeNavigator.Next(line, index);
            if (next <= index)
                break;
            var width = CellWidth(line, index, next, column);
            if (column + width > firstColumn)
                cells.Add(new GlyphCell(index, next - index, line[index..next], column, width));
            column += width;
            index = next;
        }
        return cells;
    }

    public static int HitTest(string line, double pixelX, double charWidth)
    {
        if (string.IsNullOrEmpty(line) || charWidth <= 0 || pixelX <= 0)
            return 0;
        var target = (int)Math.Round(pixelX / charWidth);
        return DisplayColumn.Utf16At(line, Math.Max(0, target));
    }

    private static int CellWidth(string line, int start, int next, int column)
    {
        if (next == start + 1 && line[start] == '\t')
        {
            var stop = DisplayColumn.TabWidth - (column % DisplayColumn.TabWidth);
            return stop == 0 ? DisplayColumn.TabWidth : stop;
        }
        return 1;
    }
}

/// <summary>
/// UI-free text pattern: document, selection, caret, visible range, and movement by grapheme.
/// The WinUI automation peer is a thin wrapper over this model.
/// </summary>
public sealed class TextPatternModel
{
    public TextPatternModel(EditingSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public EditingSession Session { get; }

    public (int Start, int End) DocumentRange
    {
        get
        {
            var length = DocumentEnd();
            return (0, length);
        }
    }

    public (int Start, int End) GetSelection()
    {
        var start = DocumentCoordinates.ToUtf16(Session.Document, Session.AnchorLine, Session.AnchorColumn);
        var end = Session.CaretUtf16;
        return start <= end ? (start, end) : (end, start);
    }

    public int GetCaret() => Session.CaretUtf16;

    public (int Start, int End) GetVisibleRange(int firstLine, int lineCount)
    {
        if (lineCount < 1)
            lineCount = 1;
        if (firstLine < 0)
            firstLine = 0;
        var start = DocumentCoordinates.ToUtf16(Session.Document, firstLine, 0);
        var lastLine = firstLine + lineCount - 1;
        Session.Document.EnsureLines(lastLine + 1);
        if (lastLine >= Session.Document.CompleteLineCount)
            lastLine = Math.Max(0, Session.Document.CompleteLineCount - 1);
        var content = lastLine < Session.Document.CompleteLineCount
            ? Session.Document.GetLineContent(lastLine)
            : string.Empty;
        var end = DocumentCoordinates.ToUtf16(Session.Document, lastLine, content.Length);
        return (start, Math.Max(start, end));
    }

    public string GetText(int start, int length)
    {
        if (length < 0)
            length = Math.Max(0, DocumentEnd() - Math.Max(0, start));
        if (length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        var line = 0;
        var seen = 0;
        var guard = 0;
        while (builder.Length < length && guard++ < 8_000_000)
        {
            Session.Document.EnsureLines(line + 1);
            if (line >= Session.Document.CompleteLineCount)
                break;
            var chunk = Session.Document.GetLineContent(line) + EndingText(Session.Document.GetLineEnding(line));
            if (seen + chunk.Length > start)
            {
                var local = Math.Max(0, start - seen);
                var take = Math.Min(chunk.Length - local, length - builder.Length);
                if (take > 0)
                    builder.Append(chunk, local, take);
            }
            seen += chunk.Length;
            if (seen >= start + length)
                break;
            line++;
        }
        return builder.ToString();
    }

    public int Move(int index, PatternUnit unit, int count)
    {
        if (Session.Document.ByteLength > TextDocument.MaterializeLimit)
            throw new NotSupportedException("Pattern movement on a document this large is not supported in M1.");
        var text = Session.Document.GetText();
        index = Math.Clamp(index, 0, text.Length);
        if (count == 0 || text.Length == 0)
            return index;

        var direction = Math.Sign(count);
        var steps = Math.Abs(count);
        for (var step = 0; step < steps; step++)
        {
            var next = unit switch
            {
                PatternUnit.Character when direction < 0 => GraphemeNavigator.Previous(text, index),
                PatternUnit.Character => GraphemeNavigator.Next(text, index),
                PatternUnit.Word when direction < 0 => GraphemeNavigator.PreviousWord(text, index),
                PatternUnit.Word => GraphemeNavigator.NextWord(text, index),
                PatternUnit.Line or PatternUnit.Paragraph => MoveLine(text, index, direction),
                PatternUnit.Document => direction < 0 ? 0 : text.Length,
                _ => index
            };
            if (next == index)
                break;
            index = next;
        }
        return index;
    }

    public void SelectRange(int start, int end)
    {
        if (end < start)
            (start, end) = (end, start);
        Session.PlaceUtf16(start, extend: false);
        Session.PlaceUtf16(end, extend: true);
    }

    private int DocumentEnd()
    {
        Session.Document.EnsureLines(int.MaxValue);
        var lines = Session.Document.CompleteLineCount;
        if (lines <= 0)
            return 0;
        var last = lines - 1;
        return DocumentCoordinates.ToUtf16(Session.Document, last, Session.Document.GetLineContent(last).Length);
    }

    private static int MoveLine(string text, int index, int direction)
    {
        if (direction < 0)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
            if (lineStart < 0)
                return 0;
            if (lineStart > 0 && text[lineStart - 1] == '\r')
                lineStart--;
            var previous = text.LastIndexOf('\n', Math.Max(0, lineStart - 1));
            if (previous < 0)
                return 0;
            return previous + 1;
        }

        var next = text.IndexOf('\n', index);
        return next < 0 ? text.Length : next + 1;
    }

    private static string EndingText(LineEndingKind kind) => kind switch
    {
        LineEndingKind.Crlf => "\r\n",
        LineEndingKind.Lf => "\n",
        LineEndingKind.Cr => "\r",
        _ => string.Empty
    };
}

/// <summary>
/// Composition state machine behind <c>CoreTextEditContext</c>.
/// Emoji-panel commits and IME updates both arrive as text replacements.
/// </summary>
public sealed class TextInputModel
{
    private readonly EditingSession _session;
    private bool _composing;
    private int _compositionStart;
    private int _compositionLength;

    public TextInputModel(EditingSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public bool IsComposing => _composing;
    public int CompositionStart => _compositionStart;
    public int CompositionLength => _compositionLength;

    public void BeginComposition()
    {
        _composing = true;
        _compositionStart = _session.CaretUtf16;
        _compositionLength = 0;
    }

    public void UpdateComposition(string text)
    {
        text ??= string.Empty;
        if (!_composing)
            BeginComposition();
        _session.Document.ReplaceUtf16(_compositionStart, _compositionLength, text);
        _compositionLength = text.Length;
        _session.PlaceUtf16(_compositionStart + text.Length, extend: false);
    }

    public void CompleteComposition(string? finalText = null)
    {
        if (!_composing)
        {
            if (!string.IsNullOrEmpty(finalText))
                ApplyTextUpdate(_session.CaretUtf16, _session.CaretUtf16, finalText);
            return;
        }

        if (finalText is not null && finalText != CurrentCompositionText())
            UpdateComposition(finalText);
        _composing = false;
        _compositionLength = 0;
    }

    public void CancelComposition()
    {
        if (!_composing)
            return;
        if (_compositionLength > 0)
        {
            _session.Document.ReplaceUtf16(_compositionStart, _compositionLength, string.Empty);
            _session.PlaceUtf16(_compositionStart, extend: false);
        }
        _composing = false;
        _compositionLength = 0;
    }

    /// <summary>Keyboard text, paste from the input framework, or an emoji-panel commit.</summary>
    public void ApplyTextUpdate(int replaceStart, int replaceEnd, string text)
    {
        text ??= string.Empty;
        var start = Math.Min(replaceStart, replaceEnd);
        var end = Math.Max(replaceStart, replaceEnd);
        _session.Document.ReplaceUtf16(start, end - start, text);
        _session.PlaceUtf16(start + text.Length, extend: false);
        if (_composing)
        {
            _compositionStart = start;
            _compositionLength = text.Length;
        }
    }

    private string CurrentCompositionText()
    {
        if (_compositionLength <= 0)
            return string.Empty;
        return DocumentCoordinates.Slice(_session.Document, _compositionStart, _compositionLength);
    }
}
