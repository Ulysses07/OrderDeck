using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Yayıncı paneli — shopper destek talepleri (forgot-password). Mobil
/// DestekTalepleriScreen'in WPF karşılığı. R7-02 sonrası akış: yayıncı
/// "SMS doğrulaması başlat" der, SUNUCU shopper'a OTP SMS'i gönderir ve
/// <c>status="verification-sent"</c> döner. Parola artık yayıncıya HİÇ
/// gösterilmez (WhatsApp/Kopyala yolları kaldırıldı) — kimlik kanıtı
/// telefonun sahibinde kalır.
/// </summary>
public sealed partial class SupportRequestsViewModel : ViewModelBase
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Sunucunun "SMS'i gönderdim" kanıtı. Yalnız bu değer başarıdır;
    /// HTTP 200 tek başına yetmez (eski sunucu parola döndürüp status boş bırakır).</summary>
    private const string StatusVerificationSent = "verification-sent";

    private readonly LicenseApiClient _api;

    public ObservableCollection<SupportRequestRow> Items { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _includeResolved;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isEmpty;

    public SupportRequestsViewModel(LicenseApiClient api) => _api = api;

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
        if (row is null || row.IsResolved || row.IsBusy || row.VerificationSent) return;
        row.IsBusy = true;
        row.RowError = null;
        try
        {
            var resp = await _api.IssueTempPasswordAsync(row.Id, CancellationToken.None);
            if (resp.Status == StatusVerificationSent)
            {
                // Tam reload YAPMA — includeResolved=false iken satır kaybolur
                // ve yayıncı bilgi panelini göremeden talep ekrandan uçar.
                row.MarkVerificationSent();
            }
            else
            {
                // 200 döndü ama kanıt yok: eski sunucu ya da beklenmeyen yanıt.
                // Talebi kapatma — shopper'a SMS gitmemiş olabilir.
                row.RowError = "Sunucu doğrulama SMS'ini onaylamadı. Sunucu güncel mi? "
                    + $"(status: {resp.Status ?? "boş"})";
            }
        }
        catch (Exception ex)
        {
            row.RowError = $"Doğrulama başlatılamadı: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

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
        [NotifyPropertyChangedFor(nameof(CanIssue))]
        [NotifyPropertyChangedFor(nameof(ShowResolvedLabel))]
        private bool _verificationSent;

        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string? _rowError;

        public string KindLabel => Kind switch
        {
            "forgot-password" => "Parola sıfırlama",
            _ => Kind,
        };

        public bool CanIssue => !IsResolved && !VerificationSent;
        public bool ShowResolvedLabel => IsResolved && !VerificationSent;

        public void MarkVerificationSent()
        {
            VerificationSent = true;
            IsResolved = true;
        }

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
