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
/// <para><b>Değişmeyenler (K6):</b> iptal edilmiş etiket, kargo ücreti satırı (ürün değil),
/// kargoya VERİLMİŞ dosyadaki etiket ve başka bilgisayarın dosyasındaki etiket (bağlı dosya bu bilgisayarda
/// yok, durumu bilinmiyor — spec 2026-10-13 §7.4). Kargoya verilmemiş dosyanın etiketi düzelir, dosyanın
/// toplamı fark kadar değişir.</para>
/// </summary>
public sealed class PriceCorrectionPlan
{
    public PriceCorrectionPlan(
        IReadOnlyList<Label> correctable, IReadOnlyList<Label> shipped, IReadOnlyList<Label> excluded,
        IReadOnlyList<Label>? elsewhere = null)
    {
        Correctable = correctable;
        Shipped = shipped;
        Excluded = excluded;
        Elsewhere = elsewhere ?? Array.Empty<Label>();
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

    /// <summary>Başka bilgisayarın dosyasındaki etiketler — DEĞİŞMEZ ("başka bilgisayarın dosyasında"). Bağlı
    /// olduğu kargo dosyası bu bilgisayarda yok (mod Off'ta <c>disable</c> turunun sildiği karışık dosya, §7.4):
    /// dosya kargoya verilmiş olabilir, tutarı burada düzeltilemez.</summary>
    public IReadOnlyList<Label> Elsewhere { get; }

    public IReadOnlyList<PriceGroup> PriceGroups { get; }

    /// <summary>Verilen etiketlerin (<paramref name="ids"/>) <paramref name="labels"/> içindeki durumunun parmak
    /// izi; listede olmayan Id "yok" diye girer (arada iptal edildi). Kod düzeyindeki düzeltme bununla operatörün
    /// GÖRDÜĞÜ etiketlerin değişip değişmediğine bakar — arada yazılan yeni etiket kodun etiketidir, düzeltmeye
    /// girer ama gösterilen özeti bayatlatmaz (yayında yorum yağarken onay hiç tutmazdı).</summary>
    public static string FingerprintOf(IEnumerable<string> ids, IEnumerable<Label> labels)
    {
        var byId = new Dictionary<string, Label>(StringComparer.Ordinal);
        foreach (var l in labels) byId[l.Id] = l;
        return string.Join(";", ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => byId.TryGetValue(id, out var l)
                ? string.Join("/", l.Id, l.Price.ToString(CultureInfo.InvariantCulture), l.PrintedAt.HasValue ? "p" : "q",
                    l.IsTentativeBackup ? "t" : "c", l.ShipmentId ?? "-", l.CustomerId)
                : id + "/yok"));
    }
}

/// <summary>Kod düzeyinde fiyat düzeltmesinin bir kodu (sahip kararı F3): kodun yayındaki BÜTÜN etiketleri
/// (artı seçimde o koddan olanlar), sınıflanmış. <see cref="Code"/> null = kodsuz etiketler — genişletilecek kod
/// yok, yalnız seçilenler.</summary>
public sealed record CodeCorrectionPlan(string? Code, PriceCorrectionPlan Plan);

/// <summary>Bir yayında bir kodun fiyatı (sahip kararı F1): kaynağı o yayındaki o kodun iptal edilmemiş
/// etiketleri — ayrı bir fiyat belleği yok.</summary>
/// <param name="Price">Kodun fiyatı: düzeltilebilir etiketlerin en çok tekrarlanan fiyatı (eşitlikte en son
/// yazılanınki). Düzeltilebilir etiket yoksa kargoya verilmiş dosyadakilerin.</param>
/// <param name="Correctable">Düzeltilebilir etiket sayısı (kuyruk, basılmış, yedek; kargoya verilmiş dosya
/// hariç).</param>
/// <param name="Groups">Düzeltilebilir etiketlerin fiyat grupları; birden çoksa kodda karışık fiyat var (eski
/// veri ya da pazarlıklı yedek).</param>
/// <param name="Shipped">Kargoya verilmiş dosyadaki — değişmeyen — etiket sayısı.</param>
public sealed record CodePrice(decimal Price, int Correctable, IReadOnlyList<PriceGroup> Groups, int Shipped)
{
    public bool IsMixed => Groups.Count > 1;
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
/// <param name="CustomerDeltas">Müşteri toplamındaki fark (yalnız basılmış ve onaylı satırlar). Bilgi için döner:
/// toplamın kendisi etiketlerden türetilir (spec 2026-10-13 §6.1).</param>
/// <param name="ShipmentDeltas">Kargoya verilmemiş dosyaların toplamına yazılan fark (Y5 dosya bloğunun uyguladığı:
/// mod On'da ve bu arada yerelden kalkmış dosyada boş).</param>
/// <param name="Elsewhere">Başka bilgisayarın dosyasında olduğu için DEĞİŞMEYEN etiketler (§7.4).</param>
public sealed record PriceCorrectionResult(
    IReadOnlyList<CorrectedLabel> Corrected,
    IReadOnlyList<Label> Shipped,
    IReadOnlyDictionary<string, decimal> CustomerDeltas,
    IReadOnlyDictionary<string, decimal> ShipmentDeltas,
    IReadOnlyList<Label> Elsewhere)
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
