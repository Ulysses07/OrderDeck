using System.Windows.Controls;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class ChatPanelView : UserControl
{
    public ChatPanelView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<ChatPanelViewModel>();
    }
}
