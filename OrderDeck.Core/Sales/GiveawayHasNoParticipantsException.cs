namespace OrderDeck.Core.Sales;

/// <summary>
/// Thrown by <see cref="GiveawayService.Draw"/> when the keyword captured zero
/// participants. Distinct from generic InvalidOperationException so the WPF
/// shell can catch it specifically and show a Turkish "no entries" prompt
/// rather than letting an empty winner list propagate to the overlay
/// (which animates a blank wheel for ~10s and confuses the operator).
/// </summary>
public sealed class GiveawayHasNoParticipantsException : System.InvalidOperationException
{
    public string Keyword { get; }

    public GiveawayHasNoParticipantsException(string keyword)
        : base($"Çekiliş için hiç katılımcı yok (anahtar kelime: '{keyword}').")
    {
        Keyword = keyword;
    }
}
