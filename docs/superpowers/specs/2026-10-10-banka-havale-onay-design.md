# Banka Havalesi Onayı — Tasarım (Faz 2)

**Tarih:** 2026-10-10 · **Durum:** sahip kararları alındı (§16), inceleme bulguları işlendi (§17), uygulama planı bekliyor · **Önceki:** `2026-09-25-obifin-banka-hareketi-golge-eslestirme-design.md` (Faz 1, gölge mod — prod'da)

Kaynaklar: LiveDeck `origin/master` 3d484d73, OrderDeck-Mobile `origin/main` 76fcf8f, Shopper `1261aef`. Bu belge, gölge mod spec'inde (`2026-09-25-obifin-...-design.md:22`) "Faz 2" olarak ayrılan işin yerini alır. Satır numaraları o kesite göredir; uygulamadan önce `origin/master`'da doğrulanır. 2026-10-10 incelemesinin 57 bulgusu işlendi; kararlar §17'de.

## 1. Amaç ve kapsam dışı

**Amaç**
- Hiçbir havale ilk görüşte otomatik onaylanmaz. Yeni bir IBAN'dan ya da açıklamaya yazılmış kullanıcı adından kurulan her ilk eşleşmede yayıncıya "Bu havale X'in mi?" diye sorulur. Soru panelde ve WPF'te aynı anda görünür; birinde cevaplanınca ötekinden de kalkar.
- "Evet", bugünkü dekont onayıyla aynı zinciri çalıştırır ama dekont istemez. Aynı zamanda bu IBAN'ı o müşteriye öğretir.
- Aynı IBAN'dan sonra gelen havaleler yalnız §5'teki dar kuralla otomatik onaylanır. En küçük şüphede yine soru sorulur.
- Eşleşmeyen ya da değerlendirilemeyen havale kaybolmaz; "Eşleşmeyen" listesine düşer ya da tarama onu yeniden dener (§3).
- Yanlış onay her zaman geri alınabilir ve havale yeniden doğru müşteriye onaylanabilir (§11).

**Kapsam dışı**
- Obifin kiracılık kararı (§13'te iki seçenekle de uyumlu)
- Obifin kimliği değişince hareketlerin yeniden anahtarlanması: ortak kimlik spec'inin işi. Bu spec yalnız korumayı tanımlar (§13).
- Self-servis banka bağlama
- WPF'te ödeme geçmişi ekranı
- `GroupId` birleştirmesi
- POS ve giden hareketler (`BankOptions.ExcludedTransactionCodes`)

## 2. Veri modeli

**1. `PaymentExpectation` (yeni, ayrı tablo).** Sunucu bugün istenen tutarı bilmiyor. Kargo hesabı yalnız WPF'te (`PaymentRequestService.cs:1171-1194`, `ComputeShipping`), mesaj da serbest metin. Bu tablo o boşluğu kapatır.
- Alanlar: `LicenseId`, `WpfCustomerId`, `ScopeKey`, `RequestId` (son isteğin istemci kimliği), `Revision` (sunucu verir), `GrossAmount`, `AppliedBalance`, `Amount` (net istenen), `PaidAmount` (tahsislerin toplamı; aynı SaveChanges'te güncellenen önbellek), `Channel`, `RequestedAt`, `ExpiresAt`, `Status` (Open / Fulfilled / Cancelled), `CancelReason` (Wpf / Waived / Purged), `Flags` (Conflicted, RevisedAfterPayment), `UpdatedAt` (jeton).
- `(LicenseId, WpfCustomerId, ScopeKey)` tekil.
- **Kaynak:** WPF, isteğin müşteriye gerçekten gittiği iki noktada beklenti üretir: Cloud API `Sent` (`PaymentRequestService.cs:348`) ve wa.me `Opened` (`:362`). `deliveryJob`'un null kaldığı yollar da (`:306-319`: bakiye akışına girmeyen müşteri, çevrimdışı) dahildir; beklenti `CloseJob`'a (`:376`) bağlanmaz. `SendPending` sonuçlanınca üretilir.
- **Revizyon sırasını sunucu verir.** PUT öğesi `requestId` (her gönderimde yeni Guid; `PaymentJob`'dan bağımsız) ve `baseRevision` (bu PC'nin o anahtar için son gördüğü sunucu revizyonu) taşır. Yanıt her öğenin güncel revizyonunu döner, PC onu saklar.
  - Satır yoksa ya da `baseRevision` güncel revizyona eşitse kabul edilir: `Revision + 1`, `Conflicted` kalkar.
  - Aynı `requestId` yeniden gelirse idempotent, değişiklik yok.
  - `baseRevision` eskiyse (başka PC araya girdi, yedekten dönüş): tutar aynıysa yalnız `RequestedAt` güncellenir; farklıysa satır `Conflicted` olur ve soru sorulur.
  - `RequestedAt`'i güncel satırdan eski istek (kuyrukta gecikmiş) yok sayılır, olay yazılır.
  - **Ödemeden sonra gelen revizyon:** `PaidAmount` korunur. Yeni `Amount ≤ PaidAmount` ise satır `Fulfilled` kalır; hatırlatma mesajı ödenmiş borcu yeniden açmaz. Büyükse yalnız kalanla `Open` olur ve `RevisedAfterPayment` işareti alır (otomatik onay dışı).
- `ExpiresAt = RequestedAt + Bank:ExpectationTtlDays` (varsayılan 14). `Expired` diye bir durum yazılmaz; sorgu anında `ExpiresAt < now` ile türetilir, ayrı bir iş gerekmez. Süresi geçen beklenti yalnız otomatik onaydan ve fazla hesabından düşer; panelde "süresi geçti" diye görünür.
- Banka dışı kapanışlar (Papara, elden ödeme, WPF'te elle "ödendi", satış iptali) WPF'ten `Fulfilled` ya da `Cancelled` revizyonu olarak gelir (W2).

**2. `PaymentExpectationAllocation` (yeni).** `(PaymentId, ExpectationId, Amount, CreatedAt, ReversedAt?)`. Hangi ödemenin hangi beklentinin ne kadarını kapattığını tutar. Onayın SaveChanges'inde yazılır; geri almada `ReversedAt` alır ve `PaidAmount` yeniden hesaplanır. Tek bir `FulfilledByPaymentId` bunu taşıyamazdı: bir ödeme birden çok beklentiyi, bir beklenti birden çok ödemeyi kapatabilir.

**3. `Payment`'a eklenecek alanlar**
- `WpfCustomerId Guid?`: shopper dekontunda gönderim anında `ShopperBroadcasterLinks.WpfCustomerId`'den doldurulur, eski satırlar geri doldurulur. WPF dekontunda W3 ile sync öğesine eklenir; `SyncPaymentItem` bugün müşteri taşımıyor (`LicensesPaymentsSyncController.cs:44-51`). Bilinmiyorsa null kalır ve §5-6'da "müşterisi bilinmeyen" sayılır.
- `Source` (Receipt / Bank). PR2'de mevcut satırlar `Receipt` ile geri doldurulur.
- `ApprovalSource` (ReceiptHuman / BankHuman / BankAuto)
- `ApprovedByOperatorId Guid?`: personel onayında `TenantClaims.GetOperatorId` (`TenantClaims.cs:72`).
- `BankTransactionId Guid?`: FK **kaskadsız** (`NoAction`). Hareket silinse de ödeme silinmez.

Bankadan doğan bir satır şöyle yazılır:
- `Status = Approved`, `Amount = tx.Amount`, `PaidAt = tx.OccurredAt`, `PdfHash = null`
- `ReferansNo = "bank:{BankTransactionId:N}:{n}"`; `n` = 1 + o harekete daha önce yazılmış banka satırı sayısı. Tekil indeks (`LicenseDbContext.cs:442`) filtresiz; geri alınan satır `Rejected` olarak kaldığı için yeniden onay yeni bir `n` ile yazılır. ReferansNo çift onaya karşı koruma **değildir**; koruma `Decision` jetonu ve hareket başına tek eşleşme satırıdır (§5.3). `ObifinId` kullanılmaz, çünkü kimlik değişiminde değişir (§13).
- `PayerName = tx.CounterpartyName`: yalnız panelde görünür; shopper'a ve WPF'e gitmez (§10, §8).
- `ShopperId`, `ShopperBroadcasterLinks(LeftAt == null)` üzerinden bulunur; yoksa null.
- `ApprovedByCustomerId` insan onayında tenant kimliği, otomatik onayda null.

**4. `PaymentMatch`**
- `Status` bugünkü anlamını korur: eşleştiricinin isabeti. **Yeni değer eklenmez.** Otomatik onaylanan satırın `Status`'u `Proposed` kalır; ayrımı `Decision` yapar. `AutoApproved` diye bir Status değeri `PaymentMatchMetrics.Classify`'ı (`:157-166`, bilinmeyen değerde fırlatıyor) ve eşleştiricinin kilidini (`PaymentMatcher.cs:109`) bozardı.
- Yeni sütunlar: `Decision` (§3), `DecisionRevision` (int, her karar geçişinde +1), `AskReasons` (kapalı küme, §5.2), `DecidedVia`, `DismissReason`, `DismissNote` (180. günde silinir).
- Eşleştirici `Decision ≠ None` satırı yeniden hesaplamaz (`PaymentMatcher.cs:109` kilidine eklenir). Açık sorunun önerisi yayıncının altından değişmez.
- `ExpectationId` sütunu **yok**: bir havale birden çok beklenti kapatabilir; bunu tahsis tablosu (2) ve olay satırı taşır.
- "1 hareket = 1 eşleşme satırı" (`:1077`) ve "1 ödeme = 1 hareket" (`:1080`) indeksleri yerinde kalır.

**5. `CustomerIbanMemory` ve `BankIbanState`**
- `CustomerIbanMemory` tekil indeksi `(LicenseId, IbanHash)` yerine `(LicenseId, IbanHash, WpfCustomerId)` olur (`:1099`). Bugünkü indeksle "IBAN tek müşteriye bağlı" koşulu hep doğru çıkıyor; denetim olarak işe yaramıyor. Göç, birleştirilmiş kopyaları önce asıl kayda çevirip tekilleştirir.
- Yeni kaynak `IbanMemorySource.BankConfirm`, yeni alan `ConfirmedAt`.
- "Evet" **upsert** eder. Çift varsa (gölgede `HumanApproval` ya da `ManualMatch` ile öğrenilmiş) `LearnedFrom = BankConfirm`, `ConfirmedAt = now`, `SourceBankTransactionId = tx` olur; yoksa eklenir. Bugünkü `LearnIbanAsync` satır varsa erken dönüyor (`Reconciler:333-351`); "Evet" o yolu kullanmaz.
- Sayım asıl kayıt üzerinden yapılır: çiftlerin `WpfCustomerId`'leri `MergedIntoId` ile asıl kayda çevrilir, sonra farklı müşteri sayılır (`PaymentMatcher.cs:141-143`'teki `FirstOrDefault` yerine). Adaylar da asıl kayda çevrilir (`:204-206`).
- **`BankIbanState` (yeni, `(LicenseId, IbanHash)` tekil).** Kişiye bağ taşımaz; purge ve geri alma onu silmez.
  - `Shared`: IBAN iki farklı asıl müşteri için onaylanınca yazılır ve **hiç kalkmaz**. Bugün bu durumda yalnız log yazılıyor (`Reconciler:338-341`). Purge ya da geri alma çiftlerden birini silse de IBAN yeniden tek sahipli sayılmaz; bunun bedeli yalnız soru sormaktır.
  - `Disputed`: IBAN önerisine "Hayır" denince ya da o IBAN'dan gelen bir onay geri alınınca yazılır. Aynı IBAN için yeni bir `BankConfirm` "Evet"i gelene dek otomatik onay yok.

**6. `BankDecisionEvent` (yalnız ekleme yapılan denetim satırı)**
- Alanlar: `TxId?` ve `ExpectationId?` (FK'lar kaskadsız), `PaymentId?`, `ActorKind` (Owner / Staff / System), `OperatorId?`, `Surface` (Panel / Wpf / Sink / Sweep / ReceiptApproval / Admin), `Action`, `ReasonCode?`, `RuleVersion`.
- İlk değerlendirme olayı (`Evaluated`) ölçüm girdilerini taşır: `WouldAutoApprove` (bayrak hariç bütün koşullar), kuralın seçeceği müşteri ve beklenti id'leri, tutmayan koşullar. Karar olayları: memoryId, `LearnedFrom`, `ConfirmedAt`, beklenti id'leri ve revizyonları, tutarlar.
- Ham açıklama, ad, IBAN ve serbest not yazılmaz. `AuditLogEntry` kullanılamıyor, çünkü `AdminId` zorunlu (`AuditLogEntry.cs:7`). WPF kararları `Owner` yazılır; WPF tenant jetonuyla çağırıyor.

**7. Lisans bayrakları:** `BankQuestionsEnabled`, `BankAutoApproveEnabled` (yalnız sorular açıkken etkili), `BankQuestionsSince`. Desen `ShopperAppEnabled` ile aynı (`License.cs:35-40`). Bugün paneli lisans bayraklarına açan bir uç yok; panel bayrağı `/count` yanıtından okur (§6).

**8. Bakiye defteri:** yeni hareket türü `"bank-overpayment"`, `Reason` sabit metin ("Havale fazlası"). Defter shopper'a da açık (`CustomerBalanceTransaction.cs:5`); banka metni (ad, açıklama) oraya asla yazılmaz. Genel geri alma ucu (`PanelCustomerBalanceController.cs:198`) bu türü reddeder; satır yalnız banka geri almasıyla ya da "Fazlayı iade ettim" eylemiyle `reversal` alır (§11.2).

## 3. Bir havalenin yaşam döngüsü (`Decision`)

**Durumlar**
- `None`: karar katmanına girmemiş. Bayrak kapalıysa ya da hareket `BankQuestionsSince`'ten eskiyse gölgedir. Bayrak açıksa ve hareket Since'ten yeniyse "değerlendirilmeyi bekliyor" demektir; tarama onu yakalar.
- `Excluded`: POS gibi dışlanan kod ya da para birimi TL/TRY değil (`BankTransaction.Currency`, `BankTransaction.cs:18`; eşleştirici bugün para birimine bakmıyor).
- `Awaiting`: öneri var ama otomatik kural tutmadı.
- `Unmatched`: öneri yok, çelişki var, "Hayır" dendi ya da onay geri alındı.
- `Approved`: insan onayı. `Status` bugünkü gibi yazılır: öneri kabul edildiyse `ConfirmedByHuman`, başka müşteri seçildiyse `Contradicted`, öneri yokken elle eşlendiyse `ManualOnly`.
- `AutoApproved`: §5'teki kuralla onaylandı.
- `ClosedByReceipt`: onaylı bir dekonta bağlandı (§4.4).
- `Dismissed`: gerekçeyle "müşteri ödemesi değil" denildi.

Geri alma ayrı bir durum değildir; `Reverted` olayı yazılır ve satır `Unmatched`'e döner.

**İlk değerlendirme**
- Sink ve tarama `MatchAndResolveGapAsync` (`Reconciler:81-85`) yerine `BankPaymentDecisionService.EvaluateAsync`'i çağırır. Eşleştirici öneriyi hesaplar ama kaydetmez, ardından gap çözümü koşar ve karar verilir. Eşleşme satırı, karar, varsa ödeme ve olay **tek SaveChanges**'te yazılır. Bugün öneri kendi SaveChanges'inde kaydediliyor ve sink her hatayı yutuyor (`MatchingBankTransactionSink.cs:52`). Karar adımı ayrı olsaydı ve düşseydi satır `None`'da kalırdı; tarama da onu bir daha seçmezdi, çünkü yalnız eşleşme satırı olmayan hareketi seçiyor (`BankMatchSweepJob.cs:61-65`).
- Tarama ayrıca `Decision == None && BankQuestionsEnabled && OccurredAt >= BankQuestionsSince && FetchedAt < now − FetchGrace` satırlarını da seçer. Beklenti jetonu yarışını kaybeden otomatik onay aynı istekte yeniden değerlendirilir (normalde `Awaiting`).
- Bayrağı kapatıp açmak `BankQuestionsSince`'i sıfırlamaz. Aradaki `None` satırları tarama değerlendirir; 3 günden eski olanlar kural 7 gereği sorulur.

**Geçişler**
- `None` → {Excluded, Awaiting, Unmatched, AutoApproved, ClosedByReceipt}: yalnız ilk değerlendirmede.
- `Awaiting` → {Approved, Unmatched, Dismissed, ClosedByReceipt}
- `Unmatched` → {Approved, Dismissed, ClosedByReceipt}
- `Dismissed` → `Unmatched` (geri aç)
- `Approved` / `AutoApproved` → `Unmatched` (geri al, §11.2)
- `ClosedByReceipt` → `Unmatched` (bağı kaldır, §11.3)
- **Bir kez sorulmuş havale bir daha otomatik onaylanmaz.** Yayıncının önündeki satırın kararını arka plan işi değiştirmez: gecikmeli bağdaştırma (`PaymentMatchReconcileJob`), gap çözümü ve eşleştiricinin yeniden hesabı `Decision ≠ None` satıra dokunmaz. Tek istisna dekont onayı anındaki dar kapanış kuralıdır (§4.4); o da bir insan eylemidir.

**Jeton ve 409**
- Her mutasyon isteği ekranda görülen `decisionRevision`'ı taşır. Eşleşmezse 409 `already-decided` döner; yanıt kararın ne olduğunu, nerede ve ne zaman verildiğini söyler.
- `DecisionRevision` aynı ama satırın `UpdatedAt` jetonu değişmişse (saklama işinin kanıt boşaltması `BankDataRetentionJob.cs:40-42`, purge, birleştirme), sunucu satırı yeniden okuyup bir kez yeniden dener (`RetryOnceAsync` deseni). Yine çakışırsa 409 `stale` döner; arayüz listeyi yeniler ama "başka yerde yanıtlandı" demez.

## 4. Onay çekirdeği

### 4.1 Tek servis
- `PanelPaymentsController`'daki private yan etki metotları (`:192-281`) `PaymentApprovalService`'e taşınır. Dekont ve banka kararları bu tek servisi kullanır; kodu kopyalamak iki yolun zamanla ayrışmasına yol açardı.
- "Evet" tek `SaveChanges` içinde şunları yapar: Payment oluşturur (ya da bekleyen dekontu onaylar), `PaymentMatch`'i günceller (`Decision`, `Status`, `PaymentId`, `DecisionRevision`), IBAN çiftini upsert eder (§2.5), `BankIbanState`'i günceller (`Disputed` kalkar; ikinci asıl müşteriyse `Shared` olur), tahsisleri ve beklenti durumlarını yazar, fazla varsa bakiye satırı ekler, olay satırını yazar.
- Commit'ten sonra `PaymentApproved` etiketi uygulanır. `ShopperId` yoksa `TryApplyAndSaveByWpfCustomersAsync` kullanılır (`LabelRuleApplier.cs:131`). Ardından shopper push gider. `PaymentMatchReconcileJob` zamanlanmaz.

### 4.2 Tahsis: havale hangi isteği kapatır
- **Uygun beklenti:** müşterinin (asıl kayıt) `Open`, süresi geçmemiş, `cumulative` kapsamlı olmayan beklentileri. `cumulative` kapsam ömür boyu toplamdır ve ödeme gelince düşmüyor (`CustomerRepository.cs:317`).
- **Otomatik onayda** §5-5'teki kural hangi beklentileri seçtiyse onlar kapanır.
- **İnsan "Evet"inde** alt sayfa müşterinin açık beklentilerini listeler; en eskiden başlayarak havale tutarı bitene dek seçili gelir, yayıncı değiştirebilir. İstek seçilen beklenti id'lerini ve revizyonlarını taşır.
- Tahsis en eski beklentiden başlar; sonuncusu kısmen kapanabilir (eksik ödeme). Havale seçilen kalanları aşıyorsa artan kısım "fazla"dır.
- **Dekont onayı da** aynı servisten geçtiği için beklentiyi aynı kuralla kapatır: tek eşit beklenti varsa o; bütün açıkların toplamına eşitse hepsi; tek açık beklentiden küçükse kısmi. Kural tahsis üretemezse beklenti açık kalır ve müşteride "beklentiye bağlanmamış onaylı ödeme" oluşur. Bu durumda §5-6 o müşteride otomatik onayı kapatır; panel beklentiyi "Kalanı sil" ya da "İptal" ile kapatmayı önerir (§6).

### 4.3 Fazla ve eksik ödeme
- **Fazla (karar §16-4):** fazla yalnız **güvenilir** beklentiye karşı hesaplanır. Seçili beklentiler uygun olmalı (§4.2) ve lisansın beklenti akışı güvenilir olmalı (§5-5: bütün etkin PC'ler W2+). Bu sağlanınca "Evet" fazlayı her zaman bakiyeye yazar (`bank-overpayment`) ve alt sayfa "Fazla X ₺ bakiyeye eklenecek" der.
- **Beklenti yoksa ya da akış güvenilir değilse** fazla hesaplanmaz. Bakiyeye hiçbir şey yazılmaz, "Eksik" gösterilmez, alt sayfa "İstenen tutar bilinmiyor" der. Bu, sahibin kararının uygulanışıdır: "fazla" ancak bilinen bir isteğe göre vardır. Havalenin tamamını fazla saymak müşteriye aynı parayı iki kez kredi yazardı. Pilot PR5 ve W2'den önce koştuğu için pilotta hiç bakiye satırı yazılmaz.
- İstek gösterilen fazlayı ve kalanı da (`shownOverpayment`, `shownRemaining`) taşır. Sunucu yeniden hesaplar; tutmazsa (beklenti arada değiştiyse) 409 `stale` döner.
- **Eksik (karar §16-5):** "Evet" ödemeyi dekont onayı gibi onaylar (kargolanacak etiketi dahil). Beklentinin kalanı açık kalır (`PaidAmount < Amount`, `Status = Open`); panelde, WPF müşteri kartında ve havale listesinde **"Eksik X ₺"** görünür.
- Kalan > 0 ise alt sayfa, WPF dekont girişindeki "Kargo Ücreti Eksik" sorusunu (`DekontEkleViewModel.cs:267`) aynen sorar: Normal / Beklet / Alıcı öder. Seçim varsayılan gelmez ve `shipmentDirective` olarak gönderilir. Bu olmasaydı banka satırı varsayılan `Normal` yönergeyle kargolanır, kargo bedelini satıcı öderdi.
- Otomatik onayda fazla da eksik de oluşmaz, çünkü kural kuruşu kuruşuna eşitlik ister (§5-5).

### 4.4 Dekont ile havale aynı borç için gelirse
İlke: bir para iki kez onaylanmaz ve bir müşterinin sorusu başka bir müşterinin dekontuyla kapanmaz.
- **İlk değerlendirmede** açık bir dekont gap'i (`TryResolveGapAsync`) bu havaleyi tek aday buluyorsa havale `ClosedByReceipt` olur, ama **yalnız bağ doğrulanmışsa**: öneri dekontun müşterisiyse ya da gönderen adının token'ları açıklamada geçiyorsa (bugünkü öğretme kuralı, `Reconciler` sınıf özeti). Doğrulanmamışsa havale `Awaiting` olur (neden `ReceiptOtherCustomer` ya da `PendingReceipt`) ve dekont aday olarak listelenir.
- **"Evet" sayfasında aday dekont:** aynı tutar, ±2 gün, müşterisi seçilen müşteri ya da bilinmeyen.
  - Bekleyen dekont için "Bu dekontu onayla": yeni satır yaratılmaz, dekont onaylanır ve harekete bağlanır (`receiptPaymentId`).
  - Harekete bağlanmamış onaylı dekont için "Bu dekontun havalesi": `ClosedByReceipt`, yeni ödeme yok. Düz "Evet" yine seçilebilir (iki ayrı ödeme), sayfa uyarır.
- **Dekont onaylanırken** (`PaymentApprovalService`, panel ve WPF yolu):
  - Aynı tutarda, ±2 gün içinde, dekontun çözülen müşterisinde banka kaynaklı onaylı bir ödeme varsa onay açık bir seçim ister. Müşteri çözülemiyorsa bu arama bütün müşterilerde yapılır. **"Aynı ödeme"** dekontu sabit gerekçeyle ("Bu ödeme banka havalesiyle zaten onaylandı") kapatır; ret push'u gitmez, olay iki ödemeyi birbirine bağlar. **"Ayrı ödeme"** dekontu normal onaylar.
  - Aynı tutarda, ±2 gün içinde ve önerisi dekontun çözülen müşterisi olan **tek bir** açık soru (`Awaiting`/`Unmatched`) varsa soru aynı işlemde `ClosedByReceipt` olur ve olay yazılır. Öneri başka müşteriyse, öneri yoksa ya da birden çok soru varsa hiçbir soru değişmez; liste dekontu aday olarak gösterir. Böylece dekontlarda "Tümünü onayla" başka bir müşterinin havalesini sessizce kapatamaz.
- **Gecikmeli bağdaştırma** (`PaymentMatchReconcileJob`, `ApprovalDelay` 20 dk, `ReconcileJob.cs:20-25`) ve gap çözümü yalnız `Decision = None` satırlara bağ kurar; `CandidatesAsync`'teki `taken` kümesi `Decision ≠ None` satırları da dışlar. Admin elle eşleme ve kaldırma (`ManualMatchAsync` `Reconciler:195-214`, `UnmatchAsync`) `Decision ≠ None` satırda reddedilir: "Bu hareket karar katmanında; panelden yönetin."
- Bankadan onaylanmış bir ödemeye sonradan dekont yüklenirse `bank-already-approved` yumuşak bayrağı döner (`ShopperPaymentSubmissionService.cs:197-203`). Bu bir red değil, uyarı. Asıl koruma onay anındaki "aynı ödeme" seçimidir; yükleme anında havale henüz çekilmemiş olabilir.

### 4.5 Gölge ölçümü
- Gölge ölçümü (`PaymentMatchMetrics`) yalnız `Decision ∈ {None, ClosedByReceipt}` satırları sayar. "Dekont onaylı karar" için bağlı ödemenin `Source = Receipt` olması gerekir (`:104`). Süzülmezse banka onayları da sayılır ve eşik kendi kendini doğrular. `Source` PR2'de geri doldurulur; doldurulmasaydı eski satırlar (NULL) ölçümden düşerdi.
- `MeetsPhase2Threshold` (`:59`) kapı olmaktan çıkar ve gölge isabeti bilgisi olarak sayfada kalır. Otomatik onayın kapısı §16-6, sayaçları §11.4.

## 5. Otomatik onay kuralı

### 5.1 Koşullar
Bütün koşullar her zaman değerlendirilir; ilk tutmayan koşulda durulmaz. Tutmayanların hepsi `AskReasons`'a yazılır. `WouldAutoApprove` 1. koşul hariç hesaplanır (§11.4). Koşullardan biri tutmazsa havale `Awaiting` olur.

1. Lisansta `BankAutoApproveEnabled` açık.
2. Öneri IBAN hafızasından geliyor (`Layer = IbanMemory`). Yalnız kullanıcı adına dayanan öneri her zaman sorulur.
3. IBAN'ın asıl kayda çevrilmiş tam olarak **bir** onaylı çifti var, bu çiftin kaynağı `BankConfirm`, ve `BankIbanState` ne `Shared` ne `Disputed`.
4. Müşteri asıl kayıt: birleştirilmiş, silinmiş ya da KVKK ile temizlenmiş değil. WPF silmesi sunucuya ulaşıyor (§12).
5. Tutar kuralı (karar §16-2), uygun beklentilerin (§4.2) kalanları (`Amount − PaidAmount`) üzerinden, kuruşu kuruşuna:
   - havaleye eşit **tam olarak bir** beklenti varsa o kapanır;
   - yoksa ve **bütün** uygun beklentilerin toplamı havaleye eşitse hepsi kapanır;
   - aksi hâlde sorulur: aynı tutarda iki ayrı beklenti, yalnız bir alt kümenin toplamına eşitlik (hangisinin ödendiği belirsiz; tahmin yürütülmez), uygun beklenti yok;
   - `Conflicted` ya da `RevisedAfterPayment` işaretli beklenti varsa sorulur;
   - lisansın beklenti akışı güvenilir olmalı: son `Bank:ActivePcWindowDays` (varsayılan 14) gün içinde görülmüş ve devre dışı bırakılmamış her etkinleştirme W2 sürümünde ya da üstünde (`Activation.AppVersion`, `LastSeenAt`, `Activation.cs:13-20`). Eski sürüm PC'nin istekleri sunucuya hiç gelmez, toplam eksik kalır.
6. Çift ödeme izi yok:
   - aynı tutarda, ±2 gün içinde, müşterisi bu müşteri **ya da bilinmeyen** (`Payment.WpfCustomerId` null; shopper bağı çözülemeyen) bekleyen dekont yok;
   - bu müşteride aynı tutarda, ±2 gün içinde, hiçbir harekete bağlanmamış onaylı ödeme yok;
   - açık beklentinin `RequestedAt`'inden sonra onaylanmış ama hiçbir beklentiye tahsis edilmemiş ödeme yok (§4.2). Varsa beklentinin "açık" görünmesi güvenilir değildir.
7. Havale en fazla 3 günlük. Geriye dönük doldurulan eski hareketler otomatik onaylanmaz.

### 5.2 Sorma nedenleri (kapalı küme)
Metni sunucu üretir; panel ve WPF aynı metni gösterir. Bayrak durumu hiçbir zaman neden olarak gösterilmez.

| Kod | Metin |
|---|---|
| `UsernameOnly` | Öneri yalnız açıklamadaki kullanıcı adına dayanıyor |
| `IbanNotConfirmed` | Bu IBAN bu müşteri için havale onayıyla henüz onaylanmadı |
| `IbanShared` | Bu IBAN'dan birden çok müşteri ödeme yaptı |
| `IbanDisputed` | Bu IBAN için daha önce "Hayır" ya da geri alma var |
| `CustomerNotCanonical` | Önerilen müşteri birleştirilmiş ya da silinmiş |
| `NoExpectation` | İstenen tutar bilinmiyor |
| `AmountMismatch` | İstenen {X} ₺, gelen {Y} ₺ |
| `AmbiguousExpectations` | Açık isteklerden hangisinin ödendiği belirsiz |
| `ExpectationUnreliable` | İstek kaydı güvenilir değil (çakışma, ödeme sonrası değişiklik ya da istek bildirmeyen eski sürüm PC) |
| `PendingReceipt` | Aynı tutarda bekleyen dekont var |
| `UnlinkedApprovedPayment` | Bu müşteride havaleye ya da isteğe bağlanmamış onaylı ödeme var |
| `ReceiptOtherCustomer` | {Müşteri}'nin dekontuyla aynı tutar |
| `TooOld` | Havale 3 günden eski |

### 5.3 Fail-safe'ler
- Çift onaya karşı koruma: `Decision` jetonu (`DecisionRevision` + `UpdatedAt`), hareket başına tek eşleşme satırı (`:1077`) ve ödeme başına tek hareket (`:1080`). ReferansNo bu iş için kullanılmaz (§2.3).
- Beklentinin `Open → Fulfilled` geçişi jetonla tek kazananlı; kaybeden yeniden değerlendirilir. Müşteri aynı borcu iki kez öderse ikinci havale soru olarak gelir. Hatırlatma mesajı ödenmiş beklentiyi yeniden açmaz (§2.1).
- Eksik ya da fazla ödeme her zaman sorulur.
- Bayrağı kapatmak anında durdurur (kill switch).

## 6. API'ler (tek servis: `BankPaymentDecisionService`)

WPF zaten Bearer-Customer ile `/api/panel/*` uçlarını çağırıyor (`LicenseApiClient.cs:545-560`). Bu yüzden panel ve WPF aynı uçları kullanır. Uçlar `[AllowStockStaff]` taşımaz. Her mutasyon `decisionRevision` ister (§3).

| Uç | Ne yapar |
|---|---|
| `GET /api/panel/bank-transfers?state=awaiting\|unmatched\|approved\|dismissed&cursor=&take=50` | `awaiting` ve `unmatched` en eskiden, `approved` ve `dismissed` en yeniden sıralanır; imleçli sayfalama. `approved` = son 30 gündeki `Approved`, `AutoApproved` ve `ClosedByReceipt`. Alanlar: tutar, tarih, maskeli IBAN, karşı taraf adı ve açıklama (saklama süresi dolana kadar), öneri (asıl wpfCustomerId, kullanıcı adı, platform, katman), `askReasons` (kod + metin), önerilen müşterinin açık beklentileri (id, revizyon, kalan, işaretler), aday dekontlar, `decisionRevision`. `approved`'da ek olarak `ApprovalSource`, onaylayan (Owner / Staff / System), zaman ve fazla satırı. Hash ve RawJson hiçbir zaman dönmez. |
| `GET .../count` | `{enabled, awaiting, unmatched, lastBankDataAt, stuckCount}`. Rozet = `awaiting`. `lastBankDataAt` = `ObifinConnection.LastPolledAt` ile etkin hesapların `BankAccount.LastBankSyncAt` değerlerinin en eskisi. `stuckCount` = "değerlendirilmeyi bekleyen" (§3) ve 15 dakikadan eski satırlar. |
| `GET .../customers?q=` | "Başka müşteri" seçicisi: `WpfCustomerProjections` üzerinde FullName / Username / DisplayName araması; yalnız asıl, silinmemiş kayıtlar, en fazla 20 sonuç. Bugünkü `/api/panel/search` müşteriyi siparişlerden türetiyor, projeksiyon id'si dönmüyor ve siparişi olmayan müşteriyi bulmuyor (`PanelSearchController.cs:39-43, 84-96`). |
| `POST .../{id}/confirm {wpfCustomerId, decisionRevision, receiptPaymentId?, expectations[{id, revision}], shownOverpayment, shownRemaining, shipmentDirective?}` | "Evet", "Başka müşteri", elle eşleme ve dekonta bağlama (§4.4). Sunucu `wpfCustomerId`'yi asıl kayda çevirir (`CustomerIdResolver`). `overpaymentToBalance` parametresi **yok**: fazla §4.3'e göre hesaplanır, sahibin kararı (§16-4) istemciye bırakılmaz. |
| `POST .../{id}/decline {decisionRevision}` | "Hayır": havale `Unmatched`'e düşer; öneri IBAN hafızasından geldiyse IBAN `Disputed` olur. |
| `POST .../{id}/dismiss {decisionRevision, reason, note?}` ve `.../{id}/restore {decisionRevision}` | Müşteri ödemesi olmayan hareketi kapatır; geri açılabilir. Gerekçe kapalı küme: `OwnTransfer`, `MarketplacePayout`, `Refund`, `Other`. `note` yalnız `Other`'da alınır ve 180. günde silinir. Sistem arşivi `RetentionExpired` kullanır (§12). |
| `POST .../{id}/revert {decisionRevision, reason}` | Banka kaynaklı onayı geri alır ya da `ClosedByReceipt` bağını kaldırır (§11). Gerekçe: `WrongCustomer`, `NotCustomerPayment`, `BankReturned`, `Other`. |
| `POST .../{id}/overpayment-refunded {decisionRevision}` | "Fazlayı iade ettim": `bank-overpayment` satırını `reversal` ile kapatır, olay yazar. |
| `GET /api/panel/payment-expectations?wpfCustomerId=` ve `POST .../{id}/waive`, `.../{id}/cancel` | Açık beklentiler ve kalanları (panel ve WPF'teki "Eksik X ₺" için). "Kalanı sil" ve "İptal" olay yazar. |

Genel bakiye geri alma ucu `bank-overpayment` satırını 409 `use-bank-revert` ile reddeder.

**Yalnız WPF'in kullanacağı uçlar**
- `PUT /api/v1/licenses/{id}/payment-expectations`: toplu ve idempotent. Öğe `requestId` ve `baseRevision` taşır; yanıt her öğenin güncel sunucu revizyonunu döner (§2.1).
- `GET .../payments/since`: yanıta tutar, tarih, ReferansNo, WpfCustomerId, Source ve ShipmentDirective eklenir. `Source = Bank` satırda `PayerName` boş gönderilir.

**Yayıncıya push:** yeni tip `type=bank-transfer`. Yalnız yeni `Awaiting` için gider; sink ya da tarama koşusu başına tek bildirimde birleştirilir; gövde yalnız sayıdır ("3 havale onay bekliyor"), ad ve tutar taşımaz. `AutoApproved` ve `Unmatched` için push yok. Bugünkü dekont push'u gibi tenant'ın bütün cihazlarına gider (`INotificationSender.SendToCustomerAsync` rol ayırmıyor); gövde yalnız sayı taşıdığı için stok personelinin cihazına düşmesi veri sızdırmaz.

## 7. Panel arayüzü

- `/dekontlar` içinde "Dekontlar | Havaleler" segmenti olur. Yeni sekme açılmaz, çünkü alt menü 5 sekmeyle sınırlı (`lib/nav.ts:38-40`).
- Havaleler'de üç liste var:
  - **Onay bekleyen**
  - **Eşleşmeyen**; içinde "Kapatılanlar" süzgeci, geri açma buradan
  - **Onaylananlar**: son 30 gün; kim, ne zaman, insan mı otomatik mi. "Geri al", `ClosedByReceipt` satırında "Bağı kaldır" ve "Fazlayı iade ettim" buradan.
- Toplu onay ve çoklu seçim yok; toplu onay "ilk görüşte otomatik yok" kuralını sessizce delerdi.
- Rozet kaynağı `pendingTransfers` = yalnız `awaiting`. Dekontlar sekmesinde dekontlarla toplam sayı görünür; AnaScreen'e ikinci bir satır eklenir (`:133-159` deseni). `Unmatched` sayısı yalnız segmentin içinde görünür; müşteri olmayan alacaklar (kendi havalesi, pazaryeri ödemesi) rozeti hiç sıfırlanmaz hâle getirirdi.
- Satıra dokununca `RejectReasonModal` biçiminde bir alt sayfa açılır:
  - Müşteri ve platform rozeti, tutar, maskeli IBAN.
  - Sorma nedenleri: sunucunun metni (§5.2).
  - Açık beklentiler ve seçili gelenler (§4.2); altında "Fazla X ₺ bakiyeye eklenecek", "Eksik X ₺" ya da "İstenen tutar bilinmiyor".
  - Eksik varsa kargo yönergesi seçimi (§4.3).
  - Aday dekont varsa "Bu dekontu onayla" ya da "Bu dekontun havalesi" (§4.4).
  - Düğmeler: Evet / Başka müşteri / Hayır. Taşma menüsünde gerekçe seçimli "Müşteri ödemesi değil".
  - "Başka müşteri" yeni seçici ucunu kullanır (§6).
- 409 `already-decided` gelirse sonuç şeridinde "Başka yerde yanıtlandı", `stale` gelirse "Bilgi değişti, yenilendi" görünür; ikisinde de liste yenilenir.
- Tazelik: sayaç sorgusu sekme görünürken 30 sn'de bir yenilenir, **yalnız Dekontlar menüsü olan oturumda** (BottomNav deseni). Stok personeli yoklama yapmaz ve 403 üretmez. Liste yalnız segment açıkken çekilir. Panelde bu ilk yoklama olacak.
- Segment `/count`'tan gelen `enabled` false ise gizli kalır. `lastBankDataAt` eşikten eskiyse (`Bank:StaleFeedAfter`, varsayılan 2 saat) ya da `stuckCount > 0` ise segmentin üstünde "Banka verisi X'ten beri gelmiyor" şeridi çıkar.
- Bakiye kartında `bank-overpayment` → "Havale fazlası" (`customerBalance.ts` `kindLabel`).
- **Dağıtım:** web paneli merge'te hemen canlıya çıkıyor (`ci.yml:40-90`). Android paneli ise `dist`'i paketliyor (`capacitor.config.ts`'te `server.url` yok), yani P1 de AAB ister. P1 ve P2 (push dokunuşu `/dekontlar?tab=havale` adresine gider; eski sürüm bilinmeyen tipte `null` döner, `deepLink.ts`) tek AAB'de çıkar; Play yüklemesi elle yapılır. Pilot web paneli ve WPF üzerinden koşar, AAB'yi beklemez.

## 8. WPF arayüzü

- Modal pencere yok; yayın sırasında operatörün işini keser.
- Üst çubukta form zilinin kopyası bir rozet: "Havale onayı (3)" (`ShellTopBar.xaml:143-168`), yalnız `awaiting`.
- Rozete tıklanınca **sağ çekmece** açılır (`IDrawerService`): onay bekleyen sorular ve panelle aynı düğmeler. Çekmece canlı sipariş akışını örtmez; yayın sırasında bir soru buradan yanıtlanır. Sayfalar (`IPageService`) yayın dışı görünümler içindir ve içerik alanının tamamını kaplar.
- Çekmecedeki "Tümünü gör" sayfayı açar; Destek Talepleri deseniyle aynı (`MainShellViewModel.cs:1599-1606`). Sayfada panelle aynı üç liste bulunur. Sayfa kendiliğinden açılmaz.
- "Başka müşteri" için yeni bir müşteri seçme çekmecesi gerekir (`VariantPickerDrawer` deseni). Müşteri kimliği yerelde aranmadan önce `ResolveId` ile çözülür (`PaymentRequestService.cs:283`); sunucu onu bir kez daha asıl kayda çevirir.
- Yeni `BankQuestionPollHostedService`:
  - 30 sn'de bir yalnız `/count`'u yoklar. Liste yalnız çekmece ya da sayfa açıkken, imleçli sayfalarla çekilir; kimse bakmıyorken PC'ye ad ve açıklama inmez.
  - Son başarılı yoklamanın zamanını ve son hatayı gösterir; 401/5xx'te eski listeyi tazeymiş gibi göstermez. `lastBankDataAt` ve `stuckCount` şeridi panelle aynıdır.
  - `AppHost`'ta kaydedilir ve `App.xaml.cs`'te ayrıca başlatılır.
- Cevap çevrimiçi ve doğrudan gönderilir; kuyruğa yazılmaz. Kuyrukta bekleyen bir "Evet", başka PC'nin verdiği "Hayır"ı ezebilir. Çevrimdışıyken düğmeler pasif kalır.
- **W1** ayrıca müşteri silme işaretini müşteri senkronuna ekler (§12).
- **W2 — beklenti:** beklenti, gönderilmemiş kayıt kuyruğu üzerinden gider; bekleyen sayıya (`SyncPendingCounter`) ve kapanışta boşaltmaya (`SyncFlushService`) dahil edilir. Üretim noktaları ve revizyon kuralları §2.1'de. Banka dışı kapanışlar (Papara, elden, elle "ödendi", satış iptali) `Fulfilled` ya da `Cancelled` revizyonu gönderir. Müşteri kartındaki "Eksik X ₺" beklenti ucundan okunur.
- **W3 — ödemeler:** `ApplyServerStatus` bugün yalnız UPDATE yapıyor (`PaymentRepository.cs:94-112`). INSERT yolu ve SQLite göçü eklenir (`CustomerId`, `Source`). Bu değişiklik, shopper dekontlarının WPF'e hiç inmemesi açığını da kapatır.
  - Sunucudan inen satır `SyncedAt = now` ile yazılır. Yoksa WPF satırı geri iterdi ve sunucu `PayerName`, `Amount`, `PaidAt`, `PdfHash`'i ezerdi (`LicensesPaymentsSyncController.cs:105-110`); boş bir `PdfHash` sunucudaki çift dekont korumasını silerdi. Sunucu da banka ve shopper satırlarında istemci düzenlemesini yok sayar.
  - WPF'te `ReferansNo` UNIQUE (`014_payments.sql:13`); banka anahtarı (`bank:…:n`) yerel dekont numaralarıyla çakışmaz.
  - Göç `SyncCursor`'daki `payment-decision-in` satırını (`PaymentSyncService.cs:33`) siler. İmleç pilottaki onayların ötesine geçmiş durumda; yeniden çekim olmadan o onaylar WPF'e hiç inmez.
  - Sunucunun `UpdatedAt`'i yeniyse upsert `PayerName`'i de (boş dahil) yazar; purge temizliği WPF kopyasına böyle ulaşır. Banka satırında `PayerName` zaten boş gelir.
  - Çekilen banka ödemesi WPF'in kargo akışını tetiklemez; yönerge sunucudan gelir. `RecipientPays` geldiyse müşterinin yapışkan bayrağı (`DekontEkleViewModel.cs:376-381`) uygulanır.
  - WPF dekontunun sync öğesine, biliniyorsa, müşteri kimliği eklenir (§2.3).
  - `LabelOf`'a `bank-overpayment` → "Havale fazlası" eklenir.

## 9. Çoklu PC ve panel yakınsaması

- Durumun tek sahibi sunucu. Yerelde soru kopyası ya da imleç tutulmaz; yedekten geri yükleme, cevaplanmış soruları geri getirmez.
- Aynı anda iki basışta ikincisi 409 alır. Emsal: `PanelPaymentDecisionConcurrencyTests`.
- Cevabı veren arayüzde satır hemen kalkar; diğer PC'lerde ve panelde en geç 30 sn içinde (sayaç değişince liste yenilenir).
- Rozet sayısı PC başına değil, lisans geneli.
- Beklenti sunucuda tutulur ve revizyon sırasını sunucu verir (§2.1); kural, isteği hangi PC'nin gönderdiğinden bağımsız çalışır. Birden çok açık beklentide §5-5 geçerlidir: tek eşit beklenti ya da bütün açıkların toplamı otomatik onaylanır; alt küme eşitliği ya da aynı tutarlı iki beklenti sorulur.
- Karışık sürüm: W2 öncesi bir PC'nin istekleri sunucuya gelmez. Böyle bir lisansta otomatik onay ve fazla hesabı kapalı kalır (§5-5, §4.3).
- **Müşteri birleştirme:** `CustomerIdentityMerger` bugün IBAN satırlarını tek tek taşıyor (`CustomerIdentityMerger.cs:118-120`). Yeni tekil indekste aynı çift iki kez oluşursa birleştirme, ve onu koşan müşteri senkronu, düşerdi. Birleştirici artık çiftleri tekilleştirir (en eski `BankConfirm` kalır), beklentileri asıl kayda taşır (aynı `ScopeKey` çakışırsa ikisi de `Conflicted` olur) ve `Payment.WpfCustomerId`'yi asıl kayda çevirir. Tahsisler beklenti id'sine bağlı olduğu için etkilenmez.

## 10. Shopper tarafı

- **Gizlilik sunucuda sağlanır:** `Source = Bank` satırda shopper DTO'su `PayerName = ""` ve `ReferansNo = null` döner (`ShopperBroadcastersController.cs:299-309`), istemci sürümünden bağımsız olarak. Eski sürümler `PaymentDetailModal`'da Gönderici ve Referans no gösteriyor; yanlış bir "Evet"te başka bir müşteri gerçek ödeyenin adını görürdü.
- DTO'ya `source` alanı eklenir (`receipt` | `bank`). Yeni bir durum değeri eklenmez: eski istemciler bilinmeyen durumu "Bekliyor" olarak gösteriyor (`DekontlarımScreen.tsx:21-24`).
- Banka satırının metinleri (SH1):
  - Liste: "{tutar} havalen alındı", alt yazı "Banka havalesi".
  - Modal başlığı: "Ödeme Detayı".
  - Zaman çizgisi: "Bankaya ulaştı", ardından "Onaylandı".
- Push `type=payment-approved` olarak kalır ki eski sürümler doğru ekrana gitsin; yanına `source=bank` eklenir. Metin: "{tutar} ₺ ödemen alındı".
- Push yalnız "Evet" ve otomatik onayda gider. Ayar ipucu "Ödemen onaylandığında…" olarak değişir (`ProfilScreen.tsx:185`).
- Onaylanmamış öneri shopper'a hiç gösterilmez; tahmin yanlışsa başkasının havalesi sızardı.
- Geri alma (§11.2): `WrongCustomer` gerekçesinde satırın `ShopperId`'si boşaltılır; satır o shopper'ın uygulamasından kaybolur ve push gitmez. "Reddedildi" olarak kalsaydı başkasının ödemesi onun uygulamasında dururdu. Diğer gerekçelerde satır sabit metinle ("Ödeme doğrulanamadı") `rejected` görünür.
- Bakiye geçmişinde `bank-overpayment` → "Havale fazlası" (`queries.ts`).

## 11. Denetim, geri alma ve ölçüm

### 11.1 Denetim
- Her karar bir `BankDecisionEvent` satırı yazar (§2.6). `Payment.ApprovalSource` ve `ApprovedByOperatorId` kimin onayladığını gösterir. Personel kararı `Staff` ve `OperatorId` ile, sahip ve WPF kararı `Owner` ile kaydedilir.

### 11.2 Geri al (`Approved` / `AutoApproved`)
Giriş noktası panelde ve WPF'te "Onaylananlar" listesidir. Hepsi tek SaveChanges'te yapılır:
- **Banka kaynaklı Payment** `Approved → Rejected` olur; `RejectReason` gerekçeye göre sabit metindir. Shopper tarafı §10'da. `PaymentRejected` etiketi uygulanır (`WaLabelEvent.cs:20`).
- **"Evet"le onaylanmış dekont** (`Source = Receipt`) `Rejected` olmaz: `Pending`'e döner, hareketten ayrılır ve normal insan incelemesine geri gider. Shopper, hiç bakılmamış bir dekontu reddedilmiş görmez.
- `PaymentMatch.PaymentId`, `ActualWpfCustomerId` ve `DecidedAt` boşaltılır; `Decision = Unmatched`. `PaymentId` boşaltılmasaydı filtreli indeks (`:1080`) yeniden onayı engellerdi.
- (IBAN, müşteri) çifti `(LicenseId, IbanHash, WpfCustomerId)` ile **silinir**; `CustomerIbanMemory` dokümanı zaten silmeyi öngörüyor. Bugünkü `UnmatchOnceAsync` çifti `SourceBankTransactionId` ile siliyor (`Reconciler:216-256`); eski bir çiftten otomatik onaylanan havalede o çift kalırdı. IBAN `Disputed` olur.
- Tahsisler `ReversedAt` alır, beklentiler yeniden `Open` olur.
- **Fazla:** satır zaten kapatılmışsa (ör. "Fazlayı iade ettim") yapılacak bir şey yoktur. Değilse `reversal` yazılır. Fazla harcanmış ve bakiye yetmiyorsa geri alma **engellenmez**: bakiyede ne varsa o kadarı geri alınır, kalan tutar olay satırına ve uyarıya ("X ₺ fazla bakiyeden harcanmış") yazılır, bakiye eksiye düşmez (sahibe soru 2). Bakiye geri almasının eksiyi reddetmesi (`disallowNegative`) ve satır başına tek geri alma (`ReversesTransactionId` tekil, `LicenseDbContext.cs:880-882`) yüzünden düşen bir geri alma, yanlış onayı düzeltilemez hâle getirirdi.
- Havale sonra başka bir müşteriye onaylanırsa yeni Payment `n+1` anahtarıyla yazılır (§2.3).
- Admin sayfasındaki `UnmatchAsync` `Decision ≠ None` satırda reddedilir (§4.4); bugün onayı yetim bırakırdı.

### 11.3 Bağı kaldır (`ClosedByReceipt`)
Havale `Unmatched`'e, dekont açık gap'e döner (bugünkü kaldırma kuralı). Dekontun onayına dokunulmaz.

### 11.4 Ölçüm (§16-6'nın uygulanışı)
- Sayaçlar `PaymentMatch`'in güncel durumundan değil `BankDecisionEvent` satırlarından hesaplanır. Geri al → yeniden onay zinciri güncel satırda iz bırakmaz; güncel durumdan sayılsaydı geri alma görünmezdi.
- İlk değerlendirme olayı `WouldAutoApprove`'u, kuralın seçeceği müşteri ve beklentileri ve `RuleVersion`'ı kaydeder.
- **Uyum:** son insan kararı aynı müşteriyi ve aynı beklentileri seçti. **Çelişki:** başka müşteri, "Hayır", "Müşteri ödemesi değil", farklı beklenti seçimi ya da "otomatik olurdu" kararının sonradan geri alınması. `ClosedByReceipt`, dekontun müşterisi kuralınkiyle aynıysa uyum, değilse çelişki sayılır. Yanıtsız sorular sayılmaz, ayrıca gösterilir.
- Sayılanlar: sorulan, Evet, Başka müşteri, Hayır, Müşteri ödemesi değil, Geri al, otomatik kuralın tutacağı ve tutmayacağı, uyum, çelişki.
- **Pencere** ancak şu üçü sağlanınca başlar: EMAR'da beklentiler akıyor (PR5 + W2); EMAR'ın bütün etkin PC'leri W2+ (§5-5); "Onaylananlar" listesi panelde ve WPF'te canlı. Beklentisiz pilotta kural 5 hiç tutmaz, "otomatik olurdu" sayısı sıfır kalır ve "çelişki yok" kanıtsız geçer. Geri alma yapılamıyorsa da "hiç geri alma yok" kendiliğinden doğru çıkar. `RuleVersion` değişince pencere yeniden başlar.
- Admin ölçüm sayfası bu sayaçları gösterir.

## 12. KVKK

- IBAN yalnız hash ve maske olarak saklanır. Olay satırları ve loglar yalnız kimlik içerir (`Reconciler:60`).
- Elle eşleme kuyruğunda 150 günü geçen satırlarda uyarı gösterilir, çünkü açıklama ve ad 180. günde siliniyor (`BankDataRetentionJob.cs:33-42`). 180 günü geçen `Unmatched` satır sistem tarafından `Dismissed (RetentionExpired)` olarak arşivlenir ve geri açılabilir. Aynı iş `DismissNote`'u da siler.
- RawJson içindeki serbest açıklama temizlenir. Bugün yalnız adında IBAN, VKN ya da TCKN geçen alanlar siliniyor (`BankRawJsonRedactor.cs:9-18`).
- **Pilottan önce şart** (yalnız otomatik onaydan önce değil): WPF'teki müşteri silmesi sunucuya ulaşmalı. Bugün ulaşmıyor; purge yalnız shopper bağı üzerinden yürüyor (`ShopperPurgeService.cs:195-255`). WPF müşteri senkronuna bir silme işareti eklenir (`LicensesWpfCustomersSyncController.cs:72`, W1) ve sunucu projeksiyonu `MarkPurged` ile işaretler. Yoksa silinmiş müşteri öneri ve seçici adayı olarak kalır; operatör "Evet"e basıp o kişiyle yeni bir finansal bağ kurar.
- Purge **silmez, boşaltır** (`ShopperPurgeService` ile tutarlı):
  - IBAN çiftleri silinir (kişi bağıdır); `BankIbanState` kalır (kişi bağı yok).
  - Banka kaynaklı Payment'larda `PayerName = ""` olur; tutar, tarih ve ReferansNo kalır.
  - Bağlı hareketlerin açıklaması ve karşı taraf adı boşaltılır.
  - Açık beklentiler `Cancelled (Purged)` olur.
- `Payment.PayerName`, dekonttaki gönderen adıyla aynı statüde bir finansal kayıt olur; yalnız panelde görünür, shopper'a ve WPF'e gitmez (§10, §8). Purge onu `WpfCustomerId` üzerinden boşaltır. Bu karar spec'e açıkça yazılır.
- Serbest metin girişi yok: dismiss ve geri alma gerekçeleri kapalı kümedir; tek serbest not (`Other`) 180. günde silinir.
- Bakiye satırının `Reason`'ı sabit metindir; defter shopper'a açık (§2.8).
- Yayıncı push'u yalnız sayı taşır; kilit ekranına üçüncü kişinin adı düşmez (§6).

## 13. Obifin kiracılık bağımlılığı

`ObifinConnection` bugün lisans başına (`ObifinConnection.cs:7-10`). Karar katmanı yalnız `BankTransaction.LicenseId`'ye bakar; bağlantı **modelinden** bağımsızdır, iki seçenekle de çalışır:

- **(B) Lisans başına kimlik:** değişiklik gerekmez.
- **(A) Tek paylaşılan kimlik:**
  - Hareket içeri alınırken `BankAccount → LicenseId` eşlemesi zorunlu ve tekil olur.
  - Eşlenmemiş hesabın hareketi hiçbir lisansa yazılmaz, admin ekranına düşer.
  - Hash anahtarı global olduğu için tüm hafıza sorguları `LicenseId` ile süzülür; lisanslar arasında IBAN öğrenimi olmaz.

İki seçenekte de bayraklar lisans başına.

**Kimlik değişikliğinden ise bağımsız değildir.** `UserCode` ya da `BaseUrl` değişince `UpsertWithResultAsync` (`allowShadowReset`, `ObifinConnectionService.cs:177`) lisansın bütün hareketlerini, eşleşmelerini (kaskadla), IBAN hafızasını ve gap'lerini siler ve imleci sıfırlar (`:560-571`); sonraki çekim 90 günü yeniden doldurur. Faz 2'de o satırlar finansal kararlardır ve EMAR'ı (A)'ya taşımak tam olarak bu işlemdir. Yeniden doldurulan hareketler yeni `ObifinId`'lerle gelir, yeniden sorulur, bir kısmı yeniden otomatik onaylanırdı: ikinci Payment, ikinci push, ikinci fazla.
- Lisansta `Decision ≠ None` bir satır ya da `Source = Bank` bir ödeme varsa gölge sıfırlama **reddedilir** (PR2).
- Bundan sonra kimlik değişimi ayrı bir yeniden anahtarlama işlemidir ve ortak kimlik spec'inin parçasıdır. O işlem satırları korur; eskiden yeniye eşlemeyi bankaya özgü doğal anahtarla yapar (hesap IBAN hash'i, `BankReference`, tutar, `OccurredAt`) ve bilinen doğal anahtar için yeni hareket yaratmaz. Önce EMAR verisinde `BankReference` doluluk oranı ölçülür. Herhangi bir yeniden doldurmadan sonra `BankQuestionsSince` o ana çekilir.
- `Payment.BankTransactionId` ve `BankDecisionEvent.TxId` hiçbir zaman kaskadla silinmez (§2.3, §2.6).

## 14. Yayına alma

- Bayraklar varsayılan olarak kapalı; pilot EMAR.
- Önce yalnız sorular açılır (`BankQuestionsSince` = açılış anı); eski hareketler gölgede kalır. Otomatik onay daha sonra, ayrı bayrakla ve §16-6 penceresinden sonra açılır.
- Sıra: sunucu, sonra web paneli, sonra WPF (Velopack). Android paneli (P1 + P2 AAB) ayrı ve elle yüklenir; pilot onu beklemez (§7).
- **Pilottan önce kapılar:**
  - WPF müşteri silme işareti sunucuda ve WPF'te canlı (PR4 + W1, §12).
  - Gizlilik ve aydınlatma metni güncel: kategori (banka hareketi, IBAN hash'i), amaç (ödeme eşleştirme ve onay), saklama (90/180 gün), alt işleyen (Obifin), otomatik onay ve yayıncı üzerinden itiraz yolu. Metin sahibin onayıyla yayınlanır.
- **Genişlet/daralt:** yeni sütunlar nullable, önce okuyucu yayınlanır. `Source = Receipt` geri doldurması PR2'de yapılır. Bütün yeni enum değerleri (`Decision`, `AskReasons`, `ApprovalSource`, `IbanMemorySource.BankConfirm`, gerekçeler) PR2'de tanımlanır, yalnız sonraki PR'lar onları yazar. Enum'lar string saklanıyor; otomatik geri alma imajı geri alır ama veriyi almaz, eski kod bilmediği değerde düşer.
- **Gözlem:** bugün çekim işi `AutomaticRetry=0` ve hata yalnız Hangfire'da görünüyor; Obifin başarılı ama boş sayfa dönerken banka tarafındaki senkron durursa (`BankAccount.LastBankSyncAt`) yayıncı "havale yok" ile "besleme öldü"yü ayırt edemiyor. Yeni `bank-feed-watchdog` işi saatte bir koşar; besleme bayatsa (`lastBankDataAt` eşikten eski), `ObifinConnection.LastError` doluysa ya da `stuckCount > 0` ise `Admin:AlertEmail`'e yazar (`BackupRestoreDrillJob.cs:106` deseni; aynı durum için günde bir kez).
- Merge ve deploy yayın penceresine denk gelmez (Paz/Pzt/Çar/Per 20:00–01:00).

## 15. PR sırası

**LiveDeck (sunucu)**
1. `PaymentApprovalService` çıkarımı; davranış değişmez.
2. Şema ve korumalar: Payment sütunları (+ `Source` geri doldurması), `Decision` / `DecisionRevision` / `AskReasons`, IBAN indeksi (tekilleştiren göç), `BankIbanState` ve upsert, `MergedIntoId` çözümü ve birleştirici, `BankDecisionEvent`, `PaymentExpectation` ve tahsis tablosu, bayraklar, ölçüm süzgeci, bütün enum değerleri, kaskadsız FK'lar, gölge sıfırlama reddi, eşleştirici kilidi.
3. `bank-transfers` uçları (dört liste, sayfalama, sayaç ve sağlık alanları, seçici), tek SaveChanges'li değerlendirme ve tarama yüklemi, dekont etkileşimi (§4.4), geri alma ve bağ kaldırma, yayıncı push, shopper DTO boşaltması, admin sayfası korumaları, watchdog. Otomatik onay yok; bakiye satırı yok.
4. KVKK: WPF silme işaretinin sunucu tarafı, purge boşaltması, RawJson temizliği, 180 gün arşivi.
5. Beklenti ucu (sunucu revizyonu), beklenti okuma ve vazgeçme uçları, tahsis ve fazla hesabı, dekont onayında tahsis, `payments/since` genişlemesi, `bank-already-approved` bayrağı.
6. Otomatik onay kuralı (bayrak arkasında).

**OrderDeck-Mobile:** P1 Havaleler segmenti (üç liste, alt sayfa, seçici, bayrak ve yoklama kuralı, "Havale fazlası" etiketi); P2 push deep link. İkisi tek AAB'de.

**OrderDeck-Shopper:** SH1 `source` alanına göre metinler, push metni ve "Havale fazlası" etiketi.

**WPF:** W1 rozet, çekmece, sayfa, yoklama servisi, müşteri seçici ve müşteri silme işareti; W2 beklenti kuyruğu ve banka dışı kapanışlar; W3 payments INSERT yolu, göç, imleç sıfırlama, `PayerName` kuralı, kargo yönergesi ve `LabelOf`. Ardından Velopack sürümü.

**Akış:** 1 → 2 → 3 → 4 → P1 (web) → W1 → aydınlatma metni → **pilot: EMAR'da yalnız sorular** → 5 + W2 + W3 → EMAR'ın bütün PC'leri W2+ → **§16-6 penceresi** → 6 → rakamlar sahibe → EMAR'da otomatik onay. SH1, 3'ten sonra paralel ilerleyebilir; gizlilik sunucuda sağlandığı için pilot SH1'i beklemez.

### Testler
Çift onay güvenliği tekil indekslere ve jetonlara dayanıyor; InMemory ikisini de uygulamıyor (`PanelPaymentDecisionConcurrencyTests` notu). Bu yüzden:
- **`SqlServerContainerFixture` (gerçek SQL Server):** sink otomatik onayı ∥ panel "Evet"; "Evet" ∥ dekont onayı; geri al ∥ onay; iki havale ∥ tek beklenti; tarama ∥ sink; iki PC'den aynı anda "Evet"; geri al → başka müşteriye onay (`n+1` anahtarı); gölge sıfırlamanın reddi.
- **Göç testleri** (`CustomerProjectionUniqueIdentityMigrationTests` deseni): IBAN indeksinin tekilleştirilmesi, `Source` geri doldurması.
- **WPF SQLite (W3):** INSERT yolu, `SyncedAt`, imleç sıfırlama, `PayerName` üzerine yazma.
- **İdempotens:** aynı hareketin sink'e yeniden gelmesi; aynı `requestId`'li beklenti PUT'u.
- InMemory yalnız saf mantık için: §5 kural tablosu, tahsis, sorma nedenleri, revizyon kuralları.

## 16. Sahip kararları (2026-10-10)

| # | Konu | Karar |
|---|---|---|
| 0 | İlk görüşte | Hiçbir havale ilk görüşte otomatik onaylanmaz; ilk eşleşmede panelde ve WPF'te sorulur, "Evet" = ödeme onayı (dekont gerekmez) + IBAN öğrenimi. |
| 0b | Sonraki havaleler | Aynı IBAN'dan sonrakiler §5 kuralıyla otomatik; IBAN tek müşteriye bağlı değilse ya da tutar tutmazsa sorulur. |
| 1 | Personel | Dekontu onaylayabilen personel havaleyi de onaylar; stok personeli görmez. |
| 2 | Birden çok açık istek | Havale tek bir isteğe ya da **bütün** açık isteklerin toplamına eşitse otomatik onay; alt küme eşitliği sorulur (§5-5). |
| 3 | Gölge dönemde öğrenilen IBAN | "İlk onay" sayılmaz; bu akışta bir kez daha sorulur (§5-3: yalnız `BankConfirm` kaynağı). |
| 4 | Fazla ödeme | Fazla tutar her zaman bakiyeye yazılır (§4). |
| 5 | Eksik ödeme | Onaylanır (dekontla aynı), "Eksik X ₺" uyarısı görünür, kalan açık kalır (§4). |
| 6 | Otomatik onayın açılışı | Veriye bağlı: yalnız-soru pilotunda ≥ 2 hafta ve ≥ 30 "Evet"; "otomatik açık olsaydı" kararlarından hiçbiri insan kararıyla çelişmemiş ve hiç geri alma yok → rakamlar sahibe sunulur, sahip "aç/bekle" der. Admin ölçüm sayfası bu sayaçları gösterir: sorulan, Evet, Başka müşteri, Hayır, Geri al, otomatik kuralın tutacağı ve tutmayacağı. Bayrak her an kapatılabilir. |
| 4 (uygulama) | Fazla ödeme | "Fazla" yalnız güvenilir açık beklentiye karşı hesaplanır. Beklenti yoksa ya da beklenti akışı güvenilir değilse bakiyeye hiçbir şey yazılmaz ve ekran "İstenen tutar bilinmiyor" der (§4.3). |
| 6 (uygulama) | Otomatik onayın açılışı | Sayaçlar olay satırlarından hesaplanır. Pencere, beklentiler akınca, bütün PC'ler W2+ olunca ve "Onaylananlar" listesi canlıyken başlar; kural sürümü değişince yeniden başlar (§11.4). |

**Bağımlılık — Obifin kiracılık:** sahip tek paylaşılan Obifin kullanıcısı + banka bağlantısı→lisans eşlemesini istiyor; Obifin'e "sakıncası var mı" sorusu hafta başı gidiyor (§13). Bu spec iki modelle de çalışır; seçilen model ayrı bir spec/PR dizisidir. Kimlik değişimine karşı koruma bu spec'te (§13).

### Sahibe sorulacaklar (inceleme sonrası)
1. **§16-6'ya alt sınır.** "Otomatik olurdu" vakaları için bir alt sınır eklensin mi? Alt sınır olmazsa kural pencerede hiç tutmadığında "çelişki yok" kanıtsız geçer (sıfır vaka, sıfır çelişki). **Öneri:** en az 20 "otomatik olurdu" vakası; sıfır çelişki, sıfır geri alma.
2. **Harcanmış fazlanın geri alınması.** Yanlış onay geri alınırken bakiyeye yazılmış fazla çoktan harcanmışsa ne olsun? **Öneri (karar gelene dek varsayılan):** geri alma engellenmez, bakiyede ne varsa o kadarı geri alınır, kalan tutar uyarı olarak gösterilir, bakiye eksiye düşmez. Alternatif: bakiye eksiye düşer ve müşteri kartında "Borç X ₺" görünür (bugün bakiye hiçbir yolda eksiye inmiyor).

## 17. İnceleme (2026-10-10) — bulgu kararları

Mercekler: **kod** = kod tutarlılığı, **para** = para güvenliği, **kvkk** = kullanım ve KVKK, **eleştirmen** = bütünsel eleştiri. "Birleşik" aynı tasarım kararına bağlanan bulguları gösterir.

| # | Mercek | Önem | Konu | Karar |
|---|---|---|---|---|
| 1 | kod | Kritik | Geri al sonrası ReferansNo yeniden onayı engelliyor | İşlendi §2.3, §11.2 (birleşik: 23) |
| 2 | kod | Önemli | Karar yazımı düşerse havale None'da kalıyor | İşlendi §3: ayrı değer yerine tek SaveChanges + tarama yüklemi (birleşik: 19) |
| 3 | kod | Önemli | PaymentId yazan yollar Decision'ı atlıyor | İşlendi §3, §4.4, §11.2 (birleşik: 18, 44) |
| 4 | kod | Önemli | Gölge IBAN çifti BankConfirm'e yükselmiyor | İşlendi §2.5 (upsert) |
| 5 | kod | Önemli | Ortak IBAN işareti silmeyle kayboluyor | İşlendi §2.5 `BankIbanState` (birleşik: 25) |
| 6 | kod | Önemli | WPF dekontunun müşterisi bilinmiyor | İşlendi §2.3, §5-6, §8 W3 (birleşik: 17) |
| 7 | kod | Önemli | Beklentisiz fazla, overpaymentToBalance çelişkisi | İşlendi §4.3, §6, §16 "4 (uygulama)" (birleşik: 16, 33) |
| 8 | kod | Önemli | Fazla geri alınamayınca geri alma düşüyor | İşlendi §2.8, §11.2 + sahibe soru 2 (birleşik: 23) |
| 9 | kod | Önemli | Seçici projeksiyon id'si döndürmüyor | İşlendi §6 seçici ucu, §7 (birleşik: 34) |
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
