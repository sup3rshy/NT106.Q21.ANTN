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

        if (envelope.Type == MessageType.MoveObject)
        {
            var movePayload = MessageEnvelope.DeserializePayload<MoveObjectPayload>(payload);
            if (movePayload == null) return;
            var msg = NetMessage<MoveObjectPayload>.Create(MessageType.MoveObject, senderId, sender.UserName, roomId, movePayload);
            await _roomService.BroadcastToRoomAsync(roomId, msg, exclude: sender);
        }
        else
        {
            var deletePayload = MessageEnvelope.DeserializePayload<DeleteObjectPayload>(payload);
            if (deletePayload == null) return;
            _roomService.GetRoom(roomId)?.RemoveActionById(deletePayload.ActionId);
            var msg = NetMessage<DeleteObjectPayload>.Create(MessageType.DeleteObject, senderId, sender.UserName, roomId, deletePayload);
            await _roomService.BroadcastToRoomAsync(roomId, msg, exclude: sender);
        }
    }
}
