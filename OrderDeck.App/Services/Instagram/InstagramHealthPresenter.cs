using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrderDeck.App.ViewModels;
using OrderDeck.Chat.Ingestors.Instagram;

namespace OrderDeck.App.Services.Instagram;

/// <summary>Sağlık şeridindeki bir satırın noktasının rengi.</summary>
public enum InstagramStatusSeverity
{
    /// <summary>Yeşil: hesap canlı.</summary>
    Live,

    /// <summary>Soluk: bilgi; uyarı değil.</summary>
    Neutral,

    /// <summary>Sarı: uyarı.</summary>
    Warning,
}

/// <summary>
/// Bağlı Instagram hesaplarının ana ekrandaki görünümü (<see cref="InstagramHealthPresenter.Evaluate"/> sonucu).
/// </summary>
/// <param name="Rows">Sağlık şeridinin satırları, anlık görüntü sırasıyla. Şeride girmeyen girdinin satırı
/// yoktur.</param>
/// <param name="ShowStrip">Şerit görünsün mü.</param>
/// <param name="AnyAccountLive">En az bir hesap canlı mı (durumu canlı ve yanıtsız değil).</param>
/// <param name="TopDotTooltip">Üst bardaki sohbet noktasını sarartan Instagram durumunun cümlesi; noktayı sarartan
/// durum yoksa null.</param>
public sealed record InstagramHealthView(
    IReadOnlyList<InstagramAccountStatusViewModel> Rows,
    bool ShowStrip,
    bool AnyAccountLive,
    string? TopDotTooltip);

/// <summary>
/// Instagram hesaplarının sağlık olgularını (<see cref="InstagramHealthMonitor.Snapshot"/>) ana ekranın gösterdiğine
/// çevirir: şerit satırları, şeridin görünürlüğü, "en az bir hesap canlı" bilgisi ve üst nokta ipucu (çoklu hesap
/// tasarımı §8.5 "Görünüm kuralları", §8.6.3, §8.6.4).
///
/// <para><b>Saf ve durumsuz.</b> Saat okumaz (an parametredir), log yazmaz, bir şey saklamaz. İzleyici yalnız olgu
/// tutar; "yanıt yok", "yayın kesildi", "canlı yayın bulunamadı" gibi yorumlar okuma anında burada türetilir. Böylece
/// yoklama döngüsü asılı kalsa bile (yeni olgu yazılmasa da) uyarı çıkar.</para>
///
/// <para><b>"Canlı"</b> = durumu <see cref="InstagramAccountState.Live"/> ve yanıtsız değil. Öbür hesapların
/// satırlarını sarartan kurallar ile <see cref="InstagramHealthView.AnyAccountLive"/> bu tanıma bakar; durumu canlı
/// kalıp susmuş hesap canlı sayılmaz (yoksa uzun bir kesintide kenar çubuğu yeşil kalıp sarı şeritle çelişirdi).</para>
///
/// <para><b>Bağlı Sayfa sayısı</b> anlık görüntüdeki girdi sayısıdır, durumdan bağımsız. Tek Sayfada bir sorun durumu
/// yalnız hesap bu oturumda canlıydıysa sarıdır: Instagram'ı hiç kullanmayan ya da o gece Instagram'da yayın yapmayan
/// yayıncı hiçbir şey görmez. İki ve üzeri Sayfada sorun durumları hep sarıdır: iki hesap bağlayan ikisini de
/// kullanmak istiyordur, ikisi birden oturum başından bozuksa da uyarı çıkmalıdır.</para>
///
/// <para><b>Eşikler</b> aşağıdaki beş sabittir; hepsinde aşım kesin büyüktür (eşiğin kendisi aşım değildir).</para>
/// </summary>
public static class InstagramHealthPresenter
{
    /// <summary>Canlı hesap bundan uzun süre sonuç vermezse "yanıt yok" sayılır.</summary>
    private static readonly TimeSpan LiveSilence = TimeSpan.FromSeconds(10);

    /// <summary>Yayın bekleyen, yayını bitmiş ya da bağlanan hesap bundan uzun süre sonuç vermezse "yanıt yok"
    /// sayılır.</summary>
    private static readonly TimeSpan IdleSilence = TimeSpan.FromSeconds(45);

    /// <summary>Yeniden deneme saatini bekleyen hesap, o saat bundan fazla geçildiyse "yanıt yok" sayılır.</summary>
    private static readonly TimeSpan RetryOverdue = TimeSpan.FromSeconds(45);

    /// <summary>Başka bir hesap canlıyken yayını bundan uzun süredir görünmeyen hesap "canlı yayın bulunamadı"
    /// olur.</summary>
    private static readonly TimeSpan MissingBroadcastAfter = TimeSpan.FromSeconds(90);

    /// <summary>"Yayın kesildi" üst noktayı yalnız bu kadar süre sarartır; sonra yalnız şeritte sarı kalır.</summary>
    private static readonly TimeSpan EndedTopDotFor = TimeSpan.FromMinutes(2);

    private const string ReauthorizeHint = "Ayarlar > Facebook > \"Facebook izinlerini güncelle\".";

    private const string UnresponsiveTooltip =
        "Instagram'dan yanıt alınamıyor, yeniden deneniyor. Yorumlar gecikiyor olabilir.";

    private const string WaitingTooltip = "Bu hesapta canlı yayın açılınca yorumlar akmaya başlar.";

    private const string MissingBroadcastTooltip =
        "Diğer hesap yayında ama bu hesapta canlı yayın görünmüyor. Bu hesapta yayını açtın mı?";

    private const string BroadcastCutTooltip = "Bu hesabın yayını kapandı; yorumları gelmiyor. Diğer hesap yayında.";

    private const string RateLimitedTooltip =
        "Instagram istek sınırına takıldı. Bu hesabın yorumları o saate kadar gelmeyecek.";

    private const string ResolveFailedTooltip = "Sayfaya bağlı Instagram hesabı okunamadı. Sürerse: " + ReauthorizeHint;

    private const string PrimaryNotLinkedTooltip =
        "Birincil Sayfaya bağlı Instagram hesabı görünmüyor. Sayfa ile Instagram hesabının bağını kontrol et; " +
        "bağ duruyorsa Ayarlar > Facebook > \"Facebook izinlerini güncelle\"ye basıp tüm hesapları işaretle.";

    private const string ExtraNotLinkedTooltip =
        "Bu Sayfaya bir Instagram profesyonel hesabı bağla ya da Sayfayı Ayarlar'dan kaldır.";

    /// <summary>Anlık görüntüyü görünüme çevirir.</summary>
    /// <param name="snapshot">İzleyicinin anlık görüntüsü: bağlı her Sayfa için bir girdi.</param>
    /// <param name="now">Değerlendirme anı. Ofseti, metinlerdeki saatin (SS:DD) yazılacağı saat dilimidir: çağıran
    /// yerel saatli anı verir.</param>
    /// <param name="sessionActive">Yayın oturumu açık mı. Yalnız <see cref="InstagramHealthView.ShowStrip"/>'i
    /// kapılar; satırlar, canlılık bilgisi ve üst nokta ipucu oturumdan bağımsız hesaplanır.</param>
    public static InstagramHealthView Evaluate(
        IReadOnlyList<InstagramAccountHealth> snapshot, DateTimeOffset now, bool sessionActive)
    {
        // 1. geçiş: her girdinin yanıtsızlığı ve canlı küme. "Canlı yayın bulunamadı" ve "yayın kesildi" ÖBÜR
        // hesapların canlılığına baktığı için bu bilgi satırlardan önce hazır olmalıdır.
        var unresponsiveFor = new TimeSpan?[snapshot.Count];
        DateTimeOffset? liveSince = null;   // canlı hesaplardan en erken canlı olanın canlı olduğu an
        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            unresponsiveFor[i] = UnresponsiveFor(entry, now);
            var live = entry.State == InstagramAccountState.Live && unresponsiveFor[i] is null;
            if (live && (liveSince is null || entry.Since < liveSince)) liveSince = entry.Since;
        }

        // 2. geçiş: girdiler anlık görüntü sırasıyla satıra çevrilir.
        var rows = new List<InstagramAccountStatusViewModel>(snapshot.Count);
        var raisingTopDot = new List<InstagramAccountStatusViewModel>();
        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            if (Describe(entry, now, snapshot.Count, unresponsiveFor[i], liveSince) is not { } line) continue;

            var label = line.ByPageName || string.IsNullOrWhiteSpace(entry.Username)
                ? entry.PageName
                : "@" + entry.Username;
            var text = line.ByPageName ? $"{label}: {line.StateText}" : $"{label} {line.StateText}";
            var row = new InstagramAccountStatusViewModel(
                entry.PageId, label, line.StateText, text, line.Severity, line.Tooltip);
            rows.Add(row);
            if (line.Severity == InstagramStatusSeverity.Warning && !line.StripOnly) raisingTopDot.Add(row);
        }

        var showStrip = sessionActive
            && (rows.Count >= 2 || rows.Any(r => r.Severity == InstagramStatusSeverity.Warning));
        var topDotTooltip = raisingTopDot.Count switch
        {
            0 => null,
            1 => $"Instagram {raisingTopDot[0].Label}: {raisingTopDot[0].StateText}.",
            _ => $"Instagram: {raisingTopDot.Count} hesapta sorun " +
                 $"({string.Join(", ", raisingTopDot.Select(r => r.Label))}).",
        };
        return new InstagramHealthView(rows, showStrip, AnyAccountLive: liveSince is not null, topDotTooltip);
    }

    /// <summary>
    /// Bir satırın etiketsiz hâli; etiketi ve tam metni <see cref="Evaluate"/> ekler.
    /// </summary>
    /// <param name="StateText">Etiketsiz durum metni.</param>
    /// <param name="Severity">Satırın rengi.</param>
    /// <param name="Tooltip">Satırın ipucu; yoksa null.</param>
    /// <param name="ByPageName">Satır, kullanıcı adı bilinse de Sayfa adıyla ve "{Sayfa adı}: …" biçiminde
    /// yazılır.</param>
    /// <param name="StripOnly">Satır sarı olsa da üst noktayı sarartmaz.</param>
    private readonly record struct Line(
        string StateText,
        InstagramStatusSeverity Severity,
        string? Tooltip,
        bool ByPageName = false,
        bool StripOnly = false);

    /// <summary>Girdinin satırı; girdi şeride girmiyorsa null.</summary>
    private static Line? Describe(
        InstagramAccountHealth entry, DateTimeOffset now, int pageCount, TimeSpan? unresponsiveFor,
        DateTimeOffset? liveSince)
    {
        // Yalnız Facebook kullanan tek Sayfalı kurulum (Sayfasında Instagram hesabı yok) hiçbir şey görmez. Gizleme
        // "yanıt yok" türetmesinden ÖNCE uygulanır: yeniden deneme saati geçince satır başka bir metinle çıkmasın.
        if (entry.State == InstagramAccountState.NotLinked && pageCount == 1 && !entry.WasLiveThisSession)
            return null;

        // Sorun durumlarının rengi. Uyarı kapısı yalnız tek Sayfadadır: orada hesap bu oturumda canlı olmadıysa
        // aynı metin nötr yazılır.
        var problem = pageCount >= 2 || entry.WasLiveThisSession
            ? InstagramStatusSeverity.Warning
            : InstagramStatusSeverity.Neutral;

        // "Yanıt yok" taban durumun kendi satırından önce gelir ve bir sorun durumudur.
        if (unresponsiveFor is { } quiet)
            return new Line($"yanıt yok ({WholeSeconds(quiet)} sn)", problem, UnresponsiveTooltip);

        switch (entry.State)
        {
            case InstagramAccountState.Live:
                return new Line(
                    "canlı", InstagramStatusSeverity.Live, $"Son yanıt {WholeSeconds(Silence(entry, now))} sn önce");

            case InstagramAccountState.Starting:
                return new Line("bağlanıyor", InstagramStatusSeverity.Neutral, Tooltip: null);

            case InstagramAccountState.WaitingForBroadcast:
                // Süre şu iki andan geç olanından ölçülür: bu hesabın beklemeye başladığı an ve canlı hesaplardan
                // en erken canlı olanın canlı olduğu an. Canlı hesap yoksa hesap yalnızca yayın bekliyordur.
                return liveSince is { } otherLiveSince
                       && now - Later(entry.Since, otherLiveSince) > MissingBroadcastAfter
                    ? new Line(
                        "canlı yayın bulunamadı", InstagramStatusSeverity.Warning, MissingBroadcastTooltip,
                        StripOnly: true)
                    : new Line("yayın bekleniyor", InstagramStatusSeverity.Neutral, WaitingTooltip);

            case InstagramAccountState.BroadcastEnded:
                // Yalnız başka bir hesap hâlâ canlıyken uyarıdır; canlı hesap kalmadıysa yayın bitmiştir.
                var endedAt = Clock(entry.Since, now);
                return liveSince is not null
                    ? new Line(
                        $"yayın kesildi ({endedAt})", InstagramStatusSeverity.Warning, BroadcastCutTooltip,
                        StripOnly: now - entry.Since > EndedTopDotFor)
                    : new Line($"yayın bitti ({endedAt})", InstagramStatusSeverity.Neutral, Tooltip: null);

            case InstagramAccountState.RateLimited:
                return new Line("hız sınırı" + RetryClause(entry, now), problem, RateLimitedTooltip);

            case InstagramAccountState.Faulted:
                return new Line("bağlantı yenilenmeli", problem, FaultedTooltip(entry, now));

            case InstagramAccountState.ResolveFailed:
                return new Line(
                    "hesap bilgisi okunamadı" + RetryClause(entry, now), problem, ResolveFailedTooltip,
                    ByPageName: true);

            case InstagramAccountState.NotLinked:
                return new Line(
                    "Instagram hesabı bağlı değil", problem,
                    entry.IsPrimary ? PrimaryNotLinkedTooltip : ExtraNotLinkedTooltip, ByPageName: true);

            default:
                return null;   // Stopped, Skipped: şeride girmez
        }
    }

    /// <summary>
    /// Hesap yanıtsızsa ne zamandır yanıtsız olduğu ("yanıt yok (N sn)"nin süresi); değilse null. Yoklanan
    /// durumlarda süre sessizliktir. Yeniden deneme saatini bekleyen durumlarda o saatin ne kadar geçildiğidir:
    /// bekleme bitmiş ama yeni bir sonuç gelmemiştir, satır geçmiş bir saatle ve eski sebeple kalmaz.
    /// </summary>
    private static TimeSpan? UnresponsiveFor(InstagramAccountHealth entry, DateTimeOffset now) => entry.State switch
    {
        InstagramAccountState.Live => Exceeding(Silence(entry, now), LiveSilence),

        InstagramAccountState.WaitingForBroadcast
            or InstagramAccountState.BroadcastEnded
            or InstagramAccountState.Starting => Exceeding(Silence(entry, now), IdleSilence),

        InstagramAccountState.RateLimited
            or InstagramAccountState.Faulted
            or InstagramAccountState.ResolveFailed
            or InstagramAccountState.NotLinked =>
            entry.RetryAt is { } retryAt ? Exceeding(now - retryAt, RetryOverdue) : null,

        _ => null,   // Stopped, Skipped: yoklanmıyor
    };

    /// <summary>Süre eşiği aştıysa sürenin kendisi, aşmadıysa null. Eşiğin kendisi aşım değildir.</summary>
    private static TimeSpan? Exceeding(TimeSpan elapsed, TimeSpan threshold) => elapsed > threshold ? elapsed : null;

    /// <summary>
    /// Hesaptan ne zamandır sonuç alınmadığı: son temas ile duruma giriş anından geç olanından bu yana; temas yoksa
    /// duruma girişten. Duruma giriş de sayılır ki yeniden başlayan ya da çözümlemesi yeni düzelen hesap, önceki
    /// temasının eskiliği yüzünden anında "yanıt yok" görünmesin.
    /// </summary>
    private static TimeSpan Silence(InstagramAccountHealth entry, DateTimeOffset now) =>
        now - (entry.LastContactAt is { } contact ? Later(contact, entry.Since) : entry.Since);

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>Sürenin tam saniyesi (kesri atılır), metne hazır.</summary>
    private static string WholeSeconds(TimeSpan elapsed) =>
        Math.Max(0L, elapsed.Ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);

    /// <summary>Durum metninin yeniden deneme parçası: ", 21:20'de dener"; saat bilinmiyorsa boş.</summary>
    private static string RetryClause(InstagramAccountHealth entry, DateTimeOffset now) =>
        entry.RetryAt is { } retryAt ? $", {ClockWithLocative(retryAt, now)} dener" : "";

    /// <summary>Kalıcı hatanın ipucu: yoklamanın mesajı (varsa) + nereden düzeltileceği + (saat biliniyorsa) ne
    /// zaman yeniden deneneceği.</summary>
    private static string FaultedTooltip(InstagramAccountHealth entry, DateTimeOffset now)
    {
        var tooltip = string.IsNullOrWhiteSpace(entry.Detail) ? ReauthorizeHint : $"{entry.Detail} {ReauthorizeHint}";
        return entry.RetryAt is { } retryAt
            ? $"{tooltip} {ClockWithLocative(retryAt, now)} yeniden denenecek."
            : tooltip;
    }

    /// <summary>
    /// Anın, <paramref name="now"/>'ın ofsetindeki duvar saati (SS:DD). Makinenin saat dilimi değil çağıranın
    /// verdiği ofset esas alınır: saat dilimi de girdidir, sonuç makineye göre değişmez.
    /// </summary>
    private static string Clock(DateTimeOffset at, DateTimeOffset now) =>
        at.ToOffset(now.Offset).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// <see cref="Clock"/> + bulunma eki: "21:20'de". Eki mevcut <see cref="PaymentResultPresenter.Locative"/>
    /// verir; o, anı makinenin yerel saatine çevirip okuduğu için aynı duvar saati yerel ofsetle yeniden kurulup
    /// verilir (an doğrudan verilseydi ek, <paramref name="now"/>'ın ofsetindeki değil makinedeki saate uyardı).
    /// </summary>
    private static string ClockWithLocative(DateTimeOffset at, DateTimeOffset now)
    {
        var wall = at.ToOffset(now.Offset).DateTime;
        var sameWallClockLocally = new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
        return Clock(at, now) + PaymentResultPresenter.Locative(sameWallClockLocally);
    }
}
