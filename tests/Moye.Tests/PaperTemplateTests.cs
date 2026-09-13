using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Controls;
using Moye.Models;
using Moye.Services;
using Moye.ViewModels;
using PdfSharp.Pdf.IO;

namespace Moye.Tests;

public sealed class PaperTemplateTests
{
    [Fact]
    public void ExistingNumericTemplatesKeepTheirMeaningAndNewTemplatesRoundTrip()
    {
        Assert.Equal(0, (int)PaperTemplate.Plain);
        Assert.Equal(1, (int)PaperTemplate.Ruled);
        Assert.Equal(2, (int)PaperTemplate.Grid);
        Assert.Equal(PaperTemplate.Plain, JsonSerializer.Deserialize<NotePage>("{\"template\":0}", DocumentJson.Options)!.Template);
        Assert.Equal(PaperTemplate.Ruled, JsonSerializer.Deserialize<NotePage>("{\"template\":1}", DocumentJson.Options)!.Template);
        Assert.Equal(PaperTemplate.Grid, JsonSerializer.Deserialize<NotePage>("{\"template\":2}", DocumentJson.Options)!.Template);
        foreach (var template in Enum.GetValues<PaperTemplate>())
        {
            var original = new NotePage { Template = template };
            var restored = JsonSerializer.Deserialize<NotePage>(JsonSerializer.Serialize(original, DocumentJson.Options), DocumentJson.Options)!;
            Assert.Equal(template, restored.Template);
            Assert.Equal(template, original.Snapshot().Template);
        }
    }

    [Theory]
    [InlineData(793.7, 1122.5)]
    [InlineData(420, 240)]
    [InlineData(36, 24)]
    public void AllPatternCoordinatesStayInsidePortraitLandscapeAndSmallPages(double width, double height)
    {
        foreach (var template in Enum.GetValues<PaperTemplate>())
        {
            foreach (var line in PaperPattern.Lines(template, width, height))
            {
                Assert.InRange(line.Start.X, 0, width);
                Assert.InRange(line.End.X, 0, width);
                Assert.InRange(line.Start.Y, 0, height);
                Assert.InRange(line.End.Y, 0, height);
                Assert.True(line.Thickness > 0);
            }
            foreach (var dot in PaperPattern.Dots(template, width, height))
            {
                Assert.InRange(dot.Center.X - dot.Radius, 0, width);
                Assert.InRange(dot.Center.X + dot.Radius, 0, width);
                Assert.InRange(dot.Center.Y - dot.Radius, 0, height);
                Assert.InRange(dot.Center.Y + dot.Radius, 0, height);
            }
        }
    }

    [Fact]
    public void NewTemplatesHaveDistinctGeometryAndAccurateCaptions()
    {
        var dots = PaperPattern.Dots(PaperTemplate.DotGrid, 240, 320).ToArray();
        Assert.NotEmpty(dots);
        Assert.Empty(PaperPattern.Lines(PaperTemplate.DotGrid, 240, 320));
        Assert.Equal(24, dots[1].Center.X - dots[0].Center.X);
        var grid = PaperPattern.Lines(PaperTemplate.Grid, 240, 320).ToArray();
        var graph = PaperPattern.Lines(PaperTemplate.Graph, 240, 320).ToArray();
        Assert.True(graph.Length > grid.Length);
        Assert.Contains(graph, line => line.Start.X == 60 && line.End.X == 60 && line.Thickness == 1);
        var cornell = PaperPattern.Lines(PaperTemplate.Cornell, 240, 320).ToArray();
        Assert.Single(cornell, line => line.Start.X == line.End.X);
        foreach (var (template, label) in new[] { (PaperTemplate.DotGrid, "Dot Grid"), (PaperTemplate.Cornell, "Cornell"), (PaperTemplate.Graph, "Graph") })
        {
            var page = new NotePage { Template = template };
            Assert.Equal($"01  ·  {label}", new PageViewModel(page, 1, 1).Caption);
            page.Pdf = new PdfPageSource();
            Assert.Equal("01  ·  PDF", new PageViewModel(page, 1, 1).Caption);
        }
    }

    [Fact]
    public async Task PickerOffersAllSixTemplatesWithinTwoCompactTouchRows()
    {
        await Sta(() =>
        {
            var picker = new PaperTemplatePicker();
            picker.Measure(new Size(360, double.PositiveInfinity));
            picker.Arrange(new Rect(new Point(), picker.DesiredSize));
            picker.UpdateLayout();
            Assert.InRange(picker.ActualHeight, 44, 300);
            var grid = Assert.IsType<UniformGrid>(picker.Content);
            Assert.Equal(3, grid.Columns);
            Assert.Equal(2, grid.Rows);
            var choices = grid.Children.Cast<RadioButton>().ToArray();
            Assert.Equal(6, choices.Length);
            Assert.Contains(choices, choice => AutomationProperties.GetName(choice) == "Dot Grid paper");
            foreach (var (choice, template) in choices.Zip(Enum.GetValues<PaperTemplate>()))
            {
                Assert.True(choice.ActualHeight >= 44 && choice.ActualWidth >= 44);
                choice.IsChecked = true;
                Assert.Equal(template, picker.SelectedTemplate);
                Assert.Single(choices, item => item.IsChecked == true);
            }
            return true;
        });
    }

    [Theory]
    [InlineData(PaperTemplate.DotGrid, 24, 24, 36, 36)]
    [InlineData(PaperTemplate.Cornell, 78, 140, 50, 140)]
    [InlineData(PaperTemplate.Graph, 60, 79, 66, 79)]
    public async Task NewTemplatePdfKeepsVectorMarksAlignedAndPreservesImportedBackground(PaperTemplate template, int markX, int markY, int blankX, int blankY)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoyePaperTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var repository = new MemoryRepository();
            var service = new PdfService(repository);
            var page = new NotePage { Width = 240, Height = 320, Template = template };
            var before = await service.RenderAsync(page, 1);
            var output = Path.Combine(directory, "template.pdf");
            await service.ExportAsync(output, new NotebookDocument { Pages = [page] });
            using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
            {
                var content = Encoding.Latin1.GetString(pdf.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
                Assert.Contains(template == PaperTemplate.DotGrid ? " c" : " l", content);
                Assert.DoesNotContain(" Do", content); // The paper is vector paths, not a raster image resource.
            }
            var imported = Assert.Single(await service.ImportAsync(output));
            var after = await service.RenderAsync(imported, 1);
            Assert.Equal(before.PixelWidth, after.PixelWidth);
            Assert.Equal(before.PixelHeight, after.PixelHeight);
            foreach (var bitmap in new[] { before, after })
            {
                var pixels = Pixels(bitmap);
                Assert.True(HasBlueMarkNear(pixels, bitmap.PixelWidth, markX, markY), $"{template} mark was missing at ({markX}, {markY}).");
                var offset = (blankY * bitmap.PixelWidth + blankX) * 4;
                Assert.True(pixels[offset] >= 253 && pixels[offset + 1] >= 253 && pixels[offset + 2] >= 253);
            }

            // A template value on a source PDF must not paint over its original page.
            imported.Template = PaperTemplate.Grid;
            var secondExport = Path.Combine(directory, "source-preserved.pdf");
            await service.ExportAsync(secondExport, new NotebookDocument { Pages = [imported] });
            var preserved = Assert.Single(await service.ImportAsync(secondExport));
            Assert.Equal(Pixels(after), Pixels(await service.RenderAsync(preserved, 1)));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool HasBlueMarkNear(byte[] bytes, int width, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var offset = ((y + dy) * width + x + dx) * 4;
                if (bytes[offset + 2] < 250 && bytes[offset] > bytes[offset + 2]) return true;
            }
        return false;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }

    private static Task<T> Sta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class MemoryRepository : INotebookRepository
    {
        private readonly Dictionary<string, AssetData> _assets = [];
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes)
        {
            var asset = new AssetData(Guid.NewGuid().ToString("N"), fileName, contentType, bytes);
            _assets.Add(asset.Id, asset);
            return Task.FromResult(asset);
        }
        public Task<AssetData> GetAssetAsync(string id) => Task.FromResult(_assets[id]);
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => throw new NotSupportedException();
        public Task<NotebookDocument?> LoadAsync(string id) => throw new NotSupportedException();
        public Task SaveAsync(NotebookDocument document) => throw new NotSupportedException();
        public Task DeleteAsync(string id) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
