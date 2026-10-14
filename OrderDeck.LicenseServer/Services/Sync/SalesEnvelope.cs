using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Satış isteklerinin istek düzeyindeki v2 zarfı alanları (spec 2026-10-13 §4.0): <c>sessions/sync</c>,
/// <c>orders/sync</c> ve <c>shipments/sync</c> gövdeleri bunları taşır. Hepsi isteğe bağlı: bugünkü istemci
/// (≤0.9.10) hiçbirini göndermez ve protokolsüz (v1) sayılır.
/// </summary>
public interface ISalesEnvelopeFields
{
    Guid? ReplicaId { get; }
    int? Protocol { get; }
    int? SeenMode { get; }
}

/// <summary>
/// Doğrulanmış zarf. v1 = protokolsüz istek (bugünkü istemci); v2 = <c>Protocol = 2</c>, ReplicaId ve görülen kip
/// dolu. Yarım bir zarf (ör. protokolsüz ReplicaId, ReplicaId'siz v2) sessizce v1 sayılmaz, 400 ile reddedilir:
/// istemci hatası görünür olmalı (§1 amaç 5).
/// </summary>
public sealed record SalesEnvelope(int Protocol, Guid ReplicaId, SalesSyncMode SeenMode)
{
    public const int V2 = 2;

    public static readonly SalesEnvelope Legacy = new(1, Guid.Empty, SalesSyncMode.Off);

    public bool IsV2 => Protocol == V2;

    /// <summary>Gövdedeki zarf alanlarını doğrular. <paramref name="fields"/> null ise (gövdesiz istek) v1.</summary>
    public static bool TryRead(ISalesEnvelopeFields? fields, out SalesEnvelope envelope, out string? error)
        => TryRead(fields?.ReplicaId, fields?.Protocol, fields?.SeenMode, out envelope, out error);

    public static bool TryRead(
        Guid? replicaId, int? protocol, int? seenMode, out SalesEnvelope envelope, out string? error)
    {
        envelope = Legacy;
        error = null;

        if (protocol is null)
        {
            if (replicaId is null && seenMode is null) return true;
            error = "replicaId ve seenMode yalnız protocol ile gönderilir.";
            return false;
        }

        if (protocol != V2)
        {
            error = $"Desteklenmeyen protokol: {protocol}.";
            return false;
        }

        if (replicaId is not { } id || id == Guid.Empty)
        {
            error = "v2 isteği replicaId taşımalı.";
            return false;
        }

        if (seenMode is not (0 or 1))
        {
            error = "v2 isteği seenMode (0 ya da 1) taşımalı.";
            return false;
        }

        envelope = new SalesEnvelope(V2, id, (SalesSyncMode)seenMode.Value);
        return true;
    }
}
