using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OrderDeck.Licensing.Api;

namespace OrderDeck.Licensing.Backup;

public sealed class BackupClient : IBackupClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public BackupClient(HttpClient http) => _http = http;

    /// <summary>
    /// Yedeği yükler. 409 alırsa <b>bir kez</b> yeniden dener: sunucu aynı
    /// müşterinin eşzamanlı iki yüklemesinden birini kota hakemiyle geri
    /// çeviriyor (bkz. <c>BackupQuotaCounter</c>) ve kaybeden istek yeniden
    /// denendiğinde güncel toplamı görüp ya geçiyor ya da dürüst bir 507
    /// alıyor. Tek deneme yeter: hakemi kaybetmek için karşımızda gerçekten
    /// eşzamanlı bir yükleme olması gerekiyor, o da bu sırada bitmiş olur.
    /// Sınırsız denemek, kalıcı bir hatayı sonsuz döngüye çevirirdi.
    /// </summary>
    public async Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var content = new ByteArrayContent(zipPayload);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");

            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/backups") { Content = content };
            req.Headers.Add("X-Backup-Sha256", sha256Hex);
            if (!string.IsNullOrEmpty(machineName))
                req.Headers.Add("X-Machine-Name", machineName);

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict && attempt == 0)
                continue;

            await EnsureSuccessOrThrowAsync(resp, ct);
            var meta = await resp.Content.ReadFromJsonAsync<BackupMetadata>(JsonOpts, ct);
            return meta ?? throw new LicenseApiUnknownException((int)resp.StatusCode, "Empty response from upload");
        }
    }

    public async Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/v1/me/backups", ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
        var list = await resp.Content.ReadFromJsonAsync<List<BackupMetadata>>(JsonOpts, ct);
        return list ?? new List<BackupMetadata>();
    }

    public async Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/v1/me/backups/{backupId}/download", ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task DeleteAsync(Guid backupId, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/api/v1/me/backups/{backupId}", ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new LicenseApiUnknownException((int)resp.StatusCode, body);
    }
}
