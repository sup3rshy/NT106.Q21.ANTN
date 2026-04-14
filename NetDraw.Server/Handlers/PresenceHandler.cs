using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Handlers;

public class PresenceHandler : IMessageHandler
{
    private readonly IRoomService _roomService;

    public PresenceHandler(IRoomService roomService) => _roomService = roomService;

    public bool CanHandle(MessageType type) => type is MessageType.CursorMove;

    public async Task HandleAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        var cursorPayload = MessageEnvelope.DeserializePayload<CursorPayload>(payload);
        if (cursorPayload == null) return;
        var msg = NetMessage<CursorPayload>.Create(MessageType.CursorMove, senderId, sender.UserName, roomId, cursorPayload);
        await _roomService.BroadcastToRoomAsync(roomId, msg, exclude: sender);
    }
}
