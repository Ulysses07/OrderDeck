using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının kendi Netgsm + İYS kurulumu (spec §2). Dört alan girilir;
/// kaydetme anında <c>/iys/search</c> ile senkron doğrulanır.
///
/// <para><b>Owner-only.</b> Bunlar yayıncının faturalı Netgsm hesabının
/// kimlikleri; staff operatör ne görür ne değiştirir.</para>
///
/// <para><b>Şifre tek yön.</b> Panel yalnız <c>passwordSet</c> bayrağını
/// görür. Geri okunabilen bir alan, panel oturumu ele geçiren birine
/// yayıncının Netgsm hesabını da verirdi.</para>
/// </summary>
[ApiController]
[Route("api/panel/netgsm/account")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelNetgsmAccountController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly NetgsmAccountVerifier _verifier;
    private readonly Services.Iys.IysConsentCollector _consents;

    public PanelNetgsmAccountController(
        LicenseDbContext db, NetgsmAccountService accounts, NetgsmAccountVerifier verifier,
        Services.Iys.IysConsentCollector consents)
    {
        _db = db;
        _accounts = accounts;
        _verifier = verifier;
        _consents = consents;
    }

    /// <param name="Status">none | failed | verified | disabled.</param>
    /// <param name="SmsEnabled">Yetki tablosunun (spec §2.1) tek cevabı:
    /// kampanya ve onay toplama yalnız bu true iken açık.</param>
    public sealed record AccountView(
        string Status,
        bool SmsEnabled,
        string? UserCode,
        string? Header,
        string? BrandCode,
        bool PasswordSet,
        string? LastError,
        DateTimeOffset? LastVerifiedAt);

    private IActionResult? OwnerOnly() =>
        User.IsOperator()
            ? Problem(title: "owner-only",
                detail: "Netgsm kurulumunu yalnız hesap sahibi görüntüleyip değiştirebilir.",
                statusCode: 403)
            : null;

    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
    {
        if (OwnerOnly() is { } forbidden) return forbidden;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var acc = await _db.NetgsmAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);

        return Ok(ToView(acc));
    }

    /// <summary>
    /// Panel yolunun KENDİ sözleşme cümlesi. Doğrulayıcı yalnız olguyu
    /// döndürüyor ("İYS'ye ulaşılamadı" / "beklenmeyen yanıt kodu (70)");
    /// "bundan sonra ne olacak" sorusunun cevabı çağırana göre değişiyor ve
    /// burada tek doğru cevap bu: satır <c>UpsertAsync</c> tarafından zaten
    /// <c>Failed</c> yazıldı (fail-closed) ve günlük iş yalnız <c>Verified</c>
    /// satırları taradığı için bu satıra bir daha uğramayacak — yani kimse
    /// yayıncı adına tekrar denemeyecek.
    /// </summary>
    private const string SaveRetryContract = "Birkaç dakika sonra formu tekrar kaydedin.";

    public sealed record SaveRequest(
        string UserCode, string? Password, string Header, string BrandCode);

    [HttpPut]
    public async Task<IActionResult> SaveAsync([FromBody] SaveRequest req, CancellationToken ct)
    {
        if (OwnerOnly() is { } forbidden) return forbidden;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var userCode = (req.UserCode ?? "").Trim();
        var header = (req.Header ?? "").Trim();
        var brandCode = (req.BrandCode ?? "").Trim();

        if (userCode.Length is 0 or > 32)
            return Problem(title: "invalid-user-code",
                detail: "Netgsm abone numarası zorunlu (en fazla 32 karakter).", statusCode: 400);
        if (header.Length is 0 or > 11)
            return Problem(title: "invalid-header",
                detail: "Gönderici başlığı zorunlu (en fazla 11 karakter).", statusCode: 400);
        // Rakam dışı karakteri kapıda kesiyoruz: DB'deki
        // CK_NetgsmAccounts_BrandCode aynı kuralı uyguluyor ama oraya varmak
        // DbUpdateException → 500 demek olurdu.
        if (brandCode.Length is 0 or > 16 || !brandCode.All(char.IsAsciiDigit))
            return Problem(title: "invalid-brand-code",
                detail: "İYS marka kodu yalnız rakamlardan oluşur.", statusCode: 400);

        var existing = await _db.NetgsmAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);
        if (existing?.Status == NetgsmAccountStatus.Disabled)
            return Problem(title: "netgsm-account-disabled",
                detail: "Netgsm kurulumunuz yönetici tarafından kapatıldı. "
                        + "Yeniden açılması için destekle iletişime geçin.",
                statusCode: 409);

        // Marka kodu DOĞRULANMIŞ hesaplar arasında tekil (filtreli indeks,
        // Görev 7). Dış çağrıdan ÖNCE bakıyoruz: yoksa yayıncının tek
        // öğreneceği şey bir 500 olurdu.
        var squatted = await _db.NetgsmAccounts.AsNoTracking().AnyAsync(
            a => a.BrandCode == brandCode
                 && a.Status == NetgsmAccountStatus.Verified
                 && a.LicenseId != licenseId.Value, ct);
        if (squatted)
            return Problem(title: "brand-code-taken",
                detail: "Bu İYS marka kodu başka bir hesapta doğrulanmış durumda.",
                statusCode: 409);

        // --- Doğrulama penceresini KAPAT: SÜRÜM JETONU, reload DEĞİL ---
        // VerifyAsync bir AĞ çağrısı; saniyeler sürebilir. O aralıkta satır
        // değişmiş olabilir ve sonucu körlemesine yazmanın üç somut zararı var:
        //
        //  1. Admin bu arada hesabı `Disabled` yaptıysa, bizim `Ok`'umuz kapatma
        //     anahtarını sessizce geri alır — Görev 6'nın "Disabled yapışkan"
        //     garantisi tam da burada çöker.
        //  2. Aynı yayıncıdan ikinci bir PUT başka kimlikleri yazdıysa, bizim
        //     `Ok`'umuz BAŞKASININ doğrulanmamış kimliklerini `Verified` yapar.
        //     EF yalnız değişen sütunları yazdığı için `UserCode` korunur ama
        //     satır yine de açılır: fail-closed sözleşmesi delinir.
        //  3. O ikinci PUT yalnız **parolayı** değiştirdiyse alan karşılaştırması
        //     bunu göremez: `UserCode` ve `BrandCode` aynı kalır, satır açılır ve
        //     hiç doğrulanmamış bir parola `Verified` damgası alır.
        //
        // Bu yüzden `ReloadAsync` + alan karşılaştırması YAPMIYORUZ. Reload,
        // EF'in ÖZGÜN değerlerini de tazeler — yani tam da yarışı yakalayacak
        // kanıtı siler. Onun yerine `UpsertAsync`'ten dönen İZLENEN nesnenin
        // özgün `UpdatedAt` değeri son yazıma kadar korunur; Görev 3'te eklenen
        // eşzamanlılık jetonu `WHERE UpdatedAt = @original` üretir. Araya giren
        // HERHANGİ bir yazım (parola dahil) sürümü ilerletmiş olur ve
        // `SaveChanges` sıfır satır etkiler → `DbUpdateConcurrencyException`.
        try
        {
            NetgsmAccount account;
            try
            {
                account = await _accounts.UpsertAsync(
                    licenseId.Value, userCode, req.Password, header, brandCode, ct);
            }
            // Muhafız DAR: yalnız `rawPassword` parametresini suçlayan istisna
            // "şifre zorunlu" mesajına çevrilir (`UpsertAsync` bunu `nameof` ile
            // fırlatıyor, sözleşme o metodun doc'unda). Çıplak `catch
            // (ArgumentException)` ileride başka bir parametreden — ya da bir
            // bağımlılıktan — gelen istisnayı da yutar ve yayıncıyı olmayan bir
            // şifre sorununa yönlendirirdi. Beklenmeyen `ArgumentException`
            // buradan kaçıp 500 üretsin: fail-closed olan budur.
            catch (ArgumentException ex) when (ex.ParamName == "rawPassword")
            {
                return Problem(title: "password-required",
                    detail: "İlk kayıtta Netgsm API şifresi zorunlu.", statusCode: 400);
            }

            // Satır şu an Failed: doğrulama düşse bile kapı KAPALI kalır.
            // Şifre çözülemezse DIŞ ÇAĞRI YAPILMAZ — ama erken `return`
            // etmiyoruz: `LastError` yazımı da aynı CAS korumasından geçmeli,
            // yoksa bayat bir istek kapatılmış hesaba hata metni yazabilir.
            var password = _accounts.TryUnprotectPassword(account.PasswordProtected);

            NetgsmVerifyResult result;
            if (password is null)
            {
                // Bu dalın metni KENDİ bağlamını anlatıyor (Görev 9): dış çağrı
                // hiç yapılmadı, sorun anahtar dizininde. Aşağıdaki panel
                // cümlesi buna EKLENMEMELİ — "birkaç dakika sonra tekrar
                // kaydedin" demek, kaydetmekle çözülmeyecek bir şeyi yayıncıya
                // tekrar tekrar denetmek olurdu.
                result = new NetgsmVerifyResult(
                    NetgsmVerifyOutcome.Unavailable, NetgsmAccountService.UndecryptableMessage);
            }
            else
            {
                result = await _verifier.VerifyAsync(
                    new Services.Iys.IysAccountContext(
                        account.LicenseId, account.UserCode, password, account.BrandCode),
                    ct);

                // Doğrulayıcının metnini EZMİYORUZ, kendi sözleşme cümlemizi
                // EKLİYORUZ. Gerekçe: `Unavailable`'ın İKİ biçimi var ve yalnız
                // biri panelde eksik. "İYS'ye ulaşılamadı" olgusu her iki
                // bağlamda da doğru ama tek başına yayıncıya ne yapacağını
                // söylemiyor; "İYS beklenmeyen yanıt kodu döndürdü (70). Sorun
                // sürerse Netgsm'e danışın" ise HEM doğru HEM de sanitize
                // edilmiş kodu taşıyor — destek ekibinin isteyeceği tek somut
                // bilgi o. Toptan değiştirme (ilk uygulama) o teşhisi siliyor ve
                // bozuk bir yanıt biçimi için "bekle, tekrar kaydet" diye yanlış
                // tavsiye veriyordu.
                //
                // Cümleyi doğrulayıcıda bağlama göre dallandırmak da YANLIŞ
                // olurdu: doğrulayıcı kendisini kimin çağırdığını bilmemeli, o
                // bilgi çağırandadır. Kardeşi `NetgsmAccountVerifyJob` aynı yerde
                // kendi cümlesini ("kapatılmadı, kendiliğinden tekrar denenecek")
                // ekliyor.
                //
                // Ayrımı `Outcome`'a değil DALA bakarak yapıyoruz — şifre
                // çözülemeyen yol da `Unavailable` üretiyor ama onun metnine bu
                // cümle EKLENMEMELİ (Görev 9).
                if (result.Outcome == NetgsmVerifyOutcome.Unavailable)
                    result = result with { Message = $"{result.Message} {SaveRetryContract}" };
            }

            if (result.Outcome == NetgsmVerifyOutcome.Ok)
            {
                account.Status = NetgsmAccountStatus.Verified;
                account.LastVerifiedAt = DateTimeOffset.UtcNow;
                account.LastError = null;

                // Kurulum geri geldi: admin kapatmasıyla duraklatılmış
                // kampanyalar devam etsin. Kayıt AŞAĞIDAKİ tek SaveChanges'te —
                // hesabın Verified'ı ile kampanyaların pending'i ya birlikte
                // iner ya hiç inmez. Ayrılsalardı aradaki çökme kampanyaları
                // paused'da bırakırdı ve hiçbir süpürme onları bulmazdı.
                await _accounts.StageResumePausedCampaignsAsync(account.LicenseId, ct);

                // Kurulum kapalıyken gelen ONAY/RET olayları yalnız olay
                // tablosuna düşmüştü (`ErrorCode="no-brand"`): markası
                // çözülemediği için durum satırına hiç uygulanmadılar. Marka
                // ARTIK doğrulandı — reddi şimdi uygulamazsak gönderim kapısı
                // bayat `Onay`'ı kabul eder ve onayını geri çekmiş kişiye ticari
                // SMS gider (6563 ihlali); penceresi (3 iş günü) hâlâ açık
                // onayları uygulamazsak kurulum bitmeden toplanan izinler
                // kaybolur. Aynı SaveChanges'te olması şart: ayrılsalardı
                // aradaki çökme hesabı açık, olayları düşmüş bırakırdı.
                //
                // Yalnız burada çağrılıyor çünkü `Failed → Verified` geçişinin
                // TEK yolu bu PUT: günlük iş yalnız `Verified` hesapları tarar,
                // admin "Aç" düğmesi `Failed` yazar (Görev 12).
                await _consents.StageReplayNoBrandEventsAsync(
                    account.LicenseId, account.BrandCode, ct);
            }
            else
            {
                account.LastError = result.Message;
            }

            // Sonuç başka hiçbir sütunu değiştirmese bile (örn. `Failed` satıra
            // AYNI `LastError` yazıldı) bir UPDATE üretilmeli: UPDATE yoksa
            // `WHERE UpdatedAt = @original` hiç koşmaz ve CAS sessizce atlanır.
            // `IsModified = true` damgalamayı da tetikler (Görev 3'teki
            // `StampNetgsmAccountVersions` yalnız `Modified` girdilere bakar).
            _db.Entry(account).Property(a => a.UpdatedAt).IsModified = true;
            await _db.SaveChangesAsync(ct);

            return Ok(ToView(account));
        }
        catch (NetgsmAccountDisabledException)
        {
            // Ön kontrolle `UpsertAsync` arasında admin kapattı: servis
            // guard'ı (Görev 3) bize haber verdi. Ön kontrol bu yarışı
            // KAPATMAZ, yalnız tipik durumda anlaşılır cevap verir —
            // fail-closed garantisi servisteki guard'dan gelir.
            _db.ChangeTracker.Clear();
            return Problem(title: "netgsm-account-disabled",
                detail: "Netgsm kurulumunuz yönetici tarafından kapatıldı. "
                        + "Yeniden açılması için destekle iletişime geçin.",
                statusCode: 409);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Doğruladığımız sürüm artık satırda durmuyor. Sonucu ATIYORUZ —
            // yazmak, yukarıdaki üç zarardan birini üretmek olurdu.
            //
            // `Clear()` BUGÜN gözlemlenebilir bir etki üretmiyor ve bu yüzden
            // testle ölçülemiyor: istek `Problem` ile bitiyor, scoped DbContext
            // bir daha `SaveChanges` görmüyor. Yine de duruyor, çünkü izlenen
            // kirli varlığı bağlamda bırakmak sonraki görevlerde tehlikeli olur —
            // aynı istek içinde ikinci bir yazım yolu açıldığı anda (Görev 6'nın
            // `Disabled` → 409 dalı, Görev 13'ün devam ettirme yazımı) bu varlık
            // kimsenin karar vermediği bir anda diske basılır. Kardeşi
            // `NetgsmAccountService.UpsertAsync`'te aynı gerekçeyle var.
            _db.ChangeTracker.Clear();
            return Problem(title: "verification-superseded",
                detail: "Kurulum, doğrulama sürerken değişti. Formu tekrar kaydedin.",
                statusCode: 409);
        }
        catch (DbUpdateException ex) when (IsUniqueIndexConflict(ex, "BrandCode"))
        {
            // Ön kontrol ile kayıt arasında başka kiracı aynı markayı
            // doğruladı. Filtreli tekil indeks kesin kararı verdi; bizimki
            // Failed kalmalı ve yayıncı anlaşılır bir cevap almalı.
            _db.ChangeTracker.Clear();
            return Problem(title: "brand-code-taken",
                detail: "Bu İYS marka kodu başka bir hesapta doğrulanmış durumda.",
                statusCode: 409);
        }
        catch (DbUpdateException ex) when (IsUniqueIndexConflict(ex, "LicenseId"))
        {
            // Aynı lisans için İKİ sekmeden eşzamanlı İLK kayıt: ikisi de
            // "satır yok" görüp INSERT üretti, kaybeden
            // `IX_NetgsmAccounts_LicenseId`'den 2601 aldı. Marka indeksiyle
            // aynı hata kodu ama BAŞKA bir olay — ve yayıncının yapması gereken
            // şey de başka: markası elinden gitmedi, kurulumu zaten kaydedildi.
            // Filtre yalnız markayı tanıdığı sürece istisna dışarı kaçıp 500
            // üretiyordu.
            //
            // `DbUpdateConcurrencyException` dalı bu yarışı YAKALAMAZ: orada
            // var olan bir satırın sürümü kayıyor, burada satır henüz YOK —
            // CAS'ın koruyacağı bir özgün değer üretilmemiş durumda.
            _db.ChangeTracker.Clear();
            return Problem(title: "netgsm-account-concurrent-create",
                detail: "Kurulumunuz başka bir sekmede kaydedildi. "
                        + "Sayfayı yenileyip tekrar deneyin.",
                statusCode: 409);
        }
    }

    /// <summary>
    /// <c>DbUpdateException</c>, adı <paramref name="indexName"/> geçen tekil
    /// indeksin ihlali mi? 2601/2627 = unique index/constraint ihlali; indeks
    /// adı filtresi, aynı hata koduyla gelen BAŞKA yarışların (ve tamamen
    /// ilgisiz DB arızalarının) yanlış etiketlenmesini önler. Filtresiz bir
    /// <c>catch (DbUpdateException)</c>, taşan bir <c>LastError</c>'ı ya da
    /// kopan bir bağlantıyı da yayıncıya "marka kodunuz başkasında" diye
    /// gösterirdi — yanlış yeri saatlerce aratır.
    ///
    /// <para><b>Neden ad parametreli.</b> Bu denetleyicide iki farklı tekil
    /// indeks ihlali iki farklı cevaba çıkıyor (marka işgali / eşzamanlı ilk
    /// kayıt); tek bir sabite gömülü ad, ikinci ihlali sessizce 500'e
    /// düşürüyordu.</para>
    ///
    /// <para><b>Neden <c>public</c>.</b> Yarış InMemory'de üretilemiyor (tekil
    /// indeks uygulanmıyor), yani karar yalnız saf fonksiyon olarak
    /// sınanabilir. Sunucu projesinde <c>InternalsVisibleTo</c> yok; aynı
    /// durumda Görev 9 da <c>ToView</c>'u <c>public static</c> yapmıştı —
    /// kalıbı bozmamak için aynısı. Statik olduğu için MVC bunu eylem
    /// saymaz.</para>
    ///
    /// <para>Kalıp: <c>PanelCustomerBalanceController.IsDuplicateReversal</c>
    /// (PanelCustomerBalanceController.cs:335).</para>
    /// </summary>
    public static bool IsUniqueIndexConflict(DbUpdateException ex, string indexName) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains(indexName, StringComparison.Ordinal);

    public static AccountView ToView(NetgsmAccount? acc) => acc is null
        ? new AccountView("none", false, null, null, null, false, null, null)
        : new AccountView(
            Status: acc.Status switch
            {
                NetgsmAccountStatus.Verified => "verified",
                NetgsmAccountStatus.Disabled => "disabled",
                _ => "failed",
            },
            SmsEnabled: acc.Status == NetgsmAccountStatus.Verified,
            UserCode: acc.UserCode,
            Header: acc.Header,
            BrandCode: acc.BrandCode,
            PasswordSet: !string.IsNullOrEmpty(acc.PasswordProtected),
            LastError: acc.LastError,
            LastVerifiedAt: acc.LastVerifiedAt);
}
