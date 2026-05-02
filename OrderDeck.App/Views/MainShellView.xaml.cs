using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrderDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.Views;

public partial class MainShellView : UserControl
{
    public MainShellView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<MainShellViewModel>();
        Loaded += OnLoaded;
        // Window-bubbled ESC handler — cancels backup-selection mode without
        // requiring focus on a particular control. We attach in Loaded so the
        // ancestor window is materialised.
        Loaded += AttachWindowEscHandler;
    }

    private void AttachWindowEscHandler(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (win is null) return;
        // Use PreviewKeyDown so we get the key before TextBoxes consume it for
        // their own purposes (typing ESC into a text input still cancels mode,
        // matching most apps' behaviour).
        win.PreviewKeyDown -= OnWindowPreviewKeyDown;
        win.PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not MainShellViewModel vm) return;
        if (!vm.IsInBackupSelectionMode) return;

        vm.CancelBackupSelectionCommand.Execute(null);
        e.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Phase 4c: trial just started this session — show banner once
        var licenseService = global::OrderDeck.App.App.Host.Services
            .GetRequiredService<global::OrderDeck.Licensing.Services.LicenseService>();
        if (licenseService.JustStartedTrial)
        {
            System.Windows.MessageBox.Show(
                "Deneme süresi başladı. 14 gün boyunca Instagram chat ile tüm özellikleri ücretsiz kullanabilirsiniz.",
                "OrderDeck — Deneme süresi",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);

            // Flag'i hemen düş — bir sonraki Loaded'da göstermesin
            licenseService.AcknowledgeTrialStartBanner();
        }
    }

    private void ChatList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainShellViewModel vm) return;
        if (ChatList.SelectedItem is not ChatMessageViewModel msgVm) return;

        // Backup-mode short-circuits the queue-add flow: route the chosen chat
        // user to the active label as a backup, then return to normal.
        if (vm.TryAssignChatAsBackup(msgVm)) return;

        vm.AddChatToQueue(msgVm);
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

        // Same branching as double-click: backup mode wins.
        if (!vm.TryAssignChatAsBackup(msgVm))
            vm.AddChatToQueue(msgVm);
        e.Handled = true;
    }
}
