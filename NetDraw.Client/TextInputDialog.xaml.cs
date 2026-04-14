using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NetDraw.Client;

public partial class TextInputDialog : Window
{
    public string InputText => TxtInput.Text;
    public string SelectedFontFamily => (CmbFont.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Segoe UI";
    public double SelectedFontSize => double.TryParse((CmbSize.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var s) ? s : 18;
    public bool IsBold => BtnBold.IsChecked == true;
    public bool IsItalic => BtnItalic.IsChecked == true;
    public bool IsUnderline => BtnUnderline.IsChecked == true;
    public bool IsStrikethrough => BtnStrikethrough.IsChecked == true;

    public TextInputDialog(string defaultText = "Hello!")
    {
        InitializeComponent();
        TxtInput.Text = defaultText;
        TxtInput.SelectAll();
        TxtInput.Focus();
        TxtInput.TextChanged += (_, _) => UpdatePreview();
    }

    private void FontOption_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (TxtPreview == null) return;
        TxtPreview.Text = TxtInput.Text;
        TxtPreview.FontFamily = new System.Windows.Media.FontFamily(SelectedFontFamily);
        TxtPreview.FontSize = SelectedFontSize;
        TxtPreview.FontWeight = IsBold ? FontWeights.Bold : FontWeights.Normal;
        TxtPreview.FontStyle = IsItalic ? FontStyles.Italic : FontStyles.Normal;

        TxtPreview.TextDecorations = null;
        var decorations = new System.Windows.TextDecorationCollection();
        if (IsUnderline) decorations.Add(System.Windows.TextDecorations.Underline);
        if (IsStrikethrough) decorations.Add(System.Windows.TextDecorations.Strikethrough);
        if (decorations.Count > 0) TxtPreview.TextDecorations = decorations;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TxtInput.Text))
        {
            DialogResult = true;
            Close();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TxtInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnOk_Click(sender, e);
    }
}
