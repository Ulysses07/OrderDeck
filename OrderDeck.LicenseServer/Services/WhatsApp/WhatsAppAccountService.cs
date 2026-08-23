using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>Bir hesabı bağlamak için gereken her şey. <paramref name="TwoStepPin"/>
/// yalnız Embedded Signup'ta dolu — elle bağlamada PIN'i biz belirlemiyoruz.</summary>
public sealed record WhatsAppAccountUpsert(
    string WabaId,
    string PhoneNumberId,
    string DisplayPhoneNumber,
    string AccessToken,
    string? VerifiedName,
    string? TwoStepPin);

/// <summary>WABA düzeyi çağrılar için kimlikler — bkz.
/// <see cref="WhatsAppAccountService.ResolveWabaContextAsync"/>.</summary>
public sealed record WhatsAppWabaContext(string WabaId, string AccessToken);

/// <summary><see cref="WhatsAppAccountService.UpsertAsync"/> sonucu.
/// <paramref name="Conflict"/> = numara başka lisansta (çağıran 409 döner).</summary>
public sealed record WhatsAppAccountUpsertResult(bool Ok, bool Conflict, WhatsAppAccount? Account)
{
    public static WhatsAppAccountUpsertResult Success(WhatsAppAccount a) => new(true, false, a);
    public static readonly WhatsAppAccountUpsertResult Taken = new(false, true, null);
}

/// <summary>
/// Tenant'ın WhatsApp hesabını çözer ve Access Token'ı şifreler/çözer.
///
/// <para><b>Token saklama:</b> <c>IDataProtector</c> ile şifrelenir. Anahtarlar
/// prod'da <c>./keys:/app/keys</c> volume'unda kalıcıdır (yol <c>DataProtection:KeysPath</c>
/// ile sabit — konteyner root koşmadığı için <c>$HOME</c>'a güvenilemez);
/// bu klasör kaybolursa token'lar çözülemez ve yayıncıların Embedded Signup'ı
/// tekrar yapması gerekir (backup kapsamında olmalı).</para>
///
/// <para><b>Config fallback:</b> DB'de kayıt yoksa <see cref="WhatsAppOptions.DefaultPhoneNumberId"/>
/// + <see cref="WhatsAppOptions.DefaultAccessToken"/> kullanılır — Embedded Signup
/// akışı devreye girene kadar tek numarayla uçtan test için.</para>
/// </summary>
public sealed class WhatsAppAccountService
{
    private const string ProtectorPurpose = "OrderDeck.WhatsApp.AccessToken.v1";

    /// <summary>PIN'in purpose'u token'ınkinden AYRI: aynı purpose'la iki alan
    /// korunursa birinin şifreli metni öbürünün yerine geçirilebilir hâle gelir
    /// (satırı yazabilen biri token'ı PIN sütununa taşıyıp okutabilirdi).</summary>
    private const string PinProtectorPurpose = "OrderDeck.WhatsApp.TwoStepPin.v1";

    private readonly LicenseDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _pinProtector;
    private readonly WhatsAppOptions _opt;

    public WhatsAppAccountService(
        LicenseDbContext db, IDataProtectionProvider protection, IOptions<WhatsAppOptions> opt)
    {
        _db = db;
        _protector = protection.CreateProtector(ProtectorPurpose);
        _pinProtector = protection.CreateProtector(PinProtectorPurpose);
        _opt = opt.Value;
    }

    public string ProtectToken(string rawToken) => _protector.Protect(rawToken);

    /// <summary>Şifreli token'ı çözer. Anahtar döndüyse/bozuksa null döner
    /// (çağıran hesabı "revoked" işaretleyip yayıncıdan yeniden bağlanmasını ister).</summary>
    public string? TryUnprotectToken(string protectedToken) =>
        TryUnprotect(_protector, protectedToken);

    public string ProtectPin(string rawPin) => _pinProtector.Protect(rawPin);

    /// <summary>Şifreli iki adımlı PIN'i çözer. Anahtar döndüyse null döner —
    /// çağıran o zaman yeni PIN üretip Meta'nın ne diyeceğine bakar.</summary>
    public string? TryUnprotectPin(string protectedPin) =>
        TryUnprotect(_pinProtector, protectedPin);

    private static string? TryUnprotect(IDataProtector protector, string payload)
    {
        try { return protector.Unprotect(payload); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public Task<WhatsAppAccount?> GetActiveByLicenseAsync(Guid licenseId, CancellationToken ct) =>
        _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId && a.Status == "active", ct);

    /// <summary>Webhook yönlendirmesi: gelen payload'daki <c>metadata.phone_number_id</c>
    /// ile hangi tenant olduğunu bulur.</summary>
    public Task<WhatsAppAccount?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken ct) =>
        _db.WhatsAppAccounts.FirstOrDefaultAsync(a => a.PhoneNumberId == phoneNumberId, ct);

    /// <summary>Lisans için gönderim kimliklerini çözer. DB kaydı yoksa config
    /// default'una düşer; ikisi de yoksa null (gönderim yapılamaz).</summary>
    public async Task<WhatsAppSendContext?> ResolveSendContextAsync(Guid licenseId, CancellationToken ct)
    {
        var account = await GetActiveByLicenseAsync(licenseId, ct);
        if (account is not null)
        {
            var token = TryUnprotectToken(account.AccessTokenProtected);
            if (!string.IsNullOrEmpty(token))
                return new WhatsAppSendContext(account.PhoneNumberId, token);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_opt.DefaultPhoneNumberId) &&
            !string.IsNullOrWhiteSpace(_opt.DefaultAccessToken))
        {
            return new WhatsAppSendContext(_opt.DefaultPhoneNumberId, _opt.DefaultAccessToken);
        }

        return null;
    }

    /// <summary>
    /// WABA düzeyi Graph çağrıları (şablon kataloğu) için kimlikler.
    ///
    /// <para><b>Gönderim bağlamından neden ayrı:</b> mesaj <c>{PhoneNumberId}/messages</c>'a,
    /// şablon listesi <c>{WabaId}/message_templates</c>'a gidiyor. Ayrıca
    /// <see cref="ResolveSendContextAsync"/>'in config fallback'i (tek numarayla
    /// uçtan test) WABA id taşımıyor — bu yüzden burada fallback YOK ve hesabı
    /// bağlı olmayan lisans null alır.</para>
    /// </summary>
    public async Task<WhatsAppWabaContext?> ResolveWabaContextAsync(Guid licenseId, CancellationToken ct)
    {
        var account = await GetActiveByLicenseAsync(licenseId, ct);
        if (account is null || string.IsNullOrWhiteSpace(account.WabaId)) return null;

        var token = TryUnprotectToken(account.AccessTokenProtected);
        return string.IsNullOrEmpty(token) ? null : new WhatsAppWabaContext(account.WabaId, token);
    }

    /// <summary>
    /// Lisansın WhatsApp hesabını oluşturur ya da günceller. Elle bağlayan admin
    /// ucu ile Embedded Signup ucunun ORTAK gövdesi — "aynı Phone Number ID iki
    /// lisansa bağlanamaz" kuralı burada tek kopya duruyor.
    /// </summary>
    public async Task<WhatsAppAccountUpsertResult> UpsertAsync(
        Guid licenseId, WhatsAppAccountUpsert input, CancellationToken ct)
    {
        var phoneNumberId = input.PhoneNumberId.Trim();

        var owner = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.PhoneNumberId == phoneNumberId, ct);
        if (owner is not null && owner.LicenseId != licenseId)
            return WhatsAppAccountUpsertResult.Taken;

        var account = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);
        var now = DateTimeOffset.UtcNow;

        if (account is null)
        {
            account = new WhatsAppAccount { Id = Guid.NewGuid(), LicenseId = licenseId, ConnectedAt = now };
            _db.WhatsAppAccounts.Add(account);
        }

        account.WabaId = input.WabaId.Trim();
        account.PhoneNumberId = phoneNumberId;
        account.DisplayPhoneNumber = input.DisplayPhoneNumber;
        account.VerifiedName = string.IsNullOrWhiteSpace(input.VerifiedName) ? null : input.VerifiedName.Trim();
        account.AccessTokenProtected = ProtectToken(input.AccessToken.Trim());
        // PIN yalnız yeni geldiyse yazılır: elle bağlama (PIN'siz) bir Embedded
        // Signup'ın bıraktığı PIN'i silmemeli, yoksa numara yeniden register
        // edilemez hâle gelir.
        if (!string.IsNullOrWhiteSpace(input.TwoStepPin))
            account.TwoStepPinProtected = ProtectPin(input.TwoStepPin);
        account.Status = "active";
        account.LastError = null;
        account.DisconnectedAt = null;

        await _db.SaveChangesAsync(ct);
        return WhatsAppAccountUpsertResult.Success(account);
    }
}
