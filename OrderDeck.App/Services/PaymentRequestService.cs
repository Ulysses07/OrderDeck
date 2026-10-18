using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services;

public enum PaymentRequestResult
{
    Opened,
    PhoneRequired,
    LaunchFailed,

    /// <summary>Mesaj WhatsApp Cloud API ile doğrudan gönderildi — operatörün
    /// gönder tuşuna basmasına gerek yok, wa.me penceresi açılmadı.</summary>
    Sent,

    /// <summary>Sunucu aynı gönderimin hâlâ işlendiğini söyledi (<c>in_progress</c>)
    /// — sonuç BİLİNMİYOR.
    ///
    /// <para>Neden <see cref="Sent"/> değil: bu cevap, ilk denemenin yarıda
    /// kesildiği (deploy sırasında sunucu yeniden başladı, bağlantı koptu, proxy
    /// 502) durumda da geliyor. Orada rezervasyon bilerek pending bırakılır ve
    /// müşteriye HİÇBİR ŞEY gitmemiştir. "Gönderildi" demek operatöre yalan
    /// söylemek olur; wa.me'yi de açmıyoruz çünkü gönderim gerçekten uçuştaysa
    /// ikinci bir faturalı kopya gider. Doğru davranış: operatörü uyarıp
    /// doğrulamaya yönlendirmek.</para></summary>
    SendPending,

    /// <summary>Bakiye durumu doğrulanamadı (apply/geri alma belirsiz kaldı ya
    /// da lisans/önizleme erişilemedi). Mesaj GÖNDERİLMEDİ — operatör tekrar
    /// denemeli; deneme aynı anahtarla replay yapar, çift düşüm imkânsız.</summary>
    BalanceUncertain,

    /// <summary>U12 (Bölüm C): çağıranın anlık görüntüsü (liste/rapor) bayat — liste
    /// yüklendikten sonra yerel taşıma müşteriyi başka Id'ye geçirdi. Hiçbir şey
    /// GÖNDERİLMEDİ, iş açılmadı; çağıran listeyi yeniler, operatör yeniden seçer.</summary>
    ListStale,

    /// <summary>Sahip kararı 2 (2026-10-10): bu kişiye bu kapsam için TESLİM EDİLMİŞ bir istek var.
    /// Ağ ya da para adımından ÖNCE döner, hiçbir şey değişmedi; çağıran operatöre
    /// <see cref="PaymentRequestOutcome.Prompt"/>'u sorar, "Evet" gelirse belirteçle yeniden çağırır
    /// (PhoneRequired deseni). "Hayır" → hiçbir şey yapılmaz.</summary>
    AlreadyRequested,

    /// <summary>Sahip kararı 4: bu kişiye bu kapsam için birden çok istek var (biri para taşıyor ya
    /// da teslim edilmiş) ya da uzlaştırma bekleyen teslim edilmiş miras isteği var — tek istekte
    /// birleştirilsin mi? Onaydan önce indirme, geri alma, ağ çağrısı ve mesaj YOK; onay gelirse
    /// önceki düşümler iade edilir ve kişinin toplamı yeniden istenir.</summary>
    MergeRequired,

    /// <summary>Bu kişi için başka bir ödeme akışı sürüyor (öbür pencere) ve süresi sınırlı bekleme
    /// doldu. Hiçbir şey gönderilmedi; operatör biraz sonra tekrar dener. Mod On'da başka bilgisayarın kirası da
    /// (spec 2026-10-13 §6.4 <c>in-progress-elsewhere</c>): metin makine adıyla
    /// <see cref="PaymentRequestOutcome.Message"/>'da.</summary>
    PaymentInProgress,

    /// <summary>Mod On'da (spec 2026-10-13 §6.4, W3) ön gönderim ya da sunucunun kirası isteği durdurdu: gönderilemeyen
    /// bekleyen komut, sunucunun reddi, güncelleme gerekli, uç yok. Hiçbir şey gönderilmedi; neden
    /// <see cref="PaymentRequestOutcome.Message"/>'da ve SATIR İÇİ gösterilir (açılır pencere yok — sahibin #508
    /// tercihi).</summary>
    Blocked,
}

/// <summary>Operatöre sorulacak soru (<see cref="PaymentRequestResult.AlreadyRequested"/> /
/// <see cref="PaymentRequestResult.MergeRequired"/>). Metni <see cref="PaymentResultPresenter"/> kurar.</summary>
/// <param name="Requests">Müşteriye gitmiş istekler (teslim sırasıyla; tekrar göndermede en sonuncusu sorulur).</param>
/// <param name="Token">Onay belirteci: "Evet" gelince yeniden çağrıya verilir. Soru ile onay arasında
/// durum değiştiyse belirteç tutmaz ve yeniden sorulur.</param>
/// <param name="UnsentDeductions">Birleştirmede para taşıyan ama müşteriye hiç gitmemiş istek sayısı
/// ("gitmiş" sayılmaz, ayrıca söylenir).</param>
/// <param name="RefundsToOtherRow">Birleştirmenin iade edeceği düşümlerden biri başka bir satırın
/// bakiyesinden yapılmış: bakiye satır başına tutulduğu için iade o satırda kalır, bu istekte
/// kullanılmaz (ayrı kartken ayrı ürünler için yapılmış meşru düşüm de olabilir).</param>
/// <param name="RefundsDeductions">Birleştirme bir düşümü iade ediyor; değilse (parasız istekler
/// birleşiyor) soru "önceki düşümler iade edilir" demez.</param>
/// <param name="RemoteDeductions">Kişi düzeyindeki kapsam uzlaştırmasının (R4-03) sunucuda bulduğu
/// ayrı düşümlerin tutarları (eskiden yeniye). Sunucu teslim bilgisini bilmez: bunlar "gitmiş istek"
/// değildir, ayrı anlatılır (<see cref="Requests"/> boştur).</param>
/// <param name="NewTotal">Tekrar göndermede (K5, 2026-10-12): istenecek toplam son istenenden FARKLIYSA
/// yeni toplam (kargo dahil, bakiye düşülmeden önce) — soru ikisini birlikte gösterir ("250 TL istendi;
/// yeni tutar 205 TL"). Aynıysa null: soru eskisi gibi.</param>
/// <param name="PriorHadBalance">Son isteğin netinden bakiye düşülmüştü: yeni toplam bakiye düşülmeden
/// önceki tutardır, soru bunu söyler (net ancak düşüm yeniden uygulanınca belli olur).</param>
public sealed record PaymentPrompt(
    PersonScopePromptKind Kind, string ScopeKey, IReadOnlyList<PriorPaymentRequest> Requests, string Token,
    int UnsentDeductions = 0, bool RefundsToOtherRow = false, bool RefundsDeductions = true,
    IReadOnlyList<decimal>? RemoteDeductions = null, decimal? NewTotal = null, bool PriorHadBalance = false)
{
    /// <summary>
    /// Aynı "Evet"in geçirdiği öbür kapıların belirteçleri (spec 2026-10-13 §6.4, §6.7; W3-09): mod On'da sunucunun
    /// <c>already-requested</c> sorusuna yerel planın sorusu katılır — <see cref="Token"/> uzak onay, buradaki yerel planın
    /// belirteci. Tek onay iki kapıyı da geçirir; ikinci diyalog açılmaz. Çoğu soruda boş.
    /// </summary>
    public IReadOnlyList<string> AlsoConfirms { get; init; } = [];

    /// <summary>"Evet"in onayladığı bütün belirteçler: <see cref="Token"/> ve <see cref="AlsoConfirms"/>. Onaylayan her
    /// çağıran (soru, fiyat düzeltmesinin toplu gönderimi) yeniden çağrıya bunların HEPSİNİ verir.</summary>
    public IReadOnlyList<string> Tokens => AlsoConfirms.Count == 0 ? [Token] : [Token, .. AlsoConfirms];
}

/// <summary>Daha önce gitmiş bir istek: müşteriden istenen net tutar ve (son) teslim anı.
/// <see cref="Unconfirmed"/> = gönderim başladı ama sonucu yazılamadı: istek GİTMİŞ OLABİLİR (kesin
/// değil); tutar ve an o denemenin tutarı ve anıdır.
/// <para><see cref="Machine"/>: sunucunun kaydı (mod On, <c>already-requested</c>'ın öncülü; spec 2026-10-13 §6.4-e,
/// W3-09) — isteği yapan bilgisayarın adı; soru onu makine adıyla anlatır. Yerel işte null.</para></summary>
public sealed record PriorPaymentRequest(decimal Amount, DateTimeOffset? At, bool Unconfirmed = false,
    string? Machine = null);

/// <summary>"Ödeme iste"nin sonucu.
/// <para><see cref="OwnerId"/> ve <see cref="Demoted"/> banka beklentisi (W2) için: beklenti işin sahip
/// satırına yazılır (işsiz yollarda kişinin belirlenimli sahibi; GUID'li üye yoksa null → beklenti yok),
/// indirilen her iş için (CustomerId, OriginalScopeKey) beklentisi iptal edilir. Sahip yeniden
/// türetilmez.</para>
/// <para><see cref="Message"/>: operatöre gösterilecek metin — <see cref="PaymentRequestResult.Blocked"/>'ta her zaman,
/// <see cref="PaymentRequestResult.PaymentInProgress"/>'ta kirayı tutan bilgisayarın adı biliniyorsa; öbür sonuçlarda
/// null (metni <see cref="PaymentResultPresenter"/> kurar).</para></summary>
public sealed record PaymentRequestOutcome(
    PaymentRequestResult Result,
    PaymentPrompt? Prompt = null,
    string? OwnerId = null,
    IReadOnlyList<DemotedJob>? Demoted = null,
    string? Message = null);

/// <summary>
/// Phase 4g: Customer + tutar + tarih → Settings template substitution + wa.me launch.
/// Phone null/invalid ise PhoneRequired (caller PhoneEntryDialog açar).
///
/// Kargo entegrasyon (2026-05-12): caller ürün toplamını geçer, service
/// kargo ayarı (IShippingSettingsProvider: mod Off'ta AppSettings.Shipping, mod On'da
/// lisans geneli ayar) + Customer.RecipientPaysActive flag'ine göre kargo
/// ücretini hesaplar, template'e ShippingNote + final TotalAmount yansıtır.
/// Mevcut caller signature'ları aynı kalır (productTotal eski "totalAmount"un
/// semantic karşılığı — caller zaten label sum geçiriyordu).
/// </summary>
public sealed class PaymentRequestService
{
    private readonly SettingsStore _settingsStore;
    private readonly WhatsAppMessageBuilder _messageBuilder;
    private readonly IUrlLauncher _launcher;
    private readonly LicenseApiClient _api;
    private readonly ICurrentLicenseProvider _currentLicense;
    private readonly IPaymentJobStore _jobs;
    private readonly IShippingSettingsProvider _shipping;
    private readonly Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? _log;
    private readonly CustomerRepository? _customers;
    private readonly CustomerBusySet? _busy;
    private readonly PaymentOffRecordWriter? _offRecords;
    private readonly PaymentLeaseCoordinator? _lease;

    /// <summary>Kişi başına özel ödeme kilidi — servis tekil (AppHost), yani süreç içi tek kilit tablosu.</summary>
    private readonly PaymentPersonGate _personGate = new();

    /// <summary>Aynı kişinin başka bir akışını bekleme süresi; dolarsa
    /// <see cref="PaymentRequestResult.PaymentInProgress"/>. Testler kısaltır.</summary>
    internal TimeSpan PersonGateTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <param name="shipping">Kargo eşiği ve ücretinin kaynağı (spec 2026-10-13 §6.2): mod Off'ta yerel ayar, mod On'da
    /// lisans geneli ayar. Mesaj şablonu ve hesap bilgileri <paramref name="settingsStore"/>'dan okunmaya devam eder.</param>
    /// <param name="customers">U12 (Bölüm C): bakiye akışının girişinde müşteri Id'sini yerel
    /// yönlendirme tablosundan çözer. DI verir; null yalnız testlerde (Id olduğu gibi kullanılır).</param>
    /// <param name="busy">U13: senkron deposuyla paylaşılan TEK meşgul müşteri kümesi (DI tekil
    /// örneği — ikinci bir örnek kirayı senkrona görünmez yapardı). Null yalnız testlerde (kira yok).</param>
    /// <param name="offRecords">Mod Off'ta her teslim ya da belirsiz gönderimden sonraki kirasız ödeme kaydı (spec
    /// 2026-10-13 §6.4 "Ödeme geçmişi (mod Off'ta da)"; W3-06). DI verir; null yalnız testlerde (kayıt yok).</param>
    /// <param name="lease">Mod On sarmalı (spec 2026-10-13 §6.4; W3-07): ön gönderim, bekleyen komut engeli, kapsamdan
    /// yeniden okunan tutar, bakiyeden önce kira (<c>begin</c>; fail-closed) ve her çıkışta <c>finish</c>. Yalnız girişte ve
    /// çıkışta çağrılır, ödeme işinin durum makinesi değişmez; yerel kip Off'ta hiçbir şey yapmaz. DI verir; null yalnız
    /// testlerde (bugünkü yol).</param>
    public PaymentRequestService(
        SettingsStore settingsStore,
        WhatsAppMessageBuilder messageBuilder,
        IUrlLauncher launcher,
        LicenseApiClient api,
        ICurrentLicenseProvider currentLicense,
        IPaymentJobStore jobs,
        IShippingSettingsProvider shipping,
        Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? log = null,
        CustomerRepository? customers = null,
        CustomerBusySet? busy = null,
        PaymentOffRecordWriter? offRecords = null,
        PaymentLeaseCoordinator? lease = null)
    {
        _settingsStore = settingsStore;
        _messageBuilder = messageBuilder;
        _launcher = launcher;
        _api = api;
        _currentLicense = currentLicense;
        _jobs = jobs;
        _shipping = shipping;
        _log = log;
        _customers = customers;
        _busy = busy;
        _offRecords = offRecords;
        _lease = lease;
    }

    // License key → Guid LicenseId resolution. Aynı pattern CustomerChangesPullService /
    // WpfCustomerProjectionSync'te de var. Caching: LicenseKey değişene kadar tutar.
    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    /// <summary>Bakiye özelliği açık (lisans anahtarı var) ama lisans kimliği çözülemiyor — sunucuya
    /// ulaşılamıyor. Akış bu durumda bakiyeyi DENEMEDEN gönderebilir (iş henüz uygulanmamışsa —
    /// <see cref="OfflineOutcome"/>); fiyat düzeltmesinin toplu gönderimi bunu "çevrimdışı" diye atlar (K5).
    /// Anahtar yoksa bakiye özelliği zaten kapalı: çevrimdışı sayılmaz.</summary>
    public async Task<bool> IsBalanceOfflineAsync(CancellationToken ct = default)
        => !string.IsNullOrEmpty(_currentLicense.CurrentLicenseKey) && await ResolveLicenseIdAsync(ct) is null;

    private async Task<Guid?> ResolveLicenseIdAsync(CancellationToken ct)
    {
        var key = _currentLicense.CurrentLicenseKey;
        if (string.IsNullOrEmpty(key)) return null;
        if (_cachedLicenseId is not null && _cachedLicenseKey == key)
            return _cachedLicenseId;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == key);
            if (match?.Id is null) return null;
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = key;
            return _cachedLicenseId;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.LogDebug("Lisans id çözümü zaman aşımına uğradı");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogDebug(ex, "License id resolve failed for payment request");
            return null;
        }
    }

    /// <param name="productTotal">Müşterinin ürün label'larının toplamı
    /// (kargo HARİÇ). Service kargo ücretini settings + customer flag'e göre
    /// otomatik ekler.</param>
    public PaymentRequestResult OpenWhatsApp(Customer customer, decimal productTotal, DateTime streamDate)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var (totalAmount, shippingFee, shippingNote) =
            ComputeShipping(customer, productTotal, _shipping.Current().Settings);

        var ctx = new PaymentContext(
            DisplayName: customer.DisplayName ?? customer.Username,
            TotalAmount: totalAmount,
            StreamDate: streamDate,
            Iban: settings.Payment.Iban,
            AccountHolder: settings.Payment.AccountHolder,
            Papara: settings.Payment.Papara,
            ProductTotal: productTotal,
            ShippingFee: shippingFee,
            ShippingNote: shippingNote);

        var message = _messageBuilder.BuildMessage(settings.Payment.WhatsAppMessageTemplate, ctx);
        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            return PaymentRequestResult.LaunchFailed;
        }
    }

    /// <summary>
    /// E3b (2026-06): bakiye-aware async versiyon. Müşterinin yayıncı bazlı
    /// bakiyesi varsa <see cref="LicenseApiClient.ApplyBalanceAsync"/> çağırıp
    /// ledger'a purchase-deduction kaydı düşer, WhatsApp template'inde
    /// {bakiye} / {net_tutar} placeholder'ları dolar. Sonra wa.me link açılır.
    ///
    /// R2-01..04 (2026-09-11): bakiye sonucu KESİNLEŞMEDEN mesaj gönderilmez —
    /// belirsizlikte <see cref="PaymentRequestResult.BalanceUncertain"/> döner.
    /// </summary>
    /// <param name="scopeKey">Satışın kalıcı kimlik kapsamı:
    /// "session:{id}" (yayın raporu) | "cumulative" (genel bakiye).
    /// Aynı kapsam + aynı müşteri = aynı satış; tutar değişirse revizyon.</param>
    public Task<PaymentRequestResult> OpenWhatsAppAsync(
        Customer customer, decimal productTotal, DateTime streamDate,
        string scopeKey, CancellationToken ct = default)
        => OpenWhatsAppAsync(customer, _ => productTotal, streamDate, scopeKey, ct);

    /// <summary><see cref="RequestPaymentAsync"/>'in yalnız sonucu dönen biçimi (onay belirteci
    /// vermez: teslim edilmiş istekte <see cref="PaymentRequestResult.AlreadyRequested"/> döner).</summary>
    public async Task<PaymentRequestResult> OpenWhatsAppAsync(
        Customer customer, Func<PaymentPerson, decimal?> productTotalFor, DateTime streamDate,
        string scopeKey, CancellationToken ct = default)
        => (await RequestPaymentAsync(customer, productTotalFor, streamDate, scopeKey, null, ct)).Result;

    /// <summary>
    /// "Ödeme iste" — KİŞİ düzeyinde (tasarım 1, 2026-10-10). Tutar akış girişinde yeniden okunur
    /// (U12, Bölüm C).
    ///
    /// <para>Pencere ya da liste açıkken senkron müşteriyi başka Id'ye taşımış olabilir: akış
    /// tıklanan satırın güncel Id'sini KİRALAR, kişisini (aynı gruptaki bütün satırlar) okur, kişinin
    /// bütün üyelerini özel ödeme kilidiyle (<see cref="PaymentPersonGate"/>) ve senkron kirasıyla
    /// (<see cref="CustomerBusySet"/>) tutar. Kira sürdükçe senkron hiçbir üyeyi taşımaz/dönüştürmez
    /// (U13) — iş uçuşta yeniden adlandırılıp kapatılamaz (U8'in çift düşüm penceresi).</para>
    ///
    /// <para>Ödeme işi kişi × kapsamdır: kart birincili ya da tıklanan satır ne olursa olsun aynı kişi
    /// ve kapsam için aynı iş bulunur (<see cref="IPaymentJobStore.FindOrCreateForPerson"/>). Sunucuya
    /// giden her çağrı işin SAHİP satırının Id'siyle yapılır.</para>
    ///
    /// <para>Gönderim, çağıranın tuttuğu nesneyle değil kira altında okunan GÜNCEL satırlarla yapılır:
    /// ad tıklanan satırdan; telefon ve alıcı ödemeli kişi düzeyinde (<see cref="PaymentPerson.ContactPhone"/>,
    /// <see cref="PaymentPerson.RecipientPaysActive"/>) — kart ve rapor farklı satırları kiralasa da aynı
    /// toplam. KVKK silmesi boşaltılmış satırı bırakmış olabilir — silinmiş kişinin eski telefonuna
    /// mesaj gitmez.</para>
    /// </summary>
    /// <param name="productTotalFor">Kira altında okunan GÜNCEL kişiyle çağrılır ve kişinin ürün
    /// toplamını döner (kart: üyelerin yayın/kümülatif toplamı; rapor: üyelerin yayın toplamı — ikisi
    /// aynı kişide aynı sonucu vermeli, yoksa iş girişler arasında sürekli revize edilir). Üye kümesi
    /// akış sırasında değişirse yeniden çağrılabilir. <c>null</c> = çağıran reddetti (anlık görüntüsü
    /// bayat): <see cref="PaymentRequestResult.ListStale"/> döner, hiçbir şey gönderilmez.</param>
    /// <param name="confirmedTokens">Operatörün onayladığı soruların belirteçleri
    /// (<see cref="PaymentPrompt.Token"/>).</param>
    public async Task<PaymentRequestOutcome> RequestPaymentAsync(
        Customer customer, Func<PaymentPerson, decimal?> productTotalFor, DateTime streamDate,
        string scopeKey, IReadOnlySet<string>? confirmedTokens = null, CancellationToken ct = default)
    {
        // Mod On'da kiranın yeniden denenen retleri (spec 2026-10-13 §6.4 "409'ların istemcideki karşılığı"; W3-08): koşu
        // döndüğünde kişi kilitleri ve tıklanan müşterinin kirası BIRAKILMIŞTIR — tur kira tutulurken işe yaramazdı (müşteri
        // ve satış akışının ApplyItem'ı meşgul müşteriyi erteler). Tur koşar, sonra bütün akış AYNI onay belirteçleriyle
        // yeniden koşar. Her hak bir kez; harcanmış hakkın reddi satır içi engeldir (koordinatör karar verir).
        var retried = PaymentLeaseRetry.None;
        // mode-off'tan sonraki yeniden kurmada ilk koşunun begin toplamı (uzak etiketler dahil): yerel toplam 0'a düştüyse
        // mesaj gitmez (PaymentLeaseCoordinator.CheckModeOffScope).
        decimal? modeOnTotal = null;
        while (true)
        {
            var run = new LeaseRun();
            var outcome = await RequestPaymentOnceAsync(
                customer, productTotalFor, streamDate, scopeKey, confirmedTokens, retried, modeOnTotal, run, ct);
            var retry = run.Flow?.PendingRetry ?? PaymentLeaseRetry.None;
            if (retry == PaymentLeaseRetry.None) return outcome;

            var stop = await _lease!.RunRetryAsync(retry, ct);
            if (stop is not null) return stop with { OwnerId = outcome.OwnerId };
            if ((retry & PaymentLeaseRetry.ModeOffRound) != 0) modeOnTotal = run.Flow!.BegunProductTotal;
            retried |= retry;
        }
    }

    /// <summary>Bir koşunun mod On akışı (<see cref="PaymentLeaseCoordinator.EnterAsync"/>; yerel kip Off'ta null).</summary>
    private sealed class LeaseRun
    {
        public PaymentLeaseFlow? Flow { get; set; }
    }

    /// <summary><see cref="RequestPaymentAsync"/>'in bir koşusu; mod On akışı <paramref name="run"/>'a yazılır.</summary>
    /// <param name="modeOnTotal">Bu koşu <c>begin</c>'in <c>mode-off</c>'undan sonraki yeniden kurmaysa ilk koşunun begin
    /// toplamı; değilse null.</param>
    private async Task<PaymentRequestOutcome> RequestPaymentOnceAsync(
        Customer customer, Func<PaymentPerson, decimal?> productTotalFor, DateTime streamDate,
        string scopeKey, IReadOnlySet<string>? confirmedTokens, PaymentLeaseRetry retried, decimal? modeOnTotal,
        LeaseRun run, CancellationToken ct)
    {
        // Mod On (spec 2026-10-13 §6.4-1, W3-07): ön gönderim akışın EN BAŞINDA, hiçbir kira alınmadan — bekleyen
        // komutların yanıt satırları ve gönderimlerin yönlendirmeleri bu kişinin kirasını beklemesin (U13/U17). Yerel kip
        // Off'ta null: bugünkü yol.
        PaymentLeaseFlow? paymentLease;
        bool enteredModeOff;
        try
        {
            paymentLease = _lease is null ? null : await _lease.EnterAsync(retried, ct);
            run.Flow = paymentLease;
            // Kirasız kaydın kararı GİRİŞTE verilir (spec 2026-10-13 §6.4 "Ödeme geçmişi (mod Off'ta da)"): bu akış mod Off
            // yolundan gidiyorsa teslim ya da belirsiz gönderimden sonra kaydı yazılır — akış ağda beklerken açılış turu yerel
            // kipi 1'e çevirse de. Teslimden sonra kip yeniden okunsaydı kirasız giden istek ne kira ne kayıt bırakırdı;
            // ödeme geçmişi o ana dek tamamlanmış olduğundan onu bir daha hiçbir yol göndermezdi. Kira sarmalı bağlıyken
            // (üretim) null akış kipin On olmadığı demektir; sarmalsız kurulumda (testler) kip burada bir kez okunur.
            enteredModeOff = paymentLease is null && (_lease is not null || _offRecords?.IsModeOff() == true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogError(ex, "Ödeme akışının kip kapısı okunamadı — mesaj engellendi (customer={CustomerId})",
                customer.Id);
            return new(PaymentRequestResult.BalanceUncertain);
        }

        IDisposable lease;
        string customerId;
        try
        {
            (lease, customerId) = await EnterCustomerAsync(customer.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // M-1: giriş (Id çözümü, kiralama) yerel bir hatayla düşerse mesaj engellenir —
            // genel hata kutusuna gitmez; operatör tekrar dener.
            _log?.LogError(ex, "Ödeme akışının girişi düştü — mesaj engellendi (customer={CustomerId})",
                customer.Id);
            return new(PaymentRequestResult.BalanceUncertain);
        }

        using (lease)
        {
            // Üye kümesi akış sırasında değişirse (elle birleştirme kiraya takılmaz) iş, yazma
            // işleminin içinde bunu görür (MembershipChanged) ve kişi yeniden okunur.
            for (var attempt = 0; attempt < MaxEnterAttempts; attempt++)
            {
                PersonHold? hold;
                try
                {
                    hold = await EnterPersonAsync(customer, customerId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.LogError(ex, "Ödeme kişisi kiralanamadı — mesaj engellendi (customer={CustomerId})",
                        customerId);
                    return new(PaymentRequestResult.BalanceUncertain);
                }
                if (hold is null)
                {
                    _log?.LogWarning("Bu kişi için başka bir ödeme akışı sürüyor (customer={CustomerId})", customerId);
                    return new(PaymentRequestResult.PaymentInProgress);
                }

                using (hold)
                {
                    var person = hold.Person;
                    decimal? productTotal;
                    Customer current;
                    try
                    {
                        // Önce çağıranın reddi: bayat kartta operatöre telefon da sorulmaz.
                        productTotal = productTotalFor(person);
                        if (productTotal is null) return new(PaymentRequestResult.ListStale);
                        current = _customers?.GetById(person.LeasedId) ?? customer with { Id = person.LeasedId };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log?.LogError(ex, "Ödeme akışının tutarı/müşterisi okunamadı — mesaj engellendi (customer={CustomerId})",
                            customerId);
                        return new(PaymentRequestResult.BalanceUncertain);
                    }

                    // Mod Off yolu: kapsamın yayını bu bilgisayarda değilse (Off turu uzak yayını sildi) ya da mode-off'tan
                    // sonraki yeniden kurmada kişinin bütün etiketleri uzaktıysa mesaj gitmez — telefon da sorulmaz.
                    if (paymentLease is null
                        && _lease?.CheckModeOffScope(person, scopeKey, productTotal.Value, modeOnTotal) is { } offStop)
                        return offStop with { OwnerId = person.OwnerId };

                    var phone = person.ContactPhone;
                    if (!PhoneNormalizer.IsValidTr(phone))
                        return new(PaymentRequestResult.PhoneRequired, OwnerId: person.OwnerId);
                    var contact = current with { Phone = phone, RecipientPaysActive = person.RecipientPaysActive };

                    var outcome = await OpenWhatsAppCoreAsync(
                        contact, person, productTotal.Value, streamDate, scopeKey, confirmedTokens, paymentLease,
                        enteredModeOff, ct);
                    if (outcome is not null) return outcome;
                    _log?.LogInformation(
                        "Ödeme kişisinin üye kümesi akış sırasında değişti — yeniden okunuyor (customer={CustomerId})",
                        customerId);
                }
            }
            _log?.LogError("Ödeme kişisi kararlı okunamadı — mesaj engellendi (customer={CustomerId})", customerId);
            return new(PaymentRequestResult.BalanceUncertain);
        }
    }

    /// <summary>Kişinin kilitleri: özel ödeme kilidi + tıklanan dışındaki üyelerin senkron kiraları.</summary>
    private sealed class PersonHold(PaymentPerson person, IReadOnlyList<IDisposable> holds) : IDisposable
    {
        public PaymentPerson Person { get; } = person;

        public void Dispose()
        {
            for (var i = holds.Count - 1; i >= 0; i--) holds[i].Dispose();
        }
    }

    /// <summary>Kişiyi okur, bütün üyelerini özel ödeme kilidiyle ve senkron kirasıyla tutar. Kiralar
    /// kümenin kilidinden geçer: o ana kadar başlamış taşıma commit edilmiştir; bir üye arada taşındıysa
    /// ya da üye kümesi değiştiyse bütün kilitler bırakılır ve baştan okunur. Bekleme süresi dolarsa
    /// (başka pencerenin akışı sürüyor) <c>null</c>.
    ///
    /// <para>U17: sıra tıklanan satırın kirası → kişi kilidi → öbür üyelerin kiraları → (sonra) SQLite
    /// yazma kilidi; hiçbiri açık bir veritabanı işlemi içinden alınmaz.</para></summary>
    private async Task<PersonHold?> EnterPersonAsync(Customer customer, string leasedId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxEnterAttempts; attempt++)
        {
            var person = ReadPerson(customer, leasedId);
            var gate = await _personGate.TryEnterAsync(person.MemberIds, PersonGateTimeout, ct);
            if (gate is null) return null;

            var holds = new List<IDisposable> { gate };
            try
            {
                if (_busy is not null)
                    foreach (var m in person.MemberIds)
                        if (!string.Equals(m, leasedId, StringComparison.Ordinal))
                            holds.Add(await _busy.EnterAsync(m, ct));

                var again = ReadPerson(customer, leasedId);
                var moved = _customers is not null
                    && _customers.ResolveIds(person.MemberIds).Any(kv => !string.Equals(kv.Key, kv.Value, StringComparison.Ordinal));
                if (!moved && string.Equals(again.MemberKey, person.MemberKey, StringComparison.Ordinal))
                    return new PersonHold(again, holds);
            }
            catch
            {
                for (var i = holds.Count - 1; i >= 0; i--) holds[i].Dispose();
                throw;
            }
            for (var i = holds.Count - 1; i >= 0; i--) holds[i].Dispose();
        }
        throw new InvalidOperationException($"Ödeme kişisinin üyeleri kararlı kiralanamadı (customer={leasedId})");
    }

    /// <summary>Kiralanan satırın kişisi; depo yoksa (testler) ya da satır okunamadıysa tek satırlık kişi.</summary>
    private PaymentPerson ReadPerson(Customer customer, string leasedId)
    {
        var person = _customers?.GetPaymentPerson(leasedId);
        return person is { Members.Count: > 0 } ? person : PaymentPerson.Single(customer with { Id = leasedId });
    }

    /// <summary>Müşteriyi kiralar ve kiralanan Id'nin güncel olduğunu doğrular: kiralama kümenin
    /// kilidinden geçer, yani o ana kadar başlamış bir taşıma commit edilmiştir; çözüm değiştiyse
    /// (çözümle kiralama arasında taşındı) o kira bırakılır ve güncel Id kiralanır.
    ///
    /// <para>U17: hiçbir veritabanı işlemi açıkken çağrılmaz; arayüz iş parçacığında yalnız
    /// <c>await</c> ile beklenir (küme kilidi bir senkron öğesinin yazma işlemini bekliyor
    /// olabilir).</para></summary>
    private async Task<(IDisposable Lease, string CustomerId)> EnterCustomerAsync(
        string customerId, CancellationToken ct)
    {
        if (_busy is null) return (NoLease.Instance, ResolveCustomerId(customerId));

        var id = customerId;
        for (var attempt = 0; attempt < MaxEnterAttempts; attempt++)
        {
            var resolved = ResolveCustomerId(id);
            var lease = await _busy.EnterAsync(resolved, ct);
            string current;
            try
            {
                current = ResolveCustomerId(resolved);
            }
            catch
            {
                // C4 incelemesi: sızan kira o müşteriyi uygulama yeniden başlayana dek meşgul
                // tutardı — senkron akışı her turda o öğede Busy olur, arkasındaki KVKK
                // silmeleri dahil her şey beklerdi.
                lease.Dispose();
                throw;
            }
            if (string.Equals(current, resolved, StringComparison.Ordinal)) return (lease, resolved);
            lease.Dispose();
            id = current;
        }
        throw new InvalidOperationException($"Müşteri Id'si kararlı çözülemedi (customer={customerId})");
    }

    /// <summary>Her deneme bir taşımanın çözümle kiralama arasına girmesi demek; taşıma tek
    /// yönlü (kopya → asıl) ve zincir kısaltılarak yazıldığı için birkaç deneme fazlasıyla yeter.</summary>
    private const int MaxEnterAttempts = 8;

    private string ResolveCustomerId(string id) => _customers?.ResolveId(id) ?? id;

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();
        public void Dispose() { }
    }

    /// <summary>Bakiye akışının gövdesi — kişi kiralı, <paramref name="customer"/> kira altında okunan
    /// iletişim satırı (ad tıklanan satırdan; telefon ve alıcı ödemeli kişi düzeyinde). <c>null</c> =
    /// üye kümesi yazma işleminin içinde değişmiş bulundu; çağıran kişiyi yeniden okur (hiçbir şey
    /// yazılmadı, gönderilmedi).
    ///
    /// <para>Mod On (<paramref name="paymentLease"/> dolu; spec 2026-10-13 §6.4, W3-07): kişi okunduktan sonra bekleyen
    /// komut engeli ve tutarın kapsamdan yeniden okunması — mesajın tutarı da kiranın tutarı da bu okumadan; kargo ayarı
    /// tek anlık görüntüden. Kira (<c>begin</c>) bakiyeden ÖNCE; çıkışta, hangi yoldan olursa olsun (soru, belirsizlik,
    /// üye kümesi değişti, istisna dahil), <c>finish</c> (<see cref="LeaseExit"/>).</para>
    /// <param name="enteredModeOff">Akış girişte mod Off yolunu seçti: teslim ya da belirsiz gönderimden sonra kirasız kayıt
    /// yazılır (<see cref="RecordWithoutLease"/>).</param></summary>
    private async Task<PaymentRequestOutcome?> OpenWhatsAppCoreAsync(
        Customer customer, PaymentPerson person, decimal productTotal, DateTime streamDate,
        string scopeKey, IReadOnlySet<string>? confirmedTokens, PaymentLeaseFlow? paymentLease, bool enteredModeOff,
        CancellationToken ct)
    {
        var exit = new LeaseExit { EnteredModeOff = enteredModeOff };
        try
        {
            return await SendCoreAsync(
                customer, person, productTotal, streamDate, scopeKey, confirmedTokens, paymentLease, exit, ct);
        }
        finally
        {
            if (paymentLease is not null) await paymentLease.FinishAsync(exit.Outcome, exit.Net, exit.Applied);
        }
    }

    /// <summary>
    /// Kiranın sonucu (spec 2026-10-13 §6.4 "Akışın sonu"): gönderim çıkışları işaretler — <c>Sent</c>/<c>Opened</c> →
    /// <c>delivered</c>; <c>SendPending</c> ve Cloud API'nin sonucu bilinmeden açılamayan wa.me → <c>unconfirmed</c>;
    /// gönderim denemesinden önceki her dönüş ve <c>AbandonSend</c> (varsayılan) → <c>abandoned</c>. Net ve düşüm akıştan.
    /// </summary>
    private sealed class LeaseExit
    {
        public string Outcome { get; private set; } = PaymentRequestStates.Abandoned;
        public decimal Net { get; set; }

        /// <summary>Bakiyeden düşülen; bakiye adımı sonuçlanmadıysa ya da kişide bakiye işi yoksa null.</summary>
        public decimal? Applied { get; set; }

        /// <summary>Akış girişte mod Off yolunu seçti (kira yok): gönderim çıkışlarında kirasız kayıt yazılır
        /// (<see cref="RecordWithoutLease"/>) — kip sonradan değişse de.</summary>
        public bool EnteredModeOff { get; init; }

        public void Delivered() => Outcome = PaymentRequestStates.Delivered;
        public void Unconfirmed() => Outcome = PaymentRequestStates.Unconfirmed;
    }

    private async Task<PaymentRequestOutcome?> SendCoreAsync(
        Customer customer, PaymentPerson person, decimal productTotal, DateTime streamDate,
        string scopeKey, IReadOnlySet<string>? confirmedTokens, PaymentLeaseFlow? paymentLease, LeaseExit exit,
        CancellationToken ct)
    {
        var settings = _settingsStore.Load();
        var shipping = _shipping.Current();
        IReadOnlyList<Guid> orderIds = [];
        if (paymentLease is not null)
        {
            var amount = paymentLease.ReadAmount(person, scopeKey);
            if (amount.Stop is { } stop) return stop with { OwnerId = person.OwnerId };
            productTotal = amount.ProductTotal;
            orderIds = amount.OrderIds;
        }
        var (totalAmount, shippingFee, shippingNote) =
            ComputeShipping(customer, productTotal, shipping.Settings);
        exit.Net = totalAmount;

        // Mod On: kişi × kapsamın kirası bakiyeden ÖNCE (GUID'siz kişide de). Alınamazsa mesaj gitmez (M9). Onay alanları
        // operatörün onayladığı uzak öncüllerden (W3-09 sorar); akışın yeniden koşuları aynı belirteçlerle gelir.
        if (paymentLease is not null)
        {
            var confirmation = PaymentLeaseConfirmation.From(confirmedTokens);
            while (true)
            {
                var stop = await paymentLease.BeginAsync(new PaymentLeaseBeginInput(
                    person, scopeKey, orderIds, productTotal, totalAmount, customer.RecipientPaysActive,
                    shipping.Version ?? 0, confirmation), ct);
                if (stop is null) break;
                // already-requested (§6.4-e; W3-09): sunucunun öncülleri sahip kararı 2'nin sorusuyla sorulur; yerel planın
                // sorusu aynı diyaloğa katılır (tek onay iki kapıyı da geçirir).
                if (stop is { Result: PaymentRequestResult.AlreadyRequested, Prompt: { } remote })
                    return stop with { Prompt = WithLocalPlan(remote, person, scopeKey), OwnerId = person.OwnerId };
                // settings-stale (§6.2; W3-08): kargo önbelleği sunucunun güncel ayarıyla yenilendi — kargo yeni anlık
                // görüntüden yeniden hesaplanır ve kira YERİNDE bir kez daha istenir (kişi bırakılmaz); mesaj da yeni
                // ücretle gider. Koşu başına bir kez; sonraki ret engeldir.
                if (!paymentLease.TakeShippingRecompute()) return stop with { OwnerId = person.OwnerId };
                shipping = _shipping.Current();
                (totalAmount, shippingFee, shippingNote) = ComputeShipping(customer, productTotal, shipping.Settings);
                exit.Net = totalAmount;
            }
        }

        // Bakiye uygulaması — PaymentJob durum makinesi (R2-01..04).
        // Eski fail-silent davranış BİLEREK terk edildi: sonuç belirsizse mesaj
        // gönderilmez (BalanceUncertain). "Bakiyesi düşmüş ama mesajı yanlış
        // tutarlı" ihtimali, "operatör bir kez daha tıklar" maliyetinden ağır.
        decimal appliedBalance = 0m;
        var totalBeforeBalance = totalAmount;
        PaymentJob? deliveryJob = null;   // mesaj müşteriye ulaşınca teslim kaydı yazılacak iş
        // W2: beklentinin sahibi — iş varsa onun sahip satırı, işsiz yolda kişinin belirlenimli sahibi.
        var ownerId = person.OwnerId;
        IReadOnlyList<DemotedJob> demoted = Array.Empty<DemotedJob>();
        // S5: sıfır toplam da bakiye akışına girer. Eski kapı (totalAmount > 0)
        // satıştaki tüm ürünler kaldırıldığında akışı komple atlıyordu; sunucuda
        // duran düşüm ise yerinde kalıyordu — müşterinin parası, karşılığı olmayan
        // bir satışta asılı kalırdı. Sıfır tutar bir satışın İPTALİDİR ve iadeyi
        // hak eder. Düşülecek bir şey yoksa aşağıdaki akış zaten ağa çıkmadan
        // "bakiye yok" ile kapanır.
        //
        // Kişinin GUID'li bir üyesi varsa (sahip) bakiye akışına girilir — tıklanan satır GUID'siz
        // olsa da. GUID'li üye yoksa iş yoktur (sunucu GUID olmayan Id'ye düşüm yazamaz).
        if (person.OwnerId is not null)
        {
            // Kira tutuluyken kapsam uzlaştırmasının adaylarına begin'in sunucu üyeleri de katılır (§6.4).
            var outcome = await ResolveBalanceAsync(person, totalAmount, scopeKey, confirmedTokens,
                paymentLease?.ServerMembers ?? [], ct);
            if (outcome.MembershipChanged) return null;
            demoted = outcome.Demoted ?? demoted;
            ownerId = outcome.Job?.CustomerId ?? ownerId;
            if (outcome.Prompt is { } prompt)
                return new(prompt.Kind == PersonScopePromptKind.Merge
                        ? PaymentRequestResult.MergeRequired
                        : PaymentRequestResult.AlreadyRequested,
                    prompt, ownerId, demoted);
            if (outcome.Uncertain)
                return new(PaymentRequestResult.BalanceUncertain, null, ownerId, demoted);
            deliveryJob = outcome.Job;
            appliedBalance = outcome.AppliedBalance;
            totalAmount -= appliedBalance;
        }
        exit.Net = totalAmount;
        exit.Applied = deliveryJob is not null ? appliedBalance : null;

        PaymentRequestOutcome Done(PaymentRequestResult result) => new(result, null, ownerId, demoted);

        var ctx = new PaymentContext(
            DisplayName: customer.DisplayName ?? customer.Username,
            TotalAmount: totalAmount,
            StreamDate: streamDate,
            Iban: settings.Payment.Iban,
            AccountHolder: settings.Payment.AccountHolder,
            Papara: settings.Payment.Papara,
            ProductTotal: productTotal,
            ShippingFee: shippingFee,
            ShippingNote: shippingNote,
            AppliedBalance: appliedBalance,
            TotalBeforeBalance: totalBeforeBalance);

        var message = _messageBuilder.BuildMessage(settings.Payment.WhatsAppMessageTemplate, ctx);

        // Gönderim denemesi ağ/WhatsApp adımından ÖNCE iş satırına yazılır (onay belirtecine girer).
        // Sonucu yazılamasa da (süreç düştü, teslim yazımı hata verdi) iş "gitmiş olabilir" görünür:
        // öbür pencerede bekleyen eski onay tutmaz, sıradaki tıklama "tekrar gönderilsin mi?" diye
        // sorar. Yazılamazsa mesaj gönderilmez — sonucu hiç kaydedilemeyecek bir gönderim sorusuz
        // ikinci kez gidebilirdi. Bakiye sonucu kesin; operatörün tekrar denemesi çift düşüm yapmaz.
        if (deliveryJob is not null && !TryBeginSend(deliveryJob, totalAmount))
            return Done(PaymentRequestResult.BalanceUncertain);

        // Cloud API açıksa doğrudan gönder. Başarısızsa (pencere kapalı, hesap
        // yok, ağ hatası) sessizce wa.me'ye düşeriz — yayıncı elle gönderir,
        // yani en kötü ihtimalde eski davranış.
        var cloudMayHaveSent = false;
        if (settings.Payment.UseCloudApi)
        {
            switch (await TrySendViaCloudApiAsync(
                customer.Phone!, message, BuildTemplateRef(settings, ctx), "wpf-payment", ct))
            {
                case CloudSendOutcome.Sent:
                    exit.Delivered();
                    MarkDelivered(deliveryJob, totalAmount);
                    RecordWithoutLease(paymentLease, exit, person, deliveryJob, scopeKey, unconfirmed: false, totalAmount);
                    return Done(PaymentRequestResult.Sent);
                case CloudSendOutcome.Pending:
                    // Sonuç bilinmiyor: mesaj gitmiş olabilir — teslim kaydı yazılır, sıradaki tıklama
                    // (bu pencerede ya da öbüründe) "tekrar gönderilsin mi?" diye sorar; bildirim de
                    // doğrulayıp gerekirse tekrar denemeyi söylüyor. Bakiye sonucu zaten kesin: sonraki
                    // deneme aynı anahtarı yeniden kullanır (R2-01).
                    exit.Unconfirmed();
                    MarkDelivered(deliveryJob, totalAmount);
                    RecordWithoutLease(paymentLease, exit, person, deliveryJob, scopeKey, unconfirmed: true, totalAmount);
                    return Done(PaymentRequestResult.SendPending);
                case CloudSendOutcome.FallBackUnknown:
                    cloudMayHaveSent = true;
                    break;
            }
        }

        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
        }
        catch
        {
            // İş açık kalır: operatörün ikinci tıklaması aynı anahtarı bulur,
            // bakiye ikinci kez düşmez (R2-01). wa.me açılmadı — mesaj gitmedi: deneme geri alınır,
            // sıradaki tıklama sorusuz yeniden dener. Cloud API sonucu bilinmeden düşüldüyse deneme
            // kalır: o gönderim ulaşmış olabilir (mod Off'ta belirsiz kayıt da yazılır).
            if (!cloudMayHaveSent) AbandonSend(deliveryJob);
            else
            {
                exit.Unconfirmed();
                RecordWithoutLease(paymentLease, exit, person, deliveryJob, scopeKey, unconfirmed: true, totalAmount);
            }
            return Done(PaymentRequestResult.LaunchFailed);
        }
        exit.Delivered();
        MarkDelivered(deliveryJob, totalAmount);
        RecordWithoutLease(paymentLease, exit, person, deliveryJob, scopeKey, unconfirmed: false, totalAmount);
        return Done(PaymentRequestResult.Opened);
    }

    /// <summary>
    /// Mod Off'ta teslim ya da belirsiz gönderimden sonraki kirasız ödeme kaydı (spec 2026-10-13 §6.4 "Ödeme geçmişi
    /// (mod Off'ta da)"; W3-06): yalnız kuyruğa yazar, ağa çıkmaz, mesaj akışını ve soruları değiştirmez. İşli kişide
    /// işin teslimden SONRAKİ satırı okunur (kayıt Id'si teslim ya da deneme sayısından türetilir —
    /// <see cref="PaymentOffRecordWriter"/>). Karar akışın GİRİŞİNDEKİ kipe göredir (<see cref="LeaseExit.EnteredModeOff"/>):
    /// girişte mod Off yolunu seçen akış kip sonradan 1 olsa da yazar. Kiralı akış yazmaz: o yolun kaydı kiradır (W3-07) —
    /// tek istisna, kiraya ulaşılamadığında yerel yoldan giden GUID'siz kişi (<see cref="PaymentLeaseFlow.RecordsLocally"/>,
    /// spec §5.8: "kayıt bağlantı gelince yazılır"). Hata yutulur — mesaj zaten gitti.
    /// </summary>
    private void RecordWithoutLease(
        PaymentLeaseFlow? lease, LeaseExit exit, PaymentPerson person, PaymentJob? job, string scopeKey, bool unconfirmed,
        decimal net)
    {
        if (_offRecords is null) return;
        try
        {
            if (lease is null ? !exit.EnteredModeOff : !lease.RecordsLocally) return;
            var current = job is null ? null : _jobs.Get(job.Id);
            if (job is not null && current is null) return;
            _offRecords.Record(person, current, scopeKey, unconfirmed, net);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Mod Off ödeme kaydı yazılamadı (job={JobId})", job?.Id);
        }
    }

    /// <summary>Gönderim denemesini mesajın netiyle yazar (<see cref="IPaymentJobStore.BeginSend"/>);
    /// yazılamazsa false — çağıran mesajı göndermez.</summary>
    private bool TryBeginSend(PaymentJob job, decimal requestedNet)
    {
        try
        {
            _jobs.BeginSend(job.Id, requestedNet);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Gönderim denemesi yazılamadı — mesaj engellendi (job={JobId})", job.Id);
            return false;
        }
    }

    /// <summary>wa.me açılamadı: deneme geri alınır (<see cref="IPaymentJobStore.AbandonSend"/>). Hata
    /// yutulur — en kötü sıradaki tıklama gereksiz yere "tekrar gönderilsin mi?" diye sorar.</summary>
    private void AbandonSend(PaymentJob? job)
    {
        if (job is null) return;
        try { _jobs.AbandonSend(job.Id); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Ödeme işinin gönderim denemesi geri alınamadı (job={JobId})", job.Id);
        }
    }

    /// <summary>Mesaj müşteriye ulaştı (ya da sonucu bilinmiyor — SendPending) — teslim kaydı yazılır
    /// (her teslimde: tekrar paylaşımda da), sonucu kesin iş kapanır
    /// (<see cref="IPaymentJobStore.MarkDelivered"/>). Disk hatası akışı düşürmemeli: mesaj zaten gitti;
    /// gönderim denemesi ağdan önce yazıldığı için iş yine "gitmiş olabilir" görünür, sıradaki tıklama
    /// sorar ve öbür pencerenin bekleyen onayı tutmaz.</summary>
    private void MarkDelivered(PaymentJob? job, decimal requestedNet)
    {
        if (job is null) return;
        try { _jobs.MarkDelivered(job.Id, requestedNet); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Ödeme işinin teslimi yazılamadı (job={JobId})", job.Id);
        }
    }

    private sealed record BalanceOutcome(bool Uncertain, decimal AppliedBalance, PaymentJob? Job)
    {
        /// <summary>Doluysa akış durur ve operatöre sorulur (hiçbir şey değişmedi).</summary>
        public PaymentPrompt? Prompt { get; init; }

        /// <summary>Üye kümesi yazma işleminin içinde değişmiş bulundu — kişi yeniden okunur.</summary>
        public bool MembershipChanged { get; init; }

        /// <summary>Bu tıklamada miras kapsamına indirilen işler (W2 iptali için).</summary>
        public IReadOnlyList<DemotedJob>? Demoted { get; init; }

        /// <summary>Spec 2026-10-13 §6.6 (W3-11): apply 409 <c>scope-has-deduction</c> aldı ve kapsamdaki düşüm işe
        /// (<see cref="Job"/>) benimsendi — akış revizyon adımına döner (<see cref="ReviseAndSettleAsync"/>). Sonuç tek
        /// başına kesin değildir (Uncertain=true): dönüşü işlemeyen bir çağıran mesajı engeller.</summary>
        public bool ScopeDeductionAdopted { get; init; }
    }

    /// <summary>Bir apply denemesinin (<see cref="ReplayAsync"/>) sonucu.</summary>
    /// <param name="Job">İş satırının deneme sonrası güncel hâli.</param>
    /// <param name="Stale">Spec §6.6: sunucu kapsam kilidini 5 sn içinde alamadı (409 <c>stale</c>) — hiçbir şey
    /// yazılmadı, iş anahtarıyla <c>apply_uncertain</c> kaldı. Çağıran hemen bir kez yeniden dener (en çok iki apply).</param>
    /// <param name="Adopted">Spec §6.6: 409 <c>scope-has-deduction</c> — kapsamdaki düşüm bu işe benimsendi
    /// (<see cref="IPaymentJobStore.AdoptRemoteResult(string, Guid, decimal, decimal, Guid, int)"/>).</param>
    /// <param name="LegacyScopeDeduction">Miras işte 409 <c>scope-has-deduction</c>: benimsenmedi, hiçbir şey yazılmadı —
    /// çağıran #506'nın birleştirme sorusunu sorar.</param>
    private sealed record ApplyAttempt(
        PaymentJob Job, bool Stale = false, bool Adopted = false, ScopeHasDeductionException? LegacyScopeDeduction = null);

    /// <summary>
    /// Lisans id'si çözülemediğinde (sunucuya ulaşılamıyor) verilecek karar. Kişi × kapsamın işi
    /// zaten bulundu/açıldı (<see cref="IPaymentJobStore.FindOrCreateForPerson"/> yereldir).
    ///
    /// Bu dal apply'dan ÖNCE çalışır: bu tıklamada para adına sunucuya tek bir
    /// istek bile gitmemiştir. Yani belirsiz olan sunucuya erişim, satışın
    /// sonucu değil — K3'ün blok gerekçesi burada kendiliğinden geçerli DEĞİL.
    /// Neyi bildiğimizi diskteki iş satırı söyler; SQLite yerel, ağ istemez.
    ///
    /// Her şeyi bloklamak, tek bir VPS kesintisinde bakiyesi hiç olmayan
    /// müşteriler dahil TÜM ödeme mesajlarını durdururdu — yayın ortasında tam
    /// iş durması. Blok yalnızca gerçekten bilinmeyen durumlara saklanır.
    ///
    /// <para>Çevrimdışı yapılan kişi birleştirmesi yalnız yereldir: para taşıyan kaybeden açık
    /// mirastır ve aşağıdaki denetim bu tıklamayı durdurur; sıradaki çevrimiçi tıklama onu geri alır.</para>
    /// </summary>
    private BalanceOutcome OfflineOutcome(PaymentPerson person, PaymentJob job, decimal totalAmount)
    {
        // Açık miras (033→034, taşıma ya da kişi birleştirmesi) iş: anahtarı var, sonucu
        // bilinmiyor — kesinleştirmeden devam edilemez. Kişinin BÜTÜN üyeleri taranır; asıl
        // kapsamı BAŞKA olan miras bu satışı bloklamaz (o kapsamın tıklaması uzlaştırır).
        if (ReconcilableLegacies(person, job.ScopeKey).Count > 0)
            return new(Uncertain: true, 0m, null);

        // created = BeginApply hiç çalışmamış (anahtar yazımı ile durum aynı
        // UPDATE'te değişir) → bu satış için hiç para hareketi yok. İş yine döner: mesaj ulaşınca
        // teslim kaydı yazılır (iş created ve AÇIK kalır) — sıradaki tıklama "tekrar gönderilsin
        // mi?" diye sorar; sormasaydı aynı borç için sorusuz ikinci (çevrimiçiyse düşümlü, farklı
        // tutarlı) mesaj giderdi.
        if (job.State == PaymentJobState.Created)
            return new(Uncertain: false, 0m, job);

        if (job.State is PaymentJobState.Applied or PaymentJobState.NoBalance)
        {
            // Tutar değiştiyse revizyon gerekir; revizyon geri-alma çağrısı
            // ister, o da ağsız olmaz. Eski düşümü yok sayıp tam tutar
            // yazmak müşteriye yanlış rakam göstermek olurdu.
            if (job.ProductTotal != totalAmount)
                return new(Uncertain: true, 0m, job);
            return new(Uncertain: false, job.AppliedAmount ?? 0m, job);
        }

        // apply_uncertain: replay şart; reverse_pending (R4-02): yarım kalmış
        // geri alma uzlaştırılmalı. İkisi de ağsız olmaz.
        return new(Uncertain: true, 0m, job);
    }

    /// <summary>Kişinin bütün üyelerindeki, BU tıklamanın uzlaştıracağı açık miras işleri — en yeniden
    /// eskiye. Sahibi GUID OLMAYAN miras iş uyarı günlüğüyle KAPATILIR, akışı bloklamaz: sunucu GUID
    /// olmayan Id'ye düşüm yazamaz (uzlaştırılacak bir şey yok); bloklasaydı miras tarama bütün
    /// üyelerde ve her kapsamdan önce koştuğu için kişi kalıcı olarak bloklanırdı.
    ///
    /// <para><b>Kapsam:</b> asıl kapsamı bilinen miras iş (<see cref="PaymentJob.PriorScopeKey"/> —
    /// taşıma ya da kişi birleştirmesi indirdi) yalnız O kapsamın tıklamasında uzlaşır; asıl kapsamı
    /// bilinmeyen (033→034 göçü, 047 öncesi indirme) her tıklamada. Başka kapsamın düşümü o yayının
    /// ürünleri için yapılmıştır: burada benimsenip revizyonla geri alınsaydı ya da doğrudan geri
    /// alınsaydı o ürünlerin bakiye ödemesi iade edilip bu satışta yeniden harcanırdı (yayıncı o tutarı
    /// kaybeder), kendi kapsamında kalan iş de kişi toplamına hiç revize edilmezdi. Kendi kapsamının
    /// tıklaması onu kalan işin revizyonuyla aynı tıklamada uzlaştırır (miras istek müşteriye
    /// gitmişse önce sorulur — <see cref="PersonScopePlan.PendingLegacies"/>).</para></summary>
    private IReadOnlyList<PaymentJob> ReconcilableLegacies(PaymentPerson person, string scopeKey)
    {
        var result = new List<PaymentJob>();
        foreach (var l in _jobs.GetOpenLegacies(person.MemberIds))
        {
            if (!PaymentOwnerRule.IsOwnerCapable(l.CustomerId))
            {
                _log?.LogWarning(
                    "GUID olmayan satırın miras ödeme işi kapatıldı — sunucu bu Id'ye düşüm yazamaz (job={JobId})", l.Id);
                _jobs.Close(l.Id);
                continue;
            }
            if (BelongsToScope(l, scopeKey)) result.Add(l);
        }
        return result;
    }

    /// <summary>Miras iş bu kapsamın tıklamasında uzlaşır mı: asıl kapsamı bilinmiyorsa her kapsamda,
    /// biliniyorsa yalnız kendi kapsamında.</summary>
    private static bool BelongsToScope(PaymentJob legacy, string scopeKey)
        => legacy.PriorScopeKey is null || string.Equals(legacy.PriorScopeKey, scopeKey, StringComparison.Ordinal);

    /// <summary>Satışın bakiye sonucunu kesinleştirir. Dönüşte ya sonuç
    /// kesindir (Uncertain=false; AppliedBalance mesaja yazılabilir) ya da
    /// akış durmalıdır (Uncertain=true; çağıran BalanceUncertain döner) ya da
    /// operatöre sorulmalıdır (<see cref="BalanceOutcome.Prompt"/>).</summary>
    /// <param name="serverMembers">Kira tutuluyken <c>begin</c>'in sunucu üyeleri (<see cref="PaymentLeaseFlow.ServerMembers"/>);
    /// kirasız yolda boş.</param>
    private async Task<BalanceOutcome> ResolveBalanceAsync(
        PaymentPerson person, decimal totalAmount, string scopeKey,
        IReadOnlySet<string>? confirmedTokens, IReadOnlyList<string> serverMembers, CancellationToken ct)
    {
        try
        {
            // Anahtar hiç yoksa bakiye özelliği zaten kapalı — eski davranış (iş açılmaz).
            if (string.IsNullOrEmpty(_currentLicense.CurrentLicenseKey))
                return new(Uncertain: false, 0m, null);

            // 0) Kişi × kapsam — ağ ve para adımlarından ÖNCE, tek yerel işlemde: iş bulunur ya da
            //    sahip satırda açılır; fazla işler indirilir. Teslim edilmiş istek (sahip kararı 2) ya
            //    da para taşıyan/teslim edilmiş fazla iş (sahip kararı 4) varsa ve operatör onaylamadıysa
            //    hiçbir şey yazılmadan soru döner.
            var pj = _jobs.FindOrCreateForPerson(new PersonJobRequest(
                person.LeasedId, person.MemberKey, person.MemberIds, person.OwnerId!,
                scopeKey, totalAmount, confirmedTokens));
            switch (pj.Status)
            {
                case PersonJobStatus.MembershipChanged:
                    return new(true, 0m, null) { MembershipChanged = true };
                case PersonJobStatus.ConfirmationRequired:
                    return new(true, 0m, pj.Job) { Prompt = PromptOf(pj.Plan!, scopeKey, totalAmount) };
            }
            foreach (var d in pj.Demoted)
                _log?.LogWarning(
                    "Kişi birleştirmesi: survivor={SurvivorJobId} demoted={DemotedJobId} owner={CustomerId} keptOpen={KeptOpen}",
                    pj.Job!.Id, d.JobId, d.CustomerId, d.KeptOpen);

            var outcome = await SettleScopeAsync(person, pj.Job!, totalAmount, confirmedTokens, serverMembers, ct);
            return outcome with { Demoted = pj.Demoted };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogError(ex,
                "Bakiye akışında beklenmeyen hata — mesaj engellendi (customer={CustomerId})",
                person.LeasedId);
            return new(true, 0m, null);
        }
    }

    /// <summary>
    /// Mod On'da sunucunun <c>already-requested</c> sorusuna (§6.4-e; W3-09) yerel planı katar — sahip kararları 2 ve 4'ün
    /// soruları yerel işten ve sunucu kayıtlarından BİRLİKTE kurulur (§6.7). Kira bakiyeden önce istendiği için yerel kapı
    /// (<see cref="IPaymentJobStore.FindOrCreateForPerson"/>) henüz sormadı; sorsaydı operatör aynı isteğe iki diyalog
    /// görürdü. Okuma salt okumadır (<see cref="IPaymentJobStore.GetPersonScopeJobs"/>, kapının baktığı küme) ve planı
    /// kapınınkiyle aynıdır (<see cref="PersonScopePlan.For(IReadOnlyList{PaymentJob}, IReadOnlyList{PaymentJob}, string?)"/>).
    ///
    /// <para>Yerel plan soru gerektiriyorsa tür ondan gelir (birleştirme önceliklidir; uzak soru hep tekrar göndermedir) ve
    /// planın belirteci <see cref="PaymentPrompt.AlsoConfirms"/>'a girer: "Evet" uzak onayı da yerel kapıyı da geçirir.
    /// Gösterilen istekler sunucunun öncülleridir — bu bilgisayarın kendi istekleri de sunucuda kayıtlıdır (kira ya da
    /// geri doldurma). Soru ile onay arasında yerel durum değişirse yerel belirteç tutmaz ve kapı yeniden sorar (bugünkü
    /// gibi).</para>
    ///
    /// <para>Bakiye işi yalnız GUID'li sahibi olan kişide var (<see cref="ResolveBalanceAsync"/>'in kapısı); öbüründe uzak
    /// soru olduğu gibi döner. Okuma düşerse de uzak soru döner: yerel kapı onay turunda kendisi sorar.</para>
    /// </summary>
    private PaymentPrompt WithLocalPlan(PaymentPrompt remote, PaymentPerson person, string scopeKey)
    {
        if (person.OwnerId is null) return remote;
        try
        {
            var (jobs, legacies) = _jobs.GetPersonScopeJobs(person.MemberIds, scopeKey);
            if (jobs.Count + legacies.Count == 0) return remote;
            var plan = PersonScopePlan.For(jobs, legacies, person.OwnerId);
            if (!plan.NeedsConfirmation) return remote;
            var merge = plan.Kind == PersonScopePromptKind.Merge;
            return remote with
            {
                Kind = plan.Kind,
                UnsentDeductions = plan.UnsentDeductions,
                RefundsToOtherRow = plan.RefundsToOtherRow,
                RefundsDeductions = plan.RefundsDeductions,
                NewTotal = merge ? null : remote.NewTotal,
                PriorHadBalance = !merge && remote.PriorHadBalance,
                AlsoConfirms = [plan.Token],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "Ödeme sorusuna yerel plan katılamadı; uzak soru tek başına soruluyor (customer={CustomerId})",
                person.LeasedId);
            return remote;
        }
    }

    /// <param name="totalAmount">Bu tıklamada istenecek toplam (kargo dahil, bakiye öncesi) — son
    /// istenenden farklıysa soruya yeni tutar olarak girer (K5).</param>
    private static PaymentPrompt PromptOf(PersonScopePlan plan, string scopeKey, decimal totalAmount)
    {
        var prompts = plan.PromptJobs;
        var last = prompts.Count > 0 ? prompts[^1] : null;
        // Son isteğin bakiye öncesi toplamı: mesajdaki net + (hâlâ duran) düşüm. Düşüm sonradan geri
        // alındıysa net tek başına kalır — fark yine "tutar değişti" olarak görünür.
        var priorBalance = last?.AppliedAmount is > 0m ? last.AppliedAmount.Value : 0m;
        var changed = plan.Kind == PersonScopePromptKind.Resend && last is not null
                      && last.PromptNet + priorBalance != totalAmount;
        return new(plan.Kind, scopeKey,
            prompts.Select(j => new PriorPaymentRequest(
                j.PromptNet,
                j.PromptAt is long at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
                j.HasUnconfirmedSend)).ToList(),
            plan.Token,
            plan.UnsentDeductions,
            plan.RefundsToOtherRow,
            plan.RefundsDeductions,
            NewTotal: changed ? totalAmount : null,
            PriorHadBalance: changed && priorBalance > 0m);
    }

    /// <summary>Kişi × kapsamın işini (<paramref name="job"/>) kesinleştirir. Sunucuya giden BÜTÜN
    /// çağrılar (replay, kapsam, önizleme, apply) işin SAHİP satırının Id'siyle yapılır — tıklanan
    /// satırınkiyle değil: sunucu bir anahtarı farklı asıl kayıtla oynatmayı içerik çakışması sayar.</summary>
    private async Task<BalanceOutcome> SettleScopeAsync(
        PaymentPerson person, PaymentJob job, decimal totalAmount,
        IReadOnlySet<string>? confirmedTokens, IReadOnlyList<string> serverMembers, CancellationToken ct)
    {
        var licenseId = await ResolveLicenseIdAsync(ct);
        if (licenseId is null)
            return OfflineOutcome(person, job, totalAmount);

        // 1) Miras işleri: önce HEPSİNİ kesinleştir, sonra devret ya da geri al.
        //
        // R4-04: müşteri başına birden fazla açık miras anahtarı olabilir
        // (R2-04 bunun oluşabildiğini gösterdi). Her birinin sonucu ayrı
        // öğrenilmeli; biri bile belirsiz kalırsa akış durur — yoksa
        // sunucudaki fazla düşümü hiç göremeden yeni satış açmış oluruz.
        //
        // Kişinin BÜTÜN üyeleri taranır ve her miras iş KENDİ sahip satırının Id'siyle yeniden
        // oynatılır (eskiden tıklanan satırın Id'siyle — başka satırın anahtarı içerik çakışmasıyla
        // kalıcı belirsiz kalırdı). Asıl kapsamı başka olan miras iş bu tıklamada ele alınmaz
        // (ReconcilableLegacies — başka yayının düşümü bu satışta geri alınıp yeniden harcanmaz).
        var legacies = ReconcilableLegacies(person, job.ScopeKey);

        // R6-02: işin sunucu sonucu BU tıklama içinde sunucudan GÜNCEL
        // geçerlilik taşıyan bir cevapla öğrenildiyse true — 6. adımdaki
        // tekrar-paylaşım doğrulaması o zaman gereksizdir (aynı cevabı
        // ikinci kez sormak olurdu).
        //
        // R9-F01: replay cevabı bu tanıma GİRMEZ. Sunucu idempotency
        // kaydından TARİHSEL sonucu döndürür; düşüm panel iadesiyle geri
        // alınmış olsa da replay "applied" der. Yalnız kapsam benimsemesi
        // (4. adım) sayılır: /scope sorgusu geri alınmış satırları dışlar,
        // cevabı güncel gerçektir.
        var remoteConfirmed = false;

        if (legacies.Count > 0)
        {
            var resolved = new List<PaymentJob>(legacies.Count);
            foreach (var l in legacies)
            {
                var attempt = await ReplayOnceMoreIfStaleAsync(licenseId.Value, Guid.ParseExact(l.CustomerId, "N"), l, ct);
                if (attempt.LegacyScopeDeduction is { } deduction)
                {
                    // §6.6 (W3-11): miras iş kapsamdaki düşümü BENİMSEMEZ — düşüm büyük olasılıkla başka bir yerel
                    // işindir, iki iş aynı düşümü sahiplenirdi. #506'nın birleştirme sorusu sorulur; soruya kadar
                    // hiçbir şey yazılmaz, mesaj gitmez.
                    var token = LegacyScopeDeductionToken(attempt.Job, deduction);
                    if (confirmedTokens?.Contains(token) != true)
                        return new(true, 0m, job) { Prompt = LegacyScopeDeductionPrompt(job.ScopeKey, token, deduction) };
                    if (!CloseRefusedLegacy(attempt.Job))
                        return new(true, 0m, _jobs.Get(l.Id));
                    continue;
                }
                if (attempt.Job.State == PaymentJobState.ApplyUncertain)
                    return new(true, 0m, attempt.Job);
                resolved.Add(attempt.Job);
            }

            job = _jobs.Get(job.Id)!;

            // Devralma en fazla BİR miras işine uygulanabilir: taze hedef
            // tek bir anahtar taşır. Yalnız AYNI sahip satırın miras işi
            // devralınabilir (sunucu anahtarı o asıl kayıtla tanır); onlardan
            // en yenisi seçilir — 033 akışında bu satışa en yakın olan odur.
            // Asıl kapsamı bilinen miras yalnız kendi kapsamının işine (liste zaten
            // süzülü; depo da ikinci kat olarak denetler). Kalanlar (başka
            // satırınkiler dahil) artık düşüm sayılır.
            var adoptTarget = job;
            var adopted = adoptTarget.State == PaymentJobState.Created && adoptTarget.ApplyKey is null
                ? resolved.FirstOrDefault(r =>
                    string.Equals(r.CustomerId, adoptTarget.CustomerId, StringComparison.Ordinal)
                    && BelongsToScope(r, adoptTarget.ScopeKey))
                : null;
            if (adopted is not null)
            {
                // R9-F01: remoteConfirmed BİLEREK set edilmiyor — replay
                // tarihsel sonuçtur, 6. adım güncelliği ayrıca doğrular.
                _jobs.AdoptLegacyResult(job.Id, adopted.Id);
            }

            foreach (var l in resolved)
            {
                if (adopted is not null && l.Id == adopted.Id) continue;

                // Devralınmayan miras düşümü artık: geri al, işi kapat (müşteri
                // istemez). Geri alma kesinleşmezse iş AÇIK kalır ve akış durur —
                // kapatmak, bilinmeyen bir düşümü sessizce gömmek olurdu.
                if (l.AppliedAmount is > 0m
                    && !await TryReverseAsync(licenseId.Value, l.ApplyKey!.Value, ct))
                {
                    // Koşul tutmazsa satırı başka bir akış devraldı; bizim
                    // geri almamız yine de bilinmiyor — bu tıklama durur.
                    _jobs.MarkUncertain(l.Id, l.ApplyKey, l.Revision);
                    return new(true, 0m, l);
                }
                _jobs.Close(l.Id);
            }

            job = _jobs.Get(job.Id)!;
        }

        if (!Guid.TryParseExact(job.CustomerId, "N", out var wpfCustomerId))
        {
            _log?.LogError("Ödeme işinin sahibi GUID değil — mesaj engellendi (job={JobId})", job.Id);
            return new(true, 0m, job);
        }

        // 2) Yarım kalmış geri alma (R4-02): HER ŞEYDEN önce uzlaştır.
        //
        // İstenen toplam eski değere dönmüş olabilir; o zaman revizyon dalı
        // hiç çalışmaz ve niyet burada yakalanmazsa görünmez olur. Geri
        // alınmış bir işlemin anahtarı replay edilirse sunucu onun tarihsel
        // sonucunu döndürür ve geri alınan tutar geçerli düşüm sanılır.
        if (job.State == PaymentJobState.ReversePending)
        {
            job = await FinishReversalAsync(licenseId.Value, job, ct);
            if (job.State == PaymentJobState.ReversePending)
                return new(true, 0m, job);
        }

        // 3) Belirsiz iş: bir kez replay (K3) — hâlâ belirsizse blokla.
        //    R9-F01: replay idempotency kaydının TARİHSEL sonucunu getirir;
        //    düşüm bu arada dışarıdan geri alındıysa yine "applied" der.
        //    remoteConfirmed set edilmez — 6. adım güncelliği doğrulasın.
        //    W3-11 (§6.6): kilit zaman aşımı (stale; hiçbir şey yazılmadı) hemen bir kez daha denenir. Anahtar
        //    sunucuda yoksa ve kapsamda başka düşüm duruyorsa (scope-has-deduction) o düşüm benimsenir: kilit altındaki
        //    güncel cevaptır (yüklem geri alınmışları dışlar), 6. adım yeniden sormaz; tutar farkını 5. adım revize eder.
        if (job.State == PaymentJobState.ApplyUncertain)
        {
            var attempt = await ReplayOnceMoreIfStaleAsync(licenseId.Value, wpfCustomerId, job, ct);
            job = attempt.Job;
            if (attempt.Adopted)
                remoteConfirmed = true;
            else if (job.State == PaymentJobState.ApplyUncertain)
                return new(true, 0m, job);
        }

        // 4) R4-03 uzlaştırma (KİŞİ düzeyinde): iş hiç denenmemiş görünüyor ama
        //    sunucuda bu kapsamda geri alınmamış bir düşüm olabilir — kişinin
        //    HERHANGİ bir GUID'li üyesinde.
        //
        //    Tipik sebep yedek geri yükleme: satışın yerel kimliği (apply
        //    anahtarı) diskte yaşıyordu ve onunla birlikte yok oldu; uzak
        //    defter ise düşümü hatırlamaya devam ediyor. Sormadan devam
        //    etmek aynı satışı ikinci kez düşürür — R4-03'ün ta kendisi.
        //    Kişi düzeyi: geçiş dönemindeki eski sahipler, başka bilgisayarın
        //    (eski sürüm) düşümü ve telefon senkron gecikmesi aynı kişinin
        //    başka satırında iz bırakabilir; yalnız işin sahibine sormak onu
        //    görmez ve ikinci kez düşerdi.
        //
        //    Sıra önemli: geri alma tamamlandıktan (2) ve belirsiz iş
        //    kesinleştikten (3) SONRA sorulmalı, yoksa az önce geri
        //    aldığımız satırı benimseyebiliriz. Revizyondan (5) ÖNCE
        //    olmalı ki tutar karşılaştırması benimsenen gerçek tutara karşı
        //    yapılsın.
        //    R6-01: sorgulardan biri BAŞARISIZSA mesaj bloklanır. Anahtarsız
        //    created iş tam da çifte düşüm penceresidir; "sorulamadı"yı
        //    "yok" saymak, yedeği geri yüklenmiş kurulumda aynı satışı
        //    ikinci kez düşürür. İş dokunulmamış (created/anahtarsız)
        //    kaldığı için operatörün sıradaki tıklaması temiz dener.
        if (job.State == PaymentJobState.Created && job.ApplyKey is null)
        {
            var reconciled = await ReconcilePersonScopeAsync(
                licenseId.Value, person, job, confirmedTokens, serverMembers, ct);
            if (reconciled.Prompt is not null) return new(true, 0m, job) { Prompt = reconciled.Prompt };
            if (reconciled.Job is null) return new(true, 0m, job);
            job = reconciled.Job;
            wpfCustomerId = Guid.ParseExact(job.CustomerId, "N");   // iz başka üyedeyse sahip oraya geçti
            if (job.ApplyKey is not null)
                remoteConfirmed = true; // düşüm az önce sunucudan benimsendi
        }

        // 5–8. W3-11 (spec 2026-10-13 §6.6): apply 409 scope-has-deduction aldıysa kapsamdaki düşüm işe benimsendi ve akış
        //    revizyon adımına BİR KEZ döner — toplam aynıysa kesin sonuç, farklıysa benimsenen düşüm geri alınır ve yeni
        //    anahtarla taze uygulanır. Cevap kilit altındaki güncel gerçektir (yüklem geri alınmışları dışlar): 6. adım
        //    yeniden doğrulamaz. Aynı tıklamada ikinci benimseme (araya yine başka düşüm girdi) dönmez: DefiniteForCall
        //    toplam tutarsa mesaja çıkar, tutmazsa durur — sıradaki tıklama revize eder (ping-pong yok).
        var settled = await ReviseAndSettleAsync(licenseId.Value, wpfCustomerId, job, totalAmount, remoteConfirmed, ct);
        if (!settled.ScopeDeductionAdopted) return settled;
        settled = await ReviseAndSettleAsync(licenseId.Value, wpfCustomerId, settled.Job!, totalAmount, remoteConfirmed: true, ct);
        return settled.ScopeDeductionAdopted ? DefiniteForCall(settled.Job!, totalAmount) : settled;
    }

    /// <summary><see cref="SettleScopeAsync"/>'in 5–8. adımları (revizyon, tekrar paylaşım, sıfır toplam, taze uygulama) —
    /// 1–4 kesinleşmiş işle. Apply'ın <c>scope-has-deduction</c>'ında (W3-11) çağıran bu adımlara benimsenen işle bir kez
    /// döner.</summary>
    /// <param name="remoteConfirmed">R6-02: işin sonucu bu tıklamada sunucudan güncel bir cevapla öğrenildi (kapsam
    /// benimsemesi) — 6. adım yeniden sormaz.</param>
    private async Task<BalanceOutcome> ReviseAndSettleAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, decimal totalAmount, bool remoteConfirmed,
        CancellationToken ct)
    {
        // 5) Revizyon (K2): kapsam aynı, tutar değişti — eski düşümü geri
        //    al, yeni toplam + YENİ anahtarla taze uygula. created işte de
        //    çalışır (geri alınacak şey yoktur, sadece tutar güncellenir).
        if (job.ProductTotal != totalAmount)
        {
            if (job.AppliedAmount is > 0m)
            {
                // R4-02: niyet ÖNCE diske — istek tele çıkmadan.
                // false = eşzamanlı akış kazandı; satırı yeniden okuyup
                // onun bıraktığı duruma göre devam ederiz.
                _jobs.BeginReversal(job.Id, job.Revision, job.ApplyKey!.Value, totalAmount);
                job = _jobs.Get(job.Id)!;

                if (job.State == PaymentJobState.ReversePending)
                {
                    job = await FinishReversalAsync(licenseId, job, ct);
                    if (job.State == PaymentJobState.ReversePending)
                        return new(true, 0m, job);
                }
            }

            // Buraya gelindiğinde geri alınacak düşüm yok: ya hiç olmadı ya
            // da kesinleşerek geri alındı (o durumda CompleteReversal işi
            // zaten hedef toplamla 'created'a döndürdü, koşul tutmaz ve
            // akış aşağıdaki taze uygulama adımına düşer).
            if (job.ProductTotal != totalAmount)
            {
                _jobs.BeginRevision(job.Id, totalAmount, Guid.NewGuid(), job.Revision);
                job = _jobs.Get(job.Id)!;
                return await SettleAsync(licenseId, wpfCustomerId, job, totalAmount, ct);
            }
        }

        // 6) Tekrar paylaşım: sonuç kesin, tutar aynı — finansal YAZMA yok.
        //
        //    R6-02: para düşülmüş görünen iş, paylaşımdan önce sunucudan
        //    DOĞRULANIR — panel iadesi düşümü geri almış olabilir; eski fişi
        //    "bakiye düşüldü" diye tekrar paylaşmak müşteriye olmayan bir
        //    indirimi gösterirdi. Doğrulama fırsatçıdır: uç cevap veremezse
        //    diskteki kesin sonuçla devam edilir (Applied iş çevrimdışı bile
        //    paylaşılabiliyor — S3; çevrimiçi tek uç hatası daha katı
        //    olamaz). no_balance hiç sorulmaz: sunucudan bir şey düşülmedi.
        if (job.State is PaymentJobState.Applied or PaymentJobState.NoBalance)
        {
            if (!remoteConfirmed
                && job is { State: PaymentJobState.Applied, ApplyKey: { } appliedKey,
                            AppliedAmount: > 0m })
            {
                var blocked = await VerifyAppliedStillStandsAsync(
                    licenseId, job, appliedKey, ct);
                if (blocked is not null) return blocked;

                // R9-F02: doğrulama await'i sırasında eşzamanlı bir akış
                // satırı taşımış olabilir (geri alma niyeti, revizyon).
                // Sunucu cevabı await ÖNCESİ duruma aitti; kesin mesaj
                // await SONRASI satırdan verilmeli. Deneme kimliği
                // tutmuyorsa bu tıklama durur — sıradaki tıklama satırın
                // güncel hâlinden sürdürür (K3, ClassifySettledJob deseni).
                var current = _jobs.Get(job.Id)!;
                if (current.State != PaymentJobState.Applied
                    || current.ApplyKey != appliedKey
                    || current.Revision != job.Revision)
                {
                    _log?.LogWarning(
                        "Doğrulama sırasında iş taşındı — mesaj engellendi "
                        + "(job={JobId}, durum={State})", current.Id, current.State);
                    return new(true, 0m, current);
                }
                job = current;
            }
            return DefiniteForCall(job, totalAmount);
        }

        // 7) S5: satış sıfıra indi. Geri alınacak eski düşüm varsa (5)
        //    çoktan geri alındı; buradan sonrası için düşülecek bir şey
        //    yok. Sunucuya apply GÖNDERMİYORUZ — sıfır tutarı zaten
        //    "nothing-to-apply" ile reddeder; sonucu yerelde kesinleştirip
        //    boş bir gidiş-dönüşten kaçınıyoruz.
        if (totalAmount <= 0m)
        {
            if (!_jobs.MarkNoBalance(job.Id, job.ApplyKey, job.Revision))
            {
                job = _jobs.Get(job.Id)!;
                return job.State is PaymentJobState.Applied or PaymentJobState.NoBalance
                    ? DefiniteForCall(job, totalAmount)
                    : new(true, 0m, job);
            }
            return new(false, 0m, _jobs.Get(job.Id));
        }

        // R6-03: 2. adım geri alma niyetini boşaltmıştı; buraya
        // reverse_pending ile düşmenin tek yolu araya giren eşzamanlı bir
        // akışın niyeti O ANDAN SONRA yazmasıdır. Aşağıdaki taze uygulama
        // diskteki anahtarı yeniden kullanır (BeginApply yarışı kaybeder ve
        // akış Settle'a düşer) — yani geri alınmak üzere olan düşümü ikinci
        // kez yazardık. Mesajı engelle; operatörün sıradaki tıklaması
        // 2. adımdan kaldığı yerden sürdürür.
        if (job.State == PaymentJobState.ReversePending)
        {
            _log?.LogWarning(
                "Geri alma beklerken taze düşüm istendi — mesaj engellendi (job={JobId})",
                job.Id);
            return new(true, 0m, job);
        }

        // 8) Taze iş: önizleme (bakiye yoksa anahtar hiç yazılmaz) →
        //    anahtar diske → apply.
        decimal previewBalance;
        try
        {
            var preview = await _api.GetBalancePreviewAsync(licenseId, wpfCustomerId, ct);
            previewBalance = preview.Balance;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "Bakiye önizlemesi alınamadı — mesaj engellendi (job={JobId})", job.Id);
            return new(true, 0m, job); // anahtar yazılmadı, iş created kaldı
        }
        if (previewBalance <= 0)
        {
            if (!_jobs.MarkNoBalance(job.Id, job.ApplyKey, job.Revision))
            {
                // Araya başka bir akış girdi ve sonucu o yazdı: bizim
                // "bakiye yok" gözlemimiz artık bu denemeye ait değil.
                job = _jobs.Get(job.Id)!;
                return job.State is PaymentJobState.Applied or PaymentJobState.NoBalance
                    ? DefiniteForCall(job, totalAmount)
                    : new(true, 0m, job);
            }
            return new(false, 0m, _jobs.Get(job.Id));
        }

        if (!_jobs.BeginApply(job.Id, Guid.NewGuid()))
        {
            // Yarışı kaybettik (R2-04/A9): kazananın anahtarı diskte.
            job = _jobs.Get(job.Id)!;
            if (job.State is PaymentJobState.Applied or PaymentJobState.NoBalance)
                return DefiniteForCall(job, totalAmount);
        }
        job = _jobs.Get(job.Id)!;
        return await SettleAsync(licenseId, wpfCustomerId, job, totalAmount, ct);
    }

    /// <summary>Kişi düzeyindeki kapsam uzlaştırmasının sonucu: <see cref="Job"/> null = sorgu ya da
    /// geri alma kesinleşmedi (mesaj bloklanır); <see cref="Prompt"/> dolu = operatöre sorulmalı.</summary>
    private sealed record ScopeReconciliation(PaymentJob? Job, PaymentPrompt? Prompt = null);

    /// <summary>R4-03 (kişi düzeyinde): sunucuda bu kapsamda duran geri alınmamış düşümü TAZE işe
    /// benimsetir. Mevcut /scope ucu kişinin HER GUID'li üyesi için sorulur (sunucu değişikliği yok);
    /// sonuçlar işlem kimliğiyle tekilleştirilir.
    /// <list type="bullet">
    /// <item>İz yok → iş olduğu gibi döner, akış değişmez.</item>
    /// <item>Tek iz → iş o üyeye geçer (<see cref="IPaymentJobStore.ReassignOwner"/> — yalnız taze işte)
    ///   ve iz benimsenir: düşüm o asıl kayıtla tanınır, başka sahiple tekrar oynatmak içerik çakışması
    ///   olurdu.</item>
    /// <item>Birden çok iz (aynı kişiye iki satırdan düşülmüş) → birleştirme sorulur; onayda en eski
    ///   düşüm kalır, ötekiler geri alınır.</item>
    /// </list>
    ///
    /// <para>R6-01: sorgulardan biri <b>başarısız</b> olursa <c>Job = null</c> döner ve çağıran
    /// mesajı bloklar. "Yok" ile "sorulamadı" aynı şey değil: bu pencerede
    /// (anahtarsız created iş) sorulamamışken devam etmek, sunucudaki düşümü
    /// görmeden ikinci kez düşmek demek — denetimin çifte düşüm deneyi tam
    /// buydu.</para>
    ///
    /// <para>Benimseme koşullu: araya giren bir akış işe anahtar yazdıysa
    /// (false) satır yeniden okunur ve onun bıraktığı durumdan devam edilir.</para>
    ///
    /// <para><b>Sunucu üyeleri</b> (mod On, kira tutuluyken; spec 2026-10-13 §6.4 "Kapsam uzlaştırması", §6.6, §12 risk 12;
    /// W3-08): adaylar = yerel GUID'li üyeler ∪ <c>begin</c> yanıtının GUID'li üyeleri (ordinal tekil, yerel önce). Grup
    /// senkronu gecikmiş bilgisayar başka üyeye yazılmış düşümü böyle görür ve kendi çapasından ikinci düşüm yazmaz; §6.6
    /// kuralı çapa düzeyinde kalır, bu katman onu kişi düzeyinde tamamlar. İz yerelde bilinmeyen bir sunucu üyesindeyse (yerel
    /// kopyanın sunucudaki asıl kaydı; yönlendirmesi henüz inmedi) iş ona TAŞINMAZ: yerel kişi işi bulamaz olurdu — sonraki
    /// tıklama ikinci iş açar, taşıma çakışır, istek kilitlenirdi. Düşüm işlem kimliğiyle benimsenir; geri alma ve doğrulama
    /// işlem kimliğiyle çalışır.</para></summary>
    private async Task<ScopeReconciliation> ReconcilePersonScopeAsync(
        Guid licenseId, PaymentPerson person, PaymentJob job,
        IReadOnlySet<string>? confirmedTokens, IReadOnlyList<string> serverMembers, CancellationToken ct)
    {
        var localCandidates = person.Members.Select(m => m.Id).Where(PaymentOwnerRule.IsOwnerCapable).ToList();
        if (!localCandidates.Contains(job.CustomerId, StringComparer.Ordinal)) localCandidates.Insert(0, job.CustomerId);
        var local = localCandidates.ToHashSet(StringComparer.Ordinal);
        var candidates = localCandidates
            .Concat(serverMembers.Where(PaymentOwnerRule.IsOwnerCapable))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count > localCandidates.Count)
            _log?.LogInformation(
                "Kapsam uzlaştırması: kiranın sunucu üyelerinden {Extra} aday eklendi (job={JobId}, scope={ScopeKey})",
                candidates.Count - localCandidates.Count, job.Id, job.ScopeKey);

        var traces = new List<(string MemberId, CustomerBalanceScope Scope)>();
        foreach (var memberId in candidates)
        {
            CustomerBalanceScope? remote;
            try
            {
                remote = await _api.GetBalanceScopeAsync(licenseId, Guid.ParseExact(memberId, "N"), job.ScopeKey, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex,
                    "Kapsam uzlaştırması yapılamadı — mesaj engellendi (job={JobId}, scope={ScopeKey})",
                    job.Id, job.ScopeKey);
                return new(null);
            }
            if (remote is not null && traces.All(t => t.Scope.TransactionId != remote.TransactionId))
                traces.Add((memberId, remote));
        }

        if (traces.Count == 0) return new(job);

        if (traces.Count > 1)
        {
            var token = "uzak:" + string.Join(";", traces
                .Select(t => t.Scope.TransactionId.ToString("N")).OrderBy(x => x, StringComparer.Ordinal));
            // İzler tanım gereği farklı üyelerde: iade edilen düşüm kendi satırına döner. Sunucu teslim
            // bilgisini bilmez — izler "gitmiş istek" diye değil, bulunan bakiye düşümleri diye anlatılır
            // (biri gönderilememiş de olabilir; eski sürüm bilgisayarın LaunchFailed/SendPending'i).
            if (confirmedTokens?.Contains(token) != true)
                return new(job, new PaymentPrompt(PersonScopePromptKind.Merge, job.ScopeKey,
                    Array.Empty<PriorPaymentRequest>(), token, RefundsToOtherRow: true,
                    RemoteDeductions: traces.OrderBy(t => t.Scope.CreatedAt)
                        .Select(t => t.Scope.AppliedAmount).ToList()));

            // Onaylandı: en eski düşüm kalır (sunucu teslim bilgisini bilmez); fazlası iade edilir.
            var keep = traces.OrderBy(t => t.Scope.CreatedAt).ThenBy(t => candidates.IndexOf(t.MemberId)).First();
            foreach (var extra in traces.Where(t => t.Scope.TransactionId != keep.Scope.TransactionId))
            {
                if (!await TryReverseAsync(licenseId, extra.Scope.TransactionId, ct))
                {
                    _log?.LogWarning(
                        "Kişinin fazladan düşümü geri alınamadı — mesaj engellendi (job={JobId}, tx={TransactionId})",
                        job.Id, extra.Scope.TransactionId);
                    return new(null);
                }
                _log?.LogWarning(
                    "Kişinin fazladan düşümü geri alındı (job={JobId}, tx={TransactionId}, kalan={KeptTransactionId})",
                    job.Id, extra.Scope.TransactionId, keep.Scope.TransactionId);
            }
            traces = new() { keep };
        }

        var (owner, scope) = traces[0];
        if (!local.Contains(owner))
        {
            // Yalnız sunucunun bildiği üye (W3-08): iş sahibinde kalır, düşüm işlem kimliğiyle benimsenir.
            _log?.LogInformation(
                "Kapsam izi yerelde bilinmeyen bir sunucu üyesinde — iş sahibinde kalıyor (job={JobId}, member={CustomerId})",
                job.Id, owner);
        }
        else if (!string.Equals(owner, job.CustomerId, StringComparison.Ordinal))
        {
            // Teslim edilmiş taze iş de taşınır (soru kapıda sorulmuştu). W2: taşınan teslimin beklentisi
            // yeni sahibe yeniden anahtarlanmalı, eski sahipte Superseded.
            if (!_jobs.ReassignOwner(job.Id, owner))
            {
                _log?.LogWarning(
                    "Kapsam izi başka üyede ama iş ona geçirilemedi — mesaj engellendi (job={JobId})", job.Id);
                return new(null);
            }
            _log?.LogInformation(
                "Kapsam izi başka üyede — iş o üyeye geçti (job={JobId}, owner={CustomerId})", job.Id, owner);
        }

        _log?.LogInformation(
            "Sunucuda bu kapsamda düşüm bulundu, benimseniyor "
            + "(job={JobId}, scope={ScopeKey}, tx={TransactionId}, applied={Applied})",
            job.Id, job.ScopeKey, scope.TransactionId, scope.AppliedAmount);

        _jobs.AdoptRemoteResult(job.Id, scope.TransactionId, scope.AppliedAmount, scope.ProductTotal);
        return new(_jobs.Get(job.Id)!);
    }

    /// <summary>R6-02: tekrar paylaşımdan önce "kayıtlı düşüm sunucuda hâlâ
    /// geçerli mi?" Cevaba göre üç yol:
    ///
    /// <list type="bullet">
    /// <item><c>null</c> — düşüm geçerli YA DA uca ulaşılamadı: çağıran
    /// diskteki kesin sonuçla paylaşır (doğrulama fırsatçıdır, S3).</item>
    /// <item>Bloklu sonuç — sunucu düşümü geri almış (panel iadesi) ya da hiç
    /// tanımıyor (404): iş <see cref="IPaymentJobStore.AdoptExternalReversal"/>
    /// ile anahtarsız created'a döner ve BU tıklama durur. Aynı tıklamada
    /// sessizce yeniden düşmüyoruz — para hareketi operatör kararıdır (R8-02);
    /// sıradaki tıklama 4. adımdan itibaren taze akışla ilerler (sunucu bu
    /// arada yeni bir düşüm yazdıysa kapsam benimsemesi onu bulur).</item>
    /// </list>
    ///
    /// İşlem kimliği = işin apply anahtarı (sunucu idempotency anahtarını
    /// işlem kimliği yapar); kapsamsız yazılmış eski satırlar için de doğru
    /// çalışmasının sırrı bu — kapsam sorgusu onları göremezdi.</summary>
    private async Task<BalanceOutcome?> VerifyAppliedStillStandsAsync(
        Guid licenseId, PaymentJob job, Guid appliedKey, CancellationToken ct)
    {
        CustomerBalanceTransactionStatus? status;
        try
        {
            status = await _api.GetBalanceTransactionStatusAsync(licenseId, appliedKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "Düşüm doğrulaması yapılamadı — diskteki sonuçla devam (job={JobId})", job.Id);
            return null;
        }

        if (status is { Reversed: false }) return null;

        _log?.LogWarning(
            "Kayıtlı düşüm sunucuda {Durum} — mesaj engellendi, iş sıfırlandı "
            + "(job={JobId}, tx={TransactionId})",
            status is null ? "tanınmıyor" : "geri alınmış", job.Id, appliedKey);

        // false = araya giren akış işi taşıdı (örn. geri alma niyeti yazıldı);
        // niyet ezilmez, bu tıklama yine durur ve sıradaki tıklama satırın
        // güncel hâlinden devam eder.
        _jobs.AdoptExternalReversal(job.Id, appliedKey, job.Revision);
        return new(true, 0m, _jobs.Get(job.Id));
    }

    /// <summary>Diskteki anahtarla apply — bir deneme + bir anında tekrar (K3).
    /// <para>W3-11 (spec 2026-10-13 §6.6): kilit zaman aşımının (409 <c>stale</c>) "bir kez yeniden"i K3'ün anında
    /// tekrarıdır — en çok iki apply; ikisi de stale ise iş anahtarıyla <c>apply_uncertain</c> kalır (sıradaki tıklama
    /// oynatır), mesaj gitmez. Apply 409 <c>scope-has-deduction</c> aldıysa düşüm işe benimsenmiştir: sonuç
    /// <see cref="BalanceOutcome.ScopeDeductionAdopted"/> ile döner, çağıran revizyon adımına döner.</para></summary>
    private async Task<BalanceOutcome> SettleAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, decimal requestedTotal,
        CancellationToken ct)
    {
        // R6-03: girişteki deneme kimliği. Cevap, gönderildiği denemeye aittir;
        // yeniden okunan satır başka bir revizyona geçtiyse oradaki terminal
        // sonuç BU çağrının satışına mal edilemez.
        var entryKey = job.ApplyKey;
        var entryRevision = job.Revision;

        var attempt = await ReplayAsync(licenseId, wpfCustomerId, job, ct);
        if (!attempt.Adopted && attempt.Job.State == PaymentJobState.ApplyUncertain)
        {
            attempt = await ReplayAsync(licenseId, wpfCustomerId, attempt.Job, ct);
            if (attempt.Stale) LogStalePersists(attempt.Job);
        }
        if (attempt.Adopted)
            return new(true, 0m, attempt.Job) { ScopeDeductionAdopted = true };
        return ClassifySettledJob(attempt.Job, entryKey, entryRevision, requestedTotal);
    }

    /// <summary>Tek apply denemesi (<see cref="ReplayAsync"/>); kilit zaman aşımında (409 <c>stale</c>, hiçbir şey
    /// yazılmadı) hemen bir kez daha — spec 2026-10-13 §6.6, en çok iki apply. Belirsiz işin (3. adım) ve miras işlerin
    /// (1. adım) tek replay'i için: ağ hatası bugünkü gibi yeniden denenmez, akış durur.</summary>
    private async Task<ApplyAttempt> ReplayOnceMoreIfStaleAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
    {
        var attempt = await ReplayAsync(licenseId, wpfCustomerId, job, ct);
        if (!attempt.Stale || attempt.Job.State != PaymentJobState.ApplyUncertain) return attempt;
        attempt = await ReplayAsync(licenseId, wpfCustomerId, attempt.Job, ct);
        if (attempt.Stale) LogStalePersists(attempt.Job);
        return attempt;
    }

    /// <summary>§6.6: ikinci deneme de kilit zaman aşımı aldı — ayrı günlük satırı (sahadan gelen günlükte "neden
    /// gitmedi"nin tek ayırt edici bilgisi).</summary>
    private void LogStalePersists(PaymentJob job) => _log?.LogWarning(
        "Bakiye apply'ı ikinci kez kilit zaman aşımı aldı (stale) — mesaj engellendi, iş anahtarıyla belirsiz kalıyor; "
        + "sıradaki tıklama oynatır (job={JobId}, key={Key})", job.Id, job.ApplyKey);

    /// <summary>§6.6 (W3-11): miras işin birleştirme sorusunun onay belirteci — iki izin kimliğinden: miras işin denemesi
    /// (Id, anahtar, revizyon) ve sunucunun kapsamdaki işlemi. Biri değişirse (başka deneme, başka düşüm) tutmaz, yeniden
    /// sorulur.</summary>
    private static string LegacyScopeDeductionToken(PaymentJob legacy, ScopeHasDeductionException deduction)
        => $"miras-uzak:{legacy.Id}/{legacy.ApplyKey?.ToString("N") ?? "-"}/"
           + $"{legacy.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{deduction.TransactionId:N}";

    /// <summary>§6.6 (W3-11): miras işin apply'ı kapsamda başka bir geri alınmamış düşüm buldu — #506'nın birleştirme
    /// sorusu, kapsam uzlaştırmasının çok izli sorusuyla aynı biçimde (<see cref="ReconcilePersonScopeAsync"/>): sunucu
    /// teslimi bilmez, iz "gitmiş istek" değil bulunan bakiye düşümü diye anlatılır. Onayda kapsamdaki düşüm kalır, miras
    /// iş kapanır (<see cref="CloseRefusedLegacy"/>); kişinin toplamı tek istekte yeniden istenir.</summary>
    private static PaymentPrompt LegacyScopeDeductionPrompt(
        string scopeKey, string token, ScopeHasDeductionException deduction)
        => new(PersonScopePromptKind.Merge, scopeKey, Array.Empty<PriorPaymentRequest>(), token,
            RemoteDeductions: [deduction.AppliedAmount]);

    /// <summary>§6.6 (W3-11): operatör miras işin birleştirmesini onayladı. Sunucu kuralı yalnız anahtarı YENİ apply'da
    /// çalışır ve kilit altında kapsamdaki düşümü gösterdi: miras anahtarı sunucuda hiç yazılmadı — işin düşümü yok, geri
    /// alınacak bir şey yok. İş kesin "düşüm yok" olarak yazılır ve kapatılır; benimsenmez (devralma adayı da olmaz —
    /// yazılmamış anahtarı taze işe taşırdı). Kapsamdaki düşüm kalır: kalan iş onu taşıyorsa sürer, tazeyse kapsam
    /// uzlaştırması (4. adım) benimser. Satır arada başka akışa geçtiyse false (bu tıklama durur).</summary>
    private bool CloseRefusedLegacy(PaymentJob legacy)
    {
        if (!_jobs.MarkNoBalance(legacy.Id, legacy.ApplyKey, legacy.Revision))
        {
            _log?.LogWarning(
                "Miras işin birleştirmesi onaylandı ama iş arada taşındı — mesaj engellendi (job={JobId})", legacy.Id);
            return false;
        }
        _jobs.Close(legacy.Id);
        _log?.LogInformation(
            "Miras işin anahtarı sunucuda yazılmamış (kapsamda başka düşüm var) — birleştirme onaylandı, iş kapatıldı "
            + "(job={JobId})", legacy.Id);
        return true;
    }

    /// <summary>R6-03: para yolunda sonucu üreten SON nokta. Bir sonuç yalnız
    /// "belirsiz kelimesini taşımadığı" için kesin sayılmaz — kesinlik pozitif
    /// listeyle verilir: bu denemeye ait, alanları tutarlı <c>Applied</c> veya
    /// <c>NoBalance</c>. Gerisi (created, apply_uncertain, reverse_pending,
    /// bilinmeyen gelecek durumlar, AppliedAmount'u boş 'applied') tek
    /// davranışa düşer: mesaj engellenir, operatörün sıradaki tıklaması
    /// kaldığı yerden sürdürür (K3).
    ///
    /// R8 deneyindeki delik buraydı: repository geri alma beklerken geç apply
    /// cevabını reddediyor, ama yeniden okunan reverse_pending iş eski
    /// AppliedAmount'la "kesin" diye mesaja çıkıyordu — mesaj 250 gösterirken
    /// uzak net düşüm 0'dı.</summary>
    private BalanceOutcome ClassifySettledJob(
        PaymentJob job, Guid? entryKey, int entryRevision, decimal requestedTotal)
    {
        if (job.Revision != entryRevision || job.ApplyKey != entryKey)
        {
            _log?.LogWarning(
                "Settle sonucu başka bir denemeye ait — mesaj engellendi "
                + "(job={JobId}, girişRev={EntryRevision}, güncelRev={Revision})",
                job.Id, entryRevision, job.Revision);
            return new(true, 0m, job);
        }

        switch (job.State)
        {
            case PaymentJobState.Applied when job.AppliedAmount is not null:
                return DefiniteForCall(job, requestedTotal);
            case PaymentJobState.NoBalance:
                return DefiniteForCall(job, requestedTotal);
            default:
                _log?.LogWarning(
                    "Settle kesin sonuca ulaşmadı — mesaj engellendi (job={JobId}, state={State})",
                    job.Id, job.State);
                return new(true, 0m, job);
        }
    }

    /// <summary>R10-F01: kesin bir sonuç mesaja ancak BU çağrının istediği
    /// toplama aitse çıkabilir. Yarış kaybedilip kazananın satırı devralındığında
    /// (BeginApply/MarkNoBalance false, ya da BeginApply yarışı kaybedip kazananın
    /// anahtarını Settle etmek) satır başka bir toplamın sonucunu taşıyor
    /// olabilir; onu bu satışın mesajına yazmak müşteriye yanlış net tutar
    /// göstermek olur (250 istenmişken 400 düşülmüş → net −150). Entry key +
    /// revision denetimi bunu yakalayamaz: kaybeden akış kazananın satırını
    /// YENİDEN OKUYUP onun kimliğiyle sınıflandırır, kimlik tutar ama bağlam
    /// (istenen toplam) tutmaz. Toplam eşleşmiyorsa bu tıklama durur —
    /// kazananın kendi çağrısı doğru mesajı üretir, kaybedenin sıradaki
    /// tıklaması 5. adımda kendi toplamını revizyonla yazar (K3).</summary>
    private BalanceOutcome DefiniteForCall(PaymentJob job, decimal requestedTotal)
    {
        if (job.ProductTotal != requestedTotal)
        {
            _log?.LogWarning(
                "Kesin sonuç başka bir toplamın satışına ait — mesaj engellendi "
                + "(job={JobId}, istenen={Requested}, satırdaki={RowTotal})",
                job.Id, requestedTotal, job.ProductTotal);
            return new(true, 0m, job);
        }
        return new(false, job.AppliedAmount ?? 0m, job);
    }

    /// <summary>İşin diskteki anahtarıyla apply'ı (yeniden) dener, sonucu işe
    /// yazar. Amount=ProductTotal gönderilir — sunucu bakiyeye/toplama kırpar;
    /// replay'de birebir aynı gövde gittiği için A11 içerik kontrolünden geçer.
    ///
    /// R4-03: gövdeye işin <c>ScopeKey</c>'i de eklenir — satışın kalıcı
    /// kimliği. Sunucu bunu A11 karşılaştırmasının DIŞINDA tutar, yoksa bu
    /// sürüme yükselen bir kurulumun kapsamsız yazılmış satırı tekrar
    /// denemesi kalıcı çakışmaya dönerdi. Miras işte asıl apply'ın kapsamı
    /// gider (<see cref="SaleScopeOf"/>).
    ///
    /// R4-01: cevap, GÖNDERİLDİĞİ denemeye aittir. İstek tele çıktıktan sonra
    /// araya bir revizyon girebilir (ağda takılan cevap dakikalarca sürebilir);
    /// o yüzden sonuç, isteği açan anahtar + revizyon koşuluyla yazılır. Koşul
    /// tutmuyorsa cevap sessizce DEĞİL, günlüğe yazılarak atılır — dönüşte
    /// satır yeniden okunduğu için çağıran zaten kazananın sonucunu görür.
    ///
    /// <para>Spec 2026-10-13 §6.6 (W3-11; sunucu bunları yalnız mod On'da üretir, kod moddan bağımsız):
    /// <list type="bullet">
    /// <item>409 <c>stale</c> (kapsam kilidi 5 sn'de alınamadı): hiçbir şey yazılmadı, iş anahtarıyla belirsiz kalır;
    ///   <see cref="ApplyAttempt.Stale"/> ile döner, çağıran bir kez yeniden dener.</item>
    /// <item>409 <c>scope-has-deduction</c>: kural yalnız anahtarı yeni apply'da çalışır — bu anahtar sunucuda HİÇ
    ///   yazılmadı, kapsamda başka bir geri alınmamış düşüm duruyor. Miras olmayan işte düşüm denemenin koşuluyla
    ///   benimsenir (<see cref="ApplyAttempt.Adopted"/>; çağıran revizyon adımına döner). Miras işte
    ///   (<see cref="PaymentJob.IsLegacyScope"/>) benimseme YOK: düşüm büyük olasılıkla başka bir yerel işindir ve iki iş
    ///   aynı düşümü sahiplenirdi — hiçbir şey yazılmaz, <see cref="ApplyAttempt.LegacyScopeDeduction"/> ile döner.</item>
    /// </list></para></summary>
    private async Task<ApplyAttempt> ReplayAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
    {
        if (job.ApplyKey is null)
            throw new InvalidOperationException($"Replay anahtarsız işte çağrıldı (job={job.Id})");

        var key = job.ApplyKey.Value;
        var revision = job.Revision;
        // R6-03: koşulun tutmamasının iki nedeni var — araya revizyon girmiş
        // olabilir (R4-01) ya da iş geri alma bekliyor olabilir (R4-02).
        // İkisini ayırt etmek, sahadan gelen günlükte tek fark yaratan bilgi.
        void LogBayat(string sonuc) => _log?.LogWarning(
            "Gecikmiş bakiye cevabı yok sayıldı ({Sonuc}) — iş artık {Durum} "
            + "(job={JobId}, key={Key}, rev={Revision})",
            sonuc, _jobs.Get(job.Id)?.State ?? "bilinmiyor", job.Id, key, revision);

        try
        {
            var apply = await _api.ApplyBalanceAsync(
                licenseId,
                new CustomerBalanceApplyRequest(
                    wpfCustomerId, job.ProductTotal, job.ProductTotal, key, SaleScopeOf(job)),
                ct);
            if (!_jobs.MarkApplied(job.Id, key, revision, apply.AppliedAmount))
                LogBayat("uygulandı");
        }
        catch (ValidationException ex) when (ex.Code is "no-balance" or "nothing-to-apply")
        {
            // Replay sunucuda balance kontrolünden ÖNCE çalışır: bu 409,
            // anahtarın hiç uygulanmadığının ve bakiye olmadığının kesin kanıtı.
            if (!_jobs.MarkNoBalance(job.Id, key, revision))
                LogBayat("bakiye yok");
        }
        catch (ValidationException ex) when (ex.Code == "content-conflict")
        {
            // Olmamalı: anahtar sunucuda FARKLI içerikle kayıtlı (A11). Kesin
            // cevap sayamayız — belirsiz bırak, yüksek sesle logla.
            _log?.LogError(
                "Bakiye anahtarı sunucuda farklı içerikle kayıtlı (job={JobId}, key={Key})",
                job.Id, job.ApplyKey);
        }
        catch (ValidationException ex) when (ex.Code == "stale")
        {
            _log?.LogWarning(
                "Bakiye apply'ı kilit zaman aşımı aldı (stale) — sunucu hiçbir şey yazmadı (job={JobId}, key={Key})",
                job.Id, key);
            return new(_jobs.Get(job.Id)!, Stale: true);
        }
        catch (ScopeHasDeductionException ex)
        {
            if (job.IsLegacyScope)
            {
                _log?.LogWarning(
                    "Miras işin apply'ı kapsamda başka düşüm buldu (scope-has-deduction) — benimsenmedi, birleştirme "
                    + "sorulacak (job={JobId}, key={Key}, tx={TransactionId})", job.Id, key, ex.TransactionId);
                return new(_jobs.Get(job.Id)!, LegacyScopeDeduction: ex);
            }
            if (_jobs.AdoptRemoteResult(job.Id, ex.TransactionId, ex.AppliedAmount, ex.ProductTotal, key, revision))
            {
                _log?.LogInformation(
                    "Apply kapsamda geri alınmamış düşüm buldu (scope-has-deduction) — düşüm benimsendi, revizyon "
                    + "adımına dönülüyor (job={JobId}, key={Key}, tx={TransactionId})", job.Id, key, ex.TransactionId);
                return new(_jobs.Get(job.Id)!, Adopted: true);
            }
            LogBayat("kapsamda düşüm var");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Bakiye apply belirsiz kaldı (job={JobId})", job.Id);
        }
        return new(_jobs.Get(job.Id)!);
    }

    /// <summary>Sunucuya giden satış kapsamı: asıl apply'ın gönderdiği kapsam. Miras işte bu, indirme
    /// öncesi kapsamdır (<see cref="PaymentJob.PriorScopeKey"/>) — <c>legacy:…</c> adı değil. Anahtar
    /// sunucuya hiç ulaşmamışsa replay düşümü ŞİMDİ yazar; miras adıyla yazılsaydı /scope (tam eşleşme)
    /// onu asıl kapsamda bulamaz, R4-03 çift düşüm koruması delinirdi (yedekten dönen kurulum aynı satışı
    /// ikinci kez düşerdi). Kapsam A11 içerik karşılaştırmasının dışında: kayıtlı anahtarların replay'i
    /// etkilenmez, idempotency anahtarı (ApplyKey) aynı kalır. Asıl kapsamı bilinmeyen (047 öncesi) miras
    /// eskisi gibi kendi adıyla gider. Tanım <see cref="PaymentJob.SaleScopeKey"/>'de: kirasız ödeme kaydı
    /// (W3-06) aynı eşlemeyle gider.</summary>
    private static string SaleScopeOf(PaymentJob job) => job.SaleScopeKey;

    /// <summary>R4-02: diske inmiş geri alma niyetini uzlaştırır. Sunucudaki
    /// geri alma deterministik idempotenttir (tekrar = 409 already-reversed =
    /// başarı, A8), yani kaybolan bir cevap HER ZAMAN tekrar denenerek
    /// kesinleştirilebilir — bu yüzden burada ürün kararına gerek yok.
    ///
    /// <para>Kesinleşirse iş hedef toplamla "taze"ye döner ve çağıran normal
    /// akıştan YENİ bir anahtarla temiz bir düşüm yapar. Kesinleşmezse niyet
    /// diskte kalır (dönüşte durum hâlâ reverse_pending) ve akış durur:
    /// operatörün bir sonraki tıklaması kaldığı yerden sürdürür.</para></summary>
    private async Task<PaymentJob> FinishReversalAsync(
        Guid licenseId, PaymentJob job, CancellationToken ct)
    {
        var key = job.ApplyKey!.Value;
        if (!await TryReverseAsync(licenseId, key, ct))
        {
            _log?.LogWarning(
                "Geri alma kesinleşmedi — niyet diskte kalıyor, mesaj engellendi "
                + "(job={JobId}, tx={TxId})", job.Id, key);
            return job;
        }
        // false = araya giren akış uzlaştırmayı zaten yaptı; yeniden okuyup
        // onun bıraktığı satırla devam etmek doğru davranış.
        _jobs.CompleteReversal(job.Id, job.Revision, key);
        return _jobs.Get(job.Id)!;
    }

    /// <summary>Eski düşümü geri alır (K2). 409 already-reversed = idempotent
    /// başarı (A8). Geçici hatada bir kez daha dener (K3); yine olmazsa false.</summary>
    private async Task<bool> TryReverseAsync(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await _api.ReverseBalanceTransactionAsync(licenseId, transactionId, ct);
                return true;
            }
            catch (ValidationException ex) when (ex.Code == "already-reversed")
            {
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.LogWarning(ex,
                    "Bakiye geri alma denemesi {Attempt} düştü (tx={TxId})", attempt, transactionId);
            }
        }
        return false;
    }

    /// <summary>
    /// Kümülatif kargo PR-E (2026-05-12): Müşteri ücretsiz kargo eşiğini
    /// aştığında vendor "Evet kargolansın" dedikten sonra çağrılır. WhatsApp
    /// linki açar; phone yoksa PhoneRequired döner. Template'i AppSettings'ten
    /// okur; boşsa Opened ile fail-silent çıkar (mesaj atılmaz).
    /// </summary>
    public PaymentRequestResult OpenShippingWonWhatsApp(Customer customer, decimal cumulativeAmount)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var template = settings.Payment.ShippingWonTemplate;
        if (string.IsNullOrWhiteSpace(template))
            return PaymentRequestResult.Opened; // template kapalı, sessiz geç

        var message = _messageBuilder.BuildShippingWonMessage(
            template,
            customer.DisplayName ?? customer.Username,
            cumulativeAmount);
        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            return PaymentRequestResult.LaunchFailed;
        }
    }

    /// <summary>Cloud API denemesinin ayrı sonuçları. bool yetmiyor: "gönderildi"
    /// ile "sonucu bilmiyoruz" ikisi de wa.me'yi açtırmıyor ama operatöre
    /// söylenecek şey taban tabana zıt.</summary>
    private enum CloudSendOutcome
    {
        /// <summary>Sunucu gönderimi onayladı.</summary>
        Sent,

        /// <summary>Çağıran wa.me'ye düşmeli; Cloud API mesajı KESİN GÖNDERMEDİ: istek hiç gitmedi
        /// (lisans çözülemedi) ya da sunucu Graph'a çıkmadan reddetti
        /// (<see cref="CloudNeverSentCodes"/>). Yalnız bu durumda wa.me açılamazsa deneme geri alınır.</summary>
        FallBack,

        /// <summary>Çağıran wa.me'ye düşmeli ama Cloud API'nin mesajı gönderip göndermediği
        /// bilinmiyor (zaman aşımı, ağ/sunucu hatası, sunucunun <c>network</c>/<c>server_error</c>
        /// cevabı, Graph'ın hata kodu ve tanınmayan her kod) — wa.me de açılamazsa gönderim denemesi
        /// geri alınmaz.</summary>
        FallBackUnknown,

        /// <summary>Aynı gönderim sunucuda hâlâ işleniyor — sonuç bilinmiyor.</summary>
        Pending
    }

    /// <summary>
    /// Pencere kapalıyken kullanılacak onaylı şablonu hazırlar; hazırlanamıyorsa
    /// null döner ve sunucu eski davranışa (<c>window_closed</c> → wa.me) düşer.
    ///
    /// <para>Prodda 24 saatlik pencere çoğu müşteride KAPALI olacağı için asıl
    /// gönderim yolu burasıdır — serbest metin yalnız müşteri son 24 saatte
    /// yazmışsa geçerli.</para>
    ///
    /// <para>Şablon adı ya da alan eşlemesi boşsa yol bilerek kapalıdır: Meta'da
    /// onaylanmamış bir ad göndermek her denemede hata almak demek, eşlemesiz
    /// şablon da yanlış sayıda parametre demek. Parametrelerden biri (ör. IBAN
    /// ya da hesap sahibi girilmemişse) boşsa da null döner — Meta boş parametre
    /// kabul etmiyor.</para>
    /// </summary>
    private WhatsAppTemplateRef? BuildTemplateRef(AppSettings settings, PaymentContext ctx)
    {
        var name = settings.Payment.CloudTemplateName;
        var language = settings.Payment.CloudTemplateLanguage;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language))
            return null;

        // Eşleme ayardan geliyor çünkü şablonun şeklini Meta belirliyor, biz
        // değil (ad ve dil onaydan sonra değiştirilemiyor). Eşleme kurulmamışsa
        // parametre sayısını tahmin etmektense şablon yolunu hiç açmıyoruz.
        var mapping = settings.Payment.CloudTemplateParams;
        if (mapping is null || mapping.Count == 0)
        {
            _log?.LogInformation(
                "WhatsApp şablonu atlandı: '{Name}' için alan eşlemesi kurulmamış (Ayarlar → WhatsApp Cloud API)",
                name);
            return null;
        }

        var parameters = _messageBuilder.BuildPaymentTemplateParams(ctx, mapping);
        if (parameters is null)
        {
            _log?.LogInformation(
                "WhatsApp şablonu atlandı: gövde parametrelerinden biri boş (IBAN/hesap sahibi girili mi?)");
            return null;
        }

        return new WhatsAppTemplateRef(name.Trim(), language.Trim(), parameters);
    }

    /// <summary>Sunucunun Graph'a ÇIKMADAN döndürdüğü ret kodları — mesaj kesin gönderilmedi
    /// (LicenseServer WhatsAppMessagingService.SendWithFallbackAsync: telefon geçersiz, gövde boş,
    /// lisansa bağlı hesap yok, 24 saatlik pencere kapalı ve şablon yok). Gerisi — 'network',
    /// 'server_error', Graph'ın sayısal hata kodları ya da HTTP durumu, tanınmayan kod — sonucu bilinmeyen
    /// gönderimdir.</summary>
    private static readonly HashSet<string> CloudNeverSentCodes = new(StringComparer.Ordinal)
    {
        "bad_phone", "empty_body", "no_account", "window_closed",
    };

    /// <summary>
    /// Mesajı Cloud API ile göndermeyi dener.
    ///
    /// Yalnızca çağıranın açıkça iptal ettiği durum (ct iptal edilmişse)
    /// dışarı sızar; diğer tüm hatalar — HTTP zaman aşımı dahil — yutulur:
    /// ödeme isteme akışı, opsiyonel bir gönderim yolunun hatasıyla durmamalı.
    /// </summary>
    private async Task<CloudSendOutcome> TrySendViaCloudApiAsync(
        string phone, string message, WhatsAppTemplateRef? template,
        string origin, CancellationToken ct)
    {
        // Çağrı başına yeni anahtar: dayanıklılık katmanı bu POST'u yeniden
        // denerse gövde (dolayısıyla anahtar) aynı kalır ve sunucu tekrarı eler.
        // try'ın DIŞINDA duruyor ki zaman aşımı kaydında da yer alsın.
        var idempotencyKey = Guid.NewGuid();
        try
        {
            var licenseId = await ResolveLicenseIdAsync(ct);
            if (licenseId is null) return CloudSendOutcome.FallBack;

            var resp = await _api.SendWhatsAppTextAsync(
                licenseId.Value,
                new WhatsAppSendRequest(phone, message, origin, idempotencyKey, template), ct);

            if (!resp.Ok)
            {
                if (resp.ErrorCode == WhatsAppSendErrorCodes.InProgress)
                {
                    // wa.me açmak operatörü ikinci bir kopya yollamaya davet eder;
                    // ama "gönderildi" demek de yalan olur — sunucu sadece
                    // "bilmiyorum" diyor. Kararı operatöre bırakıyoruz.
                    _log?.LogWarning(
                        "Aynı WhatsApp gönderimi hâlâ işleniyor — sonuç bilinmiyor, wa.me açılmıyor (key={Key})",
                        idempotencyKey);
                    return CloudSendOutcome.Pending;
                }

                _log?.LogInformation(
                    "WhatsApp Cloud API gönderimi yapılamadı ({Code}) — wa.me'ye düşülüyor (key={Key})",
                    resp.ErrorCode, idempotencyKey);
                // Ok=false her zaman "gönderilmedi" demek değil: sunucu Graph'a çıkıp sonucu bilemediğinde
                // de (Graph zaman aşımı → 'network', sunucu istisnası → 'server_error' — dayanıklılık
                // katmanının yeniden denemesi bunu oynatır, Graph 5xx) Ok=false döner; Meta mesajı kabul
                // etmiş olabilir. Yalnız Graph'a ÇIKMADAN verilen ret kesin.
                return resp.ErrorCode is { } code && CloudNeverSentCodes.Contains(code)
                    ? CloudSendOutcome.FallBack
                    : CloudSendOutcome.FallBackUnknown;
            }

            return CloudSendOutcome.Sent;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Zaman aşımında sunucunun mesajı gönderip göndermediğini bilmiyoruz;
            // wa.me açılıyor, yani operatör elle ikinci bir kopya yollayabilir.
            // Anahtarı loga yazıyoruz ki "müşteriye iki mesaj gitti" şikâyetinde
            // gönderim sunucu tarafında izlenebilsin.
            _log?.LogWarning(
                "WhatsApp Cloud API zaman aşımı — wa.me'ye düşülüyor (key={Key})", idempotencyKey);
            return CloudSendOutcome.FallBackUnknown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "WhatsApp Cloud API gönderimi hata verdi — wa.me'ye düşülüyor (key={Key})", idempotencyKey);
            return CloudSendOutcome.FallBackUnknown;
        }
    }

    /// <summary>
    /// Kargo karar matrisi:
    /// <list type="bullet">
    ///   <item>Customer.RecipientPaysActive → "Kargo: alıcı ödemeli" (kargo fee
    ///     müşteriden istenmez; kargo şirketi tahsil eder)</item>
    ///   <item>Shipping feature kapalı → ShippingNote boş, total = productTotal</item>
    ///   <item>productTotal &gt;= FreeShippingThreshold → "Ücretsiz kargo"</item>
    ///   <item>productTotal &lt; threshold → "Kargo: X TL", total = productTotal + fee</item>
    /// </list>
    /// <para>Ayarı çağıran <see cref="IShippingSettingsProvider"/>'dan verir (spec 2026-10-13 §6.2): <c>AppSettings</c>
    /// alan bir biçim bilerek yok — mod On'da yerel ayarın okunduğu bir yol derlenmesin.</para>
    /// </summary>
    internal static (decimal Total, decimal? Fee, string Note) ComputeShipping(
        Customer customer, decimal productTotal, ShippingSettings shipping)
    {
        if (customer.RecipientPaysActive)
        {
            return (productTotal, null, "Kargo: alıcı ödemeli");
        }

        if (!shipping.IsEnabled)
        {
            return (productTotal, null, "");
        }

        if (productTotal >= shipping.FreeShippingThreshold!.Value)
        {
            return (productTotal, null, "Ücretsiz kargo");
        }

        var fee = shipping.ShippingFee!.Value;
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var note = $"Kargo: {fee.ToString("N2", tr)} TL";
        return (productTotal + fee, fee, note);
    }
}
