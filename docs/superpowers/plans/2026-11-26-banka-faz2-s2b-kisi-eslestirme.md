# Banka Faz 2 S2b (kişi düzeyi eşleştirme) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gölge eşleştiriciyi kişi düzeyine almak (`MatcherVersion = 2`), IBAN'ı kişi kümesi olarak okuyup adayları sıralamak, `Decision ≠ None` kilidini beş yere koymak, ölçümü karar katmanından ve banka onayından süzmek, dekontun müşterisini gönderimde ve yeniden bağlamada kanıt kuralıyla yazmak, kopyada kalmış IBAN bağını onu gösteren eşleşme satırlarıyla birlikte asıl kayda taşımak, bağlantısı kopyada kalmış kişiyi çözmek; S3b'nin yazacağı banka satırını gap, `payments/sync`, panel ve gölge sıfırlamasına karşı önceden korumak; TL/TRY dışı havaleyi dışlamak. PR başlığı `fix(bank): …` (tasarım §15, S2b satırı).

**Architecture:** Okuyucu önce. Bu PR'da prod'da hiçbir satır `Decision ≠ None` olmaz, banka satırı (`Source = Bank`) doğmaz: kilitler, banka satırı süzgeçleri ve sıfırlama reddi uykudadır, testleri tohumla yazılır; S3b geri alınırsa S3a imajı (S2b'yi içerir) S3b'nin verisiyle çalışır. Şema değişmez; tek göç yalnız veri yazar (iki `Sql()`) ve bir kez koşar, S2a imajı aynı şemada ve bu veriyle çalışır. Gölgede değişen dört davranış: (1) kişi düzeyi öneri, satırında `MatcherVersion = 2`; (2) TL/TRY dışı hareket dışlanır (eşleştirici satırında `excluded:currency`; dekont adaylığı, gap ve K-6 geri sarması — kaldırma, hesap reddi, kalan doğrulama — sürümsüz değişir, payı merge öncesi sorgu (1)'de); (3) kopyada kalmış bağlantı asıl kayda çözülür (bağdaştırma, silme); kanıtsız geçici kopyada müşteri bilinmez, o shopper'ın açık gap'i bir daha çözülmez (Karar 9; payı sorgu (3) ve (3b)'de); (4) onaylı dekontun dört alanı senkronda ezilmez. Boş sürümlü satırların karşılaştırma kuralı değişmez (Karar 4); onların ölçümünü yalnız (2) ve (3) değiştirir, payları sorguyla ölçülür. Kanıt kuralı tek çözücüde (`ShopperCustomerResolver`), zincir bilgisi tek yerde (`CustomerIdResolver.ChainOfAsync`). Dekontun müşterisini yazan iki yeni yol (gönderim, yeniden yazım) koşulludur: silmenin ya da kanıt kaybının kestiği bağı geri yazmaz (Karar 11).

**Tech Stack:** ASP.NET Core 10, EF Core 10.0.10 (SQL Server prod Express; InMemory test), xunit + FluentAssertions 7, Testcontainers SQL Server 2022 (CI), LocalDB (yerel).

**Spec:** `docs/superpowers/specs/2026-10-10-banka-havale-onay-design.md` (§2 "Kişi", §2.3, §2.4, §2.5, §3, §4.4, §4.5, §12, §13, §15 S2b satırı ve Testler; sahip kararları §16; S2a'dan gelenler §18.7 satır 1-18). Tasarım dosyası bu PR'da DEĞİŞMEZ; ayrılan ve açık kalan yerler "Kararlar"da, metin değişikliği ayrı `docs(bank)` işinde.

**Saat:** ≈ 40 (görev başlıklarında); tasarım §15 S2b'ye 25 veriyor. Fark ve kısaltmalar Karar 15'te; sahibe PR gövdesinde bildirilir.

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
- **Dekont satırı:** `Payment.UpdatedAt` ilerlemez (`payments/since` imleci), `Status` (jeton) yazılmaz; müşteri yeniden yazımı yalnız `Source = Receipt`. Müşteriyi yazan her yeni yol koşulludur: seçilen bağlantı hâlâ okunan müşteride ve shopper silinmemiş (Karar 11).
- **Kanıt biçimleri sözleşmedir:** `username={key}` ardından son, `;` ya da `,` (KVKK araması, `ShopperPurgeService` 3b); `;reeval` sonda (ölçüm `EndsWith`); `excluded:` öneki ve `iban-memory` alt dizesi (tarama 2. aşaması).
- **Koşu.** `T(x)`: `cd <WT> && ORDERDECK_TEST_SHARD=inmemory:0/1 DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo --filter "FullyQualifiedName~x"` (birden çok ad `|FullyQualifiedName~y`). `S(x)`: aynı komut `ORDERDECK_TEST_SHARD` olmadan, başına `ORDERDECK_TEST_LOCALDB=OrderDeckTest ORDERDECK_TEST_LOCALDB_DIR='<yazılabilir klasör>'`; ön koşul yereldeki LocalDB yaması (depoda yok, fixture'ı `skip-worktree` yapar, commit'e girmez, bitince geri alınır). LocalDB yoksa `B` + CI (SQL Server 2022). `B`: `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo`.
- **SQL Server testi:** `[Collection(SqlServerCollection.Name)]` + `[Trait("Category","Testcontainers")]`. Kırmızı hâli görülmediyse görülmüş gibi yazma: yeni ilişkisel test Step 2'de `S(...)` ile de koşulur. LocalDB o an yoksa kırmızı CI'da görülemez (CI yalnız son hâli koşar); LocalDB ya da Docker açılınca uygulama geçici geri alınıp `S` bir kez koşulur, sonuç commit gövdesine yazılır; hiç görülmediyse gövdede "kırmızı görülmedi" yazar.
- **Koruma testi:** bugünkü kodda da geçen test adının yanında **(koruma)** yazar; Step 2'de geçer, değişmemeli. Düşmesi beklenen testlerle karıştırılmaz.
- **Commit:** görev başına bir, Türkçe emir kipi, gövde nedeni söyler, son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`; yalnız görevin dosyaları `git add`.
- **Yayın:** master merge = prod deploy; imaj otomatik geri alınır, göç alınmaz. Merge yayın penceresi (Paz/Pzt/Çar/Per 20:00–01:00 TR) dışında ve sahibindedir.

## Review Focus

1. **IBAN birden çok kişiye düşünce tahmin yok.** Sırasız tek satır okuması kalkar; küme dışı ya da çok kişili küme öneri üretmez, adaylar belirlenimli sıradadır. Görev 5: `Ortak_IBAN_kullanici_adi_yoksa_oneri_yok_adaylar_son_dogrulamaya_gore`, `SharedIbanMatcherRelationalTests.Iki_kisiye_bagli_ozet_her_kosuda_ayni_sirayla_okunur`.
2. **Karar katmanındaki havaleye hiçbir arka plan yolu dokunmaz.** Biri atlanırsa açık sorunun önerisi yayıncının önünde değişir. Görev 6: beş InMemory kilit testi, `Decision_karsilastirmasi_SQLe_cevrilir`, `Aday_secildikten_sonra_karar_katmanina_giren_hareket_baglanmaz`.
3. **Geçici kopyadan kanıtsız çıkılmaz, kesilen bağ dekonta geri yazılmaz.** Çözülseydi silme gerçek müşterinin projeksiyonunu siler, kanıtsız kişinin dekontu ona yazılırdı; bayat okumayla yazılsaydı silmenin ve kanıt kaybının boşalttığı dekont asıl kayda döner, okuyucu bunu ayırt edemezdi. Görev 2: `Gecici_kopyadan_kanitsiz_cikilmaz` (Görev 7, 9, 10 aynı çözücüyü çağırır); Görev 9: `Yeniden_yazim_bagi_okuduktan_sonra_silme_commit_olursa_yazmaz`, `Gonderim_cozdukten_sonra_bag_kesilirse_dekont_musterisiz_kalir`; koşulun iki maddesi tek tek (silme ikisini birden bozar): `Yeniden_yazim_bagi_okuduktan_sonra_bag_bosalirsa_yazmaz`, `Yeniden_yazim_bagi_okuduktan_sonra_hesap_silinirse_yazmaz`, `Gonderim_cozdukten_sonra_hesap_silinirse_dekont_musterisiz_kalir`; RCSI açık DB'de koşulun commit olmamış kesmeyi beklemesi (Karar 11): `PaymentRederivationRcsiRelationalTests`.
4. **Göç bir kez koşar: bağ ve karar birlikte, yalnız taşınabilir kopyadan.** Yarım taşıma IBAN talebini, K-6'yı, dekont onayını ve silmeyi bozar (S2a planı Karar 5). Görev 11: `Goc_bag_ve_karari_birlikte_tasir_dekont_musterisini_yeniden_turetir`, `Tasinan_bag_ve_karar_dort_yolda_bulunur`.
5. **Ölçüm kendini doğrulamaz, eski blok bozulmaz.** Banka onayı ya da karar katmanı sayılırsa eşik kendini doğrular; sürüm karışırsa nüfus değişimi isabet sanılır. Görev 8: `Surumler_ayri_sayilir_hareket_sayilari_ortak`, `Karar_katmanindaki_satir_sayilmaz_dekontla_kapanan_sayilir`, `Banka_odemesine_bagli_satir_dekont_onayli_sayilmaz`.

## Kararlar

1. **Öneri satırı** (tasarım açık bırakıyor): kişi bir asıl kaydıyla önerilir. Kullanıcı adında vuran satır; kişide birden çok vuran satır varsa IBAN bağının satırı aralarındaysa o, yoksa en küçük `Id` (`"N"` ordinal). Yalnız IBAN'da kişinin en son doğrulanan bağının asıl kaydı (eşitlikte küçük `Id`). Ad katmanında en küçük `Id`. Bütün sıralamalar bellekte, `Id.ToString("N")` ordinal karşılaştırmayla yapılır; SQL'de `ORDER BY` yoktur, çünkü `uniqueidentifier` sırası `Guid.CompareTo`'nunkinden farklıdır. Belirlenimli olsun diye; platform kırılımı gerçek müşteriden okunur, etkilenmez.
2. **`MatcherVersion = 2` eşleştiricinin her yazımında** (dışlama, belirsizlik, `;reeval` dahil). İnsan kararı (`LinkAsync`, elle eşleme), birleştirici ve kaldırma dokunmaz: sürüm, satırın son hesabının üreticisidir. Kaldırma öneri alanlarını boşaltır, `SupportedByIbanMemory`'yi `false`'a çeker, sürümü bırakır. Durdurulmuş bağlantıda yeniden hesap koşmaz; sürüm `null`'a çekilseydi v2 satırı eski bloğa `NoProposal` olarak geçer ("eski blok bozulmaz" çiğnenir), deploy sonrası sorgusu da yanlış alarm verirdi. Boş sürümlü satırlar toplu yeniden hesaplanmaz (gölge sayaçları bir gecede sıçrardı).
3. **`SupportedByIbanMemory`'yi eşleştirici yazar** (öneri kişisi IBAN kümesinde). Tasarım sütunun anlamını veriyor, yazanını söylemiyor; eşleştirici dışından hesaplanamaz, S3a okur.
4. **Öneri–karar karşılaştırması sürüme bağlı.** `LinkAsync`'in `Status`'u ve doğrulanmış bağ koşulu, elle eşlemenin `Status`'u: `MatcherVersion = 2` satırda kişi düzeyi (aynı kişi anahtarı), boş sürümde satır düzeyi (bugünkü). Kişi düzeyi öneri satır düzeyinde karşılaştırılsaydı IG'ye önerilen havalenin TT dekontu `Contradicted` sayılır, v2'nin çelişkisi sahte şişerdi (§2.4); eski satıra uygulansaydı eski ölçüm bloğu deploy anında anlam değiştirirdi. `LearnIbanAsync`'in aynı kişinin öbür satırındaki bağı (`ConflictOtherCustomer`), `RemainingConfirmationsAsync`, `taken` ve bayat seçim denetimi satır düzeyinde kalır: ikinci bağ dar indeks dururken yazılamaz, yarım kişi düzeyi K-6 geri sarmasını bozar; S3b'nin "dekont etkileşimi (kişi düzeyi)" kalemiyle.
5. **Kanıt biçimleri korunur.** `ambiguous:{n}`, `ambiguous-name:{n}` kişi sayar. İki yeni belirteç: `iban-memory-shared:{n}` (kullanıcı adı yok, küme > 1 kişi; `iban-memory` alt dizesini taşır, tarama bağ silinince satırı yeniden yakalar) ve `excluded:currency`.
6. **Adaylar** `PaymentMatcher.CandidatePersonsAsync` okumasıdır, saklanmaz; S2b'de tüketicisi yok, ucu S3a. Kullanıcı adı birden çok kişiye düşerse aday yok (§3 `Unmatched`).
7. **Taşınabilir kopya tek kuraldır (göç ve okuyucu):** kopya geçici değil ve silinmemiş; zincir en çok 3 adımda, aynı lisansta silinmemiş bir asıl kayda varıyor; ara adımlar da geçici ya da silinmiş değil. Göç yalnız onu taşır; kişi düzeyi okuyucu kopyadaki bağı yalnız bu kuralla asıl kayda sayar. Tasarım (§2.5) "silinmiş asıl kayda ve silinmiş kopyadan taşımaz" diyor; geçici kopyadan da taşınmaz, çünkü SQL telefon kanıtını sınayamaz ve bağ kanıtsız kişinin havalesinden öğrenilmiş olabilir. Kalan satırlar bugünkü gibidir (eşleştirici görmez, 12 ay saklama); sayısı PR gövdesinde. Silme bağı kopya Id'siyle bulur, kararı ise yalnız karar da kopyadaysa. Bağlantısı da aynı kopyada kalmış kişide S2b çözücüsü yeni kararı asıl kayda yazar; `LearnIbanAsync` çelişki döner, bağ tazelenmez. IBAN talebi (`EraseAsync`, kararları bağın müşterisiyle arar) o kararı bulmaz. Bu nüfus iki yarış ister (bağ da bağlantı da aynı kopyada; geçici kopyada ayrıca telefon kanıtı), sayısı sorgu (3c)'de. Öneri için tek yarış yeter: göçten sonra taşınabilir kopyada kalan bağı (birleştirme `LearnIbanAsync` ile çakışırsa; geri alma penceresinde S2a imajı da yazar, README kalıntı 4) okuyucu asıl kayda sayar ve havaleye asıl kaydın v2 IBAN önerisini yazar (`Layer = IbanMemory` ya da `SupportedByIbanMemory`). IBAN talebi müşteri kümesini bağın Id'sinden (kopya) kurar; öneri asıl kaydı gösterdiği için satır "yalnız kanıt" sayılır, kanıt boşalır, öneri kalır. Açıklaması ve kanıtı silinmiş satırı tarama yeniden hesaplamaz: öneri hareketin 180. gününe kadar durur. S2b'den önce bu öneri doğmazdı (eşleştirici kopyayı aday saymazdı); merge'den önce bu nüfus yoktur, sayısı deploy sonrası sorgusundadır. Kararı ve öneriyi bağın kişisiyle aramak S4'e yazıldı; purge'ün kopyalarla aramasıyla aynı kuralla ele alınır. S2b'ye çekmek (`EraseAsync`'in müşteri kümesine bağın okuyucu kuralıyla vardığı asıl kaydı eklemek) sahip kararıdır.
8. **Göç `PaymentMatches.UpdatedAt`'i ilerletmez, `Status`'u düzeltmez.** Göç sırasında yazan yok (tek konteyner, açılışta `Migrate()`); ilerleseydi tarama 2. aşaması (`m.UpdatedAt < b.CreatedAt`) bağdan önce hesaplanmış satırı seçmezdi. Bozulma 3'le yazılmış `Contradicted` kalır: düzeltmek ölçümü geçmişe dönük ve sürümle ayrılamaz biçimde değiştirirdi (sayısı PR gövdesinde).
9. **Bağlantı çözücüsü statik ve tektir** (`Services/ShopperLinking/ShopperCustomerResolver`, kanıt kuralının evi `WpfCustomerLinkMatcher` yanında; DI ve kurucular değişmez: çözücü ve `PaymentMatcher` zincir için `new CustomerIdResolver(db)` kurar). Kanıtsız geçici kopyada sonuç kopya Id'si değil null: kopya Id'si yazılsaydı `CustomerIdResolver` okuyucusu onu gerçek müşteriye çözerdi. Silinmiş asıl kayda giden bağlantı bugünkü gibi çözülür (S2a sabitiyle eşlik; okuyucunun silinmiş kayıt kuralı IBAN bağı içindir, §2.5). Bağdaştırıcı null'da bugünkü "bağ yok" yolundadır, gap yazmaz. Bedeli: bu shopper'ların S2a'da açılmış açık gap'i bir daha çözülmez (`TryResolveGapOnceAsync` null'da döner), onaylı dekontları bağdaştırılmaz. `OpenGaps` hareket düzeyinde ve sürümsüz olduğu için bu, sürümle ayrılamayan kalıcı bir artıştır; payı sorgu (3b)'de (üst sınır), `PaymentMatchSummary` özetine de yazılır (Görev 8).
10. **Yeniden bağlamada yeniden yazım** (§2.3 açık maddesi, öneri alındı): geriye dönük eşleştirme ve `ResolvePendingLinksAsync`'in iki çağıranı, bağ kaydedildikten SONRA shopper'ın o lisanstaki `Source = Receipt` dekontlarını türetme kuralıyla yazar. Yazım lisans başına tek ve koşullu bir UPDATE'tir (ilişkisel: koşul tablolarını kilitli okuyan ham SQL, Karar 11; InMemory: aynı süzgeçle izlenen yazım, `SessionReleaser` deseni). Koşul Karar 11'dedir. `Status`'a bakmaz, onu yazmaz: araya giren onay yazımı düşürmez. Düşerse (her hata) uyarı logu yazılır (yalnız Id'ler ve istisna türü), yanıt değişmez. Aynı kayda konmaz: senkronun yakalayıcısı bağlamayı da düşürürdü. Katılma yazmaz: geçici satır açan katılma gerçek müşterinin eski dekontlarını yeni satıra taşırdı.
11. **Gönderim ve yeniden yazım bayat bağlantıyla yazmaz.** Çözüm ile kayıt arasında biten birleştirmenin bıraktığı Id ya kopyadır (okuyucu zinciri izler) ya geçici kopyadır (okuyucu kanıt ister). Bağı kesen yollar ise (silme bağlantıyı siler; `ReproveLinksAsync` ve `CustomerIdentityMergeJob` bağlantıyı boşaltır, `UnbindPaymentsAsync` dekontu) `Status`'a dokunmaz. Geride asıl kaydın kendisi kalır ve okuyucunun kanıt kuralı onu yakalayamaz. Bu yüzden:
    - Yeniden yazımın WHERE'i, seçilen bağlantının hâlâ okunan müşteride olmasını (`ShopperBroadcasterLinks.Any(l => l.Id == seçilen && l.WpfCustomerId == okunan)`) ve shopper'ın `DeletedAt`'inin boş olmasını ister.
    - Gönderim çözümü R2 yüklemesinden sonra, kayıttan hemen önce yapar. Kayıttan sonra aynı koşulu sınar, tutmuyorsa dekontun müşterisini null'a çeker.
    - Silme adım 2'de `WpfCustomerId`'yi her zaman yazar (`IsModified`, Görev 10). Böylece okumasından sonra commit olan yazım da boşalır; EF değişmeyen boş değeri UPDATE'e koymazdı.
    - Koşul tabloları (`ShopperBroadcasterLinks`, `Shoppers`) aynı tek deyimde `WITH (READCOMMITTEDLOCK)` ile okunur: bağlantıyı silen ya da boşaltan, shopper'a `DeletedAt` yazan işlem commit olana dek beklenir, RCSI ayarından bağımsız. Prod'da RCSI büyük olasılıkla AÇIK: DB ilk kurulumda `EnsureCreated()` ile oluşturuldu (`deploy/bootstrap-migration-history.sql` başı) ve EF Core DB'yi oluştururken (Azure SQL dışında) `READ_COMMITTED_SNAPSHOT ON` yazar (yerelde: `model` 0, EF'in oluşturduğu DB 1). Test fikstürü DB'yi düz `CREATE DATABASE` ile açar (RCSI kapalı), depodaki yarış testleri kilitli okumayı sınar. RCSI açıkken ipucusuz EXISTS ifade başındaki anlık görüntüyü okurdu: kesen işlem commit olmamışken koşan yazım eski bağlantıyı görür, kesilen bağı yazar; gönderimin denetimi boşaltmaz. Hedef tablo (`Payments`) RCSI açıkken de U kilidiyle en son commit'ten okunur, yalnız koşul tabloları sürümlü okunurdu. EF Core LINQ tablo ipucu desteklemez: iki yazım ilişkisel yolda ham SQL'dir (`ExecuteSqlInterpolatedAsync`, değerler parametre), InMemory yolu değişmez. Uygulama kilidi (`sp_getapplock`) ve REPEATABLE READ seçilmedi: ilki kesen bütün yolları (silme, `DeleteMe`, `ReproveLinksAsync`, birleştirme işi) değiştirir, ikincisi S kilidini işlem sonuna dek tutar ve kesenle kilitlenme döngüsü kurar. Kanıt `PaymentRederivationRcsiRelationalTests` (dört test: iki deyim × iki kesme, her biri tek ipucunu sınar): RCSI göçten önce açılır, önkoşul her testte sınanır; kesen işlem, yazımın onun kilidinde beklediği `sys.dm_exec_requests`'te görülene dek commit edilmez (sabit süre yok: geç başlayan yazım commit'ten sonra koşup boşuna geçemez); ipucusuz kodda dördü de düşer, RCSI kapalı DB'de geçer. Sorgu (6) artık merge kapısı değil, bilgidir. Kilitli okumanın penceresi RCSI kapalı tasarımdakiyle aynı ve plana bağlı: dekont önce U kilidiyle okunup koşul sonra sınanırsa (yerelde gözlenen plan) yazım dekontun kilidini tutarak bekler, kesen ardından aynı dekonta yazarsa kilitlenme olur (yerel sondada kurban yeniden yazımdı; `TryRederivePaymentsAsync` ve gönderimin 12b adımı yakalar, dekont kesenin değerinde kalır); ilişkisiz EXISTS dekontun kilidinden önce sınanırsa, kesen aynı kayıtta dekontu bağlantıdan ya da shopper'dan önce yazdığında (milisaniye altı) yazım kesmeyi görmeden yazar. Bugünkü kesenler (silme, `ReproveLinksAsync`, birleştirme işi) tek `SaveChanges`'te dekontu bağlantıdan ve shopper'dan önce yazar (EF tablo adı sırası, bu tablolar arasında yabancı anahtar yok; yerel sondada ölçüldü), `DeleteMe` dekont yazmaz: kilitlenme dalı ancak bu sırayı tersine çeviren yeni bir kesenle doğar, bugünkü kesenlerle kalan ikinci daldır.
    - Kalan pencere: kesen `ReproveLinksAsync` ya da birleştirme işiyse ve yazım onun okuma→kayıt aralığında (aynı istek, milisaniyeler) commit olursa dekont asıl kayıtta kalır. Kapatmak, kesenin dekontu kayıt anında küme tabanlı boşaltmasını ister (S2a kodu); S3a'ya yazıldı.
12. **Banka satırı süzgeçleri "banka olduğu bilinmedikçe dekont"tur.** Gap ve ölçüm `Source ≠ Receipt` olduğu bilinen ödemeyi dışlar; ödeme satırı bulunamayan bağ bugünkü gibi dekont sayılır (bağ FK'sız; var olan testler bağlanmamış Id'yle kurar). Panel `Source = Receipt` (izin listesi). `payments/sync` yalnız tasarımın saydığı dört alanı korur; `ShipmentDirective` ve `UpdatedAt` bugünkü gibi.
13. **Sıfırlama reddi** kimlik değişince izin denetiminden ÖNCE ve silmenin yeniden denemesinde bir kez daha sorulur; ayrı istisna (`ShadowResetRefusedException`), onay isteyen istisnadan türemez (sayfa "kutuyu işaretleyin" demesin, sayfa kodu değişmez).
14. **Notlardan alınmayanlar:** admin elle eşleme ve kaldırma reddi (tasarım §15'te S3b "admin reddi"); `TryResolveGapOnceAsync` ve `PaymentMatchReconcileJob`'a ek `Source` denetimi (gap yalnız `ReturnPaymentsToGapsAsync` ve dekont onayından doğar, onay işi yalnız dekontta zamanlanır); gönderimde açık `Source = Receipt` (alan başlangıcı zaten öyle); `payments/sync`'te banka satırını bütün alanlarıyla atlamak; ölçümde karar katmanı satır sayısı (S3c).
15. **Saat ve kısaltma.** §15 S2b'ye 25 saat veriyor, plan ≈ 40 saat. Farkın +10'u kapsamın eksik tahmininden gelir: 13 kırmızı/yeşil döngü, dört yeni SQL Server sınıfı ve üç var olan sınıfa ekler, göç senaryosu ve dört yolun hizmet testi. +6'sı inceleme düzeltmelerinden gelir: koşullu yazım ve yarış testleri, silmenin dekont müşterisini her zaman yazması, yeniden yazımın hata yalıtımı, K-6 para birimi testi, geri alma kalıntıları ve sorgular. Kısaltmalar −1'dir. Göç senaryosu yalnız Developer'da koşar: S2a'nın Express sınıfı ONLINE dizin işlemi ve sürüm denetimi içindi, bu göç ise DDL'siz DML'dir ve aynı biçimli türetme S2a'da Express'te ölçüldü. İki Docker'sız dize testi teke iner. Pilota kadarki toplam 345 → 360, genel toplam 440 → 455 olur; §15'in ve "Etki" satırının metni ayrı `docs(bank)` işinde değişir (PR gövdesi sahibe bildirir).

**Sonraki PR'lara bırakılan (yazma):**
- **S3a:** kilit `Unmatched`'ı da kapsar; §3'ün `Unmatched → Awaiting` yeniden değerlendirmesi kilidi delmeden ayrı yoldan. `CandidatePersonsAsync`'in ucu, `IbanShared`/`IbanOtherCustomer` nedenleri. `Payment.WpfCustomerId` okuyucuları `ShopperCustomerResolver.ResolveLinkAsync` kuralıyla çözer, `CustomerIdResolver` ile değil. Karar yazdığı için admin reddinin (§4.4, S3b) S3a'ya çekilmesi planında tartışılır. Tartışmaya kaldırmanın karar katmanıyla etkileşimi de girer: admin `Decision ≠ None` satırı elle eşleyip (karar anı boş olduğu için izinli) kaldırırsa kaldırma öneri alanlarını boşaltır, ardından gelen yeniden hesap K1'e takılır; `Awaiting` soru önerisiz kalır, `UnmatchAsync` yine true döner ve sayfa "öneri yeniden hesaplandı" der. Sıfırlama denetimi anlık görüntüdür: yüklenmiş bir harekete sonradan yazılan karar satırını hareketin kaskadı jetonsuz siler; değerlendirmenin yazım yolu buna bakar. Birleştiricinin geçici kopyanın banka izini (IBAN bağı, kararlar) kanıtsız taşıması (`CustomerIdentityMerger.RepointReferencesAsync`; birleştirme işi ve devralma onu geçici kopyada da çağırır) Karar 7 ile çelişir: S3a'dan önce değerlendirilir. Bağ kesen yollar (`UnbindPaymentsAsync`: `ReproveLinksAsync` ve birleştirme işi) dekontu okuma anındaki değerle boşaltır; kesmenin okuma→kayıt aralığında commit olan yazım asıl kayıtta kalır (Karar 11). Okuyucu sütunu kullanmadan önce bu da değerlendirilir.
- **S3b:** dar indeks düşerken `LearnIbanAsync`, `RemainingConfirmationsAsync`, `taken` ve bayat seçim denetimi kişi düzeyine. `UnbindPaymentsAsync` (`CustomerIdentityMerger` 224-228) ve silmenin 2. adımı (`ShopperPurgeService` 205-213) `Source` süzmez: banka satırı yazılınca insan kararının müşterisini boşaltırlar.
- **S3c:** ölçüm sayfasında karar katmanı satır sayısı. **S4:** purge'ün IBAN hafızasını birleştirilmiş kopyalarla araması (taşınmayan ve S2b'den sonra yarışla doğan kopya bağları). Aynı kuralla IBAN talebinin (`BankSubjectRequestService.EraseAsync`) kararları ve önerileri bağın müşterisinin zinciriyle araması da S4'tedir: bağ kopyada, karar ya da öneri asıl kayıtta kalan satırlar (Karar 7; S2a'dan beri bağı yarışla kopyada kalanlar da).
- **Banka dışı, ayrı iş:** `ShopperBalanceController` 59-70 bağlantıyı `CustomerIdResolver` ile kanıtsız çözer; geçici kopyada kalmış bağlantıda shopper gerçek müşterinin bakiyesini görür.

## Dosya haritası

| Dosya (`LS/` = `OrderDeck.LicenseServer/`, `T/` = `OrderDeck.LicenseServer.Tests/`) | Sorumluluk | Görev |
|---|---|---|
| `LS/Services/CustomerSync/CustomerIdResolver.cs` | zincirin kopya bilgisi | 1 |
| `LS/Services/ShopperLinking/ShopperCustomerResolver.cs` (yeni) | bağlantı → müşteri, kanıt kuralı, dekontun yeniden yazımı | 2, 9 |
| `LS/Services/Bank/PaymentMatcher.cs` | para birimi, kişi düzeyi, IBAN kümesi, adaylar, K1 | 3-6 |
| `LS/Services/Bank/PaymentMatchReconciler.cs` | K2-K4, sürüme bağlı karşılaştırma, çözücü, kaldırma, gap | 6, 7, 12 |
| `LS/Services/Bank/BankMatchSweepJob.cs` | K5 | 6 |
| `LS/Services/Bank/BankSubjectScrub.cs` | silmede kararsız önerinin IBAN desteği (uygulamada eklendi) | 4 |
| `LS/Services/Bank/PaymentMatchMetrics.cs`, `LS/Pages/Admin/BankaEslestirme/Index.cshtml(.cs)` | para birimi projeksiyonu, ölçüm süzgeci, sürüm blokları | 3, 8 |
| `LS/Services/ShopperPayments/ShopperPaymentSubmissionService.cs`, `LS/Domain/Payment.cs` | gönderimde dekontun müşterisi | 9 |
| `LS/Controllers/Licenses/LicensesWpfCustomersSyncController.cs`, `LS/Controllers/Shopper/ShopperAuthController.cs` | yeniden bağlamada yeniden yazım | 9 |
| `LS/Services/Shoppers/ShopperPurgeService.cs` | bağlantının asıl kayda çözülmesi, dekont müşterisinin her zaman yazılması | 10 |
| `LS/Data/Migrations/*_BankPersonMatchingS2b*` | veri göçü | 11 |
| `LS/Controllers/Licenses/LicensesPaymentsSyncController.cs`, `LS/Controllers/Panel/PanelPaymentsController.cs`, `PanelSearchController.cs` | banka satırı korumaları | 12 |
| `LS/Services/Bank/ObifinConnectionService.cs` | sıfırlama reddi | 13 |
| `deploy/README.md` | geri alma notu | 14 |
| `T/...` (görevlerde) | testler | 1-14 |

### Task 1: Zincirin kopya bilgisi (1,5 sa)

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

### Task 2: Bağlantıdan müşteriye tek çözücü (2,5 sa)

**Files:** Create `LS/Services/ShopperLinking/ShopperCustomerResolver.cs`. Test: `T/Services/ShopperLinking/ShopperCustomerResolverTests.cs` (InMemory, yeni klasör).

**Interfaces:**
- Consumes: Görev 1 `ChainOfAsync` (`new CustomerIdResolver(db).ChainOfAsync(...)`; çözücünün DI kaydı yok); `WpfCustomerLinkMatcher.PhoneProves`.
- Produces (`public static class ShopperCustomerResolver`):
  - `Task<Guid?> ResolveLinkAsync(LicenseDbContext db, Guid licenseId, Guid linkCustomerId, string? shopperPhone, DateTimeOffset? phoneVerifiedAt, CancellationToken ct)` — zincir geçici kopyadan geçmiyorsa varılan Id (`CustomerIdResolver` ile aynı, silinmiş asıl kayıt dahil, Karar 9); geçiyorsa varılan satır (`IgnoreQueryFilters`, aynı lisans) asıl kayıt, geçici değil ve `PhoneProves(satır.Phone, shopperPhone, phoneVerifiedAt)` ise o, değilse null (başka lisansın Id'si ve adım sınırında kalan kopya da null). Adım sınırında varılan dördüncü düğüm geçici kopya olsa da bayrağı sayılmaz (yalnız izlenen kopyalar): sonuç `CustomerIdResolver`'ınki gibi o kopyanın Id'sidir; birleştirme zinciri düzleştirdiği için pratikte varılmaz, null'a çekmek sahip kararıdır.
  - `public readonly record struct DerivedCustomer(Guid? CustomerId, Guid LinkId, Guid LinkCustomerId);` ve `Task<DerivedCustomer?> DeriveAsync(LicenseDbContext db, Guid licenseId, Guid? shopperId, CancellationToken ct)` — türetme kuralı (§2.3): shopper×lisansın `WpfCustomerId`'li bağlantılarından `OrderByDescending(JoinedAt).ThenByDescending(Id)` ilki, `AsNoTracking`, `LeftAt`'e bakılmaz; sonra `ResolveLinkAsync` (shopper telefonu yalnız zincir geçici kopyadan geçince okunur). Seçilen bağlantının Id'si ve okunan müşterisi de döner: Görev 9'un koşullu yazımı bunları kullanır. Bağlantı yoksa ya da shopper null ise null.
  - `Task<Guid?> PaymentCustomerAsync(LicenseDbContext db, Guid licenseId, Guid? shopperId, CancellationToken ct)` = `(await DeriveAsync(...))?.CustomerId`.
  - Görev 9: `RederivePaymentsAsync`, `TryRederivePaymentsAsync`, `ClearIfLinkChangedAsync`.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
public enum Proof { Matches, OtherPhone, NotVerified, CanonicalProvisional }
[Theory, InlineData(Proof.Matches, true), InlineData(Proof.OtherPhone, false), InlineData(Proof.NotVerified, false),
 InlineData(Proof.CanonicalProvisional, false)]
public async Task Gecici_kopyadan_kanitsiz_cikilmaz(Proof proof, bool resolves)
// Kp (geçici kopya) → P; bağlantı Kp'de. P.Phone = shopper telefonu (OtherPhone: başka TestPhone.NewE164()); PhoneVerifiedAt
// dolu (NotVerified: boş); CanonicalProvisional: P.CreatedByShopper = true. ResolveLinkAsync ve PaymentCustomerAsync: resolves ? P : null.

[Fact] public async Task Gecici_kopyadan_gecen_zincir_lisansin_asil_kaydina_varmazsa_kanit_aranmaz()
// telefon kanıtlıyor: Kp → X (X başka lisansta) ⇒ null; Kp → Z3 → Z2 → Z1 → W (adım sınırında Z1 kopyası) ⇒ null

[Fact] public async Task Gecici_olmayan_kopya_asil_kayda_cozulur_gecici_asil_kayit_kendisidir()
// K→P (geçici değil): telefon aranmadan P; S (geçici asıl): S; 4 adımlı zincir: CustomerIdResolver'ın vardığı Id.

[Fact] public async Task Dekontun_musterisi_en_yeni_musterili_baglantidan_turetilir()
// etkin bağlantı müşterisiz + eski ayrılmış bağlantı Q'da ⇒ Q; daha yeni ayrılmış bağlantı P'de ⇒ P;
// bağlantı yalnız başka lisansta ⇒ null; shopperId null ⇒ null. JoinedAt eşitliği tohumlanmaz (Guid sırası sağlayıcıya göre).
// DeriveAsync seçilen bağlantının Id'sini ve okunan müşterisini (kopya Id'si dahil) döner.
```

- [ ] **Step 2: Koş** — `T(ShopperCustomerResolverTests)`. Beklenen: derleme hatası.
- [ ] **Step 3: Uygula** — sınıf özeti: kural §2.5'tir ve bağlantıdan müşteriye giden dört yol (bağdaştırma, silme, gönderim, yeniden yazım) bu sınıftan geçer; tek yol değildir, bakiye ucu ayrı iştir ("Banka dışı, ayrı iş"); kanıt `CustomerIdentityMergeJob`'unkiyle aynıdır (orada farklı yazılsaydı aynı bağlantı birleştirmede beklemeye düşer, burada asıl kayda giderdi); SQL eşi Görev 11'deki göçtür ve kanıtlı geçici kopyada bilerek NULL verir.
- [ ] **Step 4: Yeniden koş** — `T(ShopperCustomerResolverTests)`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `feat(shopper-link): bağlantıdan müşteriye kanıt kurallı tek çözücü`

### Task 3: TL/TRY dışı havale dışlanır (1,5 sa)

**Files:** Modify `LS/Services/Bank/PaymentMatcher.cs` (`ExclusionReason` 116-123, sınıf özeti), `LS/Services/Bank/PaymentMatchMetrics.cs` (projeksiyon 107-111). Tests: `T/Services/Bank/PaymentMatcherTests.cs` (`Incoming`'e `currency` parametresi), `PaymentMatchReconcilerTests.cs`, `PaymentMatchMetricsTests.cs` (InMemory).

**Interfaces:**
- Produces: `public const string CurrencyExclusion = "currency";` `internal static bool IsTryCurrency(string? currency)` — `Trim()` edilmiş değer `"TL"` ya da `"TRY"` ile `OrdinalIgnoreCase` eşit. `ExclusionReason`'ın son kolu: `!IsTryCurrency(tx.Currency) ? CurrencyExclusion : null` (yön, sıfır, kod önce). Kanıt `excluded:currency`.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Theory, InlineData("TL"), InlineData("TRY"), InlineData("try"), InlineData(" TL")]
public async Task TL_ve_TRY_degerlendirilir(string currency)   // (koruma) kullanıcı adı açıklamada ⇒ Proposed
[Theory, InlineData("USD"), InlineData("EUR"), InlineData("GBP"), InlineData("TRL"), InlineData("Tür"), InlineData("")]
public async Task TL_disi_para_birimi_dislanir(string currency) // NoProposal, Evidence "excluded:currency", IsExcluded true
// (izin listesi: kara liste mutasyonu GBP, TRL ve kırpılmış ad "Tür" ile düşer)
// PaymentMatchReconcilerTests
[Fact] public async Task Yabanci_para_birimli_tek_aday_dekonta_baglanmaz_elle_esleme_ogretmez()
// USD hareket dekontun tek tutar/zaman adayı ⇒ gap NoCandidate; ManualMatchAsync ⇒ ManualMatchIbanOutcome.Excluded, bağ yok
[Fact] public async Task TL_disi_hareketin_dogrulamasi_kaldirmada_kalan_sayilmaz()
// bağın son doğrulaması T3 (TL), daha eski işaretli doğrulama T2 (USD), öğreten havale silinmiş ⇒ T3'ün kaldırılması
// bağı siler ve "kalan doğrulama yok" imha kaydı yazar (bugün T2'ye geri çekilir)
// PaymentMatchMetricsTests
[Fact] public async Task Yabanci_para_birimli_hareket_olcumde_dislanir() // Excluded 1; satırı karar sayımına girmez
// AdminBankaEslestirmePageTests (inceleme düzeltmesi: bildirim ve ManualMatchIbanOutcome.Excluded özeti para birimini de anar)
[Fact] public async Task Elle_esleme_TL_disi_havalede_IBAN_ogretmez_bildirim_para_birimini_anar()
```

- [ ] **Step 2: Koş** — `T(PaymentMatcherTests|PaymentMatchReconcilerTests|PaymentMatchMetricsTests)`. Beklenen: derleme hatası (`CurrencyExclusion` yok), sonra yeni testler düşer; ölçüm testi projeksiyon `Currency`'yi taşımadığı için ("TL" varsayılır) düşer; `TL_ve_TRY_degerlendirilir` (koruma) geçer.
- [ ] **Step 3: Uygula** — kural ve projeksiyona `Currency = t.Currency`. Sınıf özeti: değer `ObifinClient`'tan kırpılmış ama harfi normalleştirilmemiş gelir, boşu "TL" yapılır; boş değer (başka yoldan) dışlanır; dağılım merge'den önce ölçülür (Görev 14). `IsExcluded`'ın K-6 tüketicileri de (kaldırmada geri sarma, hesap reddi, `RemainingConfirmationsAsync`) TL dışı hareketi doğrulama saymaz. S2b'den önce böyle bir harekette işaretlenmiş doğrulama artık kalan sayılmaz, kaldırılırsa da geri sarılmaz. Payı merge öncesi sorgu (1)'dedir; 0'dan büyükse sahip karar verir.
- [ ] **Step 4: Yeniden koş** — aynı süzgeç + `T(BankMatchSweepJobTests|MatchingSinkTests)`. Beklenen: geçer (var olan testler `"TL"` kurar).
- [ ] **Step 5: Commit** — `fix(bank): TL/TRY dışı havaleyi dışla, ölçüm de para birimini okusun`

### Task 4: Kişi düzeyi eşleştirici: kullanıcı adı, ad, sürüm (3 sa)

**Files:** Modify `LS/Services/Bank/PaymentMatcher.cs` (`Candidate` 214, `CandidatesAsync` 227-240, katman 1 ve 3 kararı 170-209, `Set` 307-311, sınıf özeti), `LS/Services/Bank/BankSubjectScrub.cs` (`MatchForCustomer`, uygulamada eklendi: kararsız öneriyi boşaltırken `SupportedByIbanMemory = false`; Karar 2'nin silme eşi, KVKK silmesi ve IBAN talebinin ortak kuralı; S2b'den önce bayrak hep false olduğu için prod sonucu değişmez). Test: `T/Services/Bank/PaymentMatcherTests.cs` (InMemory; `Customer` yardımcısına `groupId` ve `platform`).

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
public async Task GroupId_ordinal_bos_ya_da_bosluk_gruplanmamis(string? a, string? b) // (koruma) ⇒ NoProposal "ambiguous:2"
[Fact] public async Task Ad_katmani_ayni_kisinin_iki_satirini_tek_kisi_sayar()          // ⇒ NameAmount 0.50 "name:2"
[Fact] public async Task Her_yazim_surum_2_yazar()  // excluded:CCP, ambiguous:2, no-signal ve ReevaluateAsync (";reeval" sonda) ⇒ MatcherVersion 2
[Fact] public async Task Ayni_kisi_ayni_grup_ya_da_ayni_Id_aday_disi_satir_satir_duzeyi() // SamePersonAsync
```

- [ ] **Step 2: Koş** — `T(PaymentMatcherTests)`. Beklenen: derleme hatası, sonra kişi testleri düşer (`ambiguous:2`, `conflict`); `GroupId_ordinal_bos_ya_da_bosluk_gruplanmamis` (koruma) geçer.
- [ ] **Step 3: Uygula** — tablo; sınıf özetine kişi tanımı, sürüm ve öneri satırı kuralı.
- [ ] **Step 4: Yeniden koş** — `T(PaymentMatcherTests|PaymentMatchReconcilerTests|BankMatchSweepJobTests)`. Beklenen: geçer; `Birden_fazla_aday_oneri_uretmez` (farklı kişiler) ve `Iban_hafizasi_kullanici_adiyla_celisirse_oneri_yok` (biçim) değişmeden.
- [ ] **Step 5: Commit** — `fix(bank): eşleştirici adayları kişiye toplasın, sürüm 2 yazsın`

### Task 5: IBAN kişi kümesi ve adaylar (3 sa)

**Files:** Modify `LS/Services/Bank/PaymentMatcher.cs` (katman 2, 160-168). Tests: `T/Services/Bank/PaymentMatcherTests.cs` (InMemory; tekil indeks yok, çift bağ tohumlanır), `T/Services/Bank/SharedIbanMatcherRelationalTests.cs` (SQL Server, `EnsureCreated` + `SharedIbanSchema`).

**Interfaces:**
- Consumes: Görev 1 `ChainOfAsync`, `new CustomerIdResolver(_db).ChainOfAsync(...)` ile (`PaymentMatcher`'ın kurucusu, DI kaydı ve 11 `new PaymentMatcher(` çağrısı değişmez); Görev 4.
- Produces: `public const int MaxCandidatePersons = 5;` `public Task<IReadOnlyList<Guid>> CandidatePersonsAsync(BankTransaction tx, CancellationToken ct)`; özel `IbanPersonsAsync` → `IReadOnlyList<(string PersonKey, Guid RowId, DateTimeOffset LastConfirmed)>`.
- Kural: `(LicenseId, IbanHash)`'in bütün bağları okunur (`LastConfirmedAt ?? CreatedAt`; sırasız `FirstOrDefault` kalkar). Her bağ `ChainOfAsync` ile çözülür; zincir geçici ya da silinmiş kopyadan geçiyorsa ya da varılan kayıt aday değilse (silinmiş, başka lisans, kırık zincir) sayılmaz (Karar 7). Sıralama bellekte, SQL'de `ORDER BY` yok (Karar 1). Kişi başına en yeni doğrulamalı bağın asıl kaydı seçilir; eşitlikte asıl kaydın `Id.ToString("N")`'i ordinal küçük olan. Kişiler `LastConfirmed` azalan, eşitlikte aynı `"N"` ordinal artan sıralanır. Görev 4'ün tablosuna: {P} ∈ küme (küme > 1 dahil) ⇒ 0,98 `username={key};iban-memory`; kullanıcı adı {P}, küme ∌ P ⇒ `conflict:username={key},iban-memory`; kullanıcı adı yok, küme tek kişi ⇒ 0,85; küme > 1 ⇒ NoProposal `iban-memory-shared:{n}`.
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
[Fact] public async Task Dar_tekil_dururken_ikinci_bag_2601()                   // (koruma) S2b şeması: aynı özete ikinci müşteri ⇒ 2601, dar indeksin adı
```

- [ ] **Step 2: Koş** — `T(PaymentMatcherTests)` ve `S(SharedIbanMatcherRelationalTests)`. Beklenen: derleme hatası (`CandidatePersonsAsync` yok), sonra küme testleri ve `Iki_kisiye_bagli_ozet_her_kosuda_ayni_sirayla_okunur` düşer; `Dar_tekil_dururken_ikinci_bag_2601` (koruma) geçer.
- [ ] **Step 3: Uygula** — kural; kanıt değişmeden `username={key};iban-memory` (küme büyüklüğü kanıta girmez); sınıf özetine kümenin dar indeks dururken prod'da tek kişi olduğu, kodun S3b şemasına hazır olduğu.
- [ ] **Step 4: Yeniden koş** — `T(PaymentMatcherTests|PaymentMatchReconcilerTests)`; `S(SharedIbanMatcherRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): IBAN'ı kişi kümesi olarak oku, adayları sırala`

### Task 6: Beş kilit (3,5 sa)

**Files:** Modify `LS/Services/Bank/PaymentMatcher.cs` (130), `LS/Services/Bank/PaymentMatchReconciler.cs` (439, 398-401, 575-582, 551, sınıf özeti 74-89), `LS/Services/Bank/BankMatchSweepJob.cs` (149-150, sınıf özeti). Tests: `PaymentMatcherTests`, `PaymentMatchReconcilerTests`, `BankMatchSweepJobTests` (InMemory), `T/Services/Bank/BankDecisionLockRelationalTests.cs` (SQL Server, `EnsureCreated`; desen `IbanReevaluationRelationalTests`).

**Interfaces:** Consumes `PaymentMatch.Decision`. Produces: yok (davranış).

| # | Yer | Ek koşul |
|---|---|---|
| K1 | `MatchOnceAsync` insan kararı dönüşü | `\|\| match.Decision != PaymentMatchDecision.None` ⇒ dokunmadan dön |
| K2 | `LinkAsync` bayat seçim | `\|\| match.Decision != PaymentMatchDecision.None` ⇒ `StaleCandidateException` (yeniden deneme K3 ile eler) |
| K3 | `CandidatesAsync` `taken` | `\|\| m.Decision != PaymentMatchDecision.None` |
| K4a | `ReevaluateSameIbanAsync` seçimi | `&& m.Decision == PaymentMatchDecision.None` |
| K4b | `ReevaluateUndecidedAsync` dönüşü | `&& row.Decision == PaymentMatchDecision.None` (K1'in döndürdüğü `Proposed` sayılmasın) |
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

- [ ] **Step 2: Koş** — `T(PaymentMatcherTests|PaymentMatchReconcilerTests|BankMatchSweepJobTests)` ve `S(BankDecisionLockRelationalTests)`. Beklenen: beş InMemory testi ve üç ilişkisel test düşer (kilit yok; kod tam adla yazılır, Step 4'teki `git grep` beş kilidi bulsun).
- [ ] **Step 3: Uygula** — tablo; sınıf özetlerine kilidin tasarım §2.4 olduğu, S2b'de prod'da `Decision ≠ None` satır olmadığı (okuyucu önce) ve `Unmatched` istisnasının S3a'da ayrı yoldan geldiği.
- [ ] **Step 4: Yeniden koş** — aynı süzgeç; `S(BankDecisionLockRelationalTests|IbanReevaluationRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): karar katmanındaki havaleyi beş yolda kilitle`

### Task 7: Bağdaştırıcı: sürüme bağlı karşılaştırma, bağlantı çözümü, kaldırma (3 sa)

**Files:** Modify `LS/Services/Bank/PaymentMatchReconciler.cs` (`ResolveWpfCustomerAsync` 379-385, `LinkAsync` 447-451, `ManualMatchOnceAsync` 304, `ReevaluateSameIbanAsync` 594, `UnmatchOnceAsync` 352). Test: `T/Services/Bank/PaymentMatchReconcilerTests.cs` (InMemory).

**Interfaces:**
- Consumes: Görev 2 `PaymentCustomerAsync`; Görev 4 `SamePersonAsync`, `PersonLevelVersion`.
- Produces: özel `Task<bool> SameProposalPersonAsync(PaymentMatch match, Guid customer, CancellationToken ct)` — öneri yoksa false; `Proposed == customer`; değilse yalnız `MatcherVersion == PersonLevelVersion` ise `Matcher.SamePersonAsync` (Karar 4). Kullananlar: `LinkAsync`'in `Status`'u ve doğrulanmış bağ koşulu, elle eşlemenin `Status`'u, yeniden değerlendirme sayacı. `ResolveWpfCustomerAsync` gövdesi `ShopperCustomerResolver.PaymentCustomerAsync(_db, licenseId, shopperId, ct)`. Kaldırma: `SupportedByIbanMemory = false;`, `MatcherVersion` korunur (Karar 2).

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Kisi_duzeyi_oneri_ayni_kisinin_obur_satirinin_dekontuyla_dogrulanir_ve_IBAN_ogretir()
// ortak GroupId: A-IG (açıklamadaki kullanıcı adı), A-TT (shopper bağlantısı) ⇒ öneri A-IG v2; onay ⇒ ConfirmedByHuman, Actual A-TT, bağ A-TT'de
[Fact] public async Task Satir_duzeyi_oneri_bugunku_gibi_siniflanir() // (koruma) aynı kurulum, satır MatcherVersion null ⇒ Contradicted, bağ öğrenilmez
[Theory, InlineData(2, PaymentMatchStatus.ConfirmedByHuman), InlineData(null, PaymentMatchStatus.ManualOnly)]
public async Task Elle_esleme_ayni_kisinin_obur_satirina(int? version, PaymentMatchStatus expected) // null satırı (koruma)
[Fact] public async Task Baglantisi_kopyada_kalmis_kisinin_dekontu_asil_kayda_baglanir() // bağlantı K→C (geçici değil), öneri C ⇒ ConfirmedByHuman, Actual C
[Fact] public async Task Kanitsiz_gecici_kopyadaki_baglantinin_dekontu_bagdastirilmaz()   // ne PaymentId ne gap
[Fact] public async Task Kaldirma_surumu_korur_yeniden_hesap_2_yazar()
// açık bağlantıda: kaldırma + yeniden hesap ⇒ 2; durdurulmuş bağlantıda (yeniden hesap koşmaz) v2 satırı 2, boş sürümlü satır boş kalır;
// ikisinde de SupportedByIbanMemory false (tohum true)
[Fact] public async Task Yeniden_degerlendirme_sayaci_ayni_kisinin_obur_satirina_verilen_oneriyi_sayar() // A-TT öğrenilir, eski satır A-IG'ye ⇒ ProposedToCustomer 1
```

- [ ] **Step 2: Koş** — `T(PaymentMatchReconcilerTests)`. Beklenen: yeni testler düşer; `Satir_duzeyi_oneri_bugunku_gibi_siniflanir` ve `Elle_esleme_ayni_kisinin_obur_satirina`'nın `null` satırı (koruma) geçer.
- [ ] **Step 3: Uygula** — sınıf özetine: karşılaştırmanın satırın sürümüne bağlı olduğu, `taken` ve `LearnIbanAsync`'in satır düzeyinde kaldığı (S3b), bağlantının kanıt kurallı çözüldüğü.
- [ ] **Step 4: Yeniden koş** — `T(PaymentMatchReconcilerTests|PaymentMatchReconcileJobTests|MatchingSinkTests|AdminBankaEslestirmePageTests)`; `S(PaymentMatchReconcilerConcurrencyTests|IbanReevaluationRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): bağdaştırıcı kişi düzeyi öneriyi kişiyle karşılaştırsın, bağlantıyı asıl kayda çözsün`

### Task 8: Ölçüm süzgeci ve sürüm blokları (3 sa)

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

- [ ] **Step 2: Koş** — `T(PaymentMatchMetricsTests|AdminBankaEslestirmePageTests)` ve `S(BankDecisionLockRelationalTests)`. Beklenen: derleme hatası (`ComputeAsync` imzası), sonra iddia hataları.
- [ ] **Step 3: Uygula** — var olan `ComputeAsync` çağrıları sürüm alır (`PaymentMatchMetricsTests`'teki var olan çağrıların hepsi, Görev 3'ün eklediği dahil, 2 alır; tohum yardımcısı 2 yazar; `IbanReevaluationRelationalTests:141` 2, satırı eşleştirici yazdı; sayfa iki çağrı); `PaymentMatchSummary` özetine süzgecin gerekçesi (§4.5: süzülmezse eşik kendini doğrular) ve `OpenGaps`'in sürümsüz olduğu (kanıtsız geçici kopyadaki shopper'ın açık gap'i S2b'den sonra çözülmez, Karar 9).
- [ ] **Step 4: Yeniden koş** — aynı süzgeç; `S(BankDecisionLockRelationalTests|IbanReevaluationRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): gölge ölçümü karar katmanını ve banka onayını saymasın, sürümleri ayırsın`

### Task 9: Dekontun müşterisi: gönderim ve yeniden bağlama (4,5 sa)

**Files:** Modify `LS/Services/ShopperPayments/ShopperPaymentSubmissionService.cs` (245-305), `LS/Services/ShopperLinking/ShopperCustomerResolver.cs`, `LS/Controllers/Licenses/LicensesWpfCustomersSyncController.cs` (345-388), `LS/Controllers/Shopper/ShopperAuthController.cs` (`ResolvePendingLinksAsync` 769-794, çağıranlar 497/500 ve 591/598), `LS/Domain/Payment.cs` (93-95 özeti). Tests: `T/Services/ShopperPayments/ShopperPaymentSubmissionServiceTests.cs`, `T/Controllers/Licenses/LicensesWpfCustomersSyncControllerTests.cs`, `T/Controllers/Shopper/ShopperAuthRegisterTests.cs`, `ShopperAuthResetPasswordTests.cs` (InMemory), `T/Services/ShopperLinking/PaymentRederivationRelationalTests.cs` (SQL Server, yeni), `T/Services/ShopperPayments/PaymentSubmitConcurrencyTests.cs` (SQL Server, var olan).

**Interfaces:**
- Consumes: Görev 2 `DeriveAsync`.
- Produces (`ShopperCustomerResolver`, Karar 10 ve 11):
  - `public static Task<int> RederivePaymentsAsync(LicenseDbContext db, Guid shopperId, IReadOnlyCollection<Guid> licenseIds, CancellationToken ct)` — lisans başına `DeriveAsync`; sonuç null ise (müşterili bağlantı yok) o lisans geçilir. Süzgeç: `LicenseId`, `ShopperId`, `Source == Receipt`, `WpfCustomerId != d.CustomerId`, `db.ShopperBroadcasterLinks.Any(l => l.Id == d.LinkId && l.WpfCustomerId == d.LinkCustomerId)` ve `db.Shoppers.Any(s => s.Id == shopperId && s.DeletedAt == null)`. İlişkiselde bu süzgeç tek ham SQL UPDATE'tir (`ExecuteSqlInterpolatedAsync`; koşul tabloları `WITH (READCOMMITTEDLOCK)` ile okunur, Karar 11; EF Core LINQ tablo ipucu desteklemez). `Status`'a bakmaz, `UpdatedAt`'i ve jetonu yazmaz, izleyiciyi atlar (çağıranlar sonra `Payment` okumaz). InMemory'de (`!IsRelational()`) aynı süzgeçle izlenen yazım ve `SaveChangesAsync` yapılır. Dönüş değişen satır sayısıdır.
  - `public static Task<int> TryRederivePaymentsAsync(LicenseDbContext db, Guid shopperId, IReadOnlyCollection<Guid> licenseIds, ILogger log, CancellationToken ct)` — her hatayı yakalar (1205, zaman aşımı, programlama hatası dahil; `TryReevaluateSameIbanAsync` deseni). İzlenen `Payment` girdilerini ayırır, uyarı logu yazar ve 0 döner. Logda shopper ve lisans Id'leri, istisna türü ve yığın izi olur; ileti yazılmaz, SQL parametresi taşıyabilir. Üç çağıran bunu kullanır.
  - `public static Task ClearIfLinkChangedAsync(LicenseDbContext db, Guid paymentId, Guid shopperId, DerivedCustomer d, CancellationToken ct)` — yalnız ilişkiselde koşar. `Payments.Where(p => p.Id == paymentId && p.WpfCustomerId == d.CustomerId && !(bağlantı koşulu && shopper koşulu))` için `WpfCustomerId = null` yazar; aynı koşul, aynı ham SQL biçimi ve kilitli okuma (Karar 11).
- `ResolvePendingLinksAsync` bağladığı lisansları döner (`List<Guid>`).
- Gönderim, adım 12'de, R2 yüklemesinden SONRA ve `Payments.Add`'den hemen önce çalışır: `var derived = await ShopperCustomerResolver.DeriveAsync(_db, input.LicenseId, input.ShopperId, ct)`, ardından `WpfCustomerId = derived?.CustomerId`. Başarılı `SaveChangesAsync`'ten sonra, `derived?.CustomerId` doluysa `ClearIfLinkChangedAsync` koşar. Bu çağrı düşerse yanıt değişmez; uyarı logu yalnız Id ve istisna türü taşır (push adımı deseni).
- Yerleşim (Karar 10): geriye dönük eşleştirmede bağlanan bağlantıların shopper'ları toplanır. `retroactiveMatches` kaydı BAŞARILIYSA her biri için `TryRederivePaymentsAsync(…, [licenseId], _logger, ct)` çağrılır. Telefon doğrulaması ve parola sıfırlamada kayıt (ya da `DeletedWonAsync == false`) geçince, bağlanan lisans varsa çağrılır; silinmiş hesap yolunda çağrılmaz.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Gonderim_dekontun_musterisini_turetme_kuraliyla_yazar() // bağlantı K→P ⇒ P; UpdatedAt == CreatedAt, Source Receipt
[Fact] public async Task Gonderim_kanitsiz_gecici_kopyada_musterisiz_birakir()  // Kp→P kanıtsız ⇒ null; kanıtlı ⇒ P; bağlantısız ⇒ null
[Fact] public async Task Geriye_donuk_eslestirme_shopperin_o_lisanstaki_dekontlarini_yeniden_turetir()
// bekleyen bağlantı; aynı shopper'ın iki boş dekontu (biri Approved), başka lisanstaki dekontu, Source = Bank satırı; başka shopper'ın
// dekontu ⇒ senkron telefonla bağlar: iki dekont P, Status ve UpdatedAt aynı; öbürleri değişmez; RetroactiveMatches 1
[Fact] public async Task Telefon_dogrulamasi_baglanan_lisansin_dekontlarini_doldurur() // ShopperAuthRegisterTests:209 deseni
[Fact] public async Task Parola_sifirlama_baglanan_lisansin_dekontlarini_doldurur()    // ShopperAuthResetPasswordTests:108 deseni
[Fact] public async Task Yeniden_yazim_duserse_senkron_yaniti_degismez()
// HookedApiFactory: yeniden yazımın (InMemory yolu) kaydında, yani partiden ve bağlamadan sonraki kayıtta istisna
// ⇒ 200, RetroactiveMatches 1, bağ kayıtlı, dekont boş (bugün çağrı yok; yalıtımsız çağrı 500 dönerdi)
[Fact] public async Task Yeniden_yazim_duserse_telefon_dogrulamasi_yaniti_degismez()
// ShopperAuthRegisterTests, fikstür HookedApiFactory: bağ kaydedildikten sonraki ilk kayıtta istisna ⇒ 204, bağ kayıtlı, dekont boş
// (parola sıfırlaması aynı yalıtılmış yardımcıyı çağırır; yalıtımsız çağrı istisnayı yanıta taşırdı)
// PaymentRederivationRelationalTests (SQL Server)
[Fact] public async Task Yeniden_yazim_bagi_okuduktan_sonra_silme_commit_olursa_yazmaz()
// ReadQueryHook: bağlantı okunduktan sonra, ilk [WpfCustomerProjections] okumasından önce ayrı bağlamda gerçek ShopperPurgeService
// ⇒ dönüş 0, dekontun WpfCustomerId'si boş (WHERE Status = özgün taşıyan izlenen yazım P'yi silinmiş shopper'ın dekontuna geri yazardı)
[Fact] public async Task Yeniden_yazim_araya_giren_onayla_dusmez()
// aynı kanca, ayrı bağlamdan ExecuteUpdate Status = Approved ⇒ dönüş 1, WpfCustomerId P, Status Approved, UpdatedAt aynı
[Fact] public async Task Yeniden_yazim_bagi_okuduktan_sonra_bag_bosalirsa_yazmaz()
// aynı kanca, bağlantının WpfCustomerId = NULL (kanıt kaybı), shopper canlı ⇒ dönüş 0, dekont boş (bağlantı koşulu tek başına)
[Fact] public async Task Yeniden_yazim_bagi_okuduktan_sonra_hesap_silinirse_yazmaz()
// aynı kanca, DeleteMe'nin etkisi (Shoppers.DeletedAt, bağlantının LeftAt'i; bağın müşterisi yerinde) ⇒ dönüş 0, dekont boş
// (shopper koşulu tek başına)
// PaymentSubmitConcurrencyTests (SQL Server)
[Fact] public async Task Gonderim_cozdukten_sonra_bag_kesilirse_dekont_musterisiz_kalir()
// RaceSaveHook.Once: gönderimin kaydından hemen önce ayrı bağlamdan bağlantı WpfCustomerId = NULL (ReproveLinksAsync'in etkisi)
// ⇒ kayıttan sonra dekontun WpfCustomerId'si boş; bağlantı değişmeden kalan gönderimde P
[Fact] public async Task Gonderim_cozdukten_sonra_hesap_silinirse_dekont_musterisiz_kalir()
// RaceSaveHook.Once: DeleteMe'nin etkisi, bağ yerinde ⇒ dekont boş (shopper koşulu tek başına)
```

- [ ] **Step 2: Koş** — `T(ShopperPaymentSubmissionServiceTests|LicensesWpfCustomersSyncControllerTests|ShopperAuthRegisterTests|ShopperAuthResetPasswordTests)` ve `S(PaymentRederivationRelationalTests|PaymentSubmitConcurrencyTests)`. Beklenen: derleme hatası (`RederivePaymentsAsync` yok), sonra yeni testler düşer.
- [ ] **Step 3: Uygula** — `Payment.WpfCustomerId` özeti: gönderim yazar, iki yazıcı yeniden yazar, katılma yazmaz, okuyucu kanıt kuralıyla çözer; yazımlar koşulludur, kesilen bağı geri yazmaz (Karar 11).
- [ ] **Step 4: Yeniden koş** — aynı `T` süzgeci + `T(ShopperIdentityTakeoverTests|ShopperBroadcastersJoinTests)`; `S(PaymentRederivationRelationalTests|PaymentSubmitConcurrencyTests|WpfCustomerIdentityRaceTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(bank): dekontun müşterisini gönderimde yaz, yeniden bağlamada yeniden türet`

### Task 10: Shopper silmesi bağlantıyı asıl kayda çözer (2,5 sa)

**Files:** Modify `LS/Services/Shoppers/ShopperPurgeService.cs` (adım 2 205-220, adım 3 `projectionKeys` 233-263, 3b yorumu 296-316). Tests: `T/Services/Shoppers/ShopperPurgeServiceTests.cs` (InMemory), `ShopperPurgeServiceConcurrencyTests.cs` (SQL Server, var olan).

**Interfaces:**
- Consumes: Görev 2 `ResolveLinkAsync` (`shopper.Phone`, `PhoneVerifiedAt`; adım 6 telefonu boşaltmadan önce).
- Kural: her bağlantı (lisans, K) için anahtarlar {K} ∪ {çözülen C}; K her zaman kalır (taşınmamış satırlar, geçici kopyanınkiler). `MarkPurged` varsayılan süzgeçli projeksiyonlardan (C dahil, kopya K hariç); IBAN ve eşleşme aramaları, `purgedCustomers`, geç bağ taraması, 3a ve 3c bu kümeyle. Kanıtsız geçici kopyada C eklenmez: sahte hesabın silmesi gerçek müşterinin bağını ve kararını silerdi.
- Adım 2: `p.WpfCustomerId = null` ardından `_db.Entry(p).Property(x => x.WpfCustomerId).IsModified = true` (değer zaten boş olsa da). EF değişmeyen boş değeri UPDATE'e koymaz; silmenin okumasından sonra commit olan yeniden yazım (Görev 9) böylece yine boşalır (Karar 11). İmha kaydının sayımı okunan değerden yapılır, değişmez.

- [ ] **Step 1: Düşen testleri yaz**

```csharp
[Fact] public async Task Kopyada_kalmis_baglantinin_asil_kaydi_silinir_bagi_ve_karari_bulunur()
// K (geçici değil) → C, bağlantı K'da: C MarkPurged; C'deki bağ silinir; Proposed/Actual C kararları boşalır; K'daki bağ da silinir
[Fact] public async Task Kanitli_gecici_kopyadaki_baglanti_asil_kayda_cozulur()
[Fact] public async Task Kanitsiz_gecici_kopyadaki_baglanti_gercek_musteriye_dokunmaz() // (koruma) C MarkPurged değil, C'nin bağı ve kararı yerinde; K'nınkiler gider
// ShopperPurgeServiceConcurrencyTests (SQL Server)
[Fact] public async Task Silmenin_okumasindan_sonra_yazilan_dekont_musterisi_de_bosalir()
// dekont boş, bağlantı P'de; RaceSaveHook.Once: silmenin kaydından hemen önce ayrı bağlamdan UPDATE Payments SET WpfCustomerId = P ⇒ silmeden sonra boş
```

- [ ] **Step 2: Koş** — `T(ShopperPurgeServiceTests)` ve `S(ShopperPurgeServiceConcurrencyTests)`. Beklenen: ilk ikisi düşer (bugün kopyadaki bağlantıda hiçbir projeksiyon silinmiyor); üçüncüsü (koruma) bugünkü davranışı kilitler, geçer; ilişkisel test düşer (EF değişmeyen boş değeri yazmaz, P kalır).
- [ ] **Step 3: Uygula** — 3b yorumuna kural ve tasarım §2.5/§12 dayanağı; adım 2 yorumuna zorunlu yazımın nedeni.
- [ ] **Step 4: Yeniden koş** — `T(ShopperPurgeServiceTests|PaymentMatchReconcilerTests|LicensesWpfCustomersSyncControllerTests)`; `S(ShopperPurgeServiceConcurrencyTests|SalesFeedKvkkRelationalTests)` ya da `B`. Beklenen: geçer.
- [ ] **Step 5: Commit** — `fix(kvkk): shopper silmesi kopyada kalmış bağlantıyı kanıt kuralıyla asıl kayda çözsün`

### Task 11: Göç `BankPersonMatchingS2b` (6 sa)

**Files:** Create (üretilir) `LS/Data/Migrations/<zaman>_BankPersonMatchingS2b.cs`, `.Designer.cs` (`LicenseDbContextModelSnapshot.cs` içerik olarak değişmemeli). Test: `T/Data/BankPersonMatchingS2bMigrationTests.cs` — `…ScriptTests` (Docker'sız), `…MigrationTests` (SQL Server Developer; Express sınıfı yok, Karar 15), `internal static BankPersonMatchingS2bMigrationScenario`.

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
        BankPersonMatchingS2b.RederivePaymentCustomers.Should().Contain("[CreatedByShopper] = 1", "geçici kopyadan zincir izlenmez")
            .And.Contain("p.[Source] = N'Receipt'").And.Contain("ORDER BY x.[JoinedAt] DESC, x.[Id] DESC");
        new BankPersonMatchingS2b().DownOperations.Should().BeEmpty("göç geri alınmaz");
    }
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
- [ ] **Step 6: SQL Server senaryosunu yaz** (desen `BankDecisionsS2aMigrationTests`: önceki göç `_BankDecisionsS2a`'ya kur, tohumla, hedefi uygula; şema aynı olduğu için tohum EF ile; S2a senaryosunun yardımcıları gerekirse `internal`). `Goc_bag_ve_karari_birlikte_tasir_dekont_musterisini_yeniden_turetir` (Developer):

| Konu | Tohum ⇒ iddia |
|---|---|
| Taşınır | A: K (geçici değil, canlı) → P; K'da bağ + öğreten karar (Proposed = Actual = K, `IbanConfirmedAt`) + yalnız önerisi K olan satır. E: K3→K2→K1→P. ⇒ bağ ve satırlar P'de; bağın `CreatedAt`, `LastConfirmedAt`, `LearnedFrom`, `SourceBankTransactionId`'si, satırın `Status`, `UpdatedAt`, `MatcherVersion`'ı aynı |
| Taşınmaz | B geçici kopya, C' silinmiş geçici kopya, D silinmiş asıl kaydın kopyası, F başka lisansa yönelen kopya, G 4 adımlı zincir, H geçici ara adımlı zincir; kuralın maddeleri tek tek (C' iki maddeye birden takılır): I silinmiş, geçici olmayan kopya, J ilk ara adımı silinmiş, L ikinci ara adımı silinmiş, M ikinci ara adımı geçici zincir ⇒ bağ ve satırlar yerinde |
| Okuyucu eşliği | her durumun özetiyle yeni hareket, `PaymentMatcher.MatchAsync`, göçten önce (bağlar kopyada: okuyucunun kopya kuralı) ve sonra: A, E ⇒ `iban-memory` önerisi P; B, C', D, F, G, H, I, J, L, M ⇒ `no-signal`. Önceki koşunun satırları göçün anlık görüntülerinden önce yazılır |
| Yeniden türetme | S2a senaryosunun (a)-(j) durumları aynen + (k) bağlantı geçici kopyada, telefon kanıtlasa da ⇒ NULL; (l) geçici asıl kayıt ⇒ o; (m) geçici ara adım, telefon kanıtı yok ⇒ NULL; (n) aynı shopper'ın `Source = 'Bank'` satırı ⇒ değişmez; `UpdatedAt`, `Status` aynı |
| Eşlik | her dekontta `ShopperCustomerResolver.PaymentCustomerAsync` ile aynı; yalnız (k) ayrı: C# P, SQL NULL (Karar 9). (m)'ye kanıt tohumlanmaz: kanıtlı ara adımda C# da P verir |
| Bir kez | `MoveCopyBonds` yeniden koşulunca `CustomerIbanMemories` (Id → `WpfCustomerId`) ve `PaymentMatches` (Id → Proposed, Actual, `UpdatedAt`) anlık görüntüsü öncekiyle aynı. Dönüş değeri iddia edilmez: `ExecuteNonQuery` `@CopyMap`'e INSERT'i de sayar, kopyalar durduğu için 0 olmaz. `RederivePaymentCustomers` (tek UPDATE) yeniden koşulunca 0 satır; dar ve geniş IBAN tekili duruyor ve tekil |

- [ ] **Step 7: Hizmetlerle iddiayı yaz** — `Tasinan_bag_ve_karar_dort_yolda_bulunur` (aynı sınıf, kendi veritabanı, göçten sonra gerçek hizmetler; S2a planı Karar 5'in dört ölçümünün tersi): (1) bağlantısı K'da kalmış shopper'ın onaylı dekontu `ReconcileApprovalAsync` ile aynı özetli yeni harekete (öneri P) bağlanır ⇒ `ConfirmedByHuman`, `Actual` P, `IbanConfirmedAt` dolu, bağ P'de ve `LastConfirmedAt`'i ilerledi (tazeleme); (2) `ShopperPurgeService.PurgeAsync` bağı da kararı da bulur, P `MarkPurged`; (3) ayrı lisansta IBAN talebi (`BankSubjectRequestRelationalTests` deseni: talep → doğrulama → `EraseAsync`) taşınan kararın müşterisini ve `IbanConfirmedAt`'ini boşaltır; (4) ayrı lisansta P'ye verilmiş başka elle eşlemenin kaldırılması bağı silmez (kalan doğrulama taşınan karardır). B'de dekont bağdaştırılmaz, silme yalnız kopya satırlarını bulur, P'nin bağı ve projeksiyonu dokunulmaz.
- [ ] **Step 8: Derle ve koş** — `B` 0 hata; `S(BankPersonMatchingS2bMigration|BankDecisionsS2aMigration)` ya da CI.
- [ ] **Step 9: Commit** — `feat(bank): S2b göçü kopyada kalmış bağı kararla birlikte taşısın, dekont müşterisini yeniden türetsin`

### Task 12: Banka satırı korumaları: gap, `payments/sync`, panel (2 sa)

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

### Task 13: Gölge sıfırlamanın reddi (2,5 sa)

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
[Fact] public async Task Ayni_kimlige_yeni_parola_reddedilmez() // (koruma)
[Fact] public async Task Karar_katmanli_lisansta_kimlik_degisimi_red_mesajini_gosterir() // AdminObifinPageTests: ShadowResetRefusedMessage, onay metni değil
// ObifinConnectionServiceConcurrencyTests (SQL Server)
[Fact] public async Task Silmeden_once_karar_yazilirsa_sifirlama_reddedilir_hicbir_sey_silinmez() // interleave: Decision = N'Approved', UpdatedAt ilerler
[Fact] public async Task Silmeden_once_banka_odemesi_ve_karar_yazilirsa_reddedilir()              // interleave: Source = 'Bank' Payment + bir eşleşmenin UPDATE'i
```

- [ ] **Step 2: Koş** — `T(ObifinConnectionServiceTests|AdminObifinPageTests)` ve `S(ObifinConnectionServiceConcurrencyTests)`. Beklenen: derleme hatası (`ShadowResetRefusedException` yok), sonra iddia hataları (iki ilişkisel test dahil: silme yapılır); `Ayni_kimlige_yeni_parola_reddedilmez` (koruma) geçer.
- [ ] **Step 3: Uygula** — `UpsertAsync` özetine (300-304) red kuralı ve §13 dayanağı.
- [ ] **Step 4: Yeniden koş** — aynı süzgeç; `S(ObifinConnectionServiceConcurrencyTests)` ya da `B`. Beklenen: geçer; var olan `Eszamanli_eslestirme_yazisiyla_carpisan_onayli_sifirlama_tazeleyip_yine_siler` (karar yok) değişmeden.
- [ ] **Step 5: Commit** — `fix(bank): karar ya da banka onayı olan lisansta kimlik değişimi gölge veriyi silmesin`

### Task 14: Son doğrulama, işletim notu ve PR gövdesi (1,5 sa)

**Files:** Modify `deploy/README.md` (S2a bloğunun altına S2b bloğu), `T/Services/Bank/BankQuestionsDeployTests.cs` (`README_komutlari_ve_geri_alma_uyarisi_var`). **Interfaces:** Consumes Görev 1-13. Produces: PR'a hazır dal (push ve PR sahibin onayıyla).

- [ ] **Step 1: Düşen README testi** — `README_komutlari_ve_geri_alma_uyarisi_var`'a `.And.Contain("Banka Faz 2 S2b tabanı", "geri alma bölümünde S2b bloğu olmalı")`; `T(BankQuestionsDeployTests)`. Beklenen: düşer (blok yok).
- [ ] **Step 2: README bloğu** — **UYARI — Banka Faz 2 S2b tabanı:** göç yalnız veri yazar, şema aynı, otomatik geri alma güvenli; S2a imajı bu veriyle çalışır. Pencerenin dört kalıntısı var. Sayımlar salt okunurdur ve S2b yeniden yayınlanmadan önce koşulur:
  - (1) v2 satırı S2a kuralıyla işlenir. O imajın yeniden hesabı (IBAN hafızası değişince, kaldırmada) satır düzeyi sonucu `MatcherVersion = 2` ve bayat `SupportedByIbanMemory` ile bırakır (S3a okur). Bağdaştırması ve elle eşlemesi v2 satırını satır düzeyinde sınıflar: aynı kişinin öbür satırına giden dekont `Contradicted` yazılır, güncel bloğun çelişkisi şişer. Sayım: `SELECT Status, COUNT(*) FROM PaymentMatches WHERE MatcherVersion = 2 AND UpdatedAt >= '<geri alma anı>' GROUP BY Status`. Çelişki payı aynı sorgudur, `AND Status IN ('Contradicted','ManualOnly') AND DecidedAt >= '<geri alma anı>'` eklenir.
  - (2) Pencerede gönderilen dekont müşterisiz kalır (S2a imajı gönderimde yazmaz, göç bir daha koşmaz): temkinli yön, S3a'da fazladan soru. Sayım: `SELECT COUNT(*) FROM Payments WHERE ShopperId IS NOT NULL AND Source = 'Receipt' AND WpfCustomerId IS NULL AND CreatedAt >= '<geri alma anı>'`.
  - (3) S2a imajının shopper silmesi bağı ve kararı bağlantının Id'siyle arar, göçün asıl kayda taşıdığını bulmaz. İleri dönünce yeniden uygulamak da bulmaz, çünkü silme bağlantıları siler. Bu yüzden pencerede shopper silmesi ve IBAN talebi uygulanmaz: ikisi de yalnız yönetici eliyle yapılır, talep bekler, S2b yeniden yayınlanınca işlenir. Uygulandıysa pencerenin silmeleri şuradan listelenir: `SELECT DISTINCT SubjectId FROM DataDestructionRecords WHERE [Trigger] = 'shopper-purge' AND OccurredAt >= '<geri alma anı>'`. Bunlardan merge öncesi listede (sorgu 3d) olanların her biri için K'yı gösteren geçici bir bağlantı satırı eklenir (`Platform`, `Username` yer tutucu). Silme, S2b yayındayken yeni talep satırıyla yeniden uygulanır; S2b silmesi K'yı asıl kayda çözer ve bağlantıyı yine siler.
  - (4) O imajın bağdaştırması, bağlantısı kopyada kalmış kişide kararı (yeni IBAN'da bağı da) kopyaya yazar. Sayım: `SELECT (SELECT COUNT(*) FROM PaymentMatches pm JOIN WpfCustomerProjections c ON c.Id = pm.ActualWpfCustomerId AND c.LicenseId = pm.LicenseId WHERE c.MergedIntoId IS NOT NULL AND pm.DecidedAt >= '<geri alma anı>') AS Kararlar, (SELECT COUNT(*) FROM CustomerIbanMemories m JOIN WpfCustomerProjections c ON c.Id = m.WpfCustomerId AND c.LicenseId = m.LicenseId WHERE c.MergedIntoId IS NOT NULL AND m.CreatedAt >= '<geri alma anı>') AS Baglar`. Sıfırdan büyükse S2b yeniden yayınlandıktan sonra `MoveCopyBonds` metni bir kez elle koşulur; kural okuyucununkiyle aynıdır, tekil indekslere takılmaz. `RederivePaymentCustomers` elle koşulmaz: kanıtlı geçici kopyanın değerini de NULL'a çeker.

  Sonra `T(BankQuestionsDeployTests)`: geçer.
- [ ] **Step 3: InMemory tam koşu** — `T(x)` süzgeçsiz. Beklenen: 0 hata; yük altında düşen varsa tek başına yeniden koş, sonucu yaz.
- [ ] **Step 4: Model ve derleme** — Görev 11 Step 5'in `has-pending-model-changes` komutu "No changes"; `cd <WT> && DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.sln --nologo -c Release` 0 hata, yeni uyarı yok.
- [ ] **Step 5: Davranış taraması** — `cd <WT> && git log --stat --format= a876a158..HEAD -- OrderDeck.App OrderDeck.Core OrderDeck.Licensing Extension` boş. `a876a158` bu planın ilk commit'idir; S2b WPF'e dokunmaz, B-W0 tabanda. `cd <WT> && git grep -n "PaymentMatchDecision.None" -- 'OrderDeck.LicenseServer/Services/*.cs'` yalnız beş kilidi, ölçümü ve sıfırlama reddini gösterir.
- [ ] **Step 6: SQL Server kanıtı** — yerelde `S(...)` ya da CI; yeşil olmadan PR hazır sayılmaz: `BankDecisionLockRelationalTests`, `SharedIbanMatcherRelationalTests`, `PaymentRederivationRelationalTests`, `PaymentRederivationRcsiRelationalTests`, `PaymentSubmitConcurrencyTests`, `BankPersonMatchingS2bMigrationTests`, `ObifinConnectionServiceConcurrencyTests`, `IbanReevaluationRelationalTests`, `PaymentMatcherConcurrencyTests`, `PaymentMatchReconcilerConcurrencyTests`, `BankDecisionsS2aMigrationTests` (+Express), `CustomerIdentityMergerRelationalTests`, `ShopperPurgeServiceConcurrencyTests`, `BankSubjectRequestRelationalTests`, `WpfCustomerIdentityRaceTests`.
- [ ] **Step 7: Commit** — `docs(bank): S2b geri alma penceresinin kalıntılarını işletim notuna yaz`

**PR gövdesine.** Sahibe bildirim:
- Tasarımdan ayrılan ve açık kalan yerler "Kararlar"dadır (özellikle 4, 7, 8, 10, 11).
- Saat 25'ten ≈ 40'a çıkar (Karar 15; §15 ayrı `docs(bank)` işinde değişir).
- Geri alma penceresinde shopper silmesi ve IBAN talebi bekletilir (README kalıntı 3).
- Tek istemci etkisi: `payments/sync` onaylı dekontta dört alanı artık ezmez (yalnız gönderilmemiş eski satırlar etkilenir).
- Plandan sapma: `BankSubjectScrub` kararsız öneriyi boşaltırken IBAN desteğini de boşaltır (Karar 2'nin silme eşi; KVKK silmesi ve IBAN talebinin ortak kuralı); prod sonucu değişmez, S2b'den önce bayrak hep false.
- Plandan sapma (Görev 8-13; gerekçeli ve zararsız): (a) elle eşleme bildirimi "bu müşteriye ya da aynı kişinin öbür kaydına" der; bunun için Görev 8'in dosya listesinde olmayan `ManualMatchResult.cs` de değişti. (b) Gönderimde müşteri çözümü düşerse yüklenen PDF geri silinir (`Musteri_cozumu_duserse_yuklenen_pdf_geri_alinir`). (c) `Karar_katmanindaki_satir_sayilmaz_dekontla_kapanan_sayilir` `Enum.GetValues` ile `Unmatched`'ı da kapsar. (d) Görev 13'ün iki ilişkisel interleave'i ham SQL değil EF ile yazıldı; değer dönüştürücü iki yanda aynı. (e) 12b uyarı logu Id ve istisna türüne ek olarak yığın izi taşır; ileti ve PII yok.
- Ölçüm: deploy anında güncel (kişi düzeyi) kutu neredeyse boş başlar, sayaçlar düşmüş görünür; S2b öncesi satırların kararları "Satır düzeyi (eski)" kutusunda sayılır (tasarım gereği, §4.5).
- Faz 2 satırı tasarım §4.5'e ("kapı gibi gösterilmez") uyduruldu: eşik tutunca yeşil renk ve "✓ eşik sağlandı" yok, satır hep nötr; "· eşik sayısına ulaştı" ve "bilgi; otomatik onayın kapısı değil (§16-6)" yazar (`Faz2_esigi_tutunca_satir_kapi_gibi_gosterilmez`). Yeşil ✓ Faz 1'in görünümüydü (o zaman eşik kapıydı); istenirse tek satırla geri gelir. Kod özetleri (`PaymentMatchMetrics`) "kapı değil, kapı §16-6" der.
- Sahip kararı: IBAN talebi, taşınabilir kopyada kalmış bağdan doğan v2 IBAN önerisini silmez (Karar 7; tek yarış yeter, sayısı deploy sonrası sorgusunda). S2b'de `EraseAsync`'e okuyucunun zincir kuralı eklenir ya da S4'te kalır.

Sorgular salt okunur, sahip koşar. `<map>` yerine şu başlık yazılır: `;WITH map (CopyId, LicenseId, CanonicalId) AS (<Görev 11 Step 4'teki @CopyMap SELECT'i, göç dosyasından aynen>)`.
- **Merge'den önce:**
  - (1) `SELECT Currency, COUNT(*) FROM BankTransactions GROUP BY Currency`. Harf duyarsız TL/TRY dışı bir değer çıkarsa (ör. kırpılmış ad) kural merge'den önce ona göre genişletilir, tahminle değil. K-6 payı: `SELECT (SELECT COUNT(*) FROM PaymentMatches pm JOIN BankTransactions t ON t.Id = pm.BankTransactionId WHERE pm.IbanConfirmedAt IS NOT NULL AND UPPER(LTRIM(RTRIM(t.Currency))) NOT IN ('TL','TRY')) AS Dogrulayan, (SELECT COUNT(*) FROM CustomerIbanMemories m JOIN BankTransactions t ON t.Id = m.SourceBankTransactionId WHERE UPPER(LTRIM(RTRIM(t.Currency))) NOT IN ('TL','TRY')) AS Ogreten`. 0'dan büyükse sahip karar verir (Görev 3).
  - (2) Taşınacaklar: `<map> SELECT (SELECT COUNT(*) FROM CustomerIbanMemories m JOIN map ON map.CopyId = m.WpfCustomerId AND map.LicenseId = m.LicenseId) AS Baglar, (SELECT COUNT(*) FROM PaymentMatches pm WHERE EXISTS (SELECT 1 FROM map WHERE map.LicenseId = pm.LicenseId AND map.CopyId IN (pm.ProposedWpfCustomerId, pm.ActualWpfCustomerId))) AS Eslesmeler`. Taşınmayacak kopya bağları (geçici, silinmiş, kırık zincir): `SELECT COUNT(*) FROM CustomerIbanMemories m JOIN WpfCustomerProjections c ON c.Id = m.WpfCustomerId AND c.LicenseId = m.LicenseId WHERE c.MergedIntoId IS NOT NULL`, eksi `Baglar`.
  - (3) Bağlantısı kopyada kalmış shopper, geçici olup olmamasına göre: `SELECT c.CreatedByShopper, COUNT(*) FROM ShopperBroadcasterLinks l JOIN WpfCustomerProjections c ON c.Id = l.WpfCustomerId AND c.LicenseId = l.LicenseId WHERE c.MergedIntoId IS NOT NULL GROUP BY c.CreatedByShopper`.
  - (3b) Geçici kopyadaki bağlantının çözülmeyecek gap'i ve dekontu (Karar 9). Kanıt SQL'de sınanamadığı için bu bir üst sınırdır: `SELECT COUNT(DISTINCT g.Id) AS AcikGap, COUNT(DISTINCT CASE WHEN p.Status = 1 THEN p.Id END) AS OnayliDekont FROM ShopperBroadcasterLinks l JOIN WpfCustomerProjections c ON c.Id = l.WpfCustomerId AND c.LicenseId = l.LicenseId JOIN Payments p ON p.ShopperId = l.ShopperId AND p.LicenseId = l.LicenseId AND p.Source = 'Receipt' LEFT JOIN PaymentMatchGaps g ON g.PaymentId = p.Id AND g.ResolvedAt IS NULL WHERE c.MergedIntoId IS NOT NULL AND c.CreatedByShopper = 1`.
  - (3c) Bağı ve bağlantısı aynı kopyada kalmış kişi (Karar 7): `SELECT c.CreatedByShopper, COUNT(*) FROM CustomerIbanMemories m JOIN WpfCustomerProjections c ON c.Id = m.WpfCustomerId AND c.LicenseId = m.LicenseId WHERE c.MergedIntoId IS NOT NULL AND EXISTS (SELECT 1 FROM ShopperBroadcasterLinks l WHERE l.LicenseId = m.LicenseId AND l.WpfCustomerId = m.WpfCustomerId) GROUP BY c.CreatedByShopper`. Tek yarışlı öneri nüfusu (Karar 7) merge'den önce yoktur; onu deploy sonrası sorgusu sayar.
  - (3d) Geri alma onarımının listesi (README kalıntı 3). Sonuç depo dışında saklanır, PR'a yazılmaz: `<map> SELECT l.ShopperId, l.LicenseId, l.WpfCustomerId FROM ShopperBroadcasterLinks l JOIN map ON map.CopyId = l.WpfCustomerId AND map.LicenseId = l.LicenseId`.
  - (4) `Contradicted` kalacak aynı kayıt, iki yönde: `<map> SELECT COUNT(*) FROM PaymentMatches pm LEFT JOIN map mp ON mp.CopyId = pm.ProposedWpfCustomerId AND mp.LicenseId = pm.LicenseId LEFT JOIN map ma ON ma.CopyId = pm.ActualWpfCustomerId AND ma.LicenseId = pm.LicenseId WHERE pm.Status = 'Contradicted' AND COALESCE(mp.CanonicalId, pm.ProposedWpfCustomerId) = COALESCE(ma.CanonicalId, pm.ActualWpfCustomerId)`.
  - (5) Satır sayıları: S2a PR gövdesindeki sorgu; göç bütçesi S2a ölçümüyle karşılaştırılır.
  - (6) Bilgi, merge kapısı değil: `SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()`. Büyük olasılıkla 1 (Karar 11: DB `EnsureCreated()` ile kuruldu); Karar 11'in iki yazımı koşul tablolarını kilitli okur, sonuç ne olursa olsun davranış aynıdır.
- **Deploy sonrası:** `SELECT MatcherVersion, COUNT(*) FROM PaymentMatches WHERE CreatedAt >= '<deploy>' GROUP BY MatcherVersion` yalnız 2; kopya bağı sayısı = (2)'nin taşınmayanı; S2a'nın "yanlış bağ" sorguları (1) ve (3) 0; `SELECT COUNT(*) FROM PaymentMatches WHERE Decision <> 'None'` = 0; `SELECT COUNT(*) FROM Payments WHERE Source <> 'Receipt'` = 0. Taşınabilir kopyada kalmış bağdan doğan öneri (Karar 7; IBAN talebi silmez, sonradan yarışla doğar): `<map> SELECT COUNT(*) FROM PaymentMatches pm JOIN BankTransactions t ON t.Id = pm.BankTransactionId WHERE pm.MatcherVersion = 2 AND (pm.Layer = 'IbanMemory' OR pm.SupportedByIbanMemory = 1) AND EXISTS (SELECT 1 FROM CustomerIbanMemories m JOIN map ON map.CopyId = m.WpfCustomerId AND map.LicenseId = m.LicenseId WHERE m.LicenseId = pm.LicenseId AND m.IbanHash = t.CounterpartyIbanHash AND map.CanonicalId = pm.ProposedWpfCustomerId)` = 0.
