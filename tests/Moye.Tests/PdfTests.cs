using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;
using Moye.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

namespace Moye.Tests;

public sealed class PdfTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MoyePdfTests-" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public PdfTests(Xunit.Abstractions.ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_directory); }
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task CropAndRotationPlacePressureInkOverOriginalVisibleMark(int rotation)
    {
        var source = Path.Combine(_directory, "source.pdf");
        await Sta(() =>
        {
            using var document = new PdfDocument();
            var page = document.AddPage();
            page.MediaBox = new PdfRectangle(new XPoint(12, 18), new XPoint(372, 498));
            page.CropBox = new PdfRectangle(new XPoint(48, 66), new XPoint(324, 432));
            page.Rotate = rotation;
            using (var graphics = XGraphics.FromPdfPage(page))
                graphics.DrawRectangle(XBrushes.Red, 110, 150, 18, 18);
            document.Save(source);
        });
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var page = Assert.Single(await service.ImportAsync(source));
        var before = await service.RenderAsync(page, 1);
        Assert.True(before.IsFrozen);
        var red = Bounds(before, (r, g, b) => r > 190 && g < 80 && b < 80);
        Assert.True(red.Width > 10, "Original mark should be visible inside the crop.");
        var center = new Point(red.X + red.Width / 2, red.Y + red.Height / 2);
        page.InkData = await Sta(() => Ink(new Stroke(new StylusPointCollection
        {
            new StylusPoint(center.X - 3, center.Y, 1), new StylusPoint(center.X + 3, center.Y, 1)
        }, new DrawingAttributes { Color = Colors.Blue, Width = 9, Height = 9, IgnorePressure = false })));
        var output = Path.Combine(_directory, "output.pdf");
        await service.ExportAsync(output, new NotebookDocument { Pages = [page] });
        var roundTrip = Assert.Single(await service.ImportAsync(output));
        var after = await service.RenderAsync(roundTrip, 1);
        Assert.Equal(before.PixelWidth, after.PixelWidth);
        Assert.Equal(before.PixelHeight, after.PixelHeight);
        var blue = Bounds(after, (r, g, b) => b > 180 && r < 100 && g < 100);
        _output.WriteLine($"Page {page.Width} x {page.Height}; raster {before.PixelWidth} x {before.PixelHeight}; crop {page.Pdf}; expected {center}; blue {blue}");
        Assert.InRange(Math.Abs(blue.X + blue.Width / 2 - center.X), 0, 2);
        Assert.InRange(Math.Abs(blue.Y + blue.Height / 2 - center.Y), 0, 2);
        Assert.Equal(rotation, roundTrip.Pdf!.Rotation);
    }

    [Fact]
    public async Task ExportKeepsPressureChineseTextAndTransparentImages()
    {
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var page = new NotePage { Width = 300, Height = 240 };
        page.InkData = await Sta(() => Ink(
            new Stroke(new StylusPointCollection
            { new StylusPoint(40, 65, .15f), new StylusPoint(110, 65, .6f), new StylusPoint(180, 65, 1) },
                new DrawingAttributes { Color = Colors.Blue, Width = 20, Height = 20, IgnorePressure = false }),
            new Stroke(new StylusPointCollection { new StylusPoint(40, 205), new StylusPoint(180, 205) },
                new DrawingAttributes { Color = Colors.Yellow, Width = 18, Height = 18, IsHighlighter = true, IgnorePressure = true }),
            new Stroke(new StylusPointCollection { new StylusPoint(70, 205), new StylusPoint(140, 205) },
                new DrawingAttributes { Color = Colors.Yellow, Width = 18, Height = 18, IsHighlighter = true, IgnorePressure = true })));
        page.Texts.Add(new NoteText { X = 35, Y = 105, Width = 170, Height = 75, FontSize = 24,
            Text = "繁體中文筆記\n第二行", Color = "#FF111111" });
        var png = await Sta(() =>
        {
            var pixels = Enumerable.Repeat(new byte[] { 0, 0, 255, 128 }, 16 * 16).SelectMany(p => p).ToArray();
            var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        });
        var asset = await repository.PutAssetAsync("red.png", "image/png", png);
        page.Images.Add(new NoteImage { AssetId = asset.Id, X = 230, Y = 25, Width = 32, Height = 32 });
        var output = Path.Combine(_directory, "writing.pdf");
        await service.ExportAsync(output, new NotebookDocument { Pages = [page] });
        var imported = Assert.Single(await service.ImportAsync(output));
        var bitmap = await service.RenderAsync(imported, 1);
        _output.WriteLine($"Original note {page.Width} x {page.Height}; imported {imported.Width} x {imported.Height}; raster {bitmap.PixelWidth} x {bitmap.PixelHeight}");
        Assert.InRange(bitmap.PixelWidth, 299, 301);
        var pixelsAfter = Pixels(bitmap);
        int BlueHeight(int x) => Enumerable.Range(25, 80).Count(y =>
        { var c = Pixel(pixelsAfter, bitmap.PixelWidth, x, y); return c.B > 180 && c.R < 80; });
        Assert.True(BlueHeight(165) > BlueHeight(50) * 1.5, "Pressure must survive as a changing outline width.");
        var chinese = Bounds(bitmap, (r, g, b) => r < 80 && g < 80 && b < 80);
        Assert.InRange(chinese.Left, 30, 45);
        Assert.True(chinese.Width > 100 && chinese.Height > 40, "Both Chinese lines must be rendered, with glyphs rather than missing boxes.");
        var translucentImage = Pixel(pixelsAfter, bitmap.PixelWidth, 246, 41);
        Assert.True(translucentImage.R > 245 && translucentImage.G is > 100 and < 160 && translucentImage.B is > 100 and < 160,
            $"Expected a red image at half opacity over white, got {translucentImage}.");
        var highlighter = Pixel(pixelsAfter, bitmap.PixelWidth, 100, 205);
        Assert.True(highlighter.R > 245 && highlighter.G > 245 && highlighter.B is > 100 and < 160, $"Highlighter pixel was {highlighter}.");
    }

    [Fact]
    public async Task ReorderedDuplicatedPagesKeepExistingVisibleAnnotationsAndOriginalText()
    {
        var source = Path.Combine(_directory, "annotations.pdf");
        await Sta(() =>
        {
            using var document = new PdfDocument();
            for (var index = 0; index < 2; index++)
            {
                var page = document.AddPage(); page.Width = XUnit.FromPoint(240); page.Height = XUnit.FromPoint(300);
                using (var graphics = XGraphics.FromPdfPage(page))
                {
                    graphics.DrawRectangle(index == 0 ? XBrushes.Red : XBrushes.Blue, 40, 40, 25, 25);
                    graphics.DrawString("Original selectable text", new XFont("Arial", 12), XBrushes.Black, 35, 120);
                }
                page.Annotations.Add(new PdfTextAnnotation(document)
                { Rectangle = new PdfRectangle(new XPoint(80, 180), new XPoint(105, 205)), Contents = "Existing note",
                    Icon = PdfTextAnnotationIcon.Comment, Color = XColors.Yellow });
            }
            document.Save(source);
        });
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var pages = await service.ImportAsync(source);
        var before = await service.RenderAsync(pages[1], 1);
        var output = Path.Combine(_directory, "reordered.pdf");
        await service.ExportAsync(output, new NotebookDocument { Pages = [pages[1], pages[0], pages[1].Snapshot()] });
        var imported = await service.ImportAsync(output);
        Assert.Equal(3, imported.Count);
        var after = await service.RenderAsync(imported[0], 1);
        Assert.Equal(Pixels(before), Pixels(after));
        var duplicate = await service.RenderAsync(imported[2], 1);
        Assert.Equal(Pixels(before), Pixels(duplicate));
        using var exported = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.All(exported.Pages.Cast<PdfPage>(), p =>
        {
            Assert.Single(p.Annotations.Cast<PdfAnnotation>());
            var stream = p.Contents.CreateSingleContent().Stream.UnfilteredValue;
            Assert.Contains("Tj", Encoding.Latin1.GetString(stream));
        });
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("form")]
    [InlineData("signature")]
    public async Task UnsupportedPdfIsRejectedBeforeAnyAssetIsStored(string kind)
    {
        var input = Path.Combine(_directory, "unsupported.pdf");
        await Sta(() =>
        {
            using var document = new PdfDocument(); document.AddPage();
            if (kind == "encrypted") document.SecuritySettings.UserPassword = "password";
            if (kind == "form") document.Internals.Catalog.Elements["/AcroForm"] = new PdfDictionary(document);
            if (kind == "signature") document.Internals.Catalog.Elements["/Perms"] = new PdfDictionary(document);
            document.Save(input);
        });
        using var repository = new MemoryRepository();
        await Assert.ThrowsAsync<InvalidDataException>(() => new PdfService(repository).ImportAsync(input));
        Assert.Empty(repository.Assets);
    }

    [Fact]
    public async Task CancelledExportLeavesExistingDestinationUntouched()
    {
        var output = Path.Combine(_directory, "preserve.pdf");
        var original = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(output, original);
        using var repository = new MemoryRepository();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfService(repository).ExportAsync(output,
            new NotebookDocument { Pages = [new NotePage()] }, cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Single(Directory.GetFiles(_directory));
    }

    private static byte[] Ink(params Stroke[] strokes)
    {
        using var stream = new MemoryStream(); new StrokeCollection(strokes).Save(stream); return stream.ToArray();
    }

    [Fact]
    public async Task ExportAfterSectionReorderIncludesEverySectionAndKeepsInkPosition()
    {
        using var repository = new MemoryRepository();
        using var model = new Moye.ViewModels.MainViewModel(repository);
        await model.CreateAsync("Mathematics", "Semester 1", PaperTemplate.Plain);
        model.RenameSection("Algebra");
        var algebraPage = model.SelectedPage!.Page;
        algebraPage.Width = 300; algebraPage.Height = 400;
        algebraPage.InkData = await Sta(() => Ink(new Stroke(new StylusPointCollection
        { new StylusPoint(40, 80, 1), new StylusPoint(140, 80, 1) },
            new DrawingAttributes { Color = Colors.Blue, Width = 8, Height = 8, IgnorePressure = false })));
        model.Changed();
        model.AddSection("Calculus");
        model.SelectedPage!.Page.Width = 420; model.SelectedPage.Page.Height = 300;
        model.Changed();
        model.AddPage(PaperTemplate.Graph);
        model.SelectedPage!.Page.Width = 350; model.SelectedPage.Page.Height = 250;
        model.Changed();
        model.MoveSection(-1);
        Assert.Equal(2, model.Pages.Count); // Only Calculus is visible in the editor.
        var file = Path.Combine(_directory, "sections.pdf");
        await model.Pdf.ExportAsync(file, model.Document!.Snapshot());
        var imported = await model.Pdf.ImportAsync(file);
        Assert.Equal(3, imported.Count); // Algebra must still be included.
        Assert.InRange(imported[0].Width, 419, 421);
        Assert.InRange(imported[1].Width, 349, 351);
        Assert.InRange(imported[2].Width, 299, 301);
        var bitmap = await model.Pdf.RenderAsync(imported[2], 1);
        var blue = Bounds(bitmap, (r, g, b) => b > 180 && r < 100 && g < 100);
        Assert.InRange(blue.X + blue.Width / 2, 89, 91);
        Assert.InRange(blue.Y + blue.Height / 2, 79, 81);
        await model.Autosave.FlushAsync();
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }

    private static (byte R, byte G, byte B) Pixel(byte[] bytes, int width, int x, int y)
    { var offset = (y * width + x) * 4; return (bytes[offset + 2], bytes[offset + 1], bytes[offset]); }

    private static Rect Bounds(BitmapSource bitmap, Func<byte, byte, byte, bool> match)
    {
        var pixels = Pixels(bitmap);
        var minX = bitmap.PixelWidth; var minY = bitmap.PixelHeight; var maxX = -1; var maxY = -1;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var color = Pixel(pixels, bitmap.PixelWidth, x, y);
                if (!match(color.R, color.G, color.B)) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        return maxX < minX ? Rect.Empty : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
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
        public Dictionary<string, AssetData> Assets { get; } = [];
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes)
        {
            var asset = new AssetData(Guid.NewGuid().ToString("N"), fileName, contentType, bytes);
            Assets.Add(asset.Id, asset); return Task.FromResult(asset);
        }
        public Task<AssetData> GetAssetAsync(string id) => Task.FromResult(Assets[id]);
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => Task.FromResult<IReadOnlyList<NotebookSummary>>([]);
        public Task<NotebookDocument?> LoadAsync(string id) => Task.FromResult<NotebookDocument?>(null);
        public Task SaveAsync(NotebookDocument document) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public void Dispose() { }
    }
}
