using System.Windows;
using System.Windows.Controls;

namespace Moye.Controls;

public sealed class InputDialog : Window
{
    private readonly List<TextBox> _inputs = [];
    public string[] Values => _inputs.Select(t => t.Text.Trim()).ToArray();
    public InputDialog(Window owner, string title, params (string Label, string Value)[] fields)
    {
        Owner = owner; Title = title; Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Background = System.Windows.Media.Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        foreach (var (label, value) in fields)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 18), MaxLength = 160 };
            _inputs.Add(input); panel.Children.Add(input);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var save = new Button { Content = "OK", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) => { if (_inputs.Any(t => string.IsNullOrWhiteSpace(t.Text))) { _inputs.First(t => string.IsNullOrWhiteSpace(t.Text)).Focus(); return; } DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { _inputs[0].Focus(); _inputs[0].SelectAll(); };
    }
}
