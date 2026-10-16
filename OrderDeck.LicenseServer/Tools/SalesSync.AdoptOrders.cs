using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// <c>sales-sync adopt-orders --license &lt;guid&gt; --orders &lt;guid,…&gt; --replica &lt;guid&gt; [--apply]</c> — spec
/// 2026-10-13 §4.10, §4.6 (<c>attach-shipment</c> adayları), M22, §15-44.
///
/// <para><b>Ne için.</b> Kargo bağı bilinmeyen sipariş (<c>OriginReplicaId</c> boş: hiçbir Ws geri doldurmasının
/// üstlenmediği satır — Ws'ye hiç güncellenmemiş ya da emekli PC'nin geçmişi) mod On'da <c>attach-shipment</c> adayı
/// olmaz: sunucuda bağı yok ama kargolanmış olabilir. Burak siparişin gerçekten gönderilmediğini doğruladıktan sonra
/// onu verilen kopyaya benimsetir; sipariş o andan sonra aday olur.</para>
///
/// <para><b>Argümanlar.</b> <c>--orders</c> virgülle ayrılmış GUID listesi; boş öğe, GUID olmayan öğe ya da tekrarlar
/// ayıklandıktan sonra <see cref="MaxAdoptOrders"/>'tan fazla sipariş kullanım hatasıdır. Tekrarlar ilk görüldükleri
/// sırada bir kez kalır. <c>--replica</c> bu lisansın bir <c>WpfReplicas</c> satırı olmalı; değilse (başka lisansın
/// kopyası dahil) 2.</para>
///
/// <para><b>Kuru koşu</b> (varsayılan) hiçbir şey yazmaz; siparişleri üç kümeye ayırıp sayı ve Id'leriyle basar:
/// benimsenecek (bu lisansta, <c>OriginReplicaId</c> boş), kargo bağı zaten bilinen (dokunulmaz) ve bu lisansta
/// bulunamayan (hiç yok ya da başka lisansın).</para>
///
/// <para><b><c>--apply</c></b> tek işlemde koşullu bir UPDATE'tir: <c>LicenseId</c> ∧ Id benimsenecek kümede ∧
/// <c>OriginReplicaId IS NULL</c> → <c>OriginReplicaId</c> = kopya. Birimlere, <c>SyncVersion</c>'a (telde
/// <c>Version</c>; istemcinin beklenen sürümü) ve <c>UpdatedAt</c>'e dokunmaz; <c>ChangeSeq</c> (rowversion) ilerler,
/// akış siparişi yeniden taşır. Koşul bir geri doldurmanın araya girdiğini yakalar: güncellenen satır sayısı kuru
/// kısımda okunan küme kadar değilse işlem geri alınır, hiçbir şey yazılmaz ve 1 döner — yeniden çalıştırınca güncel
/// kümeyle koşar. İki kipte de çalışır (mod Off'ta aday kuralı uygulanmaz ama değer mod açılınca geçerlidir).</para>
/// </summary>
public static partial class SalesSync
{
    /// <summary><c>--orders</c>'ın tavanı (tekrarlar ayıklandıktan sonra); komutların <c>expected</c> tavanıyla aynı.</summary>
    public const int MaxAdoptOrders = 500;

    /// <summary><c>adopt-orders</c>'ın ayrıştırılmış argümanları. Eşitlik sipariş listesini içerikle (sırasıyla)
    /// karşılaştırır.</summary>
    /// <param name="Orders">Tekrarsız, ilk görülme sırasında.</param>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record AdoptOrdersArgs(Guid License, IReadOnlyList<Guid> Orders, Guid Replica, bool Apply)
        : SalesSyncArgs
    {
        public bool Equals(AdoptOrdersArgs? other) =>
            other is not null && License == other.License && Replica == other.Replica && Apply == other.Apply
            && Orders.SequenceEqual(other.Orders);

        public override int GetHashCode() => HashCode.Combine(License, Replica, Apply, Orders.Count);
    }

    /// <summary><c>adopt-orders</c>'ın gövdesi: argümanlar ayrıştırılmış, bağlantı nesnesi kurulmuş. Testler gerçek SQL
    /// Server'a buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, AdoptOrdersArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.SalesSyncMode })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }
        if (!await db.WpfReplicas.AsNoTracking()
                .AnyAsync(r => r.LicenseId == args.License && r.ReplicaId == args.Replica, ct))
        {
            error.WriteLine($"Kopya bu lisansta yok: {args.Replica}. Hiçbir şey yazılmadı.");
            return 2;
        }

        var ids = args.Orders.ToList();
        var found = await db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == args.License && ids.Contains(o.Id))
            .Select(o => new { o.Id, o.OriginReplicaId })
            .ToListAsync(ct);
        var adoptable = found.Where(o => o.OriginReplicaId is null).Select(o => o.Id).ToList();
        var known = found.Where(o => o.OriginReplicaId is not null).Select(o => o.Id).ToList();
        var missing = ids.Except(found.Select(o => o.Id)).ToList();

        output.WriteLine(
            $"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — sales-sync adopt-orders — lisans {args.License} — "
            + $"kopya {args.Replica} — {Time(now)}");
        output.WriteLine($"Mod: {license.SalesSyncMode}");
        output.WriteLine($"Verilen sipariş: {ids.Count}");
        output.WriteLine($"Kargo bağı bilinmeyen (OriginReplicaId boş), benimsenecek: {adoptable.Count}");
        WriteIds(output, adoptable);
        output.WriteLine($"Kargo bağı zaten bilinen, dokunulmayacak: {known.Count}");
        WriteIds(output, known);
        output.WriteLine($"Bu lisansta bulunamayan: {missing.Count}");
        WriteIds(output, missing);

        if (!args.Apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }
        if (adoptable.Count == 0)
        {
            output.WriteLine("Benimsenecek sipariş yok: hiçbir şey yazılmadı.");
            return 0;
        }

        Guid? replica = args.Replica;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Orders
            .Where(o => o.LicenseId == args.License && adoptable.Contains(o.Id) && o.OriginReplicaId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.OriginReplicaId, replica), ct);
        if (updated != adoptable.Count)
        {
            await transaction.RollbackAsync(ct);
            error.WriteLine(
                $"Siparişler bu arada değişti: benimsenecek {adoptable.Count} siparişten yalnız {updated} hâlâ "
                + "boştu; hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }
        await transaction.CommitAsync(ct);

        output.WriteLine($"UYGULANDI: {updated} sipariş benimsendi, OriginReplicaId = {args.Replica}");
        return 0;
    }

    /// <summary><c>adopt-orders</c>: <c>--license</c>, <c>--orders</c> ve <c>--replica</c> zorunlu; <c>--apply</c>
    /// isteğe bağlı. Sipariş listesi için bkz. sınıf dokümanı.</summary>
    private static bool TryParseAdoptOrders(string[] args, int start, [NotNullWhen(true)] out SalesSyncArgs? parsed)
    {
        parsed = null;
        if (!TryReadOptions(args, start, ["--license", "--orders", "--replica"], ["--apply"], out var values,
                out var flags)
            || !TryGuidOption(values, "--license", out var license)
            || !TryGuidOption(values, "--replica", out var replica)
            || !values.TryGetValue("--orders", out var list))
            return false;

        var orders = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var item in list.Split(','))
        {
            if (!Guid.TryParse(item, out var id)) return false;
            if (seen.Add(id)) orders.Add(id);
        }
        if (orders.Count > MaxAdoptOrders) return false;

        parsed = new AdoptOrdersArgs(license, orders, replica, flags.Contains("--apply"));
        return true;
    }
}
