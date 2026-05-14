using FluentAssertions;
using OrderDeck.LicenseServer.Controllers.Licenses;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Push;

/// <summary>
/// Push notification title/body formatlama saf unit testleri — controller'a
/// hit etmeden helper'ları direkt çağırır.
/// </summary>
public class NotificationFormattingTests
{
    [Fact]
    public void Single_payment_renders_payer_and_amount()
    {
        var (title, body) = LicensesPaymentsSyncController.BuildPaymentNotification(
            new[] { ("Ali Veli", 250.75m) });

        title.Should().Be("Yeni dekont");
        body.Should().Be("Ali Veli — 250,75 ₺");
    }

    [Fact]
    public void Multiple_payments_renders_count_and_total()
    {
        var (title, body) = LicensesPaymentsSyncController.BuildPaymentNotification(
            new[] { ("A", 100m), ("B", 200m), ("C", 350.5m) });

        title.Should().Be("Yeni dekont");
        body.Should().Contain("3 yeni dekont").And.Contain("650,50");
    }

    [Fact]
    public void Order_batch_always_renders_count_and_total()
    {
        var (title, body) = LicensesSessionsSyncController.BuildOrderNotification(
            new[] { 50m, 100m, 75.25m });

        title.Should().Be("Yeni sipariş");
        body.Should().Contain("3 yeni sipariş").And.Contain("225,25");
    }
}
