using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moye.Models;

namespace Moye.Controls;

/// <summary>Stepped width adjustment with a live preview; one pointer gesture commits once.</summary>
public sealed class StrokeWidthPicker : UserControl
{
    // Equally spaced slider positions make the fine pen sizes as easy to reach
    // as broader highlighter sizes. Persisted widths remain in page DIP.
    private static readonly double[] WidthSteps =
    [
        WritingPreferences.MinimumWidth,
        .. new[] { .15, .2, .25, .3, .35, .4, .45, .5, .6, .8, 1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6 }
            .Select(mm => mm * 96 / 25.4),
        WritingPreferences.MaximumWidth
    ];
    private readonly Slider _slider = new()
    {
        Minimum = 0, Maximum = WidthSteps.Length - 1,
        SmallChange = 1, LargeChange = 3, TickFrequency = 1, IsSnapToTickEnabled = true, MinHeight = 44,
        IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _value = new() { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly InkSample _sample = new() { Height = 62, Margin = new Thickness(0, 4, 0, 0) };
    private bool _synchronizing, _adjusting;
    private int _gestureRevision;
    private double _strokeWidth = 1.7008, _committedWidth = 1.7008;
    public event EventHandler? StrokeWidthChanged;
    public event EventHandler? StrokeWidthCommitted;

    public double StrokeWidth
    {
        get => _strokeWidth;
        set
        {
            if (!double.IsFinite(value)) return;
            _gestureRevision++; _adjusting = false;
            _synchronizing = true;
            try
            {
                // Opening settings or changing another preset field must not
                // round an existing width. Snap only deliberate slider edits.
                _strokeWidth = Math.Clamp(value, WritingPreferences.MinimumWidth, WritingPreferences.MaximumWidth);
                _slider.Value = PositionForWidth(_strokeWidth);
                _committedWidth = _strokeWidth;
            }
            finally { _synchronizing = false; }
            RefreshPreview();
        }
    }

    public StrokeWidthPicker()
    {
        var panel = new StackPanel();
        var heading = new DockPanel();
        DockPanel.SetDock(_value, Dock.Right); heading.Children.Add(_value);
        heading.Children.Add(new TextBlock { Text = "Thickness", Foreground = Brushes.SlateGray, FontSize = 12 });
        panel.Children.Add(heading); panel.Children.Add(_slider);
        panel.Children.Add(new TickBar
        {
            Minimum = _slider.Minimum, Maximum = _slider.Maximum, TickFrequency = 1,
            Placement = TickBarPlacement.Bottom, Height = 6, Margin = new Thickness(22, 0, 22, 0),
            Fill = Brushes.SlateGray, IsHitTestVisible = false
        });
        panel.Children.Add(_sample); Content = panel;
        AutomationProperties.SetName(_slider, "Stroke thickness");
        _slider.SetResourceReference(StyleProperty, "TouchSlider");
        _slider.ToolTip = "Drag between fixed thickness levels. Arrow keys choose the next size.";
        _slider.CommandBindings.Add(new CommandBinding(Slider.IncreaseSmall, (_, e) =>
        { MoveToAdjacentStep(true); e.Handled = true; }));
        _slider.CommandBindings.Add(new CommandBinding(Slider.DecreaseSmall, (_, e) =>
        { MoveToAdjacentStep(false); e.Handled = true; }));
        _slider.ValueChanged += (_, _) =>
        {
            if (_synchronizing) return;
            var index = (int)Math.Round(_slider.Value, MidpointRounding.AwayFromZero);
            // WPF snaps pointer/keyboard input; also normalize accessibility
            // range-value edits, which may otherwise supply fractional levels.
            _synchronizing = true;
            try { _slider.Value = index; }
            finally { _synchronizing = false; }
            if (_strokeWidth == WidthSteps[index]) return;
            _strokeWidth = WidthSteps[index];
            RefreshPreview();
            StrokeWidthChanged?.Invoke(this, EventArgs.Empty);
            if (!_adjusting) CommitPendingWidth();
        };
        _slider.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        { if (e.ChangedButton == MouseButton.Left) BeginAdjustment(); }), true);
        _slider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        { if (e.ChangedButton == MouseButton.Left) QueueCommit(); }), true);
        _slider.AddHandler(Stylus.PreviewStylusDownEvent, new StylusDownEventHandler((_, _) => BeginAdjustment()), true);
        _slider.AddHandler(Stylus.PreviewStylusUpEvent, new StylusEventHandler((_, _) => QueueCommit()), true);
        _slider.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, _) => BeginAdjustment()), true);
        _slider.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>((_, _) => QueueCommit()), true);
        _slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginAdjustment()), true);
        _slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitPendingWidth()), true);
        _slider.LostMouseCapture += (_, _) => { if (!_slider.IsMouseCaptureWithin) QueueCommit(); };
        _slider.LostKeyboardFocus += (_, _) => CommitPendingWidth();
        Unloaded += (_, _) => CommitPendingWidth();
        StrokeWidth = _strokeWidth;
    }

    private static double PositionForWidth(double width)
    {
        for (var i = 1; i < WidthSteps.Length; i++)
            if (width <= WidthSteps[i])
                return i - 1 + (width - WidthSteps[i - 1]) / (WidthSteps[i] - WidthSteps[i - 1]);
        return WidthSteps.Length - 1;
    }

    private void MoveToAdjacentStep(bool increase)
    {
        // A loaded custom width may lie between levels. The first arrow press
        // must choose the next size in that direction, without skipping it.
        var position = increase ? Math.Floor(_slider.Value) + 1 : Math.Ceiling(_slider.Value) - 1;
        _slider.Value = Math.Clamp(position, _slider.Minimum, _slider.Maximum);
    }

    private void BeginAdjustment() { _gestureRevision++; _adjusting = true; }

    private void QueueCommit()
    {
        var revision = _gestureRevision;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        { if (revision == _gestureRevision) CommitPendingWidth(); }));
    }

    public void CommitPendingWidth()
    {
        _gestureRevision++;
        _adjusting = false;
        if (_synchronizing || _committedWidth == StrokeWidth) return;
        _committedWidth = StrokeWidth;
        StrokeWidthCommitted?.Invoke(this, EventArgs.Empty);
    }

    public void ConfigurePreview(Color color, bool highlighter, double opacity, bool pressure, bool smoothing)
    {
        _sample.Color = color; _sample.Highlighter = highlighter;
        _sample.InkOpacity = double.IsFinite(opacity) ? Math.Clamp(opacity, .1, 1) : 1;
        _sample.Pressure = pressure; _sample.Smoothing = smoothing;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _value.Text = (StrokeWidth * 25.4 / 96).ToString("0.##", CultureInfo.CurrentCulture) + " mm";
        AutomationProperties.SetHelpText(_slider, "Current thickness: " + _value.Text +
            $". {WidthSteps.Length} thickness levels; arrow keys choose the next size.");
        _sample.StrokeWidth = StrokeWidth; _sample.InvalidateVisual();
    }

    private sealed class InkSample : FrameworkElement
    {
        public Color Color { get; set; } = Colors.Black;
        public bool Highlighter { get; set; }
        public double InkOpacity { get; set; } = 1;
        public bool Pressure { get; set; } = true;
        public bool Smoothing { get; set; } = true;
        public double StrokeWidth { get; set; } = 1.7008;
        protected override void OnRender(DrawingContext drawing)
        {
            var bounds = new Rect(RenderSize);
            if (bounds.Width < 40 || bounds.Height < 30) return;
            drawing.PushClip(new RectangleGeometry(bounds, 8, 8));
            drawing.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(222, 228, 237)), 1), bounds, 8, 8);
            var rule = new Pen(new SolidColorBrush(Color.FromRgb(236, 240, 246)), 1);
            drawing.DrawLine(rule, new Point(12, 22), new Point(bounds.Width - 12, 22));
            drawing.DrawLine(rule, new Point(12, 43), new Point(bounds.Width - 12, 43));
            var points = new StylusPointCollection();
            for (int i = 0; i <= 60; i++)
            {
                var t = i / 60d;
                points.Add(new StylusPoint(20 + (bounds.Width - 40) * t, 32 - 9 * Math.Sin(t * Math.PI * 2), (float)(.55 + .45 * Math.Sin(t * Math.PI))));
            }
            new Stroke(points, new DrawingAttributes
            {
                Width = StrokeWidth, Height = StrokeWidth,
                Color = Color.FromArgb((byte)Math.Round(Color.A * (Highlighter ? 1 : InkOpacity)), Color.R, Color.G, Color.B),
                IsHighlighter = Highlighter, IgnorePressure = !Pressure, FitToCurve = Smoothing,
                StylusTip = Highlighter ? StylusTip.Rectangle : StylusTip.Ellipse
            }).Draw(drawing);
            drawing.Pop();
        }
    }
}
