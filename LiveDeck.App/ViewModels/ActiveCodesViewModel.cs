using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;

namespace LiveDeck.App.ViewModels;

public sealed partial class ActiveCodesViewModel : ViewModelBase
{
    private readonly ActiveCodeService _service;
    private readonly StreamSessionService _sessions;

    public ObservableCollection<ActiveCode> Codes { get; } = new();

    [ObservableProperty] private ActiveCode? _selected;

    public ActiveCodesViewModel(ActiveCodeService service, StreamSessionService sessions)
    {
        _service = service;
        _sessions = sessions;
        Refresh();
    }

    public void Refresh()
    {
        Codes.Clear();
        var session = _sessions.GetActive();
        if (session is null) return;
        foreach (var c in _service.GetActive(session.Id)) Codes.Add(c);
    }

    [RelayCommand]
    private void Add()
    {
        var session = _sessions.GetActive();
        if (session is null)
        {
            MessageBox.Show("Önce yayın başlat (Sipariş Kuyruğu → Yayın Başlat).",
                "Aktif yayın yok", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Views.EditCodeDialog();
        if (dialog.ShowDialog() != true) return;

        var sizes = (dialog.SizesText ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .ToArray();
        if (sizes.Length == 0) sizes = new[] { "TEK BEDEN" };

        _service.Add(session.Id, dialog.CodeText ?? "", sizes, dialog.Price);
        Refresh();
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is null) return;
        var dialog = new Views.EditCodeDialog
        {
            CodeText = Selected.Code,
            SizesText = string.Join(", ", Selected.Sizes),
            Price = Selected.Price
        };
        if (dialog.ShowDialog() != true) return;

        _service.UpdatePrice(Selected.Id, dialog.Price);
        var sizes = (dialog.SizesText ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .ToArray();
        _service.UpdateSizes(Selected.Id, sizes);
        Refresh();
    }

    [RelayCommand]
    private void CloseSelected()
    {
        if (Selected is null) return;
        _service.Close(Selected.Id);
        Refresh();
    }
}
