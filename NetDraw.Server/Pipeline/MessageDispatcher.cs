using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Pipeline;

public class MessageDispatcher
{
    private readonly List<IMessageHandler> _handlers = new();
    private readonly IRateLimiter _rateLimiter;

    public MessageDispatcher(IRateLimiter rateLimiter)
    {
        _rateLimiter = rateLimiter;
    }

    public void Register(IMessageHandler handler)
    {
        _handlers.Add(handler);
    }

    public async Task DispatchAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        if (!_rateLimiter.TryAcquire(sender))
        {
            var err = NetMessage<ErrorPayload>.Create(MessageType.Error, "server", "Server", roomId,
                new ErrorPayload { Message = "Rate limit exceeded" });
            await sender.SendAsync(err);
            return;
        }

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
