using System.IO.Compression;
using System.Text;
using NetDraw.Shared.Models;
using Newtonsoft.Json;

namespace NetDraw.Shared.IO;

/// <summary>
/// Reader/writer for the .ndraw save-file format.
/// A .ndraw file is a zip archive containing manifest.json and actions.json.
/// </summary>
public static class NdrawFile
{
    public const int CurrentVersion = 1;

    private const string ManifestEntry = "manifest.json";
    private const string ActionsEntry = "actions.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerSettings ActionSerializerSettings = new()
    {
        Converters = { new DrawActionConverter() },
        NullValueHandling = NullValueHandling.Ignore
    };

    public static void Save(string path, IList<DrawActionBase> actions)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(actions);

        var manifest = new NdrawManifest
        {
            Version = CurrentVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            ActionCount = actions.Count
        };

        var manifestJson = JsonConvert.SerializeObject(manifest, Formatting.Indented);
        var actionsJson = JsonConvert.SerializeObject(actions, Formatting.Indented, ActionSerializerSettings);

        var tmp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteEntry(archive, ManifestEntry, manifestJson);
                WriteEntry(archive, ActionsEntry, actionsJson);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* leave */ } }
            throw;
        }
    }

    public static NdrawDocument Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Not a valid .ndraw file", ex);
        }

        using (archive)
        {
            var manifestEntry = archive.GetEntry(ManifestEntry)
                ?? throw new InvalidDataException("Missing manifest.json");
            var actionsEntry = archive.GetEntry(ActionsEntry)
                ?? throw new InvalidDataException("Missing actions.json");

            var manifestJson = ReadEntry(manifestEntry);
            var actionsJson = ReadEntry(actionsEntry);

            NdrawManifest? manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<NdrawManifest>(manifestJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Malformed manifest.json", ex);
            }

            if (manifest is null)
                throw new InvalidDataException("Malformed manifest.json");

            if (manifest.Version < 1)
                throw new InvalidDataException($"Invalid .ndraw version: {manifest.Version}");

            if (manifest.Version > CurrentVersion)
                throw new InvalidDataException($"Unsupported .ndraw version: {manifest.Version}");

            List<DrawActionBase>? actions;
            try
            {
                actions = JsonConvert.DeserializeObject<List<DrawActionBase>>(actionsJson, ActionSerializerSettings);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Malformed actions.json", ex);
            }

            if (actions is null)
                throw new InvalidDataException("Malformed actions.json: null payload");

            if (manifest.ActionCount != actions.Count)
                throw new InvalidDataException(
                    $"Manifest actionCount ({manifest.ActionCount}) does not match actions.json length ({actions.Count})");

            return new NdrawDocument
            {
                Version = manifest.Version,
                CreatedAt = manifest.CreatedAt,
                Actions = actions
            };
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Utf8NoBom);
        writer.Write(contents);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Utf8NoBom);
        return reader.ReadToEnd();
    }
}

public sealed class NdrawDocument
{
    public int Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IList<DrawActionBase> Actions { get; init; } = new List<DrawActionBase>();
}

internal sealed class NdrawManifest
{
    [JsonProperty("version", Required = Required.Always)]
    public int Version { get; set; }

    [JsonProperty("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("actionCount")]
    public int ActionCount { get; set; }
}
