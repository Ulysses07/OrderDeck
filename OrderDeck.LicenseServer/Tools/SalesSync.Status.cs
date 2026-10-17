using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// <c>sales-sync status --license &lt;guid&gt;</c> — salt okunur durum (spec 2026-10-13 §4.10, §6.2, §8.6). Hiçbir şey
/// yazmaz; <c>--apply</c> kullanım hatasıdır.
/// <list type="bullet">
///   <item><b>Lisans:</b> mod ve <c>SalesSyncModeChangedAt</c>; yapılandırılmış <c>SalesSync:MinClientVersion</c>
///   (ayrıştırılabiliyorsa yazıldığı gibi, değilse "tanımsız" — kapı kapalı, mod açılamaz); protokolsüz satış
///   isteğinin son izi (<c>SalesLegacyTraces</c>, mod kapısının 3. koşulu); lisans geneli kargo ayarı satırı.</item>
///   <item><b>Kopyalar:</b> lisansın bütün <c>WpfReplicas</c> satırları, son görülme anına göre en yenisi başta; son 14
///   günde görülenler işaretli (mod kapısının penceresi, §4.10-2). Her kopyada önceki kopya (geri yükleme zinciri),
///   istemci sürümü, protokol, <c>featureLevel</c>, görülen kip ve anları, son görülme, geri doldurma ve ödeme geçmişi
///   tamamlanma anları, geri doldurma birim ayrışması, parmak izi çakışması (disk klonu, §8.7) ve bildirdiği kargo
///   ayarı. Görülen kip Off ise kopya Off turunu bitirmiştir: eski sürüme dönüşün ön koşulunun kanıtı (§8.6).</item>
///   <item><b>Bildirilen farklı kargo değerleri</b>, son 14 günde görülen kopyalardan; <c>shipping set</c>'in kuru
///   koşusuyla aynı gruplama (§6.2).</item>
/// </list>
/// Makine adı ve donanım parmak izi veritabanından hiç okunmaz.
/// </summary>
public static partial class SalesSync
{
    /// <summary><c>status</c>'un gövdesi: argümanlar ayrıştırılmış, bağlantı ve <c>SalesSync</c> ayarları verilmiş.
    /// Testler gerçek SQL Server'a buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, StatusArgs args, SalesSyncOptions options, TextWriter output, TextWriter error,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.SalesSyncMode, l.SalesSyncModeChangedAt })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey okunmadı.");
            return 2;
        }

        var legacyAt = await db.SalesLegacyTraces.AsNoTracking()
            .Where(t => t.LicenseId == args.License)
            .Select(t => (DateTimeOffset?)t.LastLegacySalesPushAt)
            .SingleOrDefaultAsync(ct);
        var shipping = await LicenseShippingSettingsStore.ReadAsync(db, args.License, ct);
        // Makine adı ve parmak izi seçilmez: çıktıya ancak okunursa sızabilir.
        var replicas = (await db.WpfReplicas.AsNoTracking()
                .Where(r => r.LicenseId == args.License)
                .Select(r => new StatusReplica(
                    r.ReplicaId, r.PreviousReplicaId, r.ClientVersion, r.Protocol, r.FeatureLevel,
                    r.SeenMode, r.SeenModeAt, r.SeenModeOnAt, r.LastSeenAt,
                    r.BackfillCompletedAt, r.PaymentHistoryCompletedAt,
                    r.BackfillDivergedLocal, r.BackfillDivergedServer,
                    r.HardwareFingerprintConflictAt, r.ReportedThreshold, r.ReportedFee))
                .ToListAsync(ct))
            .OrderByDescending(r => r.LastSeenAt)
            .ThenBy(r => r.ReplicaId.ToString("N"), StringComparer.Ordinal)
            .ToList();
        var since = now - RecentWindow;
        var recent = replicas.Where(r => r.LastSeenAt >= since).ToList();

        output.WriteLine($"DURUM (salt okunur) — sales-sync status — lisans {args.License} — {Time(now)}");
        output.WriteLine($"Mod: {license.SalesSyncMode}, son değişim={Time(license.SalesSyncModeChangedAt)}");
        output.WriteLine($"MinClientVersion: {MinClientVersionText(options)}");
        output.WriteLine($"Son protokolsüz satış isteği (LastLegacySalesPushAt): {Time(legacyAt)}");
        WriteSettings(output, "Lisans geneli kargo ayarı", shipping);

        output.WriteLine($"Kopyalar: {replicas.Count} (son {RecentWindow.Days} günde görülen: {recent.Count})");
        foreach (var r in replicas)
        {
            output.WriteLine($"- Kopya {r.ReplicaId}: "
                + (r.LastSeenAt >= since ? $"son {RecentWindow.Days} günde görüldü" : $"{RecentWindow.Days} günden eski"));
            output.WriteLine($"    önceki kopya={r.PreviousReplicaId?.ToString() ?? "yok"}, "
                + $"sürüm={r.ClientVersion ?? "bilinmiyor"}, protokol={r.Protocol}, featureLevel={r.FeatureLevel}");
            output.WriteLine($"    görülen kip={r.SeenMode?.ToString() ?? "bildirmedi"}, kip zamanı={Time(r.SeenModeAt)}, "
                + $"ilk On={Time(r.SeenModeOnAt)}");
            output.WriteLine($"    son görülme={Time(r.LastSeenAt)}, geri doldurma tamam={Time(r.BackfillCompletedAt)}, "
                + $"ödeme geçmişi tamam={Time(r.PaymentHistoryCompletedAt)}");
            output.WriteLine($"    geri doldurma ayrışması: yerel kazanan={r.BackfillDivergedLocal}, "
                + $"sunucu kazanan={r.BackfillDivergedServer}");
            output.WriteLine("    parmak izi çakışması="
                + (r.HardwareFingerprintConflictAt is { } conflict ? $"VAR ({Time(conflict)})" : "yok"));
            output.WriteLine($"    bildirilen kargo: {Values(r.ReportedThreshold, r.ReportedFee)}");
        }

        WriteReportedShipping(output, recent.Select(r => new ReportedShipping(r.ReplicaId, r.ReportedThreshold, r.ReportedFee)));
        return 0;
    }

    /// <summary>Kopya satırının <c>status</c>'ta basılan alanları — makine adı ve parmak izi bilerek yok.</summary>
    private sealed record StatusReplica(
        Guid ReplicaId,
        Guid? PreviousReplicaId,
        string? ClientVersion,
        byte Protocol,
        byte FeatureLevel,
        SalesSyncMode? SeenMode,
        DateTimeOffset? SeenModeAt,
        DateTimeOffset? SeenModeOnAt,
        DateTimeOffset LastSeenAt,
        DateTimeOffset? BackfillCompletedAt,
        DateTimeOffset? PaymentHistoryCompletedAt,
        int BackfillDivergedLocal,
        int BackfillDivergedServer,
        DateTimeOffset? HardwareFingerprintConflictAt,
        decimal? ReportedThreshold,
        decimal? ReportedFee);

    /// <summary>Kapının gördüğü alt sınır: ayrıştırılabiliyorsa yazıldığı gibi (kırpılmış); boşsa ya da
    /// ayrıştırılamıyorsa "tanımsız" — <see cref="SalesSyncOptions.ParseMinClientVersion"/> null döner, kapı kapalı.</summary>
    private static string MinClientVersionText(SalesSyncOptions options)
    {
        if (options.ParseMinClientVersion() is not null) return options.MinClientVersion!.Trim();
        return string.IsNullOrWhiteSpace(options.MinClientVersion)
            ? "tanımsız"
            : "tanımsız (yazılı değer ayrıştırılamıyor)";
    }
}
