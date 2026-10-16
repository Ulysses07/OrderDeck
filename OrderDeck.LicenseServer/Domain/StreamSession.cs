namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayın oturumu (WPF lokalde "StreamSession"). Mobile Panel "Siparişler"
/// ekranında yayın bazlı sipariş listesi için server'a replica edilir.
///
/// WPF authoritative — kararlar (yayın başlat/bitir) WPF'te verilir, server
/// pasif replika. Tenant izolasyonu LicenseId üzerinden.
/// </summary>
public sealed class StreamSession
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public string? Title { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Comma-separated platform list: "instagram,youtube,tiktok"</summary>
    public string Platforms { get; set; } = "";

    public string? Notes { get; set; }

    /// <summary>Server-side last update — reverse-sync cursor için.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    // ── Sahiplik ve devir (yayın ve etiket senkronu, spec 2026-10-13 §2, §3.1).

    /// <summary>Yayını açan kopya (<see cref="WpfReplica.ReplicaId"/>). NULL =
    /// yayın eski sürümde açıldı (miras).</summary>
    public Guid? OwnerReplicaId { get; set; }
    public string? OwnerMachineName { get; set; }

    /// <summary>Zorla bitirmeyi yapan: <see cref="ForceEndedByCli"/> ya da
    /// ReplicaId. Yalnız zorla bitirme yazar.</summary>
    public string? ForceEndedBy { get; set; }

    /// <summary><see cref="ForceEndedBy"/>'ın <c>sales-sync force-end</c>
    /// CLI'ıyla bitirilen yayındaki değeri (§4.10).</summary>
    public const string ForceEndedByCli = "cli";

    /// <summary>Devir anı (sunucu saati). NULL ve <see cref="EndedAt"/> NULL =
    /// canlı; NULL ve EndedAt dolu = devrediliyor; dolu = devredilmiş (§2).</summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Sahibin <c>Release = false</c> kapanış gönderiminin bildirdiği
    /// gönderilmemiş etiket sayısı (§4.1).</summary>
    public int? UnsentLabelCount { get; set; }

    /// <summary>SQL Server rowversion — değişiklik akışının imleci (§4.4).
    /// Eşzamanlılık jetonu DEĞİL. InMemory sağlayıcısında hep 0 kalır; bkz.
    /// <see cref="WpfCustomerProjection.ChangeSeq"/>.</summary>
    public long ChangeSeq { get; set; }
}
