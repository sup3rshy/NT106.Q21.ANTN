using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using Newtonsoft.Json;
using Xunit;

namespace NetDraw.Shared.Tests;

public class DrawActionConverterTests
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new DrawActionConverter() }
    };

    private static T RoundTrip<T>(T action) where T : DrawActionBase
    {
        var json = JsonConvert.SerializeObject(action, Settings);
        var revived = JsonConvert.DeserializeObject<DrawActionBase>(json, Settings);
        Assert.IsType<T>(revived);
        return (T)revived!;
    }

    [Fact]
    public void Pen_RoundTrip_PreservesPointsAndStyle()
    {
        var pen = new PenAction
        {
            UserId = "u1",
            Color = "#FF0000",
            StrokeWidth = 4.5,
            PenStyle = PenStyle.Highlighter,
            Points = { new PointData(1, 2), new PointData(3.5, 4.25) }
        };
        var revived = RoundTrip(pen);
        Assert.Equal(pen.Id, revived.Id);
        Assert.Equal(PenStyle.Highlighter, revived.PenStyle);
        Assert.Equal(2, revived.Points.Count);
        Assert.Equal(3.5, revived.Points[1].X);
    }

    [Fact]
    public void Shape_RoundTrip_PreservesShapeTypeAndFill()
    {
        var shape = new ShapeAction
        {
            ShapeType = ShapeType.Ellipse,
            X = 10, Y = 20, Width = 100, Height = 50,
            FillColor = "#00FF00"
        };
        var revived = RoundTrip(shape);
        Assert.Equal(ShapeType.Ellipse, revived.ShapeType);
        Assert.Equal("#00FF00", revived.FillColor);
        Assert.Equal(100, revived.Width);
    }

    [Fact]
    public void Line_RoundTrip_PreservesArrowFlag()
    {
        var line = new LineAction { StartX = 0, StartY = 0, EndX = 50, EndY = 50, HasArrow = true };
        var revived = RoundTrip(line);
        Assert.True(revived.HasArrow);
        Assert.Equal(50, revived.EndX);
    }

    [Fact]
    public void Text_RoundTrip_PreservesUnicodeAndFormatting()
    {
        var text = new TextAction
        {
            Text = "Xin chào — \U0001F44B",
            FontFamily = "Segoe UI",
            FontSize = 18,
            IsBold = true,
            X = 5, Y = 7
        };
        var revived = RoundTrip(text);
        Assert.Equal("Xin chào — \U0001F44B", revived.Text);
        Assert.True(revived.IsBold);
    }

    [Fact]
    public void Image_RoundTrip_PreservesBase64Payload()
    {
        var img = new ImageAction
        {
            ImageData = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 }),
            X = 1, Y = 2, Width = 3, Height = 4
        };
        var revived = RoundTrip(img);
        Assert.Equal(img.ImageData, revived.ImageData);
    }

    [Fact]
    public void Erase_RoundTrip_PreservesPointsAndSize()
    {
        var erase = new EraseAction
        {
            EraserSize = 30,
            Points = { new PointData(1, 1), new PointData(2, 2), new PointData(3, 3) }
        };
        var revived = RoundTrip(erase);
        Assert.Equal(30, revived.EraserSize);
        Assert.Equal(3, revived.Points.Count);
    }

    [Fact]
    public void Unknown_TypeDiscriminator_Throws()
    {
        var json = """{"type":"banana","id":"x"}""";
        Assert.Throws<JsonSerializationException>(() =>
            JsonConvert.DeserializeObject<DrawActionBase>(json, Settings));
    }

    [Fact]
    public void List_OfMixedActions_RoundTripsCorrectly()
    {
        var list = new List<DrawActionBase>
        {
            new PenAction { Color = "#111111" },
            new ShapeAction { ShapeType = ShapeType.Rect },
            new TextAction { Text = "abc" }
        };
        var json = JsonConvert.SerializeObject(list, Settings);
        var revived = JsonConvert.DeserializeObject<List<DrawActionBase>>(json, Settings)!;
        Assert.IsType<PenAction>(revived[0]);
        Assert.IsType<ShapeAction>(revived[1]);
        Assert.IsType<TextAction>(revived[2]);
        Assert.Equal("abc", ((TextAction)revived[2]).Text);
    }
}
