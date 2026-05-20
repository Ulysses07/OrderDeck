namespace OrderDeck.LicenseServer.Services.Auth;

public sealed class JwtOptions
{
    public string SecretKey { get; set; } = "";
    public string Issuer { get; set; } = "";

    /// <summary>Customer access-token lifetime (short — paired with refresh-token rotation).</summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 15;

    /// <summary>Customer refresh-token lifetime. Refresh tokens rotate on every use.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    public const string CustomerAudience = "orderdeck-customer";
    public const string AdminAudience = "orderdeck-admin";
    public const string ShopperAudience = "orderdeck-shopper";
}
