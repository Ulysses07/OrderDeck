using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.Core.Customers;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Yayıncı paneli — shopper destek talepleri (forgot-password). Mobil
/// DestekTalepleriScreen'in WPF karşılığı. Yayıncı talebi görür, "Geçici parola
/// üret" der; üretilen parola satırda gösterilir, "WhatsApp ile gönder" /
/// "Kopyala" ile shopper'a iletilir.
/// </summary>
public sealed partial class SupportRequestsViewModel : ViewModelBase
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly LicenseApiClient _api;
    private readonly WhatsAppMessageBuilder _wa;
    private readonly IUrlLauncher _launcher;

    public ObservableCollection<SupportRequestRow> Items { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _includeResolved;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isEmpty;

    public SupportRequestsViewModel(
        LicenseApiClient api, WhatsAppMessageBuilder wa, IUrlLauncher launcher)
    {
        _api = api;
        _wa = wa;
        _launcher = launcher;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var rows = await _api.GetSupportRequestsAsync(IncludeResolved, take: 100, CancellationToken.None);
            Items.Clear();
            // Bekleyenler önce, sonra tarihe göre azalan.
            foreach (var r in rows.OrderBy(x => x.ResolvedAt != null).ThenByDescending(x => x.CreatedAt))
                Items.Add(SupportRequestRow.FromDto(r));
            IsEmpty = Items.Count == 0;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Talepler yüklenemedi: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleResolvedAsync()
    {
        IncludeResolved = !IncludeResolved;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task IssueTempPasswordAsync(SupportRequestRow? row)
    {
        if (row is null || row.IsResolved || row.IsBusy || row.HasTempPassword) return;
        row.IsBusy = true;
        row.RowError = null;
        try
        {
            var resp = await _api.IssueTempPasswordAsync(row.Id, CancellationToken.None);
            // Parolayı satırda göster (kapatmadan WhatsApp/Kopyala yapılabilsin).
            // Tam reload YAPMA — includeResolved=false iken satır kaybolur ve
            // yayıncı henüz parolayı iletmemiş olur.
            row.TempPassword = resp.TempPassword;
            row.MarkResolved();
        }
        catch (Exception ex)
        {
            row.RowError = $"Parola üretilemedi: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private void SendWhatsApp(SupportRequestRow? row)
    {
        if (row is null || string.IsNullOrEmpty(row.TempPassword)) return;
        var msg = BuildMessage(row.ShopperName, row.TempPassword!);
        var link = _wa.BuildWaMeLink(row.ShopperPhone, msg);
        _launcher.Launch(link);
    }

    /// <summary>Shopper'a gönderilecek geçici parola mesajı (mobildeki ile aynı).</summary>
    public static string BuildMessage(string shopperName, string tempPassword) =>
        $"Merhaba {shopperName}, OrderDeck uygulamamızdaki hesabın için geçici parolan: "
        + $"{tempPassword}\n\nLütfen giriş yaptıktan sonra Profilim > Parolayı değiştir'den "
        + "yeni parolanı belirle.";

    public sealed partial class SupportRequestRow : ObservableObject
    {
        public Guid Id { get; init; }
        public string ShopperName { get; init; } = "";
        public string ShopperPhone { get; init; } = "";
        public string Kind { get; init; } = "";
        public string CreatedAtLabel { get; init; } = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanIssue))]
        [NotifyPropertyChangedFor(nameof(ShowResolvedLabel))]
        private bool _isResolved;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTempPassword))]
        [NotifyPropertyChangedFor(nameof(CanIssue))]
        private string? _tempPassword;

        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string? _rowError;

        public string KindLabel => Kind switch
        {
            "forgot-password" => "Parola sıfırlama",
            _ => Kind,
        };

        public bool HasTempPassword => !string.IsNullOrEmpty(TempPassword);
        public bool CanIssue => !IsResolved && !HasTempPassword;
        public bool ShowResolvedLabel => IsResolved && !HasTempPassword;

        public void MarkResolved() => IsResolved = true;

        public static SupportRequestRow FromDto(SupportRequestDto d) => new()
        {
            Id = d.Id,
            ShopperName = d.ShopperName,
            ShopperPhone = d.ShopperPhone,
            Kind = d.Kind,
            IsResolved = d.ResolvedAt is not null,
            CreatedAtLabel = d.CreatedAt.LocalDateTime.ToString("dd MMM yyyy HH:mm", Tr),
        };
    }
}
