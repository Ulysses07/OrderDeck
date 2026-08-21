using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.ShopperPayments;

namespace OrderDeck.LicenseServer.Services.Shoppers;

/// <summary>Purge sonucunun özeti; admin paneline ve audit log'a gider.</summary>
public sealed record ShopperPurgeResult(
    int PaymentsScrubbed,
    int PdfsDeleted,
    int ProjectionsScrubbed,
    int DependentRowsDeleted)
{
    public string ToNote() =>
        $"{PaymentsScrubbed} ödeme temizlendi, {PdfsDeleted} dekont silindi, "
        + $"{ProjectionsScrubbed} yayıncı kopyası temizlendi, "
        + $"{DependentRowsDeleted} bağlı satır silindi.";
}

/// <summary>
/// Shopper'ın kişisel verisini kalıcı olarak siler. Admin panelinden ELLE
/// tetiklenir — zamanlanmış iş yok, silme kararı her zaman bir insanın.
///
/// İzlenen kural, <c>AdminCustomersController.Purge</c> ile aynı: bağlı
/// satırlar silinir, ana satır anonimleştirilerek BIRAKILIR. Shopper satırının
/// silinmemesinin sebebi <c>Payment.ShopperId</c> — ödeme kayıtları mali kayıt
/// olarak duruyor ve hangi kişiye ait olduğunu gösteren bağ kopmamalı; bağ
/// kopsa ödeme itirazında kaydı kimseye bağlayamayız.
///
/// Ödemelerde SİLİNMEYENLER ve gerekçesi: <c>PdfHash</c> ve
/// <c>MetadataHash</c>. Bunlar mükerrer dekont dedektörünün tek dayanağı
/// (bkz. <c>ShopperPaymentSubmissionService</c> adım 5-7); silinirlerse aynı
/// dekont yeniden yüklenip kabul edilebilir hale gelir — üstelik başka bir
/// yayıncıya karşı da. Tek yönlü özet oldukları için kimliğe geri
/// döndürülemezler; tutmanın meşru menfaati sahtekârlık önleme.
/// </summary>
public sealed class ShopperPurgeService
{
    private readonly LicenseDbContext _db;
    private readonly IShopperPaymentStorage _storage;
    private readonly ILogger<ShopperPurgeService> _log;

    public ShopperPurgeService(
        LicenseDbContext db,
        IShopperPaymentStorage storage,
        ILogger<ShopperPurgeService> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task<ShopperPurgeResult?> PurgeAsync(Guid shopperId, CancellationToken ct)
    {
        var shopper = await _db.Shoppers.FirstOrDefaultAsync(s => s.Id == shopperId, ct);
        if (shopper is null) return null;

        var now = DateTimeOffset.UtcNow;
        var phone = shopper.Phone;

        // 1. R2'deki dekont PDF'leri. Satırdaki anahtarı boşaltmadan ÖNCE
        //    silinmeli; sıra ters olsaydı nesneye giden tek referansı
        //    kaybeder, kovada yetim bırakırdık.
        var payments = await _db.Payments
            .Where(p => p.ShopperId == shopperId)
            .ToListAsync(ct);

        var pdfsDeleted = 0;
        foreach (var p in payments.Where(p => p.MediaObjectKey is not null))
        {
            try
            {
                await _storage.DeleteAsync(p.MediaObjectKey!, ct);
                pdfsDeleted++;
            }
            catch (Exception ex)
            {
                // Tek bir nesnenin silinememesi tüm purge'ü durdurmamalı;
                // kalan kişisel veriyi temizlemek daha acil. Yetim nesne
                // loglanıyor, kovadan elle temizlenebilir.
                _log.LogError(ex,
                    "[ShopperPurge] R2 nesnesi silinemedi: {Key} (shopper {ShopperId})",
                    p.MediaObjectKey, shopperId);
            }
        }

        // 2. Ödeme satırlarındaki kişisel alanlar. Tutar, tarih, referans no
        //    ve hash'ler kalıyor (sınıf özetindeki gerekçe).
        foreach (var p in payments)
        {
            p.PayerName = "";
            p.RecipientIban = null;
            p.RecipientName = null;
            p.MediaObjectKey = null;
            p.MediaContentType = null;
            p.PdfPurgedAt = now;
            p.UpdatedAt = now;
        }

        // 3. Yayıncıya açık olan sunucu kopyası. PurgedAt damgası şart:
        //    yayıncının KENDİ bilgisayarındaki SQLite satırı bu işlemle
        //    silinmiyor (WPF ingest'i var olan kaydı atlıyor, yalnız yeni
        //    ekliyor — ShopperRegistrationIngestService), dolayısıyla oradaki
        //    veri bir sonraki push'ta buraya geri akardı ve temizlik sessizce
        //    geri alınırdı. Damga o kapıyı kapatıyor
        //    (LicensesWpfCustomersSyncController).
        //
        //    Yayıncının diskindeki kopya bu PR'ın kapsamı dışında; WPF
        //    tarafında ayrı bir temizleme dalı gerekiyor ve o ancak yeni
        //    sürüm yayınıyla sahaya iner.
        var links = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId)
            .Select(l => new { l.LicenseId, l.Platform, l.Username })
            .ToListAsync(ct);
        var licenseIds = links.Select(l => l.LicenseId).Distinct().ToList();

        // Üçlü (LicenseId, Platform, Username) eşleşmesini sorguya gömmek
        // istemedim: yerel listeyle kurulan çok alanlı Any() SQL'e
        // çevrilemez, EF çalışma anında patlar. Bunun yerine kaba filtreyi
        // sunucuda (telefon VEYA ilgili lisanslar), kesin eşleşmeyi bellekte
        // yapıyoruz. Bir yayıncının müşteri listesi elle silme işlemi için
        // yeterince küçük.
        var linkKeys = links
            .Select(l => (l.LicenseId, l.Platform, l.Username))
            .ToHashSet();

        var projections = (await _db.WpfCustomerProjections
                .Where(c => c.Phone == phone || licenseIds.Contains(c.LicenseId))
                .ToListAsync(ct))
            .Where(c => c.Phone == phone
                || linkKeys.Contains((c.LicenseId, c.Platform, c.Username)))
            .ToList();

        foreach (var c in projections)
        {
            c.FullName = null;
            c.Phone = null;
            c.Address = null;
            c.PurgedAt = now;
            c.UpdatedAt = now;
        }

        // 4. Bağlı satırlar. Hepsi ya doğrudan kişisel veri taşıyor
        //    (kullanıcı adı, cihaz kimliği, IP) ya da yalnızca oturum
        //    açmaya yarıyor; hiçbirinin mali kayıt değeri yok.
        var devices = await _db.ShopperPushDevices.Where(d => d.ShopperId == shopperId).ToListAsync(ct);
        var tokens = await _db.ShopperRefreshTokens.Where(t => t.ShopperId == shopperId).ToListAsync(ct);
        var resetCodes = await _db.ShopperPasswordResetCodes.Where(r => r.ShopperId == shopperId).ToListAsync(ct);
        var supportRequests = await _db.ShopperSupportRequests.Where(r => r.ShopperId == shopperId).ToListAsync(ct);
        var linkRows = await _db.ShopperBroadcasterLinks.Where(l => l.ShopperId == shopperId).ToListAsync(ct);

        _db.ShopperPushDevices.RemoveRange(devices);
        _db.ShopperRefreshTokens.RemoveRange(tokens);
        _db.ShopperPasswordResetCodes.RemoveRange(resetCodes);
        _db.ShopperSupportRequests.RemoveRange(supportRequests);
        _db.ShopperBroadcasterLinks.RemoveRange(linkRows);

        var dependents = devices.Count + tokens.Count + resetCodes.Count
            + supportRequests.Count + linkRows.Count;

        // 5. Ödeme denetim kayıtları: satır mali izin parçası, yalnız istemci
        //    parmak izi gidiyor. SecurityDataRetentionJob'ın 90 günde yaptığı
        //    şeyin aynısını şimdi yapıyoruz.
        var submissionAudits = await _db.PaymentSubmissionAudits
            .Where(a => a.ShopperId == shopperId)
            .ToListAsync(ct);
        foreach (var a in submissionAudits)
        {
            a.IpAddress = "";
            a.UserAgent = "";
        }

        // 6. Shopper satırının kendisi. Telefon boşaltılıyor: kimliğin en
        //    güçlü tekil alanı o, ve boşaltılınca kişi isterse aynı numarayla
        //    sıfırdan kayıt olabiliyor. PasswordHash hiçbir doğrulamanın
        //    eşleyemeyeceği bir değere çekiliyor — bu hesaba bir daha
        //    girilemez.
        shopper.FullName = "[Silindi]";
        shopper.Phone = "";
        shopper.Address = "";
        shopper.Email = null;
        shopper.Tc = null;
        shopper.PasswordHash = "PURGED";
        shopper.SmsConsent = false;
        shopper.DeletedAt ??= now;
        shopper.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        return new ShopperPurgeResult(
            PaymentsScrubbed: payments.Count,
            PdfsDeleted: pdfsDeleted,
            ProjectionsScrubbed: projections.Count,
            DependentRowsDeleted: dependents);
    }
}
