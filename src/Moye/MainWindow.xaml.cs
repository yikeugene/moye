using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Moye.Controls;
using Moye.Models;
using Moye.Services;
using Moye.ViewModels;

namespace Moye;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly Dictionary<Border, PageEditor> _editors = [];
    private readonly Dictionary<Border, CancellationTokenSource> _loading = [];
    private readonly Dictionary<int, (TouchDevice Device, Point Point)> _touches = [];
    private readonly HashSet<int> _blockedTouches = [];
    private readonly DispatcherTimer _thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _pdfZoomTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly HashSet<PageEditor> _dirtyThumbnails = [];
    private InkTool _tool = InkTool.Pen;
    private InkTool _eraserTool = InkTool.PointEraser;
    private bool _eraserPopupDismissedOnButton;
    private Color _color = (Color)ColorConverter.ConvertFromString("#25334A");
    private Color _penColor = (Color)ColorConverter.ConvertFromString("#25334A");
    private Color _highlighterColor = (Color)ColorConverter.ConvertFromString("#F4CF58");
    private double _width = 2.5;
    private bool _ready, _closing, _suppressPageSelection, _focusMode, _addingPage, _fitWidthActive, _fitWidthPending;
    private WindowState _oldWindowState;
    private bool _sidebarBeforeFocus;
    private ScrollViewer? _scroll;
    private DateTime _ignoreTouchUntil;
    private double _pinchDistance;
    private Point _touchCenter;
    private bool _zoomNavigationActive;
    private int _zoomNavigationRevision;
    private sealed record PageZoomAnchor(PageViewModel Page, Point PagePoint, Point ViewportPoint);
    private bool AnyPenDown => _editors.Values.Any(e => e.IsInputActive);

    public MainWindow(INotebookRepository repository)
    {
        ViewModel = new(repository); InitializeComponent(); DataContext = ViewModel;
        Width = Math.Min(1400, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(960, SystemParameters.WorkArea.Height - 24);
        SystemEvents.PowerModeChanged += PowerModeChanged;
        PageList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(DocumentScrolled));
        Viewport.SizeChanged += (_, _) => ScheduleFitWidth();
        ViewModel.DocumentReplaced += (_, _) => { _scroll = null; SyncPageTemplate(); SelectLibraryCurrent(); };
        ViewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Zoom)) { foreach (var editor in _editors.Values) editor.LayoutTransform = new ScaleTransform(ViewModel.Zoom, ViewModel.Zoom); _pdfZoomTimer.Stop(); _pdfZoomTimer.Start(); } };
        _pdfZoomTimer.Tick += async (_, _) => { _pdfZoomTimer.Stop(); await RefreshVisiblePdfsAsync(); };
        _thumbnailTimer.Tick += (_, _) =>
        {
            _thumbnailTimer.Stop();
            foreach (var editor in _dirtyThumbnails.ToArray())
            {
                if (editor.IsInputActive) continue;
                UpdateThumbnail(editor); _dirtyThumbnails.Remove(editor);
            }
            if (_dirtyThumbnails.Count > 0) _thumbnailTimer.Start();
        };
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await RunAsync("Loading your notebooks…", async () => { await ViewModel.InitializeAsync(); _ready = true; UpdateTool(); });
    }

    private async Task RunAsync(string message, Func<Task> action)
    {
        if (ViewModel.IsBusy) return;
        CloseSettingsPopups();
        ViewModel.Operation = message; ViewModel.IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { ViewModel.Status = "Operation canceled"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to complete the operation", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { ViewModel.IsBusy = false; }
    }

    private void CommitEditors() { foreach (var editor in _editors.Values.ToArray()) editor.CommitPendingEdits(); }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        if (ViewModel.IsBusy) { MessageBox.Show(this, "Please wait for the current operation to finish before closing.", "Moye"); return; }
        try
        {
            CommitEditors(); await ViewModel.Autosave.FlushAsync();
            _closing = true; _thumbnailTimer.Stop(); _pdfZoomTimer.Stop(); SystemEvents.PowerModeChanged -= PowerModeChanged;
            foreach (var cts in _loading.Values) cts.Cancel();
            ViewModel.Dispose(); _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex) { MessageBox.Show(this, "Some changes could not be saved. This window will stay open.\nUse More → Retry Save, or Back Up This Notebook.\n\n" + ex.Message, "Unsaved Changes", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void WindowDeactivated(object? sender, EventArgs e)
    {
        if (!_ready || _closing) return;
        ClearTouches(); CommitEditors();
        try { await ViewModel.Autosave.FlushAsync(); } catch { /* Visible save status retains the error and unsaved snapshots. */ }
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => { if (!_closing) { ClearTouches(); CommitEditors(); } });
    }

    private async void NewNoteClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NotebookDialog(this, folder: ViewModel.Folder);
        if (dialog.ShowDialog() != true) return;
        CommitEditors(); await RunAsync("Creating notebook…", async () => { await ViewModel.CreateAsync(dialog.NotebookTitle, dialog.Folder, dialog.SelectedTemplate); await PrepareNotebookViewAsync(); });
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Document is null) return;
        var dialog = new InputDialog(this, "Rename and Category", ("Name", ViewModel.Title), ("Category", ViewModel.Folder));
        if (dialog.ShowDialog() != true) return;
        CommitEditors(); ViewModel.Rename(dialog.Values[0], dialog.Values[1]); SelectLibraryCurrent();
    }

    private async void NotebookSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ViewModel.IsBusy || NotebookList.SelectedItem is not NotebookSummary summary || summary.Id == ViewModel.Document?.Id) return;
        await OpenNotebookAsync(summary.Id);
        SelectLibraryCurrent();
    }
    private async void OpenNotebookClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: NotebookSummary summary }) await OpenNotebookAsync(summary.Id);
    }
    private async Task OpenNotebookAsync(string id)
    {
        CommitEditors(); ClearTouches();
        await RunAsync("Opening notebook…", async () => { await ViewModel.OpenAsync(id); await PrepareNotebookViewAsync(); });
    }
    private async Task PrepareNotebookViewAsync()
    {
        _fitWidthActive = false;
        SelectLibraryCurrent(); ShowSidebarTab(false); ScrollToSelected();
        await Dispatcher.InvokeAsync(FitPage, DispatcherPriority.Loaded);
        PageList.Focus();
    }
    private void SelectLibraryCurrent() => NotebookList.SelectedItem = ViewModel.Notebooks.FirstOrDefault(n => n.Id == ViewModel.Document?.Id);

    private void ThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncPageTemplate();
        if (_ready && !_suppressPageSelection) ScrollToSelected();
    }
    private void PageSelectionChanged(object sender, SelectionChangedEventArgs e) => SyncPageTemplate();
    private void DocumentScrolled(object sender, ScrollChangedEventArgs e)
    {
        UpdateVisiblePageSelection();
    }
    private void UpdateVisiblePageSelection()
    {
        if (!_ready || ViewModel.IsLibraryVisible || AnyPenDown || ViewModel.IsBusy || _zoomNavigationActive || _suppressPageSelection) return;
        PageViewModel? visible = null; double largest = 0;
        foreach (var host in _editors.Keys)
        {
            if (!host.IsLoaded || host.DataContext is not PageViewModel item) continue;
            double top = host.TranslatePoint(new Point(), Viewport).Y;
            double overlap = Math.Min(Viewport.ActualHeight, top + host.ActualHeight) - Math.Max(0, top);
            if (overlap > largest) { largest = overlap; visible = item; }
        }
        if (visible is not null && visible != ViewModel.SelectedPage)
        {
            _suppressPageSelection = true; ViewModel.SelectedPage = visible; SyncPageTemplate(); _suppressPageSelection = false;
        }
    }
    private void ScrollToSelected() { if (ViewModel.SelectedPage is not null) PageList.ScrollIntoView(ViewModel.SelectedPage); }

    private void PageHostLoaded(object sender, RoutedEventArgs e) => AttachPage((Border)sender);
    private void PageHostDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var host = (Border)sender;
        DetachPage(host); if (host.IsLoaded) AttachPage(host);
    }
    private void PageHostUnloaded(object sender, RoutedEventArgs e) => DetachPage((Border)sender);

    private async void AttachPage(Border host)
    {
        if (host.DataContext is not PageViewModel item || _editors.ContainsKey(host)) return;
        var cts = new CancellationTokenSource(); _loading[host] = cts;
        var editor = new PageEditor(item.Page, id => ViewModel.Repository.GetAssetAsync(id));
        editor.InkCanvas.HoldToStraightenEnabled = HoldToStraightenToggle.IsChecked == true;
        editor.InkCanvas.EditingModeInverted = _eraserTool == InkTool.StrokeEraser ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.EraseByPoint;
        editor.LayoutTransform = new ScaleTransform(ViewModel.Zoom, ViewModel.Zoom);
        editor.HorizontalAlignment = HorizontalAlignment.Left; editor.VerticalAlignment = VerticalAlignment.Top;
        editor.SetTool(_tool, _color, EffectiveWidth());
        editor.ContentChanged += EditorChanged;
        editor.VisualContentChanged += EditorVisualChanged;
        editor.AssetLoadFailed += EditorAssetFailed;
        editor.PenContactChanged += EditorPenContactChanged;
        _editors[host] = editor; host.Child = editor;
        try
        {
            if (item.Page.Pdf is not null)
            {
                var bitmap = await ViewModel.Pdf.RenderAsync(item.Page, Math.Min(2, ViewModel.Zoom * VisualTreeHelper.GetDpi(this).DpiScaleX), cts.Token);
                if (!cts.IsCancellationRequested) editor.SetPdfBackground(bitmap);
            }
            if (!cts.IsCancellationRequested) UpdateThumbnail(editor);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) ViewModel.Status = "Unable to load this page: " + ex.Message; }
    }

    private void DetachPage(Border host)
    {
        if (_loading.Remove(host, out var cts)) { cts.Cancel(); cts.Dispose(); }
        if (_editors.Remove(host, out var editor))
        {
            editor.CommitPendingEdits();
            editor.ContentChanged -= EditorChanged; editor.VisualContentChanged -= EditorVisualChanged; editor.AssetLoadFailed -= EditorAssetFailed; editor.PenContactChanged -= EditorPenContactChanged;
            _dirtyThumbnails.Remove(editor); host.Child = null;
        }
    }
    private void EditorChanged(object? sender, EventArgs e)
    {
        if (sender is not PageEditor editor || ViewModel.Document?.Pages.Contains(editor.Page) != true) return;
        ViewModel.Changed(); _dirtyThumbnails.Add(editor); _thumbnailTimer.Start();
    }
    private void EditorPenContactChanged(object? sender, EventArgs e)
    {
        if (sender is PageEditor { IsPenDown: true }) { _ignoreTouchUntil = DateTime.UtcNow.AddMilliseconds(200); ClearTouches(true); }
        else { _ignoreTouchUntil = DateTime.UtcNow.AddMilliseconds(120); ScheduleFitWidth(); }
    }
    private void EditorVisualChanged(object? sender, EventArgs e) { if (sender is PageEditor editor) { _dirtyThumbnails.Add(editor); _thumbnailTimer.Start(); } }
    private void EditorAssetFailed(object? sender, string message) => ViewModel.Status = "Unable to load image: " + message;
    private void PageHostMouseDown(object sender, MouseButtonEventArgs e) => ActivatePage((Border)sender);
    private void PageHostStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Stylus) ActivatePage((Border)sender);
    }
    private void ActivatePage(Border host)
    {
        if (host.DataContext is not PageViewModel item) return;
        _suppressPageSelection = true; ViewModel.SelectedPage = item; SyncPageTemplate(); _suppressPageSelection = false;
        if (_tool is InkTool.Pen or InkTool.Highlighter or InkTool.PointEraser or InkTool.StrokeEraser or InkTool.Lasso) PageList.Focus();
    }

    private void UpdateThumbnail(PageEditor editor)
    {
        var item = ViewModel.Pages.FirstOrDefault(p => ReferenceEquals(p.Page, editor.Page));
        if (item is not null && editor.IsLoaded && !editor.IsInputActive) item.Thumbnail = DecodeBitmap(editor.CreateThumbnail(180));
    }
    private async void ThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not PageViewModel item || item.Thumbnail is not null) return;
        try
        {
            var editor = new PageEditor(item.Page.Snapshot(), id => ViewModel.Repository.GetAssetAsync(id));
            editor.VisualContentChanged += (_, _) => { if (!_closing && ViewModel.Pages.Contains(item)) item.Thumbnail = DecodeBitmap(editor.CreateThumbnail(180)); };
            if (item.Page.Pdf is not null)
            {
                var image = await ViewModel.Pdf.RenderAsync(item.Page, .2);
                editor.SetPdfBackground(image);
            }
            if (item.Thumbnail is null) item.Thumbnail = DecodeBitmap(editor.CreateThumbnail(180));
        }
        catch { /* Full page reports load failures when opened. */ }
    }

    private async Task RefreshVisiblePdfsAsync()
    {
        var zoom = ViewModel.Zoom;
        foreach (var pair in _editors.ToArray())
        {
            if (_closing || pair.Value.Page.Pdf is null || !_loading.TryGetValue(pair.Key, out var cts)) continue;
            try
            {
                var bitmap = await ViewModel.Pdf.RenderAsync(pair.Value.Page, zoom * VisualTreeHelper.GetDpi(this).DpiScaleX, cts.Token);
                if (!cts.IsCancellationRequested && zoom == ViewModel.Zoom && _editors.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value)) current.SetPdfBackground(bitmap);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closing) ViewModel.Status = "Unable to refresh PDF preview: " + ex.Message; }
        }
    }

    private PageEditor? CurrentEditor => _editors.Values.FirstOrDefault(e => ReferenceEquals(e.Page, ViewModel.SelectedPage?.Page));
    private void ToolClick(object sender, RoutedEventArgs e)
    {
        var tool = Enum.Parse<InkTool>((string)((Button)sender).Tag);
        SetTool(tool == InkTool.PointEraser ? _eraserTool : tool);
    }
    private void EraserToolClick(object sender, RoutedEventArgs e)
    {
        bool close = (_tool is InkTool.PointEraser or InkTool.StrokeEraser) &&
                     (EraserSettingsPopup.IsOpen || _eraserPopupDismissedOnButton);
        SetTool(_eraserTool);
        _eraserPopupDismissedOnButton = false;
        EraserSettingsPopup.IsOpen = !close;
    }
    private void EraserPopupClosed(object? sender, EventArgs e)
    {
        // StaysOpen=False dismisses a popup before its trigger receives Click.
        // Remember that dismissal for this press so re-clicking closes it.
        var stylus = Stylus.CurrentStylusDevice;
        var pointer = stylus is { InAir: false } ? stylus.GetPosition(EraserButton) : Mouse.GetPosition(EraserButton);
        _eraserPopupDismissedOnButton = (Mouse.LeftButton == MouseButtonState.Pressed || stylus is { InAir: false }) &&
            new Rect(EraserButton.RenderSize).Contains(pointer);
    }
    private void EraserPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.None)
        {
            SetTool(_eraserTool); PageList.Focus(); e.Handled = true; return;
        }
        if (e.Key != Key.Escape) return;
        CloseSettingsPopups(); PageList.Focus(); e.Handled = true;
    }
    private void CloseSettingsPopups()
    {
        PenSettingsPopup.IsOpen = false;
        PaperSettingsPopup.IsOpen = false;
        EraserSettingsPopup.IsOpen = false;
        _eraserPopupDismissedOnButton = false;
    }
    private void EraserMenuClick(object sender, RoutedEventArgs e) => SetTool(Enum.Parse<InkTool>((string)((MenuItem)sender).Tag));
    private void EraserOptionClick(object sender, RoutedEventArgs e)
    {
        SetTool(Enum.Parse<InkTool>((string)((Button)sender).Tag));
    }
    private void PenSettingsClick(object sender, RoutedEventArgs e)
    {
        bool open = !PenSettingsPopup.IsOpen;
        CommitEditors(); CloseSettingsPopups();
        PenSettingsPopup.IsOpen = open; UpdateTool();
    }
    private void PageSettingsClick(object sender, RoutedEventArgs e)
    {
        ShowPaperPicker(false);
    }
    private void SetTool(InkTool tool)
    {
        CommitEditors(); CloseSettingsPopups();
        if (_tool == InkTool.Pen) _penColor = _color;
        if (_tool == InkTool.Highlighter) _highlighterColor = _color;
        _tool = tool;
        if (tool == InkTool.Pen) _color = _penColor;
        if (tool == InkTool.Highlighter) _color = _highlighterColor;
        if (tool is InkTool.PointEraser or InkTool.StrokeEraser) _eraserTool = tool;
        UpdateTool();
    }
    private double EffectiveWidth() => _width;
    private void HoldToStraightenChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        CommitEditors();
        foreach (var editor in _editors.Values) editor.InkCanvas.HoldToStraightenEnabled = HoldToStraightenToggle.IsChecked == true;
    }
    private void UpdateTool()
    {
        if (PenButton is null) return;
        foreach (var button in Descendants<Button>(WritingTools))
        {
            if (button.Tag is string tag && Enum.TryParse<InkTool>(tag, out var tool))
            {
                bool selected = tool == _tool || tool == InkTool.PointEraser && _tool == InkTool.StrokeEraser;
                button.Background = selected ? new SolidColorBrush(Color.FromRgb(237, 242, 254)) : Brushes.Transparent;
                button.Foreground = selected ? (Brush)FindResource("Accent") : (Brush)FindResource("Ink");
            }
        }
        // A Popup has its own visual tree; update its swatches explicitly.
        foreach (var button in ColorPalette.Children.OfType<Button>())
        {
            bool selected = (Color)ColorConverter.ConvertFromString((string)button.Tag) == _color;
            button.BorderThickness = new Thickness(selected ? 2 : 0); button.BorderBrush = (Brush)FindResource("Accent");
        }
        CurrentColor.Fill = new SolidColorBrush(_color);
        CurrentWidth.Text = _width.ToString("0.#", CultureInfo.InvariantCulture);
        foreach (var option in new[] { PointEraseOption, StrokeEraseOption })
        {
            bool selected = Enum.Parse<InkTool>((string)option.Tag) == _eraserTool;
            option.Background = selected ? new SolidColorBrush(Color.FromRgb(237, 242, 254)) : Brushes.Transparent;
            option.Foreground = selected ? (Brush)FindResource("Accent") : (Brush)FindResource("Ink");
            option.BorderBrush = selected ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(223, 228, 236));
            option.BorderThickness = new Thickness(1);
        }
        EraserModeLabel.Text = _eraserTool == InkTool.PointEraser ? "Pixel" : "Stroke";
        EraserButton.ToolTip = $"{EraserModeLabel.Text} Eraser (E); click to choose an eraser mode";
        System.Windows.Automation.AutomationProperties.SetHelpText(EraserButton, $"Current mode: {EraserModeLabel.Text} Eraser. Click to choose Pixel Eraser or Stroke Eraser.");
        foreach (var editor in _editors.Values)
        {
            editor.SetTool(_tool, _color, EffectiveWidth());
            editor.InkCanvas.EditingModeInverted = _eraserTool == InkTool.StrokeEraser ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.EraseByPoint;
        }
        ToolStatus.Text = (_tool switch { InkTool.Pen => "Pen", InkTool.Highlighter => "Highlighter", InkTool.PointEraser => "Pixel Eraser · Keep the rest of the stroke", InkTool.StrokeEraser => "Stroke Eraser · Remove the whole stroke", InkTool.Lasso => "Lasso · Draw to select / Ctrl+D to duplicate", InkTool.Text => "Text · Tap the page to type", InkTool.Select => "Select · Drag to move / resize from the corner", _ => "Browse" }) + "  ·  One finger to pan · Pinch to zoom";
    }
    private void ColorClick(object sender, RoutedEventArgs e)
    {
        _color = (Color)ColorConverter.ConvertFromString((string)((Button)sender).Tag);
        if (_tool is InkTool.Lasso or InkTool.Text or InkTool.Select) CurrentEditor?.ApplyColorToSelection(_color);
        UpdateTool();
    }
    private void CustomColorClick(object sender, RoutedEventArgs e)
    {
        CloseSettingsPopups();
        var dialog = new InputDialog(this, "Custom Color", ("Hex color (for example, #326AE8)", $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}"));
        if (dialog.ShowDialog() != true) return;
        try { _color = (Color)ColorConverter.ConvertFromString(dialog.Values[0]); if (_tool is InkTool.Lasso or InkTool.Text or InkTool.Select) CurrentEditor?.ApplyColorToSelection(_color); UpdateTool(); }
        catch { MessageBox.Show(this, "Enter a valid hex color, such as #326AE8.", "Invalid Color"); }
    }
    private void WidthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthPicker?.SelectedItem is ComboBoxItem item) _width = double.Parse((string)item.Tag, CultureInfo.InvariantCulture);
        if (_ready) UpdateTool();
    }
    private void UndoClick(object sender, RoutedEventArgs e) { CommitEditors(); ViewModel.Undo(); }
    private void RedoClick(object sender, RoutedEventArgs e) { CommitEditors(); ViewModel.Redo(); }
    private void AddPageClick(object sender, RoutedEventArgs e) => ShowPaperPicker(true);
    private void DuplicatePageClick(object sender, RoutedEventArgs e) { CommitEditors(); ViewModel.DuplicatePage(); ScrollToSelected(); }
    private void MovePageUpClick(object sender, RoutedEventArgs e) { CommitEditors(); ViewModel.MovePage(-1); ScrollToSelected(); }
    private void MovePageDownClick(object sender, RoutedEventArgs e) { CommitEditors(); ViewModel.MovePage(1); ScrollToSelected(); }
    private void DeletePageClick(object sender, RoutedEventArgs e) { CommitEditors(); ViewModel.DeletePage(); ScrollToSelected(); }
    private void SyncPageTemplate()
    {
        if (TemplatePicker is null) return;
        if (!PaperSettingsPopup.IsOpen) TemplatePicker.SelectedTemplate = ViewModel.SelectedPage?.Page.Template ?? PaperTemplate.Ruled;
        ScheduleFitWidth();
    }
    private void ShowPaperPicker(bool addingPage)
    {
        if (ViewModel.Document is null) return;
        CommitEditors(); CloseSettingsPopups(); _addingPage = addingPage;
        TemplatePicker.SelectedTemplate = ViewModel.SelectedPage?.Page.Pdf is null ? ViewModel.SelectedPage?.Page.Template ?? PaperTemplate.Ruled : PaperTemplate.Ruled;
        TemplatePicker.IsEnabled = addingPage || ViewModel.SelectedPage?.Page.Pdf is null;
        ApplyPaperButton.IsEnabled = TemplatePicker.IsEnabled;
        PaperPickerTitle.Text = addingPage ? "Add a Page" : "Choose Paper";
        PaperPickerHint.Text = addingPage ? "A4 paper · Insert after the current page." : TemplatePicker.IsEnabled ? "Change this page's background. Your notes stay in place." : "PDF pages keep their original background. Add a new page to use a template.";
        ApplyPaperButton.Content = addingPage ? "Add Page" : "Apply Paper";
        PaperSettingsPopup.PlacementTarget = Sidebar.Visibility == Visibility.Visible ? PageOptionsButton : PenSettingsButton;
        Dispatcher.BeginInvoke(() => PaperSettingsPopup.IsOpen = true, DispatcherPriority.Input);
    }
    private void CancelPaperClick(object sender, RoutedEventArgs e) => CloseSettingsPopups();
    private void ApplyPaperClick(object sender, RoutedEventArgs e)
    {
        if (!TemplatePicker.IsEnabled) return;
        var template = TemplatePicker.SelectedTemplate;
        CloseSettingsPopups();
        CommitEditors();
        if (_addingPage) { ViewModel.AddPage(template); ScrollToSelected(); }
        else ViewModel.SetTemplate(template);
    }

    private async void ImportPdfClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF documents|*.pdf", Title = "Import PDF into This Notebook" };
        if (dialog.ShowDialog(this) != true) return;
        CommitEditors(); await RunAsync("Importing PDF…", async () => { var pages = await ViewModel.Pdf.ImportAsync(dialog.FileName); ViewModel.AppendPages(pages); ScrollToSelected(); await ViewModel.Autosave.FlushAsync(); });
    }
    private async void ExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Document is null) return;
        CommitEditors(); var dialog = new SaveFileDialog { Filter = "PDF documents|*.pdf", FileName = SafeFileName(ViewModel.Title) + ".pdf", Title = "Export PDF (text boxes become vector outlines)" };
        if (dialog.ShowDialog(this) != true) return;
        var snapshot = ViewModel.Document.Snapshot();
        await RunAsync("Exporting PDF…", async () => { await ViewModel.Pdf.ExportAsync(dialog.FileName, snapshot); ViewModel.Status = "PDF exported · " + Path.GetFileName(dialog.FileName); });
    }
    private async void ImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg", Title = "Insert Image" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync("Inserting image…", async () => { await InsertImageAsync(await File.ReadAllBytesAsync(dialog.FileName), Path.GetFileName(dialog.FileName), Path.GetExtension(dialog.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg"); });
    }
    private async Task InsertImageAsync(byte[] bytes, string name, string contentType)
    {
        if (ViewModel.SelectedPage is null) return;
        CommitEditors(); var bitmap = new BitmapImage(); using (var stream = new MemoryStream(bytes)) { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); }
        if (bitmap.PixelWidth > 20000 || bitmap.PixelHeight > 20000 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 80_000_000) throw new InvalidDataException("This image is too large. Resize it to at most 80 million pixels, with neither side exceeding 20,000 pixels.");
        var asset = await ViewModel.Repository.PutAssetAsync(name, contentType, bytes);
        var page = ViewModel.SelectedPage.Page; var width = Math.Min(page.Width - 96, bitmap.PixelWidth * .75); var height = width * bitmap.PixelHeight / bitmap.PixelWidth;
        if (height > page.Height - 96) { width *= (page.Height - 96) / height; height = page.Height - 96; }
        var image = new NoteImage { AssetId = asset.Id, X = 48, Y = 48, Width = width, Height = height };
        var editor = CurrentEditor;
        if (editor is not null) editor.AddImage(image); else { page.Images.Add(image); ViewModel.Changed(true); }
        SetTool(InkTool.Select);
    }
    private async Task PasteImageAsync()
    {
        if (!Clipboard.ContainsImage()) return;
        var bitmap = Clipboard.GetImage(); if (bitmap is null) return;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream);
        await RunAsync("Pasting image…", () => InsertImageAsync(stream.ToArray(), "Screenshot.png", "image/png"));
    }
    private async void BackupNoteClick(object sender, RoutedEventArgs e) => await BackupAsync(false);
    private async void BackupAllClick(object sender, RoutedEventArgs e) => await BackupAsync(true);
    private async Task BackupAsync(bool all)
    {
        if (!all && ViewModel.Document is null) return;
        CommitEditors(); var dialog = new SaveFileDialog { Filter = "Moye backups|*.moye", FileName = (all ? "All Moye Notebooks" : SafeFileName(ViewModel.Title)) + $"-{DateTime.Now:yyyyMMdd}.moye" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync("Creating editable backup…", async () =>
        {
            var documents = new List<NotebookDocument>();
            if (all)
            {
                foreach (var summary in await ViewModel.Repository.ListAsync()) { var doc = await ViewModel.Repository.LoadAsync(summary.Id); if (doc is not null) documents.Add(doc); }
                foreach (var pending in ViewModel.Autosave.PendingDocuments) { documents.RemoveAll(d => d.Id == pending.Id); documents.Add(pending.Snapshot()); }
            }
            if (ViewModel.Document is { } current) { documents.RemoveAll(d => d.Id == current.Id); documents.Add(current.Snapshot()); }
            await ViewModel.Backup.ExportAsync(dialog.FileName, documents); ViewModel.Status = "Backup exported · " + Path.GetFileName(dialog.FileName);
        });
    }
    private async void RestoreClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Moye backups|*.moye", Title = "Restore as New Notebook Copies" };
        if (dialog.ShowDialog(this) != true) return;
        CommitEditors(); await RunAsync("Validating and restoring backup…", async () =>
        {
            await ViewModel.Autosave.FlushAsync(); var docs = await ViewModel.Backup.ImportAsync(dialog.FileName);
            foreach (var doc in docs) await ViewModel.Repository.SaveAsync(doc);
            await ViewModel.RefreshLibraryAsync();
            if (!ViewModel.IsLibraryVisible && docs.Count > 0) { await ViewModel.OpenAsync(docs[0].Id); await PrepareNotebookViewAsync(); }
            SelectLibraryCurrent();
            ViewModel.Status = $"Restored {docs.Count} notebook(s)";
        });
    }
    private async void RetrySaveClick(object sender, RoutedEventArgs e) => await RunAsync("Retrying save…", () => ViewModel.Autosave.RetryAsync());
    private void MoreClick(object sender, RoutedEventArgs e) { var button = (Button)sender; button.ContextMenu.PlacementTarget = button; button.ContextMenu.Placement = PlacementMode.Bottom; button.ContextMenu.IsOpen = true; }
    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Write with a pen. Pan with one finger and pinch with two.\nTouch gestures pause while the pen is down.\n\nAll Notes saves and returns to your notebook home. Click the title to rename.\nUse Insert (+) for pages, PDFs and images.\nPen Settings controls color, thickness and Draw and Hold.\nDraw a line, hold about 0.65 seconds, then lift to finish.\nWhile held, drag the endpoint to adjust length and angle.\nClick the eraser to choose Pixel Eraser or Stroke Eraser.\nChoose a paper preview when creating notebooks or pages.\nPage Options changes existing page backgrounds.\nFit Width sits beside Fit Page and fills the writing area.\nClick the zoom percentage for Actual Size.\n\nB Pen · H Highlighter · E Eraser · L Lasso · T Text · V Select\nCtrl+Z Undo · Ctrl+Y Redo · Ctrl+D Duplicate selection\nCtrl+V Paste image · Delete Remove selection · Ctrl+S Save\nCtrl+wheel Zoom · F9 Sidebar · F11 Focus Mode\n\nIn Select mode, drag the top-right handle to move an object,\nand the bottom-right handle to resize it. In Text mode, tap\nthe page to add text or click existing text to edit it.\n\nNotes save automatically on this device. Use More to create\n.moye backups that keep all content editable. Use Share to\nexport a PDF with flattened annotations.\n\nMoye 1.4 · Offline Windows notebooks", "Moye User Guide");

    private void SidebarTabClick(object sender, RoutedEventArgs e) => ShowSidebarTab((string)((Button)sender).Tag == "Notebooks");
    private async void ShowNotebooksClick(object sender, RoutedEventArgs e)
    {
        ClearTouches(); CommitEditors();
        await RunAsync("Saving and returning to your notebooks…", async () =>
        {
            await ViewModel.ReturnToLibraryAsync();
            _fitWidthActive = false;
            if (_focusMode) ToggleFocus();
        });
        if (ViewModel.IsLibraryVisible) HomeSearch.Focus();
    }
    private void ShowPagesClick(object sender, RoutedEventArgs e) => ShowSidebarTab(false);
    private void ShowSidebarTab(bool notebooks)
    {
        Sidebar.Visibility = Visibility.Visible; SidebarColumn.Width = new GridLength(224);
        PagesPanel.Visibility = notebooks ? Visibility.Collapsed : Visibility.Visible;
        NotebooksPanel.Visibility = notebooks ? Visibility.Visible : Visibility.Collapsed;
        PagesTab.Background = notebooks ? Brushes.Transparent : Brushes.White;
        NotebooksTab.Background = notebooks ? Brushes.White : Brushes.Transparent;
        PagesTab.Foreground = notebooks ? Brushes.SlateGray : (Brush)FindResource("Accent");
        NotebooksTab.Foreground = notebooks ? (Brush)FindResource("Accent") : Brushes.SlateGray;
        CloseSettingsPopups();
    }
    private void ToggleSidebarClick(object sender, RoutedEventArgs e) => ToggleSidebar();
    private void ToggleSidebar() { bool show = Sidebar.Visibility != Visibility.Visible; Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed; SidebarColumn.Width = new GridLength(show ? 224 : 0); CloseSettingsPopups(); }
    private void FocusClick(object sender, RoutedEventArgs e) => ToggleFocus();
    private void ToggleFocus()
    {
        CloseSettingsPopups();
        _focusMode = !_focusMode;
        if (_focusMode) { _oldWindowState = WindowState; _sidebarBeforeFocus = Sidebar.Visibility == Visibility.Visible; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; Sidebar.Visibility = Visibility.Collapsed; SidebarColumn.Width = new GridLength(0); }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = _oldWindowState; Sidebar.Visibility = _sidebarBeforeFocus ? Visibility.Visible : Visibility.Collapsed; SidebarColumn.Width = new GridLength(_sidebarBeforeFocus ? 224 : 0); }
        FocusButton.Content = _focusMode ? "\uE73F" : "\uE740";
        FocusButton.ToolTip = _focusMode ? "Exit Focus Mode (F11)" : "Focus Mode (F11)";
    }
    private void ZoomInClick(object sender, RoutedEventArgs e) => ChangeZoom(ViewModel.Zoom * 1.15, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    private void ZoomOutClick(object sender, RoutedEventArgs e) => ChangeZoom(ViewModel.Zoom / 1.15, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    private void FitWidthClick(object sender, RoutedEventArgs e) => FitWidth();
    private void FitWidth()
    {
        if (ViewModel.IsLibraryVisible || AnyPenDown || ViewModel.SelectedPage is not { } target || Viewport.ActualWidth < 100) return;
        // Reserve the real scrollbar width even before it appears. The paper
        // retains a small gutter on either side and never runs under the sidebar.
        var available = Viewport.ActualWidth - PageList.Padding.Left - PageList.Padding.Right - SystemParameters.VerticalScrollBarWidth - 4;
        ChangeZoom(available / target.Page.Width, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
        _fitWidthActive = true;
        FitWidthButton.Foreground = (Brush)FindResource("Accent");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_fitWidthActive || ViewModel.IsLibraryVisible) return;
            var scroll = GetScroll();
            if (scroll is not null) scroll.ScrollToHorizontalOffset(Math.Max(0, (scroll.ExtentWidth - scroll.ViewportWidth) / 2));
        }));
    }
    private void ScheduleFitWidth()
    {
        if (!_fitWidthActive || _fitWidthPending) return;
        _fitWidthPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { _fitWidthPending = false; if (_fitWidthActive) FitWidth(); }));
    }
    private void ClearFitWidth()
    {
        _fitWidthActive = false;
        FitWidthButton.Foreground = new SolidColorBrush(Color.FromRgb(104, 117, 138));
    }
    private void FitPageClick(object sender, RoutedEventArgs e) => FitPage();
    private void ActualSizeClick(object sender, RoutedEventArgs e) => ChangeZoom(1, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    private void FitPage()
    {
        if (AnyPenDown || ViewModel.SelectedPage is not { } target || Viewport.ActualWidth < 100 || Viewport.ActualHeight < 140) return;
        ClearFitWidth();
        var revision = BeginZoomNavigation();
        var document = ViewModel.Document;
        try
        {
            var page = target.Page;
            ViewModel.Zoom = Math.Min((Viewport.ActualWidth - 90) / page.Width, (Viewport.ActualHeight - 106) / page.Height);
            PageList.UpdateLayout();
            // Keep the original target until recycling and BringIntoView finish.
            // ScrollChanged must not replace it with a transient visible neighbour.
            PageList.ScrollIntoView(target);
            PageList.UpdateLayout();
            CompleteZoomAfterLayout(revision, document, null, target);
        }
        catch { EndZoomNavigation(revision); throw; }
    }
    private void ChangeZoom(double zoom, Point anchor)
    {
        if (AnyPenDown || !double.IsFinite(zoom)) return;
        ClearFitWidth();
        if (Math.Abs(Math.Clamp(zoom, .25, 4) - ViewModel.Zoom) < .000001) return;
        var paperAnchor = CapturePageZoomAnchor(anchor);
        var revision = BeginZoomNavigation();
        var document = ViewModel.Document;
        try
        {
            ViewModel.Zoom = zoom;
            PageList.UpdateLayout();
            var restored = paperAnchor is null || RestorePageZoomAnchor(paperAnchor);
            // Once restored synchronously, do not reposition later: a touch-move
            // handler can still add its pan delta after ChangeZoom returns.
            CompleteZoomAfterLayout(revision, document, restored ? null : paperAnchor, null);
        }
        catch { EndZoomNavigation(revision); throw; }
    }

    private int BeginZoomNavigation()
    {
        _zoomNavigationActive = true;
        return ++_zoomNavigationRevision;
    }

    private PageZoomAnchor? CapturePageZoomAnchor(Point anchor)
    {
        PageZoomAnchor? nearest = null;
        PageZoomAnchor? visibleFallback = null;
        double nearestDistance = double.PositiveInfinity;
        var viewportBounds = new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight);
        foreach (var host in _editors.Keys)
        {
            if (!host.IsLoaded || host.ActualWidth <= 0 || host.ActualHeight <= 0 || host.DataContext is not PageViewModel item || !ViewModel.Pages.Contains(item)) continue;
            var origin = host.TranslatePoint(new Point(), Viewport);
            var bounds = new Rect(origin, host.RenderSize);
            // If the pointer is in a gutter, preserve the nearest paper edge and
            // the existing gutter distance; page labels/gaps remain fixed DIP.
            var paperPoint = new Point(Math.Clamp(anchor.X, bounds.Left, bounds.Right), Math.Clamp(anchor.Y, bounds.Top, bounds.Bottom));
            var candidate = new PageZoomAnchor(item,
                new Point((paperPoint.X - origin.X) * item.Page.Width / host.ActualWidth,
                          (paperPoint.Y - origin.Y) * item.Page.Height / host.ActualHeight), paperPoint);
            if (bounds.IntersectsWith(viewportBounds)) visibleFallback ??= candidate;
            var distance = (paperPoint - anchor).LengthSquared;
            if (distance < nearestDistance) { nearestDistance = distance; nearest = candidate; }
        }
        return nearest ?? visibleFallback;
    }

    private bool RestorePageZoomAnchor(PageZoomAnchor anchor)
    {
        if (AnyPenDown || !ViewModel.Pages.Contains(anchor.Page)) return false;
        Border? FindHost() => _editors.Keys.FirstOrDefault(h => h.IsLoaded && ReferenceEquals(h.DataContext, anchor.Page) && h.ActualWidth > 0 && h.ActualHeight > 0);
        var host = FindHost();
        if (host is null)
        {
            // A large zoom can recycle the anchor's container. Realize that exact
            // page, rather than using SelectedPage or an estimated item height.
            PageList.ScrollIntoView(anchor.Page);
            PageList.UpdateLayout();
            host = FindHost();
        }
        var scroll = GetScroll();
        if (host is null || scroll is null) return false;
        // A second pass reconciles extent estimates after the virtualizing panel
        // realizes the destination. Both passes complete before touch panning.
        for (int pass = 0; pass < 2; pass++)
        {
            host = FindHost();
            if (host is null) return false;
            var actual = host.TranslatePoint(new Point(anchor.PagePoint.X * host.ActualWidth / anchor.Page.Page.Width,
                                                       anchor.PagePoint.Y * host.ActualHeight / anchor.Page.Page.Height), Viewport);
            // These coordinates include fixed padding, labels and centered-page
            // offsets, which cannot be obtained by scaling the total scroll offset.
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + actual.X - anchor.ViewportPoint.X);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + actual.Y - anchor.ViewportPoint.Y);
            PageList.UpdateLayout();
        }
        return true;
    }

    private void CompleteZoomAfterLayout(int revision, NotebookDocument? document, PageZoomAnchor? anchor, PageViewModel? fitTarget)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (revision != _zoomNavigationRevision) return;
            if (_closing || AnyPenDown || !ReferenceEquals(document, ViewModel.Document) ||
                (fitTarget is not null && !ReferenceEquals(ViewModel.SelectedPage, fitTarget)))
            { EndZoomNavigation(revision); return; }
            try
            {
                // ScrollIntoView may itself enqueue a Loaded-priority operation.
                // Reconcile once its generated container and extent are available.
                if (fitTarget is not null)
                {
                    PageList.ScrollIntoView(fitTarget);
                    PageList.UpdateLayout();
                }
                else if (anchor is not null) RestorePageZoomAnchor(anchor);
            }
            finally
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    EndZoomNavigation(revision);
                    if (fitTarget is null && revision == _zoomNavigationRevision) UpdateVisiblePageSelection();
                }));
            }
        }));
    }

    private void EndZoomNavigation(int revision)
    {
        if (revision == _zoomNavigationRevision) _zoomNavigationActive = false;
    }
    private ScrollViewer? GetScroll() => _scroll ??= Descendants<ScrollViewer>(PageList).FirstOrDefault();
    private void ViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (AnyPenDown) { e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ChangeZoom(ViewModel.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), e.GetPosition(Viewport)); e.Handled = true; }
    }

    private void ViewportTouchDown(object sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (AnyPenDown || DateTime.UtcNow < _ignoreTouchUntil) { _blockedTouches.Add(e.TouchDevice.Id); return; }
        _blockedTouches.Remove(e.TouchDevice.Id);
        _touches[e.TouchDevice.Id] = (e.TouchDevice, e.GetTouchPoint(Viewport).Position);
        e.TouchDevice.Capture(Viewport); ResetTouchBaseline();
    }
    private void ViewportTouchMove(object sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (AnyPenDown || _blockedTouches.Contains(e.TouchDevice.Id) || !_touches.TryGetValue(e.TouchDevice.Id, out var previous)) return;
        var position = e.GetTouchPoint(Viewport).Position; _touches[e.TouchDevice.Id] = (e.TouchDevice, position);
        var scroll = GetScroll(); if (scroll is null) return;
        if (_touches.Count == 1) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + previous.Point.Y - position.Y);
        else if (_touches.Count == 2)
        {
            var points = _touches.Values.Select(t => t.Point).ToArray(); var distance = (points[0] - points[1]).Length; var center = new Point((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);
            if (_pinchDistance > 20 && distance > 20) ChangeZoom(ViewModel.Zoom * distance / _pinchDistance, center);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + _touchCenter.Y - center.Y); scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + _touchCenter.X - center.X);
            _pinchDistance = distance; _touchCenter = center;
        }
    }
    private void ViewportTouchUp(object sender, TouchEventArgs e)
    {
        e.Handled = true; _touches.Remove(e.TouchDevice.Id); _blockedTouches.Remove(e.TouchDevice.Id);
        if (e.TouchDevice.Captured == Viewport) e.TouchDevice.Capture(null); ResetTouchBaseline();
    }
    private void ViewportLostTouch(object sender, TouchEventArgs e) { _touches.Remove(e.TouchDevice.Id); ResetTouchBaseline(); }
    private void ResetTouchBaseline()
    {
        if (_touches.Count != 2) { _pinchDistance = 0; return; }
        var p = _touches.Values.Select(t => t.Point).ToArray(); _pinchDistance = (p[0] - p[1]).Length; _touchCenter = new Point((p[0].X + p[1].X) / 2, (p[0].Y + p[1].Y) / 2);
    }
    private void ClearTouches(bool block = false)
    {
        var devices = _touches.Values.ToArray();
        if (block) foreach (var id in _touches.Keys) _blockedTouches.Add(id); else _blockedTouches.Clear();
        _touches.Clear(); foreach (var touch in devices) if (touch.Device.Captured == Viewport) touch.Device.Capture(null); _pinchDistance = 0;
    }
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!_ready || ViewModel.IsBusy) return;
        // Library search keeps its text shortcuts; editor commands cannot act
        // on the notebook retained in memory while the home screen is visible.
        if (ViewModel.IsLibraryVisible) return;
        if (e.Key == Key.Escape && (PenSettingsPopup.IsOpen || PaperSettingsPopup.IsOpen || EraserSettingsPopup.IsOpen)) { CloseSettingsPopups(); e.Handled = true; return; }
        if (e.Key == Key.F9) { ToggleSidebar(); e.Handled = true; return; }
        if (e.Key == Key.F11) { ToggleFocus(); e.Handled = true; return; }
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.S) { CommitEditors(); await RunAsync("Saving…", () => ViewModel.Autosave.FlushAsync()); e.Handled = true; return; }
        // Do not steal IME, clipboard, or text undo from an active text box.
        if (Keyboard.FocusedElement is TextBoxBase) return;
        if (e.Key == Key.Escape) { if (_focusMode) ToggleFocus(); else SetTool(InkTool.Pen); e.Handled = true; return; }
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z: CommitEditors(); ViewModel.Undo(); e.Handled = true; break;
                case Key.Y: CommitEditors(); ViewModel.Redo(); e.Handled = true; break;
                case Key.D: CurrentEditor?.DuplicateSelection(); e.Handled = true; break;
                case Key.A: SetTool(InkTool.Lasso); CurrentEditor?.SelectAllInk(); e.Handled = true; break;
                case Key.V: await PasteImageAsync(); e.Handled = true; break;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            InkTool? tool = e.Key switch { Key.B => InkTool.Pen, Key.H => InkTool.Highlighter, Key.E => _eraserTool, Key.L => InkTool.Lasso, Key.T => InkTool.Text, Key.V => InkTool.Select, _ => null };
            if (tool.HasValue) { SetTool(tool.Value); e.Handled = true; }
            if (e.Key == Key.Delete) { CurrentEditor?.DeleteSelection(); e.Handled = true; }
        }
    }
    private static BitmapSource DecodeBitmap(byte[] bytes)
    {
        var bitmap = new BitmapImage(); using var stream = new MemoryStream(bytes);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    private static string SafeFileName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
