# WhatsApp Etiket Altyapısı — Tasarım (TASLAK / brainstorming devam ediyor)

> **Durum:** Bu belge brainstorming sırasında yazıldı, **henüz onaylanmış bir
> spec DEĞİL.** Session'lar arası devir için var. Yeni session bu dosyayı okuyup
> kaldığı yerden (aşağıdaki "Açık soru") devam edebilir. Onaylanınca aynı yola
> `-design.md` olarak temize çekilecek, sonra `writing-plans`'e geçilecek.
>
> **Tarih:** 2026-08-18 · **Repo:** LiveDeck (sunucu) + OrderDeck-Mobile (panel ekranları)

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

## Kilitlenen kararlar (kullanıcı onayladı)

- **Etiketler tamamen dinamik ve yayıncıya ait** — biz hiçbir etiket
  tanımlamıyoruz. Yayıncı panelde istediği kadar açar ("Ödeme geldi",
  "Kargolandı", "VIP", ne isterse). Tablo boş başlar.
- **Otomatik kural, dinamik etiketi SABİT olaya bağlar.** Olaylar dinamik olamaz
  (sunucu ancak bildiği anları tetikler); ama her olay yayıncının kendi açtığı
  herhangi bir etikete bağlanabilir. Ayar ekranı: her olay satırında "kendi
  etiketlerinden seç" açılır kutusu.
- **Telefonla iş çıkmayanlar sessizce atlanır** (hata değil): TR dışı numara
  (`PhoneNormalizer` reddediyor) ve hiç WhatsApp yazmamış müşteri (ortada sohbet
  yok).
- **Cloud API'de etiket API'si YOK** (Meta'nın 4 resmi yüzeyinden doğrulandı —
  bkz. `reference_whatsapp_cloud_api_limitleri.md`). Kategorileme yalnız bizim
  panelimizde. Telefonda görünür iz istenirse tek yol `reaction` emoji; bu
  belgenin kapsamı dışında, opsiyonel.

## Veri modeli (öneri)

- **`WaLabel`** — `Id, LicenseId, Name, Color, CreatedAt`. Dinamik, yayıncıya ait.
  Panelde CRUD, sohbete elle atama/kaldırma.
- **`WaLabelRule`** — `LicenseId, EventKey (enum), WaLabelId`. Sabit olayı seçilen
  etikete bağlar. Etiket silinirse ona bağlı kural düşer.
- **`WaConversationLabel`** — sohbet↔etiket bağlantısı. **AÇIK SORU (aşağıda):**
  many-to-many (çoklu) mu, yoksa `WaConversation`'da tek `WaLabelId` alanı (tek) mi.

## Sunucunun gerçekten bildiği, telefona bağlanabilen olaylar (koddan doğrulandı)

| Olay (EventKey) | Tetiklendiği yer | Telefon nereden |
|---|---|---|
| Ödeme onaylandı | `PanelPaymentsController.Approve` | `Payment.ShopperId → Shopper.Phone` |
| Ödeme reddedildi | `PanelPaymentsController.Reject` | aynı |
| Sipariş geldi (WPF sync) | `LicensesSessionsSyncController` | `Order.CustomerId → WpfCustomerProjection.Phone` |
| Kargo durumu değişti | `LicensesShipmentsSyncController` | `Shipment.CustomerId → WpfCustomerProjection.Phone` |
| Yedek: Evet / Hayır / cevapsız | (Faz 2 yedek bildirimi — ikinci spec) | `WaConversation.CustomerPhone` |

**Eşleştirme telefon üzerinden:** olayın telefonu ↔ `WaConversation.CustomerPhone`,
her iki taraf `PhoneNormalizer` (`Services/Auth/PhoneNormalizer.cs`) ile
`+90XXXXXXXXXX`'e normalize. WhatsApp'ın `905443579314` biçimi de doğru
normalleşiyor. **`WpfCustomerId` bağlantısına GEREK YOK** — etiket doğrudan
telefonla kurulur (alt-ajanın "eşleşmemiş sohbet etiketlenemez" iddiası YANLIŞ,
düzeltildi).

**İki gerçek sınır:** (1) `PhoneNormalizer` TR dışı numarayı reddeder → yabancı
numaralı sohbet etiketlenemez; (2) müşteri hiç WhatsApp yazmamışsa sohbet yoktur.
İkisi de sessizce atlanır.

## Panel tarafı (OrderDeck-Mobile reposu)

Etiket yönetimi (CRUD), sohbet listesinde etikete göre filtre ve "olay → etiket"
ayar ekranı **OrderDeck-Mobile**'da (`apps/panel`). Yani iş iki repoya yayılıyor.

## Faz 2 için ayrıca not edilen kod gerçeği (yedek bildirimi spec'ine)

Webhook parser buton cevabını `title` ile alıyor
(`WhatsAppWebhookPayload.cs:181`, `button_reply` → `title`). İki eşzamanlı
teklifte "Evet" aynı olduğundan ayırt edilemez → yedek bildiriminde
`interactive.button_reply.id` kullanılmalı. `WhatsAppInboundMessage` record'una
reply-id alanı eklenecek. (Bu, etiket altyapısının değil yedek spec'inin işi.)

## AÇIK SORU (brainstorming burada duruyor)

**Bir sohbete aynı anda birden çok etiket yapışabilsin mi?**

- **a) Çoklu (ÖNERİM)** — `WaConversationLabel` ara tablosu (many-to-many).
  Olaylar zaman içinde birikir (önce ödeme, sonra kargo); tek alan olsaydı her
  yeni olay öncekinin izini silerdi, "ödemiş ama kargolanmamış" filtresi
  imkânsızlaşırdı. Maliyeti bir ara tablo.
- **b) Tek** — `WaConversation`'a tek `WaLabelId`. Basit ama yeni olay eskisini
  ezer.

## Sıradaki adımlar (brainstorming akışı)

1. Açık soruyu netleştir (çoklu/tek).
2. 2-3 yaklaşım sun (özellikle "olay olunca etiketi kim/nerede yapıştırıyor" —
   controller içi doğrudan mı, ortak bir `LabelRuleApplier` servisi mi, olay
   yayını mı).
3. Tasarımı bölüm bölüm sun, onay al.
4. Bu belgeyi temize çek + commit.
5. Spec öz-denetimi → kullanıcı incelemesi → `writing-plans`.

## Kapsam dışı

- Yedek bildirimi (ayrı spec, WPF sürümüne kilitli).
- `reaction` emoji ile telefonda görünür iz (opsiyonel, sonra).
- WhatsApp Business App içi "liste"ye programatik atama (Cloud API'de mümkün değil).

## Karıştırılmayacak dosyalar

Repoda commit'siz duran 8 dosya bu işin PR'ına ASLA karışmayacak:
`.claude/launch.json`, `.gitignore`, `.codex/`, `AGENTS.md`,
`docs/proje-analiz-raporu-2026-07-16.md` ve 3 adet
`docs/superpowers/{plans,specs}/2026-07-28.../2026-08-15...` dosyası.
