using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LiveDeck.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private object? _currentView;

    private readonly ActiveCodesViewModel _activeCodes;
    private readonly OrderQueueViewModel _orders;
    private readonly ChatPanelViewModel _chat;

    public MainViewModel(
        ActiveCodesViewModel activeCodes,
        OrderQueueViewModel orders,
        ChatPanelViewModel chat)
    {
        _activeCodes = activeCodes;
        _orders = orders;
        _chat = chat;
        CurrentView = _orders;
    }

    [RelayCommand]
    private void NavigateToOrders() => CurrentView = _orders;

    [RelayCommand]
    private void NavigateToActiveCodes() => CurrentView = _activeCodes;

    [RelayCommand]
    private void NavigateToChat() => CurrentView = _chat;
}
