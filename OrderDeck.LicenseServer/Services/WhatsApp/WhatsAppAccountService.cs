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
/// prod'da <c>./keys:/root/.aspnet/DataProtection-Keys</c> volume'unda kalıcıdır;
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

    private readonly LicenseDbContext _db;
    private readonly IDataProtector _protector;
    private readonly WhatsAppOptions _opt;

    public WhatsAppAccountService(
        LicenseDbContext db, IDataProtectionProvider protection, IOptions<WhatsAppOptions> opt)
    {
        _db = db;
        _protector = protection.CreateProtector(ProtectorPurpose);
        _opt = opt.Value;
    }

    public string ProtectToken(string rawToken) => _protector.Protect(rawToken);

    /// <summary>Şifreli token'ı çözer. Anahtar döndüyse/bozuksa null döner
    /// (çağıran hesabı "revoked" işaretleyip yayıncıdan yeniden bağlanmasını ister).</summary>
    public string? TryUnprotectToken(string protectedToken)
    {
        try { return _protector.Unprotect(protectedToken); }
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
            account.TwoStepPinProtected = ProtectToken(input.TwoStepPin);
        account.Status = "active";
        account.LastError = null;
        account.DisconnectedAt = null;

        await _db.SaveChangesAsync(ct);
        return WhatsAppAccountUpsertResult.Success(account);
    }
}
