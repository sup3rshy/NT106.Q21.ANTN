using NetDraw.Shared.Models;

namespace NetDraw.Server;

public class Room
{
    private readonly List<(ClientHandler Client, UserInfo User)> _clients = new();
    private readonly List<DrawActionBase> _history = new();
    private readonly object _lock = new();
    private const int MaxHistory = 5000;

    public string RoomId { get; }
    public long CreatedAt { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    public Room(string roomId) => RoomId = roomId;

    public void AddClient(ClientHandler client, UserInfo user)
    {
        lock (_lock) _clients.Add((client, user));
    }

    public void RemoveClient(ClientHandler client)
    {
        lock (_lock) _clients.RemoveAll(c => c.Client == client);
    }

    public List<ClientHandler> GetClients()
    {
        lock (_lock) return _clients.Select(c => c.Client).ToList();
    }

    public List<UserInfo> GetUsers()
    {
        lock (_lock) return _clients.Select(c => c.User).ToList();
    }

    public UserInfo? GetUser(ClientHandler client)
    {
        lock (_lock) return _clients.FirstOrDefault(c => c.Client == client).User;
    }

    public void AddAction(DrawActionBase action)
    {
        lock (_lock)
        {
            _history.Add(action);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }
    }

    public void AddActions(List<DrawActionBase> actions)
    {
        lock (_lock)
        {
            _history.AddRange(actions);
            if (_history.Count > MaxHistory)
                _history.RemoveRange(0, _history.Count - MaxHistory);
        }
    }

    public List<DrawActionBase> GetHistory()
    {
        lock (_lock) return new List<DrawActionBase>(_history);
    }

    public void ClearHistory()
    {
        lock (_lock) _history.Clear();
    }

    public DrawActionBase? RemoveLastActionByUser(string userId)
    {
        lock (_lock)
        {
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].UserId == userId)
                {
                    var action = _history[i];
                    _history.RemoveAt(i);
                    return action;
                }
            }
            return null;
        }
    }

    public bool RemoveActionById(string actionId)
    {
        lock (_lock) return _history.RemoveAll(a => a.Id == actionId) > 0;
    }

    public DrawActionBase? FindActionById(string actionId)
    {
        lock (_lock) return _history.FirstOrDefault(a => a.Id == actionId);
    }
}
