using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Services.ShopperPayments;

public interface IShopperPaymentStorage
{
    Task<string> UploadAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default);
    Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    /// <summary>Lisansın dekont önekindeki (<see cref="ShopperPaymentKeys.LicensePrefix"/>) TÜM anahtarlar; sayfalama içeride.
    /// Yayıncı silmesi (KVKK) için.</summary>
    Task<IReadOnlyList<string>> ListLicenseObjectsAsync(Guid licenseId, CancellationToken ct = default);

    /// <summary>Toplu silme. Fırlatmaz: silinemeyen her anahtar hata metniyle döner, çağıran yeniden dener.</summary>
    Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys, CancellationToken ct = default);
}

/// <summary>Dekont nesne anahtarı <c>payments/{lisans:N}/{rastgele:N}.pdf</c>. Yazan
/// (<see cref="ShopperPaymentSubmissionService"/>) ve yayıncı silmesi öneki buradan alır: ikisi ayrışsaydı silme sessizce boş
/// liste görür, dekontlar kovada kalırdı.</summary>
public static class ShopperPaymentKeys
{
    public static string LicensePrefix(Guid licenseId) => $"payments/{licenseId:N}/";
}
