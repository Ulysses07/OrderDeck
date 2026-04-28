using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class MainShellView : UserControl
{
    public MainShellView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<MainShellViewModel>();
    }

    private void ChatList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainShellViewModel vm
            && ChatList.SelectedItem is ChatMessageViewModel msgVm)
        {
            vm.AddChatToQueue(msgVm);
        }
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (MenuButton.ContextMenu is { } cm)
        {
            cm.PlacementTarget = MenuButton;
            cm.IsOpen = true;
        }
    }
}
