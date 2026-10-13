using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Satış senkronunun değişiklik akışı (<c>GET sales/changes</c>) — spec 2026-10-13 §4.4. Desen müşteri akışıyla aynı
/// (<c>LicensesWpfCustomersPullController.Changes</c>): imleç sunucunun rowversion'ı, istemci saati yok.
///
/// <para><b>Kaynaklar.</b> Devredilmiş oturumlar (<c>ReleasedAt</c> dolu); devredilmiş yayının siparişleri
/// (<c>SessionReleasedAt</c> dolu; SessionId NULL sipariş hiç girmez — yerelde SessionId NOT NULL FK, sayısı denetim
/// raporunda); kargo dosyaları (iki kipte — istemci yalnız mod On'da ve <c>disable</c> turunda uygular, o tur
/// dosyaların son hâlini görmeli). Canlı ve devrediliyor yayınlar ile siparişleri hiç verilmez: başka bilgisayar
/// canlı yayına dokunamaz (Y1). Akış moddan bağımsız çalışır.</para>
///
/// <para><b>Ufuk.</b> <c>MIN_ACTIVE_ROWVERSION()</c> sayfa başına bir kez okunur: commit olmamış bir işlem daha
/// KÜÇÜK bir rowversion almış olabilir; ufkun üstünü vermek, o işlem commit olunca imlecin gerisinde kalıp satırın
/// hiç inmemesi demek. Ufuk veritabanı genelidir; üç kaynak aynı ufukla okunur.</para>
///
/// <para><b>Geçersiz imleç sıfırlanır.</b> Eksi imleç ya da ufkun üstündeki imleç (veritabanı yedekten döndü ya da
/// kopyalandı) her sayfayı boş döndürür ve o bilgisayar sessizce değişiklik almayı bırakırdı; sayfa baştan verilir
/// ve <see cref="SalesChangesPage.CursorReset"/> bunu söyler.</para>
/// </summary>
public sealed class SalesChangesFeed
{
    /// <summary>Sayfa başına en çok öğe.</summary>
    public const int MaxTake = 500;

    private readonly LicenseDbContext _db;
    private readonly ILogger<SalesChangesFeed> _log;

    public SalesChangesFeed(LicenseDbContext db, ILogger<SalesChangesFeed> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Bir sayfa okur. Lisans sahipliği çağıran uçta denetlenmiş olmalı.</summary>
    public async Task<SalesChangesPage> ReadAsync(Guid licenseId, long afterSeq, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, MaxTake);
        var horizon = await _db.Database
            .SqlQueryRaw<long>("SELECT CAST(MIN_ACTIVE_ROWVERSION() AS bigint) AS [Value]")
            .SingleAsync(ct);

        var cursorReset = afterSeq < 0 || afterSeq >= horizon;
        if (cursorReset)
        {
            _log.LogWarning(
                "Satış değişiklik akışı: geçersiz imleç {AfterSeq} (ufuk {Horizon}), baştan veriliyor (lisans {LicenseId}) — veritabanı yedekten dönmüş ya da kopyalanmış olabilir",
                afterSeq, horizon, licenseId);
            afterSeq = 0;
        }

        var sessions = await _db.StreamSessions.AsNoTracking()
            .Where(s => s.LicenseId == licenseId
                        && s.ReleasedAt != null
                        && s.ChangeSeq > afterSeq && s.ChangeSeq < horizon)
            .OrderBy(s => s.ChangeSeq)
            .Take(take)
            .ToListAsync(ct);

        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == licenseId
                        && o.SessionReleasedAt != null
                        && o.SessionId != null
                        && o.ChangeSeq > afterSeq && o.ChangeSeq < horizon)
            .OrderBy(o => o.ChangeSeq)
            .Take(take)
            .ToListAsync(ct);

        var shipments = await _db.Shipments.AsNoTracking()
            .Where(s => s.LicenseId == licenseId && s.ChangeSeq > afterSeq && s.ChangeSeq < horizon)
            .OrderBy(s => s.ChangeSeq)
            .Take(take)
            .ToListAsync(ct);

        var selected = SalesChangesPageBuilder.Select(sessions, orders, shipments, take);

        // Yan liste (imleci etkilemez): yalnız sayfaya giren siparişler için, güncel hâliyle.
        var pageOrders = SalesChangesPageBuilder.OrdersOf(selected);
        var sessionIds = pageOrders.Select(o => o.SessionId!.Value).Distinct().ToList();
        var orderSessions = sessionIds.Count == 0
            ? new Dictionary<Guid, StreamSession>()
            : await _db.StreamSessions.AsNoTracking()
                .Where(s => s.LicenseId == licenseId && sessionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);
        var customers = pageOrders.Count == 0
            ? SalesCustomerDirectory.Empty
            : await SalesCustomerDirectory.LoadAsync(_db, licenseId, pageOrders.Select(o => o.CustomerId), ct);

        return SalesChangesPageBuilder.Assemble(afterSeq, cursorReset, selected, orderSessions, customers);
    }
}
