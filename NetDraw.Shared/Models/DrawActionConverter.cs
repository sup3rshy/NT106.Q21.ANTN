using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NetDraw.Shared.Models.Actions;

namespace NetDraw.Shared.Models;

/// <summary>
/// Custom JSON converter for <see cref="DrawActionBase"/> that handles
/// polymorphic serialization/deserialization based on the "type" discriminator field.
/// </summary>
public class DrawActionConverter : JsonConverter<DrawActionBase>
{
    public override bool CanWrite => true;

    public override DrawActionBase? ReadJson(
        JsonReader reader,
        Type objectType,
        DrawActionBase? existingValue,
        bool hasExistingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var jObject = JObject.Load(reader);
        var typeName = jObject["type"]?.Value<string>();

        DrawActionBase action = typeName switch
        {
            "pen"   => new PenAction(),
            "shape" => new ShapeAction(),
            "line"  => new LineAction(),
            "text"  => new TextAction(),
            "image" => new ImageAction(),
            "erase" => new EraseAction(),
            _       => throw new JsonSerializationException(
                           $"Unknown draw action type: '{typeName}'")
        };

        // Populate the created instance from the JSON object.
        // Use a fresh reader so the serializer can walk all properties.
        using var subReader = jObject.CreateReader();
        serializer.Populate(subReader, action);

        return action;
    }

    public override void WriteJson(
        JsonWriter writer,
        DrawActionBase? value,
        JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        // Serialize with NullValueHandling.Ignore to keep payloads compact.
        var innerSerializer = JsonSerializer.CreateDefault(new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        });

        var jObject = JObject.FromObject(value, innerSerializer);
        jObject.WriteTo(writer);
    }
}
