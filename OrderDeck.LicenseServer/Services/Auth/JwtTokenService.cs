using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace OrderDeck.LicenseServer.Services.Auth;

public sealed class JwtTokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _signing;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
        _signing = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueCustomerToken(Guid customerId, string email)
    {
        var lifetimeMinutes = _options.AccessTokenLifetimeMinutes > 0
            ? _options.AccessTokenLifetimeMinutes
            : 15;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(lifetimeMinutes);
        var token = Build(JwtOptions.CustomerAudience, expiresAt,
            new Claim(TenantClaims.Sub, customerId.ToString()),
            new Claim(TenantClaims.TenantCustomerId, customerId.ToString()),
            new Claim(TenantClaims.PrincipalType, "customer"),
            new Claim("email", email));
        return (token, expiresAt);
    }

    /// <summary>
    /// Operator (staff) token. `sub` = operatorId, `tcid` = License sahibinin
    /// Customer.Id'si. Tenant query'leri tcid'i kullanır → operator owner'ın
    /// tüm verisini görür ama kimlik kendi audit trail'inde kayıtlı.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) IssueOperatorToken(
        Guid operatorId, Guid tenantCustomerId, string email)
    {
        var lifetimeMinutes = _options.AccessTokenLifetimeMinutes > 0
            ? _options.AccessTokenLifetimeMinutes
            : 15;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(lifetimeMinutes);
        var token = Build(JwtOptions.CustomerAudience, expiresAt,
            new Claim(TenantClaims.Sub, operatorId.ToString()),
            new Claim(TenantClaims.TenantCustomerId, tenantCustomerId.ToString()),
            new Claim(TenantClaims.PrincipalType, "operator"),
            new Claim(TenantClaims.OperatorId, operatorId.ToString()),
            new Claim("email", email));
        return (token, expiresAt);
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueAdminToken(Guid adminId, string username)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var token = Build(JwtOptions.AdminAudience, expiresAt,
            new Claim("sub", adminId.ToString()),
            new Claim("username", username));
        return (token, expiresAt);
    }

    /// <summary>
    /// Shopper (müşteri app kullanıcısı) token. `sub` = shopperId. `principal=shopper`
    /// claim'i Bearer-Customer (yayıncı) ve Bearer-Admin'den ayırt eder. `phone`
    /// claim'i sipariş eşleşme join'lerinde okunabilir.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) IssueShopperToken(Guid shopperId, string phone)
    {
        var lifetimeMinutes = _options.AccessTokenLifetimeMinutes > 0
            ? _options.AccessTokenLifetimeMinutes
            : 30;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(lifetimeMinutes);
        var token = Build(JwtOptions.ShopperAudience, expiresAt,
            new Claim("sub", shopperId.ToString()),
            new Claim("principal", "shopper"),
            new Claim("phone", phone));
        return (token, expiresAt);
    }

    private string Build(string audience, DateTimeOffset expiresAt, params Claim[] claims)
    {
        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: _signing);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
