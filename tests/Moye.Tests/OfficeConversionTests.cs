using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml;
using Moye.Services;

namespace Moye.Tests;

/// <summary>Package and process-boundary checks. These tests never start or automate Office.</summary>
public sealed class OfficeConversionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MoyeOfficeValidation-" + Guid.NewGuid().ToString("N"));
    public OfficeConversionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(".docx")]
    [InlineData(".pptx")]
    [InlineData(".PPSX")]
    public void StandardPackagesWithEmbeddedContentAndExternalHyperlinksPass(string extension)
    {
        var path = Package(extension,
            "<Relationship Id='embedded' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='media/image.png'/>" +
            "<Relationship Id='link' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink' Target='https://example.org/course' TargetMode='External'/>" +
            "<Relationship Id='strict-link' Type='http://purl.oclc.org/ooxml/officeDocument/relationships/hyperlink' Target='https://example.org/lecture' TargetMode='External'/>");
        OfficeDocumentValidation.Validate(path);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("attachedTemplate")]
    [InlineData("oleObject")]
    [InlineData("video")]
    public void ExternalContentIsRejectedBeforeAnyOfficeApplicationIsNeeded(string relationshipType)
    {
        var path = Package(".docx", $"<Relationship Id='external' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/{relationshipType}' Target='https://example.invalid/private-content' TargetMode='External'/>");
        var error = Assert.Throws<InvalidDataException>(() => OfficeDocumentValidation.Validate(path));
        Assert.Contains("Embed", error.Message);
        Assert.Contains("PDF", error.Message);
    }

    [Fact]
    public void PasswordProtectedCompoundPackageIsRejectedWithoutStartingOffice()
    {
        var path = Path.Combine(_directory, "encrypted.docx");
        File.WriteAllBytes(path, [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0, 0, 0, 0]);
        var error = Assert.Throws<InvalidDataException>(() => OfficeDocumentValidation.Validate(path));
        Assert.Contains("Password-protected", error.Message);
    }

    [Theory]
    [InlineData("word/vbaProject.bin")]
    [InlineData("word/activeX/activeX1.xml")]
    public void RenamedMacroOrControlPackageCannotBypassExtensionCheck(string partName)
    {
        var path = Package(".docx", extraPart: (partName, "<control/>"));
        Assert.Throws<InvalidDataException>(() => OfficeDocumentValidation.Validate(path));
    }

    [Theory]
    [InlineData("word/document.xml")]
    [InlineData("word/_rels/document.xml.rels")]
    public void DtdInMainDocumentOrRelationshipsIsRejected(string partName)
    {
        var path = Package(".docx", replacementPart: (partName,
            "<!DOCTYPE document [<!ENTITY external SYSTEM 'file:///not-read-by-this-test'>]><document>&external;</document>"));
        Assert.Throws<XmlException>(() => OfficeDocumentValidation.Validate(path));
    }

    [Fact]
    public void MismatchedDocumentTypeIsRejected()
    {
        var original = Package(".docx");
        var renamed = Path.ChangeExtension(original, ".pptx");
        File.Move(original, renamed);
        Assert.Throws<InvalidDataException>(() => OfficeDocumentValidation.Validate(renamed));
    }

    [Fact]
    public void OversizedCompressedXmlIsRejectedBeforeParsing()
    {
        var path = Package(".docx", replacementPart: ("word/document.xml", "<document>" + new string('x', 16 * 1024 * 1024) + "</document>"));
        var error = Assert.Throws<InvalidDataException>(() => OfficeDocumentValidation.Validate(path));
        Assert.Contains("large XML", error.Message);
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".ppt")]
    [InlineData(".rtf")]
    [InlineData(".docm")]
    [InlineData(".pptm")]
    [InlineData(".odt")]
    [InlineData(".pdf")]
    public void MicrosoftBackendDoesNotAdvertiseUnsupportedFormats(string extension)
    {
        Assert.False(OfficeDocumentValidation.SupportsExtension(extension));
        Assert.False(OfficeConversionProcess.IsAvailableFor(extension));
    }

    [Fact]
    public void WorkerArgumentsKeepSpecialCharactersInSeparatePathArguments()
    {
        var executable = Path.Combine(_directory, "Moye.exe");
        var source = Path.Combine(_directory, "Lecture 1 & 'quoted' $(text).docx");
        var output = Path.Combine(_directory, "converted pages.pdf");
        var start = (ProcessStartInfo)typeof(OfficeConversionProcess).GetMethod("CreateStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [executable, source, output])!;
        Assert.Equal(executable, start.FileName);
        Assert.Equal(["--convert-office", source, output], start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.True(start.RedirectStandardError);
        Assert.True(start.RedirectStandardOutput);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public async Task CancelledConversionDoesNotReadOrLaunchTheMissingDocument()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficeConversionProcess().ConvertAsync(
            Path.Combine(_directory, "does-not-exist.docx"), Path.Combine(_directory, "output.pdf"), cancellation.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ConversionDoesNotOverwriteAnExistingDestination()
    {
        var source = Package(".docx");
        var output = Path.Combine(_directory, "preserve.pdf");
        File.WriteAllText(output, "existing output");
        await Assert.ThrowsAsync<IOException>(() => new OfficeConversionProcess().ConvertAsync(source, output));
        Assert.Equal("existing output", File.ReadAllText(output));
    }

    private string Package(string extension, string relationships = "", (string Name, string Content)? extraPart = null,
        (string Name, string Content)? replacementPart = null)
    {
        var directory = extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ? "word" : "ppt";
        var mainPart = directory == "word" ? "document.xml" : "presentation.xml";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'/>",
            [$"{directory}/{mainPart}"] = "<document/>",
            [$"{directory}/_rels/{mainPart}.rels"] = "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" + relationships + "</Relationships>"
        };
        if (extraPart is { } extra) parts[extra.Name] = extra.Content;
        if (replacementPart is { } replacement) parts[replacement.Name] = replacement.Content;
        var path = Path.Combine(_directory, "fixture" + extension);
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, value) in parts)
        {
            using var part = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
            using var writer = new StreamWriter(part, new UTF8Encoding(false)); writer.Write(value);
        }
        return path;
    }
}
