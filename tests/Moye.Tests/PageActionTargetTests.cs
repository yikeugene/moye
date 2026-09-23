using Moye.Models;
using Moye.Services;
using Moye.ViewModels;

namespace Moye.Tests;

public sealed class PageActionTargetTests
{
    [Fact]
    public async Task MenuRetainsClickedPageWhenAnotherPageBecomesSelected()
    {
        using var viewModel = await OpenNotebook();
        var clicked = viewModel.Pages[1];
        var target = Target(viewModel, clicked);
        viewModel.SelectedPage = viewModel.Pages[0];

        Assert.Same(clicked, MainWindow.ResolvePageActionTarget(viewModel, target));
        Assert.Same(viewModel.Pages[0], viewModel.SelectedPage);
    }

    [Fact]
    public async Task RemovedTargetCannotFallBackToNextPageAtSameIndex()
    {
        using var viewModel = await OpenNotebook();
        viewModel.SelectedPage = viewModel.Pages[1];
        var target = Target(viewModel, viewModel.SelectedPage);
        viewModel.DeletePage();

        Assert.Equal(2, viewModel.Pages.Count);
        Assert.Null(MainWindow.ResolvePageActionTarget(viewModel, target));
    }

    [Fact]
    public async Task ReorderingKeepsTargetIdentityInsteadOfPageNumber()
    {
        using var viewModel = await OpenNotebook();
        var target = Target(viewModel, viewModel.Pages[1]);
        viewModel.SelectedPage = viewModel.Pages[1];
        viewModel.MovePage(-1);
        viewModel.SelectedPage = viewModel.Pages[2];

        var resolved = MainWindow.ResolvePageActionTarget(viewModel, target);
        Assert.NotNull(resolved);
        Assert.Equal(target.PageId, resolved.Page.Id);
        Assert.Equal(1, resolved.Number);
        Assert.Same(viewModel.Pages[2], viewModel.SelectedPage);
    }

    [Fact]
    public async Task MenuFromBeforeUndoCannotModifyReplacedDocument()
    {
        using var viewModel = await OpenNotebook();
        var target = Target(viewModel, viewModel.Pages[0]);
        viewModel.DuplicatePage();
        viewModel.Undo();

        Assert.Equal(target.Document.Id, viewModel.Document!.Id);
        Assert.Contains(viewModel.Pages, page => page.Page.Id == target.PageId);
        Assert.Null(MainWindow.ResolvePageActionTarget(viewModel, target));
    }

    [Fact]
    public async Task SectionNavigationAndMovingTargetRejectOldMenu()
    {
        using var viewModel = await OpenNotebook();
        var firstSection = viewModel.SelectedSection!;
        var target = Target(viewModel, viewModel.Pages[0]);
        viewModel.AddSection("Exercises");
        var secondSection = viewModel.SelectedSection!;
        Assert.Null(MainWindow.ResolvePageActionTarget(viewModel, target));

        viewModel.SelectedSection = firstSection;
        viewModel.SelectedPage = MainWindow.ResolvePageActionTarget(viewModel, target);
        Assert.NotNull(viewModel.SelectedPage);
        viewModel.MovePageToSection(secondSection.Id);
        Assert.Null(MainWindow.ResolvePageActionTarget(viewModel, target));
    }

    [Fact]
    public async Task BusyLibraryAndAnotherNotebookRejectMenu()
    {
        using var viewModel = await OpenNotebook();
        var target = Target(viewModel, viewModel.Pages[0]);
        viewModel.IsBusy = true;
        Assert.Null(MainWindow.ResolvePageActionTarget(viewModel, target));
        viewModel.IsBusy = false;
        Assert.NotNull(MainWindow.ResolvePageActionTarget(viewModel, target));

        await viewModel.ReturnToLibraryAsync();
        Assert.Null(MainWindow.ResolvePageActionTarget(viewModel, target));
        await viewModel.CreateAsync("Another synthetic notebook", "Tests");
        Assert.Null(MainWindow.ResolvePageActionTarget(viewModel, target));
    }

    private static MainWindow.PageActionTarget Target(MainViewModel model, PageViewModel page)
        => new(model.Document!, page.Page.SectionId, page.Page.Id);

    private static async Task<MainViewModel> OpenNotebook()
    {
        var model = new MainViewModel(new MemoryRepository());
        await model.CreateAsync("Synthetic page action notebook", "Tests");
        model.AddPage(PaperTemplate.Grid);
        model.AddPage(PaperTemplate.Cornell);
        return model;
    }

    private sealed class MemoryRepository : INotebookRepository
    {
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => Task.FromResult<IReadOnlyList<NotebookSummary>>([]);
        public Task<NotebookDocument?> LoadAsync(string id) => Task.FromResult<NotebookDocument?>(null);
        public Task SaveAsync(NotebookDocument document) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes) => throw new NotSupportedException();
        public Task<AssetData> GetAssetAsync(string id) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
