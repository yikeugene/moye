using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Moye.Models;
using Moye.Services;

namespace Moye.Tests;

public sealed class NotebookStructureStorageTests
{
    [Fact]
    public void LegacyNormalizationIsIdempotentAndSnapshotsKeepIndependentSections()
    {
        var pages = new[] { new NotePage { Texts = [new NoteText { Text = "第一頁" }] }, new NotePage(), new NotePage() };
        var document = new NotebookDocument { Pages = pages.ToList() };
        NotebookStructure.Normalize(document);
        var general = Assert.Single(document.Sections);
        Assert.Equal("General", general.Title);
        Assert.True(Guid.TryParseExact(general.Id, "N", out _));
        Assert.Equal(pages, document.Pages);
        Assert.All(document.Pages, page => Assert.Equal(general.Id, page.SectionId));
        NotebookStructure.Normalize(document);
        Assert.Same(general, Assert.Single(document.Sections));
        var snapshot = document.Snapshot();
        snapshot.Sections[0].Title = "Changed copy";
        snapshot.Pages[0].SectionId = "different";
        Assert.Equal("General", general.Title);
        Assert.Equal(general.Id, pages[0].SectionId);
        Assert.Equal("第一頁", snapshot.Pages[0].Texts[0].Text);
    }

    [Fact]
    public void NormalizeGroupsSectionsStablyAndRecoversOrphansWithoutLosingPagesOrEmptySections()
    {
        var first = new NoteSection { Title = "Course 一" };
        var second = new NoteSection { Title = "Course 二" };
        var empty = new NoteSection { Title = "Empty" };
        var a1 = new NotePage { SectionId = first.Id };
        var a2 = new NotePage { SectionId = first.Id };
        var b1 = new NotePage { SectionId = second.Id };
        var b2 = new NotePage { SectionId = second.Id };
        var orphan = new NotePage { SectionId = "missing" };
        var legacy = new NotePage();
        var document = new NotebookDocument { Sections = [first, second, empty], Pages = [b1, a1, orphan, b2, a2, legacy] };
        NotebookStructure.Normalize(document);
        Assert.Equal(new[] { a1, a2, b1, b2, orphan, legacy }, document.Pages);
        Assert.Equal(new[] { first, second, empty }, document.Sections.Take(3));
        var general = document.Sections[3];
        Assert.Equal("General", general.Title);
        Assert.Equal(general.Id, orphan.SectionId);
        Assert.Equal(general.Id, legacy.SectionId);
        NotebookStructure.Normalize(document);
        Assert.Equal(4, document.Sections.Count);
        Assert.Equal(6, document.Pages.Select(page => page.Id).Distinct().Count());
    }

    [Fact]
    public void DuplicateSectionRecoveryRetainsTitlesAndExistingPageMembership()
    {
        var section = new NoteSection { Title = "Retain both" };
        var document = new NotebookDocument { Sections = [section, section], Pages = [new NotePage { SectionId = section.Id }] };
        NotebookStructure.Normalize(document);
        Assert.Equal(2, document.Sections.Count);
        Assert.All(document.Sections, item => Assert.Equal("Retain both", item.Title));
        Assert.Equal(2, document.Sections.Select(item => item.Id).Distinct().Count());
        Assert.Equal(section.Id, Assert.Single(document.Pages).SectionId);
    }

    [Fact]
    public async Task V1MigrationPersistsGeneralAndPreservesMetadataAssetsInkAndEmptyNotebooks()
    {
        using var directory = new StorageTestDirectory();
        var original = LegacyPageJson();
        CreateV1Database(directory.DatabasePath, original);
        using (var repository = new SqliteNotebookRepository(directory.DatabasePath))
        {
            var loaded = (await repository.LoadAsync("legacy"))!;
            var section = Assert.Single(loaded.Sections);
            Assert.Equal("General", section.Title);
            Assert.Equal(new[] { "page-b", "page-a" }, loaded.Pages.Select(page => page.Id));
            Assert.All(loaded.Pages, page => Assert.Equal(section.Id, page.SectionId));
            Assert.Equal(new byte[] { 1, 3, 7, 9 }, loaded.Pages[0].InkData);
            Assert.Equal("舊筆記中文字", loaded.Pages[0].Texts[0].Text);
            Assert.True(loaded.Pages[0].Texts[0].Bold);
            Assert.Equal(new byte[] { 8, 5, 2 }, (await repository.GetAssetAsync("asset")).Bytes);
            var empty = (await repository.LoadAsync("empty"))!;
            Assert.Single(empty.Sections);
            Assert.Empty(empty.Pages);
        }
        using var connection = Open(directory.DatabasePath);
        Assert.Equal(2L, Scalar(connection, "PRAGMA user_version"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM sections"));
        var metadata = JsonNode.Parse((string)Scalar(connection, "SELECT metadata_json FROM pages WHERE id='page-b'")!)!;
        Assert.Equal("preserve me", metadata["extension"]!["value"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(metadata["sectionId"]!.GetValue<string>()));
        using var reopened = new SqliteNotebookRepository(directory.DatabasePath);
        var again = (await reopened.LoadAsync("legacy"))!;
        Assert.Equal(metadata["sectionId"]!.GetValue<string>(), again.Sections[0].Id);
    }

    [Fact]
    public async Task FailedMigrationRollsBackVersionSectionSchemaAndAllPageData()
    {
        using var directory = new StorageTestDirectory();
        CreateV1Database(directory.DatabasePath, "{invalid-json");
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        await Assert.ThrowsAsync<SqliteException>(() => repository.InitializeAsync());
        using var connection = Open(directory.DatabasePath);
        Assert.Equal(1L, Scalar(connection, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='sections'"));
        Assert.Equal("{invalid-json", Scalar(connection, "SELECT metadata_json FROM pages WHERE id='page-b'"));
        Assert.Equal(new byte[] { 1, 3, 7, 9 }, (byte[])Scalar(connection, "SELECT ink FROM pages WHERE id='page-b'")!);
        Assert.Equal(new byte[] { 8, 5, 2 }, (byte[])Scalar(connection, "SELECT data FROM assets WHERE id='asset'")!);
    }

    [Fact]
    public async Task SectionReorderRenamePageMoveAndEmptySectionsSurviveReopenAndDeleteTogether()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var a = new NoteSection { Title = "A" };
        var b = new NoteSection { Title = "B" };
        var empty = new NoteSection { Title = "Empty" };
        var first = new NotePage { SectionId = a.Id };
        var second = new NotePage { SectionId = b.Id };
        var third = new NotePage { SectionId = b.Id };
        var document = new NotebookDocument { Sections = [a, b, empty], Pages = [first, second, third] };
        await repository.SaveAsync(document);
        b.Title = "重新命名";
        document.Sections = [empty, b];
        first.SectionId = b.Id;
        document.Pages = [third, first, second];
        await repository.SaveAsync(document);
        var loaded = (await repository.LoadAsync(document.Id))!;
        Assert.Equal(new[] { empty.Id, b.Id }, loaded.Sections.Select(section => section.Id));
        Assert.Equal("重新命名", loaded.Sections[1].Title);
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, loaded.Pages.Select(page => page.Id));
        Assert.All(loaded.Pages, page => Assert.Equal(b.Id, page.SectionId));
        await repository.DeleteAsync(document.Id);
        using var connection = Open(directory.DatabasePath);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sections"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM pages"));
    }

    [Fact]
    public async Task FailedSaveRollsBackSectionsAndPagesInTheSameTransaction()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var original = new NoteSection { Title = "Committed" };
        var document = new NotebookDocument { Sections = [original], Pages = [new NotePage { SectionId = original.Id }] };
        await repository.SaveAsync(document);
        document.Sections = [new NoteSection { Title = "Not committed" }];
        document.Pages[0].SectionId = document.Sections[0].Id;
        document.Pages[0].Width = double.NaN;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.SaveAsync(document));
        var recovered = (await repository.LoadAsync(document.Id))!;
        Assert.Equal(original, Assert.Single(recovered.Sections));
        Assert.Equal(original.Id, recovered.Pages[0].SectionId);
        Assert.True(double.IsFinite(recovered.Pages[0].Width));
    }

    [Fact]
    public async Task SavingLegacySnapshotsDoesNotMutateTheCallerOrGenerateNewSectionIdsEveryTime()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var document = new NotebookDocument { Pages = [new NotePage()] };
        await repository.SaveAsync(document);
        var first = (await repository.LoadAsync(document.Id))!;
        await repository.SaveAsync(document);
        var second = (await repository.LoadAsync(document.Id))!;
        Assert.Empty(document.Sections);
        Assert.Equal("", document.Pages[0].SectionId);
        Assert.Equal(first.Sections, second.Sections);
        Assert.Equal(first.Pages[0].SectionId, second.Pages[0].SectionId);
    }

    private static string LegacyPageJson()
    {
        var node = JsonSerializer.SerializeToNode(new NotePage
        {
            Id = "page-b", Texts = [new NoteText { Text = "舊筆記中文字", Bold = true }]
        }, DocumentJson.Options)!.AsObject();
        node.Remove("sectionId");
        node["extension"] = new JsonObject { ["value"] = "preserve me" };
        return node.ToJsonString();
    }

    private static void CreateV1Database(string path, string metadata)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE notebooks(id TEXT PRIMARY KEY,title TEXT NOT NULL,folder TEXT NOT NULL,created_utc TEXT NOT NULL,modified_utc TEXT NOT NULL);
            CREATE TABLE pages(notebook_id TEXT NOT NULL REFERENCES notebooks(id) ON DELETE CASCADE,id TEXT NOT NULL,ordinal INTEGER NOT NULL,metadata_json TEXT NOT NULL,ink BLOB NOT NULL,content_hash TEXT NOT NULL,PRIMARY KEY(notebook_id,id));
            CREATE TABLE assets(id TEXT PRIMARY KEY,file_name TEXT NOT NULL,content_type TEXT NOT NULL,data BLOB NOT NULL);
            INSERT INTO notebooks VALUES('legacy','舊筆記','Course','2026-01-02T00:00:00.0000000+00:00','2026-02-03T00:00:00.0000000+00:00');
            INSERT INTO notebooks VALUES('empty','Empty','Course','2026-01-02T00:00:00.0000000+00:00','2026-02-03T00:00:00.0000000+00:00');
            INSERT INTO pages VALUES('legacy','page-b',0,$metadata,X'01030709','old-hash');
            INSERT INTO pages VALUES('legacy','page-a',1,'{"id":"page-a","width":794,"height":1123,"template":0,"inkData":"","texts":[],"images":[]}',X'','old-hash');
            INSERT INTO assets VALUES('asset','legacy.bin','application/octet-stream',X'080502');
            PRAGMA user_version=1;
            """;
        command.Parameters.AddWithValue("$metadata", metadata);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
