using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Mimari guard rail (architectural debt audit'inden, 2026-05-20):
///
/// Tüm Panel controller'ları "Bearer-Customer" auth scheme'ı altında çalışmak
/// ZORUNDA. Bu scheme JWT customer claim'ini parse eder ve
/// <c>User.GetTenantCustomerId()</c>'in nonempty Guid dönmesini sağlar. Bu
/// claim olmadan controller içindeki tüm tenant filtreleri (örn.
/// <c>licenseIds.Contains(...)</c>) hatalı veya boş çalışır — sessizce
/// herhangi bir tenant'ın verisini sızdırma riski oluşur.
///
/// EF Core HasQueryFilter ile global tenant filter daha güçlü bir savunma
/// olurdu, ancak DbContext'e ITenantAccessor inject etmek + admin endpoint'ler
/// için IgnoreQueryFilters çağrıları gerektirir (yüksek surface area). Bu
/// convention test çok daha cheap — yeni Panel controller eklerken auth
/// attribute unutulursa CI'de bozulur.
/// </summary>
public class PanelControllerConventionTests
{
    private const string ExpectedAuthScheme = "Bearer-Customer";
    private const string PanelNamespace = "OrderDeck.LicenseServer.Controllers.Panel";

    private static Type[] DiscoverPanelControllers() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t =>
                t.IsClass
                && !t.IsAbstract
                && t.Namespace == PanelNamespace
                && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.FullName)
            .ToArray();

    [Fact]
    public void All_panel_controllers_require_BearerCustomer_authentication_scheme()
    {
        var controllers = DiscoverPanelControllers();
        controllers.Should().NotBeEmpty(
            "the test must find Panel controllers via reflection — empty " +
            "list means the namespace moved or this test is now stale");

        var offenders = controllers
            .Where(t =>
            {
                var attr = t.GetCustomAttribute<AuthorizeAttribute>(inherit: true);
                return attr is null
                    || !string.Equals(attr.AuthenticationSchemes, ExpectedAuthScheme, StringComparison.Ordinal);
            })
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "every Panel controller must be class-level decorated with " +
            $"[Authorize(AuthenticationSchemes = \"{ExpectedAuthScheme}\")] " +
            "so the customer JWT scheme parses claims and tenant filters can " +
            "rely on a non-empty CustomerId");
    }

    [Fact]
    public void All_panel_controllers_are_marked_ApiController()
    {
        var offenders = DiscoverPanelControllers()
            .Where(t => t.GetCustomAttribute<ApiControllerAttribute>(inherit: true) is null)
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "[ApiController] enables ModelState validation + ProblemDetails " +
            "responses; missing it means inconsistent error shapes");
    }
}
