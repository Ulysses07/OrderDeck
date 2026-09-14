using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.ShopperLinking;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App'in lokal Customer kayıtlarını LicenseServer'a periyodik bulk sync.
/// Server-side WpfCustomerProjection tablosuna upsert. Sipariş eşleşmesi:
/// shopper-app kullanıcısı bir yayıncıya bağlanırken (LicenseId, Platform,
/// Username) ile match yapılır; match retroactive olarak burada da çalıştırılır
/// (sync sırasında yeni eşleşen link.WpfCustomerId güncellenir).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[Route("api/v1/licenses/{licenseId:guid}/wpf-customers")]
public sealed class LicensesWpfCustomersSyncController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesWpfCustomersSyncController(LicenseDbContext db) => _db = db;

    public sealed record SyncItem(
        Guid Id,
        string Platform,
        string Username,
        string? FullName,
        string? Phone,
        string? Address,
        DateTimeOffset UpdatedAt);

    public sealed record SyncRequest(List<SyncItem> Customers);

    public sealed record SyncResponse(int Synced, int RetroactiveMatches);

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(Guid licenseId, [FromBody] SyncRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (req?.Customers is null || req.Customers.Count == 0)
            return Ok(new SyncResponse(0, 0));

        if (req.Customers.Count > 500)
            return Problem(title: "batch-too-large", statusCode: 400, detail: "Max 500 customers per batch");

        // Validate input items minimally
        foreach (var c in req.Customers)
        {
            if (string.IsNullOrWhiteSpace(c.Platform) || c.Platform.Length > 32)
                return Problem(title: "invalid-platform", statusCode: 400);
            if (string.IsNullOrWhiteSpace(c.Username) || c.Username.Length > 128)
                return Problem(title: "invalid-username", statusCode: 400);
        }

        var ids = req.Customers.Select(c => c.Id).ToList();
        var existing = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var synced = 0;
        foreach (var item in req.Customers)
        {
            if (existing.TryGetValue(item.Id, out var current))
            {
                // Silinmiş kayıt: kişisel alanlara DOKUNMA. Yayıncının kendi
                // bilgisayarındaki kopya silinmediği için (WPF ingest yalnızca
                // yeni satır ekliyor, var olanı güncellemiyor) bu satır her
                // push'ta ad/telefon/adresi geri getirirdi; silme tek bir
                // yayında yorum yazılmasıyla sessizce geri alınırdı.
                // Sayılıyor ama yazılmıyor: istemcinin watermark'ı ilerlesin,
                // aynı parti sonsuza kadar yeniden gönderilmesin.
                if (current.PurgedAt is not null)
                {
                    synced++;
                    continue;
                }

                current.Platform = item.Platform.ToLowerInvariant();
                current.Username = item.Username;
                current.FullName = item.FullName;
                current.Phone = item.Phone;
                current.Address = item.Address;
                current.UpdatedAt = item.UpdatedAt;
            }
            else
            {
                _db.WpfCustomerProjections.Add(new WpfCustomerProjection
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    Platform = item.Platform.ToLowerInvariant(),
                    Username = item.Username,
                    FullName = item.FullName,
                    Phone = item.Phone,
                    Address = item.Address,
                    UpdatedAt = item.UpdatedAt,
                });
            }
            synced++;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Purge, bu sync'in okumasından sonra tombstone yazmış olabilir.
            // Bayat kişisel veriyi reload edip tekrar denemek silmeyi geri alır;
            // istemci güncel satırı okuyup yeni bir paketle karar vermeli.
            return Problem(
                title: "sync-conflict",
                detail: "Müşteri verisi eşzamanlı değişti; güncel durumla yeniden deneyin.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Retroactive match: for newly-synced (or updated) projections, find any
        // ShopperBroadcasterLink with matching (LicenseId, Platform, Username) where
        // WpfCustomerId is null, and set it. Drive-by — avoids needing a cron job.
        //
        // Burada da telefon kanıtı şart; kural WpfCustomerLinkMatcher'da. Bu üçüncü
        // kopyanın kapısız kalması, kayıt ve katılma akışlarındaki düzeltmeleri
        // TAMAMEN boşa çıkarırdı: kanıt gelmediği için beklemede bırakılan bağlantı
        // bir sonraki WPF sync'inde buradan sessizce bağlanırdı.
        //
        // Bu aynı zamanda kurtarma yolu: yayıncı WPF'te müşterinin telefonunu
        // girdiğinde sync o telefonu buraya taşır ve beklemedeki bağlantı kendiliğinden
        // kurulur — ayrı bir onay ekranı gerekmiyor.
        // Ham payload'ı burada yeniden kullanma: yukarıda tombstone olduğu için
        // atlanan öğe hâlâ telefon taşıyabilir. İlk kayıttan sonra yalnız DB'de
        // gerçekten var olan ve PurgedAt IS NULL satırlar eşleştirmeye adaydır.
        var matchableProjections = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId
                && ids.Contains(p.Id)
                && p.PurgedAt == null)
            .ToListAsync(ct);

        var retroactiveMatches = 0;
        foreach (var projection in matchableProjections)
        {
            var unmatchedLinks = await _db.ShopperBroadcasterLinks
                .Where(l => l.LicenseId == licenseId
                    && l.WpfCustomerId == null
                    && l.LeftAt == null
                    && l.Platform == projection.Platform
                    && l.Username == projection.Username)
                .Select(l => new
                {
                    Link = l,
                    l.Shopper!.Phone,
                    l.Shopper.PhoneVerifiedAt,
                })
                .ToListAsync(ct);
            foreach (var row in unmatchedLinks)
            {
                if (!WpfCustomerLinkMatcher.PhoneProves(
                        projection.Phone, row.Phone, row.PhoneVerifiedAt)) continue;
                row.Link.WpfCustomerId = projection.Id;
                retroactiveMatches++;
            }
        }
        if (retroactiveMatches > 0)
            await _db.SaveChangesAsync(ct);

        return Ok(new SyncResponse(synced, retroactiveMatches));
    }
}
