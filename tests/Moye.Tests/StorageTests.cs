using System.IO;
using System.Diagnostics;
using System.Windows.Ink;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Moye.Models;
using Moye.Services;
using Xunit.Abstractions;

namespace Moye.Tests;

public sealed class StorageTests(ITestOutputHelper output)
{
    [Fact]
    public void NativeSqliteEngineIncludesSecurityFixes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        output.WriteLine($"Native SQLite engine: {version}");
        Assert.True(version >= new Version(3, 50, 2), $"SQLite {version} predates the CVE-2025-6965 fix.");
    }

    [Fact]
    public async Task FutureDatabaseVersionIsRejectedWithoutChangingVersionOrData()
    {
        using var directory = new StorageTestDirectory();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=3; CREATE TABLE future_data(value TEXT); INSERT INTO future_data VALUES('keep');";
            command.ExecuteNonQuery();
        }
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.InitializeAsync());
        using var verification = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.DatabasePath, Pooling = false }.ToString());
        verification.Open();
        using var query = verification.CreateCommand();
        query.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, query.ExecuteScalar());
        query.CommandText = "SELECT value FROM future_data";
        Assert.Equal("keep", query.ExecuteScalar());
    }

    [Fact]
    public async Task SaveReopenPreservesChineseMixedContentAndInkPressure()
    {
        using var directory = new StorageTestDirectory();
        var ink = new StrokeCollection { new Stroke(new StylusPointCollection { new StylusPoint(12, 19, .2f), new StylusPoint(31, 40, .85f) }) };
        using var stream = new MemoryStream();
        ink.Save(stream);
        var document = new NotebookDocument
        {
            Title = "九月手寫筆記 🖊", Folder = "學習／中文",
            Pages = [new NotePage { Template = PaperTemplate.Grid, InkData = stream.ToArray(), Texts = [new NoteText { Text = "香港繁體中文\n第二行", X = 113.5, FontSize = 26 }] }]
        };
        string assetId;
        using (var repository = new SqliteNotebookRepository(directory.DatabasePath))
        {
            await repository.InitializeAsync();
            var asset = await repository.PutAssetAsync("示範圖片.png", "image/png", [1, 2, 3, 4, 5]);
            assetId = asset.Id;
            document.Pages[0].Images.Add(new NoteImage { AssetId = asset.Id, X = 182, Y = 232 });
            await repository.SaveAsync(document);
        }
        using var reopened = new SqliteNotebookRepository(directory.DatabasePath);
        var saved = await reopened.LoadAsync(document.Id);
        Assert.NotNull(saved);
        Assert.Equal(document.Title, saved.Title);
        Assert.Equal(document.Folder, saved.Folder);
        Assert.Equal(document.Pages[0].Texts[0], saved.Pages[0].Texts[0]);
        Assert.Equal(PaperTemplate.Grid, saved.Pages[0].Template);
        Assert.Equal(assetId, saved.Pages[0].Images[0].AssetId);
        Assert.Equal(document.Pages[0].InkData, saved.Pages[0].InkData);
        var recoveredInk = new StrokeCollection(new MemoryStream(saved.Pages[0].InkData));
        Assert.InRange(recoveredInk[0].StylusPoints[0].PressureFactor, .19f, .21f);
        Assert.InRange(recoveredInk[0].StylusPoints[1].PressureFactor, .84f, .86f);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, (await reopened.GetAssetAsync(assetId)).Bytes);
        var summary = Assert.Single(await reopened.ListAsync());
        Assert.Equal(document.Title, summary.Title);
        Assert.Equal(1, summary.PageCount);
    }

    [Fact]
    public async Task HundredPagesTwentyThousandStrokesPersistAndSinglePageEditRoundTrips()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var strokes = new StrokeCollection();
        for (var line = 0; line < 200; line++)
        {
            var points = new StylusPointCollection();
            for (var point = 0; point < 32; point++)
                points.Add(new StylusPoint(32 + (line % 10) * 68 + point, 48 + (line / 10) * 46 + Math.Sin(point * .5) * 8, .15f + point / 40f));
            strokes.Add(new Stroke(points));
        }
        using var inkStream = new MemoryStream();
        strokes.Save(inkStream);
        var originalInk = inkStream.ToArray();
        var document = new NotebookDocument
        {
            Title = "一百頁手寫壓力測試",
            Pages = Enumerable.Range(0, 100).Select(i => new NotePage
            {
                InkData = originalInk.ToArray(), Template = PaperTemplate.Ruled,
                Texts = [new NoteText { Text = $"第 {i + 1} 頁・200 筆手寫" }]
            }).ToList()
        };
        await repository.InitializeAsync();
        var timer = Stopwatch.StartNew();
        await repository.SaveAsync(document);
        var initialSave = timer.Elapsed;
        strokes.Add(new Stroke(new StylusPointCollection { new StylusPoint(10, 10, .4f), new StylusPoint(80, 80, .9f) }));
        using var changedInk = new MemoryStream();
        strokes.Save(changedInk);
        document.Pages[49].InkData = changedInk.ToArray();
        timer.Restart();
        await repository.SaveAsync(document);
        var editSave = timer.Elapsed;
        timer.Restart();
        var reopened = await repository.LoadAsync(document.Id);
        var load = timer.Elapsed;
        Assert.Equal(100, reopened!.Pages.Count);
        Assert.Equal(201, new StrokeCollection(new MemoryStream(reopened.Pages[49].InkData)).Count);
        Assert.Equal(originalInk, reopened.Pages[0].InkData);
        Assert.Equal(originalInk, reopened.Pages[99].InkData);
        Assert.Equal(document.Pages.Select(p => p.Id), reopened.Pages.Select(p => p.Id));
        output.WriteLine($"100 pages; 20,000 original strokes; {originalInk.Length * 100:N0} ISF bytes. Initial commit: {initialSave.TotalMilliseconds:N0} ms. Single-page edit commit: {editSave.TotalMilliseconds:N0} ms. Full notebook reload: {load.TotalMilliseconds:N0} ms.");
    }

    [Fact]
    public async Task ReorderDeleteAndRenamePersistWithoutOrphanPages()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var document = new NotebookDocument { Pages = [new(), new(), new()] };
        var originalOrder = document.Pages.Select(p => p.Id).ToArray();
        await repository.SaveAsync(document);
        document.Title = "重新命名";
        document.Pages = [document.Pages[2], document.Pages[0]];
        await repository.SaveAsync(document);
        var saved = await repository.LoadAsync(document.Id);
        Assert.Equal("重新命名", saved!.Title);
        Assert.Equal(new[] { originalOrder[2], originalOrder[0] }, saved.Pages.Select(p => p.Id));
        await repository.DeleteAsync(document.Id);
        Assert.Null(await repository.LoadAsync(document.Id));
        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task FailedPageSerializationRollsBackWholeNotebookTransaction()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var document = new NotebookDocument { Title = "已提交", Pages = [new(), new()] };
        await repository.SaveAsync(document);
        document.Title = "不應提交";
        document.Pages[0].Texts.Add(new NoteText { Text = "未完成變更" });
        document.Pages[1].Width = double.NaN;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.SaveAsync(document));
        var recovered = await repository.LoadAsync(document.Id);
        Assert.Equal("已提交", recovered!.Title);
        Assert.Empty(recovered.Pages[0].Texts);
        Assert.True(double.IsFinite(recovered.Pages[1].Width));
    }

    [Fact]
    public async Task SaveCapturesImmutableSnapshotAndAssetsDeduplicateByContent()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var document = new NotebookDocument { Pages = [new NotePage { InkData = [5, 9], Texts = [new NoteText { Text = "原稿" }] }] };
        var save = repository.SaveAsync(document);
        document.Title = "稍後修改";
        document.Pages[0].InkData[0] = 99;
        document.Pages[0].Texts[0].Text = "稍後修改";
        await save;
        var loaded = await repository.LoadAsync(document.Id);
        Assert.Equal("原稿", loaded!.Pages[0].Texts[0].Text);
        Assert.Equal(5, loaded.Pages[0].InkData[0]);
        var asset1 = await repository.PutAssetAsync("甲.png", "image/png", [7, 8]);
        var asset2 = await repository.PutAssetAsync("乙.png", "image/png", [7, 8]);
        Assert.Equal(asset1.Id, asset2.Id);
    }

    [Fact]
    public async Task AutosaveFailureRetainsSnapshotAndRetryClearsErrorOnlyAfterCommit()
    {
        using var repository = new RecordingRepository { Fail = true };
        using var autosave = new AutosaveCoordinator(repository, TimeSpan.FromSeconds(2));
        var document = new NotebookDocument { Title = "未儲存", Pages = [new NotePage { InkData = [11, 22] }] };
        autosave.Schedule(document);
        document.Pages[0].InkData[0] = 99;
        await Assert.ThrowsAsync<IOException>(() => autosave.FlushAsync());
        Assert.True(autosave.IsDirty);
        Assert.NotNull(autosave.LastError);
        Assert.Equal(11, Assert.Single(autosave.PendingDocuments).Pages[0].InkData[0]);
        repository.Fail = false;
        await autosave.RetryAsync();
        Assert.False(autosave.IsDirty);
        Assert.Null(autosave.LastError);
        Assert.Equal(11, Assert.Single(repository.Saved).Pages[0].InkData[0]);
    }

    [Fact]
    public async Task EditDuringSaveRemainsPendingAndFlushCommitsLatestAcrossNotebooks()
    {
        using var repository = new RecordingRepository { BlockFirstSave = true };
        using var autosave = new AutosaveCoordinator(repository, TimeSpan.FromSeconds(2));
        var first = new NotebookDocument { Title = "第一版" };
        autosave.Schedule(first);
        var flush = autosave.FlushAsync();
        await repository.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Title = "第二版";
        autosave.Schedule(first);
        var second = new NotebookDocument { Title = "另一本" };
        autosave.Schedule(second);
        repository.AllowFirstSave.SetResult();
        await flush;
        Assert.False(autosave.IsDirty);
        Assert.Equal("第二版", repository.Saved.Last(d => d.Id == first.Id).Title);
        Assert.Equal("另一本", repository.Saved.Last(d => d.Id == second.Id).Title);
    }

    [Fact]
    public async Task AutosaveContinualEditsStillStartsSavingWithinTwoSecondWindow()
    {
        using var repository = new RecordingRepository();
        using var autosave = new AutosaveCoordinator(repository, TimeSpan.FromMilliseconds(1500));
        var document = new NotebookDocument();
        for (var i = 0; i < 26 && !repository.FirstSaveStarted.Task.IsCompleted; i++)
        {
            document.Title = i.ToString();
            autosave.Schedule(document);
            await Task.Delay(100);
        }
        Assert.True(repository.FirstSaveStarted.Task.IsCompleted, "Continuous edits must not reset the autosave deadline indefinitely.");
        await autosave.FlushAsync();
    }

    internal sealed class RecordingRepository : INotebookRepository
    {
        public bool Fail { get; set; }
        public bool BlockFirstSave { get; set; }
        public List<NotebookDocument> Saved { get; } = [];
        public TaskCompletionSource FirstSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public async Task SaveAsync(NotebookDocument document)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstSaveStarted.TrySetResult();
                if (BlockFirstSave) await AllowFirstSave.Task;
            }
            if (Fail) throw new IOException("模擬磁碟寫入失敗");
            Saved.Add(document.Snapshot());
        }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => Task.FromResult<IReadOnlyList<NotebookSummary>>([]);
        public Task<NotebookDocument?> LoadAsync(string id) => Task.FromResult<NotebookDocument?>(null);
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes) => throw new NotSupportedException();
        public Task<AssetData> GetAssetAsync(string id) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

internal sealed class StorageTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Moye-tests", Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(Root, "notes.db");
    public StorageTestDirectory() => Directory.CreateDirectory(Root);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
