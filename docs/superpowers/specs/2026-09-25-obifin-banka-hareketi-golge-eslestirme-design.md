# Obifin Banka Hareketi Çekimi + Gölge Modda Ödeme Eşleştirme — Tasarım (Faz 1)

**Tarih:** 2026-09-25 · **Karar sahibi:** Burak · **Durum:** onaylı (sohbette, 2026-09-25)
**Öncül:** 2026-09-17 tasarım turu (eşleştirme kararları, emniyet kuralları, elenen fikirler) —
`memory/project_parasut_entegrasyonu.md`. Veri kaynağı Paraşüt'ten **Obifin**'e değişti; eşleştirme
motoru kararları aynen geçerli.

## 1. Amaç ve kapsam

**Amaç:** Yayıncının banka hesabına gelen EFT/havale/FAST hareketlerini Obifin üzerinden otomatik
çekmek ve her gelen hareketi hangi müşterinin ödemesi olduğuna dair bir **öneriye** bağlamak; bu
öneriyi bugünkü elle dekont onayıyla karşılaştırıp doğruluğu ölçmek. Faz 1'in çıktısı **ölçülmüş
doğruluk**tur, otomatik onay değildir.

**Faz 1 kapsamı (pilot):**
- Yalnız EMAR GLOBAL lisansı, yalnız QNB (ileride diğer bankalar aynı yoldan).
- Obifin web servis kimliği ve QNB banka bağlantısı **admin sayfasından** girilir (yayıncı ekranı yok).
- Çekim işi, veri modeli, üç katmanlı eşleştirme, insan kararıyla karşılaştırma, admin "Banka
  eşleştirme" sayfası ve ölçüm.
- Otomatik onay **kapalı**; hiçbir öneri `Payment` durumunu değiştirmez.

**Kapsam dışı (Faz 2, ayrı spec):** otomatik onay (tam tutar kuralı, `PaymentExpectation`, fazla
ödeme → bakiye, geri alma → IBAN hafızası silme), çok kiracılı self-servis onboarding, panel
ekranları, müşteri uygulamasında IBAN/açıklama yönergesi, Obifin dışı kaynaklar (ekstre dosyası).

## 2. Obifin API — doğrulanmış gerçekler (doküman v1.03.04 + Postman v1.03.06 + 25.09 demo ölçümü)

- Kimlik: her istekte header `KullaniciAdi` (e-posta), `Sifre`, `APIKey`. Kimlikler **müşteri
  (lisans) başına ayrı web servis**; Obifin elle açar (API'de müşteri oluşturma ucu yok). İstekler
  yalnız beyaz listedeki IP'den (prod VPS'in sabit IP'si — repo dışı notlarda) kabul edilir; yerel geliştirme Obifin'e bağlanamaz.
- Banka bağlama: `POST /webservis/bankaapi/ekle/{bankakodu}/` (34 banka). QNB için `qnb`
  (KullaniciAdi + Sifre + Url = Maestro Core Ekstre servisi) ya da `qnbapi` (ClientId/ClientSecret +
  token). Bankanın **kurumsal web servis kimliği** gerekir; internet bankacılığı şifresi değil.
  `bankaapi/liste`, `bankaapi/guncelle/{id}`, `bankaapi/sil`, `bankaapi/bankakodlari`.
- Hesaplar: `POST /webservis/hesaplar/hesaplistesi/` → `Id`, `BankaKodu`, `BankaApiId`, `HesapNo`,
  `IBAN`, `ParaBirimi`, `Bakiye`, `GuncellemeTarihi` (Obifin'in bankadan son çekimi), `Durum`
  (1 = aktif sorgulanıyor), `BildirimNotu` (banka kimliği hatalıysa açıklama).
- Hareketler: `POST /webservis/hesaphareketleri/hesaphareketleriliste/` — zorunlu
  `BaslangicTarihi`/`BitisTarihi` (`YYYY-MM-DD`), **aralık ≤ 31 gün** (aşınca
  `Hata: ["Secilen tarih araligi 31 gunden fazla olamaz!"]`); `BaslangicHareketId` → yalnız
  `Id > değer` (tarih penceresi yine uygulanır, 25.09'da doğrulandı); `SayfaBasinaKayitSayisi`
  **tavan 1000** (2000 istenince 1000 döndü); `SayfaNo`; `ToplamSayfaSayisi`, `ToplamKayitSayisi`
  (tavana takılabilir, güvenilmez); `Hata[]` boş liste = başarı. Liste **zaman sıralı değil**;
  `Id` artan ve tekil.
- Hareket alanları: `Id`, `HesapId`, `IslemNo`, `IslemZamaniDT` (`YYYY-MM-DD HH:mm:ss`, TR yerel),
  `Aciklama`, `IslemAciklama`, `IslemKodu`, `OrtakIslemTipi` (Obifin normalize tipi), `Tutar`,
  `TutarEksiArti` (işaretli: `+` gelen, `−` giden), `ParaBirimi`, `Bakiye`, `KarsiHesapIBAN`
  ("dolu gelmişse kesinlikle karşı taraf IBAN'ı"), `GonderenAdi`, `AlacakliVKN`/`BorcluVKN`/
  `AmirVKN`/`LehdarTCKN`, `AliciAdi`, `GonderenBanka`/`GonderenSube`, `MusteriAciklamasi`,
  `ReferansNo`, `MakbuzNo`, `EkDetay1-4`, `BankaKodu`, `IBAN` (bizim hesap).
- Demo ölçümü (1.000 hareket, 534 gelen): gelen hareketlerde `KarsiHesapIBAN` doluluğu bankaya
  göre — garanti 114/114, ziraat 25/25, yapikredi 113/124, denizbank 88/131, vakifbank 2/2,
  akbank 8/78, **isbank 0/30, qnb 0/17, teb 0/11**. `GonderenAdi` **hiçbir bankada dolu değil**;
  ad açıklamanın içinde. `Aciklama` %100 dolu. QNB demo gelenlerinin hepsi POS tahsilatıydı
  (`IslemKodu = CCP`); QNB EFT/havale açıklama kalıbı gerçek hesapta ölçülecek.
- **Webhook yok** → çekme (polling).
- Fatura: aylık her 1.000 hareket 300 TL (kademeli, IBAN ayrımı yok, sert tavan yok). "Hareket"in
  gelen+giden tüm hareketler mi olduğu Obifin'den teyit edilecek (§10).

## 3. Veri modeli (hepsi `LicenseId` ile, `LicenseDbContext`)

- **`ObifinConnection`** — lisans başına en çok bir. `UserCode` (e-posta), `PasswordProtected`,
  `ApiKeyProtected` (ikisi de `IDataProtectionProvider`, Netgsm kalıbı), `BaseUrl`, `Status`
  (`Unverified | Verified | Failed | Disabled`), `LastVerifiedAt`, `LastPolledAt`,
  `LastObifinTransactionId` (imleç, `long?`), `LastError` (≤ 500), `BackfillCompletedAt`,
  `CreatedAt`, `UpdatedAt`. Şifre/API key görünüme dönmez, yalnız "kayıtlı" bayrağı.
- **`BankConnection`** — `ObifinConnectionId`, `BankaKodu` (≤ 32), `BankaApiId` (Obifin'in verdiği),
  `Label` (≤ 80), `Status` (`Active | Failed | Removed`), `LastError`, `CreatedAt`. Banka web
  servis kimlikleri **saklanmaz**: admin girer → sunucu `bankaapi/ekle/{banka}` çağırır → yanıttan
  `BankaApiId` alınır → kimlikler bellekten düşer. Hata olursa yeniden girilir.
- **`BankAccount`** — `BankConnectionId`, `ObifinAccountId`, `BankaKodu`, `IbanMasked`
  (`TR12…345`, ≤ 40), `IbanHash` (§7), `Currency` (3), `Active` (Obifin `Durum`),
  `LastBankSyncAt` (`GuncellemeTarihi`), `NotificationNote` (`BildirimNotu`, ≤ 500),
  `RefreshedAt`. Tekil: (`LicenseId`, `ObifinAccountId`).
- **`BankTransaction`** — `ObifinId` (`long`, tekil: (`LicenseId`, `ObifinId`) — idempotent yazım),
  `BankAccountId`, `BankaKodu`, `Direction` (`Incoming | Outgoing`, `TutarEksiArti` işaretinden),
  `Amount` (`decimal(18,2)`, mutlak), `Currency`, `OccurredAt` (`IslemZamaniDT`, Europe/Istanbul →
  UTC), `Description` (`Aciklama`, ≤ 512), `TransactionCode` (`IslemKodu`, ≤ 32),
  `CommonType` (`OrtakIslemTipi`, ≤ 64), `BankReference` (`IslemNo`, ≤ 64),
  `CounterpartyIbanHash` (§7, null olabilir), `CounterpartyIbanMasked`, `CounterpartyName`
  (`GonderenAdi`, ≤ 160), `CounterpartyTaxIdHash` (VKN/TCKN, dolu olan; §7), `RawJson`
  (`nvarchar(max)`, tanı; 90 gün), `FetchedAt`, `DescriptionPurgedAt`. Gelen **ve** giden hepsi
  yazılır (Burak'ın isteği); eşleştirme yalnız `Incoming`.
- **`PaymentMatch`** — `BankTransactionId` (tekil), `ProposedWpfCustomerId` (null = öneri yok),
  `Layer` (`UsernameInDescription | IbanMemory | NameAmount | None`), `Confidence` (`decimal(4,3)`),
  `Evidence` (≤ 500; eşleşen token/hafıza kaydı/ad benzerliği), `Status`
  (`Proposed | ConfirmedByHuman | Contradicted | NoProposal | ManualOnly`), `PaymentId` (insan
  kararının bağlandığı dekont; null olabilir), `ActualWpfCustomerId` (insan kararındaki müşteri),
  `DecidedAt`, `CreatedAt`, `UpdatedAt`.
- **`CustomerIbanMemory`** — `WpfCustomerId`, `IbanHash`, `IbanMasked`, `LearnedFrom`
  (`ManualMatch | HumanApproval`), `SourceBankTransactionId`, `CreatedAt`, `RevokedAt`,
  `RevokedReason`. Tekil aktif: (`LicenseId`, `IbanHash`) — bir IBAN aynı anda tek müşteriye.
  Geri alma **satırı siler** (onaylı kural: yanlış öğrenip sessizce tekrarlamayı önlemek için
  `RevokedAt` yalnız denetim kopyasında; aktif sorgular yalnız silinmemiş satırları görür).
- **`PaymentMatchGap`** — insan kararı olan ama aday hareketi bulunamayan dekont: `PaymentId` (tekil),
  `Reason` (`NoCandidate | AmbiguousCandidates`), `CreatedAt`, `ResolvedBankTransactionId` (sonradan
  gelen hareket bağlanırsa), `ResolvedAt`. Ölçüm ve admin listesi için.
- `PaymentExpectation` **Faz 2** (onaylı ayrı tablo; Faz 1'de tutar karşılaştırması yalnız ölçüm).

## 4. Obifin istemcisi (`IObifinClient`)

- `HttpClient` "obifin", zaman aşımı 40 sn, `application/x-www-form-urlencoded`. Kimlik header'ları
  çağrı başına `ObifinConnection`'dan çözülür (bellekte kısa ömür, loglanmaz).
- Yanıt sözleşmesi: HTTP 200 + `Hata: []` = başarı; `Hata` doluysa `ObifinApiException(messages)`;
  JSON değilse `ObifinProtocolException`. Ham gövde yalnız tanı kopyası olarak ≤ 2000 karakter
  (İYS istemcisinin 2026-09-22 dersi: **kesme ayrıştırmadan önce yapılmaz**).
- Metotlar: `ListAccountsAsync`, `ListBankConnectionsAsync`, `AddBankConnectionAsync(bankaKodu,
  form)`, `RemoveBankConnectionAsync(id)`, `ListTransactionsAsync(from, to, sinceId?, page,
  pageSize)`. Tarih aralığı > 31 gün istemcide reddedilir (sunucuya gitmeden).
- Test host: `NullObifinClient` (ApiFactory) — "not-configured"; testler `IObifinClient`'ı
  stub'lar (İYS kalıbı).

## 5. Çekim işi

**`ObifinPollJob`** — Hangfire recurring `obifin-poll`, `*/5 * * * *` (5 dk ızgarasında; mevcut
`*/5` işleri hafif). `[DisableConcurrentExecution("obifin-poll:{0}", 600)]` bağlantı (lisans)
başına; `[AutomaticRetry(Attempts = 0)]` — hata `LastError`'a yazılır ve koşu Failed görünür;
imleç ilerlemez.

Algoritma (bağlantı başına):
1. `Status == Verified` değilse çık.
2. **İlk çekim (backfill):** `BackfillCompletedAt` boşsa, bugünden geriye 90 gün, **31 günlük
   dilimlerle** (`[t−90,t−60]`, `[t−59,t−29]`, `[t−28,t]`), imleçsiz; her dilim sayfa sayfa
   (`SayfaBasinaKayitSayisi=1000`, `SayfaNo` artan; sayfa < 1000 kayıt döndüğünde dur). Bitince
   `BackfillCompletedAt` ve imleç = görülen en büyük `Id`.
3. **Artımlı çekim:** pencere `[bugün−30, bugün]` (31 gün, API tavanı) + `BaslangicHareketId =
   LastObifinTransactionId`; sayfalama aynı. Pencere geniş tutulur çünkü banka geç muhasebeleşen
   hareketi eski tarihle verebilir; imleç sayesinde maliyet yalnız yeni kayıtlar.
4. Kayıtlar `Id`'ye göre sıralanır; her kayıt `BankTransaction`'a **idempotent** yazılır
   ((`LicenseId`, `ObifinId`) tekil; çakışmada güncelleme yok, atlanır). `TutarEksiArti` işareti
   → `Direction`; `IslemZamaniDT` TR → UTC.
5. Her yeni `Incoming` hareket için eşleştirme (§6) **aynı işte** koşar; sonuç `PaymentMatch`.
6. İmleç yalnız **tüm sayfalar başarıyla yazıldıktan sonra** en büyük `Id`'ye ilerler
   (kısmi hata → imleç eski kalır, sonraki koşu tekrar dener; idempotency çift yazımı önler).
7. Sayfa 1000 dolu döndüyse (tavan) aynı koşuda ilerlemiş imleçle bir tur daha (drenaj), en çok 10 tur.

**`ObifinAccountRefreshJob`** — recurring `obifin-accounts`, `20 * * * *` (saatte bir):
`hesaplistesi` → `BankAccount` upsert (`Active`, `LastBankSyncAt`, `NotificationNote`).
`GuncellemeTarihi` ile `now` farkı **Obifin→banka gecikmesi** metriği (admin sayfasında).

**`BankDataRetentionJob`** — recurring `bank-data-retention`, `55 4 * * *`: `RawJson` 90 gün,
`Description`/`CounterpartyName` 180 gün sonra boşaltılır (`DescriptionPurgedAt`); tutar, tarih,
hash'ler kalır. `PaymentMatch.Evidence` de 180 gün.

Zamanlama notu: 04:52'de `iys-mirror-sync`, 04:47 saklama; `55 4` boş. `*/5` işleri: `sms-campaign-
recovery`, `iys-consent-push`, `iys-consent-verify`; `obifin-poll` tek bağlantıyla saniyeler sürer.

## 6. Eşleştirme (gölge)

Girdi: yeni `Incoming` hareket. Önce **dışlama**: `CommonType`/`TransactionCode` POS tahsilatı,
kart, faiz, masraf, kendi hesapları arası ise `Status = NoProposal`, `Evidence = "excluded:<tip>"`.
Dışlama listesi yapılandırma (`OrderDeck:Bank:ExcludedTransactionCodes`, başlangıç `CCP`) + gerçek
veride genişletilir; **belirsizse dışlanmaz** (gölge modda yanlış öneri ucuz, kaçırılan ölçülemez).

Normalizasyon (`BankTextNormalizer`): küçük harf (tr-TR), Türkçe harfler ASCII'ye (ı→i, ş→s, ğ→g,
ü→u, ö→o, ç→c, İ→i), `@ _ . - / : ;` ve fazla boşluk tek boşluğa; token = boşlukla bölünmüş
parçalar; ayrıca bitişik yazımlar için boşluksuz birleşik metin.

Katmanlar (sırayla; ilk kesin sonuç alınır, kalanlar kanıt olarak eklenir):
1. **Kullanıcı adı (açıklamada müşteri kodu).** Lisansın `WpfCustomerProjection.Username`
   değerleri normalize edilip açıklama tokenlarıyla **tam token** eşleştirilir; ≥ 6 karakterli
   kullanıcı adları için bitişik metinde alt dize eşleşmesi de kabul. < 4 karakter veya sözlük
   kelimesi (`stopwords.tr`) olan kullanıcı adları tam token dışında eşleşmez. Tek aday →
   `Confidence 0.90`; aynı açıklamada IBAN hafızası da aynı müşteriyi gösteriyorsa `0.98`. Birden
   fazla aday → `NoProposal`, `Evidence = "ambiguous:<n>"`.
2. **IBAN hafızası.** `CounterpartyIbanHash` doluysa `CustomerIbanMemory`'de aranır; bulunursa
   `0.85` (kullanıcı adı katmanı boşsa). QNB'de çoğunlukla boş gelecek — ölçülecek.
3. **Ad + tutar (yalnız öneri).** Açıklamadan ad adayı: büyük harfli 2–4 kelimelik diziler; müşteri
   `FullName` (WpfCustomerProjection, yoksa `ShopperBroadcasterLink` → `Shopper.FullName`) ile
   normalize Jaro-Winkler ≥ 0.92 **ve** tek aday → `0.50`. Tutar Faz 1'de karara girmez (tutarlar
   50'nin katları, ayırt edici değil — 17.09 kararı), yalnız kanıta yazılır.

Sonuç `PaymentMatch(Proposed | NoProposal)`; hiçbir yerde `Payment` yaratılmaz/değiştirilmez.

**İnsan kararı bağlama (`PaymentMatchReconciler`):** `PanelPaymentsController.Approve/Reject`
sonrası (aynı istek, best-effort, hata onayı düşürmez) — dekontun `ShopperId` →
`ShopperBroadcasterLink.WpfCustomerId`; aday hareketler: aynı lisans, `Incoming`, `Amount ==
Payment.Amount`, `OccurredAt ∈ [PaidAt − 2 gün, PaidAt + 2 gün]`, henüz bağlanmamış. Tek aday →
bağla; birden fazla → `PayerName` ~ açıklama benzerliği en yüksek olan; hiç yok → `Payment` için
`PaymentMatchGap` kaydı (dekont var, hareket yok: banka gecikmesi ya da başka hesap — metrik).
Bağlanınca: öneri müşterisi == gerçek → `ConfirmedByHuman`; farklı → `Contradicted`; öneri yoktu
→ `ManualOnly`. Onaylı dekontla bağlanan hareketin `CounterpartyIbanHash`'i doluysa
`CustomerIbanMemory(LearnedFrom = HumanApproval)` öğrenilir; ret kararı öğretmez.

**Admin "elle eşle":** hareket → müşteri seçimi (`WpfCustomerProjection` arama) → `ManualOnly` +
IBAN varsa hafızaya (`ManualMatch`); "eşlemeyi kaldır" hafıza satırını **siler**.

Faz 1'in gerçek uygulaması bu bölümden birçok yerde bilinçli olarak ayrılır ya da onu inceltir: bkz. §14.

## 7. Emniyet ve gizlilik

- IBAN/VKN/TCKN **ham saklanmaz**: `IbanHash = HMAC-SHA256(normalize(IBAN), key)`; `key` =
  `OrderDeck:Bank:HashKey` (32+ bayt, `.env`, Bitwarden'da yedek). Düz SHA yetersiz: IBAN
  entropisi düşük, sözlük saldırısına açık. Maskeli biçim (`TR12…345`) yalnız görüntü.
- Obifin kimlikleri DataProtection ile şifreli; görünümde yalnız `PasswordSet`/`ApiKeySet`; loga
  asla yazılmaz (`TenantSmsCredentials` kalıbı).
- Banka web servis kimlikleri hiç saklanmaz (§3).
- Ham JSON 90 gün, açıklama/ad 180 gün (§5); KVKK: hareket açıklamaları kişisel veri içerir;
  yayıncının müşterileri adına işlenir (mevcut dekont akışıyla aynı temel).
- Çekim hataları sessiz kalmaz: `LastError`, Failed koşu, admin sayfasında kırmızı satır.
- Yazımlar idempotent; imleç yalnız tam başarılı turda ilerler.
- Otomatik onay yok; `PaymentMatch` yalnız okunur veri üretir. Faz 2'ye geçiş §9 ölçütleriyle.
- Sözleşme notu (Burak): Obifin TCMB faaliyet izni başvurusu beklemede — sözleşmede
  "izin reddedilir/iptal olursa fesih + veri iadesi" maddesi.

## 8. Admin sayfaları (AdminCookie, mevcut Razor kalıbı: `Pages/Admin/Netgsm`)

- **`Admin/Obifin/Index`** — lisans seç (pilotta EMAR GLOBAL); Obifin kimliği gir/güncelle
  ("Doğrula" = `hesaplistesi` çağrısı → `Verified/Failed` + `LastError`); banka bağlantısı ekle
  (banka kodu seçimi + o bankanın alanları; QNB için `qnb`: kullanıcı adı, şifre, servis URL;
  `qnbapi`: ClientId/Secret/token) → `bankaapi/ekle` → `BankaApiId`; hesap listesi (`Active`,
  IBAN maskeli, `LastBankSyncAt`, `NotificationNote`); son çekim zamanı/imleç/hata; "Şimdi çek"
  düğmesi (`ObifinPollJob` kuyruğa).
- **`Admin/BankaEslestirme/Index`** — son 30 gün gelen hareketler (tarih, tutar, banka, açıklama,
  karşı IBAN maskeli), öneri (müşteri, katman, güven, kanıt), gerçek karar (bağlı dekont/müşteri,
  durum), "elle eşle" / "eşlemeyi kaldır"; üstte ölçüm kutusu (§9). Sayfalama 50.

## 9. Ölçüm ve Faz 2 ölçütü

Son 30 gün (kayan): gelen hareket sayısı; dışlanan; öneri üretilen; `ConfirmedByHuman`;
`Contradicted`; `ManualOnly` (insan eşledi, öneri yoktu); `NoProposal` ve kararsız kalan;
`PaymentMatchGap` (dekont var, hareket yok); Obifin→banka gecikmesi (medyan/maks);
katman bazında isabet. **Faz 2 (otomatik onay) önerisi için eşik:** ≥ 3 hafta, ≥ 200 insan
kararıyla bağlanmış hareket, `Contradicted / (Confirmed + Contradicted) ≤ %2`, `PaymentMatchGap`
oranı açıklanmış. Eşik sağlanmazsa katman kuralları düzeltilir, ölçüm devam eder.

## 10. Açık sorular (Obifin/QNB'ye sorulacak; spec'i bloklamaz)

1. Obifin: yeni müşteri için web servis açılış süreci ve süresi (Faz 2 için).
2. Obifin: bankadan çekme sıklığı (`GuncellemeTarihi` ile ölçülecek ama beyanı da alınsın).
3. Obifin: faturadaki "hareket" gelen+giden tüm hareketler mi; API çağrıları sayılmıyor (varsayım).
4. QNB: hesap hareketleri web servisi (`qnb` Maestro ekstre mi, `qnbapi` mi) başvurusu — müşteri
   temsilcisi; Obifin'in QNB için önerdiği tip.
5. Obifin: test hesabı (demo, 2022 verisi) gerçek hesap açılınca kapanacak mı; iki bağlantı
   (`ObifinConnection`) ile paralel tutulabilir.

## 11. Testler (xunit, InMemory; Obifin stub)

- İstemci: `Hata[]` → istisna; JSON değil → protokol istisnası; 31 gün üstü aralık istemcide red;
  header'lar kimlikten; ham gövde kesme yalnız tanı kopyasında.
- Çekim: backfill 90 gün → 3 dilim, sayfa döngüsü; artımlı pencere + imleç; sayfa 1000 dolu → drenaj;
  kısmi hata → imleç ilerlemez, sonraki koşu idempotent; TR saat → UTC; `Direction` işaretten;
  gelen hareket → `PaymentMatch` üretimi; `Verified` değil → çık.
- Eşleştirme: normalizasyon (Türkçe harf, `@`, bitişik yazım); tam token eşleşmesi; kısa/sözlük
  kullanıcı adı yalnız tam token; çoklu aday → `NoProposal`; IBAN hafızası; kullanıcı adı + hafıza
  → 0.98; ad+tutar yalnız 0.50; dışlama listesi (`CCP`); QNB/Garanti/Akbank açıklama kalıpları
  (demo'daki maskeli şekillerden türetilmiş sentetik örnekler; kişisel veri yok).
- Bağlama: tek aday → `ConfirmedByHuman`/`Contradicted`; çoklu → ad benzerliği; yok → gap; ret
  öğretmez; onay IBAN öğretir; elle eşleme öğretir, kaldırma siler (satır yok).
- Hash: HMAC, aynı IBAN aynı hash, farklı anahtar farklı hash; maskeleme.
- Saklama işi: 90/180 gün sınırları.
- Admin: yetkisiz 401; kimlik görünümde `PasswordSet` var, şifre yok; "Doğrula" akışı.
- Program.cs DI + recurring kayıtları (`Job_DI_kapsamindan_cozulur` kalıbı; slot çakışması yok).

## 12. Teslim sırası

1. **PR-1 (sunucu):** veri modeli + göç, `IObifinClient` + `NullObifinClient`, `ObifinPollJob` +
   `ObifinAccountRefreshJob` + saklama işi, `Admin/Obifin` sayfası, testler. Deploy sonrası admin
   kimlik girişi → ilk gerçek çekim (backfill 90 gün) → hareketler DB'de.
2. **PR-2 (sunucu):** eşleştirme motoru + `PaymentMatch` + reconciler + `Admin/BankaEslestirme` +
   ölçüm kutusu, testler.
3. 2–4 hafta gölge ölçüm → Faz 2 spec (otomatik onay + self-servis).

## 13. Bilinçli sınırlar

- Çok kiracılı model veri modelinde hazır (her şey `LicenseId`), ekranları yok.
- Webhook yok; 5 dk + Obifin'in banka çekim gecikmesi = fiili gecikme; ölçülüp raporlanacak.
- QNB'de karşı IBAN gelmezse IBAN hafızası pasif kalır; katman 1 (kullanıcı adı) ana yol; müşteri
  uygulamasına "açıklamaya kullanıcı adını yaz" yönergesi Faz 2'de eklenir (bugün chat/WhatsApp).
- Demo hesabı 2022 verisi; QNB EFT kalıbı ancak gerçek hesapta görülecek.

## 14. Faz 1 uygulama notları (2026-09-28)

PR-1 (#485) ve PR-2'nin kodu tasarımdan aşağıdaki noktalarda ayrılır ya da onu inceltir. Faz 2 spec'i bu bölümü esas
alır; yukarıdaki bölümlerle çelişkide bu bölüm (ve kod) geçerlidir.

**Çekim (PR-1)**
- Ham JSON saklanmadan önce adı `IBAN`, `VKN` ya da `TCKN` içeren her alan (iç içe dahil, harf duyarsız) `[redakte]`
  olur; hash ve maske redaksiyondan önce ham değerden üretilir. Ad alanları kalır.
- Durum `Failed`'a yalnız şunlarla düşer: Obifin'in reddi (`Hata` dolu) — çekimde ya da saatlik hesap yenilemede;
  çekimin ya da "Doğrula"nın çözemediği saklı kimlik; elle "Doğrula"nın her başarısızlığı. Çekimde ve hesap yenilemede
  ağ/vekil/zaman aşımı yalnız `LastError` yazar, durum `Verified` kalır (çekim koşusu Failed görünür, 5 dk sonra aynı
  imleçten dener). Banka ekleme hiçbir hatada durumu değiştirmez; sonucu belirsiz ekleme "eklenemedi" denmeden
  Obifin'deki etiketle bildirilir. Başarılı hesap yenilemesi ya da banka eklemesi `Failed`'ı yeniden `Verified`
  yapar; `Disabled`'a kanıt dokunmaz.
- Kimlik (kullanıcı kodu ya da açıkça girilen adres) değişince imleç sıfırlanır; lisansın gölge verisi (hareket, hesap,
  eşleşme, IBAN hafızası, gap, eski hesabın banka bağlantıları) yalnız admin açıkça onaylarsa silinir. Karar kayıt
  servisinde verilir; onay yoksa hiçbir şey değişmez. Yalnız parola/API anahtarı değişimi veriye dokunmaz. Çekim her
  kayıttan önce kimliği taze okur; koşu sürerken değiştiyse hiçbir şey yazmaz.
- `OrderDeck:Bank:HashKey` yoksa ya da 32 bayttan kısaysa sunucu yine açılır, banka modülü kapalıdır: `obifin-poll`,
  `obifin-accounts` ve `bank-match-sweep` zamanlanmaz (önceki kayıtları silinir), iki admin sayfası uyarıyla açılır ve
  hiçbir POST yazmaz. `bank-data-retention` anahtardan bağımsız koşar (KVKK silmesi durmaz).
- Takvim (hepsi UTC): `obifin-poll` `*/5`, `obifin-accounts` saatte bir :02, `bank-match-sweep` saatte bir :07,
  `bank-data-retention` 04:57. Çekim ile hesap yenileme tek, sabit bir kilidi paylaşır; bağlantı başına kilit yok.
- İlk çekim 90 günü (bugün dahil) üç 31 günlük dilimle alır; artımlı çekim `[bugün−30, bugün]` + imleç, en çok 10
  drenaj turu. Eşleştiriciye yalnız tutarı sıfır olmayan gelen hareket verilir.
- Ölçülmüş Obifin davranışı (25–26.09): boş pencere `Hata: []`, boş `Liste` ve `ToplamSayfaSayisi: 0` döner; son
  sayfanın ötesindeki `SayfaNo` boş değil, yeniden DOLU bir sayfa döndürür — döngü `ToplamSayfaSayisi` geldiyse onu hiç
  aşmaz, gelmediyse "sayfa dolu → devam" der ve yeni `Id` getirmeyen sayfada durur (pencere başına en çok 100 sayfa);
  `BaslangicHareketId` = `Id > X`; `SayfaBasinaKayitSayisi` ve toplamlar yanıttan yanıta dize ya da sayı gelir, ikisi
  de okunur; sayfa boyutu istenenden değil yanıttan alınır.

**Eşleştirme**
- Normalizasyon: NFKD ayrıştırması, ardından birleşik işaretler (NonSpacingMark, EnclosingMark) ve görünmez biçim
  karakterleri (Format) atlanır; elle yalnız `ı→i`; küçük harf sabit tr-TR ile (ortam kültüründen bağımsız); `a–z` ve
  `0–9` dışındaki her karakter ayırıcı; geçersiz UTF-16 birimi atmaz, ayırıcı sayılır.
- Kullanıcı adı katmanı: anahtar en az 3 karakter; 3 karakterlik anahtar yalnız tam token olarak bulunur. Kullanıcı
  adı harf/rakam sınırında parçalanır ve açıklamada token sınırına hizalı parça dizisi olarak aranır; ≥ 6 karakterli
  anahtar ayrıca açıklamadaki ardışık TAM token'ların birleşimi olarak bulunur (bitişik/ayrık yazım). Tasarımdaki
  tam dizi ve sınırsız ≥ 6 alt dize kuralı yerine budur: sınırsız alt dize komşu kelimelere ve rakamlara taşıyordu.
  `stopwords.tr` yok; yerine banka açıklamalarının kalıp kelimeleri listesi.
- Kullanıcı adı katman 1'e ancak kalıp olmayan bir harf parçası taşıyorsa girer: salt rakam (Facebook sayısal
  kimlikleri) ve yalnız kalıp kelimelerden oluşan adlar dışarıda kalır (müşteri IBAN hafızasıyla yine önerilebilir).
  YouTube satırlarının kullanıcı adı kanal kimliğidir, havale açıklamasında pratikte geçmez: katman 1 fiilen
  Instagram/TikTok ve kayıt formu kullanıcı adlarında işler.
- Ad katmanı Jaro-Winkler değil: müşterinin normalize `FullName`'inin (en az iki token) açıklamada ardışık token dizisi
  olarak geçmesi; kanıta ad değil yalnız `name:N` (token sayısı) yazılır. Aday yalnız lisansın silinmemiş müşterisidir;
  `Shopper.FullName` yedeği yok.
- IBAN hafızası yalnız hafızadaki müşteri hâlâ adaysa (silinmemiş) sayılır; kullanıcı adı katmanındaki çoklu adayı
  çözmez (belirsizlik = öneri yok); tek kullanıcı adı adayıyla çelişirse öneri yok.
- Tek dışlama kuralı: gelen değil, tutar sıfır ya da `TransactionCode` (kırpılmış, harf duyarsız)
  `ExcludedTransactionCodes`'ta. Liste yapılandırmayla yalnız genişler (`CCP` çıkarılamaz); `CommonType`'a bakılmaz.
  Eşleştirici, bağdaştırıcının aday seçimi ve ölçüm aynı kuralı kullanır; dışlama kanıttan değil hareketin
  alanlarından okunur.
- Eşleştirici `BankHasher` istemez (saklı hash'leri karşılaştırır). Çekimin eşleştiricisi kendi DI alt kapsamında koşar
  (çekim işinin bağlamında değil). `bank-match-sweep` yalnız son 30 günün, hiç eşleşme satırı olmayan gelen
  hareketlerini tarar.

**Bağlama**
- Bağdaştırma onay isteğinde değil, onaydan 20 dk sonra koşan bir Hangfire işidir; yalnız onayda zamanlanır, onayı ne
  yavaşlatır ne düşürür (zamanlama hatası yalnız loglanır). İş, ödeme hâlâ onaylıysa ve lisansın Obifin bağlantısı
  varsa koşar. Ret hiçbir şeye bağlanmaz (reddedilen dekontun gerçek müşterisi yoktur).
- Aday: aynı lisans, gelen, tutar eşit, `PaidAt ± 2 gün`, dışlanmamış, bir dekonta bağlanmamış ve başka müşteriye elle
  verilmemiş. Birden çok aday → gönderen adının en az 3 karakterli TÜM token'larını açıklamasında taşıyan TEK aday;
  seçilemezse `AmbiguousCandidates` gap'i (tasarımdaki "en yüksek benzerlik" yerine). Aday yok → `NoCandidate` gap'i.
- Gap yalnız tek adayla çözülür: yeni hareketin penceresinde tutarı tutan TEK açık gap varsa o dekontun aday seçimi
  aynı kuralla yeniden koşar ve tek aday bu hareketse bağlanır (çekim ve tarama).
- Onay IBAN'ı yalnız doğrulanmış bağdan öğretir (öneri dekontun müşterisini gösteriyorsa ya da gönderen adının tüm
  token'ları açıklamadaysa); yalnız tutar ve zamana dayanan bağ kurulur, karşılaştırılır ama öğretmez. Hiçbir öğrenme
  var olan hafıza satırını (başka müşterininki dahil) ezmez.
- Elle eşleme yalnız karara bağlanmamış gelen harekete verilir ve IBAN öğretir; dışlanan kodlu hareketi kabul eder ama
  onun IBAN'ını öğretmez. Öneriye eşit elle eşleme `ConfirmedByHuman` sayılır (öneriye bakılarak verilmiş karar; bkz.
  Ölçüm).
- Kaldırma (admin onayıyla): bu hareketten öğrenilen IBAN satırı silinir (denetim kopyası tutulmaz); dekonta bağlı
  satırda dekont açık gap'e döner (`UnlinkedByAdmin` ya da çözülmüş gap'i yeniden açılır); öneri, kaldırma
  kaydedildikten sonra yeniden hesaplanır.
- `PaymentMatch.UpdatedAt` eşzamanlılık jetonudur ve kayıtta merkezi damgalanır; çakışmada taze satırla bir kez
  yeniden denenir, ikincide iş yolu uyarıyla vazgeçer, admin yolu "çakışma" mesajı verir.
- KVKK silmesi müşterinin IBAN hafızasını siler, ona ait kanıt metinlerini boşaltır; eşleşme satırı ölçüm için kalır.

**Ölçüm**
- Pencere hareketin anına (`OccurredAt`) göre çizilir, satırın yaratılma anına göre değil; açık gap'ler gap'in
  yaratılma anına göre.
- Öneriden farklı müşteriye elle eşleme (`ManualOnly` + öneri) çelişki sayılır; dışlanan hareket yalnız "dışlanan"da
  sayılır. Katman ve gerçek müşterinin platformu bazında kırılım; silinmiş ya da bilinmeyen müşteri `?`.
- Gecikme = çekildiği an − işlem anı, medyan ve maks; 7 günü aşan (ilk geri doldurma) ve negatif farklar sayım dışı.
- Faz 2 eşiği yalnız dekont onaylı öneri kararlarından (satır bir dekonta bağlı): ≥ 200 karar ve çelişki ≤ %2, tam
  sayılarla (yuvarlı oranla değil). Panelde dekont onayı öneri görülmeden verilir; sayfadaki karar öneriye bakılarak
  verildiği için ayrı gösterilir, eşiğe girmez. Tasarımdaki "≥ 3 hafta" ve "gap oranı açıklanmış" ölçütleri kodda
  değil, karar verenin denetiminde.

**Bilinen sınırlar**
- Katman 1'de kalan yanlış pozitif biçimleri: harf+rakam kullanıcı adının rakam kuyruğu, açıklamada adın hemen
  ardından gelen tutar/tarih parçasına denk gelebilir ("ayse1000" ↔ "AYSE 1000,00"); ilk ada eşit tek token'lı
  kullanıcı adı ("ayse") o adı taşıyan her havaleye önerilir; kalıp kelimelerin bitişik yazımı ("gelenhavale") kalıp
  sayılmaz.
- `CounterpartyName` (`GonderenAdi`) eşleştirmede kullanılmıyor (demoda hiç dolu değildi); dolu gelen bankalarda Faz 2
  için ad katmanının kaldıracı.
- Eşleştiricinin çekim anında düştüğü ve 30 günden eski hareket (ilk 90 günlük geri doldurmanın başı) önerisiz kalır:
  tarama onu görmez.
- Kaldırmadan sonraki yeniden hesap düşerse satır "öneri yok" kalır: tarama satırı olan hareketi taramaz; admin yine
  elle eşleyebilir.
- Sonucu belirsiz banka eklemesinin audit satırı yok; Obifin'de açılmış bir banka kaydını yerel bağlantıya bağlayan
  arayüz de yok (etiket günlükte ve bildirimde).
