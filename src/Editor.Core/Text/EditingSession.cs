namespace Editor.Core;

/// <summary>
/// Caret, selection, and editing commands over a <see cref="TextDocument"/>.
/// Columns are UTF-16 offsets snapped to grapheme boundaries. The status column
/// is the monospace display column, which treats a tab as a stop of width 8.
/// </summary>
public sealed class EditingSession
{
    private int _anchorLine;
    private int _anchorColumn;
    private int _preferredColumn;
    private string _remotePreview = string.Empty;

    public EditingSession(TextDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        PageLineCount = 32;
    }

    public TextDocument Document { get; }
    public int CaretLine { get; private set; }
    public int CaretColumn { get; private set; }
    public int AnchorLine => _anchorLine;
    public int AnchorColumn => _anchorColumn;
    public int PageLineCount { get; set; }
    public bool HasRemoteCaret { get; private set; }
    public long RemoteViewByte { get; private set; }

    public bool HasSelection => CaretLine != _anchorLine || CaretColumn != _anchorColumn;

    public int CaretUtf16 => DocumentCoordinates.ToUtf16(Document, CaretLine, CaretColumn);

    public bool GoToLine(int lineNumber)
    {
        if (!Document.TryGoToLine(lineNumber, out _))
            return false;
        PlaceJump(Document.LastJump, extend: false);
        return true;
    }

    public void Move(CaretMove move, bool extend)
    {
        if (HasRemoteCaret && move != CaretMove.DocumentEnd)
        {
            MoveRemote(move, extend);
            return;
        }

        var line = CaretLine;
        var column = CaretColumn;
        var content = LineOrEmpty(line);
        switch (move)
        {
            case CaretMove.Left:
                if (column > 0)
                    column = GraphemeNavigator.Previous(content, column);
                else if (line > 0)
                {
                    line--;
                    column = LineOrEmpty(line).Length;
                }
                _preferredColumn = DisplayColumn.Get(LineOrEmpty(line), column);
                break;
            case CaretMove.Right:
                if (column < content.Length)
                    column = GraphemeNavigator.Next(content, column);
                else if (line + 1 < LineCount())
                {
                    line++;
                    column = 0;
                }
                _preferredColumn = DisplayColumn.Get(LineOrEmpty(line), column);
                break;
            case CaretMove.Up:
                if (line > 0)
                {
                    line--;
                    column = DisplayColumn.Utf16At(LineOrEmpty(line), _preferredColumn);
                }
                break;
            case CaretMove.Down:
                if (line + 1 < LineCount())
                {
                    line++;
                    column = DisplayColumn.Utf16At(LineOrEmpty(line), _preferredColumn);
                }
                break;
            case CaretMove.LineStart:
                column = 0;
                _preferredColumn = 0;
                break;
            case CaretMove.LineEnd:
                column = content.Length;
                _preferredColumn = DisplayColumn.Get(content, column);
                break;
            case CaretMove.WordLeft:
                column = GraphemeNavigator.PreviousWord(content, column);
                if (column == 0 && CaretColumn == 0 && line > 0)
                {
                    line--;
                    column = LineOrEmpty(line).Length;
                }
                _preferredColumn = DisplayColumn.Get(LineOrEmpty(line), column);
                break;
            case CaretMove.WordRight:
                if (column >= content.Length && line + 1 < LineCount())
                {
                    line++;
                    column = GraphemeNavigator.NextWord(LineOrEmpty(line), 0);
                }
                else
                {
                    column = GraphemeNavigator.NextWord(content, column);
                }
                _preferredColumn = DisplayColumn.Get(LineOrEmpty(line), column);
                break;
            case CaretMove.DocumentStart:
                line = 0;
                column = 0;
                _preferredColumn = 0;
                break;
            case CaretMove.DocumentEnd:
                PlaceJump(Document.JumpToEnd(), extend);
                return;
            case CaretMove.PageUp:
                line = Math.Max(0, line - Math.Max(1, PageLineCount));
                column = DisplayColumn.Utf16At(LineOrEmpty(line), _preferredColumn);
                break;
            case CaretMove.PageDown:
                Document.EnsureLines(line + Math.Max(1, PageLineCount) + 1);
                line = Math.Min(Math.Max(0, Document.CompleteLineCount - 1), line + Math.Max(1, PageLineCount));
                column = DisplayColumn.Utf16At(LineOrEmpty(line), _preferredColumn);
                break;
        }

        SetCaret(line, column, extend);
    }

    public void SetCaret(int line, int utf16Column, bool extend)
    {
        line = Math.Clamp(line, 0, Math.Max(0, LineCount() - 1));
        var content = LineOrEmpty(line);
        utf16Column = GraphemeNavigator.SnapBack(content, Math.Clamp(utf16Column, 0, content.Length));
        if (!extend)
        {
            _anchorLine = line;
            _anchorColumn = utf16Column;
        }
        CaretLine = line;
        CaretColumn = utf16Column;
    }

    public void PlaceUtf16(int offset, bool extend)
    {
        var (line, column) = DocumentCoordinates.FromUtf16(Document, offset);
        if (!extend)
            _preferredColumn = DisplayColumn.Get(LineOrEmpty(line), column);
        SetCaret(line, column, extend);
    }

    public void InsertText(string text)
    {
        text ??= string.Empty;
        if (text.Length == 0 && !HasSelection)
            return;
        var start = SelectionStartUtf16();
        var count = HasSelection ? SelectionLengthUtf16() : 0;
        Document.ReplaceUtf16(start, count, text);
        PlaceUtf16(start + text.Length, extend: false);
    }

    public void InsertNewLine() => InsertText(Document.LineEndings.PreferredNewLine);

    public void Backspace()
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }
        if (CaretColumn > 0)
        {
            var content = LineOrEmpty(CaretLine);
            var previous = GraphemeNavigator.Previous(content, CaretColumn);
            var start = Document.GetByteOffset(CaretLine, previous);
            var end = Document.GetByteOffset(CaretLine, CaretColumn);
            Document.DeleteByteRange(start, end - start);
            SetCaret(CaretLine, previous, extend: false);
            return;
        }
        if (CaretLine == 0)
            return;
        var previousLength = LineOrEmpty(CaretLine - 1).Length;
        var joinStart = Document.GetByteOffset(CaretLine - 1, previousLength);
        var joinEnd = Document.GetByteOffset(CaretLine, 0);
        Document.DeleteByteRange(joinStart, joinEnd - joinStart);
        SetCaret(CaretLine - 1, previousLength, extend: false);
    }

    public void DeleteForward()
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }
        var content = LineOrEmpty(CaretLine);
        if (CaretColumn < content.Length)
        {
            var next = GraphemeNavigator.Next(content, CaretColumn);
            var start = Document.GetByteOffset(CaretLine, CaretColumn);
            var end = Document.GetByteOffset(CaretLine, next);
            Document.DeleteByteRange(start, end - start);
            SetCaret(CaretLine, CaretColumn, extend: false);
            return;
        }
        if (CaretLine + 1 >= LineCount())
            return;
        var joinStart = Document.GetByteOffset(CaretLine, content.Length);
        var joinEnd = Document.GetByteOffset(CaretLine + 1, 0);
        Document.DeleteByteRange(joinStart, joinEnd - joinStart);
        SetCaret(CaretLine, CaretColumn, extend: false);
    }

    public void DeleteSelection()
    {
        if (!HasSelection)
            return;
        var start = SelectionStartUtf16();
        var count = SelectionLengthUtf16();
        Document.DeleteUtf16(start, count);
        PlaceUtf16(start, extend: false);
    }

    public void SelectAll()
    {
        Document.EnsureLines(int.MaxValue);
        _anchorLine = 0;
        _anchorColumn = 0;
        CaretLine = Math.Max(0, LineCount() - 1);
        CaretColumn = LineOrEmpty(CaretLine).Length;
        _preferredColumn = DisplayColumn.Get(LineOrEmpty(CaretLine), CaretColumn);
    }

    public string GetSelectedText()
    {
        if (!HasSelection)
            return string.Empty;
        var start = SelectionStartUtf16();
        return DocumentCoordinates.Slice(Document, start, SelectionLengthUtf16());
    }

    public bool Undo()
    {
        if (!Document.Undo())
            return false;
        Clamp();
        return true;
    }

    public bool Redo()
    {
        if (!Document.Redo())
            return false;
        Clamp();
        return true;
    }

    public (int Start, int End) LineSelection(int line)
    {
        var (startLine, startColumn, endLine, endColumn) = OrderedSelection();
        if (line < startLine || line > endLine)
            return (0, 0);
        var contentLength = LineOrEmpty(line).Length;
        var start = line == startLine ? startColumn : 0;
        var end = line == endLine ? endColumn : contentLength;
        if (line != endLine && line >= startLine && line < endLine)
            end = contentLength;
        return (start, Math.Max(start, end));
    }

    private void Clamp()
    {
        var lines = Math.Max(1, LineCount());
        CaretLine = Math.Clamp(CaretLine, 0, lines - 1);
        _anchorLine = Math.Clamp(_anchorLine, 0, lines - 1);
        CaretColumn = Math.Clamp(CaretColumn, 0, LineOrEmpty(CaretLine).Length);
        _anchorColumn = Math.Clamp(_anchorColumn, 0, LineOrEmpty(_anchorLine).Length);
        CaretColumn = GraphemeNavigator.SnapBack(LineOrEmpty(CaretLine), CaretColumn);
        _anchorColumn = GraphemeNavigator.SnapBack(LineOrEmpty(_anchorLine), _anchorColumn);
    }

    private void PlaceJump(JumpTarget target, bool extend)
    {
        if (target.Exact)
        {
            HasRemoteCaret = false;
            SetCaret(target.Line, target.Column, extend);
            return;
        }

        HasRemoteCaret = true;
        RemoteViewByte = target.LineStart;
        _remotePreview = target.Preview ?? string.Empty;
        CaretLine = target.Line;
        CaretColumn = Math.Clamp(target.Column, 0, _remotePreview.Length);
        _preferredColumn = DisplayColumn.Get(_remotePreview, CaretColumn);
        if (!extend)
        {
            _anchorLine = CaretLine;
            _anchorColumn = CaretColumn;
        }
    }

    private void MoveRemote(CaretMove move, bool extend)
    {
        if (move == CaretMove.DocumentStart)
        {
            HasRemoteCaret = false;
            SetCaret(0, 0, extend);
            return;
        }

        var column = CaretColumn;
        var content = _remotePreview;
        switch (move)
        {
            case CaretMove.Left:
            case CaretMove.WordLeft:
                column = move == CaretMove.WordLeft
                    ? GraphemeNavigator.PreviousWord(content, column)
                    : GraphemeNavigator.Previous(content, column);
                break;
            case CaretMove.Right:
            case CaretMove.WordRight:
                column = move == CaretMove.WordRight
                    ? GraphemeNavigator.NextWord(content, column)
                    : GraphemeNavigator.Next(content, column);
                break;
            case CaretMove.LineStart:
                column = 0;
                break;
            case CaretMove.LineEnd:
                column = content.Length;
                break;
        }

        CaretColumn = GraphemeNavigator.SnapBack(content, Math.Clamp(column, 0, content.Length));
        if (!extend)
        {
            _anchorLine = CaretLine;
            _anchorColumn = CaretColumn;
        }
    }

    private int LineCount()
    {
        Document.EnsureLines(CaretLine + 2);
        var count = Document.CompleteLineCount;
        return count == 0 ? 1 : count;
    }

    private string LineOrEmpty(int line)
    {
        if (HasRemoteCaret && line == CaretLine)
            return _remotePreview;
        if (Document.ByteLength > 256 * 1024 && line > Document.CompleteLineCount + 8)
            return _remotePreview;
        Document.EnsureLines(line + 1);
        if (line < 0 || line >= Document.CompleteLineCount)
            return string.Empty;
        return Document.GetLineContent(line);
    }

    private int SelectionStartUtf16()
    {
        var (line, column, _, _) = OrderedSelection();
        return DocumentCoordinates.ToUtf16(Document, line, column);
    }

    private int SelectionLengthUtf16()
    {
        var (startLine, startColumn, endLine, endColumn) = OrderedSelection();
        return DocumentCoordinates.ToUtf16(Document, endLine, endColumn)
            - DocumentCoordinates.ToUtf16(Document, startLine, startColumn);
    }

    private (int StartLine, int StartColumn, int EndLine, int EndColumn) OrderedSelection()
    {
        if (CaretLine < _anchorLine || (CaretLine == _anchorLine && CaretColumn < _anchorColumn))
            return (CaretLine, CaretColumn, _anchorLine, _anchorColumn);
        return (_anchorLine, _anchorColumn, CaretLine, CaretColumn);
    }
}

public static class DocumentCoordinates
{
    public static int ToUtf16(TextDocument document, int line, int column)
    {
        var sum = 0;
        for (var i = 0; i < line; i++)
        {
            document.EnsureLines(i + 1);
            if (i >= document.CompleteLineCount)
                break;
            sum += document.GetLineContent(i).Length;
            sum += EndingLength(document.GetLineEnding(i));
        }
        return sum + Math.Max(0, column);
    }

    public static (int Line, int Column) FromUtf16(TextDocument document, int offset)
    {
        if (offset <= 0)
            return (0, 0);
        var line = 0;
        var guard = 0;
        while (guard++ < 8_000_000)
        {
            document.EnsureLines(line + 1);
            if (line >= document.CompleteLineCount)
            {
                var last = Math.Max(0, document.CompleteLineCount - 1);
                return (last, document.CompleteLineCount == 0 ? 0 : document.GetLineContent(last).Length);
            }

            var content = document.GetLineContent(line);
            if (offset <= content.Length)
                return (line, offset);
            offset -= content.Length;
            var ending = EndingLength(document.GetLineEnding(line));
            if (ending == 0 || offset < ending)
                return (line, content.Length);
            offset -= ending;
            if (offset == 0)
            {
                var next = line + 1;
                document.EnsureLines(next + 1);
                if (next >= document.CompleteLineCount)
                    return (line, content.Length);
                return (next, 0);
            }
            line++;
        }
        return (line, 0);
    }

    public static string Slice(TextDocument document, int start, int length)
    {
        if (length <= 0)
            return string.Empty;
        if (document.ByteLength <= TextDocument.MaterializeLimit)
        {
            var text = document.GetText();
            start = Math.Clamp(start, 0, text.Length);
            length = Math.Clamp(length, 0, text.Length - start);
            return text.Substring(start, length);
        }

        return new TextPatternModel(new EditingSession(document)).GetText(start, length);
    }

    private static int EndingLength(LineEndingKind kind) => kind switch
    {
        LineEndingKind.Crlf => 2,
        LineEndingKind.Lf => 1,
        LineEndingKind.Cr => 1,
        _ => 0
    };
}
