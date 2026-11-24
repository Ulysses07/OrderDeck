using System;
using System.Collections.Generic;
using System.Threading;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>Bağlı bir Sayfanın Instagram sohbetinin durumu.</summary>
public enum InstagramAccountState
{
    /// <summary>Hesap yoklanmıyor: oturum yok, oturum bitti ya da Sayfa listesi oturum içinde
    /// boşaldı.</summary>
    Stopped,

    /// <summary>Hesap döngüsü başladı; henüz çözümleme ya da tur sonucu yok.</summary>
    Starting,

    /// <summary>Sayfaya bağlı Instagram profesyonel hesabı görünmüyor.</summary>
    NotLinked,

    /// <summary>Sayfanın Instagram hesabı okunamadı.</summary>
    ResolveFailed,

    /// <summary>Aynı Instagram hesabını başka bir Sayfa yokluyor; bu Sayfa atlandı (çift yorum
    /// olmasın).</summary>
    Skipped,

    /// <summary>Yoklanıyor; hesapta canlı yayın yok ve bu oturumda hiç canlı olmadı.</summary>
    WaitingForBroadcast,

    /// <summary>Canlı yayın var, yorumlar okunuyor.</summary>
    Live,

    /// <summary>Hesap bu oturumda canlıydı, şimdi yayın yok.</summary>
    BroadcastEnded,

    /// <summary>Kota aşıldı; hesap <see cref="InstagramAccountHealth.RetryAt"/>'e kadar bekliyor.</summary>
    RateLimited,

    /// <summary>Kalıcı hata (token ya da izin); yoklama durdu.</summary>
    Faulted,
}

/// <summary>
/// Bir hesabın sağlık olguları: <see cref="InstagramHealthMonitor.Snapshot"/> anındaki değişmez
/// kopya. Yorum taşımaz; "yanıt yok", "yayın kesildi" gibi sonuçları okuyan taraf bu alanlardan
/// okuma anında türetir.
/// </summary>
/// <param name="PageId">Bağlı Facebook Sayfasının kimliği; girdinin anahtarı.</param>
/// <param name="PageName">Sayfanın görünen adı.</param>
/// <param name="IsPrimary">Birincil Sayfa mı (yapılandırma listesinin ilk girdisi).</param>
/// <param name="Username">Sayfaya bağlı Instagram hesabının kullanıcı adı; hesap henüz
/// çözülmediyse ya da adı okunamadıysa null.</param>
/// <param name="State">Hesabın durumu.</param>
/// <param name="Detail">Duruma eşlik eden açıklama (kalıcı hatada operatöre gösterilecek mesaj);
/// yoksa null.</param>
/// <param name="Since">Hesabın bu duruma girdiği an. Aynı durumu yineleyen bildirim bunu
/// değiştirmez.</param>
/// <param name="LastContactAt">Yoklamanın son kez sonuç aldığı an: yorum sayfası, "yayın yok" ya
/// da kota yanıtı. Geçici ve kalıcı hata bunu yenilemez. Oturum başından beri sonuç alınmadıysa
/// null.</param>
/// <param name="RetryAt">Hesabın yeniden deneneceği an; beklenmiyorsa ya da henüz yazılmadıysa
/// null.</param>
/// <param name="WasLiveThisSession">Hesap bu oturumda en az bir kez canlı oldu mu.</param>
public sealed record InstagramAccountHealth(
    string PageId,
    string PageName,
    bool IsPrimary,
    string? Username,
    InstagramAccountState State,
    string? Detail,
    DateTimeOffset Since,
    DateTimeOffset? LastContactAt,
    DateTimeOffset? RetryAt,
    bool WasLiveThisSession);

/// <summary>
/// Bağlı her Sayfanın Instagram sohbetinin sağlığını hesap başına tutar. Instagram API yolunun
/// ana ekranda hiçbir göstergesi yoktu; susan hesap operatörden gizli kalıyordu.
///
/// <para><b>Yalnız olgu tutar.</b> Durum, son temas ve yeniden deneme saati burada durur;
/// "yanıt yok" gibi yorumlar okuma anında, okuyan tarafta türetilir. İzleyici log da yazmaz:
/// durum geçişini günlüğe, bildirimi yapan taraf yazar.</para>
///
/// <para><b>Tekil ve kilitli.</b> Bütün üyeler tek kilit altındadır: yazanlar (hesap başına
/// yoklama döngüleri) ile okuyan (arayüz) aynı örneği ayrı iş parçacıklarından çağırır.
/// Bilinmeyen Sayfa kimliği her üyede yok sayılır: kaldırılmış bir Sayfanın geç gelen bildirimi
/// fırlatmaz, silinen girdiyi de diriltmez.</para>
///
/// <para><b><see cref="InstagramAccountHealth.Since"/></b> yalnız durum değişince "şimdi" olur;
/// aynı durumu yineleyen bildirim onu değiştirmez (saniyede bir gelen tur süreyi hep sıfırlardı).
/// Tek istisna <see cref="ResetSession"/>.</para>
/// </summary>
public sealed class InstagramHealthMonitor
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    /// <summary>Girdiler, <see cref="Configure"/> sırasıyla. Kayıtlar değişmezdir: her değişiklik
    /// yerine yenisini yazar, bu yüzden <see cref="Snapshot"/> listeyi kopyalamakla yetinir.</summary>
    private readonly List<InstagramAccountHealth> _entries = new();

    /// <param name="time">Saat kaynağı; verilmezse sistem saati.</param>
    public InstagramHealthMonitor(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// Girdileri bağlı Sayfa listesine uydurur: sıra, birincil işareti (ilk girdi) ve Sayfa adı her
    /// çağrıda listeden yazılır; listede olmayan girdi silinir; yeni Sayfa
    /// <see cref="InstagramAccountState.Stopped"/> olarak eklenir. Kalan girdinin öbür alanlarına
    /// dokunulmaz: liste oturum içinde değişince çalışan hesapların durumu sıfırlanmaz.
    /// </summary>
    /// <param name="pages">Bağlı Sayfalar, birincil önce; Sayfa kimliğine göre tekil.</param>
    public void Configure(IReadOnlyList<FacebookLinkedPage> pages)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var next = new List<InstagramAccountHealth>(pages.Count);
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var index = IndexOf(page.PageId);
                var entry = index >= 0
                    ? _entries[index]
                    : new InstagramAccountHealth(
                        page.PageId, page.PageName, IsPrimary: false, Username: null,
                        InstagramAccountState.Stopped, Detail: null, Since: now,
                        LastContactAt: null, RetryAt: null, WasLiveThisSession: false);
                next.Add(entry with { PageName = page.PageName, IsPrimary = i == 0 });
            }

            _entries.Clear();
            _entries.AddRange(next);
        }
    }

    /// <summary>
    /// Yeni oturum: her girdi <see cref="InstagramAccountState.Stopped"/> olur ve oturuma ait
    /// alanlar (bu oturumda canlıydı, son temas, yeniden deneme saati, açıklama) temizlenir;
    /// kullanıcı adı kalır. <see cref="InstagramAccountHealth.Since"/> burada HEP "şimdi"dir,
    /// girdi zaten durmuş olsa da: önceki oturumdan kalan süre yenisine taşınmaz.
    /// </summary>
    public void ResetSession()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            for (var i = 0; i < _entries.Count; i++)
            {
                _entries[i] = _entries[i] with
                {
                    State = InstagramAccountState.Stopped,
                    Since = now,
                    Detail = null,
                    LastContactAt = null,
                    RetryAt = null,
                    WasLiveThisSession = false,
                };
            }
        }
    }

    /// <summary>
    /// Oturum bitti ya da Sayfa listesi oturum içinde boşaldı: her girdi
    /// <see cref="InstagramAccountState.Stopped"/> olur, silinmez. Durumu değişen girdide
    /// <see cref="InstagramAccountHealth.Since"/> genel kural gereği "şimdi" olur; başka HİÇBİR
    /// alan değişmez: "bu oturumda canlıydı" bilgisi korunur ki aynı Sayfa oturum içinde yeniden
    /// bağlanıp bozuk çıkarsa uyarısı gizlenmesin.
    ///
    /// <para>Çağıran önce poller'ları durdurur: geç gelen tur olayı durmuş girdide de işlenir ve
    /// onu yeniden canlı gösterirdi.</para>
    /// </summary>
    public void StopAll()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            for (var i = 0; i < _entries.Count; i++)
                _entries[i] = WithState(_entries[i], InstagramAccountState.Stopped, now);
        }
    }

    /// <summary>Bağlantı yok: bütün girdiler silinir.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    /// <summary>
    /// Sayfanın Instagram hesabı çözüldü: kullanıcı adı yazılır (null dahil). Girdi bir çözümleme
    /// sonucuyla bekliyorsa (<see cref="InstagramAccountState.NotLinked"/>,
    /// <see cref="InstagramAccountState.ResolveFailed"/>, <see cref="InstagramAccountState.Skipped"/>)
    /// ayrıca <see cref="InstagramAccountState.Starting"/>'e alınır, açıklaması ve yeniden deneme
    /// saati temizlenir: hesap düzeldiği hâlde satır eski sebeple kalmasın. Öbür durumlarda başka
    /// alan değişmez.
    /// </summary>
    public void SetAccount(string pageId, string? username)
    {
        lock (_gate)
        {
            var index = IndexOf(pageId);
            if (index < 0) return;

            var entry = _entries[index] with { Username = username };
            if (entry.State is InstagramAccountState.NotLinked
                or InstagramAccountState.ResolveFailed
                or InstagramAccountState.Skipped)
            {
                entry = WithState(entry, InstagramAccountState.Starting, _time.GetUtcNow())
                    with { Detail = null, RetryAt = null };
            }

            _entries[index] = entry;
        }
    }

    /// <summary>
    /// Hesabın durumunu yazar. Açıklama ve yeniden deneme saati HER çağrıda verilen değerle
    /// yazılır; verilmeyen temizlenir (önceki durumun sebebi ve saati yenisine sızmasın).
    ///
    /// <para><see cref="InstagramAccountState.Starting"/> yalnız durmuş
    /// (<see cref="InstagramAccountState.Stopped"/>) girdide uygulanır; öbür durumlarda çağrı
    /// tümüyle yok sayılır. Poller her yeniden kurulduğunda hesap "bağlanıyor"a dönseydi sürmekte
    /// olan durum ve süresi her kurulumda silinirdi.</para>
    /// </summary>
    public void Report(
        string pageId, InstagramAccountState state, string? detail = null, DateTimeOffset? retryAt = null)
    {
        lock (_gate)
        {
            var index = IndexOf(pageId);
            if (index < 0) return;

            var entry = _entries[index];
            if (state == InstagramAccountState.Starting && entry.State != InstagramAccountState.Stopped)
                return;

            _entries[index] = WithState(entry, state, _time.GetUtcNow())
                with { Detail = detail, RetryAt = retryAt };
        }
    }

    /// <summary>
    /// "Sohbeti yenile" bekleyen bir hesabı uyandırdı: girdi, durumuna BAKILMADAN
    /// <see cref="InstagramAccountState.Starting"/>'e alınır ("bağlanıyor"); açıklaması ve yeniden
    /// deneme saati temizlenir, "bu oturumda canlıydı" bilgisi korunur.
    ///
    /// <para><see cref="Report"/>'un Starting kuralının (yalnız durmuş girdide etkili) bilinçli
    /// istisnasıdır: operatör düğmeye bastığında bekleyen hesabın yeniden denendiğini görmeli.
    /// Yalnız beklemesi kesilen hesap için çağrılır; sağlıklı çalışan okuyucunun girdisine
    /// dokunulmaz.</para>
    /// </summary>
    public void MarkRetrying(string pageId)
    {
        lock (_gate)
        {
            var index = IndexOf(pageId);
            if (index < 0) return;

            _entries[index] = WithState(_entries[index], InstagramAccountState.Starting, _time.GetUtcNow())
                with { Detail = null, RetryAt = null };
        }
    }

    /// <summary>
    /// O Sayfanın poller'ına verilecek gözlemci: tur sonuçlarını Sayfanın girdisine işler. Girdi
    /// o an yoksa da döner; girdisi olmayan Sayfanın tur olayı yok sayılır.
    /// </summary>
    public IInstagramPollObserver ObserverFor(string pageId) => new PageObserver(this, pageId);

    /// <summary>Girdilerin o anki kopyası, <see cref="Configure"/> sırasıyla. Sonraki değişiklikler
    /// dönen listeyi etkilemez.</summary>
    public IReadOnlyList<InstagramAccountHealth> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>
    /// Tur sonucunu girdiye işler. Geçici hata hiçbir alanı değiştirmez: hesap yanıt vermiyor
    /// demektir ve bunu okuyan taraf son temasın eskimesinden türetir.
    /// </summary>
    private void ApplyRound(string pageId, InstagramPollKind kind, string? message, TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var index = IndexOf(pageId);
            if (index < 0) return;

            var entry = _entries[index];
            var now = _time.GetUtcNow();
            string? detail = null;
            DateTimeOffset? retryAt = null;
            switch (kind)
            {
                case InstagramPollKind.Ok:
                    entry = WithState(entry, InstagramAccountState.Live, now)
                        with { LastContactAt = now, WasLiveThisSession = true };
                    break;

                case InstagramPollKind.NoBroadcast:
                    // Yayın yok: hesap bu oturumda canlı olduysa yayını bitmiştir, olmadıysa
                    // henüz açılmamıştır. İkisi operatöre ayrı şeyler söyler.
                    var idle = entry.WasLiveThisSession
                        ? InstagramAccountState.BroadcastEnded
                        : InstagramAccountState.WaitingForBroadcast;
                    entry = WithState(entry, idle, now) with { LastContactAt = now };
                    break;

                case InstagramPollKind.RateLimited:
                    entry = WithState(entry, InstagramAccountState.RateLimited, now)
                        with { LastContactAt = now };
                    retryAt = now + retryAfter;
                    break;

                case InstagramPollKind.Fatal:
                    // Son temas yazılmaz. Yeniden deneme saatini de poller bilmez: bekleyecek
                    // olan çağıran, poller bittikten sonra Report ile yazar.
                    entry = WithState(entry, InstagramAccountState.Faulted, now);
                    detail = message;
                    break;

                default:
                    return; // Transient: hiçbir alan değişmez.
            }

            // Sonuç bildiren her tur açıklamayı ve yeniden deneme saatini de yeniden yazar:
            // vermediği temizlenir, önceki durumun sebebi ve saati yenisine sızmaz.
            _entries[index] = entry with { Detail = detail, RetryAt = retryAt };
        }
    }

    private int IndexOf(string pageId)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].PageId == pageId) return i;
        }

        return -1;
    }

    /// <summary><see cref="InstagramAccountHealth.Since"/> kuralının tek yeri: durum değişiyorsa
    /// "şimdi" olur, değişmiyorsa girdi aynen döner.</summary>
    private static InstagramAccountHealth WithState(
        InstagramAccountHealth entry, InstagramAccountState state, DateTimeOffset now) =>
        entry.State == state ? entry : entry with { State = state, Since = now };

    private sealed class PageObserver(InstagramHealthMonitor monitor, string pageId) : IInstagramPollObserver
    {
        public void RoundStarting()
        {
            // Turun başlaması bir olgu değiştirmez; sonucu RoundCompleted bildirir.
        }

        public void RoundCompleted(
            InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader) =>
            monitor.ApplyRound(pageId, kind, message, retryAfter);
    }
}
