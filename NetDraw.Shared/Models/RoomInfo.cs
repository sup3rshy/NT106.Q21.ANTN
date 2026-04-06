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

public class CursorData
{
    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }
}

public class MoveObjectPayload
{
    [JsonProperty("actionId")]
    public string ActionId { get; set; } = string.Empty;

    [JsonProperty("deltaX")]
    public double DeltaX { get; set; }

    [JsonProperty("deltaY")]
    public double DeltaY { get; set; }
}

public class DeleteObjectPayload
{
    [JsonProperty("actionId")]
    public string ActionId { get; set; } = string.Empty;
}

/// <summary>
/// File format cho Save/Load bản vẽ
/// </summary>
public class DrawingFile
{
    [JsonProperty("version")]
    public string Version { get; set; } = "1.0";

    [JsonProperty("appName")]
    public string AppName { get; set; } = "NetDraw";

    [JsonProperty("createdAt")]
    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonProperty("canvasWidth")]
    public double CanvasWidth { get; set; } = 800;

    [JsonProperty("canvasHeight")]
    public double CanvasHeight { get; set; } = 600;

    [JsonProperty("actions")]
    public List<DrawAction> Actions { get; set; } = new();
}
