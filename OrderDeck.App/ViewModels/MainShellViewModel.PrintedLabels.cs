using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.ViewModels;

// ─── "Basılanlar" sekmesi ve toplu fiyat düzeltme (sahip kararları K1–K7, 2026-10-12) ─────────────
//
// Sorun (sahibin sözleriyle): "Bir ürün çıkardığımda ve bu ürünün fiyatını yanlış girip 20–30 etiket
// çıkardığım zaman bunu düzeltmek aşırı zor, hele yayın anında daha da zor." Fiyat etiket yazılırken her
// etikete kopyalanıyor ve sonradan değiştiren bir akış yoktu: her müşterinin kartına girip iptal edip
// yeniden yazmak gerekiyordu. Burada: yazdırma kuyruğu panelinde ikinci sekme ("Basılanlar"), seçimle
// fiyat düzeltme (kuyrukta da), yeniden baskı ve toplu iptal. Çekmeceler ve yazıcı kabuğun; sekmenin
// verisi ve seçimi PrintedLabelsViewModel'de.
public sealed partial class MainShellViewModel
{
    private readonly CorrectedPaymentRequests? _paymentFollowUp;
    private readonly PrintedLabelsNavigator? _printedNavigator;

    /// <summary>"Basılanlar" sekmesinin verisi ve seçimi.</summary>
    public PrintedLabelsViewModel PrintedLabels { get; }

    /// <summary>Sağ paneldeki sekme: false = Kuyruk, true = Basılanlar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueTabActive))]
    [NotifyPropertyChangedFor(nameof(ShowQueueFixPrice))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFromQueueCommand))]
    private bool _isPrintedTabActive;

    /// <summary>Kuyruk sekmesindeki "Fiyatı düzelt" (K4): sekme açık ve seçim var.</summary>
    public bool ShowQueueFixPrice => !IsPrintedTabActive && HasQueueSelection;

    /// <summary><see cref="IsPrintedTabActive"/>'in tersi — Kuyruk sekme düğmesinin iki yönlü bağı.</summary>
    public bool IsQueueTabActive
    {
        get => !IsPrintedTabActive;
        set { if (value) IsPrintedTabActive = false; }
    }

    partial void OnIsPrintedTabActiveChanged(bool value)
    {
        if (!value) return;
        RefreshPrintedLabels();
        PrintedLabels.MarkOutcomesRead();
    }

    /// <summary>Kuyrukta seçili satır var mı — Kuyruk sekmesindeki "Fiyatı düzelt".</summary>
    public bool HasQueueSelection => SelectedQueueItems.Count > 0;

    /// <summary>Açık fiyat düzeltme çekmecesinin içeriği; testler akışı bunun üzerinden sürer.</summary>
    public PriceCorrectionViewModel? ActivePriceCorrection { get; private set; }

    /// <summary>Rapordan açılmış yayın (K6); null = aktif yayın gösterilir.</summary>
    private PrintedLabelsRequest? _otherSession;

    /// <summary>Sekmeyi tazeler — her "Yazdır"dan sonra da çağrılıyor. Sekme kapalıyken liste KURULMAZ,
    /// yalnız başlıktaki sayı güncellenir (sayım sorgusu); liste ve ödeme rozetleri sekme açılınca
    /// (<see cref="OnIsPrintedTabActiveChanged"/>). Açıkken liste yerinde güncellenir
    /// (<see cref="PrintedLabelsViewModel.Load"/>): gruplu, sanallaştırmasız listeyi her baskıda baştan
    /// kurmak UI'ı yüzlerce etikette saniyeye yakın kilitliyordu.</summary>
    private void RefreshPrintedLabels()
    {
        var sessionId = _otherSession?.SessionId ?? _sessions.GetActive()?.Id;
        PrintedLabels.IsOtherSession = _otherSession is not null;
        PrintedLabels.OtherSessionTitle = _otherSession?.Title ?? "";
        if (IsPrintedTabActive) PrintedLabels.Load(sessionId);
        else PrintedLabels.UpdateCount(sessionId);
    }

    /// <summary>Yayın başladı/bitti ya da operatör "bu yayına dön" dedi: sekme aktif yayına döner.</summary>
    [RelayCommand]
    private void ShowActiveSessionInPrintedTab()
    {
        _otherSession = null;
        RefreshPrintedLabels();
    }

    /// <summary>K6: rapordaki "Basılan etiketler" — sayfalar kapanır, sekme o yayına açılır (çekmeceler
    /// sayfanın arkasında kalırdı; bkz. <see cref="PrintedLabelsNavigator"/>).</summary>
    private void OnPrintedLabelsRequested(PrintedLabelsRequest request)
    {
        while (_pages?.Back() == true) { }
        _otherSession = string.Equals(request.SessionId, _sessions.GetActive()?.Id, StringComparison.Ordinal)
            ? null
            : request;
        if (IsPrintedTabActive) RefreshPrintedLabels();
        else IsPrintedTabActive = true;
    }

    private DateTime PrintedStreamDate => _otherSession?.StreamDate ?? DateTime.Now;

    // ─── Fiyatı düzelt (K4 kuyruk, K5 basılanlar) ──────────────────────────

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task FixQueuePriceAsync()
    {
        var session = _sessions.GetActive();
        if (session is null || SelectedQueueItems.Count == 0) return Task.CompletedTask;
        // Kuyruk ekleme sırasıyla; seçim sırası değil.
        var ids = PrintQueue.Where(SelectedQueueItems.Contains).Select(vm => vm.Id).ToList();
        return OpenPriceCorrectionAsync(session.Id, ids, DateTime.Now);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task FixPrintedPriceAsync()
    {
        var sessionId = PrintedLabels.SessionId;
        var ids = PrintedLabels.SelectedIds;
        if (sessionId is null || ids.Count == 0) return Task.CompletedTask;
        return OpenPriceCorrectionAsync(sessionId, ids, PrintedStreamDate);
    }

    private async Task OpenPriceCorrectionAsync(
        string sessionId, IReadOnlyList<string> ids, DateTime streamDate)
    {
        if (_drawers is null) return;   // yalnız testte; bkz. alanın notu

        var vm = WireCorrection(new PriceCorrectionViewModel(
            _labels, _paymentFollowUp, sessionId, ids, streamDate,
            inFlight: () => _printInFlightIds,
            applied: ApplyPriceCorrection,
            reprint: ReprintAsync,
            cloudApiEnabled: SafeCloudApiEnabled()));
        ActivePriceCorrection = vm;
        try
        {
            await _drawers.ShowAsync("Fiyatı Düzelt", d =>
            {
                vm.Close = () => d.Close(true);
                return Views.Drawers.PriceCorrectionDrawer.Create(vm);
            });
        }
        finally
        {
            ActivePriceCorrection = null;
            RefreshPrintedLabels();
        }
    }

    /// <summary>Düzeltmenin arka plan işleri (yeniden baskı, toplu gönderim) çekmeceye ya da FİYAT kutusunun
    /// satır içi sonucuna bağlı DEĞİL: çekmece/sonuç iş sürerken de kapatılabilir (askıda kalan yazıcı ya da
    /// yavaş ağ yayında kod/fiyat kutusunu ve kuyruğu örtmesin), iş arka planda sürer. Sonucu (baskı hatası,
    /// "elle bakılmalı" adları ve sebepleri) iş BİTİNCE "Basılanlar"daki kalıcı sonuç şeridine EKLENİR. Uçucu
    /// ileti satırına değil: seçim, başka bir ileti, iptal ya da tazeleme onu silerdi; şerit yalnız operatör
    /// kapatınca gider.</summary>
    private PriceCorrectionViewModel WireCorrection(PriceCorrectionViewModel vm)
    {
        vm.WorkFinished = (report, needsAction) =>
        {
            RefreshPrintedLabels();
            AddPrintedOutcome(report, needsAction);
        };
        return vm;
    }

    /// <summary>"Basılanlar"daki kalıcı sonuç şeridine bir kayıt ekler. Sekme açıksa şerit görünüyor; kapalıysa
    /// başlıkta okunmamış işareti kalır (F5). <paramref name="needsAction"/>: kayıt operatörün yapacağı işi taşıyor
    /// — şeridin sınırıyla düşmez (<see cref="PrintedLabelsViewModel.AddOutcome"/>).</summary>
    private void AddPrintedOutcome(string outcome, bool needsAction)
    {
        PrintedLabels.AddOutcome(outcome, needsAction);
        if (IsPrintedTabActive) PrintedLabels.MarkOutcomesRead();
    }

    private bool SafeCloudApiEnabled()
    {
        try { return _settingsStore.Load().Payment.UseCloudApi; }
        catch { return false; }
    }

    /// <summary>Düzeltme yazıldı: kuyruktaki etkilenen satırlar YERİNDE güncel etiketle değiştirilir
    /// (seçim ve sıra korunur). Yapılmasaydı kuyrukta eski fiyat görünürdü; kâğıt ise baskıdan hemen önce
    /// veritabanından yeniden okunur (§5.6). Etkin kod düzeltilen kodsa FİYAT kutusu kodun yeni
    /// fiyatını gösterir (F4, <see cref="ShowCorrectedCodePrice"/>): sonraki yorum eski fiyatla yazılmasın.</summary>
    internal void ApplyPriceCorrection(PriceCorrectionResult result)
    {
        RefreshQueueRows(result.Corrected.Select(c => c.Before.Id));
        ShowCorrectedCodePrice(result);
        RefreshHeroStats();
        RefreshPrintedLabels();
    }

    /// <summary>Kuyruktaki verilen etiketlerin ekrandaki kopyasını veritabanındaki güncel hâliyle YERİNDE
    /// değiştirir (seçim ve sıra korunur). Kopya ekranda görünen hâldir: veritabanını değiştiren her akış
    /// (fiyat düzeltme, yedek onayı) kuyruktaki kopyayı da güncellemeli. Baskı ayrıca kendisi yeniden
    /// okur (§5.6), bayat kopya kâğıda gitmez.</summary>
    private void RefreshQueueRows(IEnumerable<string> labelIds)
    {
        var ids = labelIds.ToHashSet(StringComparer.Ordinal);
        var inQueue = PrintQueue.Where(vm => ids.Contains(vm.Id)).Select(vm => vm.Id).ToList();
        if (inQueue.Count == 0) return;
        var fresh = _labelRepo.GetByIds(inQueue).ToDictionary(l => l.Id, StringComparer.Ordinal);
        foreach (var vm in PrintQueue)
            if (fresh.TryGetValue(vm.Id, out var updated)) vm.Replace(updated);
    }

    // ─── Yeniden bas (K3/K5) ────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ReprintPrintedAsync()
    {
        var ids = PrintedLabels.SelectedIds;
        if (ids.Count == 0) return;
        // Sayı BASILANLARIN sayısı, seçilenlerin değil: arada iptal edilmiş satır basılmaz
        // (PrepareReprint süzer) — "3 basıldı" denip 2 kâğıt çıkıyordu.
        var (printed, error) = await ReprintCoreAsync(ids);
        PrintedLabels.Message = error ?? $"{printed} etiket yeniden basıldı.";
    }

    /// <summary>
    /// Etiketlerin GÜNCEL hâlini yeniden basar — yazma YOK: ciro ikinci kez artmaz, ilk PrintedAt ve
    /// basış grubu bozulmaz (<see cref="LabelService.PrepareReprint"/>). Fiyatı düzeltilmiş etiketin
    /// kâğıdında "DÜZELTME" işareti. Kuyruk baskısıyla aynı yazıcı kapısından geçer
    /// (<see cref="RunPrinterJobAsync"/>). Hata metni döner (pop-up yok), başarıda null.
    /// </summary>
    internal async Task<string?> ReprintAsync(IReadOnlyList<string> ids) => (await ReprintCoreAsync(ids)).Error;

    /// <summary><see cref="ReprintAsync"/> + basılan etiket sayısı (yalnız basılmış, iptal edilmemiş olanlar).</summary>
    private async Task<(int Printed, string? Error)> ReprintCoreAsync(IReadOnlyList<string> ids)
    {
        if (_printTask is not null) return (0, "Başka bir baskı sürüyor; bitince tekrar dene.");
        var reprint = _labels.PrepareReprint(ids);
        if (reprint.Labels.Count == 0) return (0, "Yeniden basılacak etiket yok (iptal edilmiş olabilir).");

        string? error = null;
        var ran = await RunPrinterJobAsync(reprint.Labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal), async () =>
        {
            var recipientPays = ComputeRecipientPaysLabelIds(reprint.Labels);
            try
            {
                await Task.Run(() => _printer.Print(reprint.Labels, recipientPays, reprint.CorrectionIds));
                return true;
            }
            catch (Exception ex)
            {
                error = $"Yeniden basılamadı: {ex.Message}. Yazıcıyı kontrol edip tekrar dene.";
                return false;
            }
        }, isQueue: false);
        if (!ran && error is null) error = "Başka bir baskı sürüyor; bitince tekrar dene.";
        return (error is null ? reprint.Labels.Count : 0, error);
    }

    /// <summary>Kapanınca liste tazelenir (<see cref="ShowCustomerDetailAsync"/> — her yoldan).</summary>
    [RelayCommand]
    private async Task OpenCustomerDetailFromPrintedAsync(PrintedLabelRowViewModel? row)
    {
        if (row is null) return;
        await ShowCustomerDetailAsync(row.CustomerId);
    }

    // ─── Toplu iptal (K3) ───────────────────────────────────────────────────

    /// <summary>
    /// Seçili basılmış etiketleri iptal eder: sebep BİR kez sorulur (aynı sebep çekmecesi), müşteri
    /// detayındaki iptalle AYNI sonuç (<see cref="LabelService.Cancel"/> — ciro geri alınır, stok
    /// sunucuda iade edilir). Yedeği olan etiketlerin yedekleri TEK çekmecede listelenir (25 ayrı çekmece
    /// değil).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task CancelPrintedAsync()
    {
        if (_drawers is null) return;   // yalnız testte; bkz. alanın notu
        var ids = PrintedLabels.SelectedIds;
        if (ids.Count == 0) return;

        Views.Drawers.CancelLabelDrawer? view = null;
        var ok = await _drawers.ShowAsync($"Etiket İptali ({ids.Count})",
            d => view = Views.Drawers.CancelLabelDrawer.Create(d));
        if (!ok || view?.SelectedReasonCode is not { Length: > 0 } reason) return;

        await CancelAndTransferBackupsAsync(ids, reason);
    }

    /// <summary>İptal + yedek çekmecesi. Çekmecede onaylanan yedeğin fiyatı değişmiş olabilir
    /// (<see cref="LabelService.ConfirmBackup"/> yalnız veritabanını yazar): yedek hâlâ kuyruktaysa
    /// ekrandaki kopyası ONAYDA güncellenir (<see cref="OnBackupConfirmed"/>) — yoksa kâğıda eski fiyat
    /// basılır, ciroya yeni fiyat yazılırdı.</summary>
    internal async Task CancelAndTransferBackupsAsync(IReadOnlyList<string> ids, string reason)
    {
        var withBackups = CancelLabels(ids, reason);
        if (withBackups.Count == 0 || _drawers is null) return;
        await _drawers.ShowAsync($"İptal edilen etiketlerin yedekleri ({withBackups.Sum(g => g.Backups.Count)})",
            d => Views.Drawers.BackupTransferDrawer.Create(d, _labels, withBackups, onConfirmed: OnBackupConfirmed));
        RefreshQueueRows(withBackups.SelectMany(g => g.Backups).Select(b => b.Id));
        RefreshPrintedLabels();
        RefreshHeroStats();
        AfterLabelsCancelled();   // onaylanan yedeğin fiyatı değişmiş olabilir
    }

    /// <summary>Yedek çekmecesinde (toplu iptal ya da müşteri detayı) bir yedek onaylandı: kuyruktaysa
    /// ekrandaki kopyası HEMEN güncellenir — çekmece kapanmayı beklemez. Çekmece sağ panelin üstünü örtüyor
    /// ama alttaki yazdır yuvası açık: onaydan sonra çekmece açıkken "Yazdır"a basılırsa kâğıda eski fiyat
    /// basılır, ciroya (veritabanından) yeni fiyat yazılırdı.</summary>
    internal void OnBackupConfirmed(string backupId) => RefreshQueueRows(new[] { backupId });

    /// <summary>Toplu iptalin çekirdeği: iptal + liste tazeleme; geçici yedeği olan etiketleri (yedek
    /// aktarımı için) döner.</summary>
    internal IReadOnlyList<(Label Parent, IReadOnlyList<Label> Backups)> CancelLabels(
        IReadOnlyList<string> ids, string reason)
    {
        // R7-04 Soru B (kuyruk iptaliyle aynı): uçuştaki işle (kuyruk baskısı ya da yeniden baskı) kesişen
        // etiketler. İptal yazıcıyı durdurmaz — o kâğıtlar ("DÜZELTME" işaretli yeniden baskı dahil) yine
        // çıkar; operatöre imha etmesi söylenir (satır içi, pop-up yok).
        var inFlight = _printInFlightIds is { } flying ? ids.Count(flying.Contains) : 0;
        _labels.Cancel(ids, reason);
        var withBackups = new List<(Label Parent, IReadOnlyList<Label> Backups)>();
        foreach (var id in ids)
        {
            var backups = _labels.GetBackups(id);
            if (backups.Count == 0) continue;
            if (_labelRepo.GetById(id) is { } parent) withBackups.Add((parent, backups));
        }
        RefreshPrintedLabels();
        RefreshHeroStats();
        AfterLabelsCancelled();
        PrintedLabels.Message = $"{ids.Count} etiket iptal edildi." + (inFlight > 0
            ? $" {inFlight} tanesi devam eden baskı işinde: iptal yazıcıyı durdurmaz — bu etiketlerden çıkan " +
              "ya da sonradan çıkacak kâğıtları imha et."
            : "");
        return withBackups;
    }
}
