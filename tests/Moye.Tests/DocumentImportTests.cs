using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Moye.Models;
using Moye.Services;

namespace Moye.Tests;

/// <summary>Routing, ownership and failure tests; no installed Office app is launched.</summary>
public sealed class DocumentImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MoyeDocumentImport-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] SourceBytes = Encoding.UTF8.GetBytes("Synthetic source with 中文; never modify this file.");
    private static readonly byte[] ConvertedPdf = Encoding.ASCII.GetBytes("%PDF-1.7 synthetic converter output");

    public DocumentImportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".PDF")]
    public async Task PdfUsesExistingImporterWithoutOfficeConversion(string extension)
    {
        var source = WriteSource(extension);
        var converter = new FakeConverter();
        var pdf = new FakePdf();
        var pages = await new DocumentImportService(pdf, converter).ImportAsync(source);
        Assert.Equal(0, converter.Calls);
        Assert.Equal(1, pdf.Calls);
        Assert.Equal(Path.GetFullPath(source), Path.GetFullPath(pdf.ImportedPath!));
        Assert.Same(pdf.Pages, pages);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".DOCX")]
    [InlineData(".pptx")]
    [InlineData(".ppsx")]
    [InlineData(".odt")]
    [InlineData(".odp")]
    public async Task OfficeInputUsesIsolatedCopyAndDeletesConversionFilesAfterImport(string extension)
    {
        var source = WriteSource(extension);
        var converter = new FakeConverter();
        var pdf = new FakePdf();
        converter.Convert = async (input, output, token) =>
        {
            Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(input));
            Assert.Equal(Path.GetExtension(source), Path.GetExtension(input), ignoreCase: true);
            Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(input, token));
            Assert.Equal(Path.GetDirectoryName(input), Path.GetDirectoryName(output));
            await File.WriteAllBytesAsync(output, ConvertedPdf, token);
        };

        var pages = await new DocumentImportService(pdf, converter).ImportAsync(source);
        Assert.Equal(1, converter.Calls);
        Assert.Equal(1, pdf.Calls);
        Assert.Same(pdf.Pages, pages);
        Assert.Equal(ConvertedPdf, pdf.ImportedBytes);
        Assert.Equal(converter.OutputPath, pdf.ImportedPath);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
        AssertConversionDirectoryRemoved(converter);
    }

    [Fact]
    public async Task ConverterFailureLeavesSourceUntouchedAndNeverCallsPdfImporter()
    {
        var source = WriteSource(".docx");
        var converter = new FakeConverter
        {
            Convert = async (_, output, token) =>
            {
                await File.WriteAllBytesAsync(output, [1, 2, 3], token);
                throw new InvalidOperationException("Install Microsoft Word or LibreOffice to import this document.");
            }
        };
        var pdf = new FakePdf();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DocumentImportService(pdf, converter).ImportAsync(source));
        Assert.Contains("Microsoft Word", error.Message);
        Assert.Equal(0, pdf.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
        AssertConversionDirectoryRemoved(converter);
    }

    [Fact]
    public async Task InvalidConvertedPdfReturnsNoPagesAndCleansTemporaryDirectory()
    {
        var source = WriteSource(".pptx");
        var converter = new FakeConverter();
        var pdf = new FakePdf { Import = (_, _) => throw new InvalidDataException("Converted PDF is damaged.") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DocumentImportService(pdf, converter).ImportAsync(source));
        Assert.Equal(1, converter.Calls);
        Assert.Equal(1, pdf.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
        AssertConversionDirectoryRemoved(converter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyConverterOutputNeverReachesPdfImporter(bool emptyFile)
    {
        var source = WriteSource(".docx");
        var converter = new FakeConverter
        {
            Convert = async (_, output, token) =>
            {
                if (emptyFile) await File.WriteAllBytesAsync(output, [], token);
            }
        };
        var pdf = new FakePdf();
        await Assert.ThrowsAsync<InvalidDataException>(() => new DocumentImportService(pdf, converter).ImportAsync(source));
        Assert.Equal(0, pdf.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
        AssertConversionDirectoryRemoved(converter);
    }

    [Fact]
    public async Task CancellationDuringConversionCleansPartialOutputAndKeepsSource()
    {
        var source = WriteSource(".docx");
        using var cancellation = new CancellationTokenSource();
        var converter = new FakeConverter
        {
            Convert = async (_, output, token) =>
            {
                await File.WriteAllBytesAsync(output, [1, 2, 3], token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };
        var pdf = new FakePdf();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocumentImportService(pdf, converter).ImportAsync(source, cancellation.Token));
        Assert.Equal(0, pdf.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
        AssertConversionDirectoryRemoved(converter);
    }

    [Fact]
    public async Task CancellationAfterConversionStopsBeforeAnyPdfAssetCanBeImported()
    {
        var source = WriteSource(".pptx");
        using var cancellation = new CancellationTokenSource();
        var converter = new FakeConverter
        {
            Convert = async (_, output, token) =>
            {
                await File.WriteAllBytesAsync(output, ConvertedPdf, token);
                cancellation.Cancel();
            }
        };
        var pdf = new FakePdf();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocumentImportService(pdf, converter).ImportAsync(source, cancellation.Token));
        Assert.Equal(0, pdf.Calls);
        AssertConversionDirectoryRemoved(converter);
    }

    [Fact]
    public async Task AlreadyCancelledImportNeverStartsConversionOrPdfImport()
    {
        var source = WriteSource(".docx");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var converter = new FakeConverter();
        var pdf = new FakePdf();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocumentImportService(pdf, converter).ImportAsync(source, cancellation.Token));
        Assert.Equal(0, converter.Calls);
        Assert.Equal(0, pdf.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".png")]
    [InlineData(".xlsx")]
    [InlineData(".doc")]
    [InlineData(".rtf")]
    [InlineData(".ppt")]
    [InlineData(".pps")]
    public async Task UnsupportedExtensionIsRejectedBeforeConversion(string extension)
    {
        var source = WriteSource(extension);
        var converter = new FakeConverter();
        var pdf = new FakePdf();
        await Assert.ThrowsAsync<NotSupportedException>(() => new DocumentImportService(pdf, converter).ImportAsync(source));
        Assert.Equal(0, converter.Calls);
        Assert.Equal(0, pdf.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task ConsecutiveImportsUseDifferentTemporaryCopies()
    {
        var source = WriteSource(".docx");
        var converter = new FakeConverter();
        var service = new DocumentImportService(new FakePdf(), converter);
        await service.ImportAsync(source);
        var firstCopy = converter.InputPath;
        AssertConversionDirectoryRemoved(converter);
        await service.ImportAsync(source);
        Assert.NotEqual(firstCopy, converter.InputPath);
        AssertConversionDirectoryRemoved(converter);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(source));
    }

    private string WriteSource(string extension)
    {
        var path = Path.Combine(_directory, "Lecture 中文 notes" + extension);
        File.WriteAllBytes(path, SourceBytes);
        return path;
    }

    private static void AssertConversionDirectoryRemoved(FakeConverter converter)
    {
        Assert.NotNull(converter.InputPath);
        Assert.False(File.Exists(converter.InputPath), "The isolated Office input copy must be removed.");
        Assert.False(File.Exists(converter.OutputPath), "The temporary converted PDF must be removed.");
        Assert.False(Directory.Exists(Path.GetDirectoryName(converter.InputPath)), "The service owns and removes the conversion directory.");
    }

    private sealed class FakeConverter : IOfficePdfConverter
    {
        public int Calls { get; private set; }
        public string? InputPath { get; private set; }
        public string? OutputPath { get; private set; }
        public Func<string, string, CancellationToken, Task>? Convert { get; set; }
        public async Task ConvertAsync(string sourcePath, string outputPdfPath, CancellationToken cancellationToken = default)
        {
            Calls++; InputPath = sourcePath; OutputPath = outputPdfPath;
            if (Convert is not null) await Convert(sourcePath, outputPdfPath, cancellationToken);
            else await File.WriteAllBytesAsync(outputPdfPath, ConvertedPdf, cancellationToken);
        }
    }

    private sealed class FakePdf : IPdfService
    {
        public int Calls { get; private set; }
        public string? ImportedPath { get; private set; }
        public byte[]? ImportedBytes { get; private set; }
        public IReadOnlyList<NotePage> Pages { get; } = [new() { Pdf = new() { AssetId = "synthetic-pdf", PageIndex = 0 } }, new() { Pdf = new() { AssetId = "synthetic-pdf", PageIndex = 1 } }];
        public Func<string, CancellationToken, Task<IReadOnlyList<NotePage>>>? Import { get; init; }
        public async Task<IReadOnlyList<NotePage>> ImportAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++; ImportedPath = path;
            ImportedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return Import is null ? Pages : await Import(path, cancellationToken);
        }
        public Task<BitmapSource> RenderAsync(NotePage page, double scale, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(string path, NotebookDocument document, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
