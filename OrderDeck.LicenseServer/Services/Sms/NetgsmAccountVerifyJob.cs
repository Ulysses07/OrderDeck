using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Günlük yeniden doğrulama (spec §2.3). Abonelik biter, API şifresi döner,
/// marka kodu iptal olur — tek seferlik doğrulama bunları görmez. Düşen hesap
/// <see cref="NetgsmAccountStatus.Failed"/> olur; kampanya kapısı VE onay
/// toplama durur, çünkü geçersiz markayla toplanan onay zaten geçersizdir.
///
/// <para><b>Aşırı tepki asıl tehlike.</b> Yalnız
/// <see cref="NetgsmVerifyOutcome.Rejected"/> hesabı düşürür.
/// <see cref="NetgsmVerifyOutcome.Unavailable"/> yalnız
/// <see cref="NetgsmAccount.LastError"/> yazar: <c>Failed</c>'dan çıkışın tek
/// yolu yayıncının panele girip kaydetmesi olduğu için, İYS'nin yarım saatlik
/// bir kesintisi aksi hâlde bütün yayıncıları elle müdahale gerektiren bir
/// duruma sokardı.</para>
/// </summary>
public sealed class NetgsmAccountVerifyJob
{
    /// <summary>
    /// Günlük işin KENDİ sözleşme cümlesi. Doğrulayıcı yalnız olguyu
    /// ("İYS'ye ulaşılamadı") döndürüyor; "bundan sonra ne olacak" sorusunun
    /// cevabı yalnız BURADA bu: satır <see cref="NetgsmAccountStatus.Verified"/>
    /// kaldı ve yarınki tur onu yeniden tarayacak. Aynı cümle panel yolunda
    /// yalan olurdu (orada satır zaten <c>Failed</c> doğuyor ve tarama dışında
    /// kalıyor), bu yüzden doğrulayıcıda değil çağıranda duruyor.
    ///
    /// <para>Şifre çözülemeyen yol bu cümleyi ALMAZ: o dal
    /// <c>VerifyOneAsync</c> içinde erken dönüyor ve kendi metnini
    /// (<c>NetgsmAccountService.UndecryptableMessage</c>) yazıyor — anahtar
    /// dizini kendiliğinden düzelmeyeceği için "tekrar denenecek" demek
    /// yayıncıyı sonu gelmeyen bir beklemeye yollardı.</para>
    /// </summary>
    private const string RetryContract =
        "Kurulumunuz kapatılmadı, doğrulama kendiliğinden tekrar denenecek.";

    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly NetgsmAccountVerifier _verifier;
    private readonly ILogger<NetgsmAccountVerifyJob> _log;

    public NetgsmAccountVerifyJob(
        LicenseDbContext db,
        NetgsmAccountService accounts,
        NetgsmAccountVerifier verifier,
        ILogger<NetgsmAccountVerifyJob> log)
    {
        _db = db;
        _accounts = accounts;
        _verifier = verifier;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var accountId in await _accounts.ListVerifiedIdsAsync(ct))
        {
            try
            {
                await VerifyOneAsync(accountId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Bir kiracının turu düşerse sıradakine geçilir. Temizlik ŞART:
                // paylaşılan scoped DbContext'te bu hesabın kirli kayıtları
                // sıradakinin SaveChanges'ine biner ve yanlış satıra yazılırdı.
                _log.LogWarning(ex,
                    "NetgsmAccountVerifyJob: hesap {AccountId} turu düştü", accountId);
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task VerifyOneAsync(Guid accountId, CancellationToken ct)
    {
        var acc = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        // Liste alındıktan sonra admin kill switch'i çalışmış olabilir.
        if (acc is null || acc.Status != NetgsmAccountStatus.Verified) return;

        // Ağ çağrısından ÖNCE yakala: ret kararını yazarken "hangi sürümü
        // doğruladım" sorusunun cevabı bu. Çağrı sürerken yayıncı paneli
        // kaydedip şifreyi değiştirebilir ya da admin hesabı Disabled
        // yapabilir; o zaman elimizdeki ret ARTIK BAŞKA BİR HESABIN retidir.
        var expectedUpdatedAt = acc.UpdatedAt;
        var licenseId = acc.LicenseId;

        var password = _accounts.TryUnprotectPassword(acc.PasswordProtected);
        if (password is null)
        {
            acc.LastError = NetgsmAccountService.UndecryptableMessage;
            _db.Entry(acc).Property(a => a.UpdatedAt).IsModified = true;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var result = await _verifier.VerifyAsync(
            new IysAccountContext(acc.LicenseId, acc.UserCode, password, acc.BrandCode), ct);

        switch (result.Outcome)
        {
            case NetgsmVerifyOutcome.Ok:
                acc.LastVerifiedAt = DateTimeOffset.UtcNow;
                acc.LastError = null;
                break;

            case NetgsmVerifyOutcome.Rejected:
                _log.LogWarning(
                    "NetgsmAccountVerifyJob: lisans {LicenseId} kurulumu düştü — {Message}",
                    licenseId, result.Message);
                // Hesabı Failed yapmak TEK BAŞINA yetmiyor: akmakta olan bir
                // kampanya marka/onayları döngüden ÖNCE okuyor
                // (SmsCampaignSendJob.cs:111-133), yani kapı kapansa bile
                // kalan alıcılara gönderim sürer. §2.3 "düşen kurulum
                // gönderimi durdurur" diyorsa durdurması gereken yer burası.
                // Kampanyaları da duraklatıyoruz — hesap yazımıyla TEK
                // SaveChanges'te. `expectedUpdatedAt` kararı doğruladığımız
                // sürüme BAĞLAR: araya giren yazım varsa ret sessizce düşer.
                var paused = await _accounts.CloseAccountAndPauseCampaignsAsync(
                    accountId, NetgsmAccountStatus.Failed, result.Message, ct,
                    expectedUpdatedAt);
                if (paused > 0)
                    _log.LogWarning(
                        "NetgsmAccountVerifyJob: lisans {LicenseId} için {Count} kampanya duraklatıldı",
                        licenseId, paused);
                return;   // kaydı o metot yaptı; aşağıdaki SaveChanges'e düşme

            case NetgsmVerifyOutcome.Unavailable:
                // Doğrulayıcının teşhisini EZMİYORUZ, üstüne ekliyoruz:
                // `Unavailable`'ın iki biçimi var ve biri sanitize edilmiş
                // yanıt kodunu taşıyor ("beklenmeyen yanıt kodu (70)") —
                // yayıncının Netgsm'e danışırken söyleyeceği tek somut bilgi o.
                acc.LastError = $"{result.Message} {RetryContract}";
                break;
        }

        // Neden `acc.UpdatedAt = ...` değil: damgayı
        // `LicenseDbContext.StampNetgsmAccountVersions()` merkezî olarak atıyor
        // (`max(UtcNow, özgün + 1 tick)`). Burada elle `UtcNow` yazsaydık saat
        // ilerlemediğinde jeton yerinde kalırdı. `IsModified = true` yalnız
        // girdiyi `Modified` yapıp damgalayıcıyı tetikler — yalnız
        // `LastVerifiedAt`/`LastError` değiştiğinde bile sürümün ilerlemesini
        // garanti eder.
        _db.Entry(acc).Property(a => a.UpdatedAt).IsModified = true;
        await _db.SaveChangesAsync(ct);
    }
}
