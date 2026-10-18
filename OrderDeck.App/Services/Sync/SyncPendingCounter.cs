using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bekleyen kayıt sayısı ve dikkat sayacı; müşteri imlecini lisansa göre çözer.
/// İmleç adı gönderim servisinin sabitinden okunur (U15: biçim-2 imleci) — önceki sürümün
/// <c>customer-projection-out</c> imleciyle biçim 2 ile hiç gönderilmemiş satırlar "gitti" sayılırdı.
/// Lisans yoksa (boş/boşluk) imleç 0: bütün müşteriler bekler (gönderim de lisanssız koşmaz).</summary>
public sealed class SyncPendingCounter
{
    private readonly SyncOutboxRepository _outbox;
    private readonly SyncCursorRepository _cursors;
    private readonly ICurrentLicenseProvider _license;
    private readonly Func<int>? _stuckHandovers;

    /// <param name="stuckHandovers">Devredilemeyen kendi yayınlarının sayısı (W2-09, spec 2026-10-13 §5.7: dikkat
    /// sayacına eklenir; <see cref="SalesChangesPullService.StuckHandovers"/>). null = sayılmaz.</param>
    public SyncPendingCounter(SyncOutboxRepository outbox, SyncCursorRepository cursors, ICurrentLicenseProvider license,
        Func<int>? stuckHandovers = null)
    {
        _outbox = outbox; _cursors = cursors; _license = license; _stuckHandovers = stuckHandovers;
    }

    public int Count()
    {
        var key = _license.CurrentLicenseKey;
        var seq = string.IsNullOrWhiteSpace(key)
            ? 0L
            : _cursors.Get(WpfCustomerProjectionSyncService.CursorName, key)?.Seq ?? 0L;
        return _outbox.CountPending(seq);
    }

    /// <summary>D2'nin kalıcı uyarıları (U8, U10) ve devredilemeyen yayınlar (§5.7).</summary>
    public SyncAttention Attention() =>
        _outbox.CountAttention() with { StuckHandovers = _stuckHandovers?.Invoke() ?? 0 };
}
