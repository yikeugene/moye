using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Moye.Controls;

/// <summary>Only Apply publishes a selected color; closing or Cancel retains the original value.</summary>
public sealed class ColorPickerDialog : Window
{
    public Color SelectedColor { get; private set; }
    public ColorPickerSurface Picker { get; }

    public ColorPickerDialog(Window owner, Color initialColor, string title = "Choose Color")
    {
        Owner = owner; Title = title; SelectedColor = initialColor;
        Width = 480; Height = 640; MinWidth = 420; MinHeight = 480;
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 24);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei"); FontSize = 14;
        ResizeMode = ResizeMode.CanResize;
        var root = new DockPanel { Margin = new Thickness(22) };
        var titleBlock = new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(titleBlock, Dock.Top); root.Children.Add(titleBlock);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, MinHeight = 44, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply", IsDefault = true, MinWidth = 88, MinHeight = 44, Background = new SolidColorBrush(Color.FromRgb(50, 106, 232)), Foreground = Brushes.White };
        if (TryFindResource("PrimaryButton") is Style primary) apply.Style = primary;
        footer.Children.Add(cancel); footer.Children.Add(apply); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Picker = new ColorPickerSurface(initialColor);
        root.Children.Add(new ScrollViewer { Content = Picker, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        apply.Click += (_, _) => { SelectedColor = Picker.SelectedColor; DialogResult = true; };
        Content = root;
        Loaded += (_, _) => Picker.ColorField.Focus();
    }
}
