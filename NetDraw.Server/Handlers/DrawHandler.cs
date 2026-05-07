using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Handlers;

public class DrawHandler : IMessageHandler
{
    private readonly IRoomService _roomService;

    public DrawHandler(IRoomService roomService) => _roomService = roomService;

    public bool CanHandle(MessageType type) =>
        type is MessageType.Draw or MessageType.DrawPreview
            or MessageType.ClearCanvas or MessageType.Undo or MessageType.Redo;

    public async Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        var senderId = envelope.SenderId;
        var roomId = envelope.RoomId;
        var payload = envelope.RawPayload;

        switch (envelope.Type)
        {
            case MessageType.Draw:
                var drawPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (drawPayload?.Action == null) return;
                _roomService.GetRoom(roomId)?.AddAction(drawPayload.Action);
                var drawMsg = NetMessage<DrawPayload>.Create(MessageType.Draw, senderId, sender.UserName, roomId, drawPayload);
                await _roomService.BroadcastToRoomAsync(roomId, drawMsg, exclude: sender);
                break;

            case MessageType.DrawPreview:
                var previewPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (previewPayload == null) return;
                var previewMsg = NetMessage<DrawPayload>.Create(MessageType.DrawPreview, senderId, sender.UserName, roomId, previewPayload);
                await _roomService.BroadcastToRoomAsync(roomId, previewMsg, exclude: sender);
                break;

            case MessageType.ClearCanvas:
                _roomService.GetRoom(roomId)?.ClearHistory();
                var clearMsg = NetMessage<SnapshotPayload>.Create(MessageType.ClearCanvas, senderId, sender.UserName, roomId);
                await _roomService.BroadcastToRoomAsync(roomId, clearMsg, exclude: sender);
                break;

            case MessageType.Undo:
                // Send a small DeleteObject diff instead of re-broadcasting the entire
                // history snapshot. With MaxHistory=5000 a snapshot can be several MB
                // of JSON × N peers per Undo click — that scaled badly. The client
                // already knows how to apply DeleteObject to remove a stroke locally.
                var undoRoom = _roomService.GetRoom(roomId);
                var removed = undoRoom?.RemoveLastActionByUser(senderId);
                if (removed == null) return;
                var undoMsg = NetMessage<DeleteObjectPayload>.Create(
                    MessageType.DeleteObject, senderId, sender.UserName, roomId,
                    new DeleteObjectPayload { ActionId = removed.Id });
                await _roomService.BroadcastToRoomAsync(roomId, undoMsg);
                break;

            case MessageType.Redo:
                var redoPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (redoPayload?.Action == null) return;
                _roomService.GetRoom(roomId)?.AddAction(redoPayload.Action);
                // Exclude sender so the originating client doesn't double-apply: it has
                // already restored the action locally before sending Redo.
                var redoMsg = NetMessage<DrawPayload>.Create(MessageType.Redo, senderId, sender.UserName, roomId, redoPayload);
                await _roomService.BroadcastToRoomAsync(roomId, redoMsg, exclude: sender);
                break;
        }
    }
}