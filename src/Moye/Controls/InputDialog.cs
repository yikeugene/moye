using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Moye.Controls;

public sealed class InputDialog : Window
{
    private readonly List<TextBox> _inputs = [];
    public string[] Values => _inputs.Select(t => t.Text.Trim()).ToArray();
    public InputDialog(Window owner, string title, params (string Label, string Value)[] fields)
    {
        Owner = owner; Title = title; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Background = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24)
        });
        var error = new TextBlock
        {
            Foreground = (Brush)FindResource("Danger"), TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        foreach (var (label, value) in fields)
        {
            var input = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 18), MaxLength = 160 };
            AutomationProperties.SetName(input, label);
            panel.Children.Add(new Label
            {
                Content = label, Target = input, Padding = new Thickness(0), FontWeight = FontWeights.SemiBold
            });
            input.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;
            _inputs.Add(input); panel.Children.Add(input);
        }
        panel.Children.Add(error);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Style = (Style)FindResource("SecondaryButton") };
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 100, Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) =>
        {
            var empty = _inputs.FirstOrDefault(t => string.IsNullOrWhiteSpace(t.Text));
            if (empty is not null)
            {
                error.Text = $"{AutomationProperties.GetName(empty)} is required.";
                error.Visibility = Visibility.Visible;
                empty.Focus();
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Loaded += (_, _) => { if (_inputs.Count > 0) { _inputs[0].Focus(); _inputs[0].SelectAll(); } };
    }
}
