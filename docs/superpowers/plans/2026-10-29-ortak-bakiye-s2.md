# Ortak Bakiye (Kişi Havuzu) — S2 Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ortak bakiye spec'inin (`docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md`) §12 **S2** satırını sunucuya getirmek: kip bayrağının (`License.PersonBalancePoolSince`) okunması; `ResolveDrawSetAsync` (`memberIds`, kural 1–4: kesin `no-balance`, 3a/3b, silinmiş üyeler `S`'den, `S∖P` eksileri), `PlanDraw`, `DrawAsync` (kişi kilidi, üyeliğin ikinci okuması, her denemede Lookup, çapanın bakiye satırı, §6.6 kapsam kuralının çapa düzeyinde korunması); `PersonBalance:MinClientVersion` kapısı ve compose satırı; havuzlu preview ve apply; panel ve Shopper havuz okumaları; K5 (panelde çok bacaklı elle eksi ve iade iptali, havuz kipi üyeliği, eski `reversal` → `not-reversible`); `person-balance enable|disable` (Wh ⊇ Ws dahil); D3 uyarısı; dört metrik; Testcontainers 1, 2, 6–10, 14, 15. **Deploy anında hiçbir lisans havuz kipinde değildir ve satır kipinin davranışı değişmez**; şema değişmez (göç yok).

**Architecture:** Kip her istekte lisans başına okunur: WPF uçlarında sahiplik sorgusunun ikinci alanı, panel ve Shopper'da `PersonBalance.IsPoolModeAsync`. Bayrak boşken her uç S1'deki koduyla, aynı sırayla çalışır; havuz dalları ayrı yöntemlerdedir ve satır kipinin yoluna dokunmaz. Para yazan bütün havuz yolları (WPF apply, panel K5 elle eksi ve iade iptali; ileride banka PR5) tek servis yöntemi `PersonBalance.DrawAsync`'ten geçer: çekim kümesi `P` ya `ResolveDrawSetAsync` (WPF, `W` = `memberIds`) ya `PersonDrawSetAsync` (panel, `P = S`) ile kurulur, plan saf `PlanDraw`'dan gelir, yazım tek `SaveChanges`'tir. Jeton çakışmasında, bakiye satırı açma yarışında, kilitlenmede (1205, okumalar dahil) ve üyelik değişiminde `ChangeTracker.Clear()` ile baştan kurulur (en çok 3 deneme, sonra 503 `balance-busy`). Uca özgü adımlar (Lookup, kapsam yüklemi) her denemenin başında bir geri çağrıyla koşar; kapsam kuralının uygulama kilidi deneme başına açılan işlemde alınır. Kip açma/kapama yalnız CLI'dadır; kapı koşulları ayrı bir değerlendiricide (`PersonBalanceEnableGate`) S1 denetiminin (`PersonBalanceVerifier`) sayılarını okur.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server prod / InMemory test), System.Diagnostics.Metrics (OpenTelemetry + Prometheus), xunit + FluentAssertions, Testcontainers (SQL Server 2022 Developer).

---

## Spec ↔ kod farkları (master `b6ee847c`, 2026-10-29)

Spec `7f8d1efa` kesitine göre yazıldı; arada satış senkronu S3 (#520), A5b (#493) ve ortak bakiye S1 (#533) aynı dosyaları değiştirdi. Aşağıdaki her madde için önerilen çözüm planda uygulanmıştır; Burak farklı karar verirse ilgili görev değişir. Gerçek sahip kararları en sondaki "Sahip kararları (29.10.2026)"nda.

1. **Satır numaraları kaydı.** `LicensesCustomerBalanceApplyController` (743 satır): preview `:106-128` + kayıt `:96-104` (spec `:74-91`), scope `:170-196` + yüklem `LatestScopeDeductionAsync` `:213-233` (spec `:123-154`), transactions `:263-293` (spec `:180-205`), apply üç parça — `Apply` `:312-367`, `ApplyOncePerScopeAsync` `:395-440`, `WriteDeductionAsync` `:449-541` (spec `:224-362`; jeton döngüsü `:487-521`, PK oynatması `:522-537`), `LookupAsync` `:577-636` (spec `:385-440`), reverse `:658-684` (spec `:450-519`), `BalanceBusy` `:686-694`, `ScopeLockResource` `:705-706`, `OwnedLicenseModeAsync` `:710-717`, `ClientMeetsMinimum` `:722-725`, `ClientUpdateRequired` `:727-735`. `PanelCustomerBalanceController` (412 satır): GET `:73-108` (spec `:63-93`), `manual-adjustment` `:179-208` (spec `:164-193`), reverse `:212-268` + `ReverseDrawGroupAsync` `:277-311` (spec `:197-246`), `ApplyTransactionAsync` `:339-404` (spec `:274-339`). `ShopperBalanceController` GET `:46-89` (spec `:38-76`). `Program.cs`: CLI `person-balance` `:74-79` (spec `:44-55`), `SalesSyncOptions` kaydı `:184-187`, `UseSqlServer` `:147` (`EnableRetryOnFailure` yok; spec `:115`), `Migrate()` `:967` (spec `:868`), açılış uyarısı emsali `:984-990`. `deploy/docker-compose.yml` SalesSync bloğu `:213-227` (spec `:163-196`). `LicenseDbContext`: bakiye tekil indeksi `:1093`, bakiye jetonu `:1098`, N01 `:1119-1121`. Plan bugünkü numaraları kullanır.
2. **§4.2–§4.5 ve §4.10'un S1 kısmı bitmiş.** Lookup/scope/transactions `−Σ(grup)`, bacak anahtar → foreign 404 (`:585`), scope'un çapa süzgeci (`:221`), WPF reverse'in grup geri alması (`PersonBalance.ReverseGroupAsync`), Shopper'ın asıl kayda çözmesi ve `Reason` maskesi (`ShopperBalanceController.cs:70`, `:84-85`; S1 planı fark 10) master'da. §12'nin S2 satırındaki "Shopper … `Reason` maskesi" artık yalnız havuz kipinin `S`-geneli okuması demek. **Çözüm:** S2 bunlara dokunmaz; maske ifadesi aynen kalır, havuz dalı `HistoryAsync`'e `S`'nin Id'lerini verir. `transactions/{id}` ve WPF reverse değişmez; reverse'e havuz sürüm kapısı konmaz (§7.5, §14-19).
3. **Sürüm denetimi ile `member-ids-required`'ın sırası (spec iç çelişkisi).** §4.0 "havuz kipinde `memberIds` zorunlu, yoksa 400" diye başlar, sürümü sonra anlatır; ama §6.2 ve §8.5 `memberIds` göndermeyen v0.9.9'un **409 `client-update-required`** almasını bekler (saha denemesi buna göre). **Çözüm:** havuz kipinde sıra (satış senkronu kapısı) → (apply: yeniden oynatma) → havuz sürüm kapısı → `memberIds` → `P`. Eski sürüm `memberIds` göndermese de 409 alır.
4. **İki sürüm kapısı, iki yer (§15-6 ↔ §4.2 "kayıtlı anahtar `client-update-required` almaz").** Satış senkronu kapısı (mod On, `SalesSync:MinClientVersion`) bugün apply'da yeniden oynatmadan ÖNCE (`:343-345`, yorum "§6.5-3: bakiyenin her okumasından (oynatma dahil) önce"). **Çözüm:** o kapı iki kipte yerinde kalır; havuz kapısı (`PersonBalance:MinClientVersion`) oynatmadan SONRA. §4.2'nin cümlesi yalnız havuz kapısı için geçerlidir. scope, transactions ve reverse'e havuz kapısı yok.
5. **`MembersAsync` "a sunucuda yok"u ayırt etmez** (`PersonBalance.cs:57-59`: projeksiyon yoksa `S = {a}`). Kural 1 ve 2 bu ayrımı ister. **Çözüm:** `ResolveDrawSetAsync`, `W ∪ {a}`'nın projeksiyon varlığını ve silinmişliğini tek sorguyla okur; kopya Id'leri önce `CustomerIdResolver.CanonicalOfAsync`'ten geçtiği için sorgu süzgeci (`MergedIntoId == null`) yeterli. `MembersAsync`, `Member`, `Person` değişmez.
6. **`PersonBalance:*` ayarları yok.** **Çözüm:** yeni `PersonBalanceOptions` (`SalesSyncOptions` kalıbı: `Section = "PersonBalance"`, `MinClientVersion`, `ActivePcWindowDays = 14`, `ParseMinClientVersion()`), `Validate(ActivePcWindowDays > 0)` + `ValidateOnStart`. Compose'a spec'in lafzıyla yalnız `PersonBalance__MinClientVersion` satırı eklenir; `ActivePcWindowDays` compose'da yok, prod'da kod varsayılanı 14 (satış senkronu kapısıyla aynı ölçüt). `verify`'ın 9. maddesi S1'deki sabit pencerede kalır: §7.3-9 pencere tanımlamaz, ayar §7.2-1'de yalnız `enable`'ın 1. koşulu için; `PersonBalanceVerifier.ActivePcWindow` yorumu buna göre güncellenir (S1 planı fark 9'un kapanışı).
7. **Kapsam kuralının işlemi (§4.2 "Her denemede").** Bugün `ApplyOncePerScopeAsync` tek işlem açıp kilidi bir kez alır, denemeler aynı işlemde (`:398-404`, `:437`). Havuz yolunda kilitlenme kurbanı (1205) işlemi ve uygulama kilidini bütünüyle düşürür; aynı işlemde yeniden denemek yanlış olur. **Çözüm:** havuz yolunda her deneme kendi işlemini açar → kilit → Lookup → yüklem → plan → yazım → commit (`DrawRequest.ScopeLockResource`). Satır kipinin `ApplyOncePerScopeAsync`'i aynen kalır.
8. **Satır kipinin bilinen kusurları korunur.** `WriteDeductionAsync`'te 2.+ denemede Lookup yok (`:497-521`), 3. jeton çakışması 500, 1205 → 500. §4.2'nin düzeltmeleri (her denemede Lookup, baştan kurma, 503, `IsDeadlock`) yalnız havuz yolundadır: satır kipinin davranışı değişmez.
9. **Yakalama sırası ve PK tanıyıcısı.** Bugünkü PK oynatması genel `DbUpdateException when key != null` (`:522`). Havuz yazımında bakiye satırı açma yarışı da `DbUpdateException` olduğundan genel süzgeç onu oynatmaya götürürdü. **Çözüm:** dar tanıyıcı `PersonBalance.IsDuplicateTransactionKey` (2601/2627 + `PK_CustomerBalanceTransactions`, göç `20260602125823_AddCustomerBalance`); `DrawAsync`'in sırası `DbUpdateConcurrencyException` → `IsDuplicateReversal` → `IsDuplicateBalanceRow` → `IsDuplicateTransactionKey` (yalnız anahtar istemcinin) → `Exception when IsDeadlock` (okumalar dahil).
10. **Panel reverse, eski (`DrawGroupId NULL`) eksi hedef, havuz kipinde.** Bugünkü yol parayı rota satırına yazar (`ApplyTransactionAsync(…, canonicalId, …)` `:249`); havuz kipinde hedef `S(rota)`'nın başka üyesinde olabilir, K1 "her bacak kendi satırına döner" der, §4.9 düşüm ve elle eksi için grup geri almasını ister. **Çözüm:** havuz kipinde eski eksi hedef (`Amount ≤ 0`) `ReverseGroupAsync`'e gider (tek bacaklıda `DrawGroupId = NULL`, para hedefin güncel satırına, M13). Satır kipinde bugünkü yol birebir (S1 planı fark 12).
11. **K5 tek satır yazıcısını kullanamaz (M12).** `ApplyTransactionAsync`'in "yeniden yükle + deltayı yeniden uygula" döngüsü (`:393-402`) çok bacaklı kayıtta deltayı çakışmayan satırlara iki kez yazardı; ayrıca hareketi okumadan önce izleyiciye ekler. **Çözüm:** havuz kipinin eksi ayarı ve artı iptali `DrawAsync`'ten geçer; denetleyici çağırmadan önce izleyiciye bir şey eklemez. Satır kipinin yolu aynen.
12. **K5'in çakışma sonucu tanımsız (§4.13 "baştan kur, 3 deneme").** **Çözüm:** üç denemeden sonra panelde 503 `balance-busy`, grup geri almasıyla aynı metin (`PanelCustomerBalanceController.cs:306-307`). Satır kipinin 500'ü değişmez.
13. **`enable`/`disable` ayrıştırıcısı.** `PersonBalanceCommand.TryParseArgs` yalnız `verify`'ı tanır; S1 testi `person-balance enable --license …`'ı geçersiz sayar (`PersonBalanceCommandTests.cs:38`) ve `RunAsync(db, parsed!, …)` `VerifyArgs` türüne bağlı (`:67-72`). **Çözüm:** `TryParseArgs` ve S1 testleri aynen kalır (verify'a özgü); `enable|disable` için ayrı `TryParseToggleArgs`; `RunAsync(string[])` `args[1]`'e göre dağıtır.
14. **`enable`'ın 1. koşulunun sorgusu.** `SalesModeGate.ClientVersionsAsync` (`:309-324`) private ve `SalesSyncOptions`'a bağlı; `PersonBalanceVerifier.ActivationRow` (`:52`) Id taşımıyor. **Çözüm:** `PersonBalanceEnableGate` aynı süzgeçle (devre dışı bırakılmamış, `LastSeenAt >= now − ActivePcWindow`, makine adı okunmaz) kendi sorgusunu kurar ve public `SalesModeGate.OldActivation`/`ClientVersionCheck` kayıtlarını yeniden kullanır; satış senkronu koduna dokunulmaz.
15. **`enable`'ın 2. ve 3. koşulunun tanımı.** 2: kırpma farkı spec'in SQL'i değil S1'in `TrimMismatches` + `ControlCharGroups`'u (S1 planı fark 15); D4, D5, D6, yetim bacak `LicenseReport`'tan. 3: tabanın varlığı verifier'ın `HasBaseline`'ı (işaret satırı ya da ilk yakalamadan sonra verilmiş lisans, S1 fark 7); incelenmemiş = lisansın `ReviewedAt`'i boş bütün taban satırları (işaret dahil; `--mark-reviewed` işareti de işaretler); yeni sapma = günlük uyarının taban kalemleri (`NegativeRows.Outside`, `WorsenedBaselineRows`, `LedgerMismatches.Outside`, `ChangedMismatchRows`, `CopyRows.Outside`). Tabanı olmayan lisans reddedilir ("önce `--capture-baseline`"); prod'da 29.10'daki lisanssız yakalama bütün lisanslara işaret yazdı.
16. **D3 uyarısı (S1 planı fark 8).** Verifier'ın anlık görüntüsünde kip yok (`LicenseRow(Id, IssuedAt)`, `:42`). **Çözüm:** `LicenseRow`'a varsayılan değerli `PoolSince`; havuz kipindeki lisansta `NegativePersons > NegativePersonsExplained` → `Alerts`'a "D3" satırı. Tabandan bağımsızdır: §8.1'in "taban yoksa yalnız sayı" istisnası eksi/D1/kopya kalemleri içindir. Günlük iş (`PersonBalanceVerifyJob`) değişmeden e-postaya taşır.
17. **Yarış testlerinin zamanlaması.** §8.2-6'nın üç konumu için üretim koduna test kancası eklenmez: okuma konumları `ReadQueryHook` zinciriyle (`FROM [CustomerBalances]` / `FROM [WpfCustomerProjections]`), kayıt öncesi `RaceSaveHook` ile. `RaceSaveHook`'un API'si koddaki (`Once(Func<DbContext,Task>)` aşırı yüklemesi, sayaç `ThrowingConcurrencyExceptionAsync`'te), S1 planının Görev 8 bloğundaki değil.
18. **Belirsizlikler — mühendislik kararıyla (sahip sorusu değil):**
    - a. Üyeliğin ikinci okumasında fark → deneme sayılır (3 sınırına girer; sonsuz döngü yok, tükenince 503).
    - b. Silinmiş üyeler arasında eşitlik kırıcı da Id "N" ordinal (M7 "sıra belirlenimli").
    - c. Oynatmadaki `RemainingBalance` havuz kipinde 409 kuralları uygulanmadan hesaplanır (`PoolBalanceAsync`): `max(0, Σ)`, `memberIds` yoksa `Σ_S` (§4.2 "kayıtlı anahtar hiçbir zaman `customer-not-synced` almaz").
    - d. İptal çapasının `Reason`'ı bugünkü `"Reverse of {id:N}"`, bacakta NULL (§3.2).
    - e. `enable` zaten açıkken / `disable` zaten kapalıyken: mesaj, yazım yok, çıkış 0; `PersonBalancePoolSince` korunur (`sales-sync` emsali, `SalesSync.Enable.cs:43`). Ayrıca (Görev 12): kuru `enable` (`--apply`'sız) dört koşulu basar ve koşul tutmasa da **0** döner; ret (1) yalnız `--apply`'da (spec §7.2 kuru koşunun çıkış kodunu söylemez; `sales-sync enable` emsali `SalesSync.Enable.cs:107-111`). `disable` **yapılandırma okumaz** (`PersonBalance:*`/`SalesSync:*` yalnız `enable` dalında okunur, yalnız bağlantı dizesi ortak): kill switch, kullanmadığı bir ayarın bozukluğuyla kilitlenmemeli (§7.4). `ActivePcWindowDays <= 0 → 2` denetimi `RunEnableAsync`'in başındadır.
    - f. `memberIds` taşıması: preview'da tekrarlı sorgu parametresi `memberIds` (`[FromQuery(Name = "memberIds")] Guid[]?`), apply'da gövde alanı `memberIds` (`ApplyRequest.MemberIds`, `IReadOnlyList<Guid>?`). Yok ile boş aynı sayılır (ASP.NET üst düzey dizi parametresi verilmezse boş dizi bağlar) → 400 `member-ids-required`. Satır kipinde alan okunmaz. **W-havuz aynı adları kullanmalı.** **Tek satır kipi farkı (kabul edildi, Görev 5):** Guid'e bağlanamayan `memberIds` değeri (`memberIds=` boş metin ya da bozuk Guid) preview'da sorgu, apply'da JSON gövdesi model bağlamasında, kip okunmadan `[ApiController]`'ın 400 `ValidationProblem`'ını alır (başlık `member-ids-required` değil); bugün aynı istek bilinmeyen parametre/alan sayılıp yok sayılır ve 200 alır. Bugünkü WPF alanı göndermez, W-havuz yalnız geçerli Guid gönderir (spec §6.1-a); sahada hiçbir isteğin yolu ve para davranışı değişmez. Parametreyi `string[]?` bağlamak farkı kapatırdı ama `Preview` imzasını ve apply'ın gövde alanıyla tutarlılığı bozardı; seçilmedi. Preview tarafı `PersonBalancePoolPreviewTests.Satir_kipinde_memberIds_yok_sayilir_PersonPool_false` (`memberIds=bozuk` kolu, Görev 5), apply tarafı `PersonBalancePoolReadersTests.Satir_kipinde_memberIds_okunmaz_bozugu_400` (`memberIds = ["bozuk"]` ve `memberIds = "bozuk"` gövdeleri, Görev 6 Not 6) ile kilitli.
    - g. Preview `UpdatedAt` (havuz kipi): `P`'nin son para hareketi; yoksa `P`'nin bakiye satırlarının en yeni `UpdatedAt`'i; o da yoksa şimdi (M9).
    - h. Metrik adları ve etiketleri (spec yalnız dört sayaç ve "bacak sayısı etiketi" der): `orderdeck.balance.customer_not_synced_total` (`endpoint`), `orderdeck.balance.client_update_required_total` (`gate` = `person-balance` | `sales-sync`, `endpoint`; §8.5'in v0.9.9 denemesi satış kapısını da görür), `orderdeck.balance.busy_total` (`endpoint`: WPF apply ve reverse, panel reverse ve elle eksi), `orderdeck.balance.multi_leg_draws_total` (`legs`, çapa dahil yazılan satır sayısı; yalnız `legs > 1`). `endpoint` değerleri: WPF'te `preview`, `scope`, `apply`, `reverse`; panelde `panel/manual-adjustment`, `panel/reverse`. Sayaç başına: `client_update_required` WPF'te `preview | scope | apply | reverse` (satış kapısı dördünde de, `ClientUpdateRequired` `:117`, `:188`, `:345`, `:665`; havuz kapısı yalnız `preview | apply`, fark 4); `customer_not_synced` `preview | apply`; `busy` `apply | reverse | panel/manual-adjustment | panel/reverse`. **`scope` sayılır (karar, Görev 13):** spec §8.6 `endpoint` değerlerini saymaz, saymak spec'e aykırı değil; mod On'da v0.9.10 anahtarsız işte önce `scope`'u sorar ve onun 409'unda mesajı engeller (R6-01, `:164-168`), preview'a hiç gelmez — `scope` sayılmasa §8.5'in "metrikte `client-update-required` görünür" denetimi tam bu istemcide boş kalırdı. Görev 13'ün "mimar reddederse" yedek bloğu uygulanmaz. Satış senkronunun başka uçlarındaki `client-update-required` (komut, kira) sayılmaz.
    - i. 409 `customer-not-synced` günlüğü (§5.5): lisans + satır Id'leri ("N"), ad/telefon yok; düzey Information.
    - j. Havuz kipinde alt sınır boş/bozuk: 409 `client-update-required` + günlüğe **Error** (§7.2 "günlüğe hata yazılır"); preview de aynı kapıdan geçer. Açılışta aynı durum + havuz kipinde lisans → tek **Warning**.
    - k. Panel artı hareket iptali (K5, `ReverseInPoolAsync`): `DrawAsync`'in geri çağrısı 2.+ denemede "geri alındı mı"yı yeniden sorar; eşzamanlı ikinci iptal, kazananın commit'inden sonra taze planda `insufficient-balance` yerine 409 `already-reversed` alır (§4.9 "Hakem N01"; jeton çakışması N01 ihlalinden önce gelebilir). **Genişletme (Görev 10 Not 4):** `DrawAsync` `Insufficient` döndürürse denetleyici "geri alındı mı"yı bir kez daha sorar; kazanan, kaybedenin ilk sorusundan SONRA ama 1. denemenin bakiye okumalarından ÖNCE commit ettiyse (jeton çakışması olmaz, ikinci deneme gelmez) yanıt yine `already-reversed` olur. Bu yeniden soru servisin dışındadır; 1205'i `BalanceBusy` (503) olur, 500 değil. Havuz kipinde iptalin üyelik okuması (`MembersAsync`, `ReverseInPoolAsync`'in başı ve `ReverseDrawGroupAsync`'in havuz dalı) da servisin dışındadır ve `CustomerBalances`'ı okur; 1205'i aynı biçimde 503 olur (Görev 10 Not 6, kanıt Görev 16 Not 10). Para güvenliği N01'e dayanır, bu yalnız yanıtın doğruluğudur; yalnız havuz dalında, satır kipi değişmez.
    - l. Havuz kipinde panel eksi `manual-adjustment`'ın kuruş altı tutarı → 400 `invalid-money-scale` (apply'ın `:319-322` emsali, detay "Amount kuruş hassasiyetini aşamaz."), `DrawManualAdjustmentAsync`'ten önce (Görev 9). Satır kipi aynen (bugün yalnız `Amount == 0` reddedilir, `PanelCustomerBalanceController.cs:185`; SQL `decimal(18,2)` yuvarlar). `DrawAsync`'in `Requested` kuruş altıysa döngüden önce attığı `ArgumentOutOfRangeException` (Görev 4) savunma olarak kalır: `decimal(18,2)` bacağın `Amount`'unu ve satırın bakiyesini ayrı yuvarlar, D1 bozulurdu. Görev 4'ün "`Requested` kuruş hassasiyeti" notu (eski adıyla "Sahip sorusu (Görev 9'a bağımlı)") bu maddeye bağlanır; sahip sorusu değildir.
    - m. Havuzlu apply'da aynı anahtarlı PK yarışında kazanan bulunamazsa (Görev 7 Not 2): satır yolu özgün `DbUpdateException`'ı `throw;` ile yeniden atar (`:532`); `DrawAsync` PK ihlalini `DuplicateKey`'e çevirdiği için (fark 9) havuz yolu `InvalidOperationException("Bakiye apply: anahtar PK ihlali verdi ama kazanan bulunamadı")` atar. İkisi de 500, gerçek DB sorunu yutulmaz; yalnız istisna türü farklı. Satır kipi değişmez. **Son inceleme düzeltmesi (30.10.2026):** havuz yolunda kazananın Lookup'ı fark 18n'nin yardımcısından geçer: anahtar "foreign" çıkarsa 404 (ön oynatmanın ve `NoBalance` kolunun kararıyla aynı; `LookupAsync` belgesine göre istemci hatası), Lookup 1205 alırsa 503 `balance-busy`; 500 yalnız kayıt hiç bulunamazsa. Satır yolu foreign'de 500 kalır (fark 8); bilinçli fark, para etkisi yok (iki yanıtta da hiçbir şey yazılmadı, WPF işi belirsiz bırakır).
    - n. **Spec ↔ kod farkı — `no-balance`'tan önce son bir Lookup (Görev 7 Not 6).** §4.2 adım 6 Lookup'ı "2. ve sonraki her denemenin başında" ister; amacı ise kayıtlı anahtarın ret almaması. Kapsamsız yolda 1. denemenin tek koruması kilitsiz ön oynatmadır: aynı anahtarlı ilk istek ön oynatmadan sonra, `DrawAsync`'in 1. deneme okumalarından önce commit edip havuzu tüketirse (`Σ ≤ 0`) ikinci istek 409 `no-balance` alırdı. Çözüm: `ApplyPoolAsync` `NoBalance` sonucunda, anahtar varsa `LookupAsync(…, pool: true, …)`'u bir kez daha koşar — foreign → 404, conflict → 409 `content-conflict`, kayıt → oynatma; yoksa 409 `no-balance`. Salt okuma; `Drawn` ve `NothingToApply` yollarına, satır kipine dokunmaz. InMemory'de kanıtlanamaz. **Son inceleme düzeltmesi (30.10.2026):** aynı pencere `customer-not-synced`'te de açıktı (aynı anahtarlı istek ön oynatmadan sonra commit eder, bu arada ilgisiz bir yazım kural 3/3b'yi bozar; spec §4.2 "kayıtlı anahtar hiçbir zaman customer-not-synced almaz"). Son Lookup artık `NoBalance` ve `CustomerNotSynced`'te koşar, `DuplicateKey` de aynı yardımcıyı (`ReplayWrittenKeyAsync`) kullanır (fark 18m). Bu Lookup `DrawAsync`'in korunan denemesinin dışındadır ve oynatmada `PoolBalanceAsync` ile `CustomerBalances`'ı okur: 1205 → 503 `balance-busy` (fark 18k'nın iptal emsali). Havuz kipinde Apply'ın ön oynatması da aynı okumayı yapar ve aynı kararı alır; satır kipinin ön oynatması değişmez (fark 8). Kaynağı ayıran satır denetleyicide (`ReplayDeadlocked`, Information). Kanıt: Görev 14'ün sınıfında `Ilk_denemede_ayni_anahtarli_rakip_yazdiysa_customer_not_synced_yerine_oynatilir`, `Cekimden_sonraki_Lookup_kilitlenme_kurbani_olursa_503_balance_busy`, `On_oynatmanin_bakiye_okumasi_kilitlenme_kurbani_olursa_503_balance_busy`.
19. **Preview'da kural 1'in kesin kolu (sahip kararı 1, 29.10; Görev 5).** Spec §4.0-1 preview ve apply için 409 `no-balance` (kesin) der. **Çözüm (sahip kararı):** havuz kipinde preview bu kolda (ne `a` ne `W`'nin bir Id'si sunucuda) **200 `Balance = 0`, `PersonPool = true`** döner — satır kipinin "satır yoksa 0" yanıtı (`:127`), yalnız bayrağı farklı; apply 409 `no-balance` kalır. Gerekçe: bugünkü WPF (`OrderDeck.App/Services/PaymentRequestService.cs:1397-1420`) preview'ın her hatasını genel `catch`'le yutup mesajı engeller, `MarkNoBalance`'ı yalnız önizleme bakiyesi ≤ 0 iken yazar. 409 dönseydi spec'in "yeni açılmış, henüz gönderilmemiş müşteri bekletilmez" amacına karşın müşteri her denemede bekletilirdi; 200 `Balance = 0` ile hem bugünkü hem W-havuz WPF istemci değişikliği olmadan doğru davranır, spec §6.1-b'ye eşleme eklemek gerekmez. Anlamca da doğru: hiçbir üye sunucuda yoksa para olamaz, kesin 0. Preview tarafı `PersonBalancePoolPreviewTests.Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir` (Görev 5), apply tarafı `Kural1_kesin_409_no_balance` (Görev 7) ile kilitli.

## Bilerek S2 dışında bırakılanlar

- **W-havuz (WPF) ve Wh sürümü** → ayrı PR (spec §6.1, §12). Bu plan sunucu sözleşmesini sabitler: preview `?memberIds=` (tekrarlı), apply gövdesinde `memberIds`; 409 `customer-not-synced` / `client-update-required` / `no-balance`; 400 `member-ids-required`; `PersonPool` (preview ve scope). Sahip kararı 1 (fark 19) gereği preview kural 1'in kesin kolunda 200 `Balance = 0` döner; W-havuz preview'da `no-balance` için ayrı eşleme gerektirmez (bakiye ≤ 0 → `NoBalance` bugünkü yoldan), `no-balance`'ı yalnız apply'da görür.
- **D-banka** (spec §9 docs PR) ve banka PR5'in `DrawAsync` çağrısı. `DrawAsync` yeniden kullanılabilir imzada yazılır (`DrawAmountRule.UpToPool`, çekim türü iptal girişi: `Kind = reversal`, `ReversesTransactionId`), banka çağrısı yok.
- Panel ve Shopper istemcileri (sözleşme aynı); panel `manual-adjustment` arayüzü (§6.3).
- §10 açık kalanları: Ayır'da silinmiş hesabın parası, numara izlemesinin hakemi, kimlik birleşmesinin grup açığı, kesinleşmiş `NoBalance`'ın yeniden sorulması. S2'yi bloklamaz.
- İade uçlarının idempotency'si ve ilk satır yarışındaki 500 (§4.7), `Balance >= 0` CHECK, dinamik sürüm kapısı (§7.2), kişi düzeyinde A11/scope (M10), `person-balance status`.
- Taban dışı bir sapmayı bilinçli kabul eden ya da düzeltilmiş D1 taban satırının uyarısını susturan CLI (S1 planı fark 20, 22) → Sahip kararı 2: S2'de değişiklik yok (prod tabanında düzeltilecek satır yok).
- `verify`'ın 9. maddesinin pencereyi ayardan okuması (fark 6).
- Şema değişikliği ve göç: yok. Bir görev göç gerektirirse durur ve sorar.

## Ön koşullar ve sıra (spec §7.1)

- **Adım 0–4 tamam (prod, 29.10.2026):** S1 (#533) 22.10'dan beri prod'da; `person-balance verify` temiz; `verify --capture-baseline --apply` (lisanssız: bütün lisanslara işaret satırı); dört lisansta `verify --mark-reviewed --license <id> --apply`. `enable`'ın 3. koşulu bunlara dayanır.
- **Adım 5 = bu plan.** S2 merge = otomatik prod deploy. Merge **yayın penceresi dışında** (Paz/Pzt/Çar/Per 20:00–01:00 TR değil), Burak yapar; PR açılırsa DRAFT kalır.
- S2 deploy'unda **hiçbir lisans havuz kipinde değil**: `PersonBalancePoolSince`'i yalnız `person-balance enable --apply` yazar; S2 bu komutu getirir, koşturmaz. Satır kipinin davranışı değişmez. S2 göç eklemez → otomatik geri alma S1 imajına döner ve güvenlidir (§7.4); S1 bayrağı okumaz.
- S2 sonrası saha kontrolü (kip kapalı, §8.5): aynı müşteride preview, apply, reverse bugünkü gibi; yeni satırların `DrawGroupId`'si NULL. Sorguları Görev 17 yazar, Burak koşar.
- **`enable` ancak şunlardan sonra (adım 6–9):** W-havuz + **Wh** Velopack sürümü yayımlanıp PC'ler güncellenir; `.env`'e `PersonBalance__MinClientVersion=<Wh>` yazılır ve `docker compose up -d license-server` (koşul 4 için `SalesSync__MinClientVersion` dolu ve ≤ Wh); test lisansında karışık filo denemesi (§8.5); pilot lisansta önce kuru `person-balance enable --license <id>`, sonra `--apply` — Burak'ın onayıyla, yayın penceresi dışında. Sorun olursa `person-balance disable --license <id> --apply` (kill switch, veri işlemi yok).

---

## Genel kurallar

- Worktree: `C:/Users/burak/source/repos/LiveDeck/.claude/worktrees/ortak-bakiye-s2`, dal `feat/ortak-bakiye-s2` (`origin/master` `b6ee847c`'den; upstream yok, **push yok**; push ve PR yalnız Burak isterse). Bütün komutlar bu kökten. Ana checkout'a (`C:/Users/burak/source/repos/LiveDeck`, docs dalı) dokunma.
- Repo **public**: testlerde literal kimlik bilgisi YOK. Parola/özet/anahtar üret: `$"g-{Guid.NewGuid():N}"`, değişkene **nötr ad** ver (`giris`, `ozet`; `password`/`secret` gibi adlar tarayıcıyı tetikler). Telefon `TestPhone.NewE164()`. Kullanıcı adları, grup numaraları ve Id'ler `Guid`'den üretilir; gerçek ad/telefon/IBAN yok. Doküman yorumlarında da parola biçimli örnek yok.
- Türkçe yorum ve test adı (snake, `Kural3b_..._customer_not_synced` biçimi; `using Kit = …PersonBalanceTestKit;`). Commit: `git add <dosyalar>` + `git commit -F - <<'MSG' … MSG`; başlık `feat(balance): …` / `test(balance): …` / `docs(deploy): …`, Türkçe emir kipi; gövde nedeni söyler; son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.
- Sunucu derlemesi `TreatWarningsAsErrors`: 0 uyarı. Kullanılmayan özel üye, kullanılmayan yerel ya da parametre bırakma (bir görevin eklediği üyeyi kullanan kod aynı görevde olmalı).
- Filtreli test (git-bash): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~<Sınıf>"`; birden çok sınıf `"FullyQualifiedName~A|FullyQualifiedName~B"`. PowerShell'de: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test ...`.
- Testcontainers testleri Docker ister. Yerelde "Docker kapalı" gibi düşerlerse: `docker context ls`, sonra **PowerShell'den** `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'` (İKİ eğik çizgi; boru adı `docker_engine` da olabilir — `docker version` hangisinde yanıt veriyorsa). git-bash bu değeri bozar. Yeni ilişkisel test sınıfı `[Collection(SqlServerCollection.Name)]` + `[Trait("Category", "Testcontainers")]` + `IAsyncLifetime` + ctor'da `SqlServerContainerFixture` (CI parçası `ShardingTestFramework` koleksiyondan otomatik seçer; elle atama yok). Sınıf yorumunun son cümlesi: "Canlı yayın sırasında ÇALIŞTIRILMAZ."
- Kaynak dosyaları **yalnız Edit/Write aracıyla** düzenle. PowerShell 5.1 `Get-Content`/`Set-Content` BOM'suz UTF-8'i bozar.
- **S2 göç eklemez.** `dotnet ef migrations add` yok; model değişikliği gerekirse dur ve sor (genişlet/daralt; S1'e otomatik dönüş güvenli kalmalı).
- InMemory'de eşzamanlılık jetonu **denetlenir** ama `SaveChanges` atomik **değildir**, tekil indeks yoktur; çakışma ve yarış iddiaları yalnız Testcontainers'ta.
- **Satır kipi regresyonu:** her görevin "PASS + regresyon" adımı kendi testlerinin yanında var olan bakiye testlerini DEĞİŞTİRMEDEN koşturur. Filtre kümeleri:
  - **WPF:** `LicensesCustomerBalanceApplyControllerTests|CustomerBalanceScopeRuleTests|PersonBalanceWpfReadersTests|PersonBalanceScopeRuleSumTests|PersonBalanceGroupReversalTests`
  - **Panel/Shopper:** `PanelCustomerBalanceControllerTests|PersonBalancePanelListTests|PersonBalancePanelReverseTests|PersonBalanceShopperListTests|ShopperBalanceControllerTests|ShopperIdentityTakeoverTests`
  - **Servis/CLI (önek):** `PersonBalanceMembers|PersonBalanceHistoryTests|PersonBalanceVerifier|PersonBalanceVerifyJobTests|PersonBalanceCommand`
  - **İlişkisel (Docker):** `CustomerBalanceScopeRuleRelationalTests|CustomerBalanceConcurrencyTests|PersonBalanceReversalRaceTests|PersonBalanceReversalDeadlockTests|PersonBalanceReversalLegMoveRaceTests|PersonGroupHookRelationalTests`
  - `~X` sınıf adına (tam adın alt dizgisine) eşleşir, dosya adına değil; aynı dosyadaki kardeş sınıflar ayrıca yazılır. Bu yüzden `PersonBalanceScopeRuleSumTests` (master `PersonBalanceWpfReadersTests.cs:147`'de ayrı üst düzey sınıf; mod On'da scope `−Σ(grup)`, 409 `scope-has-deduction` gövdesi) WPF kümesinde açıkça listelenir; `ShopperIdentityTakeoverTests` Shopper `GET …/balance`'ı çağırır (`:210`, `:250`, `:308`…).
  - Servis/CLI kümesi bilerek önektir: `~PersonBalanceMembers` `PersonBalanceMembersRelationalTests`'i (`PersonBalanceMembersTests.cs:100`), `~PersonBalanceVerifier` `PersonBalanceVerifierLoadTests`/`CaptureTests`/`RelationalTests`'i (`PersonBalanceVerifierTests.cs:412/:473/:600`), `~PersonBalanceCommand` `PersonBalanceCommandRelationalTests`'i de kapsar (Görev 11 Adım 5'in notu emsal). İlişkisel kardeşler Testcontainers'tır, **Docker ister** (yukarıdaki `DOCKER_HOST` adımı). `~PersonBalanceVerifier` `PersonBalanceVerifyJobTests`'i yakalamaz (ayrı yazılır).
- Alt ajan kuralı: Monitor ya da arka plan bekleme yok. Tam paket (Görev 17) yürütücünün koşacağı komut olarak yazılır.
- Geliştirme PC'si yayın PC'si değil: yayın saatlerinde yerel test/Docker serbest; yasak yalnız merge/deploy/sürüm.

## Dosya haritası

| Görev | Dosya |
|---|---|
| 1 | Oluştur: `Services/CustomerSync/PersonBalanceOptions.cs`, `Services/CustomerSync/PersonBalanceStartup.cs`. Değiştir: `Program.cs` (DI `:184-187` yanına; açılış uyarısı `:963-971` kapsamında), `Services/CustomerSync/PersonBalance.cs` (`IsPoolModeAsync`), `Controllers/Licenses/LicensesCustomerBalanceApplyController.cs` (`OwnedLicenseModeAsync` → `(Mode, Pool)?`, dört çağıran). Test: `TestHelpers/PersonBalanceApiFactories.cs` (yeni), `Controllers/Licenses/PersonBalanceTestKit.cs` (genişlet), `Services/CustomerSync/PersonBalanceOptionsTests.cs`, `Services/CustomerSync/PersonBalanceStartupTests.cs`, `Controllers/Licenses/PersonBalanceModeTests.cs` |
| 2 | Değiştir: `PersonBalance.cs` (ctor + `CustomerIdResolver`; `CodeNoBalance`, `CodeCustomerNotSynced`, `DrawSet`, `DrawSetRejection`, `DrawSetResult`, `ResolveDrawSetAsync`, `PersonDrawSetAsync`, `PoolBalanceAsync`). Test: `Services/CustomerSync/PersonBalanceDrawSetTests.cs` |
| 3 | Değiştir: `PersonBalance.cs` (`DrawLeg`, `PlanDraw`). Test: `Services/CustomerSync/PersonBalancePlanDrawTests.cs` |
| 4 | Değiştir: `PersonBalance.cs` (`DrawAmountRule`, `DrawRequest`, `DrawStatus`, `DrawResult`, `DrawAsync`, `IsDuplicateTransactionKey`). Test: `Services/CustomerSync/PersonBalanceDrawTests.cs` |
| 5 | Değiştir: `LicensesCustomerBalanceApplyController.cs` (ctor `IOptions<PersonBalanceOptions>`, preview havuz dalı, havuz yardımcıları). Test: `Controllers/Licenses/PersonBalancePoolPreviewTests.cs` |
| 6 | Değiştir: `LicensesCustomerBalanceApplyController.cs` (`ApplyRequest.MemberIds`, `LatestScopeDeductionAsync(…, personPool, …)`, `LookupAsync(…, pool, …)`). Test: `Controllers/Licenses/PersonBalancePoolReadersTests.cs` |
| 7 | Değiştir: `LicensesCustomerBalanceApplyController.cs` (apply havuz dalı `ApplyPoolAsync`, `ScopeHasDeduction`). Test: `Controllers/Licenses/PersonBalancePoolApplyTests.cs` |
| 8 | Değiştir: `Controllers/Panel/PanelCustomerBalanceController.cs` (GET havuz dalı), `Controllers/Shopper/ShopperBalanceController.cs` (GET havuz dalı). Test: `Controllers/Panel/PersonBalancePoolPanelListTests.cs`, `Controllers/Shopper/PersonBalancePoolShopperListTests.cs` |
| 9 | Değiştir: `PanelCustomerBalanceController.cs` (`manual-adjustment` havuz dalı, `BalanceBusy` yardımcısı). Test: `Controllers/Panel/PersonBalancePoolManualAdjustmentTests.cs` |
| 10 | Değiştir: `PanelCustomerBalanceController.cs` (reverse havuz dalı `ReverseInPoolAsync`, `ReverseDrawGroupAsync(…, pool, …)`). Test: `Controllers/Panel/PersonBalancePoolReverseTests.cs` |
| 11 | Oluştur: `Services/CustomerSync/PersonBalanceEnableGate.cs`. Test: `Services/CustomerSync/PersonBalanceEnableGateTests.cs` |
| 12 | Değiştir: `Tools/PersonBalanceCommand.cs` (`ToggleArgs`, `TryParseToggleArgs`, `RunEnableAsync`, `RunDisableAsync`, dağıtım). Test: `Tools/PersonBalanceToggleCommandTests.cs` (InMemory), `Tools/PersonBalanceToggleCommandRelationalTests.cs` (Testcontainers) |
| 13 | Değiştir: `Services/CustomerSync/PersonBalanceVerifier.cs` (`LicenseRow.PoolSince`, D3, `ActivePcWindow` yorumu), `Services/Observability/OrderDeckMetrics.cs`, `LicensesCustomerBalanceApplyController.cs` ve `PanelCustomerBalanceController.cs` (sayaçlar). Test: `Services/CustomerSync/PersonBalanceD3AlertTests.cs`, `Services/CustomerSync/PersonBalanceVerifyJobTests.cs` (bir test ekle), `Controllers/Licenses/PersonBalanceMetricsTests.cs` |
| 14 | Test: `Controllers/Licenses/PersonBalanceDrawRaceTests.cs` (Testcontainers 1, 2, 10, 503), `Controllers/Licenses/PersonBalanceDrawDeadlockTests.cs` (1205) |
| 15 | Test: `Controllers/Licenses/PersonBalanceDrawMembershipRaceTests.cs` (Testcontainers 6, 7, 8) |
| 16 | Değiştir: `Controllers/Panel/CustomerBalanceConcurrencyTests.cs` (Testcontainers 14). Test: `Controllers/Panel/PersonBalancePanelDrawRaceTests.cs` (9), `Controllers/Licenses/PersonBalanceScopeRuleRelationalTests.cs` (15) |
| 17 | Değiştir: `deploy/docker-compose.yml`, `deploy/README.md`. Test: `Services/CustomerSync/PersonBalanceDeployTests.cs`. Tam paket |

Bütün yollar `OrderDeck.LicenseServer/` ya da `OrderDeck.LicenseServer.Tests/` altında (tablodaki göreli yollar ona göre; `deploy/` repo kökünde).

## Görev özeti

| # | Görev | Kabaca efor |
|---|---|---|
| 1 | `PersonBalanceOptions`, açılış uyarısı, kip okuması, test kiti ve fabrikalar | 2,5 sa |
| 2 | `ResolveDrawSetAsync` (kural 1–4), `PersonDrawSetAsync`, `PoolBalanceAsync` | 2,5 sa |
| 3 | `PlanDraw` (saf) + tablo testleri | 1 sa |
| 4 | `DrawAsync`: yazım, kişi kilidi, üyeliğin ikinci okuması, baştan kurma | 3,5 sa |
| 5 | Havuzlu preview + havuz sürüm kapısı + `member-ids-required` + `customer-not-synced` | 2,5 sa |
| 6 | scope `PersonPool`, oynatmanın `RemainingBalance`'ı, `ApplyRequest.MemberIds` | 2 sa |
| 7 | Havuzlu apply (`DrawAsync`'e bağlama, kapsam kuralı deneme başına, Örnek 1–2) | 3,5 sa |
| 8 | Panel ve Shopper havuz okumaları | 2 sa |
| 9 | K5: panel eksi `manual-adjustment` (havuz kipi) | 1,5 sa |
| 10 | K5: panel reverse (havuz üyeliği, iade iptali, eski `reversal` → `not-reversible`) | 2,5 sa |
| 11 | `PersonBalanceEnableGate` (koşul 1–4) | 2,5 sa |
| 12 | `person-balance enable|disable` CLI | 3 sa |
| 13 | D3 uyarısı + dört metrik (iki parça, iki commit) | 2 sa |
| 14 | Testcontainers 1, 2, 10 + 503 + 1205 (apply yarışları) | 3 sa |
| 15 | Testcontainers 6, 7, 8 (üyelik yarışları) | 2,5 sa |
| 16 | Testcontainers 9, 14, 15 (K5 ∥ apply, var olan dört test havuz kipinde, kapsam kuralı) | 3,5 sa |
| 17 | Compose satırı, `deploy/README`, saha notları + tam paket | 1 sa (+ ~15 dk koşu) |

Toplam ≈ 41 sa (+ ~15 dk tam paket koşusu) ≈ 5 iş günü. Spec tahmini 4 gün; S1'in 3,75 gün tahmini planda ≈ 33 sa idi. İlk kestirim 35 sa'tı; görevler yazıldıktan sonra içeriklerine göre yeniden kestirildi (+6 sa). Fark test hacminden ve çekim yolundan geliyor: Görev 16 var olan dört eşzamanlılık testini iki kipe çevirir, TC9'u, eşzamanlı iade iptalinin üç N01 testini, iptalin üyelik okumasının 1205 testini (iki kol, Görev 10 Not 6) ve TC15'in kilit testlerini ekler (23 test, üç sınıf); Görev 12'de 14 InMemory ve 4 ilişkisel test var (iki yarış kancası); Görev 14'te sekiz yarış testi ve 1205 fabrikası; Görev 11'de 24 kapı testi; Görev 13 iki parça ve dört üretim dosyası; Görev 1'de dört yeni test dosyası, kit ve iki fabrika; Görev 4 ve 7'de `DrawAsync`'in deneme döngüsü ile `ApplyPoolAsync`'in geri çağrısı; Görev 5'te 19 test; Görev 6 ve 8'de ikişer test sınıfı.

## Görevler arası imzalar (tek kaynak)

Her görev yalnız kendi satırlarını TANIMLAR; sonraki görevler aynen TÜKETİR. Ad, tür ya da parametre sırası değişirse bu blok ve tüketen bütün görevler birlikte güncellenir.

```csharp
// ── Görev 1 ── OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceOptions.cs
namespace OrderDeck.LicenseServer.Services.CustomerSync;
public sealed class PersonBalanceOptions
{
    public const string Section = "PersonBalance";
    public const int DefaultActivePcWindowDays = 14;
    public string? MinClientVersion { get; set; }
    public int ActivePcWindowDays { get; set; } = DefaultActivePcWindowDays;
    public TimeSpan ActivePcWindow => TimeSpan.FromDays(ActivePcWindowDays);
    public ClientVersion? ParseMinClientVersion() => ClientVersion.Parse(MinClientVersion); // Services.Sync.ClientVersion
}
// ── Görev 1 ── Services/CustomerSync/PersonBalanceStartup.cs
public static class PersonBalanceStartup
{
    /// döner: uyarı yazıldıysa true (alt sınır boş/bozuk VE havuz kipinde ≥ 1 lisans)
    public static Task<bool> WarnIfPoolWithoutMinimumAsync(
        LicenseDbContext db, PersonBalanceOptions options, ILogger logger, CancellationToken ct);
}
// ── Görev 1 ── PersonBalance
public Task<bool> IsPoolModeAsync(Guid licenseId, CancellationToken ct);
// ── Görev 1 ── LicensesCustomerBalanceApplyController (özel)
private Task<(SalesSyncMode Mode, bool Pool)?> OwnedLicenseModeAsync(Guid licenseId, CancellationToken ct);
// ── Görev 1 ── test: TestHelpers/PersonBalanceApiFactories.cs (namespace OrderDeck.LicenseServer.Tests.TestHelpers)
public sealed class PersonBalanceConfiguredApiFactory : ApiFactory          // InMemory
{   public const string MinClientVersion = "0.9.11";                        // PersonBalance: ve SalesSync: ikisi de
    public LogRecorderProvider Log { get; } }
public sealed class PersonBalanceRelationalApiFactory : RelationalApiFactory // SQL Server
{   public PersonBalanceRelationalApiFactory(string connectionString);
    public RaceSaveHook Hook { get; }  public ReadQueryHook Queries { get; }  public LogRecorderProvider Log { get; } }
// ── Görev 1 ── test: PersonBalanceTestKit eklemeleri
public const string PoolUserAgent = SalesCommandTestKit.WpfUserAgent;      // "OrderDeck-WPF/0.9.11"
public const string OldUserAgent = "OrderDeck-WPF/0.9.10";
public Task EnablePoolAsync(DateTimeOffset? since = null);
public Task DisablePoolAsync();
public Task<HttpResponseMessage> PreviewAsync(Guid row, IReadOnlyCollection<Guid>? memberIds,
    string? userAgent = SalesCommandTestKit.WpfUserAgent);
public Task<HttpResponseMessage> ApplyAsync(Guid row, decimal amount, decimal productTotal, Guid? key, string? saleScope,
    IReadOnlyCollection<Guid>? memberIds, string? userAgent = SalesCommandTestKit.WpfUserAgent);
public Task<HttpResponseMessage> PanelManualAdjustmentAsync(Guid row, decimal amount, string reason);
public Task<List<CustomerBalanceTransaction>> GroupRowsAsync(Guid anchorId);

// ── Görev 2 ── PersonBalance
public const string CodeNoBalance = "no-balance";
public const string CodeCustomerNotSynced = "customer-not-synced";
public PersonBalance(LicenseDbContext db, CustomerIdResolver customerIds, ILogger<PersonBalance> log);
public sealed record DrawSet(Guid AnchorId, Person Person, IReadOnlyList<Member> Draw, IReadOnlyList<Member> LockedNegatives)
{ public decimal Pool => Draw.Sum(m => m.Balance) + LockedNegatives.Sum(m => m.Balance); }
public sealed record DrawSetRejection(string Code, IReadOnlyList<Guid> RowIds);
public sealed record DrawSetResult(DrawSet? Set, DrawSetRejection? Rejection);
public Task<DrawSetResult> ResolveDrawSetAsync(Guid licenseId, Guid anchorId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);
public Task<DrawSet> PersonDrawSetAsync(Guid licenseId, Guid anchorId, CancellationToken ct);
public Task<decimal> PoolBalanceAsync(Guid licenseId, Guid anchorId, IReadOnlyCollection<Guid>? memberIds, CancellationToken ct);

// Draw = P (WPF: (W ∩ S) canlı ∪ S silinmiş; panel: S'nin hepsi); LockedNegatives = S∖P'nin Balance < 0 üyeleri
// (panelde boş). Rejection.Code ∈ {CodeNoBalance, CodeCustomerNotSynced}; RowIds: customer-not-synced'e yol açan satırlar.
// PoolBalanceAsync: oynatma için 409 kuralları uygulanmadan max(0, Σ); memberIds null/boş → Σ_S.

// ── Görev 3 ── PersonBalance
// Amount ≥ 0: o satırdan çekilen (yazıcı −Amount yazar). İlk eleman HER ZAMAN çapa (0 olabilir); sonra silinmiş, sonra
// canlı üyeler, bakiye büyükten küçüğe, eşitlikte Id "N" ordinal; ≤ 0 bakiyeli üye ve 0 çekimli çapa dışı üye listede yok.
public sealed record DrawLeg(Guid WpfCustomerId, decimal Amount);
public static IReadOnlyList<DrawLeg> PlanDraw(Guid anchorId, IReadOnlyList<Member> draw, decimal amount);

// ── Görev 4 ── PersonBalance
public enum DrawAmountRule { UpToPool, AllOrNothing }
public sealed record DrawRequest(
    Guid LicenseId, Guid AnchorRowId, Guid AnchorTransactionId, bool AnchorIdIsIdempotencyKey, string Kind,
    decimal Requested, DrawAmountRule Rule, IReadOnlyCollection<Guid>? MemberIds, decimal? OriginalAmount,
    string? SaleScope, string? Reason, Guid? ReversesTransactionId, Guid CreatedByCustomerId, string? ScopeLockResource);
// Requested > 0: çekilecek tutar (apply: min(Amount, ProductTotal); K5: |tutar|). UpToPool: Σ ≤ 0 → NoBalance,
// applied = min(Requested, Σ) (≤ 0 → NothingToApply). AllOrNothing: max(0, Σ) < Requested → Insufficient (kısmi yok).
// MemberIds != null → ResolveDrawSetAsync (WPF); null → PersonDrawSetAsync (panel, P = S).
// ScopeLockResource != null → her deneme kendi işlemi + SalesCommandLock (SalesCommandRejection çağırana yükselir).
public enum DrawStatus { Drawn, Stopped, NoBalance, NothingToApply, Insufficient, CustomerNotSynced, AlreadyReversed, DuplicateKey, Busy }
// Applied: bütün bacakların toplamı (> 0 yalnız Drawn'da); Pool: planın Σ'sı (izlenen satırlardan, eksiler dahil);
// Legs: yazılan hareket satırı sayısı (çapa dahil; Drawn dışında 0); RejectedRowIds yalnız CustomerNotSynced'te dolu.
public sealed record DrawResult(
    DrawStatus Status, Guid AnchorTransactionId, decimal Applied, decimal Pool, int Legs, IReadOnlyList<Guid> RejectedRowIds);
// beforeAttempt(attempt, ct): kilitten SONRA, okumalardan ÖNCE her denemede; true → hiçbir şey yazılmaz, Stopped.
public Task<DrawResult> DrawAsync(DrawRequest request, Func<int, CancellationToken, Task<bool>>? beforeAttempt, CancellationToken ct);
public static bool IsDuplicateTransactionKey(DbUpdateException ex);   // 2601/2627 + "PK_CustomerBalanceTransactions"

// ── Görev 5 ── LicensesCustomerBalanceApplyController
public LicensesCustomerBalanceApplyController(LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
    IOptions<SalesSyncOptions> salesSync, IOptions<PersonBalanceOptions> personBalance,
    ILogger<LicensesCustomerBalanceApplyController> log);
public async Task<IActionResult> Preview(Guid licenseId, [FromQuery] Guid wpfCustomerId,
    [FromQuery(Name = "memberIds")] Guid[]? memberIds, CancellationToken ct);
private const string CodeMemberIdsRequired = "member-ids-required";
private static bool HasMemberIds(IReadOnlyCollection<Guid>? memberIds);
private bool PoolClientMeetsMinimum();
private ObjectResult PoolClientUpdateRequired(Guid licenseId, string endpoint);
private ObjectResult MemberIdsRequired(Guid licenseId, string endpoint);
private ObjectResult CustomerNotSynced(Guid licenseId, string endpoint, IReadOnlyList<Guid> rowIds);
private Task<IActionResult> PoolPreviewAsync(Guid licenseId, Guid requestId, Guid canonicalId,
    IReadOnlyCollection<Guid> memberIds, CancellationToken ct);

// ── Görev 6 ── LicensesCustomerBalanceApplyController
public sealed record ApplyRequest(Guid WpfCustomerId, decimal Amount, decimal ProductTotal, Guid? IdempotencyKey = null,
    string? SaleScope = null, IReadOnlyList<Guid>? MemberIds = null);
private Task<ScopeResponse?> LatestScopeDeductionAsync(Guid licenseId, Guid canonicalCustomerId, string saleScope,
    bool personPool, CancellationToken ct);
private Task<(ApplyResponse? Replay, bool Foreign, bool ContentConflict)> LookupAsync(Guid licenseId, ApplyRequest req,
    Guid canonicalCustomerId, Guid key, bool pool, CancellationToken ct);

// ── Görev 7 ── LicensesCustomerBalanceApplyController
private Task<IActionResult> ApplyPoolAsync(Guid licenseId, ApplyRequest req, Guid canonicalId, SalesSyncMode mode, CancellationToken ct);
private ObjectResult ScopeHasDeduction(Guid licenseId, ScopeResponse existing);

// ── Görev 8 ── PanelCustomerBalanceController / ShopperBalanceController (özel)
private Task<BalanceDetailsResponse> PoolDetailsAsync(Guid requestId, Guid licenseId, Guid canonicalId, int take, CancellationToken ct); // panel
private Task<BalanceDetailsResponse> PoolDetailsAsync(Guid licenseId, Guid canonicalId, int take, CancellationToken ct);                 // Shopper

// ── Görev 9 ── PanelCustomerBalanceController (özel)
private ObjectResult BalanceBusy(Guid licenseId, string endpoint);
private Task<IActionResult> DrawManualAdjustmentAsync(Guid licenseId, Guid canonicalId, Guid customerId, decimal amount,
    string reason, CancellationToken ct);

// ── Görev 10 ── PanelCustomerBalanceController (özel)
private Task<IActionResult> ReverseDrawGroupAsync(Guid licenseId, Guid canonicalId, Guid customerId, Guid anchorId,
    CustomerBalanceTransaction target, bool pool, CancellationToken ct);
private Task<IActionResult> ReverseInPoolAsync(Guid licenseId, Guid canonicalId, Guid customerId,
    CustomerBalanceTransaction target, CancellationToken ct);

// ── Görev 11 ── Services/CustomerSync/PersonBalanceEnableGate.cs
public static class PersonBalanceEnableGate
{
    public sealed record VerifyCheck(int D4, int D5, int D6, int OrphanLegs, int TrimMismatches, int ControlCharGroups)
    { public bool Passes => D4 + D5 + D6 + OrphanLegs + TrimMismatches + ControlCharGroups == 0; }
    public sealed record BaselineCheck(bool HasMarker, bool HasBaseline, int Unreviewed, int NegativeOutside, int Worsened,
        int LedgerMismatchOutside, int ChangedMismatch, int CopyOutside)
    { public bool Passes => HasBaseline && Unreviewed == 0
          && NegativeOutside + Worsened + LedgerMismatchOutside + ChangedMismatch + CopyOutside == 0; }
    public sealed record SupersetCheck(string? PersonBalanceMin, string? SalesSyncMin, bool Passes);
    public sealed record Result(int WindowDays, SalesModeGate.ClientVersionCheck Clients, VerifyCheck Verify,
        BaselineCheck Baseline, SupersetCheck Superset)
    { public bool Passes => Clients.Passes && Verify.Passes && Baseline.Passes && Superset.Passes;
      // Hesaplanan salt-okunur özellik (otomatik özellik DEĞİL): Passes=false olan koşul numaraları 1..4, artan; her okumada.
      public IReadOnlyList<int> Failing =>
          new[] { (Clients.Passes, 1), (Verify.Passes, 2), (Baseline.Passes, 3), (Superset.Passes, 4) }
              .Where(c => !c.Item1).Select(c => c.Item2).ToList(); }
    public static Task<Result> EvaluateAsync(LicenseDbContext db, Guid licenseId, PersonBalanceOptions personBalance,
        SalesSyncOptions salesSync, DateTimeOffset now, CancellationToken ct);
}

// ── Görev 12 ── Tools/PersonBalanceCommand.cs
public sealed record ToggleArgs(bool Enable, Guid License, bool Apply);
public static bool TryParseToggleArgs(string[] args, [NotNullWhen(true)] out ToggleArgs? parsed);
// Dağıtım (saf; tek kullanıcısı RunAsync(string[])): args[1] enable|disable → TryParseToggleArgs, değilse TryParseArgs.
// true'da çıktılardan tam biri dolu; false'ta ikisi de boş.
public static bool TryParseAnyArgs(string[] args, out VerifyArgs? verify, out ToggleArgs? toggle);
public static Task<int> RunEnableAsync(LicenseDbContext db, ToggleArgs args, PersonBalanceOptions personBalance,
    SalesSyncOptions salesSync, TextWriter output, TextWriter error, CancellationToken ct);
public static Task<int> RunDisableAsync(LicenseDbContext db, ToggleArgs args, TextWriter output, TextWriter error, CancellationToken ct);

// ── Görev 13 ──
public sealed record LicenseRow(Guid Id, DateTimeOffset IssuedAt, DateTimeOffset? PoolSince = null); // PersonBalanceVerifier
public Counter<long> BalanceCustomerNotSynced { get; }      // "orderdeck.balance.customer_not_synced_total"   etiket endpoint
public Counter<long> BalanceClientUpdateRequired { get; }   // "orderdeck.balance.client_update_required_total" etiket gate, endpoint
public Counter<long> BalanceBusy { get; }                   // "orderdeck.balance.busy_total"                   etiket endpoint
public Counter<long> BalanceMultiLegDraws { get; }          // "orderdeck.balance.multi_leg_draws_total"        etiket legs
// endpoint (fark 18h): WPF preview | scope | apply | reverse (scope yalnız client_update_required, gate=sales-sync);
// panel panel/manual-adjustment | panel/reverse.
// WPF denetleyici ctor'u: (…, IOptions<PersonBalanceOptions> personBalance, OrderDeckMetrics metrics, ILogger<…> log)
// Panel denetleyici ctor'u: (LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person, OrderDeckMetrics metrics, ILogger<…> log)
```

## Sahip kararları (29.10.2026)

1. **Preview'da kural 1'in kesin kolu → (b): 200 `Balance = 0`, `PersonPool = true`; apply 409 `no-balance` kalır** (fark 19, Görev 5). Spec §4.0-1 preview ve apply için "409 `no-balance` (kesin)" der; amacı "yeni açılmış, henüz gönderilmemiş müşteri bekletilmez". Ama bugünkü WPF (master `OrderDeck.App/Services/PaymentRequestService.cs:1397-1408`) preview'ın her hatasını genel `catch`'le yutup mesajı engeller; `MarkNoBalance` yalnız önizleme bakiyesi ≤ 0 iken yazılır (`:1409-1420`). 409 ile tam da bekletilmemesi gereken müşteri, sahip satırı sunucuya ulaşana kadar her denemede bekletilirdi. Gerekçe: kuralı istemcinin eşlemesine bırakmayan çözüm — satır kipinin "satır yoksa 0" emsali (`:127`), hem bugünkü hem W-havuz WPF istemci değişikliği olmadan doğru davranır, spec §6.1-b'ye eşleme eklemek gerekmez; anlamca da doğru (hiçbir üye sunucuda yok → para olamaz → kesin 0). Görev 5'te tek dal ve bir test değişti; spec §4.0-1, §8.3 ve §10 (K7) planla aynı commit'te güncellendi.
2. **S1 planı fark 22 (ve 20) → (a): S2'de değişiklik yok.** Soru, tabandaki bir D1 satırı düzeltilirse ya da tabana alınmamış yeni sapma doğarsa `enable`'ın 3. koşulunun (spec §7.2-3, fark 15) o lisansı kalıcı reddedeceğiydi. Gerekçe: prod tabanı 29.10'da incelendi; 4 lisansta da taban yalnız işaret satırından ibaret (eksi/D1/kopya 0), düzeltilecek taban satırı yok — soru konusuz. `enable` günlük uyarıyla aynı kuralı uygular (Görev 11'in varsayılanı aynen).

---

## Görev 1: `PersonBalanceOptions`, açılış uyarısı, kip okuması, test kiti ve fabrikalar (spec §4.0 "Kip", §7.2, M1)

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceOptions.cs`, `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceStartup.cs`
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (DI: `SalesSyncOptions` kaydının `:184-187` hemen ardına; açılış uyarısı: göç kapsamı `:963-971`, `SeedAdminAsync` `:970`'ten sonra), `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` (kişi bölümünün sonu: `MembersAsync` `:51-70`'ten sonra, "Çekim grupları" `:72`'den önce `IsPoolModeAsync`), `OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs` (`OwnedLicenseModeAsync` `:708-717` → `(Mode, Pool)?`; dört çağıran: preview `:114-117`, scope `:183-188`, apply `:341-345`, reverse `:662-665`)
- Test (oluştur): `OrderDeck.LicenseServer.Tests/TestHelpers/PersonBalanceApiFactories.cs`, `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceOptionsTests.cs`, `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceStartupTests.cs`, `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceModeTests.cs`
- Test (genişlet): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceTestKit.cs` (sabitler `:17-20` ardına; kurulum `:151`'den sonra; okuma `:190-196` ardına; istek `:203-204`, `:215-217`, `:225-227` ardına — var olan üyeler AYNEN)

**Not (spec ↔ kod):** İskelet farkı 6 bu görevde uygulanır. Spec §7.2 ayarları tanımlar ama kodda `PersonBalance:*` bölümü yok; `SalesSyncOptions` kalıbıyla yeni `PersonBalanceOptions` gelir. Compose satırı (yalnız `PersonBalance__MinClientVersion`) Görev 17'de; `ActivePcWindowDays` compose'a girmez, prod'da kod varsayılanı 14 (§7.2-1). `verify`'ın 9. maddesi S1'in sabit penceresinde (`PersonBalanceVerifier.ActivePcWindow`, `:31`) kalır: §7.3-9 pencere tanımlamaz, ayar yalnız `enable`'ın 1. koşulu içindir (Görev 11–12). Spec'in "havuz kipinde anahtar boşsa günlüğe hata" cümlesi istek anının kuralıdır (Görev 5, iskelet 18j); bu görev yalnız açılış kolunu getirir: alt sınır boş/bozuk ∧ havuz kipinde ≥ 1 lisans → tek **Warning**.

**Uygulama notu:** Açılış uyarısı göç kapsamında `IOptions<PersonBalanceOptions>.Value`'yu okur. Bu, `ValidateOnStart`'tan ÖNCE doğrulamayı tetikler; bozuk pencere (`0`) sunucuyu yine durdurur, istisna (`OptionsValidationException`) yalnız daha erken atılır. `SalesSyncOptions` için aynı durum bugün `:987-988`'de var; test istisnayı iç içe istisnalarda arar (`ForceEndSilenceOptionsTests` deseni).

Bu görevde **hiçbir uç `Pool`'u kullanmaz**: kip okunur ama dallanma yok (Görev 5–10). Bayrak boş ya da dolu, her ucun davranışı S1'deki gibidir; satır kipi regresyonu bunu kanıtlar. Bayrak açıkken bir ucun davranışını iddia eden test bu görevde YAZILMAZ.

- [ ] **Adım 1: Fabrikalar ve ayar testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/TestHelpers/PersonBalanceApiFactories.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Ortak bakiye (spec 2026-10-11) testlerinin InMemory fabrikası: <c>PersonBalance:MinClientVersion</c> (Wh) ve
/// <c>SalesSync:MinClientVersion</c> (Ws) dolu ve eşit — Wh ⊇ Ws (§7.2-4). Kitin varsayılan User-Agent'ı
/// (<c>SalesCommandTestKit.WpfUserAgent</c>, 0.9.11) iki kapıyı da geçer. Uygulamanın günlükleri <see cref="Log"/>'da.
/// </summary>
public sealed class PersonBalanceConfiguredApiFactory : ApiFactory
{
    /// <summary>Testte Wh = Ws.</summary>
    public const string MinClientVersion = "0.9.11";

    public LogRecorderProvider Log { get; } = new();

    protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
    {
        ["PersonBalance:MinClientVersion"] = MinClientVersion,
        ["SalesSync:MinClientVersion"] = MinClientVersion,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(Log));
    }
}

/// <summary>
/// <see cref="PersonBalanceConfiguredApiFactory"/>'nin gerçek SQL Server hâli (Testcontainers): aynı ayarlar + kayıt
/// kancası (<see cref="RaceSaveHook"/>), okuma kancası (<see cref="ReadQueryHook"/>) ve günlük toplayıcı. Spec §8.2'nin
/// havuz kipi yarışları bu fabrikayla kurulur. Kancalar kurulmadıkça etkisizdir.
/// </summary>
public sealed class PersonBalanceRelationalApiFactory : RelationalApiFactory
{
    public PersonBalanceRelationalApiFactory(string connectionString) : base(connectionString) { }

    public RaceSaveHook Hook { get; } = new();

    public ReadQueryHook Queries { get; } = new();

    public LogRecorderProvider Log { get; } = new();

    protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
    {
        ["PersonBalance:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
        ["SalesSync:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
    };

    protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook, Queries);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(Log));
    }
}
```

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceOptionsTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <c>PersonBalance</c> ayarları — spec 2026-10-11 §7.2, M1, §8.3. Alt sınır (Wh) koda gömülmez, yapılandırmadan
/// okunur ve satış senkronunun <see cref="ClientVersion"/> yardımcısıyla ayrıştırılır (yeni ayrıştırıcı yok, §15-3);
/// boş ya da bozuk değer tanımsızdır (kapalı-hata). Etkin PC penceresi varsayılan 14 gün; 0 ya da eksiyle sunucu açılmaz.
/// </summary>
public sealed class PersonBalanceOptionsTests : IClassFixture<PersonBalanceConfiguredApiFactory>
{
    private readonly PersonBalanceConfiguredApiFactory _factory;

    public PersonBalanceOptionsTests(PersonBalanceConfiguredApiFactory factory) => _factory = factory;

    /// <summary>§7.2-1: pencere varsayılanı 14 gün; alt sınır verilmemişse tanımsız.</summary>
    [Fact]
    public void Varsayilan_pencere_14_gun_alt_sinir_tanimsiz()
    {
        var options = new PersonBalanceOptions();

        options.ActivePcWindowDays.Should().Be(14);
        options.ActivePcWindow.Should().Be(TimeSpan.FromDays(14));
        options.ParseMinClientVersion().Should().BeNull("alt sınır yazılmadan havuz kipi kapalı-hata çalışır");
    }

    /// <summary>§7.2: "+" sonrası atılır, "-" sonrası ön sürüm eki; boş, boşluk ve ayrıştırılamayan değer tanımsız.</summary>
    [Theory]
    [InlineData("0.9.11", "0.9.11.0", null)]
    [InlineData("0.9.11+abc", "0.9.11.0", null)]
    [InlineData("0.9.11-rc1", "0.9.11.0", "rc1")]
    [InlineData("", null, null)]
    [InlineData("  ", null, null)]
    [InlineData("abc", null, null)]
    public void Alt_sinir_ClientVersion_ile_ayristirilir(string raw, string? number, string? preRelease)
    {
        var parsed = new PersonBalanceOptions { MinClientVersion = raw }.ParseMinClientVersion();

        if (number is null)
        {
            parsed.Should().BeNull("boş ya da ayrıştırılamayan alt sınır tanımsızdır (kapalı-hata, §7.2)");
            return;
        }
        parsed.Should().NotBeNull();
        parsed!.Number.Should().Be(Version.Parse(number));
        parsed.PreRelease.Should().Be(preRelease);
    }

    /// <summary>§8.3: karşılaştırma metin değil <see cref="System.Version"/> — "0.9.10", "0.9.9" alt sınırını geçer.</summary>
    [Fact]
    public void Alt_sinir_System_Version_ile_karsilastirilir()
    {
        var min = new PersonBalanceOptions { MinClientVersion = "0.9.9" }.ParseMinClientVersion();

        ClientVersion.IsAtLeast(ClientVersion.Parse("0.9.10"), min)
            .Should().BeTrue("metin sırası 0.9.10'u 0.9.9'dan küçük bulurdu");
        ClientVersion.IsAtLeast(ClientVersion.Parse("0.9.8"), min).Should().BeFalse();
    }

    /// <summary>§7.2: alt sınır <c>PersonBalance</c> bölümünden bağlanır; pencere compose'da yok, kod varsayılanı.</summary>
    [Fact]
    public void Yapilandirmadan_baglanir()
    {
        var options = _factory.Services.GetRequiredService<IOptions<PersonBalanceOptions>>().Value;

        options.MinClientVersion.Should().Be(PersonBalanceConfiguredApiFactory.MinClientVersion);
        options.ActivePcWindowDays.Should().Be(PersonBalanceOptions.DefaultActivePcWindowDays,
            "compose pencereyi vermez; prod'da kod varsayılanı geçerli");
    }

    /// <summary>0 ya da eksi pencere hiçbir PC'yi etkin saymaz ve <c>enable</c>'ın 1. koşulunu boşuna geçirirdi
    /// (§7.2-1): sunucu açılmaz.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void Sifir_pencere_acilisi_durdurur(string days)
    {
        using var factory = new WindowApiFactory(days);

        // İstemci oluşturmak host'u kurar ve başlatır.
        var act = () => factory.CreateClient();

        var failure = act.Should().Throw<Exception>().Which;
        FindOptionsValidationException(failure)
            .Should().NotBeNull("pencere ValidateOnStart ile bağlı olmalı")
            .And.Subject.As<OptionsValidationException>()
            .Failures.Should().ContainMatch("*ActivePcWindowDays*");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary><see cref="ApiFactory"/> + <c>PersonBalance:ActivePcWindowDays</c>.</summary>
    public sealed class WindowApiFactory(string days) : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["PersonBalance:ActivePcWindowDays"] = days,
        };
    }

    /// <summary>Doğrulama hatası doğrudan ya da <c>AggregateException</c> içinde gelebilir
    /// (<c>ForceEndSilenceOptionsTests</c> deseni).</summary>
    private static OptionsValidationException? FindOptionsValidationException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is OptionsValidationException ove) return ove;
            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    var found = FindOptionsValidationException(inner);
                    if (found is not null) return found;
                }
                return null;
            }
            ex = ex.InnerException;
        }
        return null;
    }
}
```

- [ ] **Adım 2: Açılış uyarısı testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceStartupTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Açılış uyarısı — spec 2026-10-11 §7.2: "Açılışta anahtar boşken havuz kipinde lisans varsa uyarı günlüğe yazılır."
/// Alt sınır boş ya da ayrıştırılamıyorsa VE en az bir lisans havuz kipindeyse TEK uyarı (o lisanslarda preview ve yeni
/// yazım 409 <c>client-update-required</c> döner); aksi hâlde sessiz. InMemory yeterli: tek sayım sorgusu. Son test
/// <c>Program.cs</c> bağlantısını uçtan uca sınar (göçten sonra, bölüme bağlı ayarla çağrılıyor mu).
/// </summary>
public sealed class PersonBalanceStartupTests
{
    private static LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"pb-startup-{Guid.NewGuid():N}").Options);

    /// <summary>Kiracısı ve anahtarı üretilmiş bir lisans; <paramref name="pool"/> ise havuz kipinde.</summary>
    private static async Task SeedLicenseAsync(LicenseDbContext db, bool pool)
    {
        var now = DateTimeOffset.UtcNow;
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            LicenseKey = $"lic-{Guid.NewGuid():N}",
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
            PersonBalancePoolSince = pool ? now.AddDays(-1) : null,
        });
        await db.SaveChangesAsync();
    }

    private static Task<bool> WarnAsync(
        LicenseDbContext db, string? minClientVersion, LogRecorder<PersonBalanceStartupTests> log) =>
        PersonBalanceStartup.WarnIfPoolWithoutMinimumAsync(
            db, new PersonBalanceOptions { MinClientVersion = minClientVersion }, log, CancellationToken.None);

    [Fact]
    public async Task Alt_sinir_tanimsiz_ve_havuz_kipinde_lisans_varsa_tek_uyari()
    {
        using var db = NewDb();
        await SeedLicenseAsync(db, pool: true);
        await SeedLicenseAsync(db, pool: true);
        await SeedLicenseAsync(db, pool: false);
        var log = new LogRecorder<PersonBalanceStartupTests>();

        var warned = await WarnAsync(db, null, log);

        warned.Should().BeTrue();
        var entry = log.Entries.Should().ContainSingle("açılışta tek uyarı").Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("havuz kipinde 2 lisans", "satır kipindeki lisans sayılmaz")
            .And.Contain("PersonBalance__MinClientVersion", "uyarı düzeltme yolunu söyler");
    }

    [Fact]
    public async Task Alt_sinir_bozuksa_tanimsiz_sayilir()
    {
        using var db = NewDb();
        await SeedLicenseAsync(db, pool: true);
        var log = new LogRecorder<PersonBalanceStartupTests>();

        var warned = await WarnAsync(db, "abc", log);

        warned.Should().BeTrue("ayrıştırılamayan alt sınır tanımsızdır (§7.2)");
        log.Entries.Should().ContainSingle().Which.Message.Should().Contain("havuz kipinde 1 lisans");
    }

    [Fact]
    public async Task Havuz_kipinde_lisans_yoksa_uyari_yok()
    {
        using var db = NewDb();
        await SeedLicenseAsync(db, pool: false);
        var log = new LogRecorder<PersonBalanceStartupTests>();

        var warned = await WarnAsync(db, null, log);

        warned.Should().BeFalse("S2 deploy'unda hiçbir lisans havuz kipinde değil; boş alt sınır olağan durum");
        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Alt_sinir_doluysa_uyari_yok()
    {
        using var db = NewDb();
        await SeedLicenseAsync(db, pool: true);
        var log = new LogRecorder<PersonBalanceStartupTests>();

        (await WarnAsync(db, PersonBalanceConfiguredApiFactory.MinClientVersion, log)).Should().BeFalse();

        // Alt sınır doluyken veritabanına hiç gidilmez: kapalı bağlam sorguda atardı.
        var closed = NewDb();
        closed.Dispose();
        (await WarnAsync(closed, PersonBalanceConfiguredApiFactory.MinClientVersion, log))
            .Should().BeFalse("alt sınır dolu → sayım sorgusu koşmaz");
        log.Entries.Should().BeEmpty();
    }

    /// <summary>
    /// <c>Program.cs</c> bağlantısı: host açılırken uyarı göçten SONRA, <c>PersonBalance</c> bölümüne bağlı ayarla
    /// yazılır. Çağrı unutulur, <c>EnsureCreated</c>'dan önceye taşınır ya da başka bir ayar örneği geçirilirse kırmızı.
    /// </summary>
    [Fact]
    public void Program_acilista_havuz_kipindeki_lisans_icin_uyari_yazar()
    {
        using var factory = new PoolAtStartupFactory();

        // İstemci oluşturmak host'u kurar ve Program'ın göç kapsamını koşturur.
        using var client = factory.CreateClient();

        factory.Log.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning && e.Message.Contains("havuz kipinde 1 lisans", StringComparison.Ordinal),
            "alt sınır boş ve havuz kipinde bir lisans var (§7.2)");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>
    /// Alt sınırı boş, açılıştan önce havuz kipinde bir lisansı olan fabrika. Lisans <c>UseSeeding</c> ile yazılır:
    /// EF 10'da Program'ın (eşzamanlı) <c>EnsureCreated</c>'ı tohumlayıcıyı çağırır, yani lisans uyarıdan ÖNCE
    /// depodadır. Eşzamanlı <c>UseSeeding</c> şart — yalnız <c>UseAsyncSeeding</c> verilseydi eşzamanlı
    /// <c>EnsureCreated</c> atardı.
    /// </summary>
    private sealed class PoolAtStartupFactory : ApiFactory
    {
        public LogRecorderProvider Log { get; } = new();

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["PersonBalance:MinClientVersion"] = "",
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) =>
            opt.UseSeeding((ctx, _) =>
            {
                ctx.Set<License>().Add(new License
                {
                    Id = Guid.NewGuid(),
                    CustomerId = Guid.NewGuid(),
                    LicenseKey = $"lic-{Guid.NewGuid():N}",
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                    PersonBalancePoolSince = DateTimeOffset.UtcNow.AddDays(-1),
                });
                ctx.SaveChanges();
            });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(l => l.AddProvider(Log));
        }
    }
}
```

- [ ] **Adım 3: Test kitine havuz yardımcıları + kip okuması testleri (kırmızı)**

`PersonBalanceTestKit.cs` — sabitlerin (`:17-20`) ardına, `KindReversal` satırından sonra:

```csharp
    public const string KindReversal = "reversal";
```

şununla değişir:

```csharp
    public const string KindReversal = "reversal";

    /// <summary>Havuz kipinin alt sınırını (Wh, <see cref="PersonBalanceConfiguredApiFactory.MinClientVersion"/>) geçen
    /// istemci; satış senkronu kapısını (Ws) da geçer.</summary>
    public const string PoolUserAgent = SalesCommandTestKit.WpfUserAgent;

    /// <summary>Wh'den eski istemci: havuz kipinde preview ve yeni yazım 409 <c>client-update-required</c> alır.
    /// Havuz kapısını kanıtlayan testler kiti SalesSyncMode.Off ile kurar (<see cref="CreateAsync"/>'in varsayılanı);
    /// mod On'da 409 satış senkronu kapısından da gelir (bu sürüm Ws'yi de geçemez) ve havuz kapısını kanıtlamaz.</summary>
    public const string OldUserAgent = "OrderDeck-WPF/0.9.10";
```

Kurulum bölümünün sonuna (`SeedLegacyDeductionAsync` `:136-151`'den sonra), şu satırın ÖNÜNE:

```csharp
    /// <summary>Spec Örnek 1'in sonu.</summary>
```

ekle (satırın kendisi aynen kalır):

```csharp
    /// <summary>
    /// Lisansı havuz kipine alır (spec §3.1, §7.2): <c>PersonBalancePoolSince</c> = <paramref name="since"/> (verilmezse
    /// bir dakika önce). Prod'da bayrağı yalnız <c>person-balance enable --apply</c> yazar; testte kapı koşulları atlanır.
    /// </summary>
    public Task EnablePoolAsync(DateTimeOffset? since = null) =>
        SetPoolSinceAsync(since ?? DateTimeOffset.UtcNow.AddMinutes(-1));

    /// <summary>Lisansı satır kipine indirir (<c>disable</c>, kill switch).</summary>
    public Task DisablePoolAsync() => SetPoolSinceAsync(null);

    private Task SetPoolSinceAsync(DateTimeOffset? value) =>
        Sales.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == LicenseId);
            license.PersonBalancePoolSince = value;
        });

```

Okuma bölümünde (`ReversalsOfAsync` `:190-196`'dan sonra), şu satırın ÖNÜNE:

```csharp
    public Task<WpfCustomerProjection> ProjectionAsync(Guid id) =>
```

ekle:

```csharp
    /// <summary>
    /// Bir çekim grubunun bütün satırları (spec §3.2): çapa (<c>Id == anchorId</c>) ilk, sonra bacaklar satır Id'sinin
    /// "N" metnine göre ordinal. Eski tek satırlık hareket (<c>DrawGroupId</c> NULL) de çapa olarak döner.
    /// </summary>
    public async Task<List<CustomerBalanceTransaction>> GroupRowsAsync(Guid anchorId)
    {
        var rows = await Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.Id == anchorId || t.DrawGroupId == anchorId)
            .ToListAsync());
        return rows
            .OrderBy(t => t.Id == anchorId ? 0 : 1)
            .ThenBy(t => t.WpfCustomerId.ToString("N"), StringComparer.Ordinal)
            .ToList();
    }

```

İstek bölümü — var olan `PreviewAsync(Guid)` (`:203-204`):

```csharp
    public Task<HttpResponseMessage> PreviewAsync(Guid row) =>
        SendAsync(HttpMethod.Get, $"{WpfUrl}/preview?wpfCustomerId={row}");
```

şununla değişir (var olan aynen + yeni aşırı yükleme; yeninin `memberIds`'i varsayılansız → tek argümanlı çağrı belirsiz olmaz):

```csharp
    public Task<HttpResponseMessage> PreviewAsync(Guid row) =>
        SendAsync(HttpMethod.Get, $"{WpfUrl}/preview?wpfCustomerId={row}");

    /// <summary>Havuz kipi preview'ı (spec §4.0, §4.1): her üye Id'si tekrarlı <c>memberIds</c> sorgu parametresi
    /// (iskelet 18f); <paramref name="memberIds"/> null → parametre hiç gönderilmez.</summary>
    public Task<HttpResponseMessage> PreviewAsync(
        Guid row, IReadOnlyCollection<Guid>? memberIds, string? userAgent = SalesCommandTestKit.WpfUserAgent)
    {
        var url = $"{WpfUrl}/preview?wpfCustomerId={row}";
        if (memberIds is not null) url += string.Concat(memberIds.Select(id => $"&memberIds={id}"));
        return SendAsync(HttpMethod.Get, url, userAgent: userAgent);
    }
```

Var olan 5 parametreli `ApplyAsync` (`:215-217`):

```csharp
    public Task<HttpResponseMessage> ApplyAsync(Guid row, decimal amount, decimal productTotal, Guid? key, string? saleScope) =>
        SendAsync(HttpMethod.Post, $"{WpfUrl}/apply",
            new { wpfCustomerId = row, amount, productTotal, idempotencyKey = key, saleScope });
```

şununla değişir:

```csharp
    public Task<HttpResponseMessage> ApplyAsync(Guid row, decimal amount, decimal productTotal, Guid? key, string? saleScope) =>
        SendAsync(HttpMethod.Post, $"{WpfUrl}/apply",
            new { wpfCustomerId = row, amount, productTotal, idempotencyKey = key, saleScope });

    /// <summary>Havuz kipi apply'ı (spec §4.0, §4.2): gövdede <c>memberIds</c> (iskelet 18f; null → JSON'da null).
    /// 5 argümanlı çağrı var olan aşırı yüklemeye gider; <paramref name="memberIds"/> varsayılansız.</summary>
    public Task<HttpResponseMessage> ApplyAsync(
        Guid row, decimal amount, decimal productTotal, Guid? key, string? saleScope,
        IReadOnlyCollection<Guid>? memberIds, string? userAgent = SalesCommandTestKit.WpfUserAgent) =>
        SendAsync(HttpMethod.Post, $"{WpfUrl}/apply",
            new { wpfCustomerId = row, amount, productTotal, idempotencyKey = key, saleScope, memberIds }, userAgent);
```

Var olan `PanelRefundAsync` (`:225-227`):

```csharp
    public Task<HttpResponseMessage> PanelRefundAsync(Guid row, decimal amount) =>
        SendAsync(HttpMethod.Post, $"/api/panel/customers/{row}/balance/refund-full",
            new { amount, reason = (string?)null }, userAgent: null);
```

şununla değişir:

```csharp
    public Task<HttpResponseMessage> PanelRefundAsync(Guid row, decimal amount) =>
        SendAsync(HttpMethod.Post, $"/api/panel/customers/{row}/balance/refund-full",
            new { amount, reason = (string?)null }, userAgent: null);

    /// <summary>Panel elle ayarı (<c>ManualAdjustmentRequest(Amount, Reason)</c>); eksi tutar K5'in çekimi (spec §4.13).</summary>
    public Task<HttpResponseMessage> PanelManualAdjustmentAsync(Guid row, decimal amount, string reason) =>
        SendAsync(HttpMethod.Post, $"/api/panel/customers/{row}/balance/manual-adjustment",
            new { amount, reason }, userAgent: null);
```

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceModeTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Kip okuması — spec 2026-10-11 §4.0 "Kip", §3.1. Kip lisans başına, her istekte <c>License.PersonBalancePoolSince</c>'ten
/// okunur: boş = satır kipi. Bilinmeyen lisans satır kipidir; başka lisansın bayrağı bu lisansı etkilemez. Kitin
/// <see cref="Kit.EnablePoolAsync"/> / <see cref="Kit.DisablePoolAsync"/> yardımcılarını da sınar (sonraki görevler havuz
/// kipini bunlarla açar).
/// </summary>
public sealed class PersonBalanceModeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceModeTests(ApiFactory factory) => _factory = factory;

    private async Task<bool> IsPoolAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PersonBalance>()
            .IsPoolModeAsync(licenseId, CancellationToken.None);
    }

    private Task<DateTimeOffset?> PoolSinceAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.Licenses.AsNoTracking()
            .Where(l => l.Id == kit.LicenseId)
            .Select(l => l.PersonBalancePoolSince)
            .SingleAsync());

    [Fact]
    public async Task Bayrak_bos_lisans_satir_kipinde()
    {
        var kit = await Kit.CreateAsync(_factory);

        (await IsPoolAsync(kit.LicenseId)).Should().BeFalse("S2 deploy'unda hiçbir lisans havuz kipinde değil (§7.1)");
        (await kit.Sales.ReadDbAsync(db => db.Licenses.AsNoTracking()
                .Where(l => l.Id == kit.LicenseId)
                .Select(l => l.SalesSyncMode)
                .SingleAsync()))
            .Should().Be(SalesSyncMode.Off,
                "kitin varsayılanı Off: havuz kapısının 409'u satış senkronu kapısının 409'uyla karışmaz (OldUserAgent özeti)");
    }

    [Fact]
    public async Task EnablePool_sonrasi_havuz_DisablePool_sonrasi_satir_kipi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var since = DateTimeOffset.UtcNow.AddHours(-2);

        await kit.EnablePoolAsync(since);

        (await IsPoolAsync(kit.LicenseId)).Should().BeTrue();
        (await PoolSinceAsync(kit)).Should().Be(since);

        await kit.DisablePoolAsync();

        (await IsPoolAsync(kit.LicenseId)).Should().BeFalse("disable kill switch'tir: lisans satır kipine iner (§7.2)");
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    [Fact]
    public async Task Baska_lisansin_bayragi_bu_lisansi_etkilemez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var other = await kit.Sales.SecondLicenseAsync(SalesSyncMode.Off);
        await other.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == other.LicenseId);
            license.PersonBalancePoolSince = DateTimeOffset.UtcNow.AddMinutes(-1);
        });

        (await IsPoolAsync(other.LicenseId)).Should().BeTrue();
        (await IsPoolAsync(kit.LicenseId)).Should().BeFalse("kip lisans başına (§4.0, M1)");
    }

    [Fact]
    public async Task Bilinmeyen_lisans_satir_kipinde()
    {
        (await IsPoolAsync(Guid.NewGuid())).Should().BeFalse();
    }
}
```

- [ ] **Adım 4: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceOptionsTests|FullyQualifiedName~PersonBalanceStartupTests|FullyQualifiedName~PersonBalanceModeTests"`
Expected: FAIL — derleme: `PersonBalanceOptions` ve `PersonBalanceStartup` türleri yok (CS0246), `PersonBalance.IsPoolModeAsync` yok (CS1061). Derleme düzelince de `Program_acilista_havuz_kipindeki_lisans_icin_uyari_yazar` kırmızı kalır (Program henüz çağırmıyor; çağrı Adım 9'da).

- [ ] **Adım 5: `PersonBalanceOptions`**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceOptions.cs`:

```csharp
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiyenin (kişi havuzu) sunucu ayarları — <c>PersonBalance</c> bölümü (spec 2026-10-11 §7.2, M1). Prod'da
/// compose ortamından yalnız <c>PersonBalance__MinClientVersion</c> gelir; <see cref="ActivePcWindowDays"/> kod
/// varsayılanında kalır.
/// </summary>
public sealed class PersonBalanceOptions
{
    public const string Section = "PersonBalance";

    /// <summary><see cref="ActivePcWindowDays"/>'in varsayılanı (§7.2-1; satış senkronu kapısıyla aynı ölçüt).</summary>
    public const int DefaultActivePcWindowDays = 14;

    /// <summary>
    /// Wh'nin (W-havuz'u taşıyan ilk WPF sürümü, spec §2) numarası; Wh yayımlanınca Burak yazar (§7.1 adım 6). Wh'nin
    /// numarası S2 yazılırken bilinmediği için koda gömülmez; ileride yükseltilebilir. Boş = tanımsız:
    /// <c>person-balance enable --apply</c> reddeder; havuz kipindeki lisansta preview ve yeni yazım yapacak apply 409
    /// <c>client-update-required</c> döner (kapalı-hata, §7.2). Ayrıştırılamayan değer de tanımsız sayılır; bkz.
    /// <see cref="ParseMinClientVersion"/>. Açılışta tanımsızken havuz kipinde lisans varsa tek uyarı
    /// (<see cref="PersonBalanceStartup"/>).
    /// </summary>
    public string? MinClientVersion { get; set; }

    /// <summary>
    /// <c>person-balance enable</c>'ın 1. koşulunun penceresi (§7.2-1): son bu kadar gün içinde görülmüş ve devre dışı
    /// bırakılmamış her etkinleştirmenin sürümü ≥ <see cref="MinClientVersion"/> olmalı. Yalnız orada okunur;
    /// <c>verify</c>'ın 9. maddesi S1'in sabit penceresinde kalır. 0'dan büyük olmalı — sunucu aksi hâlde açılmaz
    /// (0 ya da eksi hiçbir PC'yi etkin saymaz ve 1. koşulu boşuna geçirirdi).
    /// </summary>
    public int ActivePcWindowDays { get; set; } = DefaultActivePcWindowDays;

    /// <summary><see cref="ActivePcWindowDays"/> süre olarak.</summary>
    public TimeSpan ActivePcWindow => TimeSpan.FromDays(ActivePcWindowDays);

    /// <summary>Alt sınırın ayrıştırılmış hâli (satış senkronunun ortak yardımcısı, §15-3); boş ya da
    /// ayrıştırılamıyorsa null (kapı kapalı).</summary>
    public ClientVersion? ParseMinClientVersion() => ClientVersion.Parse(MinClientVersion);
}
```

- [ ] **Adım 6: `PersonBalanceStartup`**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceStartup.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiyenin açılış denetimi (spec 2026-10-11 §7.2): "Açılışta anahtar boşken havuz kipinde lisans varsa uyarı
/// günlüğe yazılır." Sunucu yine açılır; kapı kapalı-hata çalışır: o lisanslarda WPF preview ve apply'ın yeni
/// yazımı 409 client-update-required alır (panel yazımları, WPF scope ve reverse ve kayıtlı anahtarın oynatması
/// kapıdan geçmez, spec §7.2, iskelet fark 4). Uyarı yalnız durumu görünür kılar. Göçten sonra çağrılır (sayım bayrak
/// sütununu okur).
/// </summary>
public static class PersonBalanceStartup
{
    /// <summary>
    /// Alt sınır (<see cref="PersonBalanceOptions.MinClientVersion"/>) boş ya da ayrıştırılamıyorsa VE en az bir lisans
    /// havuz kipindeyse tek uyarı yazar. Alt sınır doluysa veritabanına gitmez.
    /// </summary>
    /// <returns>Uyarı yazıldıysa true.</returns>
    public static async Task<bool> WarnIfPoolWithoutMinimumAsync(
        LicenseDbContext db, PersonBalanceOptions options, ILogger logger, CancellationToken ct)
    {
        if (options.ParseMinClientVersion() is not null) return false;

        var count = await db.Licenses.CountAsync(l => l.PersonBalancePoolSince != null, ct);
        if (count == 0) return false;

        logger.LogWarning(
            "PersonBalance:MinClientVersion tanımsız ya da ayrıştırılamıyor ve havuz kipinde {Count} lisans var — bu lisanslarda preview ve yeni bakiye yazımı 409 client-update-required döner (kapalı-hata, spec 2026-10-11 §7.2). .env'e PersonBalance__MinClientVersion=<Wh> yazıp docker compose up -d license-server",
            count);
        return true;
    }
}
```

- [ ] **Adım 7: `PersonBalance.IsPoolModeAsync`**

`PersonBalance.cs` — `MembersAsync`'in sonu (`:69-70`):

```csharp
        return new Person(canonicalId, head?.GroupId, members);
    }
```

şununla değişir (kişi bölümünün sonu; "Çekim grupları" başlığı `:72` aynen kalır):

```csharp
        return new Person(canonicalId, head?.GroupId, members);
    }

    /// <summary>
    /// Lisans havuz kipinde mi: <c>License.PersonBalancePoolSince</c> dolu (spec §3.1, §7.2). Kip lisans başına, her
    /// istekte okunur (§4.0); panel ve Shopper kullanır, WPF uçları sahiplik sorgusunun ikinci alanından okur.
    /// Bilinmeyen lisans satır kipidir (false).
    /// </summary>
    public Task<bool> IsPoolModeAsync(Guid licenseId, CancellationToken ct) =>
        _db.Licenses.AsNoTracking().AnyAsync(l => l.Id == licenseId && l.PersonBalancePoolSince != null, ct);
```

Sınıf özetinin son satırı (`:15`) da güncellenir; "S1 kip bayrağını OKUMAZ" cümlesi S1 için doğru kalır, aynen durur. Şu satır:

```csharp
/// (genişlet/daralt). <b>S2:</b> <c>ResolveDrawSetAsync</c>, <c>PlanDraw</c>, <c>DrawAsync</c>.</para>
```

şununla değişir:

```csharp
/// (genişlet/daralt). <b>S2:</b> <see cref="IsPoolModeAsync"/> (kip bayrağını okur), <c>ResolveDrawSetAsync</c>,
/// <c>PlanDraw</c>, <c>DrawAsync</c>.</para>
```

- [ ] **Adım 8: Koştur — DI bağlaması ve açılış çağrısı eksik, üç test yöntemi (dört vaka) FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceOptionsTests|FullyQualifiedName~PersonBalanceStartupTests|FullyQualifiedName~PersonBalanceModeTests"`
Expected: FAIL — `Yapilandirmadan_baglanir` (`MinClientVersion` null: `IOptions<PersonBalanceOptions>` bölüme bağlı değil, varsayılan örnek döner), `Sifir_pencere_acilisi_durdurur` iki satırı (istisna atılmadı: doğrulama kayıtlı değil) ve `Program_acilista_havuz_kipindeki_lisans_icin_uyari_yazar` (uyarı satırı yok: `Program.cs` `WarnIfPoolWithoutMinimumAsync`'i henüz çağırmıyor). Öbür testler PASS.

- [ ] **Adım 9: `Program.cs` — DI ve açılış uyarısı**

DI — `SalesSyncOptions` kaydı (`:184-187`):

```csharp
        builder.Services.AddOptions<OrderDeck.LicenseServer.Services.Sync.SalesSyncOptions>()
            .Bind(builder.Configuration.GetSection(OrderDeck.LicenseServer.Services.Sync.SalesSyncOptions.Section))
            .Validate(o => o.ForceEndSilenceMinutes > 0, "SalesSync:ForceEndSilenceMinutes 0'dan büyük olmalı.")
            .ValidateOnStart();
```

şununla değişir (ilk dört satır aynen):

```csharp
        builder.Services.AddOptions<OrderDeck.LicenseServer.Services.Sync.SalesSyncOptions>()
            .Bind(builder.Configuration.GetSection(OrderDeck.LicenseServer.Services.Sync.SalesSyncOptions.Section))
            .Validate(o => o.ForceEndSilenceMinutes > 0, "SalesSync:ForceEndSilenceMinutes 0'dan büyük olmalı.")
            .ValidateOnStart();
        // Ortak bakiye (spec 2026-10-11 §7.2, M1): havuz kipinin istemci alt sınırı (Wh) ve enable'ın etkin PC
        // penceresi. Compose ortamından yalnız PersonBalance__MinClientVersion gelir; pencere kod varsayılanı 14
        // (satış senkronu kapısıyla aynı ölçüt). 0 ya da eksi pencere hiçbir PC'yi etkin saymaz ve enable'ın 1.
        // koşulunu boşuna geçirirdi; sunucu açılmaz, deploy geri alınır.
        builder.Services.AddOptions<OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceOptions>()
            .Bind(builder.Configuration.GetSection(OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceOptions.Section))
            .Validate(o => o.ActivePcWindowDays > 0, "PersonBalance:ActivePcWindowDays 0'dan büyük olmalı.")
            .ValidateOnStart();
```

Açılış uyarısı — göç kapsamı (`:963-971`):

```csharp
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            if (db.Database.IsRelational())
                db.Database.Migrate();
            else
                db.Database.EnsureCreated();
            await SeedAdminAsync(db, app.Configuration);
        }
```

şununla değişir:

```csharp
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            if (db.Database.IsRelational())
                db.Database.Migrate();
            else
                db.Database.EnsureCreated();
            await SeedAdminAsync(db, app.Configuration);
            // Ortak bakiye (spec 2026-10-11 §7.2): alt sınır boş/bozukken havuz kipinde lisans varsa tek uyarı — o
            // lisanslarda preview ve yeni yazım 409 client-update-required döner (kapalı-hata). Göçten SONRA: sayım
            // bayrak sütununu okur. .Value doğrulamayı ValidateOnStart'tan önce tetikler; bozuk pencere burada atar.
            await OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceStartup.WarnIfPoolWithoutMinimumAsync(
                db,
                scope.ServiceProvider
                    .GetRequiredService<IOptions<OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceOptions>>().Value,
                app.Logger,
                CancellationToken.None);
        }
```

(`Microsoft.Extensions.Options` `Program.cs:21`'de zaten içe aktarılı.)

- [ ] **Adım 10: WPF uçlarında kip sahiplik sorgusunun ikinci alanı**

`LicensesCustomerBalanceApplyController.cs` — `OwnedLicenseModeAsync` (`:708-717`):

```csharp
    /// <summary>Lisans çağırana aitse kipi; değilse null (uç 404 döner). Sahiplik
    /// ve kip tek sorguda.</summary>
    private async Task<SalesSyncMode?> OwnedLicenseModeAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        return await _db.Licenses
            .Where(l => l.Id == licenseId && l.CustomerId == callerId)
            .Select(l => (SalesSyncMode?)l.SalesSyncMode)
            .FirstOrDefaultAsync(ct);
    }
```

şununla değişir:

```csharp
    /// <summary>Lisans çağırana aitse satış senkronu kipi ve ortak bakiye kipi (spec 2026-10-11 §4.0: havuz kipi ⇔
    /// <c>PersonBalancePoolSince</c> dolu); değilse null (uç 404 döner). Sahiplik, satış senkronu kipi ve ortak
    /// bakiye kipi tek sorguda; kip her istekte okunur.</summary>
    private async Task<(SalesSyncMode Mode, bool Pool)?> OwnedLicenseModeAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        var row = await _db.Licenses
            .Where(l => l.Id == licenseId && l.CustomerId == callerId)
            .Select(l => new { l.SalesSyncMode, Pool = l.PersonBalancePoolSince != null })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.SalesSyncMode, row.Pool);
    }
```

Dört çağıranda sahiplik satırları değişir, kapı satırları AYNEN kalır (`mode` artık `SalesSyncMode`, null değil; apply'ın `:363`'teki `mode == SalesSyncMode.On` denetimi değişmeden derlenir).

Preview (`:114-117`):

```csharp
        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "preview");
```

şununla değişir:

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, _) = owned.Value;
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "preview");
```

Scope (`:183-188`):

```csharp
        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        // v0.9.10 bulduğu düşümü benimser ve toplam aynıysa önizleme ile
        // apply'ı hiç çağırmadan mesajı gönderir; kapı burada da olmalı.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "scope");
```

şununla değişir:

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, _) = owned.Value;
        // v0.9.10 bulduğu düşümü benimser ve toplam aynıysa önizleme ile
        // apply'ı hiç çağırmadan mesajı gönderir; kapı burada da olmalı.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "scope");
```

Apply (`:341-345`):

```csharp
        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        // §6.5-3: bakiyenin her okumasından (oynatma dahil) önce.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "apply");
```

şununla değişir:

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, _) = owned.Value;
        // §6.5-3: bakiyenin her okumasından (oynatma dahil) önce.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "apply");
```

Reverse (`:662-665`):

```csharp
        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "reverse");
```

şununla değişir:

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, _) = owned.Value;
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "reverse");
```

Preview ve reverse'ün metin blokları aynı olduğundan Edit aracında her birini bir sonraki satırla (`"preview"` / `"reverse"` uç adı) birlikte eşleştir. Bu adımın kırmızı testi yok: saf yeniden düzenleme; satır kipi regresyonu (Adım 11) korur. `Pool` alanını Görev 5–7 kullanır.

- [ ] **Adım 11: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceOptionsTests|FullyQualifiedName~PersonBalanceStartupTests|FullyQualifiedName~PersonBalanceModeTests|FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRuleTests|FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceGroupReversalTests|FullyQualifiedName~PersonBalanceCommandTests|FullyQualifiedName~ForceEndSilenceOptionsTests"`
Expected: hepsi PASS. WPF kümesi değişmeden yeşil: dört uçta sahiplik/404 ve satış senkronu kapısı aynı sırada (`CustomerBalanceScopeRuleTests`'in başka yayıncı 404'ü ve mod On 409'ları dahil). `ForceEndSilenceOptionsTests`: açılıştaki yeni sayım ve ikinci `ValidateOnStart` öbür seçeneklerin açılış doğrulamasını bozmaz.

Docker açıksa (isteğe bağlı, açılıştaki sayım sorgusu gerçek SQL Server'da göçten sonra koşar):
Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceScopeRuleRelationalTests|FullyQualifiedName~PersonGroupHookRelationalTests"`
Expected: PASS.

- [ ] **Adım 12: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceOptions.cs \
  OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceStartup.cs \
  OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs \
  OrderDeck.LicenseServer/Program.cs \
  OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs \
  OrderDeck.LicenseServer.Tests/TestHelpers/PersonBalanceApiFactories.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceTestKit.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceModeTests.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceOptionsTests.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceStartupTests.cs
git commit -F - <<'MSG'
feat(balance): ortak bakiye ayarlarını, açılış uyarısını ve kip okumasını ekle

PersonBalance:MinClientVersion (Wh) koda gömülmez, yapılandırmadan okunur ve
satış senkronunun ClientVersion yardımcısıyla ayrıştırılır; boş ya da bozuk
değer tanımsızdır (kapalı-hata). ActivePcWindowDays varsayılan 14, 0 ya da
eksiyle sunucu açılmaz. Alt sınır tanımsızken havuz kipinde lisans varsa
açılışta tek uyarı (ortak bakiye §7.2, M1).

Kip lisans başına her istekte okunur: panel ve Shopper için
PersonBalance.IsPoolModeAsync, WPF uçlarında sahiplik sorgusunun ikinci alanı.
Hiçbir uç havuz alanını henüz kullanmaz; satır kipinin davranışı değişmez.
Test kitine havuz kipini açan/kapayan ve memberIds taşıyan yardımcılar,
InMemory ve SQL Server fabrikaları eklenir.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 2: `ResolveDrawSetAsync` (kural 1–4), `PersonDrawSetAsync`, `PoolBalanceAsync` (spec §2, §4.0, §4.2, §8.3)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` — alanlar + ctor (`:28-35`); yeni bölüm `// ── Çekim kümesi (§4.0) ──` kişi bölümünün sonuna (`MembersAsync` `:51-70` ve Görev 1'in `IsPoolModeAsync`'inden sonra, `// ── Çekim grupları (§2, §3.2) ──` `:72`'den önce). Sınıf belgesindeki "S2:" cümlesi (`:12-15`) aynen kalır.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDrawSetTests.cs`

`PersonBalance` hiçbir yerde elle kurulmuyor (`new PersonBalance(` yok); yalnız DI'dan (`Program.cs:175`) gelir, `CustomerIdResolver` aynı kapsamda zaten scoped (`Program.cs:173`) — ctor değişikliği kayıt gerektirmez. Bu görev yalnız okur: yazım, kip okuması, 409/400 yanıtları ve günlük satırı yok (Görev 4–5). Yöntemler kip bayrağına bakmaz; çağıran (Görev 4, 5, 6) havuz kipinde çağırır. Yarış iddiası yok (InMemory); üyeliğin iki okuma arasında değişmesi Görev 4'ün ikinci okumasıyla, Testcontainers 6–8 Görev 15'te.

**Not (spec ↔ kod) — bu görev:**
- **İskelet fark 5 uygulanır.** `MembersAsync` "a sunucuda yok"u ayırt etmez (`PersonBalance.cs:57-59`: projeksiyon yoksa `S = {a}`, `Purged = false`). Kural 1 ve 2 için `W ∪ {a}`'nın varlığı ve silinmişliği ayrı, TEK bir sorguyla okunur (`KnownMembersAsync`). Kopya Id'leri önce `CustomerIdResolver.CanonicalOfAsync`'ten (`CustomerIdResolver.cs:25-42`) geçtiği için sorgu süzgeci (`MergedIntoId == null`, `LicenseDbContext.cs:964`) doğru: kopya sözlükte görünmez, onun asıl kaydı görünür. `MembersAsync`, `Member`, `Person`, `PersonMembership` değişmez; yeni üyelik sorgusu yok (§15-5).
- **İskelet fark 18c uygulanır.** `PoolBalanceAsync` oynatmanın `RemainingBalance`'ı içindir (§4.2 "kayıtlı anahtar hiçbir zaman `customer-not-synced` almaz"): kural 1 ve 3 uygulanmaz, `max(0, Σ)`; `memberIds` yok/boşsa `Σ_S`.
- **Ön koşul (çağıranın):** `anchorId` asıl kayıt olmalı (spec §2 "`a` = istekteki Id'nin asıl kaydı"; uçlar bugün de `CanonicalOfAsync`'ten geçirir). Kopya Id'siyle çağrılırsa süzgeç onu gizler ve kural 1 `customer-not-synced` döner — sessizce yanlış küme kurulmaz.
- **Silinmiş `a`:** spec kural 1'i "projeksiyonu sunucuda yoksa" diye tanımlar; silinmiş `a`'nın projeksiyonu vardır → kural 1 tetiklenmez. `a` `W`'nin silinmişi olarak yok sayılır, `S`'nin silinmişi olarak `Draw`'a girer (K3). Spec'le aynı; yeni davranış değil.
- **Uzun kopya zinciri:** `CanonicalOfAsync` en çok 3 adım izler (`MaxHops`); daha uzun zincirin ucu kopya kalır, süzgeç gizler ve kural 2 onu atar. Birleştirme işi zinciri düzleştirir (`CustomerIdResolver.cs:11-13`); kod değişikliği gerekmez.
- Sahip sorusu adayı: yok.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDrawSetTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Çekim kümesi P ve 409 kuralları — spec 2026-10-11 §4.0 kural 1–4, §8.3. W = memberIds ∪ {a}, asıl kayda
/// çözülmüş; W'nin yalnız canlıları kullanılır, silinmişler S'den alınır (K3). Panelin kümesi P = S (K5); oynatmanın
/// havuzu 409 kuralları uygulanmadan max(0, Σ). Yalnız okuma: yarış iddiası yok (InMemory).
/// </summary>
public sealed class PersonBalanceDrawSetTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceDrawSetTests(ApiFactory factory) => _factory = factory;

    private async Task<T> WithServiceAsync<T>(Func<PersonBalance, Task<T>> body)
    {
        using var scope = _factory.Services.CreateScope();
        return await body(scope.ServiceProvider.GetRequiredService<PersonBalance>());
    }

    private Task<PersonBalance.DrawSetResult> Resolve(Kit kit, Guid a, params Guid[] w) =>
        WithServiceAsync(s => s.ResolveDrawSetAsync(kit.LicenseId, a, w, default));

    private Task<decimal> Pool(Kit kit, Guid a, IReadOnlyCollection<Guid>? w) =>
        WithServiceAsync(s => s.PoolBalanceAsync(kit.LicenseId, a, w, default));

    private static PersonBalance.DrawSet Accepted(PersonBalance.DrawSetResult result)
    {
        result.Rejection.Should().BeNull();
        result.Set.Should().NotBeNull();
        return result.Set!;
    }

    private static PersonBalance.DrawSetRejection Rejected(PersonBalance.DrawSetResult result)
    {
        result.Set.Should().BeNull();
        result.Rejection.Should().NotBeNull();
        return result.Rejection!;
    }

    private static PersonBalance.Member Live(Guid id, decimal balance) => new(id, balance, false);

    // ── Kural 1 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kural1_hicbir_W_uyesi_sunucuda_yok_no_balance_kesin()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = Guid.NewGuid();                                    // yeni açılmış, henüz gönderilmemiş sahip satırı

        var rejection = Rejected(await Resolve(kit, a, a, Guid.NewGuid()));

        rejection.Code.Should().Be("no-balance");
        rejection.RowIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Kural1_a_yok_ama_bir_uye_var_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_factory);
        var b = await kit.MemberAsync(Kit.NewGroup(), 30m);
        var a = Guid.NewGuid();                                    // sahip satırı sunucuya ulaşmadı, kardeşi ulaştı

        var rejection = Rejected(await Resolve(kit, a, b));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(a);
    }

    // ── Kural 2 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kural2_sunucuda_olmayan_W_idsi_atilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 50m);

        var set = Accepted(await Resolve(kit, a, a, Guid.NewGuid()));

        set.AnchorId.Should().Be(a);
        set.Draw.Should().Equal(Live(a, 50m));
        set.LockedNegatives.Should().BeEmpty();
        set.Pool.Should().Be(50m);
    }

    // ── Kural 3 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kural3a_W_de_olup_S_de_olmayan_canli_uye_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 10m);
        var x = await kit.MemberAsync(Kit.NewGroup());             // WPF birleştirdi, sunucu henüz görmedi

        var rejection = Rejected(await Resolve(kit, a, a, x));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(x);
    }

    [Fact]
    public async Task Kural3b_S_de_olup_W_de_olmayan_pozitif_canli_uye_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);                     // başka PC ekledi, bu PC henüz çekmedi

        var rejection = Rejected(await Resolve(kit, a, a));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(b);
    }

    [Fact]
    public async Task Kural3a_ve_3b_birlikte_RowIds_birlesim_ordinal_sirali()
    {
        // RowIds = 3a ∪ 3b, "N" ordinal sıralı (§5.5 günlüğü bu sırayı yazar).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);                     // 3b: S'de, W'de yok, pozitif
        var x = await kit.MemberAsync(Kit.NewGroup());             // 3a: W'de, S'de yok

        var rejection = Rejected(await Resolve(kit, a, a, x));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(new[] { b, x }.OrderBy(id => id.ToString("N"), StringComparer.Ordinal));
    }

    [Fact]
    public async Task Kural3b_W_disindaki_sifir_ve_eksi_uyeler_gecer()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, balanceRow: false);       // bakiye satırı yok → 0 sayılır, 3b tetiklenmez
        var c = await kit.MemberAsync(g, -5m);

        var set = Accepted(await Resolve(kit, a, a));

        set.Person.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo(new[] { a, b, c });
        set.Person.Members.Should().Contain(Live(b, 0m));
        set.Draw.Should().Equal(Live(a, 10m));
        set.LockedNegatives.Should().Equal(Live(c, -5m));          // sıfır (b) yok sayılır
        set.Pool.Should().Be(5m);
    }

    // ── Kural 4 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kural4_A100_B_eksi60_W_A_havuz_40()
    {
        // spec §8.3: A = 100, B = −60, W = {A} → kullanılabilir 40. Görülmeyen borç havuzu açmaz (D3).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);

        var set = Accepted(await Resolve(kit, a, a));

        set.Draw.Should().Equal(Live(a, 100m));
        set.LockedNegatives.Should().Equal(Live(b, -60m));
        set.Pool.Should().Be(40m);
    }

    [Fact]
    public async Task Silinmis_uye_S_den_alinir_W_de_olmasa_da()
    {
        // Silinmiş satırın yerel grup numarası PC'ler arasında ayrışabilir, bu yüzden W'de eksik olabilir; sunucu S'nin
        // silinmişlerini kendisi ekler (K3).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, 40m, purged: true);

        var set = Accepted(await Resolve(kit, a, a));

        set.Draw.Should().BeEquivalentTo(new[] { Live(a, 0m), new PersonBalance.Member(p, 40m, true) });
        set.LockedNegatives.Should().BeEmpty();
        set.Pool.Should().Be(40m);
    }

    [Fact]
    public async Task W_deki_silinmis_uye_yok_sayilir()
    {
        // Silinmiş satırın yerel grup numarası PC'ler arasında ayrışabilir: W'deki silinmiş Id ne 3a'yı tetikler
        // ne kümeye girer.
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 10m);
        var z = await kit.MemberAsync(Kit.NewGroup(), 5m, purged: true);

        var set = Accepted(await Resolve(kit, a, a, z));

        set.Draw.Should().Equal(Live(a, 10m));
        set.Draw.Select(m => m.WpfCustomerId).Should().NotContain(z);
        set.Pool.Should().Be(10m);
    }

    // ── Kimlik ve numara ─────────────────────────────────────────────────────

    [Fact]
    public async Task Kopya_Id_asil_kayda_cozulur()
    {
        // Eski bir PC b'yi birleştirmeden önceki kopyanın Id'siyle tanıyor: kopya çözülmeseydi süzgeç onu gizler,
        // kural 2 atar ve b'nin 40'ı kural 3b'yi tetiklerdi.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: b);

        var set = Accepted(await Resolve(kit, a, a, copy));

        set.Draw.Select(m => m.WpfCustomerId).Should().BeEquivalentTo(new[] { a, b });
        set.Pool.Should().Be(40m);
    }

    [Fact]
    public async Task Ordinal_numara_ayri_kisi()
    {
        // §4.13, M5: "g…" ile "G…" ayrı kişi; büyük harfli numaranın satırı W'de → 3a.
        var kit = await Kit.CreateAsync(_factory);
        var stem = Kit.NewGroup();
        var a = await kit.MemberAsync(stem, 10m);
        var x = await kit.MemberAsync(stem.ToUpperInvariant(), 20m);

        var rejection = Rejected(await Resolve(kit, a, a, x));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(x);
    }

    [Fact]
    public async Task Grupsuz_a_tek_uyeli_kisi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 20m);
        await kit.MemberAsync(null, 7m);                           // başka grupsuz satır kişiye girmez

        var set = Accepted(await Resolve(kit, a, a));

        set.Person.Members.Should().Equal(Live(a, 20m));
        set.Draw.Should().Equal(Live(a, 20m));
        set.Pool.Should().Be(20m);
    }

    // ── Panel (K5) ve oynatma ────────────────────────────────────────────────

    [Fact]
    public async Task PersonDrawSetAsync_S_nin_hepsi_eksiler_Draw_icinde()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var c = await kit.MemberAsync(g, -20m);

        var set = await WithServiceAsync(s => s.PersonDrawSetAsync(kit.LicenseId, a, default));

        set.AnchorId.Should().Be(a);
        set.Draw.Should().BeEquivalentTo(new[] { Live(a, 10m), Live(b, 40m), Live(c, -20m) });
        set.LockedNegatives.Should().BeEmpty();
        set.Pool.Should().Be(30m);
    }

    [Fact]
    public async Task PoolBalanceAsync_memberIds_yokken_S_toplami()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        await kit.MemberAsync(g, 40m);
        await kit.MemberAsync(g, -10m);

        (await Pool(kit, a, null)).Should().Be(30m);
        (await Pool(kit, a, Array.Empty<Guid>())).Should().Be(30m);
    }

    [Fact]
    public async Task PoolBalanceAsync_kural_ihlalinde_ret_vermez()
    {
        // Kayıtlı anahtarın oynatması customer-not-synced almaz (§4.2): 3b ihlalinde de Σ_P döner.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);

        (await Pool(kit, a, new[] { a })).Should().Be(0m);
        (await Pool(kit, a, new[] { a, b })).Should().Be(40m);
    }

    [Fact]
    public async Task PoolBalanceAsync_eksi_havuz_sifir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, -60m);

        (await Pool(kit, a, new[] { a, b })).Should().Be(0m);
    }
}
```

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawSetTests"`
Expected: FAIL — derleme hatası: `PersonBalance.DrawSetResult`, `PersonBalance.DrawSet`, `PersonBalance.DrawSetRejection` türleri (CS0426) ve `ResolveDrawSetAsync`, `PersonDrawSetAsync`, `PoolBalanceAsync` yöntemleri (CS1061) yok.

- [ ] **Adım 3: Ctor'a `CustomerIdResolver`**

`PersonBalance.cs`'te (`:28-35`) bugünkü:

```csharp
    private readonly LicenseDbContext _db;
    private readonly ILogger<PersonBalance> _log;

    public PersonBalance(LicenseDbContext db, ILogger<PersonBalance> log)
    {
        _db = db;
        _log = log;
    }
```

şununla değiştir:

```csharp
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly ILogger<PersonBalance> _log;

    public PersonBalance(LicenseDbContext db, CustomerIdResolver customerIds, ILogger<PersonBalance> log)
    {
        _db = db;
        _customerIds = customerIds;
        _log = log;
    }
```

`CustomerIdResolver` aynı ad alanında (`OrderDeck.LicenseServer.Services.CustomerSync`); `using` gerekmez. Alan Adım 4'te kullanılır — Adım 3 ile 4 birlikte derlenir (arada derleme yok).

- [ ] **Adım 4: Çekim kümesi bölümü**

`PersonBalance.cs`'te kişi bölümünün sonuna — `MembersAsync`'in (ve Görev 1'in eklediği `IsPoolModeAsync`'in) kapanışından sonra, şu satırdan HEMEN ÖNCE:

```csharp
    // ── Çekim grupları (§2, §3.2) ────────────────────────────────────────────
```

ekle:

```csharp
    // ── Çekim kümesi (§4.0) ──────────────────────────────────────────────────

    /// <summary>409 kodu: kişinin hiçbir üyesi sunucuda yok — kesin; yeni açılmış müşteri bekletilmez (§4.0 kural 1).</summary>
    public const string CodeNoBalance = "no-balance";

    /// <summary>
    /// 409 kodu (K2): sahip satırı sunucuda yok ya da WPF'in kişisi (W) sunucununkiyle (S) uyuşmuyor (§4.0 kural 1,
    /// 3a, 3b). Bir gönderim + çekme turuyla kapanır (§6.1-b).
    /// </summary>
    public const string CodeCustomerNotSynced = "customer-not-synced";

    /// <summary>
    /// Çekim kümesi (spec 2026-10-11 §2, §4.0-4). <c>Draw</c> = P: WPF'te (W ∩ S)'nin canlıları ∪ S'nin silinmişleri,
    /// panelde (K5) S'nin hepsi. <c>LockedNegatives</c> = S∖P'nin eksi bakiyeli üyeleri (sıfırlar yok sayılır; panelde
    /// boş): para onlardan çekilmez ama havuza sayılır ve kilitlenir — görülmeyen borç havuzu açmaz (D3, §4.13). İki
    /// liste de <c>Person.Members</c>'ın sırasında ("N" ordinal).
    /// </summary>
    /// <param name="AnchorId">a: isteğin asıl kaydı (çapanın satırı).</param>
    /// <param name="Person">S(a), <see cref="MembersAsync"/>'ten.</param>
    public sealed record DrawSet(
        Guid AnchorId, Person Person, IReadOnlyList<Member> Draw, IReadOnlyList<Member> LockedNegatives)
    {
        /// <summary>Σ = Σ_P + Σ_{S∖P, Balance &lt; 0}. Kullanılabilir = max(0, Σ).</summary>
        public decimal Pool => Draw.Sum(m => m.Balance) + LockedNegatives.Sum(m => m.Balance);
    }

    /// <param name="Code"><see cref="CodeNoBalance"/> ya da <see cref="CodeCustomerNotSynced"/>.</param>
    /// <param name="RowIds">customer-not-synced'e yol açan satırlar, "N" ordinal sıralı (§5.5 günlüğü); no-balance'ta
    /// boş.</param>
    public sealed record DrawSetRejection(string Code, IReadOnlyList<Guid> RowIds);

    /// <summary>Tam olarak biri dolu: <c>Set</c> ya da <c>Rejection</c>.</summary>
    public sealed record DrawSetResult(DrawSet? Set, DrawSetRejection? Rejection);

    /// <summary>
    /// Havuz kipinde WPF preview ve apply'ın çekim kümesi P ve 409 kuralları — spec 2026-10-11 §4.0 kural 1–4.
    /// W = <paramref name="memberIds"/> ∪ {a}, her Id asıl kayda çözülmüş; W'nin yalnız sunucuda var olan CANLI
    /// üyeleri kullanılır, silinmişler S'den alınır (K3).
    /// <list type="number">
    /// <item>a'nın projeksiyonu yoksa: W'nin hiçbir Id'si de yoksa <see cref="CodeNoBalance"/> (kesin; apply bunu 409
    /// döner, preview 200 <c>Balance = 0</c> — plan fark 19), en az biri varsa <see cref="CodeCustomerNotSynced"/>
    /// (<c>RowIds</c> = [a]).</item>
    /// <item>Sunucuda olmayan W Id'si atılır; W'nin silinmiş üyeleri yok sayılır.</item>
    /// <item>(a) W'nin S'de olmayan canlı üyesi ya da (b) S'nin W'de olmayan, bakiyesi &gt; 0 canlı üyesi varsa
    /// <see cref="CodeCustomerNotSynced"/> (<c>RowIds</c> = ikisinin birleşimi).</item>
    /// <item><see cref="DrawSet"/>: P ve S∖P'nin eksileri.</item>
    /// </list>
    /// <para>Plan fark 5: <see cref="MembersAsync"/> "a sunucuda yok"u ayırt etmez (projeksiyon yoksa S = {a}); kural 1
    /// ve 2'nin varlık ve silinmişlik bilgisi <see cref="KnownMembersAsync"/>'in tek sorgusundan gelir. Yalnız okur;
    /// iki okuma arasında üyelik değişirse yazıcı (<c>DrawAsync</c>) üyeliği kayıttan önce yeniden okur.</para>
    /// </summary>
    /// <param name="anchorId">a — asıl kayıt olmalı (çağıran <see cref="CustomerIdResolver"/>'dan geçirir).</param>
    public async Task<DrawSetResult> ResolveDrawSetAsync(
        Guid licenseId, Guid anchorId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var known = await KnownMembersAsync(licenseId, anchorId, memberIds, ct);

        // Kural 1: çapa yazılamaz (hareket projeksiyona FK). Hiçbir üye yoksa hiçbir üyede para da olamaz (bakiye
        // satırı projeksiyona FK): kesin no-balance.
        if (!known.ContainsKey(anchorId))
            return new DrawSetResult(null, known.Count == 0
                ? new DrawSetRejection(CodeNoBalance, [])
                : new DrawSetRejection(CodeCustomerNotSynced, [anchorId]));

        // Kural 2 + K3: sunucuda olmayan Id sözlükte yok; W'nin silinmişleri burada düşer.
        var wLive = LiveIds(known);
        var person = await MembersAsync(licenseId, anchorId, ct);

        // Kural 3: W ile S'nin CANLI üyeleri. 3a S dışından, 3b S içinden gelir: iki küme ayrık.
        var inPerson = person.Members.Select(m => m.WpfCustomerId).ToHashSet();
        var notSynced = wLive.Where(id => !inPerson.Contains(id))
            .Concat(person.Members
                .Where(m => !m.Purged && m.Balance > 0m && !wLive.Contains(m.WpfCustomerId))
                .Select(m => m.WpfCustomerId))
            .OrderBy(id => id.ToString("N"), StringComparer.Ordinal)
            .ToList();
        if (notSynced.Count > 0)
            return new DrawSetResult(null, new DrawSetRejection(CodeCustomerNotSynced, notSynced));

        return new DrawSetResult(BuildDrawSet(anchorId, person, wLive), null);
    }

    /// <summary>
    /// Panelin çekim kümesi (K5, §4.8, §4.9): P = S(a), eksiler dahil; <c>LockedNegatives</c> boş. Panel WPF üye
    /// listesi taşımaz; 409 kuralı yok.
    /// </summary>
    /// <param name="anchorId">a — asıl kayıt (rota satırı ya da hedef hareketin satırı).</param>
    public async Task<DrawSet> PersonDrawSetAsync(Guid licenseId, Guid anchorId, CancellationToken ct)
    {
        var person = await MembersAsync(licenseId, anchorId, ct);
        return new DrawSet(anchorId, person, person.Members, []);
    }

    /// <summary>
    /// Yeniden oynatmanın <c>RemainingBalance</c>'ı (§4.2; plan fark 18c): kayıtlı anahtar hiçbir zaman
    /// <see cref="CodeCustomerNotSynced"/> almaz, bu yüzden kural 1 ve 3 UYGULANMAZ. <paramref name="memberIds"/> null
    /// ya da boşsa max(0, Σ_S); değilse kural 2 ve 4'le kurulan kümenin max(0, Σ)'sı (kural 1 uygulanmaz: a sunucuda
    /// yoksa S = {a} olur ve küme boş kalır → 0; kayıtlı anahtarda çapa hareketi projeksiyona FK olduğundan bu kol
    /// pratikte oluşmaz).
    /// </summary>
    public async Task<decimal> PoolBalanceAsync(
        Guid licenseId, Guid anchorId, IReadOnlyCollection<Guid>? memberIds, CancellationToken ct)
    {
        if (memberIds is null || memberIds.Count == 0)
        {
            var whole = await MembersAsync(licenseId, anchorId, ct);
            return Math.Max(0m, whole.Members.Sum(m => m.Balance));
        }

        var known = await KnownMembersAsync(licenseId, anchorId, memberIds, ct);
        var person = await MembersAsync(licenseId, anchorId, ct);
        return Math.Max(0m, BuildDrawSet(anchorId, person, LiveIds(known)).Pool);
    }

    /// <summary>
    /// W = <paramref name="memberIds"/> ∪ {a}, asıl kayda çözülmüş (kopya Id'si eski PC'den gelebilir); döner: W'nin
    /// sunucuda projeksiyonu olan Id'si → silinmiş mi. TEK sorgu; sorgu süzgeci kopyaları gizler — girdiler asıl
    /// kayda çözülmüş olduğundan doğru (plan fark 5).
    /// </summary>
    private async Task<Dictionary<Guid, bool>> KnownMembersAsync(
        Guid licenseId, Guid anchorId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var canonical = await _customerIds.CanonicalOfAsync(licenseId, memberIds.Append(anchorId), ct);
        var w = canonical.Values.Append(anchorId).Distinct().ToList();
        return await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && w.Contains(p.Id))
            .Select(p => new { p.Id, Purged = p.PurgedAt != null })
            .ToDictionaryAsync(x => x.Id, x => x.Purged, ct);
    }

    /// <summary>W'nin sunucuda var olan canlı üyeleri: bilinmeyenler (kural 2) ve silinmişler (K3) düşer.</summary>
    private static HashSet<Guid> LiveIds(IReadOnlyDictionary<Guid, bool> known) =>
        known.Where(kv => !kv.Value).Select(kv => kv.Key).ToHashSet();

    /// <summary>
    /// Kural 4 (saf): P = (W ∩ S)'nin canlıları ∪ S'nin silinmişleri; S∖P'nin eksi bakiyeli üyeleri kilitlenen eksiler
    /// (sıfırlar yok sayılır). Sıra <c>person.Members</c>'ınki.
    /// </summary>
    private static DrawSet BuildDrawSet(Guid anchorId, Person person, IReadOnlySet<Guid> wLive)
    {
        var draw = person.Members
            .Where(m => m.Purged || wLive.Contains(m.WpfCustomerId))
            .ToList();
        var lockedNegatives = person.Members
            .Where(m => !m.Purged && !wLive.Contains(m.WpfCustomerId) && m.Balance < 0m)
            .ToList();
        return new DrawSet(anchorId, person, draw, lockedNegatives);
    }

```

(`Dictionary<Guid, bool>` `IReadOnlyDictionary`'yi, `HashSet<Guid>` `IReadOnlySet`'i uygular; `ToDictionaryAsync` `Microsoft.EntityFrameworkCore`'dan — dosyanın `using`'leri yeterli.)

- [ ] **Adım 5: Koştur — PASS + regresyon**

Önce elle kurulum olmadığını doğrula (ctor yalnız DI'dan geçer):

Run: `git grep -n "new PersonBalance(" -- "*.cs"`
Expected: çıktı yok.

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawSetTests"`
Expected: PASS (17 test).

Regresyon — ctor değişikliği `PersonBalance`'ı DI'dan alan her uca dokunur (WPF reverse ve okuyucular, panel, Shopper, CLI, günlük iş); kip bayrağı okunmadığı için davranış değişmez:

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawSetTests|FullyQualifiedName~PersonBalanceMembersTests|FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalanceVerifierTests|FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~PersonBalanceCommandTests|FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRuleTests|FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceGroupReversalTests|FullyQualifiedName~PanelCustomerBalanceControllerTests|FullyQualifiedName~PersonBalancePanelListTests|FullyQualifiedName~PersonBalancePanelReverseTests|FullyQualifiedName~PersonBalanceShopperListTests|FullyQualifiedName~ShopperBalanceControllerTests"`
Expected: hepsi PASS, var olan testler değişmeden. Derleme 0 uyarı (`TreatWarningsAsErrors`; yeni özel üyelerin hepsi kullanılıyor: `_customerIds` `KnownMembersAsync` içinde okunur; `KnownMembersAsync`, `LiveIds` ve `BuildDrawSet` her biri iki yerden çağrılır — `ResolveDrawSetAsync` ve `PoolBalanceAsync`).

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDrawSetTests.cs
git commit -F - <<'MSG'
feat(balance): çekim kümesini kural 1–4 ile çöz, havuz bakiyesini hesapla

ResolveDrawSetAsync WPF'in üye listesini (W) asıl kayda çözer, W ∪ {a}'nın
varlığını ve silinmişliğini tek sorguyla okur (MembersAsync "a sunucuda
yok"u ayırt etmez): hiç üye yoksa kesin no-balance, a yoksa ya da W ile S
uyuşmuyorsa (3a, 3b) customer-not-synced ve satır Id'leri; değilse P =
(W ∩ S)'nin canlıları ∪ S'nin silinmişleri, S∖P'nin eksileri havuza sayılıp
kilitlenir (D3). PersonDrawSetAsync panelin P = S kümesi (K5);
PoolBalanceAsync oynatmanın kalan bakiyesi için 409 kurallarını uygulamadan
max(0, Σ) döner. PersonBalance artık CustomerIdResolver alır (yalnız DI).
Kip bayrağı okunmaz; satır kipinin davranışı değişmez (ortak bakiye §4.0).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 3: `PlanDraw` (saf) + tablo testleri (spec §4.2 adım 4, M7, §8 "PlanDraw tablo testleri")

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` — yeni bölüm `// ── Çekim planı (§4.2 adım 4, M7) ──` (`DrawLeg`, `PlanDraw`), `// ── Çekim grupları (§2, §3.2) ──` başlığının hemen ÖNÜNE. Master'da başlık `:72`'de; Görev 2'nin `// ── Çekim kümesi (§4.0) ──` bölümü kişi bölümünden (`:37-70`) sonra eklendiği için yürütme anında aşağı kaymış olur. Ekleme başlık metnine bağlanır (satır numarasına değil), böylece yeni bölüm "Çekim kümesi"nin ardından, "Çekim grupları"nın önünden girer.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalancePlanDrawTests.cs` (saf, fikstürsüz; `PersonBalanceHistoryTests` emsali — sınıf koleksiyonsuz, `ShardingTestFramework` onu InMemory parçasına koyar).

Plan, `DrawAsync`'in (Görev 4) çekim kümesinden (`DrawSet.Draw`, Görev 2) ve uygulanacak tutardan bacak listesini üretir. Saftır: veritabanı, saat, rastgelelik yok; aynı girdi her zaman aynı listeyi verir (M7 "sıra belirlenimli"), girdi listesinin sırası sonucu değiştirmez. Kural (spec `:166-169`): her adımda o satırdan `min(max(0, bakiye), kalan)` alınır; sıra çapa → silinmiş üyeler (K3) → canlı üyeler; ikisinde de bakiye büyükten küçüğe, eşitlikte Id `"N"` ordinal. Listenin ilk elemanı HER ZAMAN çapadır ve tutarı 0 olabilir (spec §3.2: çapa satırı tutarı 0 olan bir düşüm satırıdır — Örnek 1'de `K = 0,00`); çapa `draw`'da yoksa bakiyesi 0 sayılır (çapanın bakiye satırı olmayabilir, §4.2 adım 5). Çapa dışında bakiyesi ≤ 0 olan üye ve 0 çekilen üye listede yer almaz. Toplam `min(max(0, amount), çapanın artısı + Σ artı bakiyeler)`'dir. `amount ≤ max(0, Σ)` garantisini (Σ `S∖P` eksilerini de içerir) çağıran `DrawAsync` verir; `PlanDraw` onu denetlemez, yalnız kırpar.

**Not (spec ↔ kod):** Bu görevde yeni fark yok. Spec §4.2 adım 4.2 silinmiş üyeler arasında eşitlik kırıcı söylemez; iskelet farkı **18b** uygulanır: silinmişler arasında da Id `"N"` ordinal (M7 "sıra belirlenimli"). `Member` (`PersonBalance.cs:40`) S1'den aynen kullanılır; yeni alan yok. Şema/göç yok, satır kipi bu yöntemi çağırmaz.

- [ ] **Adım 1: Tablo testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalancePlanDrawTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;
using DrawLeg = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance.DrawLeg;
using Member = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance.Member;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <see cref="PersonBalance.PlanDraw"/> — spec 2026-10-11 §4.2 adım 4, M7 ve §8 "PlanDraw tablo testleri" (saf,
/// fikstürsüz): çapa önce, silinmiş önce, büyük bakiye önce, Id ("N", ordinal) eşitliği, eksi satır atlanır, tutar
/// kırpma. Her iddia listenin TAMAMINI sırasıyla karşılaştırır; girdi bilerek karışık sırada verilir.
/// </summary>
public sealed class PersonBalancePlanDrawTests
{
    private static Member M(Guid id, decimal balance, bool purged = false) => new(id, balance, purged);

    private static DrawLeg L(Guid id, decimal amount) => new(id, amount);

    /// <summary>"N" metni bilinen sırada olan Id: <c>G('1')</c>'in metni <c>G('2')</c>'ninkinden ordinal küçük.</summary>
    private static Guid G(char c) => Guid.Parse(new string(c, 32));

    private static IReadOnlyList<DrawLeg> Plan(Guid anchor, decimal amount, params Member[] draw) =>
        PersonBalance.PlanDraw(anchor, draw, amount);

    [Fact]
    public void Ornek1_capa_sifir_I_once_sonra_Y()
    {
        // Spec Örnek 1 (§4.2): T = 0 (çapa), I = 100, Y = 30; applied 120 → K(T, 0), L1(I, −100), L2(Y, −20).
        var t = Guid.NewGuid();
        var i = Guid.NewGuid();
        var y = Guid.NewGuid();

        var legs = Plan(t, 120m, M(y, 30m), M(t, 0m), M(i, 100m));

        legs.Should().Equal(L(t, 0m), L(i, 100m), L(y, 20m));
        legs.Sum(l => l.Amount).Should().Be(120m, "bacakların toplamı uygulanan tutardır");
    }

    [Fact]
    public void Capa_once_cekilir()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 30m, M(b, 100m), M(a, 50m)).Should().Equal(L(a, 30m));
    }

    [Fact]
    public void Capa_yetmezse_kalan_obur_uyelerden()
    {
        // Spec §4.8 örneği: A = 10, B = 40, rota A, −30 → çapa (A, −10), bacak (B, −20).
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 30m, M(b, 40m), M(a, 10m)).Should().Equal(L(a, 10m), L(b, 20m));
    }

    [Fact]
    public void Silinmis_uyeler_canlilardan_once()
    {
        // Spec §5.4 (K3): {A = 0, P silinmiş = 40}; 30'luk ödeme → çapa A 0, bacak P −30 — büyük bakiyeli canlı B'ye
        // rağmen.
        var a = Guid.NewGuid();
        var p = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 30m, M(b, 100m), M(p, 40m, purged: true), M(a, 0m)).Should().Equal(L(a, 0m), L(p, 30m));
    }

    [Fact]
    public void Silinmisler_yetmezse_canlilara_gecilir()
    {
        var a = Guid.NewGuid();
        var p = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 30m, M(b, 100m), M(a, 0m), M(p, 10m, purged: true))
            .Should().Equal(L(a, 0m), L(p, 10m), L(b, 20m));
    }

    [Fact]
    public void Silinmislerde_buyuk_bakiye_once()
    {
        // Spec §4.2 adım 4.2: silinmişler kendi aralarında da bakiye büyükten küçüğe. Id ordinal sırası (G1 < G2)
        // bakiye sırasının tersine kurulu: yalnız Id'ye göre sıralayan bir uygulama G1'i önce koyar ve düşer.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var g1 = G('1');
        var g2 = G('2');

        Plan(a, 45m, M(b, 100m), M(g1, 10m, purged: true), M(a, 0m), M(g2, 40m, purged: true))
            .Should().Equal(L(a, 0m), L(g2, 40m), L(g1, 5m));
    }

    [Fact]
    public void Buyuk_bakiye_once()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        Plan(a, 60m, M(a, 0m), M(b, 20m), M(c, 50m)).Should().Equal(L(a, 0m), L(c, 50m), L(b, 10m));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Esitlikte_Id_N_ordinal(bool tersSira)
    {
        var a = Guid.NewGuid();
        var g1 = G('1');
        var g2 = G('2');
        var draw = tersSira
            ? new[] { M(g2, 30m), M(a, 0m), M(g1, 30m) }
            : new[] { M(a, 0m), M(g1, 30m), M(g2, 30m) };

        PersonBalance.PlanDraw(a, draw, 40m).Should().Equal(L(a, 0m), L(g1, 30m), L(g2, 10m));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Silinmislerde_de_esitlik_Id_N_ordinal(bool tersSira)
    {
        // Plan farkı 18b: silinmişler arasında da eşitlik kırıcı Id ("N", ordinal). Canlı B'nin büyük bakiyesi sırayı
        // değiştirmez.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var g3 = G('3');
        var g4 = G('4');
        var draw = tersSira
            ? new[] { M(g4, 30m, purged: true), M(b, 100m), M(g3, 30m, purged: true), M(a, 0m) }
            : new[] { M(a, 0m), M(b, 100m), M(g3, 30m, purged: true), M(g4, 30m, purged: true) };

        PersonBalance.PlanDraw(a, draw, 40m).Should().Equal(L(a, 0m), L(g3, 30m), L(g4, 10m));
    }

    [Fact]
    public void Eksi_ve_sifir_uyeler_atlanir()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        Plan(a, 40m, M(b, -60m), M(a, 0m), M(c, 0m), M(d, 50m)).Should().Equal(L(a, 0m), L(d, 40m));
    }

    [Fact]
    public void Capa_eksi_bakiyeliyse_sifir_cekilir()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 20m, M(a, -10m), M(b, 50m)).Should().Equal(L(a, 0m), L(b, 20m));
    }

    [Fact]
    public void Capa_listede_yoksa_sifir_tutarli_ilk_eleman()
    {
        // Çapanın bakiye satırı olmayabilir (§4.2 adım 5); yine de planın ilk elemanıdır.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 20m, M(b, 50m)).Should().Equal(L(a, 0m), L(b, 20m));
    }

    [Fact]
    public void Tutar_kirpilir_son_bacak_kismi()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        Plan(a, 40m, M(c, 20m), M(a, 0m), M(b, 30m)).Should().Equal(L(a, 0m), L(b, 30m), L(c, 10m));
    }

    [Fact]
    public void Tutar_artilarin_toplamini_asarsa_artilar_kadar()
    {
        // Savunma: DrawAsync tutarı Σ'ya kırptıktan sonra çağırır; PlanDraw yine de artıların ötesine geçmez.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 100m, M(a, 0m), M(b, 30m)).Should().Equal(L(a, 0m), L(b, 30m));
    }

    [Fact]
    public void Sifir_tutar_yalniz_sifir_capa()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 0m, M(a, 50m), M(b, 40m)).Should().Equal(L(a, 0m));
        // Eksi tutar 0 sayılır (savunma; çağıran eksi tutar vermez).
        Plan(a, -5m, M(a, 50m), M(b, 40m)).Should().Equal(L(a, 0m));
    }

    [Fact]
    public void Tek_bacakli_cekim_yalniz_capa()
    {
        var a = Guid.NewGuid();

        Plan(a, 100m, M(a, 100m)).Should().Equal(L(a, 100m));
    }
}
```

`Should().Equal(...)` (FluentAssertions `GenericCollectionAssertions<T>.Equal(params T[])`) sırayı ve eleman sayısını birlikte denetler; `DrawLeg` bir `record` olduğundan eşitlik değer eşitliğidir (`decimal` eşitliği ölçeği yok sayar: `20m == 20.00m`).

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePlanDrawTests"`
Expected: FAIL — derleme hatası: `PersonBalance.DrawLeg` yok (CS0426, `using DrawLeg = …` takma adı) ve `PersonBalance.PlanDraw` yok (CS0117).

- [ ] **Adım 3: `PersonBalance`'a çekim planı bölümü**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` — yeni bölüm "Çekim grupları" başlığının hemen önüne girer. Master b6ee847c'de başlık `:72`'dedir ve önünde `MembersAsync`'in kapanışı (`:69`) bulunur; Görev 1–2'den sonra başlığın hemen önünde Görev 2'nin "Çekim kümesi" bölümünün son satırları yer alır. Başlığın önündeki satırlar Edit'e girmez; eşleşme yalnız başlık satırına bağlıdır (dosyada bir kez geçer).

Edit: `old_string` yalnız başlık satırı

```csharp
    // ── Çekim grupları (§2, §3.2) ────────────────────────────────────────────
```

`new_string`:

```csharp
    // ── Çekim planı (§4.2 adım 4, M7) ────────────────────────────────────────

    /// <summary>Planın bir satırı: o satırdan çekilecek tutar (≥ 0). Yazıcı (<c>DrawAsync</c>) <c>−Amount</c> yazar.</summary>
    public sealed record DrawLeg(Guid WpfCustomerId, decimal Amount);

    /// <summary>
    /// Çekim planı — spec 2026-10-11 §4.2 adım 4, M7 (sıra belirlenimli). Saf: veritabanı, saat, rastgelelik yok;
    /// girdi listesinin sırası sonucu değiştirmez. Her adımda o satırdan <c>min(max(0, bakiye), kalan)</c> alınır.
    /// Sıra: çapa (alıcının parası önce, K1) → silinmiş üyeler (ayırmada erişilemez kalabilecek para önce, K3) →
    /// canlı üyeler; iki grupta da bakiye büyükten küçüğe, eşitlikte Id ("N", ordinal). Silinmişler arasında eşitlik
    /// kırıcı da Id (plan fark 18b).
    ///
    /// <para>İlk eleman HER ZAMAN çapadır ve tutarı 0 olabilir: çapa satırı tutarı 0 olan bir düşüm satırıdır (§3.2).
    /// Çapa <paramref name="draw"/>'da yoksa bakiyesi 0 sayılır (bakiye satırı olmayabilir, §4.2 adım 5). Çapa dışında
    /// bakiyesi ≤ 0 olan üye ve 0 çekilen üye listede yoktur.</para>
    ///
    /// <para>Bacakların toplamı <c>min(max(0, amount), çapanın artısı + Σ artı bakiyeler)</c>. Çağıran
    /// <c>amount ≤ max(0, Σ)</c> garantisini verir (Σ, <c>S∖P</c>'nin eksilerini de içerir); burada denetlenmez,
    /// yalnız kırpılır.</para>
    /// </summary>
    /// <param name="anchorId">Çapa: istekteki (rota/sahip) satırın asıl kaydı.</param>
    /// <param name="draw">Çekim kümesi P (<c>DrawSet.Draw</c>); eksi ve sıfır üyeler içerebilir.</param>
    /// <param name="amount">Uygulanacak tutar (≥ 0 beklenir; eksi 0 sayılır).</param>
    public static IReadOnlyList<DrawLeg> PlanDraw(Guid anchorId, IReadOnlyList<Member> draw, decimal amount)
    {
        var remaining = Math.Max(0m, amount);

        var anchorBalance = draw.FirstOrDefault(m => m.WpfCustomerId == anchorId)?.Balance ?? 0m;
        var fromAnchor = Math.Min(Math.Max(0m, anchorBalance), remaining);
        var legs = new List<DrawLeg> { new(anchorId, fromAnchor) };
        remaining -= fromAnchor;

        var others = draw
            .Where(m => m.WpfCustomerId != anchorId && m.Balance > 0m)
            .OrderByDescending(m => m.Purged)
            .ThenByDescending(m => m.Balance)
            .ThenBy(m => m.WpfCustomerId.ToString("N"), StringComparer.Ordinal);
        foreach (var member in others)
        {
            if (remaining <= 0m) break;
            var take = Math.Min(member.Balance, remaining);
            legs.Add(new DrawLeg(member.WpfCustomerId, take));
            remaining -= take;
        }

        return legs;
    }

    // ── Çekim grupları (§2, §3.2) ────────────────────────────────────────────
```

Notlar: `OrderByDescending(m => m.Purged)` `true`'yu önce koyar (silinmiş önce). Döngüde `member.Balance > 0` ve `remaining > 0` olduğundan `take > 0`'dır; çapa dışında 0'lık bacak oluşmaz. Proje `ImplicitUsings` açık (`System.Linq` ekli); yeni `using` gerekmez. Sınıf belgesindeki "S2: `ResolveDrawSetAsync`, `PlanDraw`, `DrawAsync`" cümlesi (`:12-15`) olduğu gibi kalır.

- [ ] **Adım 4: Koştur — PASS**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePlanDrawTests"`
Expected: PASS — 18 vaka (14 `[Fact]` + iki `[Theory]` × 2), derleme 0 uyarı.

- [ ] **Adım 5: Regresyon — Servis/CLI + çekim kümesi**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePlanDrawTests|FullyQualifiedName~PersonBalanceDrawSetTests|FullyQualifiedName~PersonBalanceMembersTests|FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalanceVerifierTests|FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~PersonBalanceCommandTests"`
Expected: hepsi PASS. Değişiklik yalnız ekleme (yeni kayıt + statik yöntem); var olan testler değiştirilmez, satır kipinin hiçbir yolu `PlanDraw`'ı çağırmaz.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalancePlanDrawTests.cs
git commit -F - <<'MSG'
feat(balance): çekim planını saf PlanDraw olarak ekle

Havuz kipindeki her çekimin (WPF apply, panel K5) bacak listesini tek
yerde, belirlenimli üret: önce çapa (tutarı 0 olabilir, her zaman ilk
eleman), sonra silinmiş üyeler, sonra canlı üyeler; bakiye büyükten
küçüğe, eşitlikte Id "N" ordinal (silinmişler arasında da). Her adımda
min(max(0, bakiye), kalan) alınır; eksi ve sıfır üyeler atlanır, tutar
artıların toplamına kırpılır. Saf olduğu için tablo testleriyle
kanıtlanır; DrawAsync onu çağıracak (ortak bakiye §4.2 adım 4, M7, K3).
Satır kipi değişmez.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 4: `DrawAsync`: yazım, kişi kilidi, üyeliğin ikinci okuması, baştan kurma (spec §4.2 adım 1–7, §3.2, §4.8, §4.9, §4.13, M6, M7, M12)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` — `using` bloğu (`:1-4`), sınıf belgesi (`:12-15`), sabitler (`:26` yanına `TransactionPrimaryKey`), yeni bölüm `// ── Çekim (§4.2, §4.8, §4.9, §4.13) ──` "Çekim grupları" bölüm başlığının (`:72`) hemen önüne — yani Görev 2'nin "Çekim kümesi" ve Görev 3'ün "Çekim planı" bölümlerinden SONRA; `IsDuplicateTransactionKey` "Hata tanıma" bölümüne (`:361-393`, `IsDuplicateBalanceRow` `:369-373`'ten sonra). Satır numaraları master `b6ee847c`'ninkiler; Görev 1–3'ün eklemeleri onları kaydırır, Edit bağlamları kaymadan etkilenmez.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDrawTests.cs` (`IClassFixture<ApiFactory>`, InMemory; servis `factory.Services.CreateScope()`'tan; kurulum `Kit`).

**Not (spec ↔ kod), bu görev:**
- *Her denemede Lookup* (§4.2-6) ve *PK ihlalinde kazananı oynat* (§4.2-6) uca özgüdür: servis Lookup'ı bilmez. Lookup çağıranın `beforeAttempt` geri çağrısıdır (her denemenin başında, kilitten sonra, okumalardan önce; `true` → `Stopped`, hiçbir şey yazılmaz); PK ihlali yalnız anahtar istemcinin iken (`AnchorIdIsIdempotencyKey`) `DuplicateKey` döner, oynatmayı çağıran yapar (Görev 7). Sunucunun ürettiği Id'de (K5) PK ihlali yakalanmaz.
- *Üyeliğin ikinci okumasında fark* deneme sayılır (iskelet fark 18a): `"üyelik değişti"` nedeniyle baştan kurulur, `MaxAttempts`'e girer, tükenince `Busy`.
- *Kapsam kuralının işlemi* deneme başınadır (iskelet fark 7): `ScopeLockResource` doluysa her deneme kendi işlemini açar ve `SalesCommandLock`'u alır; commit edilmeyen işlem `await using` ile atılınca geri alınır, kilit (`@LockOwner = 'Transaction'`) bırakılır. InMemory'de işlem açılmaz (kilit zaten no-op).
- *Yakalama sırası* (iskelet fark 9): `DbUpdateConcurrencyException` → `IsDuplicateReversal` → `IsDuplicateBalanceRow` → `IsDuplicateTransactionKey` (yalnız istemci anahtarı) → `Exception when IsDeadlock`. `try` denemenin okumalarını, kilidi ve geri çağrıyı da kapsar (§4.2-6 S2 uygulama notu).
- *`applied ≤ 0` denetimi* iki kurala ortak uygulanır. Spec yalnız UpToPool için `nothing-to-apply` der; AllOrNothing'de `Requested > 0` çağıranın sözleşmesidir (K5: `|tutar|`), ortak denetim yalnız savunmadır — 0 tutarlı bir çapa yazılmasını önler. Davranış eklemez.
- *Ret sonuçlarında `Pool`*: tutar retlerinde (`NoBalance` (Σ'den), `NothingToApply`, `Insufficient`) izlenen satırlardan Σ; küme retlerinde (`NoBalance` kural 1, `CustomerNotSynced`), `Stopped`, `AlreadyReversed`, `DuplicateKey`, `Busy`'de 0 (Σ kurulmadı).
- *İzlenen yükleme* çapanın Id'sini de kümeye ekler (`Draw ∪ LockedNegatives ∪ {a}`): çapa her iki kümede de `Draw`'dadır (Görev 2), ekleme yalnız savunmadır ve sorgu sayısını değiştirmez.
- *Günlükteki `anchor`* çapanın hareket Id'sidir (`AnchorTransactionId` = WPF'in anahtarı), `ReverseGroupAsync`'in `anchor={AnchorId}`'siyle aynı anlam; WPF günlüğüyle anahtardan eşleşir. Ad/telefon yok.
- *Kural 3b izlenen satırlarla yeniden* (talimat brief-04 adım 7'den sapma; spec §4.0-3b kazanır). Talimat "izlenen bakiyesi artık < 0 değilse toplama girmez" diyordu. Ama S∖P'nin izlemesiz okumada (e) eksi olan bir üyesi, (e) ile izlenen yükleme (5) arasında commit eden bir iadeyle artıya geçebilir. İzlenen jeton iade sonrasının değeridir, yani çekim iadeden SONRA sıralanır. O sırada geçerli veriyle kural 3b tutar: S'de olup W'de olmayan, bakiyesi > 0 canlı üye → 409 `customer-not-synced`. Talimatın davranışı o parayı yok sayıp çekerdi ya da Σ ≤ 0'da kesin `NoBalance` dönerdi; WPF `MarkNoBalance` yazar ve iş bir daha sorulmazdı (3b'nin önlediği durum). **Çözüm:** üyeliğin ikinci okumasından sonra, Σ'den önce, `LockedNegatives`'in izlenen bakiyesi > 0 olan üyeleri "N" ordinal sıralı `RejectedRowIds` ile `CustomerNotSynced` döner, hiçbir şey yazılmaz (`Pool = 0`, küme reddi gibi). 0'a inen üye eskisi gibi toplama girmez ama kilitlenir. İzlenen yüklemeden sonra gelen iadeyi jeton yakalar (baştan kurulan deneme kümede 3b'yi görür). Yalnız WPF yolunda olur (panelde `LockedNegatives` boş). InMemory'de iki okuma arasına yazım sokulamaz; kanıt Testcontainers'ta: Görev 15'in `Kural3b_W_disi_eksi_uye_izlenen_yuklemede_artiya_gecerse_customer_not_synced` testi — `ReadQueryHook` zinciri denemenin 1. `FROM [CustomerBalances]` okumasında (e) kurulur, S∖P'nin eksi üyesini artıya çeviren iade 2. okumanın (izlenen yükleme) hemen önünde commit eder; beklenen 409 `customer-not-synced`, çekim grubu yazılmaz, hareket sayısı yalnız iade kadar artar. Sahip "yok say" derse bu madde Sahip sorusuna döner.
- *`Requested` kuruş hassasiyeti* (ön koşul, savunma). `decimal(18,2)` (göç `20260602125823_AddCustomerBalance.cs:21`, `:47`) bacağın `Amount`'unu ve satırın bakiyesini ayrı ayrı yuvarlar. Örnek: K5'te Requested 10.005, A = 10, B = 40 → bacaklar (A, 10.00), (B, 0.005); B'de D1 bozulur ve bacak toplamı `Applied`'a eşit olmaz. Apply tutarı zaten doğruluyor (`LicensesCustomerBalanceApplyController.cs:319-322`, 400 `invalid-money-scale`); `DrawAsync` döngüden önce `ArgumentOutOfRangeException` atar (apply'da davranış değişmez). **Karar (iskelet fark 18l; sahip sorusu değil):** panel `manual-adjustment` (`PanelCustomerBalanceController.cs:185`) yalnız `Amount == 0`'ı reddediyor; satır kipi bugün 3 ondalığı kabul ediyor (SQL yuvarlar). Havuz kolunda bu tutar 500 olurdu. Görev 9 havuz kolunda apply'ın `:319-322` doğrulamasının eşiyle (400 `invalid-money-scale`, `DrawManualAdjustmentAsync`'ten önce) kapattı; satır kipi aynen kalır. Panel `reverse` (Görev 10) tutarı `decimal(18,2)` sütundan aldığı için etkilenmez.
- *Çağıranın açık işlemi yasak* (ön koşul, savunma). `ScopeLockResource` doluyken çağıranın işlemi varsa `BeginTransactionAsync` "connection is already in a transaction" atardı; boşken `SaveChanges` çağıranın işlemine katılır ve 1205 o işlemi bütünüyle düşürür, "baştan kur" denemesi ölü işlemde koşardı. Döngüden önce `_db.Database.CurrentTransaction is not null` → `InvalidOperationException`. Bugün hiçbir çağıran (Görev 7, 9, 10) işlem açmıyor; InMemory'de `CurrentTransaction` hep null, bu yüzden InMemory testi yok.

**Okuma sırası (komut düzeyi; Görev 14–15'in `ReadQueryHook` zincirleri buna dayanır).** WPF yolunda (`MemberIds` dolu) bir denemede: (a) `CustomerIdResolver.CanonicalOfAsync` — `FROM [WpfCustomerProjections]` (1–3 sorgu, `MergedIntoId IS NOT NULL`); (b) `W ∪ {a}`'nın varlık/silinmişlik sorgusu — `FROM [WpfCustomerProjections]`; (c) `MembersAsync`'in baş sorgusu — `FROM [WpfCustomerProjections]`; (d) gruplu ise üyeler — `FROM [WpfCustomerProjections]`; (e) izlemesiz bakiyeler — `FROM [CustomerBalances]` (denemenin 1. bakiye okuması); (5) izlenen yükleme — `FROM [CustomerBalances]` (denemenin **2.** bakiye okuması); (6) üyeliğin ikinci okuması `MembersAsync` — (c)(d)(e) yeniden; sonra `SaveChanges` (çapa satırı açılıyorsa `INSERT [CustomerBalances]`, hareketler, bakiye `UPDATE`'leri ve jeton dokunuşları tek komut grubunda). Panel yolunda (`MemberIds` null, `PersonDrawSetAsync`) (a)(b) yoktur. `beforeAttempt` bunlardan önce koşar; onun okumaları (Lookup, kapsam yüklemi: `FROM [CustomerBalanceTransactions]`) bu sayıma girmez.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDrawTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using Rule = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance.DrawAmountRule;
using Status = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance.DrawStatus;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Çok bacaklı çekim yazımı (spec 2026-10-11 §4.2 adım 1–7, §3.2, §4.8, §4.9; <see cref="PersonBalance.DrawAsync"/>):
/// para yalnız P'den, S∖P'nin eksileri havuza sayılır ve kilitlenir; satır biçimi §3.2; kişi kilidi (M6) çekilmeyen
/// izlenen satırların jetonunu ilerletir; geri çağrı her denemenin başında. InMemory: jeton çakışması, satır açma yarışı,
/// 1205, PK ve üyelik değişimi burada kanıtlanmaz (SaveChanges atomik değil, tekil indeks yok) — Testcontainers
/// (Görev 14, 15).
/// </summary>
public sealed class PersonBalanceDrawTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceDrawTests(ApiFactory factory) => _factory = factory;

    private static PersonBalance.DrawRequest Request(
        Kit kit, Guid anchor, decimal requested, Rule rule, IReadOnlyCollection<Guid>? memberIds,
        string kind = Kit.KindDeduction, Guid? key = null, decimal? originalAmount = null, string? saleScope = null,
        string? reason = null, Guid? reverses = null) =>
        new(LicenseId: kit.LicenseId, AnchorRowId: anchor, AnchorTransactionId: key ?? Guid.NewGuid(),
            AnchorIdIsIdempotencyKey: key is not null, Kind: kind, Requested: requested, Rule: rule,
            MemberIds: memberIds, OriginalAmount: originalAmount, SaleScope: saleScope, Reason: reason,
            ReversesTransactionId: reverses, CreatedByCustomerId: kit.CustomerId, ScopeLockResource: null);

    private async Task<PersonBalance.DrawResult> DrawAsync(
        PersonBalance.DrawRequest request, Func<int, CancellationToken, Task<bool>>? beforeAttempt = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PersonBalance>().DrawAsync(request, beforeAttempt, default);
    }

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId));

    [Fact]
    public async Task Ornek1_satirlari_birebir()
    {
        // Spec Örnek 1: T (sahip) = 0, I = 100, Y = 30; session:42, ProductTotal 120, Amount 120, anahtar K.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        var tBefore = (await kit.BalanceRowAsync(t))!.UpdatedAt;
        var key = Guid.NewGuid();

        var result = await DrawAsync(Request(kit, t, 120m, Rule.UpToPool, [t, i, y], key: key,
            originalAmount: 120m, saleScope: Kit.Session42));

        result.Should().BeEquivalentTo(new
        {
            Status = Status.Drawn, AnchorTransactionId = key, Applied = 120m, Pool = 130m, Legs = 3,
        });
        result.RejectedRowIds.Should().BeEmpty();

        var rows = await kit.GroupRowsAsync(key);
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.DrawGroupId == key && r.Kind == Kit.KindDeduction
            && r.CreatedAt == rows[0].CreatedAt && r.CreatedByCustomerId == kit.CustomerId);
        rows[0].Should().BeEquivalentTo(new
        {
            Id = key, WpfCustomerId = t, Amount = 0m, OriginalAmount = (decimal?)120m, SaleScope = Kit.Session42,
            Reason = (string?)null, ReversesTransactionId = (Guid?)null,
        });
        rows.Single(r => r.WpfCustomerId == i).Should().BeEquivalentTo(new
        {
            Amount = -100m, OriginalAmount = (decimal?)null, SaleScope = (string?)null, Reason = (string?)null,
            ReversesTransactionId = (Guid?)null,
        });
        rows.Single(r => r.WpfCustomerId == y).Should().BeEquivalentTo(new
        {
            Amount = -20m, OriginalAmount = (decimal?)null, SaleScope = (string?)null, Reason = (string?)null,
            ReversesTransactionId = (Guid?)null,
        });

        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m);
        (await kit.BalanceAsync(y)).Should().Be(10m);
        foreach (var row in new[] { t, i, y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        (await kit.BalanceRowAsync(t))!.UpdatedAt.Should().BeAfter(tBefore, "0 çekilen çapanın satırı da kilide girer");
    }

    [Fact]
    public async Task Capanin_bakiye_satiri_yoksa_0_ile_acilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g, balanceRow: false);
        var i = await kit.MemberAsync(g, 50m);
        (await kit.BalanceRowAsync(t)).Should().BeNull();
        var key = Guid.NewGuid();

        var result = await DrawAsync(Request(kit, t, 30m, Rule.UpToPool, [t, i], key: key));

        result.Status.Should().Be(Status.Drawn);
        result.Legs.Should().Be(2);
        var opened = await kit.BalanceRowAsync(t);
        opened.Should().NotBeNull("çapanın bakiye satırı 0 ile açılır ve kilide girer (§4.2-5)");
        opened!.Balance.Should().Be(0m);
        var rows = await kit.GroupRowsAsync(key);
        rows[0].Should().BeEquivalentTo(new { Id = key, WpfCustomerId = t, Amount = 0m });
        (await kit.BalanceAsync(i)).Should().Be(20m);
        (await kit.LedgerHoldsAsync(t)).Should().BeTrue("0'lık satır D1'i bozmaz");
        (await kit.LedgerHoldsAsync(i)).Should().BeTrue();
    }

    [Fact]
    public async Task Tek_bacakli_havuz_cekiminde_capa_DrawGroupId_kendi_Id()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        var key = Guid.NewGuid();

        var result = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a], key: key));

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 30m, Pool = 100m, Legs = 1 });
        var row = (await kit.GroupRowsAsync(key)).Should().ContainSingle().Subject;
        row.Should().BeEquivalentTo(new { Id = key, WpfCustomerId = a, Amount = -30m, DrawGroupId = (Guid?)key },
            "havuz kipinde tek bacaklı çekimin de DrawGroupId'si kendi Id'si (§3.2)");
        (await kit.BalanceAsync(a)).Should().Be(70m);
    }

    [Fact]
    public async Task Eksiler_kilitlenir_cekilmez()
    {
        // Spec §8.3: A = 100, B = −60, W = {A} → havuz 40; B'nin borcu havuzu daraltır ama B'den çekilmez.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);

        var result = await DrawAsync(Request(kit, a, 100m, Rule.UpToPool, [a]));

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 40m, Pool = 40m, Legs = 1 });
        (await kit.BalanceAsync(a)).Should().Be(60m);
        (await kit.BalanceAsync(b)).Should().Be(-60m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task UpToPool_havuz_sifir_ya_da_eksi_NoBalance_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, -60m);
        // Sınır: Σ = 0 da NoBalance'tır (WPF'in kesin "bakiye yok"u, §4.2-2), NothingToApply değil.
        var g2 = Kit.NewGroup();
        var c = await kit.MemberAsync(g2, 60m);
        var d = await kit.MemberAsync(g2, -60m);
        var before = await TransactionCountAsync(kit);

        var negative = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a, b]));
        var zero = await DrawAsync(Request(kit, c, 30m, Rule.UpToPool, [c, d]));

        negative.Should().BeEquivalentTo(new { Status = Status.NoBalance, Applied = 0m, Pool = -10m, Legs = 0 });
        zero.Should().BeEquivalentTo(new { Status = Status.NoBalance, Applied = 0m, Pool = 0m, Legs = 0 });
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(a)).Should().Be(50m);
        (await kit.BalanceAsync(c)).Should().Be(60m);
    }

    [Fact]
    public async Task Requested_kurus_hassasiyetini_asarsa_firlatir_yazim_yok()
    {
        // Ön koşul: Requested en çok 2 ondalık. decimal(18,2) bacağın Amount'unu ve satırın bakiyesini ayrı ayrı
        // yuvarlardı; kuruş altı artık başka üyenin satırına taşınır, D1 bozulurdu.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var before = await TransactionCountAsync(kit);

        var act = () => DrawAsync(Request(kit, a, 10.005m, Rule.AllOrNothing, memberIds: null, kind: Kit.KindManual,
            reason: $"elle düzeltme {Guid.NewGuid():N}"));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    [Fact]
    public async Task UpToPool_Requested_sifir_NothingToApply()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var before = await TransactionCountAsync(kit);

        var result = await DrawAsync(Request(kit, a, 0m, Rule.UpToPool, [a]));

        result.Should().BeEquivalentTo(new { Status = Status.NothingToApply, Applied = 0m, Pool = 50m, Legs = 0 });
        (await TransactionCountAsync(kit)).Should().Be(before);
    }

    [Fact]
    public async Task Kural_reddi_CustomerNotSynced_satirlariyla()
    {
        // Kural 3b: S'de olup W'de olmayan pozitif canlı üye.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);
        var before = await TransactionCountAsync(kit);

        var result = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a]));

        result.Should().BeEquivalentTo(new { Status = Status.CustomerNotSynced, Applied = 0m, Legs = 0 });
        result.RejectedRowIds.Should().Equal(b);
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    [Fact]
    public async Task AllOrNothing_K5_ornegi()
    {
        // Spec §4.8: A = 10, B = 40, rota A, "−30 elle düzeltme" → çapa (A, −10, Reason), bacak (B, −20).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var reason = $"elle düzeltme {Guid.NewGuid():N}";
        var request = Request(kit, a, 30m, Rule.AllOrNothing, memberIds: null, kind: Kit.KindManual, reason: reason);

        var result = await DrawAsync(request);

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 30m, Pool = 50m, Legs = 2 });
        var rows = await kit.GroupRowsAsync(request.AnchorTransactionId);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Kind == Kit.KindManual && r.DrawGroupId == request.AnchorTransactionId);
        rows[0].Should().BeEquivalentTo(new { WpfCustomerId = a, Amount = -10m, Reason = reason });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = b, Amount = -20m, Reason = (string?)null });
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(20m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task AllOrNothing_havuz_yetmezse_Insufficient_yazim_yok()
    {
        // Panelde P = S: C'nin eksisi Draw'da, havuz 10 + 40 − 20 = 30 < 40 → kısmi ayar yok.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var c = await kit.MemberAsync(g, -20m);
        var before = await TransactionCountAsync(kit);

        var result = await DrawAsync(Request(kit, a, 40m, Rule.AllOrNothing, memberIds: null, kind: Kit.KindManual,
            reason: $"elle düzeltme {Guid.NewGuid():N}"));

        result.Should().BeEquivalentTo(new { Status = Status.Insufficient, Applied = 0m, Pool = 30m, Legs = 0 });
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.BalanceAsync(c)).Should().Be(-20m);
    }

    [Fact]
    public async Task Cekim_turu_iptal_ReversesTransactionId_yalniz_capada()
    {
        // Spec Örnek 4: A'ya iade R (+80), sonra A'dan 50 düşüm → A = 30; B = 60. R'nin iptali (−80) havuzdan.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var r = await kit.FundAsync(a, 80m);
        await kit.SeedLegacyDeductionAsync(a, 50m, 50m);
        var b = await kit.MemberAsync(g, 60m);
        var reason = $"Reverse of {r:N}";
        var request = Request(kit, a, 80m, Rule.AllOrNothing, memberIds: null, kind: Kit.KindReversal,
            reason: reason, reverses: r);

        var result = await DrawAsync(request);

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 80m, Pool = 90m, Legs = 2 });
        var rows = await kit.GroupRowsAsync(request.AnchorTransactionId);
        rows.Should().OnlyContain(x => x.Kind == Kit.KindReversal && x.DrawGroupId == request.AnchorTransactionId);
        rows[0].Should().BeEquivalentTo(new
        {
            WpfCustomerId = a, Amount = -30m, ReversesTransactionId = (Guid?)r, Reason = reason,
        });
        rows[1].Should().BeEquivalentTo(new
        {
            WpfCustomerId = b, Amount = -50m, ReversesTransactionId = (Guid?)null, Reason = (string?)null,
        }, "çekim türü iptalde ReversesTransactionId yalnız çapada (§3.2)");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(10m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task Kisi_kilidi_cekilmeyen_satirlarin_jetonunu_ilerletir()
    {
        // M6, §4.13: P'nin satırlı bütün üyeleri (Z, 0) ve S∖P'nin eksileri (B) çekilmese de jetona dokunulur.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var z = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, -60m);
        var zBefore = (await kit.BalanceRowAsync(z))!.UpdatedAt;
        var bBefore = (await kit.BalanceRowAsync(b))!.UpdatedAt;

        var result = await DrawAsync(Request(kit, a, 10m, Rule.UpToPool, [a, z]));

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 10m, Pool = 40m, Legs = 1 });
        var zAfter = (await kit.BalanceRowAsync(z))!;
        var bAfter = (await kit.BalanceRowAsync(b))!;
        zAfter.UpdatedAt.Should().BeAfter(zBefore);
        bAfter.UpdatedAt.Should().BeAfter(bBefore);
        zAfter.Balance.Should().Be(0m);
        bAfter.Balance.Should().Be(-60m);
        (await kit.BalanceAsync(a)).Should().Be(90m);
    }

    [Fact]
    public async Task beforeAttempt_true_donerse_Stopped_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        var before = await TransactionCountAsync(kit);
        var calls = new List<int>();

        var result = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a]), (attempt, _) =>
        {
            calls.Add(attempt);
            return Task.FromResult(true);
        });

        result.Should().BeEquivalentTo(new { Status = Status.Stopped, Applied = 0m, Legs = 0 });
        calls.Should().Equal(1);
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(a)).Should().Be(100m);
    }

    [Fact]
    public async Task beforeAttempt_false_donerse_cekim_surer_ilk_deneme_1()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        var calls = new List<int>();

        var result = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a]), (attempt, _) =>
        {
            calls.Add(attempt);
            return Task.FromResult(false);
        });

        result.Status.Should().Be(Status.Drawn);
        calls.Should().Equal(1);
        (await kit.BalanceAsync(a)).Should().Be(70m);
    }

    [Fact]
    public void IsDuplicateTransactionKey_yalniz_PK_adini_tanir()
    {
        static DbUpdateException Save(int number, string message) =>
            new("kayıt düştü", SqlExceptionFactory.Create(number, message));

        PersonBalance.IsDuplicateTransactionKey(Save(2627,
                "Violation of PRIMARY KEY constraint 'PK_CustomerBalanceTransactions'. Cannot insert duplicate key in object 'dbo.CustomerBalanceTransactions'."))
            .Should().BeTrue("aynı anahtarla yarış: çapanın PK'sı");
        PersonBalance.IsDuplicateTransactionKey(Save(2601,
                "Cannot insert duplicate key row in object 'dbo.CustomerBalanceTransactions' with unique index 'PK_CustomerBalanceTransactions'."))
            .Should().BeTrue();

        PersonBalance.IsDuplicateTransactionKey(Save(2601,
                "Cannot insert duplicate key row in object 'dbo.CustomerBalances' with unique index 'IX_CustomerBalances_LicenseId_WpfCustomerId'."))
            .Should().BeFalse("bakiye satırı açma yarışı yeniden denenir, oynatılmaz");
        PersonBalance.IsDuplicateTransactionKey(Save(2601,
                "Cannot insert duplicate key row in object 'dbo.CustomerBalanceTransactions' with unique index 'IX_CustomerBalanceTransactions_ReversesTransactionId'."))
            .Should().BeFalse("N01 ihlali already-reversed'tir");
        PersonBalance.IsDuplicateTransactionKey(Save(547, "'PK_CustomerBalanceTransactions'"))
            .Should().BeFalse("numara da tutmalı");
        PersonBalance.IsDuplicateTransactionKey(new DbUpdateException("iç istisna yok")).Should().BeFalse();
    }
}
```

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawTests"`
Expected: FAIL — derleme hatası: `PersonBalance.DrawAmountRule`, `PersonBalance.DrawStatus`, `PersonBalance.DrawRequest`, `PersonBalance.DrawResult`, `DrawAsync` ve `IsDuplicateTransactionKey` yok (CS0426 / CS1061 / CS0117).

- [ ] **Adım 3: `using`, sınıf belgesi ve PK adı**

PK adı göçten doğrulandı: `OrderDeck.LicenseServer/Data/Migrations/20260602125823_AddCustomerBalance.cs:58` → `table.PrimaryKey("PK_CustomerBalanceTransactions", x => x.Id);` (EF'in varsayılan adıyla da aynı).

`PersonBalance.cs` `using` bloğu (`:1-4`) — bugün:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
```

şununla değiştir:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;
```

Sınıf belgesinin S1/S2 paragrafı — Görev 1'den sonraki hâli (master'da `:12-15`; Görev 1 Adım 7 son satırı iki satıra
böldü ve `IsPoolModeAsync`'i ekledi, Görev 2 ve 3 bu paragrafa dokunmaz):

```csharp
/// <para><b>S1 (bu sınıf):</b> kişi (<see cref="MembersAsync"/>), çekim grubu okuyucuları (<see cref="IsAnchor"/>,
/// <see cref="AnchorOfAsync"/>, <see cref="GroupSumAsync"/>), grup geri alması ve toplanmış liste. S1 kip bayrağını
/// OKUMAZ ve çok bacaklı çekim YAZMAZ — para davranışı değişmez; S2'nin biçimini yalnız okur ve geri alır
/// (genişlet/daralt). <b>S2:</b> <see cref="IsPoolModeAsync"/> (kip bayrağını okur), <c>ResolveDrawSetAsync</c>,
/// <c>PlanDraw</c>, <c>DrawAsync</c>.</para>
```

şununla değiştir:

```csharp
/// <para><b>S1:</b> kişi (<see cref="MembersAsync"/>), çekim grubu okuyucuları (<see cref="IsAnchor"/>,
/// <see cref="AnchorOfAsync"/>, <see cref="GroupSumAsync"/>), grup geri alması ve toplanmış liste; S2'nin biçimini
/// okur ve geri alır (genişlet/daralt). <b>S2:</b> kip bayrağı (<see cref="IsPoolModeAsync"/>), çekim kümesi
/// (<see cref="ResolveDrawSetAsync"/>, <see cref="PersonDrawSetAsync"/>), plan (<see cref="PlanDraw"/>) ve çok bacaklı
/// çekim yazımı (<see cref="DrawAsync"/>; kişi kilidi, üyeliğin ikinci okuması, baştan kurma). <see cref="DrawAsync"/>'i
/// yalnız havuz kipindeki uçlar çağırır: kip kapalı lisansta para davranışı S1'deki gibidir.</para>
```

Sabitler — bugün `:26`:

```csharp
    private const string BalanceRowIndex = "IX_CustomerBalances_LicenseId_WpfCustomerId";
```

şununla değiştir:

```csharp
    private const string BalanceRowIndex = "IX_CustomerBalances_LicenseId_WpfCustomerId";

    /// <summary>Hareketin PK'sı (göç <c>20260602125823_AddCustomerBalance</c>): çapanın Id'si istemcinin anahtarıdır.</summary>
    private const string TransactionPrimaryKey = "PK_CustomerBalanceTransactions";
```

- [ ] **Adım 4: `IsDuplicateTransactionKey`**

"Hata tanıma" bölümünde `IsDuplicateBalanceRow` (bugün `:369-373`):

```csharp
    /// <summary>Bakiye satırı açma yarışı: tekil <c>(LicenseId, WpfCustomerId)</c> indeksi.</summary>
    public static bool IsDuplicateBalanceRow(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains(BalanceRowIndex, StringComparison.Ordinal);
```

şununla değiştir (yöntem aynen kalır, ardına yenisi):

```csharp
    /// <summary>Bakiye satırı açma yarışı: tekil <c>(LicenseId, WpfCustomerId)</c> indeksi.</summary>
    public static bool IsDuplicateBalanceRow(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains(BalanceRowIndex, StringComparison.Ordinal);

    /// <summary>
    /// Aynı anahtarla yarış: hareketin PK'sı (çapa = istemcinin anahtarı). Genel <c>DbUpdateException</c> süzgeci
    /// yetmez: havuz yazımında bakiye satırı açma yarışı ve N01 ihlali de <c>DbUpdateException</c>'dır, onlar oynatmaya
    /// değil yeniden denemeye ve <c>already-reversed</c>'e gider (plan fark 9).
    /// </summary>
    public static bool IsDuplicateTransactionKey(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains(TransactionPrimaryKey, StringComparison.Ordinal);
```

- [ ] **Adım 5: `DrawAsync`**

Bugünkü şu satırın (`:72`; Görev 2 ve 3'ün bölümleri bunun ÖNÜNDE durur) hemen önüne ekle:

```csharp
    // ── Çekim grupları (§2, §3.2) ────────────────────────────────────────────
```

Eklenecek blok (sonunda boş satır):

```csharp
    // ── Çekim (§4.2, §4.8, §4.9, §4.13) ──────────────────────────────────────

    /// <summary>
    /// Tutar kuralı. <c>UpToPool</c> — WPF apply (ve ileride banka): <c>Σ ≤ 0</c> → <c>NoBalance</c>,
    /// <c>applied = min(Requested, Σ)</c>. <c>AllOrNothing</c> — panel K5 (elle eksi, artı hareketin iptali):
    /// <c>max(0, Σ) &lt; Requested</c> → <c>Insufficient</c>, kısmi çekim yok (spec §4.8, §4.9).
    /// </summary>
    public enum DrawAmountRule { UpToPool, AllOrNothing }

    /// <summary>Bir çekim (spec §4.2 adım 5, §3.2 satır biçimleri; K5 §4.8, §4.9).</summary>
    /// <param name="AnchorRowId">Çapanın satırı (a), asıl kayıt: WPF'te istekteki satır, panelde rota satırı ya da
    /// iptal edilen hareketin satırı.</param>
    /// <param name="AnchorTransactionId">Çapanın Id'si = grubun <c>DrawGroupId</c>'si (tek bacaklıda da).</param>
    /// <param name="AnchorIdIsIdempotencyKey">true: Id istemcinin anahtarı; PK ihlali <c>DuplicateKey</c> döner, çağıran
    /// kazananı oynatır. false: sunucunun ürettiği Id; PK ihlali yakalanmaz.</param>
    /// <param name="Requested">Çekilecek tutar, &gt; 0 ve en çok 2 ondalık (apply: <c>min(Amount, ProductTotal)</c>,
    /// ikisi de <c>invalid-money-scale</c> ile doğrulanmış; K5: <c>|tutar|</c>). Kuruş altı tutar
    /// <see cref="ArgumentOutOfRangeException"/> atar: <c>decimal(18,2)</c> bacağın <c>Amount</c>'unu ve satırın
    /// bakiyesini ayrı ayrı yuvarlar, D1 bozulurdu.</param>
    /// <param name="MemberIds">WPF'in üye listesi (W): dolu → <see cref="ResolveDrawSetAsync"/>; null →
    /// <see cref="PersonDrawSetAsync"/> (panel, P = S).</param>
    /// <param name="OriginalAmount">Yalnız çapaya yazılır (§3.2, D4); <paramref name="SaleScope"/>,
    /// <paramref name="Reason"/> ve <paramref name="ReversesTransactionId"/> da öyle.</param>
    /// <param name="ScopeLockResource">Dolu → her deneme kendi işlemini açar ve <c>SalesCommandLock</c>'u alır (kapsam
    /// kuralı, satış senkronu §6.6; plan fark 7). <c>SalesCommandRejection</c> çağırana yükselir.</param>
    public sealed record DrawRequest(
        Guid LicenseId, Guid AnchorRowId, Guid AnchorTransactionId, bool AnchorIdIsIdempotencyKey, string Kind,
        decimal Requested, DrawAmountRule Rule, IReadOnlyCollection<Guid>? MemberIds, decimal? OriginalAmount,
        string? SaleScope, string? Reason, Guid? ReversesTransactionId, Guid CreatedByCustomerId, string? ScopeLockResource);

    /// <summary>
    /// <c>Stopped</c>: geri çağrı durdurdu (ör. Lookup kaydı buldu). <c>NoBalance</c>: kural 1'in kesin kolu ya da
    /// UpToPool'da <c>Σ ≤ 0</c>. <c>CustomerNotSynced</c>: kümenin kural 1/3a/3b reddi ya da kural 3b'nin izlenen
    /// satırlarla taze denetimi (S∖P'nin eksi üyesi artıya geçmiş). <c>AlreadyReversed</c>: N01 ihlali (çekim türü iptal). <c>DuplicateKey</c>: aynı
    /// anahtarla yarış (PK). <c>Busy</c>: <see cref="MaxAttempts"/> denemede kurulamadı (503 <c>balance-busy</c>).
    /// </summary>
    public enum DrawStatus
    {
        Drawn, Stopped, NoBalance, NothingToApply, Insufficient, CustomerNotSynced, AlreadyReversed, DuplicateKey, Busy,
    }

    /// <param name="Applied">Bütün bacakların toplamı (&gt; 0 yalnız <c>Drawn</c>'da).</param>
    /// <param name="Pool">Planın Σ'sı, izlenen satırlardan, eksiler dahil (§4.0-4); Σ kurulmadıysa 0.</param>
    /// <param name="Legs">Yazılan hareket satırı sayısı, çapa dahil (<c>Drawn</c> dışında 0).</param>
    /// <param name="RejectedRowIds">Yalnız <c>CustomerNotSynced</c>'te dolu (§5.5 günlüğü, Görev 5).</param>
    public sealed record DrawResult(
        DrawStatus Status, Guid AnchorTransactionId, decimal Applied, decimal Pool, int Legs, IReadOnlyList<Guid> RejectedRowIds);

    /// <summary>
    /// Havuz kipindeki bütün para çeken yazımların tek yolu (spec §4.2 adım 1–7; K5 §4.8, §4.9): WPF apply, panel elle
    /// eksi ve artı hareketin iptali (ileride banka). Para yalnız P'den çekilir; S∖P'nin eksileri havuza sayılır ve
    /// kilitlenir, çekilmez (D3). Plan <see cref="PlanDraw"/>'dan; yazım tek <c>SaveChanges</c> (§3.2: ortak
    /// <c>CreatedAt</c>, açıklama/kapsam/iptal bağı yalnız çapada, tek bacaklıda da <c>DrawGroupId = Id</c>).
    ///
    /// <para><b>Deneme</b> (her biri baştan; 2.+ denemede <c>ChangeTracker.Clear()</c>): kapsam kilidi (varsa, kendi
    /// işleminde) → <paramref name="beforeAttempt"/> (çağıranın Lookup'ı ve kapsam yüklemi; true → <c>Stopped</c>) →
    /// çekim kümesi → P ve S∖P eksilerinin bakiye satırları İZLENEN nesneler olarak (tek sorgu) → <b>üyeliğin ikinci
    /// okuması</b> (S(a) ve numara; fark → baştan, deneme sayılır — §4.2-2, §4.11, plan fark 18a) → kural 3b izlenen
    /// satırlarla (S∖P'nin eksi üyesi artıya geçtiyse <c>CustomerNotSynced</c>, §4.0-3b) → Σ izlenen satırlardan →
    /// tutar kuralı → plan → yazım. Çapanın bakiye satırı yoksa 0 ile açılır (§4.2-5, §4.12).</para>
    ///
    /// <para><b>Kişi kilidi (M6, §4.13):</b> çekilmeyen her izlenen satırın (P'nin satırlı üyeleri, S∖P'nin eksileri,
    /// 0 çekilen çapa) <c>UpdatedAt</c>'i "değişti" işaretlenir; değeri <c>StampCustomerBalanceVersions</c> ilerletir.
    /// Aynı kişiye eşzamanlı iki çekimden biri jetonda çakışır ve taze planla baştan kurulur.</para>
    ///
    /// <para><b>Baştan kurma:</b> jeton çakışması, bakiye satırı açma yarışı, kilitlenme kurbanlığı (1205, okumalar
    /// dahil — <see cref="IsDeadlock"/>) ve üyelik değişimi; en çok <see cref="MaxAttempts"/> deneme, sonra
    /// <c>Busy</c> ve TEK uyarı satırı. N01 ihlali → <c>AlreadyReversed</c>; istemci anahtarının PK ihlali →
    /// <c>DuplicateKey</c>. "Yeniden yükle, deltayı yeniden uygula" çok satırlı kayıtta yanlıştır (M12).</para>
    ///
    /// <para>Çağıran DbContext'te izlenen değişiklik bırakmamalı (izleyici denemeler arasında temizlenir) ve çağırmadan
    /// önce izleyiciye bir şey eklememeli: izlenen bir bakiye satırı yükleme sorgusunda tazelenmezdi.</para>
    ///
    /// <para>Çağıran açık bir işlem tutmamalı (tutarsa <see cref="InvalidOperationException"/>): kapsam kuralında
    /// deneme kendi işlemini açar; 1205 bütün işlemi düşürür, dıştaki bir işlemde yeniden denemek yanlış olurdu
    /// (çağıranın önceki yazımları sessizce geri alınır ya da sonraki komut 3930 alır).</para>
    /// </summary>
    /// <param name="beforeAttempt">(deneme numarası 1'den, ct): kilitten SONRA, okumalardan ÖNCE her denemede.</param>
    public async Task<DrawResult> DrawAsync(
        DrawRequest request, Func<int, CancellationToken, Task<bool>>? beforeAttempt, CancellationToken ct)
    {
        // Ön koşullar; ikisi de bugünkü hiçbir çağıranda tutmaz (apply tutarı doğrular, uçlar işlem açmaz), savunmadır.
        // InMemory'de CurrentTransaction her zaman null.
        if (_db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("DrawAsync açık bir işlem içinde çağrılamaz.");
        if (decimal.Round(request.Requested, 2) != request.Requested)
            throw new ArgumentOutOfRangeException(nameof(request), "Requested kuruş hassasiyetini aşamaz.");

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1) _db.ChangeTracker.Clear();

            // try denemenin kilidini, geri çağrısını ve OKUMALARINI da kapsar: kilitlenme kurbanı bir okuma da olabilir
            // (§4.2-6 S2 notu; S1'in grup geri almasında görüldü). Okumalar yazmaz; yeniden denemek güvenli.
            string retryReason;
            try
            {
                // Kapsam kuralı (satış senkronu §6.6) deneme başına kendi işleminde: kilitlenme kurbanı işlemi ve kilidi
                // bütünüyle düşürür, aynı işlemde yeniden denemek yanlış olurdu (plan fark 7). Commit edilmeyen işlem
                // atılınca geri alınır; kilit işleme aittir. InMemory'de işlem yok (kilit zaten no-op).
                await using var tx = request.ScopeLockResource is not null && _db.Database.IsRelational()
                    ? await _db.Database.BeginTransactionAsync(ct)
                    : null;
                if (tx is not null && request.ScopeLockResource is { } resource)
                    await SalesCommandLock.AcquireAsync(_db, resource, SalesCommandLock.DefaultTimeout, ct);

                if (beforeAttempt is not null && await beforeAttempt(attempt, ct))
                    return Rejected(DrawStatus.Stopped, request, 0m);

                var resolved = request.MemberIds is { } memberIds
                    ? await ResolveDrawSetAsync(request.LicenseId, request.AnchorRowId, memberIds, ct)
                    : new DrawSetResult(await PersonDrawSetAsync(request.LicenseId, request.AnchorRowId, ct), null);
                if (resolved.Rejection is { } rejection)
                    // Code ∈ {CodeNoBalance, CodeCustomerNotSynced} (Görev 2).
                    return rejection.Code == CodeCustomerNotSynced
                        ? new DrawResult(DrawStatus.CustomerNotSynced, request.AnchorTransactionId, 0m, 0m, 0, rejection.RowIds)
                        : Rejected(DrawStatus.NoBalance, request, 0m);
                var set = resolved.Set ?? throw new InvalidOperationException("Çekim kümesi ne küme ne ret döndü.");

                // §4.2-2: P'nin ve S∖P'nin eksi üyelerinin bakiye satırları izlenen nesneler olarak, TEK sorguda.
                // AsTracking: bağlamın varsayılanı ne olursa olsun jeton dokunuşu ancak izlenen girdide UPDATE olur.
                var ids = set.Draw.Concat(set.LockedNegatives).Select(m => m.WpfCustomerId)
                    .Append(request.AnchorRowId).Distinct().ToList();
                var rows = await _db.CustomerBalances.AsTracking()
                    .Where(b => b.LicenseId == request.LicenseId && ids.Contains(b.WpfCustomerId))
                    .ToDictionaryAsync(b => b.WpfCustomerId, ct);

                // Üyeliğin ikinci okuması (§4.2-2, §4.11): üyelik okuması ile bakiye yüklemesi arasında kaydedilen bir
                // ayırma burada görünür; bundan sonra kaydedilen ayırmayı kanca (jeton) yakalar.
                var again = await MembersAsync(request.LicenseId, request.AnchorRowId, ct);
                if (MembershipChanged(set.Person, again))
                {
                    retryReason = "üyelik değişti";
                }
                else
                {
                    // Kural 3b taze veriyle (§4.0-3b): S∖P'nin izlemesiz okumada eksi olan bir üyesi, iki okuma arasında
                    // commit eden bir iadeyle artıya geçmiş olabilir. İzlenen jeton iade sonrasınındır, yani yazım
                    // iadeden SONRA sıralanır; o sırada o para W'de görülmeyen paradır → 409, hiçbir şey yazılmaz.
                    // Yok sayıp çekmek ya da Σ ≤ 0'da kesin NoBalance dönmek WPF'e MarkNoBalance yazdırırdı. Yalnız
                    // WPF yolunda olur (panelde LockedNegatives boş). İzlenen yüklemeden SONRA gelen iadeyi jeton yakalar.
                    var nowPositive = set.LockedNegatives
                        .Where(m => rows.TryGetValue(m.WpfCustomerId, out var b) && b.Balance > 0m)
                        .Select(m => m.WpfCustomerId)
                        .OrderBy(id => id.ToString("N"), StringComparer.Ordinal)
                        .ToList();
                    if (nowPositive.Count > 0)
                        return new DrawResult(
                            DrawStatus.CustomerNotSynced, request.AnchorTransactionId, 0m, 0m, 0, nowPositive);

                    // Σ izlenen satırlardan (kümenin izlemesiz okuması bayat olabilir). S∖P'nin üyesi 0'a indiyse
                    // havuza girmez; satırı yine izlenir ve jetonu dokunulur.
                    var draw = set.Draw
                        .Select(m => m with { Balance = rows.TryGetValue(m.WpfCustomerId, out var b) ? b.Balance : 0m })
                        .ToList();
                    var locked = set.LockedNegatives
                        .Sum(m => rows.TryGetValue(m.WpfCustomerId, out var b) ? Math.Min(0m, b.Balance) : 0m);
                    var pool = draw.Sum(m => m.Balance) + locked;

                    decimal applied;
                    if (request.Rule == DrawAmountRule.UpToPool)
                    {
                        if (pool <= 0m) return Rejected(DrawStatus.NoBalance, request, pool);
                        applied = Math.Min(request.Requested, pool);
                    }
                    else
                    {
                        if (Math.Max(0m, pool) < request.Requested) return Rejected(DrawStatus.Insufficient, request, pool);
                        applied = request.Requested;
                    }
                    if (applied <= 0m) return Rejected(DrawStatus.NothingToApply, request, pool);

                    var legs = PlanDraw(request.AnchorRowId, draw, applied);
                    StageDraw(request, rows, legs, DateTimeOffset.UtcNow);
                    await _db.SaveChangesAsync(ct);
                    if (tx is not null) await tx.CommitAsync(ct);

                    if (legs.Count > 1)
                        _log.LogInformation(
                            "Ortak bakiye: çok bacaklı çekim yazıldı ({Legs} bacak, license={LicenseId}, anchor={AnchorId})",
                            legs.Count, request.LicenseId, request.AnchorTransactionId);
                    return new DrawResult(DrawStatus.Drawn, request.AnchorTransactionId, applied, pool, legs.Count, []);
                }
            }
            // Sıra önemli: DbUpdateConcurrencyException, DbUpdateException'dan türer (plan fark 9).
            catch (DbUpdateConcurrencyException)
            {
                retryReason = "jeton çakışması";
            }
            catch (DbUpdateException ex) when (IsDuplicateReversal(ex))
            {
                // Çekim türü iptalde N01 hakem: eşzamanlı ikinci iptal bütün bacaklarıyla geri alındı.
                _db.ChangeTracker.Clear();
                return Rejected(DrawStatus.AlreadyReversed, request, 0m);
            }
            catch (DbUpdateException ex) when (IsDuplicateBalanceRow(ex))
            {
                // Çapanın satırını eşzamanlı biri açtı (işlem bütünüyle geri alındı).
                retryReason = "bakiye satırı açma yarışı";
            }
            catch (DbUpdateException ex) when (request.AnchorIdIsIdempotencyKey && IsDuplicateTransactionKey(ex))
            {
                // Aynı anahtarla yarış: bütün bacaklar geri alındı; kazananı çağıran oynatır (§4.2-6).
                _db.ChangeTracker.Clear();
                return Rejected(DrawStatus.DuplicateKey, request, 0m);
            }
            // Exception, DbUpdateException değil: varsayılan yürütme stratejisi 1205'i sarar, satır okunurken gelen
            // 1205 çıplak gelir (bkz. IsDeadlock).
            catch (Exception ex) when (IsDeadlock(ex))
            {
                retryReason = "kilitlenme (1205)";
            }

            // Hiçbir şey yazılmadı; plan taze okumayla baştan kurulur (döngü başı izleyiciyi temizler).
            if (attempt >= MaxAttempts)
            {
                _db.ChangeTracker.Clear();
                // balance-busy'nin TEK uyarı satırı (denetleyiciler yalnız Debug yazar).
                _log.LogWarning(
                    "Ortak bakiye: çekim {Attempts} denemede kurulamadı, son neden: {Reason}; balance-busy (license={LicenseId}, anchor={AnchorId})",
                    MaxAttempts, retryReason, request.LicenseId, request.AnchorTransactionId);
                return Rejected(DrawStatus.Busy, request, 0m);
            }
            _log.LogInformation(
                "Ortak bakiye: çekim {Attempt}. denemede kurulamadı ({Reason}), baştan kuruluyor (license={LicenseId}, anchor={AnchorId})",
                attempt, retryReason, request.LicenseId, request.AnchorTransactionId);
        }
    }

    private static DrawResult Rejected(DrawStatus status, DrawRequest request, decimal pool) =>
        new(status, request.AnchorTransactionId, 0m, pool, 0, []);

    /// <summary>
    /// Üyeliğin ikinci okuması farklı mı: a'nın numarası (ordinal) ya da <c>(WpfCustomerId, Purged)</c> kümesi.
    /// Silinmişlik de karşılaştırılır: K3'ün silinmiş üyesi P'ye silinmişliği yüzünden girer.
    /// </summary>
    private static bool MembershipChanged(Person before, Person after) =>
        !string.Equals(before.GroupId, after.GroupId, StringComparison.Ordinal)
        || !before.Members.Select(m => (m.WpfCustomerId, m.Purged)).ToHashSet()
            .SetEquals(after.Members.Select(m => (m.WpfCustomerId, m.Purged)));

    /// <summary>
    /// Planı izleyiciye yazar (§3.2, §4.2-5): ilk bacak her zaman çapadır (<see cref="PlanDraw"/>). Çekilen satırın
    /// bakiyesi düşer; çekilmeyen her izlenen satıra jeton dokunuşu (kişi kilidi). Çapanın satırı yoksa 0 ile açılır:
    /// eklenen satır kendi değeriyle yazılır, tekil indeks yarışı <c>IsDuplicateBalanceRow</c> ile baştan kurulur.
    /// </summary>
    private void StageDraw(
        DrawRequest request, IReadOnlyDictionary<Guid, CustomerBalance> rows, IReadOnlyList<DrawLeg> legs,
        DateTimeOffset now)
    {
        if (!rows.ContainsKey(request.AnchorRowId))
            _db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = request.LicenseId, WpfCustomerId = request.AnchorRowId,
                Balance = 0m, UpdatedAt = now,
            });

        var drawn = new HashSet<Guid>();
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var isAnchor = i == 0;
            _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = isAnchor ? request.AnchorTransactionId : Guid.NewGuid(),
                LicenseId = request.LicenseId,
                WpfCustomerId = leg.WpfCustomerId,
                Amount = -leg.Amount,
                Kind = request.Kind,
                OriginalAmount = isAnchor ? request.OriginalAmount : null,
                SaleScope = isAnchor ? request.SaleScope : null,
                Reason = isAnchor ? request.Reason : null,
                ReversesTransactionId = isAnchor ? request.ReversesTransactionId : null,
                DrawGroupId = request.AnchorTransactionId,
                CreatedByCustomerId = request.CreatedByCustomerId,
                CreatedAt = now,
            });
            // Yalnız çapa 0 olabilir; > 0 çekilen satırın izlenen bakiyesi > 0'dır, yani satırı yüklüdür.
            if (leg.Amount == 0m) continue;
            var row = rows[leg.WpfCustomerId];
            row.Balance -= leg.Amount;
            row.UpdatedAt = now;
            drawn.Add(leg.WpfCustomerId);
        }

        foreach (var (id, row) in rows)
            if (!drawn.Contains(id))
                _db.Entry(row).Property(b => b.UpdatedAt).IsModified = true;
    }

```

Notlar (yürütücü için): `Person`, `Member`, `DrawSetResult`, `DrawLeg`, `CodeCustomerNotSynced`, `ResolveDrawSetAsync`, `PersonDrawSetAsync`, `PlanDraw` Görev 2–3'ten, `MembersAsync`, `MaxAttempts`, `IsDuplicateReversal`, `IsDuplicateBalanceRow`, `IsDeadlock` S1'den aynı sınıfta. `_db.Database.IsRelational()` ve `BeginTransactionAsync` `Microsoft.EntityFrameworkCore` ad alanındadır (dosyada var). `await using var tx = koşul ? await … : null` null'da hiçbir şey atmaz. Jeton dokunuşunun emsali `PersonGroupSaveHook.cs:207` (`entry.Property(b => b.UpdatedAt).IsModified = true`); damga `LicenseDbContext.cs:189` (`StampCustomerBalanceVersions`, `SaveChangesAsync` `:240`'ta çağrılır).

- [ ] **Adım 6: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawTests"`
Expected: PASS (15 test).

Run (servis/CLI + Görev 2/3 + hata tanıma): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceMembersTests|FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalanceVerifierTests|FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~PersonBalanceCommandTests|FullyQualifiedName~PersonBalanceDrawSetTests|FullyQualifiedName~PersonBalancePlanDrawTests|FullyQualifiedName~PersonBalanceDeadlockTests"`
Expected: hepsi PASS, değişmeden.

Run (WPF, satır kipi): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRuleTests|FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceGroupReversalTests"`
Expected: hepsi PASS, değişmeden (bu görev hiçbir uca bağlanmaz; satır kipi aynı).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDrawTests.cs
git commit -F - <<'MSG'
feat(balance): çok bacaklı çekimi kişi kilidiyle yazan DrawAsync'i ekle

Havuz kipindeki bütün para çeken yazımların tek yolu: para yalnız P'den
çekilir, S∖P'nin eksileri havuza sayılır ve jetonu dokunulur (D3, M6).
Bakiye satırları izlenen nesneler olarak yüklendikten sonra üyelik
yeniden okunur; fark, jeton çakışması, satır açma yarışı ve 1205'te
izleyici temizlenip plan baştan kurulur (3 deneme, sonra Busy). Kilitli
eksi üye izlenen yüklemede artıya geçtiyse kural 3b customer-not-synced
döner. Çapanın bakiye satırı yoksa 0 ile açılır; tek bacaklıda da
DrawGroupId = Id. Kuruş altı tutar ve çağıranın açık işlemi reddedilir.
Kapsam kilidi deneme başına kendi işleminde; Lookup çağıranın geri
çağrısı. Henüz hiçbir uca bağlı değil: satır kipi değişmez
(ortak bakiye §4.2, §4.8, §4.9, §4.13).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 5: Havuzlu preview + havuz sürüm kapısı + `member-ids-required` + `customer-not-synced` (spec §4.0, §4.1, §5.5, §7.2, §8.3)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs` — sabitler (`:74-76`), alanlar ve kurucu (`:78-94`), `PreviewResponse` belgesi (`:98-99`), `Preview` (`:106-128`), yeni özel yardımcılar `ClientUpdateRequired`'ın (`:727-735`) hemen ardına. Satır numaraları `b6ee847c`'ninki; Görev 1 `OwnedLicenseModeAsync`'i ve dört çağıranını değiştirdiği için birkaç satır kayar. `using OrderDeck.LicenseServer.Services.CustomerSync;` (`:8`) ve `using OrderDeck.LicenseServer.Services.Sync;` (`:9`) zaten var.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolPreviewTests.cs`

Preview, havuz kipinin ilk ucudur. Bayrak boşken (`Pool == false`) kod bugünkü satır kipi yolundan, aynı sırayla geçer; `memberIds` bağlanır ama kullanılmaz, havuz kapısı yoktur (bozuk değerin tek istisnası için fark 18f). Havuz dalının yardımcıları (`PoolClientMeetsMinimum`, `PoolClientUpdateRequired`, `MemberIdsRequired`, `CustomerNotSynced`) Görev 7'de apply da kullanacak; bu görev onları preview'la birlikte tanımlar. Kurucuya `IOptions<PersonBalanceOptions>` eklenir (Görev 1 DI'a kaydetti). Göç yok.

**Not (spec ↔ kod) — bu görevde uygulanan iskelet kararları:**
- **Fark 3 (sıra):** havuz kipinde satış senkronu kapısı (mod On, yerinde) → havuz sürüm kapısı → `memberIds` → `P`. Sürüm, `member-ids-required`'dan ÖNCE: `memberIds` göndermeyen v0.9.9 409 `client-update-required` alır (§6.2, §8.5). Test: `Eski_surum_memberIds_gondermese_de_409_client_update_required`.
- **Fark 4:** satış senkronu kapısı (`ClientMeetsMinimum`, `SalesSync:MinClientVersion`) preview'da yerinde kalır; havuz kapısı ayrı bir yöntemdir (`PoolClientMeetsMinimum`, `PersonBalance:MinClientVersion`). İkisi de aynı başlığı ve metni döner: istemci ikisini ayırt etmez.
- **Fark 18f:** `memberIds` tekrarlı sorgu parametresidir (`?memberIds=…&memberIds=…`). Yok ile boş aynı sayılır: ASP.NET verilmeyen üst düzey diziyi boş bağlar. İkisi de 400 `member-ids-required` döner. Guid'e bağlanamayan bir değer (`memberIds=` boş metin ya da bozuk Guid) `[ApiController]`'ın otomatik 400 `ValidationProblem`'ını alır, `member-ids-required` başlığını almaz. Model bağlama kip okunmadan önce koştuğu için bu 400 **satır kipinde de** döner; bugün aynı istek bilinmeyen parametre sayılıp yok sayılır ve 200 alır. Bu, kabul edilen tek satır kipi farkıdır: bugünkü WPF `memberIds` hiç göndermez, W-havuz yalnız geçerli Guid gönderir (spec §6.1-a, `PaymentRequestService.cs:1096` süzgeci GUID'li üyeler), yani sahadaki hiçbir istemcinin isteği bu yola düşmez ve para davranışı değişmez. Parametreyi `string[]?` bağlayıp Guid ayrıştırmasını havuz dalına taşımak farkı kapatırdı ama iskeletin `Preview` imzasını değiştirir ve apply'ın gövde alanıyla (Görev 6'nın `ApplyRequest.MemberIds`'i; `[ApiController]` + `[FromBody]` altında JSON'da bozuk Guid de System.Text.Json hatasıyla satır kipinde 400 alır — apply tarafı Görev 6'nın `Satir_kipinde_memberIds_okunmaz_bozugu_400` testiyle kilitlidir) tutarsız kalırdı; seçilmedi. Fark `Satir_kipinde_memberIds_yok_sayilir_PersonPool_false`'un `memberIds=bozuk` koluyla kilitlenir.
- **Fark 18g (`UpdatedAt`, M9):** `P`'nin son para hareketinin anı (`PersonBalance.LastMovementAtAsync`). Hareket yoksa `P`'nin bakiye satırlarının en yeni `UpdatedAt`'i, o da yoksa şimdi. `S∖P`'nin kilitli eksileri `Balance`'a girer ama `UpdatedAt`'e girmez.
- **Fark 18i (§5.5):** 409 `customer-not-synced` günlüğe Information düzeyinde yazılır: lisans ve satır Id'leri ("N", virgülle), ad ve telefon yok. 409 gövdesine uzantı eklenmez.
- **Fark 18j (§7.2):** havuz kipinde `PersonBalance:MinClientVersion` boş ya da bozuksa güncel istemci de 409 `client-update-required` alır ve günlüğe **Error** yazılır (açılıştaki tek Warning Görev 1'de). Alt sınır doluyken eski, ayrıştırılamayan ya da olmayan sürüm Information düzeyinde, lisans ve ham sürümle (`yok`) yazılır.
- **Sahip kararı 1 (29.10, (b); iskelet fark 19):** kural 1'in kesin kolu (ne `a` ne `W`'nin bir Id'si sunucuda) preview'da **200 `Balance = 0`, `PersonPool = true`** döner; spec §4.0-1'in 409 `no-balance`'ı yalnız apply'da kalır. Yanıt, satır kipinin "satır yoksa 0" dalıyla (bugünkü `Preview`, `:127`: `new PreviewResponse(wpfCustomerId, 0m, DateTimeOffset.UtcNow)`) aynı biçimde kurulur; tek farkı `PersonPool: true`. Gerekçe: bugünkü WPF (`OrderDeck.App/Services/PaymentRequestService.cs:1397-1420`) preview'ın her hatasını genel `catch`'le yutup mesajı engeller, `MarkNoBalance`'ı yalnız önizleme bakiyesi ≤ 0 iken yazar; 200 `Balance = 0` ile hem bugünkü hem W-havuz WPF istemci değişikliği olmadan "bakiye yok"a gider, spec §6.1-b'ye eşleme eklemek gerekmez. Dal yalnız `PoolPreviewAsync`'in ret eşlemesinde, beklentisi `Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir` testinde durur; apply'ın aynı kurulumda hâlâ 409 `no-balance` döndüğünü Görev 7'nin `Kural1_kesin_409_no_balance` testi kilitler.
- **Bu göreve özgü küçük not:** `CanonicalOfAsync`, havuz kapısından önce koşar (iskeletin akışı). Bu bakiye değil projeksiyon okumasıdır ve yanıtı etkilemez.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolPreviewTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye S2, havuzlu preview (spec 2026-10-11 §4.0, §4.1, §8.3; InMemory). Havuz kipinde sıra: satış senkronu
/// kapısı → havuz sürüm kapısı (<c>PersonBalance:MinClientVersion</c>) → <c>memberIds</c> → P ve kural 1–4 (plan
/// fark 3). <c>Balance = max(0, Σ)</c>, <c>UpdatedAt</c> P'nin son para hareketi (M9, fark 18g),
/// <c>PersonPool = true</c>, yanıttaki Id istekteki Id. Bayrak boşken preview bugünkü gibi; <c>memberIds</c>
/// bağlanır ama kullanılmaz (bozuk değer 400, fark 18f). <see cref="LogApiFactory"/>, <c>PersonBalance:MinClientVersion</c>'ı tanımsız sunucudur (fark 18j).
/// Kural 1–4'ün servis düzeyindeki tablosu <c>PersonBalanceDrawSetTests</c>'te; burada uçtaki eşlemeleri.
/// </summary>
public sealed class PersonBalancePoolPreviewTests
    : IClassFixture<PersonBalanceConfiguredApiFactory>, IClassFixture<LogApiFactory>
{
    private const string ClientUpdateRequired = "client-update-required";
    private const string CustomerNotSynced = "customer-not-synced";

    private static readonly string ControllerCategory = typeof(LicensesCustomerBalanceApplyController).FullName!;

    private readonly PersonBalanceConfiguredApiFactory _configured;
    private readonly LogApiFactory _unconfigured;

    public PersonBalancePoolPreviewTests(PersonBalanceConfiguredApiFactory configured, LogApiFactory unconfigured)
    {
        _configured = configured;
        _unconfigured = unconfigured;
    }

    // ── Satır kipi ────────────────────────────────────────────────────────────

    /// <summary>
    /// Bayrak boşken preview bugünkü gibi (R20): <c>memberIds</c> bağlanır ama kullanılmaz (bilinmeyen Id ret
    /// doğurmaz), bakiye istenen satırınki, <c>PersonPool</c> false. Havuz kapısı da yok: eski sürüm üye listesi
    /// göndermeden 200 alır. Tek kabul edilen fark (fark 18f): Guid'e bağlanamayan <c>memberIds</c> model
    /// bağlamada, kip okunmadan 400 <c>ValidationProblem</c> alır; <c>member-ids-required</c> başlığını almaz.
    /// </summary>
    [Fact]
    public async Task Satir_kipinde_memberIds_yok_sayilir_PersonPool_false()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 30m);
        await kit.MemberAsync(g, 40m);

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [Guid.NewGuid()]));

        preview.WpfCustomerId.Should().Be(a);
        preview.Balance.Should().Be(30m);
        preview.PersonPool.Should().BeFalse();
        (await kit.PreviewAsync(a, null, Kit.OldUserAgent)).StatusCode.Should().Be(HttpStatusCode.OK,
            "satır kipinde havuz sürüm kapısı ve memberIds zorunluluğu yok");

        var malformed = await kit.SendAsync(HttpMethod.Get,
            $"/api/v1/licenses/{kit.LicenseId}/customer-balance/preview?wpfCustomerId={a}&memberIds=bozuk");
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest, await malformed.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(malformed)).Should().NotBe("member-ids-required",
            "bozuk Guid kipten bağımsız model bağlama hatasıdır, havuz kipinin üye listesi kuralı değil");
    }

    // ── Havuz kipi: P ve kural 1–4 ──────────────────────────────────────────

    /// <summary>
    /// Spec §8.3: S∖W'de yalnız sıfır ya da eksi üye varken kesişimden çekilir, eksiler Σ'ya eklenir. A = 100,
    /// B = −60, W = {A} → kullanılabilir 40.
    /// </summary>
    [Fact]
    public async Task Havuz_kipinde_kesisim_ve_eksiler_A100_B_eksi60_W_A_40()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = a, Balance = 40m, PersonPool = true });
    }

    /// <summary>Spec §4.1 örneği: A = 50, B = −60 → Σ = −10 → <c>Balance = 0</c> (satır kipinde A'nın 50'si).</summary>
    [Fact]
    public async Task Ornek_4_1_A50_B_eksi60_Balance_0()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));

        preview.Balance.Should().Be(0m);
        preview.PersonPool.Should().BeTrue();
    }

    /// <summary>
    /// Kopya Id'si (A5b): istek ve üye listesindeki kopya asıl kayda çözülür, havuz asıl kaydın kişisinden okunur;
    /// yanıttaki Id istekteki (kopyanın) Id'sidir (§4.1).
    /// </summary>
    [Fact]
    public async Task Kopya_Id_ile_istenince_yanit_istekteki_Id()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, 20m);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: a);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(copy, [copy, b]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = copy, Balance = 70m, PersonPool = true });
    }

    /// <summary>
    /// Kural 1, kesin kol: ne a ne W'nin bir Id'si sunucuda → preview 200 <c>Balance = 0</c>, <c>PersonPool = true</c>
    /// (sahip kararı 1, 29.10 (b); iskelet fark 19). Satır kipinin "satır yoksa 0" emsali; bugünkü WPF bakiye ≤ 0'ı
    /// "bakiye yok" yazar, 409'u ise yutup mesajı engellerdi. Spec §4.0-1'in 409 <c>no-balance</c>'ı apply'da kalır:
    /// aynı kurulumu Görev 7'nin <c>Kural1_kesin_409_no_balance</c> testi apply'la sınar.
    /// </summary>
    [Fact]
    public async Task Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir()
    {
        var kit = await Kit.CreateAsync(_configured);
        await kit.EnablePoolAsync();
        var unsynced = Guid.NewGuid();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(unsynced, [Guid.NewGuid()]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = unsynced, Balance = 0m, PersonPool = true });
    }

    /// <summary>Kural 1, öbür kol: a sunucuda yok ama W'nin bir üyesi var → 409 <c>customer-not-synced</c> (K2).</summary>
    [Fact]
    public async Task Kural1_a_yok_ama_uye_var_409_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_configured);
        var b = await kit.MemberAsync(Kit.NewGroup(), 20m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(Guid.NewGuid(), [b]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, CustomerNotSynced);
    }

    /// <summary>Kural 2: sunucuda projeksiyonu olmayan W Id'si atılır; ret yok.</summary>
    [Fact]
    public async Task Kural2_bilinmeyen_W_idsi_atilir_200()
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(Kit.NewGroup(), 30m);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, Guid.NewGuid()]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = a, Balance = 30m, PersonPool = true });
    }

    /// <summary>Kural 3a: W'de olup S(a)'da olmayan canlı üye (başka kişide) → 409 <c>customer-not-synced</c>.</summary>
    [Fact]
    public async Task Kural3a_W_de_olup_S_de_olmayan_canli_uye_409_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(Kit.NewGroup(), 10m);
        var x = await kit.MemberAsync(Kit.NewGroup());
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a, x]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, CustomerNotSynced);
    }

    /// <summary>
    /// Kural 3b: S(a)'da olup W'de olmayan, bakiyesi &gt; 0 canlı üye → 409 <c>customer-not-synced</c>. Kesişimle
    /// çekmek o parayı görmezden gelirdi (önizleme 0, WPF <c>MarkNoBalance</c> yazardı).
    /// </summary>
    [Fact]
    public async Task Kural3b_S_de_olup_W_de_olmayan_pozitif_uye_409_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, CustomerNotSynced);
    }

    /// <summary>
    /// Silinmiş üyeler W'den değil S'den alınır (K3): kişinin silinmiş üyesi P (40) W'de olmasa da havuzdadır;
    /// W'deki başka kişinin silinmiş üyesi Z yok sayılır (ne ret ne para).
    /// </summary>
    [Fact]
    public async Task Silinmis_uye_S_den_alinir_W_deki_silinmis_yok_sayilir()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        await kit.MemberAsync(g, 40m, purged: true);
        var z = await kit.MemberAsync(Kit.NewGroup(), 25m, purged: true);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, z]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = a, Balance = 40m, PersonPool = true });
    }

    // ── Havuz kipi: sürüm kapısı ve memberIds ────────────────────────────────

    /// <summary>
    /// R16: havuz kipinde <c>PersonBalance:MinClientVersion</c>'dan (0.9.11) eski, ön sürüm ekli, ayrıştırılamayan ya
    /// da hiç olmayan sürüm → 409 <c>client-update-required</c>; günlüğe lisans ve ham sürüm (yoksa "yok"),
    /// Information düzeyinde. Satış senkronu mod Off: bu 409 havuz kapısının.
    /// </summary>
    [Theory]
    [InlineData(Kit.OldUserAgent, "0.9.10")]
    [InlineData("OrderDeck-WPF/0.9.11-rc1", "0.9.11-rc1")]
    [InlineData("OrderDeck-WPF/bozuk", "bozuk")]
    [InlineData(null, "yok")]
    public async Task Eski_surum_havuz_kipinde_409_client_update_required(string? userAgent, string loggedVersion)
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a], userAgent);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, ClientUpdateRequired);
        _configured.Log.Entries.Should().Contain(e => e.Category == ControllerCategory
            && e.Level == LogLevel.Information
            && e.Message.Contains(kit.LicenseId.ToString(), StringComparison.Ordinal)
            && e.Message.Contains($"sürümü {loggedVersion} alt", StringComparison.Ordinal));
    }

    /// <summary>
    /// Karşılaştırma <c>System.Version</c> ile (§7.2): "0.9.100" metin sırasında "0.9.11"den küçük görünür ama
    /// geçer; "+" sonrası derleme bilgisi sıralamaya girmez.
    /// </summary>
    [Theory]
    [InlineData("OrderDeck-WPF/0.9.100")]
    [InlineData("OrderDeck-WPF/0.9.11+abc")]
    [InlineData("OrderDeck-WPF/1.0.0")]
    public async Task Guncel_surum_havuz_kipinde_200_surum_sayisal_karsilastirilir(string userAgent)
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a], userAgent));

        preview.PersonPool.Should().BeTrue();
    }

    /// <summary>
    /// Plan fark 3: sürüm denetimi <c>memberIds</c>'ten önce. Üye listesi göndermeyen eski sürüm (sahadaki v0.9.9 gibi)
    /// 400 değil 409 <c>client-update-required</c> alır; §6.2 ve §8.5'in saha denemesi bunu bekler.
    /// </summary>
    [Fact]
    public async Task Eski_surum_memberIds_gondermese_de_409_client_update_required()
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, null, Kit.OldUserAgent);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, ClientUpdateRequired);
    }

    /// <summary>Havuz kipinde güncel sürüm üye listesi göndermezse 400 <c>member-ids-required</c> (R17).</summary>
    [Fact]
    public async Task memberIds_yok_400_member_ids_required()
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, null);

        await ShouldBeProblemAsync(resp, HttpStatusCode.BadRequest, "member-ids-required");
    }

    /// <summary>
    /// Fark 18j (§7.2 kapalı-hata): alt sınır tanımsız sunucuda havuz kipindeki lisansın güncel istemcisi de 409
    /// <c>client-update-required</c> alır ve günlüğe lisanslı bir Error yazılır. Aynı sunucuda satır kipi
    /// etkilenmez.
    /// </summary>
    [Fact]
    public async Task Alt_sinir_tanimsiz_havuz_kipinde_409_ve_hata_gunlugu()
    {
        var kit = await Kit.CreateAsync(_unconfigured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, ClientUpdateRequired);
        _unconfigured.Log.Entries.Should().Contain(e => e.Category == ControllerCategory
            && e.Level == LogLevel.Error
            && e.Message.Contains(kit.LicenseId.ToString(), StringComparison.Ordinal));

        await kit.DisablePoolAsync();
        (await OkPreviewAsync(await kit.PreviewAsync(a, null))).Balance.Should().Be(30m);
    }

    /// <summary>
    /// §5.5 / fark 18i: 409 <c>customer-not-synced</c> günlüğe lisans ve satır Id'siyle ("N") yazılır, Information
    /// düzeyinde; üyenin kullanıcı adı hiçbir günlük satırında geçmez.
    /// </summary>
    [Fact]
    public async Task customer_not_synced_gunlugu_lisans_ve_satir_Idsi_icerir()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var name = $"u{Guid.NewGuid():N}";
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m, username: name);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, CustomerNotSynced);
        var entry = _configured.Log.Entries.Should().ContainSingle(e => e.Category == ControllerCategory
            && e.Message.Contains(CustomerNotSynced, StringComparison.Ordinal)
            && e.Message.Contains(kit.LicenseId.ToString(), StringComparison.Ordinal)).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain(b.ToString("N"));
        _configured.Log.Messages.Should().NotContain(m => m.Contains(name, StringComparison.Ordinal));
    }

    // ── Havuz kipi: UpdatedAt (M9) ve kip değişimi ───────────────────────────

    /// <summary>
    /// Fark 18g: <c>UpdatedAt</c> P'nin son para hareketidir. A'nın bakiye satırının <c>UpdatedAt</c>'i daha yeni
    /// (kurulum anı) ama hareket değil; S∖P'deki eksi C'nin daha yeni hareketi Σ'ya girer (10 − 5 = 5) ama
    /// <c>UpdatedAt</c>'e girmez.
    /// </summary>
    [Fact]
    public async Task UpdatedAt_P_nin_son_para_hareketi()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g);
        await kit.MemberAsync(g, -5m);
        var at = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        await kit.FundAsync(a, 10m, at: at);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));

        preview.Balance.Should().Be(5m);
        preview.UpdatedAt.Should().Be(at);
    }

    /// <summary>Fark 18g: P'de hiç para hareketi yoksa P'nin bakiye satırlarının en yeni <c>UpdatedAt</c>'i.</summary>
    [Fact]
    public async Task UpdatedAt_hareket_yoksa_bakiye_satirlarinin_en_yenisi()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g);
        await kit.EnablePoolAsync();
        var newest = new[] { (await kit.BalanceRowAsync(a))!.UpdatedAt, (await kit.BalanceRowAsync(b))!.UpdatedAt }.Max();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));

        preview.Balance.Should().Be(0m);
        preview.UpdatedAt.Should().Be(newest);
    }

    /// <summary>Kill switch (§7.2 <c>disable</c>): bayrak boşalınca aynı istek satır kipinin bakiyesini döner.</summary>
    [Fact]
    public async Task Bayrak_kapatilinca_satir_kipine_doner()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 30m);
        var b = await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();

        var pooled = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));
        await kit.DisablePoolAsync();
        var row = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));

        pooled.Should().BeEquivalentTo(new { Balance = 70m, PersonPool = true });
        row.Should().BeEquivalentTo(new { Balance = 30m, PersonPool = false });
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static async Task<LicensesCustomerBalanceApplyController.PreviewResponse> OkPreviewAsync(
        HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.PreviewResponse>())!;
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string title)
    {
        resp.StatusCode.Should().Be(status, await resp.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(resp)).Should().Be(title);
    }
}
```

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolPreviewTests"`
Expected: FAIL. Derleme geçer: test yalnız Görev 1'in kit üyelerini ve var olan `PreviewResponse`'u kullanır. Preview bayrağı henüz okumadığı için:
- Havuz testlerinde yanıt 200, `PersonPool` false ve `Balance` istenen satırınki (ör. kesişim testi 40 yerine 100, `Kopya_Id_…` 70 yerine 50).
- `Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir` 200 `Balance = 0` alır ama `PersonPool` false'tur (true beklenir); `Kural1_a_yok_ama_uye_var_…` 409 yerine 200 `Balance = 0` alır.
- 3a, 3b ve sürüm testleri 409 yerine, `memberIds_yok_…` 400 yerine 200 alır.
- Günlük testleri satırı bulamaz.
- `UpdatedAt_…` testleri bakiye satırının zamanını görür.
- `Satir_kipinde_memberIds_yok_sayilir_PersonPool_false` yalnız son kolunda kırmızıdır: bugün `memberIds=bozuk` bilinmeyen parametre sayılıp yok sayılır, istek 400 yerine 200 alır. Öbür kolları bugün de yeşildir (koruma).

- [ ] **Adım 3: Sabit, alan ve kurucu**

Sabitler (`:74-76`), bugünkü hâli:

```csharp
    private const string CodeClientUpdateRequired = "client-update-required";
    private const string CodeScopeHasDeduction = "scope-has-deduction";
    private const string CodeBalanceBusy = "balance-busy";
```

şununla değişir (üç satır aynen kalır, yalnız dördüncüsü yeni):

```csharp
    private const string CodeClientUpdateRequired = "client-update-required";
    private const string CodeScopeHasDeduction = "scope-has-deduction";
    private const string CodeBalanceBusy = "balance-busy";
    private const string CodeMemberIdsRequired = "member-ids-required";
```

Alanlar ve kurucu (`:78-94`), bugünkü hâli:

```csharp
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly SalesSyncOptions _salesSync;
    private readonly ILogger<LicensesCustomerBalanceApplyController> _log;

    public LicensesCustomerBalanceApplyController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        IOptions<SalesSyncOptions> salesSync,
        ILogger<LicensesCustomerBalanceApplyController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _salesSync = salesSync.Value;
        _log = log;
    }
```

şununla değişir:

```csharp
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly SalesSyncOptions _salesSync;
    private readonly PersonBalanceOptions _personBalance;
    private readonly ILogger<LicensesCustomerBalanceApplyController> _log;

    public LicensesCustomerBalanceApplyController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        IOptions<SalesSyncOptions> salesSync, IOptions<PersonBalanceOptions> personBalance,
        ILogger<LicensesCustomerBalanceApplyController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _salesSync = salesSync.Value;
        _personBalance = personBalance.Value;
        _log = log;
    }
```

Denetleyici yalnız DI'dan kurulur (`new LicensesCustomerBalanceApplyController(` hiçbir yerde yok; grep doğrulandı).

- [ ] **Adım 4: `PreviewResponse` belgesi ve `Preview`**

`PreviewResponse`'un `PersonPool` belgesi (`:98-99`), bugün:

```csharp
    /// <param name="PersonPool">Ortak bakiye (spec 2026-10-11 §4.1): havuz kipinde true. S1 kip bayrağını okumaz,
    /// hep false döner — satır kipi, bakiye istenen satırınki.</param>
```

şununla değişir (kayıt aynen kalır):

```csharp
    /// <param name="PersonPool">Ortak bakiye (spec 2026-10-11 §4.1): havuz kipinde true. O zaman <c>Balance</c>
    /// kişinin havuzundan (<c>max(0, Σ)</c>), <c>UpdatedAt</c> P'nin son para hareketinden gelir (M9). Satır kipinde
    /// false: bakiye istenen satırınki.</param>
```

`Preview` (`:106-128`), Görev 1'den sonraki hâli:

```csharp
    // ── GET preview ─────────────────────────────────────────────────────────

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        Guid licenseId,
        [FromQuery] Guid wpfCustomerId,
        CancellationToken ct)
    {
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, _) = owned.Value;
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "preview");

        // Bakiye asıl kayıtta (A5b). Yanıt istemcinin gönderdiği Id'yi taşır —
        // istemci yanıtı kendi Id'siyle eşleştirebilir.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, wpfCustomerId, ct);
        var row = await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId)
            .Select(b => new PreviewResponse(wpfCustomerId, b.Balance, b.UpdatedAt, false))
            .FirstOrDefaultAsync(ct);

        return Ok(row ?? new PreviewResponse(wpfCustomerId, 0m, DateTimeOffset.UtcNow));
    }
```

şununla değişir. Satır kipinin dalı karakteri karakterine aynı kalır:

```csharp
    // ── GET preview ─────────────────────────────────────────────────────────

    /// <summary>
    /// Önizleme: istemcinin mesaja yazacağı bakiye.
    ///
    /// <para><b>Satır kipi</b> (bayrak boş): bugünkü gibi, bakiye istenen Id'nin asıl kaydının satırı; satır yoksa
    /// 0. <paramref name="memberIds"/> bağlanır ama kullanılmaz, havuz kapısı yok. Guid'e bağlanamayan değer model
    /// bağlamada, kip okunmadan <c>[ApiController]</c>'ın 400 <c>ValidationProblem</c>'ını alır; bu satır kipinde de
    /// geçerli tek farktır (plan fark 18f).</para>
    ///
    /// <para><b>Havuz kipi</b> (spec 2026-10-11 §4.0, §4.1): satış senkronu kapısından sonra havuz sürüm kapısı
    /// (<c>PersonBalance:MinClientVersion</c>; eski, ayrıştırılamayan ya da olmayan sürüm, alt sınır tanımsızsa
    /// güncel sürüm de 409 <c>client-update-required</c>), sonra <paramref name="memberIds"/> (yoksa 400
    /// <c>member-ids-required</c>), sonra P ve kural 1–4 (<see cref="PoolPreviewAsync"/>). Sürüm
    /// <c>memberIds</c>'ten ÖNCE: üye listesi göndermeyen eski sürüm (v0.9.9) de 409 <c>client-update-required</c>
    /// alır (§6.2, §8.5; plan fark 3).</para>
    /// </summary>
    /// <param name="memberIds">WPF'in kişi üyeleri (W), tekrarlı sorgu parametresi <c>memberIds</c>. Yok ile boş aynı
    /// sayılır: ASP.NET verilmeyen üst düzey diziyi boş bağlar (plan fark 18f).</param>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        Guid licenseId,
        [FromQuery] Guid wpfCustomerId,
        [FromQuery(Name = "memberIds")] Guid[]? memberIds,
        CancellationToken ct)
    {
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, pool) = owned.Value;
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "preview");

        // Bakiye asıl kayıtta (A5b). Yanıt istemcinin gönderdiği Id'yi taşır —
        // istemci yanıtı kendi Id'siyle eşleştirebilir.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, wpfCustomerId, ct);

        if (pool)
        {
            // Plan fark 3: havuz sürüm kapısı memberIds'ten önce.
            if (!PoolClientMeetsMinimum()) return PoolClientUpdateRequired(licenseId, "preview");
            if (!HasMemberIds(memberIds)) return MemberIdsRequired(licenseId, "preview");
            return await PoolPreviewAsync(licenseId, wpfCustomerId, canonicalId, memberIds!, ct);
        }

        var row = await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId)
            .Select(b => new PreviewResponse(wpfCustomerId, b.Balance, b.UpdatedAt, false))
            .FirstOrDefaultAsync(ct);

        return Ok(row ?? new PreviewResponse(wpfCustomerId, 0m, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Havuz kipinde preview (spec 2026-10-11 §4.0 kural 1–4, §4.1). P <see cref="PersonBalance.ResolveDrawSetAsync"/>'ten
    /// gelir.
    /// <list type="bullet">
    ///   <item>Kural 1'in kesin kolu (ne a ne W'nin bir Id'si sunucuda) 200 <c>Balance = 0</c>,
    ///   <c>PersonPool = true</c>: satır kipinin "satır yoksa 0" yanıtı, yalnız bayrağı farklı (sahip kararı 1,
    ///   plan fark 19). Spec §4.0-1'in 409 <c>no-balance</c>'ı yalnız apply'da.</item>
    ///   <item>Öbür retler 409 <c>customer-not-synced</c> (<see cref="CustomerNotSynced"/>).</item>
    ///   <item>Aksi hâlde <c>Balance = max(0, Σ)</c>. Σ, P'nin bakiyeleri ile S∖P'nin eksi bakiyelerinin toplamıdır
    ///   (<see cref="PersonBalance.DrawSet.Pool"/>).</item>
    /// </list>
    ///
    /// <para><c>UpdatedAt</c> (M9, plan fark 18g): P'nin son para hareketinin <c>CreatedAt</c>'i. Böylece kişi
    /// kilidinin ilerlettiği jeton "güncellendi" zamanını oynatmaz. Hareket yoksa P'nin bakiye satırlarının en yeni
    /// <c>UpdatedAt</c>'i, o da yoksa şimdi. S∖P'nin kilitli eksileri Σ'ya girer, zamana girmez. Yanıttaki Id
    /// istekteki Id'dir (kopya olabilir, §4.1).</para>
    /// </summary>
    private async Task<IActionResult> PoolPreviewAsync(
        Guid licenseId, Guid requestId, Guid canonicalId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var resolved = await _person.ResolveDrawSetAsync(licenseId, canonicalId, memberIds, ct);
        if (resolved.Rejection is { } rejection)
            return rejection.Code switch
            {
                // Kural 1'in kesin kolu (spec §4.0-1): hiçbir üye sunucuda yok → para olamaz, kesin 0.
                // Sahip kararı 1 (29.10, (b)): preview 409 değil, satır kipinin "satır yoksa 0" yanıtı
                // PersonPool = true ile; bugünkü WPF ≤ 0'ı "bakiye yok" yazar. Apply 409 no-balance kalır.
                PersonBalance.CodeNoBalance =>
                    Ok(new PreviewResponse(requestId, 0m, DateTimeOffset.UtcNow, PersonPool: true)),
                _ => CustomerNotSynced(licenseId, "preview", rejection.RowIds),
            };

        var set = resolved.Set!;
        var ids = set.Draw.Select(m => m.WpfCustomerId).ToList();
        var updatedAt = await _person.LastMovementAtAsync(licenseId, ids, ct)
            ?? await _db.CustomerBalances.AsNoTracking()
                .Where(b => b.LicenseId == licenseId && ids.Contains(b.WpfCustomerId))
                .MaxAsync(b => (DateTimeOffset?)b.UpdatedAt, ct)
            ?? DateTimeOffset.UtcNow;

        return Ok(new PreviewResponse(requestId, Math.Max(0m, set.Pool), updatedAt, PersonPool: true));
    }
```

(`a ?? await x ?? y` sağdan bağlanır: bakiye satırı sorgusu yalnız hareket yoksa koşar. `MaxAsync`, nullable seçiciyle boş kümede null döner.)

- [ ] **Adım 5: Havuz yardımcıları**

`ClientUpdateRequired`'ın (`:727-735`) hemen ardına ekle. `ClientMeetsMinimum` ve `ClientUpdateRequired` aynen kalır:

```csharp
    /// <summary>Havuz kipinde <c>memberIds</c> var mı. Yok (null) ile boş aynı: <c>a</c> her zaman W'ye eklendiği
    /// için boş liste anlamlı bir girdi değil (plan fark 18f).</summary>
    private static bool HasMemberIds(IReadOnlyCollection<Guid>? memberIds) => memberIds is { Count: > 0 };

    /// <summary>İstemci (<c>User-Agent</c>) <c>PersonBalance:MinClientVersion</c>'ı (Wh, spec §7.2) karşılıyor mu.
    /// Satış senkronu kapısıyla aynı karşılaştırma (<see cref="ClientVersion.IsAtLeast"/>, §15): kapalı-hata, sürüm
    /// bilinmiyor ya da ayrıştırılamıyorsa ya da alt sınır tanımsızsa false.</summary>
    private bool PoolClientMeetsMinimum() =>
        ClientVersion.IsAtLeast(
            ClientVersion.FromUserAgent(Request.Headers.UserAgent.ToString()),
            _personBalance.ParseMinClientVersion());

    /// <summary>
    /// Havuz kipinin 409 <c>client-update-required</c>'ı. Başlık ve metin satış senkronu kapısınınkiyle aynıdır;
    /// istemci ayırt etmez. Günlük ayırır:
    /// <list type="bullet">
    ///   <item>Alt sınır tanımsız ya da bozuksa yapılandırma hatası, <b>Error</b> (§7.2, plan fark 18j).</item>
    ///   <item>Aksi hâlde eski ya da sürümsüz istemci, Information: lisans ve ham sürüm (yoksa "yok"). Kapı
    ///   açıldıktan sonra dönen eski PC böyle görünür (§7.2 "çalışma anı koruması").</item>
    /// </list>
    /// </summary>
    private ObjectResult PoolClientUpdateRequired(Guid licenseId, string endpoint)
    {
        if (_personBalance.ParseMinClientVersion() is null)
            _log.LogError(
                "customer-balance/{Endpoint}: havuz kipinde PersonBalance:MinClientVersion tanımsız ya da "
                + "ayrıştırılamıyor, 409 client-update-required (license={LicenseId})",
                endpoint, licenseId);
        else
            _log.LogInformation(
                "customer-balance/{Endpoint}: havuz kipinde istemci sürümü {Version} alt sınırın altında ya da "
                + "bilinmiyor, 409 client-update-required (license={LicenseId})",
                endpoint, ClientVersion.RawFromUserAgent(Request.Headers.UserAgent.ToString()) ?? "yok", licenseId);
        return Problem(title: CodeClientUpdateRequired, statusCode: 409,
            detail: "Bakiye işlemi için uygulamanın güncel sürümü gerekiyor.");
    }

    /// <summary>Havuz kipinde üye listesi olmayan istek (spec §4.0): 400 <c>member-ids-required</c>. Sürüm
    /// kapısından SONRA çağrılır (plan fark 3).</summary>
    private ObjectResult MemberIdsRequired(Guid licenseId, string endpoint)
    {
        _log.LogInformation(
            "customer-balance/{Endpoint}: havuz kipinde memberIds yok, 400 member-ids-required (license={LicenseId})",
            endpoint, licenseId);
        return Problem(title: CodeMemberIdsRequired, statusCode: 400,
            detail: "Havuz kipinde kişinin üye listesi (memberIds) zorunlu.");
    }

    /// <summary>
    /// 409 <c>customer-not-synced</c> (spec §4.0 kural 1 ve 3, K2). Günlüğe lisans ve 409'a yol açan satırların Id'leri
    /// ("N", virgülle) yazılır, ad ve telefon yazılmaz (§5.5, plan fark 18i). Kalıcı bir uyuşmazlık (damgasız eski
    /// <c>GroupId</c>) bu satırlardan izlenir. Gövdeye uzantı eklenmez.
    /// </summary>
    private ObjectResult CustomerNotSynced(Guid licenseId, string endpoint, IReadOnlyList<Guid> rowIds)
    {
        _log.LogInformation(
            "customer-balance/{Endpoint}: 409 customer-not-synced (license={LicenseId}, rows={RowIds})",
            endpoint, licenseId, string.Join(",", rowIds.Select(id => id.ToString("N"))));
        return Problem(title: PersonBalance.CodeCustomerNotSynced, statusCode: 409,
            detail: "Kişinin bir üyesi sunucuya henüz ulaşmadı; senkron tamamlanınca yeniden deneyin.");
    }
```

Kullanılan var olan üyeler (worktree'de doğrulandı):
- `ClientVersion.FromUserAgent`, `RawFromUserAgent` (public, `Services/Sync/ClientVersion.cs:32`) ve `IsAtLeast`.
- `PersonBalance.LastMovementAtAsync(Guid, IReadOnlyCollection<Guid>, CancellationToken)` (`PersonBalance.cs:352`).
- `PersonBalance.Member.WpfCustomerId` (`:40`).
- `ControllerBase.Problem` (`ObjectResult`).

Görev 1 ve 2'den gelenler: `PersonBalanceOptions.ParseMinClientVersion`, `ResolveDrawSetAsync`, `DrawSet.Pool`, `DrawSetResult.Rejection`/`Set`, `DrawSetRejection.Code`/`RowIds`, `CodeNoBalance`, `CodeCustomerNotSynced` ve `OwnedLicenseModeAsync`'in `(Mode, Pool)?` dönüşü.

- [ ] **Adım 6: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolPreviewTests"`
Expected: hepsi PASS.

Run (WPF regresyonu, testler değiştirilmeden): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRuleTests|FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceGroupReversalTests|FullyQualifiedName~PersonBalanceModeTests"`
Expected: hepsi PASS. Bayrak NULL olduğu için preview birebir bugünkü gibidir (R20, R70):
- `LicensesCustomerBalanceApplyControllerTests`'in preview testleri: `Preview_returns_current_balance` `:61`, `Preview_no_balance_returns_zero` `:72`, kopya Id'si `:974`.
- `PersonBalanceWpfReadersTests.Preview_PersonPool_false_ve_bakiye_satir_kipinde_bugunku_gibi` (`:128`).
- `CustomerBalanceScopeRuleTests`'in satış senkronu kapısı testleri: mod On eski istemci preview 409 `:80-112`, alt sürüm tanımsız 409, güncel istemci preview 200 `:381`, mod Off eski istemci 200 `:44-57`. Kurucunun yeni parametresi DI'dan çözülür.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolPreviewTests.cs
git commit -F - <<'MSG'
feat(balance): havuz kipinde preview kişinin havuzunu döndürsün

Havuz kipindeki lisansta preview, kişinin havuzunu (max(0, Σ)) ve P'nin son
para hareketini (M9) döndürür; PersonPool = true, yanıttaki Id istekteki Id.
Sıra: satış senkronu kapısı, sonra PersonBalance:MinClientVersion kapısı.
Eski, sürümsüz ya da tanımsız alt sınır 409 client-update-required alır;
tanımsız alt sınır günlüğe Error yazar. Ardından memberIds gelir: yoksa 400
member-ids-required. Son olarak P'nin kuralları uygulanır: kural 1'in kesin
kolu (hiçbir üye sunucuda yok) sahip kararıyla 200 Balance = 0, PersonPool =
true döner, apply'da 409 no-balance kalır; öbür retler 409
customer-not-synced, lisans ve satır Id'leri günlüğe yazılır, ad yazılmaz
(ortak bakiye §4.0, §4.1, §5.5, §7.2). Sürüm
memberIds'ten önce geldiği için üye listesi göndermeyen v0.9.9 da 409 alır.
Bayrak boşken preview bugünkü gibi; memberIds bağlanır ama kullanılmaz.
Guid'e bağlanamayan memberIds model bağlamada, kipten bağımsız 400
ValidationProblem alır; bu kabul edilen tek satır kipi farkıdır.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 6: scope `PersonPool`, oynatmanın `RemainingBalance`'ı, `ApplyRequest.MemberIds` (spec §4.0, §4.2 "İdempotency ve A11", §4.3, §6.1-a)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs` — `ScopeResponse` belgesi (`:132-140`), `Scope` (`:183-193`), `LatestScopeDeductionAsync` (`:198-233`), `ApplyRequest` (`:297-305`), `Apply`'ın ön oynatması (`:341-359`), `ApplyOncePerScopeAsync` (`:414-422`, yalnız çağrı argümanları), `WriteDeductionAsync`'in PK dalı (`:529-530`, yalnız çağrı argümanı), `LookupAsync` (`:547-636`). Satır numaraları master `b6ee847c`'nin; Görev 1 ve 5 dosyayı değiştirdiğinden kayarlar — Edit bağlamı aşağıdaki alıntılarla bulunur.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolReadersTests.cs` (iki sınıf: `PersonBalancePoolReadersTests` mod Off, `ApiFactory`; `PersonBalancePoolReadersModeOnTests` mod On, `PersonBalanceConfiguredApiFactory`; ikisi de InMemory)

Bu görev yazım yolunu değiştirmez; yalnız iki okuyucuya kip bilgisini verir. `/scope` havuz kipindeki lisansta `PersonPool = true` döner (yüklem, çapa süzgeci ve `−Σ(grup)` S1'deki gibi). Kayıtlı anahtarın oynatması havuz kipinde `RemainingBalance` olarak o anki havuzu döner: `memberIds` varsa kural 4'ün `Σ`'sı, yoksa `Σ_S`. 409 kuralları uygulanmaz (fark 18c, `PersonBalance.PoolBalanceAsync`). `ApplyRequest` `memberIds` gövde alanını taşır; satır kipinde okunmaz. Satır kipinde iki uç da bayttan bayta bugünkü yanıtı verir; tek istisna, apply gövdesinde Guid'e bağlanamayan `memberIds`'in 400'üdür (Not 6). A11 (Foreign, bacak anahtar → 404, `sameCustomer`, `OriginalAmount`, `−Σ > Amount`) değişmez.

**Not (spec ↔ kod, bu görev):**
1. **Görev 6 → 7 arası ara durum.** Bu görevde `LookupAsync`'in havuz kolunu yalnız `Apply`'ın ön oynatması kullanır. `ApplyOncePerScopeAsync` ve `WriteDeductionAsync`'in PK dalı `pool: false` geçer; ikisi de satır kipinin yoludur. Görev 7 havuz kipini `ApplyPoolAsync`'e alır. O zamana kadar havuz kipindeki yeni anahtarlı apply hâlâ satır yolundan geçer. Bu ara durum yalnız bayrak açıkken görülür, bayrağı da yalnız `person-balance enable --apply` (Görev 12) yazar. İki görev aynı PR'da gider, deploy anında hiçbir lisans havuz kipinde olmaz; ara durum sahaya çıkmaz.
2. **§4.2 "o anki havuz (`Σ`, §4.0-4)" ↔ kurallar.** §4.0-4'ün `Σ`'sı kural 1–3'ün geçtiğini varsayar. Oynatmada ise kurallar uygulanmaz ("kayıtlı anahtar hiçbir zaman `customer-not-synced` almaz"). İskeletin fark 18c'si uygulanır: `P` kural 2 ve 4'le kurulur, kural 1 ve 3 atlanır, sonuç `max(0, Σ)` olur. Örnek: Örnek 1'in sonunda `memberIds = [T]` olsun. Bu 3b ihlalidir (Y = 10 `W` dışında), yine de 200 döner ve `RemainingBalance = Σ_P = 0` olur. WPF bu alanı okumuyor (§4.2).
3. **`personPool` yalnız yanıt bayrağıdır.** Spec §4.2, 409 `scope-has-deduction`'ın uzantılarını `{transactionId, appliedAmount, productTotal}` diye sayar; `PersonPool` orada yok. Uzantılar değişmez; `ApplyOncePerScopeAsync` yüklemi `personPool: false` ile çağırır, çünkü bayrak 409 gövdesinde kullanılmaz.
4. **`/scope`'ta havuz sürüm kapısı yok** (fark 4: havuz kapısı yalnız preview ve yeni yazım yapan apply'da). Mod On'da satış senkronu kapısı yerinde kalır, sırası değişmez. Prod'daki tek gerçek birleşim (havuz + mod On) `PersonBalancePoolReadersModeOnTests`'te sınanır: `/scope` ve kayıtlı anahtarın oynatması bu kapıdan sonra havuzu döner, eski istemci ikisinde de 409 alır.
5. `PersonBalanceWpfReadersTests`'teki `because` metni "S1 kip bayrağını okumaz" artık "satır kipinde" anlamına geliyor. Test değiştirilmez (regresyon kuralı); bayrak kapalıyken iddiası doğru kalır.
6. **Kabul edilen tek satır kipi farkı: bozuk `memberIds` apply'da da 400** (fark 18f'nin apply karşılığı; Görev 5'in fark 18f notunun preview için yazdığının aynısı). Denetleyici `[ApiController]`'dır ve `ApplyRequest` gövdeden System.Text.Json ile bağlanır. `MemberIds` alanı eklendikten sonra Guid'e bağlanamayan bir `memberIds` (`["bozuk"]` gibi Guid olmayan eleman ya da `"bozuk"` gibi dizi olmayan değer) bağlamada düşer ve kip okunmadan otomatik 400 `ValidationProblem` alır; eyleme girilmez, hiçbir şey yazılmaz. Bu **satır kipinde de** olur. Bugün aynı gövde bilinmeyen alan sayılıp yok sayılır ve 200 alır. Fark kabul edilir: bugünkü WPF alanı hiç göndermez, W-havuz yalnız geçerli Guid gönderir (spec §6.1-a), yani sahadaki hiçbir istemcinin isteği bu yola düşmez ve para davranışı değişmez. Alanı `JsonElement?` bağlayıp ayrıştırmayı havuz dalına taşımak farkı kapatırdı ama `ApplyRequest.MemberIds`'in türünü ve Görev 7'nin kullandığı `IReadOnlyList<Guid>?` sözleşmesini değiştirirdi; preview tarafıyla da tutarsız kalırdı. Seçilmedi. Fark `Satir_kipinde_memberIds_okunmaz_bozugu_400`'ün bozuk değerli koluyla kilitlenir.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolReadersTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye S2, WPF okuyucularının havuz kipi (spec 2026-10-11 §4.2 "İdempotency ve A11", §4.3).
/// <list type="bullet">
///   <item><c>/scope</c> havuz kipinde <c>PersonPool = true</c> döner; yüklem ve <c>−Σ</c> S1'deki gibi, çapa düzeyinde.</item>
///   <item>Kayıtlı anahtarın oynatması <c>RemainingBalance</c> olarak o anki havuzu verir: <c>memberIds</c> varsa
///   kural 4'ün Σ'sı, yoksa <c>S</c>'nin toplamı. 409 kuralları uygulanmaz (plan fark 18c).</item>
///   <item>Satır kipinde ikisi de bugünkü gibi; <c>memberIds</c> okunmaz. Tek fark: Guid'e bağlanamayan
///   <c>memberIds</c> apply gövdesinde kipten bağımsız 400 alır (plan Görev 6 Not 6).</item>
/// </list>
/// Satış senkronu mod Off (sürüm kapısı yok). Kayıtlar Örnek 1'in sonucu, elle kurulur; bu görevde havuzlu yazım yok.
/// </summary>
public sealed class PersonBalancePoolReadersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolReadersTests(ApiFactory factory) => _factory = factory;

    private sealed record ScopeDto(Guid TransactionId, decimal AppliedAmount, decimal ProductTotal, DateTimeOffset CreatedAt, bool PersonPool);
    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    /// <summary>Gövdede <c>personPool</c> alanı gerçekten var mı (eksik alan record'da da false okunurdu).</summary>
    private static async Task<bool> PersonPoolFieldAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("personPool").GetBoolean();
    }

    private static async Task<ApplyDto> OkApplyAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApplyDto>())!;
    }

    /// <summary>Örnek 1'in sonucu (T 0, I 0, Y 10; K çapa T'de) ve lisans havuz kipinde.</summary>
    private async Task<(Kit, Kit.Example1)> PoolExample1Async()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();
        return (kit, ex);
    }

    // ── scope (§4.3) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Havuz_kipinde_scope_PersonPool_true()
    {
        var (kit, ex) = await PoolExample1Async();

        var resp = await kit.ScopeAsync(ex.T, Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ScopeDto>()).Should().BeEquivalentTo(new
        {
            TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = true,
        }, "yüklem ve −Σ(grup) S1'deki gibi; yalnız kip bayrağı değişir (§4.3)");
        (await PersonPoolFieldAsync(resp)).Should().BeTrue("WPF birleştirme notu için kipi okur (§6.1-d)");
    }

    [Fact]
    public async Task Havuz_kipinde_baska_uyedeki_capa_scope_204()
    {
        var (kit, ex) = await PoolExample1Async();

        (await kit.ScopeAsync(ex.I, Kit.Session42)).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "kapsam kuralı havuz kipinde de çapa düzeyinde: bacak kapsam taşımaz (Örnek 1, M10)");
    }

    [Fact]
    public async Task Satir_kipinde_scope_PersonPool_false()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var resp = await kit.ScopeAsync(ex.T, Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ScopeDto>()).Should().BeEquivalentTo(new
        {
            TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = false,
        });
        (await PersonPoolFieldAsync(resp)).Should().BeFalse("bayrak boş: satır kipi (§4.0 Kip)");
    }

    // ── oynatmanın RemainingBalance'ı (§4.2 "İdempotency ve A11") ────────────

    [Fact]
    public async Task Havuz_kipinde_oynatma_RemainingBalance_havuz()
    {
        var (kit, ex) = await PoolExample1Async();

        var body = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]));

        body.Should().Be(new ApplyDto(ex.K, 120m, 10m), "oynatma o anki havuzu döner: T 0 + I 0 + Y 10 (§4.2)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m, "oynatma hiçbir şey yazmaz");
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
    }

    [Fact]
    public async Task Havuz_kipinde_memberIds_siz_oynatma_RemainingBalance_S_toplami()
    {
        var (kit, ex) = await PoolExample1Async();

        // 5 argümanlı aşırı yükleme gövdeye memberIds alanını hiç koymaz (eski WPF'in gönderdiği biçim);
        // 6 argümanlıda null → JSON'da açık "memberIds": null.
        var withoutField = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42));
        var explicitNull = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, memberIds: null));
        var emptyList = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, memberIds: []));

        withoutField.Should().Be(new ApplyDto(ex.K, 120m, 10m),
            "memberIds yoksa havuz S(a)'nın toplamı; kayıtlı anahtar member-ids-required almaz (§4.2)");
        explicitNull.Should().Be(withoutField, "açık null ile alanın yokluğu aynı sayılır");
        emptyList.Should().Be(withoutField, "yok ile boş liste aynı sayılır (plan fark 18f)");
    }

    [Fact]
    public async Task Havuz_kipinde_oynatma_kural_ihlalinde_de_200()
    {
        var (kit, ex) = await PoolExample1Async();

        // W = {T}: Y = 10 W dışında → kural 3b ihlali; oynatma yine de 200 (fark 18c).
        var onlyOwner = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T]));
        onlyOwner.Should().Be(new ApplyDto(ex.K, 120m, 0m),
            "kayıtlı anahtar customer-not-synced almaz; RemainingBalance = Σ_P = T'nin 0'ı (§4.2)");

        // I = 5 W dışında (3b ihlali), Y W'de: Σ_P = Y'nin 10'u. Satır kipinin cevabı T'nin 0'ı olurdu.
        await kit.FundAsync(ex.I, 5m);
        var ownerAndY = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.Y]));
        ownerAndY.Should().Be(new ApplyDto(ex.K, 120m, 10m),
            "Σ, kural 1 ve 3 uygulanmadan kural 4'ün P'sinden: I'nin 5'i havuza girmez");
    }

    [Fact]
    public async Task Havuz_kipinde_kopya_Id_ile_oynatma()
    {
        var (kit, ex) = await PoolExample1Async();
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: ex.T);

        var body = await OkApplyAsync(
            await kit.ApplyAsync(copy, 120m, 120m, ex.K, Kit.Session42, [copy, ex.I, ex.Y]));

        body.Should().Be(new ApplyDto(ex.K, 120m, 10m),
            "kopya Id'si asıl kayda (T) çözülür: aynı kişinin aynı isteği, havuz S(T) (A5b, §4.2)");
    }

    [Fact]
    public async Task Satir_kipinde_oynatma_RemainingBalance_asil_kaydin_bakiyesi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var body = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]));

        body.Should().Be(new ApplyDto(ex.K, 120m, 0m),
            "satır kipinde RemainingBalance bugünkü gibi asıl kaydın (T) bakiyesi; memberIds okunmaz");
    }

    /// <summary>
    /// Satır kipinde geçerli <c>memberIds</c> okunmaz. Tek kabul edilen fark (Not 6, fark 18f'nin apply karşılığı):
    /// Guid'e bağlanamayan <c>memberIds</c> (bozuk eleman ya da dizi olmayan değer) gövde bağlamasında, kip
    /// okunmadan <c>[ApiController]</c>'ın 400 <c>ValidationProblem</c>'ını alır ve hiçbir şey yazılmaz.
    /// </summary>
    [Fact]
    public async Task Satir_kipinde_memberIds_okunmaz_bozugu_400()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var key = Guid.NewGuid();

        var body = await OkApplyAsync(
            await kit.ApplyAsync(a, 30m, 30m, key, saleScope: null, memberIds: [Guid.NewGuid()]));

        body.Should().Be(new ApplyDto(key, 30m, 20m), "bilinmeyen üye Id'si satır kipinde hiçbir şeyi değiştirmez");
        var row = await kit.Sales.ReadDbAsync(db =>
            db.CustomerBalanceTransactions.AsNoTracking().SingleAsync(t => t.Id == key));
        row.WpfCustomerId.Should().Be(a);
        row.Amount.Should().Be(-30m);
        row.DrawGroupId.Should().BeNull("satır kipinde çapa tek bacaklı, DrawGroupId NULL (R02)");
        (await kit.BalanceAsync(a)).Should().Be(20m);

        foreach (object malformedMemberIds in new object[] { new[] { "bozuk" }, "bozuk" })
        {
            var badKey = Guid.NewGuid();
            var malformed = await kit.SendAsync(HttpMethod.Post,
                $"/api/v1/licenses/{kit.LicenseId}/customer-balance/apply",
                new
                {
                    wpfCustomerId = a, amount = 10m, productTotal = 10m, idempotencyKey = badKey,
                    saleScope = (string?)null, memberIds = malformedMemberIds,
                });

            malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "bozuk memberIds gövde bağlamasında, kip okunmadan düşer; satır kipinde de 400 (Not 6)");
            (await kit.Sales.ReadDbAsync(db =>
                db.CustomerBalanceTransactions.AsNoTracking().AnyAsync(t => t.Id == badKey)))
                .Should().BeFalse("bağlama hatasında eyleme girilmez, hiçbir şey yazılmaz");
        }
        (await kit.BalanceAsync(a)).Should().Be(20m, "bozuk istekler bakiyeye dokunmaz");
    }

    [Fact]
    public async Task Havuz_kipinde_A11_content_conflict_degismez()
    {
        var (kit, ex) = await PoolExample1Async();

        var resp = await kit.ApplyAsync(ex.T, 120m, 999m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("content-conflict",
            "OriginalAmount çapada karşılaştırılır; havuz kipi A11'i değiştirmez (§4.2)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }
}

/// <summary>
/// Havuz kipi + satış senkronu mod On: prod'da <c>enable</c>'dan sonra görülecek tek birleşim (lisanslar mod On'da).
/// <c>/scope</c>'ta havuz sürüm kapısı yok (plan fark 4); satış senkronu kapısı yerinde ve sırası değişmez: eski
/// istemci <c>/scope</c>'ta da kayıtlı anahtarın oynatmasında da bakiyeyi okumadan 409 <c>client-update-required</c>
/// alır (§6.5-3). Güncel istemcide <c>/scope</c> <c>PersonPool = true</c>, oynatma o anki havuzu döner.
/// </summary>
public sealed class PersonBalancePoolReadersModeOnTests : IClassFixture<PersonBalanceConfiguredApiFactory>
{
    private readonly PersonBalanceConfiguredApiFactory _factory;

    public PersonBalancePoolReadersModeOnTests(PersonBalanceConfiguredApiFactory factory) => _factory = factory;

    private sealed record ScopeDto(Guid TransactionId, decimal AppliedAmount, decimal ProductTotal, DateTimeOffset CreatedAt, bool PersonPool);
    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    /// <summary>Örnek 1'in sonucu (T 0, I 0, Y 10; K çapa T'de), satış senkronu mod On ve lisans havuz kipinde.</summary>
    private async Task<(Kit, Kit.Example1)> PoolExample1ModeOnAsync()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();
        return (kit, ex);
    }

    [Fact]
    public async Task Mod_On_havuz_kipinde_scope_PersonPool_true()
    {
        var (kit, ex) = await PoolExample1ModeOnAsync();

        var resp = await kit.ScopeAsync(ex.T, Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ScopeDto>()).Should().BeEquivalentTo(new
        {
            TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = true,
        }, "güncel istemci satış senkronu kapısını geçer; yüklem ve −Σ(grup) mod Off'takiyle aynı (§4.3)");
    }

    [Fact]
    public async Task Mod_On_havuz_kipinde_oynatma_RemainingBalance_havuz()
    {
        var (kit, ex) = await PoolExample1ModeOnAsync();

        var resp = await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ApplyDto>()).Should().Be(new ApplyDto(ex.K, 120m, 10m),
            "kapıdan sonra ön oynatma o anki havuzu döner, kapsam kuralına (§6.6) inmez (§4.2)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m, "oynatma hiçbir şey yazmaz");
    }

    [Fact]
    public async Task Mod_On_havuz_kipinde_eski_istemci_scope_ve_oynatma_409_client_update_required()
    {
        var (kit, ex) = await PoolExample1ModeOnAsync();

        var scope = await kit.SendAsync(HttpMethod.Get,
            $"/api/v1/licenses/{kit.LicenseId}/customer-balance/scope?wpfCustomerId={ex.T}&saleScope={Uri.EscapeDataString(Kit.Session42)}",
            userAgent: Kit.OldUserAgent);
        var replay = await kit.ApplyAsync(
            ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y], Kit.OldUserAgent);

        scope.StatusCode.Should().Be(HttpStatusCode.Conflict, await scope.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(scope)).Should().Be("client-update-required",
            "havuz kipinde de satış senkronu kapısı /scope'ta yerinde (plan fark 4: havuz kapısı burada yok)");
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict, await replay.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(replay)).Should().Be("client-update-required",
            "kapı bakiyenin her okumasından, oynatma dahil, önce (§6.5-3)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
    }
}
```

- [ ] **Adım 2: Koştur, FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolReaders"`

(Süzgeç `PersonBalancePoolReaders`: iki sınıfı da, `PersonBalancePoolReadersTests` ve `PersonBalancePoolReadersModeOnTests`, seçer.)

Expected: derlenir (`EnablePoolAsync`, `OldUserAgent`, 6 parametreli `ApplyAsync` ve `PersonBalanceConfiguredApiFactory` Görev 1'de). 13 testten sekizi FAIL:
- `Havuz_kipinde_scope_PersonPool_true`, `Mod_On_havuz_kipinde_scope_PersonPool_true`: `PersonPool` true bekleniyor, false geliyor (yansıtmada literal `false`).
- `Havuz_kipinde_oynatma_RemainingBalance_havuz`, `Havuz_kipinde_memberIds_siz_oynatma_RemainingBalance_S_toplami`, `Havuz_kipinde_kopya_Id_ile_oynatma`, `Mod_On_havuz_kipinde_oynatma_RemainingBalance_havuz`: `RemainingBalance` 10 bekleniyor, 0 bulunuyor (oynatma T'nin satırını okuyor).
- `Havuz_kipinde_oynatma_kural_ihlalinde_de_200`: ikinci iddiada `RemainingBalance` 10 bekleniyor, 0 bulunuyor.
- `Satir_kipinde_memberIds_okunmaz_bozugu_400`: geçerli Id'li ilk kol geçer (System.Text.Json bilinmeyen alanı yok sayar); bozuk değerli kolda 400 bekleniyor, 200 geliyor (alan henüz `ApplyRequest`'te yok, yok sayılıyor ve satır yazılıyor).

Beş test şimdiden PASS; bunlar koruma testleridir ve uygulamadan sonra da yeşil kalmalıdır: `Havuz_kipinde_baska_uyedeki_capa_scope_204`, `Satir_kipinde_scope_PersonPool_false`, `Satir_kipinde_oynatma_RemainingBalance_asil_kaydin_bakiyesi`, `Havuz_kipinde_A11_content_conflict_degismez`, `Mod_On_havuz_kipinde_eski_istemci_scope_ve_oynatma_409_client_update_required` (satış senkronu kapısı bugün de `/scope`'ta ve ön oynatmadan önce).

- [ ] **Adım 3: `ApplyRequest.MemberIds`**

`ApplyRequest`'i (`:297-305`) şu blokla değiştir. Bugünkü hâli:

```csharp
    /// <param name="SaleScope">R4-03: satışın kalıcı kimliği
    /// (<c>"session:{id}"</c> | <c>"cumulative"</c> | <c>"legacy:{key}"</c>).
    /// Opsiyonel — eski istemciler göndermez, davranışları değişmez.</param>
    public sealed record ApplyRequest(
        Guid WpfCustomerId,
        decimal Amount,
        decimal ProductTotal,
        Guid? IdempotencyKey = null,
        string? SaleScope = null);
```

Yenisi:

```csharp
    /// <param name="SaleScope">R4-03: satışın kalıcı kimliği
    /// (<c>"session:{id}"</c> | <c>"cumulative"</c> | <c>"legacy:{key}"</c>).
    /// Opsiyonel — eski istemciler göndermez, davranışları değişmez.</param>
    /// <param name="MemberIds">Ortak bakiye §4.0: havuz kipinde kişinin üye Id'leri (WPF'in bildiği hâliyle);
    /// satır kipinde okunmaz; eski istemci göndermez. Gövde alanı <c>memberIds</c>. Alanın yokluğu ile boş liste
    /// aynı sayılır (plan fark 18f).</param>
    public sealed record ApplyRequest(
        Guid WpfCustomerId,
        decimal Amount,
        decimal ProductTotal,
        Guid? IdempotencyKey = null,
        string? SaleScope = null,
        IReadOnlyList<Guid>? MemberIds = null);
```

(`ApplyRequest` yalnız bu dosyada kullanılır ve hep model bağlamayla kurulur; konumsal kuran kod yok. Varsayılan değer, alanı göndermeyen eski gövdeyi bozmaz. Alanı Guid'e bağlanamayan değerle gönderen gövde ise artık 400 `ValidationProblem` alır, satır kipinde de: Not 6.)

- [ ] **Adım 4: scope ve yüklem**

`ScopeResponse`'un `PersonPool` belgesi (`:134`). Bugünkü satır:

```csharp
    /// <param name="PersonPool">Havuz kipinde true; S1'de hep false (WPF birleştirme notunda kullanır, §6.1-d).</param>
```

Yenisi:

```csharp
    /// <param name="PersonPool">Lisans havuz kipindeyse (<c>PersonBalancePoolSince</c> dolu) true, satır kipinde
    /// false (spec 2026-10-11 §4.3). WPF birleştirme notunda kullanır (§6.1-d).</param>
```

`Scope` içinde Görev 1'den sonraki kip okuması ve yüklem çağrısı. Bugünkü hâli:

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, _) = owned.Value;
        // v0.9.10 bulduğu düşümü benimser ve toplam aynıysa önizleme ile
        // apply'ı hiç çağırmadan mesajı gönderir; kapı burada da olmalı.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "scope");

        // Düşüm asıl kayıtta (A5b: hareketler birleştirmede taşınır, yenileri
        // çözülmüş Id'yle yazılır).
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, wpfCustomerId, ct);
        var row = await LatestScopeDeductionAsync(licenseId, canonicalId, saleScope, ct);
```

Yenisi:

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, pool) = owned.Value;
        // v0.9.10 bulduğu düşümü benimser ve toplam aynıysa önizleme ile
        // apply'ı hiç çağırmadan mesajı gönderir; kapı burada da olmalı.
        // Havuz sürüm kapısı burada yok (plan fark 4): uç salt okunur.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "scope");

        // Düşüm asıl kayıtta (A5b: hareketler birleştirmede taşınır, yenileri
        // çözülmüş Id'yle yazılır). Ortak bakiye §4.3: yüklem iki kipte aynı
        // (çapa düzeyinde); havuz kipinde yalnız PersonPool = true.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, wpfCustomerId, ct);
        var row = await LatestScopeDeductionAsync(licenseId, canonicalId, saleScope, pool, ct);
```

`LatestScopeDeductionAsync`'i (`:198-233`) bütünüyle şu blokla değiştir. Yüklem, sıralama ve `−Σ(grup)` değişmez; yalnız belgenin son paragrafı ve imzadaki `personPool` eklenir, yansıtmadaki literal `false` onunla değişir:

```csharp
    /// <summary>
    /// <c>/scope</c>'un yüklemi; apply'ın kapsam kuralı (§6.6) da aynı sorguyu
    /// koşar: aynı asıl kayıt, aynı kapsam, <c>purchase-deduction</c>, geri
    /// alınmamış; en yenisi (<c>CreatedAt</c>, sonra <c>Id</c>). Tek sorgu: uç
    /// "düşüm yok" dediği hâlde kuralın 409 vermesi (ya da tersi) olamaz.
    ///
    /// <para><b>Ortak bakiye (spec 2026-10-11 §4.2, §4.3):</b> yalnız çapalar
    /// aranır (bacaklar kapsam taşımaz; süzgeç savunma) ve tutar grubun
    /// toplamıdır — 409 <c>scope-has-deduction</c>'ın <c>appliedAmount</c>'u da
    /// buradan gelir. Çapa düzeyinde kalır: aynı kişinin başka satırındaki çapa
    /// bu yüklemi tetiklemez (M10). Çapanın geri alma satırı grubun tamamını
    /// temsil eder (D5, grup tek işlemde geri alınır). Eski tek bacaklı satırda
    /// grup yalnız kendisidir; S2 çapasında <c>DrawGroupId = Id</c> olduğundan
    /// çapa toplamda bir kez sayılır.</para>
    ///
    /// <para><paramref name="personPool"/> yalnız yanıt bayrağıdır
    /// (<see cref="ScopeResponse.PersonPool"/>); yüklemi değiştirmez. 409
    /// <c>scope-has-deduction</c>'ın uzantıları onu taşımaz.</para>
    /// </summary>
    private Task<ScopeResponse?> LatestScopeDeductionAsync(
        Guid licenseId, Guid canonicalCustomerId, string saleScope, bool personPool, CancellationToken ct) =>
        _db.CustomerBalanceTransactions
            .AsNoTracking()
            .Where(t => t.LicenseId == licenseId
                && t.WpfCustomerId == canonicalCustomerId
                && t.SaleScope == saleScope
                && t.Kind == KindPurchaseDeduction
                && (t.DrawGroupId == null || t.DrawGroupId == t.Id)
                && !_db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id))
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new ScopeResponse(
                t.Id,
                -_db.CustomerBalanceTransactions
                    .Where(g => g.LicenseId == licenseId && (g.Id == t.Id || g.DrawGroupId == t.Id))
                    .Sum(g => g.Amount),
                t.OriginalAmount ?? 0m,
                t.CreatedAt,
                personPool))
            .FirstOrDefaultAsync(ct);
```

(`personPool` bir kapanış değişkenidir. EF onu sorgu parametresi olarak taşır, yansıtmada sabit gibi okunur. Satır kipinde değer `false` olduğu için SQL'in sonucu bugünküyle aynıdır.)

`ApplyOncePerScopeAsync` içinde yüklem çağrısı (`:422`). Bugünkü hâli:

```csharp
        if (await LatestScopeDeductionAsync(licenseId, canonicalId, saleScope, ct) is { } existing)
```

Yenisi:

```csharp
        // Satır kipinin yolu; yanıt bayrağı burada kullanılmaz (409 uzantıları PersonPool taşımaz).
        if (await LatestScopeDeductionAsync(licenseId, canonicalId, saleScope, personPool: false, ct) is { } existing)
```

- [ ] **Adım 5: `LookupAsync`'in havuz kolu ve çağıranlar**

`LookupAsync`'in belgesinde `RemainingBalance` paragrafını (`:561-564`) ve imzayı (`:575-578`) değiştir. Bugünkü hâli:

```csharp
    /// <para><c>RemainingBalance</c> o anki gerçek bakiyedir (donmuş bir kopya
    /// değil): istemci bunu ekranda gösteriyor, eski bir değeri oynatmak
    /// operatöre yanlış bakiye gösterirdi. <c>AppliedAmount</c> ise ledger
    /// satırından gelir — "ne kadar düştü" cevabı değişmemeli.</para>
```

Yenisi:

```csharp
    /// <para><c>RemainingBalance</c> o anki gerçek bakiyedir (donmuş bir kopya
    /// değil): istemci bunu ekranda gösteriyor, eski bir değeri oynatmak
    /// operatöre yanlış bakiye gösterirdi. <c>AppliedAmount</c> ise ledger
    /// satırından gelir — "ne kadar düştü" cevabı değişmemeli. Satır kipinde
    /// asıl kaydın bakiyesi; havuz kipinde (<paramref name="pool"/>) o anki
    /// havuz (spec 2026-10-11 §4.2): <c>memberIds</c> varsa kural 4'ün
    /// <c>Σ</c>'sı, yoksa <c>S</c>'nin toplamı, 409 kuralları uygulanmadan
    /// (plan fark 18c, <see cref="PersonBalance.PoolBalanceAsync"/>).</para>
```

Bugünkü param ve imza:

```csharp
    /// <param name="canonicalCustomerId">İstekteki müşteri Id'sinin asıl kaydı
    /// (A5b, <see cref="CustomerIdResolver"/>).</param>
    private async Task<(ApplyResponse? Replay, bool Foreign, bool ContentConflict)> LookupAsync(
        Guid licenseId, ApplyRequest req, Guid canonicalCustomerId, Guid key, CancellationToken ct)
```

Yenisi:

```csharp
    /// <param name="canonicalCustomerId">İstekteki müşteri Id'sinin asıl kaydı
    /// (A5b, <see cref="CustomerIdResolver"/>).</param>
    /// <param name="pool">Lisans havuz kipinde mi; yalnız oynatılan
    /// <c>RemainingBalance</c>'ı belirler. Foreign, A11 ve <c>−Σ</c> iki kipte aynı.</param>
    private async Task<(ApplyResponse? Replay, bool Foreign, bool ContentConflict)> LookupAsync(
        Guid licenseId, ApplyRequest req, Guid canonicalCustomerId, Guid key, bool pool, CancellationToken ct)
```

`LookupAsync`'in son kısmı (`:625-635`). Bugünkü hâli:

```csharp
        // Kalan bakiye asıl kayıttan (kopyanın satırı birleştirmede 0'lanır).
        var remaining = await _db.CustomerBalances
            .AsNoTracking()
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalCustomerId)
            .Select(b => (decimal?)b.Balance)
            .FirstOrDefaultAsync(ct) ?? 0m;

        _log.LogInformation(
            "Bakiye uygulama sonucu tekrar oynatıldı (key={Key}, license={LicenseId})", key, licenseId);
        // remaining satır kipinde bugünkü gibi asıl kaydın bakiyesi; havuz Σ'sı S2.
        return (new ApplyResponse(tx.Id, applied, remaining), false, false);
```

Yenisi:

```csharp
        // Satır kipi: kalan bakiye asıl kayıttan (kopyanın satırı birleştirmede 0'lanır).
        // Havuz kipi: o anki havuz, 409 kuralları uygulanmadan — spec §4.2, plan fark 18c;
        // kayıtlı anahtar hiçbir zaman customer-not-synced almaz.
        var remaining = pool
            ? await _person.PoolBalanceAsync(licenseId, canonicalCustomerId, req.MemberIds, ct)
            : (await _db.CustomerBalances
                .AsNoTracking()
                .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalCustomerId)
                .Select(b => (decimal?)b.Balance)
                .FirstOrDefaultAsync(ct) ?? 0m);

        _log.LogInformation(
            "Bakiye uygulama sonucu tekrar oynatıldı (key={Key}, license={LicenseId})", key, licenseId);
        return (new ApplyResponse(tx.Id, applied, remaining), false, false);
```

`Foreign` kolu (`:585-591`), `applied`/`sameCustomer`/A11 karşılaştırması (`:612-623`) ve aradaki yorumlar aynen kalır. `req.MemberIds`'in türü `IReadOnlyList<Guid>?`; `PoolBalanceAsync`'in `IReadOnlyCollection<Guid>?` parametresine doğrudan geçer. Kopya Id'leri ve bilinmeyen Id'leri `PoolBalanceAsync` çözer ya da atar (Görev 2, kural 2).

`Apply`'ın ön oynatması (`:341-359`). Görev 1'den sonraki hâli:

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, _) = owned.Value;
        // §6.5-3: bakiyenin her okumasından (oynatma dahil) önce.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "apply");

        // Kopya Id'si asıl kayda çözülür (A5b): bakiye araması, yazılan hareket
        // ve idempotency karşılaştırması asıl kayıtla.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, req.WpfCustomerId, ct);

        // Sahiplik kontrolünden SONRA bakıyoruz: anahtar başka lisansa aitse
        // çağıran onun sonucunu görmemeli.
        if (req.IdempotencyKey is { } preKey)
        {
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, preKey, ct);
```

Yenisi:

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, pool) = owned.Value;
        // §6.5-3: bakiyenin her okumasından (oynatma dahil) önce.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "apply");

        // Kopya Id'si asıl kayda çözülür (A5b): bakiye araması, yazılan hareket
        // ve idempotency karşılaştırması asıl kayıtla.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, req.WpfCustomerId, ct);

        // Sahiplik kontrolünden SONRA bakıyoruz: anahtar başka lisansa aitse
        // çağıran onun sonucunu görmemeli.
        if (req.IdempotencyKey is { } preKey)
        {
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, preKey, pool, ct);
```

(Yalnız `var (mode, pool)` ve `LookupAsync`'in `pool` argümanı değişir; yorumlar bugünkü gibi kalır — Görev 7 Adım 3 bu bloğu birebir alıntılar. Blok devamı, `if (foreign) …` ile kapsam dalı ve `WriteDeductionAsync` çağrısı, aynen kalır.)

`ApplyOncePerScopeAsync` içinde (`:414-416`). Bugünkü hâli:

```csharp
        if (req.IdempotencyKey is { } key)
        {
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, key, ct);
```

Yenisi:

```csharp
        if (req.IdempotencyKey is { } key)
        {
            // Satır kipinin yolu: oynatılan RemainingBalance asıl kaydın bakiyesi (havuz kipi bu yönteme girmez).
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, key, pool: false, ct);
```

`WriteDeductionAsync`'in PK dalında (`:529-530`). Bugünkü hâli:

```csharp
                var (winner, _, conflict) = await LookupAsync(
                    licenseId, req, canonicalId, req.IdempotencyKey.Value, ct);
```

Yenisi:

```csharp
                var (winner, _, conflict) = await LookupAsync(
                    licenseId, req, canonicalId, req.IdempotencyKey.Value, pool: false, ct);
```

(`pool: false, ct` ve `personPool: false, ct`: adlandırılmış argüman kendi konumunda olduğundan ardından konumsal argüman gelebilir (C# 7.2).)

- [ ] **Adım 6: Koştur, PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolReaders"`
Expected: 13/13 PASS, 0 uyarı.

Regresyon (değiştirmeden):

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRuleTests|FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceScopeRuleSumTests|FullyQualifiedName~PersonBalanceGroupReversalTests|FullyQualifiedName~PersonBalanceModeTests|FullyQualifiedName~PersonBalancePoolPreviewTests"`
Expected: hepsi PASS. Satır kipinde değişmemesi gerekenler:
- `PersonBalanceWpfReadersTests`: `PersonPool = false` iddiaları, oynatmanın `−Σ`'sı, bacak anahtar → 404.
- `PersonBalanceScopeRuleSumTests`: 409 `scope-has-deduction` uzantıları, `appliedAmount` 120.
- `LicensesCustomerBalanceApplyControllerTests`: A11 ve kopya Id oynatması, `RemainingBalance` asıl kaydın bakiyesi.
- `CustomerBalanceScopeRuleTests`: §15-6 kapısı ve mod Off'ta `/scope`.

İlişkisel (Docker; `ApplyOncePerScopeAsync`'in çağrı argümanları değişti):

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceScopeRuleRelationalTests"`
Expected: PASS. Docker kapalıysa "Docker kapalı" diye düşer; Genel kurallar'daki `DOCKER_HOST` adımıyla yeniden koştur. Koşturulamadıysa adımda not düş; CI ubuntu işi koşar.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolReadersTests.cs
git commit -F - <<'MSG'
feat(balance): scope havuz kipini bildirsin, oynatma o anki havuzu dönsün

/scope havuz kipindeki lisansta PersonPool=true döner; yüklem, çapa
süzgeci ve −Σ(grup) aynen kalır (ortak bakiye §4.3, çapa düzeyinde).
Kayıtlı anahtarın oynatması havuz kipinde RemainingBalance olarak o anki
havuzu verir: memberIds varsa kural 4'ün Σ'sı, yoksa S'nin toplamı. 409
kuralları uygulanmaz; kayıtlı anahtar customer-not-synced almaz (§4.2,
plan fark 18c). ApplyRequest memberIds gövde alanını taşır, satır kipinde
okunmaz. Satır kipinin yanıtları, A11 ve scope-has-deduction uzantıları
değişmez. Tek kabul edilen fark: Guid'e bağlanamayan memberIds gövde
bağlamasında, kipten bağımsız 400 ValidationProblem alır; bugünkü hiçbir
istemci bu alanı göndermez.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 7: Havuzlu apply (`DrawAsync`'e bağlama, kapsam kuralı deneme başına, Örnek 1–2) (spec §4.2, §4.5 Örnek 2, §5.2, §5.4, §15-2)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs`
  - sınıf belgesi (`:46-64`; `/// </list></para>` ile `/// </summary>` arasına bir paragraf)
  - `Apply` (`:312-367`; Görev 1 ve 6'dan sonra birkaç satır kayar): ön oynatmadan SONRA, satır kipinin kapsam dalından (bugün `:361-364`) ÖNCE havuz dalı
  - yeni `ApplyPoolAsync` + `ScopeHasDeduction` (`ApplyOncePerScopeAsync`'in belgesinden (`:369`) hemen önce)
  - `ApplyOncePerScopeAsync`'in 409 gövdesi (`:424-434`) → `ScopeHasDeduction` çağrısı (çıktı birebir)
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolApplyTests.cs`

Havuz kipinde yeni anahtarlı (ya da anahtarsız) apply artık tek servis yönteminden, `PersonBalance.DrawAsync`'ten geçer (Görev 4): `P` `ResolveDrawSetAsync`'le (`W` = `memberIds`), plan `PlanDraw`'la, yazım tek `SaveChanges`. Uca özgü iki adım her denemenin başında, `DrawAsync`'in kilidinden SONRA koşan geri çağrıdadır: anahtarın yeniden aranması (`LookupAsync(…, pool: true, …)`) ve §6.6 kapsam yüklemi (`LatestScopeDeductionAsync(…, personPool: true, …)`). Kapsam kuralında her deneme kendi işlemini açar ve `bal:{lisans}:{asıl kayıt}:{kapsam}` kilidini o işlemde alır (`DrawRequest.ScopeLockResource`, iskelet fark 7). Satır kipinin yolu (`ApplyOncePerScopeAsync`, `WriteDeductionAsync`) davranışça değişmez (fark 8); yalnız 409 `scope-has-deduction` gövdesi ortak bir yardımcıya taşınır. Çakışma, PK yarışı, 1205 ve 503 InMemory'de kanıtlanmaz → Görev 14–16 (Testcontainers).

**Not (spec ↔ kod) — bu görev:**
1. **Anahtarın yeniden aranması 1. denemede de, ama yalnız kapsam yolunda.** Spec §4.2 adım 6 "2. ve sonraki her denemenin başında" der; §4.2 "Her denemede" ise yüklemin Lookup'tan SONRA koşmasını ister. Kapsam yolunda 1. deneme de kilit altında Lookup yapar (bugünkü `ApplyOncePerScopeAsync` `:414-420` emsali: aynı anahtarlı eşzamanlı istek kilitte beklerken kazanan yazmış olabilir; o istek kendi düşümünü "kapsamda düşüm var" diye görmemeli). Kapsamsız yolda 1. denemenin Lookup'ı `Apply`'ın ön oynatmasıdır (aynı istek, kilitsiz), tekrarlanmaz. Kural: `anahtar var ∧ (attempt > 1 ∨ kapsam dolu)`. Bu kuralın kapatmadığı pencere Not 6'da kapanır.
2. **PK yarışında kazanan bulunamazsa.** Bugünkü satır yolu özgün `DbUpdateException`'ı `throw;` ile yeniden atar (`:532`). `DrawAsync` PK ihlalini `DuplicateKey` sonucuna çevirdiği (fark 9) için özgün istisna denetleyiciye ulaşmaz; havuz yolu `InvalidOperationException("Bakiye apply: anahtar PK ihlali verdi ama kazanan bulunamadı")` atar. Sonuç aynı (500, gerçek DB sorunu yutulmaz); yalnız istisna türü farklı. Anahtar "foreign" çıkarsa da (başka lisansın satırı PK'yı yarışta aldıysa) bugünkü gibi aynı yola düşer (kazanan yok → istisna). *(30.10.2026'da değişti: havuz yolunda foreign 404, Lookup'ta 1205 503 — fark 18m.)*
3. **Kapsam kilidinin zaman aşımı.** `SalesCommandRejection` `DrawAsync`'ten çağırana yükselir (Görev 4 sözleşmesi); `ApplyPoolAsync` onu bugünkü `ApplyOncePerScopeAsync` `:406-412` günlüğü ve problem gövdesiyle 409 `stale`'e çevirir. Deneme işlemi commit edilmeden atıldığı için hiçbir şey yazılmamıştır.
4. **`SaleScope` mod Off'ta da çapaya yazılır.** Kural mod Off'ta yoktur (`scope = null`), ama alan bugünkü satır yolunda da yazılır (`:477`); `/scope` ucu ve WPF'in benimseme yolu mod Off'ta da onu okur.
5. **Önkoşul — Görev 6'nın yereli.** Bu görev `Apply`'da Görev 6'nın `var (mode, pool) = owned.Value;` yerelini kullanır (Görev 1 `var (mode, _) = …` yazdı, Görev 6 ön oynatmaya `pool` geçirmek için adlandırdı). Adım 3'teki "Görev 6 sonrası" alıntı bu hâli gösterir.
6. **Spec ↔ kod farkı — `no-balance`'tan önce son bir Lookup.** Spec §4.2 adım 6 Lookup'ı "2. ve sonraki her denemenin başında" ister; amacı ise "aynı anahtarlı başka bir istek araya girip havuzu boşalttıysa … `no-balance` değil kazananın sonucu" (spec `:177`), §4.2'ye göre de kayıtlı anahtar ret almaz. Lafız bir pencereyi açık bırakır: kapsamsız yolda (mod Off ya da `SaleScope` boş) 1. denemenin tek koruması kilitsiz ön oynatmadır. Aynı anahtarlı ilk istek ön oynatmadan SONRA, `DrawAsync`'in 1. deneme okumalarından ÖNCE commit edip havuzu tüketirse (`Σ ≤ 0`) ne jeton çakışması ne PK ihlali olur; ikinci istek 409 `no-balance` alır, WPF bunu kesin "bakiye yok" sayar (`MarkNoBalance`), oysa anahtar defterde ve para düşülmüş. (Havuz kısmen tükenmişse sorun yok: çapa PK'sı çarpar, `DuplicateKey` yolu oynatır. Bugünkü satır yolunda da aynı pencere var; regresyon değil.) Çözüm: `ApplyPoolAsync` `NoBalance` sonucunda, anahtar varsa `LookupAsync(…, pool: true, …)`'u bir kez daha koşar — foreign → 404, conflict → 409 `content-conflict`, kayıt → oynatma; yoksa bugünkü 409 `no-balance`. Kapanış kesindir: `Σ ≤ 0` okuması kazananın commit'ini gördüyse, aynı commit'teki çapa satırını Lookup da görür. Salt okuma; satır kipine ve `Drawn` yoluna dokunmaz, `NothingToApply`'a eklenmez (yalnız `Requested ≤ 0`'dan doğar, yarıştan değil). InMemory'de kanıtlanamaz (commit'in iki okuma arasına düşmesi gerekir); Görev 7'ye test eklenmez; Testcontainers kanıtı Görev 14'ün `Ilk_denemede_havuzu_tuketen_ayni_anahtarli_rakip_oynatilir` testidir (Görev 14'ün TC10'u 2. deneme Lookup'ını sınar, bu pencereyi değil). *(30.10.2026'da genişledi: `customer-not-synced` dalı ve 1205 → 503 — fark 18n.)*

Sahip sorusu adayı: yok. (Aynı satışın başka üyeye çapalı ikinci düşümünün 200 alması bilinçli: kural çapa düzeyinde, M10, §15-2; testte belgelenir.)

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolApplyTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye S2, havuzlu apply (spec 2026-10-11 §4.2): çekim <c>PersonBalance.DrawAsync</c>'ten geçer, para
/// <c>P</c>'den çok bacaklı yazılır (Örnek 1, Örnek 2; Örnek 3 ve §5.4 KVKK'nın geri alması; §5.2 yanlış birleştirme)
/// ve WPF reverse'e havuz sürüm kapısı konmaz (§7.5); sıra doğrulamalar →
/// sahiplik → satış kapısı → asıl kayıt → yeniden oynatma → havuz sürüm kapısı → <c>memberIds</c> → <c>P</c>
/// (plan farkları 3, 4). §6.6 kapsam kuralı çapa düzeyinde ve tutarı <c>−Σ(grup)</c> (§15-2). Bayrak boşken satır
/// kipi birebir (R02: <c>DrawGroupId</c> NULL). InMemory: yarış, PK, 1205 ve 503 Görev 14–16'da (Testcontainers).
/// </summary>
public sealed class PersonBalancePoolApplyTests
    : IClassFixture<PersonBalanceConfiguredApiFactory>, IClassFixture<LogApiFactory>
{
    private readonly PersonBalanceConfiguredApiFactory _factory;
    private readonly LogApiFactory _unconfigured;

    public PersonBalancePoolApplyTests(PersonBalanceConfiguredApiFactory factory, LogApiFactory unconfigured)
    {
        _factory = factory;
        _unconfigured = unconfigured;
    }

    // ── Örnekler (spec §4.2, §4.5, §5.2, §5.4) ──────────────────────────────

    /// <summary>
    /// Örnek 1'in YAZIMI (§4.2): T 0, I 100, Y 30; 120 → K (T, 0,00, OA 120, session:42, DG K), L1 (I, −100),
    /// L2 (Y, −20); bacaklarda açıklama yok, ortak CreatedAt; yanıt (K, 120, 10); transactions/K ve scope okumaları
    /// grubun toplamını verir; scope(I) 204 (bacak kapsam taşımaz). D1 üç satırda.
    /// </summary>
    [Fact]
    public async Task Ornek1_apply_satirlari_birebir()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();

        var ex = await Ornek1ApplyAsync(kit);

        var rows = await kit.GroupRowsAsync(ex.K);
        rows.Should().HaveCount(3);
        rows[0].Should().BeEquivalentTo(new
        {
            Id = ex.K, LicenseId = kit.LicenseId, WpfCustomerId = ex.T, Amount = 0m, Kind = Kit.KindDeduction,
            OriginalAmount = (decimal?)120m, SaleScope = Kit.Session42, Reason = (string?)null,
            ReversesTransactionId = (Guid?)null, DrawGroupId = (Guid?)ex.K, CreatedByCustomerId = kit.CustomerId,
        });
        rows.Single(r => r.WpfCustomerId == ex.I).Should().BeEquivalentTo(new
        {
            Amount = -100m, Kind = Kit.KindDeduction, OriginalAmount = (decimal?)null, SaleScope = (string?)null,
            Reason = (string?)null, ReversesTransactionId = (Guid?)null, DrawGroupId = (Guid?)ex.K,
        });
        rows.Single(r => r.WpfCustomerId == ex.Y).Should().BeEquivalentTo(new
        {
            Amount = -20m, Kind = Kit.KindDeduction, OriginalAmount = (decimal?)null, SaleScope = (string?)null,
            Reason = (string?)null, ReversesTransactionId = (Guid?)null, DrawGroupId = (Guid?)ex.K,
        });
        rows.Skip(1).Should().OnlyContain(r => r.Id != ex.K, "bacak Id'si sunucunun ürettiği yeni Guid");
        rows.Select(r => r.CreatedAt).Distinct().Should().ContainSingle("çekim tek anda yazılır (§3.2)");

        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");

        var tx = await kit.TransactionAsync(ex.K);
        tx.StatusCode.Should().Be(HttpStatusCode.OK);
        (await tx.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.TransactionStatusResponse>())
            .Should().Be(new LicensesCustomerBalanceApplyController.TransactionStatusResponse(ex.K, 120m, false));

        var onAnchor = await kit.ScopeAsync(ex.T, Kit.Session42);
        onAnchor.StatusCode.Should().Be(HttpStatusCode.OK);
        (await onAnchor.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.ScopeResponse>())
            .Should().BeEquivalentTo(new { TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = true });
        (await kit.ScopeAsync(ex.I, Kit.Session42)).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "bacak kapsam taşımaz (Örnek 1)");
    }

    /// <summary>
    /// Örnek 2 (§4.5): Örnek 1'den sonra toplam 120'den 90'a iner; WPF K'yi geri alır, taze K′ ile apply eder.
    /// Mod On: geri alınmış K kapsam kuralını tetiklemez. K′ (T, 0), L3 (I, −90) → I 10, Y 30, havuz 40.
    /// </summary>
    [Fact]
    public async Task Ornek2_reverse_sonrasi_taze_apply()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);
        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        var fresh = Guid.NewGuid();

        var resp = await kit.ApplyAsync(ex.T, 90m, 90m, fresh, Kit.Session42, [ex.T, ex.I, ex.Y]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(fresh, 90m, 40m));
        var rows = await kit.GroupRowsAsync(fresh);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new
        {
            Id = fresh, WpfCustomerId = ex.T, Amount = 0m, OriginalAmount = (decimal?)90m, SaleScope = Kit.Session42,
            DrawGroupId = (Guid?)fresh,
        });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = ex.I, Amount = -90m, DrawGroupId = (Guid?)fresh });
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(10m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>§5.2 "Yanlış birleştirme — harcama olduysa": X 100, Y 50; Y'nin malı 80, sahip Y → çapa Y −50, bacak X −30.</summary>
    [Fact]
    public async Task Yanlis_birlestirme_X100_Y50_sahip_Y_80()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var x = await kit.MemberAsync(g, 100m);
        var y = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(y, 80m, 80m, key, null, [x, y]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 80m, 70m));
        var rows = await kit.GroupRowsAsync(key);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new { Id = key, WpfCustomerId = y, Amount = -50m, DrawGroupId = (Guid?)key },
            "alıcının kendi parası önce (K1)");
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = x, Amount = -30m, DrawGroupId = (Guid?)key });
        (await kit.BalanceAsync(x)).Should().Be(70m);
        (await kit.BalanceAsync(y)).Should().Be(0m);
    }

    /// <summary>
    /// §5.4 KVKK: kişi {A 0, P silinmiş 40}; WPF silinmiş satırı göndermez (W = {A}), sunucu P'yi S'den alır.
    /// 30 → çapa A 0, bacak P −30 → P 10.
    /// </summary>
    [Fact]
    public async Task KVKK_A0_silinmis_P40_30()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, 40m, purged: true);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(a, 30m, 30m, key, null, [a]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 30m, 10m));
        var rows = await kit.GroupRowsAsync(key);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new { Id = key, WpfCustomerId = a, Amount = 0m });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = p, Amount = -30m, DrawGroupId = (Guid?)key });
        (await kit.BalanceAsync(p)).Should().Be(10m);
        (await kit.LedgerHoldsAsync(p)).Should().BeTrue("D1");
    }

    /// <summary>
    /// §5.4 K3'ün ikinci yarısı: <see cref="KVKK_A0_silinmis_P40_30"/>'un çekimi geri alınırsa bacak silinmiş üyeye
    /// döner (P +30 → 40, A 0) ve para yine havuzdan kullanılabilir — sunucu P'yi S'den sayar, W = {A} iken de.
    /// </summary>
    [Fact]
    public async Task KVKK_geri_alinca_para_silinmis_uyeye_doner_ve_havuzdan_kullanilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, 40m, purged: true);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, key, null, [a])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 30m, 10m));
        var group = await kit.GroupRowsAsync(key);

        (await kit.ReverseAsync(key)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.BalanceAsync(p)).Should().Be(40m);
        (await kit.BalanceAsync(a)).Should().Be(0m);
        var reversals = await kit.ReversalsOfAsync(group.Select(r => r.Id));
        reversals.Should().HaveCount(2);
        foreach (var leg in group)
            reversals.Single(r => r.ReversesTransactionId == leg.Id).Should().BeEquivalentTo(
                new { leg.WpfCustomerId, Amount = -leg.Amount, Kind = Kit.KindReversal },
                "her bacak kendi satırına döner; P'nin 30'u silinmiş satıra");
        foreach (var row in new[] { a, p })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        var preview = await kit.PreviewAsync(a, [a]);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        (await preview.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.PreviewResponse>())
            .Should().BeEquivalentTo(new { Balance = 40m, PersonPool = true },
                "silinmiş üye W'de olmasa da S'den gelir (K3)");
    }

    /// <summary>
    /// Örnek 3 (§5.2, K1): Örnek 1'den sonra Ayır (I ve Y ayrı kişi) veri işlemi yapmaz — T 0, I 0, Y 10. Ardından
    /// K iptal edilirse (WPF reverse) her bacak ayrılmış olsa bile kendi satırına döner: T +0, I +100, Y +20 → I 100,
    /// Y 30. I'nin parası T'ye değil I'ye döner; I artık tek başına kişi, havuzu 100. D1 üç satırda.
    /// </summary>
    [Fact]
    public async Task Ornek3_ayirmadan_sonra_geri_alma_her_bacagi_kendi_satirina_dondurur()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);
        var group = await kit.GroupRowsAsync(ex.K);

        (await kit.SyncGroupAsync(ex.I, null)).EnsureSuccessStatusCode();
        (await kit.SyncGroupAsync(ex.Y, null)).EnsureSuccessStatusCode();
        (await kit.BalanceAsync(ex.T)).Should().Be(0m, "Ayır veri işlemi yapmaz (§5.2)");
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);

        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m, "I'nin parası T'ye değil I'ye döner (K1)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        var reversals = await kit.ReversalsOfAsync(group.Select(r => r.Id));
        reversals.Should().HaveCount(3);
        var r0 = reversals.Single(r => r.ReversesTransactionId == ex.K);
        reversals.Should().OnlyContain(r => r.DrawGroupId == r0.Id && r.Kind == Kit.KindReversal,
            "geri alma tek grup: ortak DrawGroupId, geri alma çapasının Id'si");
        foreach (var leg in group)
            reversals.Single(r => r.ReversesTransactionId == leg.Id).Should().BeEquivalentTo(
                new { leg.WpfCustomerId, Amount = -leg.Amount }, "her bacak kendi satırına döner");
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        var preview = await kit.PreviewAsync(ex.I, [ex.I]);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        (await preview.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.PreviewResponse>())
            .Should().BeEquivalentTo(new { Balance = 100m, PersonPool = true }, "ayrılmış I'nin havuzu yalnız kendisi");
    }

    // ── Havuz ve 409 kuralları (§4.0, §4.2 adım 1–3) ────────────────────────

    /// <summary>
    /// §4.0-4, §4.13: S∖P'nin eksisi havuza sayılır ve kilitlenir, ondan çekilmez. A 100, B −60, W = {A}; 100
    /// istenir → Σ 40, uygulanan 40, kalan 0; B −60 kalır.
    /// </summary>
    [Fact]
    public async Task Eksiler_kilitlenir_A100_B_eksi60_W_A()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(a, 100m, 100m, key, null, [a]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 40m, 0m));
        (await kit.GroupRowsAsync(key)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { WpfCustomerId = a, Amount = -40m, DrawGroupId = (Guid?)key });
        (await kit.BalanceAsync(a)).Should().Be(60m);
        (await kit.BalanceAsync(b)).Should().Be(-60m, "eksi satıra yazılmaz");
    }

    /// <summary>§4.2 adım 2: Σ ≤ 0 → 409 no-balance (A 50, B −60, W = {A, B}); hiçbir şey yazılmaz.</summary>
    [Fact]
    public async Task Havuz_sifirsa_409_no_balance_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 30m, 30m, Guid.NewGuid(), null, [a, b]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "no-balance");
        (await DeductionCountAsync(kit)).Should().Be(0);
        (await kit.BalanceAsync(a)).Should().Be(50m);
    }

    /// <summary>§4.0-1 kesin kol: a da W'nin hiçbir Id'si de sunucuda yok → 409 no-balance. Preview bu kolda 200
    /// <c>Balance = 0</c>, <c>PersonPool = true</c> döner (sahip kararı 1, plan fark 19; Görev 5
    /// <c>Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir</c>); 409 yalnız apply'da.</summary>
    [Fact]
    public async Task Kural1_kesin_409_no_balance()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var unsynced = Guid.NewGuid();

        var resp = await kit.ApplyAsync(unsynced, 10m, 10m, Guid.NewGuid(), null, [unsynced, Guid.NewGuid()]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "no-balance");
        (await DeductionCountAsync(kit)).Should().Be(0);
    }

    /// <summary>§4.0-3b: S'de olup W'de olmayan parası olan canlı üye (B 40) → 409 customer-not-synced; yazım yok.</summary>
    [Fact]
    public async Task Kural3b_409_customer_not_synced_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "customer-not-synced");
        (await DeductionCountAsync(kit)).Should().Be(0);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    /// <summary>§4.2 adım 5: çapanın bakiye satırı yoksa Balance 0 ile açılır; çapa tutarı 0, para I'den.</summary>
    [Fact]
    public async Task Capanin_bakiye_satiri_yoksa_0_ile_acilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g, balanceRow: false);
        var i = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        (await kit.BalanceRowAsync(t)).Should().BeNull();
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(t, 30m, 30m, key, null, [t, i]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 30m, 20m));
        (await kit.BalanceRowAsync(t)).Should().NotBeNull().And.BeEquivalentTo(new { Balance = 0m });
        var rows = await kit.GroupRowsAsync(key);
        rows[0].Should().BeEquivalentTo(new { Id = key, WpfCustomerId = t, Amount = 0m });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = i, Amount = -30m });
        (await kit.LedgerHoldsAsync(t)).Should().BeTrue("0'lık satır D1'i bozmaz");
    }

    // ── Sıra ve kapılar (§4.0 "Sürüm denetimi", plan farkları 3, 4) ──────────

    /// <summary>§4.0, §7.2: havuz kipinde Wh altı, sürümsüz ya da bozuk UA → 409 client-update-required; yazım yok.</summary>
    [Theory]
    [InlineData(Kit.OldUserAgent)]
    [InlineData(null)]
    [InlineData("OrderDeck-WPF/bozuk")]
    public async Task Eski_surum_409_client_update_required(string? userAgent)
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, [a], userAgent);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "client-update-required");
        (await DeductionCountAsync(kit)).Should().Be(0);
        (await kit.BalanceAsync(a)).Should().Be(50m);
    }

    /// <summary>
    /// Plan fark 3 (§6.2, §8.5): sürüm kapısı memberIds denetiminden ÖNCE — memberIds göndermeyen v0.9.9/v0.9.10
    /// 400 değil 409 client-update-required alır.
    /// </summary>
    [Fact]
    public async Task Eski_surum_memberIds_siz_de_409_client_update_required()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, null, Kit.OldUserAgent);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "client-update-required");
    }

    /// <summary>§4.0: havuz kipinde güncel istemci memberIds'siz (yok ya da boş, plan fark 18f) → 400 member-ids-required.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task memberIds_yok_400_member_ids_required(bool empty)
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        await kit.EnablePoolAsync();
        IReadOnlyCollection<Guid>? memberIds = empty ? Array.Empty<Guid>() : null;

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, memberIds);

        await ShouldBeProblemAsync(resp, HttpStatusCode.BadRequest, "member-ids-required");
        (await DeductionCountAsync(kit)).Should().Be(0);
    }

    /// <summary>
    /// §7.2, plan fark 18j: havuz kipinde PersonBalance:MinClientVersion tanımsız → kapalı-hata 409
    /// client-update-required ve günlüğe Error (lisans Id'siyle).
    /// </summary>
    [Fact]
    public async Task Alt_sinir_tanimsiz_409_ve_hata_gunlugu()
    {
        var kit = await Kit.CreateAsync(_unconfigured);
        var a = await kit.MemberAsync(null, 50m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "client-update-required");
        _unconfigured.Log.Entries.Should().Contain(e => e.Level == LogLevel.Error
            && e.Message.Contains("apply") && e.Message.Contains(kit.LicenseId.ToString()));
        (await DeductionCountAsync(kit)).Should().Be(0);
    }

    /// <summary>
    /// §4.2 "İdempotency ve A11" (spec `:202`): yeniden oynatma havuz kapısından ve memberIds denetiminden ÖNCE —
    /// kayıtlı anahtar eski sürümle ve memberIds'siz de oynatılır (K, 120, Σ_S = 10); ikinci düşüm yok.
    /// </summary>
    [Fact]
    public async Task Kayitli_anahtar_eski_surumle_ve_memberIds_siz_oynatilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);

        var resp = await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, null, Kit.OldUserAgent);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(ex.K, 120m, 10m));
        (await DeductionCountAsync(kit)).Should().Be(3, "yalnız Örnek 1'in üç satırı");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    /// <summary>A11 (§4.2): aynı anahtar farklı ProductTotal → 409 content-conflict; yan etki yok.</summary>
    [Fact]
    public async Task Ayni_anahtar_farkli_icerik_409_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);

        var resp = await kit.ApplyAsync(ex.T, 120m, 999m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "content-conflict");
        (await DeductionCountAsync(kit)).Should().Be(3);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    /// <summary>
    /// §7.5 son madde (olumsuz gereksinim): WPF reverse'e havuz sürüm kapısı konmaz. Havuz kipindeki lisansta Wh
    /// altı istemci (0.9.10) çok bacaklı çekimi geri alır: 200, 409 client-update-required DEĞİL; grup geri alınır.
    /// Satış senkronu mod Off: §6.5-3 kapısı devrede değil, ölçülen yalnız havuz kapısının yokluğu. Kurulum elle
    /// tohumlanmış Örnek 1 (bu görevin apply koduna bağlı değil); kod değişmez, test kapının yokluğunu sabitler.
    /// </summary>
    [Fact]
    public async Task Havuz_kipinde_eski_surumle_WPF_reverse_200_surum_kapisi_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();

        var resp = await kit.SendAsync(HttpMethod.Post,
            $"/api/v1/licenses/{kit.LicenseId}/customer-balance/transactions/{ex.K}/reverse",
            userAgent: Kit.OldUserAgent);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3, "grup olarak geri alınır");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
    }

    // ── §6.6 kapsam kuralı, çapa düzeyinde (§4.2, §15-2) ────────────────────

    /// <summary>
    /// §15-2: mod On, aynı çapa×kapsam, yeni anahtar (ya da anahtarsız) → 409 scope-has-deduction; tutar
    /// <c>−Σ(grup)</c> = 120 (çapanın kendi payı 0 iken bile), productTotal 120. Bakiyeye dokunulmaz.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mod_On_kapsam_ikinci_farkli_anahtar_409_scope_has_deduction_eksi_sigma(bool withKey)
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);

        var resp = await kit.ApplyAsync(ex.T, 50m, 50m, withKey ? Guid.NewGuid() : null, Kit.Session42, [ex.T, ex.I, ex.Y]);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesCommandTestKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("scope-has-deduction");
        problem.GetProperty("transactionId").GetGuid().Should().Be(ex.K);
        problem.GetProperty("appliedAmount").GetDecimal().Should().Be(120m, "çapa payı 0; tutar grubun toplamı");
        problem.GetProperty("productTotal").GetDecimal().Should().Be(120m);
        (await DeductionCountAsync(kit)).Should().Be(3);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    /// <summary>
    /// M10, §4.2 "Çapa düzeyinde kalır": aynı kişinin BAŞKA üyesine çapalı aynı kapsam 409 olmaz (#506'nın çok
    /// izli akışı). Örnek 1'den sonra I'ye çapalı 10 → K3 (I, 0), bacak (Y, −10).
    /// </summary>
    [Fact]
    public async Task Mod_On_baska_uyedeki_capa_kapsam_kuralini_tetiklemez()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);
        var other = Guid.NewGuid();

        var resp = await kit.ApplyAsync(ex.I, 10m, 10m, other, Kit.Session42, [ex.T, ex.I, ex.Y]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(other, 10m, 0m));
        var rows = await kit.GroupRowsAsync(other);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new { Id = other, WpfCustomerId = ex.I, Amount = 0m, SaleScope = Kit.Session42 });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = ex.Y, Amount = -10m, SaleScope = (string?)null });
        (await kit.BalanceAsync(ex.Y)).Should().Be(0m);
    }

    /// <summary>§4.2 "Koşul": mod Off'ta kural yok — aynı kapsama iki farklı anahtar ikisi de 200; kapsam yine çapada.</summary>
    [Fact]
    public async Task Mod_Off_kapsam_kurali_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        await kit.EnablePoolAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, first, Kit.Session42, [a])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(first, 30m, 70m));
        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, second, Kit.Session42, [a])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(second, 30m, 40m));

        foreach (var id in new[] { first, second })
            (await kit.GroupRowsAsync(id)).Should().ContainSingle().Which.Should().BeEquivalentTo(
                new { Id = id, WpfCustomerId = a, Amount = -30m, SaleScope = Kit.Session42, DrawGroupId = (Guid?)id },
                "havuz yazımı tek bacakta da DrawGroupId = Id (§4.2 adım 5)");
        (await kit.BalanceAsync(a)).Should().Be(40m);
    }

    // ── Satır kipi (R02, R70) ────────────────────────────────────────────────

    /// <summary>
    /// Bayrak boş: satır kipi birebir — memberIds verilse de okunmaz, yalnız istenen satırdan çekilir (A 100 / 120
    /// istenir → 100), tek satır, DrawGroupId NULL; B'ye dokunulmaz.
    /// </summary>
    [Fact]
    public async Task Satir_kipinde_apply_DrawGroupId_NULL()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, 50m);
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(a, 120m, 120m, key, Kit.Session42, [a, b]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 100m, 0m));
        (await kit.GroupRowsAsync(key)).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Id = key, WpfCustomerId = a, Amount = -100m, DrawGroupId = (Guid?)null });
        (await kit.BalanceAsync(b)).Should().Be(50m);
    }

    /// <summary>§7.4 kill switch: EnablePool → havuz yazımı (DrawGroupId dolu); DisablePool → satır kipi (NULL).</summary>
    [Fact]
    public async Task Bayrak_kapatilinca_satir_kipine_doner()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        var pooled = Guid.NewGuid();
        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, pooled, null, [a, b])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(pooled, 30m, 120m));

        await kit.DisablePoolAsync();
        var plain = Guid.NewGuid();
        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, plain, null, [a, b])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(plain, 30m, 40m));

        (await kit.GroupRowsAsync(pooled)).Should().ContainSingle().Which.DrawGroupId.Should().Be(pooled);
        (await kit.GroupRowsAsync(plain)).Should().ContainSingle().Which.DrawGroupId.Should().BeNull();
        (await kit.BalanceAsync(a)).Should().Be(40m);
        (await kit.BalanceAsync(b)).Should().Be(50m);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record Ornek1Sonuc(Guid T, Guid I, Guid Y, Guid K);

    /// <summary>Örnek 1'in kişisi (T 0, I 100, Y 30; kip zaten açık) ve havuzlu apply'ı: K, 120, session:42 → (K, 120, 10).</summary>
    private static async Task<Ornek1Sonuc> Ornek1ApplyAsync(Kit kit)
    {
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        var k = Guid.NewGuid();

        var resp = await kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(k, 120m, 10m));
        return new Ornek1Sonuc(t, i, y, k);
    }

    private static async Task<LicensesCustomerBalanceApplyController.ApplyResponse> ReadAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.ApplyResponse>())!;
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string title)
    {
        resp.StatusCode.Should().Be(status, await resp.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(resp)).Should().Be(title);
    }

    /// <summary>Lisansın bütün düşüm satırları (çapa + bacak).</summary>
    private static Task<int> DeductionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindDeduction));
}
```

Not: `rows[0]` her zaman çapadır, sonrakiler `WpfCustomerId` "N" ordinal (Görev 1'in `GroupRowsAsync` sözleşmesi); iki bacaklı testlerde (`Ornek1`) bacaklar `Single(r => r.WpfCustomerId == …)` ile seçilir, çünkü Guid sırası rastgeledir. Tek bacaklı (çapa + bir bacak) testlerde `rows[1]` belirlidir.

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolApplyTests"`
Expected: derlenir (Görev 1'in kiti ve fabrikası (`PersonBalanceConfiguredApiFactory`), Görev 5'in kapıları, Görev 6'nın `ApplyRequest.MemberIds`'i hazır); `Satir_kipinde_apply_DrawGroupId_NULL` ve `Kural1_kesin_409_no_balance` PASS (satır yolu bugün de böyle davranır), `Havuz_kipinde_eski_surumle_WPF_reverse_200_surum_kapisi_yok` PASS (§7.5'in olumsuz gereksinimi: kurulumu elle tohumlanmış Örnek 1, reverse'e bu görev dokunmaz; test kapının yokluğunu sabitler, TDD istisnası bilinçli), geri kalanların hepsi FAIL: havuz kipindeki yeni anahtarlı apply hâlâ satır yolundan geçer (Görev 6'nın ara durumu) — Örnek 1'de T'nin bakiyesi 0 olduğu için 409 `no-balance` (beklenen 200 `(K, 120, 10)`; `Ornek1ApplyAsync`'i kullanan `Ornek3_…` de kurulumda düşer); `KVKK_A0_silinmis_P40_30` ve `KVKK_geri_alinca_…` A'nın satırı 0 olduğu için 409 `no-balance` alır; eski sürüm / `memberIds`'siz istekler 200 alır (beklenen 409 `client-update-required` / 400 `member-ids-required`); `Kural3b` 409 `no-balance` alır (beklenen `customer-not-synced`); `Mod_Off_kapsam_kurali_yok`'ta iki apply 200 `(…, 30, 70)` / `(…, 30, 40)` geçer, `DrawGroupId` NULL (beklenen `= Id`) iddiasında düşer; `Bayrak_kapatilinca_…` daha ilk apply'da düşer: satır yolu `(pooled, 30, 70)` döner (satır bakiyesi), beklenen `RemainingBalance` 120 (havuz Σ 150 − 30) — `DrawGroupId` iddiasına varılmaz.

- [ ] **Adım 3: Havuz dalını `Apply`'a ekle**

Görev 6'dan sonra `Apply`'ın kapı ve oynatma bölgesi şöyledir (bağlam; bu blok DEĞİŞMEZ):

```csharp
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, pool) = owned.Value;
        // §6.5-3: bakiyenin her okumasından (oynatma dahil) önce.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "apply");

        // Kopya Id'si asıl kayda çözülür (A5b): bakiye araması, yazılan hareket
        // ve idempotency karşılaştırması asıl kayıtla.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, req.WpfCustomerId, ct);

        // Sahiplik kontrolünden SONRA bakıyoruz: anahtar başka lisansa aitse
        // çağıran onun sonucunu görmemeli.
        if (req.IdempotencyKey is { } preKey)
        {
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, preKey, pool, ct);
            if (foreign) return NotFound();
            if (conflict) return ContentConflict();
            if (replay is not null) return Ok(replay);
        }
```

Edit — bugünkü `:361-364` (Görev 1–6 bu satırlara dokunmaz):

```csharp
        // §6.6: anahtar yeni (yok ya da defterde değil). Kural yalnız mod On'da
        // ve kapsamlı istekte; kapsamsız istek ve mod Off bugünkü yoldan.
        if (mode == SalesSyncMode.On && req.SaleScope is { } saleScope)
            return await ApplyOncePerScopeAsync(licenseId, req, canonicalId, saleScope, ct);
```

yerine:

```csharp
        // Ortak bakiye, havuz kipi (spec 2026-10-11 §4.0, §4.2): havuz sürüm kapısı yeniden oynatmadan SONRA (kayıtlı
        // anahtar client-update-required almaz, plan fark 4), memberIds denetiminden ÖNCE (memberIds göndermeyen eski
        // sürüm de 409 alır, plan fark 3). Çekim DrawAsync'ten geçer; §6.6 kapsam kuralı orada deneme başına.
        if (pool)
        {
            if (!PoolClientMeetsMinimum()) return PoolClientUpdateRequired(licenseId, "apply");
            if (!HasMemberIds(req.MemberIds)) return MemberIdsRequired(licenseId, "apply");
            return await ApplyPoolAsync(licenseId, req, canonicalId, mode, ct);
        }

        // Satır kipi, bugünkü yol birebir (plan fark 8). §6.6: anahtar yeni (yok ya da
        // defterde değil). Kural yalnız mod On'da ve kapsamlı istekte; kapsamsız istek ve
        // mod Off bugünkü yoldan.
        if (mode == SalesSyncMode.On && req.SaleScope is { } saleScope)
            return await ApplyOncePerScopeAsync(licenseId, req, canonicalId, saleScope, ct);
```

`return (await WriteDeductionAsync(licenseId, req, canonicalId, ct)).Result;` satırı ve doğrulamalar (`:318-339`) aynen kalır.

- [ ] **Adım 4: `ApplyPoolAsync` ve `ScopeHasDeduction`'ı yaz**

> **30.10.2026 notu:** Aşağıdaki kod Görev 7'nin ilk hâlidir. Son incelemeden sonra çekim sonrası dallar (`NoBalance`, `CustomerNotSynced`, `DuplicateKey`) ortak `ReplayWrittenKeyAsync` yardımcısından geçer, Lookup'taki 1205 ve havuz kipinde Apply'ın ön oynatmasındaki 1205 `ReplayDeadlocked` ile 503 olur (fark 18m, 18n; commit'ler `4a7f8a82`, `6a67d071`). Güncel hâl için koda bakın.

Edit — `ApplyOncePerScopeAsync`'in belgesinin ilk iki satırı (bugün `:369-370`):

```csharp
    /// <summary>
    /// Spec 2026-10-13 §6.6 — mod On'da kapsam başına tek geçerli düşüm. Tek
```

yerine (iki yöntem öne eklenir, anchor satırları aynen sonda):

```csharp
    /// <summary>
    /// Ortak bakiye apply'ı, havuz kipi (spec 2026-10-11 §4.2). Çekim <c>PersonBalance.DrawAsync</c>'ten geçer: para
    /// <c>P</c>'den (§4.0-4, <c>W</c> = <c>memberIds</c>), çapa istekteki asıl kayıtta (payı 0 olabilir), bacaklar
    /// <c>P</c>'nin öbür üyelerinde; <c>S∖P</c>'nin eksileri havuza sayılır ve kilitlenir; tek <c>SaveChanges</c>.
    /// Çağıran izleyicide bir şey bırakmaz (sahiplik, asıl kayıt ve oynatma okumaları izlemesiz) — <c>DrawAsync</c>'in
    /// önkoşulu.
    /// <list type="bullet">
    ///   <item><b>Her denemenin başında</b> (kilitten sonra, okumalardan önce; <c>beforeAttempt</c>): anahtar varsa ve
    ///   deneme 2.+ ya da kapsam yolundaysa <see cref="LookupAsync"/> — aynı anahtarlı istek araya girip yazdıysa onun
    ///   sonucu oynatılır, havuz boşaldı diye <c>no-balance</c> dönülmez (§4.2 adım 6). Sonra kapsam yolunda
    ///   <see cref="LatestScopeDeductionAsync"/>: geri alınmamış çapa varsa 409 <c>scope-has-deduction</c>, tutar
    ///   <c>−Σ(grup)</c> (§4.2 "Her denemede", §15-2).</item>
    ///   <item><b><c>no-balance</c>'tan önce</b> anahtar varsa son bir <see cref="LookupAsync"/>: kapsamsız yolda
    ///   1. deneme kilitsiz ön oynatmaya dayanır; ondan sonra commit edip havuzu tüketen aynı anahtarlı istek
    ///   oynatılır, kayıtlı anahtar <c>no-balance</c> almaz (§4.2 adım 6'nın amacı).</item>
    ///   <item><b>Kapsam kuralı</b> yalnız satış senkronu mod On ∧ kapsam dolu (<c>legacy:</c> muaf değil, §15-18);
    ///   çapa düzeyinde (M10): aynı kişinin başka satırındaki çapa tetiklemez. Her deneme kendi işlemini açar ve
    ///   <see cref="ScopeLockResource"/> kilidini o işlemde alır; kilit denemeler arasında bırakılır, yüklem bu yüzden
    ///   her denemede yeniden koşar (plan fark 7). Kilit zaman aşımı 409 <c>stale</c>, hiçbir şey yazılmaz.</item>
    ///   <item>Üç çakışmadan sonra 503 <c>balance-busy</c> (WPF'in dayanıklılık katmanı aynı anahtarla yeniden dener);
    ///   aynı anahtarla PK yarışında bütün bacaklar geri alınır ve kazananın sonucu oynatılır.</item>
    /// </list>
    /// Yanıt: <c>TransactionId</c> = çapa, <c>AppliedAmount</c> = bütün bacakların toplamı,
    /// <c>RemainingBalance = max(0, Σ − applied)</c> (§4.2 adım 7).
    /// </summary>
    private async Task<IActionResult> ApplyPoolAsync(
        Guid licenseId, ApplyRequest req, Guid canonicalId, SalesSyncMode mode, CancellationToken ct)
    {
        // §6.6: kural yalnız mod On'da ve kapsamlı istekte. Kapsam mod Off'ta da çapaya yazılır (satır yolu da yazar).
        var scope = mode == SalesSyncMode.On ? req.SaleScope : null;
        var request = new PersonBalance.DrawRequest(
            LicenseId: licenseId,
            AnchorRowId: canonicalId,
            // Anahtar VARSA çapanın PK'sı odur — rezervasyon budur (bugünkü sözleşme).
            AnchorTransactionId: req.IdempotencyKey ?? Guid.NewGuid(),
            AnchorIdIsIdempotencyKey: req.IdempotencyKey is not null,
            Kind: PersonBalance.KindPurchaseDeduction,
            // Sipariş tutarından fazla düşülmez; havuz sınırı DrawAsync'te (UpToPool).
            Requested: Math.Min(req.Amount, req.ProductTotal),
            Rule: PersonBalance.DrawAmountRule.UpToPool,
            MemberIds: req.MemberIds,
            OriginalAmount: req.ProductTotal,
            SaleScope: req.SaleScope,
            Reason: null,
            ReversesTransactionId: null,
            CreatedByCustomerId: User.GetTenantCustomerId(),
            ScopeLockResource: scope is null ? null : ScopeLockResource(licenseId, canonicalId, scope));

        IActionResult? stopped = null;

        async Task<bool> BeforeAttempt(int attempt, CancellationToken token)
        {
            // 1. denemenin kapsamsız Lookup'ı Apply'ın ön oynatmasıdır; kapsam yolunda 1. deneme de kilit altında
            // yeniden arar (aynı anahtarlı istek kilitte beklerken kazanan yazmış olabilir).
            if (req.IdempotencyKey is { } key && (attempt > 1 || scope is not null))
            {
                var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, key, pool: true, token);
                if (foreign)
                {
                    stopped = NotFound();
                    return true;
                }
                if (conflict)
                {
                    stopped = ContentConflict();
                    return true;
                }
                if (replay is not null)
                {
                    stopped = Ok(replay);
                    return true;
                }
            }

            if (scope is not null
                && await LatestScopeDeductionAsync(licenseId, canonicalId, scope, personPool: true, token) is { } existing)
            {
                stopped = ScopeHasDeduction(licenseId, existing);
                return true;
            }

            return false;
        }

        PersonBalance.DrawResult result;
        try
        {
            result = await _person.DrawAsync(request, BeforeAttempt, ct);
        }
        catch (SalesCommandRejection rejection)
        {
            // Kapsam kilidi zaman aşımı (§6.6): aynı kişinin aynı satışında başka bir apply sürüyor; deneme işlemi
            // commit edilmeden atıldı, hiçbir şey yazılmadı. İstemci yeniden dener.
            _log.LogInformation(
                "customer-balance/apply: kapsam kilidi alınamadı, 409 {Code} (license={LicenseId})",
                rejection.Code, licenseId);
            return Problem(title: rejection.Code, detail: rejection.Message, statusCode: rejection.StatusCode);
        }

        switch (result.Status)
        {
            case PersonBalance.DrawStatus.Drawn:
                return Ok(new ApplyResponse(
                    result.AnchorTransactionId, result.Applied, Math.Max(0m, result.Pool - result.Applied)));
            case PersonBalance.DrawStatus.Stopped:
                return stopped!;
            case PersonBalance.DrawStatus.NoBalance:
            {
                // §4.2 adım 6'nın amacı 1. denemede de: kapsamsız yolda 1. denemenin tek Lookup'ı kilitsiz ön
                // oynatmadır. Aynı anahtarlı istek ön oynatmadan sonra commit edip havuzu tükettiyse ne jeton
                // çakışması ne PK ihlali olur. Σ ≤ 0 okuması o commit'i gördüyse aynı commit'teki çapayı bu Lookup da
                // görür; kayıtlı anahtar ret almaz, kazananın sonucu oynatılır.
                if (req.IdempotencyKey is { } spentKey)
                {
                    var (spentReplay, spentForeign, spentConflict) = await LookupAsync(
                        licenseId, req, canonicalId, spentKey, pool: true, ct);
                    if (spentForeign) return NotFound();
                    if (spentConflict) return ContentConflict();
                    if (spentReplay is not null) return Ok(spentReplay);
                }
                // Bugünkü başlık; WPF bunu kesin "bakiye yok" sayar (§4.2 adım 2, §4.0-1 kesin kol).
                return Problem(title: PersonBalance.CodeNoBalance, statusCode: 409);
            }
            case PersonBalance.DrawStatus.NothingToApply:
                return Problem(title: "nothing-to-apply", statusCode: 409);
            case PersonBalance.DrawStatus.CustomerNotSynced:
                return CustomerNotSynced(licenseId, "apply", result.RejectedRowIds);
            case PersonBalance.DrawStatus.DuplicateKey:
            {
                // Aynı anahtarla yarışan iki istek: PK ihlali bütün bacaklarla birlikte geri alındı (DrawAsync
                // izleyiciyi temizledi), kaybeden hiçbir iz bırakmaz. Kazananın sonucunu oynatabiliyorsak yarış
                // hikâyesi tutuyor demektir; tutmuyorsa hata gerçek bir DB sorunudur, yutulmamalı.
                var duplicateKey = req.IdempotencyKey!.Value;
                var (winner, _, winnerConflict) = await LookupAsync(
                    licenseId, req, canonicalId, duplicateKey, pool: true, ct);
                if (winnerConflict) return ContentConflict();
                if (winner is null)
                    throw new InvalidOperationException("Bakiye apply: anahtar PK ihlali verdi ama kazanan bulunamadı");
                _log.LogWarning(
                    "Bakiye uygulama yarışı: anahtarı başka istek kazandı (key={Key}, license={LicenseId})",
                    duplicateKey, licenseId);
                return Ok(winner);
            }
            case PersonBalance.DrawStatus.Busy:
                return BalanceBusy(licenseId, "apply");
            default:
                // Insufficient yalnız AllOrNothing'de, AlreadyReversed yalnız çekim türü iptalde (K5) döner.
                throw new System.Diagnostics.UnreachableException(
                    $"Bakiye apply: DrawAsync beklenmeyen sonuç döndü ({result.Status})");
        }
    }

    /// <summary>
    /// §6.6, DM6: kapsamda geri alınmamış düşüm var → 409 <c>scope-has-deduction {transactionId, appliedAmount,
    /// productTotal}</c>; istemci onu benimser (W3). <c>appliedAmount</c> grubun toplamıdır (<c>−Σ</c>, spec 2026-10-11
    /// §4.2, §15-2) — <see cref="LatestScopeDeductionAsync"/>'ten. Satır kipi (<see cref="ApplyOncePerScopeAsync"/>)
    /// ve havuz kipi (<see cref="ApplyPoolAsync"/>) aynı gövdeyi döner.
    /// </summary>
    private ObjectResult ScopeHasDeduction(Guid licenseId, ScopeResponse existing)
    {
        _log.LogInformation(
            "customer-balance/apply: kapsamda geri alınmamış düşüm var, 409 scope-has-deduction "
            + "(license={LicenseId}, transaction={TransactionId})",
            licenseId, existing.TransactionId);
        var problem = Problem(title: CodeScopeHasDeduction, statusCode: 409,
            detail: "Bu satışta geri alınmamış bir bakiye düşümü var.");
        var details = (ProblemDetails)problem.Value!;
        details.Extensions["transactionId"] = existing.TransactionId;
        details.Extensions["appliedAmount"] = existing.AppliedAmount;
        details.Extensions["productTotal"] = existing.ProductTotal;
        return problem;
    }

    /// <summary>
    /// Spec 2026-10-13 §6.6 — mod On'da kapsam başına tek geçerli düşüm. Tek
```

Doğrulanmış üyeler (worktree `b6ee847c` + Görev 1–6): `PersonBalance.KindPurchaseDeduction` (`PersonBalance.cs:19`), `SalesCommandRejection.Code`/`StatusCode`/`Message` (`Services/Sync/Commands/SalesCommandRejection.cs:42-45`, `Exception.Message`), `User.GetTenantCustomerId()` (`Services/Auth/TenantClaims.cs:42`), `ScopeLockResource` (`:705`), `ContentConflict()` (`:543`), `BalanceBusy` (`:686`), `CodeScopeHasDeduction` (`:75`), `ScopeResponse` (`:135`), `ApplyResponse` (`:307`); `DrawRequest`/`DrawAmountRule`/`DrawStatus`/`DrawResult`/`DrawAsync` ve `CodeNoBalance` `PersonBalance`'ın iç türleri/üyeleri (Görev 2, 4; `PersonBalance.Member`/`ReverseOutcome` kalıbı); `HasMemberIds`, `PoolClientMeetsMinimum`, `PoolClientUpdateRequired`, `MemberIdsRequired`, `CustomerNotSynced` (Görev 5); `LookupAsync(…, bool pool, …)`, `LatestScopeDeductionAsync(…, bool personPool, …)`, `ApplyRequest.MemberIds` (Görev 6). `using OrderDeck.LicenseServer.Services.Sync.Commands;` (`:10`) ve `using OrderDeck.LicenseServer.Services.Auth;` (`:7`) dosyada zaten var; `System.Diagnostics` örtük using'lerde olmadığı için `UnreachableException` tam adla.

- [ ] **Adım 5: `ApplyOncePerScopeAsync`'in 409 gövdesini ortak yardımcıya bağla (çıktı birebir)**

Edit — `ApplyOncePerScopeAsync` içinde, `if (await LatestScopeDeductionAsync(…) is { } existing)` bloğunun gövdesi (bugün `:424-434`; Görev 6 yalnız `if` satırının argümanlarını değiştirdi):

```csharp
            _log.LogInformation(
                "customer-balance/apply: kapsamda geri alınmamış düşüm var, 409 scope-has-deduction "
                + "(license={LicenseId}, transaction={TransactionId})",
                licenseId, existing.TransactionId);
            var problem = Problem(title: CodeScopeHasDeduction, statusCode: 409,
                detail: "Bu satışta geri alınmamış bir bakiye düşümü var.");
            var details = (ProblemDetails)problem.Value!;
            details.Extensions["transactionId"] = existing.TransactionId;
            details.Extensions["appliedAmount"] = existing.AppliedAmount;
            details.Extensions["productTotal"] = existing.ProductTotal;
            return problem;
```

yerine:

```csharp
            return ScopeHasDeduction(licenseId, existing);
```

Günlük satırı, başlık, metin ve üç uzantı aynen yardımcıda; `CustomerBalanceScopeRuleTests.ShouldBeScopeHasDeductionAsync` ve ilişkisel kardeşi değişmeden yeşil kalır.

- [ ] **Adım 6: Sınıf belgesine havuz yolunu ekle**

Edit — sınıf belgesinin sonu (bugün `:63-64`):

```csharp
/// </list></para>
/// </summary>
```

yerine:

```csharp
/// </list></para>
///
/// <para><b>Ortak bakiye, havuz kipi (spec 2026-10-11 §4.0–§4.2).</b> Kip
/// sahiplik sorgusunun ikinci alanıdır (<c>License.PersonBalancePoolSince</c>);
/// boşsa her uç yukarıdaki gibi çalışır. Doluysa preview ve yeni yazım yapacak
/// apply, satış senkronu kapısından ayrı olarak <c>PersonBalance:MinClientVersion</c>
/// kapısından ve <c>memberIds</c> denetiminden geçer (yeniden oynatma bu iki
/// denetimden ÖNCE; kayıtlı anahtar engellenmez). Apply çekimi
/// <c>PersonBalance.DrawAsync</c>'e bırakır: çok bacaklı yazım, kişi kilidi,
/// her denemede anahtarın yeniden aranması ve §6.6 kapsam kuralı çapa
/// düzeyinde, deneme başına işlem ve kilitle. <c>scope</c>, <c>transactions/{id}</c>
/// ve <c>reverse</c>'e havuz kapısı konmaz.</para>
/// </summary>
```

- [ ] **Adım 7: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolApplyTests"`
Expected: hepsi PASS (Theory satırları dahil 27 test), 0 uyarı.

Run (satır kipi ve önceki havuz görevleri, değiştirmeden): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRuleTests|FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceScopeRuleSumTests|FullyQualifiedName~PersonBalanceGroupReversalTests|FullyQualifiedName~PersonBalanceModeTests|FullyQualifiedName~PersonBalancePoolPreviewTests|FullyQualifiedName~PersonBalancePoolReaders|FullyQualifiedName~PersonBalanceDrawTests"`
Expected: hepsi PASS. `CustomerBalanceScopeRuleTests`'in `scope-has-deduction` testleri ortak yardımcıdan aynı gövdeyi alır; `PersonBalanceScopeRuleSumTests` (`PersonBalanceWpfReadersTests.cs:147`'de ayrı üst düzey sınıf, dosya adı süzgece takılmadığı için açıkça listelendi) `.Mod_On_kapsamda_cok_bacakli_dusum_varken_409_tutari_grup_toplami` satır kipinde 409 uzantılarını ve `appliedAmount` 120'yi (`−Σ(grup)`) ortak yardımcıdan aynen alır; `PersonBalancePoolReaders` süzgeci aynı dosyadaki iki üst düzey sınıfı da yakalar (`FullyQualifiedName` sınıf adını içerir, dosya adını değil): `PersonBalancePoolReadersTests`'in havuz kipi oynatma testleri (anahtar kayıtlı) ön oynatmada kalır, havuz dalına girmez; `PersonBalancePoolReadersModeOnTests` (mod On + havuz kipi) de havuz dalına girmeden yeşil kalır — kayıtlı anahtar ön oynatmada `(K, 120, 10)` döner, eski istemci hem `/scope`'ta hem oynatmada bakiyeyi okumadan satış senkronu kapısının 409 `client-update-required`'ını alır (§6.5-3).

Run (Docker açıkken; satır kipinin kapsam kuralı gerçek SQL Server'da, kilit + işlem): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceScopeRuleRelationalTests"`
Expected: hepsi PASS, değişmeden (`ApplyOncePerScopeAsync`'in işlem/kilit akışı aynen). Docker kapalı görünürse "Genel kurallar"daki `DOCKER_HOST` adımı; yine açılamazsa bu koşuyu Görev 17'nin tam paketine bırak ve commit gövdesine "ilişkisel regresyon Görev 17'de" yaz.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalancePoolApplyTests.cs
git commit -F - <<'MSG'
feat(balance): havuz kipinde apply'ı DrawAsync'ten geçir

Havuz kipinde yeni yazım yapacak apply, yeniden oynatmadan sonra havuz
sürüm kapısından ve memberIds denetiminden geçer, çekimi
PersonBalance.DrawAsync'e bırakır: para P'den çok bacaklı yazılır (Örnek
1, Örnek 2), S∖P'nin eksileri kilitlenir. Her denemenin başında anahtar
yeniden aranır, no-balance dönmeden önce bir kez daha (ön oynatmadan
sonra havuzu tüketen aynı anahtarlı istek oynatılır); mod On'da kapsam
kuralı çapa düzeyinde, deneme başına açılan işlem ve kilit altında
koşar, 409'un tutarı grubun toplamıdır.
409 scope-has-deduction gövdesi iki yolun ortak yardımcısına taşındı
(çıktı aynı). Bayrak boşken satır kipinin yolu değişmez. Örnek 3 (Ayır
sonrası geri alma), KVKK çekiminin geri alınması ve havuz kipinde eski
sürümle WPF reverse'in 200 alması (kapı yok) testle sabitlendi (ortak
bakiye §4.2, §5.2, §5.4, §7.5, §15-2; plan farkları 3, 4, 7, 8).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 8: Panel ve Shopper havuz okumaları (spec §4.6, §4.10, §6.1-f, M9, K3, K4)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs` — `Get` (`:73-108`; kip dalı çözümden (`:81-83`) hemen sonra, `:85` yorumu), Helpers bölümü (`:313-325`; `PoolDetailsAsync` `ResolveAsync`'ten sonra)
- Değiştir: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperBalanceController.cs` — `Get` (`:46-89`; kip dalı `canonicalId`'den (`:70`) hemen sonra), sınıf sonu (`:87-90`; `PoolDetailsAsync`)
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolPanelListTests.cs`, `OrderDeck.LicenseServer.Tests/Controllers/Shopper/PersonBalancePoolShopperListTests.cs`

İki GET'e havuz kipinin okuma dalı eklenir: kip lisans başına her istekte `PersonBalance.IsPoolModeAsync` ile okunur (Görev 1). Havuz kipinde `Balance = Σ_S` (eksi olabilir, D3), hareketler `S`'nin bütün üyelerinden `HistoryAsync` ile gruplara toplanmış (grup tutarı = bu kişideki bacakların toplamı; liste toplamı `Balance`'a eşit), `take` grup sayar, `UpdatedAt` `S`'nin son para hareketi (M9). Panelde `Members[]` iki kipte `S` (S1) — havuz dalı aynı `MembersAsync` sonucunu toplam ve Id listesi için yeniden kullanır, ikinci üyelik okuması yok. Shopper'da S1'in K4 maskesi aynen (`Reason` yalnız grubun çapası bağlı satırdaysa); silinmiş üyeler dahil (K3). Bayrak boşken iki ucun kodu (sorgular, sıra, çıktı) S1'deki gibi kalır; tek ek, dalın başındaki kip okuması. DTO'lar değişmez (panel ve Shopper istemcisi değişmez). Yazım yok, kilit yok: okumalar READ COMMITTED, ayrı sorgular (satır kipinde de öyle).

**Not (spec ↔ kod) — bu görev:**
1. **Panel `Members` sırası.** S1'de `MembersAsync` hareketlerden SONRA çağrılıyor (`PanelCustomerBalanceController.cs:104`). Havuz kipinde Id listesi hareket sorgusuna gideceği için `MembersAsync` önce çağrılır; bu yalnız havuz dalında. Satır kipinin sırası değişmez.
2. **`UpdatedAt` (spec §4.6 `:244` → §4.1 `:145`).** `S`'nin son para hareketi; hareket yoksa `S`'nin bakiye satırlarının en yeni `UpdatedAt`'i — spec kuralı, fark değil (iskelet fark 18-g'nin preview kuralıyla aynı). Yalnız "hiç bakiye satırı yok → şimdi" yedeği satır kipi kodundan gelir (`PanelCustomerBalanceController.cs:95`, `ShopperBalanceController.cs:78`); havuz dalı aynısını kullanır.
3. **Shopper'da eksi `Σ_S`.** §4.10 havuz kipinde `Balance = Σ_S` der, eksi için ayrı kural koymaz; satır kipi bugün de eksi satır bakiyesini Shopper'a gösteriyor. **Çözüm:** kırpma yok, panelle aynı toplam (§6.1-f "bütün üyeler için aynı toplam"); K4 kişi toplamının gösterilmesini kabul ediyor. Ürün olarak farklı istenirse (ör. `max(0, Σ)`) tek satır değişir.

- [ ] **Adım 1: Panel havuz okuması testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolPanelListTests.cs`:

```csharp
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Controllers.Panel;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panel GET, havuz kipi (spec 2026-10-11 §4.6, §6.1-f, M9, K3): <c>Balance = Σ_S</c> (eksi olabilir), hareketler
/// <c>S</c>'nin bütün üyelerinden gruplara toplanmış (grup tutarı bu kişideki bacakların toplamı, liste toplamı =
/// <c>Balance</c>), <c>take</c> grup sayar, <c>UpdatedAt</c> <c>S</c>'nin son para hareketi, <c>Members[]</c> <c>S</c>.
/// Kişinin hangi üyesinden açılırsa açılsın aynı toplam. Bayrak boşken satır kipi (S1) aynen.
/// </summary>
public sealed class PersonBalancePoolPanelListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolPanelListTests(ApiFactory factory) => _factory = factory;

    private static async Task<PanelCustomerBalanceController.BalanceDetailsResponse> GetAsync(
        Kit kit, Guid row, int take = 50)
    {
        var response = await kit.PanelGetAsync(row, take);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PanelCustomerBalanceController.BalanceDetailsResponse>())!;
    }

    [Fact]
    public async Task Havuz_kipinde_bakiye_S_toplami_eksi_olabilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();

        var onA = await GetAsync(kit, a);
        var onB = await GetAsync(kit, b);

        onA.Balance.Balance.Should().Be(-50m, "borç operatöre görünmeli (D3)");
        onB.Balance.Balance.Should().Be(-50m);
        onA.Transactions.Sum(t => t.Amount).Should().Be(onA.Balance.Balance);
    }

    [Fact]
    public async Task Havuz_kipinde_hareketler_S_nin_butun_uyelerinden_toplanmis()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();

        var onT = await GetAsync(kit, ex.T);

        onT.Balance.Balance.Should().Be(10m);
        onT.Transactions.Should().HaveCount(3);
        // K bu kişideki bütün bacaklarıyla tek satır: 0 (T) − 100 (I) − 20 (Y); en yeni grup.
        onT.Transactions[0].Should().BeEquivalentTo(new
        {
            Id = ex.K, Amount = -120m, Kind = Kit.KindDeduction, OriginalAmount = 120m,
        });
        // I'nin ve Y'nin iadeleri (Örnek 1'in başlangıç bakiyeleri) rota T olsa da listede.
        onT.Transactions.Where(t => t.Kind == "refund-full").Select(t => t.Amount)
            .Should().BeEquivalentTo(new[] { 100m, 30m });
        onT.Transactions.Sum(t => t.Amount).Should().Be(onT.Balance.Balance);
    }

    [Fact]
    public async Task Kisinin_her_uyesinden_ayni_toplam()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();

        var onT = await GetAsync(kit, ex.T);
        var onI = await GetAsync(kit, ex.I);
        var onY = await GetAsync(kit, ex.Y);

        foreach (var (route, body) in new[] { (ex.T, onT), (ex.I, onI), (ex.Y, onY) })
        {
            body.Balance.Balance.Should().Be(10m);
            body.Balance.WpfCustomerId.Should().Be(route, "yanıttaki Id rotadaki Id");
            body.Transactions.Select(t => t.Id).Should().Equal(onT.Transactions.Select(t => t.Id));
            body.Transactions.Select(t => t.Amount).Should().Equal(onT.Transactions.Select(t => t.Amount));
        }
    }

    [Fact]
    public async Task take_grup_sayar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g);
        var t0 = DateTimeOffset.UtcNow;
        await kit.FundAsync(a, 10m, at: t0.AddMinutes(-3));
        var second = await kit.FundAsync(b, 20m, at: t0.AddMinutes(-2));
        var third = await kit.FundAsync(a, 30m, at: t0.AddMinutes(-1));
        await kit.EnablePoolAsync();

        var list = await GetAsync(kit, a, take: 2);

        // İki üyeden en yeni iki grup; satır kipinde A'nın kendi iki hareketi dönerdi.
        list.Transactions.Select(t => t.Id).Should().Equal(third, second);
        list.Balance.Balance.Should().Be(60m, "take yalnız listeyi keser, toplamı değil");
    }

    [Fact]
    public async Task take_cok_bacakli_grubu_tek_sayar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();

        var list = await GetAsync(kit, ex.T, take: 1);

        // En yeni grup K'nın üç üyeye yayılmış üç bacağı tek satır; satır sayan bir kesim tek bacak (0 ya da −100) dönerdi.
        list.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ex.K, Amount = -120m });
    }

    [Fact]
    public async Task UpdatedAt_S_nin_son_para_hareketi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var an = DateTimeOffset.UtcNow.AddMinutes(-1);
        an = an.AddTicks(-(an.UtcTicks % TimeSpan.TicksPerSecond));
        // Y'ye en yeni hareket; bakiye satırının jetonu kayıtta "şimdi"ye damgalanır — M9 onu değil hareketi gösterir.
        await kit.FundAsync(ex.Y, 5m, at: an);
        await kit.EnablePoolAsync();

        var onT = await GetAsync(kit, ex.T);

        onT.Balance.UpdatedAt.Should().Be(an);
        onT.Balance.Balance.Should().Be(15m);
    }

    [Fact]
    public async Task UpdatedAt_hareket_yoksa_S_nin_en_yeni_bakiye_satiri()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g);
        await kit.EnablePoolAsync();
        // İki üye de 0'lık satırla, hareketsiz; B sonra açıldığı için satırı daha yeni (spec §4.1 `:145`).
        var expected = new[] { (await kit.BalanceRowAsync(a))!.UpdatedAt, (await kit.BalanceRowAsync(b))!.UpdatedAt }.Max();

        var onA = await GetAsync(kit, a);

        onA.Balance.UpdatedAt.Should().Be(expected, "yalnız rota satırına bakan yedek A'nınkini dönerdi");
        onA.Transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Members_havuz_kipinde_de_S()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 20m);
        var p = await kit.MemberAsync(g, 5m, purged: true);
        await kit.EnablePoolAsync();

        var list = await GetAsync(kit, a);

        var expected = new[] { a, b, p }.OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToArray();
        list.Members.Select(m => m.WpfCustomerId).Should().Equal(expected);
        list.Members.Single(m => m.WpfCustomerId == p).Should().BeEquivalentTo(new { Balance = 5m, Purged = true });
        list.Members.Single(m => m.WpfCustomerId == b).Should().BeEquivalentTo(new { Balance = 20m, Purged = false });
        list.Balance.Balance.Should().Be(list.Members.Sum(m => m.Balance));
    }

    [Fact]
    public async Task Silinmis_uyenin_bakiyesi_toplama_girer()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        await kit.MemberAsync(g, 40m, purged: true);
        await kit.EnablePoolAsync();

        var onA = await GetAsync(kit, a);

        onA.Balance.Balance.Should().Be(40m, "silinmiş üyenin parası kişinin (K3)");
        onA.Transactions.Should().ContainSingle().Which.Amount.Should().Be(40m);
    }

    [Fact]
    public async Task Satir_kipinde_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var onT = await GetAsync(kit, ex.T);

        onT.Balance.Balance.Should().Be(0m, "bayrak boş: bakiye rota satırınınki");
        onT.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ex.K, Amount = 0m });
        onT.Members.Should().HaveCount(3, "Members iki kipte de S");
    }
}
```

- [ ] **Adım 2: Shopper havuz okuması testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Shopper/PersonBalancePoolShopperListTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// Shopper GET, havuz kipi (spec 2026-10-11 §4.10, K3, K4): bağlantının kişisi (<c>S</c>) toplamı, hareketler
/// <c>S</c>'nin bütün üyelerinden toplanmış (silinmiş üye dahil); açıklama yalnız grubun çapası bağlı satırdaysa döner
/// (S1 maskesi aynen). Bayrak boşken satır kipi (S1) aynen.
/// </summary>
public sealed class PersonBalancePoolShopperListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolShopperListTests(ApiFactory factory) => _factory = factory;

    private sealed record BalanceDto(decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, DateTimeOffset CreatedAt);
    private sealed record Response(BalanceDto Balance, TransactionDto[] Transactions);
    private sealed record AuthDto(string AccessToken);

    /// <summary>Yayıncının lisansına bir Shopper kaydı; kayıt bağlantıyı ve projeksiyonu açar. Döner: istemci ve
    /// bağlı projeksiyonun Id'si. Kaynak: <c>PersonBalanceShopperListTests.RegisterShopperAsync</c> (S1), aynen.</summary>
    private async Task<(HttpClient Client, Guid Linked)> RegisterShopperAsync(Kit kit)
    {
        var code = "pb" + Guid.NewGuid().ToString("N")[..8];
        await kit.Sales.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId);
            license.ShopperCode = code;
            license.ShopperCodeUpdatedAt = DateTimeOffset.UtcNow;
        });
        var client = _factory.CreateClient();
        var giris = $"g-{Guid.NewGuid():N}";
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", new
        {
            broadcasterCode = code, fullName = "Bakiye Alıcı", phone = TestPhone.NewE164(), password = giris,
            address = "Adres", platform = "youtube", username = $"s{Guid.NewGuid():N}"[..12],
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = await resp.Content.ReadFromJsonAsync<AuthDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        var linked = await kit.Sales.ReadDbAsync(db => db.ShopperBroadcasterLinks.AsNoTracking()
            .Where(l => l.LicenseId == kit.LicenseId && l.WpfCustomerId != null)
            .Select(l => l.WpfCustomerId!.Value).SingleAsync());
        return (client, linked);
    }

    /// <summary>Kaynak: <c>PersonBalanceShopperListTests.GetAsync</c> (S1), aynen.</summary>
    private static async Task<Response> GetAsync(HttpClient client, Guid licenseId) =>
        (await client.GetFromJsonAsync<Response>($"/api/v1/shopper/broadcasters/{licenseId}/balance"))!;

    /// <summary>Bağlantıyı kişinin bir üyesine taşır (S1 testindeki kopya bağlantısıyla aynı yol).</summary>
    private static Task LinkToAsync(Kit kit, Guid row) =>
        kit.Sales.WithDbAsync(async db =>
            (await db.ShopperBroadcasterLinks.SingleAsync(l => l.LicenseId == kit.LicenseId)).WpfCustomerId = row);

    [Fact]
    public async Task Havuz_kipinde_S_toplami_ve_hareketleri()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, _) = await RegisterShopperAsync(kit);
        var ex = await kit.Example1Async();
        await LinkToAsync(kit, ex.T);
        await kit.EnablePoolAsync();

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(10m);
        body.Transactions.Should().HaveCount(3);
        body.Transactions.Single(t => t.Id == ex.K).Should().BeEquivalentTo(new
        {
            Amount = -120m, Kind = Kit.KindDeduction, OriginalAmount = 120m,
        });
        body.Transactions.Sum(t => t.Amount).Should().Be(body.Balance.Balance);
    }

    [Fact]
    public async Task Reason_maskesi_baska_uyenin_aciklamasi_null()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, _) = await RegisterShopperAsync(kit);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 30m);
        await LinkToAsync(kit, t);
        var kendiNotu = $"n-{Guid.NewGuid():N}";
        var digerNotu = $"n-{Guid.NewGuid():N}";
        var draw = await kit.SeedDrawAsync(Kit.KindManual, [(t, 0m), (i, 5m)], reason: kendiNotu);
        var fund = await kit.FundAsync(i, 50m, reason: digerNotu);
        await kit.EnablePoolAsync();

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(75m);
        // Çapası bağlı satırda (T) olan grup: açıklama görünür; tutar bu kişideki bacakların toplamı.
        body.Transactions.Single(x => x.Id == draw[0]).Should().BeEquivalentTo(new
        {
            Amount = -5m, Kind = Kit.KindManual, Reason = kendiNotu,
        });
        // Başka üyenin (I) iadesi: tutar ve tür görünür, serbest metin gizli (K4).
        body.Transactions.Single(x => x.Id == fund).Should().BeEquivalentTo(new
        {
            Amount = 50m, Kind = "refund-full", Reason = (string?)null,
        });
        body.Transactions.Should().NotContain(x => x.Reason == digerNotu);
    }

    [Fact]
    public async Task Silinmis_uye_dahil()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, _) = await RegisterShopperAsync(kit);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, purged: true);
        await LinkToAsync(kit, t);
        var fund = await kit.FundAsync(p, 40m, reason: $"n-{Guid.NewGuid():N}");
        await kit.EnablePoolAsync();

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(40m, "silinmiş üyenin parası kişinin (K3)");
        body.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = fund, Amount = 40m, Reason = (string?)null,
        });
    }

    [Fact]
    public async Task Satir_kipinde_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, _) = await RegisterShopperAsync(kit);
        var ex = await kit.Example1Async();
        await LinkToAsync(kit, ex.T);

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(0m, "bayrak boş: bakiye bağlı satırınki");
        body.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ex.K, Amount = 0m });
    }
}
```

- [ ] **Adım 3: Koştur — beklenen FAIL**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolPanelListTests|FullyQualifiedName~PersonBalancePoolShopperListTests"`

Expected: derleme temiz (`EnablePoolAsync` Görev 1'den), 14 testin 12'si FAIL — denetleyiciler bayrağı okumadığı için havuz kipinde de satır kipinin yanıtı döner:
- `Havuz_kipinde_bakiye_S_toplami_eksi_olabilir`: "Expected onA.Balance.Balance to be -50M …, but found 10M."
- `Havuz_kipinde_hareketler_S_nin_butun_uyelerinden_toplanmis`: `Balance` 0 (yalnız T'nin satırı).
- `Kisinin_her_uyesinden_ayni_toplam`: T'de 0 (I'de 0, Y'de 10).
- `take_grup_sayar`: liste A'nın kendi iki hareketi (`third`, ilki) — `second` yok.
- `take_cok_bacakli_grubu_tek_sayar`: tek satır K, ama tutarı T'deki bacak (0) — "expected Amount -120M, found 0M".
- `UpdatedAt_S_nin_son_para_hareketi`: T'nin son hareketi (K, ~5 dk önce) döner.
- `UpdatedAt_hareket_yoksa_S_nin_en_yeni_bakiye_satiri`: A'nın kendi satırının `UpdatedAt`'i döner (B'ninkinden eski).
- `Members_havuz_kipinde_de_S`: `Members` zaten `S` (S1), ama `Balance` satır kipinde A'nın satırı (10), `Members` toplamı 35 — "expected 35M, found 10M".
- `Silinmis_uyenin_bakiyesi_toplama_girer`: "… to be 40M …, but found 0M."
- Shopper `Havuz_kipinde_S_toplami_ve_hareketleri` (0), `Reason_maskesi_baska_uyenin_aciklamasi_null` ("Expected body.Balance.Balance to be 75M …, but found 0M" — satır kipi yalnız T'nin satırı), `Silinmis_uye_dahil` (0).

PASS (bekçi, şimdiden yeşil): iki `Satir_kipinde_bugunku_gibi`.

- [ ] **Adım 4: Panel GET havuz dalı**

`PanelCustomerBalanceController.cs`, `Get` içinde çözümden sonrası — bugünkü kod (`:81-87`):

```csharp
        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        // Ortak bakiye S1, satır kipi (spec 2026-10-11 §4.6): bakiye ve hareketler rota satırınınki, hareketler
        // gruplara toplanmış; take grup sayar. UpdatedAt son para hareketidir (M9): grup değişimi kancasının ve
        // birleştiricinin jeton dokunuşu "güncellendi" zamanını oynatmaz.
```

şununla değişir (satır kipinin geri kalanı, `:88-107`, aynen kalır):

```csharp
        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        // Ortak bakiye kipi lisans başına, her istekte okunur (spec 2026-10-11 §4.0). Havuz kipi ayrı yöntemde;
        // satır kipinin yolu aşağıda S1'deki gibi.
        if (await _person.IsPoolModeAsync(licenseId, ct))
            return Ok(await PoolDetailsAsync(wpfCustomerId, licenseId, canonicalId, take, ct));

        // Ortak bakiye satır kipi (bayrak boş; spec 2026-10-11 §4.6): bakiye ve hareketler rota satırınınki, hareketler
        // gruplara toplanmış; take grup sayar. UpdatedAt son para hareketidir (M9): grup değişimi kancasının ve
        // birleştiricinin jeton dokunuşu "güncellendi" zamanını oynatmaz.
```

Helpers bölümünde `ResolveAsync`'in sonundan (`:323-325`) sonra — bugünkü kod:

```csharp
        => await _customerIds.LocateForTenantAsync(callerCustomerId, wpfCustomerId, ct) is { } located
            ? (located.LicenseId, located.CanonicalId, true)
            : (Guid.Empty, Guid.Empty, false);
```

şununla değişir:

```csharp
        => await _customerIds.LocateForTenantAsync(callerCustomerId, wpfCustomerId, ct) is { } located
            ? (located.LicenseId, located.CanonicalId, true)
            : (Guid.Empty, Guid.Empty, false);

    /// <summary>
    /// GET, havuz kipi (spec 2026-10-11 §4.6, §6.1-f, M9, K3). <c>Balance = Σ_S</c>: eksi olabilir — borç operatöre
    /// görünmeli (D3); silinmiş üyeler dahil. Hareketler S'nin bütün üyelerinden gruplara toplanmış: grup tutarı bu
    /// kişideki bacakların toplamı (ayırmadan sonra öbür kişide kalan bacak sayılmaz), take grup sayar. UpdatedAt
    /// S'nin son para hareketi; S'de hiç hareket yoksa S'nin bakiye satırlarının en yenisi, o da yoksa şimdi.
    /// <c>Members</c> aynı okumadan (ikinci üyelik okuması yok); kişinin hangi üyesinden açılırsa açılsın aynı toplam
    /// ve aynı liste. Salt okuma, kilit yok (READ COMMITTED, ayrı sorgular — satır kipinde de öyle).
    /// </summary>
    /// <param name="requestId">Rotadaki Id — yanıtta o döner (kopya Id'si olabilir; satır kipiyle aynı).</param>
    private async Task<BalanceDetailsResponse> PoolDetailsAsync(
        Guid requestId, Guid licenseId, Guid canonicalId, int take, CancellationToken ct)
    {
        var person = await _person.MembersAsync(licenseId, canonicalId, ct);
        var ids = person.Members.Select(m => m.WpfCustomerId).ToList();

        var lastMovement = await _person.LastMovementAtAsync(licenseId, ids, ct);
        var updatedAt = lastMovement
            ?? await _db.CustomerBalances.AsNoTracking()
                .Where(b => b.LicenseId == licenseId && ids.Contains(b.WpfCustomerId))
                .MaxAsync(b => (DateTimeOffset?)b.UpdatedAt, ct)
            ?? DateTimeOffset.UtcNow;
        var balance = new BalanceDto(requestId, licenseId, person.Members.Sum(m => m.Balance), updatedAt);

        // Panel yayıncının kendisi: açıklama maskesiz.
        var history = await _person.HistoryAsync(licenseId, ids, take, ct);
        var transactions = history
            .Select(h => new TransactionDto(h.Id, h.Amount, h.Kind, h.OriginalAmount, h.ShippingDeducted,
                h.Reason, h.ReversesTransactionId, h.CreatedAt))
            .ToArray();

        var members = person.Members.Select(m => new MemberDto(m.WpfCustomerId, m.Balance, m.Purged)).ToArray();
        return new BalanceDetailsResponse(balance, transactions, members);
    }
```

(`using`'ler yeterli: `Microsoft.EntityFrameworkCore` `AsNoTracking`/`MaxAsync` için zaten var, `:3`.)

- [ ] **Adım 5: Shopper GET havuz dalı**

`ShopperBalanceController.cs`, `Get` — bugünkü kod (`:68-72`):

```csharp
        // Ortak bakiye (spec 2026-10-11 §4.10): kişi çözümü asıl kayıttan başlar. Birleştirme bağlantıyı taşıdığı
        // için bugün çoğunlukla aynı Id; eski bir bağlantı kopyaya işaret edebilir.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, link.WpfCustomerId, ct);

        var row = await _db.CustomerBalances.AsNoTracking()
```

şununla değişir (satır kipinin geri kalanı, `:72-88`, aynen kalır):

```csharp
        // Ortak bakiye (spec 2026-10-11 §4.10): kişi çözümü asıl kayıttan başlar. Birleştirme bağlantıyı taşıdığı
        // için bugün çoğunlukla aynı Id; eski bir bağlantı kopyaya işaret edebilir.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, link.WpfCustomerId, ct);

        // Kip lisans başına, her istekte okunur (§4.0). Havuz kipi ayrı yöntemde; satır kipi aşağıda S1'deki gibi.
        if (await _person.IsPoolModeAsync(licenseId, ct))
            return Ok(await PoolDetailsAsync(licenseId, canonicalId, take, ct));

        var row = await _db.CustomerBalances.AsNoTracking()
```

Sınıfın sonu — bugünkü kod (`:87-90`):

```csharp

        return Ok(new BalanceDetailsResponse(balance, transactions));
    }
}
```

şununla değişir:

```csharp

        return Ok(new BalanceDetailsResponse(balance, transactions));
    }

    /// <summary>
    /// GET, havuz kipi (spec 2026-10-11 §4.10, K3, K4). <c>Balance = Σ_S</c> (silinmiş üyeler dahil; panelle aynı
    /// toplam, §6.1-f — eksi için ayrı kural yok). Hareketler S'nin bütün üyelerinden gruplara toplanmış, take grup
    /// sayar; UpdatedAt S'nin son para hareketi (M9; yoksa S'nin bakiye satırlarının en yenisi, o da yoksa şimdi).
    /// K4 maskesi satır kipindekiyle aynı ifade: açıklama yalnız grubun çapası bağlı satırdaysa döner — başka üyenin
    /// hareketinde tutar ve tür görünür, serbest metin gizli. Salt okuma, kilit yok.
    /// </summary>
    private async Task<BalanceDetailsResponse> PoolDetailsAsync(
        Guid licenseId, Guid canonicalId, int take, CancellationToken ct)
    {
        var person = await _person.MembersAsync(licenseId, canonicalId, ct);
        var ids = person.Members.Select(m => m.WpfCustomerId).ToList();

        var lastMovement = await _person.LastMovementAtAsync(licenseId, ids, ct);
        var updatedAt = lastMovement
            ?? await _db.CustomerBalances.AsNoTracking()
                .Where(b => b.LicenseId == licenseId && ids.Contains(b.WpfCustomerId))
                .MaxAsync(b => (DateTimeOffset?)b.UpdatedAt, ct)
            ?? DateTimeOffset.UtcNow;
        var balance = new BalanceDto(person.Members.Sum(m => m.Balance), updatedAt);

        var history = await _person.HistoryAsync(licenseId, ids, take, ct);
        var transactions = history
            .Select(h => new TransactionDto(h.Id, h.Amount, h.Kind, h.OriginalAmount, h.ShippingDeducted,
                h.AnchorWpfCustomerId == canonicalId ? h.Reason : null, h.CreatedAt))
            .ToArray();

        return new BalanceDetailsResponse(balance, transactions);
    }
}
```

- [ ] **Adım 6: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolPanelListTests|FullyQualifiedName~PersonBalancePoolShopperListTests"`
Expected: 14/14 PASS.

Run (Panel/Shopper regresyonu, testler değiştirilmeden): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PanelCustomerBalanceControllerTests|FullyQualifiedName~PersonBalancePanelListTests|FullyQualifiedName~PersonBalancePanelReverseTests|FullyQualifiedName~PersonBalanceShopperListTests|FullyQualifiedName~ShopperBalanceControllerTests"`
Expected: hepsi PASS. Bu sınıfların hiçbiri bayrağı açmaz; iki GET'in satır kipi kodu ve çıktısı aynı (eklenen tek şey dalın başındaki kip okuması).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs \
  OrderDeck.LicenseServer/Controllers/Shopper/ShopperBalanceController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolPanelListTests.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Shopper/PersonBalancePoolShopperListTests.cs
git commit -F - <<'MSG'
feat(balance): panel ve Shopper havuz kipinde kişinin toplamını okusun

Havuz kipindeki lisansta iki GET bakiyeyi kişinin bütün üyelerinin
toplamı olarak döner (panelde eksi olabilir; silinmiş üyeler dahil),
hareketleri S'nin bütün üyelerinden gruplara toplar ve UpdatedAt'i S'nin
son para hareketinden alır. Kişinin hangi üyesinden açılırsa açılsın aynı
toplam görünür. Shopper'da açıklama maskesi aynen: başka üyede çapalanan
grubun açıklaması gizli (ortak bakiye §4.6, §4.10, §6.1-f, M9, K3, K4).
Bayrak boşken iki ucun satır kipi kodu değişmez; DTO'lar aynı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 9: K5: panel eksi manual-adjustment (havuz kipi) (spec §4.8, §4.13, K5, M7, M12)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs` — `using`'ler (`:1-7`), `ManualAdjustment` (bugün `:179-208`; çözüm `:189-191`, satır kipi yorumu `:193-195`), yeni `DrawManualAdjustmentAsync` (`ManualAdjustment`'tan hemen sonra, `// ── POST reverse` başlığından `:210` önce), `ReverseDrawGroupAsync`'in `Busy` kolu (`:302-307`), yeni `BalanceBusy` (Helpers bölümü `:313`'ün hemen altı). Görev 8'in GET havuz dalı satırları birkaç satır aşağı kaydırır; Edit bağlamları aşağıdaki alıntılarla bulunur.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolManualAdjustmentTests.cs` (`IClassFixture<ApiFactory>`, InMemory)

**Not (spec ↔ kod):**
- Spec'in satır numaraları kaydı (iskelet farkı 1): `manual-adjustment` spec `:164-193` → bugün `:177-208`; `Reason` zorunluluğu spec `:171-172` → `:186-187`. Doğrulama havuz dalından ÖNCE kalır: boş/boşluk açıklama iki kipte de 400 `reason-required`, sıfır tutar 400 `invalid-amount`.
- İskelet farkı 11 (M12) uygulanır: havuz kipinin eksi ayarı `ApplyTransactionAsync`'ten geçmez, `PersonBalance.DrawAsync`'ten geçer; denetleyici çağırmadan önce izleyiciye hiçbir şey eklemez. İskelet farkı 12 uygulanır: üç denemeden sonra 503 `balance-busy`, panel grup geri almasıyla aynı metin; bunun için `ReverseDrawGroupAsync`'in satır içi 503'ü ortak `BalanceBusy` yardımcısına taşınır (yanıt ve Debug satırının işlenmiş metni "reverse" için birebir aynı).
- Kip, `DrawAsync`'ten önce ayrı bir sorguyla (`IsPoolModeAsync`) okunur; kip okuması ile yazım arasında koşan bir `disable` sonrası bu istek yine çok bacaklı grup yazar. Sahip sorusu değil: S1 tabanı satır kipinde de çekim gruplarını okur, listeler ve grup olarak geri alır (§4.9 "S1 tabanı", §7.4); spec kipi "her istekte" okunacak bir lisans bayrağı olarak tanımlar (§4.0), istek içi atomiklik istemez.
- Kuruş altı eksi tutar (Görev 4'ün devrettiği karar, Görev 4 Not "*`Requested` kuruş hassasiyeti*"): `DrawAsync` `Requested`'ı 2 ondalığı aşarsa döngüden önce `ArgumentOutOfRangeException` atar (`decimal(18,2)` bacağı ve satırı ayrı yuvarlar, D1 bozulurdu). Panel `manual-adjustment` bugün yalnız `Amount == 0`'ı reddeder (`:185`); doğrulama olmasa havuz kipinde `-10.005` 500 olurdu. Karar bu görevde kapanır: havuz dalında, `DrawManualAdjustmentAsync`'ten önce apply'ın emsali (`LicensesCustomerBalanceApplyController.cs:319-322`) → 400 `invalid-money-scale`, hiçbir şey yazılmaz. Satır kipi ve artı tutar birebir aynı kalır (bugün SQL yuvarlar; R47 "bugünkü gibi"). İskeletin "Sahip soruları"na madde eklenmez: 500 yerine apply'la aynı 400 yeni bir özellik değil, havuz dalının girdi doğrulamasıdır.
- Yeni spec farkı yok. Sahip sorusu adayı yok.

Kural: kip yalnız **eksi** tutarda okunur. Bayrak boşken ya da tutar artıyken akış bugünkü koddur, birebir: `ApplyTransactionAsync(…, disallowNegative: true)`, 409 `insufficient-balance`, 3. jeton çakışmasında 500 (R47). Havuz kipinde eksi tutar `DrawAsync(AllOrNothing, MemberIds = null)` olur: `P = S`; çapa rota satırının asıl kaydında (`manual-adjustment`, `Reason` yalnız çapada, çapanın bakiye satırı yoksa 0 ile açılır); kalan `S`'nin öbür pozitif üyelerinden M7 sırasıyla (silinmişler önce); `max(0, Σ_S) < |tutar|` → hiçbir şey yazılmaz, 409 `insufficient-balance`; tek `SaveChanges`, ortak `CreatedAt`, `DrawGroupId` = çapa; kişi kilidi `S`'nin bakiye satırı olan bütün üyelerine (R48, R56). Çakışma servisin döngüsünde baştan kurulur; tükenince 503 (R49). Metrik sayaçları Görev 13'te eklenir: panel kurucusu bu görevde DEĞİŞMEZ. Yarış ve 503 testleri Görev 16'da (Testcontainers 9 ve 14); InMemory'de çakışma iddiası yok.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolManualAdjustmentTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// K5 — panel eksi <c>manual-adjustment</c> (spec 2026-10-11 §4.8, §4.13, M7, M12). Havuz kipinde eksi ayar kişinin
/// havuzundan çok bacaklı çekimdir: çapa rota satırının asıl kaydında (<c>Reason</c> yalnız orada), kalan S'nin öbür
/// pozitif üyelerinden (silinmişler önce); havuz yetmezse hiçbir şey yazılmaz → 409 <c>insufficient-balance</c>.
/// Artı tutar iki kipte, eksi tutar satır kipinde bugünkü gibi (R47). Açıklamalar üretilir. Çakışma ve 503 yalnız
/// Testcontainers'ta kanıtlanır (InMemory'de SaveChanges atomik değil).
/// </summary>
public sealed class PersonBalancePoolManualAdjustmentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolManualAdjustmentTests(ApiFactory factory) => _factory = factory;

    private sealed record BalanceDto(Guid WpfCustomerId, Guid LicenseId, decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, Guid? ReversesTransactionId, DateTimeOffset CreatedAt);
    private sealed record MemberDto(Guid WpfCustomerId, decimal Balance, bool Purged);
    private sealed record Response(BalanceDto Balance, TransactionDto[] Transactions, MemberDto[] Members);

    private static string NewReason() => $"r-{Guid.NewGuid():N}";

    /// <summary>Yanıt gövdesiz (<c>Ok()</c>): çapa, üretilmiş açıklamasıyla bulunur (açıklama yalnız çapada).</summary>
    private static Task<Guid> AnchorIdAsync(Kit kit, string reason) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindManual && t.Reason == reason)
            .Select(t => t.Id)
            .SingleAsync());

    private static Task<List<CustomerBalanceTransaction>> RowsWithReasonAsync(Kit kit, string reason) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == kit.LicenseId && t.Reason == reason)
            .ToListAsync());

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId));

    /// <summary>Asıl kayda birleşmiş bir kopya (A5b): grubu ve bakiye satırı yok.</summary>
    private static async Task<Guid> CopyOfAsync(Kit kit, Guid canonical)
    {
        var id = Guid.NewGuid();
        await kit.Sales.WithDbAsync(db =>
        {
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = id, LicenseId = kit.LicenseId, Platform = "youtube", Username = $"k{Guid.NewGuid():N}",
                MergedIntoId = canonical, UpdatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });
        return id;
    }

    [Fact]
    public async Task K5_ornegi_A10_B40_rota_A_eksi30()
    {
        // Spec §4.8 örneği: çapa (A, −10, Reason), bacak (B, −20) → A = 0, B = 20.
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var reason = NewReason();

        var resp = await kit.PanelManualAdjustmentAsync(a, -30m, reason);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var anchor = await AnchorIdAsync(kit, reason);
        var rows = await kit.GroupRowsAsync(anchor);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new
        {
            Id = anchor, WpfCustomerId = a, Amount = -10m, Kind = Kit.KindManual, Reason = reason,
            DrawGroupId = (Guid?)anchor, OriginalAmount = (decimal?)null, SaleScope = (string?)null,
            ReversesTransactionId = (Guid?)null,
        });
        rows[1].Should().BeEquivalentTo(new
        {
            WpfCustomerId = b, Amount = -20m, Kind = Kit.KindManual, Reason = (string?)null,
            DrawGroupId = (Guid?)anchor,
        });
        rows.Select(r => r.CreatedAt).Distinct().Should().ContainSingle("tek SaveChanges, ortak CreatedAt");
        rows.Should().OnlyContain(r => r.CreatedByCustomerId == kit.CustomerId);
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(20m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Havuz_yetmezse_409_insufficient_balance_hicbir_sey_yazilmaz()
    {
        // Σ_S = 10 + 40 − 20 = 30 < 40: eksi üye havuzu küçültür; kısmi ayar yok (hep-ya-hiç).
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var c = await kit.MemberAsync(g, -20m);
        var before = await TransactionCountAsync(kit);

        var resp = await kit.PanelManualAdjustmentAsync(a, -40m, NewReason());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("insufficient-balance");
        (await TransactionCountAsync(kit)).Should().Be(before, "hiçbir şey yazılmaz");
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.BalanceAsync(c)).Should().Be(-20m);
    }

    [Fact]
    public async Task Havuz_tam_yeterse_eksi_uyeye_dokunulmaz()
    {
        // Sınır: max(0, Σ_S) = 30 = |tutar| → yeter. Para yalnız pozitif üyelerden; C'nin eksisi yalnız toplamda.
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var c = await kit.MemberAsync(g, -20m);
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await kit.GroupRowsAsync(await AnchorIdAsync(kit, reason));
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, -10m), (b, -20m));
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(20m);
        (await kit.BalanceAsync(c)).Should().Be(-20m);
    }

    [Fact]
    public async Task Silinmis_uye_canlidan_once()
    {
        // M7: çapa → silinmiş üyeler → canlı üyeler. P (silinmiş, 35) B'den (canlı, 40) küçük olsa da önce çekilir.
        // Bakiye sırası tek başına B'yi seçerdi; P'yi yalnız silinmiş-önce kuralı seçtirebilir (eşitlik kırıcıya kalmaz).
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, 35m, purged: true);
        var b = await kit.MemberAsync(g, 40m);
        var bBefore = (await kit.BalanceRowAsync(b))!;
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await kit.GroupRowsAsync(await AnchorIdAsync(kit, reason));
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, 0m), (p, -30m));
        rows[0].Reason.Should().Be(reason);
        rows[1].Reason.Should().BeNull();
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(p)).Should().Be(5m);
        var bAfter = (await kit.BalanceRowAsync(b))!;
        bAfter.Balance.Should().Be(40m, "canlı üyeye sıra gelmedi");
        bAfter.UpdatedAt.Should().BeAfter(bBefore.UpdatedAt,
            "kişi kilidi: S'nin bakiye satırı olan bütün üyelerinin jetonuna dokunulur (§4.13, R56)");
        (await kit.LedgerHoldsAsync(p)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Havuz_kipinde_kurus_alti_eksi_400_invalid_money_scale_hicbir_sey_yazilmaz()
    {
        // Havuz (50) tutarı karşılar; tek engel ölçek. DrawAsync kuruş altı Requested'ta istisna atar (Görev 4):
        // denetleyici çağırmadan önce apply'ın emsaliyle 400 döner, 500 değil (spec ↔ kod notu).
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var before = await TransactionCountAsync(kit);

        var resp = await kit.PanelManualAdjustmentAsync(a, -10.005m, NewReason());

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Kit.TitleAsync(resp)).Should().Be("invalid-money-scale");
        (await TransactionCountAsync(kit)).Should().Be(before, "hiçbir şey yazılmaz");
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    [Fact]
    public async Task Rota_satirinin_bakiye_satiri_yoksa_0_capa()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, balanceRow: false);
        var b = await kit.MemberAsync(g, 40m);
        (await kit.BalanceRowAsync(a)).Should().BeNull();
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        var opened = await kit.BalanceRowAsync(a);
        opened.Should().NotBeNull("çapanın bakiye satırı 0 ile açılır ve kilide girer (§4.2-5)");
        opened!.Balance.Should().Be(0m);
        var rows = await kit.GroupRowsAsync(await AnchorIdAsync(kit, reason));
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, 0m), (b, -30m));
        (await kit.BalanceAsync(b)).Should().Be(10m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Rota_kopya_Id_ise_asil_kayit_capadir()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var copy = await CopyOfAsync(kit, a);
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(copy, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await kit.GroupRowsAsync(await AnchorIdAsync(kit, reason));
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, -10m), (b, -20m));
        (await kit.BalanceRowAsync(copy)).Should().BeNull("kopyaya asla bakiye satırı açılmaz (A5b)");
        (await kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .AnyAsync(t => t.WpfCustomerId == copy))).Should().BeFalse();
    }

    [Fact]
    public async Task Arti_tutar_havuz_kipinde_de_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(a, 25m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RowsWithReasonAsync(kit, reason)).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            WpfCustomerId = a, Amount = 25m, Kind = Kit.KindManual, DrawGroupId = (Guid?)null,
        }, "artı tutar iki kipte rota satırına tek satır (R47)");
        (await kit.BalanceAsync(a)).Should().Be(35m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    [Fact]
    public async Task Satir_kipinde_eksi_bugunku_gibi()
    {
        // Bayrak boş: kişinin havuzu (50) yetse de yalnız rota satırı (10) sayılır.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var before = await TransactionCountAsync(kit);

        var refused = await kit.PanelManualAdjustmentAsync(a, -30m, NewReason());

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(refused)).Should().Be("insufficient-balance");
        (await TransactionCountAsync(kit)).Should().Be(before);

        var reason = NewReason();
        (await kit.PanelManualAdjustmentAsync(a, -5m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RowsWithReasonAsync(kit, reason)).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            WpfCustomerId = a, Amount = -5m, Kind = Kit.KindManual, DrawGroupId = (Guid?)null,
        }, "satır kipi tek satır yazar, DrawGroupId NULL");
        (await kit.BalanceAsync(a)).Should().Be(5m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Panel_listesinde_tek_grup()
    {
        // §4.6 havuz kipi: grup çapa Id'siyle tek satır, tutarı S'deki bacakların toplamı.
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        await kit.MemberAsync(g, 40m);
        var reason = NewReason();
        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);
        var anchor = await AnchorIdAsync(kit, reason);

        var list = (await (await kit.PanelGetAsync(a)).Content.ReadFromJsonAsync<Response>())!;

        list.Balance.Balance.Should().Be(20m, "havuz kipinde Σ_S");
        list.Transactions.Where(t => t.Kind == Kit.KindManual).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Id = anchor, Amount = -30m, Reason = reason });
        list.Transactions.Sum(t => t.Amount).Should().Be(list.Balance.Balance);
    }
}
```

Not: `Kit.EnablePoolAsync`, `Kit.PanelManualAdjustmentAsync` ve `Kit.GroupRowsAsync` Görev 1'in kit eklemeleri (`GroupRowsAsync` çapayı ilk, sonra bacakları `WpfCustomerId` "N" ordinal sırasıyla döner; testlerde en çok iki satır olduğu için sıra çapa → bacak). `MemberAsync(g, -20m)` eksi üyeyi "eski" bir elle ayarla kurar (D1 korunur). `Panel_listesinde_tek_grup` Görev 8'in havuz GET dalına dayanır.

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolManualAdjustmentTests"`
Expected: FAIL — `K5_ornegi_A10_B40_rota_A_eksi30`, `Havuz_tam_yeterse_eksi_uyeye_dokunulmaz`, `Silinmis_uye_canlidan_once`, `Rota_satirinin_bakiye_satiri_yoksa_0_capa`, `Rota_kopya_Id_ise_asil_kayit_capadir`, `Panel_listesinde_tek_grup` 409 `insufficient-balance` alır (bugünkü yol yalnız rota satırına bakar: 10 < 30, satır yoksa eksi reddedilir). `Havuz_kipinde_kurus_alti_eksi_400_invalid_money_scale_hicbir_sey_yazilmaz` 400 yerine 409 `insufficient-balance` alır (bugünkü yolda ölçek doğrulaması yok; rota satırı 10 − 10.005 < 0). `Havuz_yetmezse_409_insufficient_balance_hicbir_sey_yazilmaz`, `Arti_tutar_havuz_kipinde_de_bugunku_gibi` ve `Satir_kipinde_eksi_bugunku_gibi` bugünkü yolda da geçer (regresyon korkulukları).

- [ ] **Adım 3: Havuz dalı, `DrawManualAdjustmentAsync`, `BalanceBusy`**

`PanelCustomerBalanceController.cs`, `using`'ler (`:1-7`): ilk satırın önüne ekle (`UnreachableException`):

```csharp
using System.Diagnostics;
```

`ManualAdjustment`'ta, çözümden (`if (!valid) return NotFound();`, `:191`) sonraki şu blok:

```csharp
        // Negatif manuel ayar bakiyeyi sıfırın altına düşürmesin — kontrol
        // ApplyTransactionAsync'in retry döngüsünün İÇİNDE yapılır (F02):
        // buradaki ayrı bir ön okuma, eşzamanlı yazımlarda bayat değere bakardı.
        var applied = await ApplyTransactionAsync(licenseId, canonicalId, customerId,
```

şununla değişir (satır kipi kodu ve yorumu aynen kalır, önüne havuz dalı girer):

```csharp
        // Ortak bakiye S2 (spec 2026-10-11 §4.8, K5): havuz kipinde eksi ayar kişinin havuzundan çok bacaklı
        // çekimdir. Kip yalnız eksi tutarda okunur; artı tutar iki kipte de aşağıdaki yoldan (R47). Reason yukarıda
        // doğrulandı: TrimReason boş dönmez.
        if (req.Amount < 0 && await _person.IsPoolModeAsync(licenseId, ct))
        {
            // DrawAsync kuruş altı tutarda istisna atar (decimal(18,2) bacağı ve satırı ayrı yuvarlar, D1 bozulurdu):
            // apply'ın emsaliyle (LicensesCustomerBalanceApplyController) 400, 500 değil. Satır kipi bugünkü gibi.
            if (decimal.Round(req.Amount, 2) != req.Amount)
                return Problem(title: "invalid-money-scale", statusCode: 400,
                    detail: "Amount kuruş hassasiyetini aşamaz.");

            return await DrawManualAdjustmentAsync(
                licenseId, canonicalId, customerId, req.Amount, TrimReason(req.Reason)!, ct);
        }

        // Satır kipi (bayrak boş) ve artı tutar: bugünkü yol, birebir.
        // Negatif manuel ayar bakiyeyi sıfırın altına düşürmesin — kontrol
        // ApplyTransactionAsync'in retry döngüsünün İÇİNDE yapılır (F02):
        // buradaki ayrı bir ön okuma, eşzamanlı yazımlarda bayat değere bakardı.
        var applied = await ApplyTransactionAsync(licenseId, canonicalId, customerId,
```

`ManualAdjustment`'ın kapanış `}`'inden sonra, `// ── POST reverse (yanlış kayıt iptal) ───…` satırının önüne:

```csharp
    /// <summary>
    /// Havuz kipinde eksi elle ayar (spec 2026-10-11 §4.8, K5): <see cref="PersonBalance.DrawAsync"/> ile kişinin
    /// havuzundan çok bacaklı çekim, <c>P = S</c> (panelde <c>W</c> yok → <c>MemberIds = null</c>). Çapa rota
    /// satırının asıl kaydında (<c>manual-adjustment</c>, <c>Reason</c> yalnız çapada; çapanın bakiye satırı yoksa 0
    /// ile açılır); kalan <c>S</c>'nin öbür pozitif üyelerinden, M7 sırasıyla (silinmişler önce, sonra canlılar bakiye
    /// büyükten küçüğe). Hep-ya-hiç (<c>AllOrNothing</c>): <c>max(0, Σ_S) &lt; |tutar|</c> → hiçbir şey yazılmaz,
    /// 409 <c>insufficient-balance</c> (bugünkü kural, kısmi ayar yok; <c>Σ_S</c> eksi üyeleri içerir). Kişi kilidi
    /// <c>S</c>'nin bakiye satırı olan bütün üyelerine (§4.13); tek <c>SaveChanges</c>, ortak <c>CreatedAt</c>.
    ///
    /// <para>Tek satır yazıcısı <see cref="ApplyTransactionAsync"/> burada KULLANILMAZ (M12, iskelet farkı 11): onun
    /// "yeniden yükle + deltayı yeniden uygula" kalıbı çok bacaklı kayıtta deltayı çakışmayan satırlara iki kez
    /// yazardı ve hareketi okumadan önce izleyiciye ekler. Bu yöntem <c>DrawAsync</c>'ten önce izleyiciye hiçbir
    /// şey eklemez (servis denemeler arasında izleyiciyi temizler). Çakışmada servis planı baştan kurar; üç
    /// denemeden sonra 503 <c>balance-busy</c> (iskelet farkı 12; satır kipinin 3. çakışmadaki 500'ü değişmez).</para>
    ///
    /// <para>Öbür sonuçlar oluşmaz: <c>AllOrNothing</c> + <c>MemberIds = null</c> ile <c>NoBalance</c> ve
    /// <c>CustomerNotSynced</c> (kural 1–4 yalnız WPF'in <c>W</c>'sinde), <c>ReversesTransactionId</c> boş ve anahtar
    /// istemcinin değil → <c>AlreadyReversed</c> ve <c>DuplicateKey</c>, geri çağrı yok → <c>Stopped</c>, tutar &gt; 0 →
    /// <c>NothingToApply</c>.</para>
    /// </summary>
    /// <param name="amount">İstekteki eksi tutar; çekilen <c>−amount</c>.</param>
    /// <param name="reason">Kırpılmış, boş olmayan açıklama (çağıran doğruladı).</param>
    private async Task<IActionResult> DrawManualAdjustmentAsync(
        Guid licenseId, Guid canonicalId, Guid customerId, decimal amount, string reason, CancellationToken ct)
    {
        var result = await _person.DrawAsync(new PersonBalance.DrawRequest(
            LicenseId: licenseId,
            AnchorRowId: canonicalId,
            AnchorTransactionId: Guid.NewGuid(),
            AnchorIdIsIdempotencyKey: false,
            Kind: PersonBalance.KindManualAdjustment,
            Requested: -amount,
            Rule: PersonBalance.DrawAmountRule.AllOrNothing,
            MemberIds: null,
            OriginalAmount: null,
            SaleScope: null,
            Reason: reason,
            ReversesTransactionId: null,
            CreatedByCustomerId: customerId,
            ScopeLockResource: null), beforeAttempt: null, ct);

        return result.Status switch
        {
            PersonBalance.DrawStatus.Drawn => Ok(),
            PersonBalance.DrawStatus.Insufficient => Problem(title: "insufficient-balance", statusCode: 409),
            PersonBalance.DrawStatus.Busy => BalanceBusy(licenseId, "manual-adjustment"),
            _ => throw new UnreachableException($"manual-adjustment: beklenmeyen çekim sonucu {result.Status}"),
        };
    }

```

`ReverseDrawGroupAsync`'in `Busy` kolu (`:302-307`):

```csharp
            case PersonBalance.ReverseOutcome.Busy:
                // Uyarı servisin (neden ve çapayla); burada yalnız hangi ucun 503 döndüğü.
                _log.LogDebug(
                    "panel balance/reverse: 503 balance-busy (license={LicenseId})", licenseId);
                return Problem(title: "balance-busy", statusCode: 503,
                    detail: "Bakiye şu an başka bir işlemle güncelleniyor; yeniden deneyin.");
```

şununla değişir (işlenmiş günlük metni ve yanıt aynı):

```csharp
            case PersonBalance.ReverseOutcome.Busy:
                return BalanceBusy(licenseId, "reverse");
```

Helpers bölümünde, `// ── Helpers ─────…` satırının (`:313`) hemen altına, `ResolveAsync`'in belge yorumundan önce:

```csharp
    /// <summary>
    /// 503 <c>balance-busy</c> (spec §4.5, §4.13; iskelet farkı 12): servis yazımı üç denemede de kuramadı, hiçbir
    /// şey yazılmadı. Uyarı satırı servisin (neden ve çapayla); burada yalnız hangi ucun 503 döndüğü. Başlık ve
    /// metin WPF denetleyicisinin <c>BalanceBusy</c>'siyle aynı.
    /// </summary>
    /// <param name="endpoint"><c>reverse</c> ya da <c>manual-adjustment</c>.</param>
    private ObjectResult BalanceBusy(Guid licenseId, string endpoint)
    {
        _log.LogDebug(
            "panel balance/{Endpoint}: 503 balance-busy (license={LicenseId})", endpoint, licenseId);
        return Problem(title: "balance-busy", statusCode: 503,
            detail: "Bakiye şu an başka bir işlemle güncelleniyor; yeniden deneyin.");
    }

```

`switch` ifadesi dönüş türüne hedeflenir (`OkResult` ile `ObjectResult`'ın ortak türü yok; `IActionResult`'a dönüşür). `ApplyTransactionAsync`, `refund-full`, `refund-net` ve `Reverse`'in tek satır yolu değişmez.

- [ ] **Adım 4: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolManualAdjustmentTests"`
Expected: 10/10 PASS.

Regresyon (Panel/Shopper kümesi + aynı denetleyicinin Görev 8 sınıfı + servis):

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PanelCustomerBalanceControllerTests|FullyQualifiedName~PersonBalancePanelListTests|FullyQualifiedName~PersonBalancePanelReverseTests|FullyQualifiedName~PersonBalanceShopperListTests|FullyQualifiedName~ShopperBalanceControllerTests|FullyQualifiedName~PersonBalancePoolPanelListTests|FullyQualifiedName~PersonBalanceDrawTests"`
Expected: hepsi PASS, değişmeden — özellikle `ManualAdjustment_positive_adds_to_balance`, `ManualAdjustment_negative_exceeding_balance_returns_409`, `ManualAdjustment_empty_reason_returns_400`, `ManualAdjustment_kopya_Id_ile_asil_kayda_yazar_kopyaya_satir_acmaz` (satır kipi) ve `PersonBalancePanelReverseTests` (grup geri almasının `BalanceBusy`'ye taşınan kolu dışında aynı yol).

İlişkisel (Docker; satır kipinin eşzamanlı iki eksi ayarı ve panel grup geri almasının yarışları):

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceConcurrencyTests|FullyQualifiedName~PersonBalanceReversalRaceTests"`
Expected: hepsi PASS (`Esazamanli_iki_negatif_ayarlamadan_yalnizca_biri_gecer` bayrak boşken bugünkü yoldan). Docker kapalıysa "Genel kurallar"daki `DOCKER_HOST` adımı; o da olmazsa bu koşuyu atlandı diye not düş, Görev 16 ve 17'nin tam paketi yeniden koşar.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolManualAdjustmentTests.cs
git commit -F - <<'MSG'
feat(balance): havuz kipinde panel eksi elle ayarını kişinin havuzundan çek

Havuz kipinde eksi manual-adjustment artık DrawAsync ile çok bacaklı
çekimdir: çapa rota satırının asıl kaydında (Reason yalnız çapada), kalan
kişinin öbür pozitif üyelerinden, silinmişler önce; havuz yetmezse hiçbir
şey yazılmaz ve 409 insufficient-balance döner (ortak bakiye §4.8, K5, M7).
Tek satır yazıcısının deltayı yeniden uygulama döngüsü çok bacaklı kayıtta
yanlış olduğu için kullanılmaz (M12); üç çakışmadan sonra 503 balance-busy,
grup geri almasıyla ortak yardımcıdan. Havuz kipinde kuruş altı eksi tutar
apply'daki gibi 400 invalid-money-scale döner (500 değil). Artı tutar ve
satır kipi birebir aynı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 10: K5: panel reverse (havuz üyeliği, iade iptali, eski reversal → not-reversible) (spec §4.9, §3.2, §4.5; K1, K5, M8, M12, M13)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs` — `Reverse` (master `:212-268`), `ReverseDrawGroupAsync` belge yorumu + imza + üyelik satırı (master `:270-285`), yeni `ReverseInPoolAsync` (`ReverseDrawGroupAsync`'in kapanışından, master `:311`, sonra; `// ── Helpers` başlığından önce). Görev 8 (GET havuz dalı) ve Görev 9 (`manual-adjustment` havuz dalı, `BalanceBusy`) bu dosyada `Reverse`'ten önce ve `Helpers` bölümünde kod eklediği için satır numaraları kayar: blokları aşağıda alıntılanan metinle bul. `using System.Diagnostics;` (`UnreachableException`) Görev 9'da eklendi; dosyanın başında yoksa `using Microsoft.AspNetCore.Authorization;` satırının üstüne ekle.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolReverseTests.cs` (`IClassFixture<ApiFactory>`, InMemory)

Kural: bayrak (`License.PersonBalancePoolSince`) boşken iki yol S1'deki koduyla, aynı sırayla çalışır (tek ek okuma `IsPoolModeAsync`). Havuz kipinde:
- `DrawGroupId` dolu hedef: grubun bir satırı `S(rota)`'da olmalı (satır kipinde rota satırında, S1); geri kalan (`IsGroupReversible`, tür denetimi, `ReverseGroupAsync`, eşleme) aynen.
- `DrawGroupId` boş hedef → `ReverseInPoolAsync`, sırasıyla: hedef `S(rota)`'da değilse 404 → `Kind == reversal` ise 409 `not-reversible` (M8) → geri alınmışsa 409 `already-reversed` (hızlı yol, servisin kilitlenmeye karşı korunan denemesinin içinde; denetleyicide ayrı okuma yok) → `Amount ≤ 0` (düşüm, elle eksi) ise grup geri alması (`ReverseGroupAsync`, tek bacaklıda `DrawGroupId = NULL`, para hedefin güncel satırına) → `Amount > 0` (iade, artı elle ayar) ise çekim türü iptal (`DrawAsync`, `AllOrNothing`, çapa hedefin kendi satırında; `Insufficient` sonrası "geri alındı mı" yeniden sorulur).

**Not (spec ↔ kod) — bu görev:**
1. **Satır numaraları.** Spec §4.9 panel reverse'i `:197-246` diye anar; master'da `Reverse` `:212-268`, `ReverseDrawGroupAsync` `:270-311` (iskelet fark 1). §4.9'un "Hakem N01 (`:237-242`)" hızlı yolu bugün `:239-241`, N01 yakalaması `:259-264`.
2. **Eski eksi hedef havuz kipinde grup geri almasına gider (iskelet fark 10).** Bugünkü yol parayı rota satırına yazar (`ApplyTransactionAsync(licenseId, canonicalId, …)`, `:249`); havuz kipinde hedef `S(rota)`'nın başka üyesinde olabilir ve K1 "her bacak kendi satırına döner" der. `ReverseGroupAsync(licenseId, target.Id, …)` eski tek bacaklı satırı kendi çapası sayar (`IsAnchor`, `PersonBalance.cs:75`), `legs = [anchor]` kurar (`:166-171`), `DrawGroupId = NULL` yazar (`StageGroupReversalAsync` `:234`, atama `:248`; M13) ve parayı hedefin güncel `WpfCustomerId`'sine ekler. Satır biçimi bugünküyle aynı (`Kind = reversal`, `Amount = −hedef`, `ReversesTransactionId = hedef`, `Reason = "Reverse of {id:N}"`). Satır kipinde bugünkü yol birebir.
3. **İptal çapasının `Reason`'ı (iskelet fark 18d).** Spec §3.2 bacakta `NULL` der, çapayı yazmaz; bugünkü metin `"Reverse of {id:N}"` (`:254`) çapada, bacakta `NULL`.
4. **Eşzamanlı ikinci iade iptali (iskelet fark 18k, bu görevde genişletildi).** Spec §4.9 "Hakem N01" ve §4.13: kaybeden 409 `already-reversed` alır. Kaybedenin `insufficient-balance`'a düşebileceği iki pencere var: (a) jeton çakışması N01 ihlalinden önce gelir, ikinci denemenin taze planı havuzu yetmez görür; (b) kazanan, kaybedenin "geri alındı mı" sorusundan SONRA ama 1. denemenin bakiye okumalarından ÖNCE commit eder — kaybeden commit sonrası bakiyeleri okur, jeton çakışması olmaz, ikinci deneme gelmez (Örnek 4: kazanandan sonra A = 0, B = 10, havuz 10 < 80 → `Insufficient`). Çözüm iki parça: `beforeAttempt` "geri alındı mı"yı **her** denemede sorar (`true` → `Stopped` → `already-reversed`; (a)'yı kapatır), ve `Insufficient` sonrası "geri alındı mı" yeniden sorulur: kazanan kaybedenin okumalarından önce commit ettiyse yanıt `already-reversed` olur ((b)'yi kapatır — kazananın commit'i kaybedenin bir bakiye okumasından önce geldiyse sonraki sorgu iptal satırını kesin görür). Para güvenliği iki durumda da N01'e dayanır; bu yalnız yanıtın doğru olmasıdır. Yeniden soru yalnız havuz dalında; satır kipinin yolu değişmez. Yarışın kanıtı Görev 16'nın Testcontainers testlerine kalır (InMemory'de iki okuma arasına commit sokulamaz): `K5_iade_iptali_kayitta_rakip_iptal_commit_ederse_409_already_reversed`, `K5_iade_iptali_bakiye_okumasindan_once_rakip_commit_ederse_409_already_reversed` (pencere (b)) ve `K5_iade_iptali_paralel_sekiz_tek_iptal`.
5. **Üç denemeden sonra 503 `balance-busy` (iskelet fark 12).** `DrawAsync` ve `ReverseGroupAsync`'in `Busy`'si `BalanceBusy(licenseId, "reverse")` (Görev 9) → günlük metni bugünkünün aynısı (`"panel balance/reverse: 503 balance-busy (license=…)"`).
6. **Denetim sırası (mühendislik kararı; spec sıra vermiyor).** `DrawGroupId` boş hedefte: üyelik (404) → `reversal` türü (409 `not-reversible`) → geri alınmış mı (409 `already-reversed`) → para. `ReverseDrawGroupAsync`'in sırasıyla aynı (üyelik 404 önce; kişinin dışındaki bir hareketin türü sızmaz). Eski `reversal` satırı geri alınmış olsa da `not-reversible` alır (iptal edilemezlik, geri alınmışlıktan önce gelir). **"Geri alındı mı" denetleyicide ayrı bir okuma DEĞİLDİR:** spec §4.2-6 "S2 uygulama notu" N01 anahtarlarını kilitleyen yazarların kuyruğunda bekleyen hızlı yol okumasının 1205 kurbanı olabildiğini söyler (`PersonBalanceReversalDeadlockTests`); denetleyicideki korumasız okuma çok bacaklı yazarlarla yarışta 409 yerine 500 verirdi. Eksi dalda soru `ReverseGroupAsync`'in kendi hızlı yolundadır (`PersonBalance.cs:159`, `try` içinde, `IsDeadlock` kapsamında); artı dalda `beforeAttempt` içindedir (`DrawAsync`'in `try`'ı içinde). İki servis de yazımdan önce sorduğu için sıra (üyelik → `not-reversible` → `already-reversed` → para) değişmez. `Insufficient` sonrası yeniden soru servisin dışında kalır; onun 1205'i `BalanceBusy` (503, yeniden denensin) olur, 500 değil. **Üyelik okumasının 1205'i de 503'tür (kapandı; önceki taslakta bilinçli açıktı).** `MembersAsync` okuması (`ReverseInPoolAsync`'in başında ve `ReverseDrawGroupAsync`'in havuz dalında) servislerin korunan denemesinin dışındadır ve "yazılan tablolara dokunmaz" değildir: `WpfCustomerProjections`'ın yanında üyelerin bakiyelerini de `CustomerBalances`'tan okur (master `PersonBalance.cs:62-64`). `DrawAsync` ve `ReverseGroupAsync` tam bu satırları günceller ve veritabanında RCSI yoktur (repoda `READ_COMMITTED_SNAPSHOT` geçmez). Bu yüzden okuma yazarların X kilitlerinin arkasında bekleyebilir ve 1205 kurbanı olabilir. Korumasız kalsaydı yanıt 503 değil 500 olurdu. Bu yüzden iki çağrı da (Adım 4 ve 5) `try { … } catch (Exception ex) when (PersonBalance.IsDeadlock(ex)) { return BalanceBusy(licenseId, "reverse"); }` ile sarılır. `PersonBalance.IsDeadlock(Exception)` (`public static`, master `PersonBalance.cs:388`) spec §4.2-6 "S2 uygulama notu"nun iki okuma biçimini de tanır: sorgu komutu çalışırken yürütme stratejisinin `InvalidOperationException`'a sardığı `SqlException` ve satırlar okunurken gelen çıplak `SqlException` (1205). Okuma her yazımdan önce gelir, işlem açık değildir: hiçbir şey yazılmamıştır, istemci yeniden deneyebilir. Yardımcı Görev 9'un `BalanceBusy`'sidir (yeni ad yok; günlük satırı `"panel balance/reverse: 503 balance-busy (license=…)"`), Görev 13'ten sonra aynı çağrı sayacı `endpoint=panel/reverse` ile de artırır (Görev 13'ün değişikliği; bu görev ona dokunmaz). S1'den gelen grup okuması (`ReverseDrawGroupAsync`'in başı, `CustomerBalanceTransactions`) iki kipte de aynen kalır: bayrak boşken yol S1'in birebir aynısı olmalı (Kural) ve o okuma S1'de bilerek korumasızdı. InMemory'de komut kancası olmadığı için 1205 burada kurulamaz, `PersonBalancePoolReverseTests`'e test eklenmez. Kanıt Görev 16'nın Testcontainers testi `PersonBalancePanelDrawRaceTests.Havuz_kipinde_iptalin_uyelik_okumasi_kilitlenme_kurbani_olursa_503_balance_busy`'dir (iki yol; `ReadQueryHook` isteğin ilk `FROM [CustomerBalances]` okumasında, yani üyelik okumasında, bir kez `SqlException(1205)` fırlatır → 503, hiçbir şey yazılmaz, yeniden deneme 200).
7. **Tek satır yazıcısı kullanılmaz (iskelet fark 11, M12).** Havuz kipinin iptali `ApplyTransactionAsync`'e hiç girmez; hedef `AsNoTracking` okunduğu, `IsPoolModeAsync` ve `MembersAsync` izlemesiz olduğu için `DrawAsync`/`ReverseGroupAsync`'in "izleyicide değişiklik yok" önkoşulu tutar.

Sahip sorusu adayı: yok.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolReverseTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panel reverse, havuz kipi (ortak bakiye spec 2026-10-11 §4.9, K5, M8, M13; plan farkları 10, 18d). Hedef
/// S(rota)'nın herhangi bir üyesinde olabilir: çekim grubu S(rota) üyeliğiyle bulunur; eski eksi hedef grup geri
/// almasıyla kendi satırına döner; artı hareketin iptali çekim türü iptaldir (çapa hedefin satırında, kalan öbür
/// üyelerden, hep-ya-hiç — Örnek 4); eski <c>reversal</c> satırı 409 <c>not-reversible</c>. Bayrak boşken S1'in
/// koşulları aynen. InMemory: yarış iddiası yok (N01 hakemi Görev 16'da Testcontainers'ta).
/// </summary>
public sealed class PersonBalancePoolReverseTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolReverseTests(ApiFactory factory) => _factory = factory;

    private sealed record Example4(Guid A, Guid B, Guid R);

    /// <summary>
    /// Spec Örnek 4'ün kurulumu: kişi {A, B}; A'ya iade R (+80), sonra eski bir düşüm A'dan 50 → A = 30;
    /// B'nin kendi iadesi <paramref name="b"/>.
    /// </summary>
    private static async Task<Example4> Example4Async(Kit kit, decimal b)
    {
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var refund = await kit.FundAsync(a, 80m);
        await kit.SeedLegacyDeductionAsync(a, 50m, 50m);
        var bRow = await kit.MemberAsync(g, b);
        return new Example4(a, bRow, refund);
    }

    private static Task<int> ReversalRowCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindReversal));

    [Fact]
    public async Task Ornek4_baska_uyenin_iadesini_iptal()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 60m);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(ex.B, ex.R);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var anchor = (await kit.ReversalsOfAsync([ex.R])).Should().ContainSingle().Which;
        var rows = await kit.GroupRowsAsync(anchor.Id);
        rows.Should().HaveCount(2);
        rows[0].Id.Should().Be(anchor.Id);
        anchor.WpfCustomerId.Should().Be(ex.A, "çapa iptal edilen hareketin kendi satırında, rota satırında değil (§4.9)");
        anchor.Amount.Should().Be(-30m);
        anchor.Kind.Should().Be(Kit.KindReversal);
        anchor.DrawGroupId.Should().Be(anchor.Id);
        anchor.Reason.Should().Be($"Reverse of {ex.R:N}");
        var leg = rows[1];
        leg.WpfCustomerId.Should().Be(ex.B);
        leg.Amount.Should().Be(-50m);
        leg.Kind.Should().Be(Kit.KindReversal);
        leg.ReversesTransactionId.Should().BeNull("çekim türü iptalde hedefi yalnız çapa taşır (§3.2)");
        leg.DrawGroupId.Should().Be(anchor.Id);
        leg.Reason.Should().BeNull("açıklama çapada durur");
        leg.CreatedAt.Should().Be(anchor.CreatedAt, "grup tek SaveChanges'te, ortak CreatedAt ile");
        (await kit.BalanceAsync(ex.A)).Should().Be(0m);
        (await kit.BalanceAsync(ex.B)).Should().Be(10m);
        (await kit.LedgerHoldsAsync(ex.A)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(ex.B)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Ornek4_B20_havuz_yetmez_409_hicbir_sey_yazilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 20m);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(ex.B, ex.R);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("insufficient-balance");
        (await ReversalRowCountAsync(kit)).Should().Be(0, "hep-ya-hiç: havuz 50 < 80, kısmi iptal yok");
        (await kit.BalanceAsync(ex.A)).Should().Be(30m);
        (await kit.BalanceAsync(ex.B)).Should().Be(20m);
    }

    [Fact]
    public async Task Ayni_iade_ikinci_kez_iptal_409_already_reversed()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 60m);
        await kit.EnablePoolAsync();
        (await kit.PanelReverseAsync(ex.B, ex.R)).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await kit.PanelReverseAsync(ex.A, ex.R);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(again)).Should().Be("already-reversed");
        (await kit.ReversalsOfAsync([ex.R])).Should().ContainSingle();
        (await kit.BalanceAsync(ex.A)).Should().Be(0m);
        (await kit.BalanceAsync(ex.B)).Should().Be(10m);
    }

    [Fact]
    public async Task Ornek4_satir_kipinde_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 60m);

        var fromOther = await kit.PanelReverseAsync(ex.B, ex.R);
        var fromOwn = await kit.PanelReverseAsync(ex.A, ex.R);

        fromOther.StatusCode.Should().Be(HttpStatusCode.NotFound, "satır kipinde hedef rota satırının olmalı");
        fromOwn.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(fromOwn)).Should().Be("insufficient-balance", "satır kipinde bugünkü gibi: A = 30 < 80");
        (await ReversalRowCountAsync(kit)).Should().Be(0);
        (await kit.BalanceAsync(ex.A)).Should().Be(30m);
        (await kit.BalanceAsync(ex.B)).Should().Be(60m);
    }

    [Fact]
    public async Task Havuz_kipinde_baska_uyedeki_cekim_grubu_bulunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var z = await kit.MemberAsync(ex.Group);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(z, ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "grubun satırları S(rota)'da (§4.9)");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        (await kit.BalanceAsync(z)).Should().Be(0m);
    }

    [Fact]
    public async Task Satir_kipinde_rota_disindaki_grup_404_bacagin_satirindan_bulunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var z = await kit.MemberAsync(ex.Group);
        await kit.EnablePoolAsync();
        await kit.DisablePoolAsync();

        var fromStranger = await kit.PanelReverseAsync(z, ex.K);

        fromStranger.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "bayrak boş → S1 koşulu: grubun bir satırı rota satırında olmalı");
        (await kit.ReversalsOfAsync([ex.K])).Should().BeEmpty();
        (await kit.PanelReverseAsync(ex.I, ex.K)).StatusCode.Should().Be(HttpStatusCode.OK,
            "disable sonrası düzeltme yolu: bacağın satırından");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
    }

    [Fact]
    public async Task Eski_eksi_hedef_baska_uyede_kendi_satirina_geri_alinir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 50m);
        var target = await kit.SeedLegacyDeductionAsync(b, 20m, 20m);
        (await kit.PanelReverseAsync(a, target)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "satır kipinde koşul aynen");
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(a, target);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reversal = (await kit.ReversalsOfAsync([target])).Should().ContainSingle().Which;
        reversal.WpfCustomerId.Should().Be(b, "para hedefin kendi satırına döner (K1), rota satırına değil");
        reversal.Amount.Should().Be(20m);
        reversal.Kind.Should().Be(Kit.KindReversal);
        reversal.DrawGroupId.Should().BeNull("eski tek bacaklı çekimin geri alması bugünkü biçimde (M13)");
        reversal.Reason.Should().Be($"Reverse of {target:N}");
        (await kit.BalanceAsync(b)).Should().Be(50m);
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Eski_eksi_hedef_S_disinda_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup());
        var stranger = await kit.MemberAsync(Kit.NewGroup(), 50m);
        var target = await kit.SeedLegacyDeductionAsync(stranger, 20m, 20m);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(a, target);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "hedef başka kişinin satırında");
        (await kit.ReversalsOfAsync([target])).Should().BeEmpty();
        (await kit.BalanceAsync(stranger)).Should().Be(30m);
    }

    [Fact]
    public async Task Eski_reversal_havuz_kipinde_409_not_reversible()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var refund = await kit.FundAsync(a, 30m);
        (await kit.PanelReverseAsync(a, refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        var reversal = (await kit.ReversalsOfAsync([refund])).Should().ContainSingle().Which;
        reversal.DrawGroupId.Should().BeNull("satır kipinde yazılan eski biçim");
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(a, reversal.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("not-reversible", "eski reversal havuz kipinde iptal edilemez (M8)");
        (await kit.ReversalsOfAsync([reversal.Id])).Should().BeEmpty();
        (await kit.BalanceAsync(a)).Should().Be(50m);
    }

    [Fact]
    public async Task Havuz_kipinde_K5_cekim_grubunun_geri_almasi_grup_satirlariyla()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();
        var reason = $"r-{Guid.NewGuid():N}";
        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);
        var anchorId = await kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindManual && t.Reason == reason)
            .Select(t => t.Id)
            .SingleAsync());
        var drawn = await kit.GroupRowsAsync(anchorId);
        drawn.Should().HaveCount(2);

        var resp = await kit.PanelReverseAsync(a, anchorId);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reversals = await kit.ReversalsOfAsync(drawn.Select(t => t.Id));
        reversals.Should().HaveCount(2, "her bacak için bir geri alma satırı (§4.5)");
        var reversalAnchor = reversals.Single(r => r.ReversesTransactionId == anchorId);
        reversals.Should().OnlyContain(r => r.DrawGroupId == reversalAnchor.Id && r.Kind == Kit.KindReversal);
        reversals.Select(r => r.CreatedAt).Distinct().Should().ContainSingle();
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Havuz_kipinde_cekim_turu_iptal_grubu_409_not_reversible()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 60m);
        await kit.EnablePoolAsync();
        (await kit.PanelReverseAsync(ex.B, ex.R)).StatusCode.Should().Be(HttpStatusCode.OK);
        var v = (await kit.ReversalsOfAsync([ex.R])).Should().ContainSingle().Which;
        var leg = (await kit.GroupRowsAsync(v.Id))[1];

        var onAnchor = await kit.PanelReverseAsync(ex.A, v.Id);
        var onLeg = await kit.PanelReverseAsync(ex.A, leg.Id);

        onAnchor.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(onAnchor)).Should().Be("not-reversible", "çok bacaklı 'iptalin iptali' kapalı (M8)");
        onLeg.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(onLeg)).Should().Be("not-reversible");
        (await kit.ReversalsOfAsync([v.Id, leg.Id])).Should().BeEmpty();
        (await kit.BalanceAsync(ex.A)).Should().Be(0m);
        (await kit.BalanceAsync(ex.B)).Should().Be(10m);
    }

    [Fact]
    public async Task Tek_bacakli_havuz_cekiminin_geri_almasi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, 20m);
        var ids = await kit.SeedDrawAsync(Kit.KindDeduction, [(a, 30m)], originalAmount: 30m, saleScope: Kit.Session42);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(b, ids[0]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "çapa S(rota)'da");
        var reversal = (await kit.ReversalsOfAsync([ids[0]])).Should().ContainSingle().Which;
        reversal.WpfCustomerId.Should().Be(a);
        reversal.Amount.Should().Be(30m);
        reversal.DrawGroupId.Should().Be(reversal.Id,
            "havuz kipinde yazılmış tek bacaklı çekimin geri alması grup biçiminde; NULL yalnız eski satırda (M13)");
        (await kit.BalanceAsync(a)).Should().Be(50m);
        (await kit.BalanceAsync(b)).Should().Be(20m);
    }
}
```

Not: `MemberAsync(g)` 0 bakiyeli satırı açar (`balanceRow` varsayılanı `true`), `FundAsync` o satıra ekler; Örnek 4'te A'nın bakiyesi iade (+80) ve eski düşümden (−50) gelir, D1 tutar. `GroupRowsAsync` çapayı ilk döndürür (Görev 1).

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolReverseTests"`
Expected: FAIL — derleme temiz (kit üyeleri Görev 1'den, `manual-adjustment` havuz dalı Görev 9'dan); sekiz test kırmızı:
`Ornek4_baska_uyenin_iadesini_iptal`, `Ornek4_B20_havuz_yetmez_409_hicbir_sey_yazilmaz`, `Ayni_iade_ikinci_kez_iptal_409_already_reversed`, `Havuz_kipinde_cekim_turu_iptal_grubu_409_not_reversible` (hedef rota satırında değil → 404), `Havuz_kipinde_baska_uyedeki_cekim_grubu_bulunur`, `Tek_bacakli_havuz_cekiminin_geri_almasi` (grubun satırı rota satırında değil → 404), `Eski_eksi_hedef_baska_uyede_kendi_satirina_geri_alinir` (havuz kipinde de 404), `Eski_reversal_havuz_kipinde_409_not_reversible` (satır yolu iptali geri alır → 200). Yeşil kalanlar korumadır: `Ornek4_satir_kipinde_bugunku_gibi`, `Satir_kipinde_rota_disindaki_grup_404_bacagin_satirindan_bulunur`, `Eski_eksi_hedef_S_disinda_404`, `Havuz_kipinde_K5_cekim_grubunun_geri_almasi_grup_satirlariyla` (rota satırında çapa var; S1 yolu zaten grup olarak geri alır — R69 biçim testi).

- [ ] **Adım 3: `Reverse` — kip okuması ve havuz dalı**

`PanelCustomerBalanceController.cs`, `Reverse` (master `:222-233`). Bugünkü:

```csharp
        var target = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId, ct);
        if (target is null) return NotFound();

        // Ortak bakiye S1 tabanı (spec 2026-10-11 §4.9): çekim grubuna ait hedef iki kipte de grup olarak ele
        // alınır — tek satırı geri almak yarım bir geri alma bırakırdı.
        if (target.DrawGroupId is { } anchorId)
            return await ReverseDrawGroupAsync(licenseId, canonicalId, customerId, anchorId, target, ct);

        // Eski, tek satırlık hedef: bugünkü yol, birebir (satır kipi).
        if (target.WpfCustomerId != canonicalId) return NotFound();
```

şu olur (altındaki satır kipi gövdesi — hızlı yol, `ApplyTransactionAsync`, N01 yakalaması, `insufficient-balance` — master `:235-267` AYNEN kalır):

```csharp
        var target = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId, ct);
        if (target is null) return NotFound();

        // Ortak bakiye S2 (spec 2026-10-11 §4.9, K5): kip lisans başına, her istekte okunur. Bayrak boşken aşağıdaki
        // iki yol S1'deki koduyla, aynı sırayla çalışır.
        var pool = await _person.IsPoolModeAsync(licenseId, ct);

        // Ortak bakiye S1 tabanı (§4.9): çekim grubuna ait hedef iki kipte de grup olarak ele alınır — tek satırı
        // geri almak yarım bir geri alma bırakırdı. Üyelik kipe göre (rota satırı ya da S(rota)).
        if (target.DrawGroupId is { } anchorId)
            return await ReverseDrawGroupAsync(licenseId, canonicalId, customerId, anchorId, target, pool, ct);

        // Havuz kipinde eski, tek satırlık hedef: S(rota) üyeliği, grup geri alması ya da çekim türü iptal (K5).
        if (pool)
            return await ReverseInPoolAsync(licenseId, canonicalId, customerId, target, ct);

        // Eski, tek satırlık hedef: bugünkü yol, birebir (satır kipi).
        if (target.WpfCustomerId != canonicalId) return NotFound();
```

- [ ] **Adım 4: `ReverseDrawGroupAsync` — havuz kipinde S(rota) üyeliği**

Belge yorumu ve imza (master `:270-279`). Bugünkü:

```csharp
    /// <summary>
    /// Çekim grubuna ait hedef (spec §4.9, M8). Üyelik: grubun herhangi bir satırı rota satırında olmalı (S1 kip
    /// bayrağını okumaz → satır kipi koşulu; havuz kipinin S(rota) genişlemesi S2'de). Panel listesi grubu çapa
    /// Id'siyle gösterdiği için (§4.6) çapa başka satırda olsa da rota satırındaki bacaktan açılan iptal bulunur;
    /// <c>disable</c> sonrası düzeltme yolu da budur. Düşüm ve elle eksi grubu grup olarak geri alınır
    /// (<see cref="PersonBalance.ReverseGroupAsync"/>, §4.5); <c>reversal</c> grupları 409 <c>not-reversible</c>.
    /// </summary>
    private async Task<IActionResult> ReverseDrawGroupAsync(
        Guid licenseId, Guid canonicalId, Guid customerId, Guid anchorId, CustomerBalanceTransaction target,
        CancellationToken ct)
```

şu olur:

```csharp
    /// <summary>
    /// Çekim grubuna ait hedef (spec §4.9, M8). Üyelik: grubun herhangi bir satırı satır kipinde rota satırında,
    /// havuz kipinde <c>S(rota)</c>'nın bir üyesinde olmalı — panel kişinin birleşik listesini gösterir. Panel listesi
    /// grubu çapa Id'siyle gösterdiği için (§4.6) çapa başka satırda olsa da rota satırındaki bacaktan açılan iptal
    /// bulunur; <c>disable</c> sonrası (bayrak boş → satır kipi koşulu) düzeltme yolu da budur. Düşüm ve elle eksi
    /// grubu grup olarak geri alınır (<see cref="PersonBalance.ReverseGroupAsync"/>, §4.5); <c>reversal</c> grupları
    /// iki kipte 409 <c>not-reversible</c>. Havuz kipinin üyelik okuması (<see cref="PersonBalance.MembersAsync"/>)
    /// kilitlenme kurbanı (1205) olursa 503 <c>balance-busy</c>: okuma yazımdan önce, hiçbir şey yazılmamıştır.
    /// </summary>
    private async Task<IActionResult> ReverseDrawGroupAsync(
        Guid licenseId, Guid canonicalId, Guid customerId, Guid anchorId, CustomerBalanceTransaction target,
        bool pool, CancellationToken ct)
```

Üyelik satırı (master `:285`). Bugünkü:

```csharp
        if (!group.Any(t => t.WpfCustomerId == canonicalId)) return NotFound();
```

şu olur:

```csharp
        if (pool)
        {
            // Havuz kipi (S2): grubun bir satırı S(rota)'da olmalı (silinmiş üyeler dahil, MembersAsync).
            // Üyelik okuması ReverseGroupAsync'in korunan denemesinin dışında ve CustomerBalances'ı da okur: yazarların
            // X kilidinin arkasında kilitlenme kurbanı (1205) olabilir. Okuma her yazımdan önce, hiçbir şey yazılmadı →
            // 503 balance-busy, yeniden denensin (500 değil; Not 6).
            HashSet<Guid> members;
            try
            {
                members = (await _person.MembersAsync(licenseId, canonicalId, ct)).Members
                    .Select(m => m.WpfCustomerId)
                    .ToHashSet();
            }
            catch (Exception ex) when (PersonBalance.IsDeadlock(ex))
            {
                return BalanceBusy(licenseId, "reverse");
            }
            if (!group.Any(t => members.Contains(t.WpfCustomerId))) return NotFound();
        }
        else if (!group.Any(t => t.WpfCustomerId == canonicalId)) return NotFound();
```

Metodun geri kalanı (çapa, `IsGroupReversible` + tür denetimi, `ReverseGroupAsync` eşlemesi; Görev 9'dan beri `Busy` → `return BalanceBusy(licenseId, "reverse");`) aynen kalır.

- [ ] **Adım 5: `ReverseInPoolAsync`**

`ReverseDrawGroupAsync`'in kapanış parantezinden sonra, `// ── Helpers ───…` satırından önce:

```csharp
    /// <summary>
    /// Havuz kipinde <c>DrawGroupId</c>'si boş (eski, tek satırlık) hedef — spec 2026-10-11 §4.9, K5. Üyelik: hedef
    /// <c>S(rota)</c>'nın herhangi bir üyesinde olmalı (panel kişinin birleşik listesini gösterir, §4.6). Sıra:
    /// <list type="number">
    /// <item>Eski <c>reversal</c> satırı → 409 <c>not-reversible</c> (M8): "iptalin iptali" havuz kipinde kapalı; satır
    /// kipinde bugünkü gibi geri alınır.</item>
    /// <item>Geri alınmış hedef → 409 <c>already-reversed</c> (hızlı yol; asıl hakem N01 indeksi). Soru denetleyicide
    /// ayrı bir okuma değil, servisin kilitlenmeye (1205) karşı korunan denemesinin içinde: eksi dalda
    /// <see cref="PersonBalance.ReverseGroupAsync"/>'in kendi hızlı yolu, artı dalda her denemenin geri çağrısı.</item>
    /// <item>Eksi hedef (düşüm, elle eksi) → grup geri alması (<see cref="PersonBalance.ReverseGroupAsync"/>, §4.5):
    /// eski tek bacaklıda <c>DrawGroupId = NULL</c> (M13), para hedefin GÜNCEL satırına döner (K1) — rota satırına
    /// değil (plan farkı 10).</item>
    /// <item>Artı hedef (iade, artı elle ayar) → çekim türü iptal (<see cref="PersonBalance.DrawAsync"/>, §3.2): çapa
    /// hedefin KENDİ satırında, <c>Kind = reversal</c>, <c>ReversesTransactionId</c> = hedef ve açıklama
    /// <c>"Reverse of {id:N}"</c> yalnız çapada (fark 18d); kalan S'nin öbür pozitif üyelerinden; hep-ya-hiç →
    /// yetmezse 409 <c>insufficient-balance</c>, hiçbir şey yazılmaz.</item>
    /// </list>
    /// Hakem N01: eşzamanlı ikinci iptal bütün bacaklarıyla geri alınır → 409 <c>already-reversed</c> (§4.9). Kaybeden
    /// "havuz yetmez"e de düşebilir (fark 18k): jeton çakışması N01'den önce gelirse taze planda, ya da kazanan bu
    /// isteğin bakiye okumalarından önce commit ederse 1. denemede. Bu yüzden geri çağrı "geri alındı mı"yı HER
    /// denemede sorar, ve <c>Insufficient</c> sonrası da yeniden sorulur: kazanan kaybedenin okumalarından önce commit
    /// ettiyse yanıt <c>already-reversed</c> olur. Tek satır yazıcısı
    /// (<see cref="ApplyTransactionAsync"/>) kullanılmaz (M12, fark 11): çağırmadan önce izleyiciye hiçbir şey
    /// eklenmez (hedef <c>AsNoTracking</c> okundu). Üç denemede kurulamazsa 503 <c>balance-busy</c> (fark 12). Baştaki
    /// üyelik okuması (<see cref="PersonBalance.MembersAsync"/>, bakiyeler dahil) kilitlenme kurbanı (1205) olursa da
    /// 503 <c>balance-busy</c>: okuma her yazımdan önce gelir, hiçbir şey yazılmamıştır, istemci yeniden dener.
    /// </summary>
    private async Task<IActionResult> ReverseInPoolAsync(
        Guid licenseId, Guid canonicalId, Guid customerId, CustomerBalanceTransaction target, CancellationToken ct)
    {
        // Üyelik okuması servislerin korunan denemesinin dışında ve CustomerBalances'ı da okur: yazarların X kilidinin
        // arkasında kilitlenme kurbanı (1205) olabilir. Okuma her yazımdan önce, hiçbir şey yazılmadı → 503
        // balance-busy, yeniden denensin (500 değil; Not 6).
        HashSet<Guid> members;
        try
        {
            members = (await _person.MembersAsync(licenseId, canonicalId, ct)).Members
                .Select(m => m.WpfCustomerId)
                .ToHashSet();
        }
        catch (Exception ex) when (PersonBalance.IsDeadlock(ex))
        {
            return BalanceBusy(licenseId, "reverse");
        }
        if (!members.Contains(target.WpfCustomerId)) return NotFound();

        if (target.Kind == PersonBalance.KindReversal)
            return Problem(title: "not-reversible", statusCode: 409,
                detail: "Bu hareket bir iptal; havuz kipinde iptal edilemez.");

        var targetId = target.Id;
        // "Geri alındı mı" — hızlı yol; yarışta iki istek de geçebilir, asıl hakem N01 indeksi (servisler
        // IsDuplicateReversal'ı yakalar, işlem bütünüyle geri alınır). Burada ayrı okunmaz: N01 anahtarlarını kilitleyen
        // yazarların kuyruğundaki okuma 1205 kurbanı olabilir (§4.2-6 S2 notu); servislerin try'ı içinde sorulur.
        Task<bool> IsReversedAsync(CancellationToken token) =>
            _db.CustomerBalanceTransactions.AnyAsync(t => t.ReversesTransactionId == targetId, token);

        if (target.Amount <= 0m)
        {
            // Düşüm ya da elle eksi: eski tek bacaklı satır kendi çapasıdır (PersonBalance.IsAnchor). "Geri alındı mı"
            // ReverseGroupAsync'in kendi korunan hızlı yolunda → AlreadyReversed.
            switch (await _person.ReverseGroupAsync(licenseId, targetId, customerId, ct))
            {
                case PersonBalance.ReverseOutcome.Reversed:
                    return Ok();
                case PersonBalance.ReverseOutcome.AlreadyReversed:
                    return Problem(title: "already-reversed", statusCode: 409);
                case PersonBalance.ReverseOutcome.Busy:
                    return BalanceBusy(licenseId, "reverse");
                default:
                    return NotFound();
            }
        }

        var result = await _person.DrawAsync(
            new PersonBalance.DrawRequest(
                LicenseId: licenseId,
                AnchorRowId: target.WpfCustomerId,
                AnchorTransactionId: Guid.NewGuid(),
                AnchorIdIsIdempotencyKey: false,
                Kind: PersonBalance.KindReversal,
                Requested: target.Amount,
                Rule: PersonBalance.DrawAmountRule.AllOrNothing,
                MemberIds: null,
                OriginalAmount: null,
                SaleScope: null,
                Reason: $"Reverse of {targetId:N}",
                ReversesTransactionId: targetId,
                CreatedByCustomerId: customerId,
                ScopeLockResource: null),
            // HER denemenin başında (kilitten sonra, okumalardan önce; DrawAsync'in try'ı içinde, 1205 → yeniden deneme):
            // ilk denemede hızlı yol, sonrakilerde jeton çakışmasından sonra kazananı görür (fark 18k).
            beforeAttempt: (_, token) => IsReversedAsync(token),
            ct);

        // Havuz yetmedi: kazanan bu isteğin bakiye okumalarından önce commit ettiyse jeton çakışması olmaz, ikinci
        // deneme gelmez (Örnek 4: kazanandan sonra havuz 10 < 80). Kazananın commit'i okumalardan önce geldiği için
        // iptali bu sorguda kesin görünür → already-reversed (§4.9 "Hakem N01"). Yalnız havuz dalı; satır kipi aynen.
        if (result.Status == PersonBalance.DrawStatus.Insufficient)
        {
            bool reversed;
            try
            {
                reversed = await IsReversedAsync(ct);
            }
            catch (Exception ex) when (PersonBalance.IsDeadlock(ex))
            {
                // Okuma kilitlenme kurbanı (hiçbir şey yazılmadı): 500 değil, yeniden denensin.
                return BalanceBusy(licenseId, "reverse");
            }
            if (reversed) return Problem(title: "already-reversed", statusCode: 409);
        }

        return result.Status switch
        {
            PersonBalance.DrawStatus.Drawn => Ok(),
            // Stopped: hedef deneme başında geri alınmış bulundu (hızlı yol ya da çakışmadan sonra kazanan);
            // AlreadyReversed: N01 ihlali.
            PersonBalance.DrawStatus.Stopped or PersonBalance.DrawStatus.AlreadyReversed =>
                Problem(title: "already-reversed", statusCode: 409),
            PersonBalance.DrawStatus.Insufficient => Problem(title: "insufficient-balance", statusCode: 409),
            PersonBalance.DrawStatus.Busy => BalanceBusy(licenseId, "reverse"),
            // NoBalance/NothingToApply yalnız UpToPool'da, CustomerNotSynced/DuplicateKey yalnız WPF girişinde doğar.
            _ => throw new UnreachableException($"panel reverse: beklenmeyen çekim sonucu {result.Status}"),
        };
    }
```

Not: `MemberIds: null` → `DrawAsync` `PersonDrawSetAsync(licenseId, target.WpfCustomerId)` ile `P = S(hedefin satırı)` kurar; hedefin satırı `S(rota)`'nın üyesi olduğu (yukarıda doğrulandı) ve `MembersAsync` asıl kayıtları döndürdüğü için bu küme `S(rota)`'dır. Çapanın bakiyesi önce çekilir (`PlanDraw`: ilk eleman çapa), kalan silinmiş, sonra canlı üyelerden (M7).

- [ ] **Adım 6: Koştur — PASS**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolReverseTests"`
Expected: PASS (12 test), 0 uyarı.

- [ ] **Adım 7: Regresyon — satır kipi ve panel/Shopper (InMemory), sonra ilişkisel (Docker)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PanelCustomerBalanceControllerTests|FullyQualifiedName~PersonBalancePanelListTests|FullyQualifiedName~PersonBalancePanelReverseTests|FullyQualifiedName~PersonBalanceShopperListTests|FullyQualifiedName~ShopperBalanceControllerTests|FullyQualifiedName~PersonBalancePoolManualAdjustmentTests|FullyQualifiedName~PersonBalancePoolPanelListTests"`
Expected: hepsi PASS, testler DEĞİŞTİRİLMEDEN — özellikle S1'in `Eski_reversal_satiri_satir_kipinde_bugunku_gibi_geri_alinabilir`, `Rota_satirinda_grubun_satiri_yoksa_404`, `K5_iptal_grubu_capadan_da_bacaktan_da_not_reversible` ve bugünkü `Reverse_undoes_previous_transaction`, `Reverse_already_reversed_returns_409`, `Reverse_kopya_Id_ile_asil_kaydin_hareketini_geri_alir` (bayrak boş → S1 yolu).

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceConcurrencyTests|FullyQualifiedName~PersonBalanceReversalRaceTests"`
Expected: PASS (Docker gerekir; `Ayni_hareket_esazamanli_yalnizca_bir_kez_reverse_edilebilir`, `WPF_ve_panel_paralel_grup_geri_almasindan_yalniz_biri_kazanir`, `Kayit_aninda_commit_eden_rakip_geri_almada_kaybeden_N01_ile_409_already_reversed_alir(panelRival: true)` satır kipinde değişmeden). Docker kapalı düşerse "Genel kurallar"daki `DOCKER_HOST` adımı; yine olmazsa bu adımı atlamadan önce durumu not düş — havuz kipinin yarış kanıtı (Testcontainers 9, 14) Görev 16'da.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePoolReverseTests.cs
git commit -F - <<'MSG'
feat(balance): panel iptalini havuz kipinde kişinin bütün üyelerine aç (K5)

Havuz kipinde panel kişinin birleşik listesini gösterdiği için iptal
edilen hareket S(rota)'nın herhangi bir üyesinde olabilir. Çekim grubu
S(rota) üyeliğiyle bulunur; eski eksi hedef (düşüm, elle eksi) grup geri
almasıyla parayı kendi satırına döndürür (DrawGroupId NULL, M13); artı
hareketin iptali DrawAsync ile çekim türü iptaldir: çapa hedefin
satırında, kalan öbür üyelerden, hep-ya-hiç (Örnek 4). Eski reversal
satırı havuz kipinde 409 not-reversible (M8). Havuz kipinin üyelik
okuması kilitlenme kurbanı (1205) olursa 503 balance-busy (hiçbir şey
yazılmadı; 500 değil). Bayrak boşken iki yol S1'deki koduyla aynı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 11: `PersonBalanceEnableGate` (koşul 1–4) (spec §7.2 "`enable` ön koşulları", §8.1)

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceEnableGate.cs`
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceEnableGateTests.cs`

Havuz kipinin açılış kapısı, `person-balance enable`'dan (Görev 12) ayrı ve saf bir değerlendirici. Kuru koşu her koşulu sayı olarak basar, `--apply` her seferinde yeniden değerlendirir ve biri tutmazsa reddeder. Değerlendirici salt okunurdur: izlemesiz sorgular kullanır, hiçbir şey yazmaz, göç gerektirmez, hiçbir uca ve satır kipine dokunmaz. Statik sınıftır; DI kaydı gerekmez, CLI `LicenseDbContext`'i verir. Dört koşul şunlardır:

1. **İstemci sürümü (§7.2-1):** son `PersonBalance:ActivePcWindowDays` günde görülmüş ve devre dışı bırakılmamış her etkinleştirmenin `AppVersion`'ı `PersonBalance:MinClientVersion`'ı `ClientVersion.IsAtLeast` ile karşılamalı. `AppVersion` yoksa ya da ayrıştırılamıyorsa o PC eski sayılır. Alt sınır boş ya da bozuksa koşul tutmaz (R06). Pencere ayardan okunur (R05). Pencere ≤ 0 ise de koşul tutmaz (kapalı-hata): web host'ta `ValidateOnStart` (Görev 1), CLI'da `enable`'ın denetimi (Görev 12) bunu zaten engeller, ama public değerlendirici bu güvenceyi çağırana bırakmaz.
2. **Kural sayıları (§7.2-2):** `verify`'ın D4, D5, D6, yetim bacak ve kırpma farkı sayıları 0 olmalı.
3. **Taban (§7.2-3):** tabanın varlığı, işaret dahil bütün taban satırlarının incelenmiş olması ve tabana göre yeni sapma olmaması.
4. **Wh ⊇ Ws (§7.2-4):** `SalesSync:MinClientVersion` dolu ve `PersonBalance:MinClientVersion` ≥ ona; iki değer yan yana döner.

Bütün testler InMemory'de koşar: koşullar salt okumadır ve bu görev bir yarış iddiası taşımaz. Testcontainers gerekmez.

**Not (spec ↔ kod).** Bu görev yeni bir fark açmaz; iskeletteki farkları uygular:
- **Fark 14:** 1. koşulun sorgusu `SalesModeGate.ClientVersionsAsync`'in (`Services/Sync/SalesModeGate.cs:309-324`) kalıbının kopyasıdır. O metot `private`'tır ve `SalesSyncOptions`'a bağlıdır. Satış senkronu koduna dokunulmaz; yalnız public `SalesModeGate.OldActivation` (`:53`) ve `SalesModeGate.ClientVersionCheck` (`:58-61`) kayıtları yeniden kullanılır. Makine adı ve parmak izi okunmaz.
- **Fark 15:** 2. koşulda kırpma farkı, spec §7.3-5'in SQL'i değil, S1 verifier'ının `TrimMismatches` + `ControlCharGroups`'udur (`PersonBalanceVerifier.cs:351-357`). 3. koşulun parçaları şunlardır:
  - Tabanın varlığı verifier'ın `HasBaseline`'ıdır (`:307-308`: işaret satırı ya da ilk yakalamadan sonra verilmiş lisans). Tabanı olmayan lisans reddedilir; önce `verify --capture-baseline` gerekir.
  - "İncelenmemiş", lisansın `ReviewedAt`'i boş bütün **ham** taban satırlarıdır; işaret ve etkin tabana katlanan kopya satırları da buna dahildir. `--mark-reviewed` bunların hepsini işaretler.
  - "Yeni sapma", günlük uyarının (`:381-389`) taban kalemleridir: `NegativeRows.Outside`, `Details.WorsenedBaselineRows`, `LedgerMismatches.Outside`, `Details.ChangedMismatchRows`, `CopyRows.Outside`.
- **Sahip kararı 2 (29.10, (a); iskelet "Sahip kararları"):** farkı değişen D1 taban satırı `enable`'ı engeller. Farkı düzeltilip 0'a inen satır da buna girer; günlük uyarıyla aynı kural geçerlidir. Kodda özel istisna yoktur; bir test (`Kosul3_farki_degisen_taban_D1_satiri_tutmaz`) bu davranışı sabitler. S2'de değişiklik yok: 29.10'da prod tabanı dört lisansta da yalnız işaret satırıydı (eksi/D1/kopya 0), düzeltilecek taban satırı yok, soru konusuz.
- `PersonBalanceVerifier.LoadAsync(licenseId, …)` ilk yakalama anını bulmak için **bütün** lisansların taban satırlarını yükler (`:105-107`). Bu yüzden incelenmemiş sayısı `LicenseId` ile süzülür.
- `LoadAsync`'in kendi etkin PC listesi (`ActivePcs`, sabit 14 gün) burada kullanılmaz. `verify`'ın 9. maddesi değişmez (fark 6).
- Lisans yoksa `Analyze(...).Licenses.Single(...)` `InvalidOperationException` atar. Lisansı önce CLI doğrular (Görev 12).

- [ ] **Adım 1: Kapı testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceEnableGateTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Gate = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceEnableGate;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <see cref="PersonBalanceEnableGate"/>: spec 2026-10-11 §7.2 "<c>enable</c> ön koşulları" 1–4. 1: son
/// <c>ActivePcWindowDays</c> günde görülmüş, devre dışı bırakılmamış her PC alt sınırda; alt sınır boş ya da bozuksa
/// tutmaz. 2: D4/D5/D6/yetim bacak/kırpma farkı 0. 3: taban var, hepsi incelenmiş, tabana göre yeni sapma yok. 4: Wh ⊇ Ws.
/// Salt okuma, InMemory yeter (yarış iddiası yok).
/// </summary>
public sealed class PersonBalanceEnableGateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceEnableGateTests(ApiFactory factory) => _factory = factory;

    private static PersonBalanceOptions Options(
        string? pb = "0.9.11", int window = PersonBalanceOptions.DefaultActivePcWindowDays) =>
        new() { MinClientVersion = pb, ActivePcWindowDays = window };

    private static SalesSyncOptions Sales(string? ss = "0.9.11") => new() { MinClientVersion = ss };

    /// <summary>
    /// Bir yıl önce verilmiş lisans. Veritabanı sınıfın testleri arasında paylaşılır ve ilk yakalama anı bütün
    /// lisansların işaretlerinin en küçük <c>CapturedAt</c>'idir. O andan SONRA verilen lisans işaretsiz de "tabanı var"
    /// sayılır (<c>PersonBalanceVerifier.cs:308</c>). Geri tarihleme bu yüzden gerekli: aksi hâlde tabansız lisans
    /// testi, başka bir testin işaretine göre geçerdi.
    /// </summary>
    private async Task<Kit> OldLicenseAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.Sales.WithDbAsync(async db =>
            (await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId)).IssuedAt = DateTimeOffset.UtcNow.AddYears(-1));
        return kit;
    }

    /// <summary>Taban alınmış ve işareti incelenmiş, verisi olmayan eski lisans.</summary>
    private async Task<Kit> ReadyLicenseAsync()
    {
        var kit = await OldLicenseAsync();
        await MarkerAsync(kit);
        return kit;
    }

    private static Task MarkerAsync(Kit kit, bool reviewed = true) =>
        BaselineRowAsync(kit, PersonBalanceVerifier.LicenseMarker, 0m, 0m, reviewed);

    private static Task BaselineRowAsync(Kit kit, Guid row, decimal balance, decimal ledgerSum, bool reviewed) =>
        kit.Sales.WithDbAsync(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = row, Balance = balance, LedgerSum = ledgerSum,
                CapturedAt = now.AddDays(-1), ReviewedAt = reviewed ? now : null,
            });
            return Task.CompletedTask;
        });

    /// <summary>Etkinleştirme: <paramref name="lastSeenDaysAgo"/> gün önce görülmüş. Parmak izi üretilir.</summary>
    private static async Task<Guid> ActivationAsync(Kit kit, string? version, int lastSeenDaysAgo, bool deactivated = false)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await kit.Sales.WithDbAsync(db =>
        {
            db.Activations.Add(new Activation
            {
                Id = id, LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
                ActivatedAt = now.AddDays(-60), LastSeenAt = now.AddDays(-lastSeenDaysAgo), AppVersion = version,
                DeactivatedAt = deactivated ? now.AddDays(-1) : null,
            });
            return Task.CompletedTask;
        });
        return id;
    }

    private async Task<Gate.Result> EvaluateAsync(
        Kit kit, PersonBalanceOptions? personBalance = null, SalesSyncOptions? salesSync = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await Gate.EvaluateAsync(
            db, kit.LicenseId, personBalance ?? Options(), salesSync ?? Sales(), DateTimeOffset.UtcNow, default);
    }

    // ── Hepsi ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hepsi_tutunca_gecer()
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.11", lastSeenDaysAgo: 1);
        // §7.3-1 "davranışı değişecek kişi" bir koşul değil, bilgidir.
        var g = Kit.NewGroup();
        await kit.MemberAsync(g, 30m);
        await kit.MemberAsync(g, 20m);
        // Başka lisansın incelenmemiş işareti bu lisansı bağlamaz (taban satırları bütün lisanslardan yüklenir).
        await MarkerAsync(await OldLicenseAsync(), reviewed: false);

        var r = await EvaluateAsync(kit);

        r.Passes.Should().BeTrue();
        r.Failing.Should().BeEmpty();
        r.WindowDays.Should().Be(PersonBalanceOptions.DefaultActivePcWindowDays);
        (r.Clients.MinClientVersion, r.Clients.RecentActivations, r.Clients.Passes).Should().Be(("0.9.11", 1, true));
        r.Clients.Old.Should().BeEmpty();
        r.Verify.Should().Be(new Gate.VerifyCheck(0, 0, 0, 0, 0, 0));
        r.Baseline.Should().Be(new Gate.BaselineCheck(
            HasMarker: true, HasBaseline: true, Unreviewed: 0, NegativeOutside: 0, Worsened: 0,
            LedgerMismatchOutside: 0, ChangedMismatch: 0, CopyOutside: 0));
        r.Superset.Should().Be(new Gate.SupersetCheck("0.9.11", "0.9.11", true));
    }

    // ── 1. İstemci sürümü ─────────────────────────────────────────────────────

    [Fact]
    public async Task Kosul1_eski_surumlu_etkin_PC_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.11", lastSeenDaysAgo: 1);
        var old = await ActivationAsync(kit, "0.9.10", lastSeenDaysAgo: 2);

        var r = await EvaluateAsync(kit);

        r.Clients.RecentActivations.Should().Be(2);
        r.Clients.Old.Should().Equal(new SalesModeGate.OldActivation(old, "0.9.10"));
        r.Failing.Should().Equal(1);
        r.Passes.Should().BeFalse();
    }

    /// <summary>Sürüm bildirmeyen ya da ayrıştırılamayan sürüm bildiren PC eski sayılır; ham metin olduğu gibi döner.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    public async Task Kosul1_surumsuz_ya_da_ayristirilamayan_PC_eski_sayilir(string? version)
    {
        var kit = await ReadyLicenseAsync();
        var unknown = await ActivationAsync(kit, version, lastSeenDaysAgo: 1);

        var r = await EvaluateAsync(kit);

        r.Clients.Old.Should().Equal(new SalesModeGate.OldActivation(unknown, version));
        r.Failing.Should().Equal(1);
    }

    /// <summary>
    /// Pencere ≤ 0 ise sorgu hiçbir etkinleştirme döndürmez; koşul açık-hata ile geçer, eski PC'ler görünmezdi.
    /// Kapalı-hata: tutmaz. Web host'ta <c>ValidateOnStart</c>, CLI'da Görev 12'nin denetimi bunu zaten engeller; kapı
    /// bu güvenceyi çağıranın disiplinine bırakmaz.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Kosul1_gecersiz_pencere_tutmaz(int window)
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 1);

        var r = await EvaluateAsync(kit, Options(window: window));

        r.WindowDays.Should().Be(window);
        r.Clients.Passes.Should().BeFalse();
        r.Failing.Should().Equal(1);
    }

    [Fact]
    public async Task Kosul1_pencere_disi_ve_devre_disi_PC_sayilmaz()
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 20);
        await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 1, deactivated: true);

        var r = await EvaluateAsync(kit);

        r.Clients.RecentActivations.Should().Be(0);
        r.Clients.Passes.Should().BeTrue();
        r.Passes.Should().BeTrue();
    }

    [Fact]
    public async Task Kosul1_pencere_ayardan_okunur()
    {
        var kit = await ReadyLicenseAsync();
        var stale = await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 20);

        var r = await EvaluateAsync(kit, Options(window: 30));

        r.WindowDays.Should().Be(30);
        r.Clients.Old.Should().ContainSingle().Which.Id.Should().Be(stale);
        r.Failing.Should().Equal(1);
    }

    /// <summary>R06: alt sınır tanımsızsa karşılaştırılmaz, koşul tutmaz (kapalı-hata). 4. koşul da tutmaz.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abc", "abc")]
    public async Task Kosul1_alt_sinir_bos_ya_da_bozuk_tutmaz(string? pb, string? shown)
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.11", lastSeenDaysAgo: 1);

        var r = await EvaluateAsync(kit, Options(pb: pb));

        (r.Clients.MinClientVersion, r.Clients.RecentActivations, r.Clients.Passes).Should().Be(((string?)null, 1, false));
        r.Clients.Old.Should().BeEmpty("alt sınır tanımsızken karşılaştırma yapılmaz");
        r.Superset.Should().Be(new Gate.SupersetCheck(shown, "0.9.11", false));
        r.Failing.Should().Equal(1, 4);
    }

    // ── 2. Kural sayıları ─────────────────────────────────────────────────────

    [Fact]
    public async Task Kosul2_yetim_bacak_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 10m);
        // Çapası olmayan bir çekim grubuna bağlı bacak; D1 korunur (bakiye de 4 düşer).
        await kit.Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = row, Amount = -4m,
                Kind = Kit.KindDeduction, DrawGroupId = Guid.NewGuid(), CreatedByCustomerId = kit.CustomerId,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == row))
                .Balance -= 4m;
        });

        var r = await EvaluateAsync(kit);

        r.Verify.Should().Be(new Gate.VerifyCheck(0, 0, 0, OrphanLegs: 1, 0, 0));
        r.Verify.Passes.Should().BeFalse();
        r.Failing.Should().Equal(2);
    }

    /// <summary>D4: çapadan başka türde bacak. Kayıt eşitliği sayının D4'e (D5/D6'ya değil) bağlandığını sabitler.</summary>
    [Fact]
    public async Task Kosul2_D4_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 50m);
        var anchor = Guid.NewGuid();
        // Çapa düşüm (OriginalAmount 100 ≥ 15, D6 yok), bacak elle ayar (D4). Geri alma yok (D5 yok). D1 korunur.
        await kit.Sales.WithDbAsync(async db =>
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-5);
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = anchor, LicenseId = kit.LicenseId, WpfCustomerId = row, Amount = -10m, Kind = Kit.KindDeduction,
                OriginalAmount = 100m, DrawGroupId = anchor, CreatedByCustomerId = kit.CustomerId, CreatedAt = at,
            });
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = row, Amount = -5m, Kind = Kit.KindManual,
                DrawGroupId = anchor, CreatedByCustomerId = kit.CustomerId, CreatedAt = at,
            });
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == row))
                .Balance -= 15m;
        });

        var r = await EvaluateAsync(kit);

        r.Verify.Should().Be(new Gate.VerifyCheck(D4: 1, 0, 0, 0, 0, 0));
        r.Failing.Should().Equal(2);
    }

    /// <summary>
    /// D5: iki bacaklı düşüm grubunun yalnız bir bacağı geri alınmış (ya hepsi ya hiçbiri kuralı bozulur; S1 emsali
    /// <c>PersonBalanceVerifierTests.D5_bacaklarin_bir_kismi_geri_alinmis…</c>, <c>reversed: [l1]</c>). Kayıt eşitliği
    /// sayının D5'e (D4/D6'ya değil) bağlandığını sabitler.
    /// </summary>
    [Fact]
    public async Task Kosul2_D5_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var r1 = await kit.MemberAsync(null, 50m);
        var r2 = await kit.MemberAsync(null, 50m);
        // Çapa düşüm (OriginalAmount 15 = −Σ, D6 yok), bacak aynı türde ve alanları boş (D4 yok).
        var ids = await kit.SeedDrawAsync(Kit.KindDeduction, [(r1, 10m), (r2, 5m)], originalAmount: 15m);
        // Yalnız ikinci bacağı geri alan tekil geri alma: grupsuz (DrawGroupId NULL), D1 korunur (r2 +5).
        await kit.Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = r2, Amount = 5m,
                Kind = Kit.KindReversal, ReversesTransactionId = ids[1], CreatedByCustomerId = kit.CustomerId,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == r2))
                .Balance += 5m;
        });

        var r = await EvaluateAsync(kit);

        r.Verify.Should().Be(new Gate.VerifyCheck(0, D5: 1, 0, 0, 0, 0));
        r.Failing.Should().Equal(2);
    }

    /// <summary>D6: bacakların toplamı çapanın <c>OriginalAmount</c>'unu aşar (−Σ = 120 &gt; 100).</summary>
    [Fact]
    public async Task Kosul2_D6_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 150m);
        await kit.SeedDrawAsync(Kit.KindDeduction, [(row, 120m)], originalAmount: 100m);

        var r = await EvaluateAsync(kit);

        r.Verify.Should().Be(new Gate.VerifyCheck(0, 0, D6: 1, 0, 0, 0));
        r.Failing.Should().Equal(2);
    }

    /// <summary>Kırpma farkı = <c>TrimMismatches</c> + <c>ControlCharGroups</c> (plan fark 15): sondaki boşluk yalnız
    /// birincisini, sekme ikisini de sayar.</summary>
    [Theory]
    [InlineData(" ", 1, 0)]
    [InlineData("\t", 1, 1)]
    public async Task Kosul2_kirpma_farki_tutmaz(string suffix, int trim, int control)
    {
        var kit = await ReadyLicenseAsync();
        var g = Kit.NewGroup();
        await kit.MemberAsync(g);
        await kit.MemberAsync(g + suffix);

        var r = await EvaluateAsync(kit);

        (r.Verify.TrimMismatches, r.Verify.ControlCharGroups).Should().Be((trim, control));
        r.Failing.Should().Equal(2);
    }

    // ── 3. Taban ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kosul3_tabani_olmayan_lisans_tutmaz()
    {
        var kit = await OldLicenseAsync();

        var r = await EvaluateAsync(kit);

        (r.Baseline.HasMarker, r.Baseline.HasBaseline).Should().Be((false, false));
        r.Baseline.Passes.Should().BeFalse("önce verify --capture-baseline");
        r.Failing.Should().Equal(3);
    }

    [Fact]
    public async Task Kosul3_incelenmemis_isaret_tutmaz()
    {
        var kit = await OldLicenseAsync();
        await MarkerAsync(kit, reviewed: false);

        var r = await EvaluateAsync(kit);

        (r.Baseline.HasMarker, r.Baseline.HasBaseline, r.Baseline.Unreviewed).Should().Be((true, true, 1));
        r.Failing.Should().Equal(3);
    }

    /// <summary>Tabandaki eksi satır yeni sapma değildir; incelenmemişse açılışı yine engeller.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Kosul3_taban_ici_eksi_satir_incelenmisse_gecer_incelenmemisse_tutmaz(bool reviewed)
    {
        var kit = await ReadyLicenseAsync();
        var negative = await kit.MemberAsync(null, -5m);
        await BaselineRowAsync(kit, negative, -5m, -5m, reviewed);

        var r = await EvaluateAsync(kit);

        r.Baseline.NegativeOutside.Should().Be(0);
        r.Baseline.Unreviewed.Should().Be(reviewed ? 0 : 1);
        r.Failing.Should().Equal(reviewed ? Array.Empty<int>() : new[] { 3 });
    }

    [Fact]
    public async Task Kosul3_taban_disi_yeni_eksi_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        await kit.MemberAsync(null, -5m);

        var r = await EvaluateAsync(kit);

        r.Baseline.NegativeOutside.Should().Be(1);
        r.Failing.Should().Equal(3);
    }

    /// <summary>
    /// Tabandaki eksi satır daha da eksiye inerse (−5 → −8) yeni sapmadır. Taban dışı eksi ve farkı değişen D1 sıfır
    /// kalır; böylece sayı yalnız <c>WorsenedBaselineRows</c>'tan gelebilir.
    /// </summary>
    [Fact]
    public async Task Kosul3_daha_da_eksiye_inen_taban_satiri_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, -5m);
        await BaselineRowAsync(kit, row, -5m, -5m, reviewed: true);
        // Yeni bir elle ayar: defter −8, bakiye −8 (D1 korunur, fark 0 kalır).
        await kit.Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = row, Amount = -3m, Kind = Kit.KindManual,
                CreatedByCustomerId = kit.CustomerId, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == row))
                .Balance -= 3m;
        });

        var r = await EvaluateAsync(kit);

        (r.Baseline.NegativeOutside, r.Baseline.Worsened, r.Baseline.ChangedMismatch).Should().Be((0, 1, 0));
        r.Failing.Should().Equal(3);
    }

    /// <summary>Taban dışı D1: bakiye defterden 2 fazla. Öbür yeni sapma kalemleri sıfır kalır.</summary>
    [Fact]
    public async Task Kosul3_taban_disi_D1_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 10m);
        await kit.Sales.WithDbAsync(async db =>
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == row))
                .Balance += 2m);

        var r = await EvaluateAsync(kit);

        (r.Baseline.NegativeOutside, r.Baseline.Worsened, r.Baseline.LedgerMismatchOutside, r.Baseline.ChangedMismatch,
            r.Baseline.CopyOutside).Should().Be((0, 0, 1, 0, 0));
        r.Failing.Should().Equal(3);
    }

    /// <summary>
    /// Taban dışı kopya satır: kopyaya bağlı hareket ve bakiye. Bakiye defterle tutarlıdır (D1 yok), kopya eksi sayısına
    /// girmez; böylece sayı yalnız <c>CopyRows.Outside</c>'tan gelebilir.
    /// </summary>
    [Fact]
    public async Task Kosul3_taban_disi_kopya_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var canonical = await kit.MemberAsync(null);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: canonical);
        await kit.FundAsync(copy, 3m);

        var r = await EvaluateAsync(kit);

        (r.Baseline.NegativeOutside, r.Baseline.Worsened, r.Baseline.LedgerMismatchOutside, r.Baseline.ChangedMismatch,
            r.Baseline.CopyOutside).Should().Be((0, 0, 0, 0, 1));
        r.Failing.Should().Equal(3);
    }

    /// <summary>
    /// Sahip kararı 2 (29.10, (a)): tabandaki D1 satırının farkı değişirse (burada düzeltilip 0'a inmiş) günlük
    /// uyarıyla aynı kural geçerli olur ve açılış engellenir. Kapıda özel istisna yoktur.
    /// </summary>
    [Fact]
    public async Task Kosul3_farki_degisen_taban_D1_satiri_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 10m);
        // Yakalamada Balance 10, LedgerSum 8 (fark 2); bugün defter 10 (fark 0).
        await BaselineRowAsync(kit, row, 10m, 8m, reviewed: true);

        var r = await EvaluateAsync(kit);

        (r.Baseline.LedgerMismatchOutside, r.Baseline.ChangedMismatch).Should().Be((0, 1));
        r.Failing.Should().Equal(3);
    }

    // ── 4. Wh ⊇ Ws ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("abc", "abc")]
    public async Task Kosul4_SalesSync_alt_siniri_bos_tutmaz(string? ss, string? shown)
    {
        var kit = await ReadyLicenseAsync();

        var r = await EvaluateAsync(kit, salesSync: Sales(ss));

        r.Superset.Should().Be(new Gate.SupersetCheck("0.9.11", shown, false));
        r.Failing.Should().Equal(4);
    }

    /// <summary>Ön sürüm eki aynı numaranın yayımlanmış sürümünden önce gelir (<c>ClientVersion.IsAtLeast</c>).</summary>
    [Theory]
    [InlineData("0.9.10")]
    [InlineData("0.9.11-rc1")]
    public async Task Kosul4_PersonBalance_alt_siniri_kucukse_tutmaz(string pb)
    {
        var kit = await ReadyLicenseAsync();

        var r = await EvaluateAsync(kit, Options(pb: pb), Sales("0.9.11"));

        r.Superset.Should().Be(new Gate.SupersetCheck(pb, "0.9.11", false));
        r.Failing.Should().Equal(4);
    }

    [Theory]
    [InlineData("0.9.12", "0.9.11")]
    [InlineData("0.9.11", "0.9.11")]
    public async Task Kosul4_esit_ya_da_buyuk_tutar(string pb, string ss)
    {
        var kit = await ReadyLicenseAsync();

        var r = await EvaluateAsync(kit, Options(pb: pb), Sales(ss));

        r.Superset.Should().Be(new Gate.SupersetCheck(pb, ss, true));
        r.Passes.Should().BeTrue();
    }

    // ── Özet ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Failing_tutmayan_kosullari_artan_sirayla_verir()
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 1);

        var r = await EvaluateAsync(kit, Options(pb: "0.9.10"), Sales("0.9.11"));

        r.Failing.Should().Equal(1, 4);
        r.Passes.Should().BeFalse();
    }
}
```

- [ ] **Adım 2: Koştur — FAIL**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceEnableGateTests"`
Expected: FAIL. Derleme hatası `CS0234`/`CS0246`: `PersonBalanceEnableGate` bulunamaz. `PersonBalanceOptions` Görev 1'den gelir; Görev 1 yoksa o da bulunamaz.

- [ ] **Adım 3: Değerlendiriciyi yaz**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceEnableGate.cs` (yeni dosya):

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Havuz kipinin açılış kapısı: spec 2026-10-11 §7.2 "<c>enable</c> ön koşulları" 1–4. CLI
/// <c>person-balance enable</c> kuru koşuda her koşulu sayı olarak basar. <c>--apply</c> her seferinde yeniden
/// değerlendirir ve biri tutmazsa reddeder. Salt okunurdur: sorgular izlemesiz, hiçbir şey yazılmaz; makine adı,
/// parmak izi, müşteri adı ve telefon okunmaz.
/// <list type="number">
///   <item><b>İstemci sürümü.</b> Son <see cref="PersonBalanceOptions.ActivePcWindowDays"/> günde görülmüş ve devre
///   dışı bırakılmamış her etkinleştirmenin <c>AppVersion</c>'ı <c>PersonBalance:MinClientVersion</c>'ı
///   <see cref="ClientVersion.IsAtLeast"/> ile karşılar. Sürüm bildirmeyen ya da ayrıştırılamayan PC eski sayılır.
///   Alt sınır boş ya da bozuksa, ya da pencere ≤ 0 ise koşul tutmaz (kapalı-hata). Ölçüt satış senkronunun kapısıyla aynıdır. Sorgu onun
///   kopyasıdır, çünkü <see cref="SalesModeGate"/>'in sorgusu özeldir ve <see cref="SalesSyncOptions"/>'a bağlıdır.
///   Satış senkronu koduna dokunulmaz (plan fark 14); yalnız kayıtları ortaktır.</item>
///   <item><b>Kural sayıları.</b> <see cref="PersonBalanceVerifier"/>'ın D4, D5, D6, yetim bacak ve kırpma farkı
///   sayıları 0'dır. Kırpma farkı, spec §7.3-5'in SQL'i değil, S1'in <c>TrimMismatches</c> + <c>ControlCharGroups</c>
///   sayısıdır (plan fark 15).</item>
///   <item><b>Taban.</b> Lisansın tabanı vardır (<c>HasBaseline</c>; yoksa önce <c>verify --capture-baseline</c>).
///   İşaret dahil bütün taban satırlarının <c>ReviewedAt</c>'i doludur. Tabana göre yeni sapma yoktur; bunlar günlük
///   uyarının (§8.1) taban kalemleridir: taban dışı eksi, daha da eksiye inen, taban dışı D1, farkı değişen D1, taban
///   dışı kopya. Farkı düzeltilip 0'a inen taban D1 satırı da "farkı değişen" sayılır ve açılışı engeller (uyarıyla
///   aynı kural, plan sahip kararı 2).</item>
///   <item><b>Wh ⊇ Ws.</b> <c>SalesSync:MinClientVersion</c> doludur ve <c>PersonBalance:MinClientVersion</c> ona
///   eşit ya da ondan büyüktür. İki metin yan yana döner; böylece 1. koşulu geçen her PC satış senkronunu da
///   taşır.</item>
/// </list>
/// Lisans yoksa <see cref="InvalidOperationException"/> atılır; lisansı çağıran (CLI) önce doğrular.
/// </summary>
public static class PersonBalanceEnableGate
{
    /// <summary>2. koşul: verifier'ın kural sayıları (§7.3-5, §7.3-7).</summary>
    public sealed record VerifyCheck(int D4, int D5, int D6, int OrphanLegs, int TrimMismatches, int ControlCharGroups)
    {
        public bool Passes => D4 + D5 + D6 + OrphanLegs + TrimMismatches + ControlCharGroups == 0;
    }

    /// <summary>3. koşul.</summary>
    /// <param name="HasMarker">Lisansın işaret satırı var.</param>
    /// <param name="HasBaseline">İşaret var ya da lisans ilk yakalamadan sonra verilmiş (verifier'ın kuralı).</param>
    /// <param name="Unreviewed">Lisansın <c>ReviewedAt</c>'i boş taban satırları (işaret dahil).</param>
    /// <param name="NegativeOutside">Taban dışı eksi satır.</param>
    /// <param name="Worsened">Tabandakinin altına inen eksi satır.</param>
    /// <param name="LedgerMismatchOutside">Taban dışı D1.</param>
    /// <param name="ChangedMismatch">Tabandaki <c>Balance − LedgerSum</c> farkı değişen satır.</param>
    /// <param name="CopyOutside">Taban dışı kopya satır.</param>
    public sealed record BaselineCheck(bool HasMarker, bool HasBaseline, int Unreviewed, int NegativeOutside, int Worsened,
        int LedgerMismatchOutside, int ChangedMismatch, int CopyOutside)
    {
        public bool Passes => HasBaseline && Unreviewed == 0
            && NegativeOutside + Worsened + LedgerMismatchOutside + ChangedMismatch + CopyOutside == 0;
    }

    /// <summary>4. koşul. Metinler kırpılmış; boşsa null. Ayrıştırılamayan değer de olduğu gibi gösterilir, ama
    /// koşul tutmaz.</summary>
    public sealed record SupersetCheck(string? PersonBalanceMin, string? SalesSyncMin, bool Passes);

    /// <param name="WindowDays">1. koşulun penceresi (<c>PersonBalance:ActivePcWindowDays</c>).</param>
    public sealed record Result(int WindowDays, SalesModeGate.ClientVersionCheck Clients, VerifyCheck Verify,
        BaselineCheck Baseline, SupersetCheck Superset)
    {
        public bool Passes => Clients.Passes && Verify.Passes && Baseline.Passes && Superset.Passes;

        /// <summary>Tutmayan koşulların numaraları, artan sırada.</summary>
        public IReadOnlyList<int> Failing =>
            new[] { (Clients.Passes, 1), (Verify.Passes, 2), (Baseline.Passes, 3), (Superset.Passes, 4) }
                .Where(c => !c.Item1).Select(c => c.Item2).ToList();
    }

    public static async Task<Result> EvaluateAsync(LicenseDbContext db, Guid licenseId, PersonBalanceOptions personBalance,
        SalesSyncOptions salesSync, DateTimeOffset now, CancellationToken ct)
    {
        // 1. Pencere ≤ 0 ise sorgu hiçbir etkinleştirme döndürmez ve koşul açık-hata ile geçerdi (eski PC'ler
        // görünmez). Kapalı-hata: alt sınır tanımsızmış gibi döner, Passes false. Web host'ta ValidateOnStart, CLI'da
        // enable'ın kendi denetimi bunu zaten engeller; kapı yine de çağırana güvenmez.
        var clients = personBalance.ActivePcWindowDays > 0
            ? await ClientVersionsAsync(db, licenseId, personBalance, now - personBalance.ActivePcWindow, ct)
            : new SalesModeGate.ClientVersionCheck(null, 0, []);

        // 2–3: S1 denetiminin aynı sayıları. LoadAsync ilk yakalama anını bulmak için bütün lisansların taban
        // satırlarını yükler; incelenmemiş satırlar bu yüzden lisansa göre süzülür.
        var snapshot = await new PersonBalanceVerifier(db).LoadAsync(licenseId, now, ct);
        var report = PersonBalanceVerifier.Analyze(snapshot).Licenses.Single(r => r.LicenseId == licenseId);
        var verify = new VerifyCheck(
            D4: report.D4, D5: report.D5, D6: report.D6, OrphanLegs: report.OrphanLegs,
            TrimMismatches: report.TrimMismatches, ControlCharGroups: report.ControlCharGroups);
        var baseline = new BaselineCheck(
            HasMarker: report.HasMarker, HasBaseline: report.HasBaseline,
            Unreviewed: snapshot.Baselines.Count(b => b.LicenseId == licenseId && b.ReviewedAt is null),
            NegativeOutside: report.NegativeRows.Outside, Worsened: report.Details.WorsenedBaselineRows.Count,
            LedgerMismatchOutside: report.LedgerMismatches.Outside,
            ChangedMismatch: report.Details.ChangedMismatchRows.Count,
            CopyOutside: report.CopyRows.Outside);

        // 4.
        var pb = personBalance.ParseMinClientVersion();
        var ss = salesSync.ParseMinClientVersion();
        var superset = new SupersetCheck(
            Text(personBalance.MinClientVersion), Text(salesSync.MinClientVersion),
            pb is not null && ss is not null && ClientVersion.IsAtLeast(pb, ss));

        return new Result(personBalance.ActivePcWindowDays, clients, verify, baseline, superset);
    }

    private static string? Text(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary><c>SalesModeGate.ClientVersionsAsync</c>'in kalıbı, ortak bakiyenin alt sınırı ve penceresiyle.</summary>
    private static async Task<SalesModeGate.ClientVersionCheck> ClientVersionsAsync(
        LicenseDbContext db, Guid licenseId, PersonBalanceOptions options, DateTimeOffset since, CancellationToken ct)
    {
        // Makine adı ve parmak izi okunmaz.
        var recent = await db.Activations.AsNoTracking()
            .Where(a => a.LicenseId == licenseId && a.DeactivatedAt == null && a.LastSeenAt >= since)
            .OrderBy(a => a.Id)
            .Select(a => new SalesModeGate.OldActivation(a.Id, a.AppVersion))
            .ToListAsync(ct);

        var min = options.ParseMinClientVersion();
        if (min is null) return new SalesModeGate.ClientVersionCheck(null, recent.Count, []);

        var old = recent.Where(a => !ClientVersion.IsAtLeast(ClientVersion.Parse(a.AppVersion), min)).ToList();
        return new SalesModeGate.ClientVersionCheck(options.MinClientVersion!.Trim(), recent.Count, old);
    }
}
```

Var olan hiçbir dosya değişmez: `SalesModeGate`, `PersonBalanceVerifier`, `Program.cs` ve uçlar aynen kalır.

- [ ] **Adım 4: Koştur — PASS**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceEnableGateTests"`
Expected: PASS, 34 test (16 `Fact` + 18 `Theory` satırı), 0 uyarı.

- [ ] **Adım 5: Regresyon (Servis/CLI + satış senkronu CLI)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceMembers|FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalanceVerifier|FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~PersonBalanceCommand|FullyQualifiedName~SalesSyncTests"`
Expected: PASS, var olan testler değişmeden geçer. Önekler bilerek kısa tutuldu: `~PersonBalanceMembers` `PersonBalanceMembersRelationalTests`'i (`PersonBalanceMembersTests.cs:100`), `~PersonBalanceVerifier` aynı dosyadaki `PersonBalanceVerifierLoadTests`, `PersonBalanceVerifierCaptureTests` ve `PersonBalanceVerifierRelationalTests`'i (`PersonBalanceVerifierTests.cs:412`, `:473`, `:600`), `~PersonBalanceCommand` `PersonBalanceCommandRelationalTests`'i de kapsar. Kapının doğrudan çağırdığı `LoadAsync`'in testleri (`PersonBalanceVerifierLoadTests`) böylece regresyona girer. İlişkisel sınıflar Testcontainers kullandığı için Docker açık olmalıdır (Genel kurallar, `DOCKER_HOST` notu). `SalesSyncTests` yalnız `sales-sync` CLI'nin argüman ayrıştırmasıdır; veritabanı istemez ve kapıyı sınamaz. `SalesModeGate.cs`'in değişmediği şu komutun boş çıktısıyla görülür: `git diff --stat master -- OrderDeck.LicenseServer/Services/Sync/SalesModeGate.cs`.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceEnableGate.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceEnableGateTests.cs
git commit -F - <<'MSG'
feat(balance): havuz kipi açılış kapısının dört koşulunu değerlendir

person-balance enable'ın ön koşullarını (spec §7.2) tek bir salt okunur
değerlendiricide topla. 1: son ActivePcWindowDays günde görülmüş her PC,
PersonBalance:MinClientVersion'da olmalı; alt sınır boş ya da bozuksa ya da
pencere 0 veya eksiyse koşul tutmaz. 2: D4/D5/D6, yetim bacak ve kırpma farkı 0 olmalı. 3: taban olmalı,
işaret dahil hepsi incelenmiş olmalı ve tabana göre yeni sapma bulunmamalı.
4: Wh ⊇ Ws. Sayılar S1 denetiminden gelir. Sürüm sorgusu satış senkronu
kapısının kalıbını kopyalar, o koda dokunmaz. Kuru koşu ve --apply aynı
sonucu kullanacak (CLI ayrı görev).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 12: `person-balance enable|disable` CLI (spec §7.2, §7.4)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Tools/PersonBalanceCommand.cs` — sınıf belgesi `:12-37`; `Usage` `:40-44` (iki satır); `VerifyArgs`'tan (`:46-47`) sonra yeni `ToggleArgs`; `RunAsync(string[])` `:49-71` (dağıtım + iki ayar bölümünün bağlanması); `MarkReviewedAsync`'ten (`:170-210`) sonra yeni bölüm `// ── enable / disable (§7.2) ──` (`RunEnableAsync`, `RunDisableAsync`, özel `WriteGate`, `Verdict`); `TryParseArgs`'ın belgesi `:261-265` (tek cümle); `IsConsistent`'ten (`:299-304`) sonra yeni `TryParseAnyArgs` (dağıtım, saf) ve `TryParseToggleArgs`. `TryParseArgs`'ın gövdesi, `VerifyArgs`, `IsConsistent` ve verify gövdesi DEĞİŞMEZ.
- Değişmez: `OrderDeck.LicenseServer/Program.cs:74-79` (`person-balance` → `PersonBalanceCommand.RunAsync(args)`; alt komutlar sınıf içinde dağıtılır).
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Tools/PersonBalanceToggleCommandTests.cs` (InMemory, `IClassFixture<ApiFactory>`; yalnız `ExecuteUpdateAsync`'e varmadan dönen yollar: ayrıştırma, dağıtım (`TryParseAnyArgs`), geçersiz pencere, bilinmeyen lisans, zaten açık/kapalı, kuru koşu, koşulu tutmayan `--apply`).
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Tools/PersonBalanceToggleCommandRelationalTests.cs` (Testcontainers; yazan `--apply` yolları ve iki eşzamanlılık yarışı — `ExecuteUpdateAsync` InMemory'de yok; emsal `Tools/SalesSyncEnableRelationalTests.cs`: kanca `BeforeNonQueryHook` `:765-782`, CLI bağlamı `HookedEnableAsync` `:706-716`).

Ön koşul: Görev 1 (`PersonBalanceOptions`) ve Görev 11 (`PersonBalanceEnableGate`) dalda. Bu görev hiçbir lisansı havuz kipine almaz; komutu yalnız getirir (deploy'da her lisans satır kipinde, `PersonBalancePoolSince` boş). Göç yok; satır kipinin hiçbir yolu değişmez.

Kalıp `sales-sync enable|disable` (`SalesSync.Enable.cs:62-119`, `SalesSync.Disable.cs:54-87`) ile aynı: katı ayrıştırma, varsayılan kuru koşu, çıkış 0 tamam / kuru / yapılacak bir şey yok, 1 eşzamanlı değişiklik ya da `enable --apply`'da tutmayan koşul (hiçbir şey yazılmadı), 2 kullanım / yapılandırma / bilinmeyen lisans. `enable` kapının dört koşulunu (`PersonBalanceEnableGate.EvaluateAsync`, Görev 11) yalnız sayı, sürüm ve etkinleştirme Id'siyle basar (ad, kullanıcı adı, makine adı, parmak izi yok); `--apply` koşulları her koşuda yeniden değerlendirir, biri tutmazsa reddeder; yazım tek koşullu `UPDATE … WHERE PersonBalancePoolSince IS NULL`'dur (atomik; 0 satır = eşzamanlı bir `enable`). `disable` kill switch'tir: kapı yok, veri işlemi yok, yalnız `PersonBalancePoolSince = NULL` (`… IS NOT NULL` koşullu). Alt sınır boş ya da bozuksa (R06) 1. ve 4. koşul tutmaz → `enable --apply` reddeder.

**Not (spec ↔ kod):**
- İskelet farkı **13** uygulanır: `TryParseArgs` verify'a özgü kalır (S1 testi `PersonBalanceCommandTests.cs:38` `"person-balance enable --license {L}"` geçersiz sayılmaya DEĞİŞMEDEN devam eder); `enable|disable` için ayrı `TryParseToggleArgs`. Dağıtım (`args[1]`'e göre hangi ayrıştırıcı) saf bir yardımcıdadır: `TryParseAnyArgs(args, out verify, out toggle)`, tek kullanıcısı `RunAsync(string[])`. Böylece geçerli bir `enable`/`disable` satırının `TryParseToggleArgs`'a, `verify`'ın `TryParseArgs`'a gittiği yapılandırma ve veritabanı olmadan sınanır (yanlış dağıtım prod'da geçerli `enable --license X`'i Usage + 2'ye düşürürdü). Bu, iskeletin imza listesine EK bir public yardımcıdır; mevcut imzaların hiçbiri değişmez.
- İskelet farkı **18e** uygulanır: zaten açıkken `enable`, zaten kapalıyken `disable` (kuru ya da `--apply`) mesaj basar, yazmaz, 0 döner; `PersonBalancePoolSince` korunur.
- Spec ↔ kod farkı (bu görev) — **kuru koşu koşul tutmasa da 0.** Spec §7.2 "biri tutmazsa `--apply` reddedilir" der; kuru koşu için çıkış kodu söylemez. `sales-sync enable` emsali (`SalesSync.Enable.cs:107-111`): kuru koşu raporu basar ve 0 döner; ret (1) yalnız `--apply`'da.
- **Talimattan sapma (mühendislik kararı, bu görev): `disable` yapılandırma okumaz.** Talimat iki bölümün okunmasını ve `ActivePcWindowDays <= 0 → 2` denetimini `RunAsync(string[])`'te enable ve disable için birlikte ister. Bu planda `PersonBalance:*`/`SalesSync:*` yalnız `enable` dalında okunur; kill switch, onu hiç kullanmayan bir ayarın bozukluğuyla kilitlenmemeli (§7.4 "Havuz kipinde sorun: `person-balance disable`"). **Mimara not:** iskeletin 18. maddesine (belirsizlikler) bir satır eklenmeli ("`disable` yapılandırma okumaz"). Mimar reddederse talimattaki sıra uygulanır: `RunAsync(string[])` önce iki bölümü okur, sonra enable/disable'a dağıtır; pencere denetimi yine `RunEnableAsync`'in başında kalır (aşağıdaki madde), disable dalı bölümleri yalnız okur.
- **Pencere denetimi gövdede (bu görev).** `ActivePcWindowDays <= 0 → stderr + 2` `RunEnableAsync`'in başında, lisans okumasından önce yapılır; CLI giriş noktasında kopyası yok. Sebep: pencere 0 ya da eksiyse kapı (Görev 11, `PersonBalanceEnableGate.EvaluateAsync`) 1. koşulu kapalı-hata ile tutmaz sayar (sorguyu hiç koşturmaz, `ClientVersionCheck(null, 0, [])`; Görev 11 testi `Kosul1_gecersiz_pencere_tutmaz`); CLI ayrıca lisans okunmadan anlamlı bir yapılandırma hatasıyla 2 döner (ek güvence, operatöre "koşul 1 tutmuyor" yerine asıl sebebi gösterir). Denetim gövdededir ki testler de onu sınasın. `Pencere_gecersizken_enable_2_yazmaz` sınar.
- **Talimattan sapma (bu görev) — 1. koşulun alt sınır satırı ham değeri gösterir.** Talimat `"  PersonBalance:MinClientVersion: {Clients.MinClientVersion ?? "tanımsız"}"` der; `ClientVersionCheck.MinClientVersion` ayrıştırılamayan alt sınırda (ör. `abc`, R06 "bozuk") `null`'dır, bu yüzden satır "tanımsız" basar, aynı çıktının 4. koşul satırı ise `=abc` gösterir (çelişki; operatör yazdığı değeri tanımsız görür). Plan ham değeri (`Superset.PersonBalanceMin`, kırpılmış) basar, yazılı ama ayrıştırılamıyorsa ` (ayrıştırılamıyor)` ekler. Boşken metin talimattaki gibi "tanımsız".
- Bilinen dar pencere (sahip sorusu değil): kapının değerlendirmesi ile koşullu UPDATE aynı işlemde değil; aradaki milisaniyelerde ilk kez görünen eski bir PC kapıya sayılmaz. §7.2'nin çalışma anı koruması (havuz kipinde alt sınırdan eski istemcinin yeni yazımı 409 `client-update-required`) onu kapatır; `sales-sync enable`'ın 1. koşulu da işlem dışında değerlendirilir.
- Koşullu UPDATE `License.RowVersion`'ı ilerletir (`License.cs:55-57` yorumu). Aynı anda izlenen `License`'ı kaydeden seyrek bir panel yazımı (payment-account, shopper-code) bir kez eşzamanlılık hatası görebilir; `sales-sync enable|disable` ile aynı, kabul edilmiş durum (yönetici adımı, yayın penceresi dışında).

- [ ] **Adım 1: InMemory testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Tools/PersonBalanceToggleCommandTests.cs`:

```csharp
using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>person-balance enable|disable</c> (spec 2026-10-11 §7.2, §7.4) — InMemory'de sınanabilen yollar: ayrıştırma,
/// alt komuta göre dağıtım, geçersiz pencere, bilinmeyen lisans, zaten açık/kapalı, kuru koşu, koşulu tutmayan
/// <c>--apply</c>. Hepsi
/// <c>ExecuteUpdateAsync</c>'e varmadan döner (InMemory onu desteklemez); yazan yollar
/// <see cref="PersonBalanceToggleCommandRelationalTests"/>'te. Çıktıda ad, kullanıcı adı, makine adı ve parmak izi yok.
/// </summary>
public sealed class PersonBalanceToggleCommandTests : IClassFixture<ApiFactory>
{
    private const string Min = "0.9.11";

    private readonly ApiFactory _factory;

    public PersonBalanceToggleCommandTests(ApiFactory factory) => _factory = factory;

    /// <summary>Satırdaki <c>{L}</c> her koşuda üretilen bir lisans GUID'iyle değişir (literal GUID yok).</summary>
    private static string[] Args(string line) => line.Replace("{L}", Guid.NewGuid().ToString()).Split(' ');

    private static PersonBalanceOptions Pb(string? min = Min) => new() { MinClientVersion = min };

    private static SalesSyncOptions Ss(string? min = Min) => new() { MinClientVersion = min };

    // ── Ayrıştırma ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("person-balance enable --license {L}")]
    [InlineData("person-balance enable --license {L} --apply")]
    [InlineData("person-balance disable --apply --license {L}")]
    public void Gecerli_argumanlar(string line) =>
        PersonBalanceCommand.TryParseToggleArgs(Args(line), out _).Should().BeTrue();

    [Theory]
    [InlineData("person-balance enable")]
    [InlineData("person-balance enable --apply")]
    [InlineData("person-balance enable --license yok")]
    [InlineData("person-balance enable --license {L} --license {L}")]
    [InlineData("person-balance enable --license {L} --apply --apply")]
    [InlineData("person-balance enable --license {L} --details")]
    [InlineData("person-balance disable --license")]
    [InlineData("person-balance toggle --license {L}")]
    public void Gecersiz_argumanlar(string line) =>
        PersonBalanceCommand.TryParseToggleArgs(Args(line), out _).Should().BeFalse();

    [Fact]
    public void Ayristirilan_degerler()
    {
        var license = Guid.NewGuid();

        PersonBalanceCommand.TryParseToggleArgs(
                ["person-balance", "disable", "--apply", "--license", license.ToString("N")], out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new PersonBalanceCommand.ToggleArgs(Enable: false, License: license, Apply: true));
    }

    /// <summary>S1 sözleşmesi (S2 planı fark 13): verify'ın ayrıştırıcısı enable'ı tanımaz; dağıtım <c>args[1]</c>'e göre.</summary>
    [Fact]
    public void Verify_ayristiricisi_enable_i_hala_reddeder() =>
        PersonBalanceCommand.TryParseArgs(["person-balance", "enable", "--license", Guid.NewGuid().ToString()], out _)
            .Should().BeFalse();

    /// <summary>
    /// Dağıtım (S2 planı fark 13): geçerli bir enable/disable satırı <see cref="PersonBalanceCommand.TryParseToggleArgs"/>'a
    /// gider ve <c>ToggleArgs</c> verir. Yanlış dağıtım (ör. her satırın verify ayrıştırıcısına gitmesi) prod'da geçerli
    /// <c>enable --license X</c>'i Usage + 2'ye düşürürdü.
    /// </summary>
    [Theory]
    [InlineData("person-balance enable --license {L}", true, false)]
    [InlineData("person-balance enable --license {L} --apply", true, true)]
    [InlineData("person-balance disable --apply --license {L}", false, true)]
    public void Dagitim_toggle_satirini_toggle_ayristiricisina_verir(string line, bool enable, bool apply)
    {
        PersonBalanceCommand.TryParseAnyArgs(Args(line), out var verify, out var toggle).Should().BeTrue();

        verify.Should().BeNull();
        toggle.Should().NotBeNull();
        (toggle!.Enable, toggle.Apply).Should().Be((enable, apply));
    }

    /// <summary>Dağıtım: verify satırı S1'in ayrıştırıcısına gider, <c>VerifyArgs</c> verir (S1 sözleşmesi aynen).</summary>
    [Theory]
    [InlineData("person-balance verify")]
    [InlineData("person-balance verify --license {L} --details")]
    [InlineData("person-balance verify --capture-baseline --apply")]
    public void Dagitim_verify_satirini_verify_ayristiricisina_verir(string line)
    {
        PersonBalanceCommand.TryParseAnyArgs(Args(line), out var verify, out var toggle).Should().BeTrue();

        verify.Should().NotBeNull();
        toggle.Should().BeNull();
    }

    /// <summary>Dağıtım: iki ayrıştırıcının da reddettiği satır false (RunAsync Usage + 2), iki çıktı da boş.</summary>
    [Theory]
    [InlineData("person-balance")]
    [InlineData("person-balance enable --apply")]
    [InlineData("person-balance disable --license {L} --details")]
    [InlineData("person-balance verify --apply")]
    [InlineData("person-balance toggle --license {L}")]
    public void Dagitim_gecersiz_satiri_reddeder(string line)
    {
        PersonBalanceCommand.TryParseAnyArgs(Args(line), out var verify, out var toggle).Should().BeFalse();

        verify.Should().BeNull();
        toggle.Should().BeNull();
    }

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>
    /// S1 öncesinden kalan (geri tarihli) lisans ve incelenmiş taban işareti: 3. koşul tutar. Sınıfın testleri aynı
    /// veritabanını paylaşır; geri tarihleme "ilk yakalamadan sonra verilmiş" kuralını sıradan bağımsız kılar
    /// (emsal <c>PersonBalanceCommandTests.OldLicenseAsync</c>).
    /// </summary>
    private async Task<Kit> ReviewedLicenseAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await kit.Sales.WithDbAsync(async db =>
        {
            (await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId)).IssuedAt = DateTimeOffset.UtcNow.AddYears(-1);
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = PersonBalanceVerifier.LicenseMarker,
                CapturedAt = at, ReviewedAt = at,
            });
        });
        return kit;
    }

    /// <summary>Son görülmesi şimdi olan etkin bir etkinleştirme. Döner: Id, parmak izi ve makine adı (çıktıda
    /// geçmediklerini sınamak için).</summary>
    private static async Task<(Guid Id, string Fingerprint, string Machine)> ActivationAsync(Kit kit, string? version)
    {
        var activation = new Activation
        {
            Id = Guid.NewGuid(), LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
            MachineName = $"m-{Guid.NewGuid():N}", ActivatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            LastSeenAt = DateTimeOffset.UtcNow, AppVersion = version,
        };
        await kit.Sales.WithDbAsync(db =>
        {
            db.Activations.Add(activation);
            return Task.CompletedTask;
        });
        return (activation.Id, activation.HardwareFingerprint, activation.MachineName!);
    }

    private static Task<DateTimeOffset?> PoolSinceAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.Licenses.AsNoTracking()
            .Where(l => l.Id == kit.LicenseId)
            .Select(l => l.PersonBalancePoolSince)
            .SingleAsync());

    private async Task<(int Exit, string Output, string Error)> EnableAsync(
        Guid licenseId, bool apply, PersonBalanceOptions? personBalance = null, SalesSyncOptions? salesSync = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PersonBalanceCommand.RunEnableAsync(
            db, new PersonBalanceCommand.ToggleArgs(Enable: true, licenseId, apply), personBalance ?? Pb(),
            salesSync ?? Ss(), output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(int Exit, string Output, string Error)> DisableAsync(Guid licenseId, bool apply)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PersonBalanceCommand.RunDisableAsync(
            db, new PersonBalanceCommand.ToggleArgs(Enable: false, licenseId, apply), output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private static string U(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    // ── Gövde ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bilinmeyen_lisans_2(bool enable)
    {
        var unknown = Guid.NewGuid();

        var (exit, output, error) = enable ? await EnableAsync(unknown, apply: true) : await DisableAsync(unknown, apply: true);

        exit.Should().Be(2);
        error.Should().Contain($"Lisans bulunamadı: {unknown}");
        output.Should().BeEmpty();
    }

    /// <summary>
    /// Pencere 0 ya da eksiyse kapı (Görev 11) 1. koşulu kapalı-hata ile tutmaz sayar; CLI ayrıca lisans okunmadan
    /// anlamlı bir yapılandırma hatasıyla 2 döner (ek güvence, operatöre "koşul 1 tutmuyor" yerine asıl sebebi
    /// gösterir). Denetim gövdede (CLI giriş noktasında değil), lisans okunmadan ve kapı değerlendirilmeden yapılır → 2,
    /// hiçbir şey basılmaz ve yazılmaz. Lisans dört koşulu tutacak biçimde kurulsa bile.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Pencere_gecersizken_enable_2_yazmaz(int days)
    {
        var kit = await ReviewedLicenseAsync();

        var (exit, output, error) = await EnableAsync(kit.LicenseId, apply: true,
            personBalance: new PersonBalanceOptions { MinClientVersion = Min, ActivePcWindowDays = days });

        exit.Should().Be(2);
        error.Should().Contain("PersonBalance:ActivePcWindowDays 0'dan büyük olmalı.");
        output.Should().BeEmpty();
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    /// <summary>S2 planı fark 18e: zaten açıkken enable (--apply dahil) kapıyı değerlendirmez, yazmaz, 0; tarih korunur.</summary>
    [Fact]
    public async Task Zaten_acikken_enable_0_yazmaz()
    {
        var kit = await ReviewedLicenseAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-3);
        await kit.EnablePoolAsync(since);

        var (exit, output, error) = await EnableAsync(kit.LicenseId, apply: true);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA — person-balance enable").And.Contain($"Lisans: {kit.LicenseId}")
            .And.Contain($"Havuz kipi zaten açık (PersonBalancePoolSince={U(since)}); hiçbir şey yazılmadı.");
        output.Should().NotContain("Koşul 1").And.NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().Be(since);
    }

    [Fact]
    public async Task Zaten_kapaliyken_disable_0_yazmaz()
    {
        var kit = await ReviewedLicenseAsync();

        var (exit, output, error) = await DisableAsync(kit.LicenseId, apply: true);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA — person-balance disable")
            .And.Contain("Havuz kipi zaten kapalı; hiçbir şey yazılmadı.");
        output.Should().NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    /// <summary>
    /// Kuru koşu dört koşulu sayılarla basar ve hiçbir şey yazmaz (R59). Alt sınırın altındaki PC'nin yalnız Id'si ve
    /// sürümü görünür; kullanıcı adı, parmak izi ve makine adı görünmez. Koşul tutmasa da kuru koşu 0 döner.
    /// </summary>
    [Fact]
    public async Task Kuru_enable_dort_kosulu_basar_yazmaz_0()
    {
        var kit = await ReviewedLicenseAsync();
        var g = Kit.NewGroup();
        var nameA = $"p{Guid.NewGuid():N}";
        var nameB = $"p{Guid.NewGuid():N}";
        await kit.MemberAsync(g, 30m, username: nameA);
        await kit.MemberAsync(g, 20m, username: nameB);
        var current = await ActivationAsync(kit, Min);
        var old = await ActivationAsync(kit, "0.9.10");

        var (exit, output, error) = await EnableAsync(kit.LicenseId, apply: false);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("KURU ÇALIŞTIRMA — person-balance enable").And.Contain($"Lisans: {kit.LicenseId}");
        output.Should().Contain("Koşul 1 — istemci sürümü (son 14 gün): TUTMUYOR")
            .And.Contain($"PersonBalance:MinClientVersion: {Min}")
            .And.Contain("görülen etkin etkinleştirme: 2; alt sınırın altında ya da sürümsüz: 1")
            .And.Contain($"{old.Id} (sürüm=0.9.10)");
        output.Should().Contain("Koşul 2 — verify: D4=0 D5=0 D6=0 yetim=0 kırpma=0 kontrol karakteri=0: TUTUYOR");
        output.Should().Contain("Koşul 3 — taban: var; incelenmemiş 0; taban dışı eksi 0; daha da eksiye inen 0; "
            + "taban dışı D1 0; farkı değişen D1 0; taban dışı kopya 0: TUTUYOR");
        output.Should().Contain($"Koşul 4 — Wh ⊇ Ws: PersonBalance:MinClientVersion={Min} SalesSync:MinClientVersion={Min}: TUTUYOR");
        output.Should().Contain("Sonuç: koşul 1 tutmuyor")
            .And.Contain("Kuru çalıştırma: hiçbir şey yazılmadı. Açmak için --apply ekle.");
        output.Should().NotContain(current.Id.ToString(), "alt sınırı geçen PC listelenmez");
        foreach (var secret in new[] { nameA, nameB, current.Fingerprint, current.Machine, old.Fingerprint, old.Machine })
            output.Should().NotContain(secret);
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    /// <summary>R06: alt sınır boş ya da bozukken 1. ve 4. koşul tutmaz; kuru koşu 0 ile basar, <c>--apply</c> 1 ile
    /// reddeder ve bayrak boş kalır. Bozuk değer 1. koşulda ham hâliyle ve "(ayrıştırılamıyor)" ekiyle görünür; 4.
    /// koşulun satırıyla çelişmez.</summary>
    [Theory]
    [InlineData(null, "tanımsız", "tanımsız")]
    [InlineData("abc", "abc (ayrıştırılamıyor)", "abc")]
    public async Task Kosul_tutmazken_enable_apply_1_yazmaz(string? min, string shownLimit, string shownSuperset)
    {
        var kit = await ReviewedLicenseAsync();
        var broken = Pb(min);

        var dry = await EnableAsync(kit.LicenseId, apply: false, personBalance: broken);
        dry.Exit.Should().Be(0, dry.Error);
        dry.Output.Should().Contain("Koşul 1 — istemci sürümü (son 14 gün): TUTMUYOR")
            .And.Contain($"PersonBalance:MinClientVersion: {shownLimit}")
            .And.Contain("alt sınırın altında ya da sürümsüz: karşılaştırılamadı")
            .And.Contain($"Koşul 4 — Wh ⊇ Ws: PersonBalance:MinClientVersion={shownSuperset} SalesSync:MinClientVersion={Min}: TUTMUYOR")
            .And.Contain("Sonuç: koşul 1, 4 tutmuyor");

        var (exit, output, error) = await EnableAsync(kit.LicenseId, apply: true, personBalance: broken);

        exit.Should().Be(1);
        error.Should().Contain("Havuz kipi açılmadı: koşul 1, 4 tutmuyor; hiçbir şey yazılmadı.");
        output.Should().Contain("UYGULAMA — person-balance enable").And.Contain("Sonuç: koşul 1, 4 tutmuyor");
        output.Should().NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    [Fact]
    public async Task Kuru_disable_veri_islemi_yok_der_0()
    {
        var kit = await ReviewedLicenseAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-2);
        await kit.EnablePoolAsync(since);

        var (exit, output, error) = await DisableAsync(kit.LicenseId, apply: false);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("KURU ÇALIŞTIRMA — person-balance disable").And.Contain($"Lisans: {kit.LicenseId}")
            .And.Contain($"Kuru çalıştırma: havuz kipi kapatılacak (PersonBalancePoolSince={U(since)} → boş); "
                + "veri işlemi yok. Kapatmak için --apply ekle.");
        (await PoolSinceAsync(kit)).Should().Be(since);
    }
}
```

- [ ] **Adım 2: Testcontainers testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Tools/PersonBalanceToggleCommandRelationalTests.cs`:

```csharp
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using ToggleArgs = OrderDeck.LicenseServer.Tools.PersonBalanceCommand.ToggleArgs;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>person-balance enable|disable --apply</c> (spec 2026-10-11 §7.2, §7.4) gerçek SQL Server'da: tek koşullu UPDATE
/// bayrağı yazar ya da boşaltır, ikinci <c>enable</c> etkisizdir (S2 planı fark 18e), <c>disable</c> hiçbir hareket ya da
/// bakiye satırına dokunmaz. Eşzamanlı bir rakip bayrağı UPDATE'ten hemen önce değiştirirse 0 satır → 1, rakibin değeri
/// kalır. CLI'ın bağlamı fabrikanınkinden ayrı bir bağlantıdır (komutun gerçek kurulumu gibi); kanca yalnız ona takılır.
/// InMemory'de <c>ExecuteUpdateAsync</c> ve atomik koşullu yazım yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceToggleCommandRelationalTests : IAsyncLifetime
{
    private const string Min = "0.9.11";
    private static readonly PersonBalanceOptions Pb = new() { MinClientVersion = Min };
    private static readonly SalesSyncOptions Ss = new() { MinClientVersion = Min };

    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public PersonBalanceToggleCommandRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>Dört koşulu tutan lisans: geri tarihli, incelenmiş taban işareti, alt sınırda tek etkin PC, verisiz.</summary>
    private async Task<Kit> PassingLicenseAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await kit.Sales.WithDbAsync(async db =>
        {
            (await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId)).IssuedAt = DateTimeOffset.UtcNow.AddYears(-1);
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = PersonBalanceVerifier.LicenseMarker,
                CapturedAt = at, ReviewedAt = at,
            });
            db.Activations.Add(new Activation
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
                ActivatedAt = DateTimeOffset.UtcNow.AddDays(-3), LastSeenAt = DateTimeOffset.UtcNow, AppVersion = Min,
            });
        });
        return kit;
    }

    private static Task<DateTimeOffset?> PoolSinceAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.Licenses.AsNoTracking()
            .Where(l => l.Id == kit.LicenseId)
            .Select(l => l.PersonBalancePoolSince)
            .SingleAsync());

    /// <summary>CLI'ın kendi bağlamı (<c>RunAsync(string[])</c> gibi doğrudan <c>UseSqlServer</c>), isteğe bağlı kancayla.</summary>
    private async Task<(int Exit, string Output, string Error)> RunAsync(ToggleArgs args, IInterceptor? hook = null)
    {
        var builder = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs);
        if (hook is not null) builder.AddInterceptors(hook);
        await using var db = new LicenseDbContext(builder.Options);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = args.Enable
            ? await PersonBalanceCommand.RunEnableAsync(db, args, Pb, Ss, output, error, default)
            : await PersonBalanceCommand.RunDisableAsync(db, args, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    // ── enable ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kosullar_tutunca_enable_apply_bayragi_yazar()
    {
        var kit = await PassingLicenseAsync();

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: true, kit.LicenseId, Apply: true));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA — person-balance enable").And.Contain("Sonuç: bütün koşullar tutuyor")
            .And.Contain("UYGULANDI: havuz kipi açık, PersonBalancePoolSince=");
        var since = await PoolSinceAsync(kit);
        since.Should().NotBeNull();
        since!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        // İkinci koşu etkisiz (S2 planı fark 18e): 0, kapı basılmaz, tarih korunur.
        var again = await RunAsync(new ToggleArgs(Enable: true, kit.LicenseId, Apply: true));
        again.Exit.Should().Be(0, again.Error);
        again.Output.Should().Contain("Havuz kipi zaten açık").And.NotContain("Koşul 1").And.NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().Be(since);
    }

    /// <summary>Kapının değerlendirmesinden sonra, koşullu UPDATE'ten hemen önce rakip bir enable bayrağı yazar: UPDATE
    /// 0 satır günceller → 1, rakibin değeri kalır.</summary>
    [Fact]
    public async Task Enable_apply_esanli_degisiklikte_1()
    {
        var kit = await PassingLicenseAsync();
        var rival = DateTimeOffset.UtcNow.AddDays(-2);
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[PersonBalancePoolSince]", StringComparison.Ordinal),
            before: () => kit.Sales.WithDbAsync(db => db.Licenses.Where(l => l.Id == kit.LicenseId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.PersonBalancePoolSince, rival))));

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: true, kit.LicenseId, Apply: true), hook);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1, output);
        error.Should().Contain("Havuz kipi bu arada değişti (başka bir enable?)").And.Contain("yeniden çalıştır");
        output.Should().Contain("Sonuç: bütün koşullar tutuyor").And.NotContain("UYGULANDI");
        var since = await PoolSinceAsync(kit);
        since.Should().NotBeNull();
        since!.Value.Should().BeCloseTo(rival, TimeSpan.FromSeconds(1), "rakibin yazdığı değer korunur");
    }

    // ── disable ───────────────────────────────────────────────────────────────

    /// <summary>Kill switch (§7.2, §7.4): bayrak boşalır; Örnek 1'in çok bacaklı grubu ve bakiyeler olduğu gibi kalır.</summary>
    [Fact]
    public async Task Disable_apply_bayragi_bosaltir_veriye_dokunmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync(DateTimeOffset.UtcNow.AddDays(-5));
        var before = await LedgerShapeAsync(kit);

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: false, kit.LicenseId, Apply: true));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA — person-balance disable").And.Contain(
            "UYGULANDI: havuz kipi kapalı (satır kipi); çok bacaklı geçmiş okunur ve geri alınır, veri işlemi yapılmadı.");
        (await PoolSinceAsync(kit)).Should().BeNull();
        (await LedgerShapeAsync(kit)).Should().Be(before, "disable veri işlemi yapmaz");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    [Fact]
    public async Task Disable_apply_esanli_degisiklikte_1()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync(DateTimeOffset.UtcNow.AddDays(-5));
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[PersonBalancePoolSince]", StringComparison.Ordinal),
            before: () => kit.Sales.WithDbAsync(db => db.Licenses.Where(l => l.Id == kit.LicenseId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.PersonBalancePoolSince, (DateTimeOffset?)null))));

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: false, kit.LicenseId, Apply: true), hook);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1, output);
        error.Should().Contain("Havuz kipi bu arada değişti (başka bir disable?)").And.Contain("yeniden çalıştır");
        output.Should().NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    /// <summary>Lisansın defteri ve bakiyeleri: hareket sayısı, bacak sayısı (DrawGroupId dolu), hareket toplamı,
    /// bakiye satırı sayısı ve bakiye toplamı.</summary>
    private static Task<(int Transactions, int Legs, decimal LedgerTotal, int Balances, decimal BalanceTotal)>
        LedgerShapeAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(async db =>
        {
            var amounts = await db.CustomerBalanceTransactions.AsNoTracking()
                .Where(t => t.LicenseId == kit.LicenseId)
                .Select(t => new { t.Amount, Leg = t.DrawGroupId != null })
                .ToListAsync();
            var balances = await db.CustomerBalances.AsNoTracking()
                .Where(b => b.LicenseId == kit.LicenseId)
                .Select(b => b.Balance)
                .ToListAsync();
            return (amounts.Count, amounts.Count(a => a.Leg), amounts.Sum(a => a.Amount), balances.Count, balances.Sum());
        });

    // ── Kanca ─────────────────────────────────────────────────────────────────

    /// <summary>Okuyucusuz komut kancası: <c>when</c>'i tutan ilk komutun hemen öncesinde <c>before</c>'u bir kez
    /// koşturur (<see cref="SalesSyncEnableRelationalTests"/>'teki kancanın kopyası; o sınıf içi özel).</summary>
    private sealed class BeforeNonQueryHook(Func<string, bool> when, Func<Task> before) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Ran && when(command.CommandText))
            {
                Ran = true;
                await before();
            }
            return result;
        }
    }
}
```

Not: kanca yalnız okuyucusuz komutları (`ExecuteNonQuery`) görür; kapının okumaları (`PersonBalanceEnableGate`, verifier) okuyuculudur, bayrağın kolonunu içeren ilk okuyucusuz komut `RunEnableAsync`/`RunDisableAsync`'in koşullu UPDATE'idir. Rakip yazım fabrikanın bağlamından gider (kancasız).

- [ ] **Adım 3: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceToggleCommand"`
Expected: FAIL — derleme hatası: `'PersonBalanceCommand' does not contain a definition for 'TryParseToggleArgs'` / `'TryParseAnyArgs'` / `'RunEnableAsync'` / `'RunDisableAsync'` (CS0117) ve `The type name 'ToggleArgs' does not exist in the type 'PersonBalanceCommand'` (CS0426).

- [ ] **Adım 4: `PersonBalanceCommand` — sınıf belgesi, `Usage`, `ToggleArgs`, dağıtım**

`OrderDeck.LicenseServer/Tools/PersonBalanceCommand.cs` başına bir `using` ekle (`SalesSyncOptions` `Services.Sync`'te); `using OrderDeck.LicenseServer.Services.CustomerSync;` (`:8`) satırından sonra:

```csharp
using OrderDeck.LicenseServer.Services.Sync;
```

Sınıf belgesi `:12-37` — bugünkü:

```csharp
/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance verify [seçenekler]
/// Ortak bakiye denetimi (spec 2026-10-11 §7.3; çekirdek <see cref="PersonBalanceVerifier"/>):
/// <list type="bullet">
///   <item><c>verify [--license &lt;guid&gt;] [--details]</c> — salt okunur rapor (§7.3 maddeleri 1–10). Lisans
///   verilmezse bütün lisanslar, verisizler dahil (plan fark 16). <c>--details</c> şu satırların
///   <c>WpfCustomerId</c>'lerini basar: eksi, D1 ve kopya (her biri tabanda ve taban dışı ayrı), grubu dağılmış
///   silinmiş üye, kural bozan grubun ve yetim bacağın satırı, daha da eksiye inen ve D1 farkı değişen taban satırı;
///   ayrıca kural bozan grupların çapa ve yetim bacakların HAREKET Id'lerini. Yalnız GUID (ad, telefon, numara yok),
///   yalnız konsola; dosyaya yazılmaz.</item>
///   <item><c>--capture-baseline [--apply]</c> — taban çizgisi (§7.1 adım 2): tabanı olmayan lisansın taban dışı eksi,
///   D1 ve kopya satırları o anki Balance ve LedgerSum'la, lisans başına bir işaret satırıyla. Var olan taban satırına
///   dokunmaz; tabanı olan lisansın taban dışı satırı yakalamadan sonra doğmuş yeni sapmadır, tabana alınmaz (plan
///   fark 20, <see cref="PersonBalanceVerifier.BaselineRowsToCapture"/>).</item>
///   <item><c>--mark-reviewed --license &lt;guid&gt; [--details] [--apply]</c> — sahip incelemesi (§7.1 adım 4):
///   lisansın incelenmemiş taban satırlarının <c>ReviewedAt</c>'i. <c>--details</c> işaretlenecek satırların
///   <c>WpfCustomerId</c>'lerini de basar (işaret hariç).</item>
/// </list>
/// Yazan bayraklar <c>--apply</c> olmadan yazacağını basar; yalnız <c>PersonBalanceBaselines</c>'a yazar. Rapora
/// <c>--apply</c> vermek kullanım hatasıdır. Çıkış: 0 tamam; 1 eşzamanlı yazım ya da silme, ya da işaretsiz
/// lisansta inceleme, hiçbir şey yazılmadı; 2 kullanım, yapılandırma ya da bilinmeyen lisans. Öbür veritabanı
/// hataları yutulmaz: işlenmemiş istisna olarak yükselir (yığın izi, 0/1/2 dışı çıkış kodu). Argümanlar katı (<see cref="SalesSync"/>
/// deseni). Bağlantı dizesi web host'unkiyle aynı kaynaktan (<c>ConnectionStrings:LicenseDb</c>). Çıktıda ad, telefon,
/// makine adı YOK — yalnız sayılar, tutarlar, Id'ler. <c>enable</c>/<c>disable</c> S2'de bu sınıfa eklenir.
/// </summary>
```

Şununla değiştir (`:17-29`'daki üç `verify` maddesi harfiyen korunur; yalnız baş satırlar, iki yeni madde ve kapanış paragrafı değişir):

```csharp
/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance &lt;verify|enable|disable&gt; [seçenekler]
/// Ortak bakiye denetimi ve havuz kipinin anahtarı (spec 2026-10-11 §7.2, §7.3; çekirdekler
/// <see cref="PersonBalanceVerifier"/> ve <see cref="PersonBalanceEnableGate"/>):
/// <list type="bullet">
///   <item><c>verify [--license &lt;guid&gt;] [--details]</c> — salt okunur rapor (§7.3 maddeleri 1–10). Lisans
///   verilmezse bütün lisanslar, verisizler dahil (plan fark 16). <c>--details</c> şu satırların
///   <c>WpfCustomerId</c>'lerini basar: eksi, D1 ve kopya (her biri tabanda ve taban dışı ayrı), grubu dağılmış
///   silinmiş üye, kural bozan grubun ve yetim bacağın satırı, daha da eksiye inen ve D1 farkı değişen taban satırı;
///   ayrıca kural bozan grupların çapa ve yetim bacakların HAREKET Id'lerini. Yalnız GUID (ad, telefon, numara yok),
///   yalnız konsola; dosyaya yazılmaz.</item>
///   <item><c>--capture-baseline [--apply]</c> — taban çizgisi (§7.1 adım 2): tabanı olmayan lisansın taban dışı eksi,
///   D1 ve kopya satırları o anki Balance ve LedgerSum'la, lisans başına bir işaret satırıyla. Var olan taban satırına
///   dokunmaz; tabanı olan lisansın taban dışı satırı yakalamadan sonra doğmuş yeni sapmadır, tabana alınmaz (plan
///   fark 20, <see cref="PersonBalanceVerifier.BaselineRowsToCapture"/>).</item>
///   <item><c>--mark-reviewed --license &lt;guid&gt; [--details] [--apply]</c> — sahip incelemesi (§7.1 adım 4):
///   lisansın incelenmemiş taban satırlarının <c>ReviewedAt</c>'i. <c>--details</c> işaretlenecek satırların
///   <c>WpfCustomerId</c>'lerini de basar (işaret hariç).</item>
///   <item><c>enable --license &lt;guid&gt; [--apply]</c> — havuz kipini açar (§7.2, §7.1 adım 7): kuru koşu kapının dört
///   koşulunu (<see cref="PersonBalanceEnableGate"/>) sayılarla basar, koşul tutmasa da 0 döner; <c>--apply</c>
///   koşulları yeniden değerlendirir (önceki kuru koşuya güvenmez), biri tutmazsa hiçbir şey yazmadan 1; tutarsa
///   <c>PersonBalancePoolSince</c> = şimdi, tek koşullu UPDATE'le (yalnız bayrak hâlâ boşsa).</item>
///   <item><c>disable --license &lt;guid&gt; [--apply]</c> — kill switch (§7.2, §7.4): <c>PersonBalancePoolSince</c>
///   boşalır, lisans satır kipine iner. Kapı yok, yapılandırma okunmaz, veri işlemi yok; çok bacaklı geçmiş S1
///   okuyucularıyla okunur ve geri alınır.</item>
/// </list>
/// Yazan yollar <c>--apply</c> olmadan yazacağını basar. <c>verify</c>'ın bayrakları yalnız
/// <c>PersonBalanceBaselines</c>'a, <c>enable</c>/<c>disable</c> yalnız <c>Licenses.PersonBalancePoolSince</c>'a yazar.
/// Rapora <c>--apply</c> vermek kullanım hatasıdır. Çıkış: 0 tamam, kuru koşu ya da yapılacak bir şey yok (zaten
/// açıkken <c>enable</c>, zaten kapalıyken <c>disable</c>; S2 planı fark 18e); 1 eşzamanlı yazım ya da silme, işaretsiz
/// lisansta inceleme, ya da <c>enable --apply</c>'da tutmayan koşul — hiçbir şey yazılmadı; 2 kullanım, yapılandırma
/// ya da bilinmeyen lisans. Öbür veritabanı hataları yutulmaz: işlenmemiş istisna olarak yükselir (yığın izi, 0/1/2
/// dışı çıkış kodu). Argümanlar katı (<see cref="SalesSync"/> deseni). Bağlantı dizesi web host'unkiyle aynı kaynaktan
/// (<c>ConnectionStrings:LicenseDb</c>); <c>enable</c> ayrıca <c>PersonBalance:*</c> ve <c>SalesSync:*</c> bölümlerini
/// okur. Çıktıda ad, telefon, makine adı, parmak izi YOK — yalnız sayılar, tutarlar, sürümler, Id'ler.
/// </summary>
```

`Usage` `:40-44` — bugünkü:

```csharp
    private const string Usage =
        "Kullanım:\n"
        + "  person-balance verify [--license <guid>] [--details]\n"
        + "  person-balance verify [--license <guid>] [--details] --capture-baseline [--apply]\n"
        + "  person-balance verify --license <guid> [--details] --mark-reviewed [--apply]";
```

Şununla değiştir:

```csharp
    private const string Usage =
        "Kullanım:\n"
        + "  person-balance verify [--license <guid>] [--details]\n"
        + "  person-balance verify [--license <guid>] [--details] --capture-baseline [--apply]\n"
        + "  person-balance verify --license <guid> [--details] --mark-reviewed [--apply]\n"
        + "  person-balance enable --license <guid> [--apply]\n"
        + "  person-balance disable --license <guid> [--apply]";
```

`VerifyArgs` (`:46-47`) satırından hemen sonra ekle:

```csharp

    /// <summary>Ayrıştırılmış <c>enable</c>/<c>disable</c>.</summary>
    /// <param name="Enable">true = <c>enable</c>, false = <c>disable</c>.</param>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record ToggleArgs(bool Enable, Guid License, bool Apply);
```

`RunAsync(string[] args)` `:49-71` — bugünkü:

```csharp
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var parsed))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var conn = config.GetConnectionString("LicenseDb");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.Error.WriteLine("ConnectionStrings:LicenseDb bulunamadı.");
            return 2;
        }

        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(conn).Options;
        await using var db = new LicenseDbContext(options);
        return await RunAsync(db, parsed, Console.Out, Console.Error, CancellationToken.None);
    }
```

Şununla değiştir:

```csharp
    public static async Task<int> RunAsync(string[] args)
    {
        // enable/disable kendi ayrıştırıcısında; verify'ınki onları reddetmeye devam eder (S2 planı fark 13).
        if (!TryParseAnyArgs(args, out var verify, out var toggle))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var conn = config.GetConnectionString("LicenseDb");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.Error.WriteLine("ConnectionStrings:LicenseDb bulunamadı.");
            return 2;
        }

        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(conn).Options;
        await using var db = new LicenseDbContext(options);
        var output = Console.Out;
        var error = Console.Error;
        var ct = CancellationToken.None;
        if (toggle is null) return await RunAsync(db, verify!, output, error, ct);
        // disable kill switch'tir: yapılandırma okumaz, kullanmadığı bir ayarın bozukluğuyla kilitlenmez.
        if (!toggle.Enable) return await RunDisableAsync(db, toggle, output, error, ct);

        // Web host'un ValidateOnStart'ı CLI'da koşmaz; pencerenin denetimi RunEnableAsync'in başında (gövdede).
        var personBalance = config.GetSection(PersonBalanceOptions.Section).Get<PersonBalanceOptions>()
            ?? new PersonBalanceOptions();
        var salesSync = config.GetSection(SalesSyncOptions.Section).Get<SalesSyncOptions>() ?? new SalesSyncOptions();
        return await RunEnableAsync(db, toggle, personBalance, salesSync, output, error, ct);
    }
```

- [ ] **Adım 5: `PersonBalanceCommand` — `enable` / `disable` gövdeleri ve kapının basımı**

`MarkReviewedAsync`'in kapanışından (`:210`, `        return 0;\n    }`) sonra, `Breakdown`'dan (`:212`) önce ekle:

```csharp

    // ── enable / disable (§7.2) ──────────────────────────────────────────────

    /// <summary><c>enable</c>'ın gövdesi: argümanlar ayrıştırılmış, bağlam ve iki ayar bölümü verilmiş. Testler buradan
    /// girer. Pencere (<c>PersonBalance:ActivePcWindowDays</c>) 0 ya da eksiyse lisans okunmadan 2: kapı
    /// (<see cref="PersonBalanceEnableGate"/>) 1. koşulu bu durumda zaten kapalı-hata ile tutmaz sayar; bu denetim ek
    /// güvencedir ve operatöre "koşul 1 tutmuyor" yerine asıl sebebi (yapılandırma hatası) gösterir. Zaten açıksa
    /// kapıyı değerlendirmez, yazmaz, 0 (S2 planı fark 18e). Kuru koşu dört koşulu basar ve koşul tutmasa da 0 döner; <c>--apply</c> koşulları bu koşuda yeniden
    /// değerlendirir, biri tutmazsa 1. Yazım tek koşullu UPDATE'tir (yalnız bayrak hâlâ boşsa): eşzamanlı bir
    /// <c>enable</c> önce yazdıysa 0 satır → 1, hiçbir şey yazılmadı.</summary>
    public static async Task<int> RunEnableAsync(
        LicenseDbContext db, ToggleArgs args, PersonBalanceOptions personBalance, SalesSyncOptions salesSync,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        // Web host'ta ValidateOnStart bunu açılışta yakalar; CLI'da ve doğrudan çağıranda savunma burada.
        if (personBalance.ActivePcWindowDays <= 0)
        {
            error.WriteLine("PersonBalance:ActivePcWindowDays 0'dan büyük olmalı.");
            return 2;
        }

        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.PersonBalancePoolSince })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }

        output.WriteLine($"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — person-balance enable — {Time(now)}");
        output.WriteLine($"Lisans: {args.License}");
        if (license.PersonBalancePoolSince is { } since)
        {
            output.WriteLine($"Havuz kipi zaten açık (PersonBalancePoolSince={Time(since)}); hiçbir şey yazılmadı.");
            return 0;
        }

        var gate = await PersonBalanceEnableGate.EvaluateAsync(db, args.License, personBalance, salesSync, now, ct);
        WriteGate(output, gate);

        if (!args.Apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Açmak için --apply ekle.");
            return 0;
        }
        if (!gate.Passes)
        {
            error.WriteLine($"Havuz kipi açılmadı: koşul {string.Join(", ", gate.Failing)} tutmuyor; hiçbir şey yazılmadı.");
            return 1;
        }

        // Tek koşullu deyim (atomik): yalnız bayrak hâlâ boşsa yazar. Eşzamanlı bir enable önce yazdıysa 0 satır.
        var updated = await db.Licenses
            .Where(l => l.Id == args.License && l.PersonBalancePoolSince == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PersonBalancePoolSince, now), ct);
        if (updated == 0)
        {
            error.WriteLine("Havuz kipi bu arada değişti (başka bir enable?); hiçbir şey yazılmadı, yeniden çalıştır.");
            return 1;
        }
        output.WriteLine($"UYGULANDI: havuz kipi açık, PersonBalancePoolSince={Time(now)}");
        return 0;
    }

    /// <summary><c>disable</c>'ın gövdesi — kill switch (§7.2, §7.4): kapı yok, veri işlemi yok, yalnız
    /// <c>PersonBalancePoolSince = NULL</c>. Zaten kapalıysa yazmaz, 0 (S2 planı fark 18e). Yazım tek koşullu UPDATE'tir
    /// (yalnız bayrak hâlâ doluysa): eşzamanlı bir <c>disable</c> önce boşalttıysa 0 satır → 1.</summary>
    public static async Task<int> RunDisableAsync(
        LicenseDbContext db, ToggleArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.PersonBalancePoolSince })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }

        output.WriteLine($"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — person-balance disable — {Time(now)}");
        output.WriteLine($"Lisans: {args.License}");
        if (license.PersonBalancePoolSince is not { } since)
        {
            output.WriteLine("Havuz kipi zaten kapalı; hiçbir şey yazılmadı.");
            return 0;
        }
        if (!args.Apply)
        {
            output.WriteLine($"Kuru çalıştırma: havuz kipi kapatılacak (PersonBalancePoolSince={Time(since)} → boş); "
                + "veri işlemi yok. Kapatmak için --apply ekle.");
            return 0;
        }

        var updated = await db.Licenses
            .Where(l => l.Id == args.License && l.PersonBalancePoolSince != null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PersonBalancePoolSince, (DateTimeOffset?)null), ct);
        if (updated == 0)
        {
            error.WriteLine("Havuz kipi bu arada değişti (başka bir disable?); hiçbir şey yazılmadı, yeniden çalıştır.");
            return 1;
        }
        output.WriteLine("UYGULANDI: havuz kipi kapalı (satır kipi); çok bacaklı geçmiş okunur ve geri alınır, "
            + "veri işlemi yapılmadı.");
        return 0;
    }

    /// <summary>Kapının dört koşulu (§7.2) ve sonuç satırı. Yalnız sayı, sürüm ve etkinleştirme Id'si; makine adı,
    /// parmak izi, müşteri yok (kapı onları okumaz).</summary>
    private static void WriteGate(TextWriter o, PersonBalanceEnableGate.Result gate)
    {
        var clients = gate.Clients;
        o.WriteLine($"Koşul 1 — istemci sürümü (son {gate.WindowDays} gün): {Verdict(clients.Passes)}");
        // Ham değer (kırpılmış) 4. koşulun satırıyla aynı; yazılı ama ayrıştırılamıyorsa (R06 "bozuk") Clients'ın
        // alt sınırı null'dır, operatör yazdığı değeri "tanımsız" görmesin.
        var limit = gate.Superset.PersonBalanceMin;
        o.WriteLine($"  PersonBalance:MinClientVersion: {limit ?? "tanımsız"}"
            + (limit is not null && clients.MinClientVersion is null ? " (ayrıştırılamıyor)" : ""));
        o.WriteLine($"  görülen etkin etkinleştirme: {clients.RecentActivations}; alt sınırın altında ya da sürümsüz: "
            + (clients.MinClientVersion is null
                ? "karşılaştırılamadı"
                : clients.Old.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var a in clients.Old.OrderBy(a => a.Id.ToString(), StringComparer.Ordinal))
            o.WriteLine($"    {a.Id} (sürüm={a.AppVersion ?? "bilinmiyor"})");

        var v = gate.Verify;
        o.WriteLine($"Koşul 2 — verify: D4={v.D4} D5={v.D5} D6={v.D6} yetim={v.OrphanLegs} kırpma={v.TrimMismatches} "
            + $"kontrol karakteri={v.ControlCharGroups}: {Verdict(v.Passes)}");

        var b = gate.Baseline;
        o.WriteLine($"Koşul 3 — taban: {(b.HasBaseline ? "var" : "YOK — önce verify --capture-baseline")}; "
            + $"incelenmemiş {b.Unreviewed}; taban dışı eksi {b.NegativeOutside}; daha da eksiye inen {b.Worsened}; "
            + $"taban dışı D1 {b.LedgerMismatchOutside}; farkı değişen D1 {b.ChangedMismatch}; "
            + $"taban dışı kopya {b.CopyOutside}: {Verdict(b.Passes)}");

        var s = gate.Superset;
        o.WriteLine($"Koşul 4 — Wh ⊇ Ws: PersonBalance:MinClientVersion={s.PersonBalanceMin ?? "tanımsız"} "
            + $"SalesSync:MinClientVersion={s.SalesSyncMin ?? "tanımsız"}: {Verdict(s.Passes)}");

        o.WriteLine(gate.Passes
            ? "Sonuç: bütün koşullar tutuyor"
            : $"Sonuç: koşul {string.Join(", ", gate.Failing)} tutmuyor");
    }

    private static string Verdict(bool passes) => passes ? "TUTUYOR" : "TUTMUYOR";
```

`TryParseArgs`'ın belgesi `:261-265` — bugünkü ilk satır:

```csharp
    /// <summary>args[0] komut adı, args[1] alt komut (yalnız <c>verify</c>; S2 <c>enable</c>/<c>disable</c>
    /// ekler). Tanınmayan argüman, ikinci kez verilen seçenek, değersiz ya da GUID olmayan <c>--license</c>, rapora
```

Şununla değiştir (belgenin geri kalanı `:263-265` aynen):

```csharp
    /// <summary>args[0] komut adı, args[1] alt komut (yalnız <c>verify</c>; <c>enable</c>/<c>disable</c>
    /// <see cref="TryParseToggleArgs"/>'ta, bu ayrıştırıcı onları reddeder — S2 planı fark 13). Tanınmayan argüman, ikinci
    /// kez verilen seçenek, değersiz ya da GUID olmayan <c>--license</c>, rapora
```

`IsConsistent`'in (`:299-304`) kapanışından sonra, sınıfın kapanış `}`'inden (`:305`) önce ekle:

```csharp

    /// <summary>Dağıtım (S2 planı fark 13): <c>args[1]</c> <c>enable</c> ya da <c>disable</c> ise
    /// <see cref="TryParseToggleArgs"/>, değilse <see cref="TryParseArgs"/>. true'da çıktılardan tam biri dolu; false'ta
    /// ikisi de boş. Tek kullanıcısı <c>RunAsync(string[])</c>; <c>public</c>: dağıtım yapılandırma ve veritabanı
    /// olmadan saf fonksiyon olarak sınanır.</summary>
    public static bool TryParseAnyArgs(string[] args, out VerifyArgs? verify, out ToggleArgs? toggle)
    {
        verify = null;
        toggle = null;
        return args.Length > 1 && args[1] is ("enable" or "disable")
            ? TryParseToggleArgs(args, out toggle)
            : TryParseArgs(args, out verify);
    }

    /// <summary><c>enable</c>/<c>disable</c>'ın ayrıştırıcısı (args[0] komut adı, args[1] alt komut): <c>--license
    /// &lt;guid&gt;</c> tam bir kez ve zorunlu, <c>--apply</c> en çok bir kez. Başka her argüman, ikinci kez verilen
    /// seçenek, değersiz ya da GUID olmayan <c>--license</c> → false (<see cref="TryParseArgs"/>'ın katılığı).
    /// <c>public</c>: kabul edilen biçimler saf fonksiyon olarak sınanır.</summary>
    public static bool TryParseToggleArgs(string[] args, [NotNullWhen(true)] out ToggleArgs? parsed)
    {
        parsed = null;
        if (args.Length < 2 || args[1] is not ("enable" or "disable")) return false;

        Guid? license = null;
        var apply = false;
        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--license")
            {
                if (license is not null || i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var id)) return false;
                license = id;
                i++;
            }
            else if (arg == "--apply")
            {
                if (apply) return false;
                apply = true;
            }
            else
            {
                return false;
            }
        }

        if (license is not { } licenseId) return false;
        parsed = new ToggleArgs(args[1] == "enable", licenseId, apply);
        return true;
    }
```

(`CultureInfo` için `using System.Globalization;` dosyada zaten var, `:2`; `NotNullWhen` için `System.Diagnostics.CodeAnalysis`, `:1`. `PersonBalanceEnableGate` ve `PersonBalanceOptions` `Services.CustomerSync`'te, `:8`.)

- [ ] **Adım 6: Koştur — PASS**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceToggleCommand"`
Expected: PASS (InMemory sınıfı + Testcontainers sınıfı; Docker açık olmalı — kapalıysa "Genel kurallar"daki `DOCKER_HOST` adımı, PowerShell'den). Derleme 0 uyarı (`TreatWarningsAsErrors`).

- [ ] **Adım 7: Regresyon — Servis/CLI + S1 CLI ilişkisel + kapı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceMembersTests|FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalanceVerifierTests|FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~PersonBalanceCommandTests|FullyQualifiedName~PersonBalanceCommandRelationalTests|FullyQualifiedName~PersonBalanceEnableGateTests"`
Expected: hepsi PASS, S1 testleri DEĞİŞMEDEN. `PersonBalanceCommandRelationalTests` Testcontainers'tır, Docker ister; kapalıysa "Genel kurallar"daki `DOCKER_HOST` adımı (PowerShell'den), düşüşü regresyon sanma. (Filtre `FullyQualifiedName~` sınıf adıyla eşleşir, dosya adıyla değil: `PersonBalanceMembersTests.cs` ve `PersonBalanceVerifierTests.cs` içindeki `PersonBalanceMembersRelationalTests` / `PersonBalanceVerifierRelationalTests` bu filtreye girmez; onlar bu görevin değiştirdiği koda dokunmaz.) Özellikle `PersonBalanceCommandTests.Gecersiz_argumanlar("person-balance enable --license {L}")` yeşil kalır (`TryParseArgs` verify'a özgü; fark 13) ve `PersonBalanceCommandRelationalTests` verify'ın `RunAsync(db, VerifyArgs, …)` gövdesinden aynen girer.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Tools/PersonBalanceCommand.cs \
  OrderDeck.LicenseServer.Tests/Tools/PersonBalanceToggleCommandTests.cs \
  OrderDeck.LicenseServer.Tests/Tools/PersonBalanceToggleCommandRelationalTests.cs
git commit -F - <<'MSG'
feat(balance): person-balance enable|disable ile havuz kipinin anahtarını getir

Havuz kipini yalnız CLI açar ve kapatır (ortak bakiye §7.2, §7.4). enable
kuru koşuda kapının dört koşulunu (istemci sürümü, verify sayıları, taban
incelemesi, Wh ⊇ Ws) yalnız sayı, sürüm ve etkinleştirme Id'siyle basar;
--apply koşulları yeniden değerlendirir, biri tutmazsa hiçbir şey yazmadan
1 döner, tutarsa PersonBalancePoolSince'i tek koşullu UPDATE'le yazar.
disable kill switch: kapı ve veri işlemi yok, bayrağı boşaltır. Zaten
açık/kapalıyken yazmaz, 0. verify'ın ayrıştırıcısı ve S1 sözleşmesi aynen;
deploy'da hiçbir lisans havuz kipine geçmez.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 13: D3 uyarısı + dört metrik (spec §8.1, §8.5, §8.6)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifier.cs` — sınıf belgesi (`:18-22`), `ActivePcWindow` belgesi (`:30-31`), `LicenseRow` (`:42`), `LoadAsync`'in lisans seçimi (`:74-76`), `AnalyzeLicense`'ın uyarı listesi (`:378-389`; taban bloğundan SONRA).
- Değiştir: `OrderDeck.LicenseServer/Services/Observability/OrderDeckMetrics.cs` — kurucu (`:24-53`), özellikler (`:55-62`).
- Değiştir: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs` — `using`'ler (`:8-9`), alanlar ve kurucu (Görev 5'ten sonraki hâli; master `:78-94`), `ClientUpdateRequired` (master `:727-735`), Görev 5'in `PoolClientUpdateRequired` ve `CustomerNotSynced`'i, `BalanceBusy` (master `:686-694`), Görev 7'nin `ApplyPoolAsync`'i (`DrawAsync` sonucunun eşlemesinden önce), yeni `CountMultiLegDraw`.
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs` — `using`'ler (`:1-7` + Görev 9'un `System.Diagnostics`'i), alanlar ve kurucu (`:32-45`), Görev 9'un `BalanceBusy` ve `DrawManualAdjustmentAsync`'i, Görev 10'un `ReverseInPoolAsync`'i, yeni `CountMultiLegDraw`.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceD3AlertTests.cs` (saf, `Analyze`).
- Test (genişlet): `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifyJobTests.cs` — bir test.
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceMetricsTests.cs` (`IClassFixture<PersonBalanceConfiguredApiFactory>`, InMemory).

**Not (spec ↔ kod):**
- **İskelet farkı 16 uygulanır.** Verifier'ın anlık görüntüsünde kip yoktu (`LicenseRow(Id, IssuedAt)`, `:42`). `LicenseRow`'a varsayılan değerli `PoolSince` eklenir (var olan `new V.LicenseRow(Lic, issued ?? Issued)` çağrıları, `PersonBalanceVerifierTests.cs:29`, aynen derlenir). D3 uyarısı **tabandan bağımsızdır**: §8.1'in "taban yoksa yalnız sayı" istisnası eksi/D1/kopya kalemleri içindir; havuz kipi ancak tabanı olan lisansta açılır (`enable`'ın 3. koşulu, iskelet farkı 15), açıklanamayan eksi kişi orada çalışma anı kuralının (kişi kilidi, D3) bozulmasıdır. "Açıklanabilir" S1'in `NegativePersonsExplained`'idir (`:360-361`: kişinin bütün eksi satırları etkin tabanda ve hiçbiri tabandaki değerinin altında değil) — spec §8.1'in tanımıyla birebir. Satır kipinde 6. madde yalnız sayı kalır. Günlük iş (`PersonBalanceVerifyJob`) DEĞİŞMEZ: `Alerts`'ı olduğu gibi e-postaya ve CLI çıktısına (`PersonBalanceCommand.cs:232`) taşır; e-postada yalnız lisans Id'si ve sayı.
- **İskelet farkı 6'nın kapanışı:** `ActivePcWindow` belgesi "Ayarı S2'nin `enable`'ı getirir" diyordu; `verify`'ın 9. maddesi sabit 14 günde kalır, `PersonBalance:ActivePcWindowDays` yalnız `enable`'ın 1. koşulunu (`PersonBalanceEnableGate`) besler. Yalnız yorum değişir.
- **İskelet farkı 18h uygulanır** (adlar ve etiketler); WPF `endpoint` kümesi `preview | scope | apply | reverse`. Satış senkronu kapısının 409'u (`ClientUpdateRequired`) bugün dört uçtan döner — preview (`:117`), `scope` (`:187-188`), apply (`:345`), reverse (`:665`). Sayaç bu ortak yardımcıya konur; böylece `scope` da `endpoint=scope` etiketiyle sayılır. Gerekçe: mod On'da v0.9.10 anahtarsız işte ilk olarak `scope`'u sorar ve onun 409'unda mesajı engeller (R6-01, `:164-168`), preview'a hiç gelmez; `scope` sayılmasaydı §8.5'in "metrikte `client-update-required` görünür" denetimi tam bu istemcide boş kalırdı. Spec §8.6 `endpoint` değerlerini saymaz; `scope`'un sayılması spec'e aykırı değildir. Havuz kapısı (`PoolClientUpdateRequired`) yalnız preview ve apply'da (iskelet farkı 4), orada `scope` oluşmaz.
- **`balance-busy` iki kipte de sayılır.** WPF reverse (`:681`) ve panel grup geri alması (Görev 9'da `BalanceBusy`'ye taşındı) satır kipinde de 503 döndürebilir (S1'in `ReverseGroupAsync`'i). Sayaç yardımcının içinde olduğundan onlar da sayılır; yanıt, günlük ve davranış değişmez (yalnız gözlem). Satış senkronunun komut ve kira uçlarındaki `client-update-required` sayılmaz (başka denetleyiciler).
- `balance-busy` sayacı InMemory'de doğrulanmaz (üç ardışık jeton çakışması kurulamaz). `endpoint=apply` Görev 14'ün `Uc_denemede_de_cakisan_apply_503_balance_busy_hicbir_sey_yazmaz` testinde, `endpoint=panel/manual-adjustment` Görev 16'nın `K5_uc_denemede_de_cakisirsa_503_balance_busy_hicbir_sey_yazmaz` testinde aynı `MeterListener` kalıbıyla ilişkisel olarak doğrulanır (panel yardımcısının `$"panel/{endpoint}"` öneki dahil). `panel/reverse` ve WPF `endpoint=reverse` testsizdir; bu iki etiket yalnız koddan (iki `BalanceBusy` yardımcısının çağrıları) bilinir.
- Sahip sorusu adayı yok.

Bu görev iki parçadır ve iki commit'tir. **A — D3 uyarısı:** verifier lisansın kip bayrağını okur; havuz kipindeki lisansta `NegativePersons > NegativePersonsExplained` ise `Alerts`'a `"D3: havuzu < 0 kişi {n} (tabanla açıklanamayan, havuz kipi)"` satırı eklenir. **B — dört sayaç:** `OrderDeckMetrics`'e `orderdeck.balance.customer_not_synced_total` (`endpoint`), `orderdeck.balance.client_update_required_total` (`gate`, `endpoint`), `orderdeck.balance.busy_total` (`endpoint`), `orderdeck.balance.multi_leg_draws_total` (`legs`). Sayaçlar 409/503 yanıtını üreten özel yardımcılarda artar (tek yer, her çağıran kapsanır); çok bacaklı sayaç `DrawAsync`'in `Drawn` sonucunda ve yalnız `Legs > 1` iken. Yanıt gövdeleri, durum kodları, günlük satırları ve satır kipinin davranışı DEĞİŞMEZ; göç yok.

### A — D3 uyarısı

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceD3AlertTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;
using V = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifier;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// D3 uyarısı — spec 2026-10-11 §8.1 son madde, plan S2 fark 16: havuz kipindeki lisansta (<c>PoolSince</c> dolu)
/// taban satırlarıyla açıklanamayan havuzu &lt; 0 kişi <c>Alerts</c>'a girer; tabandan bağımsızdır ("taban yoksa
/// yalnız sayı" istisnası eksi/D1/kopya kalemleri içindir). Açıklanabilir = kişinin bütün eksi satırları tabanda ve
/// hiçbiri tabandaki değerinin altında değil (<see cref="PersonBalanceVerifier.LicenseReport.NegativePersonsExplained"/>).
/// Satır kipinde 6. madde yalnız sayıdır.
/// </summary>
public sealed class PersonBalanceD3AlertTests
{
    private static readonly Guid Lic = Guid.NewGuid();
    private static readonly DateTimeOffset Issued = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Captured = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PoolSince = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private const string D3Line = "D3: havuzu < 0 kişi 1 (tabanla açıklanamayan, havuz kipi)";

    /// <summary><c>PersonBalanceVerifierTests.Snap</c> kalıbı; lisans satırı kip bayrağını taşır.</summary>
    private static V.Snapshot Snap(
        DateTimeOffset? poolSince, IEnumerable<V.ProjectionRow> projections, IEnumerable<V.BalanceRow> balances,
        IEnumerable<V.LedgerRow> ledger, IEnumerable<V.BaselineRow>? baselines = null) =>
        new([new V.LicenseRow(Lic, Issued, poolSince)],
            projections.ToList(),
            balances.ToList(),
            ledger.ToList(),
            new List<V.DrawRow>(),
            new HashSet<Guid>(),
            (baselines ?? Array.Empty<V.BaselineRow>()).ToList(),
            new List<V.ActivationRow>(),
            ProjectionCount: 0, TransactionCount: 0);

    private static V.LicenseReport Single(V.Snapshot s) => V.Analyze(s).Licenses.Should().ContainSingle().Subject;

    private static V.BaselineRow Marker() => new(Lic, V.LicenseMarker, 0m, 0m, Captured, null);

    /// <summary>Kişi {A = 10, B = <paramref name="balanceOfB"/>} (aynı üretilmiş numara); defter bakiyeyle tutarlı
    /// (D1 yok).</summary>
    private static (Guid A, Guid B, V.ProjectionRow[] Projections, V.BalanceRow[] Balances, V.LedgerRow[] Ledger)
        Person(decimal balanceOfB = -60m)
    {
        var g = $"g{Guid.NewGuid():N}";
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        return (a, b,
            new V.ProjectionRow[] { new(a, Lic, g, null, false), new(b, Lic, g, null, false) },
            new V.BalanceRow[] { new(Lic, a, 10m), new(Lic, b, balanceOfB) },
            new V.LedgerRow[] { new(Lic, a, 10m, 1), new(Lic, b, balanceOfB, 1) });
    }

    [Fact]
    public void Havuz_kipinde_aciklanamayan_eksi_kisi_D3_uyarisi()
    {
        var p = Person();

        var r = Single(Snap(PoolSince, p.Projections, p.Balances, p.Ledger));

        (r.NegativePersons, r.NegativePersonsExplained).Should().Be((1, 0), "10 − 60 < 0, eksi satır tabanda değil");
        r.Alerts.Should().Contain(a => a.StartsWith("D3: havuzu < 0 kişi 1", StringComparison.Ordinal));
        r.Alerts.Should().Contain(D3Line);
    }

    [Fact]
    public void Satir_kipinde_yalniz_sayi_uyari_yok()
    {
        var p = Person();

        var r = Single(Snap(poolSince: null, p.Projections, p.Balances, p.Ledger));

        (r.NegativePersons, r.NegativePersonsExplained).Should().Be((1, 0));
        r.Alerts.Should().NotContain(a => a.StartsWith("D3", StringComparison.Ordinal),
            "§8.1: D3 uyarısı yalnız havuz kipindeki lisansta; satır kipinde 6. madde yalnız sayı");
        r.Alerts.Should().BeEmpty("taban da yok: eksi satır yalnız sayılır");
    }

    [Fact]
    public void Tabanla_aciklanan_eksi_kisi_uyari_degil()
    {
        var same = Person(-60m);
        var accepted = Single(Snap(PoolSince, same.Projections, same.Balances, same.Ledger,
            [Marker(), new V.BaselineRow(Lic, same.B, -60m, -60m, Captured, Captured)]));

        (accepted.NegativePersons, accepted.NegativePersonsExplained).Should().Be((1, 1),
            "eksi satır tabanda ve tabandaki değerinde");
        accepted.Alerts.Should().BeEmpty("açıklanan eksi kişi D3 değildir; satır da taban içi");

        var worse = Person(-70m);
        var r = Single(Snap(PoolSince, worse.Projections, worse.Balances, worse.Ledger,
            [Marker(), new V.BaselineRow(Lic, worse.B, -60m, -60m, Captured, Captured)]));

        (r.NegativePersons, r.NegativePersonsExplained).Should().Be((1, 0), "eksi satır tabandakinin altına indi");
        r.Alerts.Should().Equal("eksi: taban dışı 0, daha da eksiye inen 1", D3Line);
    }

    [Fact]
    public void Taban_yokken_de_D3_uyarisi()
    {
        var p = Person();
        // Taban varken eksi, D1 ve kopya kalemlerinin üçünü de tetikleyecek satırlar: B eksi, broken'da
        // Balance ≠ Σ, copy kopyada bakiye.
        var (broken, head, copy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var projections = p.Projections.Concat(new V.ProjectionRow[]
        {
            new(broken, Lic, null, null, false), new(head, Lic, null, null, false), new(copy, Lic, null, head, false),
        });
        var balances = p.Balances.Concat(new V.BalanceRow[] { new(Lic, broken, 50m), new(Lic, copy, 3m) });
        var ledger = p.Ledger.Concat(new V.LedgerRow[] { new(Lic, broken, 40m, 1), new(Lic, copy, 3m, 1) });

        var r = Single(Snap(PoolSince, projections, balances, ledger));

        r.HasBaseline.Should().BeFalse();
        (r.NegativeRows.Outside, r.LedgerMismatches.Outside, r.CopyRows.Outside).Should().Be((1, 1, 1));
        r.Alerts.Should().Equal(new[] { D3Line },
            "plan S2 fark 16: D3 tabandan bağımsız; eksi/D1/kopya kalemleri taban yokken yalnız sayı");
    }
}
```

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifyJobTests.cs` — `Taban_yokken_eksi_satir_uyarmaz` testinden (`:136-147`) SONRA, `Sapma_yoksa_e_posta_gitmez`'ten önce ekle:

```csharp
    [Fact]
    public async Task Havuz_kipindeki_lisansin_D3_uyarisi_e_postaya_girer_kisisel_veri_yok()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await kit.EnablePoolAsync();
        // İşaret yok: D3 tabandan bağımsız uyarır (plan S2 fark 16); eksi satır kalemi taban yokken yalnız sayı.
        var group = Kit.NewGroup();
        var username = $"u{Guid.NewGuid():N}";
        await kit.MemberAsync(group, 10m);
        var negative = await kit.MemberAsync(group, -60m, username: username);

        var (email, report) = await RunJobAsync(factory);

        var licenseReport = report.Licenses.Single(l => l.LicenseId == kit.LicenseId);
        licenseReport.HasBaseline.Should().BeFalse();
        licenseReport.Alerts.Should().Equal("D3: havuzu < 0 kişi 1 (tabanla açıklanamayan, havuz kipi)");
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.ToEmail.Should().Be(AlertTo);
        sent.PlainBody.Should().Contain(kit.LicenseId.ToString()).And.Contain("D3: havuzu < 0 kişi 1");
        sent.HtmlBody.Should().Contain(kit.LicenseId.ToString()).And.Contain("D3: havuzu &lt; 0 kişi 1");
        // Gövdede yalnız lisans Id'si ve sayı (§8.1): üyenin kullanıcı adı, satır Id'si ve numarası girmez.
        foreach (var body in new[] { sent.PlainBody, sent.HtmlBody })
            body.ToLowerInvariant().Should().NotContainAny(
                username, negative.ToString("D"), negative.ToString("N"), group.ToLowerInvariant());
    }
```

(`Kit.EnablePoolAsync` Görev 1'in; lisansın `PersonBalancePoolSince`'ini yazar. Sınıfın `RunJobAsync`'i, `AlertTo`'su ve `using`'leri yeterli; ekleme yok.)

- [ ] **Adım 2: Koştur — kırmızı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceD3AlertTests|FullyQualifiedName~PersonBalanceVerifyJobTests"`
Expected: FAIL — derleme hatası: `PersonBalanceVerifier.LicenseRow` üç argümanlı kurucu almıyor (CS1729, `PersonBalanceD3AlertTests.Snap`). (Derleme düzeltilip verifier değişmeseydi yeni iş testi de `Alerts` boş diye düşerdi: kip okunmuyor.)

- [ ] **Adım 3: Verifier — kip bayrağı ve D3 satırı**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifier.cs`:

Sınıf belgesinde (`:18-22`) şu blok:

```csharp
/// <para><b>Uyarı (§8.1) yalnız YENİ sapmada:</b> D4/D5/D6/yetim ≠ 0 (her zaman); lisansın tabanı varken taban dışı
/// eksi satır ya da tabandaki bir satırın (kopya dahil) tabandakinin altında eksi olması, taban dışı D1 ya da
/// tabandaki satırın <c>Balance − LedgerSum</c> farkının değişmesi, taban dışı kopya satır. Havuz kipindeki D3
/// uyarısı S2'de (S1 kip bayrağını okumaz); 6. madde yalnız sayı. 4. maddenin "grubunda canlı asıl üye kalmamış"
```

şununla değişir:

```csharp
/// <para><b>Uyarı (§8.1) yalnız YENİ sapmada:</b> D4/D5/D6/yetim ≠ 0 (her zaman); lisansın tabanı varken taban dışı
/// eksi satır ya da tabandaki bir satırın (kopya dahil) tabandakinin altında eksi olması, taban dışı D1 ya da
/// tabandaki satırın <c>Balance − LedgerSum</c> farkının değişmesi, taban dışı kopya satır; havuz kipindeki lisansta
/// (<see cref="LicenseRow.PoolSince"/> dolu) taban satırlarıyla açıklanamayan havuzu &lt; 0 kişi (D3'ün izi,
/// tabandan bağımsız — plan S2 fark 16). Satır kipinde 6. madde yalnız sayı. 4. maddenin "grubunda canlı asıl üye kalmamış"
```

`ActivePcWindow` (`:30-31`):

```csharp
    /// <summary>Aktif PC penceresi: satış senkronu kapısıyla aynı ölçüt (14 gün). Ayarı S2'nin <c>enable</c>'ı getirir.</summary>
    public static readonly TimeSpan ActivePcWindow = SalesModeGate.RecentWindow;
```

şununla değişir:

```csharp
    /// <summary>Aktif PC penceresi (9. madde): satış senkronu kapısıyla aynı ölçüt, sabit 14 gün. §7.3-9 pencere
    /// tanımlamaz; <c>PersonBalance:ActivePcWindowDays</c> yalnız <c>enable</c>'ın 1. koşulunu
    /// (<see cref="PersonBalanceEnableGate"/>) besler, bu raporu değil (plan S2 fark 6).</summary>
    public static readonly TimeSpan ActivePcWindow = SalesModeGate.RecentWindow;
```

`LicenseRow` (`:42`):

```csharp
    public sealed record LicenseRow(Guid Id, DateTimeOffset IssuedAt);
```

şununla değişir:

```csharp
    /// <param name="PoolSince">Kip bayrağı (<c>License.PersonBalancePoolSince</c>): dolu = havuz kipi, D3 uyarısı
    /// açılır. Varsayılan null = satır kipi (S1'in testleri iki argümanla kurar).</param>
    public sealed record LicenseRow(Guid Id, DateTimeOffset IssuedAt, DateTimeOffset? PoolSince = null);
```

`LoadAsync` (`:74-76`):

```csharp
        var licenses = await _db.Licenses.AsNoTracking()
            .Where(l => licenseId == null || l.Id == licenseId)
            .Select(l => new LicenseRow(l.Id, l.IssuedAt)).ToListAsync(ct);
```

şununla değişir:

```csharp
        var licenses = await _db.Licenses.AsNoTracking()
            .Where(l => licenseId == null || l.Id == licenseId)
            .Select(l => new LicenseRow(l.Id, l.IssuedAt, l.PersonBalancePoolSince)).ToListAsync(ct);
```

`AnalyzeLicense`'ın uyarı listesinin taban bloğu (`:381-389`):

```csharp
        if (hasBaseline)
        {
            if (negOut.Count + worsened.Count > 0)
                alerts.Add($"eksi: taban dışı {negOut.Count}, daha da eksiye inen {worsened.Count}");
            if (misOut.Count + changedMismatch.Count > 0)
                alerts.Add($"D1: taban dışı {misOut.Count}, farkı değişen {changedMismatch.Count}");
            if (copyOut.Count > 0)
                alerts.Add($"kopya: taban dışı {copyOut.Count}");
        }
```

şununla değişir (blok aynen; arkasına D3):

```csharp
        if (hasBaseline)
        {
            if (negOut.Count + worsened.Count > 0)
                alerts.Add($"eksi: taban dışı {negOut.Count}, daha da eksiye inen {worsened.Count}");
            if (misOut.Count + changedMismatch.Count > 0)
                alerts.Add($"D1: taban dışı {misOut.Count}, farkı değişen {changedMismatch.Count}");
            if (copyOut.Count > 0)
                alerts.Add($"kopya: taban dışı {copyOut.Count}");
        }
        // D3'ün izi (§8.1 son madde): havuz kipindeki lisansta taban satırlarıyla açıklanamayan havuzu < 0 kişi
        // (açıklanabilir = 6. maddenin "explained"ı). Tabandan bağımsız (plan S2 fark 16): "taban yoksa yalnız sayı"
        // istisnası eksi/D1/kopya kalemleri içindir; havuz kipi tabanı olan lisansta açılır (enable 3. koşulu) ve orada
        // açıklanamayan eksi kişi çalışma anı kuralının (kişi kilidi) bozulmasıdır. Satır kipinde yalnız sayı.
        if (license.PoolSince is not null && negativePersons.Count > explained)
            alerts.Add($"D3: havuzu < 0 kişi {negativePersons.Count - explained} (tabanla açıklanamayan, havuz kipi)");
```

(`license` `AnalyzeLicense`'ın parametresidir, `:301`; `negativePersons` ve `explained` `:359-361`'de tanımlı. `PersonBalanceEnableGate` Görev 11'in sınıfıdır, aynı ad alanında: `cref` derlenir. `PersonBalanceVerifyJob`, `PersonBalanceCommand` ve `Analyze`'ın imzası değişmez.)

- [ ] **Adım 4: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceD3AlertTests|FullyQualifiedName~PersonBalanceVerifyJobTests"`
Expected: PASS — `PersonBalanceD3AlertTests` 4/4, `PersonBalanceVerifyJobTests` hepsi (yeni test dahil).

Regresyon (Servis/CLI kümesi + `LoadAsync`'i okuyan S2 sınıfları; satır kipindeki lisansların raporu ve uyarı metinleri birebir aynı kalmalı):

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceMembers|FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalanceVerifier|FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~PersonBalanceCommandTests|FullyQualifiedName~PersonBalanceEnableGateTests|FullyQualifiedName~PersonBalanceToggleCommandTests"`
Expected: hepsi PASS, değişmeden — özellikle `Taban_hic_alinmamisken_eksi_D1_ve_kopya_yalniz_sayilir_uyarmaz`, `Havuzu_eksi_kisi_butun_eksi_satirlari_tabanda_ve_kotulesmemisse_aciklanir`, `Kisi_kalemleri_yayilmis_havuz_silinmis_kirpma_eksi_kisi_ve_surumler` (bayrak boş: `NegativePersons` yalnız sayı) ve `Taban_yokken_eksi_satir_uyarmaz`. `PersonBalanceVerifierLoadTests` (yeni projeksiyon) ve `PersonBalanceVerifierRelationalTests` (SQL Server çevirisi) koşar; Docker gerekir (`PersonBalanceMembersRelationalTests` de). Docker kapalıysa "Genel kurallar"daki `DOCKER_HOST` adımı, o da olmazsa atlandı diye not düş (Görev 17'nin tam paketi yeniden koşar).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifier.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceD3AlertTests.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifyJobTests.cs
git commit -F - <<'MSG'
feat(balance): havuz kipinde açıklanamayan eksi kişiyi D3 olarak uyar

Denetim artık lisansın kip bayrağını okur. Havuz kipindeki lisansta taban
satırlarıyla açıklanamayan havuzu < 0 kişi günlük uyarıya "D3" satırı olarak
girer; tabandan bağımsızdır, çünkü taban istisnası yalnız eksi/D1/kopya
kalemleri içindir ve havuz kipi zaten tabanı olan lisansta açılır (ortak
bakiye §8.1). Satır kipinde sayı yalnız günlüğe yazılır. Günlük iş ve
e-posta biçimi değişmez; gövdede yalnız lisans Id'si ve sayı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### B — Dört metrik

- [ ] **Adım 6: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceMetricsTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye metrikleri — spec 2026-10-11 §8.6, §8.5 ("metrikte <c>client-update-required</c> görünür"), plan S2
/// fark 18h: <c>customer-not-synced</c>, <c>client-update-required</c> (<c>gate</c> + <c>endpoint</c>),
/// <c>balance-busy</c>, çok bacaklı çekim (<c>legs</c>). Sayaçlar BCL <see cref="MeterListener"/>'la okunur; dinleyici
/// yalnız bu fabrikanın <see cref="OrderDeckMetrics"/> örneğinin aletlerini açar (paralel sınıfların fabrikaları aynı
/// <see cref="OrderDeckMetrics.MeterName"/>'i paylaşır). <c>balance-busy</c> InMemory'de kurulamaz (üç ardışık jeton
/// çakışması): <c>endpoint=apply</c> Görev 14'ün, <c>endpoint=panel/manual-adjustment</c> Görev 16'nın ilişkisel
/// 503 testinde doğrulanır; <c>panel/reverse</c> ve WPF <c>reverse</c> etiketlerinin otomatik testi yoktur.
/// </summary>
public sealed class PersonBalanceMetricsTests : IClassFixture<PersonBalanceConfiguredApiFactory>
{
    private readonly PersonBalanceConfiguredApiFactory _factory;
    private readonly OrderDeckMetrics _metrics;

    public PersonBalanceMetricsTests(PersonBalanceConfiguredApiFactory factory)
    {
        _factory = factory;
        _metrics = factory.Services.GetRequiredService<OrderDeckMetrics>();
    }

    /// <param name="Tags">"ad=değer", ordinal sıralı.</param>
    private sealed record Olcum(long Value, IReadOnlyList<string> Tags);

    /// <summary>Tek bir sayacın ölçümleri. Ölçüm isteğin içinde, yanıttan önce yazılır.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Olcum> _items = [];

        public Recorder(Instrument watched)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, watched)) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var formatted = new List<string>();
                foreach (var tag in tags) formatted.Add($"{tag.Key}={tag.Value}");
                formatted.Sort(StringComparer.Ordinal);
                lock (_items) _items.Add(new Olcum(value, formatted));
            });
            _listener.Start();
        }

        public IReadOnlyList<Olcum> Items
        {
            get { lock (_items) return _items.ToList(); }
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task customer_not_synced_preview_te_sayilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        await kit.MemberAsync(g, 20m);   // S∖W'de artı üye: kural 3b
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceCustomerNotSynced);

        var response = await kit.PreviewAsync(a, [a]);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(response)).Should().Be(PersonBalance.CodeCustomerNotSynced);
        var olcum = recorder.Items.Should().ContainSingle().Subject;
        olcum.Value.Should().Be(1);
        olcum.Tags.Should().Equal("endpoint=preview");
    }

    [Fact]
    public async Task client_update_required_havuz_kapisi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 10m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceClientUpdateRequired);

        var response = await kit.PreviewAsync(a, [a], Kit.OldUserAgent);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(response)).Should().Be("client-update-required");
        recorder.Items.Should().ContainSingle("mod Off: satış kapısı koşmaz")
            .Which.Tags.Should().Equal("endpoint=preview", "gate=person-balance");
    }

    [Fact]
    public async Task client_update_required_satis_kapisi()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var a = await kit.MemberAsync(null, 10m);
        using var recorder = new Recorder(_metrics.BalanceClientUpdateRequired);

        var preview = await kit.PreviewAsync(a, null, Kit.OldUserAgent);
        // v0.9.10 anahtarsız işte önce scope'u sorar (R6-01); onun 409'u da sayılır.
        var scope = await kit.SendAsync(HttpMethod.Get,
            $"/api/v1/licenses/{kit.LicenseId}/customer-balance/scope?wpfCustomerId={a}&saleScope={Uri.EscapeDataString(Kit.Session42)}",
            userAgent: Kit.OldUserAgent);

        foreach (var response in new[] { preview, scope })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Kit.TitleAsync(response)).Should().Be("client-update-required");
        }
        recorder.Items.Select(m => string.Join(",", m.Tags)).Should().Equal(
            "endpoint=preview,gate=sales-sync", "endpoint=scope,gate=sales-sync");
    }

    /// <summary>
    /// Plan farkı 3–4: havuz kipi + mod On'da satış kapısı havuz kapısından önce keser. Fabrikada iki alt sınır da
    /// 0.9.11 ve yanıt iki kapıda aynı; 409'un hangi kapıdan geldiğini yalnız <c>gate</c> etiketi ayırt eder
    /// (§8.5'in v0.9.9 denemesi, mod On'da <c>gate="sales-sync"</c> bekler).
    /// </summary>
    [Fact]
    public async Task client_update_required_havuz_kipinde_mod_On_once_satis_kapisi()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var a = await kit.MemberAsync(null, 10m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceClientUpdateRequired);

        var preview = await kit.PreviewAsync(a, [a], Kit.OldUserAgent);
        var apply = await kit.ApplyAsync(a, 5m, 5m, Guid.NewGuid(), null, [a], Kit.OldUserAgent);

        foreach (var response in new[] { preview, apply })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Kit.TitleAsync(response)).Should().Be("client-update-required");
        }
        recorder.Items.Select(m => string.Join(",", m.Tags)).Should().Equal(
            "endpoint=preview,gate=sales-sync", "endpoint=apply,gate=sales-sync");
    }

    [Fact]
    public async Task cok_bacakli_cekim_bacak_sayisiyla()
    {
        // Spec Örnek 1: T (sahip, 0), I 100, Y 30; 120 → K (T, 0), (I, −100), (Y, −20): çapa dahil üç satır.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m);
        var y = await kit.MemberAsync(g, 30m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceMultiLegDraws);

        var response = await kit.ApplyAsync(t, 120m, 120m, Guid.NewGuid(), Kit.Session42, [t, i, y]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var olcum = recorder.Items.Should().ContainSingle().Subject;
        (olcum.Value, string.Join(",", olcum.Tags)).Should().Be((1L, "legs=3"));
    }

    [Fact]
    public async Task tek_bacakli_cekim_sayilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceMultiLegDraws);

        var response = await kit.ApplyAsync(a, 30m, 30m, Guid.NewGuid(), null, [a]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.Items.Should().BeEmpty("yalnız çapaya yazılan çekim çok bacaklı değildir");
    }

    [Fact]
    public async Task panel_elle_eksi_cok_bacakli_cekim_bacak_sayisiyla()
    {
        // Spec §4.8 (K5) örneği, Görev 9'un K5_ornegi_A10_B40_rota_A_eksi30'u: çapa (A, −10), bacak (B, −20).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceMultiLegDraws);

        var response = await kit.PanelManualAdjustmentAsync(a, -30m, $"r-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var olcum = recorder.Items.Should().ContainSingle().Subject;
        (olcum.Value, string.Join(",", olcum.Tags)).Should().Be((1L, "legs=2"));
    }

    [Fact]
    public async Task panel_iade_iptali_cok_bacakli_cekim_bacak_sayisiyla()
    {
        // Spec Örnek 4, Görev 10'un Ornek4_baska_uyenin_iadesini_iptal'i: A'ya iade R (+80), eski düşüm A'dan 50
        // → A = 30; B = 60. B'den R'nin iptali: çapa (A, −30), bacak (B, −50).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var refund = await kit.FundAsync(a, 80m);
        await kit.SeedLegacyDeductionAsync(a, 50m, 50m);
        var b = await kit.MemberAsync(g, 60m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceMultiLegDraws);

        var response = await kit.PanelReverseAsync(b, refund);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var olcum = recorder.Items.Should().ContainSingle().Subject;
        (olcum.Value, string.Join(",", olcum.Tags)).Should().Be((1L, "legs=2"));
    }
}
```

(İki panel testi `DrawManualAdjustmentAsync` ve `ReverseInPoolAsync`'teki `CountMultiLegDraw` çağrılarını ayrı ayrı kanıtlar: çağrı unutulur ya da `return result.Status switch`'ten sonraya kayarsa ilgili test boş kayıtla düşer. Panel `BalanceBusy`'nin `panel/` öneki InMemory'de kurulamaz: Görev 14 `endpoint=apply`'ı, Görev 16'nın `K5_uc_denemede_de_cakisirsa_503_balance_busy_hicbir_sey_yazmaz` testi `panel/manual-adjustment`'ı `MeterListener`'la ölçer; `panel/reverse` testsizdir.)

- [ ] **Adım 7: Koştur — kırmızı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceMetricsTests"`
Expected: FAIL — derleme hatası: `OrderDeckMetrics` `BalanceCustomerNotSynced`, `BalanceClientUpdateRequired` ve `BalanceMultiLegDraws` içermiyor (CS1061).

- [ ] **Adım 8: `OrderDeckMetrics` — dört sayaç**

`OrderDeck.LicenseServer/Services/Observability/OrderDeckMetrics.cs`, kurucunun sonu (`:50-53`):

```csharp
        LicenseTelemetryTouchFailures = _meter.CreateCounter<long>(
            "orderdeck.license.telemetry_touch_failures_total",
            description: "LastSeen/AppVersion touches from /validate that failed and were swallowed (R9-OPS03).");
    }
```

şununla değişir:

```csharp
        LicenseTelemetryTouchFailures = _meter.CreateCounter<long>(
            "orderdeck.license.telemetry_touch_failures_total",
            description: "LastSeen/AppVersion touches from /validate that failed and were swallowed (R9-OPS03).");
        BalanceCustomerNotSynced = _meter.CreateCounter<long>(
            "orderdeck.balance.customer_not_synced_total",
            description: "Person-pool balance requests rejected with 409 customer-not-synced, tagged by endpoint.");
        BalanceClientUpdateRequired = _meter.CreateCounter<long>(
            "orderdeck.balance.client_update_required_total",
            description: "Balance requests rejected with 409 client-update-required, tagged by gate (sales-sync/person-balance) and endpoint.");
        BalanceBusy = _meter.CreateCounter<long>(
            "orderdeck.balance.busy_total",
            description: "Balance requests that returned 503 balance-busy, tagged by endpoint: a write exhausted its retries, or a read outside the retried attempt was chosen as a deadlock victim. Nothing was written in either case.");
        BalanceMultiLegDraws = _meter.CreateCounter<long>(
            "orderdeck.balance.multi_leg_draws_total",
            description: "Person-pool draws written across more than one balance row, tagged by legs (rows written, anchor included).");
    }
```

Özelliklerin sonu (`:62-63`):

```csharp
    public Counter<long> LicenseTelemetryTouchFailures { get; }
}
```

şununla değişir:

```csharp
    public Counter<long> LicenseTelemetryTouchFailures { get; }
    public Counter<long> BalanceCustomerNotSynced { get; }
    public Counter<long> BalanceClientUpdateRequired { get; }
    public Counter<long> BalanceBusy { get; }
    public Counter<long> BalanceMultiLegDraws { get; }
}
```

- [ ] **Adım 9: WPF denetleyicisi — sayaçlar**

`OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs`:

`using`'ler (`:8-9`):

```csharp
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
```

şununla değişir:

```csharp
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Sync;
```

Alan (Görev 5'in eklediği satır):

```csharp
    private readonly PersonBalanceOptions _personBalance;
```

şununla değişir:

```csharp
    private readonly PersonBalanceOptions _personBalance;
    private readonly OrderDeckMetrics _metrics;
```

Kurucunun son parametresi (dosyada tek geçiş):

```csharp
        ILogger<LicensesCustomerBalanceApplyController> log)
```

şununla değişir:

```csharp
        OrderDeckMetrics metrics, ILogger<LicensesCustomerBalanceApplyController> log)
```

Kurucu gövdesinde (Görev 5'in atadığı satır):

```csharp
        _personBalance = personBalance.Value;
```

şununla değişir:

```csharp
        _personBalance = personBalance.Value;
        _metrics = metrics;
```

Kurucunun son hâli: `(LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person, IOptions<SalesSyncOptions> salesSync, IOptions<PersonBalanceOptions> personBalance, OrderDeckMetrics metrics, ILogger<LicensesCustomerBalanceApplyController> log)`. `OrderDeckMetrics` singleton (`Program.cs:901`); denetleyici DI'dan kurulur, testlerde elle kurulan yer yok.

`ClientUpdateRequired` (satış senkronu kapısı; master `:727-731`):

```csharp
    private ObjectResult ClientUpdateRequired(Guid licenseId, string endpoint)
    {
        _log.LogInformation(
            "customer-balance/{Endpoint}: mod On'da istemci alt sürümün altında ya da alt sürüm tanımsız, "
```

şununla değişir:

```csharp
    private ObjectResult ClientUpdateRequired(Guid licenseId, string endpoint)
    {
        // §8.5-8.6: endpoint preview | scope | apply | reverse (scope: v0.9.10 önce onu sorar, R6-01).
        _metrics.BalanceClientUpdateRequired.Add(1,
            new KeyValuePair<string, object?>("gate", "sales-sync"),
            new KeyValuePair<string, object?>("endpoint", endpoint));
        _log.LogInformation(
            "customer-balance/{Endpoint}: mod On'da istemci alt sürümün altında ya da alt sürüm tanımsız, "
```

`PoolClientUpdateRequired` (Görev 5; havuz kapısı, yalnız preview ve apply) — imza satırı ve açılış parantezi:

```csharp
    private ObjectResult PoolClientUpdateRequired(Guid licenseId, string endpoint)
    {
```

şununla değişir (gövdenin geri kalanı — Error/Information günlüğü ve 409 — aynen):

```csharp
    private ObjectResult PoolClientUpdateRequired(Guid licenseId, string endpoint)
    {
        _metrics.BalanceClientUpdateRequired.Add(1,
            new KeyValuePair<string, object?>("gate", "person-balance"),
            new KeyValuePair<string, object?>("endpoint", endpoint));
```

`CustomerNotSynced` (Görev 5; preview ve apply çağırır) — imza satırı ve açılış parantezi:

```csharp
    private ObjectResult CustomerNotSynced(Guid licenseId, string endpoint, IReadOnlyList<Guid> rowIds)
    {
```

şununla değişir (gövde aynen):

```csharp
    private ObjectResult CustomerNotSynced(Guid licenseId, string endpoint, IReadOnlyList<Guid> rowIds)
    {
        _metrics.BalanceCustomerNotSynced.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint));
```

`BalanceBusy` (master `:686-688`; havuz apply'ı `"apply"`, reverse `"reverse"` ile çağırır):

```csharp
    private ObjectResult BalanceBusy(Guid licenseId, string endpoint)
    {
        // Uyarı servisin (neden ve çapayla); burada yalnız hangi ucun 503 döndüğü.
```

şununla değişir:

```csharp
    private ObjectResult BalanceBusy(Guid licenseId, string endpoint)
    {
        _metrics.BalanceBusy.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint));
        // Uyarı servisin (neden ve çapayla); burada yalnız hangi ucun 503 döndüğü.
```

`BalanceBusy`'nin kapanış `}`'inden sonra, `ScopeLockResource`'un belge yorumundan (`/// <summary>` "Kapsam kuralının kilidi") önce yeni yardımcı:

```csharp
    /// <summary>
    /// Çok bacaklı çekim sayacı (spec §8.6, plan S2 fark 18h): yalnız yazılmış (<c>Drawn</c>) ve birden çok satıra
    /// dokunan çekim; etiket <c>legs</c> çapa dahil yazılan hareket satırı sayısıdır. Yalnız çapaya yazılan çekim
    /// sayılmaz; <c>Drawn</c> dışındaki sonuçlarda <c>Legs</c> zaten 0.
    /// </summary>
    private void CountMultiLegDraw(PersonBalance.DrawResult result)
    {
        if (result.Status == PersonBalance.DrawStatus.Drawn && result.Legs > 1)
            _metrics.BalanceMultiLegDraws.Add(1, new KeyValuePair<string, object?>("legs", result.Legs));
    }
```

`ApplyPoolAsync` (Görev 7): `_person.DrawAsync(request, BeforeAttempt, ct)`'yi saran `try { … } catch (SalesCommandRejection rejection) { … }` bloğunun kapanış `}`'inden hemen sonra, `result.Status` eşlemesinden ÖNCE şu iki satırı ekle (eşleme ve dönüşler aynen):

```csharp
        // §8.6: çok bacaklı çekim (yalnız Drawn ve Legs > 1); yanıt değişmez.
        CountMultiLegDraw(result);
```

(`catch` dalı her yolda `return` eder; bu noktada `result` kesin atanmıştır. `DuplicateKey` oynatması `Legs = 0` taşır, sayılmaz: kazananın çekimi kendi isteğinde sayıldı.)

- [ ] **Adım 10: Panel denetleyicisi — sayaçlar**

`OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs`:

`using`:

```csharp
using OrderDeck.LicenseServer.Services.CustomerSync;
```

şununla değişir:

```csharp
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
```

Alanlar ve kurucu (`:32-45`; Görev 8–10 kurucuya dokunmaz):

```csharp
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly ILogger<PanelCustomerBalanceController> _log;

    public PanelCustomerBalanceController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        ILogger<PanelCustomerBalanceController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _log = log;
    }
```

şununla değişir:

```csharp
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly OrderDeckMetrics _metrics;
    private readonly ILogger<PanelCustomerBalanceController> _log;

    public PanelCustomerBalanceController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        OrderDeckMetrics metrics, ILogger<PanelCustomerBalanceController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _metrics = metrics;
        _log = log;
    }
```

`BalanceBusy` (Görev 9; çağıranlar `"manual-adjustment"` ve `"reverse"` geçer):

```csharp
    private ObjectResult BalanceBusy(Guid licenseId, string endpoint)
    {
        _log.LogDebug(
            "panel balance/{Endpoint}: 503 balance-busy (license={LicenseId})", endpoint, licenseId);
```

şununla değişir (günlük satırı ve 503 aynen):

```csharp
    private ObjectResult BalanceBusy(Guid licenseId, string endpoint)
    {
        // §8.6: panel uçları WPF'inkilerden "panel/" önekiyle ayrılır.
        _metrics.BalanceBusy.Add(1, new KeyValuePair<string, object?>("endpoint", $"panel/{endpoint}"));
        _log.LogDebug(
            "panel balance/{Endpoint}: 503 balance-busy (license={LicenseId})", endpoint, licenseId);
```

`BalanceBusy`'nin kapanış `}`'inden sonra yeni yardımcı (WPF denetleyicisindekiyle aynı kural):

```csharp
    /// <summary>
    /// Çok bacaklı çekim sayacı (spec §8.6, plan S2 fark 18h): yalnız yazılmış (<c>Drawn</c>) ve birden çok satıra
    /// dokunan çekim; etiket <c>legs</c> çapa dahil yazılan hareket satırı sayısıdır. Yalnız çapaya yazılan çekim
    /// sayılmaz.
    /// </summary>
    private void CountMultiLegDraw(PersonBalance.DrawResult result)
    {
        if (result.Status == PersonBalance.DrawStatus.Drawn && result.Legs > 1)
            _metrics.BalanceMultiLegDraws.Add(1, new KeyValuePair<string, object?>("legs", result.Legs));
    }
```

`DrawManualAdjustmentAsync` (Görev 9):

```csharp
            ScopeLockResource: null), beforeAttempt: null, ct);

        return result.Status switch
        {
            PersonBalance.DrawStatus.Drawn => Ok(),
            PersonBalance.DrawStatus.Insufficient => Problem(title: "insufficient-balance", statusCode: 409),
            PersonBalance.DrawStatus.Busy => BalanceBusy(licenseId, "manual-adjustment"),
```

şununla değişir:

```csharp
            ScopeLockResource: null), beforeAttempt: null, ct);

        CountMultiLegDraw(result);
        return result.Status switch
        {
            PersonBalance.DrawStatus.Drawn => Ok(),
            PersonBalance.DrawStatus.Insufficient => Problem(title: "insufficient-balance", statusCode: 409),
            PersonBalance.DrawStatus.Busy => BalanceBusy(licenseId, "manual-adjustment"),
```

`ReverseInPoolAsync` (Görev 10; artı hareketin iptali, `DrawAsync` + `Kind = reversal`): `var result = await _person.DrawAsync(…)` ifadesinin bittiği satırdan hemen sonra, `result.Status` eşlemesinden ÖNCE ekle (eşleme aynen; `ReverseGroupAsync`'e giden eksi hedef kolu bir çekim değildir, sayılmaz):

```csharp
        // §8.6: çok bacaklı iptal çekimi (yalnız Drawn ve Legs > 1); yanıt değişmez.
        CountMultiLegDraw(result);
```

- [ ] **Adım 11: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceD3AlertTests|FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~PersonBalanceMetricsTests"`
Expected: PASS — `PersonBalanceMetricsTests` 8/8 (WPF altı, panel iki), A parçasının testleri yeşil kalır. `client_update_required_havuz_kipinde_mod_On_once_satis_kapisi` kırmızı kalır ve `gate=person-balance` ölçümü görürse Görev 5'in `Preview`'unda ya da Görev 7'nin `Apply`'ında havuz dalı satış kapısından önce konmuştur (fark 3–4: önce satış kapısı, sonra havuz kapısı); kapı sırası orada düzeltilir.

Regresyon (yanıtlar ve satır kipi DEĞİŞMEZ; yalnız sayaç ve uyarı):

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceMembersTests|FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalanceVerifierTests|FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~PersonBalanceCommandTests"`
Expected: hepsi PASS (Servis/CLI).

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRuleTests|FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceGroupReversalTests|FullyQualifiedName~PersonBalanceScopeRuleSumTests"`
Expected: hepsi PASS, değişmeden (WPF; özellikle satış senkronu kapısının 409 testleri ve grup geri almasının 503'ü — sayaç yalnız gözlem).

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PanelCustomerBalanceControllerTests|FullyQualifiedName~PersonBalancePanelListTests|FullyQualifiedName~PersonBalancePanelReverseTests|FullyQualifiedName~PersonBalanceShopperListTests|FullyQualifiedName~ShopperBalanceControllerTests"`
Expected: hepsi PASS (Panel/Shopper; panel kurucusunun yeni parametresi DI'dan gelir).

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePoolPreviewTests|FullyQualifiedName~PersonBalancePoolApplyTests|FullyQualifiedName~PersonBalancePoolManualAdjustmentTests|FullyQualifiedName~PersonBalancePoolReverseTests"`
Expected: hepsi PASS (S2 havuz sınıfları; yanıtlar, başlıklar ve günlük satırları aynı).

- [ ] **Adım 12: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Observability/OrderDeckMetrics.cs \
  OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs \
  OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceMetricsTests.cs
git commit -F - <<'MSG'
feat(balance): ortak bakiyenin dört sayacını ekle

customer-not-synced, client-update-required (satış ve havuz kapısı ayrı
etiketle), balance-busy ve çok bacaklı çekim (bacak sayısıyla) artık
/metrics'te görünür (ortak bakiye §8.6). Karışık filo denemesinde eski
sürümün kapıya takıldığı ve pilotta çekimlerin kaç satıra yayıldığı
günlüğe inmeden izlenir (§8.5). Sayaçlar 409/503'ü üreten yardımcılarda
artar; yanıtlar, günlük satırları ve satır kipinin davranışı değişmez.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 14: Testcontainers 1, 2, 10 + 503 + 1205 (apply yarışları) (spec §8.2-1, -2, -10; §4.2 adım 6; §4.13)

**Files:**
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawRaceTests.cs` (Testcontainers 1, 2, 10, 503, çapanın bakiye satırı açma yarışı, Görev 7 Not 6'nın no-balance öncesi son Lookup'ı)
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawDeadlockTests.cs` (1205; emsal `Controllers/Licenses/PersonBalanceReversalDeadlockTests.cs:85-145`)
- Kaynak dosyası değişmez.

> **30.10.2026 notu:** Görev uygulandıktan sonraki son inceleme `PersonBalanceDrawRaceTests`'e üç test ekledi (`Ilk_denemede_ayni_anahtarli_rakip_yazdiysa_customer_not_synced_yerine_oynatilir`, `Cekimden_sonraki_Lookup_kilitlenme_kurbani_olursa_503_balance_busy`, `On_oynatmanin_bakiye_okumasi_kilitlenme_kurbani_olursa_503_balance_busy`). Bu üçü denetleyici düzeltmesine (fark 18n'nin 30.10 eki) karşı önce kırmızı yazıldı; Görev 7'nin aşağıda anılan ilk koduyla kırmızı kalırlar. Sınıf artık 11 test; aşağıdaki "8" ve "8 + 2" sayıları ilk hâlin sayısıdır.

Kod değişikliği beklenmez: havuzlu apply'ın yarış davranışını Görev 4 (`DrawAsync`: kişi kilidi = `P`'nin satırlı üyelerine ve `S∖P` eksilerine jeton dokunuşu, `ChangeTracker.Clear()` ile baştan kurma, yakalama sırası `DbUpdateConcurrencyException` → `IsDuplicateReversal` → `IsDuplicateBalanceRow` → `IsDuplicateTransactionKey` → `IsDeadlock`, en çok `MaxAttempts` deneme, tek `LogWarning` + `Busy`), Görev 7 (`ApplyPoolAsync`: 2.+ denemede Lookup geri çağrısı, `DuplicateKey` → Lookup → kazananı oynat, `NoBalance` → anahtar varsa son bir Lookup (Görev 7 Not 6), `Busy` → 503) ve Görev 13 (`BalanceBusy` sayacı, `endpoint=apply`) kurar; bu görev onları gerçek SQL Server'da kanıtlar. InMemory'de jeton denetlenir ama `SaveChanges` atomik değil, tekil indeks ve komut kancası yok — bu yüzden hepsi burada. Yarışlar sırayla kurulur, zamanlamayla değil: kayıt öncesi `RaceSaveHook` (`Once(Func<DbContext,Task>)` aşırı yüklemesi kancanın HANGİ kayıtta tetiklendiğini kanıtlar; sayaç `ThrowingConcurrencyExceptionAsync`'te — fark 17), okuma öncesi `ReadQueryHook` (zincir: kayıt kancasının gövdesinden `Arm`). İki paralel test (TC1, TC2 sekizli) gerçek eşzamanlılığı sınar; belirlenimli kardeşleri dalı şansa bırakmaz (S1 emsali `PersonBalanceReversalRaceTests.cs:116-145`). Bir test kırmızı kalırsa önce sistematik hata ayıklama: `Expected` satırları hangi görevin sözleşmesine dayandığını söyler.

**Not (spec ↔ kod) — bu görev:**
1. **TC1'in sekizli paralel testinde 503 `balance-busy` de kabul edilir (talimattaki "yalnız 200 ya da 409" yerine).** Havuz 90, her istek 30 → en çok üç commit olur; her commit o an uçuşta olan bütün istekleri jeton çakışmasına düşürür. Üç commit'in üçünde de kaybeden bir istek üçüncü denemesini de yitirir ve §4.2-6 / §4.13 gereği 503 alır (kilitlenme de deneme harcar). Sekiz eşit yarışçıda bu olağan bir sonuçtur; yalnız 200/409 isteyen bir iddia doğru kodda da düşen (flaky) bir test olurdu. 503 hiçbir şey yazmaz; iddialar bunu ayrıca kanıtlar (çapa sayısı = 200 sayısı, havuz = 90 − Σ). Kabul edilmeyen: 500, `nothing-to-apply` (tutarlar 30'un katı), başka başlıklar.
2. **TC10'da tutar 230'dur (talimattaki 120 yerine); hatayı ayırt eden iddia yanıt değil, günlük sayısıdır.** 230, spec §4.2-6'nın cümlesini birebir kurar: "aynı anahtarlı başka bir istek araya girip havuzu **boşalttıysa** ikinci deneme `no-balance` değil kazananın sonucunu döner". X'in 1. denemesi `min(230, 130, 230) = 130` planlar (T 0, I −100, Y −30) ve iadenin (I +100) ilerlettiği jetona çarpar; rakip iadeden sonraki havuzun tamamını çeker (T 0, I −200, Y −30), havuz 0 olur. Grup 3 satırdır (talimatın "3 satır"ıyla uyumlu); bakiyeler T 0, I 0, Y 0. **Yanıt tek başına R33'ü kanıtlamaz:** 2. denemenin Lookup'ı koşup sonucu yok sayılsa da X 200 ve `(K, 230)` alır, çünkü Görev 7'nin `no-balance` öncesi son Lookup'ı (Görev 7 Not 6) Σ = 0'dan sonra K'yı bulup oynatır; 120'lik kurguda da `DuplicateKey` Lookup'ı aynı işi görürdü. Tutar bu yüzden ayırt edici değildir; 230 yalnız spec'in senaryosu olduğu için seçildi. Yanıtın kanıtladıkları: (1) 2. denemenin başında Lookup koşar (`rakipler` tek elemanlı; geri çağrı koşmasaydı okuma kancası tetiklenmez, X kendi 230'unu yazardı), (2) ikinci düşüm yazılmaz. "Sonuç yok sayılmaz"ı denetleyicinin oynatma günlüğü ayırır: `LookupAsync` her oynatmada bir `"tekrar oynatıldı"` Information satırı yazar (Görev 6, `LicensesCustomerBalanceApplyController`), çağıran sonucu kullansa da kullanmasa da. Doğru kodda X için tek satır vardır (2. denemenin Lookup'ı; ön oynatma rakipten önce koştu ve K'yı bulmadı, rakip ilk yazandır); sonuç yok sayılırsa iki satır olur (yok sayılan Lookup + `NoBalance` dalının Lookup'ı; 120'de `DuplicateKey` dalınınki). TC10 `ReplayLogCount(k) == 1` ve `"Bakiye uygulama yarışı"` uyarısının yokluğunu iddia eder.
3. **Bu görevin bütün testleri satış senkronu mod Off'ta.** `Kit.CreateAsync`'in varsayılanı Off; `Session42` çapaya yazılır ama kapsam kuralının kilidi alınmaz (Görev 7: `scope = mode == On ? req.SaleScope : null`). Mod On'da TC10'un okuma kancasındaki rakip, dış isteğin deneme işleminin tuttuğu `sp_getapplock`'u bekler; dış istek de kancanın dönmesini bekler → rakip 5 sn sonra 409 `stale` alır ve test yanlış şeyi ölçer. Kapsam kuralı ∥ apply (§8.2-15) Görev 16'nın konusudur.
4. **TC2'nin belirlenimli testi kaybedenin yolunu ayırt etmez.** Rakip dış kaydın içinde commit edince dış istek ya PK'ya (`IsDuplicateTransactionKey` → `DuplicateKey` → denetleyicinin Lookup'ı) ya jetona (→ 2. denemenin Lookup'ı) çarpar; EF'in komut sırasına bağlıdır, ikisi de §4.2-6'da "kazananın sonucunu oynat"tır. Jeton yolunu sabitleyen TC10'dur (üçüncü yazım PK'ya varmadan geri alır).
5. **Isınma turu preview'la.** S1 emsali var olmayan Id'nin reverse'iyle (404) ısındı; apply'ın ısınması para yazardı. Havuzlu `PreviewAsync(row, memberIds)` (Görev 1 kiti, Görev 5) yazımsızdır ve kümenin sorgularını (`ResolveDrawSetAsync`) da derler.
6. **Görev 7 Not 6'nın penceresi burada kanıtlanır (`Ilk_denemede_havuzu_tuketen_ayni_anahtarli_rakip_oynatilir`).** Kapsamsız yolda (mod Off) X'in 1. denemesinin tek Lookup'ı kilitsiz ön oynatmadır; geri çağrı 1. denemede Lookup koşmaz (Görev 7 Not 1: `attempt > 1 ∨ kapsam dolu`). Okuma kancası iki halkalı kurulur: 1. halka X'in ön oynatmasının defter okumasında (`FROM [CustomerBalanceTransactions]`, isteğin ilk defter okuması; K henüz yok) yalnız 2. halkayı kurar; 2. halka bir sonraki `FROM [CustomerBalances]` okumasında, yani `DrawAsync`'in 1. denemesindeki çekim kümesinin izlemesiz bakiye okumasında (`ResolveDrawSetAsync` → `MembersAsync`) tetiklenir. Arada bakiye okuyan komut yoktur: ön oynatma K'yı bulamayınca bakiye okumaz, `PoolClientMeetsMinimum`/`HasMemberIds` DB'ye gitmez, `KnownMembersAsync` ve `MembersAsync`'in ilk iki sorgusu projeksiyon okur. Rakip (aynı istek, aynı K) orada commit eder ve havuzun tamamını (130) çeker; X Σ = 0 görür, ne jetona ne PK'ya çarpar (yazıma varmaz) ve `DrawAsync` `NoBalance` döner. Beklenen: X 200 ve rakibin `(K, 130, 0)`'ı; Not 6'nın son Lookup'ı olmasaydı 409 `no-balance`. Havuzun **tamamı** tüketilmeli: kısmen tükenseydi X çapa K'da PK'ya çarpar ve `DuplicateKey` yolu oynatırdı (Not 6'nın "sorun yok" dediği hâl). Kanıt ayrıca günlükle sabitlenir: tek `"tekrar oynatıldı"` satırı, `"Bakiye uygulama yarışı"` uyarısı ve `PersonBalance`'ın `"baştan kuruluyor"` satırı yok, `ConcurrencyFailures == 0`.

- [ ] **Adım 1: Apply yarış testlerini yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawRaceTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using ApplyController = OrderDeck.LicenseServer.Controllers.Licenses.LicensesCustomerBalanceApplyController;
using ApplyResponse = OrderDeck.LicenseServer.Controllers.Licenses.LicensesCustomerBalanceApplyController.ApplyResponse;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Havuzlu apply'ın eşzamanlılık sözleşmesi gerçek SQL Server'da (spec 2026-10-11 §8.2-1, -2, -10; §4.2 adım 6;
/// §4.13): kişi kilidi havuzu eksiye düşürmez (D3), aynı anahtarla yarışta tek bacak kümesi yazılır ve kaybeden
/// kazananı oynatır, her denemenin başında Lookup koşar, 1. denemede havuzu tüketen aynı anahtarlı rakip
/// <c>no-balance</c> yerine oynatılır (no-balance öncesi son Lookup), üç çakışmadan sonra 503 <c>balance-busy</c> (hiçbir şey
/// yazılmaz), çapanın bakiye satırı açma yarışı baştan kurulur. Bütün testler satış senkronu mod Off'ta: kapsam
/// kuralının kilidi okuma kancasındaki rakibi bekletirdi (§8.2-15 ayrı sınıfta). InMemory'de tekil indeks ve atomik
/// kayıt yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceDrawRaceTests : IAsyncLifetime
{
    private const int Parallel = 8;

    private readonly SqlServerContainerFixture _sql;
    private PersonBalanceRelationalApiFactory _factory = null!;

    public PersonBalanceDrawRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() =>
        _factory = new PersonBalanceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Queries.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Bir apply isteğinin sonucu; başarısızsa yalnız durum ve problem başlığı.</summary>
    private sealed record Outcome(
        HttpStatusCode Status, string? Title, Guid TransactionId = default, decimal Applied = 0m, decimal Remaining = 0m);

    /// <summary>
    /// Durum, (başarısızsa) problem başlığı, (başarılıysa) yanıt gövdesi. TestServer, sunucu tarafında yakalanmayan
    /// istisnayı istemciye fırlatır; prod'da 500 olurdu — başlık yerine en içteki istisna, düşen koşunun mesajı
    /// nedeni göstersin (emsal <c>PersonBalanceReversalRaceTests.StatusAsync</c>).
    /// </summary>
    private static async Task<Outcome> StatusAsync(Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await call();
        }
        catch (Exception e)
        {
            var root = e.GetBaseException();
            return new Outcome(HttpStatusCode.InternalServerError, $"{root.GetType().Name}: {root.Message}");
        }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) return new Outcome(resp.StatusCode, await Kit.TitleAsync(resp));
            var body = (await resp.Content.ReadFromJsonAsync<ApplyResponse>())!;
            return new Outcome(resp.StatusCode, null, body.TransactionId, body.AppliedAmount, body.RemainingBalance);
        }
    }

    /// <summary>Kişi {A = 100, B = −60, C = 50} (havuz 90), havuz kipinde — §4.13'ün kişi kilidi örneği.</summary>
    private static async Task<(Guid A, Guid B, Guid C)> AbcAsync(Kit kit)
    {
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);
        var c = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        return (a, b, c);
    }

    /// <summary>Spec Örnek 1'in ÖNCESİ (T = 0, I = 100, Y = 30; havuz 130), havuz kipinde.</summary>
    private static async Task<(Guid T, Guid I, Guid Y)> Ornek1OncesiAsync(Kit kit)
    {
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        await kit.EnablePoolAsync();
        return (t, i, y);
    }

    /// <summary>Verilen satırların bakiyeleri toplamı (kişinin havuzu).</summary>
    private static async Task<decimal> PoolAsync(Kit kit, params Guid[] ids)
    {
        var sum = 0m;
        foreach (var id in ids) sum += await kit.BalanceAsync(id);
        return sum;
    }

    /// <summary>D1: her satırda bakiye = defter toplamı.</summary>
    private static async Task ShouldHoldLedgerAsync(Kit kit, params Guid[] ids)
    {
        foreach (var id in ids)
            (await kit.LedgerHoldsAsync(id)).Should().BeTrue("D1 ({0})", id);
    }

    /// <summary>Lisansın bütün düşüm satırları (çapalar ve bacaklar).</summary>
    private static Task<List<CustomerBalanceTransaction>> DeductionsAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindDeduction)
            .ToListAsync());

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId));

    /// <summary>
    /// Denetleyicinin bu anahtar için yazdığı oynatma satırları: <c>LookupAsync</c> her oynatmada bir
    /// <c>"tekrar oynatıldı"</c> Information satırı yazar, çağıran sonucu kullansa da kullanmasa da — yok sayılan bir
    /// Lookup sonucu satır sayısında görünür.
    /// </summary>
    private int ReplayLogCount(Guid key) =>
        _factory.Log.Entries.Count(e => e.Category == typeof(ApplyController).FullName
            && e.Message.Contains("tekrar oynatıldı", StringComparison.Ordinal)
            && e.Message.Contains(key.ToString(), StringComparison.Ordinal));

    /// <summary>Denetleyicinin <c>DuplicateKey</c> dalının uyarısı (PK yarışında kazanan oynatıldı), bu anahtar için.</summary>
    private int DuplicateKeyWarningCount(Guid key) =>
        _factory.Log.Entries.Count(e => e.Category == typeof(ApplyController).FullName
            && e.Level == LogLevel.Warning
            && e.Message.Contains("Bakiye uygulama yarışı", StringComparison.Ordinal)
            && e.Message.Contains(key.ToString(), StringComparison.Ordinal));

    /// <summary><see cref="PersonBalance"/>'ın baştan kurma satırları (<c>"… baştan kuruluyor"</c>), bu çapa için.</summary>
    private List<LogEntry> RetryLogs(Guid anchor) =>
        _factory.Log.Entries
            .Where(e => e.Category == typeof(PersonBalance).FullName
                && e.Level == LogLevel.Information
                && e.Message.Contains("baştan kuruluyor", StringComparison.Ordinal)
                && e.Message.Contains(anchor.ToString(), StringComparison.Ordinal))
            .ToList();

    /// <summary>Örnek 1'in yazımı tam bir kez: tek bacak kümesi, başka düşüm yok, T 0, I 0, Y 10, D1.</summary>
    private static async Task ShouldBeOrnek1OnceAsync(Kit kit, Guid k, Guid t, Guid i, Guid y)
    {
        var rows = await kit.GroupRowsAsync(k);
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -100m), (y, -20m) }, "tek bacak kümesi: Örnek 1");
        rows[0].Id.Should().Be(k, "çapa = anahtar, ilk satır");
        (await DeductionsAsync(kit)).Should().HaveCount(3, "aynı anahtarla ikinci bir grup ya da bacak yazılmadı");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m);
        (await kit.BalanceAsync(y)).Should().Be(10m, "Y'den yalnız bir kez 20 düştü");
        await ShouldHoldLedgerAsync(kit, t, i, y);
    }

    /// <summary>
    /// §8.2-1, belirlenimli (§4.13'ün örneği birebir): C'den 50'lik apply'ın (dış istek) kaydı SQL gitmeden önce A'dan
    /// farklı anahtarlı 90'lık apply (rakip) commit eder. Çapa A'da 100 olduğu için rakip yalnız A'yı çeker; dış istek de
    /// çapa C'deki 50 ile yalnız C'yi. Çekilen satırlar ayrıktır: kişi kilidi (jeton dokunuşu) olmasaydı iki yazım da
    /// commit eder, havuz 90 − 90 − 50 = −50'ye inerdi (D3 ihlali). Rakip B ile C'ye, dış istek A ile B'ye dokunduğu
    /// için dış isteğin UPDATE'i tutmaz; taze planda Σ = 10 − 60 + 50 = 0 → 409 <c>no-balance</c>.
    /// </summary>
    [Fact]
    public async Task Tc1_iki_uyeden_farkli_anahtarli_apply_taze_planla_no_balance()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (a, b, c) = await AbcAsync(kit);
        var k1 = Guid.NewGuid();
        var k2 = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        var atDrawSave = false;
        _factory.Hook.Once(async saving =>
        {
            // Kanca dış isteğin çekim kaydında tetiklenmeli (istekte daha erken bir SaveChanges yok).
            atDrawSave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == k1);
            rakipler.Add(await StatusAsync(() => kit.ApplyAsync(a, 90m, 90m, k2, null, [a, b, c])));
        });

        var dis = await StatusAsync(() => kit.ApplyAsync(c, 50m, 50m, k1, null, [a, b, c]));
        _factory.Hook.Disarm();

        atDrawSave.Should().BeTrue("rakip, dış apply'ın çekim kaydında commit etmeli");
        var rakip = rakipler.Should().ContainSingle("kanca bir kez koşar").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k2, 90m, 0m), "rakip A'dan 90 çeker, Σ 90 − 90");
        (dis.Status, dis.Title).Should().Be((HttpStatusCode.Conflict, PersonBalance.CodeNoBalance),
            "taze planda Σ = 10 − 60 + 50 = 0 (§4.13 kişi kilidi)");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1,
            "dış istek yalnız C'yi çekti; rakip C'ye, dış istek A'ya dokunduğu için rakibin commit'ine çarptı. "
            + "Dokunuş olmasaydı ayrık satırlar commit eder, havuz −50 olurdu");

        (await kit.GroupRowsAsync(k2)).Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (a, -90m) }, "rakip tek bacak: A; B ile C'ye yalnız dokundu");
        (await kit.GroupRowsAsync(k1)).Should().BeEmpty("kaybeden hiçbir şey yazmadı");
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(-60m, "eksi satır kilitlenir, çekilmez");
        (await kit.BalanceAsync(c)).Should().Be(50m, "dış isteğin C çekimi geri alındı");
        (await PoolAsync(kit, a, b, c)).Should().Be(0m, "havuz hiçbir an < 0 değil (D3)");
        await ShouldHoldLedgerAsync(kit, a, b, c);
    }

    /// <summary>
    /// §8.2-1, paralel: aynı kişinin iki üyesinden (A ve C sırayla) farklı anahtarlı sekiz apply, her biri 30. Havuz 90
    /// olduğu için en çok üç commit olur; kaybedenler taze planda 409 <c>no-balance</c> alır ya da üç çakışmanın üçünü
    /// de yitirip 503 <c>balance-busy</c> (hiçbir şey yazmaz). Haritadaki "paralel apply için gerçek SQL testi yok"
    /// boşluğunu kapatır.
    /// </summary>
    [Fact]
    public async Task Tc1_paralel_sekiz_apply_havuzu_eksiye_dusurmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (a, b, c) = await AbcAsync(kit);
        // Isınma: yazımsız havuzlu preview — rota, EF JIT ve kümenin sorguları; gerçek turda istekler ayrışmasın.
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => kit.PreviewAsync(a, [a, b, c])));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(n =>
            StatusAsync(() => kit.ApplyAsync(n % 2 == 0 ? a : c, 30m, 30m, Guid.NewGuid(), null, [a, b, c]))));

        var seen = string.Join(", ", results.Select(r => $"{(int)r.Status} {r.Title} {r.Applied}"));
        results.Should().OnlyContain(r => r.Status == HttpStatusCode.OK
                || (r.Status == HttpStatusCode.Conflict && r.Title == PersonBalance.CodeNoBalance)
                || (r.Status == HttpStatusCode.ServiceUnavailable && r.Title == "balance-busy"),
            "500 ya da başka bir ret kişi kilidi/kilitlenme yolunun eşleşmediğini gösterir ({0})", seen);
        var winners = results.Where(r => r.Status == HttpStatusCode.OK).ToList();
        winners.Should().NotBeEmpty("ilk commit eden kazanır ({0})", seen)
            .And.OnlyContain(r => r.Applied == 30m, "havuz 30'un katı, kısmi çekim yok ({0})", seen);
        var drawn = winners.Sum(r => r.Applied);
        drawn.Should().BeLessThanOrEqualTo(90m, "havuzdan fazlası çekilmez ({0})", seen);

        (await kit.BalanceAsync(a)).Should().BeGreaterThanOrEqualTo(0m);
        (await kit.BalanceAsync(c)).Should().BeGreaterThanOrEqualTo(0m);
        (await kit.BalanceAsync(b)).Should().Be(-60m, "eksi satır kilitlenir, çekilmez");
        (await PoolAsync(kit, a, b, c)).Should().Be(90m - drawn, "havuz = 90 − Σ AppliedAmount")
            .And.BeGreaterThanOrEqualTo(0m, "havuz hiçbir an < 0 değil (D3)");
        (await DeductionsAsync(kit)).Count(t => t.Id == t.DrawGroupId).Should()
            .Be(winners.Count, "yalnız kazananlar çapa yazar; 409 ve 503 hiçbir şey yazmaz ({0})", seen);
        await ShouldHoldLedgerAsync(kit, a, b, c);
    }

    /// <summary>
    /// §8.2-2, belirlenimli: aynı anahtarla (K) çok bacaklı apply yarışı. Rakip aynı istek dış kaydın içinde, SQL gitmeden
    /// commit eder; dış istek PK'ya (→ <c>DuplicateKey</c> → Lookup) ya da jetona (→ 2. denemenin Lookup'ı) çarpar —
    /// ikisinde de bütün bacaklar geri alınır ve kazananın sonucu oynatılır.
    /// </summary>
    [Fact]
    public async Task Tc2_ayni_anahtarla_cok_bacakli_yaris_tek_grup_kazanan_oynatilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        var atDrawSave = false;
        _factory.Hook.Once(async saving =>
        {
            atDrawSave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == k);
            rakipler.Add(await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y])));
        });

        var dis = await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]));
        _factory.Hook.Disarm();

        atDrawSave.Should().BeTrue("rakip, dış apply'ın çekim kaydında commit etmeli");
        var rakip = rakipler.Should().ContainSingle("kanca bir kez koşar").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 120m, 10m), "rakip Örnek 1'i yazar: (K, 120, 10)");
        dis.Status.Should().Be(HttpStatusCode.OK, "kaybeden kazananı oynatır ({0})", dis.Title);
        (dis.TransactionId, dis.Applied).Should().Be((rakip.TransactionId, rakip.Applied),
            "oynatılan sonuç kazananınki: çapa K, tutar −Σ(grup)");
        await ShouldBeOrnek1OnceAsync(kit, k, t, i, y);
    }

    /// <summary>§8.2-2, paralel: aynı istek (aynı anahtar K) sekiz kez birlikte — hepsi 200 ve aynı sonuç, tek grup.</summary>
    [Fact]
    public async Task Tc2_paralel_sekiz_ayni_anahtar_tek_grup()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => kit.PreviewAsync(t, [t, i, y])));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel)
            .Select(_ => StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]))));

        var seen = string.Join(", ", results.Select(r => $"{(int)r.Status} {r.Title} {r.TransactionId:N} {r.Applied}"));
        results.Should().OnlyContain(r => r.Status == HttpStatusCode.OK && r.TransactionId == k && r.Applied == 120m,
            "tek kazanan yazar, kaybedenler PK ya da 2. denemenin Lookup'ıyla onu oynatır ({0})", seen);
        await ShouldBeOrnek1OnceAsync(kit, k, t, i, y);
    }

    /// <summary>
    /// §8.2-10 (§4.2 adım 6 "2. ve sonraki her denemenin başında Lookup"): dış istek X (anahtar K, tutar 230) kaydında
    /// araya üçüncü bir yazım girer — panel iadesi I'ye +100, I'nin jetonunu ilerletir, X'in 1. denemesi (130'luk plan)
    /// geri alınır. X'in 2. denemesinin başındaki Lookup sorgusundan hemen önce aynı anahtarlı rakip commit eder ve
    /// iadeden sonraki havuzun tamamını (230) çekerek havuzu SIFIRLAR (zincir jetona dayanır, PK'ya değil). X'in Lookup'ı
    /// K'yı bulur: <c>no-balance</c> değil, ikinci düşüm değil — kazananın sonucu. Lookup'sız bir döngüde okuma kancası
    /// tetiklenmez, X 2. denemede havuzdan kendi 230'unu çekerdi (§14-8): <c>rakipler</c> boş kalır. Lookup koşup sonucu
    /// yok sayılsaydı yanıt YİNE 200 olurdu — Görev 7'nin <c>no-balance</c> öncesi son Lookup'ı Σ = 0'dan sonra K'yı
    /// oynatır (Görev 7 Not 6) — bu yüzden ayırt eden iddia denetleyicinin oynatma günlüğüdür: tek
    /// <c>"tekrar oynatıldı"</c> satırı (yok sayılan Lookup + son Lookup = iki satır).
    /// </summary>
    [Fact]
    public async Task Tc10_araya_giren_ucuncu_yazimla_ikinci_istek_kazanani_oynatir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        var atDrawSave = false;
        _factory.Hook.Once(async saving =>
        {
            atDrawSave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == k);
            // Üçüncü yazım: I'ye +100 → X'in 1. denemesi (T 0, I −100, Y −30) I'nin jetonuna çarpar.
            (await kit.PanelRefundAsync(i, 100m)).EnsureSuccessStatusCode();
            // X'in 2. denemesi önce Lookup'ı koşar (defter okuması); rakip ondan hemen önce commit eder ve
            // iadeden sonraki havuzun tamamını (0 + 200 + 30) çeker.
            _factory.Queries.Arm(
                sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal),
                async () => rakipler.Add(
                    await StatusAsync(() => kit.ApplyAsync(t, 230m, 230m, k, Kit.Session42, [t, i, y]))));
        });

        // 1. denemede applied = min(230, 130, 230) = 130.
        var dis = await StatusAsync(() => kit.ApplyAsync(t, 230m, 230m, k, Kit.Session42, [t, i, y]));
        _factory.Hook.Disarm();
        _factory.Queries.Disarm();

        atDrawSave.Should().BeTrue("üçüncü yazım X'in çekim kaydında girmeli");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1,
            "X'in 1. denemesi iadenin ilerlettiği jetonla geri alınmalı");
        var rakip = rakipler.Should().ContainSingle("rakip X'in 2. denemesinin Lookup'ından önce koşmalı").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 230m, 0m),
            "rakip iadeden sonraki havuzun tamamını çeker: (K, 230, 0)");
        dis.Status.Should().Be(HttpStatusCode.OK,
            "no-balance değil: 2. denemenin başındaki Lookup kazananı bulur, havuz artık 0 ({0})", dis.Title);
        (dis.TransactionId, dis.Applied).Should().Be((rakip.TransactionId, rakip.Applied),
            "X kazananın sonucunu oynatır");
        // Yanıt, sonucu yok sayılan bir Lookup'ı ayırt etmez (Görev 7'nin no-balance öncesi son Lookup'ı kurtarır);
        // günlük ayırır. Ön oynatma rakipten önce koştu (K yoktu), rakip ilk yazan: oynatma yalnız X'in 2. denemesinde.
        ReplayLogCount(k).Should().Be(1,
            "tek oynatma: X'in 2. denemesinin başındaki Lookup. İki satır = o Lookup'ın sonucu yok sayıldı ve kazananı "
            + "Görev 7'nin no-balance öncesi son Lookup'ı oynattı (R33, §4.2-6)");
        DuplicateKeyWarningCount(k).Should().Be(0, "PK yolu değil: X'in 2. denemesi yazıma varmadı");
        RetryLogs(k).Should().ContainSingle("tek baştan kurma: 1. deneme jetona çarptı, 2. deneme Lookup'ta durdu")
            .Which.Message.Should().Contain("jeton çakışması");

        // İadeden sonra I = 200: rakibin taze planı çapa T (0) → I'den 200 → Y'den 30.
        (await kit.GroupRowsAsync(k)).Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -200m), (y, -30m) }, "tek grup, rakibin taze planı");
        (await DeductionsAsync(kit)).Should().HaveCount(3)
            .And.OnlyContain(r => r.Id == k || r.DrawGroupId == k, "ikinci düşüm yok: K'dan başka grup yazılmadı");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m, "100 + 100 iade − 200, bir kez");
        (await kit.BalanceAsync(y)).Should().Be(0m, "30 − 30, bir kez");
        await ShouldHoldLedgerAsync(kit, t, i, y);
    }

    /// <summary>
    /// §4.2 adım 6'nın amacı 1. denemede (Görev 7 Not 6, iskelet farkı "no-balance'tan önce son bir Lookup"): mod Off,
    /// kapsamsız istek — 1. denemenin tek Lookup'ı kilitsiz ön oynatmadır. X'in (anahtar K, 130) ön oynatmasından SONRA,
    /// <c>DrawAsync</c>'in 1. denemesindeki ilk bakiye okumasından (çekim kümesinin izlemesiz okuması) hemen ÖNCE aynı
    /// anahtarlı rakip commit eder ve havuzun tamamını (130) çeker. X Σ = 0 görür; ne jeton çakışması ne PK ihlali olur
    /// (yazıma varmaz), <c>DrawAsync</c> <c>NoBalance</c> döner. Beklenen: 409 <c>no-balance</c> değil, kazananın sonucu —
    /// <c>ApplyPoolAsync</c>'in <c>NoBalance</c> dalındaki son Lookup K'yı bulur. O Lookup olmasaydı WPF kesin
    /// "bakiye yok" yazardı (<c>MarkNoBalance</c>), oysa anahtar defterde ve para düşülmüş.
    /// </summary>
    [Fact]
    public async Task Ilk_denemede_havuzu_tuketen_ayni_anahtarli_rakip_oynatilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        var onOynatmaGecti = false;
        // 1. halka: X'in ön oynatması (Apply'ın kilitsiz LookupAsync'i, isteğin ilk defter okuması). K henüz yok;
        // halka yalnız bir sonrakini kurar.
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal),
            () =>
            {
                onOynatmaGecti = true;
                // 2. halka: X'in bundan sonraki ilk bakiye okuması = DrawAsync'in 1. denemesinde çekim kümesinin
                // izlemesiz okuması (ResolveDrawSetAsync → MembersAsync). Rakip ondan hemen önce aynı anahtarla commit
                // eder ve havuzun tamamını (0 + 100 + 30) çeker.
                _factory.Queries.Arm(
                    sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
                    async () => rakipler.Add(
                        await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y]))));
                return Task.CompletedTask;
            });

        var dis = await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y]));
        _factory.Queries.Disarm();

        onOynatmaGecti.Should().BeTrue("1. halka X'in ön oynatmasında kurulmalı");
        var rakip = rakipler.Should().ContainSingle("rakip X'in 1. deneme bakiye okumasından önce koşmalı").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 130m, 0m),
            "rakip ilk yazan, havuzun tamamını çeker: (K, 130, 0)");
        dis.Should().Be(rakip,
            "no-balance değil: NoBalance dalındaki son Lookup kazananı oynatır, havuz artık 0 ({0})", dis.Title);
        _factory.Hook.ConcurrencyFailures.Should().Be(0, "X hiç yazmadı: Σ = 0 planı jetona varmaz");
        RetryLogs(k).Should().BeEmpty("X 1. denemede NoBalance aldı; baştan kurma yok, 2. deneme Lookup'ı koşmadı");
        DuplicateKeyWarningCount(k).Should().Be(0, "PK yolu değil: havuzun tamamı tükendi, X çapa yazmaya varmadı");
        ReplayLogCount(k).Should().Be(1,
            "tek oynatma: NoBalance dalının son Lookup'ı (ön oynatma rakipten önce koştu, kapsamsız 1. denemede geri "
            + "çağrı Lookup koşmaz)");

        (await kit.GroupRowsAsync(k)).Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -100m), (y, -30m) }, "tek grup, rakibin planı");
        (await DeductionsAsync(kit)).Should().HaveCount(3)
            .And.OnlyContain(r => r.Id == k || r.DrawGroupId == k, "ikinci düşüm yok: K'dan başka grup yazılmadı");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m, "100 − 100, bir kez");
        (await kit.BalanceAsync(y)).Should().Be(0m, "30 − 30, bir kez");
        await ShouldHoldLedgerAsync(kit, t, i, y);
    }

    /// <summary>
    /// §4.2 adım 6 / §4.13: üç denemede de jeton çakışması → 503 <c>balance-busy</c>; SaveChanges tek işlem olduğu için
    /// hiçbir şey yazılmaz. Servis tek uyarı yazar, sayaç <c>endpoint=apply</c> ile bir artar (Görev 13; InMemory'de üç
    /// çakışma kurulamadığı için sayaç burada doğrulanır).
    /// </summary>
    [Fact]
    public async Task Uc_denemede_de_cakisan_apply_503_balance_busy_hicbir_sey_yazmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var oncekiHareketler = await TransactionCountAsync(kit);
        var metrics = _factory.Services.GetRequiredService<OrderDeckMetrics>();
        var olcumler = new ConcurrentQueue<(long Value, string? Endpoint)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Paralel sınıflar aynı Meter adını paylaşır: yalnız bu fabrikanın sayacı.
            if (ReferenceEquals(instrument, metrics.BalanceBusy)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!ReferenceEquals(instrument, metrics.BalanceBusy)) return;
            string? endpoint = null;
            foreach (var tag in tags)
                if (tag.Key == "endpoint") endpoint = tag.Value as string;
            olcumler.Enqueue((value, endpoint));
        });
        listener.Start();
        var k = Guid.NewGuid();
        _factory.Hook.Always(() => TouchBalanceAsync(i));

        var sonuc = await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, null, [t, i, y]));
        _factory.Hook.Disarm();

        (sonuc.Status, sonuc.Title).Should().Be((HttpStatusCode.ServiceUnavailable, "balance-busy"));
        _factory.Hook.ConcurrencyFailures.Should().Be(PersonBalance.MaxAttempts);
        (await TransactionCountAsync(kit)).Should().Be(oncekiHareketler, "SaveChanges tek işlem: yeni hareket yok");
        (await kit.GroupRowsAsync(k)).Should().BeEmpty();
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(100m);
        (await kit.BalanceAsync(y)).Should().Be(30m);
        _factory.Log.Entries
            .Where(e => e.Category == typeof(PersonBalance).FullName && e.Level == LogLevel.Warning)
            .Should().ContainSingle("tükenişte tek uyarı").Which.Message.Should().Contain("balance-busy");
        olcumler.Should().ContainSingle("tek 503, tek ölçüm").Which.Should().Be((1L, "apply"));
    }

    /// <summary>
    /// §4.2 adım 5–6 (R30/R34): çapa T'nin bakiye satırı yok; apply onu 0 ile açarken kaydından hemen önce panel iadesi
    /// T'nin satırını açar. Tekil <c>(LicenseId, WpfCustomerId)</c> ihlali "bakiye satırı açma yarışı" olarak baştan
    /// kurulur (PK oynatmasına gitmez — fark 9); taze planda çapa T'nin 5'i önce kullanılır.
    /// </summary>
    [Fact]
    public async Task Capanin_bakiye_satiri_acma_yarisi_bastan_kurulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g, balanceRow: false);
        var i = await kit.MemberAsync(g, 100m);
        await kit.EnablePoolAsync();
        var atAnchorRowOpen = false;
        _factory.Hook.Once(async saving =>
        {
            atAnchorRowOpen = saving.ChangeTracker.Entries<CustomerBalance>()
                .Any(e => e.State == EntityState.Added && e.Entity.WpfCustomerId == t);
            (await kit.PanelRefundAsync(t, 5m)).EnsureSuccessStatusCode();
        });

        var sonuc = await StatusAsync(() => kit.ApplyAsync(t, 50m, 50m, Guid.NewGuid(), null, [t, i]));
        _factory.Hook.Disarm();

        atAnchorRowOpen.Should().BeTrue("kanca çapanın satırını açan çekim kaydında tetiklenmeli");
        sonuc.Status.Should().Be(HttpStatusCode.OK, "satır açma yarışı baştan kurulur ({0})", sonuc.Title);
        sonuc.Applied.Should().Be(50m);
        (await kit.Sales.ReadDbAsync(db => db.CustomerBalances.AsNoTracking()
                .CountAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == t)))
            .Should().Be(1, "T'nin tek bakiye satırı var");
        var tBalance = await kit.BalanceAsync(t);
        tBalance.Should().BeGreaterThanOrEqualTo(0m);
        (tBalance + await kit.BalanceAsync(i)).Should().Be(55m, "105 − 50");
        await ShouldHoldLedgerAsync(kit, t, i);
        _factory.Log.Entries
            .Where(e => e.Category == typeof(PersonBalance).FullName
                && e.Message.Contains("bakiye satırı açma yarışı", StringComparison.Ordinal))
            .Should().ContainSingle("tek yeniden deneme, nedeni satır açma yarışı")
            .Which.Level.Should().Be(LogLevel.Information);
    }

    /// <summary>Kilit dokunuşu: satıra yalnız "değişti" işareti, jetonu StampVersions ilerletir.</summary>
    private async Task TouchBalanceAsync(Guid row)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == row);
        db.Entry(balance).Property(b => b.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync();
    }
}
```

Kullanılan var olan üyeler (worktree'de doğrulandı): `SqlServerCollection.Name` (`TestHelpers/SqlServerContainerFixture.cs:359`), `SqlServerContainerFixture.CreateDatabaseAsync`, `RaceSaveHook.Once(Func<DbContext,Task>)` / `Always` / `Disarm` / `ConcurrencyFailures` (`TestHelpers/RaceSaveHook.cs:19-37`), `ReadQueryHook.Arm` / `Disarm` (`TestHelpers/ReadQueryHook.cs:19-25`), `LogRecorderProvider.Entries` + `LogEntry(Category, Level, Message)` (`TestHelpers/LogApiFactory.cs:26`, `:48`), `Kit.MemberAsync` / `NewGroup` / `BalanceAsync` / `LedgerHoldsAsync` / `PanelRefundAsync` / `TitleAsync` / `Session42` / `KindDeduction` / `Sales.ReadDbAsync` (`PersonBalanceTestKit.cs`, `SalesCommandTestKit.cs:469`), `LicensesCustomerBalanceApplyController.ApplyResponse` (`:307-310`), `PersonBalance.MaxAttempts` (`:24`), `OrderDeckMetrics` tekil kayıt (`Program.cs:901`), denetleyicinin günlük kategorisi `ILogger<LicensesCustomerBalanceApplyController>` (`:82`) ve `LookupAsync`'in oynatma satırı `"Bakiye uygulama sonucu tekrar oynatıldı (key={Key}, license={LicenseId})"` (`:629-630`; Görev 6'nın havuzlu `LookupAsync`'i aynen korur), `ReadQueryHook`'un zinciri (gövdeden `Arm`, `ReadQueryHook.cs:7-10`). Önceki görevlerden: denetleyicinin `DuplicateKey` uyarısı `"Bakiye uygulama yarışı: …(key={Key}, …)"` ve `NoBalance` dalının son Lookup'ı (Görev 7 Adım 4, Not 6), `DrawAsync`'in `"… {Attempt}. denemede kurulamadı ({Reason}), baştan kuruluyor (… anchor={AnchorId})"` satırı (Görev 4 Adım 5), `PersonBalanceRelationalApiFactory` (`Hook`, `Queries`, `Log`), `Kit.EnablePoolAsync`, `Kit.PreviewAsync(row, memberIds)`, `Kit.ApplyAsync(…, memberIds)`, `Kit.GroupRowsAsync` (Görev 1), `PersonBalance.CodeNoBalance` (Görev 2), `OrderDeckMetrics.BalanceBusy` (Görev 13).

- [ ] **Adım 2: Koştur (Docker açık) — PASS bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawRaceTests"`
Expected: 8 test PASS (kod değişikliği yok; Görev 4, 6, 7 ve 13'ün kodu yeterli).

Docker'a bağlanamıyorsa (yerelde "Docker is either not running…"): `docker context ls`, sonra **PowerShell'den** `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'; $env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawRaceTests"` (boru adı `docker_engine` da olabilir; git-bash değeri bozar).

Kırmızı kalırsa olası nedenler ve dayandığı sözleşme:
- `atDrawSave`/`atAnchorRowOpen` false → kanca çekimin değil istekte daha erken bir `SaveChanges`'in önünde tetiklendi: Görev 7'nin `Apply` → `ApplyPoolAsync` yolunda çekimden önce yazım olmamalı.
- TC1 belirlenimli: dış istek 200 (C'den 50) ve havuz −50 → Görev 4 Adım 5 — `DrawAsync` akışının `StageDraw` yazımındaki jeton dokunuşu eksik: `P`'nin satırı olan çekilmeyen üyeleri (dış istekte A ve B, rakipte B ve C) `IsModified` işaretlenmiyor (kişi kilidi, R56). 500 → `DbUpdateConcurrencyException` yakalanmıyor.
- TC1 paralel 500 → `IsDeadlock` süzgeci ya da `try`'ın okumaları kapsaması (Görev 4, R36); çapa sayısı ≠ 200 sayısı → bir ret dalı (NoBalance/Busy) yazımdan önce dönmüyor.
- TC2 500 `InvalidOperationException: … PK ihlali verdi ama kazanan bulunamadı` → Görev 7'nin `DuplicateKey` dalındaki Lookup; 500 `DbUpdateException` (2627 `PK_CustomerBalanceTransactions`) → Görev 4'ün `IsDuplicateTransactionKey` dalı yok ya da `AnchorIdIsIdempotencyKey` false geçiliyor. Yalnız 3'ten fazla düşüm satırı → kaybeden bacakları ayrı commit etti (tek `SaveChanges` değil, R32).
- TC10 `ReplayLogCount(k) == 2` (yanıt 200 olsa da) → 2. denemenin Lookup'ı K'yı buluyor ama sonucu oynatma olarak dönmüyor (Görev 7'nin geri çağrısı `stopped`'ı kurmuyor/`true` dönmüyor ya da Görev 4'ün döngüsü `beforeAttempt`'in `true`'sunu `Stopped`'a çevirmiyor; R33, §14-8); kazananı Görev 7'nin `NoBalance` dalındaki son Lookup kurtarıyor. TC10 X 409 `no-balance` → bu hata ile birlikte Görev 7 Not 6'nın son Lookup'ı da yok (`Ilk_denemede_…` testi de kırmızı olmalı). `rakipler` boş → 2. denemede Lookup yok (Görev 7'nin geri çağrısı `attempt > 1`'de koşmuyor ya da Görev 4 onu okumalardan ÖNCE çağırmıyor) ya da 1. deneme jetona çarpmadı (`ConcurrencyFailures == 0`). `RetryLogs(k)` ≠ 1 → 1. deneme jeton dışı bir nedenle (ör. üyelik) ya da 2. deneme de baştan kuruldu.
- `Ilk_denemede_havuzu_tuketen_ayni_anahtarli_rakip_oynatilir` X 409 `no-balance` → Görev 7 `ApplyPoolAsync`'in `NoBalance` dalında son Lookup yok ya da `IdempotencyKey`'i görmüyor (Görev 7 Not 6). `onOynatmaGecti` false → ön oynatma koşmadı (Görev 7 `Apply`'ın `preKey` bloğu); `rakipler` boş → X ön oynatmadan sonra hiç `FROM [CustomerBalances]` okumadı (EF metni farklı) ya da istek `DrawAsync`'e varmadı (`dis.Title`'a bak). `ConcurrencyFailures` > 0 ya da `RetryLogs(k)` dolu → X Σ = 0'ı görmedi: rakip ilk bakiye okumasından SONRA commit etti (bakiye okuması ön oynatmanın içinde ya da kümeden önce — Görev 6'nın `LookupAsync`'i K yokken bakiye okumamalı). `DuplicateKeyWarningCount(k)` > 0 → X çapa yazmaya vardı: havuz tamamen tükenmedi.
- 503 testinde `ConcurrencyFailures` ≠ 3 → `MaxAttempts` döngüsü; uyarı 0 ya da >1 → Görev 4'ün tükeniş günlüğü (tek `LogWarning`); ölçüm yok ya da `endpoint` ≠ `apply` → Görev 13 `BalanceBusy(licenseId, "apply")`'ın sayacı.
- Satır açma yarışı 500 ya da `InvalidOperationException` (kazanan bulunamadı) → `IsDuplicateBalanceRow` dalı `IsDuplicateTransactionKey`/genel PK oynatmasından SONRA (fark 9'un sırası); günlük satırı yoksa neden metni Görev 4'teki `"bakiye satırı açma yarışı"` dizesiyle aynı değil.

- [ ] **Adım 3: Kilitlenme (1205) testini yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawDeadlockTests.cs`:

```csharp
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using ApplyResponse = OrderDeck.LicenseServer.Controllers.Licenses.LicensesCustomerBalanceApplyController.ApplyResponse;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Havuzlu apply'ın kilitlenme kurbanı (1205) dalı, gerçek EF + SQL Server hattında ve belirlenimli (spec 2026-10-11
/// §4.2 adım 6 "S2 uygulama notu"). Paralel yarış (<see cref="PersonBalanceDrawRaceTests"/>) kilitlenmeyi yalnız bazı
/// koşularda doğurur; burada komut kancası seçilen komutta BİR KEZ <c>SqlException(1205)</c> fırlatır, komut sunucuya
/// gitmez.
/// <list type="bullet">
/// <item><b>Kayıt:</b> çekimin ilk yazım komutu. Varsayılan yürütme stratejisi onu
/// <see cref="InvalidOperationException"/>'a sarar; yalnız <see cref="DbUpdateException"/>'a bakan süzgeç kurbanı
/// 500'e düşürürdü.</item>
/// <item><b>Okuma:</b> denemenin ilk bakiye okuması (<c>ResolveDrawSetAsync</c>). <c>DrawAsync</c>'in <c>try</c>'ı
/// okumaları da kapsar; kapsamasaydı kurban 500 alırdı.</item>
/// </list>
/// InMemory'de komut kancası yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceDrawDeadlockTests : IAsyncLifetime
{
    public const string AtSave = "kayıt";
    public const string AtBalanceRead = "okuma";

    private readonly SqlServerContainerFixture _sql;
    private DeadlockFactory _factory = null!;

    public PersonBalanceDrawDeadlockTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new DeadlockFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Deadlock.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(AtSave)]
    [InlineData(AtBalanceRead)]
    public async Task Kilitlenme_kurbani_apply_bastan_kurar_ve_basarir(string at)
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        await kit.EnablePoolAsync();
        var k = Guid.NewGuid();
        _factory.Deadlock.Arm(at == AtSave ? DeadlockOnce.IsFirstWrite : DeadlockOnce.IsBalanceRead);

        var resp = await kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]);
        _factory.Deadlock.Disarm();

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ApplyResponse>()).Should()
            .Be(new ApplyResponse(k, 120m, 10m), "Örnek 1'in yanıtı: (K, 120, 10)");
        _factory.Deadlock.Injected.Should().Be(1, "kanca seçilen komutta ({0}) bir kez fırlatmalı", at);
        _factory.Log.Entries.Where(e => e.Message.Contains("kilitlenme (1205)", StringComparison.Ordinal))
            .Should().ContainSingle("tek yeniden deneme, nedeni kilitlenme")
            .Which.Level.Should().Be(LogLevel.Information, "baştan kurma satırı; balance-busy uyarısı değil");

        var rows = await kit.GroupRowsAsync(k);
        rows.Should().HaveCount(3, "ilk denemeden hiçbir şey kalmadı, ikinci tam yazdı");
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -100m), (y, -20m) }, "Örnek 1'in satırları");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m);
        (await kit.BalanceAsync(y)).Should().Be(10m, "baştan kurulan çekimde Y'den bir kez");
        foreach (var row in new[] { t, i, y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// Kurulduktan sonra <c>when</c>'i tutan ilk komutta bir kez SQL Server kilitlenme kurbanı hatası fırlatır; komut
    /// sunucuya hiç gitmez (emsal <c>PersonBalanceReversalDeadlockTests.DeadlockOnce</c>).
    /// </summary>
    private sealed class DeadlockOnce : DbCommandInterceptor
    {
        private Func<string, bool>? _when;
        private int _injected;

        public int Injected => Volatile.Read(ref _injected);

        /// <summary>Çekimin ilk yazım komutu: defter INSERT'i ya da bakiye UPDATE'i (EF tek grupta gönderir).</summary>
        public static bool IsFirstWrite(string sql) =>
            sql.Contains("INSERT INTO [CustomerBalanceTransactions]", StringComparison.Ordinal)
            || sql.Contains("MERGE [CustomerBalanceTransactions]", StringComparison.Ordinal)
            || sql.Contains("UPDATE [CustomerBalances]", StringComparison.Ordinal);

        /// <summary>Denemenin ilk bakiye okuması (çekim kümesinin izlemesiz bakiyeleri).</summary>
        public static bool IsBalanceRead(string sql) =>
            sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal);

        public void Arm(Func<string, bool> when) => Volatile.Write(ref _when, when);

        public void Disarm() => Volatile.Write(ref _when, null);

        private void ThrowOnce(DbCommand command)
        {
            if (Volatile.Read(ref _when) is not { } when || !when(command.CommandText)) return;
            if (Interlocked.Exchange(ref _when, null) is null) return;
            Interlocked.Increment(ref _injected);
            throw SqlExceptionFactory.Create(1205, "Transaction was deadlocked and has been chosen as the deadlock victim.");
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnce(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnce(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Havuz kipi için yapılandırılmış ilişkisel fabrika (iki sürüm kapısı da kitin <c>User-Agent</c>'ını kabul eder) +
    /// komut kancası + <see cref="PersonBalance"/>'ın günlüğü.
    /// </summary>
    private sealed class DeadlockFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public DeadlockOnce Deadlock { get; } = new();

        /// <summary>Yeniden deneme nedeni yalnız günlükte: <see cref="PersonBalance"/>'ın satırları.</summary>
        public LogRecorder<PersonBalance> Log { get; } = new();

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["PersonBalance:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
            ["SalesSync:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Deadlock);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<ILogger<PersonBalance>>(Log));
        }
    }
}
```

Kullanılan var olan üyeler: `RelationalApiFactory(string)` (`TestHelpers/RelationalApiFactory.cs:14-23`), `ApiFactory.ExtraConfig` (`TestHelpers/ApiFactory.cs:59`), `ConfigureDbContextOptions` (`:76`), `LogRecorder<T>` (`TestHelpers/LogRecorder.cs:7`, `Entries` = `(Level, Message)`), `SqlExceptionFactory.Create(int, string)` (emsal `PersonBalanceReversalDeadlockTests.cs:111`), `PersonBalance.IsDeadlock` (`PersonBalance.cs:388`; okuma ve kayıt biçimleri). Görev 1'den `PersonBalanceConfiguredApiFactory.MinClientVersion` (`"0.9.11"` = kitin varsayılan `User-Agent`'ı).

- [ ] **Adım 4: Koştur (Docker açık) — PASS bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawRaceTests|FullyQualifiedName~PersonBalanceDrawDeadlockTests"`
Expected: 8 + 2 test PASS.

Kırmızı kalırsa: (a) "kayıt"ta 500 `InvalidOperationException` → `DrawAsync`'in kilitlenme süzgeci `DbUpdateException`'a daralmış (Görev 4, `catch (Exception ex) when (IsDeadlock(ex))`); (b) "okuma"da 500 → `try` küme okumalarını (`ResolveDrawSetAsync`) kapsamıyor (Görev 4 Adım 5 — `DrawAsync` akışında `try` kapsam kilidini, `beforeAttempt`'i, çekim kümesi okumalarını, izlenen bakiye yüklemesini, üyeliğin ikinci okumasını ve `StageDraw` + `SaveChanges`'i kapsar; döngü başındaki `ChangeTracker.Clear()` dışarıda); (c) `Injected == 0` → seçilen komut hiç gelmedi: EF komut metni farklı (`FROM [CustomerBalances] AS [c]` / `INSERT INTO [CustomerBalanceTransactions]` beklenir). "okuma"da 500 ve `Injected == 1` → bakiye `DrawAsync`'in `try`'ı dışında okundu (Görev 7: ön oynatma K'yı bulamayınca bakiye okumaz), (b)'ye bak; (d) günlük satırı yok → neden metni Görev 4'teki `"kilitlenme (1205)"` ile birebir değil; (e) 409 `client-update-required` → fabrikanın `PersonBalance:MinClientVersion`'ı bağlanmadı (Görev 1 `PersonBalanceOptions.Section`).

- [ ] **Adım 5: Regresyon — ilişkisel küme (Docker)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceScopeRuleRelationalTests|FullyQualifiedName~CustomerBalanceConcurrencyTests|FullyQualifiedName~PersonBalanceReversalRaceTests|FullyQualifiedName~PersonBalanceReversalDeadlockTests|FullyQualifiedName~PersonBalanceReversalLegMoveRaceTests|FullyQualifiedName~PersonGroupHookRelationalTests"`
Expected: hepsi PASS, değiştirilmeden (kaynak değişmedi; satır kipinin yarışları ve S1'in grup geri alması yarışları aynen). Yeni iki sınıf `[Collection(SqlServerCollection.Name)]` taşıdığı için CI'da `ShardingTestFramework` onları metot başına ilişkisel parçalara kendiliğinden dağıtır; elle atama yok.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawRaceTests.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawDeadlockTests.cs
git commit -F - <<'MSG'
test(balance): havuzlu apply yarışlarını gerçek SQL Server'da kanıtla

Kişi kilidi farklı anahtarlı eşzamanlı apply'larda havuzu eksiye düşürmez
(taze planda no-balance); aynı anahtarla yarışta tek bacak kümesi yazılır ve
kaybeden kazananı oynatır; araya giren üçüncü yazımdan sonra ikinci deneme
Lookup'la kazananı bulur; ilk denemede havuzu tüketen aynı anahtarlı rakip
no-balance yerine oynatılır; üç çakışmada 503 balance-busy, hiçbir şey yazılmaz
ve sayaç artar; çapanın bakiye satırı açma yarışı ve 1205 (kayıt ve okuma)
baştan kurulur (ortak bakiye §8.2-1, -2, -10; §4.2 adım 6). InMemory bunları
kanıtlayamaz: tekil indeks, atomik kayıt ve komut kancası yok.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 15: Testcontainers 6, 7, 8 (üyelik yarışları) (spec §8.2-6, -7, -8; §4.2-2, §4.11, M6)

**Files:**
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawMembershipRaceTests.cs`
- Kaynak dosyası değişmez.

Kod değişikliği beklenmez: Görev 2 (`ResolveDrawSetAsync`, kural 3a/3b, `W`'nin asıl kayda çözülmesi), Görev 4 (`DrawAsync`: izlenen yükleme, üyeliğin ikinci okuması, `P`'nin jeton dokunuşu, baştan kurma) ve Görev 7 (havuzlu apply) bu yarışları kapatır; S1'in `PersonGroupSaveHook`'u (`GroupId` değişince bakiye jetonu) ve iki işin "çakışan grubu geri al ve say" döngüleri (`CustomerIdentityMergeJob.cs:287-309`, `CustomerGroupingJob.cs:166-189`; bileşen işlemi `:230-243`, kayıt `:241`) değişmeden kullanılır. Bu görev onları gerçek SQL Server'da kanıtlar — iki işin döngüsü de ters yön testiyle (`Tc7_apply_once…`, `Tc8_apply_once…`) çalıştırılır. Bir test kırmızı kalırsa önce sistematik hata ayıklama: yarış sırayla (kanca) kurulur, zamanlamayla değil.

**Not (spec ↔ kod).** §8.2-6 üç konumu ister (bakiye yüklemesinin önünde, üyeliğin iki okuması arasında, ikinci okumadan sonra). İskelet fark 17'ye göre üretim koduna test kancası EKLENMEZ; konumlar test tarafında kurulur — (i) ve (ii) `ReadQueryHook` zinciriyle, (iii) `RaceSaveHook` ile. Yeni bir spec ↔ kod farkı yok; testler Görev 4'ün "Okuma sırası" paragrafına (komut düzeyi) dayanır:

- WPF yolunda bir denemede (a) `CanonicalOfAsync` ve (b) varlık sorgusu, (c) `MembersAsync` başı, (d) gruplu ise üyeler — hepsi `FROM [WpfCustomerProjections]`; (e) izlemesiz bakiyeler — denemenin **1.** `FROM [CustomerBalances]` okuması; (5) izlenen yükleme — **2.** `FROM [CustomerBalances]`; (6) üyeliğin ikinci okuması (c)(d)(e) yeniden; sonra `SaveChanges`. Havuzlu apply'ın denetleyicisi `DrawAsync`'ten önce `CustomerBalances` okumaz (anahtarlı Lookup `FROM [CustomerBalanceTransactions]` okur; metin `FROM [CustomerBalances]`'ı içermez).
- (i) "bakiye yüklemesinin önünde": zincir 1. bakiye okumasında kurulur, ayırma 2.'sinden (izlenen yükleme) hemen önce commit eder → izlenen yükleme yeni jetonu okur, ikinci okuma farkı görür. (ii) "iki üyelik okuması arasında": ayırma izlenen yüklemeden SONRA, ikinci okumanın ilk projeksiyon sorgusundan hemen önce → yine ikinci okuma görür (izlenen jeton eskidir). (i) ve (ii)'de kanıt 409 ile `Separated == true`'nun birlikteliğidir: ikinci okuma yoksa izlenen yüklemeden sonra hiçbir `FROM [WpfCustomerProjections]` okuması koşmaz (`PersonGroupSaveHook` değişen projeksiyon yokken sorgulamaz, `SaveChanges` partisi bu metni içermez), (ii)'nin üçüncü halkası hiç tetiklenmez. `ConcurrencyFailures == 0` yalnız destekleyici sinyaldir: farkın kayıttan önce görüldüğünü gösterir. (iii) "ikinci okumadan sonra": `RaceSaveHook` apply'ın `SaveChanges`'inde → senkronun kancası B'nin bakiye jetonunu ilerletir, apply'ın UPDATE'i tutmaz (`ConcurrencyFailures == 1`).
- Ters yön (§4.11 "Apply kancanın yüklemesiyle senkronun kaydı arasında kaydedilirse senkron çakışır"): `RaceSaveHook` `base.SaveChangesAsync`'in içinde tetiklenir (`LicenseDbContext.cs:241`), senkronun `PersonGroupSaveHook`'u B'nin bakiye satırını ondan önce yüklemiştir (`:237`) — kanca tam spec'in andığı pencerededir. Senkron, temizle-yeniden-uygula döngüsüyle (`LicensesWpfCustomersSyncController.cs:256-298`, `MaxAttempts = 2`) taze okumayla tamamlar.
- TC7'nin iki yönü (§8.2-7): birleşme apply'ın kaydında commit eder (apply baştan kurar, para asıl kayıttan) ve apply birleşmenin kaydında commit eder (iş grubu geri alır ve `Report.FailedGroups`'ta sayar, `CustomerIdentityMergeJob.cs:191-193`, `:287-309`). İkinci yönde birleşmenin işlemi (`:452`) kancada açıktır ama yalnız okumuştur (READ COMMITTED, satır kilidi yok); apply engellenmez.
- TC8'in iki yönü (§8.2-8, §4.11 "birleştirme ve gruplama işleri çakışan grubu geri alıp sayar"): `group-customers` apply'ın kaydında commit eder (apply baştan kurar) ve apply gruplama bileşeninin kaydında commit eder. İkinci yönde kanca `CustomerGroupingJob.ApplyAsync`'in açık işleminde (`:232`), `SaveChangesAsync`'in (`:241`) içinde tetiklenir: gruplamanın `PersonGroupSaveHook`'u A ve B'nin bakiye satırlarını işlem içinde yalnız okumuştur (READ COMMITTED, yazım yok), apply engellenmez; apply B'den çekip A'ya dokunarak commit eder, gruplamanın UPDATE'i ilerlemiş jetona çarpar, bileşen geri alınır ve `FailedComponents`'te sayılır (`:174-178`).
- Kural 3b taze veriyle (spec §4.0-3b; Görev 4 Adım 5 — `DrawAsync`'in `nowPositive` denetimi; Görev 4 Not "Kural 3b izlenen satırlarla yeniden" kanıtı bu göreve bırakır): `S∖P`'nin izlemesiz okumada (e) eksi olan üyesi, (e) ile izlenen yükleme (5) arasında commit eden bir iadeyle artıya geçer. Zincir (i)'deki gibidir: 1. bakiye okuması zinciri kurar, iade 2.'sinin (izlenen yükleme) hemen önünde commit eder. Üyelik değişmez (`MembershipChanged` yalnız `(WpfCustomerId, Purged)` ve numarayı karşılaştırır, bakiyeyi değil), yani ikinci okuma baştan kurmaz; 409'u yalnız `nowPositive` verir. Denetim yoksa Σ = 100 + min(0, 50) = 100 → 200 ve B = 50.
- TC7'nin olağan kipi (§8.2-7 "Apply ∥ `CustomerIdentityMergeJob`"; prod'da `merge-customer-identities`, `atTwins: false`): grup birliği yok (`CustomerIdentityMergeJob.cs:511-512` yalnız `atTwins`'te). Kopyanın asıl kaydı başka kişideyse apply W'deki kopyayı asıl kayda çözer ve kural 3a ile 409 döner; başka kişinin parasından çekmez. Kurgu iki nedenle "@" ikizinden ayrıdır: (1) olağan kipte iki asıl kayıt yalnız saklı `IdentityKey` bayatsa aynı gruba düşer (tekil indeks saklı anahtara bakar, `LicenseDbContext.cs:944-947`; iş hesaplanan anahtarla gruplar, `CustomerIdentityMergeJob.cs:352`; `Username` setter'ı anahtarı hep yeniden türettiği için bayat anahtar ham SQL'le kurulur, emsal `IdentityKeyRepairJobTests`); (2) asıl kayıt grupsuz olsaydı alan aktarımı kopyanın numarasını boş hedefe doldururdu (`CustomerFieldMerge.cs:54-56`, damgasız birimde `Unit` → `Fill`, `:145`) ve X kişiye katılırdı — "S dışında" durumu ancak X'in kendi numarası varken kurulur.
- Bugünkü kodla dallar belirlenimlidir ama iki dal da kabul edilir (brief): TC7'nin ilkinde `UnionGroupsAsync` grupsuz asıl kaydı kopyanın numarasına alır (`CustomerIdentityMergeJob.cs:550`, hedef `groupBefore ?? kopyaların en küçüğü`) → 200 dalı; TC8'de `PlanOf`'un hedefi kayıtlı tarafın numarasıdır (`CustomerGroupingJob.cs:205`) → A ve B g0'a taşınır, jetonları ilerler → 409 dalı. Dal, sonuçtan değil veriden seçilir ve durumla bakiye birlikte iddia edilir.

- [ ] **Adım 1: Testcontainers 6, 7, 8'i yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawMembershipRaceTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Havuzlu apply ∥ üyelik değişimi gerçek SQL Server'da (spec 2026-10-11 §8.2-6, -7, -8; §4.2-2, §4.11, M6): ayırma
/// senkronu apply'ın bakiye yüklemesinin önünde, iki üyelik okumasının arasında ve ikinci okumadan sonra commit eder —
/// ayrılan para tutan satırdan ayırmadan sonra çekim yok; apply önce commit ederse senkron taze okumayla yeniden uygular.
/// Kural 3b taze veriyle: W dışındaki eksi üye iki bakiye okuması arasında artıya geçerse 409, çekim yok.
/// Kimlik birleşmesi ("@" ikizi birliği ve olağan kip) ve <c>group-customers</c> apply'ın kaydında commit eder; apply birleşmenin ya
/// da gruplama bileşeninin kaydında commit ederse iş grubu/bileşeni geri alır ve sayar. Yarış sırayla kurulur: okuma konumları <see cref="ReadQueryHook"/>
/// zinciriyle, kayıt <see cref="RaceSaveHook"/> ile (üretim kodunda test kancası yok). InMemory'de komut kancası, atomik
/// kayıt ve işlem yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceDrawMembershipRaceTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private PersonBalanceRelationalApiFactory _factory = null!;

    public PersonBalanceDrawMembershipRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() =>
        _factory = new PersonBalanceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Queries.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static bool IsBalanceRead(string sql) => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal);

    private static bool IsProjectionRead(string sql) =>
        sql.Contains("FROM [WpfCustomerProjections]", StringComparison.Ordinal);

    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    /// <summary>Apply'ın sonucu: durum, (başarısızsa) problem başlığı, (başarılıysa) uygulanan tutar.</summary>
    private sealed record ApplyOutcome(HttpStatusCode Status, string? Title, decimal? Applied);

    /// <summary>
    /// TestServer, sunucu tarafında yakalanmayan istisnayı istemciye fırlatır; prod'da 500 olurdu — başlık yerine en
    /// içteki istisna, düşen koşunun mesajı nedeni göstersin (emsal <c>PersonBalanceReversalRaceTests</c>).
    /// </summary>
    private static async Task<ApplyOutcome> StatusAsync(Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await call();
        }
        catch (Exception e)
        {
            var root = e.GetBaseException();
            return new ApplyOutcome(HttpStatusCode.InternalServerError, $"{root.GetType().Name}: {root.Message}", null);
        }
        if (!resp.IsSuccessStatusCode) return new ApplyOutcome(resp.StatusCode, await Kit.TitleAsync(resp), null);
        var body = await resp.Content.ReadFromJsonAsync<ApplyDto>();
        return new ApplyOutcome(resp.StatusCode, null, body!.AppliedAmount);
    }

    // ── TC6: ayırma senkronu ∥ apply (§8.2-6) ─────────────────────────────────

    /// <summary>
    /// TC6'nın ortak kurulumu: kişi g = {A (çapa, 0), B (100)}, havuz kipi; apply A'dan 50, <c>W</c> = {A, B}, anahtar
    /// K. Rakip: B'yi ayıran WPF senkronu (<c>GroupId = NULL</c> partisi).
    /// </summary>
    private sealed class Separation
    {
        public Separation(Kit kit, Guid a, Guid b)
        {
            Kit = kit;
            A = a;
            B = b;
        }

        public Kit Kit { get; }
        public Guid A { get; }
        public Guid B { get; }
        public Guid Key { get; } = Guid.NewGuid();
        public bool Separated { get; private set; }

        public Task<HttpResponseMessage> ApplyAsync() => Kit.ApplyAsync(A, 50m, 50m, Key, null, [A, B]);

        public async Task SeparateAsync()
        {
            (await Kit.SyncGroupAsync(B, null)).EnsureSuccessStatusCode();
            Separated = true;
        }
    }

    private async Task<Separation> SeparationAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 100m);
        await kit.EnablePoolAsync();
        return new Separation(kit, a, b);
    }

    /// <summary>
    /// Ayırma apply'ın planından önce commit etti: taze denemede B kişide değil ama <c>W</c>'de → kural 3a, 409
    /// <c>customer-not-synced</c>. Ayrılan para tutan satırdan ayırmadan sonra çekim yok.
    /// </summary>
    private static async Task ShouldNotHaveDrawnFromSeparatedAsync(Separation s, ApplyOutcome result)
    {
        s.Separated.Should().BeTrue("rakip ayırma kancanın konumunda commit etmeli");
        result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
            "taze denemede B W'de ama S(A)'da değil (kural 3a)");
        (await s.Kit.BalanceAsync(s.B)).Should().Be(100m, "ayrılan satırdan ayırmadan sonra çekim yok");
        (await s.Kit.BalanceAsync(s.A)).Should().Be(0m);
        (await s.Kit.GroupRowsAsync(s.Key)).Should().BeEmpty("K anahtarlı hiçbir satır yazılmadı");
        (await s.Kit.ProjectionAsync(s.B)).GroupId.Should().BeNull("ayırma kaydedildi");
        foreach (var row in new[] { s.A, s.B })
            (await s.Kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>(i) Ayırma izlenen bakiye yüklemesinin hemen önünde: ikinci üyelik okuması farkı görür (§4.2-2).</summary>
    [Fact]
    public async Task Tc6_i_ayirma_bakiye_yuklemesinden_once()
    {
        var s = await SeparationAsync();
        // 1. bakiye okuması (üyeliğin izlemesiz bakiyeleri) zinciri kurar; 2.'si (izlenen yükleme) ayırmayı önüne alır.
        _factory.Queries.Arm(IsBalanceRead, () =>
        {
            _factory.Queries.Arm(IsBalanceRead, s.SeparateAsync);
            return Task.CompletedTask;
        });

        var result = await StatusAsync(s.ApplyAsync);
        _factory.Queries.Disarm();

        await ShouldNotHaveDrawnFromSeparatedAsync(s, result);
        _factory.Hook.ConcurrencyFailures.Should().Be(0,
            "fark üyeliğin ikinci okumasında görüldü; ilk deneme kayda varmadı");
    }

    /// <summary>
    /// (ii) Ayırma izlenen yüklemeden SONRA, üyeliğin ikinci okumasından hemen önce: izlenen jeton eskidir, yine de
    /// ikinci okuma farkı görür ve plan kayda varmadan baştan kurulur.
    /// </summary>
    [Fact]
    public async Task Tc6_ii_ayirma_iki_uyelik_okumasi_arasinda()
    {
        var s = await SeparationAsync();
        // 1. bakiye okuması → 2. bakiye okuması (izlenen yükleme) → sonraki projeksiyon okuması = ikinci üyelik okumasının
        // başı; ayırma onun hemen önünde commit eder.
        _factory.Queries.Arm(IsBalanceRead, () =>
        {
            _factory.Queries.Arm(IsBalanceRead, () =>
            {
                _factory.Queries.Arm(IsProjectionRead, s.SeparateAsync);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });

        var result = await StatusAsync(s.ApplyAsync);
        _factory.Queries.Disarm();

        await ShouldNotHaveDrawnFromSeparatedAsync(s, result);
        _factory.Hook.ConcurrencyFailures.Should().Be(0,
            "ikinci okuma olmasaydı kayıtta jeton çakışması yakalardı; burada fark kayıttan önce görülmeli");
    }

    /// <summary>
    /// (iii) Ayırma ikinci okumadan sonra, apply'ın kaydında: senkronun kancası B'nin bakiye jetonunu ilerletir, apply'ın
    /// UPDATE'i tutmaz ve plan baştan kurulur (§4.11, M6).
    /// </summary>
    [Fact]
    public async Task Tc6_iii_ayirma_ikinci_okumadan_sonra_kayitta()
    {
        var s = await SeparationAsync();
        _factory.Hook.Once(() => s.SeparateAsync());

        var result = await StatusAsync(s.ApplyAsync);
        _factory.Hook.Disarm();

        await ShouldNotHaveDrawnFromSeparatedAsync(s, result);
        _factory.Hook.ConcurrencyFailures.Should().Be(1,
            "apply'ın ilk kaydı ilerlemiş jetona çarptı; ikinci deneme kural 3a'da yazmadan döner");
    }

    /// <summary>
    /// Ters sıra: apply, senkronun kancasının bakiye yüklemesiyle senkronun kaydı arasında commit eder. Sıra "apply
    /// ayırmadan önce oldu"dur (B o an kişideydi, çekim meşru); senkron B'nin jetonuna çarpar ve temizle-yeniden-uygula
    /// döngüsüyle taze okumayla tamamlar (§4.11).
    /// </summary>
    [Fact]
    public async Task Tc6_senkron_cakisirsa_taze_okumayla_yeniden_uygular()
    {
        var s = await SeparationAsync();
        var atSyncSave = false;
        ApplyOutcome? apply = null;
        _factory.Hook.Once(async saving =>
        {
            // Kanca senkronun kaydında olmalı: kaydedilen bağlamda B'nin ayrılan projeksiyonu var.
            atSyncSave = saving.ChangeTracker.Entries<WpfCustomerProjection>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Id == s.B && e.Entity.GroupId == null);
            apply = await StatusAsync(s.ApplyAsync);
        });

        var sync = await s.Kit.SyncGroupAsync(s.B, null);
        _factory.Hook.Disarm();

        atSyncSave.Should().BeTrue("apply senkronun SaveChanges'inde commit etmeli");
        apply.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 50m), "apply ayırmadan önce commit etti");
        sync.StatusCode.Should().Be(HttpStatusCode.OK, await sync.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().Be(1,
            "senkronun ilk kaydı apply'ın ilerlettiği B jetonuna çarptı; ikinci deneme taze okumayla tuttu");
        (await s.Kit.ProjectionAsync(s.B)).GroupId.Should().BeNull("senkron partiyi taze okumayla yeniden uyguladı");
        (await s.Kit.BalanceAsync(s.B)).Should().Be(50m);
        (await s.Kit.BalanceAsync(s.A)).Should().Be(0m);
        (await s.Kit.GroupRowsAsync(s.Key)).Select(r => (r.WpfCustomerId, r.Amount))
            .Should().Equal((s.A, 0m), (s.B, -50m));
        foreach (var row in new[] { s.A, s.B })
            (await s.Kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    // ── Kural 3b taze veriyle (§4.0-3b; Görev 4 Adım 5 — DrawAsync'in nowPositive denetimi) ──

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.CountAsync(t => t.LicenseId == kit.LicenseId));

    /// <summary>
    /// g = {A (çapa, 0), B (100), N (−30)}, havuz kipi; apply A'dan 50, <c>W</c> = {A, B}. Kümenin izlemesiz okumasında
    /// (denemenin 1. bakiye okuması) N eksidir: <c>S∖P</c>'nin kilitlenen eksisi, kural 3b'yi tetiklemez. N'ye 80'lik iade
    /// izlenen yüklemenin (2. bakiye okuması) hemen önünde commit eder: izlenen N = 50, W'de görülmeyen para. Üyelik
    /// değişmediği için ikinci okuma baştan kurmaz; 409'u yalnız <c>nowPositive</c> denetimi verir. Denetim olmasaydı
    /// Σ = 100 + min(0, 50) = 100 → 200 ve B = 50.
    /// </summary>
    [Fact]
    public async Task Kural3b_W_disi_eksi_uye_izlenen_yuklemede_artiya_gecerse_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 100m);
        var n = await kit.MemberAsync(g, -30m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        var before = await TransactionCountAsync(kit);
        var funded = false;
        // 1. bakiye okuması (izlemesiz, N = −30) zinciri kurar; 2.'si (izlenen yükleme) iadeyi önüne alır.
        _factory.Queries.Arm(IsBalanceRead, () =>
        {
            _factory.Queries.Arm(IsBalanceRead, async () =>
            {
                await kit.FundAsync(n, 80m);
                funded = true;
            });
            return Task.CompletedTask;
        });

        var result = await StatusAsync(() => kit.ApplyAsync(a, 50m, 50m, key, null, [a, b]));
        _factory.Queries.Disarm();

        funded.Should().BeTrue("iade izlenen yüklemenin önünde commit etmeli");
        result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
            "izlenen yüklemede N > 0: S'de olup W'de olmayan pozitif canlı üye (kural 3b)");
        _factory.Hook.ConcurrencyFailures.Should().Be(0, "fark kayda varılmadan, izlenen satırlarda görüldü");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(100m, "W'de görülmeyen para varken çekim yok");
        (await kit.BalanceAsync(n)).Should().Be(50m);
        (await kit.GroupRowsAsync(key)).Should().BeEmpty("K anahtarlı hiçbir satır yazılmadı");
        (await TransactionCountAsync(kit)).Should().Be(before + 1, "yalnız iadenin hareketi eklendi");
        foreach (var row in new[] { a, b, n })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    // ── TC7: apply ∥ kimlik birleşmesi ("@" ikizi birliği) (§8.2-7) ────────────

    /// <summary>
    /// Spec Örnek 1'in "@" ikizi hâli: kişi g = {T (çapa, 0), I ("@ad", instagram, 100)}; grupsuz asıl kayıt X ("ad",
    /// instagram, bakiye satırı yok). Birleşmede I, X'in kopyası olur; I'nin parası ve hareketleri X'e taşınır, X
    /// birlikle g'ye katılır (<c>UnionGroupsAsync</c>).
    /// </summary>
    private sealed record Twins(Kit Kit, string Group, Guid T, Guid I, Guid X);

    private async Task<Twins> TwinsAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var name = $"u{Guid.NewGuid():N}"[..16];
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram", username: "@" + name);
        var x = await kit.MemberAsync(null, balanceRow: false, platform: "instagram", username: name);
        await kit.EnablePoolAsync();
        return new Twins(kit, g, t, i, x);
    }

    private async Task<CustomerIdentityMergeJob.Report> MergeTwinsAsync(Kit kit, bool atTwins = true)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CustomerIdentityMergeJob>()
            .RunAsync(kit.LicenseId, apply: true, default, atTwins: atTwins);
    }

    /// <summary>
    /// Birleşme apply'ın kaydında commit eder: birleştirici I'nin bakiye satırını 0'lar (jeton ilerler), apply'ın
    /// UPDATE'i tutmaz; taze denemede I (kopya) asıl kayıt X'e çözülür. X birlikle kişideyse para X'ten çekilir; değilse
    /// kural 3a. İki dalda da kopyaya K grubunun satırı yazılmaz.
    /// </summary>
    [Fact]
    public async Task Tc7_kimlik_birlesmesi_kayit_aninda_apply_parayi_asil_kayittan_ceker()
    {
        var tw = await TwinsAsync();
        var key = Guid.NewGuid();
        _factory.Hook.Once(async () => (await MergeTwinsAsync(tw.Kit)).FailedGroups.Should().Be(0));

        var result = await StatusAsync(() => tw.Kit.ApplyAsync(tw.T, 50m, 50m, key, null, [tw.T, tw.I]));
        _factory.Hook.Disarm();

        (await tw.Kit.ProjectionAsync(tw.I)).MergedIntoId.Should().Be(tw.X, "'@'lı satır '@'sız asıl kayda birleşti");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0,
            "birleştirici I'nin bakiye satırını 0'ladı; apply'ın ilk UPDATE'i jetona çarptı");
        (await tw.Kit.BalanceAsync(tw.I)).Should().Be(0m, "para görünmez kopyada kalmaz");
        var rows = await tw.Kit.GroupRowsAsync(key);
        rows.Should().NotContain(r => r.WpfCustomerId == tw.I, "kopyaya K grubunun satırı yazılmaz");
        if ((await tw.Kit.ProjectionAsync(tw.X)).GroupId == tw.Group)
        {
            result.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 50m), "X birlikle S(T)'de; W'nin I'si X'e çözüldü");
            (await tw.Kit.BalanceAsync(tw.X)).Should().Be(50m, "100 birleşmeyle X'e geçti, 50 çekildi");
            rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((tw.T, 0m), (tw.X, -50m));
        }
        else
        {
            result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
                "X S(T)'de değil ama W'de (kural 3a)");
            (await tw.Kit.BalanceAsync(tw.X)).Should().Be(100m);
            rows.Should().BeEmpty();
        }
        foreach (var row in new[] { tw.T, tw.I, tw.X })
            (await tw.Kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// Olağan kip (<c>merge-customer-identities</c>, <c>atTwins: false</c>; grup birliği yok): kişi g = {T (çapa, 0),
    /// I (instagram, 100)}; asıl kayıt X (instagram, bakiye satırı yok) BAŞKA kişide, g2'de. I'nin adı X'inkiyle aynı,
    /// saklı <c>IdentityKey</c>'i bayat (B1): tekil indeks saklı anahtara bakar, iş hesaplanan anahtarla gruplar
    /// (<c>CustomerIdentityMergeJob.cs:352</c>). X önce kurulur ve <c>UpdatedAt</c>'i geriye çekilir: sıralamada asıl kayıt
    /// (<c>:399-400</c>). X'in numarası dolu olduğu için alan aktarımı onu ezmez (<c>CustomerFieldMerge.cs:54-56</c>).
    /// Birleşme apply'ın kaydında commit eder; taze denemede W'nin I'si X'e çözülür, X S(T)'de değil → kural 3a, 409.
    /// Başka kişinin parasından (X'e taşınan 100) çekim yok.
    /// </summary>
    [Fact]
    public async Task Tc7_olagan_kip_kopyanin_asil_kaydi_baska_kisideyse_kural3a_cekim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var name = $"u{Guid.NewGuid():N}"[..16];
        var g = Kit.NewGroup();
        var g2 = Kit.NewGroup();
        var x = await kit.MemberAsync(g2, balanceRow: false, platform: "instagram", username: name);
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram", username: $"v{Guid.NewGuid():N}"[..16]);
        // Ham SQL: Username setter'ı IdentityKey'i yeniden türetirdi (WpfCustomerProjection.cs:20-27) ve tekil indeks
        // (LicenseId, Platform, IdentityKey) X ile çakışırdı. Emsal IdentityKeyRepairJobTests.
        (await kit.Sales.ReadDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE WpfCustomerProjections SET Username = {name} WHERE Id = {i}"))).Should().Be(1);
        var earlier = DateTimeOffset.UtcNow.AddHours(-1);
        (await kit.Sales.ReadDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE WpfCustomerProjections SET UpdatedAt = {earlier} WHERE Id = {x}"))).Should().Be(1);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        _factory.Hook.Once(async () => (await MergeTwinsAsync(kit, atTwins: false)).FailedGroups.Should().Be(0));

        var result = await StatusAsync(() => kit.ApplyAsync(t, 50m, 50m, key, null, [t, i]));
        _factory.Hook.Disarm();

        (await kit.ProjectionAsync(i)).MergedIntoId.Should().Be(x, "aynı ad, olağan kipte X'e birleşti");
        (await kit.ProjectionAsync(x)).GroupId.Should().Be(g2, "olağan kipte grup birliği yok; dolu numara ezilmez");
        result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
            "W'nin I'si X'e çözüldü; X S(T)'de değil (kural 3a)");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0,
            "birleştirici I'nin bakiye satırını 0'ladı; apply'ın ilk UPDATE'i jetona çarptı");
        (await kit.BalanceAsync(x)).Should().Be(100m, "başka kişinin parasından çekim yok");
        (await kit.BalanceAsync(i)).Should().Be(0m, "para görünmez kopyada kalmaz");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.GroupRowsAsync(key)).Should().BeEmpty("K anahtarlı hiçbir satır yazılmadı");
        foreach (var row in new[] { t, i, x })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// Ters sıra: apply birleşmenin kaydında commit eder (I o an asıl kayıt, çekim meşru). Birleşmenin kaydı I'nin
    /// apply'ın ilerlettiği jetonuna çarpar; iş grubu geri alır ve sayar (<c>CustomerIdentityMergeJob.cs:297-309</c>),
    /// birleşme ve grup birliği olmaz. Yeniden koşu kalanı bulur.
    /// </summary>
    [Fact]
    public async Task Tc7_apply_once_commit_ederse_birlesme_grubu_atlanir_ve_sayilir()
    {
        var tw = await TwinsAsync();
        var key = Guid.NewGuid();
        var atMergeSave = false;
        ApplyOutcome? apply = null;
        _factory.Hook.Once(async saving =>
        {
            // Kanca birleşmenin kaydında olmalı: kaydedilen bağlamda I, X'e yönlenmiş.
            atMergeSave = saving.ChangeTracker.Entries<WpfCustomerProjection>()
                .Any(e => e.Entity.Id == tw.I && e.Entity.MergedIntoId == tw.X);
            apply = await StatusAsync(() => tw.Kit.ApplyAsync(tw.T, 50m, 50m, key, null, [tw.T, tw.I]));
        });

        var report = await MergeTwinsAsync(tw.Kit);
        _factory.Hook.Disarm();

        atMergeSave.Should().BeTrue("apply birleşme grubunun SaveChanges'inde commit etmeli");
        apply.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 50m), "apply birleşmeden önce commit etti");
        report.Groups.Should().Be(1);
        report.FailedGroups.Should().Be(1, "grup I'nin ilerlemiş jetonuna çarptı; geri alındı ve sayıldı");
        _factory.Hook.ConcurrencyFailures.Should().Be(1, "yalnız birleşmenin kaydı çakıştı");
        (await tw.Kit.ProjectionAsync(tw.I)).MergedIntoId.Should().BeNull("birleşme geri alındı");
        (await tw.Kit.ProjectionAsync(tw.X)).GroupId.Should().BeNull("grup birliği aynı işlemde geri alındı");
        (await tw.Kit.BalanceAsync(tw.I)).Should().Be(50m);
        (await tw.Kit.BalanceRowAsync(tw.X)).Should().BeNull("birleşmenin açtığı satır geri alındı");
        (await tw.Kit.GroupRowsAsync(key)).Select(r => (r.WpfCustomerId, r.Amount))
            .Should().Equal((tw.T, 0m), (tw.I, -50m));
        foreach (var row in new[] { tw.T, tw.I, tw.X })
            (await tw.Kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    // ── TC8: apply ∥ group-customers (§8.2-8) ────────────────────────────────

    /// <summary>
    /// g1 = {A (çapa, 0), B (40)}, g0 = {C (100)}; apply A'dan 30, <c>W</c> = {A, B}. <c>group-customers</c> (A, C)
    /// apply'ın kaydında commit eder. Apply ya B'den 30 çeker (200) ya da birleşen kişide <c>W</c> dışında pozitif C
    /// görür (kural 3b, 409); hangisi olursa olsun <c>W</c> dışındaki C'den ASLA çekilmez.
    /// </summary>
    [Fact]
    public async Task Tc8_group_customers_kayit_aninda_W_disindaki_uyeden_cekim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a = await kit.MemberAsync(g1);
        var b = await kit.MemberAsync(g1, 40m);
        var c = await kit.MemberAsync(g0, 100m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        _factory.Hook.Once(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var report = await new CustomerGroupingJob(db)
                .RunAsync(kit.LicenseId, [new CustomerGroupingJob.Pair(a, c)], apply: true, default);
            report.FailedComponents.Should().Be(0);
        });

        var result = await StatusAsync(() => kit.ApplyAsync(a, 30m, 30m, key, null, [a, b]));
        _factory.Hook.Disarm();

        (await kit.ProjectionAsync(a)).GroupId.Should().Be((await kit.ProjectionAsync(c)).GroupId, "gruplama kaydedildi");
        (await kit.BalanceAsync(c)).Should().Be(100m, "W dışındaki üyeden ASLA çekilmez");
        var rows = await kit.GroupRowsAsync(key);
        rows.Should().NotContain(r => r.WpfCustomerId == c);
        // Dal veriden seçilir: A ve B g0'a taşındıysa jetonları ilerledi, apply'ın ilk kaydı tutmamalıydı.
        var moved = (await kit.ProjectionAsync(b)).GroupId == g0;
        if (!moved)
        {
            (await kit.ProjectionAsync(c)).GroupId.Should().Be(g1, "A ve B yerinde kaldıysa C g1'e geldi");
            result.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 30m),
                "A ve B'nin jetonu değişmedi; P = W ∩ S(A) = {A, B}'den çekim meşru");
            (await kit.BalanceAsync(b)).Should().Be(10m);
            rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, 0m), (b, -30m));
        }
        else
        {
            result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
                "birleşen kişide W dışında pozitif C (kural 3b)");
            _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0,
                "A ve B taşındı; jetonları ilerledi, apply baştan kurdu");
            (await kit.BalanceAsync(b)).Should().Be(40m);
            rows.Should().BeEmpty();
        }
        foreach (var row in new[] { a, b, c })
        {
            (await kit.BalanceAsync(row)).Should().BeGreaterThanOrEqualTo(0m, "hiçbir satır eksiye inmez");
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        }
    }

    /// <summary>
    /// Ters sıra: apply gruplama bileşeninin kaydında commit eder (A ve B o an g1'de, çekim meşru). Gruplamanın
    /// <c>PersonGroupSaveHook</c>'u A ve B'nin bakiye satırlarını apply'dan önce yüklemişti; gruplamanın UPDATE'i
    /// apply'ın ilerlettiği jetona çarpar, bileşen geri alınır ve sayılır (<c>CustomerGroupingJob.cs:166-189</c>).
    /// Yeniden koşu kalanı bulur.
    /// </summary>
    [Fact]
    public async Task Tc8_apply_once_commit_ederse_gruplama_bileseni_geri_alinir_ve_sayilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a = await kit.MemberAsync(g1);
        var b = await kit.MemberAsync(g1, 40m);
        var c = await kit.MemberAsync(g0, 100m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        var atGroupingSave = false;
        ApplyOutcome? apply = null;
        _factory.Hook.Once(async saving =>
        {
            // Kanca gruplamanın kaydında olmalı: kaydedilen bağlamda A g0'a taşınmış.
            atGroupingSave = saving.ChangeTracker.Entries<WpfCustomerProjection>()
                .Any(e => e.Entity.Id == a && e.Entity.GroupId == g0);
            apply = await StatusAsync(() => kit.ApplyAsync(a, 30m, 30m, key, null, [a, b]));
        });

        CustomerGroupingJob.Report report;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            report = await new CustomerGroupingJob(db)
                .RunAsync(kit.LicenseId, [new CustomerGroupingJob.Pair(a, c)], apply: true, default);
        }
        _factory.Hook.Disarm();

        atGroupingSave.Should().BeTrue("apply gruplama bileşeninin SaveChanges'inde commit etmeli");
        apply.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 30m), "apply gruplamadan önce commit etti");
        report.Components.Should().Be(1);
        report.FailedComponents.Should().Be(1, "bileşen A/B'nin ilerlemiş jetonuna çarptı; geri alındı ve sayıldı");
        _factory.Hook.ConcurrencyFailures.Should().Be(1, "yalnız gruplamanın kaydı çakıştı");
        (await kit.ProjectionAsync(a)).GroupId.Should().Be(g1, "gruplama geri alındı");
        (await kit.ProjectionAsync(b)).GroupId.Should().Be(g1);
        (await kit.ProjectionAsync(c)).GroupId.Should().Be(g0);
        (await kit.BalanceAsync(b)).Should().Be(10m);
        (await kit.BalanceAsync(c)).Should().Be(100m, "W dışındaki üyeden ASLA çekilmez");
        (await kit.GroupRowsAsync(key)).Select(r => (r.WpfCustomerId, r.Amount))
            .Should().Equal((a, 0m), (b, -30m));
        foreach (var row in new[] { a, b, c })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }
}
```

Derleme notları (worktree'de doğrulandı): `RaceSaveHook.Once(Func<Task>)` / `Once(Func<DbContext, Task>)` ve `ConcurrencyFailures` (`TestHelpers/RaceSaveHook.cs:19-29`), `ReadQueryHook.Arm(Func<string,bool>, Func<Task>)` (`TestHelpers/ReadQueryHook.cs:19`; gövde içinden `Arm` zinciri kurar), `SqlServerCollection.Name` / `SqlServerContainerFixture.CreateDatabaseAsync()` (`TestHelpers/SqlServerContainerFixture.cs:164`, `:357-359`), `Kit.MemberAsync/BalanceAsync/BalanceRowAsync/LedgerHoldsAsync/ProjectionAsync/SyncGroupAsync/TitleAsync/NewGroup` (`PersonBalanceTestKit.cs`), `CustomerIdentityMergeJob.RunAsync(Guid, bool, CancellationToken, bool atTwins)` ve `Report.Groups/FailedGroups` (`CustomerIdentityMergeJob.cs:191-193`, `:287`), `CustomerGroupingJob(LicenseDbContext, ILogger?)`, `Pair`, `RunAsync(Guid, IReadOnlyList<Pair>, bool, CancellationToken)`, `Report.Components/FailedComponents` (`CustomerGroupingJob.cs:90`, `:99`, `:115-117`, `:123`); `CustomerGroupingJob.Report report;` bloğun içinde kesin atanır (istisna testi düşürür). Görev 1'den: `PersonBalanceRelationalApiFactory` (`Hook`, `Queries`), `kit.EnablePoolAsync()`, 6 parametreli `kit.ApplyAsync(…, memberIds)`, `kit.GroupRowsAsync(anchorId)` (çapa ilk, sonra `WpfCustomerId` "N" ordinal — `Equal` iddialarının sırası buna dayanır). Görev 2'den: `PersonBalance.CodeCustomerNotSynced`. `[A, B]` koleksiyon ifadesi `IReadOnlyCollection<Guid>`'e bağlanır (6 argümanla yalnız yeni aşırı yükleme eşleşir). Tuple eşitliği `decimal` değer eşitliğidir (`-50.00m == -50m`). `kit.FundAsync(Guid, decimal, …)` (`PersonBalanceTestKit.cs:78`; kendi kapsamında `WithDbAsync` + `SaveChangesAsync`, kanca gövdesinde yeniden girmez — `ReadQueryHook` `_running`), `kit.Sales.ReadDbAsync<T>(Func<LicenseDbContext, Task<T>>)` (`SalesCommandTestKit.cs:469`), `ExecuteSqlInterpolatedAsync` (`Microsoft.EntityFrameworkCore`, dosyada var; enterpolasyon parametreye dönüşür). Tablo adı `WpfCustomerProjections` (emsal `IdentityKeyRepairJobTests.cs:91`).

- [ ] **Adım 2: Koştur (Docker açık)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawMembershipRaceTests"`
Expected: 10 test PASS. Docker yerelde "kapalı" gibi düşerse: `docker context ls`, sonra **PowerShell'den** `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'` (boru adı `docker_engine` da olabilir — `docker version` hangisinde yanıt veriyorsa) ve `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test …` aynı filtreyle.

Kırmızı kalırsa — hangi sözleşme:
- (a) `Tc6_i` / `Tc6_ii`'de `Separated = false` → zincir beklenen okumaya varmadı: `DrawAsync`'in okuma sırası Görev 4'ün "Okuma sırası" paragrafından sapmış (ör. `ResolveDrawSetAsync` fazladan bir `CustomerBalances` okuması yapıyor) ya da Görev 7'nin `ApplyPoolAsync`'i `DrawAsync`'ten önce bakiye okuyor. Testi değil sırayı düzelt; sıra bilerek değiştiyse zinciri yeni sıraya göre kur ve commit gövdesinde yaz.
- (b) `Tc6_i`'de 200 ve B = 50 → üyeliğin ikinci okuması (Görev 4 Adım 5 — `DrawAsync`'in üyeliğin ikinci okuması) yok: izlenen yükleme ayırmadan SONRAKİ jetonu okudu, kanca bu konumu yakalayamaz — tam da §4.2-2'nin kapattığı açık. `Tc6_ii`'de `Separated = false` (ve 200) → izlenen yüklemeden sonra projeksiyon okuması yok: üyeliğin ikinci okuması (Görev 4 Adım 5 — `DrawAsync`'in üyeliğin ikinci okuması) eksik ya da yüklemeden önce geliyor.
- (c) `Tc6_iii`'de 200 → senkronun `PersonGroupSaveHook`'u (S1 Görev 9, `PersonGroupSaveHook.cs:179-210`) B'nin bakiye jetonunu ilerletmiyor (B çekilen satır; UPDATE'i F02 jetonunu zaten taşır, `P`'nin dokunuşu burada rol oynamaz); `ConcurrencyFailures = 0` iken 409 → kanca apply'ın değil başka bir kaydın önünde tetiklendi (istekte erken bir `SaveChanges` var mı?).
- (d) 409 ama başlık `no-balance` → Görev 2'nin kural sırası (3a, Σ'dan önce gelmeli).
- (e) `Tc6_senkron…`'da senkron 409 → `LicensesWpfCustomersSyncController`'ın döngüsü iki kez çakıştı (apply kancada iki kez mi koştu?); `atSyncSave = false` → senkron partiden önce başka bir `SaveChanges` yapıyor.
- (f) `Tc7_kimlik…`'te I'ye satır ya da 500 → `W`'nin Id'leri asıl kayda çözülmüyor (Görev 2, iskelet fark 5: `CanonicalOfAsync`); 200 ama X = 100 ve I = −50 → baştan kurma bayat izleyiciyle yazdı (Görev 4 Adım 5 — `DrawAsync`'in deneme başındaki `ChangeTracker.Clear()`).
- (g) `Tc7_apply_once…`'de `FailedGroups = 0` ve I kopya → apply I'nin jetonunu ilerletmedi (Görev 4 Adım 5 — `DrawAsync`'in `StageDraw` yazımı ve kişi kilidinin jeton dokunuşu); `apply` 500 → kanca birleşmenin açık işleminde kilitlendi (birleşme işi kaydından önce yazar mı? `CustomerIdentityMergeJob.cs:452-532`).
- (h) `Tc8_group_customers…`'te C < 100 → `P = W ∩ S` kuralı (Görev 2 kural 4) bozuk; B g0'da (taşındı) ama 200 ve B = 10 → apply bayat üyelikle yazdı: gruplamanın `PersonGroupSaveHook`'u A/B'nin jetonunu ilerletmiyor ya da kişi kilidinin dokunuşu (Görev 4) eksik; durum ile bakiye ayrışıyorsa (200 ama B = 40, 409 ama B = 10) → yanıt ile yazım ayrışmış (Görev 7 eşlemesi).
- (i) `Tc8_apply_once…`'de `FailedComponents = 0` ve A g0'da → gruplamanın kaydı B'nin bakiye satırını jetonuyla yazmadı: `PersonGroupSaveHook.TouchBalancesAsync` (`PersonGroupSaveHook.cs:179-210`) gruplama yolunda bakiye satırını yüklemiyor; `atGroupingSave = false` → `RunAsync` bileşen kaydından önce başka bir `SaveChanges` yapıyor; `apply` 500/zaman aşımı → gruplama işlemi kancadan önce yazıp kilit tutuyor (`CustomerGroupingJob.cs:230-243`).
- (j) `Kural3b_…`'te 200 ve B = 50 → Görev 4 Adım 5'teki `nowPositive` denetimi yok ya da Σ'dan sonra geliyor; 409 ama `funded = false` → zincir izlenen yüklemeye varmadı (bkz. a); `ConcurrencyFailures > 0` → iade izlenen yüklemeden sonra commit etti (zincir bir okuma kaymış), 409'u jeton ve taze denemedeki küme kuralı 3b verdi, `nowPositive` sınanmadı.
- (k) `Tc7_olagan_kip…`'te I kopya değil (`MergedIntoId = null`) → ham SQL'den sonra iş grubu bulmadı (asıl kayıt sıralaması ya da `FindGroupsAsync`'in hesaplanan anahtarı, `CustomerIdentityMergeJob.cs:342-363`); X g'de → alan aktarımı dolu numarayı ezdi ya da olağan kipte birlik koştu; 200 ve X = 50 → kural 3a asıl kayda çözülen Id'yi S(T)'ye karşı denetlemiyor (Görev 2, `CanonicalOfAsync` sonrası); I'ye satır → `W` asıl kayda çözülmüyor (bkz. f).

- [ ] **Adım 3: İlişkisel regresyon (Docker)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDrawMembershipRaceTests|FullyQualifiedName~CustomerBalanceScopeRuleRelationalTests|FullyQualifiedName~CustomerBalanceConcurrencyTests|FullyQualifiedName~PersonBalanceReversalRaceTests|FullyQualifiedName~PersonBalanceReversalDeadlockTests|FullyQualifiedName~PersonBalanceReversalLegMoveRaceTests|FullyQualifiedName~PersonGroupHookRelationalTests"`
Expected: hepsi PASS, var olan testler DEĞİŞTİRİLMEDEN (satır kipinin yarışları ve S1'in grup geri alması bu görevden etkilenmez; bu görev yalnız test ekler). CI parçası `ShardingTestFramework` tarafından koleksiyondan otomatik seçilir; elle atama yok.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceDrawMembershipRaceTests.cs
git commit -F - <<'MSG'
test(balance): havuzlu apply'ın üyelik yarışlarını gerçek SQL Server'da kanıtla

Ayırma senkronu apply'ın bakiye yüklemesinin önünde, iki üyelik okumasının
arasında ve ikinci okumadan sonra commit ettiğinde ayrılan satırdan çekim
olmaz (409 customer-not-synced); apply önce commit ederse senkron taze
okumayla yeniden uygular. Kimlik birleşmesi ve group-customers apply'ın
kaydında commit edince apply baştan kurar, para asıl kayıttan ve yalnız
W'den çekilir; apply önce commit ederse birleşme grubu ve gruplama
bileşeni geri alınır ve sayılır (ortak bakiye §8.2-6, -7, -8; §4.2-2,
§4.11). Olağan kip birleşmede kopyanın asıl kaydı başka kişideyse apply
kural 3a ile döner, başka kişinin parasından çekmez. Kural 3b taze
veriyle: W dışındaki eksi üye iki bakiye okuması arasında artıya geçerse
409, hiçbir şey yazılmaz (§4.0-3b). Zamanlama üretim
koduna kanca eklemeden ReadQueryHook zinciri ve RaceSaveHook ile kurulur.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 16: Testcontainers 9, 14, 15 (K5 ∥ apply, var olan dört test havuz kipinde, kapsam kuralı) (spec §8.2-9, §8.2-14, §8.2-15; §4.13; §15-2)

**Files:**
- Değiştir (test): `OrderDeck.LicenseServer.Tests/Controllers/Panel/CustomerBalanceConcurrencyTests.cs` — sınıf belgesi ve öznitelikler (`:14-24`), dört `[Fact]` → `[Theory] [InlineData(false)] [InlineData(true)]` (`:42-45`, `:84-89`, `:111-120`, `:193-219`; dördüncü test lisansını kendisi kurar, `PersonBalancePoolSince` oraya da), `SeedAsync` (`:330-369`) → `SeedAsync(decimal initialBalance, bool pool)`, yeni `PoolSince` yardımcısı
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelDrawRaceTests.cs` (TC9 + K5 503 + panel `balance-busy` sayacı + havuz kipinde eşzamanlı iade iptali, N01 hakemi + havuz kipinde iptalin üyelik okumasında 1205 → 503)
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceScopeRuleRelationalTests.cs` (TC15)

Kod değişikliği beklenmez: K5'in havuz yolu (Görev 9 elle eksi, Görev 10 iade iptali), havuzlu apply ve kapsam kuralının deneme başına işlemi (Görev 4 `DrawAsync`, Görev 7 `ApplyPoolAsync`) bu yarışları kapatır; bu görev onları gerçek SQL Server'da kanıtlar. Üretim koduna test kancası eklenmez: TC9 ve K5 503 `RaceSaveHook` (`PersonBalanceRelationalApiFactory.Hook`), TC15 `CustomerBalanceScopeRuleRelationalTests`'in rakip kancası kalıbıyla (sınıf içine kopya) kurulur. Bir test kırmızı kalırsa önce sistematik hata ayıklama: yarış sırayla (kanca) kurulur, zamanlamayla değil. `CustomerBalanceScopeRuleRelationalTests` (S3'ün satır kipi testi) ve öbür S1 testleri DEĞİŞMEZ; regresyonda aynen koşar.

**Not (spec ↔ kod) — bu görev:**
1. **§8.2-15 "S3'ün satır kipi testi `DrawAsync`'e taşındıktan sonra da yeşil kalır".** Plan satır kipini `DrawAsync`'e taşımaz (iskelet farkları 7, 8: satır kipinin `ApplyOncePerScopeAsync` + `WriteDeductionAsync` yolu aynen kalır, havuz yolu ayrı yöntemdedir). Spec'in amacı (satır kipi testi kırılmaz) değişmeden korunur: `CustomerBalanceScopeRuleRelationalTests` bu görevin regresyonunda DEĞİŞMEDEN koşar; havuz kipindeki karşılığı yeni `PersonBalanceScopeRuleRelationalTests`'tir.
2. **§8.2-14'ün dördüncü testi (`:194`) lisansı `SeedAsync` ile kurmaz** (kendi lisansını satır içinde yazar, `:210-219`). Havuz kipinde koşabilmesi için `PersonBalancePoolSince` o lisansa da yazılır (`PoolSince(pool)`); test gövdesinin geri kalanı aynen.
3. **TC9'un paralel testi 503 `balance-busy`'ye de izin verir.** Talimat panel için 200 / 409 `insufficient-balance`, apply için 200 / 409 `no-balance` sayar. Ama 8 paralel yazarda birden çok yazım commit edebilir (apply `UpToPool` ile kısmi çeker, panel 20'lik çekimleri sığdıkça geçer); bir isteğin üç denemesi de çakışabilir ve iskelet farkı 12 (K5) ile §4.13 (apply) bunu 503 `balance-busy` yapar. Emsal: `CustomerBalanceConcurrencyTests.cs:54-58` ("8 yazar × 3 deneme yeterli değil"). Testin asıl iddiası değişmez: havuz ≥ 0, D1, korunum (70 − Σ başarılı çekim = havuz; kişide eksi üye N −30 var, yoksa satır başına jetonla havuz zaten eksiye inemezdi). 500 hâlâ izinli DEĞİL: havuz yolunda tükenen deneme 503'tür (satır kipinin 500'ü değil).
4. **TC9'un iade iptali testi belirlenimlidir.** Talimat "200 ve `AppliedAmount` 50 ya da havuz yetmezse `no-balance`" der. Görev 10'da iade iptalinin çapası hedefin kendi satırıdır (`AnchorRowId: target.WpfCustomerId`): iptal A'nın 40'ını çeker, havuzda B'nin 50'si kalır; apply'ın taze planı Σ = 50 ile tam 50'yi çeker. Çapa B olsaydı da Σ 50 kalırdı; `no-balance` yolu bu kurguda oluşmaz. Test kesin sonucu (200, 50) iddia eder.
5. **§8.2-14 havuz kipinde iki testin yolu değişir, iddiası değişmez.** (a) İki eksi ayar (`:85`): havuz kipinde `DrawAsync` (`AllOrNothing`, `P = S`); tek üyeli kişide (`GroupId` NULL) `S = {a}`, çekim çapa satırının kendisinden. Tek kazanan commit eder; kaybedenin her biri en çok bir kez çakışır (commit eden tek yazım odur), taze planda bakiye 0 → 409 `insufficient-balance`. (b) Panel reverse (`:112`): hedef eski tek bacaklı düşüm (`DrawGroupId` NULL, tutar < 0) → havuz kipinde `ReverseGroupAsync` (iskelet farkı 10, Görev 10); hakem yine N01, kaybedenler 409 `already-reversed`. İade (`:43`) ve WPF reverse (`:194`) S2'de kipe bakmaz (Görev 9 kip yalnız eksi ayarda okunur; iskelet farkı 2: WPF reverse değişmez); parametre burada "havuz bayrağı açıkken de aynı" korkuluğudur. Havuz kapısı yok, fabrikanın `PersonBalance:MinClientVersion`'ı gerekmez.

6. **§8.1 "D3'ü kişi kilidi ve Testcontainers 1 ve 9 kanıtlar".** Panel ile apply aynı satırdan çektiğinde çakışmayı o satırın kendi F02 jetonu yakalar; eksi üye yokken havuz satır başına jetonla zaten eksiye inemez. Bu yüzden ortak satırlı TC9 senaryoları kişi kilidini kanıtlamaz; D3'ü §4.13'ün örneğiyle (A 100, B −60, C 50; apply yalnız A'dan, panel yalnız C'den) kuran ortak satırı olmayan `Tc9_ortak_satiri_olmayan_panel_eksi_ayar_apply_kisi_kilidiyle_cakisir_D3` kanıtlar. Paralel teste eksi üye eklenir.
7. **§4.13 "her denemede Lookup'tan sonra yeniden" (plan farkı 7).** İlk denemede biten TC15 testleri ve kilit zaman aşımı testi, kilidin denemeler arasında bırakılıp yeniden alındığını ve yüklemin 2. denemede yeniden koştuğunu göstermez. `Ikinci_denemede_kilit_yeniden_alinir_rakip_kazanirsa_409_scope_has_deduction` bunu kanıtlar (rakip kancasına `afterWaiter` ve çakışma sayacı eklenir).
8. **Havuz kipinde eşzamanlı iade iptali — N01 hakemi (spec §4.9 "Hakem: N01", §4.13 tablo satırı "N01'de 409 `already-reversed`"; iskelet fark 18k; Görev 10 Not 4).** §8.2-14'ün panel reverse testi (`Ayni_hareket_esazamanli_yalnizca_bir_kez_reverse_edilebilir(pool: true)`) hedefi eski bir DÜŞÜMDÜR: havuz kipinde `ReverseGroupAsync`'e (S1 yolu) gider, S2'nin çekim türü iptaline (`ReverseInPoolAsync` → `DrawAsync`, `Kind = reversal`) değil; TC9'un iade iptali testinde de tek iptal apply'la yarışır. Bu yüzden üç kod yolu yalnız burada gerçek SQL Server'da koşar: `DrawAsync`'in `catch (DbUpdateException) when (IsDuplicateReversal(ex))` → `AlreadyReversed` dalı (Görev 4 Adım 5), `beforeAttempt`'in 2. denemedeki "geri alındı mı" sorusu (18k (a)) ve Görev 10'un `Insufficient` sonrası yeniden sorusu (18k (b)). InMemory'de N01 indeksi yok ve iki okuma arasına commit sokulamaz. Kurgu spec Örnek 4'tür (A = 30 — R +80, eski düşüm −50; B = 60; havuz 90; iptal A'dan 30, B'den 50 → A 0, B 10). Üç test: (i) `RaceSaveHook` ile rakip iptal dış iptalin kaydında commit eder (dış istek N01'e ya da jetona çarpar; EF'in komut sırasına bağlıdır, ikisi de `already-reversed`); (ii) `ReadQueryHook` zinciriyle rakip, dış isteğin "geri alındı mı" sorusundan SONRA ve 1. denemenin ilk bakiye okumasından ÖNCE commit eder (pencere (b): jeton çakışması yok, taze okumada havuz 10 < 80 → `Insufficient` → yeniden soru → `already-reversed`). Zincirin ilk halkası bilerek "geri alındı mı" sorgusudur (`[ReversesTransactionId] = @`): dış isteğin ilk `FROM [CustomerBalances]` okuması `ReverseInPoolAsync`'in başındaki `MembersAsync`'tir (`PersonBalance.cs:62-64` bakiyeleri de okur) ve `beforeAttempt`'ten ÖNCE gelir; kanca doğrudan ona kurulsaydı dış istek hızlı yolda `Stopped` alır, pencere (b) sınanmazdı; (iii) 8 paralel iptal (rota A ve B karışık): tam bir 200, gerisi 409 `already-reversed`; 503 `balance-busy` yalnız kilitlenmelerin denemeleri tüketmesiyle olur ve kabul edilir (Görev 14 Not 1'in gerekçesi), `insufficient-balance` ve 500 kabul edilmez.
9. **Panel `balance-busy` sayacı (spec §8.6, iskelet fark 18h; Görev 13).** Görev 14 sayacı yalnız `endpoint=apply` ile ölçer; panel denetleyicisinin ayrı `BalanceBusy` yardımcısındaki `panel/` önekli ölçüm başka hiçbir testte okunmaz. `K5_uc_denemede_de_cakisirsa_503_balance_busy_hicbir_sey_yazmaz` Görev 14'ün `MeterListener` kalıbıyla tek ölçümü (`1`, `endpoint = "panel/manual-adjustment"`) doğrular.
10. **Havuz kipinde iptalin üyelik okumasında 1205 → 503 (Görev 10 Not 6; spec §4.2-6 "S2 uygulama notu").** Görev 10 Adım 4 ve 5, `MembersAsync` okumasını (`ReverseInPoolAsync`'in başı, `ReverseDrawGroupAsync`'in havuz dalı) `catch (Exception ex) when (PersonBalance.IsDeadlock(ex))` → `BalanceBusy(licenseId, "reverse")` ile sarar: okuma servislerin korunan denemesinin dışındadır ve `CustomerBalances`'ı da okur (RCSI yok; yazarların X kilidinin arkasında 1205 kurbanı olabilir). InMemory'de komut kancası olmadığı için kanıt burada: `Havuz_kipinde_iptalin_uyelik_okumasi_kilitlenme_kurbani_olursa_503_balance_busy` (`[Theory]`, iki yol). `ReadQueryHook` isteğin ilk `FROM [CustomerBalances]` okumasında BİR KEZ `SqlException(1205)` fırlatır (`PersonBalanceReversalDeadlockTests`'in `SqlExceptionFactory` kalıbı; komut sunucuya gitmez, varsayılan yürütme stratejisi `InvalidOperationException`'a sarar). İki yolda da o okuma üyelik okumasıdır: öncesinde yalnız `LocateForTenantAsync` (projeksiyon), hedef ve (grup yolunda) grubun hareketleri (`FROM [CustomerBalanceTransactions]`), `IsPoolModeAsync` (`Licenses`) ve `MembersAsync`'in kendi projeksiyon sorguları (`WpfCustomerProjections`) koşar. Beklenti 503 `balance-busy`, hareket sayısı değişmez; istemcinin yeniden denemesi 200 ve tek iptal (iade iptalinde Örnek 4'ün sonu, grup yolunda Örnek 1'in grup geri alması: T 0, I 100, Y 30).

Sahip sorusu adayı: yok.

- [ ] **Adım 1: `CustomerBalanceConcurrencyTests`'i iki kipte koştur (§8.2-14)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/CustomerBalanceConcurrencyTests.cs` dosyasını aşağıdakiyle değiştir. Bugünkü dosyadan farklar yalnız şunlar: sınıf belgesine §8.2-14 paragrafı ve "Canlı yayın sırasında ÇALIŞTIRILMAZ." (`:14-22`); `[Trait("Category", "Testcontainers")]` (`:23-24` arası, emsal `CustomerBalanceScopeRuleRelationalTests.cs:30-31`); dört `[Fact]` → `[Theory] [InlineData(false)] [InlineData(true)]` ve `bool pool` parametresi; her testin başına "§8.2-14: havuz kipinde de" yorumu; `SeedAsync(initialBalance: …)` çağrıları `SeedAsync(initialBalance: …, pool: pool)`; dördüncü testin satır içi `License`'ına `PersonBalancePoolSince = PoolSince(pool)`; `SeedAsync`'in imzası ve `License`'ı; yeni `PoolSince`. İddiaların hiçbiri değişmez.

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// CustomerBalance kayıp-güncelleme koruması (F02, 2026-09-09 denetimi).
///
/// Bu testler neden gerçek SQL Server istiyor: InMemory'de eşzamanlılık
/// semantiği yok — read-check-write açığı orada hiçbir zaman görünmez.
/// Token öncesi davranış gerçek SQL Server'da tekrar üretilmişti: paralel
/// iadeler birbirinin bakiye yazımını eziyor, cache ledger toplamından
/// kopuyordu (invariant: Balance = SUM(Transactions.Amount)).
///
/// <para>Ortak bakiye §8.2-14: her test satır kipinde (bayrak boş) ve havuz kipinde
/// (<c>License.PersonBalancePoolSince</c> dolu) koşar; iddialar iki kipte aynıdır. Havuz kipinde eksi ayar
/// <c>PersonBalance.DrawAsync</c>'ten (tek üyeli kişide <c>P = S = {a}</c>), eski tek bacaklı düşümün panel
/// iptali grup geri almasından geçer; iade ve WPF reverse kipe bakmaz. Kişi tek üyeli olduğu için havuz
/// kipinin çok bacaklı yarışları burada değil, <c>PersonBalancePanelDrawRaceTests</c>'te sınanır.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerBalanceConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public CustomerBalanceConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paralel_iadeler_bakiyeyi_ledger_toplamindan_koparamaz(bool pool)
    {
        // §8.2-14: havuz kipinde de. İade (artı hareket) kipe bakmaz: iki kipte aynı tek satır yazıcısı.
        var (_, _, wpfCustomerId, licenseId, jwt) = await SeedAsync(initialBalance: 0m, pool: pool);
        var clients = BuildClients(jwt);

        // Isınma turu: var olmayan müşteriye istek — yönlendirme, model bağlama
        // ve EF sorgusu JIT edilsin ki gerçek turda istekler ayrışmasın.
        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/panel/customers/{Guid.NewGuid()}/balance/refund-full",
            new { Amount = 10m, Reason = "isinma" })));

        // Gerçek tur: 8 paralel iade, hepsi aynı bakiye satırına. 8 yazar ×
        // 3 deneme yeterli değil — bir kısmı retry tükenmesiyle düşer (TestServer
        // bunu istisna olarak fırlatır, prod'da 500 olur ve istemci yeniden
        // dener). Asıl güvence: başarılı sayısı ne olursa olsun hiçbir başarı
        // kaybolmaz ve cache == ledger toplamı == başarı × tutar.
        var responses = await Task.WhenAll(clients.Select(async c =>
        {
            try
            {
                var r = await c.PostAsJsonAsync(
                    $"/api/panel/customers/{wpfCustomerId}/balance/refund-full",
                    new { Amount = 100m, Reason = "yaris" });
                return r.StatusCode;
            }
            catch (Exception)
            {
                return HttpStatusCode.InternalServerError;
            }
        }));

        var succeeded = responses.Count(s => s == HttpStatusCode.OK);
        succeeded.Should().BeGreaterThan(0);

        var (balance, ledgerSum, txCount) = await ReadStateAsync(licenseId, wpfCustomerId);
        txCount.Should().Be(succeeded, "kaybeden isteklerin transaction'ı da geri alınmalı");
        balance.Should().Be(succeeded * 100m,
            "token öncesi davranışta paralel iadeler birbirini ezip bakiyeyi düşük bırakıyordu");
        balance.Should().Be(ledgerSum, "invariant: Balance = SUM(ledger)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Esazamanli_iki_negatif_ayarlamadan_yalnizca_biri_gecer(bool pool)
    {
        // Bakiye 100 — iki paralel -100 manuel ayar. Token öncesi ikisi de
        // ön kontrolde 100 görüp geçiyordu → bakiye -100 (kural delindi).
        // §8.2-14: havuz kipinde de. Orada eksi ayar DrawAsync'ten geçer (AllOrNothing, P = S = {a}: kişi tek
        // üyeli); tek kazanan commit eder, kaybedenler en çok bir kez çakışır ve taze planda 0 görüp 409 alır.
        var (_, _, wpfCustomerId, licenseId, jwt) = await SeedAsync(initialBalance: 100m, pool: pool);
        var clients = BuildClients(jwt);

        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/panel/customers/{Guid.NewGuid()}/balance/manual-adjustment",
            new { Amount = -10m, Reason = "isinma" })));

        var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/manual-adjustment",
            new { Amount = -100m, Reason = "yaris" })));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1,
            "bakiye 100 iken -100'lük ayarlardan yalnızca biri geçebilir");
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)
            .Should().Be(ParallelAttempts - 1, "kaybedenler insufficient-balance (409) almalı");

        var (balance, ledgerSum, _) = await ReadStateAsync(licenseId, wpfCustomerId);
        balance.Should().Be(0m, "bakiye asla sıfırın altına düşmemeli");
        // Seed bakiyesi ledger'sız yazıldığı için toplam yalnız -100 olmalı.
        ledgerSum.Should().Be(-100m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ayni_hareket_esazamanli_yalnizca_bir_kez_reverse_edilebilir(bool pool)
    {
        // N01 (2026-09-10 denetimi): +100 başlangıç, -60 düşüm → bakiye 40.
        // Aynı -60 hareketine 8 paralel Reverse. Ön kontrol (AnyAsync)
        // check-then-insert olduğundan yarışta hepsi geçebilir; hakem filtered
        // unique index olmalı. İndex öncesi davranış: iki+ +60 reversal satırı,
        // bakiye 160+ (defter ile bakiye TUTARLI biçimde yanlış — F02 token'ı
        // bu hatayı yakalayamaz, o yüzden ledger==balance asserti yetmez).
        // §8.2-14: havuz kipinde de. Orada eski tek bacaklı eksi hedef grup geri almasına gider
        // (PersonBalance.ReverseGroupAsync, plan farkı 10); hakem yine N01, kaybedenler already-reversed.
        var (_, _, wpfCustomerId, licenseId, jwt) = await SeedAsync(initialBalance: 40m, pool: pool);
        var deductionId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var now = DateTimeOffset.UtcNow;
            // Defteri bakiyeyle tutarlı kur: +100 başlangıç + (-60) düşüm = 40.
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                WpfCustomerId = wpfCustomerId,
                Amount = 100m,
                Kind = "refund-full",
                CreatedByCustomerId = Guid.NewGuid(),
                CreatedAt = now.AddMinutes(-2),
            });
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = deductionId,
                LicenseId = licenseId,
                WpfCustomerId = wpfCustomerId,
                Amount = -60m,
                Kind = "purchase-deduction",
                CreatedByCustomerId = Guid.NewGuid(),
                CreatedAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var clients = BuildClients(jwt);

        // Isınma: rota + EF JIT (var olmayan hareket → 404).
        await Task.WhenAll(clients.Select(c => c.PostAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/transactions/{Guid.NewGuid()}/reverse",
            content: null)));

        // Index öncesi kodda kaybedenlerin retry tükenmesi TestServer'da istisna
        // olarak fırlar (prod'da 500) — ilk testteki gibi 500 sayıyoruz ki
        // assertion'lara ulaşılabilsin.
        var responses = await Task.WhenAll(clients.Select(async c =>
        {
            try
            {
                var r = await c.PostAsync(
                    $"/api/panel/customers/{wpfCustomerId}/balance/transactions/{deductionId}/reverse",
                    content: null);
                return r.StatusCode;
            }
            catch (Exception)
            {
                return HttpStatusCode.InternalServerError;
            }
        }));

        responses.Count(s => s == HttpStatusCode.OK).Should().Be(1,
            "aynı hareketi geri almak için N eşzamanlı istekten yalnızca biri kazanmalı");
        responses.Count(s => s == HttpStatusCode.Conflict)
            .Should().Be(ParallelAttempts - 1, "kaybedenler already-reversed (409) almalı");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var reversals = await vdb.CustomerBalanceTransactions
            .Where(t => t.ReversesTransactionId == deductionId)
            .ToListAsync();
        reversals.Should().HaveCount(1, "iş kuralı: her özgün hareket en fazla bir kez geri alınır");
        reversals[0].Amount.Should().Be(60m);

        var (balance, ledgerSum, _) = await ReadStateAsync(licenseId, wpfCustomerId);
        balance.Should().Be(100m, "tek geri alma sonrası 40 + 60 = 100 olmalı (çift reversal'da 160 olurdu)");
        balance.Should().Be(ledgerSum, "invariant: Balance = SUM(ledger)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WPF_ucunda_ayni_hareket_esazamanli_yalnizca_bir_kez_reverse_edilebilir(bool pool)
    {
        // N01 — licenses-surface (WPF ucu) için kapsam doğrulaması.
        // Panel yüzeyi aynı unique index'e güveniyor; bu test WPF endpoint'inin
        // IsDuplicateReversal catch dalını da kapsar — InMemory index'i
        // uygulamadığından sıralı "ikinci çağrı" testi bu dalı asla yürütmez.
        //
        // +100 refund-full başlangıç, -60 purchase-deduction → bakiye 40.
        // 8 paralel Reverse → yalnız biri kazanır, 7'si 409 already-reversed alır.
        // §8.2-14: havuz kipinde de. WPF reverse kipe bakmaz ve havuz sürüm kapısı yok (plan farkı 2, 4).
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        Guid licenseId, wpfCustomerId, deductionId;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            licenseId = Guid.NewGuid();
            db.Licenses.Add(new License
            {
                Id = licenseId,
                LicenseKey = "wrev-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                PersonBalancePoolSince = PoolSince(pool),
            });
            wpfCustomerId = Guid.NewGuid();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = wpfCustomerId,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "u" + wpfCustomerId.ToString("N")[..6],
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                WpfCustomerId = wpfCustomerId,
                Balance = 40m,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            var now = DateTimeOffset.UtcNow;
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                WpfCustomerId = wpfCustomerId,
                Amount = 100m,
                Kind = "refund-full",
                CreatedByCustomerId = customerId,
                CreatedAt = now.AddMinutes(-2),
            });
            deductionId = Guid.NewGuid();
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = deductionId,
                LicenseId = licenseId,
                WpfCustomerId = wpfCustomerId,
                Amount = -60m,
                Kind = "purchase-deduction",
                CreatedByCustomerId = customerId,
                CreatedAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var clients = BuildClients(jwt);
        var reverseUrl = $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{deductionId}/reverse";

        // Isınma: rota + EF JIT (var olmayan hareket → 404).
        await Task.WhenAll(clients.Select(c => c.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{Guid.NewGuid()}/reverse",
            content: null)));

        // 8 paralel Reverse — IsDuplicateReversal catch dalı burada yürütülür.
        // Kaybeden retry tükenmesinde TestServer istisna fırlatır; prod'da 500.
        var responses = await Task.WhenAll(clients.Select(async c =>
        {
            try
            {
                var r = await c.PostAsync(reverseUrl, content: null);
                return r.StatusCode;
            }
            catch (Exception)
            {
                return HttpStatusCode.InternalServerError;
            }
        }));

        responses.Count(s => s == HttpStatusCode.OK).Should().Be(1,
            "aynı WPF hareketi için N eşzamanlı reverse'ten yalnızca biri kazanmalı");
        responses.Count(s => s == HttpStatusCode.Conflict)
            .Should().Be(ParallelAttempts - 1,
                "kaybedenler already-reversed (409) almalı — 500 alıyorsa IsDuplicateReversal eşleşmiyor");

        using var verifyScope = _factory.Services.CreateScope();
        var vdb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var reversals = await vdb.CustomerBalanceTransactions
            .Where(t => t.ReversesTransactionId == deductionId)
            .ToListAsync();
        reversals.Should().HaveCount(1, "iş kuralı: her özgün hareket en fazla bir kez geri alınır");
        reversals[0].Amount.Should().Be(60m);

        var (balance, ledgerSum, _) = await ReadStateAsync(licenseId, wpfCustomerId);
        balance.Should().Be(100m, "tek geri alma sonrası 40 + 60 = 100 olmalı (çift reversal'da 160 olurdu)");
        balance.Should().Be(ledgerSum, "invariant: Balance = SUM(ledger)");
    }

    private HttpClient[] BuildClients(string jwt) =>
        Enumerable.Range(0, ParallelAttempts)
            .Select(_ =>
            {
                var c = _factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                return c;
            })
            .ToArray();

    private async Task<(decimal Balance, decimal LedgerSum, int TxCount)> ReadStateAsync(
        Guid licenseId, Guid wpfCustomerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId)
            .Select(b => b.Balance)
            .SingleAsync();
        var ledger = await db.CustomerBalanceTransactions
            .Where(t => t.LicenseId == licenseId && t.WpfCustomerId == wpfCustomerId)
            .Select(t => t.Amount)
            .ToListAsync();
        return (balance, ledger.Sum(), ledger.Count);
    }

    /// <summary>Ortak bakiye kipinin bayrağı (spec §4.0): dolu → lisans havuz kipinde, boş → satır kipinde.</summary>
    private static DateTimeOffset? PoolSince(bool pool) => pool ? DateTimeOffset.UtcNow.AddMinutes(-1) : null;

    private async Task<(HttpClient client, Guid customerId, Guid wpfCustomerId, Guid licenseId, string jwt)>
        SeedAsync(decimal initialBalance, bool pool)
    {
        var (client, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = "balc-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            PersonBalancePoolSince = PoolSince(pool),
        });
        var wpfCustomerId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "u" + wpfCustomerId.ToString("N")[..6],
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        // Bakiye satırını baştan yaz: testler UPDATE yolundaki token'ı hedefliyor
        // (insert yarışı unique index'e bırakıldı — bilinçli kapsam dışı).
        db.CustomerBalances.Add(new CustomerBalance
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WpfCustomerId = wpfCustomerId,
            Balance = initialBalance,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (client, customerId, wpfCustomerId, licenseId, jwt);
    }
}
```

(`License.PersonBalancePoolSince` S1'den beri var: `OrderDeck.LicenseServer/Domain/License.cs:53`. Dört testin her biri `pool`'u kullanır — xUnit'in kullanılmayan Theory parametresi uyarısı `TreatWarningsAsErrors` altında hata olurdu.)

- [ ] **Adım 2: Koştur (Docker açık)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceConcurrencyTests"`
Expected: 8 test PASS (4 × 2 kip). `pool = false` dördü S1'deki gibi yeşil (§8.2-14'ün satır kipi yarısı).

Kırmızı kalırsa (yalnız `pool = true`): (a) iki eksi ayarda 500 ya da 503 → Görev 9'un `DrawManualAdjustmentAsync`'i (`AllOrNothing`, `MemberIds = null`) ya da Görev 4'ün çakışma döngüsü (`ChangeTracker.Clear()` + taze okuma; kaybeden en çok bir kez çakışmalı); `ledgerSum` −100 değilse çapanın `Amount`'u (Görev 4 Adım 5 — `DrawAsync` akışının `StageDraw`'ı: `PlanDraw`'ın ilk bacağı çapadır, tutarı `−bacak`); (b) panel reverse'te 404 → Görev 10'un üyelik denetimi (`S(rota)` tek üyeli kişide `{a}`), 500 → `ReverseGroupAsync`'in N01/`IsDeadlock` yolu (S1); (c) iade ya da WPF reverse kırmızıysa S2 bu yollara dokunmamalıydı — Görev 9'un kip okumasının yalnız eksi tutarda olduğunu, WPF reverse'in değişmediğini denetle.

- [ ] **Adım 3: TC9 + K5 503 + eşzamanlı iade iptali + üyelik okuması 1205 testlerini yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelDrawRaceTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// K5 panel eksi işlemleri ∥ havuzlu apply gerçek SQL Server'da (ortak bakiye spec 2026-10-11 §8.2-9, §4.13, K5; plan
/// farkı 12). Panelin elle eksi ayarı ve iade iptali kişinin havuzundan çeker (<c>P = S</c>), apply aynı havuzdan
/// (<c>P ⊆ S</c>). İki yazım da S'nin bakiye satırı olan bütün üyelerinin jetonuna dokunur (kişi kilidi): biri commit
/// edince öbürünün UPDATE'i tutmaz, plan taze okumayla baştan kurulur. Havuz hiçbir an eksiye inmez, D1 her satırda
/// tutar; üç denemede de çakışan panel eksi ayarı 503 <c>balance-busy</c> alır, hiçbir şey yazmaz ve sayaç
/// <c>endpoint=panel/manual-adjustment</c> ile bir artar (§8.6). Ortak satırdan çeken yazımları satırın kendi jetonu da
/// yakalar; kişi kilidini (D3, §8.1) ortak satırı olmayan, eksi üyeli §4.13 örneği kanıtlar. Aynı artı hareketin
/// (iade) eşzamanlı iki iptalinde hakem N01'dir (§4.9, §4.13): kaybeden 409 <c>already-reversed</c> alır —
/// <c>insufficient-balance</c> değil (Örnek 4; plan farkı 18k, Görev 10 Not 4). Havuz kipinde iptalin üyelik okuması
/// kilitlenme kurbanı (1205) olursa 503 <c>balance-busy</c> döner, hiçbir şey yazılmaz (Görev 10 Not 6).
///
/// <para>Yarış sırayla kurulur (<see cref="RaceSaveHook"/>: rakip yazım, dış isteğin <c>SaveChanges</c>'inde SQL
/// gitmeden commit eder; <see cref="ReadQueryHook"/>: rakip, dış isteğin belirli bir okumasından hemen önce commit
/// eder), zamanlamayla değil. InMemory'de <c>SaveChanges</c> atomik değil, N01 indeksi ve komut kancası yok; bu
/// iddialar orada kanıtlanamaz. Açıklamalar ve Id'ler üretilir.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalancePanelDrawRaceTests : IAsyncLifetime
{
    private const int Parallel = 8;
    private const decimal Step = 20m;

    private readonly SqlServerContainerFixture _sql;
    private PersonBalanceRelationalApiFactory _factory = null!;

    public PersonBalancePanelDrawRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() =>
        _factory = new PersonBalanceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Queries.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── TC9: K5 panel eksi işlemi ∥ apply ────────────────────────────────────

    /// <summary>
    /// §8.2-9, elle eksi ayar: apply'ın çekim kaydında (SQL gitmeden) panel B'den −70 commit eder (B 60 + C 10).
    /// Apply'ın ilk planı (B'den 60) panelin yazdığı B'nin kendi jetonuna çarpar; taze planda Σ = 30 → 30 çekilir.
    /// İki yazım ortak satırdan (B) çektiği için bu test K5'in çakışma ve taze plan yolunu sınar, kişi kilidini DEĞİL:
    /// kişi kilidini (D3) ortak satırı olmayan <see cref="Tc9_ortak_satiri_olmayan_panel_eksi_ayar_apply_kisi_kilidiyle_cakisir_D3"/> kanıtlar.
    /// </summary>
    [Fact]
    public async Task Tc9_panel_eksi_ayar_apply_kaydinda_havuz_eksiye_inmez()
    {
        var (kit, a, b, c) = await PersonAsync();
        var key = Guid.NewGuid();
        var atApplySave = false;
        _factory.Hook.Once(async saving =>
        {
            // Kanca apply'ın çekim kaydında olmalı: çapa (Id = anahtar) eklenmek üzere.
            atApplySave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == key);
            (await kit.PanelManualAdjustmentAsync(b, -70m, NewReason())).EnsureSuccessStatusCode();
        });

        var resp = await kit.ApplyAsync(a, 60m, 60m, key, null, [a, b, c]);

        atApplySave.Should().BeTrue("panel ayarı apply'ın çekim kaydında, SQL gitmeden commit etmeli");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1, "ilk plan panelin ilerlettiği jetonlara çarpmalı");
        var applied = await ReadApplyAsync(resp);
        applied.TransactionId.Should().Be(key);
        applied.AppliedAmount.Should().Be(30m, "taze plan: panel 70 çekti, havuzda 30 kaldı");
        applied.RemainingBalance.Should().Be(0m);
        (await kit.GroupRowsAsync(key)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().Equal((a, 0m), (c, -30m));
        (await PoolAsync(kit, a, b, c)).Should().Be(0m);
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b, c);
    }

    /// <summary>
    /// §8.2-9, iade iptali: A'ya 40'lık iade R. Apply'ın çekim kaydında panel R'yi iptal eder (çekim türü iptal, çapa
    /// A'da, 40 — Görev 10). Apply'ın ilk planı (A 40 + B 10) A'nın jetonuna çarpar; taze planda Σ = 50 → B'den 50.
    /// Ortak satır (A) çakışmayı kendi jetonuyla yakalar; bu test iade iptalinin K5 yolunu sınar, kişi kilidini değil.
    /// </summary>
    [Fact]
    public async Task Tc9_iade_iptali_apply_kaydinda_havuz_eksiye_inmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 50m);
        var refund = await kit.FundAsync(a, 40m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        var atApplySave = false;
        _factory.Hook.Once(async saving =>
        {
            atApplySave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == key);
            (await kit.PanelReverseAsync(b, refund)).EnsureSuccessStatusCode();
        });

        var resp = await kit.ApplyAsync(a, 50m, 50m, key, null, [a, b]);

        atApplySave.Should().BeTrue("iade iptali apply'ın çekim kaydında, SQL gitmeden commit etmeli");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1, "ilk plan iptalin ilerlettiği jetona çarpmalı");
        var applied = await ReadApplyAsync(resp);
        applied.AppliedAmount.Should().Be(50m, "iptal A'nın 40'ını aldı, havuzda B'nin 50'si kaldı");
        applied.RemainingBalance.Should().Be(0m);
        (await kit.GroupRowsAsync(key)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().Equal((a, 0m), (b, -50m));
        var cancel = (await kit.ReversalsOfAsync([refund])).Should().ContainSingle("iptal çapası tek; bacaklarda ReversesTransactionId yok").Which;
        cancel.WpfCustomerId.Should().Be(a);
        cancel.Amount.Should().Be(-40m);
        cancel.Kind.Should().Be(Kit.KindReversal);
        cancel.DrawGroupId.Should().Be(cancel.Id);
        (await PoolAsync(kit, a, b)).Should().Be(0m);
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b);
    }

    /// <summary>
    /// §8.1 (D3 — kişi kilidi ve Testcontainers 9 onu kanıtlar), §4.13'ün örneği: kişi {A 100, B −60, C 50} (havuz 90).
    /// Apply A'dan 90 ister; planı yalnız A'dan çeker. Apply'ın çekim kaydında (SQL gitmeden) panel C rotasından −50
    /// commit eder; planı yalnız C'den çeker. İki yazımın ORTAK SATIRI YOK: çakışmayı yalnız kişi kilidi doğurur (ikisi de
    /// S'nin bakiye satırı olan bütün üyelerinin jetonuna dokunur; apply'ın A UPDATE'i panelin ilerlettiği A jetonuna
    /// çarpar). Taze planda Σ = 100 − 60 + 0 = 40 → 40 çekilir, havuz 0. Kişi kilidi olmasaydı çakışma olmaz, A 10'a iner,
    /// havuz 10 − 60 + 0 = −50 olurdu. B kurulumdan eksi: eksi olmama iddiası yok, eksi üye çekilmez.
    /// </summary>
    [Fact]
    public async Task Tc9_ortak_satiri_olmayan_panel_eksi_ayar_apply_kisi_kilidiyle_cakisir_D3()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);
        var c = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        var atApplySave = false;
        _factory.Hook.Once(async saving =>
        {
            atApplySave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == key);
            (await kit.PanelManualAdjustmentAsync(c, -50m, NewReason())).EnsureSuccessStatusCode();
        });

        var resp = await kit.ApplyAsync(a, 90m, 90m, key, null, [a, b, c]);

        atApplySave.Should().BeTrue("panel ayarı apply'ın çekim kaydında, SQL gitmeden commit etmeli");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1,
            "ortak satır yok: yalnız kişi kilidi (A'nın jetonuna dokunuş) çakıştırır");
        var applied = await ReadApplyAsync(resp);
        applied.TransactionId.Should().Be(key);
        applied.AppliedAmount.Should().Be(40m, "taze plan: Σ = 100 − 60 + 0");
        applied.RemainingBalance.Should().Be(0m);
        (await kit.GroupRowsAsync(key)).Select(t => (t.WpfCustomerId, t.Amount)).Should().Equal((a, -40m));
        (await kit.BalanceAsync(a)).Should().Be(60m);
        (await kit.BalanceAsync(b)).Should().Be(-60m, "eksi üye çekilmez");
        (await kit.BalanceAsync(c)).Should().Be(0m);
        (await PoolAsync(kit, a, b, c)).Should().Be(0m, "D3: kişi kilidi olmasaydı −50");
        foreach (var row in new[] { a, b, c })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// §8.2-9, paralel: kişi {A 0, B 60, C 40, N −30} (havuz 70); 4 panel −20 (rota B) ve 4 apply 20 (çapa A) aynı anda.
    /// Toplam istek 160 &gt; 70: bir kısmı reddedilir. Havuz ≥ 0, D1, ve havuz = 70 − Σ(başarılı çekim): kayıp ya da çift
    /// yazım yok. Eksi üye N, eşzamanlı çekimler ayrık satırlardan geçerse (panel B'den, apply C'den) kişi kilidi olmadan
    /// havuzu eksiye düşürebilsin diye var; kişi kilidini belirlenimli olarak ortak satırı olmayan D3 testi kanıtlar,
    /// burada korunum ve izinli yanıtlar sınanır. Üç denemesi de çakışan istek 503 <c>balance-busy</c> alabilir (plan
    /// farkı 12, §4.13); 500 almaz.
    /// </summary>
    [Fact]
    public async Task Tc9_paralel_panel_eksi_ve_apply_havuzu_eksiye_dusurmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 60m);
        var c = await kit.MemberAsync(g, 40m);
        var n = await kit.MemberAsync(g, -30m);
        await kit.EnablePoolAsync();
        Guid[] members = [a, b, c, n];
        // Isınma: rota + EF JIT (bilinmeyen satır → panel 404, apply 409 no-balance), gerçek turda istekler ayrışmasın.
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => i % 2 == 0
            ? kit.PanelManualAdjustmentAsync(Guid.NewGuid(), -Step, NewReason())
            : kit.ApplyAsync(Guid.NewGuid(), Step, Step, Guid.NewGuid(), null, [Guid.NewGuid()])));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => i % 2 == 0
            ? OutcomeAsync(true, () => kit.PanelManualAdjustmentAsync(b, -Step, NewReason()))
            : OutcomeAsync(false, () => kit.ApplyAsync(a, Step, Step, Guid.NewGuid(), null, members))));

        var seen = string.Join(", ", results.Select(r => $"{(r.Panel ? "panel" : "apply")} {(int)r.Status} {r.Title}"));
        results.Should().OnlyContain(r => Allowed(r), "beklenmeyen yanıt ({0})", seen);
        results.Should().Contain(r => r.Status == HttpStatusCode.OK, "en az bir çekim geçmeli ({0})", seen);
        var pool = await PoolAsync(kit, a, b, c, n);
        pool.Should().BeGreaterThanOrEqualTo(0m, "havuz hiçbir an eksiye inmez ({0})", seen);
        pool.Should().Be(70m - results.Sum(r => r.Drawn),
            "her başarılı yazım havuzdan tam yazdığı kadar düşer ({0})", seen);
        (await kit.BalanceAsync(n)).Should().Be(-30m, "eksi üye çekilmez");
        (await kit.LedgerHoldsAsync(n)).Should().BeTrue("D1");
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b, c);
    }

    // ── K5: üç denemede de çakışma ───────────────────────────────────────────

    /// <summary>
    /// §4.13 + plan farkı 12: panel eksi ayarının her kaydından hemen önce B'nin jetonu ilerletilir; üç deneme de
    /// çakışır → 503 <c>balance-busy</c>. <c>SaveChanges</c> tek işlem: hiçbir hareket ve bakiye değişikliği kalmaz.
    /// Sayaç <c>orderdeck.balance.busy_total</c> panel önekiyle tek ölçüm alır (§8.6, plan farkı 18h; Görev 13'ün panel
    /// <c>BalanceBusy</c> yardımcısı — Görev 14 yalnız <c>endpoint=apply</c>'ı ölçer).
    /// </summary>
    [Fact]
    public async Task K5_uc_denemede_de_cakisirsa_503_balance_busy_hicbir_sey_yazmaz()
    {
        var (kit, a, b, c) = await PersonAsync();
        var before = await TransactionCountAsync(kit);
        var metrics = _factory.Services.GetRequiredService<OrderDeckMetrics>();
        var olcumler = new ConcurrentQueue<(long Value, string? Endpoint)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Paralel sınıflar aynı Meter adını paylaşır: yalnız bu fabrikanın sayacı.
            if (ReferenceEquals(instrument, metrics.BalanceBusy)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!ReferenceEquals(instrument, metrics.BalanceBusy)) return;
            string? endpoint = null;
            foreach (var tag in tags)
                if (tag.Key == "endpoint") endpoint = tag.Value as string;
            olcumler.Enqueue((value, endpoint));
        });
        listener.Start();
        _factory.Hook.Always(() => TouchBalanceAsync(b));

        var resp = await kit.PanelManualAdjustmentAsync(b, -30m, NewReason());
        _factory.Hook.Disarm();

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await resp.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(resp)).Should().Be("balance-busy");
        _factory.Hook.ConcurrencyFailures.Should().Be(PersonBalance.MaxAttempts);
        (await TransactionCountAsync(kit)).Should().Be(before, "yeni hareket yok");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(60m);
        (await kit.BalanceAsync(c)).Should().Be(40m);
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b, c);
        olcumler.Should().ContainSingle("tek 503, tek ölçüm").Which.Should().Be((1L, "panel/manual-adjustment"));
    }

    // ── K5: havuz kipinde eşzamanlı iade iptali (N01 hakemi) ─────────────────

    /// <summary>
    /// §4.9 "Hakem: N01", §4.13 (panel eksi işlemler: N01'de 409 <c>already-reversed</c>), Örnek 4. Dış iptalin (rota B)
    /// çekim kaydında (SQL gitmeden) rakip iptal (rota A) commit eder. Dış istek ya N01 ihlaline çarpar
    /// (<c>IsDuplicateReversal</c> → <c>AlreadyReversed</c>, Görev 4) ya da önce A'nın jetonuna (2. denemenin
    /// <c>beforeAttempt</c>'i iptali bulur → <c>Stopped</c>, plan farkı 18k (a)); hangisi önce gelirse gelsin yanıt
    /// 409 <c>already-reversed</c>'tır — taze planda havuz 10 &lt; 80 olduğu hâlde <c>insufficient-balance</c> değil.
    /// İşlem bütünüyle geri alınır: tek iptal grubu.
    /// </summary>
    [Fact]
    public async Task K5_iade_iptali_kayitta_rakip_iptal_commit_ederse_409_already_reversed()
    {
        var (kit, a, b, r) = await Ornek4Async();
        var before = await TransactionCountAsync(kit);
        var atCancelSave = false;
        (HttpStatusCode Status, string? Title)? rakip = null;
        _factory.Hook.Once(async saving =>
        {
            // Kanca dış iptalin çekim kaydında olmalı: R'yi iptal eden çapa eklenmek üzere.
            atCancelSave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.ReversesTransactionId == r);
            rakip = await StatusAsync(() => kit.PanelReverseAsync(a, r));
        });

        var dis = await StatusAsync(() => kit.PanelReverseAsync(b, r));
        _factory.Hook.Disarm();

        atCancelSave.Should().BeTrue("rakip iptal dış iptalin çekim kaydında, SQL gitmeden commit etmeli");
        rakip.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.OK, null), "rakip iptal kazanır");
        dis.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.Conflict, "already-reversed"),
            "N01 ya da jeton → 2. denemenin 'geri alındı mı'sı; insufficient-balance ya da 500 değil");
        await ShouldHaveSingleCancelAsync(kit, a, b, r, before);
    }

    /// <summary>
    /// Plan farkı 18k (b), Görev 10 Not 4: rakip iptal, dış iptalin "geri alındı mı" sorusundan SONRA ama 1. denemenin
    /// ilk bakiye okumasından ÖNCE commit eder. Dış istek commit sonrası bakiyeleri okur (A 0, B 10, havuz 10 &lt; 80):
    /// jeton çakışması yok, 2. deneme gelmez, <c>DrawAsync</c> <c>Insufficient</c> döner. Denetleyicinin yeniden sorusu
    /// iptali görür → 409 <c>already-reversed</c>. Yeniden soru olmasaydı 409 <c>insufficient-balance</c> dönerdi.
    /// Zincirin ilk halkası "geri alındı mı" sorgusudur: dış isteğin ilk <c>FROM [CustomerBalances]</c> okuması
    /// <c>ReverseInPoolAsync</c>'in başındaki <c>MembersAsync</c>'tir ve <c>beforeAttempt</c>'ten önce gelir.
    /// </summary>
    [Fact]
    public async Task K5_iade_iptali_bakiye_okumasindan_once_rakip_commit_ederse_409_already_reversed()
    {
        var (kit, a, b, r) = await Ornek4Async();
        var before = await TransactionCountAsync(kit);
        var soruldu = false;
        var rakipler = new List<(HttpStatusCode Status, string? Title)>();
        // 1. halka: dış isteğin beforeAttempt'i (deneme 1). Gövde yalnız 2. halkayı kurar; sorgu ondan sonra koşar ve
        // henüz iptal görmez (false).
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal)
                && sql.Contains("[ReversesTransactionId] = @", StringComparison.Ordinal),
            () =>
            {
                soruldu = true;
                // 2. halka: 1. denemenin ilk bakiye okuması (PersonDrawSetAsync → MembersAsync'in bakiye sorgusu).
                _factory.Queries.Arm(
                    sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
                    async () => rakipler.Add(await StatusAsync(() => kit.PanelReverseAsync(a, r))));
                return Task.CompletedTask;
            });

        var dis = await StatusAsync(() => kit.PanelReverseAsync(b, r));
        _factory.Queries.Disarm();

        soruldu.Should().BeTrue("dış istek 1. denemenin başında 'geri alındı mı' diye sormalı (beforeAttempt)");
        rakipler.Should().ContainSingle("rakip dış isteğin 1. denemesinin bakiye okumasından önce koşmalı")
            .Which.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.OK, null));
        dis.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.Conflict, "already-reversed"),
            "Insufficient sonrası yeniden soru iptali bulur; insufficient-balance değil");
        _factory.Hook.ConcurrencyFailures.Should().Be(0,
            "kazanan okumalardan önce commit etti: jeton çakışması yok, 2. deneme gelmez (pencere b)");
        await ShouldHaveSingleCancelAsync(kit, a, b, r, before);
    }

    /// <summary>
    /// §4.9, §4.13: aynı iadenin 8 paralel iptali (rota A ve B karışık). Tam biri 200; gerisi 409
    /// <c>already-reversed</c> (hızlı yol, N01, jetondan sonraki soru ya da <c>Insufficient</c> sonrası soru).
    /// Kilitlenmeler denemeleri tüketirse 503 <c>balance-busy</c> da olabilir (Görev 14 Not 1'in gerekçesi; yalnız tek
    /// commit olduğu için jeton çakışması bir isteğe en çok bir kez düşer). <c>insufficient-balance</c> ve 500 kabul
    /// edilmez. Tek iptal grubu, A 0, B 10, D1.
    /// </summary>
    [Fact]
    public async Task K5_iade_iptali_paralel_sekiz_tek_iptal()
    {
        var (kit, a, b, r) = await Ornek4Async();
        var before = await TransactionCountAsync(kit);
        // Isınma: rota + EF JIT (var olmayan hareket → 404), gerçek turda istekler ayrışmasın.
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => kit.PanelReverseAsync(i % 2 == 0 ? a : b, Guid.NewGuid())));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel)
            .Select(i => StatusAsync(() => kit.PanelReverseAsync(i % 2 == 0 ? a : b, r))));

        var seen = string.Join(", ", results.Select(x => $"{(int)x.Status} {x.Title}"));
        results.Should().ContainSingle(x => x.Status == HttpStatusCode.OK, "tam bir iptal kazanır ({0})", seen);
        results.Where(x => x.Status != HttpStatusCode.OK).Should().OnlyContain(
            x => (x.Status == HttpStatusCode.Conflict && x.Title == "already-reversed")
                || (x.Status == HttpStatusCode.ServiceUnavailable && x.Title == "balance-busy"),
            "kaybeden already-reversed alır; insufficient-balance ya da 500 yok ({0})", seen);
        await ShouldHaveSingleCancelAsync(kit, a, b, r, before);
    }

    // ── Havuz kipinde iptal: üyelik okumasının kilitlenmesi (Görev 10 Not 6) ─

    public const string InPoolPath = "eski-satır";
    public const string DrawGroupPath = "çekim-grubu";

    /// <summary>
    /// Görev 10 Not 6 (spec 2026-10-11 §4.2-6 "S2 uygulama notu"): havuz kipinde panel iptalinin üyelik okuması
    /// (<c>MembersAsync</c> — <c>ReverseInPoolAsync</c>'in başında ve <c>ReverseDrawGroupAsync</c>'in havuz dalında)
    /// servislerin korunan denemesinin dışındadır ve <c>CustomerBalances</c>'ı da okur: yazarların X kilidinin
    /// arkasında 1205 kurbanı olabilir. Kanca isteğin ilk <c>FROM [CustomerBalances]</c> okumasında BİR KEZ
    /// <c>SqlException(1205)</c> fırlatır, komut sunucuya gitmez. İki yolda da o okuma üyelik okumasıdır: öncesinde
    /// yalnız hedef, kip ve (grup yolunda) grubun hareketleri okunur. Yanıt 503 <c>balance-busy</c> (500 değil),
    /// hiçbir şey yazılmaz, istemcinin yeniden denemesi başarır. Sarmal kaldırılırsa istisna TestServer'dan istemciye
    /// düşer (<see cref="StatusAsync"/> → 500). InMemory'de komut kancası yok.
    /// </summary>
    [Theory]
    [InlineData(InPoolPath)]
    [InlineData(DrawGroupPath)]
    public async Task Havuz_kipinde_iptalin_uyelik_okumasi_kilitlenme_kurbani_olursa_503_balance_busy(string path)
    {
        Kit kit;
        Guid route, target;
        (Guid A, Guid B, Guid R) ornek4 = default;
        Kit.Example1? ornek1 = null;
        if (path == InPoolPath)
        {
            // DrawGroupId boş artı hedef (iade R, A'da), rota B → ReverseInPoolAsync.
            var (k, a, b, r) = await Ornek4Async();
            (kit, ornek4, route, target) = (k, (a, b, r), b, r);
        }
        else
        {
            // Çekim grubu K (T, I, Y), rota T → ReverseDrawGroupAsync'in havuz dalı.
            kit = await Kit.CreateAsync(_factory);
            ornek1 = await kit.Example1Async();
            await kit.EnablePoolAsync();
            (route, target) = (ornek1.T, ornek1.K);
        }
        var before = await TransactionCountAsync(kit);
        var injected = 0;
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
            () =>
            {
                injected++;
                return Task.FromException(SqlExceptionFactory.Create(1205,
                    "Transaction was deadlocked and has been chosen as the deadlock victim."));
            });

        var busy = await StatusAsync(() => kit.PanelReverseAsync(route, target));
        _factory.Queries.Disarm();

        injected.Should().Be(1, "kanca üyelik okumasında ({0}) bir kez fırlatmalı", path);
        busy.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.ServiceUnavailable, "balance-busy"),
            "üyelik okumasının 1205'i yeniden denensin (503); 500 değil");
        (await TransactionCountAsync(kit)).Should().Be(before, "okuma her yazımdan önce: hiçbir şey yazılmadı");

        (await kit.PanelReverseAsync(route, target)).StatusCode.Should().Be(HttpStatusCode.OK,
            "istemcinin yeniden denemesi başarır");
        if (path == InPoolPath)
            await ShouldHaveSingleCancelAsync(kit, ornek4.A, ornek4.B, ornek4.R, before);
        else
        {
            var ex = ornek1!;
            (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3, "tek grup geri alması, bir kez");
            (await kit.BalanceAsync(ex.T)).Should().Be(0m);
            (await kit.BalanceAsync(ex.I)).Should().Be(100m);
            (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
            await ShouldHoldD1AndNonNegativeAsync(kit, ex.T, ex.I, ex.Y);
        }
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record Outcome(bool Panel, HttpStatusCode Status, string? Title, decimal Drawn);

    private static string NewReason() => $"r-{Guid.NewGuid():N}";

    /// <summary>
    /// Spec Örnek 4'ün başı: kişi {A, B}; A'ya iade R (+80), sonra eski tek bacaklı düşüm A'dan 50 → A = 30; B = 60;
    /// havuz 90; lisans havuz kipinde. Döner: (kit, A, B, R).
    /// </summary>
    private async Task<(Kit kit, Guid a, Guid b, Guid r)> Ornek4Async()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var r = await kit.FundAsync(a, 80m);
        await kit.SeedLegacyDeductionAsync(a, 50m, 50m);
        var b = await kit.MemberAsync(g, 60m);
        await kit.EnablePoolAsync();
        return (kit, a, b, r);
    }

    /// <summary>
    /// Örnek 4'ün sonu, tek kez: R'nin tek iptal çapası (A, −30, <c>reversal</c>, Reverses R, DrawGroupId = kendi Id'si)
    /// ve tek bacağı (B, −50); kurulumdan bu yana tam iki yeni hareket; A 0, B 10; D1 iki satırda.
    /// </summary>
    private static async Task ShouldHaveSingleCancelAsync(Kit kit, Guid a, Guid b, Guid r, int before)
    {
        var cancel = (await kit.ReversalsOfAsync([r])).Should()
            .ContainSingle("N01: hareket başına tek iptal; bacaklarda ReversesTransactionId yok").Which;
        cancel.WpfCustomerId.Should().Be(a, "çapa hedefin kendi satırında (Görev 10)");
        cancel.Amount.Should().Be(-30m);
        cancel.Kind.Should().Be(Kit.KindReversal);
        cancel.DrawGroupId.Should().Be(cancel.Id);
        (await kit.GroupRowsAsync(cancel.Id)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().Equal((a, -30m), (b, -50m));
        (await TransactionCountAsync(kit)).Should().Be(before + 2, "kaybedenler hiçbir şey yazmaz");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(10m);
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b);
    }

    /// <summary>
    /// Durum ve (başarısızsa) problem başlığı. TestServer sunucuda yakalanmayan istisnayı istemciye fırlatır; prod'da
    /// 500 olurdu — başlık yerine en içteki istisna, düşen koşu nedeni göstersin.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string? Title)> StatusAsync(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            var resp = await call();
            return (resp.StatusCode, resp.IsSuccessStatusCode ? null : await Kit.TitleAsync(resp));
        }
        catch (Exception e)
        {
            var root = e.GetBaseException();
            return (HttpStatusCode.InternalServerError, $"{root.GetType().Name}: {root.Message}");
        }
    }

    /// <summary>Kişi {A (0, bakiye satırı var), B (60), C (40)} — aynı grup, havuz 100; lisans havuz kipinde.</summary>
    private async Task<(Kit kit, Guid a, Guid b, Guid c)> PersonAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 60m);
        var c = await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();
        return (kit, a, b, c);
    }

    /// <summary>
    /// Durum, (başarısızsa) problem başlığı ve (başarılıysa) havuzdan çekilen tutar. TestServer, sunucuda yakalanmayan
    /// istisnayı istemciye fırlatır; prod'da 500 olurdu — başlık yerine en içteki istisna, düşen koşu nedeni göstersin.
    /// </summary>
    private static async Task<Outcome> OutcomeAsync(bool panel, Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await call();
        }
        catch (Exception e)
        {
            var root = e.GetBaseException();
            return new Outcome(panel, HttpStatusCode.InternalServerError, $"{root.GetType().Name}: {root.Message}", 0m);
        }
        if (!resp.IsSuccessStatusCode) return new Outcome(panel, resp.StatusCode, await Kit.TitleAsync(resp), 0m);
        // Panel eksi ayarı hep-ya-hiç (tam Step); apply UpToPool (kısmi olabilir) — yanıtın AppliedAmount'u.
        var drawn = panel ? Step : (await ReadApplyAsync(resp)).AppliedAmount;
        return new Outcome(panel, resp.StatusCode, null, drawn);
    }

    /// <summary>
    /// İzinli yanıtlar: 200; panel 409 <c>insufficient-balance</c>, apply 409 <c>no-balance</c> (taze planda havuz
    /// yetmedi); ikisi de 503 <c>balance-busy</c> (üç denemede de çakıştı). 500 yok.
    /// </summary>
    private static bool Allowed(Outcome r) => r.Status switch
    {
        HttpStatusCode.OK => true,
        HttpStatusCode.Conflict => r.Title == (r.Panel ? "insufficient-balance" : PersonBalance.CodeNoBalance),
        HttpStatusCode.ServiceUnavailable => r.Title == "balance-busy",
        _ => false,
    };

    private static async Task<LicensesCustomerBalanceApplyController.ApplyResponse> ReadApplyAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.ApplyResponse>())!;

    private static async Task<decimal> PoolAsync(Kit kit, params Guid[] rows)
    {
        var sum = 0m;
        foreach (var row in rows) sum += await kit.BalanceAsync(row);
        return sum;
    }

    private static async Task ShouldHoldD1AndNonNegativeAsync(Kit kit, params Guid[] rows)
    {
        foreach (var row in rows)
        {
            (await kit.BalanceAsync(row)).Should().BeGreaterThanOrEqualTo(0m, "hiçbir satır eksiye inmez");
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        }
    }

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId));

    /// <summary>Kilit dokunuşu: satıra yalnız "değişti" işareti, jetonu StampVersions ilerletir.</summary>
    private async Task TouchBalanceAsync(Guid row)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == row);
        db.Entry(balance).Property(b => b.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync();
    }
}
```

Kurgunun dayandığı sözleşmeler: `PersonBalanceRelationalApiFactory` (Görev 1: `Hook` = `RaceSaveHook`, `PersonBalance:` ve `SalesSync:MinClientVersion` = `0.9.11`; kitin varsayılan UA'sı `OrderDeck-WPF/0.9.11` kapıyı geçer), kit mod Off (`Kit.CreateAsync` varsayılanı) → apply'da kapsam kilidi ve açık işlem yok; panel isteği kancanın içinde beklemeden commit eder. `RaceSaveHook` kancanın kendi yazımlarını (panel isteğinin ve `TouchBalanceAsync`'in `SaveChanges`'i) yeniden tetiklemez (`_running`, `TestHelpers/RaceSaveHook.cs:42`). Apply'ın havuz yolunda `DrawAsync`'ten önce `SaveChanges` yoktur (Görev 7); `atApplySave` bunu kanıtlar. Panel yanıtı gövdesizdir (`Ok()`, Görev 9): panel başarısında çekilen tutar istenen tutardır (`AllOrNothing`). Planlar: elle eksi ayarda çapa rota satırı B (60), kalan C'den (M7 büyükten küçüğe; A ≤ 0 listede yok, Görev 3); iade iptalinde çapa hedefin satırı A (Görev 10). D3 testinde: apply `P = S = {A, B, C}`, `LockedNegatives` boş, Σ = 90 (B'nin −60'ı dahil, Görev 4 `pool`), plan yalnız çapa A'dan 90; panel `P = S`, `AllOrNothing` (`max(0, 90) ≥ 50`), plan yalnız çapa C'den 50; ikisi de `StageDraw` ile A, B, C'nin jetonuna dokunur (Görev 4 Adım 5: çekilmeyen izlenen satırın `UpdatedAt`'i `IsModified`). Sıfır tutarlı bacak yazılmaz (yalnız çapa 0 olabilir), bu yüzden taze planda grup yalnız `(A, −40)`. Ortak satırlı iki sıralı test ve paralel test kişi kilidini kanıtlamaz: panel ile apply aynı satırdan çektiğinde o satırın kendi F02 jetonu çakışmayı yakalar ve eksi üye yokken satır başına jetonla hiçbir satır, dolayısıyla havuz da eksiye inemez. 503 testinin sayacı: `OrderDeckMetrics` fabrikada tekil (`Program.cs:901`), `BalanceBusy` Görev 13'ten; panel yardımcısı `$"panel/{endpoint}"` yazar, elle eksinin `Busy`'si `BalanceBusy(licenseId, "manual-adjustment")` (Görev 9) → `panel/manual-adjustment`; `MeterListener` kalıbı Görev 14'ün `Uc_denemede_de_cakisan_apply_503…` testinden. İade iptali testleri (Görev 10 `ReverseInPoolAsync`): hedef R `DrawGroupId` boş ve artı → çekim türü iptal, `DrawAsync(AllOrNothing, AnchorRowId = A, Kind = reversal, ReversesTransactionId = R, MemberIds = null, ScopeLockResource = null)`; açık işlem ve kilit yok, rakip kancanın içinde beklemeden commit eder. Kazananın planı: çapa A 30, B 50 → A 0, B 10 (`GroupRowsAsync`: çapa ilk). Okuma sırası (Görev 4 "Okuma sırası", panel yolu): denetleyicide hedef (`FROM [CustomerBalanceTransactions]`, `WHERE [Id] = …`), `IsPoolModeAsync`, `ReverseInPoolAsync`'in `MembersAsync`'i (`FROM [WpfCustomerProjections]` ve `FROM [CustomerBalances]`); `DrawAsync` 1. denemede `beforeAttempt` (`AnyAsync(t => t.ReversesTransactionId == hedef)` → `[ReversesTransactionId] = @…`), sonra `PersonDrawSetAsync` (projeksiyonlar, ardından denemenin ilk bakiye okuması). Rakip o okumadan önce commit edince dış istek A 0, B 10 okur; izlenen yükleme de commit sonrasıdır, `SaveChanges`'e varılmaz → `ConcurrencyFailures` 0. `ReadQueryHook` kancanın gövdesindeki sorguları yeniden tetiklemez (`_running`), gövdede kurulan halka gövdeden sonraki komutlarda denenir (`TestHelpers/ReadQueryHook.cs`). Paralel testte tek commit olur (N01): bir kaybeden en çok bir kez jetona çarpar, 2. denemenin `beforeAttempt`'i iptali görür. Üyelik okuması 1205 testi (Not 10): `ReadQueryHook` gövdeden çıkan istisnayı `ReaderExecutingAsync`'ten yayar (`_before = null` gövdeden önce, `_running` `finally`'de sıfırlanır; `TestHelpers/ReadQueryHook.cs`), komut sunucuya gitmez; `SqlExceptionFactory.Create` (`TestHelpers/SqlExceptionFactory.cs`, aynı derleme) EF'in geçici hata dedektörünün tanıdığı numarayı yazar, varsayılan strateji onu `InvalidOperationException`'a sarar (`PersonBalanceReversalDeadlockTests`'in okuma kolu). Panel yolunda `CustomerIdResolver.LocateForTenantAsync` yalnız `WpfCustomerProjections`'ı, `IsPoolModeAsync` `Licenses`'ı okur; `FROM [CustomerBalanceTransactions]` `FROM [CustomerBalances]` süzgecine takılmaz (kapanış köşeli parantezi). Grup yolunun ikinci isteği S1'in grup geri almasıdır (`ReverseGroupAsync`; T 0, I 100, Y 30 — `PersonBalanceReversalDeadlockTests`'in sonucu).

- [ ] **Adım 4: Koştur (Docker açık)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePanelDrawRaceTests"`
Expected: 10 test PASS (TC9 dört, K5 503 bir, eşzamanlı iade iptali üç, üyelik okuması 1205 iki).

Kırmızı kalırsa: (a) `atApplySave` false → apply yolunda `DrawAsync`'ten önce bir `SaveChanges` var (Görev 7) ya da kanca başka bir kaydın önünde tetiklendi; (b) D3 testinde (`Tc9_ortak_satiri_olmayan…`) `ConcurrencyFailures = 0`, A 10, havuz −50 → kişi kilidi yok: Görev 4'ün jeton dokunuşu (Adım 5, `StageDraw`: çekilmeyen izlenen satırların `UpdatedAt`'i `IsModified`) eksik ya da panel yolu `P = S`'nin bütün satırlarını izlemiyor (Görev 9, `MemberIds = null` → `PersonDrawSetAsync`). Ortak satırlı iki sıralı testte `ConcurrencyFailures = 0` bu yolla oluşamaz (çekilen satırın kendi jetonu çakışır); orada 0 → kanca apply'ın kaydında tetiklenmedi, (a)'ya bak; (c) iade iptalinde 409 → Görev 10'un üyelik denetimi (`R` A'da, rota B) ya da `beforeAttempt`'in ilk denemede "geri alındı mı" sorması; (d) paralel testte 500 → `DrawAsync`'in yakalama sırası (`DbUpdateConcurrencyException`, `IsDeadlock`; Görev 4) ya da panelde `Busy` eşlemesi (Görev 9 `BalanceBusy`); (e) 503 testinde `ConcurrencyFailures` ≠ 3 → `MaxAttempts` döngüsü (Görev 4), 500 → satır kipinin `ApplyTransactionAsync`'ine düşülmüş (Görev 9 kip okuması); ölçüm yok ya da `endpoint` ≠ `panel/manual-adjustment` → Görev 13 Adım 10'daki panel `BalanceBusy` yardımcısının sayacı (`$"panel/{endpoint}"`) ya da Görev 9'un `Busy` eşlemesi `BalanceBusy` yerine doğrudan `Problem(…, 503)` dönüyor; (f) iade iptali testlerinde dış istek 409 `insufficient-balance` → Görev 10 `ReverseInPoolAsync`'in `Insufficient` sonrası "geri alındı mı" yeniden sorusu (ikinci test, pencere 18k (b)) ya da `beforeAttempt`'in yalnız ilk denemede sorması (birinci test, pencere 18k (a): jeton çakışmasından sonraki deneme iptali görmeli); 500 → Görev 4 Adım 5'in `catch (DbUpdateException ex) when (IsDuplicateReversal(ex))` dalı eksik ya da `IsDuplicateTransactionKey` dalından sonra (yakalama sırası), ya da Görev 10'un `result.Status switch`'inde `AlreadyReversed`/`Stopped` eşlemesi yok (`UnreachableException`); kökü 1205 olan 500 → Görev 10 Adım 4/5'in `MembersAsync` sarmalı (`catch (Exception ex) when (PersonBalance.IsDeadlock(ex))` → `BalanceBusy(licenseId, "reverse")`) eksik; `soruldu` false ya da `rakipler` boş → "geri alındı mı" `beforeAttempt` içinde değil (denetleyicide ayrı okuma) ya da sorgu `[ReversesTransactionId] = @` biçiminde değil (Görev 10 `IsReversedAsync`); `atCancelSave` false → dış istek `DrawAsync`'ten önce başka bir `SaveChanges` yapıyor (Görev 10 Not 7); `ReversalsOfAsync` iki satır → N01 indeksi yok ya da iptal bacaklara da `ReversesTransactionId` yazıyor (Görev 4 `StageDraw`: yalnız çapa); (g) üyelik okuması 1205 testinde (Not 10) 500 → aynı sarmal eksik; `injected` 0 → üyelik okumasının komut metni `FROM [CustomerBalances]` değil; 200 ve `injected == 1` → kanca üyelik okumasında değil, `DrawAsync`/`ReverseGroupAsync`'in korunan okumasında tetiklendi (üyelik okuması bakiyeyi okumuyor ya da sıra değişti).

- [ ] **Adım 5: TC15 — havuz kipinde kapsam kuralı (§8.2-15, §15-2)**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceScopeRuleRelationalTests.cs` (rakip kancası `CustomerBalanceScopeRuleRelationalTests.cs:215-295`'ten kopya, iki ekle: `Arm`'ın isteğe bağlı üçüncü parametresi `afterWaiter` ve `RaceSaveHook` kalıbında jeton çakışması sayacı — ikinci deneme testi için. O sınıfın üyesi `private` olduğu için paylaşılamaz ve o dosya bu görevde değişmez):

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Satış senkronu mod On + ortak bakiye havuz kipi: kapsam başına tek düşüm kuralı çapa düzeyinde, gerçek SQL Server'da
/// (ortak bakiye spec 2026-10-11 §8.2-15, §4.2, §4.13, §15-2; plan farkı 7). Kuralın hakemi asıl kayıt×kapsam uygulama
/// kilididir (<c>bal:{lisans}:{asıl kayıt}:{kapsam}</c>); havuz yolunda her deneme kendi işlemini açar, kilidi alır,
/// Lookup'u ve kapsam yüklemini kilit altında koşar. 409 <c>scope-has-deduction</c>'ın tutarı <c>−Σ(grup)</c>'tur:
/// çapanın payı 0 olan planda (Örnek 1) çapa satırının tutarı değil. Başka üyedeki çapa kuralı tetiklemez (M10).
/// Jeton çakışmasından sonraki deneme kilidi yeniden alır ve yüklemi yeniden koşar: arada kazanan rakibin çapasını
/// görür (§4.13). Satır kipinin aynı sözleşmesi <see cref="CustomerBalanceScopeRuleRelationalTests"/>'te, değişmeden.
///
/// <para>Yarış sırayla kurulur: ilk apply'ın düşüm kaydında kanca rakip apply'ı başlatır ve rakibin kilitte beklediği
/// SQL Server'da görülene (<see cref="AppLockWaiterProbe"/>) ya da rakip düşüm yazımına gelene kadar bekler; üst sınır
/// kilit bekleme sınırının altındadır. Kişi {T (sahip, 0), I (100), Y (30)}; kullanıcı adları ve grup numarası
/// üretilir.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceScopeRuleRelationalTests : IAsyncLifetime
{
    /// <summary>Kancanın rakibi bekleme üst sınırı; kilit bekleme sınırının (<see cref="SalesCommandLock.DefaultTimeout"/>,
    /// 5 sn) altında. Olağan durumda kanca rakibin kilitte beklediğini görür görmez bırakır.</summary>
    private static readonly TimeSpan RivalWait = TimeSpan.FromSeconds(4);

    private const decimal PoolAtStart = 130m;

    private readonly SqlServerContainerFixture _sql;
    private string _connectionString = null!;
    private HookFactory _factory = null!;
    private Kit _kit = null!;
    private Guid _t;
    private Guid _i;
    private Guid _y;

    public PersonBalanceScopeRuleRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connectionString = await _sql.CreateDatabaseAsync();
        _factory = new HookFactory(_connectionString);
        _kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var g = Kit.NewGroup();
        _t = await _kit.MemberAsync(g);
        _i = await _kit.MemberAsync(g, 100m, platform: "instagram");
        _y = await _kit.MemberAsync(g, 30m);
        await _kit.EnablePoolAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _kit.Client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── §8.2-15: farklı anahtarlı iki apply aynı çapa ve kapsamda ────────────

    /// <summary>
    /// §8.2-15, §15-2: T'ye ve <c>session:42</c>'ye farklı anahtarlı eşzamanlı iki apply. Tam olarak biri 200, öbürü 409
    /// <c>scope-has-deduction</c>: <c>transactionId</c> kazananın çapası, <c>appliedAmount</c> = <c>−Σ(grup)</c> (çapanın
    /// payı 0 — T'nin bakiyesi yok), <c>productTotal</c> kazananınki. Tek geri alınmamış kapsamlı çapa; havuz bir kez düşer.
    /// </summary>
    [Fact]
    public async Task Tc15_farkli_anahtarli_iki_apply_biri_200_obur_409_scope_has_deduction_eksi_sigma()
    {
        var first = Guid.NewGuid();
        var rival = Guid.NewGuid();
        _factory.Hook.Arm(() => ApplyAsync(_t, 50m, rival), RivalWait);

        var firstResp = await ApplyAsync(_t, 120m, first);
        var rivalResp = await _factory.Hook.RivalAsync();

        var bodies = $"{await firstResp.Content.ReadAsStringAsync()} | {await rivalResp.Content.ReadAsStringAsync()}";
        new[] { firstResp.StatusCode, rivalResp.StatusCode }
            .Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], bodies);
        var (winner, loser) = firstResp.StatusCode == HttpStatusCode.OK
            ? ((Resp: firstResp, Key: first, Amount: 120m), (Resp: rivalResp, Key: rival, Amount: 50m))
            : ((Resp: rivalResp, Key: rival, Amount: 50m), (Resp: firstResp, Key: first, Amount: 120m));

        var winnerRows = await _kit.GroupRowsAsync(winner.Key);
        winnerRows[0].Amount.Should().Be(0m, "çapanın payı 0: 409'un tutarı çapa satırından değil grubun toplamından");
        (-winnerRows.Sum(t => t.Amount)).Should().Be(winner.Amount);
        await ShouldBeScopeHasDeductionAsync(loser.Resp, winner.Key, appliedAmount: winner.Amount, productTotal: winner.Amount);
        (await UnreversedScopeAnchorsAsync()).Should().Equal((winner.Key, _t));
        (await _kit.GroupRowsAsync(loser.Key)).Should().BeEmpty("kaybeden hiç yazmaz");
        (await PoolAsync()).Should().Be(PoolAtStart - winner.Amount, "havuz bir kez düşer");
        await ShouldHoldD1Async();
    }

    /// <summary>
    /// Aynı anahtarın eşzamanlı iki tekrarı: kilitte bekleyen ikinci istek kilidi aldığı denemede, kilit altında
    /// anahtarı yeniden arar ve kayıtlı sonucu oynatır — <c>scope-has-deduction</c> değil. İkisi de 200 ve aynı çapa;
    /// tek çekim grubu.
    /// </summary>
    [Fact]
    public async Task Tc15_ayni_anahtarli_iki_apply_ikisi_de_200_tek_grup()
    {
        var key = Guid.NewGuid();
        _factory.Hook.Arm(() => ApplyAsync(_t, 120m, key), RivalWait);

        var firstResp = await ApplyAsync(_t, 120m, key);
        var rivalResp = await _factory.Hook.RivalAsync();

        firstResp.StatusCode.Should().Be(HttpStatusCode.OK, await firstResp.Content.ReadAsStringAsync());
        rivalResp.StatusCode.Should().Be(HttpStatusCode.OK, await rivalResp.Content.ReadAsStringAsync());
        foreach (var resp in new[] { firstResp, rivalResp })
            (await ReadApplyAsync(resp)).Should().Be(
                new LicensesCustomerBalanceApplyController.ApplyResponse(key, 120m, 10m));
        (await _kit.GroupRowsAsync(key)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().BeEquivalentTo([(_t, 0m), (_i, -100m), (_y, -20m)], "Örnek 1'in tek grubu");
        (await UnreversedScopeAnchorsAsync()).Should().Equal((key, _t));
        (await _kit.BalanceAsync(_t)).Should().Be(0m);
        (await _kit.BalanceAsync(_i)).Should().Be(0m);
        (await _kit.BalanceAsync(_y)).Should().Be(10m);
        await ShouldHoldD1Async();
    }

    /// <summary>
    /// §15-2, M10: kural çapa düzeyinde. Aynı kişinin I üyesine ve sonra T'ye aynı kapsamla iki apply (farklı anahtar,
    /// sırayla): T'nin kilidinde ve yükleminde I'nin çapası görünmez → ikisi de 200 (#506'nın çok izli akışı).
    /// </summary>
    [Fact]
    public async Task Tc15_baska_uyedeki_capa_kapsam_kuralini_tetiklemez()
    {
        var onI = Guid.NewGuid();
        var onT = Guid.NewGuid();

        var iResp = await ApplyAsync(_i, 10m, onI);
        var tResp = await ApplyAsync(_t, 20m, onT);

        iResp.StatusCode.Should().Be(HttpStatusCode.OK, await iResp.Content.ReadAsStringAsync());
        tResp.StatusCode.Should().Be(HttpStatusCode.OK, await tResp.Content.ReadAsStringAsync());
        (await UnreversedScopeAnchorsAsync()).Should().BeEquivalentTo([(onI, _i), (onT, _t)], "her çapa kendi üyesinde");
        (await _kit.BalanceAsync(_t)).Should().Be(0m);
        (await _kit.BalanceAsync(_i)).Should().Be(70m, "önce kendi çapasından 10, sonra T'nin çekiminde 20");
        (await _kit.BalanceAsync(_y)).Should().Be(30m);
        await ShouldHoldD1Async();
    }

    // ── İkinci deneme: kilit, Lookup ve yüklem yeniden (deneme başına işlem, plan farkı 7) ──

    /// <summary>
    /// §4.13 ("her denemede Lookup'tan sonra yeniden"), plan farkı 7: X (120) düşüm kaydında rakip R'yi (50, farklı
    /// anahtar) başlatır; R kapsam kilidinde bekler. Kanca R'nin beklediğini görünce panel I'ye 10 iade eder (artı hareket:
    /// kapsam kilidi almaz, yalnız I'nin jetonunu ilerletir). X'in SQL'i I'nin bayat jetonuna çarpar; X'in 1. deneme
    /// işlemi atılır, kilit bırakılır. Kilitte bekleyen R kilidi alır, yüklemi temiz bulur, 50 çeker ve commit eder.
    /// X'in 2. denemesi kilidi YENİDEN alır (R'nin commit'ini bekler), Lookup'tan sonra yüklemi yeniden koşar ve R'nin
    /// çapasını görür → 409 <c>scope-has-deduction</c>, tutarı R'nin <c>−Σ(grup)</c>'u. Tek işlem/tek kilitle (satır kipinin
    /// <c>ApplyOncePerScopeAsync</c>'i gibi) R, X bitene kadar bekler ve X 200 alırdı; 2. denemede yüklem atlansaydı iki
    /// geri alınmamış kapsamlı çapa kalırdı.
    /// </summary>
    [Fact]
    public async Task Ikinci_denemede_kilit_yeniden_alinir_rakip_kazanirsa_409_scope_has_deduction()
    {
        var first = Guid.NewGuid();
        var rival = Guid.NewGuid();
        _factory.Hook.Arm(
            () => ApplyAsync(_t, 50m, rival),
            RivalWait,
            async () => (await _kit.PanelRefundAsync(_i, 10m)).EnsureSuccessStatusCode());

        var x = await ApplyAsync(_t, 120m, first);
        var r = await _factory.Hook.RivalAsync();

        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1,
            "X'in 1. denemesi panel iadesinin ilerlettiği I jetonuna çarpmalı");
        await ShouldBeScopeHasDeductionAsync(x, rival, appliedAmount: 50m, productTotal: 50m);
        (await UnreversedScopeAnchorsAsync()).Should().Equal((rival, _t));
        (await _kit.GroupRowsAsync(first)).Should().BeEmpty("X'in 1. denemesi geri alındı, 2. denemesi yazmadı");
        (await _kit.GroupRowsAsync(rival)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().Equal((_t, 0m), (_i, -50m));
        (await PoolAsync()).Should().Be(PoolAtStart + 10m - 50m, "iade +10, yalnız R'nin 50'si düştü");
        (await _kit.BalanceAsync(_i)).Should().Be(60m);
        (await _kit.BalanceAsync(_y)).Should().Be(30m);
        await ShouldHoldD1Async();
    }

    // ── Kilit zaman aşımı (deneme başına işlem, plan farkı 7) ────────────────

    /// <summary>
    /// Havuz yolunda da asıl kayıt×kapsam kilidi başka bir işlemde tutulurken gelen apply kilit bekleme sınırında 409
    /// <c>stale</c> alır (<see cref="SalesCommandLock"/>; <c>DrawAsync</c> <c>SalesCommandRejection</c>'ı yakalamaz,
    /// Görev 7 eşler). Hiçbir şey yazılmaz.
    /// </summary>
    [Fact]
    public async Task Havuz_kipinde_kapsam_kilidi_zaman_asimi_409_stale_yazim_yok()
    {
        await using var holder = NewDb();
        await using var held = await holder.Database.BeginTransactionAsync();
        await SalesCommandLock.AcquireAsync(
            holder,
            LicensesCustomerBalanceApplyController.ScopeLockResource(_kit.LicenseId, _t, Kit.Session42),
            TimeSpan.FromSeconds(1),
            default);
        var key = Guid.NewGuid();

        var resp = await ApplyAsync(_t, 100m, key);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("stale");
        (await _kit.GroupRowsAsync(key)).Should().BeEmpty();
        (await UnreversedScopeAnchorsAsync()).Should().BeEmpty();
        (await _kit.BalanceAsync(_t)).Should().Be(0m);
        (await _kit.BalanceAsync(_i)).Should().Be(100m);
        (await _kit.BalanceAsync(_y)).Should().Be(30m);
        await held.RollbackAsync();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>Kişinin bütün üyeleriyle (<c>W = S</c>), <c>session:42</c> kapsamında, ürün toplamı = tutar.</summary>
    private Task<HttpResponseMessage> ApplyAsync(Guid row, decimal amount, Guid key) =>
        _kit.ApplyAsync(row, amount, amount, key, Kit.Session42, [_t, _i, _y]);

    private static async Task<LicensesCustomerBalanceApplyController.ApplyResponse> ReadApplyAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.ApplyResponse>())!;

    /// <summary>Lisansta <c>session:42</c> kapsamlı, geri alınmamış düşüm çapaları (kapsam yalnız çapada, §3.2).</summary>
    private Task<List<(Guid Id, Guid Row)>> UnreversedScopeAnchorsAsync() =>
        _kit.Sales.ReadDbAsync(async db =>
        {
            var rows = await db.CustomerBalanceTransactions.AsNoTracking()
                .Where(t => t.LicenseId == _kit.LicenseId && t.Kind == Kit.KindDeduction && t.SaleScope == Kit.Session42
                    && !db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id))
                .Select(t => new { t.Id, t.WpfCustomerId })
                .ToListAsync();
            return rows.Select(r => (Id: r.Id, Row: r.WpfCustomerId)).ToList();
        });

    private async Task<decimal> PoolAsync() =>
        await _kit.BalanceAsync(_t) + await _kit.BalanceAsync(_i) + await _kit.BalanceAsync(_y);

    private async Task ShouldHoldD1Async()
    {
        foreach (var row in new[] { _t, _i, _y })
        {
            (await _kit.BalanceAsync(row)).Should().BeGreaterThanOrEqualTo(0m);
            (await _kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        }
    }

    private static async Task ShouldBeScopeHasDeductionAsync(
        HttpResponseMessage resp, Guid transactionId, decimal appliedAmount, decimal productTotal)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("scope-has-deduction");
        problem.GetProperty("transactionId").GetGuid().Should().Be(transactionId);
        problem.GetProperty("appliedAmount").GetDecimal().Should().Be(appliedAmount, "−Σ(grup), §15-2");
        problem.GetProperty("productTotal").GetDecimal().Should().Be(productTotal);
    }

    private LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connectionString).Options);

    /// <summary>
    /// Düşüm yazımı kancası (<c>CustomerBalanceScopeRuleRelationalTests.RivalHook</c>'un kopyası, iki ekle): yalnız
    /// kurulduğunda ve yalnız <c>purchase-deduction</c> satırı ekleyen kaydetmede tetiklenir — havuz yolunda çapa ve
    /// bacaklar tek kayıttadır, tek varış sayılır. İlk tetiklenişte rakip isteği başlatır (sonunu beklemez) ve rakip düşüm
    /// yazımına gelene, kilitte beklediği görülene ya da süre dolana kadar bekler; ardından, verildiyse,
    /// <c>afterWaiter</c>'ı bekler (ilk isteğin SQL'i gitmeden araya bir yazım sokmak için). Ekler: <c>afterWaiter</c> ve
    /// jeton çakışması sayacı (<see cref="RaceSaveHook"/> kalıbı).
    /// </summary>
    private sealed class RivalHook(string connectionString) : SaveChangesInterceptor
    {
        private readonly object _gate = new();
        private bool _armed;
        private int _arrivals;
        private int _concurrencyFailures;
        private TimeSpan _wait;
        private Func<Task<HttpResponseMessage>>? _rival;
        private Func<Task>? _afterWaiter;
        private Task<HttpResponseMessage>? _rivalTask;
        private TaskCompletionSource _rivalArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Kurulduğundan beri jeton çakışmaları (<see cref="DbUpdateConcurrencyException"/>).</summary>
        public int ConcurrencyFailures => Volatile.Read(ref _concurrencyFailures);

        public void Arm(Func<Task<HttpResponseMessage>> rival, TimeSpan wait, Func<Task>? afterWaiter = null)
        {
            lock (_gate)
            {
                _arrivals = 0;
                Volatile.Write(ref _concurrencyFailures, 0);
                _wait = wait;
                _rival = rival;
                _afterWaiter = afterWaiter;
                _rivalTask = null;
                _rivalArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _armed = true;
            }
        }

        public void Disarm()
        {
            lock (_gate) _armed = false;
        }

        /// <summary>Kancanın başlattığı rakip isteğin yanıtı.</summary>
        public async Task<HttpResponseMessage> RivalAsync()
        {
            Task<HttpResponseMessage>? task;
            lock (_gate) task = _rivalTask;
            task.Should().NotBeNull("kanca rakibi ilk isteğin düşüm yazımından önce başlatmalı");
            return await task!;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not { } db
                || !db.ChangeTracker.Entries<CustomerBalanceTransaction>()
                    .Any(e => e.State == EntityState.Added && e.Entity.Kind == Kit.KindDeduction))
                return result;

            Func<Task<HttpResponseMessage>>? rival;
            Func<Task>? afterWaiter;
            int arrival;
            lock (_gate)
            {
                if (!_armed) return result;
                arrival = ++_arrivals;
                rival = arrival == 1 ? _rival : null;
                afterWaiter = arrival == 1 ? _afterWaiter : null;
                if (rival is not null) _rivalTask = Task.Run(rival);
            }

            if (rival is not null)
            {
                await Task.WhenAny(
                    _rivalArrived.Task,
                    AppLockWaiterProbe.WaitForWaiterAsync(connectionString, _wait, cancellationToken));
                if (afterWaiter is not null) await afterWaiter();
            }
            else if (arrival == 2)
                _rivalArrived.TrySetResult();
            return result;
        }

        /// <summary>
        /// Jeton çakışmasını sayar. <c>SaveChangesFailedAsync</c> değil: EF <see cref="DbUpdateConcurrencyException"/>'ı
        /// o olaya uğratmaz (<see cref="RaceSaveHook.ThrowingConcurrencyExceptionAsync"/> gerekçesi).
        /// </summary>
        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _concurrencyFailures);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Gerçek SQL Server + rakip kancası; iki sürüm kapısı da kitin UA'sını (0.9.11) geçer.</summary>
    private sealed class HookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public RivalHook Hook { get; } = new(connectionString);

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["PersonBalance:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
            ["SalesSync:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
```

Kurgunun dayandığı sözleşmeler: Görev 7 (`ApplyPoolAsync`: kural yalnız mod On ∧ `SaleScope` dolu; `ScopeLockResource(lisans, asıl kayıt, kapsam)`; geri çağrı kapsam doluyken HER denemede önce Lookup, sonra `LatestScopeDeductionAsync(…, personPool: true)` — çapa süzgeci `WpfCustomerId == asıl kayıt`; `SalesCommandRejection` → `Problem(rejection.Code)` = 409 `stale`; `Drawn` → `ApplyResponse(çapa, Applied, max(0, Pool − Applied))`), Görev 4 (`ScopeLockResource` doluyken deneme başına `BeginTransactionAsync` + `SalesCommandLock.AcquireAsync(…, DefaultTimeout)`, `beforeAttempt` kilitten sonra; commit kilidi bırakır), Görev 6 (oynatmanın `RemainingBalance`'ı havuz kipinde `PoolBalanceAsync`: `max(0, Σ_W)` = 10 — ilk yanıtın `130 − 120`'siyle aynı), Görev 1 (`EnablePoolAsync`, `GroupRowsAsync`: çapa ilk). Rakip istek ilk isteğin işleminde tutulan kilitte bekler (aynı çapa, aynı kapsam); ilk istek commit edince kilidi alır ve kilit altında kazananın çapasını görür. İkinci deneme testinde: panel iadesi artı harekettir, kapsam kilidi almaz ve yalnız rota satırının (I) jetonunu ilerletir (spec §4.13 "Artı hareketler"; S2 bu yola dokunmaz); X'in 1. denemesi o an yalnız kilidi tutar ve okumaları READ COMMITTED olduğundan iadeyi engellemez. `DrawAsync`'in `await using var tx` denemenin `try` bloğundadır (Görev 4 Adım 5): `DbUpdateConcurrencyException`'da işlem atılır, `@LockOwner = 'Transaction'` kilidi bırakılır ve kuyrukta bekleyen R kilidi alır; X'in 2. denemesinin `AcquireAsync`'i R'nin commit'ini bekler (`DefaultTimeout` 5 sn). R'nin taze planı: T 0 (çapa, 0), I 110 → 50. X'in 2. denemesinde geri çağrı Lookup'u (anahtar `first` yok) ve ardından `LatestScopeDeductionAsync`'i koşar (Görev 7 `BeforeAttempt`), R'nin çapasını bulur → `Stopped` → 409; X hiçbir şey yazmaz.

- [ ] **Adım 6: Koştur (Docker açık)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceScopeRuleRelationalTests"`
Expected: 5 test PASS.

Kırmızı kalırsa: (g) ikinci deneme testinde X 200 alır, R 409 `scope-has-deduction` (ya da `stale`) → denemeler tek işlemde ve tek kilitle koşuyor: R, X bitene kadar beklemiş (Görev 4 Adım 5: `BeginTransactionAsync` + `AcquireAsync` deneme başına, `await using` ile deneme sonunda atılmalı); (h) R 200 ama X de 200 ve iki geri alınmamış kapsamlı çapa → 2. denemede yüklem atlanıyor (Görev 7 `BeforeAttempt`: `LatestScopeDeductionAsync` her denemede, Lookup'tan sonra koşmalı); (i) `ConcurrencyFailures = 0` → panel iadesi X'in kaydından önce commit etmedi (kanca `afterWaiter`'ı beklemiyor) ya da X'in planı I'yi yazmıyor; (a) ikisi de 200 ve iki çapa → kapsam yüklemi kilit altında koşmuyor ya da kilit işlem dışında alınıyor (Görev 4 Adım 5 — `DrawAsync` akışının deneme başına `BeginTransactionAsync` + `SalesCommandLock.AcquireAsync`'i, `beforeAttempt` kilitten sonra; Görev 7 geri çağrısı); (b) 409 ama `appliedAmount` 0 → `LatestScopeDeductionAsync`'in havuz kolu çapa satırının tutarını döndürüyor, `−Σ(grup)` değil (Görev 6); (c) `Tc15_ayni_anahtarli…`'da rakip 409 `scope-has-deduction` → geri çağrı kapsam doluyken ilk denemede Lookup'u atlıyor (Görev 7: "anahtar varsa ve (`attempt > 1` ya da `scope` dolu)"); (d) `Tc15_baska_uyedeki…`'de 409 → yüklem kişi düzeyinde süzüyor, çapa düzeyinde değil (§15-2, M10; Görev 6); (e) zaman aşımı testinde 500 → `SalesCommandRejection` `DrawAsync`'te yutuluyor ya da `ApplyPoolAsync`'te eşlenmiyor (Görev 4, 7); (f) rakip 409 `stale` → kanca rakibi kilit sınırından uzun tuttu: `AppLockWaiterProbe`'un bağlantı dizesi ve `RivalWait` (4 sn < 5 sn) emsaldeki gibi mi.

- [ ] **Adım 7: Görevin üç sınıfı + regresyon (Docker açık)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePanelDrawRaceTests|FullyQualifiedName~CustomerBalanceConcurrencyTests|FullyQualifiedName~PersonBalanceScopeRuleRelationalTests"`
Expected: 23 test PASS (8 + 10 + 5).

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceScopeRuleRelationalTests|FullyQualifiedName~PersonBalanceReversalRaceTests|FullyQualifiedName~PersonBalanceReversalDeadlockTests|FullyQualifiedName~PersonBalanceReversalLegMoveRaceTests|FullyQualifiedName~PersonGroupHookRelationalTests|FullyQualifiedName~PersonBalanceDrawRaceTests|FullyQualifiedName~PersonBalanceDrawDeadlockTests|FullyQualifiedName~PersonBalanceDrawMembershipRaceTests"`
Expected: hepsi PASS. `CustomerBalanceScopeRuleRelationalTests` DEĞİŞMEDEN yeşil: satır kipinin kapsam kuralı (S3, §8.2-15'in ikinci yarısı, §15-2) havuz yolundan etkilenmez. `PersonBalanceDrawRaceTests`, `PersonBalanceDrawDeadlockTests` (Görev 14) ve `PersonBalanceDrawMembershipRaceTests` (Görev 15) önceki görevlerin sınıflarıdır; `DrawAsync`'in deneme başına işlemini ve `IsDeadlock` dalını sınarlar.

Testcontainers yerelde "Docker kapalı" gibi düşerse: `docker context ls`, sonra **PowerShell'den** `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'` (boru adı `docker_engine` da olabilir — `docker version` hangisinde yanıt veriyorsa) ve aynı komutu `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test …` ile koştur. git-bash bu değeri bozar.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Controllers/Panel/CustomerBalanceConcurrencyTests.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelDrawRaceTests.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceScopeRuleRelationalTests.cs
git commit -F - <<'MSG'
test(balance): K5 ∥ apply ve havuz kipinde kapsam kuralını gerçek SQL Server'da kanıtla

Panelin elle eksi ayarı ve iade iptali apply'ın çekim kaydında commit
edince apply'ın planı jetona çarpar, taze okumayla baştan kurulur; havuz
eksiye inmez, D1 her satırda tutar. Ortak satırı olmayan panel ve apply
çekimi yalnız kişi kilidiyle çakışır; eksi üyeli kişide havuz eksiye
inmez (D3, §4.13 örneği). Paralel panel/apply karışımında kayıp ya da
çift yazım yok; üç denemede de çakışan panel eksi ayarı 503
balance-busy alır, hiçbir şey yazmaz ve sayaç panel/manual-adjustment
ile bir artar (ortak bakiye §8.2-9, §8.6, plan farkı 12).

Havuz kipinde aynı iadenin eşzamanlı iptalinde kaybeden 409
already-reversed alır, insufficient-balance değil: rakip kayıtta commit
ederse (N01 ya da jetondan sonraki soru), rakip bakiye okumasından önce
commit ederse (Insufficient sonrası soru) ve sekiz paralel iptalde tek
iptal grubu kalır (§4.9, §4.13, Örnek 4; plan farkı 18k). İptalin üyelik
okuması kilitlenme kurbanı (1205) olursa yanıt 503 balance-busy, hiçbir
şey yazılmaz, yeniden deneme başarır (iade iptali ve çekim grubu yolu).

Var olan dört eşzamanlılık testi satır ve havuz kipinde koşar (§8.2-14).
Mod On + havuz kipinde aynı çapa ve kapsama farklı anahtarlı iki apply'dan
biri 409 scope-has-deduction alır, tutarı −Σ(grup) (çapa payı 0 olan plan
dahil); aynı anahtar oynatılır; başka üyedeki çapa kuralı tetiklemez;
jeton çakışmasından sonraki deneme kilidi yeniden alır ve arada kazanan
rakibin çapasını görür; kilit zaman aşımı 409 stale (§8.2-15, §15-2,
plan farkı 7). Satır kipinin kapsam testi değişmedi.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

---

## Görev 17: Compose satırı, `deploy/README`, saha notları + tam paket (spec §7.1 adım 5–9, §7.2, §7.4, §8.5)

**Files:**
- Değiştir: `deploy/docker-compose.yml` — license-server `environment:` bloğu; `SalesSync__ForceEndSilenceMinutes` satırının (`:227`) hemen ARDINA yorum + `PersonBalance__MinClientVersion` satırı (yorum üslubu SalesSync bloğu `:213-224`).
- Değiştir: `deploy/README.md` — (1) `## .env template` (`:123-193`): SalesSync bloğunun (`:157-172`) son satırı `SalesSync__ForceEndSilenceMinutes=15`'in (`:172`) ardına ortak bakiye açıklaması + `PersonBalance__MinClientVersion=`; (2) `## Deploy geri alma (rollback)`: "Ortak bakiye S1 tabanı" uyarısının (`:430-486`) ARDINA, `## Masaüstü: satış senkronunu yerelde sıfırlama (≤0.9.10'a dönüş)` başlığından (`:488`) ÖNCE yeni alıntı blok "Ortak bakiye S2".
- Test (oluştur): `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDeployTests.cs` (saf, fikstürsüz; koleksiyonsuz sınıf `ShardingTestFramework.Classify`'da (`TestHelpers/ShardingTestFramework.cs:176-185`) InMemory parçasına düşer; `FindRepoFile` `Controllers/Licenses/LicensesSalesControllerTests.cs:605-617`'deki özel yöntemin sınıf içi kopyası).

Bu görev üretim koduna dokunmaz. Prod'da license-server'ın `env_file`'ı yoktur: `.env`'deki bir anahtar container'a yalnız compose'un `environment:` eşlemesindeki satırla geçer (`ObifinDataControllerGateTests.cs:147-159` dersi). Satır olmazsa Wh yayımlanıp `.env`'e yazıldıktan sonra da `PersonBalance:MinClientVersion` boş kalır, `enable --apply` hep reddeder ve havuz kipindeki bir lisansta WPF'in preview'ı ve yeni apply'ı (kayıtlı anahtarın oynatması etkilenmez) 409 alır. Compose dosyasının sahibi CI'dır: her deploy'da VPS'teki kopyayı ezer (`deploy/README.md:11-24`), değişiklik repo'da yapılır. Değer sır değildir (sürüm numarası), `.env`'de durur. `:?` değil `:-`: eksik `.env` satırı CI'ın `docker compose -f docker-compose.yml.new config -q` kapısını (`.github/workflows/license-server-deploy.yml:248`) düşürüp her deploy'u kilitlerdi; boş değer bilinçli olarak "kapı kapalı" demektir (Görev 1: boş/bozuk alt sınır tanımsız + açılış uyarısı; Görev 11–12: `enable --apply` ret; Görev 5 ve 7: havuz kipinde 409 `client-update-required`).

S2 göç eklemez; deploy anında hiçbir lisans havuz kipinde değildir (`PersonBalancePoolSince`'i yalnız `person-balance enable --apply` yazar, S2 bu komutu getirir ama koşturmaz). Bu yüzden S2 deploy'u düşerse otomatik geri alma S1 imajına döner ve güvenlidir: S1 kip bayrağını okumaz, bütün lisanslar satır kipine iner; S1 çok bacaklı geçmişi okur ve grup olarak geri alır (§7.4). README'deki yeni blok bunu ve adım 6–9'un işletimini (Wh, `.env`, karışık filo denemesi, kuru → `--apply` `enable`, kill switch `disable`) yazar. Satır kipi saha kontrolünün DB sorguları README'ye DEĞİL, planın sonundaki "Sürüm / saha notları"na girer (Burak koşar, SA parolası okunmaz).

**Not (spec ↔ kod):**
- Spec §7.2'nin compose atfı `:163-196` bugün SalesSync bloğu `:213-227`'dir (iskelet fark 1); satır o bloğun ardına girer.
- Spec §7.4'ün README için istediği tek uyarı ("Ortak bakiye S1 tabanı") S1'de (#533) yazıldı (`deploy/README.md:430-486`) ve S2 deploy'unun düşmesini de anlatır (`:461-465`). **Çözüm:** S1 bloğu aynen kalır; S2 yalnız ardına işletim bloğunu ekler (S1 planı Görev 14 biçiminde).
- Spec §7.2 "Panelde çekim grubunun iptali satır kipinde de grubun **herhangi bir satırından** açılabilir (§4.9)". README metni spec'in lafzını kullanır (talimattaki "rota satırından" daha dar; spec kazanır).
- `PersonBalance__ActivePcWindowDays` compose'a EKLENMEZ (iskelet fark 6): prod'da kod varsayılanı 14. Bekçi testi bunu sabitler; eklenirse boş varsayılanla `int` binder'ı açılışı düşürürdü (compose `:222-224` dersi).
- Saha notunun `/metrics` beklentisi: OpenTelemetry Prometheus dışlayıcısı hiç ölçüm almamış sayacı yayımlamaz; kip kapalıyken `orderdeck_balance_*` satırlarının hiç görünmemesi normaldir (talimattaki "değer 0" satırı çıkmaz). Beklenti "havuza özgü olanlar yok" olarak yazıldı. `/metrics` Caddy'de dışarıya kapalıdır (`deploy/Caddyfile:148-159`); kontrol caddy konteynerinin içinden yapılır.

- [ ] **Adım 1: Deploy yüzeyi testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDeployTests.cs`:

```csharp
using System.Text.RegularExpressions;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Ortak bakiye S2'nin deploy yüzeyi — spec 2026-10-11 §7.2, §7.4 (saf, fikstürsüz). Prod'da license-server'ın
/// <c>env_file</c>'ı yok: <c>.env</c>'deki <c>PersonBalance__MinClientVersion</c> container'a yalnız compose'un
/// <c>environment:</c> eşlemesindeki satırla geçer. Satır yoksa Wh yayımlanıp <c>.env</c>'e yazıldıktan sonra da alt
/// sınır boş kalır: <c>person-balance enable --apply</c> hep reddeder, havuz kipindeki lisansta WPF'in preview'ı ve
/// yeni apply'ı (kayıtlı anahtarın oynatması etkilenmez) 409 alır.
/// README'nin <c>.env</c> şablonu ve S2 işletim bloğu (enable/disable komutları) da burada sabitlenir.
/// </summary>
public sealed class PersonBalanceDeployTests
{
    private static readonly string MinClientVersionKey =
        $"{PersonBalanceOptions.Section}__{nameof(PersonBalanceOptions.MinClientVersion)}";

    private static readonly string ActivePcWindowKey =
        $"{PersonBalanceOptions.Section}__{nameof(PersonBalanceOptions.ActivePcWindowDays)}";

    /// <summary>Boş varsayılan (<c>:-</c>): Wh yayımlanana kadar kapı kapalı. <c>:?</c> eksik <c>.env</c> satırında CI'ın
    /// <c>docker compose config -q</c> kapısını düşürüp her deploy'u kilitlerdi.</summary>
    [Fact]
    public void Compose_alt_sinir_satiri_bos_varsayilanla_var()
    {
        var compose = ReadRepoFile("deploy/docker-compose.yml");

        compose.Should().Contain(
            "      PersonBalance__MinClientVersion: \"${PersonBalance__MinClientVersion:-}\"",
            "deploy/docker-compose.yml license-server environment: bloğunda alt sınırın satırı boş varsayılanla olmalı");
        compose.Should().NotContain(
            "${PersonBalance__MinClientVersion:?",
            "eksik .env satırı config -q kapısını düşürüp deploy'u kilitlememeli");
    }

    /// <summary>Etkin PC penceresi compose'da YOK (plan fark 6): prod'da kod varsayılanı 14 gün. Boş varsayılanla
    /// eklenirse int binder'ı boş dizeyi ayrıştıramaz ve sunucu açılmaz.</summary>
    [Fact]
    public void Compose_pencere_ayari_icermez()
    {
        var compose = ReadRepoFile("deploy/docker-compose.yml");

        compose.Should().NotContain(
            ActivePcWindowKey,
            "pencere ayarı yalnız kod varsayılanından gelir; compose satırı boş dizeyle sunucuyu düşürebilirdi");
    }

    /// <summary>Compose'daki anahtar, seçenek sınıfının bağlandığı bölüm + özellik adıyla aynı olmalı: biri yeniden
    /// adlandırılırsa değer sessizce bağlanmaz.</summary>
    [Fact]
    public void Compose_anahtari_secenek_bolumuyle_eslesir()
    {
        var compose = ReadRepoFile("deploy/docker-compose.yml");

        compose.Should().Contain(
            $"{MinClientVersionKey}: \"${{{MinClientVersionKey}:-}}\"",
            "compose anahtarı PersonBalanceOptions.Section + MinClientVersion'dan türemeli");
    }

    /// <summary>.env şablonunda satır başında anahtar: Burak Wh numarasını buraya yazar.</summary>
    [Fact]
    public void README_env_sablonunda_alt_sinir_satiri_var()
    {
        var readme = ReadRepoFile("deploy/README.md");

        Regex.IsMatch(readme, $"^{Regex.Escape(MinClientVersionKey)}=", RegexOptions.Multiline)
            .Should().BeTrue("deploy/README.md .env şablonunda PersonBalance__MinClientVersion= satırı olmalı");
    }

    /// <summary>S2 işletim bloğu: havuz kipini yalnız CLI açar/kapatır; kill switch komutu README'de bulunmalı.</summary>
    [Fact]
    public void README_S2_geri_alma_ve_disable_komutu_var()
    {
        var readme = ReadRepoFile("deploy/README.md");

        readme.Should().Contain("Ortak bakiye S2", "rollback bölümünde S2 bloğu olmalı")
            .And.Contain("person-balance enable --license", "pilot açılışının komutu yazılı olmalı")
            .And.Contain("person-balance disable --license", "kill switch komutu yazılı olmalı");
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(FindRepoFile(relativePath));

    /// <summary>Testler bin klasöründen koşuyor; repo kökünü yukarı yürüyerek buluyoruz (LicensesSalesControllerTests gibi).</summary>
    private static string FindRepoFile(string relativePath)
    {
        var parts = relativePath.Split('/');
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"{relativePath} bulunamadı ({AppContext.BaseDirectory} klasöründen yukarı arandı). " +
            "Dosya taşındıysa bu testi güncelle; testi silme.");
    }
}
```

(`PersonBalanceOptions.Section`, `MinClientVersion` ve `ActivePcWindowDays` Görev 1'den; `File`, `Path`, `DirectoryInfo`, `AppContext` `ImplicitUsings` ile gelir. Worktree'den koşunca yukarı yürüyüş önce worktree'nin `deploy/`'unu bulur.)

- [ ] **Adım 2: Koştur — FAIL**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDeployTests"`
Expected: FAIL — 4 test kırmızı: `Compose_alt_sinir_satiri_bos_varsayilanla_var` ve `Compose_anahtari_secenek_bolumuyle_eslesir` (compose'da satır yok), `README_env_sablonunda_alt_sinir_satiri_var` (şablonda satır yok), `README_S2_geri_alma_ve_disable_komutu_var` ("Ortak bakiye S2" yok). `Compose_pencere_ayari_icermez` bugün de GEÇER (bekçi testi; pencere satırının ileride eklenmesini yakalar).

- [ ] **Adım 3: Compose satırı**

`deploy/docker-compose.yml` — bugünkü `:225-227`:

```yaml
      SalesSync__MinClientVersion: "${SalesSync__MinClientVersion:-}"
      SalesSync__ScrubOrderTextOnPurge: "${SalesSync__ScrubOrderTextOnPurge:-true}"
      SalesSync__ForceEndSilenceMinutes: "${SalesSync__ForceEndSilenceMinutes:-15}"
```

şununla değişir (ilk üç satır aynen, ardına yorum + satır; girinti 6 boşluk, sonraki `# Kayıt formu "hesabınla bağlan"` yorumu yerinde kalır):

```yaml
      SalesSync__MinClientVersion: "${SalesSync__MinClientVersion:-}"
      SalesSync__ScrubOrderTextOnPurge: "${SalesSync__ScrubOrderTextOnPurge:-true}"
      SalesSync__ForceEndSilenceMinutes: "${SalesSync__ForceEndSilenceMinutes:-15}"
      # Ortak bakiye (spec 2026-10-11 §7.2). MinClientVersion = Wh (W-havuz'u
      # taşıyan ilk Velopack sürümü); Wh yayımlanana kadar BOŞ: boşken
      # `person-balance enable --apply` reddeder, havuz kipindeki lisansta
      # WPF'in preview'ı ve yeni apply'ı (kayıtlı anahtarın oynatması
      # etkilenmez) 409 client-update-required (kapalı-hata, açılışta
      # uyarı); WPF reverse ve panel işlemleri etkilenmez.
      # Değer SalesSync__MinClientVersion'dan küçük olamaz (Wh ⊇ Ws,
      # enable'ın 4. koşulu). `:?` DEĞİL `:-`: eksik satır `config -q` kapısını
      # düşürüp deploy'u kilitlemesin. ActivePcWindowDays compose'da YOK: kod
      # varsayılanı 14 gün; boş string'le eklenirse int binder'ı ayrıştıramaz.
      PersonBalance__MinClientVersion: "${PersonBalance__MinClientVersion:-}"
```

(Dosya CRLF; düzenlemeyi Edit aracıyla yap, PowerShell `Set-Content` kullanma.)

- [ ] **Adım 4: README — `.env` şablonu**

`deploy/README.md` — bugünkü `:169-172`:

```env
# Canlı yayının sahibi kaç dakika susunca başka makinedeki (geri yüklenmiş) kopya
# yayını devralabilir (Y7, Y8; devrediliyor yayın hiç devralınmaz). 0'dan büyük
# olmalı; 0 ya da eksi değerle sunucu açılmaz.
SalesSync__ForceEndSilenceMinutes=15
```

şununla değişir (dört satır aynen, ardına boş satır + yeni blok; sonraki boş satır ve `# Optional: SMTP …` yerinde kalır):

```env
# Canlı yayının sahibi kaç dakika susunca başka makinedeki (geri yüklenmiş) kopya
# yayını devralabilir (Y7, Y8; devrediliyor yayın hiç devralınmaz). 0'dan büyük
# olmalı; 0 ya da eksi değerle sunucu açılmaz.
SalesSync__ForceEndSilenceMinutes=15

# Ortak bakiye (spec 2026-10-11 §7.2). docker-compose.yml'de listeli.
# Wh'nin (ortak bakiyenin WPF değişikliklerini — W-havuz — taşıyan ilk Velopack
# sürümü) numarası. Wh yayımlanana kadar BOŞ kalır: boşken
# `person-balance enable --apply` reddeder; havuz kipindeki bir lisansta WPF'in
# preview'ı ve yeni apply'ı (kayıtlı anahtarın oynatması etkilenmez) 409
# client-update-required döner (kapalı-hata, açılışta uyarı); WPF reverse ve
# panel işlemleri etkilenmez. Wh yayımlanıp PC'ler güncellenince sürümü yaz ve
# `docker compose up -d license-server` (aşağıda "Ortak bakiye S2"). Değer
# SalesSync__MinClientVersion'dan küçük olamaz (Wh ⊇ Ws; enable'ın 4. koşulu).
# Etkin PC penceresi (PersonBalance:ActivePcWindowDays) burada YOK: kod
# varsayılanı 14 gün.
PersonBalance__MinClientVersion=
```

- [ ] **Adım 5: README — S2 işletim bloğu**

`deploy/README.md` — bugünkü `:485-488`:

```markdown
>    `--mark-reviewed --apply` lisansın incelenmemiş bütün taban satırlarını
>    tek seferde işaretler ve S2 `enable`'ının 3. ön koşulunu açar.

## Masaüstü: satış senkronunu yerelde sıfırlama (≤0.9.10'a dönüş)
```

şununla değişir (S1 bloğunun son iki satırı ve başlık aynen; araya boş satırlarla yeni blok):

```markdown
>    `--mark-reviewed --apply` lisansın incelenmemiş bütün taban satırlarını
>    tek seferde işaretler ve S2 `enable`'ının 3. ön koşulunu açar.

> **Ortak bakiye S2 — havuz kipi (spec 2026-10-11 §7.1 adım 5–9, §7.2, §7.4).**
> S2 göç eklemez. Deploy anında hiçbir lisans havuz kipinde değildir
> (`Licenses.PersonBalancePoolSince` boş; onu yalnız aşağıdaki `enable --apply`
> yazar) ve satır kipinin davranışı değişmez. Otomatik geri alma (yukarıdaki
> workflow) S1 imajına döner ve güvenlidir: S1 kip bayrağını okumaz, bütün
> lisanslar satır kipine iner; S1 çok bacaklı geçmişi okur ve grup olarak geri
> alır. Havuz verisi oluştuktan sonra elle S1'e dönmek de para kaybettirmez
> (S1 bacakları okur, grup olarak geri alır ya da `not-reversible` ile
> reddeder); S1 öncesine dönüş yukarıdaki S1 bloğunun kuralına tabidir.
>
> Havuz kipini YALNIZ CLI açar ve kapatır. Komutlar `--apply` olmadan kurudur:
> koşulları ve yapılacak değişikliği basar, hiçbir şey yazmaz. Çıkış kodu
> 0 = tamam, kuru ya da yapılacak bir şey yok; 1 = eşzamanlı değişiklik ya da
> `--apply`'da koşul tutmuyor (hiçbir şey yazılmadı); 2 = kullanım,
> yapılandırma ya da bilinmeyen lisans (`Tools/PersonBalanceCommand.cs`).
>
> Açılış (Burak; yayın penceresi dışında — Paz/Pzt/Çar/Per 20:00–01:00 TR
> değil; prod'a yazım Burak'ın onayıyla):
>
> 1. W-havuz'u taşıyan **Wh** Velopack sürümü yayımlandı ve lisansın PC'leri
>    güncellendi. Wh, satış senkronunun Ws değişikliklerini de taşır.
> 2. `.env`'e `PersonBalance__MinClientVersion=<Wh sürüm numarası>` yaz
>    (`SalesSync__MinClientVersion` dolu ve ondan küçük ya da eşit olmalı),
>    sonra `docker compose up -d license-server`.
> 3. Test lisansında karışık filo denemesi (spec §8.5, §7.5). Havuz kipi
>    AÇIKKEN yapılır: satır kipinde eski PC havuz kapısına hiç takılmaz.
>    (a) Test lisansının S1 adım 2–4'ü tamam (taban var, yukarıdaki S1
>    bloğunun 3. adımıyla `--mark-reviewed --apply` yapılmış).
>    (b) Bütün PC'leri Wh'deyken 4. maddedeki kuru → `--apply` `enable`
>    komutlarını `--license <test-id>` ile koş. Önce kip açılır, eski sürüm
>    ondan SONRA gelir: `enable`'ın 1. koşulu son 14 günde görülmüş eski bir
>    PC varken reddeder.
>    (c) Bir PC'yi Wh'den eski bir sürüme indir, bakiyeli bir müşteriye ödeme
>    iste. v0.9.9 ile beklenen: mesaj engellenir, iş `NoBalance` olmaz, apply
>    belirsiz kalır; sunucu günlüğünde ve metrikte `client-update-required`
>    görünür. Kapı sırası satış senkronu → havuz: lisansın satış senkronu modu
>    On ve sürüm `SalesSync__MinClientVersion`'dan küçükse (v0.9.9 öyle) 409
>    satış kapısından gelir (`gate="sales-sync"`). Havuz kapısını
>    (`gate="person-balance"`) görmek için denemeyi
>    `SalesSync__MinClientVersion`'dan küçük olmayan ama Wh'den eski bir
>    sürümle tekrarla (satış senkronu modu Off ise v0.9.9 da yeter).
>    (d) Yalnız #506 taşıyan bir sürümle: yerel plan sorusunda "Evet" sonrası
>    geri alma her bacağı kendi satırına döndürür (DB'de bak); sonra önizleme
>    409 alır ve mesaj gitmez.
>    (e) Bitince gerekiyorsa test lisansını kapat: önce kuru, sonra
>    `person-balance disable --license <test-id> --apply` (komutun tamamı
>    aşağıda, kill switch).
> 4. Pilot lisansta önce kuru — dört koşulu sayılarla, iki alt sınırı yan
>    yana basar:
>    `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance enable --license <id>`
>    Dördü de tutuyorsa:
>    `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance enable --license <id> --apply`
>    Koşullar: (1) son 14 günde görülmüş, devre dışı bırakılmamış her
>    etkinleştirmenin sürümü ≥ alt sınır; (2) `verify`'ın D4, D5, D6, yetim
>    bacak ve kırpma farkı 0; (3) taban var, bütün satırları incelenmiş
>    (`--mark-reviewed`) ve tabana göre yeni sapma yok; (4) alt sınır ≥
>    `SalesSync__MinClientVersion`.
> 5. Saha kontrolleri (spec §8.5; gerçek etkiden: DB satırı, yanıt, ekran).
> 6. Öbür lisanslar tek tek (spec §7.1 adım 9). Her biri için: tabanı
>    incelenmemişse önce yukarıdaki S1 bloğunun 3. adımı (inceleme, ardından
>    `--mark-reviewed --apply`; spec §7.1 adım 4), sonra 4 ve 5.
>
> **Havuz kipinde sorun — kill switch.** Önce kuru, sonra yazar:
> `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance disable --license <id>`
> `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance disable --license <id> --apply`
> Lisans satır kipine iner; veri işlemi gerekmez. Çok bacaklı geçmiş doğru
> okunmaya ve geri alınmaya devam eder; panelde bir çekim grubunun iptali
> satır kipinde de grubun herhangi bir satırından açılır.
> `PersonBalance__MinClientVersion`'ı boşaltmak kill switch DEĞİLDİR: havuz
> kipindeki lisansta yalnız WPF preview'ını ve yeni apply'ı 409
> `client-update-required`'a düşürür (kapalı-hata) ve açılışta uyarı yazar.
> Panelin eksi ayarı ve iptali havuzdan çekmeyi sürdürür. Kipten çıkmanın
> yolu `disable`.
> Elle S1'e dönüş havuz bayrağını SİLMEZ: S2'ye yeniden çıkınca bayraklı
> lisanslar kapı yeniden değerlendirilmeden havuz kipine döner (kip zaten
> açıkken `enable` koşulları basmaz). Bu yüzden dönüşten ÖNCE, S2 imajı
> ayaktayken bayraklı her lisansta `disable` koş (kuru, sonra `--apply`);
> S1 imajı komutu tanımaz (kullanım, çıkış 2). S2'ye yeniden çıkınca her
> lisansı yukarıdaki 4. adımla (kuru, dört koşul tutarsa `--apply`) yeniden aç.

## Masaüstü: satış senkronunu yerelde sıfırlama (≤0.9.10'a dönüş)
```

(Repo public: blokta IP, parola, `.env` değeri ya da müşteri verisi yok; yalnız anahtar adları ve komutlar.)

- [ ] **Adım 6: Koştur — PASS + compose regresyonu**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceDeployTests|FullyQualifiedName~LicensesSalesControllerTests.Compose_bosaltma_varsayilani_acik|FullyQualifiedName~ObifinDataControllerGateTests.Bayrak_compose_ile_containera_gecer_varsayilani_true"`
Expected: PASS — yeni 5 test ve compose'u okuyan iki var olan test (SalesSync ve Obifin satırları düzenlemeden etkilenmedi).

- [ ] **Adım 7: Sunucu derlemesi — 0 uyarı**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj -warnaserror --nologo -v q`
Expected: `Build succeeded.` `0 Warning(s)`. (Bu görev üretim koduna dokunmaz; derleme, Görev 1–16'nın birikmiş hâlinin temiz olduğunu tam paketten önce gösterir.)

- [ ] **Adım 7b: Göç yok kanıtı (spec §7.4; Genel kurallar "S2 göç eklemez")**

S1'e otomatik geri almanın güvenliği genişlet/daralt kuralına, yani S2'nin göçsüz deploy edilmesine dayanır. Bunu merge'den önce dalın kendisinden kanıtla (deploy sonrası `__EFMigrationsHistory` kontrolü ancak ikinci kanıttır). Worktree kökünden:

```bash
git diff --stat "$(git merge-base origin/master HEAD)" -- OrderDeck.LicenseServer/Data/Migrations/
git status --porcelain -- OrderDeck.LicenseServer/Data/Migrations/
```

Expected: iki komutun çıktısı da BOŞ — dalın ayrıldığı master noktasına göre (commit'lenmiş ve commit'lenmemiş değişiklikler dahil) yeni göç dosyası yok, `LicenseDbContextModelSnapshot.cs` değişmemiş; `git status` izlenmeyen (eklenmemiş) bir göç dosyası da göstermiyor. Taban `merge-base`'tir: master bu arada başka bir PR'ın göçüyle ilerlemişse o göç S2'ninmiş gibi görünmez. Çıktı doluysa **dur ve sor** (Genel kurallar: model değişikliği gerekirse dur ve sor; S1'e otomatik dönüş ancak göçsüz deploy'da güvenli). Adım 10'daki PR açıklamasının "göç yok" cümlesi bu iki boş çıktıya dayanır.

- [ ] **Adım 8: Tam sunucu paketi (yürütücü koşar; detached, ~12+ dk, Docker açık)**

Bu adımı **alt ajan koşturmaz ve beklemez** (Monitor ya da arka plan bekleme yasak): yürütücü (ana oturum) başlatır ve bitişi `.done` dosyasıyla kendisi denetler. PowerShell'den (Testcontainers için gerekiyorsa önce `$env:DOCKER_HOST` — "Genel kurallar"):

```powershell
$root = 'C:\Users\burak\source\repos\LiveDeck\.claude\worktrees\ortak-bakiye-s2'
$log  = Join-Path $env:TEMP 'ortak-bakiye-s2-tam-paket.txt'
$done = Join-Path $env:TEMP 'ortak-bakiye-s2-tam-paket.done'
Remove-Item $log, $done -ErrorAction SilentlyContinue
Start-Process -FilePath powershell.exe -WindowStyle Hidden -ArgumentList @(
  '-NoProfile', '-Command',
  "Set-Location '$root'; `$env:DOTNET_CLI_UI_LANGUAGE='en'; " +
  "dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q 2>&1 | Out-File -Encoding utf8 '$log'; " +
  "`$LASTEXITCODE | Out-File -Encoding utf8 '$done'")
```

Yürütücü bitişi `Test-Path $done` ile denetler; bitince:

```powershell
Get-Content $done -Encoding UTF8
Select-String -Path $log -Pattern 'Passed!|Failed!|error' -Encoding UTF8
```

Expected: `$done` = `0`, özet satırı `Passed!  - Failed: 0`. Sayıyı plan ya da CLAUDE.md'ye yazma (CI'dan okunur). CI'nın 6 parçası (`ORDERDECK_TEST_SHARD=relational:i/4`, `inmemory:i/2`) yerelde gerekmez — CI koşar; yeni ilişkisel sınıflar (Görev 12, 14–16) koleksiyonlarından ötürü kendiliğinden ilişkisel parçalara düşer. Kırmızı test varsa: önce hangi görevin dosyasına ait olduğunu bul, sistematik hata ayıklama; var olan bir testi spec gerekçesi olmadan değiştirme.

- [ ] **Adım 9: Commit**

```bash
git add deploy/docker-compose.yml deploy/README.md \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceDeployTests.cs
git commit -F - <<'MSG'
docs(deploy): ortak bakiye S2 — compose alt sınırı, enable/disable işletimi

PersonBalance__MinClientVersion compose ortamına boş varsayılanla girer:
satır olmadan .env'deki Wh numarası container'a ulaşmaz ve enable --apply
hep reddederdi; `:?` eksik satırda deploy'u kilitlerdi. README'ye .env
şablonu ve S2 işletim bloğu eklenir: göç yok, otomatik geri alma S1'e
güvenli, enable önce kuru sonra --apply, kill switch disable (ortak bakiye
§7.1 adım 5–9, §7.2, §7.4). Testler satırı, pencere ayarının yokluğunu ve
README komutlarını sabitler.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

- [ ] **Adım 10: Push ve PR — yalnız Burak isterse**

Bu plan push etmez. Burak PR isterse: `git push -u origin feat/ortak-bakiye-s2`, başlık `feat(balance): ortak bakiye S2 — havuz kipi (kapalı), DrawAsync, K5, enable/disable`; PR **DRAFT** açılır. Açıklamada: "Deploy anında hiçbir lisans havuz kipinde değil; satır kipinin davranışı değişmez; göç yok (otomatik geri alma S1'e güvenli)" — "göç yok" cümlesinin kanıtı Adım 7b'nin iki boş çıktısıdır; açıklamaya komutları ve "çıktı boş" sonucunu ekle (Adım 7b'den sonra göç eklendiyse cümleyi yazma, dur ve sor). Ayrıca iki sahip sorusunun yanıtı ve planın sonundaki "Sürüm / saha notları". Merge Burak'ta, **yayın penceresi dışında** (Paz/Pzt/Çar/Per 20:00–01:00 TR değil); merge = otomatik prod deploy.

---

## Sürüm / saha notları (S2 sonrası, spec §7.1 adım 5–9, §8.5)

Bütün DB sorgularını **Burak koşar** (SA parolası okunmaz); çıktılarda yalnız Id, sayı ve tutar var, ad ya da telefon yok. Prod'a yazan her adım Burak'ın onayıyla ve **yayın penceresi dışında** yapılır (Paz/Pzt/Çar/Per 20:00–01:00 TR değil). Doğrulama gerçek etkiden yapılır: DB satırı, yanıt, ekran. Workflow'un yeşil olması tek başına yetmez.

1. **Merge öncesi (adım 5'in ön koşulu)**
   - S1 adım 0–4 prod'da tamam (29.10.2026): `verify` temiz, `--capture-baseline --apply` yapıldı, dört lisansta `--mark-reviewed --apply` yapıldı.
   - Sahip kararları 1 ve 2 (29.10.2026, iskelet "Sahip kararları") plana işlendi: 1 → (b), preview kural 1'in kesin kolunda 200 `Balance = 0`, `PersonPool = true`, apply 409 `no-balance` (fark 19, Görev 5); 2 → (a), S2'de değişiklik yok (prod tabanında düzeltilecek satır yok).
   - Görev 17 Adım 7b'nin iki komutu (göç dizini, `merge-base`'e göre) boş çıktı verdi. Doluysa merge yok: dur ve sor.
   - Görev 17 Adım 8'in tam paketi ve CI'nın 6 parçası yeşil. Sayı plana yazılmaz.
   - Burak deploy öncesinin göç tabanını not eder: `SELECT TOP 1 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC;`.
   - `.env`'e `PersonBalance__MinClientVersion` satırı EKLENMEZ: compose `:-` ile boş verir, kapı kapalı kalır. `SalesSync__MinClientVersion`'ın bugünkü değeri not edilir (`enable`'ın 4. koşulunun ikinci değeri) ve değiştirilmez.
   - PR DRAFT'tır. Hazır duruma alma ve merge Burak'ındır. Merge otomatik prod deploy demektir.

2. **Deploy doğrulaması (adım 5; kip kapalı)**
   - Deploy workflow'u yeşil ve geri alma tetiklenmedi. `curl -s -o /dev/null -w "%{http_code}\n" https://license.orderdeckapp.com/ready` → `200`. VPS'te `cat /opt/orderdeck/.deployed_sha` merge commit'ini gösterir. Geri alma çalışmış olsaydı önceki (S1) sha görünürdü.
   - **Göç yok.** 1. maddedeki sorgu aynı `MigrationId`'yi döner (master `b6ee847c`'de en yenisi `20261024173809_LicenseMediaPurgeTasks`). Yeni bir satır varsa S2'den gelmemiştir; aynı deploy'a giren PR'ı bul.
   - **Günlükte `fail`/`crit` yok.** `docker logs orderdeck-license --since <deploy anı> 2>&1 | grep -E "^(fail|crit):"` → boş. Açılış `OptionsValidationException` ile düşmedi (pencere kod varsayılanı olan 14 gün).
   - Açılış uyarısı yok: `docker logs orderdeck-license --since <deploy anı> 2>&1 | grep "PersonBalance:MinClientVersion tanımsız"` → boş. Uyarı yalnız alt sınır boş/bozukken **ve** havuz kipinde ≥ 1 lisans varken çıkar (Görev 1).
   - `SELECT COUNT(*) FROM Licenses WHERE PersonBalancePoolSince IS NOT NULL;` → `0`.

3. **Satır kipinde davranış aynı (spec §8.5 "S2 sonrası")**
   - Bir müşteride WPF'ten ödeme isteği: önizleme o satırın bakiyesini ve `personPool: false`'ı döner. Gönderim (apply) ve revizyon (reverse) bugünkü gibi. Düşülen ve geri alınan tutar o satırdadır. Panel kartı ve Shopper değişmedi (S1'in toplanmış listesi).
   - Bilinen tek satır kipi farkı (iskelet fark 18f): Guid'e bağlanamayan `memberIds` değeri preview'da ve apply'da kip okunmadan 400 `ValidationProblem` alır. Bugünkü WPF bu alanı hiç göndermez; sahada görünmemesi beklenir.
   - Deploy anını UTC yazarak: `SELECT COUNT(*) FROM CustomerBalanceTransactions WHERE DrawGroupId IS NOT NULL AND CreatedAt >= '<deploy anı, UTC>';` → `0`. Satır kipinin yazımları ve geri almaları `DrawGroupId = NULL`'dır (M13).
   - Bacak denetimi (S1 README bloğunun sorgusu): `SELECT COUNT(*) FROM CustomerBalanceTransactions WHERE DrawGroupId IS NOT NULL AND DrawGroupId <> Id;` → `0`.
   - Metrikler dışarıya kapalıdır, caddy konteynerinden okunur: `docker exec orderdeck-caddy wget -qO- http://license-server:8080/metrics | grep orderdeck_balance`.
     - Kip kapalıyken satır çıkmaması normaldir: OpenTelemetry hiç ölçülmemiş sayacı yayımlamaz.
     - Şunlar OLMAMALI: `orderdeck_balance_customer_not_synced_total`, `orderdeck_balance_multi_leg_draws_total` ve `gate="person-balance"` etiketli `orderdeck_balance_client_update_required_total`.
     - Görünebilecekler: `gate="sales-sync"` etiketli `client_update_required` (satış senkronu modu On olan lisansta eski PC; `endpoint` `preview`, `scope`, `apply` ya da `reverse`) ve `orderdeck_balance_busy_total` (`endpoint="reverse"` ya da `"panel/reverse"`: S1'in grup geri alması satır kipinde de 503 verebilir).
   - **Ertesi gün:** Hangfire panosunda ve günlükte `person-balance-verify` uyarısız, uyarı e-postası yok. D3 yalnız havuz kipindeki lisansta uyarır.

4. **`enable`'ın kuru koşusu (salt okunur; S2'nin canlı olduğunun da kanıtı)**
   - Pilot adayı lisansta: `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance enable --license <pilot-id>; echo "exit=$?"`. S1 imajı `enable`'ı tanımaz, Usage basar ve `2` döner. Dört koşulun basılması S2 imajının canlı olduğunu gösterir.
   - Bugün beklenen çıktı ve nedeni:

     | Koşul | Beklenen | Neden |
     |---|---|---|
     | 1. istemci sürümü | **TUTMUYOR**, `PersonBalance:MinClientVersion: tanımsız`, "karşılaştırılamadı" | Wh yok (W-havuz ayrı PR), alt sınır boş |
     | 2. verify (D4, D5, D6, yetim, kırpma, kontrol karakteri) | TUTUYOR, hepsi 0 | `verify` temiz (29.10) |
     | 3. taban | TUTUYOR | 29.10'da `--mark-reviewed --apply` yapılan dört lisans için. Başka bir lisansta "incelenmemiş ≥ 1" → TUTMUYOR: önce §7.1 adım 4 |
     | 4. Wh ⊇ Ws | **TUTMUYOR**, `PersonBalance:MinClientVersion=tanımsız`, yanında `SalesSync:MinClientVersion=<Ws>` | Alt sınır boş |

     Son satırlar `Sonuç: koşul 1, 4 tutmuyor` ve `Kuru çalıştırma: hiçbir şey yazılmadı.`, çıkış `0`. Bu bir hata değildir: kapının kapalı olduğunun beklenen kanıtıdır.
   - 2. ya da 3. koşul TUTMUYOR çıkarsa: `… person-balance verify --license <id> --details` ile bakılır. Bu `enable`'dan önce çözülmesi gereken bir veri konusudur, S2'nin geri alınmasını gerektirmez. Farkı değişen D1 taban satırı `enable`'ı engeller (sahip kararı 2, (a): S2'de değişiklik yok; 29.10'da prod tabanı yalnız işaret satırlarıydı, böyle bir satır çıkarsa ayrı karar gerekir).
   - `--apply` KOŞTURULMAZ. Koşturulsa da 1 döner ve hiçbir şey yazmaz. Ardından `SELECT COUNT(*) FROM Licenses WHERE PersonBalancePoolSince IS NOT NULL;` → `0`.

5. **Adım 6: W-havuz ve Wh (ayrı PR; bu planın dışında)**
   - W-havuz sunucu sözleşmesini aynen kullanır. Preview'da `memberIds` tekrarlı sorgu parametresidir, apply gövdesinde `memberIds` alanıdır (fark 18f). İstemcinin ayırması gereken yanıtlar: 409 `customer-not-synced`, `client-update-required`, `no-balance` (yalnız apply) ve 400 `member-ids-required`. 503 `balance-busy` ve 404 bunlara eşlenmez: genel belirsiz kolda kalır, iş anahtarıyla bekler ve aynı anahtarla yeniden denenir (bugünkü dayanıklılık katmanı 5xx'i zaten yeniden dener). Sahip kararı 1 gereği preview kural 1'in kesin kolunda 409 değil 200 `Balance = 0`, `PersonPool = true` döner; W-havuz preview'da `no-balance` için eşleme gerektirmez (bakiye ≤ 0 → `NoBalance` bugünkü yoldan).
   - S2 ile Wh'nin sırası serbest; enable ikisini de ister.
   - Wh ⊇ Ws. Wh Velopack sürümü yayımlanır ve PC'ler güncellenir. Ardından `.env`'e `PersonBalance__MinClientVersion=<Wh>` yazılır ve `docker compose up -d license-server` koşulur.
   - Doğrulama 4. maddedeki kuru koşunun tekrarıdır. 4. koşul TUTUYOR olur ve iki değer yan yana basılır. 1. koşul ya TUTUYOR olur ya da Wh'ye geçmemiş etkin PC'leri Id ve sürümle listeler. Açılış uyarısı yine yoktur (hiçbir lisans havuz kipinde değil).

6. **Adım 7a: karışık filo denemesi (test lisansında, havuz kipi AÇIKKEN; spec §8.5, §7.5)**
   - **Ön koşul:** test lisansının tabanı var ve incelenmiş, koşul 2 ve 3 TUTUYOR. Lisansın satış senkronu modu (On/Off) not edilir.
   - Bütün PC'ler Wh'deyken: `person-balance enable --license <test-id>` (kuru), dört koşul TUTUYOR, sonra `--apply`. Sıra önemlidir: önce kip açılır, eski sürüm ondan SONRA kurulur. 1. koşul son 14 günde görülmüş eski bir PC varken reddeder.
   - **v0.9.9 kurulu PC, bakiyeli müşteriye ödeme isteği.** Beklenen: mesaj engellenir, iş `NoBalance` olmaz, apply belirsiz kalır. Sunucu günlüğünde ve metrikte `client-update-required` görünür.
     - Mod On ise ve sürüm `SalesSync__MinClientVersion`'ın altındaysa (v0.9.9 öyle) 409 satış kapısından gelir: `gate="sales-sync"`, önce `endpoint="scope"` ya da `"preview"`.
     - Havuz kapısını (`gate="person-balance"`, `endpoint="preview"`) görmek için deneme Ws'den küçük olmayan ama Wh'den eski bir sürümle tekrarlanır. Mod Off'ta v0.9.9 da yeter.
   - **Yalnız #506 taşıyan sürüm** (böyle bir sürüm yoksa madde atlanır ve nedeni yazılır).
     - Yerel plan sorusunda "Evet" sonrası geri alma her bacağı kendi satırına döndürür. DB'de bakılır: `SELECT WpfCustomerId, Amount, ReversesTransactionId, DrawGroupId FROM CustomerBalanceTransactions WHERE ReversesTransactionId IN (SELECT Id FROM CustomerBalanceTransactions WHERE DrawGroupId = '<çapa Id>');` → her bacağa bir `reversal` vardır, bacağın satırındadır ve ortak `DrawGroupId` taşır.
     - Ardından önizleme 409 alır ve mesaj gitmez (§7.5).
   - **Wh'deki PC.** İki üyesinde bakiye olan kişi: önizleme havuzu gösterir, apply çok bacaklı yazar (`orderdeck_balance_multi_leg_draws_total{legs="…"}` görünür), revizyon her bacağı kendi satırına döndürür.
   - Bitince gerekiyorsa test lisansı kapatılır: önce kuru, sonra `person-balance disable --license <test-id> --apply`.

7. **Adım 7b: pilot lisansta `enable`**
   - Önce kuru koşu: dört koşul da TUTUYOR olmalı.
   - Sonra `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance enable --license <pilot-id> --apply` → `UYGULANDI`, çıkış `0`. Burak'ın onayıyla ve yayın penceresi dışında yapılır.
   - `SELECT PersonBalancePoolSince FROM Licenses WHERE Id = '<pilot-id>';` → dolu. Açılıştan sonra günlükte "tanımsız" uyarısı yoktur (alt sınır dolu).

8. **Adım 8: saha kontrolleri (spec §8.5 "Pilot lisansta `enable` sonrası"; iki üyesinde bakiye olan bilinen bir kişi)**
   - WPF'te ödeme isteği önizlemesi havuzu gösterir (`personPool: true`).
   - DB'de aynı `DrawGroupId`'li bacaklar vardır: `SELECT DrawGroupId, COUNT(*) AS Satir, SUM(Amount) AS Toplam FROM CustomerBalanceTransactions WHERE LicenseId = '<pilot-id>' AND DrawGroupId IS NOT NULL AND CreatedAt >= '<enable anı, UTC>' GROUP BY DrawGroupId;` → çok bacaklı çekimde `Satir > 1`.
   - Revizyon her bacağı kendi satırına döndürür (6. maddedeki sorgu).
   - Panel kartı ve Shopper kişi toplamını gösterir. Başka üyenin `Reason`'ı Shopper'da görünmez.
   - Ayır onayı doğru dağılımı gösterir; Vazgeç ile çıkılır (W-havuz).
   - Yeni bir satırla Birleştir, hemen ardından Ödeme iste: önce "Henüz eşitlenmedi" çıkar, ≤ 60 sn sonra havuz gelir. Günlükte ve `orderdeck_balance_customer_not_synced_total`'da bir artış görünür.
   - `… person-balance verify --license <pilot-id>` → D4, D5, D6 ve yetim bacak 0, taban dışı D1 0. Ertesi gün `person-balance-verify` uyarısızdır; D3 satırı yoktur.
   - Günlükte `client-update-required` ve `customer-not-synced` sayıları, `/metrics`'te dört sayaç izlenir. `orderdeck_balance_busy_total` kişi kilidinin çakışma sıklığını gösterir: üç denemesi tükenen yazımları ve denemenin dışındaki bir okumanın kilitlenme kurbanı olduğu istekleri sayar (iki durumda da hiçbir şey yazılmadı). İkincisi günlükte Information satırıyla ayrılır: apply'da `oynatma Lookup'ı kilitlenme kurbanı (1205)`, panel iptalinde `üyelik okuması kilitlenme kurbanı (1205)` ya da `geri alındı mı okuması kilitlenme kurbanı (1205)`. Birincisinde servisin uyarısı vardır. Seyrek olması beklenir (§4.13).

9. **Adım 9: öbür lisanslar tek tek.** Her lisansta sırasıyla:
   - Tabanı incelenmemişse önce §7.1 adım 4: inceleme ve `--mark-reviewed --license <id> --apply`.
   - 7b (kuru, sonra `--apply`).
   - 8.
   - Ertesi gün `person-balance-verify` uyarısız olmalı.

10. **Geri dönüş**
    - Havuz kipinde sorun çıkarsa: `person-balance disable --license <id>`, önce kuru, sonra `--apply`. Bu kill switch'tir ve veri işlemi gerekmez. Çok bacaklı geçmiş S1 okuyucularıyla okunmaya ve grup olarak geri alınmaya devam eder; panelde bir çekim grubunun iptali satır kipinde de grubun herhangi bir satırından açılır (spec §7.2, §4.9).
    - `PersonBalance__MinClientVersion`'ı boşaltmak kill switch DEĞİLDİR. Yalnız WPF'in preview'ını ve yeni apply'ını 409'a düşürür (kayıtlı anahtarın oynatması etkilenmez); panelin eksi işlemleri havuzdan çekmeyi sürdürür.
    - S2 imajından S1 imajına elle dönmek güvenlidir (§7.4): S1 bacakları okur, grup olarak geri alır ya da `not-reversible` ile reddeder.
      - **Elle S1'e dönüş havuz bayrağını silmez.** S1 `PersonBalancePoolSince`'i okumaz ama silmez. S2'ye yeniden çıkınca bayraklı lisanslar `enable`'ın dört koşulu yeniden değerlendirilmeden havuz kipine döner. Oysa S1 döneminde eski sürümlü bir PC, yeni bir eksi satır ya da taban sapması oluşmuş olabilir.
      - Dönüşten önce `SELECT Id FROM Licenses WHERE PersonBalancePoolSince IS NOT NULL;` (Burak koşar) ile lisanslar not edilir.
      - Bu lisanslarda kip **dönüşten ÖNCE, S2 imajı ayaktayken** kapatılır: `person-balance disable --license <id>` önce kuru, sonra `--apply`. S1 imajı `disable`'ı tanımaz (Usage, çıkış 2).
      - S2'ye yeniden çıkışta her lisans normal açılış yoluyla yeniden açılır: kuru `person-balance enable --license <id>` (dört koşul), tutarsa `--apply`; ardından `person-balance verify --license <id>`. Kuru koşu bayrağı kapalı lisansta koşulmalıdır: kip açıkken `enable` kapıyı değerlendirmez, yalnız "Havuz kipi zaten açık" basar (Görev 12 `Zaten_acikken_enable_0_yazmaz`).
      - Dönüşten önce `disable` koşulamadıysa (S2 konteyneri ayakta değil), S2'ye yeniden çıkışın hemen ardından, yayın penceresi dışında not edilen her lisansta `disable --apply` koşulur, sonra bir üstteki madde uygulanır.
    - S1 ÖNCESİ bir imaja hiçbir zaman dönülmez (`deploy/README.md`, "Ortak bakiye S1 tabanı" uyarısı).

---

## Öz-inceleme (ilk tur plan yazıldıktan sonra; çapraz denetim ve düzelticilerden sonra 29.10 akşamı yenilendi)

**Spec kapsamı (§12 S2 satırı; R01–R73 ilk turda spec'ten çıkarılan liste).** Bu turda kanıt sütunu düzelticilerden sonraki parçalardan yeniden tarandı. Her test adı ilgili `gorev-NN.md`'de Grep ile bulundu. R listesi spec'ten yeniden çıkarılmadı.

| R | Gereksinim (spec) | Görev — davranış + test |
|---|---|---|
| R01 | Kip her istekte, lisans başına (§4.0) | 1 (`PersonBalanceModeTests`, `OwnedLicenseModeAsync`); dallar 5–10; `Bayrak_kapatilinca_satir_kipine_doner` (5, 7) |
| R02 | Satır kipinde `DrawGroupId` NULL (§3.2, M13) | 7 `Satir_kipinde_apply_DrawGroupId_NULL`, 9 `Satir_kipinde_eksi_bugunku_gibi`, 10 `Ornek4_satir_kipinde_bugunku_gibi`; saha notu 3 |
| R03 | `PersonBalance:MinClientVersion`, `ClientVersion` ile (§7.2, §15-3) | 1 `PersonBalanceOptionsTests` |
| R04 | Compose satırı, `:-` (§7.2) | 17 `PersonBalanceDeployTests` |
| R05 | `ActivePcWindowDays` = 14 (§7.2-1) | 1 (seçenek + `ValidateOnStart`), 11 `Kosul1_pencere_ayardan_okunur`, `Kosul1_gecersiz_pencere_tutmaz`, 12 `Pencere_gecersizken_enable_2_yazmaz` |
| R06 | Boş/bozuk alt sınırda kapalı-hata (§7.2) | 1 `PersonBalanceStartupTests` (açılış uyarısı); 5 `Alt_sinir_tanimsiz_havuz_kipinde_409_ve_hata_gunlugu`, 7 `Alt_sinir_tanimsiz_409_ve_hata_gunlugu`; 11 `Kosul1_alt_sinir_bos_ya_da_bozuk_tutmaz`; 12 `Kosul_tutmazken_enable_apply_1_yazmaz` |
| R07–R14 | Çekim kümesi: girdi, kural 1–4, silinmişler `S`'den, `S∖P` eksileri, ordinal (§4.0) | 2 `PersonBalanceDrawSetTests` (17 test). Kural 3b'nin izlenen satırlarla taze denetimi (R11/R27; Görev 4 "Kural 3b izlenen satırlarla yeniden"): 15 `Kural3b_W_disi_eksi_uye_izlenen_yuklemede_artiya_gecerse_customer_not_synced` |
| R15 | 409 günlüğü, lisans + satır Id'si (§5.5) | 5 `customer_not_synced_gunlugu_lisans_ve_satir_Idsi_icerir` |
| R16–R18 | Havuz kapısı, `member-ids-required`, sıra (§4.0, §4.2, §6.2) | 5 (`Eski_surum_memberIds_gondermese_de_409_client_update_required`, `memberIds_yok_400_member_ids_required`), 7 (`Eski_surum_memberIds_siz_de_409…`, `Kayitli_anahtar_eski_surumle_ve_memberIds_siz_oynatilir`); fark 3, 4 |
| R19 | `memberIds` taşıma biçimi | 1 (kit), 5 (`[FromQuery(Name = "memberIds")]`), 6 (`ApplyRequest.MemberIds`); fark 18f. Guid'e bağlanamayan değerin satır kipinde 400'ü: 5 `Satir_kipinde_memberIds_yok_sayilir_PersonPool_false` (preview), 6 `Satir_kipinde_memberIds_okunmaz_bozugu_400` (apply) |
| R20–R23 | Preview: satır kipi aynı; havuzda `max(0, Σ)`, `UpdatedAt` (M9), `PersonPool`, istekteki Id (§4.1) | 5 (`Havuz_kipinde_kesisim_ve_eksiler_…`, `Ornek_4_1_A50_B_eksi60_Balance_0`, `UpdatedAt_*`, `Kopya_Id_ile_istenince_yanit_istekteki_Id`) |
| R24 | Kural 1'in kesin kolu: preview 200 `Balance = 0`, apply 409 `no-balance` | 5 `Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir`; 7 `Kural1_kesin_409_no_balance` — **Sahip kararı 1 (b)**, fark 19 |
| R25–R32, R34, R37 | Apply adımları 1–7: ikinci üyelik okuması, plan, yazım, tek `SaveChanges`, baştan kurma, yanıt (§4.2) | 4 `PersonBalanceDrawTests`, 7 `PersonBalancePoolApplyTests`; yarış kanıtı 14, 15 (ikinci üyelik okuması: `Tc6_i/ii/iii`; R27'nin kural 3b kolu: yukarıdaki 15 testi) |
| R33 | Her denemede Lookup (§4.2-6) | 7 (geri çağrı; Not 6, fark 18n: `no-balance`'tan önce son Lookup); 14 `Tc10_araya_giren_ucuncu_yazimla_ikinci_istek_kazanani_oynatir` (ayırt edici iddia "tekrar oynatıldı" günlük sayısıdır, Görev 14 Not 2), `Ilk_denemede_havuzu_tuketen_ayni_anahtarli_rakip_oynatilir` (18n), `Ilk_denemede_ayni_anahtarli_rakip_yazdiysa_customer_not_synced_yerine_oynatilir` (18n, 30.10) |
| R35 | PK ihlalinde kazananı oynat | 4 `IsDuplicateTransactionKey_yalniz_PK_adini_tanir`, 7, 14 `Tc2_*` |
| R36 | 1205, okumalar dahil | 4 (yakalama sırası, fark 9), 14 `PersonBalanceDrawDeadlockTests`; panel iptalinin servis dışı üyelik okuması: 10 Not 6 (sarmal), 16 `Havuz_kipinde_iptalin_uyelik_okumasi_kilitlenme_kurbani_olursa_503_balance_busy` (iki yol); apply'ın servis dışı oynatma Lookup'ları (18n, 30.10): 14 `Cekimden_sonraki_Lookup_kilitlenme_kurbani_olursa_503_balance_busy`, `On_oynatmanin_bakiye_okumasi_kilitlenme_kurbani_olursa_503_balance_busy` |
| R38 | §6.6 kapsam kuralı: iki kipte, çapa düzeyinde, `−Σ(grup)`, deneme başına | 7 (InMemory: `Mod_On_kapsam_ikinci_farkli_anahtar_409_scope_has_deduction_eksi_sigma`, `Mod_Off_kapsam_kurali_yok`), 16 `Tc15_*`, `Ikinci_denemede_kilit_yeniden_alinir_…`, `Havuz_kipinde_kapsam_kilidi_zaman_asimi_409_stale_yazim_yok`; satır kipinde `CustomerBalanceScopeRule*Tests` değişmeden |
| R39 | Satış senkronu kapısı yerinde (§15-6) | 6 `PersonBalancePoolReadersModeOnTests`; 13 `client_update_required_satis_kapisi`, `client_update_required_havuz_kipinde_mod_On_once_satis_kapisi` (mod On + havuz kipinde sıra) |
| R40 | §5.2, §5.4, Örnek 2 | 7 `Yanlis_birlestirme_X100_Y50_sahip_Y_80`, `KVKK_A0_silinmis_P40_30`, `KVKK_geri_alinca_para_silinmis_uyeye_doner_ve_havuzdan_kullanilir` (§5.4'ün geri alma yarısı), `Ornek2_reverse_sonrasi_taze_apply` |
| R41 | A11 değişmez | 6 `Havuz_kipinde_A11_content_conflict_degismez`, 7 `Ayni_anahtar_farkli_icerik_409_content_conflict` |
| R42 | Oynatmada `RemainingBalance` = havuz | 2 `PoolBalanceAsync_*`, 6 `Havuz_kipinde_oynatma_RemainingBalance_havuz`, `…_memberIds_siz_oynatma_RemainingBalance_S_toplami`, `…_oynatma_kural_ihlalinde_de_200` |
| R43 | scope `PersonPool = true` (§4.3) | 6 `Havuz_kipinde_scope_PersonPool_true`, `Satir_kipinde_scope_PersonPool_false` |
| R44, R45 | Panel ve Shopper havuz okumaları, maske, silinmiş üye (§4.6, §4.10) | 8 `PersonBalancePoolPanelListTests`, `PersonBalancePoolShopperListTests` (`Reason_maskesi_baska_uyenin_aciklamasi_null`, `Silinmis_uye_dahil`) |
| R46 | İade uçları değişmez (§4.7) | kod değişmez; 16 `Paralel_iadeler_bakiyeyi_ledger_toplamindan_koparamaz(pool: true)` |
| R47–R49 | K5 elle eksi, 3 denemeden sonra 503 (§4.8) | 9 `PersonBalancePoolManualAdjustmentTests` (kuruş altı tutar: `Havuz_kipinde_kurus_alti_eksi_400_invalid_money_scale_hicbir_sey_yazilmaz`, fark 18l); ilişkisel: 16 `K5_uc_denemede_de_cakisirsa_503_balance_busy_hicbir_sey_yazmaz`, `Esazamanli_iki_negatif_ayarlamadan_yalnizca_biri_gecer(pool: true)` |
| R50–R54 | K5 reverse: üyelik, grup geri alması, çekim türü iptal, `reversal` hedefleri (§4.9, M8) | 10 `PersonBalancePoolReverseTests` (Not 4: `Insufficient` sonrası yeniden soru, fark 18k). Eski eksi hedefin grup geri alması (fark 10), ilişkisel: 16 `Ayni_hareket_esazamanli_yalnizca_bir_kez_reverse_edilebilir(pool: true)` |
| R55 | N01 hakemi (çekim türü iptal) | 10 `Ayni_iade_ikinci_kez_iptal_409_already_reversed` (InMemory, ardışık). İlişkisel: 16 `K5_iade_iptali_kayitta_rakip_iptal_commit_ederse_409_already_reversed`, `K5_iade_iptali_bakiye_okumasindan_once_rakip_commit_ederse_409_already_reversed` (18k pencere (b)), `K5_iade_iptali_paralel_sekiz_tek_iptal` |
| R56 | Kişi kilidinin jetonları (§4.13) | 4 `Kisi_kilidi_cekilmeyen_satirlarin_jetonunu_ilerletir`, 14 `Tc1_*`, 16 `Tc9_*` (`…kisi_kilidiyle_cakisir_D3` dahil) |
| R57 | READ COMMITTED, açık işlem yok (kapsam kilidi istisna) | 4 (`DrawAsync` çağıranın işlemini reddeder; işlem yalnız `ScopeLockResource` ile; InMemory testi yok, Görev 4 Not) |
| R58–R60 | `enable`/`disable`, dört koşul, kill switch (§7.2) | 11 `PersonBalanceEnableGateTests` (koşul 2'nin her kalemi ayrı: `Kosul2_D4_tutmaz`, `Kosul2_D5_tutmaz`, `Kosul2_D6_tutmaz`, `Kosul2_yetim_bacak_tutmaz`, `Kosul2_kirpma_farki_tutmaz`); 12 `PersonBalanceToggleCommandTests`, `…RelationalTests` (`Disable_apply_bayragi_bosaltir_veriye_dokunmaz`) |
| R61 | D3 uyarısı (§8.1) | 13 A `PersonBalanceD3AlertTests`, `PersonBalanceVerifyJobTests` (+1) |
| R62 | Dört sayaç (§8.6) | 13 B `PersonBalanceMetricsTests` (8: WPF altı, panel iki). `busy` ilişkisel: 14 `Uc_denemede_de_cakisan_apply_503_balance_busy_hicbir_sey_yazmaz` (`apply`), 16 `K5_uc_denemede_de_cakisirsa_503_balance_busy_hicbir_sey_yazmaz` (`panel/manual-adjustment`). `panel/reverse` ve WPF `reverse` etiketleri testsiz |
| R63 | Testcontainers 1, 2, 6–10, 14, 15 (§8.2) | 14: 1 `Tc1_*`, 2 `Tc2_*`, 10 `Tc10_*`, 503, 1205, 18n (30.10: `customer-not-synced` dalı, çekim sonrası ve ön oynatma Lookup'ında 1205). 15: 6 `Tc6_*` (üç konum + ters yön), 7 `Tc7_kimlik_birlesmesi_…`, `Tc7_apply_once_…` ve olağan kip `Tc7_olagan_kip_kopyanin_asil_kaydi_baska_kisideyse_kural3a_cekim_yok`, 8 `Tc8_*` (iki yön). 16: 9 `Tc9_*`, 14 (dört test × iki kip), 15 `Tc15_*` + kilit testleri, K5 503, N01 üçlüsü, iptalin üyelik okuması 1205 (iki yol) |
| R64–R69 | §8.3'ün S2 maddeleri: `PlanDraw`, preview havuzu, Örnek 1 ve 4'ün yazımı, panel, Shopper, geri alma biçimi | 3 `PersonBalancePlanDrawTests`; 5; 4 `Ornek1_satirlari_birebir`, 7 `Ornek1_apply_satirlari_birebir`, 9, 10 `Ornek4_baska_uyenin_iadesini_iptal`; 8, 10; 8. Geri alma biçimi: 7 `Ornek2_…`, `Ornek3_ayirmadan_sonra_geri_alma_her_bacagi_kendi_satirina_dondurur`, `KVKK_geri_alinca_…`; 10 `Havuz_kipinde_K5_cekim_grubunun_geri_almasi_grup_satirlariyla`, `Tek_bacakli_havuz_cekiminin_geri_almasi` |
| R70 | Satır kipi regresyonu (§8.3) | her görevin "PASS + regresyon" adımı ("Genel kurallar"daki dört filtre); 17 tam paket. Tek bilinçli fark 18f (yukarıda R19) |
| R71, R72 | Göç yok; deploy anında hiçbir lisans havuzda değil; geri alma S1'e güvenli (§7.1-5, §7.4) | "Bilerek S2 dışında"; 17 Adım 7b (iki boş çıktı) ve README bloğu; saha notu 1, 2, 10 |
| R73 | S2 sonrası saha kontrolü (§8.5) | saha notu 3 |
| Olumsuz | Kişi düzeyinde A11/scope yok (M10) | 6 `Havuz_kipinde_baska_uyedeki_capa_scope_204`, 7 `Mod_On_baska_uyedeki_capa_kapsam_kuralini_tetiklemez`, 16 `Tc15_baska_uyedeki_capa_kapsam_kuralini_tetiklemez` |
| Olumsuz | WPF reverse'e havuz sürüm kapısı yok (§7.5) | kod değişmez; 7 `Havuz_kipinde_eski_surumle_WPF_reverse_200_surum_kapisi_yok` (mod Off, eski User-Agent, 200 ve grup geri alındı) |

Spec §12'nin S2 satırındaki "Shopper `Reason` maskesi" S1'de yapılmıştı (fark 2). S2 maskeyi değiştirmez, yalnız havuz kipinin `S` geneli okumasını ekler (Görev 8). Bilinen test boşlukları (davranış eksiği değil): `busy`'nin `panel/reverse` ve WPF `reverse` etiketleri, fark 18m'nin savunma yolu (PK yarışında kazanan bulunamazsa 500).

**Yer tutucu taraması.** `00-iskelet.md`, `gorev-01…17.md` ve bu dosya Grep'le tarandı. Kalıplar:
- `\bTBD\b|\bTODO\b|\bFIXME\b|\bXXX\b`
- `benzer şekilde|aynı şekilde|Görev N'e benzer|uygun hata` (büyük/küçük harf duyarsız)
- satır sonunda `...` ya da `…`
- `<…>` / `<...>`
- `// aynen`, `// bugünkü kod`, `// …`
- küçük harfli `Görev N adım M` ve var olmayan adıma giden `Görev N Adım M`

Sonuç:
- İlk iki kalıp yalnız bu bölümün kendi metninde geçiyor.
- Satır sonu eksiltmesinin tek isabeti `gorev-12.md` Adım 4'ün "bugünkü" alıntısındaydı: `…(verify'ın üç maddesi :17-29 aynen)…`. Edit'in eski metni birebir değildi. Bu turda worktree `Tools/PersonBalanceCommand.cs:17-29`'dan (master `b6ee847c`) harfiyen dolduruldu; yeni blok zaten tamdı.
- `<…>` yalnız iskelet imza bloğunun iki ctor özet yorumunda geçiyor (`ILogger<…>`). Tam kurucular Görev 13'te (`gorev-13.md:651`, `:667`, `:809-811`). Yorumdur, kod değildir.
- Küçük harfli "Görev 4 adım 1/2/6/10" atıfları (14, 15, 16) düzelticilerce "Görev 4 Adım 5 — …" biçimine çevrildi; yalnız bu dosyanın eski metninde kalmıştı. Kalan `Görev N Adım M` atıflarının hepsi var olan adımlara gidiyor: 1/7, 4/5, 7/3, 7/4, 11/5, 13/10, 17/8.
- `<id>`, `<Wh>`, `<deploy anı>`, `<pilot-id>`, `<test-id>` yalnız işletim komutlarında geçiyor; operatör dolduracak.
- Sahip kararları işlendi (29.10): karar 1 (b) Görev 5'in Not'unda (`gorev-05.md:16`), testinde (`:147-163`, `Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir`) ve `PoolPreviewAsync`'in ret eşlemesinde (`:645-649`). Apply tarafının testi (`gorev-07.md:315-317`) ve `ResolveDrawSetAsync`'in belge yorumu (`gorev-02.md:421-423`) preview'ın 200'üne atıf veriyor; başka parçada preview için 409 `no-balance` beklentisi yok (Grep). Görev 11'de karar 2 (a) planın varsayılanıyla aynı; "Sahip sorusu 2" ifadeleri karar diline çevrildi (`gorev-11.md:22`, `:496`, `:607`), davranış ve test aynı. Görev 13'ün eski "mimar reddederse" yedek bloğu kaldırıldı (fark 18h karara bağlandı).

**İmza tutarlılığı.** İskeletin "Görevler arası imzalar" bloğu tanımlayan görevle Grep'le karşılaştırıldı. Ad, tür ve parametre sırası aynı:
- Görev 1: `IsPoolModeAsync` (`gorev-01.md:738`), `OwnedLicenseModeAsync → (SalesSyncMode Mode, bool Pool)?` (`:850`; bugünkü `SalesSyncMode?` `:834`), `WarnIfPoolWithoutMinimumAsync` (`:702`), iki fabrika (`:31`, `:56`), kit (`PoolUserAgent` `:401`).
- Görev 2–3: `ResolveDrawSetAsync` (`gorev-02.md:434`), `PersonDrawSetAsync` (`:469`), `PoolBalanceAsync` (`:482`), `PlanDraw` (`gorev-03.md:268`).
- Görev 4: `DrawRequest` (`gorev-04.md:553`; 14 alan aynı sırada), `DrawStatus` (9 değer), `DrawResult` (`:573`), `DrawAsync` (`:606`), `IsDuplicateTransactionKey` (`:511`).
- Görev 5–7:
  - `PoolPreviewAsync` (`gorev-05.md:638`), ctor (`:504`).
  - `ApplyRequest` (`gorev-06.md:369`; `MemberIds` sonda, varsayılanlı), `LatestScopeDeductionAsync(…, personPool, ct)` (`:453`), `LookupAsync(…, key, pool, ct)` (`:531`). Görev 7'nin dört çağrısı `pool`'u geçiriyor (`gorev-07.md:682`, `:788`, `:846`, `:865`).
  - `ApplyPoolAsync` (`gorev-07.md:757`), `ScopeHasDeduction` (`:890`).
- Görev 8–10: iki `PoolDetailsAsync` (`gorev-08.md:454`, `:531`), `BalanceBusy` (`gorev-09.md:459`; Görev 13 gövdeye sayaç ekler, imza aynı), `DrawManualAdjustmentAsync` (`gorev-09.md:402`), `ReverseDrawGroupAsync(…, pool, ct)` (`gorev-10.md:408`), `ReverseInPoolAsync` (`:478`).
- Görev 11: `Result.Failing` hesaplanan özelliktir, iskeletle aynı ifade (`gorev-11.md:649`); `EvaluateAsync` (`:654`).
- Görev 12: `ToggleArgs` (`gorev-12.md:726`), `RunEnableAsync` (`:812`), `RunDisableAsync` (`:872`), `TryParseAnyArgs` (`:976`; iskelete eklendi), `TryParseToggleArgs` (`:989`).
- Görev 13: `LicenseRow(…, PoolSince = null)` (`gorev-13.md:239`), WPF ctor'un son hâli (`:651`, `:667`), panel ctor (`:809-811`). `endpoint` kümeleri fark 18h ile aynı (`gorev-13.md:15`).

Düzelticilerin hiçbiri "İMZA DEĞİŞTİ" bildirmedi. İmza bloğundaki değişiklikler yalnız iskelet düzelticisinden geldi: `TryParseAnyArgs` eklendi, `Failing` hesaplanan özellik olarak yazıldı, `endpoint` yorumu eklendi. İlk turun "iskelete işlenmemiş ekler" listesi iskelete işlendi:
- (a) → imza bloğu
- (b) → 18h
- (c) → 18e
- (d) → 18f
- (e) → 18n
- (f) → 18k
- (g) → 18m
- Görev 4 → 9 kuruş altı devri → 18l. `DrawAsync` kuruş altı `Requested`'ta `ArgumentOutOfRangeException` atar; Görev 9 havuz kolunda bunu 400 `invalid-money-scale` ile önler.

Satır numaraları bu turdaki eklemelerden sonraki parçalara göredir.

**Bu turda parçalar arası düzeltilen uyumsuzluklar** (düzelticilerin dosya sınırı yüzünden açık kalanlar):
- `00-iskelet.md` 18f: "apply tarafı … testi yok" cümlesi Görev 6'nın `Satir_kipinde_memberIds_okunmaz_bozugu_400` testine bağlandı. "Görev özeti" sayıları güncellendi: Görev 16 için 21 test (sonra 23: Görev 10 Not 6'nın kapanmasıyla iki kollu üyelik okuması 1205 testi eklendi), Görev 14 için sekiz yarış testi, Görev 11 için 24 kapı testi.
- `gorev-05.md` fark 18f: "apply tarafında hiçbir görevde test edilmez" cümlesi aynı teste bağlandı.
- `gorev-04.md`:
  - "Sahip sorusu (Görev 9'a bağımlı)" notu "Karar (fark 18l): Görev 9 kapattı" oldu.
  - "Görev 15'e eklenecek senaryo" cümlesi Görev 15'in `Kural3b_…` testine bağlandı. Beklenti de o testle aynı yapıldı: hareket sayısı iade kadar artar.
- `gorev-07.md` Not 6: "Görev 14'ün kapsamına aday" ifadesi `Ilk_denemede_havuzu_tuketen_ayni_anahtarli_rakip_oynatilir` olarak düzeltildi.
- `gorev-10.md` Not 4: "Görev 16'nın testine kalır" cümlesine üç N01 testinin adı eklendi.
- `gorev-13.md` (Not, sınıf belgesi, Adım 6 notu): "`panel/` önekli ölçümün testi yok" ifadesi düzeltildi. Görev 16 `panel/manual-adjustment`'ı `MeterListener`'la ölçüyor; testsiz kalanlar yalnız `panel/reverse` ve WPF `reverse`.
- `gorev-12.md` Adım 4: eksiltme dolduruldu (yukarıda).
- `gorev-17.md` Adım 5 README bloğu: kill switch paragrafına elle S1'e dönüşün bayrağı silmediği uyarısı eklendi (saha notu 10 ile aynı).

**Sıra ve yeşil kalma.**
- Her görev derlenir ve var olan testleri değiştirmeden geçer. İki bilinçli istisna var:
  - Görev 13 `PersonBalanceVerifyJobTests`'e bir test ekler (var olanlar aynen).
  - Görev 16 `CustomerBalanceConcurrencyTests`'in dört `[Fact]`'ini `[Theory] (pool: false/true)` yapar. Spec §8.2-14 bunu ister; `pool: false` dalı bugünkü testin aynısıdır ve iddialar değişmez.
- TDD istisnaları bilinçlidir:
  - Görev 7'nin §7.5 testi kurulum elle tohumlandığı için Adım 2'de de yeşildir.
  - Görev 14, 15 ve 16 üretim koduna dokunmaz. Yalnız kanıt testi ekler (16 ayrıca var olan dört testi iki kipe çevirir); ilk koşuda PASS beklenir. *(30.10.2026: son inceleme Görev 14'ün sınıfına denetleyici düzeltmesiyle birlikte üç test ekledi, sınıf 11 test; fark 18n.)*
- Görev 6 → 7 arasında, yalnız bayrak açıkken görülen bir ara durum var (Görev 6 Not 1). Aynı PR'da gider ve sahaya çıkmaz.
- Bağımlılıklar sırayla karşılanıyor:
  - Görev 13'ün sayaçları 9 ve 10'un yardımcılarına dayanır.
  - Görev 14'ün ve Görev 16'nın 503 testleri Görev 13'ün `busy` sayacını okur.
  - Görev 12, Görev 11'in kapısını kullanır.
  - Görev 14'ün TC10 ve 18n testleri Görev 7'nin geri çağrısına ve `LookupAsync`'in "tekrar oynatıldı" günlük satırına dayanır.
  - Görev 15'in kural 3b testi Görev 4'ün `nowPositive` dalına dayanır.
  - Görev 16'nın üyelik okuması 1205 testi Görev 10 Adım 4 ve 5'in `MembersAsync` sarmalına dayanır.
  - Görev 4 Adım 3, Görev 1 Adım 7'nin yazdığı `PersonBalance.cs` sınıf belgesini alıntılar. Görev 1'in o adımı değişirse Görev 4'ün alıntısı da aynı anda güncellenir.
- Göç yok. Mekanik kanıtı Görev 17 Adım 7b'dir. Hiçbir görev `PersonBalancePoolSince`'i prod'da yazmaz; yazan komut getirilir, koşturulmaz.
- Commit sayısı 18 (Görev 13 iki commit).

**Kalan açık sorunlar.** Hiçbiri merge'i bloklamaz ve sahip kararı gerektirmez. 1, 2, 4 ve 7 kapandı (aşağıda nedenleriyle).
1. **Kesik düzeltici raporları — kapandı.** Görev 17 düzelticisinin "99-kapanis'e aktarılacak içerik" metni raporda kesik gelmişti; Görev 17'den silinen "Sürüm / saha notları" bölümü bir yerde saklanmamıştı. Görev 17'nin kalan Not'ları ve README bloğu bu saha notlarıyla karşılaştırılıp eksik iki madde eklenmişti (1'de Adım 7b, 10'da "grubun herhangi bir satırından"). Ardından düzelticinin tam çıktısıyla karşılaştırıldı: silinen bölümde 99-kapanis'te olmayan tek cümle "S2 ile Wh'nin sırası serbest; enable ikisini de ister." idi ve saha notu 5'e eklendi (Grep ile doğrulandı). Görev 07, 09, 13, 14, 15 ve 16'nın kesik raporlarının aktarılacak içeriği dosyalardan çıkarıldı (yukarıdaki düzeltmeler).
2. **Düşen ajan şüphesi — kapandı.** `gorev-02.md`, `gorev-03.md` ve `gorev-08.md` için düzeltici raporu yoktu. Nedeni: bu üç parçanın tutarlılık denetçileri 0 bulgu döndürdü, bu yüzden düzelticileri hiç çalışmadı; düşen ajan yok. (Son turda `gorev-02.md`'ye karar 1 için bir belge yorumu eklendi, `:421-423`; davranış aynı.)
3. **Doğrudan bulgunun geri dönüş tarifi düzeltilerek uygulandı.** Önerilen "S2'ye yeniden çıkışta bayraklı lisansta kuru `enable`" işe yaramaz. Kip açıkken `enable` kapıyı değerlendirmez, yalnız "zaten açık" basar (Görev 12 `RunEnableAsync`, test `Zaten_acikken_enable_0_yazmaz`). Saha notu 10 ve README bu yüzden şunu der: önce `disable` (dönüşten önce, S2 ayaktayken), sonra yeniden çıkışta normal `enable` yolu. Kip açıkken kapıyı değerlendiren bir komut (`person-balance status`) bilerek S2 dışında.
4. **İptalin üyelik okumasında 1205 → 500 — kapandı** (Görev 10 Not 6):
   - Havuz kipinde iptalde `MembersAsync` okuması (`ReverseInPoolAsync` başında, `ReverseDrawGroupAsync`'in havuz dalında) servislerin korunan denemesinin dışındadır ve `CustomerBalances`'ı da okur; RCSI olmadığından yazarların X kilidinde bekleyip 1205 kurbanı olabilir.
   - Görev 10 Adım 4 ve 5 iki çağrıyı `try { … } catch (Exception ex) when (PersonBalance.IsDeadlock(ex)) { return BalanceBusy(licenseId, "reverse"); }` ile sarar → 503 `balance-busy`, hiçbir şey yazılmaz (okuma her yazımdan önce). `IsDeadlock` `public static` (master `PersonBalance.cs:388`), `BalanceBusy` Görev 9'un yardımcısı; ikisi worktree ve parçalara karşı doğrulandı.
   - Kanıt Görev 16 Not 10: `PersonBalancePanelDrawRaceTests.Havuz_kipinde_iptalin_uyelik_okumasi_kilitlenme_kurbani_olursa_503_balance_busy` (`[Theory]`, iade iptali ve çekim grubu yolu; `ReadQueryHook` ilk `FROM [CustomerBalances]` okumasında bir kez `SqlException(1205)` fırlatır). Görev 16 Adım 4: 10 test, Adım 7: 23 test.
   - S1'den gelen grup okuması (`ReverseDrawGroupAsync`'in başı, `CustomerBalanceTransactions`) iki kipte aynen korumasız kalır: bayrak boşken yol S1'in birebir aynısı olmalı.
5. **Testsiz yollar (davranış tanımlı, otomatik kanıt yok):**
   - `busy`'nin `panel/reverse` ve WPF `reverse` etiketleri.
   - Fark 18m: PK yarışında kazanan bulunamazsa `InvalidOperationException` (500; satır yolunun `throw;`'u da 500). 30.10 ekiyle aynı dalda foreign → 404 ve Lookup 1205 → 503 de testsiz; ikisi `ReplayWrittenKeyAsync` yardımcısından geçer ve o yardımcının 1205 kolu `NoBalance` dalında testli.
   - Panel iptalinin `Insufficient` sonrası "geri alındı mı" okumasının 1205 kolu (`ReadDeadlocked`, `geri alındı mı okuması`); üyelik okuması kolu testli.
   - Fark 18f'nin satır kipi farkı ise artık iki tarafta da testli (R19).
6. **`PersonBalanceTestKit.PoolUserAgent`** (Görev 1) hiçbir görevde kullanılmıyor. Public sabit olduğu için uyarı üretmez; istenirse Görev 1'den çıkarılabilir.
7. **Spec §4.0-1 notu — kapandı.** Spec, planla aynı commit'te güncellendi: §4.0-1'in kesin kolu "apply'da 409 `no-balance`, preview'da 200 `Balance = 0`, `PersonPool = true` (K7)", §8.3'ün preview maddesi aynı biçimde, §10'a sahip kararı K7 (2026-10-29) eklendi.
