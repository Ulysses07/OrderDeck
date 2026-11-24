using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services.Chat;
using OrderDeck.App.Services.Instagram;
using OrderDeck.Core.Chat;

namespace OrderDeck.App.ViewModels;

// ─── Sohbet durum şeridi ve "Sohbeti yenile" (tasarım 2026-11-24 §5.4, §5.5) ────────────────────────────────────
//
// Sohbet panelinde başlığın altındaki şerit bütün sohbet kaynaklarını gösterir: YouTube, TikTok, Facebook
// (ChatSourceMonitor'dan, ChatSourcePresenter ile) ve Instagram hesapları (InstagramHealthMonitor'dan,
// InstagramHealthPresenter ile). İki sunucu aynı kuralı paylaşır: bu oturumda bağlıyken düşen kaynak, başka
// HERHANGİ bir kaynak bağlıyken "yayın kesildi"dir. O yüzden değerlendirme burada, tek yerde ve belli bir sırayla
// koşar (EvaluateChatStatus); üst nokta ve kenar çubuğu aynı sonucu okur.
//
// Yenileme yayın oturumuna, kuyruğa, etiketlere dokunmaz: servislere yalnız sinyal verir (ChatReconnectSignal),
// bağlanma servislerin kendi döngüsünde sürer. Düğmenin evresini saf ChatRefreshTracker tutar.
public sealed partial class MainShellViewModel
{
    /// <summary>Yenileme sürerken şeridin güncellenme aralığı (olağan nabız 5 sn).</summary>
    private static readonly TimeSpan ChatRefreshTick = TimeSpan.FromMilliseconds(500);

    private readonly ChatSourceMonitor? _chatSources;
    private readonly ChatReconnectSignal? _chatReconnect;

    private readonly ChatRefreshTracker _chatRefresh = new(
        minimum: TimeSpan.FromSeconds(1), maximum: TimeSpan.FromSeconds(15), doneFor: TimeSpan.FromSeconds(1.5));

    private DispatcherTimer? _chatRefreshTimer;

    /// <summary>Son değerlendirmenin platform görünümü; izleyici yoksa null.</summary>
    private ChatSourceView? _chatSourceView;

    /// <summary>Son değerlendirmede okunan platform anlık görüntüsü: kenar çubuğu "bağlı mı"yı buradan okur.</summary>
    private IReadOnlyList<ChatSourceHealth> _chatSourceSnapshot = [];

    /// <summary>Şeridin satırları: önce YouTube, TikTok, Facebook; sonra Instagram hesapları.</summary>
    public ObservableCollection<ChatStatusRow> ChatStatusRows { get; } = new();

    /// <summary>Şerit (ve yenileme düğmesi) görünsün mü: yayın oturumu açıkken hep görünür.</summary>
    [ObservableProperty] private bool _showChatStatus;

    /// <summary>Yenileme sürüyor: düğmede halka döner.</summary>
    [ObservableProperty] private bool _isRefreshingChat;

    /// <summary>Yenileme sorunsuz bitti: düğmede kısa süre onay işareti durur.</summary>
    [ObservableProperty] private bool _chatRefreshDone;

    /// <summary>
    /// Sohbet kaynaklarını değerlendirir ve şeride yazar. Sıra bağlayıcıdır: Instagram'ın "yayın kesildi"si
    /// platformların canlılığına, platformlarınki Instagram'ın canlılığına bakar.
    /// </summary>
    private void EvaluateChatStatus(bool sessionActive)
    {
        var snapshot = _chatSources?.Snapshot() ?? [];
        var platformLive = snapshot.Any(source => source.State == ChatSourceState.Live);

        EvaluateInstagramHealth(sessionActive, otherSourceLive: platformLive);

        _chatSourceSnapshot = snapshot;
        _chatSourceView = _chatSources is null
            ? null
            : ChatSourcePresenter.Evaluate(
                snapshot, InstagramNow(), otherSourceLive: _instagramView?.AnyAccountLive ?? false);

        var rows = new List<ChatStatusRow>();
        if (_chatSourceView is not null) rows.AddRange(_chatSourceView.Rows);
        if (_instagramView is not null) rows.AddRange(InstagramStatusRows(_instagramView));

        // Koleksiyon baştan kurulmaz: eşit satıra dokunulmaz, açık ipucu her nabızda kapanmaz.
        if (!ChatStatusRows.SequenceEqual(rows))
        {
            for (var i = 0; i < rows.Count && i < ChatStatusRows.Count; i++)
            {
                if (!Equals(ChatStatusRows[i], rows[i])) ChatStatusRows[i] = rows[i];
            }

            while (ChatStatusRows.Count > rows.Count) ChatStatusRows.RemoveAt(ChatStatusRows.Count - 1);
            for (var i = ChatStatusRows.Count; i < rows.Count; i++) ChatStatusRows.Add(rows[i]);
        }

        ShowChatStatus = sessionActive;
    }

    /// <summary>
    /// Instagram hesap satırlarını şerit satırına çevirir. "Instagram" etiketi ilk hesabın satırındadır: şerit alt
    /// satıra inerken ondan ayrılmaz. Sağlıklı (canlı) hesap yalnız adını yazar.
    /// </summary>
    private static IEnumerable<ChatStatusRow> InstagramStatusRows(InstagramHealthView view)
    {
        for (var i = 0; i < view.Rows.Count; i++)
        {
            var row = view.Rows[i];
            yield return new ChatStatusRow(
                "instagram:" + row.PageId,
                i == 0 ? "Instagram" : null,
                row.Severity == InstagramStatusSeverity.Live ? row.Label : row.Text,
                row.Severity switch
                {
                    InstagramStatusSeverity.Live => ChatStatusSeverity.Live,
                    InstagramStatusSeverity.Warning => ChatStatusSeverity.Warning,
                    _ => ChatStatusSeverity.Neutral,
                },
                row.Tooltip,
                view.ProblemPageIds.Contains(row.PageId),
                view.ConnectingPageIds.Contains(row.PageId));
        }
    }

    /// <summary>
    /// Üst noktayı sarartan sohbet kaynağı cümleleri (platformlar, sonra Instagram), satır satır; yoksa null.
    /// </summary>
    private string? ChatStatusTopDotTooltip()
    {
        var sentences = new[] { _chatSourceView?.TopDotTooltip, _instagramView?.TopDotTooltip }
            .Where(sentence => sentence is not null)
            .ToArray();
        return sentences.Length == 0 ? null : string.Join("\n", sentences);
    }

    /// <summary>İzleyicideki son değerlendirmeye göre platform bağlı mı (kenar çubuğu).</summary>
    private bool IsChatSourceLive(string platform) =>
        _chatSourceSnapshot.Any(source => source.Platform == platform && source.State == ChatSourceState.Live);

    // ── Yenileme ────────────────────────────────────────────────────────────────────────────────

    private bool CanRefreshChat() =>
        IsStreamActive && _chatReconnect is not null && _chatRefresh.Phase == ChatRefreshPhase.Idle;

    /// <summary>
    /// "Sohbeti yenile": her kaynağa "baştan bağlan" sinyali verir ve düğmeyi meşgul eder. Onay sormaz (geri
    /// alınacak bir şey bozmaz); arayüzü bekletmez.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshChat))]
    private void RefreshChat()
    {
        _chatRefresh.Start(InstagramNow());
        _chatReconnect!.Request();

        // Servisler isteği alır almaz "bağlanıyor" bildirir: şerit nabzı beklemeden gösterir.
        EvaluateChatStatus(sessionActive: true);
        RefreshConnections();
        ApplyChatRefreshPhase();

        _chatRefreshTimer ??= new DispatcherTimer(
            ChatRefreshTick, DispatcherPriority.Background, (_, _) => TickChatRefresh(), _dispatcher);
        _chatRefreshTimer.Start();
    }

    /// <summary>
    /// Yenileme sürerken (ve onay işareti görünürken) koşan tık: satırlar yeniden değerlendirilir, düğmenin evresi
    /// ilerletilir. Ayar dosyası OKUNMAZ (olağan nabzın işi). Zamanlayıcı evre boşa dönünce durur.
    /// </summary>
    internal void TickChatRefresh()
    {
        if (_chatRefresh.Phase != ChatRefreshPhase.Idle)
        {
            EvaluateChatStatus(sessionActive: IsStreamActive);
            RefreshConnections();
            _chatRefresh.Observe(
                InstagramNow(),
                anyConnecting: ChatStatusRows.Any(row => row.IsConnecting),
                anyProblem: ChatStatusRows.Any(row => row.IsProblem));
        }

        ApplyChatRefreshPhase();
    }

    /// <summary>İzleyicinin evresini düğmenin görünümüne yansıtır; boşa dönüldüyse zamanlayıcıyı durdurur.</summary>
    private void ApplyChatRefreshPhase()
    {
        IsRefreshingChat = _chatRefresh.Phase == ChatRefreshPhase.Busy;
        ChatRefreshDone = _chatRefresh.Phase == ChatRefreshPhase.Done;
        RefreshChatCommand.NotifyCanExecuteChanged();
        if (_chatRefresh.Phase == ChatRefreshPhase.Idle) _chatRefreshTimer?.Stop();
    }

    /// <summary>
    /// Yayın oturumu açıldı ya da kapandı: şerit oturumla birlikte görünür ve kalkar (nabız beklenmez), biten
    /// oturum süren yenilemeyi iptal eder.
    /// </summary>
    partial void OnIsStreamActiveChanged(bool value)
    {
        ShowChatStatus = value;
        if (!value) _chatRefresh.Cancel();
        ApplyChatRefreshPhase();
    }

    private void StopChatRefreshTimer()
    {
        _chatRefreshTimer?.Stop();
        _chatRefreshTimer = null;
    }
}
