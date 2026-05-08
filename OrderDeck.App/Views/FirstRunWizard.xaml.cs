using System;
using System.Windows;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views;

public partial class FirstRunWizard : Window
{
    private readonly FirstRunWizardViewModel _vm;

    public FirstRunWizard(FirstRunWizardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        // DialogResult signals "operator reached Finish" (true) vs
        // "skipped / closed early" (false). The wizard's Finish command
        // already persisted HasCompletedFirstRun + YouTube handle to
        // settings.json before raising RequestClose; this just closes
        // the window with the right return value for App.OnStartup.
        DialogResult = _vm.IsStep6;
        Close();
    }
}
