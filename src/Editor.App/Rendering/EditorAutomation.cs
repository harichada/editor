using System.Runtime.InteropServices;
using Editor.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Windows.Foundation;

namespace Editor.App.Rendering;

/// <summary>
/// UI Automation text pattern for the custom surface.
/// Narrator and NVDA read the document, caret, and selection through this peer.
/// Movement and text extraction go through <see cref="TextPatternModel"/>, which is unit-tested.
/// </summary>
public sealed class EditorAutomationPeer : FrameworkElementAutomationPeer, ITextProvider2
{
    private readonly EditorSurface _surface;
    private TextPatternModel? _model;

    public EditorAutomationPeer(EditorSurface owner) : base(owner)
    {
        _surface = owner;
    }

    private TextPatternModel? Model
    {
        get
        {
            if (_surface.Session is null)
                return null;
            if (_model is null || !ReferenceEquals(_model.Session, _surface.Session))
                _model = new TextPatternModel(_surface.Session);
            return _model;
        }
    }

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        if (patternInterface is PatternInterface.Text or PatternInterface.Text2)
            return this;
        return base.GetPatternCore(patternInterface);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override string GetClassNameCore() => "EditorSurface";

    protected override string GetNameCore() => "Document";

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    public ITextRangeProvider DocumentRange => new EditorTextRange(this, 0, int.MaxValue);

    public SupportedTextSelection SupportedTextSelection =>
        Microsoft.UI.Xaml.Automation.SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        var model = Model;
        if (model is null)
            return [];
        var (start, end) = model.GetSelection();
        return [new EditorTextRange(this, start, end)];
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        var model = Model;
        if (model is null)
            return [];
        var (start, end) = model.GetVisibleRange(_surface.FirstVisibleLine, _surface.VisibleLineCount);
        return [new EditorTextRange(this, start, end)];
    }

    public ITextRangeProvider? RangeFromChild(IRawElementProviderSimple childElement) => null;

    public ITextRangeProvider? RangeFromPoint(Point screenLocation)
    {
        if (Model is null)
            return null;
        try
        {
            var origin = _surface.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var (line, column) = _surface.HitTestLocal(screenLocation.X - origin.X, screenLocation.Y - origin.Y);
            var offset = DocumentCoordinates.ToUtf16(_surface.Session!.Document, line, column);
            return new EditorTextRange(this, offset, offset);
        }
        catch (Exception)
        {
            var caret = Model.GetCaret();
            return new EditorTextRange(this, caret, caret);
        }
    }

    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        isActive = _surface.HasKeyboardFocus;
        var caret = Model?.GetCaret() ?? 0;
        return new EditorTextRange(this, caret, caret);
    }

    public ITextRangeProvider? RangeFromAnnotation(IRawElementProviderSimple annotationElement) => null;

    internal TextPatternModel? Pattern => Model;

    internal IRawElementProviderSimple AsProvider() => ProviderFromPeer(this);

    internal void ScrollTo(int utf16)
    {
        if (_surface.Session is null)
            return;
        var (line, _) = DocumentCoordinates.FromUtf16(_surface.Session.Document, utf16);
        _surface.ScrollLineIntoView(line);
    }
}

public sealed class EditorTextRange : ITextRangeProvider
{
    private readonly EditorAutomationPeer _peer;
    private int _start;
    private int _end;

    public EditorTextRange(EditorAutomationPeer peer, int start, int end)
    {
        _peer = peer;
        if (end < start && end != int.MaxValue)
            (start, end) = (end, start);
        _start = Math.Max(0, start);
        _end = end < 0 ? 0 : end;
    }

    public void AddToSelection() => Select();

    public ITextRangeProvider Clone() => new EditorTextRange(_peer, _start, _end);

    public bool Compare(ITextRangeProvider range)
    {
        if (range is not EditorTextRange other)
            return false;
        return _start == other._start && EffectiveEnd() == other.EffectiveEnd();
    }

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not EditorTextRange other)
            return -1;
        var left = endpoint == TextPatternRangeEndpoint.Start ? _start : EffectiveEnd();
        var right = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other.EffectiveEnd();
        return left.CompareTo(right);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        if (unit is TextUnit.Document or TextUnit.Page)
        {
            _start = 0;
            _end = int.MaxValue;
            return;
        }

        var next = MoveIndex(_start, unit, 1);
        var start = MoveIndex(next, unit, -1);
        if (start > _start)
            start = _start;
        _start = start;
        if (_end < next)
            _end = next;
    }

    public ITextRangeProvider? FindAttribute(int attribute, object value, bool backward) => null;

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text) || _peer.Pattern is null)
            return null;
        var windowLength = _end == int.MaxValue ? 1_000_000 : Math.Max(0, _end - _start);
        windowLength = Math.Min(windowLength, 1_000_000);
        var window = _peer.Pattern.GetText(_start, windowLength);
        var matches = new PlainTextSearcher().Find(window, text, FindOptions.Plain(!ignoreCase));
        if (matches.Count == 0)
            return null;
        var match = backward ? matches[^1] : matches[0];
        return new EditorTextRange(_peer, _start + match.Start, _start + match.Start + match.Length);
    }

    public object GetAttributeValue(int attribute) => UiaReserved.NotSupported;

    public void GetBoundingRectangles(out double[] rectangles)
    {
        var local = _peer.Owner is EditorSurface surface ? surface.CaretLocalRect() : default;
        if (local.Height <= 0)
        {
            rectangles = [];
            return;
        }
        rectangles = [local.X, local.Y, Math.Max(local.Width, 1), local.Height];
    }

    public IRawElementProviderSimple[] GetChildren() => [];

    public IRawElementProviderSimple GetEnclosingElement() => _peer.AsProvider();

    public string GetText(int maxLength)
    {
        var model = _peer.Pattern;
        if (model is null || maxLength == 0)
            return string.Empty;
        var available = _end == int.MaxValue ? int.MaxValue : Math.Max(0, _end - _start);
        var take = maxLength < 0 ? available : Math.Min(maxLength, available);
        var document = model.Session.Document;
        if (document.ByteLength > TextDocument.MaterializeLimit && (take < 0 || take > 8192))
            take = 8192;
        return model.GetText(_start, take);
    }

    public int Move(TextUnit unit, int count)
    {
        var moved = MoveEndpoint(TextPatternRangeEndpoint.Start, unit, count);
        _end = _start;
        return moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count) =>
        MoveEndpoint(endpoint, unit, count);

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not EditorTextRange other)
            return;
        var value = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other.EffectiveEnd();
        if (endpoint == TextPatternRangeEndpoint.Start)
            _start = value;
        else
            _end = value;
        if (_end < _start && _end != int.MaxValue)
            _end = _start;
    }

    public void RemoveFromSelection()
    {
        _end = _start;
        _peer.Pattern?.SelectRange(_start, _start);
    }

    public void ScrollIntoView(bool alignToTop) => _peer.ScrollTo(alignToTop ? _start : EffectiveEnd());

    public void Select()
    {
        var end = EffectiveEnd();
        _peer.Pattern?.SelectRange(_start, end);
        if (_peer.Owner is EditorSurface surface)
            surface.InvalidateView();
    }

    private int MoveEndpoint(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var current = endpoint == TextPatternRangeEndpoint.Start ? _start : EffectiveEnd();
        var direction = Math.Sign(count);
        var moved = 0;
        for (var step = 0; step < Math.Abs(count); step++)
        {
            var next = MoveIndex(current, unit, direction);
            if (next == current)
                break;
            current = next;
            moved += direction;
        }
        if (endpoint == TextPatternRangeEndpoint.Start)
            _start = current;
        else
            _end = current;
        return moved;
    }

    private int MoveIndex(int index, TextUnit unit, int count)
    {
        var model = _peer.Pattern;
        if (model is null || count == 0)
            return index;
        try
        {
            return model.Move(index, ToPattern(unit), count);
        }
        catch (NotSupportedException)
        {
            return index;
        }
    }

    private int EffectiveEnd()
    {
        if (_end != int.MaxValue)
            return _end;
        var model = _peer.Pattern;
        if (model is null)
            return _start;
        if (model.Session.Document.ByteLength > TextDocument.MaterializeLimit)
            return _start;
        return model.DocumentRange.End;
    }

    private static PatternUnit ToPattern(TextUnit unit) => unit switch
    {
        TextUnit.Word => PatternUnit.Word,
        TextUnit.Line or TextUnit.Paragraph => PatternUnit.Line,
        TextUnit.Document or TextUnit.Page => PatternUnit.Document,
        _ => PatternUnit.Character
    };
}

internal static class UiaReserved
{
    private static object? _value;

    public static object NotSupported
    {
        get
        {
            if (_value is not null)
                return _value;
            try
            {
                if (UiaGetReservedNotSupportedValue(out var pointer) == 0 && pointer != IntPtr.Zero)
                {
                    _value = Marshal.GetObjectForIUnknown(pointer);
                    return _value;
                }
            }
            catch (Exception)
            {
            }
            _value = new object();
            return _value;
        }
    }

    [DllImport("UIAutomationCore.dll")]
    private static extern int UiaGetReservedNotSupportedValue(out IntPtr notSupportedValue);
}
