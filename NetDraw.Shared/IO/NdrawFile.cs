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

    // Zip-bomb guard: a hand-crafted .ndraw can advertise a 1 MB compressed entry that
    // expands to multiple GB. Cap each entry's *decompressed* size and reject anything
    // bigger than the limits below — these are an order of magnitude above any real save.
    private const long MaxManifestBytes = 64 * 1024;          // 64 KiB — manifest is tiny
    private const long MaxActionsBytes = 256L * 1024 * 1024;  // 256 MiB — generous for a 5000-action canvas

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

            // Manifest is tiny — read as string. Actions can be huge — stream-deserialize.
            var manifestJson = ReadEntry(manifestEntry, MaxManifestBytes);

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

            // Stream the actions array directly from the zip stream into a
            // List<DrawActionBase>. Avoids materialising the entire JSON as a string
            // (an N MiB save would otherwise allocate ~3× N: bytes → string → JObject).
            List<DrawActionBase>? actions;
            try
            {
                actions = ReadActionsStreaming(actionsEntry, MaxActionsBytes);
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

    /// <summary>
    /// Stream-deserialize <c>actions.json</c> (a JSON array of <see cref="DrawActionBase"/>)
    /// directly from the zip's decompression stream. The bounded stream wrapper still enforces
    /// the zip-bomb cap; <see cref="JsonTextReader"/> never materialises the whole document.
    /// Returns null if the JSON literal was <c>null</c> — caller distinguishes that from an
    /// empty array (<c>[]</c>) and throws an InvalidDataException with the right message.
    /// </summary>
    private static List<DrawActionBase>? ReadActionsStreaming(ZipArchiveEntry entry, long maxBytes)
    {
        if (entry.Length > maxBytes)
            throw new InvalidDataException(
                $"Zip entry '{entry.FullName}' advertises {entry.Length} bytes; limit is {maxBytes}");

        using var entryStream = entry.Open();
        using var bounded = new BoundedReadStream(entryStream, maxBytes, entry.FullName);
        using var reader = new StreamReader(bounded, Utf8NoBom);
        using var jr = new JsonTextReader(reader);

        var serializer = JsonSerializer.Create(ActionSerializerSettings);
        return serializer.Deserialize<List<DrawActionBase>>(jr);
    }

    private static string ReadEntry(ZipArchiveEntry entry, long maxBytes)
    {
        // ZipArchiveEntry.Length is the advertised uncompressed size — a forged entry can
        // lie. Use it as a cheap pre-flight check, then enforce the same cap by reading
        // through a bounded stream. Both gates need to fire because a malicious zip can
        // either lie about Length (cheap reject up-front fails) or stream out forever.
        if (entry.Length > maxBytes)
            throw new InvalidDataException(
                $"Zip entry '{entry.FullName}' advertises {entry.Length} bytes; limit is {maxBytes}");

        using var entryStream = entry.Open();
        using var bounded = new BoundedReadStream(entryStream, maxBytes, entry.FullName);
        using var reader = new StreamReader(bounded, Utf8NoBom);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> if more than <c>maxBytes</c> are read.
    /// Defends against a zip whose Length header understates the real decompressed size.
    /// </summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private readonly string _entryName;
        private long _read;

        public BoundedReadStream(Stream inner, long maxBytes, string entryName)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            _entryName = entryName;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            _read += n;
            if (_read > _maxBytes)
                throw new InvalidDataException(
                    $"Zip entry '{_entryName}' exceeded {_maxBytes}-byte decompression cap (zip-bomb guard)");
            return n;
        }
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
