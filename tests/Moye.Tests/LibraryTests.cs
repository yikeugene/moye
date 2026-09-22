using Moye.Models;
using Moye.Services;
using Moye.ViewModels;

namespace Moye.Tests;

public sealed class LibraryTests
{
    public static IEnumerable<object[]> Templates => Enum.GetValues<PaperTemplate>().Select(template => new object[] { template });

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task StartupShowsLibraryWithoutCreatingOpeningOrChangingNotebooks(int notebookCount)
    {
        using var directory = new StorageTestDirectory();
        var documents = Enumerable.Range(0, notebookCount).Select(i => new NotebookDocument
        {
            Title = $"Notebook {i}", Folder = "Saved notes",
            ModifiedUtc = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            Pages = [new() { Texts = [new() { Text = "Existing content stays intact" }] }]
        }).ToList();
        using (var original = new SqliteNotebookRepository(directory.DatabasePath))
        {
            await original.InitializeAsync();
            foreach (var document in documents) await original.SaveAsync(document);
        }
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsLibraryVisible);
        Assert.False(viewModel.IsEditorVisible);
        Assert.Null(viewModel.Document);
        Assert.Empty(viewModel.Pages);
        Assert.Null(viewModel.SelectedPage);
        Assert.Equal(notebookCount > 0, viewModel.HasNotebooks);
        Assert.Equal(notebookCount, viewModel.Notebooks.Count);
        Assert.False(viewModel.Autosave.IsDirty);
        Assert.Equal(notebookCount, (await viewModel.Repository.ListAsync()).Count);
        foreach (var document in documents)
        {
            var saved = await viewModel.Repository.LoadAsync(document.Id);
            Assert.NotNull(saved);
            Assert.Equal(document.ModifiedUtc, saved.ModifiedUtc);
            Assert.Equal("Existing content stays intact", saved.Pages[0].Texts[0].Text);
        }
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task ExplicitCreationOpensNotebookWithChosenPaperAndPersistsIt(PaperTemplate template)
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.InitializeAsync();

        await viewModel.CreateAsync("  Research notes  ", "  Work  ", template);

        Assert.False(viewModel.IsLibraryVisible);
        Assert.True(viewModel.IsEditorVisible);
        Assert.True(viewModel.HasNotebooks);
        Assert.True(viewModel.HasVisibleNotebooks);
        Assert.Equal("1 notebook", viewModel.LibraryCountText);
        Assert.Equal("Research notes", viewModel.Title);
        Assert.Equal("Work", viewModel.Folder);
        Assert.Equal(template, Assert.Single(viewModel.Pages).Page.Template);
        Assert.Same(viewModel.Pages[0], viewModel.SelectedPage);
        var saved = await viewModel.Repository.LoadAsync(viewModel.Document!.Id);
        Assert.Equal(template, Assert.Single(saved!.Pages).Template);
    }

    [Fact]
    public async Task DefaultNewNotebookStartsWithRuledPaper()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));

        await viewModel.CreateAsync("New notebook", "My Notes");

        Assert.Equal(PaperTemplate.Ruled, Assert.Single(viewModel.Pages).Page.Template);
    }

    [Fact]
    public async Task ReturningHomeCommitsPendingContentAndRetainsDocumentForRecovery()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("First title", "My Notes");
        var document = viewModel.Document!;
        document.Pages[0].Texts.Add(new() { Text = "A note written just before returning home" });
        viewModel.Rename("Updated title", "Project");

        await viewModel.ReturnToLibraryAsync();

        Assert.True(viewModel.IsLibraryVisible);
        Assert.False(viewModel.IsEditorVisible);
        Assert.Same(document, viewModel.Document);
        Assert.False(viewModel.Autosave.IsDirty);
        Assert.Equal("Updated title", Assert.Single(viewModel.Notebooks).Title);
        using var reopened = new SqliteNotebookRepository(directory.DatabasePath);
        var saved = await reopened.LoadAsync(document.Id);
        Assert.Equal("Updated title", saved!.Title);
        Assert.Equal("A note written just before returning home", Assert.Single(saved.Pages[0].Texts).Text);
    }

    [Fact]
    public async Task FailedReturnHomeKeepsEditorAndPendingEditsUntilSuccessfulRetry()
    {
        using var directory = new StorageTestDirectory();
        var repository = new FailingSaveRepository(new SqliteNotebookRepository(directory.DatabasePath));
        using var viewModel = new MainViewModel(repository);
        await viewModel.CreateAsync("Committed title", "My Notes");
        var document = viewModel.Document!;
        repository.FailSave = true;
        viewModel.Rename("Unsaved title", "My Notes");

        await Assert.ThrowsAsync<IOException>(() => viewModel.ReturnToLibraryAsync());

        Assert.False(viewModel.IsLibraryVisible);
        Assert.True(viewModel.IsEditorVisible);
        Assert.Same(document, viewModel.Document);
        Assert.Equal("Unsaved title", viewModel.Title);
        Assert.True(viewModel.HasSaveError);
        Assert.True(viewModel.Autosave.IsDirty);
        Assert.Equal("Unsaved title", Assert.Single(viewModel.Autosave.PendingDocuments).Title);
        Assert.Equal("Committed title", (await repository.LoadAsync(document.Id))!.Title);

        repository.FailSave = false;
        await viewModel.ReturnToLibraryAsync();

        Assert.True(viewModel.IsLibraryVisible);
        Assert.False(viewModel.HasSaveError);
        Assert.False(viewModel.Autosave.IsDirty);
        Assert.Equal("Unsaved title", (await repository.LoadAsync(document.Id))!.Title);
    }

    [Fact]
    public async Task ReopeningFromLibraryStartsAtFirstPageWithFreshHistory()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("Notebook", "My Notes");
        viewModel.AddPage(PaperTemplate.Grid);
        Assert.Equal(2, viewModel.SelectedPage!.Number);
        Assert.True(viewModel.CanUndo);
        var id = viewModel.Document!.Id;
        await viewModel.ReturnToLibraryAsync();

        await viewModel.OpenAsync(id);

        Assert.True(viewModel.IsEditorVisible);
        Assert.False(viewModel.IsLibraryVisible);
        Assert.Equal(2, viewModel.Pages.Count);
        Assert.Equal(1, viewModel.SelectedPage!.Number);
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.CanRedo);
    }

    [Fact]
    public async Task SearchDistinguishesNoMatchesFromAnEmptyLibraryAndNotifiesBindings()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("Research", "Work");
        await viewModel.ReturnToLibraryAsync();
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        viewModel.Search = "not a matching title";

        Assert.True(viewModel.HasSearch);
        Assert.True(viewModel.HasNotebooks);
        Assert.False(viewModel.HasVisibleNotebooks);
        Assert.Equal("0 of 1 notebooks", viewModel.LibraryCountText);
        Assert.Equal("No notebooks found", viewModel.EmptyLibraryTitle);
        Assert.Equal("Try another notebook name or category, or clear your search.", viewModel.EmptyLibraryDescription);
        Assert.Contains(nameof(MainViewModel.HasSearch), notifications);
        Assert.Contains(nameof(MainViewModel.HasVisibleNotebooks), notifications);
        Assert.Contains(nameof(MainViewModel.LibraryCountText), notifications);
        Assert.Contains(nameof(MainViewModel.EmptyLibraryDescription), notifications);
        viewModel.Search = "work";
        Assert.True(viewModel.HasSearch);
        Assert.True(viewModel.HasVisibleNotebooks);
        Assert.Single(viewModel.Notebooks);

        notifications.Clear();
        viewModel.Search = "";
        Assert.False(viewModel.HasSearch);
        Assert.True(viewModel.HasVisibleNotebooks);
        Assert.Equal("1 notebook", viewModel.LibraryCountText);
        Assert.Contains(nameof(MainViewModel.HasSearch), notifications);
    }

    [Fact]
    public async Task WhitespaceSearchCanBeClearedWithoutHidingNotebooks()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("Research", "Work");
        await viewModel.ReturnToLibraryAsync();

        viewModel.Search = "   ";

        Assert.True(viewModel.HasSearch);
        Assert.True(viewModel.HasVisibleNotebooks);
        Assert.Equal("Research", Assert.Single(viewModel.Notebooks).Title);
        Assert.Equal("1 notebook", viewModel.LibraryCountText);

        viewModel.Search = "";

        Assert.False(viewModel.HasSearch);
        Assert.Equal("Research", Assert.Single(viewModel.Notebooks).Title);
    }

    [Theory]
    [InlineData("missing notebook")]
    [InlineData("   ")]
    public async Task ClearingSearchInAnEmptyLibraryRestoresFirstNotebookGuidance(string search)
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.InitializeAsync();
        Assert.False(viewModel.HasSearch);
        Assert.Equal("Your next idea starts here", viewModel.EmptyLibraryTitle);

        viewModel.Search = search;

        Assert.True(viewModel.HasSearch);
        Assert.False(viewModel.HasNotebooks);
        Assert.False(viewModel.HasVisibleNotebooks);
        Assert.Equal("No notebooks found", viewModel.EmptyLibraryTitle);
        Assert.Equal("Try another notebook name or category, or clear your search.", viewModel.EmptyLibraryDescription);

        viewModel.Search = "";

        Assert.False(viewModel.HasSearch);
        Assert.Equal("Your next idea starts here", viewModel.EmptyLibraryTitle);
        Assert.Equal("Create a notebook and choose the paper that works for you.", viewModel.EmptyLibraryDescription);
        Assert.Empty(await viewModel.Repository.ListAsync());
    }

    [Fact]
    public async Task FailedOpenLeavesLibraryVisibleAndExistingDocumentIntact()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("Existing notebook", "My Notes");
        var document = viewModel.Document;
        await viewModel.ReturnToLibraryAsync();

        await Assert.ThrowsAsync<IOException>(() => viewModel.OpenAsync("missing-notebook"));

        Assert.True(viewModel.IsLibraryVisible);
        Assert.False(viewModel.IsEditorVisible);
        Assert.Same(document, viewModel.Document);
        Assert.Single(viewModel.Notebooks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingCurrentNotebookClearsEditorHistoryAndCannotBeResurrected(bool fromHome)
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("Delete me", "My Notes");
        var id = viewModel.Document!.Id;
        viewModel.AddPage();
        viewModel.Rename("Delete latest version", "My Notes");
        viewModel.Undo(); // Both undo and redo contain notebook snapshots.
        if (fromHome) await viewModel.ReturnToLibraryAsync();

        await viewModel.DeleteNotebookAsync(id);

        Assert.True(viewModel.IsLibraryVisible);
        Assert.Null(viewModel.Document);
        Assert.Empty(viewModel.Pages);
        Assert.Null(viewModel.SelectedPage);
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.CanRedo);
        Assert.False(viewModel.HasNotebooks);
        Assert.False(viewModel.HasVisibleNotebooks);
        Assert.Equal("Your next idea starts here", viewModel.EmptyLibraryTitle);
        viewModel.Undo(); viewModel.Redo(); viewModel.Changed();
        await viewModel.Autosave.RetryAsync();
        await viewModel.ReturnToLibraryAsync();
        Assert.False(viewModel.Autosave.IsDirty);
        using var reopened = new SqliteNotebookRepository(directory.DatabasePath);
        Assert.Null(await reopened.LoadAsync(id));
        Assert.Empty(await reopened.ListAsync());
        await viewModel.CreateAsync("Next notebook", "My Notes");
        Assert.True(viewModel.IsEditorVisible);
        Assert.Single(viewModel.Notebooks);
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public async Task DeletingFilteredNotebookPreservesOtherNotebookAndUpdatesCounts()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("Delete me", "Archive");
        var id = viewModel.Document!.Id;
        await viewModel.CreateAsync("Keep me", "Work");
        var retained = viewModel.Document;
        viewModel.Rename("Keep my latest edits", "Work");
        viewModel.Search = "Archive";

        await viewModel.DeleteNotebookAsync(id);

        Assert.Same(retained, viewModel.Document);
        Assert.True(viewModel.IsEditorVisible);
        Assert.True(viewModel.CanUndo);
        Assert.True(viewModel.HasNotebooks);
        Assert.False(viewModel.HasVisibleNotebooks);
        Assert.Equal("0 of 1 notebooks", viewModel.LibraryCountText);
        Assert.Equal("No notebooks found", viewModel.EmptyLibraryTitle);
        Assert.Equal("Keep my latest edits", (await viewModel.Repository.LoadAsync(retained!.Id))!.Title);
        viewModel.Search = "";
        Assert.Equal(retained.Id, Assert.Single(viewModel.Notebooks).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedSaveOrDeletePreservesNotebookAndAllowsRetry(bool failSave)
    {
        using var directory = new StorageTestDirectory();
        var repository = new FailingSaveRepository(new SqliteNotebookRepository(directory.DatabasePath));
        using var viewModel = new MainViewModel(repository);
        await viewModel.CreateAsync("Keep until success", "My Notes");
        var document = viewModel.Document!;
        repository.FailSave = failSave; repository.FailDelete = !failSave;
        viewModel.Rename("Latest content", "My Notes");

        await Assert.ThrowsAsync<IOException>(() => viewModel.DeleteNotebookAsync(document.Id));

        Assert.Same(document, viewModel.Document);
        Assert.Single(viewModel.Notebooks);
        Assert.True(viewModel.IsEditorVisible);
        Assert.True(viewModel.CanUndo);
        Assert.NotNull(await repository.LoadAsync(document.Id));
        if (failSave)
        {
            Assert.True(viewModel.HasSaveError);
            Assert.Equal("Latest content", Assert.Single(viewModel.Autosave.PendingDocuments).Title);
        }
        repository.FailSave = false; repository.FailDelete = false;
        await viewModel.DeleteNotebookAsync(document.Id);
        Assert.Null(await repository.LoadAsync(document.Id));
        Assert.False(viewModel.HasNotebooks);
    }

    [Fact]
    public async Task DeletionWaitsForInFlightSaveBeforeRemovingNotebook()
    {
        using var directory = new StorageTestDirectory();
        var repository = new FailingSaveRepository(new SqliteNotebookRepository(directory.DatabasePath));
        using var viewModel = new MainViewModel(repository);
        await viewModel.CreateAsync("Save in flight", "My Notes");
        var id = viewModel.Document!.Id;
        repository.BlockNextSave = true;
        viewModel.Rename("New title", "My Notes");
        var flush = viewModel.Autosave.FlushAsync();
        await repository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deletion = viewModel.DeleteNotebookAsync(id);
        try { Assert.False(deletion.IsCompleted); }
        finally { repository.AllowSave.TrySetResult(); }
        await Task.WhenAll(flush, deletion).WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.Autosave.RetryAsync();
        Assert.Null(await repository.LoadAsync(id));
        Assert.Empty(viewModel.Autosave.PendingDocuments);
    }

    private sealed class FailingSaveRepository(INotebookRepository inner) : INotebookRepository
    {
        public bool FailSave { get; set; }
        public bool FailDelete { get; set; }
        public bool BlockNextSave { get; set; }
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync() => inner.InitializeAsync();
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => inner.ListAsync();
        public Task<NotebookDocument?> LoadAsync(string id) => inner.LoadAsync(id);
        public async Task SaveAsync(NotebookDocument document)
        {
            if (BlockNextSave)
            {
                BlockNextSave = false; SaveStarted.TrySetResult();
                await AllowSave.Task;
            }
            if (FailSave) throw new IOException("Simulated disk write failure");
            await inner.SaveAsync(document);
        }
        public Task DeleteAsync(string id) => FailDelete ? Task.FromException(new IOException("Simulated delete failure")) : inner.DeleteAsync(id);
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes) => inner.PutAssetAsync(fileName, contentType, bytes);
        public Task<AssetData> GetAssetAsync(string id) => inner.GetAssetAsync(id);
        public void Dispose() => inner.Dispose();
    }
}
