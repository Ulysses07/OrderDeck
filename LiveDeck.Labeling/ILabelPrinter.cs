using System.Collections.Generic;
using LiveDeck.Core.Sales;

namespace LiveDeck.Labeling;

/// <summary>Etiket yazdırma soyutlaması. Test'lerde fake implementation kullanılır.</summary>
public interface ILabelPrinter
{
    /// <summary>Verilen etiketleri sırayla yazdırır. Boş listede no-op.</summary>
    void Print(IReadOnlyList<Label> labels);
}
