using System.Net;
using System.Net.Http;
using FluentAssertions;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Requests;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.YouTube;
using OrderDeck.Chat.YouTube;
using OrderDeck.Core.Settings;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// Error-mapping coverage for <see cref="YouTubeModerationService.MapException"/>.
/// We exercise the function directly (made <c>internal</c> + <c>InternalsVisibleTo</c>)
/// instead of standing up the full Google.Apis client — the goal is to lock
/// down the user-facing Turkish messages so a refactor of the helper
/// can't silently change what the operator sees.
/// </summary>
public class YouTubeModerationServiceTests
{
    private static GoogleApiException FakeApiException(HttpStatusCode status, string message)
    {
        // GoogleApiException's public constructor takes (string serviceName, string message);
        // the HTTP status is set via property assignment.
        return new GoogleApiException("youtube", message)
        {
            HttpStatusCode = status,
        };
    }

    [Fact]
    public void MapException_401_returns_session_expired_message()
    {
        var ex = FakeApiException(HttpStatusCode.Unauthorized, "auth bad");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Should().BeOfType<ModerationException>();
        mapped.Message.Should().Contain("oturum");
        mapped.Message.Should().Contain("tekrar bağlan");
        mapped.InnerException.Should().Be(ex);
    }

    [Fact]
    public void MapException_403_returns_permission_message()
    {
        var ex = FakeApiException(HttpStatusCode.Forbidden, "no scope");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Message.Should().Contain("yetki");
        mapped.Message.Should().Contain("moderatör");
    }

    [Fact]
    public void MapException_404_returns_not_found_message()
    {
        var ex = FakeApiException(HttpStatusCode.NotFound, "no such msg");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Message.Should().Contain("bulunamadı");
    }

    [Fact]
    public void MapException_429_returns_quota_message()
    {
        var ex = FakeApiException((HttpStatusCode)429, "rate limited");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Message.Should().Contain("kota");
        mapped.Message.Should().Contain("Yarın");
    }

    [Fact]
    public void MapException_unknown_status_includes_underlying_message()
    {
        var ex = FakeApiException(HttpStatusCode.InternalServerError, "upstream timeout");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Message.Should().Contain("YouTube API hatası");
        mapped.Message.Should().Contain("upstream timeout");
    }

    [Fact]
    public void ModerationException_constructor_with_message_only_has_no_inner()
    {
        var ex = new ModerationException("custom failure");

        ex.Message.Should().Be("custom failure");
        ex.InnerException.Should().BeNull();
    }

    // ── Sohbet döngüsünün yayın araması (tasarım: Sohbeti yenile §5.2, §5.4) ──
    //
    // NEDEN: sohbet döngüsü üç sonucu ayırt etmeli: etkin yayın yok (hızlı aranır), operatörün
    // düzeltmesi gereken sorun (şeritte sebebiyle durur, hızlı aranmaz), geçici hata (yeniden
    // denenir). Eskiden ilk ikisi aynı istisna türüydü, hesabın bağlı olmaması ise istisna bile
    // değildi: döngü Google izin sayfasını açıp orada bekliyordu. İpuçları bilerek literal.

    private const string SessionExpired = "YouTube oturumunun süresi doldu. Ayarlar > YouTube'dan yeniden bağlan.";
    private const string NoPermission =
        "Bu YouTube hesabının kanalın canlı yayınını okuma yetkisi yok. Ayarlar > YouTube'dan doğru hesapla bağlan.";
    private const string QuotaExhausted = "Günlük YouTube API kotası doldu; sohbet yarına kadar okunamayabilir.";

    [Theory]
    [InlineData(401, null, BroadcastLookupStatus.NeedsAttention, SessionExpired)]
    [InlineData(403, "insufficientLivePermissions", BroadcastLookupStatus.NeedsAttention, NoPermission)]
    [InlineData(403, null, BroadcastLookupStatus.NeedsAttention, NoPermission)]
    // Kota aşımı 429 değil 403 gelir (resmî hata tablosu, 24.11.2026): sebebe bakılmazsa
    // operatöre "yanlış hesap" denir ve boş yere hesap değiştirir.
    [InlineData(403, "quotaExceeded", BroadcastLookupStatus.NeedsAttention, QuotaExhausted)]
    [InlineData(403, "dailyLimitExceeded", BroadcastLookupStatus.NeedsAttention, QuotaExhausted)]
    // Geçici hız sınırı bekleyince geçer: "kota doldu, yarına kadar" demek yanlış olurdu.
    [InlineData(403, "rateLimitExceeded", BroadcastLookupStatus.Failed, null)]
    [InlineData(429, null, BroadcastLookupStatus.Failed, null)]
    [InlineData(500, null, BroadcastLookupStatus.Failed, null)]
    public void ClassifyFailure_maps_google_errors(
        int status, string? reason, BroadcastLookupStatus expected, string? message)
    {
        var ex = FakeApiException((HttpStatusCode)status, "upstream says no");
        if (reason is not null)
            ex.Error = new RequestError { Errors = [new SingleError { Reason = reason }] };

        YouTubeModerationService.ClassifyFailure(ex)
            .Should().Be(new BroadcastLookup(expected, Message: message));
    }

    [Fact]
    public void ClassifyFailure_treats_a_revoked_grant_as_needs_attention()
    {
        // Yetki geri alınmışsa erişim anahtarı yenilenemez; beklemek düzeltmez.
        var ex = new TokenResponseException(new TokenErrorResponse { Error = "invalid_grant" });

        YouTubeModerationService.ClassifyFailure(ex)
            .Should().Be(new BroadcastLookup(BroadcastLookupStatus.NeedsAttention, Message: SessionExpired));
    }

    [Fact]
    public void ClassifyFailure_treats_a_network_error_as_failed()
    {
        // Geçici hatanın metni şeride çıkmaz (günlüğe yazılır): Message boş.
        YouTubeModerationService.ClassifyFailure(new HttpRequestException("ağ yok"))
            .Should().Be(new BroadcastLookup(BroadcastLookupStatus.Failed));
    }

    [Fact]
    public async Task Lookup_without_a_stored_grant_reports_not_connected_without_starting_a_sign_in()
    {
        // Diskte yetki yok. Arama "bağlı mı" diye tarayıcı açmadan sormalı ve orada durmalı;
        // yetkili servis istemeye kalkarsa Google izin akışı başlar (bu kurulumda istemci
        // kimliği olmadığı için istisnayla) ve sonuç NotConnected olmaz.
        var store = new EmptyTokenStore();
        var oauth = new YouTubeOAuthService(
            () => new AppSettings(), store, NullLogger<YouTubeOAuthService>.Instance);
        var service = new YouTubeModerationService(oauth, NullLogger<YouTubeModerationService>.Instance);

        var result = await service.LookupActiveBroadcastAsync(fresh: true);

        result.Should().Be(new BroadcastLookup(
            BroadcastLookupStatus.NotConnected,
            Message: "YouTube hesabı bağlı değil. Ayarlar > YouTube'dan bağla."));
        store.Writes.Should().Be(0);
    }

    /// <summary>Hiç yetki tutmayan depo: her okuma boş döner.</summary>
    private sealed class EmptyTokenStore : IDataStore
    {
        public int Writes { get; private set; }

        public Task StoreAsync<T>(string key, T value)
        {
            Writes++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync<T>(string key) => Task.CompletedTask;

        public Task<T> GetAsync<T>(string key) => Task.FromResult(default(T)!);

        public Task ClearAsync() => Task.CompletedTask;
    }
}
