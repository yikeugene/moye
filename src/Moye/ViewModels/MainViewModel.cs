using System.Collections.ObjectModel;
using System.Windows;
using Moye.Models;
using Moye.Services;

namespace Moye.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public INotebookRepository Repository { get; }
    public IPdfService Pdf { get; }
    public IBackupService Backup { get; }
    public AutosaveCoordinator Autosave { get; }
    public ObservableCollection<NotebookSummary> Notebooks { get; } = [];
    public ObservableCollection<PageViewModel> Pages { get; } = [];
    private readonly NotebookHistory _history = new();
    private List<NotebookSummary> _library = [];
    private NotebookDocument? _document;
    private PageViewModel? _selectedPage;
    private string _search = "", _status = "Your notebooks, on this device", _operation = "";
    private double _zoom = .85;
    private bool _isBusy, _isLibraryVisible = true;
    public event EventHandler? DocumentReplaced;
    public event EventHandler? HistoryChanged;
    public NotebookDocument? Document { get => _document; private set { _document = value; Notify(); Notify(nameof(Title)); Notify(nameof(Folder)); Notify(nameof(PageCountText)); } }
    public string Title => Document?.Title ?? "Moye";
    public string Folder => Document?.Folder ?? "My Notes";
    public string PageCountText => $"{Pages.Count} {(Pages.Count == 1 ? "page" : "pages")}";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Operation { get => _operation; set => Set(ref _operation, value); }
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) Notify(nameof(CanInteract)); } }
    public bool CanInteract => !IsBusy;
    public bool IsLibraryVisible { get => _isLibraryVisible; private set { if (Set(ref _isLibraryVisible, value)) Notify(nameof(IsEditorVisible)); } }
    public bool IsEditorVisible => !IsLibraryVisible;
    public bool HasNotebooks => _library.Count > 0;
    public bool HasVisibleNotebooks => Notebooks.Count > 0;
    public string LibraryCountText => string.IsNullOrWhiteSpace(Search)
        ? $"{_library.Count} {(_library.Count == 1 ? "notebook" : "notebooks")}" : $"{Notebooks.Count} of {_library.Count} notebooks";
    public string EmptyLibraryTitle => HasNotebooks ? "No notebooks found" : "Your next idea starts here";
    public string EmptyLibraryDescription => HasNotebooks ? "Try searching for another notebook name or folder." : "Create a notebook and choose the paper that works for you.";
    public bool HasSaveError => Autosave.LastError is not null;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public string Search { get => _search; set { if (Set(ref _search, value)) FilterLibrary(); } }
    public double Zoom { get => _zoom; set { if (Set(ref _zoom, Math.Clamp(value, .25, 4))) { foreach (var page in Pages) page.Zoom = _zoom; Notify(nameof(ZoomLabel)); } } }
    public string ZoomLabel => $"{Zoom:P0}";
    public PageViewModel? SelectedPage { get => _selectedPage; set { if (Set(ref _selectedPage, value)) Notify(nameof(CurrentPageText)); } }
    public string CurrentPageText => SelectedPage is null ? "" : $"Page {SelectedPage.Number} of {Pages.Count}";

    public MainViewModel(INotebookRepository repository)
    {
        Repository = repository; Pdf = new PdfService(repository); Backup = new BackupService(repository); Autosave = new(repository);
        var dispatcher = Application.Current?.Dispatcher;
        Autosave.StateChanged += (_, _) =>
        {
            if (dispatcher is null || dispatcher.CheckAccess()) UpdateSaveStatus();
            else if (!dispatcher.HasShutdownStarted) dispatcher.InvokeAsync(UpdateSaveStatus);
        };
    }

    public async Task InitializeAsync()
    {
        await Repository.InitializeAsync(); await RefreshLibraryAsync();
        UpdateSaveStatus();
    }

    public async Task RefreshLibraryAsync()
    {
        _library = (await Repository.ListAsync()).ToList(); FilterLibrary();
    }

    private void FilterLibrary()
    {
        Notebooks.Clear();
        foreach (var note in _library.Where(n => string.IsNullOrWhiteSpace(Search) || n.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase) || n.Folder.Contains(Search, StringComparison.CurrentCultureIgnoreCase))) Notebooks.Add(note);
        Notify(nameof(HasNotebooks)); Notify(nameof(HasVisibleNotebooks)); Notify(nameof(LibraryCountText));
        Notify(nameof(EmptyLibraryTitle)); Notify(nameof(EmptyLibraryDescription));
    }

    public async Task OpenAsync(string id)
    {
        await Autosave.FlushAsync();
        var doc = await Repository.LoadAsync(id) ?? throw new IOException("This notebook could not be found.");
        ReplaceDocument(doc, true); IsLibraryVisible = false; UpdateSaveStatus();
    }

    public async Task CreateAsync(string title, string folder, PaperTemplate template = PaperTemplate.Ruled)
    {
        await Autosave.FlushAsync();
        var document = new NotebookDocument { Title = title.Trim(), Folder = folder.Trim(), Pages = [new() { Template = template }] };
        await Repository.SaveAsync(document); await RefreshLibraryAsync();
        ReplaceDocument(document, true); IsLibraryVisible = false; UpdateSaveStatus();
    }

    public async Task ReturnToLibraryAsync()
    {
        await Autosave.FlushAsync();
        await RefreshLibraryAsync();
        IsLibraryVisible = true; UpdateSaveStatus();
    }

    public void ReplaceDocument(NotebookDocument document, bool resetHistory = false)
    {
        var selectedId = resetHistory ? null : SelectedPage?.Page.Id;
        Document = document; Pages.Clear();
        for (int i = 0; i < document.Pages.Count; i++) Pages.Add(new(document.Pages[i], i + 1, Zoom));
        SelectedPage = Pages.FirstOrDefault(p => p.Page.Id == selectedId) ?? Pages.FirstOrDefault();
        if (resetHistory) _history.Reset(document);
        Notify(nameof(PageCountText)); Notify(nameof(CurrentPageText));
        UpdateLibrarySummary();
        DocumentReplaced?.Invoke(this, EventArgs.Empty); UpdateHistory();
    }

    public void Changed(bool rebuildPages = false)
    {
        if (Document is null) return;
        Document.ModifiedUtc = DateTimeOffset.UtcNow;
        _history.Record(Document); Autosave.Schedule(Document);
        if (rebuildPages) ReplaceDocument(Document);
        Notify(nameof(Title)); Notify(nameof(Folder)); UpdateHistory(); UpdateSaveStatus();
        UpdateLibrarySummary();
    }

    private void UpdateLibrarySummary()
    {
        if (Document is null) return;
        var summary = _library.FirstOrDefault(n => n.Id == Document.Id);
        if (summary is not null)
        {
            bool metadataChanged = summary.Title != Document.Title || summary.Folder != Document.Folder || summary.PageCount != Document.Pages.Count;
            summary.Title = Document.Title; summary.Folder = Document.Folder; summary.PageCount = Document.Pages.Count; summary.ModifiedUtc = Document.ModifiedUtc;
            if (metadataChanged) FilterLibrary();
        }
    }

    public void Rename(string title, string folder)
    {
        if (Document is null) return;
        Document.Title = title.Trim(); Document.Folder = folder.Trim(); Changed();
    }

    public void AddPage(PaperTemplate template = PaperTemplate.Plain)
    {
        if (Document is null) return;
        var page = new NotePage { Template = template };
        var index = SelectedPage is null ? Document.Pages.Count : SelectedPage.Number;
        Document.Pages.Insert(index, page); Changed(true); SelectedPage = Pages[index];
    }

    public void DuplicatePage()
    {
        if (Document is null || SelectedPage is null) return;
        var index = SelectedPage.Number; var page = SelectedPage.Page.Snapshot(); page.Id = Guid.NewGuid().ToString("N");
        foreach (var t in page.Texts) t.Id = Guid.NewGuid().ToString("N");
        foreach (var image in page.Images) image.Id = Guid.NewGuid().ToString("N");
        Document.Pages.Insert(index, page); Changed(true); SelectedPage = Pages[index];
    }

    public void MovePage(int delta)
    {
        if (Document is null || SelectedPage is null) return;
        var index = SelectedPage.Number - 1; var target = index + delta;
        if (target < 0 || target >= Document.Pages.Count) return;
        var page = Document.Pages[index]; Document.Pages.RemoveAt(index); Document.Pages.Insert(target, page); Changed(true); SelectedPage = Pages[target];
    }

    public void DeletePage()
    {
        if (Document is null || SelectedPage is null) return;
        var index = SelectedPage.Number - 1; Document.Pages.RemoveAt(index);
        if (Document.Pages.Count == 0) Document.Pages.Add(new());
        Changed(true); SelectedPage = Pages[Math.Min(index, Pages.Count - 1)];
    }

    public void SetTemplate(PaperTemplate template)
    {
        if (SelectedPage is null || SelectedPage.Page.Pdf is not null) return;
        SelectedPage.Page.Template = template; Changed(true);
    }

    public void AppendPages(IReadOnlyList<NotePage> pages)
    {
        if (Document is null) return;
        var index = SelectedPage?.Number ?? Document.Pages.Count;
        Document.Pages.InsertRange(index, pages); Changed(true); SelectedPage = Pages[index];
    }

    public void Undo()
    {
        var doc = _history.Undo(); if (doc is null) return;
        doc.ModifiedUtc = DateTimeOffset.UtcNow; ReplaceDocument(doc); Autosave.Schedule(doc); UpdateSaveStatus();
    }
    public void Redo()
    {
        var doc = _history.Redo(); if (doc is null) return;
        doc.ModifiedUtc = DateTimeOffset.UtcNow; ReplaceDocument(doc); Autosave.Schedule(doc); UpdateSaveStatus();
    }
    private void UpdateHistory() { Notify(nameof(CanUndo)); Notify(nameof(CanRedo)); HistoryChanged?.Invoke(this, EventArgs.Empty); }
    private void UpdateSaveStatus()
    {
        Status = Autosave.LastError is not null ? "Save failed · Retry or create a backup" : Autosave.IsSaving ? "Saving…" : Autosave.IsDirty ? "Waiting to save…" : Document is null ? "Your notebooks, on this device" : "Saved on this device";
        Notify(nameof(HasSaveError));
    }
    public void Dispose() { Autosave.Dispose(); (Pdf as IDisposable)?.Dispose(); Repository.Dispose(); }
}
