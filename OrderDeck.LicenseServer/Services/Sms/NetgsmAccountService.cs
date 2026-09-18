using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Lisans → Netgsm hesabı çözümlemesi ve API şifresinin şifrelenmesi.
///
/// <para><b>Anahtar kaybı:</b> <c>IDataProtection</c> anahtarları prod'da
/// <c>.\keys:/app/keys</c> volume'unda kalıcıdır (bkz. WhatsAppAccountService).
/// Klasör kaybolursa şifreler çözülemez ve yayıncıların kimliklerini yeniden
/// girmesi gerekir — bu klasör yedek kapsamında olmalı.</para>
///
/// <para><b>Fail-closed:</b> yalnız <c>Status == Verified</c> hesap iş yapar.
/// Doğrulanmamış hesap için marka çözülmez → onay satırı açılmaz (spec §5.1).</para>
/// </summary>
public sealed class NetgsmAccountService
{
    /// <summary>Şifreleme amacı — değiştirilirse mevcut şifreler çözülemez.
    ///
    /// <para><b>Değişmez kural:</b> korunan her alanın KENDİ purpose'u olur, purpose
    /// paylaşılmaz. Aynı purpose'la iki alan korunursa birinin şifreli metni öbürünün
    /// sütununa taşınıp okutulabilir (bkz. <c>WhatsAppAccountService</c>: token ve PIN
    /// ayrı purpose'larda). Bugün <c>NetgsmAccount</c>'ta korunan tek alan var; ileride
    /// ikinci bir sır eklenirse (örn. İYS API anahtarı) bu protector'a ORTAK edilmez,
    /// yeni bir purpose sabiti tanımlanır.</para></summary>
    private const string PasswordProtectorPurpose = "OrderDeck.Netgsm.Password.v1";

    private readonly LicenseDbContext _db;
    private readonly IDataProtector _protector;

    public NetgsmAccountService(LicenseDbContext db, IDataProtectionProvider protection)
    {
        _db = db;
        _protector = protection.CreateProtector(PasswordProtectorPurpose);
    }

    public string ProtectPassword(string rawPassword) => _protector.Protect(rawPassword);

    /// <summary>Şifreli metni çözer. Anahtar döndüyse/bozuksa <c>null</c> döner —
    /// çağıran hesabı <c>disabled</c> işaretler, sessizce göndermez.</summary>
    public string? TryUnprotectPassword(string protectedPassword)
    {
        try { return _protector.Unprotect(protectedPassword); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public Task<NetgsmAccount?> GetVerifiedByLicenseAsync(Guid licenseId, CancellationToken ct)
        => _db.NetgsmAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId && a.Status == NetgsmAccountStatus.Verified, ct);

    public async Task<IReadOnlyList<NetgsmAccount>> ListVerifiedAsync(CancellationToken ct)
        => await _db.NetgsmAccounts
            .Where(a => a.Status == NetgsmAccountStatus.Verified)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Onay toplama yolunun ihtiyacı: yalnız marka kodu, şifre değil.</summary>
    public async Task<string?> GetBrandCodeAsync(Guid licenseId, CancellationToken ct)
        => await _db.NetgsmAccounts
            .Where(a => a.LicenseId == licenseId && a.Status == NetgsmAccountStatus.Verified)
            .Select(a => a.BrandCode)
            .FirstOrDefaultAsync(ct);
}
