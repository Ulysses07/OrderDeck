using System.Linq;
using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Shell;

public partial class PrintQueuePanel : UserControl
{
    public PrintQueuePanel() => InitializeComponent();

    /// <summary>Kuyruğun vazgeçilmez yüksekliği: çerçeve + sekme başlığı + listenin asgarisi
    /// (<c>OD.Layout.QueueMinHeight</c>). FİYAT değişikliğinin satır içi sonucu bu paya girmez
    /// (<see cref="MainShellView"/>); sekme başlığı ilk ölçümden sonra belli olur.</summary>
    internal double ReservedHeight
        => Frame.BorderThickness.Top + Frame.BorderThickness.Bottom + TabHeader.ActualHeight + QueueList.MinHeight;

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
