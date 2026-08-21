using Etikra.Models;

namespace Etikra.Services;

internal sealed record EditorSnapshot(
    LabelDocument Document,
    Guid? SelectedElementId,
    bool DocumentPristine,
    bool DocumentCreatedFromMedia)
{
    public static EditorSnapshot Capture(
        LabelDocument document,
        Guid? selectedElementId,
        bool documentPristine,
        bool documentCreatedFromMedia) =>
        new(
            DocumentService.CreateSnapshot(document),
            selectedElementId,
            documentPristine,
            documentCreatedFromMedia);

    public EditorSnapshot Copy() => Capture(
        Document,
        SelectedElementId,
        DocumentPristine,
        DocumentCreatedFromMedia);
}

internal sealed class EditorHistory(int capacity = 100)
{
    private sealed record HistoryState(long Revision, EditorSnapshot Snapshot);

    private readonly int _capacity = Math.Max(2, capacity);
    private readonly List<HistoryState> _states = [];
    private int _index = -1;
    private long _nextRevision;
    private long? _savedRevision;

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index >= 0 && _index < _states.Count - 1;
    public bool IsDirty => _index >= 0 && (_savedRevision is null || _states[_index].Revision != _savedRevision);
    public int Count => _states.Count;

    public void Reset(EditorSnapshot snapshot, bool markSaved)
    {
        _states.Clear();
        _nextRevision = 1;
        _states.Add(new HistoryState(_nextRevision, snapshot.Copy()));
        _index = 0;
        _savedRevision = markSaved ? _nextRevision : null;
    }

    public void Push(EditorSnapshot snapshot)
    {
        if (_index < _states.Count - 1)
        {
            _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        }

        _nextRevision++;
        _states.Add(new HistoryState(_nextRevision, snapshot.Copy()));
        _index = _states.Count - 1;

        if (_states.Count <= _capacity)
        {
            return;
        }

        var removed = _states[0].Revision;
        _states.RemoveAt(0);
        _index--;
        if (_savedRevision == removed)
        {
            _savedRevision = null;
        }
    }

    public EditorSnapshot? Undo()
    {
        if (!CanUndo)
        {
            return null;
        }

        _index--;
        return _states[_index].Snapshot.Copy();
    }

    public EditorSnapshot? Redo()
    {
        if (!CanRedo)
        {
            return null;
        }

        _index++;
        return _states[_index].Snapshot.Copy();
    }

    public void MarkSaved()
    {
        if (_index >= 0)
        {
            _savedRevision = _states[_index].Revision;
        }
    }

    public void UpdateCurrentSelection(Guid? selectedElementId)
    {
        if (_index < 0)
        {
            return;
        }

        var current = _states[_index];
        var snapshot = current.Snapshot;
        _states[_index] = current with
        {
            Snapshot = new EditorSnapshot(
                snapshot.Document,
                selectedElementId,
                snapshot.DocumentPristine,
                snapshot.DocumentCreatedFromMedia)
        };
    }
}
