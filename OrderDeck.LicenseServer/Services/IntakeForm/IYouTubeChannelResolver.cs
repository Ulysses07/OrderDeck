namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <param name="Available">
/// API'ye ulaşılabildi mi. false ise sonuç hakkında HİÇBİR ŞEY bilmiyoruz
/// (key yok, kota bitti, ağ düştü) — çağıran bunu müşteriyi engellemek için
/// KULLANMAMALI, yoksa bizim arızamız müşteriye fatura edilmiş olur.
/// </param>
/// <param name="Exists">Handle gerçekten bir kanala karşılık geliyor mu.</param>
public sealed record YouTubeChannel(
    bool Available, bool Exists, string? Title, string? Thumbnail, string? ChannelId);

public interface IYouTubeChannelResolver
{
    Task<YouTubeChannel> ResolveHandleAsync(string? handle, CancellationToken ct);

    /// <summary>
    /// Kanal kimliğinden (<c>youtube.com/channel/UC…</c> adresinden çıkarılmış
    /// <c>UC…</c> değeri) kanalı çözer. Handle yolundan tek farkı sorgunun
    /// <c>forHandle</c> yerine <c>id</c> ile yapılması; kart/onay akışı aynıdır.
    ///
    /// DİKKAT: dönen <see cref="YouTubeChannel.ChannelId"/> girdinin yankısı
    /// DEĞİLDİR — API'nin döndürdüğü değerdir. Çağıran taraf kaydettiği kimliği
    /// buradan almalı, elindeki girdiden değil.
    /// </summary>
    Task<YouTubeChannel> ResolveChannelIdAsync(string? channelId, CancellationToken ct);
}
