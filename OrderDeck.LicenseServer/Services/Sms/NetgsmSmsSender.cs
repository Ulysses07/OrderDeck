using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Observability;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Netgsm REST v2 SMS gönderici. <c>POST {BaseUrl}/sms/rest/v2/send</c>,
/// HTTP Basic auth (usercode:password). Email tarafındaki SmtpEmailSender
/// gibi: hata propagate eder (telefon log'da maskelenir, KVKK).
/// </summary>
public sealed class NetgsmSmsSender : ISmsSender
{
    private readonly HttpClient _http;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<NetgsmSmsSender> _log;

    public NetgsmSmsSender(HttpClient http, IOptions<NetgsmOptions> opt, ILogger<NetgsmSmsSender> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task SendAsync(string toPhone, string message, CancellationToken ct = default)
    {
        // Netgsm 10 haneli (5XXXXXXXXX) bekler; PhoneNormalizer +90XXXXXXXXXX verir.
        var no = ToNetgsmNo(toPhone);

        var payload = new
        {
            msgheader = _opt.Header,
            messages = new[] { new { msg = message, no } },
        };
        var json = JsonSerializer.Serialize(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_opt.BaseUrl.TrimEnd('/')}/sms/rest/v2/send");
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_opt.UserCode}:{_opt.Password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Netgsm SMS send failed (network) for {Phone}",
                PiiMasker.MaskPhone(toPhone));
            throw;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode || !IsNetgsmSuccess(body))
        {
            _log.LogWarning("Netgsm SMS rejected for {Phone}: status={Status} body={Body}",
                PiiMasker.MaskPhone(toPhone), (int)resp.StatusCode, body);
            throw new InvalidOperationException(
                $"Netgsm SMS rejected (status={(int)resp.StatusCode}).");
        }

        _log.LogInformation("Netgsm SMS sent to {Phone}", PiiMasker.MaskPhone(toPhone));
    }

    // +90XXXXXXXXXX → XXXXXXXXXX (10 hane). Beklenmedik format → olduğu gibi
    // (sadece + temizlenir) bırakılır; Netgsm reddederse zaten exception olur.
    private static string ToNetgsmNo(string e164)
    {
        var digits = new string(e164.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("90") && digits.Length == 12)
            return digits[2..];
        return digits;
    }

    // Netgsm REST v2 başarı kodları "00"/"01"/"02" (gönderim kabul edildi).
    private static bool IsNetgsmSuccess(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var codeEl))
            {
                var code = codeEl.ValueKind == JsonValueKind.String
                    ? codeEl.GetString()
                    : codeEl.ToString();
                return code is "00" or "01" or "02";
            }
        }
        catch (JsonException)
        {
            // Bazı hata yanıtları düz metin "30", "40" vb. döndürür.
        }
        var trimmed = body.Trim().Trim('"');
        return trimmed is "00" or "01" or "02" || trimmed.StartsWith("00 ") || trimmed.StartsWith("01 ") || trimmed.StartsWith("02 ");
    }
}
