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

    public AiHandler(IRoomService roomService, IMcpClient mcpClient, IAiParser fallbackParser)
    {
        _roomService = roomService;
        _mcpClient = mcpClient;
        _fallbackParser = fallbackParser;
    }

    public bool CanHandle(MessageType type) => type is MessageType.AiCommand;

    public async Task HandleAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender)
    {
        var cmdPayload = MessageEnvelope.DeserializePayload<AiCommandPayload>(payload);
        if (cmdPayload == null) return;

        var prompt = cmdPayload.Prompt;
        AiResultPayload result;

        if (_mcpClient.IsConnected)
        {
            var mcpResult = await _mcpClient.SendCommandAsync(prompt, roomId);
            result = (mcpResult != null && mcpResult.Actions.Count > 0)
                ? mcpResult
                : await FallbackParseAsync(prompt);
        }
        else
        {
            result = await FallbackParseAsync(prompt);
        }

        result.Prompt = prompt;

        if (result.Actions.Count > 0)
            _roomService.GetRoom(roomId)?.AddActions(result.Actions);

        var msg = NetMessage<AiResultPayload>.Create(MessageType.AiResult, "server", "AI", roomId, result);
        await _roomService.BroadcastToRoomAsync(roomId, msg);
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
