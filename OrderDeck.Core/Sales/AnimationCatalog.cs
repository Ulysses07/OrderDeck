using System.Collections.Generic;
using System.Linq;

namespace OrderDeck.Core.Sales;

/// <summary>
/// Server-side known-animations registry. The list is the source of truth
/// the validation in <see cref="GiveawayService.Start"/> uses to reject
/// unknown ids and fall back to the wheel.
///
/// Phase 1 shipped only "wheel". Phase 2 adds slot-machine, bingo, card-draw.
/// Phase 3 will add the remaining six.
///
/// IMPORTANT: every id added here must have a matching folder under
/// OrderDeck.Overlay/wwwroot/animations/&lt;id&gt;/ AND a matching entry in
/// OrderDeck.Overlay/wwwroot/animations/manifest.json.
/// </summary>
public static class AnimationCatalog
{
    public const string DefaultId = "wheel";

    public static IReadOnlyList<string> KnownIds { get; } = new[]
    {
        "wheel",
        "slot-machine",
        "bingo",
        "card-draw",
        "magic-hat",
        "spotlight-grid",
        "eliminator",
    };

    public static bool IsKnown(string id) =>
        !string.IsNullOrWhiteSpace(id) && KnownIds.Contains(id);
}
