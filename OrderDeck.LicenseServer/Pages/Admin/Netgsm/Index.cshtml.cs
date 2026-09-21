using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Pages.Admin.Netgsm;

/// <summary>
/// Yayıncı Netgsm/İYS kurulumlarının yönetici görünümü + kapatma anahtarı.
///
/// <para><b>[Authorize] YOK — kasıtlı.</b> <c>Program.cs</c>'teki
/// <c>AuthorizeFolder("/Admin", "AdminOnly")</c> tüm klasörü kapsıyor;
/// sayfaya ayrıca öznitelik koymak ikinci bir doğruluk kaynağı yaratır
/// (bkz. komşu <c>Pages/Admin/Iys/Index.cshtml.cs</c>).</para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, NetgsmAccountService accounts, IAuditService audit)
    {
        _db = db;
        _accounts = accounts;
        _audit = audit;
    }

    public sealed record Row(
        Guid AccountId,
        Guid LicenseId,
        string CustomerEmail,
        string UserCode,
        string Header,
        string BrandCode,
        NetgsmAccountStatus Status,
        string? LastError,
        DateTimeOffset? LastVerifiedAt,
        int ActiveCampaigns);

    [BindProperty]
    public Guid AccountId { get; set; }

    public List<Row> Rows { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Rows = await _db.NetgsmAccounts.AsNoTracking()
            .OrderBy(a => a.Status).ThenBy(a => a.UpdatedAt)
            .Select(a => new Row(
                a.Id,
                a.LicenseId,
                _db.Licenses.Where(l => l.Id == a.LicenseId)
                    .Select(l => l.Customer.Email).FirstOrDefault() ?? "(bilinmiyor)",
                a.UserCode,
                a.Header,
                a.BrandCode,
                a.Status,
                a.LastError,
                a.LastVerifiedAt,
                _db.SmsCampaigns.Count(c => c.LicenseId == a.LicenseId
                    && (c.Status == "pending" || c.Status == "sending"))))
            .ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken ct)
    {
        // AsNoTracking bilinçli: asıl yazımı servis yapıyor ve çakışmada
        // ChangeTracker'ı temizliyor. Burada izlenen bir kopya tutarsak o
        // temizlik onu da kopartır ve elimizde yarı-geçerli bir nesne kalır.
        var licenseId = await _db.NetgsmAccounts.AsNoTracking()
            .Where(a => a.Id == AccountId)
            .Select(a => (Guid?)a.LicenseId)
            .FirstOrDefaultAsync(ct);
        if (licenseId is null) return NotFound();

        // Kapatma + kampanya duraklatma TEK yerde: Görev 8'in kesin-ret dalı
        // da aynı metodu çağırıyor. İki kopya olsaydı biri ileride "sending"i
        // ya da retry'ı unutur, kapatma sessizce yarım kalırdı. Metot
        // "pending" VE "sending" kampanyaları hesabın yazımıyla aynı
        // SaveChanges'te duraklatır; "pending" bırakmak yetmezdi, çünkü
        // SmsCampaignRecoveryJob iki dakika sonra onu kuyruğa alıp kapatma
        // kararını sessizce geri alırdı.
        int pausedCampaigns;
        try
        {
            pausedCampaigns = await _accounts.CloseAccountAndPauseCampaignsAsync(
                AccountId, NetgsmAccountStatus.Disabled,
                "Yönetici tarafından kapatıldı.", ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Retry döngüsü dört turda da çakıştı (Görev 8). Yönetici kararı
            // bilinçli olarak jetonsuz, yani "kaybetmez" — ama sonsuz da
            // denemez. Buraya düşmek satırın o anda başka bir yazarla
            // dövüştüğü anlamına gelir; yakalamazsak yöneticiye 500 gider ve
            // kapatmanın gerçekleşip gerçekleşmediğini bilmez. Servis
            // fırlatmadan önce ChangeTracker'ı temizliyor, bu scope'ta
            // yarı-yazılmış bir nesne kalmıyor.
            TempData["Error"] =
                "Kurulum şu anda başka bir işlemle güncelleniyor. Tekrar deneyin.";
            return RedirectToPage();
        }


        await _audit.LogAsync(
            AuditEvents.NetgsmAccountDisable, AuditTargets.NetgsmAccount,
            AccountId.ToString(),
            new { licenseId, pausedCampaigns }, ct);

        TempData["Success"] = pausedCampaigns == 0
            ? "Kurulum kapatıldı."
            : $"Kurulum kapatıldı, {pausedCampaigns} kampanya duraklatıldı.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken ct)
    {
        var acc = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.Id == AccountId, ct);
        if (acc is null) return NotFound();

        // "Aç" YALNIZ "Kapat"ın geri alınmasıdır. Aşağıdaki CAS eşzamanlılığı
        // koruyor ama bayat SAYFAYI korumuyor: iki yönetici listeyi hesap
        // Disabled'ken açar, biri açar, kurulum panelden doğrulanıp Verified
        // olur, sonra ikincisi hâlâ "Aç" düğmesini gösteren ekranından
        // basarsa TEK yazım olur, jeton eşleşir, CAS geçer — ve canlı bir
        // Verified kurulum Failed'a düşer. Failed marka çözemediği için o
        // yayıncının gönderimi sessizce durur, üstelik kampanyalar
        // duraklatılmadığı için rezerve krediler asılı kalır. Durum kapısı
        // bunu kapatıyor: Failed ya da Verified bir hesapta "aç" anlamsız.
        if (acc.Status != NetgsmAccountStatus.Disabled)
        {
            _db.ChangeTracker.Clear();
            // Burası bir Razor Page: JSON dönmek yöneticiyi çıplak bir gövdeye
            // düşürür ve tam da okuması gereken şeyi — hesabın GÜNCEL durumunu —
            // ekrandan siler. Mesajın kendisi "sayfayı yenileyin" diyor; o
            // yenilemeyi biz yapıyoruz. Kardeş OnPostDisableAsync de böyle.
            TempData["Error"] = "Kurulum zaten açık. Sayfayı yenileyin.";
            return RedirectToPage();
        }

        // Verified DEĞİL: yönetici markanın İYS'de hâlâ geçerli olduğunu
        // bilemez. Doğrulama tek yoldan — panel kaydı ya da günlük iş — geçer.
        // Duraklatılmış kampanyalar da burada devam ettirilmez; devam,
        // doğrulamanın başarılı olduğu anda gelir.
        acc.Status = NetgsmAccountStatus.Failed;
        acc.LastError = null;
        acc.DisabledAt = null;
        // Damgayı LicenseDbContext merkezî olarak atıyor (Görev 3); burada
        // elle UtcNow yazmak, saat ilerlemediğinde jetonu yerinde bırakırdı.
        _db.Entry(acc).Property(a => a.UpdatedAt).IsModified = true;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Yönetici sayfayı açtıktan sonra biri hesabı yazmış: yayıncı
            // panelden kaydetmiş ya da günlük iş sonuç yazmış olabilir.
            // Ekrandaki "Disabled" artık gerçeği göstermiyor, dolayısıyla
            // "aç" kararı da bayat — sessizce uygulamak yerine yöneticiye
            // güncel hâli gösteriyoruz.
            _db.ChangeTracker.Clear();
            TempData["Error"] =
                "Kurulum başka bir işlemle değişti. Sayfayı yenileyin.";
            return RedirectToPage();
        }


        await _audit.LogAsync(
            AuditEvents.NetgsmAccountEnable, AuditTargets.NetgsmAccount,
            acc.Id.ToString(), new { licenseId = acc.LicenseId }, ct);

        TempData["Success"] = "Kurulum açıldı — doğrulama bekleniyor.";
        return RedirectToPage();
    }
}
