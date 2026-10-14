using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll sales-audit --license &lt;guid&gt;
/// Yayın ve etiket senkronunun salt okunur raporu (spec 2026-10-13 §4.10): takılı oturumlar, akışa girmeyen ya da
/// damgası eksik siparişler, kopyaların kargo ayarı, tek fiyat ihlalleri, kapsam başına birden çok düşüm, kaynağı
/// bilinmeyen dosya ve siparişler, geri doldurma ayrışması. Kalemler ve tanımları <see cref="SalesAuditReport"/>'ta.
/// <b>Hiçbir şey yazmaz</b>: kuru/uygulama ayrımı yoktur, <c>--apply</c> tanınmayan argümandır. Bilgisayarlar açıkken
/// de koşabilir; sayılar okunduğu anın görüntüsüdür.
///
/// <para>Çıkış kodları: 0 rapor yazıldı (bulgu olsa da); 2 kullanım, yapılandırma ya da bilinmeyen lisans.</para>
///
/// <para>Argümanlar katı (<see cref="GroupCustomers"/> deseni): tanınmayan argüman, değeri eksik ya da GUID olmayan
/// <c>--license</c>, ikinci kez verilen <c>--license</c> ya da hiç <c>--license</c> yok → 2, yapılandırmaya ve
/// veritabanına dokunmadan. Yazım hatalı bir lisans Id'si "her şey temiz" diye sıfırlı bir rapor üretmesin diye
/// bilinmeyen lisans da 2'dir.</para>
///
/// Bağlantı dizesi web host'unkiyle aynı kaynaktan (ConnectionStrings:LicenseDb, ortam değişkeni dahil). Çıktıda
/// kişisel veri YOK — yalnız sayılar, tutarlar ve Id'ler.
/// </summary>
public static class SalesAudit
{
    private const string Usage = "Kullanım: sales-audit --license <guid>";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var license))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var conn = config.GetConnectionString("LicenseDb");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.Error.WriteLine("ConnectionStrings:LicenseDb bulunamadı.");
            return 2;
        }

        var options = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(conn)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
        await using var db = new LicenseDbContext(options);
        return await RunAsync(db, license, Console.Out, Console.Error, CancellationToken.None);
    }

    /// <summary>Komutun gövdesi: argümanlar ayrıştırılmış, bağlantı nesnesi kurulmuş. Testler gerçek SQL Server'a
    /// buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, Guid license, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var report = await SalesAuditReport.BuildAsync(db, license, now, ct);
        if (report is null)
        {
            error.WriteLine($"Lisans bulunamadı: {license}. Hiçbir şey okunmadı.");
            return 2;
        }

        Write(output, license, now, report);
        return 0;
    }

    /// <summary>args[0] komut adıdır. Bkz. sınıf dokümanı: katı ayrıştırma. <c>public</c>: geçerli argümanlı yol
    /// yapılandırmaya ve veritabanına gider; kabul edilen biçimler yalnız saf fonksiyon olarak sınanabilir.</summary>
    public static bool TryParseArgs(string[] args, out Guid license)
    {
        Guid? parsed = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] != "--license" || parsed is not null
                || i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var value))
            {
                license = Guid.Empty;
                return false;
            }
            parsed = value;
            i++;
        }
        license = parsed ?? Guid.Empty;
        return parsed.HasValue;
    }

    private static void Write(TextWriter o, Guid license, DateTimeOffset now, SalesAuditReport.Result r)
    {
        var inv = CultureInfo.InvariantCulture;
        o.WriteLine($"SATIŞ SENKRONU DENETİMİ (salt okunur) — lisans {license} — {now.ToString("u", inv)}");

        o.WriteLine($"1. 24 saatten eski canlı ya da devrediliyor oturum: {r.StaleSessions.Count}");
        foreach (var s in r.StaleSessions.Take(SalesAuditReport.MaxListedIds))
            o.WriteLine($"   {s.Id} {(s.Live ? "canlı" : "devrediliyor")} sahip={(s.OwnerReplicaId?.ToString() ?? "yok (miras)")}");
        More(o, r.StaleSessions.Count);

        o.WriteLine($"2. SessionId'si boş sipariş: {r.OrdersWithoutSession}");
        Ids(o, r.OrdersWithoutSessionSample, r.OrdersWithoutSession);

        o.WriteLine($"3. Projeksiyonu olmayan CustomerId: {r.CustomerIdsWithoutProjection.Count}");
        Ids(o, r.CustomerIdsWithoutProjection, r.CustomerIdsWithoutProjection.Count);
        o.WriteLine($"   CustomerId'si GUID olmayan sipariş: {r.OrdersWithNonGuidCustomer}");

        o.WriteLine($"4. Oturumu devredilmiş ama SessionReleasedAt'i boş sipariş: {r.OrdersMissingReleaseStamp}");

        var shipping = r.DistinctReportedShipping switch
        {
            0 => "bildiren yok",
            1 => "hepsi aynı",
            _ => $"FARKLI ({r.DistinctReportedShipping} farklı değer)",
        };
        o.WriteLine($"5. Kopyaların bildirdiği kargo ayarı: {shipping}; bildirmeyen ya da kapalı bildiren kopya: {r.ReplicasWithoutShipping}");
        foreach (var s in r.ReportedShipping)
            o.WriteLine($"   {s.ReplicaId} eşik={Amount(s.Threshold)} ücret={Amount(s.Fee)}");

        o.WriteLine($"6. Tek fiyat ihlali (DM5): {r.PriceViolations.Sum(v => v.Codes)} kod, {r.PriceViolations.Count} yayında");
        foreach (var v in r.PriceViolations.Take(SalesAuditReport.MaxListedIds))
            o.WriteLine($"   {v.SessionId} kod={v.Codes}");
        More(o, r.PriceViolations.Count);

        o.WriteLine($"7. Birden çok geri alınmamış düşümü olan müşteri satırı × kapsam: {r.DuplicateDeductions.Count}");
        foreach (var d in r.DuplicateDeductions.Take(SalesAuditReport.MaxListedIds))
            o.WriteLine($"   {d.WpfCustomerId} düşüm={d.TransactionIds.Count}: {string.Join(", ", d.TransactionIds)}");
        More(o, r.DuplicateDeductions.Count);

        o.WriteLine($"8. Shipped olmayan, OriginReplicaId'si boş dosya: {r.ShipmentsWithoutOrigin.Count}");
        Ids(o, r.ShipmentsWithoutOrigin, r.ShipmentsWithoutOrigin.Count);
        o.WriteLine($"   Shipped olmayan, tutarı > 0 ve bağlı siparişi olmayan dosya: {r.ShipmentsWithAmountWithoutLinks.Count}");
        Ids(o, r.ShipmentsWithAmountWithoutLinks, r.ShipmentsWithAmountWithoutLinks.Count);

        o.WriteLine($"9. Kargo bağı bilinmeyen (OriginReplicaId'si boş) sipariş: {r.OrdersWithUnknownLink}; "
            + $"bunlardan bağsız, iptalsiz, geçici yedek olmayan (attach-shipment adayı olmaz): "
            + $"{r.UnknownLinkCandidates}, tutar {Amount(r.UnknownLinkCandidateAmount)}");

        o.WriteLine($"10. Geri doldurma birim ayrışması: yerel kazanan {r.BackfillDivergence.Sum(d => d.Local)}, "
            + $"sunucu kazanan {r.BackfillDivergence.Sum(d => d.Server)}");
        foreach (var d in r.BackfillDivergence)
            o.WriteLine($"   {d.ReplicaId} yerel={d.Local} sunucu={d.Server}");

        o.WriteLine($"11. Aynı ReplicaId'ye iki donanım parmak izi (disk klonu): {r.FingerprintConflicts.Count}");
        foreach (var id in r.FingerprintConflicts)
            o.WriteLine($"   {id}");

        var n = 12;
        foreach (var (item, logText) in SalesAuditReport.LogOnlyItems)
            o.WriteLine($"{n++}. {item}: veritabanında iz yok, ölçülmüyor — sunucu günlüğünde \"{logText}\" satırları");

        string Amount(decimal? value) => value?.ToString("0.00", inv) ?? "boş";
    }

    private static void Ids<T>(TextWriter o, IReadOnlyList<T> sample, int total)
    {
        if (sample.Count == 0) return;
        o.WriteLine($"   {string.Join(", ", sample.Take(SalesAuditReport.MaxListedIds))}");
        More(o, total);
    }

    private static void More(TextWriter o, int total)
    {
        if (total > SalesAuditReport.MaxListedIds)
            o.WriteLine($"   … ve {total - SalesAuditReport.MaxListedIds} tane daha");
    }
}
