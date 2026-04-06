using System.Windows;
using System.Windows.Input;

namespace NetDraw.Client;

public partial class InputDialog : Window
{
    public string InputText => TxtInput.Text;

    public InputDialog(string prompt, string defaultText = "")
    {
        InitializeComponent();
        TxtPrompt.Text = prompt;
        TxtInput.Text = defaultText;
        TxtInput.SelectAll();
        TxtInput.Focus();
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

    private void TxtInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
        }
    }
}
