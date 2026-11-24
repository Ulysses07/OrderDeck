namespace OrderDeck.Core.Chat;

/// <summary>Bir sohbet kaynağının (YouTube, Facebook, TikTok) durumu.</summary>
public enum ChatSourceState
{
    /// <summary>Kaynak çalışmıyor: oturum yok, kaynak kurulu değil ya da deneme sürümü.</summary>
    Stopped,

    /// <summary>Yayın aranıyor ya da okuyucu kuruluyor; yenileme isteği alındı.</summary>
    Connecting,

    /// <summary>Platform "etkin yayın yok" dedi ya da okuyucu yayının bittiğini bildirdi. TikTok'ta:
    /// köprüye bağlı sekme yok.</summary>
    NoBroadcast,

    /// <summary>Okuyucu çalışıyor. TikTok'ta: canlı yayın sekmesi köprüye bağlı.</summary>
    Live,

    /// <summary>Okuyucu hatayla çıktı; döngü <see cref="ChatSourceHealth.RetryAt"/>'te yeniden
    /// deneyecek.</summary>
    Retrying,

    /// <summary>Operatörün düzeltmesi gereken ya da bekleyerek geçmeyecek sorun; açıklaması
    /// <see cref="ChatSourceHealth.Detail"/>'de.</summary>
    NeedsAttention,
}

/// <summary>
/// Bir kaynağın sağlık olguları: <see cref="ChatSourceMonitor.Snapshot"/> anındaki değişmez kopya.
/// Yorum taşımaz; "yayın kesildi mi bitti mi", "sarı mı soluk mu" okuyan tarafta türetilir.
/// </summary>
/// <param name="Platform">Kaynağın anahtarı, küçük harf: <c>youtube</c>, <c>tiktok</c>, <c>facebook</c>.</param>
/// <param name="State">Kaynağın durumu.</param>
/// <param name="Detail">Duruma eşlik eden, operatöre gösterilecek açıklama; yoksa null.</param>
/// <param name="Since">Kaynağın bu duruma girdiği an. Aynı durumu yineleyen bildirim bunu değiştirmez.</param>
/// <param name="RetryAt">Yeniden denemenin yapılacağı an; bilinmiyorsa null.</param>
/// <param name="WasLiveThisSession">Kaynak bu yayın oturumunda en az bir kez <see cref="ChatSourceState.Live"/>
/// oldu mu.</param>
public sealed record ChatSourceHealth(
    string Platform,
    ChatSourceState State,
    string? Detail,
    DateTimeOffset Since,
    DateTimeOffset? RetryAt,
    bool WasLiveThisSession);

/// <summary>
/// YouTube, Facebook ve TikTok sohbet kaynaklarının durum izleyicisi: servisler yazar, kabuk okur.
/// Instagram'ın kendi izleyicisi var (hesap başına girdi tutuyor); bu, tek girdili platformlar için
/// onun yalın karşılığıdır.
///
/// <para>Yalnız olgu tutar. İş parçacığı güvenlidir: servis döngüleri ve arayüz aynı anda erişir.</para>
/// </summary>
public sealed class ChatSourceMonitor
{
    /// <summary>Anlık görüntüde öne alınan platformlar, şeritteki sırayla.</summary>
    private static readonly string[] LeadingOrder = ["youtube", "tiktok", "facebook"];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ChatSourceHealth> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ChatSourceMonitor(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// Kaynağın durumunu yazar. Açıklama ve yeniden deneme saati her bildirimde yazılır (verilmeyen
    /// silinir); duruma giriş anı yalnız durum DEĞİŞİNCE şimdiye çekilir.
    /// </summary>
    public void Report(string platform, ChatSourceState state, string? detail = null, DateTimeOffset? retryAt = null)
    {
        var key = platform.ToLowerInvariant();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var previous = _entries.GetValueOrDefault(key);
            _entries[key] = new ChatSourceHealth(
                key,
                state,
                detail,
                previous is not null && previous.State == state ? previous.Since : now,
                retryAt,
                state == ChatSourceState.Live || previous is { WasLiveThisSession: true });
        }
    }

    /// <summary>
    /// Yeni yayın oturumu: "bu oturumda canlıydı" bilgisi sıfırlanır. O an canlı olan kaynak canlı
    /// sayılmayı sürdürür (TikTok sekmesi oturumdan önce açılıp bağlı kalmış olabilir).
    /// </summary>
    public void ResetSession()
    {
        lock (_gate)
        {
            foreach (var (key, entry) in _entries.ToArray())
                _entries[key] = entry with { WasLiveThisSession = entry.State == ChatSourceState.Live };
        }
    }

    /// <summary>Bildirim almış kaynakların anlık görüntüsü: önce YouTube, TikTok, Facebook; sonra
    /// öbürleri ada göre.</summary>
    public IReadOnlyList<ChatSourceHealth> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderBy(e => Array.IndexOf(LeadingOrder, e.Platform) is var index && index >= 0 ? index : int.MaxValue)
                .ThenBy(e => e.Platform, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
