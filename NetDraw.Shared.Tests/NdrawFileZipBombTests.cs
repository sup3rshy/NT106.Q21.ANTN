using System.IO;
using System.IO.Compression;
using System.Text;
using NetDraw.Shared.IO;
using Xunit;

namespace NetDraw.Shared.Tests;

/// <summary>
/// Verifies the zip-bomb guard in NdrawFile.Load. A hand-crafted .ndraw can advertise a
/// 1 MB compressed entry that decompresses to multiple GB; without the cap, Load would
/// happily allocate gigabytes of memory before any validation kicks in.
/// </summary>
public class NdrawFileZipBombTests
{
    [Fact]
    public void Load_RejectsManifestExceedingCap()
    {
        // 64 KiB cap on manifest; pad to 128 KiB.
        var bigManifest = "{\"version\":1,\"createdAt\":\"2024-01-01T00:00:00Z\",\"actionCount\":0,\"junk\":\""
                          + new string('x', 128 * 1024) + "\"}";
        var path = WriteArchive(bigManifest, "[]");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
            Assert.Contains("manifest.json", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_AcceptsNormalSizedFile()
    {
        var manifest = "{\"version\":1,\"createdAt\":\"2024-01-01T00:00:00Z\",\"actionCount\":0}";
        var path = WriteArchive(manifest, "[]");
        try
        {
            var doc = NdrawFile.Load(path);
            Assert.Equal(1, doc.Version);
            Assert.Empty(doc.Actions);
        }
        finally { File.Delete(path); }
    }

    private static string WriteArchive(string manifestJson, string actionsJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ndraw-test-{Guid.NewGuid():N}.ndraw");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", manifestJson);
            WriteEntry(archive, "actions.json", actionsJson);
        }
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }
}
