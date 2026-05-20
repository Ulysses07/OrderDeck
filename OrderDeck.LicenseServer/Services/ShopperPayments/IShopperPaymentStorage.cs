namespace OrderDeck.LicenseServer.Services.ShopperPayments;

public interface IShopperPaymentStorage
{
    Task<string> UploadAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default);
    Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}
