using System.Globalization;
using System.IO;
using System.Text;
using Moye.Models;
using Moye.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Moye.Tests;

public sealed class OfficePdfPreparationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MoyeOfficePdfPreparation-" + Guid.NewGuid().ToString("N"));
    public OfficePdfPreparationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PdfWithoutInternalActionsIsByteForByteUnchanged(bool externalUri)
    {
        var path = WriteFixture(externalUri ? "/A << /S /URI /URI (https://example.org/lecture) >>" : "");
        var before = await File.ReadAllBytesAsync(path);
        await OfficePdfPreparation.PrepareAsync(path);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("/Dest [3 0 R /Fit]")]
    [InlineData("/A << /S /GoTo /D [3 0 R /Fit] >>")]
    [InlineData("/AA << /E << /S /JavaScript /JS (void 0) >> >>")]
    [InlineData("/A 123")]
    public async Task InternalActionsBecomeStaticWithoutChangingPageOrAnnotationAppearance(string navigation)
    {
        var path = WriteFixture(navigation);
        var before = ReadAppearance(path);
        await OfficePdfPreparation.PrepareAsync(path);
        var after = ReadAppearance(path);
        Assert.Equal(before.PageContent, after.PageContent);
        Assert.Equal(before.AnnotationAppearance, after.AnnotationAppearance);
        Assert.Equal(before.ImageBytes, after.ImageBytes);
        Assert.Equal(before.Rectangle, after.Rectangle);
        Assert.Equal(before.Border, after.Border);
        Assert.Equal(before.Text, after.Text);
        using var document = Read(path);
        var annotation = document.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!;
        Assert.False(annotation.Elements.ContainsKey("/Dest"));
        Assert.False(annotation.Elements.ContainsKey("/AA"));
        Assert.False(annotation.Elements.ContainsKey("/A"));
        Assert.Single(document.Pages[0].Elements.GetArray("/Annots")!.Elements.Cast<PdfItem>());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ExternalUriSurvivesWhileItsIndirectNextActionChainIsRemoved()
    {
        var path = WriteFixture("/Dest [3 0 R /Fit] /AA << /E << /S /GoTo /D [3 0 R /Fit] >> >> /A 9 0 R",
            "<< /S /URI /URI (https://example.org/lecture) /Next [<< /S /GoTo /D [3 0 R /Fit] >> << /S /JavaScript /JS (void 0) >>] >>");
        var before = ReadAppearance(path);
        await OfficePdfPreparation.PrepareAsync(path);
        using var document = Read(path);
        var annotation = document.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!;
        var action = annotation.Elements.GetDictionary("/A")!;
        Assert.Equal("/URI", action.Elements.GetName("/S"));
        Assert.Equal("https://example.org/lecture", action.Elements.GetString("/URI"));
        Assert.False(action.Elements.ContainsKey("/Next"));
        Assert.False(annotation.Elements.ContainsKey("/Dest"));
        Assert.False(annotation.Elements.ContainsKey("/AA"));
        var after = ReadAppearance(path);
        Assert.Equal(before.PageContent, after.PageContent);
        Assert.Equal(before.AnnotationAppearance, after.AnnotationAppearance);
        Assert.Equal(before.ImageBytes, after.ImageBytes);
    }

    [Fact]
    public async Task PreparedDocumentPassesNormalImportAndKeepsItsVisibleContentsThroughExport()
    {
        var path = WriteFixture("/Dest [3 0 R /Fit]");
        using var repository = new MemoryRepository();
        var pdf = new PdfService(repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => pdf.ImportAsync(path));
        Assert.Empty(repository.Assets);
        await OfficePdfPreparation.PrepareAsync(path);
        var page = Assert.Single(await pdf.ImportAsync(path));
        var before = await pdf.RenderAsync(page, 2);
        var export = Path.Combine(_directory, "annotated-export.pdf");
        await pdf.ExportAsync(export, new NotebookDocument { Pages = [page] });
        var imported = Assert.Single(await pdf.ImportAsync(export));
        var after = await pdf.RenderAsync(imported, 2);
        Assert.Equal(before.PixelWidth, after.PixelWidth);
        Assert.Equal(before.PixelHeight, after.PixelHeight);
        var stride = before.PixelWidth * 4;
        var originalPixels = new byte[stride * before.PixelHeight];
        var exportedPixels = new byte[stride * after.PixelHeight];
        before.CopyPixels(originalPixels, stride, 0);
        after.CopyPixels(exportedPixels, stride, 0);
        Assert.Equal(originalPixels, exportedPixels);
    }

    [Fact]
    public async Task CancellationDoesNotChangeOriginalConvertedPdfOrLeaveATemporaryFile()
    {
        var path = WriteFixture("/Dest [3 0 R /Fit]");
        var before = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfficePdfPreparation.PrepareAsync(path, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task InvalidPdfIsNotOverwritten()
    {
        var path = Path.Combine(_directory, "broken.pdf");
        byte[] before = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, before);
        await Assert.ThrowsAnyAsync<Exception>(() => OfficePdfPreparation.PrepareAsync(path));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FailedReplacementLeavesConvertedPdfIntactAndRemovesStagedOutput()
    {
        var path = WriteFixture("/Dest [3 0 R /Fit]");
        var before = await File.ReadAllBytesAsync(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => OfficePdfPreparation.PrepareAsync(path));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(_directory));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public async Task NormalizationIsIdempotent()
    {
        var path = WriteFixture("/Dest [3 0 R /Fit]");
        await OfficePdfPreparation.PrepareAsync(path);
        var once = await File.ReadAllBytesAsync(path);
        await OfficePdfPreparation.PrepareAsync(path);
        Assert.Equal(once, await File.ReadAllBytesAsync(path));
    }

    private string WriteFixture(string annotationNavigation, string extraAction = "null")
    {
        // Raw, synthetic PDF so both direct/indirect actions are represented
        // exactly. The page has selectable text, vector marks and an image;
        // the link also has a visible border and a separate appearance stream.
        const string pageContent = "1 0 0 rg 20 20 30 30 re f\nBT /F1 14 Tf 20 220 Td (Synthetic lecture text) Tj ET\nq 40 0 0 20 120 40 cm /Im1 Do Q\n";
        const string appearance = "0 0.6 0 rg 0 0 60 30 re f\n";
        const string image = "FF00000000FF>";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 300] /Resources << /Font << /F1 7 0 R >> /XObject << /Im1 8 0 R >> >> /Contents 4 0 R /Annots [5 0 R] >>",
            Stream(pageContent),
            "<< /Type /Annot /Subtype /Link /Rect [20 100 80 130] /Border [0 0 2] /C [0 0 1] /Contents (Visible link note) /AP << /N 6 0 R >> " + annotationNavigation + " >>",
            Stream(appearance, "/Type /XObject /Subtype /Form /BBox [0 0 60 30] /Resources << >>"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream(image, "/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode"),
            extraAction
        };
        var result = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(result.ToString()));
            result.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(result.ToString());
        result.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) result.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        result.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(_directory, "converted.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(result.ToString()));
        return path;
    }

    private static string Stream(string content, string dictionary = "") =>
        $"<< {dictionary} /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static PdfDocument Read(string path) => PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), PdfDocumentOpenMode.Import);

    private static Appearance ReadAppearance(string path)
    {
        using var document = Read(path);
        var page = document.Pages[0];
        var annotation = page.Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!;
        var appearance = annotation.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!;
        var image = page.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!.Elements.GetDictionary("/Im1")!;
        return new(page.Contents.CreateSingleContent().Stream.UnfilteredValue,
            appearance.Stream.UnfilteredValue, image.Stream.UnfilteredValue,
            annotation.Elements.GetArray("/Rect")!.ToString(), annotation.Elements.GetArray("/Border")!.ToString(),
            annotation.Elements.GetString("/Contents"));
    }

    private sealed record Appearance(byte[] PageContent, byte[] AnnotationAppearance, byte[] ImageBytes, string Rectangle, string Border, string Text);

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
