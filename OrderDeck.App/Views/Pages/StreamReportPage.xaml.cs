using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Yayın raporu sayfası (eski <c>StreamReportDialog</c> penceresi).
///
/// Pencere sürümünde rapor önce yaratılıp SONRA <c>LoadReport(sessionId)</c>
/// çağrılıyordu; fabrikaya taşındı — yüklenmemiş bir rapor sayfası artık
/// yaratılamıyor.
/// </summary>
public partial class StreamReportPage : UserControl
{
    private StreamReportPage(StreamReportViewModel vm, string sessionId)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Load(sessionId);
    }

    public static StreamReportPage Create(StreamReportViewModel vm, string sessionId)
        => new(vm, sessionId);
}
