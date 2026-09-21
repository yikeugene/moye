using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Moye.Models;

namespace Moye.Controls;

/// <summary>Edits a private copy. Cancel never mutates active writing settings.</summary>
public sealed class PresetManagerDialog : Window
{
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(WritingPreset.Name), MinWidth = 190 };
    private readonly TextBox _name = new() { MaxLength = 80 };
    private readonly Button _color = new() { MinHeight = 44, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Border _colorSwatch = new() { Width = 36, Height = 26, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 12, 0) };
    private readonly StrokeWidthPicker _width = new();
    private readonly ComboBox _tool = new() { ItemsSource = new[] { "Pen", "Highlighter" } };
    private readonly TextBox _opacity = new();
    private readonly CheckBox _pressure = Check("Pressure sensitivity"), _smoothing = Check("Stroke smoothing"), _favorite = Check("Show in favourite toolbar");
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
    private WritingPreset? _editing;
    private bool _loading;
    private Color _selectedColor;
    public WritingPreferences Preferences { get; }

    public PresetManagerDialog(Window owner, WritingPreferences preferences)
    {
        Preferences = preferences.Snapshot();
        Owner = owner; Title = "Pen Presets"; Width = 760; Height = 760;
        MaxHeight = Math.Max(500, SystemParameters.WorkArea.Height - 30);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Background = Brushes.White;
        var root = new Grid { Margin = new Thickness(24), Background = Brushes.White };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Your everyday pens", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(240) }); body.ColumnDefinitions.Add(new()); Grid.SetRow(body, 1); root.Children.Add(body);
        var left = new DockPanel { Margin = new Thickness(0, 0, 20, 0) };
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        AddButton(actions, "↑", () => Move(-1), "Move preset up"); AddButton(actions, "↓", () => Move(1), "Move preset down");
        AddButton(actions, "Duplicate", Duplicate); AddButton(actions, "Delete", Delete);
        DockPanel.SetDock(actions, Dock.Bottom); left.Children.Add(actions);
        _list.ItemContainerStyle = new Style(typeof(ListBoxItem));
        _list.ItemContainerStyle.Setters.Add(new Setter(MinHeightProperty, 48d));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10)));
        left.Children.Add(_list); body.Children.Add(left);
        var form = new StackPanel();
        AddField(form, "Preset name", _name); AddField(form, "Tool", _tool);
        var colorContent = new StackPanel { Orientation = Orientation.Horizontal };
        colorContent.Children.Add(_colorSwatch);
        colorContent.Children.Add(new TextBlock { Text = "Choose color…", VerticalAlignment = VerticalAlignment.Center });
        _color.Content = colorContent;
        System.Windows.Automation.AutomationProperties.SetName(_color, "Choose preset color");
        _color.Click += (_, _) => ChooseColor();
        AddField(form, "Color", _color);
        _width.Margin = new Thickness(0, 0, 0, 12); form.Children.Add(_width);
        AddField(form, "Pen opacity (%)", _opacity);
        form.Children.Add(new TextBlock { Text = "Highlighter uses its native 50% transparency.", Foreground = Brushes.SlateGray, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(_pressure); form.Children.Add(_smoothing); form.Children.Add(_favorite); form.Children.Add(_error);
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var save = new Button { Content = "Save and Use", IsDefault = true, Style = TryFindResource("PrimaryButton") as Style };
        save.Click += (_, _) => { if (CommitSelection()) DialogResult = true; }; footer.Children.Add(save); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (!StoreEditor()) { _loading = true; _list.SelectedItem = _editing; _loading = false; return; }
            LoadEditor(_list.SelectedItem as WritingPreset);
        };
        _tool.SelectionChanged += (_, _) => { _opacity.IsEnabled = _tool.SelectedIndex == 0; UpdatePreview(); };
        _opacity.TextChanged += (_, _) => UpdatePreview();
        _pressure.Checked += (_, _) => UpdatePreview(); _pressure.Unchecked += (_, _) => UpdatePreview();
        _smoothing.Checked += (_, _) => UpdatePreview(); _smoothing.Unchecked += (_, _) => UpdatePreview();
        Refresh(Preferences.Presets.FirstOrDefault(p => p.Id == Preferences.LastPresetId) ?? Preferences.Presets[0]);
    }

    private static CheckBox Check(string text) => new() { Content = text, MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center };
    private static void AddField(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5), Foreground = Brushes.SlateGray, FontSize = 12 });
        input.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(input);
    }
    private static void AddButton(Panel panel, string text, Action action, string? help = null)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 3, 4, 3), ToolTip = help ?? text };
        System.Windows.Automation.AutomationProperties.SetName(button, help ?? text);
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void Refresh(WritingPreset selected)
    {
        _loading = true; _list.ItemsSource = null; _list.ItemsSource = Preferences.Presets; _list.SelectedItem = selected; _loading = false; LoadEditor(selected);
    }
    private void LoadEditor(WritingPreset? preset)
    {
        _editing = preset; if (preset is null) return;
        _loading = true;
        try
        {
            _name.Text = preset.Name;
            SetSelectedColor((Color)ColorConverter.ConvertFromString(preset.Color));
            // Keep the persisted DIP value exact; millimetres are display only.
            _width.StrokeWidth = preset.Width;
            _tool.SelectedIndex = preset.Tool == InkTool.Highlighter ? 1 : 0;
            _opacity.Text = (preset.Opacity * 100).ToString("0.######", CultureInfo.CurrentCulture);
            _pressure.IsChecked = preset.PressureSensitivity; _smoothing.IsChecked = preset.Smoothing;
            _favorite.IsChecked = preset.IsFavorite; _error.Text = "";
        }
        finally { _loading = false; }
        UpdatePreview();
    }
    private void ChooseColor()
    {
        var dialog = new ColorPickerDialog(this, _selectedColor, "Choose Preset Color");
        if (dialog.ShowDialog() == true) SetSelectedColor(dialog.SelectedColor);
    }
    private void SetSelectedColor(Color color)
    {
        _selectedColor = color;
        _colorSwatch.Background = new SolidColorBrush(color);
        UpdatePreview();
    }
    private void UpdatePreview()
    {
        if (_loading || _editing is null) return;
        var highlighter = _tool.SelectedIndex == 1;
        var opacity = _editing.Opacity;
        if (double.TryParse(_opacity.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var percent) &&
            double.IsFinite(percent) && percent >= 10 && percent <= 100) opacity = percent / 100;
        _width.ConfigurePreview(_selectedColor, highlighter, highlighter ? .5 : opacity,
            _pressure.IsChecked == true, _smoothing.IsChecked == true);
    }
    private bool StoreEditor()
    {
        if (_editing is null) return true;
        if (string.IsNullOrWhiteSpace(_name.Text)) { _error.Text = "Give this preset a name."; return false; }
        if (!double.TryParse(_opacity.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var opacity) || !double.IsFinite(opacity) || opacity < 10 || opacity > 100)
        { _error.Text = "Use an opacity between 10 and 100 percent."; return false; }
        _editing.Name = _name.Text.Trim();
        _editing.Width = _width.StrokeWidth;
        _editing.Color = _selectedColor.ToString();
        _editing.Tool = _tool.SelectedIndex == 1 ? InkTool.Highlighter : InkTool.Pen;
        _editing.Opacity = _editing.Tool == InkTool.Highlighter ? .5 : opacity / 100;
        _editing.PressureSensitivity = _pressure.IsChecked == true; _editing.Smoothing = _smoothing.IsChecked == true; _editing.IsFavorite = _favorite.IsChecked == true;
        _error.Text = ""; return true;
    }
    private bool CommitSelection()
    {
        if (!StoreEditor()) return false;
        Preferences.LastPresetId = _editing?.Id ?? Preferences.Presets[0].Id;
        return true;
    }
    private void Move(int offset)
    {
        if (!StoreEditor() || _editing is null) return;
        var index = Preferences.Presets.IndexOf(_editing); var next = index + offset;
        if (next < 0 || next >= Preferences.Presets.Count) return;
        Preferences.Presets.RemoveAt(index); Preferences.Presets.Insert(next, _editing); Refresh(_editing);
    }
    private void Duplicate()
    {
        if (!StoreEditor() || _editing is null) return;
        if (Preferences.Presets.Count >= 40) { _error.Text = "You can keep up to 40 presets."; return; }
        var copy = _editing.Snapshot(); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = _editing.Name + " Copy";
        Preferences.Presets.Insert(Preferences.Presets.IndexOf(_editing) + 1, copy); Refresh(copy);
    }
    private void Delete()
    {
        if (_editing is null) return;
        if (Preferences.Presets.Count == 1) { _error.Text = "Keep at least one preset."; return; }
        var index = Preferences.Presets.IndexOf(_editing); Preferences.Presets.Remove(_editing);
        if (Preferences.LastPresetId == _editing.Id) Preferences.LastPresetId = Preferences.Presets[0].Id;
        _editing = null; Refresh(Preferences.Presets[Math.Min(index, Preferences.Presets.Count - 1)]);
    }
}
