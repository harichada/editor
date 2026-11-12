using Editor.Core;
using Windows.UI.Text.Core;

namespace Editor.App.Rendering;

/// <summary>
/// Bridges <see cref="CoreTextEditContext"/> to <see cref="TextInputModel"/>.
/// Keyboard text, IME composition, and the emoji panel all arrive as text updates.
/// </summary>
internal sealed class TextInputController : IDisposable
{
    private readonly EditorSurface _surface;
    private readonly TextInputModel _model;
    private CoreTextEditContext? _context;
    private bool _focused;

    public TextInputController(EditorSurface surface, EditingSession session)
    {
        _surface = surface;
        _model = new TextInputModel(session);
    }

    public TextInputModel Model => _model;
    public string? InitializationError { get; private set; }
    public bool IsAvailable => _context is not null;

    public void Start()
    {
        if (_context is not null)
            return;
        try
        {
            var manager = CoreTextServicesManager.GetForCurrentView();
            _context = manager.CreateEditContext();
            _context.InputScope = CoreTextInputScope.Text;
            _context.TextRequested += OnTextRequested;
            _context.SelectionRequested += OnSelectionRequested;
            _context.TextUpdating += OnTextUpdating;
            _context.SelectionUpdating += OnSelectionUpdating;
            _context.CompositionStarted += OnCompositionStarted;
            _context.CompositionCompleted += OnCompositionCompleted;
            _context.LayoutRequested += OnLayoutRequested;
            _context.FocusRemoved += OnFocusRemoved;
        }
        catch (Exception ex)
        {
            InitializationError = ex.Message;
            _context = null;
        }
    }

    public void FocusEntered()
    {
        if (_context is null || _focused)
            return;
        _focused = true;
        _context.NotifyFocusEnter();
    }

    public void FocusLeft()
    {
        if (_context is null || !_focused)
            return;
        _focused = false;
        _context.NotifyFocusLeave();
    }

    public void NotifySelection()
    {
        if (_context is null || _surface.Session is null)
            return;
        var caret = _surface.Session.CaretUtf16;
        _context.NotifySelectionChanged(new CoreTextRange(caret, caret));
    }

    public void NotifyExternalEdit()
    {
        if (_context is null || _surface.Session is null)
            return;
        var caret = _surface.Session.CaretUtf16;
        _context.NotifyTextChanged(new CoreTextRange(0, caret), caret, new CoreTextRange(caret, caret));
        _context.NotifySelectionChanged(new CoreTextRange(caret, caret));
    }

    public void Dispose()
    {
        FocusLeft();
        _context = null;
    }

    private void OnTextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        var range = args.Request.Range;
        var length = range.EndCaretPosition - range.StartCaretPosition;
        args.Request.Text = _surface.GetTextSlice(range.StartCaretPosition, length);
    }

    private void OnSelectionRequested(CoreTextEditContext sender, CoreTextSelectionRequestedEventArgs args)
    {
        var caret = _surface.Session?.CaretUtf16 ?? 0;
        args.Request.Selection = new CoreTextRange(caret, caret);
    }

    private void OnTextUpdating(CoreTextEditContext sender, CoreTextTextUpdatingEventArgs args)
    {
        // Enter, Backspace, and Delete are owned by the surface key handler.
        // Swallowing those control strings here avoids inserting them twice.
        if (!_model.IsComposing && IsEditingKey(args.Text))
        {
            args.Result = CoreTextTextUpdatingResult.Succeeded;
            return;
        }

        _model.ApplyTextUpdate(args.Range.StartCaretPosition, args.Range.EndCaretPosition, args.Text);
        args.Result = CoreTextTextUpdatingResult.Succeeded;
        _surface.AfterExternalEdit();
    }

    private static bool IsEditingKey(string text) =>
        text is "\r" or "\n" or "\r\n" or "\b";

    private void OnSelectionUpdating(CoreTextEditContext sender, CoreTextSelectionUpdatingEventArgs args)
    {
        _surface.Session?.PlaceUtf16(args.Selection.StartCaretPosition, extend: false);
        if (args.Selection.EndCaretPosition != args.Selection.StartCaretPosition)
            _surface.Session?.PlaceUtf16(args.Selection.EndCaretPosition, extend: true);
        args.Result = CoreTextSelectionUpdatingResult.Succeeded;
        _surface.InvalidateView();
    }

    private void OnCompositionStarted(CoreTextEditContext sender, CoreTextCompositionStartedEventArgs args) =>
        _model.BeginComposition();

    private void OnCompositionCompleted(CoreTextEditContext sender, CoreTextCompositionCompletedEventArgs args) =>
        _model.CompleteComposition();

    private void OnLayoutRequested(CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        var bounds = _surface.GetScreenBounds();
        args.Request.LayoutBounds.ControlBounds = bounds.Control;
        args.Request.LayoutBounds.TextBounds = bounds.Caret;
    }

    private void OnFocusRemoved(CoreTextEditContext sender, object args) => _focused = false;
}
