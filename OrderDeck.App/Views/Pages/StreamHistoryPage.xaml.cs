using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services.Pages;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Yayın geçmişi sayfası (eski <c>StreamHistoryDialog</c> penceresi).
/// </summary>
public partial class StreamHistoryPage : UserControl
{
    private StreamHistoryPage(StreamHistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    public static StreamHistoryPage Create(StreamHistoryViewModel vm) => new(vm);

    /// <summary>
    /// Satıra çift tıklama raporu YIĞINA BİNDİRİR. Pencere sürümünde
    /// <c>report.ShowDialog()</c> ile iç içe modal açılıyordu; sayfa
    /// sürümünde geri oku raporu kapatıp buraya döner.
    ///
    /// <c>await</c> edilmiyor (dolayısıyla <c>async void</c> de yok): rapor
    /// kapandıktan sonra burada yapılacak iş yok, geçmiş listesi rapordan
    /// etkilenmiyor.
    /// </summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not StreamHistoryRow row) return;
        if (App.Host is null) return;

        var services = App.Host.Services;
        _ = services.GetRequiredService<IPageService>().ShowAsync(
            "stream-report", "Yayın Raporu",
            _ => StreamReportPage.Create(
                     services.GetRequiredService<StreamReportViewModel>(), row.SessionId));
    }
}
