using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Gerçek WhatsApp Cloud API göndereni: <c>POST {graph}/{version}/{phoneNumberId}/messages</c>,
/// Bearer = tenant Access Token. Text (service) + template mesajı destekler.
/// Başarıda wamid, hatada Meta error code/mesajı döner (fırlatmaz — yapısal sonuç).
/// </summary>
public sealed class CloudApiWhatsAppSender : IWhatsAppSender
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _opt;
    private readonly ILogger<CloudApiWhatsAppSender> _log;

    public CloudApiWhatsAppSender(
        HttpClient http, IOptions<WhatsAppOptions> opt, ILogger<CloudApiWhatsAppSender> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public Task<WhatsAppSendResult> SendTextAsync(
        WhatsAppSendContext ctx, string toPhoneE164, string text, CancellationToken ct = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = Normalize(toPhoneE164),
            type = "text",
            text = new { preview_url = false, body = text },
        };
        return PostAsync(ctx, payload, ct);
    }

    public Task<WhatsAppSendResult> SendTemplateAsync(
        WhatsAppSendContext ctx, string toPhoneE164, WhatsAppTemplate template, CancellationToken ct = default)
    {
        object templateObj = template.BodyParams.Count == 0
            ? new { name = template.Name, language = new { code = template.LanguageCode } }
            : new
            {
                name = template.Name,
                language = new { code = template.LanguageCode },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        parameters = template.BodyParams
                            .Select(p => new { type = "text", text = p }).ToArray(),
                    },
                },
            };

        var payload = new
        {
            messaging_product = "whatsapp",
            to = Normalize(toPhoneE164),
            type = "template",
            template = templateObj,
        };
        return PostAsync(ctx, payload, ct);
    }

    private async Task<WhatsAppSendResult> PostAsync(
        WhatsAppSendContext ctx, object payload, CancellationToken ct)
    {
        var url = $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}/{ctx.PhoneNumberId}/messages";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WhatsApp send network error → {To}", ctx.PhoneNumberId);
            return WhatsAppSendResult.Failure("network", ex.Message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (resp.IsSuccessStatusCode &&
                root.TryGetProperty("messages", out var msgs) &&
                msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0 &&
                msgs[0].TryGetProperty("id", out var idEl))
            {
                return WhatsAppSendResult.Success(idEl.GetString() ?? "");
            }

            // Hata gövdesi: { "error": { "message", "code", "error_data": { "details" } } }
            if (root.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.ToString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (err.TryGetProperty("error_data", out var ed) &&
                    ed.TryGetProperty("details", out var det))
                {
                    msg = $"{msg} — {det.GetString()}";
                }
                _log.LogWarning("WhatsApp send failed ({Code}): {Msg}", code, msg);
                return WhatsAppSendResult.Failure(code, msg);
            }

            return WhatsAppSendResult.Failure(((int)resp.StatusCode).ToString(), body);
        }
        catch (JsonException)
        {
            return WhatsAppSendResult.Failure(((int)resp.StatusCode).ToString(), body);
        }
    }

    /// <summary>E.164 → Graph "to" (baştaki '+' atılır; rakamlar kalır).</summary>
    private static string Normalize(string phone) => WaPhone.Canonical(phone);
}
