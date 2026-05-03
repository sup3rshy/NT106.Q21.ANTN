using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

public class ResumePayload : IPayload
{
    [JsonProperty("token")]
    public string Token { get; set; } = string.Empty;
}
