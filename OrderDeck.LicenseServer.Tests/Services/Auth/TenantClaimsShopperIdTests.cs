using System.Security.Claims;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class TenantClaimsShopperIdTests
{
    [Fact]
    public void GetShopperId_reads_sub_claim()
    {
        var id = Guid.NewGuid();
        var principal = MakePrincipal(("sub", id.ToString()));
        principal.GetShopperId().Should().Be(id);
    }

    [Fact]
    public void GetShopperId_falls_back_to_NameIdentifier()
    {
        // JWT middleware bazı durumlarda `sub` → NameIdentifier'a map ediyor.
        var id = Guid.NewGuid();
        var principal = MakePrincipal((ClaimTypes.NameIdentifier, id.ToString()));
        principal.GetShopperId().Should().Be(id);
    }

    [Fact]
    public void GetShopperId_returns_null_when_no_claim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        principal.GetShopperId().Should().BeNull();
    }

    [Fact]
    public void GetShopperId_returns_null_when_claim_not_a_guid()
    {
        var principal = MakePrincipal(("sub", "not-a-guid"));
        principal.GetShopperId().Should().BeNull();
    }

    [Fact]
    public void GetShopperId_prefers_sub_over_NameIdentifier()
    {
        var subId = Guid.NewGuid();
        var nameId = Guid.NewGuid();
        var principal = MakePrincipal(
            ("sub", subId.ToString()),
            (ClaimTypes.NameIdentifier, nameId.ToString()));
        principal.GetShopperId().Should().Be(subId);
    }

    private static ClaimsPrincipal MakePrincipal(params (string type, string value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.type, c.value)),
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }
}
