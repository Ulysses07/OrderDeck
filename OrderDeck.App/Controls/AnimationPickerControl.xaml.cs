using System;
using System.Linq;
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

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnimationPickerViewModel vm
            && sender is Button { CommandParameter: string id })
        {
            var entry = vm.Animations.FirstOrDefault(a => a.Id == id);
            if (entry is null) return;
            var url = $"{entry.OverlayBase}/overlay/preview?animation={Uri.EscapeDataString(id)}";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preview launch failed: {ex.Message}");
            }
        }
    }
}
