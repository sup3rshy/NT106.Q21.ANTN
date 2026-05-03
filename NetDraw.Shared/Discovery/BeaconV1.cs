using Newtonsoft.Json;

namespace NetDraw.Shared.Discovery;

// On-the-wire shape of one UDP discovery beacon. JsonProperty names are the actual
// wire keys — they match docs/design/lan-discovery-and-server-cache.md and must not
// drift; the client decodes by these exact names.
public class BeaconV1
{
    [JsonProperty("v")]
    public byte Version { get; set; } = 1;

    [JsonProperty("id")]
    public string ServerId { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("port")]
    public ushort Port { get; set; }

    [JsonProperty("appVersion")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonProperty("rooms")]
    public ushort Rooms { get; set; }

    [JsonProperty("clients")]
    public ushort Clients { get; set; }

    [JsonProperty("maxClients")]
    public ushort MaxClients { get; set; }

    [JsonProperty("ts")]
    public long UnixSeconds { get; set; }
}
