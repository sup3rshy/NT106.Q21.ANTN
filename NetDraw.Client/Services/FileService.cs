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
        var bounds = VisualTreeHelper.GetDescendantBounds(canvas);
        if (bounds.IsEmpty) bounds = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);

        var rtb = new RenderTargetBitmap(
            (int)Math.Max(bounds.Width, 1), (int)Math.Max(bounds.Height, 1),
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(canvas);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
