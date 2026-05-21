using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Settings → "Müşteri App" sekmesi. Yayıncının shopper-code'unu yönetir.
/// Server-side validator: format/reserved/profanity/cooldown(7d)/taken. 7g cooldown
/// dolmadan değiştirilemez — UI bunu gri buton + bilgi mesajıyla gösterir.
/// </summary>
public sealed partial class ShopperAppSettingsViewModel : ObservableObject
{
    private readonly LicenseApiClient _api;
    private readonly ILogger<ShopperAppSettingsViewModel> _log;

    [ObservableProperty] private string _codeInput = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _cooldownMessage;
    [ObservableProperty] private bool _canEditCode = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusMessage; // success feedback ("Kaydedildi")

    private DateTimeOffset? _canChangeAt;
    private string? _currentSavedCode;   // for re-save detection

    public ShopperAppSettingsViewModel(
        LicenseApiClient api,
        ILogger<ShopperAppSettingsViewModel> log)
    {
        _api = api;
        _log = log;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;
            var resp = await _api.GetShopperCodeAsync(ct);
            CodeInput = resp.Code ?? "";
            _currentSavedCode = resp.Code;
            _canChangeAt = resp.CanChangeAt;
            UpdateCooldownState();
        }
        catch (System.Net.Http.HttpRequestException ex) when ((int?)ex.StatusCode == 404)
        {
            ErrorMessage = "Lisans bulunamadı. Önce aktivasyon yap.";
            CanEditCode = false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ShopperCode load failed");
            ErrorMessage = "Sunucuya bağlanılamadı. Daha sonra tekrar dene.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateCooldownState()
    {
        if (_canChangeAt is { } at && at > DateTimeOffset.UtcNow)
        {
            CanEditCode = false;
            var remaining = at - DateTimeOffset.UtcNow;
            CooldownMessage = remaining.TotalDays >= 1
                ? $"Yeniden değiştirebilirsin: {at.LocalDateTime:dd.MM.yyyy HH:mm} ({(int)remaining.TotalDays} gün kaldı)"
                : $"Yeniden değiştirebilirsin: {at.LocalDateTime:dd.MM.yyyy HH:mm} ({(int)remaining.TotalHours} saat kaldı)";
        }
        else
        {
            CanEditCode = true;
            CooldownMessage = null;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        StatusMessage = null;

        var code = (CodeInput ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code))
        {
            ErrorMessage = "Kod boş olamaz.";
            return;
        }

        try
        {
            IsLoading = true;
            var resp = await _api.SetShopperCodeAsync(code, default);
            CodeInput = resp.Code ?? "";
            _currentSavedCode = resp.Code;
            _canChangeAt = resp.CanChangeAt;
            UpdateCooldownState();
            StatusMessage = "Kaydedildi.";
        }
        catch (ShopperCodeValidationException ex)
        {
            ErrorMessage = ex.ErrorCode switch
            {
                "empty"      => "Kod boş olamaz.",
                "length"     => "Kod 3-20 karakter olmalı.",
                "format"     => "Sadece küçük harf ve rakam (a-z, 0-9).",
                "reserved"   => "Bu kelime sistem tarafından ayrılmış.",
                "profanity"  => "Bu kelime uygun değil.",
                "cooldown"   => "Henüz 7 günlük bekleme süresi dolmadı.",
                "taken"      => "Bu kod başka bir yayıncı tarafından kullanılıyor.",
                _            => $"Bilinmeyen hata: {ex.ErrorCode}",
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ShopperCode save failed");
            ErrorMessage = "Beklenmedik hata. Daha sonra dene.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
