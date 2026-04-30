using System.Windows;
using System.Windows.Input;
using OrderDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.Views;

public partial class StreamHistoryDialog : Window
{
    public StreamHistoryDialog()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<StreamHistoryViewModel>();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is StreamHistoryRow row)
        {
            var report = App.Host.Services.GetRequiredService<StreamReportDialog>();
            report.LoadReport(row.SessionId);
            report.Owner = this;
            report.ShowDialog();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
