using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;
using Moye.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Moye.Tests;

public sealed class PdfImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MoyePdfImport-" + Guid.NewGuid().ToString("N"));
    public PdfImportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("<< >>")]
    [InlineData("<< /Fields [] >>")]
    [InlineData("<< /Fields [] /NeedAppearances false /DA (/Helvetica 12 Tf) /CO [] /SigFlags 0 >>")]
    public async Task EmptyFormMetadataImportsAndExportsWithoutChangingOriginalPage(string form)
    {
        var source = await WritePdfAsync("/AcroForm " + form);
        var originalBytes = await File.ReadAllBytesAsync(source);
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var page = Assert.Single(await service.ImportAsync(source));
        Assert.Equal(originalBytes, Assert.Single(repository.Assets.Values).Bytes);
        var before = await service.RenderAsync(page, 1);
        Assert.Contains(Pixels(before).Chunk(4), pixel => pixel[2] > 200 && pixel[1] < 50 && pixel[0] < 50);

        var exported = Path.Combine(_directory, "round-trip.pdf");
        await service.ExportAsync(exported, new NotebookDocument { Pages = [page] });
        var roundTrip = Assert.Single(await service.ImportAsync(exported));
        var after = await service.RenderAsync(roundTrip, 1);
        Assert.Equal(before.PixelWidth, after.PixelWidth);
        Assert.Equal(before.PixelHeight, after.PixelHeight);
        Assert.Equal(Pixels(before), Pixels(after));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullOptionalMetadataAndUriAnnotationRemainSupported(bool indirectNull)
    {
        var nullValue = indirectNull ? "6 0 R" : "null";
        var source = await WritePdfAsync($"/AcroForm {nullValue} /Perms {nullValue}", "/Annots [5 0 R]",
            ["<< /Type /Annot /Subtype /Link /Rect [20 20 50 50] /Border [0 0 0] " +
             $"/Dest {nullValue} /AA {nullValue} /A << /S /URI /URI (https://example.org/course) >> >>", "null"]);
        using var repository = new MemoryRepository();
        var service = new PdfService(repository);
        var page = Assert.Single(await service.ImportAsync(source));
        var exported = Path.Combine(_directory, "uri-round-trip.pdf");
        await service.ExportAsync(exported, new NotebookDocument { Pages = [page] });
        using var result = PdfReader.Open(exported, PdfDocumentOpenMode.Import);
        var annotations = result.Pages[0].Elements.GetArray("/Annots")!;
        Assert.Single(annotations.Elements.Cast<PdfItem>());
        Assert.Equal("https://example.org/course", annotations.Elements.GetDictionary(0)!.Elements.GetDictionary("/A")!.Elements.GetString("/URI"));
        Assert.Single(await service.ImportAsync(exported));
    }

    [Theory]
    [InlineData("fields", "/AcroForm << /Fields [5 0 R] >>", "", "<< /FT /Tx /T (Input) >>")]
    [InlineData("invalid-fields", "/AcroForm << /Fields 123 >>", "", "null")]
    [InlineData("xfa", "/AcroForm << /Fields [] /XFA [(template) 5 0 R] >>", "", "<< /Length 0 >>\nstream\n\nendstream")]
    [InlineData("signature-flags", "/AcroForm << /Fields [] /SigFlags 1 >>", "", "null")]
    [InlineData("calculations", "/AcroForm << /Fields [] /CO [5 0 R] >>", "", "<< /FT /Tx /T (Input) >>")]
    [InlineData("widget", "/AcroForm << /Fields [] >>", "/Annots [5 0 R]", "<< /Type /Annot /Subtype /Widget /Rect [20 20 50 50] /FT /Tx /T (Input) >>")]
    [InlineData("signature", "/Perms << /DocMDP 5 0 R >>", "", "<< /Type /Sig >>")]
    public async Task InteractiveContentIsStillRejectedBeforeStoringAnyAsset(string kind, string catalog, string page, string extraObject)
    {
        var source = await WritePdfAsync(catalog, page, [extraObject], fileName: kind + ".pdf");
        using var repository = new MemoryRepository();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new PdfService(repository).ImportAsync(source));
        if (kind == "widget") Assert.Contains("PDF page 1:", exception.Message);
        Assert.Empty(repository.Assets);
    }

    [Fact]
    public async Task EncryptedDocumentIsStillRejectedBeforeStoringAnyAsset()
    {
        var source = Path.Combine(_directory, "encrypted.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.UserPassword = "fixture-password";
            document.Save(source);
        }
        using var repository = new MemoryRepository();
        await Assert.ThrowsAsync<InvalidDataException>(() => new PdfService(repository).ImportAsync(source));
        Assert.Empty(repository.Assets);
    }

    [Theory]
    [InlineData(1, 14400)]
    [InlineData(14400, 1)]
    public async Task ExtremeAspectRatioPageUsesBoundedImportProbe(double width, double height)
    {
        var source = await WritePdfAsync(width: width, height: height);
        using var repository = new MemoryRepository();
        var page = Assert.Single(await new PdfService(repository).ImportAsync(source));
        Assert.InRange(page.Width / page.Height, width / height * .99, width / height * 1.01);
        Assert.Single(repository.Assets);
    }

    [Fact]
    public async Task CancelledImportDoesNotStoreAnAsset()
    {
        var source = await WritePdfAsync("/AcroForm << /Fields [] >>");
        using var repository = new MemoryRepository();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfService(repository).ImportAsync(source, cancellation.Token));
        Assert.Empty(repository.Assets);
    }

    private async Task<string> WritePdfAsync(string catalog = "", string page = "", string[]? extraObjects = null,
        double width = 240, double height = 300, string fileName = "source.pdf")
    {
        // Build the optional entries literally: some PDF writers remove null
        // values or empty form dictionaries and would hide this regression.
        const string drawing = "1 0 0 rg 20 20 30 30 re f\n";
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width.ToString(CultureInfo.InvariantCulture)} {height.ToString(CultureInfo.InvariantCulture)}] /Resources << >> /Contents 4 0 R {page} >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(drawing)} >>\nstream\n{drawing}endstream"
        };
        objects.AddRange(extraObjects ?? []);
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
        var path = Path.Combine(_directory, fileName);
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes(result.ToString()));
        return path;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var result = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(result, converted.PixelWidth * 4, 0);
        return result;
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
