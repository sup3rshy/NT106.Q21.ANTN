using Newtonsoft.Json;

namespace NetDraw.Shared.Protocol.Payloads;

/// <summary>
/// Payload for <see cref="MessageType.Error"/> messages.
/// </summary>
public class ErrorPayload : IPayload
{
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("code")]
    public string Code { get; set; } = string.Empty;
}

public static class ErrorCodes
{
    public const string AuthTokenMismatch = "AUTH_TOKEN_MISMATCH";
    public const string ProtocolVersion   = "PROTOCOL_VERSION";
    public const string RateLimited       = "RATE_LIMITED";
}
