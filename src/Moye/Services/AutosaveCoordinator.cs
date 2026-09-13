using Moye.Models;

namespace Moye.Services;

/// <summary>Coalesces edits without postponing a continuously edited document indefinitely.</summary>
public sealed class AutosaveCoordinator : IDisposable
{
    private sealed record Pending(long Revision, NotebookDocument Document);
    private readonly INotebookRepository _repository;
    private readonly SynchronizationContext? _context;
    private readonly TimeSpan _delay;
    private readonly object _sync = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Timer _timer;
    private long _revision;
    private DateTimeOffset? _firstPending;
    private bool _disposed;
    private bool _isSaving;
    private Exception? _lastError;

    public AutosaveCoordinator(INotebookRepository repository, TimeSpan? delay = null)
    {
        _repository = repository;
        _context = SynchronizationContext.Current;
        _delay = delay ?? TimeSpan.FromMilliseconds(750);
        if (_delay <= TimeSpan.Zero || _delay > TimeSpan.FromSeconds(2)) throw new ArgumentOutOfRangeException(nameof(delay));
        _timer = new Timer(_ => _ = SaveFromTimerAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? StateChanged;
    public bool IsDirty { get { lock (_sync) return _pending.Count > 0; } }
    public bool IsSaving { get { lock (_sync) return _isSaving; } }
    public Exception? LastError { get { lock (_sync) return _lastError; } }
    public IReadOnlyList<NotebookDocument> PendingDocuments
    {
        get { lock (_sync) return _pending.Values.Select(p => Clone(p.Document)).ToList(); }
    }

    public void Schedule(NotebookDocument document)
    {
        var snapshot = Clone(document);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending[document.Id] = new Pending(++_revision, snapshot);
            _firstPending ??= DateTimeOffset.UtcNow;
            var remaining = TimeSpan.FromSeconds(2) - (DateTimeOffset.UtcNow - _firstPending.Value);
            var due = remaining < _delay ? remaining : _delay;
            _timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        Notify();
    }

    /// <summary>Completes all pending revisions or throws while retaining every unsaved revision.</summary>
    public async Task FlushAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _isSaving = _pending.Count > 0;
            }
            Notify();
            while (true)
            {
                Pending? next;
                lock (_sync) next = _pending.Values.OrderBy(p => p.Revision).FirstOrDefault();
                if (next is null) break;
                try { await _repository.SaveAsync(next.Document).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    lock (_sync) _lastError = exception;
                    throw;
                }
                lock (_sync)
                {
                    if (_pending.TryGetValue(next.Document.Id, out var current) && current.Revision == next.Revision)
                        _pending.Remove(next.Document.Id);
                    _lastError = null;
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _isSaving = false;
                if (_pending.Count == 0) _firstPending = null;
            }
            _saveGate.Release();
            Notify();
        }
    }

    public Task RetryAsync() => FlushAsync();

    private async Task SaveFromTimerAsync()
    {
        try { await FlushAsync().ConfigureAwait(false); }
        catch { /* Failure remains available in LastError and PendingDocuments until retry. */ }
    }

    private static NotebookDocument Clone(NotebookDocument document)
    {
        var snapshot = document.Snapshot();
        foreach (var page in snapshot.Pages) page.InkData = page.InkData.ToArray();
        return snapshot;
    }

    private void Notify()
    {
        if (_context is null) StateChanged?.Invoke(this, EventArgs.Empty);
        else _context.Post(_ => { if (!_disposed) StateChanged?.Invoke(this, EventArgs.Empty); }, null);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
        // The owner calls FlushAsync before disposal. Uncommitted snapshots remain accessible.
    }
}
