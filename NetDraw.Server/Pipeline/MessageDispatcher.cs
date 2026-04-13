using NetDraw.Shared.Protocol;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Pipeline;

public class MessageDispatcher
{
    private readonly List<IMessageHandler> _handlers = new();

    public void Register(IMessageHandler handler)
    {
        _handlers.Add(handler);
    }

    public async Task DispatchAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        var handler = _handlers.FirstOrDefault(h => h.CanHandle(type));
        if (handler != null)
        {
            await handler.HandleAsync(type, senderId, senderName, roomId, payload, sender);
        }
        else
        {
            Console.WriteLine($"[!] No handler for message type: {type}");
        }
    }
}
