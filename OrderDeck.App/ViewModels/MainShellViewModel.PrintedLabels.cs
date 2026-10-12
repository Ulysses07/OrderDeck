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
        if (value) RefreshPrintedLabels();
    }

    /// <summary>Kuyrukta seçili satır var mı — Kuyruk sekmesindeki "Fiyatı düzelt".</summary>
    public bool HasQueueSelection => SelectedQueueItems.Count > 0;

    /// <summary>Açık fiyat düzeltme çekmecesinin içeriği; testler akışı bunun üzerinden sürer.</summary>
    public PriceCorrectionViewModel? ActivePriceCorrection { get; private set; }

    /// <summary>Rapordan açılmış yayın (K6); null = aktif yayın gösterilir.</summary>
    private PrintedLabelsRequest? _otherSession;

    /// <summary>Sekmeyi yeniden kurar. Sekme açık değilse rozetsiz (tek sorgu) — her "Yazdır"dan sonra
    /// çağrılıyor, kişi başına ödeme işi taraması sekme açılınca yapılır.</summary>
    private void RefreshPrintedLabels()
    {
        var sessionId = _otherSession?.SessionId ?? _sessions.GetActive()?.Id;
        PrintedLabels.IsOtherSession = _otherSession is not null;
        PrintedLabels.OtherSessionTitle = _otherSession?.Title ?? "";
        PrintedLabels.Load(sessionId, withPaymentStatus: IsPrintedTabActive);
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
        return OpenPriceCorrectionAsync(session.Id, ids, PriceCorrectionMode.Queue, DateTime.Now);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task FixPrintedPriceAsync()
    {
        var sessionId = PrintedLabels.SessionId;
        var ids = PrintedLabels.SelectedIds;
        if (sessionId is null || ids.Count == 0) return Task.CompletedTask;
        return OpenPriceCorrectionAsync(sessionId, ids, PriceCorrectionMode.Printed, PrintedStreamDate);
    }

    private async Task OpenPriceCorrectionAsync(
        string sessionId, IReadOnlyList<string> ids, PriceCorrectionMode mode, DateTime streamDate)
    {
        if (_drawers is null) return;   // yalnız testte; bkz. alanın notu

        var vm = new PriceCorrectionViewModel(
            _labels, _paymentFollowUp, sessionId, ids, mode, streamDate,
            inFlight: () => _printInFlightIds,
            applied: ApplyPriceCorrection,
            reprint: ReprintAsync,
            cloudApiEnabled: SafeCloudApiEnabled());
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

    private bool SafeCloudApiEnabled()
    {
        try { return _settingsStore.Load().Payment.UseCloudApi; }
        catch { return false; }
    }

    /// <summary>Düzeltme yazıldı: kuyruktaki etkilenen satırlar YERİNDE güncel etiketle değiştirilir
    /// (seçim ve sıra korunur). Yapılmasaydı kâğıda eski fiyat basılır, ciro yeni fiyatla yazılırdı —
    /// baskı veritabanını değil satırın kopyasını basıyor.</summary>
    internal void ApplyPriceCorrection(PriceCorrectionResult result)
    {
        var ids = result.Corrected.Select(c => c.Before.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count > 0)
        {
            var fresh = _labelRepo.GetByIds(ids.ToList()).ToDictionary(l => l.Id, StringComparer.Ordinal);
            foreach (var vm in PrintQueue)
                if (fresh.TryGetValue(vm.Id, out var updated)) vm.Replace(updated);
        }
        RefreshHeroStats();
        RefreshPrintedLabels();
    }

    // ─── Yeniden bas (K3/K5) ────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ReprintPrintedAsync()
    {
        var ids = PrintedLabels.SelectedIds;
        if (ids.Count == 0) return;
        var error = await ReprintAsync(ids);
        PrintedLabels.Message = error ?? $"{ids.Count} etiket yeniden basıldı.";
    }

    /// <summary>
    /// Etiketlerin GÜNCEL hâlini yeniden basar — yazma YOK: ciro ikinci kez artmaz, ilk PrintedAt ve
    /// basış grubu bozulmaz (<see cref="LabelService.PrepareReprint"/>). Fiyatı düzeltilmiş etiketin
    /// kâğıdında "DÜZELTME" işareti. Kuyruk baskısıyla aynı yazıcı kapısından geçer
    /// (<see cref="RunPrinterJobAsync"/>). Hata metni döner (pop-up yok), başarıda null.
    /// </summary>
    internal async Task<string?> ReprintAsync(IReadOnlyList<string> ids)
    {
        if (_printTask is not null) return "Başka bir baskı sürüyor; bitince tekrar dene.";
        var reprint = _labels.PrepareReprint(ids);
        if (reprint.Labels.Count == 0) return "Yeniden basılacak etiket yok (iptal edilmiş olabilir).";

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
        });
        if (!ran && error is null) error = "Başka bir baskı sürüyor; bitince tekrar dene.";
        return error;
    }

    [RelayCommand]
    private async Task OpenCustomerDetailFromPrintedAsync(PrintedLabelRowViewModel? row)
    {
        if (row is null) return;
        await ShowCustomerDetailAsync(row.CustomerId);
        RefreshPrintedLabels();   // müşteri detayından iptal/geri alma yapılmış olabilir
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

        var withBackups = CancelLabels(ids, reason);
        if (withBackups.Count > 0)
        {
            await _drawers.ShowAsync($"İptal edilen etiketlerin yedekleri ({withBackups.Sum(g => g.Backups.Count)})",
                d => Views.Drawers.BackupTransferDrawer.Create(d, _labels, withBackups));
            RefreshPrintedLabels();
        }
    }

    /// <summary>Toplu iptalin çekirdeği: iptal + liste tazeleme; geçici yedeği olan etiketleri (yedek
    /// aktarımı için) döner.</summary>
    internal IReadOnlyList<(Label Parent, IReadOnlyList<Label> Backups)> CancelLabels(
        IReadOnlyList<string> ids, string reason)
    {
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
        PrintedLabels.Message = $"{ids.Count} etiket iptal edildi.";
        return withBackups;
    }
}
