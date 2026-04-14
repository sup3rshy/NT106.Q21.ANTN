using Newtonsoft.Json;
using NetDraw.Shared.Models;

namespace NetDraw.Shared.Models.Actions;

/// <summary>
/// Text placement action.
/// </summary>
public class TextAction : DrawActionBase
{
    [JsonProperty("type")]
    public override string Type => "text";

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    [JsonProperty("fontSize")]
    public double FontSize { get; set; } = 14;

    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }

    [JsonProperty("fontFamily", NullValueHandling = NullValueHandling.Ignore)]
    public string? FontFamily { get; set; }

    [JsonProperty("isBold")]
    public bool IsBold { get; set; }

    [JsonProperty("isItalic")]
    public bool IsItalic { get; set; }

    [JsonProperty("isUnderline")]
    public bool IsUnderline { get; set; }

    [JsonProperty("isStrikethrough")]
    public bool IsStrikethrough { get; set; }
}
