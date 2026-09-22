using System.IO;
using System.IO.Compression;
using System.Xml;

namespace Moye.Services;

/// <summary>Checks Office packages without opening them in an Office application.</summary>
public static class OfficeDocumentValidation
{
    private const int MaximumEntries = 30_000;
    private const long MaximumXmlBytes = 16 * 1024 * 1024;
    private const long MaximumMetadataBytes = 64 * 1024 * 1024;
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static bool SupportsExtension(string extension) => extension.ToLowerInvariant() is ".docx" or ".pptx" or ".ppsx";

    public static void Validate(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportsExtension(extension)) throw new NotSupportedException("Choose a DOCX, PPTX or PPSX document, or export the document to PDF first.");
        using var input = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        var signatureLength = input.Read(signature);
        if (signatureLength == 8 && signature.SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            throw new InvalidDataException("Password-protected Office documents are not supported. Save an unencrypted DOCX or PPTX copy, or export a PDF from Office first.");
        if (signatureLength < 4 || signature[0] != 'P' || signature[1] != 'K')
            throw new InvalidDataException("This is not a readable Office document package. Save a fresh DOCX or PPTX copy from the source application.");
        input.Position = 0;
        using var package = new ZipArchive(input, ZipArchiveMode.Read);
        if (package.Entries.Count > MaximumEntries) throw new InvalidDataException("The Office document contains too many package parts to import.");
        var mainPart = extension == ".docx" ? "word/document.xml" : "ppt/presentation.xml";
        if (package.GetEntry("[Content_Types].xml") is null || package.GetEntry(mainPart) is null)
            throw new InvalidDataException("The document package does not match its file extension. Save it again as DOCX or PPTX.");
        long metadataBytes = 0;
        foreach (var entry in package.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (name.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("/activeX/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Documents containing macros or ActiveX controls cannot be imported. Save a plain DOCX/PPTX copy or export a PDF from Office.");
            var isRelationships = name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
            if (!isRelationships && !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            metadataBytes += entry.Length;
            if (entry.Length > MaximumXmlBytes || metadataBytes > MaximumMetadataBytes)
                throw new InvalidDataException("The Office document has unusually large XML metadata. Export it to PDF first.");
            using var part = entry.Open();
            using var reader = XmlReader.Create(part, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true
            });
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isRelationships || reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship" || reader.NamespaceURI != RelationshipNamespace) continue;
                var external = reader.GetAttribute("TargetMode");
                var type = reader.GetAttribute("Type") ?? "";
                if (!string.Equals(external, "External", StringComparison.OrdinalIgnoreCase)) continue;
                // A hyperlink is not fetched while converting. Linked images,
                // templates, media and OLE objects can be fetched on opening.
                if (type is "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" or
                    "http://purl.oclc.org/ooxml/officeDocument/relationships/hyperlink") continue;
                throw new InvalidDataException("This document contains linked images, templates or other external content. " +
                    "Embed the linked content in Office, or export a PDF there, then import that copy.");
            }
        }
    }
}
