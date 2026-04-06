using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetDraw.Client;

public partial class ImageImportDialog : Window
{
    private BitmapSource _originalBitmap;
    public BitmapSource? ResultBitmap { get; private set; }
    public double ScalePercent => SliderScale.Value;

    public ImageImportDialog(string imagePath)
    {
        InitializeComponent();

        var uri = new Uri(imagePath, UriKind.Absolute);
        _originalBitmap = new BitmapImage(uri);
        ImgPreview.Source = _originalBitmap;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap == null) return;
        ApplyFilter();
    }

    private void SliderScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtScale != null)
            TxtScale.Text = $"{(int)SliderScale.Value}%";
    }

    private void ApplyFilter()
    {
        if (RbOriginal.IsChecked == true)
        {
            ImgPreview.Source = _originalBitmap;
            return;
        }

        var src = new WriteableBitmap(_originalBitmap);
        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        byte[] pixels = new byte[h * stride];
        src.CopyPixels(pixels, stride, 0);

        if (RbGrayscale.IsChecked == true)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte gray = (byte)(0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i]);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
            }
        }
        else if (RbSepia.IsChecked == true)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte r = pixels[i + 2], g = pixels[i + 1], b = pixels[i];
                pixels[i + 2] = (byte)Math.Min(255, 0.393 * r + 0.769 * g + 0.189 * b);
                pixels[i + 1] = (byte)Math.Min(255, 0.349 * r + 0.686 * g + 0.168 * b);
                pixels[i]     = (byte)Math.Min(255, 0.272 * r + 0.534 * g + 0.131 * b);
            }
        }
        else if (RbInvert.IsChecked == true)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i]     = (byte)(255 - pixels[i]);
                pixels[i + 1] = (byte)(255 - pixels[i + 1]);
                pixels[i + 2] = (byte)(255 - pixels[i + 2]);
            }
        }
        else if (RbHighContrast.IsChecked == true)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte gray = (byte)(0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i]);
                byte val = gray > 128 ? (byte)255 : (byte)0;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = val;
            }
        }
        else if (RbSketch.IsChecked == true)
        {
            // Edge detection (simple Sobel-like)
            byte[] grayData = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int idx = y * stride + x * 4;
                    grayData[y * w + x] = (byte)(0.299 * pixels[idx + 2] + 0.587 * pixels[idx + 1] + 0.114 * pixels[idx]);
                }

            byte[] result = new byte[pixels.Length];
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int gx = -grayData[(y - 1) * w + x - 1] + grayData[(y - 1) * w + x + 1]
                           - 2 * grayData[y * w + x - 1] + 2 * grayData[y * w + x + 1]
                           - grayData[(y + 1) * w + x - 1] + grayData[(y + 1) * w + x + 1];
                    int gy = -grayData[(y - 1) * w + x - 1] - 2 * grayData[(y - 1) * w + x] - grayData[(y - 1) * w + x + 1]
                           + grayData[(y + 1) * w + x - 1] + 2 * grayData[(y + 1) * w + x] + grayData[(y + 1) * w + x + 1];
                    int mag = (int)Math.Sqrt(gx * gx + gy * gy);
                    byte edge = (byte)(255 - Math.Min(255, mag));
                    int idx = y * stride + x * 4;
                    result[idx] = result[idx + 1] = result[idx + 2] = edge;
                    result[idx + 3] = 255;
                }
            pixels = result;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        ImgPreview.Source = bmp;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        ResultBitmap = ImgPreview.Source as BitmapSource ?? _originalBitmap;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
