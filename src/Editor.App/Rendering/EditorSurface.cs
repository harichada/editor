using Editor.Core;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Editor.App.Rendering;

public sealed partial class EditorSurface : UserControl
{
    private const double Pad = 12;

    private EditingSession? _session;
    private TextInputController? _input;
    private CanvasTextFormat? _format;
    private DispatcherTimer? _caretTimer;
    private bool _caretOn = true;
    private bool _scrollInternal;
    private bool _dragging;
    private bool _firstFrame;
    private int _scrollLine;
    private int _scrollColumn;
    private float _charWidth = 9;
    private float _lineHeight = 20;
    private int _visibleLines = 1;
    private int _visibleColumns = 80;
    private int _clicks;
    private long _lastClickTicks;
    private double _lastClickX;
    private double _lastClickY;

    public EditorSurface()
    {
        InitializeComponent();
        _format = new CanvasTextFormat
        {
            FontFamily = "Cascadia Mono, Consolas, Courier New",
            FontSize = 16,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            Canvas.Invalidate();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) =>
        {
            UpdateScrollBars();
            Canvas.Invalidate();
        };
        ActualThemeChanged += (_, _) => Canvas.Invalidate();
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        GotFocus += (_, _) => _input?.FocusEntered();
        LostFocus += (_, _) => _input?.FocusLeft();
        Canvas.PointerPressed += OnPointerPressed;
        Canvas.PointerMoved += OnPointerMoved;
        Canvas.PointerReleased += OnPointerReleased;
        Canvas.PointerWheelChanged += OnWheel;
        IsTabStop = true;
    }

    public event EventHandler? DocumentChanged;
    public event EventHandler? FirstFrameRendered;

    public EditingSession? Session => _session;
    public int FirstVisibleLine => _scrollLine;
    public int VisibleLineCount => Math.Max(1, _visibleLines);
    public bool InputAvailable => _input?.IsAvailable == true;
    public string? InputError => _input?.InitializationError;
    internal TextInputModel? InputModel => _input?.Model;

    public void Attach(EditingSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _input?.Dispose();
        _session = session;
        _input = new TextInputController(this, session);
        _scrollLine = 0;
        _scrollColumn = 0;
        if (IsLoaded)
            _input.Start();
        UpdateScrollBars();
        Canvas.Invalidate();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void InvalidateView()
    {
        UpdateScrollBars();
        EnsureCaretVisible();
        Canvas.Invalidate();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RevealCaret()
    {
        EnsureCaretVisible();
        Canvas.Invalidate();
    }

    public void AfterExternalEdit() => InvalidateView();

    public string GetTextSlice(int start, int length)
    {
        if (_session is null || length == 0)
            return string.Empty;
        var model = new TextPatternModel(_session);
        var take = length < 0 ? 8192 : Math.Min(length, 8192);
        return model.GetText(Math.Max(0, start), take);
    }

    public (Rect Control, Rect Caret) GetScreenBounds()
    {
        var origin = TransformToVisual(null).TransformPoint(new Point(0, 0));
        var control = new Rect(origin.X, origin.Y, ActualWidth, ActualHeight);
        var local = CaretLocalRect();
        return (control, new Rect(origin.X + local.X, origin.Y + local.Y, Math.Max(1, local.Width), Math.Max(1, local.Height)));
    }

    public (int Line, int Column) HitTestLocal(double x, double y)
    {
        if (_session is null)
            return (0, 0);
        var line = _scrollLine + (int)Math.Floor((y - Pad) / Math.Max(1, _lineHeight));
        _session.Document.EnsureLines(line + 1);
        var last = Math.Max(0, _session.Document.CompleteLineCount - 1);
        line = Math.Clamp(line, 0, last);
        var content = SafeLine(line);
        var column = MonospaceLayout.HitTest(content, x - Pad + (_scrollColumn * _charWidth), _charWidth);
        return (line, column);
    }

    public void ScrollLineIntoView(int line)
    {
        if (line < _scrollLine)
            _scrollLine = Math.Max(0, line);
        else if (line >= _scrollLine + _visibleLines)
            _scrollLine = Math.Max(0, line - _visibleLines + 1);
        SyncScrollBars();
        Canvas.Invalidate();
    }

    internal Rect CaretLocalRect()
    {
        if (_session is null)
            return new Rect(Pad, Pad, 1, _lineHeight);
        var content = SafeLine(_session.CaretLine);
        var column = DisplayColumn.Get(content, _session.CaretColumn);
        var x = Pad + ((column - _scrollColumn) * _charWidth);
        var y = Pad + ((_session.CaretLine - _scrollLine) * _lineHeight);
        return new Rect(x, y, Math.Max(1, _charWidth * 0.12), _lineHeight);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EditorAutomationPeer(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _input?.Start();
        _caretTimer?.Start();
        Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _caretTimer?.Stop();
        _input?.Dispose();
        _input = null;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        MeasureFont(sender);
        var dark = ActualTheme == ElementTheme.Dark;
        var background = dark ? Color.FromArgb(255, 28, 27, 31) : Color.FromArgb(255, 255, 251, 255);
        var foreground = dark ? Color.FromArgb(255, 230, 225, 229) : Color.FromArgb(255, 28, 27, 31);
        var selection = dark ? Color.FromArgb(255, 63, 79, 107) : Color.FromArgb(255, 211, 227, 253);
        var caret = Color.FromArgb(255, 196, 85, 42);
        var underline = caret;
        args.DrawingSession.Clear(background);
        if (_session is null || _format is null)
            return;

        _session.PageLineCount = Math.Max(1, _visibleLines);
        var estimated = ViewportLayout.EstimateLineCount(
            _session.Document.ByteLength,
            _session.Document.ScannedBytes,
            _session.Document.CompleteLineCount);
        var plan = ViewportLayout.Plan(
            _scrollLine,
            _visibleLines,
            _session.Document.CompleteLineCount,
            _session.Document.IsLineIndexComplete,
            estimated);
        _session.Document.EnsureLines(plan.FirstLine + plan.LineCount + 1);
        var available = _session.Document.CompleteLineCount;
        var first = plan.FirstLine;

        for (var row = 0; row < plan.LineCount; row++)
        {
            var line = first + row;
            if (line >= available)
                break;
            var content = SafeLine(line);
            var y = (float)(Pad + (row * _lineHeight));
            DrawSelection(args, line, content, y, selection);
            foreach (var cell in MonospaceLayout.VisibleCells(content, _scrollColumn, _visibleColumns + 1))
            {
                var x = (float)(Pad + ((cell.Column - _scrollColumn) * _charWidth));
                args.DrawingSession.DrawText(cell.Text, x, y, foreground, _format);
            }
            DrawComposition(args, line, content, y, underline);
        }

        if (_caretOn && IsKeyboardFocusWithin)
        {
            var local = CaretLocalRect();
            if (local.Y >= Pad - 1 && local.Y < sender.ActualHeight)
                args.DrawingSession.FillRectangle((float)local.X, (float)local.Y, (float)local.Width, (float)_lineHeight, caret);
        }

        if (!_firstFrame)
        {
            _firstFrame = true;
            FirstFrameRendered?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DrawSelection(CanvasDrawEventArgs args, int line, string content, float y, Color color)
    {
        if (_session is null || !_session.HasSelection)
            return;
        var (start, end) = _session.LineSelection(line);
        if (end <= start)
            return;
        var left = DisplayColumn.Get(content, start);
        var right = DisplayColumn.Get(content, end);
        var x = (float)(Pad + ((left - _scrollColumn) * _charWidth));
        var width = (float)((right - left) * _charWidth);
        if (width > 0)
            args.DrawingSession.FillRectangle(x, y, width, _lineHeight, color);
    }

    private void DrawComposition(CanvasDrawEventArgs args, int line, string content, float y, Color color)
    {
        var model = _input?.Model;
        if (model is null || !model.IsComposing || model.CompositionLength <= 0 || _session is null)
            return;
        var lineStart = DocumentCoordinates.ToUtf16(_session.Document, line, 0);
        var lineEnd = lineStart + content.Length;
        var compStart = model.CompositionStart;
        var compEnd = compStart + model.CompositionLength;
        var from = Math.Max(lineStart, compStart);
        var to = Math.Min(lineEnd, compEnd);
        if (to <= from)
            return;
        var left = DisplayColumn.Get(content, from - lineStart);
        var right = DisplayColumn.Get(content, to - lineStart);
        var x1 = (float)(Pad + ((left - _scrollColumn) * _charWidth));
        var x2 = (float)(Pad + ((right - _scrollColumn) * _charWidth));
        var baseline = y + _lineHeight - 2;
        args.DrawingSession.DrawLine(x1, baseline, x2, baseline, color, 1.5f);
    }

    private void MeasureFont(CanvasControl sender)
    {
        if (_format is null)
            return;
        using var layout = new CanvasTextLayout(sender, "0", _format, 400, 200);
        _charWidth = (float)Math.Max(1, layout.LayoutBounds.Width);
        _lineHeight = (float)Math.Max(1, layout.LayoutBounds.Height + 2);
        var height = Math.Max(1, sender.ActualHeight - (Pad * 2));
        var width = Math.Max(1, sender.ActualWidth - (Pad * 2));
        _visibleLines = Math.Max(1, (int)(height / _lineHeight));
        _visibleColumns = Math.Max(1, (int)(width / _charWidth));
        if (_session is not null)
            _session.PageLineCount = _visibleLines;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is null)
            return;
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.Left:
                _session.Move(ctrl ? CaretMove.WordLeft : CaretMove.Left, shift);
                break;
            case VirtualKey.Right:
                _session.Move(ctrl ? CaretMove.WordRight : CaretMove.Right, shift);
                break;
            case VirtualKey.Up:
                _session.Move(CaretMove.Up, shift);
                break;
            case VirtualKey.Down:
                _session.Move(CaretMove.Down, shift);
                break;
            case VirtualKey.Home:
                _session.Move(ctrl ? CaretMove.DocumentStart : CaretMove.LineStart, shift);
                break;
            case VirtualKey.End:
                _session.Move(ctrl ? CaretMove.DocumentEnd : CaretMove.LineEnd, shift);
                break;
            case VirtualKey.PageUp:
                _session.Move(CaretMove.PageUp, shift);
                break;
            case VirtualKey.PageDown:
                _session.Move(CaretMove.PageDown, shift);
                break;
            case VirtualKey.Back:
                _session.Backspace();
                break;
            case VirtualKey.Delete:
                _session.DeleteForward();
                break;
            case VirtualKey.Enter:
                _session.InsertNewLine();
                break;
            case VirtualKey.Tab:
                _session.InsertText("\t");
                break;
            case VirtualKey.A when ctrl:
                _session.SelectAll();
                break;
            case VirtualKey.Z when ctrl:
                _session.Undo();
                break;
            case VirtualKey.Y when ctrl:
                _session.Redo();
                break;
            case VirtualKey.C when ctrl:
                CopySelection();
                break;
            case VirtualKey.X when ctrl:
                CopySelection();
                _session.DeleteSelection();
                break;
            case VirtualKey.V when ctrl:
                _ = PasteAsync();
                break;
            default:
                handled = false;
                break;
        }

        if (!handled)
            return;
        e.Handled = true;
        _caretOn = true;
        var edited = e.Key is VirtualKey.Back or VirtualKey.Delete or VirtualKey.Enter or VirtualKey.Tab
            || (ctrl && e.Key is VirtualKey.V or VirtualKey.X or VirtualKey.Z or VirtualKey.Y);
        if (edited)
            _input?.NotifyExternalEdit();
        else
            _input?.NotifySelection();
        InvalidateView();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_session is null || _input?.IsAvailable == true)
            return;
        if (args.Character < ' ' && args.Character != '\t')
            return;
        _session.InsertText(args.Character.ToString());
        args.Handled = true;
        InvalidateView();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        if (_session is null)
            return;
        var point = e.GetCurrentPoint(Canvas);
        var (line, column) = HitTestLocal(point.Position.X, point.Position.Y);
        var now = Environment.TickCount64;
        var near = Math.Abs(point.Position.X - _lastClickX) < 4 && Math.Abs(point.Position.Y - _lastClickY) < 4;
        _clicks = near && now - _lastClickTicks < 400 ? _clicks + 1 : 1;
        _lastClickTicks = now;
        _lastClickX = point.Position.X;
        _lastClickY = point.Position.Y;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (_clicks >= 3)
            SelectLine(line);
        else if (_clicks == 2)
            SelectWord(line, column);
        else
            _session.SetCaret(line, column, shift);
        _dragging = _clicks == 1;
        Canvas.CapturePointer(e.Pointer);
        e.Handled = true;
        _caretOn = true;
        InvalidateView();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _session is null || !e.GetCurrentPoint(Canvas).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetCurrentPoint(Canvas);
        var (line, column) = HitTestLocal(point.Position.X, point.Position.Y);
        _session.SetCaret(line, column, extend: true);
        EnsureCaretVisible();
        Canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Canvas).Properties.MouseWheelDelta;
        var steps = delta > 0 ? -3 : 3;
        _scrollLine = Math.Max(0, _scrollLine + steps);
        SyncScrollBars();
        Canvas.Invalidate();
        e.Handled = true;
    }

    private void SelectWord(int line, int column)
    {
        if (_session is null)
            return;
        var content = SafeLine(line);
        var index = Math.Clamp(column, 0, content.Length);
        if (index < content.Length && !GraphemeNavigator.IsWord(content, index) && index > 0)
            index = GraphemeNavigator.Previous(content, index);
        var start = index;
        while (start > 0 && GraphemeNavigator.IsWord(content, GraphemeNavigator.Previous(content, start)))
            start = GraphemeNavigator.Previous(content, start);
        var end = index;
        while (end < content.Length && GraphemeNavigator.IsWord(content, end))
            end = GraphemeNavigator.Next(content, end);
        if (end == start && end < content.Length)
            end = GraphemeNavigator.Next(content, end);
        _session.SetCaret(line, start, extend: false);
        _session.SetCaret(line, end, extend: true);
    }

    private void SelectLine(int line)
    {
        if (_session is null)
            return;
        var content = SafeLine(line);
        _session.SetCaret(line, 0, extend: false);
        _session.SetCaret(line, content.Length, extend: true);
    }

    private async Task PasteAsync()
    {
        if (_session is null)
            return;
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Text))
            return;
        var text = await view.GetTextAsync();
        _session.InsertText(text);
        _input?.NotifyExternalEdit();
        InvalidateView();
    }

    private void CopySelection()
    {
        if (_session is null || !_session.HasSelection)
            return;
        var package = new DataPackage();
        package.SetText(_session.GetSelectedText());
        Clipboard.SetContent(package);
    }

    private void EnsureCaretVisible()
    {
        if (_session is null)
            return;
        if (_session.CaretLine < _scrollLine)
            _scrollLine = _session.CaretLine;
        else if (_session.CaretLine >= _scrollLine + Math.Max(1, _visibleLines))
            _scrollLine = _session.CaretLine - Math.Max(1, _visibleLines) + 1;

        var content = SafeLine(_session.CaretLine);
        var column = DisplayColumn.Get(content, _session.CaretColumn);
        if (column < _scrollColumn)
            _scrollColumn = column;
        else if (column >= _scrollColumn + _visibleColumns)
            _scrollColumn = Math.Max(0, column - _visibleColumns + 1);
        SyncScrollBars();
    }

    private void UpdateScrollBars()
    {
        if (_session is null)
            return;
        var estimated = ViewportLayout.EstimateLineCount(
            _session.Document.ByteLength,
            _session.Document.ScannedBytes,
            _session.Document.CompleteLineCount);
        var total = _session.Document.IsLineIndexComplete
            ? Math.Max(1, _session.Document.CompleteLineCount)
            : estimated;
        _scrollInternal = true;
        VerticalBar.Maximum = Math.Max(0, total - 1);
        VerticalBar.ViewportSize = Math.Max(1, _visibleLines);
        VerticalBar.LargeChange = Math.Max(1, _visibleLines);
        VerticalBar.SmallChange = 1;
        VerticalBar.Value = Math.Min(VerticalBar.Maximum, _scrollLine);
        var maxColumn = _scrollColumn + _visibleColumns;
        var rows = Math.Min(Math.Max(1, _visibleLines), Math.Max(1, _session.Document.CompleteLineCount));
        for (var row = 0; row < rows; row++)
        {
            var sample = SafeLine(_scrollLine + row);
            if (sample.Length > 2000)
                sample = sample[..2000];
            maxColumn = Math.Max(maxColumn, DisplayColumn.Count(sample));
        }
        HorizontalBar.Maximum = Math.Max(0, maxColumn);
        HorizontalBar.ViewportSize = Math.Max(1, _visibleColumns);
        HorizontalBar.LargeChange = Math.Max(1, _visibleColumns);
        HorizontalBar.SmallChange = 1;
        HorizontalBar.Value = Math.Min(HorizontalBar.Maximum, _scrollColumn);
        _scrollInternal = false;
    }

    private void SyncScrollBars() => UpdateScrollBars();

    private void OnVerticalScroll(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_scrollInternal)
            return;
        _scrollLine = Math.Max(0, (int)e.NewValue);
        Canvas.Invalidate();
    }

    private void OnHorizontalScroll(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_scrollInternal)
            return;
        _scrollColumn = Math.Max(0, (int)e.NewValue);
        Canvas.Invalidate();
    }

    private string SafeLine(int line)
    {
        if (_session is null)
            return string.Empty;
        _session.Document.EnsureLines(line + 1);
        if (line < 0 || line >= _session.Document.CompleteLineCount)
            return string.Empty;
        return _session.Document.GetLineContent(line);
    }
}
