using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Observability;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Netgsm REST v2 ile kiracı kimlikli ticari gönderim. Kardeşi
/// <see cref="NetgsmSmsSender"/> (merkezi/Transactional); istek biçimi aynı,
/// fark: (a) Basic auth ve msgheader ÇAĞRI parametresinden gelir,
/// (b) iysfilter daima <see cref="NetgsmOptions.CommercialIysFilter"/>,
/// (c) başarıda jobid döndürülür, (d) temiz ret <see cref="NetgsmSmsException"/>
/// olarak kod taşır (§3.4 sınıflandırması için).
/// </summary>
public sealed class NetgsmTenantSmsSender : ITenantSmsSender
{
    private readonly HttpClient _http;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<NetgsmTenantSmsSender> _log;

    public NetgsmTenantSmsSender(
        HttpClient http, IOptions<NetgsmOptions> opt, ILogger<NetgsmTenantSmsSender> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<string?> SendAsync(
        TenantSmsCredentials credentials, string toPhone, string message,
        CancellationToken ct = default)
    {
        var no = ToNetgsmNo(toPhone);

        var payload = new Dictionary<string, object?>
        {
            ["msgheader"] = credentials.Header,
            ["messages"] = new[] { new { msg = message, no } },
            // Bu yol tanımı gereği ticari: Netgsm alıcıyı kendi İYS kaydına
            // göre de eler (çift kapı — bizim IysConsentGate + Netgsm filtresi).
            ["iysfilter"] = _opt.CommercialIysFilter,
        };
        if (!string.IsNullOrWhiteSpace(_opt.Encoding))
            payload["encoding"] = _opt.Encoding;
        var json = JsonSerializer.Serialize(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_opt.BaseUrl.TrimEnd('/')}/sms/rest/v2/send");
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{credentials.UserCode}:{credentials.Password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            // Ağ hatası: mesaj Netgsm'e ULAŞMIŞ olabilir. NetgsmSmsException'a
            // SARMA — o sınıf "hiçbir şey gitmedi" garantisi taşıyor.
            _log.LogWarning(ex, "Netgsm tenant SMS failed (network) for {Phone}",
                PiiMasker.MaskPhone(toPhone));
            throw;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var (code, jobId) = ParseResponse(body);

        if (resp.IsSuccessStatusCode && code is "00" or "01" or "02")
        {
            _log.LogInformation("Netgsm tenant SMS sent to {Phone} (jobid={JobId})",
                PiiMasker.MaskPhone(toPhone), jobId ?? "-");
            return jobId;
        }

        // Yanıt alındı ve kabul kodu yok → Netgsm işi reddetti, hiçbir SMS
        // gitmedi. Gövde LOG'A yazılır ama İSTİSNAYA yazılmaz: istisna mesajı
        // alıcı satırının Error alanına düşüyor ve panelde görünecek —
        // ham gövde oraya taşınmamalı (LastError kuralının kardeşi).
        _log.LogWarning("Netgsm tenant SMS rejected for {Phone}: status={Status} body={Body}",
            PiiMasker.MaskPhone(toPhone), (int)resp.StatusCode, body);
        throw new NetgsmSmsException(code,
            $"Netgsm reddetti (code={code ?? "?"}, http={(int)resp.StatusCode}).");
    }

    // Kardeşi NetgsmSmsSender.ToNetgsmNo — bilinçli kopya, iki satır.
    private static string ToNetgsmNo(string e164)
    {
        var digits = new string(e164.Where(char.IsDigit).ToArray());
        return digits.StartsWith("90") && digits.Length == 12 ? digits[2..] : digits;
    }

    /// <summary>Yanıttan (code, jobid) çıkarır. JSON değilse düz metin kod
    /// denenir ("30", "00 ..." gibi). Hiçbiri değilse (null, null).</summary>
    private static (string? Code, string? JobId) ParseResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                string? code = null, jobId = null;
                if (doc.RootElement.TryGetProperty("code", out var codeEl))
                    code = codeEl.ValueKind == JsonValueKind.String
                        ? codeEl.GetString() : codeEl.ToString();
                if (doc.RootElement.TryGetProperty("jobid", out var jobEl))
                    jobId = jobEl.ValueKind == JsonValueKind.String
                        ? jobEl.GetString() : jobEl.ToString();
                return (code, jobId);
            }
            // Çıplak "40" geçerli JSON'dur (sayı) — düz metin koda düş.
        }
        catch (JsonException)
        {
            // Düz metin gövde ("40", "00 ...") — aşağıda denenir.
        }
        var trimmed = body.Trim().Trim('"');
        var head = trimmed.Split(' ')[0];
        return (head.Length is 2 or 3 && head.All(char.IsDigit) ? head : null, null);
    }
}
