using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Moye.Controls;
using Moye.Models;
using Moye.Services;

namespace Moye;

public partial class MainWindow
{
    private const string InkClipboardFormat = "Moye.Ink.ISF.v1";
    private const string PresetDragFormat = "Moye.WritingPreset.v1";
    private readonly WritingPreferencesStore _preferencesStore;
    private WritingPreferences _preferences = WritingPreferences.CreateDefault();
    private readonly DispatcherTimer _preferencesTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private Task _preferencesSaveTask = Task.CompletedTask;
    private long _preferencesRevision, _savedPreferencesRevision;
    private bool _syncingPreferences, _canSavePreferences = true;
    private string? _preferencesLoadWarning;
    private WritingPreset _workingPreset = WritingPreferences.CreateDefault().Presets[0].Snapshot();
    private readonly Dictionary<InkTool, WritingPreset> _recentWritingPresets = [];
    private Point _presetDragStart, _panPoint;
    private InkTool? _toolBeforePan;
    private bool _mousePanning;

    private void InitializeWritingUi()
    {
        _preferencesTimer.Tick += async (_, _) => { _preferencesTimer.Stop(); await SavePreferencesAsync(); };
        ApplyPreset(_preferences.Presets[0], false);
    }

    private async Task LoadPreferencesAsync()
    {
        var result = await _preferencesStore.LoadAsync();
        _preferences = result.Preferences; _canSavePreferences = result.CanSave; _preferencesLoadWarning = result.Warning;
        _syncingPreferences = true;
        try
        {
            _eraserTool = _preferences.EraserTool;
            HoldToStraightenToggle.IsChecked = _preferences.HoldToStraightenEnabled;
            EraserSizeSlider.Value = _preferences.EraserSize;
            HighlightOnlyToggle.IsChecked = _preferences.EraseHighlightOnly;
            ApplyPreset(_preferences.Presets.FirstOrDefault(p => p.Id == _preferences.LastPresetId) ?? _preferences.Presets[0], false);
        }
        finally { _syncingPreferences = false; }
        if (result.Warning is not null) ShowPreferencesWarning(result.Warning);
    }

    private void QueuePreferencesSave()
    {
        if (!_ready || _syncingPreferences) return;
        if (!_canSavePreferences)
        {
            ShowPreferencesWarning((_preferencesLoadWarning ?? "Writing settings are read-only.") + " Tool changes apply only to this session. Restart after resolving the settings file.");
            return;
        }
        _preferences.EraserTool = _eraserTool;
        _preferences.HoldToStraightenEnabled = HoldToStraightenToggle.IsChecked == true;
        _preferences.EraserSize = EraserSizeSlider.Value;
        _preferences.EraseHighlightOnly = HighlightOnlyToggle.IsChecked == true;
        _preferencesRevision++; _preferencesTimer.Stop(); _preferencesTimer.Start();
    }

    private async Task SavePreferencesAsync(bool throwOnError = false)
    {
        _preferencesTimer.Stop();
        if (_preferencesRevision == _savedPreferencesRevision) return;
        // A single ordered chain prevents an older snapshot overwriting a newer edit.
        var previous = _preferencesSaveTask;
        var revision = _preferencesRevision; var snapshot = _preferences.Snapshot();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _preferencesSaveTask = completion.Task;
        try
        {
            await previous;
            if (revision <= _savedPreferencesRevision) return;
            if (!_canSavePreferences) throw new IOException(_preferencesLoadWarning ?? "The writing settings file cannot be overwritten.");
            await _preferencesStore.SaveAsync(snapshot);
            _savedPreferencesRevision = revision;
            PreferencesRetryButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowPreferencesWarning("Writing settings could not be saved. " + ex.Message);
            if (throwOnError) throw;
        }
        finally { completion.SetResult(); }
    }
    private void ShowPreferencesWarning(string message)
    {
        PreferencesRetryButton.Visibility = Visibility.Visible; PreferencesRetryButton.ToolTip = message;
        ViewModel.Status = message;
    }
    private async void RetryPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (!_canSavePreferences || _preferencesRevision == _savedPreferencesRevision)
        { MessageBox.Show(this, _preferencesLoadWarning ?? "Writing settings are saved.", "Writing Settings"); return; }
        await SavePreferencesAsync();
    }

    private void ApplyPreset(WritingPreset preset, bool save = true)
    {
        RememberWorkingPreset();
        CancelPanForToolChange();
        CommitEditors(); CloseSettingsPopups();
        _workingPreset = preset.Snapshot(); _preferences.LastPresetId = preset.Id;
        _tool = preset.Tool; _color = (Color)ColorConverter.ConvertFromString(preset.Color); _width = preset.Width;
        if (_tool == InkTool.Pen) _penColor = _color; else _highlighterColor = _color;
        _recentWritingPresets[_tool] = _workingPreset.Snapshot();
        _syncingPreferences = true;
        try { WidthPicker.StrokeWidth = _width; }
        finally { _syncingPreferences = false; }
        UpdateTool(); RefreshPresetToolbar();
        if (save) QueuePreferencesSave();
    }

    private void RememberWorkingPreset()
    {
        if (_tool is not (InkTool.Pen or InkTool.Highlighter)) return;
        var preset = _workingPreset.Snapshot(); preset.Tool = _tool; preset.Color = _color.ToString(); preset.Width = _width;
        _recentWritingPresets[_tool] = preset;
    }

    private void ConfigureEditor(PageEditor editor)
    {
        editor.SetTextDefaults(_textDefaults);
        editor.ConfigureWriting(_workingPreset, _preferences.EraserSize, _preferences.EraseHighlightOnly);
        editor.SetTool(_tool, _color, EffectiveWidth());
        editor.InkCanvas.HoldToStraightenEnabled = HoldToStraightenToggle.IsChecked == true;
        editor.InkCanvas.EditingModeInverted = _eraserTool == InkTool.StrokeEraser ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.EraseByPoint;
    }

    private void RefreshPresetToolbar()
    {
        FavouritePresets.Children.Clear();
        int index = 0;
        foreach (var preset in _preferences.Presets.Where(p => p.IsFavorite).Take(9))
        {
            var number = ++index;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Ellipse { Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.Color)), Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            panel.Children.Add(new TextBlock { Text = $"{number}  {preset.Name}", MaxWidth = 105, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            bool active = _workingPreset.Id == preset.Id && _tool == preset.Tool;
            var button = new Button { Content = panel, Tag = preset.Id, MinHeight = 44, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 4, 0), AllowDrop = true,
                Background = active ? (Brush)FindResource("AccentSoft") : Brushes.Transparent,
                ToolTip = $"{preset.Name} · {preset.Width * 25.4 / 96:0.##} mm ({number})\nDrag to reorder. Use Manage pens to edit or hide." };
            System.Windows.Automation.AutomationProperties.SetName(button, $"Preset {number}: {preset.Name}");
            button.Click += (_, _) => { ApplyPreset(preset); PageList.Focus(); };
            button.PreviewMouseLeftButtonDown += (_, e) => { if (!PenInkCanvas.IsTouch(e.StylusDevice)) _presetDragStart = e.GetPosition(button); };
            button.PreviewMouseMove += (_, e) =>
            {
                if (!button.IsPressed || e.LeftButton != MouseButtonState.Pressed || PenInkCanvas.IsTouch(e.StylusDevice)) return;
                var delta = e.GetPosition(button) - _presetDragStart;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                DragDrop.DoDragDrop(button, new DataObject(PresetDragFormat, preset.Id), DragDropEffects.Move); e.Handled = true;
            };
            button.Drop += (_, e) =>
            {
                if (e.Data.GetData(PresetDragFormat) is not string sourceId || sourceId == preset.Id) return;
                var source = _preferences.Presets.FirstOrDefault(p => p.Id == sourceId); if (source is null) return;
                int targetIndex = _preferences.Presets.IndexOf(preset);
                _preferences.Presets.Remove(source); _preferences.Presets.Insert(targetIndex, source);
                RefreshPresetToolbar(); QueuePreferencesSave(); e.Handled = true;
            };
            FavouritePresets.Children.Add(button);
        }
        if (index == 0) FavouritePresets.Children.Add(new TextBlock { Text = "Add favorites in Manage pens", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("MutedInk"), Margin = new Thickness(8) });
    }

    private void ManagePresetsClick(object sender, RoutedEventArgs e)
    {
        CommitEditors(); CloseSettingsPopups();
        var dialog = new PresetManagerDialog(this, _preferences);
        if (dialog.ShowDialog() != true) return;
        _preferences = dialog.Preferences;
        ApplyPreset(_preferences.Presets.FirstOrDefault(p => p.Id == _preferences.LastPresetId) ?? _preferences.Presets[0]);
    }
    private void SavePresetClick(object sender, RoutedEventArgs e)
    {
        CloseSettingsPopups();
        if (_preferences.Presets.Count >= 40) { MessageBox.Show(this, "You can keep up to 40 presets. Remove one in Presets first.", "Pen Presets"); return; }
        var writingTool = _tool is InkTool.Pen or InkTool.Highlighter ? _tool : _workingPreset.Tool;
        var dialog = new InputDialog(this, "Save Pen Preset", ("Name", writingTool == InkTool.Highlighter ? "My Highlighter" : "My Pen"));
        if (dialog.ShowDialog() != true) return;
        var preset = _workingPreset.Snapshot(); preset.Id = Guid.NewGuid().ToString("N"); preset.Name = dialog.Values[0];
        preset.Color = _color.ToString(); preset.Width = _width; preset.IsFavorite = true;
        preset.Tool = writingTool;
        if (preset.Tool == InkTool.Highlighter) preset.Opacity = .5;
        _preferences.Presets.Add(preset); ApplyPreset(preset);
    }
    private void EraserSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingPreferences) return;
        _preferences.EraserSize = EraserSizeSlider.Value; _preferences.EraseHighlightOnly = HighlightOnlyToggle.IsChecked == true;
        UpdateTool(); QueuePreferencesSave();
    }

    private void CopyInk(bool cut)
    {
        var editor = CurrentEditor; var bytes = editor?.ExportSelectedInk(); if (bytes is null) return;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var data = new DataObject();
            // Raw stream data marshals as bytes, without object serialization.
            data.SetData(InkClipboardFormat, stream, autoConvert: false);
            Clipboard.SetDataObject(data, true);
            // Cutting is safe only after the clipboard accepted the editable copy.
            if (cut) editor!.DeleteSelection();
            ViewModel.Status = cut ? "Ink cut · Paste onto another page with Ctrl+V" : "Ink copied · Paste onto another page with Ctrl+V";
        }
        catch (Exception ex) { ViewModel.Status = "Clipboard unavailable: " + ex.Message; }
    }
    private async Task PasteContentAsync()
    {
        if (CurrentEditor is not { } editor) return;
        try
        {
            if (Clipboard.ContainsData(InkClipboardFormat))
            {
                var data = Clipboard.GetData(InkClipboardFormat);
                var bytes = data switch { byte[] b => b, MemoryStream stream => stream.ToArray(), _ => null };
                if (bytes is null || bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Unsupported clipboard ink.");
                SetTool(InkTool.Lasso); editor.ImportInk(bytes); PageList.Focus(); return;
            }
            await PasteImageAsync();
        }
        catch (Exception ex) { ViewModel.Status = "Unable to paste: " + ex.Message; }
    }
    private void CopyInkClick(object sender, RoutedEventArgs e) => CopyInk(false);
    private void CutInkClick(object sender, RoutedEventArgs e) => CopyInk(true);
    private async void PasteContentClick(object sender, RoutedEventArgs e) => await PasteContentAsync();

    private void BeginTemporaryPan()
    {
        if (_toolBeforePan is not null || AnyPenDown) return;
        _toolBeforePan = _tool; SetTool(InkTool.Hand); Viewport.Cursor = Cursors.Hand;
    }
    private void CancelPanForToolChange()
    {
        _toolBeforePan = null; _mousePanning = false;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        Viewport.Cursor = null;
    }
    private void EndTemporaryPan()
    {
        _mousePanning = false;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        if (_toolBeforePan is not { } previous) return;
        _toolBeforePan = null; Viewport.Cursor = null; SetTool(previous);
    }
    private void WindowKeyUp(object sender, KeyEventArgs e) { if (e.Key == Key.Space && _toolBeforePan is not null) { EndTemporaryPan(); e.Handled = true; } }
    private void ViewportPanDown(object sender, MouseButtonEventArgs e)
    {
        if (_toolBeforePan is null || e.StylusDevice is not null || e.ChangedButton != MouseButton.Left) return;
        _panPoint = e.GetPosition(Viewport); _mousePanning = true; Viewport.CaptureMouse(); e.Handled = true;
    }
    private void ViewportPanMove(object sender, MouseEventArgs e)
    {
        if (!_mousePanning || e.StylusDevice is not null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(Viewport); var scroll = GetScroll();
        scroll?.ScrollToHorizontalOffset(scroll.HorizontalOffset + _panPoint.X - point.X);
        scroll?.ScrollToVerticalOffset(scroll.VerticalOffset + _panPoint.Y - point.Y);
        _panPoint = point; e.Handled = true;
    }
    private void ViewportPanUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mousePanning || e.ChangedButton != MouseButton.Left) return;
        _mousePanning = false; Viewport.ReleaseMouseCapture(); e.Handled = true;
    }
    private void ApplyFocusChrome()
    {
        NotebookHeader.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;
        WritingHeader.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;
        EditorFooter.Visibility = _focusMode && !ViewModel.HasSaveError ? Visibility.Collapsed : Visibility.Visible;
        ExitFocusButton.Visibility = _focusMode ? Visibility.Visible : Visibility.Collapsed;
    }
}
