namespace OrderDeck.Shared.Sales;

/// <summary>
/// Etiketin (sunucuda: siparişin) para kurallarının <b>tek tanımı</b> — masaüstü ve sunucu aynı kodu
/// çağırır (spec 2026-10-13 §4.11).
///
/// <para><b>Neden burada.</b> Yayın bittikten sonra etiketi sunucu da değiştirecek (fiyat düzeltme, iptal,
/// kargo). Kural iki tarafta elle tutulursa sessizce ayrışır: her taraf kendi kopyasıyla tutarlı kalır, yalnız
/// birbirleriyle tutarsız olur ve bunu hiçbir tek taraflı test göstermez (aynı gerekçe:
/// <see cref="OrderDeck.Shared.Text.TurkishAscii"/>). Bu yüzden kurallar burada ve ortak vektör dosyası
/// (<c>LabelRules.vectors.json</c>) iki test projesinde birden koşar.</para>
///
/// <para><b>Bugünkü anlamın birebir kopyası.</b> Her fonksiyon masaüstündeki bugünkü tanımı aynen taşır
/// (kaynak satırı her fonksiyonun notunda). Masaüstü bu PR'da kendi kodunu kullanmaya devam eder; eşitliği
/// OrderDeck.Tests'teki eşlik testleri aynı vektörlerle sabitler.</para>
///
/// <para>Core'un <c>Label</c> tipini bilmez: girdi en küçük kayıttır (<see cref="LabelFacts"/>). Kod
/// eşleşmesi SQL harmanıyla değil bellekte yapılır; harmanlar Türkçe harfleri katlamaz.</para>
/// </summary>
public static class LabelRules
{
    /// <summary>
    /// İki etiket kodu aynı ürün mü: harf duyarsız (<see cref="StringComparison.OrdinalIgnoreCase"/>), kırpma
    /// YOK. Masaüstündeki tanımın aynısı (<c>LabelRepository.SameCode</c>). Sorgu kodunu kırpmak çağıranın işidir
    /// (<c>LabelService.GetCodePrice</c> kırpar). <paramref name="a"/> null ise eşleşmez (kodsuz etiket hiçbir
    /// kodla aynı değildir).
    /// </summary>
    public static bool SameCode(string? a, string b)
        => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Kod gruplamasının anahtarı: kırpılmış kod; kodsuz etiket için boş metin
    /// (<c>LabelService.CodeKey</c>). Harf katlamaz — gruplayan karşılaştırıcı
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>'dir.</summary>
    public static string CodeKey(string? code) => code?.Trim() ?? "";

    /// <summary>
    /// Fiyat düzeltmesi açısından etiketin sınıfı (<c>LabelService.Classify</c>):
    /// iptal edilmiş ya da kargo ücreti satırı → <see cref="LabelPriceClass.Excluded"/>; kargoya VERİLMİŞ dosyaya
    /// bağlı → <see cref="LabelPriceClass.Shipped"/> (değişmez); gerisi <see cref="LabelPriceClass.Correctable"/>.
    /// Bağlı olduğu dosya bilinmeyen etiket (<paramref name="shipmentStatusOf"/> null döner) bugün düzeltilebilir
    /// sayılır.
    /// </summary>
    /// <param name="shipmentStatusOf">Dosya Id'sinden durumu; dosya bilinmiyorsa null. Yalnız dosyaya bağlı,
    /// iptal edilmemiş, kargo ücreti olmayan etiket için çağrılır.</param>
    public static LabelPriceClass Classify(LabelFacts facts, Func<string, SalesShipmentStatus?> shipmentStatusOf)
    {
        if (facts.Cancelled || facts.ShippingFee) return LabelPriceClass.Excluded;
        if (facts.ShipmentId is { } sid && shipmentStatusOf(sid) == SalesShipmentStatus.Shipped)
            return LabelPriceClass.Shipped;
        return LabelPriceClass.Correctable;
    }

    /// <summary>
    /// Bir yayında bir kodun fiyatı (sahip kararı F1, <c>LabelService.GetCodePrice</c>): kaynak kodun düzeltilebilir
    /// etiketleri, yoksa kargoya verilmiş dosyadakiler; fiyat kaynağın <see cref="CodePrice(IEnumerable{decimal})"/>'ı.
    /// Kaynak boşsa null (kodun bu yayında sayılan etiketi yok).
    /// </summary>
    /// <param name="labelsInWriteOrder">Kodun etiketleri, yazılış sırasıyla (masaüstünde <c>AddedAt, rowid</c>).
    /// Kodla süzmek çağıranın işidir (<see cref="SameCode"/>).</param>
    public static decimal? CodePrice(
        IReadOnlyList<LabelFacts> labelsInWriteOrder, Func<string, SalesShipmentStatus?> shipmentStatusOf)
    {
        var classes = labelsInWriteOrder.Select(l => (l.Price, Class: Classify(l, shipmentStatusOf))).ToList();
        var source = classes.Any(x => x.Class == LabelPriceClass.Correctable)
            ? LabelPriceClass.Correctable
            : LabelPriceClass.Shipped;
        return CodePrice(classes.Where(x => x.Class == source).Select(x => x.Price));
    }

    /// <summary>Fiyat kaynağının fiyatı: en çok tekrarlanan fiyat; eşitlikte en son yazılanınki. Boş kaynakta
    /// null.</summary>
    /// <param name="pricesInWriteOrder">Fiyatlar yazılış sırasıyla.</param>
    public static decimal? CodePrice(IEnumerable<decimal> pricesInWriteOrder)
    {
        var prices = pricesInWriteOrder.Select((price, order) => (Price: price, Order: order)).ToList();
        if (prices.Count == 0) return null;
        return prices
            .GroupBy(x => x.Price)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(x => x.Order))
            .First().Key;
    }

    /// <summary>
    /// Etiketin müşteri toplamına katkısı: basılmış ∧ iptal edilmemiş ∧ geçici yedek değil → (1, Price); aksi
    /// (0, 0). Masaüstünde beş yerde elle tekrarlanan kural (<c>LabelService</c>: iptal, iptali geri alma, baskı,
    /// fiyat düzeltme, yedek onayı). Kargo ücreti satırı ve dosya durumu kuralın dışındadır — bugün de öyle.
    /// </summary>
    public static (int Count, decimal Amount) Contribution(LabelFacts facts)
        => facts.Printed && !facts.Cancelled && !facts.Tentative ? (1, facts.Price) : (0, 0m);

    /// <summary>
    /// Fiyat düzeltmesinden sonra etiketin <c>PaidPrice</c>'ı (göç 048, <c>LabelRepository.UpdatePriceForCorrection</c>
    /// CASE'i): etiket ÖDENMİŞSE — bir kargo dosyasına bağlı ya da tamamen bakiyeden düşülmüş bir isteğin içinde —
    /// ve PaidPrice henüz boşsa düzeltmeden ÖNCEKİ fiyat (ödendiği fiyat) yazılır; aksi hâlde PaidPrice değişmez.
    /// </summary>
    /// <param name="paidPrice">Düzeltmeden önceki PaidPrice.</param>
    /// <param name="priceBefore">Düzeltmeden önceki fiyat.</param>
    /// <param name="linkedToShipment">Etiket bir kargo dosyasına bağlı (ShipmentId dolu).</param>
    /// <param name="paidFromBalance">Etiketi içeren teslim edilmiş istek tamamen bakiyeden düşülmüş.</param>
    public static decimal? PaidPriceOnCorrection(
        decimal? paidPrice, decimal priceBefore, bool linkedToShipment, bool paidFromBalance)
        => paidPrice is null && (linkedToShipment || paidFromBalance) ? priceBefore : paidPrice;

    /// <summary>
    /// Türetilmiş kargo dosyasının tutarı (DM7, spec 2026-10-13 §3.3, §7.2): dosyaya bağlı, iptal edilmemiş ve geçici
    /// yedek olmayan etiketlerin fiyat toplamı. İptal bağı çözmez, tutardan düşürür; iptali geri almak tutarı eski
    /// hâline döndürür. Kargo ücreti satırı ve basılmamış etiket dahildir — <c>ShipmentService.AttachLabels</c> bugün
    /// de onları katıyor (bağlanmamış etiket süzgeci yalnız iptal ve geçici yedeği dışarıda bırakır,
    /// <c>LabelRepository.GetUnattachedByCustomer</c>). Dosyanın durumu çağıranın işidir: Shipped dosyanın tutarı
    /// donmuştur ve yeniden türetilmez.
    /// </summary>
    /// <param name="shipmentId">Dosyanın Id'si (<see cref="LabelFacts.ShipmentId"/> ile aynı biçim, ordinal).</param>
    /// <param name="labels">Aday etiketler; yalnız bu dosyaya bağlı olanlar sayılır.</param>
    public static decimal DerivedShipmentAmount(string shipmentId, IEnumerable<LabelFacts> labels)
        => labels
            .Where(l => string.Equals(l.ShipmentId, shipmentId, StringComparison.Ordinal)
                        && !l.Cancelled
                        && !l.Tentative)
            .Sum(l => l.Price);
}

/// <summary>
/// <see cref="LabelRules"/>'un girdisi: bir etiketin kurala giren en küçük hâli. Masaüstü <c>Label</c>'dan,
/// sunucu <c>Order</c>'dan kurar. Id'ler metindir (masaüstünde GUID "N" metni; sunucu GUID'i aynı biçimle
/// metne çevirir).
/// </summary>
/// <param name="Printed"><c>PrintedAt</c> dolu.</param>
/// <param name="Cancelled"><c>CancelledAt</c> dolu.</param>
/// <param name="Tentative">Geçici (onaylanmamış) yedek.</param>
/// <param name="ShippingFee">Kargo ücreti satırı (ürün değil).</param>
/// <param name="ShipmentId">Bağlı kargo dosyası; yoksa null.</param>
public sealed record LabelFacts(
    string Id,
    string? Code,
    decimal Price,
    bool Printed,
    bool Cancelled,
    bool Tentative,
    bool ShippingFee,
    string? ShipmentId);

/// <summary>
/// Kargo dosyasının durumu, ortak kuralların gözünden. Değerler ve adlar masaüstündeki
/// (<c>OrderDeck.Core.Sales.ShipmentStatus</c>) ve sunucudaki (<c>OrderDeck.LicenseServer.Domain.ShipmentStatus</c>)
/// enum'larla birebir aynıdır; iki taraftaki eşlik testleri bunu sabitler.
/// </summary>
public enum SalesShipmentStatus
{
    Pending = 0,
    Held = 1,
    RecipientPays = 2,
    Shipped = 3,
}

/// <summary>Fiyat düzeltmesi açısından etiketin sınıfı (<see cref="LabelRules.Classify"/>).</summary>
public enum LabelPriceClass
{
    /// <summary>Düzeltilebilir: iptal edilmemiş, kargo ücreti değil, kargoya verilmiş dosyada değil.</summary>
    Correctable,

    /// <summary>Kargoya verilmiş dosyada — DEĞİŞMEZ.</summary>
    Shipped,

    /// <summary>İptal edilmiş ya da kargo ücreti satırı — düzeltmenin konusu değil.</summary>
    Excluded,
}
