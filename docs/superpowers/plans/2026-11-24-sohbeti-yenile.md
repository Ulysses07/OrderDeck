# Sohbeti yenile (İş 2) — uygulama planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Yayın oturumuna dokunmadan sohbet bağlantılarını yenileyen düğme, YouTube ve Instagram'da oturum başlar başlamaz bağlanma ve dört platformlu durum şeridi.

**Architecture:** İki paylaşılan tekil nesne `OrderDeck.Core.Chat`'te durur: `ChatReconnectSignal` (yenileme isteği olayı; servisler oturum olaylarını dinledikleri gibi bunu da dinler) ve `ChatSourceMonitor` (YouTube, Facebook, TikTok için yalnız olgu tutan durum izleyicisi). Metin, renk ve ipucu uygulama katmanındaki saf sunucularda türetilir; Instagram kendi izleyicisini ve sunucusunu korur. Görünüm modeli iki sunucunun satırlarını tek listede birleştirir, yenileme süresini saf bir izleyiciyle (`ChatRefreshTracker`) yönetir.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, gRPC (YouTube), Graph API (Facebook, Instagram), xUnit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-11-24-dugme-hareketleri-ve-sohbeti-yenile-design.md` (§5, §9 "İş 2"). Ön koşul: İş 1 (`docs/superpowers/plans/2026-11-24-dugme-hareketleri.md`) dalda.

## Global Constraints

- Yenileme yayın oturumuna, kuyruğa, etiketlere, satışlara, çekilişe dokunmaz; yalnız oturum açıkken çalışır.
- Yenileme isteğinin dinleyicileri arayüz iş parçacığında çalışır ve BEKLETMEZ: yalnız iptal eder, uyandırır, alan yazar.
- Yinelenen mesaj yok, kaybolan mesaj yok (§5.6). YouTube'un gösterilmiş mesaj belleği servis düzeyinde, son 5000 kimlik.
- YouTube sohbet döngüsü Google izin sayfasını ASLA açmaz: önce tarayıcı açmadan "bağlı mı" diye sorar.
- Hızlı arama: 5 sn aralık, 2 dakika pencere; yalnız "etkin yayın yok" cevabında. Olağan aralık 60 sn.
- Instagram'da sağlıklı çalışan okuyucuya dokunulmaz; hız sınırı beklemesi (okuyucunun içinde) kesilmez.
- Platform anahtarları küçük harf: `youtube`, `tiktok`, `facebook`. Şerit sırası: YouTube, TikTok, Facebook, Instagram.
- Şerit metinleri ve ipuçları tasarımdaki tablolardan (§5.4) birebir alınır.
- Meşgul hâl en az 1 sn, en çok 15 sn; onay işareti 1,5 sn; şerit yenileme sürerken 500 ms'de bir güncellenir.
- Durum renkleri yalnız 8 px noktalarda; platform ikonu yok, düz ad.
- Günlüğe token, kimlik, mesaj metni yazılmaz. Testlerde literal kimlik bilgisi, gerçek görünümlü ad, telefon yok; değerler üretilir (`FacebookOAuthTestKit.NewId()`, `NewToken()`).
- Sunucu, yerel şema, uzantı değişmez.
- Commit iletileri Türkçe emir kipi; son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.

## Review Focus

Testlerin kapsamadığı, en çok ısıracak beş durum:

1. Gerçek platformlarda "yayını kapat, yeniden aç, Yenile'ye bas" akışı (sahte ağla değil): saha denemesi, önce YouTube (Görev 10).
2. Yenileme isteği ile oturum bitişinin aynı anda gelmesi (iptal yarışları): YouTube ve Facebook döngüsünde iptalden sonra durum `Stopped`'a iner, asılı okuyucu kalmaz. Testi Görev 5 ve 6'da.
3. Yenilemeye art arda basılması: her basış bir öncekini temiz keser, okuyucu sayısı birde kalır. Testi Görev 5'te.
4. Uzantının yoklama soketleri (arka plan, açılır pencere) TikTok'u "bağlı" göstermez. Testi Görev 3'te.
5. Ayarda YouTube kanalı yazılı, hesap bağlı değil: tarayıcı açılmaz, satır "bağlantı kurulamıyor" olur, hızlı arama çalışmaz. Testi Görev 5'te.

---

### Task 1: Yenileme sinyali ve durum izleyicisi

**Files:**
- Create: `OrderDeck.Core/Chat/ChatReconnectSignal.cs`, `OrderDeck.Core/Chat/ChatSourceMonitor.cs`
- Test: `OrderDeck.Tests/Chat/ChatSourceMonitorTests.cs`, `OrderDeck.Tests/Chat/ChatReconnectSignalTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class ChatReconnectSignal { public event EventHandler? Requested; public void Request(); }` — `Request` her dinleyiciyi çağırır; biri istisna atarsa öbürleri yine çağrılır, istisna yutulur.
  - `public enum ChatSourceState { Stopped, Connecting, NoBroadcast, Live, Retrying, NeedsAttention }`
  - `public sealed record ChatSourceHealth(string Platform, ChatSourceState State, string? Detail, DateTimeOffset Since, DateTimeOffset? RetryAt, bool WasLiveThisSession);`
  - `public sealed class ChatSourceMonitor` — `ChatSourceMonitor(TimeProvider? time = null)`; `void Report(string platform, ChatSourceState state, string? detail = null, DateTimeOffset? retryAt = null)`; `void ResetSession()`; `IReadOnlyList<ChatSourceHealth> Snapshot()`. İş parçacığı güvenli.
  - Kurallar: aynı durumu yineleyen bildirim `Since`'i değiştirmez; `Detail` ve `RetryAt` her bildirimde yazılır; `Live` bildirimi `WasLiveThisSession`'ı açar; `ResetSession` her girdide `WasLiveThisSession = (State == Live)` yapar; `Snapshot` sırası `youtube`, `tiktok`, `facebook`, sonra öbürleri ada göre; platform adı büyük-küçük harf ayırmaz.

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
public class ChatSourceMonitorTests
{
    // ManualTime ile (OrderDeck.Tests/TestHelpers/ManualTime.cs)
    [Fact] public void Unknown_platform_is_absent_from_the_snapshot()
    [Fact] public void Repeating_a_state_keeps_since_but_rewrites_detail_and_retry_time()
    [Fact] public void A_new_state_moves_since_to_now()
    [Fact] public void Live_marks_the_source_as_live_this_session_and_it_survives_later_states()
    [Fact] public void Reset_session_forgets_past_liveness_but_keeps_a_currently_live_source()
    [Fact] public void Snapshot_is_ordered_youtube_tiktok_facebook()
    [Fact] public void Platform_names_are_case_insensitive()
}

public class ChatReconnectSignalTests
{
    [Fact] public void Request_calls_every_listener()
    [Fact] public void A_throwing_listener_does_not_stop_the_others()
    [Fact] public void Request_without_listeners_does_nothing()
}
```

- [ ] **Step 2: Çalıştır** — `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ChatSourceMonitorTests|FullyQualifiedName~ChatReconnectSignalTests"` → derleme hatası
- [ ] **Step 3: İki sınıfı yaz**
- [ ] **Step 4: Testi çalıştır** — Expected: 10 test geçer
- [ ] **Step 5: Commit** — `feat(chat): yenileme sinyali ve sohbet kaynağı durum izleyicisi`

---

### Task 2: Durum satırlarının sunucuları

**Files:**
- Create: `OrderDeck.App/Services/Chat/ChatSourcePresenter.cs`
- Modify: `OrderDeck.App/Services/Instagram/InstagramHealthPresenter.cs`, `OrderDeck.App/ViewModels/InstagramAccountStatusViewModel.cs`
- Test: `OrderDeck.Tests/Services/Chat/ChatSourcePresenterTests.cs`, `OrderDeck.Tests/Services/Instagram/InstagramHealthPresenterTests.cs`

**Interfaces:**
- Consumes: `ChatSourceHealth`, `ChatSourceState`
- Produces:
  - `public enum ChatStatusSeverity { Live, Neutral, Warning }`
  - `public sealed record ChatStatusRow(string Key, string? GroupLabel, string DisplayText, ChatStatusSeverity Severity, string? Tooltip, bool IsProblem, bool IsConnecting);`
  - `public sealed record ChatSourceView(IReadOnlyList<ChatStatusRow> Rows, bool AnyLive, string? TopDotTooltip);`
  - `public static class ChatSourcePresenter { public static ChatSourceView Evaluate(IReadOnlyList<ChatSourceHealth> snapshot, DateTimeOffset now, bool otherSourceLive); }` — `otherSourceLive`: herhangi bir Instagram hesabı canlı mı. Saf; saat okumaz. `Key` platform adıdır, `GroupLabel` null.
  - `InstagramHealthPresenter.Evaluate(snapshot, now, sessionActive, bool otherSourceLive = false)` — yeni parametre: Instagram dışı bir kaynak canlı mı.
  - `InstagramAccountStatusViewModel` kaydına sona iki parametre: `bool IsProblem = false, bool IsConnecting = false`.

**Kurallar** (tasarım §5.4; metinler oradaki tablolardan birebir):

| `ChatSourceState` | Koşul | Metin (YouTube örneği) | Renk | Sorun | Bağlanıyor |
|---|---|---|---|---|---|
| `Stopped` | — | satır yok | | | |
| `Connecting` | — | `YouTube bağlanıyor` | Neutral | hayır | evet |
| `Live` | — | `YouTube` | Live | hayır | hayır |
| `NoBroadcast` | bu oturumda canlı olmadı | `YouTube yayın bekleniyor` | Neutral | hayır | hayır |
| `NoBroadcast` | canlıydı, başka kaynak canlı | `YouTube yayın kesildi (21:14)` | Warning | evet | hayır |
| `NoBroadcast` | canlıydı, canlı kaynak yok | `YouTube yayın bitti (23:41)` | Neutral | hayır | hayır |
| `Retrying` | `RetryAt` var | `YouTube bağlantı koptu, 21:20'de dener` | canlıydıysa Warning, değilse Neutral | evet | hayır |
| `NeedsAttention` | — | `YouTube bağlantı kurulamıyor` | Warning | evet | hayır |

- "Başka kaynak canlı" = `otherSourceLive` ya da anlık görüntüdeki başka bir platform `Live`.
- Saat `Since`'ten, `now`'ın ofsetiyle `HH:mm`; "'de dener" eki `InstagramHealthPresenter`'daki `ClockWithLocative` ile aynı yoldan (ortak yardımcıya taşınır).
- `NeedsAttention` ipucu servis bildiriminin `Detail`'idir (tasarımdaki ipucu tablosu servislerde üretilir); `Detail` boşsa `{Platform} sohbetine bağlanılamıyor.`
- TikTok: `Live` → `TikTok`, ipucu `TikTok canlı yayın sekmesi bağlı.`; `NoBroadcast` + bu oturumda bağlıydı → `TikTok sekme bağlı değil` (başka kaynak canlıysa Warning + sorun, değilse Neutral); `NoBroadcast` + hiç bağlanmadı → satır yok; öbür durumlar TikTok'ta üretilmez.
- Facebook'ta "yayın kesildi" ipucunun sonu: `Yayını yeniden açtıysan birkaç saniye içinde kendiliğinden bağlanır.`
- `TopDotTooltip`: Warning satırlar için `"{Platform}: {durum metni}."`; "yayın kesildi" yalnız `now - Since <= 2 dk` iken katılır; birden çok satır boşlukla birleşir; yoksa null.
- Instagram sunucusu: `BroadcastEnded` dalında "başka hesap canlı" koşulu `liveSince is not null || otherSourceLive` olur; yalnız öbür platform canlıysa ipucu `Bu hesabın yayını kapandı; yorumları gelmiyor. Öbür kaynaklar yayında.` `WaitingForBroadcast` dalı DEĞİŞMEZ (yalnız öbür Instagram hesaplarına bakar). `IsProblem`: canlı, bağlanıyor, "yayın bekleniyor" ve "yayın bitti" dışındaki her satır. `IsConnecting`: `Starting` satırı.

- [ ] **Step 1: Başarısız testleri yaz**

`ChatSourcePresenterTests`: tablonun her satırı için bir test (metin, renk, ipucu, `IsProblem`, `IsConnecting`); "yayın bekleniyor başka kaynak canlıyken sararmaz"; "yayın kesildi yalnız başka kaynak canlıyken"; TikTok'un üç durumu; `TopDotTooltip`'in iki dakika kuralı; Facebook ipucu; satır sırası.

`InstagramHealthPresenterTests`'e: `Tek_hesap_dusup_baska_platform_canliyken_yayin_kesildi_olur`, `Baska_platform_canli_diye_yayin_bekleyen_hesap_sararmaz`, `Sorun_ve_baglaniyor_bayraklari_durumlara_gore`.

- [ ] **Step 2: Çalıştır** — derleme hatası
- [ ] **Step 3: Sunucuyu yaz, Instagram sunucusunu genişlet**
- [ ] **Step 4: Testi çalıştır** — yeni testler ve mevcut `InstagramHealthPresenterTests` geçer
- [ ] **Step 5: Commit** — `feat(chat): platform durum satırları ve kaynaklar arası "yayın kesildi"`

---

### Task 3: Köprü — TikTok sekmesi izleme ve kilidi bırakma

**Files:**
- Modify: `OrderDeck.Chat/Bridge/ExtensionBridgeServer.cs`
- Test: `OrderDeck.Tests/Chat/ExtensionBridgeServerTests.cs`

**Interfaces:**
- Consumes: `ChatSourceMonitor`, `ChatReconnectSignal`
- Produces:
  - Kurucuya sona iki isteğe bağlı parametre: `ChatSourceMonitor? sources = null, ChatReconnectSignal? reconnect = null`.
  - `{ Type: "connected", Platform: not null }` iletisi: bağlantı o platformun sekmesi olarak işaretlenir (bağlantı başına bir kez); platformun işaretli bağlantı sayısı 0'dan 1'e çıkınca `sources.Report(platform, Live)`.
  - Bağlantı kapanınca işaretleri düşülür; sayı 0'a inince `sources.Report(platform, NoBroadcast)`.
  - `public void ReleaseActiveSources()` — `_activeSources`'u boşaltır (`_lastOwnerByPlatform` kalır), bir satır günlük yazar. `reconnect.Requested` buna bağlanır; `DisposeAsync` aboneliği kaldırır.

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
[Fact] public async Task Connected_message_marks_the_platform_live()
// monitor verilir; connect + {type:"connected",platform:"tiktok"} → Snapshot'ta tiktok Live
[Fact] public async Task A_socket_that_never_says_connected_is_not_a_tab()
// yalnız bağlanıp kapanan soket (uzantının yoklaması) → tiktok girdisi yok
[Fact] public async Task Closing_the_last_tab_reports_no_broadcast()
[Fact] public async Task Second_tab_keeps_the_platform_live_when_the_first_closes()
[Fact] public async Task Releasing_sources_lets_another_tab_take_over_immediately()
// A etkin; B'nin mesajı düşer; ReleaseActiveSources(); B'nin SONRAKİ mesajı yayımlanır
[Fact] public async Task Handover_duplicate_is_still_filtered_after_release()
// A bir mesaj yayımladı; Release; B aynı kullanıcı+metni pencere içinde gönderir → yayımlanmaz
[Fact] public async Task Reconnect_signal_releases_sources()
```

- [ ] **Step 2: Çalıştır** — FAIL
- [ ] **Step 3: Köprüyü genişlet**
- [ ] **Step 4: Testi çalıştır** — `ExtensionBridgeServerTests`'in tamamı geçer
- [ ] **Step 5: Commit** — `feat(bridge): TikTok sekmesini izle, yenilemede etkin kaynak kilidini bırak`

---

### Task 4: YouTube — tarayıcı açmayan yayın araması, kalıcı mesaj belleği, bitiş sebebi

**Files:**
- Create: `OrderDeck.Chat/Ingestors/YouTube/BroadcastLookup.cs`, `OrderDeck.Chat/Ingestors/YouTube/YouTubeSeenMessages.cs`, `OrderDeck.Chat/Ingestors/YouTube/IYouTubeChatIngestor.cs`
- Modify: `OrderDeck.Chat/YouTube/YouTubeModerationService.cs`, `OrderDeck.Chat/Ingestors/YouTube/YouTubeOfficialChatIngestor.cs`
- Test: `OrderDeck.Tests/Chat/YouTube/YouTubeModerationServiceTests.cs`, `OrderDeck.Tests/Chat/YouTube/YouTubeSeenMessagesTests.cs`

**Interfaces:**
- Produces:
  - `public enum BroadcastLookupStatus { Found, NoBroadcast, NotConnected, NeedsAttention, Failed }`
  - `public readonly record struct BroadcastLookup(BroadcastLookupStatus Status, string? VideoId = null, string? LiveChatId = null, string? Message = null);`
  - `YouTubeModerationService.LookupActiveBroadcastAsync(bool fresh, CancellationToken ct = default)` → `Task<BroadcastLookup>`. Sıra: `IsConnectedAsync` false → `NotConnected` (tarayıcı AÇILMAZ); `fresh` → önbellek atılır; etkin yayın yok → `NoBroadcast`; Google hatası ve yetki yenileme hatası → `ClassifyFailure`. İptal fırlatılır. `GetActiveBroadcastAsync` davranışı değişmez.
  - `internal static BroadcastLookup ClassifyFailure(Exception ex)`: `GoogleApiException` 401, 403, 429 → `NeedsAttention` + ipucu; `Google.Apis.Auth.OAuth2.Responses.TokenResponseException` → `NeedsAttention` + oturum ipucu; öbürleri → `Failed` (mesajı günlüğe, satıra değil).
  - İpuçları (birebir): hesap bağlı değil `YouTube hesabı bağlı değil. Ayarlar > YouTube'dan bağla.`; oturum `YouTube oturumunun süresi doldu. Ayarlar > YouTube'dan yeniden bağlan.`; 403 `Bu YouTube hesabının kanalın canlı yayınını okuma yetkisi yok. Ayarlar > YouTube'dan doğru hesapla bağlan.`; kota `Günlük YouTube API kotası doldu; sohbet yarına kadar okunamayabilir.`
  - `public sealed class YouTubeSeenMessages { public bool TryAdd(string id); }` — son 5000 kimlik, FIFO, iş parçacığı güvenli.
  - `public enum YouTubeIngestorEnd { Cancelled, BroadcastEnded, Failed }`
  - `public interface IYouTubeChatIngestor : IDisposable { Task StartAsync(CancellationToken ct); Task StopAsync(CancellationToken ct); Task Completion { get; } YouTubeIngestorEnd EndReason { get; } }` — `YouTubeOfficialChatIngestor` uygular. `EndReason`: `offline_at` ve `FAILED_PRECONDITION` → `BroadcastEnded`; öbür hata → `Failed`; iptal → `Cancelled`.
  - `YouTubeOfficialChatIngestor` kurucusuna sona `YouTubeSeenMessages? seen = null` (verilmezse kendi örneği); `StartAsync` canlı sohbet kimliği bulamazsa bugünkü `InvalidOperationException` yerine `YouTubeNoLiveChatException : Exception` fırlatır.

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
// YouTubeModerationServiceTests
[Theory]
[InlineData(401, BroadcastLookupStatus.NeedsAttention)]
[InlineData(403, BroadcastLookupStatus.NeedsAttention)]
[InlineData(429, BroadcastLookupStatus.NeedsAttention)]
[InlineData(500, BroadcastLookupStatus.Failed)]
public void ClassifyFailure_maps_google_errors(int status, BroadcastLookupStatus expected)
[Fact] public void ClassifyFailure_treats_a_revoked_grant_as_needs_attention()   // TokenResponseException
[Fact] public void ClassifyFailure_treats_a_network_error_as_failed()            // HttpRequestException

// YouTubeSeenMessagesTests
[Fact] public void First_sighting_is_new_and_the_second_is_not()
[Fact] public void Memory_forgets_the_oldest_beyond_5000()
```

- [ ] **Step 2: Çalıştır** — derleme hatası
- [ ] **Step 3: Tipleri ve yöntemleri yaz**
- [ ] **Step 4: Testi çalıştır** — geçer; `YouTubeOfficialMapperTests` geçer
- [ ] **Step 5: Commit** — `feat(youtube): tarayıcı açmayan yayın araması, kalıcı mesaj belleği, okuyucu bitiş sebebi`

---

### Task 5: YouTube servis döngüsü — erken bağlanma, yenileme, durum

**Files:**
- Modify: `OrderDeck.Chat/Ingestors/YouTube/YouTubeOfficialChatHostedService.cs`
- Test: `OrderDeck.Tests/Chat/YouTube/YouTubeOfficialChatHostedServiceTests.cs` (yeni)

**Interfaces:**
- Consumes: `BroadcastLookup`, `IYouTubeChatIngestor`, `YouTubeIngestorEnd`, `YouTubeSeenMessages`, `YouTubeNoLiveChatException`, `ChatReconnectSignal`, `ChatSourceMonitor`
- Produces:
  - Genel kurucu: `activeBroadcastResolver` parametresi kalkar; yerine `Func<bool, CancellationToken, Task<BroadcastLookup>>? broadcastLookup = null`; sona `ChatReconnectSignal? reconnect = null, ChatSourceMonitor? sources = null, TimeProvider? time = null`.
  - `internal sealed record YouTubeChatTimings(TimeSpan IdleWhenOffline, TimeSpan IdleAfterExit, TimeSpan FastResolveInterval, TimeSpan FastResolveWindow)`; `Default` = 60 sn, 30 sn, 5 sn, 2 dk.
  - `internal sealed record YouTubeIngestorRequest(string VideoId, string ApiKey, string? LiveChatId, YouTubeSeenMessages Seen);`
  - İç (test) kurucu: genel kurucunun parametreleri + `YouTubeChatTimings timings, Func<YouTubeIngestorRequest, IYouTubeChatIngestor> ingestorFactory`.
  - `ComputeBackoff` olduğu gibi kalır.

**Davranış:**
- Bütün beklemeler uyandırılabilir (Facebook'taki `_wake` + `IdleAsync` kalıbı, `TimeProvider` ile).
- `SessionStarted` ve yenileme isteği: hızlı arama penceresini açar (`now + FastResolveWindow`) ve döngüyü uyandırır. Yenileme ek olarak: sıradaki aramayı `fresh` yapar, art arda hata sayacını sıfırlar, süren arama çağrısını ve çalışan okuyucuyu keser, durumu `Connecting` bildirir.
- Kapılar sırayla, hepsi `Stopped` bildirip `IdleWhenOffline` bekler: deneme sürümü; kanal/video yazılı değil; oturum yok. API anahtarı yoksa `NeedsAttention` (`Bu kurulumda YouTube API anahtarı yok; sohbet okunamıyor.`).
- Ayarda video bağlantısı varsa arama yapılmaz. Kanal yazılıysa `broadcastLookup(fresh, ct)`: `Found` → okuyucu; `NoBroadcast` → `NoBroadcast` bildir, pencere içindeyse `FastResolveInterval`, değilse `IdleWhenOffline`; `NotConnected` ve `NeedsAttention` → `NeedsAttention` + ipucu, `IdleWhenOffline` (hızlı arama YOK); `Failed` → `Retrying(retryAt)`, `IdleWhenOffline`; `broadcastLookup` null → `NeedsAttention` (hesap bağlı değil ipucu).
- Okuyucu `StartAsync`'ten dönünce `Live`. `YouTubeNoLiveChatException` → `NeedsAttention` (`Ayarlardaki YouTube bağlantısı canlı bir yayına ait değil. Ayarlar > YouTube'a kanal adını ya da yeni yayının bağlantısını yaz.`), pencere içindeyse `FastResolveInterval`, değilse `IdleWhenOffline`; hata sayacı artmaz.
- Okuyucu bitince: `BroadcastEnded` → `NoBroadcast`, `IdleAfterExit`; `Failed` ya da istisna → sayaç artar, `Retrying(now + ComputeBackoff)`; yenilemeyle kesildiyse beklemeden başa döner; oturum bittiyse `Stopped`.
- Okuyucuya servis ömrünce yaşayan tek `YouTubeSeenMessages` verilir.
- Her yenileme isteği günlüğe bir satır: `[ChatReconnect] youtube: {ne yapıldı}`.

- [ ] **Step 1: Başarısız testleri yaz** (sahte okuyucu, betiklenen arama, `ManualTime`, kısa süreli `YouTubeChatTimings`; bekleme `TestPatience.Upper`'a kadar koşul yoklayan `Until`)

```csharp
[Fact] public async Task Session_start_wakes_the_idle_loop_and_connects_without_waiting()
[Fact] public async Task No_broadcast_is_retried_at_the_fast_interval_inside_the_window_then_at_the_offline_interval()
[Fact] public async Task A_missing_account_never_opens_a_browser_and_is_not_retried_fast()
// arama NotConnected döner → durum NeedsAttention + ipucu; pencere içinde bile ikinci arama IdleWhenOffline'dan önce gelmez
[Fact] public async Task Refresh_cuts_the_running_ingestor_and_reconnects_immediately_with_a_fresh_lookup()
// 1. okuyucu Live; Request(); 1. okuyucu durduruldu, arama fresh=true ile çağrıldı, 2. okuyucu kuruldu, durum yeniden Live
[Fact] public async Task Refresh_cancels_a_lookup_that_is_still_running()
[Fact] public async Task Refresh_resets_the_crash_backoff()
[Fact] public async Task Repeated_refreshes_leave_exactly_one_ingestor_running()
[Fact] public async Task Every_ingestor_gets_the_same_seen_message_memory()
[Fact] public async Task A_finished_broadcast_reports_no_broadcast_and_a_failure_reports_retrying_with_a_time()
[Fact] public async Task A_fixed_video_that_is_not_live_needs_attention_without_escalating_backoff()
[Fact] public async Task Session_end_stops_the_ingestor_and_reports_stopped()
[Fact] public async Task Refresh_racing_with_session_end_leaves_no_ingestor_and_ends_stopped()
[Fact] public async Task Trial_mode_and_missing_handle_report_stopped()
```

- [ ] **Step 2: Çalıştır** — derleme hatası
- [ ] **Step 3: Döngüyü yeniden yaz**
- [ ] **Step 4: Testi çalıştır** — 13 test geçer; `LongBroadcastStressTest` geçer
- [ ] **Step 5: Commit** — `feat(youtube): oturum başlar başlamaz bağlan, yenilemede baştan kur, durumu bildir`

---

### Task 6: Facebook servis döngüsü — yenileme, durum, yetki hatası

**Files:**
- Modify: `OrderDeck.Chat/Ingestors/Facebook/FacebookChatHostedService.cs`, `OrderDeck.Chat/Ingestors/Facebook/FacebookLiveVideoResolver.cs`
- Test: `OrderDeck.Tests/Chat/Facebook/FacebookChatHostedServiceLoopTests.cs` (yeni), `OrderDeck.Tests/Chat/Facebook/FacebookLiveVideoResolverTests.cs` (yeni)

**Interfaces:**
- Consumes: `ChatReconnectSignal`, `ChatSourceMonitor`, `InstagramGraphError.Classify`
- Produces:
  - `public enum FacebookLiveLookupStatus { Live, NotLive, AuthProblem, Failed }`; `public readonly record struct FacebookLiveLookup(FacebookLiveLookupStatus Status, string? VideoId = null);`
  - `FacebookLiveVideoResolver.ResolveDetailedAsync(string pageId, string pageAccessToken, CancellationToken ct)` → `Task<FacebookLiveLookup>`. `AuthProblem`: Graph'ın oturum/izin hatası (`InstagramGraphError.Classify` kalıcı hata dediğinde). `ResolveAsync` bunun üstüne kurulur ve bugünkü sözleşmesini korur (canlı değilse ya da hata varsa null).
  - Servisin genel kurucusuna sona `ChatReconnectSignal? reconnect = null, ChatSourceMonitor? sources = null, TimeProvider? time = null`.
  - `internal sealed record FacebookChatTimings(TimeSpan IdleWhenOffline, TimeSpan IdleAfterStreamExit, TimeSpan FastResolveInterval, TimeSpan FastResolveWindow, TimeSpan WatchdogInterval, TimeSpan StaleWindow)`; `Default` bugünkü sabitler. İç (test) kurucu bunu alır.

**Davranış:**
- Zaman `TimeProvider`'dan; mevcut `IdleAsync` ve bekçi beklemesi onu kullanır.
- Ana döngü `ResolveDetailedAsync` kullanır: `AuthProblem` → `NeedsAttention` (`Facebook oturumu ya da izni geçersiz. Ayarlar > Facebook > "Facebook izinlerini güncelle".`), `IdleWhenOffline`; `NotLive` ve `Failed` → bugünkü "canlı değil" yolu, `NoBroadcast` bildirilir. Bekçi `ResolveAsync`'i kullanmayı sürdürür.
- Durum: deneme sürümü, Sayfa bağlı değil, oturum yok → `Stopped`; okuyucu başlayınca `Live`; çıkış sınıflandırmasına göre: kanıtlı bitiş → `NoBroadcast`; geri çekilme → `Retrying(now + süre)`; iptal sonrası hızlı yeniden bağlanma → `Connecting`.
- Yenileme isteği: hızlı pencereyi açar, bayat yayın kara listesini temizler, hata sayacını sıfırlar, çalışan okuyucuyu keser, döngüyü uyandırır, `Connecting` bildirir, günlüğe `[ChatReconnect] facebook: …` yazar.

- [ ] **Step 1: Başarısız testleri yaz** (`FacebookOAuthTestKit` + `FakeGraph`; `live_videos` ve yorum uçları betiklenir)

```csharp
// FacebookLiveVideoResolverTests
[Fact] public async Task Live_video_is_found()
[Fact] public async Task No_live_video_is_not_live()
[Fact] public async Task Invalid_token_is_an_auth_problem_but_the_plain_call_still_returns_null()
[Fact] public async Task Server_error_is_failed()

// FacebookChatHostedServiceLoopTests
[Fact] public async Task Live_page_reports_live_once_the_poller_starts()
[Fact] public async Task Invalid_session_needs_attention_instead_of_waiting_for_a_broadcast()
[Fact] public async Task Refresh_cuts_the_poller_clears_the_stale_blacklist_and_rebinds()
[Fact] public async Task Refresh_keeps_the_seen_comment_memory()   // aynı yorum ikinci kez yayımlanmaz
[Fact] public async Task Refresh_racing_with_session_end_ends_stopped()
[Fact] public async Task No_page_or_no_session_reports_stopped()
```

- [ ] **Step 2: Çalıştır** — derleme hatası
- [ ] **Step 3: Çözümleyiciyi ve servisi genişlet**
- [ ] **Step 4: Testi çalıştır** — yeni testler ve mevcut `FacebookChatHostedServiceTests` geçer
- [ ] **Step 5: Commit** — `feat(facebook): yenilemede baştan bağlan, durumu bildir, geçersiz oturumu ayırt et`

---

### Task 7: Instagram — oturum başlangıcında ve yenilemede uyanma

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramWake.cs`
- Modify: `OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs`, `InstagramAccountLoop.cs`, `InstagramHealthMonitor.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.Wake.cs` (yeni partial), `OrderDeck.Tests/Chat/Instagram/InstagramHealthMonitorTests.cs`

**Interfaces:**
- Consumes: `ChatReconnectSignal`
- Produces:
  - `internal sealed class InstagramWake : IDisposable { public void Signal(); public Task<bool> DelayAsync(TimeSpan duration, TimeProvider time, CancellationToken ct); }` — `DelayAsync`: süre dolarsa false, `Signal` gelirse true döner; `ct` iptalinde fırlatır. `Signal` o an bekleyen bütün `DelayAsync` çağrılarını uyandırır.
  - `InstagramLoopServices` kaydına sona `InstagramWake Wake`.
  - Servisin iki kurucusuna `ChatReconnectSignal? reconnect` (genel kurucuda sona, varsayılan null; iç kurucuda `timings`'ten sonra).
  - `InstagramHealthMonitor.MarkRetrying(string pageId)` — girdiyi durumuna bakmadan `Starting`'e alır, `Detail` ve `RetryAt`'i temizler, `WasLiveThisSession`'ı korur; bilinmeyen Sayfa yok sayılır.

**Davranış:**
- Servis `SessionStarted`'ı da dinler; o ve yenileme isteği `Wake.Signal()` çağırır.
- Üst döngünün üç beklemesi (`IdleWhenOffline` × 2, oturum sonrası `IdleAfterPollerExit`) `Wake.DelayAsync` ile yapılır.
- Hesap döngüsünün beklemeleri (`IdleAsync`, `WaitUnlessTokenChangedAsync`, `WaitWhileOwnedByAsync` adımları) `Wake.DelayAsync` ile yapılır; uyanınca bekleme biter, döngü `Health.MarkRetrying(_pageId)` çağırıp turu baştan başlatır. `WaitUnlessTokenChangedAsync` uyanınca true döner.
- Çalışan okuyucuya dokunulmaz; okuyucunun içindeki hız sınırı beklemesi değişmez.
- Her yenileme isteği günlüğe bir satır: `[ChatReconnect] instagram: {uyandırılan döngü sayısı}`.

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
// InstagramMultiAccountHostedServiceTests.Wake.cs
[Fact] public async Task Session_start_wakes_the_service_without_waiting_for_the_offline_interval()
// servis oturumsuz başlar, IdleWhenOffline uzun (ör. 5 dk); StartSession() → okuyucu başlar
[Fact] public async Task Refresh_retries_an_account_whose_resolve_failed()
// çözümleme hata verir → ResolveFailed; ağ düzelir; Request() → IdleAfterFatal dolmadan Live
[Fact] public async Task Refresh_marks_a_waiting_account_as_connecting()
[Fact] public async Task Refresh_leaves_a_healthy_poller_and_its_watermark_alone()
// canlı hesap; Request(); okuyucu yeniden kurulmaz (PollerStarts aynı), yorum kaybolmaz ve yinelenmez
[Fact] public async Task Refresh_does_not_cut_a_rate_limit_wait()

// InstagramHealthMonitorTests
[Fact] public void MarkRetrying_moves_any_state_to_starting_and_keeps_session_liveness()
[Fact] public void MarkRetrying_ignores_an_unknown_page()
```

- [ ] **Step 2: Çalıştır** — derleme hatası
- [ ] **Step 3: `InstagramWake`'i yaz, servisi ve döngüyü bağla**
- [ ] **Step 4: Testi çalıştır** — yeni testler ve bütün `OrderDeck.Tests.Chat.Instagram` geçer
- [ ] **Step 5: Commit** — `feat(instagram): oturum başlangıcında ve yenilemede bekleyen döngüleri uyandır`

---

### Task 8: Görünüm modeli — birleşik şerit, yenileme komutu, kenar çubuğu, üst nokta

**Files:**
- Create: `OrderDeck.App/ViewModels/MainShellViewModel.ChatStatus.cs`, `OrderDeck.App/Services/Chat/ChatRefreshTracker.cs`
- Modify: `OrderDeck.App/ViewModels/MainShellViewModel.cs` (kurucu, `UpdateChatHealth`, `RefreshConnections`), `MainShellViewModel.InstagramHealth.cs`, `OrderDeck.Tests/App/MainShellTestHarness.cs`
- Test: `OrderDeck.Tests/Services/Chat/ChatRefreshTrackerTests.cs`, `OrderDeck.Tests/App/MainShellChatStatusTests.cs`, mevcut `MainShellInstagramHealthTests.cs`, `MainShellConnectionsTests.cs`

**Interfaces:**
- Consumes: `ChatSourceMonitor`, `ChatReconnectSignal`, `ChatSourcePresenter`, `ChatStatusRow`, `InstagramHealthPresenter.Evaluate(..., otherSourceLive)`
- Produces:
  - `public enum ChatRefreshPhase { Idle, Busy, Done }`
  - `public sealed class ChatRefreshTracker` — `ChatRefreshTracker(TimeSpan minimum, TimeSpan maximum, TimeSpan doneFor)`; `ChatRefreshPhase Phase { get; }`; `void Start(DateTimeOffset now)`; `void Observe(DateTimeOffset now, bool anyConnecting, bool anyProblem)`; `void Cancel()`. `Busy` → (`now - başlangıç >= minimum` ve bağlanan yoksa, ya da `>= maximum`) → sorun yoksa VE tavana takılmadıysa `Done`, yoksa `Idle`; `Done` → `doneFor` sonra `Idle`.
  - `MainShellViewModel` kurucusuna sona: `OrderDeck.Core.Chat.ChatSourceMonitor? chatSources = null, OrderDeck.Core.Chat.ChatReconnectSignal? chatReconnect = null`. `MainShellTestHarness.Build`'e aynı iki isteğe bağlı parametre.
  - `public ObservableCollection<ChatStatusRow> ChatStatusRows { get; }`; `bool ShowChatStatus`; `bool IsRefreshingChat`; `bool ChatRefreshDone`; `RefreshChatCommand` (çalıştırılabilir: oturum açık VE yenileme sinyali verilmiş VE `Idle`); `internal void TickChatRefresh()`.
  - Instagram satırları `ChatStatusRow`'a: `Key = "instagram:" + PageId`; ilk Instagram satırında `GroupLabel = "Instagram"`; `DisplayText` canlıysa `Label`, değilse `Text`.

**Davranış:**
- `UpdateChatHealth` içinde: platform anlık görüntüsünden "bir platform canlı mı" çıkarılır → Instagram değerlendirilir (`otherSourceLive`) → platform satırları değerlendirilir (`otherSourceLive` = Instagram'da canlı hesap var mı) → `ChatStatusRows` sırayla platform satırları + Instagram satırları. Koleksiyon bugünkü Instagram şeridindeki gibi yerinde güncellenir (eşit satıra dokunulmaz).
- `ShowChatStatus` = oturum açık. `ShowInstagramStatus` ve `InstagramAccounts` bugünkü anlamıyla kalır (mevcut testler).
- Üst nokta: platform sunucusunun ve Instagram'ın `TopDotTooltip`'leri (boş olmayanlar) satır sonuyla birleşir; biri varsa bugünkü Instagram kuralı uygulanır.
- `RefreshConnections`: izleyici verildiyse `youtube`, `facebook`, `tiktok` satırlarında `IsConnected` = o platform `Live`; `ShowViewerCount` = bağlı VE izleyici kaydı var. Verilmediyse bugünkü davranış.
- `RefreshChat`: izleyici `Start(now)`, `ChatReconnectSignal.Request()`, 500 ms'lik zamanlayıcı başlar. Her tıkta `TickChatRefresh`: durum satırları yeniden değerlendirilir (ayar dosyası OKUNMAZ), `Observe` çağrılır, `IsRefreshingChat` ve `ChatRefreshDone` evreden türetilir; `Idle` olunca zamanlayıcı durur. Oturum biterse `Cancel`.
- Zaman `InstagramNow` dikişinden.

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
// ChatRefreshTrackerTests (saf)
[Fact] public void Stays_busy_for_at_least_the_minimum_even_when_nothing_is_connecting()
[Fact] public void Ends_done_when_everything_settled_without_problems()
[Fact] public void Ends_idle_without_a_check_mark_when_a_problem_remains()
[Fact] public void Gives_up_at_the_maximum_without_a_check_mark()
[Fact] public void Done_returns_to_idle_after_its_display_time()
[Fact] public void Cancel_returns_to_idle_at_once()

// MainShellChatStatusTests
[Fact] public void Strip_lists_platform_rows_then_instagram_rows_with_the_group_label_on_the_first()
[Fact] public void Healthy_rows_show_only_their_name()
[Fact] public void Strip_is_visible_for_the_whole_session_and_hidden_without_one()
[Fact] public void Instagram_account_that_drops_while_youtube_is_live_turns_yellow()
[Fact] public void Refresh_requests_a_reconnect_and_goes_busy()
[Fact] public void Refresh_cannot_run_without_a_session_or_while_busy()
[Fact] public void Refresh_ends_with_a_check_mark_when_no_problem_row_remains()
[Fact] public void Refresh_ends_without_a_check_mark_when_a_dim_problem_row_remains()
[Fact] public void Ending_the_session_cancels_a_running_refresh()
[Fact] public void A_platform_warning_turns_the_top_dot_yellow_with_its_sentence()
[Fact] public void Sidebar_rows_follow_the_monitor_and_facebook_can_be_connected()
```

- [ ] **Step 2: Çalıştır** — derleme hatası
- [ ] **Step 3: İzleyiciyi ve görünüm modelini yaz**
- [ ] **Step 4: Testi çalıştır** — yeni testler, `MainShellInstagramHealthTests`, `MainShellConnectionsTests` geçer
- [ ] **Step 5: Commit** — `feat(shell): birleşik sohbet durum şeridi ve Sohbeti yenile komutu`

---

### Task 9: Görünüm — ikonlar, şerit, düğme

**Files:**
- Modify: `OrderDeck.App/Themes/Icons.xaml`, `OrderDeck.App/Views/Shell/ChatPanel.xaml`
- Test: `OrderDeck.Tests/App/ChatPanelStatusStripTests.cs` (yeni)

**Interfaces:**
- Consumes: `ChatStatusRows`, `ShowChatStatus`, `IsRefreshingChat`, `ChatRefreshDone`, `RefreshChatCommand`, `controls:ButtonState.IsBusy`
- Produces:
  - `OD.Path.Refresh` (iki oklu): `F1 M4.552,7.7 A8.6,8.6 0 0 1 19.448,7.7 L17.543,8.8 A6.4,6.4 0 0 0 6.457,8.8 Z M20.195,11.194 L21.093,6.75 L15.897,9.75 Z M19.448,16.3 A8.6,8.6 0 0 1 4.552,16.3 L6.457,15.2 A6.4,6.4 0 0 0 17.543,15.2 Z M3.805,12.806 L2.907,17.25 L8.103,14.25 Z`
  - `OD.Path.Check`: `M4.722,13.278 L10.04,18.6 L19.32,8.233 L17.68,6.767 L9.96,15.4 L6.278,11.722 Z`
  - Şerit (`ChatPanel.xaml` satır 1): `ShowChatStatus` ile görünür. Sağda, üste yaslı yenile düğmesi (adı `RefreshChatButton`): `OD.Button.Ghost`, 24×24, dolgu 0, içerik ortada; içerik `OD.IconPath` (`OD.Icon.Md`), verisi `ChatRefreshDone` doğruyken `OD.Path.Check`, değilken `OD.Path.Refresh`; `controls:ButtonState.IsBusy` = `IsRefreshingChat`; ipucu ve `AutomationProperties.Name` = `Sohbet bağlantılarını yenile`. Kalan yerde `ChatStatusRows` için `WrapPanel`'li `ItemsControl` (adı `ChatStatusList`): her öğe yatay: (varsa) `GroupLabel` (`OD.Text.Micro`, sağında 8 px), 8 px nokta (`Live` → `Success`, `Warning` → `Amber`, yoksa `TextMute`), `DisplayText` (`OD.Font.F1`, `TextDim`); öğenin ipucu `Tooltip`; öğeler arası boşluk SAĞDA (alt satır soldan hizalı başlasın).

- [ ] **Step 1: Başarısız testleri yaz** (`ThemeTestHost.RunOnSta`, ekran dışı pencere, `MainShellTestHarness`)

```csharp
[Fact] public void Strip_and_button_are_hidden_without_a_session()
[Fact] public void Button_sits_at_the_right_end_on_the_first_line_of_the_strip()
[Fact] public void Header_keeps_its_layout()                       // arama kutusunun genişliği şeritten bağımsız
[Fact] public void At_half_screen_the_strip_wraps_inside_the_panel_and_lines_start_flush_left()   // 400 px
[Fact] public void Instagram_label_stays_with_its_first_account()  // aynı öğenin içinde
[Fact] public void Dot_colour_follows_severity()
[Fact] public void Button_is_busy_while_refreshing_and_shows_the_check_mark_when_done()
[Fact] public void Icon_geometries_exist_and_fit_the_24_box()
```

- [ ] **Step 2: Çalıştır** — FAIL
- [ ] **Step 3: İkonları ve şeridi yaz** (bugünkü Instagram şeridi bu şeride dönüşür)
- [ ] **Step 4: Testi çalıştır** — yeni testler, `ChatPanelLayoutTests`, `UnstyledControlGuardTests`, `ThemeMergeTests` geçer
- [ ] **Step 5: Commit** — `feat(shell): sohbet durum şeridi ve yenile düğmesi`

---

### Task 10: Bağlama, tüm paket, saha denemesi

**Files:**
- Modify: `OrderDeck.App/AppHost.cs`
- Test: `OrderDeck.Tests/App/ChatStatusDiTests.cs` (yeni; `InstagramHealthDiTests` kalıbı)

**Interfaces:**
- Consumes: bütün önceki görevler
- Produces: `ChatReconnectSignal` ve `ChatSourceMonitor` tekil kayıtları; izleyici kaydı `StreamSessionService.SessionStarted`'a `ResetSession` bağlar; köprü, üç sohbet servisi ve görünüm modeli AYNI örnekleri alır; YouTube servisine `broadcastLookup: (fresh, ct) => moderation.LookupActiveBroadcastAsync(fresh, ct)`.

- [ ] **Step 1: Başarısız testi yaz**

```csharp
[Fact] public void Shell_bridge_and_services_share_one_signal_and_one_monitor()
[Fact] public void Session_start_resets_session_liveness_in_the_monitor()
```

- [ ] **Step 2: Çalıştır** — FAIL
- [ ] **Step 3: `AppHost`'u bağla**
- [ ] **Step 4: Tüm paketi çalıştır** — `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` → tümü geçer
- [ ] **Step 5: Commit** — `feat(chat): yenileme sinyalini ve durum izleyicisini bağla`
- [ ] **Step 6: Görsel ve saha doğrulaması (depoya girmez)**
  - Şeridin ve düğmenin gerçek görünümü, onaylanan çizimle (yerleşim 2) karşılaştırılır.
  - Burak'la, gerçek yayında: her platformda "yayını kapat, yeniden aç, Yenile'ye bas"; önce YouTube. Günlükte `[ChatReconnect]` satırları ve şerit beklenen sırayla değişir.
