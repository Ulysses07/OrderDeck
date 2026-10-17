using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bekleyen komutların yerel yazıma çevrilmesinin sonucu (<c>disable</c> turunun 3. adımı, §8.5-3).</summary>
/// <param name="Converted">Yerel yazıma çevrilip kuyruktan düşen komutlar.</param>
/// <param name="Dropped">Kaydı okunamadığı için çevrilemeden düşen komutlar (<c>SalesConflictLog</c>'a iz).</param>
/// <param name="Remaining">Kuyrukta kalan komutlar (yerel yazım düştü; sonraki turda ya da açılışta yeniden).</param>
/// <param name="Settled">Arayüze dönük sonuç: değişen etiketler, imha edilecek kâğıtlar (iptal edilmiş etiketin basılmış
/// kâğıdı) ve uyarılar — bekleyen komutların sunucu sonucuyla aynı şerit (§5.5).</param>
public sealed record SalesPendingConversion(int Converted, int Dropped, int Remaining, PendingSalesCommandsSettled Settled)
{
    public static SalesPendingConversion None { get; } = new(0, 0, 0, PendingSalesCommandsSettled.None);

    public bool Any => Converted + Dropped > 0;
}

/// <summary>
/// <c>disable</c> turunun yerel adımları (spec 2026-10-13 §8.5, §5.5 "Mod Off'a dönüşte", §6.2, M15; W2-11). Kip
/// adımı (<see cref="SalesModeCoordinator"/>) son akış boş sayfaya kadar uygulandıktan sonra çağırır.
///
/// <para><b>2. adım</b> (<see cref="ApplyLocal"/>): tek kilitli işlemde yerel kip 0, kendi yayınları yerel otoriteye,
/// uzak satırlar silinir, toplamlar türetilir, Off'ta başka kopyanın olacak açık dosyalar silinir
/// (<see cref="SalesFeedRepository.ApplyModeOff"/>). İşlem işlendikten SONRA lisans geneli kargo ayarının önbelleği
/// (<c>SalesSyncState</c>, son <c>sales/state</c>) yerel <c>AppSettings.Shipping</c>'e yazılır (§6.2): Off'a dönen
/// bilgisayarlar farklı yerel değerlerle farklı kargo tutarı istemez. Boş eşik ya da ücret "kargo kapalı" demektir.
/// Lisans ayarı hiç yazılmamışsa (sürüm boş) yerel ayar korunur — mod açılışı ayarı şart koştuğu için (§4.10-4) olağan
/// değildir. Yazım düşerse sonraki turda yeniden denenir (<see cref="Resume"/>).</para>
///
/// <para><b>3. adım</b> (<see cref="ConvertPendingCommands"/>), kilit DIŞINDA ve olağan yerel yazım olarak: bekleyen
/// <c>cancel</c> → <see cref="LabelService.Cancel"/>(etiketler, aynı neden); <c>mark-printed</c> →
/// <see cref="LabelService.MarkPrintedAndRecord(IReadOnlyList{string}, string, IReadOnlyList{int})"/>(aynı basış
/// kimliği, aynı sıra). Yazım tetikleyiciyle kuyruğa girer ve push'la gider; basılmış kâğıt yeniden teklif edilmez. Her
/// satır kendi yerel yazımından SONRA silinir — örtü o ana kadar kalkmaz; ikisi arasında süreç ölürse yeniden çeviri
/// zararsızdır (iptal edilmiş ve basılmış etiket atlanır). Yazımı düşen satır ve arkasındakiler kuyrukta kalır.</para>
///
/// <para><b>Yeniden koşma</b> (<see cref="Resume"/>): açılışta (AppHost, hiçbir gönderimden önce) ve her satış akışı
/// turunun başında — yerel <c>Mode = 0</c> ∧ <c>PendingSalesCommand</c> dolu ise 3. adım yeniden koşar (süreç 2. ile 3.
/// adım arasında kapanmış). Görülen kip o ana kadar 1 kalır (<see cref="SalesSyncStateRepository.GetSeenMode"/>).</para>
///
/// <para>Günlük yalnız kimlik, tür ve sayı taşır.</para>
/// </summary>
public sealed class SalesModeOffRound
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly SalesFeedRepository _feed;
    private readonly SalesSyncStateRepository _state;
    private readonly PendingSalesCommandRepository _pending;
    private readonly LabelService _labelService;
    private readonly LabelRepository _labels;
    private readonly SalesConflictLogRepository _conflicts;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _liveSettings;
    private readonly IClock _clock;
    private readonly ILogger<SalesModeOffRound> _log;

    /// <summary>3. adımın tek koşucusu: açılış ve tur aynı satırı iki kez çevirmez.</summary>
    private readonly Lock _gate = new();

    /// <summary>2. adımdan sonraki kargo ayarı yazımı düştü: sonraki turda yeniden.</summary>
    private volatile bool _shippingCopyPending;

    public SalesModeOffRound(
        SalesFeedRepository feed,
        SalesSyncStateRepository state,
        PendingSalesCommandRepository pending,
        LabelService labelService,
        LabelRepository labels,
        SalesConflictLogRepository conflicts,
        SettingsStore settingsStore,
        AppSettings liveSettings,
        IClock clock,
        ILogger<SalesModeOffRound> log)
    {
        _feed = feed;
        _state = state;
        _pending = pending;
        _labelService = labelService;
        _labels = labels;
        _conflicts = conflicts;
        _settingsStore = settingsStore;
        _liveSettings = liveSettings;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// 2. adım ve kargo ayarının kopyası. Ödeme akışındaki müşteri yüzünden işlem yazılmadıysa
    /// (<see cref="SalesModeOffResult.Busy"/>) kargo ayarına da dokunulmaz.
    /// </summary>
    /// <param name="modeSeenAt"><c>sales/state</c>'in <c>modeChangedAt</c>'i (unix saniye).</param>
    public SalesModeOffResult ApplyLocal(long? modeSeenAt, Guid replicaId)
    {
        var result = _feed.ApplyModeOff(modeSeenAt, replicaId);
        if (result.Busy) return result;
        _shippingCopyPending = true;
        CopyLicenseShipping();
        return result;
    }

    /// <summary>
    /// Açılışta ve her turun başında: yarıda kalmış kargo ayarı yazımı ve yerel kip 0 iken kuyrukta kalmış bekleyen
    /// komutlar (3. adımın yeniden koşması). Yerel kip 1 ise hiçbir şey yapmaz (komutlar gönderilir).
    /// </summary>
    public SalesPendingConversion Resume()
    {
        if (_shippingCopyPending && _state.GetMode() == 0) CopyLicenseShipping();
        if (!_state.HasPendingSalesCommands()) return SalesPendingConversion.None;
        return ConvertPendingCommands();
    }

    /// <summary>3. adım: yerel kip 0 iken bekleyen komutları sırayla yerel yazıma çevirir.</summary>
    public SalesPendingConversion ConvertPendingCommands()
    {
        lock (_gate)
        {
            if (_state.GetMode() != 0) return SalesPendingConversion.None;    // mod On: komutla gider (gönderici)
            var rows = _pending.List();
            if (rows.Count == 0) return SalesPendingConversion.None;

            var changed = new HashSet<string>(StringComparer.Ordinal);
            var discard = new HashSet<string>(StringComparer.Ordinal);
            var warnings = new List<string>();
            int converted = 0, dropped = 0;
            foreach (var row in rows)
            {
                LocalWrite? write;
                try { write = Decode(row); }
                catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException
                                           or NotSupportedException)
                {
                    write = null;
                }
                if (write is null)
                {
                    Drop(row, changed, warnings);
                    dropped++;
                    continue;
                }

                try
                {
                    if (write.Kind == PendingSalesCommandKinds.MarkPrinted)
                        discard.UnionWith(_labels.GetByIds(write.LabelIds)
                            .Where(l => l.CancelledAt is not null).Select(l => l.Id));
                    write.Apply(_labelService);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex,
                        "Bekleyen satış komutu {Kind} {CommandId} yerel yazıma çevrilemedi; kuyrukta kaldı, sonraki turda ya da açılışta yeniden",
                        row.Kind, row.CommandId);
                    break;                                                        // sıra korunur
                }
                _pending.Delete(row.CommandId);                                   // örtü yerel yazımdan sonra kalkar
                changed.UnionWith(row.LabelIds);
                converted++;
                _log.LogInformation(
                    "Bekleyen satış komutu {Kind} {CommandId} yerel yazıma çevrildi (mod Off): {Labels} etiket kuyruğa alındı",
                    row.Kind, row.CommandId, row.LabelIds.Count);
            }

            var remaining = rows.Count - converted - dropped;
            if (remaining == 0)
                _log.LogInformation(
                    "Satış senkronu Off turunun 3. adımı tamamlandı: {Converted} bekleyen komut yerel yazıma çevrildi, {Dropped} okunamadı; görülen kip 0",
                    converted, dropped);
            if (discard.Count > 0)
                _log.LogWarning(
                    "Off turu: {Count} etiketin kâğıdı basılmıştı ama etiket başka bilgisayarda iptal edilmiş; kâğıt imha edilmeli",
                    discard.Count);
            return new SalesPendingConversion(converted, dropped, remaining,
                new PendingSalesCommandsSettled(changed.ToList(), discard.ToList(), warnings));
        }
    }

    // ── kargo ayarı ─────────────────────────────────────────────────────

    /// <summary>Lisans geneli kargo ayarının önbelleği → <c>AppSettings.Shipping</c> (disk ve canlı örnek). Düşerse
    /// sonraki turda yeniden.</summary>
    private void CopyLicenseShipping()
    {
        try
        {
            var state = _state.Get();
            if (state.ShippingVersion is null)
            {
                _shippingCopyPending = false;
                _log.LogWarning("Off turu: lisans geneli kargo ayarı sunucuda yazılmamış; yerel kargo ayarı korunuyor");
                return;
            }
            var threshold = state.FreeShippingThreshold;
            var fee = state.ShippingFee;
            // N04: diske atomik Update, canlı örnek aynı değerle (okuyucular — dekont eşiği, ödeme isteği — bunu okur).
            _settingsStore.Update(s => s.Shipping = new ShippingSettings
            {
                FreeShippingThreshold = threshold, ShippingFee = fee,
            });
            _liveSettings.Shipping = new ShippingSettings { FreeShippingThreshold = threshold, ShippingFee = fee };
            _shippingCopyPending = false;
            _log.LogInformation(
                "Off turu: lisans geneli kargo ayarı (sürüm {Version}) yerel ayara yazıldı; kargo {State}",
                state.ShippingVersion, threshold is > 0 && fee is > 0 ? "açık" : "kapalı");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Off turu: lisans geneli kargo ayarı yerel ayara yazılamadı; sonraki turda yeniden");
        }
    }

    // ── bekleyen komut → yerel yazım ────────────────────────────────────

    /// <summary>Bekleyen komutun yerel yazımdaki karşılığı.</summary>
    private sealed record LocalWrite(string Kind, IReadOnlyList<string> LabelIds, Action<LabelService> Apply);

    /// <summary>Kaydın yükünden yerel yazım (<see cref="PendingSalesCommandCodec"/>); okunamıyorsa null.</summary>
    private static LocalWrite? Decode(PendingSalesCommandRow row)
    {
        var intent = PendingSalesCommandCodec.Decode(row);
        switch (row.Kind)
        {
            case PendingSalesCommandKinds.Cancel:
            {
                var payload = JsonSerializer.Deserialize<SalesCancelPayload>(intent.PayloadJson, Web);
                if (payload?.Items is not { Count: > 0 } items || string.IsNullOrWhiteSpace(payload.Reason)) return null;
                var ids = items.Select(id => id.ToString("N")).Distinct(StringComparer.Ordinal).ToList();
                var reason = payload.Reason;
                return new LocalWrite(row.Kind, ids, labels => labels.Cancel(ids, reason));
            }
            case PendingSalesCommandKinds.MarkPrinted:
            {
                var payload = JsonSerializer.Deserialize<SalesMarkPrintedPayload>(intent.PayloadJson, Web);
                if (payload?.Items is not { Count: > 0 } items || string.IsNullOrWhiteSpace(payload.BatchId)) return null;
                var paper = items.OrderBy(i => i.Seq).ToList();                   // kâğıt sırası
                var ids = paper.Select(i => i.OrderId.ToString("N")).ToList();
                var seqs = paper.Select(i => i.Seq).ToList();
                var batchId = payload.BatchId;
                return new LocalWrite(row.Kind, ids, labels => labels.MarkPrintedAndRecord(ids, batchId, seqs));
            }
            default:
                return null;
        }
    }

    /// <summary>Okunamayan kayıt: düşer, örtü kalkar, bir iz ve bir uyarı.</summary>
    private void Drop(PendingSalesCommandRow row, HashSet<string> changed, List<string> warnings)
    {
        _pending.Delete(row.CommandId);
        changed.UnionWith(row.LabelIds);
        warnings.Add(SalesCommandMessages.PendingNotConverted(row.Kind, row.LabelIds.Count));
        _log.LogWarning("Bekleyen satış komutu {Kind} {CommandId} okunamadı; yerel yazıma çevrilmeden düştü, {Labels} etiketin örtüsü kalktı",
            row.Kind, row.CommandId, row.LabelIds.Count);
        try
        {
            _conflicts.Add(_clock.UnixNow(), null, SalesConflictLog.PendingCommandRejected,
                $"{row.Kind}; command={row.CommandId}; code=unreadable-mode-off");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Bekleyen satış komutu {CommandId}: SalesConflictLog yazılamadı", row.CommandId);
        }
    }
}
