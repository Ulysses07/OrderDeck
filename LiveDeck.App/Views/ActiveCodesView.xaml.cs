using System.Windows.Controls;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class ActiveCodesView : UserControl
{
    public ActiveCodesView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<ActiveCodesViewModel>();
    }
}
