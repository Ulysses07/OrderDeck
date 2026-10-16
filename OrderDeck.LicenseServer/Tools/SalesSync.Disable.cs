using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// <c>sales-sync disable --license &lt;guid&gt; [--apply]</c> — acil kapatma anahtarı, spec 2026-10-13 §4.10
/// "<c>disable</c>", §3.1 <c>Shipments.OriginReplicaId</c>, §8.5 (On → Off), §8.6, §8.7, M15, M19, §12 risk 15, §15-38.
///
/// <para><b>Ne yapar.</b> Lisansın modunu 0 yapar. Damgalar kalır ve bayat push'a karşı komut kararını korumaya devam
/// eder (K2); birim damgalarına, siparişlere, oturumlara ve ödeme kiralarına dokunulmaz. Aynı işlemde Shipped olmayan
/// her dosyanın <c>OriginReplicaId</c>'si, mod Off'taki tek yazıcısı olacak kopyaya, yani <b>bağlı etiketlerin
/// sahibine</b> göre yeniden yazılır; kural <see cref="SalesModeDisable"/>'da (oturum sahibi, yoksa siparişin kaynağı;
/// geri yükleme zinciri en yeni kopyaya; karışık dosya bağlı tutarı büyük olana, eşitlikte <c>"N"</c> metni ordinal
/// küçük ReplicaId'ye). Shipped dosya kapsam dışıdır. <c>SalesSyncModeChangedAt</c> şimdi yazılır: mod On dönemini hiç
/// görmemiş Ws de onu yerelde son gördüğünden yeni bulup Off turunu koşar (§8.5).</para>
///
/// <para><b>Kuru koşu</b> (varsayılan) hiçbir şey yazmaz; sayıları (yeniden yazılacak, zaten doğru, sahibi
/// bilinmeyen), yeniden yazılacak dosyaları (<c>Id: eski → yeni</c>) ve karışık dosyaları kopya başına bağlı tutarla
/// basar. Karışık dosyada küçük payın sahibinin bağlı etiketleri risk 15'teki durumda kalır; Burak isterse
/// <c>--apply</c>'dan önce bu dosyaların kararlarını (kargola, alıcı ödesin) verdirir. Çıktıda yalnız Id, sayı ve tutar
/// var; müşteri, ad, metin ve makine adı yok.</para>
///
/// <para><b><c>--apply</c></b>, tek işlemde:
/// <list type="number">
///   <item>Koşullu UPDATE: <c>SalesSyncMode = 0</c>, <c>SalesSyncModeChangedAt</c> = şimdi, yalnız mod hâlâ 1 ise.
///   Satır bulunmazsa (eşzamanlı bir <c>disable</c>) geri alınır, 1. Lisans satırının kilidi işlem sonuna kadar
///   tutulur: modu bundan sonra okuyan komut ve kira istekleri commit'i bekler ve Off görür.</item>
///   <item>Shipped olmayan dosyalar izlenerek yüklenir ve plan bu satırlarla (önceki bir kuru koşuya güvenmeden)
///   kurulup basılır. Sahibi değişen dosyada <c>OriginReplicaId</c> = yeni sahip, <c>Version + 1</c>,
///   <c>UpdatedAt</c> = şimdi; <c>ChangeSeq</c> (rowversion) ilerler, her istemci yeni değeri <c>disable</c> turunun son
///   akışında alır (§4.4, §8.5-1). Kararlar, tutar, türetme ve komut damgaları değişmez.</item>
///   <item>Commit. Bir dosya bu arada yazıldıysa (sürüm jetonu) DbUpdateConcurrencyException: işlem, mod yazımı dahil,
///   geri alınır, 1 — yeniden çalıştır.</item>
/// </list>
/// Kalan dar yarış: modu bu işlemden önce okumuş bir komut (<c>attach-shipment</c>) planın okumasından sonra yeniden
/// yazılmayan bir dosyaya başka kopyanın etiketini bağlayabilir ya da yeni dosya açabilir; yeniden yazılan dosyaya
/// dokunursa sürüm jetonu yakalar. Böyle bir dosya <c>sales-audit</c>'te görünür.</para>
///
/// <para><b>Zaten kapalı:</b> mesaj, hiçbir şey yazılmaz, 0 (kuru koşu ve <c>--apply</c>). Bu yüzden ikinci koşu
/// etkisizdir.</para>
/// </summary>
public static partial class SalesSync
{
    /// <summary><c>disable</c>'ın ayrıştırılmış argümanları.</summary>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record DisableArgs(Guid License, bool Apply) : SalesSyncArgs;

    /// <summary><c>disable</c>'ın gövdesi: argümanlar ayrıştırılmış, bağlantı nesnesi kurulmuş. Testler gerçek SQL
    /// Server'a buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, DisableArgs args, TextWriter output, TextWriter error, CancellationToken ct)
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

        output.WriteLine(
            $"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — sales-sync disable — lisans {args.License} — {Time(now)}");
        if (license.SalesSyncMode == SalesSyncMode.Off)
        {
            output.WriteLine($"Mod zaten kapalı (Off, son değişim={Time(license.SalesSyncModeChangedAt)}): "
                + "yapılacak bir şey yok, hiçbir şey yazılmadı.");
            return 0;
        }
        output.WriteLine($"Mod: {license.SalesSyncMode} (son değişim={Time(license.SalesSyncModeChangedAt)})");

        if (!args.Apply)
        {
            var shipments = await SalesModeGate.OpenShipments(db, args.License).AsNoTracking().ToListAsync(ct);
            WriteDisablePlan(output, await SalesModeDisable.PlanAsync(db, args.License, shipments, ct));
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }

        return await ApplyDisableAsync(db, args.License, now, output, error, ct);
    }

    /// <summary><c>--apply</c>'ın işlemi; bkz. sınıf dokümanı.</summary>
    private static async Task<int> ApplyDisableAsync(
        LicenseDbContext db, Guid licenseId, DateTimeOffset now, TextWriter output, TextWriter error, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Licenses
            .Where(l => l.Id == licenseId && l.SalesSyncMode == SalesSyncMode.On)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.SalesSyncMode, SalesSyncMode.Off)
                .SetProperty(l => l.SalesSyncModeChangedAt, now), ct);
        if (updated == 0)
        {
            await transaction.RollbackAsync(ct);
            error.WriteLine("Mod bu arada değişti (başka bir disable?); hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }

        var shipments = await SalesModeGate.OpenShipments(db, licenseId).ToListAsync(ct);
        var plan = await SalesModeDisable.PlanAsync(db, licenseId, shipments, ct);
        WriteDisablePlan(output, plan);

        var byId = shipments.ToDictionary(s => s.Id);
        var rewrites = plan.Rewrites;
        foreach (var rewrite in rewrites)
        {
            var shipment = byId[rewrite.Id];
            shipment.OriginReplicaId = rewrite.NewOriginReplicaId;
            shipment.Version++;
            shipment.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            error.WriteLine("Bir kargo dosyası bu arada yazıldı; hiçbir şey yazılmadı, mod değişmedi. "
                + "Komutu yeniden çalıştır.");
            return 1;
        }
        await transaction.CommitAsync(ct);

        output.WriteLine($"UYGULANDI: mod Off, SalesSyncModeChangedAt={Time(now)}; "
            + $"sahibi yeniden yazılan dosya: {rewrites.Count}");
        return 0;
    }

    // ── Çıktı ────────────────────────────────────────────────────────────────

    private static void WriteDisablePlan(TextWriter output, SalesModeDisable.Plan plan)
    {
        output.WriteLine($"Shipped olmayan dosya: {plan.Shipments.Count}; bağlı sipariş: {plan.LinkedOrders} "
            + $"(sahibi bilinmeyen: {plan.LinkedOrdersWithUnknownOwner})");

        var rewrites = plan.Rewrites;
        output.WriteLine($"Sahibi yeniden yazılacak dosya: {rewrites.Count}");
        foreach (var p in rewrites)
            output.WriteLine($"   {p.Id}: {p.CurrentOriginReplicaId?.ToString() ?? "boş"} → {p.NewOriginReplicaId}");

        output.WriteLine($"Sahibi zaten doğru, değişmeyecek: {plan.AlreadyOwned.Count}");

        var unknown = plan.NoKnownOwner;
        output.WriteLine($"Bağlı siparişlerinin sahibi bilinmeyen, değişmeyecek: {unknown.Count} "
            + $"(bağlı siparişi olmayan: {unknown.Count(p => p.LinkedOrders == 0)})");
        WriteIds(output, unknown.Select(p => p.Id));

        var mixed = plan.Mixed;
        output.WriteLine($"Karışık dosya (birden çok kopyanın etiketi): {mixed.Count}");
        foreach (var p in mixed)
            output.WriteLine($"   {p.Id} → {p.NewOriginReplicaId}: "
                + string.Join(", ", p.Shares.Select(s => $"{s.ReplicaId}={Amount(s.Amount)}")));
        if (mixed.Count > 0)
            output.WriteLine("   Karışık dosya bağlı tutarı (iptalsiz, geçici yedek olmayan) büyük olan kopyaya, eşitlikte "
                + "küçük ReplicaId'ye gider. Öbür kopyanın bu dosyaya bağlı etiketleri o bilgisayarda yerel olmayan bir "
                + "dosyaya bağlı kalır (risk 15); istenirse --apply'dan önce bu dosyaların kararları verilir.");
    }

    /// <summary><c>disable</c>: <c>--license</c> zorunlu, <c>--apply</c> isteğe bağlı.</summary>
    private static bool TryParseDisable(string[] args, int start, [NotNullWhen(true)] out SalesSyncArgs? parsed)
    {
        parsed = null;
        if (!TryReadOptions(args, start, ["--license"], ["--apply"], out var values, out var flags)
            || !TryGuidOption(values, "--license", out var license))
            return false;

        parsed = new DisableArgs(license, flags.Contains("--apply"));
        return true;
    }
}
