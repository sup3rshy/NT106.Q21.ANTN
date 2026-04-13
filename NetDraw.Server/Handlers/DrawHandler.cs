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

    public async Task HandleAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        switch (type)
        {
            case MessageType.Draw:
                var drawPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (drawPayload?.Action == null) return;
                _roomService.GetRoom(roomId)?.AddAction(drawPayload.Action);
                var drawMsg = NetMessage<DrawPayload>.Create(MessageType.Draw, senderId, senderName, roomId, drawPayload);
                await _roomService.BroadcastToRoomAsync(roomId, drawMsg, exclude: sender);
                break;

            case MessageType.DrawPreview:
                var previewPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (previewPayload == null) return;
                var previewMsg = NetMessage<DrawPayload>.Create(MessageType.DrawPreview, senderId, senderName, roomId, previewPayload);
                await _roomService.BroadcastToRoomAsync(roomId, previewMsg, exclude: sender);
                break;

            case MessageType.ClearCanvas:
                _roomService.GetRoom(roomId)?.ClearHistory();
                var clearMsg = NetMessage<SnapshotPayload>.Create(MessageType.ClearCanvas, senderId, senderName, roomId);
                await _roomService.BroadcastToRoomAsync(roomId, clearMsg, exclude: sender);
                break;

            case MessageType.Undo:
                var room = _roomService.GetRoom(roomId);
                room?.RemoveLastActionByUser(senderId);
                var snapshot = NetMessage<SnapshotPayload>.Create(
                    MessageType.CanvasSnapshot, "server", "Server", roomId,
                    new SnapshotPayload { Actions = room?.GetHistory() ?? new() });
                await _roomService.BroadcastToRoomAsync(roomId, snapshot);
                break;

            case MessageType.Redo:
                var redoPayload = MessageEnvelope.DeserializePayload<DrawPayload>(payload);
                if (redoPayload?.Action == null) return;
                _roomService.GetRoom(roomId)?.AddAction(redoPayload.Action);
                var redoMsg = NetMessage<DrawPayload>.Create(MessageType.Draw, senderId, senderName, roomId, redoPayload);
                await _roomService.BroadcastToRoomAsync(roomId, redoMsg);
                break;
        }
    }
}
