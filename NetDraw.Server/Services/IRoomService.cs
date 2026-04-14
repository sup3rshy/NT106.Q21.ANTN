using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server.Services;

public interface IRoomService
{
    Room GetOrCreateRoom(string roomId);
    Room? GetRoom(string roomId);
    List<RoomInfo> GetAllRoomInfos();
    void AddUserToRoom(string roomId, ClientHandler client, UserInfo user);
    void RemoveUserFromRoom(ClientHandler client);
    string? GetRoomIdForClient(ClientHandler client);
    Task BroadcastToRoomAsync<T>(string roomId, NetMessage<T> message, ClientHandler? exclude = null) where T : IPayload;
}
