namespace VideoSplitter.Editing;

public sealed class EditHistory<T>
{
    public const int DefaultMaxSnapshots = 200;

    private readonly int _maxSnapshots;
    private readonly LinkedList<T> _undoHistory = new();
    private readonly Stack<T> _redoStack = new();

    public EditHistory(int maxSnapshots = DefaultMaxSnapshots)
    {
        _maxSnapshots = Math.Max(1, maxSnapshots);
    }

    public bool CanUndo => _undoHistory.Count > 1;
    public bool CanRedo => _redoStack.Count > 0;

    public void Push(T snapshot)
    {
        _redoStack.Clear();
        _undoHistory.AddLast(snapshot);

        while (_undoHistory.Count > _maxSnapshots + 1)
        {
            _undoHistory.RemoveFirst();
        }
    }

    public T Undo()
    {
        if (!CanUndo)
        {
            throw new InvalidOperationException("Aucune action a annuler.");
        }

        T current = _undoHistory.Last!.Value;
        _undoHistory.RemoveLast();
        _redoStack.Push(current);

        return _undoHistory.Last!.Value;
    }

    public T Redo()
    {
        if (!CanRedo)
        {
            throw new InvalidOperationException("Aucune action a retablir.");
        }

        T restored = _redoStack.Pop();
        _undoHistory.AddLast(restored);
        return restored;
    }

    public void Clear()
    {
        _undoHistory.Clear();
        _redoStack.Clear();
    }
}
