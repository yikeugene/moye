using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Moye.Models;
using Moye.Services;

namespace Moye.UiPreview;

/// <summary>Runs the real window with disposable synthetic notes, independently of SQLite.</summary>
public static class InteractivePreview
{
    /// <param name="outputDir">The project's artifacts directory.</param>
    public static int Run(ResourceDictionary resources, string outputDir, bool compact = false)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var preferencesDirectory = Path.GetFullPath(Path.Combine(outputDir, "interactive-preview"));
        Directory.CreateDirectory(preferencesDirectory);
        var application = new Application
        {
            Resources = resources,
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        using var repository = new MemoryRepository(CreateNotebook());
        var preferences = new WritingPreferencesStore(Path.Combine(preferencesDirectory, "writing-preferences.json"));
        var window = new MainWindow(repository, preferences)
        {
            Title = "Moye · Synthetic UI Test (memory only)"
        };
        if (compact) { window.Width = 1024; window.Height = 700; }
        return application.Run(window);
    }

    private static NotebookDocument CreateNotebook()
    {
        var general = new NoteSection { Title = "General" };
        var algebra = new NoteSection { Title = "Algebra" };
        var calculus = new NoteSection { Title = "Calculus" };
        var document = new NotebookDocument
        {
            Title = "Mathematics",
            Folder = "Synthetic UI Test",
            Sections = [general, algebra, calculus],
            Pages =
            [
                Page(general, "Course overview", "This notebook contains synthetic test data.\n\nUse the sections to try navigation, page moves, undo and typing.\n\nNotes in this test window exist only in memory and reset when it closes.", PaperTemplate.Ruled),
                Page(algebra, "Linear equations", "2x + 3 = 11\nx = 4\n\nTry moving this page to Calculus, then undo the move.", PaperTemplate.Grid),
                Page(algebra, "Vectors and matrices", "v = (2, 3)\nA = [[1, 0], [0, 1]]\n\nThis is the second page in Algebra.", PaperTemplate.DotGrid),
                Page(calculus, "Rates of change", "f(x) = x²\nf′(x) = 2x\n\nUse this section to test page insertion and section ordering.", PaperTemplate.Cornell)
            ]
        };
        NotebookStructure.Normalize(document);
        return document;
    }

    private static NotePage Page(NoteSection section, string title, string body, PaperTemplate paper) => new()
    {
        SectionId = section.Id,
        Template = paper,
        Texts =
        [
            new NoteText { X = 64, Y = 80, Width = 650, Height = 64, Text = title, FontFamily = "Segoe UI, Microsoft JhengHei", FontSize = 30, Bold = true },
            new NoteText { X = 64, Y = 164, Width = 640, Height = 350, Text = body, FontFamily = "Segoe UI, Microsoft JhengHei", FontSize = 22 }
        ]
    };

    private sealed class MemoryRepository : INotebookRepository
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, NotebookDocument> _documents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AssetData> _assets = new(StringComparer.Ordinal);
        private bool _disposed;

        public MemoryRepository(NotebookDocument document) => _documents[document.Id] = Snapshot(document);

        public Task InitializeAsync()
        {
            lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotebookSummary>> ListAsync()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return Task.FromResult<IReadOnlyList<NotebookSummary>>(_documents.Values
                    .OrderByDescending(document => document.ModifiedUtc).ThenBy(document => document.Id, StringComparer.Ordinal)
                    .Select(document => new NotebookSummary
                    {
                        Id = document.Id, Title = document.Title, Folder = document.Folder,
                        ModifiedUtc = document.ModifiedUtc, PageCount = document.Pages.Count
                    }).ToList());
            }
        }

        public Task<NotebookDocument?> LoadAsync(string id)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return Task.FromResult(_documents.TryGetValue(id, out var document) ? Snapshot(document) : null);
            }
        }

        public Task SaveAsync(NotebookDocument document)
        {
            var snapshot = Snapshot(document);
            NotebookStructure.Normalize(snapshot);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _documents[snapshot.Id] = snapshot;
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _documents.Remove(id);
            }
            return Task.CompletedTask;
        }

        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes)
        {
            var copy = bytes.ToArray();
            var id = Convert.ToHexStringLower(SHA256.HashData(copy));
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _assets.TryAdd(id, new AssetData(id, Path.GetFileName(fileName), contentType, copy));
                return Task.FromResult(_assets[id] with { Bytes = _assets[id].Bytes.ToArray() });
            }
        }

        public Task<AssetData> GetAssetAsync(string id)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_assets.TryGetValue(id, out var asset)) throw new FileNotFoundException("This synthetic library does not contain the requested asset.", id);
                return Task.FromResult(asset with { Bytes = asset.Bytes.ToArray() });
            }
        }

        private static NotebookDocument Snapshot(NotebookDocument document)
        {
            var snapshot = document.Snapshot();
            foreach (var page in snapshot.Pages) page.InkData = page.InkData.ToArray();
            return snapshot;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _documents.Clear();
                _assets.Clear();
            }
        }
    }
}
