using FluentAssertions;
using Grpc.Core;
using OrderDeck.Chat.Ingestors.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// YouTube okuyucusunun neden çıktığı (tasarım: Sohbeti yenile §5.4, "Veri" tablosu).
///
/// NEDEN: çıkış sebebi şeritteki satırı ve döngünün sonraki adımını belirler. Yayın bittiyse satır
/// "yayın kesildi / bitti" olur ve yayın yeniden aranır; hata ise "bağlantı koptu" olur ve döngü
/// geri çekilerek dener. İkisini karıştırmak ya biten yayında boşuna hata sayacı büyütür ya da
/// gerçek hatayı "yayın bitti" diye gizler.
/// </summary>
public class YouTubeIngestorEndTests
{
    private static YouTubeIngestorEnd Classify(Exception? error, bool offline = false, bool cancelled = false)
        => YouTubeOfficialChatIngestor.ClassifyEnd(error, offline, cancelled);

    private static RpcException Rpc(StatusCode code) => new(new Status(code, "test"));

    [Fact]
    public void Offline_marker_means_the_broadcast_ended()
    {
        Classify(error: null, offline: true).Should().Be(YouTubeIngestorEnd.BroadcastEnded);
    }

    [Fact]
    public void Failed_precondition_means_the_broadcast_ended()
    {
        // Sohbet kapatıldı ya da yayın bitti; gRPC ikisini ayırt etmez.
        Classify(Rpc(StatusCode.FailedPrecondition)).Should().Be(YouTubeIngestorEnd.BroadcastEnded);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.ResourceExhausted)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.NotFound)]
    public void Any_other_rpc_error_is_a_failure(StatusCode code)
    {
        Classify(Rpc(code)).Should().Be(YouTubeIngestorEnd.Failed);
    }

    [Fact]
    public void An_unexpected_exception_is_a_failure()
    {
        Classify(new InvalidOperationException("beklenmeyen")).Should().Be(YouTubeIngestorEnd.Failed);
    }

    [Fact]
    public void Cancellation_wins_over_whatever_the_stream_threw()
    {
        // İptal edilen gRPC çağrısı OperationCanceledException yerine RpcException(Cancelled)
        // atabilir. Yenileme ve oturum bitişi okuyucuyu iptalle keser; bu "hata" sayılırsa her
        // yenileme hata sayacını büyütür ve satır "bağlantı koptu" görünür.
        Classify(Rpc(StatusCode.Cancelled), cancelled: true).Should().Be(YouTubeIngestorEnd.Cancelled);
        Classify(new OperationCanceledException(), cancelled: true).Should().Be(YouTubeIngestorEnd.Cancelled);
        Classify(error: null, cancelled: true).Should().Be(YouTubeIngestorEnd.Cancelled);
        Classify(Rpc(StatusCode.Unavailable), offline: true, cancelled: true)
            .Should().Be(YouTubeIngestorEnd.Cancelled);
    }
}
