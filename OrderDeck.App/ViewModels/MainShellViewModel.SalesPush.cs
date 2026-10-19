using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;

namespace OrderDeck.App.ViewModels;

// ─── Etiket gönderiminin sonucu (spec 2026-10-13 §5.2, §5.6-5, §5.7 "Reddedilen push"; W1-08) ─────────────
//
// Gönderim turu (SessionOrderSyncService, arka plan iş parçacığı) sunucunun yok saydığı ya da reddettiği satırlara
// sunucunun değerini uyguladıktan sonra PushOutcome yayar. Operatör iki durumda uyarılır, açılır pencere OLMADAN
// (kabuk şeridi, "Tamam" ile kapanır): reddedilen değişiklikler ("N değişiklik uygulanamadı: …") ve başka
// bilgisayarda iptal edilmiş olduğu anlaşılan basılı kâğıtlar ("… kâğıtları imha et"). Metin yalnız sayı taşır —
// ad, kullanıcı adı ya da mesaj yok (§9). Şerit kapatılana kadar gelen sonuçlar birikir; aynı etiket iki kez
// sayılmaz (uçuşta değişip sonraki turda yeniden reddedilen satır).
//
// W2-07 (§5.5, §5.6-5): kalıntı kuyruğun komutları da aynı şeridi kullanır — kâğıdın mark-printed'inde sunucunun
// cancelled[]'i "… iptal edilmişti; kâğıtları imha et", alreadyPrinted[]'i "… zaten basılmıştı; kâğıtları imha et"
// olur (sebep ayrı söylenir); kuyruk iptalinin uygulanamayan satırları ve bekleyen komutun kalıcı reddi metin olarak
// eklenir.
public sealed partial class MainShellViewModel
{
    private readonly SessionOrderSyncService? _orderSync;

    /// <summary>Şerit kapatılana kadar biriken reddedilen etiketler: kimlik → neden (boş neden "").</summary>
    private readonly Dictionary<string, string> _pendingRejected = new(System.StringComparer.Ordinal);

    /// <summary>Şerit kapatılana kadar biriken imha edilecek kâğıtların etiketleri.</summary>
    private readonly HashSet<string> _pendingDiscard = new(System.StringComparer.Ordinal);

    /// <summary><see cref="_pendingDiscard"/>'ın başka yerde zaten basılmış (iptal edilmemiş) kısmı: mark-printed'in
    /// <c>alreadyPrinted[]</c>'i. Metin sebebi ayrı söyler.</summary>
    private readonly HashSet<string> _pendingDuplicate = new(System.StringComparer.Ordinal);

    /// <summary>Şerit kapatılana kadar biriken satış uyarıları (W2-07, §5.5): kuyruk iptalinin ya da kâğıdın sunucuya
    /// işlenemeyen satırları, bekleyen komutun kalıcı reddi. Aynı metin bir kez.</summary>
    private readonly List<string> _pendingNotes = new();

    /// <summary><see cref="_pendingNotes"/>'taki bekletilen satır notu (§5.8: baskının <c>PrintHeld</c>'i ya da "Yayını
    /// bitir"in <c>EndStreamHeld</c>'i); null = yok. Kuyruğun o anki durumunu söyler: en çok bir tane, yenisi sayısı ne
    /// olursa olsun eskisinin yerine geçer (<see cref="ShowHeldNotice"/>); kuyruk baskısından ya da "Yayını bitir"den
    /// sonra kuyrukta bekletilen satır kalmadıysa kalkar (<see cref="ClearStaleHeldNotice"/>). Başka yol (ör. kuyruktan
    /// çıkarma) onu kaldırmaz: şerit kapatılana kadar kalır.</summary>
    private string? _heldNote;

    /// <summary>Gönderim sonucunun uyarı şeridi; null = şerit kapalı.</summary>
    [ObservableProperty] private string? _salesPushWarning;

    /// <summary>Arka plan iş parçacığından gelir: dispatcher'a taşınır, gönderim turunu bekletmez.</summary>
    private void OnSalesPushOutcome(object? sender, SalesPushOutcome outcome)
    {
        if (!outcome.NeedsAttention) return;
        if (_dispatcher.CheckAccess()) ShowSalesPushOutcome(outcome);
        else _dispatcher.BeginInvoke(() => ShowSalesPushOutcome(outcome));
    }

    /// <summary>Sonucu şeride ekler (UI iş parçacığında).</summary>
    internal void ShowSalesPushOutcome(SalesPushOutcome outcome)
    {
        foreach (var r in outcome.Rejected) _pendingRejected[r.LabelId] = r.Reason ?? "";
        foreach (var id in outcome.PrintDiscardLabelIds) _pendingDiscard.Add(id);
        RenderSalesPushWarning();
    }

    /// <summary>
    /// Kalıntı kuyruğun ve bekleyen komutların sonucu aynı şeride (W2-07, spec 2026-10-13 §5.5, §5.6-5): imha edilecek
    /// kâğıtlar ("… kâğıtları imha et") ve uyarılar. Aynı etiket ve aynı metin bir kez sayılır — kâğıdın anlık gönderimi
    /// ile arka plandaki sonucu (<see cref="SalesChangesPullService.PendingCommandsSettled"/>) aynı şeyi iki kez söylemez.
    /// UI iş parçacığında.
    /// </summary>
    /// <param name="alreadyPrintedLabelIds"><paramref name="discardLabelIds"/>'in başka yerde zaten basılmış kısmı.</param>
    internal void ShowSalesNotice(IEnumerable<string> discardLabelIds, IEnumerable<string> notes,
        IEnumerable<string>? alreadyPrintedLabelIds = null)
    {
        foreach (var id in discardLabelIds) _pendingDiscard.Add(id);
        foreach (var id in alreadyPrintedLabelIds ?? []) _pendingDuplicate.Add(id);
        foreach (var note in notes)
            if (!_pendingNotes.Contains(note)) _pendingNotes.Add(note);
        RenderSalesPushWarning();
    }

    /// <summary>Bekletilen satırların notu (§5.8) şeride; şeritte önceki bekletme notu varsa (eski bir baskının farklı
    /// sayılı <c>PrintHeld</c>'i dahil) onun yerine geçer — birbiriyle çelişen iki sayı görünmez. UI iş parçacığında.</summary>
    private void ShowHeldNotice(string note)
    {
        if (_heldNote is { } previous) _pendingNotes.Remove(previous);
        _heldNote = note;
        ShowSalesNotice([], [note]);
    }

    /// <summary>Kuyrukta bekletilen satır kalmadıysa (bağlantı geldi ve basıldı ya da artık bekletilmiyor) bekletme notu
    /// (<see cref="_heldNote"/>) bayattır: şeritten kalkar, diğer notlar kalır; hiç not kalmadıysa şerit kapanır. Kuyruk
    /// baskısından ve "Yayını bitir"den sonra. Not yoksa (mod Off dahil) hiçbir şey yapmaz. UI iş parçacığında.</summary>
    private void ClearStaleHeldNotice()
    {
        if (_heldNote is not { } note) return;
        if (_editor.PrintHeldRows(PrintQueue.Select(r => r.Id).ToList()).Count > 0) return;
        _pendingNotes.Remove(note);
        _heldNote = null;
        RenderSalesPushWarning();
    }

    /// <summary>Arka plan iş parçacığından gelir (satış akışı turu, kâğıdın gecikmiş komutu, devir kilidi açılınca biten
    /// kâğıt işareti — <see cref="Services.Sales.ILabelEditor.DeferredPrintSettled"/>): dispatcher'a taşınır.</summary>
    private void OnPendingCommandsSettled(object? sender, PendingSalesCommandsSettled settled)
    {
        void Apply()
        {
            try
            {
                if (settled.NeedsAttention)
                    ShowSalesNotice(settled.DiscardLabelIds, settled.Warnings, settled.AlreadyPrintedLabelIds);
                RefreshPrintedLabels();
                RefreshHeroStats();
            }
            catch (System.Exception ex) when (ex is not System.OperationCanceledException)
            {
                _log.LogWarning(ex, "Bekleyen satış komutlarının sonucu ekrana yansıtılamadı");
            }
        }

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.BeginInvoke(Apply);
    }

    private void RenderSalesPushWarning()
    {
        var byReason = _pendingRejected.Values
            .GroupBy(r => r, System.StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), System.StringComparer.Ordinal);
        // Bir kâğıt hem iptal hem "zaten basılmış" sayıldıysa (iki farklı sonuç) iptal sebebi kazanır.
        var duplicate = _pendingDuplicate.Count(_pendingDiscard.Contains);
        SalesPushWarning = SalesPushWarningText(byReason, _pendingDiscard.Count - duplicate, _pendingNotes, duplicate);
    }

    [RelayCommand]
    private void DismissSalesPushWarning()
    {
        _pendingRejected.Clear();
        _pendingDiscard.Clear();
        _pendingDuplicate.Clear();
        _pendingNotes.Clear();
        _heldNote = null;
        SalesPushWarning = null;
    }

    /// <summary>Şeridin metni; uyarılacak bir şey yoksa null.</summary>
    /// <param name="rejectedByReason">Sunucunun nedeni → reddedilen etiket sayısı.</param>
    /// <param name="discard">İmha edilecek kâğıt sayısı: etiketi başka bilgisayarda iptal edilmiş.</param>
    /// <param name="notes">Satış uyarıları (W2-07), sırasıyla; yoksa null.</param>
    /// <param name="alreadyPrinted">İmha edilecek kâğıt sayısı: etiket başka yerde zaten basılmış (iptal değil).</param>
    internal static string? SalesPushWarningText(IReadOnlyDictionary<string, int> rejectedByReason, int discard,
        IReadOnlyList<string>? notes = null, int alreadyPrinted = 0)
    {
        var parts = new List<string>(2);
        var rejected = rejectedByReason.Values.Sum();
        if (rejected > 0)
        {
            var reasons = rejectedByReason
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, System.StringComparer.Ordinal)
                .Select(kv => rejectedByReason.Count(x => x.Value > 0) == 1
                    ? RejectReasonText(kv.Key)
                    : $"{RejectReasonText(kv.Key)} ({kv.Value})");
            parts.Add($"{rejected} değişiklik uygulanamadı: {string.Join(", ", reasons)}.");
        }
        if (discard > 0)
            parts.Add($"{discard} etiket başka bilgisayarda iptal edilmişti; kâğıtları imha et.");
        if (alreadyPrinted > 0)
            parts.Add($"{alreadyPrinted} etiket başka bilgisayarda zaten basılmıştı; kâğıtları imha et.");
        if (notes is not null) parts.AddRange(notes);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Sunucunun ret nedeninin operatör metni (§5.2).</summary>
    internal static string RejectReasonText(string? reason) => reason switch
    {
        "session-released" => "yayın sunucuya devredilmişti",
        "shipment-shipped" => "kargoya verilmiş dosya",
        "session-owned-elsewhere" => "yayın başka bilgisayarda açık",
        _ => "sunucu kabul etmedi",
    };
}
