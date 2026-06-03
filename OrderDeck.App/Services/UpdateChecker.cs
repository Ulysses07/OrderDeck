using System;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.Licensing.Api;

namespace OrderDeck.App.Services;

/// <summary>
/// Açılışta sunucudan en son uygulama sürümünü sorar ve mevcut sürümle
/// karşılaştırır. Best-effort: ağ/parse hatası veya boş LatestVersion → null
/// (sessiz geç, kullanıcıyı rahatsız etme).
/// </summary>
public sealed class UpdateChecker
{
    private readonly LicenseApiClient _api;

    public UpdateChecker(LicenseApiClient api) => _api = api;

    public sealed record UpdateInfo(string LatestVersion, string DownloadUrl);

    /// <summary>Yeni sürüm varsa bilgisini döner; yoksa null. <paramref name="currentVersion"/>
    /// çağıran tarafça verilir (App assembly sürümü) — test edilebilirlik için.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            var info = await _api.GetLatestAppVersionAsync(ct);
            if (info is null || string.IsNullOrWhiteSpace(info.LatestVersion))
                return null;
            if (!Version.TryParse(info.LatestVersion, out var latest))
                return null;
            return latest > currentVersion
                ? new UpdateInfo(info.LatestVersion!, info.DownloadUrl)
                : null;
        }
        catch
        {
            // Güncelleme kontrolü asla akışı bozmaz.
            return null;
        }
    }
}
