using System.Text.Json;
using Moye.Models;
using Moye.Services;
using Moye.ViewModels;

namespace Moye.Tests;

public sealed class SectionWorkflowTests
{
    [Fact]
    public void LegacyNotebookOpensUnderGeneralWithoutLosingPagesOrCreatingAnEdit()
    {
        using var viewModel = new MainViewModel(new MemoryRepository());
        var pages = new List<NotePage> { Page("A"), Page("B") };
        var document = new NotebookDocument { Title = "Existing notebook", Pages = pages };

        viewModel.ReplaceDocument(document, true);

        var section = Assert.Single(viewModel.Sections);
        Assert.Equal("General", section.Title);
        Assert.Equal("2 pages", section.PageCountText);
        Assert.Equal(pages.Select(page => page.Id), viewModel.Pages.Select(page => page.Page.Id));
        Assert.All(viewModel.Pages, page => Assert.Equal(section.Id, page.Page.SectionId));
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.Autosave.IsDirty);
    }

    [Fact]
    public void SwitchingSectionsKeepsIndependentSelectionAndIsNavigationOnly()
    {
        using var viewModel = Hierarchy();
        var original = JsonSerializer.Serialize(viewModel.Document, DocumentJson.Options);
        var sections = viewModel.Sections.ToArray();
        viewModel.SelectedPage = viewModel.Pages[1];
        var firstSelected = viewModel.SelectedPage.Page.Id;
        var refreshes = 0;
        viewModel.DocumentReplaced += (_, _) => refreshes++;

        viewModel.SelectedSection = sections[1];
        Assert.Equal(new[] { "B1", "B2" }, Labels(viewModel.Pages));
        Assert.Equal(new[] { 1, 2 }, viewModel.Pages.Select(page => page.Number));
        Assert.Equal("Exercises", viewModel.SelectedSectionTitle);
        Assert.Equal("2 pages", viewModel.SectionPageCountText);
        Assert.Equal("6 pages", viewModel.PageCountText);
        viewModel.SelectedPage = viewModel.Pages[1];
        var secondSelected = viewModel.SelectedPage.Page.Id;
        viewModel.SelectedSection = sections[0];
        Assert.Equal(firstSelected, viewModel.SelectedPage!.Page.Id);
        viewModel.SelectedSection = sections[1];
        Assert.Equal(secondSelected, viewModel.SelectedPage!.Page.Id);

        Assert.Equal(3, refreshes);
        Assert.Equal(original, JsonSerializer.Serialize(viewModel.Document, DocumentJson.Options));
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.Autosave.IsDirty);
    }

    [Fact]
    public void AddingPageInSecondSectionInsertsAfterItsActualGlobalPage()
    {
        using var viewModel = Hierarchy();
        SelectSection(viewModel, "Exercises");
        var sectionId = viewModel.SelectedSection!.Id;
        var unchanged = viewModel.Document!.Pages.Where(page => page.SectionId != sectionId).Select(page => page.Id).ToArray();
        var before = viewModel.Pages.Select(page => page.Page.Id).ToArray();

        viewModel.AddPage(PaperTemplate.Cornell);

        Assert.Equal(sectionId, viewModel.SelectedPage!.Page.SectionId);
        Assert.Equal(PaperTemplate.Cornell, viewModel.SelectedPage.Page.Template);
        Assert.Equal(2, viewModel.SelectedPage.Number);
        Assert.Equal(before[0], viewModel.Pages[0].Page.Id);
        Assert.Equal(before[1], viewModel.Pages[2].Page.Id);
        Assert.Equal(unchanged, viewModel.Document.Pages.Where(page => page.SectionId != sectionId).Select(page => page.Id));
        AssertCanonical(viewModel.Document);
        viewModel.Undo();
        Assert.Equal(before, viewModel.Pages.Select(page => page.Page.Id));
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public void DuplicateMoveAndDeleteOnlyAffectTheVisibleSection()
    {
        using var viewModel = Hierarchy();
        SelectSection(viewModel, "Exercises");
        var sectionId = viewModel.SelectedSection!.Id;
        var untouched = viewModel.Document!.Pages.Where(page => page.SectionId != sectionId).Select(page => page.Id).ToArray();
        var source = viewModel.SelectedPage!.Page;

        viewModel.DuplicatePage();
        var duplicate = viewModel.SelectedPage!.Page;
        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.NotEqual(source.Texts[0].Id, duplicate.Texts[0].Id);
        Assert.Equal(source.Texts[0].Text, duplicate.Texts[0].Text);
        Assert.Equal(2, viewModel.SelectedPage.Number);
        viewModel.MovePage(1);
        Assert.Equal(duplicate.Id, viewModel.Pages[2].Page.Id);
        Assert.Equal(new[] { "B1", "B2", "B1" }, Labels(viewModel.Pages));
        viewModel.MovePage(-2);
        Assert.Equal(duplicate.Id, viewModel.Pages[0].Page.Id);
        viewModel.DeletePage();

        Assert.Equal(new[] { "B1", "B2" }, Labels(viewModel.Pages));
        Assert.Equal(untouched, viewModel.Document.Pages.Where(page => page.SectionId != sectionId).Select(page => page.Id));
        AssertCanonical(viewModel.Document);
    }

    [Fact]
    public void PageMovesCannotCrossSectionBoundariesOrOverflowIndices()
    {
        using var viewModel = Hierarchy();
        SelectSection(viewModel, "Exercises");
        var original = viewModel.Document!.Pages.Select(page => page.Id).ToArray();

        viewModel.MovePage(-1);
        viewModel.MovePage(int.MaxValue);
        viewModel.MovePage(int.MinValue);
        viewModel.SelectedPage = viewModel.Pages[^1];
        viewModel.MovePage(1);

        Assert.Equal(original, viewModel.Document.Pages.Select(page => page.Id));
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.Autosave.IsDirty);
    }

    [Fact]
    public void ImportedPdfPagesUseCurrentSectionAndInsertAfterLocalSelection()
    {
        using var viewModel = Hierarchy();
        SelectSection(viewModel, "Exercises");
        var sectionId = viewModel.SelectedSection!.Id;
        var pages = new[]
        {
            new NotePage { SectionId = "foreign", Width = 1000, Height = 500, Pdf = new PdfPageSource { AssetId = "original", PageIndex = 3 } },
            new NotePage { SectionId = "foreign", Width = 500, Height = 1000, Pdf = new PdfPageSource { AssetId = "original", PageIndex = 4 } }
        };

        viewModel.AppendPages(pages);

        Assert.Equal(sectionId, pages[0].SectionId);
        Assert.Equal(sectionId, pages[1].SectionId);
        Assert.Same(pages[0], viewModel.SelectedPage!.Page);
        Assert.Equal(2, viewModel.SelectedPage.Number);
        Assert.Same(pages[1], viewModel.Pages[2].Page);
        Assert.Equal("B2", viewModel.Pages[3].Page.Texts[0].Text);
        Assert.Equal(1000, pages[0].Width);
        Assert.Equal(3, pages[0].Pdf!.PageIndex);
        AssertCanonical(viewModel.Document!);
        viewModel.Undo();
        Assert.Equal(new[] { "B1", "B2" }, Labels(viewModel.Pages));
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public void MovingAndCopyingAcrossSectionsFollowStablePageIdsAndUndoTheirMembership()
    {
        using var viewModel = Hierarchy();
        SelectSection(viewModel, "Revision");
        var sourceSectionId = viewModel.SelectedSection!.Id;
        var source = viewModel.SelectedPage!.Page;
        source.Images.Add(new NoteImage { AssetId = "shared-image" });
        source.Texts[0].Bold = true;
        source.InkData = [1, 2, 3];
        viewModel.ReplaceDocument(viewModel.Document!, true);
        SelectSection(viewModel, "Revision");
        var destination = viewModel.Sections.Single(section => section.Title == "Exercises").Id;

        viewModel.MovePageToSection(destination);

        Assert.Same(source, viewModel.SelectedPage!.Page);
        Assert.Equal(destination, viewModel.SelectedSection!.Id);
        Assert.Equal(source.Id, viewModel.Pages[^1].Page.Id);
        Assert.Equal("0 pages", viewModel.Sections.Single(section => section.Id == sourceSectionId).PageCountText);
        Assert.Equal(6, viewModel.Document!.Pages.Count);
        viewModel.Undo();
        Assert.Equal(sourceSectionId, viewModel.SelectedSection!.Id);
        Assert.Equal(source.Id, viewModel.SelectedPage!.Page.Id);
        Assert.False(viewModel.CanUndo);
        viewModel.Redo();
        Assert.Equal(destination, viewModel.SelectedSection!.Id);
        Assert.Equal(source.Id, viewModel.SelectedPage!.Page.Id);

        viewModel.CopyPageToSection(sourceSectionId);
        var copy = viewModel.SelectedPage!.Page;
        Assert.Equal(sourceSectionId, viewModel.SelectedSection!.Id);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.NotEqual(source.Texts[0].Id, copy.Texts[0].Id);
        Assert.NotEqual(source.Images[0].Id, copy.Images[0].Id);
        Assert.Equal(source.Images[0].AssetId, copy.Images[0].AssetId);
        Assert.True(copy.Texts[0].Bold);
        Assert.Equal(source.InkData, copy.InkData);
        copy.Texts[0].Text = "independent copy";
        Assert.Equal("C1", viewModel.Document.Pages.Single(page => page.Id == source.Id).Texts[0].Text);
        Assert.Equal(7, viewModel.Document.Pages.Count);
        AssertCanonical(viewModel.Document);
    }

    [Fact]
    public void AddingToAnEmptySectionInsertsBeforeFollowingSections()
    {
        using var viewModel = Hierarchy();
        SelectSection(viewModel, "Exercises");
        viewModel.DeletePage(); viewModel.DeletePage();
        var sectionId = viewModel.SelectedSection!.Id;
        Assert.Empty(viewModel.Pages);
        Assert.Null(viewModel.SelectedPage);
        Assert.Equal("0 pages", viewModel.SectionPageCountText);

        viewModel.AddPage(PaperTemplate.Graph);

        Assert.Equal(sectionId, Assert.Single(viewModel.Pages).Page.SectionId);
        Assert.Equal(1, viewModel.SelectedPage!.Number);
        Assert.Equal(PaperTemplate.Graph, viewModel.SelectedPage.Page.Template);
        Assert.Equal(3, viewModel.Document!.Pages.FindIndex(page => page.Id == viewModel.SelectedPage.Page.Id));
        Assert.Equal("C1", viewModel.Document.Pages[^1].Texts[0].Text);
        AssertCanonical(viewModel.Document);
    }

    [Fact]
    public void SectionsCanBeAddedRenamedReorderedAndRestoredWithWholeHierarchyHistory()
    {
        using var viewModel = Hierarchy();
        viewModel.AddSection("  Lab notes  ", PaperTemplate.DotGrid);
        var addedId = viewModel.SelectedSection!.Id;
        var addedPageId = Assert.Single(viewModel.Pages).Page.Id;
        Assert.Equal("Lab notes", viewModel.SelectedSection.Title);
        Assert.Equal(PaperTemplate.DotGrid, viewModel.Pages[0].Page.Template);
        viewModel.RenameSection("  Laboratory  ");
        viewModel.MoveSection(-2);

        Assert.Equal(new[] { "Lectures", "Laboratory", "Exercises", "Revision" }, viewModel.Sections.Select(section => section.Title));
        Assert.Equal(addedId, viewModel.SelectedSection!.Id);
        Assert.Equal(addedPageId, viewModel.SelectedPage!.Page.Id);
        AssertCanonical(viewModel.Document!);
        viewModel.Undo();
        Assert.Equal(new[] { "Lectures", "Exercises", "Revision", "Laboratory" }, viewModel.Sections.Select(section => section.Title));
        Assert.Equal(addedId, viewModel.SelectedSection!.Id);
        viewModel.Undo();
        Assert.Equal("Lab notes", viewModel.SelectedSection!.Title);
        viewModel.Undo();
        Assert.Equal(3, viewModel.Sections.Count);
        Assert.Equal(6, viewModel.Document!.Pages.Count);
        Assert.False(viewModel.CanUndo);
        viewModel.Redo(); viewModel.Redo(); viewModel.Redo();
        Assert.Equal(new[] { "Lectures", "Laboratory", "Exercises", "Revision" }, viewModel.Sections.Select(section => section.Title));
        Assert.Contains(viewModel.Document.Pages, page => page.Id == addedPageId && page.SectionId == addedId);
    }

    [Fact]
    public void DeletingSectionRemovesItsPagesButUndoRestoresEverything()
    {
        using var viewModel = Hierarchy();
        SelectSection(viewModel, "Exercises");
        var sectionId = viewModel.SelectedSection!.Id;
        var pageIds = viewModel.Pages.Select(page => page.Page.Id).ToArray();

        viewModel.DeleteSection();

        Assert.DoesNotContain(viewModel.Sections, section => section.Id == sectionId);
        Assert.DoesNotContain(viewModel.Document!.Pages, page => pageIds.Contains(page.Id));
        Assert.Equal("Revision", viewModel.SelectedSection!.Title);
        Assert.Equal(new[] { "C1" }, Labels(viewModel.Pages));
        Assert.Equal(4, viewModel.Document.Pages.Count);
        viewModel.Undo();
        Assert.Equal(3, viewModel.Sections.Count);
        Assert.Equal(pageIds, viewModel.Document.Pages.Where(page => page.SectionId == sectionId).Select(page => page.Id));
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public void LastSectionCannotBeDeletedAndLastNotebookPageIsReplacedWithBlank()
    {
        using var viewModel = new MainViewModel(new MemoryRepository());
        viewModel.ReplaceDocument(new NotebookDocument { Pages = [Page("last page")] }, true);
        var sectionId = Assert.Single(viewModel.Sections).Id;
        var originalPageId = viewModel.SelectedPage!.Page.Id;

        viewModel.DeleteSection();
        Assert.False(viewModel.CanDeleteSection);
        Assert.False(viewModel.CanUndo);
        Assert.Equal(originalPageId, Assert.Single(viewModel.Pages).Page.Id);
        viewModel.DeletePage();

        var replacement = Assert.Single(viewModel.Pages).Page;
        Assert.NotEqual(originalPageId, replacement.Id);
        Assert.Equal(sectionId, replacement.SectionId);
        Assert.Empty(replacement.Texts);
        Assert.Equal(sectionId, Assert.Single(viewModel.Sections).Id);
        viewModel.Undo();
        Assert.Equal(originalPageId, Assert.Single(viewModel.Pages).Page.Id);
        Assert.Equal("last page", viewModel.Pages[0].Page.Texts[0].Text);
    }

    [Fact]
    public void DeletingTheOnlyPopulatedSectionKeepsOnePageInRemainingEmptySection()
    {
        using var viewModel = Hierarchy();
        var sourceId = viewModel.SelectedSection!.Id;
        var destination = viewModel.Sections.Single(section => section.Title == "Revision").Id;
        while (viewModel.Document!.Pages.Any(page => page.SectionId != destination))
        {
            var source = viewModel.Sections.First(section => section.Id != destination && section.PageCount > 0);
            viewModel.SelectedSection = source;
            viewModel.MovePageToSection(destination);
        }
        Assert.Equal(6, viewModel.Pages.Count);

        viewModel.DeleteSection();

        Assert.Equal(2, viewModel.Sections.Count);
        var page = Assert.Single(viewModel.Document!.Pages);
        Assert.Equal(viewModel.SelectedSection!.Id, page.SectionId);
        Assert.Empty(page.Texts);
        Assert.Single(viewModel.Pages);
        AssertCanonical(viewModel.Document);
    }

    [Fact]
    public void InvalidHierarchyActionsAreNoOpsAndNullSelectionCannotHideActiveSection()
    {
        using var viewModel = Hierarchy();
        var original = JsonSerializer.Serialize(viewModel.Document, DocumentJson.Options);
        var selected = viewModel.SelectedSection;
        viewModel.AddSection(" "); viewModel.RenameSection("");
        viewModel.RenameSection(selected!.Title);
        viewModel.MoveSection(-1); viewModel.MoveSection(int.MaxValue); viewModel.MoveSection(int.MinValue);
        viewModel.MovePageToSection("missing"); viewModel.CopyPageToSection("missing");
        viewModel.MovePageToSection(selected.Id); viewModel.AppendPages([]);
        viewModel.SelectedSection = null;

        Assert.Same(selected, viewModel.SelectedSection);
        Assert.Equal(original, JsonSerializer.Serialize(viewModel.Document, DocumentJson.Options));
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.Autosave.IsDirty);
    }

    [Fact]
    public async Task HierarchyPersistsAcrossSaveHomeAndReopenWithFreshNavigation()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("Course notes", "University", PaperTemplate.Cornell);
        var generalId = viewModel.SelectedSection!.Id;
        viewModel.AddSection("Seminars", PaperTemplate.Grid);
        var seminarsId = viewModel.SelectedSection!.Id;
        viewModel.AddPage(PaperTemplate.DotGrid);
        var movedId = viewModel.SelectedPage!.Page.Id;
        viewModel.MovePageToSection(generalId);
        viewModel.RenameSection("Lectures");
        viewModel.MoveSection(1);
        var id = viewModel.Document!.Id;
        await viewModel.ReturnToLibraryAsync();

        await viewModel.OpenAsync(id);

        Assert.Equal(new[] { "Seminars", "Lectures" }, viewModel.Sections.Select(section => section.Title));
        Assert.Equal(seminarsId, viewModel.SelectedSection!.Id);
        Assert.Single(viewModel.Pages);
        Assert.Equal(PaperTemplate.Grid, viewModel.Pages[0].Page.Template);
        Assert.Equal(1, viewModel.SelectedPage!.Number);
        Assert.Contains(viewModel.Document!.Pages, page => page.Id == movedId && page.SectionId == generalId);
        Assert.Equal("3 pages", viewModel.PageCountText);
        Assert.Equal(3, Assert.Single(viewModel.Notebooks).PageCount);
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.CanRedo);
        Assert.False(viewModel.Autosave.IsDirty);
        AssertCanonical(viewModel.Document);
    }

    [Fact]
    public async Task DeletingNotebookClearsSectionNavigationAsWellAsPagesAndHistory()
    {
        using var directory = new StorageTestDirectory();
        using var viewModel = new MainViewModel(new SqliteNotebookRepository(directory.DatabasePath));
        await viewModel.CreateAsync("Delete hierarchy", "My Notes");
        viewModel.AddSection("Another section");
        var id = viewModel.Document!.Id;

        await viewModel.DeleteNotebookAsync(id);

        Assert.Empty(viewModel.Sections);
        Assert.Null(viewModel.SelectedSection);
        Assert.Equal("", viewModel.SelectedSectionTitle);
        Assert.Equal("0 pages", viewModel.SectionPageCountText);
        Assert.False(viewModel.CanDeleteSection);
        Assert.Empty(viewModel.Pages);
        Assert.Null(viewModel.Document);
        Assert.False(viewModel.CanUndo);
        viewModel.Undo(); viewModel.Redo(); viewModel.AddSection("Must not revive");
        Assert.Empty(viewModel.Sections);
    }

    private static MainViewModel Hierarchy()
    {
        var sections = new[] { new NoteSection { Title = "Lectures" }, new NoteSection { Title = "Exercises" }, new NoteSection { Title = "Revision" } };
        var document = new NotebookDocument
        {
            Title = "Course", Sections = sections.ToList(),
            Pages = [Page("A1", sections[0].Id), Page("A2", sections[0].Id), Page("A3", sections[0].Id), Page("B1", sections[1].Id), Page("B2", sections[1].Id), Page("C1", sections[2].Id)]
        };
        var viewModel = new MainViewModel(new MemoryRepository());
        viewModel.ReplaceDocument(document, true);
        return viewModel;
    }
    private static NotePage Page(string label, string sectionId = "") => new() { SectionId = sectionId, Texts = [new() { Text = label }] };
    private static IEnumerable<string> Labels(IEnumerable<PageViewModel> pages) => pages.Select(page => page.Page.Texts[0].Text);
    private static void SelectSection(MainViewModel viewModel, string title) => viewModel.SelectedSection = viewModel.Sections.Single(section => section.Title == title);
    private static void AssertCanonical(NotebookDocument document)
    {
        Assert.Equal(document.Sections.SelectMany(section => document.Pages.Where(page => page.SectionId == section.Id)).Select(page => page.Id), document.Pages.Select(page => page.Id));
        Assert.Equal(document.Pages.Count, document.Pages.Select(page => page.Id).Distinct().Count());
        Assert.All(document.Pages, page => Assert.Contains(document.Sections, section => section.Id == page.SectionId));
    }
    private sealed class MemoryRepository : INotebookRepository
    {
        private readonly Dictionary<string, NotebookDocument> _documents = [];
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => Task.FromResult<IReadOnlyList<NotebookSummary>>(_documents.Values.Select(document => new NotebookSummary { Id = document.Id, Title = document.Title, Folder = document.Folder, PageCount = document.Pages.Count }).ToArray());
        public Task<NotebookDocument?> LoadAsync(string id) => Task.FromResult(_documents.GetValueOrDefault(id)?.Snapshot());
        public Task SaveAsync(NotebookDocument document) { _documents[document.Id] = document.Snapshot(); return Task.CompletedTask; }
        public Task DeleteAsync(string id) { _documents.Remove(id); return Task.CompletedTask; }
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes) => throw new NotSupportedException();
        public Task<AssetData> GetAssetAsync(string id) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
