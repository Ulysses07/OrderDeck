using System.Net;
using System.Text;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Netgsm <c>/iys/search</c> ucunu GERÇEK yanıt biçimiyle taklit eder; gerçek
/// <c>NetgsmIysClient</c>'ın önüne takılır. Biçimin kendisi sınanan şey:
/// <b>tek alıcılı sorguda <c>query</c> NESNE, çok alıcılıda DİZİ</b> döner
/// (2026-10-22 prod ölçümü — tek alıcılı yanıtı okuyamayan istemci 24 onayı
/// "Süresi Doldu"ya düşürdü). Alan seti prod yanıtından alındı.
///
/// <para>Durumu verilmeyen alıcıya İYS'nin kendisi gibi <c>RET</c> der: İYS
/// "kayıt yok" ile "reddetti"yi ayırt etmiyor (2026-09-17 ölçümü).</para>
/// </summary>
internal sealed class NetgsmIysSearchHandler : HttpMessageHandler
{
    /// <summary>Alıcı (E.164) → İYS'nin vereceği durum metni ("ONAY"/"RET").</summary>
    public Dictionary<string, string> Statuses { get; } = new(StringComparer.Ordinal);

    /// <summary>Her /iys/search isteğinin alıcı listesi, geliş sırasıyla.</summary>
    public List<string[]> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/iys/search", StringComparison.Ordinal) != true)
            throw new NotSupportedException($"Beklenmeyen uç: {request.RequestUri}");

        var body = await request.Content!.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var recipients = doc.RootElement.GetProperty("body").GetProperty("data")
            .EnumerateArray()
            .Select(r => r.GetProperty("recipient").GetString()!)
            .ToArray();
        Requests.Add(recipients);

        var transactionId = Guid.NewGuid().ToString("N");
        var rows = recipients
            .Select(r => Row(r, Statuses.GetValueOrDefault(r, "RET"), transactionId))
            .ToArray();
        // Netgsm'in biçimi: tek satır diziye SARILMAZ.
        object query = rows.Length == 1 ? rows[0] : rows;
        var json = JsonSerializer.Serialize(new { code = "0", error = "false", query });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static Dictionary<string, string> Row(string recipient, string status, string transactionId)
        => new()
        {
            ["consentDate"] = "",
            ["source"] = "",
            ["recipientType"] = "BIREYSEL",
            ["status"] = status,
            ["type"] = "MESAJ",
            ["recipient"] = recipient,
            ["retailerCode"] = "",
            ["creationDate"] = "",
            ["retailerTitle"] = "",
            ["retailerAccessCount"] = "",
            ["transactionId"] = transactionId,
        };
}
