using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// "!kayıt → DM" linkindeki <c>?ig=</c> token'ı. Kendinden-doğrulamalı:
/// ITimeLimitedDataProtector (24 sa) — DB kaydı yok, deploy/restart'ta
/// yaşamaya devam eder. Tek-kullanımlık DEĞİL, bilinçli: token yalnız
/// izleyicinin kendi DM'inde; tekrar açması kendi kimliğini tekrar bağlar,
/// zarar yüzeyi yok. Payload'da PII yok (slug + IG kullanıcı adı).
/// </summary>
public sealed class IntakeIgTokenService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly ITimeLimitedDataProtector _protector;

    public IntakeIgTokenService(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("IntakeForm.InstagramDmLink.v1")
            .ToTimeLimitedDataProtector();

    public string Create(string slug, string igUsername)
        => _protector.Protect($"{slug}\n{igUsername}", Lifetime);

    /// <summary>Geçersiz/süresi dolmuş token'da null — form bağlantısız açılır,
    /// hata ekranı YOK (spec §4).</summary>
    public (string Slug, string IgUsername)? TryRead(string token)
    {
        try
        {
            var parts = _protector.Unprotect(token).Split('\n');
            return parts.Length == 2 ? (parts[0], parts[1]) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
