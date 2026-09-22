using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Moye.Services;

/// <summary>Checks ODF packages before an external document converter opens them.</summary>
public static class OdfDocumentValidation
{
    private const long MaximumXmlBytes = 16 * 1024 * 1024;
    private const long MaximumMetadataBytes = 64 * 1024 * 1024;
    private const string ManifestNamespace = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private const string ScriptNamespace = "urn:oasis:names:tc:opendocument:xmlns:script:1.0";
    private const string XLinkNamespace = "http://www.w3.org/1999/xlink";
    private const string TextNamespace = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string DrawingNamespace = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string OfficeNamespace = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    public static bool SupportsExtension(string extension) => extension.ToLowerInvariant() is ".odt" or ".odp";

    public static void Validate(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportsExtension(extension)) throw new NotSupportedException("Choose an ODT or ODP document, or export it to PDF first.");
        using var input = File.OpenRead(path);
        using var package = new ZipArchive(input, ZipArchiveMode.Read);
        if (package.Entries.Count > 30_000) throw new InvalidDataException("The OpenDocument file contains too many package parts.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in package.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') ||
                entry.FullName.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("The OpenDocument file contains invalid or duplicate package paths.");
            if (entry.FullName.Split('/').Any(part => part.Equals("Basic", StringComparison.OrdinalIgnoreCase) || part.Equals("Scripts", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("OpenDocument files containing macros cannot be imported. Export a PDF from the source application.");
        }
        var mimetype = package.GetEntry("mimetype");
        if (mimetype is null || mimetype.Length > 128 || package.GetEntry("META-INF/manifest.xml") is null || package.GetEntry("content.xml") is null)
            throw new InvalidDataException("This is not a complete OpenDocument package.");
        using (var mimeStream = mimetype.Open())
        using (var mimeReader = new StreamReader(mimeStream, Encoding.UTF8, false))
        {
            var expected = extension == ".odt" ? "application/vnd.oasis.opendocument.text" : "application/vnd.oasis.opendocument.presentation";
            if (mimeReader.ReadToEnd() != expected) throw new InvalidDataException("The OpenDocument package does not match its file extension.");
        }
        long metadataBytes = 0;
        foreach (var entry in package.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            metadataBytes += entry.Length;
            if (entry.Length > MaximumXmlBytes || metadataBytes > MaximumMetadataBytes)
                throw new InvalidDataException("The OpenDocument file has unusually large XML metadata. Export it to PDF first.");
            using var part = entry.Open();
            using var reader = XmlReader.Create(part, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes, MaxCharactersFromEntities = 0, IgnoreComments = true
            });
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.NamespaceURI == ManifestNamespace && reader.LocalName == "encryption-data")
                    throw new InvalidDataException("Password-protected OpenDocument files cannot be imported. Save an unencrypted copy or export a PDF.");
                if (reader.NamespaceURI == ScriptNamespace || reader.NamespaceURI == OfficeNamespace && reader.LocalName == "script")
                    throw new InvalidDataException("OpenDocument files containing scripts or macro events cannot be imported. Export a PDF from the source application.");
                if (reader.MoveToFirstAttribute())
                {
                    do
                    {
                        if (reader.NamespaceURI == ScriptNamespace)
                            throw new InvalidDataException("OpenDocument files containing scripts or macro events cannot be imported. Export a PDF from the source application.");
                    } while (reader.MoveToNextAttribute());
                    reader.MoveToElement();
                }
                if (!string.IsNullOrEmpty(reader.GetAttribute("base", "http://www.w3.org/XML/1998/namespace")))
                    throw new InvalidDataException("OpenDocument files with alternate resource locations cannot be imported. Embed the content or export a PDF.");
                var reference = reader.GetAttribute("href", XLinkNamespace);
                if (string.IsNullOrEmpty(reference)) continue;
                if (reader.LocalName == "a" && reader.NamespaceURI is TextNamespace or DrawingNamespace) continue;
                ValidateEmbeddedReference(entry.FullName, reference, names);
            }
        }
    }

    private static void ValidateEmbeddedReference(string part, string reference, HashSet<string> names)
    {
        if (reference.StartsWith('#')) return;
        var decoded = Uri.UnescapeDataString(reference).Replace('\\', '/');
        if (decoded.StartsWith('/') || Uri.TryCreate(decoded, UriKind.Absolute, out _)) RejectExternalContent();
        var resource = decoded.Split(['#', '?'], 2)[0];
        var baseDirectory = part.Contains('/') ? part[..(part.LastIndexOf('/') + 1)] : "";
        var segments = new List<string>();
        foreach (var segment in (baseDirectory + resource).Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) RejectExternalContent();
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        var name = string.Join('/', segments);
        if (!names.Contains(name) && !names.Any(entry => entry.StartsWith(name + "/", StringComparison.Ordinal))) RejectExternalContent();
    }

    private static void RejectExternalContent() => throw new InvalidDataException(
        "This OpenDocument file contains linked images, templates, objects, or other external content. Embed the content or export a PDF from the source application.");
}
