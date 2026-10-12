using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OrderDeck.Core.Sales;

/// <summary>Seçimdeki bir fiyat grubu ("250 TL × 23").</summary>
public sealed record PriceGroup(decimal Price, int Count);

/// <summary>
/// "Fiyatı düzelt"in okuma tarafı (sahip kararı K5): seçilen etiketlerin hangileri düzelir,
/// hangileri değişmez. Yazma yok; <see cref="LabelService.CorrectPrices"/> aynı sınıflamayı
/// yazmadan hemen önce yeniden yapar.
///
/// <para><b>Değişmeyenler (K6):</b> iptal edilmiş etiket, kargo ücreti satırı (ürün değil) ve
/// kargoya VERİLMİŞ dosyadaki etiket. Kargoya verilmemiş dosyanın etiketi düzelir, dosyanın toplamı
/// fark kadar değişir.</para>
/// </summary>
public sealed class PriceCorrectionPlan
{
    public PriceCorrectionPlan(
        IReadOnlyList<Label> correctable, IReadOnlyList<Label> shipped, IReadOnlyList<Label> excluded)
    {
        Correctable = correctable;
        Shipped = shipped;
        Excluded = excluded;
        PriceGroups = correctable
            .GroupBy(l => l.Price)
            .Select(g => new PriceGroup(g.Key, g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Price)
            .ToList();
    }

    /// <summary>Düzeltilebilir etiketler: iptal edilmemiş, kargo ücreti değil, kargoya verilmiş
    /// dosyada değil.</summary>
    public IReadOnlyList<Label> Correctable { get; }

    /// <summary>Kargoya verilmiş dosyadaki etiketler — DEĞİŞMEZ ("kargoya verildi, değişmedi").</summary>
    public IReadOnlyList<Label> Shipped { get; }

    /// <summary>İptal edilmiş ya da kargo ücreti satırı — düzeltmenin konusu değil.</summary>
    public IReadOnlyList<Label> Excluded { get; }

    public IReadOnlyList<PriceGroup> PriceGroups { get; }

    /// <summary>Durumun parmak izi: onay anında yeniden okunan plan bununla karşılaştırılır — arada
    /// bir etiket basıldı, iptal edildi ya da fiyatı değiştiyse operatör eski özeti onaylamış olurdu.</summary>
    public string Fingerprint => string.Join(";",
        Correctable.Concat(Shipped).OrderBy(l => l.Id, StringComparer.Ordinal).Select(l => string.Join("/",
            l.Id, l.Price.ToString(CultureInfo.InvariantCulture), l.PrintedAt.HasValue ? "p" : "q",
            l.IsTentativeBackup ? "t" : "c", l.ShipmentId ?? "-", l.CustomerId)));
}

/// <summary>Bir etiketin düzeltmesi: önceki hâli ve yeni fiyatı.</summary>
public sealed record CorrectedLabel(Label Before, decimal NewPrice)
{
    public decimal Delta => NewPrice - Before.Price;
    public bool WasPrinted => Before.PrintedAt.HasValue;
}

/// <summary><see cref="LabelService.CorrectPrices"/> sonucu.</summary>
/// <param name="Corrected">Fiyatı değişen etiketler.</param>
/// <param name="Shipped">Kargoya verilmiş dosyada olduğu için DEĞİŞMEYEN etiketler.</param>
/// <param name="CustomerDeltas">Müşteri toplamına yazılan fark (yalnız basılmış ve onaylı satırlar).</param>
/// <param name="ShipmentDeltas">Kargoya verilmemiş dosyaların toplamına yazılan fark.</param>
public sealed record PriceCorrectionResult(
    IReadOnlyList<CorrectedLabel> Corrected,
    IReadOnlyList<Label> Shipped,
    IReadOnlyDictionary<string, decimal> CustomerDeltas,
    IReadOnlyDictionary<string, decimal> ShipmentDeltas)
{
    public IReadOnlyList<CorrectedLabel> CorrectedPrinted => Corrected.Where(c => c.WasPrinted).ToList();
}

/// <summary>Düzeltme yazılırken bir etiketin okunduğu hâlden farklı bulundu (arada basıldı, iptal
/// edildi, fiyatı ya da kargo dosyası değişti). Hiçbir şey yazılmadı — paket geri sarıldı.</summary>
public sealed class PriceCorrectionConflictException : Exception
{
    public PriceCorrectionConflictException(string message) : base(message) { }
}

/// <summary>Yeniden basılacak etiketlerin güncel hâli (<see cref="LabelService.PrepareReprint"/>).
/// <see cref="CorrectionIds"/>: fiyatı düzeltilmiş olanlar — kâğıtta "DÜZELTME" işareti.</summary>
public sealed record Reprint(IReadOnlyList<Label> Labels, IReadOnlySet<string> CorrectionIds);
