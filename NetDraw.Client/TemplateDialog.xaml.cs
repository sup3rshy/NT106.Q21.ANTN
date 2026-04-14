using System.Windows;
using System.Windows.Input;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;

namespace NetDraw.Client;

public partial class TemplateDialog : Window
{
    public List<DrawActionBase>? SelectedActions { get; private set; }

    public TemplateDialog()
    {
        InitializeComponent();
        LoadTemplates();
    }

    private void LoadTemplates()
    {
        LstTemplates.ItemsSource = new[]
        {
            new TemplateItem("Grid", "📐", "Lưới ô vuông", CreateGrid),
            new TemplateItem("Ruled Lines", "📝", "Giấy kẻ dòng", CreateRuledLines),
            new TemplateItem("Dot Grid", "⬡", "Lưới chấm", CreateDotGrid),
            new TemplateItem("Coordinate", "📊", "Hệ tọa độ XY", CreateCoordinate),
            new TemplateItem("Storyboard", "🎬", "Khung storyboard 6 ô", CreateStoryboard),
            new TemplateItem("Wireframe", "🖥", "Wireframe giao diện", CreateWireframe),
            new TemplateItem("Flowchart", "🔀", "Khung flowchart", CreateFlowchartTemplate),
            new TemplateItem("Music Sheet", "🎵", "Khuông nhạc", CreateMusicSheet),
            new TemplateItem("Calendar", "📅", "Lịch tháng trống", CreateCalendar),
            new TemplateItem("Comic", "💬", "Khung truyện tranh", CreateComic),
        };
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (LstTemplates.SelectedItem is TemplateItem item)
        {
            SelectedActions = item.Generator();
            DialogResult = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void LstTemplates_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        BtnOk_Click(sender, e);
    }

    #region Template Generators

    private static List<DrawActionBase> CreateGrid()
    {
        var actions = new List<DrawActionBase>();
        double spacing = 40;
        for (double x = 0; x <= 1600; x += spacing)
            actions.Add(MakeLine(x, 0, x, 1200, "#D0D0D0", 0.5));
        for (double y = 0; y <= 1200; y += spacing)
            actions.Add(MakeLine(0, y, 1600, y, "#D0D0D0", 0.5));
        return actions;
    }

    private static List<DrawActionBase> CreateRuledLines()
    {
        var actions = new List<DrawActionBase>();
        // Margin line
        actions.Add(MakeLine(80, 0, 80, 1200, "#FFB3BA", 1));
        // Horizontal lines
        for (double y = 40; y <= 1200; y += 30)
            actions.Add(MakeLine(0, y, 1600, y, "#B0C4DE", 0.5));
        return actions;
    }

    private static List<DrawActionBase> CreateDotGrid()
    {
        var actions = new List<DrawActionBase>();
        double spacing = 30;
        for (double y = spacing; y < 1200; y += spacing)
            for (double x = spacing; x < 1600; x += spacing)
            {
                actions.Add(new ShapeAction
                {
                    ShapeType = ShapeType.Ellipse,
                    X = x - 1.5, Y = y - 1.5, Width = 3, Height = 3,
                    Color = "#B0B0B0", FillColor = "#B0B0B0", StrokeWidth = 0.5
                });
            }
        return actions;
    }

    private static List<DrawActionBase> CreateCoordinate()
    {
        var actions = new List<DrawActionBase>();
        double cx = 800, cy = 600;
        // Axes
        actions.Add(MakeArrowLine(40, cy, 1560, cy, "#333333", 2)); // X axis
        actions.Add(MakeArrowLine(cx, 1160, cx, 40, "#333333", 2)); // Y axis
        // Grid lines
        for (double x = cx - 700; x <= cx + 700; x += 100)
            if (Math.Abs(x - cx) > 1)
                actions.Add(MakeLine(x, cy - 5, x, cy + 5, "#666666", 1));
        for (double y = cy - 500; y <= cy + 500; y += 100)
            if (Math.Abs(y - cy) > 1)
                actions.Add(MakeLine(cx - 5, y, cx + 5, y, "#666666", 1));
        // Labels
        actions.Add(MakeText("X", 1540, cy + 15, "#333333", 16));
        actions.Add(MakeText("Y", cx + 15, 40, "#333333", 16));
        actions.Add(MakeText("O", cx + 8, cy + 8, "#333333", 14));
        return actions;
    }

    private static List<DrawActionBase> CreateStoryboard()
    {
        var actions = new List<DrawActionBase>();
        double margin = 40, gap = 20;
        double cellW = (1600 - 2 * margin - gap) / 2;
        double cellH = (1200 - 2 * margin - 2 * gap) / 3;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 2; col++)
            {
                double x = margin + col * (cellW + gap);
                double y = margin + row * (cellH + gap);
                actions.Add(MakeRect(x, y, cellW, cellH, "#555555", 1.5));
                actions.Add(MakeText($"Scene {row * 2 + col + 1}", x + 10, y + 10, "#999999", 14));
            }
        return actions;
    }

    private static List<DrawActionBase> CreateWireframe()
    {
        var actions = new List<DrawActionBase>();
        // Browser chrome
        actions.Add(MakeRect(100, 50, 1400, 60, "#CCCCCC", 1.5));
        actions.Add(MakeRect(110, 65, 900, 30, "#E0E0E0", 1, "#F5F5F5"));
        // Navbar
        actions.Add(MakeRect(100, 110, 1400, 50, "#E8E8E8", 1, "#F0F0F0"));
        actions.Add(MakeText("Logo", 130, 122, "#888888", 18));
        actions.Add(MakeText("Menu 1    Menu 2    Menu 3    Menu 4", 400, 125, "#AAAAAA", 14));
        // Hero
        actions.Add(MakeRect(100, 160, 1400, 300, "#D0D0D0", 1));
        actions.Add(MakeText("Hero Image / Banner", 550, 290, "#888888", 22));
        // Content grid
        for (int i = 0; i < 3; i++)
        {
            double x = 100 + i * 480;
            actions.Add(MakeRect(x, 490, 440, 250, "#E0E0E0", 1));
            actions.Add(MakeRect(x + 20, 510, 400, 120, "#D0D0D0", 0.5));
            actions.Add(MakeText("Card Title", x + 20, 650, "#888888", 14));
            actions.Add(MakeText("Description text here...", x + 20, 670, "#BBBBBB", 11));
        }
        // Footer
        actions.Add(MakeRect(100, 780, 1400, 60, "#CCCCCC", 1, "#E8E8E8"));
        actions.Add(MakeText("Footer", 750, 800, "#AAAAAA", 14));
        return actions;
    }

    private static List<DrawActionBase> CreateFlowchartTemplate()
    {
        var actions = new List<DrawActionBase>();
        // Start oval
        actions.Add(new ShapeAction { ShapeType = ShapeType.Ellipse, X = 700, Y = 50, Width = 200, Height = 60, Color = "#2196F3", StrokeWidth = 2 });
        actions.Add(MakeText("Start", 770, 65, "#2196F3", 16));
        // Arrow down
        actions.Add(MakeArrowLine(800, 110, 800, 170, "#555555", 1.5));
        // Process box
        actions.Add(MakeRect(650, 170, 300, 70, "#4CAF50", 2));
        actions.Add(MakeText("Process 1", 750, 192, "#4CAF50", 14));
        // Arrow down
        actions.Add(MakeArrowLine(800, 240, 800, 300, "#555555", 1.5));
        // Decision diamond (using lines)
        actions.Add(MakeLine(800, 300, 920, 370, "#FF9800", 2));
        actions.Add(MakeLine(920, 370, 800, 440, "#FF9800", 2));
        actions.Add(MakeLine(800, 440, 680, 370, "#FF9800", 2));
        actions.Add(MakeLine(680, 370, 800, 300, "#FF9800", 2));
        actions.Add(MakeText("Decision?", 748, 358, "#FF9800", 14));
        // Yes arrow
        actions.Add(MakeArrowLine(800, 440, 800, 510, "#555555", 1.5));
        actions.Add(MakeText("Yes", 808, 465, "#4CAF50", 12));
        // No arrow
        actions.Add(MakeArrowLine(920, 370, 1050, 370, "#555555", 1.5));
        actions.Add(MakeText("No", 970, 350, "#F44336", 12));
        // Process 2
        actions.Add(MakeRect(650, 510, 300, 70, "#4CAF50", 2));
        actions.Add(MakeText("Process 2", 750, 532, "#4CAF50", 14));
        // End
        actions.Add(MakeArrowLine(800, 580, 800, 640, "#555555", 1.5));
        actions.Add(new ShapeAction { ShapeType = ShapeType.Ellipse, X = 700, Y = 640, Width = 200, Height = 60, Color = "#F44336", StrokeWidth = 2 });
        actions.Add(MakeText("End", 778, 655, "#F44336", 16));
        return actions;
    }

    private static List<DrawActionBase> CreateMusicSheet()
    {
        var actions = new List<DrawActionBase>();
        for (int staff = 0; staff < 6; staff++)
        {
            double baseY = 80 + staff * 180;
            for (int line = 0; line < 5; line++)
            {
                double y = baseY + line * 12;
                actions.Add(MakeLine(80, y, 1520, y, "#333333", 0.8));
            }
            // Barlines
            actions.Add(MakeLine(80, baseY, 80, baseY + 48, "#333333", 1.5));
            actions.Add(MakeLine(1520, baseY, 1520, baseY + 48, "#333333", 1.5));
            for (double x = 440; x < 1520; x += 360)
                actions.Add(MakeLine(x, baseY, x, baseY + 48, "#555555", 0.8));
        }
        return actions;
    }

    private static List<DrawActionBase> CreateCalendar()
    {
        var actions = new List<DrawActionBase>();
        double startX = 150, startY = 100, cellW = 180, cellH = 130;
        string[] days = { "CN", "T2", "T3", "T4", "T5", "T6", "T7" };

        // Title
        actions.Add(MakeText("THÁNG _ / ____", 600, 40, "#333333", 28));

        // Day headers
        for (int d = 0; d < 7; d++)
        {
            double x = startX + d * cellW;
            actions.Add(MakeRect(x, startY, cellW, 40, "#89B4FA", 1, "#89B4FA"));
            actions.Add(MakeText(days[d], x + cellW / 2 - 10, startY + 10, "#1E1E2E", 16));
        }

        // Grid cells
        for (int row = 0; row < 5; row++)
            for (int col = 0; col < 7; col++)
            {
                double x = startX + col * cellW;
                double y = startY + 40 + row * cellH;
                actions.Add(MakeRect(x, y, cellW, cellH, "#CCCCCC", 0.5));
            }
        return actions;
    }

    private static List<DrawActionBase> CreateComic()
    {
        var actions = new List<DrawActionBase>();
        double margin = 30;

        // Row 1: 2 panels
        actions.Add(MakeRect(margin, margin, 770, 370, "#222222", 3));
        actions.Add(MakeRect(margin + 790, margin, 770, 370, "#222222", 3));

        // Row 2: 3 panels
        double pw = (1600 - 2 * margin - 40) / 3;
        for (int i = 0; i < 3; i++)
            actions.Add(MakeRect(margin + i * (pw + 20), 420, pw, 340, "#222222", 3));

        // Row 3: 1 wide panel
        actions.Add(MakeRect(margin, 780, 1600 - 2 * margin, 370, "#222222", 3));

        return actions;
    }

    #endregion

    #region Helper Methods

    private static LineAction MakeLine(double x1, double y1, double x2, double y2, string color, double width)
    {
        return new LineAction
        {
            StartX = x1, StartY = y1, EndX = x2, EndY = y2,
            Color = color, StrokeWidth = width
        };
    }

    private static LineAction MakeArrowLine(double x1, double y1, double x2, double y2, string color, double width)
    {
        return new LineAction
        {
            StartX = x1, StartY = y1, EndX = x2, EndY = y2,
            Color = color, StrokeWidth = width, HasArrow = true
        };
    }

    private static ShapeAction MakeRect(double x, double y, double w, double h, string color, double width, string? fill = null)
    {
        return new ShapeAction
        {
            ShapeType = ShapeType.Rect,
            X = x, Y = y, Width = w, Height = h,
            Color = color, StrokeWidth = width, FillColor = fill
        };
    }

    private static TextAction MakeText(string text, double x, double y, string color, double fontSize)
    {
        return new TextAction
        {
            Text = text, X = x, Y = y,
            Color = color, FontSize = fontSize
        };
    }

    #endregion
}

public class TemplateItem
{
    public string Name { get; set; }
    public string Icon { get; set; }
    public string Description { get; set; }
    public Func<List<DrawActionBase>> Generator { get; set; }

    public TemplateItem(string name, string icon, string description, Func<List<DrawActionBase>> generator)
    {
        Name = name; Icon = icon; Description = description; Generator = generator;
    }
}
