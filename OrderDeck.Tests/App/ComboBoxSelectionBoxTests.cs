using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// KALICI BEKÇİ — Faz 4b'de yaşanan gerçek bir gerilemeden doğdu.
///
/// <c>DisplayMemberPath</c> seçim kutusuna <c>ItemTemplateSelector</c> üzerinden
/// ulaşıyor. <c>OD.ComboBox</c>'ın şablonundaki <c>ContentPresenter</c> o
/// TemplateBinding'i taşımazsa WPF varsayılan şablona düşüyor ve seçili öğeyi
/// <c>ToString()</c> ile çiziyor — Çekiliş çekmecesinde açılır listeler
/// "DurationOption { Label = Manuel bitir, Seconds = 0 }" gösterdi.
///
/// Derleme kırılmadığı, XAML de geçerli olduğu için tek kanıt çalışma anında
/// çizilen metin: bu yüzden test şablonu değil, ÇIKTIYI ölçüyor.
/// </summary>
public class ComboBoxSelectionBoxTests
{
    private sealed record Option(string Label, int Seconds);

    [Fact]
    public void OD_ComboBox_selection_box_honours_DisplayMemberPath()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var combo = new ComboBox
            {
                Style = (Style)Application.Current!.Resources["OD.ComboBox"],
                ItemsSource = new[] { new Option("Manuel bitir", 0) },
                DisplayMemberPath = "Label",
                SelectedIndex = 0,
            };

            combo.Measure(new Size(240, 40));
            combo.Arrange(new Rect(0, 0, 240, 40));
            combo.UpdateLayout();
            ThemeTestHost.Pump();

            var drawn = TextBlocks(combo).Select(t => t.Text).ToList();

            Assert.Contains("Manuel bitir", drawn);
        });

        Assert.Null(error);
    }

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBlock text) yield return text;

            foreach (var nested in TextBlocks(child)) yield return nested;
        }
    }
}
