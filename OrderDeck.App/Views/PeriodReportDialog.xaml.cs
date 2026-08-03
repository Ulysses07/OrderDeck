using System.Windows;
using OrderDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.Views;

public partial class PeriodReportDialog : Window
{
    public PeriodReportDialog()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<PeriodReportViewModel>();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
