using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
using OrderDeck.Tests.TestHelpers;
using static OrderDeck.Tests.TestHelpers.FacebookOAuthTestKit;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Çoklu hesap günlüğünün denetimi (çoklu hesap tasarımı §8.8, §8.1, §8.2): iki hesaplı bir oturumun ve OAuth
/// servisinin hata yollarının bütün satırları taranır.
///
/// NEDEN: depo herkese açık ve günlükler destek için paylaşılıyor. Hiçbir düzeyde token ya da
/// <c>access_token</c> taşıyan adres (<c>paging.next</c> dahil) yazılmamalı; Information ve üstünde izleyici
/// kullanıcı adı ve yorum metni olmamalı; yeni satırlar filtrelenebilsin diye kategorilerinin önekini taşımalı.
/// Aranan değerlerin hepsi testin ürettiği ya da kitten okuduğu değerlerdir; literal yazılmaz.
/// </summary>
public sealed class InstagramLoggingAuditTests : IDisposable
{
    private const string AccountPrefix = "[InstagramChatHostedService] account ";
    private const string UsagePrefix = "[InstagramChatHostedService] usage ";

    private const string TransitionLine =
        @"^\[InstagramChatHostedService\] account .+ \(page [^)]+\): \w+ -> \w+(, retry at \d\d:\d\d:\d\d)?$";

    private const string Bucket =
        @"business \S+ call_count [\d.]+% total_cputime [\d.]+% total_time [\d.]+% regain [\d.]+ min";

    private const string UsageLine =
        @"^\[InstagramChatHostedService\] usage .+ \(page [^)]+\): (" + Bucket + "(; " + Bucket +
        @")*|X-Business-Use-Case-Usage header (absent|unreadable))$";

    private readonly FacebookOAuthTestKit _kit = new();
    private readonly ManualTime _time = new();
    private readonly CapturingLoggerFactory _logs = new();
    private readonly ChatBus _bus = new(ringBufferSize: 50);
    private readonly ConcurrentQueue<ChatMessage> _received = new();

    public InstagramLoggingAuditTests()
    {
        _bus.Subscribe(_received.Enqueue);
    }

    public void Dispose() => _kit.Dispose();

    /// <summary>
    /// İhlalleri "kural: kategori: ileti" biçiminde döner (boş = temiz; bir satır bir kuralı en çok bir kez
    /// ihlal eder):
    /// 1. her düzeyde ileti + istisna metninde token'lardan biri ya da <c>access_token=</c> geçiyorsa;
    /// 2. Information ve üstünde ileti + istisna metninde izleyici verisinden biri geçiyorsa;
    /// 3. Information ve üstünde, kategorisi InstagramChatHostedService / InstagramMergeGate /
    ///    FacebookOAuthService ile biten satırın iletisi sırasıyla "[InstagramChatHostedService]" /
    ///    "[InstagramMergeGate]" / "Facebook OAuth" ile başlamıyorsa.
    /// </summary>
    private static IReadOnlyList<string> Violations(
        IEnumerable<CapturingLoggerFactory.Entry> entries,
        IReadOnlyCollection<string> tokens,
        IReadOnlyCollection<string> viewerData)
    {
        (string CategorySuffix, string Prefix)[] prefixes =
        [
            (nameof(InstagramChatHostedService), "[InstagramChatHostedService]"),
            (nameof(InstagramMergeGate), "[InstagramMergeGate]"),
            (nameof(FacebookOAuthService), "Facebook OAuth"),
        ];

        var violations = new List<string>();
        foreach (var entry in entries)
        {
            var full = entry.Full;
            var visible = entry.Level >= LogLevel.Information;

            if (full.Contains("access_token=", StringComparison.Ordinal)
                || tokens.Any(token => full.Contains(token, StringComparison.Ordinal)))
                violations.Add($"token ya da adres: {entry.Category}: {entry.Message}");

            if (visible && viewerData.Any(value => full.Contains(value, StringComparison.Ordinal)))
                violations.Add($"izleyici verisi Information ve üstünde: {entry.Category}: {entry.Message}");

            if (visible)
            {
                foreach (var (suffix, prefix) in prefixes)
                {
                    if (entry.Category.EndsWith(suffix, StringComparison.Ordinal)
                        && !entry.Message.StartsWith(prefix, StringComparison.Ordinal))
                        violations.Add($"önek yok ({prefix}): {entry.Category}: {entry.Message}");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Sahte Graph'a giden her isteğin <c>access_token</c>'ı + depodaki demetin kullanıcı ve Sayfa token'ları
    /// (ekler dahil).
    /// </summary>
    private async Task<HashSet<string>> TokensAsync()
    {
        var tokens = _kit.Graph.Requests
            .Select(InstagramLiveFeed.TokenIn)
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        if (await _kit.Store.LoadAsync() is { } bundle)
        {
            tokens.Add(bundle.UserAccessToken);
            tokens.Add(bundle.PageAccessToken);
            foreach (var extra in bundle.ExtraPages ?? [])
                tokens.Add(extra.PageAccessToken);
        }

        return tokens;
    }

    /// <summary>Information ve üstü, iletisi <paramref name="prefix"/> ile başlayan satırlar (yazım sırasıyla).</summary>
    private IReadOnlyList<string> Info(string prefix) => _logs.Entries
        .Where(e => e.Level >= LogLevel.Information && e.Message.StartsWith(prefix, StringComparison.Ordinal))
        .Select(e => e.Message)
        .ToList();

    /// <summary>Koşul sağlanana dek 20 ms'de bir yoklar; <see cref="TestPatience.Upper"/> dolarsa düşer.</summary>
    private static async Task Until(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TestPatience.Upper)
                throw new TimeoutException($"koşul {TestPatience.Upper.TotalSeconds} sn'de sağlanmadı");
            await Task.Delay(20);
        }
    }

    /// <summary>Fırlayan istisnayı döner; fırlamazsa testi düşürür.</summary>
    private static async Task<Exception> ThrownBy(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new Xunit.Sdk.XunitException("istisna bekleniyordu, fırlamadı");
    }

    [Fact]
    public void Tarayici_sizintiyi_ve_yanlis_oneki_yakalar()
    {
        var (token, viewer) = (NewToken(), NewId());
        var log = _logs.CreateLogger("denetim");
        log.LogDebug("istek {Url}", $"https://example.test/x?access_token={token}"); // her düzeyde yasak
        log.LogInformation("yorum {User}", viewer);                                   // Information'da yasak
        log.LogDebug("yorum {User}", viewer);                                         // Debug'da serbest
        _logs.CreateLogger(typeof(InstagramChatHostedService).FullName!).LogInformation("öneksiz satır");

        Violations(_logs.Entries, [token], [viewer]).Should().HaveCount(3);
    }

    [Fact]
    public async Task Iki_hesapli_oturumun_gunlugunde_token_adres_ve_izleyici_verisi_yok()
    {
        var p = await _kit.SeedAsync(extraPages: 1);
        var business = NewId();
        var a = new InstagramLiveFeed(_kit.Graph, p[0].IgUserId!) { UsageHeader = InstagramLiveFeed.Usage(business, 12.5, 3, 4) };
        var b = new InstagramLiveFeed(_kit.Graph, p[1].IgUserId!); // başlıksız
        var oauth = _kit.CreateService(_logs.CreateLogger<FacebookOAuthService>());
        var timings = new InstagramChatTimings(
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(1));
        using var service = new InstagramChatHostedService(
            oauth, _bus, _logs, new SpamFilter(() => new SpamFilterSettings()), sessions: null, _kit.HttpFactory,
            new InstagramHealthMonitor(_time), _time, timings);
        await service.StartAsync(default);
        await Task.WhenAll(a.PrimedAsync(), b.PrimedAsync());
        var first = a.Add();
        var repeat = a.Add(first.Viewer, first.Text); // yineleme süzgeçten düşer
        var other = b.Add();
        await Until(() => _received.Any(m => m.ExternalId == other.Id) && _logs.Entries.Any(e => e.Message.Contains(repeat.Id)));
        var (extraId, extraName, extraIg) = (NewId(), NewId(), NewId()); // üçüncü Sayfa: ekle, yoklansın, kaldır
        _kit.Graph.AddPage(extraId, extraName, extraIg, NewId());
        var c = new InstagramLiveFeed(_kit.Graph, extraIg);
        await oauth.AddExtraPageAsync(extraId, extraName);
        await c.PrimedAsync();
        await oauth.RemoveExtraPageAsync(extraId);
        await _kit.ReauthorizeAllAsync(oauth); // izin güncelleme
        b.Intercept = _ => FakeGraph.Error(4);   // hız sınırı: "retry at" taşıyan geçiş
        a.Intercept = _ => FakeGraph.Error(190); // kalıcı hata
        await Until(() => Info(AccountPrefix).Any(l => l.Contains("-> RateLimited, retry at"))
            && Info(AccountPrefix).Any(l => l.Contains("-> Faulted, retry at"))); // kalıcı hatanın satırı da saat taşır
        await service.StopAsync(default); // kapı özeti Dispose'ta yazılır

        Violations(_logs.Entries, await TokensAsync(), [first.Viewer, first.Text, other.Viewer, other.Text]).Should().BeEmpty();
        _logs.Entries.Single(e => e.Message.Contains(repeat.Id)).Level.Should().Be(LogLevel.Debug); // düşen yorum yalnız kimliğiyle
        Info("Facebook OAuth").Count(l => l.Contains(extraId) && l.Contains(extraName)).Should().BeGreaterThanOrEqualTo(2); // ekleme + kaldırma
        Info("[InstagramMergeGate]").Should().NotBeEmpty();
        Info(AccountPrefix).Should().OnlyContain(l => Regex.IsMatch(l, TransitionLine));
        Info(UsagePrefix).Should().OnlyContain(l => Regex.IsMatch(l, UsageLine))
            .And.Contain(l => l.Contains($"business {business} call_count 12.5% total_cputime 3% total_time 4% regain 0 min"))
            .And.Contain(l => l.EndsWith("X-Business-Use-Case-Usage header absent"));
    }

    [Fact]
    public void Token_tasiyan_kayitlar_sablona_verilse_de_token_basmaz()
    {
        var (user, page, extra) = (NewToken(), NewToken(), NewToken());
        var linked = new FacebookLinkedPage(NewId(), extra, NewId());
        _logs.CreateLogger("denetim").LogInformation(
            "{Page} {Bundle}", linked, new FacebookTokenBundle(user, NewId(), page, NewId(), null, [linked]));

        _logs.Entries.Should().ContainSingle();
        Violations(_logs.Entries, [user, page, extra], []).Should().BeEmpty();
    }

    [Fact]
    public async Task OAuth_hata_yollari_istisna_mesajinda_token_ve_adres_tasimaz()
    {
        await _kit.SeedAsync();
        var oauth = _kit.CreateService(_logs.CreateLogger<FacebookOAuthService>(), callbackTimeout: TimeSpan.FromMilliseconds(50));
        var (pageId, name) = (NewId(), NewId());
        _kit.Graph.Map(u => u.AbsolutePath.EndsWith("/" + pageId), _ => FakeGraph.Json("null")); // boş gövde
        var errors = new List<Exception> { await ThrownBy(() => oauth.AddExtraPageAsync(pageId, name)) };
        _kit.Graph.Map(u => u.AbsolutePath.EndsWith("/" + pageId), _ => FakeGraph.Error(190)); // Graph hatası
        errors.Add(await ThrownBy(() => oauth.AddExtraPageAsync(pageId, name)));
        errors.Add(await ThrownBy(() => oauth.ReauthorizeAsync((_, _) => Task.FromResult(true)))); // zaman aşımı
        errors[2].Should().BeOfType<TimeoutException>();

        var tokens = await TokensAsync();
        errors.Should().OnlyContain(e => !e.ToString().Contains("access_token=")
            && !e.ToString().Contains("graph.facebook.com")
            && tokens.All(s => !e.ToString().Contains(s)));
        Violations(_logs.Entries, tokens, []).Should().BeEmpty();
    }
}
