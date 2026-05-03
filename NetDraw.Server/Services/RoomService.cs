using System.Collections.Concurrent;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server.Services;

public class RoomService : IRoomService
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<ClientHandler, string> _clientRooms = new();

    private static readonly string[] Palette =
    {
        "#E74C3C", "#3498DB", "#2ECC71", "#F39C12", "#9B59B6",
        "#1ABC9C", "#E67E22", "#34495E", "#16A085", "#C0392B"
    };
    private const string FallbackColor = "#7F8C8D";

    public int MaxUsersPerRoom { get; }
    public int MaxRooms { get; }

    private readonly object _membershipLock = new();

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
        lock (_membershipLock)
        {
            if (!_rooms.ContainsKey(roomId) && _rooms.Count >= MaxRooms)
                return JoinResult.ServerFull;

            var room = _rooms.GetOrAdd(roomId, id => new Room(id));

            bool isRejoin = room.GetClients().Contains(client) || room.GetUsers().Any(u => u.UserId == user.UserId);
            if (!isRejoin && room.ClientCount >= MaxUsersPerRoom)
                return JoinResult.RoomFull;

            var color = PickColorForRoom(room, user.UserId);
            client.UserColor = color;
            user.Color = color;

            room.AddClient(client, user);
            _clientRooms[client] = roomId;
            return JoinResult.Ok;
        }
    }

    private static string PickColorForRoom(Room room, string joiningUserId)
    {
        var taken = room.GetUsers()
            .Where(u => u.UserId != joiningUserId)
            .Select(u => u.Color)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Palette.FirstOrDefault(c => !taken.Contains(c)) ?? FallbackColor;
    }

    // Resume path: swap a dead handler ref for a live one without re-running color
    // selection (so the resumed user keeps the color peers were already seeing).
    // Caller picks user.Color from the existing room entry; we use it as-is.
    public bool RebindClient(string roomId, ClientHandler newClient, UserInfo user)
    {
        lock (_membershipLock)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return false;

            // Drop _clientRooms mappings for any prior handlers that held this UserId.
            // Room.AddClient below will dedupe their _clients entries by UserId; this
            // keeps the (handler -> roomId) index from leaking dead refs.
            var deadHandlers = room.GetClients()
                .Where(h => !ReferenceEquals(h, newClient) && room.GetUser(h)?.UserId == user.UserId)
                .ToList();
            foreach (var dead in deadHandlers) _clientRooms.TryRemove(dead, out _);

            newClient.UserColor = user.Color;
            room.AddClient(newClient, user);
            _clientRooms[newClient] = roomId;
            return true;
        }
    }

    public void RemoveUserFromRoom(ClientHandler client)
    {
        lock (_membershipLock)
        {
            if (_clientRooms.TryRemove(client, out var roomId))
                GetRoom(roomId)?.RemoveClient(client);
        }
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

        // Server-out envelopes never carry a session token. If a future handler ever
        // copies the inbound envelope's token onto a NetMessage and broadcasts it,
        // every other room member would receive the originator's auth credential in
        // cleartext — exactly the spoof the token system is meant to prevent.
        message.SessionToken = string.Empty;

        var json = message.Serialize();
        var sends = room.GetClients()
            .Where(c => c != exclude)
            .Select(c => c.SendRawAsync(json));
        await Task.WhenAll(sends);
    }
}
