namespace OrderDeck.Licensing.Api;

/// <summary>
/// Server (Faz 0c-1) shopper-code PUT'ta validation hatası verdiğinde fırlatılır.
/// <see cref="ErrorCode"/> server'ın Problem.Title değeridir:
///   "empty" / "length" / "format" / "reserved" / "profanity" / "cooldown" / "taken"
/// UI bunu Türkçeye map'ler.
/// </summary>
public sealed class ShopperCodeValidationException : Exception
{
    public string ErrorCode { get; }
    public ShopperCodeValidationException(string errorCode) : base(errorCode) => ErrorCode = errorCode;
}
