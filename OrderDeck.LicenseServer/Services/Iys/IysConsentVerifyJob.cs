using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Hangfire işi: <c>/iys/search</c> ile İYS'nin gerçek cevabını okur.
///
/// <para><b>Push'tan AYRI bir adım olması bu tasarımın merkezi.</b> Push
/// "kuyruğa alındı" cevabı döner; kabul yalnız burada görülebilir. Sonuç
/// <see cref="IysConsent.LastVerifiedStatus"/>'e yazılır — yerel
/// <see cref="IysConsent.Status"/> asla ezilmez, çünkü o kişinin bize verdiği
/// onayın ispatı (kural 2).</para>
///
/// <para>Randevu takvimi <see cref="IysVerifySchedule"/>: 15dk → 1sa → 6sa →
/// 24sa. Erken sorgu, henüz işlenmemiş kaydı "RET" sanmaya yol açar.</para>
///
/// <para><b>Marka başına adalet:</b> hem sorgu hem koşu sınırı marka başına
/// uygulanır. Sınır markadan önce uygulanırsa, kalıcı hata veren bir
/// yayıncının kayıtları her koşuda ilk sıraları kapar ve diğer yayıncılar
/// sonsuza dek doğrulanmadan bekler (hat başı tıkanması).</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysConsentVerifyJob
{
    /// <summary>Tek sorguda sorulan alıcı sayısı.</summary>
    public const int BatchSize = 20;

    /// <summary>Tek koşuda tek markadan doğrulanacak azami kayıt.</summary>
    public const int MaxPerBrandPerRun = BatchSize * 5;

    /// <summary>Ağ/geçici hata sonrası aynı kaydı yeniden denemeden önceki bekleme.</summary>
    public static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMinutes(5);

    private readonly LicenseDbContext _db;
    private readonly IIysClient _client;
    private readonly NetgsmAccountService _accounts;
    private readonly ILogger<IysConsentVerifyJob> _log;

    public IysConsentVerifyJob(
        LicenseDbContext db, IIysClient client, NetgsmAccountService accounts,
        ILogger<IysConsentVerifyJob> log)
    {
        _db = db;
        _client = client;
        _accounts = accounts;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var accounts = await _accounts.ListVerifiedAsync(ct);
        if (accounts.Count == 0) return;

        foreach (var acct in accounts)
        {
            try
            {
                await VerifyBrandAsync(acct, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Change tracker'ı BOŞALT: bütün marka turları aynı scoped
                // DbContext'i paylaşıyor. Düşen turun kirli (Modified/Added)
                // varlıkları askıda kalırsa SIRADAKİ markanın SaveChangesAsync'i
                // onları da yazar — A'nın doğrulama olayları B'nin turunda
                // commit edilir. Her tur kendi içinde kaydettiği için burada
                // atılacak bir şey yok: kaydedilmemiş her şey o turun çöpüdür.
                _db.ChangeTracker.Clear();

                // Marka başına yalıtım: A'nın bozuk ayarı B'nin doğrulamasını
                // durdurmaz. Randevular olduğu yerde kalır; ayar düzeltilince
                // kaldığı yerden devam eder.
                _log.LogError(ex,
                    "İYS doğrulama: {Brand} markası atlandı (lisans {LicenseId})",
                    acct.BrandCode, acct.LicenseId);
            }
        }
    }

    private async Task VerifyBrandAsync(NetgsmAccount acct, CancellationToken ct)
    {
        var password = _accounts.TryUnprotectPassword(acct.PasswordProtected);
        if (password is null)
        {
            // Hesabın Status'üne DOKUNULMAZ (bkz. IysConsentPushJob): Failed →
            // Verified dönen kod yolu yok ve IysConsentCollector markayı yalnız
            // Verified hesaptan çözer — hesabı kapatmak o yayıncının YENİ onay
            // toplamasını da durdurur, geri doldurulamaz. Anahtar geri geldiğinde
            // sistem kendiliğinden düzelsin; şimdilik yalnız arızayı görünür kıl.
            //
            // Hesabı VERİTABANINDAN TAZE oku — elimizdeki acct izlenmiyor
            // (ListVerifiedAsync no-tracking döner) ve bütün marka turları aynı
            // scoped DbContext'i paylaşıyor. Detached nesneye yazıp SaveChanges
            // demek hiçbir hata vermeden hiçbir satırı güncellemez; LastError'ın
            // TEK amacı operatöre görünürlük olduğu için o sessiz kayıp
            // düzeltmenin kendisini işe yaramaz kılar. "Gereksiz sorgu" diye
            // sadeleştirilmemeli.
            var tracked = await _db.NetgsmAccounts
                .FirstOrDefaultAsync(a => a.Id == acct.Id, ct);
            if (tracked is not null)
            {
                tracked.LastError = Truncate(
                    "Kayıtlı şifre çözülemedi (veri koruma anahtarı okunamıyor); "
                    + "anahtar erişimi düzelene kadar İYS doğrulaması bekletiliyor.", 500);
                tracked.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            // Satır yoksa hesap bu koşu sırasında silinmiş (KVKK/lisans iptali);
            // yazacak yer yok, log yeterli.
            _log.LogError(
                "İYS doğrulama: {Brand} markasının şifresi çözülemedi, bu tur atlandı "
                + "(hesap durumu değiştirilmedi)", acct.BrandCode);
            return;
        }

        var account = new IysAccountContext(
            acct.LicenseId, acct.UserCode, password, acct.BrandCode);

        var now = DateTimeOffset.UtcNow;
        var due = await _db.IysConsents
            .Where(c => c.BrandCode == acct.BrandCode
                        && c.PushState == IysPushState.Pushed
                        && c.NextVerifyAt != null && c.NextVerifyAt <= now)
            .OrderBy(c => c.NextVerifyAt)
            .Take(MaxPerBrandPerRun)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var batch in due.Chunk(BatchSize))
        {
            IysSearchResult result;
            try
            {
                result = await _client.SearchAsync(
                    account, batch.Select(c => c.Recipient).ToArray(), ct);
            }
            catch (IysConfigurationException cfg)
            {
                // Bu markanın turu durur — RunAsync'teki döngü yakalar.
                // Randevular olduğu yerde kalır: ayar düzeltildiğinde
                // doğrulama kaldığı yerden devam eder.
                _log.LogError(cfg,
                    "İYS yapılandırma hatası ({Code}) — {Brand} markasının doğrulaması durdu",
                    cfg.Code, account.BrandCode);
                throw;
            }
            catch (Exception ex)
            {
                // Randevuyu İLERİ AL. Eskiden randevu olduğu yerde kalıyordu:
                // kalıcı hata veren kayıtlar her koşuda yeniden ilk sıraları
                // kapıyor, aynı markanın diğer kayıtları hiç sıra alamıyordu.
                // VerifyAttempts'e DOKUNULMAZ — o sayaç İYS'nin cevabını
                // bekleme takvimidir; bir ağ hatası onu tüketip kaydı erken
                // Failed yapmamalı.
                var failedAt = DateTimeOffset.UtcNow;
                var retryAt = failedAt + TransientRetryDelay;
                foreach (var c in batch)
                {
                    c.NextVerifyAt = retryAt;
                    // UpdatedAt randevu DEĞİL, "en son ne zaman dokunuldu"
                    // damgası; retryAt yazmak onu geleceğe atardı ve satırı
                    // zaman aralığına göre süzen her sorguyu yanıltırdı.
                    c.UpdatedAt = failedAt;
                }
                await _db.SaveChangesAsync(ct);
                _log.LogWarning(ex,
                    "İYS doğrulama: {Brand} markasında {Count} kayıtlık sorgu başarısız",
                    account.BrandCode, batch.Length);
                continue;
            }

            var stamp = DateTimeOffset.UtcNow;
            foreach (var c in batch)
            {
                // Yanıtta hiç görünmeyen alıcı Unknown — fail-closed.
                var status = result.Statuses.TryGetValue(c.Recipient, out var s)
                    ? s : IysConsentStatus.Unknown;

                c.LastVerifiedStatus = status;
                c.LastVerifiedAt = stamp;
                c.VerifyAttempts++;
                c.UpdatedAt = stamp;

                _db.IysConsentEvents.Add(new IysConsentEvent
                {
                    Id = Guid.NewGuid(),
                    LicenseId = account.LicenseId,
                    BrandCode = account.BrandCode,
                    IysConsentId = c.Id,
                    Recipient = c.Recipient,
                    OccurredAt = stamp,
                    EventType = IysConsentEventType.SearchResult,
                    Status = status,
                    ApiResponseCode = result.Code,
                    ApiResponseBody = result.RawBody.Length > 2000 ? result.RawBody[..2000] : result.RawBody,
                });

                // Kabul = İYS BEYANIMIZLA aynı şeyi söylüyor: ONAY beyanı için
                // ONAY, RET beyanı için RET. Yalnız ONAY'a bakmak iki yönde
                // yanlıştı: (1) RET beyanı İYS RET dese de hiç Confirmed
                // olamıyor, takvim tükenip Failed'a düşüyor, kurtarma yeniden
                // itiyor ve satır pencere dolana dek admin "sorunlu" listesinde
                // dönüyordu — İYS kabul etmişken; (2) RET beyanı, İYS reddi
                // henüz İŞLEMEMİŞKEN gelen ONAY cevabıyla "kabul" sayılıyordu.
                // RET tarafında "kayıt yok" ile "reddetti" ayırt edilemez ama
                // ikisi de aynı sonuca çıkar: gönderim yok. Confirmed RET
                // gönderim izni DEĞİLDİR — kapı Status'u da okur (kural 4).
                if (status != IysConsentStatus.Unknown && status == c.Status)
                {
                    c.PushState = IysPushState.Confirmed;
                    c.NextVerifyAt = null;
                    c.LastError = null;
                    continue;
                }

                // Beyanla henüz eşleşmiyor (ONAY için "kayıt yok/reddetti",
                // RET için "İYS reddi henüz işlemedi"). Beklemekten başka
                // yapacak bir şey yok; takvim tükenene kadar tekrar sorulur.
                var next = IysVerifySchedule.Next(stamp, c.VerifyAttempts);
                c.NextVerifyAt = next;
                if (next is null)
                {
                    c.PushState = IysPushState.Failed;
                    c.LastError = $"iys-not-confirmed status={status}";
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        var confirmed = due.Count(c => c.PushState == IysPushState.Confirmed);
        _log.LogInformation(
            "İYS doğrulama ({Brand}): {Total} kayıt soruldu, {Confirmed} beyanla eşleşti (kabul)",
            account.BrandCode, due.Count, confirmed);
    }

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max];
}
