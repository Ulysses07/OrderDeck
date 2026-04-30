using System.Collections.Generic;
using OrderDeck.Core.Sales;

namespace OrderDeck.Labeling;

/// <summary>Etiket yazdırma soyutlaması. Test'lerde fake implementation kullanılır.</summary>
public interface ILabelPrinter
{
    /// <summary>Verilen etiketleri sırayla yazdırır. Boş listede no-op.</summary>
    void Print(IReadOnlyList<Label> labels);
}
