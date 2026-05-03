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
    public const string AuthTokenMismatch        = "AUTH_TOKEN_MISMATCH";
    public const string AuthResumeFailed         = "AUTH_RESUME_FAILED";
    public const string ProtocolVersion          = "PROTOCOL_VERSION";
    public const string RateLimited              = "RATE_LIMITED";
    public const string BinaryNotImplemented     = "BINARY_NOT_IMPLEMENTED";
    public const string BinaryBodyUnderrun       = "BINARY_BODY_UNDERRUN";
    public const string BinaryVersionUnsupported = "BINARY_VERSION_UNSUPPORTED";
    public const string BinaryBadMagic           = "BINARY_BAD_MAGIC";
}
