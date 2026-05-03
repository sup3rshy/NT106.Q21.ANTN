using NetDraw.Shared.Models;

namespace NetDraw.Client.Drawing;

public class HistoryManager
{
    private readonly List<DrawActionBase> _allActions = new();
    private readonly Stack<DrawActionBase> _undoStack = new();
    private readonly object _lock = new();

    public event Action<DrawActionBase>? ActionAdded;
    public event Action? ActionCleared;

    public List<DrawActionBase> GetAll()
    {
        lock (_lock) return new List<DrawActionBase>(_allActions);
    }

    public void Add(DrawActionBase action, bool isLocal = false)
    {
        lock (_lock)
        {
            _allActions.Add(action);
            if (isLocal) _undoStack.Clear();
        }
        ActionAdded?.Invoke(action);
    }

    public void AddRange(List<DrawActionBase> actions)
    {
        lock (_lock) _allActions.AddRange(actions);
    }

    public DrawActionBase? Undo(string userId)
    {
        lock (_lock)
        {
            for (int i = _allActions.Count - 1; i >= 0; i--)
            {
                if (_allActions[i].UserId == userId)
                {
                    var action = _allActions[i];
                    _allActions.RemoveAt(i);
                    _undoStack.Push(action);
                    return action;
                }
            }
            return null;
        }
    }

    public DrawActionBase? Redo()
    {
        lock (_lock)
        {
            if (_undoStack.Count == 0) return null;
            var action = _undoStack.Pop();
            _allActions.Add(action);
            return action;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _allActions.Clear();
            _undoStack.Clear();
        }
        ActionCleared?.Invoke();
    }

    public void ReplaceAll(List<DrawActionBase> actions)
    {
        lock (_lock)
        {
            _allActions.Clear();
            _allActions.AddRange(actions);
            _undoStack.Clear();
        }
    }
}
