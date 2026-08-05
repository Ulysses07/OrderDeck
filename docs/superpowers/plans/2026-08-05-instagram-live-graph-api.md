# Instagram Live yorumları — resmi Graph API uygulama planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Instagram canlı yayın yorumlarını Chrome extension DOM scraper'ı yerine resmi Instagram Graph API'den (read-only, saniyede 1 polling) çekmek; varsayılan `Scraper` kalır, geçiş tek ayarla yapılır.

**Architecture:** Facebook ingestor'ının aynası. Bağlı Facebook Sayfa token'ına biniyoruz — ayrı OAuth yok. `GET /{page-id}?fields=instagram_business_account` ile IG hesabı çözülür, sonra `GET /{ig-user-id}/live_media?fields=id,comments.limit(50){...}` tek çağrıda hem aktif yayını hem yorumlarını verir. Yeni yorum tespiti **timestamp watermark** ile yapılır (sayfalama yok — Meta cursor'ları için "saklamayın" diyor); sayfa taşarsa polling aralığı otomatik sıkılaşır. Saf mantık (watermark, hata sınıflandırma, JSON parse, aralık hesabı) ağ erişimi olmayan sınıflara ayrılır, hepsi xUnit ile test edilir.

**Tech Stack:** .NET 10, `System.Text.Json`, `IHttpClientFactory` named client, xUnit + FluentAssertions, WPF (CommunityToolkit.Mvvm).

**Spec:** `docs/superpowers/specs/2026-08-04-instagram-live-graph-api-design.md`

---

## Dosya yapısı

**Yeni — `OrderDeck.Chat/Ingestors/Instagram/`:**

| Dosya | Sorumluluk |
|---|---|
| `InstagramComment.cs` | Tek yorum DTO'su + `InstagramLiveMediaPage` |
| `InstagramCommentWatermark.cs` | Saf durum makinesi: yeni yorum tespiti, sıralama, taşma tespiti |
| `InstagramGraphError.cs` | Saf hata sınıflandırma + `X-Business-Use-Case-Usage` başlık ayrıştırma |
| `InstagramLiveMediaParser.cs` | Saf JSON → `InstagramLiveMediaPage` |
| `InstagramAccountResolver.cs` | Sayfa → IG business hesabı (HTTP, cache'li) |
| `InstagramPermissionProbe.cs` | `GET /me/permissions` — IG izinleri verilmiş mi (saf ayrıştırıcı + HTTP) |
| `InstagramLiveCommentsPoller.cs` | Polling döngüsü, uyarlanabilir aralık, chat'e basma |
| `InstagramChatHostedService.cs` | Yaşam döngüsü, kapılar (trial/session/mod), backoff |

**Yeni — `OrderDeck.Tests/Chat/Instagram/`:** her saf sınıf için bir test dosyası.

**Değişecek:**
- `OrderDeck.Chat/Bridge/ExtensionBridgeServer.cs` — `OfficialApi` açıkken extension'ın `instagram` mesajlarını düşür
- `OrderDeck.Chat/Facebook/FacebookOAuthService.cs` — kullanıcı token'ını dışa aç (izin sorgusu için)
- `OrderDeck.App/AppHost.cs` — named client + hosted service kaydı + bridge'e bayrak
- `OrderDeck.App/ViewModels/SettingsViewModel.cs` — IG modu toggle + durum satırı + izin uyarısı
- `OrderDeck.App/Views/SettingsDialog.xaml` — Facebook sekmesine Instagram kartı

**Dokunulmayacak:** `Extension/` (susturma tek taraflı, bridge tarafında), moderasyon sınıfları (IG'de moderasyon yok), `InstagramIngestMode.cs` ve `AppSettings.InstagramIngestMode` (zaten var).

---

## Task 1: Yorum DTO'ları

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramComment.cs`

- [ ] **Step 1: Dosyayı oluştur**

```csharp
using System.Collections.Generic;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Graph API'den gelen tek bir canlı yayın yorumu, wire şeklinden arındırılmış
/// hali. <paramref name="TimestampUnix"/> <b>Meta'nın</b> zaman damgasıdır —
/// watermark karşılaştırmaları daima Meta zamanı ile Meta zamanı arasında
/// yapılır, yerel saat hiç karışmaz (kullanıcının sistem saati yanlış olabilir).
/// </summary>
public sealed record InstagramComment(
    string Id,
    string Text,
    long TimestampUnix,
    string? Username);

/// <summary>
/// Bir <c>live_media</c> çekiminin sonucu.
///
/// <para><b>Kritik ayrım:</b> <see cref="Comments"/> <c>null</c> ise alan
/// yanıtta <b>hiç gelmedi</b> — bu bir izin/hata sinyalidir. Boş liste ise
/// yayın var ama henüz yorum yok — bu normaldir. İkisi aynı muamele
/// görmemeli.</para>
/// </summary>
public sealed record InstagramLiveMediaPage(
    string MediaId,
    IReadOnlyList<InstagramComment>? Comments);
```

- [ ] **Step 2: Derle**

Run: `dotnet build OrderDeck.Chat/OrderDeck.Chat.csproj`
Expected: 0 error, 0 warning

- [ ] **Step 3: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramComment.cs
git commit -m "feat(instagram): canlı yorum DTO'ları"
```

---

## Task 2: Watermark (yeni yorum tespiti)

Bu işin kalbi. Comments ucunda timestamp filtresi yok, sayfa ters kronolojik ve en fazla 50 kayıt. "Son görülen id'ye kadar yürü" yaklaşımı kırılgan — yayıncı o yorumu silerse algoritma eşleşme bulamaz ve 50 yorumun hepsini yeni sanar. Bunun yerine timestamp watermark.

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramCommentWatermark.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramCommentWatermarkTests.cs`

- [ ] **Step 1: Testleri yaz**

```csharp
using System;
using System.Collections.Generic;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Watermark saf bir durum makinesi — ağ yok, saat yok. Testler spec'teki
/// "Test stratejisi / Watermark" maddelerinin birebir karşılığı.
/// </summary>
public class InstagramCommentWatermarkTests
{
    private static InstagramComment C(string id, long ts, string text = "x") =>
        new(id, text, ts, "@u");

    /// <summary>Graph ters kronolojik döner — testlerde de öyle veriyoruz.</summary>
    private static List<InstagramComment> Page(params InstagramComment[] newestFirst) =>
        new(newestFirst);

    [Fact]
    public void First_poll_primes_and_publishes_nothing()
    {
        // Yayına ortadan bağlanma: geçmişi chat'e basmıyoruz.
        var w = new InstagramCommentWatermark();

        var r = w.Advance("m1", Page(C("c3", 300), C("c2", 200), C("c1", 100)));

        r.Primed.Should().BeTrue();
        r.NewComments.Should().BeEmpty();
        r.Overflowed.Should().BeFalse();
    }

    [Fact]
    public void Second_poll_returns_only_newer_comments_oldest_first()
    {
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("c1", 100)));

        var r = w.Advance("m1", Page(C("c3", 300), C("c2", 200), C("c1", 100)));

        r.NewComments.Should().HaveCount(2);
        r.NewComments[0].Id.Should().Be("c2"); // kronolojik sıraya çevrildi
        r.NewComments[1].Id.Should().Be("c3");
    }

    [Fact]
    public void Same_second_multiple_comments_all_published_once()
    {
        // Aynı saniyede 3 yorum: ilk çekimde biri görüldü, diğer ikisi
        // sonraki çekimde gelmeli ve tekrar basılmamalı.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("a", 500)));

        var first = w.Advance("m1", Page(C("c", 500), C("b", 500), C("a", 500)));
        first.NewComments.Should().HaveCount(2);
        first.NewComments[0].Id.Should().Be("b"); // id ikincil anahtar → deterministik
        first.NewComments[1].Id.Should().Be("c");

        var second = w.Advance("m1", Page(C("c", 500), C("b", 500), C("a", 500)));
        second.NewComments.Should().BeEmpty();
    }

    [Fact]
    public void Deleted_comment_does_not_cause_replay()
    {
        // Yayıncı "c2"yi IG uygulamasından siliyor. Eski "son görülen id'ye
        // kadar yürü" yaklaşımı burada tüm sayfayı yeni sanardı.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("c2", 200), C("c1", 100)));

        var r = w.Advance("m1", Page(C("c1", 100)));

        r.NewComments.Should().BeEmpty();
    }

    [Fact]
    public void New_media_id_resets_state()
    {
        // Yayıncı yayını kapatıp yenisini açtı → temiz başla, geçmiş basma.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("c9", 900)));

        var r = w.Advance("m2", Page(C("d1", 100)));

        r.Primed.Should().BeTrue();
        r.NewComments.Should().BeEmpty();
    }

    [Fact]
    public void Overflow_detected_when_every_comment_in_a_full_page_is_new()
    {
        // Sayfa doldu ve en eski yorum bile watermark'tan yeni → mesaj kaybı.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("a3", 300), C("a2", 200), C("a1", 100)));

        var r = w.Advance("m1", Page(C("b3", 900), C("b2", 800), C("b1", 700)));

        r.Overflowed.Should().BeTrue();
        r.NewComments.Should().HaveCount(3);
    }

    [Fact]
    public void No_overflow_when_page_still_contains_a_known_comment()
    {
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("a3", 300), C("a2", 200), C("a1", 100)));

        var r = w.Advance("m1", Page(C("b1", 400), C("a3", 300), C("a2", 200)));

        r.Overflowed.Should().BeFalse();
        r.NewComments.Should().ContainSingle().Which.Id.Should().Be("b1");
    }

    [Fact]
    public void Empty_page_is_not_overflow_and_publishes_nothing()
    {
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("a1", 100)));

        var r = w.Advance("m1", new List<InstagramComment>());

        r.NewComments.Should().BeEmpty();
        r.Overflowed.Should().BeFalse();
    }

    [Fact]
    public void Priming_on_empty_page_still_publishes_later_comments()
    {
        // Yayın açıldı, henüz yorum yok. Sonra gelen yorumlar basılmalı.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", new List<InstagramComment>());

        var r = w.Advance("m1", Page(C("c1", 100)));

        r.NewComments.Should().ContainSingle().Which.Id.Should().Be("c1");
    }
}
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramCommentWatermarkTests"`
Expected: derleme hatası — `InstagramCommentWatermark` tipi yok

- [ ] **Step 3: Uygulamayı yaz**

```csharp
using System;
using System.Collections.Generic;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>Bir <see cref="InstagramCommentWatermark.Advance"/> çağrısının sonucu.</summary>
/// <param name="NewComments">Yayınlanacak yorumlar, <b>kronolojik</b> (eski→yeni) sırada.</param>
/// <param name="Overflowed">Sayfa taştı, mesaj kaybedildi — polling aralığı sıkılaştırılmalı.</param>
/// <param name="Primed">Bu çağrı ilk çekimdi; hiçbir şey yayınlanmadı (geçmiş basılmaz).</param>
public readonly record struct WatermarkResult(
    IReadOnlyList<InstagramComment> NewComments,
    bool Overflowed,
    bool Primed);

/// <summary>
/// Yeni yorum tespiti. Comments ucunda timestamp filtresi yok
/// (<i>"Comments cannot be filtered by timestamp"</i>), sayfa başına en fazla
/// 50 kayıt, sıralama ters kronolojik.
///
/// <para><b>Neden id-yürüyüşü değil:</b> "son görülen id'ye kadar yürü"
/// yaklaşımında yayıncı o yorumu silerse id listeden kaybolur, algoritma
/// eşleşme bulamaz ve sayfanın tamamını yeni sanıp tekrar basar.</para>
///
/// <para><b>Kural:</b> bir yorum yeni sayılır eğer
/// <c>timestamp &gt; lastTimestamp</c> <b>veya</b>
/// (<c>timestamp == lastTimestamp</c> <b>ve</b> id daha önce görülmediyse).
/// Karşılaştırma daima Meta zamanı ile Meta zamanı arasında.</para>
///
/// <para><b>Taşma tespiti:</b> sayfadaki <b>en eski</b> yorum bile
/// watermark'tan yeniyse sayfa taşmış, arada mesaj kaybedilmiştir. Yanlış
/// pozitif riski var (nadir: bir yorum silinip sayfa küçüldüğünde) ama zararı
/// yok — sonuç sadece polling aralığının bir tur sıkılaşması.</para>
///
/// <para>Thread-safe <b>değil</b>; tek polling döngüsünden çağrılır.</para>
/// </summary>
public sealed class InstagramCommentWatermark
{
    private string? _mediaId;
    private long _lastTimestamp;
    private readonly HashSet<string> _seenAtLastTimestamp = new(StringComparer.Ordinal);
    private int _maxPageSize;
    private bool _primed;

    /// <summary>Şu an izlenen yayının media id'si; hiç çekim yapılmadıysa null.</summary>
    public string? MediaId => _mediaId;

    /// <summary>İlk çekim yapıldı mı (geçmiş yutuldu mu).</summary>
    public bool IsPrimed => _primed;

    /// <summary>
    /// Bir çekimin sonucunu işler. <paramref name="page"/> Graph'ın döndürdüğü
    /// sırayla (ters kronolojik) verilebilir — sıralama burada yapılır.
    /// </summary>
    public WatermarkResult Advance(string mediaId, IReadOnlyList<InstagramComment> page)
    {
        if (!string.Equals(mediaId, _mediaId, StringComparison.Ordinal))
        {
            // Yeni yayın → temiz sayfa.
            _mediaId = mediaId;
            _primed = false;
            _lastTimestamp = 0;
            _seenAtLastTimestamp.Clear();
            _maxPageSize = 0;
        }

        if (page.Count > _maxPageSize) _maxPageSize = page.Count;

        if (!_primed)
        {
            _primed = true;
            Adopt(page);
            return new WatermarkResult(Array.Empty<InstagramComment>(), false, true);
        }

        var fresh = new List<InstagramComment>();
        foreach (var c in page)
        {
            if (c.TimestampUnix > _lastTimestamp ||
                (c.TimestampUnix == _lastTimestamp && !_seenAtLastTimestamp.Contains(c.Id)))
            {
                fresh.Add(c);
            }
        }

        // Sayfa doluydu ve hiçbir tanıdık yorum kalmamış → arada kayıp var.
        bool overflowed = page.Count > 0
                          && fresh.Count == page.Count
                          && page.Count >= _maxPageSize;

        fresh.Sort(CompareChronological);

        // Watermark'ı sayfanın TAMAMINA göre ilerlet, sadece yenilere göre
        // değil: aynı saniyedeki "zaten görülmüş" id'ler de sette kalmalı,
        // yoksa bir sonraki çekimde tekrar yeni sayılırlar.
        Adopt(page);

        return new WatermarkResult(fresh, overflowed, false);
    }

    private void Adopt(IReadOnlyList<InstagramComment> page)
    {
        if (page.Count == 0) return;

        long max = long.MinValue;
        foreach (var c in page)
            if (c.TimestampUnix > max) max = c.TimestampUnix;

        if (max < _lastTimestamp) return;

        if (max > _lastTimestamp)
        {
            _lastTimestamp = max;
            _seenAtLastTimestamp.Clear();
        }

        foreach (var c in page)
            if (c.TimestampUnix == max) _seenAtLastTimestamp.Add(c.Id);
    }

    /// <summary>Birincil anahtar timestamp, ikincil id. Aynı saniyedeki gerçek
    /// sıra bilinemez ama sıralamanın <b>deterministik</b> olması şart —
    /// aksi hâlde iki çekim aynı yorumları farklı sırada basardı.</summary>
    private static int CompareChronological(InstagramComment a, InstagramComment b)
    {
        int t = a.TimestampUnix.CompareTo(b.TimestampUnix);
        return t != 0 ? t : string.CompareOrdinal(a.Id, b.Id);
    }
}
```

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramCommentWatermarkTests"`
Expected: 9 passed

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramCommentWatermark.cs OrderDeck.Tests/Chat/Instagram/InstagramCommentWatermarkTests.cs
git commit -m "feat(instagram): timestamp watermark ile yeni yorum tespiti"
```

---

## Task 3: Hata sınıflandırma + kota başlığı

Spec'teki hata tablosunun kod karşılığı. Saf, ağsız.

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramGraphError.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramGraphErrorTests.cs`

- [ ] **Step 1: Testleri yaz**

```csharp
using System;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

public class InstagramGraphErrorTests
{
    private static string Body(int code, int? subcode = null) =>
        subcode is null
            ? $"{{\"error\":{{\"message\":\"x\",\"type\":\"OAuthException\",\"code\":{code}}}}}"
            : $"{{\"error\":{{\"message\":\"x\",\"type\":\"OAuthException\",\"code\":{code},\"error_subcode\":{subcode}}}}}";

    [Theory]
    [InlineData(190)]
    [InlineData(463)]
    public void Token_errors_are_fatal_token_expired(int code)
    {
        InstagramGraphError.Classify(400, Body(code))
            .Should().Be(InstagramErrorKind.TokenExpired);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(10)]
    public void Permission_errors_are_fatal_permission_denied(int code)
    {
        InstagramGraphError.Classify(403, Body(code))
            .Should().Be(InstagramErrorKind.PermissionDenied);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(613)]
    [InlineData(80002)]
    public void Throttling_codes_are_rate_limited(int code)
    {
        InstagramGraphError.Classify(400, Body(code))
            .Should().Be(InstagramErrorKind.RateLimited);
    }

    [Fact]
    public void Subcode_2446079_is_rate_limited()
    {
        InstagramGraphError.Classify(400, Body(1, 2446079))
            .Should().Be(InstagramErrorKind.RateLimited);
    }

    [Fact]
    public void Code_100_means_broadcast_ended()
    {
        InstagramGraphError.Classify(400, Body(100))
            .Should().Be(InstagramErrorKind.BroadcastEnded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Generic_api_errors_are_transient(int code)
    {
        InstagramGraphError.Classify(500, Body(code))
            .Should().Be(InstagramErrorKind.Transient);
    }

    [Fact]
    public void Server_error_without_parsable_body_is_transient()
    {
        InstagramGraphError.Classify(502, "<html>bad gateway</html>")
            .Should().Be(InstagramErrorKind.Transient);
    }

    [Fact]
    public void Unknown_code_is_transient()
    {
        // Bilinmeyen kodda oturumu öldürmüyoruz — geri çekilip tekrar deniyoruz.
        InstagramGraphError.Classify(400, Body(999999))
            .Should().Be(InstagramErrorKind.Transient);
    }

    // ── Kota başlığı ─────────────────────────────────────────────────────────

    [Fact]
    public void Parses_estimated_time_to_regain_access_in_minutes()
    {
        const string header =
            "{\"3939617702835404\":[{\"type\":\"instagram\",\"call_count\":100," +
            "\"total_cputime\":25,\"total_time\":25," +
            "\"estimated_time_to_regain_access\":12}]}";

        InstagramGraphError.TryGetRetryAfter(header, out var wait).Should().BeTrue();
        wait.Should().Be(TimeSpan.FromMinutes(12));
    }

    [Fact]
    public void Zero_estimated_time_means_no_wait_required()
    {
        const string header =
            "{\"app\":[{\"type\":\"instagram\",\"estimated_time_to_regain_access\":0}]}";

        InstagramGraphError.TryGetRetryAfter(header, out _).Should().BeFalse();
    }

    [Fact]
    public void Missing_or_garbage_header_returns_false()
    {
        InstagramGraphError.TryGetRetryAfter(null, out _).Should().BeFalse();
        InstagramGraphError.TryGetRetryAfter("", out _).Should().BeFalse();
        InstagramGraphError.TryGetRetryAfter("not json", out _).Should().BeFalse();
    }

    [Fact]
    public void Picks_the_largest_wait_across_buckets()
    {
        // Yanıtta birden çok kova olabilir; en uzun beklemeye uyarız.
        const string header =
            "{\"app\":[{\"type\":\"pages\",\"estimated_time_to_regain_access\":3}," +
            "{\"type\":\"instagram\",\"estimated_time_to_regain_access\":9}]}";

        InstagramGraphError.TryGetRetryAfter(header, out var wait).Should().BeTrue();
        wait.Should().Be(TimeSpan.FromMinutes(9));
    }
}
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramGraphErrorTests"`
Expected: derleme hatası — `InstagramGraphError` tipi yok

- [ ] **Step 3: Uygulamayı yaz**

```csharp
using System;
using System.Text.Json;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>Bir Graph hatasına nasıl tepki verileceği.</summary>
public enum InstagramErrorKind
{
    /// <summary>Geri çekilip tekrar dene; oturumu öldürme.</summary>
    Transient,

    /// <summary>Token süresi doldu/iptal edildi. Döngüyü durdur, kullanıcıdan
    /// yeniden bağlanmasını iste. Sonsuz retry yok.</summary>
    TokenExpired,

    /// <summary>İzin verilmemiş. Döngüyü durdur, farklı mesaj göster.</summary>
    PermissionDenied,

    /// <summary>Kota aşıldı. <see cref="InstagramGraphError.TryGetRetryAfter"/>
    /// ile başlıktan gelen süre kadar bekle; sabit backoff uydurma.</summary>
    RateLimited,

    /// <summary>Yayın bitti — media artık okunamıyor. Bu bir hata değil,
    /// normal yaşam döngüsü sonu.</summary>
    BroadcastEnded,
}

/// <summary>
/// Graph hata gövdesini ve kota başlığını yorumlayan saf yardımcılar.
/// Ağ erişimi yok, tamamen test edilebilir.
/// </summary>
public static class InstagramGraphError
{
    /// <summary>
    /// Hata gövdesini sınıflandırır. Gövde ayrıştırılamazsa
    /// <see cref="InstagramErrorKind.Transient"/> döner — bilinmeyende
    /// oturumu öldürmüyoruz.
    /// </summary>
    public static InstagramErrorKind Classify(int httpStatus, string? body)
    {
        int? code = null, subcode = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    if (err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci))
                        code = ci;
                    if (err.TryGetProperty("error_subcode", out var s) && s.TryGetInt32(out var si))
                        subcode = si;
                }
            }
            catch (JsonException) { /* HTML hata sayfası vb. → Transient */ }
        }

        if (subcode == 2446079) return InstagramErrorKind.RateLimited;

        return code switch
        {
            190 or 463 => InstagramErrorKind.TokenExpired,
            200 or 10 => InstagramErrorKind.PermissionDenied,
            4 or 17 or 32 or 613 or 80002 => InstagramErrorKind.RateLimited,
            100 => InstagramErrorKind.BroadcastEnded,
            _ => InstagramErrorKind.Transient,
        };
    }

    /// <summary>
    /// <c>X-Business-Use-Case-Usage</c> başlığından beklenmesi gereken süreyi
    /// çıkarır. Meta <c>estimated_time_to_regain_access</c> değerini
    /// <b>dakika</b> cinsinden verir. Birden çok kova varsa en uzunu alınır.
    /// </summary>
    public static bool TryGetRetryAfter(string? headerValue, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;

        int maxMinutes = 0;
        try
        {
            using var doc = JsonDocument.Parse(headerValue);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var appEntry in doc.RootElement.EnumerateObject())
            {
                if (appEntry.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var bucket in appEntry.Value.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object) continue;
                    if (bucket.TryGetProperty("estimated_time_to_regain_access", out var e) &&
                        e.TryGetInt32(out var minutes) &&
                        minutes > maxMinutes)
                    {
                        maxMinutes = minutes;
                    }
                }
            }
        }
        catch (JsonException) { return false; }

        if (maxMinutes <= 0) return false;
        wait = TimeSpan.FromMinutes(maxMinutes);
        return true;
    }
}
```

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramGraphErrorTests"`
Expected: 20 passed (Theory satırları dahil)

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramGraphError.cs OrderDeck.Tests/Chat/Instagram/InstagramGraphErrorTests.cs
git commit -m "feat(instagram): Graph hata sınıflandırma ve kota başlığı ayrıştırma"
```

---

## Task 4: `live_media` yanıt ayrıştırıcı

Tek çağrıda hem aktif yayını hem yorumlarını alıyoruz. Kritik ayrım: `comments` alanının **hiç gelmemesi** (izin/hata sinyali) ile **boş dizi** (yorum yok, normal) aynı şey değil.

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramLiveMediaParser.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramLiveMediaParserTests.cs`

- [ ] **Step 1: Testleri yaz**

```csharp
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

public class InstagramLiveMediaParserTests
{
    [Fact]
    public void Empty_data_means_no_active_broadcast()
    {
        InstagramLiveMediaParser.Parse("{\"data\":[]}").Should().BeNull();
    }

    [Fact]
    public void Missing_data_property_returns_null()
    {
        InstagramLiveMediaParser.Parse("{}").Should().BeNull();
    }

    [Fact]
    public void Garbage_returns_null()
    {
        InstagramLiveMediaParser.Parse("<html>oops</html>").Should().BeNull();
        InstagramLiveMediaParser.Parse("").Should().BeNull();
    }

    [Fact]
    public void Parses_media_id_and_comments()
    {
        const string json = """
        {"data":[{"id":"17895695668004550","comments":{"data":[
          {"id":"17870913088019932","text":"MAVI XL","timestamp":"2026-08-05T12:34:56+0000","username":"ayse_y"},
          {"id":"17870913088019931","text":"kac lira","timestamp":"2026-08-05T12:34:55+0000","username":"veli"}
        ]}}]}
        """;

        var page = InstagramLiveMediaParser.Parse(json);

        page.Should().NotBeNull();
        page!.MediaId.Should().Be("17895695668004550");
        page.Comments.Should().NotBeNull().And.HaveCount(2);
        page.Comments![0].Id.Should().Be("17870913088019932");
        page.Comments[0].Text.Should().Be("MAVI XL");
        page.Comments[0].Username.Should().Be("ayse_y");
        // 2026-08-05T12:34:56+0000 → unix saniye
        page.Comments[0].TimestampUnix.Should().Be(1785933296);
    }

    [Fact]
    public void Absent_comments_field_is_null_not_empty()
    {
        // İzin eksikse Meta comments alanını hiç göndermez. Bunu "yorum yok"
        // sanıp sessizce çalışmaya devam edersek arıza görünmez olur.
        var page = InstagramLiveMediaParser.Parse("{\"data\":[{\"id\":\"m1\"}]}");

        page.Should().NotBeNull();
        page!.Comments.Should().BeNull();
    }

    [Fact]
    public void Present_but_empty_comments_is_empty_list()
    {
        var page = InstagramLiveMediaParser.Parse(
            "{\"data\":[{\"id\":\"m1\",\"comments\":{\"data\":[]}}]}");

        page!.Comments.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Comment_without_text_is_skipped()
    {
        // Metinsiz yorum (nadir metadata çerçevesi) chat'e basılamaz.
        var page = InstagramLiveMediaParser.Parse("""
        {"data":[{"id":"m1","comments":{"data":[
          {"id":"c1","timestamp":"2026-08-05T12:34:56+0000","username":"a"},
          {"id":"c2","text":"  ","timestamp":"2026-08-05T12:34:56+0000","username":"a"},
          {"id":"c3","text":"ok","timestamp":"2026-08-05T12:34:56+0000","username":"a"}
        ]}}]}
        """);

        page!.Comments.Should().ContainSingle().Which.Id.Should().Be("c3");
    }

    [Fact]
    public void Comment_without_username_still_parses()
    {
        // username eksikse (izin sorunu) yorumu atmıyoruz — operatör metni
        // görsün, kim yazdığı "bilinmiyor" kalsın.
        var page = InstagramLiveMediaParser.Parse("""
        {"data":[{"id":"m1","comments":{"data":[
          {"id":"c1","text":"selam","timestamp":"2026-08-05T12:34:56+0000"}
        ]}}]}
        """);

        page!.Comments.Should().ContainSingle();
        page.Comments![0].Username.Should().BeNull();
    }

    [Fact]
    public void Unparsable_timestamp_drops_the_comment()
    {
        // Watermark'ın tek dayanağı timestamp. Ayrıştırılamayan bir damgaya
        // "şimdi" atamak watermark'ı bozar ve mesaj kaybına yol açar.
        var page = InstagramLiveMediaParser.Parse("""
        {"data":[{"id":"m1","comments":{"data":[
          {"id":"c1","text":"selam","timestamp":"dun aksam"}
        ]}}]}
        """);

        page!.Comments.Should().BeEmpty();
    }

    [Fact]
    public void First_live_media_wins_when_multiple_returned()
    {
        var page = InstagramLiveMediaParser.Parse(
            "{\"data\":[{\"id\":\"m1\"},{\"id\":\"m2\"}]}");

        page!.MediaId.Should().Be("m1");
    }
}
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramLiveMediaParserTests"`
Expected: derleme hatası — `InstagramLiveMediaParser` tipi yok

- [ ] **Step 3: Uygulamayı yaz**

```csharp
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
```

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramLiveMediaParserTests"`
Expected: 10 passed

Beklenen unix değeri sapıyorsa testteki `1785933296` sabitini
`DateTimeOffset.Parse("2026-08-05T12:34:56+0000").ToUnixTimeSeconds()` ile
doğrula ve **testi** düzelt — üretim kodunu değil.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramLiveMediaParser.cs OrderDeck.Tests/Chat/Instagram/InstagramLiveMediaParserTests.cs
git commit -m "feat(instagram): live_media yanıt ayrıştırıcı"
```

---

## Task 5: IG hesabı çözümleyici

Bağlı Facebook Sayfa'sından IG business hesabını çözer. `FacebookLiveVideoResolver`'ın aynısı şekilde: hata → null, çağıran "bağlı hesap yok" muamelesi yapar.

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramAccountResolver.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramAccountResolverTests.cs`

- [ ] **Step 1: Testleri yaz**

`HttpClient`'ı sahte bir handler ile besliyoruz — ağ yok.

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

public class InstagramAccountResolverTests
{
    /// <summary>Tek bir sabit yanıt döndüren sahte handler; kaç kez çağrıldığını sayar.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int Calls { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }

    private static InstagramAccountResolver Make(StubHandler handler) =>
        new(new HttpClient(handler), NullLogger<InstagramAccountResolver>.Instance);

    [Fact]
    public async Task Resolves_linked_business_account()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"instagram_business_account\":{\"id\":\"17841400000000000\",\"username\":\"mezatdunyasi\"}," +
            "\"id\":\"811177875420245\"}");

        var account = await Make(handler).ResolveAsync("811177875420245", "tok", CancellationToken.None);

        account.Should().NotBeNull();
        account!.Value.IgUserId.Should().Be("17841400000000000");
        account.Value.Username.Should().Be("mezatdunyasi");
    }

    [Fact]
    public async Task Returns_null_when_no_instagram_account_linked()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"id\":\"811177875420245\"}");

        var account = await Make(handler).ResolveAsync("811177875420245", "tok", CancellationToken.None);

        account.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_on_error_response()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"nope\",\"code\":100}}");

        var account = await Make(handler).ResolveAsync("p", "tok", CancellationToken.None);

        account.Should().BeNull();
    }

    [Fact]
    public async Task Successful_result_is_cached_per_page()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"instagram_business_account\":{\"id\":\"ig1\",\"username\":\"u\"}}");
        var resolver = Make(handler);

        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);
        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Failure_is_not_cached()
    {
        // Geçici hata kalıcı "bağlı hesap yok"a dönüşmemeli.
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");
        var resolver = Make(handler);

        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);
        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Different_page_bypasses_cache()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"instagram_business_account\":{\"id\":\"ig1\",\"username\":\"u\"}}");
        var resolver = Make(handler);

        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);
        await resolver.ResolveAsync("page2", "tok", CancellationToken.None);

        handler.Calls.Should().Be(2);
    }
}
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramAccountResolverTests"`
Expected: derleme hatası — `InstagramAccountResolver` tipi yok

- [ ] **Step 3: Uygulamayı yaz**

```csharp
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>Bir Sayfa'ya bağlı Instagram professional hesabı.</summary>
public readonly record struct InstagramAccount(string IgUserId, string? Username);

/// <summary>
/// <c>GET /{page-id}?fields=instagram_business_account{id,username}</c> ile
/// bağlı IG hesabını çözer.
///
/// <para>Bu çağrı <c>ads_read</c> koşullu maddesinden <b>etkilenmiyor</b>
/// (yalnızca <c>live_media</c> ve <c>comments</c> uçları etkileniyor), yani
/// hesap çözümlemesi çalışıp yorum okuma patlıyorsa sorun izinlerdedir.</para>
///
/// <para>Başarılı sonuç Sayfa başına cache'lenir — yayın boyunca saniyede bir
/// aynı çağrıyı yapmanın anlamı yok. Başarısızlık <b>cache'lenmez</b>: geçici
/// bir 5xx kalıcı "bağlı hesap yok" hâline dönüşmemeli.</para>
/// </summary>
public sealed class InstagramAccountResolver
{
    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";

    private readonly HttpClient _http;
    private readonly ILogger<InstagramAccountResolver> _log;

    private string? _cachedPageId;
    private InstagramAccount? _cached;

    public InstagramAccountResolver(HttpClient http, ILogger<InstagramAccountResolver> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Bağlı IG hesabını döner; yoksa veya çağrı başarısızsa null.</summary>
    public async Task<InstagramAccount?> ResolveAsync(
        string pageId, string pageAccessToken, CancellationToken ct)
    {
        if (_cached is not null && string.Equals(_cachedPageId, pageId, StringComparison.Ordinal))
            return _cached;

        var url = $"{GraphBase}/{Uri.EscapeDataString(pageId)}" +
                  $"?fields={Uri.EscapeDataString("instagram_business_account{id,username}")}" +
                  $"&access_token={Uri.EscapeDataString(pageAccessToken)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug(
                    "[InstagramAccountResolver] {Status} for page {PageId}: {Body}",
                    (int)resp.StatusCode, pageId, Truncate(body));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("instagram_business_account", out var acc) ||
                acc.ValueKind != JsonValueKind.Object ||
                !acc.TryGetProperty("id", out var idEl))
            {
                _log.LogInformation(
                    "[InstagramAccountResolver] page {PageId} has no linked Instagram professional account",
                    pageId);
                return null;
            }

            var igUserId = idEl.GetString();
            if (string.IsNullOrEmpty(igUserId)) return null;

            var username = acc.TryGetProperty("username", out var u) ? u.GetString() : null;
            var account = new InstagramAccount(igUserId, username);

            _cachedPageId = pageId;
            _cached = account;

            _log.LogInformation(
                "[InstagramAccountResolver] page {PageId} → IG @{Username} ({IgUserId})",
                pageId, username ?? "?", igUserId);
            return account;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[InstagramAccountResolver] resolve failed for page {PageId}", pageId);
            return null;
        }
    }

    /// <summary>Facebook bağlantısı değişince (disconnect/yeniden bağlan) çağrılır.</summary>
    public void Invalidate()
    {
        _cachedPageId = null;
        _cached = null;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s.Substring(0, 200);
}
```

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramAccountResolverTests"`
Expected: 6 passed

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramAccountResolver.cs OrderDeck.Tests/Chat/Instagram/InstagramAccountResolverTests.cs
git commit -m "feat(instagram): Sayfa → IG business hesabı çözümleyici"
```

---

## Task 6: Polling döngüsü

Önceki dört parçayı birleştirir. `FacebookLiveCommentsStream`'in aynı şekli: `IChatIngestor` + `Completion`.

**Kritik ayrıntı — kullanıcı adı biçimi.** Extension chat'e `@ayse_y` biçiminde gönderiyor ve müşteri eşleştirmesi bu anahtara dayanıyor. Graph `username` alanını **@'sız** veriyor. Başına `@` koymazsak aynı müşteri iki ayrı kayıt olur.

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramLiveCommentsPoller.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramLiveCommentsPollerTests.cs`

- [ ] **Step 1: Testleri yaz**

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

public class InstagramLiveCommentsPollerTests
{
    /// <summary>Sıradaki yanıtları tek tek döndürür; bitince sonuncuyu tekrarlar.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _queue;
        private (HttpStatusCode Status, string Body) _last;

        public ScriptedHandler(params (HttpStatusCode, string)[] responses)
        {
            _queue = new Queue<(HttpStatusCode, string)>(responses);
            _last = responses[^1];
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var next = _queue.Count > 0 ? _queue.Dequeue() : _last;
            return Task.FromResult(new HttpResponseMessage(next.Status)
            {
                Content = new StringContent(next.Body),
            });
        }
    }

    private static InstagramLiveCommentsPoller Make(ScriptedHandler handler, IChatBus bus) =>
        new("ig1", "tok", bus, new HttpClient(handler),
            NullLogger<InstagramLiveCommentsPoller>.Instance);

    private static string MediaJson(params string[] comments) =>
        "{\"data\":[{\"id\":\"m1\",\"comments\":{\"data\":[" + string.Join(",", comments) + "]}}]}";

    private static string Comment(string id, string text, string ts, string user) =>
        $"{{\"id\":\"{id}\",\"text\":\"{text}\",\"timestamp\":\"{ts}\",\"username\":\"{user}\"}}";

    // ── Uyarlanabilir aralık (saf) ───────────────────────────────────────────

    [Fact]
    public void NextInterval_tightens_on_overflow()
    {
        var i1 = InstagramLiveCommentsPoller.NextInterval(TimeSpan.FromSeconds(1), overflowed: true);
        i1.Should().Be(TimeSpan.FromMilliseconds(500));

        var i2 = InstagramLiveCommentsPoller.NextInterval(i1, overflowed: true);
        i2.Should().Be(TimeSpan.FromMilliseconds(300)); // taban

        var i3 = InstagramLiveCommentsPoller.NextInterval(i2, overflowed: true);
        i3.Should().Be(TimeSpan.FromMilliseconds(300)); // tabandan aşağı inmez
    }

    [Fact]
    public void NextInterval_relaxes_gradually_back_to_one_second()
    {
        var i = TimeSpan.FromMilliseconds(300);
        for (int n = 0; n < 20; n++)
            i = InstagramLiveCommentsPoller.NextInterval(i, overflowed: false);

        i.Should().Be(TimeSpan.FromSeconds(1)); // tavanı aşmaz
    }

    [Fact]
    public void NextInterval_relaxation_is_one_step_at_a_time()
    {
        InstagramLiveCommentsPoller
            .NextInterval(TimeSpan.FromMilliseconds(300), overflowed: false)
            .Should().Be(TimeSpan.FromMilliseconds(400));
    }

    // ── Döngü davranışı ──────────────────────────────────────────────────────

    [Fact]
    public async Task Publishes_only_comments_that_arrive_after_priming()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        var received = new List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        var handler = new ScriptedHandler(
            // 1. çekim: geçmiş → yutulur
            (HttpStatusCode.OK, MediaJson(Comment("c1", "eski", "2026-08-05T12:00:00+0000", "ayse_y"))),
            // 2. çekim: yeni yorum → basılır
            (HttpStatusCode.OK, MediaJson(
                Comment("c2", "yeni", "2026-08-05T12:00:05+0000", "veli"),
                Comment("c1", "eski", "2026-08-05T12:00:00+0000", "ayse_y"))));

        using var poller = Make(handler, bus);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await poller.StartAsync(cts.Token);

        await WaitUntil(() => { lock (received) return received.Count >= 1; }, TimeSpan.FromSeconds(5));
        await poller.StopAsync(CancellationToken.None);

        lock (received)
        {
            received.Should().ContainSingle();
            received[0].Text.Should().Be("yeni");
            received[0].ExternalId.Should().Be("c2");
            received[0].Platform.Should().Be("instagram");
            // Extension ile aynı anahtar biçimi — müşteri eşleştirmesi buna bağlı.
            received[0].Username.Should().Be("@veli");
            received[0].DisplayName.Should().Be("veli");
        }
    }

    [Fact]
    public async Task Token_error_stops_the_loop_with_a_fatal_reason()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        var handler = new ScriptedHandler(
            (HttpStatusCode.BadRequest,
             "{\"error\":{\"message\":\"expired\",\"type\":\"OAuthException\",\"code\":190}}"));

        using var poller = Make(handler, bus);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await poller.StartAsync(cts.Token);

        await poller.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        poller.FatalReason.Should().NotBeNull();
        poller.FatalReason.Should().Contain("bağlantı");
    }

    [Fact]
    public async Task Permission_error_stops_the_loop_with_a_different_message()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        var handler = new ScriptedHandler(
            (HttpStatusCode.Forbidden,
             "{\"error\":{\"message\":\"no perm\",\"type\":\"OAuthException\",\"code\":200}}"));

        using var poller = Make(handler, bus);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await poller.StartAsync(cts.Token);

        await poller.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        poller.FatalReason.Should().Contain("izin");
    }

    [Fact]
    public async Task Missing_comments_field_stops_the_loop_as_permission_problem()
    {
        // Alan hiç gelmiyorsa sessizce "yorum yok" sanıp saatlerce boş
        // dönmemeliyiz — bu bir izin arızasıdır, operatöre söylenmeli.
        var bus = new ChatBus(ringBufferSize: 10);
        var handler = new ScriptedHandler((HttpStatusCode.OK, "{\"data\":[{\"id\":\"m1\"}]}"));

        using var poller = Make(handler, bus);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await poller.StartAsync(cts.Token);

        await poller.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        poller.FatalReason.Should().Contain("izin");
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }
}
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramLiveCommentsPollerTests"`
Expected: derleme hatası — `InstagramLiveCommentsPoller` tipi yok

- [ ] **Step 3: Uygulamayı yaz**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Chat;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Bir IG business hesabının aktif canlı yayınındaki yorumları polling ile
/// çekip <see cref="IChatBus"/>'a basar. Read-only — Meta canlı yayın
/// yorumlarında hide/delete desteklemiyor.
///
/// <para><b>Tek çağrı:</b> <c>live_media</c> alan genişletmesiyle hem aktif
/// yayını hem yorumlarını getirir. Ayrı bir "yayın var mı" sorgusu yok.</para>
///
/// <para><b>Sayfalama yok.</b> Comments ucunun sayfalama şeması dokümante
/// edilmemiş ve Meta cursor'lar için <i>"Don't store cursors"</i> diyor.
/// Yerine uyarlanabilir aralık: sayfa taşarsa polling sıklaşır
/// (1s → 0.5s → 0.3s), akış sakinleşince gevşer. Kota darboğaz olmadığı için
/// bu bedava bir emniyet supabı.</para>
///
/// <para><b>Yayın sonu:</b> Meta canlı yorumları yayın bittikten sonra
/// okutmuyor (<i>"can only be read while ... being broadcast"</i>), yani son
/// polling aralığındaki yorumlar kaybolur. Bilinçli ödün.</para>
/// </summary>
public sealed class InstagramLiveCommentsPoller : IChatIngestor, IDisposable
{
    /// <summary>Normal polling aralığı.</summary>
    internal static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(1);

    /// <summary>Taşma hâlinde inilebilecek en kısa aralık.</summary>
    internal static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>Sakinleşince her turda bu kadar gevşer.</summary>
    internal static readonly TimeSpan RelaxStep = TimeSpan.FromMilliseconds(100);

    /// <summary>Aktif yayın yokken bekleme. Yayın açılınca <see cref="BaseInterval"/>'e döner.</summary>
    private static readonly TimeSpan NoBroadcastIdle = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Bu kadar art arda geçici hatadan sonra döngüyü bırak; hosted
    /// service yeniden kurar (token yenilenmiş olabilir).</summary>
    private const int MaxConsecutiveErrors = 5;

    /// <summary>
    /// Yalnızca kullandığımız alanlar isteniyor. Kullanılmayan alan istemek
    /// App Review'da <i>"selecting unneeded permissions"</i> ile aynı kategoriye
    /// düşer. <c>limit(50)</c> <b>açıkça</b> yazılıyor: 50 sınırı sadece
    /// doğrudan uçta belgeli, iç içe genişletmede Graph'ın genel varsayılanı
    /// 25 gelebilir. Gerçekte kaç döndüğü pilot yayında ölçülecek.
    /// </summary>
    private const string Fields = "id,comments.limit(50){id,text,timestamp,username}";

    private readonly string _igUserId;
    private readonly string _pageAccessToken;
    private readonly IChatBus _bus;
    private readonly HttpClient _http;
    private readonly ILogger<InstagramLiveCommentsPoller> _log;
    private readonly SpamFilter? _spamFilter;
    private readonly InstagramCommentWatermark _watermark = new();

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private readonly TaskCompletionSource _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Platform => "instagram";

    /// <summary>Döngü bittiğinde tamamlanır.</summary>
    public Task Completion => _completionTcs.Task;

    /// <summary>
    /// Döngü kalıcı bir sebeple durduysa operatöre gösterilecek Türkçe mesaj;
    /// aksi hâlde null. Hosted service bunu görünce yeniden denemez.
    /// </summary>
    public string? FatalReason { get; private set; }

    public InstagramLiveCommentsPoller(
        string igUserId,
        string pageAccessToken,
        IChatBus bus,
        HttpClient http,
        ILogger<InstagramLiveCommentsPoller> log,
        SpamFilter? spamFilter = null)
    {
        _igUserId = igUserId;
        _pageAccessToken = pageAccessToken;
        _bus = bus;
        _http = http;
        _log = log;
        _spamFilter = spamFilter;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runner = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[InstagramLiveCommentsPoller] stop wait swallowed");
            }
        }
    }

    /// <summary>
    /// Bir sonraki polling aralığı. Taşmada yarıya iner (tabana kadar),
    /// aksi hâlde kademeli olarak tavana gevşer. Saf — test edilebilir.
    /// </summary>
    internal static TimeSpan NextInterval(TimeSpan current, bool overflowed)
    {
        if (overflowed)
        {
            var halved = TimeSpan.FromMilliseconds(current.TotalMilliseconds / 2);
            return halved < MinInterval ? MinInterval : halved;
        }

        var relaxed = current + RelaxStep;
        return relaxed > BaseInterval ? BaseInterval : relaxed;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}" +
                  $"/{Uri.EscapeDataString(_igUserId)}/live_media" +
                  $"?fields={Uri.EscapeDataString(Fields)}" +
                  $"&access_token={Uri.EscapeDataString(_pageAccessToken)}";

        _log.LogInformation(
            "[InstagramLiveCommentsPoller] polling live_media for IG user {IgUserId}", _igUserId);

        var interval = BaseInterval;
        int consecutiveErrors = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var outcome = await PollOnceAsync(url, ct).ConfigureAwait(false);

                switch (outcome.Kind)
                {
                    case PollKind.Fatal:
                        FatalReason = outcome.Message;
                        _log.LogWarning(
                            "[InstagramLiveCommentsPoller] stopping: {Reason}", outcome.Message);
                        return;

                    case PollKind.RateLimited:
                        _log.LogWarning(
                            "[InstagramLiveCommentsPoller] rate limited; waiting {Wait}",
                            outcome.RetryAfter);
                        await Task.Delay(outcome.RetryAfter, ct).ConfigureAwait(false);
                        continue;

                    case PollKind.Transient:
                        if (++consecutiveErrors >= MaxConsecutiveErrors)
                        {
                            _log.LogWarning(
                                "[InstagramLiveCommentsPoller] giving up after {Count} errors",
                                consecutiveErrors);
                            return;
                        }
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                        continue;

                    case PollKind.NoBroadcast:
                        consecutiveErrors = 0;
                        interval = BaseInterval; // yayın açılınca hemen 1sn'den başla
                        await Task.Delay(NoBroadcastIdle, ct).ConfigureAwait(false);
                        continue;

                    case PollKind.Ok:
                        consecutiveErrors = 0;
                        var result = _watermark.Advance(outcome.MediaId!, outcome.Comments!);

                        if (result.Overflowed)
                        {
                            _log.LogWarning(
                                "[InstagramLiveCommentsPoller] comment page overflowed for media " +
                                "{MediaId} — tightening poll interval (messages may have been lost)",
                                outcome.MediaId);
                        }

                        foreach (var c in result.NewComments)
                            Publish(c);

                        interval = NextInterval(interval, result.Overflowed);
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                        continue;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on stop / shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[InstagramLiveCommentsPoller] poll loop failed for IG user {IgUserId}", _igUserId);
        }
        finally
        {
            _completionTcs.TrySetResult();
        }
    }

    private enum PollKind { Ok, NoBroadcast, Transient, RateLimited, Fatal }

    private readonly record struct PollOutcome(
        PollKind Kind,
        string? MediaId = null,
        System.Collections.Generic.IReadOnlyList<InstagramComment>? Comments = null,
        string? Message = null,
        TimeSpan RetryAfter = default);

    private async Task<PollOutcome> PollOnceAsync(string url, CancellationToken ct)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(RequestTimeout);

        try
        {
            using var resp = await _http.GetAsync(url, reqCts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var kind = InstagramGraphError.Classify((int)resp.StatusCode, body);
                switch (kind)
                {
                    case InstagramErrorKind.TokenExpired:
                        return new PollOutcome(PollKind.Fatal,
                            Message: "Instagram bağlantını yenilemen gerekiyor.");
                    case InstagramErrorKind.PermissionDenied:
                        return new PollOutcome(PollKind.Fatal,
                            Message: "Instagram yorum izni verilmemiş. Facebook bağlantısını yenile.");
                    case InstagramErrorKind.RateLimited:
                        var wait = TimeSpan.FromMinutes(1);
                        if (resp.Headers.TryGetValues("X-Business-Use-Case-Usage", out var vals))
                        {
                            foreach (var v in vals)
                                if (InstagramGraphError.TryGetRetryAfter(v, out var parsed))
                                {
                                    wait = parsed;
                                    break;
                                }
                        }
                        return new PollOutcome(PollKind.RateLimited, RetryAfter: wait);
                    case InstagramErrorKind.BroadcastEnded:
                        return new PollOutcome(PollKind.NoBroadcast);
                    default:
                        _log.LogDebug(
                            "[InstagramLiveCommentsPoller] transient {Status}: {Body}",
                            (int)resp.StatusCode, Truncate(body));
                        return new PollOutcome(PollKind.Transient);
                }
            }

            var page = InstagramLiveMediaParser.Parse(body);
            if (page is null) return new PollOutcome(PollKind.NoBroadcast);

            if (page.Comments is null)
            {
                // Yayın var ama comments alanı hiç gelmedi → izin arızası.
                // "Yorum yok" sanıp sessizce dönmek arızayı görünmez yapardı.
                return new PollOutcome(PollKind.Fatal,
                    Message: "Instagram yorumları okunamıyor — yorum izni eksik görünüyor.");
            }

            return new PollOutcome(PollKind.Ok, page.MediaId, page.Comments);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[InstagramLiveCommentsPoller] request failed");
            return new PollOutcome(PollKind.Transient);
        }
    }

    private void Publish(InstagramComment c)
    {
        // Extension "@ayse_y" gönderiyordu ve müşteri eşleştirmesi bu anahtara
        // dayanıyor; Graph username'i @'sız veriyor. Başına @ koymazsak aynı
        // müşteri iki ayrı kayda bölünür.
        var handle = string.IsNullOrEmpty(c.Username) ? "bilinmiyor" : "@" + c.Username;

        if (_spamFilter is not null)
        {
            var reason = _spamFilter.ShouldDrop(c.Text, handle, c.TimestampUnix);
            if (reason is not null)
            {
                _log.LogDebug("[InstagramLiveCommentsPoller] dropped {Id} ({Reason})", c.Id, reason);
                return;
            }
        }

        _bus.Publish(new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            Platform: Platform,
            ExternalId: c.Id,   // gerçek comment id → mevcut dedupe olduğu gibi çalışır
            Username: handle,
            DisplayName: c.Username,
            AvatarUrl: null,    // live_media yorumlarında profil fotoğrafı yok
            Text: c.Text,
            ReceivedAt: c.TimestampUnix,
            Badges: Array.Empty<string>()));
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s.Substring(0, 200);

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
```

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramLiveCommentsPollerTests"`
Expected: 7 passed

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramLiveCommentsPoller.cs OrderDeck.Tests/Chat/Instagram/InstagramLiveCommentsPollerTests.cs
git commit -m "feat(instagram): canlı yorum polling döngüsü"
```

---

## Task 7: Hosted service (yaşam döngüsü + kapılar)

`FacebookChatHostedService`'in birebir kardeşi, tek fark: bir de **feature-flag kapısı** var (`InstagramIngestMode == OfficialApi`) ve kalıcı hatada uzun idle'a düşüyor.

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramChatHostedServiceTests.cs`

- [ ] **Step 1: Testleri yaz**

```csharp
using System;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Backoff eğrisini kilitler. Facebook ve YouTube ile aynı şekil (30s × 2ⁿ,
/// 5dk tavan) olmalı ki operatör bir platformun diğerinden çok daha hızlı
/// toparladığını görmesin.
/// </summary>
public class InstagramChatHostedServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ComputeBackoff_zero_or_one_crash_returns_short_idle(int crashes)
    {
        InstagramChatHostedService.ComputeBackoff(crashes)
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ComputeBackoff_doubles_per_crash()
    {
        InstagramChatHostedService.ComputeBackoff(2).Should().Be(TimeSpan.FromSeconds(60));
        InstagramChatHostedService.ComputeBackoff(3).Should().Be(TimeSpan.FromSeconds(120));
        InstagramChatHostedService.ComputeBackoff(4).Should().Be(TimeSpan.FromSeconds(240));
    }

    [Fact]
    public void ComputeBackoff_caps_at_five_minutes()
    {
        InstagramChatHostedService.ComputeBackoff(5).Should().Be(TimeSpan.FromMinutes(5));
        InstagramChatHostedService.ComputeBackoff(50).Should().Be(TimeSpan.FromMinutes(5));
    }
}
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramChatHostedServiceTests"`
Expected: derleme hatası — `InstagramChatHostedService` tipi yok

- [ ] **Step 3: Uygulamayı yaz**

```csharp
using System;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Instagram canlı yorum ingestor'ının yaşam döngüsü.
/// <see cref="OrderDeck.Chat.Ingestors.Facebook.FacebookChatHostedService"/>
/// ile aynı şekil; iki farkı var:
///
/// <list type="number">
///   <item><b>Feature-flag kapısı.</b> <see cref="AppSettings.InstagramIngestMode"/>
///     <c>OfficialApi</c> değilse hiç çalışmaz — varsayılan <c>Scraper</c>,
///     yani App Review onayına kadar davranış değişmez.</item>
///   <item><b>Kalıcı hata</b> (token/izin) → yeniden denemek yerine uzun idle.
///     Sonsuz retry kullanıcının token'ını düzeltmiyor, sadece log dolduruyor.</item>
/// </list>
///
/// <para>Ayrı OAuth yok: bağlı Facebook Sayfa token'ına biniyoruz.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstagramChatHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan IdleWhenOffline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleAfterPollerExit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleAfterFatal = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public const string HttpClientName = "instagram-graph";

    private readonly Func<AppSettings> _settingsProvider;
    private readonly FacebookOAuthService _oauth;
    private readonly IChatBus _bus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<InstagramChatHostedService> _log;
    private readonly ITrialModeProbe? _trialProbe;
    private readonly SpamFilter? _spamFilter;
    private readonly StreamSessionService? _sessions;
    private readonly IHttpClientFactory? _httpFactory;

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private CancellationTokenSource? _pollerCts;

    public InstagramChatHostedService(
        Func<AppSettings> settingsProvider,
        FacebookOAuthService oauth,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null,
        IHttpClientFactory? httpFactory = null)
    {
        _settingsProvider = settingsProvider;
        _oauth = oauth;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<InstagramChatHostedService>();
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _sessions = sessions;
        _httpFactory = httpFactory;

        if (_sessions is not null)
            _sessions.SessionEnded += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // Operatör "Yayını Bitir" dedi → polling'i hemen kes.
        try { _pollerCts?.Cancel(); } catch { /* ignore */ }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;
        _runner = Task.Run(() => RunAsync(ct), ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[InstagramChatHostedService] stop wait swallowed");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var resolverHttp = _httpFactory?.CreateClient(HttpClientName)
                           ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resolver = new InstagramAccountResolver(
            resolverHttp, _loggerFactory.CreateLogger<InstagramAccountResolver>());

        int consecutiveCrashes = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 1) Feature-flag. Varsayılan Scraper → hiçbir şey yapma.
                if (_settingsProvider().InstagramIngestMode != InstagramIngestMode.OfficialApi)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                if (_trialProbe?.IsTrialMode == true)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // 2) Facebook Sayfa bağlantısı yoksa IG'ye de erişemeyiz.
                var creds = await _oauth.GetPageCredentialsAsync(ct).ConfigureAwait(false);
                if (creds is null)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // 3) Operatör "Yayın Başlat"a basmadıysa Graph'a dokunma.
                if (_sessions is not null && _sessions.GetActive() is null)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                var account = await resolver.ResolveAsync(
                    creds.Value.PageId, creds.Value.PageAccessToken, ct).ConfigureAwait(false);
                if (account is null)
                {
                    _log.LogInformation(
                        "[InstagramChatHostedService] page {PageId} has no linked Instagram " +
                        "professional account; Instagram chat stays off", creds.Value.PageId);
                    await Task.Delay(IdleAfterFatal, ct);
                    continue;
                }

                _log.LogInformation(
                    "[InstagramChatHostedService] starting poller for IG @{Username}",
                    account.Value.Username ?? account.Value.IgUserId);

                var pollHttp = _httpFactory?.CreateClient(HttpClientName)
                               ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                using var poller = new InstagramLiveCommentsPoller(
                    account.Value.IgUserId,
                    creds.Value.PageAccessToken,
                    _bus,
                    pollHttp,
                    _loggerFactory.CreateLogger<InstagramLiveCommentsPoller>(),
                    _spamFilter);

                using var pollerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _pollerCts = pollerCts;

                bool crashed = false;
                try
                {
                    await poller.StartAsync(pollerCts.Token);
                    consecutiveCrashes = 0;
                    await poller.Completion.WaitAsync(pollerCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* SessionEnded — devam */ }
                catch (Exception ex)
                {
                    crashed = true;
                    consecutiveCrashes++;
                    _log.LogWarning(ex,
                        "[InstagramChatHostedService] poller crashed (#{Count} consecutive)",
                        consecutiveCrashes);
                }
                finally
                {
                    _pollerCts = null;
                    try { await poller.StopAsync(CancellationToken.None); } catch { /* ignore */ }
                }

                if (ct.IsCancellationRequested) break;

                // Kalıcı hata (token/izin) → hemen tekrar denemek anlamsız.
                if (poller.FatalReason is not null)
                {
                    _log.LogWarning(
                        "[InstagramChatHostedService] {Reason} — retrying in {Idle} min",
                        poller.FatalReason, IdleAfterFatal.TotalMinutes);
                    await Task.Delay(IdleAfterFatal, ct);
                    continue;
                }

                await Task.Delay(
                    crashed ? ComputeBackoff(consecutiveCrashes) : IdleAfterPollerExit, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[InstagramChatHostedService] outer loop error; sleeping before retry");
                try { await Task.Delay(IdleAfterPollerExit, ct); } catch { break; }
            }
        }
    }

    /// <summary>30s × 2^(n-1), 5dk tavan. Facebook/YouTube ile aynı eğri.</summary>
    internal static TimeSpan ComputeBackoff(int consecutiveCrashes)
    {
        if (consecutiveCrashes <= 1) return IdleAfterPollerExit;
        var exp = Math.Min(consecutiveCrashes - 1, 10);
        var seconds = IdleAfterPollerExit.TotalSeconds * Math.Pow(2, exp);
        if (seconds >= MaxBackoff.TotalSeconds) return MaxBackoff;
        return TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        if (_sessions is not null)
            _sessions.SessionEnded -= OnSessionEnded;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
```

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramChatHostedServiceTests"`
Expected: 4 passed (Theory dahil 5 kayıt)

`ComputeBackoff` `internal` olduğu için test projesinden görünmüyorsa
`OrderDeck.Chat/OrderDeck.Chat.csproj` içinde zaten bir
`InternalsVisibleTo("OrderDeck.Tests")` vardır (Facebook testleri aynı şekilde
`ComputeBackoff` çağırıyor). Yoksa Facebook'un nasıl eriştiğine bak ve aynısını
uygula — yeni bir mekanizma icat etme.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs OrderDeck.Tests/Chat/Instagram/InstagramChatHostedServiceTests.cs
git commit -m "feat(instagram): hosted service — feature-flag kapısı ve backoff"
```

---

## Task 8: Köprüde extension Instagram mesajlarını bastır

`OfficialApi` açıkken aynı yorum iki yoldan gelebilir: extension DOM'dan, poller
Graph'tan. **Dedupe bunu yakalayamaz** — extension `externalId`'yi DOM
düğümünden üretiyor (`ig-1-abc`), Graph gerçek yorum id'si veriyor
(`17912345678901234`). İki ayrı anahtar uzayı → `TryRegisterSeen` ikisini de
"yeni" görür → operatör her siparişi çift görür. Facebook'ta bire bir aynı hatayı
yaşadık.

Çözüm: `OfficialApi` modundayken `ExtensionBridgeServer`, extension'dan gelen
**`instagram` platformlu** chat mesajlarını sessizce düşürsün. TikTok ve diğerleri
etkilenmez.

**Kapı yönü önemli:** bayrağı bir `Func<bool>` olarak veriyoruz, `AppSettings`'i
doğrudan okumuyoruz. Sebep: `ExtensionBridgeServer` `OrderDeck.Chat`'te ve ayar
okuma AppHost'un işi; ayrıca operatör Ayarlar'dan modu değiştirdiğinde köprüyü
yeniden başlatmadan davranış değişsin istiyoruz.

**Files:**
- Modify: `OrderDeck.Chat/Bridge/ExtensionBridgeServer.cs:97-109` (ctor) ve `:243` (chat dalı)
- Test: `OrderDeck.Tests/Chat/ExtensionBridgeServerTests.cs` (mevcut dosyaya ekle)

- [ ] **Step 1: Testleri yaz**

Mevcut `ExtensionBridgeServerTests` sınıfının sonuna ekle. `SerializeChat` /
`SendRaw` yardımcıları dosyada zaten var, yeniden tanımlama.

```csharp
    // ── Official-API bastırma ────────────────────────────────────────────────

    [Fact]
    public async Task Official_mode_drops_extension_instagram_messages()
    {
        // OfficialApi açıkken IG yorumları Graph poller'dan geliyor. Extension
        // aynı yorumu farklı bir externalId ile gönderirse dedupe yakalayamaz
        // (DOM-üretimi id vs. gerçek yorum id'si) → operatör çift sipariş görür.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(
            bus, port: 0, isInstagramOfficial: () => true);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        await SendRaw(ws, SerializeChat("instagram", "@ali", "AB-25", externalId: "ig-1-abc"));
        await Task.Delay(200);

        lock (received) received.Should().BeEmpty();

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Official_mode_does_not_affect_other_platforms()
    {
        // Bastırma yalnız Instagram'a özgü. TikTok hâlâ extension'dan geliyor.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(
            bus, port: 0, isInstagramOfficial: () => true);
        await server.StartAsync(CancellationToken.None);

        var received = new TaskCompletionSource<ChatMessage>();
        using var sub = bus.Subscribe(m => received.TrySetResult(m));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        await SendRaw(ws, SerializeChat("tiktok", "@mehmet", "KIRMIZI M", externalId: "tt-1"));

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Platform.Should().Be("tiktok");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Scraper_mode_still_forwards_instagram()
    {
        // Bayrak false → eski davranış aynen. Varsayılan (bayrak hiç verilmemiş)
        // da bu olmalı; "Forwards_chat_message_from_extension_to_ChatBus" testi
        // varsayılanı zaten kilitliyor.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(
            bus, port: 0, isInstagramOfficial: () => false);
        await server.StartAsync(CancellationToken.None);

        var received = new TaskCompletionSource<ChatMessage>();
        using var sub = bus.Subscribe(m => received.TrySetResult(m));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        await SendRaw(ws, SerializeChat("instagram", "@ayse_y", "MAVI XL", externalId: "ig-9"));

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Platform.Should().Be("instagram");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ExtensionBridgeServerTests"`
Expected: derleme hatası — `ExtensionBridgeServer` ctor'unda `isInstagramOfficial`
diye bir parametre yok

- [ ] **Step 3: Uygulamayı yaz**

`ExtensionBridgeServer.cs` — alan ekle (mevcut `_viewers` alanının hemen altına,
satır 33 civarı):

```csharp
    // OfficialApi modunda extension'dan gelen Instagram yorumlarını bastırırız;
    // aynı yorum Graph poller'dan da geliyor ve iki yolun externalId uzayları
    // ayrık olduğu için dedupe yakalayamaz. Func<> çünkü operatör Ayarlar'dan
    // modu değiştirince köprüyü yeniden başlatmadan etkili olmalı.
    private readonly Func<bool>? _isInstagramOfficial;
```

Ctor imzasını genişlet (satır 97-101). **Parametre en sona eklenir** — mevcut
çağrı yerleri (AppHost + ~20 test) konumsal argüman kullanıyor, araya sokarsan
hepsi sessizce kayar:

```csharp
    public ExtensionBridgeServer(IChatBus bus, int port = 4748,
        ILogger<ExtensionBridgeServer>? log = null,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        ViewerCountTracker? viewers = null,
        Func<bool>? isInstagramOfficial = null)
    {
        _bus = bus;
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _viewers = viewers;
        _isInstagramOfficial = isInstagramOfficial;
        _log = log ?? NullLogger<ExtensionBridgeServer>.Instance;
```

Chat dalının **en başına** (satır 243'teki `if (msg is { Type: "chat", ... })`
bloğunun ilk ifadesi, trial-mode kapısından **önce**) ekle:

```csharp
                    // Instagram resmi API'deyken extension kopyası düşer.
                    // Trial kapısından önce: trial modda da çift yayın istemiyoruz.
                    if (string.Equals(msg.Platform, "instagram", StringComparison.OrdinalIgnoreCase) &&
                        _isInstagramOfficial?.Invoke() == true)
                    {
                        _log.LogDebug(
                            "Instagram OfficialApi mode: dropping extension message from {Username}",
                            msg.Username);
                        continue;
                    }
```

- [ ] **Step 4: Testleri çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ExtensionBridgeServerTests"`
Expected: hepsi geçer — 3 yeni test dahil, mevcut testlerin hiçbiri kırılmamalı
(bayrak verilmediğinde `_isInstagramOfficial` null → eski davranış).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Chat/Bridge/ExtensionBridgeServer.cs OrderDeck.Tests/Chat/ExtensionBridgeServerTests.cs
git commit -m "feat(instagram): resmi API açıkken extension yorumlarını bastır"
```

---

## Task 9: AppHost kaydı

Üç şey: named HTTP client, hosted service kaydı, köprüye bayrak.

Bu task'ın testi yok — DI kaydı derleme + gerçek çalıştırma ile doğrulanır.
`AppHost` `net10.0-windows`; `OrderDeck.Tests` WPF projesine referans vermiyor.
Yalan bir test yazmak yerine Task 11'deki uçtan doğrulamaya bırakıyoruz.

**Files:**
- Modify: `OrderDeck.App/AppHost.cs:116-122` (köprü kaydı) ve `:219-228` sonrası
  (Facebook hosted service kaydının hemen altı)

- [ ] **Step 1: Named client'ı ekle**

`AppHost.cs`'te Facebook `StreamClientName` kaydının bittiği yere (satır 201'den
sonra, `EncryptedFacebookTokenStore` kaydından önce) ekle:

```csharp
        // Instagram canlı yorum polling'i (2026-08-05). Facebook'un SSE'sinden
        // farklı: saniyede ~1 kısa istek. Bu yüzden ayrı named client — bağlantı
        // havuzu sıcak kalsın, her poll'da yeni TCP el sıkışması olmasın.
        services.AddHttpClient(
            OrderDeck.Chat.Ingestors.Instagram.InstagramChatHostedService.HttpClientName,
            c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                // Poll aralığı 1sn; havuzu 10 dakika açık tut ki her istekte
                // yeniden bağlanmayalım.
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 4,
            });
```

- [ ] **Step 2: Hosted service'i kaydet**

Facebook `AddHostedService` bloğunun (satır 219-228) hemen altına:

```csharp
        // Instagram canlı yorumları — resmi Graph API. Facebook OAuth
        // servisini paylaşıyor (IG erişimi Sayfa token'ı üzerinden gidiyor;
        // ayrı bir IG oturumu yok). Varsayılan olarak uykuda:
        // AppSettings.InstagramIngestMode == Scraper iken hiçbir çağrı yapmaz.
        services.AddHostedService(sp =>
            new OrderDeck.Chat.Ingestors.Instagram.InstagramChatHostedService(
                () => sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<OrderDeck.Chat.Facebook.FacebookOAuthService>(),
                sp.GetRequiredService<IChatBus>(),
                sp.GetRequiredService<ILoggerFactory>(),
                trialProbe: sp.GetRequiredService<LicenseService>(),
                spamFilter: sp.GetRequiredService<SpamFilter>(),
                sessions: sp.GetRequiredService<StreamSessionService>(),
                httpFactory: sp.GetRequiredService<IHttpClientFactory>()));
```

- [ ] **Step 3: Köprüye bayrağı ver**

`AppHost.cs:116-122`'yi güncelle:

```csharp
        services.AddSingleton(sp => new ExtensionBridgeServer(
            sp.GetRequiredService<IChatBus>(),
            port: 4748,
            log: sp.GetRequiredService<ILogger<ExtensionBridgeServer>>(),
            trialProbe: sp.GetRequiredService<LicenseService>(),
            spamFilter: sp.GetRequiredService<SpamFilter>(),
            viewers: sp.GetRequiredService<ViewerCountTracker>(),
            // Resmi API açıkken extension'ın IG yorumları düşer; aynı yorum
            // Graph poller'dan geliyor ve iki yolun externalId'leri ayrık.
            isInstagramOfficial: () =>
                sp.GetRequiredService<AppSettings>().InstagramIngestMode
                    == InstagramIngestMode.OfficialApi));
```

`InstagramIngestMode` `OrderDeck.Core.Chat` altında. `AppHost.cs`'in using'leri
arasında `OrderDeck.Core.Chat` yoksa tam nitelikli yaz
(`OrderDeck.Core.Chat.InstagramIngestMode.OfficialApi`) — dosyanın geri kalanı
zaten `OrderDeck.Chat.Ingestors.*` için tam nitelikli isim kullanıyor.

- [ ] **Step 4: Derle**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: 0 hata. Özellikle **`CA1416` platform uyarısı çıkmamalı** —
`InstagramChatHostedService` `[SupportedOSPlatform("windows")]` işaretli
(DPAPI token store'a bağlı) ve `AppHost` zaten Windows-only.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.App/AppHost.cs
git commit -m "feat(instagram): hosted service ve named client'ı AppHost'a bağla"
```

---

## Task 10: İzin kontrolü (`instagram_manage_comments` verilmiş mi?)

Mevcut kullanıcıların **hepsi** IG izinleri eklenmeden önce bağlandı. Onların
token'ında `instagram_basic` / `instagram_manage_comments` **yok** — resmi API'yi
açtıklarında sessizce boş yorum listesi görürler. Ayarlar'da "yeniden bağlan"
uyarısı bu yüzden şart.

`GET /me/permissions` **kullanıcı** token'ı ister — Sayfa token'ıyla `/me` Sayfa'yı
işaret eder ve yanlış cevap verir. `FacebookOAuthService` şu an dışarıya kullanıcı
token'ı vermiyor; küçük bir erişimci ekliyoruz.

**Files:**
- Modify: `OrderDeck.Chat/Facebook/FacebookOAuthService.cs:116-121` (hemen altına yeni metot)
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramPermissionProbe.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramPermissionProbeTests.cs`

- [ ] **Step 1: Testleri yaz**

```csharp
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// `GET /me/permissions` yanıtının ayrıştırılması. HTTP'siz — saf metin girdisi.
/// </summary>
public class InstagramPermissionProbeTests
{
    private const string GrantedBoth =
        """
        {"data":[
          {"permission":"pages_show_list","status":"granted"},
          {"permission":"instagram_basic","status":"granted"},
          {"permission":"instagram_manage_comments","status":"granted"}
        ]}
        """;

    private const string OldConnection =
        """
        {"data":[
          {"permission":"pages_show_list","status":"granted"},
          {"permission":"pages_read_engagement","status":"granted"}
        ]}
        """;

    private const string Declined =
        """
        {"data":[
          {"permission":"instagram_basic","status":"granted"},
          {"permission":"instagram_manage_comments","status":"declined"}
        ]}
        """;

    [Fact]
    public void Both_permissions_granted_returns_true()
    {
        InstagramPermissionProbe.HasInstagramPermissions(GrantedBoth).Should().BeTrue();
    }

    [Fact]
    public void Pre_instagram_connection_returns_false()
    {
        // IG izinleri eklenmeden önce bağlanmış kullanıcı — uyarıyı görmeli.
        InstagramPermissionProbe.HasInstagramPermissions(OldConnection).Should().BeFalse();
    }

    [Fact]
    public void Declined_is_not_granted()
    {
        // Kullanıcı izin ekranında IG'yi kaldırdı → "granted" değil.
        InstagramPermissionProbe.HasInstagramPermissions(Declined).Should().BeFalse();
    }

    [Fact]
    public void Basic_without_manage_comments_returns_false()
    {
        // instagram_manage_comments olmadan yorumlarda `username` gelmiyor
        // (Meta 27 Ağu 2024 değişikliği) — yarım izin işe yaramaz.
        const string json =
            """{"data":[{"permission":"instagram_basic","status":"granted"}]}""";
        InstagramPermissionProbe.HasInstagramPermissions(json).Should().BeFalse();
    }

    [Fact]
    public void Malformed_json_returns_false()
    {
        InstagramPermissionProbe.HasInstagramPermissions("not json").Should().BeFalse();
        InstagramPermissionProbe.HasInstagramPermissions("").Should().BeFalse();
        InstagramPermissionProbe.HasInstagramPermissions("{}").Should().BeFalse();
    }

    [Fact]
    public void Error_response_returns_false()
    {
        // Token süresi dolmuş → `data` yok, `error` var. Uyarı göstermek doğru
        // davranış: kullanıcı zaten yeniden bağlanmalı.
        const string json =
            """{"error":{"message":"Session has expired","code":190}}""";
        InstagramPermissionProbe.HasInstagramPermissions(json).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramPermissionProbeTests"`
Expected: derleme hatası — `InstagramPermissionProbe` tipi yok

- [ ] **Step 3: OAuth servisine kullanıcı token erişimcisi ekle**

`FacebookOAuthService.cs`, `GetPageCredentialsAsync`'in hemen altına:

```csharp
    /// <summary>
    /// Returns the stored long-lived <b>user</b> access token, or null if not
    /// connected. Only needed for user-scoped edges such as
    /// <c>GET /me/permissions</c> — Page-scoped calls must keep using
    /// <see cref="GetPageCredentialsAsync"/>, since with a Page token
    /// <c>/me</c> resolves to the Page and returns the wrong answer.
    /// </summary>
    public async Task<string?> GetUserAccessTokenAsync(CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct).ConfigureAwait(false)) return null;
        return _bundle!.UserAccessToken;
    }
```

- [ ] **Step 4: Probe'u yaz**

Create `OrderDeck.Chat/Ingestors/Instagram/InstagramPermissionProbe.cs`:

```csharp
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Bağlı Facebook oturumunda Instagram izinlerinin verilip verilmediğini
/// kontrol eder.
///
/// <para><b>Neden gerekli:</b> IG izinleri Facebook Login for Business
/// yapılandırmasına 2026-08-05'te eklendi. Daha önce bağlanmış operatörlerin
/// token'ında bu izinler yok ve Graph, izin eksikliğini <b>hata olarak değil</b>
/// alanı yanıttan düşürerek bildiriyor — yani resmi API sessizce hiç yorum
/// getirmez. Ayarlar'da açık bir "yeniden bağlan" uyarısı olmazsa operatör
/// bunu asla anlayamaz.</para>
/// </summary>
public sealed class InstagramPermissionProbe
{
    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";

    /// <summary>Yorum okumak için ikisi de şart: <c>instagram_basic</c>
    /// olmadan hesap uçları kapalı, <c>instagram_manage_comments</c> olmadan
    /// yorumlarda <c>username</c> gelmiyor (Meta, 27 Ağustos 2024).</summary>
    private static readonly string[] Required =
        { "instagram_basic", "instagram_manage_comments" };

    private readonly HttpClient _http;
    private readonly ILogger<InstagramPermissionProbe> _log;

    public InstagramPermissionProbe(HttpClient http, ILogger<InstagramPermissionProbe> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// <c>GET /me/permissions</c> — <paramref name="userAccessToken"/>
    /// <b>kullanıcı</b> token'ı olmalı. Ağ hatasında null döner ("bilinmiyor"),
    /// böylece geçici bir kesinti yanlışlıkla "izin yok" uyarısına dönüşmez.
    /// </summary>
    public async Task<bool?> HasInstagramPermissionsAsync(
        string userAccessToken, CancellationToken ct = default)
    {
        var url = $"{GraphBase}/me/permissions" +
                  $"?access_token={Uri.EscapeDataString(userAccessToken)}";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // 400 + code 190 (token süresi dolmuş) da geçerli bir cevap: izin yok.
            if (!resp.IsSuccessStatusCode && body.Contains("\"error\"", StringComparison.Ordinal))
                return HasInstagramPermissions(body);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("[InstagramPermissionProbe] {Status}", (int)resp.StatusCode);
                return null;
            }
            return HasInstagramPermissions(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[InstagramPermissionProbe] permission check failed");
            return null;
        }
    }

    /// <summary>Saf ayrıştırıcı — <c>{"data":[{"permission":..,"status":..}]}</c>.
    /// Gerekli izinlerin <b>hepsi</b> <c>granted</c> ise true.</summary>
    public static bool HasInstagramPermissions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var required in Required)
            {
                bool found = false;
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = item.TryGetProperty("permission", out var p) ? p.GetString() : null;
                    if (!string.Equals(name, required, StringComparison.Ordinal)) continue;
                    var status = item.TryGetProperty("status", out var s) ? s.GetString() : null;
                    found = string.Equals(status, "granted", StringComparison.Ordinal);
                    break;
                }
                if (!found) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 5: Testleri çalıştır**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~InstagramPermissionProbeTests"`
Expected: 6 passed

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.Chat/Facebook/FacebookOAuthService.cs OrderDeck.Chat/Ingestors/Instagram/InstagramPermissionProbe.cs OrderDeck.Tests/Chat/Instagram/InstagramPermissionProbeTests.cs
git commit -m "feat(instagram): izin kontrolü (instagram_manage_comments)"
```

---

## Task 11: Ayarlar arayüzü

Facebook sekmesine ikinci bir kart: resmi API anahtarı, IG hesap durumu, izin
uyarısı. **Yeni ayar alanı yok** — `AppSettings.InstagramIngestMode` zaten var,
şu ana kadar hiçbir yerden okunmuyordu.

Bu task'ın otomatik testi yok (WPF view + async DI). `OrderDeck.Tests`
`OrderDeck.App`'e referans vermiyor; sırf test yazmak için VM'i taşımak
istenmeyen bir refactor olur. Doğrulama Task 12'de elle.

**Files:**
- Modify: `OrderDeck.App/ViewModels/SettingsViewModel.cs` (alanlar 74-81 sonrası,
  ctor 114-138, `LoadFromSettings` ~373, `Save` ~472)
- Modify: `OrderDeck.App/Views/SettingsDialog.xaml:235` (Facebook kartından sonra)

- [ ] **Step 1: ViewModel alanlarını ekle**

`SettingsViewModel.cs`, Facebook alanlarının hemen altına (satır 81 sonrası):

```csharp
    // Instagram resmi Graph API (2026-08-05). Ayrı bir OAuth yok — IG erişimi
    // bağlı Facebook Sayfa'sı üzerinden gidiyor. Bu yüzden burada yalnız
    // anahtar + durum var, "Bağlan" butonu yok.
    [ObservableProperty] private bool _useOfficialInstagramApi;
    [ObservableProperty] private string _instagramAccountStatus = "Kontrol ediliyor...";

    /// <summary>Bağlı Facebook oturumunda IG izinleri yoksa true — kullanıcı
    /// IG izinleri yapılandırmaya eklenmeden önce bağlanmış demektir ve
    /// yeniden bağlanmadan resmi API sessizce boş döner.</summary>
    [ObservableProperty] private bool _instagramNeedsReconnect;
```

- [ ] **Step 2: Ctor'a bağımlılıkları ekle**

İmzanın **sonuna** ekle (satır 120'deki `waTemplateSync` parametresinden sonra;
araya sokarsan konumsal çağrılar kayar):

```csharp
        System.Net.Http.IHttpClientFactory? httpFactory = null)
```

Alanı sınıfa ekle (satır 112 civarı, `_waTemplateSync`'in altına):

```csharp
    private readonly System.Net.Http.IHttpClientFactory? _httpFactory;
```

Ctor gövdesinde ata ve durum yenilemeyi başlat (satır 130-138 arası):

```csharp
        _httpFactory = httpFactory;
        ...
        _ = RefreshFacebookConnectionStatusAsync();
        _ = RefreshInstagramStatusAsync();
```

- [ ] **Step 3: Durum yenileyiciyi yaz**

`RefreshFacebookConnectionStatusAsync`'in hemen altına:

```csharp
    /// <summary>
    /// IG durumunu iki soruyla belirler: (1) bağlı Sayfa'ya bir IG profesyonel
    /// hesabı bağlı mı, (2) oturumda IG izinleri var mı. İkisi de Graph'a birer
    /// istek — dialog açılışında bir kez çalışır, yayın sırasında değil.
    /// </summary>
    public async System.Threading.Tasks.Task RefreshInstagramStatusAsync()
    {
        if (_facebookOAuth is null)
        {
            InstagramAccountStatus = "Önce Facebook'a bağlan.";
            InstagramNeedsReconnect = false;
            return;
        }

        try
        {
            var creds = await _facebookOAuth.GetPageCredentialsAsync().ConfigureAwait(true);
            if (creds is null)
            {
                InstagramAccountStatus = "Önce Facebook'a bağlan.";
                InstagramNeedsReconnect = false;
                return;
            }

            var http = _httpFactory?.CreateClient(
                           OrderDeck.Chat.Ingestors.Instagram.InstagramChatHostedService.HttpClientName)
                       ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            // İzin kontrolü kullanıcı token'ı ister (Sayfa token'ıyla /me Sayfa olur).
            var userToken = await _facebookOAuth.GetUserAccessTokenAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(userToken))
            {
                var probe = new OrderDeck.Chat.Ingestors.Instagram.InstagramPermissionProbe(
                    http, Microsoft.Extensions.Logging.Abstractions
                        .NullLogger<OrderDeck.Chat.Ingestors.Instagram.InstagramPermissionProbe>.Instance);
                // null = bilinmiyor (ağ hatası) → uyarı gösterme, yanlış alarm olmasın.
                InstagramNeedsReconnect =
                    await probe.HasInstagramPermissionsAsync(userToken!).ConfigureAwait(true) == false;
            }

            var resolver = new OrderDeck.Chat.Ingestors.Instagram.InstagramAccountResolver(
                http, Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<OrderDeck.Chat.Ingestors.Instagram.InstagramAccountResolver>.Instance);
            var account = await resolver.ResolveAsync(
                creds.Value.PageId, creds.Value.PageAccessToken,
                System.Threading.CancellationToken.None).ConfigureAwait(true);

            // InstagramAccount bir record struct → nullable erişim .Value ile.
            InstagramAccountStatus = account is null
                ? "Bu Sayfa'ya bağlı Instagram profesyonel hesabı yok."
                : $"Bağlı Instagram hesabı: @{account.Value.Username ?? account.Value.IgUserId}";
        }
        catch (Exception ex)
        {
            InstagramAccountStatus = $"Durum okunamadı: {ex.Message}";
            InstagramNeedsReconnect = false;
        }
    }
```

`NullLogger` için dosyanın using'lerinde `Microsoft.Extensions.Logging.Abstractions`
yoksa yukarıdaki tam nitelikli hâli bırak — yeni using ekleyip dosyanın geri
kalanını kirletme.

- [ ] **Step 4: Load / Save bağla**

`LoadFromSettings` içinde, YouTube satırının (373) altına:

```csharp
        // Instagram — resmi Graph API opt-in.
        UseOfficialInstagramApi =
            _liveSettings.InstagramIngestMode == OrderDeck.Core.Chat.InstagramIngestMode.OfficialApi;
```

`Save` içinde, YouTube bloğunun (472-474) altına:

```csharp
        // Instagram. Köprü bu değeri Func ile her mesajda okuyor, hosted service
        // döngü başında — Kaydet'e basar basmaz etkili, yeniden başlatma yok.
        _liveSettings.InstagramIngestMode = UseOfficialInstagramApi
            ? OrderDeck.Core.Chat.InstagramIngestMode.OfficialApi
            : OrderDeck.Core.Chat.InstagramIngestMode.Scraper;
```

- [ ] **Step 5: XAML kartını ekle**

`SettingsDialog.xaml`, Facebook `Border`'ının kapanışından sonra (satır 235 ile
236 arası, `</StackPanel>`'den önce):

```xml
                    <Border Style="{StaticResource S.Card}">
                        <StackPanel>
                            <TextBlock Style="{StaticResource S.CardTitle}" Text="Instagram canlı yorumlar" Margin="0,0,0,14"/>
                            <TextBlock Text="{Binding InstagramAccountStatus}"
                                       Foreground="{StaticResource S.Text}" Margin="0,0,0,10" TextWrapping="Wrap"/>
                            <TextBlock Margin="0,0,0,10" TextWrapping="Wrap"
                                       Foreground="{StaticResource S.Amber}"
                                       Visibility="{Binding InstagramNeedsReconnect, Converter={StaticResource BoolToVis}}"
                                       Text="Instagram yorumları için Facebook bağlantını yenilemen gerekiyor — yukarıdaki 'Bağlantıyı Kaldır' + 'Facebook'a Bağlan' adımını uygula."/>
                            <CheckBox Content="Instagram yorumlarını resmi API'den al (deneysel)"
                                      IsChecked="{Binding UseOfficialInstagramApi}"
                                      FontWeight="SemiBold" Margin="0,0,0,8"/>
                            <TextBlock Style="{StaticResource S.Hint}">
                                Kapalıyken yorumlar Chrome eklentisinden gelir (bugünkü davranış). Açtığında yorumlar
                                doğrudan Instagram'ın resmi API'sinden okunur — tarayıcı sekmesi açık olmasına gerek kalmaz.
                                Instagram canlı yayında yorum silme/gizleme desteklemediği için resmi yolda sağ-tık
                                moderasyon menüsü çıkmaz. Meta onayı tamamlanana kadar varsayılan kapalıdır.
                            </TextBlock>
                        </StackPanel>
                    </Border>
```

`BoolToVis` dönüştürücüsü dosyanın 17. satırında zaten tanımlı.

- [ ] **Step 6: Derle**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: 0 hata

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.App/ViewModels/SettingsViewModel.cs OrderDeck.App/Views/SettingsDialog.xaml
git commit -m "feat(instagram): Ayarlar'da resmi API anahtarı ve hesap/izin durumu"
```

---

## Task 12: Uçtan doğrulama ve kapanış

- [ ] **Step 1: Tam derleme**

```bash
dotnet build OrderDeck.Chat/OrderDeck.Chat.csproj
dotnet build OrderDeck.App/OrderDeck.App.csproj
```
Expected: ikisi de 0 hata, **0 yeni uyarı**. Özellikle `CA1416` yok.

- [ ] **Step 2: Tüm test paketi**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
```
Expected: mevcut ~620 test + bu plandaki yeniler geçiyor, **hiçbir mevcut test
kırılmamış**. Kırılan varsa neredeyse kesinlikle `ExtensionBridgeServer` ctor'una
eklenen parametredir (Task 8) — konumsal argüman kullanan bir test kaymıştır.

- [ ] **Step 3: Kapsam denetimi**

```bash
grep -rn "InstagramIngestMode" --include=*.cs .
```
Expected: tam 4 yer — `OrderDeck.Core/Chat/InstagramIngestMode.cs` (tanım),
`AppSettings.cs` (alan), `InstagramChatHostedService.cs` (kapı),
`AppHost.cs` (köprü bayrağı) + `SettingsViewModel.cs` (load/save).
Başka yerde okunuyorsa fazlalık var.

```bash
grep -rn "instagram" Extension/ --include=*.js -l
```
Expected: extension **değişmedi** — IG scraper yerinde duruyor. Bu plan
extension'a dokunmuyor; `Scraper` hâlâ varsayılan ve geri dönüş yolu o.

```bash
grep -n "Platform, \"" OrderDeck.App/ViewModels/MainShellViewModel.cs
```
Expected: moderasyon komutlarının hepsi `"youtube"` veya `"facebook"` ile
kapılı (satır ~1036-1233), `"instagram"` hiç geçmiyor. Spec'in "IG'de sağ-tık
moderasyon menüsü açılmaz" maddesi **kendiliğinden** sağlanıyor — kod
eklenmeyecek. Bu adım sadece doğrulama.

- [ ] **Step 4: Gerçek yayın pilotu (kullanıcı)**

Ayarlar → Facebook sekmesi:
1. "Bağlı Instagram hesabı: @…" görünüyor mu? Görünmüyorsa IG business hesabı
   Sayfa'ya bağlı değildir — Meta Business Suite'ten bağla.
2. Sarı "bağlantını yenilemen gerekiyor" uyarısı çıktıysa Facebook bağlantısını
   kaldır + yeniden bağlan, izin ekranında Instagram izinlerini onayla.
3. "Instagram yorumlarını resmi API'den al" işaretle → Kaydet.
4. Instagram'dan canlı yayın aç, telefondan birkaç yorum yaz.

Log'da (`~/Documents/OrderDeck/Logs/log-YYYYMMDD.txt`):
```
grep -iE "Instagram(ChatHostedService|LiveCommentsPoller|AccountResolver)" ~/Documents/OrderDeck/Logs/log-*.txt
```
Beklenen: hesap çözümlendi → poller başladı → yorumlar chat paneline düşüyor,
**çift görünmüyor** (extension kopyası bastırılmış olmalı).

- [ ] **Step 5: Pilotta ölçülecekler (spec'teki açık sorular)**

Bunlar plana yazılmadı çünkü Meta belgelemiyor; pilotta ölçülüp spec'e işlenecek:

| Soru | Nasıl bakılır |
|---|---|
| `ads_read` gerçekten şart mı? | İzin verilmeden `comments` çağrısı 200 dönüyor mu, yoksa `#200` mü? |
| `comments.limit(50)` gerçekten 50 mi döndürüyor, 25 mi? | Yoğun anda dönen dizi uzunluğu |
| `X-Business-Use-Case-Usage` hangi eşikte doluyor? | 1 sn polling'de yayın boyunca `call_count` |
| Hidden Words filtresi yorumları gizliyor mu? | Meta'nın gizlediği bir yorumu API döndürüyor mu? |
| Yayın sonu kayıp ne kadar? | Son yorum ile yayın bitişi arasındaki fark |
| Sıralama gerçekten ters-kronolojik mi? | Ham JSON'da ilk eleman en yeni mi? |

Ölçüm sonuçları farklı çıkarsa **önce spec'i güncelle**, sonra kodu.

- [ ] **Step 6: Spec'e kapanış notu**

`docs/superpowers/specs/2026-08-04-instagram-live-graph-api-design.md` sonuna:

```markdown
## Uygulama durumu

Kod planı: `docs/superpowers/plans/2026-08-05-instagram-live-graph-api.md`
(12 task, TDD). Varsayılan `Scraper` — resmi yol Ayarlar'dan opt-in.
App Review başvurusunda bu anahtar ekran kaydıyla gösterilecek (Meta
test edemediği izni onaylamıyor).
```

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-08-04-instagram-live-graph-api-design.md
git commit -m "docs(instagram): spec'e uygulama durumu notu"
```

---

## Yayın

Tek PR: `feat/instagram-official-api` → `master`, başlık
`feat(instagram): canlı yorumları resmi Graph API'den oku`.

**PR'a karıştırılmayacak** (commit'siz duran dosyalar):
`.claude/launch.json`, `.gitignore`, `docs/proje-analiz-raporu-2026-07-16.md`,
`docs/superpowers/plans/2026-07-28-whatsapp-odeme-hatirlatma-cloud-api.md`,
`docs/superpowers/specs/2026-07-28-whatsapp-otomasyon-design.md`.

## Kapsam dışı

- **Moderasyon:** Meta canlı IG yorumlarında hide/delete desteklemiyor. Mevcut
  scraper'da da yok → kayıp yok.
- **Webhook:** `comments` webhook'u ileride seçenek; şimdilik 1 sn polling.
- **İzleyici sayısı:** `live_media` vermiyor; extension'daki kod zaten hiç
  çalışmadı → kayıp yok.
- **Extension'dan IG scraper'ını silmek:** App Review onayı gelip varsayılan
  `OfficialApi` olana kadar geri dönüş yolu olarak duruyor.
- **`GraphApiVersion` yükseltmesi** (v22 → v25): ayrı PR, tüm Facebook
  entegrasyonunu etkiler.
