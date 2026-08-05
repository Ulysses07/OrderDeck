using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// <c>GET /{ig-user-id}/live_media?fields=id,comments.limit(50){id,text,timestamp,username}</c>
/// yanıtını ayrıştırır. Saf — ağ yok.
///
/// <para><c>JsonSerializer.Deserialize&lt;T&gt;</c> yerine
/// <see cref="JsonDocument"/> kullanılıyor çünkü "alan yok" ile "alan boş"
/// ayrımını POCO ile temiz yapmak zor: eksik bir <c>comments</c> nesnesi de
/// boş bir <c>data</c> dizisi de aynı null'a düşerdi.</para>
/// </summary>
public static class InstagramLiveMediaParser
{
    /// <summary>
    /// Aktif yayını döner; yayın yoksa veya gövde ayrıştırılamazsa null.
    /// </summary>
    public static InstagramLiveMediaPage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var media in data.EnumerateArray())
            {
                if (!media.TryGetProperty("id", out var idEl)) continue;
                var mediaId = idEl.GetString();
                if (string.IsNullOrEmpty(mediaId)) continue;

                // Meta aynı anda birden fazla canlı yayın döndürmez, ama uç
                // liste döndüğü için ilk geçerli olanı alıyoruz.
                return new InstagramLiveMediaPage(mediaId, ReadComments(media));
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Alan yoksa null (izin/hata sinyali), varsa liste (boş olabilir).</summary>
    private static IReadOnlyList<InstagramComment>? ReadComments(JsonElement media)
    {
        if (!media.TryGetProperty("comments", out var comments) ||
            comments.ValueKind != JsonValueKind.Object ||
            !comments.TryGetProperty("data", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<InstagramComment>();
        foreach (var c in arr.EnumerateArray())
        {
            if (!c.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;

            var text = c.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var rawTs = c.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;
            if (!TryParseMetaTimestamp(rawTs, out var unixSeconds)) continue;

            var username = c.TryGetProperty("username", out var u) ? u.GetString() : null;

            result.Add(new InstagramComment(id, text!, unixSeconds, username));
        }

        return result;
    }

    /// <summary>
    /// Meta <c>2026-08-05T12:34:56+0000</c> gönderiyor — iki nokta içermeyen
    /// offset. <c>System.Text.Json</c>'ın DateTime okuyucusu bunu reddeder,
    /// bu yüzden string olarak alıp <see cref="System.DateTimeOffset"/> ile
    /// esnek ayrıştırıyoruz (Facebook ingestor'ında da aynı tuzak vardı).
    ///
    /// <para>Ayrıştırılamazsa yorumu <b>düşürüyoruz</b>. "Şimdi"yi varsaymak
    /// watermark'ı bozar: yerel saat Meta saatinden ileriyse sonraki gerçek
    /// yorumlar eski sayılıp sessizce kaybolur.</para>
    /// </summary>
    private static bool TryParseMetaTimestamp(string? raw, out long unixSeconds)
    {
        unixSeconds = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!System.DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        unixSeconds = parsed.ToUnixTimeSeconds();
        return true;
    }
}
