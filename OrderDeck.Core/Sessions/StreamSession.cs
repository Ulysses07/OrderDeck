using System.Collections.Generic;

namespace OrderDeck.Core.Sessions;

public sealed record StreamSession(
    string Id,
    string? Title,
    long StartedAt,
    long? EndedAt,
    IReadOnlyList<string> Platforms,
    string? Notes,
    /// <summary>PR siparis-sync (2026-05-13): LicenseServer'a son sync zamanı.
    /// Null = outbox'ta, henüz push edilmedi. End/Update sonrası null'a düşer
    /// → bir sonraki tick'te tekrar push.</summary>
    long? SyncedAt = null);
