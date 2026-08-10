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

    /// <summary>
    /// Lisans başlatıldıktan SONRA okunur.
    ///
    /// ANLAMI: <c>LicenseStatus != NoLicense</c> — yani "aktif lisans" DEĞİL,
    /// "bu makinede bir lisans var". <c>OfflineGrace</c>, <c>OfflineExpired</c>,
    /// <c>ExpiredOnline</c>, <c>TrialActive</c>… hepsi true döner; giriş gate'i
    /// yalnızca hiç lisans yokken açılmalı. <c>== Active</c> yazan bir
    /// gerçekleme, sunucuya ulaşamayan her operatörü açılışta giriş ekranına
    /// hapseder. Tek gerçeklemesi <c>WpfStartupEnvironment.HasLicense</c>,
    /// yani <c>CurrentStatus != LicenseStatus.NoLicense</c>.
    /// </summary>
    bool HasLicense { get; }

    /// <summary>Yerel DB yok ya da 10 KB'ın altında (boş şema).</summary>
    bool IsDatabaseMissingOrTiny();

    Task<IReadOnlyList<BackupMetadata>> ListBackupsAsync();

    /// <summary>
    /// İlk açılış sihirbazı daha önce sonuna kadar götürüldü mü. Property
    /// değil metot: gerçeklemesi ayarları diskten okuyor
    /// (<c>SettingsStore.Load()</c> → <c>File.ReadAllText</c>), yani ucuz bir
    /// alan erişimi gibi görünmemeli — <see cref="IsDatabaseMissingOrTiny"/>
    /// ile aynı gerekçe.
    /// </summary>
    bool HasCompletedFirstRun();

    StreamSession? GetActiveSession();

    void EndSession(string sessionId);

    /// <summary>
    /// Overlay + köprü + hosted service'leri başlatır. false = ölümcül hata
    /// (port çakışması).
    ///
    /// SÖZLEŞME — uygulayan sınıf <c>false</c> döndürmeden ÖNCE hatayı
    /// kullanıcıya göstermek ve <see cref="RequestShutdown"/> çağırmak
    /// ZORUNDA. Akış <c>false</c> görünce sessizce döner: ne mesaj gösterir
    /// ne kapatma ister, shell'i kurmadan başka hiçbir iz bırakmaz. NEDEN
    /// burada: her başarısızlığın kendine ait açıklaması var (hangi port,
    /// hangi servis) ve o metni yalnızca gerçekleme biliyor.
    /// </summary>
    Task<bool> StartBackgroundServicesAsync();

    /// <summary>Shell'i kök görünüme yerleştirir — akışın son adımı.</summary>
    void MountShell();

    void RequestShutdown();

    /// <summary>Uygulamayı kapatıp yeniden açar (geri yükleme sonrası).</summary>
    void RequestRestart();
}
