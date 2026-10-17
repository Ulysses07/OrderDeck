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
    long? SyncedAt = null,
    /// <summary>F05: her SyncedAt-düşüren mutasyonda +1; MarkSynced yalnız
    /// Revision değişmemişse yazar (bkz. <see cref="Sales.Label"/>).</summary>
    long Revision = 0,
    /// <summary>Göç 049 (spec 2026-10-13 §2, §5.2): sunucunun devir anı (unix saniye); devir sunucudan görülene
    /// kadar boş. Mod Off'ta yalnız saklanır.</summary>
    long? ReleasedAt = null,
    /// <summary>Göç 049: <see cref="StreamSessionAuthority.Local"/> ise satırı bu bilgisayar yazar,
    /// <see cref="StreamSessionAuthority.Server"/> ise yalnız komut yanıtı ve akış. Kendi oturumu ancak mod On'da
    /// devir görülünce <c>server</c> olur (§5.2).</summary>
    string Authority = StreamSessionAuthority.Local);

/// <summary><c>StreamSession.Authority</c> değerleri (göç 049'un CHECK kısıtı).</summary>
public static class StreamSessionAuthority
{
    public const string Local = "local";
    public const string Server = "server";
}
