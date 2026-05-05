using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Controls;

public partial class AnimationPickerControl : UserControl
{
    public AnimationPickerControl()
    {
        InitializeComponent();
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnimationPickerViewModel vm
            && sender is Button { CommandParameter: string id })
        {
            vm.SelectedId = id;
        }
    }
}
