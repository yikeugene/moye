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

/// <summary>Visual width adjustment. Preview changes continuously; one pointer gesture commits once.</summary>
public sealed class StrokeWidthPicker : UserControl
{
    private readonly Slider _slider = new()
    {
        Minimum = WritingPreferences.MinimumWidth, Maximum = WritingPreferences.MaximumWidth,
        Value = 1.7008, SmallChange = .1, LargeChange = 1, MinHeight = 44,
        IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _value = new() { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly InkSample _sample = new() { Height = 62, Margin = new Thickness(0, 4, 0, 0) };
    private bool _synchronizing, _adjusting;
    private int _gestureRevision;
    private double _committedWidth = 1.7008;
    public event EventHandler? StrokeWidthChanged;
    public event EventHandler? StrokeWidthCommitted;

    public double StrokeWidth
    {
        get => _slider.Value;
        set
        {
            if (!double.IsFinite(value)) return;
            _gestureRevision++; _adjusting = false;
            _synchronizing = true;
            try { _slider.Value = Math.Clamp(value, _slider.Minimum, _slider.Maximum); _committedWidth = _slider.Value; }
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
        panel.Children.Add(heading); panel.Children.Add(_slider); panel.Children.Add(_sample); Content = panel;
        AutomationProperties.SetName(_slider, "Stroke thickness");
        _slider.SetResourceReference(StyleProperty, "TouchSlider");
        _slider.ToolTip = "Drag to preview thickness. Arrow keys adjust in small steps.";
        _slider.ValueChanged += (_, _) =>
        {
            RefreshPreview();
            if (_synchronizing) return;
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
        RefreshPreview();
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
        AutomationProperties.SetHelpText(_slider, "Current thickness: " + _value.Text);
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
