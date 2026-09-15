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
    public ObservableCollection<SectionViewModel> Sections { get; } = [];
    public ObservableCollection<PageViewModel> Pages { get; } = [];
    private readonly NotebookHistory _history = new();
    private List<NotebookSummary> _library = [];
    private NotebookDocument? _document;
    private PageViewModel? _selectedPage;
    private SectionViewModel? _selectedSection;
    private readonly Dictionary<string, string> _sectionPageSelections = [];
    private bool _rebuildingSections;
    private string _search = "", _status = "Your notebooks, on this device", _operation = "";
    private double _zoom = .85;
    private bool _isBusy, _isLibraryVisible = true;
    public event EventHandler? DocumentReplaced;
    public event EventHandler? HistoryChanged;
    public NotebookDocument? Document { get => _document; private set { _document = value; Notify(); Notify(nameof(Title)); Notify(nameof(Folder)); Notify(nameof(PageCountText)); } }
    public string Title => Document?.Title ?? "Moye";
    public string Folder => Document?.Folder ?? "My Notes";
    public string PageCountText => $"{Document?.Pages.Count ?? 0} {(Document?.Pages.Count == 1 ? "page" : "pages")}";
    public string SectionPageCountText => $"{Pages.Count} {(Pages.Count == 1 ? "page" : "pages")}";
    public string SelectedSectionTitle => SelectedSection?.Title ?? "";
    public bool CanDeleteSection => Sections.Count > 1;
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
    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is not null && !Pages.Contains(value)) return;
            if (!Set(ref _selectedPage, value)) return;
            if (value is not null && value.Page.SectionId == SelectedSection?.Id)
                _sectionPageSelections[value.Page.SectionId] = value.Page.Id;
            Notify(nameof(CurrentPageText));
        }
    }
    public SectionViewModel? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (_rebuildingSections || value is null || Document is null) return;
            var section = Sections.FirstOrDefault(candidate => candidate.Id == value.Id);
            if (section is null || section.Id == _selectedSection?.Id) return;
            // The shell commits the old page editors before changing sections.
            // Navigation alone must not create history or schedule a save.
            _selectedSection = section;
            RebuildVisiblePages();
            NotifySectionState();
            DocumentReplaced?.Invoke(this, EventArgs.Empty);
        }
    }
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
        NotebookStructure.Normalize(document);
        await Repository.SaveAsync(document); await RefreshLibraryAsync();
        ReplaceDocument(document, true); IsLibraryVisible = false; UpdateSaveStatus();
    }

    public async Task ReturnToLibraryAsync()
    {
        await Autosave.FlushAsync();
        await RefreshLibraryAsync();
        IsLibraryVisible = true; UpdateSaveStatus();
    }

    public async Task DeleteNotebookAsync(string id)
    {
        // The UI commits editors and blocks input before this operation. Finish
        // queued/in-flight saves first so they cannot recreate the deleted note.
        await Autosave.FlushAsync();
        await Repository.DeleteAsync(id);
        if (Document?.Id == id)
        {
            Document = null;
            _history.Clear(); Pages.Clear(); SelectedPage = null;
            _rebuildingSections = true;
            try { Sections.Clear(); _selectedSection = null; _sectionPageSelections.Clear(); }
            finally { _rebuildingSections = false; }
            IsLibraryVisible = true;
            Notify(nameof(PageCountText)); Notify(nameof(CurrentPageText));
            NotifySectionState();
            DocumentReplaced?.Invoke(this, EventArgs.Empty); UpdateHistory();
        }
        // Update locally after deletion succeeds; a failed list refresh must not
        // leave a deleted notebook selectable or its editor/history active.
        _library.RemoveAll(notebook => notebook.Id == id);
        FilterLibrary(); UpdateSaveStatus();
    }

    public void ReplaceDocument(NotebookDocument document, bool resetHistory = false)
        => ReplaceDocumentCore(document, resetHistory);

    private void ReplaceDocumentCore(NotebookDocument document, bool resetHistory, string? sectionId = null, string? pageId = null)
    {
        var retainNavigation = !resetHistory && Document?.Id == document.Id;
        sectionId ??= retainNavigation ? SelectedSection?.Id : null;
        pageId ??= retainNavigation ? SelectedPage?.Page.Id : null;
        if (!retainNavigation) _sectionPageSelections.Clear();
        NotebookStructure.Normalize(document);
        // When undo/redo moves a selected page between sections, follow that
        // page by stable identity rather than its old section-local number.
        var selectedPage = document.Pages.FirstOrDefault(page => page.Id == pageId);
        if (selectedPage is not null) sectionId = selectedPage.SectionId;
        Document = document;
        _rebuildingSections = true;
        try
        {
            Sections.Clear();
            for (int i = 0; i < document.Sections.Count; i++)
            {
                var section = document.Sections[i];
                Sections.Add(new(section, i + 1, document.Pages.Count(page => page.SectionId == section.Id)));
            }
            _selectedSection = Sections.FirstOrDefault(section => section.Id == sectionId) ?? Sections.FirstOrDefault();
            RebuildVisiblePages(pageId);
        }
        finally { _rebuildingSections = false; }
        if (resetHistory) _history.Reset(document);
        Notify(nameof(PageCountText)); Notify(nameof(CurrentPageText));
        NotifySectionState();
        UpdateLibrarySummary();
        DocumentReplaced?.Invoke(this, EventArgs.Empty); UpdateHistory();
    }

    private void RebuildVisiblePages(string? preferredPageId = null)
    {
        if (preferredPageId is null && SelectedSection is not null)
            _sectionPageSelections.TryGetValue(SelectedSection.Id, out preferredPageId);
        Pages.Clear();
        if (Document is not null && SelectedSection is not null)
        {
            foreach (var page in Document.Pages.Where(page => page.SectionId == SelectedSection.Id))
                Pages.Add(new(page, Pages.Count + 1, Zoom));
        }
        SelectedPage = Pages.FirstOrDefault(page => page.Page.Id == preferredPageId) ?? Pages.FirstOrDefault();
        Notify(nameof(SectionPageCountText)); Notify(nameof(CurrentPageText));
    }

    private void NotifySectionState()
    {
        Notify(nameof(SelectedSection)); Notify(nameof(SelectedSectionTitle));
        Notify(nameof(SectionPageCountText)); Notify(nameof(CanDeleteSection));
    }

    public void Changed(bool rebuildPages = false)
        => RecordChange(rebuildPages);

    private void RecordChange(bool rebuildPages, string? sectionId = null, string? pageId = null)
    {
        if (Document is null) return;
        if (rebuildPages) NotebookStructure.Normalize(Document);
        Document.ModifiedUtc = DateTimeOffset.UtcNow;
        _history.Record(Document); Autosave.Schedule(Document);
        if (rebuildPages) ReplaceDocumentCore(Document, false, sectionId, pageId);
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
        if (Document is null || SelectedSection is null) return;
        var page = new NotePage { Template = template, SectionId = SelectedSection.Id };
        Document.Pages.Insert(InsertionIndex(SelectedSection.Id, SelectedPage?.Page.Id), page);
        RecordChange(true, SelectedSection.Id, page.Id);
    }

    public void DuplicatePage()
    {
        if (Document is null || SelectedPage is null) return;
        var index = Document.Pages.FindIndex(page => page.Id == SelectedPage.Page.Id);
        if (index < 0) return;
        var page = CopyPage(SelectedPage.Page);
        Document.Pages.Insert(index + 1, page);
        RecordChange(true, page.SectionId, page.Id);
    }

    public void MovePage(int delta)
    {
        if (Document is null || SelectedPage is null) return;
        var localIndex = Pages.IndexOf(SelectedPage);
        var target = (long)localIndex + delta;
        if (localIndex < 0 || target < 0 || target >= Pages.Count || target == localIndex) return;
        var page = SelectedPage.Page;
        var sourceIndex = Document.Pages.FindIndex(candidate => candidate.Id == page.Id);
        var targetIndex = Document.Pages.FindIndex(candidate => candidate.Id == Pages[(int)target].Page.Id);
        if (sourceIndex < 0 || targetIndex < 0) return;
        Document.Pages.RemoveAt(sourceIndex);
        Document.Pages.Insert(targetIndex, page);
        RecordChange(true, page.SectionId, page.Id);
    }

    public void DeletePage()
    {
        if (Document is null || SelectedPage is null) return;
        var localIndex = Pages.IndexOf(SelectedPage);
        var sectionId = SelectedPage.Page.SectionId;
        var index = Document.Pages.FindIndex(page => page.Id == SelectedPage.Page.Id);
        if (index < 0) return;
        Document.Pages.RemoveAt(index);
        if (Document.Pages.Count == 0) Document.Pages.Add(new() { SectionId = sectionId });
        var remaining = Document.Pages.Where(page => page.SectionId == sectionId).ToArray();
        var selectedId = remaining.Length == 0 ? null : remaining[Math.Clamp(localIndex, 0, remaining.Length - 1)].Id;
        RecordChange(true, sectionId, selectedId);
    }

    public void SetTemplate(PaperTemplate template)
    {
        if (SelectedPage is null || SelectedPage.Page.Pdf is not null) return;
        SelectedPage.Page.Template = template; Changed(true);
    }

    public void AppendPages(IReadOnlyList<NotePage> pages)
    {
        if (Document is null || SelectedSection is null || pages.Count == 0) return;
        var sectionId = SelectedSection.Id;
        var index = InsertionIndex(sectionId, SelectedPage?.Page.Id);
        foreach (var page in pages) page.SectionId = sectionId;
        Document.Pages.InsertRange(index, pages);
        RecordChange(true, sectionId, pages[0].Id);
    }

    public void AddSection(string title, PaperTemplate? template = null)
    {
        if (Document is null || string.IsNullOrWhiteSpace(title)) return;
        var section = new NoteSection { Title = title.Trim() };
        var paper = template ?? (SelectedPage?.Page.Pdf is null ? SelectedPage?.Page.Template : null) ?? PaperTemplate.Ruled;
        var page = new NotePage { SectionId = section.Id, Template = paper };
        Document.Sections.Add(section); Document.Pages.Add(page);
        RecordChange(true, section.Id, page.Id);
    }

    public void RenameSection(string title)
    {
        if (Document is null || SelectedSection is null || string.IsNullOrWhiteSpace(title)) return;
        var section = Document.Sections.FirstOrDefault(candidate => candidate.Id == SelectedSection.Id);
        if (section is null || section.Title == title.Trim()) return;
        section.Title = title.Trim();
        RecordChange(true, section.Id, SelectedPage?.Page.Id);
    }

    public void MoveSection(int delta)
    {
        if (Document is null || SelectedSection is null) return;
        var index = Document.Sections.FindIndex(section => section.Id == SelectedSection.Id);
        var target = (long)index + delta;
        if (index < 0 || target < 0 || target >= Document.Sections.Count || target == index) return;
        var section = Document.Sections[index];
        Document.Sections.RemoveAt(index); Document.Sections.Insert((int)target, section);
        RecordChange(true, section.Id, SelectedPage?.Page.Id);
    }

    public void DeleteSection()
    {
        if (Document is null || SelectedSection is null || !CanDeleteSection) return;
        var index = Document.Sections.FindIndex(section => section.Id == SelectedSection.Id);
        if (index < 0) return;
        var sectionId = SelectedSection.Id;
        Document.Sections.RemoveAt(index);
        Document.Pages.RemoveAll(page => page.SectionId == sectionId);
        var nextSection = Document.Sections[Math.Min(index, Document.Sections.Count - 1)];
        if (Document.Pages.Count == 0) Document.Pages.Add(new() { SectionId = nextSection.Id });
        RecordChange(true, nextSection.Id);
    }

    public void MovePageToSection(string targetId)
    {
        if (Document is null || SelectedPage is null || SelectedPage.Page.SectionId == targetId || !Document.Sections.Any(section => section.Id == targetId)) return;
        var page = SelectedPage.Page;
        var index = Document.Pages.FindIndex(candidate => candidate.Id == page.Id);
        if (index < 0) return;
        Document.Pages.RemoveAt(index);
        page.SectionId = targetId;
        Document.Pages.Insert(InsertionIndex(targetId), page);
        RecordChange(true, targetId, page.Id);
    }

    public void CopyPageToSection(string targetId)
    {
        if (Document is null || SelectedPage is null || !Document.Sections.Any(section => section.Id == targetId)) return;
        var page = CopyPage(SelectedPage.Page);
        page.SectionId = targetId;
        Document.Pages.Insert(InsertionIndex(targetId), page);
        RecordChange(true, targetId, page.Id);
    }

    private static NotePage CopyPage(NotePage source)
    {
        var page = source.Snapshot(); page.Id = Guid.NewGuid().ToString("N");
        foreach (var text in page.Texts) text.Id = Guid.NewGuid().ToString("N");
        foreach (var image in page.Images) image.Id = Guid.NewGuid().ToString("N");
        return page;
    }

    private int InsertionIndex(string sectionId, string? afterPageId = null)
    {
        if (Document is null) return 0;
        if (afterPageId is not null)
        {
            var selected = Document.Pages.FindIndex(page => page.Id == afterPageId && page.SectionId == sectionId);
            if (selected >= 0) return selected + 1;
        }
        var last = Document.Pages.FindLastIndex(page => page.SectionId == sectionId);
        if (last >= 0) return last + 1;
        var sectionIndex = Document.Sections.FindIndex(section => section.Id == sectionId);
        var followingIds = Document.Sections.Skip(sectionIndex + 1).Select(section => section.Id).ToHashSet();
        var followingPage = Document.Pages.FindIndex(page => followingIds.Contains(page.SectionId));
        return followingPage < 0 ? Document.Pages.Count : followingPage;
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
