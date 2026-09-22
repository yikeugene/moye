using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Moye.Services;

namespace Moye.Tests;

public sealed class LibreOfficeConversionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("Moye-LibreOffice-tests-").FullName;
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nfixture conversion output\n%%EOF\n");

    [Theory]
    [InlineData(".docx", "writer_pdf_Export")]
    [InlineData(".odt", "writer_pdf_Export")]
    [InlineData(".PPTX", "impress_pdf_Export")]
    [InlineData(".ppsx", "impress_pdf_Export")]
    [InlineData(".odp", "impress_pdf_Export")]
    public void UsesSeparateArgumentsAndTheCorrectStaticPdfFilter(string extension, string filter)
    {
        var input = Path.Combine(_directory, "input with spaces & symbols" + extension);
        var output = Path.Combine(_directory, "output directory");
        var profile = Path.Combine(_directory, "profile with spaces #1");
        var executable = Path.Combine(_directory, "LibreOffice program", "soffice.com");
        var start = LibreOfficePdfConverter.CreateStartInfo(executable, input, output, profile);

        Assert.True(LibreOfficePdfConverter.SupportsExtension(extension));
        Assert.True(LibreOfficePdfConverter.SupportsExtension(input));
        Assert.Equal(executable, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput);
        Assert.Empty(start.Arguments);
        Assert.Equal(input, start.ArgumentList[^1]);
        Assert.Equal(output, start.ArgumentList[^2]);
        var profileUri = new Uri(Assert.Single(start.ArgumentList, a => a.StartsWith("-env:UserInstallation=", StringComparison.Ordinal)).Split('=', 2)[1]);
        Assert.Equal(Path.TrimEndingDirectorySeparator(profile), Path.TrimEndingDirectorySeparator(profileUri.LocalPath));
        Assert.Contains("%20", profileUri.AbsoluteUri);
        Assert.Contains("%23", profileUri.AbsoluteUri);
        Assert.Contains("--headless", start.ArgumentList);
        var conversion = start.ArgumentList[start.ArgumentList.IndexOf("--convert-to") + 1];
        Assert.StartsWith("pdf:" + filter + ":", conversion);
        using var options = JsonDocument.Parse(conversion[(conversion.IndexOf('{'))..]);
        foreach (var name in new[] { "ExportFormFields", "ExportBookmarks", "ExportBookmarksToPDFDestination", "ExportLinksRelativeFsys" })
        {
            Assert.Equal("boolean", options.RootElement.GetProperty(name).GetProperty("type").GetString());
            Assert.Equal("false", options.RootElement.GetProperty(name).GetProperty("value").GetString());
        }
        Assert.Equal("boolean", options.RootElement.GetProperty("ExportHiddenSlides").GetProperty("type").GetString());
        Assert.Equal("true", options.RootElement.GetProperty("ExportHiddenSlides").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("sample.doc")]
    [InlineData("sample.ppt")]
    [InlineData("sample.rtf")]
    [InlineData("sample.xlsx")]
    [InlineData("sample.docm")]
    [InlineData("sample.pdf")]
    public void RejectsFormatsOutsideTheImportScope(string path)
    {
        Assert.False(LibreOfficePdfConverter.SupportsExtension(path));
        Assert.False(LibreOfficePdfConverter.IsAvailableFor(path));
    }

    [Fact]
    public async Task CreatesAnIsolatedRestrictedProfileForEachConversionAndPreservesTheSource()
    {
        var source = WriteDocument("my source.odt");
        var original = await File.ReadAllBytesAsync(source);
        var executable = WriteFile("soffice.com", []);
        var profiles = new List<string>();
        var jobs = new List<string>();
        var converter = new LibreOfficePdfConverter(executable, TimeSpan.FromMinutes(2), (start, _) =>
        {
            var profile = ProfilePath(start);
            profiles.Add(profile);
            jobs.Add(start.WorkingDirectory);
            Assert.Equal(source, start.ArgumentList[^1]);
            var configuration = XDocument.Load(Path.Combine(profile, "user", "registrymodifications.xcu"));
            AssertSetting(configuration, "/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel", "3");
            AssertSetting(configuration, "/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution", "true");
            AssertSetting(configuration, "/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks", "true");
            AssertSetting(configuration, "/org.openoffice.Office.Common/Security/Scripting", "DisableActiveContent", "true");
            AssertSetting(configuration, "/org.openoffice.Office.Common/Security/Scripting", "SecureURL", "");
            AssertSetting(configuration, "/org.openoffice.Office.Writer/Content/Update", "Link", "2");
            AssertSetting(configuration, "/org.openoffice.Office.Writer/Content/Update", "Field", "false");
            AssertSetting(configuration, "/org.openoffice.Office.Writer/Content/Update", "Chart", "false");
            WriteConvertedPdf(start, Pdf);
            return Task.FromResult(new LibreOfficePdfConverter.ConversionResult(0, ""));
        });

        await converter.ConvertAsync(source, Path.Combine(_directory, "first result.pdf"));
        await converter.ConvertAsync(source, Path.Combine(_directory, "second result.pdf"));

        Assert.Equal(2, profiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(jobs, path => Assert.False(Directory.Exists(path)));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(Path.Combine(_directory, "first result.pdf")));
    }

    [Fact]
    public async Task MissingDependencyDoesNotModifyTheOutput()
    {
        var source = WriteDocument("document.docx");
        var output = WriteFile("existing.pdf", "existing"u8.ToArray());
        var converter = new LibreOfficePdfConverter(Path.Combine(_directory, "not-installed", "soffice.com"));
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => converter.ConvertAsync(source, output));
        Assert.Contains("LibreOffice is not installed", exception.Message);
        Assert.Equal("existing", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotLookForAnInstallationOrChangeFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var converter = new LibreOfficePdfConverter(Path.Combine(_directory, "missing.com"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(
            Path.Combine(_directory, "absent.docx"), Path.Combine(_directory, "result.pdf"), cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeoutAwaitTheRunnerBeforeCleaningUp(bool timeout)
    {
        var source = WriteDocument("document.pptx");
        var executable = WriteFile("soffice.com", []);
        var output = WriteFile("existing.pdf", "existing"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var runnerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopped = false;
        string? job = null;
        var converter = new LibreOfficePdfConverter(executable, timeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(2), async (start, token) =>
        {
            job = start.WorkingDirectory;
            runnerStarted.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Assert.True(Directory.Exists(job)); stopped = true; }
            return new(0, "");
        });

        var conversion = converter.ConvertAsync(source, output, cancellation.Token);
        await runnerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout) cancellation.Cancel();
        if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => conversion);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion);
        Assert.True(stopped);
        Assert.False(Directory.Exists(job));
        Assert.Equal("existing", await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task FailedConversionDoesNotReplaceAnExistingOutput(int exitCode, bool createsFile)
    {
        var source = WriteDocument("document.odp");
        var executable = WriteFile("soffice.com", []);
        var output = WriteFile("existing.pdf", "existing"u8.ToArray());
        var converter = new LibreOfficePdfConverter(executable, TimeSpan.FromMinutes(2), (start, _) =>
        {
            if (createsFile) WriteConvertedPdf(start, Pdf);
            return Task.FromResult(new LibreOfficePdfConverter.ConversionResult(exitCode, "conversion error"));
        });
        await Assert.ThrowsAsync<IOException>(() => converter.ConvertAsync(source, output));
        Assert.Equal("existing", await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData("https://example.com/document.docx")]
    [InlineData(@"\\server\share\document.docx")]
    public async Task RejectsNetworkSourcesBeforeStartingAProcess(string source)
    {
        var converter = new LibreOfficePdfConverter(Path.Combine(_directory, "missing.com"));
        await Assert.ThrowsAsync<NotSupportedException>(() => converter.ConvertAsync(source, Path.Combine(_directory, "result.pdf")));
    }

    [Theory]
    [InlineData("image", "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0", "https://example.com/image.png")]
    [InlineData("image", "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0", "../../outside.png")]
    [InlineData("object", "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0", "file:///C:/outside.odt")]
    [InlineData("template", "urn:oasis:names:tc:opendocument:xmlns:style:1.0", "https://example.com/template.ott")]
    [InlineData("image", "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0", "%68%74%74%70%73%3A%2F%2Fexample.com/image.png")]
    public async Task OdfExternalResourcesAreRejectedBeforeLaunchingTheConverter(string element, string ns, string target)
    {
        var xml = new XElement(XName.Get(element, ns), new XAttribute(XName.Get("href", "http://www.w3.org/1999/xlink"), target)).ToString();
        var source = WriteDocument("external.odt", body: xml);
        var executable = WriteFile("soffice.com", []);
        bool ran = false;
        var converter = new LibreOfficePdfConverter(executable, TimeSpan.FromMinutes(2), (_, _) =>
        {
            ran = true;
            return Task.FromResult(new LibreOfficePdfConverter.ConversionResult(0, ""));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => converter.ConvertAsync(source, Path.Combine(_directory, "out.pdf")));
        Assert.False(ran);
    }

    [Fact]
    public void OdfInternalImagesAndNormalHyperlinksAreAllowed()
    {
        var source = WriteDocument("safe.odt", """
            <root xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
                  xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:xlink="http://www.w3.org/1999/xlink">
              <draw:image xlink:href="Pictures/image.png"/>
              <text:a xlink:href="https://example.com/">A normal hyperlink</text:a>
              <draw:a xlink:href="https://example.com/"/>
            </root>
            """);
        using (var package = ZipFile.Open(source, ZipArchiveMode.Update)) WritePart(package, "Pictures/image.png", "fixture");
        OdfDocumentValidation.Validate(source);
    }

    [Fact]
    public void OdfRejectsEncryptedManifestAndMismatchedMimeType()
    {
        var encrypted = WriteDocument("encrypted.odt", manifestExtra: "<manifest:file-entry manifest:full-path=\"content.xml\"><manifest:encryption-data/></manifest:file-entry>");
        Assert.Throws<InvalidDataException>(() => OdfDocumentValidation.Validate(encrypted));
        var mismatch = WriteDocument("wrong.odp", mimeOverride: "application/vnd.oasis.opendocument.text");
        Assert.Throws<InvalidDataException>(() => OdfDocumentValidation.Validate(mismatch));
    }

    [Fact]
    public void OdfRejectsMacroPartsInlineScriptsAndDtds()
    {
        var macro = WriteDocument("macro.odt");
        using (var package = ZipFile.Open(macro, ZipArchiveMode.Update)) WritePart(package, "Basic/Standard/Module1.xml", "<module/>");
        Assert.Throws<InvalidDataException>(() => OdfDocumentValidation.Validate(macro));
        var inlineScript = WriteDocument("inline.odt", "<office:script xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\">do not execute</office:script>");
        Assert.Throws<InvalidDataException>(() => OdfDocumentValidation.Validate(inlineScript));
        var macroEvent = WriteDocument("event.odt", "<event xmlns:script=\"urn:oasis:names:tc:opendocument:xmlns:script:1.0\" script:macro-name=\"do-not-execute\"/>");
        Assert.Throws<InvalidDataException>(() => OdfDocumentValidation.Validate(macroEvent));
        var dtd = WriteDocument("dtd.odt", "<!DOCTYPE content [<!ENTITY unsafe SYSTEM 'file:///C:/outside.txt'>]><content>&unsafe;</content>");
        Assert.Throws<XmlException>(() => OdfDocumentValidation.Validate(dtd));
    }

    [Fact]
    public void OdfChecksStylesForLinkedContentAndHonorsPreCancellation()
    {
        var source = WriteDocument("styles.odp");
        using (var package = ZipFile.Open(source, ZipArchiveMode.Update))
            WritePart(package, "styles.xml", "<background xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:href=\"https://example.com/background.png\"/>");
        Assert.Throws<InvalidDataException>(() => OdfDocumentValidation.Validate(source));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OdfDocumentValidation.Validate(source, cancellation.Token));
    }

    private string WriteFile(string name, byte[] bytes) { var path = Path.Combine(_directory, name); File.WriteAllBytes(path, bytes); return path; }
    private string WriteDocument(string name, string? body = null, string? manifestExtra = null, string? mimeOverride = null)
    {
        var path = Path.Combine(_directory, name);
        using var file = File.Create(path);
        using var package = new ZipArchive(file, ZipArchiveMode.Create);
        if (Path.GetExtension(name) is ".odt" or ".odp")
        {
            var type = Path.GetExtension(name) == ".odt" ? "text" : "presentation";
            WritePart(package, "mimetype", mimeOverride ?? "application/vnd.oasis.opendocument." + type);
            WritePart(package, "META-INF/manifest.xml", "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">" + manifestExtra + "</manifest:manifest>");
            WritePart(package, "content.xml", body ?? "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"><office:body><office:" + type + "/></office:body></office:document-content>");
        }
        else
        {
            WritePart(package, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            WritePart(package, Path.GetExtension(name) == ".docx" ? "word/document.xml" : "ppt/presentation.xml", "<document/>");
        }
        return path;
    }
    private static void WritePart(ZipArchive package, string name, string content)
    {
        using var writer = new StreamWriter(package.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
    private static string ProfilePath(ProcessStartInfo start) => new Uri(start.ArgumentList.Single(a => a.StartsWith("-env:UserInstallation=", StringComparison.Ordinal)).Split('=', 2)[1]).LocalPath;
    private static void WriteConvertedPdf(ProcessStartInfo start, byte[] bytes) => File.WriteAllBytes(
        Path.Combine(start.ArgumentList[start.ArgumentList.IndexOf("--outdir") + 1], Path.GetFileNameWithoutExtension(start.ArgumentList[^1]) + ".pdf"), bytes);
    private static void AssertSetting(XDocument document, string path, string name, string value)
    {
        XNamespace registry = "http://openoffice.org/2001/registry";
        var item = Assert.Single(document.Root!.Elements("item"), item => (string?)item.Attribute(registry + "path") == path);
        var property = Assert.Single(item.Elements("prop"), property => (string?)property.Attribute(registry + "name") == name);
        Assert.Equal(value, property.Element("value")!.Value);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
