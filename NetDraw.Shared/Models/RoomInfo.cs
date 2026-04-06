using Newtonsoft.Json;

namespace NetDraw.Shared.Models;

public class RoomInfo
{
    [JsonProperty("roomId")]
    public string RoomId { get; set; } = string.Empty;

    [JsonProperty("roomName")]
    public string RoomName { get; set; } = string.Empty;

    [JsonProperty("userCount")]
    public int UserCount { get; set; }

    [JsonProperty("maxUsers")]
    public int MaxUsers { get; set; } = 10;

    [JsonProperty("createdAt")]
    public long CreatedAt { get; set; }
}

public class UserInfo
{
    [JsonProperty("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonProperty("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonProperty("color")]
    public string Color { get; set; } = "#000000";
}

public class ChatMsg
{
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("isSystem")]
    public bool IsSystem { get; set; }
}

public class AiCommandPayload
{
    [JsonProperty("prompt")]
    public string Prompt { get; set; } = string.Empty;
}

public class AiResultPayload
{
    [JsonProperty("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonProperty("actions")]
    public List<DrawAction> Actions { get; set; } = new();
}
