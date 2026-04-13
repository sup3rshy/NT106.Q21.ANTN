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

    public async Task HandleAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        var chatPayload = MessageEnvelope.DeserializePayload<ChatPayload>(payload);
        if (chatPayload == null) return;
        var msg = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, senderId, senderName, roomId, chatPayload);
        await _roomService.BroadcastToRoomAsync(roomId, msg);
    }
}
