using NetDraw.Shared.Protocol.Payloads;

namespace NetDraw.Server.Services;

public interface IMcpClient
{
    bool IsConnected { get; }
    Task ConnectAsync();
    Task<AiResultPayload?> SendCommandAsync(string command, string roomId);
}
