# Banka Faz 2 S2b (kişi düzeyi eşleştirme) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gölge eşleştiriciyi kişi düzeyine almak (`MatcherVersion = 2`), IBAN'ı kişi kümesi olarak okuyup adayları sıralamak, `Decision ≠ None` kilidini beş yere koymak, ölçümü karar katmanından ve banka onayından süzmek, dekontun müşterisini gönderimde ve yeniden bağlamada kanıt kuralıyla yazmak, kopyada kalmış IBAN bağını onu gösteren eşleşme satırlarıyla birlikte asıl kayda taşımak, bağlantısı kopyada kalmış kişiyi çözmek; S3b'nin yazacağı banka satırını gap, `payments/sync`, panel ve gölge sıfırlamasına karşı önceden korumak; TL/TRY dışı havaleyi dışlamak. PR başlığı `fix(bank): …` (tasarım §15, S2b satırı).

**Architecture:** Okuyucu önce. Bu PR'da prod'da hiçbir satır `Decision ≠ None` olmaz, banka satırı (`Source = Bank`) doğmaz: kilitler, banka satırı süzgeçleri ve sıfırlama reddi uykudadır, testleri tohumla yazılır; S3b geri alınırsa S3a imajı (S2b'yi içerir) S3b'nin verisiyle çalışır. Şema değişmez; tek göç yalnız veri yazar (iki `Sql()`) ve bir kez koşar, S2a imajı aynı şemada ve bu veriyle çalışır. Gölgede değişen dört davranış: (1) kişi düzeyi öneri, satırında `MatcherVersion = 2`; (2) TL/TRY dışı hareket dışlanır (eşleştirici satırında `excluded:currency`; dekont adaylığı ve gap sürümsüz değişir, payı merge öncesi sorguda); (3) kopyada kalmış bağlantı asıl kayda çözülür (bağdaştırma, silme; payı merge öncesi sorguda); (4) onaylı dekontun dört alanı senkronda ezilmez. Boş sürümlü satırların karşılaştırma kuralı değişmez (Karar 4); onların ölçümünü yalnız (2) ve (3) değiştirir, payları sorguyla ölçülür. Kanıt kuralı tek çözücüde (`ShopperCustomerResolver`), zincir bilgisi tek yerde (`CustomerIdResolver.ChainOfAsync`).

**Tech Stack:** ASP.NET Core 10, EF Core 10.0.10 (SQL Server prod Express; InMemory test), xunit + FluentAssertions 7, Testcontainers SQL Server 2022 (CI), LocalDB (yerel).

**Spec:** `docs/superpowers/specs/2026-10-10-banka-havale-onay-design.md` (§2 "Kişi", §2.3, §2.4, §2.5, §3, §4.4, §4.5, §12, §13, §15 S2b satırı ve Testler; sahip kararları §16; S2a'dan gelenler §18.7 satır 1-18). Tasarım dosyası bu PR'da DEĞİŞMEZ; ayrılan ve açık kalan yerler "Kararlar"da, metin değişikliği ayrı `docs(bank)` işinde.

## Global Constraints

- **Yer:** `<WT>` = `bank-s2b` çalışma ağacının mutlak yolu (`git worktree list`), dal `feat/banka-faz2-s2b`; kod = origin/master üstüne #566-#570 (S2a dahil). Her Bash komutu `cd <WT> &&` ile başlar. Başka ağaca ve ana klasöre yazma; `git stash`, push, PR, `amend` yok; `dotnet` süreçlerini öldürme.
- **Depo PUBLIC:** sır, kişi adı, gerçek Id yazılmaz. Test değerleri üretilir: Id `Guid.NewGuid()`, kimlik `$"{prefix}-{Guid.NewGuid():N}"`, telefon `TestPhone.NewE164()`, 64 hex özet `$"{Guid.NewGuid():N}{Guid.NewGuid():N}"`. Literal IBAN, telefon, parola yok.
- **Kaynak düzenleme** yalnız Edit/Write (PowerShell 5.1 UTF-8'i bozar). Göç dosyasını `dotnet ef` üretir. `OrderDeck.LicenseServer` uyarıyı hata sayar.
- **Kişi** (§2): aynı `GroupId`'yi paylaşan asıl kayıtlar (`MergedIntoId` boş); boş ya da yalnız boşluk `GroupId` gruplanmamıştır (`PersonMembership.IsGrouped`); karşılaştırma ham değerde ordinal (kırpma, harf katlaması yok). Anahtar `PaymentPersonKey.ForCanonical(Id, GroupId)`.
- **`MatcherVersion`** (§2.4): boş = satır düzeyi, 2 = kişi düzeyi.
- **Adaylar** (§2.4): önce kullanıcı adının kişisi, sonra kümedekiler, son doğrulaması (`LastConfirmedAt ?? CreatedAt`) en yeni önde; en çok 5; saklanmaz, okunurken hesaplanır; hepsi asıl kayıt.
- **Kilit beş yerde** (§2.4): `PaymentMatcher.MatchOnceAsync`, `PaymentMatchReconciler.LinkAsync`, `CandidatesAsync`'in `taken` kümesi, `ReevaluateSameIbanAsync` ile `ReevaluateUndecidedAsync`, `BankMatchSweepJob` 2. aşaması.
- **Dar IBAN tekili** `(LicenseId, IbanHash)` KALIR (S3b düşürür); çift bağ yalnız InMemory'de ve `SharedIbanSchema` ile.
- **Dışlama** (§3): dışlanan kod ya da para birimi TL/TRY değil (`BankTransaction.Currency`).
- **Kanıt kuralı** (§2.5): birleştirilmiş geçici (`CreatedByShopper`) kopyadan geçen bağlantı yalnız asıl kayıt geçici değil ve `WpfCustomerLinkMatcher.PhoneProves(asıl.Phone, shopper.Phone, shopper.PhoneVerifiedAt)` ise asıl kayda çözülür, değilse müşteri bilinmez (null). SQL türetmesi geçici kopyadan zinciri izlemez (NULL).
- **Banka satırı** (§2.3, §4.4, §13): `payments/sync` onaylı ya da `Source = Bank` satırda `PayerName`, `Amount`, `PaidAt`, `PdfHash`'i ezmez; panel listesi ve araması `Source = Bank` döndürmez; gap yalnız dekont içindir; lisansta `Decision ≠ None` satır ya da `Source = Bank` ödeme varsa gölge sıfırlama reddedilir.
- **Ölçüm** (§4.5): yalnız `Decision ∈ {None, ClosedByReceipt}`; dekont onaylı karar = bağlı ödeme `Source = Receipt`; güncel sürüm sayılır, eski ayrı gösterilir; `MeetsPhase2Threshold` kapı değildir.
- **Dekont satırı:** `Payment.UpdatedAt` ilerlemez (`payments/since` imleci), `Status` (jeton) yazılmaz; müşteri yeniden yazımı yalnız `Source = Receipt`.
- **Kanıt biçimleri sözleşmedir:** `username={key}` ardından son, `;` ya da `,` (KVKK araması, `ShopperPurgeService` 3b); `;reeval` sonda (ölçüm `EndsWith`); `excluded:` öneki ve `iban-memory` alt dizesi (tarama 2. aşaması).
- **Koşu.** `T(x)`: `cd <WT> && ORDERDECK_TEST_SHARD=inmemory:0/1 DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo --filter "FullyQualifiedName~x"` (birden çok ad `|FullyQualifiedName~y`). `S(x)`: aynı komut `ORDERDECK_TEST_SHARD` olmadan, başına `ORDERDECK_TEST_LOCALDB=OrderDeckTest ORDERDECK_TEST_LOCALDB_DIR='<yazılabilir klasör>'`; ön koşul yereldeki LocalDB yaması (depoda yok, fixture'ı `skip-worktree` yapar, commit'e girmez, bitince geri alınır). LocalDB yoksa `B` + CI (SQL Server 2022). `B`: `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo`.
- **SQL Server testi:** `[Collection(SqlServerCollection.Name)]` + `[Trait("Category","Testcontainers")]`. Kırmızı hâli görülmediyse görülmüş gibi yazma.
- **Commit:** görev başına bir, Türkçe emir kipi, gövde nedeni söyler, son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`; yalnız görevin dosyaları `git add`.
- **Yayın:** master merge = prod deploy; imaj otomatik geri alınır, göç alınmaz. Merge yayın penceresi (Paz/Pzt/Çar/Per 20:00–01:00 TR) dışında ve sahibindedir.

## Review Focus

1. **IBAN birden çok kişiye düşünce tahmin yok.** Sırasız tek satır okuması kalkar; küme dışı ya da çok kişili küme öneri üretmez, adaylar belirlenimli sıradadır. Görev 5: `Ortak_IBAN_kullanici_adi_yoksa_oneri_yok_adaylar_son_dogrulamaya_gore`, `SharedIbanMatcherRelationalTests.Iki_kisiye_bagli_ozet_her_kosuda_ayni_sirayla_okunur`.
2. **Karar katmanındaki havaleye hiçbir arka plan yolu dokunmaz.** Biri atlanırsa açık sorunun önerisi yayıncının önünde değişir. Görev 6: beş InMemory kilit testi, `Decision_karsilastirmasi_SQLe_cevrilir`, `Aday_secildikten_sonra_karar_katmanina_giren_hareket_baglanmaz`.
3. **Geçici kopyadan kanıtsız çıkılmaz.** Çözülseydi silme gerçek müşterinin projeksiyonunu siler, kanıtsız kişinin dekontu ona yazılırdı. Görev 2: `Gecici_kopyadan_kanitsiz_cikilmaz` (Görev 7, 9, 10 aynı çözücüyü çağırır).
4. **Göç bir kez koşar: bağ ve karar birlikte, yalnız taşınabilir kopyadan.** Yarım taşıma IBAN talebini, K-6'yı, dekont onayını ve silmeyi bozar (S2a planı Karar 5). Görev 11: `Goc_bag_ve_karari_birlikte_tasir_dekont_musterisini_yeniden_turetir`, `Tasinan_bag_ve_karar_dort_yolda_bulunur`.
5. **Ölçüm kendini doğrulamaz, eski blok bozulmaz.** Banka onayı ya da karar katmanı sayılırsa eşik kendini doğrular; sürüm karışırsa nüfus değişimi isabet sanılır. Görev 8: `Surumler_ayri_sayilir_hareket_sayilari_ortak`, `Karar_katmanindaki_satir_sayilmaz_dekontla_kapanan_sayilir`, `Banka_odemesine_bagli_satir_dekont_onayli_sayilmaz`.

## Kararlar

1. **Öneri satırı** (tasarım açık bırakıyor): kişi bir asıl kaydıyla önerilir. Kullanıcı adında vuran satır; kişide birden çok vuran satır varsa IBAN bağının satırı aralarındaysa o, yoksa en küçük `Id` (`"N"` ordinal). Yalnız IBAN'da kişinin en son doğrulanan bağının asıl kaydı (eşitlikte küçük `Id`). Ad katmanında en küçük `Id`. Belirlenimli olsun diye; platform kırılımı gerçek müşteriden okunur, etkilenmez.
2. **`MatcherVersion = 2` eşleştiricinin her yazımında** (dışlama, belirsizlik, `;reeval` dahil). İnsan kararı (`LinkAsync`, elle eşleme) ve birleştirici dokunmaz: sürüm, notlanan önerinin üreticisidir. Kaldırma öneri alanlarıyla birlikte `null`'a, `SupportedByIbanMemory`'yi `false`'a çeker. Boş sürümlü satırlar toplu yeniden hesaplanmaz (gölge sayaçları bir gecede sıçrardı).
3. **`SupportedByIbanMemory`'yi eşleştirici yazar** (öneri kişisi IBAN kümesinde). Tasarım sütunun anlamını veriyor, yazanını söylemiyor; eşleştirici dışından hesaplanamaz, S3a okur.
4. **Öneri–karar karşılaştırması sürüme bağlı.** `LinkAsync`'in `Status`'u ve doğrulanmış bağ koşulu, elle eşlemenin `Status`'u: `MatcherVersion = 2` satırda kişi düzeyi (aynı kişi anahtarı), boş sürümde satır düzeyi (bugünkü). Kişi düzeyi öneri satır düzeyinde karşılaştırılsaydı IG'ye önerilen havalenin TT dekontu `Contradicted` sayılır, v2'nin çelişkisi sahte şişerdi (§2.4); eski satıra uygulansaydı eski ölçüm bloğu deploy anında anlam değiştirirdi. `LearnIbanAsync`'in aynı kişinin öbür satırındaki bağı (`ConflictOtherCustomer`), `RemainingConfirmationsAsync`, `taken` ve bayat seçim denetimi satır düzeyinde kalır: ikinci bağ dar indeks dururken yazılamaz, yarım kişi düzeyi K-6 geri sarmasını bozar; S3b'nin "dekont etkileşimi (kişi düzeyi)" kalemiyle.
5. **Kanıt biçimleri korunur.** `ambiguous:{n}`, `ambiguous-name:{n}` kişi sayar. İki yeni belirteç: `iban-memory-shared:{n}` (kullanıcı adı yok, küme > 1 kişi; `iban-memory` alt dizesini taşır, tarama bağ silinince satırı yeniden yakalar) ve `excluded:currency`.
6. **Adaylar** `PaymentMatcher.CandidatePersonsAsync` okumasıdır, saklanmaz; S2b'de tüketicisi yok, ucu S3a. Kullanıcı adı birden çok kişiye düşerse aday yok (§3 `Unmatched`).
7. **Taşınabilir kopya tek kuraldır (göç ve okuyucu):** kopya geçici değil ve silinmemiş; zincir en çok 3 adımda, aynı lisansta silinmemiş bir asıl kayda varıyor; ara adımlar da geçici ya da silinmiş değil. Göç yalnız onu taşır; kişi düzeyi okuyucu kopyadaki bağı yalnız bu kuralla asıl kayda sayar. Tasarım (§2.5) "silinmiş asıl kayda ve silinmiş kopyadan taşımaz" diyor; geçici kopyadan da taşınmaz, çünkü SQL telefon kanıtını sınayamaz ve bağ kanıtsız kişinin havalesinden öğrenilmiş olabilir. Kalan satırlar bugünkü gibidir (eşleştirici görmez, 12 ay saklama, silme kopya Id'siyle bulur); sayısı PR gövdesinde.
8. **Göç `PaymentMatches.UpdatedAt`'i ilerletmez, `Status`'u düzeltmez.** Göç sırasında yazan yok (tek konteyner, açılışta `Migrate()`); ilerleseydi tarama 2. aşaması (`m.UpdatedAt < b.CreatedAt`) bağdan önce hesaplanmış satırı seçmezdi. Bozulma 3'le yazılmış `Contradicted` kalır: düzeltmek ölçümü geçmişe dönük ve sürümle ayrılamaz biçimde değiştirirdi (sayısı PR gövdesinde).
9. **Bağlantı çözücüsü statik ve tektir** (`Services/ShopperLinking/ShopperCustomerResolver`, kanıt kuralının evi `WpfCustomerLinkMatcher` yanında; DI ve kurucular değişmez). Kanıtsız geçici kopyada sonuç kopya Id'si değil null: kopya Id'si yazılsaydı `CustomerIdResolver` okuyucusu onu gerçek müşteriye çözerdi. Silinmiş asıl kayda giden bağlantı bugünkü gibi çözülür (S2a sabitiyle eşlik; okuyucunun silinmiş kayıt kuralı IBAN bağı içindir, §2.5). Bağdaştırıcı null'da bugünkü "bağ yok" yolundadır, gap yazmaz.
10. **Yeniden bağlamada yeniden yazım** (§2.3 açık maddesi, öneri alındı): geriye dönük eşleştirme ve `ResolvePendingLinksAsync`'in iki çağıranı, bağ kaydedildikten SONRA ayrı `SaveChanges`'te shopper'ın o lisanstaki `Source = Receipt` dekontlarını türetme kuralıyla yazar; `Status` jetonu çakışırsa bir kez taze okumayla yeniden, yine düşerse uyarı logu (yanıt değişmez). Aynı kayda konmaz: senkronun yakalayıcısı bağlamayı da düşürürdü. Katılma yazmaz: geçici satır açan katılma gerçek müşterinin eski dekontlarını yeni satıra taşırdı.
11. **Gönderim yarışı kapatılmaz.** Çözüm ile kayıt arasında biten birleştirmenin bıraktığı Id ya kopyadır (okuyucu zinciri izler) ya geçici kopyadır (okuyucu kanıt ister): S3a okuyucuları kanıt kurallı çözücüyü kullanır (§18.7-18). Kayıttan sonra yeniden çözüm adımı eklenmedi.
12. **Banka satırı süzgeçleri "banka olduğu bilinmedikçe dekont"tur.** Gap ve ölçüm `Source ≠ Receipt` olduğu bilinen ödemeyi dışlar; ödeme satırı bulunamayan bağ bugünkü gibi dekont sayılır (bağ FK'sız; var olan testler bağlanmamış Id'yle kurar). Panel `Source = Receipt` (izin listesi). `payments/sync` yalnız tasarımın saydığı dört alanı korur; `ShipmentDirective` ve `UpdatedAt` bugünkü gibi.
13. **Sıfırlama reddi** kimlik değişince izin denetiminden ÖNCE ve silmenin yeniden denemesinde bir kez daha sorulur; ayrı istisna (`ShadowResetRefusedException`), onay isteyen istisnadan türemez (sayfa "kutuyu işaretleyin" demesin, sayfa kodu değişmez).
14. **Notlardan alınmayanlar:** admin elle eşleme ve kaldırma reddi (tasarım §15'te S3b "admin reddi"); `TryResolveGapOnceAsync` ve `PaymentMatchReconcileJob`'a ek `Source` denetimi (gap yalnız `ReturnPaymentsToGapsAsync` ve dekont onayından doğar, onay işi yalnız dekontta zamanlanır); gönderimde açık `Source = Receipt` (alan başlangıcı zaten öyle); `payments/sync`'te banka satırını bütün alanlarıyla atlamak; ölçümde karar katmanı satır sayısı (S3c).

**Sonraki PR'lara bırakılan (yazma):**
- **S3a:** kilit `Unmatched`'ı da kapsar; §3'ün `Unmatched → Awaiting` yeniden değerlendirmesi kilidi delmeden ayrı yoldan. `CandidatePersonsAsync`'in ucu, `IbanShared`/`IbanOtherCustomer` nedenleri. `Payment.WpfCustomerId` okuyucuları `ShopperCustomerResolver.ResolveLinkAsync` kuralıyla çözer, `CustomerIdResolver` ile değil. Karar yazdığı için admin reddinin (§4.4, S3b) S3a'ya çekilmesi planında tartışılır. Sıfırlama denetimi anlık görüntüdür: yüklenmiş bir harekete sonradan yazılan karar satırını hareketin kaskadı jetonsuz siler; değerlendirmenin yazım yolu buna bakar. Birleştiricinin geçici kopyanın banka izini (IBAN bağı, kararlar) kanıtsız taşıması (`CustomerIdentityMerger.RepointReferencesAsync`; birleştirme işi ve devralma onu geçici kopyada da çağırır) Karar 7 ile çelişir: S3a'dan önce değerlendirilir.
- **S3b:** dar indeks düşerken `LearnIbanAsync`, `RemainingConfirmationsAsync`, `taken` ve bayat seçim denetimi kişi düzeyine. `UnbindPaymentsAsync` (`CustomerIdentityMerger` 224-228) ve silmenin 2. adımı (`ShopperPurgeService` 205-213) `Source` süzmez: banka satırı yazılınca insan kararının müşterisini boşaltırlar.
- **S3c:** ölçüm sayfasında karar katmanı satır sayısı. **S4:** purge'ün IBAN hafızasını birleştirilmiş kopyalarla araması (taşınmayan ve S2b'den sonra yarışla doğan kopya bağları).
- **Banka dışı, ayrı iş:** `ShopperBalanceController` 59-70 bağlantıyı `CustomerIdResolver` ile kanıtsız çözer; geçici kopyada kalmış bağlantıda shopper gerçek müşterinin bakiyesini görür.

## Dosya haritası

| Dosya (`LS/` = `OrderDeck.LicenseServer/`, `T/` = `OrderDeck.LicenseServer.Tests/`) | Sorumluluk | Görev |
|---|---|---|
| `LS/Services/CustomerSync/CustomerIdResolver.cs` | zincirin kopya bilgisi | 1 |
| `LS/Services/ShopperLinking/ShopperCustomerResolver.cs` (yeni) | bağlantı → müşteri, kanıt kuralı, dekontun yeniden yazımı | 2, 9 |
| `LS/Services/Bank/PaymentMatcher.cs` | para birimi, kişi düzeyi, IBAN kümesi, adaylar, K1 | 3-6 |
| `LS/Services/Bank/PaymentMatchReconciler.cs` | K2-K4, sürüme bağlı karşılaştırma, çözücü, kaldırma, gap | 6, 7, 12 |
| `LS/Services/Bank/BankMatchSweepJob.cs` | K5 | 6 |
| `LS/Services/Bank/PaymentMatchMetrics.cs`, `LS/Pages/Admin/BankaEslestirme/Index.cshtml(.cs)` | para birimi projeksiyonu, ölçüm süzgeci, sürüm blokları | 3, 8 |
| `LS/Services/ShopperPayments/ShopperPaymentSubmissionService.cs`, `LS/Domain/Payment.cs` | gönderimde dekontun müşterisi | 9 |
| `LS/Controllers/Licenses/LicensesWpfCustomersSyncController.cs`, `LS/Controllers/Shopper/ShopperAuthController.cs` | yeniden bağlamada yeniden yazım | 9 |
| `LS/Services/Shoppers/ShopperPurgeService.cs` | bağlantının asıl kayda çözülmesi | 10 |
| `LS/Data/Migrations/*_BankPersonMatchingS2b*` | veri göçü | 11 |
| `LS/Controllers/Licenses/LicensesPaymentsSyncController.cs`, `LS/Controllers/Panel/PanelPaymentsController.cs`, `PanelSearchController.cs` | banka satırı korumaları | 12 |
| `LS/Services/Bank/ObifinConnectionService.cs` | sıfırlama reddi | 13 |
| `deploy/README.md` | geri alma notu | 14 |
| `T/...` (görevlerde) | testler | 1-14 |

### Task 1: Zincirin kopya bilgisi

**Files:** Modify `LS/Services/CustomerSync/CustomerIdResolver.cs`. Test: `T/Services/CustomerSync/CustomerIdResolverTests.cs` (InMemory).

**Interfaces:**
- Consumes: `WpfCustomerProjection.MergedIntoId`, `CreatedByShopper`, `PurgedAt`.
- Produces: `public readonly record struct CustomerChain(Guid Canonical, bool ViaProvisionalCopy, bool ViaPurgedCopy);` ve `public Task<IReadOnlyDictionary<Guid, CustomerChain>> ChainOfAsync(Guid licenseId, IEnumerable<Guid> ids, CancellationToken ct)`. Döngü `CanonicalOfAsync`'inkiyle aynı (`IgnoreQueryFilters`, en çok `MaxHops`, yalnız aynı lisans); okunan satır `Id, MergedIntoId, CreatedByShopper, PurgedAt`. Bayraklar yalnız İZLENEN kopyalardan (`MergedIntoId`'si takip edilen satır); varılan satırın kendi bayrağı sayılmaz. `CanonicalOfAsync` ve ~20 çağıranı değişmez.

- [ ] **Step 1: Düşen testi yaz**

```csharp
[Fact]
public async Task Zincir_CanonicalOf_ile_ayni_kayda_varir_izledigi_kopyalarin_bayragini_tasir()
{
    // lic: K3→K2→K1→P (K2 geçici kopya), Q→R (Q silinmiş kopya), S (geçici ASIL kayıt), X→Y (Y başka lisansta),
    // Z4→Z3→Z2→Z1→W (4 adım), N (projeksiyonu yok). Hepsi Guid.NewGuid().
    var chains = await resolver.ChainOfAsync(lic, ids, default);
    var canonical = await resolver.CanonicalOfAsync(lic, ids, default);
    ids.Should().OnlyContain(id => chains[id].Canonical == canonical[id]);
    chains[k3].Should().Be(new CustomerChain(p, ViaProvisionalCopy: true, ViaPurgedCopy: false));
    chains[k1].Should().Be(new CustomerChain(p, false, false));
    chains[q].Should().Be(new CustomerChain(r, false, true));
    chains[s].Should().Be(new CustomerChain(s, false, false), "asıl kaydın kendi bayrağı zincir değildir");
    (chains[x].Canonical, chains[z4].Canonical, chains[n].Canonical).Should().Be((y, z1, n));
}
```

- [ ] **Step 2: Koş** — `T(CustomerIdResolverTests)`. Beklenen: derleme hatası (`ChainOfAsync` yok).
- [ ] **Step 3: Uygula** — XML özeti: bayrakların anlamı, kullananlar (Görev 2 çözücüsü ve Görev 5 okuyucusu), zincir kuralının tek yerde kaldığı.
- [ ] **Step 4: Yeniden koş** — `T(CustomerIdResolverTests)`. Beklenen: hepsi geçer.
- [ ] **Step 5: Commit** — `refactor(customers): zincirin geçici ya da silinmiş kopyadan geçtiğini döndür`

### Task 2: Bağlantıdan müşteriye tek çözücü

**Files:** Create `LS/Services/ShopperLinking/ShopperCustomerResolver.cs`. Test: `T/Services/ShopperLinking/ShopperCustomerResolverTests.cs` (InMemory, yeni klasör).

**Interfaces:**
- Consumes: Görev 1 `ChainOfAsync`; `WpfCustomerLinkMatcher.PhoneProves`.
- Produces (`public static class ShopperCustomerResolver`):
  - `Task<Guid?> ResolveLinkAsync(LicenseDbContext db, Guid licenseId, Guid linkCustomerId, string? shopperPhone, DateTimeOffset? phoneVerifiedAt, CancellationToken ct)` — zincir geçici kopyadan geçmiyorsa varılan Id (`CustomerIdResolver` ile aynı, silinmiş asıl kayıt dahil, Karar 9); geçiyorsa varılan satır (`IgnoreQueryFilters`) asıl kayıt, geçici değil ve `PhoneProves(satır.Phone, shopperPhone, phoneVerifiedAt)` ise o, değilse null.
  - `Task<Guid?> PaymentCustomerAsync(LicenseDbContext db, Guid licenseId, Guid? shopperId, CancellationToken ct)` — türetme kuralı (§2.3): shopper×lisansın `WpfCustomerId`'li bağlantılarından `OrderByDescending(JoinedAt).ThenByDescending(Id)` ilki, `AsNoTracking`, `LeftAt`'e bakılmaz; sonra `ResolveLinkAsync` (shopper telefonu yalnız zincir geçici kopyadan geçince okunur). Bağlantı yoksa ya da shopper null ise null.
  - Görev 9: `RederivePaymentsAsync`.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
public enum Proof { Matches, OtherPhone, NotVerified, CanonicalProvisional }
[Theory, InlineData(Proof.Matches, true), InlineData(Proof.OtherPhone, false), InlineData(Proof.NotVerified, false),
 InlineData(Proof.CanonicalProvisional, false)]
public async Task Gecici_kopyadan_kanitsiz_cikilmaz(Proof proof, bool resolves)
// Kp (geçici kopya) → P; bağlantı Kp'de. P.Phone = shopper telefonu (OtherPhone: başka TestPhone.NewE164()); PhoneVerifiedAt
// dolu (NotVerified: boş); CanonicalProvisional: P.CreatedByShopper = true. ResolveLinkAsync ve PaymentCustomerAsync: resolves ? P : null.

[Fact] public async Task Gecici_olmayan_kopya_asil_kayda_cozulur_gecici_asil_kayit_kendisidir()
// K→P (geçici değil): telefon aranmadan P; S (geçici asıl): S; 4 adımlı zincir: CustomerIdResolver'ın vardığı Id.

[Fact] public async Task Dekontun_musterisi_en_yeni_musterili_baglantidan_turetilir()
// etkin bağlantı müşterisiz + eski ayrılmış bağlantı Q'da ⇒ Q; daha yeni ayrılmış bağlantı P'de ⇒ P;
// bağlantı yalnız başka lisansta ⇒ null; shopperId null ⇒ null. JoinedAt eşitliği tohumlanmaz (Guid sırası sağlayıcıya göre).
```

- [ ] **Step 2: Koş** — `T(ShopperCustomerResolverTests)`. Beklenen: derleme hatası.
- [ ] **Step 3: Uygula** — sınıf özeti: kural §2.5'tir ve bağlantıdan müşteriye giden her yolda (bağdaştırma, silme, gönderim, yeniden yazım) bu sınıftan geçilir; kanıt `CustomerIdentityMergeJob`'unkiyle aynıdır (orada farklı yazılsaydı aynı bağlantı birleştirmede beklemeye düşer, burada asıl kayda giderdi); SQL eşi Görev 11'deki göçtür ve kanıtlı geçici kopyada bilerek NULL verir.
- [ ] **Step 4: Yeniden koş** — `T(ShopperCustomerResolverTests)`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `feat(shopper-link): bağlantıdan müşteriye kanıt kurallı tek çözücü`

### Task 3: TL/TRY dışı havale dışlanır

**Files:** Modify `LS/Services/Bank/PaymentMatcher.cs` (`ExclusionReason` 116-123, sınıf özeti), `LS/Services/Bank/PaymentMatchMetrics.cs` (projeksiyon 107-111). Tests: `T/Services/Bank/PaymentMatcherTests.cs` (`Incoming`'e `currency` parametresi), `PaymentMatchReconcilerTests.cs`, `PaymentMatchMetricsTests.cs` (InMemory).

**Interfaces:**
- Produces: `public const string CurrencyExclusion = "currency";` `internal static bool IsTryCurrency(string? currency)` — `Trim()` edilmiş değer `"TL"` ya da `"TRY"` ile `OrdinalIgnoreCase` eşit. `ExclusionReason`'ın son kolu: `!IsTryCurrency(tx.Currency) ? CurrencyExclusion : null` (yön, sıfır, kod önce). Kanıt `excluded:currency`.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Theory, InlineData("TL"), InlineData("TRY"), InlineData("try"), InlineData(" TL")]
public async Task TL_ve_TRY_degerlendirilir(string currency)   // kullanıcı adı açıklamada ⇒ Proposed
[Theory, InlineData("USD"), InlineData("EUR"), InlineData("")]
public async Task TL_disi_para_birimi_dislanir(string currency) // NoProposal, Evidence "excluded:currency", IsExcluded true
// PaymentMatchReconcilerTests
[Fact] public async Task Yabanci_para_birimli_tek_aday_dekonta_baglanmaz_elle_esleme_ogretmez()
// USD hareket dekontun tek tutar/zaman adayı ⇒ gap NoCandidate; ManualMatchAsync ⇒ ManualMatchIbanOutcome.Excluded, bağ yok
// PaymentMatchMetricsTests
[Fact] public async Task Yabanci_para_birimli_hareket_olcumde_dislanir() // Excluded 1; satırı karar sayımına girmez
```

- [ ] **Step 2: Koş** — `T(PaymentMatcherTests|PaymentMatchReconcilerTests|PaymentMatchMetricsTests)`. Beklenen: yeni testler düşer; ölçüm testi projeksiyon `Currency`'yi taşımadığı için ("TL" varsayılır) düşer.
- [ ] **Step 3: Uygula** — kural ve projeksiyona `Currency = t.Currency`. Sınıf özeti: değer `ObifinClient`'tan kırpılmış ama harfi normalleştirilmemiş gelir, boşu "TL" yapılır; boş değer (başka yoldan) dışlanır; dağılım merge'den önce ölçülür (Görev 14).
- [ ] **Step 4: Yeniden koş** — aynı süzgeç + `T(BankMatchSweepJobTests|MatchingSinkTests)`. Beklenen: geçer (var olan testler `"TL"` kurar).
- [ ] **Step 5: Commit** — `fix(bank): TL/TRY dışı havaleyi dışla, ölçüm de para birimini okusun`

### Task 4: Kişi düzeyi eşleştirici: kullanıcı adı, ad, sürüm

**Files:** Modify `LS/Services/Bank/PaymentMatcher.cs` (`Candidate` 214, `CandidatesAsync` 227-240, katman 1 ve 3 kararı 170-209, `Set` 307-311, sınıf özeti). Test: `T/Services/Bank/PaymentMatcherTests.cs` (InMemory; `Customer` yardımcısına `groupId` ve `platform`).

**Interfaces:**
- Consumes: `PaymentPersonKey.ForCanonical`.
- Produces: `public const int PersonLevelVersion = 2;` `Candidate`'e `string PersonKey` (aday sorgusu `GroupId`'yi de okur; yeni SQL yok); `public async Task<bool> SamePersonAsync(Guid licenseId, Guid a, Guid b, CancellationToken ct)` — `a == b` ya da ikisi de aday ve `PersonKey` ordinal eşit; aday değilse satır düzeyi. `Set(...)` `bool supportedByIban` alır, `MatcherVersion = PersonLevelVersion` ve `SupportedByIbanMemory` yazar.
- Karar (kişi = aday satırların `PersonKey` kümesi; IBAN bu görevde bugünkü tek bağdır, kişisine çevrilir):

| Kullanıcı adı | IBAN | Sonuç |
|---|---|---|
| > 1 kişi | — | NoProposal `ambiguous:{kişi}` |
| {P} | P'den başka kişi | NoProposal `conflict:username={key},iban-memory` |
| {P} | P | Proposed, 0,98 `username={key};iban-memory`, destekli |
| {P} | yok | Proposed, 0,90 `username={key}` |
| yok | P | Proposed, 0,85 `iban-memory`, destekli |
| ad {P} / ad > 1 kişi / hiçbiri | — | `name:{n}` 0,50 / `ambiguous-name:{kişi}` / `no-signal` |

  Satır Karar 1'le; `{key}` seçilen satırın anahtarı.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Ayni_kisinin_iki_satiri_ayni_kullanici_adiyla_tek_oneri_ve_surum_2()
// "G-{Guid:N}" ortak IG+TT, aynı kullanıcı adı ⇒ Proposed, 0.90, satır en küçük "N", MatcherVersion 2, SupportedByIbanMemory false
[Fact] public async Task Kullanici_adi_bir_satiri_IBAN_ayni_kisinin_obur_satirini_gosterirse_iki_katman_destekler()
// ⇒ 0.98, "username={key};iban-memory", SupportedByIbanMemory, öneri kullanıcı adının satırı; bağ başka kişide ⇒ conflict biçimi aynen
[Theory, InlineData("G1", "g1"), InlineData("G1", "G1 "), InlineData("  ", "  "), InlineData(null, "")]
public async Task GroupId_ordinal_bos_ya_da_bosluk_gruplanmamis(string? a, string? b) // ⇒ NoProposal "ambiguous:2"
[Fact] public async Task Ad_katmani_ayni_kisinin_iki_satirini_tek_kisi_sayar()          // ⇒ NameAmount 0.50 "name:2"
[Fact] public async Task Her_yazim_surum_2_yazar()  // excluded:CCP, ambiguous:2, no-signal ve ReevaluateAsync (";reeval" sonda) ⇒ MatcherVersion 2
[Fact] public async Task Ayni_kisi_ayni_grup_ya_da_ayni_Id_aday_disi_satir_satir_duzeyi() // SamePersonAsync
```

- [ ] **Step 2: Koş** — `T(PaymentMatcherTests)`. Beklenen: derleme hatası, sonra kişi testleri düşer (`ambiguous:2`, `conflict`).
- [ ] **Step 3: Uygula** — tablo; sınıf özetine kişi tanımı, sürüm ve öneri satırı kuralı.
- [ ] **Step 4: Yeniden koş** — `T(PaymentMatcherTests|PaymentMatchReconcilerTests|BankMatchSweepJobTests)`. Beklenen: geçer; `Birden_fazla_aday_oneri_uretmez` (farklı kişiler) ve `Iban_hafizasi_kullanici_adiyla_celisirse_oneri_yok` (biçim) değişmeden.
- [ ] **Step 5: Commit** — `fix(bank): eşleştirici adayları kişiye toplasın, sürüm 2 yazsın`

### Task 5: IBAN kişi kümesi ve adaylar

**Files:** Modify `LS/Services/Bank/PaymentMatcher.cs` (katman 2, 160-168). Tests: `T/Services/Bank/PaymentMatcherTests.cs` (InMemory; tekil indeks yok, çift bağ tohumlanır), `T/Services/Bank/SharedIbanMatcherRelationalTests.cs` (SQL Server, `EnsureCreated` + `SharedIbanSchema`).

**Interfaces:**
- Consumes: Görev 1 `ChainOfAsync`; Görev 4.
- Produces: `public const int MaxCandidatePersons = 5;` `public Task<IReadOnlyList<Guid>> CandidatePersonsAsync(BankTransaction tx, CancellationToken ct)`; özel `IbanPersonsAsync` → `IReadOnlyList<(string PersonKey, Guid RowId, DateTimeOffset LastConfirmed)>`.
- Kural: `(LicenseId, IbanHash)`'in bütün bağları okunur (`LastConfirmedAt ?? CreatedAt`; sırasız `FirstOrDefault` kalkar). Her bağ `ChainOfAsync` ile çözülür; zincir geçici ya da silinmiş kopyadan geçiyorsa ya da varılan kayıt aday değilse (silinmiş, başka lisans, kırık zincir) sayılmaz (Karar 7). Kişi başına en yeni doğrulamalı bağın asıl kaydı (eşitlikte küçük `Id`); kişiler son doğrulamaya göre azalan, sonra `Id`. Görev 4'ün tablosuna: {P} ∈ küme (küme > 1 dahil) ⇒ 0,98 `username={key};iban-memory`; kullanıcı adı {P}, küme ∌ P ⇒ `conflict:username={key},iban-memory`; kullanıcı adı yok, küme tek kişi ⇒ 0,85; küme > 1 ⇒ NoProposal `iban-memory-shared:{n}`.
- `CandidatePersonsAsync`: dışlanan hareket ⇒ boş; kullanıcı adı > 1 kişi ⇒ boş; önce kullanıcı adının kişisinin satırı (tekse), sonra kümedeki öbür kişiler sırayla; en çok 5.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Ortak_IBAN_kullanici_adinin_kisisini_iceriyorsa_oneri_odur()          // P∈{P,Q}: Proposed P 0.98, destekli; adaylar [P, Q]
[Fact] public async Task Ortak_IBAN_kullanici_adi_yoksa_oneri_yok_adaylar_son_dogrulamaya_gore() // {P 10 gün, Q 2 gün önce} ⇒ "iban-memory-shared:2", [Q, P]
[Fact] public async Task Kullanici_adi_kume_disindaysa_celiski_adaylar_once_kullanici_adinin_kisisi() // P, {Q} ⇒ conflict; [P, Q]
[Fact] public async Task Ayni_kisinin_iki_bagi_tek_kisi_en_yeni_bagin_satiri_onerilir()      // IG ve TT bağı ⇒ 0.85, TT (yeni) satırı
[Fact] public async Task Kopyadaki_bag_asil_kayda_sayilir_gecici_ya_da_silinmis_kopyadaki_sayilmaz()
// K→P (geçici değil, canlı) ⇒ P; Kp (geçici kopya)→P, Ks (silinmiş kopya)→P, silinmiş asıl kaydın bağı, başka lisansın bağı ⇒ "no-signal"
[Fact] public async Task Adaylar_en_cok_bes_kisi_kullanici_adinin_kisisi_ilk()                // 7 kişilik küme ⇒ 5
// SharedIbanMatcherRelationalTests (SQL Server)
[Fact] public async Task Iki_kisiye_bagli_ozet_her_kosuda_ayni_sirayla_okunur() // SharedIbanSchema.ApplyAsync; iki bağ; iki koşu aynı aday listesi ve aynı NoProposal
[Fact] public async Task Dar_tekil_dururken_ikinci_bag_2601()                   // S2b şeması: aynı özete ikinci müşteri ⇒ 2601, dar indeksin adı
```

- [ ] **Step 2: Koş** — `T(PaymentMatcherTests)`. Beklenen: derleme hatası, sonra küme testleri düşer.
- [ ] **Step 3: Uygula** — kural; kanıt değişmeden `username={key};iban-memory` (küme büyüklüğü kanıta girmez); sınıf özetine kümenin dar indeks dururken prod'da tek kişi olduğu, kodun S3b şemasına hazır olduğu.
- [ ] **Step 4: Yeniden koş** — `T(PaymentMatcherTests|PaymentMatchReconcilerTests)`; `S(SharedIbanMatcherRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): IBAN'ı kişi kümesi olarak oku, adayları sırala`

### Task 6: Beş kilit

**Files:** Modify `LS/Services/Bank/PaymentMatcher.cs` (130), `LS/Services/Bank/PaymentMatchReconciler.cs` (439, 398-401, 575-582, 551, sınıf özeti 74-89), `LS/Services/Bank/BankMatchSweepJob.cs` (149-150, sınıf özeti). Tests: `PaymentMatcherTests`, `PaymentMatchReconcilerTests`, `BankMatchSweepJobTests` (InMemory), `T/Services/Bank/BankDecisionLockRelationalTests.cs` (SQL Server, `EnsureCreated`; desen `IbanReevaluationRelationalTests`).

**Interfaces:** Consumes `PaymentMatch.Decision`. Produces: yok (davranış).

| # | Yer | Ek koşul |
|---|---|---|
| K1 | `MatchOnceAsync` insan kararı dönüşü | `\|\| match.Decision != PaymentMatchDecision.None` ⇒ dokunmadan dön |
| K2 | `LinkAsync` bayat seçim | `\|\| match.Decision != None` ⇒ `StaleCandidateException` (yeniden deneme K3 ile eler) |
| K3 | `CandidatesAsync` `taken` | `\|\| m.Decision != PaymentMatchDecision.None` |
| K4a | `ReevaluateSameIbanAsync` seçimi | `&& m.Decision == PaymentMatchDecision.None` |
| K4b | `ReevaluateUndecidedAsync` dönüşü | `&& row.Decision == None` (K1'in döndürdüğü `Proposed` sayılmasın) |
| K5 | tarama 2. aşaması | `&& m.Decision == PaymentMatchDecision.None` |

- [ ] **Step 1: Düşen testleri yaz** (tohum: `Decision = Awaiting`, `Status = Proposed`, öneri X, `UpdatedAt = t0`; her testte satırın öneri, kanıt, `UpdatedAt`, `MatcherVersion`'ı değişmez)

```csharp
[Fact] public async Task Karar_katmanindaki_satir_yeniden_hesaplanmaz()              // K1: MatchAsync ve ReevaluateAsync
[Fact] public async Task Karar_katmanindaki_hareket_dekontun_adayi_olmaz()           // K3: ReconcileApprovalAsync ⇒ gap NoCandidate, PaymentId boş
[Fact] public async Task Elle_esleme_karar_katmanindaki_ayni_IBAN_satirini_yeniden_degerlendirmez() // K4a: Learned, Reevaluated 0
[Fact] public async Task Karar_katmanindaki_satir_yeniden_degerlendirilmis_sayilmaz() // K4b: ReevaluateUndecidedAsync null
[Fact] public async Task Tarama_karar_katmanindaki_satiri_ikinci_asamada_secmez()     // K5: bağdan önce hesaplanmış, yine de dokunulmaz
// BankDecisionLockRelationalTests (SQL Server)
[Fact] public async Task Decision_karsilastirmasi_SQLe_cevrilir() // K3, K4a, K5 sorguları gerçek SQL'de Awaiting satırı eler (Görev 8 ölçümü ekler)
[Fact] public async Task Yeniden_hesap_okuduktan_sonra_karar_katmanina_giren_satira_dokunmaz()
// ConcurrentDecision deseni: eşleştiricinin kaydında ayrı bağlamdan UPDATE Decision = N'Awaiting', UpdatedAt ilerler ⇒ jeton; taze satırda K1
[Fact] public async Task Aday_secildikten_sonra_karar_katmanina_giren_hareket_baglanmaz()
// DbCommandInterceptor yalnız bağdaştırmanın bağlamında: hareketin Id'sini parametre taşıyan ilk PaymentMatches okumasından (LinkAsync)
// önce bir kez aynı UPDATE ⇒ K2 StaleCandidate, yeniden denemede K3 eler ⇒ gap NoCandidate, satırın PaymentId'si boş
```

- [ ] **Step 2: Koş** — `T(PaymentMatcherTests|PaymentMatchReconcilerTests|BankMatchSweepJobTests)`. Beklenen: beş test düşer.
- [ ] **Step 3: Uygula** — tablo; sınıf özetlerine kilidin tasarım §2.4 olduğu, S2b'de prod'da `Decision ≠ None` satır olmadığı (okuyucu önce) ve `Unmatched` istisnasının S3a'da ayrı yoldan geldiği.
- [ ] **Step 4: Yeniden koş** — aynı süzgeç; `S(BankDecisionLockRelationalTests|IbanReevaluationRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): karar katmanındaki havaleyi beş yolda kilitle`

### Task 7: Bağdaştırıcı: sürüme bağlı karşılaştırma, bağlantı çözümü, kaldırma

**Files:** Modify `LS/Services/Bank/PaymentMatchReconciler.cs` (`ResolveWpfCustomerAsync` 379-385, `LinkAsync` 447-451, `ManualMatchOnceAsync` 304, `ReevaluateSameIbanAsync` 594, `UnmatchOnceAsync` 352). Test: `T/Services/Bank/PaymentMatchReconcilerTests.cs` (InMemory).

**Interfaces:**
- Consumes: Görev 2 `PaymentCustomerAsync`; Görev 4 `SamePersonAsync`, `PersonLevelVersion`.
- Produces: özel `Task<bool> SameProposalPersonAsync(PaymentMatch match, Guid customer, CancellationToken ct)` — öneri yoksa false; `Proposed == customer`; değilse yalnız `MatcherVersion == PersonLevelVersion` ise `Matcher.SamePersonAsync` (Karar 4). Kullananlar: `LinkAsync`'in `Status`'u ve doğrulanmış bağ koşulu, elle eşlemenin `Status`'u, yeniden değerlendirme sayacı. `ResolveWpfCustomerAsync` gövdesi `ShopperCustomerResolver.PaymentCustomerAsync(_db, licenseId, shopperId, ct)`. Kaldırma: `MatcherVersion = null; SupportedByIbanMemory = false;`.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Kisi_duzeyi_oneri_ayni_kisinin_obur_satirinin_dekontuyla_dogrulanir_ve_IBAN_ogretir()
// ortak GroupId: A-IG (açıklamadaki kullanıcı adı), A-TT (shopper bağlantısı) ⇒ öneri A-IG v2; onay ⇒ ConfirmedByHuman, Actual A-TT, bağ A-TT'de
[Fact] public async Task Satir_duzeyi_oneri_bugunku_gibi_siniflanir() // aynı kurulum, satır MatcherVersion null ⇒ Contradicted, bağ öğrenilmez
[Theory, InlineData(2, PaymentMatchStatus.ConfirmedByHuman), InlineData(null, PaymentMatchStatus.ManualOnly)]
public async Task Elle_esleme_ayni_kisinin_obur_satirina(int? version, PaymentMatchStatus expected)
[Fact] public async Task Baglantisi_kopyada_kalmis_kisinin_dekontu_asil_kayda_baglanir() // bağlantı K→C (geçici değil), öneri C ⇒ ConfirmedByHuman, Actual C
[Fact] public async Task Kanitsiz_gecici_kopyadaki_baglantinin_dekontu_bagdastirilmaz()   // ne PaymentId ne gap
[Fact] public async Task Kaldirma_surumu_bosaltir_yeniden_hesap_2_yazar()
// açık bağlantıda: kaldırma + yeniden hesap ⇒ 2; durdurulmuş bağlantıda (yeniden hesap koşmaz) ⇒ MatcherVersion null, SupportedByIbanMemory false
[Fact] public async Task Yeniden_degerlendirme_sayaci_ayni_kisinin_obur_satirina_verilen_oneriyi_sayar() // A-TT öğrenilir, eski satır A-IG'ye ⇒ ProposedToCustomer 1
```

- [ ] **Step 2: Koş** — `T(PaymentMatchReconcilerTests)`. Beklenen: yeni testler düşer.
- [ ] **Step 3: Uygula** — sınıf özetine: karşılaştırmanın satırın sürümüne bağlı olduğu, `taken` ve `LearnIbanAsync`'in satır düzeyinde kaldığı (S3b), bağlantının kanıt kurallı çözüldüğü.
- [ ] **Step 4: Yeniden koş** — `T(PaymentMatchReconcilerTests|PaymentMatchReconcileJobTests|MatchingSinkTests|AdminBankaEslestirmePageTests)`; `S(PaymentMatchReconcilerConcurrencyTests|IbanReevaluationRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): bağdaştırıcı kişi düzeyi öneriyi kişiyle karşılaştırsın, bağlantıyı asıl kayda çözsün`

### Task 8: Ölçüm süzgeci ve sürüm blokları

**Files:** Modify `LS/Services/Bank/PaymentMatchMetrics.cs` (`ComputeAsync`, satır sorgusu 114-126, özetler), `LS/Pages/Admin/BankaEslestirme/Index.cshtml.cs` (174), `Index.cshtml` (39-72). Tests: `T/Services/Bank/PaymentMatchMetricsTests.cs` (InMemory; `M` yardımcısına sürüm, varsayılan 2), `T/Pages/Admin/AdminBankaEslestirmePageTests.cs`, `IbanReevaluationRelationalTests.cs:141` (çağrı), `BankDecisionLockRelationalTests` (SQL Server).

**Interfaces:**
- Produces: `public Task<PaymentMatchSummary> ComputeAsync(Guid licenseId, int days, int? matcherVersion, CancellationToken ct)` — eşleşme satırları `m.MatcherVersion == matcherVersion` (null = satır düzeyi) ve `(m.Decision == PaymentMatchDecision.None || m.Decision == PaymentMatchDecision.ClosedByReceipt)`; `HasReceipt = m.PaymentId != null && !_db.Payments.Any(p => p.Id == m.PaymentId && p.Source != PaymentSource.Receipt)` (Karar 12). Hareket düzeyi alanlar (`Incoming`, `Excluded`, gecikme, `OpenGaps`) sürümsüz. Sayfa: `Summary` (2) ve `LegacySummary` (null); `data-box='metrics'` güncel blok, `data-box='metrics-legacy'` "Satır düzeyi (eski) eşleştirici" karar sayıları; Faz 2 satırı yalnız güncel blokta ve "bilgi; otomatik onayın kapısı değil (§16-6)" der.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Surumler_ayri_sayilir_hareket_sayilari_ortak()
// v2: 2 Confirmed (biri dekontlu), 1 Proposed; v1: 1 Contradicted, 1 NoProposal ⇒ (…, 2): Confirmed 2, Pending 1, Contradicted 0;
// (…, null): Contradicted 1, NoProposal 1; Incoming, Excluded, OpenGaps, LagMedian iki çağrıda aynı
[Fact] public async Task Karar_katmanindaki_satir_sayilmaz_dekontla_kapanan_sayilir() // Approved, AutoApproved, Awaiting, Excluded, Dismissed yok; ClosedByReceipt var
[Fact] public async Task Banka_odemesine_bagli_satir_dekont_onayli_sayilmaz()
// Source = Bank ödemeye bağlı ConfirmedByHuman ⇒ ReceiptConfirmed 0, PageConfirmed 1; bağlanmamış PaymentId ⇒ ReceiptConfirmed 1 (bugünkü gibi)
// AdminBankaEslestirmePageTests: Olcum_kutusu_… tohumu MatcherVersion = 2; yeni
[Fact] public async Task Eski_eslestirici_kutusu_satir_duzeyi_kararlari_ayri_gosterir() // v1 satırı yalnız metrics-legacy'de; Faz 2 satırı "kapısı değil" der
// BankDecisionLockRelationalTests
[Fact] public async Task Olcum_suzgeci_SQLe_cevrilir() // Decision dizesi, NOT EXISTS ve MatcherVersion IS NULL gerçek SQL'de
```

- [ ] **Step 2: Koş** — `T(PaymentMatchMetricsTests|AdminBankaEslestirmePageTests)`. Beklenen: derleme hatası, sonra iddia hataları.
- [ ] **Step 3: Uygula** — var olan `ComputeAsync` çağrıları sürüm alır (`PaymentMatchMetricsTests`'in dokuzu 2, tohum yardımcısı 2 yazar; `IbanReevaluationRelationalTests:141` 2, satırı eşleştirici yazdı; sayfa iki çağrı); `PaymentMatchSummary` özetine süzgecin gerekçesi (§4.5: süzülmezse eşik kendini doğrular).
- [ ] **Step 4: Yeniden koş** — aynı süzgeç; `S(BankDecisionLockRelationalTests|IbanReevaluationRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): gölge ölçümü karar katmanını ve banka onayını saymasın, sürümleri ayırsın`

### Task 9: Dekontun müşterisi: gönderim ve yeniden bağlama

**Files:** Modify `LS/Services/ShopperPayments/ShopperPaymentSubmissionService.cs` (245-266), `LS/Services/ShopperLinking/ShopperCustomerResolver.cs`, `LS/Controllers/Licenses/LicensesWpfCustomersSyncController.cs` (345-388), `LS/Controllers/Shopper/ShopperAuthController.cs` (`ResolvePendingLinksAsync` 769-794, çağıranlar 497/500 ve 591/598), `LS/Domain/Payment.cs` (93-95 özeti). Tests: `T/Services/ShopperPayments/ShopperPaymentSubmissionServiceTests.cs`, `T/Controllers/Licenses/LicensesWpfCustomersSyncControllerTests.cs`, `T/Controllers/Shopper/ShopperAuthRegisterTests.cs`, `ShopperAuthResetPasswordTests.cs` (InMemory), `T/Services/ShopperLinking/PaymentRederivationRelationalTests.cs` (SQL Server).

**Interfaces:**
- Consumes: Görev 2.
- Produces: `public static Task<int> RederivePaymentsAsync(LicenseDbContext db, Guid shopperId, IReadOnlyCollection<Guid> licenseIds, ILogger log, CancellationToken ct)` — lisans başına `PaymentCustomerAsync`; `Payments` (izlenen) `LicenseId`, `ShopperId`, `Source == Receipt` ve değeri farklı olanlara yazar; `SaveChangesAsync`; `DbUpdateConcurrencyException`'da yalnız bu `Payment` girdileri ayrılır, bir kez baştan; ikincide uyarı logu (yalnız Id'ler) ve 0. Değişen satır sayısını döner. `ResolvePendingLinksAsync` bağladığı lisansları döner (`List<Guid>`). Gönderim: `WpfCustomerId = await ShopperCustomerResolver.PaymentCustomerAsync(_db, input.LicenseId, input.ShopperId, ct)`.
- Yerleşim (Karar 10): geriye dönük eşleştirmede bağlanan bağlantıların shopper'ları toplanır, `retroactiveMatches` kaydı BAŞARILIYSA her biri için `RederivePaymentsAsync(…, [licenseId], _logger, ct)`; telefon doğrulaması ve parola sıfırlamada kayıt (ya da `DeletedWonAsync == false`) geçince, bağlanan lisans varsa çağrılır; silinmiş hesap yolunda çağrılmaz.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Gonderim_dekontun_musterisini_turetme_kuraliyla_yazar() // bağlantı K→P ⇒ P; UpdatedAt == CreatedAt, Source Receipt
[Fact] public async Task Gonderim_kanitsiz_gecici_kopyada_musterisiz_birakir()  // Kp→P kanıtsız ⇒ null; kanıtlı ⇒ P; bağlantısız ⇒ null
[Fact] public async Task Geriye_donuk_eslestirme_shopperin_o_lisanstaki_dekontlarini_yeniden_turetir()
// bekleyen bağlantı; aynı shopper'ın iki boş dekontu (biri Approved), başka lisanstaki dekontu, Source = Bank satırı; başka shopper'ın
// dekontu ⇒ senkron telefonla bağlar: iki dekont P, Status ve UpdatedAt aynı; öbürleri değişmez; RetroactiveMatches 1
[Fact] public async Task Telefon_dogrulamasi_baglanan_lisansin_dekontlarini_doldurur() // ShopperAuthRegisterTests:209 deseni
[Fact] public async Task Parola_sifirlama_baglanan_lisansin_dekontlarini_doldurur()    // ShopperAuthResetPasswordTests:108 deseni
// PaymentRederivationRelationalTests (SQL Server)
[Fact] public async Task Yeniden_yazim_okuduktan_sonra_onaylanan_dekont_bir_yeniden_denemeyle_dolar()
// SaveChangesInterceptor ilk kayıtta ayrı bağlamdan ExecuteUpdate Status = Approved ⇒ dönüş 1, WpfCustomerId P, Status Approved, UpdatedAt aynı
```

- [ ] **Step 2: Koş** — `T(ShopperPaymentSubmissionServiceTests|LicensesWpfCustomersSyncControllerTests|ShopperAuthRegisterTests|ShopperAuthResetPasswordTests)`. Beklenen: yeni testler düşer.
- [ ] **Step 3: Uygula** — `Payment.WpfCustomerId` özeti: gönderim yazar, iki yazıcı yeniden yazar, katılma yazmaz, okuyucu kanıt kuralıyla çözer.
- [ ] **Step 4: Yeniden koş** — aynı süzgeç + `T(PaymentSubmitConcurrencyTests|ShopperIdentityTakeoverTests|ShopperBroadcastersJoinTests)`; `S(PaymentRederivationRelationalTests|WpfCustomerIdentityRaceTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): dekontun müşterisini gönderimde yaz, yeniden bağlamada yeniden türet`

### Task 10: Shopper silmesi bağlantıyı asıl kayda çözer

**Files:** Modify `LS/Services/Shoppers/ShopperPurgeService.cs` (adım 3 `projectionKeys` 233-263, 3b yorumu 296-316). Test: `T/Services/Shoppers/ShopperPurgeServiceTests.cs` (InMemory).

**Interfaces:**
- Consumes: Görev 2 `ResolveLinkAsync` (`shopper.Phone`, `PhoneVerifiedAt`; adım 6 telefonu boşaltmadan önce).
- Kural: her bağlantı (lisans, K) için anahtarlar {K} ∪ {çözülen C}; K her zaman kalır (taşınmamış satırlar, geçici kopyanınkiler). `MarkPurged` varsayılan süzgeçli projeksiyonlardan (C dahil, kopya K hariç); IBAN ve eşleşme aramaları, `purgedCustomers`, geç bağ taraması, 3a ve 3c bu kümeyle. Kanıtsız geçici kopyada C eklenmez: sahte hesabın silmesi gerçek müşterinin bağını ve kararını silerdi.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Kopyada_kalmis_baglantinin_asil_kaydi_silinir_bagi_ve_karari_bulunur()
// K (geçici değil) → C, bağlantı K'da: C MarkPurged; C'deki bağ silinir; Proposed/Actual C kararları boşalır; K'daki bağ da silinir
[Fact] public async Task Kanitli_gecici_kopyadaki_baglanti_asil_kayda_cozulur()
[Fact] public async Task Kanitsiz_gecici_kopyadaki_baglanti_gercek_musteriye_dokunmaz() // C MarkPurged değil, C'nin bağı ve kararı yerinde; K'nınkiler gider
```

- [ ] **Step 2: Koş** — `T(ShopperPurgeServiceTests)`. Beklenen: üçü düşer (bugün kopyadaki bağlantıda hiçbir projeksiyon silinmiyor).
- [ ] **Step 3: Uygula** — 3b yorumuna kural ve tasarım §2.5/§12 dayanağı.
- [ ] **Step 4: Yeniden koş** — `T(ShopperPurgeServiceTests|PaymentMatchReconcilerTests|LicensesWpfCustomersSyncControllerTests)`; `S(ShopperPurgeServiceConcurrencyTests|SalesFeedKvkkRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(kvkk): shopper silmesi kopyada kalmış bağlantıyı kanıt kuralıyla asıl kayda çözsün`

### Task 11: Göç `BankPersonMatchingS2b`

**Files:** Create (üretilir) `LS/Data/Migrations/<zaman>_BankPersonMatchingS2b.cs`, `.Designer.cs` (`LicenseDbContextModelSnapshot.cs` içerik olarak değişmemeli). Test: `T/Data/BankPersonMatchingS2bMigrationTests.cs` — `…ScriptTests` (Docker'sız), `…MigrationTests` ve `…ExpressTests` (SQL Server), `internal static BankPersonMatchingS2bMigrationScenario`.

**Interfaces:**
- Consumes: S2a şeması (model değişmez); Görev 2, 5, 7, 10 (eşlik ve hizmet iddiaları).
- Produces: `public const string MoveCopyBonds`, `public const string RederivePaymentCustomers`; `Up` sırayla iki `migrationBuilder.Sql(...)`, `Down` boş (göç geri alınmaz). Sınıf özeti: yalnız veri; taşınabilir kopya kuralı (Karar 7) okuyucuyla aynı; `UpdatedAt` ve `Status` yazılmaz (Karar 8); dar tekil dururken çift doğamaz (`(LicenseId, IbanHash)` değişmez, tek satır); türetme S2a sabitinden iki noktada ayrılır.

- [ ] **Step 1: Docker'sız düşen testi ve senaryo iskeletini yaz** (desen `BankDecisionsS2aMigrationScenario.Migrations`)

```csharp
public sealed class BankPersonMatchingS2bMigrationScriptTests
{
    private static readonly IReadOnlyList<MigrationOperation> Up = new BankPersonMatchingS2b().UpOperations;
    [Fact]
    public void Yalniz_veri_yazar_iki_deyim_jeton_ve_durum_yok()
    {
        Up.Should().HaveCount(2).And.AllBeOfType<SqlOperation>("şema değişmez: S2a imajı aynı şemada çalışır");
        Up.Cast<SqlOperation>().Select(s => s.Sql).Should().Equal(BankPersonMatchingS2b.MoveCopyBonds, BankPersonMatchingS2b.RederivePaymentCustomers);
        Up.Cast<SqlOperation>().Should().OnlyContain(s => !Regex.IsMatch(s.Sql, @"UpdatedAt|\[Status\]|\bDELETE\b|\bDROP\b|\bONLINE\b"));
        new BankPersonMatchingS2b().DownOperations.Should().BeEmpty("göç geri alınmaz");
    }
    [Fact]
    public void Turetme_gecici_kopyadan_izlemez_yalniz_dekonta_yazar() =>
        BankPersonMatchingS2b.RederivePaymentCustomers.Should().Contain("[CreatedByShopper] = 1")
            .And.Contain("p.[Source] = N'Receipt'").And.Contain("ORDER BY x.[JoinedAt] DESC, x.[Id] DESC");
}
```

- [ ] **Step 2: Koş** — `T(BankPersonMatchingS2bMigrationScriptTests)`. Beklenen: derleme hatası (`BankPersonMatchingS2b` yok).
- [ ] **Step 3: Göçü üret** — `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add BankPersonMatchingS2b --project OrderDeck.LicenseServer --startup-project OrderDeck.LicenseServer --context LicenseDbContext --output-dir Data/Migrations`. Model değişmediği için `Up`/`Down` boş üretilmeli; değilse model kaymıştır: `migrations remove --force`, nedeni bul.
- [ ] **Step 4: İki deyimi ekle** (`Up`'ta bu sırayla; DML, DDL yok):

```sql
-- MoveCopyBonds: kopyada kalmış IBAN bağı ve aynı kopyayı gösteren eşleşmeler AYNI eşlemeyle asıl kayda (Karar 7).
DECLARE @CopyMap TABLE (CopyId uniqueidentifier NOT NULL PRIMARY KEY, LicenseId uniqueidentifier NOT NULL, CanonicalId uniqueidentifier NOT NULL);
INSERT INTO @CopyMap (CopyId, LicenseId, CanonicalId)
SELECT k.[Id], k.[LicenseId], c.[Id]
FROM [WpfCustomerProjections] k
JOIN [WpfCustomerProjections] a1 ON a1.[Id] = k.[MergedIntoId] AND a1.[LicenseId] = k.[LicenseId]
LEFT JOIN [WpfCustomerProjections] a2 ON a2.[Id] = a1.[MergedIntoId] AND a2.[LicenseId] = k.[LicenseId]
LEFT JOIN [WpfCustomerProjections] a3 ON a3.[Id] = a2.[MergedIntoId] AND a3.[LicenseId] = k.[LicenseId]
CROSS APPLY (SELECT CASE WHEN a1.[MergedIntoId] IS NULL THEN a1.[Id] WHEN a2.[MergedIntoId] IS NULL THEN a2.[Id]
                         WHEN a3.[MergedIntoId] IS NULL THEN a3.[Id] END AS [Id]) x      -- 3 adımda bitmeyen ya da kırık zincir: NULL
JOIN [WpfCustomerProjections] c ON c.[Id] = x.[Id] AND c.[LicenseId] = k.[LicenseId]
WHERE k.[MergedIntoId] IS NOT NULL AND k.[PurgedAt] IS NULL AND k.[CreatedByShopper] = 0 AND c.[PurgedAt] IS NULL
  AND NOT (a1.[MergedIntoId] IS NOT NULL AND (a1.[CreatedByShopper] = 1 OR a1.[PurgedAt] IS NOT NULL))
  AND NOT (a2.[MergedIntoId] IS NOT NULL AND (a2.[CreatedByShopper] = 1 OR a2.[PurgedAt] IS NOT NULL));
UPDATE m SET m.[WpfCustomerId] = cm.[CanonicalId]
FROM [CustomerIbanMemories] m JOIN @CopyMap cm ON cm.[CopyId] = m.[WpfCustomerId] AND cm.[LicenseId] = m.[LicenseId];
UPDATE pm SET pm.[ProposedWpfCustomerId] = COALESCE(cp.[CanonicalId], pm.[ProposedWpfCustomerId]),
              pm.[ActualWpfCustomerId]   = COALESCE(ca.[CanonicalId], pm.[ActualWpfCustomerId])
FROM [PaymentMatches] pm
LEFT JOIN @CopyMap cp ON cp.[CopyId] = pm.[ProposedWpfCustomerId] AND cp.[LicenseId] = pm.[LicenseId]
LEFT JOIN @CopyMap ca ON ca.[CopyId] = pm.[ActualWpfCustomerId]   AND ca.[LicenseId] = pm.[LicenseId]
WHERE cp.[CopyId] IS NOT NULL OR ca.[CopyId] IS NOT NULL;

-- RederivePaymentCustomers: S2a BackfillPaymentCustomers'ın kopyası; zincir birleştirilmiş GEÇİCİ kopyadan geçerse NULL, yalnız Receipt.
UPDATE p SET p.[WpfCustomerId] = d.[Derived]
FROM [Payments] p
OUTER APPLY (SELECT TOP (1) x.[WpfCustomerId] FROM [ShopperBroadcasterLinks] x
             WHERE x.[ShopperId] = p.[ShopperId] AND x.[LicenseId] = p.[LicenseId] AND x.[WpfCustomerId] IS NOT NULL
             ORDER BY x.[JoinedAt] DESC, x.[Id] DESC) l
LEFT JOIN [WpfCustomerProjections] h1 ON h1.[Id] = l.[WpfCustomerId] AND h1.[LicenseId] = p.[LicenseId]
LEFT JOIN [WpfCustomerProjections] h2 ON h2.[Id] = h1.[MergedIntoId] AND h2.[LicenseId] = p.[LicenseId]
LEFT JOIN [WpfCustomerProjections] h3 ON h3.[Id] = h2.[MergedIntoId] AND h3.[LicenseId] = p.[LicenseId]
CROSS APPLY (SELECT CASE WHEN (h1.[MergedIntoId] IS NOT NULL AND h1.[CreatedByShopper] = 1)
                           OR (h2.[MergedIntoId] IS NOT NULL AND h2.[CreatedByShopper] = 1)
                           OR (h3.[MergedIntoId] IS NOT NULL AND h3.[CreatedByShopper] = 1) THEN NULL
                         ELSE COALESCE(h3.[MergedIntoId], h2.[MergedIntoId], h1.[MergedIntoId], l.[WpfCustomerId]) END AS [Derived]) d
WHERE p.[ShopperId] IS NOT NULL AND p.[Source] = N'Receipt'
  AND NOT EXISTS (SELECT p.[WpfCustomerId] INTERSECT SELECT d.[Derived]);
```

- [ ] **Step 5: Yeniden koş** — `T(BankPersonMatchingS2bMigrationScriptTests|BankDecisionsS2aMigrationScriptTests)` geçer; `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations has-pending-model-changes --project OrderDeck.LicenseServer --startup-project OrderDeck.LicenseServer --context LicenseDbContext` → "No changes".
- [ ] **Step 6: SQL Server senaryosunu yaz** (desen `BankDecisionsS2aMigrationTests`: önceki göç `_BankDecisionsS2a`'ya kur, tohumla, hedefi uygula; şema aynı olduğu için tohum EF ile; S2a senaryosunun yardımcıları gerekirse `internal`). `Goc_bag_ve_karari_birlikte_tasir_dekont_musterisini_yeniden_turetir` (Developer ve Express):

| Konu | Tohum ⇒ iddia |
|---|---|
| Taşınır | A: K (geçici değil, canlı) → P; K'da bağ + öğreten karar (Proposed = Actual = K, `IbanConfirmedAt`) + yalnız önerisi K olan satır. E: K3→K2→K1→P. ⇒ bağ ve satırlar P'de; bağın `CreatedAt`, `LastConfirmedAt`, `LearnedFrom`, `SourceBankTransactionId`'si, satırın `Status`, `UpdatedAt`, `MatcherVersion`'ı aynı |
| Taşınmaz | B geçici kopya, C' silinmiş geçici kopya, D silinmiş asıl kaydın kopyası, F başka lisansa yönelen kopya, G 4 adımlı zincir, H geçici ara adımlı zincir ⇒ bağ ve satırlar yerinde |
| Okuyucu eşliği | her durumun özetiyle yeni hareket, `PaymentMatcher.MatchAsync`: A, E ⇒ `iban-memory` önerisi P; B, C', D, F, G, H ⇒ `no-signal` |
| Yeniden türetme | S2a senaryosunun (a)-(j) durumları aynen + (k) bağlantı geçici kopyada, telefon kanıtlasa da ⇒ NULL; (l) geçici asıl kayıt ⇒ o; (m) geçici ara adım ⇒ NULL; (n) aynı shopper'ın `Source = 'Bank'` satırı ⇒ değişmez; `UpdatedAt`, `Status` aynı |
| Eşlik | her dekontta `ShopperCustomerResolver.PaymentCustomerAsync` ile aynı; yalnız (k) ayrı: C# P, SQL NULL (Karar 9) |
| Bir kez | iki sabit yeniden koşulunca 0 satır; dar ve geniş IBAN tekili duruyor ve tekil |

- [ ] **Step 7: Hizmetlerle iddiayı yaz** — `Tasinan_bag_ve_karar_dort_yolda_bulunur` (aynı sınıf, kendi veritabanı, göçten sonra gerçek hizmetler; S2a planı Karar 5'in dört ölçümünün tersi): (1) bağlantısı K'da kalmış shopper'ın onaylı dekontu `ReconcileApprovalAsync` ile aynı özetli yeni harekete (öneri P) bağlanır ⇒ `ConfirmedByHuman`, `Actual` P, `IbanConfirmedAt` dolu, bağ P'de ve `LastConfirmedAt`'i ilerledi (tazeleme); (2) `ShopperPurgeService.PurgeAsync` bağı da kararı da bulur, P `MarkPurged`; (3) ayrı lisansta IBAN talebi (`BankSubjectRequestRelationalTests` deseni: talep → doğrulama → `EraseAsync`) taşınan kararın müşterisini ve `IbanConfirmedAt`'ini boşaltır; (4) ayrı lisansta P'ye verilmiş başka elle eşlemenin kaldırılması bağı silmez (kalan doğrulama taşınan karardır). B'de dekont bağdaştırılmaz, silme yalnız kopya satırlarını bulur, P'nin bağı ve projeksiyonu dokunulmaz.
- [ ] **Step 8: Derle ve koş** — `B` 0 hata; `S(BankPersonMatchingS2bMigration|BankDecisionsS2aMigration)` ya da CI.
- [ ] **Step 9: Commit** — `feat(bank): S2b göçü kopyada kalmış bağı kararla birlikte taşısın, dekont müşterisini yeniden türetsin`

### Task 12: Banka satırı korumaları: gap, `payments/sync`, panel

**Files:** Modify `LS/Services/Bank/PaymentMatchReconciler.cs` (`ReturnPaymentsToGapsAsync` 366-377), `LS/Controllers/Licenses/LicensesPaymentsSyncController.cs` (98-111), `LS/Controllers/Panel/PanelPaymentsController.cs` (64-65), `LS/Controllers/Panel/PanelSearchController.cs` (111-113). Tests: `PaymentMatchReconcilerTests`, `BankConnectionTerminatorTests`, `T/Controllers/Licenses/LicensesPaymentsSyncControllerTests.cs`, `T/Controllers/Panel/PanelPaymentsControllerTests.cs`, `PanelSearchControllerTests.cs` (InMemory).

**Interfaces:**
- `ReturnPaymentsToGapsAsync`: istenen Id'lerden `Payments.Where(Id ∈, LicenseId, Source != Receipt)` olanlar çıkarılır (Karar 12); dönüş gap'e dönen sayı (hesap reddinin `GapsReopened`'ı yalnız dekont sayar). Bağın kaldırılması (`match.PaymentId = null`) değişmez.
- Sync: `if (current.Status != PaymentStatus.Approved && current.Source == PaymentSource.Receipt) { PayerName; Amount; PaidAt; PdfHash }`; `ShipmentDirective` ve `UpdatedAt` bugünkü gibi; sözleşme (istek ve yanıt alanları) değişmez.
- Panel: iki sorguya `&& p.Source == PaymentSource.Receipt`; DTO değişmez.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Banka_odemesi_gap_e_donmez_dekont_ve_baglanmamis_Id_doner() // [bank, receipt, bağlanmamış] ⇒ 2, gap yalnız ikisinde
[Fact] public async Task Banka_odemesine_bagli_satirin_kaldirilmasi_gap_acmaz()       // UnmatchAsync: bağ kalkar, gap yok
[Fact] public async Task Hesap_reddi_banka_odemeli_eslesmeye_gap_acmaz()              // BankConnectionTerminatorTests: GapsReopened yalnız dekont
[Fact] public async Task Sync_onayli_dekontun_dort_alanini_ezmez_yonergeyi_gunceller() // yanıt "approved"; Rejected ve Pending bugünkü gibi
[Fact] public async Task Sync_banka_satirinin_alanlarini_ezmez()
[Theory, InlineData(null), InlineData("approved"), InlineData("rejected")]
public async Task Liste_banka_satirini_dondurmez(string? status)
[Fact] public async Task Arama_banka_referansiyla_dekontu_listeden_dusurmez()
// t = 4 hex üretilir; 10 banka satırı ReferansNo $"bank:{t}{Guid.NewGuid():N}:1" (daha yeni), 1 dekont $"r-{t}-{Guid.NewGuid():N}" ⇒ dekont listede, banka yok
```

- [ ] **Step 2: Koş** — `T(PaymentMatchReconcilerTests|BankConnectionTerminatorTests|LicensesPaymentsSyncControllerTests|PanelPaymentsControllerTests|PanelSearchControllerTests)`. Beklenen: yeni testler düşer; `Sync_updates_mutable_fields_on_existing_payment` (Pending) geçmeye devam eder.
- [ ] **Step 3: Uygula** — üç yerin yorumuna tasarım dayanağı (§4.4 gap yalnız dekont; §2.3 senkron ve panel; satırları S3b yazar, okuyucu önce).
- [ ] **Step 4: Yeniden koş** — aynı süzgeç; `S(BankConnectionTerminatorRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): banka onaylı ödemeyi gap'ten, senkron ezmesinden ve panel listesinden koru`

### Task 13: Gölge sıfırlamanın reddi

**Files:** Modify `LS/Services/Bank/ObifinConnectionService.cs` (sabit, istisna, 371-379, `SaveAsync` 409-431). Tests: `T/Services/Bank/ObifinConnectionServiceTests.cs` (InMemory), `ObifinConnectionServiceConcurrencyTests.cs` (SQL Server, `BeforeMatchDelete`), `T/Pages/Admin/AdminObifinPageTests.cs`.

**Interfaces:**
- Produces: `public const string ShadowResetRefusedMessage = "Bu lisansta banka kararları var; kimlik değişikliği banka verisini sıfırlayamaz. Kimlik değişimi ayrı bir yeniden anahtarlama işlemidir.";` `public sealed class ShadowResetRefusedException : ObifinValidationException`; özel `Task<bool> HasBankDecisionsAsync(Guid licenseId, CancellationToken ct)` = `PaymentMatches.AnyAsync(LicenseId && Decision != None) || Payments.AnyAsync(LicenseId && Source == Bank)`.
- Yer: `if (credentialChanged)` içinde, `allowShadowReset` denetiminden önce (bağlantıya dokunmadan fırlatır); `SaveAsync`'in yakalayıcısında `RefreshMatchVersionsAsync`'ten sonra yeniden: doğruysa `ChangeTracker.Clear()` + aynı istisna. Aynı kimliğe yeni parola reddedilmez. Başka silme yolları (hesap reddi, "Kaldır ve sil", hizmet sonu, 180. gün, IBAN talebi) değişmez (§12).

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Theory, InlineData(PaymentMatchDecision.Approved, false), InlineData(PaymentMatchDecision.Approved, true), InlineData(PaymentMatchDecision.Excluded, true)]
public async Task Karar_katmanina_girmis_lisansta_kimlik_degisimi_reddedilir(PaymentMatchDecision decision, bool allowReset)
// ShadowResetRefusedException; UserCode, imleç, hareket, eşleşme, IBAN, gap aynı; imha kaydı yok
[Fact] public async Task Banka_odemesi_olan_lisansta_kimlik_degisimi_reddedilir()   // eşleşmesiz, yalnız Source = Bank ödeme
[Fact] public async Task Ayni_kimlige_yeni_parola_reddedilmez()
[Fact] public async Task Karar_katmanli_lisansta_kimlik_degisimi_red_mesajini_gosterir() // AdminObifinPageTests: ShadowResetRefusedMessage, onay metni değil
// ObifinConnectionServiceConcurrencyTests (SQL Server)
[Fact] public async Task Silmeden_once_karar_yazilirsa_sifirlama_reddedilir_hicbir_sey_silinmez() // interleave: Decision = N'Approved', UpdatedAt ilerler
[Fact] public async Task Silmeden_once_banka_odemesi_ve_karar_yazilirsa_reddedilir()              // interleave: Source = 'Bank' Payment + bir eşleşmenin UPDATE'i
```

- [ ] **Step 2: Koş** — `T(ObifinConnectionServiceTests|AdminObifinPageTests)`. Beklenen: derleme hatası, sonra iddia hataları.
- [ ] **Step 3: Uygula** — `UpsertAsync` özetine (300-304) red kuralı ve §13 dayanağı.
- [ ] **Step 4: Yeniden koş** — aynı süzgeç; `S(ObifinConnectionServiceConcurrencyTests)` ya da `B`. Beklenen: geçer; var olan `Eszamanli_eslestirme_yazisiyla_carpisan_onayli_sifirlama_tazeleyip_yine_siler` (karar yok) değişmeden.
- [ ] **Step 5: Commit** — `fix(bank): karar ya da banka onayı olan lisansta kimlik değişimi gölge veriyi silmesin`

### Task 14: Son doğrulama, işletim notu ve PR gövdesi

**Files:** Modify `deploy/README.md` (S2a bloğunun altına S2b bloğu), `T/Services/Bank/BankQuestionsDeployTests.cs` (`README_komutlari_ve_geri_alma_uyarisi_var`'a `Contain("Banka Faz 2 S2b tabanı")`). **Interfaces:** Consumes Görev 1-13. Produces: PR'a hazır dal (push ve PR sahibin onayıyla).

- [ ] **Step 1: README bloğu** — **UYARI — Banka Faz 2 S2b tabanı:** göç yalnız veri yazar, şema aynı, otomatik geri alma güvenli. Pencerenin iki kalıntısı: (1) S2a imajının yeniden hesapladığı satır (yalnız IBAN hafızası değişince ve kaldırmada) `MatcherVersion = 2` taşır ama satır düzeyi kuralla üretilmiştir: ölçümün güncel bloğunda kalır, sayısı küçük; (2) pencerede gönderilen dekont müşterisiz kalır (S2a imajı gönderimde yazmaz, göç bir daha koşmaz): temkinli yön, S3a'da fazladan soru; sayım `SELECT COUNT(*) FROM Payments WHERE ShopperId IS NOT NULL AND Source = 'Receipt' AND WpfCustomerId IS NULL AND CreatedAt >= '<geri alma anı>'`. Göç deyimleri elle yeniden koşulmaz: SQL türetmesi kanıtlı geçici kopyanın değerini de NULL'a çeker.
- [ ] **Step 2: InMemory tam koşu** — `T(x)` süzgeçsiz. Beklenen: 0 hata; yük altında düşen varsa tek başına yeniden koş, sonucu yaz.
- [ ] **Step 3: Model ve derleme** — Görev 11 Step 5'in `has-pending-model-changes` komutu "No changes"; `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.sln --nologo -c Release` 0 hata, yeni uyarı yok.
- [ ] **Step 4: Davranış taraması** — `cd <WT> && git log --stat --format= <bu planın commit'i>..HEAD -- OrderDeck.App OrderDeck.Core OrderDeck.Licensing Extension` boş (S2b WPF'e dokunmaz; B-W0 tabanda); `cd <WT> && git grep -n "PaymentMatchDecision.None" -- 'OrderDeck.LicenseServer/Services/*.cs'` yalnız beş kilit, ölçüm ve sıfırlama reddi.
- [ ] **Step 5: SQL Server kanıtı** — yerelde `S(...)` ya da CI; yeşil olmadan PR hazır sayılmaz: `BankDecisionLockRelationalTests`, `SharedIbanMatcherRelationalTests`, `PaymentRederivationRelationalTests`, `BankPersonMatchingS2bMigrationTests` (+Express), `ObifinConnectionServiceConcurrencyTests`, `IbanReevaluationRelationalTests`, `PaymentMatcherConcurrencyTests`, `PaymentMatchReconcilerConcurrencyTests`, `BankDecisionsS2aMigrationTests`, `CustomerIdentityMergerRelationalTests`, `ShopperPurgeServiceConcurrencyTests`, `BankSubjectRequestRelationalTests`, `WpfCustomerIdentityRaceTests`.
- [ ] **Step 6: Commit** — `docs(bank): S2b geri alma penceresinin kalıntılarını işletim notuna yaz`

**PR gövdesine.** Sahibe bildirim: tasarımdan ayrılan ve açık kalan yerler "Kararlar"da (özellikle 4, 7, 8, 10, 11); `payments/sync`'in onaylı dekontta dört alanı artık ezmediği tek istemci etkisidir (yalnız gönderilmemiş eski satırlar). Sorgular salt okunur, sahip koşar:
- **Merge'den önce:** (1) `SELECT Currency, COUNT(*) FROM BankTransactions GROUP BY Currency` — harf duyarsız TL/TRY dışı değer çıkarsa (ör. kırpılmış ad) kural merge'den önce ona göre genişletilir, tahminle değil. (2) `MoveCopyBonds`'un `@CopyMap` SELECT'iyle taşınacak bağ ve eşleşme sayısı; taşınmayacak kopya bağları (geçici, silinmiş, kırık zincir): `SELECT COUNT(*) FROM CustomerIbanMemories m JOIN WpfCustomerProjections c ON c.Id = m.WpfCustomerId AND c.LicenseId = m.LicenseId WHERE c.MergedIntoId IS NOT NULL` eksi taşınacak. (3) Bağlantısı kopyada kalmış shopper, geçici olup olmamasına göre: `SELECT c.CreatedByShopper, COUNT(*) FROM ShopperBroadcasterLinks l JOIN WpfCustomerProjections c ON c.Id = l.WpfCustomerId AND c.LicenseId = l.LicenseId WHERE c.MergedIntoId IS NOT NULL GROUP BY c.CreatedByShopper`. (4) `Contradicted` kalacak aynı kişi: `SELECT COUNT(*) FROM PaymentMatches pm JOIN WpfCustomerProjections c ON c.Id = pm.ActualWpfCustomerId AND c.LicenseId = pm.LicenseId WHERE pm.Status = 'Contradicted' AND c.MergedIntoId = pm.ProposedWpfCustomerId`. (5) Satır sayıları (S2a PR gövdesindeki sorgu; göç bütçesi S2a ölçümüyle karşılaştırılır).
- **Deploy sonrası:** `SELECT MatcherVersion, COUNT(*) FROM PaymentMatches WHERE CreatedAt >= '<deploy>' GROUP BY MatcherVersion` yalnız 2; kopya bağı sayısı = (2)'nin taşınmayanı; S2a'nın "yanlış bağ" sorguları (1) ve (3) 0; `SELECT COUNT(*) FROM PaymentMatches WHERE Decision <> 'None'` = 0; `SELECT COUNT(*) FROM Payments WHERE Source <> 'Receipt'` = 0.
