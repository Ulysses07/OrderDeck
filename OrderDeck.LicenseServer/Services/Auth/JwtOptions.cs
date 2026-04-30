namespace OrderDeck.LicenseServer.Services.Auth;

public sealed class JwtOptions
{
    public string SecretKey { get; set; } = "";
    public string Issuer { get; set; } = "";
    public const string CustomerAudience = "orderdeck-customer";
    public const string AdminAudience = "orderdeck-admin";
}
