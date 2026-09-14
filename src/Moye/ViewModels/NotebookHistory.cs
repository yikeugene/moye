using Moye.Models;

namespace Moye.ViewModels;

// Snapshots share immutable ink byte arrays; unchanged ink is never copied per action.
public sealed class NotebookHistory
{
    private readonly List<NotebookDocument> _undo = [];
    private readonly List<NotebookDocument> _redo = [];
    private NotebookDocument? _current;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Clear() { _undo.Clear(); _redo.Clear(); _current = null; }
    public void Reset(NotebookDocument document) { _undo.Clear(); _redo.Clear(); _current = document.Snapshot(); }
    public void Record(NotebookDocument document)
    {
        if (_current is not null) _undo.Add(_current);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _current = document.Snapshot(); _redo.Clear();
    }
    public NotebookDocument? Undo()
    {
        if (!CanUndo || _current is null) return null;
        _redo.Add(_current); _current = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); return _current.Snapshot();
    }
    public NotebookDocument? Redo()
    {
        if (!CanRedo || _current is null) return null;
        _undo.Add(_current); _current = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); return _current.Snapshot();
    }
}
