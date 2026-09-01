using System.Windows;

namespace ScoreCap.Views;

public partial class TextInputDialog : System.Windows.Window
{
    public string ResultText { get; private set; } = string.Empty;
    public double ResultFontSize { get; private set; } = 28;

    public TextInputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => TextInputBox.Focus();
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        ResultText = TextInputBox.Text;
        ResultFontSize = FontSizeSlider.Value;
        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
