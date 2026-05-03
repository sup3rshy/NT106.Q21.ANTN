using NetDraw.Shared.Protocol;

namespace NetDraw.Server.Pipeline;

public interface IMessageHandler
{
    bool CanHandle(MessageType type);
    Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender);
}
