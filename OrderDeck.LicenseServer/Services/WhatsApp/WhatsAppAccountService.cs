using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

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
}
