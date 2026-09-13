using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;

namespace Moye.Controls;

/// <summary>Paper is drawn in document DIP coordinates; it never owns input.</summary>
public sealed class PaperVisual : FrameworkElement
{
    private PaperTemplate _template;
    private BitmapSource? _pdf;
    public PaperTemplate Template { get => _template; set { _template = value; InvalidateVisual(); } }
    public BitmapSource? PdfBackground { get => _pdf; set { _pdf = value; InvalidateVisual(); } }

    public PaperVisual() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.White, null, bounds);
        if (_pdf is not null)
        {
            dc.DrawImage(_pdf, bounds);
            return;
        }
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(221, 231, 242)), .7);
        pen.Freeze();
        if (_template == PaperTemplate.Ruled)
        {
            for (double y = 80; y < ActualHeight - 32; y += 32)
                dc.DrawLine(pen, new Point(40, y), new Point(ActualWidth - 40, y));
        }
        else if (_template == PaperTemplate.Grid)
        {
            for (double x = 24; x < ActualWidth; x += 24)
                dc.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
            for (double y = 24; y < ActualHeight; y += 24)
                dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
        }
    }
}
