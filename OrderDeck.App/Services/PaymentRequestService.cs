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
}

/// <summary>
/// Phase 4g: Customer + tutar + tarih → Settings template substitution + wa.me launch.
/// Phone null/invalid ise PhoneRequired (caller PhoneEntryDialog açar).
///
/// Kargo entegrasyon (2026-05-12): caller ürün toplamını geçer, service
/// AppSettings.Shipping + Customer.RecipientPaysActive flag'ine göre kargo
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
    private readonly Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? _log;

    public PaymentRequestService(
        SettingsStore settingsStore,
        WhatsAppMessageBuilder messageBuilder,
        IUrlLauncher launcher,
        LicenseApiClient api,
        ICurrentLicenseProvider currentLicense,
        IPaymentJobStore jobs,
        Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? log = null)
    {
        _settingsStore = settingsStore;
        _messageBuilder = messageBuilder;
        _launcher = launcher;
        _api = api;
        _currentLicense = currentLicense;
        _jobs = jobs;
        _log = log;
    }

    // License key → Guid LicenseId resolution. Aynı pattern ShopperRegistrationIngest /
    // WpfCustomerProjectionSync'te de var. Caching: LicenseKey değişene kadar tutar.
    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

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
        var (totalAmount, shippingFee, shippingNote) = ComputeShipping(customer, productTotal, settings);

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
    public async Task<PaymentRequestResult> OpenWhatsAppAsync(
        Customer customer, decimal productTotal, DateTime streamDate,
        string scopeKey, CancellationToken ct = default)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var (totalAmount, shippingFee, shippingNote) = ComputeShipping(customer, productTotal, settings);

        // Bakiye uygulaması — PaymentJob durum makinesi (R2-01..04).
        // Eski fail-silent davranış BİLEREK terk edildi: sonuç belirsizse mesaj
        // gönderilmez (BalanceUncertain). "Bakiyesi düşmüş ama mesajı yanlış
        // tutarlı" ihtimali, "operatör bir kez daha tıklar" maliyetinden ağır.
        decimal appliedBalance = 0m;
        var totalBeforeBalance = totalAmount;
        PaymentJob? deliveryJob = null;   // mesaj müşteriye ulaşınca kapatılacak iş
        if (totalAmount > 0 && Guid.TryParseExact(customer.Id, "N", out var wpfCustomerId))
        {
            var outcome = await ResolveBalanceAsync(
                customer, wpfCustomerId, totalAmount, scopeKey, ct);
            if (outcome.Uncertain)
                return PaymentRequestResult.BalanceUncertain;
            deliveryJob = outcome.Job;
            appliedBalance = outcome.AppliedBalance;
            totalAmount -= appliedBalance;
        }

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

        // Cloud API açıksa doğrudan gönder. Başarısızsa (pencere kapalı, hesap
        // yok, ağ hatası) sessizce wa.me'ye düşeriz — yayıncı elle gönderir,
        // yani en kötü ihtimalde eski davranış.
        if (settings.Payment.UseCloudApi)
        {
            switch (await TrySendViaCloudApiAsync(
                customer.Phone!, message, BuildTemplateRef(settings, ctx), "wpf-payment", ct))
            {
                case CloudSendOutcome.Sent:
                    CloseJob(deliveryJob);
                    return PaymentRequestResult.Sent;
                case CloudSendOutcome.Pending:
                    // Sonuç bilinmiyor — iş açık kalır, bir sonraki deneme aynı
                    // anahtarı yeniden kullanır (R2-01).
                    return PaymentRequestResult.SendPending;
            }
        }

        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            CloseJob(deliveryJob);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            // İş açık kalır: operatörün ikinci tıklaması aynı anahtarı bulur,
            // bakiye ikinci kez düşmez (R2-01).
            return PaymentRequestResult.LaunchFailed;
        }
    }

    /// <summary>Mesaj müşteriye ulaştı — iş kapanır. Disk hatası akışı
    /// düşürmemeli: mesaj zaten gitti; en kötü iş açık kalır ve bir sonraki
    /// deneme sonucu diskten/replay'den yeniden bulur.</summary>
    private void CloseJob(PaymentJob? job)
    {
        if (job is null) return;
        try { _jobs.Close(job.Id); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Ödeme işi kapatılamadı (job={JobId})", job.Id);
        }
    }

    private sealed record BalanceOutcome(bool Uncertain, decimal AppliedBalance, PaymentJob? Job);

    /// <summary>
    /// Lisans id'si çözülemediğinde (anahtar yok, ya da sunucuya ulaşılamıyor)
    /// verilecek karar.
    ///
    /// Bu dal apply'dan ÖNCE çalışır: bu tıklamada para adına sunucuya tek bir
    /// istek bile gitmemiştir. Yani belirsiz olan sunucuya erişim, satışın
    /// sonucu değil — K3'ün blok gerekçesi burada kendiliğinden geçerli DEĞİL.
    /// Neyi bildiğimizi diskteki iş satırı söyler; SQLite yerel, ağ istemez.
    ///
    /// Her şeyi bloklamak, tek bir VPS kesintisinde bakiyesi hiç olmayan
    /// müşteriler dahil TÜM ödeme mesajlarını durdururdu — yayın ortasında tam
    /// iş durması. Blok yalnızca gerçekten bilinmeyen durumlara saklanır.
    /// </summary>
    private BalanceOutcome OfflineOutcome(Customer customer, decimal totalAmount, string scopeKey)
    {
        // Anahtar hiç yoksa bakiye özelliği zaten kapalı — eski davranış.
        if (string.IsNullOrEmpty(_currentLicense.CurrentLicenseKey))
            return new(Uncertain: false, 0m, null);

        // Açık miras (033→034) iş: anahtarı var, sonucu bilinmiyor. Hangi
        // satışa ait olduğu da belirsiz — kesinleştirmeden devam edilemez.
        if (_jobs.GetOpenLegacies(customer.Id).Count > 0)
            return new(Uncertain: true, 0m, null);

        var job = _jobs.FindOrCreate(customer.Id, scopeKey, totalAmount);

        // created = BeginApply hiç çalışmamış (anahtar yazımı ile durum aynı
        // UPDATE'te değişir) → bu satış için hiç para hareketi yok.
        if (job.State == PaymentJobState.Created)
            return new(Uncertain: false, 0m, null);

        if (job.State is PaymentJobState.Applied or PaymentJobState.NoBalance)
        {
            // Tutar değiştiyse revizyon gerekir; revizyon geri-alma çağrısı
            // ister, o da ağsız olmaz. Eski düşümü yok sayıp tam tutar
            // yazmak müşteriye yanlış rakam göstermek olurdu.
            if (job.ProductTotal != totalAmount)
                return new(Uncertain: true, 0m, job);
            return new(Uncertain: false, job.AppliedAmount ?? 0m, job);
        }

        // apply_uncertain: replay şart, o da ağsız olmaz.
        return new(Uncertain: true, 0m, job);
    }

    /// <summary>Satışın bakiye sonucunu kesinleştirir. Dönüşte ya sonuç
    /// kesindir (Uncertain=false; AppliedBalance mesaja yazılabilir) ya da
    /// akış durmalıdır (Uncertain=true; çağıran BalanceUncertain döner).</summary>
    private async Task<BalanceOutcome> ResolveBalanceAsync(
        Customer customer, Guid wpfCustomerId, decimal totalAmount,
        string scopeKey, CancellationToken ct)
    {
        try
        {
            var licenseId = await ResolveLicenseIdAsync(ct);
            if (licenseId is null)
                return OfflineOutcome(customer, totalAmount, scopeKey);

            // 1) Miras (033→034) işleri: önce HEPSİNİ kesinleştir, sonra devret.
            //
            // R4-04: müşteri başına birden fazla açık miras anahtarı olabilir
            // (R2-04 bunun oluşabildiğini gösterdi). Her birinin sonucu ayrı
            // öğrenilmeli; biri bile belirsiz kalırsa akış durur — yoksa
            // sunucudaki fazla düşümü hiç göremeden yeni satış açmış oluruz.
            var legacies = _jobs.GetOpenLegacies(customer.Id);
            PaymentJob job;
            if (legacies.Count > 0)
            {
                var resolved = new List<PaymentJob>(legacies.Count);
                foreach (var l in legacies)
                {
                    var r = await ReplayAsync(licenseId.Value, wpfCustomerId, l, ct);
                    if (r.State == PaymentJobState.ApplyUncertain)
                        return new(true, 0m, r);
                    resolved.Add(r);
                }

                job = _jobs.FindOrCreate(customer.Id, scopeKey, totalAmount);

                // Devralma en fazla BİR miras işine uygulanabilir: taze hedef
                // tek bir anahtar taşır. En yenisi seçilir — 033 akışında bu
                // satışa en yakın olan odur; kalanlar artık düşüm sayılır.
                var adopted = job.State == PaymentJobState.Created && job.ApplyKey is null
                    ? resolved[0]
                    : null;
                if (adopted is not null)
                    _jobs.AdoptLegacyResult(job.Id, adopted.Id);

                foreach (var l in resolved)
                {
                    if (adopted is not null && l.Id == adopted.Id) continue;

                    // Devralınmayan miras düşümü artık: geri al, işi kapat.
                    // Geri alma kesinleşmezse iş AÇIK kalır ve akış durur —
                    // kapatmak, bilinmeyen bir düşümü sessizce gömmek olurdu.
                    if (l.AppliedAmount is > 0m
                        && !await TryReverseAsync(licenseId.Value, l.ApplyKey!.Value, ct))
                    {
                        _jobs.MarkUncertain(l.Id);
                        return new(true, 0m, l);
                    }
                    _jobs.Close(l.Id);
                }

                job = _jobs.Get(job.Id)!;
            }
            else
            {
                job = _jobs.FindOrCreate(customer.Id, scopeKey, totalAmount);
            }

            // 2) Belirsiz iş: bir kez replay (K3) — hâlâ belirsizse blokla.
            if (job.State == PaymentJobState.ApplyUncertain)
            {
                job = await ReplayAsync(licenseId.Value, wpfCustomerId, job, ct);
                if (job.State == PaymentJobState.ApplyUncertain)
                    return new(true, 0m, job);
            }

            // 3) Revizyon (K2): kapsam aynı, tutar değişti — eski düşümü geri
            //    al, yeni toplam + YENİ anahtarla taze uygula. created işte de
            //    çalışır (geri alınacak şey yoktur, sadece tutar güncellenir).
            if (job.ProductTotal != totalAmount)
            {
                if (job.AppliedAmount is > 0m
                    && !await TryReverseAsync(licenseId.Value, job.ApplyKey!.Value, ct))
                {
                    _jobs.MarkUncertain(job.Id);
                    return new(true, 0m, job);
                }
                // false = eşzamanlı revizyon kazandı; onun anahtarıyla devam.
                _jobs.BeginRevision(job.Id, totalAmount, Guid.NewGuid(), job.Revision);
                job = _jobs.Get(job.Id)!;
                return await SettleAsync(licenseId.Value, wpfCustomerId, job, ct);
            }

            // 4) Tekrar paylaşım: sonuç kesin, tutar aynı — finansal çağrı YOK.
            if (job.State is PaymentJobState.Applied or PaymentJobState.NoBalance)
                return new(false, job.AppliedAmount ?? 0m, job);

            // 5) Taze iş: önizleme (bakiye yoksa anahtar hiç yazılmaz) →
            //    anahtar diske → apply.
            decimal previewBalance;
            try
            {
                var preview = await _api.GetBalancePreviewAsync(licenseId.Value, wpfCustomerId, ct);
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
                _jobs.MarkNoBalance(job.Id);
                return new(false, 0m, _jobs.Get(job.Id));
            }

            if (!_jobs.BeginApply(job.Id, Guid.NewGuid()))
            {
                // Yarışı kaybettik (R2-04/A9): kazananın anahtarı diskte.
                job = _jobs.Get(job.Id)!;
                if (job.State is PaymentJobState.Applied or PaymentJobState.NoBalance)
                    return new(false, job.AppliedAmount ?? 0m, job);
            }
            job = _jobs.Get(job.Id)!;
            return await SettleAsync(licenseId.Value, wpfCustomerId, job, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogError(ex,
                "Bakiye akışında beklenmeyen hata — mesaj engellendi (customer={CustomerId})",
                customer.Id);
            return new(true, 0m, null);
        }
    }

    /// <summary>Diskteki anahtarla apply — bir deneme + bir anında tekrar (K3).</summary>
    private async Task<BalanceOutcome> SettleAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
    {
        job = await ReplayAsync(licenseId, wpfCustomerId, job, ct);
        if (job.State == PaymentJobState.ApplyUncertain)
            job = await ReplayAsync(licenseId, wpfCustomerId, job, ct);
        return job.State == PaymentJobState.ApplyUncertain
            ? new(true, 0m, job)
            : new(false, job.AppliedAmount ?? 0m, job);
    }

    /// <summary>İşin diskteki anahtarıyla apply'ı (yeniden) dener, sonucu işe
    /// yazar. Amount=ProductTotal gönderilir — sunucu bakiyeye/toplama kırpar;
    /// replay'de birebir aynı gövde gittiği için A11 içerik kontrolünden geçer.</summary>
    private async Task<PaymentJob> ReplayAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
    {
        if (job.ApplyKey is null)
            throw new InvalidOperationException($"Replay anahtarsız işte çağrıldı (job={job.Id})");
        try
        {
            var apply = await _api.ApplyBalanceAsync(
                licenseId,
                new CustomerBalanceApplyRequest(
                    wpfCustomerId, job.ProductTotal, job.ProductTotal, job.ApplyKey),
                ct);
            _jobs.MarkApplied(job.Id, apply.AppliedAmount);
        }
        catch (ValidationException ex) when (ex.Code is "no-balance" or "nothing-to-apply")
        {
            // Replay sunucuda balance kontrolünden ÖNCE çalışır: bu 409,
            // anahtarın hiç uygulanmadığının ve bakiye olmadığının kesin kanıtı.
            _jobs.MarkNoBalance(job.Id);
        }
        catch (ValidationException ex) when (ex.Code == "content-conflict")
        {
            // Olmamalı: anahtar sunucuda FARKLI içerikle kayıtlı (A11). Kesin
            // cevap sayamayız — belirsiz bırak, yüksek sesle logla.
            _log?.LogError(
                "Bakiye anahtarı sunucuda farklı içerikle kayıtlı (job={JobId}, key={Key})",
                job.Id, job.ApplyKey);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Bakiye apply belirsiz kaldı (job={JobId})", job.Id);
        }
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

    /// <summary>Cloud API denemesinin üç ayrı sonucu. bool yetmiyor: "gönderildi"
    /// ile "sonucu bilmiyoruz" ikisi de wa.me'yi açtırmıyor ama operatöre
    /// söylenecek şey taban tabana zıt.</summary>
    private enum CloudSendOutcome
    {
        /// <summary>Sunucu gönderimi onayladı.</summary>
        Sent,

        /// <summary>Çağıran wa.me'ye düşmeli (hesap bağlı değil, pencere kapalı,
        /// lisans çözülemedi, ağ hatası).</summary>
        FallBack,

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
                return CloudSendOutcome.FallBack;
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
            return CloudSendOutcome.FallBack;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "WhatsApp Cloud API gönderimi hata verdi — wa.me'ye düşülüyor (key={Key})", idempotencyKey);
            return CloudSendOutcome.FallBack;
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
    /// </summary>
    internal static (decimal Total, decimal? Fee, string Note) ComputeShipping(
        Customer customer, decimal productTotal, AppSettings settings)
    {
        if (customer.RecipientPaysActive)
        {
            return (productTotal, null, "Kargo: alıcı ödemeli");
        }

        var shipping = settings.Shipping;
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
