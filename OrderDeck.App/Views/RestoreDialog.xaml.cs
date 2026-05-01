using System.Collections.Generic;
using System.Windows;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Views;

public partial class RestoreDialog : Window
{
    private readonly RestoreDialogViewModel _vm;

    public RestoreDialog(RestoreService service, IReadOnlyList<BackupMetadata> available)
    {
        InitializeComponent();
        _vm = new RestoreDialogViewModel(service);
        _vm.Populate(available);
        _vm.CloseRequested += (_, _) => { DialogResult = false; Close(); };
        _vm.RestoreCompletedEvent += (_, _) => { DialogResult = true; Close(); };
        DataContext = _vm;
    }
}
