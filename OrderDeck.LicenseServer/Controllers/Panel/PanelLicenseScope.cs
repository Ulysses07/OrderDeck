using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Panel isteğindeki tenant müşterisinden aktif lisansı çözer.
///
/// <para>Repoda bu sorgu her controller'da özel bir metot olarak tekrarlanıyor
/// (<c>PanelOperatorsController.ResolveLicenseAsync</c> vb.). Bu iş üç yeni
/// controller getiriyor; aynı gövdeyi üç kez daha kopyalamak yerine buraya
/// alındı. <b>Mevcut controller'lar bilinçli olarak ellenmiyor</b> — bu iş
/// etiket altyapısı, genel bir yeniden düzenleme değil.</para>
/// </summary>
internal static class PanelLicenseScope
{
    /// <summary>Tipik kullanımda müşterinin tek lisansı var; birden fazlaysa
    /// ilk aktif olan seçilir.</summary>
    public static Task<Guid?> ResolveAsync(
        LicenseDbContext db, Guid customerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Licenses
            .Where(l => l.CustomerId == customerId
                && l.RevokedAt == null
                && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
