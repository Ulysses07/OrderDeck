using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;

namespace OrderDeck.App.ViewModels;

// ─── Satış akışı (spec 2026-10-13 §5.4-6, §5.7 "Akış"; W2-03) ─────────────────────────────────────────────
//
// Satış akışı turu (SalesChangesPullService, arka plan iş parçacığı) diğer bilgisayarların devredilmiş yayınlarını,
// komut sonuçlarını ve kargo dosyalarını yerele yazdıktan sonra SalesChanged yayar. Kabuk dispatcher'a taşır ve
// ekrandaki kopyaları tazeler: kuyruktaki etiketlerin kopyası (RefreshQueueRows), Basılanlar sekmesi, hero sayaçları.
// Durum satırının altında, yalnız mod On'da, satış akışının satırı görünür (SalesFeedStatusFormatter); mod Off'ta
// (bugün her lisans) metin boştur ve satır gizlidir — tek istisna mod On açılışının beklemesi (W2-10). İlk yetişme
// tamamlanınca (FirstCatchUpCompleted) kendi etiketlerinde sunucunun değeri alındıysa gönderim şeridine bir kez not düşer.
// W2-11: komut ucu mode-off döndüğünde (ModeOffStarted) "Senkron kapatıldı; ekran yenileniyor." şeridi açılır, Off turu
// sonuçlanınca (ModeOffFinished) kalkar; turun silinen uzak satırları ve yerele dönen yayınları SalesChanged tazeler.
public sealed partial class MainShellViewModel
{
    private readonly SalesChangesPullService? _salesPull;

    /// <summary>Satış akışının durum satırı (§5.7); boş = gizli (mod Off ya da lisans yok).</summary>
    [ObservableProperty] private string _salesSyncStatusText = "";
    [ObservableProperty] private bool _isSalesSyncHealthy = true;
    [ObservableProperty] private string _salesSyncStatusTooltip = "";

    /// <summary>W2-11 (§5.3 mode-off): komut ucu modun kapandığını söyledi — "Senkron kapatıldı; ekran yenileniyor."
    /// Off turu sonuçlanınca kendiliğinden kalkar (açılır pencere yok); null = gizli.</summary>
    [ObservableProperty] private string? _salesModeOffBanner;

    /// <summary>Komut ucunun mode-off'u (arka plandan ya da komutu gönderenin iş parçacığından): şerit açılır.</summary>
    private void OnSalesModeOffStarted(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess()) SalesModeOffBanner = SalesCommandMessages.ModeOff;
        else _dispatcher.BeginInvoke(() => SalesModeOffBanner = SalesCommandMessages.ModeOff);
    }

    /// <summary>Off turu sonuçlandı (turun iş parçacığından): şerit kalkar; ekranlar akışın değişiklik olayıyla tazelendi.</summary>
    private void OnSalesModeOffFinished(object? sender, EventArgs e)
    {
        void Apply()
        {
            SalesModeOffBanner = null;
            RefreshSalesSyncStatus(_attention?.Invoke().SkippedSalesFeedItems ?? 0);
        }

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.BeginInvoke(Apply);
    }

    /// <summary>Arka plan iş parçacığından gelir: dispatcher'a taşınır, akış turunu bekletmez.</summary>
    private void OnSalesChanged(object? sender, SalesChangedEventArgs e)
    {
        if (_dispatcher.CheckAccess()) ApplySalesChanged(e);
        else _dispatcher.BeginInvoke(() => ApplySalesChanged(e));
    }

    /// <summary>Akıştan inen değişiklikleri ekrana yansıtır (UI iş parçacığında). Hatası kabuğu düşürmez.</summary>
    internal void ApplySalesChanged(SalesChangedEventArgs e)
    {
        try
        {
            RefreshQueueRows(e.LabelIds);
            RefreshPrintedLabels();
            RefreshPrintedEditAvailability();   // W2-05: inen oturum sunucuya geçmiş olabilir (§5.7 düğme durumu)
            RefreshHeroStats();
            RefreshSalesSyncStatus(_attention?.Invoke().SkippedSalesFeedItems ?? 0);
            RefreshRemoteSessions();            // W2-09: kendi yayınının devri ya da force-end'i bandı açar
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış akışından inen değişiklikler ekrana yansıtılamadı; sonraki tazelemede yeniden");
        }
    }

    /// <summary>Mod On'un ilk yetişmesi tamamlandı (W2-10, §8.4-4; arka plan iş parçacığından): toplamlar türetildi —
    /// sayaçlar ve satır tazelenir; kendi etiketlerinde sunucunun değeri alındıysa şeride bir kez not düşer (açılır
    /// pencere yok, "Tamam" ile kapanır).</summary>
    private void OnFirstCatchUpCompleted(object? sender, SalesFirstCatchUp e)
    {
        if (_dispatcher.CheckAccess()) ShowFirstCatchUp(e);
        else _dispatcher.BeginInvoke(() => ShowFirstCatchUp(e));
    }

    /// <summary>İlk yetişmenin sonucu ekrana (UI iş parçacığında). Hatası kabuğu düşürmez.</summary>
    internal void ShowFirstCatchUp(SalesFirstCatchUp e)
    {
        try
        {
            if (e.CatchUpOverwrites > 0)
                ShowSalesNotice([], [SalesCommandMessages.CatchUpOverwritten(e.CatchUpOverwrites)]);
            RefreshHeroStats();
            RefreshSalesSyncStatus(_attention?.Invoke().SkippedSalesFeedItems ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış senkronunun ilk yetişmesi ekrana yansıtılamadı");
        }
    }

    /// <summary>Satış akışı satırı: lisans varken ve akış mod On bildiriyorsa görünür. W2-09: önde kendi yayınının
    /// devri — "Yayın sunucuya devrediliyor…" (devri bekleyen yayın varken; sorgu yalnız mod On'da) ya da "Yayın X
    /// devredilemedi: N etiket gönderilemiyor" (<see cref="SalesChangesPullService.StuckHandovers"/>).</summary>
    private void RefreshSalesSyncStatus(int skipped)
    {
        SyncStatusFormatter.Status? line = null;
        if (SyncLicenseKey() is not null && _salesPull?.Status is { ModeOn: true } feed)
            line = SalesFeedStatusFormatter.Format(feed, skipped, _sessions.HasPendingHandover(), _salesPull.StuckHandovers);
        else if (SyncLicenseKey() is not null && _salesPull?.Status is { OpeningPending: true } opening)
            line = SalesFeedStatusFormatter.Format(opening, skipped);   // W2-10: "Senkron açılıyor; bekleyen gönderim var"
        if (line is not { } status)
        {
            SalesSyncStatusText = "";
            IsSalesSyncHealthy = true;
            SalesSyncStatusTooltip = "";
            return;
        }
        SalesSyncStatusText = status.Text;
        IsSalesSyncHealthy = status.Healthy;
        SalesSyncStatusTooltip = SalesFeedStatusFormatter.Tooltip(status, skipped);
    }
}
