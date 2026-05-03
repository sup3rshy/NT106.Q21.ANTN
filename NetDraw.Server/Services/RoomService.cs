using System.Collections.Concurrent;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server.Services;

public class RoomService : IRoomService
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<ClientHandler, string> _clientRooms = new();

    public int MaxUsersPerRoom { get; }
    public int MaxRooms { get; }

    public RoomService(int maxUsersPerRoom = 10, int maxRooms = 100)
    {
        MaxUsersPerRoom = maxUsersPerRoom;
        MaxRooms = maxRooms;
    }

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
            MaxUsers = MaxUsersPerRoom,
            CreatedAt = r.CreatedAt
        }).ToList();

    public JoinResult AddUserToRoom(string roomId, ClientHandler client, UserInfo user)
    {
        if (!_rooms.ContainsKey(roomId) && _rooms.Count >= MaxRooms)
            return JoinResult.ServerFull;

        var room = GetOrCreateRoom(roomId);
        lock (room)
        {
            // Re-join from same client/UserId is allowed (Room.AddClient dedupes); only block
            // genuinely new joiners when the room is at capacity.
            bool isRejoin = room.GetClients().Contains(client) || room.GetUsers().Any(u => u.UserId == user.UserId);
            if (!isRejoin && room.ClientCount >= MaxUsersPerRoom)
                return JoinResult.RoomFull;

            room.AddClient(client, user);
            _clientRooms[client] = roomId;
            return JoinResult.Ok;
        }
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
