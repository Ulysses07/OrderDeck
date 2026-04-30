namespace LiveDeck.Core.Sales;

/// <summary>
/// One unique <c>(Platform, Username)</c> who entered a giveaway by typing its keyword.
/// <see cref="IsWinner"/> is set when the giveaway is drawn.
/// </summary>
public sealed record GiveawayParticipant(
    string Id,
    string GiveawayId,
    string CustomerId,
    string Platform,
    string Username,
    long EnteredAt,
    bool IsWinner);
