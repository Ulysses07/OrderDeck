using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.Facebook;
using OrderDeck.Core.Chat;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// R11-CHAT01: yayınlanmış yorum hafızası poller'ın ÖMRÜNÜ aşmalı.
///
/// <para>Hafıza <see cref="FacebookLiveCommentsStream"/>'in alanıyken her
/// yeniden bağlanma onu sıfırlıyordu: hosted service döngüsü her turda yeni
/// bir poller kuruyor, yeni poller'ın ilk anketi
/// (<c>order=reverse_chronological&amp;limit=100</c>) son 100 yorumu getirip
/// hepsini "yeni" sayıyordu. R10-CHAT01 geçici hatada karaliste yerine AYNI
/// videoya backoff'la geri bağlanmayı getirdiği için bu yol artık çok sık
/// geziliyor — kopuk ağda her tur sohbete 100 yorumluk tekrar basıyordu.
/// "Kodu ilk yazan alır" akışında bu, sırayı yanıltan bir gürültü.</para>
/// </summary>
public class FacebookSeenCommentsTests
{
    // ---- halka semantiği -------------------------------------------------

    [Fact]
    public void Ayni_videoya_ResetFor_hafizayi_korur()
    {
        var seen = new FacebookSeenComments();
        seen.ResetFor("vid-1");
        seen.TryAdd("c-1").Should().BeTrue();

        // Yeniden bağlanma: aynı video → hatırlamak SÜRMELİ.
        seen.ResetFor("vid-1");
        seen.TryAdd("c-1").Should().BeFalse("aynı yayına geri bağlanmak tekrar yayın demek değil");
    }

    [Fact]
    public void Video_degisince_hafiza_sifirlanir()
    {
        var seen = new FacebookSeenComments();
        seen.ResetFor("vid-1");
        seen.TryAdd("c-1");

        // Başka yayının id'leri bu halkayı boşuna doldurmasın.
        seen.ResetFor("vid-2");
        seen.TryAdd("c-1").Should().BeTrue();
    }

    [Fact]
    public void Kapasite_asilinca_en_eski_id_tahliye_edilir()
    {
        var seen = new FacebookSeenComments(capacity: 2);
        seen.TryAdd("a").Should().BeTrue();
        seen.TryAdd("b").Should().BeTrue();
        seen.TryAdd("c").Should().BeTrue();

        seen.TryAdd("a").Should().BeTrue("halka sınırlı — en eski id FIFO ile düşer");
        seen.TryAdd("c").Should().BeFalse();
    }

    // ---- poller ile birlikte ---------------------------------------------

    private const string OneComment = """
        {"data":[{"id":"c-1","message":"kod 42",
        "created_time":"2026-09-16T12:00:00+0000",
        "from":{"id":"u-1","name":"Ayse"}}]}
        """;

    /// <summary>
    /// Hosted service'in bir döngü turunu taklit eder: önce
    /// <see cref="FacebookSeenComments.ResetFor"/>, sonra YENİ bir poller.
    /// İlk anket tek yorumu döner, ikinci anket <c>code:100</c> döner →
    /// döngü deterministik biter, yani Completion çözüldüğünde ilk anketin
    /// yayınları tamamlanmıştır.
    /// </summary>
    private static async Task PollOnceAsync(
        IChatBus bus, string videoId, FacebookSeenComments? seen)
    {
        seen?.ResetFor(videoId);

        int calls = 0;
        var handler = new FakeHttpMessageHandler(_ =>
            Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(OneComment),
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"code":100}}"""),
                });

        using var stream = new FacebookLiveCommentsStream(
            videoId, "tok", bus, new HttpClient(handler),
            NullLogger<FacebookLiveCommentsStream>.Instance,
            spamFilter: null, seen: seen);

        await stream.StartAsync(CancellationToken.None);
        await stream.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Yeniden_baglanan_poller_ayni_yorumu_tekrar_yayimlamaz()
    {
        var bus = new ChatBus(ringBufferSize: 16);
        var seen = new FacebookSeenComments();

        await PollOnceAsync(bus, "vid-1", seen);
        // Geçici hata sonrası AYNI videoya geri bağlanma (R10-CHAT01 backoff).
        await PollOnceAsync(bus, "vid-1", seen);

        bus.RecentMessages().Should().HaveCount(1,
            "hafıza poller'ı aştığı için ikinci turun anketi tamamen tekrar");
    }

    [Fact]
    public async Task Yeni_yayina_gecince_poller_hafizayi_sifirlar()
    {
        // Kontrol: bariyer video kimliğine bağlı. Farklı yayının aynı id'li
        // yorumu susturulmamalı (ve eski yayının id'leri halkayı doldurmamalı).
        var bus = new ChatBus(ringBufferSize: 16);
        var seen = new FacebookSeenComments();

        await PollOnceAsync(bus, "vid-1", seen);
        await PollOnceAsync(bus, "vid-2", seen);

        bus.RecentMessages().Should().HaveCount(2);
    }

    [Fact]
    public async Task Hafiza_verilmezse_yeniden_baglanma_tekrar_yayimlar()
    {
        // Hatanın kendisi: her poller kendi halkasını açarsa ikinci tur
        // aynı yorumu bir daha basar. Yukarıdaki testin gerçekten hafızayı
        // ölçtüğünü kanıtlayan kontrol.
        var bus = new ChatBus(ringBufferSize: 16);

        await PollOnceAsync(bus, "vid-1", seen: null);
        await PollOnceAsync(bus, "vid-1", seen: null);

        bus.RecentMessages().Should().HaveCount(2);
    }
}
