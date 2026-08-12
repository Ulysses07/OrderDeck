using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Auth;

/// <summary>
/// Stok elemanına AÇIK uçların envanteri (Faz 1a).
///
/// Yalnız açık küme sabitleniyor, panelin tamamı değil: işaretlenmeyen bir uç
/// zaten <b>kapalı</b> doğar (<see cref="StockStaffScopeFilter"/> varsayılan-kapalı),
/// yani unutmanın bedeli güvenli tarafta kalır ve alarma gerek yoktur. Tehlike
/// tek yönlü — <b>genişleme</b>. <c>[AllowStockStaff]</c> yazan biri bu testi
/// kırar ve kararını buraya yazarak bilinçli olarak kaydetmek zorunda kalır.
///
/// Sınıf seviyesinde işaretleme yapı gereği imkânsız
/// (<see cref="AllowStockStaffAttribute"/> yalnız <c>AttributeTargets.Method</c>),
/// bu yüzden burada ayrıca aranmıyor — derleyici zaten geçirmiyor.
/// </summary>
public class StockStaffEndpointInventoryTests
{
    private const string PanelNamespace = "OrderDeck.LicenseServer.Controllers.Panel";

    /// <summary>
    /// Spec (2026-08-07 stok sistemi): stok elemanı "ürün kartı açar, stok girer,
    /// etiket basar; müşteri, sipariş, ödeme ve ciro bilgilerini göremez".
    /// Buraya bir satır eklemeden önce ucun bu cümleyi ihlal etmediğini doğrula.
    /// </summary>
    private static readonly string[] ExpectedOpenEndpoints =
    [
        "PanelCategoriesController.List",
        "PanelCategoriesController.Create",
        "PanelCategoriesController.Update",
        "PanelCategoriesController.Move",
        "PanelCategoriesController.Delete",

        "PanelProductsController.List",
        "PanelProductsController.NextCode",
        "PanelProductsController.Get",
        "PanelProductsController.Create",
        "PanelProductsController.Update",
        "PanelProductsController.Delete",

        "PanelProductVariantsController.Create",
        "PanelProductVariantsController.CreateBulk",
        "PanelProductVariantsController.Update",
        "PanelProductVariantsController.Delete",

        "PanelProductPhotoController.CreateUploadUrl",
        "PanelProductPhotoController.Attach",
        "PanelProductPhotoController.GetUrl",
        "PanelProductPhotoController.Delete",
    ];

    private static string[] DiscoverOpenEndpoints() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t =>
                t.IsClass
                && !t.IsAbstract
                && t.Namespace == PanelNamespace
                && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m =>
                    !m.IsSpecialName
                    && m.GetCustomAttribute<AllowStockStaffAttribute>(inherit: true) is not null)
                .Select(m => $"{t.Name}.{m.Name}"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Stock_staff_reaches_exactly_the_catalog_endpoints()
    {
        var open = DiscoverOpenEndpoints();

        open.Should().BeEquivalentTo(
            ExpectedOpenEndpoints,
            "stok elemanına açık uç kümesi bilerek sabit tutuluyor. Bu test "
          + "kırıldıysa: [AllowStockStaff] eklediysen, ucun spec'teki 'müşteri, "
          + "sipariş, ödeme ve ciro bilgilerini göremez' kuralını ihlal "
          + "etmediğini doğrula ve ExpectedOpenEndpoints listesine ekle; "
          + "kaldırdıysan listeden çıkar");
    }
}
