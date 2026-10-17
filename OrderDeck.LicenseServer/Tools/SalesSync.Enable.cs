using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// <c>sales-sync enable --license &lt;guid&gt; [--accept-shipment &lt;guid&gt;]… [--apply]</c> — mod kapısı (mühendislik
/// kararı M1), spec 2026-10-13 §4.10, §4.6, §7.4, §8.1 adım 5–7, §8.3, §15-5, -29, -53.
///
/// <para><b>Kuru koşu</b> (varsayılan) hiçbir şey yazmaz: <see cref="SalesModeGate"/>'in altı koşulunu tutuyor ya da
/// tutmuyor diye, sayıları ve Id'leriyle, ardından bilgi raporunu (7) basar. Çıktıda ad, kullanıcı adı, mesaj, müşteri
/// ve makine adı yok.</para>
///
/// <para><b><c>--accept-shipment &lt;id&gt;</c></b> (tekrarlanabilir): Burak dosyayı açan PC'de baktıktan sonra
/// "saklanan tutar doğru, bağlar eksik" der. Kabul edilen dosya 6. koşulun iki parçasından da muaftır (kaynak ve
/// fark) ve türetilmez: <c>AmountDerivedAt</c> boş kalır, saklanan tutar korunur; K7, onarım işinin (c) adımı ve
/// <c>attach-shipment</c> türetilmemiş dosyaya zaten dokunmaz (S2), havuz ve <c>shipment-decision</c> onu saklanan
/// tutarıyla sayar. Kabul edilen Id bu lisansın Shipped olmayan bir dosyası olmalı; değilse 2.</para>
///
/// <para><b><c>--apply</c></b> bütün koşulları yeniden değerlendirir (önceki bir kuru koşuya güvenmez); biri tutmazsa
/// hiçbir şey yazmadan 1 döner. Tutarsa tek işlemde:
/// <list type="number">
///   <item>Koşullu UPDATE: <c>SalesSyncMode = 1</c>, <c>SalesSyncModeChangedAt</c> = şimdi, yalnız mod hâlâ 0 ise.
///   Satır bulunmazsa (eşzamanlı bir <c>enable</c>) geri alınır, 1.</item>
///   <item>Shipped olmayan dosyalar izlenerek, ardından bağlı siparişler yüklenir (<see cref="ShipmentAmountDeriver"/>'ın
///   jeton sırası). 6. koşul bu satırlarla yeniden değerlendirilir: değerlendirme ile işlem arasında yeni ya da
///   değişmiş bir dosya koşulu bozuyorsa geri alınır, 1. Kabul edilmemiş her dosya türetilir:
///   <c>CumulativeAmount</c> = türetilen, <c>AmountDerivedAt</c> = şimdi, <c>Version + 1</c>, <c>UpdatedAt</c>. Kabul
///   edilen dosya daha önceki bir mod On döneminden türetme bayrağı taşıyorsa (<c>disable</c> bayrağı silmez) bayrak
///   kaldırılır; aksi hâlde K7 ve <c>attach-shipment</c> onu yine türetirdi ve kabulün anlamı kaybolurdu.</item>
///   <item>Commit. Bir dosya bu arada yazıldıysa (<c>shipments/sync</c>, sürüm jetonu) DbUpdateConcurrencyException:
///   işlem geri alınır, 1 — yeniden çalıştır.</item>
/// </list>
/// Kararlar (Status, HeldAt, ShippedAt) ve Shipped dosyalar hiç değişmez. Kalan dar yarış: mod Off'u işlemden önce
/// okumuş bir <c>orders/sync</c> bağlı bir siparişi türetmenin okumasından sonra değiştirirse türetilen tutar bayat
/// kalabilir; mod Off'taki yazım dosyayı türetmez ve sürümünü artırmaz. Onarım işinin (c) adımı mod On'da türetilmiş
/// açık dosyayı yeniden türetir (§4.9).</para>
///
/// <para><b>Zaten açık:</b> mesaj, hiçbir şey yazılmaz, 0 (kuru koşu ve <c>--apply</c>).</para>
/// </summary>
public static partial class SalesSync
{
    /// <summary><c>enable</c>'ın ayrıştırılmış argümanları. Eşitlik kabul listesini içerikle (sırasıyla)
    /// karşılaştırır.</summary>
    /// <param name="AcceptedShipments">Tekrarsız, ilk görülme sırasında.</param>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record EnableArgs(Guid License, IReadOnlyList<Guid> AcceptedShipments, bool Apply) : SalesSyncArgs
    {
        public bool Equals(EnableArgs? other) =>
            other is not null && License == other.License && Apply == other.Apply
            && AcceptedShipments.SequenceEqual(other.AcceptedShipments);

        public override int GetHashCode() => HashCode.Combine(License, Apply, AcceptedShipments.Count);
    }

    /// <summary><c>enable</c>'ın gövdesi: argümanlar ayrıştırılmış, bağlantı ve <c>SalesSync</c> ayarları verilmiş.
    /// Testler gerçek SQL Server'a buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, EnableArgs args, SalesSyncOptions options, TextWriter output, TextWriter error,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.SalesSyncMode, l.SalesSyncModeChangedAt })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }

        var header =
            $"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — sales-sync enable — lisans {args.License} — {Time(now)}";
        if (license.SalesSyncMode != SalesSyncMode.Off)
        {
            output.WriteLine(header);
            output.WriteLine($"Mod zaten açık ({license.SalesSyncMode}, son değişim={Time(license.SalesSyncModeChangedAt)}): "
                + "yapılacak bir şey yok, hiçbir şey yazılmadı.");
            return 0;
        }

        var accepted = args.AcceptedShipments.ToHashSet();
        var acceptable = await SalesModeGate.OpenShipments(db, args.License).AsNoTracking()
            .Where(s => accepted.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);
        var invalid = args.AcceptedShipments.Except(acceptable).ToList();
        if (invalid.Count > 0)
        {
            error.WriteLine("--accept-shipment bu lisansın Shipped olmayan bir dosyası olmalı; olmayan: "
                + string.Join(", ", invalid.Select(id => id.ToString()).Order(StringComparer.Ordinal))
                + ". Hiçbir şey yazılmadı.");
            return 2;
        }

        output.WriteLine(header);
        output.WriteLine("Mod: Off");
        var gate = await SalesModeGate.EvaluateAsync(db, args.License, options, accepted, now, ct)
            ?? throw new InvalidOperationException("Lisans değerlendirme sırasında kayboldu.");
        WriteGate(output, gate);

        if (!args.Apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }
        if (!gate.Passes)
        {
            error.WriteLine($"Mod açılmadı: koşul {string.Join(", ", gate.Failing)} tutmuyor; hiçbir şey yazılmadı.");
            return 1;
        }

        return await ApplyEnableAsync(db, args.License, accepted, now, output, error, ct);
    }

    /// <summary><c>--apply</c>'ın işlemi; bkz. sınıf dokümanı.</summary>
    private static async Task<int> ApplyEnableAsync(
        LicenseDbContext db, Guid licenseId, IReadOnlySet<Guid> accepted, DateTimeOffset now, TextWriter output,
        TextWriter error, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Licenses
            .Where(l => l.Id == licenseId && l.SalesSyncMode == SalesSyncMode.Off)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.SalesSyncMode, SalesSyncMode.On)
                .SetProperty(l => l.SalesSyncModeChangedAt, now), ct);
        if (updated == 0)
        {
            await transaction.RollbackAsync(ct);
            error.WriteLine("Mod bu arada değişti (başka bir enable?); hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }

        // Önce dosyalar (izlenen, sürüm jetonuyla), sonra bağlı siparişler — ShipmentAmountDeriver sırası.
        var shipments = await SalesModeGate.OpenShipments(db, licenseId).ToListAsync(ct);
        var linked = await SalesModeGate.LinkedOrdersAsync(db, licenseId, shipments.Select(s => s.Id).ToList(), ct);
        var backfill = await SalesModeGate.ReplicaBackfillAsync(db, licenseId, ct);
        var checks = SalesModeGate.CheckShipments(shipments, linked, backfill, accepted);
        var broken = checks.Where(c => !c.Passes).Select(c => c.Id).ToList();
        if (broken.Count > 0)
        {
            await transaction.RollbackAsync(ct);
            error.WriteLine($"Kargo dosyaları bu arada değişti: {broken.Count} dosya 6. koşulu artık tutmuyor ("
                + string.Join(", ", broken.Select(id => id.ToString()).Order(StringComparer.Ordinal))
                + "); hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }

        var facts = linked.Select(o => o.ToLabelFacts()).ToList();
        var derived = new List<Guid>();
        var changed = 0;
        var totalDiff = 0m;
        var unflagged = new List<Guid>();
        foreach (var shipment in shipments)
        {
            if (accepted.Contains(shipment.Id))
            {
                if (shipment.AmountDerivedAt is null) continue;
                shipment.AmountDerivedAt = null;
                shipment.UpdatedAt = now;
                shipment.Version++;
                unflagged.Add(shipment.Id);
                continue;
            }

            var amount = LabelRules.DerivedShipmentAmount(shipment.Id.ToString("N"), facts);
            if (amount != shipment.CumulativeAmount)
            {
                changed++;
                totalDiff += shipment.CumulativeAmount - amount;
            }
            shipment.CumulativeAmount = amount;
            shipment.AmountDerivedAt = now;
            shipment.UpdatedAt = now;
            shipment.Version++;
            derived.Add(shipment.Id);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            error.WriteLine("Bir kargo dosyası bu arada yazıldı; hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }
        await transaction.CommitAsync(ct);

        output.WriteLine($"UYGULANDI: mod On, SalesSyncModeChangedAt={Time(now)}");
        output.WriteLine($"Türetilen dosya: {derived.Count} (tutarı değişen: {changed}, "
            + $"toplam fark (saklanan − türetilen)={Amount(totalDiff)})");
        WriteIds(output, derived);
        var acceptedIds = accepted.ToList();
        output.WriteLine($"Kabul edilen, türetilmeyen dosya: {acceptedIds.Count} (türetme bayrağı kaldırılan: {unflagged.Count})");
        WriteIds(output, acceptedIds);
        return 0;
    }

    // ── Çıktı ────────────────────────────────────────────────────────────────

    private static void WriteGate(TextWriter output, SalesModeGate.Result gate)
    {
        var accepted = gate.ShipmentLinks.Accepted;
        output.WriteLine($"Kabul edilen dosya (--accept-shipment): {accepted.Count}");
        WriteIds(output, accepted.Select(s => s.Id));

        var versions = gate.ClientVersions;
        output.WriteLine($"Koşul 1 — istemci sürümü: {Verdict(versions.Passes)}");
        output.WriteLine($"   MinClientVersion: {versions.MinClientVersion ?? "tanımsız"}");
        output.WriteLine($"   son {SalesModeGate.RecentWindow.Days} günde görülen etkin etkinleştirme: "
            + $"{versions.RecentActivations}; alt sınırın altında ya da sürümsüz: "
            + (versions.MinClientVersion is null ? "karşılaştırılamadı" : versions.Old.Count.ToString()));
        foreach (var a in versions.Old.OrderBy(a => a.Id.ToString(), StringComparer.Ordinal))
            output.WriteLine($"      {a.Id} (sürüm={a.AppVersion ?? "bilinmiyor"})");

        var replicas = gate.Replicas;
        output.WriteLine($"Koşul 2 — kopyalar: {Verdict(replicas.Passes)}");
        output.WriteLine($"   son {SalesModeGate.RecentWindow.Days} günde görülen kopya: {replicas.Recent}");
        WriteReasons(output, $"protokol {SalesEnvelope.V2} değil", replicas.WrongProtocol);
        WriteReasons(output, $"featureLevel < {SalesModeGate.RequiredFeatureLevel}", replicas.LowFeatureLevel);
        WriteReasons(output, "geri doldurma tamamlanmamış", replicas.BackfillIncomplete);
        WriteReasons(output, "ödeme geçmişi tamamlanmamış", replicas.PaymentHistoryIncomplete);
        WriteReasons(output, "parmak izi çakışması", replicas.FingerprintConflict);

        var legacy = gate.Legacy;
        output.WriteLine($"Koşul 3 — eski sürüm izi: {Verdict(legacy.Passes)}");
        output.WriteLine($"   son protokolsüz satış isteği: {Time(legacy.LastLegacySalesPushAt)}; "
            + $"{SalesModeGate.LegacyQuietPeriod.Days} günden eski olmalı (sınır {Time(legacy.QuietSince)})");

        var shipping = gate.Shipping;
        output.WriteLine($"Koşul 4 — lisans geneli kargo ayarı: {Verdict(shipping.Passes)}");
        output.WriteLine(shipping.Settings is { } s
            ? $"   sürüm {s.Version}, {Values(s.FreeShippingThreshold, s.Fee)}, yazan={s.UpdatedBy ?? "boş"}"
            : "   satır yok");

        var audit = gate.Audit;
        output.WriteLine($"Koşul 5 — sales-audit: {Verdict(audit.Passes)}");
        output.WriteLine($"   {SalesAuditReport.StaleAfter.TotalHours:0} saatten eski devredilmemiş oturum: "
            + audit.StaleSessions.Count);
        foreach (var session in audit.StaleSessions)
            output.WriteLine($"      {session.Id} ({(session.Live ? "canlı" : "devrediliyor")})");
        output.WriteLine($"   SessionReleasedAt'i eksik sipariş: {audit.OrdersMissingReleaseStamp}");

        var links = gate.ShipmentLinks;
        output.WriteLine($"Koşul 6 — kargo bağları: {Verdict(links.Passes)}");
        output.WriteLine($"   Shipped olmayan dosya: {links.Shipments.Count} (kabul edilen: {accepted.Count})");
        WriteReasons(output, "OriginReplicaId'si boş", links.MissingOrigin.Select(c => c.Id).ToList());
        output.WriteLine($"   kaynak kopyanın geri doldurması tamamlanmamış: {links.OriginNotBackfilled.Count}");
        foreach (var c in links.OriginNotBackfilled)
            output.WriteLine($"      {c.Id} (kopya {c.OriginReplicaId})");
        output.WriteLine($"   açıklanamayan fark: {links.Unexplained.Count}");
        foreach (var c in links.Unexplained) output.WriteLine("      " + ShipmentLine(c));

        var info = gate.Information;
        output.WriteLine("Rapor 7 (bilgi, koşul değil):");
        output.WriteLine($"   açıklanan farkı olan dosya (Y5): {info.Explained.Count}, toplam fark={Amount(info.ExplainedDiffTotal)}");
        foreach (var c in info.Explained) output.WriteLine("      " + ShipmentLine(c));
        if (info.Threshold is { } threshold)
        {
            output.WriteLine($"   eşik {Amount(threshold)}: türetmeyle altına inen {info.FallBelowThreshold.Count}, "
                + $"eşiğe çıkan {info.RiseToThreshold.Count}");
            foreach (var c in info.FallBelowThreshold) output.WriteLine("      altına inen: " + ShipmentLine(c));
            foreach (var c in info.RiseToThreshold) output.WriteLine("      eşiğe çıkan: " + ShipmentLine(c));
        }
        else
        {
            output.WriteLine("   eşik tanımsız: eşik karşılaştırması yapılmadı");
        }
        output.WriteLine($"   kargo bağı bilinmeyen sipariş: {info.OrdersWithUnknownLink}; bağsız, iptalsiz, geçici yedek "
            + $"olmayan: {info.UnknownLinkCandidates}, tutar={Amount(info.UnknownLinkCandidateAmount)}");
        output.WriteLine($"   geri doldurma birim ayrışması olan kopya: {info.BackfillDivergence.Count}");
        foreach (var d in info.BackfillDivergence)
            output.WriteLine($"      kopya {d.ReplicaId}: yerel kazanan={d.Local}, sunucu kazanan={d.Server}");

        output.WriteLine(gate.Passes
            ? "Sonuç: bütün koşullar tutuyor."
            : $"Sonuç: koşul {string.Join(", ", gate.Failing)} tutmuyor; mod açılamaz.");
    }

    private static string Verdict(bool passes) => passes ? "TUTUYOR" : "TUTMUYOR";

    /// <summary>Bir neden: sayı ve (varsa) Id'ler bir alt satırda.</summary>
    private static void WriteReasons(TextWriter output, string reason, IReadOnlyCollection<Guid> ids)
    {
        output.WriteLine($"   {reason}: {ids.Count}");
        var list = ids.Select(id => id.ToString()).Order(StringComparer.Ordinal).ToList();
        if (list.Count > 0) output.WriteLine("      " + string.Join(", ", list));
    }

    private static string ShipmentLine(SalesModeGate.ShipmentCheck c) =>
        $"{c.Id}: saklanan={Amount(c.Stored)}, türetilen={Amount(c.Derived)}, fark={Amount(c.Diff)}, "
        + $"bağlı iptal={Amount(c.CancelledLinked)}, bağlı sipariş={c.LinkedOrders}";

    private static string Amount(decimal value) => Amount((decimal?)value);

    /// <summary><c>enable</c>: <c>--license</c> zorunlu; <c>--accept-shipment</c> tekrarlanabilir, her değer GUID,
    /// tekrarlar ilk görüldükleri sırada bir kez kalır; <c>--apply</c> isteğe bağlı. Öbür kurallar
    /// <see cref="TryReadOptions"/>'taki gibi katı.</summary>
    private static bool TryParseEnable(string[] args, int start, [NotNullWhen(true)] out SalesSyncArgs? parsed)
    {
        parsed = null;
        var accepted = new List<Guid>();
        var seen = new HashSet<Guid>();
        var rest = new List<string>();
        for (var i = start; i < args.Length; i++)
        {
            if (args[i] != "--accept-shipment")
            {
                rest.Add(args[i]);
                continue;
            }
            if (!TryValue(args, i, out var text) || !Guid.TryParse(text, out var id)) return false;
            if (seen.Add(id)) accepted.Add(id);
            i++;
        }

        if (!TryReadOptions(rest.ToArray(), 0, ["--license"], ["--apply"], out var values, out var flags)
            || !TryGuidOption(values, "--license", out var license))
            return false;

        parsed = new EnableArgs(license, accepted, flags.Contains("--apply"));
        return true;
    }
}
