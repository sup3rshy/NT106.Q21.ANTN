using System.Collections.Concurrent;
using System.Diagnostics;
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

    private static readonly long RejectReplyCooldownTicks = Stopwatch.Frequency; // 1 s

    private readonly List<IMessageHandler> _handlers = new();
    private readonly IRateLimiter _rateLimiter;
    private readonly ConcurrentDictionary<ClientHandler, long> _lastRejectReply = new();

    public MessageDispatcher(IRateLimiter rateLimiter)
    {
        _rateLimiter = rateLimiter;
    }

    public void Register(IMessageHandler handler)
    {
        _handlers.Add(handler);
    }

    public void ForgetClient(ClientHandler client) => _lastRejectReply.TryRemove(client, out _);

    public async Task DispatchAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        if (!RateLimitExempt.Contains(type) && !_rateLimiter.TryAcquire(sender))
        {
            var now = Stopwatch.GetTimestamp();
            var prev = _lastRejectReply.GetOrAdd(sender, 0L);
            if (now - prev < RejectReplyCooldownTicks) return;
            _lastRejectReply[sender] = now;

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
