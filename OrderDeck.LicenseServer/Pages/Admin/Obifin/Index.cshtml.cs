using System.Globalization;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Pages.Admin.Obifin;

/// <summary>Pilot yönetimi (spec §8): Obifin kimliği, doğrulama, banka bağlantısı, hesaplar, "Şimdi çek".
/// [Authorize] yok — Program.cs `AuthorizeFolder("/Admin")`. Şifre/API key asla görünüme çıkmaz; banka web servis
/// kimlikleri (<c>Field_*</c>) yalnız isteğin belleğinde yaşar: TempData'ya, audit'e, loga, sayfaya girmez.
/// <para><b>Banka modülü kapalıyken de sayfa açılır.</b> <see cref="BankHasher"/> anahtar yoksa kurucusunda düşen bir
/// singleton; ona bağlı <see cref="ObifinConnectionService"/> kurucuya enjekte edilseydi sayfa 500 verirdi. Servis
/// yalnız modül açıkken, işleyicinin içinde çözülür; kapalıyken her POST <see cref="BankHasher.DisabledMessage"/> ile
/// döner ve Obifin'e dokunmaz.</para>
/// <para>Admin'e yalnız servisin kendi doğrulama mesajları (<see cref="ObifinValidationException"/>) ve sınıflandırılmış
/// istemci hataları dostça metin olarak gösterilir; başka her istisna (JSON, EF, DI…) programlama hatasıdır, yukarı gider
/// (500, çerçeve tam iziyle günlüğe yazar). Bildirime çevrilen hata sunucu günlüğüne de düşer (<see cref="LogHandled"/>):
/// yalnız işleyici adı ve istisna türü — istisna metni (Obifin/banka yankısı taşıyabilir) ve form değerleri asla.</para>
/// <para>Taahhüt edilmiş bir yazımın (kimlik kaydı — belki geri dönüşsüz gölge veri silmesi —, doğrulama, kuyruğa atma,
/// banka ekleme) ardındaki audit kaydı istek jetonuyla DEĞİL <see cref="CancellationToken.None"/> ile yazılır: istek o anda
/// iptal edilse iş yapılmış, izi kaybolmuş olurdu. Sayfanın her görüntülenmesi de denetime yazılır
/// (<see cref="AuditEvents.BankPageView"/>, KVKK Ö-4).</para>
/// <para><b>Durdur / Etkinleştir / Kaldır ve sil / Teyit alındı / hesap reddi</b> (KVKK K-6, Ö-9, Ö-10;
/// <see cref="BankConnectionTerminator"/>) <see cref="BankHasher"/> istemez: banka modülü kapalıyken de çalışır. Geri dönüşsüz
/// iki eylem yazılı onay ister (Purge'ün <c>confirmEmail</c> kalıbı): "Kaldır ve sil" lisans anahtarının son 6 karakterini, hesap
/// reddi hesap numarasını. İmha kaydının ve teyit kaydının yapanı yöneticinin kimliğidir
/// (<see cref="DataDestruction.ActorOf"/>), kullanıcı adı değil.</para></summary>
public class IndexModel : PageModel
{
    public const string NotVerifiedMessage = "Bağlantı doğrulanmamış; önce Doğrula.";
    public const string NoConnectionMessage = "Bu lisansın Obifin bağlantısı yok.";
    /// <summary>Onay istenince yeniden basılan formun bildirimine eklenir: şifre ve API anahtarı forma geri basılmaz.</summary>
    public const string ReenterSecretsHint = "Şifre ya da API anahtarını da değiştirdiyseniz yeniden girin; güvenlik gereği forma geri doldurulmaz.";

    /// <summary>Banka ekleme formunda seçilen bankanın yöntemine göre tek satırlık "bu bilgiler nereden gelir" açıklaması.</summary>
    public const string WebServiceHelp = "Bu bilgileri banka, kurumsal internet şubesinden ya da şubeden 'hesap hareketleri web servisi' başvurusuyla verir.";
    public const string ApiHelp = "Bu bilgiler bankanın API başvurusuyla alınır.";

    /// <summary>"Kaldır ve sil" onayı. Tek tırnaklı JS dizesine gömülür: kesme işareti içeremez.</summary>
    public const string RemoveConfirmMessage = "Bu lisansın Obifin bağlantısı kaldırılacak ve bütün banka verisi (hareketler, eşleşmeler, IBAN hafızası, hesaplar) silinecek. Geri alınamaz. Emin misiniz?";
    /// <summary>Hesap reddinin onayı. Tek tırnaklı JS dizesine gömülür: kesme işareti içeremez.</summary>
    public const string RejectConfirmMessage = "Bu hesabın bu lisanstaki hareketleri, eşleşmeleri ve öğrettiği IBAN bağları silinecek; onayı kalkacak. Geri alınamaz.";
    /// <summary>Kaldırmanın sonuç bildirimine eklenir (TempData; JS'e gömülmez).</summary>
    public const string ObifinCopyWarning = "Obifin'deki hareket kopyasının silinmesini Obifin'den YAZILI isteyin (API'de hareket silme ucu yok); teyit gelince 'Obifin silme teyidi bekleyenler'de 'Teyit alındı'ya basın.";
    /// <summary>Yazılı onay (Purge'ün confirmEmail kalıbı) tutmadı.</summary>
    public const string RemoveConfirmMismatchMessage = "Onay için lisans anahtarının son 6 karakterini yazın; eşleşmedi, hiçbir şey silinmedi.";
    public const string RejectConfirmMismatchMessage = "Onay için hesap numarasını yazın; eşleşmedi, hiçbir şey silinmedi.";
    /// <summary>"Kaldır ve sil"in yazılı onayındaki karakter sayısı (lisans anahtarının sonu).</summary>
    public const int RemoveConfirmLength = 6;
    /// <summary>Hizmet sonu silmesinin durdurmadan sonraki süresi (gün) — durum hücresindeki "silinecek" tarihi ve "Durdur"
    /// bildirimi. Tek kaynak işin sabiti (<see cref="BankServiceEndPurgeJob.GracePeriodDays"/>).</summary>
    public const int ServiceEndGraceDays = BankServiceEndPurgeJob.GracePeriodDays;

    private readonly LicenseDbContext _db;
    private readonly IServiceProvider _services;
    private readonly IBackgroundJobClient _jobs;
    private readonly IAuditService _audit;
    private readonly BankConnectionTerminator _terminator;
    private readonly BankOptions _bank;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(LicenseDbContext db, IServiceProvider services, IBackgroundJobClient jobs, IAuditService audit,
        BankConnectionTerminator terminator, IOptions<BankOptions> bank, ILogger<IndexModel> log)
    { _db = db; _services = services; _jobs = jobs; _audit = audit; _terminator = terminator; _bank = bank.Value; _log = log; }

    /// <summary>Bağlantı satırı. <see cref="DisabledAt"/> ve <see cref="BankDataPurgedAt"/> durum hücresinde yalnız Disabled iken
    /// gösterilir (KVKK K-6: hizmet sonu sayacı, yerel silme).</summary>
    public sealed record ConnectionRow(Guid LicenseId, string CustomerEmail, string BaseUrl, string UserCode,
        ObifinConnectionStatus Status, DateTimeOffset? DisabledAt, DateTimeOffset? BankDataPurgedAt, string? LastError,
        DateTimeOffset? LastVerifiedAt, DateTimeOffset? LastPolledAt, long? Cursor, DateTimeOffset? BackfillCompletedAt,
        DateOnly? InitialCollectFrom, int BankConnections, int Accounts, int Transactions);
    /// <summary>"Kaldır ve sil"den sonra Obifin'in yazılı silme teyidini bekleyen kayıt (karar 21). Lisans silinmişse e-posta yok,
    /// kimlik gösterilir. <see cref="ManualAction"/>: Obifin'de elle yapılacak iş (null = yalnız yazılı teyit).</summary>
    public sealed record CopyDeletionRow(Guid Id, Guid LicenseId, string? CustomerEmail, DateTimeOffset RequestedAt,
        string? ManualAction);
    /// <summary>Hesap satırı. IBAN niteliği sayaçları (KVKK Ö-7): bu hesaba yazılan hareketlerde karşı IBAN tam / maskeli / boş;
    /// değer yok, yalnız sayılar. Onay (KVKK Ö-10): <see cref="ApprovedAt"/> boşsa hesabın hareketi kaydedilmez,
    /// <see cref="UnapprovedSkippedCount"/> atlananları sayar. Obifin hesap numarası yöneticinin hesabı tanıması ve reddin yazılı
    /// onayı (Görev 13) için.</summary>
    public sealed record AccountRow(Guid Id, long ObifinAccountId, string BankaKodu, string IbanMasked, string Currency, bool Active,
        DateTimeOffset? LastBankSyncAt, string? NotificationNote, int IbanFullCount, int IbanMaskedCount, int IbanEmptyCount,
        DateTimeOffset? ApprovedAt, int UnapprovedSkippedCount);
    public sealed record LicenseOption(Guid Id, string Label);
    /// <summary>Düzenlenen bağlantı: formda yalnız "kayıtlı" bayrağı — değerler asla geri basılmaz.</summary>
    public sealed record EditTarget(bool PasswordStored, bool ApiKeyStored);

    [BindProperty] public Guid LicenseId { get; set; }
    [BindProperty] public string? BaseUrl { get; set; }
    [BindProperty] public string? UserCode { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? ApiKey { get; set; }
    /// <summary>Kimlik değişimi gölge veriyi siler; açık onay olmadan kaydedilmez. Yalnız izindir: silinip silinmeyeceğine
    /// <see cref="ObifinConnectionService.UpsertAsync"/> karar verir.</summary>
    [BindProperty] public bool ConfirmReset { get; set; }
    [BindProperty] public string? BankaKodu { get; set; }
    [BindProperty] public string? Label { get; set; }
    /// <summary>"Kaldır ve sil"in yazılı onayı: lisans anahtarının son <see cref="RemoveConfirmLength"/> karakteri (harf
    /// duyarsız). Gizli bir onay alanı YOK: formun kendisi onu gönderdiği için sunucu denetimi anlamsız olurdu.</summary>
    [BindProperty] public string? ConfirmLicenseKey { get; set; }

    public List<ConnectionRow> Rows { get; private set; } = new();
    public Dictionary<Guid, List<AccountRow>> AccountsByLicense { get; private set; } = new();
    public List<LicenseOption> Licenses { get; private set; } = new();
    /// <summary>Banka ekleme formunun lisansları: yalnız Obifin bağlantısı olanlar — <see cref="ObifinConnectionService.AddBankConnectionAsync"/>
    /// başkasında çalışmaz.</summary>
    public List<LicenseOption> BankLicenses { get; private set; } = new();
    public EditTarget? Editing { get; private set; }
    /// <summary>Banka ekleme formu yalnız bu türün alanlarıyla basılır; null = banka seçilmedi (ya da kod tanınmadı), yalnız
    /// seçici görünür.</summary>
    public ObifinBankType? SelectedBank { get; private set; }
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    /// <summary>Açılıştaki kararın aynısı (Program.cs): anahtar yoksa banka modülü kapalı.</summary>
    public bool BankDisabled => !BankHasher.IsValidKey(_bank.HashKey);

    /// <summary>Onaysız hesap sayısı (sayfanın üstündeki uyarı; KVKK Ö-10).</summary>
    public int UnapprovedAccounts => AccountsByLicense.Values.Sum(list => list.Count(a => a.ApprovedAt is null));

    /// <summary>Banka verisi hizmet sonunda yerelde silinmiş, Obifin tarafı "Kaldır ve sil" bekleyen bağlantı sayısı (karar 21).</summary>
    public int PendingObifinRemoval => Rows.Count(r => r.BankDataPurgedAt is not null);

    /// <summary>"Kaldır ve sil"den sonra Obifin'in yazılı silme teyidini bekleyen kayıtlar (eskiden yeniye; karar 21).</summary>
    public List<CopyDeletionRow> PendingCopyDeletions { get; private set; } = new();

    /// <summary>Yalnız modül açıkken çözülür (bkz. sınıf özeti).</summary>
    private ObifinConnectionService Connections => _services.GetRequiredService<ObifinConnectionService>();

    /// <summary><paramref name="license"/> verilirse kimlik formu o bağlantının kullanıcı kodu ve adresiyle dolar: değişmeden
    /// kaydedilen form hiçbir zaman gölge veri sıfırlamasına yol açmaz. <paramref name="banka"/> (<see cref="ObifinBankCatalog"/>
    /// kodu, harf duyarsız) verilirse banka ekleme formu o bankanın alanlarıyla açılır.</summary>
    public async Task OnGetAsync(Guid? license, string? banka, CancellationToken ct)
    {
        SelectedBank = ObifinBankCatalog.TryGet(banka, out var bank) ? bank : null;
        Rows = await _db.ObifinConnections.AsNoTracking().OrderBy(c => c.CreatedAt)
            .Select(c => new ConnectionRow(c.LicenseId,
                _db.Licenses.Where(l => l.Id == c.LicenseId).Select(l => l.Customer.Email).FirstOrDefault() ?? "(bilinmiyor)",
                c.BaseUrl, c.UserCode, c.Status, c.DisabledAt, c.BankDataPurgedAt, c.LastError, c.LastVerifiedAt,
                c.LastPolledAt, c.LastObifinTransactionId,
                c.BackfillCompletedAt, c.InitialCollectFrom,
                _db.BankConnections.Count(b => b.LicenseId == c.LicenseId && b.Status == BankConnectionStatus.Active),
                _db.BankAccounts.Count(a => a.LicenseId == c.LicenseId),
                _db.BankTransactions.Count(t => t.LicenseId == c.LicenseId)))
            .ToListAsync(ct);
        var licenseIds = Rows.Select(r => r.LicenseId).ToList();
        AccountsByLicense = (await _db.BankAccounts.AsNoTracking().Where(a => licenseIds.Contains(a.LicenseId)).ToListAsync(ct))
            .GroupBy(a => a.LicenseId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AccountRow(a.Id, a.ObifinAccountId,
                a.BankaKodu, a.IbanMasked, a.Currency, a.Active, a.LastBankSyncAt, a.NotificationNote, a.IbanFullCount,
                a.IbanMaskedCount, a.IbanEmptyCount, a.ApprovedAt, a.UnapprovedSkippedCount)).ToList());
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
        // Obifin silme teyidi bekleyenler (karar 21). Kayıt bağlantıdan bağımsız: bağlantı satırı "Kaldır ve sil"le silindi.
        PendingCopyDeletions = await _db.ObifinCopyDeletions.AsNoTracking().Where(d => d.ConfirmedAt == null)
            .OrderBy(d => d.RequestedAt)
            .Select(d => new CopyDeletionRow(d.Id, d.LicenseId,
                _db.Licenses.Where(l => l.Id == d.LicenseId).Select(l => l.Customer.Email).FirstOrDefault(), d.RequestedAt,
                d.ManualAction))
            .ToListAsync(ct);

        // Kullanıcı kodu ve adres tablonun satırından (aynı bağlantı, bu istekte yüklendi); "kayıtlı" bayrakları ayrıca.
        if (license is { } id && Rows.FirstOrDefault(r => r.LicenseId == id) is { } row
            && await LoadEditingAsync(id, ct) is { } editing)
        {
            LicenseId = id; BaseUrl = row.BaseUrl; UserCode = row.UserCode;
            Editing = editing;
        }
        // KVKK Ö-4: hesap maskeleri, bildirim notları ve yayıncı e-postaları gösteren sayfanın her görüntülenmesi denetime
        // yazılır (kimlik onayı isteyen kayıt da sayfayı buradan basar). Hedef düzenleme formu açılan lisans, yoksa boş.
        await _audit.LogAsync(AuditEvents.BankPageView, AuditTargets.ObifinConnection, license?.ToString(),
            new { page = "obifin" }, ct);
    }

    /// <summary>Düzenlenen bağlantının "kayıtlı" bayrakları; bağlantı yoksa null. Yalnız bayraklar okunur: şifreli değerler
    /// sayfa modeline hiç yüklenmez. Bağlı form alanlarına (lisans, kullanıcı kodu, adres) dokunmaz.</summary>
    private async Task<EditTarget?> LoadEditingAsync(Guid licenseId, CancellationToken ct)
        => await _db.ObifinConnections.AsNoTracking().Where(c => c.LicenseId == licenseId)
            .Select(c => new EditTarget(c.PasswordProtected.Length > 0, c.ApiKeyProtected.Length > 0))
            .FirstOrDefaultAsync(ct);

    /// <summary>Kullanıcı kodu ya da adres değişir ve lisansın gölge verisi varsa, <see cref="ConfirmReset"/> işaretli
    /// değilse HİÇBİR ŞEY kaydedilmez: <see cref="ObifinConnectionService.UpsertAsync"/> o veriyi geri dönüşsüz siler.
    /// Karar yalnız serviste verilir; sayfa yalnız izni (<see cref="ConfirmReset"/>) taşır.
    /// <para>Onay istenirse YÖNLENDİRİLMEZ: sayfa admin'in yazdığı lisans, kullanıcı kodu ve adresle yeniden basılır (şifre
    /// ve API anahtarı asla geri basılmaz, kutu işaretsiz gelir). <c>?license=</c>'e yönlendirmek formu SAKLI kimlikle
    /// doldururdu: bildirimdeki adımı izleyip kutuyu işaretleyen admin eski kimliği gönderir, değişiklik sessizce düşer,
    /// Verified bağlantı Unverified'a iner ve "kaydedildi" bildirimi gelirdi.</para></summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (BankDisabled) return BankDisabledResult();
        if (!await _db.Licenses.AnyAsync(l => l.Id == LicenseId, ct))
        {
            TempData["Error"] = "Lisans bulunamadı.";
            return RedirectToPage();
        }
        try
        {
            // Gölge veri silinirse imha kaydının yapanı yöneticinin kimliği (KVKK Ö-14; kaydı budanmaz, adı denetim satırında).
            var saved = await Connections.UpsertWithResultAsync(LicenseId, BaseUrl ?? "", UserCode ?? "", Password, ApiKey, ct,
                allowShadowReset: ConfirmReset, actor: DataDestruction.ActorOf(User));
            // Kayıt (ve varsa geri dönüşsüz silme) taahhüt edildi: audit istek iptaliyle düşmesin.
            await _audit.LogAsync(AuditEvents.ObifinConnectionSave, AuditTargets.ObifinConnection, LicenseId.ToString(),
                new { UserCode = UserCode?.Trim(), shadowDataReset = saved.ShadowDataReset }, CancellationToken.None);
            // Durdurulmuş bağlantı kayıtla açılmaz (aynı kimliğe yeni parola, ya da lisans bitmişken kimlik değişimi): Doğrula
            // gizli ve servis onu reddeder, "Şimdi doğrulayın" yanlış yönlendirirdi.
            var next = saved.Connection.Status == ObifinConnectionStatus.Disabled
                ? ObifinConnectionService.StoppedConnectionMessage
                : "Şimdi doğrulayın.";
            TempData["Success"] = saved.ShadowDataReset
                ? "Obifin kimliği kaydedildi, eski hesabın banka verisi silindi. " + next
                : "Obifin kimliği kaydedildi. " + next;
        }
        catch (ShadowResetConfirmationRequiredException ex)
        {
            // Beklenen onay adımı, hata değil: uyarı düzeyinde gürültü yapmaz.
            _log.LogInformation("Obifin admin Save: kimlik değişikliği gölge veri silme onayı bekliyor — lisans={LicenseId}", LicenseId);
            // Layout'un bildirimi bu istekte okur ve tüketir: sonraki sayfada yinelenmez.
            TempData["Error"] = ex.Message + " " + ReenterSecretsHint;
            await OnGetAsync(null, null, ct); // license = null: bağlı LicenseId/BaseUrl/UserCode'a dokunmaz
            // Bağlantı var (kimlik değişimi ancak mevcut bağlantıda onay ister): "kayıtlı; boş = değiştirme" ipuçları için.
            Editing = await LoadEditingAsync(LicenseId, ct);
            return Page();
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("Save", ex);
            TempData["Error"] = ex.Message;
        }
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
        catch (ObifinValidationException ex)
        {
            LogHandled("Verify", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        // Doğrulama sonucu (durum + hesaplar) kaydedildi: audit istek iptaliyle düşmesin.
        await _audit.LogAsync(AuditEvents.ObifinConnectionVerify, AuditTargets.ObifinConnection, LicenseId.ToString(),
            new { result.Ok, result.AccountCount }, CancellationToken.None);
        if (result.Ok) TempData["Success"] = $"Doğrulandı: {result.AccountCount} hesap.";
        else TempData["Error"] = "Doğrulanamadı: " + result.Error;
        return RedirectToPage();
    }

    /// <summary>İstek jetonu BİLEREK kullanılmaz: <c>bankaapi/ekle</c> idempotent değil. Sekme kapanınca çağrı Obifin kaydı
    /// açtıktan sonra iptal edilseydi yerelde bilinmeyen, banka kimliği taşıyan yetim bir kayıt kalır, admin'in tekrarı
    /// ikincisini açardı. Ekleme, ardındaki audit ve hesap yenilemesi sonuna kadar koşar; süreyi HttpClient zaman aşımı
    /// sınırlar (iptal olmadığından her <see cref="OperationCanceledException"/> zaman aşımıdır).
    /// <para>Aynı sebeple sonucu belirsiz ekleme (<see cref="ObifinBankAddUncertainException"/>: ekleme çağrısı ağ/zaman
    /// aşımıyla düştü, ya da geçti ama liste alınamadı ya da etiket listede bulunamadı) "eklenemedi" diye gösterilmez:
    /// servisin mesajı Obifin'deki etiketi ve tekrar eklememe uyarısını taşır, önek eklenmeden bildirime düşer.</para>
    /// <para>Alanlar <see cref="ObifinBankCatalog"/>'tan: yalnız seçilen türün <c>Field_&lt;Anahtar&gt;</c> değerleri okunur,
    /// başka ad yok sayılır. Zorunlu alan boşsa Obifin'e hiç gidilmez; bildirim yalnız eksik alanların ETİKETİNİ taşır,
    /// gönderilen değerleri asla. Bilinen bir bankanın her dönüşü (hata ya da başarı) o bankanın formuna
    /// (<c>?banka=</c>): admin bankayı yeniden seçmez. Formun değerleri geri basılmaz, yeniden girilir.</para></summary>
    public async Task<IActionResult> OnPostAddBankAsync()
    {
        var bankCode = ObifinBankCatalog.TryGet(BankaKodu, out var type) ? type.Code : null;
        // Tanınmayan kodun formu yok: düz sayfaya döner.
        IActionResult BackToForm() => bankCode is null ? RedirectToPage() : RedirectToPage(new { banka = bankCode });
        if (BankDisabled)
        {
            TempData["Error"] = BankHasher.DisabledMessage;
            return BackToForm();
        }
        if (type is null) { TempData["Error"] = "Bilinmeyen banka kodu."; return BackToForm(); }
        // Alanlar Field_<Anahtar> olarak gelir; yalnız bu isteğin belleğinde yaşar, loglanmaz, saklanmaz.
        var form = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var f in type.Fields)
        {
            var v = Request.Form["Field_" + f.Key].ToString();
            if (!string.IsNullOrWhiteSpace(v)) form[f.Key] = v.Trim();
            else if (f.Required) missing.Add(f.Label);
        }
        if (missing.Count > 0)
        {
            TempData["Error"] = "Eksik alan: " + string.Join(", ", missing);
            return BackToForm();
        }
        var connections = Connections;
        BankConnection bc;
        try
        {
            bc = await connections.AddBankConnectionAsync(LicenseId, type.Code, Label ?? "", form, CancellationToken.None);
        }
        catch (ObifinBankAddUncertainException ex)
        {
            // Kayıt Obifin'de açılmış olabilir (ya da açıldı): "eklenemedi" demek admin'i tekrar eklemeye iterdi.
            LogHandled("AddBank", ex);
            TempData["Error"] = ex.Message;
            return BackToForm();
        }
        catch (Exception ex) when (DescribeFailure(ex, CancellationToken.None) is { } msg)
        {
            LogHandled("AddBank", ex);
            TempData["Error"] = "Banka bağlantısı eklenemedi: " + msg;
            return BackToForm();
        }
        await _audit.LogAsync(AuditEvents.ObifinBankAdd, AuditTargets.ObifinConnection, LicenseId.ToString(),
            new { banka = type.Code, bc.BankaApiId }, CancellationToken.None);
        // Bağlantı Obifin'de ve yerelde AÇILDI: hesap yenilemesi NE sebeple düşerse düşsün "eklenemedi" demek ya da 500
        // vermek yanlış olur (admin tekrar ekler, Obifin'de ikinci kayıt açılır). Saatlik yenileme işi hesapları sonra
        // getirir. Yalnız ObjectDisposedException (programlama hatası) yukarı gider; çağıranın iptali yok (jeton None),
        // her OperationCanceledException zaman aşımıdır. Bu çağrı banka form değeri taşımaz: istisna nesnesiyle loglanır.
        try
        {
            await connections.RefreshAccountsAsync(LicenseId, CancellationToken.None);
            TempData["Success"] = $"Banka bağlantısı eklendi (Obifin #{bc.BankaApiId}).";
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            _log.LogWarning(ex, "Obifin admin AddBank: bağlantı eklendi (Obifin #{BankaApiId}) ama hesap yenileme düştü — lisans={LicenseId}",
                bc.BankaApiId, LicenseId);
            var msg = DescribeFailure(ex, CancellationToken.None) ?? $"beklenmeyen hata ({ex.GetType().Name})";
            TempData["Error"] = $"Banka bağlantısı eklendi (Obifin #{bc.BankaApiId}) ama hesap listesi yenilenemedi: {msg}. Saatlik yenileme tekrar dener.";
        }
        return BackToForm();
    }

    /// <summary>KVKK Ö-10: hesabı lisansa bağlar; bundan sonraki hareketleri kaydedilir (önceki atlananlar geriye dönük gelmez).
    /// Yalnız yayıncının müşteri tahsilatı hesabı onaylanmalı. <see cref="BankHasher"/> istemez: modül kapalıyken de çalışır,
    /// Obifin'e gitmez. Onaylı hesapta bir şey değiştirmez (ilk onay anı ve onaylayan kalır). Geri alma: "Onayı geri al ve
    /// verisini sil" (<see cref="OnPostRejectAccountAsync"/>).</summary>
    public async Task<IActionResult> OnPostApproveAccountAsync(Guid accountId, CancellationToken ct)
    {
        var account = await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
        {
            TempData["Error"] = "Hesap bulunamadı.";
            return RedirectToPage();
        }
        if (account.ApprovedAt is null)
        {
            account.ApprovedAt = DateTimeOffset.UtcNow;
            // Yöneticinin kimliği, kullanıcı adı değil (KVKK Ö-14; Görev 9 kuralı): sütun budanmaz, ad denetim satırında.
            account.ApprovedBy = DataDestruction.ActorOf(User);
            await _db.SaveChangesAsync(ct);
            // Onay taahhüt edildi: audit istek iptaliyle düşmesin. Ayrıntıda IBAN yok.
            await _audit.LogAsync(AuditEvents.ObifinAccountApprove, AuditTargets.ObifinConnection, account.LicenseId.ToString(),
                new { account.ObifinAccountId, account.BankaKodu }, CancellationToken.None);
        }
        TempData["Success"] = "Hesap onaylandı; bundan sonraki hareketleri kaydedilecek.";
        return RedirectToPage();
    }

    /// <summary>Çekimi durdurur (KVKK K-6): çekim ve eşleştirme durur; <see cref="ServiceEndGraceDays"/> gün içinde
    /// etkinleştirilmezse hizmet sonu işi bu lisansın banka verisini yerelde siler. Lisans yenilense de kendiliğinden açılmaz
    /// (yönetici kararı). BankHasher istemez.</summary>
    public async Task<IActionResult> OnPostStopAsync(CancellationToken ct)
    {
        try
        {
            await _terminator.StopAsync(LicenseId, ct);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("Stop", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        // Durdurma kaydedildi: audit istek iptaliyle düşmesin.
        await _audit.LogAsync(AuditEvents.ObifinConnectionStop, AuditTargets.ObifinConnection, LicenseId.ToString(), null,
            CancellationToken.None);
        TempData["Success"] = $"Çekim durduruldu. {ServiceEndGraceDays} gün içinde etkinleştirilmezse bu lisansın banka verisi silinir.";
        return RedirectToPage();
    }

    /// <summary>Durdurulmuş bağlantıyı yalnız lisans geçerliyse açar (Unverified; yönetici Doğrula'ya basar). BankHasher
    /// istemez.</summary>
    public async Task<IActionResult> OnPostResumeAsync(CancellationToken ct)
    {
        try
        {
            await _terminator.ResumeAsync(LicenseId, ct);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("Resume", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.ObifinConnectionResume, AuditTargets.ObifinConnection, LicenseId.ToString(), null,
            CancellationToken.None);
        TempData["Success"] = "Bağlantı etkinleştirildi; şimdi Doğrula'ya basın.";
        return RedirectToPage();
    }

    /// <summary>Obifin'de bu lisansa ait banka bağlantılarını kaldırır (paylaşılan Obifin kullanıcısında Obifin'e gitmez, sahibi
    /// doğrulanamayana dokunmaz) ve lisansın bütün banka verisini siler (KVKK K-6, Ö-9). <c>bankaapi/sil</c>'i çağıran TEK yol.
    /// İstek jetonu BİLEREK kullanılmaz: yarıda kesilen kaldırma Obifin'de bağlantı bırakıp yerelde iz kaybettirirdi. Yazılı onay:
    /// lisans anahtarının son <see cref="RemoveConfirmLength"/> karakteri (Purge'ün <c>confirmEmail</c> kalıbı) — geri dönüşsüz
    /// işlem tek JS confirm()'una kalmasın. Bildirim ve denetim kimlik bilgisi içermez.</summary>
    public async Task<IActionResult> OnPostRemoveAsync()
    {
        var licenseKey = await _db.Licenses.AsNoTracking().Where(l => l.Id == LicenseId).Select(l => l.LicenseKey)
            .FirstOrDefaultAsync(CancellationToken.None);
        var typed = (ConfirmLicenseKey ?? "").Trim();
        if (licenseKey is null || typed.Length != RemoveConfirmLength
            || !licenseKey.EndsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = RemoveConfirmMismatchMessage;
            return RedirectToPage();
        }
        BankRemovalResult r;
        try
        {
            // Yapan: yöneticinin kimliği, kullanıcı adı değil — imha kaydı ve teyit kaydı budanmaz (Görev 9 kuralı).
            r = await _terminator.RemoveAndDeleteAsync(LicenseId, DataDestruction.Triggers.AdminRemove,
                DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (BankDeletionIncompleteException ex)
        {
            // Silme yarıda kaldı ama ilk adımın durdurması kaydedildi (hizmet sonu sayacı işliyor): durdurma denetime girer.
            LogHandled("Remove", ex);
            await _audit.LogAsync(AuditEvents.ObifinConnectionStop, AuditTargets.ObifinConnection, LicenseId.ToString(),
                new { removeIncomplete = true }, CancellationToken.None);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("Remove", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        // Denetimde "elle iş" bilgisi de durur. Yazılı silme teyidinin takibi denetim satırında DEĞİL (24 ayda maskelenir,
        // kapatılamaz): ObifinCopyDeletions kaydında, hatırlatması hizmet sonu işinde (karar 21).
        await _audit.LogAsync(AuditEvents.ObifinConnectionRemove, AuditTargets.ObifinConnection, LicenseId.ToString(),
            new
            {
                r.Transactions, r.Matches, r.Memories, r.Gaps, r.Accounts, r.BankConnections,
                obifinRemoved = r.ObifinRemoved.Count, obifinFailed = r.ObifinFailed.Count, obifinUnowned = r.ObifinUnowned.Count,
                r.ObifinUnreachable, r.SharedObifinUser, needsManualObifinAction = r.NeedsManualObifinAction,
                obifinCopyDeletionId = r.ObifinCopyDeletionId,
            },
            CancellationToken.None);
        var summary = $"Banka verisi silindi: {r.Transactions} hareket, {r.Matches} eşleşme, {r.Memories} IBAN bağı, {r.Accounts} hesap. ";
        if (r.NeedsManualObifinAction)
            TempData["Error"] = summary + r.ManualActionText() + " " + ObifinCopyWarning;
        else
            TempData["Success"] = summary + $"Obifin'de {r.ObifinRemoved.Count} banka bağlantısı kaldırıldı. " + ObifinCopyWarning;
        return RedirectToPage();
    }

    /// <summary>Obifin'in hareket kopyasını sildiğine dair yazılı teyit alındı (KVKK Ö-9 öneri 3; karar 21): kayıt kapanır, imha
    /// kaydı yazılır. Teyit yazısı sistemde saklanmaz (KVKK klasörü). BankHasher istemez.</summary>
    public async Task<IActionResult> OnPostConfirmObifinCopyDeletedAsync(Guid deletionId)
    {
        Guid licenseId;
        try
        {
            licenseId = await _terminator.ConfirmObifinCopyDeletedAsync(deletionId, DataDestruction.ActorOf(User),
                CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("ConfirmObifinCopyDeleted", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.ObifinCopyDeletionConfirm, AuditTargets.ObifinConnection, licenseId.ToString(),
            new { deletionId }, CancellationToken.None);
        TempData["Success"] = "Obifin'in yazılı silme teyidi kaydedildi. Teyit yazısını KVKK klasöründe saklayın.";
        return RedirectToPage();
    }

    /// <summary>KVKK Ö-10: hesabı reddeder (onaylıysa onayını geri alır) ve o hesabın bu lisanstaki hareketlerini, eşleşmelerini,
    /// öğrettiği IBAN bağlarını siler; tazelediği bağın süresini kalan doğrulamalara geri çeker. Yazılı onay: hesap numarası.
    /// İstek jetonu BİLEREK kullanılmaz (silmenin denetimi düşmesin). BankHasher istemez.</summary>
    public async Task<IActionResult> OnPostRejectAccountAsync(Guid accountId, string? confirmAccount)
    {
        var account = await _db.BankAccounts.AsNoTracking().Where(a => a.Id == accountId)
            .Select(a => new { a.LicenseId, a.ObifinAccountId }).FirstOrDefaultAsync(CancellationToken.None);
        if (account is null)
        {
            TempData["Error"] = BankConnectionTerminator.AccountNotFoundMessage;
            return RedirectToPage();
        }
        if ((confirmAccount ?? "").Trim() != account.ObifinAccountId.ToString(CultureInfo.InvariantCulture))
        {
            TempData["Error"] = RejectConfirmMismatchMessage;
            return RedirectToPage();
        }
        BankAccountRejectionResult r;
        try
        {
            r = await _terminator.RejectAccountAsync(accountId, DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("RejectAccount", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.ObifinAccountReject, AuditTargets.ObifinConnection, account.LicenseId.ToString(),
            new { account.ObifinAccountId, r.Transactions, r.Matches, r.Memories, r.GapsReopened }, CancellationToken.None);
        TempData["Success"] = $"Hesap reddedildi: {r.Transactions} hareket, {r.Matches} eşleşme, {r.Memories} IBAN bağı silindi; " +
            "bundan sonraki hareketleri kaydedilmez.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPollNowAsync(CancellationToken ct)
    {
        if (BankDisabled) return BankDisabledResult();
        var conn = await _db.ObifinConnections.AsNoTracking().FirstOrDefaultAsync(c => c.LicenseId == LicenseId, ct);
        if (conn is null)
        {
            TempData["Error"] = NoConnectionMessage;
            return RedirectToPage();
        }
        // Çekim işi yalnız Verified bağlantıyı çeker; kuyruğa atıp "alındı" demek sessizce hiçbir şey yapmamak olurdu.
        if (conn.Status != ObifinConnectionStatus.Verified)
        {
            TempData["Error"] = NotVerifiedMessage;
            return RedirectToPage();
        }
        _jobs.Enqueue<ObifinPollJob>(j => j.PollConnectionAsync(conn.Id, CancellationToken.None));
        // İş kuyruğa yazıldı: audit istek iptaliyle düşmesin.
        await _audit.LogAsync(AuditEvents.ObifinPollNow, AuditTargets.ObifinConnection, LicenseId.ToString(), null, CancellationToken.None);
        // İş, zamanlanmış çekim ve saatlik yenilemeyle aynı kilidi (obifin-poll, 300 sn) bekler: "birkaç saniye" sözü verilmez.
        TempData["Success"] = "Çekim kuyruğa alındı; başka bir çekim sürüyorsa birkaç dakika içinde başlar.";
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

    /// <summary>Dostça metne çevrilen hatanın sunucu izi: işleyici adı + istisna türü. İstisna nesnesi verilmez — metni
    /// Obifin'in/bankanın yankıladığı alanı taşıyabilir; form değerleri de yazılmaz.</summary>
    private void LogHandled(string handler, Exception ex)
        => _log.LogWarning("Obifin admin {Handler} hatayı bildirime çevirdi: {ExceptionType}", handler, ex.GetType().Name);

    /// <summary>Admin ekranına düşecek metin; null = beklenmeyen hata, yukarı gider. Servisin kendi doğrulama/durum mesajları
    /// (<see cref="ObifinValidationException"/>) olduğu gibi; istemci hataları servisin sınıflandırmasıyla — Obifin'in
    /// mesajları (banka eklemede maskeli), ağ/vekil/zaman aşımı için kısa metin (ham istisna metni değil). Başka her
    /// <see cref="InvalidOperationException"/>/<see cref="ArgumentException"/> (ör. <see cref="ObjectDisposedException"/>,
    /// JSON/EF hataları) programlama hatasıdır: gösterilmez, yukarı gider. İsteğin kendi iptali sınıflandırılmaz. Kimlik
    /// hiçbir dalda yer almaz.</summary>
    private static string? DescribeFailure(Exception ex, CancellationToken ct)
        => ex is ObifinValidationException
            ? ex.Message
            : ObifinConnectionService.DescribeClientFailure(ex, ct);
}
