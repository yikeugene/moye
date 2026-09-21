using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Moye.Controls;

/// <summary>A private, live color preview. RGB edits retain the caller's original alpha.</summary>
public sealed class ColorPickerSurface : StackPanel
{
    private readonly List<(Button Button, Color Color)> _swatches = [];
    private readonly Border _newColor = new();
    private readonly TextBlock _description = new();
    private HsvColor _hsv;
    private bool _updating;
    public Color InitialColor { get; }
    public Color SelectedColor { get; private set; }
    public HsvColor Hsv => _hsv;
    public SaturationValuePlane ColorField { get; } = new() { Height = 150, Margin = new Thickness(1, 12, 1, 4) };
    public Slider HueSlider { get; } = new() { Minimum = 0, Maximum = 360, SmallChange = 1, LargeChange = 15, Height = 44, MinHeight = 44, IsMoveToPointEnabled = true };
    public event EventHandler? ColorChanged;

    public ColorPickerSurface(Color initialColor)
    {
        InitialColor = SelectedColor = initialColor;
        _hsv = HsvColor.FromColor(initialColor);
        MinWidth = 320;
        Children.Add(Label("Palette"));
        var palette = new WrapPanel { Margin = new Thickness(-2, 3, -2, 0) };
        var colors = new (string Name, Color Color)[]
        {
            ("Ink", Color.FromRgb(37, 51, 74)), ("Gray", Color.FromRgb(107, 114, 128)), ("Black", Colors.Black), ("White", Colors.White),
            ("Blue", Color.FromRgb(50, 106, 232)), ("Sky blue", Color.FromRgb(14, 165, 233)), ("Teal", Color.FromRgb(15, 118, 110)), ("Green", Color.FromRgb(34, 197, 94)),
            ("Red", Color.FromRgb(229, 72, 77)), ("Orange", Color.FromRgb(249, 115, 22)), ("Amber", Color.FromRgb(234, 179, 8)), ("Yellow", Color.FromRgb(255, 241, 118)),
            ("Purple", Color.FromRgb(139, 92, 246)), ("Magenta", Color.FromRgb(217, 70, 239)), ("Pink", Color.FromRgb(236, 72, 153)), ("Brown", Color.FromRgb(139, 94, 60))
        };
        foreach (var (name, color) in colors)
        {
            var button = new Button
            {
                Width = 44, Height = 44, MinWidth = 44, MinHeight = 44,
                Margin = new Thickness(2), Padding = new Thickness(5), ToolTip = name,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2), Background = Brushes.Transparent,
                Content = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(color), BorderBrush = new SolidColorBrush(Color.FromRgb(193, 202, 215)), BorderThickness = new Thickness(1) },
                Template = SwatchButtonTemplate()
            };
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => SelectColor(color);
            _swatches.Add((button, color)); palette.Children.Add(button);
        }
        Children.Add(palette);
        Children.Add(ColorField);
        ColorField.SelectionChanged += (_, _) => SetSaturationValue(ColorField.Saturation, ColorField.Value);
        Children.Add(new TextBlock { Text = "Hue", FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 6, 0, 0) });
        HueSlider.Template = HueSliderTemplate();
        AutomationProperties.SetName(HueSlider, "Hue");
        AutomationProperties.SetHelpText(HueSlider, "Choose a hue. Use left and right arrow keys for fine adjustment.");
        HueSlider.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _hsv = _hsv with { Hue = HsvColor.NormalizeHue(HueSlider.Value) };
            UpdateColor(syncHue: false);
        };
        Children.Add(HueSlider);

        var comparison = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        comparison.ColumnDefinitions.Add(new()); comparison.ColumnDefinitions.Add(new());
        var current = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        current.Children.Add(Label("Current"));
        var reset = new Button
        {
            Margin = new Thickness(0, 5, 0, 0), MinHeight = 44, Height = 44, Padding = new Thickness(0),
            Background = CreateCheckerboardBrush(), BorderBrush = new SolidColorBrush(Color.FromRgb(211, 219, 231)), BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Border { Background = new SolidColorBrush(initialColor), Height = 42 },
            Template = ComparisonButtonTemplate()
        };
        AutomationProperties.SetName(reset, "Restore current color");
        reset.ToolTip = "Restore current color";
        reset.Click += (_, _) => SelectColor(InitialColor);
        current.Children.Add(reset); comparison.Children.Add(current);
        var preview = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        preview.Children.Add(Label("New"));
        var checker = new Border { Background = CreateCheckerboardBrush(), Height = 44, Margin = new Thickness(0, 5, 0, 0), BorderBrush = new SolidColorBrush(Color.FromRgb(211, 219, 231)), BorderThickness = new Thickness(1), Child = _newColor };
        preview.Children.Add(checker); Grid.SetColumn(preview, 1); comparison.Children.Add(preview); Children.Add(comparison);
        _description.FontSize = 12; _description.Foreground = Brushes.SlateGray; _description.TextWrapping = TextWrapping.Wrap;
        _description.Margin = new Thickness(0, 10, 0, 0);
        Children.Add(_description);
        AutomationProperties.SetLiveSetting(_description, AutomationLiveSetting.Polite);
        RefreshPreview(syncHue: true);
    }

    public void SelectColor(Color color)
    {
        var next = HsvColor.FromColor(color);
        // Gray has no intrinsic hue; retain the last hue so adding saturation
        // again returns to the color the user was adjusting.
        _hsv = next.Saturation == 0 ? next with { Hue = _hsv.Hue } : next;
        UpdateColor(syncHue: true);
    }

    public void SetHue(double hue)
    {
        _hsv = _hsv with { Hue = HsvColor.NormalizeHue(hue) };
        UpdateColor(syncHue: true);
    }

    public void SetSaturationValue(double saturation, double value)
    {
        _hsv = _hsv with { Saturation = HsvColor.Unit(saturation), Value = HsvColor.Unit(value) };
        UpdateColor(syncHue: false);
    }

    private void UpdateColor(bool syncHue)
    {
        var color = _hsv.ToColor(InitialColor.A);
        var changed = color != SelectedColor;
        SelectedColor = color;
        RefreshPreview(syncHue);
        if (changed) ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshPreview(bool syncHue)
    {
        _updating = true;
        try
        {
            if (syncHue) HueSlider.Value = _hsv.Hue;
            ColorField.SetColor(_hsv);
            _newColor.Background = new SolidColorBrush(SelectedColor);
            foreach (var (button, color) in _swatches)
            {
                bool selected = color.R == SelectedColor.R && color.G == SelectedColor.G && color.B == SelectedColor.B;
                button.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(50, 106, 232)) : Brushes.Transparent;
                AutomationProperties.SetItemStatus(button, selected ? "Selected" : "");
            }
            _description.Text = InitialColor.A == 255
                ? "Arrow keys adjust the color field. Hold Shift for larger steps."
                : $"Opacity stays at {InitialColor.A / 255d:P0}. Arrow keys adjust the color field.";
            AutomationProperties.SetName(_newColor, $"New color: red {SelectedColor.R}, green {SelectedColor.G}, blue {SelectedColor.B}, opacity {SelectedColor.A / 255d:P0}");
        }
        finally { _updating = false; }
    }

    public static Brush CreateCheckerboardBrush()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var gray = new SolidColorBrush(Color.FromRgb(220, 225, 233));
        drawing.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        drawing.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16), Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = Brushes.SlateGray };

    private static ControlTemplate ComparisonButtonTemplate() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4">
            <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#326AE8"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#326AE8"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static ControlTemplate SwatchButtonTemplate() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="10">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#EEF3FF"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#25334A"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static ControlTemplate HueSliderTemplate() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid Height="44">
            <Border Height="16" CornerRadius="8" Margin="22,0" VerticalAlignment="Center">
              <Border.Background><LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                <GradientStop Offset="0" Color="Red"/><GradientStop Offset="0.166667" Color="Yellow"/><GradientStop Offset="0.333333" Color="Lime"/>
                <GradientStop Offset="0.5" Color="Cyan"/><GradientStop Offset="0.666667" Color="Blue"/><GradientStop Offset="0.833333" Color="Magenta"/><GradientStop Offset="1" Color="Red"/>
              </LinearGradientBrush></Border.Background>
            </Border>
            <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" IsDirectionReversed="False">
              <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb Width="44" Height="44"><Thumb.Template><ControlTemplate TargetType="Thumb"><Grid Background="Transparent"><Border Width="18" Height="28" Background="White" BorderBrush="#25334A" BorderThickness="2" CornerRadius="8"/></Grid></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
            </Track>
            <Border x:Name="Focus" IsHitTestVisible="False" BorderBrush="Transparent" BorderThickness="2" CornerRadius="8"/>
          </Grid>
          <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="BorderBrush" Value="#326AE8"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
}

/// <summary>A two-axis field with one active pointer and keyboard equivalents.</summary>
public sealed class SaturationValuePlane : FrameworkElement
{
    private HsvColor _color;
    private bool _mouseDragging;
    private StylusDevice? _stylus;
    private TouchDevice? _touch;
    public double Saturation => _color.Saturation;
    public double Value => _color.Value;
    public event EventHandler? SelectionChanged;

    public SaturationValuePlane()
    {
        Focusable = true; Cursor = Cursors.Cross; MinHeight = 44; MinWidth = 44;
        KeyboardNavigation.SetIsTabStop(this, true);
        AutomationProperties.SetName(this, "Saturation and brightness");
        AutomationProperties.SetHelpText(this, "Left and right adjust saturation. Up and down adjust brightness. Hold Shift for larger steps.");
        GotKeyboardFocus += (_, _) => InvalidateVisual(); LostKeyboardFocus += (_, _) => InvalidateVisual();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice is not null || _stylus is not null || _touch is not null) return;
            Focus(); _mouseDragging = CaptureMouse(); SelectPoint(e.GetPosition(this)); e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (!_mouseDragging || e.StylusDevice is not null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { ReleaseMouseCapture(); _mouseDragging = false; return; }
            SelectPoint(e.GetPosition(this)); e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_mouseDragging || e.StylusDevice is not null) return;
            SelectPoint(e.GetPosition(this)); _mouseDragging = false; ReleaseMouseCapture(); e.Handled = true;
        };
        LostMouseCapture += (_, _) => _mouseDragging = false;
        StylusDown += (_, e) =>
        {
            if (e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus) return;
            if (_stylus is not null || _touch is not null || _mouseDragging) { e.Handled = true; return; }
            Focus(); _stylus = e.StylusDevice.Capture(this) ? e.StylusDevice : null;
            SelectPoint(e.GetPosition(this)); e.Handled = true;
        };
        StylusMove += (_, e) => { if (_stylus == e.StylusDevice) { SelectPoint(e.GetPosition(this)); e.Handled = true; } };
        StylusUp += (_, e) =>
        {
            if (_stylus != e.StylusDevice) return;
            SelectPoint(e.GetPosition(this)); _stylus = null; e.StylusDevice.Capture(null); e.Handled = true;
        };
        LostStylusCapture += (_, e) => { if (_stylus == e.StylusDevice) _stylus = null; };
        TouchDown += (_, e) =>
        {
            e.Handled = true;
            if (_touch is not null || _stylus is not null || _mouseDragging) return;
            Focus(); _touch = e.TouchDevice.Capture(this) ? e.TouchDevice : null; SelectPoint(e.GetTouchPoint(this).Position);
        };
        TouchMove += (_, e) => { e.Handled = true; if (_touch == e.TouchDevice) SelectPoint(e.GetTouchPoint(this).Position); };
        TouchUp += (_, e) =>
        {
            e.Handled = true;
            if (_touch != e.TouchDevice) return;
            SelectPoint(e.GetTouchPoint(this).Position); _touch = null; e.TouchDevice.Capture(null);
        };
        LostTouchCapture += (_, e) => { if (_touch == e.TouchDevice) _touch = null; };
        Unloaded += (_, _) => ReleasePointer();
        KeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : .01;
            switch (e.Key)
            {
                case Key.Left: MoveSelection(-step, 0); break;
                case Key.Right: MoveSelection(step, 0); break;
                case Key.Up: MoveSelection(0, step); break;
                case Key.Down: MoveSelection(0, -step); break;
                case Key.Home: Select(0, 1); break;
                case Key.End: Select(1, 0); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    public void SetColor(HsvColor color)
    {
        _color = new(HsvColor.NormalizeHue(color.Hue), HsvColor.Unit(color.Saturation), HsvColor.Unit(color.Value));
        AutomationProperties.SetItemStatus(this, $"Saturation {Saturation:P0}, brightness {Value:P0}");
        InvalidateVisual();
    }
    public void SelectPoint(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        Select(point.X / ActualWidth, 1 - point.Y / ActualHeight);
    }
    public void MoveSelection(double saturationDelta, double valueDelta) => Select(Saturation + saturationDelta, Value + valueDelta);
    private void Select(double saturation, double value)
    {
        var next = _color with { Saturation = HsvColor.Unit(saturation), Value = HsvColor.Unit(value) };
        if (next == _color) return;
        SetColor(next); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ReleasePointer()
    {
        var stylus = _stylus; var touch = _touch;
        _stylus = null; _touch = null; _mouseDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (stylus?.Captured == this) stylus.Capture(null);
        if (touch?.Captured == this) touch.Capture(null);
    }
    protected override void OnRender(DrawingContext context)
    {
        var rect = new Rect(RenderSize);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        context.PushClip(new RectangleGeometry(rect, 10, 10));
        context.DrawRectangle(new SolidColorBrush(new HsvColor(_color.Hue, 1, 1).ToColor()), null, rect);
        context.DrawRectangle(new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)), null, rect);
        context.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)), null, rect);
        context.Pop();
        context.DrawRoundedRectangle(null, new Pen(IsKeyboardFocused ? new SolidColorBrush(Color.FromRgb(50, 106, 232)) : new SolidColorBrush(Color.FromRgb(194, 204, 219)), IsKeyboardFocused ? 3 : 1), rect, 10, 10);
        var marker = new Point(Math.Clamp(Saturation * rect.Width, 8, Math.Max(8, rect.Width - 8)), Math.Clamp((1 - Value) * rect.Height, 8, Math.Max(8, rect.Height - 8)));
        context.DrawEllipse(null, new Pen(Brushes.Black, 4), marker, 6, 6);
        context.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 6, 6);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ColorFieldAutomationPeer(this);
    private sealed class ColorFieldAutomationPeer(SaturationValuePlane owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(SaturationValuePlane);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }
}
