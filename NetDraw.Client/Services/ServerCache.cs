using System.IO;
using Newtonsoft.Json;

namespace NetDraw.Client.Services;

public class CachedServerEntry
{
    [JsonProperty("serverId")] public string ServerId { get; set; } = string.Empty;
    [JsonProperty("host")] public string Host { get; set; } = string.Empty;
    [JsonProperty("port")] public int Port { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonProperty("lastSeenUtc")] public DateTimeOffset LastSeenUtc { get; set; }
}

public class CachedServersFile
{
    [JsonProperty("version")] public int Version { get; set; } = 1;
    [JsonProperty("servers")] public List<CachedServerEntry> Servers { get; set; } = new();
}

public class ServerCache
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly Action<string> _log;
    private readonly Dictionary<string, CachedServerEntry> _byId = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CachedServerEntry> All
    {
        get { lock (_lock) return _byId.Values.OrderByDescending(e => e.LastSeenUtc).ToList(); }
    }

    public ServerCache(string? overrideFilePath = null, Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        _filePath = overrideFilePath ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        // GetFolderPath gives the right answer on Windows (%APPDATA% Roaming),
        // Linux ($XDG_CONFIG_HOME or ~/.config) and macOS (~/Library/Application Support).
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "NetDraw", "known_servers.json");
    }

    public void Load()
    {
        lock (_lock)
        {
            _byId.Clear();
            if (!File.Exists(_filePath))
            {
                _log($"[ServerCache] No cache file at {_filePath}");
                return;
            }

            try
            {
                var text = File.ReadAllText(_filePath);
                var parsed = JsonConvert.DeserializeObject<CachedServersFile>(text);
                if (parsed?.Servers != null)
                {
                    foreach (var e in parsed.Servers)
                        if (!string.IsNullOrEmpty(e.ServerId))
                            _byId[e.ServerId] = e;
                }
                _log($"[ServerCache] Loaded {_byId.Count} cached servers from {_filePath}");
            }
            catch (Exception ex)
            {
                // Corrupt cache must never crash the connect picker. Quarantine and keep going
                // so the next save overwrites with valid JSON.
                _log($"[ServerCache] Parse failed: {ex.Message}; quarantining");
                try
                {
                    var bad = _filePath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                    File.Move(_filePath, bad);
                }
                catch { }
            }
        }
    }

    // Called on every new discovery — design's "save on every new beacon (id we haven't seen)"
    // rule keeps the relaunch picker pre-populated without waiting for the next 2s tick.
    public void RecordDiscovery(DiscoveredServer s)
    {
        if (string.IsNullOrEmpty(s.ServerId)) return;
        lock (_lock)
        {
            _byId[s.ServerId] = new CachedServerEntry
            {
                ServerId = s.ServerId,
                Host = s.Host,
                Port = s.Port,
                Name = s.Name,
                AppVersion = s.AppVersion,
                LastSeenUtc = s.LastSeen,
            };
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var file = new CachedServersFile { Version = 1, Servers = _byId.Values.ToList() };
            var json = JsonConvert.SerializeObject(file, Formatting.Indented);

            // Atomic-replace pattern: write tmp + rename so an interrupted save can't leave a
            // half-written file that breaks the next Load().
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath))
                File.Replace(tmp, _filePath, destinationBackupFileName: null);
            else
                File.Move(tmp, _filePath);
        }
        catch (Exception ex)
        {
            _log($"[ServerCache] Save failed: {ex.Message}");
        }
    }
}
