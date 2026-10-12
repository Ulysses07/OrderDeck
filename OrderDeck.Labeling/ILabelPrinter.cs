using System.Collections.Generic;
using OrderDeck.Core.Sales;

namespace OrderDeck.Labeling;

/// <summary>Etiket yazdırma soyutlaması. Test'lerde fake implementation kullanılır.</summary>
public interface ILabelPrinter
{
    /// <summary>Verilen etiketleri sırayla yazdırır. Boş listede no-op.</summary>
    /// <param name="labels">Yazdırılacak etiketler.</param>
    /// <param name="recipientPaysLabelIds">Kargo PR F (2026-05-11): etikette
    /// "ALICI ÖDEMELİ" kırmızı yazı render edilecek label id'leri. Null/boş =
    /// hiçbir etikette mark yok (geriye uyumlu çağrılar default davranışı korur).
    /// Caller müşterinin RecipientPaysActive bayrağından bu set'i oluşturur.</param>
    /// <param name="correctionLabelIds">Fiyat düzeltme (K5, 2026-10-12): fiyatı düzeltilmiş ve
    /// YENİDEN basılan etiketler — kâğıtta küçük "DÜZELTME" işareti, eski (yanlış fiyatlı) kâğıtla
    /// karışmasın. Null/boş = işaret yok.</param>
    void Print(IReadOnlyList<Label> labels, IReadOnlySet<string>? recipientPaysLabelIds = null,
        IReadOnlySet<string>? correctionLabelIds = null);

    /// <summary>Çekiliş kazananları için etiket basar. Normal satış etiketinden
    /// farkı: fiyat yerine "HEDİYE" yazar (bu etiketler satış değil). İçerik =
    /// kazanan adı + çekiliş kodu (Label.MessageText). Boş listede no-op.</summary>
    void PrintGiftLabels(IReadOnlyList<Label> labels);
}
