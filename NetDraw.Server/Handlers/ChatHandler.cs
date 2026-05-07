using System.Text;
using Microsoft.Extensions.Logging;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Handlers;

public class ChatHandler : IMessageHandler
{
    // Cap chat at 8 KiB UTF-8. Without this a peer can send a 10 MB message and the
    // server will fan it out to every client in the room — easy DoS amplifier.
    private const int MaxChatBytes = 8 * 1024;

    private readonly IRoomService _roomService;
    private readonly ILogger<ChatHandler>? _logger;

    public ChatHandler(IRoomService roomService, ILogger<ChatHandler>? logger = null)
    {
        _roomService = roomService;
        _logger = logger;
    }

    public bool CanHandle(MessageType type) => type is MessageType.ChatMessage;

    public async Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        var chatPayload = MessageEnvelope.DeserializePayload<ChatPayload>(envelope.RawPayload);
        if (chatPayload == null) return;

        // Clients must not be able to forge "system" messages — those are formatted with
        // server-style chrome and reserved for AI feedback / system events. Reset the flag.
        chatPayload.IsSystem = false;

        var msg = chatPayload.Message ?? string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(msg);
        if (byteCount > MaxChatBytes)
        {
            _logger?.LogInformation("Chat from {SenderId} dropped: {Bytes} bytes > {Cap} cap",
                envelope.SenderId, byteCount, MaxChatBytes);
            var err = NetMessage<ErrorPayload>.Create(MessageType.Error, "server", "Server", envelope.RoomId,
                new ErrorPayload { Message = $"Chat message too long ({byteCount} > {MaxChatBytes} bytes)" });
            try { await sender.SendAsync(err); } catch { /* peer gone */ }
            return;
        }

        // Strip C0 control characters except TAB / CR / LF — they break terminal logs and can
        // hide injection in some clients. Cheap to do once on the server.
        chatPayload.Message = StripBadControls(msg);

        var broadcast = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, envelope.SenderId, sender.UserName, envelope.RoomId, chatPayload);
        await _roomService.BroadcastToRoomAsync(envelope.RoomId, broadcast);
    }

    private static string StripBadControls(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            // Keep printable + common whitespace.
            if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20) sb.Append(c);
        }
        return sb.ToString();
    }
}
