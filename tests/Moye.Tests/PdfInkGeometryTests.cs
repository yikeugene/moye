using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;
using Moye.Services;
using PdfSharp.Pdf.IO;

namespace Moye.Tests;

/// <summary>
/// Compares exported vector ink with WPF's actual stroke renderer, excluding
/// antialiased boundaries. A bounding-box check misses white holes inside ink.
/// All documents and assets here are synthetic and stay outside the user library.
/// </summary>
public sealed class PdfInkGeometryTests : IDisposable
{
    private const int PageWidth = 260, PageHeight = 180, RasterScale = 4;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MoyePdfInkGeometry-" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PdfInkGeometryTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("short", false)]
    [InlineData("short", true)]
    [InlineData("curved", false)]
    [InlineData("curved", true)]
    [InlineData("retraced", false)]
    [InlineData("retraced", true)]
    public async Task PressureInkHasNoWhiteHolesAtJoinsOrEndpoints(string shape, bool smoothing)
    {
        var page = new NotePage { Width = PageWidth, Height = PageHeight };
        page.InkData = await Sta(() => Serialize(new StrokeCollection { CreateStroke(shape, smoothing) }));
        var originalInk = page.InkData.ToArray();
        var expected = await Sta(() => RenderNativeInk(page.InkData));
        var name = $"pressure-{shape}-{(smoothing ? "smooth" : "raw")}";
        var actual = await ExportAndRender(page, name, expected);

        AssertInteriorMatches(expected, actual, name);
        Assert.Equal(originalInk, page.InkData); // Export never repairs by mutating editable ink.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HighlighterPressureCapsStayFilledAndOverlapsKeepNativeOpacity(bool overlapping)
    {
        var page = new NotePage { Width = PageWidth, Height = PageHeight };
        page.InkData = await Sta(() =>
        {
            var first = CreateStroke("curved", true);
            first.DrawingAttributes.Color = Colors.Gold;
            first.DrawingAttributes.IsHighlighter = true;
            first.DrawingAttributes.StylusTip = StylusTip.Rectangle;
            var strokes = new StrokeCollection { first };
            if (overlapping)
            {
                var second = first.Clone();
                second.Transform(new Matrix(1, 0, 0, 1, 4, 3), false);
                strokes.Add(second);
            }
            return Serialize(strokes);
        });
        var expected = await Sta(() => RenderNativeInk(page.InkData));
        var name = overlapping ? "highlighter-overlap" : "highlighter-single";
        var actual = await ExportAndRender(page, name, expected);
        AssertInteriorMatches(expected, actual, name);
    }

    [Fact]
    public async Task TranslucentPressureInkKeepsItsNativeOpacityInsideOverlappingOutlinePieces()
    {
        var page = new NotePage { Width = PageWidth, Height = PageHeight };
        page.InkData = await Sta(() =>
        {
            var stroke = CreateStroke("retraced", true);
            stroke.DrawingAttributes.Color = Color.FromArgb(128, 37, 51, 74);
            return Serialize(new StrokeCollection { stroke });
        });
        var expected = await Sta(() => RenderNativeInk(page.InkData));
        var actual = await ExportAndRender(page, "translucent-pressure", expected);
        AssertInteriorMatches(expected, actual, "translucent-pressure");
    }

    [Fact]
    public async Task TextGlyphCountersRemainOpenAfterGeometryFillConversion()
    {
        var text = new NoteText
        {
            X = 20, Y = 35, Width = 220, Height = 110, FontSize = 70,
            FontFamily = "Segoe UI, Microsoft JhengHei", Text = "O8口", Color = "#FF25334A"
        };
        var page = new NotePage { Width = PageWidth, Height = PageHeight, Texts = [text] };
        var expected = await Sta(() => RenderNativeText(text));
        var actual = await ExportAndRender(page, "text-counters", expected);
        var expectedHoles = CountEnclosedWhiteRegions(Pixels(expected));
        var actualHoles = CountEnclosedWhiteRegions(Pixels(actual));
        _output.WriteLine($"Text glyph counters: WPF {expectedHoles}, PDF {actualHoles}.");
        Assert.True(expectedHoles >= 4, "The O, 8 and Chinese square must exercise visible glyph counters.");
        Assert.Equal(expectedHoles, actualHoles);
    }

    private async Task<BitmapSource> ExportAndRender(NotePage page, string name, BitmapSource expected)
    {
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var path = Path.Combine(_directory, name + ".pdf");
        await service.ExportAsync(path, new NotebookDocument { Title = "Synthetic pressure ink regression", Pages = [page] });
        var imported = Assert.Single(await service.ImportAsync(path));
        var actual = await service.RenderAsync(imported, RasterScale);
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            // These fixtures contain only paper, ink and text. Rasterizing the
            // whole page could hide the bug while silently losing vector quality.
            var resources = pdf.Pages[0].Elements.GetDictionary("/Resources");
            var objects = resources?.Elements.GetDictionary("/XObject");
            Assert.True(objects is null || objects.Elements.Count == 0, "Ink/text export must remain vector paths.");
        }
        if (Environment.GetEnvironmentVariable("MOYE_PDF_QA_DIR") is { Length: > 0 } artifacts)
        {
            artifacts = Path.GetFullPath(artifacts);
            Directory.CreateDirectory(artifacts);
            File.Copy(path, Path.Combine(artifacts, name + ".pdf"), true);
            await Sta(() =>
            {
                SavePng(expected, Path.Combine(artifacts, name + "-wpf.png"));
                SavePng(actual, Path.Combine(artifacts, name + "-pdf.png"));
            });
        }
        return actual;
    }

    private void AssertInteriorMatches(BitmapSource expectedImage, BitmapSource actualImage, string name)
    {
        var expected = Pixels(expectedImage);
        var actual = Pixels(actualImage);
        var interior = 0;
        var mismatched = 0;
        // Three pixels at 4x removes renderer-specific edge antialiasing while
        // preserving pressure-cap holes even in short classroom handwriting.
        const int radius = 3;
        for (var y = radius; y < expected.Height - radius; y++)
        for (var x = radius; x < expected.Width - radius; x++)
        {
            var color = expected.At(x, y);
            if (Math.Min(color.R, Math.Min(color.G, color.B)) > 230) continue;
            var solidInterior = true;
            for (var dy = -radius; dy <= radius && solidInterior; dy++)
            for (var dx = -radius; dx <= radius; dx++)
                if (Difference(color, expected.At(x + dx, y + dy)) > 8) { solidInterior = false; break; }
            if (!solidInterior) continue;
            interior++;
            if (Difference(color, actual.At(x, y)) > 35) mismatched++;
        }
        _output.WriteLine($"{name}: {mismatched}/{interior} solid interior pixels disagree with native WPF ink.");
        Assert.True(interior > 100, "The fixture must contain a meaningful solid stroke interior.");
        Assert.True(mismatched <= Math.Max(4, interior / 200),
            $"{name}: {mismatched}/{interior} solid interior pixels were hollow or had the wrong opacity after PDF export.");
    }

    private static Stroke CreateStroke(string shape, bool smoothing)
    {
        var points = new StylusPointCollection();
        if (shape == "short")
        {
            points.Add(new StylusPoint(55, 70, .22f)); points.Add(new StylusPoint(60, 73, .9f));
            points.Add(new StylusPoint(65, 69, .38f)); points.Add(new StylusPoint(69, 72, .7f)); points.Add(new StylusPoint(72, 73, .18f));
        }
        else if (shape == "curved")
        {
            for (var index = 0; index <= 80; index++)
                points.Add(new StylusPoint(40 + index * 2, 80 + Math.Sin(index * .16) * 26,
                    (float)(.12 + .88 * (.5 + .5 * Math.Sin(index * .37)))));
        }
        else
        {
            points.Add(new StylusPoint(60, 110, .2f)); points.Add(new StylusPoint(85, 45, .9f));
            points.Add(new StylusPoint(115, 115, .35f)); points.Add(new StylusPoint(80, 75, 1));
            points.Add(new StylusPoint(135, 75, .25f)); points.Add(new StylusPoint(100, 115, .8f));
            points.Add(new StylusPoint(155, 45, .4f)); points.Add(new StylusPoint(180, 110, .95f));
        }
        return new Stroke(points, new DrawingAttributes
        {
            Color = Color.FromRgb(37, 51, 74), Width = 20, Height = 20,
            IgnorePressure = false, FitToCurve = smoothing
        });
    }

    private static BitmapSource RenderNativeInk(byte[] ink)
    {
        using var stream = new MemoryStream(ink, false);
        var strokes = new StrokeCollection(stream);
        return Render(dc => strokes.Draw(dc));
    }

    private static BitmapSource RenderNativeText(NoteText text)
    {
        var formatted = new FormattedText(text.Text, CultureInfo.GetCultureInfo("zh-HK"), FlowDirection.LeftToRight,
            new Typeface(text.FontFamily), text.FontSize, new SolidColorBrush((Color)ColorConverter.ConvertFromString(text.Color)), 1);
        return Render(dc => dc.DrawText(formatted, new Point(text.X + NoteTextLayout.HorizontalInset, text.Y)));
    }

    private static BitmapSource Render(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(RasterScale, RasterScale));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidth, PageHeight));
            draw(dc);
            dc.Pop();
        }
        var image = new RenderTargetBitmap(PageWidth * RasterScale, PageHeight * RasterScale, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    private static byte[] Serialize(StrokeCollection strokes)
    {
        using var stream = new MemoryStream(); strokes.Save(stream); return stream.ToArray();
    }

    private readonly record struct Pixel(byte R, byte G, byte B);
    private sealed record Raster(int Width, int Height, byte[] Data)
    {
        public Pixel At(int x, int y)
        {
            var offset = (y * Width + x) * 4;
            return new(Data[offset + 2], Data[offset + 1], Data[offset]);
        }
    }

    private static Raster Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var data = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(data, image.PixelWidth * 4, 0);
        return new(image.PixelWidth, image.PixelHeight, data);
    }

    private static int Difference(Pixel first, Pixel second) =>
        Math.Max(Math.Abs(first.R - second.R), Math.Max(Math.Abs(first.G - second.G), Math.Abs(first.B - second.B)));

    private static int CountEnclosedWhiteRegions(Raster image)
    {
        var visited = new bool[image.Width * image.Height];
        var pending = new Queue<int>();
        var holes = 0;
        for (var index = 0; index < visited.Length; index++)
        {
            if (visited[index]) continue;
            visited[index] = true;
            if (!White(index)) continue;
            pending.Enqueue(index);
            var area = 0;
            var border = false;
            while (pending.TryDequeue(out var current))
            {
                area++;
                var x = current % image.Width;
                var y = current / image.Width;
                border |= x == 0 || y == 0 || x == image.Width - 1 || y == image.Height - 1;
                if (x > 0) Visit(current - 1);
                if (x + 1 < image.Width) Visit(current + 1);
                if (y > 0) Visit(current - image.Width);
                if (y + 1 < image.Height) Visit(current + image.Width);
            }
            if (!border && area >= 100) holes++;
        }
        return holes;

        bool White(int position)
        {
            var pixel = image.At(position % image.Width, position / image.Width);
            return pixel.R > 245 && pixel.G > 245 && pixel.B > 245;
        }
        void Visit(int position)
        {
            if (visited[position]) return;
            visited[position] = true;
            if (White(position)) pending.Enqueue(position);
        }
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static Task Sta(Action action) => Sta(() => { action(); return true; });
    private static Task<T> Sta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private sealed class MemoryRepository : INotebookRepository
    {
        private readonly Dictionary<string, AssetData> _assets = [];
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes)
        {
            var asset = new AssetData(Guid.NewGuid().ToString("N"), fileName, contentType, bytes);
            _assets.Add(asset.Id, asset); return Task.FromResult(asset);
        }
        public Task<AssetData> GetAssetAsync(string id) => Task.FromResult(_assets[id]);
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => Task.FromResult<IReadOnlyList<NotebookSummary>>([]);
        public Task<NotebookDocument?> LoadAsync(string id) => Task.FromResult<NotebookDocument?>(null);
        public Task SaveAsync(NotebookDocument document) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public void Dispose() { }
    }
}
