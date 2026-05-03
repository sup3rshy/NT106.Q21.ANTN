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

    public AiHandler(IRoomService roomService, IMcpClient mcpClient, IAiParser fallbackParser, ILogger<AiHandler> logger)
    {
        _roomService = roomService;
        _mcpClient = mcpClient;
        _fallbackParser = fallbackParser;
        _logger = logger;
    }

    public bool CanHandle(MessageType type) => type is MessageType.AiCommand;

    public Task HandleAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        var cmdPayload = MessageEnvelope.DeserializePayload<AiCommandPayload>(payload);
        if (cmdPayload == null) return Task.CompletedTask;

        // *** CRITICAL: run AI work in background so the client message-loop is NOT blocked.
        // Without this, cursor moves / draw strokes / chat from this client all freeze while
        // waiting for the AI response (which can take 10–60 s with Claude API).
        _ = Task.Run(() => ProcessInBackgroundAsync(cmdPayload.Prompt, senderId, roomId));
        return Task.CompletedTask;
    }

    private async Task ProcessInBackgroundAsync(string prompt, string senderId, string roomId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("AI prompt from {SenderId} in {RoomId}: {Prompt} (mcp={McpStatus})",
            senderId, roomId, prompt, _mcpClient.IsConnected ? "connected" : "offline");

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
