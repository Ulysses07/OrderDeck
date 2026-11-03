# Ortak Bakiye (Kişi Havuzu) — W-havuz (WPF) Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ortak bakiye spec'inin (`docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md`) §12 **W-havuz** satırını (spec §6.1 a–h) WPF istemcisine getirmek: preview ve apply'a (replay dahil) kişinin GUID'li üyeleri `memberIds` olarak gider; sahip kuralı "GUID **ve** sunucunun kabul edeceği satır" olur (`IsServerAcceptable` Core'a taşınır, `PaymentPersonMember`'a `Platform`/`Username`); 409 `customer-not-synced` yeni `BalanceNotSynced` sonucuna döner ("Henüz eşitlenmedi", 5 dakika metni, `MarkNoBalance` yok) ve fire-and-forget bir müşteri senkron turu (`CustomerChangesPullService.PullOnceAsync`) tetikler; birleştirme notu üç durumludur (`PaymentPrompt.PersonPool`, yerel planda her zaman `null`); "Ayır" bakiye dağılımını gösterip onay ister; GUID'siz satırdan açılan müşteri kartı kişinin sahip satırının bakiyesini okur; `PersonJob` kalan iş gerekçesi güncellenir. **Sunucu, panel ve Shopper değişmez; satır kipinde (bugünkü bütün lisanslar) davranış yalnız spec'in saydığı yerlerde değişir** (sahip kuralı, Ayır onayı, kart düzeltmesi; ayrıca yerel planın not metni — fark 12; sahip kuralının iki sonucu: mod On'da çevrimdışı yol — fark 26, sahip adayı olmayan kişide kişi × kapsam işinin açılmaması — fark 27). Göç yok.

**Architecture:** Yeni davranışların hepsi sunucu sinyaline bağlıdır: `memberIds`'i satır kipindeki sunucu okumaz, `customer-not-synced` ve `personPool: true` yalnız havuz kipinden gelir. Üye listesi tek özel yardımcıda (`PaymentRequestService.MemberIdsOf`) akışın kişisinden kurulur — tıklamanın girişinde okunan, kilitleri ve kiraları tutulan anlık görüntü (`PersonHold`); `SettleScopeAsync` onu dört özel yöntemle aşağı geçirir, tıklama içinde depo yeniden okunmaz (fark 4); önizleme ve `ReplayAsync` (taze apply, belirsiz işin ve miras işlerin oynatması) aynı yardımcıyı kullanır. `customer-not-synced` bayrağı denemenin sonucuyla (`ApplyAttempt.NotSynced`) ve bakiye sonucuyla (`BalanceOutcome.NotSynced`) `SendCoreAsync`'e taşınır; sonuç orada `BalanceNotSynced` olur, 5 dakika kuralı kişi anahtarı (`PaymentPerson.MemberKey`) başına bellekte tutulur. Senkron turu `RequestPaymentAsync`'te koşu döndükten (kişi kilitleri ve müşteri kirası bırakıldıktan) sonra üç durumlu sayaçla (en çok bir koşan + bir bekleyen tur; sıra doluyken tetik düşer) başlatılır. Sahip kuralı saf Core fonksiyonudur; senkron servisi ve sahip seçimi aynı `ServerCustomerRule.IsServerAcceptable`'ı kullanır. Ayır onayı panel `GET …/balance` yanıtının `members[]`'ini yerel kart üyelerine eşler. İstemci DTO'ları `OrderDeck.Licensing`'te genişler; eski sunucu yeni alanı göndermezse varsayılan değer bugünkü davranışı verir.

**Tech Stack:** WPF (.NET 10, `net10.0-windows`), CommunityToolkit.Mvvm 8.4.2, Dapper + SQLite (yerel), System.Text.Json (camelCase, `LicenseApiClient.JsonOpts`), xunit 2.9.3 + FluentAssertions 7.2.2 + Moq, `TimeProvider` (test sahtesi elle).

**Terimler:** "iskelet", bu planın görevlerden önceki ortak bölümleridir (farklar, kurallar, dosya haritası, imzalar, sahip soruları); "fark N" oradaki "Spec ↔ kod farkları" listesinin maddesidir. Bazı görev notlarındaki "talimat"/"brief", görev yazılırken verilen kısa tanımdır; plana alınmadı, ondan bilinçli sapmalar görevin notunda gerekçesiyle yazılıdır. "Saha notu N", planın sonundaki "Sürüm / saha notları"nın maddesidir.

---

## Spec ↔ kod farkları (master `7960eba3`, 2026-11-02)

Spec 11.10 kesitine göre yazıldı; arada satış senkronu (W3-06…W3-11), #506 sonrası işler, ortak bakiye S1 (#533) ve S2 (#553) aynı dosyaları değiştirdi. Her madde için önerilen çözüm planda uygulanmıştır; Burak farklı karar verirse ilgili görev değişir. Gerçek sahip soruları en sondaki "Sahip soruları"nda.

1. **Satır numaraları kaydı.** Plan bugünkü numaraları kullanır (worktree `7960eba3`). `OrderDeck.Licensing/Api/LicenseApiClient.cs`: `GetBalancePreviewAsync` `:789-796` (spec `:392-399`), `ApplyBalanceAsync` `:843-863` (spec `:441-446`; 409 gövdesini artık kendisi okur, `:856-862`), `GetCustomerBalanceAsync` `:880-888` (spec `:463-471`), `MapProblem` `:1210-1222` (4xx → `ValidationException(Code = title)`). `OrderDeck.Licensing/Api/Models/CustomerBalanceDtos.cs`: `CustomerBalanceApplyRequest` `:18-23`, `CustomerBalanceScope` `:32-36`, `CustomerBalanceDetailsResponse` `:65-67`. `OrderDeck.App/Services/PaymentRequestService.cs`: `PaymentRequestResult` `:15-70`, `PaymentPrompt` `:92-107` (`RefundsToOtherRow` param yorumu `:79-81` = spec `:71-73`), `PaymentRequestOutcome` `:125-130`, ctor `:177-203`, `RequestPaymentAsync` `:332-357`, bakiye kapısı `SendCoreAsync` `:694-714` (`Uncertain` → `BalanceUncertain` `:709-710`), `BalanceOutcome` `:870-885` (spec `:619-630`), `ApplyAttempt` `:895-896`, `ReconcilableLegacies` `:958-973`, `ResolveBalanceAsync` `:986-1026` (yerel planın sorusu `:1007-1008` = spec `:738`), `WithLocalPlan` `:1044-1071` (spec'te yok), `PromptOf` `:1075-1095` (spec `:760-768`), `SettleScopeAsync` `:1100-1279` (miras belirsiz dönüşü `:1151-1152`, belirsiz işin dönüşü `:1231-1232`), `ReviseAndSettleAsync` `:1286-1432`, önizleme `:1400` + genel `catch` `:1403-1408` (spec `:1034`, `:1037-1042`), `MarkNoBalance` `:1411`, `ReconcilePersonScopeAsync` üye süzgeci `:1470` (spec `:1096`), izlerin sorusu `:1511-1514` (spec `:1127-1131`), `SettleAsync` `:1616-1635`, `ReplayOnceMoreIfStaleAsync` `:1640-1648`, `LegacyScopeDeductionPrompt` `:1667-1670`, `ClassifySettledJob` `:1704-1728`, `ReplayAsync` `:1780-1852` (spec `:1315-1362`; genel kol `:1847-1850` = spec `:1357-1360`), scope/durum/reverse çağrıları `:1488`/`:1588`/`:1899` (spec `:1105`/`:1198`/`:1409`). `OrderDeck.App/Services/PaymentResultPresenter.cs`: `Notify(outcome)` `:35-48`, `Notify(result)` `:57-115` (spec `:47-60`; `BalanceUncertain` kolu `:73-86`), `PromptText` belgesi `:148-167` (iade cümlesi `:153-155`; düzenleme bloğu `:150-155`), `WithOtherRowNote` `:240-244` (spec `:185-189`). `OrderDeck.Core/Customers/PaymentPerson.cs` aynı (`PaymentPersonMember` `:11-16`, `IsOwnerCapable` `:108`, `PickOwner` `:122-124`). `OrderDeck.Core/Storage/Repositories/PaymentPersonSql.cs` `:24-37`, `Columns` `:39-40`. `CustomerRepository.GetPaymentPerson` `:601-606` (spec `:607-612`), `UnmergeGroup` `:610-616` (spec `:616-622`). `PersonJob.cs` kalan iş yorumu `:75-80`, `RefundsToOtherRow` `:145-146` + belgesi `:212-214`. `WpfCustomerProjectionSyncService.IsServerAcceptable` `:103-113`, kullanımı `:215-222`. `CustomerChangesPullService.PullOnceAsync` `:167-172` (spec `:159`), turun gönderimi `:258` (spec `:226`), `_tourGate` `:125`. `CustomerSearchViewModel.UnmergeGroup` `:265-272` (spec `:258-265`), ctor `:118-134`. `CustomerDetailViewModel.TryGetProjectionId` `:229-239` (spec `:195-200`), `ReloadBalanceAsync` `:241-267` (spec `:202-228`), "server eşlemesi yok" `:246` (spec `:207`) ve Bakiye Ekle'nin `MessageBox` metni `:274-277`, `AddBalanceAsync` `:269-290` (spec `:230-250`), `AddBalanceDrawer.xaml.cs` hedefi `:106-125` (değişmez).
2. **`IsOwnerCapable`'ın Id-dizesi çağıranları (spec §6.1-a).** Spec `IsOwnerCapable`'ı "GUID ve sunucunun kabul edeceği satır" yapar; ama aynı yüklem Id dizesiyle üç yerde "sunucuya Id olarak gidebilir mi" sorusuna cevap verir (`PaymentRequestService.cs:963` miras işler, `:1470`/`:1474` kapsam adayları) ve yeni `memberIds` süzgeci de bu anlamı ister (silinmiş üyeler dahil). **Çözüm:** `IsOwnerCapable(string)` GUID "N" olarak kalır; yeni `PaymentOwnerRule.IsOwnerCandidate(PaymentPersonMember)` = `IsOwnerCapable(Id) ∧ ServerCustomerRule.IsServerAcceptable(Platform, Username)`; yalnız `PickOwner` onu kullanır. `Order` değişmez (ilk anahtar `IsOwnerCapable`): adaylar arasındaki sıra aynı olduğu için seçilen sahip eşdeğerdir, `MemberIds` sırası ve `ContactPhone` yedeği değişmez (Görev 1).
3. **`PaymentPersonMember` konumsal kuruluyor.** Yeni alanlar sona varsayılanlı eklenir (`string? Platform = null, string? Username = null`); üretim kurucuları (`PaymentPersonSql`, `PaymentPerson.Single`) doldurur. Elle kurulan test üyeleri alan taşımazsa yeni kuralla sahipsiz kalır: `PaymentJobStoreContractTests.cs:947`, `:959` (InMemory fikstürü) ve `PaymentOwnerRuleTests.cs:64` (vektör kurucusu) SQLite fikstürünün (`:905`, `"instagram"`, `"u" + id[..10]`) değerlerini taşıyacak biçimde güncellenir — iddia değişmez, yalnız fikstür (Görev 1).
4. **Önizlemede ve `ReplayAsync`'te kişi yok; liste akışın kişisinden kurulur (spec §6.1-a "kişiyi zaten elinde tutar").** Önizleme `ReviseAndSettleAsync`'te (`:1400`), apply `ReplayAsync`'te (`:1800`) yalnız işi bilir; kişi `SettleScopeAsync`'te (`hold.Person`: tıklamanın girişinde okunan, kişi kilidi ve öbür üyelerin kiraları tutulan anlık görüntü, `EnterPersonAsync` `:496-526`; `FindOrCreateForPerson` `MemberKey`'i yazma işleminin içinde doğrular). **Çözüm:** `SettleScopeAsync` `person`'u dört özel yöntemle aşağı geçirir (`ReviseAndSettleAsync`, `SettleAsync`, `ReplayOnceMoreIfStaleAsync`, `ReplayAsync`; yeni parametre `licenseId`'nin ardında — `ReconcilePersonScopeAsync` deseni; dört yöntemin bütün çağıranları `SettleScopeAsync` ağacında, arka plan çağıranı yok); tek özel statik yardımcı `MemberIdsOf(Guid anchor, PaymentPerson person)` hem önizlemeyi hem `ReplayAsync`'i besler: `person.MemberIds`, `IsOwnerCapable` süzgeci (silinmişler ve sahip dahil), çapa (`anchor`) her zaman listede, ordinal tekil, "N" biçiminin ordinal sırası (belirlenimli). **Depo okunmaz.** Kira ve kişi kilidi akış ortasındaki grup değişikliğini durdurmaz: çekme turu var olan yerel satırı `isBusy`'ye bakmadan yazar (`CustomerSyncRepository.ApplyServerCustomer` `:160-177`; `isBusy` yalnız kimlik sahipleri kolunda, `:188`) ve `GroupId` senkronlanan birimdir (`OrderDeck.Core/Customers/CustomerUnitMerge.cs:53-56`). Çağrı anında depodan okunan liste, çapa akış ortasında başka kişiye geçtiyse (PC-B `a`'yı {a, b}'den ayırıp `x`'le birleştirdi, tur indi) `{a, x}` olurdu: sunucu bunu kendi `S(a)`'sıyla tutarlı bulur (S2 kural 3a/3b boş, `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs:156-165`), önizleme `x`'in bakiyesini gösterir, apply onu `b`'nin kartından açılan, {a, b}'nin toplamı ve telefonuyla kurulan mesaja harcar; yeni grubun toplamı ≤ 0 ise `MarkNoBalance` yanlış kesinleşir. Anlık görüntü `W = {a, b}` gidince sunucu ayrışmayı görür (3a: `b` `S(a)`'da değil) → 409 `customer-not-synced` (Görev 4: `BalanceNotSynced`, hiçbir şey düşülmez). Bayat görüntü her zaman 409 almaz: 409 yalnız para taşıyan ayrışmada gelir — `W`'nin `S` dışındaki canlı üyesi (3a) ya da `S`'nin `W` dışındaki, bakiyesi > 0 canlı üyesi (3b); `W`'nin sunucuda bilinmeyen ya da silinmiş üyesi atılır (kural 2 + K3, `LiveIds` `:219`), `S`'ye bakiyesi ≤ 0 bir canlı üyenin katılması da 409 vermez. 3a/3b'yi geçen bayat görüntüde çekim kümesi `W ∩ S`'nin canlıları ∪ `S`'nin silinmişleridir (kural 4, `BuildDrawSet` `:222-235`): para operatörün kişisinde olmayan hiçbir canlı satırdan çekilmez; `S`'nin silinmiş üyeleri K3 gereği `W`'den bağımsız olarak çekim kümesindedir (spec l.133), `S∖P`'nin eksi bakiyeli canlı üyeleri havuza sayılır ama çekilmez. Mod On'da liste kiranın `begin`'ine giden üye kümesiyle aynı kaynaktan gelir (`PaymentLeaseCoordinator.cs:585` `person.MemberIds`; spec §15-5, l.820). **Spec'ten bilinçli sapma:** spec önizleme için elde tutulan kişiyi söyler (l.408), `ReplayAsync` için "üyeleri çağrı anında `GetPaymentPerson(job.CustomerId)` ile okur" der (l.409). Plan bunu tıklama düzeyinde karşılar: kişi her tıklamanın girişinde depodan yeniden okunur, aynı tıklama içinde değişmez — iş açıldıktan sonra birleşen üye sonraki tıklamanın oynatmasında gider (spec'in amacı); tıklamanın içinde yeniden okumak yukarıdaki yanlış kişiden çekimi açar ve §15-5'i bozardı. Spec'in l.409'u bu tasarıma göre Görev 9 Adım 7.8'de güncellenir: merge sonrası spec ile kod aynı şeyi söyler, spec'e uyan sonraki bir değişiklik tıklama içi depo okumasını geri getirmez. Görev 9 Adım 10'un PR metni sapmayı adıyla sayar. Depo yoksa (`_customers` testte null) `ReadPerson` (`:529-533`) tek satırlık kişiye düşer (`PaymentPerson.Single`) → liste yalnız çapa (sunucu `a`'yı zaten `W`'ye ekler). `MemberIdsOf` saftır: depo okuması ve hata yolu yok; çağrılar yine var olan `try` bloklarının içinde (Görev 3).
5. **`customer-not-synced` gövdesinde satır Id'si yok.** Sunucu Id'leri yalnız kendi günlüğüne yazar (`LicensesCustomerBalanceApplyController.cs:1140-1149`). **Çözüm:** istemci yalnız `ValidationException.Code`'a bakar; 5 dakika günlüğüne kendi üye Id'lerini (`PaymentPerson.MemberIds`) yazar (Görev 4).
6. **`PaymentRequestService` `CustomerChangesPullService`'e bağlı değil (spec §6.1-b).** **Çözüm:** ctor'a isteğe bağlı son parametre `CustomerChangesPullService? customerPull = null` (`AppHost.cs:368` `AddSingleton<PaymentRequestService>()` kayıtlı tekili kendisi verir; döngü yok — akış servisi ödeme servisine bağlı değil). Test yüzeyi `internal Func<CancellationToken, Task>? CustomerTour` (ctor'da `customerPull.PullOnceAsync` yöntem grubu). `PullOnceAsync` tur kilidini **bekler**; spec'in iki cümlesi ("tur kilidi üst üste binmeyi önler; bekleyen bir tetik varken yenisi eklenmez") tetikleyicide üç durumlu `Interlocked` sayaçla kurulur: en çok bir koşan tur + bir sıradaki (bekleyen) tur; sıra doluyken gelen tetik düşer. Koşan turun gönderiminden sonra yapılan bir birleştirme/ayırma böylece 30 sn'lik döngüye kalmaz. Tetik `RequestPaymentAsync`'te `RequestPaymentOnceAsync` döndükten sonra: kişi kilitleri ve müşteri kirası bırakılmıştır — kira tutulurken çekilen öğe o üyeye düşseydi akış `Busy`'de dururdu (`CustomerChangesPullService.cs:403-410`) (Görev 5).
7. **`NotSynced`'in taşınması.** Spec bayrağı `BalanceOutcome`'a koyar. Apply tarafında `ReplayAsync`'in sonucu üç yoldan belirsiz dönüşe varır: miras işin oynatması (`:1151-1152`), belirsiz işin oynatması (`:1231-1232`), `SettleAsync` → `ClassifySettledJob` (`:1634`). **Çözüm:** `ApplyAttempt`'e `bool NotSynced = false`, `BalanceOutcome`'a `init` `NotSynced`; üç dönüş yeri ve önizlemenin yeni `catch`'i bayrağı taşır; `SendCoreAsync` `:709`'da `NotSynced` → `BalanceNotSynced`. `MarkNoBalance` yazılmaz (`no-balance`/`nothing-to-apply` kolu `:1806-1812` aynen) (Görev 4).
8. **Yeni sonucun tüketicileri.** `PaymentResultPresenter.Notify` iki aşırı yükleme; `CorrectedPaymentRequests.cs:537-556` `switch` (`_` kolu `outcome.Result.ToString()` gösterirdi); `PaymentLeaseCoordinator` sonuçla dallanmaz (değişiklik yok); banka W2 tüketicisi WPF'te yok (spec §6.1-b son madde, R18 — iş yok). **Çözüm:** K2 "WPF bunu `BalanceUncertain` gibi ele alır": fiyat düzeltmesinin toplu gönderimi `BalanceNotSynced`'i `BalanceUncertain` satırıyla aynı metne eşler (spec bu bağlam için metin vermiyor; yeni metin icat edilmez) (Görev 4).
9. **5 dakika kuralının kişi anahtarı ve sıfırlanması (spec "ilk görülme anı bellekte, kişi anahtarı başına").** **Mühendislik kararı:** anahtar `PaymentPerson.MemberKey` (#506'nın kişi anahtarı). Aynı üye kümesi aynı anahtarı üretir (`PaymentPerson.KeyOf` tekilleştirip sıralar, `OrderDeck.Core/Customers/PaymentPerson.cs:86-87`, master `7960eba3`; Görev 1 sonrası kayar): ayırıp aynı satırları yeniden birleştirmek anahtarı DEĞİŞTİRMEZ. Bu yüzden kayıt ilk ve son görülmeyi tutar; önceki 409'dan bu yana `NotSyncedStreakGap` (30 dk, Sahip sorusu 3) aşıldıysa ilk görülme yeniden başlar. Servis tekil, kayıt süreç ömrü boyunca bellekte; kayıt o kişinin bakiye akışı kesin bir sonuç ya da soru üretince silinir (ağ belirsizliği silmez); "uzun" = ilk görülmeden bu yana **> 5 dk**; süreler monoton saatle (`GetTimestamp`/`GetElapsedTime` — duvar saati NTP/elle düzeltmede sıçrar); saat `internal TimeProvider NotSyncedClock` (test sahtesi `ManualTimeProvider`) (Görev 4).
10. **`PaymentPrompt.PersonPool` nasıl eklenir.** Konumsal parametre bütün `new PaymentPrompt(…)` çağrılarını (servis, kira koordinatörü, testler) etkilerdi. **Çözüm:** `init` özellik `bool? PersonPool { get; init; } = false` (`AlsoConfirms` deseni): `false` = bugünkü metin, var olan kurulumlar ve testler değişmez. Yerel planın sorusu **iki** yerde kuruluyor — `PromptOf` (`:1075-1095`) ve satış senkronunun eklediği `WithLocalPlan` (`:1044-1071`, spec bilmiyor); ikisi de açıkça `PersonPool = null` (scope yanıtından önce kurulur). `LegacyScopeDeductionPrompt` (`:1667-1670`) ve kira koordinatörünün uzak sorusu `RefundsToOtherRow = false` taşır, not çıkmaz → dokunulmaz (Görev 6).
11. **Sunucu izlerinin farklı `PersonPool` taşıması** (yalnız akış ortasındaki `enable/disable` yarışında). **Mühendislik kararı:** bütün izler aynı değeri taşıyorsa o, değilse `null` (iki kipte de doğru nötr metin) (Görev 6).
12. **Satır kipinde yerel planın not metni değişir.** Spec §6.1 girişi satır kipindeki değişiklikleri (a), (e), (f) diye sayar; ama §6.1-d yerel planın sorusunda değeri **her zaman `null`** ister ("iki kipte de doğru nötr metin"). **Çözüm:** spec kazanır — yerel planın birleştirme sorusunun notu bugün de nötr metne döner (" Not: … iade o hesaba döner."; "bu istekte kullanılmaz" düşer). Yalnız metin; para davranışı değişmez. Sunucu izlerinin sorusu satır kipinde ve eski sunucuda `personPool` `false` (ya da alan yok → `false`) → bugünkü metin aynen (Görev 6). Spec'in iki satırı bu farkla bayatlar: §6.1 girişi (l.404, "bu üçü dışında bugünkü gibi çalışır") ve §7.1 adım 6 (l.496, "Satır kipinde Wh = #506 davranışı + Ayır onayı + kart düzeltmesi + sahip kuralı"). Görev 9 Adım 7.6/7.7 ikisine nötr notu ekler (yeni tasarım değil, bayat sayımın düzeltilmesi); metin farkı Burak'a saha notlarının 3. maddesiyle de bildirilir. Fark 26 ve 27 sahip kuralının (a) sonuçlarıdır, spec'in sayımında (a)'nın içinde kalır: o iki satıra ayrıca yazılmaz, PR metni (Görev 9 Adım 10) ve saha notu 3 onları adıyla sayar.
13. **Belge yorumu "`:125-127`" belirsiz.** Bugün o satırlar `PersonJob.cs`'te ve `PaymentResultPresenter.cs`'te ilgisiz yorumlar. **Çözüm:** `RefundsToOtherRow`'u anlatan üç yer kipe bağlı anlatıma güncellenir: `PaymentRequestService.cs:79-81` (param), `PaymentResultPresenter.cs:153-155` (`PromptText` belgesi; düzenleme bloğu `:150-155`), `PersonJob.cs:212-214` (`RefundsToOtherRow` belgesi); spec §6.1-g'nin kalan iş gerekçesi `PersonJob.cs:75-80` (Görev 6).
14. **Panel DTO'sunda `Members` yok, arama VM'i istemciye bağlı değil (spec §6.1-e).** `CustomerBalanceDetailsResponse` (`CustomerBalanceDtos.cs:65-67`) `members[]`'i okumuyor (sunucu S1'den beri gönderiyor, `PanelCustomerBalanceController.cs:68-73`); `CustomerSearchViewModel` ctor'unda `LicenseApiClient` yok. **Çözüm:** `CustomerBalanceMemberDto` + `CustomerBalanceMemberDto[]? Members = null` (Görev 2); VM ctor'una isteğe bağlı son parametre `LicenseApiClient? api = null` — DI verir (`AppHost.cs:354`, istemci tekil `:452`); null yalnız test/kompozisyonda ve bugünkü sorusuz yol (Görev 7).
15. **Onay düğmesi [Ayır] (spec §6.1-e).** `IDialogService.ConfirmAsync` (`IDialogService.cs:51`) → `MessageDrawer.ForConfirm` düğmesi sabit "Evet" (`MessageDrawer.xaml.cs:26`). **Çözüm:** `ConfirmAsync(string message, string title, string confirmText = "Evet")`; `MessageDrawer.ForConfirm(…, string confirmText = "Evet")`; var olan çağıran (`PaymentResultPresenter.cs:137`) değişmez; Moq ile sahtelenen yer yok (Görev 7).
16. **Ayır metninin üye biçimi.** Spec §6.1-e "`Instagram @…: 70,00 TL`" der, §5.2 Örnek 3 "`TikTok: 0,00 TL`" (kullanıcı adsız). **Çözüm:** W-havuz bölümü (§6.1-e) uygulanır: canlı üye `"{Platform} @{tanıtıcı}: {N2 tr} TL"`, silinmiş üye `"(silinmiş hesap): {N2 tr} TL — ayırınca kullanılamaz"`, ayraç `" · "`, önce canlı üyeler sonra silinmişler (her grup sunucunun sırasıyla, "N" ordinal). Platform adı ve tanıtıcı `CustomerDetailViewModel`'in bugünkü özel yardımcılarından (`PlatformLabel` `:442-450`, `IdentityHandle` `:438-439` — YouTube'da görünen ad) ortak `OrderDeck.App/Formatting/CustomerIdentityText.cs`'e taşınır (kopya yok); tanıtıcı zaten "@" ile başlıyorsa "@" eklenmez. Tutar `TrFormats.Currency` (Görev 7).
17. **Ayır'da hangi üyeyle sorulur ve hata kolları.** Spec "kartın GUID'li bir üyesi". **Çözüm:** kişinin sahibi (`GetPaymentPerson(card.Primary.Id).OwnerId`, Görev 1'in kuralı: sunucuya ulaşabilen satır); null → GUID'li (gönderilebilir) üye yok → sunucuda bakiye olamaz → sorusuz ayır. Panel GET'in 404'ü (sahibin projeksiyonu henüz yok), ağ hatası ve `members` alanının yokluğu (eski sunucu) → "çağrı başarısız" → genel uyarı. Genel uyarının başlığı spec'te yok → onay penceresiyle aynı "Grubu ayır" (Görev 7).
18. **Bayat spec satırları (ek sahip kararı 12.10.2026).** Spec §10 "Açık kalanlar"daki "Sahip kararı bekliyor — Ayır'da silinmiş hesabın parası" (l.695-696), §4.12 (l.324), §5.2 (l.373) ve başlıktaki "bir sahip kararı açık" bayattır. Karar: silinmiş hesapta para varken bütün canlı hesaplar ayrılırsa **Ayır SERBEST + uyarı**; onay penceresi silinmiş hesabın tutarını "(silinmiş hesap): X TL — ayırınca kullanılamaz" diye gösterir. **Çözüm:** Görev 7 bu davranışı uygular; Görev 9 spec'in bayat satırlarını kararla günceller (yalnız belge).
19. **Kart: GUID'li ama gönderilemeyen üyeler (spec §6.1-f "hiç GUID'li üyesi yoksa").** Yeni sahip kuralıyla `OwnerId` iki durumda null: GUID'li üye yok ya da hiçbiri gönderilemez. **Çözüm:** ikisinde de "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor." (gönderilemeyen satır sunucuya hiç ulaşmaz — metin doğru); Bakiye Ekle'nin `MessageBox` metni (`:274-277`) aynı kapıdan geçer. Açılan satır GUID'liyse (spec lafzı) bugünkü gibi o satır. `TryGetProjectionId` test için `internal` olur (Görev 8). GUID'siz satırda `TryGetProjectionId` artık yerel DB okur (sahip okuması DB'ye gider; kapı try/catch ile sarılır): `ReloadBalanceAsync`'in kapı satırı (`:241-249`) try/catch'e alınır ve okuma hatasında var olan `$"Bakiye yüklenemedi: {ex.Message}"` metni gösterilir (`BalanceLoaded = false`, `BalanceTransactions.Clear()`; yeni metin yok). `AddBalanceAsync`'in aynı okuması korumasız kalır (bilinçli). Kilit: `CustomerDetailOwnerBalanceTests.Sahip_okumasi_atarsa_bakiye_yuklenemedi_metni`.
20. **Spec §6.2'nin satır atıfları v0.9.9 kodudur** (`CustomerSearchViewModel.cs:331-339`, `StreamReportViewModel.cs:234-247`; bugünkü master'da #506 sonrası iki giriş aynı kişi × kapsam işini aynı toplamla açar, `PaymentPersonViewModelTests.cs:108-130`). W-havuz'a kod düşmez; saha notu.
21. **`OrderDeck.Licensing` kapsamda.** `LicenseApiClient` ve DTO'lar ne App ne Core'da; spec §6.1-a/d/e onları değiştirir. Yalnız istemci kütüphanesi (`Api/LicenseApiClient.cs`, `Api/Models/CustomerBalanceDtos.cs`); `OrderDeck.Licensing.csproj` `Nullable` + `TreatWarningsAsErrors`; InternalsVisibleTo yalnız `OrderDeck.Licensing.Tests` → `OrderDeck.Tests`'teki tel testleri yalnız public yüzeyi kullanır (Görev 2).
22. **Önizlemenin `personPool`'u okunmaz.** Spec §6.1 tüketici tanımlamaz; K7'nin kesin kolu (200 `Balance = 0`, `personPool: true`) `previewBalance <= 0` → `MarkNoBalance` bugünkü yoldan doğru kapanır. **Çözüm:** `CustomerBalancePreview` değişmez (istenmeyen kapsam yok); K7 bir servis testiyle kilitlenir (Görev 4).
23. **503 `balance-busy`, 404, 400 `member-ids-required`, 409 `client-update-required`.** Hepsi genel belirsiz kolda kalır, `MarkNoBalance` yazılmaz; önizlemede iş taze (anahtarsız), apply'da iş anahtarıyla bekler (bugünkü genel `catch`'ler). Kaynaklar ayrı: 503 `balance-busy` ve 404 için S2 saha notu 5 "eşlenmez" der; `client-update-required` için spec §6.1-c genel `BalanceUncertain` yolunu yeterli sayar (metin değişmez); 400 `member-ids-required`'ı S2 saha notu 5 (`2026-10-29-ortak-bakiye-s2.md:13321`) kesin sonuçtan ayrılması gerekenler arasında sayar — genel belirsiz kol bunu sağlar; Wh çapayı her zaman `memberIds`'e koyduğu için normalde gelmez. **Spec ↔ S2 notu:** S2 notu `client-update-required` ve `member-ids-required`'ın ayrılmasını ister; spec §6.1-c genel kolu yeterli sayar; ayrım `MarkNoBalance`'ın yazılmamasıyla sağlanır. **Çözüm:** kod değişmez, birer regresyon testiyle kilitlenir (Görev 4). `client-update-required`'da güncelleme sorusu (`IAppUpdatePrompt`) açılmaz (spec §6.1-c "metin değişmez"). **Eski sunucu / satır kipi uyumu:** satır kipindeki sunucunun `memberIds`'i okumadığı, `personPool`'u false döndüğü ve bozuk Guid'i 400'lediği S2'nin sunucu testleriyle kanıtlıdır (istemci tarafı bunu test edemez): `PersonBalancePoolPreviewTests.Satir_kipinde_memberIds_yok_sayilir_PersonPool_false` (`:46`), `PersonBalancePoolApplyTests.Satir_kipinde_apply_DrawGroupId_NULL` (`:550`), `PersonBalancePoolReadersTests.Satir_kipinde_scope_PersonPool_false` (`:82`) ve `Satir_kipinde_memberIds_okunmaz_bozugu_400` (`:184`) (hepsi `OrderDeck.LicenseServer.Tests/Controllers/Licenses/`). W-havuz yalnız geçerli Guid gönderir (Görev 3, `Onizleme_kisinin_guidli_butun_uyelerini_gonderir_silinmis_dahil_guidsiz_haric`). Aynı atıf Görev 9'un PR açıklamasına girer.
24. **Compose ortamı yenileme.** S2 planı `PersonBalance__MinClientVersion` için `docker compose up -d license-server` der; ev kuralı (v0.9.11 dersi) `--force-recreate` + konteyner env doğrulaması. Saha notları ev kuralını yazar (Görev 9).
25. **§8.4-4'ün iki Core testi (spec l.616 "kip durumuyla genişletilir").** `PersonScopePlan` ve depo sözleşmesi kipi bilmez: plan sunucu çağrısından önce kurulur (§6.1-d), `RefundsToOtherRow` (`PersonJob.cs:145`) kipten bağımsız bir olgudur ve hesabı değişmez. **Çözüm:** spec'in maddesi App tarafında `PaymentResultPresenterTests` (üç durum + `[Theory]`) ve `PaymentRequestServiceTests.Person`/`.PoolNote` ile karşılanır. `PersonScopePlanTests` ve `PaymentJobStoreContractTests` bilerek değişmez, R-Kişi regresyonuyla korunur (Görev 6).
26. **Sahip kuralının mod On sonucu (Görev 1).** Bütün GUID'li üyeleri gönderilemeyen ve kapsamda uzak etiketi olmayan kişide kira sunucusuna ulaşılamazsa `PaymentLeaseCoordinator.Unreachable` (`:698-699`) artık `FailClosed` değil yerel yolu seçer: mesaj gider, kayıt bağlantı gelince yazılır (GUID'siz kişinin bugünkü yolu, §5.8 Y3). Bu kişide bakiye işi zaten açılmaz; uzak etiketi olan kişi kirayı yine atlayamaz. Kod değişmez (yalnız `PaymentLeaseCoordinator` yorum/günlük metni). Kilit: `PaymentLeaseTests.Begin_cevrimdisi_GUIDli_ama_gonderilemeyen_ve_yalniz_kendi_etiketleri_olan_kiside_yerel_yol_ve_kayit`. Görev 9'un PR metni ve saha notu bu değişikliği sayar ("mod On'da gönderilemeyen-sahipli kişinin çevrimdışı yolu"; "Mod On, ağ kesik: yalnız gönderilemeyen GUID'li üyeleri olan kişide mesaj yerel yoldan gider (bugün engelleniyordu); bağlantı gelince kayıt sunucuya yazılır.").
27. **Sahip adayı olmayan kişide kişi × kapsam işi açılmaz (Görev 1-b).** Bütün GUID'li üyeleri gönderilemeyen kişide `OwnerId` null olur; `SendCoreAsync`'in iş dalı (`:696`) atlanır ve kişi GUID'siz kişinin bugünkü yolundan gider: o kapsamda daha önce açılmış yerel işler okunmaz, aynı kapsamda ikinci tıklamada "tekrar gönderilsin mi?" ve birleştirme sorusu çıkmaz (mesaj sorusuz yeniden gider), teslim kaydı yazılmaz; mod On'da yerel plan uzak soruya katılmaz (`WithLocalPlan` `:1046`); fiyat düzeltmesinin "son istek" planı sahipsiz kurulur (`CorrectedPaymentRequests.cs:277`). Bakiye tarafı doğrudur: sunucu böyle satırı hiç görmez, düşüm yazılamaz. Spec §6.1-a "aday yoksa `OwnerId` null, o kişide bakiye işi açılmaz" der; kod değişmez. Müşteri oluşturan yollar (sohbet, kayıt formu) Platform ve Username'i hep doldurduğu için kişinin sayısı küçük beklenir; büyüklük saha notu 1a'nın `adaysiz` satırlarıyla ölçülür. Kilit: `PaymentRequestServiceTests.Person.Gonderilemeyen_tek_uyeli_kiside_eski_teslim_okunmaz_ikinci_tiklama_sorusuz_gider`. Korumayı bu kişilerde sürdürmek ürün kararıdır → Sahip sorusu 6. Görev 9'un PR metni ve saha notu 3 bu farkı sayar.

## Bilerek W-havuz dışında bırakılanlar

- **Sunucu, panel (OrderDeck-Mobile), Shopper (OrderDeck-Shopper), `OrderDeck.Chat`:** hiçbir değişiklik. S2 sözleşmesi (`LicensesCustomerBalanceApplyController.cs`, `PanelCustomerBalanceController.cs`) sabit; bu okumada sunucuda W-havuz'u engelleyen eksik bulunmadı.
- **Sürüm etiketleme ve yayımlama (Wh), `.env` yazımı (`PersonBalance__MinClientVersion`, `AppRelease__LatestVersion`), `person-balance enable` (kuru ya da `--apply`), web indir sayfası bump'ı** → Burak; plan yalnız "Sürüm / saha notları"nda sırayı anlatır. Kodda sürüm sabiti yok; UA deseni (`AppHost.cs:448`) değişmez.
- **Banka W2 beklentisi** (WPF'te `OwnerId`/`Demoted` tüketicisi yok), **D-banka** ve banka PR5.
- **Önizlemenin `personPool` alanı** (tüketicisi yok, fark 22) ve **`client-update-required`'da güncelleme sorusu** (spec §6.1-c).
- **Spec §1/§10 kapsam dışıları:** tek üyeyi gruptan çıkarma, kalıcı `PersonId`, kesinleşmiş `NoBalance`'ın yeniden sorulması (§11 risk 7), silinmiş satır için `GroupId` gönderimi (§4.12).
- **§6.1-h değişmeyenleri:** `scope`, `transactions/{id}`, `reverse` çağrıları (`memberIds` almaz), WhatsApp `{bakiye}` alanı, arama ve rapor başlatıcıları (`CustomerSearchViewModel.OpenWhatsAppAsync`, `StreamReportViewModel.OpenWhatsAppAsync`), `CustomerRepository.UnmergeGroup`'un kendisi, `AddBalanceDrawer`.
- **503 `balance-busy` için özel eşleme** (fark 23) ve sunucunun `customer-not-synced` satır Id'lerini istemciye döndürmesi (fark 5; sunucu değişikliği olurdu).
- **Şema değişikliği ve göç:** yok (yerel şema 49 kalır). Bir görev göç gerektirirse durur ve sorar.

## Ön koşullar ve sıra (spec §7.1)

- **Sunucu tamam (prod):** S1 (#533) 22.10'dan, S2 (#553) 31.10.2026'dan beri prod'da, deploy doğrulandı. **Hiçbir lisans havuz kipinde değil.** `person-balance enable` kuru koşusu koşul 1 ve 4'ü TUTMUYOR gösteriyor, çünkü `PersonBalance:MinClientVersion` tanımsız (beklenen; S2 saha notu 4). Ws = `SalesSync:MinClientVersion` = **0.9.11**.
- **#506 (ödeme isteğini kişiye bağla)** 11.10'da master'a girdi, v0.9.10'dan beri sahada: spec'in "#506'nın üstüne" dediği yer bugünkü master'dır (`7960eba3`). v0.9.12'den sonra master'a istemci commit'i girmedi (`git log v0.9.12..HEAD -- OrderDeck.App OrderDeck.Core OrderDeck.Licensing` boş).
- **W-havuz merge = yalnız master.** Burak yapar, **yayın penceresi dışında** (Paz/Pzt/Çar/Per 20:00–01:00 TR değil); PR açılırsa DRAFT kalır. Master merge sunucu deploy iş akışını da tetikler ama sunucu kodu değişmez. WPF sahaya ancak Velopack sürümü ve PC güncellemesiyle çıkar.
- **Wh** = W-havuz'u taşıyan ilk Velopack sürümü: son yayımlanan v0.9.12 → **Wh ≥ 0.9.13**; **Wh ⊇ Ws** (0.9.13 > 0.9.11, mekanik). Numara Burak'ındır ve koda gömülmez; `InformationalVersion` ön sürüm eki taşımamalı (`0.9.13-rc1` < `0.9.13`, kapıyı geçmez).
- **Wh, havuz kipi açılmadan önce sahaya çıkabilir** (spec §6.1 giriş): yeni davranışların hepsi sunucu sinyaline bağlı; satır kipinde Wh = bugünkü davranış + sahip kuralı + Ayır onayı + kart düzeltmesi + yerel planın nötr not metni (fark 12); ayrıca mod On'da gönderilemeyen-sahipli kişinin çevrimdışı yolu (sahip kuralının mod On sonucu — fark 26) ve sahip adayı olmayan kişide Resend/Merge sorusunun ve teslim kaydının düşmesi (fark 27).
- **`enable` ancak şunlardan sonra (spec §7.1 adım 6–9):** son 14 günde görülmüş bütün etkin PC'ler Wh+ (koşul 1); `.env`'e `PersonBalance__MinClientVersion=<Wh>` + `docker compose up -d --force-recreate license-server` (koşul 4: ≥ Ws); test lisansında karışık filo denemesi (§8.5); pilot lisansta önce kuru `person-balance enable --license <id>`, sonra `--apply` — Burak'ın onayıyla, yayın penceresi dışında. Kill switch `person-balance disable --license <id> --apply`.

---

## Genel kurallar

- **Worktree:** `C:/Users/burak/source/repos/LiveDeck/.claude/worktrees/ortak-bakiye-w-havuz`, dal `feat/ortak-bakiye-w-havuz` (`origin/master` `7960eba3`'ten; **push yok**; push ve PR yalnız Burak isterse). Bütün komutlar bu kökten. Ana checkout'a (`C:/Users/burak/source/repos/LiveDeck`) dokunma.
- **Kapsam:** `OrderDeck.Core`, `OrderDeck.App`, `OrderDeck.Licensing` (yalnız `Api/LicenseApiClient.cs` ve `Api/Models/CustomerBalanceDtos.cs` — istemci kütüphanesi, sunucu değil; fark 21), `OrderDeck.Tests`, `docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md` (yalnız Görev 9'un bayat satırları). `OrderDeck.LicenseServer*`, `OrderDeck.Chat`, `Extension/`, `web/`, `deploy/` DOKUNULMAZ. Sunucuda gerçek bir eksik bulunursa görev durur ve sorar.
- **Derleme:** `OrderDeck.App` (`net10.0-windows`), `OrderDeck.Core` (`net10.0`), `OrderDeck.Licensing` (`net10.0-windows`) üçü de `Nullable enable` + `TreatWarningsAsErrors true` (yalnız `CS1591` hariç): 0 uyarı. Kullanılmayan özel üye, atanıp okunmayan alan (CS0414), kullanılmayan yerel/parametre bırakma; bir görevin eklediği üyeyi kullanan kod aynı görevde olmalı. `OrderDeck.Tests` `Nullable enable`, TWAE yok; App'in `internal`'ları görünür (IVT), Core ve Licensing'inkiler görünmez → testten çağrılan Core/Licensing üyesi `public`.
- **Repo public:** testlerde literal kimlik bilgisi/parola/anahtar YOK. Lisans anahtarı `$"LDK-{Guid.NewGuid():N}"` (`BalanceApiStub` emsali) ya da var olan sabit; yeni üretilen değerler `$"g-{Guid.NewGuid():N}"`, değişkene **nötr ad** (`giris`, `deger`; `password`/`secret`/`token` değil). Telefon `TestPhone.NewE164()`; kullanıcı adları ve Id'ler `Guid`'den üretilir; GUID'siz yerel Id `$"yerel-{Guid.NewGuid():N}"`; gerçek ad/telefon/IBAN yok. Var olan eski literaller (`WhatsAppStubHandler.LicenseKey`, sabit `LicenseId`, `WithPaymentDetails` IBAN'ı) yeni koda kopyalanmaz. Doküman yorumlarında parola biçimli örnek yok.
- **Günlük:** yalnız Id'ler (iş, anahtar, müşteri satırı); ad, kullanıcı adı, telefon yok. Testte `CapturingLogger<T>` ile denetlenir.
- **Türkçe yorum ve test adı:** alt çizgili Türkçe cümle, Türkçe karakter yok (`Onizleme_customer_not_synced_BalanceNotSynced_doner`); `[Fact] // tek satır gerekçe` + spec maddesi (`§6.1-b`, `K2`); FluentAssertions `because` Türkçe. Kullanıcıya görünen metinler spec'tekilerin AYNISI (aşağıdaki imza bloğundaki sabitler tek kaynak).
- **Commit (git-bash):** `git add <dosyalar>` + `git commit -F - <<'MSG' … MSG`; başlık `feat(balance): …` / `test(balance): …` / `docs(balance): …`, Türkçe emir kipi; gövde nedeni söyler; son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.
- **Test komutu (PowerShell):** `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~<Sınıf>"`; birden çok sınıf `"FullyQualifiedName~A|FullyQualifiedName~B"`. git-bash'te `DOTNET_CLI_UI_LANGUAGE=en dotnet test …`. Derleme: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q` (0 uyarı). `~X` sınıf adının alt dizgisine eşleşir (`~PaymentRequestServiceTests` bütün partial dosyaları kapsar).
- **Satır numaraları** master `7960eba3`'ündür ve önceki görevlerin eklemeleriyle kayabilir; Edit'ler satır numarasına değil alıntı metne bağlanır (ör. Görev 1 `PaymentRequestService.cs` `:695` sonrasını +1, `PaymentLeaseTests.cs` `:594` sonrasını yaklaşık +27 kaydırır; Görev 6'nın `PaymentLeaseTests` `:793-812` atfı bu yüzden kayar).
- **Kaynak dosyaları yalnız Edit/Write aracıyla** düzenlenir. PowerShell 5.1 `Get-Content`/`Set-Content` BOM'suz UTF-8'i bozar.
- **TDD sırası her görevde:** başarısız test → koştur (beklenen FAIL, nedeni yazılı) → en küçük uygulama → koştur (PASS) → derleme → regresyon kümesi → commit.
- **Satır kipi regresyonu:** her görevin regresyon adımı var olan testleri DEĞİŞTİRMEDEN koşturur. Bilerek değişen var olan test satırları yalnız şunlardır ve görevlerinde adıyla yazılır: `PaymentOwnerRuleTests` vektör kurucusu ve `PaymentJobStoreContractTests` InMemory fikstürü (Görev 1, fark 3; Görev 1'de ayrıca `PaymentOwnerRuleTests`'in okuma ve kayıt fikstürü — sınıf belgesi, `VectorMember`, JSON okuması — ve `PaymentPersonTests.Insert`'in geriye uyumlu `username` parametresi değişir, iddialar aynıdır), `PaymentPersonViewModelTests.cs:190` `Execute` → `await …ExecuteAsync` (Görev 7), `PaymentRequestServiceTests.Person.cs:164` not iddiası (Görev 6, fark 12). Filtre kümeleri:
  - **R-Kişi:** `PaymentOwnerRuleTests|PaymentPersonTests|PaymentPersonGateTests|PersonScopePlanTests|PaymentJobStoreContractTests|PaymentJobRepositoryTests|CustomerRepositoryTests`
  - **R-Ödeme:** `PaymentRequestServiceTests|PaymentResultPresenterTests|CorrectedPaymentRequestsTests|PaymentPersonViewModelTests`
  - **R-ModOn (yavaş, ~dakikalar):** `PaymentLeaseTests|PaymentLeaseServerRulesTests|RemotePaymentRecordsTests|TwoPcSalesConvergenceTests`
  - **R-Senkron:** `WpfCustomerProjectionSyncServiceTests|CustomerChangesPullServiceTests|CustomerSyncDiTests`
  - **R-İstemci:** `LicenseApiClientPaymentRequestTests|LicenseApiClientTests` + `dotnet test OrderDeck.Licensing.Tests/OrderDeck.Licensing.Tests.csproj --nologo -v q`
  - **R-Arayüz:** `CustomerSearchViewModelTests|CustomerDetailViewModelTests|CustomerDetailRoutingTests|MainShellViewCompositionTests`
- **Partial test dosyaları:** `PaymentRequestServiceTests` partial sınıfına her görev KENDİ dosyasını ekler (Görev 3 `.MemberIds.cs`, Görev 4 `.NotSynced.cs`, Görev 5 `.CustomerTour.cs`, Görev 6 `.PoolNote.cs`). Özel yardımcı adları imza bloğundakilerdir; yeni ad koymadan önce sınıfta çakışma Grep'lenir (var olanlar: `Uye`, `Kisi`, `KisiSut`, `Isler`, `MirasIs`, `G`, `ScopeJsonAt`, `MakeSut`, `MakeCloudSut`, `SutOn`, `IkinciSut`, `EvetAsync`, `MakeCustomer`, `FileDb`, `Drop`, `T`).
- **Alt ajan kuralı:** Monitor ya da arka plan bekleme yok. Tam paket (Görev 9) yürütücünün koşacağı komut olarak yazılır.
- **Yayın penceresi:** geliştirme PC'si yayın PC'si değil; yayın saatlerinde yerel test serbest, yasak yalnız merge/deploy/sürüm.

## Dosya haritası

| Görev | Dosya |
|---|---|
| 1 | Oluştur: `OrderDeck.Core/Customers/ServerCustomerRule.cs`. Değiştir: `OrderDeck.Core/Customers/PaymentPerson.cs` (`PaymentPersonMember` belge + kayıt `:7-16`, `Single` `:78-83`, `OwnerId` belgesi `:49-50`, `PaymentOwnerRule` belgesi `:90-104`, `IsOwnerCapable` belgesi `:107`, `PickOwner` `:122-124`), `OrderDeck.Core/Storage/Repositories/PaymentPersonSql.cs` (`:33-49`), `OrderDeck.App/Services/Sync/WpfCustomerProjectionSyncService.cs` (`:97-114` silinir, yorum + çağrı `:200-215`), `OrderDeck.App/Services/PaymentRequestService.cs` (yalnız yorum `:694-695`), `OrderDeck.App/Services/Sync/PaymentLeaseCoordinator.cs` (yalnız yorum/günlük metni `:161-162`, `:686-689`, `:712-713`). Test: `OrderDeck.Tests/Customers/ServerCustomerRuleTests.cs` (yeni), `OrderDeck.Tests/Customers/PaymentOwnerRuleTests.cs` + `PaymentOwnerRuleVectors.json` (genişlet), `OrderDeck.Tests/Storage/PaymentPersonTests.cs` (genişlet), `OrderDeck.Tests/Storage/PaymentJobStoreContractTests.cs` (`:947`, `:959` fikstür), `OrderDeck.Tests/Sync/PaymentLeaseTests.cs` (bir test), `OrderDeck.Tests/Services/PaymentRequestServiceTests.Person.cs` (bir test, fark 27) |
| 2 | Değiştir: `OrderDeck.Licensing/Api/Models/CustomerBalanceDtos.cs`, `OrderDeck.Licensing/Api/LicenseApiClient.cs` (`:787-796`). Test: `OrderDeck.Tests/Licensing/LicenseApiClientPaymentRequestTests.cs` (yeni bölüm) |
| 3 | Değiştir: `OrderDeck.App/Services/PaymentRequestService.cs` (`SettleScopeAsync`'in dört çağrısı `:1138`, `:1227`, `:1275`, `:1277`; dört özel yöntemin imzasına `PaymentPerson person` — `ReviseAndSettleAsync` `:1286-1288`, `SettleAsync` `:1616-1618`, `ReplayOnceMoreIfStaleAsync` `:1640-1641`, `ReplayAsync` `:1780-1781` — ve iç çağrıları `:1319`, `:1431`, `:1626`, `:1629`, `:1643`, `:1645`; önizleme yorumu + çağrı `:1395-1402`; `ReplayAsync` belgesi `:1754-1756` ve `:1764-1770`; apply isteği `:1796-1802`; yeni özel statik `MemberIdsOf` `SaleScopeOf` `:1862` ardına). Test: `OrderDeck.Tests/Services/PaymentRequestServiceTests.cs` (`WhatsAppStubHandler` eklemeleri), `OrderDeck.Tests/Services/PaymentRequestServiceTests.MemberIds.cs` (yeni) |
| 4 | Değiştir: `PaymentRequestService.cs` (enum, `Message` belgesi, `BalanceOutcome`, `ApplyAttempt`, `SendCoreAsync` `:696-714`, önizleme `catch`, `ReplayAsync` `catch`, `SettleScopeAsync` `:1151`/`:1231`, `SettleAsync` `:1634`, `NotSyncedOutcome`), `OrderDeck.App/Services/PaymentResultPresenter.cs` (`Notify` ×2, sabitler), `OrderDeck.App/Services/PriceCorrection/CorrectedPaymentRequests.cs` (`:546-547`). Test: `OrderDeck.Tests/TestHelpers/ManualTimeProvider.cs` (yeni), `PaymentRequestServiceTests.cs` (`PreviewResponse`, `Problem` public, `SutOn` `log`), `PaymentRequestServiceTests.NotSynced.cs` (yeni), `PaymentResultPresenterTests.cs`, `CorrectedPaymentRequestsTests.cs`, `OrderDeck.Tests/Sync/PaymentLeaseTests.cs` (bir test: mod On kira etkileşimi) |
| 5 | Değiştir: `PaymentRequestService.cs` (ctor `customerPull`, `CustomerTour`, `LastCustomerTour`, `_customerTourPending`, `TriggerCustomerTour`, `TakeQueuedCustomerTour`, `RequestPaymentAsync` belgesi `:305-324` (`</summary>` `:324`'ten önce yeni `<para>`) ve döngüsü `:347-349`). Test: `PaymentRequestServiceTests.CustomerTour.cs` (yeni), `OrderDeck.Tests/App/CustomerSyncDiTests.cs` (bir test) |
| 6 | Değiştir: `PaymentRequestService.cs` (`PaymentPrompt.PersonPool`, `:79-81` yorumu, `WithLocalPlan`, `PromptOf`, izlerin sorusu `:1511-1514`, `PersonPoolOf`), `PaymentResultPresenter.cs` (`WithOtherRowNote`, belge `:153-155`, düzenleme bloğu `:150-155`), `OrderDeck.Core/Storage/Repositories/PersonJob.cs` (yorumlar `:75-80`, `:212-214`). Test: `PaymentResultPresenterTests.cs`, `PaymentRequestServiceTests.Person.cs` (`ScopeJsonAt` isteğe bağlı parametre), `PaymentRequestServiceTests.PoolNote.cs` (yeni), `OrderDeck.Tests/Sync/PaymentLeaseTests.cs` (bir test) |
| 7 | Oluştur: `OrderDeck.App/Formatting/CustomerIdentityText.cs`. Değiştir: `OrderDeck.App/Services/IDialogService.cs` (`:49-51`), `OrderDeck.App/Services/WpfDialogService.cs` (`:48-49`), `OrderDeck.App/Views/Drawers/MessageDrawer.xaml.cs` (`:13-38`), `OrderDeck.App/ViewModels/CustomerSearchViewModel.cs` (ctor, `UnmergeGroup` `:265-272`), `OrderDeck.App/ViewModels/CustomerDetailViewModel.cs` (kimlik rozeti çağrısı `:207` → `CustomerIdentityText`; özel yardımcılar `:429-450` silinir). Test: `OrderDeck.Tests/App/MessageDrawerButtonTests.cs` (yeni), `OrderDeck.Tests/Fakes/FakeDialogService.cs`, `OrderDeck.Tests/TestHelpers/BalanceApiStub.cs` (panel GET), `OrderDeck.Tests/ViewModels/CustomerSearchUnmergeTests.cs` (yeni), `OrderDeck.Tests/ViewModels/PaymentPersonViewModelTests.cs` (`:190`), `PaymentResultPresenterTests.cs` (bir test), `CustomerSyncDiTests.cs` (bir test) |
| 8 | Değiştir: `OrderDeck.App/ViewModels/CustomerDetailViewModel.cs` (`TryGetProjectionId` `:229-239`, erişim private→internal; `ReloadBalanceAsync` kapı satırı `:241-249` try/catch içine). Test: `OrderDeck.Tests/ViewModels/CustomerDetailOwnerBalanceTests.cs` (yeni) |
| 9 | Değiştir: `docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md` (bayat satırlar). Tam WPF paketi + `OrderDeck.Licensing.Tests` + çözüm Release derlemesi (`OrderDeck.sln`, CI biçimi); "Sürüm / saha notları" |

Bütün kaynak yolları worktree köküne göre.

## Görev özeti

| # | Görev | Kabaca efor |
|---|---|---|
| 1 | Sahip kuralı: `ServerCustomerRule` Core'a, `PaymentPersonMember` Platform/Username, `IsOwnerCandidate` | 2,5 sa |
| 2 | İstemci sözleşmesi: preview/apply `memberIds`, scope `PersonPool`, panel `Members` | 1,5 sa |
| 3 | Ödeme akışında `memberIds`: önizleme, apply, replay (akışın kişisi dört özel yöntemle aşağı geçer) | 2 sa |
| 4 | `customer-not-synced` → `BalanceNotSynced`: bayrak, uyarı, 5 dakika metni, öbür yanıtların regresyonu | 3 sa |
| 5 | `PullOnceAsync` tetiklemesi (fire-and-forget, bir koşan + bir bekleyen tur) + DI | 1,5 sa |
| 6 | Birleştirme notunun üç durumu + belge yorumları + `PersonJob` gerekçesi | 2 sa |
| 7 | Ayır onayı (dağılım, [Ayır]/[Vazgeç], genel uyarı) | 3 sa |
| 8 | Müşteri kartı sahip yedeği | 1 sa |
| 9 | Tam WPF paketi, spec'in bayat satırları, saha notları | 1 sa (+ ~15 dk koşu) |

Toplam ≈ 17,5 sa (2,5 + 1,5 + 2 + 3 + 1,5 + 2 + 3 + 1 + 1) ≈ 2,2 iş günü; görevlerin R-ModOn regresyonları (Görev 1, 3, 4, 5, 6; her biri ~dakikalar) bu sürelere dahildir, Görev 9'un tam paket koşusu (~15 dk; iki yarıya bölünürse daha uzun) satırındaki gibi ayrıca. Spec tahmini 1,75 gün (≈ 14 sa); +3,5 sa sapmanın gerekçesi parçaların gerçek boyudur: Görev 3'te akışın kişisinin dört özel yöntemle aşağı geçirilmesi (spec §6.1-a'nın "çağrı anında" okumasından bilinçli sapma, fark 4; 15 Edit) ve akış ortası testi; test sahtelerinin genişlemesi (`WhatsAppStubHandler` önizleme senaryosu ve `memberIds` gözlemi, `BalanceApiStub` panel ucu, `FakeDialogService` düğme yazısı, `ManualTimeProvider`), iki DI testi, spec'in bilmediği kurulum yerleri (`WithLocalPlan`, `CorrectedPaymentRequests`, InMemory sözleşme fikstürü); Görev 1'de satır kipinin iki bilinçli farkını kilitleyen testler (mod On kira `PaymentLeaseTests`, Sahip sorusu 6'nın (Görev 1-b) `PaymentRequestServiceTests.Person` testi), beş yeni vektör ve iki fikstür güncellemesi (17 adım); Görev 4'te iki commit'e bölünen iş (4a bayrak/uyarı + mod On kira testi, 4b 5 dakika kuralının beş sayaç testi + toplu gönderim + K7/§6.1-c/fark 23 kilit testleri; 16 adım, 18 test yöntemi).

## Görevler arası imzalar (tek kaynak)

Her görev yalnız kendi satırlarını TANIMLAR; sonraki görevler aynen TÜKETİR. Ad, tür ya da parametre sırası değişirse bu blok ve tüketen bütün görevler birlikte güncellenir.

```csharp
// ── Görev 1 ── OrderDeck.Core/Customers/ServerCustomerRule.cs (yeni; using System.Diagnostics.CodeAnalysis)
namespace OrderDeck.Core.Customers;
public static class ServerCustomerRule
{
    public const int MaxPlatformLength = 32;    // LicensesWpfCustomersSyncController ile aynı
    public const int MaxUsernameLength = 128;
    public static bool IsServerAcceptable([NotNullWhen(true)] string? platform, [NotNullWhen(true)] string? username);
    // = !IsNullOrWhiteSpace(platform) && platform.Length <= 32 && !IsNullOrWhiteSpace(username) && username.Length <= 128
}
// ── Görev 1 ── OrderDeck.Core/Customers/PaymentPerson.cs
public sealed record PaymentPersonMember(
    string Id, string? Phone, bool Purged, bool RecipientPaysActive = false, decimal TotalAmount = 0m,
    string? Platform = null, string? Username = null);
public static class PaymentOwnerRule
{
    public static bool IsOwnerCapable(string id);                       // DEĞİŞMEZ: Guid.TryParseExact(id, "N", out _)
    public static bool IsOwnerCandidate(PaymentPersonMember member);    // YENİ: IsOwnerCapable(member.Id) && ServerCustomerRule.IsServerAcceptable(member.Platform, member.Username)
    public static IReadOnlyList<PaymentPersonMember> Order(IEnumerable<PaymentPersonMember> members);   // DEĞİŞMEZ
    public static string? PickOwner(IEnumerable<PaymentPersonMember> members);   // Order(members).FirstOrDefault(IsOwnerCandidate)?.Id
}
// PaymentPerson.OwnerId = PickOwner(Members): GUID'li VE gönderilebilir üye yoksa null.
// PaymentPerson.Single(customer): new PaymentPersonMember(customer.Id, customer.Phone, Purged: false,
//     customer.RecipientPaysActive, customer.TotalAmount, customer.Platform, customer.Username)
// PaymentPersonSql: Columns = "SELECT Id, Phone, PurgedAt, RecipientPaysActive, TotalAmount, Platform, Username FROM Customer";
//     Row'a string? Platform, string? Username; üye Platform: r.Platform, Username: r.Username taşır.
// WpfCustomerProjectionSyncService: özel MaxPlatformLength/MaxUsernameLength/IsServerAcceptable SİLİNİR;
//     :215 → ServerCustomerRule.IsServerAcceptable(c.Platform, c.Username).
// ── Görev 1 ── test: OrderDeck.Tests/Customers/PaymentOwnerRuleVectors.json üye alanları
//     "platform": "ok" | "empty" | "long", "username": "ok" | "empty" | "long" (alan yoksa "ok").

// ── Görev 2 ── OrderDeck.Licensing/Api/Models/CustomerBalanceDtos.cs
public sealed record CustomerBalanceApplyRequest(
    Guid WpfCustomerId, decimal Amount, decimal ProductTotal,
    Guid? IdempotencyKey = null, string? SaleScope = null, IReadOnlyList<Guid>? MemberIds = null);   // JSON "memberIds" (null → "memberIds":null)
public sealed record CustomerBalanceScope(
    Guid TransactionId, decimal AppliedAmount, decimal ProductTotal, DateTimeOffset CreatedAt,
    bool PersonPool = false);                                                                         // JSON "personPool"; yoksa false
public sealed record CustomerBalanceMemberDto(Guid WpfCustomerId, decimal Balance, bool Purged);     // JSON members[{wpfCustomerId,balance,purged}]
public sealed record CustomerBalanceDetailsResponse(
    CustomerBalanceDto Balance, CustomerBalanceTransactionDto[] Transactions,
    CustomerBalanceMemberDto[]? Members = null);                                                     // JSON "members"; yoksa null
// CustomerBalancePreview DEĞİŞMEZ (fark 22).
// ── Görev 2 ── OrderDeck.Licensing/Api/LicenseApiClient.cs
public Task<CustomerBalancePreview> GetBalancePreviewAsync(
    Guid licenseId, Guid wpfCustomerId, CancellationToken ct = default);        // KORUNUR: boş listeyle yeni aşırı yüklemeyi çağırır
public Task<CustomerBalancePreview> GetBalancePreviewAsync(
    Guid licenseId, Guid wpfCustomerId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct = default);
// sorgu: "?wpfCustomerId={wpfCustomerId:D}" + her üye için "&memberIds={id:D}" (verilen sırayla; boşsa parametre yok).
// 409 customer-not-synced / client-update-required, 400 member-ids-required → ValidationException { Code = başlık } (MapProblem, değişmez).

// ── Görev 3 ── OrderDeck.App/Services/PaymentRequestService.cs (özel)
private static IReadOnlyList<Guid> MemberIdsOf(Guid anchor, PaymentPerson person);
// = person.MemberIds.Where(PaymentOwnerRule.IsOwnerCapable)
//     .Select(id => Guid.ParseExact(id, "N")).Append(anchor).Distinct()
//     .OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList()          — depo OKUNMAZ (fark 4)
// person = akışın kişisi: SettleScopeAsync'in person'u (tıklamanın girişindeki PersonHold anlık görüntüsü). Dört özel
// yöntem onu aşağı geçirir; yeni parametre licenseId'nin ardında (ReconcilePersonScopeAsync deseni), gerisi aynen:
private async Task<BalanceOutcome> ReviseAndSettleAsync(
    Guid licenseId, PaymentPerson person, Guid wpfCustomerId, PaymentJob job, decimal totalAmount, bool remoteConfirmed,
    CancellationToken ct);
private async Task<BalanceOutcome> SettleAsync(
    Guid licenseId, PaymentPerson person, Guid wpfCustomerId, PaymentJob job, decimal requestedTotal,
    CancellationToken ct);
private async Task<ApplyAttempt> ReplayOnceMoreIfStaleAsync(
    Guid licenseId, PaymentPerson person, Guid wpfCustomerId, PaymentJob job, CancellationToken ct);
private async Task<ApplyAttempt> ReplayAsync(
    Guid licenseId, PaymentPerson person, Guid wpfCustomerId, PaymentJob job, CancellationToken ct);
// Çağrı yerleri (master numaraları; hepsi SettleScopeAsync ağacında, arka plan çağıranı yok): SettleScopeAsync →
//     ReplayOnceMoreIfStaleAsync (miras :1138, belirsiz iş :1227) ve ReviseAndSettleAsync (:1275, :1277);
//     ReviseAndSettleAsync → SettleAsync (:1319, :1431); SettleAsync → ReplayAsync (:1626, :1629);
//     ReplayOnceMoreIfStaleAsync → ReplayAsync (:1643, :1645).
// Önizleme (8. adım): _api.GetBalancePreviewAsync(licenseId, wpfCustomerId, MemberIdsOf(wpfCustomerId, person), ct)
// ReplayAsync: new CustomerBalanceApplyRequest(wpfCustomerId, job.ProductTotal, job.ProductTotal, key, SaleScopeOf(job),
//     MemberIds: MemberIdsOf(wpfCustomerId, person))   — ikisi de var olan try bloklarının İÇİNDE
// ── Görev 3 ── test: WhatsAppStubHandler (OrderDeck.Tests/Services/PaymentRequestServiceTests.cs) eklemeleri
public List<IReadOnlyList<Guid>> PreviewMemberIds { get; }          // her önizlemenin tekrarlı memberIds değerleri (yoksa boş), _sync altında
public IReadOnlyList<IReadOnlyList<Guid>?> AppliedMemberIds { get; } // AppliedBalanceBodies'den; alan yok/null → null
public static IReadOnlyList<Guid>? MemberIdsOfBody(string applyBody);
// test yardımcıları (PaymentRequestServiceTests.MemberIds.cs, private static):
//     void Silinmis(SyncedDb db, Customer c)             — Dapper: UPDATE Customer SET PurgedAt = 1 WHERE Id = @id
//     Customer YerelUye(SyncedDb db, string username)    — Uye(db, username, id: $"yerel-{Guid.NewGuid():N}")
//     string UyeAdi()                                    — $"u-{Guid.NewGuid():N}" (üretilmiş kullanıcı adı)
//     void GrubaAl(SyncedDb db, string grupId, params Customer[] uyeler)
//                                                        — Dapper: UPDATE Customer SET GroupId = @grupId WHERE Id IN @ids
//                                                          (çekme turunun var olan satıra GroupId yazmasının yerine)
// akış ortası kancası: WhatsAppStubHandler.ScopeResponseFor (master'da var, :347; kapsam sorgusunda koşar) — stub eklemesi yok

// ── Görev 4 ── PaymentRequestService.cs
public enum PaymentRequestResult { /* … var olanlar aynen …, */ Blocked, BalanceNotSynced }   // yeni üye EN SONDA
// PaymentRequestOutcome.Message belgesi: BalanceNotSynced'te 5 dakikadan uzunsa NotSyncedPersistentText, değilse null.
internal TimeProvider NotSyncedClock { get; set; } = TimeProvider.System;
internal static readonly TimeSpan NotSyncedPatience = TimeSpan.FromMinutes(5);
internal static readonly TimeSpan NotSyncedStreakGap = TimeSpan.FromMinutes(30);   // art arda 409 aralığı (Sahip sorusu 3)
private const string CustomerNotSyncedCode = "customer-not-synced";
private readonly ConcurrentDictionary<string, (long First, long Last)> _notSyncedSince = new(StringComparer.Ordinal);  // anahtar PaymentPerson.MemberKey; NotSyncedClock.GetTimestamp() damgaları
private PaymentRequestOutcome NotSyncedOutcome(PaymentPerson person, string? ownerId, IReadOnlyList<DemotedJob> demoted);
private sealed record BalanceOutcome(bool Uncertain, decimal AppliedBalance, PaymentJob? Job)
{   /* var olan init'ler aynen */ public bool NotSynced { get; init; } }
private sealed record ApplyAttempt(PaymentJob Job, bool Stale = false, bool Adopted = false,
    ScopeHasDeductionException? LegacyScopeDeduction = null, bool NotSynced = false);
// ── Görev 4 ── OrderDeck.App/Services/PaymentResultPresenter.cs
public const string NotSyncedTitle = "Henüz eşitlenmedi";
public const string NotSyncedText =
    "Kişinin hesapları sunucuyla henüz eşitlenmedi; mesaj gönderilmedi. Birazdan tekrar deneyin — bakiye kaybolmaz, çift düşüm olmaz.";
public const string NotSyncedPersistentText =
    "Bu kişinin hesaplarından biri sunucuya ulaşmıyor; mesaj gönderilmedi. Müşteri kaydını kontrol edin ya da grubu ayırıp yeniden birleştirin.";
// Notify(dialogs, outcome): BalanceNotSynced → dialogs.Show(outcome.Message ?? NotSyncedText, NotSyncedTitle, DialogSeverity.Warning)
// Notify(dialogs, result):  BalanceNotSynced → dialogs.Show(NotSyncedText, NotSyncedTitle, DialogSeverity.Warning)
// CorrectedPaymentRequests: PaymentRequestResult.BalanceUncertain or PaymentRequestResult.BalanceNotSynced → aynı satır (metin aynen).
// ── Görev 4 ── test
public Func<int, HttpResponseMessage?>? PreviewResponse { get; set; }   // WhatsAppStubHandler: kaçıncı önizleme (1'den); dolu → o yanıt, null → normal yol
public static HttpResponseMessage Problem(string title, HttpStatusCode status = (HttpStatusCode)409);   // WhatsAppStubHandler: private → public
// SutOn(…, PaymentOffRecordWriter? offRecords = null, ILogger<PaymentRequestService>? log = null)   — log son parametre, ctor'a geçer
// OrderDeck.Tests/TestHelpers/ManualTimeProvider.cs (namespace OrderDeck.Tests.TestHelpers)
public sealed class ManualTimeProvider : TimeProvider
{   public override long TimestampFrequency { get; }  public override long GetTimestamp();
    public override DateTimeOffset GetUtcNow();       public void Advance(TimeSpan by); }
// test yardımcısı (PaymentRequestServiceTests.NotSynced.cs, private):
//     (PaymentRequestService Sut, WhatsAppStubHandler Handler, CapturingLogger<PaymentRequestService> Log, ManualTimeProvider Saat)
//         EsitlenmemisSut(SyncedDb db)

// ── Görev 5 ── PaymentRequestService.cs
public PaymentRequestService(
    SettingsStore settingsStore, WhatsAppMessageBuilder messageBuilder, IUrlLauncher launcher, LicenseApiClient api,
    ICurrentLicenseProvider currentLicense, IPaymentJobStore jobs, IShippingSettingsProvider shipping,
    Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? log = null, CustomerRepository? customers = null,
    CustomerBusySet? busy = null, PaymentOffRecordWriter? offRecords = null, PaymentLeaseCoordinator? lease = null,
    CustomerChangesPullService? customerPull = null);                       // YENİ son parametre
internal Func<CancellationToken, Task>? CustomerTour { get; set; }       // ctor: customerPull is null ? null : customerPull.PullOnceAsync
internal Task LastCustomerTour { get; private set; } = Task.CompletedTask;   // son başlatılan tur; yalnız testler bekler
private int _customerTourPending;                                        // 0 boşta / 1 tur koşuyor / 2 koşuyor + bir tur sırada; Interlocked
private void TriggerCustomerTour();                                      // 0→1 tur başlar, 1→2 sıraya alır, 2'de düşer
private bool TakeQueuedCustomerTour();                                   // tur sonunda: 2→1 true (bir tur daha), 1→0 false
// RequestPaymentAsync: her RequestPaymentOnceAsync dönüşünden hemen sonra
//     if (outcome.Result == PaymentRequestResult.BalanceNotSynced) TriggerCustomerTour();
// ── Görev 5 ── test yardımcısı (PaymentRequestServiceTests.CustomerTour.cs, private sealed class)
//     TurSayaci { int Cagri; TaskCompletionSource Baslasin, Bitsin; Exception? Firlat; Task Tur(CancellationToken ct); }
// ── Görev 8 ── test yardımcıları (CustomerDetailOwnerBalanceTests, ayrı sınıf): Kurulum, Kur(), Satir(...)

// ── Görev 6 ── PaymentRequestService.cs
// PaymentPrompt (record gövdesine):
public bool? PersonPool { get; init; } = false;   // false = satır kipi/eski sunucu (bugünkü not); true = havuz (not yok); null = bilinmiyor (nötr not)
private static bool? PersonPoolOf(IReadOnlyList<(string MemberId, CustomerBalanceScope Scope)> traces);
// = traces.Select(t => t.Scope.PersonPool).Distinct().Count() == 1 ? traces[0].Scope.PersonPool : null
// PromptOf → new(…) { PersonPool = null };  WithLocalPlan → remote with { …, PersonPool = null };
// izlerin sorusu (:1511) → new PaymentPrompt(…) { PersonPool = PersonPoolOf(traces) }
// ── Görev 6 ── PaymentResultPresenter.WithOtherRowNote (özel, imza aynı): RefundsToOtherRow false → mesaj aynen;
//     PersonPool false → + " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner, bu istekte kullanılmaz."
//     PersonPool true  → mesaj aynen (not yok)
//     PersonPool null  → + " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner."
// ── Görev 6 ── test: PaymentRequestServiceTests.Person.cs
private static string ScopeJsonAt(Guid tx, decimal applied, decimal total, DateTimeOffset at, bool? personPool = null);
// personPool null → bugünkü gövde aynen (alan yok); dolu → "personPool":true|false eklenir

// ── Görev 7 ── OrderDeck.App/Services/IDialogService.cs
Task<bool> ConfirmAsync(string message, string title, string confirmText = "Evet");
// WpfDialogService.ConfirmAsync(message, title, confirmText = "Evet") → _drawers.ShowAsync(title, d => MessageDrawer.ForConfirm(d, message, confirmText))
// ── Görev 7 ── OrderDeck.App/Views/Drawers/MessageDrawer.xaml.cs
public static MessageDrawer ForConfirm(Drawer drawer, string message, string confirmText = "Evet");
// özel ctor (Drawer, string message, DialogSeverity, bool asQuestion, string confirmText): ConfirmButton.Content = asQuestion ? confirmText : "Tamam"
// ── Görev 7 ── OrderDeck.App/Formatting/CustomerIdentityText.cs (yeni)
namespace OrderDeck.App.Formatting;
public static class CustomerIdentityText
{
    public static string PlatformLabel(string? platform);   // "instagram"→"Instagram", "youtube"→"YouTube", "facebook"→"Facebook",
                                                            // "tiktok"→"TikTok", "twitch"→"Twitch", boş→"—", öbürü olduğu gibi
    public static string Handle(Customer customer);         // YouTube → customer.Display, öbürü → customer.Username
}
// CustomerDetailViewModel'in özel PlatformLabel/IdentityHandle'ı SİLİNİR (:429-450); :207 rozeti
//     CustomerIdentityText.PlatformLabel/Handle'ı doğrudan çağırır (metin ve davranış aynı).
// ── Görev 7 ── OrderDeck.App/ViewModels/CustomerSearchViewModel.cs
public CustomerSearchViewModel(CustomerRepository customers, CustomerService customerService, SessionRepository sessions,
    LabelRepository labels, PaymentRequestService paymentService, IDialogService dialogService,
    LicenseApiClient? api = null);                                      // YENİ son parametre
internal const string UnmergeTitle = "Grubu ayır";
internal const string UnmergeButton = "Ayır";
internal const string UnmergeFallbackMessage =
    "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır.";
internal static string? UnmergeMessage(IReadOnlyList<Customer> localMembers, IReadOnlyList<CustomerBalanceMemberDto> serverMembers);
// null → soru yok (bütün tutarlar 0); eşlenemeyen canlı üyede bakiye ≠ 0 → UnmergeFallbackMessage (Sahip sorusu 1'in önerisi);
// aksi: "Ayırınca her hesap kendi bakiyesiyle kalır: " + string.Join(" · ", canlılar.Concat(silinmişler))
//     + ". Başka hesaptan bu kişinin alışverişine kullanılmış tutarlar, o satış geri alınmadıkça geri dönmez."
// canlı: $"{CustomerIdentityText.PlatformLabel(c.Platform)} {(h.StartsWith('@') ? h : "@" + h)}: {TrFormats.Currency(m.Balance)}"
// silinmiş: $"(silinmiş hesap): {TrFormats.Currency(m.Balance)} — ayırınca kullanılamaz"
[RelayCommand] private async Task UnmergeGroupAsync(CustomerCard? card);   // komut adı UnmergeGroupCommand (XAML aynı)
// ── Görev 7 ── test: OrderDeck.Tests/Fakes/FakeDialogService.cs
public List<string> ConfirmTexts { get; } = new();                       // çekmece onaylarının onay düğmesi yazıları, sırayla
public Task<bool> ConfirmAsync(string message, string title, string confirmText = "Evet");   // ConfirmTexts'e ekler, Confirm(message, title)
// ── Görev 7 ── test: OrderDeck.Tests/TestHelpers/BalanceApiStub.cs — GET /api/panel/customers/{id}/balance
public List<(Guid WpfCustomerId, decimal Balance, bool Purged)> PanelMembers { get; } = new();
public decimal PanelBalance { get; set; }                                // balance.balance
public bool PanelFails { get; set; }                                     // true → 404 problem "Not Found"
public List<Guid> PanelBalanceRequests { get; } = new();                 // istenen Id'ler, sırayla (_sync altında)

// ── Görev 8 ── OrderDeck.App/ViewModels/CustomerDetailViewModel.cs
internal bool TryGetProjectionId(out Guid id);   // private → internal. _customerId GUID ("N") ise o; değilse
                                                 // _customers.GetPaymentPerson(_customerId).OwnerId ("N"); ikisi de yoksa false
// ReloadBalanceAsync: TryGetProjectionId atarsa var olan "Bakiye yüklenemedi: …" metni (yeni metin yok)
```

**Kullanıcıya görünen metinler (spec'in aynısı):** "Henüz eşitlenmedi" / `NotSyncedText` / `NotSyncedPersistentText` (spec §6.1-b); "Grubu ayır", [Ayır] [Vazgeç], onay metni şablonu ve "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır." (spec §6.1-e); not metinleri (spec §6.1-d tablosu); "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor." (değişmez). Değişmeyen: "Bakiye doğrulanamadı" uyarısı (`client-update-required` ve öbür belirsizler).

## Sahip soruları

1. **Ayır onayında kartta olmayan canlı sunucu üyesi (spec §6.1-e, yalnız "yerel satırlara eşlenir" diyor).** Sunucunun `members[]`'inde olup bu PC'nin kartında bulunmayan, silinmemiş bir üye olabilir (başka PC birleştirdi, akış henüz inmedi; ya da yerel kopya Id'si sunucuda asıl kayda çözülmüş). Platform ve kullanıcı adı yerelde yok. Seçenekler:
   - **(A) Bakiyesi ≠ 0 ise liste yerine genel uyarı** ("Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır."), 0 ise listeden çıkar — spec'in var olan metni, yeni metin yok. **Öneri: A** (plan bunu uygular; Görev 7).
   - (B) Yeni bir satır metni (ör. "(bu bilgisayarda görünmeyen hesap): X TL") — spec'te olmayan metin.
   - (C) Hiç gösterme — tutar gizlenir, onay metni eksik dağılım gösterir.
   Burak farklı karar verirse yalnız `CustomerSearchViewModel.UnmergeMessage` ve iki testi değişir.
   - **Ters yön (bilgi):** sunucuya gönderilmiş ama grup değişikliği sunucuya henüz ulaşmamış kart üyesi sahibin `members[]`'inde görünmez; sahibin grubundaki tutarlar 0 ise Ayır sorusuz geçer ve o satırın ≠ 0 bakiyesi gösterilmez (geçici pencere, spec §5.2; para taşınmaz — K1). Plan bunu değiştirmez; Burak isterse sonraki karar yalnız `UnmergeMessage`'ı etkiler.
2. **Gönderilemeyen sahipteki TAZE iş yeni adaya taşınsın mı (spec §8.4-3, Görev 1-a).** Sahip kuralı yalnız YENİ işi düzeltir; bugün gönderilemeyen satırda açılmış iş (`cumulative` kalıcı, `FindOrCreateForPerson` `ClosedAt` süzmez) A'da kalır: satır kipinde o kapsamda öbür üyenin bakiyesi kullanılmaz, havuz kipinde düşüm yazılamaz. (A) Spec'teki gibi taşınmaz, kod yok, büyüklük Görev 1'in salt okunur sorgusuyla (saha notu) ölçülür. **Öneri: A** (plan bunu uygular). (B) Taze iş (`created`, `ApplyKey` NULL, açık) var olan `ReassignOwner` koşuluyla taşınır — spec §8.4-3 değişir, ayrı görev.
3. **5 dakika kuralında "art arda" aralığı (spec §6.1-b "5 dakikadan uzun sürerse", aralık vermez; Görev 4, fark 9).** Aynı üye kümesi aynı `MemberKey`'i üretir (`PaymentPerson.KeyOf` sıralar): ayırıp yeniden birleştirme sayacı sıfırlamaz. Aralık olmasa operatör önerilen kurtarmayı (ayır + yeniden birleştir) yaptıktan sonraki ilk kısa 409'da, ya da saatler sonraki ilk 409'da uzun metni anında görür ve günlüğe `Warning` düşer. (A) Önceki 409'dan bu yana **30 dk** aşıldıysa ilk görülme yeniden başlar (`NotSyncedStreakGap`). **Öneri: A** (plan bunu uygular; 5 dk eşiğinin ve testlerin 6 dk adımlarının çok üstünde). (B) Başka bir değer — yalnız sabit değişir, `Uzun_aradan_sonraki_ilk_esitlenmeme_kisa_metinle_baslar` 40 dk'yı yeni değerin üstüne çeker. (C) Aralık yok — kayıt yalnız kesin sonuç/soruyla silinir; davranış yukarıdaki gibi bilinen sınır olur.
4. **§10 numara izlemesi hakemi `:699` (12.10 kararının yan etkisi; Görev 9 Not).** Spec `:699`'daki §8.1 uyarısı seçeneği 12.10 kararından sonra hâlâ geçerli mi? Ayır engellenmediği için §7.3-4 kaleminin §8.1 uyarısı, meşru bir Ayır sonucundan ayırt edilemez; `:699`'un "Ayır … engellenirse" cümlesi bayatladı. Hakem kararı açık; bu plan spec'in o satırını değiştirmez ve W-havuz'a kod düşmez. (A) Seçenek kalkar, `:699` bir sonraki spec düzeltmesinde güncellenir. (B) Seçenek başka bir gerekçeyle kalır.
5. **Ayır sorusunun çevrimdışı gecikmesi (Görev 7; saha notu 3).** Satır kipinde de Ayır'ın sorusu panel okumasını bekler. Ağ kesikken istemcinin yeniden deneme politikası (`AppHost.cs:451`, `AddStandardResilienceHandler`: 3 yeniden deneme, üstel bekleme, toplam zaman aşımı 30 sn) yüzünden soru ~30 sn'ye kadar gecikir, düğme o sürede pasif kalır, ardından genel uyarı gelir (bugün Ayır anlıktır). (A) Kabul; kod yok. **Öneri: A** (plan bunu uygular; çevrimdışı Ayır seyrek, genel uyarı güvenli). (B) Panel okumasına kısa bir iptal süresi; iptal genel uyarıya düşer — ayrı görev ve test.
6. **Sahip adayı olmayan kişide Resend/Merge koruması (Görev 1-b, fark 27).** Bütün GUID'li üyeleri gönderilemeyen kişide `OwnerId` null olur, kişi × kapsam işi açılmaz: aynı kapsamda ikinci tıklama "tekrar gönderilsin mi?" sorulmadan gider, teslim kaydı yazılmaz (GUID'siz kişinin bugünkü yolu). (A) Spec'teki gibi: aday yoksa iş yok; kod yok, davranış `Gonderilemeyen_tek_uyeli_kiside_eski_teslim_okunmaz_ikinci_tiklama_sorusuz_gider` ile kilitli; büyüklük saha notu 1a'nın `adaysiz` satırlarıyla ölçülür. **Öneri: A** (plan bunu uygular). (B) İş dalı "GUID'li üye var" koşuluna bağlanır, sahip adayı yoksa önizleme/apply atlanır (bakiye 0), kişi × kapsam işi ve soruları kalır — spec §6.1-a değişir, işin `CustomerId`'si gönderilemeyen satır olur (Sahip sorusu 2'nin taşıma sorusu yeni işlere de yayılır); ayrı görev, yeni servis testleri.

---

## Görev 1: Sahip kuralı: `ServerCustomerRule` Core'a, `PaymentPersonMember` Platform/Username, `IsOwnerCandidate` (spec §6.1-a, M14, §8.4-3)

**Files:**
- Oluştur: `OrderDeck.Core/Customers/ServerCustomerRule.cs`
- Değiştir: `OrderDeck.Core/Customers/PaymentPerson.cs` (`PaymentPersonMember` belge + kayıt `:7-16`; `OwnerId` belgesi `:49-50`; `Single` `:78-83`; `PaymentOwnerRule` belgesi `:90-104`; `IsOwnerCapable` belgesi `:107`, ardına yeni `IsOwnerCandidate`; `PickOwner` `:122-124`), `OrderDeck.Core/Storage/Repositories/PaymentPersonSql.cs` (üye kurulumu `:33-36`, `Columns` `:39-40`, `Row` `:42-49`), `OrderDeck.App/Services/Sync/WpfCustomerProjectionSyncService.cs` (`using`'ler `:1-7`; özel sabitler ve `IsServerAcceptable` `:97-114` SİLİNİR; çağrının gerekçe yorumu + çağrı `:200-215`), `OrderDeck.App/Services/PaymentRequestService.cs` (yalnız yorum `:694-695`), `OrderDeck.App/Services/Sync/PaymentLeaseCoordinator.cs` (yalnız yorum ve günlük metni: `:161-162`, `:686-689`, `:712-713`)
- Test (oluştur): `OrderDeck.Tests/Customers/ServerCustomerRuleTests.cs`
- Test (genişlet): `OrderDeck.Tests/Customers/PaymentOwnerRuleTests.cs` (sınıf belgesi `:12-17`, `VectorMember` `:20`, okuma `:29-32`, yardımcılar `:39-45` ardına, vektör kurucusu `:63-65`, yeni olgu `:92`'den sonra), `OrderDeck.Tests/Customers/PaymentOwnerRuleVectors.json` (`_aciklama` `:2`, beş yeni vektör `:89`'dan sonra), `OrderDeck.Tests/Storage/PaymentPersonTests.cs` (`Insert` yardımcısı `:31-37`, beş yeni test sınıf sonunda), `OrderDeck.Tests/Storage/PaymentJobStoreContractTests.cs` (InMemory fikstürü `:947`, `:959` — iddia değişmez), `OrderDeck.Tests/Sync/PaymentLeaseTests.cs` (bir yeni test, `Begin_cevrimdisi_GUIDsiz_ama_uzak_yayinda_etiketi_olan_kiside_mesaj_yok`'tan (`:568-594`) sonra), `OrderDeck.Tests/Services/PaymentRequestServiceTests.Person.cs` (bir yeni test, `Tiklanan_satir_degil_isin_sahip_satiri_sunucuya_gider`'den (`:95-124`) sonra)

**Not (spec ↔ kod):**
- **İskelet farkı 2 bu görevde uygulanır.** Spec §6.1-a `IsOwnerCapable`'ın kendisini "GUID ve sunucunun kabul edeceği satır" yapar. Ama aynı yüklem Id dizesiyle "sunucuya Id olarak gidebilir mi" sorusuna da cevap veriyor: miras işler `PaymentRequestService.cs:963`, kapsam adayları `:1470` ve `:1474`, ve Görev 3'ün `memberIds` süzgeci (silinmiş üyeler dahil). Bu çağıranların elinde yalnız Id var, Platform/Username yok. Bu yüzden `IsOwnerCapable(string)` GUID "N" olarak **kalır** (gövde aynı, yalnız belgesi değişir). Yeni `PaymentOwnerRule.IsOwnerCandidate(PaymentPersonMember)` = `IsOwnerCapable(Id) ∧ ServerCustomerRule.IsServerAcceptable(Platform, Username)`; onu yalnız `PickOwner` kullanır. `Order` değişmez: ilk anahtarı `IsOwnerCapable` kalır, GUID'li ama gönderilemeyen üye listede yerinde durur, `PickOwner` onu atlar. Adayların kendi aralarındaki sıra (silinmemiş → telefonlu → Id) aynı olduğu için seçilen sahip "önce aday, sonra 2–4" sırasıyla eşdeğerdir. `MemberIds` sırası, `MemberKey` ve `ContactPhone` yedeği değişmez.
- **İskelet farkı 3.** `PaymentPersonMember` konumsal kuruluyor. Yeni alanlar sona varsayılanlı eklenir (`string? Platform = null, string? Username = null`), böylece var olan çağrılar derlenir. Üretimdeki iki kurucu alanları doldurur: `PaymentPersonSql` ve `PaymentPerson.Single`. `Single`'ın doldurması şart, çünkü testler `_customers` null iken `Single` yolundan geçiyor (`PaymentRequestService.cs:532`); doldurmasa GUID'li tek satır bile sahipsiz kalırdı. Elle kurulan test üyeleri alan taşımazsa yeni kuralla sahipsiz kalır. Bilerek değişen iki fikstür var, iddiaları değişmez: InMemory sözleşme fikstürü (`PaymentJobStoreContractTests.cs:947`, `:959`; SQLite fikstürü `Insert` `:902-908`, değerleri `:905`'te: `"instagram"`, `"u" + id[..10]`) ve vektör kurucusu (`PaymentOwnerRuleTests.cs:64`).
- **Satır numaraları.** Spec'in verdiği `IsServerAcceptable` `:109` bugün `:109-113`'te, sabitleri `:103-104`'te, gerekçe yorumu `:97-102` ve `:106-108`'de, kullanımı `:215-222`'de (çağrının kendi gerekçe yorumu `:200-214`). `PaymentPersonMember` hâlâ `:11-16`'da.
- **Senkron yorumundaki bayat sunucu iddiası.** `WpfCustomerProjectionSyncService.cs:200-202` ve `:101-102` "sunucu PARTİNİN TAMAMINI 400'le reddediyor" der. Bugünkü sunucu böyle yapmıyor: `LicensesWpfCustomersSyncController.cs:190-200` geçersiz öğeyi `invalid` listesine alır, günlüğe yazar, yazmadan sayar ve partinin geri kalanını işler. 400 ile reddetme eski sunucunun davranışıydı (2026-08-14 olayı). `ServerCustomerRule`'ın yeni belgesi ve bu görevde düzenlenen çağrı yorumu bugünkü davranışı yazar; istemcideki eleme yine gerekir (eski sunucu, ve satır sunucuya hiç yazılmaz).
- **Satır kipindeki etki (bugünkü bütün lisanslar), spec §6.1 (a).** Değişen tek şey şu: kişinin GUID'li ama sunucunun reddettiği üyesi (Username boş, Platform ya da Username sınırı aşıyor) artık YENİ işin sahibi olmaz.
  - **Karışık kişi.** Yeni işin sahibi, gönderilebilir GUID'li üyeye geçer. Bugün gönderilemeyen satır sahip olunca önizleme sunucunun bilmediği satırı soruyordu. Sunucu bilmediği satırda 200 `Balance = 0` döner (`LicensesCustomerBalanceApplyController.cs:173`). Sonuç `MarkNoBalance` oluyordu ve öbür üyenin bakiyesi hiç kullanılmıyordu. Kuralın amacı da bunu düzeltmek.
  - **Var olan işler — düzelmez.** Düzelme yalnız YENİ işte olur (session kapsamında yeni yayında). Bugün gönderilemeyen bir satırda açılmış iş üye taramasıyla bulunmaya devam eder: `FindOrCreateForPerson` (`PaymentJobRepository.cs:391-461`) `CustomerId IN @ids AND ScopeKey=@scopeKey` sorgusunda `ClosedAt` süzmez ve anahtar `(CustomerId, ScopeKey)` başına tektir (`UX_PaymentJob_Scope`). `cumulative` kapsamındaki iş bu yüzden kalıcıdır: her tıklamada aynı satır revize edilir ve iş A'da (gönderilemeyen satırda) kalır. Satır kipinde her tıklama A için önizleme 200 `Balance = 0` → `MarkNoBalance` olur; B'nin bakiyesi hiç kullanılmaz (bugünkü davranışın aynısı, Wh onu düzeltmez). Havuz kipinde çapa A sunucuda olmadığı için düşüm yazılamaz: M14'ün önlemek istediği kalıcı durum bu işte sürer. Hiçbir yol işi taşımaz: `ReassignOwner` (`PaymentJobRepository.cs:514-522`) yalnız sunucuda B'de kapsam izi varken çağrılır (`PaymentRequestService.cs:1546`). Spec §8.4-3 "var olan işin sahibi değişmez" der; Adım 10'daki `Var_olan_isin_sahibi_kural_degisse_de_degismez` bu durumu BİLEREK kilitler. Taşıma kodu icat edilmez → **Sahip sorusu (Görev 1-a)** aşağıda. Sahadaki büyüklük için salt okunur sorgu: "Saha notu (Görev 9'a)" aşağıda.
  - **Bütün GUID'li üyeleri gönderilemeyen kişi.** `OwnerId = null` olur, bakiye işi açılmaz (`SendCoreAsync` `:696`). Bugün aynı kişi `Balance = 0` → `MarkNoBalance` ile kapanıyordu. Mesajın tutarı aynı. Ama `:696`'daki dal yalnız bakiyeyi değil, kişi × kapsam işinin BÜTÜN yerel korumasını taşır; dal atlanınca kişi GUID'siz kişinin yolundan gider:
    - **Var olan yerel işler artık okunmaz.** Bu satırda bugün açılmış işler (`cumulative` kalıcı; süren yayının `session` işi) diskte kalır ama `FindOrCreateForPerson` hiç çağrılmaz (`ResolveBalanceAsync` `:1000-1002` yalnız dalın içinde).
    - **Resend/Merge sorusu yok.** Teslim edilmiş iş için "tekrar gönderilsin mi?" (sahip kararı 2) ve para taşıyan/teslim edilmiş fazla iş için birleştirme sorusu (sahip kararı 4) sorulmaz: aynı kapsamda ikinci tıklama mesajı sorusuz yeniden gönderir.
    - **Teslim kaydı yok.** `deliveryJob` null kalır; `TryBeginSend` (`:740`) ve `MarkDelivered` (`:754`, `:765`, `:795`) çalışmaz, işe gönderim denemesi ve teslim yazılmaz.
    - **Mod On'da yerel plan uzak soruya katılmaz.** `WithLocalPlan` (`:1046` `if (person.OwnerId is null) return remote;`): kira koordinatörünün uzak sorusu yerel işin belirtecini (`AlsoConfirms`) ve birleştirme türünü taşımaz; sunucu kayıtlarından gelen tekrar gönderme sorusu yine sorulur.
    - **Fiyat düzeltmesinin "son istek" planı.** `CorrectedPaymentRequests.cs:277` `PersonScopePlan.For(jobs, legacies, person.OwnerId)`'ye sahip olarak null verir: plan sıralamasının sahip eşitlik bozucusu (`PersonJob.cs:115`) düşer, sıra `CreatedAt`/`Id` ile kurulur; yalnız mirası olan kapsamda (`jobs` boş) benimsenecek miras (`:128-135`, `survivorRow = null`) bulunmaz. Okuma sürer (yerel işler `GetPersonScopeJobs` ile okunur), yalnız eşitlik ve benimseme değişir.

    Bu, "satır kipinde değişen tek şey sahip kuralı" iddiasının dışında kalan ikinci gözlenebilir farktır (ilki aşağıdaki "Mod On etkisi"). Kod değişmez: spec §6.1-a "aday yoksa `OwnerId` null, o kişide bakiye işi açılmaz" der ve `:696` bugün de GUID'siz kişide aynı yolu izler. Adım 10'daki `Gonderilemeyen_tek_uyeli_kiside_eski_teslim_okunmaz_ikinci_tiklama_sorusuz_gider` bunu BİLEREK kilitler. Resend korumasını bu kişilerde korumak bir ürün kararıdır → **Sahip sorusu (Görev 1-b)** aşağıda; iskelette fark 27 ve Sahip sorusu 6 olarak yer alır. Etkilenen kişi büyüklüğü "Saha notu"ndaki sorgunun `Kisi = 'adaysiz'` satırlarıyla ölçülür.
- **Mod On etkisi (satış senkronu açık lisanslar; 26.10'dan beri sahada).** `PaymentLeaseCoordinator.Unreachable` (`:691-720`) yerel yolu `person.OwnerId is null && !HasRemoteLabels(...)` ile seçer (`:698-699`). Bütün GUID'li üyeleri gönderilemeyen ve kapsamda uzak etiketi olmayan kişide kira sunucusuna ulaşılamazsa bugün sonuç `FailClosed` (`BalanceUncertain`, mesaj gitmez). Görevden sonra `OwnerId` null olduğu için sonuç `null` + `RecordsLocally = true` olur: mesaj bugünkü yerel yoldan gider, kayıt bağlantı gelince yazılır — GUID'siz kişinin bugünkü yolu (§5.8, Y3). Bu bir para yolu değişikliğidir ve "satır kipinde değişen tek şey" iddiasının dışındadır; doğrudur, çünkü böyle kişide bakiye işi zaten açılmaz (sunucu satırı hiç görmez, düşüm yazılamaz) ve uzak etiketi olan kişi yine kirayı atlayamaz. Adım 10'daki `Begin_cevrimdisi_GUIDli_ama_gonderilemeyen_ve_yalniz_kendi_etiketleri_olan_kiside_yerel_yol_ve_kayit` bunu kilitler. Kod değişmez; `:161-162`, `:686-689` belgeleri ve `:712-713` günlük metni "sahip adayı (GUID'li ve gönderilebilir üye)" diye güncellenir (Adım 14).
- **Kod değişmeden yeni anlamı devralan öbür yerler.** Şu yerler de artık "GUID'li üye yoksa" değil, "sahip adayı yoksa" kolunu görür:
  - `PaymentLeaseCoordinator.cs:584`: `OwnerId ?? MemberIds[0]`.
  - `PaymentOffRecordWriter.cs:96`.
  - `PaymentRequestService.cs:696`: kişi × kapsam işinin dalı (yukarıdaki "Bütün GUID'li üyeleri gönderilemeyen kişi" maddesi: yerel işler okunmaz, Resend/Merge sorusu ve teslim kaydı yok).
  - `PaymentRequestService.cs:1046`: `WithLocalPlan`'ın erken dönüşü (mod On'da yerel plan uzak soruya katılmaz).
  - `CorrectedPaymentRequests.cs:277`: `PersonScopePlan.For`'a sahip olarak null (eşitlik bozucu ve miras benimsemesi).

  İlk ikisi için bu doğru: gönderilemeyen satır sunucuya ve başka bilgisayara hiç ulaşmaz. Son üçü bilinen bir davranış farkıdır (iskelet fark 27, Sahip sorusu 6 = Görev 1-b). Kod değişmez.
- **Sunucu.** Sunucudaki `LicensesWpfCustomersSyncController.cs:795-797` belge yorumu `WpfCustomerProjectionSyncService.IsServerAcceptable`'ı anar; bu görevden sonra yorum bayatlar. Sunucuya dokunulmaz, yalnız yorum; davranış farkı yok.

**Sahip sorusu (Görev 1-a): gönderilemeyen sahipteki TAZE iş yeni adaya taşınsın mı?** Spec §8.4-3 "var olan işin sahibi değişmez" der ve plan bunu uygular (yukarıdaki "Var olan işler" maddesi). Bunun bedeli: sahibi gönderilemeyen satır olan `cumulative` işi (ve süren yayının `session` işi) Wh'den sonra da A'da kalır; satır kipinde öbür üyenin bakiyesi o kapsamda kullanılmaz, havuz kipinde o kapsamda düşüm yazılamaz. Seçenekler:
- **(A) Spec'teki gibi: taşınmaz.** Kod yok. Etkilenen iş sayısı aşağıdaki sorguyla ölçülür ve karar ona göre verilir. **Plan bunu uygular.**
- (B) Taze iş (`State = created`, `ApplyKey IS NULL`, `ClosedAt IS NULL`) `FindOrCreateForPerson` içinde var olan `ReassignOwner` koşuluyla yeni adaya taşınır. Spec §8.4-3'ü değiştirir; yeni bir görev ve sözleşme testi gerekir. Teslim edilmiş ya da anahtarlı iş yine taşınmaz.
Burak (B) derse spec §8.4-3 güncellenir ve ayrı bir görev yazılır; bu görevdeki `Var_olan_isin_sahibi_kural_degisse_de_degismez` o görevde bilerek değişir.

**Sahip sorusu (Görev 1-b): sahip adayı olmayan kişide Resend/Merge koruması kalsın mı?** Bütün GUID'li üyeleri gönderilemeyen kişide `OwnerId` null olur ve `SendCoreAsync`'in iş dalı (`:696`) atlanır (yukarıdaki "Bütün GUID'li üyeleri gönderilemeyen kişi" maddesi). Bakiye tarafı doğru: sunucu satırı hiç görmez, düşüm yazılamaz. Ama kişi × kapsam işinin yerel koruması da gider: o kapsamda daha önce teslim edilmiş istek için "tekrar gönderilsin mi?" sorulmaz, ikinci tıklama mesajı sorusuz yeniden gönderir, teslim kaydı yazılmaz (GUID'siz kişinin bugünkü yolu). Seçenekler:
- **(A) Spec'teki gibi: aday yoksa iş yok.** Kod yok; davranış `Gonderilemeyen_tek_uyeli_kiside_eski_teslim_okunmaz_ikinci_tiklama_sorusuz_gider` ile kilitli. Etkilenen kişi büyüklüğü aşağıdaki sorgunun `Kisi = 'adaysiz'` satırlarıyla ölçülür. **Plan bunu uygular.**
- (B) İş dalı "GUID'li üye var" koşuluna bağlanır (`IsOwnerCapable`), sahip adayı yoksa önizleme/apply atlanır: kişi × kapsam işi ve soruları kalır, bakiye 0 sayılır. Spec §6.1-a'nın "o kişide bakiye işi açılmaz" cümlesini değiştirir; işin `CustomerId`'si gönderilemeyen satır olur ve bu, Görev 1-a'nın taşıma sorusunu yeni işlere de yayar. Ayrı görev, yeni servis testleri ve spec güncellemesi gerekir; bu planda kod icat edilmez.
Burak (B) derse spec §6.1-a güncellenir ve ayrı bir görev yazılır; Adım 10'daki `Gonderilemeyen_tek_uyeli_kiside_…` o görevde bilerek değişir.

**Saha notu (planın sonundaki "Sürüm / saha notları" 1a maddesi).** Wh'den önce her yayın PC'sinin verisiyle, sahibi gönderilemeyen (`ServerCustomerRule`'ı tutmayan) işleri sayan salt okunur ölçüm; komut ve kopyalama adımları orada (yayın PC'lerinde `sqlite3` yok; sorgu geliştirme PC'sinde Python'un `sqlite3` modülüyle, `mode=ro`). Çıktıda yalnız sayılar var (ad, telefon, Id yok). `Kisi` sütunu iki sahip sorusunu ayırır: `karisik` = kişinin gönderilebilir başka GUID'li üyesi var (Görev 1-a: iş eski satırda kalır), `adaysiz` = kişinin hiç sahip adayı yok (Görev 1-b: iş artık hiç okunmaz).

- [ ] **Adım 1: `ServerCustomerRule` testleri (kırmızı)**

`OrderDeck.Tests/Customers/ServerCustomerRuleTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Customers;
using Xunit;

namespace OrderDeck.Tests.Customers;

/// <summary>
/// Sunucunun kabul edeceği müşteri satırı (<see cref="ServerCustomerRule"/>) — ortak bakiye spec §6.1-a, M14.
/// Müşteri senkronu ve ödeme sahibi kuralı aynı tanımı kullanır; sınırlar sunucunun kolon sınırlarıyla
/// (<c>LicensesWpfCustomersSyncController.IsAcceptable</c>: Platform 32, Username 128) aynı. Uzun değerler
/// <c>new string(c, n)</c> ile üretilir.
/// </summary>
public sealed class ServerCustomerRuleTests
{
    public static TheoryData<string?, string?, bool> Sinirlar => new()
    {
        { null, "u", false },
        { " ", "u", false },
        { "instagram", null, false },
        { "instagram", "", false },
        { new string('p', 32), "u", true },
        { new string('p', 33), "u", false },
        { "instagram", new string('u', 128), true },
        { "instagram", new string('u', 129), false },
    };

    [Theory] // §6.1-a: boş/boşluk ya da sınırı aşan Platform/Username sunucuya gönderilemez
    [MemberData(nameof(Sinirlar))]
    public void Platform_ve_kullanici_adi_sinirlari(string? platform, string? username, bool beklenen)
        => ServerCustomerRule.IsServerAcceptable(platform, username).Should().Be(beklenen,
            "platform uzunluğu {0}, kullanıcı adı uzunluğu {1}", platform?.Length, username?.Length);

    [Fact] // Sunucunun kolon sınırları: biri değişirse istemcideki eleme de değişmeli
    public void Sabitler_sunucu_sinirlariyla_ayni()
    {
        ServerCustomerRule.MaxPlatformLength.Should().Be(32);
        ServerCustomerRule.MaxUsernameLength.Should().Be(128);
    }
}
```

- [ ] **Adım 2: Koştur — beklenen FAIL**

Run (PowerShell): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ServerCustomerRuleTests"`
Expected: derleme FAIL: `ServerCustomerRuleTests.cs`'te `error CS0103: The name 'ServerCustomerRule' does not exist in the current context`. Sınıf henüz yok.

- [ ] **Adım 3: `ServerCustomerRule`'ı Core'a ekle**

`OrderDeck.Core/Customers/ServerCustomerRule.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Sunucunun bir müşteri satırını kabul edip etmeyeceği — TEK tanım. Sınırlar sunucunun kabul sınırlarının
/// (<c>LicensesWpfCustomersSyncController.IsAcceptable</c>, kolon sınırları) AYNISI. Burada tekrarlanıyor, çünkü tek
/// amaç "sunucu bunu reddeder mi?" sorusunu göndermeden önce cevaplamak. İki kullanıcı aynı tanımı kullanır:
/// <list type="bullet">
/// <item>Müşteri senkronu (<c>WpfCustomerProjectionSyncService</c>). Sunucu böyle satırı yazmaz: bugün yazılmadan
///   sayılır (<c>LicensesWpfCustomersSyncController</c>, <c>invalid</c> kolu). Eski sunucu partinin TAMAMINI 400'le
///   reddederdi; imleç ilerlemediği için 2026-08-14'te Username'i boş tek kayıt 565 müşteriyi 12 gün bekletti. İki
///   durumda da satır sunucuya hiç ulaşmaz; bozuk satır bu yüzden istemcide de elenir.</item>
/// <item>Ödeme sahibi kuralı (<c>PaymentOwnerRule.IsOwnerCandidate</c>). Elenen satır sunucuya hiç ulaşmaz; sahip
///   olsaydı düşüm çapası hiç yazılamaz, kişide kalıcı <c>customer-not-synced</c> olurdu (ortak bakiye §6.1-a, M14).</item>
/// </list>
/// <para>Sunucudaki doğrulama aynen kalmalı: sınır orada. Sunucu sınırı bir gün gevşerse buradaki eleme fazladan satır
/// atlar (hata değil, yalnız gecikme); sıkılaşırsa sunucu satırı yazmadan sayar ve bu sabitler güncellenmeli. Değerler
/// bu yüzden kaynağıyla birlikte anılıyor.</para>
/// </summary>
public static class ServerCustomerRule
{
    /// <summary>Sunucunun Platform kolon sınırı.</summary>
    public const int MaxPlatformLength = 32;

    /// <summary>Sunucunun Username kolon sınırı.</summary>
    public const int MaxUsernameLength = 128;

    /// <summary>Satır sunucuya gönderilebilir mi: Platform ve Username boş/boşluk değil ve sınırları içinde.
    /// <c>[NotNullWhen(true)]</c>: "kabul edilebilir" demek aynı zamanda "boş değil" demek; çağıranın null akışı
    /// <c>true</c> kolunda iki değeri null değil bilir.</summary>
    public static bool IsServerAcceptable(
        [NotNullWhen(true)] string? platform,
        [NotNullWhen(true)] string? username)
        => !string.IsNullOrWhiteSpace(platform) && platform.Length <= MaxPlatformLength
        && !string.IsNullOrWhiteSpace(username) && username.Length <= MaxUsernameLength;
}
```

- [ ] **Adım 4: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ServerCustomerRuleTests"`
Expected: PASS (8 teori durumu + 1 olgu).

- [ ] **Adım 5: Müşteri senkronunu Core'daki tanıma bağla (davranış aynı)**

Önce Grep: `OrderDeck.App/Services/Sync/WpfCustomerProjectionSyncService.cs` içinde `NotNullWhen|MaybeNull|DoesNotReturn|SuppressMessage|CodeAnalysis`. `7960eba3`'te yalnız `:1` (`using`), `:106` (`// [NotNullWhen(true)]:` yorumu) ve `:110-111` (öznitelikler) çıkar; üçü de aşağıda silinen blokta. Silinen blok (`:97-114`) DIŞINDA bir eşleşme çıkarsa `using System.Diagnostics.CodeAnalysis;` kalır.

`OrderDeck.App/Services/Sync/WpfCustomerProjectionSyncService.cs` `:1-7`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
```

şununla değişir:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
```

`OrderDeck.Core.Customers`'ın türleri (`Customer`, `CustomerBusySet`, …) bu dosyada yalnız yorum ve günlük dizelerinde geçiyor. Yeni `using` ad çakışması yapmaz.

`:97-114`, yani ctor'un kapanışından sonraki blok (sondaki boş satır dahil):

```csharp
    // Sunucunun kabul sınırları (LicensesWpfCustomersSyncController "Validate
    // input items minimally" bloğu). Burada AYNEN tekrarlanıyor, çünkü tek amaç
    // "sunucu bunu reddeder mi?" sorusunu göndermeden önce cevaplamak. Sunucu
    // sınırı bir gün gevşerse buradaki eleme fazladan kayıt atlar (hata değil,
    // yalnız gecikme); sıkılaşırsa yine parti reddedilir ve bu sabitler
    // güncellenmeli — bu yüzden değerler kaynağıyla birlikte anılıyor.
    private const int MaxPlatformLength = 32;
    private const int MaxUsernameLength = 128;

    // [NotNullWhen(true)]: "kabul edilebilir" demek aynı zamanda "boş değil"
    // demek. Bu olmadan aşağıdaki uyarı satırındaki `?.` derleyiciyi şüpheye
    // düşürüp WpfCustomerSyncItem kurulumunda CS8604 veriyor.
    private static bool IsServerAcceptable(
        [NotNullWhen(true)] string? platform,
        [NotNullWhen(true)] string? username)
        => !string.IsNullOrWhiteSpace(platform) && platform.Length <= MaxPlatformLength
        && !string.IsNullOrWhiteSpace(username) && username.Length <= MaxUsernameLength;

```

SİLİNİR. Gerekçe yorumu `ServerCustomerRule`'ın sınıf belgesine taşındı. Ctor'un `}`'u ile `/// <summary>` (`SyncOnceAsync`) arasında tek boş satır kalır.

`:200-215`, yani `SyncCoreAsync` içindeki gerekçe yorumu ve çağrı. Yorumun ilk iki satırı bayat bir sunucu davranışını şimdiki zamanla yazıyor ("PARTİNİN TAMAMINI 400'le reddediyor"); bugünkü sunucu satırı yazmadan sayıyor (`LicensesWpfCustomersSyncController.cs:190-200`). Yalnız yorum düzelir, davranış aynı:

```csharp
                // Sunucu boş/aşırı uzun Platform veya Username gördüğünde
                // PARTİNİN TAMAMINI 400'le reddediyor
                // (LicensesWpfCustomersSyncController). Watermark hatada
                // ilerlemediği için tek bir bozuk satır boru hattını KALICI
                // kilitler: aynı parti her turda yeniden gönderilir, arkasındaki
                // herkes rehin kalır ve tek belirti dakikada bir düşen bir
                // uyarı satırı olur. 2026-08-14'te sahada tam olarak bu yaşandı
                // — Facebook App Review onayından önce açılmış, Username'i boş
                // TEK kayıt 565 müşteriyi 12 gün boyunca sunucuya ulaştırmadı.
                //
                // Bu yüzden bozuk satır burada elenir. Sunucudaki doğrulama
                // aynen kalmalı: sınır orada, bu yalnız istemcinin kendi
                // kuyruğunu zehirlememesi. Elenen kayıt zaten eşleşemezdi —
                // sunucu tarafı match'i (LicenseId, Platform, Username) üçlüsüne
                // dayanıyor, bu alanlar boşken eşleşecek bir şey yok.
                if (!IsServerAcceptable(c.Platform, c.Username))
```

şununla değişir:

```csharp
                // Sunucu boş/aşırı uzun Platform veya Username taşıyan satırı
                // yazmaz: bugün yazılmadan sayılır (LicensesWpfCustomersSyncController,
                // 'invalid' kolu). Eski sunucu PARTİNİN TAMAMINI 400'le reddederdi;
                // watermark hatada ilerlemediği için tek bir bozuk satır boru hattını
                // KALICI kilitlerdi: aynı parti her turda yeniden gönderilir, arkasındaki
                // herkes rehin kalırdı. 2026-08-14'te sahada tam olarak bu yaşandı
                // — Facebook App Review onayından önce açılmış, Username'i boş
                // TEK kayıt 565 müşteriyi 12 gün boyunca sunucuya ulaştırmadı.
                //
                // Bu yüzden bozuk satır burada da elenir. Sunucudaki doğrulama
                // aynen kalmalı: sınır orada, bu yalnız istemcinin kendi
                // kuyruğunu zehirlememesi. Elenen kayıt zaten eşleşemezdi —
                // sunucu tarafı match'i (LicenseId, Platform, Username) üçlüsüne
                // dayanıyor, bu alanlar boşken eşleşecek bir şey yok. Tanım Core'da tek
                // (ServerCustomerRule): ödeme sahibi kuralı da gönderilemeyen satırı aynı
                // tanımla eler (ortak bakiye §6.1-a).
                if (!ServerCustomerRule.IsServerAcceptable(c.Platform, c.Username))
```

`[NotNullWhen(true)]` Core'daki yöntemde korunur; uyarı satırındaki (`:216-221`) `?.` sonrası akış durumu bugünküyle aynı kalır. `ToItem` çağrısı (`:230`, yöntem `:304`) `c`'yi bütün olarak alır, öznitelik orada rol oynamaz.

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~WpfCustomerProjectionSyncServiceTests|FullyQualifiedName~ServerCustomerRuleTests"`
Expected: PASS. `SyncOnce_customer_the_server_would_reject_is_skipped` (`WpfCustomerProjectionSyncServiceTests.cs:323`) değişmeden yeşil kalır; bu, senkron servisinin Core'daki aynı tanımı kullandığının kanıtı.

- [ ] **Adım 6: Üye alanlarının testleri (kırmızı)**

`OrderDeck.Tests/Storage/PaymentPersonTests.cs` `:31-37`, `Insert` yardımcısı: isteğe bağlı kullanıcı adı alır, var olan çağrılar aynen derlenir.

```csharp
    private string Insert(string? phone = null, string? groupId = null, string platform = "instagram")
    {
        var id = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(id, platform, "u" + id[..8], null, null, 1, 1, false, null, null,
            0, 0m, null, null, phone, GroupId: groupId));
        return id;
    }
```

şununla değişir:

```csharp
    private string Insert(string? phone = null, string? groupId = null, string platform = "instagram",
        string? username = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(id, platform, username ?? "u" + id[..8], null, null, 1, 1, false, null, null,
            0, 0m, null, null, phone, GroupId: groupId));
        return id;
    }
```

Sınıfın sonuna (son testin `}`'undan, `:129`'dan sonra; sınıfın kapanış `}`'undan önce) ekle:

```csharp

    // Ortak bakiye §6.1-a: üye, sahip kuralının girdisi olan Platform/Username'i satırdan taşır
    // (senkronlanan, yakınsayan alanlar — iki bilgisayar aynı girdiyi görür).
    [Fact]
    public void Uyeler_platform_ve_kullanici_adini_tasir()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(groupId: group);
        var b = Insert(groupId: group, platform: "tiktok");

        var person = _customers.GetPaymentPerson(a);

        foreach (var id in new[] { a, b })
        {
            var row = _customers.GetById(id)!;
            var member = person.Members.Single(m => m.Id == id);
            member.Platform.Should().Be(row.Platform);
            member.Username.Should().Be(row.Username);
        }
    }

    [Fact] // §6.1-a: tek satırlık kişi de gönderilebilirliği taşır — yoksa GUID'li tek satır bile sahipsiz kalırdı
    public void Tek_satirlik_kisi_platform_ve_kullanici_adini_tasir()
    {
        var id = Insert(phone: TestPhone.NewE164());
        var customer = _customers.GetById(id)!;

        var person = PaymentPerson.Single(customer);

        person.Members.Should().ContainSingle();
        person.Members[0].Platform.Should().Be(customer.Platform);
        person.Members[0].Username.Should().Be(customer.Username);
        person.OwnerId.Should().Be(customer.Id, "GUID'li ve gönderilebilir tek satır sahiptir");
    }
```

- [ ] **Adım 7: Koştur — beklenen FAIL**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentPersonTests"`
Expected: derleme FAIL: `PaymentPersonTests.cs`'te `error CS1061: 'PaymentPersonMember' does not contain a definition for 'Platform'` (ve `'Username'`).

- [ ] **Adım 8: `PaymentPersonMember`'a Platform/Username ekle; `Single` ve SQL okuması doldursun**

`OrderDeck.Core/Customers/PaymentPerson.cs` `:7-16`:

```csharp
/// <summary>Ödeme kişisinin bir üyesi (Customer satırı) — sahip kuralının ve kişi düzeyindeki
/// iletişim/kargo kararlarının girdisi. Yalnız senkronlanan ve yakınsayan alanlar: liste sırası,
/// <c>LastSeenAt</c> gibi bilgisayara özgü veriler bilerek YOK (iki bilgisayar aynı sahibi seçsin).
/// <see cref="TotalAmount"/> yalnız kartın kümülatif tutarı içindir, sahip kuralına girmez.</summary>
public sealed record PaymentPersonMember(
    string Id,
    string? Phone,
    bool Purged,
    bool RecipientPaysActive = false,
    decimal TotalAmount = 0m);
```

şununla değişir:

```csharp
/// <summary>Ödeme kişisinin bir üyesi (Customer satırı) — sahip kuralının ve kişi düzeyindeki
/// iletişim/kargo kararlarının girdisi. Yalnız senkronlanan ve yakınsayan alanlar: liste sırası,
/// <c>LastSeenAt</c> gibi bilgisayara özgü veriler bilerek YOK (iki bilgisayar aynı sahibi seçsin).
/// <see cref="Platform"/> ve <see cref="Username"/> sahip kuralının girdisidir: gönderilebilirlik
/// (<see cref="ServerCustomerRule.IsServerAcceptable"/>, ortak bakiye §6.1-a). İkisi de senkronlanan,
/// yakınsayan alanlar. Sona varsayılanlı eklendiler; alan taşımayan üye sahip adayı olmaz.
/// <see cref="TotalAmount"/> yalnız kartın kümülatif tutarı içindir, sahip kuralına girmez.</summary>
public sealed record PaymentPersonMember(
    string Id,
    string? Phone,
    bool Purged,
    bool RecipientPaysActive = false,
    decimal TotalAmount = 0m,
    string? Platform = null,
    string? Username = null);
```

`:78-83` (`Single`):

```csharp
    public static PaymentPerson Single(Customer customer)
        => new(customer.Id, new[]
        {
            new PaymentPersonMember(customer.Id, customer.Phone, Purged: false,
                customer.RecipientPaysActive, customer.TotalAmount),
        });
```

şununla değişir:

```csharp
    public static PaymentPerson Single(Customer customer)
        => new(customer.Id, new[]
        {
            new PaymentPersonMember(customer.Id, customer.Phone, Purged: false,
                customer.RecipientPaysActive, customer.TotalAmount, customer.Platform, customer.Username),
        });
```

`OrderDeck.Core/Storage/Repositories/PaymentPersonSql.cs` `:33-49`:

```csharp
        return rows
            .Select(r => new PaymentPersonMember(
                r.Id, r.Phone, r.PurgedAt is not null, r.RecipientPaysActive != 0, r.TotalAmount))
            .ToList();
    }

    private const string Columns =
        "SELECT Id, Phone, PurgedAt, RecipientPaysActive, TotalAmount FROM Customer";

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string? Phone { get; init; }
        public long? PurgedAt { get; init; }
        public int RecipientPaysActive { get; init; }
        public decimal TotalAmount { get; init; }
    }
```

şununla değişir:

```csharp
        return rows
            .Select(r => new PaymentPersonMember(
                r.Id, r.Phone, r.PurgedAt is not null, r.RecipientPaysActive != 0, r.TotalAmount,
                r.Platform, r.Username))
            .ToList();
    }

    // Platform/Username: sahip kuralının girdisi (ServerCustomerRule). İşlem içi doğrulama yalnız
    // MemberKey'e (Id kümesi) bakar; yeni kolonlar onu etkilemez.
    private const string Columns =
        "SELECT Id, Phone, PurgedAt, RecipientPaysActive, TotalAmount, Platform, Username FROM Customer";

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string? Phone { get; init; }
        public long? PurgedAt { get; init; }
        public int RecipientPaysActive { get; init; }
        public decimal TotalAmount { get; init; }
        public string? Platform { get; init; }
        public string? Username { get; init; }
    }
```

KVKK boşaltması (`CustomerRepository.ScrubAssignments` `:34-45`) Platform ve Username'e dokunmaz. Silinmiş üye bu yüzden gönderilebilirliğini korur; "hepsi silinmişse yine sahip seçilir" vektörü gerçek veride de geçerli kalır.

- [ ] **Adım 9: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentPersonTests|FullyQualifiedName~PaymentOwnerRuleTests"`
Expected: PASS. İki yeni test yeşil; sahip kuralı henüz alanlara bakmıyor, var olan testler değişmeden yeşil.

- [ ] **Adım 10: Sahip kuralının testleri (kırmızı) + iki fikstürün alanları**

`OrderDeck.Tests/Customers/PaymentOwnerRuleVectors.json`: `:2`'deki `_aciklama` şununla değişir:

```json
  "_aciklama": "PaymentOwnerRule ortak test vektörleri. Sunucu ileride aynı sahip kuralını uygularsa bu dosya ona da koşturulur. Telefonlar sembolik: valid = geçerli TR cep (test anında üretilir), invalid = numara şekilli geçersiz, none = boş. Id'ler sembolik 32 haneli GUID 'N' biçimi; 'c...' ile başlayanlar GUID değildir. platform/username gönderilebilirliktir (ServerCustomerRule, ortak bakiye §6.1-a): ok = kabul edilir (alan yoksa ok), empty = boş, long = sunucu sınırının bir fazlası (Platform 33, Username 129).",
```

Son vektörün (`telefon_oncelik_silinmeden_sonra`) kapanış `}`'u (`:89`) ile dizinin `]`'i (`:90`) arasına ekle. `:89` `    }` → `    },` olur.

```json
    {
      "name": "gonderilemeyen_guid_uye_sahip_olmaz",
      "members": [
        { "id": "a0000000000000000000000000000001", "phone": "valid", "username": "empty" },
        { "id": "a0000000000000000000000000000002", "phone": "none" }
      ],
      "owner": "a0000000000000000000000000000002"
    },
    {
      "name": "uzun_platform_gonderilemez",
      "members": [
        { "id": "a0000000000000000000000000000001", "phone": "valid", "platform": "long" },
        { "id": "a0000000000000000000000000000002", "phone": "none" }
      ],
      "owner": "a0000000000000000000000000000002"
    },
    {
      "name": "uzun_kullanici_adi_gonderilemez",
      "members": [
        { "id": "a0000000000000000000000000000001", "phone": "valid", "username": "long" },
        { "id": "a0000000000000000000000000000002", "phone": "none" }
      ],
      "owner": "a0000000000000000000000000000002"
    },
    {
      "name": "hicbir_guid_uye_gonderilemezse_sahip_yok",
      "members": [
        { "id": "a0000000000000000000000000000001", "phone": "none", "username": "empty" },
        { "id": "a0000000000000000000000000000002", "phone": "none", "platform": "empty" },
        { "id": "c1", "phone": "valid" }
      ],
      "owner": null
    },
    {
      "name": "silinmis_ama_gonderilebilir_uye_hala_aday",
      "members": [
        { "id": "a0000000000000000000000000000001", "phone": "none", "purged": true },
        { "id": "a0000000000000000000000000000002", "phone": "none", "username": "empty" }
      ],
      "owner": "a0000000000000000000000000000001"
    }
```

Var olan vektörler değişmez; alanları yok, yani `ok`.

`OrderDeck.Tests/Customers/PaymentOwnerRuleTests.cs`:

`:12-17` (sınıf belgesi):

```csharp
/// <summary>
/// Ödeme işinin sahip satırı (<see cref="PaymentOwnerRule"/>). Vektörler ortak dosyadan
/// (<c>PaymentOwnerRuleVectors.json</c>): sunucu ileride aynı kuralı uygularsa aynı küme ona da koşar.
/// Her vektör üyelerin BÜTÜN sıralamalarında ve bilgisayara özgü alanlar (ciro, alıcı ödemeli)
/// değiştirilerek de koşar — iki bilgisayar aynı kişi için aynı sahibi seçmeli.
/// </summary>
```

şununla değişir:

```csharp
/// <summary>
/// Ödeme işinin sahip satırı (<see cref="PaymentOwnerRule"/>). Vektörler ortak dosyadan
/// (<c>PaymentOwnerRuleVectors.json</c>): sunucu ileride aynı kuralı uygularsa aynı küme ona da koşar.
/// Her vektör üyelerin BÜTÜN sıralamalarında ve bilgisayara özgü alanlar (ciro, alıcı ödemeli)
/// değiştirilerek de koşar — iki bilgisayar aynı kişi için aynı sahibi seçmeli. Üyeler gönderilebilirliği
/// (platform/username: ok | empty | long) taşır: GUID'li ama sunucunun reddedeceği satır sahip olmaz
/// (ortak bakiye §6.1-a, M14).
/// </summary>
```

`:20`:

```csharp
    private sealed record VectorMember(string Id, string Phone, bool Purged);
```

şununla değişir:

```csharp
    private sealed record VectorMember(string Id, string Phone, bool Purged, string Platform, string Username);
```

`:29-32` (üye okuması):

```csharp
            v.GetProperty("members").EnumerateArray().Select(m => new VectorMember(
                m.GetProperty("id").GetString()!,
                m.GetProperty("phone").GetString()!,
                m.TryGetProperty("purged", out var p) && p.GetBoolean())).ToList(),
```

şununla değişir:

```csharp
            v.GetProperty("members").EnumerateArray().Select(m => new VectorMember(
                m.GetProperty("id").GetString()!,
                m.GetProperty("phone").GetString()!,
                m.TryGetProperty("purged", out var p) && p.GetBoolean(),
                m.TryGetProperty("platform", out var pl) ? pl.GetString()! : "ok",
                m.TryGetProperty("username", out var un) ? un.GetString()! : "ok")).ToList(),
```

`PhoneOf`'tan (`:39-45`) sonra ekle:

```csharp

    // Gönderilebilirlik (ServerCustomerRule): ok = kabul edilir, empty = boş, long = sunucu sınırının bir fazlası.
    private static string PlatformOf(string kind) => kind switch
    {
        "ok" => "instagram",
        "empty" => "",
        "long" => new string('p', 33),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "bilinmeyen platform türü"),
    };

    private static string UsernameOf(string kind) => kind switch
    {
        "ok" => $"u{Guid.NewGuid():N}"[..12],
        "empty" => "",
        "long" => new string('u', 129),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "bilinmeyen kullanıcı adı türü"),
    };
```

`:63-65`, vektör kurucusu. Bilerek değişen fikstür satırı (iskelet farkı 3); iddia aynı.

```csharp
        var members = vector.Members
            .Select(m => new PaymentPersonMember(m.Id, PhoneOf(m.Phone), m.Purged))
            .ToList();
```

şununla değişir:

```csharp
        var members = vector.Members
            .Select(m => new PaymentPersonMember(m.Id, PhoneOf(m.Phone), m.Purged,
                Platform: PlatformOf(m.Platform), Username: UsernameOf(m.Username)))
            .ToList();
```

(`noisy` satırı `m with { … }` olduğu için Platform/Username'i korur, değişmez.)

`Kisi_anahtari_uye_sirasindan_bagimsizdir`'ın (`:82-92`) üyeleri Platform/Username taşımaz; yeni kuralla iki `OwnerId` de null olur ve `ab.OwnerId == ba.OwnerId` iddiası boş yere geçer. Var olan test DEĞİŞMEZ (iskeletin "Satır kipi regresyonu" kuralı); artık yalnız `MemberKey`'i kanıtlar. Sahip eşitliğini gönderilebilir üyelerle kanıtlayan yeni olgu, `:92`'den (o testin kapanış `}`'u) sonra:

```csharp

    // Ortak bakiye §6.1-a: sahip, gönderilebilir (Platform/Username dolu) üyeler arasında da sıradan bağımsız.
    // Alan taşımayan üyeli eski olgu yeni kuralla iki null'u karşılaştırır; sahip eşitliği burada kanıtlanır.
    [Fact]
    public void Kisi_anahtari_ve_sahip_gonderilebilir_uyelerle_sirasindan_bagimsizdir()
    {
        var a = new PaymentPersonMember(Guid.NewGuid().ToString("N"), null, false,
            Platform: "instagram", Username: $"u{Guid.NewGuid():N}"[..12]);
        var b = new PaymentPersonMember(Guid.NewGuid().ToString("N"), null, false,
            Platform: "instagram", Username: $"u{Guid.NewGuid():N}"[..12]);
        var ab = new PaymentPerson(a.Id, new[] { a, b });
        var ba = new PaymentPerson(b.Id, new[] { b, a });

        ab.MemberKey.Should().Be(ba.MemberKey);
        ab.OwnerId.Should().NotBeNull("iki üye de GUID'li ve gönderilebilir");
        ab.OwnerId.Should().Be(ba.OwnerId);
    }
```

`OrderDeck.Tests/Sync/PaymentLeaseTests.cs`: mod On'daki yeni yerel yol (yukarıdaki "Mod On etkisi"). `Begin_cevrimdisi_GUIDsiz_ama_uzak_yayinda_etiketi_olan_kiside_mesaj_yok`'un (`:568-594`) kapanış `}`'undan sonra ekle. `GuidlessCustomer` (`:1514-1520`) ham Id'li müşteri kurar; burada Id GUID'li, Username boş. Uzak etiketi yok, yerel canlı yayında basılmış tek etiketi var:

```csharp

    [Fact]
    public async Task Begin_cevrimdisi_GUIDli_ama_gonderilemeyen_ve_yalniz_kendi_etiketleri_olan_kiside_yerel_yol_ve_kayit()
    {
        // Ortak bakiye §6.1-a: GUID'li ama Username'i boş satır sahip adayı değil (OwnerId null). Sunucu onu hiç
        // görmez, düşüm yazılamaz: kişi GUID'siz kişinin yolundan gider (§5.8, Y3). Bugün FailClosed'du.
        var pc = await PcAsync();
        var live = pc.StartStream();
        var customer = new Customer(Guid.NewGuid().ToString("N"), "tiktok", "", "Örnek", null,
            1, 1, false, null, null, 0, 0m, null, null, TestPhone.NewE164());
        pc.Customers.Insert(customer);
        var label = InsertLabel(pc, live, customer.Id, "tiktok", "", 100m, printed: true);
        _server.EnqueueTimeout(FakeSalesServer.PaymentRequestsBegin);
        _server.EnqueueTimeout(FakeSalesServer.PaymentRequestsBegin);

        (await RequestAsync(pc, customer, Scope(live))).Result.Should().Be(PaymentRequestResult.Opened,
            "sahip adayı olmayan, yalnız kendi etiketleri olan kişi kira alınamazsa yerel yoldan gider");

        pc.Launcher.LaunchedUrls.Should().ContainSingle();
        var record = pc.PendingPaymentRecords.List().Where(r => r.Kind == PendingPaymentRecordKinds.Record)
            .Select(PendingPaymentRecordCodec.DecodeRecord).Should().ContainSingle(
                "RecordsLocally: kayıt bağlantı gelince yazılır (W3-06'nın kirasız kaydı)").Subject;
        record.ScopeKey.Should().Be(Scope(live));
        record.State.Should().Be(PaymentRequestStates.Delivered);
        record.OrderIds.Should().Equal(Guid.ParseExact(label, "N"));
        _server.Calls(FakeSalesServer.BalancePreviewEndpoint).Should().BeEmpty("sahip yok: bakiye işi açılmaz");
    }
```

`OrderDeck.Tests/Services/PaymentRequestServiceTests.Person.cs`: satır kipinde bilerek kilitlenen ikinci fark (Not, "Bütün GUID'li üyeleri gönderilemeyen kişi"; iskelet fark 27, Sahip sorusu 6 = Görev 1-b). `Tiklanan_satir_degil_isin_sahip_satiri_sunucuya_gider`'in kapanış `}`'undan (`:124`) sonra ekle. Ön koşul işi, satır gönderilebilirken açılıp teslim edilir; sonra Username boşaltılır — Wh'den önce gönderilemeyen satırda açılmış işin diskteki hâli budur (eski kural sahibi Platform/Username'e bakmadan seçiyordu). Varsayılan `PreviewBalance = 0` → iş `MarkNoBalance` + teslim, tıpkı sahadaki gönderilemeyen satır gibi (sunucu bilmediği satırda 200 `Balance = 0`).

```csharp

    // Ortak bakiye §6.1-a (W-havuz planı fark 27): GUID'li ama sunucunun reddedeceği tek satırlı kişi artık sahip
    // adayı değil (OwnerId null) → SendCoreAsync'in iş dalı atlanır. O satırda önceden açılmış, teslim edilmiş iş
    // OKUNMAZ: "tekrar gönderilsin mi?" sorusu yok, teslim kaydı yazılmaz, ikinci mesaj sorusuz gider — GUID'siz kişinin
    // yolu. BİLEREK kilitlenen satır kipi farkı; korumayı sürdürme kararı alınırsa bu test o görevde bilerek değişir.
    [Fact]
    public async Task Gonderilemeyen_tek_uyeli_kiside_eski_teslim_okunmaz_ikinci_tiklama_sorusuz_gider()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"u{Guid.NewGuid():N}"[..12]);
            var (sut, handler) = KisiSut(db);

            // Ön koşul: satır gönderilebilirken iş açılır, bakiye yok, teslim edilir.
            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            var once = Isler(db, "session:s1").Should().ContainSingle().Subject;
            once.CustomerId.Should().Be(a.Id);
            once.IsDelivered.Should().BeTrue("ön koşul: istek teslim edildi");
            handler.PreviewCustomerIds.Should().ContainSingle("ön koşul: ilk tıklama bakiye akışına girdi");

            using (var conn = db.Factory.Open())
                Dapper.SqlMapper.Execute(conn, "UPDATE Customer SET Username = '' WHERE Id = @id", new { id = a.Id });
            db.Customers.GetPaymentPerson(a.Id).OwnerId.Should().BeNull("ön koşul: tek GUID'li üye gönderilemez");

            var ikinci = await sut.RequestPaymentAsync(a, _ => 250m, T, "session:s1");

            ikinci.Result.Should().Be(PaymentRequestResult.Opened,
                "iş dalı atlanır: eski teslim sorulmaz (eski kural AlreadyRequested derdi)");
            ikinci.Prompt.Should().BeNull();
            ikinci.OwnerId.Should().BeNull("işsiz yolda beklentinin sahibi kişinin sahibidir: yok");
            _launcher.LaunchedUrls.Should().HaveCount(2, "ikinci mesaj sorusuz gider");
            _launcher.LaunchedUrls.Should().OnlyContain(u => u.Contains("250%2C00"), "bakiye yok: iki mesaj aynı tutar");
            handler.PreviewCustomerIds.Should().ContainSingle("ikinci tıklama bakiye akışına hiç girmez");
            var sonra = Isler(db, "session:s1").Should().ContainSingle("yeni iş açılmaz").Subject;
            sonra.Id.Should().Be(once.Id);
            sonra.Revision.Should().Be(once.Revision, "iş okunmaz, revize edilmez");
            sonra.LastDeliveredAt.Should().Be(once.LastDeliveredAt, "teslim kaydı yazılmaz");
        }
        finally { Drop(db); }
    }
```

`PaymentRequestServiceTests.Person.cs`'in `using`'leri yeterli (`OrderDeck.Core.Storage.Repositories` `:10`; `Dapper.SqlMapper` tam adla, `Isler` yardımcısındaki gibi). `"250%2C00"` biçimi Repro A'daki (`:92`, `"150%2C00"`) tutar iddiasının aynısı.

`OrderDeck.Tests/Storage/PaymentPersonTests.cs`: Adım 6'da eklenen testlerin ardına (sınıfın kapanış `}`'undan önce) ekle:

```csharp

    // Ortak bakiye §6.1-a, M14: GUID'li ama sunucunun reddedeceği satır (Username boş) sahip seçilmez —
    // telefon önceliği onu öne alsa da. Gönderilebilir öbür GUID'li üye sahip olur; kişiden çıkan satır yok.
    [Fact]
    public void Kullanici_adi_bos_guidli_satir_sahip_secilmez_gonderilebilir_uye_secilir()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(phone: TestPhone.NewE164(), groupId: group, username: "");
        var b = Insert(groupId: group, platform: "tiktok");

        var person = _customers.GetPaymentPerson(a);

        person.OwnerId.Should().Be(b, "eski kural telefonlu A'yı seçerdi; A sunucuya hiç ulaşmaz");
        person.MemberIds.Should().BeEquivalentTo(new[] { a, b }, "gönderilemeyen satır kişiden çıkmaz");
    }

    [Fact] // §6.1-a: hiçbir GUID'li üye gönderilemiyorsa sahip yok — o kişide bakiye işi açılmaz
    public void Hicbir_uye_gonderilemezse_sahip_yok()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(phone: TestPhone.NewE164(), groupId: group, username: "");
        var b = Insert(groupId: group, platform: new string('p', 33));

        var person = _customers.GetPaymentPerson(a);

        person.OwnerId.Should().BeNull();
        person.MemberIds.Should().BeEquivalentTo(new[] { a, b });
    }

    // §8.4-3: kural yalnız YENİ işin sahibini seçer. Sahibi sonradan gönderilemez olan kişinin var olan işi
    // üye taramasıyla bulunur; yeni sahip onu taşımaz, ikinci iş açılmaz. BİLEREK kilitlenen sınır: o iş
    // gönderilemeyen satırda kalır (satır kipinde MarkNoBalance, havuz kipinde düşüm yazılamaz) — sahip
    // sorusu Görev 1-a; Burak taşımayı seçerse bu test o görevde bilerek değişir.
    [Fact]
    public void Var_olan_isin_sahibi_kural_degisse_de_degismez()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(phone: TestPhone.NewE164(), groupId: group);
        var b = Insert(groupId: group, platform: "tiktok");
        var jobs = new PaymentJobRepository(_db);

        var once = _customers.GetPaymentPerson(a);
        once.OwnerId.Should().Be(a, "ön koşul: telefonlu ve gönderilebilir A sahip");
        var ilk = jobs.FindOrCreateForPerson(new PersonJobRequest(
            once.LeasedId, once.MemberKey, once.MemberIds, once.OwnerId!, "session:s1", 100m));
        ilk.Status.Should().Be(PersonJobStatus.Ready);
        ilk.Job!.CustomerId.Should().Be(a);

        using (var conn = _db.Open())
            conn.Execute("UPDATE Customer SET Username = '' WHERE Id = @a", new { a });

        var sonra = _customers.GetPaymentPerson(a);
        sonra.OwnerId.Should().Be(b, "yeni işin sahibi artık gönderilebilir B");
        var ikinci = jobs.FindOrCreateForPerson(new PersonJobRequest(
            sonra.LeasedId, sonra.MemberKey, sonra.MemberIds, sonra.OwnerId!, "session:s1", 100m));

        ikinci.Status.Should().Be(PersonJobStatus.Ready);
        ikinci.Job!.Id.Should().Be(ilk.Job!.Id, "iş üye taramasıyla bulunur — ikinci iş açılmaz");
        ikinci.Job!.CustomerId.Should().Be(a, "var olan işin sahibi değişmez");
    }
```

`OrderDeck.Tests/Storage/PaymentJobStoreContractTests.cs`, InMemory fikstürü. Bilerek değişen fikstür satırları (iskelet farkı 3); iddialar aynı. Alan taşımayan üye yeni kuralla sahipsiz kalır ve `Req`'in `p.OwnerId!`'i null olurdu. Alanlar SQLite fikstürününkilerle (`Insert`, `:902-908`) aynı.

`:947`:

```csharp
        return new PaymentPerson(ids[0], ids.Select(id => new PaymentPersonMember(id, null, false)).ToList());
```

şununla değişir:

```csharp
        // SQLite fikstürüyle (Insert) aynı gönderilebilir alanlar: sahip kuralı onları ister (ortak bakiye §6.1-a).
        return new PaymentPerson(ids[0], ids.Select(id => new PaymentPersonMember(id, null, false,
            Platform: "instagram", Username: "u" + id[..10])).ToList());
```

`:959`:

```csharp
            .Select(id => new PaymentPersonMember(id, null, false)).ToList());
```

şununla değişir:

```csharp
            .Select(id => new PaymentPersonMember(id, null, false,
                Platform: "instagram", Username: "u" + id[..10])).ToList());
```

- [ ] **Adım 11: Koştur — beklenen FAIL**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentOwnerRuleTests|FullyQualifiedName~PaymentPersonTests|FullyQualifiedName~PaymentJobStoreContractTests|FullyQualifiedName~PaymentLeaseTests.Begin_cevrimdisi_GUIDli_ama_gonderilemeyen|FullyQualifiedName~PaymentRequestServiceTests.Gonderilemeyen_tek_uyeli_kiside"`
Expected: derleme geçer. Sahip kuralı henüz Platform/Username'e bakmıyor; şunlar FAIL:
- `Vektor_her_siralamada_…(name: "gonderilemeyen_guid_uye_sahip_olmaz")`, `"uzun_platform_gonderilemez"`, `"uzun_kullanici_adi_gonderilemez"`: beklenen `a…02`, bulunan `a…01` (telefonlu GUID'li üye).
- `"hicbir_guid_uye_gonderilemezse_sahip_yok"`: beklenen null, bulunan `a…01`.
- `"silinmis_ama_gonderilebilir_uye_hala_aday"`: beklenen `a…01`, bulunan `a…02` (silinmemiş önce).
- `Kullanici_adi_bos_guidli_satir_sahip_secilmez_gonderilebilir_uye_secilir`: `OwnerId` A.
- `Hicbir_uye_gonderilemezse_sahip_yok`: `OwnerId` A, null değil.
- `Var_olan_isin_sahibi_kural_degisse_de_degismez`: `sonra.OwnerId` hâlâ A.
- `Begin_cevrimdisi_GUIDli_ama_gonderilemeyen_ve_yalniz_kendi_etiketleri_olan_kiside_yerel_yol_ve_kayit`: beklenen `Opened`, bulunan `BalanceUncertain` (`OwnerId` GUID'li satır → `Unreachable` `FailClosed`).
- `Gonderilemeyen_tek_uyeli_kiside_eski_teslim_okunmaz_ikinci_tiklama_sorusuz_gider`: ön koşul `OwnerId.Should().BeNull` FAIL (eski kural Username'e bakmaz, `OwnerId` A); kural gelse bile bugünkü kod ikinci tıklamada `AlreadyRequested` derdi.

`Kisi_anahtari_ve_sahip_gonderilebilir_uyelerle_sirasindan_bagimsizdir` bu adımda YEŞİL (iki üye de GUID'li; eski kural da aynı sahibi seçer); kural değişikliğinden sonra da yeşil kalmasının kanıtı Adım 13. Var olan vektörler ve öbür testler yeşil. InMemory fikstür değişikliği bu adımda kırmızı üretmez; Adım 12'den sonra gerekir.

- [ ] **Adım 12: `IsOwnerCandidate` + `PickOwner` + belgeler**

`OrderDeck.Core/Customers/PaymentPerson.cs` `:49-50` (`OwnerId` belgesi):

```csharp
    /// <summary>Yeni bir iş açılacaksa sahibi (<see cref="PaymentOwnerRule.PickOwner"/>); GUID'li
    /// üye yoksa null — o kişide bakiye işi açılmaz (sunucu GUID olmayan Id'ye düşüm yazamaz).</summary>
```

şununla değişir:

```csharp
    /// <summary>Yeni bir iş açılacaksa sahibi (<see cref="PaymentOwnerRule.PickOwner"/>); GUID'li ve
    /// sunucunun kabul edeceği (Platform/Username, <see cref="ServerCustomerRule.IsServerAcceptable"/>) üye
    /// yoksa null — o kişide bakiye işi açılmaz (sunucu GUID olmayan Id'ye düşüm yazamaz; gönderilemeyen
    /// satır sunucuya hiç ulaşmaz).</summary>
```

`:90-104` (`PaymentOwnerRule` sınıf belgesi):

```csharp
/// <summary>
/// Yeni ödeme işinin sahip satırı — saf ve belirlenimli. Sıra:
/// <list type="number">
/// <item>GUID "N" biçimli Id (sunucu yalnız böyle Id'ye düşüm yazar; GUID'siz üye hiç sahip olmaz),</item>
/// <item>silinmemiş (<c>PurgedAt IS NULL</c>) önce,</item>
/// <item>geçerli TR telefonu olan önce (<see cref="PhoneNormalizer.IsValidTr"/> — <c>PhoneRequired</c>
///   kapısıyla aynı kural),</item>
/// <item>Id (küçük harfe indirilmiş, ordinal), eşitlikte ham Id (ordinal).</item>
/// </list>
/// <para>Liste sırası, <c>TotalAmount</c>, <c>LastSeenAt</c> KULLANILMAZ: senkron yakınsadıktan sonra
/// iki bilgisayar aynı kişi ve kapsam için aynı (WpfCustomerId, ScopeKey) çiftini seçer. Telefon
/// önceliği tek telefonlu grupta bugünkü kart birincilini korur.</para>
/// <para>Kural yalnız YENİ işin sahibini seçer: telefonu sonradan değişen bir üye var olan işin
/// sahibini değiştirmez (iş üye taramasıyla bulunur).</para>
/// </summary>
```

şununla değişir:

```csharp
/// <summary>
/// Yeni ödeme işinin sahip satırı — saf ve belirlenimli. Sıra:
/// <list type="number">
/// <item>GUID "N" biçimli Id VE sunucunun kabul edeceği Platform/Username
///   (<see cref="ServerCustomerRule.IsServerAcceptable"/>; ikisi birlikte <see cref="IsOwnerCandidate"/>):
///   sunucu yalnız böyle satıra düşüm yazar. Gönderilemeyen satır sahip olsaydı çapa hiç yazılamaz, kişide
///   kalıcı <c>customer-not-synced</c> olurdu. Gönderilemeyen satır başka bilgisayara da inmez — iki
///   bilgisayarın seçimi tutarlı kalır. GUID'siz ya da gönderilemeyen üye hiç sahip olmaz,</item>
/// <item>silinmemiş (<c>PurgedAt IS NULL</c>) önce,</item>
/// <item>geçerli TR telefonu olan önce (<see cref="PhoneNormalizer.IsValidTr"/> — <c>PhoneRequired</c>
///   kapısıyla aynı kural),</item>
/// <item>Id (küçük harfe indirilmiş, ordinal), eşitlikte ham Id (ordinal).</item>
/// </list>
/// <para><see cref="Order"/>'ın ilk anahtarı yalnız GUID'dir (<see cref="IsOwnerCapable"/>): GUID'li ama
/// gönderilemeyen üye listede yerinde kalır, <see cref="PickOwner"/> onu atlar. Adayların kendi aralarındaki
/// sıra 2–4 ile aynı olduğu için seçilen sahip yukarıdaki sıranın aynısıdır; <c>MemberIds</c> sırası ve
/// iletişim telefonu yedeği gönderilebilirlikten etkilenmez.</para>
/// <para>Liste sırası, <c>TotalAmount</c>, <c>LastSeenAt</c> KULLANILMAZ: senkron yakınsadıktan sonra
/// iki bilgisayar aynı kişi ve kapsam için aynı (WpfCustomerId, ScopeKey) çiftini seçer. Telefon
/// önceliği tek telefonlu grupta bugünkü kart birincilini korur.</para>
/// <para>Kural yalnız YENİ işin sahibini seçer: telefonu sonradan değişen bir üye var olan işin
/// sahibini değiştirmez (iş üye taramasıyla bulunur).</para>
/// </summary>
```

`:107-108`:

```csharp
    /// <summary>Id sunucuya müşteri kimliği (WpfCustomerId) olarak gidebilir mi.</summary>
    public static bool IsOwnerCapable(string id) => Guid.TryParseExact(id, "N", out _);
```

şununla değişir. Gövde aynı; ardına yeni yüklem gelir.

```csharp
    /// <summary>Id sunucuya müşteri kimliği (WpfCustomerId) olarak gidebilir mi (GUID "N"). Sahip seçimine
    /// yetmez — <see cref="IsOwnerCandidate"/>. Id dizesiyle sorulan yerler (miras işler, kapsam adayları,
    /// <c>memberIds</c>) bunu kullanır.</summary>
    public static bool IsOwnerCapable(string id) => Guid.TryParseExact(id, "N", out _);

    /// <summary>Üye yeni işin sahibi olabilir mi: Id GUID ("N") VE satır sunucuya gönderilebilir
    /// (<see cref="ServerCustomerRule.IsServerAcceptable"/>, ortak bakiye §6.1-a, M14).</summary>
    public static bool IsOwnerCandidate(PaymentPersonMember member)
        => IsOwnerCapable(member.Id) && ServerCustomerRule.IsServerAcceptable(member.Platform, member.Username);
```

`:122-124`:

```csharp
    /// <summary>Sahip satırın Id'si; GUID'li üye yoksa null.</summary>
    public static string? PickOwner(IEnumerable<PaymentPersonMember> members)
        => Order(members).FirstOrDefault(m => IsOwnerCapable(m.Id))?.Id;
```

şununla değişir:

```csharp
    /// <summary>Sahip satırın Id'si: sahip kuralı sırasındaki ilk aday (<see cref="IsOwnerCandidate"/>);
    /// aday yoksa null.</summary>
    public static string? PickOwner(IEnumerable<PaymentPersonMember> members)
        => Order(members).FirstOrDefault(IsOwnerCandidate)?.Id;
```

`Order` (`:110-120`) DEĞİŞMEZ.

- [ ] **Adım 13: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentOwnerRuleTests|FullyQualifiedName~PaymentPersonTests|FullyQualifiedName~PaymentJobStoreContractTests|FullyQualifiedName~ServerCustomerRuleTests|FullyQualifiedName~PaymentLeaseTests.Begin_cevrimdisi_GUIDli_ama_gonderilemeyen|FullyQualifiedName~PaymentRequestServiceTests.Gonderilemeyen_tek_uyeli_kiside"`
Expected: PASS. Adım 11'deki on kırmızı yeşile döner; `Kisi_anahtari_ve_sahip_gonderilebilir_uyelerle_sirasindan_bagimsizdir` yeşil kalır. Hem `SqlitePaymentJobStoreContractTests` hem `InMemoryPaymentJobStoreContractTests` yeşil kalır: InMemory fikstürü artık gönderilebilir üyeler taşıyor.

- [ ] **Adım 14: Bakiye kapısının ve kira sarmalının yorumları (`PaymentRequestService.cs:694-695`, `PaymentLeaseCoordinator.cs:161-162`, `:686-689`, `:712-713`; yalnız yorum ve günlük metni)**

```csharp
        // Kişinin GUID'li bir üyesi varsa (sahip) bakiye akışına girilir — tıklanan satır GUID'siz
        // olsa da. GUID'li üye yoksa iş yoktur (sunucu GUID olmayan Id'ye düşüm yazamaz).
```

şununla değişir:

```csharp
        // Kişinin GUID'li ve sunucuya gönderilebilir bir üyesi varsa (sahip) bakiye akışına girilir —
        // tıklanan satır GUID'siz ya da gönderilemez olsa da. Yoksa iş yoktur: sunucu GUID olmayan Id'ye
        // düşüm yazamaz, gönderilemeyen satır (ServerCustomerRule) sunucuya hiç ulaşmaz (ortak bakiye §6.1-a).
```

Kod değişmez (`if (person.OwnerId is not null)` `:696` aynen).

`OrderDeck.App/Services/Sync/PaymentLeaseCoordinator.cs` (yalnız belge ve günlük metni; davranış aynı — "Mod On etkisi"):

`:161-162`:

```csharp
/// <param name="Person">Kira altında okunan kişi: <c>memberIds</c> bütün üyeleri (GUID'siz ham Id'ler dahil), sahip
/// <see cref="PaymentPerson.OwnerId"/> ya da GUID'li üye yoksa sahip kuralının belirlenimli ilk üyesi.</param>
```

şununla değişir:

```csharp
/// <param name="Person">Kira altında okunan kişi: <c>memberIds</c> bütün üyeleri (GUID'siz ham Id'ler dahil), sahip
/// <see cref="PaymentPerson.OwnerId"/> ya da sahip adayı (GUID'li ve gönderilebilir üye, ortak bakiye §6.1-a) yoksa
/// sahip kuralının belirlenimli ilk üyesi.</param>
```

`:686-689`:

```csharp
    /// Kiraya ulaşılamadı (çevrimdışı, sunucu hatası, lisans çözülemedi, zarf hazır değil, uç yok; §5.8, §6.5-2, §8.6).
    /// İstisna: GUID'li üyesi olmayan VE kapsamdaki hiçbir etiketi uzak (akıştan inmiş) bir yayında olmayan kişi bugünkü
    /// yerel yoldan gider (null, <see cref="PaymentLeaseFlow.RecordsLocally"/>) — çevrimdışı canlı yayın sürebilsin (Y3);
    /// uzak etiketi olan hiçbir kişi kirayı atlayamaz. Öbürlerinde mesaj gitmez.
```

şununla değişir:

```csharp
    /// Kiraya ulaşılamadı (çevrimdışı, sunucu hatası, lisans çözülemedi, zarf hazır değil, uç yok; §5.8, §6.5-2, §8.6).
    /// İstisna: sahip adayı (GUID'li ve gönderilebilir üye, <see cref="PaymentOwnerRule.IsOwnerCandidate"/>) olmayan VE
    /// kapsamdaki hiçbir etiketi uzak (akıştan inmiş) bir yayında olmayan kişi bugünkü yerel yoldan gider (null,
    /// <see cref="PaymentLeaseFlow.RecordsLocally"/>) — çevrimdışı canlı yayın sürebilsin (Y3); böyle kişide bakiye işi
    /// açılmaz, sunucu satırı hiç görmez. Uzak etiketi olan hiçbir kişi kirayı atlayamaz. Öbürlerinde mesaj gitmez.
```

`:712-713` (günlük metni; yer tutucular ve argümanlar aynı, yalnız Id):

```csharp
                "Ödeme isteği kirasına ulaşılamadı ({Reason}); GUID'siz ve yalnız kendi etiketleri olan kişi yerel yoldan "
              + "gidiyor, kayıt bağlantı gelince (customer={CustomerId})", reason, person.LeasedId);
```

şununla değişir:

```csharp
                "Ödeme isteği kirasına ulaşılamadı ({Reason}); sahip adayı (GUID'li ve gönderilebilir üye) olmayan ve "
              + "yalnız kendi etiketleri olan kişi yerel yoldan gidiyor, kayıt bağlantı gelince (customer={CustomerId})",
                reason, person.LeasedId);
```

`PaymentLeaseCoordinator.cs` `:2`'de `using OrderDeck.Core.Customers;` var; `<see cref="PaymentOwnerRule.IsOwnerCandidate"/>` çözülür (Adım 12'den sonra yöntem var).

- [ ] **Adım 15: Derleme — 0 uyarı**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`. Core ve App TreatWarningsAsErrors. `WpfCustomerProjectionSyncService`'te kullanılmayan `using` ya da CS8604 olmamalı; `ServerCustomerRule.IsServerAcceptable`'ın `[NotNullWhen(true)]`'si derlemeler arası geçerli.

- [ ] **Adım 16: Regresyon (değiştirmeden)**

Run (R-Kişi + yeni sınıf):
`$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ServerCustomerRuleTests|FullyQualifiedName~PaymentOwnerRuleTests|FullyQualifiedName~PaymentPersonTests|FullyQualifiedName~PaymentPersonGateTests|FullyQualifiedName~PersonScopePlanTests|FullyQualifiedName~PaymentJobStoreContractTests|FullyQualifiedName~PaymentJobRepositoryTests|FullyQualifiedName~CustomerRepositoryTests"`
Expected: PASS. `Kisi_anahtari_uye_sirasindan_bagimsizdir` (`PaymentOwnerRuleTests.cs:82-92`) alan taşımayan üyelerle kurulur ve değiştirilmez; eski test artık yalnız `MemberKey`'i kanıtlar (iki `OwnerId` null). Sahip eşitliği Adım 10'daki `Kisi_anahtari_ve_sahip_gonderilebilir_uyelerle_sirasindan_bagimsizdir`'da.

Run (R-Senkron):
`$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~WpfCustomerProjectionSyncServiceTests|FullyQualifiedName~CustomerChangesPullServiceTests|FullyQualifiedName~CustomerSyncDiTests"`
Expected: PASS. Senkronun eleme davranışı aynı (`SyncOnce_customer_the_server_would_reject_is_skipped`).

Run (R-Ödeme):
`$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CorrectedPaymentRequestsTests|FullyQualifiedName~PaymentPersonViewModelTests"`
Expected: PASS. Test müşterileri gönderilebilir (Platform ve Username dolu); `_customers` null kurulumları `PaymentPerson.Single` üzerinden alanları taşır. Sahip ve bakiye akışı aynı. Sahip adayı olmayan kişinin işsiz yolunu (`:696` dalı atlanır) yalnız Adım 10'daki `Gonderilemeyen_tek_uyeli_kiside_…` görür.

Run (R-ModOn, yavaş ~dakikalar):
`$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentLeaseTests|FullyQualifiedName~PaymentLeaseServerRulesTests|FullyQualifiedName~RemotePaymentRecordsTests|FullyQualifiedName~TwoPcSalesConvergenceTests"`
Expected: PASS. Kiranın sahibi (`PaymentLeaseCoordinator.cs:584`, `OwnerId ?? MemberIds[0]`) ve `Unreachable`'ın yerel kolu (`:698`) var olan test müşterilerinde aynı değeri görür (hepsi gönderilebilir); yeni kolu yalnız Adım 10'daki `Begin_cevrimdisi_GUIDli_ama_gonderilemeyen_…` görür. `RemotePaymentRecordsTests.cs:155`, `:237`'deki alan taşımayan elle kurulmuş kişiler önbellek anahtarı içindir (üye kümesi); `OwnerId`'yi okumazlar.

Run (R-Arayüz; arama kartının ödeme akışı ve kart sahibi de `PaymentPerson.OwnerId`'yi kullanır, ör. `CustomerSearchViewModelTests.cs:498`):
`$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerSearchViewModelTests|FullyQualifiedName~CustomerDetailViewModelTests|FullyQualifiedName~CustomerDetailRoutingTests|FullyQualifiedName~MainShellViewCompositionTests"`
Expected: PASS, iddialar değişmeden (test müşterilerinin Platform ve Username'i dolu; sahip aynı satıra düşer).

Kırmızı varsa: düzeltme bu görevin dosyalarında yapılır. Var olan bir testin iddiası DEĞİŞTİRİLMEZ; bilerek değişenler yalnız iskeletin saydıklarıdır: iki fikstür (`PaymentJobStoreContractTests` InMemory `:947`, `:959`; `PaymentOwnerRuleTests` vektör kurucusu), `PaymentOwnerRuleTests`'in okuma/kayıt fikstürü (sınıf belgesi, `VectorMember`, JSON okuması) ve `PaymentPersonTests.Insert`'in geriye uyumlu `username` parametresi.

- [ ] **Adım 17: Commit**

```bash
git add OrderDeck.Core/Customers/ServerCustomerRule.cs \
  OrderDeck.Core/Customers/PaymentPerson.cs \
  OrderDeck.Core/Storage/Repositories/PaymentPersonSql.cs \
  OrderDeck.App/Services/Sync/WpfCustomerProjectionSyncService.cs \
  OrderDeck.App/Services/PaymentRequestService.cs \
  OrderDeck.App/Services/Sync/PaymentLeaseCoordinator.cs \
  OrderDeck.Tests/Customers/ServerCustomerRuleTests.cs \
  OrderDeck.Tests/Customers/PaymentOwnerRuleTests.cs \
  OrderDeck.Tests/Customers/PaymentOwnerRuleVectors.json \
  OrderDeck.Tests/Storage/PaymentPersonTests.cs \
  OrderDeck.Tests/Storage/PaymentJobStoreContractTests.cs \
  OrderDeck.Tests/Sync/PaymentLeaseTests.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.Person.cs
git commit -F - <<'MSG'
feat(balance): sahip kuralı gönderilemeyen satırı seçmesin (ServerCustomerRule Core'a)

Sunucu boş ya da aşırı uzun Platform/Username taşıyan satırı kabul etmez;
müşteri senkronu böyle satırı zaten göndermez. Bu satır ödeme işinin sahibi
olursa düşüm çapası sunucuda hiç yazılamaz: satır kipinde öbür üyenin
bakiyesi kullanılmaz, havuz kipinde kişi kalıcı customer-not-synced alır
(ortak bakiye §6.1-a, M14).

Kabul tanımı ServerCustomerRule olarak Core'a taşınır; senkron servisi ve
sahip kuralı aynı tanımı kullanır. PaymentPersonMember Platform ve Username
taşır (senkronlanan, yakınsayan alanlar; SQL okuması ve tek satırlık kişi
doldurur). Yeni IsOwnerCandidate = GUID ve gönderilebilir; yalnız PickOwner
kullanır. IsOwnerCapable Id dizesi soran yerler (miras işler, kapsam
adayları) için GUID olarak kalır, Order değişmez.

Kural yalnız yeni işin sahibini seçer: var olan iş üye taramasıyla bulunur,
sahibi değişmez (§8.4-3); gönderilemeyen satırdaki eski iş bu yüzden
düzelmez. Mod On'da sahip adayı olmayan kişi kiraya ulaşılamazsa GUID'siz
kişinin yerel yolundan gider (kira sarmalının yorumları güncellenir). Sahip
adayı olmayan kişide iş dalı atlanır: o satırdaki eski iş okunmaz, tekrar
gönderme sorusu ve teslim kaydı yok (GUID'siz kişinin yolu; test kilitler).
InMemory sözleşme fikstürü ve vektör kurucusu gönderilebilir alanları
taşıyacak biçimde güncellenir; iddialar aynı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 2: İstemci sözleşmesi: preview/apply `memberIds`, scope `PersonPool`, panel `Members` (spec §6.1-a, §6.1-d, §6.1-e; §4.1, §4.2, §4.3, §4.6; S2 saha notu 5)

**Files:**
- Değiştir: `OrderDeck.Licensing/Api/Models/CustomerBalanceDtos.cs` — `CustomerBalanceApplyRequest` belgesi ve kaydı (`:9-23`), `CustomerBalanceScope` belgesi ve kaydı (`:30-36`), yeni `CustomerBalanceMemberDto` (`:63`'ten sonra, `CustomerBalanceTransactionDto`'nun ardına), `CustomerBalanceDetailsResponse` (`:65-67`)
- Değiştir: `OrderDeck.Licensing/Api/LicenseApiClient.cs` — `GetBalancePreviewAsync` (`:787-796`) iki aşırı yüklemeye bölünür
- Test (genişlet): `OrderDeck.Tests/Licensing/LicenseApiClientPaymentRequestTests.cs` — sınıf belgesi (`:13-19`) bir paragraf; sınıfın sonuna (`:868`'deki `Apply_200_ve_400_bugunku_gibi`'nin kapanışından sonra, `:869`'daki sınıf kapanışından önce) yeni bölüm `// ── customer-balance: W-havuz (ortak bakiye §6.1) ──`
- DEĞİŞMEZ: `CustomerBalancePreview` (fark 22), `MapProblem`/`ThrowMappedAsync`/`ApplyBalanceAsync`'in gövdesi, `GetBalanceScopeAsync`, `GetCustomerBalanceAsync` (yalnız dönüş türü genişler; yedek kurulumu `:885-887` iki argümanla derlenir, `Members` null), `PaymentRequestService` (yeni aşırı yüklemeyi Görev 3 çağırır)

Bu görev yalnız istemci kütüphanesinin tel sözleşmesini S2 sunucusuna (master `7960eba3`) hizalar; ödeme akışı, kart ve arama ekranı bu görevde hiçbir yeni alanı okumaz/yazmaz. Kapsadığı maddeler: R05 (preview tekrarlı `memberIds`), R06 (apply gövdesi `memberIds`), R10 (eski sunucu uyumu: bütün yeni alanların varsayılanı bugünkü davranış), R23'ün istemci kısmı (`CustomerBalanceScope.PersonPool`), R28'in istemci kısmı (`CustomerBalanceMemberDto`, `CustomerBalanceDetailsResponse.Members`), C07/C08/C09/C14 eşlemesinin kilitlenmesi (kod değişmez).

Sunucu sözleşmesi koddan doğrulandı (salt okuma):
- Preview: `[HttpGet("preview")] Preview(Guid licenseId, [FromQuery] Guid wpfCustomerId, [FromQuery(Name = "memberIds")] Guid[]? memberIds, …)` — `LicensesCustomerBalanceApplyController.cs:143-147`; yanıt `PreviewResponse(Guid WpfCustomerId, decimal Balance, DateTimeOffset UpdatedAt, bool PersonPool = false)` `:118-122`.
- Apply: `ApplyRequest(Guid WpfCustomerId, decimal Amount, decimal ProductTotal, Guid? IdempotencyKey = null, string? SaleScope = null, IReadOnlyList<Guid>? MemberIds = null)` `:400-406` (belge `:396-399`: "Alanın yokluğu ile boş liste aynı sayılır").
- Scope: `ScopeResponse(Guid TransactionId, decimal AppliedAmount, decimal ProductTotal, DateTimeOffset CreatedAt, bool PersonPool = false)` `:225-230`.
- Panel: `MemberDto(Guid WpfCustomerId, decimal Balance, bool Purged)` ve `BalanceDetailsResponse(BalanceDto Balance, TransactionDto[] Transactions, MemberDto[] Members)` — `PanelCustomerBalanceController.cs:68-73`.
- Problem başlıkları: `member-ids-required` 400 (`:1128-1135`), `customer-not-synced` 409, gövdeye uzantı yok (`:1142-1150`), `client-update-required` 409 (iki kapı aynı).
- JSON: sunucu MVC varsayılanı (`JsonSerializerDefaults.Web`, camelCase); istemci `LicenseApiClient.JsonOpts` (`:11-15`: camelCase, büyük/küçük harf duyarsız, `DefaultIgnoreCondition` yok → null alan yazılır).

**Not (spec ↔ kod, bu görev):**
1. **Satır numaraları.** Spec `LicenseApiClient.cs:392-399` (preview) → bugün `:787-796`; `:441-446` (apply DTO) → `CustomerBalanceDtos.cs:18-23`; `:463-471` (panel GET) → `LicenseApiClient.cs:880-888` (iskelet fark 1).
2. **Preview iki aşırı yükleme.** Spec §6.1-a "`GetBalancePreviewAsync` üyeleri taşır" der; imza yerinde değiştirilseydi `PaymentRequestService.cs:1400` bu görevde de değişmek zorunda kalırdı. Üç argümanlı biçim korunur ve boş listeyle yenisini çağırır: sorgu bugünkü çağrıyla bayttan bayta aynı kalır (R10), ödeme akışı bu görevde değişmez. Görev 3 çağrıyı dört argümanlı biçime taşır; ondan sonra üç argümanlı biçimin üretim çağıranı kalmaz ve havuz kipinde 400 `member-ids-required` alır (para hareketi yok, BalanceUncertain yolu). Bu yüzden silinmez, `<summary>`'si yalnız satır kipi/eski çağrı biçimi testi için korunduğunu söyler.
3. **`memberIds` yalnız geçerli Guid.** S2 sunucusu Guid'e bağlanamayan `memberIds` değerini **satır kipinde de** 400 `ValidationProblem` ile reddeder (S2 fark 18f; AC:130-132). Tür (`IReadOnlyCollection<Guid>` / `IReadOnlyList<Guid>?`) bunu derleme anında garanti eder; `D` biçimi `wpfCustomerId`'nin bugünkü biçimiyle aynı. Virgüllü liste sunucuda bağlanmaz → her üye ayrı `&memberIds=`.
4. **Apply'da `"memberIds": null` yazılır.** `JsonOpts` null'ı yazar (C14); sunucu null'ı yok ile aynı sayar (AC:396-399), S1 öncesi sunucu bilinmeyen üyeyi atlar. `[JsonIgnore(WhenWritingNull)]` eklenmez: talimat ve sözleşme açık null'ı kabul ediyor; S2 testi `Havuz_kipinde_memberIds_siz_oynatma_RemainingBalance_S_toplami` açık null'ı ayrıca sınıyor.
5. **`customer-not-synced` gövdesinde satır Id'si yok** (AC:1140 "Gövdeye uzantı eklenmez"; iskelet fark 5). İstemci yalnız `ValidationException.Code`'a bakar; bu görev bunu yalnız kilitler.
6. **`CustomerBalancePreview` `personPool`'u okumaz** (iskelet fark 22). Yanıttaki bilinmeyen `personPool` alanı çözümü bozmaz; test bunu gerçek S2 gövdesiyle gösterir.
7. **`Members` null mı boş mu.** Sunucu S1'den beri `members[]`'i her zaman gönderir (`MemberDto[] Members`, null değil). İstemcide alan `CustomerBalanceMemberDto[]? Members = null`: alanın yokluğu (S1 öncesi sunucu) `null`, Görev 7 bunu "dağılım alınamadı" sayar; boş dizi ile ayrışır. `GetCustomerBalanceAsync`'in "gövde null" yedeği (`:885-887`) değişmez → `Members` null.
8. **R-ModOn'daki `PaymentLeaseServerRulesTests` gerçek sunucuya değil, sahte sunucuya gider** (talimatın "gerçek sunucu" ifadesi kayık): `FakeSalesServer.PaymentRules.cs:753-762` apply gövdesini `JsonDocument` ile alan alan okur (`wpfCustomerId`, `amount`, `productTotal`, `idempotencyKey`, `saleScope`); fazladan `"memberIds": null` yok sayılır. Bu testler `CustomerBalanceApplyRequest`'i beş konumsal argümanla kurar (`PaymentLeaseServerRulesTests.cs:615-647`); yeni son parametre varsayılanlı olduğundan değişmeden derlenir.
9. **Licensing'in `internal`'ları `OrderDeck.Tests`'e görünmez** (IVT yalnız `OrderDeck.Licensing.Tests`, `OrderDeck.Licensing.csproj:12-14`; iskelet fark 21): testler yalnız public yüzeyi kullanır. `OrderDeck.Licensing.csproj` `ImplicitUsings enable` → `System.Linq` örtük, `using` eklenmez. `GenerateDocumentationFile` yok → belge yorumları uyarı üretmez; yine de `cref`'ler gerçek türlere bağlanır.

- [ ] **Adım 1: Başarısız testleri yaz**

`OrderDeck.Tests/Licensing/LicenseApiClientPaymentRequestTests.cs` — sınıf belgesi. Bugünkü (`:18-20`):

```csharp
/// üyeleriyle, 404 uç yok, ağ hatası ve zaman aşımı çevrimdışı, 5xx ve bozuk gövde başarısız; yalnız iptal fırlar.
/// </summary>
public sealed class LicenseApiClientPaymentRequestTests
```

yerine:

```csharp
/// üyeleriyle, 404 uç yok, ağ hatası ve zaman aşımı çevrimdışı, 5xx ve bozuk gövde başarısız; yalnız iptal fırlar.
/// <para>Ortak bakiye W-havuz (spec 2026-10-11 §6.1-a/d/e): önizlemenin tekrarlı <c>memberIds</c> sorgu parametresi,
/// apply gövdesinin <c>memberIds</c> alanı, kapsam yanıtının <c>personPool</c>'u ve panel GET'in <c>members</c>'ı sunucunun
/// S2 kayıtlarıyla (<c>LicensesCustomerBalanceApplyController.PreviewResponse/ApplyRequest/ScopeResponse</c>,
/// <c>PanelCustomerBalanceController.MemberDto</c>) birebir. customer-balance uçları "fırlatmaz" kuralına girmez: 4xx
/// <see cref="ValidationException"/> olur, <c>Code</c> = problem başlığı.</para>
/// </summary>
public sealed class LicenseApiClientPaymentRequestTests
```

Aynı dosyanın sonu. Bugünkü (`:865-869`):

```csharp
        var down = Client(_ => FakeHttpMessageHandler.Problem(503, "fake-fault"));
        await FluentActions.Awaiting(() => down.ApplyBalanceAsync(License, Apply()))
            .Should().ThrowAsync<LicenseApiUnknownException>();
    }
}
```

yerine:

```csharp
        var down = Client(_ => FakeHttpMessageHandler.Problem(503, "fake-fault"));
        await FluentActions.Awaiting(() => down.ApplyBalanceAsync(License, Apply()))
            .Should().ThrowAsync<LicenseApiUnknownException>();
    }

    // ── customer-balance: W-havuz (ortak bakiye §6.1) ─────────────────────

    /// <summary>S2 önizleme yanıtı (<c>PreviewResponse</c>): havuz kipinde <c>personPool: true</c>. İstemci DTO'su alanı
    /// okumaz (plan fark 22); bilinmeyen alan çözümü bozmamalı.</summary>
    private static string PreviewJson(Guid customer) => $$"""
        {"wpfCustomerId":"{{customer}}","balance":40.00,"updatedAt":"2026-11-01T18:42:10+00:00","personPool":true}
        """;

    private static CustomerBalanceApplyRequest PoolApply(Guid customer, IReadOnlyList<Guid> members) =>
        new(customer, 250m, 250m, Guid.NewGuid(), "cumulative", MemberIds: members);

    [Fact] // §6.1-a, R05: sunucu [FromQuery(Name = "memberIds")] Guid[] — virgüllü liste bağlanmaz (S2 fark 18f)
    public async Task Onizleme_memberIds_tekrarli_sorgu_parametresi_olarak_gider()
    {
        var a = Guid.NewGuid();
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var client = Client(req =>
        {
            captured = req;
            return FakeHttpMessageHandler.Json(200, PreviewJson(a));
        });

        var preview = await client.GetBalancePreviewAsync(License, a, [m1, m2]);

        preview.WpfCustomerId.Should().Be(a);
        preview.Balance.Should().Be(40m, "yanıttaki personPool alanı önizleme DTO'sunun çözümünü bozmaz");
        captured!.Method.Should().Be(HttpMethod.Get);
        captured.Content.Should().BeNull("GET gövde taşımaz");
        captured.RequestUri!.AbsolutePath.Should().Be($"/api/v1/licenses/{License}/customer-balance/preview");
        captured.RequestUri.Query.Should().Be($"?wpfCustomerId={a:D}&memberIds={m1:D}&memberIds={m2:D}",
            "her üye ayrı memberIds= ile, verilen sırayla; virgüllü liste sunucuda 400 alır");
        var query = HttpUtility.ParseQueryString(captured.RequestUri.Query);
        query["wpfCustomerId"].Should().Be(a.ToString("D"));
        query.GetValues("memberIds").Should().Equal(m1.ToString("D"), m2.ToString("D"));
    }

    [Theory] // R10: boş listede parametre yok — bugünkü çağrı biçimi (satır kipi, eski sunucu) bayttan bayta aynı
    [InlineData(true)]
    [InlineData(false)]
    public async Task Onizleme_bos_uye_listesinde_memberIds_parametresi_yok(bool eskiAsiriYukleme)
    {
        var a = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var client = Client(req =>
        {
            captured = req;
            return FakeHttpMessageHandler.Json(200, PreviewJson(a));
        });

        var preview = eskiAsiriYukleme
            ? await client.GetBalancePreviewAsync(License, a)
            : await client.GetBalancePreviewAsync(License, a, Array.Empty<Guid>());

        preview.Balance.Should().Be(40m);
        captured!.RequestUri!.AbsolutePath.Should().Be($"/api/v1/licenses/{License}/customer-balance/preview");
        captured.RequestUri.Query.Should().Be($"?wpfCustomerId={a:D}", "üye yoksa memberIds hiç yazılmaz");
    }

    [Fact] // §6.1-a, R06: sunucu ApplyRequest.MemberIds (IReadOnlyList<Guid>?) — gövde alanı "memberIds", JSON dizisi
    public async Task Apply_govdesi_memberIds_dizisini_tasir()
    {
        var a = Guid.NewGuid();
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        var key = Guid.NewGuid();
        string? body = null;
        var client = Client(req =>
        {
            body = Read(req);
            return FakeHttpMessageHandler.Json(200, $$"""
                {"transactionId":"{{key}}","appliedAmount":250.00,"remainingBalance":40.00}
                """);
        });

        var response = await client.ApplyBalanceAsync(License,
            new CustomerBalanceApplyRequest(a, 250m, 250m, key, "cumulative", MemberIds: [m1, m2]));

        response.Should().Be(new CustomerBalanceApplyResponse(key, 250m, 40m), "apply yanıtı S2'de değişmedi");
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Names(root).Should().BeEquivalentTo(
            ["wpfCustomerId", "amount", "productTotal", "idempotencyKey", "saleScope", "memberIds"],
            "sunucu ApplyRequest'i bu adlarla okur");
        root.GetProperty("wpfCustomerId").GetGuid().Should().Be(a);
        root.GetProperty("memberIds").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("memberIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(m1, m2);
    }

    [Fact] // C14: JsonOpts null'ı yazar; sunucu "memberIds": null'ı yok ile aynı sayar, S1 öncesi sunucu bilinmeyen alanı atlar
    public async Task Apply_memberIds_verilmezse_alan_null_gider()
    {
        string? body = null;
        var client = Client(req =>
        {
            body = Read(req);
            return FakeHttpMessageHandler.Problem(409, "no-balance");
        });

        await FluentActions.Awaiting(() => client.ApplyBalanceAsync(License, Apply()))
            .Should().ThrowAsync<ValidationException>();

        using var json = JsonDocument.Parse(body!);
        json.RootElement.TryGetProperty("memberIds", out var memberIds).Should()
            .BeTrue("alan her gövdede yazılır; verilmezse null");
        memberIds.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory] // §6.1-d, R23: scope yanıtının personPool'u (S2 ScopeResponse.PersonPool); eski sunucu alanı göndermez → false
    [InlineData(",\"personPool\":true", true)]
    [InlineData(",\"personPool\":false", false)]
    [InlineData("", false)]
    public async Task Kapsam_yaniti_personPool_okunur_alan_yoksa_false(string ek, bool beklenen)
    {
        var a = Guid.NewGuid();
        var tx = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var client = Client(req =>
        {
            captured = req;
            return FakeHttpMessageHandler.Json(200, $$"""
                {"transactionId":"{{tx}}","appliedAmount":250.00,"productTotal":250.00,"createdAt":"2026-11-01T18:42:10+00:00"{{ek}}
                """ + "}");
        });

        var scope = await client.GetBalanceScopeAsync(License, a, "cumulative");

        scope.Should().Be(new CustomerBalanceScope(
            tx, 250m, 250m, DateTimeOffset.Parse("2026-11-01T18:42:10+00:00"), beklenen));
        captured!.RequestUri!.Query.Should().Be($"?wpfCustomerId={a:D}&saleScope=cumulative",
            "§6.1-h: scope memberIds almaz");

        var bos = Client(_ => FakeHttpMessageHandler.Empty(204));
        (await bos.GetBalanceScopeAsync(License, a, "cumulative")).Should().BeNull("204 = iz yok (değişmez)");
    }

    [Fact] // §6.1-e, R28: panel GET members[] (MemberDto(WpfCustomerId, Balance, Purged)); istemci sunucunun sırasını aynen korur (sıralamaz)
    public async Task Panel_bakiye_yaniti_members_dizisini_okur()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var silinmis = Guid.NewGuid();
        var lisans = Guid.NewGuid();
        var islem = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var client = Client(req =>
        {
            captured = req;
            return FakeHttpMessageHandler.Json(200, $$"""
                {"balance":{"wpfCustomerId":"{{a}}","licenseId":"{{lisans}}","balance":-20.00,"updatedAt":"2026-11-01T18:42:10+00:00"},
                 "transactions":[{"id":"{{islem}}","amount":-250.00,"kind":"purchase-deduction","originalAmount":250.00,
                                  "shippingDeducted":null,"reason":null,"reversesTransactionId":null,"createdAt":"2026-11-01T18:42:10+00:00"}],
                 "members":[{"wpfCustomerId":"{{a}}","balance":30.00,"purged":false},
                            {"wpfCustomerId":"{{b}}","balance":-60.00,"purged":false},
                            {"wpfCustomerId":"{{silinmis}}","balance":10.00,"purged":true}]}
                """);
        });

        var details = await client.GetCustomerBalanceAsync(a, take: 1);

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.PathAndQuery.Should().Be($"/api/panel/customers/{a}/balance?take=1");
        details.Balance.Balance.Should().Be(-20m, "havuz kipinde Σ_S, eksi olabilir (§4.6)");
        details.Transactions.Should().ContainSingle().Which.Id.Should().Be(islem);
        details.Members.Should().NotBeNull("S1'den beri sunucu members[]'i gönderir");
        details.Members!.Select(m => m.WpfCustomerId).Should().Equal(a, b, silinmis);
        details.Members!.Select(m => m.Balance).Should().Equal(30m, -60m, 10m);
        details.Members!.Select(m => m.Purged).Should().Equal(false, false, true);
    }

    [Fact] // R10: members alanı olmayan gövde (S1 öncesi sunucu) → Members null; Balance ve Transactions bugünkü gibi
    public async Task Panel_bakiye_yaniti_members_yoksa_null()
    {
        var a = Guid.NewGuid();
        var lisans = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"balance":{"wpfCustomerId":"{{a}}","licenseId":"{{lisans}}","balance":15.00,"updatedAt":"2026-11-01T18:42:10+00:00"},
             "transactions":[]}
            """));

        var details = await client.GetCustomerBalanceAsync(a);

        details.Members.Should().BeNull("alanın yokluğu boş dizi değil; Ayır onayı bunu 'dağılım alınamadı' sayar (Görev 7)");
        details.Balance.Should().Be(new CustomerBalanceDto(a, lisans, 15m, DateTimeOffset.Parse("2026-11-01T18:42:10+00:00")));
        details.Transactions.Should().BeEmpty();
    }

    [Theory] // C07–C09, C14: havuz retleri ValidationException.Code = başlık (MapProblem değişmez; Görev 3/4 bu koda bakar)
    [InlineData(409, "customer-not-synced")]
    [InlineData(409, "client-update-required")]
    [InlineData(400, "member-ids-required")]
    public async Task Onizleme_ve_apply_havuz_retleri_ValidationException_koduyla(int status, string code)
    {
        var a = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Problem(status, code, "x"));

        (await FluentActions.Awaiting(() => client.GetBalancePreviewAsync(License, a, [a, Guid.NewGuid()]))
                .Should().ThrowAsync<ValidationException>())
            .Which.Code.Should().Be(code, "önizleme: GetExpectingJsonAsync → ThrowMappedAsync");

        (await FluentActions.Awaiting(() => client.ApplyBalanceAsync(License, PoolApply(a, [a, Guid.NewGuid()])))
                .Should().ThrowAsync<ValidationException>())
            .Which.Code.Should().Be(code,
                status == 409 ? "apply 409 gövdesini kendisi okur, scope-has-deduction dışı MapProblem" : "apply: ThrowMappedAsync");
    }

    [Fact] // K7'nin apply kolu: havuzda kural 1 kesin kolu ya da Σ ≤ 0 → 409 no-balance, gövdede detail yok (AC:623-629)
    public async Task Apply_409_no_balance_ValidationException()
    {
        var a = Guid.NewGuid();
        var client = Client(_ => FakeHttpMessageHandler.Json(409, """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"no-balance","status":409}
            """));

        var thrown = await FluentActions.Awaiting(() => client.ApplyBalanceAsync(License, PoolApply(a, [a])))
            .Should().ThrowAsync<ValidationException>();

        thrown.Which.Code.Should().Be("no-balance", "ödeme akışı bu kodla bugünkü no-balance yolundan kapanır");
    }
}
```

Yardımcı adları (`PreviewJson`, `PoolApply`) sınıfta yok (bugünkü özel yardımcılar: `Create`, `Client`, `Read`, `Names`, `Member`, `NewDigest`, `Begin`, `Finish`, `BackfillItem`, `Backfill`, `Put`, `CallAsync`, `Apply`).

- [ ] **Adım 2: Testleri koştur — FAIL (derleme)**

PowerShell:

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicenseApiClientPaymentRequestTests"
```

Beklenen: `OrderDeck.Tests` derlenmez, test koşmaz. Hatalar (sözleşme henüz yok):
- `CS1739` — `CustomerBalanceApplyRequest`'in `MemberIds` adlı parametresi yok (`PoolApply`, `Apply_govdesi_memberIds_dizisini_tasir`);
- `CS9174`/`CS1503` — `GetBalancePreviewAsync`'in üçüncü parametresi `CancellationToken`; koleksiyon ifadesi `[m1, m2]` ve `Array.Empty<Guid>()` ona dönüşmez;
- `CS1729` — `CustomerBalanceScope` beş argümanlı kurucu almaz;
- `CS1061` — `CustomerBalanceDetailsResponse`'ta `Members` yok.

- [ ] **Adım 3: DTO'ları genişlet**

`OrderDeck.Licensing/Api/Models/CustomerBalanceDtos.cs`. Bugünkü (`:9-23`):

```csharp
/// <summary>Bakiye düşüm isteği. <paramref name="IdempotencyKey"/> çağrı başına
/// bir kez üretilir: dayanıklılık katmanı bu POST'u yeniden denediğinde gövde
/// (dolayısıyla anahtar) aynı kalır ve sunucu bakiyeyi ikinci kez düşürmez.
/// Anahtar aynı zamanda oluşan ledger satırının kimliğidir.
///
/// <para>R4-03: <paramref name="SaleScope"/> satışın <b>kalıcı</b> kimliği —
/// işin <c>ScopeKey</c>'i. Idempotency anahtarı yerel diskte yaşıyor ve yedek
/// geri yüklenince kayboluyor; kapsam sunucuda kaldığından düşüm sonradan
/// yine tanınabiliyor.</para></summary>
public sealed record CustomerBalanceApplyRequest(
    Guid WpfCustomerId,
    decimal Amount,
    decimal ProductTotal,
    Guid? IdempotencyKey = null,
    string? SaleScope = null);
```

yerine:

```csharp
/// <summary>Bakiye düşüm isteği. <paramref name="IdempotencyKey"/> çağrı başına
/// bir kez üretilir: dayanıklılık katmanı bu POST'u yeniden denediğinde gövde
/// (dolayısıyla anahtar) aynı kalır ve sunucu bakiyeyi ikinci kez düşürmez.
/// Anahtar aynı zamanda oluşan ledger satırının kimliğidir.
///
/// <para>R4-03: <paramref name="SaleScope"/> satışın <b>kalıcı</b> kimliği —
/// işin <c>ScopeKey</c>'i. Idempotency anahtarı yerel diskte yaşıyor ve yedek
/// geri yüklenince kayboluyor; kapsam sunucuda kaldığından düşüm sonradan
/// yine tanınabiliyor.</para>
///
/// <para>Ortak bakiye: <paramref name="MemberIds"/> kişinin GUID'li üyeleri
/// (spec 2026-10-11 §6.1-a). null ile boş aynı; havuz kipinde zorunlu
/// (400 <c>member-ids-required</c>), satır kipinde okunmaz. Gövde alanı
/// <c>memberIds</c>; verilmezse <c>"memberIds": null</c> yazılır — sunucu onu
/// yok ile aynı sayar, alanı tanımayan eski sunucu atlar.</para></summary>
public sealed record CustomerBalanceApplyRequest(
    Guid WpfCustomerId,
    decimal Amount,
    decimal ProductTotal,
    Guid? IdempotencyKey = null,
    string? SaleScope = null,
    IReadOnlyList<Guid>? MemberIds = null);
```

Bugünkü (`:30-36`):

```csharp
/// <summary>R4-03: bir kapsamda sunucuda duran, geri alınmamış düşüm. Yerel
/// anahtar kaybolmuş olsa bile satış bununla tanınır.</summary>
public sealed record CustomerBalanceScope(
    Guid TransactionId,
    decimal AppliedAmount,
    decimal ProductTotal,
    DateTimeOffset CreatedAt);
```

yerine:

```csharp
/// <summary>R4-03: bir kapsamda sunucuda duran, geri alınmamış düşüm. Yerel
/// anahtar kaybolmuş olsa bile satış bununla tanınır.</summary>
/// <param name="PersonPool">Lisans havuz kipinde mi (spec 2026-10-11 §4.3);
/// birleştirme notu (§6.1-d) kullanır. Eski sunucu alanı göndermez → false.</param>
public sealed record CustomerBalanceScope(
    Guid TransactionId,
    decimal AppliedAmount,
    decimal ProductTotal,
    DateTimeOffset CreatedAt,
    bool PersonPool = false);
```

Bugünkü (`:55-67`):

```csharp
public sealed record CustomerBalanceTransactionDto(
    Guid Id,
    decimal Amount,
    string Kind,
    decimal? OriginalAmount,
    decimal? ShippingDeducted,
    string? Reason,
    Guid? ReversesTransactionId,
    DateTimeOffset CreatedAt);

public sealed record CustomerBalanceDetailsResponse(
    CustomerBalanceDto Balance,
    CustomerBalanceTransactionDto[] Transactions);
```

yerine:

```csharp
public sealed record CustomerBalanceTransactionDto(
    Guid Id,
    decimal Amount,
    string Kind,
    decimal? OriginalAmount,
    decimal? ShippingDeducted,
    string? Reason,
    Guid? ReversesTransactionId,
    DateTimeOffset CreatedAt);

/// <summary>Panel GET'in kişi üyesi (spec 2026-10-11 §4.6): WPF'in Ayır onayı
/// kullanır. İki kipte de S(a) — silinmişler dahil, <c>WpfCustomerId</c>'nin "N"
/// biçiminin ordinal sırasıyla; bakiye satırı olmayan üyede 0.</summary>
public sealed record CustomerBalanceMemberDto(
    Guid WpfCustomerId,
    decimal Balance,
    bool Purged);

/// <param name="Members">Kişinin üyeleri ve satır bakiyeleri (§4.6). Sunucu
/// S1'den beri gönderir; alanın yokluğu (eski sunucu) <c>null</c> — boş diziyle
/// karışmaz.</param>
public sealed record CustomerBalanceDetailsResponse(
    CustomerBalanceDto Balance,
    CustomerBalanceTransactionDto[] Transactions,
    CustomerBalanceMemberDto[]? Members = null);
```

- [ ] **Adım 4: Önizlemeyi iki aşırı yüklemeye böl**

`OrderDeck.Licensing/Api/LicenseApiClient.cs`. Bugünkü (`:787-796`):

```csharp
    /// <summary>WPF "Ödeme iste" anlığında müşterinin bakiyesini sorgular.
    /// Hiç bakiye yoksa 0 dönen response, hata değil.</summary>
    public async Task<CustomerBalancePreview> GetBalancePreviewAsync(
        Guid licenseId, Guid wpfCustomerId, CancellationToken ct = default)
    {
        var qs = $"?wpfCustomerId={wpfCustomerId:D}";
        return await GetExpectingJsonAsync<CustomerBalancePreview>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview{qs}", ct)
            ?? new CustomerBalancePreview(wpfCustomerId, 0m, DateTimeOffset.UtcNow);
    }
```

yerine:

```csharp
    /// <summary>WPF "Ödeme iste" anlığında müşterinin bakiyesini sorgular.
    /// Hiç bakiye yoksa 0 dönen response, hata değil. Üye listesi göndermez:
    /// boş listeyle aşağıdaki aşırı yüklemeyi çağırır, sorgu bugünküyle aynı.
    /// Havuz kipinde 400 <c>member-ids-required</c> alır; ödeme akışı
    /// (PaymentRequestService) dört argümanlı biçimi kullanır. Yalnız satır kipi/eski çağrı biçimi
    /// testi için korunur.</summary>
    public Task<CustomerBalancePreview> GetBalancePreviewAsync(
        Guid licenseId, Guid wpfCustomerId, CancellationToken ct = default)
        => GetBalancePreviewAsync(licenseId, wpfCustomerId, Array.Empty<Guid>(), ct);

    /// <summary>Ortak bakiye (spec 2026-10-11 §6.1-a): önizleme, kişinin GUID'li
    /// üyeleriyle. <paramref name="memberIds"/> tekrarlı sorgu parametresi olarak
    /// gider (<c>&amp;memberIds={id}</c>, verilen sırayla; virgüllü liste sunucuda
    /// bağlanmaz); boş listede parametre yazılmaz. Sunucu havuz kipinde okur
    /// (yoksa 400 <c>member-ids-required</c>), satır kipinde yok sayar; Guid'e
    /// bağlanamayan değer iki kipte de 400 — yalnız geçerli Guid gider (S2 fark 18f).
    /// Havuz retleri (409 <c>customer-not-synced</c>, 409 <c>client-update-required</c>,
    /// 400 <c>member-ids-required</c>) <see cref="ValidationException"/> olur,
    /// <c>Code</c> = problem başlığı.</summary>
    public async Task<CustomerBalancePreview> GetBalancePreviewAsync(
        Guid licenseId, Guid wpfCustomerId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct = default)
    {
        var qs = $"?wpfCustomerId={wpfCustomerId:D}" + string.Concat(memberIds.Select(id => $"&memberIds={id:D}"));
        return await GetExpectingJsonAsync<CustomerBalancePreview>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview{qs}", ct)
            ?? new CustomerBalancePreview(wpfCustomerId, 0m, DateTimeOffset.UtcNow);
    }
```

Aşırı yükleme çözümü: `(licenseId, wpfCustomerId)` ve `(licenseId, wpfCustomerId, ct)` (bugün `PaymentRequestService.cs:1400`) yalnız ilkine uyar; `IReadOnlyCollection<Guid>` alan (dizi, liste, koleksiyon ifadesi) yalnız ikinciye. `default` üçüncü argümanla çağıran yok (Grep: `GetBalancePreviewAsync(` yalnız `PaymentRequestService.cs:1400`).

- [ ] **Adım 5: Testleri koştur — PASS**

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicenseApiClientPaymentRequestTests"
```

Beklenen: `Failed: 0`; yeni 14 vaka (`Onizleme_memberIds_…` 1, `Onizleme_bos_uye_…` 2, `Apply_govdesi_…` 1, `Apply_memberIds_verilmezse_…` 1, `Kapsam_yaniti_…` 3, `Panel_…_okur` 1, `Panel_…_null` 1, `…havuz_retleri…` 3, `Apply_409_no_balance…` 1) ve var olanların hepsi yeşil.

- [ ] **Adım 6: Derleme — 0 uyarı**

```powershell
dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q
```

Beklenen: `0 Warning(s)`, `0 Error(s)` (`OrderDeck.Licensing` ve `OrderDeck.Core` de derlenir; ikisi `TreatWarningsAsErrors`). `PaymentRequestService.cs:1400` üç argümanlı aşırı yüklemeyle değişmeden derlenir.

- [ ] **Adım 7: Regresyon (testler değiştirilmeden)**

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicenseApiClientPaymentRequestTests|FullyQualifiedName~LicenseApiClientTests"
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Licensing.Tests/OrderDeck.Licensing.Tests.csproj --nologo -v q
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CorrectedPaymentRequestsTests|FullyQualifiedName~PaymentPersonViewModelTests"
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentLeaseTests|FullyQualifiedName~PaymentLeaseServerRulesTests|FullyQualifiedName~RemotePaymentRecordsTests|FullyQualifiedName~TwoPcSalesConvergenceTests"
```

Beklenen: dördü de `Failed: 0` (R-İstemci, R-Ödeme, R-ModOn; sonuncusu dakikalar sürer). Neden kırılmazlar:
- Apply gövdesine eklenen `"memberIds": null` var olan iddiaları etkilemez: `AppliedBalanceBodies` okuyan testler alanları adla okur (`idempotencyKey` `PaymentRequestServiceTests.cs:845`, `saleScope` `:1780`, `AppliedKey` `:1007`, `AppliedAmountOf`); ad kümesi iddiası (`Names(...)`) yalnız `LicenseApiClientPaymentRequestTests`'in begin/finish/backfill/liste gövdelerinde (`:117`, `:447`, `:572`, `:575`), apply'da yok.
- `WhatsAppStubHandler` ve `FakeSalesServer` apply gövdesini `JsonDocument` ile alan alan okur (`FakeSalesServer.cs:2202-2212`, `FakeSalesServer.PaymentRules.cs:753-762`; `WhatsAppStubHandler` gövdeyi ham dize olarak saklar, `PaymentRequestServiceTests.cs:242-249`); fazladan alan yok sayılır.
- Bütün var olan konumsal kurulumlar (`CustomerBalanceApplyRequest` beş argüman: `PaymentRequestService.cs:1800`, `PaymentLeaseServerRulesTests.cs:615-647`; `CustomerBalanceDetailsResponse` iki argüman: `LicenseApiClient.cs:885`) yeni son parametreler varsayılanlı olduğundan aynen derlenir; bu kayıtları yapıbozuma (`var (…) =`) uğratan kod yok.

- [ ] **Adım 8: Commit**

git-bash:

```bash
git add OrderDeck.Licensing/Api/Models/CustomerBalanceDtos.cs OrderDeck.Licensing/Api/LicenseApiClient.cs OrderDeck.Tests/Licensing/LicenseApiClientPaymentRequestTests.cs
git commit -F - <<'MSG'
feat(balance): istemci sözleşmesine memberIds, personPool ve members alanlarını ekle

Ortak bakiye W-havuz (spec 2026-10-11 §6.1-a/d/e) S2 sunucusunun tel
sözleşmesini kullanır: önizleme kişinin GUID'li üyelerini tekrarlı memberIds
sorgu parametresiyle, apply gövdesi memberIds alanıyla taşır; kapsam yanıtı
personPool'u, panel GET members[]'i okur.

Eski çağrı biçimi korunur: üç argümanlı önizleme boş listeyle yeni aşırı
yüklemeyi çağırır ve sorgu bugünküyle aynı kalır; yeni alanların varsayılanı
(null, false) satır kipinde ve eski sunucuda bugünkü davranışı verir. Ödeme
akışı bu commit'te yeni alanları kullanmaz (Görev 3, 6, 7).

Havuz retleri (customer-not-synced, client-update-required,
member-ids-required, no-balance) bugünkü MapProblem eşlemesiyle
ValidationException.Code olur; testlerle kilitlenir.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 3: Ödeme akışında `memberIds`: önizleme, apply, replay (spec §6.1-a, §8.4-1)

**Files:**
- Değiştir: `OrderDeck.App/Services/PaymentRequestService.cs`
  - `SettleScopeAsync`'in dört çağrısı — `person` aşağı geçer: miras işin oynatması `:1138` (iki satıra bölünür), belirsiz işin oynatması `:1227`, 5–8. adımlar `:1275`, `:1277` (ikisi de iki satıra bölünür);
  - dört özel yöntemin imzası — `licenseId`'nin ardına `PaymentPerson person`: `ReviseAndSettleAsync` `:1286-1288`, `SettleAsync` `:1616-1618`, `ReplayOnceMoreIfStaleAsync` `:1640-1641`, `ReplayAsync` `:1780-1781` (satır sayısı değişmez) — ve iç çağrıları `:1319`, `:1431`, `:1626`, `:1629`, `:1643`, `:1645`;
  - 8. adım önizlemesi `:1395-1402` (yorum + `GetBalancePreviewAsync` çağrısı; var olan `try` bloğunun İÇİNDE, genel `catch` `:1403-1408` aynen);
  - `ReplayAsync` belgesi `:1754-1756` (A11 cümlesi) ve `:1764-1770` (yeni `<para>`), apply isteği `:1796-1802` (var olan `try` bloğunun İÇİNDE, genel kol `:1847-1850` aynen);
  - yeni özel statik `MemberIdsOf` — `SaleScopeOf` (`:1854-1862`) ardından, belgesiyle.
- Test genişlet: `OrderDeck.Tests/Services/PaymentRequestServiceTests.cs` — `WhatsAppStubHandler`: önizleme kolu `:228-232`, `PreviewMemberIds` (`PreviewCustomerIds` `:361-362` ardı), `AppliedMemberIds` (`AppliedCustomerIds` `:364-368` ardı), `MemberIdsOfBody` (`WpfCustomerIdOf(string)` `:373-377` ardı).
- Test oluştur: `OrderDeck.Tests/Services/PaymentRequestServiceTests.MemberIds.cs` (`public partial class PaymentRequestServiceTests`).

Bu görev Görev 2'nin yüzeyini TÜKETİR (`GetBalancePreviewAsync(…, IReadOnlyCollection<Guid> memberIds, …)`, `CustomerBalanceApplyRequest.MemberIds`); o dalda olmadan derlenmez. Görev 1'e derleme bağımlılığı yok (`MemberIdsOf` yalnız değişmeyen `PaymentOwnerRule.IsOwnerCapable(string)`'i çağırır, `PaymentPerson.cs:108`). Sıradan dolayı Görev 1 de dalda bulunur; sahip kuralı bu testlerin sahibini değiştirmez (`MakeCustomer` platform `"twitch"`, `Username` dolu — `UyeAdi()` 34 karakter, sınır 128).

**Not (spec ↔ kod):**
- **Satır numaraları:** spec'in önizlemesi `:1034` → bugün `:1400`; `ReplayAsync` `:1315-1362` → `:1780-1852`; "`:1096`'daki süzgeç" → `ReconcilePersonScopeAsync` `:1470` (`person.Members.Select(m => m.Id).Where(PaymentOwnerRule.IsOwnerCapable)`). Grep: `OrderDeck.App`'te `GetBalancePreviewAsync` ve `new CustomerBalanceApplyRequest(` birer yerde (`:1400`, `:1800`); kirasız ödeme kaydı ve kira koordinatörü bakiye çağırmaz — değişecek başka çağrı yeri yok. `ReviseAndSettleAsync`, `SettleAsync`, `ReplayOnceMoreIfStaleAsync`, `ReplayAsync`'in çağıranları yalnız `SettleScopeAsync` ağacında (`:1138`, `:1227`, `:1275`, `:1277`, `:1319`, `:1431`, `:1626`, `:1629`, `:1643`, `:1645`; Grep, başka çağıran yok) — `person` her yerde elde.
- **İskelet farkı 4 (önizleme ve `ReplayAsync` kişiyi tutmuyor; spec §6.1-a "Preview çağrısı kişiyi zaten elinde tutar"):** `ReviseAndSettleAsync` (`:1286-1288`) yalnız `licenseId, wpfCustomerId, job` bilir, `ReplayAsync` de öyle; kişi `SettleScopeAsync`'te (`hold.Person`, tıklamanın girişindeki anlık görüntü). `SettleScopeAsync` `person`'u dört özel yöntemle aşağı geçirir; statik `MemberIdsOf(anchor, person)` hem önizlemeyi hem `ReplayAsync`'i besler ve **depoyu okumaz**. Depo okuması (`GetPaymentPerson(job.CustomerId)`, spec l.409'un lafzı) tıklama içinde yanlış kişiyi okuyabilir: kira ve kişi kilidi grup değişikliğini durdurmaz — çekme turu var olan yerel satırı `isBusy`'ye bakmadan yazar (`CustomerSyncRepository.cs:160-177`; `isBusy` yalnız kimlik sahipleri kolunda `:188`) ve `GroupId` senkronlanan birimdir (`CustomerUnitMerge.cs:53-56`). Akış kapsam sorgularını beklerken (4. adım) tur, çapa `a`'nın GroupId'sini `x`'in grubuna yazarsa depo `{a, x}` döner; sunucu bu listeyi kendi `S(a)`'sıyla tutarlı bulur (kural 3a/3b boş) ve `x`'in bakiyesini `b`'nin kartından açılan, {a, b}'nin toplamı ve telefonuyla kurulan mesaja harcar (yeni grubun toplamı ≤ 0 ise `MarkNoBalance` yanlış kesinleşir). Akışın kişisi `W = {a, b}` gidince sunucu 3a ile 409 `customer-not-synced` döner, hiçbir şey düşülmez. Böylece iş, toplam, telefon, onay ve `memberIds` aynı kişiden gelir; mod On'da kiranın `begin`'iyle de (spec §15-5). Spec l.409'un "çağrı anında"sı tıklama düzeyinde karşılanır: sonraki tıklama kişiyi girişinde yeniden okur, oynatma güncel listeyi gönderir (spec'in amacı; bilinçli sapma iskelet fark 4'te). UX bedeli: akış ortasında kişiye artı bakiyeli bir üye katılırsa (S∖W, kural 3b) eski tasarım onu da çekerdi, bu tasarım 409 alır — sonraki tıklama kişiyi yeniden okur, döngü yok.
- **FARK-a2 (depo null):** `_customers` yalnız test/kompozisyonda null → `ReadPerson` (`:529-533`) tek satırlık yedek kişiye düşer (`PaymentPerson.Single(customer with { Id = leasedId })`) → `person.MemberIds` = [çapa] → liste yalnız çapa; `MemberIdsOf`'ta ayrı bir null dalı yok. Sunucu `a`'yı zaten `W`'ye ekler (`W ∪ {a}`) ve `HasMemberIds` `Count > 0` ister (`LicensesCustomerBalanceApplyController.cs:1086-1088`): çapa her zaman listede olduğu için havuz kipinde `member-ids-required` hiçbir zaman istemciden kaynaklanmaz.
- **Akış ortası testi (yeni kırmızı test):** kanca stub'ın var olan `ScopeResponseFor`'u (`PaymentRequestServiceTests.cs:347`; kapsam sorgusunda, `_sync` kilidinin dışında koşar): 4. adımın kapsam sorgusu — önizlemeden ve apply'dan önceki son ağ beklemesi — sürerken `GrubaAl` (Dapper) `a` ile `x`'i yeni bir gruba yazar ve `null` (204, iz yok) döner. Çekme turunun gerçek yolu (`ApplyServerCustomer`) damgalı `ServerCustomer` kurmayı gerektirirdi; testin ihtiyacı yalnız yerel `GroupId`'nin akış ortasında değişmesidir. Stub'a yeni kanca eklenmez.
- **Kiranın sunucu üyeleri (`serverMembers`, mod On, `:1474`) `memberIds`'e KATILMAZ.** Sunucu sözleşmesi `MemberIds`'i "WPF'in bildiği hâliyle" tanımlar (`LicensesCustomerBalanceApplyController.cs:397-398`); sunucunun kendi listesini ona geri göndermek `W`'yi `S`'den türetmek olurdu. `serverMembers` bugünkü işini (kapsam uzlaştırmasının adayları) aynen yapar.
- **Sıra:** spec "sıra belirlenimli" der, yöntemini söylemez → Guid'in `"N"` metninin ordinal sırası (S2'nin `PlanDraw` eşitlik kırıcısıyla aynı biçim). Aynı kişi her çağrıda aynı listeyi gönderir (aynı tıklamanın önizlemesi ve bütün denemeleri tek kişiden); sunucu listeyi küme olarak okur ve A11 içerik karşılaştırmasına sokmaz (`:902-932`: müşteri + `ProductTotal` + `Amount`) — liste tıklamalar arasında değişse bile (sonraki tıklamada birleşen üye) oynatma `content-conflict` almaz.
- **Neden oynatma da taşır (sunucudan doğrulandı):** havuz kipinde kayıtlı anahtarın oynatması havuz denetimlerinden ÖNCE döner (`:458-472`), ama sunucuya hiç ulaşmamış anahtarın "oynatması" yeni yazımdır ve sürüm kapısından sonra `memberIds` denetiminden geçer (`:474-481`). Belirsiz iş ve miras iş `ReplayAsync`'ten geçtiği için tek değişiklik üç yolu da kapsar (R08).
- **Test sapması (miras):** talimatın `MirasIs(db, b.Id)`'si yerine `MirasIs(db, a.Id)`: bu kurulum var olan, yeşil `Baska_uyenin_miras_isi_kendi_sahibiyle_oynatilir_benimsenmez_geri_alinir` (`PaymentRequestServiceTests.Person.cs:206-234`) yolunun aynısıdır (iki apply gövdesi: `[0]` miras anahtarı çapa `a`, `[1]` taze iş çapa `b`) ve iki farklı çapanın aynı kişi listesini gönderdiğini birlikte gösterir. `b.Id`'de miras taze işe benimsenir; akışın ikinci gövde üretip üretmediği 6. adımın doğrulamasına bağlıdır — test daha kırılgan olurdu.
- **R04 (satır kipi):** `WhatsAppStubHandler` yeni alanı yalnız gözler, cevabı değiştirmez; `FakeSalesServer` önizlemede ya sorguyu hiç okumaz (kurallarsız kol `FakeSalesServer.cs:2190-2194`) ya da yalnız `wpfCustomerId`'yi okur (kurallı kol `FakeSalesServer.PaymentRules.cs:729`, `QueryGuid` `:928-932`, `[?&]wpfCustomerId=` regex'i); apply'da yalnız bildiği alanları okur (`FakeSalesServer.PaymentRules.cs:755-762`). Var olan testler değişmez.

- [ ] **Adım 1: `WhatsAppStubHandler`'a `memberIds` gözlemi**

`OrderDeck.Tests/Services/PaymentRequestServiceTests.cs`, önizleme kolu (`:228-232`) bugünkü:

```csharp
            if (path.EndsWith("/customer-balance/preview", StringComparison.Ordinal))
            {
                var previewed = WpfCustomerIdOf(request);
                lock (_sync) PreviewCustomerIds.Add(previewed);
                if (OnPreviewAsync is not null) await OnPreviewAsync();
```

şununla değiştir:

```csharp
            if (path.EndsWith("/customer-balance/preview", StringComparison.Ordinal))
            {
                var previewed = WpfCustomerIdOf(request);
                // W-havuz: tekrarlı parametre GetValues ile okunur — indeksleyici (["memberIds"]) değerleri virgülle birleştirir.
                var memberIds = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query).GetValues("memberIds");
                lock (_sync)
                {
                    PreviewCustomerIds.Add(previewed);
                    PreviewMemberIds.Add(memberIds?.Select(v => Guid.Parse(v)).ToList() ?? new List<Guid>());
                }
                if (OnPreviewAsync is not null) await OnPreviewAsync();
```

`PreviewCustomerIds` (`:361-362`) bugünkü:

```csharp
        /// <summary>Önizleme ucuna sorulan WpfCustomerId'ler (sahip anahtarı testleri).</summary>
        public List<Guid> PreviewCustomerIds { get; } = new();
```

şununla değiştir:

```csharp
        /// <summary>Önizleme ucuna sorulan WpfCustomerId'ler (sahip anahtarı testleri).</summary>
        public List<Guid> PreviewCustomerIds { get; } = new();

        /// <summary>W-havuz: her önizlemenin tekrarlı memberIds değerleri, geliş sırasıyla (parametre yoksa boş liste).</summary>
        public List<IReadOnlyList<Guid>> PreviewMemberIds { get; } = new();
```

`AppliedCustomerIds` (`:364-368`) bugünkü:

```csharp
        /// <summary>Apply gövdelerinin WpfCustomerId'leri, gönderim sırasıyla.</summary>
        public IReadOnlyList<Guid> AppliedCustomerIds
        {
            get { lock (_sync) return AppliedBalanceBodies.Select(WpfCustomerIdOf).ToList(); }
        }
```

şununla değiştir:

```csharp
        /// <summary>Apply gövdelerinin WpfCustomerId'leri, gönderim sırasıyla.</summary>
        public IReadOnlyList<Guid> AppliedCustomerIds
        {
            get { lock (_sync) return AppliedBalanceBodies.Select(WpfCustomerIdOf).ToList(); }
        }

        /// <summary>W-havuz: apply gövdelerinin memberIds alanı, gönderim sırasıyla (alan yok ya da null → null).</summary>
        public IReadOnlyList<IReadOnlyList<Guid>?> AppliedMemberIds
        {
            get { lock (_sync) return AppliedBalanceBodies.Select(MemberIdsOfBody).ToList(); }
        }
```

`WpfCustomerIdOf(string)` (`:373-377`) bugünkü:

```csharp
        public static Guid WpfCustomerIdOf(string applyBody)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(applyBody);
            return doc.RootElement.GetProperty("wpfCustomerId").GetGuid();
        }
```

şununla değiştir:

```csharp
        public static Guid WpfCustomerIdOf(string applyBody)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(applyBody);
            return doc.RootElement.GetProperty("wpfCustomerId").GetGuid();
        }

        /// <summary>W-havuz: apply gövdesinin <c>memberIds</c> alanı — ham JSON adı, sunucunun bağladığı ad
        /// (<c>LicensesCustomerBalanceApplyController.ApplyRequest.MemberIds</c>). Alan yoksa ya da null ise null.</summary>
        public static IReadOnlyList<Guid>? MemberIdsOfBody(string applyBody)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(applyBody);
            return doc.RootElement.TryGetProperty("memberIds", out var el)
                   && el.ValueKind == System.Text.Json.JsonValueKind.Array
                ? el.EnumerateArray().Select(e => e.GetGuid()).ToList()
                : null;
        }
```

Stub cevabı değişmez (satır kipindeki sunucu gibi alanı okumaz).

- [ ] **Adım 2: Başarısız testleri yaz**

`OrderDeck.Tests/Services/PaymentRequestServiceTests.MemberIds.cs` (yeni):

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── Ortak bakiye W-havuz: kişinin üyeleri sunucuya (spec 2026-10-11 §6.1-a, §8.4-1) ─────────────────────
//
// Havuz kipinde sunucu çekim kümesini WPF'in bildirdiği üyelerle kurar (W ∪ {a}); W göndermeyen istemci 400
// member-ids-required alır. Önizleme (tekrarlı sorgu parametresi) ve apply (gövde alanı) kişinin GUID'li BÜTÜN
// üyelerini taşır: silinmişler (sunucu onları S'den alır, yine de gönderilir — K3) ve sahip dahil; GUID'siz satır hiç
// gitmez (sunucu bozuk Guid'i satır kipinde de 400'ler). Liste AKIŞIN KİŞİSİDİR: tıklamanın girişinde okunan, kilitleri
// ve kiraları tutulan anlık görüntü; tıklama içinde depo yeniden okunmaz — çekme turu akış ortasında çapanın GroupId'sini
// yazsa da önizleme ve apply operatörün kişisini taşır (iskelet fark 4). Belirsiz işin ve miras işin oynatması aynı
// listeyi gönderir; iş açıldıktan sonra birleşen üye sonraki tıklamada (kişi girişte yeniden okunur) gider.
// Satır kipindeki sunucu alanı okumaz: stub da okumaz, var olan testler değişmeden yeşil kalır (R04).
public partial class PaymentRequestServiceTests
{
    /// <summary>Üyeyi KVKK silinmiş yapar (<c>PurgedAt</c> dolu). Tam silme yolu akıştan gelir; burada yalnız kişi
    /// okumasının (<c>PaymentPersonSql</c>) gördüğü kolon gerekir.</summary>
    private static void Silinmis(SyncedDb db, Customer c)
    {
        using var conn = db.Factory.Open();
        Dapper.SqlMapper.Execute(conn, "UPDATE Customer SET PurgedAt = 1 WHERE Id = @id", new { id = c.Id });
    }

    /// <summary>Üretilmiş kullanıcı adı (repo public: literal ad yok). Hiçbir iddia kullanıcı adına bakmaz;
    /// 34 karakter sunucunun Username sınırının (128) altında, sahip seçimini değiştirmez.</summary>
    private static string UyeAdi() => $"u-{Guid.NewGuid():N}";

    /// <summary>GUID'siz (yerel) Id'li üye — sunucuya müşteri kimliği olarak gidemez.</summary>
    private static Customer YerelUye(SyncedDb db, string username)
        => Uye(db, username, id: $"yerel-{Guid.NewGuid():N}");

    /// <summary>Üyeleri <paramref name="grupId"/> grubuna yazar — çekme turunun var olan yerel satıra GroupId yazmasının
    /// yerine (<c>CustomerSyncRepository.ApplyServerCustomer</c> o kolda kira denetimi yapmaz). Dapper: ödeme akışının
    /// kişi kilidinden ve kiralarından geçmez; akış ortası kancasında çağrılır.</summary>
    private static void GrubaAl(SyncedDb db, string grupId, params Customer[] uyeler)
    {
        using var conn = db.Factory.Open();
        Dapper.SqlMapper.Execute(conn, "UPDATE Customer SET GroupId = @grupId WHERE Id IN @ids",
            new { grupId, ids = uyeler.Select(u => u.Id).ToList() });
    }

    [Fact] // §6.1-a, K3: GUID'li bütün üyeler (silinmiş dahil) önizlemeye gider; GUID'siz satır hiç gitmez
    public async Task Onizleme_kisinin_guidli_butun_uyelerini_gonderir_silinmis_dahil_guidsiz_haric()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi());
            var b = Uye(db, UyeAdi(), phone: false);
            var c = YerelUye(db, UyeAdi());
            Kisi(db, a, b, c);
            Silinmis(db, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 0m;

            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.PreviewMemberIds.Should().ContainSingle()
                .Which.Should().Equal(new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal),
                    "silinmiş b sunucuda S'den gelir ama yine gönderilir; yerel c'nin Id'si Guid değil, hiç gitmez");
            handler.AppliedBalanceBodies.Should().BeEmpty("önizleme 0 — anahtar hiç yazılmaz");
            Isler(db, "session:s1").Should().ContainSingle()
                .Which.State.Should().Be(PaymentJobState.NoBalance, "kesin sonuç bugünkü yoldan");
            _launcher.LaunchedUrls.Should().ContainSingle("bakiye yok: mesaj düşümsüz gider");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-a: apply gövdesi aynı listeyi taşır — sahip dahil; çapa (wpfCustomerId) değişmez; scope almaz (§6.1-h)
    public async Task Apply_govdesi_kisinin_guidli_uyelerini_tasir_sahip_dahil()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi(), phone: false);
            var b = Uye(db, UyeAdi());
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            var beklenen = new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList();
            handler.PreviewMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen);
            handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen,
                "sahip b de listede; sunucu çekim kümesini W ∪ {a} kurar");
            handler.AppliedCustomerIds.Should().ContainSingle().Which.Should().Be(G(b), "çapa işin sahip satırı");
            handler.ScopeQueries.Should().NotBeEmpty().And.OnlyContain(q => !q.Contains("memberIds"),
                "§6.1-h: scope çağrısı memberIds almaz");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-a, §8.4-1: oynatma tıklamanın kişisini taşır — kişi her tıklamanın girişinde okunur, sonradan birleşen üye sonraki tıklamada gider
    public async Task Replay_uyeleri_tiklama_girisinde_okur_sonradan_birlesen_uye_sonraki_tiklamada_dahil()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi(), phone: false);
            var b = Uye(db, UyeAdi());
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.ThrowTimeoutOnApply = true;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1"))
                .Should().Be(PaymentRequestResult.BalanceUncertain);
            Isler(db, "session:s1").Should().ContainSingle()
                .Which.State.Should().Be(PaymentJobState.ApplyUncertain);
            var ilkGovdeSayisi = handler.AppliedBalanceBodies.Count;
            ilkGovdeSayisi.Should().BeGreaterThan(0);
            handler.AppliedMemberIds[0].Should().Equal(
                new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal),
                "ilk denemede kişi {a, b}");

            // İş açıldıktan SONRA gruba yeni bir GUID'li üye katılır (başka PC'nin birleştirmesi indi).
            var d = Uye(db, UyeAdi(), phone: false);
            Kisi(db, a, b, d);
            handler.ThrowTimeoutOnApply = false;

            // İkinci tıklama kişiyi girişinde yeniden okur: {a, b, d}; belirsiz işin oynatması bu kişiyi taşır.
            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.AppliedBalanceBodies.Should().HaveCountGreaterThan(ilkGovdeSayisi, "belirsiz iş oynatıldı");
            AppliedKey(handler.AppliedBalanceBodies[^1]).Should().Be(AppliedKey(handler.AppliedBalanceBodies[0]),
                "oynatma: aynı anahtar, yeni düşüm yok");
            handler.AppliedMemberIds[^1].Should().Equal(
                new[] { G(a), G(b), G(d) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal),
                "oynatan tıklamanın kişisi girişinde okundu — sonradan birleşen d dahil");
            _launcher.LaunchedUrls.Should().ContainSingle("oynatma kesinleşti, mesaj gitti");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-a, §15-5 (iskelet fark 4): liste akışın kişisidir — akış ortasında çapa başka kişiye geçse de önizleme ve apply x'i taşımaz
    public async Task Akis_ortasinda_capa_baska_kisiye_gecse_de_memberIds_akisin_kisisidir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi());                   // telefonlu: sahip, çapa
            var b = Uye(db, UyeAdi(), phone: false);
            var x = Uye(db, UyeAdi(), phone: false);     // başka bir kişi
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            var yeniGrup = Guid.NewGuid().ToString("N");
            // 4. adımın kapsam sorgusu (önizlemeden ve apply'dan önceki son ağ beklemesi) sürerken çekme turu iner: a'nın
            // yerel GroupId'si x'inkiyle aynı yeni gruba yazılır (PC-B a'yı {a, b}'den ayırıp x'le birleştirdi). Kanca
            // her kapsam sorgusunda koşar (aynı yazım, eş etkili); iz yok → 204.
            handler.ScopeResponseFor = _ =>
            {
                GrubaAl(db, yeniGrup, a, x);
                return null;
            };

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            db.Customers.GetPaymentPerson(a.Id).MemberIds.Should().BeEquivalentTo(new[] { a.Id, x.Id },
                "kanca çalıştı: depoda çapanın kişisi artık {a, x}");
            var beklenen = new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList();
            handler.PreviewMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen,
                "önizleme tıklamanın kişisini taşır; depo çağrı anında okunsaydı {a, x} giderdi, sunucu bunu S(a) ile tutarlı bulurdu");
            handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen,
                "apply da aynı listeyi taşır: x'in bakiyesi b'nin mesajına harcanmaz (gerçek sunucu bu ayrışmayı 409 customer-not-synced ile kapatır)");
            handler.AppliedCustomerIds.Should().ContainSingle().Which.Should().Be(G(a), "çapa işin sahip satırı");
        }
        finally { Drop(db); }
    }

    [Fact] // Grupsuz tek satır: liste yalnız o satır
    public async Task Tek_satirlik_kiside_memberIds_yalniz_cikis_satiri()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi());
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.PreviewMemberIds.Should().ContainSingle().Which.Should().Equal(G(a));
            handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(G(a));
        }
        finally { Drop(db); }
    }

    [Fact] // Depo yok (test/kompozisyon): kişi tek satırlık yedek (ReadPerson → PaymentPerson.Single), liste yalnız çapa — sunucu a'yı zaten W'ye ekler, havuzda member-ids-required almaz
    public async Task Depo_yoksa_tek_satirlik_yedek_kisi_memberIds_yalniz_capa()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer(TestPhone.NewE164(), id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

        var capa = Guid.ParseExact(customer.Id, "N");
        handler.PreviewMemberIds.Should().ContainSingle().Which.Should().Equal(capa);
        handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(capa);
    }

    [Fact] // §8.4-1: miras işin oynatması da memberIds taşır (havuz kipinde member-ids-required almaz)
    public async Task Miras_isin_oynatmasi_da_memberIds_tasir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi(), phone: false);
            var b = Uye(db, UyeAdi());
            Kisi(db, a, b);
            var (_, k) = MirasIs(db, a.Id);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            var beklenen = new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList();
            handler.AppliedBalanceBodies.Should().HaveCount(2);
            AppliedKey(handler.AppliedBalanceBodies[0]).Should().Be(k);
            handler.AppliedCustomerIds[0].Should().Be(G(a), "miras iş kendi sahip satırıyla (çapa a) oynatılır");
            handler.AppliedMemberIds[0].Should().Equal(beklenen, "miras işin oynatması da kişinin üyelerini taşır");
            handler.AppliedCustomerIds[1].Should().Be(G(b));
            handler.AppliedMemberIds[1].Should().Equal(beklenen, "taze apply (çapa b): aynı kişi, aynı liste");
        }
        finally { Drop(db); }
    }
}
```

Yardımcı adları (`Silinmis`, `UyeAdi`, `YerelUye`, `GrubaAl`), yeni test adı (`Akis_ortasinda_capa_baska_kisiye_gecse_de_memberIds_akisin_kisisidir`) ve stub üyeleri (`PreviewMemberIds`, `AppliedMemberIds`, `MemberIdsOfBody`) sınıfta yok (Grep ile denetlendi). `Uye`, `Kisi`, `KisiSut`, `Isler`, `MirasIs`, `G` `.Person.cs:27-60`'ta; `FileDb`/`Drop`/`SyncedDb` `PaymentRequestServiceTests.cs:2158-2180` (`SyncedDb.Customers` gerçek `CustomerRepository`, `GetPaymentPerson` public); `MakeCloudSut` `:418-426`; `MakeCustomer` `:509-513`; `AppliedKey` `:1004`; `T` `:1001`; akış ortası kancası `WhatsAppStubHandler.ScopeResponseFor` `:347` (çağrısı `:220-222`, `_sync` kilidinin dışında). Kullanıcı adları `UyeAdi()` ile üretilir (Genel kurallar); telefon `TestPhone.NewE164()` (`Uye` içinden). Akış ortası testinde sahip `a`'dır (telefonlu tek üye, sahip kuralının telefon önceliği; `PaymentPerson.cs:117`); tıklanan `b`'nin telefonu yok, mesaj kişinin iletişim numarasına (`ContactPhone` → `a`) gider.

- [ ] **Adım 3: Koştur — FAIL bekle**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Onizleme_kisinin|FullyQualifiedName~PaymentRequestServiceTests.Apply_govdesi|FullyQualifiedName~PaymentRequestServiceTests.Replay_uyeleri|FullyQualifiedName~PaymentRequestServiceTests.Akis_ortasinda_capa|FullyQualifiedName~PaymentRequestServiceTests.Tek_satirlik_kiside_memberIds|FullyQualifiedName~PaymentRequestServiceTests.Depo_yoksa_tek_satirlik|FullyQualifiedName~PaymentRequestServiceTests.Miras_isin_oynatmasi"`
Expected: FAIL (7 test; derleme temiz — Görev 2'nin DTO'su ve aşırı yüklemesi dalda). Önizleme iddiaları: servis hâlâ 3 parametreli `GetBalancePreviewAsync`'i çağırır, sorguda `memberIds` yok → `PreviewMemberIds` elemanı boş liste ("Expected … to be equal to {…}, but … contains 2 item(s) less"). Apply iddiaları: istek `MemberIds`'siz kurulur, gövde `"memberIds":null` → `MemberIdsOfBody` null ("Expected … to be equal to {…}, but found <null>"). `Akis_ortasinda_…`'nın ön koşul iddiası (depoda çapanın kişisi `{a, x}`) bugün de geçer — kapsam sorgusu (4. adım) master'da var, kanca koşar; test önizleme iddiasında aynı nedenle düşer. Bu test ayrıca depo okuyan tasarıma karşı kilittir: `MemberIdsOf` üyeleri çağrı anında depodan okusaydı önizleme ve apply `{a, x}` taşırdı ("… differs at index …").

- [ ] **Adım 4: `MemberIdsOf` + kişinin aşağı geçirilmesi + önizleme + oynatma**

`OrderDeck.App/Services/PaymentRequestService.cs` — on beş Edit, dosya sırasıyla. `SettleScopeAsync` akışın kişisini (`person`, `hold.Person`) dört özel yönteme aşağı geçirir; yeni parametre her imzada `licenseId`'nin ardında (`ReconcilePersonScopeAsync(licenseId, person, job, …)` deseni). İmzaların satır sayısı değişmez; uzayan üç çağrı (`:1138`, `:1275`, `:1277`) iki satıra bölünür. Bu Edit'ler Görev 4'ün alıntıladığı bloklara (miras dönüşü `:1151-1153`, belirsiz işin dönüşü `:1229-1232`, önizlemenin `catch`'leri, `SettleAsync`'in sonu `:1632-1634`, `ReplayAsync`'in `catch`'leri) dokunmaz.

(i) `SettleScopeAsync` 1. adım — miras işin oynatması (`:1138`) bugünkü:

```csharp
                var attempt = await ReplayOnceMoreIfStaleAsync(licenseId.Value, Guid.ParseExact(l.CustomerId, "N"), l, ct);
```

şununla değiştir (çapa miras işin kendi satırı; liste yine akışın kişisi — miras iş o kişinin bir üyesindedir, `ReconcilableLegacies` `_jobs.GetOpenLegacies(person.MemberIds)` ile toplar):

```csharp
                var attempt = await ReplayOnceMoreIfStaleAsync(
                    licenseId.Value, person, Guid.ParseExact(l.CustomerId, "N"), l, ct);
```

(ii) `SettleScopeAsync` 3. adım — belirsiz işin oynatması (`:1227`) bugünkü:

```csharp
            var attempt = await ReplayOnceMoreIfStaleAsync(licenseId.Value, wpfCustomerId, job, ct);
```

şununla değiştir:

```csharp
            var attempt = await ReplayOnceMoreIfStaleAsync(licenseId.Value, person, wpfCustomerId, job, ct);
```

(iii) `SettleScopeAsync` 5–8. adımların iki çağrısı (`:1275-1277`) bugünkü:

```csharp
        var settled = await ReviseAndSettleAsync(licenseId.Value, wpfCustomerId, job, totalAmount, remoteConfirmed, ct);
        if (!settled.ScopeDeductionAdopted) return settled;
        settled = await ReviseAndSettleAsync(licenseId.Value, wpfCustomerId, settled.Job!, totalAmount, remoteConfirmed: true, ct);
```

şununla değiştir:

```csharp
        var settled = await ReviseAndSettleAsync(
            licenseId.Value, person, wpfCustomerId, job, totalAmount, remoteConfirmed, ct);
        if (!settled.ScopeDeductionAdopted) return settled;
        settled = await ReviseAndSettleAsync(
            licenseId.Value, person, wpfCustomerId, settled.Job!, totalAmount, remoteConfirmed: true, ct);
```

(iv) `ReviseAndSettleAsync` imzası (`:1286-1288`) bugünkü:

```csharp
    private async Task<BalanceOutcome> ReviseAndSettleAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, decimal totalAmount, bool remoteConfirmed,
        CancellationToken ct)
```

şununla değiştir:

```csharp
    private async Task<BalanceOutcome> ReviseAndSettleAsync(
        Guid licenseId, PaymentPerson person, Guid wpfCustomerId, PaymentJob job, decimal totalAmount, bool remoteConfirmed,
        CancellationToken ct)
```

(v) 5. adımın taze uygulaması (`:1317-1319`) bugünkü:

```csharp
                _jobs.BeginRevision(job.Id, totalAmount, Guid.NewGuid(), job.Revision);
                job = _jobs.Get(job.Id)!;
                return await SettleAsync(licenseId, wpfCustomerId, job, totalAmount, ct);
```

şununla değiştir:

```csharp
                _jobs.BeginRevision(job.Id, totalAmount, Guid.NewGuid(), job.Revision);
                job = _jobs.Get(job.Id)!;
                return await SettleAsync(licenseId, person, wpfCustomerId, job, totalAmount, ct);
```

(vi) Önizleme (`:1395-1402`) bugünkü:

```csharp
        // 8) Taze iş: önizleme (bakiye yoksa anahtar hiç yazılmaz) →
        //    anahtar diske → apply.
        decimal previewBalance;
        try
        {
            var preview = await _api.GetBalancePreviewAsync(licenseId, wpfCustomerId, ct);
            previewBalance = preview.Balance;
        }
```

şununla değiştir:

```csharp
        // 8) Taze iş: önizleme (bakiye yoksa anahtar hiç yazılmaz) →
        //    anahtar diske → apply.
        //    W-havuz (spec 2026-10-11 §6.1-a): önizleme akışın kişisinin GUID'li üyelerini memberIds ile taşır (MemberIdsOf;
        //    depo okunmaz — tıklamanın girişindeki anlık görüntü, apply'la ve oynatmayla aynı liste).
        decimal previewBalance;
        try
        {
            var preview = await _api.GetBalancePreviewAsync(
                licenseId, wpfCustomerId, MemberIdsOf(wpfCustomerId, person), ct);
            previewBalance = preview.Balance;
        }
```

(vii) 8. adımın taze uygulaması (`:1430-1432`) bugünkü:

```csharp
        job = _jobs.Get(job.Id)!;
        return await SettleAsync(licenseId, wpfCustomerId, job, totalAmount, ct);
    }
```

şununla değiştir (alıntı bilerek üç satır: master'da 8 boşluklu çağrı satırı tek başına (v)'nin 16 boşluklu satırının alt dizgisidir — Edit'ler hangi sırayla uygulanırsa uygulansın alıntı tekil kalır):

```csharp
        job = _jobs.Get(job.Id)!;
        return await SettleAsync(licenseId, person, wpfCustomerId, job, totalAmount, ct);
    }
```

(viii) `SettleAsync` imzası (`:1616-1618`) bugünkü:

```csharp
    private async Task<BalanceOutcome> SettleAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, decimal requestedTotal,
        CancellationToken ct)
```

şununla değiştir:

```csharp
    private async Task<BalanceOutcome> SettleAsync(
        Guid licenseId, PaymentPerson person, Guid wpfCustomerId, PaymentJob job, decimal requestedTotal,
        CancellationToken ct)
```

(ix) `SettleAsync`'in iki apply denemesi (`:1626-1629`) bugünkü:

```csharp
        var attempt = await ReplayAsync(licenseId, wpfCustomerId, job, ct);
        if (!attempt.Adopted && attempt.Job.State == PaymentJobState.ApplyUncertain)
        {
            attempt = await ReplayAsync(licenseId, wpfCustomerId, attempt.Job, ct);
```

şununla değiştir (ilk satır `ReplayOnceMoreIfStaleAsync`'te de aynen geçer; ikinci satır alıntıyı tekil yapar):

```csharp
        var attempt = await ReplayAsync(licenseId, person, wpfCustomerId, job, ct);
        if (!attempt.Adopted && attempt.Job.State == PaymentJobState.ApplyUncertain)
        {
            attempt = await ReplayAsync(licenseId, person, wpfCustomerId, attempt.Job, ct);
```

(x) `ReplayOnceMoreIfStaleAsync` imzası ve iki denemesi (`:1640-1645`) bugünkü:

```csharp
    private async Task<ApplyAttempt> ReplayOnceMoreIfStaleAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
    {
        var attempt = await ReplayAsync(licenseId, wpfCustomerId, job, ct);
        if (!attempt.Stale || attempt.Job.State != PaymentJobState.ApplyUncertain) return attempt;
        attempt = await ReplayAsync(licenseId, wpfCustomerId, attempt.Job, ct);
```

şununla değiştir:

```csharp
    private async Task<ApplyAttempt> ReplayOnceMoreIfStaleAsync(
        Guid licenseId, PaymentPerson person, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
    {
        var attempt = await ReplayAsync(licenseId, person, wpfCustomerId, job, ct);
        if (!attempt.Stale || attempt.Job.State != PaymentJobState.ApplyUncertain) return attempt;
        attempt = await ReplayAsync(licenseId, person, wpfCustomerId, attempt.Job, ct);
```

(xi) `ReplayAsync` belgesinin ilk paragrafı (`:1754-1756`) bugünkü:

```csharp
    /// <summary>İşin diskteki anahtarıyla apply'ı (yeniden) dener, sonucu işe
    /// yazar. Amount=ProductTotal gönderilir — sunucu bakiyeye/toplama kırpar;
    /// replay'de birebir aynı gövde gittiği için A11 içerik kontrolünden geçer.
```

şununla değiştir (oynatma gövdesi artık bayt bayt aynı olmayabilir: `memberIds` oynatan tıklamanın kişisidir — iş açıldıktan sonra birleşen üye sonraki tıklamada listeye girer):

```csharp
    /// <summary>İşin diskteki anahtarıyla apply'ı (yeniden) dener, sonucu işe
    /// yazar. Amount=ProductTotal gönderilir — sunucu bakiyeye/toplama kırpar;
    /// replay'de A11'in karşılaştırdığı alanlar (müşteri, ProductTotal, Amount) aynı gittiği için içerik kontrolünden geçer
    /// (kapsam ve memberIds karşılaştırmanın dışında).
```

(xii) `ReplayAsync` belgesi (`:1764-1770`) bugünkü:

```csharp
    /// R4-01: cevap, GÖNDERİLDİĞİ denemeye aittir. İstek tele çıktıktan sonra
    /// araya bir revizyon girebilir (ağda takılan cevap dakikalarca sürebilir);
    /// o yüzden sonuç, isteği açan anahtar + revizyon koşuluyla yazılır. Koşul
    /// tutmuyorsa cevap sessizce DEĞİL, günlüğe yazılarak atılır — dönüşte
    /// satır yeniden okunduğu için çağıran zaten kazananın sonucunu görür.
    ///
    /// <para>Spec 2026-10-13 §6.6 (W3-11; sunucu bunları yalnız mod On'da üretir, kod moddan bağımsız):
```

şununla değiştir:

```csharp
    /// R4-01: cevap, GÖNDERİLDİĞİ denemeye aittir. İstek tele çıktıktan sonra
    /// araya bir revizyon girebilir (ağda takılan cevap dakikalarca sürebilir);
    /// o yüzden sonuç, isteği açan anahtar + revizyon koşuluyla yazılır. Koşul
    /// tutmuyorsa cevap sessizce DEĞİL, günlüğe yazılarak atılır — dönüşte
    /// satır yeniden okunduğu için çağıran zaten kazananın sonucunu görür.
    ///
    /// <para>W-havuz (spec 2026-10-11 §6.1-a, §8.4-1): gövde akışın kişisinin GUID'li üyelerini <c>memberIds</c> ile taşır
    /// (<see cref="MemberIdsOf"/>; <paramref name="person"/> tıklamanın girişindeki anlık görüntü, depo okunmaz) — taze apply,
    /// belirsiz işin ve miras işlerin oynatması hepsi buradan geçer. Havuz kipinde kayıtlı anahtarın oynatması üye denetiminden
    /// önce döner, ama sunucuya hiç ulaşmamış anahtarın oynatması yeni yazımdır ve <c>memberIds</c> ister. Liste A11 içerik
    /// karşılaştırmasının dışında: tıklamalar arasında değişse de oynatma çakışmaz.</para>
    ///
    /// <para>Spec 2026-10-13 §6.6 (W3-11; sunucu bunları yalnız mod On'da üretir, kod moddan bağımsız):
```

(xiii) `ReplayAsync` imzası (`:1780-1781`) bugünkü:

```csharp
    private async Task<ApplyAttempt> ReplayAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
```

şununla değiştir:

```csharp
    private async Task<ApplyAttempt> ReplayAsync(
        Guid licenseId, PaymentPerson person, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
```

(xiv) Apply isteği (`:1796-1802`) bugünkü:

```csharp
        try
        {
            var apply = await _api.ApplyBalanceAsync(
                licenseId,
                new CustomerBalanceApplyRequest(
                    wpfCustomerId, job.ProductTotal, job.ProductTotal, key, SaleScopeOf(job)),
                ct);
```

şununla değiştir:

```csharp
        try
        {
            var apply = await _api.ApplyBalanceAsync(
                licenseId,
                new CustomerBalanceApplyRequest(
                    wpfCustomerId, job.ProductTotal, job.ProductTotal, key, SaleScopeOf(job),
                    MemberIds: MemberIdsOf(wpfCustomerId, person)),
                ct);
```

(xv) `SaleScopeOf` (`:1862`) bugünkü:

```csharp
    private static string SaleScopeOf(PaymentJob job) => job.SaleScopeKey;
```

şununla değiştir:

```csharp
    private static string SaleScopeOf(PaymentJob job) => job.SaleScopeKey;

    /// <summary>W-havuz (spec 2026-10-11 §6.1-a): sunucuya giden kişi üyeleri — akışın kişisinin
    /// (<paramref name="person"/>: tıklamanın girişinde okunan, kilitleri ve kiraları tutulan anlık görüntü,
    /// <see cref="PersonHold"/>) GUID'li bütün üyeleri (silinmişler ve sahip dahil; <see cref="PaymentOwnerRule.IsOwnerCapable"/>
    /// süzgeci, kapsam uzlaştırmasının yerel adaylarıyla aynı). Depo OKUNMAZ: çekme turu kira altındaki satırın GroupId'sini
    /// de yazar (<c>CustomerSyncRepository.ApplyServerCustomer</c>, var olan satırda kira denetimi yok); akış ortasında
    /// okunan kişi çapayı başka bir kişiyle gösterebilir, sunucu o listeyi kendi S(a)'sıyla tutarlı bulur ve işin
    /// toplamını, telefonunu, onayını taşıyan kişinin dışındaki bir satırdan düşerdi. Anlık görüntü para taşıyan bir
    /// ayrışma içeriyorsa (W'nin S dışındaki canlı üyesi ya da S'nin W dışındaki, bakiyesi &gt; 0 canlı üyesi) sunucu
    /// <c>customer-not-synced</c> döner; öbür bayat görüntülerde çekim <c>W ∩ S</c>'nin canlıları ∪ S'nin silinmişleriyle
    /// sınırlıdır (kişide olmayan canlı satırdan para çekilmez). Sonraki tıklama kişiyi girişte yeniden okur (iş açıldıktan sonra
    /// birleşen üye o tıklamada gider). Mod On'da liste kiranın <c>begin</c>'ine giden üye kümesiyle aynı kaynaktan
    /// (spec §15-5). Çapa (<paramref name="anchor"/>) her zaman listede: sunucu <c>W ∪ {a}</c> kurar; depo yoksa (testler)
    /// kişi tek satırlıktır (<see cref="ReadPerson"/>), liste yalnız çapa. Sıra <c>"N"</c> biçiminin ordinal sırası: aynı
    /// kişi her çağrıda aynı listeyi gönderir; liste A11 içerik karşılaştırmasına girmez. Kiranın sunucu üyeleri
    /// katılmaz — liste WPF'in bildiği kişidir. Satır kipindeki sunucu alanı okumaz; eski sunucu yok sayar.</summary>
    private static IReadOnlyList<Guid> MemberIdsOf(Guid anchor, PaymentPerson person)
        => person.MemberIds
            .Where(PaymentOwnerRule.IsOwnerCapable)
            .Select(id => Guid.ParseExact(id, "N"))
            .Append(anchor)
            .Distinct()
            .OrderBy(g => g.ToString("N"), StringComparer.Ordinal)
            .ToList();
```

`catch` kollarının hiçbiri değişmez: `customer-not-synced` bu görevde bugünkü genel kola (`:1847-1850`) düşer (Görev 4 ayırır). `ImplicitUsings` açık (`OrderDeck.App.csproj:8`): `IReadOnlyList<T>` için `using` gerekmez; `PaymentPerson` ve `PaymentOwnerRule` `OrderDeck.Core.Customers`'tan (`:7` zaten var). `MemberIdsOf` statik ve saftır: depo okuması yok, atmaz (`Guid.ParseExact` yalnız `IsOwnerCapable`'ın geçirdiği Id'lere); önizleme ve apply çağrıları yine var olan `try` bloklarının içinde. `person` null olamaz: `ReadPerson` (`:529-533`) depo yoksa ya da satır okunamazsa tek satırlık kişiye düşer.

- [ ] **Adım 5: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Onizleme_kisinin|FullyQualifiedName~PaymentRequestServiceTests.Apply_govdesi|FullyQualifiedName~PaymentRequestServiceTests.Replay_uyeleri|FullyQualifiedName~PaymentRequestServiceTests.Akis_ortasinda_capa|FullyQualifiedName~PaymentRequestServiceTests.Tek_satirlik_kiside_memberIds|FullyQualifiedName~PaymentRequestServiceTests.Depo_yoksa_tek_satirlik|FullyQualifiedName~PaymentRequestServiceTests.Miras_isin_oynatmasi"`
Expected: PASS (7 test).

Çağrı yeri denetimi (değişecek başka yer kalmadı):

Run: `git grep -n "GetBalancePreviewAsync\|new CustomerBalanceApplyRequest(" -- OrderDeck.App`
Expected: iki satır, ikisi de `OrderDeck.App/Services/PaymentRequestService.cs` (önizleme ve `ReplayAsync`).

Kişinin bütün çağrılarda aşağı geçtiği (eski biçimli çağrı kalmadı):

Run: `git grep -n "(licenseId, wpfCustomerId\|licenseId.Value, wpfCustomerId\|licenseId.Value, Guid.ParseExact" -- OrderDeck.App/Services/PaymentRequestService.cs`
Expected: çıktı yok — dört yöntemin on çağrısı da `person`'u `licenseId`'nin ardında geçirir (önizlemenin `licenseId, wpfCustomerId, MemberIdsOf(…)` satırı parantezle başlamadığı için eşleşmez).

- [ ] **Adım 6: Derleme — 0 uyarı**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Expected: `Build succeeded`, 0 uyarı (`TreatWarningsAsErrors`; statik `MemberIdsOf` iki yerden çağrılır; dört imzanın yeni `person` parametresinin hepsi kullanılır — `ReplayAsync` ve `ReviseAndSettleAsync`'te `MemberIdsOf`'a, öbürlerinde aşağı geçer; yeni alan yok).

- [ ] **Adım 7: Regresyon (var olan testler değişmeden)**

R-Ödeme — satır kipi para davranışı aynı (stub `memberIds`'i yalnız gözler):

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CorrectedPaymentRequestsTests|FullyQualifiedName~PaymentPersonViewModelTests"`
Expected: hepsi PASS.

R-ModOn (yavaş) — kira akışı ve `FakeSalesServer` yeni sorgu parametresini/gövde alanını yok sayar:

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentLeaseTests|FullyQualifiedName~PaymentLeaseServerRulesTests|FullyQualifiedName~RemotePaymentRecordsTests|FullyQualifiedName~TwoPcSalesConvergenceTests"`
Expected: hepsi PASS.

R-İstemci — Görev 2'nin tel sözleşmesi bu görevle değişmedi:

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicenseApiClientPaymentRequestTests|FullyQualifiedName~LicenseApiClientTests"`
Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Licensing.Tests/OrderDeck.Licensing.Tests.csproj --nologo -v q`
Expected: hepsi PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.App/Services/PaymentRequestService.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.MemberIds.cs
git commit -F - <<'MSG'
feat(balance): önizleme ve apply kişinin GUID'li üyelerini memberIds ile göndersin

Havuz kipinde sunucu çekim kümesini WPF'in bildirdiği üyelerle kurar
(W ∪ {a}); W göndermeyen istemci 400 member-ids-required alır. Önizleme
(tekrarlı sorgu parametresi) ve apply (gövde alanı) artık kişinin GUID'li
bütün üyelerini taşır: silinmişler ve sahip dahil, GUID'siz satır hariç,
"N" ordinal sırasıyla, çapa her zaman listede. Tek özel yardımcı
(MemberIdsOf) listeyi akışın kişisinden kurar — tıklamanın girişinde
okunan, kilitleri ve kiraları tutulan anlık görüntü; SettleScopeAsync onu
dört özel yöntemle aşağı geçirir, depo tıklama içinde yeniden okunmaz.
Çekme turu kira altındaki satırın GroupId'sini de yazdığı için akış
ortasında okunan liste çapayı başka bir kişiyle gösterebilirdi; sunucu
onu kendi kişisiyle tutarlı bulur ve başka kişinin bakiyesini bu mesaja
harcardı. Anlık görüntü para taşıyan bir ayrışma içeriyorsa (W'nin S
dışındaki canlı üyesi ya da S'nin W dışındaki, bakiyesi > 0 canlı üyesi)
sunucu 409 customer-not-synced döner; öbür durumlarda çekim W ∩ S'nin
canlıları ∪ S'nin silinmişleriyle sınırlıdır.
Belirsiz işin ve miras işlerin oynatması da aynı listeyi gönderir; iş
açıldıktan sonra birleşen üye sonraki tıklamada gider. Depo yoksa kişi
tek satırlık, liste yalnız çapa. Satır kipindeki sunucu ve eski sunucu
alanı okumaz; davranış değişmez (ortak bakiye §6.1-a, §8.4-1, §15-5).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 4: customer-not-synced → BalanceNotSynced: bayrak, uyarı, 5 dakika metni, öbür yanıtların regresyonu (spec §6.1-b, §6.1-c, K2, K7, §8.4-2)

**Files:**
- Değiştir: `OrderDeck.App/Services/PaymentRequestService.cs`
  - `:1-2` `using System.Collections.Concurrent;` (4b)
  - `:65-70` enum'un son üyesi `Blocked`'tan sonra `BalanceNotSynced` (4a; belgesi 4b'de bir cümle alır)
  - `:122-124` `PaymentRequestOutcome` belgesinin `Message` paragrafı (4b)
  - `:161-163` `PersonGateTimeout`'un ardı: `CustomerNotSyncedCode` (4a); `NotSyncedPatience`, `NotSyncedStreakGap`, `NotSyncedClock`, `_notSyncedSince` (4b)
  - `:701-713` `SendCoreAsync` bakiye kapısı (4a: sonuç ayrımı; 4b: sayaç sıfırlama + `NotSyncedOutcome`)
  - `:870-896` `BalanceOutcome` (`NotSynced` init), `ApplyAttempt` (`NotSynced = false` + belge) (4a); `BalanceOutcome`'un önüne yeni `NotSyncedOutcome` (4b)
  - `:1151-1152` miras işin belirsiz dönüşü, `:1229-1232` belirsiz işin dönüşü (4a)
  - `:1403` önizlemenin genel `catch`'inin ÖNÜNE yeni `catch` (4a)
  - `:1632-1634` `SettleAsync`'in son satırı (4a)
  - `:1821-1828` `ReplayAsync`: `stale` kolu ile `ScopeHasDeductionException` kolu arasına yeni `catch` (4a)
  - Numaralar master `7960eba3`'ten. Önceki görevlerin kaydırması: Görev 1 bakiye kapısı yorumunu (`:694-695`) +1 satır uzatır; Görev 3 miras işin oynatma çağrısını (`:1138`, +1: iki satıra bölünür), 5–8. adımların iki çağrısını (`:1275`, `:1277`, +2), önizlemeyi (`:1395-1402`, +3 satır: 2 yorum + 1 çağrı), `ReplayAsync` belgesini (`:1754-1756` +1, `:1764-1770` +6 = +7 satır) ve apply isteğini (`:1796-1802`, +1 satır) uzatır; dört imzanın (`ReviseAndSettleAsync`, `SettleAsync`, `ReplayOnceMoreIfStaleAsync`, `ReplayAsync`) ve öbür çağrıların satır sayısı değişmez; `MemberIdsOf` `SaleScopeOf`'un ardına (`:1862` sonrası) eklenir. Yürütmede `:701-1137` yaklaşık +1, miras dönüşü (`:1151-1152`) ve belirsiz işin dönüşü (`:1229-1232`) yaklaşık +2, önizleme `catch`'i (`:1403`) ve `SettleAsync`'in sonu (`:1632-1634`) yaklaşık +7, `ReplayAsync` içi (`:1821-1828`, `:1847-1850`) yaklaşık +15 kayar. Görev 3'ün değiştirdiği satırlar (çağrılar, imzalar, önizleme çağrısı, apply isteği) bu görevin hiçbir alıntısında yok. Her Edit satır numarasına değil alıntılanan metne bağlıdır.
- Değiştir: `OrderDeck.App/Services/PaymentResultPresenter.cs` — sınıf başına sabitler (`:26-27` ardı; 4a iki, 4b bir), `Notify(outcome)` belgesi ve kolu (`:28-48`), `Notify(result)` kolu (`:73-86`'nın ardı) (4a).
- Değiştir: `OrderDeck.App/Services/PriceCorrection/CorrectedPaymentRequests.cs:546-547` (4b).
- Oluştur (test): `OrderDeck.Tests/TestHelpers/ManualTimeProvider.cs` (4b), `OrderDeck.Tests/Services/PaymentRequestServiceTests.NotSynced.cs` (4a; 4b genişletir).
- Değiştir (test): `OrderDeck.Tests/Services/PaymentRequestServiceTests.cs` — `WhatsAppStubHandler`: `:146-147` ardına `PreviewResponse` + sayaç, önizleme kolu (`:233-235`, bakiye okumasının önü), `Problem` `:400-406` `private` → `public` (4a); `SutOn` `:428-446` son parametre `log` (4b).
- Değiştir (test): `OrderDeck.Tests/Sync/PaymentLeaseTests.cs` — `Cloud_API_isleniyor_SendPending_finish_unconfirmed`'in (`:309-326`) ardına mod On testi (4a; §6.1-b'nin kirayla etkileşimi).
- Değiştir (test): `OrderDeck.Tests/Services/PaymentResultPresenterTests.cs` (sınıf sonu `:239-241`; 4a iki test, 4b bir test), `OrderDeck.Tests/Services/CorrectedPaymentRequestsTests.cs` (`:974`'ün ardı, 4b bir test).

İki parça, iki commit: **4a** bayrağın taşınması + yeni sonuç + uyarı (Adım 1–7); **4b** 5 dakika kuralı + fiyat düzeltmesinin toplu gönderimi + değişmeyen yanıtların kilit testleri (Adım 8–16). Görev 3 (önizleme ve `ReplayAsync`'te `MemberIdsOf`) önce uygulanmış olmalı; bu görevin `catch`'leri o çağrıların bulunduğu `try` bloklarına girer.

**Not (spec ↔ kod):**
- Spec'in satırları 11.10 kesitinden: önizlemenin genel `catch`'i `:1037-1042` → bugün `:1403-1408`; `BalanceOutcome` `:619-630` → `:870-885`; `ReplayAsync`'in genel kolu `:1357-1360` → `:1847-1850`; `Notify` `:47-60` → `:57-115` (`BalanceUncertain` kolu `:73-86`); enum `:15` aynı. Kod eşlemesi spec'in dediği `ThrowMappedAsync` (önizleme) ve apply'ın kendi 409 okuması (`LicenseApiClient.cs:856-862`); ikisi de `MapProblem` (`:1210-1222`) üzerinden 4xx başlığını `ValidationException.Code` yapar — istemci kodu değişmez.
- **Fark 7 (taşıma).** Apply tarafında `ReplayAsync`'in sonucu belirsiz dönüşe üç yoldan varır: miras işin oynatması (`:1151-1152`), belirsiz işin oynatması (`:1231-1232`), `SettleAsync` → `ClassifySettledJob` (`:1634`). Üçü de `ApplyAttempt.NotSynced`'i `BalanceOutcome.NotSynced`'e taşır. K3'ün anında tekrarı (`:1626-1630`) aynen kalır: `customer-not-synced` `stale` değildir, ikinci apply yine gider; son denemenin bayrağı geçerlidir. `ReplayOnceMoreIfStaleAsync` yalnız `stale`'de yineler (miras ve belirsiz işte tek apply). `no-balance`/`nothing-to-apply` kolu (`:1806-1812`) aynen: `MarkNoBalance` yalnız orada.
- **Fark 8 (tüketiciler).** `PaymentResultPresenter.Notify` iki aşırı yükleme; `CorrectedPaymentRequests` `switch`'i (`_` kolu `outcome.Result.ToString()` gösterirdi) `BalanceNotSynced`'i `BalanceUncertain` satırının metnine eşler — K2 "WPF bunu `BalanceUncertain` gibi ele alır"; spec bu bağlam için metin vermiyor, yenisi icat edilmez. `PaymentLeaseCoordinator` sonuçla dallanmaz (Grep: yalnız `new PaymentRequestOutcome(…)` kurar, `:545`, `:555`, `:681`, `:734`, `:829`); kiranın çıkışı `LeaseExit`'in varsayılanı `abandoned` (`:617-631`) — gönderim denemesinden önceki her dönüş gibi. Mod On'da (sahadaki bütün lisanslar) bunu `PaymentLeaseTests.Mod_On_esitlenmeme_kira_abandoned_is_taze_sonraki_tiklama_surer` kanıtlar (Adım 2): finish `abandoned`, iş taze, apply ve mesaj yok, sonraki tıklama yeni kirayla `delivered`. `CustomerSearchViewModel.cs:327` ve `StreamReportViewModel.cs:265` `Notify(outcome)` çağırır; değişiklik yok.
- **Fark 9 (5 dakika).** Anahtar `PaymentPerson.MemberKey`; ilk ve son görülme servis tekilinde bellekte (`ConcurrentDictionary<string, (long First, long Last)>`, monoton zaman damgası `TimeProvider.GetTimestamp`; süre `GetElapsedTime` ile — duvar saati NTP/elle düzeltmede sıçrar); "uzun" = ilk görülmeden bu yana **> 5 dk** (tam 5 dk kısa metin); saat `internal TimeProvider NotSyncedClock` (test sahtesi `ManualTimeProvider`). Kayıt, kişinin bakiye akışı kesin bir sonuç (mesaja çıkan) ya da soru (`Prompt`) üretince silinir; ağ belirsizliği ve öbür belirsizlikler silmez. **Ayırıp yeniden birleştirme anahtarı DEĞİŞTİRMEZ:** `PaymentPerson.KeyOf` (`OrderDeck.Core/Customers/PaymentPerson.cs:86-87`, master `7960eba3`; Görev 1'in `Single` genişletmesinden sonra birkaç satır aşağı kayar) Id'leri tekilleştirip sıralar, aynı üye kümesi aynı anahtarı üretir. Bu yüzden "art arda" tanımı gerekir: önceki 409'dan bu yana `NotSyncedStreakGap` (**30 dk**) aşıldıysa ilk görülme yeniden başlar — operatörün önerilen kurtarmadan (ayır + yeniden birleştir) sonraki ilk kısa 409'u ve saatler sonraki ilk 409 uzun metni anında göstermez. Spec "5 dakikadan uzun sürerse" der, aralığı vermez: 30 dk mühendislik varsayılanıdır (5 dk eşiğinin ve testlerin 6 dk adımlarının çok üstünde) — değer Sahip sorusu. Uzun metin servis tarafında `PaymentRequestOutcome.Message`'a konur (`PaymentInProgress` deseni, `PaymentResultPresenter.cs:41-43`), çünkü `Notify(…, PaymentRequestResult)` kişiyi bilmez.
- **Fark 5.** 409 gövdesinde satır Id'si yok; 5 dakika günlüğü istemcinin kendi üye Id'lerini (`PaymentPerson.MemberIds`; GUID'siz yerel Id'ler dahil, yalnız Id) yazar. Her 409 ayrıca `Information` düzeyinde iş/anahtar Id'siyle günlüğe girer (K2'ye göre geçici durum; `Warning` yalnız 5 dakikadan sonra).
- **Fark 22 / K7.** Havuz kipinde kesin kolun önizlemesi 200 `{balance: 0, personPool: true}`: `previewBalance <= 0` → `MarkNoBalance` bugünkü yoldan doğru kapanır; `CustomerBalancePreview` değişmez (bilinmeyen alan yok sayılır). Kilit testi Adım 13.
- **Fark 23 / §6.1-c.** 409 `client-update-required` (önizleme ve apply), 400 `member-ids-required`, 503 `balance-busy` ve 404 yeni bir sonuca eşlenmez: bugünkü genel belirsiz kollar (`BalanceUncertain`, "Bakiye doğrulanamadı", `MarkNoBalance` yok, apply'da iş anahtarıyla bekler). Kaynakları ayrı: 503 `balance-busy` ve 404 için S2 saha notu 5 "eşlenmez" der; `client-update-required` için spec §6.1-c genel `BalanceUncertain` yolunu yeterli sayar (metin değişmez); 400 `member-ids-required`'ı S2 saha notu 5 kesin sonuçtan ayrılması gerekenler arasında sayar — genel belirsiz kol bunu sağlar (`MarkNoBalance` yok, iş anahtarla bekler); Wh çapayı her zaman `memberIds`'e koyduğu için normalde gelmez. **Spec ↔ S2 notu:** S2 notu `client-update-required` ve `member-ids-required`'ın ayrılmasını ister; spec §6.1-c genel kolu yeterli sayar; ayrım `MarkNoBalance`'ın yazılmamasıyla sağlanır. Kod değişmez; Adım 13 kilitler. `client-update-required`'da güncelleme sorusu açılmaz.
- **R18.** Spec "Banka W2 açısından `BalanceUncertain` gibidir": WPF'te `OwnerId`/`Demoted` tüketicisi yok (`anla-wpf-odeme` §6.1) — kod yok; sonuç `OwnerId`/`Demoted`'i `BalanceUncertain` gibi taşır (sözleşme enum belgesinde).
- `PullOnceAsync` tetiklemesi bu görevde YOK (Görev 5; `RequestPaymentAsync`'e dokunulmaz).
- Test altyapısı: `ManualTimeProvider` paylaşılan sınıf olarak `TestHelpers`'a; `SessionOrderSyncCadenceTests.cs:41-48` ve `SessionOrderSyncHandoverTests.cs:39` içindeki özel `ManualTime` kopyalarına dokunulmaz (iç içe tür adı farklı, çakışma yok — Grep: aynı adlı sınıf yok).

---

### 4a — bayrak, sonuç, uyarı

- [ ] **Adım 1: `WhatsAppStubHandler`'a önizleme senaryosu ve açık `Problem`**

`OrderDeck.Tests/Services/PaymentRequestServiceTests.cs` — üç Edit.

(i) `OnPreviewAsync` özelliğinin ardına:

Bugün:
```csharp
        /// <summary>A9: preview cevabından önce beklenir (yarış rendezvous'u).</summary>
        public Func<Task>? OnPreviewAsync { get; set; }
```
Yeni:
```csharp
        /// <summary>A9: preview cevabından önce beklenir (yarış rendezvous'u).</summary>
        public Func<Task>? OnPreviewAsync { get; set; }

        /// <summary>W-havuz (spec 2026-10-11 §6.1-b): önizlemenin çağrı başına cevabı (parametre kaçıncı önizleme olduğu, 1'den;
        /// sayaç <c>_sync</c> altında). Dolu dönerse o cevap gider (ör. 409 <c>customer-not-synced</c>, havuz kipinin
        /// <c>personPool</c>'lu 200'ü); null → normal yol (<see cref="PreviewBalance"/> / <see cref="RowBalances"/>). Fonksiyon
        /// fırlatırsa istek ağ hatasıyla düşer.</summary>
        public Func<int, HttpResponseMessage?>? PreviewResponse { get; set; }
        private int _previewCount;
```

(ii) Önizleme kolunda bakiye okumasının önüne (Görev 3'ün `PreviewMemberIds` kaydından ve `OnPreviewAsync`'ten SONRA — ikisi de bu satırların üstünde kalır):

Bugün:
```csharp
                decimal balance;
                lock (_sync)
                    balance = RowBalances is { } rows ? rows.GetValueOrDefault(CanonicalOf(previewed)) : PreviewBalance;
```
Yeni:
```csharp
                int onizlemeSira;
                lock (_sync) onizlemeSira = ++_previewCount;
                // W-havuz: senaryolu cevap (409 customer-not-synced, client-update-required, havuzun 200'ü, ağ hatası).
                if (PreviewResponse?.Invoke(onizlemeSira) is { } scriptedPreview) return scriptedPreview;
                decimal balance;
                lock (_sync)
                    balance = RowBalances is { } rows ? rows.GetValueOrDefault(CanonicalOf(previewed)) : PreviewBalance;
```

(iii) `Problem` testlerden kurulabilsin (iç içe türün `private` üyesi kapsayan sınıfa görünmez):

Bugün:
```csharp
        private static HttpResponseMessage Problem(
            string title, HttpStatusCode status = (HttpStatusCode)409) => new(status)
```
Yeni:
```csharp
        /// <summary>Sunucunun problem+json reddi (<c>title</c> = <c>ValidationException.Code</c>). Varsayılan 409.</summary>
        public static HttpResponseMessage Problem(
            string title, HttpStatusCode status = (HttpStatusCode)409) => new(status)
```

- [ ] **Adım 2: Başarısız servis testlerini yaz**

`OrderDeck.Tests/Services/PaymentRequestServiceTests.NotSynced.cs` (yeni):

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── W-havuz: 409 customer-not-synced → BalanceNotSynced (spec 2026-10-11 §6.1-b, K2, §8.4-2) ─────────────────────
//
// Havuz kipinde sunucu, kişinin bir üyesini henüz tanımıyorsa (birleştirme ya da ayırma sunucuya ulaşmadı) önizlemeye ve
// yeni anahtarlı apply'a 409 customer-not-synced döner. Bu "bakiye yok" DEĞİLDİR: MarkNoBalance yazılırsa iş kesinleşir
// ve sunucu eşitlendikten sonra da bakiye bir daha sorulmaz (K2'nin önlediği kalıcı kayıp). Mesaj gitmez, iş taze ya da
// anahtarlı-belirsiz kalır; sonraki tıklama kaldığı yerden sürdürür. Operatöre "Henüz eşitlenmedi" uyarısı; aynı kişide 5
// dakikadan uzun sürerse ayrı metin ve günlüğe kişinin satır Id'leri. Öbür yanıtlar bugünkü genel belirsiz kolda kalır:
// client-update-required (spec §6.1-c: genel BalanceUncertain yolu yeterli), member-ids-required (S2 saha notu 5 kesin
// sonuçtan ayrılmasını ister — genel kol MarkNoBalance yazmaz), balance-busy (S2 saha notu 5: eşlenmez).
public partial class PaymentRequestServiceTests
{
    [Fact] // §6.1-b (K2): önizlemenin customer-not-synced'i ayrı sonuç — MarkNoBalance yok, iş taze, mesaj gitmez
    public async Task Onizleme_customer_not_synced_BalanceNotSynced_is_taze_mesaj_gitmez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            outcome.Message.Should().BeNull("ilk görülme: kısa metni sunum katmanı kurar");
            outcome.OwnerId.Should().Be(b.Id, "banka W2 açısından BalanceUncertain gibi: sahip satırı taşınır");
            handler.PreviewCustomerIds.Should().ContainSingle("önizleme soruldu ve 409 aldı");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.Created, "MarkNoBalance yazılmadı — iş taze kaldı");
            job.ApplyKey.Should().BeNull("anahtar yazılmadı");
            handler.AppliedBalanceBodies.Should().BeEmpty();
            handler.SentBodies.Should().BeEmpty();
            _launcher.LaunchedUrls.Should().BeEmpty("mesaj gönderilmedi");

            // Sunucu eşitlendi: aynı iş bakiyeyi düşer, mesaj gider ("bakiye yok" diye kesinleşmemişti).
            handler.PreviewResponse = null;
            var sonra = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            sonra.Result.Should().Be(PaymentRequestResult.Opened);
            handler.AppliedBalanceBodies.Should().ContainSingle();
            db.Jobs.Get(job.Id)!.State.Should().Be(PaymentJobState.Applied);
            _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("150%2C00", "250 − 100");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b (K2): apply'ın customer-not-synced'i — iş anahtarıyla belirsiz bekler, sonraki tıklama aynı anahtarla oynatır
    public async Task Apply_customer_not_synced_is_anahtarli_belirsiz_kalir_sonraki_tiklama_ayni_anahtarla_oynatir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            // İlk üç apply 409: ilk tıklamanın iki denemesi (SettleAsync yolu) + ikinci tıklamanın tek oynatması (belirsiz işin
            // 3. adımı). Dördüncü apply (üçüncü tıklama) başarılı.
            handler.ApplyResponse = n => n <= 3 ? WhatsAppStubHandler.Problem("customer-not-synced") : null;

            var ilk = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            ilk.Result.Should().Be(PaymentRequestResult.BalanceNotSynced, "SettleAsync yolu bayrağı taşır");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.ApplyUncertain, "anahtar diskte, sonuç bilinmiyor — MarkNoBalance yazılmadı");
            job.ApplyKey.Should().NotBeNull();
            handler.AppliedBalanceBodies.Should().HaveCount(2, "K3: bir deneme + bir anında tekrar, ikisi de 409");
            handler.AppliedBalanceBodies.Select(AppliedKey).Should().OnlyContain(k => k == job.ApplyKey!.Value,
                "anında tekrar aynı anahtarla");
            _launcher.LaunchedUrls.Should().BeEmpty("mesaj gönderilmedi");

            // İkinci tıklama: belirsiz işin oynatması (3. adım, ReplayOnceMoreIfStaleAsync) yine 409 — stale değil, tek apply.
            var ikinci = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            ikinci.Result.Should().Be(PaymentRequestResult.BalanceNotSynced,
                "belirsiz işin oynatması da bayrağı taşır — 'Bakiye doğrulanamadı' değil");
            handler.AppliedBalanceBodies.Should().HaveCount(3, "belirsiz işin tek oynatması — stale değil, anında tekrar yok");
            AppliedKey(handler.AppliedBalanceBodies[2]).Should().Be(job.ApplyKey!.Value, "oynatma aynı anahtarla");
            db.Jobs.Get(job.Id)!.State.Should().Be(PaymentJobState.ApplyUncertain, "MarkNoBalance yine yazılmadı");
            _launcher.LaunchedUrls.Should().BeEmpty("mesaj yine gönderilmedi");

            // Üçüncü tıklama: sunucu eşitlendi, aynı anahtarın oynatması düşer.
            var ucuncu = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            ucuncu.Result.Should().Be(PaymentRequestResult.Opened);
            handler.AppliedBalanceBodies.Should().HaveCount(4);
            AppliedKey(handler.AppliedBalanceBodies[3]).Should().Be(job.ApplyKey!.Value,
                "sonraki tıklama aynı anahtarla oynatır — çift düşüm yok");
            var son = db.Jobs.Get(job.Id)!;
            son.State.Should().Be(PaymentJobState.Applied);
            son.AppliedAmount.Should().Be(100m);
            _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("150%2C00", "250 − 100");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: miras işin oynatması (1. adım) da customer-not-synced alırsa ayrı sonuç — miras iş anahtarıyla bekler
    public async Task Miras_isin_oynatmasi_customer_not_synced_BalanceNotSynced()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (miras, anahtar) = MirasIs(db, a.Id);
            var (sut, handler) = KisiSut(db);
            handler.ApplyProblemTitle = "customer-not-synced";

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            db.Jobs.Get(miras.Id)!.State.Should().Be(PaymentJobState.ApplyUncertain, "miras iş anahtarıyla bekler");
            handler.AppliedBalanceBodies.Should().ContainSingle("miras işin tek oynatması — stale değil, anında tekrar yok");
            AppliedKey(handler.AppliedBalanceBodies[0]).Should().Be(anahtar);
            handler.PreviewCustomerIds.Should().BeEmpty("akış miras işte durdu — taze işin önizlemesi sorulmadı");
            _launcher.LaunchedUrls.Should().BeEmpty();
            Isler(db, "session:s1").Should().ContainSingle().Which.ApplyKey.Should().BeNull("taze iş anahtarsız bekler");
        }
        finally { Drop(db); }
    }
}
```

`OrderDeck.Tests/Services/PaymentResultPresenterTests.cs` — sınıfın sonuna:

Bugün:
```csharp
        message.Should().StartWith(
            "Bu kişiye bu yayın için 2 istek gitmiş (50,00 TL, 150,00 TL (gitmiş olabilir)); tek istekte birleştirilsin mi?");
    }
}
```
Yeni:
```csharp
        message.Should().StartWith(
            "Bu kişiye bu yayın için 2 istek gitmiş (50,00 TL, 150,00 TL (gitmiş olabilir)); tek istekte birleştirilsin mi?");
    }

    [Fact] // §6.1-b (K2): "Henüz eşitlenmedi" uyarısı spec'in metniyle birebir, Uyarı önemiyle (hata değil)
    public void Esitlenmedi_uyarisi_baslik_ve_metin_birebir()
    {
        const string metin =
            "Kişinin hesapları sunucuyla henüz eşitlenmedi; mesaj gönderilmedi. Birazdan tekrar deneyin — bakiye kaybolmaz, çift düşüm olmaz.";
        var dialogs = new OrderDeck.Tests.Fakes.FakeDialogService();

        PaymentResultPresenter.Notify(dialogs, new PaymentRequestOutcome(PaymentRequestResult.BalanceNotSynced));

        dialogs.Shown.Should().ContainSingle().Which.Should().Be(("Henüz eşitlenmedi", metin, DialogSeverity.Warning));
        PaymentResultPresenter.NotSyncedTitle.Should().Be("Henüz eşitlenmedi");
        PaymentResultPresenter.NotSyncedText.Should().Be(metin);
        dialogs.ErrorsShown.Should().BeEmpty();
        dialogs.InfosShown.Should().BeEmpty();
    }

    [Fact] // §6.1-b: sonuç tek başına bildirilirse (kişi bilinmiyor) kısa metin
    public void Esitlenmedi_sonucu_tek_basina_kisa_metin()
    {
        var dialogs = new OrderDeck.Tests.Fakes.FakeDialogService();

        PaymentResultPresenter.Notify(dialogs, PaymentRequestResult.BalanceNotSynced);

        dialogs.Shown.Should().ContainSingle().Which.Should().Be(
            (PaymentResultPresenter.NotSyncedTitle, PaymentResultPresenter.NotSyncedText, DialogSeverity.Warning));
    }
}
```

`OrderDeck.Tests/Sync/PaymentLeaseTests.cs` — mod On'da (sahadaki bütün lisanslar 26.10'dan beri; havuz kipi bu lisanslarda açılacak) eşitlenmemenin kirayla etkileşimi: kira begin'de alınır, önizleme 409 verir, mesaj denemesi olmadan çıkılır → `LeaseExit`'in varsayılanı (`PaymentRequestService.cs:619`, `PaymentRequestStates.Abandoned`) ile finish. `Cloud_API_isleniyor_SendPending_finish_unconfirmed`'in ardına (`:326`'dan sonra).

Bugün:
```csharp
        finish.GetProperty("netAmount").GetDecimal().Should().Be(120m + 50m);
        pc.Launcher.LaunchedUrls.Should().BeEmpty("bugünkü davranış: wa.me açılmaz");
    }

    [Fact]
    public async Task Tamami_bakiyeden_odenen_istegin_finishi_fullyFromBalance_ve_dusumu_tasir()
```
Yeni:
```csharp
        finish.GetProperty("netAmount").GetDecimal().Should().Be(120m + 50m);
        pc.Launcher.LaunchedUrls.Should().BeEmpty("bugünkü davranış: wa.me açılmaz");
    }

    [Fact] // W-havuz §6.1-b (K2), mod On: eşitlenmeme kirayı abandoned ile kapatır; iş taze, mesaj yok; sonraki tıklama yeni kirayla sürer
    public async Task Mod_On_esitlenmeme_kira_abandoned_is_taze_sonraki_tiklama_surer()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, $"alici-{Guid.NewGuid():N}", 120m);
        _server.BalancePreview = 50m;
        _server.EnqueueProblem(FakeSalesServer.BalancePreviewEndpoint, 409, "customer-not-synced");
        var scope = Scope(live);

        (await RequestAsync(pc, buyer.Customer, scope)).Result.Should().Be(PaymentRequestResult.BalanceNotSynced);

        FinishOutcomes().Should().Equal(new[] { "abandoned" }, "mesaj gitmedi: kira gönderim denemesinden önce kapanır");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        _server.Calls(FakeSalesServer.BalanceApplyEndpoint).Should().BeEmpty();
        var job = pc.Jobs.GetPersonScopeJobs([buyer.Customer.Id], scope).Jobs.Should().ContainSingle().Subject;
        job.State.Should().Be(PaymentJobState.Created, "MarkNoBalance yazılmadı");
        job.ApplyKey.Should().BeNull();

        (await RequestAsync(pc, buyer.Customer, scope)).Result.Should().Be(PaymentRequestResult.Opened);
        FinishOutcomes().Should().Equal("abandoned", "delivered");
        _server.Calls(FakeSalesServer.BalanceApplyEndpoint).Should().ContainSingle();
    }

    [Fact]
    public async Task Tamami_bakiyeden_odenen_istegin_finishi_fullyFromBalance_ve_dusumu_tasir()
```

`PaymentJobState` `OrderDeck.Core.Storage.Repositories`'te (dosyanın `using`'i `:11` zaten var); `EnqueueProblem(string, int, string)` `FakeSalesServer.cs:329`, betik yalnız ilk önizlemeyi karşılar (`HandleBalanceAsync` kuyruktan bir yanıt çeker), ikinci tıklamanın önizlemesi `BalancePreview` (50) döner. Kullanıcı adı yalnız bu testte; Cloud API kapalı (wa.me yolu → `Opened`).

- [ ] **Adım 3: Koştur — FAIL bekle**

Run (PowerShell): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Onizleme_customer_not_synced|FullyQualifiedName~PaymentRequestServiceTests.Apply_customer_not_synced|FullyQualifiedName~PaymentRequestServiceTests.Miras_isin_oynatmasi_customer_not_synced|FullyQualifiedName~PaymentResultPresenterTests.Esitlenmedi|FullyQualifiedName~PaymentLeaseTests.Mod_On_esitlenmeme"`
Expected: FAIL — derleme hatası: `PaymentRequestResult.BalanceNotSynced` yok (CS0117; servis testleri ve `PaymentLeaseTests.Mod_On_esitlenmeme_…`), `PaymentResultPresenter.NotSyncedTitle` / `NotSyncedText` yok (CS0117). (Bugünkü kodla derlenseydi üç servis testi ve mod On testi `BalanceUncertain` alırdı: iki 409 de genel kollara düşüyor.)

- [ ] **Adım 4: Servis — sonuç, bayrak, iki `catch`, üç taşıma yeri, kapı**

`OrderDeck.App/Services/PaymentRequestService.cs` — on Edit.

(i) Enum'un sonu:

Bugün:
```csharp
    /// tercihi).</summary>
    Blocked,
}
```
Yeni:
```csharp
    /// tercihi).</summary>
    Blocked,

    /// <summary>Spec 2026-10-11 §6.1-b (K2): sunucu kişinin bir üyesini henüz tanımıyor (409 <c>customer-not-synced</c>; yalnız
    /// havuz kipi — birleştirme ya da ayırma sunucuya henüz ulaşmadı). Mesaj GÖNDERİLMEDİ, <c>MarkNoBalance</c> yazılmadı: iş
    /// taze ya da anahtarlı-belirsiz; sonraki tıklama kaldığı yerden sürdürür, çift düşüm olmaz. Banka W2 açısından
    /// <see cref="BalanceUncertain"/> gibidir (beklenti yazılmaz). Yeni üye EN SONDA: var olan üyelerin değerleri değişmez.</summary>
    BalanceNotSynced,
}
```

(ii) Kod sabiti, `PersonGateTimeout`'un ardına:

Bugün:
```csharp
    internal TimeSpan PersonGateTimeout { get; set; } = TimeSpan.FromSeconds(20);
```
Yeni:
```csharp
    internal TimeSpan PersonGateTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Spec 2026-10-11 §6.1-b: havuz kipinin "kişinin bir üyesi sunucuda henüz yok" reddinin problem başlığı
    /// (<c>ValidationException.Code</c>; önizlemede ve yeni anahtarlı apply'da 409).</summary>
    private const string CustomerNotSyncedCode = "customer-not-synced";
```

(iii) `SendCoreAsync`'in bakiye kapısı:

Bugün:
```csharp
            if (outcome.Uncertain)
                return new(PaymentRequestResult.BalanceUncertain, null, ownerId, demoted);
            deliveryJob = outcome.Job;
```
Yeni:
```csharp
            // Spec 2026-10-11 §6.1-b (K2): sunucu kişinin bir üyesini henüz tanımıyor (409 customer-not-synced) — mesaj gitmez,
            // MarkNoBalance yazılmadı; operatöre ayrı uyarı ("Henüz eşitlenmedi"). Banka W2 açısından BalanceUncertain gibidir.
            if (outcome.Uncertain)
                return new(outcome.NotSynced ? PaymentRequestResult.BalanceNotSynced : PaymentRequestResult.BalanceUncertain,
                    null, ownerId, demoted);
            deliveryJob = outcome.Job;
```

(iv) `BalanceOutcome`'a bayrak:

Bugün:
```csharp
        public bool ScopeDeductionAdopted { get; init; }
    }
```
Yeni:
```csharp
        public bool ScopeDeductionAdopted { get; init; }

        /// <summary>Spec 2026-10-11 §6.1-b (K2): belirsizliğin (<see cref="Uncertain"/>) nedeni 409 <c>customer-not-synced</c> —
        /// sunucu kişinin bir üyesini henüz tanımıyor (önizleme ya da apply). Sonuç
        /// <see cref="PaymentRequestResult.BalanceNotSynced"/>; <c>MarkNoBalance</c> yazılmadı, iş taze ya da
        /// anahtarlı-belirsiz.</summary>
        public bool NotSynced { get; init; }
    }
```

(v) `ApplyAttempt`'e bayrak:

Bugün:
```csharp
    /// çağıran #506'nın birleştirme sorusunu sorar.</param>
    private sealed record ApplyAttempt(
        PaymentJob Job, bool Stale = false, bool Adopted = false, ScopeHasDeductionException? LegacyScopeDeduction = null);
```
Yeni:
```csharp
    /// çağıran #506'nın birleştirme sorusunu sorar.</param>
    /// <param name="NotSynced">Spec 2026-10-11 §6.1-b (K2): 409 <c>customer-not-synced</c> — sunucu kişinin bir üyesini henüz
    /// tanımıyor, hiçbir şey yazmadı. İş anahtarıyla <c>apply_uncertain</c> kalır (sonraki tıklama aynı anahtarla oynatır);
    /// <c>MarkNoBalance</c> YOK. Belirsiz dönüş bayrağı <see cref="BalanceOutcome.NotSynced"/>'a taşır.</param>
    private sealed record ApplyAttempt(
        PaymentJob Job, bool Stale = false, bool Adopted = false, ScopeHasDeductionException? LegacyScopeDeduction = null,
        bool NotSynced = false);
```

(vi) Miras işin belirsiz dönüşü (`SettleScopeAsync` 1. adım):

Bugün:
```csharp
                if (attempt.Job.State == PaymentJobState.ApplyUncertain)
                    return new(true, 0m, attempt.Job);
                resolved.Add(attempt.Job);
```
Yeni:
```csharp
                if (attempt.Job.State == PaymentJobState.ApplyUncertain)
                    return new(true, 0m, attempt.Job) { NotSynced = attempt.NotSynced };
                resolved.Add(attempt.Job);
```

(vii) Belirsiz işin dönüşü (3. adım):

Bugün:
```csharp
            if (attempt.Adopted)
                remoteConfirmed = true;
            else if (job.State == PaymentJobState.ApplyUncertain)
                return new(true, 0m, job);
```
Yeni:
```csharp
            if (attempt.Adopted)
                remoteConfirmed = true;
            else if (job.State == PaymentJobState.ApplyUncertain)
                return new(true, 0m, job) { NotSynced = attempt.NotSynced };
```

(viii) Önizlemenin genel `catch`'inin ÖNÜNE (Görev 3 sonrası `try` bloğu `GetBalancePreviewAsync(licenseId, wpfCustomerId, MemberIdsOf(…), ct)` çağırır; `catch`'ler değişmeden durur):

Bugün:
```csharp
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "Bakiye önizlemesi alınamadı — mesaj engellendi (job={JobId})", job.Id);
```
Yeni:
```csharp
        catch (ValidationException ex) when (ex.Code == CustomerNotSyncedCode)
        {
            // Spec 2026-10-11 §6.1-b (K2): sunucu kişinin bir üyesini henüz tanımıyor. Kesin "bakiye yok" DEĞİL: MarkNoBalance
            // yazılırsa iş kesinleşir ve eşitlemeden sonra bakiye bir daha sorulmaz. Anahtar yazılmadı, iş created kalır —
            // sonraki tıklama temiz dener.
            _log?.LogInformation(
                "Bakiye önizlemesi: kişinin bir üyesi sunucuya henüz ulaşmadı (customer-not-synced) — mesaj engellendi, iş taze "
                + "kaldı (job={JobId})", job.Id);
            return new(true, 0m, job) { NotSynced = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "Bakiye önizlemesi alınamadı — mesaj engellendi (job={JobId})", job.Id);
```

(ix) `SettleAsync`'in sonu:

Bugün:
```csharp
        if (attempt.Adopted)
            return new(true, 0m, attempt.Job) { ScopeDeductionAdopted = true };
        return ClassifySettledJob(attempt.Job, entryKey, entryRevision, requestedTotal);
```
Yeni:
```csharp
        if (attempt.Adopted)
            return new(true, 0m, attempt.Job) { ScopeDeductionAdopted = true };
        // Spec 2026-10-11 §6.1-b: son denemenin customer-not-synced'i belirsiz sonuca taşınır (BalanceNotSynced).
        var classified = ClassifySettledJob(attempt.Job, entryKey, entryRevision, requestedTotal);
        return attempt.NotSynced && classified.Uncertain ? classified with { NotSynced = true } : classified;
```

(x) `ReplayAsync`: `stale` kolu ile `ScopeHasDeductionException` kolu arasına:

Bugün:
```csharp
            return new(_jobs.Get(job.Id)!, Stale: true);
        }
        catch (ScopeHasDeductionException ex)
```
Yeni:
```csharp
            return new(_jobs.Get(job.Id)!, Stale: true);
        }
        catch (ValidationException ex) when (ex.Code == CustomerNotSyncedCode)
        {
            // Spec 2026-10-11 §6.1-b (K2): yeni anahtar sunucuda HİÇ yazılmadı (kayıtlı anahtarın oynatması üyelik denetiminden
            // önce çalışır, bu 409'u almaz). "Bakiye yok" DEĞİL — MarkNoBalance yazılmaz; iş anahtarıyla apply_uncertain kalır,
            // sonraki tıklama aynı anahtarla oynatır.
            _log?.LogInformation(
                "Bakiye apply: kişinin bir üyesi sunucuya henüz ulaşmadı (customer-not-synced) — iş anahtarıyla belirsiz kaldı "
                + "(job={JobId}, key={Key})", job.Id, key);
            return new(_jobs.Get(job.Id)!, NotSynced: true);
        }
        catch (ScopeHasDeductionException ex)
```

Notlar: `ValidationException` dosyada zaten kullanılıyor (`using OrderDeck.Licensing.Api;`). Filtreli `catch`'ler sıradan bağımsız derlenir; yeni kol `OperationCanceledException` ve genel `Exception` kollarının önünde. `ReplayOnceMoreIfStaleAsync` (`:1640-1648`; Görev 3 imzasına `person` ekledi) bu görevde değişmez: `NotSynced` `Stale` değildir, miras ve belirsiz işte tek apply.

- [ ] **Adım 5: Sunum — sabitler ve iki `Notify` kolu**

`OrderDeck.App/Services/PaymentResultPresenter.cs` — dört Edit.

(i) Sınıfın başına:

Bugün:
```csharp
public static class PaymentResultPresenter
{
    /// <summary>
    /// Sonucun tamamıyla bildirim (spec 2026-10-13 §6.4; W3-07): <see cref="PaymentRequestResult.Blocked"/> açılır pencere
```
Yeni:
```csharp
public static class PaymentResultPresenter
{
    /// <summary>Spec 2026-10-11 §6.1-b (K2): <see cref="PaymentRequestResult.BalanceNotSynced"/> uyarısının başlığı.</summary>
    public const string NotSyncedTitle = "Henüz eşitlenmedi";

    /// <summary>Spec §6.1-b: sunucu kişinin bir hesabını henüz tanımıyor (birleştirmeden hemen sonra) — kısa metin.</summary>
    public const string NotSyncedText =
        "Kişinin hesapları sunucuyla henüz eşitlenmedi; mesaj gönderilmedi. Birazdan tekrar deneyin — bakiye kaybolmaz, çift düşüm olmaz.";

    /// <summary>
    /// Sonucun tamamıyla bildirim (spec 2026-10-13 §6.4; W3-07): <see cref="PaymentRequestResult.Blocked"/> açılır pencere
```

(ii) `Notify(outcome)` belgesi:

Bugün:
```csharp
    /// (<see cref="PaymentRequestOutcome.Message"/>) gösterir; öbür sonuçlar <see cref="Notify(IDialogService, PaymentRequestResult)"/>
    /// ile bugünkü gibi.
```
Yeni:
```csharp
    /// (<see cref="PaymentRequestOutcome.Message"/>) gösterir. <see cref="PaymentRequestResult.BalanceNotSynced"/> (spec 2026-10-11
    /// §6.1-b) "Henüz eşitlenmedi" uyarısını servisin metniyle (<see cref="PaymentRequestOutcome.Message"/>; aynı kişide 5
    /// dakikadan uzun sürüyorsa), yoksa kısa metinle gösterir. Öbür sonuçlar
    /// <see cref="Notify(IDialogService, PaymentRequestResult)"/> ile bugünkü gibi.
```

(iii) `Notify(outcome)` kolu:

Bugün:
```csharp
            case PaymentRequestResult.PaymentInProgress when !string.IsNullOrWhiteSpace(outcome.Message):
                dialogs.ShowInfo(outcome.Message);
                return;
            default:
```
Yeni:
```csharp
            case PaymentRequestResult.PaymentInProgress when !string.IsNullOrWhiteSpace(outcome.Message):
                dialogs.ShowInfo(outcome.Message);
                return;
            case PaymentRequestResult.BalanceNotSynced:
                dialogs.Show(outcome.Message ?? NotSyncedText, NotSyncedTitle, DialogSeverity.Warning);
                return;
            default:
```

(iv) `Notify(result)`: `BalanceUncertain` kolunun ardına:

Bugün:
```csharp
                    "Bakiye doğrulanamadı",
                    DialogSeverity.Warning);
                break;
```
Yeni:
```csharp
                    "Bakiye doğrulanamadı",
                    DialogSeverity.Warning);
                break;

            case PaymentRequestResult.BalanceNotSynced:
                // Spec 2026-10-11 §6.1-b (K2): sunucu kişinin bir hesabını henüz tanımıyor; mesaj gönderilmedi, iş taze ya da
                // anahtarlı-belirsiz. BalanceUncertain gibi UYARI: kayıp yok, operatör biraz sonra tekrar dener. Kişi burada
                // bilinmez: 5 dakika metni yalnız Notify(dialogs, outcome) yolundan (Message) gelir.
                dialogs.Show(NotSyncedText, NotSyncedTitle, DialogSeverity.Warning);
                break;
```

- [ ] **Adım 6: Koştur — PASS, derleme, regresyon**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Onizleme_customer_not_synced|FullyQualifiedName~PaymentRequestServiceTests.Apply_customer_not_synced|FullyQualifiedName~PaymentRequestServiceTests.Miras_isin_oynatmasi_customer_not_synced|FullyQualifiedName~PaymentResultPresenterTests.Esitlenmedi|FullyQualifiedName~PaymentLeaseTests.Mod_On_esitlenmeme"`
Expected: PASS — 6 test (mod On testi gerçek kira sarmalıyla: ilk finish `abandoned`, ikinci `delivered`).

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Expected: 0 uyarı, 0 hata (`NotSynced` bayrakları okunuyor; `CorrectedPaymentRequests`'in `switch` ifadesi `_` koluyla yeni üyeyi derler — 4b eşler).

Run (R-Ödeme, değiştirmeden): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CorrectedPaymentRequestsTests|FullyQualifiedName~PaymentPersonViewModelTests"`
Expected: hepsi PASS. Satır kipinde sunucu `customer-not-synced` üretmez; stub'ın `PreviewResponse`'u varsayılan null (bugünkü yol), `Problem`'in yalnız erişimi değişti.

- [ ] **Adım 7: Commit (4a)**

```bash
git add OrderDeck.App/Services/PaymentRequestService.cs \
  OrderDeck.App/Services/PaymentResultPresenter.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.NotSynced.cs \
  OrderDeck.Tests/Services/PaymentResultPresenterTests.cs \
  OrderDeck.Tests/Sync/PaymentLeaseTests.cs
git commit -F - <<'MSG'
feat(balance): customer-not-synced'i BalanceNotSynced olarak ayır (MarkNoBalance yok)

Havuz kipinde sunucu, kişinin bir üyesini henüz tanımıyorsa (birleştirme
ya da ayırma sunucuya ulaşmadı) önizlemeye ve yeni anahtarlı apply'a 409
customer-not-synced döner. Bugün ikisi de genel belirsiz kola düşüp
"Bakiye doğrulanamadı" gösteriyordu. Önizleme ve ReplayAsync bu kodu
genel koldan önce yakalar; bayrak ApplyAttempt ve BalanceOutcome ile
SendCoreAsync'e taşınır (miras işin oynatması, belirsiz işin oynatması ve
SettleAsync yolları dahil) ve sonuç yeni BalanceNotSynced olur: "Henüz
eşitlenmedi" uyarısı. MarkNoBalance yazılmaz; iş taze ya da
anahtarlı-belirsiz kalır, sonraki tıklama aynı anahtarla sürdürür (ortak
bakiye §6.1-b, K2). Satır kipinde sunucu bu kodu üretmez; davranış
değişmez.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

### 4b — 5 dakika kuralı, toplu gönderim, değişmeyen yanıtların kilidi

- [ ] **Adım 8: Test altyapısı — paylaşılan saat ve `SutOn`'a günlük**

`OrderDeck.Tests/TestHelpers/ManualTimeProvider.cs` (yeni):

```csharp
namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Elle ilerletilen zaman (<see cref="TimeProvider"/>): zaman damgası tik cinsinden, başlangıç Unix çağından bir gün sonra.
/// Ödeme servisinin 5 dakika kuralı (spec 2026-10-11 §6.1-b, <c>PaymentRequestService.NotSyncedClock</c>) gibi süre
/// eşiklerini uykusuz, belirlenimli sınamak için. Emsal <c>Sync/SessionOrderSyncCadenceTests.ManualTime</c> (oradaki özel
/// kopyalar yerinde kalır).
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks = TimeSpan.FromDays(1).Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
```

`OrderDeck.Tests/Services/PaymentRequestServiceTests.cs` — `SutOn`:

Bugün:
```csharp
    /// <param name="offRecords">Mod Off'taki kirasız ödeme kaydı (W3-06); verilmezse kayıt yok.</param>
    private PaymentRequestService SutOn(
        WhatsAppStubHandler handler, SettingsStore store, FakeUrlLauncher launcher,
        ICurrentLicenseProvider? licenseProvider = null, IPaymentJobStore? jobs = null,
        CustomerRepository? customers = null, CustomerBusySet? busy = null,
        IShippingSettingsProvider? shipping = null, PaymentOffRecordWriter? offRecords = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        return new PaymentRequestService(store, new WhatsAppMessageBuilder(), launcher,
            api, licenseProvider ?? new FixedLicenseProvider(), jobs ?? _jobs, shipping ?? LocalShipping.Of(store),
            log: null, customers: customers, busy: busy, offRecords: offRecords)
```
Yeni:
```csharp
    /// <param name="offRecords">Mod Off'taki kirasız ödeme kaydı (W3-06); verilmezse kayıt yok.</param>
    /// <param name="log">Servisin günlüğü (W-havuz: "günlükte yalnız Id" denetimi); verilmezse günlük yok.</param>
    private PaymentRequestService SutOn(
        WhatsAppStubHandler handler, SettingsStore store, FakeUrlLauncher launcher,
        ICurrentLicenseProvider? licenseProvider = null, IPaymentJobStore? jobs = null,
        CustomerRepository? customers = null, CustomerBusySet? busy = null,
        IShippingSettingsProvider? shipping = null, PaymentOffRecordWriter? offRecords = null,
        Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? log = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        return new PaymentRequestService(store, new WhatsAppMessageBuilder(), launcher,
            api, licenseProvider ?? new FixedLicenseProvider(), jobs ?? _jobs, shipping ?? LocalShipping.Of(store),
            log: log, customers: customers, busy: busy, offRecords: offRecords)
```

Var olan çağıranlar (`MakeCloudSut` `:424`, `IkinciSut` `:458`, `.OffRecords.cs:60`) konumsal/adlandırılmış argümanlarıyla aynen derlenir.

- [ ] **Adım 9: Başarısız testleri yaz (5 dakika kuralı, uzun metin, toplu gönderim)**

`OrderDeck.Tests/Services/PaymentRequestServiceTests.NotSynced.cs` — 4a'nın son testinin ardına (dosyanın son `}`'inden önce):

Bugün:
```csharp
            Isler(db, "session:s1").Should().ContainSingle().Which.ApplyKey.Should().BeNull("taze iş anahtarsız bekler");
        }
        finally { Drop(db); }
    }
}
```
Yeni:
```csharp
            Isler(db, "session:s1").Should().ContainSingle().Which.ApplyKey.Should().BeNull("taze iş anahtarsız bekler");
        }
        finally { Drop(db); }
    }

    /// <summary>Önizlemesi hep 409 <c>customer-not-synced</c> dönen sahte sunucu, günlük toplayıcı ve elle ilerletilen saat
    /// üstünde bir servis (5 dakika kuralı, spec §6.1-b). Testler <c>Handler.PreviewResponse</c>'u değiştirebilir.</summary>
    private (PaymentRequestService Sut, WhatsAppStubHandler Handler, CapturingLogger<PaymentRequestService> Log,
        ManualTimeProvider Saat) EsitlenmemisSut(SyncedDb db)
    {
        var handler = new WhatsAppStubHandler
        {
            PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced"),
        };
        var log = new CapturingLogger<PaymentRequestService>();
        var saat = new ManualTimeProvider();
        var sut = SutOn(handler, _store, _launcher, jobs: db.Jobs, customers: db.Customers, busy: db.Busy, log: log);
        sut.NotSyncedClock = saat;
        return (sut, handler, log, saat);
    }

    [Fact] // §6.1-b: 5 dakikadan UZUN süren eşitlenmeme ayrı metin + günlükte kişinin satır Id'leri (ad, telefon yok)
    public async Task Bes_dakika_dolmadan_kisa_metin_dolunca_uzun_metin_ve_gunluge_satir_idleri()
    {
        var db = FileDb();
        try
        {
            var adA = $"kisi-{Guid.NewGuid():N}";
            var adB = $"kisi-{Guid.NewGuid():N}";
            var a = Uye(db, adA, phone: false);
            var b = Uye(db, adB);
            Kisi(db, a, b);
            var (sut, _, log, saat) = EsitlenmemisSut(db);
            Task<PaymentRequestOutcome> Iste() => sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            var ilk = await Iste();
            ilk.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            ilk.Message.Should().BeNull("ilk görülme");

            saat.Advance(TimeSpan.FromMinutes(5));
            var tamBes = await Iste();
            tamBes.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            tamBes.Message.Should().BeNull("tam 5 dakika henüz 'uzun' değil (> 5 dk)");
            log.Warnings.Should().NotContain(w => w.Message.Contains("5 dakikadan uzun"));

            saat.Advance(TimeSpan.FromSeconds(1));
            var uzun = await Iste();
            uzun.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            uzun.Message.Should().Be(PaymentResultPresenter.NotSyncedPersistentText);

            var uyari = log.Warnings.Should().ContainSingle(w => w.Message.Contains("5 dakikadan uzun")).Subject;
            uyari.Full.Should().Contain(a.Id).And.Contain(b.Id, "günlüğe kişinin satır Id'leri yazılır");
            log.Entries.Should().NotContain(e => e.Full.Contains(adA) || e.Full.Contains(adB) || e.Full.Contains(b.Phone!),
                "günlükte ad, kullanıcı adı ve telefon yok — yalnız Id");
            Isler(db, "session:s1").Should().ContainSingle().Which.State.Should().Be(PaymentJobState.Created,
                "üç tıklamada da MarkNoBalance yazılmadı");
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: ilk görülme anı KİŞİ başına — bir kişinin uzun beklemesi öbür kişinin metnini değiştirmez
    public async Task Esitlenmeme_sayaci_kisi_basina()
    {
        var db = FileDb();
        try
        {
            var a1 = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var a2 = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a1, a2);
            var b1 = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b2 = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, b1, b2);
            var (sut, _, _, saat) = EsitlenmemisSut(db);

            (await sut.RequestPaymentAsync(a2, _ => 250m, T, "session:s1")).Message.Should().BeNull();
            saat.Advance(TimeSpan.FromMinutes(6));
            (await sut.RequestPaymentAsync(a2, _ => 250m, T, "session:s1")).Message.Should()
                .Be(PaymentResultPresenter.NotSyncedPersistentText, "A kişisi 6 dakikadır eşitlenmedi");

            var bSonuc = await sut.RequestPaymentAsync(b2, _ => 250m, T, "session:s1");

            bSonuc.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            bSonuc.Message.Should().BeNull("B kişisi ilk kez görülüyor — sayaç kişi başına");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: kesin sonuç (sunucu kişiyi tanıdı) sayacı sıfırlar — sonraki eşitlenmeme yeniden kısa metinle başlar
    public async Task Kesin_sonuctan_sonra_sayac_sifirlanir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler, _, saat) = EsitlenmemisSut(db);

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            saat.Advance(TimeSpan.FromMinutes(6));

            handler.PreviewResponse = null;
            handler.PreviewBalance = 0m;
            var kesin = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");
            kesin.Result.Should().Be(PaymentRequestResult.Opened, "sunucu kişiyi tanıdı: bakiye yok, mesaj gitti");
            Isler(db, "session:s1").Should().ContainSingle().Which.State.Should().Be(PaymentJobState.NoBalance);

            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");
            var yeni = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s2");

            yeni.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            yeni.Message.Should().BeNull("kesin sonuç 6 dakikalık ilk görülmeyi sildi — sayaç yeniden başladı");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b "sürerse": art arda iki 409 arasında 30 dakikadan uzun ara sayacı yeniden başlatır (aynı üye kümesi aynı
           // anahtarı üretir — ayırıp yeniden birleştirmeden sonraki ilk 409 eski ilk görülmeyle uzun metin göstermez)
    public async Task Uzun_aradan_sonraki_ilk_esitlenmeme_kisa_metinle_baslar()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, _, log, saat) = EsitlenmemisSut(db);

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Message.Should().BeNull("ilk görülme");
            saat.Advance(TimeSpan.FromMinutes(40));

            var sonra = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            sonra.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            sonra.Message.Should().BeNull("40 dakikalık ara > NotSyncedStreakGap — ilk görülme yeniden başladı");
            log.Warnings.Should().NotContain(w => w.Message.Contains("5 dakikadan uzun"));

            saat.Advance(TimeSpan.FromMinutes(6));
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Message.Should()
                .Be(PaymentResultPresenter.NotSyncedPersistentText, "yeni ilk görülmeden bu yana 6 dakika, ara 6 dakika");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: ağ belirsizliği (sunucu cevap vermedi) sayacı SIFIRLAMAZ — kişinin eşitlendiği bilinmiyor
    public async Task Ag_belirsizligi_sayaci_sifirlamaz()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler, _, saat) = EsitlenmemisSut(db);

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);

            handler.PreviewResponse = _ => throw new TaskCanceledException("stub timeout");
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceUncertain, "ağ hatası: bugünkü genel belirsiz kol");

            saat.Advance(TimeSpan.FromMinutes(6));
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");
            var uzun = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            uzun.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            uzun.Message.Should().Be(PaymentResultPresenter.NotSyncedPersistentText,
                "ağ belirsizliği ilk görülmeyi silmedi — 6 dakikadır eşitlenmiyor");
        }
        finally { Drop(db); }
    }
}
```

`OrderDeck.Tests/Services/PaymentResultPresenterTests.cs` — 4a'nın son testinin ardına:

Bugün:
```csharp
        dialogs.Shown.Should().ContainSingle().Which.Should().Be(
            (PaymentResultPresenter.NotSyncedTitle, PaymentResultPresenter.NotSyncedText, DialogSeverity.Warning));
    }
}
```
Yeni:
```csharp
        dialogs.Shown.Should().ContainSingle().Which.Should().Be(
            (PaymentResultPresenter.NotSyncedTitle, PaymentResultPresenter.NotSyncedText, DialogSeverity.Warning));
    }

    [Fact] // §6.1-b: 5 dakikadan uzun sürenin metni servisten (Message) gelir — başlık ve önem aynı
    public void Esitlenmedi_uzun_metin_sonucun_mesajindan()
    {
        const string uzun =
            "Bu kişinin hesaplarından biri sunucuya ulaşmıyor; mesaj gönderilmedi. Müşteri kaydını kontrol edin ya da grubu ayırıp yeniden birleştirin.";
        var dialogs = new OrderDeck.Tests.Fakes.FakeDialogService();

        PaymentResultPresenter.Notify(dialogs, new PaymentRequestOutcome(PaymentRequestResult.BalanceNotSynced,
            Message: PaymentResultPresenter.NotSyncedPersistentText));

        dialogs.Shown.Should().ContainSingle().Which.Should().Be(("Henüz eşitlenmedi", uzun, DialogSeverity.Warning));
        PaymentResultPresenter.NotSyncedPersistentText.Should().Be(uzun, "spec §6.1-b'nin metni birebir");
    }
}
```

`OrderDeck.Tests/Services/CorrectedPaymentRequestsTests.cs` — `Resend_skips_whoever_needs_an_extra_question`'ın ardına:

Bugün:
```csharp
        calls.Should().Be(1, "ek soru onaylanmaz — ikinci çağrı yok");
    }
```
Yeni:
```csharp
        calls.Should().Be(1, "ek soru onaylanmaz — ikinci çağrı yok");
    }

    [Fact] // W-havuz §6.1-b (K2): eşitlenmemiş bakiye toplu gönderimde BalanceUncertain gibi elle bırakılır — aynı metin
    public async Task Resend_esitlenmemis_bakiye_belirsizle_ayni_metinle_elle_birakilir()
    {
        var (_, _, _, person) = GroupedPersonWithRequest();
        var sut = Sut((_, _, _, _, _) =>
            Task.FromResult(new PaymentRequestOutcome(PaymentRequestResult.BalanceNotSynced)));

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        var o = outcomes.Should().ContainSingle().Subject;
        o.Status.Should().Be(ResendStatus.Manual, "elle bakılmalı");
        o.Note.Should().Be("bakiye doğrulanamadı (sunucuya ulaşılamıyor olabilir)",
            "K2: WPF bunu BalanceUncertain gibi ele alır — yeni metin icat edilmez");
        o.Note.Should().NotContain("BalanceNotSynced", "ham enum adı operatöre gösterilmez");
    }
```

- [ ] **Adım 10: Koştur — FAIL bekle**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Bes_dakika|FullyQualifiedName~PaymentRequestServiceTests.Esitlenmeme_sayaci|FullyQualifiedName~PaymentRequestServiceTests.Kesin_sonuctan_sonra|FullyQualifiedName~PaymentRequestServiceTests.Ag_belirsizligi|FullyQualifiedName~PaymentRequestServiceTests.Uzun_aradan_sonraki|FullyQualifiedName~PaymentResultPresenterTests.Esitlenmedi|FullyQualifiedName~CorrectedPaymentRequestsTests.Resend_esitlenmemis"`
Expected: FAIL — derleme hatası: `PaymentRequestService.NotSyncedClock` yok (CS1061, `EsitlenmemisSut`), `PaymentResultPresenter.NotSyncedPersistentText` yok (CS0117). (Derlenseydi toplu gönderim testi `Note == "BalanceNotSynced"` ile düşerdi — `switch`'in `_` kolu.)

- [ ] **Adım 11: 5 dakika kuralı, uzun metin, toplu gönderim eşlemesi**

`OrderDeck.App/Services/PaymentRequestService.cs` — altı Edit.

(i) `using`:

Bugün:
```csharp
using System;
using System.Threading;
```
Yeni:
```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
```

(ii) Enum belgesine 5 dakika cümlesi:

Bugün:
```csharp
    /// <see cref="BalanceUncertain"/> gibidir (beklenti yazılmaz). Yeni üye EN SONDA: var olan üyelerin değerleri değişmez.</summary>
    BalanceNotSynced,
```
Yeni:
```csharp
    /// <see cref="BalanceUncertain"/> gibidir (beklenti yazılmaz). Aynı kişide 5 dakikadan uzun sürerse metin
    /// <see cref="PaymentRequestOutcome.Message"/>'ta. Yeni üye EN SONDA: var olan üyelerin değerleri değişmez.</summary>
    BalanceNotSynced,
```

(iii) `Message` belgesi:

Bugün:
```csharp
/// <see cref="PaymentRequestResult.PaymentInProgress"/>'ta kirayı tutan bilgisayarın adı biliniyorsa; öbür sonuçlarda
/// null (metni <see cref="PaymentResultPresenter"/> kurar).</para></summary>
```
Yeni:
```csharp
/// <see cref="PaymentRequestResult.PaymentInProgress"/>'ta kirayı tutan bilgisayarın adı biliniyorsa,
/// <see cref="PaymentRequestResult.BalanceNotSynced"/>'ta aynı kişide <c>customer-not-synced</c> 5 dakikadan uzun sürüyorsa
/// (<see cref="PaymentResultPresenter.NotSyncedPersistentText"/>; daha kısaysa null — spec 2026-10-11 §6.1-b); öbür
/// sonuçlarda null (metni <see cref="PaymentResultPresenter"/> kurar).</para></summary>
```

(iv) Saat, eşik ve bellek — 4a'nın sabitinin ardına:

Bugün:
```csharp
    private const string CustomerNotSyncedCode = "customer-not-synced";
```
Yeni:
```csharp
    private const string CustomerNotSyncedCode = "customer-not-synced";

    /// <summary>Spec §6.1-b: aynı kişide <c>customer-not-synced</c> bundan UZUN sürerse metin değişir ve günlüğe kişinin satır
    /// Id'leri yazılır (tam 5 dakika henüz kısa metindir).</summary>
    internal static readonly TimeSpan NotSyncedPatience = TimeSpan.FromMinutes(5);

    /// <summary>Spec §6.1-b'nin "sürerse"si: aynı kişinin art arda iki <c>customer-not-synced</c>'i arasında bundan uzun ara
    /// varsa ilk görülme yeniden başlar (ör. ayırıp yeniden birleştirme sonrası, saatler sonraki ilk 409). Spec aralığı
    /// vermez — mühendislik varsayılanı, değer Sahip sorusu.</summary>
    internal static readonly TimeSpan NotSyncedStreakGap = TimeSpan.FromMinutes(30);

    /// <summary>5 dakika kuralının saati (monoton zaman damgası: <see cref="TimeProvider.GetTimestamp"/>); testler elle
    /// ilerletir (<see cref="PersonGateTimeout"/> deseni).</summary>
    internal TimeProvider NotSyncedClock { get; set; } = TimeProvider.System;

    /// <summary>Spec §6.1-b: kişinin <c>customer-not-synced</c>'i ilk ve son gördüğü an (<see cref="NotSyncedClock"/>'un
    /// zaman damgası; duvar saati değil — NTP ya da elle saat düzeltmesi eşiği kaydırmaz), kişi anahtarı
    /// (<see cref="PaymentPerson.MemberKey"/>) başına. Servis tekil: süreç ömrü boyunca bellekte. Kesin sonuç ya da soru kaydı
    /// siler (<see cref="SendCoreAsync"/>); ağ belirsizliği silmez. Aynı üye kümesi aynı anahtarı üretir
    /// (<see cref="PaymentPerson.KeyOf"/> sıralar): ayırıp yeniden birleştirme anahtarı değiştirmez; sayacı
    /// <see cref="NotSyncedStreakGap"/> yeniden başlatır.</summary>
    private readonly ConcurrentDictionary<string, (long First, long Last)> _notSyncedSince = new(StringComparer.Ordinal);
```

(v) `SendCoreAsync`'in bakiye kapısı (4a'nın hâli):

Bugün:
```csharp
            ownerId = outcome.Job?.CustomerId ?? ownerId;
            if (outcome.Prompt is { } prompt)
                return new(prompt.Kind == PersonScopePromptKind.Merge
                        ? PaymentRequestResult.MergeRequired
                        : PaymentRequestResult.AlreadyRequested,
                    prompt, ownerId, demoted);
            // Spec 2026-10-11 §6.1-b (K2): sunucu kişinin bir üyesini henüz tanımıyor (409 customer-not-synced) — mesaj gitmez,
            // MarkNoBalance yazılmadı; operatöre ayrı uyarı ("Henüz eşitlenmedi"). Banka W2 açısından BalanceUncertain gibidir.
            if (outcome.Uncertain)
                return new(outcome.NotSynced ? PaymentRequestResult.BalanceNotSynced : PaymentRequestResult.BalanceUncertain,
                    null, ownerId, demoted);
```
Yeni:
```csharp
            ownerId = outcome.Job?.CustomerId ?? ownerId;
            // Spec 2026-10-11 §6.1-b: 5 dakika sayacı yalnız art arda gelen customer-not-synced'lerle yaşar. Kesin sonuç ya da
            // soru kişinin kaydını siler; ağ belirsizliği ve öbür belirsizlikler (sunucu kişiyi tanıdığını söylemedi) silmez.
            if (!outcome.NotSynced && (!outcome.Uncertain || outcome.Prompt is not null))
                _notSyncedSince.TryRemove(person.MemberKey, out _);
            if (outcome.Prompt is { } prompt)
                return new(prompt.Kind == PersonScopePromptKind.Merge
                        ? PaymentRequestResult.MergeRequired
                        : PaymentRequestResult.AlreadyRequested,
                    prompt, ownerId, demoted);
            // Spec 2026-10-11 §6.1-b (K2): sunucu kişinin bir üyesini henüz tanımıyor (409 customer-not-synced) — mesaj gitmez,
            // MarkNoBalance yazılmadı; operatöre ayrı uyarı ("Henüz eşitlenmedi"). Banka W2 açısından BalanceUncertain gibidir.
            if (outcome.Uncertain)
                return outcome.NotSynced
                    ? NotSyncedOutcome(person, ownerId, demoted)
                    : new PaymentRequestOutcome(PaymentRequestResult.BalanceUncertain, null, ownerId, demoted);
```

(vi) `NotSyncedOutcome`, `BalanceOutcome`'un önüne:

Bugün:
```csharp
    private sealed record BalanceOutcome(bool Uncertain, decimal AppliedBalance, PaymentJob? Job)
```
Yeni:
```csharp
    /// <summary>Spec 2026-10-11 §6.1-b: <see cref="PaymentRequestResult.BalanceNotSynced"/> sonucu ve 5 dakika kuralı. İlk ve
    /// son görülme kişi anahtarı başına bellekte (<see cref="_notSyncedSince"/>); önceki 409'dan bu yana
    /// <see cref="NotSyncedStreakGap"/> aşıldıysa ilk görülme yeniden başlar. İlk görülmeden bu yana
    /// <see cref="NotSyncedPatience"/>'tan UZUN zaman geçtiyse metin <see cref="PaymentResultPresenter.NotSyncedPersistentText"/>
    /// olur ve günlüğe kişinin satır Id'leri yazılır (ad, kullanıcı adı, telefon yok). Sunucu 409 gövdesinde Id döndürmez;
    /// Id'ler bu PC'nin üyeleridir. Süreler monoton saatle (<see cref="TimeProvider.GetElapsedTime(long, long)"/>).</summary>
    private PaymentRequestOutcome NotSyncedOutcome(PaymentPerson person, string? ownerId, IReadOnlyList<DemotedJob> demoted)
    {
        var clock = NotSyncedClock;
        var now = clock.GetTimestamp();
        var seen = _notSyncedSince.AddOrUpdate(person.MemberKey,
            _ => (now, now),
            (_, old) => clock.GetElapsedTime(old.Last, now) > NotSyncedStreakGap ? (now, now) : (old.First, now));
        var elapsed = clock.GetElapsedTime(seen.First, now);
        if (elapsed <= NotSyncedPatience)
            return new(PaymentRequestResult.BalanceNotSynced, null, ownerId, demoted);
        _log?.LogWarning("customer-not-synced 5 dakikadan uzun sürüyor ({Elapsed}) — kişinin satırları: {MemberIds}",
            elapsed, string.Join(",", person.MemberIds));
        return new(PaymentRequestResult.BalanceNotSynced, null, ownerId, demoted,
            Message: PaymentResultPresenter.NotSyncedPersistentText);
    }

    private sealed record BalanceOutcome(bool Uncertain, decimal AppliedBalance, PaymentJob? Job)
```

`OrderDeck.App/Services/PaymentResultPresenter.cs` — uzun metin sabiti:

Bugün:
```csharp
    public const string NotSyncedText =
        "Kişinin hesapları sunucuyla henüz eşitlenmedi; mesaj gönderilmedi. Birazdan tekrar deneyin — bakiye kaybolmaz, çift düşüm olmaz.";
```
Yeni:
```csharp
    public const string NotSyncedText =
        "Kişinin hesapları sunucuyla henüz eşitlenmedi; mesaj gönderilmedi. Birazdan tekrar deneyin — bakiye kaybolmaz, çift düşüm olmaz.";

    /// <summary>Spec §6.1-b: aynı kişide <c>customer-not-synced</c> 5 dakikadan uzun sürüyor (kalıcı eşitlenmeme: bozuk satır,
    /// hiç inmeyen üye, yakınsamayan eski grup) — servis <see cref="PaymentRequestOutcome.Message"/>'a koyar.</summary>
    public const string NotSyncedPersistentText =
        "Bu kişinin hesaplarından biri sunucuya ulaşmıyor; mesaj gönderilmedi. Müşteri kaydını kontrol edin ya da grubu ayırıp yeniden birleştirin.";
```

`OrderDeck.App/Services/PriceCorrection/CorrectedPaymentRequests.cs` — `ResendAsync`'in `switch`'i:

Bugün:
```csharp
                PaymentRequestResult.BalanceUncertain => new(c, ResendStatus.Manual,
                    "bakiye doğrulanamadı (sunucuya ulaşılamıyor olabilir)"),
```
Yeni:
```csharp
                // W-havuz (spec 2026-10-11 §6.1-b, K2): eşitlenmemiş bakiye BalanceUncertain gibi ele alınır — aynı metin.
                PaymentRequestResult.BalanceUncertain or PaymentRequestResult.BalanceNotSynced => new(c, ResendStatus.Manual,
                    "bakiye doğrulanamadı (sunucuya ulaşılamıyor olabilir)"),
```

- [ ] **Adım 12: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Bes_dakika|FullyQualifiedName~PaymentRequestServiceTests.Esitlenmeme_sayaci|FullyQualifiedName~PaymentRequestServiceTests.Kesin_sonuctan_sonra|FullyQualifiedName~PaymentRequestServiceTests.Ag_belirsizligi|FullyQualifiedName~PaymentRequestServiceTests.Uzun_aradan_sonraki|FullyQualifiedName~PaymentResultPresenterTests.Esitlenmedi|FullyQualifiedName~CorrectedPaymentRequestsTests.Resend_esitlenmemis"`
Expected: PASS — 9 test (beş servis, üç sunum — 4a'nın ikisi dahil, bir toplu gönderim).

- [ ] **Adım 13: Değişmeyen yanıtların kilit testleri (K7, §6.1-c, fark 23)**

Bu testler bugünkü davranışı kilitler; uygulama değişikliği gerektirmez, ilk koşuda PASS beklenir. `OrderDeck.Tests/Services/PaymentRequestServiceTests.NotSynced.cs` — `Ag_belirsizligi_sayaci_sifirlamaz`'ın ardına:

Bugün:
```csharp
            uzun.Message.Should().Be(PaymentResultPresenter.NotSyncedPersistentText,
                "ağ belirsizliği ilk görülmeyi silmedi — 6 dakikadır eşitlenmiyor");
        }
        finally { Drop(db); }
    }
}
```
Yeni:
```csharp
            uzun.Message.Should().Be(PaymentResultPresenter.NotSyncedPersistentText,
                "ağ belirsizliği ilk görülmeyi silmedi — 6 dakikadır eşitlenmiyor");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-c: önizlemenin client-update-required'ı genel BalanceUncertain yolu yeterli, metin değişmez — NoBalance yok
    public async Task Onizleme_client_update_required_belirsiz_NoBalance_yazilmaz()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("client-update-required");

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceUncertain, "spec §6.1-c: metin değişmez");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.Created, "MarkNoBalance yazılmadı");
            job.ApplyKey.Should().BeNull();
            handler.AppliedBalanceBodies.Should().BeEmpty();
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // S2 saha notu 5: member-ids-required kesin sonuçtan ayrılır — genel belirsiz kol, MarkNoBalance yok, iş taze
    public async Task Onizleme_member_ids_required_belirsiz()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("member-ids-required", HttpStatusCode.BadRequest);

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceUncertain);
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.Created, "MarkNoBalance yazılmadı");
            job.ApplyKey.Should().BeNull();
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Theory] // S2 saha notu 5: apply'ın member-ids-required'ı (ayrılır), balance-busy'si ve 404'ü (eşlenmez) genel belirsiz kol — iş anahtarla bekler
    [InlineData("member-ids-required", HttpStatusCode.BadRequest)]
    [InlineData("balance-busy", HttpStatusCode.ServiceUnavailable)]
    [InlineData("Not Found", HttpStatusCode.NotFound)]
    public async Task Apply_member_ids_required_ve_balance_busy_is_anahtarla_bekler(string baslik, HttpStatusCode durum)
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.ApplyResponse = _ => WhatsAppStubHandler.Problem(baslik, durum);

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceUncertain);
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.ApplyUncertain, "anahtar diskte, MarkNoBalance yazılmadı");
            job.ApplyKey.Should().NotBeNull();
            handler.AppliedBalanceBodies.Should().HaveCount(2, "K3: bir deneme + bir anında tekrar");
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-c: apply'ın client-update-required'ı genel belirsiz kol — iş anahtarıyla bekler
    public async Task Apply_client_update_required_belirsiz()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.ApplyProblemTitle = "client-update-required";

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceUncertain);
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.ApplyUncertain, "MarkNoBalance yazılmadı");
            job.ApplyKey.Should().NotBeNull();
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // K7: havuz kipinde kural 1'in kesin kolu — önizleme 200 {balance: 0, personPool: true} kesin NoBalance, eşitlenmeme değil
    public async Task Havuz_onizlemesi_sifir_personPool_true_kesin_NoBalance()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"wpfCustomerId":"{{G(b)}}","balance":0,"updatedAt":"2030-01-01T00:00:00+00:00","personPool":true}""",
                    Encoding.UTF8, "application/json"),
            };

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.Opened, "kişinin hiçbir üyesi sunucuda yok: para olamaz");
            outcome.Result.Should().NotBe(PaymentRequestResult.BalanceNotSynced);
            Isler(db, "session:s1").Should().ContainSingle().Which.State.Should().Be(PaymentJobState.NoBalance);
            handler.AppliedBalanceBodies.Should().BeEmpty("bakiye 0 — apply çağrılmaz");
            _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("250%2C00", "bakiye düşülmeden tam tutar");
        }
        finally { Drop(db); }
    }
}
```

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Onizleme_client_update_required|FullyQualifiedName~PaymentRequestServiceTests.Onizleme_member_ids_required|FullyQualifiedName~PaymentRequestServiceTests.Apply_member_ids_required|FullyQualifiedName~PaymentRequestServiceTests.Apply_client_update_required|FullyQualifiedName~PaymentRequestServiceTests.Havuz_onizlemesi"`
Expected: PASS — 7 vaka (dört `[Fact]` + `[Theory]` × 3). İstemci: önizlemenin 409/400'ü `ThrowMappedAsync` → `ValidationException(Code = başlık)` → `CustomerNotSyncedCode` değil → genel `catch`; apply'ın 400/404/503'ü `ThrowMappedAsync` → `MapProblem` (400 ve 404 `ValidationException`, 404'ün `Code`'u ASP.NET'in `NotFound()` başlığı `"Not Found"` — sunucu `LicensesCustomerBalanceApplyController.cs:443`, `:469`; 503 `LicenseApiUnknownException`), 409'u `MapProblem` → `ReplayAsync`'in genel kolu; `personPool` alanı `CustomerBalancePreview`'da yok, yok sayılır.

- [ ] **Adım 14: Derleme**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Expected: 0 uyarı, 0 hata (`_notSyncedSince`, `NotSyncedPatience`, `NotSyncedStreakGap`, `NotSyncedClock`, `NotSyncedOutcome`, `NotSyncedPersistentText` hepsi kullanılıyor).

- [ ] **Adım 15: Regresyon (değiştirmeden)**

Run (R-Ödeme): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CorrectedPaymentRequestsTests|FullyQualifiedName~PaymentPersonViewModelTests"`
Expected: hepsi PASS. `Resend_skips_whoever_needs_an_extra_question`'ın `BalanceUncertain` vakası aynı metni almaya devam eder.

Run (R-ModOn, yavaş): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentLeaseTests|FullyQualifiedName~PaymentLeaseServerRulesTests|FullyQualifiedName~RemotePaymentRecordsTests|FullyQualifiedName~TwoPcSalesConvergenceTests"`
Expected: hepsi PASS. `PaymentLeaseCoordinator` sonuçla dallanmaz; satır kipindeki gerçek sunucu `customer-not-synced` üretmez; sayaç yalnız `BalanceNotSynced`'te yazılır.

Run (R-Arayüz): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerSearchViewModelTests|FullyQualifiedName~CustomerDetailViewModelTests|FullyQualifiedName~CustomerDetailRoutingTests|FullyQualifiedName~MainShellViewCompositionTests"`
Expected: hepsi PASS (`CustomerSearchViewModel` ve `StreamReportViewModel` `Notify(outcome)`'u çağırır; yeni kol yalnız yeni sonuçta).

- [ ] **Adım 16: Commit (4b)**

```bash
git add OrderDeck.App/Services/PaymentRequestService.cs \
  OrderDeck.App/Services/PaymentResultPresenter.cs \
  OrderDeck.App/Services/PriceCorrection/CorrectedPaymentRequests.cs \
  OrderDeck.Tests/TestHelpers/ManualTimeProvider.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.NotSynced.cs \
  OrderDeck.Tests/Services/PaymentResultPresenterTests.cs \
  OrderDeck.Tests/Services/CorrectedPaymentRequestsTests.cs
git commit -F - <<'MSG'
feat(balance): 5 dakikadan uzun eşitlenmemede ayrı metin ve satır Id günlüğü

Kalıcı customer-not-synced (bozuk satır, bu PC'ye hiç inmeyen üye,
yakınsamayan eski grup) operatöre her seferinde "birazdan tekrar deneyin"
dedirtirdi. İlk görülme anı kişi anahtarı (MemberKey) başına bellekte
tutulur; 5 dakikadan uzun sürerse sonuç uzun metni Message'ta taşır ve
günlüğe kişinin satır Id'leri yazılır (ad, telefon yok). Kesin sonuç ya
da soru sayacı sıfırlar; ağ belirsizliği sıfırlamaz. Fiyat düzeltmesinin
toplu gönderimi BalanceNotSynced'i BalanceUncertain'ın metniyle elle
bırakır (K2: "BalanceUncertain gibi"). Aynı kişide art arda gelen
409'lar arasında 30 dakikadan uzun ara varsa sayaç yeniden başlar; süre
monoton saatle ölçülür. client-update-required (spec §6.1-c: genel
BalanceUncertain yolu yeterli), member-ids-required (S2 saha notu 5
kesin sonuçtan ayrılmasını ister; genel kol MarkNoBalance yazmaz),
balance-busy (S2 saha notu 5: eşlenmez) ve havuzun personPool'lu sıfır
önizlemesi (K7) bugünkü kollarda kalır; testlerle kilitlenir (ortak bakiye
§6.1-b, §6.1-c).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 5: `PullOnceAsync` tetiklemesi (fire-and-forget, bir koşan + bir bekleyen tur) + DI (spec §6.1-b, §8.4-2, §14 #18, M14)

**Files:**
- Değiştir: `OrderDeck.App/Services/PaymentRequestService.cs`
  - ctor belgesi + ctor (`:173-203` — `lease` param belgesi `:173-176`, ctor `:177-203`): yeni son parametre `CustomerChangesPullService? customerPull = null` ve belgesi; ctor'un hemen ardına yeni üyeler `CustomerTour`, `LastCustomerTour`, `_customerTourPending`.
  - `RequestPaymentAsync` belgesi (`:305-324`, `</summary>`'dan önce yeni `<para>`) ve döngüsü (`:347-349`); döngünün ardına (`:357` ile `LeaseRun` `:359` arası) yeni `TriggerCustomerTour` ve `TakeQueuedCustomerTour`.
  - Satır numaraları master `7960eba3`'tendir; Görev 3–4 dosyanın başka yerlerine eklediği için yürütme anında kayar. Her Edit aşağıda alıntılanan **metne** bağlıdır (satır numarasına değil); alıntılanan bloklara Görev 1–4 dokunmaz.
- `OrderDeck.App/AppHost.cs` DEĞİŞMEZ: `:368` `services.AddSingleton<PaymentRequestService>()` tek ctor'u çözer, `:669` kayıtlı tekil `CustomerChangesPullService`'i isteğe bağlı parametreye kendisi verir (DI testi kanıtlar).
- Test (oluştur): `OrderDeck.Tests/Services/PaymentRequestServiceTests.CustomerTour.cs` (partial; `FileDb` kalıbı).
- Test (genişlet): `OrderDeck.Tests/App/CustomerSyncDiTests.cs` — bir test, `Odeme_kirasi_sarmali_odeme_servisine_bagli_on_gonderimi_tekil_orneklerden`'in (`:351-382`) hemen ardına.

Spec §6.1-b (K2, M14): havuz kipinde preview ya da apply 409 `customer-not-synced` alınca, akış `BalanceNotSynced` dönerken arka planda bir müşteri senkron turu başlar: `CustomerChangesPullService.PullOnceAsync` (`CustomerChangesPullService.cs:167-172`; önce gönderir `:258`, sonra çeker — kural 3'ün iki kolu: ayırmayı/birleştirmeyi bu PC yaptıysa gönderim, başka PC yaptıysa çekme). Yalnız gönderim turu (`WpfCustomerProjectionSyncService.SyncOnceAsync`) tetiklenmez. Kabul (spec §6.1-b, §8.4-2): (1) akış turu beklemez; (2) tur kilidi üst üste binmeyi önler — tur koşarken gelen `customer-not-synced` bir tur sıraya alır (koşan bitince koşar), bekleyen bir tur varken gelen yeni tur eklemez; tur(lar) bitince yeni bir 409 yeniden tetikler; (3) turun istisnası yutulur ve günlüğe yazılır. Satır kipinde `customer-not-synced` hiç gelmez → tur hiç başlamaz; davranış değişmez.

**Not (spec ↔ kod):**
- **Fark 6 uygulanır.** `PaymentRequestService` bugün `CustomerChangesPullService`'e bağlı değil (yalnız `PaymentLeaseCoordinator` bağlı, `PaymentLeaseCoordinator.cs:372`). İsteğe bağlı son ctor parametresi (`lease = null` kalıbı); DI döngüsü yok — `CustomerChangesPullService`'in bağımlılıkları (`:157-161`) ödeme servisine uzanmaz ve aynı tekil zaten `PaymentLeaseCoordinator` üstünden ödeme servisine bağlıdır. 11 `new PaymentRequestService(` çağrısı (7 test dosyası, `MainShellViewCompositionTests`'in adlandırılmış argümanlı kurulumu) değişmeden derlenir; hepsinde `CustomerTour` null → tur yok.
- **Test yüzeyi:** `PullOnceAsync` sanal değil, sınıf `sealed` → `internal Func<CancellationToken, Task>? CustomerTour` (emsal `PersonGateTimeout` `:163`; App iç üyeleri `OrderDeck.Tests`'e açık, `OrderDeck.App.csproj:40-42`). ctor'da yöntem grubu: `PullOnceAsync` `Task<CustomerPullOutcome>` döner; yöntem grubu dönüşümü dönüş türünde başvuru kovaryansına izin verir (`Task<T>` → `Task`), oluşan temsilcinin `Target`'ı servisin kendisidir (DI testi buna bakar). İskeletin `customerPull is null ? null : customerPull.PullOnceAsync` ifadesi gövdede eşdeğer `if` ile yazılır (koşullu ifadenin doğal tür çıkarımına bağlı kalmamak için); imza aynı. Ayrı bir alan tutulmaz (atanıp yalnız temsilcide okunan alan CS0414 riskini doğururdu).
- **"Tur kilidi üst üste binmeyi önler; bekleyen bir tetik varken yenisi eklenmez":** spec iki ayrı durum sayar — **koşan** tur ve **bekleyen** (koşanın arkasında sırada) tur. `PullOnceAsync` tur kilidini (`_tourGate` `:125`) **bekler** (`:169`), düşürmez; doğrudan çağrılar sınırsız sıraya girerdi. Kural tetikleyicide üç durumlu `Interlocked` sayaçla (`_customerTourPending`: 0 boşta, 1 koşuyor, 2 koşuyor + bir tur sırada) kurulur: 0→1 tur başlar, 1→2 bir tur sıraya alınır, 2'de tetik düşer; tur sonunda 2→1 bir tur daha koşar, 1→0 tetik serbest (`TakeQueuedCustomerTour`). Sıradaki tur neden gerekli: `PullOnceCoreAsync` önce gönderir (`CustomerChangesPullService.cs:258`) sonra çeker; operatör koşan tur gönderim aşamasını geçtikten sonra birleştirip/ayırıp 409 alırsa o değişikliği bu tur ne gönderir ne çeker. Yerel birleştirme anında gönderilmez (gönderim döngüsü 60 sn, `WpfCustomerProjectionSyncHostedService.cs:13`; çekme döngüsü 30 sn, `CustomerChangesPullHostedService.cs:18`); sıradaki tur §14 #18'in kapattığı gecikmeyi burada da kapatır. Sıradaki tur koşanın bitişini kendi döngüsünde bekler (aynı görev; `_tourGate`'e ikinci bekleyen girmez). `CustomerChangesPullService` değişmez.
- **Tetiğin yeri `RequestPaymentAsync` (spec "preview ya da apply alınınca"):** sonuç `SendCoreAsync`'te tek bir `BalanceNotSynced`'e döner (Görev 4); `SettleAsync`'in anında tekrarının (bir deneme + bir tekrar; `PaymentRequestService.cs:1611-1613`, W3-11 / spec 2026-10-13 §6.6) iki apply'ı da tek sonuç → tek tetik. Tetik `RequestPaymentOnceAsync` döndükten hemen sonradır: kişi kilitleri (`using (hold)`) ve müşteri kirası (`using (lease)`) bırakılmıştır — kira tutulurken çekilen öğe o üyeye düşseydi akış öğeyi `Busy`'de bırakır, tur o öğede dururdu (`CustomerChangesPullService.cs:403-410`). Bugünkü mod On yeniden deneme turu (`PaymentLeaseCoordinator.cs:369-373`) da aynı gerekçeyle koşu dönüşünde koşar; o **beklenir** (sınırlı), bu beklenmez.
- **Tur ömrü:** `Task.Run` ile iş parçacığı havuzunda, `CancellationToken.None` (akışın `ct`'si tıklama bitince geçersiz; tur kendi yaşar ve `_tourGate` sırasını bekler — 30 sn'lik arka plan turu koşuyorsa onun arkasında). Uygulama kapanırken süren tur yarıda kalabilir; istisnası yutulur ve Warning yazılır (30 sn'lik arka plan turuyla aynı risk; `CustomerChangesPullHostedService.TickAsync` de hatayı yutar).
- **`LastCustomerTour` yalnız testler için:** akış onu okumaz. Görev yalnız 0→1 geçişinde atanır; sıradaki tur aynı görevin döngüsünde koşar, dolayısıyla `LastCustomerTour` koşan ve sıradaki turun ikisi de bitince tamamlanır. Testler sıralı tıklar.
- **Ad benzerliği:** `PaymentLeaseRetry.CustomerTour` (`PaymentLeaseCoordinator.cs:62`) ayrı bir enum üyesidir; servisteki kullanımlar nitelikli (`PaymentLeaseRetry.None` …), çakışma yok.
- **Banka W2 / yeni hosted service / `AppHost` kaydı:** yok. Spec §6.1-b'nin "yalnız gönderim yetmez" kuralı gereği `WpfCustomerProjectionSyncService.SyncOnceAsync` doğrudan çağrılmaz.

- [ ] **Adım 1: Servis testleri (kırmızı)**

`OrderDeck.Tests/Services/PaymentRequestServiceTests.CustomerTour.cs` (yeni). Yardımcı adı `TurSayaci` sınıfta yok (Grep: `TurSayaci|CustomerTour|LastCustomerTour` → `OrderDeck.Tests`'te eşleşme yok). Var olanlar: `Uye`, `Kisi`, `KisiSut` (`.Person.cs:27-40`), `FileDb`/`Drop`/`Leased` (`PaymentRequestServiceTests.cs:2165-2190`), `T` (`:1001`), `SutOn(…, log:)`, `WhatsAppStubHandler.PreviewResponse` ve public `Problem` (Görev 4).

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── customer-not-synced'in tetiklediği müşteri senkron turu (spec 2026-10-11 §6.1-b, §8.4-2, §14 #18, M14) ──
//
// Havuz kipinde sunucu kişinin bir üyesini henüz tanımıyorsa (409 customer-not-synced) akış BalanceNotSynced döner ve
// arka planda bir müşteri senkron turu başlatır (CustomerChangesPullService.PullOnceAsync: önce gönderir, sonra çeker).
// Akış turu BEKLEMEZ; tur koşarken gelen tetik bir tur sıraya alır, bekleyen bir tur varken yenisi eklenmez; turun
// hatası yutulur ve günlüğe yazılır. Tur kişi kilitleri ve müşteri kirası bırakıldıktan SONRA başlar. Burada CustomerTour'a sahte tur bağlanır; DI'ın gerçek
// servisi bağladığını CustomerSyncDiTests.Odeme_servisi_musteri_turunu_DIdan_alinan_akis_servisine_baglar kanıtlar.
public partial class PaymentRequestServiceTests
{
    /// <summary><see cref="PaymentRequestService.CustomerTour"/>'a bağlanan sahte tur: çağrıları sayar, başladığını
    /// bildirir, test <see cref="Bitsin"/>'i bırakana kadar sürer; <see cref="Firlat"/> doluysa sonunda fırlatır.</summary>
    private sealed class TurSayaci
    {
        public int Cagri;
        public TaskCompletionSource Baslasin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Bitsin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Firlat { get; set; }

        public async Task Tur(CancellationToken ct)
        {
            Interlocked.Increment(ref Cagri);
            Baslasin.TrySetResult();
            await Bitsin.Task;
            if (Firlat is not null) throw Firlat;
        }
    }

    [Fact] // §6.1-b kabul 1: akış turu beklemez (kira iddiası yalnız duman denetimi; tetiğin yeri Adım 5'in Edit'iyle sabit)
    public async Task Esitlenmeme_musteri_turunu_tetikler_akis_turu_beklemez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");
            var sayac = new TurSayaci();
            bool? kiraTurBasindaTutuluyordu = null;
            sut.CustomerTour = ct =>
            {
                kiraTurBasindaTutuluyordu = Leased(db, a.Id) || Leased(db, b.Id);
                return sayac.Tur(ct);
            };

            // Bitsin bırakılmadı: akış turu bekleseydi burada asılı kalırdı (üst sınır yalnız asılı kalan testi düşürür).
            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1").WaitAsync(TestPatience.Upper);

            outcome.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            _launcher.LaunchedUrls.Should().BeEmpty("mesaj gönderilmedi");
            await sayac.Baslasin.Task.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(1);
            sut.LastCustomerTour.IsCompleted.Should().BeFalse("tur test bırakana kadar sürüyor; akış onu beklemeden döndü");
            kiraTurBasindaTutuluyordu.Should().BeFalse("duman denetimi: tur başında kira tutulmuyor");

            sayac.Bitsin.SetResult();
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b kabul 2: tur koşarken gelen tetik bir tur sıraya alır; bekleyen tur varken yenisi eklenmez;
           // turlar bitince yeni 409 yeniden tetikler
    public async Task Bekleyen_tur_varken_ikinci_esitlenmeme_yeni_tur_eklemez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");
            var sayac = new TurSayaci();
            sut.CustomerTour = sayac.Tur;

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            await sayac.Baslasin.Task.WaitAsync(TestPatience.Upper);
            var ilkTur = sut.LastCustomerTour;

            // Tur sürerken üç tıklama daha: hepsi eşitlenmedi. İlki bir tur sıraya alır (koşan tur gönderimini geçmiş
            // olabilir — bu PC'nin yeni birleştirmesini sıradaki tur gönderir); sıra doluyken öbür ikisi tur EKLEMEZ.
            for (int i = 0; i < 3; i++)
            {
                (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                    .Should().Be(PaymentRequestResult.BalanceNotSynced);
            }
            ReferenceEquals(sut.LastCustomerTour, ilkTur).Should().BeTrue("sıradaki tur koşan turun görevinde koşar");
            sayac.Cagri.Should().Be(1, "tur kilidi üst üste binmeyi önler: sıradaki tur koşan bitmeden başlamaz");

            sayac.Bitsin.SetResult();
            await ilkTur.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(2, "dört eşitlenmeme: koşan tur + bir sıradaki tur; bekleyen varken yenisi eklenmedi");

            // Turlar bitti (sayaç boşta): yeni bir 409 yeniden tetikler. Bitsin tamam — üçüncü tur hemen biter.
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            ReferenceEquals(sut.LastCustomerTour, ilkTur).Should().BeFalse("turlar bitince yeni 409 yeni bir tur başlatır");
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(3);
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: apply'ın customer-not-synced'i de tetikler; tetik koşu başına tek (SettleAsync'in anında tekrarının
           // iki apply'ı tek sonuç)
    public async Task Apply_customer_not_synced_de_turu_tetikler()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.ApplyProblemTitle = "customer-not-synced";
            var sayac = new TurSayaci();
            sut.CustomerTour = sayac.Tur;

            // Bitsin akış boyunca TUTULUR: apply başına bir tetik olsaydı ilki turu başlatır (0→1), ikincisi turu
            // belirlenimci olarak sıraya alırdı (1→2) ve Bitsin bırakılınca Cagri 2 olurdu.
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            await sayac.Baslasin.Task.WaitAsync(TestPatience.Upper);
            handler.AppliedBalanceBodies.Should().HaveCount(2, "SettleAsync'in anında tekrarı: bir deneme + bir tekrar");
            sayac.Cagri.Should().Be(1);

            sayac.Bitsin.SetResult();
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(1, "iki apply tek sonuç: koşu başına tek tetik, sıraya tur girmedi");
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b kabul 3: turun istisnası yutulur ve günlüğe yazılır; sayaç boşa döner, sonraki 409 yeniden tetikler
    public async Task Tur_hatasi_yutulur_gunluge_yazilir_sayac_bosa_doner()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var log = new CapturingLogger<PaymentRequestService>();
            var handler = new WhatsAppStubHandler
            {
                PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced"),
            };
            var sut = SutOn(handler, _store, _launcher, jobs: db.Jobs, customers: db.Customers, busy: db.Busy, log: log);
            var sayac = new TurSayaci { Firlat = new InvalidOperationException("tur-hatasi") };
            sayac.Bitsin.SetResult();
            sut.CustomerTour = sayac.Tur;

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);   // fırlatmaz: hata turun içinde yutuldu

            sut.LastCustomerTour.IsCompletedSuccessfully.Should().BeTrue();
            log.Warnings.Should().ContainSingle(e => e.Message.Contains("müşteri senkron turu başarısız"))
                .Which.Exception.Should().Contain("tur-hatasi");

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(2, "hatalı tur sayacı boşa döndürdü: sonraki 409 yeniden tetikler");
        }
        finally { Drop(db); }
    }

    [Theory] // yalnız customer-not-synced tetikler: öbür belirsizler (client-update-required) ve kesin sonuç tur başlatmaz
    [InlineData("client-update-required", PaymentRequestResult.BalanceUncertain)]
    [InlineData(null, PaymentRequestResult.Opened)]
    public async Task Esitlenmeme_disinda_tur_tetiklenmez(string? onizlemeHatasi, PaymentRequestResult beklenen)
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 0m;  // hata yoksa önizleme kesin NoBalance
            if (onizlemeHatasi is not null)
            {
                string baslik = onizlemeHatasi;
                handler.PreviewResponse = _ => WhatsAppStubHandler.Problem(baslik);
            }
            var sayac = new TurSayaci();
            sayac.Bitsin.SetResult();     // yanlışlıkla başlayan tur hemen biter: aşağıdaki bekleme asılı kalmaz
            sut.CustomerTour = sayac.Tur;

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result.Should().Be(beklenen);
            // Tetik yoksa LastCustomerTour tamamlanmış görevdir; tetik olsaydı bu bekleme turu koşturur ve sayaç 1 olurdu.
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);

            sayac.Cagri.Should().Be(0);
        }
        finally { Drop(db); }
    }

    [Fact] // CustomerTour null (DI'sız kurulum, testlerin varsayılanı): akış aynı sonucu verir, istisna yok
    public async Task Tur_yoksa_akis_degismez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");

            sut.CustomerTour.Should().BeNull("customerPull verilmedi");
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            sut.LastCustomerTour.IsCompleted.Should().BeTrue("tur yok: başlatılan bir şey yok");
        }
        finally { Drop(db); }
    }
}
```

Notlar: `Leased` (`PaymentRequestServiceTests.cs:2189-2190`) `CustomerBusySet.RunLocked` ile kirayı okur; değişken tur iş parçacığında yazılır ve `Baslasin` beklendikten sonra okunur (TCS tamamlanması önce-olur ilişkisini kurar). Test 1'in kira iddiası yalnız **duman denetimidir**, belirlenimci değildir: tur gövdesi `Task.Run` ile havuzda koştuğundan, yanlışlıkla `RequestPaymentOnceAsync`'in içine (kira tutulurken) konmuş bir tetiği yalnız tur akış kirayı bırakmadan önce koşarsa yakalar, sonra koşarsa kaçırır (yanlış kırmızı vermez). Tetiğin koşu sonrasındaki yeri bu iddiayla değil, Adım 5'in Edit'iyle — `RequestPaymentOnceAsync` döndükten hemen sonraki satır — sabitlenir. Test 3'ün "koşu başına tek tetik" iddiası ise belirlenimcidir: `Bitsin` tutulurken ikinci bir tetik sayacı eşzamanlı olarak 1→2 yapar ve sıradaki tur `Bitsin` bırakılınca kesin koşar. Duvar saati sınırlarının hepsi `TestPatience.Upper`'a bağlıdır ve yalnız "olması gereken"i bekler; "olmadığını" sınayan iddialar (`Cagri == 0`, `Cagri == 1`, `ReferenceEquals(…, ilkTur)`) süreye değil, sayacın ve `LastCustomerTour`'un tetikte eşzamanlı güncellenmesine dayanır (`TriggerCustomerTour` görevi `Task.Run` döner dönmez atar; sıraya alma ve düşürme `RequestPaymentAsync` dönmeden tamamdır).

- [ ] **Adım 2: DI testi (kırmızı)**

`OrderDeck.Tests/App/CustomerSyncDiTests.cs` — `Odeme_kirasi_sarmali_odeme_servisine_bagli_on_gonderimi_tekil_orneklerden`'in kapanışından sonra, `Uzak_odeme_kayitlari_…`'nın `[Fact]`'ından önce. Edit eşleşmesi:

Eski:
```csharp
        PrivateField<global::OrderDeck.App.Services.IAppUpdatePrompt>(lease, "_updatePrompt").Should().BeSameAs(
            host.Services.GetRequiredService<global::OrderDeck.App.Services.IAppUpdatePrompt>());
    }

    [Fact]
    public void Uzak_odeme_kayitlari_tekil_fiyat_duzeltmesine_bagli_akis_ve_kayit_gondericisiyle_bosalir()
```

Yeni:
```csharp
        PrivateField<global::OrderDeck.App.Services.IAppUpdatePrompt>(lease, "_updatePrompt").Should().BeSameAs(
            host.Services.GetRequiredService<global::OrderDeck.App.Services.IAppUpdatePrompt>());
    }

    [Fact]
    public void Odeme_servisi_musteri_turunu_DIdan_alinan_akis_servisine_baglar()
    {
        // Ortak bakiye W-havuz (spec 2026-10-11 §6.1-b, K2): customer-not-synced'te fire-and-forget müşteri senkron turu.
        // PaymentRequestService'in isteğe bağlı parametresi — DI vermezse tur sessizce hiç koşmaz, kişi 30 sn'lik çekme
        // döngüsünü beklerdi. Tur, müşteri akışının TEK örneğinin PullOnceAsync'i: tek tur kilidi (_tourGate) yalnız
        // tek örnekte geçerli (arka plan işi ve satış akışı da aynı örneği tutar).
        using var host = new global::OrderDeck.App.AppHost();
        var payment = host.Services.GetRequiredService<global::OrderDeck.App.Services.PaymentRequestService>();
        var pull = host.Services.GetRequiredService<CustomerChangesPullService>();

        payment.CustomerTour.Should().NotBeNull(
            "spec §6.1-b: isteğe bağlı parametre DI'dan gelmezse customer-not-synced turu sessizce hiç koşmazdı");
        payment.CustomerTour!.Target.Should().BeSameAs(pull, "müşteri akışının tekil örneği");
        payment.CustomerTour.Method.Name.Should().Be(nameof(CustomerChangesPullService.PullOnceAsync),
            "önce gönderir sonra çeker; yalnız gönderim turu kural 3'ün çekme kolunu kapatmaz");
    }

    [Fact]
    public void Uzak_odeme_kayitlari_tekil_fiyat_duzeltmesine_bagli_akis_ve_kayit_gondericisiyle_bosalir()
```

Sınıfın `using`'leri yeter (`OrderDeck.App.Services.Sync` `:5`, `Microsoft.Extensions.DependencyInjection` `:3`). `AppHost` geçici kökte kurulur (sınıf belgesi, TestAssemblyInit).

- [ ] **Adım 3: Koştur — FAIL bekle**

Run (PowerShell): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~CustomerSyncDiTests"`
Expected: FAIL — derleme hatası: `'PaymentRequestService' does not contain a definition for 'CustomerTour'` ve `'LastCustomerTour'` (CS1061; `.CustomerTour.cs` ve `CustomerSyncDiTests.cs`). Test projesi derlenmediği için hiçbir test koşmaz.

- [ ] **Adım 4: ctor parametresi ve tur üyeleri**

`OrderDeck.App/Services/PaymentRequestService.cs` — `lease` param belgesinin son satırından ctor'un kapanışına kadar olan blok (`:176-203`) değişir; yeni üyeler ctor'un hemen ardına girer (`// License key → Guid LicenseId resolution.` yorumundan önce). `using System.Threading;` (`:2`) ve `using OrderDeck.App.Services.Sync;` (`:6`) zaten var; yeni `using` gerekmez.

Eski:
```csharp
    /// testlerde (bugünkü yol).</param>
    public PaymentRequestService(
        SettingsStore settingsStore,
        WhatsAppMessageBuilder messageBuilder,
        IUrlLauncher launcher,
        LicenseApiClient api,
        ICurrentLicenseProvider currentLicense,
        IPaymentJobStore jobs,
        IShippingSettingsProvider shipping,
        Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? log = null,
        CustomerRepository? customers = null,
        CustomerBusySet? busy = null,
        PaymentOffRecordWriter? offRecords = null,
        PaymentLeaseCoordinator? lease = null)
    {
        _settingsStore = settingsStore;
        _messageBuilder = messageBuilder;
        _launcher = launcher;
        _api = api;
        _currentLicense = currentLicense;
        _jobs = jobs;
        _shipping = shipping;
        _log = log;
        _customers = customers;
        _busy = busy;
        _offRecords = offRecords;
        _lease = lease;
    }
```

Yeni:
```csharp
    /// testlerde (bugünkü yol).</param>
    /// <param name="customerPull">Spec 2026-10-11 §6.1-b (K2): 409 customer-not-synced'te fire-and-forget müşteri senkron turu
    /// (önce gönderim, sonra çekme). DI verir; null yalnız testlerde (tur yok).</param>
    public PaymentRequestService(
        SettingsStore settingsStore,
        WhatsAppMessageBuilder messageBuilder,
        IUrlLauncher launcher,
        LicenseApiClient api,
        ICurrentLicenseProvider currentLicense,
        IPaymentJobStore jobs,
        IShippingSettingsProvider shipping,
        Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? log = null,
        CustomerRepository? customers = null,
        CustomerBusySet? busy = null,
        PaymentOffRecordWriter? offRecords = null,
        PaymentLeaseCoordinator? lease = null,
        CustomerChangesPullService? customerPull = null)
    {
        _settingsStore = settingsStore;
        _messageBuilder = messageBuilder;
        _launcher = launcher;
        _api = api;
        _currentLicense = currentLicense;
        _jobs = jobs;
        _shipping = shipping;
        _log = log;
        _customers = customers;
        _busy = busy;
        _offRecords = offRecords;
        _lease = lease;
        // Yöntem grubu: PullOnceAsync Task<CustomerPullOutcome> döner, temsilci Task bekler (dönüş türü kovaryansı).
        // Temsilcinin Target'ı DI'ın tekil örneğidir — tek tur kilidi yalnız tek örnekte geçerli.
        if (customerPull is not null) CustomerTour = customerPull.PullOnceAsync;
    }

    /// <summary>customer-not-synced'in tetiklediği müşteri senkron turu (ctor: <see cref="CustomerChangesPullService.PullOnceAsync"/>;
    /// spec 2026-10-11 §6.1-b). Null = tur yok. Testler kendi turunu verir.</summary>
    internal Func<CancellationToken, Task>? CustomerTour { get; set; }

    /// <summary>Son başlatılan tur (yoksa tamamlanmış görev). Akış BEKLEMEZ ve okumaz; yalnız testler bekler.</summary>
    internal Task LastCustomerTour { get; private set; } = Task.CompletedTask;

    /// <summary>Tur sayacı (spec §6.1-b: "tur kilidi üst üste binmeyi önler; bekleyen bir tetik varken yenisi eklenmez"):
    /// 0 = boşta, 1 = bir tur koşuyor, 2 = bir tur koşuyor ve bir tur sırada (bekleyen). Yalnız <see cref="Interlocked"/>
    /// ile değişir. <see cref="CustomerChangesPullService.PullOnceAsync"/> tur kilidini BEKLER (düşürmez); kural buradadır.</summary>
    private int _customerTourPending;
```

- [ ] **Adım 5: Tetik ve `RequestPaymentAsync`**

Aynı dosya. Önce `RequestPaymentAsync` belgesine tetiğin yeri (`:322-325`):

Eski:
```csharp
    /// mesaj gitmez.</para>
    /// </summary>
    /// <param name="productTotalFor">Kira altında okunan GÜNCEL kişiyle çağrılır ve kişinin ürün
```

Yeni:
```csharp
    /// mesaj gitmez.</para>
    ///
    /// <para>Spec 2026-10-11 §6.1-b (K2): koşu <see cref="PaymentRequestResult.BalanceNotSynced"/> dönerse (havuz kipi;
    /// sunucu kişinin bir üyesini henüz tanımıyor) bir müşteri senkron turu (<see cref="CustomerTour"/>) arka planda
    /// başlatılır ve BEKLENMEZ. Tetik koşu döndükten SONRADIR: kişi kilitleri ve müşteri kirası bırakılmıştır — kira
    /// tutulurken çekilen öğe o üyeye düşseydi müşteri akışı onu meşgul sayıp turu o öğede durdururdu. Koşu başına tek
    /// sonuç olduğundan önizlemenin ya da apply'ın (SettleAsync'in anında tekrarı dahil; spec 2026-10-13 §6.6, W3-11)
    /// 409'u tek tetiktir. Tur koşarken gelen tetik bir tur sıraya alır; bekleyen bir tur varken yenisi eklenmez
    /// (<see cref="TriggerCustomerTour"/>).</para>
    /// </summary>
    /// <param name="productTotalFor">Kira altında okunan GÜNCEL kişiyle çağrılır ve kişinin ürün
```

Sonra döngü ve döngünün ardına tetikleyici (`:347-363`):

Eski:
```csharp
            var outcome = await RequestPaymentOnceAsync(
                customer, productTotalFor, streamDate, scopeKey, confirmedTokens, retried, modeOnTotal, run, ct);
            var retry = run.Flow?.PendingRetry ?? PaymentLeaseRetry.None;
            if (retry == PaymentLeaseRetry.None) return outcome;

            var stop = await _lease!.RunRetryAsync(retry, ct);
            if (stop is not null) return stop with { OwnerId = outcome.OwnerId };
            if ((retry & PaymentLeaseRetry.ModeOffRound) != 0) modeOnTotal = run.Flow!.BegunProductTotal;
            retried |= retry;
        }
    }

    /// <summary>Bir koşunun mod On akışı (<see cref="PaymentLeaseCoordinator.EnterAsync"/>; yerel kip Off'ta null).</summary>
```

Yeni:
```csharp
            var outcome = await RequestPaymentOnceAsync(
                customer, productTotalFor, streamDate, scopeKey, confirmedTokens, retried, modeOnTotal, run, ct);
            // Spec §6.1-b: kişi kilitleri ve müşteri kirası burada bırakılmıştır — tur beklenmez.
            if (outcome.Result == PaymentRequestResult.BalanceNotSynced) TriggerCustomerTour();
            var retry = run.Flow?.PendingRetry ?? PaymentLeaseRetry.None;
            if (retry == PaymentLeaseRetry.None) return outcome;

            var stop = await _lease!.RunRetryAsync(retry, ct);
            if (stop is not null) return stop with { OwnerId = outcome.OwnerId };
            if ((retry & PaymentLeaseRetry.ModeOffRound) != 0) modeOnTotal = run.Flow!.BegunProductTotal;
            retried |= retry;
        }
    }

    /// <summary>Spec 2026-10-11 §6.1-b: customer-not-synced'in müşteri senkron turunu arka planda başlatır (fire-and-forget;
    /// iş parçacığı havuzunda — müşteri akışı arayüz iş parçacığında koşmamalı). Tur kilidi üst üste binmeyi önler; bekleyen
    /// bir tetik varken yenisi eklenmez (<see cref="_customerTourPending"/>): boştaysa (0→1) tur başlar; tur koşuyorsa
    /// (1→2) bir tur sıraya alınır — koşan tur gönderim aşamasını geçmiş olabilir, bu PC'nin yeni birleştirmesini/ayırmasını
    /// sıradaki tur gönderir; bir tur zaten sıradaysa (2) tetik düşer. Sıradaki tur aynı görevin döngüsünde, koşan bitince
    /// koşar. Tur akışın iptalinden bağımsız yaşar (<see cref="CancellationToken.None"/>; tıklama bitince akışın belirteci
    /// geçersiz). Turun hatası yutulur ve Warning olarak yazılır (yalnız istisna; ad, telefon yok) — akışı düşürmez,
    /// sonraki 409 yeniden tetikler.</summary>
    private void TriggerCustomerTour()
    {
        var tour = CustomerTour;
        if (tour is null) return;
        while (true)
        {
            int state = Volatile.Read(ref _customerTourPending);
            if (state == 2)
            {
                _log?.LogDebug("customer-not-synced: bir müşteri senkron turu zaten sırada — yeni tetik eklenmedi");
                return;
            }
            // Yarışta (başka tetik ya da biten tur sayacı değiştirdi) yeniden okunur.
            if (Interlocked.CompareExchange(ref _customerTourPending, state + 1, state) != state) continue;
            if (state == 1)
            {
                _log?.LogDebug("customer-not-synced: müşteri senkron turu koşuyor — bir tur sıraya alındı");
                return;
            }
            break;   // 0 → 1: tur bu tetikle başlar
        }
        LastCustomerTour = Task.Run(async () =>
        {
            try
            {
                do
                {
                    try { await tour(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { _log?.LogWarning(ex, "customer-not-synced'in tetiklediği müşteri senkron turu başarısız"); }
                }
                while (TakeQueuedCustomerTour());
            }
            catch
            {
                // Yalnız günlük yazıcısı fırlatırsa buraya düşülür: sayaç kilitli kalmasın, sonraki 409 yeniden tetiklesin.
                Volatile.Write(ref _customerTourPending, 0);
                throw;
            }
        });
    }

    /// <summary>Bir tur bitince çağrılır. true: sırada bir tur vardı (2→1), aynı görev bir tur daha koşar. false: sıra boş
    /// (1→0), sonraki tetik yeni tur başlatır. İki karşılaştır-değiştir arasında bir tetik 1'i 2 yapabilir; döngü yeniden
    /// dener (tur koşarken sayaç yalnız 1 ya da 2'dir, biri mutlaka tutar).</summary>
    private bool TakeQueuedCustomerTour()
    {
        while (true)
        {
            if (Interlocked.CompareExchange(ref _customerTourPending, 1, 2) == 2) return true;
            if (Interlocked.CompareExchange(ref _customerTourPending, 0, 1) == 1) return false;
        }
    }

    /// <summary>Bir koşunun mod On akışı (<see cref="PaymentLeaseCoordinator.EnterAsync"/>; yerel kip Off'ta null).</summary>
```

Derleme notları: `Task.Run(Func<Task>)` iç görevi açar — `LastCustomerTour` koşan tur ve (varsa) sıradaki tur bitip sayaç boşa dönünce tamamlanır. `_customerTourPending` `Interlocked`/`Volatile` ile okunur ve yazılır (CS0414 yok); `LastCustomerTour`'un `private set`'i yalnız burada (yalnız 0→1 geçişinde atanır). Dış `catch { …; throw; }` yalnız iç `catch`'in günlük çağrısı fırlatırsa çalışır; görevi hatalı bırakır (gözlenmeyen istisna, akışı etkilemez). `LogDebug`/`LogWarning` `Microsoft.Extensions.Logging` uzantıları (`:5`). `BalanceNotSynced` Görev 4'ün enum üyesidir.

- [ ] **Adım 6: Koştur — PASS**

Run (PowerShell): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Esitlenmeme_|FullyQualifiedName~PaymentRequestServiceTests.Bekleyen_tur_|FullyQualifiedName~PaymentRequestServiceTests.Apply_customer_not_synced_de_turu|FullyQualifiedName~PaymentRequestServiceTests.Tur_|FullyQualifiedName~CustomerSyncDiTests.Odeme_servisi_musteri_turunu"`
Expected: PASS — bu görevin 8 vakası (5 `[Fact]` + `[Theory]` × 2 + DI testi). Filtre Görev 4'ün `Esitlenmeme_sayaci_kisi_basina`'sını da yakalar; o da PASS.

- [ ] **Adım 7: Derleme — 0 uyarı**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Expected: `Build succeeded`, 0 Warning(s), 0 Error(s) (`TreatWarningsAsErrors` + `Nullable`).

- [ ] **Adım 8: Regresyon (var olan testler değiştirilmeden)**

Run (PowerShell):
```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CorrectedPaymentRequestsTests|FullyQualifiedName~PaymentPersonViewModelTests"
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~WpfCustomerProjectionSyncServiceTests|FullyQualifiedName~CustomerChangesPullServiceTests|FullyQualifiedName~CustomerSyncDiTests|FullyQualifiedName~MainShellViewCompositionTests"
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentLeaseTests|FullyQualifiedName~PaymentLeaseServerRulesTests|FullyQualifiedName~RemotePaymentRecordsTests|FullyQualifiedName~TwoPcSalesConvergenceTests"
```
Expected: hepsi PASS (R-Ödeme; R-Senkron + `MainShellViewCompositionTests`; R-ModOn — yavaş, ~dakikalar). Testlerdeki bütün `new PaymentRequestService(…)` kurulumları `customerPull` vermez → `CustomerTour` null → tur yok, davranış aynı; `CustomerChangesPullService` değişmedi; `AppHost` kaydı değişmedi (DI testleri aynı tekilleri görür); satır kipinde `customer-not-synced` gelmediği için `TriggerCustomerTour` hiç çağrılmaz. `TwoPcSalesStack` (`:315`) ödeme servisini mod On sarmalıyla ama `customerPull`'suz kurar — mod On yeniden deneme turu (`PaymentLeaseCoordinator.RunRetryAsync`) değişmeden koşar.

- [ ] **Adım 9: Commit**

```bash
git add OrderDeck.App/Services/PaymentRequestService.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.CustomerTour.cs \
  OrderDeck.Tests/App/CustomerSyncDiTests.cs
git commit -F - <<'MSG'
feat(balance): customer-not-synced'te müşteri senkron turunu tetikle (bir koşan + bir bekleyen tur)

Havuz kipinde sunucu kişinin bir üyesini henüz tanımıyorsa (409
customer-not-synced) akış BalanceNotSynced dönerken arka planda bir
müşteri senkron turu başlat: CustomerChangesPullService.PullOnceAsync
önce gönderir, sonra çeker; ayırmayı ya da birleştirmeyi bu PC yaptıysa
gönderim, başka PC yaptıysa çekme kapatır (yalnız gönderim turu ikinci
kolu 30 sn'lik döngüye bırakırdı). Akış turu beklemez; tetik koşu
döndükten sonradır, kişi kilitleri ve müşteri kirası bırakılmıştır
(kira tutulurken çekilen öğe Busy'de kalırdı). PullOnceAsync tur
kilidini beklediği için "tur kilidi üst üste binmeyi önler; bekleyen
tetik varken yenisi eklenmez" kuralı tetikleyicide üç durumlu
Interlocked sayaçla kurulur: tur koşarken gelen tetik bir tur sıraya
alır (koşan tur gönderimini geçmiş olabilir), sıra doluyken tetik düşer.
Turun hatası yutulur ve Warning yazılır. Bağımlılık isteğe bağlı son ctor parametresi: DI kayıtlı
tekili kendisi verir (AppHost değişmez, DI testi kanıtlar); testlerde
null, tur yok. Satır kipi değişmez (ortak bakiye §6.1-b, §8.4-2, K2, M14).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 6: Birleştirme notunun üç durumu (PaymentPrompt.PersonPool) + belge yorumları + PersonJob gerekçesi (spec §6.1-d, §6.1-g, §7.5, §8.4-4, M3)

**Files:**
- Değiştir: `OrderDeck.App/Services/PaymentRequestService.cs`:
  - `PaymentPrompt` gövdesi `:96-107`: `AlsoConfirms` (`:98-102`) ile `Tokens` (`:104-106`) arasına yeni `PersonPool` özelliği.
  - `RefundsToOtherRow` param yorumu `:79-81`: yalnız belge, ikinci commit'te.
  - `WithLocalPlan` `:1044-1071`: `with` bloğu `:1054-1063`, `AlsoConfirms = [plan.Token],` `:1062`'nin altına bir satır.
  - `PromptOf` `:1075-1095`: `return new(…)` `:1084-1094`'e nesne başlatıcı eklenir.
  - Sunucu izlerinin sorusu: yorum `:1507-1509`, `return` `:1510-1514`.
  - Yeni özel `PersonPoolOf`: `ReconcilePersonScopeAsync` bitiminden (`:1561-1563`) hemen sonra.
- Değiştir: `OrderDeck.App/Services/PaymentResultPresenter.cs`: `WithOtherRowNote` `:240-244` (imza aynı); `PromptText` belgesi `:148-167`'nin iade cümlesi `:152-155` (yalnız belge, ikinci commit'te).
- Değiştir: `OrderDeck.Core/Storage/Repositories/PersonJob.cs`: yalnız yorumlar. Kalan iş paragrafı `:75-80`, `RefundsToOtherRow` belgesi `:212-214`. Kod (`:112-118` sıralama, `:145-146` hesap) DEĞİŞMEZ.
- Test (genişlet): `OrderDeck.Tests/Services/PaymentResultPresenterTests.cs`. Beş test yöntemi (biri `[Theory]`) `Uzak_dusumler_gitmis_istek_diye_anlatilmaz` (`:124-140`) sonrasına eklenir.
- Test (genişlet): `OrderDeck.Tests/Services/PaymentRequestServiceTests.Person.cs`: `ScopeJsonAt` `:62-67` isteğe bağlı parametre alır. `Birlesen_kisinin_parali_iki_isi_…` iddiasının `:164` satırı değişir (aşağıda Not 4).
- Test (oluştur): `OrderDeck.Tests/Services/PaymentRequestServiceTests.PoolNote.cs`.
- Test (genişlet): `OrderDeck.Tests/Sync/PaymentLeaseTests.cs`: bir test, `Yerel_teslim_ve_uzak_oncul_Hayir_mesaj_yok_yeni_kira_yok` (`:793-812`) sonrasına.
- DOKUNULMAZ: `LegacyScopeDeductionPrompt` (`PaymentRequestService.cs:1667-1670`) ve `PaymentLeaseCoordinator.cs:822`'deki uzak soru. İkisi de `RefundsToOtherRow = false` taşır, not çıkmaz, varsayılan `PersonPool = false` zararsızdır. Ayrıca DOKUNULMAZ: `PersonScopePlanTests`, `PaymentJobStoreContractTests` (`RefundsToOtherRow` hesabı değişmez; spec §8.4-4'ten bilerek sapma, aşağıda Not 8).

Satır numaraları `7960eba3`'ündür. Görev 3–5 aynı servis dosyasını değiştirdiği için kayarlar; Edit bağlamı aşağıdaki alıntılarla bulunur.

**Bağımlılık:** Görev 2'nin `CustomerBalanceScope.PersonPool` alanı (`bool PersonPool = false`, JSON `personPool`) bu dalda olmalı. `PersonPoolOf` onu okur, testlerin `ScopeJsonAt(…, personPool: true)` gövdesi de onunla ayrıştırılır. Görev 1'in sahip kuralı test üyelerini etkilemez: `MakeCustomer` `"twitch"` + kullanıcı adı, `BuyerAsync` `"tiktok"` + kullanıcı adı kurar, ikisi de `IsServerAcceptable`'dan geçer.

**Not (spec ↔ kod) — bu görev:**
1. **Satır kaydı (iskelet fark 1).** Spec'in atıfları bugünkü kodda şöyle:

   | Spec | Bugün |
   |---|---|
   | yerel planın sorusu `PaymentRequestService.cs:760-768` | `PromptOf` `:1075-1095` |
   | kapı `:738` | `:1007-1008` |
   | sunucu izlerinin sorusu `:1127-1131` | `:1510-1514` |
   | param yorumu `:71-73` | `:79-81` |
   | `WithOtherRowNote` `:185-189` | `:240-244` |
   | `PersonJob.cs:75-80` | aynı |
   | `PersonJob.cs:145-146` | aynı |

   Spec'in "`:125-127`" belge yorumu belirsiz; iskelet fark 13 uygulanır. `RefundsToOtherRow`'u anlatan üç yer kipe bağlı anlatıma geçer: `PaymentRequestService.cs:79-81`, `PaymentResultPresenter.cs:152-155`, `PersonJob.cs:212-214`.
2. **İki yerel plan kurulum yeri (fark 10; FARK-d2).** Spec yalnız `PromptOf`'u bilir. Satış senkronunun eklediği `WithLocalPlan`, mod On'da sunucunun `already-requested` sorusuna yerel planı katar ve `RefundsToOtherRow = plan.RefundsToOtherRow` yazar. O soruda da `scope` çağrısı yapılmamıştır (kira bakiyeden önce istenir). İkisi açıkça `PersonPool = null` alır. `PaymentPrompt`'a konumsal parametre eklenmez: bütün `new PaymentPrompt(…)` çağrıları etkilenirdi. `AlsoConfirms` deseninde `init` özellik eklenir, varsayılan `false`. Bu alanı kurmayan sorular ve var olan testler bugünkü metni korur.
3. **Karışık izler (fark 11).** Sunucu izlerinin `personPool` değeri yalnız akış ortasındaki `enable/disable` yarışında farklı olabilir. Hepsi aynıysa o değer kullanılır, değilse `null` (iki kipte de doğru nötr metin). 204 yanıtı iz üretmez ve kipe katılmaz. İz yoksa soru da yoktur.
4. **Satır kipinde yerel planın not metni nötre döner (fark 12, spec kazanır).** Spec §6.1 girişi, satır kipinde değişen şeyleri (a), (e), (f) olarak sayar. §6.1-d ise yerel planın sorusunda değeri "her zaman `null`" ister ("iki kipte de doğru nötr metin"). Spec'in açık tablosu uygulanır: yerel planın birleştirme sorusunun notu bugün de " Not: … iade o hesaba döner." olur, "bu istekte kullanılmaz" kuyruğu düşer. Değişen yalnız metindir; para davranışı aynıdır. Bunun sonucu olarak var olan bir test satırı **bilerek değişir**: `PaymentRequestServiceTests.Person.cs:164` (`Birlesen_kisinin_parali_iki_isi_sorulur_onayda_kaybeden_iade_edilir_kalan_revize_edilir`) bugün `.And.Contain("iade o hesaba döner, bu istekte kullanılmaz", …)` der. Yeni metinde iddia nötr nottur. Bu satır iskeletin "Satır kipi regresyonu" bölümündeki "bilerek değişen var olan test satırları" listesindedir. Sunucu izlerinin sorusu satır kipinde ve eski sunucuda bugünkü metni aynen verir: `personPool` `false` gelir ya da alan hiç gelmez, ikisi de `false` sayılır. Bu metin değişikliği spec'in iki satırını bayatlatır: §6.1 girişi (l.404, "bu üçü dışında bugünkü gibi çalışır") ve §7.1 adım 6 (l.496, "Satır kipinde Wh = #506 davranışı + Ayır onayı + kart düzeltmesi + sahip kuralı"). Görev 9 Adım 7.6/7.7 bu iki spec satırına nötr notu ekler (bu görev spec'e dokunmaz); fark iskelet fark 12'de kayıtlı, saha notlarının 3. maddesi Burak'a ayrıca bildirir.
5. **§7.5 / M3 sıra güvenliği: kod yok.** #506'nın notu satır kipinde doğrudur. Havuz kipi yalnız bütün aktif PC'ler Wh+ iken açılır (§7.2, S2'nin `enable` koşul 1). Notun yanlış görüneceği tek durum, kapıdan sonra dönen ve yalnız #506 taşıyan bir PC'dir. Bunun saha denemesi Görev 9'un notundadır (§8.5 karışık filo).
6. **Önbellek yok.** Önceki akışların `scope` yanıtındaki `personPool` serviste TUTULMAZ (spec §6.1-d, §14 #28): `enable`/`disable` oturum ortasında kipi değiştirebilir. Bu bir testle kilitlenir. Aynı servis örneğinde başka bir kişinin akışı `personPool: true` görmüş olsa da yerel planın sorusu `null` kalır.
7. **Spec §6.1-g yalnız yorumdur.** `PersonScopePlan`'ın kalan iş sıralaması (`PersonJob.cs:112-118`) AYNEN kalır; yalnız gerekçe paragrafı güncellenir. Bu adımın kırmızı testi yoktur: derleme ve R-Kişi regresyonu korur.
8. **§8.4-4 ↔ kod: iki Core testi bilerek değişmez (iskelet fark 25).** Spec §8.4-4 (l.616), `PersonScopePlanTests` ve `PaymentJobStoreContractTests`'in "kip durumuyla genişletilmesini" ister. Ama `PersonScopePlan` Core'dadır ve kipi bilmez. Plan sunucu çağrısından önce kurulur (§6.1-d). `RefundsToOtherRow` (`PersonJob.cs:145`) kipten bağımsız bir olgudur ve hesabı değişmez. Kipe bağlı olan yalnız metindir. Spec'in maddesini App tarafındaki testler karşılar: `PaymentResultPresenterTests` (üç durum + `[Theory]`) ve `PaymentRequestServiceTests.Person`/`.PoolNote`. `PersonScopePlanTests.cs:63` ve `PaymentJobStoreContractTests.cs:419` bilerek değiştirilmez; R-Kişi regresyonu onları korur.

Sahip sorusu adayı: yok.

- [ ] **Adım 1: Sunum testlerini yaz (kırmızı)**

`OrderDeck.Tests/Services/PaymentResultPresenterTests.cs`'te `Uzak_dusumler_gitmis_istek_diye_anlatilmaz` testinin kapanışından sonra eklenir. Edit bağlamı şu bloktur:

```csharp
            .And.EndWith("iade o hesaba döner, bu istekte kullanılmaz.")
            .And.NotContain("istek gitmiş");
    }
```

Bu bloğun hemen altına eklenecek kod:

```csharp

    // ── Birleştirme notunun üç durumu (spec 2026-10-11 §6.1-d, M3; W-havuz) ─────────────────────────────
    // Var olan iki not testi (yukarıda) PersonPool vermez: varsayılan false bugünkü metni korur.

    private const string SatirKipiNotu =
        " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner, bu istekte kullanılmaz.";

    private const string NotrNot =
        " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner.";

    /// <summary>Yerel biçimli birleştirme sorusu (iki teslim edilmiş istek, başka satırın düşümü iade ediliyor).</summary>
    private static PaymentPrompt YerelBirlestirme(bool? personPool)
        => new PaymentPrompt(PersonScopePromptKind.Merge, "session:s1",
            new[] { Istek(50m, 60), Istek(150m) }, "t", RefundsToOtherRow: true) { PersonPool = personPool };

    [Fact] // §6.1-d: havuz kipinde iade kişinin bakiyesine döner ve birleşik istekte kullanılır — not yok
    public void Birlestirme_notu_havuz_kipinde_yok()
    {
        var (title, message) = PaymentResultPresenter.PromptText(YerelBirlestirme(true), Simdi);

        title.Should().Be("Birden çok ödeme isteği");
        message.Should().EndWith("Önceki düşümler iade edilir, kişinin toplamı tek istekte yeniden istenir.")
            .And.NotContain("Not:", "havuz kipinde iade bu istekte kullanılır; söylenecek bir istisna yok");
    }

    [Fact] // §6.1-d: kip bilinmiyorsa (yerel planın sorusu) iki kipte de doğru nötr not
    public void Birlestirme_notu_bilinmiyorsa_notr_metin()
    {
        var (_, message) = PaymentResultPresenter.PromptText(YerelBirlestirme(null), Simdi);

        message.Should().EndWith(
                "Önceki düşümler iade edilir, kişinin toplamı tek istekte yeniden istenir." + NotrNot)
            .And.NotContain("bu istekte kullanılmaz",
                "havuz kipinde iade birleşik istekte kullanılır; nötr not bunun tersini iddia etmez");
    }

    [Fact] // §6.1-d: satır kipi (ve alanı göndermeyen eski sunucu) bugünkü metni aynen gösterir
    public void Birlestirme_notu_satir_kipinde_bugunku_metin()
    {
        var (_, message) = PaymentResultPresenter.PromptText(YerelBirlestirme(false), Simdi);

        message.Should().EndWith(
            "Önceki düşümler iade edilir, kişinin toplamı tek istekte yeniden istenir." + SatirKipiNotu);
    }

    [Theory] // §6.1-d: sunucunun bulduğu ayrı düşümlerin sorusunda da aynı tablo
    [InlineData(true, "")]
    [InlineData(false, SatirKipiNotu)]
    [InlineData(null, NotrNot)]
    public void Uzak_dusumlerde_birlestirme_notu_kipe_gore(bool? personPool, string not)
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Merge, "session:s1",
            Array.Empty<PriorPaymentRequest>(), "t", RefundsToOtherRow: true,
            RemoteDeductions: new[] { 40m, 210m }) { PersonPool = personPool };

        var (_, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        message.Should().Be(
            "Sunucuda bu kişinin bu yayın için 2 ayrı bakiye düşümü bulundu (40,00 TL, 210,00 TL); " +
            "müşteriye gidip gitmedikleri bilinmiyor. Tek istekte birleştirilsin mi? " +
            "En eski düşüm kalır, ötekiler iade edilir; kişinin toplamı tek istekte yeniden istenir." + not);
    }

    [Fact] // §6.1-d: iade başka hesabın düşümüne dokunmuyorsa kip ne olursa olsun not yok
    public void Iade_yoksa_kipten_bagimsiz_not_yok()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Merge, "session:s1",
            new[] { Istek(50m, 60), Istek(150m) }, "t") { PersonPool = null };

        var (_, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        message.Should().EndWith("Önceki düşümler iade edilir, kişinin toplamı tek istekte yeniden istenir.")
            .And.NotContain("Not:");
    }
```

(`PersonScopePromptKind` için `using OrderDeck.Core.Storage.Repositories;` dosyada zaten var, `:5`.)

- [ ] **Adım 2: Koştur — beklenen FAIL (derleme)**

Run (PowerShell): `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentResultPresenterTests"`

Expected: FAIL, derleme hatası. Her nesne başlatıcı için CS0117 `'PaymentPrompt' does not contain a definition for 'PersonPool'`.

- [ ] **Adım 3: En küçük uygulama — `PaymentPrompt.PersonPool` ve not tablosu**

`OrderDeck.App/Services/PaymentRequestService.cs`'te `PaymentPrompt` gövdesine eklenir. Değişecek bugünkü blok (`:98-106`):

```csharp
    public IReadOnlyList<string> AlsoConfirms { get; init; } = [];

    /// <summary>"Evet"in onayladığı bütün belirteçler: <see cref="Token"/> ve <see cref="AlsoConfirms"/>. Onaylayan her
```

Yeni hâli:

```csharp
    public IReadOnlyList<string> AlsoConfirms { get; init; } = [];

    /// <summary>
    /// Lisansın bakiye kipi, birleştirme notu (<see cref="RefundsToOtherRow"/>) için (spec 2026-10-11 §6.1-d, M3):
    /// <c>false</c> = satır kipi ya da eski sunucu (iade o hesaba döner, bu istekte kullanılmaz — bugünkü not);
    /// <c>true</c> = havuz kipi (iade kişinin bakiyesine döner, birleşik istekte kullanılır — not yok);
    /// <c>null</c> = bilinmiyor (iki kipte de doğru nötr not).
    /// <para>Kaynak yalnız aynı akışta alınan <c>GET scope</c> yanıtlarıdır (sunucu izlerinin sorusu). Yerel planın sorusu
    /// hiçbir sunucu çağrısından önce kurulduğu için her zaman <c>null</c>dır; önceki yanıtlardan bellekte değer TUTULMAZ:
    /// <c>enable</c>/<c>disable</c> oturum ortasında kipi değiştirebilir, bayat değer yanlış not gösterirdi. Varsayılan
    /// <c>false</c>: bu alanı kurmayan sorular (kiranın uzak sorusu, miras işin kapsam sorusu) zaten not taşımaz.</para>
    /// </summary>
    public bool? PersonPool { get; init; } = false;

    /// <summary>"Evet"in onayladığı bütün belirteçler: <see cref="Token"/> ve <see cref="AlsoConfirms"/>. Onaylayan her
```

`OrderDeck.App/Services/PaymentResultPresenter.cs`'te `WithOtherRowNote` değişir. Bugünkü hâli (`:240-244`):

```csharp
    private static string WithOtherRowNote(string message, PaymentPrompt prompt)
        => prompt.RefundsToOtherRow
            ? message + " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; " +
              "iade o hesaba döner, bu istekte kullanılmaz."
            : message;
```

Yeni hâli:

```csharp
    /// <summary>Birleştirme notu, kipe göre (spec 2026-10-11 §6.1-d tablosu). Not yalnız iade başka bir hesabın
    /// düşümüne dokunuyorsa (<see cref="PaymentPrompt.RefundsToOtherRow"/>) kurulur. <see cref="PaymentPrompt.PersonPool"/>
    /// <c>false</c> (satır kipi, eski sunucu): bugünkü metin, iade o hesapta kalır ve bu istekte kullanılmaz.
    /// <c>true</c> (havuz kipi): not yok, iade kişinin bakiyesine döner ve birleşik istekte kullanılır.
    /// <c>null</c> (bilinmiyor; yerel planın sorusu): iki kipte de doğru nötr metin.</summary>
    private static string WithOtherRowNote(string message, PaymentPrompt prompt)
        => !prompt.RefundsToOtherRow ? message : prompt.PersonPool switch
        {
            false => message + " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; " +
                     "iade o hesaba döner, bu istekte kullanılmaz.",
            true => message,
            null => message + " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner.",
        };
```

(`bool?` üzerindeki `switch` üç kolla tamdır, CS8509 çıkmaz. İki çağrı yeri değişmez: uzak düşümler `:215`, yerel `:237`.)

- [ ] **Adım 4: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentResultPresenterTests"`

Expected: PASS. Bu sınıfın bütün testleri yeşil olmalı; `Birlestirme_baska_hesabin_dusumunu_iade_ediyorsa_soyler` ve `Uzak_dusumler_gitmis_istek_diye_anlatilmaz` de değişmeden geçer, çünkü varsayılan `false` bugünkü metni verir.

- [ ] **Adım 5: Servis testlerini yaz (kırmızı)**

(a) `OrderDeck.Tests/Services/PaymentRequestServiceTests.Person.cs`, `ScopeJsonAt`. Bugünkü hâli (`:62-67`):

```csharp
    private static string ScopeJsonAt(Guid tx, decimal applied, decimal total, DateTimeOffset at) => $$"""
        {"transactionId":"{{tx}}",
         "appliedAmount":{{applied.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
         "productTotal":{{total.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
         "createdAt":"{{at:O}}"}
        """;
```

Yeni hâli:

```csharp
    /// <summary>Kapsam ucunun gövdesi. <paramref name="personPool"/> null → alan yok: S1 öncesi ya da eski sunucu biçimi,
    /// bugünkü gövdenin baytı baytına aynısı. Dolu → <c>"personPool":true|false</c>: S1'den beri satır kipinde false,
    /// havuz kipinde true (spec 2026-10-11 §4.3, §6.1-d).</summary>
    private static string ScopeJsonAt(
        Guid tx, decimal applied, decimal total, DateTimeOffset at, bool? personPool = null)
    {
        var body = $$"""
            {"transactionId":"{{tx}}",
             "appliedAmount":{{applied.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
             "productTotal":{{total.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
             "createdAt":"{{at:O}}"
            """;
        var pool = personPool is { } p ? ",\"personPool\":" + (p ? "true" : "false") : "";
        return body + pool + "}";
    }
```

Var olan çağrılar (`:512`, `:539-540`, `:1175`) aynı gövdeyi üretir: ham dizgi kapanıştan önceki satır sonunu içermez, `body + "" + "}"` eski `…"createdAt":"…"}` ile aynıdır.

(b) Aynı dosyada var olan testin iddiası değişir (Not 4; **bilerek değişen var olan test satırı**). Bugünkü `:163-165`:

```csharp
                .And.Contain("Önceki düşümler iade edilir")
                .And.Contain("iade o hesaba döner, bu istekte kullanılmaz", "düşümler farklı satırlarda")
                .And.NotContain("Fazladan", "ayrı ürünler için yapılmış düşüm fazla değildir");
```

Yeni hâli:

```csharp
                .And.Contain("Önceki düşümler iade edilir")
                .And.EndWith(" Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner.",
                    "düşümler farklı satırlarda; yerel planın sorusu kipi bilmez — nötr not (spec 2026-10-11 §6.1-d)")
                .And.NotContain("bu istekte kullanılmaz", "havuz kipinde yanlış olurdu; yerel plan kipi bilmez")
                .And.NotContain("Fazladan", "ayrı ürünler için yapılmış düşüm fazla değildir");
```

(c) Oluştur: `OrderDeck.Tests/Services/PaymentRequestServiceTests.PoolNote.cs`

```csharp
using System;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── Birleştirme notunun kipi (ortak bakiye spec 2026-10-11 §6.1-d, M3, §8.4-4; W-havuz) ─────────────────────
//
// Not yalnız iade başka bir satırın düşümüne dokunuyorsa (RefundsToOtherRow) çıkar; metnini PaymentPrompt.PersonPool
// seçer. Sunucu izlerinin sorusunda kaynak aynı akışın scope yanıtlarıdır. Hepsi aynıysa o değer, karışıksa null; eski
// sunucu alanı göndermez, değer false olur. Yerel planın sorusu sunucu çağrısından önce kurulur: her zaman null, önceki
// akışların yanıtı bellekte tutulmaz.
public partial class PaymentRequestServiceTests
{
    private const string PoolNoteNotrNot =
        " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner.";

    private const string PoolNoteSatirKipiNotu =
        " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner, bu istekte kullanılmaz.";

    /// <summary>İki üyeli kişi; sunucu kapsamda iki ayrı üyede düşüm bulur (emsal
    /// <c>Kapsam_izi_iki_uyedeyse_sorulur_onayda_yenisi_iade_edilir</c>). İzlerin <c>personPool</c> alanı verilen
    /// değerlerle kurulur; null olan izde alan hiç yoktur (eski sunucu). Döner: onaysız ilk çağrının birleştirme sorusu.</summary>
    private async Task<PaymentRequestOutcome> IkiIzliSoruAsync(bool? izA, bool? izB)
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}");
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            var (txA, txB) = (Guid.NewGuid(), Guid.NewGuid());
            var t0 = DateTimeOffset.UtcNow.AddHours(-2);
            handler.ScopeResponseFor = id =>
                id == G(a) ? ScopeJsonAt(txA, 40m, 250m, t0, izA)
                : id == G(b) ? ScopeJsonAt(txB, 40m, 250m, t0.AddHours(1), izB)
                : null;

            var soru = await sut.RequestPaymentAsync(a, _ => 250m, T, "session:s1");

            soru.Result.Should().Be(PaymentRequestResult.MergeRequired);
            soru.Prompt!.RefundsToOtherRow.Should().BeTrue("izler tanım gereği farklı üyelerde");
            soru.Prompt.RemoteDeductions.Should().Equal(40m, 40m);
            handler.ReverseCalls.Should().BeEmpty("onaydan önce geri alma yok");
            _launcher.LaunchedUrls.Should().BeEmpty();
            return soru;
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-d: scope yanıtları havuz kipini söylüyor — iade birleşik istekte kullanılır, not yok
    public async Task Sunucu_izlerinin_sorusu_scope_personPool_true_ise_not_yok()
    {
        var soru = await IkiIzliSoruAsync(true, true);

        soru.Prompt!.PersonPool.Should().BeTrue("kaynak aynı akışın scope yanıtları — kesin bilgi");
        var (_, message) = PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now);
        message.Should().EndWith("kişinin toplamı tek istekte yeniden istenir.").And.NotContain("Not:");
    }

    [Fact] // §6.1-d: satır kipindeki sunucu personPool: false döner — bugünkü not aynen
    public async Task Sunucu_izlerinin_sorusu_satir_kipinde_bugunku_not()
    {
        var soru = await IkiIzliSoruAsync(false, false);

        soru.Prompt!.PersonPool.Should().BeFalse();
        PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now).Message
            .Should().EndWith(PoolNoteSatirKipiNotu);
    }

    [Fact] // §4.3 + §6.1-d: S1 öncesi sunucu scope yanıtında personPool alanını göndermez → DTO varsayılanı false → bugünkü not
    public async Task Eski_sunucu_izlerinde_alan_yok_bugunku_not()
    {
        var soru = await IkiIzliSoruAsync(null, null);

        soru.Prompt!.PersonPool.Should().BeFalse("DTO varsayılanı: alan yoksa satır kipi");
        PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now).Message
            .Should().EndWith(PoolNoteSatirKipiNotu);
    }

    [Fact] // Plan fark 11: izler farklı kip taşıyorsa (akış ortasında enable/disable) bilinmiyor — nötr not
    public async Task Karisik_izlerde_PersonPool_null()
    {
        var soru = await IkiIzliSoruAsync(true, false);

        soru.Prompt!.PersonPool.Should().BeNull("iki kipte de doğru metin seçilir");
        PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now).Message
            .Should().EndWith(PoolNoteNotrNot);
    }

    // §6.1-d (+ spec §14 #28): yerel planın sorusu hiçbir sunucu çağrısından önce kurulur → her zaman null. Aynı servis
    // örneğinde başka bir kişinin akışı havuz kipinin scope yanıtını (personPool: true) görmüş olsa da değer bellekte
    // tutulmaz: enable/disable oturum ortasında kipi değiştirebilir. Kurulum Birlesen_kisinin_parali_iki_isi_… ile aynı.
    [Fact]
    public async Task Yerel_planin_sorusunda_PersonPool_her_zaman_null()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}");
            var c = Uye(db, $"kisi-{Guid.NewGuid():N}");
            var d = Uye(db, $"kisi-{Guid.NewGuid():N}");
            var (sut, handler) = KisiSut(db);
            handler.RowBalances = new() { [G(a)] = 50m, [G(c)] = 50m };

            (await sut.OpenWhatsAppAsync(a, 100m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            (await sut.OpenWhatsAppAsync(c, 200m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            Kisi(db, a, c);

            // Başka bir kişinin (d) akışı havuz kipinin scope yanıtını görür: tek iz, benimsenir.
            var tx = Guid.NewGuid();
            handler.ScopeResponseFor = id =>
                id == G(d) ? ScopeJsonAt(tx, 40m, 250m, DateTimeOffset.UtcNow, personPool: true) : null;
            (await sut.OpenWhatsAppAsync(d, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            var sorgular = handler.ScopeQueries.Count;

            var soru = await sut.RequestPaymentAsync(a, _ => 300m, T, "session:s1");

            soru.Result.Should().Be(PaymentRequestResult.MergeRequired);
            soru.Prompt!.RemoteDeductions.Should().BeNull("yerel planın sorusu (PromptOf), sunucu izlerininki değil");
            soru.Prompt.RefundsToOtherRow.Should().BeTrue("kaybedenin düşümü kalan işin satırında değil");
            soru.Prompt.PersonPool.Should().BeNull("yerel plan kipi bilmez; önceki akışın personPool'u tutulmaz");
            handler.ScopeQueries.Should().HaveCount(sorgular, "yerel planın sorusu sunucu çağrısından önce döner");
            PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now).Message
                .Should().EndWith(PoolNoteNotrNot).And.NotContain("bu istekte kullanılmaz");
        }
        finally { Drop(db); }
    }
}
```

(Ad çakışması denetimi: `IkiIzliSoruAsync`, `PoolNoteNotrNot`, `PoolNoteSatirKipiNotu` sınıfta yok. Var olanlar ve Görev 3–5'in eklediği `Silinmis`, `YerelUye`, `GrubaAl`, `EsitlenmemisSut`, `TurSayaci` bunlarla çakışmaz. Yürütücü yazmadan önce `Grep "IkiIzliSoru|PoolNote" OrderDeck.Tests/Services` ile boş olduğunu doğrular.)

(d) `OrderDeck.Tests/Sync/PaymentLeaseTests.cs`'e eklenir. Edit bağlamı `Yerel_teslim_ve_uzak_oncul_Hayir_mesaj_yok_yeni_kira_yok`'un kapanışıdır:

```csharp
        FinishOutcomes().Should().Equal(new[] { "delivered" }, "409 kira yazmaz: kapatılacak kira yok");
    }
```

Bu bloğun hemen altına:

```csharp

    [Fact] // Ortak bakiye §6.1-d (plan fark 10): uzak soruya katılan yerel planın notu kipi bilmez — PersonPool null
    public async Task Uzak_soruya_katilan_yerel_planin_notu_kipten_bagimsiz_null()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, $"alici-{Guid.NewGuid():N}", 500m);
        var own = await DeliveredLocallyAsync(pc, buyer.Customer, "cumulative");
        EnqueueAlreadyRequested(Prior(own, "PC-A", 550m, 550m, TenOClock));
        var dialogs = new FakeDialogService();

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        outcome.Prompt!.AlsoConfirms.Should().ContainSingle("yerel planın belirteci aynı soruya katıldı (WithLocalPlan)");
        outcome.Prompt.PersonPool.Should().BeNull("kira bakiyeden önce istenir; scope yanıtı yok, kip bilinmiyor");
        dialogs.Confirmations.Should().ContainSingle().Which.Message.Should()
            .NotContain("Not:", "tekrar gönderme sorusu, iade yok (RefundsToOtherRow false)");
    }
```

- [ ] **Adım 6: Koştur — beklenen FAIL (davranış)**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Sunucu_izlerinin|FullyQualifiedName~PaymentRequestServiceTests.Eski_sunucu_izlerinde|FullyQualifiedName~PaymentRequestServiceTests.Karisik_izlerde|FullyQualifiedName~PaymentRequestServiceTests.Yerel_planin_sorusunda|FullyQualifiedName~PaymentRequestServiceTests.Birlesen_kisinin_parali_iki_isi|FullyQualifiedName~PaymentLeaseTests.Uzak_soruya_katilan"`

Expected: derleme temiz. `PersonPool` Adım 3'ten, `CustomerBalanceScope.PersonPool` Görev 2'den gelir. Yedi testten beşi FAIL:
- `Sunucu_izlerinin_sorusu_scope_personPool_true_ise_not_yok`: `PersonPool` true bekleniyor, false geliyor (izlerin sorusu alanı kurmuyor, varsayılan).
- `Karisik_izlerde_PersonPool_null`: null bekleniyor, false geliyor.
- `Yerel_planin_sorusunda_PersonPool_her_zaman_null`: null bekleniyor, false geliyor (`PromptOf` varsayılanı).
- `Birlesen_kisinin_parali_iki_isi_sorulur_onayda_kaybeden_iade_edilir_kalan_revize_edilir`: mesaj hâlâ "…bu istekte kullanılmaz." ile bitiyor, nötr not bekleniyor.
- `Uzak_soruya_katilan_yerel_planin_notu_kipten_bagimsiz_null`: null bekleniyor, false geliyor (`WithLocalPlan` `remote`'un varsayılanını taşıyor).

İki koruma testi şimdiden PASS: `Sunucu_izlerinin_sorusu_satir_kipinde_bugunku_not` ve `Eski_sunucu_izlerinde_alan_yok_bugunku_not`. Varsayılan `false` bugünkü notu verir; uygulamadan sonra da yeşil kalmalılar.

- [ ] **Adım 7: En küçük uygulama — üç kurulum yeri ve `PersonPoolOf`**

`OrderDeck.App/Services/PaymentRequestService.cs`:

(a) `WithLocalPlan`. Bugünkü (`:1060-1063`):

```csharp
                NewTotal = merge ? null : remote.NewTotal,
                PriorHadBalance = !merge && remote.PriorHadBalance,
                AlsoConfirms = [plan.Token],
            };
```

Yeni:

```csharp
                NewTotal = merge ? null : remote.NewTotal,
                PriorHadBalance = !merge && remote.PriorHadBalance,
                AlsoConfirms = [plan.Token],
                // Ortak bakiye §6.1-d: yerel plan, sunucunun scope yanıtından önce kurulur; kip bilinmez, nötr not.
                PersonPool = null,
            };
```

(b) `PromptOf`. Bugünkü (`:1084-1095`):

```csharp
        return new(plan.Kind, scopeKey,
            prompts.Select(j => new PriorPaymentRequest(
                j.PromptNet,
                j.PromptAt is long at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
                j.HasUnconfirmedSend)).ToList(),
            plan.Token,
            plan.UnsentDeductions,
            plan.RefundsToOtherRow,
            plan.RefundsDeductions,
            NewTotal: changed ? totalAmount : null,
            PriorHadBalance: changed && priorBalance > 0m);
    }
```

Yeni:

```csharp
        return new(plan.Kind, scopeKey,
            prompts.Select(j => new PriorPaymentRequest(
                j.PromptNet,
                j.PromptAt is long at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
                j.HasUnconfirmedSend)).ToList(),
            plan.Token,
            plan.UnsentDeductions,
            plan.RefundsToOtherRow,
            plan.RefundsDeductions,
            NewTotal: changed ? totalAmount : null,
            PriorHadBalance: changed && priorBalance > 0m)
        {
            // Ortak bakiye §6.1-d: yerel planın sorusu hiçbir sunucu çağrısından önce kurulur; kip bilinmez, nötr not.
            // Önceki akışların scope yanıtı bellekte tutulmaz: enable/disable oturum ortasında kipi değiştirebilir.
            PersonPool = null,
        };
    }
```

(c) Sunucu izlerinin sorusu. Bugünkü (`:1507-1514`):

```csharp
            // İzler tanım gereği farklı üyelerde: iade edilen düşüm kendi satırına döner. Sunucu teslim
            // bilgisini bilmez — izler "gitmiş istek" diye değil, bulunan bakiye düşümleri diye anlatılır
            // (biri gönderilememiş de olabilir; eski sürüm bilgisayarın LaunchFailed/SendPending'i).
            if (confirmedTokens?.Contains(token) != true)
                return new(job, new PaymentPrompt(PersonScopePromptKind.Merge, job.ScopeKey,
                    Array.Empty<PriorPaymentRequest>(), token, RefundsToOtherRow: true,
                    RemoteDeductions: traces.OrderBy(t => t.Scope.CreatedAt)
                        .Select(t => t.Scope.AppliedAmount).ToList()));
```

Yeni:

```csharp
            // İzler tanım gereği farklı üyelerde. İade edilen düşüm satır kipinde kendi satırına, havuz kipinde
            // kişinin bakiyesine döner; notu bu akışın scope yanıtlarındaki kip seçer (PersonPoolOf; ortak bakiye
            // §6.1-d). Sunucu teslim bilgisini bilmez: izler "gitmiş istek" diye değil, bulunan bakiye düşümleri diye
            // anlatılır (biri gönderilememiş de olabilir; eski sürüm bilgisayarın LaunchFailed/SendPending'i).
            if (confirmedTokens?.Contains(token) != true)
                return new(job, new PaymentPrompt(PersonScopePromptKind.Merge, job.ScopeKey,
                    Array.Empty<PriorPaymentRequest>(), token, RefundsToOtherRow: true,
                    RemoteDeductions: traces.OrderBy(t => t.Scope.CreatedAt)
                        .Select(t => t.Scope.AppliedAmount).ToList())
                {
                    PersonPool = PersonPoolOf(traces),
                });
```

(d) `PersonPoolOf`, `ReconcilePersonScopeAsync`'in hemen ardına. Bugünkü bitiş (`:1561-1563`):

```csharp
        _jobs.AdoptRemoteResult(job.Id, scope.TransactionId, scope.AppliedAmount, scope.ProductTotal);
        return new(_jobs.Get(job.Id)!);
    }
```

Yeni:

```csharp
        _jobs.AdoptRemoteResult(job.Id, scope.TransactionId, scope.AppliedAmount, scope.ProductTotal);
        return new(_jobs.Get(job.Id)!);
    }

    /// <summary>Sunucu izlerinin sorusunun kipi (ortak bakiye spec 2026-10-11 §6.1-d). İzlerin hepsi aynı
    /// <c>personPool</c>'u taşıyorsa o değer, değilse <c>null</c> (iki kipte de doğru nötr not). İzler aynı lisansın aynı
    /// akışta alınmış scope yanıtlarıdır; değerler yalnız akış ortasındaki <c>enable</c>/<c>disable</c> yarışında
    /// karışır. Alanı göndermeyen eski sunucunun izi <c>false</c> taşır (DTO varsayılanı) ve bugünkü notu verir.</summary>
    private static bool? PersonPoolOf(IReadOnlyList<(string MemberId, CustomerBalanceScope Scope)> traces)
        => traces.Select(t => t.Scope.PersonPool).Distinct().Count() == 1 ? traces[0].Scope.PersonPool : null;
```

(`traces` yerelde `List<(string MemberId, CustomerBalanceScope Scope)>` olarak tanımlı, `IReadOnlyList<…>`'e örtük dönüşür. Çağrı yerinde `traces.Count > 1` olduğu için `traces[0]` her zaman vardır. `CustomerBalanceScope` için `using OrderDeck.Licensing.Api.Models;` dosyada zaten var, `:11`.)

- [ ] **Adım 8: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests.Sunucu_izlerinin|FullyQualifiedName~PaymentRequestServiceTests.Eski_sunucu_izlerinde|FullyQualifiedName~PaymentRequestServiceTests.Karisik_izlerde|FullyQualifiedName~PaymentRequestServiceTests.Yerel_planin_sorusunda|FullyQualifiedName~PaymentRequestServiceTests.Birlesen_kisinin_parali_iki_isi|FullyQualifiedName~PaymentLeaseTests.Uzak_soruya_katilan|FullyQualifiedName~PaymentResultPresenterTests"`

Expected: hepsi PASS.

- [ ] **Adım 9: Derleme — 0 uyarı**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`

Expected: `0 Warning(s)`, `0 Error(s)`. `PersonPoolOf` kullanılıyor, `switch` tam.

- [ ] **Adım 10: Regresyon (var olan testler DEĞİŞTİRİLMEDEN; tek istisna Adım 5b'nin `.Person.cs:164` satırı)**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CorrectedPaymentRequestsTests|FullyQualifiedName~PaymentPersonViewModelTests"`
Expected: PASS (R-Ödeme). Var olan sunucu izi testleri (`Kapsam_izi_iki_uyedeyse_sorulur_onayda_yenisi_iade_edilir`, `Kapsam_izi_baska_uyedeyse_…`, `Teslimli_taze_isin_izi_baska_uyedeyse_…`) `ScopeJsonAt`'ın alansız gövdesiyle değişmeden geçer. `ScopeDeduction` testleri miras işin sorusunu (`LegacyScopeDeductionPrompt`, not yok) değişmeden geçer.

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentOwnerRuleTests|FullyQualifiedName~PaymentPersonTests|FullyQualifiedName~PaymentPersonGateTests|FullyQualifiedName~PersonScopePlanTests|FullyQualifiedName~PaymentJobStoreContractTests|FullyQualifiedName~PaymentJobRepositoryTests|FullyQualifiedName~CustomerRepositoryTests"`
Expected: PASS (R-Kişi). `RefundsToOtherRow` hesabı değişmedi; `PaymentJobStoreContractTests.cs:419` aynen yeşil.

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentLeaseTests|FullyQualifiedName~PaymentLeaseServerRulesTests|FullyQualifiedName~RemotePaymentRecordsTests|FullyQualifiedName~TwoPcSalesConvergenceTests"`
Expected: PASS (R-ModOn, birkaç dakika sürer). W3-09'un tek diyalog testleri (`Yerel_teslim_ve_uzak_oncul_tek_diyalog_onayda_mesaj` tam metin eşitliğiyle) değişmez: tekrar gönderme sorusunda not yok.

- [ ] **Adım 11: Commit (davranış)**

```bash
git add OrderDeck.App/Services/PaymentRequestService.cs \
  OrderDeck.App/Services/PaymentResultPresenter.cs \
  OrderDeck.Tests/Services/PaymentResultPresenterTests.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.Person.cs \
  OrderDeck.Tests/Services/PaymentRequestServiceTests.PoolNote.cs \
  OrderDeck.Tests/Sync/PaymentLeaseTests.cs
git commit -F - <<'MSG'
feat(balance): birleştirme notunu kipe bağla (PersonPool; yerel planda null)

#506'nın "iade o hesaba döner, bu istekte kullanılmaz" notu satır kipinde
doğru, havuz kipinde yanlış: orada iade kişinin bakiyesine döner ve birleşik
istekte kullanılır (ortak bakiye §6.1-d, M3). PaymentPrompt'a init özellik
PersonPool (bool?, varsayılan false) eklenir. Notu bu değer seçer: false
bugünkü metin, true not yok, null iki kipte de doğru nötr metin.

Sunucu izlerinin sorusu değeri aynı akışın scope yanıtlarından alır. Hepsi
aynıysa o, karışıksa null; eski sunucu alanı göndermez, false sayılır.
Yerel planın sorusu (PromptOf ve mod On'daki WithLocalPlan) sunucu
çağrısından önce kurulur, değeri her zaman null'dır. Önceki yanıt bellekte
tutulmaz, çünkü enable/disable oturum ortasında kipi değiştirebilir.
Satır kipinde yerel planın notu bu yüzden nötr metne döner (plan fark 12;
para davranışı aynı). Kişi testindeki not iddiası buna göre güncellendi.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

- [ ] **Adım 12: Belge yorumlarını kipe bağla (kod değişmez)**

(a) `OrderDeck.App/Services/PaymentRequestService.cs`, `RefundsToOtherRow` param yorumu. Bugünkü (`:79-81`):

```csharp
/// <param name="RefundsToOtherRow">Birleştirmenin iade edeceği düşümlerden biri başka bir satırın
/// bakiyesinden yapılmış: bakiye satır başına tutulduğu için iade o satırda kalır, bu istekte
/// kullanılmaz (ayrı kartken ayrı ürünler için yapılmış meşru düşüm de olabilir).</param>
```

Yeni:

```csharp
/// <param name="RefundsToOtherRow">Birleştirmenin iade edeceği düşümlerden biri başka bir satırın
/// bakiyesinden yapılmış (ayrı kartken ayrı ürünler için yapılmış meşru düşüm de olabilir). Satır kipinde
/// iade o satırda kalır, bu istekte kullanılmaz. Havuz kipinde kişinin bakiyesine döner ve birleşik istekte
/// kullanılır. Notu <see cref="PersonPool"/> belirler (spec 2026-10-11 §6.1-d). Satır kipi geçiş boyunca
/// sürdüğü için alan kalır.</param>
```

(Kayıt düzeyindeki `<param>` içinden kaydın üyesine `cref` mümkündür. Emsal: aynı bloktaki `<see cref="Requests"/>`, `:86`.)

(b) `OrderDeck.App/Services/PaymentResultPresenter.cs`, `PromptText` belgesi. Bugünkü (`:150-155`):

```csharp
    /// <para>Birleştirmede yalnız TESLİM edilmiş istekler "gitmiş" sayılır; para taşıyan ama hiç
    /// gönderilmemiş istek ayrıca söylenir (operatöre gitmemiş bir isteği gitmiş diye göstermemek
    /// için). "Fazladan düşülen" denmez: iki ayrı kartken ayrı ürünler için yapılmış düşümler de
    /// birleşir — iade edilen her zaman fazla değildir. İade başka bir satırın bakiyesine dönüyorsa
    /// (bakiye satır başına) bu da söylenir: o tutar bu istekte kullanılmaz. İade edilecek düşüm yoksa
    /// (parasız istekler birleşiyor) iade cümlesi hiç kurulmaz.</para>
```

Yeni:

```csharp
    /// <para>Birleştirmede yalnız TESLİM edilmiş istekler "gitmiş" sayılır; para taşıyan ama hiç
    /// gönderilmemiş istek ayrıca söylenir (operatöre gitmemiş bir isteği gitmiş diye göstermemek
    /// için). "Fazladan düşülen" denmez: iki ayrı kartken ayrı ürünler için yapılmış düşümler de
    /// birleşir — iade edilen her zaman fazla değildir. İade başka bir satırın düşümüne dokunuyorsa
    /// (<see cref="PaymentPrompt.RefundsToOtherRow"/>) notu kip belirler (<see cref="PaymentPrompt.PersonPool"/>,
    /// spec 2026-10-11 §6.1-d). Satır kipinde iade o satırda kalır ve bu istekte kullanılmaz (bugünkü not).
    /// Havuz kipinde kişinin bakiyesine döner ve birleşik istekte kullanılır (not yok). Kip bilinmiyorsa
    /// (yerel planın sorusu) iki kipte de doğru nötr not gösterilir. İade edilecek düşüm yoksa (parasız
    /// istekler birleşiyor) iade cümlesi hiç kurulmaz.</para>
```

(c) `OrderDeck.Core/Storage/Repositories/PersonJob.cs`, kalan iş paragrafı (spec §6.1-g). Bugünkü (`:75-80`):

```csharp
/// <para><b>Kalan iş:</b> önce para taşıyan, sonra en son teslim edilen, sonra belirlenimli sahibin
/// işi, sonra en eski (CreatedAt, Id). Sahibin el değmemiş işi para taşıyan ya da teslim edilmiş bir
/// işin önüne GEÇMEZ: bakiye satır başına tutulur; başka üyenin bakiyesinden düşmüş işi geri alıp
/// bakiyesi olmayan satırda yeniden uygulamak müşterinin indirimini yok ederdi. Eşitlikte sahibinki
/// kalır: iki bilgisayar aynı (WpfCustomerId, ScopeKey) anahtarında buluşur. Kalan iş kendi sahip
/// satırını korur. Kapsamın işi yoksa kalan iş de yoktur (onayda sahip satırda açılır).</para>
```

Yeni:

```csharp
/// <para><b>Kalan iş:</b> önce para taşıyan, sonra en son teslim edilen, sonra belirlenimli sahibin
/// işi, sonra en eski (CreatedAt, Id). Sahibin el değmemiş işi para taşıyan ya da teslim edilmiş bir
/// işin önüne GEÇMEZ (ortak bakiye spec 2026-10-11 §6.1-g). Sunucu kayıtlı anahtarı (A11) çapa satırı
/// düzeyinde karşılaştırır: düşüm yazıldığı satırın anahtarıdır, başka satırla oynatılamaz. Para taşıyan
/// işi geri alıp sahip satırda yeniden uygulamak havuz kipinde gereksiz para hareketi, satır kipinde
/// (bakiye satır başına) müşterinin indirim kaybı demek. Eşitlikte sahibinki kalır: iki bilgisayar aynı
/// (WpfCustomerId, ScopeKey) anahtarında buluşur. Kalan iş kendi sahip satırını korur. Kapsamın işi yoksa
/// kalan iş de yoktur (onayda sahip satırda açılır).</para>
```

(d) Aynı dosya, `RefundsToOtherRow` belgesi. Bugünkü (`:212-214`):

```csharp
    /// <summary>Birleştirme, kalan işin satırından BAŞKA bir satırdan yapılmış düşümü iade ediyor.
    /// Bakiye satır başına tutulur: iade o satıra döner, kalan işin revizyonu onu kullanamaz. İki ayrı
    /// kartken ayrı ürünler için yapılmış meşru düşümler de böyle görünür — operatör soruda görmeli.</summary>
```

Yeni:

```csharp
    /// <summary>Birleştirme, kalan işin satırından BAŞKA bir satırdan yapılmış düşümü iade ediyor. Bu kipten
    /// bağımsız bir olgudur: kaybedenin düşümü kalan işin satırında değil. Sonucu kipe bağlıdır. Satır kipinde
    /// iade o satıra döner, kalan işin revizyonu onu kullanamaz; havuz kipinde kişinin bakiyesine döner. Plan
    /// sunucuya gitmeden kurulduğu için kipi bilmez. Metnin kipe göre seçimi sunucunun scope yanıtıyla
    /// (<c>PaymentPrompt.PersonPool</c>, ortak bakiye spec 2026-10-11 §6.1-d) yalnız sunucu izlerinin sorusunda
    /// yapılır; bu planın sorusu nötr not gösterir. İki ayrı kartken ayrı ürünler için yapılmış meşru düşümler
    /// de böyle görünür; operatör soruda görmeli.</summary>
```

(Core, App'e başvurmaz; `PaymentPrompt` bu yüzden `cref` değil `<c>` ile anılır.)

- [ ] **Adım 13: Derleme + regresyon**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Expected: `0 Warning(s)`, `0 Error(s)` (Core da derlenir; `cref`'ler çözülür, CS1574 yok).

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonScopePlanTests|FullyQualifiedName~PaymentJobStoreContractTests|FullyQualifiedName~PaymentResultPresenterTests"`
Expected: PASS (yalnız yorum değişti).

- [ ] **Adım 14: Commit (belge)**

```bash
git add OrderDeck.App/Services/PaymentRequestService.cs \
  OrderDeck.App/Services/PaymentResultPresenter.cs \
  OrderDeck.Core/Storage/Repositories/PersonJob.cs
git commit -F - <<'MSG'
docs(balance): RefundsToOtherRow ve kalan iş gerekçesi yorumlarını kipe göre güncelle

"Bakiye satır başına tutulur, iade bu istekte kullanılmaz" anlatımı artık
yalnız satır kipinde doğru. RefundsToOtherRow'u anlatan üç yorum
(PaymentPrompt parametresi, PromptText belgesi, PersonScopePlan özelliği)
olguyu kipten ayırır: düşüm başka satırda. Ne anlama geldiğini
PaymentPrompt.PersonPool seçer (ortak bakiye §6.1-d).

PersonScopePlan'ın kalan iş kuralı aynen kalır; yalnız gerekçesi
güncellenir (§6.1-g). A11 çapa satırı düzeyinde karşılaştırıyor. Para
taşıyan işi geri alıp sahip satırda yeniden uygulamak havuz kipinde gereksiz
para hareketi, satır kipinde indirim kaybı demek. Kod değişmez.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 7: Ayır onayı (dağılım, [Ayır]/[Vazgeç], genel uyarı) (spec §6.1-e, K1, §5.2, ek sahip kararı 12.10.2026, §8.4-5)

**Files:**
- Oluştur: `OrderDeck.App/Formatting/CustomerIdentityText.cs`
- Değiştir: `OrderDeck.App/Services/IDialogService.cs` (`ConfirmAsync` belgesi + imza `:49-51`)
- Değiştir: `OrderDeck.App/Services/WpfDialogService.cs` (`ConfirmAsync` `:48-49`)
- Değiştir: `OrderDeck.App/Views/Drawers/MessageDrawer.xaml.cs` (özel ctor `:13-30`, `ForConfirm` `:32-34`, `ForNotice` `:36-38`)
- Değiştir: `OrderDeck.App/ViewModels/CustomerDetailViewModel.cs` (kimlik rozeti çağrısı `:207`; özel yardımcılar `:430-450` ve önlerindeki boş satır `:429` silinir — `CustomerIdentityText`'e taşınır)
- Değiştir: `OrderDeck.App/ViewModels/CustomerSearchViewModel.cs` (`using`'ler `:1-11`, alanlar `:17-22`, ctor `:118-134`, `UnmergeGroup` `:265-272`)
- Değiştir (test yardımcısı): `OrderDeck.Tests/Fakes/FakeDialogService.cs` (`ConfirmAsync` `:56-59`), `OrderDeck.Tests/TestHelpers/BalanceApiStub.cs` (özellikler `:36-37`'den sonra; panel GET kolu `:93`'ten önce)
- Test (oluştur): `OrderDeck.Tests/ViewModels/CustomerSearchUnmergeTests.cs`, `OrderDeck.Tests/App/MessageDrawerButtonTests.cs`
- Test (genişlet): `OrderDeck.Tests/Services/PaymentResultPresenterTests.cs` (bir test, `Onay_sorunun_butun_belirteclerini_yeniden_cagriya_verir` testinin kapanışından sonra — 7960eba3'te `:204-226`; Görev 6'nın eklemelerinden sonra kayar, test adıyla bul), `OrderDeck.Tests/App/CustomerSyncDiTests.cs` (bir test, `:148-157`'den sonra)
- Test (bilerek değişen tek var olan satır): `OrderDeck.Tests/ViewModels/PaymentPersonViewModelTests.cs:190`

`CustomerSearchDrawer.xaml` DEĞİŞMEZ (`:247-255`; `UnmergeGroupAsync`'ten üretilen komutun adı yine `UnmergeGroupCommand` — toolkit "Async" ekini atar). `AppHost.cs` DEĞİŞMEZ: `AddTransient<ViewModels.CustomerSearchViewModel>()` (`:354`) isteğe bağlı son parametreyi kayıtlı tekil `LicenseApiClient`'la (`:452`) doldurur; DI testi bunu kilitler. `CustomerRepository.UnmergeGroup` (`:608-616`) DEĞİŞMEZ. Görev iki parça, iki commit: **7a** onay altyapısı (düğme yazısı + `CustomerIdentityText`), **7b** VM + `BalanceApiStub` panel ucu + testler.

Bağımlılıklar: **Görev 2** (`CustomerBalanceMemberDto`, `CustomerBalanceDetailsResponse.Members`) — 7b onsuz derlenmez. **Görev 1** (`PaymentPerson.OwnerId` = GUID'li **ve** gönderilebilir üye; `ServerCustomerRule.MaxUsernameLength`) — sahip seçimini Görev 1 belirler. 7b'nin iki testi (`Gonderilemeyen_GUIDli_uye_sahip_olmaz_…`, `Yalniz_gonderilemeyen_GUIDli_uyeler_…`) Görev 1'in kuralını kilitler, onsuz derlenmez ve geçmez; öbür testler bugünkü kuralla da geçer (bütün öbür test üyeleri gönderilebilir `Platform`/`Username` taşır).

**Not (spec ↔ kod) — bu görev:**
- **Satır kaydı.** Spec'in `CustomerSearchViewModel.UnmergeGroup` `:258-265`'i bugün `:265-272` (içerik aynı); `LicenseApiClient.GetCustomerBalanceAsync` `:463-471` bugün `:880-888` (içerik aynı; 2xx değilse `ThrowMappedAsync` → 404 `ValidationException("Not Found")`, ağ/zaman aşımı `LicenseApiNetworkException`); `CustomerRepository.UnmergeGroup` `:616-622` bugün `:608-616`.
- **İskelet fark 14 uygulanır.** VM `LicenseApiClient`'a bağlı değil → isteğe bağlı son ctor parametresi `LicenseApiClient? api = null`. Null yalnız test/kompozisyonda (5 var olan kurulum değişmeden derlenir: `CustomerSearchViewModelTests.cs:60`, `:354`, `PaymentPersonViewModelTests.cs:55`, `MainShellViewCompositionTests.cs:231`, `PaymentLeaseTests.cs:1621` — 7960eba3 numarası; Görev 1/6 testleri bunun öncesine eklenir, satır kayar) ve bugünkü sorusuz yol; `UnmergeQuestionAsync` o zaman senkron tamamlanır, komut bugünkü gibi senkron biter.
- **İskelet fark 15 uygulanır (FARK-e2).** `ConfirmAsync` bugün yalnız [Evet] [Vazgeç] (`MessageDrawer.xaml.cs:26`). Spec [Ayır] [Vazgeç] ister → `ConfirmAsync(string message, string title, string confirmText = "Evet")`, `MessageDrawer.ForConfirm(…, string confirmText = "Evet")`. Tek var olan çağıran `PaymentResultPresenter.cs:137` değişmez ve "Evet" kalır (test kilitler). `IDialogService`'in öbür uygulayıcısı yalnız `FakeDialogService`; Moq ile sahtelenen yer yok (`git grep` Adım 2'de doğrular). `MessageBox` yolu (`Confirm`) değişmez.
- **İskelet fark 16 uygulanır.** Üye biçimi §6.1-e'ninki: canlı `"{Platform} @{tanıtıcı}: {N2 tr} TL"`, silinmiş `"(silinmiş hesap): {N2 tr} TL — ayırınca kullanılamaz"`, ayraç `" · "`, önce canlılar sonra silinmişler (her grup sunucunun `members[]` sırasıyla). Platform adı ve tanıtıcı `CustomerDetailViewModel`'in özel yardımcılarından (`PlatformLabel` `:442-450`, `IdentityHandle` `:438-439`) ortak `CustomerIdentityText`'e taşınır (kopya yok); YouTube'da tanıtıcı görünen addır ve çoğu zaman zaten "@" ile başlar → "@" eklenmez. Tutar `TrFormats.Currency` (`TrFormats.cs:16`, `"70,00 TL"`).
- **İskelet fark 17 uygulanır.** Spec "kartın GUID'li bir üyesi" → kişinin sahibi (`GetPaymentPerson(card.Primary.Id).OwnerId`): sunucuya ulaşabilen satır. `OwnerId` null → GUID'li (gönderilebilir) üye yok → sunucuda bakiye olamaz → çağrı yok, sorusuz (spec "GUID'li üye yoksa da öyle"). 404 (sahibin projeksiyonu yok), ağ hatası, 5xx ve `members` alanının yokluğu (S1 öncesi sunucu) "çağrı başarısız" sayılır → genel uyarı. Genel uyarının başlığı spec'te yok → onayla aynı "Grubu ayır". `take: 1`: `members[]` `take`'ten bağımsızdır (`PanelCustomerBalanceController.cs:112-114`), hareket listesi kullanılmaz; sunucu 1–200'ü kabul eder (`:83`).
- **İskelet fark 18 uygulanır (FARK-e3, ek sahip kararı 12.10.2026).** Spec §10'daki "Sahip kararı bekliyor — Ayır'da silinmiş hesabın parası" (ve §4.12 l.324, §5.2 l.373) bayattır: silinmiş hesapta para varken bütün canlı hesaplar ayrılırsa **Ayır SERBEST + uyarı**; onay penceresi silinmiş hesabın tutarını "(silinmiş hesap): X TL — ayırınca kullanılamaz" diye gösterir. Bu görev davranışı uygular (`Silinmis_hesapta_para_varken_ayir_serbest`); spec metninin güncellenmesi Görev 9'da.
- **Sahip sorusu 1 (SORU-e4), öneri (A) uygulanır.** Sunucunun `members[]`'inde olup bu kartta bulunmayan **canlı** üye: bakiyesi ≠ 0 → liste yerine genel uyarı (`UnmergeFallbackMessage`); 0 → listeden çıkar. Burak farklı karar verirse yalnız `UnmergeMessage` ve iki testi (`Kartta_olmayan_…`) değişir.
- **Kartta olup sunucu `members[]`'inde olmayan yerel satır** (GUID'siz ya da sunucunun bu kişide görmediği satır) listelenmez: liste sunucunun kişi görüşüdür (spec "`Members[]` yerel satırlara eşlenir" — eşleme yönü sunucudan yerele). "Ayırınca her hesap kendi bakiyesiyle kalır" cümlesi o satırlar için de doğrudur (K1: para taşınmaz). Yeni metin icat edilmez. Sunucuya gönderilmiş ama grubu henüz inmemiş kart üyesi de listelenmez; sahibin grubundaki tutarlar 0 ise soru çıkmaz (geçici pencere, para taşınmaz — K1; spec §5.2 "Arada sunucu yarım bir kişi görür"). Bu, Sahip sorusu 1'in ters yönüdür (iskelette tek satırla not edildi); Burak isterse sonraki karar yalnız `UnmergeMessage`'ı etkiler. Kod değişmez.
- **Nullable koruması.** Talimattaki `if (card?.GroupId is not { Length: > 0 } groupId) return;` satırı `if (card is null || card.GroupId is not { Length: > 0 } groupId) return;` olarak yazılır: anlam aynı, `card`'ın null olmadığı derleyiciye açıkça gösterilir (TWAE altında CS8604 riski yok).
- **Satır kipi.** Ayır onayı spec §6.1'in satır kipinde de değişen üç davranışından biridir (a, **e**, f). Sunucu `members[]`'i iki kipte de `S` üzerinden döner (`PanelCustomerBalanceController.cs:112-114` satır kipi, `:91-92` → `PoolDetailsAsync` havuz kipi); kip ayrımı yapılmaz (spec §6.1-e son madde). Ayırmanın kendisi, senkron gönderimi (tetikleyiciler) ve bakiye değişmez; gösterilen tutar okuma anınındır (bilgi amaçlı). Satır kipi: sunucuya ulaşılamazken ya da sunucu takıldığında Ayır'ın sorusu yeniden deneme politikası (`AppHost.cs:451`, `AddStandardResilienceHandler`: 3 yeniden deneme, üstel bekleme taban 2 sn, toplam zaman aşımı 30 sn) yüzünden ~30 sn'ye kadar gecikir; düğme o sürede pasif, ardından genel uyarı gelir (bugün Ayır anlıktır). Yükleniyor göstergesi spec'te yok, eklenmez. Bu not Görev 9'un saha/test notlarına da girmeli (çevrimdışı Ayır denemesi). Sınırlı bir zaman aşımı istenirse **Sahip sorusu 5**'tir (iskelet; öneri A: kabul): iptal jetonu eklenirse `UnmergeQuestionAsync`'in süzgeçsiz `catch`'i iptali de genel uyarıya çevirmeye devam etmeli.
- **Test dosyası eklemesi.** `OrderDeck.Tests/App/MessageDrawerButtonTests.cs` eklenir (dosya haritasının 7. satırında): düğme yazısı WPF görünümünde değişir ve başka hiçbir test onu görmez (sahte servis yalnız parametreyi kaydeder). STA kalıbı `MainShellViewCompositionTests.cs:58-63` ve `ActiveProductBarLayoutTests.cs:57` (`FindName`) emsali.
- **Günlük yok** (spec §6.1-e günlük istemez; VM `ILogger` taşımaz). Sahip sorusu adayı: yok (bu görevin soruları iskelette 1 ve 5).

### 7a — onay düğmesinin yazısı + ortak kimlik metni

- [ ] **Adım 1: Başarısız testleri yaz (kırmızı)**

`OrderDeck.Tests/Fakes/FakeDialogService.cs`'te bugünkü (`:56-59`):

```csharp
    // Çekmece karşılıkları AYNI listelere yazar: bir çağrı yeri senkrondan
    // async'e geçtiğinde testinin assert'ini değiştirmek gerekmesin.
    public Task<bool> ConfirmAsync(string message, string title)
        => Task.FromResult(Confirm(message, title));
```

şununla değiştir:

```csharp
    /// <summary>Çekmece onaylarının onay düğmesi yazıları, sırayla (ödeme soruları "Evet", Ayır onayı "Ayır" —
    /// spec 2026-10-11 §6.1-e).</summary>
    public List<string> ConfirmTexts { get; } = new();

    // Çekmece karşılıkları AYNI listelere yazar: bir çağrı yeri senkrondan
    // async'e geçtiğinde testinin assert'ini değiştirmek gerekmesin.
    public Task<bool> ConfirmAsync(string message, string title, string confirmText = "Evet")
    {
        ConfirmTexts.Add(confirmText);
        return Task.FromResult(Confirm(message, title));
    }
```

`OrderDeck.Tests/Services/PaymentResultPresenterTests.cs`'te `Onay_sorunun_butun_belirteclerini_yeniden_cagriya_verir` testinin kapanış `}`'ından sonra ekle (7960eba3'te `:204-226`; Görev 6'nın eklemelerinden sonra kayar — test adıyla bul):

```csharp

    [Fact] // Onay düğmesinin yazısı parametre oldu (spec 2026-10-11 §6.1-e, Ayır); ödeme soruları "Evet" kalır
    public async Task Odeme_sorusunun_onay_dugmesi_Evet_kalir()
    {
        var dialogs = new OrderDeck.Tests.Fakes.FakeDialogService { ConfirmResult = _ => false };
        var prompt = new PaymentPrompt(PersonScopePromptKind.Resend, "cumulative",
            new[] { new PriorPaymentRequest(550m, Onda, Machine: "PC-A") }, "t");

        await PaymentResultPresenter.RequestConfirmedAsync(dialogs,
            _ => Task.FromResult(new PaymentRequestOutcome(PaymentRequestResult.AlreadyRequested, prompt)));

        dialogs.ConfirmTexts.Should().Equal(new[] { "Evet" }, "var olan çağıran düğme yazısı vermez");
    }
```

`OrderDeck.Tests/App/MessageDrawerButtonTests.cs` (yeni):

```csharp
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Views.Drawers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Onay çekmecesinin düğme yazıları (ortak bakiye spec 2026-10-11 §6.1-e): Ayır onayı [Ayır] [Vazgeç] der; var olan
/// sorular [Evet] [Vazgeç], bildirim tek [Tamam] kalır. Sahte diyalog servisi yalnız parametreyi kaydeder — yazının
/// düğmeye gerçekten yazıldığını ancak görünümün kendisi gösterir. MessageDrawer yalnız yığın üzerinden kurulur
/// (Drawer'ın ctor'u internal; MainShellViewCompositionTests emsali).
/// </summary>
public class MessageDrawerButtonTests
{
    [Fact] // Tek Fact: her Fact kendi STA thread'ini açıyor
    public void Onay_dugmesi_yazisi_parametreden_gelir_varsayilan_Evet_bildirim_Tamam()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            static MessageDrawer Ac(Func<Drawer, MessageDrawer> create)
            {
                var stack = new DrawerStack();
                _ = stack.ShowAsync("Başlık", create);
                return Assert.IsType<MessageDrawer>(stack.Top!.Content);
            }

            static Button Dugme(MessageDrawer drawer, string name) => (Button)drawer.FindName(name);

            var varsayilan = Ac(d => MessageDrawer.ForConfirm(d, "Emin misin?"));
            Assert.Equal("Evet", Dugme(varsayilan, "ConfirmButton").Content);
            Assert.Equal("Vazgeç", Dugme(varsayilan, "CancelButton").Content);
            Assert.Equal(Visibility.Visible, Dugme(varsayilan, "CancelButton").Visibility);

            var ayir = Ac(d => MessageDrawer.ForConfirm(d, "Ayırınca her hesap kendi bakiyesiyle kalır.", "Ayır"));
            Assert.Equal("Ayır", Dugme(ayir, "ConfirmButton").Content);
            Assert.Equal("Vazgeç", Dugme(ayir, "CancelButton").Content);
            Assert.Equal(Visibility.Visible, Dugme(ayir, "CancelButton").Visibility);

            var bildirim = Ac(d => MessageDrawer.ForNotice(d, "Bilgi", DialogSeverity.Warning));
            Assert.Equal("Tamam", Dugme(bildirim, "ConfirmButton").Content);
            Assert.Equal(Visibility.Collapsed, Dugme(bildirim, "CancelButton").Visibility);
        });

        Assert.Null(error);
    }
}
```

(`Func<Drawer, MessageDrawer>` dönüş kovaryansıyla `IDrawerService.ShowAsync`'in `Func<Drawer, object>`'ine geçer; `ShowAsync`'in görevi kapanışı bekler, test beklemez — `MainShellViewCompositionTests.cs:62` gibi.)

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~MessageDrawerButtonTests"`
Expected: FAIL — derleme hatası: `FakeDialogService` `IDialogService.ConfirmAsync(string, string)`'i uygulamıyor (CS0535) ve `MessageDrawer.ForConfirm` üç argüman almıyor (CS1501).

Başka uygulayıcı ya da Moq sahtesi olmadığını doğrula:

Run: `git grep -n -e ": IDialogService" -e "Mock<IDialogService>" -e "Mock.Of<IDialogService>" -- "*.cs"`
Expected: yalnız `OrderDeck.App/Services/WpfDialogService.cs:9` ve `OrderDeck.Tests/Fakes/FakeDialogService.cs:7`.

- [ ] **Adım 3: `IDialogService.ConfirmAsync`'e düğme yazısı**

`OrderDeck.App/Services/IDialogService.cs`'te bugünkü (`:49-51`):

```csharp
    /// <summary>Evet/Vazgeç onayı, çekmecede. <see cref="Confirm"/> ile aynı
    /// sözleşme: Evet ise true, ESC/Vazgeç ise false.</summary>
    Task<bool> ConfirmAsync(string message, string title);
```

şununla değiştir:

```csharp
    /// <summary>Onay/Vazgeç sorusu, çekmecede. <see cref="Confirm"/> ile aynı
    /// sözleşme: onay ise true, ESC/Vazgeç ise false. <paramref name="confirmText"/>
    /// onay düğmesinin yazısı; varsayılan "Evet" (ödeme soruları). Ayır onayı
    /// "Ayır" verir (ortak bakiye spec 2026-10-11 §6.1-e: [Ayır] [Vazgeç]).</summary>
    Task<bool> ConfirmAsync(string message, string title, string confirmText = "Evet");
```

`OrderDeck.App/Services/WpfDialogService.cs`'te bugünkü (`:48-49`):

```csharp
    public Task<bool> ConfirmAsync(string message, string title)
        => _drawers.ShowAsync(title, d => MessageDrawer.ForConfirm(d, message));
```

şununla değiştir:

```csharp
    public Task<bool> ConfirmAsync(string message, string title, string confirmText = "Evet")
        => _drawers.ShowAsync(title, d => MessageDrawer.ForConfirm(d, message, confirmText));
```

- [ ] **Adım 4: `MessageDrawer` onay düğmesinin yazısını parametreden alır**

`OrderDeck.App/Views/Drawers/MessageDrawer.xaml.cs`'te bugünkü (`:13-38`):

```csharp
    private MessageDrawer(Drawer drawer, string message, DialogSeverity severity, bool asQuestion)
    {
        InitializeComponent();
        _drawer = drawer;

        MessageText.Text = message;
        Stripe.Background = (Brush)FindResource(severity switch
        {
            DialogSeverity.Error   => "OD.Brush.Accent",
            DialogSeverity.Warning => "OD.Brush.Amber",
            _                      => "OD.Brush.Info",
        });

        ConfirmButton.Content = asQuestion ? "Evet" : "Tamam";
        // Bildirimde iptal düğmesi yok: kapatmanın tek anlamı var.
        CancelButton.Content = "Vazgeç";
        CancelButton.Visibility = asQuestion ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Evet/Vazgeç sorusu. <c>MessageBoxButton.YesNo</c>'nun yerine.</summary>
    public static MessageDrawer ForConfirm(Drawer drawer, string message)
        => new(drawer, message, DialogSeverity.Info, asQuestion: true);

    /// <summary>Tek düğmeli bildirim. <c>MessageBoxButton.OK</c>'un yerine.</summary>
    public static MessageDrawer ForNotice(Drawer drawer, string message, DialogSeverity severity)
        => new(drawer, message, severity, asQuestion: false);
```

şununla değiştir:

```csharp
    private MessageDrawer(Drawer drawer, string message, DialogSeverity severity, bool asQuestion, string confirmText)
    {
        InitializeComponent();
        _drawer = drawer;

        MessageText.Text = message;
        Stripe.Background = (Brush)FindResource(severity switch
        {
            DialogSeverity.Error   => "OD.Brush.Accent",
            DialogSeverity.Warning => "OD.Brush.Amber",
            _                      => "OD.Brush.Info",
        });

        ConfirmButton.Content = asQuestion ? confirmText : "Tamam";
        // Bildirimde iptal düğmesi yok: kapatmanın tek anlamı var.
        CancelButton.Content = "Vazgeç";
        CancelButton.Visibility = asQuestion ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Onay/Vazgeç sorusu. <c>MessageBoxButton.YesNo</c>'nun yerine. Onay düğmesinin yazısı
    /// <paramref name="confirmText"/> (varsayılan "Evet"; Ayır onayı "Ayır", spec 2026-10-11 §6.1-e).</summary>
    public static MessageDrawer ForConfirm(Drawer drawer, string message, string confirmText = "Evet")
        => new(drawer, message, DialogSeverity.Info, asQuestion: true, confirmText);

    /// <summary>Tek düğmeli bildirim. <c>MessageBoxButton.OK</c>'un yerine.</summary>
    public static MessageDrawer ForNotice(Drawer drawer, string message, DialogSeverity severity)
        => new(drawer, message, severity, asQuestion: false, confirmText: "Tamam");
```

- [ ] **Adım 5: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~MessageDrawerButtonTests"`
Expected: PASS (var olan `PaymentResultPresenterTests` + 2 yeni test).

- [ ] **Adım 6: Kimlik metnini ortak sınıfa taşı (davranış aynı)**

Önce taşımanın kilidini koştur (bugünkü kodla yeşil olmalı):

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerDetailViewModelTests"`
Expected: PASS (kimlik rozeti testleri `Identity_badge_shows_the_platform_username_not_the_persons_name`, `YouTube_identity_badge_shows_the_handle_because_username_is_a_channel_id` dahil).

`OrderDeck.App/Formatting/CustomerIdentityText.cs` (yeni):

```csharp
using OrderDeck.Core.Customers;

namespace OrderDeck.App.Formatting;

/// <summary>
/// Müşteri satırının kimlik metinleri — tek tanım. Müşteri kartının kimlik rozeti ("Instagram: kullanici")
/// ve Ayır onayının hesap satırı ("Instagram @kullanici: 70,00 TL", ortak bakiye spec 2026-10-11 §6.1-e)
/// aynı platform adını ve aynı tanıtıcıyı gösterir. <c>CustomerDetailViewModel</c>'den taşındı.
/// </summary>
public static class CustomerIdentityText
{
    /// <summary>Platform kodunu okunabilir etikete çevirir (instagram → Instagram).</summary>
    public static string PlatformLabel(string? platform) => platform?.ToLowerInvariant() switch
    {
        "instagram" => "Instagram",
        "youtube" => "YouTube",
        "facebook" => "Facebook",
        "tiktok" => "TikTok",
        "twitch" => "Twitch",
        _ => string.IsNullOrWhiteSpace(platform) ? "—" : platform,
    };

    /// <summary>
    /// Kimlikte gösterilecek kullanıcı adı. Kayıt formundan gelen müşterilerde <c>DisplayName</c>'e
    /// Ad Soyad yazılıyor (arama listesinde isim görünsün diye), o yüzden <c>Display</c> kullanılamaz —
    /// "Instagram: Ayşe Önal" gibi, handle'ı hiç göstermeyen bir satır çıkar. YouTube istisna: orada
    /// <c>Username</c> = channelId, okunabilir @handle <c>DisplayName</c>'de durur.
    /// </summary>
    public static string Handle(Customer customer)
        => customer.Platform?.ToLowerInvariant() == "youtube" ? customer.Display : customer.Username;
}
```

`OrderDeck.App/ViewModels/CustomerDetailViewModel.cs`'te bugünkü (`:206-207`):

```csharp
        foreach (var m in members)
            Identities.Add($"{PlatformLabel(m.Platform)}: {IdentityHandle(m)}");
```

şununla değiştir:

```csharp
        foreach (var m in members)
            Identities.Add($"{CustomerIdentityText.PlatformLabel(m.Platform)}: {CustomerIdentityText.Handle(m)}");
```

Aynı dosyada bugünkü bloğu (`:428-450`: `:428`'deki `CanUncancelSelected` satırı, `:429` boş satır, `:430-450` iki
yardımcı; sınıfın kapanış `}`'ı `:451`):

```csharp
    private bool CanUncancelSelected() => SelectedLabels.Any(l => l.IsCancelled) && UncancelBlockedReason is null;

    /// <summary>
    /// Kimlik rozetinde gösterilecek kullanıcı adı. Kayıt formundan gelen
    /// müşterilerde <c>DisplayName</c>'e Ad Soyad yazılıyor (arama listesinde
    /// isim görünsün diye), o yüzden rozette <c>Display</c> kullanılamaz —
    /// "Instagram: Ayşe Önal" gibi, handle'ı hiç göstermeyen bir satır çıkar.
    /// YouTube istisna: orada <c>Username</c> = channelId, okunabilir @handle
    /// <c>DisplayName</c>'de durur.
    /// </summary>
    private static string IdentityHandle(Customer c) =>
        c.Platform?.ToLowerInvariant() == "youtube" ? c.Display : c.Username;

    /// <summary>Platform kodunu okunabilir etikete çevirir (instagram → Instagram).</summary>
    private static string PlatformLabel(string platform) => platform?.ToLowerInvariant() switch
    {
        "instagram" => "Instagram",
        "youtube" => "YouTube",
        "facebook" => "Facebook",
        "tiktok" => "TikTok",
        "twitch" => "Twitch",
        _ => string.IsNullOrWhiteSpace(platform) ? "—" : platform,
    };
```

yalnız `:428`'deki satırla değiştir (`:429-450` silinir):

```csharp
    private bool CanUncancelSelected() => SelectedLabels.Any(l => l.IsCancelled) && UncancelBlockedReason is null;
```

(Sonra `:428`'deki `CanUncancelSelected` satırının ardından doğrudan sınıfın kapanış `}`'ı gelir; sarkan boş satır kalmaz. `using OrderDeck.App.Formatting;` dosyada zaten var, `:11`.)

Run: `git grep -n -e "IdentityHandle" -e "PlatformLabel(" -- "*.cs"`
Expected: yalnız `OrderDeck.App/Formatting/CustomerIdentityText.cs` ve `CustomerDetailViewModel.cs`'teki `CustomerIdentityText.PlatformLabel(` çağrısı.

- [ ] **Adım 7: Derleme + regresyon**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Expected: 0 uyarı, 0 hata (TWAE; kullanılmayan özel üye yok — eski yardımcılar silindi).

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerSearchViewModelTests|FullyQualifiedName~CustomerDetailViewModelTests|FullyQualifiedName~CustomerDetailRoutingTests|FullyQualifiedName~MainShellViewCompositionTests|FullyQualifiedName~PaymentRequestServiceTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CorrectedPaymentRequestsTests|FullyQualifiedName~PaymentPersonViewModelTests|FullyQualifiedName~DrawerHostLayoutTests|FullyQualifiedName~ShellPrintSlotLayoutTests|FullyQualifiedName~MessageDrawerButtonTests"`
Expected: hepsi PASS, var olan testler değişmeden (R-Arayüz + R-Ödeme + `ForConfirm`'u kuran iki yerleşim testi). Kimlik rozeti metinleri aynı.

- [ ] **Adım 8: Commit (7a)**

```bash
git add OrderDeck.App/Services/IDialogService.cs OrderDeck.App/Services/WpfDialogService.cs \
  OrderDeck.App/Views/Drawers/MessageDrawer.xaml.cs OrderDeck.App/Formatting/CustomerIdentityText.cs \
  OrderDeck.App/ViewModels/CustomerDetailViewModel.cs OrderDeck.Tests/Fakes/FakeDialogService.cs \
  OrderDeck.Tests/Services/PaymentResultPresenterTests.cs OrderDeck.Tests/App/MessageDrawerButtonTests.cs
git commit -F - <<'MSG'
feat(balance): onay çekmecesine düğme yazısı parametresi ekle

Ortak bakiye spec'i (2026-10-11 §6.1-e) Ayır onayını [Ayır] [Vazgeç]
düğmeleriyle ister; çekmece onayı bugün yalnız [Evet] [Vazgeç] yazıyordu.
ConfirmAsync ve MessageDrawer.ForConfirm isteğe bağlı confirmText alır,
varsayılan "Evet": ödeme soruları değişmez. Platform adı ve kimlik
tanıtıcısı müşteri kartından ortak CustomerIdentityText'e taşındı; Ayır
onayının hesap satırı aynı biçimi kullanacak (rozet metni aynı).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### 7b — Ayır onayı

- [ ] **Adım 9: `BalanceApiStub`'a panel bakiye okuması (test yardımcısı)**

`OrderDeck.Tests/TestHelpers/BalanceApiStub.cs`'te bugünkü (`:36-37`):

```csharp
    /// <summary>Geri alınan işlem kimlikleri.</summary>
    public List<Guid> ReverseCalls { get; } = new();
```

şununla değiştir:

```csharp
    /// <summary>Geri alınan işlem kimlikleri.</summary>
    public List<Guid> ReverseCalls { get; } = new();

    /// <summary>Panel bakiye okumasının (<c>GET /api/panel/customers/{id}/balance</c>) <c>members[]</c>'i, verilen
    /// sırayla: (WpfCustomerId, satır bakiyesi, silinmiş mi) — Ayır onayı (ortak bakiye spec 2026-10-11 §6.1-e) ve
    /// müşteri kartı (§6.1-f). Sunucu paritesi: <c>PanelCustomerBalanceController.MemberDto</c>.</summary>
    public List<(Guid WpfCustomerId, decimal Balance, bool Purged)> PanelMembers { get; } = new();

    /// <summary>Panel bakiye okumasının <c>balance.balance</c>'ı.</summary>
    public decimal PanelBalance { get; set; }

    /// <summary>true → panel bakiye okuması 404 problem döner (sahibin projeksiyonu sunucuda yok —
    /// <c>PanelCustomerBalanceController.cs:87</c>).</summary>
    public bool PanelFails { get; set; }

    /// <summary>Panel bakiye okumasında istenen Id'ler (yolun segmenti), sırayla.</summary>
    public List<Guid> PanelBalanceRequests { get; } = new();
```

Aynı dosyada bugünkü (`:93`):

```csharp
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
```

şununla değiştir:

```csharp
        if (request.Method == HttpMethod.Get
            && path.StartsWith("/api/panel/customers/", StringComparison.Ordinal)
            && path.EndsWith("/balance", StringComparison.Ordinal))
        {
            var id = Guid.Parse(path.Split('/')[^2]);
            List<(Guid WpfCustomerId, decimal Balance, bool Purged)> members;
            lock (_sync)
            {
                PanelBalanceRequests.Add(id);
                members = new(PanelMembers);
            }
            if (PanelFails)
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"title":"Not Found","status":404}""", Encoding.UTF8,
                        "application/problem+json"),
                };
            var items = string.Join(",", members.Select(m =>
                $$"""{"wpfCustomerId":"{{m.WpfCustomerId}}","balance":{{Dec(m.Balance)}},"purged":{{(m.Purged ? "true" : "false")}}}"""));
            return Json($$"""
                {"balance":{"wpfCustomerId":"{{id}}","licenseId":"{{_licenseId}}","balance":{{Dec(PanelBalance)}},
                  "updatedAt":"2030-01-01T00:00:00+00:00"},
                 "transactions":[],"members":[{{items}}]}
                """);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
```

(`System.Linq` test projesinde örtük `using`; panelin `refund-full`/`refund-net` POST'ları `/balance` ile bitmez, bu kola düşmez.)

- [ ] **Adım 10: Başarısız testleri yaz (kırmızı)**

`OrderDeck.Tests/ViewModels/CustomerSearchUnmergeTests.cs` (yeni):

```csharp
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Ayır onayı (ortak bakiye spec 2026-10-11 §6.1-e, K1; ek sahip kararı 12.10.2026): ayırmadan önce kişinin bakiye
/// dağılımı panel ucundan (<c>members[]</c>) okunur. Bir hesapta bakiye ≠ 0 ise "Grubu ayır" sorulur, [Ayır] [Vazgeç].
/// Tutarlar 0'sa, kişinin gönderilebilir sahibi yoksa ya da istemci verilmemişse bugünkü gibi sorusuz ayrılır; okuma
/// başarısızsa genel uyarı sorulur. Ayırmanın kendisi (<c>CustomerRepository.UnmergeGroup</c>) değişmez.
/// </summary>
public sealed class CustomerSearchUnmergeTests : IDisposable
{
    private const string Baslik = "Grubu ayır";
    private const string GenelUyari =
        "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır.";
    private const string Kapanis =
        ". Başka hesaptan bu kişinin alışverişine kullanılmış tutarlar, o satış geri alınmadıkça geri dönmez.";

    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;
    private readonly BalanceApiStub _stub = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly string _settingsPath =
        Path.Combine(Path.GetTempPath(), $"orderdeck-unmerge-{Guid.NewGuid():N}.json");
    private readonly CustomerSearchViewModel _vm;

    public CustomerSearchUnmergeTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
        new SettingsStore(_settingsPath).Save(new AppSettings());
        _vm = Kur(_stub.Client());
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    /// <summary>Arama VM'i aynı veritabanı ve aynı diyalog sahtesiyle; <paramref name="api"/> Ayır onayının istemcisi
    /// (null = DI dışı kurulum, bugünkü sorusuz yol). Ödeme servisi bu testlerde çağrılmaz.</summary>
    private CustomerSearchViewModel Kur(LicenseApiClient? api)
    {
        var store = new SettingsStore(_settingsPath);
        var sessions = new SessionRepository(_db);
        var labels = new LabelRepository(_db);
        var payment = new PaymentRequestService(store, new WhatsAppMessageBuilder(), new FakeUrlLauncher(),
            PaymentRequestServiceTestHelpers.StubApiClient(), new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
            new InMemoryPaymentJobStore(), LocalShipping.Of(store));
        var customerService = new CustomerService(_customers, sessions, labels, Mock.Of<IClock>(c => c.UnixNow() == 1L));
        return new CustomerSearchViewModel(_customers, customerService, sessions, labels, payment, _dialogs, api);
    }

    /// <summary>Kişinin bir satırı; kullanıcı adı ve Id verilmezse üretilir (repo public: literal yok).</summary>
    private Customer Satir(string platform, bool phone = false, string? displayName = null, string? id = null,
        string? username = null)
    {
        var c = new Customer(id ?? Guid.NewGuid().ToString("N"), platform, username ?? $"u{Guid.NewGuid():N}"[..12], displayName,
            null, 100, 100, false, null, null, 0, 0m, null, null, phone ? TestPhone.NewE164() : null);
        _customers.Insert(c);
        return c;
    }

    /// <summary>Satırları tek kişide birleştirir ve listedeki tek kartı döner.</summary>
    private CustomerSearchViewModel.CustomerCard Kart(CustomerSearchViewModel vm, params Customer[] uyeler)
    {
        _customers.MergeIntoGroup(uyeler.Select(u => u.Id).ToList());
        vm.RefreshSearch();
        return vm.Results.Should().ContainSingle().Subject;
    }

    private static Guid G(Customer c) => Guid.ParseExact(c.Id, "N");

    private bool Ayrildi(Customer c) => _customers.GetById(c.Id)!.GroupId is null;

    [Fact] // §6.1-e: bütün tutarlar 0 → bugünkü gibi sorusuz
    public async Task Butun_tutarlar_sifirsa_sorusuz_ayrilir()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 0m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().ContainSingle("dağılım bir kez okunur");
        _dialogs.Confirmations.Should().BeEmpty();
        Ayrildi(ig).Should().BeTrue();
        Ayrildi(tt).Should().BeTrue();
        _vm.Results.Should().HaveCount(2);
    }

    [Fact] // §6.1-e metni birebir: canlılar sunucu sırasıyla, sonra silinmiş hesap; düğme [Ayır]
    public async Task Bakiye_varsa_dagilim_sorulur_metin_birebir()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _stub.PanelMembers.Add((Guid.NewGuid(), 10m, true));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Should().Be((Baslik,
            $"Ayırınca her hesap kendi bakiyesiyle kalır: Instagram @{ig.Username}: 70,00 TL · TikTok @{tt.Username}: "
            + "0,00 TL · (silinmiş hesap): 10,00 TL — ayırınca kullanılamaz" + Kapanis));
        _dialogs.ConfirmTexts.Should().Equal(new[] { "Ayır" }, "spec: [Ayır] [Vazgeç]");
    }

    [Fact] // [Vazgeç] → hiçbir şey: grup durur, liste aynı
    public async Task Vazgec_ile_grup_ayrilmaz()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _dialogs.ConfirmResult = _ => false;

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle();
        Ayrildi(ig).Should().BeFalse();
        Ayrildi(tt).Should().BeFalse();
        _vm.Results.Should().ContainSingle().Which.Should().BeSameAs(kart, "liste yenilenmedi");
    }

    [Fact] // [Ayır] → bugünkü ayırma + liste yenileme
    public async Task Ayir_ile_grup_ayrilir()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _dialogs.ConfirmResult = _ => true;

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle();
        Ayrildi(ig).Should().BeTrue();
        Ayrildi(tt).Should().BeTrue();
        _vm.Results.Should().HaveCount(2);
    }

    [Fact] // §6.1-e "bakiye ≠ 0": eksi bakiye de sorulur
    public async Task Eksi_bakiye_de_sorulur()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), -20m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message
            .Should().Contain($"Instagram @{ig.Username}: -20,00 TL");
        Ayrildi(ig).Should().BeFalse("varsayılan cevap Vazgeç");
    }

    [Fact] // Ek sahip kararı 12.10.2026: silinmiş hesapta para varken bütün canlılar ayrılsa da Ayır SERBEST + uyarı
    public async Task Silinmis_hesapta_para_varken_ayir_serbest()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 0m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _stub.PanelMembers.Add((Guid.NewGuid(), 10m, true));
        _dialogs.ConfirmResult = _ => true;

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message
            .Should().Contain("(silinmiş hesap): 10,00 TL — ayırınca kullanılamaz");
        Ayrildi(ig).Should().BeTrue("engel yok: onaylanınca ayrılır");
        Ayrildi(tt).Should().BeTrue();
    }

    [Theory] // §6.1-e: çağrı başarısızsa genel uyarı, [Ayır] [Vazgeç]; cevaba göre ayrılır ya da ayrılmaz
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cagri_hatasinda_genel_uyari(bool ayir)
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelFails = true;
        _dialogs.ConfirmResult = _ => ayir;

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Should().Be((Baslik,
            "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır."));
        _dialogs.ConfirmTexts.Should().Equal(new[] { "Ayır" });
        Ayrildi(ig).Should().Be(ayir);
        Ayrildi(tt).Should().Be(ayir);
    }

    [Fact] // members alanı olmayan yanıt (S1 öncesi sunucu) dağılım vermez → çağrı başarısız sayılır
    public async Task Members_alani_yoksa_genel_uyari()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"balance":{"wpfCustomerId":"{{Guid.NewGuid()}}","licenseId":"{{Guid.NewGuid()}}","balance":70,
              "updatedAt":"2030-01-01T00:00:00+00:00"},"transactions":[]}
            """));
        var vm = Kur(new LicenseApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://stub") },
            new LicenseTokenStore()));
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(vm, ig, tt);

        await vm.UnmergeGroupCommand.ExecuteAsync(kart);

        handler.Requests.Should().ContainSingle();
        _dialogs.Confirmations.Should().ContainSingle().Which.Should().Be((Baslik, GenelUyari));
    }

    [Fact] // §6.1-e "GUID'li üye yoksa da öyle (sunucuda bakiye olamaz)": sunucuya gidilmez, sorusuz
    public async Task Gonderilebilir_uyesi_olmayan_kart_sunucuya_sorulmadan_ayrilir()
    {
        var ig = Satir("instagram", id: $"yerel-{Guid.NewGuid():N}");
        var tt = Satir("tiktok", phone: true, id: $"yerel-{Guid.NewGuid():N}");
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((Guid.NewGuid(), 50m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().BeEmpty();
        _dialogs.Confirmations.Should().BeEmpty();
        Ayrildi(ig).Should().BeTrue();
        Ayrildi(tt).Should().BeTrue();
    }

    [Fact] // İskelet fark 17: dağılım kartın birincili ile değil, kişinin sahip satırıyla (Görev 1'in kuralı) okunur
    public async Task Dagilim_kisinin_sahip_satiriyla_sorulur()
    {
        var yerel = Satir("tiktok", phone: true, id: $"yerel-{Guid.NewGuid():N}");
        var ig = Satir("instagram");
        var kart = Kart(_vm, yerel, ig);
        kart.Primary.Id.Should().Be(yerel.Id, "ön koşul: kartın birincili telefonlu satır (BuildCards), GUID'siz");

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().Equal(new[] { G(ig) }, "sahip: kişinin tek GUID'li üyesi; birincil GUID'siz");
    }

    [Fact] // Görev 1 (§6.1-a, M14): GUID'li ama sunucunun kabul etmediği üye sahip olamaz — telefonu olsa da
    public async Task Gonderilemeyen_GUIDli_uye_sahip_olmaz_dagilim_gonderilebilir_uyeyle_sorulur()
    {
        var uzun = Satir("tiktok", phone: true, username: new string('u', ServerCustomerRule.MaxUsernameLength + 1));
        var ig = Satir("instagram");
        var kart = Kart(_vm, uzun, ig);
        kart.Primary.Id.Should().Be(uzun.Id, "ön koşul: kartın birincili telefonlu satır (BuildCards)");

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().Equal(new[] { G(ig) },
            "Username 129 karakter: sunucu satırı kabul etmez, sahip gönderilebilir üyedir");
    }

    [Fact] // Görev 1 + §6.1-e "GUID'li üye yoksa da öyle": yalnız gönderilemeyen GUID'li üyeler → sahip yok, sorusuz
    public async Task Yalniz_gonderilemeyen_GUIDli_uyeler_sunucuya_sorulmadan_ayrilir()
    {
        var uzun = new string('u', ServerCustomerRule.MaxUsernameLength + 1);
        var tt = Satir("tiktok", phone: true, username: uzun);
        var ig = Satir("instagram", username: uzun);
        var kart = Kart(_vm, tt, ig);
        _stub.PanelMembers.Add((G(tt), 50m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().BeEmpty();
        _dialogs.Confirmations.Should().BeEmpty();
        Ayrildi(tt).Should().BeTrue();
        Ayrildi(ig).Should().BeTrue();
    }

    [Fact] // Sahip sorusu 1, öneri (A): kartta olmayan canlı sunucu üyesinde bakiye ≠ 0 → liste yerine genel uyarı
    public async Task Kartta_olmayan_canli_uyede_bakiye_varsa_genel_uyari()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _stub.PanelMembers.Add((Guid.NewGuid(), 15m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Should().Be((Baslik, GenelUyari));
    }

    [Fact] // Sahip sorusu 1, öneri (A): kartta olmayan canlı üyenin 0 bakiyesi listelenmez; sunucunun görmediği yerel satır da
    public async Task Kartta_olmayan_canli_uyenin_sifir_bakiyesi_listelenmez()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((Guid.NewGuid(), 0m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().Be(
            $"Ayırınca her hesap kendi bakiyesiyle kalır: Instagram @{ig.Username}: 70,00 TL" + Kapanis);
    }

    [Fact] // YouTube'da tanıtıcı görünen ad (Username = channelId) ve zaten "@" ile başlar → "@@" yok
    public async Task YouTube_uyesinde_gorunen_tanitici_cift_at_yok()
    {
        var ad = $"y{Guid.NewGuid():N}"[..12];
        var yt = Satir("youtube", displayName: "@" + ad);
        var ig = Satir("instagram", phone: true);
        var kart = Kart(_vm, yt, ig);
        _stub.PanelMembers.Add((G(yt), 25m, false));
        _stub.PanelMembers.Add((G(ig), 0m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        var mesaj = _dialogs.Confirmations.Should().ContainSingle().Subject.Message;
        mesaj.Should().Contain($"YouTube @{ad}: 25,00 TL").And.NotContain("@@");
        mesaj.Should().NotContain(yt.Username, "YouTube'da Username channelId'dir, gösterilmez");
    }

    [Fact] // İstemci verilmemiş kurulum (DI dışı) bugünkü gibi: sorusuz, sunucuya gidilmez, komut senkron biter
    public void Istemci_yoksa_bugunku_gibi_sorusuz()
    {
        var vm = Kur(api: null);
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));

        vm.UnmergeGroupCommand.Execute(kart);

        Ayrildi(ig).Should().BeTrue("Execute döndüğünde ayrılmış: await edilecek iş yok");
        Ayrildi(tt).Should().BeTrue();
        _stub.PanelBalanceRequests.Should().BeEmpty();
        _dialogs.Confirmations.Should().BeEmpty();
    }
}
```

`OrderDeck.Tests/App/CustomerSyncDiTests.cs`'te `Musteri_detay_penceresi_kargo_deposunu_DIdan_alir` testinin (`:148-157`) kapanış `}`'ından sonra ekle:

```csharp

    [Fact]
    public void Musteri_arama_Ayir_onayi_lisans_istemcisini_DIdan_alir()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var vm = host.Services.GetRequiredService<global::OrderDeck.App.ViewModels.CustomerSearchViewModel>();

        PrivateField<global::OrderDeck.Licensing.Api.LicenseApiClient>(vm, "_api").Should().BeSameAs(
            host.Services.GetRequiredService<global::OrderDeck.Licensing.Api.LicenseApiClient>(),
            "spec §6.1-e: isteğe bağlı parametre DI'dan gelmezse Ayır onayı sessizce hiç sorulmazdı");
    }
```

`OrderDeck.Tests/ViewModels/PaymentPersonViewModelTests.cs`'te bugünkü (`:190`) — bu görevde bilerek değişen tek var olan satır (komut async oldu; bu sınıfın VM'i istemcisiz kurulduğu için davranış ve iddialar aynı):

```csharp
        _search.UnmergeGroupCommand.Execute(_search.Results.Single());
```

şununla değiştir:

```csharp
        await _search.UnmergeGroupCommand.ExecuteAsync(_search.Results.Single());
```

- [ ] **Adım 11: Koştur — FAIL bekle**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerSearchUnmergeTests|FullyQualifiedName~CustomerSyncDiTests|FullyQualifiedName~PaymentPersonViewModelTests"`
Expected: FAIL — derleme hatası: `CustomerSearchViewModel` 7 argümanlı ctor'a sahip değil (CS1729) ve `IRelayCommand<CustomerSearchViewModel.CustomerCard?>` `ExecuteAsync` içermiyor (CS1061; komut bugün senkron).

- [ ] **Adım 12: `CustomerSearchViewModel` — istemci bağımlılığı ve Ayır onayı**

`OrderDeck.App/ViewModels/CustomerSearchViewModel.cs`'te bugünkü `using` bloğu (`:1-11`):

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
```

şununla değiştir:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Formatting;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
```

Bugünkü alanlar (`:21-22`):

```csharp
    private readonly PaymentRequestService _paymentService;
    private readonly IDialogService _dialogService;
```

şununla değiştir:

```csharp
    private readonly PaymentRequestService _paymentService;
    private readonly IDialogService _dialogService;
    private readonly LicenseApiClient? _api;
```

Bugünkü ctor (`:118-134`):

```csharp
    public CustomerSearchViewModel(
        CustomerRepository customers,
        CustomerService customerService,
        SessionRepository sessions,
        LabelRepository labels,
        PaymentRequestService paymentService,
        IDialogService dialogService)
    {
        _customers = customers;
        _customerService = customerService;
        _sessions = sessions;
        _labels = labels;
        _paymentService = paymentService;
        _dialogService = dialogService;

        SelectedCards.CollectionChanged += (_, _) => MergeSelectedCommand.NotifyCanExecuteChanged();
    }
```

şununla değiştir:

```csharp
    /// <param name="api">Ortak bakiye spec 2026-10-11 §6.1-e (K1): Ayır onayının bakiye dağılımı (panel
    /// <c>GET …/balance</c>, <c>members[]</c>). DI verir (tekil istemci); null yalnız test/kompozisyonda — o zaman
    /// Ayır bugünkü gibi sorusuz.</param>
    public CustomerSearchViewModel(
        CustomerRepository customers,
        CustomerService customerService,
        SessionRepository sessions,
        LabelRepository labels,
        PaymentRequestService paymentService,
        IDialogService dialogService,
        LicenseApiClient? api = null)
    {
        _customers = customers;
        _customerService = customerService;
        _sessions = sessions;
        _labels = labels;
        _paymentService = paymentService;
        _dialogService = dialogService;
        _api = api;

        SelectedCards.CollectionChanged += (_, _) => MergeSelectedCommand.NotifyCanExecuteChanged();
    }
```

Bugünkü `UnmergeGroup` (`:265-272`):

```csharp
    /// <summary>Grubu tekil satırlara ayırır (yanlış birleştirmeyi geri alır).</summary>
    [RelayCommand]
    private void UnmergeGroup(CustomerCard? card)
    {
        if (card?.GroupId is not { Length: > 0 } groupId) return;
        _customers.UnmergeGroup(groupId);
        RefreshSearch();
    }
```

şununla değiştir:

```csharp
    /// <summary>Ayır onayının başlığı (spec 2026-10-11 §6.1-e); genel uyarı da aynı başlıkla sorulur.</summary>
    internal const string UnmergeTitle = "Grubu ayır";

    /// <summary>Ayır onayının onay düğmesi (spec §6.1-e: [Ayır] [Vazgeç]).</summary>
    internal const string UnmergeButton = "Ayır";

    /// <summary>Bakiye dağılımı okunamadığında sorulan genel uyarı (spec §6.1-e).</summary>
    internal const string UnmergeFallbackMessage =
        "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır.";

    /// <summary>
    /// Ayır onayının metni (spec 2026-10-11 §6.1-e, K1). Sunucunun kişi üyeleri (<paramref name="serverMembers"/>,
    /// panel <c>members[]</c>) kartın yerel satırlarına Id ile eşlenir; platform ve tanıtıcı yereldendir.
    /// null → soru yok (bütün tutarlar 0). Canlı üye "Instagram @kullanici: 70,00 TL", silinmiş üye
    /// "(silinmiş hesap): 10,00 TL — ayırınca kullanılamaz" (ek sahip kararı 12.10.2026: Ayır serbest, tutar
    /// gösterilir); önce canlılar, sonra silinmişler, her grup sunucunun sırasıyla. Kartta olmayan canlı üyenin
    /// bakiyesi ≠ 0 ise liste yerine <see cref="UnmergeFallbackMessage"/> (Sahip sorusu 1, öneri A); 0 ise listelenmez.
    /// </summary>
    internal static string? UnmergeMessage(
        IReadOnlyList<Customer> localMembers, IReadOnlyList<CustomerBalanceMemberDto> serverMembers)
    {
        if (serverMembers.All(m => m.Balance == 0m)) return null;

        var live = new List<string>();
        var purged = new List<string>();
        foreach (var m in serverMembers)
        {
            var amount = TrFormats.Currency(m.Balance);
            if (m.Purged)
            {
                purged.Add($"(silinmiş hesap): {amount} — ayırınca kullanılamaz");
                continue;
            }
            var local = localMembers.FirstOrDefault(c => Guid.TryParseExact(c.Id, "N", out var g) && g == m.WpfCustomerId);
            if (local is null)
            {
                if (m.Balance != 0m) return UnmergeFallbackMessage;
                continue;
            }
            var handle = CustomerIdentityText.Handle(local);
            live.Add($"{CustomerIdentityText.PlatformLabel(local.Platform)} {(handle.StartsWith('@') ? handle : "@" + handle)}: {amount}");
        }
        return "Ayırınca her hesap kendi bakiyesiyle kalır: " + string.Join(" · ", live.Concat(purged))
             + ". Başka hesaptan bu kişinin alışverişine kullanılmış tutarlar, o satış geri alınmadıkça geri dönmez.";
    }

    /// <summary>Grubu tekil satırlara ayırır (yanlış birleştirmeyi geri alır). Spec 2026-10-11 §6.1-e (K1): önce kişinin
    /// bakiye dağılımı okunur; bir üyede bakiye ≠ 0 ise "Grubu ayır" onayı ([Ayır] [Vazgeç]). Ayırmanın kendisi değişmez;
    /// gösterilen tutar okuma anınındır (bilgi amaçlı), kip ayrımı yok.</summary>
    [RelayCommand]
    private async Task UnmergeGroupAsync(CustomerCard? card)
    {
        if (card is null || card.GroupId is not { Length: > 0 } groupId) return;
        var question = await UnmergeQuestionAsync(card);
        if (question is not null && !await _dialogService.ConfirmAsync(question, UnmergeTitle, UnmergeButton)) return;
        _customers.UnmergeGroup(groupId);
        RefreshSearch();
    }

    /// <summary>Ayır'ın sorusu; null → sorusuz. İstemci yoksa ya da kişinin gönderilebilir sahibi yoksa sunucuya
    /// gidilmez (sunucuda bakiye olamaz). Çağrı hatası (404, ağ, 5xx) ve <c>members</c>'ın yokluğu genel uyarıdır.
    /// İstemcinin her hatası genel uyarıdır; iptal jetonu yok (kullanıcı iptali diye bir yol yok), süzgeç de yok —
    /// kaçan bir istisna Ayır'ı sessizce yutardı. İstemci yokken senkron tamamlanır: komut bugünkü gibi senkron biter.</summary>
    private async Task<string?> UnmergeQuestionAsync(CustomerCard card)
    {
        if (_api is null) return null;
        var owner = _customers.GetPaymentPerson(card.Primary.Id).OwnerId;
        if (owner is null || !Guid.TryParseExact(owner, "N", out var ownerId)) return null;
        try
        {
            var details = await _api.GetCustomerBalanceAsync(ownerId, take: 1, CancellationToken.None);
            return details.Members is { } members ? UnmergeMessage(card.Members, members) : UnmergeFallbackMessage;
        }
        catch (Exception)
        {
            return UnmergeFallbackMessage;
        }
    }
```

(`[RelayCommand]` `UnmergeGroupAsync`'ten `UnmergeGroupCommand` (`IAsyncRelayCommand<CustomerCard?>`) üretir — XAML bağlaması `CustomerSearchDrawer.xaml:252` aynı; emsal `CustomerDetailViewModel.cs:343-347`. Varsayılan `AllowConcurrentExecutions = false`: okuma sürerken düğme pasif, çift tık ikinci okuma açmaz. Okuma, istemcinin yeniden deneme politikasından geçer (`AppHost.cs:451` `AddStandardResilienceHandler`: 5xx/ağ hatasında 3 yeniden deneme, üstel bekleme, toplam 30 sn): sunucuya ulaşılamazken soru ~30 sn'ye kadar gecikir — Not bölümündeki "Satır kipi" maddesi. `CustomerBalanceMemberDto[]` `IReadOnlyList<CustomerBalanceMemberDto>`'yu uygular.)

- [ ] **Adım 13: Koştur — PASS**

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerSearchUnmergeTests|FullyQualifiedName~CustomerSyncDiTests.Musteri_arama_Ayir_onayi_lisans_istemcisini_DIdan_alir"`
Expected: PASS (16 test yöntemi, 17 vaka + 1 DI testi).

- [ ] **Adım 14: Derleme + regresyon**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Expected: 0 uyarı, 0 hata (yeni özel üyelerin hepsi kullanılıyor: `_api` ve `UnmergeQuestionAsync` `UnmergeGroupAsync`'ten; sabitler ve `UnmergeMessage` `UnmergeQuestionAsync`/`UnmergeGroupAsync`'ten).

Run: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerSearchViewModelTests|FullyQualifiedName~CustomerDetailViewModelTests|FullyQualifiedName~CustomerDetailRoutingTests|FullyQualifiedName~MainShellViewCompositionTests|FullyQualifiedName~PaymentPersonViewModelTests|FullyQualifiedName~PaymentResultPresenterTests|FullyQualifiedName~CustomerSyncDiTests|FullyQualifiedName~CustomerRepositoryTests|FullyQualifiedName~MessageDrawerButtonTests|FullyQualifiedName~CustomerSearchUnmergeTests"`
Expected: hepsi PASS. Var olan testlerden yalnız `PaymentPersonViewModelTests.cs:190` satırı değişti (`Ayirmadan_sonra_sahip_kendi_toplamina_revize_edilir_obur_satir_kendi_isini_acar` — iddiaları aynı); `CustomerRepositoryTests.UnmergeGroup_clears_group_for_all_members` (`:883-893`) depo davranışının değişmediğini kilitler. `PaymentLeaseTests.cs:1621` (7960eba3 numarası; Görev 1/6 testleri öncesine eklenir) ve `MainShellViewCompositionTests.cs:231` VM'i istemcisiz kurar (isteğe bağlı parametre): derlenir, Ayır'ı çağırmaz.

Panel bakiye kolunun var olan `BalanceApiStub` kullanıcılarını etkilemediğini doğrula (o yol bugün 404'tü, kimse çağırmıyordu):

Run: `git grep -n "BalanceApiStub" -- "OrderDeck.Tests/*.cs"`
Expected: `PaymentPersonViewModelTests.cs` ve `CustomerSearchUnmergeTests.cs` (+ `BalanceApiStub.cs`); Görev 8 aynı kolu kendi testinde tüketir.

- [ ] **Adım 15: Commit (7b)**

```bash
git add OrderDeck.App/ViewModels/CustomerSearchViewModel.cs OrderDeck.Tests/TestHelpers/BalanceApiStub.cs \
  OrderDeck.Tests/ViewModels/CustomerSearchUnmergeTests.cs OrderDeck.Tests/ViewModels/PaymentPersonViewModelTests.cs \
  OrderDeck.Tests/App/CustomerSyncDiTests.cs
git commit -F - <<'MSG'
feat(balance): Ayır'dan önce kişinin bakiye dağılımını göster ve onay iste

Ortak bakiye K1'de her hesap kendi parasını tutar; başka hesaptan bu
kişinin alışverişine kullanılmış tutar ayırınca geri dönmez. Ayır artık
önce kişinin sahip satırıyla panel bakiye okumasını yapar, members[]'i
kartın satırlarına eşler ve bir hesapta bakiye sıfır değilse "Grubu ayır"
sorusunu [Ayır] [Vazgeç] ile sorar; silinmiş hesabın tutarı "ayırınca
kullanılamaz" diye gösterilir, Ayır serbesttir (sahip kararı 12.10.2026).
Tutarlar sıfırsa ya da gönderilebilir üye yoksa bugünkü gibi sorusuz;
okuma başarısızsa ya da kartta olmayan bir hesapta para varsa genel uyarı.
Ayırmanın kendisi değişmez, kip ayrımı yok. İstemci DI'dan isteğe bağlı
son parametreyle gelir; test sahtesi panel ucunu taklit eder.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 8: Müşteri kartı sahip yedeği (spec §6.1-f, §8.4-6, §11 risk 4)

**Files:**
- Değiştir: `OrderDeck.App/ViewModels/CustomerDetailViewModel.cs` — `TryGetProjectionId` (`:229-239`: belge `:229-233` + yöntem `:234-239`; erişim `private` → `internal`) ve `ReloadBalanceAsync`'in yalnız kapı satırı (`:241-249`: imza + kapı; `TryGetProjectionId` çağrısı `try/catch` içine; bkz. Not "Depo okuması"). `ReloadBalanceAsync`'in `:250-266` gövdesi, `AddBalanceAsync` (`:269-290`) ve iki metin (`:246`, `:274-277`) DEĞİŞMEZ: ikisi de zaten `TryGetProjectionId`'yi çağırıyor (`:243`, `:272`).
- Test (oluştur): `OrderDeck.Tests/ViewModels/CustomerDetailOwnerBalanceTests.cs` (kendi küçük kurulumu; `CustomerDetailViewModelTests.Build` DEĞİŞMEZ).
- Değişmeyenler: `OrderDeck.App/Views/Drawers/AddBalanceDrawer.xaml.cs` (hedef Id'yi çağırandan alır, `Create` `:41-43`, kullanım `:106-125`), `Views/Drawers/CustomerDetailDrawer.xaml`, `OrderDeck.Core/Storage/Repositories/CustomerRepository.cs` (`GetPaymentPerson` `:601-606`), `OrderDeck.Tests/TestHelpers/BalanceApiStub.cs` (panel GET kolu Görev 7'de eklendi; bu görev yalnız tüketir).

**Bağımlılık:** Görev 1 (sahip kuralı `PaymentOwnerRule.PickOwner` = `IsOwnerCandidate`; `PaymentPersonSql` `Platform`/`Username` okur) ve Görev 7 (`BalanceApiStub.PanelBalance`, `PanelBalanceRequests`, panel GET kolu) master'a değil, bu dala önceden girmiş olmalı. Bu görev ikisini de değiştirmez.

**Not (spec ↔ kod) — bu görev:**
- **Satır kaydı (iskelet fark 1).** Spec'in `CustomerDetailViewModel.cs:195-200` / `:202-228` / `:207` / `:230-250` atıfları bugün `:229-239` / `:241-267` / `:246` / `:269-290`; `AddBalanceDrawer.xaml.cs:106-117` bugün `:106-125`. İçerik aynı; yalnız numara kaydı.
- **İskelet fark 19 uygulanır (anlama SORU-f1).** Spec "Server eşlemesi yok metni yalnız kişinin **hiç GUID'li üyesi yoksa** çıkar" der. Görev 1'in sahip kuralıyla `OwnerId` iki durumda null olur: GUID'li üye yok, ya da GUID'li üyelerin hiçbiri sunucunun kabul edeceği satır değil (`ServerCustomerRule.IsServerAcceptable` tutmaz: boş/uzun platform ya da kullanıcı adı). İkinci durumda o satırlar sunucuya hiç gönderilmez (`WpfCustomerProjectionSyncService` aynı kuralla eler), sunucuda projeksiyonları ve bakiyeleri olamaz; GUID'li ama gönderilemeyen bir satıra yedeklemek panel GET'te 404 → "Bakiye yüklenemedi: …" üretirdi. **Çözüm:** `OwnerId` null → bugünkü "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor." metni (metin doğru: o kişinin sunucu eşlemesi gerçekten yok). Sahip kuralına "GUID'li ama gönderilemez" yedeği eklenmez (icat olurdu). Bakiye Ekle'nin `MessageBox` metni (`:274-277`) aynı kapıdan geçer, metni değişmez.
- **Açılan satır GUID'liyse bugünkü gibi o satır (spec lafzı)** — gönderilemeyen GUID'li satır olsa bile (`Guid.TryParseExact` tek ölçüt, bugünkü davranış). Havuz kipinde panel GET hangi üyeyle sorulursa sorulsun `Σ_S` döner; satır kipinde açılan satırın kendi bakiyesi (bugünkü). Sahibe yalnız GUID'siz satırdan açılan kart düşer.
- **`AddBalanceAsync`'in uyarısı doğrudan `MessageBox.Show`** (`:274`, `IDialogService` yok) — testte sürülemez. "Bakiye Ekle aynı Id'ye gider" iddiası bu yüzden `TryGetProjectionId`'nin doğrudan çağrısıyla kilitlenir: `AddBalanceAsync` hedef Id'yi yalnız bu yöntemden alır (`:272` → `:285` `AddBalanceDrawer.Create(d, _api, projectionId, label)`). Bunun için yöntem `internal` olur (App → Tests IVT, `OrderDeck.App.csproj:39-43`). Yeni test yüzeyi yok.
- **Depo okuması: kapı satırı sarılır (talimattaki "gövde değişmez"den bilinçli sapma).** Bugün `TryGetProjectionId` saftır, atmaz; Görev 8'den sonra GUID'siz satırda SQLite okur (`GetPaymentPerson` → `CustomerIdSql.Resolve` + `PaymentPersonSql.ReadMembers`, `CustomerRepository.cs:601-606`) ve `ReloadBalanceAsync`'te `try`'ın dışından (`:243`) çağrılır. `Load` bu yöntemi ateşle-unut çağırır (`:224` `_ = ReloadBalanceAsync();`): okuma atarsa (kilitli/meşgul DB) istisna atılan Task'ta kalır, ancak GC'de `UnobservedTaskException` olarak günlüğe düşer (`App.xaml.cs:69`, `:201`). Kart geçici olduğu için alanlar ilk değerinde kalır (`_balanceLabel = "0,00 TL"` `:126`, `BalanceError` null): operatör bakiyeyi sıfır sanar. `Load`'un öbür okumaları (`GetById`, `GetGroupMembers`, `GetParticipationsByCustomer`) çağırana yayılır (çekmece hiç açılmaz); bu okuma ise yutulurdu — "yeni hata sınıfı eklenmez" demek doğru olmazdı. **Çözüm:** yalnız kapı satırı `try/catch` içine alınır, `catch` var olan `:264` metnini (`$"Bakiye yüklenemedi: {ex.Message}"`) yazar, `BalanceLoaded = false`, `BalanceTransactions.Clear()`. Yeni kullanıcı metni yok. Satır yoksa `GetPaymentPerson` üyesiz kişi döner (`CustomerRepository.cs:596-600` belgesi) → `OwnerId` null → "server eşlemesi yok" metni (istisna değil).
- **Kalan risk (bilinçli, kapsam dışı):** `AddBalanceAsync` (`:272`) aynı okumayı korumasız yapar; atarsa istisna `AsyncRelayCommand` üzerinden Dispatcher işleyicisine gider, günlüğe düşer, düğme sessizce bir şey yapmaz. Kart açılırken aynı okuma az önce `ReloadBalanceAsync`'te hata metnini zaten göstermiştir; Bakiye Ekle'ye yeni bir hata metni (spec'te yok) eklenmez.
- **R35 (arama, sohbet, kara liste aynı havuz).** Bütün girişler `CustomerDetailViewModel.Load(id)`'ye satır Id'si verir (`CustomerSearchDrawer.xaml.cs:38`, `MainShellViewModel.cs:2844-2929`, `BlacklistViewModel.cs:67-83`); düzeltme tek yöntemde olduğu için giriş başına kod yok. Karışık filo/saha denemesi Görev 9'un saha notunda.
- **Satır kipi.** Bu, spec §6.1'in satır kipinde de değişen üç şeyinden biridir (f): GUID'siz satırdan açılan kart bugün metin gösterir, Wh'de kişinin sahibinin satır bakiyesini gösterir. Sunucu sinyali gerekmez; eski sunucu da aynı panel GET'i yanıtlar.
- Sahip sorusu adayı: yok.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.Tests/ViewModels/CustomerDetailOwnerBalanceTests.cs`:

```csharp
using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Müşteri kartının bakiye kimliği — spec 2026-10-11 §6.1-f, §8.4-6, §11 risk 4. Açılan satır GUID ("N") ise o satır
/// (bugünkü gibi); değilse kişinin sahip satırı (<c>GetPaymentPerson(id).OwnerId</c>, sahip kuralı: GUID'li VE
/// sunucunun kabul edeceği satır, spec §6.1-a). Sahip yoksa bugünkü "server eşlemesi yok" metni (W-havuz planı,
/// Spec ↔ kod farkı 19). Bakiye okuması ve "Bakiye Ekle" aynı Id'yi kullanır. <c>Load</c> bakiyeyi ateşle-unut çağırır;
/// testler ayrıca <c>await ReloadBalanceAsync()</c> ile belirlenimli bekler, bu yüzden panel isteği iki kez kaydedilebilir.
/// </summary>
public sealed class CustomerDetailOwnerBalanceTests
{
    private const string EslemeYokMetni = "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor.";

    private sealed record Kurulum(InMemorySqlite Db, CustomerRepository Customers, BalanceApiStub Stub,
        CustomerDetailViewModel Vm);

    /// <summary>Yerel veritabanı hatası: <see cref="Bozuk"/> açıkken her açılış fırlatır
    /// (<c>MainShellSyncStatusTests.FailingFactory</c> emsali). Kurulumda kapalı.</summary>
    private sealed class BozukFabrika(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public bool Bozuk { get; set; }
        public IDbConnection Open() => Bozuk ? throw new SqliteException("database is locked", 5) : inner.Open();
    }

    /// <summary><see cref="CustomerDetailViewModelTests"/>'in <c>Build</c> emsali; istemci <see cref="BalanceApiStub"/>
    /// (panel GET kolu istenen Id'leri kaydeder). Çekmece servisi verilmez (Bakiye Ekle çekmecesi açılmaz).</summary>
    private static Kurulum Kur() => Kur(out _);

    /// <summary><see cref="Kur()"/>; müşteri deposu <paramref name="fabrika"/> üzerinden açar (kapalı başlar), böylece
    /// test kurulumdan sonra yalnız müşteri okumalarını bozabilir.</summary>
    private static Kurulum Kur(out BozukFabrika fabrika)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 2_000L);
        fabrika = new BozukFabrika(db);

        var sessions = new SessionRepository(db);
        var customers = new CustomerRepository(fabrika);
        var labels = new LabelRepository(db);
        var customerSvc = new CustomerService(customers, sessions, labels, clock);
        var labelSvc = new LabelService(labels, customerSvc, db, clock);

        var stub = new BalanceApiStub();
        var vm = new CustomerDetailViewModel(customers, labels, labelSvc, new GiveawayRepository(db),
            new StreamSessionService(sessions, clock), stub.Client());
        return new Kurulum(db, customers, stub, vm);
    }

    private static Customer Satir(Kurulum k, string id, string platform, string username, string? phone = null)
    {
        var c = new Customer(id, platform, username, null, null,
            100, 100, false, null, null, 0, 0m, null, null, phone);
        k.Customers.Insert(c);
        return c;
    }

    private static string GuidliId() => Guid.NewGuid().ToString("N");
    private static string YerelId() => $"yerel-{Guid.NewGuid():N}";
    private static string KullaniciAdi() => $"u{Guid.NewGuid():N}"[..12];

    [Fact] // §6.1-f, §8.4-6: GUID'siz satırdan açılan kart kişinin sahip satırını okur; Bakiye Ekle aynı Id
    public async Task Guidsiz_satirdan_acilan_kart_kisinin_sahip_satirinin_bakiyesini_okur()
    {
        var k = Kur();
        var yerel = Satir(k, YerelId(), "tiktok", KullaniciAdi(), TestPhone.NewE164());
        var a = Satir(k, GuidliId(), "instagram", KullaniciAdi(), TestPhone.NewE164());
        k.Customers.MergeIntoGroup(new[] { yerel.Id, a.Id });
        k.Stub.PanelBalance = 125m;

        k.Vm.Load(yerel.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        var beklenen = Guid.ParseExact(a.Id, "N");
        var istekler = k.Stub.PanelBalanceRequests.ToArray();
        istekler.Should().NotBeEmpty("GUID'siz satırın kişisinde gönderilebilir bir sahip var");
        istekler.Should().OnlyContain(id => id == beklenen, "kart kişinin sahip satırının bakiyesini sorar");
        k.Vm.BalanceLoaded.Should().BeTrue();
        k.Vm.BalanceLabel.Should().Be("125,00 TL");
        k.Vm.BalanceError.Should().BeNull();

        k.Vm.TryGetProjectionId(out var hedef).Should().BeTrue();
        hedef.Should().Be(beklenen, "Bakiye Ekle hedefini aynı yöntemden alır (AddBalanceDrawer.Create)");
    }

    [Fact] // §6.1-f: açılan satır GUID'liyse bugünkü gibi o satır — sahibe düşmez
    public async Task Guidli_satirdan_acilan_kart_bugunku_gibi_o_satiri_okur()
    {
        var k = Kur();
        var a = Satir(k, GuidliId(), "instagram", KullaniciAdi(), TestPhone.NewE164());
        var b = Satir(k, GuidliId(), "tiktok", KullaniciAdi());
        k.Customers.MergeIntoGroup(new[] { a.Id, b.Id });

        k.Vm.Load(b.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        var beklenen = Guid.ParseExact(b.Id, "N");
        var istekler = k.Stub.PanelBalanceRequests.ToArray();
        istekler.Should().NotBeEmpty();
        istekler.Should().OnlyContain(id => id == beklenen,
            "açılan satır GUID'li: telefonlu sahip A olsa da kart B'nin kendi Id'siyle sorar");
        k.Vm.TryGetProjectionId(out var hedef).Should().BeTrue();
        hedef.Should().Be(beklenen);
    }

    [Fact] // §6.1-a/f: sahip kuralı gönderilemeyen GUID'li satırı atlar (kullanıcı adı boş)
    public async Task Sahip_kurali_gonderilemeyen_satiri_atlar()
    {
        var k = Kur();
        var yerel = Satir(k, YerelId(), "tiktok", KullaniciAdi(), TestPhone.NewE164());
        // A telefonlu: eski kuralla sıranın başında ve sahip olurdu; kullanıcı adı boş → sunucuya gönderilemez.
        var a = Satir(k, GuidliId(), "instagram", "", TestPhone.NewE164());
        var b = Satir(k, GuidliId(), "youtube", KullaniciAdi());
        k.Customers.MergeIntoGroup(new[] { yerel.Id, a.Id, b.Id });

        k.Vm.Load(yerel.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        var beklenen = Guid.ParseExact(b.Id, "N");
        var istekler = k.Stub.PanelBalanceRequests.ToArray();
        istekler.Should().NotBeEmpty();
        istekler.Should().OnlyContain(id => id == beklenen, "sahip GUID'li VE gönderilebilir ilk üyedir (spec §6.1-a, ServerCustomerRule.IsServerAcceptable)");
        k.Vm.TryGetProjectionId(out var hedef).Should().BeTrue();
        hedef.Should().Be(beklenen);
    }

    [Fact] // §6.1-f: kişinin hiç GUID'li üyesi yoksa bugünkü metin, sunucuya istek yok
    public async Task Hic_guidli_uyesi_olmayan_kiside_server_eslemesi_yok_metni()
    {
        var k = Kur();
        var yerel1 = Satir(k, YerelId(), "instagram", KullaniciAdi(), TestPhone.NewE164());
        var yerel2 = Satir(k, YerelId(), "tiktok", KullaniciAdi());
        k.Customers.MergeIntoGroup(new[] { yerel1.Id, yerel2.Id });

        k.Vm.Load(yerel1.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        k.Vm.BalanceError.Should().Be(EslemeYokMetni);
        k.Vm.BalanceLoaded.Should().BeFalse();
        k.Stub.PanelBalanceRequests.Should().BeEmpty();
        k.Vm.TryGetProjectionId(out _).Should().BeFalse("Bakiye Ekle de aynı kapıdan uyarıya düşer");
    }

    [Fact] // §6.1-f + plan farkı 19: GUID'li üyelerin hiçbiri gönderilemiyorsa da sahip yok → aynı metin, istek yok
    public async Task Guidli_uyeleri_gonderilemeyen_kiside_server_eslemesi_yok_metni()
    {
        var k = Kur();
        var yerel = Satir(k, YerelId(), "tiktok", KullaniciAdi(), TestPhone.NewE164());
        var a = Satir(k, GuidliId(), "instagram", "", TestPhone.NewE164());
        k.Customers.MergeIntoGroup(new[] { yerel.Id, a.Id });

        k.Vm.Load(yerel.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        k.Vm.BalanceError.Should().Be(EslemeYokMetni, "gönderilemeyen satır sunucuya hiç ulaşmaz; eşlemesi yok");
        k.Vm.BalanceLoaded.Should().BeFalse();
        k.Stub.PanelBalanceRequests.Should().BeEmpty();
        k.Vm.TryGetProjectionId(out _).Should().BeFalse();
    }

    [Fact] // bugünkü davranış: grupsuz GUID'siz satır → metin
    public async Task Grupsuz_guidsiz_satir_bugunku_gibi_metin()
    {
        var k = Kur();
        var yerel = Satir(k, YerelId(), "instagram", KullaniciAdi(), TestPhone.NewE164());

        k.Vm.Load(yerel.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        k.Vm.BalanceError.Should().Be(EslemeYokMetni);
        k.Vm.BalanceLoaded.Should().BeFalse();
        k.Stub.PanelBalanceRequests.Should().BeEmpty();
        k.Vm.TryGetProjectionId(out _).Should().BeFalse();
    }

    [Fact] // sahip okuması yerel DB'ye gider: atarsa kart sessizce "0,00 TL"de kalmaz, bugünkü hata metnini gösterir
    public async Task Sahip_okumasi_atarsa_bakiye_yuklenemedi_metni()
    {
        var k = Kur(out var fabrika);
        var yerel = Satir(k, YerelId(), "instagram", KullaniciAdi(), TestPhone.NewE164());

        k.Vm.Load(yerel.Id).Should().BeTrue();   // ateşle-unut bakiye: sahip yok → metin, ağ isteği yok
        fabrika.Bozuk = true;                    // kilitli/meşgul yerel DB

        await k.Vm.ReloadBalanceAsync();         // atmamalı

        k.Vm.BalanceError.Should().StartWith("Bakiye yüklenemedi:",
            "Load bunu ateşle-unut çağırır; istisna Task'ta yutulursa operatör hiçbir hata görmez");
        k.Vm.BalanceLoaded.Should().BeFalse();
        k.Vm.BalanceTransactions.Should().BeEmpty();
        k.Stub.PanelBalanceRequests.Should().BeEmpty();
    }
}
```

Notlar (test kurgusu):
- `Load` bakiyeyi ateşle-unut çağırır (`CustomerDetailViewModel.cs:224`); testin `await ReloadBalanceAsync()`'i ikinci bir istek atar. İki çağrı aynı yanıtı aynı özelliklere yazar. Liste anlık görüntüyle (`ToArray()`) okunur: ateşle-unut isteği iddia sırasında kaydedilse bile numaralandırma "Collection was modified" atmaz; iddia "boş değil + hepsi beklenen Id"dir, sayı değil.
- Kullanıcı adı `""` satırları her testte platform başına bir tane (`UX_Customer_Platform_Username` `001_initial.sql:39`); `CustomerIdentity.KeyOrNull("")` null yazar, kimlik eşleşmesi tetiklenmez.
- `Guidli_satirdan_…`, `Hic_guidli_…`, `Guidli_uyeleri_gonderilemeyen_…`, `Grupsuz_…` bugünkü davranışı (ve Görev 1'in kuralını) kilitler; uygulamadan önce de geçerler. Kırmızı olan üçü `Guidsiz_…`, `Sahip_kurali_…` ve `Sahip_okumasi_atarsa_…`dır.
- `Sahip_okumasi_atarsa_…` veritabanını `InMemorySqlite.Dispose()` ile düşürmez: bağlantı metninde `Pooling` yok, `Microsoft.Data.Sqlite` 10'da varsayılan açık; havuzdaki boştaki bağlantılar paylaşılan bellek-içi DB'yi yaşatır, `Dispose` sonrası okuma yine başarılı olabilir. Global `SqliteConnection.ClearAllPools()` ise paralel sınıflarla yarışır (#532). Bu yüzden yalnız müşteri deposu `BozukFabrika` üzerinden açar (`MainShellSyncStatusTests.FailingFactory:42-46` emsali; `SqliteException("database is locked", 5)`); öbür depolar `db`'yi doğrudan kullanır. Grupsuz GUID'siz satır seçilir: `Load`'un ateşle-unut çağrısı sahip bulamaz → ağ isteği yok, geç tamamlanıp sonucu ezecek bir istek kalmaz (belirlenimli).
- `Kurulum` imzası brief/iskeletteki gibidir (`IDisposable` yok); `var k = Kur()` — `CustomerDetailViewModelTests.Build` emsali (o da bağlantıyı açık bırakır). `Kur(out BozukFabrika)` yalnız bu sınıfa özel ek aşırı yüklemedir; `Kur()` onu çağırır.

- [ ] **Adım 2: Koştur — beklenen FAIL (derleme)**

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerDetailOwnerBalanceTests"
```

Beklenen: derleme hatası `CS0122` — `CustomerDetailViewModel.TryGetProjectionId(out Guid)` koruma düzeyi yüzünden erişilemez (bugün `private`, `:234`). Testlerin hiçbiri koşmaz.

- [ ] **Adım 3: Yalnız erişimi aç (davranış aynı)**

`OrderDeck.App/ViewModels/CustomerDetailViewModel.cs:234` — bugünkü:

```csharp
    private bool TryGetProjectionId(out Guid id)
```

yerine:

```csharp
    internal bool TryGetProjectionId(out Guid id)
```

Gövde ve belge bu adımda değişmez (kırmızıyı davranışla görmek için).

- [ ] **Adım 4: Koştur — beklenen FAIL (davranış)**

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerDetailOwnerBalanceTests"
```

Beklenen: 3 FAIL, 4 PASS.
- `Guidsiz_satirdan_acilan_kart_kisinin_sahip_satirinin_bakiyesini_okur` — `Expected istekler not to be empty because GUID'siz satırın kişisinde gönderilebilir bir sahip var` (bugünkü yöntem yalnız `Guid.TryParseExact(_customerId, "N")`'e bakar; `yerel-…` ayrıştırılamaz → metin, istek yok).
- `Sahip_kurali_gonderilemeyen_satiri_atlar` — aynı neden, aynı ileti (boş liste).
- `Sahip_okumasi_atarsa_bakiye_yuklenemedi_metni` — `Expected k.Vm.BalanceError to start with "Bakiye yüklenemedi:" … but "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor." differs` (bugünkü yöntem DB okumaz; bozuk fabrikaya hiç gitmez).
- Geçen dördü: `Guidli_satirdan_…` (açılan satır GUID'li), `Hic_guidli_…`, `Guidli_uyeleri_gonderilemeyen_…`, `Grupsuz_…` (hepsi metne düşer, istek yok).

- [ ] **Adım 5: En küçük uygulama — sahip yedeği**

`OrderDeck.App/ViewModels/CustomerDetailViewModel.cs:229-239` — bugünkü blok (Adım 3'ten sonraki hâli):

```csharp
    /// <summary>Customer.Id'yi server projection ID'ye parse eder.
    /// PR #88 sonrası ingest edilen müşteriler için Id zaten hex N format
    /// (== WpfCustomerProjection.Id). Eski Customer'lar (shopper app öncesi)
    /// projection ile eşleşmemiş olabilir — bu durumda parse fail, balance
    /// section gizli kalır.</summary>
    internal bool TryGetProjectionId(out Guid id)
    {
        id = Guid.Empty;
        if (_customerId is null) return false;
        return Guid.TryParseExact(_customerId, "N", out id);
    }
```

yerine:

```csharp
    /// <summary>Kartın bakiye kimliği (server WpfCustomerProjection.Id). Açılan satır GUID ("N") ise o satır — bugünkü gibi.
    /// Değilse (eski/yerel satır) kişinin sahip satırı (spec 2026-10-11 §6.1-f; GetPaymentPerson(id).OwnerId, sahip kuralı:
    /// GUID'li ve sunucunun kabul edeceği satır). Böylece arama, sohbet ve kara listeden açılan kartlar aynı kişinin bakiyesini
    /// okur; havuz kipinde panel GET bütün üyeler için aynı toplamı döner. Sahip yoksa (GUID'li ya da gönderilebilir üye yok)
    /// false: "server eşlemesi yok" metni — o satırlar sunucuya hiç ulaşmaz. Bakiye okuması ve "Bakiye Ekle" bu Id'yi kullanır.
    /// internal: testler sahip yedeğini doğrudan sınar.</summary>
    internal bool TryGetProjectionId(out Guid id)
    {
        id = Guid.Empty;
        if (_customerId is null) return false;
        if (Guid.TryParseExact(_customerId, "N", out id)) return true;
        var owner = _customers.GetPaymentPerson(_customerId).OwnerId;
        return owner is not null && Guid.TryParseExact(owner, "N", out id);
    }
```

Ardından `ReloadBalanceAsync`'in kapı satırı (`:241-249`) — bugünkü:

```csharp
    public async Task ReloadBalanceAsync()
    {
        if (!TryGetProjectionId(out var projectionId))
        {
            BalanceLoaded = false;
            BalanceError = "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor.";
            BalanceTransactions.Clear();
            return;
        }
```

yerine:

```csharp
    public async Task ReloadBalanceAsync()
    {
        // Sahip yedeği yerel DB okur (GetPaymentPerson). Load bunu ateşle-unut çağırır: istisna Task'ta yutulur,
        // kart "0,00 TL"de hatasız kalırdı — bugünkü hata metniyle gösterilir.
        bool mapped;
        Guid projectionId;
        try { mapped = TryGetProjectionId(out projectionId); }
        catch (Exception ex)
        {
            BalanceError = $"Bakiye yüklenemedi: {ex.Message}";
            BalanceLoaded = false;
            BalanceTransactions.Clear();
            return;
        }
        if (!mapped)
        {
            BalanceLoaded = false;
            BalanceError = "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor.";
            BalanceTransactions.Clear();
            return;
        }
```

`:250-266` (`try { … GetCustomerBalanceAsync(projectionId, …) … } catch …`) aynen kalır; `projectionId` artık `out var` yerine önceden bildirilmiş yerel. Derleme: `catch` dalı `return` ettiği için `projectionId` `try` sonrasında kesin atanmış (CS0165 yok — `out` argümanı çağrı dönünce atanmış sayılır, istisna yolu `return` ile çıkar). Metin `$"Bakiye yüklenemedi: {ex.Message}"` dosyadaki `:264` metninin aynısı; yeni kullanıcı metni yok.

Derleme notları: `_customers` (`CustomerRepository`, `:24`) ve `GetPaymentPerson` (`CustomerRepository.cs:601`) var; `PaymentPerson` için `using OrderDeck.Core.Customers;` dosyada zaten var (`:14`), `var` kullanıldığı için tür adı da geçmiyor. `TryParseExact` başarısızken `id`'yi `Guid.Empty` yapar → `false` dönüşlerinde `id` boş kalır (çağıranlar zaten okumaz). `ReloadBalanceAsync`'te yalnız kapı satırı değişir (yukarıda); `AddBalanceAsync` (`:269-290`) değişmez.

- [ ] **Adım 6: Koştur — PASS**

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerDetailOwnerBalanceTests"
```

Beklenen: 7/7 PASS.

- [ ] **Adım 7: Derleme — 0 uyarı**

```powershell
dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q
```

Beklenen: `0 Warning(s)`, `0 Error(s)` (`TreatWarningsAsErrors`).

- [ ] **Adım 8: Regresyon (var olan testler DEĞİŞTİRİLMEDEN)**

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerSearchViewModelTests|FullyQualifiedName~CustomerDetailViewModelTests|FullyQualifiedName~CustomerDetailRoutingTests|FullyQualifiedName~MainShellViewCompositionTests|FullyQualifiedName~CustomerSyncDiTests|FullyQualifiedName~CustomerSearchUnmergeTests|FullyQualifiedName~PaymentPersonViewModelTests"
```

Beklenen: hepsi PASS. Gerekçe:
- **R-Arayüz** — `CustomerDetailViewModelTests`'in harness'ı her isteğe 404 dönen istemciyle kurulur, müşteri Id'si `"c1"` (GUID'siz). `Load` artık `GetPaymentPerson("c1")`'i okur; grupsuz tek GUID'siz üye → `OwnerId` null → bugünkü metin, istek yok. Davranış aynı; hiçbir test bakiye iddiası taşımıyor. `CustomerDetailRoutingTests` (`:71`) kendi kurulumuyla aynı VM'yi kurar, ctor değişmedi.
- `CustomerSyncDiTests` — VM DI'dan çözülür (`:152`), ctor değişmedi.
- `CustomerSearchUnmergeTests` ve `PaymentPersonViewModelTests` — Görev 7'nin `BalanceApiStub` panel kolunu paylaşan komşular; bu görev stub'a dokunmaz.

- [ ] **Adım 9: Commit**

```bash
git add OrderDeck.App/ViewModels/CustomerDetailViewModel.cs OrderDeck.Tests/ViewModels/CustomerDetailOwnerBalanceTests.cs
git commit -F - <<'MSG'
feat(balance): GUID'siz satırdan açılan kart kişinin sahip satırının bakiyesini göstersin

Spec 2026-10-11 §6.1-f: TryGetProjectionId açılan satır GUID ("N") değilse
kişinin sahip satırına (GetPaymentPerson(id).OwnerId, sahip kuralı §6.1-a:
GUID'li ve sunucunun kabul edeceği satır) düşer. Bakiye okuması ve "Bakiye
Ekle" aynı Id'yi kullanır; arama, sohbet ve kara listeden açılan kartlar aynı
kişinin bakiyesini gösterir (havuz kipinde panel GET Σ_S döner). Açılan satır
GUID'liyse bugünkü gibi o satır. Sahip yoksa (GUID'li üye yok ya da hiçbiri
gönderilemez) bugünkü "server eşlemesi yok" metni; metinler değişmez.
Sahip okuması yerel DB'ye gittiği için ReloadBalanceAsync kapıyı sarar:
okuma atarsa (Load ateşle-unut çağırır) bugünkü "Bakiye yüklenemedi: …"
metni gösterilir, kart sessizce 0,00 TL'de kalmaz. Yöntem testler için
internal.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 9: Tam WPF paketi, spec'in bayat satırları, sürüm / saha notları (spec §7.1 adım 6–9, §7.2, §7.5, §8.5, §10; iskelet fark 4, 12, 18, 20, 24)

**Files:**
- Değiştir: `docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md` — yalnız bayat satırlar: başlık `:3` (Durum), §4.12 `:324` (son cümle), §5.2 "Sunucuya varış" `:373` (son cümle), §10 "Açık kalanlar" `:695` (madde) ve `:696` (alt maddenin son cümlesi); ayrıca Görev 6 Not 4'ün (fark 12) bayatlattığı iki satır: §6.1 girişi `:404` ve §7.1 adım 6 `:496`; ve Görev 3'ün (fark 4) bayatlattığı §6.1-a `:409` (`ReplayAsync` üyelerinin kaynağı).
- Kaynak kod: YOK. Test: YOK (yalnız koşturma: tam `OrderDeck.Tests`, `OrderDeck.Licensing.Tests`, Release çözüm derlemesi).

Bu görev üretim koduna ve testlere dokunmaz. Üç işi var: (1) Görev 1–8'in birlikte derlendiğini ve CI'nın biçimiyle (Release, `--no-build`, `xUnit.ParallelizeTestCollections=false` — `.github/workflows/build-test.yml:95-96`, `:107-124`) bütün WPF testlerinin yeşil olduğunu göstermek; (2) spec'in 12.10.2026 ek sahip kararıyla bayatlamış satırlarını (iskelet fark 18) Görev 6'nın satır kipi metin farkıyla (fark 12) bayatlamış iki satırını ve Görev 3'ün üye kaynağıyla (fark 4) bayatlamış §6.1-a satırını güncellemek; (3) planın sonundaki "Sürüm / saha notları" bölümü (Burak'ın koşacağı adımlar; bu görev koşturmaz). Tam pakette bir düşüş çıkarsa düzeltme bu görevde yapılmaz: hangi görevin dosyasına ait olduğu bulunur, düzeltme o görevin kapsamında (önce o görevin testine kırmızı test) yapılır.

**Not (spec ↔ kod):**
- **Başlık (`:3`).** Durum satırı hem "sahip kararları alındı (§10)" hem "bir sahip kararı açık (§10)" diyor; talimattaki "→ sahip kararları alındı (§10; 12.10 ek kararı dahil)" değişikliği ikinci ifadeyi silip birinciye eki koyarak yapılır (aynı ifade iki kez yazılmaz). "uygulama planı bekliyor" → üç planın yolu. W-havuz planının adı `docs/superpowers/plans/2026-11-02-ortak-bakiye-w-havuz.md` (iskeletin kesit tarihi); plan başka adla kaydedildiyse Adım 7'deki Edit'te o ad yazılır (Adım 6 ile doğrulanır).
- **`:696` alt maddesi** ("Aynı durum: kopyaya dönüşte grupsuz asıl kayıt") son cümlesiyle 695'teki karara bağlıydı ("…aynı kararla belirlenir"). Karar Ayır'ı engellemediği için bu durumda engellenecek bir işlem yoktur; cümle bunu söyleyecek biçimde güncellenir, alt maddenin geri kalanı aynen kalır.
- **§6.1 girişi (`:404`) ve §7.1 adım 6 (`:496`) — fark 12'nin sonucu (Görev 6 Not 4).** İki satır satır kipinde değişen davranışları (a), (e), (f) olarak sayar. Görev 6 ise spec §6.1-d'nin açık tablosunu uygular: yerel planın birleştirme sorusunun notu satır kipinde de nötr metne döner ("… iade o hesaba döner."; "bu istekte kullanılmaz" düşer). İki satıra bu ibare eklenir (7.6, 7.7); satırların geri kalanı aynen kalır. Sahip kuralının iki sonucu (iskelet fark 26: mod On'da çevrimdışı yol; fark 27: sahip adayı olmayan kişide iş açılmaz) spec'in sayımında (a)'nın içindedir; bu satırlara eklenmez, Adım 10'un PR metni ve saha notu 3 adıyla sayar.
- **§6.1-a `:409` — fark 4'ün sonucu (Görev 3).** Satır `ReplayAsync`'in üyeleri "çağrı anında `GetPaymentPerson(job.CustomerId)` ile" okuduğunu söyler. Görev 3 üyeleri akışın kişisinden kurar: kişi her tıklamanın girişinde okunur, önizlemeye ve `ReplayAsync`'e aynı anlık görüntü gider; tıklama içinde depo okuması, akış ortasında başka kişiye geçen çapada sunucunun yeni `S(a)`'sıyla tutarlı bir liste gönderip başka kişinin bakiyesini düşürürdü (iskelet fark 4). Satır bu tasarıma göre güncellenir (7.8); maddenin öbür satırları aynen kalır.
- **Dokunulmayanlar:** §10 "Numara izlemesinin hakemi" maddesi (`:697-699`) açık bir karar olarak kalır. `:699`'un son cümlesi ("Ayır, silinmiş üyesinde parası olan kişide engellenirse … bu yüzden ilk maddeyle birlikte karara bağlanır") 12.10 kararıyla bayatladı: Ayır engellenmedi, bu yüzden §7.3-4 kaleminin §8.1 uyarısı meşru Ayır sonucundan ayırt edilemez. Hakem kararı açık kaldığı için bu planda değiştirilmedi. **Sahip sorusu (iskeletin "Sahip soruları"na 4. madde olarak eklenir):** `:699`'daki §8.1 uyarısı seçeneği 12.10 kararından sonra hâlâ geçerli mi? §14 satır 21 (`:800`) ve §5.2 `:373`'teki tırnaklı kısa biçim ("silinmiş hesap — ayırınca kullanılamaz") inceleme geçmişi/özet metindir; talimat "başka spec değişikliği YOK" dediği için değişmez (istisnalar yukarıdaki `:404` ve `:496` — fark 12'nin sonucu — ve `:409` — fark 4'ün sonucu; ikisi de yeni tasarım değil, kodun davranışıyla bayatlayan satırın düzeltilmesi). Onay penceresinin tam metni §6.1-e'dedir (`:442-443`) ve kararla zaten uyumludur. §5.2 Örnek 3 (`:362`) değişmez: spec'in başka bölümleri yeniden yazılmaz, WPF onay metninin biçimi (iskelet fark 16) kodda ve §6.1-e'dedir.
- **Ayır sorusunun çevrimdışı gecikmesi (Görev 7 Not "Satır kipi").** Satır kipinde de Ayır'ın sorusu panel okumasını bekler; ağ kesikken istemcinin yeniden deneme politikası (`AppHost.cs:451`, `AddStandardResilienceHandler`: 3 yeniden deneme, üstel bekleme, toplam zaman aşımı 30 sn) yüzünden soru ~30 sn'ye kadar gecikir, düğme o sürede pasif kalır, ardından genel uyarı gelir (bugün Ayır anlıktır). Saha notu 3 bunu yazar. İskeletin Sahip sorusu 5'i: (A) ~30 sn bekleme kabul (plan bunu uygular); (B) panel okumasına kısa bir iptal süresi, ayrı görev, iptal genel uyarıya düşer.
- **S2 planının saha notu 5** (`docs/superpowers/plans/2026-10-29-ortak-bakiye-s2.md:13323`) `docker compose up -d license-server` der; ev kuralı `--force-recreate` + konteyner env doğrulaması (iskelet fark 24). S2 planı değişmez; doğru komut bu planın saha notlarındadır.
- **Spec §8.5 "≤ 60 sn"**: tetiklenen tur (Görev 5, `PullOnceAsync`) önce gönderir, sonra çeker: kendi PC'nin birleştirmesini hemen gönderir; başka PC'nin değişikliği o PC gönderince (gönderim döngüsü ≤ 60 sn, `WpfCustomerProjectionSyncHostedService.cs:13`) bu PC'de bir sonraki 409'un tetiklediği turun çekmesiyle ya da 30 sn'lik çekme döngüsüyle iner (`CustomerChangesPullHostedService.cs:18`, `DefaultCadence = TimeSpan.FromSeconds(30)`; belgesi `CustomerChangesPullService.cs:86`). Saha notu iki yolu da yazar.

- [ ] **Adım 1: Görev 1–8 yerinde ve kapsam dışına taşmamış (salt okunur, git-bash, worktree kökünden)**

```bash
git status --porcelain
git log --oneline "$(git merge-base origin/master HEAD)"..HEAD
```

Expected: `git status` boş; `git log` Görev 1–8'in commit'lerini listeler (Görev 9'unki henüz yok). Planın kendi docs commit'i varsa (`docs(balance): W-havuz uygulama planı`) o da listelenir. İstisna: `git status`'ta plan dosyası (`docs/superpowers/plans/…-ortak-bakiye-w-havuz.md`) `??` görünürse bu bir görev eksikliği değildir; dur, planı Burak'ın onayıyla ayrı bir `docs(balance): W-havuz uygulama planı` commit'iyle ekle, sonra Adım 1'i yinele. Başka bir satır görünürse görev eksik ya da kapsam dışı değişiklik var: dur.

İmzalar (iskeletin "Görevler arası imzalar" bloğu) yerinde mi — her komut en az bir satır basmalı. Çok desenli komutlarda `--all-match` şarttır: onsuz `git grep -e A -e B` desenlerden YALNIZ BİRİ eşleşse de satır basar ve eksik bir görevin imzasını gizler; `--all-match` ile dosyada bütün desenler yoksa komut hiçbir şey basmaz.

```bash
git grep -n "IsServerAcceptable" -- OrderDeck.Core/Customers/ServerCustomerRule.cs
git grep -n "IsOwnerCandidate" -- OrderDeck.Core/Customers/PaymentPerson.cs
git grep -n --all-match -e "MemberIds" -e "PersonPool" -e "CustomerBalanceMemberDto" -- OrderDeck.Licensing/Api/Models/CustomerBalanceDtos.cs
git grep -n "IReadOnlyCollection<Guid> memberIds" -- OrderDeck.Licensing/Api/LicenseApiClient.cs
git grep -n --all-match -e "MemberIdsOf" -e "BalanceNotSynced" -e "TriggerCustomerTour" -e "PersonPoolOf" -e "NotSyncedClock" -- OrderDeck.App/Services/PaymentRequestService.cs
git grep -n --all-match -e "NotSyncedTitle" -e "NotSyncedText" -e "NotSyncedPersistentText" -- OrderDeck.App/Services/PaymentResultPresenter.cs
git grep -n "BalanceNotSynced" -- OrderDeck.App/Services/PriceCorrection/CorrectedPaymentRequests.cs
git grep -n --all-match -e "UnmergeTitle" -e "UnmergeFallbackMessage" -e "UnmergeGroupAsync" -- OrderDeck.App/ViewModels/CustomerSearchViewModel.cs
git grep -n "class CustomerIdentityText" -- OrderDeck.App/Formatting/CustomerIdentityText.cs
git grep -n "internal bool TryGetProjectionId" -- OrderDeck.App/ViewModels/CustomerDetailViewModel.cs
git grep -n --all-match -e "confirmText" -- OrderDeck.App/Services/IDialogService.cs OrderDeck.App/Services/WpfDialogService.cs OrderDeck.App/Views/Drawers/MessageDrawer.xaml.cs
git grep -c "confirmText" -- OrderDeck.App/Services/IDialogService.cs OrderDeck.App/Services/WpfDialogService.cs OrderDeck.App/Views/Drawers/MessageDrawer.xaml.cs
```

Son komut (`-c`) üç dosyanın her biri için `<dosya>:<sayı>` basmalı; üç dosyadan biri listede yoksa o dosyada `confirmText` eksiktir.

Expected: on iki komutun hepsi satır basar (`--all-match`'li komutlarda bütün desenlerin satırları görünür). Biri boşsa o görev tamamlanmamıştır: dur, eksik görevi bitir.

Taşınan kural tek yerde (Görev 1) — boş olmalı:

```bash
git grep -n "private static bool IsServerAcceptable" -- OrderDeck.App
```

Kapsam ve göç (Genel kurallar: sunucu/Chat/web/deploy dokunulmaz, Licensing yalnız iki dosya, yerel şema 49 kalır, UA deseni değişmez, paket eklenmez):

```bash
base="$(git merge-base origin/master HEAD)"
git diff --name-only "$base" -- OrderDeck.LicenseServer OrderDeck.LicenseServer.Tests OrderDeck.Chat OrderDeck.Shared Extension web deploy
git diff --name-only "$base" -- OrderDeck.Licensing
git diff --name-only "$base" -- OrderDeck.Core/Storage/Migrations
git status --porcelain -- OrderDeck.Core/Storage/Migrations
git diff --name-only "$base" -- '*.csproj' Directory.Packages.props Directory.Build.targets global.json
git diff "$base" -- OrderDeck.App/AppHost.cs | grep -n "UserAgent"
```

Expected: 1. komut BOŞ; 2. komut tam iki satır (`OrderDeck.Licensing/Api/LicenseApiClient.cs`, `OrderDeck.Licensing/Api/Models/CustomerBalanceDtos.cs`); 3. ve 4. BOŞ (yeni göç yok — `049_sales_sync.sql` son göç kalır; saha notu 9'un "Wh yerel göç içermez" cümlesi bu iki boş çıktıya dayanır); 5. BOŞ; 6. BOŞ (UA satırı `AppHost.cs:448` değişmedi). Biri dolu çıkarsa **dur ve sor**: göç ya da sunucu değişikliği bu planın kapsamında değil.

- [ ] **Adım 2: Çözümün Release derlemesi — 0 uyarı (CI biçimi)**

PowerShell (worktree kökünden):

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'
dotnet build OrderDeck.sln --configuration Release --nologo -v q 2>&1 | Tee-Object -Variable derleme
"exit=$LASTEXITCODE"
$derleme | Select-String -Pattern ': (warning|error) '
```

git-bash karşılığı:

```bash
DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.sln --configuration Release --nologo -v q 2>&1 | tee /dev/stderr | grep -E ': (warning|error) '; echo "exit=${PIPESTATUS[0]}"
```

Expected: `exit=0`; `Select-String`/`grep` hiçbir satır basmaz. `OrderDeck.App`, `OrderDeck.Core`, `OrderDeck.Licensing` `TreatWarningsAsErrors` (CS1591 hariç) olduğu için oradaki her uyarı zaten derlemeyi düşürür. `OrderDeck.Tests`'te TWAE yok: bir uyarı satırı görünürse dosyasına bak — W-havuz'un eklediği/değiştirdiği bir test dosyasındaysa (`git diff --name-only "$base" -- OrderDeck.Tests`) o görevin kapsamında giderilir; dokunulmamış bir dosyadaysa önceden vardır, kapsam dışıdır, not edilir. (Çözüm derlemesi sunucu projelerini de derler; onlar değişmediği için master'daki gibi temizdir.)

- [ ] **Adım 3: Tam `OrderDeck.Tests` paketi (CI biçimi; ön planda, 10 dk zaman aşımı)**

Yürütücü (ana oturum) koşar; Monitor ya da arka plan bekleme yok. Komut ön planda, araç zaman aşımı 600000 ms. PowerShell:

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --configuration Release --no-build --nologo -v q --blame-hang-timeout 3m -- xUnit.ParallelizeTestCollections=false
"exit=$LASTEXITCODE"
```

git-bash karşılığı:

```bash
DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --configuration Release --no-build --nologo -v q --blame-hang-timeout 3m -- xUnit.ParallelizeTestCollections=false; echo "exit=$?"
```

Expected: `Passed!  - Failed:     0`, `exit=0`. Sayı plana ya da CLAUDE.md'ye yazılmaz (CI'dan okunur). Docker gerekmez (`OrderDeck.Tests` Testcontainers kullanmaz).

Komut 10 dakikayı aşarsa (araç zaman aşımı) paket iki yarıda, yine ön planda koşturulur; iki yarı birlikte bütün testleri kapsar:

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --configuration Release --no-build --nologo -v q --blame-hang-timeout 3m --filter "FullyQualifiedName~OrderDeck.Tests.Services|FullyQualifiedName~OrderDeck.Tests.Sync|FullyQualifiedName~OrderDeck.Tests.Storage" -- xUnit.ParallelizeTestCollections=false
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --configuration Release --no-build --nologo -v q --blame-hang-timeout 3m --filter "FullyQualifiedName!~OrderDeck.Tests.Services&FullyQualifiedName!~OrderDeck.Tests.Sync&FullyQualifiedName!~OrderDeck.Tests.Storage" -- xUnit.ParallelizeTestCollections=false
```

Expected: iki koşu da `Failed: 0`.

- [ ] **Adım 4: Düşüş varsa ayır (yalnız Adım 3 kırmızıysa)**

1. **Bilinen kırılgan süre testi** `OrderDeck.Tests/Storage/CustomerAggregateSqlTests.cs:309-341` `Prod_boyu_veritabaninda_acilis_hesabi_5_saniyenin_altinda` (20.000 müşteri / 200.000 etiket, 5 sn sınırı `:325`). Tam pakette yük altında düşerse tek başına koştur:

   ```powershell
   $env:DOTNET_CLI_UI_LANGUAGE='en'
   dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --configuration Release --no-build --nologo -v q --filter "FullyQualifiedName~CustomerAggregateSqlTests.Prod_boyu"
   ```

   Orada geçiyorsa W-havuz'la ilgisiz yük etkisidir (W-havuz `CustomerAggregateSql`'e dokunmaz); düzeltmesi kapsam dışı, PR açıklamasına bir satır not düşülür. Tek başına da düşüyorsa dur ve sor.
2. **Tam pakette ~30 sn'de geçen gizli flake adayları** (`TwoPcSalesConvergenceTests`, `SalesEditRouterTests`; bellek "yük altında düşen testler"). Test dosyasının değişmemiş olması ilgisizliği KANITLAMAZ: `OrderDeck.Tests/Sync/TwoPcSalesStack.cs` ve `TwoPcSalesConvergenceTests.Payments.cs` `PaymentRequestService`'i kurup sürer (W-havuz Görev 1, 3–6'nın dosyası); `TwoPcSalesStack` ayrıca `PaymentLeaseCoordinator`'ü (`TwoPcSalesStack.cs:313`, Görev 1) ve kişi okumasını taşıyan müşteri deposunu (`:238` `CustomerRepository` → `GetPaymentPerson` → `PaymentPersonSql`/`PaymentPerson`, Görev 1) doğrudan kurar. Düşerse:
   - Önce tek başına **3 kez** koştur (her biri ön planda):

     ```powershell
     $env:DOTNET_CLI_UI_LANGUAGE='en'
     1..3 | ForEach-Object { dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --configuration Release --no-build --nologo -v q --filter "FullyQualifiedName~<Sınıf>" -- xUnit.ParallelizeTestCollections=false; "koşu $_ exit=$LASTEXITCODE" }
     ```

     Üçünden biri düşerse yük etkisi değildir → madde 3.
   - Üçü de geçiyorsa iki kanıttan **ikisi de** "ilgisiz" demeli: (a) düşüşün yığın izi (`--blame-hang-timeout` dökümü ya da test çıktısı) W-havuz'un değiştirdiği türlere (`PaymentRequestService`, `PaymentResultPresenter`, `CorrectedPaymentRequests`, `CustomerSearchViewModel`, `CustomerDetailViewModel`, `PaymentPerson`/`PaymentOwnerRule`, `ServerCustomerRule`, `PaymentPersonSql`, `WpfCustomerProjectionSyncService`, `PaymentLeaseCoordinator`, `LicenseApiClient`, `MessageDrawer`, `WpfDialogService`) girmiyor; (b) aynı tam paket, base commit'in temiz bir worktree'sinde aynı yükle koşturulunca aynı test aynı biçimde düşüyor (`git stash` YOK):

     ```bash
     base="$(git merge-base origin/master HEAD)"
     git worktree add --detach ../wh-base-kontrol "$base"
     ```

     ```powershell
     $env:DOTNET_CLI_UI_LANGUAGE='en'
     dotnet build ../wh-base-kontrol/OrderDeck.sln --configuration Release --nologo -v q
     dotnet test ../wh-base-kontrol/OrderDeck.Tests/OrderDeck.Tests.csproj --configuration Release --no-build --nologo -v q --blame-hang-timeout 3m -- xUnit.ParallelizeTestCollections=false
     "exit=$LASTEXITCODE"
     ```

     Bitince `git worktree remove ../wh-base-kontrol`. Yalnız (a) ve (b) birlikte tutarsa PR açıklamasına bir satır not düşülür; biri tutmazsa madde 3.
   - **`TwoPcSalesConvergenceTests.Payments` kısmındaki bir düşüş varsayılan olarak W-havuz'a aittir** (ödeme akışı): doğrudan madde 3'ün yolu.
3. **Başka bir düşüş: dur.** Testin dosyasını ve dokunduğu üretim türünü W-havuz'un değiştirdiği dosyalarla karşılaştır (`git diff --name-only "$base" -- OrderDeck.App OrderDeck.Core OrderDeck.Licensing OrderDeck.Tests`). Nedeni bulunur (sistematik hata ayıklama; tahminle düzeltme yok). Düzeltme ilgili görevin kapsamındaysa o görevin test dosyasına önce kırmızı test, sonra düzeltme, sonra o görevin regresyon kümesi (iskelet "Genel kurallar" R-Kişi/R-Ödeme/R-ModOn/R-Senkron/R-İstemci/R-Arayüz) ve Adım 2–3 baştan. Var olan bir test spec gerekçesi olmadan değiştirilmez (bilerek değişen var olan satırlar yalnız iskeletin saydıklarıdır).

- [ ] **Adım 5: `OrderDeck.Licensing.Tests` (CI'nın ikinci WPF adımı)**

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'
dotnet test OrderDeck.Licensing.Tests/OrderDeck.Licensing.Tests.csproj --configuration Release --no-build --nologo -v q
"exit=$LASTEXITCODE"
```

git-bash: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Licensing.Tests/OrderDeck.Licensing.Tests.csproj --configuration Release --no-build --nologo -v q; echo "exit=$?"`

Expected: `Failed: 0`, `exit=0` (Görev 2'nin DTO ve aşırı yükleme eklemeleri `OrderDeck.Licensing`'in kendi testlerini bozmaz).

- [ ] **Adım 6: Spec'in bayat satırlarını göster (kırmızı)**

```bash
git grep -n -i -e "sahip kararı bekliyor" -e "bir sahip kararı açık" -e "uygulama planı bekliyor" -e "aynı kararla belirlenir" -e "bu üçü dışında bugünkü gibi" -e "kart düzeltmesi + sahip kuralı (§6.1-a) |" -e "üyeleri çağrı anında" -- docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md
ls docs/superpowers/plans/ | grep -i "ortak-bakiye"
```

Expected (FAIL — bayat satırlar duruyor): ilk komut sekiz satır basar: `:3` (başlık; "bir sahip kararı açık" ve "uygulama planı bekliyor" aynı satırda), `:324`, `:373`, `:404`, `:409`, `:496`, `:695`, `:696`. İkinci komut planların adlarını listeler: `2026-10-19-ortak-bakiye-s1.md`, `2026-10-29-ortak-bakiye-s2.md` ve bu planın dosyası (beklenen `2026-11-02-ortak-bakiye-w-havuz.md`; ad farklıysa Adım 7'nin ilk Edit'inde o ad yazılır). İkinci komut W-havuz planını listelemiyorsa ya da plan dosyası `git status --porcelain -- docs/superpowers/plans`'ta izlenmeyen (`??`) görünüyorsa **dur**: 7.1'de var olmayan ya da commit edilmemiş bir yol yazılmaz. Planı önce Burak'ın onayıyla ayrı bir `docs(balance): W-havuz uygulama planı` commit'iyle ekle, sonra Adım 7'ye geç.

- [ ] **Adım 7: Spec'i güncelle (Edit aracıyla; dosya CRLF — PowerShell `Set-Content` kullanma)**

Sekiz ayrı Edit; her `old_string` spec'teki metnin aynısıdır (satırlar tek tek; çok satırlı eşleme yok).

**7.1 — başlık `:3`.** `old_string`:

```text
**Durum:** sahip kararları alındı (§10), tasarım incelemesi (§13) ve spec incelemesi (§14) işlendi, bir sahip kararı açık (§10), satış senkronu bağımlılıkları işlendi (2026-10-16, §15), uygulama planı bekliyor ·
```

`new_string`:

```text
**Durum:** sahip kararları alındı (§10; 12.10 ek kararı dahil), tasarım incelemesi (§13) ve spec incelemesi (§14) işlendi, satış senkronu bağımlılıkları işlendi (2026-10-16, §15), uygulama planları: S1 `docs/superpowers/plans/2026-10-19-ortak-bakiye-s1.md`, S2 `docs/superpowers/plans/2026-10-29-ortak-bakiye-s2.md`, W-havuz `docs/superpowers/plans/2026-11-02-ortak-bakiye-w-havuz.md` ·
```

**7.2 — §4.12 `:324` (son cümle).** `old_string`:

```text
Aynı sonuç, numaranın son canlı üyesi grupsuz bir asıl kayda kopya olunca da doğar; orada onay penceresi yoktur. Sahip kararı bekliyor (§10 açık kalanlar).
```

`new_string`:

```text
Aynı sonuç, numaranın son canlı üyesi grupsuz bir asıl kayda kopya olunca da doğar; orada onay penceresi yoktur. **Karar (12.10.2026):** Ayır serbest + uyarı; onay penceresi silinmiş hesabın tutarını "(silinmiş hesap): X TL — ayırınca kullanılamaz" diye gösterir (§10).
```

**7.3 — §5.2 "Sunucuya varış" `:373` (son cümle).** `old_string`:

```text
Onay penceresi bunu "silinmiş hesap — ayırınca kullanılamaz" diye ayrıca gösterir. K3'ün bu istisnası sahip kararı bekliyor (§10).
```

`new_string`:

```text
Onay penceresi bunu "silinmiş hesap — ayırınca kullanılamaz" diye ayrıca gösterir. K3'ün bu istisnası karara bağlandı (12.10.2026, §10): Ayır serbest + uyarı.
```

**7.4 — §10 "Açık kalanlar" `:695`.** `old_string` (satırın tamamı):

```text
- **Sahip kararı bekliyor — Ayır'da silinmiş hesabın parası (K3 istisnası).** Bütün canlı hesaplar ayrılınca silinmiş hesap tek kalır; parası hiçbir canlı kayda bağlı değildir ve kullanılamaz (§4.12, §5.2). Bu tasarımın varsayılanı: Ayır serbest, onay penceresi tutarı "silinmiş hesap — ayırınca kullanılamaz" diye gösterir. Seçenek: silinmiş üyesinde parası olan kişide Ayır engellenir. Karar Ayır onayının metnini ve davranışını belirler (W-havuz); S1 ve S2'yi bloklamaz.
```

`new_string`:

```text
- **Ayır'da silinmiş hesabın parası (K3 istisnası).** **Karar (12.10.2026):** silinmiş hesapta para varken bütün canlı hesaplar ayrılırsa Ayır SERBEST + uyarı; onay penceresi silinmiş hesabın tutarını "(silinmiş hesap): X TL — ayırınca kullanılamaz" diye gösterir (W-havuz Görev 7). Bütün canlı hesaplar ayrılınca silinmiş hesap tek kalır; parası hiçbir canlı kayda bağlı değildir ve kullanılamaz (§4.12, §5.2). Seçilmeyen seçenek: silinmiş üyesinde parası olan kişide Ayır'ın engellenmesi.
```

**7.5 — `:696` alt maddesinin son cümlesi.** `old_string`:

```text
`verify`'ın §7.3-4 kalemi ("grubunda canlı asıl üye kalmamış") görür. Ayır kararının bu duruma da uygulanıp uygulanmayacağı aynı kararla belirlenir.
```

`new_string`:

```text
`verify`'ın §7.3-4 kalemi ("grubunda canlı asıl üye kalmamış") görür. 12.10 kararı Ayır'ı engellemediği için bu durumda da engellenecek bir işlem yoktur; kalem `verify`'da sayılmaya devam eder.
```

**7.6 — §6.1 girişi `:404`.** `old_string`:

```text
Wh, S1 ya da havuz kipi kapalı bir sunucuyla bu üçü dışında bugünkü gibi çalışır;
```

`new_string`:

```text
Wh, S1 ya da havuz kipi kapalı bir sunucuyla bu üçü ve yerel planın birleştirme notunun nötr metne dönmesi (§6.1-d) dışında bugünkü gibi çalışır;
```

**7.7 — §7.1 adım 6 `:496`.** `old_string`:

```text
Satır kipinde Wh = #506 davranışı + Ayır onayı + kart düzeltmesi + sahip kuralı (§6.1-a) |
```

`new_string`:

```text
Satır kipinde Wh = #506 davranışı + Ayır onayı + kart düzeltmesi + sahip kuralı (§6.1-a) + yerel planın nötr birleştirme notu (§6.1-d) |
```

**7.8 — §6.1-a `:409` (`ReplayAsync` üyelerinin kaynağı).** `old_string` (satırın tamamı):

```text
- `ReplayAsync` (`:1315-1362`) yalnız işi bilir; üyeleri çağrı anında `GetPaymentPerson(job.CustomerId)` (`CustomerRepository.cs:607-612`) ile okur.
```

`new_string`:

```text
- `ReplayAsync` (`:1315-1362`) yalnız işi bilir; üyeleri akışın kişisinden alır: kişi her tıklamanın girişinde okunur (`GetPaymentPerson`) ve önizlemeye de `ReplayAsync`'e de aynı anlık görüntü gider, tıklama içinde depo yeniden okunmaz. Çekme turu akış ortasında yerel `GroupId`'yi yazabilir; yeniden okuma, çapası başka kişiye geçmiş bir kişide sunucunun yeni `S(a)`'sıyla tutarlı bir liste gönderip başka kişinin bakiyesini düşürürdü. İş açıldıktan sonra birleşen üye sonraki tıklamanın oynatmasında gider (W-havuz planı, fark 4).
```

- [ ] **Adım 8: Spec doğrulaması (yeşil)**

```bash
git grep -n -i -e "sahip kararı bekliyor" -e "bir sahip kararı açık" -e "uygulama planı bekliyor" -e "aynı kararla belirlenir" -e "bu üçü dışında bugünkü gibi" -e "kart düzeltmesi + sahip kuralı (§6.1-a) |" -e "üyeleri çağrı anında" -- docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md
git grep -n -e "Karar (12.10.2026)" -e "12.10 ek kararı dahil" -e "karara bağlandı (12.10.2026" -e "12.10 kararı Ayır'ı engellemediği" -e "nötr metne dönmesi (§6.1-d)" -e "nötr birleştirme notu (§6.1-d)" -e "üyeleri akışın kişisinden alır" -- docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md
git diff --stat
git diff --word-diff=plain -- docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md
```

Expected: ilk komut BOŞ. İkinci komut sekiz satır basar (`:3`, `:324`, `:373`, `:404`, `:409`, `:496`, `:695`, `:696`). `git diff --stat` yalnız `docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md | 16 ++++++++--------` (`1 file changed, 8 insertions(+), 8 deletions(-)`; satır sayısı değişmez, CRLF korunur — satır sonlarında `^M` farkı görünmemeli). Kelime farkını gözle oku: yalnız Adım 7'nin sekiz değişikliği görünür, başka satır değişmemiş.

- [ ] **Adım 9: Commit**

```bash
git add docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md
git commit -F - <<'MSG'
docs(balance): spec'i 12.10 Ayır kararına ve W-havuz tasarımına göre güncelle

Ortak bakiye spec'i Ayır'da silinmiş hesabın parasını hâlâ "sahip kararı
bekliyor" diye anlatıyordu. Karar 12.10.2026'da alındı: silinmiş hesapta
para varken bütün canlı hesaplar ayrılırsa Ayır serbest + uyarı; onay
penceresi silinmiş hesabın tutarını "(silinmiş hesap): X TL — ayırınca
kullanılamaz" diye gösterir (W-havuz Görev 7). Başlık, §4.12, §5.2 ve §10
açık kalanlar kararla güncellenir; başlık üç uygulama planına bağlanır.
§6.1 girişi ve §7.1 adım 6 yerel planın nötr notunu satır kipi farkı
olarak sayar. §6.1-a, ReplayAsync'in üyeleri akışın kişisinden aldığını
söyler: tıklama içinde depo yeniden okunmaz (W-havuz planı, fark 4).
Davranış değişikliği yok, yalnız belge.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

Expected: tek dosyalı commit; `git status` boş.

- [ ] **Adım 10: Push ve PR — yalnız Burak isterse**

Bu plan push etmez. Burak PR isterse: `git push -u origin feat/ortak-bakiye-w-havuz`; başlık `feat(balance): ortak bakiye W-havuz — memberIds, customer-not-synced, Ayır onayı, kart sahip yedeği`; PR **DRAFT** açılır (bellek: yayın kapılı PR DRAFT kalır). Açıklamada: "Sunucu, panel, Shopper değişmez; yerel göç yok (şema 49); satır kipinde davranış yalnız sahip kuralı, Ayır onayı, kart düzeltmesi ve yerel planın nötr not metni (fark 12) ile değişir; sahip kuralının iki sonucu adıyla: mod On'da ağ kesikken yalnız gönderilemeyen GUID'li üyeleri olan kişide mesaj yerel yoldan gider, kayıt bağlantı gelince yazılır (fark 26); hiç sahip adayı olmayan kişide kişi × kapsam işi açılmaz, aynı kapsamda ikinci tıklama 'tekrar gönderilsin mi?' sorulmadan gider (fark 27, GUID'siz kişinin bugünkü yolu); yeni davranışların hepsi sunucu sinyaline bağlı; memberIds akışın kişisinden kurulur: spec §6.1-a l.409'un çağrı anında depo okuması tıklama düzeyinde karşılanır (fark 4: tıklama içinde depo okuması yanlış kişiden çekim açar; spec satırı Görev 9 Adım 7.8'de güncellendi)" — "göç yok" cümlesinin kanıtı Adım 1'in iki boş göç çıktısıdır. Satır kipi uyumunun sunucu kanıtı olarak iskelet fark 23'ün dört S2 testi anılır. Ayrıca iskeletin Sahip soruları: 1, 2, 3, 5 ve 6'nın uygulanan önerileri (hepsi A; 5 = çevrimdışı Ayır gecikmesi kabul, 6 = Görev 1-b), açık kalan 4. soru (hakem/§8.1 uyarısı); saha notu 1a'nın ölçüm sonucu (yapıldıysa); planın sonundaki "Sürüm / saha notları". PR açıldıktan sonra commit eklenirse Burak'a açıkça söylenir (merge, sayfa açıldığındaki head'de kalır). Açıklamanın son satırı `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Merge Burak'ta, **yayın penceresi dışında** (Paz/Pzt/Çar/Per 20:00–01:00 TR değil); merge = master'a giriş + sunucu deploy iş akışının koşması (sunucu kodu değişmediği için `/ready` 200 yeterli). WPF sahaya ancak Velopack sürümüyle çıkar.

---

## Sürüm / saha notları (W-havuz sonrası; spec §7.1 adım 6–9, §7.2, §8.5)

Bu adımların hepsini **Burak** yapar; plan hiçbirini koşturmaz. Prod'a yazan her adım (merge, tag, web bump, `.env`, `enable`/`disable`) Burak'ın onayıyla ve **yayın penceresi dışında** yapılır (Paz/Pzt/Çar/Per 20:00–01:00 TR değil). Geliştirme PC'si yayın PC'si değildir: yayın saatlerinde yerel deneme serbesttir, yasak yalnız merge, deploy ve sürümdür. Prod DB sorgularını Burak VPS'te koşar (SA parolası okunmaz, ekrana basılmaz); sorgular salt okunurdur ve çıktıları yalnız Id, sayı, sürüm, tutar ve zaman taşır, ad, kullanıcı adı ya da telefon taşımaz. Doğrulama gerçek etkiden yapılır: DB satırı, sunucu yanıtı, ekran, günlük. "Merge oldu" sahaya çıktı demek değildir: WPF değişikliği ancak Velopack sürümü ve PC güncellemesiyle çıkar. Komutlarda yer tutucu yoktur: lisans Id'leri aşağıdaki sorgularla kabuk değişkenlerine alınır, sürüm `WH=0.9.13` satırındadır.

**0. Ortak kabuk tanımları (VPS, Burak'ın kendi SSH oturumu; her yeni oturumda bir kez).**

Salt okunur sorgu yardımcısı. Sorgu heredoc'tan bir `.sql` dosyasına yazılır, konteynere kopyalanır, `sqlcmd -i` ile koşar ve silinir: iç içe tırnak `SSH → docker → sh → sqlcmd` zincirinde bozulmaz (bellek: `.sql` dosyası → `docker cp` → `sqlcmd -C -f 65001 -i`). Parola konteynerin kendi ortamından (`MSSQL_SA_PASSWORD`) okunur. `mktemp` dosyası 0600 açar; konteynerdeki `mssql` kullanıcısı okuyabilsin diye 644 yapılır. Konteynerdeki dosya kök kullanıcıyla silinir (`/tmp` yapışkan).

```bash
odsql() {
  local f; f=$(mktemp /tmp/wh-XXXXXX.sql) || return 1
  chmod 644 "$f"; cat > "$f"
  docker cp "$f" orderdeck-sqlserver:/tmp/wh.sql >/dev/null \
    && docker exec orderdeck-sqlserver sh -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d OrderDeckLicense -C -No -f 65001 -W -s "|" '"${1:-}"' -i /tmp/wh.sql'
  docker exec -u 0 orderdeck-sqlserver rm -f /tmp/wh.sql
  rm -f "$f"
}
odsql_id() { odsql "-h -1" | tr -d '\r' | grep -oE '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'; }
WH=0.9.13   # Burak'ın etiketlediği sürüm; başka bir numara seçtiyse yalnız bu satır değişir
```

Pilot lisans. 31.10'daki `enable` kuru koşusu bu lisansta yapıldı, son 14 günde etkin PC'si olan tek lisans da odur (bellek, 28.10 ve 31.10). Repo herkese açık olduğu için lisansın Id'si (öneki dahil) burada yazılmaz: Id, VPS'te Burak'ın ev dizinindeki `~/.orderdeck-pilot` dosyasında tek satır olarak durur. Dosya yoksa onu yazan tam komutu Claude verir (önek bellekte, `project_odeme_kisi_anahtari.md`). Aşağıdaki komut dosyadaki Id'nin `Licenses`'ta tek bir satıra karşılık geldiğini de doğrular; dosya yoksa ya da Id tutmuyorsa `DUR` basar. Dosyanın içeriği yalnız onaltılık rakam ve tire olarak okunur. Burak başka bir pilot seçerse dosya o lisansın Id'siyle yeniden yazılır.

```bash
PILOT=$(odsql_id <<SQL
SET NOCOUNT ON;
SELECT LOWER(CONVERT(varchar(36), Id)) FROM Licenses WHERE LOWER(CONVERT(varchar(36), Id)) = '$(tr 'A-F' 'a-f' 2>/dev/null < ~/.orderdeck-pilot | tr -cd '0-9a-f-')';
SQL
)
[ "$(printf '%s\n' "$PILOT" | grep -c .)" = 1 ] && echo "PILOT=$PILOT" || echo "DUR: ~/.orderdeck-pilot yok ya da tek bir lisansı göstermiyor"
```

Lisansların durumu. Satış senkronu sütunu `0` = Off, `1` = On; `HavuzSince` boş = satır kipi.

```bash
odsql <<'SQL'
SET NOCOUNT ON;
SELECT LOWER(CONVERT(varchar(36), l.Id)) AS Lisans, l.SalesSyncMode AS SatisSenkronu,
       CONVERT(varchar(16), l.PersonBalancePoolSince, 126) AS HavuzSince,
       (SELECT COUNT(*) FROM Activations a WHERE a.LicenseId = l.Id AND a.DeactivatedAt IS NULL
          AND a.LastSeenAt >= DATEADD(day, -14, SYSDATETIMEOFFSET())) AS EtkinPc14g
FROM Licenses l
WHERE l.RevokedAt IS NULL
ORDER BY l.Id;
SQL
```

Beklenen (Wh öncesi): bütün satırlarda `HavuzSince` `NULL`; `EtkinPc14g` yalnız pilotta sıfırdan büyük.

**1. Merge (spec §7.1 adım 6'nın ilk yarısı).**
- Görev 9 Adım 2, 3 ve 5 yeşil; CI'nın `build-test` işi yeşil. Test sayısı yazılmaz, CI'dan okunur.
- PR DRAFT'tır; hazır duruma alma ve merge Burak'ındır, yayın penceresi dışında. PR açıldıktan sonra commit eklendiyse merge'den önce Burak'a söylenir (merge, sayfa açıldığındaki head'de kalır).
- Merge yalnız master'a girer. Sunucu deploy iş akışı da koşar, ama sunucu kodu değişmediği için prod davranışı aynıdır: `curl -s -o /dev/null -w "%{http_code}\n" https://license.orderdeckapp.com/ready` → `200`.
- Merge'den sonra hiçbir lisans havuz kipinde değildir ve Wh henüz yoktur. Sahada hiçbir şey değişmez.

**1a. Wh öncesi ölçüm (her yayın PC'sinin verisiyle; Sahip sorusu 2 ve 6'nın girdisi).** Sahibi gönderilemeyen (`ServerCustomerRule`'ı tutmayan) işleri sayan salt okunur sorgu (Görev 1 "Saha notu"). `sqlite3` komut satırı aracı yayın PC'lerinde de geliştirme PC'sinde de kurulu değil; hiçbir PC'ye araç kurulmaz. Sorgu geliştirme PC'sinde, Python'un yerleşik `sqlite3` modülüyle ve salt okunur (`mode=ro`) koşar.
- **Kopyalama (her yayın PC'si, yayın penceresi dışında):** OrderDeck tamamen kapatılır (tepsi simgesi dahil); `Documents\OrderDeck\data\orderdeck.db` (yanında `orderdeck.db-wal` varsa o da) geliştirme PC'sinde PC başına ayrı bir klasöre kopyalanır: `C:\Users\burak\wh-olcum\<pc-adı>\`. Sonra OrderDeck yeniden açılır. Kopyalar müşteri verisi taşır: ölçümden sonra silinir (son komut).
- **Sorgu (geliştirme PC'si, PowerShell; klasördeki bütün kopyaları sırayla sayar).** Çıktı yalnız sayıdır (ad, telefon, Id yok). Komut 03.11.2026'da geliştirme PC'sinde, sahte iki satır eklenmiş bir kopyada denendi: `adaysiz` ve `karisik` satırlarını doğru ayırdı.

```powershell
$sql = @'
SELECT CASE WHEN EXISTS (SELECT 1 FROM Customer o WHERE c.GroupId IS NOT NULL AND o.GroupId = c.GroupId AND o.Id <> c.Id AND length(o.Id) = 32 AND o.Id NOT GLOB '*[^0-9a-fA-F]*' AND trim(o.Platform) <> '' AND length(o.Platform) <= 32 AND trim(o.Username) <> '' AND length(o.Username) <= 128) THEN 'karisik' ELSE 'adaysiz' END AS Kisi, CASE WHEN j.ScopeKey = 'cumulative' THEN 'cumulative' ELSE 'session' END AS Kapsam, CASE WHEN j.ClosedAt IS NULL THEN 'acik' ELSE 'kapali' END AS Durum, j.State AS Durum2, COUNT(*) AS IsSayisi FROM PaymentJob j JOIN Customer c ON c.Id = j.CustomerId WHERE j.ScopeKey NOT LIKE 'legacy%' AND (c.Platform IS NULL OR trim(c.Platform) = '' OR length(c.Platform) > 32 OR c.Username IS NULL OR trim(c.Username) = '' OR length(c.Username) > 128) GROUP BY 1, 2, 3, 4;
'@
Get-ChildItem "$env:USERPROFILE\wh-olcum" -Recurse -Filter orderdeck.db | ForEach-Object {
    "== $($_.Directory.Name)"
    python -I -c "import sqlite3,sys,pathlib; c=sqlite3.connect(pathlib.Path(sys.argv[1]).resolve().as_uri()+'?mode=ro', uri=True); rows=list(c.execute(sys.argv[2])); print('etkilenen is yok' if not rows else '\n'.join('|'.join(map(str,r)) for r in rows))" $_.FullName $sql
}
```

- Satır biçimi `Kisi|Kapsam|Durum|Durum2|IsSayisi`. `Kisi` iki soruyu ayırır: `karisik` = kişinin gönderilebilir başka GUID'li üyesi var (Sahip sorusu 2 / Görev 1-a: iş eski satırda kalır), `adaysiz` = kişinin hiç sahip adayı yok (Sahip sorusu 6 / Görev 1-b: iş artık okunmaz, Resend/Merge sorusu çıkmaz). `etkilenen is yok` = o PC'de etkilenen iş yok. `cumulative` satırları `kapali` da olsa sayılır (`FindOrCreateForPerson` kapalı işi de bulur ve revize eder); `session` satırları yalnız süren yayında önemlidir. Sorgu `IsNullOrWhiteSpace` yerine `trim`, UTF-16 yerine karakter uzunluğu kullanır; büyüklük ölçümü için yeterlidir.
- Sayılar bir oturumda (Claude) da okunabilir: kopyalar klasöre konunca aynı komut koşturulur, yalnız sayılar raporlanır.
- Sayı 0 değilse Sahip sorusu 2 ve 6'nın (A)/(B) kararı Burak'a yeniden sorulur. (B) seçilirse ayrı bir görev açılır, spec §8.4-3 ya da §6.1-a güncellenir ve iş Wh'den önce yapılır. (A) seçilirse kod yoktur, 2. maddeye geçilir.
- Kopyaları sil (ölçüm bitince):

```powershell
Remove-Item -LiteralPath "$env:USERPROFILE\wh-olcum" -Recurse -Force
```

**2. Wh Velopack sürümü (sürüm akışı: bellek `project_app_update_channel.md`; sürüm dersleri: `project_surum_yayin_borcu.md`).**
- **Numara:** Burak seçer, beklenen `0.9.13` (son yayımlanan `0.9.12`). **Wh ≥ 0.9.13** ve **Wh ⊇ Ws** (Ws = `SalesSync:MinClientVersion` = `0.9.11`) mekaniktir: `0.9.13 > 0.9.11` ve Wh satış senkronunun W1–W3'ünü taşır. **Ön sürüm eki yok** (`0.9.13-rc1` sunucunun `ClientVersion.IsAtLeast`'inde `0.9.13` alt sınırını geçmez). Kodda sürüm sabiti yok: numara tag'den gelir (`release.yml` "Compute version", `installer/build.ps1` `-p:Version`), UA `OrderDeck-WPF/<sürüm>` deseni değişmez (`AppHost.cs:448`).
- **İçerik ve tag (Burak'ın geliştirme PC'si, git-bash):**

```bash
git fetch origin --tags
git log -1 --format='%H %s' origin/master   # W-havuz merge commit'i (ya da ondan sonraki) olmalı
git log --oneline v0.9.12..origin/master -- OrderDeck.App OrderDeck.Core OrderDeck.Licensing   # sürüm notunun içeriği
WH=0.9.13
WH_SHA=$(git rev-parse origin/master)
git tag -a "v$WH" "$WH_SHA" -m "OrderDeck v$WH — ortak bakiye W-havuz"
git push origin "v$WH"
```

- `release.yml` (feed-baseline → build → publish) koşar. SSH flake'inde: `gh run rerun "$(gh run list --workflow release.yml -L 1 --json databaseId -q '.[0].databaseId')" --failed`.
- **Delta var mı:**

```bash
curl -sL --compressed https://orderdeckapp.com/downloads/velopack/releases.win.json | tr -d ' \r\n' | grep -oE '\{[^{}]*"Version":"'"$WH"'"[^{}]*\}'
```

  İki nesne beklenir: `"Type":"Full"` ve `"Type":"Delta"` (v0.9.12'de Full ~106 MB, Delta ~24 MB; boyutlar belleğe yazılır). Delta yoksa herkes tam paketi indirir: dur, nedeni bulunur.
- **GitHub sürüm notu** (Türkçe kısa özet, v0.9.10 emsali): ödeme isteği kişinin bütün hesaplarını sunucuya bildirir; "Henüz eşitlenmedi" uyarısı; "Grubu ayır" onayı hesap başına bakiyeyi gösterir; GUID'siz satırdan açılan müşteri kartı kişinin bakiyesini gösterir. Yerel göç yok (şema 49).
- **Web bump:** `web/lib/i18n.ts` `LATEST_RELEASE` (`version`, `releasedAt`; installer büyüdüyse `sizeMB`) → PR → merge → `web-deploy.yml`. PR'dan önce yerel Next.js önizlemesinde `/indir` ve `/en/download` gövdesinde sürüm dizgisi görülür. Deploy sonrası: `curl -sL --compressed https://orderdeckapp.com/indir | grep -o "$WH" | head -1` (`-L` şart: `/indir` 308 verir).
- **`.env` `AppRelease__LatestVersion` (web deploy bittikten SONRA; v0.4.4 öncesi kurulumların bildirimi).** VPS'te, Burak'ın SSH'ıyla. `.env` CRLF'tir; `sed` yalnız sürüm karakterlerini değiştirir, satır sonundaki `\r`'ye dokunmaz:

```bash
cd /opt/orderdeck && cp -p .env .env.bak-0.9.12
sed -i "s/^\(AppRelease__LatestVersion=\)[0-9A-Za-z.]*/\1$WH/" .env
docker compose up -d --force-recreate license-server
docker exec orderdeck-license printenv AppRelease__LatestVersion | od -c | head -2
curl -s https://license.orderdeckapp.com/api/app/version
curl -s -o /dev/null -w "%{http_code}\n" https://license.orderdeckapp.com/ready
```

  Beklenen: `od -c` çıktısında sürümden sonra `\n` var, `\r` YOK; `/api/app/version` Wh'yi döner; `/ready` `200`. Recreate'ten sonraki ilk saniyelerde Caddy 502'si normaldir; `docker logs orderdeck-license --since 2m 2>&1 | grep "Application started"` görülünce yinelenir. Yalnız `up -d` `.env` değişikliğini fark etmez; `--force-recreate` şart. Bu adım 5. maddeyle aynı turda yapılabilir.

**3. Wh satır kipinde — ilk PC güncellendikten sonra (bugünkü bütün lisanslar; spec §6.1 giriş, §8.5 "S2 sonrası").** Beklenen: bugünkü davranış + spec'in saydığı üç fark (sahip kuralı a, Ayır onayı e, kart düzeltmesi f) + planın bilerek kabul ettiği üç fark (aşağıda). Yeni davranışların geri kalanı yalnız havuz kipinin sinyaliyle (`customer-not-synced`, `personPool: true`) çalışır.
- **Ödeme isteği,** bakiyeli ve bakiyesiz birer müşteride bugünkü gibi: önizleme o satırın bakiyesi, mesaj gider, `{bakiye}` alanı aynı anlamda. `memberIds` satır kipindeki sunucuca okunmaz. **"Henüz eşitlenmedi" satır kipinde HİÇ çıkmamalı**: yalnız havuz kipinin yanıtıdır.
- **(a) Sahip kuralı:** GUID'li ama kullanıcı adı boş (ya da sınırı aşan) bir satırı ve gönderilebilir bir satırı olan kişide yeni işin önizlemesi gönderilebilir satırın bakiyesini gösterir (bugün 0 görünüyordu). Sahada böyle bir kişi yoksa madde atlanır (kapsamı Görev 1 testleri).
- **(e) Ayır onayı:** iki hesabında bakiye olan birleştirilmiş bir kişide "Grubu ayır" penceresi hesap başına dağılımı gösterir (`Instagram @…: N TL · …`; silinmiş üyede `(silinmiş hesap): N TL — ayırınca kullanılamaz`), düğmeler **[Ayır] [Vazgeç]**. Önce **Vazgeç** → grup değişmez. Gerçekten ayırmak gerekmiyorsa her denemede Vazgeç'le çıkılır. Bütün tutarları 0 olan kişide soru çıkmaz; hiç gönderilebilir GUID'li üyesi olmayan kişide soru çıkmaz, sunucuya da gidilmez. Ağ kesikken ya da sahip satırı sunucuya henüz ulaşmamışken (panel GET 404) "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır." çıkar. Ağ kesikken soru, istemcinin yeniden deneme politikası (`AppHost.cs:451`, `AddStandardResilienceHandler`: 3 yeniden deneme, toplam 30 sn) yüzünden ~30 sn'ye kadar gecikir ve düğme o sürede pasiftir; bugün Ayır anlıktır (beklenen, askıda kalma sanılmamalı; daha kısa süre istenirse Sahip sorusu 5).
- **(f) Kart düzeltmesi:** GUID'siz (eski, yerel Id'li) bir satırdan açılan müşteri kartı kişinin sahip satırının bakiyesini gösterir; panel kartında aynı tutar görünür. "Bakiye Ekle" aynı satıra yazar. Hiç gönderilebilir GUID'li üyesi olmayan kişide "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor." çıkar (değişmeyen metin).
- **Bilerek kabul edilen üç fark (iskelet fark 12, 26 ve 27 = Görev 1-b):**
  - Yerel planın birleştirme sorusunun notu nötr metinle biter: "… iade o hesaba döner." ("bu istekte kullanılmaz" düşer). Sunucu izlerinden gelen soruda satır kipinde not bugünkü gibidir. Sahada böyle bir iş yoksa gözlenmez (kapsamı Görev 6 testleri).
  - Mod On, ağ kesik: yalnız gönderilemeyen GUID'li üyeleri olan kişide mesaj yerel yoldan gider (bugün engelleniyordu); bağlantı gelince kayıt sunucuya yazılır.
  - Hiç sahip adayı olmayan kişide (bütün GUID'li üyeleri gönderilemeyen) aynı kapsamda ikinci tıklama "tekrar gönderilsin mi?" sorulmadan gider, teslim kaydı yazılmaz (GUID'siz kişinin bugünkü yolu). 1a'nın `adaysiz` sayısı bu kişilerin büyüklüğüdür.
- **Sunucu (VPS):** satır kipinde hiçbir grup çekimi yazılmamış olmalı.

```bash
odsql <<'SQL'
SET NOCOUNT ON;
SELECT (SELECT COUNT(*) FROM Licenses WHERE PersonBalancePoolSince IS NOT NULL) AS HavuzLisans,
       (SELECT COUNT(*) FROM CustomerBalanceTransactions
         WHERE DrawGroupId IS NOT NULL AND CreatedAt >= '2026-10-31T11:36:00+00:00') AS S2denBeriGrupluSatir;
SQL
docker exec orderdeck-caddy wget -qO- http://license-server:8080/metrics | grep -E '^orderdeck_balance_(customer_not_synced|multi_leg_draws)_total'
```

  Beklenen: `0|0`; `grep` boş (kip kapalıyken bu iki sayaç hiç ölçülmez, OpenTelemetry yayımlamaz).
- **WPF günlüğü (PC'de, PowerShell):**

```powershell
Select-String -Path "$env:USERPROFILE\Documents\OrderDeck\Logs\log-*.txt" -Pattern 'customer-not-synced' | Select-Object -Last 20
Select-String -Path "$env:USERPROFILE\Documents\OrderDeck\Logs\log-*.txt" -Pattern 'Bakiye önizlemesi alınamadı' | Measure-Object
```

  Birincisi satır kipinde boş dönmeli (bu ifade yalnız havuz kipinin 409'unda yazılır). İkincisi (ağ/belirsiz kolun var olan satırı) Wh öncesindeki sıklıkta kalmalı.

**4. PC'ler Wh+ ve sürümün sunucuda görülmesi (spec §7.1 adım 6'nın ikinci yarısı; `enable` koşul 1).**
- Burak pilot lisansın her etkin PC'sini Wh'ye günceller (yayın PC'leri yayın penceresi dışında; uygulama açılışta güncellemeyi sorar, onaylanınca yeniden başlar). PC açılıp sunucuya bağlandığında `Activations.AppVersion` UA'dan güncellenir.
- Sunucuda görülen sürümler (VPS):

```bash
odsql <<'SQL'
SET NOCOUNT ON;
SELECT LOWER(CONVERT(varchar(36), a.LicenseId)) AS Lisans, LOWER(CONVERT(varchar(36), a.Id)) AS Etkinlestirme,
       ISNULL(a.AppVersion, '-') AS Surum, CONVERT(varchar(16), a.LastSeenAt, 126) AS SonGorulmeUtc
FROM Activations a
WHERE a.DeactivatedAt IS NULL AND a.LastSeenAt >= DATEADD(day, -14, SYSDATETIMEOFFSET())
ORDER BY a.LicenseId, a.LastSeenAt DESC;
SQL
```

  Beklenen: pilotun bütün satırlarında `Surum` = Wh. Son 14 günde (`PersonBalance:ActivePcWindowDays`, kod varsayılanı 14) görülmüş, devre dışı bırakılmamış eski sürümlü satır kalmamalı. Listede artık kullanılmayan bir PC varsa ya güncellenir ya da 14 gün görünmemesi beklenir; 14 günden eski görülmüş PC kapıya sayılmaz, dönerse havuz kipindeki lisansta `client-update-required` alır (fail-closed, beklenen).
- Kuru `enable` (salt okunur): `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance enable --license "$PILOT"; echo "exit=$?"`. Alt sınır 5. maddeden önce yazılmadıysa `Koşul 1 — istemci sürümü (son 14 gün): TUTMUYOR` ve "karşılaştırılamadı" görünür; 5. maddeden sonra yinelenir.

**5. Alt sınır: `PersonBalance__MinClientVersion=<Wh>` (spec §7.1 adım 6, §7.2; `enable` koşul 4).**
- Değerin PC güncellemesinden önce yazılması da güvenlidir: hiçbir lisans havuz kipinde değildir; değer yalnız `enable`'ın değerlendirmesini ve havuz kipindeki lisansların istemci kapısını etkiler. Değer `SalesSync__MinClientVersion`'dan (`0.9.11`) küçük olamaz, ön sürüm eki taşımaz.
- Compose satırı S2'den beri vardır (`deploy/docker-compose.yml:238`, `"${PersonBalance__MinClientVersion:-}"`); `.env`'de satır YOKTUR. S2 saha notu 5 `docker compose up -d license-server` der; ev kuralı `--force-recreate` + konteyner env doğrulamasıdır (iskelet fark 24). VPS'te:

```bash
cd /opt/orderdeck && cp -p .env ".env.bak-$WH-pre-personbalance"
grep -c '^PersonBalance__MinClientVersion=' .env
tail -c 2 .env | od -c
```

  - `grep -c` `0` ise (beklenen) satır eklenir. `od -c` `\r  \n` gösteriyorsa dosya CRLF'tir: `printf 'PersonBalance__MinClientVersion=%s\r\n' "$WH" >> .env`. Yalnız `\n` gösteriyorsa LF: `printf 'PersonBalance__MinClientVersion=%s\n' "$WH" >> .env`. Son karakter `\n` değilse önce satır sonu eklenir (`printf '\r\n' >> .env` ya da `printf '\n' >> .env`).
  - `1` ise değiştirilir: `sed -i "s/^\(PersonBalance__MinClientVersion=\)[0-9A-Za-z.]*/\1$WH/" .env`.

```bash
docker compose up -d --force-recreate license-server
docker exec orderdeck-license printenv PersonBalance__MinClientVersion | od -c | head -2
docker exec orderdeck-license printenv SalesSync__MinClientVersion
curl -s -o /dev/null -w "%{http_code}\n" https://license.orderdeckapp.com/ready
docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance enable --license "$PILOT"; echo "exit=$?"
```

  Beklenen:
  - `od -c` Wh'yi ve `\n`'yi gösterir, `\r` YOK; `SalesSync__MinClientVersion` = `0.9.11` (değişmedi); `/ready` `200`.
  - Kuru koşu: `Koşul 1 — istemci sürümü (son 14 gün): TUTUYOR`; alt satır `  PersonBalance:MinClientVersion: 0.9.13` sonunda ` (ayrıştırılamıyor)` eki YOK; `alt sınırın altında ya da sürümsüz: 0` (sayı; "karşılaştırılamadı" değil — değerin yazıldığı kadar ayrıştırıldığının da kanıtı). Koşul 1 TUTMUYOR ise eski etkinleştirmeler `<Id> (sürüm=…)` diye listelenir: o PC güncellenir, komut yinelenir.
  - `Koşul 4 — Wh ⊇ Ws: PersonBalance:MinClientVersion=0.9.13 SalesSync:MinClientVersion=0.9.11: TUTUYOR`.
  - Koşul 2 ve 3 TUTUYOR (29.10'da dört lisansta `--mark-reviewed --apply` yapıldı). Son satırlar `Sonuç: bütün koşullar tutuyor` ve `Kuru çalıştırma: hiçbir şey yazılmadı. Açmak için --apply ekle.`, `exit=0`.
  - Açılış günlüğündeki `PersonBalance:MinClientVersion tanımsız` uyarısının yokluğu burada kanıt değildir: uyarı yalnız havuz kipinde lisans varken yazılır (`PersonBalanceStartup`).
- **Boşaltmak kill switch DEĞİLDİR:** havuz kipindeki lisansta yalnız WPF'in preview'ını ve yeni apply'ını 409 `client-update-required`'a düşürür. Kill switch `person-balance disable` (8. ve 9. madde).

**6. Adım 7a — karışık filo denemesi (spec §8.5, §7.5; test lisansında, havuz kipi AÇIKKEN; S2 saha notu 6 ile aynı sıra).**
- **Test lisansı.** Pilot dışındaki bir lisans; tabanı alınmış ve incelenmiş olmalı (koşul 2 ve 3). Test bilgisayarı (ayrı bir bilgisayar ya da ayrı bir Windows kullanıcısı: Velopack kullanıcı başına kurar, veri o kullanıcının `Documents\OrderDeck`'indedir) Wh ile kurulur ve bu lisansla etkinleştirilir. Lisansı, son 24 saatte görülen tek pilot dışı etkinleştirmeden alın:

```bash
[ -n "$PILOT" ] || echo "DUR: önce 0. maddedeki PILOT satırı"
TEST_LIC=$(odsql_id <<SQL
SET NOCOUNT ON;
SELECT DISTINCT LOWER(CONVERT(varchar(36), a.LicenseId))
FROM Activations a
WHERE a.DeactivatedAt IS NULL AND a.LastSeenAt >= DATEADD(hour, -24, SYSDATETIMEOFFSET())
  AND LOWER(CONVERT(varchar(36), a.LicenseId)) <> '$PILOT';
SQL
)
[ "$(printf '%s\n' "$TEST_LIC" | grep -c .)" = 1 ] && echo "TEST_LIC=$TEST_LIC" || echo "DUR: test lisansı tek bir Id değil"
```

- **Kip açılır (önce), eski sürümler SONRA çalıştırılır:** koşul 1 son 14 günde görülmüş eski bir PC varken reddeder. Lisansın satış senkronu modu 0. maddedeki tablodan not edilir.

```bash
docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance enable --license "$TEST_LIC"; echo "exit=$?"
```

  Dört koşul TUTUYOR ise (koşul 3 `YOK — önce verify --capture-baseline` derse önce spec §7.1 adım 2–4) Burak'ın onayıyla aynı komut `--apply` ile → `UYGULANDI: havuz kipi açık, PersonBalancePoolSince=…`, `exit=0`. Doğrulama: 0. maddedeki lisans tablosu, test lisansında `HavuzSince` dolu.
- **Eski sürümü nerede çalıştır:** yalnız test bilgisayarında ya da ayrı Windows kullanıcısında, o sürümün GitHub sürümündeki `OrderDeck-win-Setup.exe`'siyle, test lisansıyla; güncelleme sorusu "Sonra" ile geçilir. **Üretim PC'si ≤0.9.10'a asla indirilmez** (şema 49 → eski ikili satış senkronu durumundan çıkamaz; `deploy/README.md` "Masaüstü: satış senkronunu yerelde sıfırlama").
- **v0.9.9, bakiyeli müşteriye Ödeme iste.** Beklenen: mesaj "Bakiye doğrulanamadı" uyarısıyla engellenir, iş `NoBalance` olmaz, apply belirsiz kalır. Metrik: `docker exec orderdeck-caddy wget -qO- http://license-server:8080/metrics | grep orderdeck_balance_client_update_required_total` → satış senkronu On ise `gate="sales-sync"` (v0.9.9 < Ws), Off ise `gate="person-balance"`.
- **Ws ≤ sürüm < Wh (`0.9.11` ya da `0.9.12`; ikisi de #506 taşır).** Aynı denemede `gate="person-balance"`, `endpoint="preview"` artar, mesaj gitmez. Bu PC'de birleştirme sorusu çıkarsa (kişinin başka hesabından düşülmüş eski bir iş): soru eski, kipsiz notla ("… bu istekte kullanılmaz") görünür — beklenen (§7.5). "Evet" sonrası geri alma her bacağı kendi satırına döndürür, ardından önizleme 409 alır ve mesaj gitmez. Böyle bir iş yoksa madde atlanır, nedeni not edilir. Geri alma sorgusu (VPS; bu planda bundan sonraki bütün atıflar bu sorguyu kullanır):

```bash
odsql <<'SQL'
SET NOCOUNT ON;
SELECT LOWER(CONVERT(varchar(36), r.LicenseId)) AS Lisans,
       LOWER(CONVERT(varchar(36), COALESCE(o.DrawGroupId, o.Id))) AS Capa,
       LOWER(CONVERT(varchar(36), r.WpfCustomerId)) AS Satir, r.Amount AS Tutar,
       LOWER(CONVERT(varchar(36), r.DrawGroupId)) AS ReversalGrup,
       CASE WHEN r.WpfCustomerId = o.WpfCustomerId THEN 1 ELSE 0 END AS KendiSatirinda
FROM CustomerBalanceTransactions r
JOIN CustomerBalanceTransactions o ON o.Id = r.ReversesTransactionId
JOIN Licenses l ON l.Id = r.LicenseId
WHERE r.Kind = 'reversal' AND l.PersonBalancePoolSince IS NOT NULL AND r.CreatedAt >= l.PersonBalancePoolSince
ORDER BY r.CreatedAt;
SQL
```

  Beklenen: her satırda `KendiSatirinda` = `1`. Havuz kipi açılmadan (satır kipinde) yazılmış eski tek bacaklı düşümün geri alması tek satırdır ve `ReversalGrup` `NULL`'dır (M13; `Capa` = düşümün kendi Id'si). Havuz kipinde yazılmış çok bacaklı düşümde her bacağa bir satır vardır, hepsi aynı `Capa`'yı taşır.
- **Wh PC, iki üyesinde bakiye olan kişi.** Önizleme havuzu gösterir: ekrandaki önizleme tutarı panel kartındaki kişi toplamına eşittir (`max(0, Σ üyeler)`). Yanıttaki `personPool: true` yalnız sözleşme notudur; WPF onu okumaz, sahada doğrudan görülmez (iskelet fark 22). Mesaj gönderilir; apply çok bacaklı yazar:

```bash
docker exec orderdeck-caddy wget -qO- http://license-server:8080/metrics | grep orderdeck_balance_multi_leg_draws_total
odsql <<'SQL'
SET NOCOUNT ON;
SELECT LOWER(CONVERT(varchar(36), t.LicenseId)) AS Lisans, LOWER(CONVERT(varchar(36), t.DrawGroupId)) AS Grup,
       t.Kind AS Tur, COUNT(*) AS Satir, SUM(t.Amount) AS Toplam
FROM CustomerBalanceTransactions t
JOIN Licenses l ON l.Id = t.LicenseId
WHERE l.PersonBalancePoolSince IS NOT NULL AND t.DrawGroupId IS NOT NULL AND t.CreatedAt >= l.PersonBalancePoolSince
GROUP BY t.LicenseId, t.DrawGroupId, t.Kind
ORDER BY MIN(t.CreatedAt);
SQL
```

  Beklenen: `legs="…"` etiketli sayaç satırı artar; tabloda `purchase-deduction` satırında `Satir > 1`. Revizyon (fiyat düzeltmesi ya da iptal) her bacağı kendi satırına döndürür. Tabloda `reversal` satırları düşümün `Grup`'unda DEĞİL, kendi `Grup`'larında görünür (geri alma çapasının Id'si; `PersonBalance.StageGroupReversalAsync`): o grubun `Satir` sayısı düşümünkine eşittir, `Toplam`'ı düşüm toplamının tersidir. İki grup arasındaki bağ geri alma sorgusundadır: o satırların hepsi düşümün `Grup`'unu `Capa` olarak taşır ve her satırda `KendiSatirinda = 1` olur. Birleştirme sorusu çıkarsa: sunucu izlerinden gelen soruda not YOK; yerel planın sorusunda nötr not ("… iade o hesaba döner."), "bu istekte kullanılmaz" YOK (Görev 6).
- Bitince gerekiyorsa test lisansı kapatılır: `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance disable --license "$TEST_LIC"` (kuru), sonra aynı komut `--apply`.

**7. `customer-not-synced` denemesi (Adım 7a'nın parçası; test lisansı havuz kipinde, Wh PC).**
- Bakiyeli bir kişiyi **yeni bir satırla Birleştir**, hemen ardından **Ödeme iste**. Beklenen: "Henüz eşitlenmedi" başlıklı uyarı, metin "Kişinin hesapları sunucuyla henüz eşitlenmedi; mesaj gönderilmedi. Birazdan tekrar deneyin — bakiye kaybolmaz, çift düşüm olmaz."; mesaj GİTMEZ; iş `NoBalance` olmaz.
- Uyarı arka planda bir müşteri senkron turu başlatır (`CustomerChangesPullService.PullOnceAsync`: önce bu PC'nin birleştirmesini gönderir, sonra çeker). Birkaç saniye sonra yeniden **Ödeme iste** → havuz bakiyesiyle mesaj. Değişiklik başka bir PC'deyse önce o PC'nin gönderim döngüsüyle (≤ 60 sn) sunucuya ulaşır; bu PC'de onu bir sonraki 409'un tetiklediği turun çekmesi ya da 30 sn'lik çekme döngüsü indirir. Spec §8.5'in "≤ 60 sn sonra havuz"u budur.
- Sunucu (VPS):

```bash
docker exec orderdeck-caddy wget -qO- http://license-server:8080/metrics | grep orderdeck_balance_customer_not_synced_total
docker logs orderdeck-license --since 30m 2>&1 | grep '409 customer-not-synced'
```

  Beklenen: `endpoint="preview"` (ya da `"apply"`) etiketli sayaç artar; günlük satırı `customer-balance/preview: 409 customer-not-synced (license=…, rows=…)` satır Id'lerini ("N") basar, ad ve telefon yok.
- WPF günlüğü (test bilgisayarında, PowerShell): `Select-String -Path "$env:USERPROFILE\Documents\OrderDeck\Logs\log-*.txt" -Pattern 'customer-not-synced' | Select-Object -Last 20` → `Bakiye önizlemesi: kişinin bir üyesi sunucuya henüz ulaşmadı (customer-not-synced) — mesaj engellendi, iş taze kaldı (job=…)`; yalnız iş/anahtar/satır Id'leri, ad, kullanıcı adı, telefon YOK.
- **5 dakika metni.** Sahada bilerek üretilmez: tetiklenen tur ve döngüler normal eşitlenmemeyi bir dakika içinde kapatır, kalıcı durumu güvenle üretmenin yolu (bozuk satır, yakınsamayan eski grup) üretim verisini bozmaktır. Kapsamı Görev 4'ün testleridir (`Bes_dakika_dolmadan_kisa_metin_dolunca_uzun_metin_ve_gunluge_satir_idleri`, `Esitlenmeme_sayaci_kisi_basina`, `Kesin_sonuctan_sonra_sayac_sifirlanir`, `Uzun_aradan_sonraki_ilk_esitlenmeme_kisa_metinle_baslar`, `Ag_belirsizligi_sayaci_sifirlamaz`). Kendiliğinden olursa ekranda "Bu kişinin hesaplarından biri sunucuya ulaşmıyor; mesaj gönderilmedi. Müşteri kaydını kontrol edin ya da grubu ayırıp yeniden birleştirin." görünür ve WPF günlüğüne `customer-not-synced 5 dakikadan uzun sürüyor (…) — kişinin satırları: …` (Warning) düşer; satır Id'leri sunucu günlüğündeki `rows=` ile karşılaştırılır. Kurtarma operatördedir: satırı düzeltmek ya da Ayır + yeniden Birleştir.

**8. Adım 7b — pilot `enable` ve Adım 8 — saha kontrolleri (spec §7.1 adım 7–8, §8.5 "Pilot lisansta `enable` sonrası"; iki üyesinde bakiyesi olan bilinen bir kişi).**
- 4–7 tamam. Pilotta önce kuru koşu (dört koşul TUTUYOR; 5. maddedeki komut), sonra Burak'ın onayıyla, yayın penceresi dışında: `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance enable --license "$PILOT" --apply; echo "exit=$?"` → `UYGULANDI: havuz kipi açık, PersonBalancePoolSince=…`, `exit=0`. 0. maddedeki lisans tablosunda pilotun `HavuzSince`'i dolu.
- **Önizleme havuzu gösterir:** ekrandaki önizleme tutarı panel kartındaki kişi toplamına eşittir (`max(0, Σ üyeler)`). DB'de aynı `Grup`'lu bacaklar (6. maddedeki çok bacak sorgusu; satırların `Lisans`'ı pilot); revizyon her bacağı kendi satırına döndürür (6. maddedeki geri alma sorgusu).
- **Ayır onayı** havuz kipinde de üye başına satır bakiyesini doğru gösterir; **Vazgeç** ile çıkılır.
- **Kartlar aynı havuzu gösterir:** aramadan, sohbetten ve kara listeden açılan kartlar aynı kişi toplamını gösterir (panel GET havuz kipinde bütün üyeler için `Σ` döner); GUID'siz satırdan açılan kart da aynı tutarı gösterir.
- **"Henüz eşitlenmedi":** yeni bir satırla Birleştir → hemen Ödeme iste → uyarı, ≤ 60 sn sonra havuz (7. madde). 5 dakika metni yalnız gözlenir (7. madde).
- Panel kartı ve Shopper kişi toplamını gösterir (S2'nin işi; bu PR değiştirmedi).
- **İzleme (ilk günler):** `orderdeck_balance_client_update_required_total` (`gate="person-balance"` artıyorsa güncellenmemiş bir PC var — kuru `enable` artık "Havuz kipi zaten açık" der, 4. maddedeki sürüm sorgusuna bakılır) ve `orderdeck_balance_customer_not_synced_total` (sürekli artıyorsa 7. maddenin 5 dakika satırlarına bakılır); WPF günlüklerinde `customer-not-synced` sayısı. `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance verify --license "$PILOT"` → D4, D5, D6, yetim bacak 0; ertesi gün `person-balance-verify` uyarısız.
- **Sorun → kill switch:** `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance disable --license "$PILOT"` (kuru), sonra aynı komut `--apply`. Veri işlemi gerekmez; Wh satır kipinde doğru çalışır (3. madde).
- **Öbür lisanslar tek tek (spec §7.1 adım 9):** her biri için 4 (PC'ler Wh+), kuru → `--apply` `enable`, bu maddenin kontrolleri. O lisansın Id'si 0. maddedeki tablodan okunup bir kabuk değişkenine konur; tam komutu o anda Claude verir (lisans Id'leri repoya yazılmaz).

**9. Geri dönüş.**
- **Havuz kipinde sorun:** önce kill switch (`person-balance disable`, 8. madde). Kip kapanınca bütün PC'ler (Wh ve eskiler) satır kipinde bugünkü gibi çalışır; çok bacaklı geçmiş okunmaya ve grup olarak geri alınmaya devam eder (S2 saha notu 10).
- **Wh yerel göç içermez** (şema 49 kalır; Görev 9 Adım 1'in boş göç çıktıları). Bir PC'nin v0.9.12'ye (ya da v0.9.11'e) dönmesi yerel veride güvenlidir; ≤0.9.10'a dönüş bu sürümden bağımsız olarak istisnai destek eylemidir (`deploy/README.md` "Masaüstü: satış senkronunu yerelde sıfırlama").
- **PC'yi eski sürüme döndürmek:** yalnız Wh satır kipinde de hatalıysa. Önce o PC'nin lisansı havuz kipindeyse `disable` (yoksa eski PC preview'da 409 `client-update-required` alır, mesaj "Bakiye doğrulanamadı" ile engellenir; spec §6.2, §7.4). Sonra o PC'ye v0.9.12'nin GitHub sürümündeki `OrderDeck-win-Setup.exe` kurulur; yöntem önce test bilgisayarında denenir. Feed Wh'yi sunduğu sürece uygulama güncelleme sorar ("Sonra"). Bütün filo için tercih edilen yol ileri düzeltmedir: revert PR → yeni Velopack sürümü (Wh'nin bir üstü), `PersonBalance__MinClientVersion` da ona göre.
- `PersonBalance__MinClientVersion`'ı boşaltmak geri dönüş değildir (5. madde). Sunucu tarafı değişmediği için W-havuz'un sunucu geri dönüşü yoktur; S2'nin geri dönüş kuralları (S2 saha notu 10) aynen geçerlidir.
- Spec §6.2'nin satır atıfları v0.9.9 kodudur; v0.9.10+ ve Wh'de arama kartı ile yayın raporu aynı kişi × kapsam işini aynı toplamla açar (`PaymentPersonViewModelTests.cs:108-130`), çift düşüm riski yalnız v0.9.9'dadır. Havuz kipinde v0.9.9'un engellenmesinin nedeni budur (iskelet fark 20; WPF kodu yok).

**10. Bellek (Burak/oturum; plan dosya yazmaz).** `project_surum_yayin_borcu.md`'nin en üstüne v<Wh> bloğu: tag sha, içerik (W-havuz: `memberIds`, `BalanceNotSynced` + müşteri senkron turu + 5 dakika metni, sahip kuralı, üç durumlu not, Ayır onayı, kart sahip yedeği), feed boyutları (Full/Delta), `.env` yedeklerinin adları, iki alt sınır (`SalesSync__MinClientVersion=0.9.11`, `PersonBalance__MinClientVersion=<Wh>`), `AppRelease__LatestVersion=<Wh>`, PC güncelleme durumu. Test lisansı ve pilot `enable` yapıldıysa Id'leri ve tarihleri `project_odeme_kisi_anahtari.md`'nin durum satırına.

---

## Öz-inceleme (kapanış; plan 03.11.2026'da yeniden tarandı)

**Spec kapsamı.** R01–R41 spec'ten (§2, §6.1 a–h, §7, §8.4, §8.5, §10) çıkarılan gereksinim listesidir; her test adı ilgili görevde Grep'le bulundu.

| R | Gereksinim (spec) | Görev — test / kanıt |
|---|---|---|
| R01 | Wh tanımı, Wh ≥ 0.9.13, Wh ⊇ Ws, numara koda gömülmez (§2, §7.1) | Kod yok; Görev 9 Adım 1 (UA satırı değişmedi); saha notu 2, 4, 5 |
| R02 | UA deseni değişmez, ön sürüm eki kapıyı geçmez (§7.2) | Görev 9 Adım 1 (`git diff … AppHost.cs \| grep UserAgent` boş); saha notu 2 |
| R03 | `ActivePcWindowDays` 14, `enable` koşul 1 ve 4 (§7.2) | Saha notu 4, 5 |
| R04 | Satır kipinde yalnız a/e/f değişir; yeni davranış sunucu sinyaline bağlı (§6.1 giriş) | Her görevin regresyon adımı (R-Kişi/R-Ödeme/R-ModOn/R-Senkron/R-İstemci/R-Arayüz), Görev 9 Adım 3 tam paket; 6 `Sunucu_izlerinin_sorusu_satir_kipinde_bugunku_not`, `Eski_sunucu_izlerinde_alan_yok_bugunku_not`. Bilinçli ek farklar: fark 12 (6 `Yerel_planin_sorusunda_PersonPool_her_zaman_null`), fark 26 (1 `Begin_cevrimdisi_GUIDli_ama_gonderilemeyen_ve_yalniz_kendi_etiketleri_olan_kiside_yerel_yol_ve_kayit`), Görev 1-b (1 `Gonderilemeyen_tek_uyeli_kiside_eski_teslim_okunmaz_ikinci_tiklama_sorusuz_gider`); saha notu 3 |
| R05 | Preview `memberIds` tekrarlı sorgu parametresi (§6.1-a, C02) | 2 `Onizleme_memberIds_tekrarli_sorgu_parametresi_olarak_gider`, `Onizleme_bos_uye_listesinde_memberIds_parametresi_yok`; 3 `Onizleme_kisinin_guidli_butun_uyelerini_gonderir_silinmis_dahil_guidsiz_haric` |
| R06 | Apply gövdesi `memberIds` (C03) | 2 `Apply_govdesi_memberIds_dizisini_tasir`, `Apply_memberIds_verilmezse_alan_null_gider`; 3 `Apply_govdesi_kisinin_guidli_uyelerini_tasir_sahip_dahil` |
| R07 | Kaynak `PaymentPerson.Members` (akışın kişisi), GUID süzgeci, silinmiş ve sahip dahil (§2, K3) | 3 `Onizleme_kisinin_guidli_butun_uyelerini_gonderir_silinmis_dahil_guidsiz_haric`, `Tek_satirlik_kiside_memberIds_yalniz_cikis_satiri`; fark 2 (`IsOwnerCapable(string)` GUID kalır) |
| R08 | `ReplayAsync` üyeleri tıklamanın kişisinden alır; spec l.409'un "çağrı anında"sı tıklama düzeyinde: kişi her tıklamanın girişinde okunur, sonradan birleşen üye sonraki tıklamada gider (bilinçli sapma, fark 4; spec satırı Görev 9 Adım 7.8); depo null → tek satırlık yedek kişi, liste yalnız çapa (FARK-a2) | 3 `Replay_uyeleri_tiklama_girisinde_okur_sonradan_birlesen_uye_sonraki_tiklamada_dahil`, `Miras_isin_oynatmasi_da_memberIds_tasir`, `Depo_yoksa_tek_satirlik_yedek_kisi_memberIds_yalniz_capa` |
| R09 | Preview ve apply akışın kişisini kullanır, tıklama içinde depo okunmaz — akış ortasında çapa başka kişiye geçse de liste aynı (Görev 3 Not "Akış ortası testi"; fark 4, §15-5) | 3 `MemberIdsOf(anchor, person)` + dört özel yöntemin `person` parametresi (fark 4), `Onizleme_kisinin_…`, `Akis_ortasinda_capa_baska_kisiye_gecse_de_memberIds_akisin_kisisidir` |
| R10 | Eski sunucu uyumu, yalnız geçerli Guid | 3 (GUID'siz Id hiç gitmez, aynı test); sunucu tarafı S2 testleri (fark 23) |
| R11 | Sahip kuralı `IsServerAcceptable`, Core'a taşıma, Platform/Username (§6.1-a, M14, §8.4-3) | 1 `Platform_ve_kullanici_adi_sinirlari`, `Sabitler_sunucu_sinirlariyla_ayni`, `Uyeler_platform_ve_kullanici_adini_tasir`, `Tek_satirlik_kisi_platform_ve_kullanici_adini_tasir`, `Kullanici_adi_bos_guidli_satir_sahip_secilmez_gonderilebilir_uye_secilir`, `Hicbir_uye_gonderilemezse_sahip_yok`, `Var_olan_isin_sahibi_kural_degisse_de_degismez`, `Kisi_anahtari_ve_sahip_gonderilebilir_uyelerle_sirasindan_bagimsizdir` + beş vektör; R-Senkron (`WpfCustomerProjectionSyncServiceTests` değişmeden) |
| R12 | `PaymentOwnerRule` belge yorumu | 1 Adım 12 (yalnız yorum) |
| R13 | Preview 409 `customer-not-synced` (§6.1-b, K2) | 4 `Onizleme_customer_not_synced_BalanceNotSynced_is_taze_mesaj_gitmez` |
| R14 | Apply/replay taşıma | 4 `Apply_customer_not_synced_is_anahtarli_belirsiz_kalir_sonraki_tiklama_ayni_anahtarla_oynatir`, `Miras_isin_oynatmasi_customer_not_synced_BalanceNotSynced`, `Mod_On_esitlenmeme_kira_abandoned_is_taze_sonraki_tiklama_surer` |
| R15 | `PullOnceAsync` fire-and-forget, bekleyen tetik (§6.1-b, §14 #18) | 5 `Esitlenmeme_musteri_turunu_tetikler_akis_turu_beklemez`, `Bekleyen_tur_varken_ikinci_esitlenmeme_yeni_tur_eklemez`, `Apply_customer_not_synced_de_turu_tetikler`, `Tur_hatasi_yutulur_gunluge_yazilir_sayac_bosa_doner`, `Esitlenmeme_disinda_tur_tetiklenmez`, `Tur_yoksa_akis_degismez`, DI `Odeme_servisi_musteri_turunu_DIdan_alinan_akis_servisine_baglar` |
| R16 | `BalanceNotSynced` + metin; tüketiciler (FARK-b2) | 4 `Esitlenmedi_uyarisi_baslik_ve_metin_birebir`, `Esitlenmedi_sonucu_tek_basina_kisa_metin`, `Resend_esitlenmemis_bakiye_belirsizle_ayni_metinle_elle_birakilir` |
| R17 | 5 dakika kuralı, günlükte satır Id'leri | 4 `Bes_dakika_dolmadan_kisa_metin_dolunca_uzun_metin_ve_gunluge_satir_idleri`, `Esitlenmeme_sayaci_kisi_basina`, `Kesin_sonuctan_sonra_sayac_sifirlanir`, `Uzun_aradan_sonraki_ilk_esitlenmeme_kisa_metinle_baslar`, `Ag_belirsizligi_sayaci_sifirlamaz`, `Esitlenmedi_uzun_metin_sonucun_mesajindan`; saha notu 7 (yalnız gözlem) |
| R18 | Banka W2 eşdeğerliği | İş yok (WPF'te tüketici yok; iskelet "Bilerek dışında") |
| R19 | `client-update-required` genel belirsiz (§6.1-c) | 4 `Onizleme_client_update_required_belirsiz_NoBalance_yazilmaz`, `Apply_client_update_required_belirsiz` |
| R20 | S2 saha notu 5'in eşlemesi | 4 `Onizleme_member_ids_required_belirsiz`, `Apply_member_ids_required_ve_balance_busy_is_anahtarla_bekler`; 2 `Onizleme_ve_apply_havuz_retleri_ValidationException_koduyla`, `Apply_409_no_balance_ValidationException` |
| R21 | K7: preview 200 `Balance = 0`, `PersonPool = true` | 4 `Havuz_onizlemesi_sifir_personPool_true_kesin_NoBalance` |
| R22 | `PaymentPrompt.PersonPool` (§6.1-d) | 6 Adım 3 (`init`, varsayılan `false` = bugünkü metin; fark 10) |
| R23 | Sunucu izlerinin sorusu scope'tan | 2 `Kapsam_yaniti_personPool_okunur_alan_yoksa_false`; 6 `Sunucu_izlerinin_sorusu_scope_personPool_true_ise_not_yok`, `Sunucu_izlerinin_sorusu_satir_kipinde_bugunku_not`, `Eski_sunucu_izlerinde_alan_yok_bugunku_not`, `Karisik_izlerde_PersonPool_null` |
| R24 | Yerel planın sorusu her zaman `null` (iki kurulum yeri, FARK-d2) | 6 `Yerel_planin_sorusunda_PersonPool_her_zaman_null`, `Uzak_soruya_katilan_yerel_planin_notu_kipten_bagimsiz_null` |
| R25 | `WithOtherRowNote` tablosu | 6 `Birlestirme_notu_havuz_kipinde_yok`, `Birlestirme_notu_bilinmiyorsa_notr_metin`, `Birlestirme_notu_satir_kipinde_bugunku_metin`, `Uzak_dusumlerde_birlestirme_notu_kipe_gore`, `Iade_yoksa_kipten_bagimsiz_not_yok` |
| R26 | Belge yorumları | 6 Adım 12 (yalnız yorum) |
| R27 | §7.5 sıra güvenliği | Kod yok; saha notu 6 (#506 taşıyan sürüm) |
| R28 | `UnmergeGroup` async, `Members[]` (FARK-e1) | 2 `Panel_bakiye_yaniti_members_dizisini_okur`, `Panel_bakiye_yaniti_members_yoksa_null`; 7 `Dagilim_kisinin_sahip_satiriyla_sorulur`, DI `Musteri_arama_Ayir_onayi_lisans_istemcisini_DIdan_alir` |
| R29 | Onay metni, [Ayır] [Vazgeç], 12.10 kararı (FARK-e2/e3) | 7 `Bakiye_varsa_dagilim_sorulur_metin_birebir`, `Eksi_bakiye_de_sorulur`, `Silinmis_hesapta_para_varken_ayir_serbest`, `YouTube_uyesinde_gorunen_tanitici_cift_at_yok`, `Vazgec_ile_grup_ayrilmaz`, `Ayir_ile_grup_ayrilir`, `Onay_dugmesi_yazisi_parametreden_gelir_varsayilan_Evet_bildirim_Tamam`, `Odeme_sorusunun_onay_dugmesi_Evet_kalir`; spec bayat satırları Görev 9 Adım 7 |
| R30 | Sorusuz yollar | 7 `Butun_tutarlar_sifirsa_sorusuz_ayrilir`, `Gonderilebilir_uyesi_olmayan_kart_sunucuya_sorulmadan_ayrilir`, `Yalniz_gonderilemeyen_GUIDli_uyeler_sunucuya_sorulmadan_ayrilir`, `Istemci_yoksa_bugunku_gibi_sorusuz`, `Gonderilemeyen_GUIDli_uye_sahip_olmaz_dagilim_gonderilebilir_uyeyle_sorulur` |
| R31 | Çağrı hatasında genel uyarı | 7 `Cagri_hatasinda_genel_uyari` (Ayır ve Vazgeç), `Members_alani_yoksa_genel_uyari` |
| R32 | Bilgi amaçlı; eşlenmeyen canlı üye (SORU-e4) | 7 `Kartta_olmayan_canli_uyede_bakiye_varsa_genel_uyari`, `Kartta_olmayan_canli_uyenin_sifir_bakiyesi_listelenmez` (Sahip sorusu 1, öneri A) |
| R33 | GUID'siz satırdan açılan kart sahip satıra düşer (§6.1-f) | 8 `Guidsiz_satirdan_acilan_kart_kisinin_sahip_satirinin_bakiyesini_okur`, `Guidli_satirdan_acilan_kart_bugunku_gibi_o_satiri_okur`, `Sahip_kurali_gonderilemeyen_satiri_atlar` |
| R34 | "Server eşlemesi yok" metni (SORU-f1) | 8 `Hic_guidli_uyesi_olmayan_kiside_server_eslemesi_yok_metni`, `Guidli_uyeleri_gonderilemeyen_kiside_server_eslemesi_yok_metni`, `Grupsuz_guidsiz_satir_bugunku_gibi_metin`, `Sahip_okumasi_atarsa_bakiye_yuklenemedi_metni` (fark 19) |
| R35 | Kartlar aynı havuz | R33 testleri; saha notu 3, 8 |
| R36 | `PersonJob` gerekçe yorumu (§6.1-g) | 6 Adım 12; `PersonScopePlanTests` değişmeden (R-Kişi) |
| R37 | Değişmeyenler (§6.1-h) | Görev 9 Adım 1 (kapsam taraması); R-Ödeme değişmeden |
| R38 | v0.9.9 / eski PC (§6.2, §7.4) | Kod yok; saha notu 6, 9 |
| R39 | Kesinleşmiş `NoBalance` önbelleği değişmez | Kod yok (iskelet "Bilerek dışında") |
| R40 | §8.4 test listesi 1–6 | 1 → Görev 3; 2 → Görev 4 + 5; 3 → Görev 1; 4 → Görev 6 (`PersonScopePlanTests`/`PaymentJobStoreContractTests` bilerek değişmez, fark 25); 5 → Görev 7; 6 → Görev 8. FARK-t1 → Görev 2 |
| R41 | §8.5 saha kontrollerinin W-havuz maddeleri | Saha notu 3, 6, 7, 8 |

Bilinen test boşlukları (davranış eksiği değil): `AddBalanceAsync`'in `MessageBox` uyarısı testte sürülemez (Görev 8: "Bakiye Ekle aynı Id'ye gider" iddiası `TryGetProjectionId` üzerinden); Ayır'ın çevrimdışı ~30 sn gecikmesi testsiz (saha notu 3); 5 dakika metni sahada üretilmez (saha notu 7; testle kilitli); `client-update-required`'da güncelleme sorusunun açılmadığı ayrıca test edilmez (kod değişmez).

**Yer tutucu taraması.** Plan (iskelet, dokuz görev, bu bölüm) Grep'le tarandı:
- `\bTBD\b|\bTODO\b|\bFIXME\b|\bXXX\b` → isabet yok (bu satırın kendi kalıp listesi dışında).
- `benzer|aynı şekilde|uygun ` → tek isabet Görev 5'in "Ad benzerliği" notu: iki enum adının çakışmadığını anlatır, yer tutucu değil.
- Satır sonunda `…` / `...` → isabet yok. `<…>` → yalnız Görev 6'nın bir açıklama cümlesindeki `IReadOnlyList<…>`, kod değil.
- `// aynen` → Görev 1'deki "Sunucudaki doğrulama aynen kalmalı": korunan kod yorumunun kendi metni, eksiltme değil. İskeletin imza bloğundaki `/* … var olanlar aynen …, */`, `new(…)`, `with { …, }` imza özetidir; tam kod Görev 4 ve Görev 6'dadır. Görev 8'in "aynen kalır" dediği `ReloadBalanceAsync` aralığı Edit'in dokunmadığı bloğu söyler.
- Adım atıfları var olan adımlara gider (G1 1–17, G2 1–8, G3 1–8, G4 1–16, G5 1–9, G6 1–14, G7 1–15, G8 1–9, G9 1–10). Görev 6'daki "Adım 5b", Görev 6 Adım 5'in (b) alt adımıdır.
- Saha notları: komutlarda `<…>` yer tutucusu yok. Lisans Id'leri `PILOT`/`TEST_LIC` değişkenlerine sorgudan alınır (pilotun Id'si VPS'teki `~/.orderdeck-pilot` dosyasından; repo herkese açık olduğu için Id ve öneki yazılmaz), sürüm `WH=0.9.13` satırında, zaman filtreleri `PersonBalancePoolSince` ya da S2 merge anı (`2026-10-31T11:36:00+00:00`) ile. `<Wh>` yalnız başlıklarda ve 10. maddenin bellek metninde, `<Id> (sürüm=…)` yalnız 5. maddede CLI çıktısının biçimini anlatan düzyazıdır, 1a'daki `<pc-adı>` kopyalama klasörünün adıdır; hiçbiri komut değildir. Kabuk komutu yerine çıplak SQL yok: prod sorgularının hepsi `odsql`/`odsql_id` (docker exec + sqlcmd) üzerinden, 1a'nınki geliştirme PC'sinde Python'un `sqlite3` modülüyle (`mode=ro`).

**İmza tutarlılığı.** İskeletin "Görevler arası imzalar" bloğu tanımlayan görevle Grep'le karşılaştırıldı; ad, tür ve parametre sırası aynı (Görev 1: `ServerCustomerRule`, `PaymentPersonMember`, `Single`, `Columns`, `IsOwnerCandidate`, `PickOwner`; Görev 2: dört DTO ve iki `GetBalancePreviewAsync`; Görev 3: statik `MemberIdsOf(Guid, PaymentPerson)`, dört özel yöntemin `licenseId`'nin ardındaki `PaymentPerson person` parametresi (`ReviseAndSettleAsync`, `SettleAsync`, `ReplayOnceMoreIfStaleAsync`, `ReplayAsync`), stub eklemeleri, test yardımcıları (`GrubaAl` dahil); Görev 4: enum üyesi, `BalanceOutcome.NotSynced`, `ApplyAttempt`, 5 dakika alanları, `NotSyncedOutcome`, sunum sabitleri, `PreviewResponse`, `Problem`, `SutOn(…, log)`, `ManualTimeProvider`, `EsitlenmemisSut`; Görev 5: ctor parametresi, `CustomerTour`, `LastCustomerTour`, sayaç ve iki yöntem, `TurSayaci`; Görev 6: `PersonPool`, `PersonPoolOf`, iki yerel kurulum, `ScopeJsonAt(…, bool? personPool = null)`, spec §6.1-d tablosuyla birebir not metinleri; Görev 7: `ConfirmAsync`/`ForConfirm` düğme parametresi, `CustomerIdentityText`, VM ctor, `Unmerge*` sabitleri ve `UnmergeMessage`, `UnmergeGroupAsync`, `BalanceApiStub` panel üyeleri; Görev 8: `internal bool TryGetProjectionId(out Guid id)`, `Kurulum`/`Kur()`/`Satir(…)` — ek `Kur(out BozukFabrika)` yalnız o sınıfın içinde). Tüketiciler tanımla aynı adı kullanır.

**Görev özeti.** İskeletin efor tablosu görevlerin boyuna göre güncellendi: Görev 1 2 → 2,5 sa (17 adım; mod On kira ve fark 27 testleri, beş vektör, iki fikstür), Görev 4 2,5 → 3 sa (iki commit, 16 adım, 18 test yöntemi), Görev 3 1,5 → 2 sa (akışın kişisi dört özel yöntemle aşağı geçer — 15 Edit — ve akış ortası testi; fark 4). Toplam 16 → 17,5 sa (≈ 2,2 iş günü; spec 1,75 gün, +3,5 sa gerekçesi tabloda).

**Birleştirmede çözülenler (03.11.2026).** Kapanış taramasının bıraktığı sekiz madde birleştirmede kapandı:
- Görev 9'un kendi "Sürüm / saha notları" kopyası düşürüldü; tek kaynak bu bölüm (madde numaraları korunur; görevlerdeki "saha notu 3/9" atıfları geçerli).
- Spec `:404`/`:496`: Görev 9 Adım 7.6/7.7 günceller; iskelet fark 12 ve Görev 6 Not 4 buna göre. Fark 26 ve 27 sahip kuralının (a) sonuçlarıdır, o satırlara eklenmez.
- Görev 1-b iskelete fark 27 ve Sahip sorusu 6 olarak, Ayır'ın çevrimdışı gecikmesi Sahip sorusu 5 olarak girdi; amaç satırı ve "Ön koşullar" satır kipi listesi fark 27'yi sayar.
- Görev 9 Adım 10'un PR metni fark 4, 26 ve 27'yi adıyla sayar, sahip sorularını 1–6 olarak anar. Fark 4'ün bayatlattığı spec §6.1-a `:409`'u Görev 9 Adım 7.8 günceller (ayrı soru gerekmez).
- Dosya haritasının 1. satırına `PaymentRequestServiceTests.Person.cs` eklendi.
- 1a ölçümü `sqlite3` aracına değil (yayın PC'lerinde de geliştirme PC'sinde de yok), geliştirme PC'sindeki Python'a dayanır; komut sahte satırlı bir kopyada denendi. Görev 1'in sorgu kopyası 1a'ya atıfla değiştirildi.
- Test lisansının seçimi (saha notu 6) ve 5 dakika metninin yalnız gözlenebilmesi (saha notu 7) saha konusudur; kod etkisi yok.

**Sahip soruları — son hâl (planın başındaki "Sahip soruları"):**
1. Ayır onayında kartta olmayan canlı sunucu üyesi — öneri (A) bakiyesi ≠ 0 ise genel uyarı, 0 ise listeden çıkar; plan uygular (Görev 7).
2. Gönderilemeyen sahipteki taze iş yeni adaya taşınsın mı (Görev 1-a) — öneri (A) taşınmaz, büyüklük saha notu 1a ile ölçülür; plan uygular.
3. 5 dakika kuralında "art arda" aralığı — öneri (A) 30 dk (`NotSyncedStreakGap`); plan uygular (Görev 4).
4. §10 numara izlemesi hakemi `:699` — 12.10 kararından sonra §8.1 uyarısı seçeneği geçerli mi; açık, W-havuz'a kod düşmez.
5. Ayır sorusunun çevrimdışı ~30 sn gecikmesi — öneri (A) kabul; plan uygular (kod yok).
6. Sahip adayı olmayan kişide Resend/Merge koruması (Görev 1-b, fark 27) — öneri (A) spec'teki gibi aday yoksa iş yok; plan uygular, büyüklük saha notu 1a ile ölçülür.
