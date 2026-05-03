using System.Security.Cryptography;
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
    private readonly ISessionTokenStore _sessionTokenStore;

    public RoomHandler(IRoomService roomService, IClientRegistry clientRegistry, ISessionTokenStore sessionTokenStore)
    {
        _roomService = roomService;
        _clientRegistry = clientRegistry;
        _sessionTokenStore = sessionTokenStore;
    }

    public bool CanHandle(MessageType type) =>
        type is MessageType.JoinRoom or MessageType.LeaveRoom or MessageType.RoomList or MessageType.Resume;

    public async Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        switch (envelope.Type)
        {
            case MessageType.JoinRoom:
                await HandleJoinAsync(envelope.SenderId, envelope.SenderName, envelope.RoomId, sender);
                break;
            case MessageType.LeaveRoom:
                await HandleLeaveAsync(envelope.SenderId, envelope.SenderName, sender);
                break;
            case MessageType.RoomList:
                await HandleRoomListAsync(sender);
                break;
            case MessageType.Resume:
                await HandleResumeAsync(envelope, sender);
                break;
        }
    }

    private async Task HandleJoinAsync(string senderId, string senderName, string roomId, ClientHandler sender)
    {
        // Once a connection has been issued a token under one identity, refuse a later
        // JoinRoom that claims a different senderId on the same TCP — otherwise an
        // already-issued token can be reused under a hijacked identity (JoinRoom is
        // token-exempt by design, so the dispatcher's identity check does not run here).
        if (sender.SessionTokenBytes != null
            && !string.Equals(sender.UserId, senderId, StringComparison.Ordinal))
        {
            var hijackErr = NetMessage<ErrorPayload>.Create(
                MessageType.Error, "server", "Server", roomId,
                new ErrorPayload { Message = "session token missing or invalid", Code = ErrorCodes.AuthTokenMismatch });
            await sender.SendAsync(hijackErr);
            return;
        }

        var previousRoomId = _roomService.GetRoomIdForClient(sender);

        sender.UserId = senderId;
        sender.UserName = senderName;
        // AddUserToRoom mutates user.Color (and sender.UserColor) to a per-room unique value.
        var user = new UserInfo { UserId = senderId, UserName = senderName };

        // Try the new room first. On failure the client stays in their previous room
        // (if any) instead of being orphaned by a premature leave + UserLeft broadcast.
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

        // Joined new room. If we were in a different room before, leave it now.
        // AddUserToRoom already overwrote _clientRooms[sender] to the new id, so we
        // must remove from the previous room directly rather than via RemoveUserFromRoom.
        if (previousRoomId != null && previousRoomId != roomId)
        {
            _roomService.GetRoom(previousRoomId)?.RemoveClient(sender);
            var leftMsg = NetMessage<UserPayload>.Create(
                MessageType.UserLeft, senderId, sender.UserName, previousRoomId,
                new UserPayload { User = new UserInfo { UserId = senderId, UserName = sender.UserName } });
            await _roomService.BroadcastToRoomAsync(previousRoomId, leftMsg);
        }

        _clientRegistry.Register(senderId, sender);

        // Issue the session token at most once per ClientHandler lifetime. A room-switch
        // re-uses the existing token; the field is reference-typed and the dispatcher reads
        // it lock-free, so any rewrite would race with in-flight messages on the same TCP.
        if (sender.SessionTokenBytes is null)
        {
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            sender.SessionTokenBytes = bytes;
            sender.SessionToken = Base64UrlEncoding.Encode(bytes);
            _sessionTokenStore.Issue(sender.SessionToken, bytes, sender, senderId);
        }
        _sessionTokenStore.RecordRoom(sender, roomId);

        var room = _roomService.GetRoom(roomId)!;
        var joinedMsg = NetMessage<RoomJoinedPayload>.Create(
            MessageType.RoomJoined, "server", "Server", roomId,
            new RoomJoinedPayload
            {
                Room = new RoomInfo { RoomId = roomId, RoomName = roomId, UserCount = room.ClientCount },
                History = room.GetHistory(),
                Users = room.GetUsers(),
                SessionToken = sender.SessionToken
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

    private async Task HandleResumeAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        // Resume runs before any token is bound to this connection. The dispatcher
        // exempts MessageType.Resume; the token *is* the credential for this message.
        var payload = MessageEnvelope.DeserializePayload<ResumePayload>(envelope.RawPayload);
        var token = payload?.Token ?? string.Empty;

        if (!_sessionTokenStore.TryClaim(token, sender, out var userId, out var lastRoomId))
        {
            await SendResumeFailedAsync(sender, envelope.RoomId);
            return;
        }

        var roomId = lastRoomId ?? envelope.RoomId;
        var room = _roomService.GetRoom(roomId);
        if (room is null)
        {
            // Grace expired or last room never recorded — fall through to fresh-join.
            await SendResumeFailedAsync(sender, envelope.RoomId);
            return;
        }

        var existing = room.GetUsers().FirstOrDefault(u => u.UserId == userId);
        var userName = existing?.UserName ?? envelope.SenderName;
        var color = existing?.Color ?? sender.UserColor;

        sender.UserId = userId;
        sender.UserName = userName;
        sender.UserColor = color;

        // RebindClient swaps the dead handler ref for the live one without re-running
        // PickColorForRoom — the resumed user keeps the color peers were already seeing.
        var resumedUser = new UserInfo { UserId = userId, UserName = userName, Color = color };
        _roomService.RebindClient(roomId, sender, resumedUser);
        _clientRegistry.Register(userId, sender);
        _sessionTokenStore.RecordRoom(sender, roomId);

        var resumeReply = NetMessage<RoomJoinedPayload>.Create(
            MessageType.ResumeAccepted, "server", "Server", roomId,
            new RoomJoinedPayload
            {
                Room = new RoomInfo { RoomId = roomId, RoomName = roomId, UserCount = room.ClientCount },
                History = room.GetHistory(),
                Users = room.GetUsers(),
                SessionToken = sender.SessionToken
            });
        await sender.SendAsync(resumeReply);

        // Peers see a UserJoined replay. The peer-side AddClient is idempotent on
        // the dead UserId, so this matches "user came back" semantics for the UI.
        var userJoinedMsg = NetMessage<UserPayload>.Create(
            MessageType.UserJoined, userId, userName, roomId,
            new UserPayload { User = new UserInfo { UserId = userId, UserName = userName, Color = color } });
        await _roomService.BroadcastToRoomAsync(roomId, userJoinedMsg, exclude: sender);
    }

    private static async Task SendResumeFailedAsync(ClientHandler sender, string roomId)
    {
        var err = NetMessage<ErrorPayload>.Create(
            MessageType.Error, "server", "Server", roomId,
            new ErrorPayload { Message = "session resume failed", Code = ErrorCodes.AuthResumeFailed });
        await sender.SendAsync(err);
    }
}
