using System.Linq;
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

    private void QueueList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainShellViewModel vm) return;

        foreach (var added in e.AddedItems.OfType<LabelViewModel>())
            if (!vm.SelectedQueueItems.Contains(added))
                vm.SelectedQueueItems.Add(added);

        foreach (var removed in e.RemovedItems.OfType<LabelViewModel>())
            vm.SelectedQueueItems.Remove(removed);
    }

    private void ChatList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainShellViewModel vm) return;
        if (ChatList.SelectedItem is not ChatMessageViewModel msgVm) return;

        vm.AddChatToQueue(msgVm);
        e.Handled = true;
    }
}
