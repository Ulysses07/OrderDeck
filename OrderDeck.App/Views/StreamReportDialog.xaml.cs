using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class StreamReportDialog : Window
{
    private readonly StreamReportViewModel _vm;

    public StreamReportDialog()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<StreamReportViewModel>();
        DataContext = _vm;
    }

    public void LoadReport(string sessionId) => _vm.Load(sessionId);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
