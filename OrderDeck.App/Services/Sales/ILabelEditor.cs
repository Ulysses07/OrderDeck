using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Services.Sales;

/// <summary>Bir etiketin düzenleme yolu (spec 2026-10-13 §5.3).</summary>
public enum SalesEditRoute
{
    /// <summary>Bugünkü yerel servis (<see cref="LabelService"/>), kapısız. Mod Off'ta her etiket; mod On'da bu
    /// bilgisayarın açık (canlı) yayını.</summary>
    Local,

    /// <summary>Yerel servis, devir kapısı altında (§5.2): mod On'da, ilk yetişmeden sonra, kendi bitmiş ama
    /// devredilmemiş yayını. Kilit sıfır beklemeyle alınır ve yerel yazım boyunca tutulur; alınamazsa
    /// <see cref="SalesCommandMessages.HandoverInProgress"/>.</summary>
    LocalGated,

    /// <summary>Düzenleme kapalı (§5.3, §8.4-2): mod On'un ilk yetişmesi bitmedi ve kendi bitmiş yayınının devri
    /// yerelde bilinmiyor — sunucuda devredilmiş olabilir, yerel yazım push'ta reddedilip kaybolurdu.</summary>
    Blocked,

    /// <summary>Sunucu komutu (<see cref="SalesCommandClient"/>): yayın sunucuya devredilmiş (<c>Authority =
    /// 'server'</c>).</summary>
    Command,

    /// <summary>Komut gerekir ama sunucu şu an kullanılamaz: çevrimdışı ya da komut ucu yok (§5.7, §5.8). Düğmeler
    /// pasif, araç ipucu.</summary>
    Unavailable,
}

/// <summary>Bir etiketin yönlendirme kararı.</summary>
/// <param name="SessionId">Etiketin yayını; mod Off'ta okunmaz (null).</param>
/// <param name="Reason">Blocked ve Unavailable'da operatöre görünen metin; öbürlerinde null.</param>
public sealed record SalesEditDecision(string LabelId, string? SessionId, SalesEditRoute Route, string? Reason);

/// <summary>Uygulanamayan etiketler ve sebebi (Türkçe, operatöre görünen).</summary>
public sealed record SalesEditProblem(string Message, IReadOnlyList<string> LabelIds);

/// <summary>
/// Bir düzenlemenin birleşik sonucu: karışık seçim bölünür (yerel yazım + komut), sonuçlar tek satır içi iletide
/// birleşir (§5.3, §5.7).
/// </summary>
/// <param name="Applied">Yazımı gerçekleşen etiketler (yerel ya da komutla; zaten o hâlde olan dahil).</param>
/// <param name="Problems">Uygulanamayanlar, sebebe göre gruplu.</param>
/// <param name="ChangedLabelIds">Yerelde değeri değişmiş olabilecek etiketler (yerel yazım ve komut yanıtının
/// satırları, 409'un güncel satırları dahil); ekran bunları tazeler.</param>
public sealed record SalesEditResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<SalesEditProblem> Problems,
    IReadOnlyList<string> ChangedLabelIds)
{
    public static SalesEditResult Empty { get; } = new([], [], []);

    /// <summary>Bugünkü yol: hepsi yerelde yazıldı.</summary>
    public static SalesEditResult AllLocal(IReadOnlyList<string> labelIds) => new(labelIds, [], labelIds);

    public bool HasProblems => Problems.Count > 0;

    public int NotAppliedCount => Problems.Sum(p => p.LabelIds.Count);

    /// <summary>
    /// Satır içi metin; sorun yoksa null. Tek sebep ve hiç uygulanan yoksa yalnız sebep; karışıkta her sebep kendi
    /// sayısıyla ("1 etiket değiştirilemedi: Bu değişiklik için sunucu bağlantısı gerekli.").
    /// </summary>
    public string? ProblemText =>
        Problems.Count == 0 ? null
        : Problems.Count == 1 && Applied.Count == 0 ? Sentence(Problems[0].Message)
        : string.Join(" ", Problems.Select(p => $"{p.LabelIds.Count} etiket değiştirilemedi: {Sentence(p.Message)}"));

    private static string Sentence(string text)
    {
        var t = text.Trim();
        return t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') ? t : t + ".";
    }
}

/// <summary>Yeniden baskının planı (§5.6): basılacak satırlar (istenen sırayla, basılmış ve iptal edilmemiş),
/// "DÜZELTME" işaretlileri ve — sunucu satırı önbellekten basıldıysa — ekran notu.</summary>
/// <param name="Error">Komut reddedildi: hiçbir şey basılmaz, metin satır içi.</param>
/// <param name="FromCache">Sunucu satırları çevrimdışı olduğu için yerel önbellekten okundu.</param>
/// <param name="LastSyncAt">Önbelleğin tazeliği: son satış akışı eşitlemesi (bu süreçte); bilinmiyorsa null.</param>
public sealed record SalesReprintPlan(
    IReadOnlyList<Label> Labels,
    IReadOnlySet<string> CorrectionIds,
    string? Error = null,
    bool FromCache = false,
    DateTimeOffset? LastSyncAt = null)
{
    public static SalesReprintPlan From(Reprint reprint) => new(reprint.Labels, reprint.CorrectionIds);

    /// <summary>Ekran notu ("son eşitleme SS:DD"); önbellekten basılmadıysa null.</summary>
    public string? CacheNote => FromCache ? SalesCommandMessages.LastSync(LastSyncAt) : null;
}

/// <summary>
/// Etiket düzenlemelerinin tek karar noktası (spec 2026-10-13 §5.3). Mod Off'ta (<c>SalesSyncState.Mode = 0</c>, bugün
/// her lisans) her çağrı bugünkü yerel servise gider: kapı yok, ağ yok, bekleme yok. Mod On'da etiket başına
/// yayının <c>Authority</c>'si, kendi yayınının bitiş ve devir bilgisi, ilk yetişme ve bağlantı yolu seçer
/// (<see cref="SalesEditRoute"/>). Çağıranlar sonucu satır içi gösterir; pop-up yok.
/// </summary>
public interface ILabelEditor
{
    /// <summary>Etiket başına karar; yazmaz, ağa gitmez. Kip her çağrıda okunur.</summary>
    IReadOnlyList<SalesEditDecision> Route(IReadOnlyCollection<string> labelIds);

    /// <summary>Sunucu şu an komut alamıyorsa araç ipucu metni (çevrimdışı ya da komut ucu yok); kullanılabiliyorsa
    /// null. Yalnız bellekten okur — kipten bağımsız; çağıran seçimin sunucu satırı taşıyıp taşımadığını
    /// <see cref="Route"/> ile bilir.</summary>
    string? ServerUnavailableReason { get; }

    /// <summary>Düğme pasif mi: verilen etiketlerin HEPSİ sunucu ister ve sunucu kullanılamıyorsa araç ipucu metni,
    /// değilse null (karışık seçim etkin kalır; bölünür ve sonucu satır içi söylenir).</summary>
    string? EditBlockedReason(IReadOnlyCollection<string> labelIds);

    /// <summary>İptal (<see cref="LabelService.Cancel"/> ya da <c>cancel</c> komutu).</summary>
    Task<SalesEditResult> CancelAsync(IReadOnlyList<string> labelIds, string reason, CancellationToken ct = default);

    /// <summary>İptali geri alma (<see cref="LabelService.Uncancel"/> ya da <c>uncancel</c> komutu).</summary>
    Task<SalesEditResult> UncancelAsync(IReadOnlyList<string> labelIds, CancellationToken ct = default);

    /// <summary>Yedek onayı (<see cref="LabelService.ConfirmBackup"/> ya da <c>confirm-backup</c> komutu).</summary>
    Task<SalesEditResult> ConfirmBackupAsync(string backupLabelId, decimal? newPrice, CancellationToken ct = default);

    /// <summary>Yeniden baskı (§5.6): yerel satırlar <see cref="LabelService.PrepareReprint"/>; sunucu satırları
    /// çevrimiçiyken <c>reprint</c> komutunun güncel satırları, çevrimdışıyken önbellek (notla).</summary>
    Task<SalesReprintPlan> PrepareReprintAsync(IReadOnlyList<string> labelIds, CancellationToken ct = default);
}
