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
// (bugün her lisans) metin boştur ve satır gizlidir.
public sealed partial class MainShellViewModel
{
    private readonly SalesChangesPullService? _salesPull;

    /// <summary>Satış akışının durum satırı (§5.7); boş = gizli (mod Off ya da lisans yok).</summary>
    [ObservableProperty] private string _salesSyncStatusText = "";
    [ObservableProperty] private bool _isSalesSyncHealthy = true;
    [ObservableProperty] private string _salesSyncStatusTooltip = "";

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

    /// <summary>Satış akışı satırı: lisans varken ve akış mod On bildiriyorsa görünür.</summary>
    private void RefreshSalesSyncStatus(int skipped)
    {
        var line = SyncLicenseKey() is null ? null : SalesFeedStatusFormatter.Format(_salesPull?.Status, skipped);
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
