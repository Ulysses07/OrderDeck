using FluentAssertions;
using OrderDeck.LicenseServer.Services.ShopperPayments;

namespace OrderDeck.LicenseServer.Tests.Services.ShopperPayments;

public class StubShopperPaymentStorageTests
{
    [Fact]
    public async Task Upload_stores_bytes_and_returns_key()
    {
        var stub = new StubShopperPaymentStorage();
        var bytes = new byte[] { 1, 2, 3 };
        var key = await stub.UploadAsync("payments/abc.pdf", bytes, "application/pdf");
        key.Should().Be("payments/abc.pdf");
        stub.Contains("payments/abc.pdf").Should().BeTrue();
        stub.GetBytes("payments/abc.pdf").Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task CreateDownloadUrl_returns_stub_url()
    {
        var stub = new StubShopperPaymentStorage();
        await stub.UploadAsync("p/x.pdf", new byte[] { 1 }, "application/pdf");
        var url = await stub.CreateDownloadUrlAsync("p/x.pdf");
        url.Should().Be("stub://payments/p/x.pdf");
    }

    [Fact]
    public async Task Delete_removes_object()
    {
        var stub = new StubShopperPaymentStorage();
        await stub.UploadAsync("p/x.pdf", new byte[] { 1 }, "application/pdf");
        await stub.DeleteAsync("p/x.pdf");
        stub.Contains("p/x.pdf").Should().BeFalse();
    }
}
