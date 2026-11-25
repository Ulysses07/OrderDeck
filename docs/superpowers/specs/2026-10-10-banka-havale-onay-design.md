# Banka Havalesi Onayı — Tasarım (Faz 2)

**Tarih:** 2026-10-10, güncel koda uyarlama 2026-11-25 · **Durum:** sahip kararları alındı (§16), inceleme bulguları işlendi (§17), 25.11 uyarlaması, denetimi ve son denetimi işlendi (§18), uygulama planı bekliyor · **Önceki:** `2026-09-25-obifin-banka-hareketi-golge-eslestirme-design.md` (Faz 1, gölge mod — prod'da)

Kaynaklar: LiveDeck `origin/master` a9729534, OrderDeck-Mobile `origin/main` 06ac3d2, OrderDeck-Shopper `origin/master` d0ff93e. Bu belge, gölge mod spec'inde "Faz 2" olarak ayrılan işin yerini alır. Bu kesitte Faz 2'nin onay kodu yok: `PaymentApprovalService`, `bank-transfers`, `BankDecisionEvent`, `BankIbanState` ve `bank-overpayment` aramaları docs dışında boş. Kod çapaları sınıf ve metot adıyla verilir; geçtiği yerde satır numarası bu kesite göredir. 2026-10-10 incelemesinin 57 bulgusu işlendi; kararlar §17'de.

## 1. Amaç ve kapsam dışı

**Amaç**
- Hiçbir havale ilk görüşte otomatik onaylanmaz. Yeni bir IBAN'dan ya da açıklamaya yazılmış kullanıcı adından kurulan her ilk eşleşmede yayıncıya "Bu havale X'in mi?" diye sorulur. Soru panelde ve WPF'te aynı anda görünür; birinde cevaplanınca ötekinden de kalkar.
- "Evet", bugünkü dekont onayıyla aynı zinciri çalıştırır ama dekont istemez. Aynı zamanda bu IBAN'ı o müşteriye öğretir.
- "Evet" nerede verilirse verilsin siparişi kargo dosyasına **sunucu** bağlar (§4.6, sahip kararı §16-8). Masaüstünde yalnız karar isteyenler (kargo ücreti eksik, alıcı ödemeli kişi, ücretsiz kargo eşiği) ve sunucunun hangi siparişin ödendiğini bilemediği onaylar (§4.6.1-3, soru 6) sorulur.
- Aynı IBAN'dan sonra gelen havaleler yalnız §5'teki dar kuralla otomatik onaylanır. En küçük şüphede yine soru sorulur.
- Eşleşmeyen ya da değerlendirilemeyen havale kaybolmaz; "Eşleşmeyen" listesine düşer ya da tarama onu yeniden dener (§3).
- Yanlış onay geri alınabilir: ödeme, tahsis, kargo bağı, fazla ve IBAN izi geri sarılır (§11.2). Havale hareketi saklandıkça (en çok 180 gün) yeniden doğru müşteriye onaylanabilir. Hareket silindikten sonra da geri alma çalışır, yalnız havale yeniden onaylanamaz. Geri alma, sorular kapalıyken ya da bağlantı durdurulmuşken de açıktır (§2.7). Sınırı verilmiş kargo kararıdır (§4.6.7, soru 8).

**Kapsam dışı**
- Obifin kiracılık kararı (§13'te iki seçenekle de uyumlu)
- Obifin kimliği değişince hareketlerin yeniden anahtarlanması: ortak kimlik spec'inin işi. Bu spec yalnız korumayı tanımlar (§13).
- Self-servis banka bağlama
- WPF'te ödeme geçmişi ekranı
- Ödeme isteği kayıtlarının birleştirmede yeniden anahtarlanması. Kayıt yazıldığı sahip satırında kalır, kişi okumada üye kesişimiyle bulunur (§2.1). Bakiye ise kişi düzeyindedir (ortak bakiye spec'i).
- Panelde düz dekont onayının kargoya bağlanması: bugün de bağlamıyor (`PanelPaymentsController.Approve` yalnız etiket ve bildirim). "Evet"le onaylanan dekont bağlanır.
- Shopper hesabı olmayan müşteriyi silen araç: bugün hiçbir yol yok, ayrı KVKK işi (§12).
- POS ve giden hareketler (`BankOptions.ExcludedTransactionCodes`)

## 2. Veri modeli

**Kişi.** Sunucuda bir kişi, aynı `GroupId`'yi paylaşan asıl kayıtlardır (`WpfCustomerProjection.GroupId`, `MergedIntoId` ile asıl kayda çevrilmiş); `GroupId` boşsa yalnız kaydın kendisi. `GroupId` WPF'ten zaten senkronlanıyor; ek veri gerekmez. Üyelik tek yerden okunur (`PersonMembership`, `CustomerIdResolver`); `GroupId` karşılaştırması BIN2/ordinaldir. Gruplama değerlendirme anındaki hâliyle okunur: ayrılan (unmerge) satırlar ayrı kişi sayılır. Müşteri bakiyesi kişi havuzudur: kişinin üye satırlarındaki bakiyelerin toplamı (`2026-10-11-ortak-bakiye-design.md` §2, §4). Bu belgede "kişi" hep bu anlamdadır.

**1. Beklenti: ödeme isteği kaydından türetilir, ayrı tablo yok.** 10.10'daki gerekçe ("sunucu istenen tutarı bilmiyor") düştü. `PaymentRequestRecord` net tutarı, bakiyeden düşüleni, kapsamı, sahip satırını, üyeleri ve siparişleri tutuyor; `finish` net tutarı yazıyor (`PaymentRequestRecordService.FinishOnceAsync`). Aynı borcu ikinci bir tabloda ve WPF kuyruğunda izlemek iki kaynağın ayrışması demekti.
- **İstek anı ve etkin hâl.** İstek anı = `StartedAt`: kirada sunucunun `begin` anı (kişi×kapsam kilidi altında), kirasız kayıtta istemcinin bildirdiği an (şimdiye kırpılmış). Bu belgede "en yeni", "daha yeni", "en eski" ve süre hep istek anına göredir, `FinishedAt`'e göre değil. Geç gelen `finish` ve geri doldurma `FinishedAt`'e sunucunun alış anını yazar (`PaymentRequestRecordService` 145, 425). Ona göre sıralansaydı dünkü istek bugünkünün önüne geçer, üç ay önceki istek 14 gün daha açık görünürdü. Süresi dolmuş kira veritabanında `Sending` kalabilir, `Unconfirmed`'a yalnız aynı kişi×kapsamın sonraki `begin`'i çevirir (`PaymentRequestLeaseService` 249-262). Bu yüzden `Unconfirmed` burada hep **etkin hâldir** (`PaymentRequestRules.IsExpiredLease`; liste ucunun `Effective`'i, 291-294, ortak yere taşınır). Net tutarı boş `Unconfirmed` kirada istenen tutar `TotalBeforeBalance`'tır.
- **Beklenti** = kişi × `session:{id}` kapsamının en yeni `Delivered` kaydı. **Kalan** = `NetAmount` eksi o kişi×kapsamın geri alınmamış tahsisleri (2). Kalanı 0 olan (tamamı bakiyeden karşılanan dahil) ve tahsissiz olup siparişleri bağlı olan (aşağıda) açık sayılmaz. Kalan sıfırın altındaysa fark düşmez: istek satırında "X ₺ fazla ödenmiş görünüyor" yazar, bakiye kartından elle düzeltilir.
- **Kişi eşleşmesi** `PersonKey` ile değil üye kesişimiyle yapılır (`PaymentPersonResolver.WithCopiesAsync`): birleştirme kayıtları yeniden anahtarlamaz. Üyeler yazıldıkları andaki kümedir; Ayır'dan sonra iki kişiye de kesişen kayıt güvenilmezdir (aşağıda).
- **Siparişleri bağlı istek:** kayıtta "ödendi" hâli yok (`PaymentRequestState`) ve kümesi ödenmiş siparişi de sayar (`PaymentRequestLeaseService.ServerOrdersAsync` 594-625 kargo bağına bakmaz). Tahsis yazmayan ödemenin (S3b'den önceki dekont, "Elle gir") tek izi siparişlerin kargo bağıdır; masaüstünün "ödendi" tanımı da budur (`PrintedLabelsViewModel` 360-363, `LabelService` 126). Geri alınmamış tahsisi olmayan ve toplama giren (`LabelRules.Contribution`) siparişlerinin hepsi bağlı ya da iptalle hiç kalmamış istek açık sayılmaz; yoksa ödenmiş istek 14 gün açık kalır ve "Evet"te en eskiden seçili gelirdi. Geri alınmış bir onayın adımının kurduğu ve hâlâ duran bağ (kargoya verilmiş dosya, mod Off; eşitlik §4.6.5) bağlı sayılmaz, yoksa ödenmemiş borç kaybolurdu; sonradan "Elle gir"in kurduğu bağ sayılır. Tahsisi olan istekte bağa bakılmaz, kalan belirler: eksik ödenip dosyaya giren isteğin kalanı açık kalır (§16-5); istisnası aşağıdaki "tahsissiz bağlı sipariş".
- **Güvenilmez kayıt** (otomatik onay, fazla hesabı ve "Eksik" dışı; soru sorulur). Yalnız açık ve süresi geçmemiş kayıtta sayılır: süresiz olsaydı geçmişinde tek bir genel toplam isteği olan müşteri hiç otomatik onaylanmazdı, çünkü o kayıtlar tahsis almaz ve silinmez.
  - Kapsamı `cumulative` ya da `legacy`. `cumulative` ömür boyu basılmış toplamdır, ödeme düşülmez; Müşteriler çekmecesinden yayın süzgeci olmadan gelen istek bu kapsamdadır (`CustomerSearchViewModel.OpenWhatsAppAsync`).
  - Kirasız (`Kind = Record`): tutarı ve kümesi `begin`'de doğrulanmadı (kargoda da böyle, §4.6.1-3). İstek anı istemci saati olduğu için aynı kişi×kapsamda kiradan sonra alınmış (`FinishedAt`) kirasız kayıt, eski görünse de kapsamı güvenilmez yapar.
  - Boş `NetAmount`; siparişi olmayan ya da siparişi sunucuda bulunmayan kayıt.
  - Kişi×kapsamın `Delivered`'ından yeni (yoksa en yeni) `Unconfirmed` isteği ve o `Delivered`.
  - *Birleştir / Ayır:* kişi×kapsamda, en yeni `Delivered`'ın kümesinde olmayan ve hâlâ toplama giren siparişi bulunan daha eski `Delivered` kayıt varsa (ayrı satırlara gönderilip sonra birleştirilmiş istekler, Ayır'dan önceki ortak istek) kapsamın bütün kayıtları; iki kişiye kesişen kayıt. Bu kapsamda kalan **kayıt düzeyindedir** (`NetAmount` eksi yalnız o kayda yazılmış tahsisler). Yoksa ayrı iki istek tek istek sayılır: biri ödenince öbürü kapanır ya da sahte fazla doğardı.
  - *Tahsissiz bağlı sipariş:* kümesinde, bağı bir kargo adımının kurmadığı ve bağlandığı anda (`ShipmentSetAt`) onu içeren bir kayda yazılmış tahsisi olmayan bağlı sipariş var. Parası tahsissiz yoldan gelmiştir, kalan olduğundan büyük görünür; sonradan yazılan tahsis bunu düzeltmez. Tahsisten sonra "Elle gir"le kurulan bağ (dekont, `OrderSetUnknown`) bu duruma girmez, gerçek "Eksik" korunur (§16-5). Damgasız bağ (mod Off'ta kurulmuş; damgayı yalnız komut ve koşucu basar) tahsissiz sayılır: sırası bilinmez, yanlışı bir sorudur.
  - *Kümesi değişmiş:* `ProductTotal`'ı dolu ve kümesinin güncel toplamı ondan farklı (istekten sonra iptal ya da fiyat düzeltmesi). Ekran güncel toplamı da gösterir; kargo nedeni `Shortage` değil `OrdersChanged`'dır (§4.6.1-3).
  - *Ödemeden sonra değişmiş:* kişi×kapsamın tahsisli bir kaydının siparişi sonradan iptal edilmiş. İptal bakiyeye iade edilip yeni istekte düşüldüyse aynı para hem tahsiste hem düşümde sayılır, kalan küçük görünür.
- **Canlı kira** (`Sending`, `LeaseUntil` ileride) varken "Evet" 409 `stale`: `begin` net tutar yazmaz, o an sunucudaki tutar önceki kaydınkidir.
- **Süre** istek anı + `Bank:ExpectationTtlDays` (varsayılan 14), sorgu anında türetilir. Süresi geçen beklenti yalnız otomatik onaydan ve fazla hesabından düşer; panelde "süresi geçti" görünür, insan "Evet"inde işaretli ve seçilmemiş listelenir (§4.2).
- **Ödemeden sonra yeni istek:** yeni net tutar tahsis toplamından küçük ya da ona eşitse beklenti kapalı kalır (hatırlatma ödenmiş borcu açmaz); büyükse yalnız kalanla açıktır ve sorulur (otomatik onay yok). Tahsissiz ödemede ölçüt §5-6'dadır; hatırlatma o izi silmez.
- **Banka dışı kapanış** (Elden, Papara) WPF'ten B-W2b ile gelir (§8); ödemesiz tahsis satırıdır (2).

**2. `PaymentExpectationAllocation` (yeni).** `(Id, LicenseId, PaymentId?, ExpectationId, Kind, Amount, CreatedAt, ReversedAt?)`. `ExpectationId` = `PaymentRequestRecord.Id`, **FK'sız**; tek FK `Payment`'a (ikinci kaskad yolu SQL Server'da 1785 verir). Toplam kayıt başına değil **kişi×kapsam** düzeyinde hesaplanır (tek istisna §2.1'deki Birleştir/Ayır kapsamı): hatırlatma yeni kayıt açar, kayıt bazlı toplam ödenmiş borcu yeniden açardı. Bir ödeme birden çok beklentiyi, bir beklenti birden çok ödemeyi kapatabilir. Onayın SaveChanges'inde yazılır; geri almada `ReversedAt` alır. **S3b'den itibaren, pilot dahil** yazılır (§4.2): yazılmasaydı ödenmiş istek pilotta açık kalır, sonraki "Evet"te seçili gelir ve S5'te geri doldurulamazdı. `Kind`: `Payment` (S3b); `Waived` ("Kalanı sil"), `Cancelled` ("İptal") ve `OffBank` (Elden/Papara; WPF çağrısı B-W2b'de) S5'te. Son üçünde `PaymentId` boş (şema S2a'da). Bankadan onaylanmış ödemenin tahsisi yalnız §11.2 ile geri alınır; düz dekont onayının ve ödemesiz türlerin tahsisi tek başına geri alınabilir (`allocations/{id}/reverse`, §6). Yoksa yanlış onaylanan dekontun kapattığı istek kapalı kalır, gerçek ödemesi fazla sayılırdı. Ödemesiz satır lisans silmesinde kaskadla gitmez; yayıncı silmesi (`AdminCustomersController.Purge`) onu açıkça siler.

**3. `Payment`'a eklenecek alanlar**
- `WpfCustomerId Guid?`: shopper dekontunda gönderim anında `ShopperBroadcasterLinks.WpfCustomerId`'den doldurulur (`ShopperPaymentSubmissionService`, S2b); eski satırlar S2a göçünde geri doldurulur (bağ seçimi `PaymentMatchReconciler.ResolveWpfCustomerAsync` ile aynı), birleştirmede asıl kayda çevrilir (§9). WPF dekont yazmıyor (#528); WPF'ten gelmiş eski satırlar müşterisiz kalır. Bilinmiyorsa null kalır ve §5-6'da "müşterisi bilinmeyen" sayılır.
- `Source` (Receipt / Bank). S2a'da mevcut satırlar `Receipt` ile geri doldurulur.
- `ApprovalSource` (ReceiptHuman / BankHuman / BankAuto); "Evet"le onaylanan dekont `BankHuman`'dır.
- `ApprovedByOperatorId Guid?`: personel onayında `TenantClaims.GetOperatorId`.
- `BankTransactionId Guid?`: **FK'sız** Guid. Saklama işi hareketi ve eşleşmeyi 180. günde siler (`BankDataRetentionJob.DeleteExpiredTransactionsAsync`); hesap reddi, "Kaldır ve sil" ve hizmet sonu da siler (`BankConnectionTerminator`). `NoAction` FK bu silmeleri 547 ile düşürür ve saklama işi her gece durur; `SetNull` ikinci kaskad yolu olur. Emsal: `CustomerIbanMemory.SourceBankTransactionId`, `PaymentMatchGap.ResolvedBankTransactionId`. "Evet"le onaylanan dekonta da yazılır, geri almada dekonttan boşalır; hareketi silinmiş onay bu alanla bulunur (§11.2).
- Kargo izi Payment'ta değil, §4.6 adımlarında (`ShipmentStepAt` yazılmaz).

Bankadan doğan bir satır şöyle yazılır:
- `Status = Approved`, `Amount = tx.Amount`, `PaidAt = tx.OccurredAt`, `PdfHash = null`
- `ReferansNo = "bank:{BankTransactionId:N}:{n}"`; `n` = 1 + o harekete daha önce yazılmış banka satırı sayısı. Tekil indeks (`LicenseId`, `ReferansNo`) filtresiz; geri alınan satır `Rejected` olarak kaldığı için yeniden onay yeni bir `n` ile yazılır. ReferansNo çift onaya karşı koruma **değildir**; koruma `Decision` jetonu ve hareket başına tek eşleşme satırıdır (§5.3). `ObifinId` kullanılmaz, çünkü kimlik değişiminde değişir (§13).
- `PayerName = ""`. Gönderen adı hiç yazılmıyor (`ObifinPollJob`, `CounterpartyName = null`; KVKK veri küçültme). Ad alanı yeniden açılmaz.
- `ShopperId`, `ShopperBroadcasterLinks(LeftAt == null)` üzerinden bulunur; yoksa null.
- `ApprovedByCustomerId` insan onayında tenant kimliği, otomatik onayda null.

`payments/sync` onaylı ya da `Source = Bank` satırda `PayerName`, `Amount`, `PaidAt` ve `PdfHash`'i ezmez (S2b). Bugün `LicensesPaymentsSyncController.Sync` durumdan bağımsız eziyor; bu yolu yalnız gönderilmemiş eski satırlar kullanır.

`GET /api/panel/payments` ve `GET /api/panel/search`'ün ödemeler bölümü `Source = Bank` satırını döndürmez (S2b; satırları S3b yazar, okuyucu önce çıkar; `Source` boş satır dekont sayılır). Banka onayları yalnız `bank-transfers?state=approved`'da görünür. Süzülmeseydi iki harflik arama `bank:{hex}` referansıyla eşleşir, adsız satırlar dekontları 10'luk listeden düşürürdü (`PanelSearchController` 111-119); liste ucu da personelin 48 saat süzgecini (§7) aşardı (`PanelPaymentsController` 81-108).

**4. `PaymentMatch`**
- `Status` bugünkü anlamını korur: eşleştiricinin isabeti. **Yeni değer eklenmez.** Otomatik onaylanan satırın `Status`'u `Proposed` kalır; ayrımı `Decision` yapar. `AutoApproved` diye bir Status değeri `PaymentMatchMetrics.Classify`'ı (bilinmeyen değerde fırlatıyor) ve eşleştiricinin insan kararı kilidini (`PaymentMatcher.MatchOnceAsync`) bozardı.
- Yeni sütunlar: `Decision` (§3), `DecisionRevision` (int, her karar geçişinde +1), `AskReasons` (kapalı küme, §5.2), `DecidedVia`, `DismissReason`, `DismissNote` (180. günde hareketle birlikte silinir) ve `SupportedByIbanMemory`: öneriyi IBAN hafızası da destekliyor mu. `Layer`'dan ayrıdır, çünkü kullanıcı adı ile IBAN aynı müşteriyi gösterdiğinde katman `UsernameInDescription` (0,98) yazılıyor. `MatcherVersion` (boş = satır düzeyi, 2 = kişi düzeyi): önerinin hangi eşleştiriciyle üretildiği; sütun S2a'da, yazım S2b'de.
- **`Decision ≠ None` kilidi beş yerde:** `PaymentMatcher.MatchOnceAsync`, `PaymentMatchReconciler.LinkAsync`, `CandidatesAsync`'in `taken` kümesi, `ReevaluateSameIbanAsync` ile `ReevaluateUndecidedAsync`, `BankMatchSweepJob`'ın 2. aşaması. Biri atlanırsa açık sorunun önerisi yayıncının önünde değişir (#551 spec'ten sonra geldi ve yalnız `Status`/`DecidedAt`/`PaymentId`'ye bakıyor). Tek istisna §3'teki `Unmatched` yeniden değerlendirmesi.
- **Kişi düzeyi eşleştirme (S2b).** Eşleştirici bugün satır düzeyinde: aynı kişinin IG ve TT satırı `ambiguous`, kullanıcı adı bir satırı IBAN öbürünü gösterirse `conflict` üretir; `LearnIbanAsync` öbür satırda `ConflictOtherCustomer` döner, `LinkAsync` öneriyi `Contradicted` sayar. Düzeltilmezse iki satırlı kişinin havalesi hiç sorulmadan "Eşleşmeyen"e düşer. Kullanıcı adı, IBAN ve ad katmanlarında adaylar kişiye toplanır; nüfus değiştiği için ölçüm satırları `MatcherVersion` ile ayırır (§4.5). Olaydaki `RuleVersion` bunun yerine geçmez: ölçüm yalnız `PaymentMatch` okur (`PaymentMatchMetrics` 114-130) ve bayrak kapalıyken olay yazılmaz.
- **IBAN birden çok kişiye düşerse ya da kullanıcı adıyla ayrışırsa sorulur** (§16-0b, S2b). IBAN katmanı, çiftlerin toplandığı kişi **kümesini** verir (§2.5):
  - Kullanıcı adı tek kişiyi gösteriyor ve küme onu içeriyorsa öneri odur; küme birden çok kişiyse otomatik onayı §5-3 durdurur (`IbanShared`).
  - Kullanıcı adı tek kişiyi gösteriyor ama dolu küme onu içermiyorsa (`IbanOtherCustomer`), ya da kullanıcı adı yok ve küme birden çok kişiyse (`IbanShared`): öneri yazılmaz (`Status = NoProposal`), satır yine `Awaiting` olur ve **adaylar** listelenir (önce kullanıcı adının kişisi, sonra kümedekiler, son doğrulaması en yeni olan önde; en çok 5; saklanmaz, okunurken hesaplanır). Yayıncı adayı seçer ("Bu havale kimin?"); hangisi olduğu tahmin edilmez.
  - Bugünkü kod ilkinde çelişki, ikincisinde (yeni indeksle) sıralamasız bir satır üretir (`PaymentMatcher` 162-184); havale sorulmadan "Eşleşmeyen"e düşer ya da rastgele kişiye önerilirdi. Yalnız kullanıcı adında ya da adda birden çok kişi bugünkü gibi öneri yok demektir (`Unmatched`).
- `ExpectationId` sütunu **yok**: bir havale birden çok beklenti kapatabilir; bunu tahsis tablosu (2) ve olay satırı taşır.
- "1 hareket = 1 eşleşme satırı" ve "1 ödeme = 1 hareket" indeksleri yerinde kalır (`LicenseDbContext`, `PaymentMatch` eşlemesi).

**5. `CustomerIbanMemory` ve `BankIbanState`**
- `CustomerIbanMemory` tekil indeksi `(LicenseId, IbanHash)` yerine `(LicenseId, IbanHash, WpfCustomerId)` olur. Bugünkü indeksle "IBAN tek müşteriye bağlı" koşulu hep doğru çıkıyor; denetim olarak işe yaramıyor. Göç, birleştirilmiş kopyaları önce asıl kayda çevirip tekilleştirir.
- Yeni kaynak `IbanMemorySource.BankConfirm`. Ayrı bir `ConfirmedAt` alanı **yok**: doğrulama bugünkü `CustomerIbanMemory.LastConfirmedAt` ve `PaymentMatch.IbanConfirmedAt` ile tutulur.
- **"Evet" yalnız özeti geçerli IBAN'dan üretilmiş (`CounterpartyIbanHashValid`) ve dışlanmamış harekette öğretir** (bugünkü `LearnIbanAsync` kapısı, KVKK Ö-7). Çift yoksa eklenir; varsa (gölgede `HumanApproval` ya da `ManualMatch` ile öğrenilmiş) `LearnedFrom = BankConfirm` olur ve süre `LearnIbanAsync`'in kuralıyla tazelenir: değer havalenin işlem tarihi, yalnız ileri gider, öğreten havale (`SourceBankTransactionId`) değişmez.
- Sayım **kişi** üzerinden yapılır: çiftlerin `WpfCustomerId`'leri `MergedIntoId` ile asıl kayda çevrilir, asıl kayıtlar kişiye toplanır (§2 "Kişi"), sonra farklı kişi sayılır (bugünkü tek satır okuması yerine). Adaylar da asıl kayda çevrilir. Aynı kişinin iki satırı (ör. IG ve TT) tek kişi sayılır. Birden çok kişi çıkarsa öneri kuralı §2.4'tedir.
- **`BankIbanState` (yeni, `(LicenseId, IbanHash)` tekil; Licenses'a kaskad FK).** Kişiye bağ taşımaz, ama IBAN özeti kişisel veri sayıldığı için süresiz değildir: o IBAN'dan son havaleden (`LastTransferAt`) **12 ay** sonra silinir; IBAN talep aracı (`BankSubjectRequestService`), hizmet sonu, "Kaldır ve sil" ve hesap reddi onu da kapsar, her silme imha kaydı yazar (S4). `LastTransferAt` sütunu şart (S2a): hareketler 180. günde silinir (`BankOptions` 26, `BankDataRetentionJob` 141-163), son havale sonradan bulunamaz. Değeri havalenin işlem tarihidir (ileri tarih şimdiye kırpılır, yalnız ileri gider; `CustomerIbanMemory.LastConfirmedAt` kuralı); satırı yazan karar ve o IBAN'ın sonraki her değerlendirmesi (S3a) ilerletir.
  - `Shared`: IBAN iki farklı **kişi** için onaylanınca yazılır ve bu süre içinde kalkmaz. Aynı kişinin iki satırına verilen iki "Evet" `Shared` yazmaz. Bugün bu durumda yalnız log yazılıyor. Purge ya da geri alma çiftlerden birini silse de IBAN yeniden tek sahipli sayılmaz; bunun bedeli yalnız soru sormaktır.
  - `Disputed`: IBAN önerisine "Hayır" denince ya da o IBAN'dan gelen bir onay geri alınınca yazılır. Aynı IBAN için yeni bir `BankConfirm` "Evet"i gelene dek otomatik onay yok.

**6. `BankDecisionEvent` (yalnız ekleme yapılan denetim satırı)**
- Alanlar: `LicenseId` (tek FK, kaskad: lisans silinince yetim kalmasın), `TxId?`, `ExpectationId?`, `PaymentId?`, `StepId?` (hepsi **FK'sız** Guid, 3'ün gerekçesiyle), `ActorKind` (Owner / Staff / System), `OperatorId?`, `Surface` (Panel / Wpf / Sink / Sweep / Runner / ReceiptApproval / Admin), `Action`, `ReasonCode?`, `RequestId?` (§3), `RuleVersion`.
- İlk değerlendirme olayı (`Evaluated`) ölçüm girdilerini taşır: `WouldAutoApprove` (bayrak hariç bütün koşullar), kuralın seçeceği müşteri ve beklenti id'leri, tutmayan koşullar ve `OrderSetReliable` (varsayılan seçimin sipariş kümesi kargoya bağlanabilir mi, §4.2 ve §4.6; pilotta oranı bu ölçer). Karar olayları: memoryId, `LearnedFrom`, beklenti id'leri ve sürümleri, tutarlar. Kargo olayları (bağlama, çözme, karar) yalnız Id.
- Ham açıklama, ad, IBAN ve serbest not yazılmaz. `AuditLogEntry` kullanılamıyor, çünkü `AdminId` zorunlu. WPF kararları `Owner` yazılır; WPF tenant jetonuyla çağırıyor. Saklama 24 ay (öneri, §12).

**7. Lisans bayrakları:** `BankQuestionsEnabled`, `BankAutoApproveEnabled` (yalnız sorular açıkken etkili), `BankQuestionsSince`. Yalnız CLI yazar: `bank-questions enable|disable`, kuru koşu ve `--apply`, tek koşullu `ExecuteUpdate` (`PersonBalanceCommand` ve `sales-sync` deseni). Otomatik onay bayrağının komutu S6'da.
- **Üç hâl, tek yerde hesaplanır.** `enabled` = bayrak **ve** `ObifinConnectionService.MatchingStateAsync` doğru **ve** banka anahtarı dolu. `stopped` = bayrak açık, öbür ikisinden biri yok. `off` = bayrak kapalı (kill switch). Soru listeleri (`awaiting`, `unmatched`, `dismissed`), karar uçları (seçici ve `requests` dahil), otomatik onay, tarama ve besleme bekçisi yalnız `enabled`'da çalışır; karar ucu `stopped`'ta 409 `matching-stopped` (KVKK K-6: durdurulan bağlantının verisi işlenmez), `off`'ta 409 `questions-off` döner.
- **Verilmiş kararı düzelten uçlar üç hâlde de açıktır:** `approved` listesi, geri alma, bağ kaldırma, "Fazlayı iade ettim", kargo adımı listesi ve `decide`. Bağ siler ya da siparişe dokunur; hareketi yeniden işlemez, yeniden hesap koşmaz. Kill switch onları da kapatsaydı yanlış onay geri alınamaz, takılı kargo adımı kapatılamazdı. `enabled` değilken Onaylananlar yalnız ödeme alanlarıyla döner; hareket metni ve maskeli IBAN dönmez. Soru listeleri `enabled` değilken boş sayfa döner; 409 yalnız karar uçlarındadır.
- **Beklenti uçları** (`payment-expectations`, `waive`, `cancel`, `off-bank`, `openExpectations` S5'te, tahsis geri alması S3b'de) üç hâlde de açıktır: banka hareketine dokunmaz, yalnız istek kaydı ve tahsis okur ya da yazar.
- **Dekont onayının banka yan etkileri:** Aynı/Ayrı ödeme sorusu (§4.4) üç hâlde de çalışır; hareketi değil `Payment` satırını okur, kapansaydı para iki kez onaylanırdı. Açık sorunun dekontla kapanması (§4.4) yalnız `enabled`'da koşar (K-6; bugünkü onay bağdaştırması da durdurulmuş bağlantıda durur, `PaymentMatchReconciler` 223); kapanmayan soru yeniden `enabled` olunca dekontu aday gösterir. Dekont tahsisi bayrak açıkken (`enabled`, `stopped`) yazılır; `off`'ta yazılmaz, ödeme tahsissiz sayılır (§4.2, §5-6).
- `enable`, lisansta `SalesSyncMode` On değilse reddeder (§4.6, §14).
- Panel ve WPF hâli `/count` yanıtından okur (§6); bugün paneli lisans bayraklarına açan bir uç yok.

**8. Bakiye defteri:** yeni hareket türü `"bank-overpayment"`: artı ve tek satırlı (`DrawGroupId` NULL), `Reason` sabit metin ("Havale fazlası"). Defter shopper'a da açık; banka metni (ad, açıklama) oraya asla yazılmaz. Genel geri alma bu türü ve onun `reversal` satırını üç yolda da (`PanelCustomerBalanceController` `Reverse`, `ReverseDrawGroupAsync`, `ReverseInPoolAsync`) 409 `use-bank-revert` ile reddeder; satır yalnız banka geri almasıyla ya da "Fazlayı iade ettim" eylemiyle kapanır (§11.2).

**9. Kargo adımı:** `PaymentShipmentStep` ve `PaymentShipmentStepOrder` (yeni), §4.6.3.

## 3. Bir havalenin yaşam döngüsü (`Decision`)

**Durumlar**
- `None`: karar katmanına girmemiş. Bayrak kapalıysa ya da hareket `BankQuestionsSince`'ten eskiyse gölgedir. Bayrak açıksa ve hareket Since'ten yeniyse "değerlendirilmeyi bekliyor" demektir; tarama onu yakalar.
- `Excluded`: POS gibi dışlanan kod ya da para birimi TL/TRY değil (`BankTransaction.Currency`; eşleştirici bugün para birimine bakmıyor).
- `Awaiting`: öneri, aday kişiler (§2.4) ya da doğrulanmamış dekont adayı (§4.4) var ama otomatik kural tutmadı.
- `Unmatched`: öneri ve aday yok (birden çok kullanıcı adı ya da ad tutması dahil), "Hayır" dendi, onay geri alındı ya da açıklaması değerlendirilmeden silindi.
- `Approved`: insan onayı. `Status` bugünkü gibi yazılır: öneri kabul edildiyse `ConfirmedByHuman`, başka müşteri seçildiyse `Contradicted`, öneri yokken elle eşlendiyse `ManualOnly`.
- `AutoApproved`: §5'teki kuralla onaylandı.
- `ClosedByReceipt`: onaylı bir dekonta bağlandı (§4.4).
- `Dismissed`: gerekçeyle "müşteri ödemesi değil" denildi.

Geri alma ayrı bir durum değildir; `Reverted` olayı yazılır ve satır `Unmatched`'e döner.

**İlk değerlendirme**
- Sink ve tarama `PaymentMatchReconciler.MatchAndResolveGapAsync` yerine `BankPaymentDecisionService.EvaluateAsync`'i çağırır. Eşleştirici `MatchOnceAsync`'i "hesapla" ve "kaydet" diye ayrılır; öneri hesaplanır ama kaydedilmez, ardından gap çözümü koşar ve karar verilir. Eşleşme satırı, karar, varsa ödeme, kargo adımı ve olay **tek SaveChanges**'te yazılır. Bugün öneri kendi SaveChanges'inde kaydediliyor ve sink her hatayı yutuyor (`MatchingBankTransactionSink.OnNewIncomingAsync`). Karar adımı ayrı olsaydı ve düşseydi satır `None`'da kalırdı; tarama da onu bir daha seçmezdi, çünkü yalnız eşleşme satırı olmayan hareketi seçiyor (`BankMatchSweepJob.RunAsync`). Bayrak kapalıyken bugünkü davranış birebir sınanır.
- Tarama ayrıca `Decision == None`, `enabled` (§2.7), `OccurredAt >= BankQuestionsSince` ve `FetchedAt < now − FetchGrace` satırlarını da seçer. Açıklaması silinmiş `None` satırın önerisi hesaplanmaz: dekonta bağlıysa `ClosedByReceipt`, değilse `Unmatched` olur ("açıklama saklama süresi doldu", §12); `None`'da kalıp `stuckCount`'u şişirmez. Beklenti yarışını kaybeden otomatik onay aynı istekte yeniden değerlendirilir (normalde `Awaiting`).
- **Önceden dekonta bağlanmış `None` satır** (`PaymentId` dolu; bağ bayrak kapalıyken ya da sink düşmüşken `LinkAsync` ile kurulmuş) §4.4'ün doğrulama kuralıyla değerlendirilir. Bağ doğrulanmışsa sonuç `ClosedByReceipt`'tir, soru ve ödeme yoktur. Değilse aynı SaveChanges'te bağ çözülür (dekont `ReturnPaymentsToGapsAsync` ile gap'e döner, karar alanları ve öneri boşalır), öneri yeniden hesaplanır ve satır `Awaiting` olur, dekont adaylarda görünür. `PaymentId`'si dolu satır hiçbir zaman `Awaiting` ya da `AutoApproved` olmaz ve `confirm` onda 409 `stale` döner: "Evet" ikinci bir ödeme yazıp bağı ezerdi.
- Bayrağı kapatıp açmak `BankQuestionsSince`'i sıfırlamaz. Aradaki `None` satırları tarama değerlendirir; 3 günden eski olanlar kural 7 gereği sorulur, dekonta bağlı olanlar yukarıdaki kuralla kapanır.

**Geçişler**
- `None` → {Excluded, Awaiting, Unmatched, AutoApproved, ClosedByReceipt}: yalnız ilk değerlendirmede; `ClosedByReceipt`'i `enabled` iken gecikmeli bağ da yazar (§4.4).
- `Awaiting` → {Approved, Unmatched, Dismissed, ClosedByReceipt}
- `Unmatched` → {Approved, Dismissed, ClosedByReceipt, Awaiting}. `ClosedByReceipt` yalnız insanın "Bu dekontun havalesi" seçimidir (§4.4). `Awaiting`'e yalnız yeniden değerlendirme geçirir: IBAN hafızası değişince (#551) satır öneri ya da aday alırsa `Awaiting` olur, `DecisionRevision` artar. "Hayır" denmiş ya da onayı geri alınmış satıra dokunulmaz (reddedilen müşteri yeniden sorulmaz); ayrım hareketin `BankDecisionEvent` olaylarından okunur, ayrı sütun yoktur.
- `Dismissed` → `Unmatched` (geri aç)
- `Approved` / `AutoApproved` → `Unmatched` (geri al, §11.2)
- `ClosedByReceipt` → `Unmatched` (bağı kaldır, §11.3)
- **Bir kez sorulmuş havale bir daha otomatik onaylanmaz**; yeniden değerlendirme hiç otomatik onay üretmez. `Awaiting` satırın önerisini arka plan işi değiştirmez: gecikmeli bağdaştırma (`PaymentMatchReconcileJob`), gap çözümü ve eşleştiricinin yeniden hesabı `Decision ≠ None` satıra dokunmaz (§2.4 kilidi). İki istisna var: yukarıdaki `Unmatched` geçişi ve dekont onayı anındaki dar kapanış kuralı (§4.4); ikincisi bir insan eylemidir.

**Jeton, `requestId` ve 409**
- Her mutasyon isteği ekranda görülen `decisionRevision`'ı (hareketi silinmiş ödemede yoktur, §11.2) ve istemcinin ürettiği `requestId`'yi taşır. `decisionRevision` eşleşmezse 409 `already-decided` döner; yanıt kararın ne olduğunu, nerede ve ne zaman verildiğini söyler.
- Aynı `requestId` yeniden gelirse 200 ile aynı sonuç döner, hiçbir şey yazılmaz (`BankDecisionEvent.RequestId`). WPF dayanıklılık katmanı POST'u yineler (`AppHost`, `AddStandardResilienceHandler`); yanıtı kaybolan kendi "Evet"i yoksa "başka yerde yanıtlandı" görünürdü.
- `DecisionRevision` aynı ama satırın `UpdatedAt` jetonu değişmişse (saklama işinin kanıt boşaltması, purge, birleştirme), sunucu satırı yeniden okuyup bir kez yeniden dener (`RetryOnceAsync` deseni). Yine çakışırsa 409 `stale` döner; arayüz listeyi yeniler ama "başka yerde yanıtlandı" demez.

## 4. Onay çekirdeği

### 4.1 Tek servis
- `PanelPaymentsController`'daki onay/ret yazımı ve üç yan etki (`ScheduleShadowReconcile`, `ApplyConversationLabelAsync`, `NotifyShopperPaymentDecisionAsync`) `PaymentApprovalService`'e taşınır (S1, davranış değişmez). Sıra birebir korunur: SaveChanges (`Status` jetonu, 409 `not-pending`), 20 dakikalık gecikmeli iş, etiket, push. Dekont ve banka kararları bu tek servisi kullanır; kodu kopyalamak iki yolun zamanla ayrışmasına yol açardı. WPF ödeme onaylamıyor; dekont onayının tek yeri panel.
- "Evet" tek `SaveChanges` içinde şunları yapar: Payment oluşturur (ya da bekleyen dekontu onaylar), `PaymentMatch`'i günceller (`Decision`, `Status`, `PaymentId`, `DecisionRevision`), IBAN çiftini öğretir (§2.5), `BankIbanState`'i günceller (`Disputed` kalkar; ikinci kişiyse `Shared` olur), tahsisleri yazar ve seçilen kişi×kapsamların kayıtlarının jetonunu artırır (§4.2, §5.3), fazla varsa (S5) son tahsis edilen kaydın sahip satırına `bank-overpayment` satırı ekler (§4.3), kargo adımlarını yazar (§4.6), olay satırını yazar. Jeton çakışmasında `ChangeTracker.Clear` ile baştan kurulur; "yeniden yükle, deltayı ekle" kullanılmaz (ortak bakiye M12).
- Kargo bağının kendisi bu SaveChanges'in dışındadır: adım kalıcıdır, bağı commit'ten sonra koşucu kurar (§4.6).
- Commit'ten sonra `PaymentApproved` etiketi uygulanır. `ShopperId` yoksa `LabelRuleApplier.TryApplyAndSaveByWpfCustomersAsync` kullanılır. Ardından shopper push gider. `PaymentMatchReconcileJob` zamanlanmaz.

### 4.2 Tahsis: havale hangi isteği kapatır
- **Uygun beklenti:** müşterinin kişisindeki (§2 "Kişi") bütün asıl kayıtların açık, süresi geçmemiş ve güvenilir beklentileri (§2.1). Kayıt tıklanan satıra değil isteğin sahip satırına yazıldığı için önerilen satır ile kaydın satırı farklı olabilir. `cumulative` kapsam uygun sayılmaz.
- **Otomatik onayda** §5-5'teki kural hangi beklentileri seçtiyse onlar kapanır.
- **İnsan "Evet"inde** alt sayfa seçilen müşterinin kişisinin isteklerini listeler: kapsam başına en yeni `Delivered` kayıt ve ondan yeni (yoksa en yeni) `Unconfirmed`, en çok 20 kapsam. Birleştir/Ayır kapsamında (§2.1) kalanı olan bütün kayıtlar ayrı satırdır ve seçilen her kaydın kendi kümesi bağlanır. Kalanı 0 ya da altında olan istek listelenmez; süresi geçmemişse iki istisna: bağsız siparişi varsa ya da kalanı eksiyse işaretli ve seçilmemiş listelenir (tahsis yazılmaz, yalnız bağsız siparişleri bağlanır), geri alınabilir tahsisi varsa (§6) salt okunur satırdır. Varsayılan seçim önce §5-5'in kuralıdır (havaleye eşit tek uygun beklenti, yoksa bütün uygunların toplamı); kural seçmiyorsa uygunlar en eskiden başlayarak havale tutarı bitene dek seçili gelir. Yalnız "en eski" olsaydı ödenmemiş eski istek, tam eşit yeni isteğin önüne geçer ve sahte `Shortage` üretirdi. Süresi geçmiş, güvenilmez ve siparişleri bağlı istekler ile yayınının başlangıcından bu yana kişiye tahsissiz bir ödeme onaylanmış istekler (§5-6; istek seçilmeden verilen "Evet", tahsis üretmeyen dekont) işaretli ve **seçilmemiş** gelir, yayıncı seçebilir; kümesi bilinmeyen kayıt seçilirse kargo adımı karar bekler (§4.6.1-3). Yalnız kümesi değiştiği için güvenilmez olan istek (§2.1) kişinin tek açık isteğiyse işaretli ama seçili gelir; seçilmeseydi kargo kümesi `OrderSetUnknown`'a düşerdi. Seçimi sunucu verir, panel ve WPF aynısını gösterir. İstek seçilen kayıtları ve sürümlerini taşır (`paymentRequests[{id, version}]`); kayıt listede artık yoksa ya da sürümü değiştiyse 409 `stale`. Kargo kümesi de bu seçimden çıkar (§4.6).
- **Tahsis S3b'den itibaren, pilot dahil yazılır:** "Evet" seçilen `session:{id}` kapsamlı kayıtlara yazar, geri alma `ReversedAt` verir; ödenmiş istek sonraki "Evet"te seçili gelmez. `cumulative` ve `legacy` seçimi tahsis üretmez. Pilotta ekran "Son istenen X ₺" ve kalanı gösterir; "Fazla", "Eksik" ve bakiye satırı S5'tedir, pilotta onaylanmış ödemeye geriye dönük fazla yazılmaz. "Eksik" yalnız tahsisi olan ve güvenilir isteğin kalanıdır: hiç ödeme almamış istek eksik değil, açıktır.
- Tahsis en eski beklentiden başlar; sonuncusu kısmen kapanabilir (eksik ödeme). Havale seçilen kalanları aşıyorsa artan kısım "fazla"dır.
- **Dekont onayı da** (S3b, sorular bayrağı açıkken) aynı servisten geçtiği için beklentiyi aynı kuralla kapatır: tek eşit beklenti varsa o; bütün açıkların toplamına eşitse hepsi; tek açık beklentiden küçükse kısmi. Kural tahsis üretemezse beklenti açık kalır ve müşteride "beklentiye bağlanmamış onaylı ödeme" oluşur. Bu durumda §5-6 o kişide otomatik onayı kapatır; panel beklentiyi "Kalanı sil" ya da "İptal" ile kapatmayı önerir (S5, §6). Yanlış onaylanan dekontun tahsisi tek başına geri alınır (§6): ödeme onaylı kalır, istek yeniden açılır ama tahsissiz ödeme yüzünden işaretli ve seçilmemiş gelir.

### 4.3 Fazla ve eksik ödeme
- **Fazla (karar §16-4):** fazla yalnız **güvenilir** beklentiye karşı hesaplanır. Seçili beklentiler uygun olmalı (§4.2) ve lisansın satış senkronu açık olmalı (§5-5). Bu sağlanınca "Evet" fazlayı her zaman bakiyeye yazar (`bank-overpayment`) ve alt sayfa "Fazla X ₺ bakiyeye eklenecek" der.
- **Hedef satır (ortak bakiye K6):** son tahsis edilen beklentinin sahip satırı (`PaymentRequestRecord.OwnerWpfCustomerId`, asıl kayda çözülmüş). Bir kişinin kayıtları çoğunlukla aynı sahip satırındadır; farklıysa son tahsis edilenin satırı seçilir. Havuz okumada toplandığı için satır seçimi yalnız ayırmadan sonraki sahipliği belirler (K1). Sahip GUID'siz yerel kimlikse bakiye satırı yazılamaz; fazla hesaplanmaz ve ekran bunu söyler.
- **İki kip, iki yazıcı:** artı satırı yazan ve tek satırı geri alan yol bugün denetleyicide private (`PanelCustomerBalanceController.ApplyTransactionAsync`); S5'te servise çıkar. Havuz kipinin `PersonBalance.DrawAsync`'i yalnız eksi yazar: fazla yazımında kullanılmaz, yalnız geri almada ve "Fazlayı iade ettim"de (§11.2). Kip lisans başına ve her istekte okunur.
- **Beklenti yoksa ya da güvenilir değilse** fazla hesaplanmaz. Bakiyeye hiçbir şey yazılmaz, "Eksik" gösterilmez, alt sayfa "İstenen tutar bilinmiyor" der. Bu, sahibin kararının uygulanışıdır: "fazla" ancak bilinen bir isteğe göre vardır. Havalenin tamamını fazla saymak müşteriye aynı parayı iki kez kredi yazardı. Pilot S5'ten önce koştuğu için pilotta hiç bakiye satırı yazılmaz.
- **Düşüm tavanı (S5, fazla yazımıyla aynı PR):** bakiye `apply`'ı iki kipte de (`LicensesCustomerBalanceApplyController` 533, 763, 815) en çok `ProductTotal` eksi kişi×kapsamın geri alınmamış tahsis toplamını düşer; sıfır ya da altındaysa bugünkü 409 `nothing-to-apply` döner, WPF bunu tanır (`PaymentRequestService` 2029; gönderdiği `ProductTotal` kargo dahil toplamdır, 2023). Tavansız, 600 ₺ ödenmiş 500 ₺'lik yayına 50 ₺'lik etiket eklenince 100 ₺ fazlanın hepsi düşülür; borç 550, ödenen 600 iken bakiye 0 kalırdı (§16-4).
- İstek gösterilen fazlayı ve kalanı da (`shownOverpayment`, `shownRemaining`; S5) taşır. Sunucu yeniden hesaplar; dolu alan tutmazsa (beklenti arada değiştiyse) 409 `stale` döner. **Fazla doğuran seçimde `shownOverpayment` zorunludur:** boşsa (S5'i bilmeyen istemci) 409 `client-update-required` döner ve hiçbir şey yazılmaz; havale web panelinden ya da güncel masaüstünden onaylanır. Boş alan hoş görülseydi bakiye satırı kimse görmeden yazılır, `stale` sayılsaydı "Evet" hiç geçmezdi. Fazla yoksa boş alan kabul edilir (eksikte bakiye yazılmaz, pilottaki davranış sürer).
- **Eksik (karar §16-5):** "Evet" ödemeyi dekont onayı gibi onaylar (kargolanacak etiketi dahil). Beklentinin kalanı açık kalır; panelde, WPF müşteri kartında ve havale listesinde **"Eksik X ₺"** görünür (S5, arayüzü P3 ve B-W2a; pilotta kalan yalnız "Evet" sayfasının istek satırındadır).
- **Kargo sorusu "Evet" ekranında değil.** İstekte `shipmentDirective` yok: `Payment.ShipmentDirective`'i yazan yeni kaynak kalmadı (#528), kargo için okunmuyor. Tutar seçilen isteklerin kalanından küçükse kargo adımı `Shortage` ile karar bekler ve masaüstünde sorulur: Normal / Beklet / Alıcı öder (§4.6.2); 10.10 tasarımı da her eksikte soruyordu. Kalan sonradan kapanırsa soru kendiliğinden kalkar (§4.6.1-3). Bugünkü "Kargo Ücreti Eksik" çekmecesi iki seçeneklidir ve yalnız tutar ürün toplamına eşitken çıkar (`PaymentMatcherService.Match`, `ShipmentDirectiveDrawer`); üçüncü seçenek eklenir.
- Otomatik onayda fazla da eksik de oluşmaz, çünkü kural kuruşu kuruşuna eşitlik ister (§5-5).

### 4.4 Dekont ile havale aynı borç için gelirse
İlke: bir para iki kez onaylanmaz ve bir müşterinin sorusu başka bir müşterinin dekontuyla kapanmaz. Bu bölümdeki müşteri karşılaştırmaları **kişi düzeyindedir** (§2 "Kişi"): "dekontun müşterisi", "seçilen müşteri" ve "öneri" kişinin bütün asıl kayıtlarıyla karşılaştırılır. Dekontun müşterisi tek satıra çözülür (`PaymentMatchReconciler` 379-385); satır düzeyinde aransaydı IG'deki dekont ile TT'ye önerilen havale aynı parayı iki kez onaylatırdı.
- **İlk değerlendirmede** açık bir dekont gap'i (`TryResolveGapAsync`) bu havaleyi tek aday buluyorsa havale `ClosedByReceipt` olur, ama **yalnız bağ doğrulanmışsa**: öneri dekontun kişisiyse ya da gönderen adının token'ları açıklamada geçiyorsa (bugünkü öğretme kuralı, `PaymentMatchReconciler` sınıf özeti). Doğrulanmamışsa havale `Awaiting` olur (neden `ReceiptOtherCustomer`; gap yalnız onaylı dekont için açıldığından `PendingReceipt` değil) ve dekont aday olarak listelenir. Öneri yoksa düz "Evet" sunulmaz; seçenekler "Bu dekontun havalesi", "Başka müşteri" ve "Hayır"dır.
- **"Evet" sayfasında aday dekont:** aynı tutar, ±2 gün, kişisi seçilen müşterininki ya da bilinmeyen; ilk değerlendirmede bu havaleyi tek aday bulan gap'in dekontu kişisine bakılmadan adaydır.
  - Bekleyen dekont için "Bu dekontu onayla": yeni satır yaratılmaz, dekont onaylanır ve harekete bağlanır (`receiptPaymentId`).
  - Harekete bağlanmamış onaylı dekont için "Bu dekontun havalesi": `ClosedByReceipt`, yeni ödeme yok. Düz "Evet" yine seçilebilir (iki ayrı ödeme), sayfa uyarır.
- **Dekont onaylanırken** (`PaymentApprovalService`, yalnız panel; ekranı §7):
  - (Üç hâlde de, §2.7.) Aynı tutarda, ±2 gün içinde, dekontun çözülen kişisinde banka kaynaklı onaylı bir ödeme varsa onay açık bir seçim ister. Müşteri çözülemiyorsa bu arama bütün müşterilerde yapılır. **"Aynı ödeme"** dekontu sabit gerekçeyle ("Bu ödeme banka havalesiyle zaten onaylandı") kapatır; ret push'u gitmez, olay iki ödemeyi birbirine bağlar (banka onayı geri alınırsa dekont yeniden incelemeye döner, §11.2). **"Ayrı ödeme"** dekontu normal onaylar.
  - (Yalnız `enabled`'da, §2.7.) Aynı tutarda, ±2 gün içinde ve önerisi dekontun çözülen kişisi olan **tek bir** `Awaiting` soru varsa soru aynı işlemde `ClosedByReceipt` olur ve olay yazılır. Öneri başka kişiyse, öneri yoksa ya da birden çok soru varsa hiçbir soru değişmez; liste dekontu aday olarak gösterir. Böylece dekontlarda "Tümünü onayla" başka bir müşterinin havalesini sessizce kapatamaz. `Unmatched` satır kendiliğinden kapanmaz: "Hayır" denmiş ya da geri alınmış havaleyi reddedilen müşteriye bağlardı.
- **Gecikmeli bağdaştırma** (`PaymentMatchReconcileJob`, `ApprovalDelay` 20 dk) ve gap çözümü yalnız `Decision = None` satırlara bağ kurar; `CandidatesAsync`'teki `taken` kümesi `Decision ≠ None` satırları da dışlar. `enabled` iken aynı doğrulama kuralına uyar: doğrulanmış bağda `Decision = ClosedByReceipt`'i aynı SaveChanges'te yazar (revizyon ve olay), doğrulanmamışsa bağ kurmaz, gap bırakır. Bayrak kapalıyken bugünkü gibi. Admin elle eşleme ve kaldırma (`ManualMatchAsync`, `UnmatchAsync`) `Decision ≠ None` satırda reddedilir: "Bu hareket karar katmanında; panelden yönetin."
- **Gap yalnız dekont için:** `ReturnPaymentsToGapsAsync` yalnız `Source = Receipt` ödemeyi gap'e döndürür; banka onaylı ödeme için gap açılmaz (S2b).
- Bankadan onaylanmış bir ödemeye sonradan dekont yüklenirse `bank-already-approved` yumuşak bayrağı döner (`ShopperPaymentSubmissionService`, S5). Bu bir red değil, uyarı. Asıl koruma onay anındaki "aynı ödeme" seçimidir; yükleme anında havale henüz çekilmemiş olabilir.

### 4.5 Gölge ölçümü
- Gölge ölçümü (`PaymentMatchMetrics`) yalnız `Decision ∈ {None, ClosedByReceipt}` satırları sayar. "Dekont onaylı karar" için bağlı ödemenin `Source = Receipt` olması gerekir; bugün yalnız `PaymentId` doluluğuna bakıyor (`HasReceipt`). Süzülmezse banka onayları da sayılır ve eşik kendi kendini doğrular. `Source` S2a'da geri doldurulur; doldurulmasaydı eski satırlar (NULL) ölçümden düşerdi.
- IBAN hafızası sonradan değişince üretilen önerinin ayrımı (`;reeval` eki, #551) Faz 2 sayaçlarında da korunur; kişi düzeyi eşleştirme (§2.4) `MatcherVersion` ile ayrılır: ölçüm güncel sürümü sayar, eski sürümün satırlarını ayrı gösterir.
- `MeetsPhase2Threshold` (200 dekont kararı, %2) kapı değildir; gölge isabeti bilgisi olarak sayfada kalır ve kapı gibi gösterilmez. Otomatik onayın kapısı §16-6, sayaçları §11.4.

### 4.6 Kargo (sunucu bağlar)

**Neden.** Bugün siparişi kargo dosyasına yalnız masaüstündeki "Gelen Ödemeler" bağlıyor (`DekontEkleViewModel` 325-358 → `SalesEditRouter` 1150-1212). Panel onayı yalnız etiket ve bildirim (`PanelPaymentsController` 111-146), panel kargo ucu salt okunur (`PanelShipmentsController` 67). "Ödendi" sayılmak ise bağa bakıyor: rozet (`PrintedLabelsViewModel` 362), düzeltilmiş ödeme isteği (`CorrectedPaymentRequests` 208), `PaidPrice` (`SetCodePriceCommandHandler` 176-178). Bağ kurulmazsa para onaylanır ama etiket "ödeme istendi" kalır ve fiyat düzeltmesi müşteriye yeniden istek önerir. Sahip kararı 25.11.2026 (§16-8): bağı, "Evet" nerede verilirse verilsin, baştan **sunucu** kurar.

**Seçilen yol:** kalıcı kargo adımı + komut hattından bağımsız bağlama çekirdeği. `SalesCommandService` hattı kullanılmaz: kimlik ve sürüm kapısı (118-124) panelden ve otomatik onaydan geçilemez, atlamak ikinci bir giriş açardı; günlüğe de sahte kopya yazardı. Hat dışı emsal `PaymentRequestLeaseService` 166-193 (işlem + `SalesCommandLock`). `SalesCommandScope` aynı derlemede kurulabilir (kurucu `internal`, 31).

#### 4.6.1 Akış
1. **Evet'in SaveChanges'i** ödemeyle birlikte ödeme × müşteri satırı başına bir `PaymentShipmentStep` (`Waiting`) ve o satırın donmuş siparişlerini (`PaymentShipmentStepOrder`) yazar. Müşteri satırı siparişin müşterisidir (aynı kişinin IG ve TT satırı iki adım); havuz ve karar bugün de müşteri satırı başınadır. Otomatik onay (§5) aynı adımı yazar.
2. **Küme sunucudan gelir:** `confirm`'de seçilen kiralı (`Lease`), `session:{id}` kapsamlı kayıtların siparişleri; durum `Delivered` ya da `Unconfirmed`. `begin` bu kümeyi ve toplamını sunucununkiyle birebir doğrulamıştır (`PaymentRequestLeaseService` 558-561); `Unconfirmed` yalnız mesajın sonucunun bilinmediğini söyler ve isteği insan seçmiştir. İstemci sipariş Id'si göndermez; müşterinin bütün bağsız etiketleri hiç bağlanmaz.
3. **Karar bekleyen doğan adım (`NeedsDecision`):**
   - *Sahibin saydığı kararlar (§16-8).* `Shortage`: tutar seçilen isteklerin kalanından küçük. `RecipientPays`: **kişi** alıcı ödemeli, yani kişinin herhangi bir asıl kaydında `WpfCustomerProjection.RecipientPaysActive` açık; masaüstüyle aynı kural (`PaymentPerson` 60-64, `MainShellViewModel` 2440-2461, sahip kararı 10.10). Satırdan okunsaydı bayrağı IG satırında olan kişinin TT adımı sorulmadan bağlanırdı. Koşulu bugünkü çekmeceninkidir (`PaymentMatcherService` 63-87; bayrağı okumaz, tutarın ürün toplamına eşitliğine bakar): kargo ayarı açık, seçilen isteklerin ürün toplamı eşiğin altında ve aralarında kargo istememiş istek var (`TotalBeforeBalance = ProductTotal`). Kargosu istenip ödenmiş istekte sorulmaz, yoksa ücret iki kez alınırdı. Eşiğe ulaşan kişide bağlandıktan sonra `Threshold` sorulur (6).
   - *Sunucunun hangi siparişin ödendiğini bilemediği onaylar (soru 6).* `OrderSetUnknown`: istek seçilmemiş ya da seçilen kayıt kirasız, `cumulative` ya da `legacy`. `OrdersChanged`: kümenin güncel toplamı kaydın `ProductTotal`'ından farklı. Kümesi olmayan onayda onaylanan müşteri için siparişsiz bir adım yazılır; iş kaybolmaz.
   - Birden çok neden varsa tek `Reason` yazılır, sıra: `OrderSetUnknown`, `OrdersChanged`, `Shortage`, `RecipientPays`; karar hepsini kapatır.
   - **`Shortage` isteğin kalanını izler.** Adımın seçtiği kişi×kapsamların kalanı 0'a inince ("Evet", dekont onayının tahsisi; S5'te ödemesiz tahsis) bekleyen `Shortage` sorusu aynı SaveChanges'te kalkar: öbür nedenler yeniden bakılır, neden kalmadıysa adım bağlıysa `Bound` (eşik için 6), değilse `Waiting` olur; `Version` artar, ekrandaki eski karar 409 `stale` alır. Bir geri alma (onay ya da tahsis) kalanı büyütürse, o kişi×kapsamı seçmiş ve dosyası `Shipped` olmayan onaylı adım `Shortage` ile yeniden sorulur (bağlıysa `BoundNeedsDecision`, değilse `NeedsDecision`). İzlemeseydi 500 + 80 ödeyen müşterinin ilk adımı kartta "kargo eksik" diye kalır, "Alıcı öder" denince ödenmiş kargo bir daha istenirdi.
4. **Koşucu** commit'ten sonra yeni bir DI kapsamında `Waiting` ve `Reverting` adımları işler (aynı müşteride önce `Reverting`); kalanı 5 dakikalık iş (`NextAttemptAt`) alır. Sorular bayrağına, banka anahtarına ve bağlantı durumuna bakmaz: onaylanmış ya da geri alınmış ödemenin adımı her durumda biter. Tek işlemde sırayla: mod On denetimi; asıl müşteri (`CustomerIdResolver`); `SalesCommandLock.ShipmentResource` kilidi; müşterinin yeniden çözümü (birleştirici siparişin `SyncVersion`'ını artırmaz, `CustomerIdentityMerger` 62-65, jeton birleştirmeyi göstermez); açık dosyalar; donmuş kümeden bugünkü aday süzgecini geçenler (`SalesCommandScope` 357-362, kopya `Guid.Empty` ve yalnız bellekte, yani yalnız devredilmiş yayınlar); bağlama, tutar türetme, adımın yazımı. Müşteri SaveChanges'ten sonra, commit'ten önce bir kez daha çözülür; değiştiyse işlem geri alınıp baştan kurulur. Birleştirici `ship` kilidi almaz (`CustomerIdentityMerger` 56-70): okumayla kayıt arasında bitmiş bir birleştirme dosyayı kopyada, siparişi asılda bırakırdı; yazma kilidi bizdeyken ise birleştirici bekler ve jetonda çakışır.
5. Devredilmemiş yayının siparişi bekler (`SessionLive`); bağ devirde kurulur. Kümenin bir kısmı devredilmişse o kısım bağlanır, adım `Waiting`/`SessionLive` kalır. Süzgeçten düşen (iptal edilmiş, bağlanmış, kargo bağı bilinmeyen) sipariş bağlanmaz, sayısı olaya yazılır; **onaydan sonra iptal edilmiş** sipariş düştüyse adım bağlandıktan sonra `BoundNeedsDecision` (`OrdersChanged`) olur, ödenmiş ürünün iptali görünmeden geçmez. **Kümenin hepsi düşerse** adım sessizce `Bound` olmaz, `NothingToBind` ile karar ister: yanlış istek seçilmiş ya da ödeme "Elle gir"le zaten işlenmiş olabilir; sessiz kapanış yeni ödemenin siparişini bağsız bırakırdı. Tek istisna kalan ödemesidir: siparişler aynı kişi×kapsamın **başka** onaylı adımıyla bağlanmış ve bu ödeme o kişi×kapsama tahsis yazmışsa adım `Bound` olur. Çakışmada (jeton, 1205, kilit zaman aşımı) en çok üç deneme, sonra `NextAttemptAt`; deneme sınırı aşılırsa adım `RetryExhausted` ile listeye düşer ve saatte bir yeniden denenir (iş bırakılmaz).
6. **Bağlandıktan sonra sorulan:** kümede devir bekleyen sipariş kalmayınca, müşteri satırının havuzu lisans ayarındaki eşiğe ulaştıysa adım `BoundNeedsDecision` (`Threshold`) olur; bekleyen başka neden varsa (`Shortage`, `OrdersChanged`) önce o sorulur. Kısmen bağlı adım eşikte durdurulsaydı canlı yayının siparişi devirde bağlanmazdı. Sıra bugünkü masaüstüyle aynı: kargo ücreti eksikse önce sorulur (`DekontEkleViewModel` 154-158), eşik bağlamadan sonra (161-167; `SalesEditRouter` 1162-1211).
7. **Mod Off** (acil `sales-sync disable`): bağlanmaz, adım `ModeOff` ile listede kalır; iş masaüstünde "Elle gir" ile yapılır.

#### 4.6.2 Karar (yalnız masaüstü)
- `decide {decision, stepVersion, shipmentVersion?, requestId}`; `decision`: `normal`, `hold`, `ship-now`, `recipient-pays`. Kümeyi bağlar ve kararı aynı işlemde uygular. `stepVersion` ya da `shipmentVersion` tutmazsa 409 `stale`; aynı `requestId` 200 ile aynı sonuç; mod Off'ta 409 `mode-off`. Sorular bayrağına ve bağlantı durumuna bakmaz (§2.7).
- **Geçerlilik:** yalnız `NeedsDecision` ve `BoundNeedsDecision` adımda çalışır; `Waiting` ve `Reverting` adımda (`RetryExhausted` dahil) 409 `stale`: birini koşucu zaten bağlayacak, öbüründe geri alınmış ödemenin siparişi bağlanırdı. `Threshold`'da yalnız `ship-now` ve `hold` (eşik çekmecesinin iki düğmesi), boş kümede yalnız `normal`, öbür nedenlerde `normal`, `hold`, `recipient-pays`; başkası 400.
- **Dosya kargolanmışsa** `decide` karar yazmaz, adımı `Bound` yapar ve "zaten kargolandı" döner. Karar çekirdeği (110-142) bu denetimi içermez (işleyicide 105-106), `decide` kendisi yapar; yoksa sürümü tutan bir `hold` kargolanmış dosyayı `Held` yapardı. `Threshold` ya da `Shortage` bekleyen bağlı adımın dosyası artık `Pending` ya da `Held` değilse ("Elle gir" ya da başka adımın kararı kapatmış) 5 dakikalık iş de adımı `Bound` yapar; `OrdersChanged` operatör görmeden kapanmaz.
- **Eşik havuz başınadır:** `Threshold` kararı aynı işlemde (`ship` kilidi altında) müşteri satırının öbür `Threshold` adımlarını da `Bound` yapar; iki havalesi eşiği aşan müşterinin ikinci sorusu açık kalmaz.
- Yalnız donmuş küme bağlanır. `OrderSetUnknown` ve `NothingToBind`'de küme boş olabilir: bağlanacak bir şey yoktur, operatör gerekirse "Elle gir" ile bağlar ve `normal` adımı kapatır. Kümesi aynı kişi×kapsamın başka onaylı adımıyla bağlanmış adımda bağlama yapılmaz; karar kümenin bağlı olduğu dosyaya uygulanır ve `shipmentVersion` zorunludur.
- Yayın henüz devredilmediyse karar adımda saklanır (`Decision`) ve adım `Waiting`/`SessionLive` olur; koşucu devirde kümeyi bağlar ve saklanan kararı uygular, yeniden sormaz.
- Yapışkan "alıcı öder" bayrağı (`CustomerRepository.SetRecipientPaysActive`) ve "kazandın" mesajı (`PaymentRequestService.OpenShippingWonWhatsApp`) WPF'te kalır; `decide` başarılı olunca WPF uygular.
- Durum değiştiyse commit'ten sonra `ShipmentStatusChanged` etiket kuralı, komut hattıyla eşlikte (`SalesCommandService.ApplyAsync`).
- Panel kararı sormaz, yalnız "Kargo kararı masaüstünde" satırını gösterir.

#### 4.6.3 Veri
- **`PaymentShipmentStep`:** `Id`, `PaymentId` (FK, kaskad), `LicenseId` (FK'sız), `CustomerKey nvarchar(64)` BIN2 (`Orders.CustomerId` biçimi, ordinal; GUID'siz yerel müşteri düşmesin), `State`, `Reason`, `ProposedShipmentId`, `Decision?`, `LastRequestId?`, `Attempts`, `NextAttemptAt`, `Version` (jeton).
  - `State`: `Waiting`, `NeedsDecision`, `Bound`, `BoundNeedsDecision`, `Reverting`, `Closed`.
  - `Reason`: `SessionLive`, `ModeOff`, `Shortage`, `RecipientPays`, `OrderSetUnknown`, `OrdersChanged`, `NothingToBind`, `Threshold`, `RetryExhausted`.
  - Tekil `(PaymentId, CustomerKey)`, `State <> Closed AND State <> Reverting` süzgeciyle: geri alınan dekont, bağı henüz çözülmemişken de (mod Off, kilit çakışması) aynı `PaymentId` ile yeniden onaylanabilir (§11.2).
  - **Bekleyen adım** (kart, menü sayısı, `shippingPending`, `shipping-steps?state=pending`): `State` `NeedsDecision` ya da `BoundNeedsDecision`; ya da `State` `Waiting` veya `Reverting` iken `Reason` `ModeOff` ya da `RetryExhausted`. `SessionLive` bekleyen sayılmaz, devirde kendiliğinden biter; hâli Onaylananlar satırında görünür (§6), bir günü geçerse yöneticiye yazılır (§14).
- **`PaymentShipmentStepOrder`:** `(StepId, OrderId)` PK, `LicenseId`, `RecordId`, `PriceAtApproval`, `BoundShipmentId?`, `BoundSetAt?`; indeks `(LicenseId, OrderId)`. Orders'a FK yok (gerekçe `PaymentRequestRecordOrder` ile aynı).
- `Orders`, `Shipments`, `SalesCommandLogs`, `WpfReplicas` ve SQLite şeması değişmez; iz `BankDecisionEvent` (yalnız Id). Enum'lar S2a'da tanımlanır.

#### 4.6.4 Yazım kuralları
- Bağ alanları komutla aynı: `ShipmentId`, `ShipmentSetAt` (sunucu saati), `SyncVersion` + 1; `CommandedAt` ve `Version` dosyada. Mod On'da push bağı değiştiremez (K4, `OrderUnitMerge`; açılış penceresi yalnız boş bağı doldurur). Eski istemci de yakınsar: bağ da NULL bağ da akıştan iner (`SalesFeedRepository` 241); WPF'in eşzamanlı `attach-shipment`'ı boş aday bulur, hata vermez.
- Yeni dosya: Pending, `AmountDerivedAt` ve `CommandedAt` dolu, `OriginReplicaId` `SalesModeDisable` kuralıyla (yayının sahibi, yoksa siparişin kaynağı; zincir en yeniye; payı büyük olan: 56, 149-200). Boş kalsa `SalesModeGate` 6. maddesi (128) `enable`'ı düşürürdü.
- Fiyat onaydan sonra düzeltildiyse (`Price ≠ PriceAtApproval`) `PaidPrice` boşsa `PaidPrice = PriceAtApproval` yazılır ve `PriceSetAt` damgalanır (ödendiği fiyat). Çözmede yalnız bu yazım silinir; ayırt eden `PriceSetAt = BoundSetAt` eşitliği.
- **Çekirdekler (S1b, davranışsız):** `ShipmentBindCore` (`AttachShipmentCommandHandler` 113-145) ve `ShipmentDecisionCore` (`ShipmentDecisionCommandHandler` 110-142). Ayrıştırma, kilit, aday ve `expected` işleyicide kalır; yeni dosyanın `OriginReplicaId`'si çekirdeğe parametredir (işleyici çağıran kopyayı, koşucu `SalesModeDisable` sonucunu verir). Çekirdek var olan 19 + 6 testle (`SalesShipmentCommandsTests`, `SalesCommandsRelationalTests.Shipments`) değişmeden sınanır.
- `OrderDeck.Shared/Sales/ShippingRules` ücret ve eşik kuralının tek tanımı olur; bugün dört kopya var: sunucuda `PaymentRequestLeaseService.ShippingFee` (686), WPF'te `PaymentRequestService.ComputeShipping` (2364), `PaymentMatcherService.Match` (58) ve `ShipmentService.EvaluatePool` (186). S1b dördünü de ona bağlar; metin ve dallanma yerinde kalır, var olan testleri değişmeden geçer.

#### 4.6.5 Geri alma
- Geri alma (§11.2) adımı aynı SaveChanges'te `Reverting` yapar. Koşucu yalnız **adımın kurduğu** (`BoundShipmentId` ve `BoundSetAt`, siparişin `ShipmentId` ve `ShipmentSetAt`'ine eşit) ve kargoya verilmemiş bağı çözer (`ShipmentId` boş, `ShipmentSetAt` sunucu saati; damga hiç silinmez), dosyayı yeniden türetir, adımı `Closed` yapar. Henüz bağlanmamış adım doğrudan `Closed` olur. "Elle gir"in ya da başka bilgisayarın sonradan kurduğu bağ korunur.
- Siparişi kapsayan başka bir onaylı adım varsa bağ çözülmez, ona geçer (`BoundShipmentId` ve `BoundSetAt` o adımın sipariş satırına yazılır). Geri alma o kişi×kapsamın kalanını büyüttüyse `Shortage` yeniden sorulur (§4.6.1-3).
- Dosya `Shipped` ise bağ kalır; olay satırı ve "kargolandı" uyarısı yazılır. Karar adımı açılmaz; isteğin kalanı §2.1 gereği açık görünür.
- Adımda verilmiş kargo kararının etkisi geri alınmaz (§4.6.7, soru 8).

#### 4.6.6 Bağlayıcı kurallar
1. Kümeyi istemci vermez; "bütün bağsızlar" hiç bağlanmaz (soru 6'nın cevabı 2 olmadıkça).
2. Her bağlama, çözme ve `decide` adımı jetonla (`Version`) yazar; geri alınmış ödeme bağlı kalamaz.
3. İşlem başına tek `ship` kilidi; dosyalar siparişlerden önce yüklenir (`ShipmentAmountDeriver` 21-26).
4. `WpfReplicas` ve `SalesCommandLogs` satırı yazılmaz.
5. `Held` dosya `normal` kararında `Held` kalır.
6. Kümesi boş kalan ya da denemesi tükenen adım sessizce kapanmaz, listede görünür (§4.6.3).

#### 4.6.7 Bilinen sınırlar
- Canlı yayında verilen "Evet"in bağı devirde kurulur ve akış devredilmemiş siparişi indirmez (`SalesChangesFeed` 66); Onaylananlar satırı "yayın devredilince bağlanacak" der. Bu arada masaüstü ödenmiş etikete yeniden istek önerebilir; ödeme isteği listesine eklemeli "ödendi" işareti B-W1'de değerlendirilir.
- "Elle gir" bütün bağsızları bağlar (bugünkü davranış); geri al onu çözmez. Bu yüzden ödenmemiş bir yayının tahsissiz isteği de "ödenmiş görünüyor" işareti alabilir; seçili gelmez, yayıncı seçer.
- "Elle gir"le önce bağlanmış ödemeye sonradan "Evet" denirse küme boş kalır, adım `NothingToBind` ile bir kez sorulur (`normal` kapatır). Müşterisi bilinmeyen eski dekontla (WPF'ten gelmiş, §2.3) ödenip siparişi henüz bağlanmamış istek açık görünür; yayıncı seçimi değiştirir.
- **Geri alma, verilmiş kargo kararının etkisini geri almaz:** `decide` ile `Held` ya da `RecipientPays` olmuş dosya öyle kalır, kişinin "alıcı öder" bayrağı açık kalır. Bugün de bu bayrağı kapatan ekran yok (`SetRecipientPaysActive`'in tek çağrısı `true`, `DekontEkleViewModel` 282). Geri alma yanıtı ve masaüstü uyarır; tam geri alma soru 8.
- WPF'in istek mesajı aynı yayında ödenmiş kısmı da ister (`PaymentLeaseCoordinator` 549 bağa ve tahsise bakmaz). Sunucu kalanı doğru hesaplar (§2.1, §4.3 tavanı). Ödeneni mesajdan düşmek ayrı iştir; yapılırsa kayıttaki `NetAmount` tahsis öncesi anlamını korur, yoksa tahsis iki kez düşer.
- Havale tek müşteriye onaylanır. İki kişilik havalede öbür kişinin siparişi "Elle gir" ile bağlanır; onaylanan müşteriye yazılan fazla "Fazlayı iade ettim" ile geri alınır (S5).
- `disable` ile koşucunun dar yarışı bugünkü gibi (`SalesSync` `disable` sınıf özeti).
- Güvenilmez küme oranı ölçülmedi; pilottan önce salt okunur sorguyla (§14), pilotta S3a olayıyla (§2.6) ölçülür. Yüksek çıkarsa "Evet"lerin çoğu masaüstü kararı bekler; soru 6 o zaman rakamla yeniden sorulur.
- Lisansın mod On olduğu depodan görülmez; pilot günü `sales-sync status` ile bakılır.

## 5. Otomatik onay kuralı

### 5.1 Koşullar
Bütün koşullar her zaman değerlendirilir; ilk tutmayan koşulda durulmaz. 1. koşul dışında tutmayanların hepsi `AskReasons`'a yazılır; her koşulun en az bir kodu vardır (§5.2). `WouldAutoApprove` 1. koşul hariç hesaplanır (§11.4). Koşullardan biri tutmazsa havale `Awaiting` olur. Öneri yoksa (yalnız aday var: §2.4, §4.4) kişiye bağlı koşullar (2-6) hesaplanmaz, `WouldAutoApprove` yanlıştır ve nedenler adayınkidir (`IbanShared`, `IbanOtherCustomer`, `ReceiptOtherCustomer`; 7 tutmuyorsa `TooOld`). Öneri de aday da yoksa havale `Unmatched`'tir (§3).

1. Lisansta `BankAutoApproveEnabled` açık ve `enabled` (§2.7) doğru.
2. Öneriyi IBAN hafızası **destekliyor** (`SupportedByIbanMemory`): IBAN tek başına ya da kullanıcı adıyla birlikte aynı kişiyi gösteriyor. Koşul `Layer`'a bakmaz; ikisi tuttuğunda katman `UsernameInDescription` yazıldığı için en güçlü durum hep sorulurdu. Yalnız kullanıcı adına dayanan öneri her zaman sorulur; ad katmanı (`NameAmount`) hiç otomatik onaylanmaz.
3. IBAN'ın onaylı çiftleri asıl kayda çevrilip kişiye toplandığında tam olarak **bir** kişiye düşüyor (§2.5), önerilen müşteri o kişinin bir kaydı, o kişinin çiftlerinden en az birinin kaynağı `BankConfirm`, havalenin özeti geçerli IBAN'dan (`CounterpartyIbanHashValid`) ve `BankIbanState` ne `Shared` ne `Disputed`. Sayım müşteri başına değil kişi başınadır; aynı kişinin IG ve TT satırlarındaki iki çift koşulu bozmaz. Ortak IBAN koruması (Papara havuzu, aile hesabı) sahibe soru, S6'dan önce cevaplanır (§16).
4. Müşteri asıl kayıt: birleştirilmiş, silinmiş ya da KVKK ile temizlenmiş değil (§12).
5. Tutar kuralı (karar §16-2), uygun beklentilerin (§4.2) kalanları üzerinden, kuruşu kuruşuna:
   - havaleye eşit **tam olarak bir** beklenti varsa o kapanır;
   - yoksa ve **bütün** uygun beklentilerin toplamı havaleye eşitse hepsi kapanır;
   - aksi hâlde sorulur: aynı tutarda iki ayrı beklenti, yalnız bir alt kümenin toplamına eşitlik (hangisinin ödendiği belirsiz; tahmin yürütülmez), uygun beklenti yok;
   - kişide açık ve süresi geçmemiş güvenilmez kayıt (§2.1), canlı kira ya da ödemeden sonra daha büyük tutarla yinelenmiş istek varsa sorulur;
   - lisansın `SalesSyncMode`'u On ve `SalesModeGate`'in 1. ve 2. maddesi tutuyor (13-19, 66-77): son 14 günde görülmüş her etkinleştirme alt sınırı (`MinClientVersion`) karşılıyor; son 14 günde görülmüş her kopya protokol 2, `featureLevel` ≥ 3, geri doldurması ve ödeme geçmişi tamam, parmak izi çakışması yok. Ayrı bir "W2+" kapısı yok: aynı denetim orada hazır, ikincisi ayrışırdı; kişi anahtarı düzeltmesini de içerir. Mod kapanırsa otomatik onay ve fazla hesabı durur. Eski sürüm PC'nin istekleri kayda girmez, toplam eksik kalır.
6. Çift ödeme izi yok. İzler kişinin **bütün** asıl kayıtlarında aranır; dekont ya da ödeme kişinin başka bir satırına yazılmış olabilir:
   - aynı tutarda, ±2 gün içinde, müşterisi bu kişinin bir kaydı **ya da bilinmeyen** (`Payment.WpfCustomerId` null; shopper bağı çözülemeyen) bekleyen dekont yok;
   - bu kişinin kayıtlarında aynı tutarda, ±2 gün içinde, hiçbir harekete bağlanmamış onaylı ödeme yok;
   - kişinin açık beklentilerinden birinin yayınının başlangıcından (`StreamSession.StartedAt`; yayın satırı yoksa hep) bu yana kişinin bir kaydına onaylanmış, geri alınmamış ve hiçbir beklentiye tahsis edilmemiş ödeme yok (§4.2). Varsa beklentinin "açık" görünmesi güvenilir değildir. Başlangıç istekten değil yayından alınır: ödeme istekten önce gelebilir ve hatırlatma istek anını yeniler, ikisinde de iz düşerdi.
7. Havale en fazla 3 günlük. Geriye dönük doldurulan eski hareketler otomatik onaylanmaz.
8. Başka bir kişinin doğrulanmamış açık dekont gap'i bu havaleyi tek aday bulmuyor (§4.4). Koşul olmasaydı §4.4'ün sorduğu havale `WouldAutoApprove` sayılırdı (§11.4).

### 5.2 Sorma nedenleri (kapalı küme)
Metni sunucu üretir; panel ve WPF aynı metni gösterir. Bayrak durumu hiçbir zaman neden olarak gösterilmez.

| Kod | Metin |
|---|---|
| `UsernameOnly` | Öneri yalnız açıklamadaki kullanıcı adına dayanıyor |
| `NameOnly` | Öneri yalnız ad benzerliğine dayanıyor |
| `IbanNotConfirmed` | Bu IBAN bu müşteri için havale onayıyla henüz onaylanmadı |
| `IbanShared` | Bu IBAN'dan birden çok müşteri ödeme yaptı |
| `IbanOtherCustomer` | Bu IBAN başka bir müşteriye kayıtlı, açıklamadaki kullanıcı adı farklı |
| `IbanDisputed` | Bu IBAN için daha önce "Hayır" ya da geri alma var |
| `IbanHashUnverified` | Gönderen IBAN'ı tam gelmedi; bu havaleden öğrenilemez |
| `CustomerNotCanonical` | Önerilen müşteri birleştirilmiş ya da silinmiş |
| `NoExpectation` | Açık istek yok ya da istenen tutar bilinmiyor |
| `AmountMismatch` | İstenen {X} ₺, gelen {Y} ₺ |
| `AmbiguousExpectations` | Açık isteklerden hangisinin ödendiği belirsiz |
| `ExpectationUnreliable` | İstek kaydı güvenilir değil (sonucu bilinmeyen gönderim, genel toplam isteği, sonradan değişen sipariş ya da bir kısmı ödenmiş görünen istek) |
| `RequestInProgress` | Bu kişiye şu anda ödeme isteği gönderiliyor |
| `SalesSyncNotReady` | Satış senkronu kapalı ya da bir bilgisayar güncel değil |
| `PendingReceipt` | Aynı tutarda bekleyen dekont var |
| `UnlinkedApprovedPayment` | Bu müşteride havaleye ya da isteğe bağlanmamış onaylı ödeme var |
| `ReceiptOtherCustomer` | {Müşteri}'nin dekontuyla aynı tutar |
| `TooOld` | Havale 3 günden eski |

Koşul → kod: 2 → `UsernameOnly`, `NameOnly`; 3 → `IbanNotConfirmed`, `IbanHashUnverified`, `IbanShared`, `IbanOtherCustomer`, `IbanDisputed`; 4 → `CustomerNotCanonical`; 5 → `NoExpectation`, `AmountMismatch`, `AmbiguousExpectations`, `ExpectationUnreliable`, `RequestInProgress` (canlı kira), `SalesSyncNotReady`; 6 → `PendingReceipt`, `UnlinkedApprovedPayment`; 7 → `TooOld`; 8 → `ReceiptOtherCustomer`.

### 5.3 Fail-safe'ler
- Çift onaya karşı koruma: `Decision` jetonu (`DecisionRevision` + `UpdatedAt`), hareket başına tek eşleşme satırı ve ödeme başına tek hareket (§2.4). ReferansNo bu iş için kullanılmaz (§2.3).
- Aynı isteği aynı anda iki onay kapatamaz: tahsis toplamı onayın SaveChanges'inde yeniden okunur ve seçilen kişi×kapsamların vazgeçilmemiş bütün kayıtlarının jetonu (`PaymentRequestRecord.Version`) aynı SaveChanges'te bir artar (S3b; yarış SQL Server testinde). Toplam kişi×kapsam düzeyindedir; yalnız seçilen kaydın jetonu, aynı kapsamın öbür kaydını seçen eşzamanlı onayı görmezdi. Tahsis yazan ya da geri alan her yol ("Evet", dekont onayı, geri alma, tahsis geri alması, S5'te `waive`, `cancel`, `off-bank`) aynısını yapar. Kaybeden insan onayı 409 `stale` alır, otomatik onay yeniden değerlendirilir; dekont onayında bu çakışma bugünkü `not-pending` sayılmaz (`PanelPaymentsController` 132-139 her jeton çakışmasını öyle sayar), baştan kurulur. Müşteri aynı borcu iki kez öderse ikinci havale soru olarak gelir. Hatırlatma mesajı ödenmiş beklentiyi yeniden açmaz (§2.1).
- Eksik ya da fazla ödeme her zaman sorulur.
- Bayrağı kapatmak yeni soruyu ve onayı anında durdurur (kill switch). Durdurulan banka bağlantısı ve kapatılan satış senkronu da durdurur (§2.7, §5-5). Verilmiş onayın geri alınması ve kargo adımları sürer (§2.7).

## 6. API'ler (tek servis: `BankPaymentDecisionService`)

WPF zaten Bearer-Customer ile `/api/panel/*` uçlarını çağırıyor (`LicenseApiClient`, bakiye uçları). Bu yüzden panel ve WPF aynı uçları kullanır. Uçlar `[AllowStockStaff]` taşımaz. Her mutasyon `decisionRevision` (`decide`'da `stepVersion`, `waive`, `cancel` ve `off-bank`'ta kayıt sürümü) ve `requestId` ister (§3); hareketi silinmiş ödemede `decisionRevision` yoktur, jeton `Payment.Status`'tur (§11.2). Soru listeleri ve karar uçları yalnız `enabled`'da açıktır; `approved` listesi, geri alma, bağ kaldırma, "Fazlayı iade ettim", beklenti ve kargo adımı uçları üç hâlde de açıktır (§2.7). `/count` her hâlde döner.

| Uç | Ne yapar |
|---|---|
| `GET /api/panel/bank-transfers?state=awaiting\|unmatched\|approved\|dismissed&cursor=&take=50` | `awaiting` ve `unmatched` en eskiden, `approved` ve `dismissed` en yeniden sıralanır; imleçli sayfalama. `approved` = son 30 günde bankadan onaylanan ödemeler, **`Payment`'tan okunur** (`Status = Approved`, `ApprovalSource` `BankHuman` ya da `BankAuto`; hareketi silinmiş olan da, yalnız ödeme alanları ve "banka hareketi silindi" notuyla), artı `ClosedByReceipt` satırları (personelde son 48 saat, §7). Alanlar: tutar, tarih ve saat, maskeli IBAN, maskeli açıklama (silinmişse "açıklama saklama süresi doldu"), öneri (asıl wpfCustomerId, kullanıcı adı, platform, katman) ya da adaylar (`candidates`, §2.4), `askReasons` (kod + metin), önerilen müşterinin istekleri (kayıt id, sürüm, kapsam, net tutar, kalan, istek ve kapanış anı; işaretler: güvenilmez, süresi geçmiş, ödenmiş görünüyor (siparişleri bağlı ya da yayınından bu yana tahsissiz ödeme var), fazla ödenmiş görünüyor; sunucunun verdiği seçim, §4.2), aday dekontlar, `decisionRevision`. Gönderen adı yok (§2.3). `approved`'da ek olarak `ApprovalSource`, onaylayan (Owner / Staff / System), zaman, fazla satırı ve kargo adımının durumu. `enabled` değilken yalnız `approved` dolu döner (yalnız ödeme alanlarıyla), öbür listeler boştur (§2.7). Hash ve RawJson hiçbir zaman dönmez. |
| `GET .../count` | `{enabled, stopped, revertable, awaiting, unmatched, lastBankDataAt, stuckCount, shippingPending}`. `stopped`: bayrak açık ama `enabled` tutmuyor (§2.7). `revertable`: `approved` kümesi boş değil (aynı sorgu), hâlden bağımsız. `enabled` değilken `awaiting` ve `unmatched` 0. Rozet = `awaiting`. `lastBankDataAt` = `ObifinConnection.LastPolledAt` ile etkin hesapların `BankAccount.LastBankSyncAt` değerlerinin en eskisi. `stuckCount` = "değerlendirilmeyi bekleyen" (§3) ve 15 dakikadan eski satırlar. `shippingPending` = bekleyen kargo adımı sayısı (tanımı §4.6.3: `NeedsDecision` ve `BoundNeedsDecision` hâlleri ile `ModeOff` ya da `RetryExhausted` nedeniyle duran adımlar). Aynı alanlar WPF'e `sales/state`'in eklemeli alanıyla da gider (§8). |
| `POST .../customers {q}` | "Başka müşteri" seçicisi: `WpfCustomerProjections` üzerinde kullanıcı adı, FullName, DisplayName araması; yalnız asıl, silinmemiş kayıtlar, en fazla 20 sonuç. **POST**, çünkü ad parçası URL'ye ve Caddy günlüğüne girmez; adminin araması (#550, `BankaEslestirme` `OnPostCustomerSearchAsync`) ortak servise çıkar. Bugünkü `/api/panel/search` yetmez: müşteriyi siparişlerden türetir (`PanelSearchController` 84-96), yani siparişsiz müşteriyi ve gerçek adı (FullName) bulmaz, birleşmiş kopyanın Id'sini dönebilir ve ad parçası URL'dedir. Döndürdüğü `CustomerId` projeksiyon Id'sinin hex'idir (`WpfCustomerProjection` 7-8). |
| `GET .../{id}/requests?wpfCustomerId=` | "Başka müşteri", aday seçimi ve elle eşlemede seçilen müşterinin kişisinin istekleri: listedekiyle aynı alanlar ve bu havaleye göre varsayılan seçim (§4.2). Yalnız Id alır. S3a. S3b'de kapanmış isteklerin geri alınabilir tahsisleri de salt okunur gelir (tahsis id, tür, tutar). |
| `POST .../{id}/confirm {wpfCustomerId, decisionRevision, requestId, receiptPaymentId?, paymentRequests[{id, version}], shownOverpayment?, shownRemaining?}` | "Evet", "Başka müşteri", elle eşleme ve dekonta bağlama (§4.4). Sunucu `wpfCustomerId`'yi asıl kayda çevirir (`CustomerIdResolver`). `paymentRequests` seçilen isteklerdir; `version` = `PaymentRequestRecord.Version`. Dekonta bağlı satırda (`PaymentId` dolu) 409 `stale` (§3). `shownOverpayment` ve `shownRemaining` S5'ten sonra; fazla doğuran seçimde `shownOverpayment` boşsa 409 `client-update-required` (§4.3). `shipmentDirective` ve sipariş Id'si **yok**: kargo kümesini sunucu kurar, karar ayrı uçtadır (§4.6). `overpaymentToBalance` parametresi yok: fazla §4.3'e göre hesaplanır, sahibin kararı (§16-4) istemciye bırakılmaz. |
| `POST .../{id}/decline` | "Hayır": havale `Unmatched`'e düşer; öneriyi IBAN hafızası destekliyorsa IBAN `Disputed` olur (öneri yoksa olmaz). Öneri alanları (`ProposedWpfCustomerId`, `Layer`, `Confidence`, `Evidence`, `SupportedByIbanMemory`, `AskReasons`) boşalır, `Status = NoProposal` (bugünkü `UnmatchOnceAsync` kuralı): reddedilen müşteri satırda öneri olarak kalmaz; ölçüm olaylardan okur. |
| `POST .../{id}/dismiss {reason, note?}` ve `.../{id}/restore` | Müşteri ödemesi olmayan hareketi kapatır; geri açılabilir. Gerekçe kapalı küme: `OwnTransfer`, `MarketplacePayout`, `Refund`, `Other`. `note` yalnız `Other`'da alınır ve hareketle birlikte 180. günde silinir. |
| `POST .../{id}/revert {reason}` | Banka kaynaklı onayı geri alır ya da `ClosedByReceipt` bağını kaldırır (§11). Gerekçe: `WrongCustomer`, `NotCustomerPayment`, `BankReturned`, `DataSubjectObjection` ("İlgili kişi itirazı"), `Other`. `{id}` = `BankTransactionId`. Hareket silinmişse ödeme `Payment.BankTransactionId` ile bulunur (lisans süzgeci, `ApprovalSource` `BankHuman` ya da `BankAuto`, `Status = Approved`); jeton `Payment.Status`, kaybeden 409 `already-decided` alır (§11.2). |
| `POST .../{id}/overpayment-refunded` | "Fazlayı iade ettim": fazlayı ortak bakiye çekimiyle geri alır (§11.2'deki kural, eksik kalan uyarıyla), olay yazar. `{id}` kuralı `revert`'teki gibi. Yalnız sahip (S5). |
| `GET .../shipping-steps?state=pending` ve `POST .../shipping-steps/{stepId}/decide {decision, stepVersion, shipmentVersion?, requestId}` | Kargo adımı listesi (müşteri, tutar, neden, dosya tutarı ve sürümü, sipariş sayısı) ve masaüstü kararı (§4.6.2). `pending` tanımı §4.6.3'te; iki uç da üç hâlde açıktır (§2.7). Panel yalnız listeyi okur. |
| `GET /api/panel/payment-expectations?wpfCustomerId=` ve `POST .../{recordId}/waive`, `.../{recordId}/cancel` `{version, requestId}` | S5. Açık beklentiler, kalanları ve geri alınabilir tahsisler (panel ve WPF'teki "Eksik X ₺" için; bakiye GET yanıtına da `openExpectations` eklenir). "Kalanı sil" ve "İptal" ödemesiz tahsis satırı ve olay yazar (§2.2). |
| `POST /api/panel/payment-expectations/off-bank {wpfCustomerId, amount, paymentRequests[{id, version}], requestId}` | S5; WPF çağrısı B-W2b'de (Elden/Papara). Sunucu Id'yi asıl kayda çevirir, seçilen `session:` kayıtlarına en eskiden başlayarak `OffBank` tahsisi ve olay yazar. Kalanı aşan kısım yazılmaz, bakiye satırı doğmaz. Jeton tutmazsa 409 `stale`, aynı `requestId` 200. |
| `POST /api/panel/payment-expectations/allocations/{id}/reverse {requestId}` | Tek tahsisi geri alır (`ReversedAt`), kişi×kapsamın jetonlarını artırır (§5.3), olay yazar. Yalnız sahip. S3b'de düz dekont onayının tahsisi, S5'te ödemesiz türler. Bankadan onaylanmış ödemenin tahsisinde 409 `use-bank-revert`: kargo adımı ve fazla ona bağlıdır (§11.2). |

Genel bakiye geri alma yolları `bank-overpayment` satırını ve onun `reversal`'ını 409 `use-bank-revert` ile reddeder (§2.8).

**Yazılmayacak uçlar:** `PUT payment-expectations` (§2.1) ve `GET payments/since` genişlemesi (§8). Panelin ve WPF'in havale listesini açması olay satırıyla denetlenir (KVKK Ö-4'ün karşılığı; `AuditLogEntry` yönetici kimliği istediği için kullanılamaz; S3a).

**Yayıncıya push (S3c):** yeni tip `type=bank-transfer`. Yalnız yeni `Awaiting` için gider; sink ya da tarama koşusu başına tek bildirimde birleştirilir; gövde yalnız sayıdır ("3 havale onay bekliyor"), ad ve tutar taşımaz. `AutoApproved` ve `Unmatched` için push yok. Bugünkü dekont push'u gibi tenant'ın bütün cihazlarına gider (`INotificationSender.SendToCustomerAsync` rol ayırmıyor); gövde yalnız sayı taşıdığı için stok personelinin cihazına düşmesi veri sızdırmaz. Aynı PR bugünkü "Yeni dekont" push'larını da ad, tutar ve bayraktan arındırır (KVKK planı karar 9: `ShopperPaymentSubmissionService` ve `LicensesPaymentsSyncController.BuildPaymentNotification`).

## 7. Panel arayüzü

- `/dekontlar` içinde "Dekontlar | Havaleler" segmenti olur. Yeni sekme açılmaz, çünkü alt menü 5 sekmeyle sınırlı (`lib/nav.ts` `navItemsForRole`).
- Havaleler'de üç liste var:
  - **Onay bekleyen**
  - **Eşleşmeyen**; içinde "Kapatılanlar" süzgeci, geri açma buradan
  - **Onaylananlar**: son 30 gün; kim, ne zaman, insan mı otomatik mi, kargo adımının hâli ("yayın devredilince bağlanacak" dahil). "Geri al", `ClosedByReceipt` satırında "Bağı kaldır" ve (P3) "Fazlayı iade ettim" buradan. Geri alma yanıtı, verilmiş kargo kararının durduğunu söyler (§4.6.7).
- Toplu onay ve çoklu seçim yok; toplu onay "ilk görüşte otomatik yok" kuralını sessizce delerdi.
- Rozet kaynağı `pendingTransfers` = yalnız `awaiting`. Dekontlar sekmesinde dekontlarla toplam sayı görünür; AnaScreen'e ikinci bir satır eklenir (bekleyen dekont satırının deseni). `Unmatched` sayısı yalnız segmentin içinde görünür; müşteri olmayan alacaklar (kendi havalesi, pazaryeri ödemesi) rozeti hiç sıfırlanmaz hâle getirirdi.
- Satıra dokununca bir alt sayfa açılır (`MusterilerScreen` filtre sayfası deseni):
  - Önerilen müşteri ve platform rozeti, tutar, saat, maskeli IBAN, maskeli açıklama. Gönderen adı yok; silinmiş açıklamada "açıklama saklama süresi doldu".
  - Sorma nedenleri: sunucunun metni (§5.2).
  - Öneri yoksa adaylar ("Bu havale kimin?", §2.4); seçince istekler o müşteriye göre yenilenir. Aday ya da müşteri seçilmeden düz "Evet" yoktur.
  - Müşterinin istekleri (§4.2): "Son istenen X ₺", kalan ve işaret ("süresi geçti", "kayıt kesin değil", "ödenmiş görünüyor", "X ₺ fazla ödenmiş görünüyor"); seçim sunucudan gelir, değiştirilebilir. Kapanmış isteğin geri alınabilir tahsisi salt okunur satırdır; sahip "Tahsisi geri al" diyebilir (§6). S5'ten sonra (P3) "Fazla X ₺ bakiyeye eklenecek", "Eksik X ₺" ya da "İstenen tutar bilinmiyor".
  - Kargo için yalnız salt okunur satır: "Kargo kararı masaüstünde" ya da "yayın devredilince bağlanacak" (§4.6). Panelde kargo sorusu yok.
  - Aday dekont varsa "Bu dekontu onayla" ya da "Bu dekontun havalesi" (§4.4).
  - Düğmeler: Evet / Başka müşteri / Hayır. Taşma menüsünde gerekçe seçimli "Müşteri ödemesi değil".
  - "Başka müşteri" yeni seçici ucunu kullanır; seçince istekler o müşteriye göre yenilenir (`requests`, §6).
- **Dekont ekranı (P1):** aynı tutarlı banka ödemesi varken dekont onayı "Aynı ödeme / Ayrı ödeme" sorar (§4.4). "Tümünü onayla" seçim isteyen dekontu atlar, seçili bırakır ve sonuç şeridinde söyler (bugünkü `runBatch` deseni).
- 409 `already-decided` gelirse sonuç şeridinde "Başka yerde yanıtlandı", `stale` gelirse "Bilgi değişti, yenilendi", `matching-stopped` gelirse "Banka bağlantısı durduruldu", `questions-off` gelirse "Havale soruları kapalı", `client-update-required` gelirse "Bu havale fazla ödeme içeriyor; uygulamayı güncelleyin ya da web panelinden onaylayın" görünür; hepsinde liste yenilenir. Son kod P1 ve B-W1'e baştan girer (§4.3): S5 çıktığında sahadaki istemci onu tanır.
- Tazelik: sayaç sorgusu sekme görünürken 30 sn'de bir yenilenir, **yalnız Dekontlar menüsü olan oturumda** (BottomNav deseni). Stok personeli yoklama yapmaz ve 403 üretmez. Liste yalnız segment açıkken çekilir. Panelde bu ilk yoklama olacak. Personel jetonu 15 dakikadır ve yenilenmez (`JwtOptions`, panel `auth/store.ts`); yoklama jeton bitince durur, yoksa 401 personeli form ortasında çıkışa düşürürdü.
- **Personel (varsayılan, §16-1):** soruları görür ve yanıtlar; onaylarda yalnız son 48 saati görür ve geri alır. 30 günlük liste ve "Fazlayı iade ettim" yalnız sahip: panelde personel ciroyu görmez (`Money` kuralı).
- Segment `/count`'ta `enabled`, `stopped` ya da `revertable` varken görünür, üçü de yoksa gizlidir. `enabled` değilken yalnız durum şeridi ("Banka bağlantısı durduruldu" ya da "Havale soruları kapalı") ve Onaylananlar (geri alma) görünür. `enabled` iken `lastBankDataAt` eşikten eskiyse (`Bank:StaleFeedAfter`, varsayılan 2 saat) ya da `stuckCount > 0` ise "Banka verisi X'ten beri gelmiyor" şeridi çıkar.
- Bakiye kartında `bank-overpayment` → "Havale fazlası" (`customerBalance.ts` `kindLabel`) ve "iptal" düğmesi gizli (`CustomerBalanceCard` `canReverse`; sunucu zaten 409 döner). P1'de.
- **P3 (S5'in panel yüzü):** alt sayfada fazla/eksik satırı ve `shown*` alanları; bakiye kartında açık isteğin satırında "Eksik X ₺", "Kalanı sil" ve "İptal"; havale listesinde "Eksik X ₺"; Onaylananlar'da "Fazlayı iade ettim" (yalnız sahip).
- Veri notu (`PaymentDataNotice`) iki sürümlü: sorular kapalıyken bugünkü metin, açıkken havaleli metin (T1 ile).
- **Dağıtım:** web paneli merge'te hemen canlıya çıkıyor (`ci.yml`); kapalı ve geri alınacak onayı olmayan lisansta (`enabled`, `stopped`, `revertable` yok) ekran tamamen gizli kalmalı. Android paneli ise `dist`'i paketliyor (`capacitor.config.ts`'te `server.url` yok), yani P1 de AAB ister. P1 ve P2 (push dokunuşu `/dekontlar?tab=havale` adresine gider; eski sürüm bilinmeyen tipte `null` döner, `deepLink.ts`) tek AAB'de çıkar; Play yüklemesi elle yapılır. Pilot web paneli ve WPF üzerinden koşar, AAB'yi beklemez. P3 de web'de merge'le canlıdır, AAB'si ayrı ve elledir.

## 8. WPF arayüzü

PR adları B-W0, B-W1, B-W2a ve B-W2b: "W1-W3" adı satış senkronu ve ortak bakiye planlarında da var.

- Modal pencere yok; yayın sırasında operatörün işini keser.
- **Yerleşim (sahip kararı 19.10: "Gelen Ödemeler" onay ekranı):** sol menüdeki "Gelen Ödemeler" satırına sayı (dar modda nokta) = `awaiting` + `shippingPending`. Çekmecede iki sekme: **Havaleler** (Onay bekleyen / Eşleşmeyen / Onaylananlar ve üstünde "Kargo kararı bekleyenler" kartı) ve **Elle gir** (bugünkü form, aynen). Havaleler sekmesi panel segmentiyle aynı koşulda ya da `shippingPending` > 0 iken görünür (§2.7). Alt çekmeceler aynı yığında açılır (`DrawerStack`). Üst çubuk rozeti ve "Tümünü gör" sayfası yok; sayfa çekmecenin üstüne gelirdi. Çekmece kuyruk ve kod/fiyat alanını örter; yarım ekranda (960 px) sınanır.
- **Sayaç:** ayrı yoklama servisi yok. `sales/state` yanıtına eklemeli alan (`/count` ile aynı alanlar); kalp atışı 30 sn'de zaten gidiyor (`SalesChangesPullHostedService`). Liste yalnız çekmece açıkken, imleçli sayfalarla çekilir; kimse bakmıyorken PC'ye açıklama inmez. Eski liste tazeymiş gibi gösterilmez; bayat ve durdurulmuş şeritleri panelle aynı.
- "Başka müşteri" için müşteri seçme alt çekmecesi (`VariantPickerDrawer` deseni); öneri yoksa adaylar panelle aynı listelenir (§2.4). Müşteri kimliği yerelde aranmadan önce `ResolveId` ile çözülür; sunucu onu bir kez daha asıl kayda çevirir.
- Cevap çevrimiçi ve doğrudan gönderilir, `requestId` taşır; kuyruğa yazılmaz. Kuyrukta bekleyen bir "Evet", başka PC'nin verdiği "Hayır"ı ezebilir. Çevrimdışıyken düğmeler pasif kalır.
- **Kargo:** "Evet" bağlamayı başlatmaz, sunucu bağlar (§4.6). "Kargo kararı bekleyenler" kartı bekleyen adımları (§4.6.3) listeler; `shippingPending` > 0 iken sorular kapalıyken ya da bağlantı durdurulmuşken de görünür (§2.7); var olan iki çekmece (`ShipmentDirectiveDrawer`, `ShipmentThresholdDrawer`) `decide`'ı çağırır. Yayını devredilmemiş karar adımında kart "karar saklanır, yayın devredilince bağlanacak" der; karar istemeyen `SessionLive` adımı kartta yoktur, Onaylananlar'da görünür (§4.6.3). Kargo zinciri ortak servise çıkmaz; "Elle gir"in yolu değişmez.
- **Yeni SQLite göçü yok.** Onaylananlar çevrimiçi okunur; yerel `Payment` tablosuna banka satırı inmez (tablo artık yazılmıyor ve gösterilmiyor; kopyalamak her PC'ye gereksiz finansal kişisel veri taşırdı). `payments/since` genişlemez, `payment-decision-in` imleci değişmez.
- **B-W0 (bağımsız, sıradaki sürüme binebilir):** bekleyen ödeme kaydı sayaçta ve kapanışta (bugün `SyncOutboxRepository.CountPending` ve `SyncFlushService` `PendingPaymentRecord`'u anmıyor; kapanışta kalan `finish` isteği sunucuda geçici "sonucu bilinmiyor" yapar). `CustomerDetailViewModel.LabelOf`'a `bank-overpayment` → "Havale fazlası".
- **B-W2a (S5 sonrası):** Havaleler sayfasında fazla/eksik satırı ve `shown*` alanları (§4.3); müşteri kartında kişi düzeyinde "Eksik X ₺" (beklenti ucundan).
- **B-W2b (S5 + soru 4):** "Elle gir" formuna ödeme yolu (Havale / Elden / Papara); yalnız Elden ve Papara o kişinin açık isteğini girilen tutar kadar kapatır (`off-bank` ucu, §6). Soru 4 gecikirse B-W2a beklemez. Beklenti kuyruğu, PUT protokolü, `Superseded` revizyonu ve INSERT yolu **yok**.

## 9. Çoklu PC ve panel yakınsaması

- Durumun tek sahibi sunucu. Yerelde soru kopyası ya da imleç tutulmaz; yedekten geri yükleme, cevaplanmış soruları geri getirmez.
- Aynı anda iki basışta ikincisi 409 alır. Emsal: `PanelPaymentDecisionConcurrencyTests`.
- Cevabı veren arayüzde satır hemen kalkar; diğer PC'lerde ve panelde en geç 30 sn içinde (sayaç değişince liste yenilenir).
- Rozet sayısı PC başına değil, lisans geneli.
- Beklenti sunucudaki ödeme isteği kaydından türetilir (§2.1); kural, isteği hangi PC'nin gönderdiğinden bağımsız çalışır. Birden çok açık beklentide §5-5 geçerlidir: tek eşit beklenti ya da bütün açıkların toplamı otomatik onaylanır; alt küme eşitliği ya da aynı tutarlı iki beklenti sorulur.
- Kargo bağı ve kararı sunucudadır; öbür PC'ler dosyayı ve siparişi satış akışından alır. Canlı yayının siparişi devirde iner (§4.6.7).
- Karışık sürüm: satış senkronu sürümünden (Ws) eski bir PC'nin istekleri kayda gelmez. Ayrı sürüm kapısı yok; satış senkronunun mod kapısı (`SalesModeGate`: sürüm, kopya, 14 gün) bütün PC'lerin güncel olmasını zaten ister. Mod kapalıyken otomatik onay ve fazla hesabı kapalı kalır (§5-5, §4.3) ve sunucu kargo bağlamaz (§4.6).
- **Müşteri birleştirme:** `CustomerIdentityMerger` bugün IBAN satırlarını tek tek taşıyor. Yeni tekil indekste aynı çift iki kez oluşursa birleştirme, ve onu koşan müşteri senkronu, düşerdi. Birleştirici (`CustomerIdentityMerger` 129-131) artık çiftleri tekilleştirir (en eski `BankConfirm` kalır) ve `Payment.WpfCustomerId`'yi asıl kayda çevirir; indeksle aynı PR'da (S2a). Ödeme isteği kayıtları ve kargo adımları yeniden anahtarlanmaz: okuyan üye kesişimiyle, koşucu `CustomerIdResolver` ile çözer. Tahsisler kayıt id'sine bağlı olduğu için etkilenmez.

## 10. Shopper tarafı

- **Gizlilik sunucuda sağlanır:** `Source = Bank` satırda shopper DTO'su `PayerName = ""` ve `ReferansNo = null` döner (`ShopperBroadcastersController` `PaymentItem`), istemci sürümünden bağımsız olarak. Banka satırında ad zaten yazılmıyor (§2.3); DTO kuralı ikinci bir kilittir. Eski sürümler `PaymentDetailModal`'da Gönderici ve Referans no gösteriyor; yanlış bir "Evet"te başka bir müşteri gerçek ödeyenin bilgisini görürdü.
- DTO'ya `source` alanı eklenir (`receipt` | `bank`). Yeni bir durum değeri eklenmez: eski istemciler bilinmeyen durumu "Bekliyor" olarak gösteriyor (`DekontlarımScreen.tsx`).
- Banka satırının metinleri (SH1):
  - Liste: "{tutar} havalen alındı", alt yazı "Banka havalesi".
  - Modal başlığı: "Ödeme Detayı".
  - Zaman çizgisi: "Bankaya ulaştı", ardından "Onaylandı".
- Push `type=payment-approved` olarak kalır ki eski sürümler doğru ekrana gitsin; yanına `source=bank` eklenir. Gövde banka onayına ait, sabit ve tutarsız bir metindir (KVKK K-5; bildirim gövdesi Google FCM'ye gider): `BankPaymentApprovedPushBody = "Ödemen alındı."`.
  - Dekont onayının sabiti ("Dekontun onaylandı.") kullanılmaz. `Source = Bank` satırının dekontu yok; dekont yüklememiş müşterinin kilit ekranına "Dekontun onaylandı." düşerdi.
  - Sabit, servisin de erişebildiği tarafsız bir yerde durur: `Services/Push/PaymentPushTexts`. İki dekont sabiti (`PaymentApprovedPushBody`, `PaymentRejectedPushBody`) de `PanelPaymentsController`'dan oraya taşınır (S1). Push `BankPaymentDecisionService`'ten (§6) gider; sabiti controller'dan okusaydı Services→Controllers bağımlılığı doğardı (bugün bu yönde tek bağ `IntakeFormService`'in `PanelLicenseScope` çağrıları, 57 ve 218; yenisi eklenmez).
  - Başlık yayıncı adı. data'da yalnız `type=payment-approved`, `source=bank`, `paymentId`, `licenseId` bulunur.
  - Bir test bunu KVKK planı Görev 19'un testiyle (`ShopperOrderPaymentPushTests`) aynı biçimde sınar: gövde sabite, başlık yayıncı adına tam eşit, `Data.Keys` tam olarak bu dört anahtar.
  - Tutar FCM'ye gitmez; uygulamada görünür (liste: "{tutar} havalen alındı").
- Push yalnız "Evet" ve otomatik onayda gider. Ayar ipucu "Ödemen onaylandığında…" olarak değişir (`ProfilScreen.tsx`).
- Onaylanmamış öneri shopper'a hiç gösterilmez; tahmin yanlışsa başkasının havalesi sızardı.
- Geri alma (§11.2): `WrongCustomer` gerekçesinde satırın `ShopperId`'si boşaltılır; satır o shopper'ın uygulamasından kaybolur ve push gitmez. "Reddedildi" olarak kalsaydı başkasının ödemesi onun uygulamasında dururdu. Diğer gerekçelerde satır sabit metinle ("Ödeme doğrulanamadı") `rejected` görünür.
- Bakiye geçmişinde `bank-overpayment` → "Havale fazlası" (`queries.ts`). Dekont yükleme uyarıları sözlüğüne (`FRAUD_LABELS`, `DekontYukleScreen.tsx`) `bank-already-approved` eklenir; bilinmeyen kod bugün ham adıyla görünüyor.

## 11. Denetim, geri alma ve ölçüm

### 11.1 Denetim
- Her karar bir `BankDecisionEvent` satırı yazar (§2.6). `Payment.ApprovalSource` ve `ApprovedByOperatorId` kimin onayladığını gösterir. Personel kararı `Staff` ve `OperatorId` ile, sahip ve WPF kararı `Owner` ile kaydedilir. Kargo bağlama, çözme ve `decide` da olay yazar (koşucu `System` / `Runner`).

### 11.2 Geri al (`Approved` / `AutoApproved`)
Giriş noktası panelde ve WPF'te "Onaylananlar" listesidir; sorular kapalıyken ve bağlantı durdurulmuşken de açıktır (§2.7). Gerekçe kümesi §6'da ("İlgili kişi itirazı" dahil). Geri alma hareket saklandıkça (en çok 180 gün; arayüz 30 gün gösterir) tam çalışır. **Hareket silinmişse** (180. gün, hesap reddi, "Kaldır ve sil", hizmet sonu) ödeme, tahsis, kargo adımı, fazla ve olay aynen geri alınır; uç aynıdır (§6), yalnız `PaymentMatch` maddesi atlanır ve havale yeniden onaylanamaz. O hâlde IBAN geri sarılamaz: bağı bu havale öğrettiyse (`SourceBankTransactionId`) ve bağ duruyorsa imha kaydıyla silinir, IBAN `Disputed` olur. `ClosedByReceipt` bağının kaldırılacak bir şeyi kalmaz. Aşağıdakilerin hepsi tek SaveChanges'te yapılır:
- **Banka kaynaklı Payment** `Approved → Rejected` olur; `RejectReason` gerekçeye göre sabit metindir. Shopper tarafı §10'da. `PaymentRejected` etiketi uygulanır (`WaLabelEvent.PaymentRejected`).
- **"Evet"le onaylanmış dekont** (`Source = Receipt`) `Rejected` olmaz: `Pending`'e döner, hareketten ayrılır (`BankTransactionId` boşalır) ve normal insan incelemesine geri gider. Shopper, hiç bakılmamış bir dekontu reddedilmiş görmez.
- **Bu ödemeye "Aynı ödeme" ile kapatılmış dekont** (§4.4, olay bağından bulunur) hâlâ o kapanışla `Rejected` ise `Pending`'e döner, ret alanları boşalır, push gitmez. Reddin dayanağı kalkmıştır; aynı PDF yeniden yüklenemez (`ShopperPaymentSubmissionService` 156-164) ve `Rejected` dekont onaylanamaz. İnsanın ayrıca reddettiği dekonta dokunulmaz.
- `PaymentMatch.PaymentId`, `ActualWpfCustomerId`, `DecidedAt` ve `IbanConfirmedAt` ile öneri alanları boşaltılır (`Status = NoProposal`, `decline` ile aynı kural, `AutoApproved` satırda da); `Decision = Unmatched`. `PaymentId` boşaltılmasaydı filtreli tekil indeks yeniden onayı engellerdi.
- **IBAN bağı koşulsuz silinmez.** Mevcut geri sarma kuralı kullanılır (`PaymentMatchReconciler.RewindIbanConfirmationAsync`): bağın son doğrulaması bu kararsa bağın süresi kalan doğrulamalara geri çekilir, kalan yoksa bağ imha kaydıyla silinir; bağı bu havale öğrettiyse bağ silinir. Başka havalenin öğrettiği ve hâlâ doğrulanan bağ kalır. IBAN `Disputed` olur.
- Tahsisler (S3b'den itibaren, pilotta da) `ReversedAt` alır; beklentiler türetme gereği yeniden açılır. Aynı kapsamı kapatan öbür ödemenin artanı kendiliğinden yeniden dağıtılmaz; gerekirse o onay da geri alınıp yeniden verilir.
- **Kargo:** bu ödemenin adımları `Reverting` olur; bağı commit'ten sonra koşucu çözer (§4.6.5). Kargoya verilmiş dosyada bağ kalır ve uyarı yazılır. Geri alınmış ödeme bağlı kalamaz (adım jetonu). Adımda verilmiş kargo kararı duruyorsa yanıt bunu söyler (§4.6.7).
- **Fazla:** satır zaten kapatılmışsa (ör. "Fazlayı iade ettim") yapılacak bir şey yoktur. Değilse geri alınır; fazla harcanmış ve bakiye yetmiyorsa geri alma **engellenmez**:
  - Havuz kipinde geri alınacak tutar kişi havuzuyla sınırlıdır (`max(0, Σ üye bakiyesi)`, ortak bakiye §4.0). Yazım çekim türü iptaldir: çapa fazla satırının kendi satırında, `Kind = reversal`, `ReversesTransactionId` = fazla satırı; kalan kişinin öbür pozitif üyelerinden bacak olarak. Satır kipinde "bakiye" fazla satırının kendi satırıdır. Hiçbir satır eksiye düşmez; çekilemeyen kalan olay satırına ve uyarıya ("X ₺ fazla bakiyeden harcanmış") yazılır.
  - `PersonBalance.DrawAsync` bugünkü imzasıyla bu SaveChanges'e katılamaz: kendi `SaveChanges`'ini yapar, açık işlem kabul etmez, denemeler arasında izleyiciyi temizler ve tutarı plan kurulunca belli olur (364-513). S5'te "plan belli, kayıttan önce" geri çağrısı eklenir; ödeme, eşleşme, adım ve olay yazımı o çağrıda izleyiciye girer. Satır kipinin kısmi geri alma yazıcısı servise çıkar (`ApplyTransactionAsync` hep-ya-hiç ve private).
  - Σ ≤ 0 ise `DrawAsync` satır yazmaz; fazla satırı N01'de geri alınabilir kalmasın diye ayrı bir kapanış işareti yazılır (S5). Satır başına tek geri alma hakemi (N01) yalnız çapadadır.
  - Bakiye geri almasının eksiyi reddetmesi yüzünden düşen bir geri alma, yanlış onayı düzeltilemez hâle getirirdi (karar: §16 "İnceleme sonrası" madde 2).
- Havale sonra başka bir müşteriye onaylanırsa yeni Payment `n+1` anahtarıyla, kargo adımları yeni satırlarla yazılır (§2.3, §4.6.3).
- Admin sayfasındaki `UnmatchAsync` `Decision ≠ None` satırda reddedilir (§4.4); bugün onayı yetim bırakırdı.

### 11.3 Bağı kaldır (`ClosedByReceipt`)
Havale `Unmatched`'e, dekont açık gap'e döner (bugünkü kaldırma kuralı). Dekontun onayına dokunulmaz.

### 11.4 Ölçüm (§16-6'nın uygulanışı)
- Sayaçlar `PaymentMatch`'in güncel durumundan değil `BankDecisionEvent` satırlarından hesaplanır. Geri al → yeniden onay zinciri güncel satırda iz bırakmaz; güncel durumdan sayılsaydı geri alma görünmezdi.
- İlk değerlendirme olayı `WouldAutoApprove`'u, kuralın seçeceği müşteri ve beklentileri, `RuleVersion`'ı ve `OrderSetReliable`'ı kaydeder. `OrderSetReliable` pilotta kaç "Evet"in kargoya sunucuda bağlanabileceğini gösterir (§4.6).
- **Uyum:** son insan kararı aynı müşteriyi ve aynı beklentileri seçti. **Çelişki:** başka müşteri, "Hayır", "Müşteri ödemesi değil", farklı beklenti seçimi ya da "otomatik olurdu" kararının sonradan geri alınması. `ClosedByReceipt`, dekontun müşterisi kuralınkiyle aynıysa uyum, değilse çelişki sayılır. Yanıtsız sorular sayılmaz, ayrıca gösterilir. Sonradan (IBAN hafızası değişince) üretilmiş öneriye verilen kararlar ayrı sayılır (#551'in `;reeval` ayrımı).
- Sayılanlar: sorulan, Evet, Başka müşteri, Hayır, Müşteri ödemesi değil, Geri al, otomatik kuralın tutacağı ve tutmayacağı, uyum, çelişki.
- **Pencere** ancak şu üçü sağlanınca başlar: S5 ve arayüzü canlı (P3 web'de, EMAR'ın PC'leri B-W2a'da; fazla ve eksik hesaplanıyor, "Kalanı sil" ve "İptal" kullanılabiliyor); EMAR'ın satış senkronu On ve `SalesModeGate` maddeleri tutuyor (§5-5); "Onaylananlar" listesi panelde ve WPF'te canlı. S5'ten önce elden ödenmiş ya da vazgeçilmiş istek kapatılamaz ve açık görünür: kural 5'in girdisi eksik kalır, "çelişki yok" kanıtsız geçer. Pilotta yazılan `WouldAutoApprove` bu yüzden pencereye girmez (S5 `RuleVersion`'ı artırır). Geri alma yapılamıyorsa da "hiç geri alma yok" kendiliğinden doğru çıkar. `RuleVersion` değişince pencere yeniden başlar.
- Admin ölçüm sayfası bu sayaçları gösterir.

## 12. KVKK

- IBAN yalnız hash ve maske olarak saklanır. Olay satırları ve loglar yalnız kimlik içerir.
- **Saklama koddaki tabloyla aynıdır** (`BankOptions`): ham kayıt 30 gün, eşleşmeyenin açıklaması 30 gün, hareket ve eşleşme satırı 180 gün, IBAN bağı 12 ay. Süreler işlem tarihinden işler, çekimden sonra en az 7 gün. `RetentionExpired` arşivi ve geri açması **yok**: 180. günde satır silindiği için arşivlenecek bir şey kalmaz. Açıklaması silinmiş ama duran eşleşmeyen satırda "açıklama saklama süresi doldu" görünür.
- `Approved`, `AutoApproved` ve `ClosedByReceipt` "eşleşen" sayılır (`BankDataRetentionJob` 3. adım; bugünkü ölçüt gerçek müşteri ya da dekont bağı, `Decision` ölçütü de yazılır, S4): onaylı havalenin açıklaması 30. günde gitmez.
- **180. gün, hesap reddi, "Kaldır ve sil" ve hizmet sonu silmeleri reddedilmez.** Payment, tahsis, bakiye satırı, olay ve kargo adımı hareketsiz yaşar; bekleyen soru düşer. `BankDecisionEvent` 24 ay (öneri, avukat), `BankIbanState` `LastTransferAt`'ten 12 ay (§2.5); ikisi IBAN talep aracına bağlanır, her silme `DataDestruction` kaydı yazar. Olayın hizmet sonu ve "Kaldır ve sil"de de silinip silinmeyeceği S4 planında kesinleşir; varsayılan yaşar (yalnız kimlik taşır).
- Kargo adımı yalnız kimlik ve tutar taşır (`Orders.CustomerId` ile aynı düzey); Payment'la yaşar ve onunla silinir.
- RawJson maddesi kapandı: ham kayıt beyaz listeyle yazılıyor (`BankRawJsonRedactor`).
- **Silme işareti maddesi ve pilot kapısı düştü.** WPF'te müşteri silme eylemi yok (`CustomerRepository.ScrubPersonalData`'nın üretimde çağıranı yok); silme yalnız sunucudan iner (`MarkPurged`'ın tek çağıranı `ShopperPurgeService`). Gerçek boşluk ayrı: shopper hesabı olmayan müşteriyi silen hiçbir yol yok, ayrı KVKK işi (kapsam dışı).
- Purge **silmez, boşaltır** (`ShopperPurgeService` ile tutarlı; S4):
  - IBAN çiftleri silinir (kişi bağıdır); `BankIbanState` kalır (kişi bağı yok, süresi var).
  - Payment'ı `ShopperId` yanında `WpfCustomerId` ile de bulur; banka kaynaklı satırda zaten ad yok, tutar, tarih ve ReferansNo kalır.
  - Bağlı hareketlerin açıklaması boşaltılır; `Awaiting` satırın önerisi silinen müşteriyse satır `Unmatched` olur ve revizyon artar.
  - Açık beklenti türetildiği için yazım gerekmez; kayıtlar yalnız kimlik taşır.
- Serbest metin girişi yok: dismiss ve geri alma gerekçeleri kapalı kümedir; tek serbest not (`Other`) hareketle birlikte 180. günde silinir.
- Bakiye satırının `Reason`'ı sabit metindir; defter shopper'a açık (§2.8).
- Yayıncı push'u yalnız sayı taşır; kilit ekranına üçüncü kişinin adı düşmez (§6).
- Okuma denetimi: panelin ve WPF'in havale listesini açması olay satırıyla kaydedilir (KVKK Ö-4).
- **Metin (T1):** yayındaki politika "hiçbir ödeme otomatik onaylanmaz", "tahminler panelde, masaüstünde, müşteri uygulamasında gösterilmez" diyor (`_GizlilikPolitikasi.cshtml` 145-150) ve saklama satırları 90/180 gün ile eskimiş (265-275); v2 taslağı (#549) aynı cümleleri taşıyor ve değişiklikten önce bildirimi taahhüt ediyor. Yalnız sorulu pilot bile metne aykırı; metin değişmeden bayrak açılamaz (§14). Metindeki süreler 30 / 30 / 180 gün ve 12 ay olur.

## 13. Obifin kiracılık bağımlılığı

`ObifinConnection` bugün lisans başına (tek satır, `LicenseId` tekil). Karar katmanı yalnız `BankTransaction.LicenseId`'ye bakar; bağlantı **modelinden** bağımsızdır, iki seçenekle de çalışır:

- **(B) Lisans başına kimlik:** değişiklik gerekmez.
- **(A) Tek paylaşılan kimlik:**
  - Hareket içeri alınırken `BankAccount → LicenseId` eşlemesi zorunlu ve tekil olur.
  - Eşlenmemiş hesabın hareketi hiçbir lisansa yazılmaz, admin ekranına düşer.
  - Hash anahtarı global olduğu için tüm hafıza sorguları `LicenseId` ile süzülür; lisanslar arasında IBAN öğrenimi olmaz.

İki seçenekte de bayraklar lisans başına.

**Kimlik değişikliğinden ise bağımsız değildir.** `UserCode` ya da `BaseUrl` değişince `UpsertWithResultAsync` (`allowShadowReset`, `ObifinConnectionService.ResetShadowData`) lisansın bütün hareketlerini, eşleşmelerini, IBAN hafızasını ve gap'lerini siler ve imleci sıfırlar; sonraki çekim yeniden doldurur (bugün bağlantı gününden 2 gün öncesinden, `ObifinPollJob.InitialGraceDays`). Faz 2'de o satırlar finansal kararlardır ve EMAR'ı (A)'ya taşımak tam olarak bu işlemdir. Yeniden doldurulan hareketler yeni `ObifinId`'lerle gelir, yeniden sorulur, bir kısmı yeniden otomatik onaylanırdı: ikinci Payment, ikinci push, ikinci fazla.
- Lisansta `Decision ≠ None` bir satır ya da `Source = Bank` bir ödeme varsa gölge sıfırlama **reddedilir** (S2b). Başka hiçbir silme reddedilmez (§12): 180 gün, hesap reddi, "Kaldır ve sil" ve hizmet sonu KVKK yükümlülüğüdür.
- Bundan sonra kimlik değişimi ayrı bir yeniden anahtarlama işlemidir ve ortak kimlik spec'inin parçasıdır. O işlem satırları korur; eskiden yeniye eşlemeyi bankaya özgü doğal anahtarla yapar (hesap IBAN hash'i, `BankReference`, tutar, `OccurredAt`) ve bilinen doğal anahtar için yeni hareket yaratmaz. Önce EMAR verisinde `BankReference` doluluk oranı ölçülür. Herhangi bir yeniden doldurmadan sonra `BankQuestionsSince` o ana çekilir.
- `Payment.BankTransactionId` ve `BankDecisionEvent.TxId` FK'sız Guid'dir; hareket silinse de satır yaşar (§2.3, §2.6).

## 14. Yayına alma

- Bayraklar varsayılan olarak kapalı; pilot EMAR.
- Önce yalnız sorular açılır (`BankQuestionsSince` = açılış anı); eski hareketler gölgede kalır. Otomatik onay daha sonra, ayrı bayrakla ve §16-6 penceresinden sonra açılır.
- Sıra: sunucu, sonra web paneli, sonra WPF (Velopack). Android paneli (P1 + P2 AAB) ayrı ve elle yüklenir; pilot onu beklemez (§7). Alt istemci sürümü (`MinClientVersion`) artmaz.
- **Pilottan önce kapılar:**
  - **Satış senkronu On** (`SalesSyncMode`): sunucu kargo bağı ve güvenilir beklenti buna dayanır; `bank-questions enable` On değilse reddeder (§2.7, §4.6).
  - **Kargo hattı canlı:** S3d sunucuda, B-W1'in "Kargo kararı bekleyenler" kartı WPF'te (§4.6).
  - **Kargo kümesi ölçümü:** istek anı (§2.1) son 30 güne ve mod On'dan sonraya düşen isteklerin kiralı (`Lease`) ve `session:` kapsamlı payı (salt okunur sorgu, Burak çalıştırır); `FinishedAt`'e göre sayılsaydı geri doldurulan geçmiş payı düşürürdü. Pay düşükse (öneri: %80'in altı) pilot açılmadan rakam sahibe götürülür (soru 6, §4.6.7).
  - **Saklama ve silme:** S4 bayraktan önce prod'da (§12).
  - **Faz 2 metni yayında (T1):** metin şu an Faz 2'nin tersini söylüyor; v2 taslağı (#549) da. Kategori, amaç, 30/30/180 gün ve 12 ay süreleri, alt işleyen, otomatik onay ve yayıncı üzerinden itiraz yolu; avukat ve sahip onayı. Takvimin en uzun kalemi.
  - **`--reset-iban-memory`:** kuru koşu ve `--apply` (Burak, gündüz; KVKK planı karar 5). Uygulandığına dair kayıt yok.
  - **Yönetici 2FA'sı:** KVKK planı (madde 15) "Faz 2'den ve ikinci yayıncıdan önce" diyor, kodda yok; pilotu mu yalnız otomatik onayı mı bekleteceği sahip kararı (§16, soru 3).
  - **EMAR veri sorumlusu profili:** banka modülü profilsiz açılmıyor (`ObifinConnectionService.EnsureDataControllerAsync`); EMAR'ınki dolu (depodan doğrulanamaz).
- **Genişlet/daralt:** yeni sütunlar nullable, önce okuyucu yayınlanır. `Source = Receipt` geri doldurması S2a'da yapılır. Bütün yeni enum değerleri (`Decision`, `AskReasons`, `ApprovalSource`, `IbanMemorySource.BankConfirm`, gerekçeler, kargo adımı `State`/`Reason`) S2a'da tanımlanır, yalnız sonraki PR'lar onları yazar. Banka enum'ları string saklanıyor; otomatik geri alma imajı geri alır ama veriyi almaz, eski kod bilmediği değerde düşer.
- **Gözlem:** bugün çekim işi `AutomaticRetry=0` ve hata yalnız Hangfire'da görünüyor; Obifin başarılı ama boş sayfa dönerken banka tarafındaki senkron durursa (`BankAccount.LastBankSyncAt`) yayıncı "havale yok" ile "besleme öldü"yü ayırt edemiyor. Yeni `bank-feed-watchdog` işi saatte bir koşar; besleme bayatsa (`lastBankDataAt` eşikten eski), `ObifinConnection.LastError` doluysa ya da `stuckCount > 0` ise `AdminJobAlerts` ile yazar (aynı durum için günde bir kez). Bir günü geçen bekleyen kargo adımı (§4.6.3) ve bir günü geçen `SessionLive` adımı, sorular kapalıyken de, aynı yoldan uyarır; yazan 5 dakikalık kargo işidir, bekçi değil (S3d). İkincisi takılı devirdir: çözümü yayını açan PC'nin açılması ya da `sales-sync force-end` CLI'ıdır.
- **S6 öncesi ölçüm:** otomatik onay IBAN hafızasına dayanır; EMAR'ın banka hesabının karşı IBAN doluluğu (Admin → Obifin, `IbanFullCount`/`IbanMaskedCount`/`IbanEmptyCount`) okunmadı (Faz 1 spec'indeki demo ölçümünde QNB'de 0/17). Boşsa S6 yapılmaz; yalnız sorulu düzen yeter.
- Merge ve deploy yayın penceresine denk gelmez (Paz/Pzt/Çar/Per 20:00–01:00).

## 15. PR sırası

Saatler adam-saattir ve tahmindir; inceleme payı içinde. Kargo sütunu 25.11 kararının ("baştan sunucu") değişimidir.

| PR | İçerik | Saat | Kargo | Bağımlılık |
|---|---|---|---|---|
| **D0** `docs(bank)` | Bu uyarlama ve iki denetimin düzeltmeleri (ortak bakiye §9 ve satış senkronu §13-3 dahil) | 10 | | yok |
| **S1** `refactor(payments)` | `PaymentApprovalService` çıkarımı, `PaymentPushTexts`; davranış değişmez | 6 | | yok (D0'ı beklemez) |
| **S1b** `refactor(sales-sync)` | `ShipmentBindCore`, `ShipmentDecisionCore`, `ShippingRules` (dört çağıranıyla, §4.6.4); davranış değişmez | 7 | yeni | yok (şimdi başlar) |
| **B-W0** `fix(wpf)` | Bekleyen ödeme kaydı sayaçta ve kapanışta, "Havale fazlası" etiketi | 6 | | yok |
| **S2a** `feat(bank)` | Şema (genişletme göçü, kullanılmıyor): Payment ve PaymentMatch sütunları (`Payment.WpfCustomerId` geri doldurması ve `MatcherVersion` dahil), `BankDecisionEvent`, `BankIbanState` (`LastTransferAt` dahil), tahsis, **iki kargo adımı tablosu**, IBAN indeksi ve aynı PR'da birleştiricinin IBAN çiftini tekilleştirmesi ile `Payment.WpfCustomerId`'yi çevirmesi (§9), bayraklar ve CLI, bütün enum'lar, FK'sız Guid'ler, `Bank` ayarları compose'a | 20 | +3 | D0 |
| **S2b** `fix(bank)` | Kişi düzeyi eşleştirme (`MatcherVersion` yazımı), IBAN'ın birden çok kişiye düşmesi ve adaylar (§2.4), beş kilit, ölçüm süzgeci, dekont gönderiminde `Payment.WpfCustomerId`, `payments/sync` ve gap korumaları, panel ödeme listesi ve arama süzgeci (§2.3), sıfırlama reddi, para birimi | 19 | | S2a |
| **S3a** `feat(bank)` | Değerlendirme (tek SaveChanges; §5 koşullarının hepsi, yani `AskReasons` ve `WouldAutoApprove`; `LastTransferAt`'i ilerletir; önceden bağlı ve açıklaması silinmiş `None` satır), beklenti türetme ve istek listesi (istek anı, etkin hâl, güvenilmezlik, kalan, işaretler, varsayılan seçim; §2.1, §4.2), liste, sayaç, seçici, `requests`, decline/dismiss/restore, `sales/state` alanı, `OrderSetReliable` (kira, kapsam ve güncel toplam denetimi; S3d aynısını kullanır), okuma denetimi; onay yok | 32 | | S2b |
| **S3b** `feat(bank)` | confirm, tahsis yazımı ve geri alması ("Evet", dekont onayı, tek tahsisin geri alınması), dekont etkileşimi (kişi düzeyi), revert (hareketi silinmiş onay dahil), bağ kaldırma, admin reddi; kargo adımını aynı SaveChanges'te `Waiting` yazar (karar isteyen doğum hâli S3d'de) | 40 | −4 | S1, S3a |
| **S3c** `feat(bank)` | Yayıncı push'u (ve bugünkü dekont push'larının arındırılması), shopper push'u ve DTO'su, bekçi işi, ölçüm sayfası | 10 | | S3b |
| **S3d** `feat(bank)` | Kargo: adımın doğum kuralı (§4.6.1-3, "Evet"in SaveChanges'ine girer), koşucu, 5 dk'lık iş ve bekleyen adım uyarısı, `shipping-steps` listesi ve `decide`, çözme, `Shortage`'ın kalanı izlemesi, `NothingToBind`, yarış testleri | 34 | yeni | S3b, S1b |
| **S4** `feat(kvkk)` | Saklama, silme, imha kayıtları, purge genişlemesi | 16 | | S2a, S3b; bayraktan önce |
| **P1** (OrderDeck-Mobile) | Havaleler segmenti (web; adaylar, tahsisi geri alma, `client-update-required` metni), dekontta Aynı/Ayrı ödeme, salt okunur kargo satırı, "Havale fazlası" etiketi ve gizli iptal | 43 | 0 | S3b, S3c, S3d |
| **B-W1** `feat(wpf)` | Çekmecede Havaleler (adaylar, `client-update-required` metni), menü sayısı, "Kargo kararı bekleyenler" kartı (iki alt PR: 23 + 54) | 77 | −10 | S3b, S3d |
| **T1** `docs(kvkk)` | Faz 2 cümleleri (politikanın iki kopyası, sürüm, bilgi notları); avukat | 8 | | soru 2, #549'un 22 yer tutucusu |
| **PİLOT** | Velopack sürümü, üç PC'nin geçişi, `--reset-iban-memory`, `bank-questions enable`, saha kabulü | 8 | | S3d, S4, P1, B-W1, T1, §14 kapıları |
| **S5** `feat(bank)` | Fazla/eksik, "Kalanı sil", "İptal" ve `off-bank` uçları (ödemesiz tahsis türleri ve geri alması), bakiye düşüm tavanı (§4.3), `shownOverpayment` zorunluluğu, iki kip yazıcısı, use-bank-revert, `bank-already-approved` | 33 | | pilot verisi; ortak bakiye S2 kodu (master'da); önerilen: önce havuz açılışı, aynı gün ikisi değil |
| **P3** (OrderDeck-Mobile) | S5'in panel yüzü (§7): fazla/eksik satırı ve `shown*`, "Kalanı sil", "İptal", "Fazlayı iade ettim" | 16 | | S5 |
| **B-W2a** `feat(wpf)` | Havaleler'de fazla/eksik satırı ve `shown*`, kartta "Eksik X ₺" | 12 | | S5 |
| **B-W2b** `feat(wpf)` | Elden/Papara kapanışı (`off-bank`) | 6 | | S5, soru 4 |
| **S6** `feat(bank)` | Otomatik onay (bayrak kapalı): S3a'nın değerlendirdiği kuralı eyleme bağlar, ortak IBAN koruması (soru 5); aynı kargo adımını yazar | 18 | −16 | S5, P3, B-W2a, §16-6 penceresi, 2FA kararı, soru 5 |
| **SH1 + P2** | Shopper metinleri, panel push deep link'i (AAB'ler) | 10 | | S3c; Shopper S5'ten önce Play'de |

**Etki.** Pilota kadar ≈ 336 saat (276'dan +60). Kargo +24: S1b 6, S2a +3, S3b −4, S3d 29, B-W1 −10. Denetim düzeltmeleri +13: S1b +1, S2a +1, S2b +1, S3d +1; tahsis ve türetmenin pilottan önceye alınması S3a +3, S3b +6. Son denetim (§18.6) +23: D0 +2, S2b +2, S3a +5, S3b +6, S3d +4, P1 +3, B-W1 +1. Pilot sonrası ≈ 95 saat (92'den +3: sunucu bağlama S6'dan çıktı −16, tahsis ve türetme S5'ten çıktı −8, son denetim +27: S5 +5, P3 16, B-W2 +6). Toplam ≈ 431 (368'den +63). Soru 8'in cevabı 2 olursa ayrıca ≈ 12 saat. Yönetici 2FA'sı ayrıca ≈ 24 saat. Sahip sorusundaki "pilot ≈ 16 saat daha erken" farkı hakem tasarımında 24 saate çıktı: "baştan sunucu" kalıcı adımı, koşucuyu ve yarış testlerini pilotun önüne alır.

- **Sürüm kapısı:** ayrı "W2 ⊇ kişi anahtarı" kapısı yok; satış senkronunun mod kapısı bunu içerir (§5-5).
- **OrderDeck-Mobile:** P1 ve P2 tek AAB'de, P3 ayrı. **OrderDeck-Shopper:** SH1 `source` metinleri ve "Havale fazlası" etiketi; push metni sunucuda (`PaymentPushTexts`).
- **Akış:** S1, S1b ve B-W0 hemen (D0'ı beklemez) → D0 → S2a → S2b → S3a → S3b → (S3c, S3d, S4 paralel) → (P1, B-W1 paralel) → T1 metni → **PİLOT: EMAR'da yalnız sorular** → S5 → (P3, B-W2a paralel; B-W2b soru 4'ten sonra) → **§16-6 penceresi** → S6 → rakamlar sahibe → EMAR'da otomatik onay. SH1 + P2 S3c'den sonra paralel; gizlilik sunucuda sağlandığı için pilot SH1'i beklemez.

### Testler
Çift onay güvenliği tekil indekslere ve jetonlara, kargo güvenliği adım jetonuna ve `ship` kilidine dayanıyor; InMemory hiçbirini uygulamıyor (`PanelPaymentDecisionConcurrencyTests` notu). Bu yüzden:
- **`SqlServerContainerFixture` (gerçek SQL Server):** sink otomatik onayı ∥ panel "Evet"; "Evet" ∥ dekont onayı; geri al ∥ onay; iki "Evet" ∥ tek istek ve ∥ aynı kişi×kapsamın iki kaydı (kayıt jetonu); dekont onayı ∥ "Evet" aynı istek; tahsis geri alması ∥ "Evet"; tarama ∥ sink; iki PC'den aynı anda "Evet"; geri al → başka müşteriye onay (`n+1` anahtarı); gölge sıfırlamanın reddi. S5: `apply` ∥ onay (düşüm tavanı), `off-bank` ∥ "Evet".
- **Dekont ve havale (S3a, S3b, SQL Server):** bayrak kapalıyken dekonta bağlanan satır bayrak açılınca sorulmaz, ikinci ödeme yazılmaz; yalnız tutar ve zamanla bağlanmış başka kişinin satırı sorulur; sink düşer → 20 dakikalık iş bağlar → tarama; IG'de bekleyen dekont + TT havalesi (aday görünür, "Evet"ten sonra dekont onayı Aynı/Ayrı sorar, "Tümünü onayla" atlar); "Hayır" denmiş ya da geri alınmış havale dekont onayıyla kapanmaz; geri alınca "Aynı ödeme" dekontu `Pending` olur, insanın ayrıca reddettiği olmaz.
- **Geri alma (S3b):** 180. gün, hesap reddi ve "Kaldır ve sil"den sonra onay listede görünür ve geri alınır (tahsis `ReversedAt`, adım `Reverting`, fazla S5'te); geri alınan satır yeniden değerlendirmeye girmez.
- **Kargo (S3d, SQL Server):** "Evet" ∥ "Evet"; koşucu ∥ WPF `attach-shipment` (iki sıra); koşucu ∥ geri al, `cancel`, `ship-now`, birleştirme, `disable`; çökme → 5 dk'lık iş; canlı yayın → devir; istekten sonra basılan etiket bağlanmaz; IG + TT iki adım; ikinci adım ve WPF'in kurduğu bağ korunur; eski WPF akıştan yakınsar; fiyatı düzeltilmiş sipariş (`PaidPrice`) bağlanır ve çözülür; hepsi düşen küme `NothingToBind` olur, kalan ödemesinde sorulmaz; bayrağı yalnız IG satırında olan kişide TT adımı da sorulur, kargosu istenip ödenmişte sorulmaz; deneme sınırını aşan adım listede kalır ve yeniden denenir; aynı havuzda iki eşik sorusu ve "Elle gir"le kargolanmış dosya; canlı yayında verilen karar devirde uygulanır, kısmen devredilmiş kümede eşik sonra sorulur; onaydan sonra iptal edilen sipariş sorulur; 500 + 80: ikinci "Evet" ilk adımın `Shortage` sorusunu kapatır, ∥ `decide(recipient-pays)` (iki sıra), ikincinin geri alınması yeniden sorar; bağ ikinci adıma geçip kalan açılınca sorulur; `Reverting` sürerken aynı dekonta "Evet"; `Reverting` adımda `decide` 409; koşucu ∥ birleştirme commit öncesi yeniden çözümle yakalanır.
- **Göç testleri** (`CustomerProjectionUniqueIdentityMigrationTests` deseni): IBAN indeksinin tekilleştirilmesi, `Source` ve `Payment.WpfCustomerId` geri doldurması, birleştirmede çift IBAN bağı, kargo adımının filtreli tekil indeksi (kapanan ve çözülmekte olan adım yeni adımı engellemez).
- **WPF:** sekmeler, kart ve `decide` akışı STA'da; yarım ekran (960 px); iki PC yakınsaması (`TwoPcSalesConvergence` deseni). SQLite göç testi yok (göç yok).
- **İdempotens:** aynı hareketin sink'e yeniden gelmesi; aynı `requestId`'li confirm, revert ve `decide`.
- **PR'a göre:** S2b beş kilidin her biri, kişi düzeyi eşleştirme ve `MatcherVersion`, ortak IBAN'da öneri ve aday (kullanıcı adlı, adsız, küme dışı), para birimi, `payments/sync`, gap, panel liste ve arama süzgeci. S3a `Decision` geçişleri, `Unmatched` → `Awaiting` ("Hayır" denmişe ve geri alınmışa dokunulmaz), bayrak kapalıyken bugünkü davranış. S4 180. gün, hesap reddi, "Kaldır ve sil", hizmet sonu, purge, `BankIbanState` 12 ay, olay 24 ay, imha kaydı.
- InMemory yalnız saf mantık için: §5 kural tablosu (her koşulun kodu), beklenti türetme, varsayılan seçim (tam eşit istek öne geçer; ödenmiş, siparişleri bağlı, süresi geçmiş ya da yayınından bu yana kişide tahsissiz onaylı ödemesi olan istek seçili gelmez), tahsis, eksik ödenip bağlanmış isteğin kalanının açık kalması, üç hâlin uç tablosu (§2.7), adım durum makinesi ve bekleyen adım sayımı. Beklentide ayrıca: geç `finish` sonraki isteği "en yeni" olmaktan çıkarmaz; `LeaseUntil`'i geçmiş `Sending` eski `Delivered`'ı güvenilmez yapar ve listelenir; geri doldurulan eski istek yeni kiranın önüne geçmez, süresi geçmiş güvenilmez kayıt kişide otomatik onayı engellemez; Birleştir'de iki istek ayrı listelenir, biri ödenince öbürü kapanmaz, Ayır'da ortak istek güvenilmezdir; kümesi değişmiş, tahsissiz bağlı siparişli ve ödemeden sonra iptalli kayıt güvenilmezdir, tahsisten sonra "Elle gir" "Eksik"i silmez; ödenmiş yayında yeni istek (kalan, tavan, kalanı 0 olup bağsız siparişi olan istek); istekten önce onaylanan tahsissiz ödeme ve ondan sonraki hatırlatma; kargolanmış dosyada geri alınan isteğin kalanı açık kalır.

## 16. Sahip kararları (2026-10-10; kargo 2026-11-25)

| # | Konu | Karar |
|---|---|---|
| 0 | İlk görüşte | Hiçbir havale ilk görüşte otomatik onaylanmaz; ilk eşleşmede panelde ve WPF'te sorulur, "Evet" = ödeme onayı (dekont gerekmez) + IBAN öğrenimi. |
| 0b | Sonraki havaleler | Aynı IBAN'dan sonrakiler §5 kuralıyla otomatik; IBAN tek müşteriye bağlı değilse ya da tutar tutmazsa sorulur. |
| 1 | Personel | Dekontu onaylayabilen personel havaleyi de onaylar; stok personeli görmez. *Uygulama varsayılanı (25.11, §7): personel soruları yanıtlar, yalnız son 48 saatin onaylarını görür ve geri alır; 30 günlük liste ve "Fazlayı iade ettim" yalnız sahip.* |
| 2 | Birden çok açık istek | Havale tek bir isteğe ya da **bütün** açık isteklerin toplamına eşitse otomatik onay; alt küme eşitliği sorulur (§5-5). |
| 3 | Gölge dönemde öğrenilen IBAN | "İlk onay" sayılmaz; bu akışta bir kez daha sorulur (§5-3: yalnız `BankConfirm` kaynağı). |
| 4 | Fazla ödeme | Fazla tutar her zaman bakiyeye yazılır (§4). |
| 5 | Eksik ödeme | Onaylanır (dekontla aynı), "Eksik X ₺" uyarısı görünür, kalan açık kalır (§4). |
| 6 | Otomatik onayın açılışı | Veriye bağlı: yalnız-soru pilotunda ≥ 2 hafta ve ≥ 30 "Evet"; "otomatik açık olsaydı" kararlarından hiçbiri insan kararıyla çelişmemiş ve hiç geri alma yok → rakamlar sahibe sunulur, sahip "aç/bekle" der. Admin ölçüm sayfası bu sayaçları gösterir: sorulan, Evet, Başka müşteri, Hayır, Geri al, otomatik kuralın tutacağı ve tutmayacağı. Bayrak her an kapatılabilir. |
| 4 (uygulama) | Fazla ödeme | "Fazla" yalnız güvenilir açık beklentiye karşı hesaplanır. Beklenti yoksa ya da beklenti akışı güvenilir değilse bakiyeye hiçbir şey yazılmaz ve ekran "İstenen tutar bilinmiyor" der (§4.3). |
| 6 (uygulama) | Otomatik onayın açılışı | Sayaçlar olay satırlarından hesaplanır. Pencere S5 çıkınca, satış senkronu açıkken ve "Onaylananlar" listesi canlıyken başlar; kural sürümü değişince yeniden başlar (§11.4). |
| 7 | Ortak bakiye (2026-10-11) | Fazla, onaylanan (son tahsis edilen) beklentinin sahip satırına yazılır; §11.2'deki "bakiyede ne varsa" = kişi havuzu (ortak bakiye K6). |
| **8** | **Kargo (25.11.2026)** | **"Evet" nerede verilirse verilsin siparişi kargo dosyasına sunucu bağlar, baştan (soru 1, seçenek 2).** Yalnız karar isteyenler (kargo ücreti eksik, ücretsiz kargo) masaüstünde sorulur (§4.6). Seçilmeyen: pilotta masaüstü bağlar, panel "bekliyor" yazar. *Uygulama notu (25.11 denetimi): "kargo ücreti eksik" 10.10 §4.3'ün sorusudur (kalan > 0 olan her onay); alıcı ödemeli kişi bugün de bu soruya düşer, çünkü `PaymentMatcherService.Match` bayrağı okumaz ve tutar ürün toplamına eşittir. Sunucunun hangi siparişin ödendiğini bilemediği onaylar karar metninde yok: soru 6.* |

**Bağımlılık — Obifin kiracılık:** sahip tek paylaşılan Obifin kullanıcısı + banka bağlantısı→lisans eşlemesini istiyor; Obifin'e "sakıncası var mı" sorusu hafta başı gidiyor (§13). Bu spec iki modelle de çalışır; seçilen model ayrı bir spec/PR dizisidir. Kimlik değişimine karşı koruma bu spec'te (§13).

### İnceleme sonrası sahip kararları (2026-10-10)
1. **§16-6 alt sınır:** otomatik onay ancak yalnız-soru pilotunda en az **20 "otomatik olurdu"** vakası birikmişse, bunların **hiçbiri** insan kararıyla çelişmemişse ve **hiç geri alma** yoksa önerilir (≥ 2 hafta ve ≥ 30 "Evet" koşulları da geçerli).
2. **Harcanmış fazlanın geri alınması:** geri alma **engellenmez**; bakiyede kalan kadarı `reversal` ile geri alınır, kalan eksik **uyarı** olarak gösterilir ve olay satırına yazılır; bakiye **eksiye düşmez**.

### Açık sahip soruları (25.11.2026)
Soru 1 (kargo) cevaplandı: madde 8. Kalanlar, önerisiyle:
- **Soru 2, metin:** Faz 2 cümleleri bekleyen gizlilik metnine (v2, #549) şimdi eklensin mi, yoksa v2 olduğu gibi çıkıp v3 mü yazılsın? Öneri: v2'ye ekle (avukata tek metin, kullanıcıya tek bildirim). Pilotu bekletir.
- **Soru 3, 2FA:** yönetici iki aşamalı doğrulaması pilotu mu, yalnız otomatik onayı ve ikinci yayıncıyı mı bekletsin? Öneri: ikincisi, avukat itiraz etmezse. Pilotu bekletir.
- **Soru 4, Elden/Papara:** "Elle gir" formundaki Elden/Papara ödemesi o kişinin açık isteğini kapatsın mı? Öneri: evet. B-W2b'yi bekletir.
- **Soru 5, ortak IBAN:** aynı IBAN'dan aynı kişiye en az iki ayrı "Evet" istensin ve e-para IBAN'ları hep sorulsun mu? Öneri: evet. S6'yı bekletir.
- **Soru 6, siparişi bilinmeyen onay (yeni, denetim):** "Evet" denen havalede sunucu hangi siparişin ödendiğini bilemiyorsa (istek seçilmemiş, istek yayın seçilmeden gönderilmiş ya da istekten sonra sipariş veya fiyat değişmiş) ne yapsın? 1) Bağlamasın, masaüstünde sorsun; operatör "Elle gir" ile bağlasın. 2) Bugünkü "Gelen Ödemeler" gibi müşterinin bütün bağsız etiketlerini bağlasın. Öneri: 1 (2'de ödenmemiş yayının etiketi de "ödendi" olur); pilot öncesi ölçümde payı yüksek çıkarsa rakamla yeniden sorulur (§14). Pilotu bekletir.
- **Soru 7, eksik kalan kargo ücreti (yeni, son denetim):** kargo ücreti eksik gelen ödemede masaüstünde "Alıcı öder" ya da "Beklet" seçilince o kargo ücreti müşterinin borcunda açık mı kalsın? 1) "Alıcı öder"de kapansın (müşteri kuryeye öder), "Beklet"te açık kalsın. 2) İkisinde de açık kalsın; gerekirse "Kalanı sil" ile elle kapatılır. Öneri: 1. Cevap gelene dek 2 geçerli (§16-5); S5'te uygulanır, pilotu bekletmez.
- **Soru 8, geri almada kargo kararı (yeni, son denetim):** yanlış müşteriye verilen "Evet" geri alınınca, o onayda verilmiş kargo kararı (Beklet, Alıcı öder) da geri alınsın mı? 1) Hayır: ekran "kargo kararı duruyor" diye uyarır; alıcı ödemeli işaretini kapatan düğme bugün de yok. 2) Evet: kargo dosyası eski durumuna döner, masaüstünde işareti kapatma düğmesi çıkar (≈ 12 saat, S3d ve B-W1). Öneri: 2, çünkü 1'de yanlış müşteri alıcı ödemeli kalır. Cevap gelene dek 1 geçerli (§4.6.7); pilotu bekletmez.

## 17. İnceleme (2026-10-10) — bulgu kararları

Mercekler: **kod** = kod tutarlılığı, **para** = para güvenliği, **kvkk** = kullanım ve KVKK, **eleştirmen** = bütünsel eleştiri. "Birleşik" aynı tasarım kararına bağlanan bulguları gösterir. Bölüm adları 2026-10-10 hâlidir; 25.11 uyarlamasında yeri değişen ya da düşen kararlar (ör. 10, 14, 20, 37, 38, 51, 52) §18'de.

| # | Mercek | Önem | Konu | Karar |
|---|---|---|---|---|
| 1 | kod | Kritik | Geri al sonrası ReferansNo yeniden onayı engelliyor | İşlendi §2.3, §11.2 (birleşik: 23) |
| 2 | kod | Önemli | Karar yazımı düşerse havale None'da kalıyor | İşlendi §3: ayrı değer yerine tek SaveChanges + tarama yüklemi (birleşik: 19) |
| 3 | kod | Önemli | PaymentId yazan yollar Decision'ı atlıyor | İşlendi §3, §4.4, §11.2 (birleşik: 18, 44) |
| 4 | kod | Önemli | Gölge IBAN çifti BankConfirm'e yükselmiyor | İşlendi §2.5 (upsert) |
| 5 | kod | Önemli | Ortak IBAN işareti silmeyle kayboluyor | İşlendi §2.5 `BankIbanState` (birleşik: 25) |
| 6 | kod | Önemli | WPF dekontunun müşterisi bilinmiyor | İşlendi §2.3, §5-6, §8 W3 (birleşik: 17). *2026-10-19: W3 maddesi geçersiz, WPF dekont yazmıyor* |
| 7 | kod | Önemli | Beklentisiz fazla, overpaymentToBalance çelişkisi | İşlendi §4.3, §6, §16 "4 (uygulama)" (birleşik: 16, 33) |
| 8 | kod | Önemli | Fazla geri alınamayınca geri alma düşüyor | İşlendi §2.8, §11.2 + sahibe soru 2 (birleşik: 23) |
| 9 | kod | Önemli | Seçici projeksiyon id'si döndürmüyor | İşlendi §6 seçici ucu, §7 (birleşik: 34). *25.11: gerekçe yanlıştı, karar geçerli (§18.5-10)* |
| 10 | kod | Önemli | W3 geri itme ve bayat imleç | İşlendi §8 W3 |
| 11 | kod | Önemli | Shopper DTO ad ve referans döndürüyor | İşlendi §10 (birleşik: 31) |
| 12 | kod | Küçük | AutoApproved ölçümü ve kilidi bozuyor | İşlendi §2.4: Status'a değer eklenmez; §5.1 bütün koşullar |
| 13 | kod | Küçük | Saklama işi sahte 409 üretiyor | İşlendi §3 `DecisionRevision` + `stale` |
| 14 | kod | Küçük | Beklenti kaynağı deliveryJob'a bağlı | İşlendi §2.1 (çapa düzeltildi) |
| 15 | kod | Küçük | Bayrak ucu, etiket, tek ExpectationId | İşlendi §2.4, §2.7, §6, §7, §8, §10 (birleşik: 42, 45) |
| 16 | para | Kritik | Fazla ödemenin tabanı tanımsız | İşlendi §4.3, §6; elle fazla tutarı girişi reddedildi (istenmeyen kapsam, bakiye kartı zaten var) |
| 17 | para | Kritik | Dekont onayı banka onayını görmüyor | İşlendi §4.4, §5-6 |
| 18 | para | Kritik | Gecikmeli bağ başka müşterinin sorusunu kapatıyor | İşlendi §3, §4.4 |
| 19 | para | Önemli | None'da takılan havale kayboluyor | İşlendi §3 (birleşik: 2) |
| 20 | para | Önemli | Revizyon kaynağı, ödenmiş beklenti yeniden açılıyor | İşlendi §2.1 |
| 21 | para | Önemli | Dekont ve banka dışı ödeme beklentiyi kapatmıyor | İşlendi §2.1, §4.2, §5-6, §8 W2 |
| 22 | para | Önemli | Hangi ödeme hangi beklentiyi kapattı bilinmiyor | İşlendi §2.2, §4.2 |
| 23 | para | Önemli | Geri al: anahtar, dekont, harcanmış fazla | İşlendi §11.2 (birleşik: 1, 8) |
| 24 | para | Önemli | Mutasyon uçlarında sürüm yok | İşlendi §3, §6 |
| 25 | para | Önemli | Geri almada IBAN çifti kalıyor | İşlendi §2.5, §11.2 |
| 26 | para | Önemli | Birleştirme yeni indekse çarpıyor | İşlendi §9 |
| 27 | para | Önemli | Beklentisiz pilotta ölçüm kanıtsız geçiyor | İşlendi §11.4 + sahibe soru 1 (birleşik: 33, 49) |
| 28 | para | Küçük | Liste sırasız ve sayfalamasız | İşlendi §6, §8 |
| 29 | para | Küçük | §9 toplam kuralıyla çelişiyor | İşlendi §9 |
| 30 | para | Küçük | Yeniden hesap açık soruyu değiştiriyor | İşlendi §2.4, §3 |
| 31 | kvkk | Kritik | Eski shopper'a ad sızıntısı, gerekçesiz geri alma | İşlendi §6, §10, §11.2, §12 |
| 32 | kvkk | Kritik | Geri al için arayüz girişi yok | İşlendi §6, §7, §8, §11.4 |
| 33 | kvkk | Kritik | Pilotta beklenti yok: fazla ve ölçüm boş | İşlendi §4.3, §11.4 (birleşik: 7, 16, 27) |
| 34 | kvkk | Önemli | Seçici gerçek adı aramıyor | İşlendi §6 (birleşik: 9) |
| 35 | kvkk | Önemli | Rozet ve push kapsamı, kilit ekranında ad | İşlendi §6, §7, §12 |
| 36 | kvkk | Önemli | Kararı veren personel kaydedilmiyor | İşlendi §2.3, §2.6, §11.1 |
| 37 | kvkk | Önemli | Silinen müşteri pilotta öneriliyor | İşlendi §12, §14, §15: silme işareti pilottan önce |
| 38 | kvkk | Önemli | Ödeyen adı WPF'te kalıcı | İşlendi §6, §8 W3 |
| 39 | kvkk | Önemli | Aydınlatma metni banka verisini anmıyor | İşlendi §14 (pilot kapısı) |
| 40 | kvkk | Önemli | WPF sayfası canlı akışı örtüyor | İşlendi §8 (sağ çekmece) |
| 41 | kvkk | Küçük | Sorma nedenleri kapalı küme değil | İşlendi §5.2 |
| 42 | kvkk | Küçük | Panel bayrağı göremiyor, stok yoklaması | İşlendi §6, §7 (birleşik: 15) |
| 43 | kvkk | Küçük | Dismiss gerekçesi süresiz serbest metin | İşlendi §6, §12 |
| 44 | kvkk | Küçük | "Eşleşen soru" tanımsız | İşlendi §4.4 (birleşik: 3) |
| 45 | kvkk | Küçük | bank-overpayment etiketi istemcilerde yok | İşlendi §7, §8, §10 (birleşik: 15) |
| 46 | kvkk | Küçük | Her PC adlı listeyi yokluyor | İşlendi §8 |
| 47 | eleştirmen | Kritik | Kimlik değişikliği karar verisini siliyor | İşlendi §2.3, §13, §15 PR2 |
| 48 | eleştirmen | Önemli | Ölü banka beslemesi görünmüyor | İşlendi §6, §7, §8, §14 |
| 49 | eleştirmen | Önemli | Ölçüm sayaçlarının kaynağı tanımsız | İşlendi §4.5, §11.4 (birleşik: 27) |
| 50 | eleştirmen | Önemli | Beklenti okuma, vazgeçme ve TTL yok | İşlendi §2.1, §6 |
| 51 | eleştirmen | Önemli | Eksikte kargo yönergesi sorulmuyor | İşlendi §4.3, §6, §8 W3 |
| 52 | eleştirmen | Önemli | Karışık sürümde beklentiler eksik | İşlendi §4.3, §5-5, §9, §11.4 |
| 53 | eleştirmen | Önemli | Android paneli ayrı AAB istiyor | İşlendi §7, §14, §15 |
| 54 | eleştirmen | Önemli | Test stratejisi yok | İşlendi §15 Testler |
| 55 | eleştirmen | Küçük | Döviz havalesi TL sanılıyor | İşlendi §3 (`Excluded`) |
| 56 | eleştirmen | Küçük | Geri doldurma ve enum yayın sırası | İşlendi §4.5, §14 |
| 57 | eleştirmen | Küçük | İade edilen fazla için eylem yok | İşlendi §6, §11.2 |

Sonuç: 57 bulgunun 57'si işlendi; hiçbiri bütünüyle reddedilmedi. Bir alt öneri reddedildi (16: fazla için elle tutar girişi). Üç bulgu, önerilenden farklı ama eşdeğer bir çözümle işlendi: 2 (ayrı değer yerine tek SaveChanges), 12 (Status'a değer eklenmedi), 40 (sayfa yerine çekmece). İki iş kararı sahibe soruldu (§16).

### 2026-10-10 kişi anahtarı uyumu

WPF'te ödeme isteği kişi düzeyine taşındı: kişi×kapsam başına tek iş, iş doğduğu sahip satırda kalır ve her sunucu çağrısı o satırın Id'siyle yapılır. Spec buna uyduruldu (25.11'de §2.1 beklenti ve §8 W2 maddeleri düştü, kişi tanımı, IBAN sayımı ve çift ödeme izi geçerli; §18):
- §1: "`GroupId` birleştirmesi" maddesi daraltıldı; yalnız sunucu tarafı beklenti birleştirmesi kapsam dışı.
- §2 "Kişi" tanımı (sunucu `GroupId`'sindeki asıl kayıtlar). §2.1: `WpfCustomerId` = işin sahip satırı, işsizde aynı belirlenimli kural; indirilen iş `Cancelled (Superseded)` alır, `PaidAmount > 0` ise `Fulfilled` + `Amount = PaidAmount`, kalan `RevisedAfterPayment`.
- §2.5, §4.1, §5-3: IBAN sayımı ve `Shared` kişi başına; aynı kişinin iki satırına verilen "Evet" IBAN'ı paylaşılmış yapmaz.
- §4.2, §5-5: uygun beklentiler kişinin bütün kayıtlarından toplanır; aynı kişi ve `ScopeKey`'de farklı `WpfCustomerId`'li iki açık beklenti sorulur (`ExpectationUnreliable`).
- §5-6: çift ödeme izleri kişinin bütün kayıtlarında aranır.
- §8 W2, §15: `Superseded` revizyonu W2'de; sürüm kapısı "W2 ⊇ kişi anahtarı".
- §2.1, §8 W2: sahip değişimi (`ReassignOwner`, taşıma) eski beklentiyi `Superseded` ile kapatır, yenisini yeni sahipte açar.

## 18. 2026-11-25 güncel koda uyum

Kaynak: boşluk analizi (`faz2-bosluk-analizi.json`: 22 düzeltme, 14 çelişki), kargo hakem tasarımı ve sahip kararı (§16-8); ardından bağımsız denetimin 12 bulgusu (§18.5) ve son denetim (§18.6). Dayanaklar LiveDeck a9729534'te tek tek açıldı; satır numaraları o kesite göre. Panel ve Shopper dayanakları `origin/main` 06ac3d2 ve `origin/master` d0ff93e'de bakıldı.

### 18.1 Ne değişti

| # | Konu | 10.10 metni | Şimdi | Dayanak | Bölüm |
|---|---|---|---|---|---|
| 1 | Beklenti | `PaymentExpectation` + WPF kuyruğu + PUT | `PaymentRequestRecord`'dan türetilir | `PaymentRequestRecord` 63-92; `PaymentRequestRecordService` 106-154; `PaymentRequestLeaseService` 347-371 | §2.1, §4.2, §5-5, §6, §8, §9 |
| 2 | Tahsis | beklenti satırına bağlı | kayıt Id'si FK'sız, toplam kişi×kapsam; S3b'den itibaren yazılır | `LicenseDbContext` 1245-1252 (1785) | §2.2, §4.2 |
| 3 | Bağlar | `NoAction` FK | FK'sız Guid | `BankDataRetentionJob` 141-163; `BankConnectionTerminator` 389-405, 597-624 | §2.3, §2.6, §13 |
| 4 | Geri alma süresi | "her zaman", 180. gün arşivi | arşiv yok; hareket saklandıkça (en çok 180 gün) yeniden onaylanabilir, silinince de ödeme, tahsis, kargo bağı ve fazla geri alınır (§18.6-6) | `BankDataRetentionJob` 82-163; `BankOptions` 16-33 | §1, §11.2, §12 |
| 5 | Gönderen adı | `CounterpartyName` gösterilir | yazılmıyor, listede yok | `ObifinPollJob` 388-391 | §2.3, §6, §7 |
| 6 | #551 kilidi | tek kilit | beş yerde; `Unmatched` → `Awaiting` | `PaymentMatchReconciler` 74-89, 433-455, 521-597; `BankMatchSweepJob` 141-184; `PaymentMatcher` 125-131 | §2.4, §3 |
| 7 | IBAN hafızası | `ConfirmedAt`, koşulsuz silme, süresiz durum | `LastConfirmedAt` + `IbanConfirmedAt`, geri sarma, 12 ay | `PaymentMatchReconciler` 326-342, 464-497, 668-691 | §2.5, §11.2, §12 |
| 8 | Katman koşulu | `Layer = IbanMemory` | "IBAN destekliyor", `NameOnly` | `PaymentMatcher` 175-206 | §2.4, §5.1, §5.2 |
| 9 | Sürüm kapısı | "bütün PC'ler W2+" | mod On + `SalesModeGate` | `SalesModeGate` 8-34, 309-347 | §5-5, §9, §11.4, §15 |
| 10 | Bayraklar | alan ve uç | CLI + üç hâl (`enabled`, `stopped`, `off`), 409 `matching-stopped` / `questions-off` | `PersonBalanceCommand` 296-304; `ObifinConnectionService` 280-291; `PaymentMatchReconciler` 286-290 | §2.7, §6 |
| 11 | Bakiye (D-banka) | satır kipi, tek SaveChanges | ortak bakiye §9'un dokuz maddesi, iki kip yazıcısı, `DrawAsync` geri çağrısı | `PersonBalance` 364-513; `PanelCustomerBalanceController` 288-352, 648-713 | §2, §2.8, §4.1, §4.3, §6, §11.2, §16-7 |
| 12 | WPF onayı | "panel ve WPF yolu" | yalnız panel; Aynı/Ayrı ödeme P1'de | `PanelPaymentsController` 111-146 | §4.1, §4.4, §7 |
| 13 | Kargo yönergesi | `shipmentDirective`, "yönerge sunucudan" | kalktı; kargo adımı, sunucu bağlar | `DekontEkleViewModel` 325-358; `PanelShipmentsController` 67 | §4.3, §4.6, §6, §8 |
| 14 | İdempotens | yalnız `decisionRevision` | + `requestId`, aynı istek 200 | `AppHost` 470-481 | §3, §6 |
| 15 | Müşteri araması | `GET customers?q=` | POST, ortak servis | `BankaEslestirme/Index.cshtml.cs` 264-307 | §6 |
| 16 | WPF yerleşimi | üst çubuk rozeti, sayfa, yoklama servisi | sol menü sayısı, çekmece iki sekme, `sales/state` alanı | `ShellSidebar.xaml` 213-219; `SalesChangesPullHostedService` 6-16 | §8 |
| 17 | WPF silme işareti | W1, pilot kapısı | düştü (silme eylemi yok) | `CustomerRepository` 1550; `ShopperPurgeService` 273 | §12, §14 |
| 18 | W3 ödemeler | INSERT, SQLite göçü, imleç | düştü; onaylananlar çevrimiçi | `PaymentSyncService` 29-43; `PaymentRepository` 17, 94-112 | §8 |
| 19 | Ölçüm | `HasReceipt`, 200/%2 | `Source = Receipt`, `;reeval`, kapı değil | `PaymentMatchMetrics` 47-75, 114-136 | §4.5, §11.4 |
| 20 | Silme korumaları | hareket/eşleşme bağı | yalnız kimlik değişimi reddi; diğer silmeler geçer, gap yalnız Receipt | `ObifinConnectionService` 844-910; `PaymentMatchReconciler` 366-377 | §4.4, §12, §13 |
| 21 | Personel | sınırsız | son 48 saat (varsayılan); yoklama jetonla durur | `JwtOptions` 9; panel `auth/store.ts`, `Money.test.ts` | §7, §16-1 |
| 22 | Pilot kapıları | aydınlatma, silme işareti | mod On, kargo hattı, kargo kümesi ölçümü, S4, T1, reset, 2FA, profil | KVKK planı madde 15, 27, karar 5, 9 | §14 |
| 23 | Kişi düzeyi eşleştirme | yalnız IBAN sayımı | kullanıcı adı, IBAN, ad katmanları (S2b), `MatcherVersion` | `PaymentMatcher` 160-188, 227-240 | §2.4 |
| 24 | `payments/sync` | W3'e bırakılmıştı | S2b: onaylı ve banka satırı ezilmez | `LicensesPaymentsSyncController` 98-110 | §2.3 |
| 25 | PR adları | W1-W3, 1-6 | B-W0, B-W1, B-W2a, B-W2b; 1 → S1 (+ S1b), 2 → S2a + S2b, 3 → S3a-S3d, 4 → S4, 5 → S5 (+ P3), 6 → S6 | satış senkronu §13, ortak bakiye §12 | §8, §15 |
| 26 | Kargo (25.11) | bağlama adımı yoktu | kalıcı adım + sunucu, karar masaüstünde | §4.6'daki satırlar | §4.6, §11.2, §15, §16-8 |

### 18.2 Eskiyen, yazılmayacak
`PaymentExpectation` tablosu ve `PUT payment-expectations`; `baseRevision`, `Conflicted`, `RevisedAfterPayment`, `Superseded` ve WPF beklenti kuyruğu; "W2 ⊇ kişi anahtarı" sürüm kapısı; W3 INSERT yolu, SQLite göçü ve `payment-decision-in` sıfırlaması; `payments/since` genişlemesi; üst çubuk rozeti, "Tümünü gör" sayfası, `BankQuestionPollHostedService`; W1 silme işareti; `shipmentDirective` ve `Payment.ShipmentDirective`'e dayanan kargo; `ShipmentStepAt`; `NoAction` FK; `RetentionExpired` arşivi; `CustomerIbanMemory.ConfirmedAt` alanı; `PayerName = CounterpartyName`; `GET customers?q=`.

### 18.3 Kısmen uygulanan ya da yorum eklenen
- **13. düzeltme:** "pilotta WPF zinciri çalıştırır, panel bekliyor yazar, sunucu bağlama S6'da" ve "havale kartında üç seçenek" kısmı uygulanmadı; sahip kararı §16-8 tersini seçti. Üç seçenek `decide` kartında.
- **Boşluk analizindeki çelişki:** olay satırının hizmet sonu ve "Kaldır ve sil"de silinip silinmeyeceği iki yerde farklı yazılıydı; S4 planına bırakıldı (§12).
- **Hakem tasarımına eklenen netleştirmeler (kural değil, uygulama ayrıntısı):** siparişi olmayan onayda siparişsiz adım; kısmen devredilmiş kümede `SessionLive` ve saklanan `Decision`; `Shortage`'da önceki tahsislerin düşülmesi (denetimden sonra S3b'den itibaren); `PaidPrice` geri almanın `PriceSetAt = BoundSetAt` ile tanınması; menü sayısının `shippingPending` dahil olması; koşucunun sorular bayrağından bağımsız olması; `stopped` iken Onaylananlar ve kargo adımının açık kalması; kargo siparişine `LicenseId` sütunu (indeks onu istiyor). Çekirdek satır aralıkları (118-143) koda göre düzeltildi (113-145).
- **Denetimde eklenenler.** Önceki metinden ayrılan iki nokta: kiralı `Unconfirmed` isteğin kümesi bağlanır (hakem tasarımı soruyordu; küme `begin`'de doğrulanmıştır ve isteği insan seçer, sormak §16-8'i daraltırdı); varsayılan seçim önce tam eşit isteğe bakar (10.10 yalnız en eskiyi seçiyordu; dekont onayının ve otomatik kuralın sırasıyla aynı). Uygulama ayrıntısı: `NothingToBind` ve `RetryExhausted`'ın saatlik denemesi; `requests` ucu (olmasaydı her "Başka müşteri" onayı `OrderSetUnknown` olurdu); `RecipientPays`'ın bugünkü çekmece koşulu; tahsis yarışının kayıt jetonuyla çözülmesi; dekont onayındaki tahsisin de S3b'ye alınması; `revertable` ve `questions-off`; `MatcherVersion` ve `LastTransferAt` sütunları; koşulların değerlendirmesinin S3a'da olduğunun yazılması (S6 yalnız eyleme bağlar; yoksa §16-6 penceresi S6'dan önce ölçülemezdi).
- **Tahsis satırının `Kind`'ı** (Waived, Cancelled, OffBank) analizde yoktu; beklenti tablosu düştüğü için vazgeçmenin yeri gerekiyordu. Şema S2a'da; S3b yalnız `Payment` türünü yazar, öbürlerinin ucu S5'te (`OffBank`'ın WPF çağrısı B-W2b).

### 18.4 Depodan doğrulanamayan
EMAR'ın satış senkronu kipi ve PC sürümleri; karşı IBAN doluluğu (Admin → Obifin sayacı); `cumulative` istek payı ve güvenilmez sipariş kümesi oranı; havuz kipinin hiçbir lisansta açık olmadığı; `--reset-iban-memory`'nin koşulmadığı; #549'un durumu (yalnız dal okundu: `docs/kvkk-metin-v2b`, 22 yer tutucu); EMAR veri sorumlusu profili. Saat tahminleri tahmindir; test koşulmadı.

### 18.5 Denetim bulguları (25.11)
Bağımsız denetimin 12 bulgusu a9729534'te tek tek doğrulandı. Hepsi yerindeydi; yalnız 3'ün bir alt iddiası reddedildi.

| # | Önem | Konu | Karar |
|---|---|---|---|
| 1 | Kritik | Pilotta ödenmiş istek açık kalıyor, seçili geliyor; kümesi boş adım sessizce kapanıyor | İşlendi §2.1, §2.2, §4.2, §4.6.1-5, §5.3, §15: tahsis S3b'de ("Evet" ve dekont onayı), siparişleri bağlı istek seçilmez, tam eşit istek öne geçer, hepsi düşen küme sorulur. S5'te geri doldurma gerekmez |
| 2 | Önemli | Alıcı ödemeli satır düzeyinde | İşlendi §4.6.1-3: kişi düzeyi |
| 3 | Önemli | "Yalnız karar isteyenler" daralıyor | İşlendi §1, §4.6.1, §14, §16-8, soru 6 ve pilot ölçümü; daralma azaltıldı (kiralı `Unconfirmed` bağlanır, "Başka müşteri"de istek seçilebilir). Reddedilen alt iddia: "her tutar eksiği" yeni değil, 10.10 §4.3 kalan > 0 olan her onayda soruyordu |
| 4 | Önemli | Kill switch hâli çelişkili | İşlendi §1, §2.7, §5.3, §6, §7, §8, §11.2: üç hâl; verilmiş kararı düzelten uçlar hep açık |
| 5 | Önemli | `shippingPending` `RetryExhausted`'ı saymıyor, `ModeOff` State değil | İşlendi §4.6.3 (bekleyen adım tanımı), §6, §14 |
| 6 | Önemli | `RuleVersion` ölçümde yok | İşlendi §2.4, §4.5: `PaymentMatch.MatcherVersion` |
| 7 | Önemli | Birleştirici ve `Payment.WpfCustomerId` sahipsiz | İşlendi §2.3, §9, §15 (S2a, S2b) |
| 8 | Önemli | `BankIbanState`'te tarih yok | İşlendi §2.5, §12: `LastTransferAt` |
| 9 | Küçük | Services→Controllers iddiası yanlış | Düzeltildi §10: `IntakeFormService` 57 ve 218 |
| 10 | Küçük | `/api/panel/search` iddiası yanlış | Düzeltildi §6; gerçek eksikler yazıldı |
| 11 | Küçük | Kodu olmayan koşullar, `SalesModeGate` eksik sayılmış | İşlendi §5-5, §5.2: `IbanHashUnverified`, `RequestInProgress`, `SalesSyncNotReady` |
| 12 | Küçük | Süresi geçmiş istek seçilemiyor, `ShippingRules` abartılı | İşlendi §2.1, §4.2 (seçilebilir), §4.6.4 (dört kopya, S1b) |

### 18.6 Son denetim (25.11)
Üç mercek (tutarlılık, kod, para) belgeyi a9729534 koduyla yeniden okudu. 28 ciddi bulgu (1-28) ayrı bir denetimde, 8 küçük bulgu (29-36) işlenirken kodda doğrulandı. Önem, doğrulamadan sonraki değerdir. Alınmayan öneriler gerekçeleriyle yazıldı. Sahip kararlarına dokunulmadı; iki yeni konu soru 7 ve 8 oldu. Saat etkisi §15'te.

| # | Önem | Konu | Karar |
|---|---|---|---|
| 1 | Önemli | Dekonta bağlı `None` satır yeniden soruluyor, "Evet" ikinci ödemeyi yazıyor | İşlendi §3, §4.4, §6, §15: doğrulanmış bağ kapanır, doğrulanmamış bağ çözülüp sorulur; gecikmeli bağ kararı da yazar; açıklaması silinmiş satır `None`'da kalmaz. "Bağlıysa koşulsuz kapat" alınmadı (§4.4 ilkesi) |
| 2 | Önemli | Dekont onayı "Hayır" denmiş havaleyi reddedilen müşteriye bağlıyor (birleşik: 35) | İşlendi §3, §4.4, §6, §11.2: yalnız `Awaiting` kapanır, öneri boşalır, `Status = NoProposal`; ayrım olaylardan. Reddedilen müşteri için yeni sütun alınmadı (gereksiz) |
| 3 | Önemli | İki kişiye düşen IBAN sorulmadan Eşleşmeyen'e düşüyor (birleşik: 28) | İşlendi §2.4, §2.5, §3, §5.1, §5.2, §6, §7: `Awaiting` olur, adaylar listelenir. "Son doğrulanan kişiyi öner" alınmadı (tahmin) |
| 4 | Önemli | Kargo adımının geçişleri eksik (birleşik: 29, 31) | İşlendi §4.6.1-3, -4, -5, -6, §4.6.2, §4.6.3, §15: eşik havuz başına, canlı yayında karar, `Shortage` kalanı izler, geçerli kararlar, `Reverting` süzgeci |
| 5 | Küçük | `SessionLive` metni çelişkili, kısmi kümede eşik (birleşik: 32) | İşlendi §4.6.1-5, -6, §4.6.3, §4.6.7, §7, §8, §14: Onaylananlar'da görünür, günlük uyarı. Off'ta `normal` ile kapatma ve 24 saatte bekleyen sayma alınmadı: biri bağlama borcunu siler, öbüründe operatörün yapacağı iş yok |
| 6 | Önemli | Hareket silinince geri almanın ucu, jetonu ve kapsamı yok (birleşik: 16) | İşlendi §1, §2.3, §6, §11.2, §15, §18.1-4: aynı uç, `{id}` = `BankTransactionId`, jeton `Payment.Status`. Yeni uç alınmadı |
| 7 | Küçük | "Aynı ödeme" ile kapatılan dekont geri almada reddedilmiş kalıyor | İşlendi §4.4, §11.2, §15 |
| 8 | Küçük | Doğrulanmamış dekont gap'inin hâli çelişkili, `WouldAutoApprove` şişiyor | İşlendi §3, §4.4, §5.1-8, §5.2 |
| 9 | Önemli | S5'in panel ve WPF yüzü sahipsiz, eski istemci kilitleniyor | İşlendi §4.3, §6, §7, §8, §11.4, §15: P3, B-W2a, B-W2b; fazlada `shownOverpayment` zorunlu. Eksikte 409 alınmadı (gereksiz kilit) |
| 10 | Küçük | PR tablosu, `OffBank` ucu, test listesi eksik | İşlendi §2.2, §6, §12, §15. Doğum kuralını S3a'ya taşımak ve S3d'ye S3c bağımlılığı alınmadı (koşucu S3d'den önce yok, uyarıyı kargo işi yazar) |
| 11 | Önemli | Güvenilmez kayıt kişiyi süresiz engelliyor | İşlendi §2.1, §5.1-5 |
| 12 | Önemli | Dekont onayının banka yan etkileri hiçbir hâle bağlı değil | İşlendi §2.7, §4.4, §6. "Dekont tahsisi üç hâlde yazılsın" alınmadı (§4.2) |
| 13 | Önemli | Geç `finish` eski isteği beklenti yapıyor (birleşik: 22) | İşlendi §2.1, §4.2, §5.1-6, §15: istek anı `StartedAt` |
| 14 | Önemli | Geri doldurulan istek güvenilir, süresi alış anından | İşlendi §2.1, §14, §15: `Kind = Record` güvenilmez, süre istek anından. "Kira varken sıraya girmesin" alınmadı (kiradan sonra mod Off'ta giden istek gerçekten yenidir) |
| 15 | Önemli | Süresi dolmuş kira `Sending` kalıyor | İşlendi §2.1, §15: etkin hâl |
| 16 | Önemli | Hareketsiz geri almanın girişi yok, liste `Decision`'dan | 6 ile: `approved` ve `revertable` `Payment`'tan okunur |
| 17 | Küçük | Banka ödemeleri panel listesine ve aramasına süzgeçsiz düşüyor | İşlendi §2.3, §15 (S2b) |
| 18 | Önemli | Aynı yayında ödemeden sonraki istekte para kayboluyor | İşlendi §2.1, §4.2, §4.3, §4.6.7, §15: eksi kalan görünür, iptalli tahsisli kayıt güvenilmez, S5'te düşüm tavanı. Büyüyen her isteği güvenilmez saymak alınmadı: kalan orada doğru, en sık akış seçilmemiş gelirdi. Ödeneni mesajdan düşmek alınmadı (tahsis iki kez düşerdi) |
| 19 | Önemli | Birleştir/Ayır'da ayrı iki istek tek istek sayılıyor | İşlendi §2.1, §2.2, §4.2, §15; 10.10'un "aynı kişi ve `ScopeKey`'de iki açık beklenti sorulur" kuralının yerini bu aldı. Adımı üyeye göre bölmek alınmadı (§16-8'i daraltır) |
| 20 | Önemli | Tahsissiz ödeme ölçütü isteğin anına bağlı | İşlendi §2.1, §4.2, §5.1-6, §6: yayının başlangıcı |
| 21 | Küçük | Geri almadan sonra bağlı kalan sipariş borcu saklıyor | İşlendi §2.1, §4.6.1-3, §4.6.5, §11.2. Artanı kendiliğinden dağıtmak alınmadı |
| 22 | Önemli | Geç `finish`, para merceği | 13 ile |
| 23 | Önemli | `Shortage` adımı isteğin kalanına bağlı değil | İşlendi §4.3, §4.6.1-3, -5, §4.6.2, §15; kargo payının kapanması soru 7 |
| 24 | Önemli | Dekont ile havale satır düzeyinde aranıyor, para iki kez onaylanıyor | İşlendi §4.4, §15: kişi düzeyi |
| 25 | Önemli | Yalnız "Evet" geri alınabiliyor | İşlendi §2.2, §4.2, §6, §7, §15: tek tahsisin geri alınması; hareketsiz kısmı 6 ile |
| 26 | Önemli | Güvenilmez listesinde kısmi bağ ve değişen küme yok | İşlendi §2.1, §4.2, §4.6.1-3, §5.2, §15. Adım dışı her bağı güvenilmez saymak alınmadı (gerçek "Eksik" kaybolurdu) |
| 27 | Küçük | Geri alma kargo kararını geri almıyor | Bilinen sınır §4.6.7, uyarı §7 ve §11.2, §1 daraltıldı; tam çözüm soru 8 |
| 28 | Önemli | Ortak IBAN sorulmuyor; iki kişilik havale | (a) 3 ile, (b) bilinen sınır §4.6.7. Fazla yazılmadan kısmi onay alınmadı (§16-4) |
| 29 | Küçük | Çözülmekte olan adım dekontun yeniden onayını düşürüyor | 4 ile, §4.6.3 |
| 30 | Küçük | Kayıt jetonu kişi×kapsam toplamını korumuyor | İşlendi §4.1, §5.3, §6, §15; satış senkronu §13-3 notu |
| 31 | Küçük | `ModeOff` ve `Reverting` adımın kapanışı yok | `Reverting`'de `decide` 409 (§4.6.2); Off'ta kapatma alınmadı (5) |
| 32 | Küçük | `SessionLive` hiçbir listede yok | 5 ile |
| 33 | Küçük | `RecipientPays` koşulu çekmeceninki değil | İşlendi §4.6.1-3, §15 |
| 34 | Küçük | Kilit altındaki çözüm birleştirme yarışını kapatmıyor | İşlendi §4.6.1-4, §15 |
| 35 | Küçük | Geri almada `Status` ve öneri boşalmıyor | 2 ile |
| 36 | Küçük | Kargo adımının kapanmayan üç hâli | (a) alınmadı (5), (b) 5 ile, (c) işlendi §4.6.1-5: onaydan sonra iptal edilen sipariş sorulur |
