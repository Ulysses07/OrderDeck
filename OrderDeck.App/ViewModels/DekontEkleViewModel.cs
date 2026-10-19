using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// "Gelen Ödemeler" çekmecesinin ViewModel'i: müşteriden ödeme geldiğinde kargo zincirini yürütür. Form yalnız
/// müşteri (platform + kullanıcı adı) ve gelen tutardır.
///
/// 2026-10-19: WPF'ten dekont kaydı kalktı (iş sahibi kararı). Çekmece eskiden "Yeni Dekont"tu: PDF'ten okunan ya da
/// elle girilen dekontu bir <c>Payment</c> satırı olarak yazıyor, satır payments/sync ile panelin Dekontlar kuyruğuna
/// gidiyordu. Artık hiçbir <c>Payment</c> satırı YAZMAZ; PDF, alıcı IBAN/hesap sahibi uyarısı, ödeyen, tarih ve
/// referans no alanları da kalktı. <c>PaymentSyncService</c> ve payments/sync sözleşmesi değişmedi: yerelde
/// gönderilmemiş eski satırlar yine gider. Sınıf ve komut adları (<c>DekontEkle*</c>) bilerek korundu; spec
/// 2026-10-13 (§5.3, §5.8, W2-08) ve testler bu adlarla anar.
///
/// Kargo zinciri değişmedi (Kargo PR D/F, Faz 2b, W2-08): oturum seçilir (aktif yayın, yoksa son biten), gelen tutar
/// <see cref="PaymentMatcherService"/> ile müşterinin ürün toplamı + kargo ücretiyle karşılaştırılır; kargo ücreti
/// eksikse "Kargo Ücreti Eksik" alt çekmecesi yönergeyi sorar (Alıcı Ödemeli / Kargo Beklesin / Vazgeç).
/// RecipientPays seçildiyse müşterinin yapışkan bayrağı açılır. Ardından kargo adımı (dosyaya bağlama, yönerge, eşik
/// kararı) düzenleme yönlendiricisinden geçer (<see cref="Services.Sales.IShipmentEditor"/>): mod Off'ta yerel sıra,
/// eşzamanlı; mod On'da <c>attach-shipment</c> ve <c>shipment-decision</c> komutları, ağ işi arka planda, kaydetme
/// zinciri bekler. Eşik aşıldıysa "Ücretsiz Kargo Hakkı" alt çekmecesi açılır; ShipNow'da "kazandın" mesajı. Kargo
/// adımı yapılamazsa <see cref="ShipmentWarning"/> dolar (kabuk satış şeridinde gösterir, açılır pencere yok).
///
/// Müşteri ZORUNLU: kargo zinciri müşterisiz çalışmaz. Boşsa ya da bulunamazsa satır içi hata, hiçbir şey yazılmaz.
/// Alt çekmeceleri <see cref="Services.Drawers.IDrawerService"/> üzerinden açar; servis verilmezse (test
/// fixture'ları) sorular atlanır.
/// </summary>
public sealed partial class DekontEkleViewModel : ObservableObject
{
    private readonly CustomerRepository _customers;
    private readonly SessionRepository _sessions;
    private readonly PaymentMatcherService _matcher;
    private readonly Services.Sales.IShipmentEditor _shipmentEditor;
    private readonly OrderDeck.App.Services.PaymentRequestService? _paymentRequest;
    private readonly ILogger<DekontEkleViewModel> _log;

    [ObservableProperty] private string _amountText = "";

    // Müşteri: kargo zincirinin anahtarı (zorunlu).
    [ObservableProperty] private string _customerPlatform = "";
    [ObservableProperty] private string _customerUsername = "";

    [ObservableProperty] private string? _errorMessage;

    /// <summary>W2-08 (spec 2026-10-13 §5.8): kargo adımı ya da eşik kararı uygulanamadı (çevrimdışı, komut ucu yok,
    /// sunucu reddetti). Kaydetmeyi engellemez; çekmece kapanınca kabuk satış şeridinde gösterir (açılır pencere yok).
    /// Her kaydetmede sıfırlanır.</summary>
    [ObservableProperty] private string? _shipmentWarning;

    /// <summary>Kaydetme zinciri sürüyor (<see cref="SaveAsync"/>): Kaydet pasif, ikinci çağrı zinciri yeniden
    /// koşturmaz. 2026-10-19'a kadar bu işi referans no tekrar kontrolü yan etki olarak yapıyordu (Payment satırı ilk
    /// await'ten önce yazılırdı); mod On'da attach-shipment ağda beklerken çift tıklama zinciri iki kez
    /// koşturuyordu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isSaving;

    private readonly IShippingSettingsProvider _shipping;
    private readonly Services.Drawers.IDrawerService? _drawers;

    /// <param name="shipping">Eşik çekmecesinin eşik metni (spec 2026-10-13 §6.2): değerlendirmeyle aynı kaynak — mod
    /// Off'ta yerel ayar, mod On'da lisans geneli ayar.</param>
    public DekontEkleViewModel(
        CustomerRepository customers,
        SessionRepository sessions,
        PaymentMatcherService matcher,
        ShipmentService shipments,
        IShippingSettingsProvider shipping,
        ILogger<DekontEkleViewModel> log,
        OrderDeck.App.Services.PaymentRequestService? paymentRequest = null,
        Services.Drawers.IDrawerService? drawers = null,
        Services.Sales.ILabelEditor? editor = null)
    {
        _customers = customers;
        _sessions = sessions;
        _matcher = matcher;
        // Kargo adımı (W2-08) düzenleyiciden geçer: uygulamada DI yönlendiriciyi verir; verilmezse (testler) bugünkü
        // yerel yol. Bağlanmamış etiketleri ShipmentService kendi deposundan okur.
        _shipmentEditor = editor ?? (Services.Sales.IShipmentEditor)new Services.Sales.LocalShipmentEditor(shipments);
        _shipping = shipping;
        _log = log;
        _paymentRequest = paymentRequest;
        _drawers = drawers;
    }

    public bool CanSave => !IsSaving && Validate() is null;

    partial void OnAmountTextChanged(string value) => RefreshCanSave();
    partial void OnCustomerPlatformChanged(string value) => RefreshCanSave();
    partial void OnCustomerUsernameChanged(string value) => RefreshCanSave();

    private void RefreshCanSave()
    {
        OnPropertyChanged(nameof(CanSave));
        ErrorMessage = null;
    }

    /// <summary>İlk kaydetme denemesinin sonucu. <see cref="SaveAsync"/> buna
    /// göre dallanır:
    /// <list type="bullet">
    ///   <item><c>Saved</c>: çekmece başarıyla kapanır.</item>
    ///   <item><c>NeedsShipmentDecision</c>: <c>Shortage</c> ile kargo
    ///   yönergesi çekmecesi açılır, sonra <see cref="CommitWithDirective"/>.</item>
    ///   <item><c>Error</c>: çekmece açık kalır, <c>Error</c> gösterilir.</item>
    /// </list>
    /// Tip hâlâ <c>public</c>: testler bu üç ilkeli doğrudan çağırıyor.</summary>
    public sealed record SaveResult(
        SaveResultKind Kind,
        PaymentMatcherService.MatchResult? Shortage = null,
        string? Error = null,
        // PR-C/2: kayıt başarılı + kümülatif eşik aşıldıysa kargo eşiği
        // çekmecesi açılmalı. Null = eşik aşılmadı veya hook çalışmadı
        // (feature kapalı, adım atlandı, vs.).
        ShipmentDecisionContext? ThresholdContext = null)
    {
        /// <summary>W2-08: mod On'da kargo adımı (komut) arka planda sürüyor; <see cref="SaveAsync"/> bekler ve
        /// sonucu <see cref="ThresholdContext"/>'e yazar. Mod Off'ta adım eşzamanlı biter, bu null'dır.</summary>
        internal Task<ShipmentDecisionContext?>? PendingShipmentStep { get; init; }
    }

    public enum SaveResultKind { Saved, NeedsShipmentDecision, Error }

    /// <summary>
    /// Kaydetme zincirinin tamamı — çekmecenin "Kaydet" düğmesi yalnız bunu
    /// çağırır. <c>true</c>: zincir tamam, çekmece kapansın. <c>false</c>:
    /// doğrulama hatası (<see cref="ErrorMessage"/> bağlı), operatör kargo
    /// yönergesinden vazgeçti — form açık kalsın, düzeltilebilsin — ya da
    /// zincir zaten sürüyor (<see cref="IsSaving"/>; çift tıklama), ikinci
    /// çağrı hiçbir şey yapmaz.
    ///
    /// Faz 2b'de <c>DekontEkleDialog.OnSave</c>'in switch'inden birebir
    /// taşındı; sıra ve dallanma aynı.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (IsSaving) return false;
        IsSaving = true;
        try
        {
            ShipmentWarning = null;
            var result = TrySave();

            if (result.Kind == SaveResultKind.NeedsShipmentDecision)
            {
                var directive = await AskShipmentDirectiveAsync(result.Shortage!);
                if (directive is null) return false;
                result = CommitWithDirective(directive.Value);
            }

            if (result.Kind != SaveResultKind.Saved) return false;

            // W2-08: mod On'da kargo adımı (attach-shipment) arka planda sürüyor; eşik sorusu adımın sonucuyla.
            if (result.PendingShipmentStep is { } pending)
                result = result with { ThresholdContext = await pending, PendingShipmentStep = null };

            await MaybePromptThresholdAsync(result);
            return true;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task<ShipmentDirective?> AskShipmentDirectiveAsync(
        PaymentMatcherService.MatchResult match)
    {
        if (_drawers is null) return null;
        var vm = new ShipmentDirectiveDialogViewModel(match);
        await _drawers.ShowAsync("Kargo Ücreti Eksik",
            d => new Views.Drawers.ShipmentDirectiveDrawer(d, vm));
        return vm.ChosenDirective;
    }

    /// <summary>
    /// Eşik çekmecesi. Mod On'da karar <c>stale</c> alırsa (çekmece açıkken dosya başka bilgisayarda değişti) sessiz
    /// yeniden deneme yok: plan güncel havuzla, sebebi başında yazarak BİR KEZ yeniden gösterilir (spec 2026-10-13
    /// §5.3); havuz eşiğin altına indiyse ya da dosya kargolandıysa sorulacak karar yoktur, sebep şeritte kalır.
    /// </summary>
    private async Task MaybePromptThresholdAsync(SaveResult result)
    {
        if (result.ThresholdContext is not { Shipment: not null } ctx) return;
        if (_drawers is null) return;

        var customerDisplay = $"{CustomerPlatform}/{CustomerUsername}";

        string? notice = null;
        for (var shown = 0; shown < 2; shown++)
        {
            var vm = new ShipmentThresholdDialogViewModel(
                ctx, customerDisplay, _shipping.Current().Settings.FreeShippingThreshold ?? 0m) { Notice = notice };

            await _drawers.ShowAsync("Ücretsiz Kargo Hakkı",
                d => new Views.Drawers.ShipmentThresholdDrawer(d, vm));

            if (vm.ChosenDecision is not { } decision) return;
            var decided = await ApplyShipmentDecisionCoreAsync(ctx.Shipment!.Id, decision);
            if (decided is null) return;
            if (decided.Problem is null)
            {
                if (shown > 0) ShipmentWarning = null;       // yeniden gösterilen plandaki karar uygulandı
                return;
            }
            if (decided.Replan is not { Shipment: not null } replan) return;
            ctx = replan;
            notice = Services.Sync.SalesCommandMessages.Stale;
        }
    }

    /// <summary>İlk kaydetme denemesi: doğrulama + müşteri çözümü +
    /// matcher. Dallanmayı <see cref="SaveAsync"/> yapar. Müşteri boşsa
    /// ya da bulunamazsa satır içi hata döner, hiçbir şey yazılmaz.</summary>
    public SaveResult TrySave()
    {
        var validationError = Validate();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return new SaveResult(SaveResultKind.Error, Error: validationError);
        }

        // Kargo zinciri müşterisiz çalışmaz: eskiden bulunamayan müşteride yalnız dekont yazılıyordu, artık
        // yazılacak bir şey kalmadı — sessizce kapanmak "oldu" sanılırdı.
        var customer = ResolveCustomer();
        if (customer is null)
        {
            _log.LogInformation(
                "Gelen ödeme: müşteri bulunamadı ({Platform}/{Username}); hiçbir şey yazılmadı.",
                CustomerPlatform, CustomerUsername);
            ErrorMessage = "Müşteri bulunamadı. Platformu ve kullanıcı adını kontrol et.";
            return new SaveResult(SaveResultKind.Error, Error: ErrorMessage);
        }

        // Oturum: operatörlerin çoğu ödemeyi yayın bittikten sonra işliyor → aktif yayın yokken matcher devre dışı
        // kalmasın diye son yayına düşülür.
        var session = _sessions.GetActive() ?? _sessions.GetLatestEnded();
        var amount = ParseAmount(AmountText)!.Value;

        if (session is not null)
        {
            // Matcher yalnız gelen tutarı alır; kayıt yazılmaz.
            var match = _matcher.Match(customer.Id, session.Id, amount);
            if (match.Outcome == PaymentMatcherService.MatchOutcome.ShippingShortage)
            {
                // Operatörün yönergesi bekleniyor.
                return new SaveResult(SaveResultKind.NeedsShipmentDecision, Shortage: match);
            }
        }

        return CommitInternal(ShipmentDirective.Normal);
    }

    /// <summary>Operatör kargo yönergesi çekmecesinde seçim yaptıktan sonra
    /// çağrılır. Seçilen yönergeyle kargo zincirini yürütür.</summary>
    public SaveResult CommitWithDirective(ShipmentDirective directive)
    {
        return CommitInternal(directive);
    }

    private SaveResult CommitInternal(ShipmentDirective directive)
    {
        try
        {
            // Kargo PR F: RecipientPays seçildiyse müşterinin yapışkan bayrağı açılır; baskı şablonu etikete
            // "ALICI ÖDEMELİ" basar. Bayrak için CustomerId şart.
            if (directive == ShipmentDirective.RecipientPays)
            {
                var resolved = ResolveCustomer();
                if (resolved is not null)
                {
                    _customers.SetRecipientPaysActive(resolved.Id, true);
                    _log.LogInformation(
                        "Customer {Id} RecipientPaysActive=true (kargo bedeli alıcıdan tahsil edilecek)",
                        resolved.Id);
                }
            }

            _log.LogInformation(
                "Gelen ödeme: tutar={Amount} directive={Directive} — kargo adımı başlıyor",
                ParseAmount(AmountText), directive);

            // PR-C/2: Kümülatif kargo adımı. Vendor "Hold" dediyse Shipment Held'e alınır ama kümülatif Label'lar yine
            // bağlanır — sonraki yayında eşik doğru hesaplansın.
            // W2-08: adım düzenleyiciden geçer. Mod Off'ta yerel sıra burada, eşzamanlı biter (görev tamamlanmış
            // döner); mod On'da komut arka planda sürer, SaveAsync bekler.
            var step = TryEvaluateShipmentAsync(directive);
            return step.IsCompletedSuccessfully
                ? new SaveResult(SaveResultKind.Saved, ThresholdContext: step.Result)
                : new SaveResult(SaveResultKind.Saved) { PendingShipmentStep = step };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gelen ödeme kaydedilemedi");
            ErrorMessage = "Kaydetme başarısız: " + ex.Message;
            return new SaveResult(SaveResultKind.Error, Error: ErrorMessage);
        }
    }

    /// <summary>
    /// Kümülatif kargo akışını ilerletir: müşterinin açık Shipment'ını bul/oluştur, henüz bağlanmamış Label'ları
    /// attach et, threshold check yap. Müşteri çözülemezse veya feature kapalıysa sessiz çıkar.
    ///
    /// Vendor "Hold" seçtiyse Shipment'ı Held'e alır (ApplyDecision); eşik aşılsa bile eşik sorusu sorulmaz çünkü
    /// vendor zaten beklet dedi. Vendor "RecipientPays" seçtiyse Shipment RecipientPays'a alınır — yine sorulmaz
    /// (sticky kararı).
    ///
    /// Yalnız "Normal" directive + threshold aşılmışsa context döndürülür; <see cref="SaveAsync"/> kargo eşiği
    /// çekmecesini açar.
    ///
    /// W2-08: adım <see cref="Services.Sales.IShipmentEditor"/>'dan geçer. Mod On'da adım atlanırsa (çevrimdışı, komut
    /// ucu yok, ret) uyarı <see cref="ShipmentWarning"/>'e yazılır. Hiç fırlatmaz.
    /// </summary>
    private async Task<ShipmentDecisionContext?> TryEvaluateShipmentAsync(ShipmentDirective directive)
    {
        try
        {
            var customer = ResolveCustomer();
            if (customer is null) return null;

            // Aç ya da bul → bağlanmamış etiketleri bağla → yönerge / eşik (mod Off: ShipmentService.AttachAfterPayment).
            var step = await _shipmentEditor.RunDekontShipmentStepAsync(customer.Id, directive);
            if (step.Warning is { } warning)
            {
                ShipmentWarning = warning;
                _log.LogInformation("Gelen ödeme kargo adımı: Customer={Cid} — adım tamamlanmadı, uyarı gösterildi", customer.Id);
            }
            var ctx = step.Threshold;
            if (ctx is not null)
            {
                _log.LogInformation(
                    "Kümülatif eşik aşıldı: Customer={Cid} Shipment={Sid} Total={Total}",
                    customer.Id, ctx.Shipment!.Id, ctx.PooledAmount);
            }
            return ctx;
        }
        catch (Exception ex)
        {
            // Hook silent: ana akışı bozmasın — bir sonraki gelen ödemede tekrar denenir (Shipment idempotent).
            _log.LogWarning(ex,
                "Shipment evaluate hook hata verdi (silent skip); ana save akışı etkilenmedi.");
            return null;
        }
    }

    /// <summary>
    /// Vendor kargo eşiği çekmecesinde karar verdikten sonra çağrılır.
    /// Decision Shipment state'ine uygulanır.
    ///
    /// PR-E (2026-05-12): ShipNow seçilirse müşteriye WhatsApp "ücretsiz
    /// kargo kazandın" mesajı tetiklenir (fail-silent — phone yoksa veya
    /// template boşsa sessiz geçer).
    ///
    /// W2-08: <see cref="ApplyShipmentDecisionAsync"/>'i başlatır. Mod Off'ta
    /// karar çağıranın iş parçacığında, beklemeden uygulanır (bugünkü gibi);
    /// mod On'da komut arka planda biter.
    /// </summary>
    public void ApplyShipmentDecision(string shipmentId, ShipmentDecision decision)
        => _ = ApplyShipmentDecisionAsync(shipmentId, decision);

    /// <summary>
    /// Eşik çekmecesinin kararı (spec 2026-10-13 §5.3 "Eşik çekmecesi
    /// kararı"): mod Off'ta yerel karar, mod On'da <c>shipment-decision</c>.
    /// Uygulanamazsa (kargolanmış dosya, araya başka bilgisayarın kararı
    /// girdi, çevrimdışı) metin <see cref="ShipmentWarning"/>'e yazılır; dosyanın
    /// güncel hâli yerel aynadadır. ShipNow'da "ücretsiz kargo kazandın" mesajı
    /// kararın kapsadığı dosyaların toplamıyla (mod On'da yanıtın tutarları).
    /// Hiç fırlatmaz.
    /// </summary>
    public Task ApplyShipmentDecisionAsync(string shipmentId, ShipmentDecision decision)
        => ApplyShipmentDecisionCoreAsync(shipmentId, decision);

    /// <returns>Kararın sonucu; karar hata verdiyse (günlüğe yazıldı) null.</returns>
    private async Task<Services.Sales.SalesShipmentDecisionResult?> ApplyShipmentDecisionCoreAsync(
        string shipmentId, ShipmentDecision decision)
    {
        Services.Sales.SalesShipmentDecisionResult result;
        try
        {
            result = await _shipmentEditor.DecideShipmentAsync(shipmentId, decision);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Shipment {Id} ApplyDecision {Decision} hata verdi", shipmentId, decision);
            return null;
        }

        if (result.Problem is { } problem)
        {
            ShipmentWarning = problem;
            _log.LogInformation(
                "Shipment {Id} → {Decision} uygulanmadı (kümülatif kargo çekmecesinden)",
                shipmentId, decision);
            return result;
        }
        _log.LogInformation(
            "Shipment {Id} → {Decision} (kümülatif kargo çekmecesinden)",
            shipmentId, decision);

        // PR-E: ShipNow → "kazandın" WhatsApp tetikleyici. PaymentRequestService
        // null ise (test fixture'larda) sessiz geç.
        if (decision == ShipmentDecision.ShipNow && _paymentRequest is not null && result.Shipment is not null)
        {
            TriggerShippingWonWhatsApp(result.Shipment, result.PooledAmount);
        }
        return result;
    }

    /// <param name="pooledAmount">Kararın kapsadığı dosyaların toplamı (U12 — yerel taşımadan
    /// kalan fazla dosya dahil); mesaj ve günlük bunu söyler.</param>
    private void TriggerShippingWonWhatsApp(Shipment shipment, decimal pooledAmount)
    {
        try
        {
            var customer = _customers.GetById(shipment.CustomerId);
            if (customer is null)
            {
                _log.LogDebug("Kazandın WhatsApp: Customer {Id} bulunamadı, atlandı.",
                    shipment.CustomerId);
                return;
            }
            var result = _paymentRequest!.OpenShippingWonWhatsApp(customer, pooledAmount);
            _log.LogInformation(
                "Kazandın WhatsApp tetikleyici: Shipment={Sid} Customer={Cid} Tutar={Amt} → {Result}",
                shipment.Id, shipment.CustomerId, pooledAmount, result);
        }
        catch (Exception ex)
        {
            // Fail-silent — kullanıcı manuel mesaj atabilir.
            _log.LogWarning(ex,
                "Kazandın WhatsApp tetikleyici hata verdi (silent skip).");
        }
    }

    private Customer? ResolveCustomer()
    {
        var platform = CustomerPlatform?.Trim();
        var username = CustomerUsername?.Trim();
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(username))
            return null;
        return _customers.FindByPlatformAndUsername(platform, username);
    }

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(CustomerPlatform) || string.IsNullOrWhiteSpace(CustomerUsername))
            return "Müşteri gerekli: platformu ve kullanıcı adını gir.";

        var amount = ParseAmount(AmountText);
        if (amount is null)
            return "Tutar geçersiz (örn: 250,50 veya 250.50).";
        if (amount.Value <= 0)
            return "Tutar 0'dan büyük olmalı.";

        return null;
    }

    private static decimal? ParseAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().Replace(',', '.');
        return decimal.TryParse(normalized,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var amount) ? amount : null;
    }
}
