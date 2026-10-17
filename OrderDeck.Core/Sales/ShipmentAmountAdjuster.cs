using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using OrderDeck.Core.Storage;

namespace OrderDeck.Core.Sales;

/// <summary><see cref="ShipmentAmountAdjuster.Apply(IDbConnection, IDbTransaction, IReadOnlyDictionary{string, decimal})"/>'ın
/// sonucu. Günlüğe yazılacaksa yalnız sayılar yazılır (Id ve ad yok).</summary>
/// <param name="Applied">Bulunan ve kargoya verilmemiş dosyalar, istenen farkla (fark 0 olan da; ona yazım
/// yapılmaz). Mod 1'de boş.</param>
/// <param name="Missing">Bu bilgisayarda olmayan dosya sayısı — başka bilgisayarın dosyası (§7.4); atlandı.</param>
/// <param name="Shipped">Kargoya verilmiş dosyalar: tutarı donmuştur, dokunulmadı. Çağıran bunu yarış sayabilir
/// (<see cref="LabelService.CorrectPrices"/> sayar).</param>
/// <param name="ServerOwned">Mod 1 (<c>SalesSyncState.Mode = 1</c>): dosya sunucunundur, blok hiç çalışmadı.</param>
public sealed record ShipmentAdjustResult(
    IReadOnlyDictionary<string, decimal> Applied, int Missing, IReadOnlyList<string> Shipped, bool ServerOwned)
{
    public static ShipmentAdjustResult None { get; } = new(
        new Dictionary<string, decimal>(StringComparer.Ordinal), 0, Array.Empty<string>(), false);
}

/// <summary>
/// "Y5 dosya bloğu" (spec 2026-10-13 §7.4, sahip kararı Y5, M12): kargo dosyası tutarının mod Off'taki TEK yerel
/// yazıcısı. Etiketin dosyadaki payı değişince (iptal, iptali geri alma, fiyat düzeltme, push yanıtındaki
/// <c>current</c>) dosya tutarı fark kadar oynar; bağ (<c>Label.ShipmentId</c>) DEĞİŞMEZ — "dekontla ödendi"nin
/// tek işareti odur (§7.2).
///
/// <para><b>Neden fark, neden yeniden türetme değil:</b> <c>disable</c>'dan sonra karışık bir dosya başka bilgisayarın
/// etiketlerini de taşır; yerelde olmayan etiketleri türetme düşürürdü (§7.4). Farkın tabanı açılışta bir kez
/// türetilir (<see cref="ShipmentAmountDerivation"/>; §7.4 "Fark tabanı", §8.2-8).</para>
///
/// <para><b>Kurallar</b>, sırayla:</para>
/// <list type="bullet">
/// <item>Mod 1'de hiçbir şey yazılmaz: dosya sunucunundur, tutarı sunucu türetir (K7, §5.1-a). Yazsaydı
/// <c>TR_Shipment_ServerOwned_U</c> ABORT ederdi.</item>
/// <item>Bu bilgisayarda olmayan dosya atlanır ve sayılır, istisna atılmaz: o etiketin iptali, geri alınması ve
/// kodun fiyat düzeltmesi kilitlenmesin (§7.4, §15-57).</item>
/// <item>Kargoya verilmiş dosyanın tutarı donmuştur (§7.2): dokunulmaz, sonuçta listelenir.</item>
/// <item>Gerisinde <c>CumulativeAmount += fark</c>, ondalık aritmetikle (<c>ShipmentService.AttachLabels</c> ile
/// aynı: okunan değer + fark, double olarak yazılır) ve <c>SyncedAt = NULL</c>, <c>Revision + 1</c> AÇIKÇA:
/// push yanıtının <c>current</c>'ı <see cref="SyncApplyScope"/> içinde uygulanır ve kilit
/// <c>TR_Shipment_Enqueue</c>'yu susturur — değişiklik yine de <c>shipments/sync</c> ile gitmeli (mod Off'ta
/// dosyanın tek yazıcısı bu kopya, <c>OriginReplicaId</c>). Revision'ı kendisi artırdığı için kilitsiz yazımda
/// tetikleyici etkisizdir; artış tam bir kez olur.</item>
/// </list>
///
/// <para><b>Bağlantı (U17):</b> çağıranın işleminde koşar (<see cref="DbWrite"/> ya da
/// <see cref="SyncApplyScope"/>); ikinci bağlantı açmaz. Okuma ve yazma aynı <c>BEGIN IMMEDIATE</c> işleminde
/// olduğu için okunan durum yazılana kadar değişemez.</para>
/// </summary>
public static class ShipmentAmountAdjuster
{
    /// <inheritdoc cref="Apply(IDbConnection, IDbTransaction, IReadOnlyDictionary{string, decimal})"/>
    public static ShipmentAdjustResult Apply(IReadOnlyDictionary<string, decimal> deltas, DbWrite write)
        => Apply(write.Connection, write.Transaction, deltas);

    /// <inheritdoc cref="Apply(IDbConnection, IDbTransaction, IReadOnlyDictionary{string, decimal})"/>
    public static ShipmentAdjustResult Apply(IReadOnlyDictionary<string, decimal> deltas, SyncApplyScope scope)
        => Apply(scope.Connection, scope.Transaction, deltas);

    /// <summary>Dosya başına farkları (<paramref name="deltas"/>: dosya Id'si → TL) verilen işlemde uygular.</summary>
    public static ShipmentAdjustResult Apply(
        IDbConnection conn, IDbTransaction tx, IReadOnlyDictionary<string, decimal> deltas)
    {
        if (deltas.Count == 0) return ShipmentAdjustResult.None;
        if (conn.ExecuteScalar<long>(
                "SELECT COALESCE((SELECT Mode FROM SalesSyncState WHERE Id = 1), 0)", transaction: tx) == 1)
            return ShipmentAdjustResult.None with { ServerOwned = true };

        var applied = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var shipped = new List<string>();
        var missing = 0;
        foreach (var (id, delta) in deltas.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var row = conn.QueryFirstOrDefault<Row>(
                "SELECT Status, CumulativeAmount FROM Shipment WHERE Id = @id", new { id }, tx);
            if (row is null)
            {
                missing++;
                continue;
            }
            if (Enum.Parse<ShipmentStatus>(row.Status) == ShipmentStatus.Shipped)
            {
                shipped.Add(id);
                continue;
            }
            applied[id] = delta;
            if (delta == 0m) continue;
            conn.Execute(
                "UPDATE Shipment SET CumulativeAmount = @amount, SyncedAt = NULL, Revision = Revision + 1 WHERE Id = @id",
                new { id, amount = (double)((decimal)row.CumulativeAmount + delta) }, tx);
        }
        return new ShipmentAdjustResult(applied, missing, shipped, false);
    }

    /// <summary>Etiketlerin dosyadaki payının farkı: dosyaya bağlı ve geçici yedek olmayan her etiket için
    /// <paramref name="sign"/> × fiyat (iptal −1, iptali geri alma +1). Geçici yedek dosya tutarında hiç yoktur
    /// (<c>LabelRules.DerivedShipmentAmount</c>; bağlanmamış etiket süzgeci onu dışarıda bırakır).</summary>
    public static IReadOnlyDictionary<string, decimal> ShareDeltas(IEnumerable<Label> labels, int sign)
    {
        var deltas = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var l in labels)
            if (l.ShipmentId is { } sid && !l.IsTentativeBackup)
                deltas[sid] = deltas.GetValueOrDefault(sid) + sign * l.Price;
        return deltas;
    }

    private sealed class Row
    {
        public string Status { get; set; } = "";
        public double CumulativeAmount { get; set; }
    }
}
