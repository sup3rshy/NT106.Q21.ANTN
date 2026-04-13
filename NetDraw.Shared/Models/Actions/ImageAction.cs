using Newtonsoft.Json;
using NetDraw.Shared.Models;

namespace NetDraw.Shared.Models.Actions;

/// <summary>
/// Image placement action (Base64-encoded).
/// </summary>
public class ImageAction : DrawActionBase
{
    [JsonProperty("type")]
    public override string Type => "image";

    [JsonProperty("imageData")]
    public string ImageData { get; set; } = string.Empty;

    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }

    [JsonProperty("width")]
    public double Width { get; set; }

    [JsonProperty("height")]
    public double Height { get; set; }
}
