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

    public PanelNetgsmAccountController(
        LicenseDbContext db, NetgsmAccountService accounts, NetgsmAccountVerifier verifier)
    {
        _db = db;
        _accounts = accounts;
        _verifier = verifier;
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

            var result = password is null
                ? new NetgsmVerifyResult(
                    NetgsmVerifyOutcome.Unavailable, NetgsmAccountService.UndecryptableMessage)
                : await _verifier.VerifyAsync(
                    new Services.Iys.IysAccountContext(
                        account.LicenseId, account.UserCode, password, account.BrandCode),
                    ct);

            if (result.Outcome == NetgsmVerifyOutcome.Ok)
            {
                account.Status = NetgsmAccountStatus.Verified;
                account.LastVerifiedAt = DateTimeOffset.UtcNow;
                account.LastError = null;
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
        catch (DbUpdateException ex) when (IsBrandCodeConflict(ex))
        {
            // Ön kontrol ile kayıt arasında başka kiracı aynı markayı
            // doğruladı. Filtreli tekil indeks kesin kararı verdi; bizimki
            // Failed kalmalı ve yayıncı anlaşılır bir cevap almalı.
            _db.ChangeTracker.Clear();
            return Problem(title: "brand-code-taken",
                detail: "Bu İYS marka kodu başka bir hesapta doğrulanmış durumda.",
                statusCode: 409);
        }
    }

    /// <summary>
    /// <c>DbUpdateException</c>, marka kodu tekil indeksinin ihlali mi?
    /// 2601/2627 = unique index/constraint ihlali; indeks adı filtresi, aynı
    /// hata koduyla gelen BAŞKA yarışların (ve tamamen ilgisiz DB
    /// arızalarının) "marka kodu dolu" diye yanlış etiketlenmesini önler.
    /// Filtresiz bir <c>catch (DbUpdateException)</c>, taşan bir
    /// <c>LastError</c>'ı ya da kopan bir bağlantıyı da yayıncıya "marka
    /// kodunuz başkasında" diye gösterirdi — yanlış yeri saatlerce aratır.
    /// Kalıp: <c>PanelCustomerBalanceController.IsDuplicateReversal</c>
    /// (PanelCustomerBalanceController.cs:335).
    /// </summary>
    private static bool IsBrandCodeConflict(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains("BrandCode", StringComparison.Ordinal);

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
