using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Ink;
using System.Windows.Input;
using Moye.Models;
using Moye.Services;

namespace Moye.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task RoundTripRestoresNewIdsAndExactAssetsWithoutOverwritingOriginal()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var asset = await repository.PutAssetAsync("香港.png", "image/png", [17, 8, 99, 0, 250]);
        var pdf = await repository.PutAssetAsync("原稿.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-test"));
        var document = new NotebookDocument
        {
            Title = "繁體中文 🖋", Folder = "資料夾一",
            Pages = [new NotePage
            {
                InkData = CreateInk(19), Texts = [new NoteText { Text = "第一行\n第二行", X = 28.5 }],
                Images = [new NoteImage { AssetId = asset.Id }],
                Pdf = new PdfPageSource { AssetId = pdf.Id, PageIndex = 2, Rotation = 90, CropX = 18, CropY = 32, CropWidth = 600, CropHeight = 720 }
            }]
        };
        await repository.SaveAsync(document);
        var service = new BackupService(repository);
        var path = Path.Combine(directory.Root, "中文備份.moye");
        await service.ExportAsync(path, [document]);
        var restored = Assert.Single(await service.ImportAsync(path));
        Assert.NotEqual(document.Id, restored.Id);
        Assert.NotEqual(document.Pages[0].Id, restored.Pages[0].Id);
        Assert.NotEqual(document.Pages[0].Texts[0].Id, restored.Pages[0].Texts[0].Id);
        Assert.NotEqual(document.Pages[0].Images[0].Id, restored.Pages[0].Images[0].Id);
        Assert.Equal(document.Title + " (restored copy)", restored.Title);
        Assert.Equal(document.Folder, restored.Folder);
        Assert.Equal(document.Pages[0].InkData, restored.Pages[0].InkData);
        Assert.Equal(document.Pages[0].Texts[0].Text, restored.Pages[0].Texts[0].Text);
        Assert.Equal(document.Pages[0].Pdf, restored.Pages[0].Pdf);
        Assert.Equal(asset.Id, restored.Pages[0].Images[0].AssetId);
        Assert.Equal(asset.Bytes, (await repository.GetAssetAsync(asset.Id)).Bytes);
        Assert.Single(await repository.ListAsync()); // Import leaves document commit to the caller.
        await repository.SaveAsync(restored);
        Assert.Equal(2, (await repository.ListAsync()).Count);
        Assert.Equal(document.Title, (await repository.LoadAsync(document.Id))!.Title);
        Assert.Equal(CreateInk(19), document.Pages[0].InkData); // Export did not mutate source.
    }

    [Fact]
    public async Task WholeLibraryRestoreImportsIntoCleanRepositoryAndKeepsPageOrder()
    {
        using var sourceDirectory = new StorageTestDirectory();
        using var destinationDirectory = new StorageTestDirectory();
        using var source = new SqliteNotebookRepository(sourceDirectory.DatabasePath);
        using var destination = new SqliteNotebookRepository(destinationDirectory.DatabasePath);
        var asset = await source.PutAssetAsync("共有.png", "image/png", [4, 1, 7]);
        var documents = Enumerable.Range(0, 3).Select(i => new NotebookDocument
        {
            Title = $"筆記 {i}",
            Pages = [new NotePage { Template = PaperTemplate.Ruled, InkData = CreateInk(i + 10), Images = [new NoteImage { AssetId = asset.Id }] }, new NotePage { Template = PaperTemplate.Grid }]
        }).ToList();
        var path = Path.Combine(sourceDirectory.Root, "全部.moye");
        await new BackupService(source).ExportAsync(path, documents);
        var restored = await new BackupService(destination).ImportAsync(path);
        Assert.Equal(3, restored.Count);
        foreach (var document in restored)
        {
            Assert.Equal(new[] { PaperTemplate.Ruled, PaperTemplate.Grid }, document.Pages.Select(p => p.Template));
            Assert.Equal(asset.Id, document.Pages[0].Images[0].AssetId);
        }
        Assert.Equal(asset.Bytes, (await destination.GetAssetAsync(asset.Id)).Bytes);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("ink\\..\\outside.txt")]
    [InlineData("ink//entry")]
    public async Task UnsafeArchivePathsAreRejected(string unsafePath)
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var path = Path.Combine(directory.Root, "unsafe.moye");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) archive.CreateEntry(unsafePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupService(repository).ImportAsync(path));
        Assert.Empty(await repository.ListAsync());
    }

    [Theory]
    [InlineData("future-version")]
    [InlineData("duplicate-entry")]
    [InlineData("unindexed-entry")]
    [InlineData("changed-ink")]
    [InlineData("missing-ink")]
    [InlineData("changed-asset")]
    [InlineData("null-pages")]
    [InlineData("missing-pages")]
    [InlineData("missing-version")]
    [InlineData("invalid-isf")]
    public async Task InvalidArchivesAreRejectedBeforeImportWrites(string corruption)
    {
        using var sourceDirectory = new StorageTestDirectory();
        using var destinationDirectory = new StorageTestDirectory();
        using var source = new SqliteNotebookRepository(sourceDirectory.DatabasePath);
        using var destination = new SqliteNotebookRepository(destinationDirectory.DatabasePath);
        var asset = await source.PutAssetAsync("圖片.png", "image/png", [1, 3, 7]);
        var document = new NotebookDocument { Pages = [new NotePage { InkData = CreateInk(17), Images = [new NoteImage { AssetId = asset.Id }] }] };
        var path = Path.Combine(sourceDirectory.Root, "damaged.moye");
        await new BackupService(source).ExportAsync(path, [document]);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            if (corruption is "future-version" or "missing-version")
            {
                var manifest = ReadJson(archive.GetEntry("manifest.json")!);
                if (corruption == "future-version") manifest["version"] = 99;
                else manifest.AsObject().Remove("version");
                Replace(archive, "manifest.json", manifest.ToJsonString());
            }
            else if (corruption == "duplicate-entry") archive.CreateEntry("manifest.json");
            else if (corruption == "unindexed-entry") archive.CreateEntry("extra.txt");
            else if (corruption == "changed-ink") Replace(archive, "ink/000000/000000.isf", "changed");
            else if (corruption == "missing-ink") archive.GetEntry("ink/000000/000000.isf")!.Delete();
            else if (corruption == "changed-asset") Replace(archive, $"assets/{asset.Id}.bin", "changed");
            else if (corruption == "invalid-isf")
            {
                const string invalid = "invalid ISF";
                Replace(archive, "ink/000000/000000.isf", invalid);
                var manifest = ReadJson(archive.GetEntry("manifest.json")!);
                manifest["notebooks"]![0]!["inks"]![0]!["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(invalid)));
                Replace(archive, "manifest.json", manifest.ToJsonString());
            }
            else if (corruption is "null-pages" or "missing-pages")
            {
                var doc = ReadJson(archive.GetEntry("notebooks/000000.json")!);
                if (corruption == "null-pages") doc["pages"] = null;
                else doc.AsObject().Remove("pages");
                var bytes = Encoding.UTF8.GetBytes(doc.ToJsonString());
                Replace(archive, "notebooks/000000.json", doc.ToJsonString());
                var manifest = ReadJson(archive.GetEntry("manifest.json")!);
                manifest["notebooks"]![0]!["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                Replace(archive, "manifest.json", manifest.ToJsonString());
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupService(destination).ImportAsync(path));
        Assert.Empty(await destination.ListAsync());
        await Assert.ThrowsAsync<FileNotFoundException>(() => destination.GetAssetAsync(asset.Id));
    }

    [Fact]
    public async Task FailedExportKeepsExistingBackupAndRemovesTemporaryFile()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var path = Path.Combine(directory.Root, "existing.moye");
        var original = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(path, original);
        var document = new NotebookDocument { Pages = [new NotePage { Images = [new NoteImage { AssetId = new string('a', 64) }] }] };
        await Assert.ThrowsAsync<FileNotFoundException>(() => new BackupService(repository).ExportAsync(path, [document]));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    private static JsonNode ReadJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonNode.Parse(stream)!;
    }

    private static byte[] CreateInk(int offset)
    {
        var strokes = new StrokeCollection { new Stroke(new StylusPointCollection { new StylusPoint(offset, 10, .25f), new StylusPoint(offset + 12, 24, .8f) }) };
        using var stream = new MemoryStream();
        strokes.Save(stream);
        return stream.ToArray();
    }

    private static void Replace(ZipArchive archive, string path, string text)
    {
        archive.GetEntry(path)!.Delete();
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
