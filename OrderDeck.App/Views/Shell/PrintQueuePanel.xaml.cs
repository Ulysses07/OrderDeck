using System.Linq;
using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Shell;

public partial class PrintQueuePanel : UserControl
{
    public PrintQueuePanel() => InitializeComponent();

    // MainShellView.xaml.cs'ten taşındı — gövde değişmedi. Eski görünüm
    // Görev 15'te sökülene kadar oradaki kopya da duruyor (MainShellView.xaml
    // hâlâ kendi QueueList'iyle o handler'a bağlı).
    private void QueueList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainShellViewModel vm) return;

        foreach (var added in e.AddedItems.OfType<LabelViewModel>())
            if (!vm.SelectedQueueItems.Contains(added))
                vm.SelectedQueueItems.Add(added);

        foreach (var removed in e.RemovedItems.OfType<LabelViewModel>())
            vm.SelectedQueueItems.Remove(removed);
    }
}
