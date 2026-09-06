namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// "!kayıt → DM" özelliğinin küresel bayrağı. VPS .env:
/// <c>InstagramDm__Enabled</c>, <c>InstagramDm__VerifyToken</c>.
/// Meta App Review (instagram_manage_messages advanced) onaylanana kadar
/// Enabled yazılMAZ — webhook uçları 404 döner (IntakeLogin deseni).
/// İmza doğrulaması masaüstü Meta app'inin secret'ıyla yapılır
/// (OrderDeck__Facebook__AppSecret) — ayrı app YOK, webhook o app'e bağlı.
/// </summary>
public sealed class InstagramDmOptions
{
    public const string SectionName = "InstagramDm";

    public bool Enabled { get; set; }

    /// <summary>Meta webhook abonelik doğrulamasındaki hub.verify_token.
    /// Rastgele üretilir, .env + Meta paneline aynı değer yazılır.</summary>
    public string VerifyToken { get; set; } = "";

    public bool Ready => Enabled && !string.IsNullOrWhiteSpace(VerifyToken);
}
