using NetDraw.Shared.Protocol;
using Newtonsoft.Json.Linq;

namespace NetDraw.Server.Pipeline;

public interface IMessageHandler
{
    bool CanHandle(MessageType type);
    Task HandleAsync(MessageType type, string senderId, string senderName, string roomId, JObject? payload, ClientHandler sender);
}
