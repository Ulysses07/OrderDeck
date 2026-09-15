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
/// R10-CHAT01: poller'ın çıkış SEBEBİNİ doğru bildirmesi. Hosted service'in
/// karaliste kararı bu sebebe dayanıyor — code:100 dışındaki her çıkış
/// "yayın bitti" sayılırsa, 5 geçici ağ hatası canlı yayını 2 dakika
/// erişilmez kılar (ClassifyStreamExit testleriyle birlikte okunmalı).
/// </summary>
public class FacebookLiveCommentsStreamEndReasonTests
{
    private static FacebookLiveCommentsStream NewStream(
        FakeHttpMessageHandler handler) =>
        new("vid-1", "tok", new ChatBus(ringBufferSize: 8),
            new HttpClient(handler),
            NullLogger<FacebookLiveCommentsStream>.Instance);

    [Fact]
    public async Task Code100_cikisi_BroadcastEnded_bildirir()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":{"message":"Object does not exist","code":100}}"""),
            });
        using var stream = NewStream(handler);

        await stream.StartAsync(CancellationToken.None);
        await stream.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        stream.EndReason.Should().Be(FacebookStreamEndReason.BroadcastEnded,
            "code:100 tek kanıtlı bitiş — yalnız bu çıkışta karaliste doğru");
    }

    [Fact]
    public async Task Ardisik_gecici_hatalar_TransientFailure_bildirir()
    {
        // 5 ardışık 500 → hata limiti. Eski davranışta bu çıkış da "yayın
        // bitti" gibi karalisteye gidiyordu; artık sebep ayrışıyor.
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":{"code":1}}"""),
            });
        using var stream = NewStream(handler);

        await stream.StartAsync(CancellationToken.None);
        await stream.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        stream.EndReason.Should().Be(FacebookStreamEndReason.TransientFailure,
            "hata limiti 'pes ettim' demek, 'yayın bitti' demek değil");
    }

    [Fact]
    public async Task Operator_durdurunca_Cancelled_bildirir()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}"""),
            });
        using var stream = NewStream(handler);

        await stream.StartAsync(CancellationToken.None);
        await stream.StopAsync(CancellationToken.None);
        await stream.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        stream.EndReason.Should().Be(FacebookStreamEndReason.Cancelled);
    }
}
