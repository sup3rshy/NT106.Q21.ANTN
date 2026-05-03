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

    public async Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        var cursorPayload = MessageEnvelope.DeserializePayload<CursorPayload>(envelope.RawPayload);
        if (cursorPayload == null) return;
        var msg = NetMessage<CursorPayload>.Create(MessageType.CursorMove, envelope.SenderId, sender.UserName, envelope.RoomId, cursorPayload);
        await _roomService.BroadcastToRoomAsync(envelope.RoomId, msg, exclude: sender);
    }
}
