using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Moye.Models;
using Moye.Services;

namespace Moye.Tests;

public sealed class NotebookStructureBackupTests
{
    [Fact]
    public async Task V2RestoreKeepsSectionOrderEmptySectionsAndMixedContentWithFreshMembershipIds()
    {
        using var sourceDirectory = new StorageTestDirectory();
        using var destinationDirectory = new StorageTestDirectory();
        using var source = new SqliteNotebookRepository(sourceDirectory.DatabasePath);
        using var destination = new SqliteNotebookRepository(destinationDirectory.DatabasePath);
        var image = await source.PutAssetAsync("圖片.png", "image/png", [8, 3, 5]);
        var pdf = await source.PutAssetAsync("原稿.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-original"));
        var first = new NoteSection { Title = "課堂筆記" };
        var second = new NoteSection { Title = "練習" };
        var empty = new NoteSection { Title = "Empty section" };
        var a = new NotePage
        {
            SectionId = first.Id,
            Texts = [new NoteText { Text = "甲頁\n繁體中文", Bold = true, Italic = true, Alignment = NoteTextAlignment.Right }],
            Images = [new NoteImage { AssetId = image.Id }]
        };
        var b = new NotePage { SectionId = second.Id, Texts = [new NoteText { Text = "乙頁" }], Pdf = new PdfPageSource { AssetId = pdf.Id } };
        var document = new NotebookDocument { Sections = [first, empty, second], Pages = [b, a] };
        await source.SaveAsync(document);
        var path = Path.Combine(sourceDirectory.Root, "hierarchy.moye");
        await new BackupService(source).ExportAsync(path, [document]);
        using (var archive = ZipFile.OpenRead(path))
            Assert.Equal(2, ReadJson(archive.GetEntry("manifest.json")!)["version"]!.GetValue<int>());
        var restored = Assert.Single(await new BackupService(destination).ImportAsync(path));
        Assert.Equal(new[] { first.Title, empty.Title, second.Title }, restored.Sections.Select(section => section.Title));
        Assert.All(restored.Sections, section => Assert.DoesNotContain(section.Id, document.Sections.Select(original => original.Id)));
        Assert.Equal(new[] { a.Texts[0].Text, b.Texts[0].Text }, restored.Pages.Select(page => page.Texts[0].Text));
        Assert.Equal(restored.Sections[0].Id, restored.Pages[0].SectionId);
        Assert.Equal(restored.Sections[2].Id, restored.Pages[1].SectionId);
        Assert.DoesNotContain(restored.Pages, page => page.SectionId == restored.Sections[1].Id);
        Assert.NotEqual(a.Id, restored.Pages[0].Id);
        Assert.NotEqual(a.Texts[0].Id, restored.Pages[0].Texts[0].Id);
        Assert.Equal(a.Texts[0], restored.Pages[0].Texts[0] with { Id = a.Texts[0].Id });
        Assert.Equal(image.Id, restored.Pages[0].Images[0].AssetId);
        Assert.Equal(b.Pdf, restored.Pages[1].Pdf);
        Assert.Equal(pdf.Bytes, (await destination.GetAssetAsync(pdf.Id)).Bytes);
        await destination.SaveAsync(restored);
        var reopened = (await destination.LoadAsync(restored.Id))!;
        Assert.Equal(restored.Sections, reopened.Sections);
        Assert.Equal(restored.Pages.Select(page => page.SectionId), reopened.Pages.Select(page => page.SectionId));
        Assert.Equal(new[] { b, a }, document.Pages); // Export normalized a snapshot, not the editable caller.
        Assert.Equal(first.Id, (await source.LoadAsync(document.Id))!.Sections[0].Id);
    }

    [Fact]
    public async Task V1BackupWithoutHierarchyRestoresEveryLegacyPageIntoOneNewGeneralSection()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var original = new NotebookDocument { Pages = [new NotePage { Texts = [new NoteText { Text = "First" }] }, new NotePage { Texts = [new NoteText { Text = "第二頁" }] }] };
        var path = Path.Combine(directory.Root, "v1.moye");
        var backup = new BackupService(repository);
        await backup.ExportAsync(path, [original]);
        Rewrite(path, document =>
        {
            document.AsObject().Remove("sections");
            foreach (var page in document["pages"]!.AsArray()) page!.AsObject().Remove("sectionId");
        }, version: 1);
        var firstRestore = Assert.Single(await backup.ImportAsync(path));
        var secondRestore = Assert.Single(await backup.ImportAsync(path));
        var section = Assert.Single(firstRestore.Sections);
        Assert.Equal("General", section.Title);
        Assert.NotEqual(section.Id, secondRestore.Sections[0].Id);
        Assert.Equal(new[] { "First", "第二頁" }, firstRestore.Pages.Select(page => page.Texts[0].Text));
        Assert.All(firstRestore.Pages, page => Assert.Equal(section.Id, page.SectionId));
        Assert.All(firstRestore.Pages, page => Assert.DoesNotContain(page.Id, original.Pages.Select(originalPage => originalPage.Id)));
    }

    [Theory]
    [InlineData("missing-sections")]
    [InlineData("null-sections")]
    [InlineData("empty-sections")]
    [InlineData("null-section")]
    [InlineData("duplicate-section-id")]
    [InlineData("blank-section-id")]
    [InlineData("missing-section-title")]
    [InlineData("oversized-section-title")]
    [InlineData("missing-page-section")]
    [InlineData("orphan-page")]
    [InlineData("interleaved-order")]
    public async Task InvalidV2HierarchyIsRejectedBeforeAnyAssetOrDocumentWrite(string corruption)
    {
        using var sourceDirectory = new StorageTestDirectory();
        using var destinationDirectory = new StorageTestDirectory();
        using var source = new SqliteNotebookRepository(sourceDirectory.DatabasePath);
        using var destination = new SqliteNotebookRepository(destinationDirectory.DatabasePath);
        var asset = await source.PutAssetAsync("sample.png", "image/png", [8, 4, 9]);
        var a = new NoteSection { Title = "First" };
        var b = new NoteSection { Title = "Second" };
        var original = new NotebookDocument
        {
            Sections = [a, b],
            Pages = [new NotePage { SectionId = a.Id, Images = [new NoteImage { AssetId = asset.Id }] }, new NotePage { SectionId = b.Id }]
        };
        var path = Path.Combine(sourceDirectory.Root, "invalid-v2.moye");
        await new BackupService(source).ExportAsync(path, [original]);
        Rewrite(path, document =>
        {
            switch (corruption)
            {
                case "missing-sections": document.AsObject().Remove("sections"); break;
                case "null-sections": document["sections"] = null; break;
                case "empty-sections": document["sections"]!.AsArray().Clear(); break;
                case "null-section": document["sections"]![0] = null; break;
                case "duplicate-section-id": document["sections"]![1]!["id"] = a.Id; break;
                case "blank-section-id": document["sections"]![0]!["id"] = ""; break;
                case "missing-section-title": document["sections"]![0]!.AsObject().Remove("title"); break;
                case "oversized-section-title": document["sections"]![0]!["title"] = new string('x', 10_001); break;
                case "missing-page-section": document["pages"]![0]!.AsObject().Remove("sectionId"); break;
                case "orphan-page": document["pages"]![0]!["sectionId"] = "not-a-section"; break;
                case "interleaved-order":
                    var pages = document["pages"]!.AsArray();
                    var first = pages[0]!.DeepClone();
                    pages[0] = pages[1]!.DeepClone();
                    pages[1] = first;
                    break;
            }
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupService(destination).ImportAsync(path));
        Assert.Empty(await destination.ListAsync());
        await Assert.ThrowsAsync<FileNotFoundException>(() => destination.GetAssetAsync(asset.Id));
    }

    private static void Rewrite(string path, Action<JsonNode> edit, int? version = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var document = ReadJson(archive.GetEntry("notebooks/000000.json")!);
        edit(document);
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        Replace(archive, "notebooks/000000.json", bytes);
        var manifest = ReadJson(archive.GetEntry("manifest.json")!);
        if (version.HasValue) manifest["version"] = version.Value;
        manifest["notebooks"]![0]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Replace(archive, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }

    private static JsonNode ReadJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonNode.Parse(stream)!;
    }

    private static void Replace(ZipArchive archive, string path, byte[] bytes)
    {
        archive.GetEntry(path)!.Delete();
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(bytes);
    }
}
