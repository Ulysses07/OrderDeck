using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Pages.Admin.Obifin;

/// <summary>Pilot yönetimi (spec §8): Obifin kimliği, doğrulama, banka bağlantısı, hesaplar, "Şimdi çek".
/// [Authorize] yok — Program.cs `AuthorizeFolder("/Admin")`. Şifre/API key asla görünüme çıkmaz; banka web servis
/// kimlikleri (<c>Field_*</c>) yalnız isteğin belleğinde yaşar: TempData'ya, audit'e, loga, sayfaya girmez.
/// <para><b>Banka modülü kapalıyken de sayfa açılır.</b> <see cref="BankHasher"/> anahtar yoksa kurucusunda düşen bir
/// singleton; ona bağlı <see cref="ObifinConnectionService"/> kurucuya enjekte edilseydi sayfa 500 verirdi. Servis
/// yalnız modül açıkken, işleyicinin içinde çözülür; kapalıyken her POST <see cref="BankHasher.DisabledMessage"/> ile
/// döner ve Obifin'e dokunmaz.</para></summary>
public class IndexModel : PageModel
{
    public const string ResetConfirmMessage =
        "Kimlik değişikliği bu lisansın banka verisini siler (hareketler, eşleşmeler, IBAN hafızası). Onaylamak için kutuyu işaretleyip tekrar kaydedin.";
    public const string NotVerifiedMessage = "Bağlantı doğrulanmamış; önce Doğrula.";

    private readonly LicenseDbContext _db;
    private readonly IServiceProvider _services;
    private readonly IBackgroundJobClient _jobs;
    private readonly IAuditService _audit;
    private readonly BankOptions _bank;

    public IndexModel(LicenseDbContext db, IServiceProvider services, IBackgroundJobClient jobs, IAuditService audit,
        IOptions<BankOptions> bank)
    { _db = db; _services = services; _jobs = jobs; _audit = audit; _bank = bank.Value; }

    public sealed record ConnectionRow(Guid LicenseId, string CustomerEmail, string BaseUrl, string UserCode,
        ObifinConnectionStatus Status, string? LastError, DateTimeOffset? LastVerifiedAt, DateTimeOffset? LastPolledAt,
        long? Cursor, DateTimeOffset? BackfillCompletedAt, int BankConnections, int Accounts, int Transactions);
    public sealed record AccountRow(string BankaKodu, string IbanMasked, string Currency, bool Active,
        DateTimeOffset? LastBankSyncAt, string? NotificationNote);
    public sealed record LicenseOption(Guid Id, string Label);
    /// <summary>Düzenlenen bağlantı: formda yalnız "kayıtlı" bayrağı — değerler asla geri basılmaz.</summary>
    public sealed record EditTarget(bool PasswordStored, bool ApiKeyStored);

    /// <summary>Banka kodu → Obifin'in `bankaapi/ekle` formunda beklediği alanlar (Postman v1.03.06).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> BankFields = new Dictionary<string, string[]>
    {
        ["qnb"] = ["KullaniciAdi", "Sifre", "Url"],
        ["qnbapi"] = ["ClientId", "ClientSecret", "AccessToken", "RefreshToken"],
        ["garanti"] = ["KullaniciAdi", "Sifre", "FirmaKodu"],
        ["garantibbvaapi"] = ["TanimNumarasi"],
        ["isbank"] = ["KullaniciAdi", "Sifre"],
        ["yapikredi"] = ["KullaniciAdi", "Sifre"],
        ["ziraat"] = ["KullaniciAdi", "Sifre"],
        ["akbank"] = ["FirmaAnahtar", "KullaniciAdi", "Sifre"],
        ["papara"] = ["APIKey", "APISecret"],
    };

    [BindProperty] public Guid LicenseId { get; set; }
    [BindProperty] public string? BaseUrl { get; set; }
    [BindProperty] public string? UserCode { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? ApiKey { get; set; }
    /// <summary>Kimlik değişimi gölge veriyi siler; açık onay olmadan kaydedilmez (<see cref="OnPostSaveAsync"/>).</summary>
    [BindProperty] public bool ConfirmReset { get; set; }
    [BindProperty] public string? BankaKodu { get; set; }
    [BindProperty] public string? Label { get; set; }

    public List<ConnectionRow> Rows { get; private set; } = new();
    public Dictionary<Guid, List<AccountRow>> AccountsByLicense { get; private set; } = new();
    public List<LicenseOption> Licenses { get; private set; } = new();
    /// <summary>Banka ekleme formunun lisansları: yalnız Obifin bağlantısı olanlar — <see cref="ObifinConnectionService.AddBankConnectionAsync"/>
    /// başkasında çalışmaz.</summary>
    public List<LicenseOption> BankLicenses { get; private set; } = new();
    public EditTarget? Editing { get; private set; }
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    /// <summary>Açılıştaki kararın aynısı (Program.cs): anahtar yoksa banka modülü kapalı.</summary>
    public bool BankDisabled => !BankHasher.IsValidKey(_bank.HashKey);

    /// <summary>Yalnız modül açıkken çözülür (bkz. sınıf özeti).</summary>
    private ObifinConnectionService Connections => _services.GetRequiredService<ObifinConnectionService>();

    /// <summary><paramref name="license"/> verilirse kimlik formu o bağlantının kullanıcı kodu ve adresiyle dolar: değişmeden
    /// kaydedilen form hiçbir zaman gölge veri sıfırlamasına yol açmaz.</summary>
    public async Task OnGetAsync(Guid? license, CancellationToken ct)
    {
        Rows = await _db.ObifinConnections.AsNoTracking().OrderBy(c => c.CreatedAt)
            .Select(c => new ConnectionRow(c.LicenseId,
                _db.Licenses.Where(l => l.Id == c.LicenseId).Select(l => l.Customer.Email).FirstOrDefault() ?? "(bilinmiyor)",
                c.BaseUrl, c.UserCode, c.Status, c.LastError, c.LastVerifiedAt, c.LastPolledAt, c.LastObifinTransactionId,
                c.BackfillCompletedAt,
                _db.BankConnections.Count(b => b.LicenseId == c.LicenseId && b.Status == BankConnectionStatus.Active),
                _db.BankAccounts.Count(a => a.LicenseId == c.LicenseId),
                _db.BankTransactions.Count(t => t.LicenseId == c.LicenseId)))
            .ToListAsync(ct);
        var licenseIds = Rows.Select(r => r.LicenseId).ToList();
        AccountsByLicense = (await _db.BankAccounts.AsNoTracking().Where(a => licenseIds.Contains(a.LicenseId)).ToListAsync(ct))
            .GroupBy(a => a.LicenseId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AccountRow(a.BankaKodu, a.IbanMasked, a.Currency, a.Active, a.LastBankSyncAt, a.NotificationNote)).ToList());
        // Seçim listesi e-postaya göre ilk 200 lisansla sınırlı; Obifin bağlantısı olan lisanslar ise HER ZAMAN listede
        // (düzenlenen lisans da: düzenleme ancak bağlantısı olan lisansta açılır). Listede olmayan lisans seçili gelemez;
        // tarayıcı ilk seçeneği gönderir ve form, gösterdiği kimliği ya da banka kimliğini BAŞKA bir lisansa yazar.
        var firstPage = _db.Licenses.OrderBy(l => l.Customer.Email).Take(200).Select(l => l.Id);
        Licenses = await _db.Licenses.AsNoTracking()
            .Where(l => licenseIds.Contains(l.Id) || firstPage.Contains(l.Id))
            .OrderBy(l => l.Customer.Email)
            .Select(l => new LicenseOption(l.Id, l.Customer.Email + " · " + l.LicenseKey)).ToListAsync(ct);
        var connected = licenseIds.ToHashSet();
        BankLicenses = Licenses.Where(l => connected.Contains(l.Id)).ToList(); // Licenses bağlantılıları hep içerir (yukarıda)

        if (license is { } id
            && await _db.ObifinConnections.AsNoTracking().FirstOrDefaultAsync(c => c.LicenseId == id, ct) is { } conn)
        {
            LicenseId = conn.LicenseId; BaseUrl = conn.BaseUrl; UserCode = conn.UserCode;
            Editing = new EditTarget(conn.PasswordProtected.Length > 0, conn.ApiKeyProtected.Length > 0);
        }
    }

    /// <summary>Kullanıcı kodu ya da adres değişir ve lisansın gölge verisi varsa, <see cref="ConfirmReset"/> işaretli
    /// değilse HİÇBİR ŞEY kaydedilmez: <see cref="ObifinConnectionService.UpsertAsync"/> o veriyi geri dönüşsüz siler.</summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (BankDisabled) return BankDisabledResult();
        if (!await _db.Licenses.AnyAsync(l => l.Id == LicenseId, ct))
        {
            TempData["Error"] = "Lisans bulunamadı.";
            return RedirectToPage();
        }
        var existing = await _db.ObifinConnections.AsNoTracking().FirstOrDefaultAsync(c => c.LicenseId == LicenseId, ct);
        var shadowReset = existing is not null && IsIdentityChange(existing, BaseUrl, UserCode)
            && await HasShadowDataAsync(LicenseId, ct);
        if (shadowReset && !ConfirmReset)
        {
            TempData["Error"] = ResetConfirmMessage;
            return RedirectToPage();
        }
        try
        {
            await Connections.UpsertAsync(LicenseId, BaseUrl ?? "", UserCode ?? "", Password, ApiKey, ct);
            await _audit.LogAsync(AuditEvents.ObifinConnectionSave, AuditTargets.ObifinConnection, LicenseId.ToString(),
                new { UserCode = UserCode?.Trim(), shadowDataReset = shadowReset }, ct);
            TempData["Success"] = shadowReset
                ? "Obifin kimliği kaydedildi, eski hesabın banka verisi silindi. Şimdi doğrulayın."
                : "Obifin kimliği kaydedildi. Şimdi doğrulayın.";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken ct)
    {
        if (BankDisabled) return BankDisabledResult();
        ObifinVerifyResult result;
        try
        {
            result = await Connections.VerifyAsync(LicenseId, ct);
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.ObifinConnectionVerify, AuditTargets.ObifinConnection, LicenseId.ToString(), new { result.Ok, result.AccountCount }, ct);
        if (result.Ok) TempData["Success"] = $"Doğrulandı: {result.AccountCount} hesap.";
        else TempData["Error"] = "Doğrulanamadı: " + result.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddBankAsync(CancellationToken ct)
    {
        if (BankDisabled) return BankDisabledResult();
        var banka = (BankaKodu ?? "").Trim().ToLowerInvariant();
        if (!BankFields.TryGetValue(banka, out var fields)) { TempData["Error"] = "Bilinmeyen banka kodu."; return RedirectToPage(); }
        // Alanlar Field_<Ad> olarak gelir; yalnız bu isteğin belleğinde yaşar, loglanmaz, saklanmaz.
        var form = new Dictionary<string, string>();
        foreach (var f in fields)
        {
            var v = Request.Form["Field_" + f].ToString();
            if (!string.IsNullOrWhiteSpace(v)) form[f] = v.Trim();
        }
        var connections = Connections;
        BankConnection bc;
        try
        {
            bc = await connections.AddBankConnectionAsync(LicenseId, banka, Label ?? "", form, ct);
        }
        catch (Exception ex) when (DescribeFailure(ex, ct) is { } msg)
        {
            TempData["Error"] = "Banka bağlantısı eklenemedi: " + msg;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.ObifinBankAdd, AuditTargets.ObifinConnection, LicenseId.ToString(), new { banka, bc.BankaApiId }, ct);
        // Bağlantı Obifin'de ve yerelde AÇILDI: hesap yenilemesi düşerse "eklenemedi" demek yanlış olur (admin tekrar
        // ekler, Obifin'de ikinci kayıt açılır). Saatlik yenileme işi hesapları sonra getirir.
        try
        {
            await connections.RefreshAccountsAsync(LicenseId, ct);
            TempData["Success"] = $"Banka bağlantısı eklendi (Obifin #{bc.BankaApiId}).";
        }
        catch (Exception ex) when (DescribeFailure(ex, ct) is { } msg)
        {
            TempData["Error"] = $"Banka bağlantısı eklendi (Obifin #{bc.BankaApiId}) ama hesap listesi yenilenemedi: {msg}. Saatlik yenileme tekrar dener.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPollNowAsync(CancellationToken ct)
    {
        if (BankDisabled) return BankDisabledResult();
        var conn = await _db.ObifinConnections.AsNoTracking().FirstOrDefaultAsync(c => c.LicenseId == LicenseId, ct);
        if (conn is null) return NotFound();
        // Çekim işi yalnız Verified bağlantıyı çeker; kuyruğa atıp "alındı" demek sessizce hiçbir şey yapmamak olurdu.
        if (conn.Status != ObifinConnectionStatus.Verified)
        {
            TempData["Error"] = NotVerifiedMessage;
            return RedirectToPage();
        }
        _jobs.Enqueue<ObifinPollJob>(j => j.PollConnectionAsync(conn.Id, CancellationToken.None));
        await _audit.LogAsync(AuditEvents.ObifinPollNow, AuditTargets.ObifinConnection, LicenseId.ToString(), null, ct);
        TempData["Success"] = "Çekim kuyruğa alındı; birkaç saniye içinde tablo güncellenir.";
        return RedirectToPage();
    }

    /// <summary>"X dk önce" — son başarılı çekimin yaşı, sağlık sinyali.</summary>
    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "az önce";
        if (span < TimeSpan.FromHours(2)) return $"{(int)span.TotalMinutes} dk önce";
        if (span < TimeSpan.FromDays(2)) return $"{(int)span.TotalHours} sa önce";
        return $"{(int)span.TotalDays} gün önce";
    }

    private IActionResult BankDisabledResult()
    {
        TempData["Error"] = BankHasher.DisabledMessage;
        return RedirectToPage();
    }

    /// <summary><see cref="ObifinConnectionService.UpsertAsync"/>'in gölge veri sıfırlama kararının aynısı: kırpılmış
    /// kullanıcı kodu (harf duyarlı) ya da BOŞ OLMAYAN adresin normalize hâli (<c>AbsoluteUri</c>, sondaki '/' atılmış)
    /// saklı değerden farklı mı. Boş adres "görüş yok"tur. Geçersiz adres değişiklik sayılmaz: servis onu zaten reddeder,
    /// hiçbir şey kaydedilmez. İki karar ayrışırsa veri onaysız silinebilir — sayfa testleri kozmetik farkı sabitler.</summary>
    private static bool IsIdentityChange(ObifinConnection existing, string? baseUrl, string? userCode)
    {
        if (!string.Equals(existing.UserCode, (userCode ?? "").Trim(), StringComparison.Ordinal)) return true;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !(Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            return false;
        return !string.Equals(existing.BaseUrl.TrimEnd('/'), uri.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);
    }

    /// <summary>Sıfırlamanın sileceği satırlardan herhangi biri var mı (bkz. <c>ObifinConnectionService.ResetShadowDataAsync</c>).</summary>
    private async Task<bool> HasShadowDataAsync(Guid licenseId, CancellationToken ct)
        => await _db.BankTransactions.AnyAsync(t => t.LicenseId == licenseId, ct)
           || await _db.BankAccounts.AnyAsync(a => a.LicenseId == licenseId, ct)
           || await _db.BankConnections.AnyAsync(b => b.LicenseId == licenseId, ct)
           || await _db.PaymentMatches.AnyAsync(m => m.LicenseId == licenseId, ct)
           || await _db.CustomerIbanMemories.AnyAsync(m => m.LicenseId == licenseId, ct)
           || await _db.PaymentMatchGaps.AnyAsync(g => g.LicenseId == licenseId, ct);

    /// <summary>Admin ekranına düşecek metin; null = beklenmeyen hata, yukarı gider. Yerel doğrulama/durum hataları
    /// (<see cref="ArgumentException"/>, <see cref="InvalidOperationException"/>) kendi Türkçe mesajlarıyla; istemci
    /// hataları servisin sınıflandırmasıyla — Obifin'in mesajları aynen, ağ/vekil/zaman aşımı için kısa metin (ham
    /// istisna metni değil). İsteğin kendi iptali sınıflandırılmaz. Kimlik hiçbir dalda yer almaz.</summary>
    private static string? DescribeFailure(Exception ex, CancellationToken ct)
        => ex is ArgumentException or InvalidOperationException
            ? ex.Message
            : ObifinConnectionService.DescribeClientFailure(ex, ct);
}
