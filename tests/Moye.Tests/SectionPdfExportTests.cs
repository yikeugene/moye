using System.Text.Json;
using Moye.Models;
using Moye.Services;
using Moye.ViewModels;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Moye.Tests;

public sealed class SectionPdfExportTests
{
    [Fact]
    public void ExportWithoutAnOpenNotebookHasNoSnapshot()
    {
        using var model = new MainViewModel(new MemoryRepository());

        Assert.Null(model.CreateSelectedSectionExportSnapshot());
    }

    [Fact]
    public void ExportDoesNotFallBackToAnotherSectionWhenCurrentSectionIsInvalid()
    {
        using var model = OpenNotebook(new MemoryRepository());
        var currentId = model.SelectedSection!.Id;
        model.Document!.Sections.RemoveAll(section => section.Id == currentId);

        Assert.Null(model.CreateSelectedSectionExportSnapshot());
    }

    [Fact]
    public void EmptySectionProducesAnEmptySnapshotInsteadOfExportingOtherSections()
    {
        using var model = OpenNotebook(new MemoryRepository());
        model.SelectedSection = model.Sections.Single(section => section.Title == "Revision");

        var snapshot = Assert.IsType<NotebookDocument>(model.CreateSelectedSectionExportSnapshot());

        Assert.Empty(snapshot.Pages);
        Assert.Equal("Revision", Assert.Single(snapshot.Sections).Title);
        Assert.Equal("Mathematics - Revision", snapshot.Title);
        Assert.Equal(4, model.Document!.Pages.Count);
    }

    [Fact]
    public async Task ExportUsesTheSelectedSectionAndItsDisplayedPageOrderWithoutEditingTheNotebook()
    {
        using var model = OpenNotebook(new MemoryRepository());
        model.SelectedSection = model.Sections.Single(section => section.Title == "Calculus");
        model.SelectedPage = model.Pages[1];
        model.MovePage(-1);
        await model.Autosave.FlushAsync();
        var original = JsonSerializer.Serialize(model.Document, DocumentJson.Options);
        var undoBefore = model.CanUndo;

        var snapshot = Assert.IsType<NotebookDocument>(model.CreateSelectedSectionExportSnapshot());

        Assert.Equal(new[] { "Calculus 2", "Calculus 1" }, snapshot.Pages.Select(page => page.Texts[0].Text));
        Assert.Equal(model.Pages.Select(page => page.Page.Id), snapshot.Pages.Select(page => page.Id));
        Assert.Equal(model.SelectedSection.Id, Assert.Single(snapshot.Sections).Id);
        Assert.All(snapshot.Pages, page => Assert.Equal(model.SelectedSection.Id, page.SectionId));
        Assert.Equal("Mathematics - Calculus", snapshot.Title);
        Assert.Equal(model.Document!.Id, snapshot.Id);
        Assert.Equal(model.Document.Folder, snapshot.Folder);
        Assert.Equal(model.Document.CreatedUtc, snapshot.CreatedUtc);
        Assert.Equal(model.Document.ModifiedUtc, snapshot.ModifiedUtc);
        Assert.Equal(original, JsonSerializer.Serialize(model.Document, DocumentJson.Options));
        Assert.Equal(undoBefore, model.CanUndo);
        Assert.False(model.Autosave.IsDirty);
    }

    [Fact]
    public void CapturedSectionAndEditableMetadataStayIndependentAfterNavigationAndEdits()
    {
        using var model = OpenNotebook(new MemoryRepository());
        model.SelectedSection = model.Sections.Single(section => section.Title == "Calculus");
        var original = model.Pages[0].Page;
        original.InkData = [1, 2, 3];
        original.Images = [new NoteImage { AssetId = "image", Width = 128 }];
        original.Pdf = new PdfPageSource { AssetId = "pdf", PageIndex = 2, CropWidth = 400 };
        var snapshot = Assert.IsType<NotebookDocument>(model.CreateSelectedSectionExportSnapshot());
        var captured = snapshot.Pages[0];

        model.SelectedSection = model.Sections[0];
        original.Texts[0].Text = "Later edit";
        original.Images[0].Width = 300;
        original.Pdf.PageIndex = 8;
        original.InkData = [4, 5];
        model.Document!.Sections.Single(section => section.Title == "Calculus").Title = "Renamed later";

        Assert.Equal("Mathematics - Calculus", snapshot.Title);
        Assert.Equal("Calculus", Assert.Single(snapshot.Sections).Title);
        Assert.Equal("Calculus 1", captured.Texts[0].Text);
        Assert.Equal(128, captured.Images[0].Width);
        Assert.Equal(2, captured.Pdf!.PageIndex);
        Assert.Equal(new byte[] { 1, 2, 3 }, captured.InkData);
        Assert.NotSame(original, captured);
        Assert.NotSame(original.Texts[0], captured.Texts[0]);
        Assert.NotSame(original.Images[0], captured.Images[0]);
        Assert.NotSame(original.Pdf, captured.Pdf);

        captured.Texts[0].Text = "Snapshot edit";
        captured.Images[0].Width = 500;
        captured.Pdf.CropWidth = 700;
        snapshot.Pages.Clear();
        Assert.Equal("Later edit", original.Texts[0].Text);
        Assert.Equal(300, original.Images[0].Width);
        Assert.Equal(400, original.Pdf.CropWidth);
        Assert.Equal(4, model.Document.Pages.Count);
    }

    [Fact]
    public async Task PdfContainsOnlySelectedPagesInOrderAndNeverReadsExcludedAssets()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new MemoryRepository();
        using var model = OpenNotebook(repository);
        // A broken asset elsewhere must not prevent exporting the current section.
        model.Document!.Pages[0].Images = [new NoteImage { AssetId = "missing-image" }];
        model.Document.Pages[0].Pdf = new PdfPageSource { AssetId = "missing-pdf" };
        model.SelectedSection = model.Sections.Single(section => section.Title == "Calculus");
        var first = model.Pages[0].Page;
        first.Texts.Clear(); first.Width = 480; first.Height = 320;
        var second = model.Pages[1].Page;
        second.Texts.Clear(); second.Width = 640; second.Height = 800;
        using (var source = new PdfDocument())
        {
            var sourcePage = source.AddPage();
            sourcePage.Width = XUnit.FromPoint(480);
            sourcePage.Height = XUnit.FromPoint(600);
            using var bytes = new MemoryStream();
            source.Save(bytes, false);
            var asset = await repository.PutAssetAsync("lecture.pdf", "application/pdf", bytes.ToArray());
            second.Pdf = new PdfPageSource { AssetId = asset.Id, CropWidth = 480, CropHeight = 600 };
        }
        model.SelectedPage = model.Pages[1];
        model.MovePage(-1);
        var snapshot = Assert.IsType<NotebookDocument>(model.CreateSelectedSectionExportSnapshot());
        model.SelectedSection = model.Sections[0];
        var path = Path.Combine(directory.Root, "selected-section.pdf");

        await model.Pdf.ExportAsync(path, snapshot);

        using var exported = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(2, exported.PageCount);
        Assert.Equal("Mathematics - Calculus", exported.Info.Title);
        Assert.Equal(480, exported.Pages[0].Width.Point);
        Assert.Equal(600, exported.Pages[0].Height.Point);
        Assert.Equal(360, exported.Pages[1].Width.Point);
        Assert.Equal(240, exported.Pages[1].Height.Point);
        Assert.Equal(second.Pdf!.AssetId, Assert.Single(repository.RequestedAssets));
        Assert.Equal(4, model.Document.Pages.Count);
        Assert.Equal(3, model.Document.Sections.Count);
        await model.Autosave.FlushAsync();
    }

    private static MainViewModel OpenNotebook(INotebookRepository repository)
    {
        var algebra = new NoteSection { Title = "Algebra" };
        var calculus = new NoteSection { Title = "Calculus" };
        var revision = new NoteSection { Title = "Revision" };
        var model = new MainViewModel(repository);
        model.ReplaceDocument(new NotebookDocument
        {
            Title = "Mathematics", Folder = "Semester 1",
            Sections = [algebra, calculus, revision],
            Pages = [Page(algebra.Id, "Algebra 1"), Page(algebra.Id, "Algebra 2"),
                Page(calculus.Id, "Calculus 1"), Page(calculus.Id, "Calculus 2")]
        }, true);
        return model;
    }

    private static NotePage Page(string sectionId, string label) => new()
    {
        SectionId = sectionId, Texts = [new NoteText { Text = label }]
    };

    private sealed class MemoryRepository : INotebookRepository
    {
        private readonly Dictionary<string, AssetData> _assets = [];
        public List<string> RequestedAssets { get; } = [];
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => Task.FromResult<IReadOnlyList<NotebookSummary>>([]);
        public Task<NotebookDocument?> LoadAsync(string id) => Task.FromResult<NotebookDocument?>(null);
        public Task SaveAsync(NotebookDocument document) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes)
        {
            var asset = new AssetData(Guid.NewGuid().ToString("N"), fileName, contentType, bytes);
            _assets.Add(asset.Id, asset);
            return Task.FromResult(asset);
        }
        public Task<AssetData> GetAssetAsync(string id)
        {
            RequestedAssets.Add(id);
            return Task.FromResult(_assets[id]);
        }
        public void Dispose() { }
    }
}
