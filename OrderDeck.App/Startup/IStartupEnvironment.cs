using System.Collections.Generic;
using System.Threading.Tasks;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Startup;

/// <summary>
/// Açılış sırasının servis tarafı: lisans, veritabanı, yedek, oturum,
/// arka plan servisleri ve uygulama ömrü.
///
/// StartupFlow'un servisleri doğrudan çağırmamasının sebebi test değil
/// yalnızca: bu arayüz aynı zamanda "açılışta neye dokunuluyor"un tam
/// listesi. Yeni bir açılış adımı buraya bir üye eklemeden yazılamaz.
/// </summary>
public interface IStartupEnvironment
{
    Task InitializeLicenseAsync();

    /// <summary>Lisans başlatıldıktan SONRA okunur.</summary>
    bool HasLicense { get; }

    /// <summary>Yerel DB yok ya da 10 KB'ın altında (boş şema).</summary>
    bool IsDatabaseMissingOrTiny();

    Task<IReadOnlyList<BackupMetadata>> ListBackupsAsync();

    bool HasCompletedFirstRun { get; }

    StreamSession? GetActiveSession();

    void EndSession(string sessionId);

    /// <summary>
    /// Overlay + köprü + hosted service'leri başlatır. false = ölümcül
    /// hata (port çakışması); kapatmayı uygulama KENDİ yapar, akışın işi
    /// yalnızca shell'i kurmamaktır.
    /// </summary>
    Task<bool> StartBackgroundServicesAsync();

    /// <summary>Shell'i kök görünüme yerleştirir — akışın son adımı.</summary>
    void MountShell();

    void RequestShutdown();

    /// <summary>Uygulamayı kapatıp yeniden açar (geri yükleme sonrası).</summary>
    void RequestRestart();
}
