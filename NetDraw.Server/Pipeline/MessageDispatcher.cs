using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;

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
    private readonly ILogger<MessageDispatcher> _logger;
    private readonly ConcurrentDictionary<ClientHandler, long> _lastRejectReply = new();

    public MessageDispatcher(IRateLimiter rateLimiter, ILogger<MessageDispatcher> logger)
    {
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public void Register(IMessageHandler handler)
    {
        _handlers.Add(handler);
    }

    public void ForgetClient(ClientHandler client) => _lastRejectReply.TryRemove(client, out _);

    public async Task DispatchAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        var type = envelope.Type;
        if (!RateLimitExempt.Contains(type) && !_rateLimiter.TryAcquire(sender))
        {
            var now = Stopwatch.GetTimestamp();
            var prev = _lastRejectReply.GetOrAdd(sender, 0L);
            if (now - prev < RejectReplyCooldownTicks) return;
            _lastRejectReply[sender] = now;

            var err = NetMessage<ErrorPayload>.Create(MessageType.Error, "server", "Server", envelope.RoomId,
                new ErrorPayload { Message = "Rate limit exceeded", Code = ErrorCodes.RateLimited });
            try
            {
                await sender.SendAsync(err);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to send rate-limit reply to {SenderId}: {Error}", envelope.SenderId, ex.Message);
            }
            return;
        }

        var handler = _handlers.FirstOrDefault(h => h.CanHandle(type));
        if (handler != null)
        {
            await handler.HandleAsync(envelope, sender);
        }
        else
        {
            _logger.LogWarning("No handler for message type {MessageType} from {SenderId}", type, envelope.SenderId);
        }
    }
}
