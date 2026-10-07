using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bekleyen kayıt sayısı ve dikkat sayacı; müşteri imlecini lisansa göre çözer.
/// İmleç adı gönderim servisinin sabitinden okunur (U15: biçim-2 imleci) — önceki sürümün
/// <c>customer-projection-out</c> imleciyle biçim 2 ile hiç gönderilmemiş satırlar "gitti" sayılırdı.
/// Lisans yoksa imleç 0: bütün müşteriler bekler (gönderim de lisanssız koşmaz).</summary>
public sealed class SyncPendingCounter
{
    private readonly SyncOutboxRepository _outbox;
    private readonly SyncCursorRepository _cursors;
    private readonly ICurrentLicenseProvider _license;

    public SyncPendingCounter(SyncOutboxRepository outbox, SyncCursorRepository cursors, ICurrentLicenseProvider license)
    {
        _outbox = outbox; _cursors = cursors; _license = license;
    }

    public int Count()
    {
        var key = _license.CurrentLicenseKey;
        var seq = string.IsNullOrEmpty(key)
            ? 0L
            : _cursors.Get(WpfCustomerProjectionSyncService.CursorName, key)?.Seq ?? 0L;
        return _outbox.CountPending(seq);
    }

    /// <summary>D2'nin kalıcı uyarıları (U8, U10).</summary>
    public SyncAttention Attention() => _outbox.CountAttention();
}
