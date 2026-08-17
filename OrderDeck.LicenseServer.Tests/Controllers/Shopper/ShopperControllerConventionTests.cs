using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// PR #74'teki PanelControllerConventionTests'in shopper karşılığı. Future
/// Shopper controller'ları eklenirken Bearer-Shopper auth attribute'unu
/// unutmak kolay — bu test CI'da yakar.
/// </summary>
public class ShopperControllerConventionTests
{
    private const string ExpectedAuthScheme = "Bearer-Shopper";
    private const string ShopperNamespace = "OrderDeck.LicenseServer.Controllers.Shopper";

    private static Type[] DiscoverShopperControllers() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsClass
                && !t.IsAbstract
                && t.Namespace == ShopperNamespace
                && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.FullName)
            .ToArray();

    [Fact]
    public void All_shopper_controllers_have_class_level_BearerShopper_authorize()
    {
        var controllers = DiscoverShopperControllers();
        controllers.Should().NotBeEmpty("Shopper namespace'inde controller bekleniyor");

        var offenders = new List<string>();
        foreach (var t in controllers)
        {
            var classAuth = t.GetCustomAttribute<AuthorizeAttribute>(inherit: true);
            if (classAuth is null
                || !string.Equals(classAuth.AuthenticationSchemes, ExpectedAuthScheme, StringComparison.Ordinal))
            {
                offenders.Add($"{t.FullName}: class-level [Authorize(AuthenticationSchemes=\"Bearer-Shopper\")] yok");
            }
        }

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void All_shopper_controllers_have_ApiController_attribute()
    {
        var offenders = DiscoverShopperControllers()
            .Where(t => t.GetCustomAttribute<ApiControllerAttribute>(inherit: true) is null)
            .Select(t => t.FullName)
            .ToList();
        offenders.Should().BeEmpty("[ApiController] ile ModelState validation + ProblemDetails consistency");
    }

    /// <summary>
    /// Anonim shopper ucu = internete açık kapı. Kimlik doğrulaması olmayan bir
    /// uçta rate limit yoksa kaba kuvvet, hesap sayımı ve (SMS/push gönderen
    /// uçlarda) doğrudan maliyet üretimi bedavaya gelir. Bu uçların tamamı
    /// limitsizdi; yeniden limitsiz hâle gelmesin diye kural teste bağlandı.
    /// </summary>
    [Fact]
    public void All_anonymous_shopper_actions_have_rate_limiting()
    {
        var offenders = new List<string>();
        foreach (var t in DiscoverShopperControllers())
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is null) continue;
                if (m.GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>(inherit: true) is not null) continue;
                // Sınıf düzeyinde bir politika da kabul: kuralın amacı ucun bir
                // kovaya bağlı olması, attribute'un nerede durduğu değil.
                if (t.GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>(inherit: true) is not null) continue;
                offenders.Add($"{t.Name}.{m.Name}: [AllowAnonymous] var ama [EnableRateLimiting] yok");
            }
        }

        offenders.Should().BeEmpty();
    }
}
