using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Handlers;

public class RoomHandler : IMessageHandler
{
    private readonly IRoomService _roomService;
    private readonly IClientRegistry _clientRegistry;

    public RoomHandler(IRoomService roomService, IClientRegistry clientRegistry)
    {
        _roomService = roomService;
        _clientRegistry = clientRegistry;
    }

    public bool CanHandle(MessageType type) =>
        type is MessageType.JoinRoom or MessageType.LeaveRoom or MessageType.RoomList;

    public async Task HandleAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        switch (type)
        {
            case MessageType.JoinRoom:
                await HandleJoinAsync(senderId, senderName, roomId, sender);
                break;
            case MessageType.LeaveRoom:
                await HandleLeaveAsync(senderId, senderName, sender);
                break;
            case MessageType.RoomList:
                await HandleRoomListAsync(sender);
                break;
        }
    }

    private async Task HandleJoinAsync(string senderId, string senderName, string roomId, ClientHandler sender)
    {
        // If client is already in a room, leave it first so they don't appear in two rooms
        // or duplicate within the same room when re-joining.
        var previousRoomId = _roomService.GetRoomIdForClient(sender);
        if (previousRoomId != null)
        {
            _roomService.RemoveUserFromRoom(sender);
            var leftMsg = NetMessage<UserPayload>.Create(
                MessageType.UserLeft, senderId, sender.UserName, previousRoomId,
                new UserPayload { User = new UserInfo { UserId = senderId, UserName = sender.UserName } });
            await _roomService.BroadcastToRoomAsync(previousRoomId, leftMsg);
        }

        var user = new UserInfo { UserId = senderId, UserName = senderName, Color = sender.UserColor };
        sender.UserId = senderId;
        sender.UserName = senderName;

        var joinResult = _roomService.AddUserToRoom(roomId, sender, user);
        if (joinResult != JoinResult.Ok)
        {
            var reason = joinResult switch
            {
                JoinResult.RoomFull   => $"Room '{roomId}' is full ({_roomService.MaxUsersPerRoom} users max)",
                JoinResult.ServerFull => $"Server is full ({_roomService.MaxRooms} rooms max)",
                _ => "Cannot join room"
            };
            var errorMsg = NetMessage<ErrorPayload>.Create(
                MessageType.Error, "server", "Server", roomId,
                new ErrorPayload { Message = reason });
            await sender.SendAsync(errorMsg);
            return;
        }

        _clientRegistry.Register(senderId, sender);

        var room = _roomService.GetRoom(roomId)!;
        var joinedMsg = NetMessage<RoomJoinedPayload>.Create(
            MessageType.RoomJoined, "server", "Server", roomId,
            new RoomJoinedPayload
            {
                Room = new RoomInfo { RoomId = roomId, RoomName = roomId, UserCount = room.ClientCount },
                History = room.GetHistory(),
                Users = room.GetUsers()
            });
        await sender.SendAsync(joinedMsg);

        var userJoinedMsg = NetMessage<UserPayload>.Create(
            MessageType.UserJoined, senderId, senderName, roomId,
            new UserPayload { User = user });
        await _roomService.BroadcastToRoomAsync(roomId, userJoinedMsg, exclude: sender);
    }

    private async Task HandleLeaveAsync(string senderId, string senderName, ClientHandler sender)
    {
        var roomId = _roomService.GetRoomIdForClient(sender);
        if (roomId == null) return;

        _roomService.RemoveUserFromRoom(sender);
        _clientRegistry.Unregister(senderId);

        var leftMsg = NetMessage<UserPayload>.Create(
            MessageType.UserLeft, senderId, senderName, roomId,
            new UserPayload { User = new UserInfo { UserId = senderId, UserName = senderName } });
        await _roomService.BroadcastToRoomAsync(roomId, leftMsg);
    }

    private async Task HandleRoomListAsync(ClientHandler sender)
    {
        var msg = NetMessage<RoomListPayload>.Create(
            MessageType.RoomList, "server", "Server", "",
            new RoomListPayload { Rooms = _roomService.GetAllRoomInfos() });
        await sender.SendAsync(msg);
    }
}
