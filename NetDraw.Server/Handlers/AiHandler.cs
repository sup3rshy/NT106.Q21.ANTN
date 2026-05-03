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
    private readonly IRoomService _roomService;
    private readonly IMcpClient _mcpClient;
    private readonly IAiParser _fallbackParser;
    private readonly ILogger<AiHandler> _logger;
    private readonly int _maxPromptBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _roomQueues = new();

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
                await queue.WaitAsync();
                acquired = true;
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
            _logger.LogDebug("AI prompt body: {Prompt}", LogHelper.SanitizeForLog(prompt));

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
