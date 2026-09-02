namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <param name="Available">
/// API'ye ulaşılabildi mi. false ise sonuç hakkında HİÇBİR ŞEY bilmiyoruz
/// (key yok, kota bitti, ağ düştü) — çağıran bunu müşteriyi engellemek için
/// KULLANMAMALI, yoksa bizim arızamız müşteriye fatura edilmiş olur.
/// </param>
/// <param name="Exists">Handle gerçekten bir kanala karşılık geliyor mu.</param>
/// <param name="Handle">
/// Kanalın <c>@handle</c>'ı (API'de <c>snippet.customUrl</c>). Kanal ADRESİ
/// yapıştıran müşteride kullanıcı adı kutusu boş kalır; bu değer olmadan kayıt
/// yalnız <c>UC…</c> ile açılır ve yayıncı müşteri listesinde çıplak kimlik
/// görür (WPF tarafı handle'ı DisplayName olarak taşıyor, taşıyacak handle
/// yoksa taşıyamıyor). Aynı yanıtta zaten geliyor — ek kota yok. Her kanalda
/// bulunmayabilir (handle almamış eski kanallar), bu yüzden nullable.
/// </param>
public sealed record YouTubeChannel(
    bool Available, bool Exists, string? Title, string? Thumbnail, string? ChannelId,
    string? Handle = null);

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
