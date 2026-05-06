using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Controls;

public partial class AnimationPickerControl : UserControl
{
    // Lazy singleton — reused across multiple instances of the control.
    private static readonly AnimationHoverPreviewService HoverService = new();

    public AnimationPickerControl()
    {
        InitializeComponent();
        // Ensure hover popup hides if THIS control unloads (e.g., Settings dialog closes).
        Unloaded += (_, _) => HoverService.Hide();
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnimationPickerViewModel vm
            && sender is Button { CommandParameter: string id })
        {
            vm.SelectedId = id;
        }
    }

    /// <summary>Card-level hover IN — fired by Border.MouseEnter in the DataTemplate.</summary>
    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el
            && el.DataContext is AnimationCatalogEntry entry
            && DataContext is AnimationPickerViewModel)
        {
            HoverService.ShowFor(el, entry.OverlayBase ?? "", entry.Id);
        }
    }

    /// <summary>Card-level hover OUT.</summary>
    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        HoverService.Hide();
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
