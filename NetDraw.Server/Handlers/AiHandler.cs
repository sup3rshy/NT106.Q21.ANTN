using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _roomQueues = new();

    public AiHandler(IRoomService roomService, IMcpClient mcpClient, IAiParser fallbackParser)
    {
        _roomService = roomService;
        _mcpClient = mcpClient;
        _fallbackParser = fallbackParser;
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
        try
        {
            if (_roomService.GetRoom(roomId) == null)
            {
                Console.WriteLine($"[AI] reject: room not found: {roomId}");
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
            Console.WriteLine($"[AI] queue error: {ex}");
        }
    }

    private async Task ProcessOneAsync(string prompt, string senderId, string roomId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"[AI] ▶ \"{prompt}\"  sender={senderId}  room={roomId}  mcp={(_mcpClient.IsConnected ? "connected" : "offline")}");

        AiResultPayload result;
        try
        {
            if (_mcpClient.IsConnected)
            {
                Console.WriteLine($"[AI]   → forwarding to MCP server…");
                var mcpResult = await _mcpClient.SendCommandAsync(prompt, roomId);
                Console.WriteLine($"[AI]   ← MCP replied in {sw.ElapsedMilliseconds} ms  actions={mcpResult?.Actions.Count ?? -1}");

                result = (mcpResult != null && mcpResult.Actions.Count > 0)
                    ? mcpResult
                    : await FallbackParseAsync(prompt);

                if (mcpResult == null || mcpResult.Actions.Count == 0)
                    Console.WriteLine($"[AI]   ↩ MCP returned empty — using fallback parser");
            }
            else
            {
                Console.WriteLine($"[AI]   → MCP offline, using fallback parser");
                result = await FallbackParseAsync(prompt);
            }

            result.Prompt = prompt;

            if (result.Actions.Count > 0)
                _roomService.GetRoom(roomId)?.AddActions(result.Actions);

            var msg = NetMessage<AiResultPayload>.Create(MessageType.AiResult, "server", "AI", roomId, result);
            await _roomService.BroadcastToRoomAsync(roomId, msg);

            Console.WriteLine($"[AI] ✔ done in {sw.ElapsedMilliseconds} ms  → {result.Actions.Count} action(s) broadcast");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] ✘ error after {sw.ElapsedMilliseconds} ms: {ex}");
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
