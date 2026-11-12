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
    private CancellationTokenSource? _findCancel;
    private int _findGeneration;
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
        Surface.DocumentChanged += (_, _) =>
        {
            _findCancel?.Cancel();
            UpdateChrome();
        };
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
        var current = status.Value;
        LineColumnText.Text = $"Line {current.Line}, Column {current.Column}";
        EncodingText.Text = current.Encoding;
        EndingButton.Content = current.LineEnding;
        ModifiedText.Text = current.Modified ? "Modified" : string.Empty;
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
        _findCancel?.Cancel();
        FindStatus.Text = string.Empty;
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

    private void OnFindNext(object sender, RoutedEventArgs e) => _ = FindNextAsync();

    private async Task FindNextAsync()
    {
        if (!_settings.Features.FindReplace)
            return;
        var pattern = FindBox.Text ?? string.Empty;
        if (pattern.Length == 0)
        {
            FindStatus.Text = string.Empty;
            return;
        }

        var generation = ++_findGeneration;
        var token = BeginFind();
        var options = new FindOptions(MatchCaseBox.IsChecked == true);
        var from = _session.CaretUtf16;
        var document = _session.Document;
        FindStatus.Text = "Searching…";
        try
        {
            var match = await Task.Run(() => document.FindNext(pattern, options, from, token), token);
            if (generation != _findGeneration || token.IsCancellationRequested)
                return;
            if (match is null)
            {
                FindStatus.Text = "No matches";
                return;
            }

            FindStatus.Text = string.Empty;
            _session.PlaceUtf16(match.Value.Start, extend: false);
            _session.PlaceUtf16(match.Value.Start + match.Value.Length, extend: true);
            Surface.RevealCaret();
            UpdateChrome();
        }
        catch (OperationCanceledException)
        {
            if (generation == _findGeneration)
                FindStatus.Text = "Search cancelled";
        }
    }

    private void OnReplaceOne(object sender, RoutedEventArgs e)
    {
        if (!_settings.Features.FindReplace)
            return;
        var pattern = FindBox.Text ?? string.Empty;
        var comparison = MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (pattern.Length > 0 && _session.GetSelectedText().Equals(pattern, comparison))
        {
            _session.InsertText(ReplaceBox.Text ?? string.Empty);
            Surface.InvalidateView();
        }
        _ = FindNextAsync();
    }

    private async void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        if (!_settings.Features.FindReplace)
            return;
        var pattern = FindBox.Text ?? string.Empty;
        if (pattern.Length == 0)
        {
            FindStatus.Text = string.Empty;
            return;
        }

        var generation = ++_findGeneration;
        var token = BeginFind();
        var options = new FindOptions(MatchCaseBox.IsChecked == true);
        var replacement = ReplaceBox.Text ?? string.Empty;
        var document = _session.Document;
        FindStatus.Text = "Replacing…";
        try
        {
            var count = await Task.Run(() => document.ReplaceAll(pattern, replacement, options, token), token);
            if (generation != _findGeneration || token.IsCancellationRequested)
                return;
            FindStatus.Text = count == 0 ? "No matches" : $"Replaced {count}";
            Surface.InvalidateView();
        }
        catch (OperationCanceledException)
        {
            if (generation == _findGeneration)
                FindStatus.Text = "Search cancelled";
        }
    }

    private CancellationToken BeginFind()
    {
        _findCancel?.Cancel();
        _findCancel?.Dispose();
        _findCancel = new CancellationTokenSource();
        return _findCancel.Token;
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
        if (_session.GoToLine((int)box.Value))
        {
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
        SaveFormat? format = null;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            format = await PickSaveFormatAsync();
            if (format is null)
                return false;
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
            if (format is { } chosen)
            {
                var endings = _session.Document.LineEndings;
                if (endings.IsMixed || endings.Preferred != chosen.Ending)
                    _session.Document.ConvertLineEndings(chosen.Ending);
                _session.Document.SetEncoding(chosen.Kind, chosen.Bom);
            }
            _session.Document.Save(path!);
            Surface.InvalidateView();
            return true;
        }
        catch (Exception ex)
        {
            await AlertAsync(ex.Message);
            return false;
        }
    }

    private readonly record struct SaveFormat(TextEncodingKind Kind, bool Bom, LineEndingKind Ending);

    private async Task<SaveFormat?> PickSaveFormatAsync()
    {
        var encoding = new ComboBox { MinWidth = 240 };
        encoding.Items.Add("UTF-8");
        encoding.Items.Add("UTF-8 with BOM");
        encoding.Items.Add("UTF-16 LE");
        encoding.Items.Add("UTF-16 BE");
        encoding.Items.Add($"ANSI ({_session.Document.Encoding.AnsiCodePage})");
        encoding.SelectedIndex = _session.Document.Encoding.Kind switch
        {
            TextEncodingKind.Utf16Le => 2,
            TextEncodingKind.Utf16Be => 3,
            TextEncodingKind.Ansi => 4,
            _ => _session.Document.Encoding.HasBom ? 1 : 0
        };

        var endings = new ComboBox { MinWidth = 240 };
        endings.Items.Add("CRLF");
        endings.Items.Add("LF");
        endings.Items.Add("CR");
        endings.SelectedIndex = _session.Document.LineEndings.Preferred switch
        {
            LineEndingKind.Lf => 1,
            LineEndingKind.Cr => 2,
            _ => 0
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Encoding" });
        panel.Children.Add(encoding);
        panel.Children.Add(new TextBlock { Text = "Line ending" });
        panel.Children.Add(endings);
        var dialog = new ContentDialog
        {
            Title = "Save as",
            Content = panel,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var (kind, bom) = encoding.SelectedIndex switch
        {
            1 => (TextEncodingKind.Utf8, true),
            2 => (TextEncodingKind.Utf16Le, true),
            3 => (TextEncodingKind.Utf16Be, true),
            4 => (TextEncodingKind.Ansi, false),
            _ => (TextEncodingKind.Utf8, false)
        };
        var ending = endings.SelectedIndex switch
        {
            1 => LineEndingKind.Lf,
            2 => LineEndingKind.Cr,
            _ => LineEndingKind.Crlf
        };
        return new SaveFormat(kind, bom, ending);
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
