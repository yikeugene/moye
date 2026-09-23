using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;
using Moye.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Moye.Tests;

public sealed class PdfNavigationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MoyePdfNavigation-" + Guid.NewGuid().ToString("N"));
    public PdfNavigationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("/Dest [3 0 R /Fit]", "null", false)]
    [InlineData("/Dest /LectureStart", "null", false)]
    [InlineData("/Dest (lecture-start)", "null", false)]
    [InlineData("/Dest 19 0 R", "[3 0 R /XYZ null null null]", false)]
    [InlineData("/A << /S /GoTo /D [3 0 R /Fit] >>", "null", false)]
    [InlineData("/A 19 0 R", "<< /S /GoTo /D (lecture-start) >>", false)]
    [InlineData("/AA << /E << /S /JavaScript /JS (void 0) >> >>", "null", false)]
    [InlineData("/A << /S /URI /URI (https://example.org/course) >>", "null", true)]
    [InlineData("/A 19 0 R", "<< /S 20 0 R /URI (https://example.org/course) /Next << /S /GoTo /D [3 0 R /Fit] >> >>", true)]
    [InlineData("/A 19 0 R /Dest [3 0 R /Fit] /AA << /E << /S /GoTo /D [3 0 R /Fit] >> >>",
        "<< /S /URI /URI (https://example.org/course) /Next [<< /S /GoTo /D [3 0 R /Fit] >> << /S /JavaScript /JS (void 0) >>] >>", true)]
    public async Task NavigationOnSixthPageImportsWithoutChangingSourceAndExportsAsStaticPage(
        string navigation, string indirectObject, bool expectUri)
    {
        var source = WriteFixture(navigation, indirectObject);
        var originalBytes = await File.ReadAllBytesAsync(source);
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var pages = await service.ImportAsync(source);
        Assert.Equal(6, pages.Count);
        var asset = Assert.Single(repository.Assets.Values);
        Assert.Equal(originalBytes, asset.Bytes);
        Assert.All(pages, page => Assert.Equal(asset.Id, page.Pdf!.AssetId));
        Assert.Equal(Enumerable.Range(0, 6), pages.Select(page => page.Pdf!.PageIndex));

        // Export only page six: its former target on page one no longer exists.
        var export = Path.Combine(_directory, "last-page.pdf");
        await service.ExportAsync(export, new NotebookDocument { Pages = [pages[5]] });
        using var output = Read(export);
        Assert.Equal(1, output.PageCount);
        Assert.False(output.Internals.Catalog.Elements.ContainsKey("/OpenAction"));
        Assert.False(output.Pages[0].Elements.ContainsKey("/AA"));
        AssertStaticNavigation(Annotation(output.Pages[0]), expectUri);
        Assert.Single(output.Internals.GetAllObjects().OfType<PdfDictionary>(),
            item => item.Elements.GetName("/Type") == "/Page");
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(originalBytes, (await repository.GetAssetAsync(asset.Id)).Bytes);
    }

    [Fact]
    public async Task ReorderDuplicateAndDeleteKeepVisibleAnnotationsVectorTextAndImages()
    {
        var source = WriteFixture("/A 19 0 R /Dest [3 0 R /Fit]",
            "<< /S /URI /URI (https://example.org/course) /Next << /S /GoTo /D [3 0 R /Fit] >> >>");
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var pages = await service.ImportAsync(source);
        var originalLastPage = await service.RenderAsync(pages[5], 1);
        var originalSecondPage = await service.RenderAsync(pages[1], 1);
        using var sourcePdf = Read(source);
        var expectedAppearance = Appearance(Annotation(sourcePdf.Pages[5]));
        var expectedImage = ImageBytes(sourcePdf.Pages[5]);

        // Page six moves to the front and is duplicated; pages one/three/four/five are removed.
        var duplicate = pages[5].Snapshot(); duplicate.Id = Guid.NewGuid().ToString("N");
        var export = Path.Combine(_directory, "reordered.pdf");
        await service.ExportAsync(export, new NotebookDocument { Pages = [pages[5], pages[1], duplicate] });
        var roundTrip = await service.ImportAsync(export);
        Assert.Equal(3, roundTrip.Count);
        var renderedExport = await service.RenderAsync(roundTrip[0], 1);
        Assert.Equal(Pixels(originalLastPage), Pixels(renderedExport));
        Assert.Equal(Pixels(originalSecondPage), Pixels(await service.RenderAsync(roundTrip[1], 1)));
        Assert.Equal(Pixels(originalLastPage), Pixels(await service.RenderAsync(roundTrip[2], 1)));
        SaveOptionalVisualEvidence(source, export, originalLastPage, renderedExport);

        using var output = Read(export);
        Assert.Equal(3, output.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Count(item => item.Elements.GetName("/Type") == "/Page"));
        for (var index = 0; index < output.PageCount; index++)
        {
            var page = output.Pages[index];
            var content = Encoding.ASCII.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue);
            Assert.Contains(index == 1 ? "(Original page 2) Tj" : "(Original page 6) Tj", content);
            Assert.Contains("20 20 30 30 re f", content);
            Assert.Contains("/Im1 Do", content);
            Assert.Equal(expectedImage, ImageBytes(page));
            if (index == 1) continue;
            var annotation = Annotation(page);
            AssertStaticNavigation(annotation, expectUri: true);
            Assert.Equal("Visible course note", annotation.Elements.GetString("/Contents"));
            Assert.Equal(expectedAppearance, Appearance(annotation));
            Assert.Equal(Annotation(sourcePdf.Pages[5]).Elements.GetArray("/Rect")!.ToString(),
                annotation.Elements.GetArray("/Rect")!.ToString());
            Assert.Equal(Annotation(sourcePdf.Pages[5]).Elements.GetArray("/Border")!.ToString(),
                annotation.Elements.GetArray("/Border")!.ToString());
        }
    }

    [Fact]
    public async Task SharedIndirectUriActionKeepsBothLinksAndDropsItsFollowUpAction()
    {
        var source = WriteFixture("/A 19 0 R",
            "<< /S /URI /URI (https://example.org/course) /Next << /S /GoTo /D [3 0 R /Fit] >> >>", sharedAction: true);
        var originalBytes = await File.ReadAllBytesAsync(source);
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var pages = await service.ImportAsync(source);
        var export = Path.Combine(_directory, "shared-action.pdf");
        await service.ExportAsync(export, new NotebookDocument { Pages = [pages[5]] });
        using var original = Read(source);
        using var output = Read(export);
        var annotations = output.Pages[0].Elements.GetArray("/Annots")!;
        Assert.Equal(2, annotations.Elements.Count);
        for (var index = 0; index < annotations.Elements.Count; index++)
        {
            var before = original.Pages[5].Elements.GetArray("/Annots")!.Elements.GetDictionary(index)!;
            var after = annotations.Elements.GetDictionary(index)!;
            AssertStaticNavigation(after, expectUri: true);
            Assert.Equal(before.Elements.GetString("/Contents"), after.Elements.GetString("/Contents"));
            Assert.Equal(before.Elements.GetArray("/Rect")!.ToString(), after.Elements.GetArray("/Rect")!.ToString());
            Assert.Equal(Appearance(before), Appearance(after));
        }
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(originalBytes, (await repository.GetAssetAsync(pages[5].Pdf!.AssetId)).Bytes);
    }

    [Fact]
    public async Task SaveReopenAndBackupKeepTheOriginalNavigablePdfBytes()
    {
        var source = WriteFixture("/Dest [3 0 R /Fit]");
        var originalBytes = await File.ReadAllBytesAsync(source);
        var database = Path.Combine(_directory, "library.db");
        var document = new NotebookDocument { Title = "Course notes" };
        string assetId;
        using (var repository = new SqliteNotebookRepository(database))
        {
            await repository.InitializeAsync();
            document.Pages = (await new PdfService(repository).ImportAsync(source)).ToList();
            assetId = document.Pages[5].Pdf!.AssetId;
            NotebookStructure.Normalize(document);
            await repository.SaveAsync(document);
        }

        using var reopened = new SqliteNotebookRepository(database);
        await reopened.InitializeAsync();
        var saved = await reopened.LoadAsync(document.Id);
        Assert.NotNull(saved);
        Assert.Equal(6, saved.Pages.Count);
        Assert.Equal(originalBytes, (await reopened.GetAssetAsync(assetId)).Bytes);
        var backup = Path.Combine(_directory, "course.moye");
        await new BackupService(reopened).ExportAsync(backup, [saved]);

        using var restoredRepository = new SqliteNotebookRepository(Path.Combine(_directory, "restored.db"));
        await restoredRepository.InitializeAsync();
        var restored = Assert.Single(await new BackupService(restoredRepository).ImportAsync(backup));
        Assert.NotEqual(saved.Id, restored.Id);
        Assert.Equal(6, restored.Pages.Count);
        Assert.Equal(assetId, restored.Pages[5].Pdf!.AssetId);
        var restoredBytes = (await restoredRepository.GetAssetAsync(assetId)).Bytes;
        Assert.Equal(originalBytes, restoredBytes);
        using (var original = PdfReader.Open(new MemoryStream(restoredBytes), PdfDocumentOpenMode.Import))
            Assert.NotNull(Annotation(original.Pages[5]).Elements.GetArray("/Dest"));
        await restoredRepository.SaveAsync(restored);
        var export = Path.Combine(_directory, "restored-export.pdf");
        await new PdfService(restoredRepository).ExportAsync(export, restored);
        using var output = Read(export);
        Assert.Equal(6, output.PageCount);
        AssertStaticNavigation(Annotation(output.Pages[5]), expectUri: false);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(originalBytes, (await restoredRepository.GetAssetAsync(assetId)).Bytes);
    }

    [Theory]
    [InlineData("/Widget")]
    [InlineData("/Screen")]
    [InlineData("/RichMedia")]
    public async Task UnsupportedInteractiveObjectOnSixthPageStillReportsItsPageBeforeStoringAssets(string subtype)
    {
        var source = WriteFixture("", annotationSubtype: subtype);
        var originalBytes = await File.ReadAllBytesAsync(source);
        using var repository = new MemoryRepository();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new PdfService(repository).ImportAsync(source));
        Assert.StartsWith("PDF page 6:", error.Message);
        Assert.Contains("unsupported interactive annotations", error.Message);
        Assert.Empty(repository.Assets);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
    }

    private string WriteFixture(string navigation, string indirectObject = "null", string annotationSubtype = "/Link", bool sharedAction = false)
    {
        // Keep the action on page six, with its target on page one, matching
        // lecture PDFs that fail only after several ordinary pages were read.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction [3 0 R /Fit] /Dests << /LectureStart [3 0 R /Fit] >> /Names << /Dests << /Names [(lecture-start) [3 0 R /Fit]] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R 7 0 R 8 0 R] /Count 6 >>"
        };
        for (var index = 0; index < 6; index++)
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 300] " +
                "/Resources << /Font << /F1 17 0 R >> /XObject << /Im1 18 0 R >> >> " +
                $"/Contents {9 + index} 0 R " + (index == 5 ? "/AA << /O << /S /GoTo /D [3 0 R /Fit] >> >> " +
                    (sharedAction ? "/Annots [15 0 R 21 0 R] " : "/Annots [15 0 R] ") : "") + ">>");
        for (var index = 0; index < 6; index++)
            objects.Add(Stream($"1 0 0 rg 20 20 30 30 re f\nBT /F1 14 Tf 20 220 Td (Original page {index + 1}) Tj ET\nq 40 0 0 20 120 40 cm /Im1 Do Q\n"));
        objects.Add($"<< /Type /Annot /Subtype {annotationSubtype} /Rect [20 100 80 130] /Border [0 0 2] " +
            "/C [0 0 1] /Contents (Visible course note) /AP << /N 16 0 R >> " + navigation + " >>");
        objects.Add(Stream("0 0.6 0 rg 0 0 60 30 re f\n", "/Type /XObject /Subtype /Form /BBox [0 0 60 30] /Resources << >>"));
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.Add(Stream("FF00000000FF>", "/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode"));
        objects.Add(indirectObject);
        objects.Add("/URI");
        if (sharedAction)
            objects.Add("<< /Type /Annot /Subtype /Link /Rect [90 100 150 130] /Border [0 0 2] " +
                "/Contents (Second visible course link) /AP << /N 16 0 R >> /A 19 0 R >>");
        var result = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(result.ToString()));
            result.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(result.ToString());
        result.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) result.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        result.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(_directory, "lecture.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(result.ToString()));
        return path;
    }

    private static string Stream(string content, string dictionary = "") =>
        $"<< {dictionary} /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static PdfDocument Read(string path) => PdfReader.Open(path, PdfDocumentOpenMode.Import);
    private static PdfDictionary Annotation(PdfPage page)
    {
        var annotations = page.Elements.GetArray("/Annots")!;
        Assert.Single(annotations.Elements.Cast<PdfItem>());
        return annotations.Elements.GetDictionary(0)!;
    }
    private static byte[] Appearance(PdfDictionary annotation) =>
        annotation.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Stream.UnfilteredValue;
    private static byte[] ImageBytes(PdfPage page) =>
        page.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!.Elements.GetDictionary("/Im1")!.Stream.UnfilteredValue;

    private static void AssertStaticNavigation(PdfDictionary annotation, bool expectUri)
    {
        Assert.False(annotation.Elements.ContainsKey("/Dest"));
        Assert.False(annotation.Elements.ContainsKey("/AA"));
        if (!expectUri) { Assert.False(annotation.Elements.ContainsKey("/A")); return; }
        var action = annotation.Elements.GetDictionary("/A");
        Assert.NotNull(action);
        Assert.Equal("/URI", action.Elements.GetName("/S"));
        Assert.Equal("https://example.org/course", action.Elements.GetString("/URI"));
        Assert.False(action.Elements.ContainsKey("/Next"));
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var result = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(result, converted.PixelWidth * 4, 0);
        return result;
    }

    private static void SaveOptionalVisualEvidence(string source, string export, BitmapSource before, BitmapSource after)
    {
        var destination = Environment.GetEnvironmentVariable("MOYE_PDF_QA_DIR");
        if (string.IsNullOrWhiteSpace(destination)) return;
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        File.Copy(source, Path.Combine(destination, "navigation-original.pdf"), overwrite: true);
        File.Copy(export, Path.Combine(destination, "navigation-reordered-export.pdf"), overwrite: true);
        SavePng(before, Path.Combine(destination, "navigation-original-page6.png"));
        SavePng(after, Path.Combine(destination, "navigation-export-page1.png"));
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class MemoryRepository : INotebookRepository
    {
        public Dictionary<string, AssetData> Assets { get; } = [];
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes)
        {
            var asset = new AssetData(Convert.ToHexStringLower(SHA256.HashData(bytes)), fileName, contentType, bytes);
            Assets[asset.Id] = asset;
            return Task.FromResult(asset);
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
