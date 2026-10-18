using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// <c>PendingSalesCommand.PayloadJson</c>'ın biçimi (spec 2026-10-13 §3.2, §5.5): gönderilecek niyetin TAMAMI —
/// <c>{"payload": &lt;teldeki yük&gt;, "expected": [{"orderId", "version"}]}</c>. Sunucu aynı <c>CommandId</c>'yi içerik
/// özetiyle tanır ve özet beklenen sürümleri de kapsar (<c>SalesCommandHash</c>); yanıtı kaybolan komutun yeniden
/// gönderimi bu yüzden yükü VE beklenen sürümleri aynen taşımalıdır — yerel sürüm o arada akışla ilerlese bile.
/// Mod Off'a dönüşte (§8.5-3) çevirici <c>payload</c>'ı okur: <c>cancel</c> → <c>{items, reason}</c>,
/// <c>mark-printed</c> → <c>{batchId, items [{orderId, seq}]}</c>.
/// </summary>
public static class PendingSalesCommandCodec
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string Encode(SalesCommandIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        using var payload = JsonDocument.Parse(intent.PayloadJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("payload");
            payload.RootElement.WriteTo(writer);
            writer.WritePropertyName("expected");
            JsonSerializer.Serialize(writer, intent.Expected, Web);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <exception cref="JsonException">Kayıt çözülemiyor.</exception>
    /// <exception cref="FormatException">Kimlik GUID değil.</exception>
    public static SalesCommandIntent Decode(PendingSalesCommandRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var doc = JsonDocument.Parse(row.PayloadJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new JsonException("Bekleyen komutun yükü yok.");
        var expected = root.TryGetProperty("expected", out var e)
            ? e.Deserialize<List<SalesCommandExpected>>(Web) ?? []
            : [];
        return new SalesCommandIntent(Guid.ParseExact(row.CommandId, "N"), row.Kind, payload.GetRawText(), expected);
    }
}

/// <summary>Bekleyen komutların bir gönderiminin arayüze dönük sonucu (<see cref="PendingSalesCommandSender.Settled"/>).
/// Kimlik ve metin taşır; ad, kullanıcı adı ya da mesaj yok (§9).</summary>
/// <param name="ChangedLabelIds">Yerelde değeri değişmiş olabilecek etiketler: yanıtın satırları ve örtüsü kalkanlar.</param>
/// <param name="DiscardLabelIds"><c>mark-printed</c>'in <c>cancelled[]</c> ve <c>alreadyPrinted[]</c>'i: kâğıdı basılmış ama
/// sunucuda iptal edilmiş ya da başka yerde basılmış etiketler — "kâğıtları imha et" (§5.5, §5.6-5).</param>
/// <param name="Warnings">Kalıcı retlerin satır içi metni (§5.5).</param>
/// <param name="AlreadyPrintedIds"><see cref="DiscardLabelIds"/>'in <c>alreadyPrinted[]</c>'ten gelen (iptal edilmemiş,
/// başka yerde zaten basılmış) kısmı: şerit sebebi doğru söylesin ("zaten basılmıştı", "iptal edilmişti" değil).</param>
public sealed record PendingSalesCommandsSettled(
    IReadOnlyList<string> ChangedLabelIds,
    IReadOnlyList<string> DiscardLabelIds,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? AlreadyPrintedIds = null)
{
    public static PendingSalesCommandsSettled None { get; } = new([], [], []);

    /// <summary><see cref="DiscardLabelIds"/>'in başka yerde zaten basılmış kısmı (boşsa hepsi iptal).</summary>
    public IReadOnlyList<string> AlreadyPrintedLabelIds => AlreadyPrintedIds ?? [];

    public bool NeedsAttention => DiscardLabelIds.Count > 0 || Warnings.Count > 0;
}

/// <summary>Bir gönderimin sayıları ve arayüze dönük sonucu.</summary>
/// <param name="Sent">Uygulanıp kuyruktan düşen komutlar.</param>
/// <param name="Kept">Sonucu alınamayıp kuyrukta kalan komutlar (sonraki turda yeniden).</param>
/// <param name="Dropped">Kalıcı retle kuyruktan düşen komutlar.</param>
/// <param name="Remaining">Gönderimden sonra kuyrukta kalan komut sayısı (bu gönderimin görmediği yeniler hariç).</param>
public sealed record PendingSalesCommandFlush(int Sent, int Kept, int Dropped, int Remaining,
    PendingSalesCommandsSettled Settled)
{
    public static PendingSalesCommandFlush Empty { get; } = new(0, 0, 0, 0, PendingSalesCommandsSettled.None);
}

/// <summary>
/// Bekleyen satış komutlarının gönderimi (spec 2026-10-13 §5.5, §5.8 "Kalıntı kuyruk", §6.4-1; W2-07). Kuyruğa
/// devredilmiş KENDİ yayınının kalıntı etiketlerinin iptali ve ilk baskısı girer (<see cref="Enqueue"/>; yalnız mod
/// On). Satış akışı turu kuyruğu ön gönderimden sonra, akıştan ÖNCE gönderir
/// (<see cref="SalesChangesPullService.FlushPendingSalesCommandsAsync"/>); kuyruk baskısı kâğıdın komutunu hemen
/// gönderir (<see cref="SendNowAsync"/>). Hepsi tek kapıdan: aynı komut aynı anda iki kez gitmez.
///
/// <para><b>Sonuç</b> (<see cref="SalesCommandClient.SendAsync"/>; yanıtın satırları akışla aynı fonksiyonla yazılır):
/// <list type="bullet">
///   <item>Uygulandı: satır silinir, örtü kalkar. <c>mark-printed</c>'in <c>cancelled[]</c>/<c>alreadyPrinted[]</c>'i
///   "kâğıtları imha et" olur.</item>
///   <item><c>stale</c>: istemci satırları tazeleyip taze sürümlerle BİR kez yeniden dener; yine bayatsa satır kalır.</item>
///   <item>Ağ, sunucu hatası, uç yok, güncelleme gerekli, istek gitmedi ve <c>mode-off</c> (çevirisi Off turunun, §8.5):
///   satır kalır, deneme sayılır, kuyruğun geri kalanı sonraki tura.</item>
///   <item>Kalıcı ret (<c>content-conflict</c>, geçersiz istek, görünürlük, çözülemeyen kayıt …): satır düşer, örtü
///   kalkar, <c>SalesConflictLog</c>'a BİR iz (istemcinin kendi yazdıkları ikinci kez yazılmaz) ve uyarı.</item>
/// </list>
/// Saklı niyet her denemeden sonra son gönderilenle değişir (<see cref="SalesCommandOutcome.Intent"/>): yanıtı
/// kaybolan yeniden denemenin sunucuya kaydolmuş içeriği aynen yinelenir (<see cref="PendingSalesCommandCodec"/>).</para>
///
/// <para>Yerel kip On değilse hiçbir şey gönderilmez. Ağ işi arayüz iş parçacığında koşmaz; günlük yalnız kimlik, tür ve
/// sayı taşır.</para>
/// </summary>
public sealed class PendingSalesCommandSender
{
    private readonly PendingSalesCommandRepository _repo;
    private readonly SalesCommandClient _commands;
    private readonly SalesConflictLogRepository _conflicts;
    private readonly SalesSyncStateRepository _state;
    private readonly SalesConnectivity? _connectivity;
    private readonly IClock _clock;
    private readonly ILogger<PendingSalesCommandSender> _log;

    /// <summary>Tek gönderim kapısı: tur, ödeme ön gönderimi ve kuyruk baskısının anlık gönderimi sıraya girer.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PendingSalesCommandSender(
        PendingSalesCommandRepository repo,
        SalesCommandClient commands,
        SalesConflictLogRepository conflicts,
        SalesSyncStateRepository state,
        SalesConnectivity? connectivity,
        IClock clock,
        ILogger<PendingSalesCommandSender> log)
    {
        _repo = repo;
        _commands = commands;
        _conflicts = conflicts;
        _state = state;
        _connectivity = connectivity;
        _clock = clock;
        _log = log;
    }

    /// <summary>Komutların sonucu (uygulanan ya da kalıcı retle düşen); gönderenin iş parçacığında yükselir, dinleyici
    /// arayüzü dispatcher'a taşımalı. Dinleyicinin hatası gönderimi düşürmez.</summary>
    public event EventHandler<PendingSalesCommandsSettled>? Settled;

    /// <summary>Niyeti kuyruğa yazar (sabit <c>CommandId</c>, örttüğü etiketlerle). Yerel kip On değilse yazmaz: false.</summary>
    public bool Enqueue(SalesCommandIntent intent, IReadOnlyCollection<string> labelIds)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var queued = _repo.Enqueue(intent.CommandId.ToString("N"), intent.Kind, PendingSalesCommandCodec.Encode(intent),
            labelIds, _clock.UnixNow());
        _log.LogInformation("Bekleyen satış komutu {Kind} {CommandId}: {Count} etiket kuyruğa {Result}", intent.Kind,
            intent.CommandId, labelIds.Count, queued ? "alındı" : "alınmadı (yerel kip Off)");
        return queued;
    }

    /// <summary>Bütün kuyruk, eskiden yeniye. Satış akışı turu ve ödeme ön gönderimi (§6.4-1). Arka plan gönderimi:
    /// <c>client-update-required</c> güncelleme sorusunu açmaz (her turda modal pencere olurdu).</summary>
    public Task<PendingSalesCommandFlush> FlushAsync(CancellationToken ct) => RunAsync(null, interactive: false, ct);

    /// <summary>Tek komut, hemen (kuyruk baskısı: kâğıt basıldı, sonuç beklenir). Kapı başka bir gönderimdeyse sırasını
    /// bekler. Operatörün işlemi: güncelleme sorusu süreç başına bir kez açılabilir.</summary>
    public Task<PendingSalesCommandFlush> SendNowAsync(Guid commandId, CancellationToken ct = default) =>
        RunAsync(commandId.ToString("N"), interactive: true, ct);

    private Task<PendingSalesCommandFlush> RunAsync(string? only, bool interactive, CancellationToken ct) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await ProcessAsync(only, interactive, ct).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }, ct);

    private enum Disposition { Done, Keep, KeepAndStop, Stop, Drop }

    private static Disposition Of(SalesCommandStatus status) => status switch
    {
        SalesCommandStatus.Applied => Disposition.Done,
        SalesCommandStatus.Stale => Disposition.Keep,
        SalesCommandStatus.Offline or SalesCommandStatus.Failed or SalesCommandStatus.EndpointMissing
            or SalesCommandStatus.ModeOff or SalesCommandStatus.ClientUpdateRequired => Disposition.KeepAndStop,
        SalesCommandStatus.NotReady => Disposition.Stop,
        _ => Disposition.Drop,
    };

    private async Task<PendingSalesCommandFlush> ProcessAsync(string? only, bool interactive, CancellationToken ct)
    {
        int mode;
        try { mode = _state.GetMode(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Bekleyen satış komutları: yerel kip okunamadı; gönderilmedi");
            return PendingSalesCommandFlush.Empty;
        }
        if (mode != 1) return PendingSalesCommandFlush.Empty;   // mod Off: çevirisi Off turunun (§8.5)

        var rows = only is null
            ? _repo.List()
            : _repo.Get(only) is { } single ? [single] : [];
        if (rows.Count == 0) return PendingSalesCommandFlush.Empty;

        var changed = new HashSet<string>(StringComparer.Ordinal);
        var discard = new HashSet<string>(StringComparer.Ordinal);
        var alreadyPrinted = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        int sent = 0, kept = 0, dropped = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            SalesCommandIntent intent;
            try { intent = PendingSalesCommandCodec.Decode(row); }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
                Drop(row, "unreadable", SalesCommandMessages.ContentConflict, logConflict: true, changed, warnings);
                dropped++;
                _log.LogWarning("Bekleyen satış komutu {CommandId} çözülemedi ({Error}); kuyruktan düştü",
                    row.CommandId, ex.GetType().Name);
                continue;
            }

            var outcome = await _commands.SendAsync(intent, ct, interactive).ConfigureAwait(false);
            _connectivity?.Report(outcome.Status);
            changed.UnionWith(outcome.LabelIds);

            var disposition = Of(outcome.Status);
            switch (disposition)
            {
                case Disposition.Done:
                    _repo.Delete(row.CommandId);
                    changed.UnionWith(row.LabelIds);
                    if (intent.Kind == SalesCommandKinds.MarkPrinted && outcome.Result is { } result)
                    {
                        var covered = row.LabelIds.ToHashSet(StringComparer.Ordinal);
                        var cancelled = (result.Cancelled ?? []).Select(id => id.ToString("N"))
                            .Where(covered.Contains).ToHashSet(StringComparer.Ordinal);
                        var duplicate = (result.AlreadyPrinted ?? []).Select(id => id.ToString("N"))
                            .Where(id => covered.Contains(id) && !cancelled.Contains(id));
                        discard.UnionWith(cancelled);
                        foreach (var id in duplicate)
                            if (discard.Add(id)) alreadyPrinted.Add(id);
                    }
                    sent++;
                    _log.LogInformation(
                        "Bekleyen satış komutu {Kind} {CommandId} uygulandı ({ServerStatus}; {Attempts} önceki deneme); {Labels} etiketin örtüsü kalktı",
                        intent.Kind, intent.CommandId, outcome.ServerStatus, row.Attempts, row.LabelIds.Count);
                    break;
                case Disposition.Keep or Disposition.KeepAndStop:
                    _repo.RecordAttempt(row.CommandId, outcome.Status.ToString(),
                        PendingSalesCommandCodec.Encode(outcome.Intent));
                    kept++;
                    _log.LogInformation("Bekleyen satış komutu {Kind} {CommandId} gönderilemedi ({Status}, deneme {Attempts}); kuyrukta kaldı",
                        intent.Kind, intent.CommandId, outcome.Status, row.Attempts + 1);
                    break;
                case Disposition.Stop:
                    kept++;
                    _log.LogDebug("Bekleyen satış komutu {Kind} {CommandId}: istek gitmedi ({Status}); kuyrukta kaldı",
                        intent.Kind, intent.CommandId, outcome.Status);
                    break;
                default:
                    // İstemci content-conflict ve too-many-candidates'i kendi türüyle zaten yazdı: iz bir kez.
                    var clientLogged = outcome.Status is SalesCommandStatus.ContentConflict
                        or SalesCommandStatus.TooManyCandidates;
                    Drop(row, outcome.Problem?.Code ?? outcome.Status.ToString(), outcome.Message, !clientLogged,
                        changed, warnings);
                    dropped++;
                    _log.LogWarning("Bekleyen satış komutu {Kind} {CommandId} kalıcı olarak reddedildi ({Status}); kuyruktan düştü, {Labels} etiketin örtüsü kalktı",
                        intent.Kind, intent.CommandId, outcome.Status, row.LabelIds.Count);
                    break;
            }
            if (disposition is Disposition.KeepAndStop or Disposition.Stop) break;
        }

        var settled = new PendingSalesCommandsSettled(changed.ToList(), discard.ToList(), warnings,
            alreadyPrinted.ToList());
        if (sent + dropped > 0) RaiseSettled(settled);
        return new PendingSalesCommandFlush(sent, kept, dropped, rows.Count - sent - dropped, settled);
    }

    private void Drop(PendingSalesCommandRow row, string code, string? message, bool logConflict,
        HashSet<string> changed, List<string> warnings)
    {
        _repo.Delete(row.CommandId);
        changed.UnionWith(row.LabelIds);
        warnings.Add(SalesCommandMessages.PendingRejected(row.Kind, row.LabelIds.Count, message));
        if (!logConflict) return;
        try
        {
            _conflicts.Add(_clock.UnixNow(), null, SalesConflictLog.PendingCommandRejected,
                $"{row.Kind}; command={row.CommandId}; code={code}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Bekleyen satış komutu {CommandId}: SalesConflictLog yazılamadı", row.CommandId);
        }
    }

    private void RaiseSettled(PendingSalesCommandsSettled settled)
    {
        try { Settled?.Invoke(this, settled); }
        catch (Exception ex) { _log.LogWarning(ex, "Bekleyen satış komutları: sonuç arayüze iletilemedi"); }
    }
}
