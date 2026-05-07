using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Interfaces;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Handlers;

public class AiHandler : IMessageHandler
{
    // Per-user cooldown between AI prompts. The Claude API is expensive and a single user
    // can otherwise burn the whole room's quota with rapid-fire prompts. Tuned conservatively;
    // legitimate workflows rarely need more than one prompt every few seconds.
    private static readonly TimeSpan AiPerUserCooldown = TimeSpan.FromSeconds(5);

    // Hard ceiling on how long a single room's AI queue can park a request. If a Claude
    // call hangs and the cancel doesn't propagate, we don't want every subsequent prompt
    // in that room to wedge forever.
    private static readonly TimeSpan AiQueueWaitTimeout = TimeSpan.FromMinutes(2);

    private readonly IRoomService _roomService;
    private readonly IMcpClient _mcpClient;
    private readonly IAiParser _fallbackParser;
    private readonly ILogger<AiHandler> _logger;
    private readonly int _maxPromptBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _roomQueues = new();
    private readonly ConcurrentDictionary<string, long> _lastPromptUtcMs = new();

    public AiHandler(IRoomService roomService, IMcpClient mcpClient, IAiParser fallbackParser, ILogger<AiHandler> logger, int maxPromptBytes = 4096)
    {
        _roomService = roomService;
        _mcpClient = mcpClient;
        _fallbackParser = fallbackParser;
        _logger = logger;
        _maxPromptBytes = maxPromptBytes;
    }

    public bool CanHandle(MessageType type) => type is MessageType.AiCommand;

    public async Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
    {
        var cmdPayload = MessageEnvelope.DeserializePayload<AiCommandPayload>(envelope.RawPayload);
        if (cmdPayload == null) return;

        var prompt = cmdPayload.Prompt ?? string.Empty;
        var promptBytes = Encoding.UTF8.GetByteCount(prompt);
        if (promptBytes > _maxPromptBytes)
        {
            var err = NetMessage<ErrorPayload>.Create(MessageType.Error, "server", "Server", envelope.RoomId,
                new ErrorPayload { Message = $"AI prompt too long ({promptBytes} > {_maxPromptBytes} bytes)" });
            await sender.SendAsync(err);
            return;
        }

        // Per-user cooldown — guards the Claude API quota against a single peer flooding
        // prompts. The token bucket already throttles message *frequency*, but AI calls
        // are special-cased because each one costs real $ regardless of TCP bandwidth.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var prevMs = _lastPromptUtcMs.GetOrAdd(envelope.SenderId, 0L);
        if (nowMs - prevMs < (long)AiPerUserCooldown.TotalMilliseconds)
        {
            var waitMs = (long)AiPerUserCooldown.TotalMilliseconds - (nowMs - prevMs);
            var err = NetMessage<ErrorPayload>.Create(MessageType.Error, "server", "Server", envelope.RoomId,
                new ErrorPayload { Message = $"AI cooldown — wait {waitMs / 1000.0:0.0}s before sending another prompt", Code = ErrorCodes.RateLimited });
            await sender.SendAsync(err);
            return;
        }
        _lastPromptUtcMs[envelope.SenderId] = nowMs;

        // *** CRITICAL: run AI work in background so the client message-loop is NOT blocked.
        // Without this, cursor moves / draw strokes / chat from this client all freeze while
        // waiting for the AI response (which can take 10–60 s with Claude API).
        _ = Task.Run(() => ProcessInBackgroundAsync(prompt, envelope.SenderId, envelope.RoomId));
    }

    private async Task ProcessInBackgroundAsync(string prompt, string senderId, string roomId)
    {
        try
        {
            if (_roomService.GetRoom(roomId) == null)
            {
                _logger.LogWarning("AI reject: room not found: {RoomId}", LogHelper.SanitizeForLog(roomId, 80));
                return;
            }

            SemaphoreSlim queue = _roomQueues.GetOrAdd(roomId, _ => new SemaphoreSlim(1, 1));
            bool acquired = false;
            try
            {
                // Bounded wait: if a previous Claude call hangs and its cancel didn't fire,
                // the room's queue would otherwise wedge forever and every subsequent prompt
                // would silently sit here. After the timeout, give up and tell the user.
                acquired = await queue.WaitAsync(AiQueueWaitTimeout);
                if (!acquired)
                {
                    _logger.LogWarning("AI queue wait timed out in room {RoomId} after {Seconds}s",
                        LogHelper.SanitizeForLog(roomId, 80), AiQueueWaitTimeout.TotalSeconds);
                    return;
                }
                await ProcessOneAsync(prompt, senderId, roomId);
            }
            finally
            {
                if (acquired) queue.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI queue error in room {RoomId}", LogHelper.SanitizeForLog(roomId, 80));
        }
    }

    private async Task ProcessOneAsync(string prompt, string senderId, string roomId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("AI prompt from {SenderId} in {RoomId} ({PromptLength} chars, mcp={McpStatus})",
            senderId, roomId, prompt.Length, _mcpClient.IsConnected ? "connected" : "offline");
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("AI prompt body: {Prompt}", LogHelper.SanitizeForLog(ScrubSecrets(prompt)));

        AiResultPayload result;
        try
        {
            if (_mcpClient.IsConnected)
            {
                _logger.LogDebug("Forwarding to MCP server");
                var mcpResult = await _mcpClient.SendCommandAsync(prompt, roomId);
                _logger.LogDebug("MCP replied in {ElapsedMs} ms, actions={ActionCount}",
                    sw.ElapsedMilliseconds, mcpResult?.Actions.Count ?? -1);

                result = (mcpResult != null && mcpResult.Actions.Count > 0)
                    ? mcpResult
                    : await FallbackParseAsync(prompt);

                if (mcpResult == null || mcpResult.Actions.Count == 0)
                    _logger.LogInformation("MCP returned empty — using fallback parser");
            }
            else
            {
                _logger.LogInformation("MCP offline, using fallback parser");
                result = await FallbackParseAsync(prompt);
            }

            result.Prompt = prompt;

            if (result.Actions.Count > 0)
                _roomService.GetRoom(roomId)?.AddActions(result.Actions);

            var msg = NetMessage<AiResultPayload>.Create(MessageType.AiResult, "server", "AI", roomId, result);
            await _roomService.BroadcastToRoomAsync(roomId, msg);

            _logger.LogInformation("AI done in {ElapsedMs} ms, broadcast {ActionCount} action(s)",
                sw.ElapsedMilliseconds, result.Actions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI processing failed after {ElapsedMs} ms", sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Best-effort scrub of obvious secret patterns (Anthropic / OpenAI / GitHub keys,
    /// long base64url-ish blobs that look like session tokens) before a prompt is logged
    /// at Debug level. Conservative: better to over-redact in logs than to leak a key.
    /// </summary>
    private static string ScrubSecrets(string prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return prompt;
        // Anthropic keys start with sk-ant- and are long base64url; OpenAI keys are sk-...
        prompt = System.Text.RegularExpressions.Regex.Replace(prompt, @"sk-[A-Za-z0-9_\-]{20,}", "sk-***REDACTED***");
        // GitHub fine-grained / classic personal access tokens.
        prompt = System.Text.RegularExpressions.Regex.Replace(prompt, @"gh[pousr]_[A-Za-z0-9]{20,}", "gh*_***REDACTED***");
        // Long base64url chunks — likely session tokens. ≥ 40 chars to avoid eating ordinary words.
        prompt = System.Text.RegularExpressions.Regex.Replace(prompt, @"[A-Za-z0-9_\-]{40,}", "***LONG_TOKEN_REDACTED***");
        return prompt;
    }

    private async Task<AiResultPayload> FallbackParseAsync(string prompt)
    {
        try
        {
            var actions = await _fallbackParser.ParseAsync(prompt);
            return new AiResultPayload { Actions = actions };
        }
        catch (Exception ex)
        {
            return new AiResultPayload { Error = ex.Message };
        }
    }
}
