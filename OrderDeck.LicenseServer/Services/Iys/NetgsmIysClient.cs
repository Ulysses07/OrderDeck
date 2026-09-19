using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Netgsm İYS istemcisi. <c>POST {BaseUrl}/iys/add</c> ve <c>/iys/search</c>.
///
/// <para><b>Kimlik gövdede</b>, HTTP header'da değil — SMS ucundaki Basic auth
/// deseni burada geçerli DEĞİL (2026-09-17'de canlı çağrıyla doğrulandı).</para>
///
/// <para>Ham yanıt log'a değil çağırana döner; olay tablosuna orada yazılır.
/// Log'a yalnız maskeli telefon çıkar (KVKK).</para>
///
/// <para><b>Kimlik global DEĞİL.</b> Her çağrı bir
/// <see cref="IysAccountContext"/> alır; <c>_opt</c>'tan yalnız
/// <see cref="NetgsmOptions.BaseUrl"/> okunur, çünkü Netgsm'in API adresi
/// tüm yayıncılar için aynıdır.</para>
/// </summary>
public sealed class NetgsmIysClient : IIysClient
{
    // TR yerel saat: İYS onay tarihini bu ofsette bekliyor.
    private static readonly TimeSpan TrOffset = TimeSpan.FromHours(3);

    private readonly HttpClient _http;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<NetgsmIysClient> _log;

    public NetgsmIysClient(HttpClient http, IOptions<NetgsmOptions> opt, ILogger<NetgsmIysClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<IysAddResult> AddAsync(
        IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
        CancellationToken ct = default)
    {
        var data = items.Select(i => new Dictionary<string, object?>
        {
            ["type"] = i.ChannelType,
            ["source"] = i.SourceCode,
            ["recipient"] = i.Recipient,
            ["recipientType"] = i.RecipientType,
            ["status"] = i.Status == IysConsentStatus.Onay ? "ONAY" : "RET",
            ["consentDate"] = i.ConsentDate.ToOffset(TrOffset)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            // refid mükerrer push'u zararsız kılar: aynı kaydı iki kez
            // itersek İYS ikinci satırı yeni bir olay saymaz.
            ["refid"] = i.RefId,
        }).ToArray();

        var (code, body) = await PostAsync(account, "add", data, ct);
        return new IysAddResult(code, body, Queued: code == "0");
    }

    public async Task<IysSearchResult> SearchAsync(
        IysAccountContext account, IReadOnlyList<string> recipients,
        CancellationToken ct = default)
    {
        var data = recipients.Select(r => new Dictionary<string, object?>
        {
            ["type"] = "MESAJ",
            ["recipient"] = r,
            ["recipientType"] = "BIREYSEL",
        }).ToArray();

        var (code, body) = await PostAsync(account, "search", data, ct);

        var statuses = new Dictionary<string, IysConsentStatus>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("query", out var query)
                && query.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in query.EnumerateArray())
                {
                    var recipient = row.TryGetProperty("recipient", out var r) ? r.GetString() : null;
                    var status = row.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (string.IsNullOrWhiteSpace(recipient)) continue;

                    // "Kayıt yok" ile "reddetti" ayırt EDİLEMEZ: İYS ikisine de
                    // RET diyor (2026-09-17'de kayıtsız numarayla doğrulandı) ve
                    // transactionId sorgu kimliği olduğu için ayırt edici değil.
                    // Fail-closed: ONAY olmayan her şey gönderimi keser.
                    statuses[recipient] = status switch
                    {
                        "ONAY" => IysConsentStatus.Onay,
                        "RET" => IysConsentStatus.Ret,
                        _ => IysConsentStatus.Unknown,
                    };
                }
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "İYS search yanıtı ayrıştırılamadı (code={Code})", code);
        }

        return new IysSearchResult(code, body, statuses);
    }

    private async Task<(string Code, string Body)> PostAsync(
        IysAccountContext account, string path, object data, CancellationToken ct)
    {
        var payload = new
        {
            header = new
            {
                username = account.UserCode,
                password = account.Password,
                brandCode = account.BrandCode,
            },
            body = new { data },
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{_opt.BaseUrl.TrimEnd('/')}/iys/{path}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var code = ReadCode(body);

        // Kalıcı YAPILANDIRMA hatası: her kayıt aynı hatayla düşer, sırayla
        // denemek yalnız zaman harcar. Hangi markanın düştüğü mesaja yazılır:
        // çok kiracıda "İYS ayarı bozuk" tek başına eyleme geçirilebilir değil.
        if (code is "30" or "60")
            throw new IysConfigurationException(code,
                $"İYS yapılandırma hatası (code={code}, brand={account.BrandCode}). "
                + "Marka kodu/kimlik kontrol edilmeli.");

        return (code, body.Length > 2000 ? body[..2000] : body);
    }

    // code alanı bazen string ("0"), bazen sayı dönebiliyor; ikisini de kabul et.
    private static string ReadCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var el))
                return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        }
        catch (JsonException) { /* düz metin yanıt */ }
        return body.Trim().Trim('"');
    }
}
