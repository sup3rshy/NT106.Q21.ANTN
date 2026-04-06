using System.Windows;
using System.Windows.Media;

namespace NetDraw.Client;

public partial class ColorPickerDialog : Window
{
    public string SelectedColor { get; private set; } = "#000000";
    private bool _updatingFromSlider;
    private bool _updatingFromHex;

    public ColorPickerDialog(string initialColor = "#000000")
    {
        InitializeComponent();

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(initialColor);
            SliderR.Value = color.R;
            SliderG.Value = color.G;
            SliderB.Value = color.B;
        }
        catch { }
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromHex) return;
        _updatingFromSlider = true;

        byte r = (byte)SliderR.Value;
        byte g = (byte)SliderG.Value;
        byte b = (byte)SliderB.Value;

        if (TxtR != null) TxtR.Text = r.ToString();
        if (TxtG != null) TxtG.Text = g.ToString();
        if (TxtB != null) TxtB.Text = b.ToString();

        var color = Color.FromRgb(r, g, b);
        SelectedColor = $"#{r:X2}{g:X2}{b:X2}";

        if (ColorPreview != null)
            ColorPreview.Background = new SolidColorBrush(color);
        if (TxtHex != null)
            TxtHex.Text = SelectedColor;

        _updatingFromSlider = false;
    }

    private void TxtHex_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updatingFromSlider) return;

        string hex = TxtHex.Text.Trim();
        if (!hex.StartsWith("#")) hex = "#" + hex;
        if (hex.Length != 7) return;

        try
        {
            _updatingFromHex = true;
            var color = (Color)ColorConverter.ConvertFromString(hex);
            SliderR.Value = color.R;
            SliderG.Value = color.G;
            SliderB.Value = color.B;
            ColorPreview.Background = new SolidColorBrush(color);
            SelectedColor = hex.ToUpper();
            _updatingFromHex = false;
        }
        catch
        {
            _updatingFromHex = false;
        }
    }

    private void QuickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string color)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(color);
                _updatingFromHex = true;
                SliderR.Value = c.R;
                SliderG.Value = c.G;
                SliderB.Value = c.B;
                _updatingFromHex = false;

                TxtR.Text = c.R.ToString();
                TxtG.Text = c.G.ToString();
                TxtB.Text = c.B.ToString();
                TxtHex.Text = color;
                ColorPreview.Background = new SolidColorBrush(c);
                SelectedColor = color;
            }
            catch { }
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
