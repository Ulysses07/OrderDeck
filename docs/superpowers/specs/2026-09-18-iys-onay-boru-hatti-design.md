# İYS onay boru hattı (ileri-yönlü) — tasarım

Tarih: 2026-09-18
Durum: onaylandı (bölüm bölüm), uygulama planı bekliyor

## Problem

Bugün toplanan SMS onayları İYS'ye **hiç iletilmiyor**. Form onayı yalnız
kendi veritabanımıza yazılıyor (`IntakeForm.cshtml.cs:477` →
`IntakeFormService.SaveSubmissionAsync`); kodda `/iys/add` çağrısı yok.
İYS'ye dokunan tek şey gönderim anındaki `iysfilter=11`
(`NetgsmSmsSender.cs:37`) — bu bir kayıt değil, Netgsm'in ret listesini
elemesi.

Sonucu ölçtük: 2026-09-17'de 294 mevcut onay gerçek tarihleriyle İYS'ye
gönderildi, **yalnız 10'u ONAY oldu, 284'ü RET**. Kök neden Yönetmelik
m.7/11-12 — İYS dışında alınan onay **üç iş günü** içinde kaydedilmezse
geçersiz. Kesme noktası ölçümde birebir doğrulandı: ONAY olan 10 kaydın
7'si son 3 gün içindeydi, RET olanların en yenisi 2026-09-13.

Yani sorun geçmişte değil, **şu anda da sürüyor**: bugün formu dolduran
müşterinin onayı da üç gün içinde sessizce ölüyor.

Bu tasarım o boru hattını kurar. Geçmiş kayıtların kurtarılması kapsam
dışıdır — o kapı mevzuat gereği kapalı.

## Kapsam

**Dahil:**
- Onay/ret olaylarının İYS'ye iletilmesi ve sonucunun doğrulanması
- İYS durumunun yerel aynası + ispat kaydı
- Gönderim anında onay kontrolü (fail-closed)
- TR telefon normalizasyonundaki hata (önkoşul)

**Hariç:**
- Kampanya kitlesinin genişletilmesi (`ConsentedRecipients` bugün yalnız
  `Shoppers`'tan türüyor; 298 form onayı kampanyalara görünmüyor). Ayrı spec.
- Geçmiş onayların toplu yüklenmesi — hukuken mümkün değil.
- WhatsApp üzerinden erişim (İYS kapsamı dışı, ayrı konu).

## Kararlar

| Konu | Karar |
|---|---|
| Kaynaklar | Intake form + Shopper kaydı + Shopper profili, üçü de |
| Yön | ONAY **ve** RET itilir |
| Tetikleme | Kayıtla aynı anda kuyruğa + periyodik güvenlik ağı + son tarih uyarısı |
| Yetkili kaynak | İYS aynası, **fail-closed** |
| Saklama | Durum tablosu + ekle-only olay tablosu, birlikte |
| Geçmiş | Backfill yok |

## Veri modeli

### `IysConsent` — durum

"Şu an bu numaraya ne yapabilirim?" sorusunun tek cevabı. Gönderim
yolunda okunur, tek satır.

| Alan | Açıklama |
|---|---|
| `Id` | Guid, yüzey anahtarı |
| `BrandCode`, `ChannelType`, `RecipientType`, `Recipient` | **tekil index** — İYS'nin kendi anahtarı |
| `Status` | `Onay` / `Ret` / `Unknown` — **bizim beyanımız** |
| `ConsentDate` | İYS'ye beyan edilen tarih (TR yerel) |
| `SourceCode` | `HS_WEB` vb. |
| `PushState` | `Pending` / `Pushed` / `Confirmed` / `Failed` / `Expired` |
| `PushDeadline` | onay anı + 3 iş günü |
| `LastVerifiedStatus`, `LastVerifiedAt` | **İYS'nin cevabı** (`/iys/search`) |
| `LastLocalEventAt` | en güncel yerel olayın zamanı |

`Status` ile `LastVerifiedStatus`'un **ayrı kolonlar** olması bu tasarımın
merkezi. 2026-09-17'de 284 kaydı kaybetmemizin sebebi ikisini bir sanmaktı:
Netgsm'in `code 0` yanıtı "kuyruğa alındı" demek, "kabul edildi" değil.

`Recipient` daima E.164 (`+905XXXXXXXXX`).

### `IysConsentEvent` — olay, ekle-only

Hiç silinmez, hiç güncellenmez.

| Alan | Açıklama |
|---|---|
| `Recipient`, `OccurredAt` | |
| `EventType` | `LocalConsent` / `LocalRevoke` / `PushAttempt` / `SearchResult` |
| `Status` | olayın taşıdığı durum |
| `SourceTable`, `SourceId` | hangi `IntakeFormSubmission` / `Shopper` satırı |
| `ProofIp`, `ProofUserAgent` | onay ispatı |
| `ApiResponseCode`, `ApiResponseBody`, `ErrorCode` | ham yanıt (kırpılmış) |

İspat bu tabloda yaşamak zorunda: `/iys/search` bize `consentDate` ve
`source` alanlarını **boş** döndürüyor (2026-09-17'de ölçüldü). Denetimde
"bu kişi izni ne zaman, nereden, hangi IP'den verdi" sorusunun cevabı
İYS'den geri okunamaz.

## Telefon normalizasyonu (önkoşul)

`PhoneNormalizer` iki yerde ayrı yazılmış ve ikisi de aynı deliği taşıyor:

- `OrderDeck.Core/Customers/PhoneNormalizer.cs:30-31` — 10 haneyi sorgusuz
  `+90` ile önekliyor
- `OrderDeck.LicenseServer/Services/Auth/PhoneNormalizer.cs:51` —
  `cleaned.Length != 10` dışında kural yok

Eksik kural: TR mobil abone numarası **`5` ile başlar**. `0533466482`
(9 hane + baştaki 0) bugün 10 hane sayılıp `+900533466482` üretiyor.
Prod'da en az bir böyle kayıt var.

**Değişiklik:** normalize sonrası ilk hane `5` değilse `null`/`false`.
Mevcut hata yolu kullanılır, yeni yol açılmaz. İki normalizer aynı test
kümesiyle doğrulanır — biri değişip diğeri kalmasın.

Bu iş A'nın *önkoşuludur*, yanında duran bir iş değil: bozuk numara
`IysConsent` tablosunda yanlış anahtar üretir.

Mevcut bozuk kayıtlar İYS'ye itilmez; `Unknown` + `Failed` işaretlenip
admin listesine düşer. **Sessizce atlanmaz** — sessiz atlama, 284 kaydı
fark etmeden kaybetme biçimimizdi.

## Boru hattı

1. **Olay yakalama.** Kaynak onay/ret ürettiğinde `IysConsentEvent` yazılır
   ve `IysConsent` güncellenir — kaynak işlemle **aynı transaction** içinde.
   Onay girip olay kaydı girmezse o kayıt görünmez olur.
2. **Kuyruk.** Commit sonrası Hangfire işi (`IysConsentPushJob`) kuyruğa
   alınır, saniyeler içinde çalışır.
3. **Push.** `/iys/add`. Netgsm 10 istek/dk sınırlı → bekleyenler 20'şerli
   partilenir, partiler arası beklenir. Ham yanıt olay tablosuna yazılır,
   `PushState = Pushed`.
4. **Doğrulama — ayrı adım.** `/iys/search` çağrılır, dönen cevap
   `LastVerifiedStatus`/`LastVerifiedAt`'e yazılır. Cevap beyanla eşleşiyorsa
   (ONAY→ONAY, RET→RET) `Confirmed`; Confirmed bir RET gönderim izni değildir
   (2026-09-22: yalnız-ONAY kuralı RET beyanını hiç kabul edemiyordu).
   İlk deneme push'tan ~15 dk sonra (İYS işleme anlık değil; hemen sormak
   henüz işlenmemiş kaydı "RET" sanmaya yol açar), sonra artan aralıklarla
   (1 saat, 6 saat, 24 saat) `PushDeadline`'a kadar.
5. **Güvenlik ağı.** Periyodik `IysConsentRecoveryJob`: `Pending` kalmış,
   `Pushed` ama doğrulanmamış, `Failed` kayıtları toplar. Mevcut
   `SmsCampaignRecoveryJob` deseni.
6. **Son tarih uyarısı.** `PushDeadline`'a <24 saat kalmış ve `Confirmed`
   olmayan kayıt varsa admin uyarısı. Sessiz kalmak yasak.

Yeni ayar: `NETGSM_BRANDCODE=731734` — `.env` + `docker-compose.yml`
eşlemesi. Hardcode yok; marka ileride ORDERDECK olacak.

## Durum geçiş kuralları

1. **RET kendiliğinden ONAY'a yükselmez.** `Status` yalnızca
   `LastLocalEventAt`'ten **daha yeni** açık bir onay olayıyla değişir.
   Profilden ret veren biri ertesi gün form doldurursa yükselir; eski bir
   kayıt geç işlenirse yükselmez. ("Collector yalnız ONAY'a yükseltir"
   kuralı bu yüzden reddedildi — `ShopperMeController.cs:126` açık ret yazıyor.)
2. **İYS'nin RET'i yerel ONAY'ı ezmez ama gönderimi keser.**
   `Status = Onay` + `LastVerifiedStatus = Ret` mümkündür ve mesaj gitmez.
   Kişinin bize verdiği onay ispat olarak durur; gönderim İYS'ye uyar.
   (Ölçümde görüldü: kişinin başka kanaldan daha yeni reddi olabiliyor.)
3. **"Kayıt yok" ile "reddetti" ayırt edilmez.** `/iys/search` ikisine de
   `RET` diyor; `transactionId` ayırt edici değil — toplu sorguda 20 satırın
   hepsinde aynı değer döndü, o alan sorgu kimliği. Fail-closed:
   `LastVerifiedStatus == Onay` değilse gönderim yok.
4. **Yerel ret push'u beklemez.** Onay geri çekilirse gönderim **anında**
   durur. İYS'ye RET ayrıca itilir (yasal kayıt), ama gönderim beklemez.
5. **Marka bazında ayrı.** Aynı numara 731734'te ONAY, 763208'de RET
   olabilir (ölçüldü). Sorgu ve gönderim aynı markayı kullanır.
6. **Backfill yok.** Mevcut 284 RET için otomatik yeniden deneme yapılmaz.
   Ancak kişi yeniden onay verirse geri gelirler.
7. **Gönderim anında yeniden kontrol.** `SmsCampaignSendJob.cs:92` bugün
   alıcıyı kampanya kurulurken snapshot'lıyor. Gönderim anında `IysConsent`
   tekrar okunur; arada ret vermiş biri mesaj almaz.

`Expired` kalıcı yasak değildir — yeni bir onay olayı yeni pencere açar.

## Hata yönetimi

| Sınıf | Örnek | Davranış |
|---|---|---|
| Geçici | ağ, zaman aşımı, oran sınırı | `Failed`, recovery yeniden dener (deadline içinde) |
| Kalıcı-veri | `H467` (tarih çok eski), geçersiz numara | `Expired`/`Failed`, **yeniden denenmez**, admin listesi |
| Kalıcı-yapılandırma | `code 60` marka kodu, `code 30` kimlik | **boru hattı durur** + yüksek öncelikli uyarı |

Yapılandırma hatasında sessizce devam etmek her kaydı sırayla harcar.

**Admin görünürlüğü:** bekleyen / doğrulanmamış / başarısız / süresi dolmuş
sayıları + son tarihi yaklaşanlar. Bu sayfa olmadan boru hattı bozulduğunda
yine fark etmeyiz — fark etmemek bu işin karakteristik başarısızlık biçimi.

**Loglama:** telefon `PiiMasker.MaskPhone` ile maskeli. Ham API yanıtı olay
tablosuna yazılır, log'a değil.

## Test

- **Normalizer:** `+900533466482` üreten girdi reddedilir; iki normalizer
  aynı test kümesiyle doğrulanır.
- **Durum geçişleri:** 7 kuralın her biri ayrı test. Özellikle "RET
  diriltilmez" ve "İYS RET'i gönderimi keser".
- **Push işi:** sahte Netgsm ile — **`code 0` dönmesinin `Confirmed`
  yapmadığı** testle sabitlenir. Test adı bunu açıkça söylemeli; geçen
  seferki hatayı koda gömülü tutan şey bu olacak.
- **Gönderim kontrolü:** kampanya kurulduktan sonra ret veren alıcıya mesaj
  gitmediği.
- **Eşzamanlılık:** aynı telefona iki eşzamanlı olayda tekil index tutar —
  InMemory'de kanıtlanamaz, **Testcontainers** gerekir
  (`SqlServerContainerFixture`).

## Kaynaklar

- Ticari İletişim Yönetmeliği m.7/11-12 — İYS dışı onay 3 iş günü
- İYS API `H467` — `consent_date` 3 günden eski olamaz
- 2026-09-17 ölçümü: 294 gönderim → 10 ONAY / 284 RET
- `/iys/search` davranışı: kayıt yokken de `code 0` + `RET`;
  `consentDate`/`source` boş döner
