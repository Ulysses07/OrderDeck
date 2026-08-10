using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Dönem raporu sayfası (eski <c>PeriodReportDialog</c> penceresi).
/// </summary>
public partial class PeriodReportPage : UserControl
{
    private PeriodReportPage(PeriodReportViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    public static PeriodReportPage Create(PeriodReportViewModel vm) => new(vm);
}
