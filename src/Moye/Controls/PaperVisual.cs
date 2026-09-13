using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;

namespace Moye.Controls;

/// <summary>Shared document-coordinate paper marks for screen, thumbnails, and vector PDF output.</summary>
public static class PaperPattern
{
    public readonly record struct Line(Point Start, Point End, Color Color, double Thickness);
    public readonly record struct Dot(Point Center, Color Color, double Radius);
    private static readonly Color RuleColor = Color.FromRgb(221, 231, 242);
    private static readonly Color GuideColor = Color.FromRgb(190, 207, 228);

    public static IEnumerable<Line> Lines(PaperTemplate template, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) yield break;
        if (template == PaperTemplate.Ruled && width > 80)
        {
            for (double y = 80; y < height - 32; y += 32)
                yield return new(new Point(40, y), new Point(width - 40, y), RuleColor, .7);
        }
        else if (template is PaperTemplate.Grid or PaperTemplate.Graph)
        {
            var spacing = template == PaperTemplate.Grid ? 24d : 12d;
            for (var i = 1; i * spacing < width; i++)
            {
                var major = template == PaperTemplate.Graph && i % 5 == 0;
                yield return new(new Point(i * spacing, 0), new Point(i * spacing, height),
                    major ? GuideColor : RuleColor, major ? 1 : template == PaperTemplate.Graph ? .5 : .7);
            }
            for (var i = 1; i * spacing < height; i++)
            {
                var major = template == PaperTemplate.Graph && i % 5 == 0;
                yield return new(new Point(0, i * spacing), new Point(width, i * spacing),
                    major ? GuideColor : RuleColor, major ? 1 : template == PaperTemplate.Graph ? .5 : .7);
            }
        }
        else if (template == PaperTemplate.Cornell)
        {
            var left = Math.Min(40, width * .1);
            var right = width - left;
            var top = Math.Min(80, height * .15);
            var bottom = height - Math.Min(40, height * .08);
            var summary = top + (bottom - top) * .78;
            var divider = left + (right - left) * .28;
            yield return new(new Point(left, top), new Point(right, top), GuideColor, 1);
            yield return new(new Point(left, summary), new Point(right, summary), GuideColor, 1);
            yield return new(new Point(divider, top), new Point(divider, summary), GuideColor, 1);
            var notesLeft = divider + Math.Min(16, (right - divider) * .06);
            for (var y = top + 32; y < summary - 8; y += 32)
                yield return new(new Point(notesLeft, y), new Point(right, y), RuleColor, .7);
        }
    }

    public static IEnumerable<Dot> Dots(PaperTemplate template, double width, double height)
    {
        if (template != PaperTemplate.DotGrid || !double.IsFinite(width) || !double.IsFinite(height)) yield break;
        for (double y = 24; y <= height - 24; y += 24)
            for (double x = 24; x <= width - 24; x += 24)
                yield return new(new Point(x, y), Color.FromRgb(174, 191, 212), 1.3);
    }

    public static void Draw(DrawingContext drawing, PaperTemplate template, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        var bounds = new Rect(0, 0, width, height);
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawRectangle(Brushes.White, null, bounds);
        var pens = new Dictionary<(Color, double), Pen>();
        foreach (var line in Lines(template, width, height))
        {
            if (!pens.TryGetValue((line.Color, line.Thickness), out var pen))
            {
                pen = new Pen(new SolidColorBrush(line.Color), line.Thickness);
                pen.Freeze();
                pens[(line.Color, line.Thickness)] = pen;
            }
            drawing.DrawLine(pen, line.Start, line.End);
        }
        SolidColorBrush? dotBrush = null;
        foreach (var dot in Dots(template, width, height))
        {
            if (dotBrush is null || dotBrush.Color != dot.Color)
            {
                dotBrush = new SolidColorBrush(dot.Color);
                dotBrush.Freeze();
            }
            drawing.DrawEllipse(dotBrush, null, dot.Center, dot.Radius, dot.Radius);
        }
        drawing.Pop();
    }
}

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
        if (_pdf is not null)
        {
            dc.DrawRectangle(Brushes.White, null, bounds);
            dc.DrawImage(_pdf, bounds);
            return;
        }
        PaperPattern.Draw(dc, _template, ActualWidth, ActualHeight);
    }
}
