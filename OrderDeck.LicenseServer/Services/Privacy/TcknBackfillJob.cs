using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// Eski düz metin TCKN'leri şifreler (Shoppers.Tc, IntakeFormSubmissions.Tckn;
/// WpfCustomerProjections.Tckn'de SAF bekçi — o kolon şifrelemeyle birlikte
/// doğdu, orada bulunan her düz metin <c>Protect</c>'i atlayan bir yol demektir
/// ve ayrıca uyarı olarak günlüğe yazılır).
/// İdempotent: yalnız Data Protection öneki taşımayan değerlere dokunur
/// (<see cref="TcknProtector.IsLegacyPlaintext"/> — biçim ne olursa olsun;
/// kayıt akışı eskiden TC'yi doğrulamadan yazıyordu). Geri alınabilir: çözücü
/// iki biçimi de okur.
///
/// <para>Açılışta bir kez kuyruğa alınır (deploy sonrası bir gün beklemesin),
/// sonra günde bir koşar. Günlük koşu aynı zamanda bekçi: geçiş bittikten
/// SONRA günlükte sıfırdan farklı bir sayı görmek GENELDE <c>Protect</c>'i
/// atlayan yeni bir yazma yolu demektir — ama TEK yorum bu değil. Sunucu bir
/// süre PR-0a'ya (bu PR'dan önceki sürüm; yazmalar hâlâ düz metin) geri
/// alınıp sonra PR-0b'ye dönülmüşse, o aradaki sürede biriken düz metin
/// satırlar da burada şifrelenir ve sayı yine sıfırdan farklı çıkar — bu
/// BEKLENEN bir durumdur, var olmayan bir yazma yolu kovalamaya gerek yok.
/// Günlük satırı deploy doğrulamasının kanıtıdır, ama "sıfırdan farklı" tek
/// başına alarm değildir; hangi senaryo olduğunu ayırt etmek deploy/rollback
/// geçmişine bakmayı gerektirir.</para>
///
/// <para><b>Satır başına KARŞILAŞTIR-VE-DEĞİŞTİR (CAS), izlenen entity +
/// SaveChanges DEĞİL.</b> Adaylar <c>AsNoTracking</c> ile salt okunur çekilir;
/// yazma <see cref="EncryptShopperIfUnchangedAsync"/> /
/// <see cref="EncryptFormSubmissionIfUnchangedAsync"/> /
/// <see cref="EncryptProjectionIfUnchangedAsync"/> üzerinden
/// <c>ExecuteUpdateAsync</c>'le <c>WHERE Id = .. AND [kolon] = okunanDeğer</c>
/// şartlı, tek satırlık bir UPDATE. Sebep KVKK: bu işin okuma ile yazması
/// arasındaki pencerede <c>ShopperPurgeService</c> aynı satırı purge edebilir.
/// Purge <c>shopper.DeletedAt ??= now</c> yazar — <c>DeletedAt</c> zaten
/// doluysa (örn. silme talebi daha önce açılmış) bu bir NO-OP'tur — ve
/// <c>LastResetCodeIssuedAt</c>'a hiç dokunmaz. <c>Shopper</c>'ın TEK
/// eşzamanlılık jetonları bu iki alan olduğundan (<c>LicenseDbContext</c>),
/// purge'ün <c>TcProtected = null</c> yazması hiçbir
/// <c>DbUpdateConcurrencyException</c> TETİKLEMEZ. İzlenen-entity +
/// <c>SaveChanges</c> yoluyla bu iş o purge'den SONRA kaydetseydi, purge'ün
/// SİLDİĞİ düz metnin şifreli hâlini satıra GERİ YAZARDI — bir KVKK silmesini
/// sessizce geri alırdı; aynı risk eşzamanlı bir PATCH /me TC güncellemesi
/// için de geçerli (o da ne <c>DeletedAt</c> ne <c>LastResetCodeIssuedAt</c>
/// değiştirir, kazanan yazı sessizce kaybolurdu). CAS bunu yapısal olarak
/// imkânsız kılar: <c>WHERE [kolon] = okunanDeğer</c> artık eşleşmiyorsa
/// (purge NULL yazdı, bir PATCH başka bir değer yazdı, ya da satır tamamen
/// silindi) UPDATE 0 satır etkiler — iş kendi OKUMADIĞI bir değerin üstüne
/// asla yazmaz. Bu, <see cref="TcknProtector"/>'ın "çözülmüş değer asla geri
/// yazılmaz" ilkesinin arka plan işindeki karşılığı: orada çözülmüş bir
/// değer yok, ama disiplin aynı — okunandan farklıysa dokunma.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class TcknBackfillJob
{
    private readonly LicenseDbContext _db;
    private readonly TcknProtector _protector;
    private readonly ILogger<TcknBackfillJob> _log;

    public TcknBackfillJob(LicenseDbContext db, TcknProtector protector, ILogger<TcknBackfillJob> log)
    {
        _db = db;
        _protector = protector;
        _log = log;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var count = 0;

        // Hacim küçük (yüzlerle ölçülür): adayları çekip ayrımı bellekte
        // yapmak, kuralı TcknProtector'da TEK yerde tutar. AsNoTracking: bu
        // satırlar hiç izlenmeyecek — gerçek yazma aşağıdaki CAS'tan geçiyor
        // (bkz. sınıf dokümanı).
        var shopperCandidates = await _db.Shoppers.AsNoTracking()
            .Where(s => s.TcProtected != null)
            .Select(s => new { s.Id, s.TcProtected })
            .ToListAsync(ct);
        foreach (var c in shopperCandidates.Where(c => TcknProtector.IsLegacyPlaintext(c.TcProtected!)))
            count += await EncryptShopperIfUnchangedAsync(c.Id, c.TcProtected!, ct);

        var formCandidates = await _db.IntakeFormSubmissions.AsNoTracking()
            .Where(f => f.TcknProtected != null)
            .Select(f => new { f.Id, f.TcknProtected })
            .ToListAsync(ct);
        foreach (var c in formCandidates.Where(c => TcknProtector.IsLegacyPlaintext(c.TcknProtected!)))
            count += await EncryptFormSubmissionIfUnchangedAsync(c.Id, c.TcknProtected!, ct);

        // Projeksiyon kolonu şifrelemeyle birlikte doğdu: sync ucu her zaman
        // Protect ediyor, geçirilecek eski düz metin yok. Burada SAF bekçi —
        // geri alma senaryosu da yok (eski imajlar bu kolona hiç yazmıyordu),
        // yani sıfırdan farklı sayı Protect'i atlayan bir yazma yolu demektir.
        // IgnoreQueryFilters: bekçi KOLONUN tamamını tarar — kopyalar (A5b:
        // varsayılan sorgulardan gizli) dahil; kopyada düz metin de aynı ihlal.
        var projectionCount = 0;
        var projectionCandidates = await _db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TcknProtected != null)
            .Select(p => new { p.Id, p.TcknProtected })
            .ToListAsync(ct);
        foreach (var c in projectionCandidates.Where(c => TcknProtector.IsLegacyPlaintext(c.TcknProtected!)))
            projectionCount += await EncryptProjectionIfUnchangedAsync(c.Id, c.TcknProtected!, ct);
        if (projectionCount > 0)
            _log.LogWarning(
                "TCKN bekçisi: WpfCustomerProjections'ta {Count} düz metin satır şifrelendi — sync ucu her zaman şifreler; Protect'i atlayan bir yazma yolu var",
                projectionCount);
        count += projectionCount;

        _log.LogInformation("TCKN şifreleme geçişi: {Count} düz metin satır şifrelendi", count);
        return count;
    }

    /// <summary>
    /// Bir Shopper.Tc satırını, YALNIZ <paramref name="expectedPlain"/> hâlâ
    /// satırdaki değerse şifreler (satır başına CAS — bkz. sınıf dokümanı).
    /// Satır arada purge edilmiş, silinmiş ya da başka bir değere
    /// değiştirilmişse 0 döner, hiçbir şey yazmaz. Public: deterministik
    /// yarış testleri bu metodu doğrudan çağırıp satırı ARADA değiştirip
    /// artık bayat olan <paramref name="expectedPlain"/> ile çağırabiliyor.
    /// </summary>
    public Task<int> EncryptShopperIfUnchangedAsync(Guid id, string expectedPlain, CancellationToken ct)
        => _db.Shoppers
            .Where(s => s.Id == id && s.TcProtected == expectedPlain)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.TcProtected, _protector.Protect(expectedPlain)), ct);

    /// <summary>Yukarıdakiyle aynı CAS deseni, IntakeFormSubmission.Tckn için.</summary>
    public Task<int> EncryptFormSubmissionIfUnchangedAsync(Guid id, string expectedPlain, CancellationToken ct)
        => _db.IntakeFormSubmissions
            .Where(f => f.Id == id && f.TcknProtected == expectedPlain)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.TcknProtected, _protector.Protect(expectedPlain)), ct);

    /// <summary>Aynı CAS deseni, WpfCustomerProjection.Tckn için. Eşzamanlı
    /// yazanlar KVKK silmesi (MarkPurged: null) ve sync ucu (yeni şifreli
    /// değer); ikisinde de WHERE artık eşleşmez, 0 döner. Tarama gibi kopya
    /// satırları da görür (IgnoreQueryFilters).</summary>
    public Task<int> EncryptProjectionIfUnchangedAsync(Guid id, string expectedPlain, CancellationToken ct)
        => _db.WpfCustomerProjections.IgnoreQueryFilters()
            .Where(p => p.Id == id && p.TcknProtected == expectedPlain)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.TcknProtected, _protector.Protect(expectedPlain)), ct);
}
