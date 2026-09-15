using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Moye.Models;
using Moye.Services;

namespace Moye.Tests;

/// <summary>Exercises real ZIP/JSON/ISF backup handling independently of the SQLite provider.</summary>
public sealed class SectionBackupMemoryTests
{
    [Fact]
    public async Task V2ArchivePreservesOrderedSectionsEmptySectionsAndEditableMixedContent()
    {
        using var directory = new StorageTestDirectory();
        using var source = new MemoryRepository();
        using var destination = new MemoryRepository();
        var image = await source.PutAssetAsync("圖像.png", "image/png", [1, 7, 3, 9]);
        var pdf = await source.PutAssetAsync("original.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-preserved-original"));
        var first = new NoteSection { Title = "課堂筆記" };
        var empty = new NoteSection { Title = "Future topic" };
        var second = new NoteSection { Title = "Seminars" };
        var ink = new StrokeCollection
        {
            new Stroke(new StylusPointCollection { new StylusPoint(20, 30, .2f), new StylusPoint(65, 90, .8f) },
                new DrawingAttributes { Width = 4, Height = 4, IgnorePressure = false, FitToCurve = false, Color = Colors.DarkBlue })
        };
        using var inkStream = new MemoryStream();
        ink.Save(inkStream);
        var a = new NotePage
        {
            SectionId = first.Id, Template = PaperTemplate.Cornell, InkData = inkStream.ToArray(),
            Texts = [new NoteText { Text = "大學筆記\n• 第一段", FontFamily = "Microsoft JhengHei", FontSize = 28, Bold = true, Italic = true, Alignment = NoteTextAlignment.Center, X = 93, Y = 110 }],
            Images = [new NoteImage { AssetId = image.Id, X = 45, Y = 210, Width = 230, Height = 160 }]
        };
        var b = new NotePage
        {
            SectionId = second.Id, Width = 1000, Height = 600,
            Pdf = new PdfPageSource { AssetId = pdf.Id, PageIndex = 3, Rotation = 90, CropX = 8, CropY = 12, CropWidth = 500, CropHeight = 300 }
        };
        var original = new NotebookDocument { Title = "Semester notes", Sections = [first, empty, second], Pages = [b, a] };
        await destination.SaveAsync(new NotebookDocument { Id = original.Id, Title = "Keep existing copy", Pages = [new()] });
        destination.ResetWriteCounts();
        var path = Path.Combine(directory.Root, "sections.moye");

        await new BackupService(source).ExportAsync(path, [original]);
        using (var archive = ZipFile.OpenRead(path))
            Assert.Equal(2, ReadJson(archive.GetEntry("manifest.json")!)["version"]!.GetValue<int>());
        var restored = Assert.Single(await new BackupService(destination).ImportAsync(path));

        Assert.NotEqual(original.Id, restored.Id);
        Assert.Equal("Semester notes (restored copy)", restored.Title);
        Assert.Equal(new[] { first.Title, empty.Title, second.Title }, restored.Sections.Select(section => section.Title));
        Assert.All(restored.Sections, section => Assert.DoesNotContain(section.Id, original.Sections.Select(old => old.Id)));
        Assert.Equal(restored.Sections[0].Id, restored.Pages[0].SectionId);
        Assert.Equal(restored.Sections[2].Id, restored.Pages[1].SectionId);
        Assert.DoesNotContain(restored.Pages, page => page.SectionId == restored.Sections[1].Id);
        Assert.NotEqual(a.Id, restored.Pages[0].Id);
        Assert.NotEqual(a.Texts[0].Id, restored.Pages[0].Texts[0].Id);
        Assert.NotEqual(a.Images[0].Id, restored.Pages[0].Images[0].Id);
        Assert.Equal(a.Texts[0], restored.Pages[0].Texts[0] with { Id = a.Texts[0].Id });
        Assert.Equal(a.Images[0], restored.Pages[0].Images[0] with { Id = a.Images[0].Id });
        Assert.Equal(PaperTemplate.Cornell, restored.Pages[0].Template);
        Assert.Equal(a.InkData, restored.Pages[0].InkData);
        var restoredInk = new StrokeCollection(new MemoryStream(restored.Pages[0].InkData, false));
        Assert.InRange(restoredInk[0].StylusPoints[0].PressureFactor, .19f, .21f);
        Assert.InRange(restoredInk[0].StylusPoints[1].PressureFactor, .79f, .81f);
        Assert.False(restoredInk[0].DrawingAttributes.IgnorePressure);
        Assert.Equal(Colors.DarkBlue, restoredInk[0].DrawingAttributes.Color);
        Assert.Equal(b.Pdf, restored.Pages[1].Pdf);
        Assert.Equal(1000, restored.Pages[1].Width);
        Assert.Equal(600, restored.Pages[1].Height);
        var restoredImage = await destination.GetAssetAsync(image.Id);
        var restoredPdf = await destination.GetAssetAsync(pdf.Id);
        Assert.Equal(image.FileName, restoredImage.FileName);
        Assert.Equal(image.ContentType, restoredImage.ContentType);
        Assert.Equal(image.Bytes, restoredImage.Bytes);
        Assert.Equal(pdf.FileName, restoredPdf.FileName);
        Assert.Equal(pdf.ContentType, restoredPdf.ContentType);
        Assert.Equal(pdf.Bytes, restoredPdf.Bytes);
        Assert.Equal(2, destination.AssetWrites);
        Assert.Equal(0, destination.DocumentWrites);
        Assert.Equal("Keep existing copy", (await destination.LoadAsync(original.Id))!.Title);
        Assert.Equal(new[] { b.Id, a.Id }, original.Pages.Select(page => page.Id));
        Assert.Equal(first.Id, a.SectionId);
        Assert.Equal(a.InkData, inkStream.ToArray());
        await destination.SaveAsync(restored);
        var reopened = (await destination.LoadAsync(restored.Id))!;
        Assert.Equal(restored.Sections, reopened.Sections);
        Assert.Equal(restored.Pages.Select(page => page.SectionId), reopened.Pages.Select(page => page.SectionId));
        Assert.Equal(2, (await destination.ListAsync()).Count);
    }

    [Fact]
    public async Task V1ArchiveMigratesEveryPageToANewGeneralSectionOnEachRestore()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new MemoryRepository();
        var original = new NotebookDocument
        {
            Pages = [new NotePage { Template = PaperTemplate.Grid, Texts = [new NoteText { Text = "舊筆記第一頁" }] }, new NotePage { Texts = [new NoteText { Text = "Legacy second page" }] }]
        };
        var path = Path.Combine(directory.Root, "legacy.moye");
        var backup = new BackupService(repository);
        await backup.ExportAsync(path, [original]);
        RewriteDocument(path, 0, document =>
        {
            document.AsObject().Remove("sections");
            foreach (var page in document["pages"]!.AsArray()) page!.AsObject().Remove("sectionId");
        }, version: 1);

        var first = Assert.Single(await backup.ImportAsync(path));
        var second = Assert.Single(await backup.ImportAsync(path));

        Assert.Equal("General", Assert.Single(first.Sections).Title);
        Assert.Equal("General", Assert.Single(second.Sections).Title);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Sections[0].Id, second.Sections[0].Id);
        Assert.Equal(new[] { "舊筆記第一頁", "Legacy second page" }, first.Pages.Select(page => page.Texts[0].Text));
        Assert.All(first.Pages, page => Assert.Equal(first.Sections[0].Id, page.SectionId));
        Assert.All(first.Pages, page => Assert.DoesNotContain(page.Id, original.Pages.Select(old => old.Id)));
        Assert.Equal(PaperTemplate.Grid, first.Pages[0].Template);
        Assert.Empty(original.Sections);
        Assert.All(original.Pages, page => Assert.Equal("", page.SectionId));
        Assert.Equal(0, repository.DocumentWrites);
        Assert.Equal(0, repository.AssetWrites);
    }

    [Theory]
    [InlineData("duplicate-section-id")]
    [InlineData("blank-section-id")]
    [InlineData("orphan-page")]
    [InlineData("missing-page-section")]
    [InlineData("noncanonical-order")]
    public async Task InvalidSecondNotebookRejectsWholeV2ArchiveBeforeAnyRepositoryWrite(string corruption)
    {
        using var directory = new StorageTestDirectory();
        using var source = new MemoryRepository();
        using var destination = new MemoryRepository();
        var image = await source.PutAssetAsync("first-notebook.png", "image/png", [5, 2, 8]);
        var first = new NotebookDocument { Pages = [new NotePage { Images = [new NoteImage { AssetId = image.Id }] }] };
        var a = new NoteSection { Title = "A" };
        var b = new NoteSection { Title = "B" };
        var second = new NotebookDocument { Sections = [a, b], Pages = [new NotePage { SectionId = a.Id }, new NotePage { SectionId = b.Id }] };
        var path = Path.Combine(directory.Root, "invalid-later-notebook.moye");
        await new BackupService(source).ExportAsync(path, [first, second]);
        RewriteDocument(path, 1, document =>
        {
            switch (corruption)
            {
                case "duplicate-section-id": document["sections"]![1]!["id"] = a.Id; break;
                case "blank-section-id": document["sections"]![0]!["id"] = ""; break;
                case "orphan-page": document["pages"]![0]!["sectionId"] = "missing-section"; break;
                case "missing-page-section": document["pages"]![0]!.AsObject().Remove("sectionId"); break;
                case "noncanonical-order":
                    var pages = document["pages"]!.AsArray();
                    var firstPage = pages[0]!.DeepClone();
                    pages[0] = pages[1]!.DeepClone(); pages[1] = firstPage;
                    break;
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupService(destination).ImportAsync(path));

        Assert.Equal(0, destination.AssetWrites);
        Assert.Equal(0, destination.DocumentWrites);
        Assert.Empty(await destination.ListAsync());
        await Assert.ThrowsAsync<FileNotFoundException>(() => destination.GetAssetAsync(image.Id));
    }

    private static void RewriteDocument(string path, int index, Action<JsonNode> edit, int? version = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var name = $"notebooks/{index:D6}.json";
        var document = ReadJson(archive.GetEntry(name)!);
        edit(document);
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        ReplaceEntry(archive, name, bytes);
        var manifest = ReadJson(archive.GetEntry("manifest.json")!);
        if (version is not null) manifest["version"] = version.Value;
        manifest["notebooks"]![index]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        ReplaceEntry(archive, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }
    private static JsonNode ReadJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonNode.Parse(stream)!;
    }
    private static void ReplaceEntry(ZipArchive archive, string name, byte[] bytes)
    {
        archive.GetEntry(name)!.Delete();
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    private sealed class MemoryRepository : INotebookRepository
    {
        private readonly ConcurrentDictionary<string, NotebookDocument> _documents = new();
        private readonly ConcurrentDictionary<string, AssetData> _assets = new();
        public int AssetWrites { get; private set; }
        public int DocumentWrites { get; private set; }
        public void ResetWriteCounts() { AssetWrites = 0; DocumentWrites = 0; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => Task.FromResult<IReadOnlyList<NotebookSummary>>(_documents.Values.Select(document => new NotebookSummary { Id = document.Id, Title = document.Title, Folder = document.Folder, PageCount = document.Pages.Count }).ToArray());
        public Task<NotebookDocument?> LoadAsync(string id) => Task.FromResult(_documents.GetValueOrDefault(id)?.Snapshot());
        public Task SaveAsync(NotebookDocument document) { DocumentWrites++; _documents[document.Id] = document.Snapshot(); return Task.CompletedTask; }
        public Task DeleteAsync(string id) { _documents.TryRemove(id, out _); return Task.CompletedTask; }
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes)
        {
            AssetWrites++;
            var asset = new AssetData(Convert.ToHexStringLower(SHA256.HashData(bytes)), fileName, contentType, bytes.ToArray());
            _assets[asset.Id] = asset;
            return Task.FromResult(asset);
        }
        public Task<AssetData> GetAssetAsync(string id) => _assets.TryGetValue(id, out var asset) ? Task.FromResult(asset) : Task.FromException<AssetData>(new FileNotFoundException("Fixture asset not found."));
        public void Dispose() { }
    }
}
