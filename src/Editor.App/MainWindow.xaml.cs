using System.Globalization;
using Editor.App.Services;
using Editor.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Editor.App;

public sealed partial class MainWindow : Window
{
    private readonly EditorSettings _settings;
    private readonly string? _benchmarkPath;
    private readonly DispatcherTimer? _benchmarkFallback;
    private EditingSession _session;
    private bool _allowClose;
    private bool _closing;
    private bool _benchmarkDone;

    public MainWindow(string? benchmarkPath)
    {
        InitializeComponent();
        _benchmarkPath = benchmarkPath;
        _settings = AppSettingsStore.Load();
        _session = new EditingSession(TextDocument.CreateNew(_settings));
        Title = "Editor";
        AppWindow.Resize(new SizeInt32(1100, 720));
        TryUseMica();
        BuildMenu();
        ApplyFeatureVisibility();
        Surface.Attach(_session);
        Surface.DocumentChanged += (_, _) => UpdateChrome();
        Surface.FirstFrameRendered += (_, _) => FinishBenchmark("first-frame");
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => _session.Document.Dispose();
        UpdateChrome();

        if (benchmarkPath is not null)
        {
            _benchmarkFallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _benchmarkFallback.Tick += (_, _) => FinishBenchmark("draw-timeout");
            _benchmarkFallback.Start();
        }
    }

    private void TryUseMica()
    {
        try
        {
            if (MicaController.IsSupported())
                SystemBackdrop = new MicaBackdrop();
        }
        catch (Exception)
        {
        }
    }

    private void ApplyFeatureVisibility()
    {
        FindBar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = _settings.Features.StatusBar ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildMenu()
    {
        Menu.Items.Clear();
        var file = new MenuBarItem { Title = "File" };
        file.Items.Add(Command("New", VirtualKey.N, OnNew));
        file.Items.Add(Command("Open", VirtualKey.O, OnOpen));
        file.Items.Add(Command("Save", VirtualKey.S, OnSave));
        var saveAs = Command("Save as", VirtualKey.None, OnSaveAs);
        saveAs.KeyboardAccelerators.Add(Accelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift));
        file.Items.Add(saveAs);
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(Command("Exit", VirtualKey.None, OnExit));
        Menu.Items.Add(file);

        var edit = new MenuBarItem { Title = "Edit" };
        edit.Items.Add(Command("Undo", VirtualKey.Z, OnUndo));
        edit.Items.Add(Command("Redo", VirtualKey.Y, OnRedo));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Command("Cut", VirtualKey.X, OnCut));
        edit.Items.Add(Command("Copy", VirtualKey.C, OnCopy));
        edit.Items.Add(Command("Paste", VirtualKey.V, OnPaste));
        edit.Items.Add(Command("Select all", VirtualKey.A, OnSelectAll));
        if (_settings.Features.FindReplace)
        {
            edit.Items.Add(new MenuFlyoutSeparator());
            edit.Items.Add(Command("Find", VirtualKey.F, OnShowFind));
        }
        if (_settings.Features.GoToLine)
            edit.Items.Add(Command("Go to line", VirtualKey.G, OnGoTo));
        Menu.Items.Add(edit);
    }

    private static MenuFlyoutItem Command(string text, VirtualKey key, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += handler;
        if (key != VirtualKey.None)
            item.KeyboardAccelerators.Add(Accelerator(key, VirtualKeyModifiers.Control));
        return item;
    }

    private static KeyboardAccelerator Accelerator(VirtualKey key, VirtualKeyModifiers modifiers) =>
        new() { Key = key, Modifiers = modifiers };

    private void UpdateChrome()
    {
        var name = _session.Document.DisplayName;
        Title = (_session.Document.IsModified ? "*" : string.Empty) + name + " — Editor";
        if (!_settings.Features.StatusBar)
        {
            StatusBar.Visibility = Visibility.Collapsed;
            return;
        }
        StatusBar.Visibility = Visibility.Visible;
        var status = _session.Document.TryGetStatus(_session.CaretLine, _session.CaretColumn);
        if (status is null)
            return;
        LineColumnText.Text = $"Line {status.Line}, Column {status.Column}";
        EncodingText.Text = status.Encoding;
        EndingButton.Content = status.LineEnding;
        ModifiedText.Text = status.Modified ? "Modified" : string.Empty;
    }

    private async void OnNew(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync())
            return;
        Replace(TextDocument.CreateNew(_settings));
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync())
            return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        if (string.IsNullOrEmpty(file.Path))
        {
            await AlertAsync("That location is not a file the editor can open directly.");
            return;
        }
        try
        {
            Replace(TextDocument.Open(file.Path, _settings));
        }
        catch (Exception ex)
        {
            await AlertAsync(ex.Message);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: _session.Document.FilePath is null);

    private async void OnSaveAs(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    private async void OnExit(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync())
            return;
        _allowClose = true;
        Close();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        _session.Undo();
        Surface.InvalidateView();
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        _session.Redo();
        Surface.InvalidateView();
    }

    private void OnCut(object sender, RoutedEventArgs e)
    {
        Copy();
        _session.DeleteSelection();
        Surface.InvalidateView();
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();

    private async void OnPaste(object sender, RoutedEventArgs e)
    {
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Text))
            return;
        _session.InsertText(await view.GetTextAsync());
        Surface.InvalidateView();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        _session.SelectAll();
        Surface.InvalidateView();
    }

    private void OnShowFind(object sender, RoutedEventArgs e)
    {
        if (!_settings.Features.FindReplace)
            return;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
    }

    private void OnCloseFind(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        Surface.Focus(FocusState.Programmatic);
    }

    private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            OnCloseFind(sender, e);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            OnFindNext(sender, e);
            e.Handled = true;
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e)
    {
        if (!_settings.Features.FindReplace)
            return;
        var pattern = FindBox.Text ?? string.Empty;
        if (pattern.Length == 0)
            return;
        var matches = _session.Document.Find(pattern, new FindOptions(MatchCaseBox.IsChecked == true));
        if (matches.Count == 0)
            return;
        var caret = _session.CaretUtf16;
        var chosen = matches[0];
        foreach (var match in matches)
        {
            if (match.Start >= caret && !(match.Start == caret && _session.HasSelection))
            {
                chosen = match;
                break;
            }
        }
        _session.PlaceUtf16(chosen.Start, extend: false);
        _session.PlaceUtf16(chosen.Start + chosen.Length, extend: true);
        Surface.RevealCaret();
        UpdateChrome();
    }

    private void OnReplaceOne(object sender, RoutedEventArgs e)
    {
        if (!_settings.Features.FindReplace)
            return;
        var pattern = FindBox.Text ?? string.Empty;
        var comparison = MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (pattern.Length > 0 && _session.GetSelectedText().Equals(pattern, comparison))
            _session.InsertText(ReplaceBox.Text ?? string.Empty);
        OnFindNext(sender, e);
        Surface.InvalidateView();
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        if (!_settings.Features.FindReplace)
            return;
        var pattern = FindBox.Text ?? string.Empty;
        if (pattern.Length == 0)
            return;
        _session.Document.ReplaceAll(pattern, ReplaceBox.Text ?? string.Empty, new FindOptions(MatchCaseBox.IsChecked == true));
        Surface.InvalidateView();
    }

    private async void OnGoTo(object sender, RoutedEventArgs e)
    {
        if (!_settings.Features.GoToLine)
            return;
        var box = new NumberBox
        {
            Header = "Line number",
            Minimum = 1,
            Maximum = 1_000_000_000,
            Value = _session.CaretLine + 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var dialog = new ContentDialog
        {
            Title = "Go to line",
            Content = box,
            PrimaryButtonText = "Go",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (_session.Document.TryGoToLine((int)box.Value, out var line))
        {
            _session.SetCaret(line, 0, extend: false);
            Surface.RevealCaret();
            UpdateChrome();
        }
    }

    private void OnEndingClick(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        AddEnding(flyout, LineEndingKind.Crlf, "CRLF");
        AddEnding(flyout, LineEndingKind.Lf, "LF");
        AddEnding(flyout, LineEndingKind.Cr, "CR");
        flyout.ShowAt(EndingButton);
    }

    private void AddEnding(MenuFlyout flyout, LineEndingKind kind, string label)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += (_, _) =>
        {
            _session.Document.ConvertLineEndings(kind);
            Surface.InvalidateView();
        };
        flyout.Items.Add(item);
    }

    private async Task<bool> SaveAsync(bool saveAs)
    {
        var path = _session.Document.FilePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("Text", [".txt"]);
            picker.FileTypeChoices.Add("All files", [".*"]);
            picker.SuggestedFileName = _session.Document.FilePath is null
                ? "Untitled"
                : Path.GetFileName(_session.Document.FilePath);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null || string.IsNullOrEmpty(file.Path))
                return false;
            path = file.Path;
        }
        try
        {
            _session.Document.Save(path);
            Surface.InvalidateView();
            return true;
        }
        catch (Exception ex)
        {
            await AlertAsync(ex.Message);
            return false;
        }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_session.Document.IsModified)
            return true;
        var dialog = new ContentDialog
        {
            Title = "Save changes?",
            Content = $"{_session.Document.DisplayName} has unsaved changes.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            XamlRoot = Root.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            return await SaveAsync(saveAs: _session.Document.FilePath is null);
        return result == ContentDialogResult.Secondary;
    }

    private async Task AlertAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Editor",
            Content = message,
            CloseButtonText = "Close",
            XamlRoot = Root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void Copy()
    {
        if (!_session.HasSelection)
            return;
        var package = new DataPackage();
        package.SetText(_session.GetSelectedText());
        Clipboard.SetContent(package);
    }

    private void Replace(TextDocument document)
    {
        var previous = _session.Document;
        _session = new EditingSession(document);
        Surface.Attach(_session);
        previous.Dispose();
        UpdateChrome();
        Surface.Focus(FocusState.Programmatic);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || _benchmarkPath is not null)
            return;
        if (!_session.Document.IsModified)
            return;
        args.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        try
        {
            if (await ConfirmDiscardAsync())
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _closing = false;
        }
    }

    private void FinishBenchmark(string kind)
    {
        if (_benchmarkDone || string.IsNullOrEmpty(_benchmarkPath))
            return;
        _benchmarkDone = true;
        _benchmarkFallback?.Stop();
        try
        {
            var milliseconds = StartupClock.ElapsedMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
            var directory = Path.GetDirectoryName(_benchmarkPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_benchmarkPath, "{\"kind\":\"" + kind + "\",\"elapsedMs\":" + milliseconds + "}");
        }
        catch (Exception)
        {
        }
        _allowClose = true;
        Close();
    }
}
