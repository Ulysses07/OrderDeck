using System.ComponentModel.DataAnnotations;

namespace OrderDeck.LicenseServer.Domain;

public sealed class Activation
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string HardwareFingerprint { get; set; } = "";
    public string? MachineName { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }

    /// <summary>Bu cihazda son görülen uygulama sürümü (User-Agent
    /// "OrderDeck-WPF/x.y.z"den). Saha sürüm dağılımı için — R8 §23-8
    /// telemetri boşluğu. Null = sürüm raporlamayan eski istemci.</summary>
    [MaxLength(32)]
    public string? AppVersion { get; set; }

    /// <summary>Concurrency token — protects LastSeenAt / DeactivatedAt from lost
    /// updates when Heartbeat and Deactivate race.</summary>
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
