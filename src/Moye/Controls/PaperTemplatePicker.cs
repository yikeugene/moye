using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Moye.Models;

namespace Moye.Controls;

/// <summary>A visual, keyboard-accessible choice of the supported paper backgrounds.</summary>
public sealed class PaperTemplatePicker : UserControl
{
    public static readonly DependencyProperty SelectedTemplateProperty = DependencyProperty.Register(
        nameof(SelectedTemplate), typeof(PaperTemplate), typeof(PaperTemplatePicker),
        new FrameworkPropertyMetadata(PaperTemplate.Ruled,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedTemplateChanged));

    private readonly Dictionary<PaperTemplate, RadioButton> _choices = [];

    public PaperTemplate SelectedTemplate
    {
        get => (PaperTemplate)GetValue(SelectedTemplateProperty);
        set => SetValue(SelectedTemplateProperty, value);
    }

    public event EventHandler? SelectionChanged;

    public PaperTemplatePicker()
    {
        MinWidth = 240;
        var group = new UniformGrid { Columns = 3 };
        var groupName = $"PaperTemplates_{Guid.NewGuid():N}";
        AddChoice(group, groupName, PaperTemplate.Plain, "Blank", "Free-form notes");
        AddChoice(group, groupName, PaperTemplate.Ruled, "Ruled", "Everyday writing");
        AddChoice(group, groupName, PaperTemplate.Grid, "Grid", "Diagrams & maths");
        Content = group;
        UpdateSelection();
    }

    private void AddChoice(Panel group, string groupName, PaperTemplate template, string name, string description)
    {
        var content = new StackPanel();
        var paper = new Border
        {
            Width = 62, Height = 88,
            BorderBrush = Brush("#D7DFEA"), BorderThickness = new Thickness(1),
            Background = Brushes.White, Margin = new Thickness(0, 0, 0, 10),
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = new PaperVisual { Width = 793.700787, Height = 1122.519685, Template = template }
            }
        };
        content.Children.Add(paper);
        content.Children.Add(new TextBlock
        {
            Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = description, FontSize = 11, Foreground = Brush("#66748A"),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0), MinHeight = 30
        });

        var choice = new RadioButton
        {
            GroupName = groupName, Content = content, MinHeight = 172,
            Margin = new Thickness(3), Padding = new Thickness(6, 12, 6, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Style = ChoiceStyle(), ToolTip = $"{name} paper — {description}"
        };
        AutomationProperties.SetName(choice, $"{name} paper");
        AutomationProperties.SetHelpText(choice, description);
        choice.Checked += (_, _) => SetCurrentValue(SelectedTemplateProperty, template);
        _choices[template] = choice;
        group.Children.Add(choice);
    }

    private static Style ChoiceStyle()
    {
        var style = new Style(typeof(RadioButton));
        style.Setters.Add(new Setter(ForegroundProperty, Brush("#25334A")));
        style.Setters.Add(new Setter(BackgroundProperty, Brush("#F8FAFD")));
        style.Setters.Add(new Setter(BorderBrushProperty, Brush("#E0E6EF")));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(2)));

        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentProperty));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        style.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(RadioButton)) { VisualTree = chrome }));

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, Brush("#F0F5FF")));
        style.Triggers.Add(hover);
        var selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(BorderBrushProperty, Brush("#326AE8")));
        selected.Setters.Add(new Setter(BackgroundProperty, Brush("#EDF3FF")));
        selected.Setters.Add(new Setter(ForegroundProperty, Brush("#245CCF")));
        style.Triggers.Add(selected);
        var focused = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(BorderBrushProperty, Brush("#1744A6")));
        style.Triggers.Add(focused);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .45));
        style.Triggers.Add(disabled);
        return style;
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static void OnSelectedTemplateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var picker = (PaperTemplatePicker)sender;
        picker.UpdateSelection();
        picker.SelectionChanged?.Invoke(picker, EventArgs.Empty);
    }

    private void UpdateSelection()
    {
        foreach (var (template, choice) in _choices)
            choice.IsChecked = template == SelectedTemplate;
    }
}
