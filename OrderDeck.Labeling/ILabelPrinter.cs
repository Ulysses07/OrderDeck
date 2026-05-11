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
    void Print(IReadOnlyList<Label> labels, IReadOnlySet<string>? recipientPaysLabelIds = null);
}
