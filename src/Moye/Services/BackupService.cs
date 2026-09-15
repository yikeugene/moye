using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Ink;
using Moye.Models;

namespace Moye.Services;

/// <summary>Versioned, portable backups. Restore validates the complete archive before writing assets.</summary>
public sealed class BackupService(INotebookRepository repository) : IBackupService
{
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxPayloadBytes = 512 * 1024 * 1024;
    private const int MaxDocumentBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 50_000;

    private sealed class Manifest
    {
        [JsonRequired] public string Format { get; set; } = "moye";
        [JsonRequired] public int Version { get; set; } = 2;
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        [JsonRequired] public List<DocumentEntry> Notebooks { get; set; } = [];
        [JsonRequired] public List<AssetEntry> Assets { get; set; } = [];
    }

    private sealed class DocumentEntry
    {
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public string Sha256 { get; set; } = "";
        [JsonRequired] public List<InkEntry> Inks { get; set; } = [];
    }

    private sealed class InkEntry
    {
        [JsonRequired] public string PageId { get; set; } = "";
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public string Sha256 { get; set; } = "";
    }

    private sealed class AssetEntry
    {
        [JsonRequired] public string Id { get; set; } = "";
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public string Sha256 { get; set; } = "";
        [JsonRequired] public string FileName { get; set; } = "";
        [JsonRequired] public string ContentType { get; set; } = "";
    }

    public Task ExportAsync(string path, IReadOnlyList<NotebookDocument> documents, CancellationToken cancellationToken = default)
    {
        var snapshots = documents.Select(Clone).ToList();
        return Task.Run(async () =>
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var manifest = new Manifest();
                        var assetIds = new HashSet<string>(StringComparer.Ordinal);
                        long total = 0;
                        var documentIds = new HashSet<string>(StringComparer.Ordinal);
                        if (snapshots.Count > 10_000) throw Invalid("The backup contains too many notebooks.");
                        for (var d = 0; d < snapshots.Count; d++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var document = snapshots[d];
                            ValidateDocument(document);
                            if (!documentIds.Add(document.Id)) throw Invalid("The backup contains duplicate notebooks.");
                            var entry = new DocumentEntry { Path = $"notebooks/{d:D6}.json" };
                            for (var p = 0; p < document.Pages.Count; p++)
                            {
                                var page = document.Pages[p];
                                var ink = page.InkData;
                                if (ink.Length > MaxDocumentBytes) throw Invalid("The ink on a single page exceeds the backup size limit.");
                                AddBytes(ref total, ink.Length);
                                var inkEntry = new InkEntry { PageId = page.Id, Path = $"ink/{d:D6}/{p:D6}.isf", Sha256 = Hash(ink) };
                                await WriteAsync(archive, inkEntry.Path, ink, CompressionLevel.Optimal, cancellationToken).ConfigureAwait(false);
                                entry.Inks.Add(inkEntry);
                                page.InkData = [];
                                foreach (var image in page.Images) assetIds.Add(image.AssetId);
                                if (page.Pdf is not null) assetIds.Add(page.Pdf.AssetId);
                            }
                            var json = JsonSerializer.SerializeToUtf8Bytes(document, DocumentJson.Options);
                            if (json.Length > MaxDocumentBytes) throw Invalid("The notebook data exceeds the backup size limit.");
                            AddBytes(ref total, json.Length);
                            entry.Sha256 = Hash(json);
                            await WriteAsync(archive, entry.Path, json, CompressionLevel.Optimal, cancellationToken).ConfigureAwait(false);
                            manifest.Notebooks.Add(entry);
                        }
                        foreach (var id in assetIds.Order(StringComparer.Ordinal))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var asset = await repository.GetAssetAsync(id).ConfigureAwait(false);
                            if (asset.Bytes.Length > MaxPayloadBytes) throw Invalid("An attachment exceeds the 512 MB backup limit.");
                            var hash = Hash(asset.Bytes);
                            if (id != hash) throw Invalid("Attachment integrity check failed.");
                            AddBytes(ref total, asset.Bytes.Length);
                            var entry = new AssetEntry { Id = id, Path = $"assets/{id}.bin", Sha256 = hash, FileName = asset.FileName, ContentType = asset.ContentType };
                            await WriteAsync(archive, entry.Path, asset.Bytes, CompressionLevel.NoCompression, cancellationToken).ConfigureAwait(false);
                            manifest.Assets.Add(entry);
                        }
                        if (1 + manifest.Assets.Count + manifest.Notebooks.Sum(n => 1 + n.Inks.Count) > MaxEntries)
                            throw Invalid("The backup contains too many entries.");
                        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DocumentJson.Options);
                        if (manifestBytes.Length > MaxDocumentBytes) throw Invalid("The backup index is too large.");
                        AddBytes(ref total, manifestBytes.Length);
                        await WriteAsync(archive, "manifest.json", manifestBytes, CompressionLevel.Optimal, cancellationToken).ConfigureAwait(false);
                    }
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }, cancellationToken);
    }

    /// <summary>Returns fresh-ID documents; the caller saves them after successful import.</summary>
    public Task<IReadOnlyList<NotebookDocument>> ImportAsync(string path, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<NotebookDocument>>(async () =>
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (file.Length > MaxArchiveBytes) throw Invalid("The backup file exceeds the 2 GB limit.");
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxEntries) throw Invalid("The backup contains too many entries.");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ValidatePath(entry.FullName);
                if (!entries.TryAdd(entry.FullName, entry)) throw Invalid("The backup contains duplicate paths.");
                if (entry.Length < 0 || entry.Length > MaxPayloadBytes) throw Invalid("A backup entry is too large.");
                AddBytes(ref total, entry.Length);
            }
            var manifest = Deserialize<Manifest>(await ReadAsync(Get(entries, "manifest.json"), MaxDocumentBytes, cancellationToken).ConfigureAwait(false));
            if (manifest.Format != "moye" || manifest.Version is not (1 or 2)) throw Invalid("This backup format or version is not supported.");
            if (manifest.Notebooks is null || manifest.Assets is null || manifest.Notebooks.Count > 10_000) throw Invalid("Invalid backup index.");
            var referencedPaths = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
            var assets = new Dictionary<string, AssetEntry>(StringComparer.Ordinal);
            foreach (var asset in manifest.Assets)
            {
                if (asset is null || !IsHash(asset.Id) || asset.Id != asset.Sha256 || !assets.TryAdd(asset.Id, asset)) throw Invalid("Invalid attachment index.");
                if (asset.Path != $"assets/{asset.Id}.bin" || !referencedPaths.Add(asset.Path)) throw Invalid("Invalid attachment path.");
                if (string.IsNullOrWhiteSpace(asset.FileName) || asset.FileName.Length > 1024 || asset.FileName != System.IO.Path.GetFileName(asset.FileName)
                    || string.IsNullOrWhiteSpace(asset.ContentType) || asset.ContentType.Length > 256) throw Invalid("Invalid attachment information.");
            }
            var documents = new List<NotebookDocument>();
            var originalIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in manifest.Notebooks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (descriptor is null || descriptor.Inks is null || !referencedPaths.Add(descriptor.Path)) throw Invalid("Invalid notebook index.");
                var documentBytes = await ReadVerifiedAsync(entries, descriptor.Path, descriptor.Sha256, MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
                var document = DeserializeDocument(documentBytes, manifest.Version);
                ValidateDocument(document, requireSections: manifest.Version == 2);
                if (manifest.Version == 1) NotebookStructure.Normalize(document);
                if (!originalIds.Add(document.Id) || descriptor.Inks.Count != document.Pages.Count) throw Invalid("Invalid notebook or ink index.");
                var inks = new Dictionary<string, InkEntry>(StringComparer.Ordinal);
                foreach (var ink in descriptor.Inks)
                {
                    if (ink is null || !ValidId(ink.PageId) || !inks.TryAdd(ink.PageId, ink) || !referencedPaths.Add(ink.Path)) throw Invalid("Invalid ink index.");
                }
                foreach (var page in document.Pages)
                {
                    if (page.InkData.Length != 0 || !inks.TryGetValue(page.Id, out var ink)) throw Invalid("The ink index does not match the page.");
                    page.InkData = await ReadVerifiedAsync(entries, ink.Path, ink.Sha256, MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
                    ValidateInk(page.InkData);
                    foreach (var image in page.Images) if (!assets.ContainsKey(image.AssetId)) throw Invalid("An image is missing from the backup.");
                    if (page.Pdf is not null && !assets.ContainsKey(page.Pdf.AssetId)) throw Invalid("An original PDF is missing from the backup.");
                }
                documents.Add(document);
            }
            if (referencedPaths.Count != entries.Count || entries.Keys.Any(p => !referencedPaths.Contains(p))) throw Invalid("The backup contains entries that are not listed in its index.");
            // Verify every asset before changing the repository. Never extract archive paths to disk.
            foreach (var asset in assets.Values)
                await VerifyStreamAsync(Get(entries, asset.Path), asset.Sha256, cancellationToken).ConfigureAwait(false);

            foreach (var asset in assets.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await ReadAsync(Get(entries, asset.Path), MaxPayloadBytes, cancellationToken).ConfigureAwait(false);
                var stored = await repository.PutAssetAsync(asset.FileName, asset.ContentType, bytes).ConfigureAwait(false);
                if (stored.Id != asset.Id) throw Invalid("Imported attachment integrity check failed.");
            }
            foreach (var document in documents)
            {
                document.Id = Guid.NewGuid().ToString("N");
                document.Title += " (restored copy)";
                document.CreatedUtc = document.ModifiedUtc = DateTimeOffset.UtcNow;
                var sectionIds = document.Sections.ToDictionary(section => section.Id, _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
                foreach (var section in document.Sections) section.Id = sectionIds[section.Id];
                foreach (var page in document.Pages)
                {
                    page.Id = Guid.NewGuid().ToString("N");
                    page.SectionId = sectionIds[page.SectionId];
                    foreach (var text in page.Texts) text.Id = Guid.NewGuid().ToString("N");
                    foreach (var image in page.Images) image.Id = Guid.NewGuid().ToString("N");
                }
            }
            return documents;
        }
        catch (JsonException exception) { throw new InvalidDataException("The backup contains invalid JSON.", exception); }
    }, cancellationToken);

    private static async Task WriteAsync(ZipArchive archive, string path, byte[] bytes, CompressionLevel compression, CancellationToken token)
    {
        var entry = archive.CreateEntry(path, compression);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAsync(ZipArchiveEntry entry, int limit, CancellationToken token)
    {
        if (entry.Length > limit || entry.Length < 0) throw Invalid("A backup entry exceeds the size limit.");
        var data = new byte[(int)entry.Length];
        await using var stream = entry.Open();
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, token).ConfigureAwait(false) != 0) throw Invalid("The size of a backup entry does not match its index.");
        return data;
    }

    private static async Task<byte[]> ReadVerifiedAsync(Dictionary<string, ZipArchiveEntry> entries, string path, string expected, int limit, CancellationToken token)
    {
        if (!IsHash(expected)) throw Invalid("The backup is missing a valid integrity checksum.");
        var bytes = await ReadAsync(Get(entries, path), limit, token).ConfigureAwait(false);
        if (Hash(bytes) != expected) throw Invalid("Backup integrity check failed.");
        return bytes;
    }

    private static async Task VerifyStreamAsync(ZipArchiveEntry entry, string expected, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = entry.Open();
        var buffer = new byte[65536];
        long actual = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            actual += read;
            if (actual > entry.Length || actual > MaxPayloadBytes) throw Invalid("The extracted attachment size does not match the expected size.");
            hash.AppendData(buffer, 0, read);
        }
        if (actual != entry.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != expected) throw Invalid("Attachment integrity check failed.");
    }

    private static void ValidateDocument(NotebookDocument document, bool requireSections = true)
    {
        if (!ValidId(document.Id) || document.Title is null || document.Title.Length > 10_000 || document.Folder is null || document.Folder.Length > 10_000
            || document.Pages is null || document.Pages.Count > 20_000) throw Invalid("Invalid notebook structure.");
        var sectionOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        if (requireSections)
        {
            if (document.Sections is null || document.Sections.Count is < 1 or > 20_000) throw Invalid("Invalid notebook section structure.");
            foreach (var section in document.Sections)
            {
                if (section is null || !ValidId(section.Id) || !sectionOrder.TryAdd(section.Id, sectionOrder.Count)
                    || string.IsNullOrWhiteSpace(section.Title) || section.Title.Length > 10_000)
                    throw Invalid("Invalid notebook section structure.");
            }
        }
        else if (document.Sections is not null && document.Sections.Count > 20_000) throw Invalid("Invalid notebook section structure.");
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var previousSection = -1;
        foreach (var page in document.Pages)
        {
            if (page is null || !ValidId(page.Id) || !pageIds.Add(page.Id) || !Dimension(page.Width) || !Dimension(page.Height)
                || !Enum.IsDefined(page.Template) || page.InkData is null || page.Texts is null || page.Images is null
                || page.Texts.Count > 50_000 || page.Images.Count > 50_000) throw Invalid("Invalid page structure.");
            if (requireSections)
            {
                if (!ValidId(page.SectionId) || !sectionOrder.TryGetValue(page.SectionId, out var currentSection) || currentSection < previousSection)
                    throw Invalid("A page has invalid section membership or order.");
                previousSection = currentSection;
            }
            var objectIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in page.Texts)
            {
                if (text is null || !ValidId(text.Id) || !objectIds.Add(text.Id) || !Position(text.X) || !Position(text.Y)
                    || !Dimension(text.Width) || !Dimension(text.Height) || !double.IsFinite(text.FontSize) || text.FontSize <= 0 || text.FontSize > 1000
                    || !Enum.IsDefined(text.Alignment)
                    || text.Text is null || text.Text.Length > 1_000_000 || string.IsNullOrWhiteSpace(text.FontFamily) || text.FontFamily.Length > 1024
                    || string.IsNullOrWhiteSpace(text.Color) || text.Color.Length > 64) throw Invalid("Invalid text box structure.");
            }
            foreach (var image in page.Images)
            {
                if (image is null || !ValidId(image.Id) || !objectIds.Add(image.Id) || !IsHash(image.AssetId) || !Position(image.X) || !Position(image.Y)
                    || !Dimension(image.Width) || !Dimension(image.Height)) throw Invalid("Invalid image structure.");
            }
            if (page.Pdf is { } pdf && (!IsHash(pdf.AssetId) || pdf.PageIndex < 0 || pdf.PageIndex > 1_000_000
                || pdf.Rotation is not (0 or 90 or 180 or 270) || !Position(pdf.CropX) || !Position(pdf.CropY)
                || !double.IsFinite(pdf.CropWidth) || pdf.CropWidth < 0 || pdf.CropWidth > 100_000
                || !double.IsFinite(pdf.CropHeight) || pdf.CropHeight < 0 || pdf.CropHeight > 100_000)) throw Invalid("Invalid PDF page structure.");
        }
    }

    private static void ValidateInk(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var strokes = new StrokeCollection(stream);
            if (strokes.Count > 200_000 || strokes.Sum(stroke => (long)stroke.StylusPoints.Count) > 2_000_000)
                throw Invalid("The ink on a single page exceeds the supported size.");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or EndOfStreamException or OverflowException)
        {
            throw new InvalidDataException("The backup contains invalid ISF ink data.", exception);
        }
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static bool Dimension(double value) => double.IsFinite(value) && value > 0 && value <= 100_000;
    private static bool Position(double value) => double.IsFinite(value) && Math.Abs(value) <= 10_000_000;
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static InvalidDataException Invalid(string message) => new(message);
    private static T Deserialize<T>(byte[] bytes) where T : class => JsonSerializer.Deserialize<T>(bytes, DocumentJson.Options) ?? throw Invalid("Invalid backup structure.");
    private static ZipArchiveEntry Get(Dictionary<string, ZipArchiveEntry> entries, string path) => entries.TryGetValue(path ?? "", out var entry) ? entry : throw Invalid("A required entry is missing from the backup.");

    private static NotebookDocument DeserializeDocument(byte[] bytes, int version)
    {
        // Editable model defaults are useful for new pages, but must not silently repair missing backup fields.
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        RequireFields(root, "id", "title", "folder", "createdUtc", "modifiedUtc", "pages");
        if (version >= 2)
        {
            RequireFields(root, "sections");
            foreach (var section in RequireArray(root, "sections")) RequireFields(section, "id", "title");
        }
        foreach (var page in RequireArray(root, "pages"))
        {
            RequireFields(page, "id", "width", "height", "template", "inkData", "texts", "images");
            if (version >= 2) RequireFields(page, "sectionId");
            foreach (var text in RequireArray(page, "texts"))
                RequireFields(text, "id", "x", "y", "width", "height", "text", "fontFamily", "fontSize", "color");
            foreach (var image in RequireArray(page, "images"))
                RequireFields(image, "id", "assetId", "x", "y", "width", "height");
            if (page.TryGetProperty("pdf", out var pdf) && pdf.ValueKind != JsonValueKind.Null)
                RequireFields(pdf, "assetId", "pageIndex", "rotation", "cropX", "cropY", "cropWidth", "cropHeight");
        }
        return Deserialize<NotebookDocument>(bytes);
    }

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("Invalid object structure in the backup.");
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!present.Add(property.Name)) throw Invalid("The backup JSON contains duplicate fields.");
        if (fields.Any(field => !present.Contains(field))) throw Invalid("A required field is missing from the backup.");
    }

    private static JsonElement.ArrayEnumerator RequireArray(JsonElement element, string field)
    {
        var array = element.GetProperty(field);
        if (array.ValueKind != JsonValueKind.Array) throw Invalid("Invalid list structure in the backup.");
        return array.EnumerateArray();
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':')
            || path.Split('/').Any(part => part is "" or "." or "..") || path.Any(char.IsControl)) throw Invalid("The backup contains an unsafe path.");
    }

    private static void AddBytes(ref long total, long count)
    {
        total = checked(total + count);
        if (total > MaxArchiveBytes) throw Invalid("The extracted backup exceeds the 2 GB limit.");
    }

    private static NotebookDocument Clone(NotebookDocument document)
    {
        var snapshot = document.Snapshot();
        NotebookStructure.Normalize(snapshot);
        foreach (var page in snapshot.Pages) page.InkData = page.InkData.ToArray();
        return snapshot;
    }
}
