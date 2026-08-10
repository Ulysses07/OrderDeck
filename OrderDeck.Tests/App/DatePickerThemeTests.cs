using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrderDeck.Tests.App;

/// <summary>
/// OD.DatePicker / OD.Calendar zincirini doğrular (Faz 2b).
///
/// NEDEN: bu iki stil PART_* adlarına ve ızgara ölçülerine bağlı. Adı yanlış
/// yazılmış bir PART ya da eksik bir ColumnDefinition İSTİSNA ATMAZ — takvim
/// sessizce boş, tek hücreye sıkışmış veya düğmesiz açılır. Derleyici de
/// göremez, çünkü şablon ancak kontrol gerçekten kurulunca uygulanır.
/// </summary>
public class DatePickerThemeTests
{
    [Fact]
    public void DatePicker_template_supplies_text_box_and_button()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var dp = new DatePicker
            {
                Style = (Style)Application.Current!.Resources["OD.DatePicker"],
                SelectedDate = new DateTime(2026, 8, 10)
            };
            Realize(dp);

            // Bu üçü olmadan kontrol çalışmaz: metin görünmez, açılır düğme
            // tıklanamaz, takvim koyacak yer bulamaz.
            Assert.NotNull(dp.Template.FindName("PART_TextBox", dp) as DatePickerTextBox);
            Assert.NotNull(dp.Template.FindName("PART_Button", dp) as Button);
            Assert.NotNull(dp.Template.FindName("PART_Popup", dp) as Popup);
        });

        Assert.Null(error);
    }

    [Fact]
    public void Calendar_month_grid_gets_populated()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var cal = new Calendar
            {
                Style = (Style)Application.Current!.Resources["OD.Calendar"],
                DisplayDate = new DateTime(2026, 8, 10),
                SelectedDate = new DateTime(2026, 8, 10)
            };
            Realize(cal);

            var item = FindVisual<CalendarItem>(cal);
            Assert.NotNull(item);

            var month = item!.Template.FindName("PART_MonthView", item) as Grid;
            Assert.NotNull(month);

            // 7 sütun x 7 satır: ilk satır gün adları, kalan altısı haftalar.
            // Eksik tanım hücreleri üst üste bindirir, hata vermez.
            Assert.Equal(7, month!.ColumnDefinitions.Count);
            Assert.Equal(7, month.RowDefinitions.Count);

            // 7 gün başlığı + 42 gün düğmesi. Sıfır çıkarsa PART adı yanlıştır.
            Assert.Equal(42, month.Children.OfType<CalendarDayButton>().Count());

            var year = item.Template.FindName("PART_YearView", item) as Grid;
            Assert.NotNull(year);
            Assert.Equal(12, year!.Children.OfType<CalendarButton>().Count());

            // Gün adı şablonu yalnız ControlTemplate.Resources'ta, yalnız
            // ComponentResourceKey ile aranıyor — kaçarsa başlıklar yok olur.
            Assert.Equal(7, month.Children.OfType<TextBlock>().Count());
        });

        Assert.Null(error);
    }

    /// <summary>Şablon ancak ölçülen bir ağaçta uygulanır.</summary>
    private static void Realize(FrameworkElement element)
    {
        var host = new Border { Width = 400, Height = 400, Child = element };
        host.Measure(new Size(400, 400));
        host.Arrange(new Rect(0, 0, 400, 400));
        host.UpdateLayout();
        ThemeTestHost.Pump();
        host.UpdateLayout();
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindVisual<T>(child) is { } deep) return deep;
        }

        return null;
    }
}
