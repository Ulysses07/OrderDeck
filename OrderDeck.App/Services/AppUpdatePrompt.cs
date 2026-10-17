using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Velopack;

namespace OrderDeck.App.Services;

/// <summary>
/// Uygulama güncelleme sorusu: yeni sürüm varsa indirir ve operatöre "şimdi yeniden başlatıp güncellensin mi?" diye
/// sorar. Açılışta (<see cref="App"/>) ve sunucu bu sürümü yetersiz bulduğunda (satış komutunun 409
/// <c>client-update-required</c>'ı, spec 2026-10-13 §5.3) aynı soru kullanılır — yeni bir pencere yok.
/// </summary>
public interface IAppUpdatePrompt
{
    /// <summary>Denetimi arka planda başlatır; beklemez ve fırlatmaz. Bir denetim sürerken gelen çağrı yenisini
    /// başlatmaz (soru üst üste açılmaz).</summary>
    void CheckAndPrompt();
}

/// <summary>
/// Velopack güncellemesi (eskiden <c>App.OnStartup</c>'ın içindeydi; davranış aynı): feed'den denetler, delta iner,
/// operatör onaylayınca yeniden başlatıp uygular, "Hayır" bir sonraki açılışa bırakır. Yalnız Velopack ile kurulmuş
/// sürümde çalışır (geliştirme koşusunda <c>IsInstalled = false</c> → hiçbir şey). En iyi çaba: hata günlüğe düşer,
/// akışı bozmaz.
/// </summary>
public sealed class VelopackUpdatePrompt : IAppUpdatePrompt
{
    /// <summary>Velopack feed (delta/otomatik güncelleme). Statik dosya host'u:
    /// Setup.exe + full/delta .nupkg + releases.win.json burada durur (Caddy serve).</summary>
    public const string FeedUrl = "https://orderdeckapp.com/downloads/velopack";

    private readonly ILogger<VelopackUpdatePrompt> _log;
    private int _running;

    public VelopackUpdatePrompt(ILogger<VelopackUpdatePrompt> log) => _log = log;

    public void CheckAndPrompt()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var mgr = new UpdateManager(FeedUrl);
                if (!mgr.IsInstalled) return;

                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion is null) return;

                await mgr.DownloadUpdatesAsync(newVersion);

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null) return;
                dispatcher.Invoke(() =>
                {
                    var ver = newVersion.TargetFullRelease.Version;
                    var choice = MessageBox.Show(
                        $"Yeni sürüm hazır: {ver}\n\nŞimdi yeniden başlatıp güncellensin mi?\n"
                        + "(Hayır dersen bir sonraki açılışta uygulanır.)",
                        "OrderDeck — Güncelleme", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (choice == MessageBoxResult.Yes)
                        mgr.ApplyUpdatesAndRestart(newVersion);
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Velopack update check failed (non-fatal)");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
    }
}
