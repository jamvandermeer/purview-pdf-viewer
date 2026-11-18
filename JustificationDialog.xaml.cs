using System.Windows;

namespace PurviewPdfViewer;

public partial class JustificationDialog : Window
{
    public JustificationDialog()
    {
        InitializeComponent();
    }

    /// <summary>De gekozen of ingevulde reden.</summary>
    public string Justification { get; private set; } = "";

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (ReasonOther.IsChecked == true)
        {
            var text = OtherText.Text.Trim();
            if (text.Length == 0)
            {
                MessageBox.Show(this, "Vul een reden in.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                OtherText.Focus();
                return;
            }
            Justification = text;
        }
        else if (ReasonIncorrect.IsChecked == true)
        {
            Justification = "Het vorige label was onjuist toegepast";
        }
        else
        {
            Justification = "Het vorige label is niet meer van toepassing";
        }

        DialogResult = true;
    }
}
