using Newtonsoft.Json;

namespace NetDraw.Shared.Models;

/// <summary>
/// Abstract base class for all drawing actions.
/// Serves as the root of the typed draw-action hierarchy.
/// The <see cref="Type"/> property is the JSON discriminator for polymorphic deserialization.
/// </summary>
public abstract class DrawActionBase
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("type")]
    public abstract string Type { get; }

    [JsonProperty("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonProperty("groupId", NullValueHandling = NullValueHandling.Ignore)]
    public string? GroupId { get; set; }

    [JsonProperty("color")]
    public string Color { get; set; } = "#000000";

    [JsonProperty("strokeWidth")]
    public double StrokeWidth { get; set; } = 2;

    [JsonProperty("opacity")]
    public double Opacity { get; set; } = 1.0;

    [JsonProperty("dashStyle")]
    public DashStyle DashStyle { get; set; } = DashStyle.Solid;
}
