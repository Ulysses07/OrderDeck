# Panelden WhatsApp şablonu oluşturma — tasarım

**Tarih:** 2026-09-01
**Durum:** onaylandı, uygulama planı bekliyor

## Sorun

Yayıncı panelden onaylı şablon *gönderebiliyor* ama *oluşturamıyor*. Yeni bir
şablon için Meta'nın WhatsApp Manager arayüzüne gitmesi gerekiyor — ayrı bir
hesap, ayrı bir dil, ayrı bir kavram seti. Şablon onaya düştükten sonra da
durumu ancak orada görebiliyor: panel yalnız `APPROVED` olanları listeliyor,
onay bekleyen ya da reddedilen şablon panelde hiç görünmüyor.

Bu, WhatsApp yüzeyinde panelden yapılamayan son iş.

## Kapsam

Panelden şablon **oluşturma, düzenleme ve silme**; ayrıca şablonların
**durumlarıyla** listelenmesi.

Bilerek kapsam dışı: dil seçici, medya başlık, değişkenli buton,
`AUTHENTICATION` kategorisi, `message_template_status_update` webhook'u,
panelden deneme mesajı gönderme.

## Meta tarafı — doğrulanmış kurallar

Aşağıdakiler 2026-09-01'de Meta dokümanından ve ikincil kaynaklardan
doğrulandı. Uygulamaya başlamadan önce tazelenmeli; Meta bu kuralları
değiştiriyor.

**İzin.** Şablon oluşturmak `whatsapp_business_management` istiyor. Aynı izin
şablon *okumak* için de gerekli ve panel prod'da onaylı şablonları
listeleyebiliyor — yani Embedded Signup'tan gelen token'da bu izin **zaten
var**. Yeni App Review ya da ES yapılandırma değişikliği gerekmiyor.

**Oluşturma.** `POST /{WABA_ID}/message_templates`. Zorunlu alanlar: `name`,
`category`, `language`, `components`. Ad en çok 512 karakter ve yalnız küçük
harf, rakam, alt çizgi. WABA başına saatte en çok 100 şablon.

**Bileşen sınırları.** Başlık metni 60 karakter; gövde 1024; altbilgi 60
(değişken kabul etmiyor); buton etiketi 25. Butonlar: toplam en çok 10, hızlı
yanıt 10, URL 2, telefon 1. Hızlı yanıt butonları bitişik durmalı. Değişken
kullanan her bileşen için **örnek değer zorunlu**.

**Silme.** Adla silmek o adın **tüm dil sürümlerini** siler; ID ile silmek tek
dili. Onaylı bir şablon silindikten sonra adı **30 gün** yeniden
kullanılamıyor. Reddedilmiş şablonda bu kural yok, ad hemen serbest kalıyor.

**Düzenleme.** Yalnız `APPROVED`, `REJECTED` ve `PAUSED` şablonlar
düzenlenebiliyor. Onaylı bir şablonun **adı, kategorisi ve dili
değiştirilemiyor**. Onaylıda 24 saatte 1, 30 günde 10 düzenleme hakkı var;
reddedilmiş ve duraklatılmışta sınır yok. Düzenlenen onaylı şablon yeniden
incelemeye düşüyor.

**Onay süresi.** Şablon `PENDING` doğuyor; dakikalar ile 24 saat arasında
`APPROVED` ya da `REJECTED` oluyor.

## Ürün kararları

### Yalnız gönderebildiğimiz şablonlar oluşturulabilir

Form; metin başlık (değişkensiz), gövde (`{{1..n}}` konumlu değişken),
altbilgi ve **sabit** butonlarla sınırlı.

**Neden:** gönderen yalnız `body` bileşeni yolluyor
(`CloudApiWhatsAppSender.SendTemplateAsync`). Medya başlık, başlıkta değişken,
değişkenli URL, `COPY_CODE` ve `AUTHENTICATION` şablonları ek bileşen
parametresi istiyor; katalog bunları zaten `UnsupportedReason` ile
"gönderilemez" işaretliyor. Formda izin verseydik yayıncı panelde oluşturup
panelde gönderemediği şablon üretirdi.

Sabit butonlar (hızlı yanıt, düz URL, telefon) parametresiz çalıştığı için
gönderene dokunmadan destekleniyor — `WhatsAppTemplateCatalog.ReadButtons`
yalnız `COPY_CODE` ve `{{ }}` içeren URL'i eliyor.

**Bağlayıcı değişmez:** formun ürettiği her taslak, Graph JSON'una çevrilip
katalog tarafından geri okunduğunda `UnsupportedReason == null` vermeli. Test
olarak yazılıyor.

### Durumlar gösteriliyor, saklanmıyor

Yeni liste ucu `PENDING`, `APPROVED`, `REJECTED` ve `PAUSED` şablonların
hepsini durumuyla döndürüyor; reddedilenin sebebi de Meta'dan geldiği gibi.

**Neden saklamıyoruz:** mevcut karar "liste saklanmıyor, her istekte Meta'ya
soruluyor" ve gerekçesi burada da geçerli — durum bizim verimiz değil, bayat
kopya yayıncıya gönderemeyeceği şablonu gönderilebilir gösterir. Yeni tablo,
göç ve senkron sorunu doğmuyor.

**Kritik sınır:** gönderim seçicisi (`whatsapp-approved-templates`) yalnız
`APPROVED` görmeye devam ediyor. Onay bekleyen şablon gönderilemez; seçicide
göstermek ücretli bir hataya davet olurdu.

### Kategoriyi yayıncı seçiyor

`MARKETING` ve `UTILITY` arasında seçim, yanında düz Türkçe açıklama ve
"Meta değiştirebilir" uyarısı. `AUTHENTICATION` formda yok.

**Neden seçtiriyoruz:** yayıncının şablonlarının bir kısmı gerçekten pazarlama
("yayın başlıyor"), bir kısmı işlemsel (kargo, ödeme). Hepsini `UTILITY`
göndermek reddedilmeye, hepsini `MARKETING` göndermek gereksiz maliyete ve
pazarlama sınırı yüzünden hiç gitmeyen mesaja yol açardı. Son kararı Meta
veriyor; seçim bir talep, garanti değil — liste onaydan sonra Meta'nın verdiği
gerçek kategoriyi gösteriyor.

### Dil sabit `tr`

Formda dil seçici yok.

**Neden:** iş Türkiye'de. Dil seçtirmek "aynı ad, farklı dil" ikizlerini
doğurur ve gönderim seçicisinde ayırt etme sorunu çıkarır. Başka dilde şablon
listede **görünür** (WhatsApp Manager'dan gelmiş olabilir); yalnız panelden
yenisi Türkçe açılır.

### Silme ID ile

**Neden:** adla silmek tüm dil sürümlerini götürüyor. Yayıncının Manager'da
açtığı İngilizce bir sürüm varsa, panelden Türkçesini silmek onu da sessizce
silerdi.

Onay diyaloğu onaylı şablonun adının 30 gün kilitleneceğini söylüyor;
reddedilmişte bu kuralın işlemediğini de. Bu, akıştaki tek geri alınamaz işlem.

### Düzenlemede ad, kategori ve dil her durumda kilitli

Meta yalnız *onaylı* şablonda bu üçünü kilitliyor; reddedilmişte ad
değiştirilebiliyor. Panel yine de üçünü her durumda kilitliyor.

**Neden:** aynı formun alan kilidi şablonun durumuna göre yer değiştirseydi
yayıncı "bunu neden bazen değiştirebiliyorum" sorusuyla kalırdı. Reddedilmiş bir
şablonun adını değiştirmek isteyen zaten yenisini açabilir — reddedilmişte ad
kilidi yok, silmesi bile gerekmiyor.

`PENDING` şablonda düzenleme düğmesi kapalı: Meta inceleme sürerken düzenlemeyi
kabul etmiyor.

### Düzenleme hakkı sayılmıyor

Kalan düzenleme hakkı (24 saatte 1, 30 günde 10) panelde gösterilmiyor.

**Neden:** Meta bu sayacı okutmuyor. Kendi tutacağımız sayı, Manager'dan
yapılan düzenlemeleri göremediği için bayatlar; yayıncıya "hakkın var" deyip
Meta'dan hata aldırmak, hiçbir şey söylememekten kötü. Sınıra takılırsa
Meta'nın hatası olduğu gibi gösteriliyor.

## Sunucu tasarımı

### Tek ayrıştırıcı, tek kayıt

Graph sorgusuna `id` ve `rejected_reason` alanları ekleniyor.

`ApprovedTemplate` kaydı `Id`, `Status` ve `RejectedReason` alanlarını kazanıp
`WabaTemplate` adını alıyor — onaylı olmayan satırları da taşıyacağı için eski
ad yanlış olurdu. Adı iki tüketici birden kullanıyor: panel ucu ve **WPF'in
ayar ekranına bakan** `LicensesWhatsAppApprovedTemplatesController`; ikisi de
düzeltilecek.

`ListApprovedAsync` aynı ayrıştırıcının üzerinde `status == APPROVED`
filtresine dönüşüyor. Gönderim seçicisinin DTO'su ve davranışı değişmiyor.

### Katalog dört yeni metot alıyor

Hepsi mevcut `GraphResult<T>` deyimiyle — istisna fırlatmıyor, hata veri.

| Metot | İş |
|---|---|
| `ListAllAsync` | durum ayrımı yapmadan hepsi |
| `CreateAsync` | taslaktan yeni şablon; id + status döner |
| `UpdateAsync` | var olan şablonu güncelle |
| `DeleteAsync` | ID ile sil |

**Doğrulama katalogda değil**, var olan `WhatsAppTemplateShape` saf sınıfında.
Katalogun kendi sözleşmesi "yalnız HTTP yapar; DB'ye dokunmaz, karar vermez";
doğrulamayı oraya koymak o cümleyi yalan yapardı. `WhatsAppTemplateShape` zaten
tam bu iş için var ve `CountBodyParams` olduğu gibi yeniden kullanılıyor.

### Uçlar

Rota `api/panel/whatsapp-message-templates`. Var olan iki addan bilerek ayrı:
`whatsapp-templates` WPF'in wa.me serbest metin kalıpları (Meta ile ilgisi yok),
`whatsapp-approved-templates` gönderim seçicisi. Yeni ad Meta'nın kendi ismi.

| Uç | İş |
|---|---|
| `GET /api/panel/whatsapp-message-templates` | hepsi, durumuyla |
| `POST /api/panel/whatsapp-message-templates` | oluştur |
| `POST /api/panel/whatsapp-message-templates/{id}` | düzenle |
| `DELETE /api/panel/whatsapp-message-templates/{id}` | sil |

Mevcut panel deseni: sınıf düzeyinde
`[Authorize(AuthenticationSchemes = "Bearer-Customer")]`, lisans
`PanelLicenseScope.ResolveAsync` ile token'dan çözülüyor, DTO'lar `sealed
record`.

**Yetki:** yeni uçlar `[AllowStockStaff]` **almıyor**. `StockStaffScopeFilter`
varsayılan olarak reddettiği için stok rolü şablon oluşturamıyor. Şablon para
harcatan bir varlık; doğru varsayılan bu.

### Doğrulama kuralları

Hepsi Graph'a çıkmadan uygulanıyor. Gerekçe mevcut kodun kendi gerekçesi:
Meta'nın hata kodları anlaşılmaz ve şablon ücretli.

- **Ad**: yayıncı serbest yazıyor ("Kargo Bildirimi"), panel slug'a çeviriyor
  (`kargo_bildirimi`) ve **çevrilmiş hâli gösteriyor**. Türkçe karakterler
  ASCII'ye iniyor. Gizli dönüşüm yok — yayıncı ne kaydedildiğini görüyor.
  En çok 512 karakter.
- **Gövde**: zorunlu, en çok 1024 karakter. Değişkenler `{{1}}`'den başlayarak
  bitişik; isimli değişken (`{{ad}}`) yasak. `CountBodyParams` bunu ölçüyor.
- **Örnek değer**: her gövde değişkeni için zorunlu. Meta örneksiz şablonu
  reddediyor. `example.body_text` olarak gidiyor, listede yer tutucu olarak geri
  okunuyor.
- **Başlık**: opsiyonel, düz metin, en çok 60 karakter, değişken yok. Meta 1
  değişkene izin veriyor ama gönderen başlık parametresi yollamıyor.
- **Altbilgi**: opsiyonel, en çok 60 karakter, değişken yok.
- **Butonlar**: opsiyonel. Etiket en çok 25 karakter. En çok 10 toplam, 10
  hızlı yanıt, 2 URL, 1 telefon. URL sabit — `{{ }}` içeremez. `COPY_CODE` yok.
  Form hızlı yanıtları ayrı listede tuttuğu için Meta'nın gruplama kuralı
  kendiliğinden sağlanıyor.
- **Kategori**: `MARKETING` ya da `UTILITY`.

## Panel tasarımı

**Ekran** `/whatsapp-mesaj-sablonlari`, "Daha Fazla → İletişim" altında, menüde
"WhatsApp Mesaj Şablonları" adıyla. Panelde **zaten** `/whatsapp-sablonlari`
var ve menüde "WhatsApp Şablonları" yazıyor — o ekran WPF'in wa.me metin
kalıplarının önizlemesi. Yeni ekranı tek harf farkla adlandırmak, iki şablon
kavramını panelde birbirine karıştırmanın en kısa yolu olurdu; ad Meta'nın
kendi terimini ("message template") taşıyor. Liste her
şablonu durum rozetiyle gösteriyor; reddedilenin sebebi Meta'dan geldiği gibi
yazılıyor. Çevirmeye çalışmak, tanımadığımız bir sebebi sessizce yutmak olurdu.

**Form**: ad, kategori, başlık, gövde, altbilgi, butonlar ve her değişken için
örnek değer. Yanında canlı önizleme — `WhatsAppTemplateSender`'daki deyimin
aynısı. Yayıncı ücretli mesajın nasıl görüneceğini yazarken görüyor.

**Düzenlemede** ad, kategori ve dil alanları kilitli, sebebi yanlarında yazılı.
`PENDING` şablonda düzenleme düğmesi kapalı.

**Silmede** onay diyaloğu 30 günlük ad kilidini söylüyor.

**Hata yönetimi** iki katmanlı: kendi doğrulamamız Türkçe ve alan bazında,
Meta'nın reddi olduğu gibi.

## Test

- **Sunucu birim**: `WhatsAppTemplateCatalogTests`'teki `StubHandler` ile
  create / update / delete. Giden JSON gövdesi de doğrulanıyor — Meta'ya ne
  yolladığımız sözleşmenin kendisi.
- **Sunucu uçtan**: `ApiFactory` ile yetki, lisans kapsamı, stok rolünün 403
  alması, her doğrulama kuralının 400 vermesi.
- **Değişmez testi**: taslak → Graph JSON'u → `ReadTemplate` →
  `UnsupportedReason == null`.
- **Panel**: slug dönüşümü, alan doğrulamaları, durum rozetleri, silme onayı,
  düzenlemede kilitli alanlar.

## Riskler

**Meta kuralları değişiyor.** Yukarıdaki sınırlar 2026-09-01 tarihli. Uygulama
başlarken tazelenmeli.

**Kategori kararı bizde değil.** Meta yayıncının seçtiği kategoriyi
değiştirebiliyor. Panel onaydan sonra gerçek kategoriyi gösterdiği için yanlış
bilgi kalıcı olmuyor, ama yayıncıya bunun bir talep olduğu formda yazılmalı.

**Silme geri alınamaz.** 30 günlük ad kilidi yayıncıyı şaşırtabilecek tek
davranış; onay diyaloğunun metni bu yüzden açık olmalı.
