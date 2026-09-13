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

        Assert.True(viewModel.HasNotebooks);
        Assert.False(viewModel.HasVisibleNotebooks);
        Assert.Equal("0 of 1 notebooks", viewModel.LibraryCountText);
        Assert.Equal("No notebooks found", viewModel.EmptyLibraryTitle);
        Assert.Contains(nameof(MainViewModel.HasVisibleNotebooks), notifications);
        Assert.Contains(nameof(MainViewModel.LibraryCountText), notifications);
        Assert.Contains(nameof(MainViewModel.EmptyLibraryDescription), notifications);
        viewModel.Search = "work";
        Assert.True(viewModel.HasVisibleNotebooks);
        Assert.Single(viewModel.Notebooks);
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

    private sealed class FailingSaveRepository(INotebookRepository inner) : INotebookRepository
    {
        public bool FailSave { get; set; }
        public Task InitializeAsync() => inner.InitializeAsync();
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => inner.ListAsync();
        public Task<NotebookDocument?> LoadAsync(string id) => inner.LoadAsync(id);
        public Task SaveAsync(NotebookDocument document) => FailSave ? Task.FromException(new IOException("Simulated disk write failure")) : inner.SaveAsync(document);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes) => inner.PutAssetAsync(fileName, contentType, bytes);
        public Task<AssetData> GetAssetAsync(string id) => inner.GetAssetAsync(id);
        public void Dispose() => inner.Dispose();
    }
}
