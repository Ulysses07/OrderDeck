# WhatsApp Etiket Altyapısı — Tasarım

> **Durum:** ONAYLANDI (2026-08-18). Beş bölümün tamamı kullanıcı tarafından
> bölüm bölüm onaylandı. Sıradaki adım: `writing-plans` ile uygulama planı.
> **HARD GATE: plan onaylanmadan kod yazılmaz.**
>
> **Repo:** LiveDeck (sunucu) + OrderDeck-Mobile (panel ekranları)

## Bağlam / İstek

Kullanıcı isteği: **"Faz 2 — WhatsApp otomatik ürün bildirimi + yedek bildirimi;
şimdilik yalnız sunucu/altyapı tarafı (Meta şablon onayına bağlı kısımlar sonra)."**

Konuşma sırasında Faz 2'nin iki bağımsız alt sistem olduğu netleşti ve **ikiye
ayırma kararı verildi:**

1. **Etiket altyapısı** (BU belge) — olay → sohbet etiketi. **Meta'ya bağlı
   değil**, şablon onayı beklerken tamamı yazılıp yayına alınabilir; ödeme/kargo
   etiketleri hemen işe yarar.
2. **Yedek bildirimi** (ayrı, sonraki spec) — iki yönlü, buton cevaplı, sıralı
   teklif, 1 saat timeout, otomatik terfi. **WPF sürüm yayınına kilitli**
   (`ParentLabelId` telde yok → sunucu yedek zincirini kuramıyor; terfiyi WPF'e
   geri verecek order-pull kanalı yok; 340-commit sürüm borcu burada ısırıyor).
   Etiket altyapısı bittiğinde bu spec ona sadece kendi üç olayını ekler.

### Çözülen gerçek problem

Yayıncının bugünkü elle akışı: mesajlara bakan bir çalışan dekont gelen sohbeti
"Dekont geldi" listesine alıyor; kargo çalışanları o listeden gidip dekontu açıp
tutar/gönderen adını doğruluyor; kargo hazırlanınca hazır mesaj atılıp etiket
elle kaldırılıyor. **Kaçırma riski birinci adımda:** yoğun mesaj akışında dekont
gözden kaçıyor ve para kayboluyor.

## Kilitlenen kararlar

- **Etiketler tamamen dinamik ve yayıncıya ait** — biz hiçbir etiket
  tanımlamıyoruz. Yayıncı panelde istediği kadar açar ("Ödeme geldi",
  "Kargolandı", "VIP", ne isterse). Tablo boş başlar.
- **Otomatik kural, dinamik etiketi SABİT olaya bağlar.** Olaylar dinamik olamaz
  (sunucu ancak bildiği anları tetikler); ama her olay yayıncının kendi açtığı
  herhangi bir etikete bağlanabilir.
- **Bir sohbet aynı anda birden çok etiket taşır** → `WaConversationLabel` ara
  tablosu. Gerekçe: olaylar birikir (dekont → ödeme → kargo) ve elle konan `VIP`
  gibi etiketler bunlarla aynı anda durmalı; tek alan olsaydı her yeni olay
  öncekini ezerdi.
- **Etiketler yalnız ELLE kaldırılır.** Sunucu hiçbir etiketi otomatik düşürmez;
  ödeme onayı `Dekont geldi`yi silmez, sadece kendi etiketini ekler. Gerekçe:
  yayıncı neyin ne zaman kapandığına kendisi karar versin. **Sonucu:** "iş var"
  tipli etiketler birikir → panelde kaldırma tek tık olmalı.
- **Telefonla iş çıkmayanlar sessizce atlanır** (hata değil): TR dışı numara
  (`PhoneNormalizer` reddediyor) ve hiç WhatsApp yazmamış müşteri.
- **Cloud API'de etiket API'si YOK** (Meta'nın 4 resmi yüzeyinden doğrulandı —
  bkz. memory `reference_whatsapp_cloud_api_limitleri.md`). Kategorileme yalnız
  bizim panelimizde.
- **Uygulama noktası: ortak `LabelRuleApplier` servisi.** Alternatif "her
  controller'ın içine yaz" aynı mantığı 5 yere dağıtırdı (normalize kuralı
  değişince 5 dosya, yeni olayda unutma riski). Alternatif "outbox + Hangfire"
  gecikme ve ayrı hata yolu getiriyordu; bu ölçekte karşılığı yok.
- **Kapsam: geniş** — gelen PDF dekontlar mevcut `PdfDekontParser`'dan geçirilir.

---

## Bölüm 1 — Veri modeli

**`WaLabel`** — yayıncının kendi etiketi
```
Id (Guid) · LicenseId · Name · Color · CreatedAt
```
`(LicenseId, Name)` benzersiz. Tablo boş başlar. `Color` serbest hex değil,
panelin sabit paletinden bir değer — rozet okunabilirliği garanti olsun diye.

**`WaLabelRule`** — "şu olay olunca şu etiketi yapıştır"
```
Id · LicenseId · EventKey (enum) · WaLabelId · CreatedAt
```
`(LicenseId, EventKey)` benzersiz — bir olayın tek kuralı olur. Etiket silinince
bağlı kural `Cascade` ile düşer.

`EventKey` enum'u (sabit, 5 değer):
```
PaymentApproved · PaymentRejected · OrderReceived
ShipmentStatusChanged · CustomerSentDocument
```

**`WaConversationLabel`** — sohbet ↔ etiket (çoklu)
```
ConversationId · WaLabelId · Source ("auto"|"manual") · CreatedAt
```
`(ConversationId, WaLabelId)` benzersiz — mükerrer koruması DB seviyesinde.
Silme yalnız elle. Etiket silinirse bu satırlar da `Cascade` düşer; geçmiş
sohbette "silinmiş etiket" hayaleti kalmaz.

**`WaDekontExtraction`** — mesajla 1-1 opsiyonel (Bölüm 3)
```
WaMessageId (PK/FK) · LicenseId · PayerName · Amount · PaidAt
ReferansNo · PdfHash · ParserConfidence · CreatedAt
```
`WaMessage`'a kolon eklenmiyor: bu alanlar mesajların ~%1'inde dolu olur, ana
tabloyu boş kolonlarla şişirmenin anlamı yok.

## Bölüm 2 — `LabelRuleApplier` akışı

Tek giriş noktası:
```csharp
Task ApplyAsync(Guid licenseId, WaLabelEvent eventKey, string phone, CancellationToken ct)
```

Dört adım:

1. **Kural var mı?** `WaLabelRule`'da `(LicenseId, EventKey)`. Yoksa sessizce çık
   — yayıncı bu olay için etiket seçmemiş, hata değil.
2. **Telefonu normalize et** (`Services/Auth/PhoneNormalizer.cs` → `+90XXXXXXXXXX`).
   Başarısızsa sessizce çık (TR dışı numara). WhatsApp'ın `905443579314` biçimi
   doğru normalleşiyor.
3. **Sohbeti bul** — `WaConversation`'da `(LicenseId, CustomerPhone)`. Yoksa
   sessizce çık (müşteri hiç WhatsApp yazmamış).
4. **Etiketi yapıştır** — `WaConversationLabel` ekle, `Source = "auto"`. Zaten
   varsa hiçbir şey yapma.

**Belge olayı için ayrı aşırı yükleme:**
```csharp
Task ApplyToConversationAsync(Guid licenseId, WaLabelEvent eventKey, Guid conversationId, CancellationToken ct)
```
O olayda sohbet zaten elimizde; telefon eşleştirmesine hiç girilmez.

**Çağrıldığı yerler (koddan doğrulandı):**

| EventKey | Tetiklendiği yer | Telefon nereden |
|---|---|---|
| `PaymentApproved` | `PanelPaymentsController.Approve` | `Payment.ShopperId → Shopper.Phone` |
| `PaymentRejected` | `PanelPaymentsController.Reject` | aynı |
| `OrderReceived` | `LicensesSessionsSyncController` | `Order.CustomerId → WpfCustomerProjection.Phone` |
| `ShipmentStatusChanged` | `LicensesShipmentsSyncController` | `Shipment.CustomerId → WpfCustomerProjection.Phone` |
| `CustomerSentDocument` | `WhatsAppInboundJob.ProcessMessagesAsync` — `m.Type` `document` **veya** `image` (`WhatsAppInboundJob.cs:81`) | gerekmiyor (sohbet elde) |

**Olayların iki ailesi:** ilk dördü **sonuç** olayı (yayıncı bir şey yaptıktan
sonra defter tutar), sonuncusu **gelen iş** olayı ("burada bakılacak bir şey
var"). Asıl değer sonuncuda.

**Neden `document` ve `image` tek olay:** dekontu kimi PDF, kimi ekran görüntüsü
olarak yolluyor — ikisi de aynı şeyi ifade ediyor. Gelenin gerçekten dekont
olduğunu bilemeyiz; ürün fotoğrafına da yapışır. Takas bilinçli: **yanlış
etiketin bedeli bir tık, kaçırmanın bedeli kayıp para.**

**Kritik kural — etiketleme asıl işi asla bozmaz.** Etiket, asıl işlem **commit
olduktan sonra** uygulanır ve `try/catch` içine alınır; hata yalnız loglanır.
Etiket yapıştırma sırasında sorun çıkarsa ödeme onayı geri alınmaz, kargo
senkronu patlamaz. Kabul edilen takas: nadiren bir olay etiketsiz kalabilir.
Alternatif (aynı transaction) etiket hatasının ödemeyi geri almasına yol açardı
— kabul edilemez.

## Bölüm 3 — PDF dekont ayrıştırma

**Nerede:** `WhatsAppInboundJob` medyayı, mesaj satırını yazmadan **önce**
indiriyor (Meta'nın URL'i 5 dakikada ölüyor). PDF'in byte'ları o anda elimizde —
ayrı indirme/iş gerekmiyor.

**Koşul:** `Type == "document"` **ve** MIME `application/pdf`. Diğer her şey
atlanır.

**Parser:** mevcut `OrderDeck.PdfParsing/PdfDekontParser.cs` — 12 Türk bankası,
hâlihazırda shopper ödeme akışında sunucuda bağlı (`ShopperPaymentSubmissionService`,
`ParserConfidenceCalculator`). Sonuç `WaDekontExtraction`'a yazılır.

**Ayrıştırma başarısız olursa:** hiçbir şey yazılmaz, mesaj normal kaydedilir,
**etiket yine yapışır.** Olay "müşteri belge gönderdi", "geçerli dekont
gönderdi" değil. Desteklenmeyen banka veya alakasız PDF olabilir; çalışan yine
görsün, yanında özet çıkmasın.

**Yan fayda:** "gerçekten dekont mu, ürün fotoğrafı mı" sorusu PDF'ler için
kendiliğinden cevaplanır — ayrıştırıcı okuyamıyorsa dekont değildir.

**KVKK açısından yeni bir şey yok:** WhatsApp medyası zaten R2'ye indiriliyor
(`MediaR2Key`), yani dekont PDF'i bu özellik olmadan da saklanıyor. Yalnızca
ondan çıkarılan 4 alan ekleniyor.

**Yük:** PdfPig ayrıştırması yüz milisaniyeler; zaten Hangfire kuyruğunda arka
planda çalışıyor.

**`PdfHash` saklanır ama mükerrer dekont kontrolü KURULMAZ** — kullanıcı ayrıca
istemedi, kapsam kendiliğinden büyütülmüyor. Alan durduğu için sonradan
eklenebilir.

## Bölüm 4 — Panel ve API yüzeyi

İş **iki repoya yayılıyor:** sunucu uçları LiveDeck'te, ekranlar
**OrderDeck-Mobile** (`apps/panel`).

**Yeni uçlar** (hepsi çağıranın lisansına kapsanmış):

| Uç | İş |
|---|---|
| `GET/POST/PATCH/DELETE /api/panel/wa/labels` | etiket CRUD |
| `GET/PUT /api/panel/wa/label-rules` | 5 olayın etiket eşleşmesi |
| `POST/DELETE /api/panel/wa/conversations/{id}/labels/{labelId}` | elle etiket ekle / kaldır |
| `GET /api/panel/wa/conversations?labelId=` | etikete göre filtre |

Sohbet listesi cevabına eklenenler: sohbetin **etiketleri** ve varsa **dekont
özeti** (gönderen/tutar/tarih/referans).

Repoda panel controller'ları için **convention testi** var (lisans kapsamasını
doğruluyor); yeni uçlar ona uyacak.

**Panel ekranları:**

1. **Etiket yönetimi** — liste, ekle, ad/renk düzenle, sil. Silme uyarısı: bağlı
   kural ve sohbet atamaları da düşer.
2. **Ayar ekranı** — 5 sabit olay, her satırda "kendi etiketlerinden seç" açılır
   kutusu. Boş bırakılabilir (o olay etiketlenmez).
3. **Sohbet listesi** — etiket rozetleri; rozetin üstünde **×** ile tek tık
   kaldırma (etiketler birikeceği için ayrı ekrana girmeye gerek kalmamalı).
4. **Filtre** — etikete göre süzme ("Dekont geldi" listesini aç, sırayla geç).
5. **Elle etiket atama** — sohbet içinde.

**Sıralama:** sunucu uçları önce, panel sonra. Sunucu tek başına yayına girebilir;
etiketler o an yapışmaya başlar, panel geldiğinde geçmiş dolu gelir.

## Bölüm 5 — Sınırlar, testler, göç

**Bilinçli kabul edilen sınırlar** (hata değil, tasarım kararı):

- **TR dışı numara** → `PhoneNormalizer` reddediyor, sessizce atlanır.
- **Hiç WhatsApp yazmamış müşteri** → sohbet yok, etiket yapışacak yer yok.
- **Aynı telefonu paylaşan iki müşteri** (aile/ortak hat) → tek sohbet, iki
  müşterinin olayları aynı yere yapışır. Kabul edilebilir: etiket "burada
  bakılacak iş var" sinyali, kesin muhasebe kaydı değil.
- **Görsel dekont** → etiket yapışır, özet çıkmaz; insan bakar.
- **Etiketler birikir** → temizlik yayıncının işi; panelde tek tık kaldırma
  bunun için.

**Testler** (hepsi InMemory; satır düzeyinde yarış yok, Testcontainers gerekmiyor):

- `LabelRuleApplier` birim testleri — kural tanımlı değil / TR dışı numara /
  sohbet yok / etiket zaten var (mükerrer) / mutlu yol.
- **Etiketleme hatası asıl işi bozmuyor** — ödeme onayı, etiket adımı patlasa
  bile başarılı tamamlanmalı. Bölüm 2'deki kritik kuralın kanıtı.
- PDF ayrıştırma — geçerli dekont / bozuk PDF / PDF olmayan `document`
  (hepsinde mesaj kaydedilmeli, etiket yapışmalı).
- Panel uçları mevcut convention testinden geçmeli.

**Göç:** 4 yeni tablo (`WaLabel`, `WaLabelRule`, `WaConversationLabel`,
`WaDekontExtraction`). **Mevcut hiçbir tabloya kolon eklenmiyor.** Göç tamamen
eklemeli, prod'da yıkıcı değil.

**Yayın sırası:** migration → sunucu uçları → panel ekranları.

## Kapsam dışı

- **Yedek bildirimi** (ayrı spec, WPF sürümüne kilitli).
- **Görsel dekontların AI ile okunması** (ayrı faz; self-host elendi, model
  seçimi doğruluk ölçümüne bağlı — memory `reference_gorsel_dekont_ai_okuma.md`).
- **AI mesajlaşma otomasyonu** (ayrı spec — memory
  `project_ai_mesajlasma_otomasyonu.md`). Not: AI'nin "insana devret" çıkışı
  ileride bu etiket altyapısını kullanacak ("İnsan baksın" etiketi). Yani bugün
  yazılan sistem yarın AI'nin çıkış kapısı olacak.
- **Mükerrer dekont tespiti** (`PdfHash` saklanıyor, kontrol kurulmuyor).
- `reaction` emoji ile telefonda görünür iz (opsiyonel, sonra).
- WhatsApp Business App içi "liste"ye programatik atama (Cloud API'de mümkün değil).

## Yedek bildirimi spec'ine devredilen kod gerçeği

Webhook parser buton cevabını `title` ile alıyor (`WhatsAppWebhookPayload.cs:181`,
`button_reply` → `title`). İki eşzamanlı teklifte "Evet" aynı olduğundan ayırt
edilemez → yedek bildiriminde `interactive.button_reply.id` kullanılmalı,
`WhatsAppInboundMessage` record'una reply-id alanı eklenmeli. (Bu spec'in işi
değil.)

## Karıştırılmayacak dosyalar

Repoda commit'siz duran dosyalar bu işin PR'ına ASLA karışmayacak:
`.claude/launch.json`, `.gitignore`, `.codex/`, `AGENTS.md`,
`docs/proje-analiz-raporu-2026-07-16.md` ve 3 adet
`docs/superpowers/{plans,specs}/2026-07-28.../2026-08-15...` dosyası.
