using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Pipeline;

public class MessageDispatcher
{
    // Lifecycle messages a flooded client must still be able to send so they can
    // back off cleanly without holding a server-side seat hostage.
    private static readonly HashSet<MessageType> RateLimitExempt = new()
    {
        MessageType.LeaveRoom,
    };

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
        if (!RateLimitExempt.Contains(type) && !_rateLimiter.TryAcquire(sender))
        {
            var err = NetMessage<ErrorPayload>.Create(MessageType.Error, "server", "Server", roomId,
                new ErrorPayload { Message = "Rate limit exceeded" });
            try
            {
                await sender.SendAsync(err);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to send rate-limit reply: {ex.Message}");
            }
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
