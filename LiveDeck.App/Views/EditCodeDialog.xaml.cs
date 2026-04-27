using System.Globalization;
using System.Windows;

namespace LiveDeck.App.Views;

public partial class EditCodeDialog : Window
{
    public EditCodeDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            CodeBox.Text  = CodeText  ?? "";
            SizesBox.Text = SizesText ?? "";
            PriceBox.Text = Price.ToString("0.##", CultureInfo.InvariantCulture);
        };
    }

    public string? CodeText  { get; set; }
    public string? SizesText { get; set; }
    public decimal Price     { get; set; }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        CodeText  = CodeBox.Text.Trim();
        SizesText = SizesBox.Text.Trim();
        if (!decimal.TryParse(PriceBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) &&
            !decimal.TryParse(PriceBox.Text, NumberStyles.Any, new CultureInfo("tr-TR"), out p))
        {
            MessageBox.Show("Geçerli bir fiyat girin (örn: 199 veya 199.50)",
                "Geçersiz fiyat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Price = p;
        DialogResult = true;
    }
}
