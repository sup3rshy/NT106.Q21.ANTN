using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Handlers;

public class ChatHandler : IMessageHandler
{
    private readonly IRoomService _roomService;

    public ChatHandler(IRoomService roomService) => _roomService = roomService;

    public bool CanHandle(MessageType type) => type is MessageType.ChatMessage;

    public async Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        var chatPayload = MessageEnvelope.DeserializePayload<ChatPayload>(envelope.RawPayload);
        if (chatPayload == null) return;
        var msg = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, envelope.SenderId, sender.UserName, envelope.RoomId, chatPayload);
        await _roomService.BroadcastToRoomAsync(envelope.RoomId, msg);
    }
}
