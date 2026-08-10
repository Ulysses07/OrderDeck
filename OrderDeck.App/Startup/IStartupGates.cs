using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Startup;

/// <summary>
/// Açılış sırasının UI tarafı. StartupFlow bu arayüzün ardında ne
/// olduğunu bilmez; üretimde <c>WpfStartupGates</c> gate yığınına basar,
/// testte sahte bir kayıt tutucu.
///
/// Yalnız açılışa ait DEĞİL: <see cref="ShowLoginAsync"/> çalışma anında da
/// çağrılıyor (hesap değiştirme — <c>AccountDialogViewModel</c>), o yüzden
/// <c>isStartupGate</c> parametresi var.
/// </summary>
public interface IStartupGates
{
    /// <summary>
    /// Açılış ekranını gösterir, <paramref name="work"/> bitene kadar
    /// bekler, sonra ekranı kapatır.
    ///
    /// SÖZLEŞME — uygulayan sınıf <paramref name="work"/>'ü
    /// <c>try/finally</c> içinde çağırmak ZORUNDA: gate <c>finally</c>de
    /// kapanır, işin fırlattığı hata AYNEN ondan sonra yukarı çıkar.
    /// NEDEN: akış lisans hatasını yutup devam ediyor (çevrimdışı makinede
    /// uygulama yine de açılmalı). <c>try/finally</c> olmadan yazılan bir
    /// gerçekleme derlenir ve akışın testlerini de geçer, ama internetsiz
    /// makinede açılış ekranı ekranda kilitli kalır — arkasındaki shell'e
    /// bir daha ulaşılamaz.
    ///
    /// <c>Func&lt;Task&gt;</c>, <c>Task</c> değil: iş ekran görünmeden
    /// başlamasın.
    /// </summary>
    Task ShowBootAsync(Func<Task> work);

    /// <summary>true = lisans alındı. isStartupGate:false runtime'da
    /// (hesap değiştirme / sihirbaz) kullanılır.</summary>
    Task<bool> ShowLoginAsync(bool isStartupGate);

    Task<RestoreOutcome> ShowRestoreAsync(IReadOnlyList<BackupMetadata> backups);

    Task ShowFirstRunAsync();

    Task<SessionRecoveryChoice> ShowSessionRecoveryAsync(StreamSession session);
}
