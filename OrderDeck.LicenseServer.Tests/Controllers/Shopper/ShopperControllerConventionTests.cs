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
}
