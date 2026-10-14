using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Kopya satırının, bu isteğin kalp atışından ÖNCEKİ hâli — açılış penceresi bu hâl üzerinden yargılanır (spec
/// 2026-10-13 §4.0). İsteğin kendi dokunuşu (ilk kayıtta <c>FirstSeenAt = şimdi</c>, 1 bildiriminde
/// <c>SeenModeOnAt = şimdi</c>) pencereyi kendi kendine kapatmamalı ya da açmamalı.
/// </summary>
/// <param name="FirstSeenAt">Kopyanın bu lisansta ilk görüldüğü an.</param>
/// <param name="PreviousReplicaId">Doluysa kopya yedekten geri yüklenmiştir (§8.7).</param>
/// <param name="SeenModeOnAt">Kopyanın kipi 1 bildirdiği ilk an (mod son açıldıktan sonra yeniden yazılır).</param>
/// <param name="FingerprintConflict">Aynı ReplicaId'ye ikinci bir donanım parmak izi görüldü mü (disk klonu;
/// <see cref="WpfReplica.HardwareFingerprintConflictAt"/>).</param>
/// <param name="HardwareFingerprint">Kopyanın hello'da bildirdiği ilk donanım parmak izi; null = bilinmiyor. Geri
/// yüklenen kopyanın aynı makinede olup olmadığını söyler (devralmada Y8, §8.7, §11); pencere okumaz.</param>
public sealed record ReplicaHistory(
    DateTimeOffset FirstSeenAt,
    Guid? PreviousReplicaId,
    DateTimeOffset? SeenModeOnAt,
    bool FingerprintConflict,
    string? HardwareFingerprint = null)
{
    public static ReplicaHistory Of(WpfReplica row) =>
        new(row.FirstSeenAt, row.PreviousReplicaId, row.SeenModeOnAt, row.HardwareFingerprintConflictAt is not null,
            row.HardwareFingerprint);
}

/// <summary>
/// Açılış penceresi (spec 2026-10-13 §4.0, §8.4, M16, M21): mod On'da <c>SeenMode = 0</c> bildiren v2 isteği
/// yalnız şu koşulların HEPSİ tutarsa Off kurallarıyla yargılanır (K4 ve K5-ii istisnaları, §7.1):
/// <list type="number">
///   <item>kopya, mod son açılmadan önce vardı (<c>FirstSeenAt &lt; SalesSyncModeChangedAt</c>)</item>
///   <item>yedekten geri yüklenmemiş (<c>PreviousReplicaId</c> boş)</item>
///   <item>aynı ReplicaId'ye ikinci parmak izi yok</item>
///   <item>bu açılıştan beri modu görmemiş (<c>SeenModeOnAt</c> boş ya da <c>SalesSyncModeChangedAt</c>'ten eski)</item>
/// </list>
/// Pencere istemcinin bildirdiği kipe değil kopya geçmişine bağlıdır (M21): geri yüklenmiş veritabanı yedekteki
/// kipi bildirir ve kuyruğundaki bayat değerler Off kurallarıyla alınırsa devredilmiş yayında sonradan verilmiş
/// kararları geri alırdı. Belirsiz her durumda (yeni kopya, değişim anı bilinmiyor) pencere KAPALIDIR: istek On
/// kurallarıyla yargılanır, karar korunur.
/// </summary>
public static class SalesOpeningWindow
{
    /// <param name="mode">Lisansın şimdiki kipi.</param>
    /// <param name="modeChangedAt">Kipin son değiştiği an (<c>Licenses.SalesSyncModeChangedAt</c>).</param>
    /// <param name="seenMode">İsteğin bildirdiği görülen kip.</param>
    /// <param name="before">Kopya satırının bu istekten önceki hâli; null = kopya bu istekle doğdu.</param>
    public static bool Applies(
        SalesSyncMode mode, DateTimeOffset? modeChangedAt, SalesSyncMode seenMode, ReplicaHistory? before)
    {
        if (mode != SalesSyncMode.On || seenMode != SalesSyncMode.Off) return false;
        if (before is null || modeChangedAt is not { } changedAt) return false;

        return before.FirstSeenAt < changedAt
            && before.PreviousReplicaId is null
            && !before.FingerprintConflict
            && (before.SeenModeOnAt is null || before.SeenModeOnAt < changedAt);
    }
}
