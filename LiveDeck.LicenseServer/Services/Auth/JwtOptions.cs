namespace LiveDeck.LicenseServer.Services.Auth;

public sealed class JwtOptions
{
    public string SecretKey { get; set; } = "";
    public string Issuer { get; set; } = "";
    public const string CustomerAudience = "livedeck-customer";
    public const string AdminAudience = "livedeck-admin";
}
