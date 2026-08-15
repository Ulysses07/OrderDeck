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
    // async void: WPF olay işleyicisinin başka seçeneği yok. Gövde try/catch
    // İÇERMİYOR — akışta beklenen tek await zaten çekmecenin kapanması, ve
    // istisnayı yutmak hatayı görünmez kılardı.
    private async void ChatList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainShellViewModel vm) return;
        if (ChatList.SelectedItem is not ChatMessageViewModel msgVm) return;

        // Backup-mode short-circuits the queue-add flow: route the chosen chat
        // user to the active label as a backup, then return to normal.
        if (vm.TryAssignChatAsBackup(msgVm)) return;

        await vm.AddChatToQueueAsync(msgVm);
    }

    private async void ChatList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainShellViewModel vm) return;
        if (ChatList.SelectedItem is not ChatMessageViewModel msgVm) return;

        // Same branching as double-click: backup mode wins.
        if (vm.TryAssignChatAsBackup(msgVm))
        {
            e.Handled = true;
            return;
        }

        // e.Handled await'ten ÖNCE: await'ten sonra olay çoktan işlenmiş olur.
        e.Handled = true;
        await vm.AddChatToQueueAsync(msgVm);
    }
}
