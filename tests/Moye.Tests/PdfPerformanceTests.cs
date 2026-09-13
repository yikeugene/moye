using System.Diagnostics;
using System.IO;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;
using Moye.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace Moye.Tests;

public sealed class PdfPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HundredPagePdfImportsExportsAndRendersEveryReorderedPage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoyePdfPerformance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var source = Path.Combine(directory, "hundred-pages.pdf");
            var exported = Path.Combine(directory, "annotated.pdf");
            await Sta(() =>
            {
                using var document = new PdfDocument();
                for (var index = 0; index < 100; index++)
                {
                    var page = document.AddPage();
                    page.Width = XUnit.FromPoint(595.2); page.Height = XUnit.FromPoint(841.6);
                    using var graphics = XGraphics.FromPdfPage(page);
                    var color = XColor.FromArgb(30 + index * 47 % 200, 30 + index * 71 % 200, 30 + index * 97 % 200);
                    graphics.DrawRectangle(new XSolidBrush(color), 72, 72, 30, 24);
                    graphics.DrawString($"Source page {index + 1:D3}", new XFont("Arial", 12), XBrushes.Black, 72, 220);
                }
                document.Save(source);
            });
            using var repository = new CountingRepository(new SqliteNotebookRepository(Path.Combine(directory, "moye.db")));
            await repository.InitializeAsync();
            var service = new PdfService(repository);
            var allocationsBefore = GC.GetTotalAllocatedBytes();
            var timer = Stopwatch.StartNew();
            var imported = await service.ImportAsync(source, timeout.Token);
            var importTime = timer.Elapsed;
            Assert.Equal(100, imported.Count);
            Assert.Single(imported.Select(p => p.Pdf!.AssetId).Distinct());

            timer.Restart();
            var colors = new List<(byte R, byte G, byte B)>();
            foreach (var page in imported)
            {
                var bitmap = await service.RenderAsync(page, .5, timeout.Token);
                Assert.True(bitmap.IsFrozen);
                Assert.InRange(bitmap.PixelWidth, 396, 398);
                Assert.InRange(bitmap.PixelHeight, 561, 563);
                colors.Add(Pixel(bitmap, 56, 56));
            }
            var firstRenderTime = timer.Elapsed;
            var coldAssetReads = repository.ReadCount;
            var coldAssetReadBytes = repository.ReadBytes;

            timer.Restart();
            foreach (var page in imported) _ = await service.RenderAsync(page, .5, timeout.Token);
            var warmRenderTime = timer.Elapsed;
            Assert.Equal(coldAssetReads, repository.ReadCount); // These 100 half-scale A4 rasters fit within 128 MiB.

            var payload = await Sta(() =>
            {
                var points = new StylusPointCollection { new StylusPoint(70, 145, .15f), new StylusPoint(105, 145, .6f), new StylusPoint(145, 145, 1) };
                var stroke = new Stroke(points, new DrawingAttributes { Color = Colors.Blue, Width = 10, Height = 10, IgnorePressure = false });
                using var inkStream = new MemoryStream(); new StrokeCollection([stroke]).Save(inkStream);
                var pixels = Enumerable.Repeat(new byte[] { 0, 255, 0, 128 }, 16 * 16).SelectMany(p => p).ToArray();
                var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var imageStream = new MemoryStream(); encoder.Save(imageStream);
                return (Ink: inkStream.ToArray(), Png: imageStream.ToArray());
            });
            var asset = await repository.PutAssetAsync("shared-alpha.png", "image/png", payload.Png);
            foreach (var page in imported)
            {
                page.InkData = payload.Ink;
                page.Texts.Add(new NoteText { X = 72, Y = 180, Width = 220, Height = 60, FontSize = 22, Text = "繁體中文筆記" });
                page.Images.Add(new NoteImage { AssetId = asset.Id, X = 150, Y = 70, Width = 32, Height = 32 });
            }
            var notebook = new NotebookDocument { Title = "100 頁驗證", Pages = imported.Reverse().Select(p => p.Snapshot()).ToList() };
            timer.Restart();
            await service.ExportAsync(exported, notebook, timeout.Token);
            var exportTime = timer.Elapsed;

            // Use a fresh service so this stage cannot accidentally pass from the source's bitmap cache.
            var verificationService = new PdfService(repository);
            timer.Restart();
            var roundTrip = await verificationService.ImportAsync(exported, timeout.Token);
            var secondImportTime = timer.Elapsed;
            Assert.Equal(100, roundTrip.Count);
            timer.Restart();
            for (var index = 0; index < roundTrip.Count; index++)
            {
                var bitmap = await verificationService.RenderAsync(roundTrip[index], .5, timeout.Token);
                Assert.Equal(colors[99 - index], Pixel(bitmap, 56, 56));
                var picture = Pixel(bitmap, 83, 43);
                Assert.True(picture.G > 245 && picture.R is > 100 and < 160 && picture.B is > 100 and < 160,
                    $"Page {index + 1}: reused image/mask was lost or misplaced ({picture}).");
            }
            var secondRenderTime = timer.Elapsed;
            using var process = Process.GetCurrentProcess(); process.Refresh();
            output.WriteLine($"100-page PDF timing: import {importTime.TotalMilliseconds:F0} ms; first full render {firstRenderTime.TotalMilliseconds:F0} ms; cached full render {warmRenderTime.TotalMilliseconds:F0} ms; export {exportTime.TotalMilliseconds:F0} ms; re-import {secondImportTime.TotalMilliseconds:F0} ms; second full render {secondRenderTime.TotalMilliseconds:F0} ms.");
            output.WriteLine($"Files: source {new FileInfo(source).Length:N0} bytes; annotated {new FileInfo(exported).Length:N0} bytes. First render asset reads: {coldAssetReads}, {coldAssetReadBytes:N0} bytes. Total reads: {repository.ReadCount}, {repository.ReadBytes:N0} bytes.");
            output.WriteLine($"Process peak working set {process.PeakWorkingSet64 / 1048576d:F1} MiB; managed allocations during measured work {(GC.GetTotalAllocatedBytes() - allocationsBefore) / 1048576d:F1} MiB. Timing is reported, not used as a machine-dependent pass/fail threshold.");
            using var sourceStructure = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            using var exportedStructure = PdfReader.Open(exported, PdfDocumentOpenMode.Import);
            static int FontCount(PdfDocument pdf) => pdf.Internals.GetAllObjects().OfType<PdfDictionary>().Count(d => d.Elements.GetName("/Type") == "/Font");
            output.WriteLine($"Font resource objects: source {FontCount(sourceStructure)}, annotated {FontCount(exportedStructure)}. New notebook text is outlined, so additional font objects come from copied source pages.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static (byte R, byte G, byte B) Pixel(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }

    private static Task Sta(Action action) => Sta(() => { action(); return true; });
    private static Task<T> Sta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private sealed class CountingRepository(INotebookRepository inner) : INotebookRepository
    {
        public int ReadCount { get; private set; }
        public long ReadBytes { get; private set; }
        public async Task<AssetData> GetAssetAsync(string id)
        { var asset = await inner.GetAssetAsync(id); ReadCount++; ReadBytes += asset.Bytes.LongLength; return asset; }
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes) => inner.PutAssetAsync(fileName, contentType, bytes);
        public Task InitializeAsync() => inner.InitializeAsync();
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => inner.ListAsync();
        public Task<NotebookDocument?> LoadAsync(string id) => inner.LoadAsync(id);
        public Task SaveAsync(NotebookDocument document) => inner.SaveAsync(document);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public void Dispose() => inner.Dispose();
    }
}
