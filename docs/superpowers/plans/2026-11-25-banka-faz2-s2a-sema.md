# Banka Faz 2 S2a (şema) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Banka havalesi onayının bütün şemasını tek genişletme göçüyle prod'a koymak: `Payment`, `PaymentMatch`, `License` sütunları, beş yeni tablo, geniş IBAN indeksi, `Payment.WpfCustomerId` geri doldurması, bütün enum'lar, iki banka ayarı ve `bank-questions` CLI'ı. Hiçbir akışın davranışı değişmez.

**Architecture:** Genişlet/daralt. Göç yalnız genişletir: eski imajın INSERT'i yeni NOT NULL sütunlarda kalıcı DB varsayılanına düşer, dar IBAN tekili yerinde kalır. Yeni sütunlara göçün geri doldurması, müşteri birleştiricisi (onun eşi olan bağ kesme yollarıyla) ve CLI dışında kimse yazmaz. Beş tablo boş doğar. Birleştirici çift IBAN'ı şimdiden tekilleştirir ki dar indeks S3b'de düştüğünde müşteri senkronu düşmesin. Göç birleştirilmiş kopyada kalmış IBAN bağına da onu öğreten eşleşme kararına da dokunmaz (Karar 5): gölge önerisi de değişmez.

**Tech Stack:** ASP.NET Core 10, EF Core 10.0.10 (SQL Server prod, Express; InMemory test), xunit + FluentAssertions 7, Testcontainers (SQL Server 2022 Developer ve Express).

**Spec:** `docs/superpowers/specs/2026-10-10-banka-havale-onay-design.md`, dal `docs/banka-faz2-spec` (§2, §3, §4.6.3, §5.2, §9, §14, §15). Bu dalda belgenin eski hâli durur: DEĞİŞTİRME (D0 ile çakışır; §15 gereği D0 önce birleşir). Tasarımdan ayrılan yerler "Kararlar"da.

## Global Constraints

- **Yer:** `<WT>` = `bank-s2a` çalışma ağacının mutlak yolu (`git worktree list`), dal `feat/banka-faz2-s2a`, kod = origin/master `a9729534`. Her Bash komutu `cd <WT> &&` ile başlar. Başka çalışma ağacına ve ana klasöre yazma. `git stash`, push, PR, `amend` yok. `dotnet` süreçlerini öldürme.
- **Depo PUBLIC:** sır, kişi adı, gerçek Id yazılmaz; testte her değer üretilir (`Guid.NewGuid()`). Literal kimlik, IBAN, telefon yok. 64 hex özet: `$"{Guid.NewGuid():N}{Guid.NewGuid():N}"`.
- **Kaynak düzenleme:** yalnız Edit / Write (PowerShell 5.1 UTF-8'i bozar). Göç dosyalarını `dotnet ef` üretir. `OrderDeck.LicenseServer` uyarıyı hata sayar.
- **Davranış değişmez:** yeni tablo ve sütunu okuyan ya da yazan tek kod göç, birleştirici, kanıtsız bağın kesilmesi ve shopper silmesi (göçün yazdığı kişi bağı silinebilsin, Karar 4), CLI. Dekont gönderimi, eşleştirici, bağdaştırıcı, panel, `payments/sync` ve yayıncı silmesi DEĞİŞMEZ.
- **Genişlet/daralt:** `Up`'ta `DropIndex`, `DropColumn`, `AlterColumn`, `DELETE`, `ONLINE` yok. NOT NULL eklenen sütunların hepsi DB DEFAULT'lu: `Payments.Source` `N'Receipt'`, `PaymentMatches.Decision` `N'None'`, `PaymentMatches.DecisionRevision` 0, `PaymentMatches.SupportedByIbanMemory` 0, `Licenses.BankQuestionsEnabled` 0, `Licenses.BankAutoApproveEnabled` 0. Öbürleri NULL.
- **IBAN indeksleri:** dar `IX_CustomerIbanMemories_LicenseId_IbanHash` (tekil) KALIR; geniş `IX_CustomerIbanMemories_LicenseId_IbanHash_WpfCustomerId` (tekil) eklenir.
- **Göç** `BankDecisionsS2a`, tek göç, Görev 5'te üretilir. Görev 2-4 arasında `Migrate()` koşan (SQL Server) testler `PendingModelChangesWarning` ile düşer, beklenir: o görevler InMemory ve tasarım modeliyle sınanır.
- **Sütun uzunlukları:** `Source`, `ApprovalSource`, `DecidedVia`, `ActorKind`, `Surface`, `Kind`, adım `Decision` 16; eşleşme `Decision`, `DismissReason`, adım `State` ve `Reason` 24; `Action`, `ReasonCode` 32; `CustomerKey`, `IbanHash` 64; `DismissNote` 300; `AskReasons` 512; para `decimal(18,2)`. `CustomerKey` harmanı `Latin1_General_100_BIN2`.
- **Enum'lar** string saklanır, değer listesine CHECK yok. Yeni bağlar FK'sız Guid; FK yalnız tahsis→Payment, adım→Payment, adım siparişi→adım, olay→License, IBAN durumu→License (hepsi Cascade, tablo başına tek yol, 1785 yok).
- **Koşu kısaltmaları.** `T(x)`: `cd <WT> && ORDERDECK_TEST_SHARD=inmemory:0/1 DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo --filter "FullyQualifiedName~x"` (birden çok ad `|FullyQualifiedName~y`; ortam değişkeni SQL Server sınıflarını eler). `B`: `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo`.
- **SQL Server testi** (`[Collection(SqlServerCollection.Name)]` + `[Trait("Category","Testcontainers")]`): Docker kapalı, yerelde yalnız `B` ile derlenir; CI'nın ilişkisel parçaları koşar. Kırmızı hâli yerelde görülmez; görülmüş gibi yazma.
- **Commit:** görev başına bir commit, Türkçe emir kipi, gövde nedeni söyler, son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`. Yalnız görevin dosyaları `git add` edilir.
- **Yayın:** master merge = prod deploy. Merge ve CLI yayın penceresi (Paz/Pzt/Çar/Per 20:00–01:00 TR) dışında; merge sahibindedir.

## Review Focus

1. **Geri alınan imaj yeni şemada çalışır.** Deploy geri alınınca imaj döner, göç kalır: varsayılansız NOT NULL her dekontu 515 ile düşürür, düşmüş dar IBAN tekili iki müşteriye aynı IBAN'ı öğretir. Görev 5: `BankDecisionsS2aMigrationTests.Goc_genisletir_geri_doldurur_eski_imaj_yazmayi_surdurur`, `BankDecisionsS2aMigrationScriptTests.Yalniz_genisletir`.
2. **Geri doldurma yanlış kişiye yazmaz.** En yeni müşterili bağ, lisans süzgeci, birleşme zinciri; `UpdatedAt` ilerlerse bütün dekont geçmişi masaüstüne yeniden iner. Görev 5: aynı senaryonun geri doldurma satırları ve gerçek çözücülerle eşlik iddiası (`ShouldMatchResolversAsync`).
3. **Birleştirme müşteri senkronunu düşürmez.** Çift IBAN 2601, aynı anda onaylanan dekont jeton çakışması üretir; yakalanmayan hata 500 ve sonsuz yeniden gönderimdir. Görev 6: `Ayni_ozetli_uc_bag_tek_SaveChangeste_tekillesir`, `Senkron_sirasinda_onaylanan_dekont_senkronu_dusurmez`.
4. **Dolan kişi bağı silmeden ve kanıt kaybından sonra yaşamaz, geri alma penceresinde de.** Eski imaj sütunu bilmez; orada bayatlayan DOLU bağı S2b'nin yeniden türetmesi kapatır (Karar 3). Görev 7: `Odemenin_musteri_bagi_silmede_bosalir_tekrar_silme_saymaz`, `Devralmada_kanitsiz_baglantinin_dekontu_musterisiz_kalir`; boşaltma yalnız kanıtı düşen bağın dekontunu kapsar, yaşayan bağınkini değil: `Kanitsiz_tasinan_baglanti_yalniz_kopyadan_gelen_dekontu_bosaltir`; Görev 5: senaryonun "Yeniden türetme" satırı, `Banka_onay_satirlari_lisansla_gider`.
5. **Bayrak erken açılmaz, açılış anı sıfırlanmaz.** `BankQuestionsSince` bir kez yazılır; erken yazım S3a deploy'unda eski havaleleri sordurur. Görev 9: `Karar_katmani_hazir_degilken_apply_reddeder`, `Kapat_ac_BankQuestionsSince_korur`.

## Kararlar

1. **IBAN indeksi S2a'da genişler; dar indeks S2a'da da S2b'de de düşmez, S3b'de düşer** (ikinci çifti ilk yazan PR). `LearnIbanAsync` oku-sonra-ekle yapar (`PaymentMatchReconciler` 474-494), iki müşteriye eşzamanlı öğretmeyi bugün yalnız dar indeks durdurur (`RetryOnceAsync` 719-747); `PaymentMatcher` 164-167 sırasız `FirstOrDefault` okur. Bir kısıt ancak dönülebilecek bütün imajlar gevşemiş hâle hazırken gevşer: S2a'da düşse eski imaj, S2b'de düşse S2a imajı hazırlıksızdır; S3b'den dönülen S3a (S2b okuyucusuyla) hazırdır. Dar indeks dururken çift oluşamaz: göçte tekilleştirme (DELETE, kapı) yoktur, geniş tekil kurulurken düşemez.
2. **`Source` NOT NULL DEFAULT.** Tasarım §14 nullable der; amaç eski imajın INSERT'i, DEFAULT onu karşılar ve "boş Source" hâli hiç doğmaz. Aynı kural `Decision`, `DecisionRevision`, `SupportedByIbanMemory` ve iki bayrakta (emsal `SalesSyncMode`). Modelde `HasDefaultValue(...).ValueGeneratedNever()`: göç `'Receipt'` / `'None'` üretir (varsayılansız iskele `''` üretir: EF onu 0. değer diye okur ama SQL süzgeci satırı bulamaz), yeni kod değeri her INSERT'te kendisi yazar.
3. **Geri doldurma boşu doldurmaz, bağdan türetir; S2b göçü onu yeniden koşar.** Sütun S2a boyunca türetilmiş değerdir: yazan göç, birleştirici ve bağ kesmedir, gönderim yazmaz. Aradaki dekont boş kalır. Geri alma penceresinde ise DOLU bağ bayatlar, çünkü eski imaj sütunu bilmez: kanıtı düşen bağın dekontu geçici kopyada kalır (ileri dönünce `CustomerIdResolver` onu yanlış müşteriye çözer, S3'te para iki kez onaylanabilir), silinen shopper'ınki eski müşterisinde kalır. Bağlantı boş ya da silinmiş olduğu için ikisini de kendiliğinden onaran yol yoktur (silme ancak elle yeniden uygulanır, Görev 9). Sabit bu yüzden `OUTER APPLY` ile NULL'a da yazar ve değişmeyen satıra dokunmaz: ilk koşuda sonuç aynıdır (hepsi boş), ikinci koşu 0 satırdır. S2b göçü S2b kodundan önce koştuğu için gönderimin yazdığını ezmez; metni kopyalar (göç uygulama koduna bağlanmaz).
4. **Kişi bağını kesen iki yol S2a'da:** göç prod'a anında "dekont → müşteri" bağı yazar, silme ise S4'e kadar onu bilmezdi; kod aynı bağı denetim satırında bile siliyor (`ShopperPurgeService` 3c). Kanıtı düşen Shopper bağının dekontu da birleştiriciyle gerçek müşteriye geçerdi. İkisi kalıcıdır: S2b'den sonra gönderim de yazar, sütun bir daha türetilmez.
5. **Göç kopyada kalmış IBAN bağını taşımaz** (tasarım §2.5, §15 Testler ve §18.7 satır 3 ve 11 ile aynı). Birleştirmeyle yarışan öğrenme bağı ve onu öğreten `PaymentMatch` kararını aynı SaveChanges'te aynı kopyaya (K) yazar, birleştirici de ikisini birlikte taşır. Göç yalnız bağı asıl kayda (P) taşısa çift bölünürdü; gerçek hizmetlerle SQL Server'da ölçülen dört bozulma: (1) IBAN talebiyle silme kararın müşterisini ve `IbanConfirmedAt`'ini boşaltmaz (`BankSubjectRequestService.EraseAsync`); (2) K-6 K'daki kararı kalan doğrulama saymaz, P'ye elle eşlemenin kaldırılması bağı siler (`RemainingConfirmationsAsync`); bağlantısı K'da kalmış kişide (3) onay doğru öneriyi `Contradicted` sayar ve bağı tazelemez (`ResolveWpfCustomerAsync`), (4) shopper silmesi bağı bulmaz (`ShopperPurgeService` 318-319). Kararı da taşımak (1) ve (2)'yi kapatır, (3) kalır, (4) kötüleşir (silme kararı da bulmaz), K'ya çözülen dekont da kendi kararlı havalesine artık bağlanmaz. Göç bir kez koşar, sonradan düzeltilemez. Satırlar kopyada kalır (bugünkü hâli, 12 aylık saklaması işler), sayısı PR gövdesindeki sorguda; ikisini birlikte S2b taşır (aşağıda).
6. **`BankDecisionEvent.Action` enum değil dize; S2a ad tanımlamaz.** Tasarım kapalı liste vermiyor, eski imaj bilmediği dizeyi düşmeden okur; her PR kendi sabitini ekler. Tasarımın istediği ölçüm ve bağ sütunları açık yazıldı (`CreatedAt`, `RelatedPaymentId`, `WouldAutoApprove`, `OrderSetReliable`, `WpfCustomerId`, `AskReasons`, `DetailsJson`). `RequestId` indeksi tekil DEĞİL: hakem satır jetonudur, bir istek birden çok olay yazabilir.
7. **Adımda `CreatedAt`, `StateChangedAt`:** §14'ün "bir günü geçen adım" uyarısı başka türlü hesaplanamaz (`NextAttemptAt` saatte bir ilerler). **`BankIbanState.UpdatedAt` jeton:** "Evet" ile "Hayır" aynı satırda yarışır.
8. **CLI'da yalnız `enable` ve `disable`** (tasarımda `status` yok; kuru koşu aynı bilgiyi basar). `enable`'ın 2. koşulu `DecisionLayerReady` (S2a'da `false`): tek kod koşulu satış senkronu kipiydi ve bugün tutabilir; erken `--apply` geri alınamayan `BankQuestionsSince` yazardı. Tasarıma (§2.7, §14) ek koşuldur, PR gövdesi sahibe bildirir. Pilot bir kod PR'ı değildir; anahtarı S4 açar (aşağıda).
9. **`StaleFeedAfterMinutes`:** TimeSpan bağlama `=2`'yi 2 gün okur. **Birleştirme:** eski `CreatedAt` kalır (eşitse küçük Id), ikisinden biri `BankConfirm` ise kaynak kalana geçer (tek kayıttaki tazelemenin ve §2.5 upsert'inin eşi: öğreten havale, `CreatedAt` ve maske değişmez; S2a incelemesinin `SharedConfirm` ölçümü), son doğrulama `LearnIbanAsync` kuralıyla yalnız ileri gider (düşeninki kalanın başlangıcından yeniyse alınır), tekilleştirme silmesine imha kaydı yazılmaz (bağ kalan satırda yaşar). Eşlik iki satırın bugünkü hâline göredir: satırlardan biri kaldırmayla silinip yeniden öğrenildiyse ayrışma birleşmeden önce başlar, birleşme onu düzeltemez. Son doğrulama iki satırda kesindir; aynı özetli üç ya da daha çok satır tek SaveChanges'te birleşirken ara adımda kalanın son doğrulaması boşsa (P1 öncesi) sonuç birleştirme sırasına bağlıdır ve yalnız erkene düşer (kural bilerek aynen, aşağıda S3b).

**Sonraki PR'lara bırakılan (yazma):**
- **S2b:** `BackfillPaymentCustomers` metnini kendi göçünde yeniden koşar (Karar 3; `ShopperId IS NOT NULL` koşulu metnin parçasıdır, shopper'sız satırın dolu müşterisini korur); `SharedIbanSchema` kullanır. Kopyada kalmış IBAN bağını ve onu gösteren eşleşme satırlarını (`ActualWpfCustomerId`, `ProposedWpfCustomerId`) kişi düzeyi okuyucuyla aynı PR'da BİRLİKTE asıl kayda taşır; silinmiş asıl kayda ve kendisi silinmiş kopyadan (geçici satır) taşımaz, çünkü silmeden sonra yazılan bağı silme bulamaz. Bağlantısı kopyada kalmış kişiyi de kapsar: `ResolveWpfCustomerAsync` ve `ShopperPurgeService` bağlantının Id'sini asıl kayda çevirmeden okur, taşınanı bulamaz (Karar 5). Geçici (`CreatedByShopper`) kopyada kalmış bağlantı telefon kanıtı olmadan gerçek müşteriye çözülmez (tasarım §2.5); kural göçün türetmesine de uzanır: bu sabit birleşme zincirini bugünkü okuyucular gibi kanıt aramadan izler, SQL'de kalan S2b kopyası birleştirilmiş geçici kopyadan zinciri izlemez (dekont müşterisiz kalır).
- **S3a / S3c** (`ExpectationTtlDays` ve `StaleFeedAfterMinutes`'i ilk okuyan): aynı iki varsayılan üç yerde durur (`BankOptions` 14/120, `appsettings.json`, compose `:-14`/`:-120`) ve hiçbir test onları birbirine bağlamaz; prod değeri compose'tan, `ApiFactory` testlerininki `appsettings.json`'dan gelir. Okuyanla aynı PR bir test ekler: etkili değeri fabrikadan okur (`BankJobsDiTests` deseni), compose'taki `:-N`'yi ayrıştırıp sınıf varsayılanıyla karşılaştırır. Yoksa biri değişip öbürü unutulunca testler prod'da olmayan bir değerle yeşil kalır (`GraphApiVersionSingleSourceTests` dersi).
- **S3b:** dar indeksi düşürür, `customers/sync`'e IBAN indeksinin 2601'inde tek yeniden deneme ekler. Tekilleştirme kuralı (Karar 9) onunla canlanır. Aynı özetli üç ya da daha çok satırda son doğrulama birleştirme sırasına bağlı olabilir, hata yönü erken silme (KVKK'nın güvenli yönü); kural bilerek aynen. Ayrışma yalnız son doğrulaması boş (P1 öncesi, öğreten havalesi silinmiş) satırda görülür: hepsi doluyken ikili kural en yenisini verir, tek kayıtla aynı. S3b dar indeksi düşürmeden önce `CustomerIbanMemories WHERE LastConfirmedAt IS NULL` sayısını ölçer. Geri alma kuralı tasarım §11.2'de: "Evet"in öğrettiğini ya da tazelediğini yalnız satırdan ayırır (`SourceBankTransactionId`); olaydaki `LearnedFrom` kararın öncesindeki kaynaktır, yalnız denetim izidir (tekilleştirmede kopyada "Evet"in öğrettiği satır düşer, asıl kayıttaki eski satır yükselir: olay "öğretti", satır "tazeledi" der); geri alma kaynağı indirmez, `Disputed` yeni "Evet"e dek otomatik onayı durdurur ve yeni "Evet" kaynağı yine `BankConfirm` yapar.
- **S3d:** `PaymentShipmentStep.CustomerKey` BIN2, `Orders.CustomerId` varsayılan harmandadır: SQL'de doğrudan karşılaştırma ya da birleştirme 468 alır; değer parametreyle geçer ya da `COLLATE` yazılır.
- **S4:** purge'de `WpfCustomerId` ile arama; purge'ün IBAN hafızasını birleştirilmiş kopyalarla (`WithMergedCopiesAsync`) araması; `BankIbanState` ve olay saklaması; yayıncı silmesinde (`AdminCustomersController.Purge`) ödemesiz tahsisin açıkça silinmesi ve imha kaydı (tasarım §2.2; satırı ilk yazan S5'tir, S4 ondan önce prod'dadır). Pilottan önceki son sunucu PR'ı olarak `DecisionLayerReady = true` yapar, `S2a_surumunde_karar_katmani_kapali` testini çevirir, README'deki "S2a'da `enable --apply` hep reddeder" cümlesini kaldırır (S3c ya da S3d ondan sonra birleşirse son birleşen yapar). Sabiti çevirirken `BankQuestionsCommand.RunAsync`'in onu geçirdiği satıra da bakar: `RunAsync(string[])`'i hiçbir test çağırmaz (testler `RunEnableAsync`'e parametreyi doğrudan verir), Review Focus 5'in korumasını orada kod okuması taşır; istenirse bağlantı dizesi verilmiş gövde internal bir yönteme çıkarılır ve ilişkisel testte uçtan uca koşulur. Unutulursa pilot günü `enable --apply` reddeder ve o gün prod deploy gerekir.
- **S5:** ödemesiz tahsisin silme yarışı.

## Dosya haritası

| Dosya (`LS/` = `OrderDeck.LicenseServer/`, `T/` = `OrderDeck.LicenseServer.Tests/`) | Sorumluluk | Görev |
|---|---|---|
| `LS/Domain/PaymentSource.cs`, `LS/Domain/Bank/PaymentMatchDecision.cs`, `BankAskReason.cs`, `BankDecisionCodes.cs` (yeni) | enum'lar, `BankAskReasons` | 1 |
| `LS/Domain/Payment.cs`, `License.cs`, `Bank/PaymentMatch.cs`, `Bank/CustomerIbanMemory.cs` | sütunlar, `BankConfirm` | 1, 2 |
| `LS/Domain/Bank/BankDecisionEvent.cs`, `BankIbanState.cs` (yeni) | denetim satırı, IBAN durumu | 3 |
| `LS/Domain/PaymentExpectationAllocation.cs`, `PaymentShipmentStep.cs` (yeni) | tahsis, kargo adımı ve siparişi | 4 |
| `LS/Data/LicenseDbContext.cs`, `LS/Data/Migrations/*_BankDecisionsS2a*` | eşleme, göç, snapshot | 2-5 |
| `LS/Services/CustomerSync/CustomerIdentityMerger.cs`, `CustomerIdentityMergeJob.cs`, `LS/Tools/MergeCustomerIdentities.cs` | tekilleştirme, dekont müşterisi, rapor | 6, 7 |
| `LS/Controllers/Licenses/LicensesWpfCustomersSyncController.cs`, `LS/Services/Shoppers/ShopperPurgeService.cs` | bağ kesme | 7 |
| `LS/Services/Bank/BankOptions.cs`, `LS/appsettings.json`, `deploy/docker-compose.yml`, `deploy/README.md` | ayarlar, işletim | 8, 9 |
| `LS/Tools/BankQuestionsCommand.cs` (yeni), `LS/Program.cs` | CLI | 9 |
| `T/Domain/BankDecisionCodesTests.cs`, `T/Data/BankModelTests.cs`, `T/Data/BankDecisionsS2aMigrationTests.cs`, `T/TestHelpers/SharedIbanSchema.cs` ve görevlerdekiler | testler | 1-9 |

### Task 1: Kod kümeleri ve sorma nedeni biçimi

**Files:**
- Create: `LS/Domain/PaymentSource.cs`, `LS/Domain/Bank/PaymentMatchDecision.cs`, `LS/Domain/Bank/BankAskReason.cs`, `LS/Domain/Bank/BankDecisionCodes.cs`
- Modify: `LS/Domain/Bank/CustomerIbanMemory.cs` (`IbanMemorySource`'a `BankConfirm = 2`)
- Test: `T/Domain/BankDecisionCodesTests.cs` (saf, veritabanı yok)

**Interfaces:**
- Consumes: yok.
- Produces (namespace `OrderDeck.LicenseServer.Domain`): `PaymentSource { Receipt = 0, Bank = 1 }`, `PaymentApprovalSource { ReceiptHuman = 0, BankHuman = 1, BankAuto = 2 }`.
- Produces (namespace `…Domain.Bank`, sayısal değerler sırayla 0'dan, açık yazılır):
  - `PaymentMatchDecision { None, Excluded, Awaiting, Unmatched, Approved, AutoApproved, ClosedByReceipt, Dismissed }`
  - `BankAskReason { UsernameOnly, NameOnly, IbanNotConfirmed, IbanShared, IbanOtherCustomer, IbanDisputed, IbanHashUnverified, CustomerNotCanonical, NoExpectation, AmountMismatch, AmbiguousExpectations, ExpectationUnreliable, RequestInProgress, SalesSyncNotReady, PendingReceipt, UnlinkedApprovedPayment, ReceiptOtherCustomer, TooOld }` (§5.2 sırası)
  - `BankDismissReason { OwnTransfer, MarketplacePayout, Refund, Other }`, `BankRevertReason { WrongCustomer, NotCustomerPayment, BankReturned, DataSubjectObjection, Other }`
  - `BankDecisionActorKind { Owner, Staff, System }`, `BankDecisionSurface { Panel, Wpf, Sink, Sweep, Runner, ReceiptApproval, Admin }`
  - `public static class BankAskReasons { public const int MaxLength = 512; public static string? Format(IEnumerable<BankAskReason> reasons); public static IReadOnlyList<BankAskReason> Parse(string? stored); }` — `Format`: ayrık, enum sırasıyla, `,` ayraçlı, boş küme null. `Parse`: yalnız tam ad (sayı ve farklı harf reddedilir), bilinmeyen kod ATLANIR: küme büyüyüp imaj geri alınırsa satır okunur kalsın. Saklama biçimi sütunla birlikte sabitlenir; ilk kullananı S3a.

- [ ] **Step 1: Düşen testi yaz**

```csharp
public sealed class BankDecisionCodesTests
{
    private static void Names<T>(params string[] expected) where T : struct, Enum => Enum.GetNames<T>().Should().Equal(expected);

    [Fact] // adlar veritabanında durur: değişirse eski satır okunamaz
    public void Enum_adlari_sabit()
    {
        Names<PaymentSource>("Receipt", "Bank");
        Names<PaymentApprovalSource>("ReceiptHuman", "BankHuman", "BankAuto");
        Names<PaymentMatchDecision>("None", "Excluded", "Awaiting", "Unmatched", "Approved", "AutoApproved", "ClosedByReceipt", "Dismissed");
        Names<IbanMemorySource>("ManualMatch", "HumanApproval", "BankConfirm");
        Names<BankDismissReason>("OwnTransfer", "MarketplacePayout", "Refund", "Other");
        Names<BankRevertReason>("WrongCustomer", "NotCustomerPayment", "BankReturned", "DataSubjectObjection", "Other");
        Names<BankDecisionActorKind>("Owner", "Staff", "System");
        Names<BankDecisionSurface>("Panel", "Wpf", "Sink", "Sweep", "Runner", "ReceiptApproval", "Admin");
        Names<BankAskReason>("UsernameOnly", "NameOnly", "IbanNotConfirmed", "IbanShared", "IbanOtherCustomer", "IbanDisputed", "IbanHashUnverified",
            "CustomerNotCanonical", "NoExpectation", "AmountMismatch", "AmbiguousExpectations", "ExpectationUnreliable", "RequestInProgress",
            "SalesSyncNotReady", "PendingReceipt", "UnlinkedApprovedPayment", "ReceiptOtherCustomer", "TooOld"); // Parse bilinmeyeni atlar: adı değişen neden sessizce kaybolur
        ((int)PaymentSource.Receipt, (int)PaymentMatchDecision.None).Should().Be((0, 0), "DB varsayılanı CLR varsayılanıyla aynı");
    }

    [Fact]
    public void Sorma_nedenleri_enum_sirasiyla_yazilir_bilinmeyen_kod_atlanir()
    {
        BankAskReasons.Format([BankAskReason.TooOld, BankAskReason.UsernameOnly, BankAskReason.TooOld]).Should().Be("UsernameOnly,TooOld");
        BankAskReasons.Format([]).Should().BeNull();
        BankAskReasons.Parse(null).Should().BeEmpty();
        BankAskReasons.Parse("UsernameOnly,IleridekiKod,3,tooold,TooOld").Should().Equal(BankAskReason.UsernameOnly, BankAskReason.TooOld);
        BankAskReasons.Format(Enum.GetValues<BankAskReason>())!.Length.Should().BeLessThanOrEqualTo(BankAskReasons.MaxLength);
    }
}
```

- [ ] **Step 2: Koş** — `T(BankDecisionCodesTests)`. Beklenen: derleme hatası (tipler yok).
- [ ] **Step 3: Uygula** — yukarıdaki tipler; XML özetleri Türkçe, her enum'da "string saklanır, ad değişmez" notu.
- [ ] **Step 4: Yeniden koş** — `T(BankDecisionCodesTests)`. Beklenen: 2 test geçer.
- [ ] **Step 5: Commit** — `feat(bank): Faz 2 karar enum'larını ve sorma nedeni biçimini tanımla`

### Task 2: Payment, PaymentMatch ve License sütunları

**Files:**
- Modify: `LS/Domain/Payment.cs`, `LS/Domain/Bank/PaymentMatch.cs`, `LS/Domain/License.cs`, `LS/Data/LicenseDbContext.cs` (`Payment` 468-502, `PaymentMatch` 1327-1348)
- Test: `T/Data/BankModelTests.cs` (InMemory, tasarım modeli)

**Interfaces:**
- Consumes: Görev 1 enum'ları, `BankAskReasons.MaxLength`.
- Produces: `Payment.Source` (`PaymentSource`), `.ApprovalSource` (`PaymentApprovalSource?`), `.ApprovedByOperatorId`, `.WpfCustomerId`, `.BankTransactionId` (`Guid?`); `PaymentMatch.Decision`, `.DecisionRevision` (int), `.AskReasons` (`string?`), `.DecidedVia` (`BankDecisionSurface?`), `.DismissReason` (`BankDismissReason?`), `.DismissNote` (`string?`), `.SupportedByIbanMemory` (bool), `.MatcherVersion` (`int?`); `License.BankQuestionsEnabled`, `.BankAutoApproveEnabled` (bool), `.BankQuestionsSince` (`DateTimeOffset?`).

- [ ] **Step 1: Düşen testleri yaz** (`BankModelTests`; var olan `CustomerIbanMemory_lisans_icinde_iban_hash_tekil` DEĞİŞMEZ)

```csharp
// Enum_alanlari_string_saklanir'a InlineData: (Payment, Source), (Payment, ApprovalSource), (PaymentMatch, Decision), (PaymentMatch, DecidedVia), (PaymentMatch, DismissReason)
// Hash_maske_ve_aciklama_sutun_uzunluklari'na InlineData: (PaymentMatch, AskReasons, 512), (PaymentMatch, DismissNote, 300)
// InMemory DesignModel() + GetDefaultValue() burada ÖLÇMEZ, eşleme hiç yokken de geçer: "DB varsayılanı olan sütun eklemede üretilir" kuralı
// ilişkisel sağlayıcınındır; GetDefaultValue() varsayılan verilmemişken de 0. değeri döner, açıkça verildiğini yalnız TryGet söyler.
private static IModel SqlServerDesignModel() // bağlantı açmaz
{
    using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer("Server=model-okuma;Database=yok").Options);
    return db.GetService<IDesignTimeModel>().Model;
}
[Theory]
[InlineData(typeof(Payment), nameof(Payment.Source), PaymentSource.Receipt)]
[InlineData(typeof(PaymentMatch), nameof(PaymentMatch.Decision), PaymentMatchDecision.None)]
public void Eski_imajin_vermedigi_enum_sutunu_DB_varsayilaniyla_dolar(Type entity, string property, object expected)
{
    var p = SqlServerDesignModel().FindEntityType(entity)!.FindProperty(property)!;
    (p.IsNullable, p.ValueGenerated).Should().Be((false, ValueGenerated.Never), "DEFAULT yalnız eski imaj için; yeni kod değeri her INSERT'te yazar");
    p.TryGetDefaultValue(out var value).Should().BeTrue("varsayılansız NOT NULL sütun eski imajın INSERT'ini 515 ile düşürür");
    value.Should().Be(expected);
}
[Theory] // InlineData: yukarıdaki beş alan + (CustomerIbanMemory, LearnedFrom); Görev 3-4 bu listeye ekler. Sığmayan ad yazımda 2628 ile düşerdi.
public void Enum_adlari_sutuna_sigar(Type entity, string property)
{
    var p = DesignModel().FindEntityType(entity)!.FindProperty(property)!;
    Enum.GetNames(Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType).Max(n => n.Length).Should().BeLessThanOrEqualTo(p.GetMaxLength()!.Value);
}
[Theory, InlineData(nameof(Payment.WpfCustomerId)), InlineData(nameof(Payment.ApprovedByOperatorId)), InlineData(nameof(Payment.BankTransactionId))]
public void Odemenin_banka_baglari_bos_olabilir_ve_FKsizdir(string property)
{
    var p = DesignModel().FindEntityType(typeof(Payment))!.FindProperty(property)!;
    p.IsNullable.Should().BeTrue();
    p.IsForeignKey().Should().BeFalse("hareket 180. günde silinir; NoAction FK silmeyi 547 ile düşürür (§2.3)");
}
[Fact]
public void Odemenin_musteri_ve_hareket_indeksleri_filtreli()
{
    var ix = DesignModel().FindEntityType(typeof(Payment))!.GetIndexes().ToList();
    var customer = ix.Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "WpfCustomerId" }));
    (customer.IsUnique, customer.GetFilter()).Should().Be((false, "[WpfCustomerId] IS NOT NULL"), "bir müşterinin çok dekontu olur");
    var transaction = ix.Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "BankTransactionId" }));
    (transaction.IsUnique, transaction.GetFilter()).Should().Be((false, "[BankTransactionId] IS NOT NULL"),
        "geri alınan Rejected satır aynı hareketi taşır; tekil olsaydı yeniden onay 2601 alırdı");
}
[Fact]
public void Havaleler_lisans_icinde_karar_durumuyla_aranir() =>
    DesignModel().FindEntityType(typeof(PaymentMatch))!.GetIndexes().Should().Contain(i =>
        !i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "Decision" }));
[Fact]
public void Lisansin_banka_bayraklari_kapali_dogar()
{
    var l = new License();
    (l.BankQuestionsEnabled, l.BankAutoApproveEnabled, l.BankQuestionsSince).Should().Be((false, false, (DateTimeOffset?)null));
}
```

- [ ] **Step 2: Koş** — `T(BankModelTests)`. Beklenen: derleme hatası.
- [ ] **Step 3: Uygula** — alanlar ve eşleme:
  - `Payment`: `Source` `HasConversion<string>().HasMaxLength(16).HasDefaultValue(PaymentSource.Receipt).ValueGeneratedNever().IsRequired()` (alan başlangıcı `= PaymentSource.Receipt`; `ValueGeneratedNever` olmasa EF sütunu INSERT'ten çıkarır, `OUTPUT` ile geri okur); `ApprovalSource` `HasConversion<string>().HasMaxLength(16)`; `HasIndex(LicenseId, WpfCustomerId).HasFilter("[WpfCustomerId] IS NOT NULL")`; `HasIndex(LicenseId, BankTransactionId).HasFilter("[BankTransactionId] IS NOT NULL")` (tekil DEĞİL: geri alınan `Rejected` satır aynı hareketi taşır). `Status` jetonu ve var olan indeksler değişmez.
  - `PaymentMatch`: `Decision` `HasConversion<string>().HasMaxLength(24).HasDefaultValue(PaymentMatchDecision.None).ValueGeneratedNever().IsRequired()`; `AskReasons` `HasMaxLength(BankAskReasons.MaxLength)`; `DecidedVia` ve `DismissReason` string 16 / 24; `DismissNote` 300; `HasIndex(LicenseId, Decision)`. `DecisionRevision` jeton DEĞİL (jeton `UpdatedAt`). Var olan tekil indeksler (`BankTransactionId`; `PaymentId` filtreli) değişmez; `Status` ve `Layer`'a değer eklenmez.
  - `License`: üç alan, eşleme satırı yok; özet "yalnız `bank-questions` CLI'ı yazar (spec §2.7)".
- [ ] **Step 4: Yeniden koş** — `T(BankModelTests|PaymentShopperFieldsTests|PaymentMatcherTests|PaymentMatchReconcilerTests)`. Beklenen: hepsi geçer (InMemory DB varsayılanını uygulamaz, CLR varsayılanı aynıdır).
- [ ] **Step 5: Commit** — `feat(bank): Payment, PaymentMatch ve License'a Faz 2 sütunlarını ekle`

### Task 3: BankDecisionEvent, BankIbanState ve geniş IBAN indeksi

**Files:**
- Create: `LS/Domain/Bank/BankDecisionEvent.cs`, `LS/Domain/Bank/BankIbanState.cs`
- Modify: `LS/Data/LicenseDbContext.cs` (DbSet'ler, eşleme, `StampBankIbanStateVersions`, 1245-1252 yorumu, `CustomerIbanMemory` 1350-1363)
- Test: `T/Data/BankModelTests.cs` (InMemory, tasarım modeli)

**Interfaces:**
- Consumes: Görev 1.
- Produces:
  - `BankDecisionEvent`: `Guid Id, LicenseId; License License; DateTimeOffset CreatedAt; Guid? TxId, ExpectationId, PaymentId, RelatedPaymentId, StepId; BankDecisionActorKind ActorKind; Guid? OperatorId; BankDecisionSurface Surface; string Action; string? ReasonCode; Guid? RequestId; int RuleVersion; bool? WouldAutoApprove, OrderSetReliable; Guid? WpfCustomerId; string? AskReasons, DetailsJson` (nvarchar(max)). Özet: yalnız eklenir; ad, IBAN, açıklama, not YAZILMAZ; `DetailsJson` yalnız Id, sürüm, tutar, `memoryId`, `LearnedFrom`.
  - `BankIbanState`: `Guid Id, LicenseId; License License; string IbanHash; bool Shared, Disputed; DateTimeOffset LastTransferAt, UpdatedAt` (`UpdatedAt` jeton).
  - `DbSet`'ler `BankDecisionEvents`, `BankIbanStates`; `CustomerIbanMemory` geniş tekili.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
// Lisansa_dogrudan_bagli_tablo_lisansla_kaskadla_silinir: + BankDecisionEvent, BankIbanState
// Hash_sutununa_yalniz_64_hex_yazilabilir: + (BankIbanState, "BankIbanStates", IbanHash)
// Enum_alanlari_string_saklanir ve Enum_adlari_sutuna_sigar: + (BankDecisionEvent, ActorKind), (BankDecisionEvent, Surface)
// Hash_maske_ve_aciklama_sutun_uzunluklari: + (BankDecisionEvent, Action, 32), (…, ReasonCode, 32), (…, AskReasons, 512), (BankIbanState, IbanHash, 64)
[Fact]
public void CustomerIbanMemory_dar_tekil_yaninda_lisans_ozet_musteri_de_tekil() =>
    DesignModel().FindEntityType(typeof(CustomerIbanMemory))!.GetIndexes().Where(i => i.IsUnique)
        .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
        .Should().BeEquivalentTo("LicenseId,IbanHash", "LicenseId,IbanHash,WpfCustomerId"); // dar olan S3b'de düşer
[Fact]
public void Karar_olayinin_tek_FKsi_lisans_ve_RequestId_tekil_degil()
{
    var et = DesignModel().FindEntityType(typeof(BankDecisionEvent))!;
    et.GetForeignKeys().Should().ContainSingle().Which.PrincipalEntityType.ClrType.Should().Be(typeof(License));
    var request = et.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "RequestId" }));
    (request.IsUnique, request.GetFilter()).Should().Be((false, "[RequestId] IS NOT NULL"));
}
[Fact]
public void IbanState_lisans_icinde_tekil_UpdatedAt_jeton()
{
    var et = DesignModel().FindEntityType(typeof(BankIbanState))!;
    et.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "IbanHash" }));
    et.FindProperty(nameof(BankIbanState.UpdatedAt))!.IsConcurrencyToken.Should().BeTrue();
}
[Theory, InlineData(false), InlineData(true)] // PaymentMatch_UpdatedAt_atamayan_yazan_da_jetonu_ilerletir deseni
public async Task IbanState_UpdatedAt_atamayan_yazan_da_jetonu_ilerletir(bool useAsyncSave) { /* Disputed = true; UpdatedAt == özgün + 1 tick; bayat bağlamın yazısı DbUpdateConcurrencyException */ }
```

- [ ] **Step 2: Koş** — `T(BankModelTests)`. Beklenen: derleme hatası.
- [ ] **Step 3: Uygula** — eşleme:
  - `BankDecisionEvent`: `HasOne(e => e.License).WithMany()…Cascade`; enum'lar string 16; `Action` 32 zorunlu; `ReasonCode` 32; `AskReasons` `BankAskReasons.MaxLength`; indeksler `(LicenseId, CreatedAt)`, `(LicenseId, TxId)` filtre `[TxId] IS NOT NULL`, `PaymentId` filtre `[PaymentId] IS NOT NULL`, `RelatedPaymentId` filtre `[RelatedPaymentId] IS NOT NULL`, `(LicenseId, RequestId)` filtre `[RequestId] IS NOT NULL`.
  - `BankIbanState`: lisans Cascade; `(LicenseId, IbanHash)` tekil; `LastTransferAt` indeksi (12 ay saklama, S4); `IbanHash` 64 zorunlu; CHECK `CK_BankIbanStates_IbanHash_Hex64` `LEN([IbanHash]) = 64 AND [IbanHash] NOT LIKE '%[^0-9a-f]%'`; `UpdatedAt` jeton, `StampVersions<BankIbanState>(s => s.UpdatedAt)` iki `SaveChanges` override'ında.
  - `CustomerIbanMemory`: `HasIndex(LicenseId, IbanHash)` satırı KALIR, altına `HasIndex(LicenseId, IbanHash, WpfCustomerId).IsUnique()` ve yorum: dar indeks S3b'de düşer, o zamana dek iki müşteriye eşzamanlı öğretmenin tek hakemidir.
  - 1245-1252 yorumunda lisansa doğrudan bağlılar listesine `BankDecisionEvents`, `BankIbanStates`.
- [ ] **Step 4: Yeniden koş** — `T(BankModelTests)`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `feat(bank): karar olayı, IBAN durumu ve geniş IBAN indeksini modele ekle`

### Task 4: Tahsis ve kargo adımı tabloları

**Files:**
- Create: `LS/Domain/PaymentExpectationAllocation.cs`, `LS/Domain/PaymentShipmentStep.cs`
- Modify: `LS/Data/LicenseDbContext.cs`
- Test: `T/Data/BankModelTests.cs`, `T/Domain/BankDecisionCodesTests.cs` (InMemory, tasarım modeli)

**Interfaces:**
- Consumes: Görev 2 `Payment`.
- Produces (namespace `…Domain`; enum değerleri sırayla 0'dan):
  - `PaymentAllocationKind { Payment, Waived, Cancelled, OffBank }`
  - `PaymentExpectationAllocation`: `Guid Id, LicenseId` (FK'sız); `Guid? PaymentId; Payment? Payment; Guid ExpectationId` (= `PaymentRequestRecord.Id`, FK'sız); `PaymentAllocationKind Kind; decimal Amount; DateTimeOffset CreatedAt; DateTimeOffset? ReversedAt`
  - `PaymentShipmentStepState { Waiting, NeedsDecision, Bound, BoundNeedsDecision, Reverting, Closed }`
  - `PaymentShipmentStepReason { SessionLive, ModeOff, Shortage, RecipientPays, OrderSetUnknown, OrdersChanged, NothingToBind, Threshold, RetryExhausted }`
  - `PaymentShipmentStepDecision { Normal, Hold, ShipNow, RecipientPays }`
  - `PaymentShipmentStep`: `Guid Id, PaymentId, LicenseId` (FK'sız); `Payment Payment; string CustomerKey; PaymentShipmentStepState State; PaymentShipmentStepReason? Reason; Guid? ProposedShipmentId; PaymentShipmentStepDecision? Decision; Guid? LastRequestId; int Attempts; DateTimeOffset? NextAttemptAt; int Version` (jeton); `DateTimeOffset CreatedAt, StateChangedAt; List<PaymentShipmentStepOrder> Orders`
  - `PaymentShipmentStepOrder`: `Guid StepId; PaymentShipmentStep Step; Guid OrderId, LicenseId, RecordId; decimal PriceAtApproval; Guid? BoundShipmentId; DateTimeOffset? BoundSetAt`
  - DbSet'ler: `PaymentExpectationAllocations`, `PaymentShipmentSteps`, `PaymentShipmentStepOrders`.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
// Ebeveyn_uzerinden_kaskadlanan_tablonun_lisansa_FKsi_yok_ebeveyn_FKsi_kaskad:
//   + (PaymentExpectationAllocation, Payment), (PaymentShipmentStep, Payment), (PaymentShipmentStepOrder, PaymentShipmentStep)
// Enum_alanlari_string_saklanir ve Enum_adlari_sutuna_sigar: + (PaymentExpectationAllocation, Kind), (PaymentShipmentStep, State), (…, Reason), (…, Decision)
// BankDecisionCodesTests.Enum_adlari_sabit'e dört Names<…> satırı (yukarıdaki sırayla)
[Fact]
public void Tahsis_tek_FKsi_odemeye_bos_olabilir_kaskad_ve_turle_tutarli()
{
    var et = DesignModel().FindEntityType(typeof(PaymentExpectationAllocation))!;
    var fk = et.GetForeignKeys().Should().ContainSingle().Subject;
    (fk.PrincipalEntityType.ClrType, fk.DeleteBehavior, fk.IsRequired).Should().Be((typeof(Payment), DeleteBehavior.Cascade, false));
    et.GetCheckConstraints().Should().ContainSingle(c => c.Name == "CK_PaymentExpectationAllocations_Kind_Payment").Which.Sql.Should().Be(
        "([Kind] = N'Payment' AND [PaymentId] IS NOT NULL) OR ([Kind] <> N'Payment' AND [PaymentId] IS NULL)");
    et.GetIndexes().Should().Contain(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "ExpectationId" }));
}
[Fact]
public void Kargo_adimi_odeme_musteri_basina_tek_acik_kapanan_ve_cozulen_engellemez()
{
    var et = DesignModel().FindEntityType(typeof(PaymentShipmentStep))!;
    et.GetForeignKeys().Should().ContainSingle("LicenseId, ProposedShipmentId, LastRequestId FK'sız");
    var open = et.GetIndexes().Single(i => i.GetDatabaseName() == "UX_PaymentShipmentSteps_Open");
    (open.IsUnique, open.GetFilter()).Should().Be((true, "[State] <> N'Closed' AND [State] <> N'Reverting'"));
    open.Properties.Select(p => p.Name).Should().Equal("PaymentId", "CustomerKey");
    et.GetIndexes().Should().Contain(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "PaymentId" }),
        "filtreli bileşik indeks EF'in FK indeksini bastırır; kaskad ve geri alma PaymentId ile arar");
    et.GetIndexes().Single(i => i.GetDatabaseName() == "IX_PaymentShipmentSteps_Due").GetFilter().Should().Be("[State] IN (N'Waiting', N'Reverting')");
    et.FindProperty(nameof(PaymentShipmentStep.CustomerKey))!.GetCollation().Should().Be("Latin1_General_100_BIN2");
    et.FindProperty(nameof(PaymentShipmentStep.Version))!.IsConcurrencyToken.Should().BeTrue();
}
[Fact]
public void Adim_siparisi_anahtari_adim_ve_siparis_Orders_FKsi_yok()
{
    var et = DesignModel().FindEntityType(typeof(PaymentShipmentStepOrder))!;
    et.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal("StepId", "OrderId");
    et.GetForeignKeys().Should().ContainSingle().Which.PrincipalEntityType.ClrType.Should().Be(typeof(PaymentShipmentStep));
    et.GetIndexes().Should().Contain(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "OrderId" }));
}
```

- [ ] **Step 2: Koş** — `T(BankModelTests|BankDecisionCodesTests)`. Beklenen: derleme hatası.
- [ ] **Step 3: Uygula** — tahsiste `HasOne(a => a.Payment).WithMany().HasForeignKey(a => a.PaymentId).OnDelete(DeleteBehavior.Cascade)` AÇIKÇA (isteğe bağlı FK'nın varsayılanı `ClientSetNull`, yayıncı silmesi 547 alırdı), `Kind` string 16, `Amount` (18,2), CHECK ve indeks testteki gibi. Adımda Payment Cascade, `CustomerKey` 64 + harman, `State` 24, `Reason` 24, `Decision` 16 string, `Version` `IsConcurrencyToken()`; indeksler: `(PaymentId, CustomerKey)` tekil filtreli `UX_PaymentShipmentSteps_Open`, açık `HasIndex(s => s.PaymentId)`, `NextAttemptAt` filtreli `IX_PaymentShipmentSteps_Due`, `(LicenseId, State)`. Adım siparişinde bileşik PK, Step Cascade (`WithMany(s => s.Orders)`), `PriceAtApproval` (18,2), `(LicenseId, OrderId)`.
- [ ] **Step 4: Yeniden koş** — `T(BankModelTests|BankDecisionCodesTests)`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `feat(bank): ödeme tahsisi ve kargo adımı tablolarını modele ekle`

### Task 5: Göç `BankDecisionsS2a`

**Files:**
- Create (üretilir): `LS/Data/Migrations/<zaman>_BankDecisionsS2a.cs`, `.Designer.cs`; Modify: `LicenseDbContextModelSnapshot.cs`
- Test: `T/Data/BankDecisionsS2aMigrationTests.cs` — `BankDecisionsS2aMigrationScriptTests` (Docker'sız), `BankDecisionsS2aMigrationTests` ve `BankDecisionsS2aMigrationExpressTests` (SQL Server), `internal static BankDecisionsS2aMigrationScenario`; `T/Controllers/Customers/CustomerPurgeRelationalTests.cs` (SQL Server, kaskad)

**Interfaces:**
- Consumes: Görev 2-4 modeli.
- Produces: `public const string BankDecisionsS2a.BackfillPaymentCustomers` (dekontun müşterisini bağdan türetir; S2b kopyalar); `BankDecisionsS2aMigrationScenario.Migrations(db)` (Step 1), `RunAsync(connectionString, output)` (Step 6).

- [ ] **Step 1: Docker'sız düşen testi ve senaryonun iskeletini yaz** — iskelet aynı dosyada, burada yazılmazsa test projesi Step 5'te derlenmez (desen `PersonBalanceS1MigrationScenario.Migrations` 148-153; Step 6 `RunAsync` ile genişletir)

```csharp
public sealed class BankDecisionsS2aMigrationScriptTests
{
    private static readonly IReadOnlyList<MigrationOperation> Up = new BankDecisionsS2a().UpOperations;

    [Fact]
    public void Yalniz_genisletir()
    {
        Up.Should().NotContain(o => o is DropIndexOperation || o is DropColumnOperation || o is DropTableOperation
            || o is AlterColumnOperation || o is RenameColumnOperation || o is DeleteDataOperation);
        Up.OfType<CreateIndexOperation>().Should().Contain(i => i.Name == "IX_CustomerIbanMemories_LicenseId_IbanHash_WpfCustomerId" && i.IsUnique);
        Up.OfType<CreateTableOperation>().Select(t => t.Name).Should().BeEquivalentTo("BankDecisionEvents", "BankIbanStates",
            "PaymentExpectationAllocations", "PaymentShipmentSteps", "PaymentShipmentStepOrders");
        Up.OfType<AddColumnOperation>().Where(c => !c.IsNullable).Should().OnlyContain(c => c.DefaultValue != null,
            "geri alınan imajın INSERT'i sütunu vermez");
    }
    [Fact]
    public void Geri_doldurma_sutundan_sonra_indeksten_once_ve_UpdatedAt_Status_yazmaz()
    {
        var ops = Up.ToList();
        var column = ops.FindIndex(o => o is AddColumnOperation { Table: "Payments", Name: "WpfCustomerId" });
        var sql = ops.FindIndex(o => o is SqlOperation s && s.Sql.Contains("UPDATE p"));
        var index = ops.FindIndex(o => o is CreateIndexOperation { Name: "IX_Payments_LicenseId_WpfCustomerId" });
        (column >= 0 && column < sql && sql < index).Should().BeTrue();
        ops.OfType<SqlOperation>().Should().ContainSingle("IBAN bağına ve eşleşme kararına dokunulmaz (Karar 5)")
            .Which.Sql.Should().NotContainAny("UpdatedAt", "[Status]", "DELETE");
    }
    [Fact]
    public void Betikte_ONLINE_yok_varsayilanlar_var()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer("Server=betik-uretimi;Database=yok").Options);
        var (previous, target) = BankDecisionsS2aMigrationScenario.Migrations(db);
        var script = db.GetService<IMigrator>().GenerateScript(previous, target);
        Regex.IsMatch(script, @"\bONLINE\b", RegexOptions.IgnoreCase).Should().BeFalse("prod Express");
        script.Should().Contain("[Source] nvarchar(16) NOT NULL DEFAULT N'Receipt'").And.Contain("[Decision] nvarchar(24) NOT NULL DEFAULT N'None'")
            .And.Contain("[DecisionRevision] int NOT NULL DEFAULT 0").And.Contain("[SupportedByIbanMemory] bit NOT NULL DEFAULT")
            .And.Contain("[BankQuestionsEnabled] bit NOT NULL DEFAULT").And.Contain("[BankAutoApproveEnabled] bit NOT NULL DEFAULT");
    }
}
internal static class BankDecisionsS2aMigrationScenario
{
    public static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_BankDecisionsS2a", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }
}
```

- [ ] **Step 2: Koş** — `T(BankDecisionsS2aMigrationScriptTests)`. Beklenen: derleme hatası (eksik tek tip `BankDecisionsS2a`).
- [ ] **Step 3: Göçü üret** — `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add BankDecisionsS2a --project OrderDeck.LicenseServer --startup-project OrderDeck.LicenseServer --context LicenseDbContext --output-dir Data/Migrations` (araç 10.0.10; `--context` şart, `LicenseReadOnlyDbContext` de kayıtlı). Üretilende `DropIndex` varsa dar indeks satırı modelden kaybolmuştur: aynı bayraklarla `dotnet ef migrations remove --force`, modeli düzelt, yeniden üret. İki enum sütununda `defaultValue: "Receipt"` / `"None"` üretilmiş olmalı (elle düzeltme gerekmez).
- [ ] **Step 4: Geri doldurmayı ekle** — son `AddColumn`'dan sonra, ilk `CreateTable`/`CreateIndex`'ten önce ayrı bir `migrationBuilder.Sql(...)` (tek `Sql()` içinde DDL ile DML karıştırma). Sınıf özeti: yalnız genişletir, eski imaj yazmayı sürdürür, geri doldurma bağdan türetir ve S2b'de yeniden koşar, dar IBAN indeksi S3b'de düşer, kopyada kalmış IBAN bağı taşınmaz (Karar 5).

```csharp
// Dekontun müşterisini bağdan TÜRETİR: boşu da doluyu da yazar, değişmeyen satıra ve shopper'sız dekonta (bağı yok) dokunmaz (Karar 3).
// Bağ seçimi PaymentMatchReconciler.ResolveWpfCustomerAsync ile aynı: LeftAt'e bakılmaz, en yeni JoinedAt'li MÜŞTERİLİ bağ
// (eşitlikte Id, oradaki sıra belirsiz). Asıl kayda en çok 3 adım (CustomerIdResolver.MaxHops); başka lisansın kaydından öteye
// izlenmez (varılan Id yazılır, CustomerIdResolver ile aynı). UpdatedAt yazılmaz (payments/since imleci).
public const string BackfillPaymentCustomers = """
    UPDATE p
    SET p.[WpfCustomerId] = COALESCE(h3.[MergedIntoId], h2.[MergedIntoId], h1.[MergedIntoId], l.[WpfCustomerId])
    FROM [Payments] p
    OUTER APPLY (
        SELECT TOP (1) x.[WpfCustomerId] FROM [ShopperBroadcasterLinks] x
        WHERE x.[ShopperId] = p.[ShopperId] AND x.[LicenseId] = p.[LicenseId] AND x.[WpfCustomerId] IS NOT NULL
        ORDER BY x.[JoinedAt] DESC, x.[Id] DESC) l
    LEFT JOIN [WpfCustomerProjections] h1 ON h1.[Id] = l.[WpfCustomerId] AND h1.[LicenseId] = p.[LicenseId]
    LEFT JOIN [WpfCustomerProjections] h2 ON h2.[Id] = h1.[MergedIntoId] AND h2.[LicenseId] = p.[LicenseId]
    LEFT JOIN [WpfCustomerProjections] h3 ON h3.[Id] = h2.[MergedIntoId] AND h3.[LicenseId] = p.[LicenseId]
    WHERE p.[ShopperId] IS NOT NULL AND NOT EXISTS (
        SELECT p.[WpfCustomerId] INTERSECT
        SELECT COALESCE(h3.[MergedIntoId], h2.[MergedIntoId], h1.[MergedIntoId], l.[WpfCustomerId]));
    """;
```

- [ ] **Step 5: Yeniden koş** — `T(BankDecisionsS2aMigrationScriptTests|BankModelTests)` geçer; `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations has-pending-model-changes --project OrderDeck.LicenseServer --startup-project OrderDeck.LicenseServer --context LicenseDbContext` → "No changes".
- [ ] **Step 6: SQL Server senaryosunu yaz** (desen `PersonBalanceS1MigrationTests`: `Scenario.RunAsync(cs, output)` Developer ve Express'te; önceki göçe kur, tohumla, hedefi uygula). `Payments`, `PaymentMatches`, `Licenses` eski şemada modelle YAZILAMAZ: ESKİ sütun listeleriyle ham SQL. Öbür tablolar (projeksiyon, shopper, bağ, hareket, IBAN hafızası) S2a'da sütun almaz, EF ile tohumlanır. `PreS2aPaymentColumns`, `PreS2aMatchColumns`, `PreS2aLicenseColumns` hedeften önce `INFORMATION_SCHEMA.COLUMNS` ile `BeEquivalentTo` karşılaştırılır (`Licenses.RowVersion` hariç): eksik liste "eski imaj yazar" iddiasını boşa düşürür. Asıl test adı `Goc_genisletir_geri_doldurur_eski_imaj_yazmayi_surdurur`; iddialar:

| Konu | İddia |
|---|---|
| Geri doldurma (K3→K2→K1→P zinciri, Q asıl) | (a) eski ayrılmış bağ→Q + yeni bağ→K2 ⇒ P; (b) yalnız ayrılmış bağ→Q ⇒ Q; (c) yeni bağ müşterisiz + eski→Q ⇒ Q; (d) bağ yalnız başka lisansta ⇒ NULL; (e) `ShopperId` NULL ⇒ NULL; (f) bağsız shopper ⇒ NULL; (g) bağ→K3 (3 adım, `MaxHops` sınırı) ⇒ P; (h) bağ→projeksiyonu olmayan Id ⇒ o Id; (i) iki lisanslı shopper ⇒ her dekont kendi lisansının müşterisi; (j) bağ→K4, K4 başka lisanstaki F'ye, F orada G'ye yönlenmiş ⇒ F (lisans süzgeci zinciri durdurur, G değil). Hepsinde `Source='Receipt'`, `ApprovalSource` NULL; `UpdatedAt` ve `Status` (Pending, Approved, Rejected örnekleri) göç öncesiyle aynı |
| Çözücülerle eşlik | göçten ve yeniden türetmeden sonra `ShouldMatchResolversAsync` (aşağıda): SQL'in sonucu her dekontta üretim kuralınınkiyle aynı |
| IBAN (Karar 5) | özet A→K2 (kopya), özet B→Q (asıl); her birini öğreten eşleşme satırı aynı müşteride (öneri ve karar). Göçten sonra bağ da öneri de karar da yerinde: A K2'de, B Q'da. Dar ve geniş indeks ikisi de var ve tekil; özet A'ya başka müşteri ⇒ 2601 ve ileti `'IX_CustomerIbanMemories_LicenseId_IbanHash'` taşır |
| Yeniden türetme (S2b'nin dayanağı; geri alma penceresinin kalıntısı göçten sonra elle kurulur) | Göçten hemen sonra ikinci koşu 0 satır. Sonra: (1) bağlantısı silinmiş, dekontu dolu ⇒ NULL; (2) bağlantısı NULL'a düşmüş, dekontu kopyada ⇒ NULL; (3) bağlantısı W'ye taşınmış, dekontu kopyada ⇒ W; (4) göçten sonra eski listeyle eklenen (a) shopper'ının boş dekontu ⇒ P; (5) `ShopperId` NULL, müşterisi dolu dekont ⇒ aynı. Koşu tam 4 satır yazar, bir daha koşunca 0; `UpdatedAt` ve `Status` değişmez |
| Eski imaj | Üç tabloya (`Payments`, `PaymentMatches`, `Licenses`) eski listeyle INSERT geçer: `Source='Receipt'`, `Decision='None'`, `DecisionRevision=0`, `SupportedByIbanMemory=0`, `MatcherVersion` NULL, iki bayrak 0; EF modeli bu satırları okur; `UPDATE Payments SET Status=1 … WHERE Id=@id AND Status=0` 1 satır |
| Yeni tablolar | beşi 0 satır; `sys.foreign_keys` = Global Constraints listesi, hepsi CASCADE; `CustomerKey` BIN2; `BankIbanStates`'e hex olmayan özet 547; `Kind='Payment'` ve `PaymentId` NULL 547 |
| Kargo adımı (EF ile) | `Waiting` varken ikinci `Waiting` 2601 `UX_PaymentShipmentSteps_Open`; ilki `Closed` olunca ve ilki `Reverting` iken ikinci girer |
| Süre | 30.000 dekont + bağ tohumuyla `Stopwatch` ölçüsü `ITestOutputHelper`'a (iddia değil); Express sınıfı sürümü `SERVERPROPERTY('Edition')` ile doğrular |

  `private static async Task ShouldMatchResolversAsync(LicenseDbContext db, params Guid[] licenseIds)` gövdesi (toplu tohumun lisansı ve bağdan türetilmeyen shopper'sız dekont hariç; `JoinedAt` eşitliği tohumlanmaz):

```csharp
foreach (var p in await db.Payments.AsNoTracking().Where(x => x.ShopperId != null && licenseIds.Contains(x.LicenseId)).ToListAsync())
{
    var bond = await db.ShopperBroadcasterLinks.AsNoTracking() // PaymentMatchReconciler.ResolveWpfCustomerAsync'in (private) sorgusu
        .Where(l => l.ShopperId == p.ShopperId && l.LicenseId == p.LicenseId && l.WpfCustomerId != null)
        .OrderByDescending(l => l.JoinedAt).Select(l => l.WpfCustomerId).FirstOrDefaultAsync();
    Guid? expected = bond is { } id ? await new CustomerIdResolver(db).CanonicalOfAsync(p.LicenseId, id, default) : null;
    p.WpfCustomerId.Should().Be(expected);
}
```

- [ ] **Step 7: Kaskad testini yaz** (SQL Server) — `CustomerPurgeRelationalTests.Banka_onay_satirlari_lisansla_gider`: Payment, ona bağlı tahsis (`Kind = Payment`), adım + adım siparişi, karar olayı, IBAN durumu olan yayıncı silinir ⇒ 204 ve beş tabloda o lisanstan 0 satır (lisansta `ObifinConnection` yok; varsa 409). Ödemesiz tahsis tohumlanmaz: kaskadla gitmez, onu S4 siler.
- [ ] **Step 8: Derle** — `B`, 0 hata. SQL Server testleri CI'da koşar.
- [ ] **Step 9: Commit** — `feat(bank): BankDecisionsS2a genişletme göçü ve geri doldurma`

### Task 6: Birleştirici: IBAN tekilleştirme ve dekontun müşterisi

**Files:**
- Modify: `LS/Services/CustomerSync/CustomerIdentityMerger.cs` (36-38, 50, 129-131, 148-149, sınıf özeti), `CustomerIdentityMergeJob.cs` (rapor: 236, 326, 419, 437, 617), `LS/Tools/MergeCustomerIdentities.cs` (228, 255)
- Create: `T/TestHelpers/SharedIbanSchema.cs`
- Test: `T/Services/CustomerSync/CustomerIdentityMergerTests.cs` (InMemory); `CustomerIdentityMergerRelationalTests.cs`, `CustomerIdentityMergeJobTests.cs`, `T/Controllers/Licenses/WpfCustomerIdentityRaceTests.cs`, `T/Tools/MergeCustomerIdentitiesRelationalTests.cs` (SQL Server)

**Interfaces:**
- Consumes: `Payment.WpfCustomerId`, `IbanMemorySource.BankConfirm`, geniş indeks.
- Produces: `RepointCounts(…, int WaConversations, int Payments)` (sona eklenir; `IbanMemories` silinen ikizi de sayar); `CustomerIdentityMergeJob.Report.PaymentsToMove`; CLI satırında `ödeme eşleşmesi=…`den sonra `dekont müşterisi={r.PaymentsToMove}`; `SharedIbanSchema.ApplyAsync(string connectionString)` (`DROP INDEX [IX_CustomerIbanMemories_LicenseId_IbanHash] ON CustomerIbanMemories`: S3b sonrası şema; S2b de kullanır. Özeti: fabrikayı başlatmaz, göçler kurulmuş olmalı).

- [ ] **Step 1: Düşen InMemory testlerini yaz** (InMemory tekil uygulamaz, çift tohumlanabilir)

```csharp
[Theory]
[InlineData(IbanMemorySource.BankConfirm, IbanMemorySource.HumanApproval, IbanMemorySource.BankConfirm)]   // asıl kayıttaki kalır, kopyanın "Evet"i ona geçer
[InlineData(IbanMemorySource.HumanApproval, IbanMemorySource.BankConfirm, IbanMemorySource.BankConfirm)]   // asıl kayıttaki kalır
[InlineData(IbanMemorySource.BankConfirm, IbanMemorySource.BankConfirm, IbanMemorySource.BankConfirm)]     // eski CreatedAt (asıl kayıttaki) kalır
[InlineData(IbanMemorySource.ManualMatch, IbanMemorySource.HumanApproval, IbanMemorySource.HumanApproval)] // BankConfirm yok: kalanın kaynağı aynen
[InlineData(IbanMemorySource.HumanApproval, IbanMemorySource.ManualMatch, IbanMemorySource.ManualMatch)]   // ters yön: düşenin kaynağı kalana geçmez
public async Task Iki_kayitta_ayni_IBAN_tek_satira_iner(IbanMemorySource copySource, IbanMemorySource canonicalSource, IbanMemorySource expectedSource)
// CreatedAt: asıl t0, kopya t0+1g; maske ve öğreten havale her bağda ayrı üretilir. Kalan tek satır hep asıl kayıttaki: LearnedFrom beklenen,
// CreatedAt, SourceBankTransactionId ve IbanMasked değişmez, WpfCustomerId == canonical.Id, counts.IbanMemories == 1

[Fact] public async Task Son_dogrulama_ikisinin_en_yenisi_olur()          // kalan 10 gün önce, düşen 2 gün önce ⇒ 2 gün önce
[Fact] public async Task Dusenin_bos_son_dogrulamasi_kalani_degistirmez() // hata yönü erken silme
[Fact] public async Task Farkli_IBAN_tasinir_baska_lisansin_satirina_dokunulmaz()
[Fact] public async Task Dekontun_musterisi_asil_kayda_cevrilir_UpdatedAt_ve_Status_degismez()
// üç dekont: kopyaya bağlı (Pending), kopyaya bağlı (Approved), başka lisansta kopya Id'siyle; counts.Payments.Should().Be(2);
// UpdatedAt ve Status aynı, başka lisansınki kopyada kalır. Yozlaşmış çağrı testine (kopya==asıl): counts.Payments.Should().Be(0)
```

- [ ] **Step 2: Koş** — `T(CustomerIdentityMergerTests)`. Beklenen: derleme hatası (`Payments` sayacı yok); sayaç eklenince çift satır kalır, testler düşer.
- [ ] **Step 3: Uygula** — erken çıkış (50) `return new RepointCounts(0, 0, 0, 0, 0, 0, 0, 0, 0);`. IBAN bloğu (129-131) yerine:

```csharp
var moving = await _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId && m.WpfCustomerId == fromId).ToListAsync(ct);
if (moving.Count > 0)
{
    var hashes = moving.Select(m => m.IbanHash).Distinct().ToList();
    // Karar Local'den: aynı SaveChanges'te önceki kopyadan taşınmış satır veritabanında eski kimliktedir. Local, Deleted'ı içermez.
    await _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId && m.WpfCustomerId == toId && hashes.Contains(m.IbanHash)).LoadAsync(ct);
    foreach (var m in moving)
    {
        var twin = _db.CustomerIbanMemories.Local.FirstOrDefault(x => x != m && x.LicenseId == licenseId && x.WpfCustomerId == toId && x.IbanHash == m.IbanHash);
        if (twin is null) { m.WpfCustomerId = toId; continue; }
        var (keep, drop) = PreferBond(m, twin);
        if (drop.LearnedFrom == IbanMemorySource.BankConfirm) keep.LearnedFrom = IbanMemorySource.BankConfirm;
        if (drop.LastConfirmedAt is { } at && (keep.LastConfirmedAt ?? keep.CreatedAt) < at) keep.LastConfirmedAt = at;
        keep.WpfCustomerId = toId;
        _db.CustomerIbanMemories.Remove(drop);
    }
}
// 148-149 yerine (payments: aşağıdaki dekont bloğu):
return new RepointCounts(orders.Count, shipments.Count, links.Count, txs.Count, balances, moving.Count, matches.Count, convs.Count, payments.Count);

/// <summary>Eski CreatedAt, eşitse küçük Id ("N" ordinal); kaynağa bakılmaz. İkisinden biri BankConfirm ise döngü kalanın kaynağını
/// BankConfirm yapar: tek kayıttaki tazelemenin ve §2.5 upsert'inin eşi, öğreten havale ve CreatedAt değişmez.</summary>
private static (CustomerIbanMemory Keep, CustomerIbanMemory Drop) PreferBond(CustomerIbanMemory a, CustomerIbanMemory b)
```

  `PaymentMatches` ve öbür bloklar aynen. `WaConversations`'tan önce dekont bloğu: `Payments.Where(LicenseId, WpfCustomerId == fromId)` izlenerek yüklenir, yalnız `WpfCustomerId = toId` yazılır (`UpdatedAt` İLERLETİLMEZ: `payments/since` imleci bütün dekontları yeniden yollardı; `Status` jetondur, dokunulmaz). Sınıf özeti: tekilleştirme kuralı ve tek kayıtla eşliğinin iki sınırı (Karar 9), imha kaydının neden yazılmadığı, jeton çakışmasını çağıranın ele aldığı (senkronda bir yeniden deneme, işte grup geri alma). `MergeJob`: `otherIds.Contains(p.WpfCustomerId.Value)` sayımı, `Tally.Payments`, `Report.PaymentsToMove`; CLI `Format` ve `Sum` (`PaymentsToMove = a.PaymentsToMove + b.PaymentsToMove`; unutulursa TOPLAM satırı sessizce 0 basar).
- [ ] **Step 4: Yeniden koş** — `T(CustomerIdentityMergerTests|MergeCustomerIdentitiesTests|LicensesWpfCustomersSyncControllerTests)`. Beklenen: geçer.
- [ ] **Step 5: SQL Server testlerini yaz**
  - `CustomerIdentityMergerRelationalTests`: sınıfa `private string _cs = null!;` (`InitializeAsync`'teki yerel `cs` yerine; `PreB1Schema.ApplyAsync` fabrikayı zaten başlatır). İki IBAN testinin ilk satırı `await SharedIbanSchema.ApplyAsync(_cs);` (`InitializeAsync`'e KONMAZ, öbür testlerin şeması değişirdi): `Ayni_ozetli_uc_bag_tek_SaveChangeste_tekillesir` (kopya1 `HumanApproval` t0, asıl `HumanApproval` t0+1g ve üçünün en yeni doğrulaması, kopya2 `BankConfirm` t0+2g; iki `RepointReferencesAsync`, tek `SaveChanges`; kalan kopya1'inki, kaynağı `BankConfirm`, `LastConfirmedAt` ilk adımda düşen asıl kayıttakinden gelir, 2601 yok; ikinci adımın ikizi yalnız izleyicide bulunur); `Kopyanin_satiri_kalirken_asil_satir_once_silinir` (kopyanınki daha eski `HumanApproval`, asıl kayıttaki `BankConfirm`; kalan kopyanınki, kaynağı `BankConfirm`; DELETE, UPDATE'ten önce); `Once_birlestirme_sonra_onay_iki_yazim_da_kalir` (birleştirme kaydeder, onay bağlamı eski okumasıyla `Status=Approved` yazar ⇒ 1 satır, `WpfCustomerId` asıl).
  - `WpfCustomerIdentityRaceTests`: `Senkron_sirasinda_onaylanan_dekont_senkronu_dusurmez` — geçici S + telefonu kanıtlı bağ + S'ye bağlı Pending dekont; `_race.BeforeSave` ilk kayıtta ayrı bağlamdan `ExecuteUpdate` ile `Status=Approved`; yanıt 200, dekont `Approved` ve `WpfCustomerId == W`, bağ W'de.
  - `CustomerIdentityMergeJobTests.Kuru_calistirma_tasinacak_…`: kopyaya bağlı bir dekont tohumla, `dryRun.PaymentsToMove.Should().Be(1)`, uygulamadan sonra asıl kayıtta.
  - `MergeCustomerIdentitiesRelationalTests.Kuru_calistirma_dekont_musterisini_lisanslar_arasinda_toplar`: `SeedPersonAsync` iki kez (iki lisans), her kopyaya bir dekont, kuru koşu `license: null` ⇒ çıkış 0, metin `TOPLAM (2 lisans)` ve `dekont müşterisi=2` içerir.
- [ ] **Step 6: Derle** — `B`, 0 hata.
- [ ] **Step 7: Commit** — `feat(bank): birleştirici çift IBAN bağını tekilleştirsin, dekontun müşterisini çevirsin`

### Task 7: Kişi bağını kesen yollar

**Files:**
- Modify: `LS/Services/CustomerSync/CustomerIdentityMerger.cs` (yeni yöntem), `LS/Controllers/Licenses/LicensesWpfCustomersSyncController.cs` (`ReproveLinksAsync` 727), `LS/Services/CustomerSync/CustomerIdentityMergeJob.cs` (`provisionalLinks` döngüsü), `LS/Services/Shoppers/ShopperPurgeService.cs` (203-217, `HasPersonalFields(Payment)`, 3b yorumu)
- Test: `T/Controllers/Licenses/LicensesWpfCustomersSyncControllerTests.cs`, `T/Services/Shoppers/ShopperPurgeServiceTests.cs` (InMemory); `T/Services/CustomerSync/CustomerIdentityMergeJobTests.cs` (SQL Server)

**Interfaces:**
- Consumes: `Payment.WpfCustomerId`, `Payment.ShopperId`.
- Produces: `public Task CustomerIdentityMerger.UnbindPaymentsAsync(ShopperBroadcasterLink link, Guid customerId, CancellationToken ct)` — müşterisini o bağlantıdan almış dekontlarda alanı boşaltır: shopper'ın, veritabanında bağlantıyla aynı müşteriye bağlı ve izleyicide `customerId`'ye çevrilmiş dekontları (yerinde kanıtta ikisi aynı kayıt). Shopper'ın asıl kayda zaten bağlı dekontu kalır: bağı başka bağlantıdan gelir, iş o bağa dokunmaz (türetme kuralı §2.3). `SaveChanges` çağırmaz, `UpdatedAt` ilerletmez.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
// LicensesWpfCustomersSyncControllerTests (SeedProvisionalAsync + dekont tohumu: ShopperId = s.ShopperId, WpfCustomerId = s.ProjectionId)
[Theory, InlineData(2), InlineData(1)]
public async Task Devralmada_kanitsiz_baglantinin_dekontu_musterisiz_kalir(int format)
// başka telefonla devralma ⇒ dekont.WpfCustomerId null, UpdatedAt aynı; W'ye bağlı başka shopper'ın dekontu W'de kalır
[Fact] public async Task Devralmada_kanitli_baglantinin_dekontu_W_ye_gecer()   // telefon = s.ShopperPhone ⇒ W
[Fact] public async Task Kendi_Id_farkli_telefonda_dekont_musterisiz_kalir()    // bağ S'den düşer ⇒ null

// ShopperPurgeServiceTests
[Fact]
public async Task Odemenin_musteri_bagi_silmede_bosalir_tekrar_silme_saymaz()
// SeedLinkedShopper + SeedPayment(WpfCustomerId = purgedId); ilk silme ⇒ null, "Payments.PersonalFields" 1; yalnız WpfCustomerId'si dolu
// (öbür alanları boş) dekont da sayılır; ikinci silme yeni kayıt yazmaz

// CustomerIdentityMergeJobTests (SQL Server): Kanitsiz_gecici_baglantinin_dekontu_musterisiz_kalir
// Gecici_satir_asil_kayit_olmaz… kurulumu + geçiciye bağlı dekont ⇒ LinksUnbound 1, dekont null. AddVerifiedShopperLink yalnız bağ Id'si döner,
// UnbindPaymentsAsync ise ShopperId ile arar:
var shopperId = db.ShopperBroadcasterLinks.Local.Single(l => l.Id == linkId).ShopperId;
db.Payments.Add(new Payment { Id = Guid.NewGuid(), LicenseId = lic, ShopperId = shopperId, WpfCustomerId = provisional.Id,
    ReferansNo = $"r-{Guid.NewGuid():N}", Amount = 10m, PaidAt = t0, CreatedAt = t0, UpdatedAt = t0 }); // ReferansNo lisansta tekil, her dekontta üret

// CustomerIdentityMergeJobTests (SQL Server, S2a incelemesinden)
[Theory, InlineData(false), InlineData(true)]
public async Task Kanitsiz_tasinan_baglanti_yalniz_kopyadan_gelen_dekontu_bosaltir(bool canonicalProvisional)
// aynı shopper: ayrılmış eski bağ geçici kopyada, daha yeni etkin bağ asıl kayıtta; asıl kaydın telefonu shopper'ınki değil
// (canonicalProvisional: asıl kayıt da geçici) ⇒ LinksUnbound 1, ayrılmış bağ null, etkin bağ asıl kayıtta;
// asıl kayda bağlı dekont asıl kayıtta kalır, kopyaya bağlı dekont null, UpdatedAt aynı
```

- [ ] **Step 2: Koş** — `T(LicensesWpfCustomersSyncControllerTests|ShopperPurgeServiceTests)`. Beklenen: kanıtsız bağın dekontu W'de kaldığı, silmede müşteri dolu kaldığı için düşer.
- [ ] **Step 3: Uygula** — `UnbindPaymentsAsync`: bağlantının veritabanındaki müşterisi skaler okumayla (`ShopperBroadcasterLinks.Where(Id).Select(WpfCustomerId)`: kimlik çözümüne girmez, izlenen değeri değil veritabanındakini verir; boşsa çık), sonra `Payments.Where(LicenseId, ShopperId, WpfCustomerId == o değer).ToListAsync` (taşınan bağlantı da dekont da veritabanında eski kimliktedir), izlenen değeri `customerId` olan null. Bütün `WpfCustomerId == customerId` dekontları boşaltmak yanlış: aynı shopper'ın asıl kayda doğrudan bağlı ve işin yeniden kanıtlamadığı bir bağlantısı varsa bağ yaşarken dekont müşterisiz kalır (S2a incelemesi, LocalDB'de ölçüldü). Senkronda sonuç aynı (devralmada dekont da bağ da S'den gelir, yerinde kanıtta ikisi de R). "Asıl kayda bağlı başka bağlantı kalıyorsa boşaltma" kuralı seçilmedi: kopyadan taşınan dekontu, onu getiren bağ kanıtsız kalmışken, shopper'ın asıl kayda başka bir bağı var diye asıl kayıtta bırakırdı (Karar 4). `ReproveLinksAsync`'te `row.Link.WpfCustomerId = null`'dan sonra `await _merger.UnbindPaymentsAsync(row.Link, id, ct)`; işte `link.WpfCustomerId = null`'dan sonra `(link, canonical.Id)` ile. Shopper silmesinin 2. adımında `p.WpfCustomerId = null;` ve `HasPersonalFields`'e `|| p.WpfCustomerId is not null` (hedef adı `Payments.PersonalFields` değişmez); yorum: bağ S2a geri doldurmasıyla geldi, projeksiyon kullanıcı adını koruduğu için kişiye çözülürdü; banka satırını bulma S4'te.
- [ ] **Step 4: Yeniden koş** — aynı süzgeç + `T(CustomerIdentityMergerTests)`. Beklenen: geçer. `B` 0 hata.
- [ ] **Step 5: Commit** — `fix(kvkk): kanıtı düşen bağın ve silinen kişinin dekontunda müşteri bağını kes`

### Task 8: Banka ayarları, compose ve README şablonu

**Files:**
- Modify: `LS/Services/Bank/BankOptions.cs`, `LS/appsettings.json` (`OrderDeck:Bank`), `deploy/docker-compose.yml` (204'ün altı), `deploy/README.md` (`.env` şablonu, HashKey satırının altı)
- Test: `T/Services/Bank/BankOptionsTests.cs`, `T/Services/Bank/BankQuestionsDeployTests.cs` (saf; desen `PersonBalanceDeployTests`)

**Interfaces:**
- Consumes: yok. Produces: `BankOptions.ExpectationTtlDays` (int, 14), `BankOptions.StaleFeedAfterMinutes` (int, 120), `TimeSpan BankOptions.StaleFeedAfter => TimeSpan.FromMinutes(StaleFeedAfterMinutes)`. Okuyanı S3a ve S3c.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
// BankOptionsTests
[Fact] public void Varsayilanlar() // 14, 120, TimeSpan.FromHours(2)
[Fact] public void Ortam_anahtarlari_baglanir()
// ConfigurationBuilder().AddInMemoryCollection({"OrderDeck:Bank:ExpectationTtlDays":"21","OrderDeck:Bank:StaleFeedAfterMinutes":"30"})
//   .Build().GetSection("OrderDeck:Bank").Get<BankOptions>() ⇒ 21, 30 dk

// BankQuestionsDeployTests
[Fact]
public void Compose_iki_ayari_sayisal_varsayilanla_gecirir()
{
    var compose = ReadRepoFile("deploy/docker-compose.yml");
    compose.Should().Contain("      OrderDeck__Bank__ExpectationTtlDays: \"${OrderDeck__Bank__ExpectationTtlDays:-14}\"")
        .And.Contain("      OrderDeck__Bank__StaleFeedAfterMinutes: \"${OrderDeck__Bank__StaleFeedAfterMinutes:-120}\"")
        .And.NotContain("${OrderDeck__Bank__ExpectationTtlDays:?").And.NotContain("${OrderDeck__Bank__StaleFeedAfterMinutes:?");
}
[Fact] public void README_env_sablonunda_iki_satir_var() // Contain("# OrderDeck__Bank__ExpectationTtlDays=14"), Contain("# OrderDeck__Bank__StaleFeedAfterMinutes=120")
```

- [ ] **Step 2: Koş** — `T(BankOptionsTests|BankQuestionsDeployTests)`. Beklenen: derleme hatası, sonra iddia hataları.
- [ ] **Step 3: Uygula** — compose yorumu: varsayılan SAYI zorunlu (int binder boş dizeyi ayrıştıramaz, `BankOptions` açılışta okunur), `:?` değil `:-` (eksik satır `config -q` kapısını düşürmesin). `ValidateOnStart` EKLEME (banka ayarı lisans sunucusunu düşürmesin); 0 ve eksi güvenli yönde (TTL ≤ 0 ⇒ otomatik onay yok, bayatlık ≤ 0 ⇒ şerit hep görünür), özete yaz. Saklama ayarlarını compose'a EKLEME (politika metnine bağlı). README'de iki satır da `# ` ile başlar (README 155'in biçimi: kapalı satır, değer compose varsayılanından gelir), üstlerinde bir açıklama satırı.
- [ ] **Step 4: Yeniden koş** — aynı süzgeç + `T(ObifinDataControllerGateTests)`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `feat(bank): istek ömrü ve besleme bayatlığı ayarlarını compose'a ekle`

### Task 9: `bank-questions` CLI'ı ve işletim notu

**Files:**
- Create: `LS/Tools/BankQuestionsCommand.cs`
- Modify: `LS/Program.cs` (`person-balance` bloğunun altına aynı kalıpta `args[0] == "bank-questions"`), `deploy/README.md`
- Test: `T/Tools/BankQuestionsCommandTests.cs` (InMemory, `IClassFixture<ApiFactory>`), `T/Tools/BankQuestionsCommandRelationalTests.cs` (SQL Server), `T/Services/Bank/BankQuestionsDeployTests.cs`

**Interfaces:**
- Consumes: `License` bayrakları, `SalesSyncMode`.
- Produces: `public static class BankQuestionsCommand` — `public const bool DecisionLayerReady = false;` (S4 `true` yapar), `public sealed record ToggleArgs(bool Enable, Guid License, bool Apply)`, `public static Task<int> RunAsync(string[] args)`, `public static bool TryParseArgs(string[] args, [NotNullWhen(true)] out ToggleArgs? parsed)`, `public static Task<int> RunEnableAsync(LicenseDbContext db, ToggleArgs args, bool decisionLayerReady, TextWriter output, TextWriter error, CancellationToken ct)`, `public static Task<int> RunDisableAsync(LicenseDbContext db, ToggleArgs args, TextWriter output, TextWriter error, CancellationToken ct)`.
- Kullanım: `bank-questions enable --license <guid> [--apply]`, `bank-questions disable --license <guid> [--apply]`. Ayrıştırma katı: `--license` tam bir kez ve GUID, `--apply` en çok bir kez, başka her şey Usage (stderr) + 2. Çıkış: 0 tamam, kuru koşu ya da yapılacak yok; 1 koşul tutmadı ya da eşzamanlı değişiklik (hiçbir şey yazılmadı); 2 kullanım, bağlantı dizesi yok, bilinmeyen lisans. Zaman biçimi `"u"`. Kişisel veri, makine adı, anahtar basılmaz.

| Durum | Satır (testler birebir sabitler) |
|---|---|
| başlık | `{UYGULAMA\|KURU ÇALIŞTIRMA} — bank-questions {enable\|disable} — {u}` ve `Lisans: {id}` |
| bilinmeyen lisans (stderr, 2, stdout boş) | `Lisans bulunamadı: {id}. Hiçbir şey yazılmadı.` |
| koşullar | `Koşul 1 — satış senkronu modu On: {TUTUYOR\|TUTMUYOR} ({mod})` · `Koşul 2 — karar katmanı bu sürümde: {TUTUYOR\|TUTMUYOR}` |
| bilgi | `BankQuestionsSince: {boş, açılışta yazılacak\|{u}, korunur}; otomatik onay bayrağı: {açık\|kapalı}` |
| sonuç | `Sonuç: bütün koşullar tutuyor` ya da `Sonuç: koşul 1, 2 tutmuyor` |
| kuru enable (koşul tutmasa da 0) | `Kuru çalıştırma: hiçbir şey yazılmadı. Açmak için --apply ekle.` |
| apply, koşul yok (stderr, 1) | `Havale soruları açılmadı: koşul 1, 2 tutmuyor; hiçbir şey yazılmadı.` |
| apply, 0 satır (stderr, 1) | `Lisans bu arada değişti (başka bir enable, kapanan satış senkronu ya da silinen lisans); hiçbir şey yazılmadı, yeniden çalıştır.` |
| zaten açık (0, koşullar basılmaz) | `Havale soruları zaten açık (BankQuestionsSince={u}); hiçbir şey yazılmadı.` |
| açıldı (kayıtlı değer yeniden okunur) | `UYGULANDI: havale soruları açık, BankQuestionsSince={u}` |
| disable kuru / zaten kapalı | `Kuru çalıştırma: havale soruları kapatılacak (BankQuestionsSince={u} korunur); veri işlemi yok. Kapatmak için --apply ekle.` · `Havale soruları zaten kapalı; hiçbir şey yazılmadı.` |
| disable uygulandı / 0 satır (stderr, 1) | `UYGULANDI: havale soruları kapalı; verilmiş kararların geri alınması açık kalır, veri işlemi yapılmadı.` · `Havale soruları bu arada değişti (başka bir disable?); hiçbir şey yazılmadı, yeniden çalıştır.` |

- [ ] **Step 1: Düşen InMemory testlerini yaz** (hepsi `ExecuteUpdate`'e varmadan döner; lisans `SalesCommandTestKit.CreateAsync(factory, SalesSyncMode.On|Off)`)

```csharp
[Theory] public void Gecerli_argumanlar(string line)   // "bank-questions enable --license {L}", "… --apply", "bank-questions disable --apply --license {L}"
[Theory] public void Gecersiz_argumanlar(string line)  // lisanssız, GUID değil, iki --license, iki --apply, bilinmeyen seçenek, "status", "toggle"
[Fact] public async Task Bilinmeyen_lisans_2_stdout_bos()
[Fact] public async Task Kuru_enable_mod_Off_TUTMUYOR_basar_0_doner_yazmaz()
[Fact] public async Task Mod_Off_iken_apply_1_yazmaz()                 // decisionLayerReady: true ⇒ "koşul 1 tutmuyor"
[Fact] public async Task Karar_katmani_hazir_degilken_apply_reddeder() // mod On, decisionLayerReady: false ⇒ 1, "koşul 2 tutmuyor", bayrak kapalı, Since boş
[Fact] public void S2a_surumunde_karar_katmani_kapali() => BankQuestionsCommand.DecisionLayerReady.Should().BeFalse("S4 açar");
[Fact] public async Task Zaten_acik_0_kosul_basmaz()
[Fact] public async Task Disable_kuru_kosuda_yazmaz_kapaliyken_0()
```

- [ ] **Step 2: Koş** — `T(BankQuestionsCommandTests)`. Beklenen: derleme hatası.
- [ ] **Step 3: Uygula** — iskelet `PersonBalanceCommand` (`RunAsync` 68-102, `RunEnableAsync` ve `RunDisableAsync` 252-351). Ayrıştırıcı `PersonBalanceCommand.TryParseToggleArgs` (504-534) gövdesinin kopyası; 447'deki aynı adlı `TryParseArgs` verify'ındır, `TryParseAnyArgs` dağıtımı gerekmez. `RunAsync` yalnız bağlantı dizesini okur ve `DecisionLayerReady`'yi geçirir. Yazım tek koşullu deyim (koşul WHERE'de de):

```csharp
var updated = await db.Licenses
    .Where(l => l.Id == args.License && !l.BankQuestionsEnabled && l.SalesSyncMode == SalesSyncMode.On)
    .ExecuteUpdateAsync(s => s
        .SetProperty(l => l.BankQuestionsEnabled, true)
        .SetProperty(l => l.BankQuestionsSince, l => l.BankQuestionsSince ?? (DateTimeOffset?)now), ct); // COALESCE: kapat/aç sıfırlamaz
```

  `disable`: `WHERE Id AND BankQuestionsEnabled` → `SET BankQuestionsEnabled = 0`; ön koşul ve yapılandırma yok (kill switch), `Since` ve otomatik onay bayrağı değişmez. İki komut da `BankAutoApproveEnabled`'a dokunmaz.
- [ ] **Step 4: SQL Server testlerini yaz** (`BeforeNonQueryHook`, `when` = `[BankQuestionsEnabled]`; desen `PersonBalanceToggleCommandRelationalTests`, kanca orada `private` iç sınıftır: 206-223 kopyalanır; hepsi `decisionLayerReady: true`): `Apply_enable_bayragi_ve_Since_yazar_ikincisi_etkisiz`; `Kapat_ac_BankQuestionsSince_korur` (ilk açılış anı geri tarihlenir, disable + enable sonrası aynı); `Rakip_enable_once_yazarsa_1_rakibin_Since_i_kalir`; `UPDATEten_hemen_once_mod_Off_olursa_1_yazmaz`; `Rakip_disable_1`; `Enable_ve_disable_otomatik_onay_bayragina_dokunmaz`.
- [ ] **Step 5: README** — `BankQuestionsDeployTests.README_komutlari_ve_geri_alma_uyarisi_var`: README `bank-questions enable --license`, `bank-questions disable --license`, `Banka Faz 2 S2a tabanı` ve aşağıdaki imha kaydı sorgusunu içerir. Geri alma bölümüne (Ortak bakiye S2 bloğunun altına) iki blok:
  - **UYARI — Banka Faz 2 S2a tabanı:** göç yalnız genişletir, otomatik geri alma güvenli (eski imajın INSERT'i DB varsayılanına düşer, dar IBAN tekili yerinde). `OrderDeck.LicenseServer/Tools/BankQuestionsCommand.cs` içermeyen imajda `bank-questions` koşma: tanınmayan komut ikinci bir sunucu süreci başlatır. O imajdaki birleştirme, bağ kesme ve shopper silmesi `Payments.WpfCustomerId`'yi bilmez, pencerenin iki kalıntısı olur. Silinen kişinin dekontu eski müşterisine bağlı kalır: ileri dönünce o pencerede silinenlerin silmesi yeniden uygulanır. Liste imha kaydından salt okunur sorguyla çıkar: `SELECT DISTINCT SubjectId FROM DataDestructionRecords WHERE [Trigger] = 'shopper-purge' AND OccurredAt >= '<geri alma anı>'` (deploy geri alması veritabanını geri yüklemez, tablo pencerenin satırlarını eksiksiz taşır; "Silmeleri yeniden uygula" 4. adımın özet e-postaları yedek kaynaktır, o yol yedekten dönüş içindir). Eski talep işlenmiş göründüğü için (`HandledAt` dolu) yeni talep satırıyla. Telefon kanıtı düşen bağlantının dekontu geçici kopyada kalır ve yanlış müşteriye çözülür: elle adımı yok. İkisini de S2b göçü bağdan yeniden türeterek kapatır; o zamana dek sütunu okuyan akış yoktur. Blok, kalıntıyı sayan (1) ve (3) sorgularını da taşır (PR gövdesindekiler): ileri dönüşte 0'dan büyükse kalıntı S2b'yi bekler, S2b'den sonra ikisi de 0 olmalı.
  - **Havale soruları:** kuru koşu → `--apply`, çıkış kodları, yayın penceresi dışında, `disable` kill switch; pilot kapıları (tasarım §14) listesi; "S2a'da `enable --apply` hep reddeder" (cümleyi S4 kaldırır).
- [ ] **Step 6: Yeniden koş** — `T(BankQuestionsCommandTests|BankQuestionsDeployTests)` geçer; `B` 0 hata.
- [ ] **Step 7: Commit** — `feat(bank): bank-questions enable/disable komutu ve işletim notu`

### Task 10: Son doğrulama

**Files:** değişiklik yok (bulgu çıkarsa ilgili görevin dosyası, ayrı commit). **Interfaces:** Consumes: Görev 1-9. Produces: PR'a hazır dal (push ve PR sahibin onayıyla).

- [ ] **Step 1: InMemory tam koşu** — `T(x)` komutu süzgeçsiz. Beklenen: 0 hata. Yük altında düşen test varsa tek başına yeniden koş, sonucu yaz.
- [ ] **Step 2: Model ve göç eşliği** — Görev 5 Step 5'in `has-pending-model-changes` komutu "No changes".
- [ ] **Step 3: Derleme** — `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.sln --nologo -c Release`. Beklenen: 0 hata, yeni uyarı yok (WPF projeleri değişmedi).
- [ ] **Step 4: Davranış taraması** — `cd <WT> && git diff --stat a9729534..HEAD -- OrderDeck.LicenseServer/Services/Bank OrderDeck.LicenseServer/Services/ShopperPayments OrderDeck.LicenseServer/Controllers/Panel OrderDeck.LicenseServer/Controllers/Customers OrderDeck.App OrderDeck.Core Extension`: yalnız `Services/Bank/BankOptions.cs`. `cd <WT> && git grep -nE "WouldAutoApprove|PaymentExpectationAllocations|PaymentShipmentSteps|BankDecisionEvents|BankIbanStates|BankQuestionsEnabled" -- 'OrderDeck.LicenseServer/*.cs'`: yalnız Domain, `LicenseDbContext`, göç, `BankQuestionsCommand`.
- [ ] **Step 5: SQL Server kanıtı** — Docker açılırsa yerelde, yoksa CI'da şu sınıflar yeşil olmadan PR hazır sayılmaz: `BankDecisionsS2aMigrationTests`, `BankDecisionsS2aMigrationExpressTests`, `CustomerIdentityMergerRelationalTests`, `CustomerIdentityMergeJobTests`, `WpfCustomerIdentityRaceTests`, `MergeCustomerIdentitiesRelationalTests`, `CustomerPurgeRelationalTests`, `BankQuestionsCommandRelationalTests`, `EnsureCreated` kullanan dört banka sınıfı (`IbanReevaluationRelationalTests`, `PaymentMatcherConcurrencyTests`, `PaymentMatchReconcilerConcurrencyTests`, `ObifinConnectionServiceConcurrencyTests`: 1785 ve bozuk filtre burada da patlar).

**PR gövdesine.** Sahibe bildirim: `enable`'a tasarımda (§2.7, §14) olmayan ikinci koşul eklendi (`DecisionLayerReady`, S4 açar); tasarımdan ayrılan öbür yerler "Kararlar"da. `RunAsync` sabiti geçirir, bu testle ölçülmüyor: `RunAsync(string[])`'i yalnız `Program.cs` çağırır, testler `RunEnableAsync`'e parametreyi doğrudan verir (emsal `PersonBalanceCommand.RunAsync` de sınanmıyor). S4 sabiti çevirirken o satıra bakar ("Sonraki PR'lara bırakılan", S4). Kanıtı düşen bağla kopyadan taşınan dekont, shopper'ın asıl kayda yaşayan başka bir bağı olsa da müşterisiz kalır (türetme kuralı orada asıl kaydı verirdi); hata yönü güvenli (§5-6 müşterisi bilinmeyen sayar), S2a'da sütunu okuyan yok, S2b göçü yeniden türetir, S2b'den sonra bu boşaltma kalıcıdır. Bilinen yan etki: `Payment.Status` jeton olduğu için müşteri senkronunun oku-kaydet penceresinde karara bağlanan dekont artık bir yeniden deneme doğurur; üst üste iki çakışmada 409 (istemci yeniden yollar), sonuç değişmez (Review Focus 3). Sorgular salt okunur, sahip koşar:
- **Merge'den önce** satır sayıları: `SELECT (SELECT COUNT(*) FROM Payments), (SELECT COUNT(*) FROM PaymentMatches), (SELECT COUNT(*) FROM CustomerIbanMemories), (SELECT COUNT(*) FROM ShopperBroadcasterLinks)` (göç bütçesi ≈ 30 sn; Görev 5 ölçümüyle karşılaştır).
- **Deploy sonrası:** `SELECT COUNT(*) FROM Licenses WHERE BankQuestionsEnabled = 1` = 0; beş yeni tablo 0 satır; `SELECT name, is_unique FROM sys.indexes WHERE object_id = OBJECT_ID('CustomerIbanMemories')` iki IBAN tekilini gösterir; `SELECT COUNT(*) FROM Payments WHERE ShopperId IS NOT NULL AND WpfCustomerId IS NULL` (bağsız dekont, bilgi); konteynerde `printenv OrderDeck__Bank__ExpectationTtlDays` = 14; `bank-questions enable --license <id>` kuru koşusu "Koşul 2 … TUTMUYOR" basar.
- **Yanlış bağ, üçü de 0 beklenir** (geri alma yaşandıysa (1) ve (3) ileri dönüşte yeniden koşulur; 0'dan büyükse kalıntıdır, S2b göçü kapatır): (1) kopyaya bağlı dekont `SELECT COUNT(*) FROM Payments p JOIN WpfCustomerProjections c ON c.Id = p.WpfCustomerId WHERE c.MergedIntoId IS NOT NULL`; (2) başka lisansın müşterisine bağlı dekont `SELECT COUNT(*) FROM Payments p JOIN WpfCustomerProjections c ON c.Id = p.WpfCustomerId WHERE c.LicenseId <> p.LicenseId`; (3) müşterili bağlantısı kalmamış dolu dekont `SELECT COUNT(*) FROM Payments p WHERE p.WpfCustomerId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ShopperBroadcasterLinks l WHERE l.ShopperId = p.ShopperId AND l.LicenseId = p.LicenseId AND l.WpfCustomerId IS NOT NULL)`.
- **Kopyada kalmış IBAN bağı** (bilgi; göç bu sayıyı değiştirmez, S2b eşleşme kararıyla birlikte taşır, Karar 5): `SELECT COUNT(*) FROM CustomerIbanMemories m JOIN WpfCustomerProjections c ON c.Id = m.WpfCustomerId AND c.LicenseId = m.LicenseId WHERE c.MergedIntoId IS NOT NULL`.

**PR gövdesine: plandan sapan testler.** Hepsi güçlendirme; plandaki ad ve sıranın hepsi duruyor.
- Görev 6: `Son_dogrulama_ikisinin_en_yenisi_olur` dört durumlu `[Theory]` (plan `[Fact]`: kalan 10, düşen 2 gün önce): kalanınki yeni, kalanınki boş (başlangıcı `CreatedAt`) ve düşeninki o başlangıçtan eski durumlar da sınanır. Plan dışı `Kalan_bag_yasa_sonra_Id_ye_gore_secilir_BankConfirm_kalana_gecer` (beş durumlu `[Theory]`): `PreferBond` sırası (eski `CreatedAt`, eşitse küçük Id, iki yönde; kaynak seçmez), kalanın `BankConfirm`'e yükselmesi ve `CreatedAt`'inin, öğreten havalesinin ve maskesinin değişmemesi.
- Görev 7: `Kanitsiz_gecici_baglantinin_dekontu_musterisiz_kalir` `LinksUnbound` 2 bekler (plan 1): aynı shopper'a etkin ikinci bir bağ eklendi, ikisi de düşer. Plan dışı `Devralmada_ayrilmis_baglantinin_dekontu_bosalir_etkin_baglantinin_baska_kayittaki_dekontu_kalir` (InMemory) ve `Kanitli_gecici_baglantinin_dekontu_asil_kayda_gecer` (SQL Server).
- Görev 9: `Gecerli_argumanlar(string, bool, bool)` (plan `string`): ayrıştırılan kayıt da sabitlenir. `Mod_Off_iken_apply_1_yazmaz` iki durumlu `[Theory]` (plan `[Fact]`, yalnız `decisionLayerReady: true`): S2a'nın gerçek hâli (iki koşul birlikte tutmuyor) de sınanır. `Enable_ve_disable_otomatik_onay_bayragina_dokunmaz` iki değerli `[Theory]`: ne kuran ne sıfırlayan bir SET kaçmasın. `Bilinmeyen_lisans_2_stdout_bos` iki komutu da, `Gecersiz_argumanlar` plandakinden fazla biçimi sınar. README'nin S2a bloğu, imajın S2a öncesi olduğunu anlamanın yolunu (`git cat-file -e <sha>:OrderDeck.LicenseServer/Tools/BankQuestionsCommand.cs`) da yazar.
