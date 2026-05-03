using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;

namespace NetDraw.Client.Services;

public interface INetworkService
{
    string ClientId { get; }
    bool IsConnected { get; }
    string SessionToken { get; set; }
    Task<bool> ConnectAsync(string host, int port);
    void Disconnect();
    Task SendAsync<T>(NetMessage<T> message) where T : IPayload;

    event Action<MessageType, string, string, string, JObject?>? MessageReceived;
    event Action<string>? Disconnected;
}
