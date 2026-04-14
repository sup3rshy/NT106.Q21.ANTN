using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetDraw.Shared.Models;
using Newtonsoft.Json;

namespace NetDraw.Client.Services;

public class FileService : IFileService
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new DrawActionConverter() }
    };

    public void Save(string path, List<DrawActionBase> actions)
    {
        var file = new DrawingFile { Actions = actions };
        var json = JsonConvert.SerializeObject(file, Settings);
        File.WriteAllText(path, json);
    }

    public List<DrawActionBase>? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        var file = JsonConvert.DeserializeObject<DrawingFile>(json, Settings);
        return file?.Actions;
    }

    public void ExportPng(Canvas canvas, string path)
    {
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 1 || h < 1) { w = canvas.Width; h = canvas.Height; }

        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);

        // Render with white background (not the checkered pattern)
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            ctx.DrawRectangle(new VisualBrush(canvas), null, new Rect(0, 0, w, h));
        }
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
