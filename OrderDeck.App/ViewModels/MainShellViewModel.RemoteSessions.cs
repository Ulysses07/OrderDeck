using System;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.ViewModels;

// ─── Uzak yayın çipleri, "Yayını bitir…" ve kendi yayınının bandı (spec 2026-10-13 §5.7; W2-09) ─────────────
//
// Çipler üst çubuğun altındaki şeritte, onay ve sonuç şeritleri ile band kabuk şeritlerinde (ShellBanners) durur;
// açılır pencere yok. Kaynak RemoteSessionsViewModel: yerel ayna (RemoteLiveSession) ve bu bilgisayarın açık yayını.
// Tazeleme: her satış akışı turundan sonra (TourCompleted; ayna o turda yazıldı), akıştan inen değişiklikte ve hero
// zamanlayıcısıyla dakikada bir ("son sinyal" yaşı ve eşik uygunluğu). Mod Off'ta (bugün her lisans) çip ve band
// yoktur. Canlı yol (sohbetten etiket, FİYAT, baskı, yayını bitirme) bunlardan hiçbirini beklemez.
public sealed partial class MainShellViewModel
{
    /// <summary>Dakikalık tazelemenin aralığı (hero zamanlayıcısı 1 sn'de bir sorar).</summary>
    private const long RemoteSessionsRefreshIntervalMs = 60_000;

    private long? _lastRemoteSessionsRefreshAt;

    /// <summary>Uzak yayın çipleri ve force-end akışı; null = kabuk onlarsız kuruldu (testler).</summary>
    public RemoteSessionsViewModel? RemoteSessions { get; private set; }

    /// <summary>Satış akışı turunun sonu (arka plan iş parçacığı): dispatcher'a taşınır, turu bekletmez.</summary>
    private void OnSalesTourCompleted(object? sender, EventArgs e)
    {
        if (RemoteSessions is null) return;
        if (_dispatcher.CheckAccess()) RefreshRemoteSessions();
        else _dispatcher.BeginInvoke(RefreshRemoteSessions);
    }

    /// <summary>Çipleri ve bandı yeniden kurar (UI iş parçacığında). Hatası kabuğu düşürmez.</summary>
    internal void RefreshRemoteSessions()
    {
        if (RemoteSessions is null) return;
        _lastRemoteSessionsRefreshAt = SyncTicks();
        try { RemoteSessions.Refresh(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Uzak yayın çipleri tazelenemedi; sonraki tazelemede yeniden");
        }
    }

    /// <summary>Hero zamanlayıcısının kapısı: en çok dakikada bir.</summary>
    private void RefreshRemoteSessionsThrottled()
    {
        if (RemoteSessions is null) return;
        if (_lastRemoteSessionsRefreshAt is { } last && SyncTicks() - last < RemoteSessionsRefreshIntervalMs) return;
        RefreshRemoteSessions();
    }
}
