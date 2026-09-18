using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Hangfire işi: <see cref="IysPushState.Pending"/> kayıtları <c>/iys/add</c>
/// ile İYS'ye bildirir.
///
/// <para><b>Bu iş "kabul edildi" kararı VERMEZ.</b> Netgsm'in <c>code 0</c>
/// yanıtı "kuyruğa alındı" demek. Kayıt <see cref="IysPushState.Pushed"/>'e
/// geçer ve doğrulama randevusu alır; kabulü yalnız
/// <see cref="IysConsentVerifyJob"/> yazabilir. 2026-09-17'de 284 onayı bu
/// ayrımı yapmadığımız için kaybettik.</para>
///
/// <para>Süpürme işi olduğu için satır kilidi değil <b>iş kilidi</b> kullanır:
/// <c>[DisableConcurrentExecution]</c>. Aynı anda ikinci bir kopya çalışırsa
/// aynı <c>Pending</c> satırlarını okur ve İYS'ye ikinci kez bildirir — zararsız
/// ama dakikada 10 isteklik kotayı boşa harcar ve olay tablosunu ikizler.</para>
///
/// <para><b>Marka başına yalıtım:</b> döngü doğrulanmış her Netgsm hesabı için
/// ayrı döner ve her tur kendi <c>try/catch</c>'i içindedir. Bir yayıncının
/// yanlış marka kodu yalnız kendi turunu bitirir; diğerleri etkilenmez.
/// Tek kiracıda <c>throw</c> etmek doğruydu — çok kiracıda platformu susturur.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysConsentPushJob
{
    /// <summary>Tek istekte bildirilen kayıt sayısı.</summary>
    public const int BatchSize = 20;

    /// <summary>
    /// Tek koşuda tek markadan alınacak azami kayıt. Sınır olmazsa 10.000
    /// bekleyeni olan bir yayıncı, 6 saniyelik parti gecikmesiyle koşuyu
    /// saatlerce meşgul eder ve sıradaki markalar hiç sıra alamaz.
    /// 5 dakikada bir × 100 = günde 28.800 kayıt/marka, 3 iş günü penceresine
    /// rahat sığıyor.
    /// </summary>
    public const int MaxPerBrandPerRun = BatchSize * 5;

    /// <summary>Netgsm ~10 istek/dk sınırlı; partiler arası bekleme.</summary>
    public static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(6);

    private readonly LicenseDbContext _db;
    private readonly IIysClient _client;
    private readonly NetgsmAccountService _accounts;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentPushJob> _log;

    public IysConsentPushJob(
        LicenseDbContext db, IIysClient client, NetgsmAccountService accounts,
        IOptions<NetgsmOptions> opt, ILogger<IysConsentPushJob> log)
    {
        _db = db;
        _client = client;
        _accounts = accounts;
        _opt = opt.Value;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Süresi dolmuş bekleyenler hiç gönderilmez: 3 iş günü geçtiyse
        // İYS zaten H467 ile reddeder (consent_date çok eski) ve kayıt
        // hukuken geçersiz. Sessizce silmiyoruz — Expired damgası admin
        // listesinde görünür. Bu süpürme marka bağımsız: süre dolmuşsa
        // hangi yayıncıya ait olduğu sonucu değiştirmez.
        var expired = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Pending
                        && c.PushDeadline != null && c.PushDeadline < now)
            .ToListAsync(ct);
        foreach (var e in expired)
        {
            e.PushState = IysPushState.Expired;
            e.LastError = "push-deadline-passed";
            e.UpdatedAt = now;
        }
        if (expired.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("İYS push: {Count} kaydın 3 iş günü penceresi doldu", expired.Count);
        }

        var accounts = await _accounts.ListVerifiedAsync(ct);
        if (accounts.Count == 0)
        {
            _log.LogInformation("İYS push: doğrulanmış Netgsm hesabı yok, boru hattı kapalı");
            return;
        }

        foreach (var acct in accounts)
        {
            try
            {
                await PushBrandAsync(acct, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // koşu iptal edildi; sıradaki markaya geçmek anlamsız
            }
            catch (Exception ex)
            {
                // Change tracker'ı BOŞALT: bütün marka turları aynı scoped
                // DbContext'i paylaşıyor. Düşen turun kirli (Modified/Added)
                // varlıkları askıda kalırsa SIRADAKİ markanın SaveChangesAsync'i
                // onları da yazar — A'nın başarısız turu B'nin turunda commit
                // edilir. Her tur kendi içinde kaydettiği için burada atılacak
                // bir şey yok: kaydedilmemiş her şey o turun çöpüdür.
                _db.ChangeTracker.Clear();

                // Marka başına yalıtım (spec §4, sözleşme #3).
                _log.LogError(ex,
                    "İYS push: {Brand} markası atlandı (lisans {LicenseId})",
                    acct.BrandCode, acct.LicenseId);
            }
        }
    }

    private async Task PushBrandAsync(
        NetgsmAccount acct, DateTimeOffset now, CancellationToken ct)
    {
        var password = _accounts.TryUnprotectPassword(acct.PasswordProtected);
        if (password is null)
        {
            // Anahtar döndü, veri koruma anahtar dizini bağlanmadı ya da
            // şifreli metin bozuk. Patlamak diğer yayıncıları susturur —
            // ama hesabı Failed işaretlemek çok daha pahalıya patlar:
            //
            //  1) Failed → Verified'a dönen bir kod yolu YOK; tek çıkış elle
            //     SQL. Anahtar dizini tek bir açılışta bağlanmazsa
            //     TÜM doğrulanmış hesaplar tek koşuda kapanırdı.
            //  2) IysConsentCollector markayı yalnız Verified hesaptan çözer;
            //     hesap kapandığı an o yayıncının yeni onayları IysConsent
            //     satırı bile açmaz ve geri doldurulamaz — kişiden yeniden
            //     onay almak gerekir.
            //
            // Bu yüzden Status'e DOKUNULMAZ: anahtar geri geldiğinde sistem
            // kendiliğinden düzelir, bu arada onaylar toplanmaya devam eder,
            // satırlar Pending birikir ve gecikme "son tarihe yaklaşanlar"
            // uyarı yüzeyinden görünür. Yalnız arızayı görünür kılıyoruz.
            acct.LastError = Truncate(
                "Kayıtlı şifre çözülemedi (veri koruma anahtarı okunamıyor); "
                + "anahtar erişimi düzelene kadar İYS bildirimi bekletiliyor.", 500);
            acct.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogError(
                "İYS push: {Brand} markasının şifresi çözülemedi, bu tur atlandı "
                + "(hesap durumu değiştirilmedi)", acct.BrandCode);
            return;
        }

        var account = new IysAccountContext(
            acct.LicenseId, acct.UserCode, password, acct.BrandCode);

        var pending = await _db.IysConsents
            .Where(c => c.BrandCode == acct.BrandCode
                        && c.PushState == IysPushState.Pending
                        && (c.PushDeadline == null || c.PushDeadline >= now))
            .OrderBy(c => c.CreatedAt)
            .Take(MaxPerBrandPerRun)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var first = true;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            if (!first) await Task.Delay(BatchDelay, ct);
            first = false;
            await PushBatchAsync(account, batch, ct);
        }
    }

    private async Task PushBatchAsync(
        IysAccountContext account, IysConsent[] batch, CancellationToken ct)
    {
        var records = batch.Select(c => new IysConsentRecord(
            c.Recipient, c.RecipientType, c.ChannelType, c.Status,
            c.ConsentDate ?? c.LastLocalEventAt,
            c.SourceCode ?? _opt.IysSourceCode,
            c.Id.ToString("N"))).ToArray();

        IysAddResult result;
        try
        {
            result = await _client.AddAsync(account, records, ct);
        }
        catch (IysConfigurationException cfg)
        {
            // Kalıcı yapılandırma hatası: bu markanın her kaydı aynı hatayla
            // düşer, kalan partileri denemek zaman harcar. Fırlatılan istisnayı
            // RunAsync'teki marka döngüsü yakalar → yalnız BU marka atlanır.
            // Kayıtlara DOKUNULMAZ: Failed yazmak, düzeltilebilir bir ayar
            // hatasını kayıt başına kalıcı yara gibi gösterirdi.
            _log.LogError(cfg,
                "İYS yapılandırma hatası ({Code}) — {Brand} markasının turu durdu",
                cfg.Code, account.BrandCode);
            throw;
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var c in batch)
            {
                c.PushState = IysPushState.Failed;
                c.LastError = Truncate(ex.Message, 500);
                c.UpdatedAt = now;
                AddEvent(c, account, IysConsentEventType.PushAttempt,
                    code: null, body: null, error: ex.GetType().Name);
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(ex, "İYS push: {Count} kayıtlık parti başarısız", batch.Length);
            return;
        }

        var stamp = DateTimeOffset.UtcNow;
        foreach (var c in batch)
        {
            AddEvent(c, account, IysConsentEventType.PushAttempt,
                result.Code, result.RawBody, error: null);

            if (result.Queued)
            {
                // KUYRUĞA ALINDI — kabul DEĞİL.
                c.PushState = IysPushState.Pushed;
                c.LastPushedAt = stamp;
                c.VerifyAttempts = 0;
                c.NextVerifyAt = IysVerifySchedule.Next(stamp, 0);
                c.LastError = null;
            }
            else
            {
                c.PushState = IysPushState.Failed;
                c.LastError = Truncate($"iys-add code={result.Code}", 500);
            }
            c.UpdatedAt = stamp;
        }
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "İYS push: {Count} kayıt bildirildi (code={Code}) — doğrulama bekliyor",
            batch.Length, result.Code);
    }

    private void AddEvent(
        IysConsent c, IysAccountContext account, IysConsentEventType type,
        string? code, string? body, string? error)
        => _db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            LicenseId = account.LicenseId,
            BrandCode = account.BrandCode,
            IysConsentId = c.Id,
            Recipient = c.Recipient,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = type,
            Status = c.Status,
            ApiResponseCode = code,
            ApiResponseBody = Truncate(body, 2000),
            ErrorCode = error,
        });

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max];
}
