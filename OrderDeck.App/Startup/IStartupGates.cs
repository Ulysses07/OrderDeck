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
/// </summary>
public interface IStartupGates
{
    /// <summary>
    /// Açılış ekranını gösterir, <paramref name="work"/> bitene kadar
    /// bekler, sonra ekranı kapatır. İşin fırlattığı hata AYNEN yukarı
    /// çıkar — gate kapandıktan sonra.
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
