using System.Windows.Controls;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class OrderQueueView : UserControl
{
    public OrderQueueView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<OrderQueueViewModel>();
    }
}
