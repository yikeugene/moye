using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Moye.Controls;

/// <summary>Selection chrome overlays content, so model and rendered origins coincide.</summary>
internal sealed class NoteItemFrame : Grid
{
    private readonly Border _outline;
    private readonly Thumb _move;
    private readonly Thumb _resize;
    public object Item { get; }
    public event EventHandler? Selected;
    public event EventHandler? GeometryChanged;
    public event EventHandler? EditCompleted;
    public bool IsSelected { get; private set; }
    public FrameworkElement ItemContent { get; }

    public NoteItemFrame(object item, FrameworkElement content)
    {
        Item = item;
        ItemContent = content;
        Background = Brushes.Transparent;
        Children.Add(content);
        _outline = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(51, 111, 232)), BorderThickness = new Thickness(1.5), IsHitTestVisible = false };
        _move = Handle("↔", Cursors.SizeAll, "Drag to move", HorizontalAlignment.Right, VerticalAlignment.Top);
        _resize = Handle("◢", Cursors.SizeNWSE, "Drag to resize", HorizontalAlignment.Right, VerticalAlignment.Bottom);
        Children.Add(_outline);
        Children.Add(_move);
        Children.Add(_resize);
        _move.DragDelta += (_, e) =>
        {
            var parent = Parent as Canvas;
            Canvas.SetLeft(this, Math.Clamp(Canvas.GetLeft(this) + e.HorizontalChange, 0, Math.Max(0, (parent?.ActualWidth ?? double.MaxValue) - Width)));
            Canvas.SetTop(this, Math.Clamp(Canvas.GetTop(this) + e.VerticalChange, 0, Math.Max(0, (parent?.ActualHeight ?? double.MaxValue) - Height)));
            GeometryChanged?.Invoke(this, EventArgs.Empty);
        };
        _resize.DragDelta += (_, e) =>
        {
            var parent = Parent as Canvas;
            Width = Math.Clamp(Width + e.HorizontalChange, 60, Math.Max(60, (parent?.ActualWidth ?? double.MaxValue) - Canvas.GetLeft(this)));
            Height = Math.Clamp(Height + e.VerticalChange, 44, Math.Max(44, (parent?.ActualHeight ?? double.MaxValue) - Canvas.GetTop(this)));
            GeometryChanged?.Invoke(this, EventArgs.Empty);
        };
        _move.DragCompleted += (_, _) => EditCompleted?.Invoke(this, EventArgs.Empty);
        _resize.DragCompleted += (_, _) => EditCompleted?.Invoke(this, EventArgs.Empty);
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!PenInkCanvas.IsTouch(e.StylusDevice)) Selected?.Invoke(this, EventArgs.Empty);
        };
        SetSelected(false);
    }

    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        var visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        _outline.Visibility = _move.Visibility = _resize.Visibility = visibility;
    }

    private static Thumb Handle(string label, Cursor cursor, string tooltip, HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        var background = new FrameworkElementFactory(typeof(Border));
        background.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(224, 228, 238, 255)));
        background.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, label);
        text.SetValue(TextBlock.FontSizeProperty, 22.0);
        text.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(40, 95, 200)));
        text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        background.AppendChild(text);
        return new Thumb
        {
            Width = 44, Height = 44, Cursor = cursor, ToolTip = tooltip,
            HorizontalAlignment = horizontal, VerticalAlignment = vertical,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = background }
        };
    }
}
