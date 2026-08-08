using System.Windows.Controls;
using System.Windows.Input;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Shell;

public partial class ChatPanel : UserControl
{
    public ChatPanel() => InitializeComponent();

    // MainShellView.xaml.cs'ten taşındı — gövdeler değişmedi. Eski görünüm
    // Görev 15'te sökülene kadar oradaki kopyalar da duruyor (XAML olay
    // bağlamaları hâlâ onlara işaret ediyor).
    private void ChatList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainShellViewModel vm) return;
        if (ChatList.SelectedItem is not ChatMessageViewModel msgVm) return;

        // Backup-mode short-circuits the queue-add flow: route the chosen chat
        // user to the active label as a backup, then return to normal.
        if (vm.TryAssignChatAsBackup(msgVm)) return;

        vm.AddChatToQueue(msgVm);
    }

    private void ChatList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainShellViewModel vm) return;
        if (ChatList.SelectedItem is not ChatMessageViewModel msgVm) return;

        // Same branching as double-click: backup mode wins.
        if (!vm.TryAssignChatAsBackup(msgVm))
            vm.AddChatToQueue(msgVm);
        e.Handled = true;
    }
}
