using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using OrderDeck.Core.Storage;
using OrderDeck.Shared.Sales;

namespace OrderDeck.Core.Sales;

/// <summary>Açılıştaki dosya türetmesinin sonucu: tutarı değişip yazılan dosya sayısı ve farkların mutlak toplamı
/// (TL). Günlüğe yalnız bunlar yazılır; Id ya da ad yazılmaz.</summary>
public sealed record ShipmentDerivationResult(int ChangedFiles, decimal TotalAbsDiff)
{
    public static ShipmentDerivationResult None { get; } = new(0, 0m);
}

/// <summary>
/// Y5'in fark tabanı (spec 2026-10-13 §7.4 "Fark tabanı", §8.2-8, M12, §15-46): Ws'nin ilk açılışında kargoya
/// verilmemiş her yerel dosyanın tutarı bağlı etiketlerden BİR KEZ türetilir.
///
/// <para><b>Neden:</b> Y5'in yerel yolu (<see cref="ShipmentAmountAdjuster"/>) farkla çalışır. Ws öncesinde (≤0.9.10)
/// iptal edilmiş bağlı etiket açık dosyanın tutarında hâlâ durur (o sürümün <c>Cancel</c>'ı dosyaya dokunmuyordu).
/// Taban türetilmeseydi böyle bir etiketin Ws'de iptali geri alınınca fiyatı ikinci kez eklenirdi (F = 400, K5 150
/// iptal, F 400'de kalır; geri alma → 550, gerçek 400; 500 eşiğinde yanlış ücretsiz kargo).</para>
///
/// <para><b>Kural</b> <see cref="LabelRules.DerivedShipmentAmount"/>'ın kendisidir (sunucuyla ortak tek tanım): bağlı,
/// iptal edilmemiş, geçici yedek olmayan etiketlerin fiyat toplamı. Ws öncesinde dosyaya yalnız yerel etiket
/// bağlanabildiği için türetme kesindir. Seçim SQL'de (kargoya verilmemiş dosyalar ve onlara bağlı etiketler), toplam
/// ondalık aritmetikle — <c>ShipmentService.AttachLabels</c>'ın tutarı kurduğu aritmetik; saklanan değer okunduğu
/// gibi karşılaştırılır, kayan nokta artığı "değişti" saydırmaz.</para>
///
/// <para><b>Kuyruk:</b> yazım kilit altında DEĞİL ve Revision'ı artırmaz: <c>TR_Shipment_Enqueue</c> değişen dosyayı
/// kuyruğa alır, <c>shipments/sync</c> gönderir. Yalnız değeri değişen dosya yazılır. Kargoya verilmiş dosyanın
/// tutarı donmuştur, dokunulmaz.</para>
///
/// <para><b>Mod 1:</b> dosya sunucunundur, tutarını sunucu türetir (§7.4 son madde); yerel türetme yazmaz (yazsaydı
/// <c>TR_Shipment_ServerOwned_U</c> ABORT ederdi), yalnız damga basılır — Off'a dönüşte dosyalar sunucudan türetilmiş
/// gelir. Normal sırada bu durum oluşmaz: türetme ilk açılışta, kip geçişinden önce koşar.</para>
/// </summary>
public static class ShipmentAmountDerivation
{
    /// <summary>
    /// <c>SalesSyncState.ShipmentsDerivedAt</c> boşsa dosyaları TEK işlemde türetir ve damgayı yazar; doluysa hiçbir
    /// şey yazmaz.
    /// </summary>
    /// <returns>Türetme sonucu; damga zaten doluysa <c>null</c>.</returns>
    public static ShipmentDerivationResult? DeriveAllOnce(IDbConnectionFactory factory, long nowUnix)
    {
        using var write = DbWrite.Begin(factory);
        var conn = write.Connection;
        var tx = write.Transaction;
        // BEGIN IMMEDIATE: yazma kilidi okumadan önce alındı — iki açılış aynı damgayı ikinci kez kararlaştıramaz.
        var state = conn.QuerySingleOrDefault<StateRow>(
            "SELECT ShipmentsDerivedAt, Mode FROM SalesSyncState WHERE Id = 1", transaction: tx)
            ?? throw new InvalidOperationException("SalesSyncState satırı yok (göç 049); dosyalar türetilmedi.");
        if (state.ShipmentsDerivedAt is not null) return null;

        var result = state.Mode == 0 ? DeriveOpenFiles(conn, tx) : ShipmentDerivationResult.None;
        conn.Execute(
            "UPDATE SalesSyncState SET ShipmentsDerivedAt = @now WHERE Id = 1 AND ShipmentsDerivedAt IS NULL",
            new { now = nowUnix }, tx);
        write.Commit();
        return result;
    }

    private static ShipmentDerivationResult DeriveOpenFiles(IDbConnection conn, IDbTransaction tx)
    {
        var files = conn.Query<FileRow>(
            "SELECT Id, CumulativeAmount FROM Shipment WHERE Status <> 'Shipped'", transaction: tx).ToList();
        if (files.Count == 0) return ShipmentDerivationResult.None;

        var labelsByFile = conn.Query<LabelRow>(
                @"SELECT l.Id, l.Code, l.Price, l.PrintedAt, l.CancelledAt, l.IsTentativeBackup, l.IsShippingFee,
                         l.ShipmentId
                  FROM Label l JOIN Shipment s ON s.Id = l.ShipmentId
                  WHERE s.Status <> 'Shipped'", transaction: tx)
            .Select(r => new LabelFacts(r.Id, r.Code, (decimal)r.Price, r.PrintedAt is not null, r.CancelledAt is not null,
                r.IsTentativeBackup != 0, r.IsShippingFee != 0, r.ShipmentId))
            .ToLookup(f => f.ShipmentId!, StringComparer.Ordinal);

        var changed = 0;
        var diff = 0m;
        foreach (var file in files)
        {
            var stored = (decimal)file.CumulativeAmount;
            var derived = LabelRules.DerivedShipmentAmount(file.Id, labelsByFile[file.Id]);
            if (derived == stored) continue;
            conn.Execute("UPDATE Shipment SET CumulativeAmount = @amount WHERE Id = @id",
                new { id = file.Id, amount = (double)derived }, tx);
            changed++;
            diff += Math.Abs(derived - stored);
        }
        return new ShipmentDerivationResult(changed, diff);
    }

    private sealed class StateRow
    {
        public long? ShipmentsDerivedAt { get; set; }
        public long Mode { get; set; }
    }

    private sealed class FileRow
    {
        public string Id { get; set; } = "";
        public double CumulativeAmount { get; set; }
    }

    private sealed class LabelRow
    {
        public string Id { get; set; } = "";
        public string? Code { get; set; }
        public double Price { get; set; }
        public long? PrintedAt { get; set; }
        public long? CancelledAt { get; set; }
        public long IsTentativeBackup { get; set; }
        public long IsShippingFee { get; set; }
        public string? ShipmentId { get; set; }
    }
}
