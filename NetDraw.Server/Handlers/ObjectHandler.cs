using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Handlers;

public class ObjectHandler : IMessageHandler
{
    private readonly IRoomService _roomService;

    public ObjectHandler(IRoomService roomService) => _roomService = roomService;

    public bool CanHandle(MessageType type) =>
        type is MessageType.MoveObject or MessageType.DeleteObject;

    public async Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        var senderId = envelope.SenderId;
        var roomId = envelope.RoomId;
        var payload = envelope.RawPayload;
        var room = _roomService.GetRoom(roomId);
        if (room == null) return;

        if (envelope.Type == MessageType.MoveObject)
        {
            var movePayload = MessageEnvelope.DeserializePayload<MoveObjectPayload>(payload);
            if (movePayload == null || string.IsNullOrEmpty(movePayload.ActionId)) return;

            // Ownership check: only the user who created the action may move it.
            // Without this any peer can flood MoveObject with random IDs and shove
            // every shape off-canvas.
            var action = room.FindActionById(movePayload.ActionId);
            if (action == null || !string.Equals(action.UserId, senderId, StringComparison.Ordinal))
                return;

            var msg = NetMessage<MoveObjectPayload>.Create(MessageType.MoveObject, senderId, sender.UserName, roomId, movePayload);
            await _roomService.BroadcastToRoomAsync(roomId, msg, exclude: sender);
        }
        else
        {
            var deletePayload = MessageEnvelope.DeserializePayload<DeleteObjectPayload>(payload);
            if (deletePayload == null || string.IsNullOrEmpty(deletePayload.ActionId)) return;

            // Ownership check: only the action's author may delete it. Otherwise any
            // peer can clear another user's strokes one by one.
            var action = room.FindActionById(deletePayload.ActionId);
            if (action == null || !string.Equals(action.UserId, senderId, StringComparison.Ordinal))
                return;

            room.RemoveActionById(deletePayload.ActionId);
            var msg = NetMessage<DeleteObjectPayload>.Create(MessageType.DeleteObject, senderId, sender.UserName, roomId, deletePayload);
            await _roomService.BroadcastToRoomAsync(roomId, msg, exclude: sender);
        }
    }
}
