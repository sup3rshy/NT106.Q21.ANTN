using System.Collections.Concurrent;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;

namespace NetDraw.Server;

/// <summary>
/// Phòng vẽ - quản lý danh sách user và lịch sử vẽ
/// </summary>
public class Room
{
    public string RoomId { get; }
    public string RoomName { get; }
    public int MaxUsers { get; } = 10;
    public long CreatedAt { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private readonly ConcurrentDictionary<string, ClientHandler> _clients = new();
    private readonly List<DrawAction> _drawHistory = new();
    private readonly object _historyLock = new();

    public int UserCount => _clients.Count;

    public Room(string roomId, string roomName)
    {
        RoomId = roomId;
        RoomName = roomName;
    }

    public bool AddClient(ClientHandler client)
    {
        if (_clients.Count >= MaxUsers) return false;
        return _clients.TryAdd(client.ClientId, client);
    }

    public bool RemoveClient(ClientHandler client)
    {
        return _clients.TryRemove(client.ClientId, out _);
    }

    public IEnumerable<ClientHandler> GetClients() => _clients.Values;

    public void AddDrawAction(DrawAction action)
    {
        lock (_historyLock)
        {
            _drawHistory.Add(action);
            // Giới hạn lịch sử 5000 action
            if (_drawHistory.Count > 5000)
            {
                _drawHistory.RemoveRange(0, 1000);
            }
        }
    }

    public List<DrawAction> GetDrawHistory()
    {
        lock (_historyLock)
        {
            return new List<DrawAction>(_drawHistory);
        }
    }

    public void ClearHistory()
    {
        lock (_historyLock)
        {
            _drawHistory.Clear();
        }
    }

    /// <summary>
    /// Broadcast message đến tất cả client trong phòng
    /// </summary>
    public async Task BroadcastAsync(NetMessage message, string? excludeClientId = null)
    {
        var tasks = _clients.Values
            .Where(c => c.ClientId != excludeClientId)
            .Select(c => c.SendAsync(message));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Broadcast đến tất cả kể cả sender
    /// </summary>
    public async Task BroadcastAllAsync(NetMessage message)
    {
        var tasks = _clients.Values.Select(c => c.SendAsync(message));
        await Task.WhenAll(tasks);
    }

    public RoomInfo ToRoomInfo() => new()
    {
        RoomId = RoomId,
        RoomName = RoomName,
        UserCount = UserCount,
        MaxUsers = MaxUsers,
        CreatedAt = CreatedAt
    };

    public List<UserInfo> GetUserInfoList() => _clients.Values.Select(c => new UserInfo
    {
        UserId = c.ClientId,
        UserName = c.UserName,
        Color = c.UserColor
    }).ToList();
}
