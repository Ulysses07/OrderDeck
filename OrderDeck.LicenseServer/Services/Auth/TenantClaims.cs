using System.Security.Claims;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// JWT claim name'leri ve principal'dan tenant customer id çözüm helper'ı.
/// Operator auth ile birlikte (PR-5 Faz 2, 2026-05-14) `sub` claim'i artık
/// authenticated principal id'sini taşıyor (Customer veya Operator). Tenant
/// kimliği için yeni `tcid` (tenant customer id) claim'i — tüm tenant
/// query'leri bunu kullanır.
///
/// Backward compat: `tcid` claim'i yoksa (eski token'lar veya henüz refactor
/// edilmemiş kod path'leri), `sub` doğrudan tenant id sayılır (Customer token).
/// </summary>
public static class TenantClaims
{
    /// <summary>Sub: authenticated principal id (Customer.Id veya OperatorUser.Id).</summary>
    public const string Sub = "sub";

    /// <summary>tcid: tenant customer id — License sahibinin Customer.Id'si.
    /// Hem customer hem operator token'larında set edilir.</summary>
    public const string TenantCustomerId = "tcid";

    /// <summary>principal: "customer" veya "operator".</summary>
    public const string PrincipalType = "principal";

    /// <summary>op: OperatorUser.Id, operator token'larında set edilir.</summary>
    public const string OperatorId = "op";

    /// <summary>
    /// JWT'den tenant customer id'yi çıkarır. Yeni `tcid` claim'i varsa onu,
    /// yoksa `sub`'a düşer (legacy customer token).
    /// </summary>
    /// <exception cref="InvalidOperationException">İkisi de yoksa.</exception>
    public static Guid GetTenantCustomerId(this ClaimsPrincipal principal)
    {
        var tcid = principal.FindFirst(TenantCustomerId)?.Value;
        if (!string.IsNullOrEmpty(tcid))
            return Guid.Parse(tcid);

        var sub = principal.FindFirst(Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub/tcid claim missing");
        return Guid.Parse(sub);
    }

    /// <summary>
    /// Authenticated principal'ın "operator" olup olmadığını döner. Customer
    /// token'larında false döner.
    /// </summary>
    public static bool IsOperator(this ClaimsPrincipal principal) =>
        principal.FindFirst(PrincipalType)?.Value == "operator";

    /// <summary>
    /// Operator id (varsa). Customer token'larında null.
    /// </summary>
    public static Guid? GetOperatorId(this ClaimsPrincipal principal)
    {
        var op = principal.FindFirst(OperatorId)?.Value;
        if (string.IsNullOrEmpty(op)) return null;
        return Guid.Parse(op);
    }
}
