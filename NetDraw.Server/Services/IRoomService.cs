using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server.Services;

public enum JoinResult
{
    Ok,
    RoomFull,
    ServerFull
}

public interface IRoomService
{
    int MaxUsersPerRoom { get; }
    int MaxRooms { get; }

    Room GetOrCreateRoom(string roomId);
    Room? GetRoom(string roomId);
    List<RoomInfo> GetAllRoomInfos();
    JoinResult AddUserToRoom(string roomId, ClientHandler client, UserInfo user);
    bool RebindClient(string roomId, ClientHandler newClient, UserInfo user);
    void RemoveUserFromRoom(ClientHandler client);
    string? GetRoomIdForClient(ClientHandler client);
    Task BroadcastToRoomAsync<T>(string roomId, NetMessage<T> message, ClientHandler? exclude = null) where T : IPayload;
}
