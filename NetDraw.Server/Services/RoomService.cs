using System.Collections.Concurrent;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server.Services;

public class RoomService : IRoomService
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<ClientHandler, string> _clientRooms = new();

    public Room GetOrCreateRoom(string roomId) =>
        _rooms.GetOrAdd(roomId, id => new Room(id));

    public Room? GetRoom(string roomId)
    {
        _rooms.TryGetValue(roomId, out var room);
        return room;
    }

    public List<RoomInfo> GetAllRoomInfos() =>
        _rooms.Values.Select(r => new RoomInfo
        {
            RoomId = r.RoomId,
            RoomName = r.RoomId,
            UserCount = r.ClientCount,
            MaxUsers = 10,
            CreatedAt = r.CreatedAt
        }).ToList();

    public void AddUserToRoom(string roomId, ClientHandler client, UserInfo user)
    {
        var room = GetOrCreateRoom(roomId);
        room.AddClient(client, user);
        _clientRooms[client] = roomId;
    }

    public void RemoveUserFromRoom(ClientHandler client)
    {
        if (_clientRooms.TryRemove(client, out var roomId))
            GetRoom(roomId)?.RemoveClient(client);
    }

    public string? GetRoomIdForClient(ClientHandler client)
    {
        _clientRooms.TryGetValue(client, out var roomId);
        return roomId;
    }

    public async Task BroadcastToRoomAsync<T>(string roomId, NetMessage<T> message, ClientHandler? exclude = null) where T : IPayload
    {
        var room = GetRoom(roomId);
        if (room == null) return;
        var json = message.Serialize();
        var sends = room.GetClients()
            .Where(c => c != exclude)
            .Select(c => c.SendRawAsync(json));
        await Task.WhenAll(sends);
    }
}
