using OrderDeck.Core.Chat;

namespace OrderDeck.App.Services.Chat;

/// <summary>Sohbet durum şeridindeki bir satırın noktasının rengi.</summary>
public enum ChatStatusSeverity
{
    /// <summary>Yeşil: kaynak bağlı.</summary>
    Live,

    /// <summary>Soluk: bilgi; uyarı değil.</summary>
    Neutral,

    /// <summary>Sarı: uyarı.</summary>
    Warning,
}

/// <summary>
/// Sohbet panelindeki durum şeridinin bir satırı, gösterime hazır: bir platform (YouTube, TikTok, Facebook) ya
/// da bir Instagram hesabı. Kayıt değişmezdir ve değer eşitliği taşır: iki değerlendirmenin satırları
/// karşılaştırılabilir (eşit satıra dokunulmaz, açık ipucu kapanmaz).
/// </summary>
/// <param name="Key">Satırın kimliği: platform adı ya da <c>instagram:{PageId}</c>.</param>
/// <param name="GroupLabel">Satırın önüne yazılan grup etiketi ("Instagram", yalnız ilk Instagram satırında);
/// yoksa null. Şerit alt satıra inerken etiket satırından ayrılmasın diye satırın parçasıdır.</param>
/// <param name="DisplayText">Şeritte görünen metin. Sağlıklı kaynakta yalnız addır; durum metni yalnız
/// söylenecek bir şey varken eklenir.</param>
/// <param name="Severity">Noktanın rengi.</param>
/// <param name="Tooltip">Satırın ipucu; null ise ipucu yok.</param>
/// <param name="IsProblem">Satır bir sorun durumunda mı (rengi soluk olsa da). Yenileme, sorun durumunda satır
/// kalmışsa onay işareti göstermez.</param>
/// <param name="IsConnecting">Kaynak o an bağlanmayı deniyor mu ("bağlanıyor"). Yenileme, bağlanan satır
/// kalmayınca biter.</param>
public sealed record ChatStatusRow(
    string Key,
    string? GroupLabel,
    string DisplayText,
    ChatStatusSeverity Severity,
    string? Tooltip,
    bool IsProblem,
    bool IsConnecting);

/// <summary>Platform kaynaklarının (YouTube, TikTok, Facebook) şeritteki görünümü.</summary>
/// <param name="Rows">Satırlar, anlık görüntü sırasıyla. Şeride girmeyen kaynağın satırı yoktur.</param>
/// <param name="AnyLive">Bu platformlardan en az biri bağlı mı.</param>
/// <param name="TopDotTooltip">Üst bardaki sohbet noktasını sarartan durumların cümleleri; yoksa null.</param>
public sealed record ChatSourceView(IReadOnlyList<ChatStatusRow> Rows, bool AnyLive, string? TopDotTooltip);

/// <summary>
/// Platform kaynaklarının sağlık olgularını (<see cref="ChatSourceMonitor.Snapshot"/>) şeridin gösterdiğine
/// çevirir (tasarım: Sohbeti yenile §5.4). Instagram hesaplarının karşılığı
/// <see cref="Instagram.InstagramHealthPresenter"/>'dır; ikisi aynı kuralı paylaşır: bu oturumda bağlıyken düşen
/// kaynak, başka bir kaynak bağlıyken "yayın kesildi" (sarı), kimse bağlı değilken "yayın bitti" (soluk) olur.
///
/// <para><b>Saf ve durumsuz.</b> Saat okumaz (an parametredir), log yazmaz, bir şey saklamaz.</para>
///
/// <para><b>"Yayın bekleniyor" sararmaz.</b> Bu oturumda hiç bağlanmamış kaynak, başka kaynak bağlı olsa da
/// uyarı vermez: o gece o platformda yayın yapmayan yayıncı sarı satır görmez.</para>
/// </summary>
public static class ChatSourcePresenter
{
    /// <summary>"Yayın kesildi" üst noktayı yalnız bu kadar süre sarartır; sonra yalnız şeritte sarı kalır.</summary>
    private static readonly TimeSpan CutTopDotFor = TimeSpan.FromMinutes(2);

    private const string RetryingTooltip =
        "Bağlantı koptu; kendiliğinden yeniden denenecek. Beklemeden denemek için Sohbeti yenile'ye bas.";

    private const string TikTokLiveTooltip = "TikTok canlı yayın sekmesi bağlı.";

    private const string TikTokMissingTooltip =
        "TikTok canlı yayın sekmesi kapalı ya da uzantı bağlı değil. Sekmeyi aç ya da yenile.";

    /// <summary>Anlık görüntüyü görünüme çevirir.</summary>
    /// <param name="snapshot">İzleyicinin anlık görüntüsü.</param>
    /// <param name="now">Değerlendirme anı. Ofseti, metinlerdeki saatin yazılacağı saat dilimidir.</param>
    /// <param name="otherSourceLive">Bu anlık görüntünün dışında bağlı bir kaynak var mı (canlı bir Instagram
    /// hesabı).</param>
    public static ChatSourceView Evaluate(
        IReadOnlyList<ChatSourceHealth> snapshot, DateTimeOffset now, bool otherSourceLive)
    {
        var rows = new List<ChatStatusRow>(snapshot.Count);
        var topDot = new List<string>();
        foreach (var entry in snapshot)
        {
            var othersLive = otherSourceLive
                             || snapshot.Any(other => !ReferenceEquals(other, entry) && other.State == ChatSourceState.Live);
            if (Describe(entry, now, othersLive) is not { } line) continue;

            var name = Name(entry.Platform);
            rows.Add(new ChatStatusRow(
                entry.Platform,
                GroupLabel: null,
                line.StateText.Length == 0 ? name : $"{name} {line.StateText}",
                line.Severity,
                line.Tooltip,
                line.IsProblem,
                line.IsConnecting));
            if (line.Severity == ChatStatusSeverity.Warning && !line.StripOnly)
                topDot.Add($"{name}: {line.StateText}.");
        }

        return new ChatSourceView(
            rows,
            AnyLive: snapshot.Any(entry => entry.State == ChatSourceState.Live),
            topDot.Count == 0 ? null : string.Join(" ", topDot));
    }

    /// <summary>Bir satırın adsız hâli; adı ve tam metni <see cref="Evaluate"/> ekler.</summary>
    /// <param name="StateText">Durum metni; boşsa satır yalnız adını yazar.</param>
    /// <param name="StripOnly">Satır sarı olsa da üst noktayı sarartmaz.</param>
    private readonly record struct Line(
        string StateText,
        ChatStatusSeverity Severity,
        string? Tooltip,
        bool IsProblem = false,
        bool IsConnecting = false,
        bool StripOnly = false);

    /// <summary>Kaynağın satırı; kaynak şeride girmiyorsa null.</summary>
    private static Line? Describe(ChatSourceHealth entry, DateTimeOffset now, bool othersLive)
    {
        var name = Name(entry.Platform);
        var isTikTok = entry.Platform == "tiktok";

        switch (entry.State)
        {
            case ChatSourceState.Connecting:
                return new Line("bağlanıyor", ChatStatusSeverity.Neutral, Tooltip: null, IsConnecting: true);

            case ChatSourceState.Live:
                return new Line(
                    "", ChatStatusSeverity.Live,
                    isTikTok
                        ? TikTokLiveTooltip
                        : $"{name} sohbeti bağlı ({StatusClock.ClockWithAblative(entry.Since, now)} beri).");

            case ChatSourceState.NoBroadcast when isTikTok:
                // Bu oturumda hiç sekme bağlanmadıysa TikTok kullanılmıyordur: satır yok.
                if (!entry.WasLiveThisSession) return null;
                return othersLive
                    ? new Line(
                        "sekme bağlı değil", ChatStatusSeverity.Warning, TikTokMissingTooltip, IsProblem: true,
                        StripOnly: now - entry.Since > CutTopDotFor)
                    : new Line("sekme bağlı değil", ChatStatusSeverity.Neutral, TikTokMissingTooltip);

            case ChatSourceState.NoBroadcast:
                if (!entry.WasLiveThisSession)
                {
                    return new Line(
                        "yayın bekleniyor", ChatStatusSeverity.Neutral,
                        $"{Locative(entry.Platform)} canlı yayın açılınca yorumlar akmaya başlar.");
                }

                // Yalnız başka bir kaynak hâlâ bağlıyken uyarıdır; bağlı kaynak kalmadıysa yayın bitmiştir.
                var endedAt = StatusClock.Clock(entry.Since, now);
                return othersLive
                    ? new Line(
                        $"yayın kesildi ({endedAt})", ChatStatusSeverity.Warning, CutTooltip(entry.Platform),
                        IsProblem: true, StripOnly: now - entry.Since > CutTopDotFor)
                    : new Line($"yayın bitti ({endedAt})", ChatStatusSeverity.Neutral, Tooltip: null);

            case ChatSourceState.Retrying:
                return new Line(
                    "bağlantı koptu" + (entry.RetryAt is { } retryAt
                        ? $", {StatusClock.ClockWithLocative(retryAt, now)} dener"
                        : ""),
                    entry.WasLiveThisSession ? ChatStatusSeverity.Warning : ChatStatusSeverity.Neutral,
                    RetryingTooltip, IsProblem: true);

            case ChatSourceState.NeedsAttention:
                return new Line(
                    "bağlantı kurulamıyor", ChatStatusSeverity.Warning,
                    string.IsNullOrWhiteSpace(entry.Detail) ? $"{name} sohbetine bağlanılamıyor." : entry.Detail,
                    IsProblem: true);

            default:
                return null;   // Stopped: şeride girmez
        }
    }

    /// <summary>Platformun görünen adı; bilinmeyen platformda anahtarın kendisi.</summary>
    private static string Name(string platform) => platform switch
    {
        "youtube" => "YouTube",
        "tiktok" => "TikTok",
        "facebook" => "Facebook",
        _ => platform,
    };

    /// <summary>Platform adının bulunma hâli: "YouTube'da", "Facebook'ta".</summary>
    private static string Locative(string platform) => platform switch
    {
        "youtube" => "YouTube'da",
        "facebook" => "Facebook'ta",
        _ => Name(platform) + "'da",
    };

    /// <summary>"Yayın kesildi" ipucu. Facebook kendi toparlar; YouTube'da düğme gerekir.</summary>
    private static string CutTooltip(string platform)
    {
        var name = Name(platform);
        return platform == "facebook"
            ? $"{name} yayını kapandı; yorumları gelmiyor. Yayını yeniden açtıysan birkaç saniye içinde kendiliğinden bağlanır."
            : $"{name} yayını kapandı; yorumları gelmiyor. Yayını yeniden açtıysan Sohbeti yenile'ye bas.";
    }
}
