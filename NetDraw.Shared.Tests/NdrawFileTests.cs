using System.IO.Compression;
using System.Text;
using NetDraw.Shared.IO;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using Newtonsoft.Json;
using Xunit;

namespace NetDraw.Shared.Tests;

public class NdrawFileTests
{
    private static string NewTempPath()
    {
        var path = Path.GetTempFileName();
        File.Delete(path);
        return path + ".ndraw";
    }

    [Fact]
    public void Save_Then_Load_Returns_Empty_List()
    {
        var path = NewTempPath();
        try
        {
            NdrawFile.Save(path, new List<DrawActionBase>());
            var doc = NdrawFile.Load(path);
            Assert.Equal(NdrawFile.CurrentVersion, doc.Version);
            Assert.Empty(doc.Actions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_Then_Load_Roundtrips_PenAction()
    {
        var path = NewTempPath();
        try
        {
            var pen = new PenAction
            {
                UserId = "u1",
                Color = "#FF8800",
                StrokeWidth = 3.25,
                PenStyle = PenStyle.Calligraphy,
                Points =
                {
                    new PointData(1, 2),
                    new PointData(3.5, 4.5),
                    new PointData(5, 6)
                }
            };

            NdrawFile.Save(path, new List<DrawActionBase> { pen });
            var doc = NdrawFile.Load(path);

            var revived = Assert.IsType<PenAction>(Assert.Single(doc.Actions));
            Assert.Equal(pen.Id, revived.Id);
            Assert.Equal("#FF8800", revived.Color);
            Assert.Equal(3.25, revived.StrokeWidth);
            Assert.Equal(PenStyle.Calligraphy, revived.PenStyle);
            Assert.Equal(3, revived.Points.Count);
            Assert.Equal(3.5, revived.Points[1].X);
            Assert.Equal(4.5, revived.Points[1].Y);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_Then_Load_Roundtrips_Mixed_Types()
    {
        var path = NewTempPath();
        try
        {
            var actions = new List<DrawActionBase>
            {
                new PenAction
                {
                    Color = "#111111",
                    Points = { new PointData(0, 0), new PointData(10, 10) }
                },
                new ShapeAction
                {
                    ShapeType = ShapeType.Rect,
                    X = 5, Y = 6, Width = 100, Height = 50,
                    FillColor = "#00FF00"
                },
                new LineAction
                {
                    StartX = 0, StartY = 0, EndX = 50, EndY = 75,
                    HasArrow = true
                },
                new TextAction
                {
                    Text = "hello",
                    FontSize = 18, X = 1, Y = 2,
                    IsBold = true, IsItalic = true
                },
                new ImageAction
                {
                    ImageData = Convert.ToBase64String(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
                    X = 10, Y = 20, Width = 30, Height = 40
                },
                new EraseAction
                {
                    EraserSize = 25,
                    Points = { new PointData(1, 1), new PointData(2, 2) }
                }
            };

            NdrawFile.Save(path, actions);
            var doc = NdrawFile.Load(path);

            Assert.Equal(6, doc.Actions.Count);

            var pen = Assert.IsType<PenAction>(doc.Actions[0]);
            Assert.Equal(2, pen.Points.Count);
            Assert.Equal("#111111", pen.Color);

            var shape = Assert.IsType<ShapeAction>(doc.Actions[1]);
            Assert.Equal(ShapeType.Rect, shape.ShapeType);
            Assert.Equal(100, shape.Width);
            Assert.Equal("#00FF00", shape.FillColor);

            var line = Assert.IsType<LineAction>(doc.Actions[2]);
            Assert.True(line.HasArrow);
            Assert.Equal(50, line.EndX);
            Assert.Equal(75, line.EndY);

            var text = Assert.IsType<TextAction>(doc.Actions[3]);
            Assert.Equal("hello", text.Text);
            Assert.True(text.IsBold);
            Assert.True(text.IsItalic);

            var image = Assert.IsType<ImageAction>(doc.Actions[4]);
            Assert.Equal(Convert.ToBase64String(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }), image.ImageData);
            Assert.Equal(30, image.Width);

            var erase = Assert.IsType<EraseAction>(doc.Actions[5]);
            Assert.Equal(25, erase.EraserSize);
            Assert.Equal(2, erase.Points.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_Then_Load_Preserves_Vietnamese_Text()
    {
        var path = NewTempPath();
        try
        {
            const string original = "Đây là tin nhắn có dấu — ★ \U0001F44B";
            var text = new TextAction { Text = original, X = 1, Y = 2 };

            NdrawFile.Save(path, new List<DrawActionBase> { text });
            var doc = NdrawFile.Load(path);

            var revived = Assert.IsType<TextAction>(Assert.Single(doc.Actions));
            Assert.Equal(original, revived.Text);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
            var actionsEntry = archive.GetEntry("actions.json")!;
            using var entryStream = actionsEntry.Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            var raw = ms.ToArray();
            Assert.True(IndexOfSequence(raw, new byte[] { 0xC4, 0x90 }) >= 0,
                "actions.json should contain the UTF-8 bytes for 'Đ' (0xC4 0x90)");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_Then_Load_Preserves_CreatedAt_Within_Second()
    {
        var path = NewTempPath();
        try
        {
            var before = DateTimeOffset.UtcNow;
            NdrawFile.Save(path, new List<DrawActionBase>());
            var after = DateTimeOffset.UtcNow;

            var doc = NdrawFile.Load(path);

            var lo = before.AddSeconds(-1);
            var hi = after.AddSeconds(1);
            Assert.InRange(doc.CreatedAt, lo, hi);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_Then_Load_Preserves_UserId_Per_Action()
    {
        var path = NewTempPath();
        try
        {
            var actions = new List<DrawActionBase>
            {
                new PenAction { UserId = "alice", UserName = "Alice",
                    Points = { new PointData(0, 0), new PointData(1, 1) } },
                new ShapeAction { UserId = "bob", UserName = "Bob",
                    ShapeType = ShapeType.Rect, X = 0, Y = 0, Width = 10, Height = 10 },
                new TextAction { UserId = "carol", UserName = "Carol",
                    Text = "hi", X = 1, Y = 2 }
            };

            NdrawFile.Save(path, actions);
            var doc = NdrawFile.Load(path);

            Assert.Equal(3, doc.Actions.Count);
            Assert.IsType<PenAction>(doc.Actions[0]);
            Assert.Equal("alice", doc.Actions[0].UserId);
            Assert.Equal("Alice", doc.Actions[0].UserName);
            Assert.IsType<ShapeAction>(doc.Actions[1]);
            Assert.Equal("bob", doc.Actions[1].UserId);
            Assert.Equal("Bob", doc.Actions[1].UserName);
            Assert.IsType<TextAction>(doc.Actions[2]);
            Assert.Equal("carol", doc.Actions[2].UserId);
            Assert.Equal("Carol", doc.Actions[2].UserName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_On_Missing_File()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ndraw");
        Assert.False(File.Exists(path));
        Assert.Throws<FileNotFoundException>(() => NdrawFile.Load(path));
    }

    [Fact]
    public void Load_Throws_On_Non_Zip_File()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, "this is not a zip file, just plain text");
            var ex = Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
            Assert.Equal("Not a valid .ndraw file", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_On_Missing_Manifest()
    {
        var path = NewTempPath();
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("actions.json");
                using var w = new StreamWriter(entry.Open());
                w.Write("[]");
            }

            var ex = Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
            Assert.Equal("Missing manifest.json", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_On_Missing_Actions()
    {
        var path = NewTempPath();
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("manifest.json");
                using var w = new StreamWriter(entry.Open());
                w.Write(JsonConvert.SerializeObject(new
                {
                    version = NdrawFile.CurrentVersion,
                    createdAt = DateTimeOffset.UtcNow,
                    actionCount = 0
                }));
            }

            var ex = Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
            Assert.Equal("Missing actions.json", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_On_Future_Version()
    {
        var path = NewTempPath();
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry("manifest.json");
                using (var w = new StreamWriter(manifestEntry.Open()))
                {
                    w.Write(JsonConvert.SerializeObject(new
                    {
                        version = 999,
                        createdAt = DateTimeOffset.UtcNow,
                        actionCount = 0
                    }));
                }

                var actionsEntry = archive.CreateEntry("actions.json");
                using (var w = new StreamWriter(actionsEntry.Open()))
                {
                    w.Write("[]");
                }
            }

            var ex = Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
            Assert.Equal("Unsupported .ndraw version: 999", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_On_Missing_Version_Field()
    {
        var path = NewTempPath();
        try
        {
            WriteManifestAndEmptyActions(path, manifestJson:
                "{ \"createdAt\": \"2026-05-03T00:00:00+00:00\", \"actionCount\": 0 }");
            Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Load_Throws_On_Non_Positive_Version(int version)
    {
        var path = NewTempPath();
        try
        {
            WriteManifestAndEmptyActions(path, manifestJson:
                $"{{ \"version\": {version}, \"createdAt\": \"2026-05-03T00:00:00+00:00\", \"actionCount\": 0 }}");
            var ex = Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
            Assert.Equal($"Invalid .ndraw version: {version}", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_On_Null_Actions_Payload()
    {
        var path = NewTempPath();
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var m = archive.CreateEntry("manifest.json");
                using (var w = new StreamWriter(m.Open()))
                    w.Write("{ \"version\": 1, \"createdAt\": \"2026-05-03T00:00:00+00:00\", \"actionCount\": 0 }");
                var a = archive.CreateEntry("actions.json");
                using (var w = new StreamWriter(a.Open())) w.Write("null");
            }

            var ex = Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
            Assert.Contains("null payload", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_On_ActionCount_Mismatch()
    {
        var path = NewTempPath();
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var m = archive.CreateEntry("manifest.json");
                using (var w = new StreamWriter(m.Open()))
                    w.Write("{ \"version\": 1, \"createdAt\": \"2026-05-03T00:00:00+00:00\", \"actionCount\": 5 }");
                var a = archive.CreateEntry("actions.json");
                using (var w = new StreamWriter(a.Open())) w.Write("[]");
            }

            var ex = Assert.Throws<InvalidDataException>(() => NdrawFile.Load(path));
            Assert.Contains("does not match", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteManifestAndEmptyActions(string path, string manifestJson)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        var m = archive.CreateEntry("manifest.json");
        using (var w = new StreamWriter(m.Open())) w.Write(manifestJson);

        var a = archive.CreateEntry("actions.json");
        using (var w = new StreamWriter(a.Open())) w.Write("[]");
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
