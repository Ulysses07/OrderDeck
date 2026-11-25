using FluentAssertions;
using OrderDeck.Chat.Ingestors.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// YouTube'un "gösterilmiş mesaj" belleği (tasarım: Sohbeti yenile §5.6).
///
/// NEDEN: YouTube akışı her yeni bağlantıda yakın geçmişi yeniden gönderir. Bellek okuyucunun
/// içindeyken okuyucu her yeniden kurulduğunda (yenileme, yayını bitirip açma) sıfırlanıyor ve aynı
/// mesajlar sohbete ikinci kez düşüyordu; mezat modelinde ikinci kez düşen yorum ikinci sipariştir.
/// Bellek artık servis ömrünce yaşayan ayrı bir nesne.
/// </summary>
public class YouTubeSeenMessagesTests
{
    [Fact]
    public void First_sighting_is_new_and_the_second_is_not()
    {
        var seen = new YouTubeSeenMessages();
        var id = NewId();

        seen.TryAdd(id).Should().BeTrue();
        seen.TryAdd(id).Should().BeFalse("aynı mesaj ikinci kez yayımlanmamalı");
        seen.TryAdd(NewId()).Should().BeTrue("başka mesaj etkilenmez");
    }

    [Fact]
    public void Memory_forgets_the_oldest_beyond_5000()
    {
        var seen = new YouTubeSeenMessages();
        var ids = Enumerable.Range(0, 5001).Select(_ => NewId()).ToArray();
        foreach (var id in ids) seen.TryAdd(id).Should().BeTrue();

        // Sınır 5000: 5001. kimlik en eskiyi iter; sondan ikinci hâlâ bellekte.
        seen.TryAdd(ids[^2]).Should().BeFalse();
        seen.TryAdd(ids[0]).Should().BeTrue("en eski kimlik unutulmuş olmalı; bellek sınırsız büyümez");
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
