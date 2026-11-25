# Düğme hareketleri ve "Sohbeti yenile" — tasarım

Tarih: 2026-11-24. Yön onayı: Burak, aynı gün (sade deneme penceresi üzerinden, "Evet, bu yön").
Yaklaşım onayı: aynı gün ("Evet, böyle"). Bağımsız bir inşaatçı denetiminden geçti (24.11);
bulguları işlendi.

**İlerleme onayı: Burak, 24.11.2026.** Belge uzun bulundu ("bana ne soruyorsun anlamadım"); bunun
üzerine Burak'a yalnız açık kalan tek seçim, Yenile düğmesinin yeri, gerçek çizimle soruldu ve
"gerisini konuştuğumuz gibi yazdım, okuman şart değil, seçimini alınca yapıma geçerim" denildi.
Cevabı: "2, altta" (düğme şeritte). §7'deki maddeler Burak'ın tek tek onayladığı şeyler değil, benim
kararlarımdır; itiraz gelirse değişir.

Kapsam yalnız WPF istemcisi (`OrderDeck.App`, `OrderDeck.Chat`). Sunucu, yerel şema ve tarayıcı
uzantısı değişmez; göç yok.

İki iş, iki PR, tek sürüm. Önce İş 1 (düğmeler), sonra İş 2 (Sohbeti yenile): İş 2'nin düğmesi
İş 1'in getirdiği meşgul durumunu kullanır.

## 1. Amaç

**İş 2, asıl dert.** Yayında bir platformun sohbeti düştüğünde ya da geç geldiğinde bugün tek çare
uygulamada yayını bitirip yeniden başlatmak. Bu, tek yayının satışlarını iki oturuma ve iki rapora
böler. İstenen: yayına ve satışlara dokunmadan sohbet bağlantılarını yenileyen bir düğme.

**İş 1.** O düğme ve uygulamadaki öbür düğmeler basıldığını, çalıştığını ve bittiğini sade bir
hareketle söylesin. Bugün düğmelerde basılı durum, geçiş ve meşgul durumu yok.

## 2. Söylenenler ve öneriler

### 2.1 Burak'ın söyledikleri (kesin)

- İstek: "bazen yayın düşüyor bir platformda o zaman wpf'de de yayını bitirip tekrar açmam gerekiyor
  bazen chatin gelmesi uzun sürüyor onun içinde bir buton falan koysak chat bağlantısını yenile gibi
  bir buton yayını bitirmek zorunda kalmasam." Düğme görünümü için önce rareui.com gibi siteler
  araştırılsın, beğenilirse tüm düğmeler değişsin.
- Araştırma panolarından sonra: aradığı durağan görünüm değil, "Hareketleri beğendim".
- İlk çalışan deneme (gradyan, ışıma, parıltı, sıçrayan yaylar): "genel olarak hem iyi hem kötü,
  animasyon ve renk seçimlerin çok ai slop duruyor". Reddedildi.
- İkinci, sade deneme: "Evet, bu yön".
- Yaklaşımlar ("Evet, böyle"):
  - İş 1: mevcut stiller yerinde güncellenir (basılı durum, 150 ms'lik geçiş, meşgul durumu);
    ekranlara tek tek dokunulmaz. Üzerine gelme ve basma tonları renge değil yarı saydam örtüye
    bağlanır (yayın düğmesi boştayken yeşil, yayındayken kırmızı).
  - İş 2: düğme + erken bağlanma (YouTube ve Instagram yayın başlar başlamaz bağlansın).
  - Düğme sohbet başlığında, arama kutusunun sağında bir ikon düğmesi. Basınca döner, bitince onay
    işareti olur. Platformların durumu başlığın altındaki şeritte; şerit dört platforma genişler.
  - Önce İş 1, sonra İş 2; ikisi aynı sürümde çıkar.

### 2.2 Varsayım (itiraz gelmedi)

En çok düşen platform YouTube: kendiliğinden toparlaması en zayıf olan o (§5.1). Yanlışsa saha
denemesinin sırası değişir, tasarım değişmez.

### 2.3 Başarısızlık sayılacaklar

Teknik olarak çalışsa bile şunlardan biri olursa iş başarısızdır:

- Yenileme sohbete aynı mesajı ikinci kez düşürür ya da aradaki mesajı kaybettirir.
- Yenileme yayın oturumuna, kuyruğa ya da satışlara dokunur.
- Düğmeler süslü görünür (gradyan, ışıma, parıltı, sıçrama): reddedilen ilk denemenin dönüşü.
- Düğmelerin durağan görünümü fark edilir biçimde değişir.
- Şerit, sohbet satırlarına ayrılan yerden gereğinden çok yer.
- Düğme "her şeyi düzeltir" izlenimi verir; yapamadığı şey (§5.2) ekranda söylenmez.

## 3. Sadelik kuralları (iki iş için de bağlayıcı)

İkinci denemede onaylanan yön, ürüne şu kurallarla taşınır:

- **Süre ve eğri.** Düğme geçişlerinin hepsi `OD.Dur.Fast` (150 ms), eğri `OD.Ease.Out`. Yay ve
  sıçrama (`OD.Ease.Spring`) düğmelerde kullanılmaz. Yeni süre ya da eğri belirteci eklenmez.
- **Ne canlanır.** Yalnız saydamlık ve dönüşüm (ölçek, dönme). Genişlik, yükseklik ve boşluk
  canlanmaz; görünüp kaybolan her şeyin yeri önceden ayrılıdır, yerleşim oynamaz.
- **Süs yok.** Gradyan, renkli ışıma, parıltı, gölge efekti (`Effect`) yok. Derinlik 1 px çerçeve ve
  zeminin bir ton açığı ya da koyusuyla verilir.
- **Yeni renk yok.** `Colors.xaml`'e fırça eklenmez; tonlar mevcut fırçaların saydam örtüleriyle
  elde edilir.
- **Tek vurgu.** `Accent` yalnız birincil eylemde. Durum renkleri (yeşil, sarı) yalnız 8 px'lik
  noktalarda; yeni geri bildirimler (meşgul, bitti) düğmeyi yeşile ya da sarıya boyamaz. Yayın
  düğmesinin bugünkü yeşil ve kırmızı zemini aynen kalır.
- **Döngü.** Sonsuz dönen tek şey, iş sürerken görünen halka.
- **rareui'den kod alınmaz.** Lisansı bileşenlerin başka çatıya taşınmış hâlini dağıtmayı
  yasaklıyor, depo herkese açık. Alınan yalnız fikir: bileşen başına tek anlamlı hareket.

Bu kurallar 2026-08-07 arayüz yenileme tasarımının iki satırını düğmeler için geçersiz kılar: §4'teki
"düğme, çip, geri bildirim → `EaseSpring`" eşlemesi ve referans mockup'taki üzerine gelince 1 px
yukarı kalkma. `OD.Ease.Spring` belirteci yerinde kalır (testle sabitli), düğmeler onu kullanmaz.

## 4. İş 1: Düğmelerin hareketi

### 4.1 Bugün

Uygulamadaki yaklaşık 150 düğme beş stilden beslenir: `OD.Button.Primary` (41 kullanım),
`OD.Button.Secondary` (37), `OD.Button.Ghost` (64), `OD.Chip` (5; sekme ve seçenek olarak kullanılan
`RadioButton` stilleri de bundan türer) hepsi `Themes/Controls.xaml`'de, ve kenar çubuğundaki
`NavButton` (6, `Views/Shell/ShellSidebar.xaml`). Türeyen stiller (`OD.ShortcutCapture`, kapı
ekranlarındaki hayalet türevleri) şablonu miras alır.

- Hiçbir düğme şablonunda animasyon yok; üzerine gelince renk anında değişir. `OD.Dur.Fast` hiçbir
  yerde tüketilmiyor.
- Basılı durum yok: basılan düğme üzerine gelinmiş düğmeyle aynı görünür.
- Meşgul durumu yok: uzun süren işte düğme yalnız soluklaşır.
- Birincil düğmede üzerine gelme, zemini bilmeden aydınlatan yarı saydam beyaz bir örtüyle yapılır;
  çünkü yayın düğmesi aynı stili boştayken yeşil, yayındayken kırmızı zeminle kullanır ve şablon
  içindeki sabit renk dışarıdan ezilemez. Aynı düğme üzerine gelince 1 px yukarı kalkar.

### 4.2 Değişen

Durağan görünüm hiçbir stilde değişmez. Değişen, durumlar arası geçiş ve yeni basılı durumdur.

| Stil | Üzerine gelince | Basınca |
|---|---|---|
| Birincil | Beyaz örtü (bugünkü %14) 150 ms'de belirir. 1 px kalkma kaldırılır. | Koyu örtü + %98'e küçülme |
| İkincil | `Surface2` dolgu (bugünkü görünüm) 150 ms'de belirir. | Dolgunun bir ton açığı + %98'e küçülme |
| Hayalet | `Surface2` dolgu 150 ms'de belirir; yazı `Text` rengine döner (bugünkü gibi, anlık). | Dolgunun bir ton açığı; küçülme yok |
| Çip | Yazı `Text` rengine döner (bugünkü gibi). | Dolgunun bir ton açığı; küçülme yok |
| Kenar çubuğu öğesi | `Surface2` dolgu 150 ms'de belirir. | Dolgunun bir ton açığı; küçülme yok |

- **Basılı ton.** Birincilde koyu örtü: `OD.Brush.Bg` renginde yarı saydam katman, başlangıç 0,12
  (kırmızıyı `AccentDeep`'e, yeşili koyu yeşile yaklaştırır). Öbür dört stilde açık örtü: üzerine
  gelme dolgusunun üstüne beyaz (`OD.Brush.OnAccent`) yarı saydam katman, başlangıç 0,06. Koyu
  zeminli stillerde koyulaştırma kullanılmaz: `Bg` neredeyse siyah, `Surface` ile `Surface2` arası
  tek basamak; koyu örtü basılı hâli durağan hâle eşitliyor (bağımsız denetimde hesaplandı).
- **Ölçüt.** Kesin değerler durum panosunda (§4.6) ayarlanır: durağan, üzerinde ve basılı hâller
  `Bg` ve `Surface` zeminlerinde birbirinden ayırt edilebilmeli. `Surface2` zeminde üzerine gelme
  bugünkü gibi görünmez kalır (dolgu zeminle aynı renk; bilinen sınır), basılı hâl orada da görünür.
- **Küçülme** yalnız kutu biçimli iki eylem düğmesindedir. Hayalet, çip ve kenar çubuğu öğesi çoğu
  yerde tam genişlikte satır ya da sekme olarak durur; onlarda küçülme satırı oynatır, bu yüzden
  yalnız ton değişir.
- Renk geçişleri fırçanın rengi canlandırılarak değil, örtü katmanlarının saydamlığıyla yapılır:
  tema fırçaları dondurulmuştur ve birincil düğmenin zemini dışarıdan bağlanır.
- Yazı rengi (hayalet ve çipte) bugünkü gibi anlık değişir; 20 kadar kullanım yeri yazı rengini
  kendisi verdiği için canlandırılmaz.
- Devre dışı görünümü değişmez: birincil, ikincil ve hayalette %45 saydamlık ve ok imleci. Çip ve
  kenar çubuğu öğesinde bugün devre dışı görünümü yok; eklenmez.
- Çipin seçili hâli (vurgu renkli çerçeve ve yazı) değişmez.

### 4.3 Meşgul durumu

Birincil, ikincil ve hayalet stillerin tanıdığı, düğmeye iliştirilen bir "meşgul" bayrağı.

- Bayrak açıkken içerik görünmez olur ama yerini korur: düğmenin ölçüsü ve `Content`'i değişmez.
  İçeriğin hizasında (birincil ve ikincilde ortada, hayalette içerik neredeyse orada) 16 px
  (`OD.Icon.Md`) çaplı, 2 px çizgili, düğmenin yazı renginde bir halka belirir ve döner. Bir tur
  `OD.Dur.Slow` (850 ms), doğrusal.
- Meşgul düğme fare tıklaması almaz, imleci oktur ve solmaz: komutu o sırada "çalıştırılamaz"
  bildirdiği için devre dışı kalsa bile devre dışı görünümü meşgulken uygulanmaz. Klavyeyle yeniden
  tetiklenmeyi kesmek tüketen komutun işidir (meşgulken çalışmaz).
- İçerik ile halka geçişsiz yer değiştirir. Meşgul durumu şablonun kendi tetikleyicisidir: bayrak
  şablon uygulanmadan önce de sonra da verilse sonuç aynıdır. Yumuşak geçiş istenirse sonradan
  eklenir; önce sağlamlık.
- "Bitti" işareti stilin işi değildir; gereken düğme kendi içeriğini değiştirir (§5.5).
- İlk tüketici İş 2'nin Yenile düğmesidir. İş 1 mevcut düğmeleri meşgul bayrağına bağlamaz. Ağustos
  tasarımının "stil, tüketildiği fazda yazılır" kuralı burada bir PR ara ile sağlanır; ikisi aynı
  sürümdedir.

### 4.4 Hareket azaltma

Windows'ta "pencere içi animasyonlar" kapalıysa (`SystemParameters.ClientAreaAnimation` yanlış)
geçişler anında olur, halka dönmeden durur. Karar tek yerde verilir ve testte değiştirilebilir.
Uygulamada bugün bu kuralı yalnız çekmece animasyonu gözetiyor.

### 4.5 Değişmeyenler

- Renk, ölçü, köşe yarıçapı, yazı: hiçbiri değişmez. Hiçbir ekranın XAML'ine dokunulmaz.
- Takvim, tarih seçici, açılır kutu, kaydırıcı ve sekme başlıklarının kendi iç düğmeleri bu işte
  değişmez.
- Klavye odak görünümü eklenmez.
- Deneme penceresindeki üç parça bu işte yok: yerinde onay ("Yayın bitsin mi? Vazgeç / Bitir"),
  kayan seçim ve çöp kutusu düğmesi. İstenirse ayrı iş.

### 4.6 Doğrulama (PR 1'de; merge'den önce Burak'a sunulacaklar)

Bu belgenin onayından sonra, şablonlar yazıldıktan sonra üretilir; belge onayının ön koşulu değildir.

- **Durağan görünüm aynı.** Beş stil eski ve yeni şablonla çizilir, görüntüler piksel piksel
  karşılaştırılır. Yaklaşım onayında söz verilen "ana ekranların önce ve sonra görüntüleri" bu
  yüzden aynı çıkar; kanıtı bu karşılaştırmadır.
- **Durum panosu.** Beş stilin durağan, üzerinde ve basılı hâlleri; üç düğme stilinin ayrıca devre
  dışı ve meşgul hâlleri. `Bg`, `Surface` ve `Surface2` zeminlerinde, yayın düğmesinin yeşil ve
  kırmızı hâlleri dâhil. Gerçek şablondan çizilir. Örtü değerleri bu pano üzerinden kesinleşir.
- **Elde deneme.** Hareket görüntüden anlaşılmaz; Burak merge'den önce derlenmiş uygulamada dener.

## 5. İş 2: Sohbeti yenile

### 5.1 Bugün (kodda bulunanlar, master `2de1970e`)

Elle yenileme yolu yok. Sohbet servisleri yalnız yayın oturumu açıkken çalışır ve oturumun
başladığını farklı hızlarda fark eder:

| Kaynak | Oturum başlayınca | Platformdaki yayın düşüp yeniden açılınca |
|---|---|---|
| YouTube | 60 sn'ye kadar gecikir (oturumu uyuyarak yoklar). | Okuyucu yayının bittiğini görürse 30 sn bekler, sonra yeniden arar; yeni yayın o an açık değilse 60 sn daha. Bekçisi yok: akış sessizce donarsa fark etmez. Ayarda sabit video bağlantısı varsa hep o videoya döner ve art arda hatada bekleme 5 dakikaya kadar uzar. |
| Facebook | Hemen; 2 dakika boyunca 5 sn'de bir arar. | 10–20 sn'de kendiliğinden (bekçi + hızlı arama). |
| Instagram | 60 sn'ye kadar gecikir. | Yaklaşık 10 sn'de kendiliğinden (her turda yayını sorar). Ama hesap bilgisi bir kez okunamazsa 5 dakika bekler. |
| TikTok | Uzantı sekmesine bağlı; oturumdan bağımsız. | Sekme yenilenince. Eski sekme açık ve suskun kaldıysa yeni sekme onu 60 sn sonra devralır. |

- `SessionStarted` olayını yalnız Facebook servisi dinliyor.
- YouTube'un "gösterilmiş mesaj" belleği okuyucunun içinde: okuyucu yeniden kurulunca (bugün yayını
  bitirip açınca da) akışın geçmişi yeniden yayımlanır, sohbette aynı mesajlar ikinci kez görünür.
  Facebook'un belleği servis düzeyinde, köprününki kalıcı.
- YouTube'un etkin yayın çözümleyicisi sonucu 60 sn önbellekte tutuyor; önbelleği boşaltan yol yok.
- YouTube hesabı bağlı değilken (diskte yetki yokken) sohbet döngüsünün çözümleme çağrısı Google izin
  sayfasını tarayıcıda açıyor ve operatör izin verene ya da vazgeçene kadar orada bekliyor
  (`CreateAuthorizedServiceAsync` → `ConnectAsync`). Yetki geri alınmışsa yenileme hatası, kullanıcıya
  gösterilecek iletiye çevrilmeden döngünün genel hata dalına düşüyor.
- Facebook çözümleyicisi her hatayı "yayın yok" sayıyor; geçersiz Sayfa token'ı da öyle görünüyor.
- Köprü, uzantı sekmesinin bağlanınca gönderdiği `connected` iletisini (platform, adres) yok sayıyor.
  Uzantının arka planı ve açılır penceresi de aynı uca kısa ömürlü yoklama soketleri açıyor; açık
  soket saymak "TikTok sekmesi bağlı" anlamına gelmez.
- YouTube ve Facebook servis döngülerinin testi yok (Facebook'ta yalnız saf karar kuralları
  sınanıyor); Instagram'ınki var.
- Durum göstergeleri: üst çubuktaki sohbet noktası (son mesajın yaşı), kenar çubuğundaki
  "BAĞLANTILAR" (izleyici sayısı kaydına bakar; Facebook hiç yeşil olmaz), sohbet panelindeki şerit
  (yalnız Instagram hesapları).

### 5.2 Yenile ne yapar

Yayın oturumuna, kuyruğa, etiketlere, satışlara, çekilişe dokunmaz. Yalnız oturum açıkken çalışır.
Her kaynak "oturum yeni başlamış gibi" baştan bağlanmayı dener:

| Kaynak | Yenileyince | Yapamadığı |
|---|---|---|
| YouTube | Çalışan okuyucu kesilir, bekleyen döngü hemen uyanır, hata bekleme sayacı sıfırlanır. Etkin yayın önbellek atlanarak yeniden sorulur; yayın yoksa hızlı arama penceresi açılır (§5.3). | Ayarda sabit video bağlantısı varsa yeni yayını bulamaz, o videoya döner. Hesap bağlı değilse ya da yetki düştüyse bağlanamaz; şerit söyler. |
| Facebook | Çalışan okuyucu kesilir, döngü uyanır, sayaç sıfırlanır, "az önce biten yayın" kara listesi temizlenir, 2 dakikalık hızlı arama açılır. | Sayfa bağlı değilse yapacak işi yok. |
| Instagram | Bekleyen hesap döngüleri (hesap okunamadı, hata sonrası bekleme, atlanmış Sayfa) hemen yeniden dener. Sağlıklı çalışan okuyucuya dokunulmaz. | Hız sınırı beklemesi kesilmez (kesmek sınırı uzatır). İzin sorunu sürüyorsa aynı duruma döner. |
| TikTok | Köprüdeki "tek etkin sekme" kilidi bırakılır: sıradaki mesajını ulaştıran sekme hemen etkin olur. | Sekmeyi yeniden yüklemez, sayfadaki okuyucuyu yeniden başlatmaz: sunucudan uzantıya komut gönderen bir yol yok. Sekme donduysa çare sekmeyi yenilemek. |

Yenileme isteği arayüz iş parçacığında bekletmez: servislere yalnız sinyal verir (iptal, uyandırma);
bağlanma servislerin kendi döngüsünde sürer. Yenileme yalnız uyuyan döngüyü uyandırmaz; o an süren
bir yayın arama çağrısını da keser.

**YouTube döngüsü etkileşimli yetkilendirme başlatmaz.** Sohbet döngüsü hesabın bağlı olup olmadığına
tarayıcı açmadan bakar; bağlı değilse bunu durum olarak bildirir (§5.4, "elle düzeltilmeli"). Hızlı
arama ve yenileme ancak böyle güvenli: yoksa her deneme bir izin sayfası açardı. Yan etkisi: bugün
oturum başlayınca kendiliğinden açılabilen Google izin sayfası artık açılmaz; hesap Ayarlar >
YouTube'dan bağlanır (moderasyon düğmelerinin davranışı değişmez).

### 5.3 Erken bağlanma

- YouTube ve Instagram servisleri de `SessionStarted`'ı dinler; uyuyan döngü hemen uyanır (Facebook'taki
  desen). Instagram'da oturum bitişinden sonraki 30 sn'lik bekleme de aynı sinyalle kesilir.
- YouTube'da "etkin yayın yok" cevabı gelince hızlı arama: oturum başlangıcından ya da yenilemeden
  sonraki 2 dakika boyunca 5 sn'de bir, sonra bugünkü gibi 60 sn'de bir. Uygulamada önce "Yayın
  Başlat"a basıp sonra platformda yayını açan operatör için gerekli; tek uyandırma yetmez.
  Kota: `liveBroadcasts.list` çağrısı 1 birim (resmi kota hesaplayıcısı, 24.11.2026); pencere başına
  en çok 24 birim, günlük kota 760 bin. Hesap ya da yetki sorununda hızlı arama uygulanmaz.
- Instagram oturum içinde yayını zaten sık soruyor; ona ek pencere gerekmez.

### 5.4 Durum şeridi

Sohbet panelinde başlığın altındaki Instagram şeridi, bütün sohbet kaynaklarının şeridi olur.
Aşağıdaki çizim yerleşimi anlatır. Görünüm, Burak'a uygulamanın kendi stilleriyle çizilmiş görüntü
üzerinden gösterildi (görüntü depoya girmez); plan bu bölümdeki metinden yapılır.

```
Canlı Sohbet   [Kullanıcı veya mesaj ara…]                         [Sadece A12]
● YouTube   ● TikTok   ● Facebook   Instagram ● @hesap1   ● @hesap2         [↻]
──────────────────────────────────────────────────────────────────────────────
```

Bir kaynak düşmüşken ve yenileme sürerken (▲ sarı nokta, ○ soluk nokta):

```
▲ YouTube yayın kesildi (21:14)   ● TikTok   ● Facebook   Instagram ● @hesap1   [↻]
○ YouTube bağlanıyor   ● TikTok   ○ Facebook bağlanıyor   Instagram ● @hesap1   [◌]
```

**Satırlar.** Sırayla YouTube, TikTok, Facebook, sonra "Instagram" etiketiyle Instagram hesapları.
Satır biçimi bugünkü Instagram satırıyla aynı: 8 px nokta + metin (`OD.Font.F1`, `TextDim`).
Platform ikonu kullanılmaz, düz ad yazılır: marka ikonlarının kullanım koşulları sohbet satırı için
tanımlı (`Themes/PlatformIcons.xaml`), kenar çubuğu da aynı nedenle düz ad kullanıyor. Şerit alt
satıra inerken "Instagram" etiketi ilk hesabından ayrılmaz ve her satır soldan hizalı başlar.

- YouTube satırı: ayarda kanal ya da video yazılıysa ve deneme sürümü değilse.
- Facebook satırı: bir Sayfa bağlıysa.
- TikTok satırı: oturum sırasında bir TikTok canlı yayın sekmesi köprüye bağlıysa ya da bu oturumda
  bağlı kaldıysa (oturumdan önce açılıp hâlâ bağlı duran sekme de sayılır).
- Instagram satırları: bugünkü kurallarla (2026-11-16 tasarımı §8.5, §8.6.3); değişen iki şey
  aşağıda (canlı satırın metni ve "yayın kesildi" kuralı).

**Sağlıklı satır yalnız adını yazar.** Bağlı kaynak "● YouTube" olarak görünür; durum metni yalnız
söylenecek bir şey varken eklenir. Bu kural Instagram satırlarına da uygulanır: canlı hesap
"● @hesap1 canlı" yerine "● @hesap1" görünür; öbür metinleri değişmez. Çizimde ölçülen (üç platform
ve iki Instagram hesabı): şerit yarım ekranda (sohbet paneli 400 px) iki, tam ekranda tek satır.
Her satır durumunu da yazsaydı üç ve iki satır olurdu.

**Görünürlük.** Oturum açıkken şerit hep görünür (düğmenin yeri orası); oturum yokken şerit de düğme
de yoktur. Bugünkü Instagram kuralı ("2 ve üzeri satır ya da sarı satır") kalkar: bugün şeridi hiç
görmeyen tek kaynaklı yayıncı da oturumda tek satırlık şeridi görür. Oturum açık ama hiçbir kaynak
satırı yoksa şeritte yalnız düğme durur.

**Durumlar** (YouTube örneğiyle; Facebook aynı):

| Durum | Nokta | Metin | İpucu |
|---|---|---|---|
| Bağlı | yeşil | `YouTube` | `YouTube sohbeti bağlı (21:03'ten beri).` |
| Bağlanıyor | soluk | `YouTube bağlanıyor` | yok |
| Yayın bekleniyor | soluk | `YouTube yayın bekleniyor` | `YouTube'da canlı yayın açılınca yorumlar akmaya başlar.` |
| Yayın kesildi (başka kaynak bağlıyken) | sarı | `YouTube yayın kesildi (21:14)` | `YouTube yayını kapandı; yorumları gelmiyor. Yayını yeniden açtıysan Sohbeti yenile'ye bas.` |
| Yayın bitti (bağlı kaynak kalmadıysa) | soluk | `YouTube yayın bitti (23:41)` | yok |
| Yeniden deneniyor | bu oturumda bağlıydıysa sarı, değilse soluk | `YouTube bağlantı koptu, 21:20'de dener` | `Bağlantı koptu; kendiliğinden yeniden denenecek. Beklemeden denemek için Sohbeti yenile'ye bas.` |
| Elle düzeltilmeli | sarı | `YouTube bağlantı kurulamıyor` | Sebep ve nereden düzeltileceği (aşağıda) |

"Yayın bekleniyor", "yayın bitti" ve "bağlanıyor" sorun değildir. Öbür üçü (yayın kesildi, yeniden
deneniyor, elle düzeltilmeli) **sorun durumu**dur; rengi soluk olsa da öyle sayılır (§5.5, onay
işareti).

"Elle düzeltilmeli"nin ipuçları:

| Sebep | İpucu |
|---|---|
| YouTube hesabı bağlı değil | `YouTube hesabı bağlı değil. Ayarlar > YouTube'dan bağla.` |
| YouTube yetkisi geçersiz ya da geri alınmış | `YouTube oturumunun süresi doldu. Ayarlar > YouTube'dan yeniden bağlan.` |
| YouTube kotası dolmuş | `Günlük YouTube API kotası doldu; sohbet yarına kadar okunamayabilir.` |
| YouTube API anahtarı yok | `Bu kurulumda YouTube API anahtarı yok; sohbet okunamıyor.` |
| Ayardaki sabit video canlı değil | `Ayarlardaki YouTube bağlantısı canlı bir yayına ait değil. Ayarlar > YouTube'a kanal adını ya da yeni yayının bağlantısını yaz.` |
| Facebook oturumu ya da izni geçersiz | `Facebook oturumu ya da izni geçersiz. Ayarlar > Facebook > "Facebook izinlerini güncelle".` |

- Facebook'ta "yayın kesildi" ipucu: `… Yayını yeniden açtıysan birkaç saniye içinde kendiliğinden
  bağlanır.` (Facebook kendi toparlıyor.)
- Facebook'ta "elle düzeltilmeli" bugün ayırt edilemiyor: çözümleyici her hatayı "yayın yok" sayıyor
  ve geçersiz token soluk "yayın bekleniyor" görünürdü. Çözümleyici yetki hatasını (Graph'ın oturum
  ve izin hataları) "yayın yok"tan ayırt edilebilir bildirir; bekçinin kullandığı sade çağrı ve
  "iki ardışık boş cevap" kuralı değişmez. Geçici hata (ağ, Graph'ın sunucu hatası, hız sınırı) da
  "yayın yok" sayılmaz: satır "yeniden deneniyor" olur ve arama aynı aralıkla sürer. (Uygulama
  sırasında, dal incelemesinin bulgusuyla eklendi: yayın sürerken ağ gidince satır "yayın kesildi"
  deyip operatörü açık yayını yeniden açmaya yollamasın.)
- TikTok: sekme bağlıyken `● TikTok` (ipucu: `TikTok canlı yayın sekmesi bağlı.`). Bu oturumda
  bağlıydı ve şimdi bağlı sekme yoksa `TikTok sekme bağlı değil`; başka kaynak bağlıyken sarı,
  değilse soluk. İpucu: `TikTok canlı yayın sekmesi kapalı ya da uzantı bağlı değil. Sekmeyi aç ya da
  yenile.` "Sekme bağlı" = kendini TikTok sekmesi olarak tanıtmış açık bir bağlantı var (uzantının
  bağlanınca gönderdiği `connected` iletisi; sekme yalnız canlı yayın sayfasındayken bağlanır).
  Uzantının arka plan ve açılır pencere yoklama soketleri sayılmaz. Bilinen sınır: sekme canlı yayın
  sayfasından ayrılıp açık kalırsa satır yeşil kalabilir.
- **"Yayın kesildi" kaynaklar arası çalışır.** Bu oturumda bağlıyken yayını kapanan kaynak, başka
  herhangi bir kaynak (başka platform ya da başka Instagram hesabı) bağlıyken sarı "yayın kesildi",
  bağlı kaynak kalmadıysa soluk "yayın bitti" olur. Instagram sunucusundaki tek değişiklik budur:
  bugün yalnız öbür Instagram hesaplarına bakıyor, bundan sonra öbür platformlara da bakar (YouTube
  sürerken düşen tek Instagram hesabı bugün "yayın bitti" görünüyor, "yayın kesildi" olmalı). İpucu
  buna göre "Diğer hesap yayında" yerine öbür kaynakları anar.
- **"Yayın bekleniyor" başka kaynak bağlı diye sararmaz.** O gece o platformda yayın yapmayan
  yayıncı uyarı görmez. Instagram'ın hesaplar arası "canlı yayın bulunamadı" kuralı olduğu gibi kalır
  (yalnız öbür Instagram hesaplarına bakar).

**Veri.** YouTube, Facebook ve TikTok için yeni, platformdan bağımsız bir durum izleyicisi:
Instagram izleyicisindeki gibi yalnız olgu tutar (durum, duruma giriş anı, yeniden deneme saati,
açıklama, "bu oturumda bağlıydı"). Metin, renk ve ipucu okuma anında saf bir sunucuda türetilir.
"Bu oturumda bağlıydı" bilgisi yeni oturumda sıfırlanır. Servisler durumlarını kendileri bildirir:

| Serviste olan | Durum |
|---|---|
| Oturum yok, kaynak kurulu değil ya da deneme sürümü | satır yok |
| Yayın aranıyor, okuyucu kuruluyor; yenileme isteği alındı | Bağlanıyor |
| Platform "etkin yayın yok" dedi | Bu oturumda bağlı olmadıysa "yayın bekleniyor", olduysa "yayın kesildi / bitti" |
| Okuyucu başladı, akış ya da yoklama çalışıyor | Bağlı |
| Okuyucu yayının bittiğini bildirdi | Yayın kesildi / bitti |
| Okuyucu hatayla çıktı, döngü bekleyip yeniden deneyecek | Yeniden deneniyor (deneme saatiyle) |
| Operatörün düzeltmesi gereken ya da bekleyerek geçmeyecek sorun (yukarıdaki ipucu tablosu) | Elle düzeltilmeli |

YouTube çözümleyicisi üç sonucu ayırt edilebilir biçimde bildirmelidir: etkin yayın yok; hesap ya da
yetki sorunu (bağlı değil, yetki geçersiz, kota); geçici hata (ağ). Bugün ilk ikisi aynı istisna
türü, bağlı olmama ise istisna bile değil (§5.1).

**Öbür göstergeler.**

- Kenar çubuğundaki "BAĞLANTILAR"da YouTube, Facebook ve TikTok satırlarının yeşili aynı izleyiciden
  okunur (izleyici verildiyse), şeritle çelişmesin. Facebook satırı böylece ilk kez yeşil olabilir.
  İzleyici sayıları bugünkü gibi kalır.
- Üst çubuktaki sohbet noktası: Instagram için bugün geçerli olan kural bütün kaynaklara uygulanır.
  Sarı satır noktayı sarartır ve ipucuna cümlesini yazar; "yayın kesildi" bunu yalnız ilk 2 dakika
  yapar, sonra yalnız şeritte sarı kalır. Instagram'ın "yayın kesildi"si artık öbür platformlara da
  baktığı için, YouTube sürerken düşen Instagram hesabı da noktayı sarartır.

### 5.5 Düğme

- **Yer (Burak'ın seçimi, çizim üzerinden): şeridin sağ ucunda,** durumların yanında. Şerit birden
  çok satıra inse de düğme ilk satırın hizasında, sağda kalır; satırlar ona kadar uzanır. Başlık
  bugünkü gibi kalır. Oturum yokken şeritle birlikte hiç görünmez; devre dışı hâli gerekmez.
- **Biçim.** Hayalet stilde 24 px kare ikon düğmesi. İkon tek renkli, iki oklu "yenile" simgesi
  (`Themes/Icons.xaml`'e yeni geometri); meşgul halkasından ayırt edilsin diye tek oklu değil.
  İpucu ve erişilebilirlik adı: "Sohbet bağlantılarını yenile".
- **Etkinlik.** Yalnız yayın oturumu açıkken vardır. Onay sormaz: yenileme geri alınacak bir şey
  bozmaz.
- **Basınca.** Düğme meşgul olur (ikonun yerinde halka döner). Yeniden bağlanmayı deneyecek her
  kaynak, isteğin alındığı anda "bağlanıyor"a geçer: YouTube, Facebook ve o an beklemede olan
  Instagram hesapları. Sağlıklı Instagram okuyucusu ve TikTok durum değiştirmez. (Instagram
  izleyicisi bugün "bağlanıyor"u yalnız durmuş hesaba yazıyor; yenilemeyle uyanan hesap için bu
  bilinçli bir istisnadır.) Şerit yenileme sürerken en geç 1 sn'de bir güncellenir (olağan nabız
  5 sn).
- **Bitiş.** Meşgul hâl en az 1 sn sürer (titreme olmasın) ve hiçbir satır "bağlanıyor"da
  kalmayınca biter; tavanı 15 sn. Tavana varınca düğme boşa döner, bağlanamayan kaynak kendi
  döngüsünde denemeyi sürdürür.
- **Onay işareti.** Bitişte hiçbir satır sorun durumunda değilse (§5.4; Instagram'da onun sorun
  durumları) ikon yaklaşık 1,5 sn onay işareti olur, sonra yenile simgesine döner. Sorun durumunda
  satır varsa, rengi soluk olsa bile, onay işareti gösterilmez; sorunu şerit söyler.
- Meşgulken yeniden basılamaz. Oturum biterse meşgul hâl hemen biter.

### 5.6 Güvenceler

- **Yinelenen mesaj yok.** YouTube'un "gösterilmiş mesaj" belleği okuyucudan servise taşınır
  (bugünkü sınırla, son 5000 kimlik) ve okuyucular arasında korunur. Facebook ve köprünün belleği
  zaten korunuyor. TikTok'ta kilit devrinde mevcut 10 sn'lik kopya süzgeci çalışır.
- **Kaybolan mesaj yok.** YouTube ve Facebook yeniden bağlanınca aradaki mesajları kaynağın
  geçmişinden alır (YouTube akışı ilk bağlantıda yakın geçmişi gönderir; resmî belge, 24.11.2026).
  Instagram'da sağlıklı okuyucu durdurulmadığı için filigranı korunur.
- **Yan kazanç.** Bugün yayını bitirip yeniden başlatınca YouTube geçmişinin sohbette ikinci kez
  görünmesi de böylece biter.
- **Kota.** Bir yenileme YouTube'da yaklaşık 7 birim tutar (liste 1, akış 5, gerekirse video 1).

### 5.7 Günlük

Her yenileme isteği ve her kaynağın ona verdiği karşılık günlüğe bir satır yazar (kim neyi kesti,
kim uyandı, kimin yapacak işi yoktu). Sahadaki "sohbet neden düştü" sorusu bu satırlarla cevaplanır.
Token, kimlik ya da mesaj metni yazılmaz.

### 5.8 Kapsam dışı

- YouTube'un kendiliğinden toparlaması (çalışan yayına bekçi, okuyucu çıkınca hızlı arama). Kota buna
  yetiyor görünüyor; ayrı, sonraki iş. Düğme o zaman da emniyet supabı olarak kalır.
- TikTok sekmesini uygulamadan yenilemek ya da uzantıya "yeniden tara" komutu: uzantı sürümü ister.
- YouTube, Facebook ve TikTok için "yanıt yok (N sn)" türetmesi.
- Ayarlar ekranı, oturum modeli, raporlar, overlay.

## 6. Yaklaşımlar

**İş 1**

1. **Seçilen: mevcut stilleri yerinde güncelle.** Beş stil değişir, ekranlara dokunulmaz, bütün
   düğmeler birlikte değişir. Bedeli: değişiklik her ekranı aynı anda etkiler; karşılığı §4.6'daki
   doğrulama.
2. Yeni stilleri yanına ekleyip ekranları tek tek geçirmek: risk küçük, ama uygulama bir süre iki
   görünümle kalır ve 150 kullanım yeri elle değişir. Elendi.
3. Hazır kütüphane (Material Design in XAML ve benzerleri): kendi renk ve temasıyla gelir, örtük
   stilleri bizim temaya sızar. Elendi. Web setleri (rareui, uiverse, shadcn) React/CSS'tir; WPF'te
   doğrudan kullanılamaz.

**İş 2**

1. Yalnız düğme: dört kaynağı yeniden bağlatır, "sohbet geç geliyor" derdine dokunmaz.
2. **Seçilen: düğme + erken bağlanma.** Düğmeye ek olarak YouTube ve Instagram oturum başlar
   başlamaz bağlanır.
3. Tümüyle kendiliğinden toparlama, düğmesiz: en temizi ama en uzunu; YouTube bekçisi ayrı iş olarak
   bırakıldı (§5.8).

**Düğmenin yeri** (Burak çizim üzerinden seçti; §5.5)

1. **Seçilen: şeridin sağ ucunda.** Başlık değişmez, düğme durumların yanında.
2. Başlığın en sağında, "Sadece {kod}" çipinin sağında (yaklaşım onayında konuşulan yer): her zaman
   görünür, ama çizimde ölçüldü: yarım ekranda çip görünürken arama kutusu 181 px'ten 141 px'e iner,
   ipucu metni kırpılır. Elendi.

**Şerit metni** (çizimde ikisi de vardı; seçilen yerleşim birincisiyle gösterildi)

1. **Uygulanan: sağlıklı satır yalnız adını yazar.** İki Instagram hesabıyla yarım ekranda iki, tam
   ekranda tek satır.
2. Her satır durumunu da yazar ("YouTube canlı"): bugünkü Instagram satırlarıyla birebir aynı kalır,
   ama şerit yarım ekranda üç, tam ekranda iki satıra çıkar ve sohbete ayrılan yerden yer.

## 7. Onaya sunulan kararlarım

Burak'ın söylemediği, benim verdiğim kararlar. Her biri tek başına değiştirilebilir.

1. Durağan görünüm değişmez. İkincil düğme bugünkü gibi saydam zeminli kalır; deneme penceresindeki
   dolu zemin ürüne taşınmaz, çünkü ikincil düğme farklı zeminlerin üstünde duruyor.
2. Birincil düğmenin üzerine gelince 1 px kalkması kaldırılır (sade yönde üzerine gelme yalnız
   tondur).
3. Küçülme yalnız birincil ve ikincil düğmede. Basılı ton birincilde koyulaşır, öbürlerinde açılır
   (koyu zeminde koyulaşacak pay yok; §4.2).
4. Bütün geçişler tek süre (150 ms) ve tek eğriyle; yay eğrisi düğmelerden çıkar (§3).
5. Meşgulken dönen şey ikonun kendisi değil, yerine geçen halka: aynı meşgul görünümü metinli
   düğmelerde de çalışsın diye. Yaklaşım onayındaki "ikon döner" sözünden sapma.
6. (Karar Burak'ın: Yenile düğmesi şeritte, §5.5.) Benim eklediğim: şerit oturum boyunca hep
   görünür.
7. Şeritte platform ikonu yok, sağlıklı satırda durum sözcüğü yok; Instagram'ın canlı satırından
   "canlı" sözcüğü kalkar.
8. Onay işareti yalnız hiçbir satır sorun durumunda değilken.
9. Kenar çubuğu aynı durumdan okur. Üst çubuk noktasında Instagram'ın bugünkü kuralı bütün
   kaynaklara uygulanır (sarı satır noktayı sarartır).
10. Facebook Sayfasını yalnız Instagram için bağlayan yayıncı, oturum boyunca soluk "Facebook yayın
    bekleniyor" satırını görür. Instagram'daki "bağlı ama bu gece kullanılmıyor" hesabın bugün
    gördüğü muameleyle aynı.
11. Yenileme TikTok'ta yalnız kilidi bırakır; Instagram'da sağlıklı okuyucuya dokunmaz.
12. YouTube'da hızlı arama penceresi: 5 sn aralık, 2 dakika (Facebook'taki değerler).
13. YouTube sohbet döngüsü Google izin sayfasını artık kendiliğinden açmaz (§5.2). Bugünkü
    davranıştan bilinçli sapma; hesap yalnız Ayarlar'dan bağlanır.
14. Facebook'ta geçersiz oturum ya da izin "yayın bekleniyor" değil "bağlantı kurulamıyor" görünür;
    bunun için çözümleyici yetki hatasını ayırt eder (§5.4). Kapsamı büyüten küçük bir ek; yapılmazsa
    şerit bu durumda yanlış bilgi verir.
15. Ayarda YouTube kanalı yazılı ama hesap bağlı değilse satır her oturumda sarıdır ve üst noktayı
    sarartır. YouTube kullanmayan yayıncının çaresi ayardaki kanalı silmektir.

## 8. Uygulayıcıya bırakılanlar

- Geçişlerin mekanizması (görsel durum yöneticisi ya da tetikleyici). Koşullar: durumlar testte
  doğrudan tetiklenebilmeli, hareket azaltma tek yerde karara bağlanmalı, örtü ve ölçek dışında
  özellik canlanmamalı.
- Örtü saydamlıklarının adlandırılmış kaynak olup olmayacağı ve nerede duracağı.
- Meşgul bayrağının ve yeni tiplerin adları; yenileme sinyalinin biçimi (olay taşıyan tekil nesne ya
  da arayüz). Sohbet servisleri bugün fabrika lambdasıyla kayıtlı; görünüm modeli onlara tipleriyle
  ulaşamaz, bu yüzden oturum servisindeki olay deseni doğal adaydır.
- Yeni durum izleyicisinin ve sunucusunun tip yapısı; satır kaydının Instagram satırıyla ortak mı
  ayrı mı olacağı.
- Şeridin yenileme sırasında nasıl sık güncelleneceği (olay ya da geçici zamanlayıcı).
- YouTube ve Facebook servis döngülerini sınanabilir kılacak dikişler (saat, okuyucu ve çözümleyici
  fabrikası); ikisi de bugün gerçek saatle çalışıyor ve parçalarını kendi içinde kuruyor.
- YouTube'da "hesap bağlı mı" sorusunun tarayıcı açmadan nasıl sorulacağı (`IsConnectedAsync` var) ve
  yetki yenileme hatasının nasıl sınıflanacağı.
- Üst nokta ipucunun birden çok kaynakta sorun varken nasıl birleştirileceği (Instagram cümlesinin
  deseniyle).
- YouTube'da aynı sohbete yeniden bağlanırken geçmişi yeniden almak mı, son `nextPageToken` ile
  kaldığı yerden sürdürmek mi (resmî belge ikisini de anlatıyor: ilk bağlantıda yakın geçmiş gelir,
  belirteçle kalınan yerden sürer). Hangisi seçilirse seçilsin kimlik belleği servis düzeyinde kalır.
- Instagram'da hız sınırı beklemesinin nerede yaşadığını doğrulamak; yenileme onu kesmemeli.
- "Yenile" ve "onay" ikonlarının geometrisi (24×24 kutu, tek renk, öbür ikonlarla aynı çizgi).
- Mevcut testlerin uyarlanması: şerit görünürlüğü (`MainShellInstagramHealthTests`), kenar çubuğunun
  yeni izleyiciden okuması (`MainShellConnectionsTests`), Instagram sunucusundaki iki değişiklik
  (`InstagramHealthPresenterTests`: canlı satırın metni, "yayın kesildi"nin öbür platformlara bakması).

## 9. Test

**İş 1**

- Beş stilin şablonu gerçekleşir; durağan hâlde örtüler görünmez, ölçek 1.
- Stil başına üzerinde ve basılı hâllerin uç değerleri (durum doğrudan tetiklenerek); devre dışı
  hâli yalnız üç düğme stilinde.
- Hareket azaltma açıkken geçiş anında uç değere varır; halka dönmez.
- Meşgul: açılınca içerik görünmez, halka görünür, `Content` ve ölçü değişmez, fare tıklaması komutu
  çalıştırmaz; kapanınca içerik geri gelir; devre dışı düğme meşgulken solmaz. Düğmeyi `Content`
  metniyle bulan mevcut testler geçmeye devam eder.
- Koruma: düğme şablonlarında `Effect` ve gradyan fırça yok; `Colors.xaml`'deki fırça sayısı
  değişmedi (mevcut test); stil listesi testine `OD.Button.Secondary` eklenir.

**İş 2**

- YouTube servisi (sahte saat, sahte okuyucu ve çözümleyici): oturum başlayınca beklemeden çözümler;
  yenilemede çalışan okuyucu ve süren çözümleme kesilir, beklemeden yeniden kurulur; önbellek
  atlanır; hata sayacı sıfırlanır; yayın yokken hızlı arama aralığı, pencere bitince 60 sn; hesap ya
  da yetki sorununda hızlı arama yok ve durum "elle düzeltilmeli"; hesap bağlı değilken tarayıcı
  açılmaz; aynı yayına yeniden bağlanınca gösterilmiş mesaj yeniden yayımlanmaz; oturum bitince
  okuyucu kesilir.
- Facebook servisi (sahte saat, sahte çözümleyici ve okuyucu): yenilemede okuyucu kesilir, kara liste
  temizlenir, hızlı arama açılır, yorum belleği korunur; geçersiz oturum "elle düzeltilmeli" olur;
  bekçinin "iki ardışık boş cevap" kuralı değişmez.
- Instagram: yenilemede uzun beklemeler kesilir ve uyanan hesap "bağlanıyor"a geçer; sağlıklı okuyucu
  ve filigranı yerinde kalır; oturum başlayınca üst döngü beklemeden başlar.
- Köprü: kilit bırakılınca öbür sekmenin sıradaki mesajı kabul edilir; devir penceresindeki kopya
  yine süzülür; `connected` iletisi bağlantıyı TikTok sekmesi olarak işaretler, yoklama soketi
  sayılmaz, işaretli bağlantı kapanınca "bağlı sekme yok" olur.
- Durum sunucusu (saf): her durumun metni, rengi, ipucu; "yayın kesildi"nin başka kaynak bağlıyken
  sarı, değilken "yayın bitti" oluşu; "yayın bekleniyor"un başka kaynak bağlı diye sararmaması; sorun
  durumu tanımı; üst nokta kuralı ("yayın kesildi" yalnız ilk 2 dakika); şerit görünürlüğü; sağlıklı
  satırda yalnız ad.
- Kabuk: yenileme yalnız oturum açıkken çalışır; meşgulken yeniden çalışmaz; satırlar sonuçlanınca
  biter; 1 sn tabanı ve 15 sn tavanı; sorun durumunda satır yokken onay işareti, varken (soluk olsa
  da) yok; oturum bitince meşgul biter; kenar çubuğu şeritle aynı durumu gösterir; üst nokta her
  kaynağın sarı satırıyla sararır.
- Yerleşim: düğme şeridin sağ ucunda, ilk satırın hizasında kalır; şerit oturum boyunca görünür,
  oturum yokken ikisi de yoktur; başlık değişmez. Yarım ekranda şerit taşmaz, alt satıra iner;
  "Instagram" etiketi ilk hesabından ayrılmaz.
- Saha (otomatik değil, Burak'la): her platformda "yayını kapat, yeniden aç, Yenile'ye bas"
  denemesi; önce YouTube.

## 10. Sürüm ve sıra

- PR 1: İş 1. PR 2: İş 2 (PR 1 master'a girdikten sonra açılır; yığılmaz).
- İkisi aynı sürümde çıkar. Sunucu ve uzantı değişmediği için sunucu dağıtımına ve Web Store
  sürümüne bağlı bir adım yok.
- Merge ve sürüm yayın saatleri dışında; merge kararı Burak'ta.

## 11. Riskler

- **İş 1 bütün ekranları birden etkiler.** Karşılığı: durağan görünümün piksel karşılaştırması ve
  durum panosu (§4.6).
- **YouTube ve Facebook servis döngüleri bugün testsiz** ve bu işin en çok dokunduğu yer. Karşılığı:
  değişiklikten önce mevcut davranışı sabitleyen testler ve onları mümkün kılan dikişler.
- **YouTube döngüsünün izin sayfası açmayı bırakması bir davranış değişikliği.** Hesabını bugün bu
  yolla bağlamış olan yayıncı etkilenmez (yetki diskte); yeni kurulumda bağlanma Ayarlar'dan yapılır.
- **Şerit, bir hafta önce onaylanan Instagram şeridine dokunur.** Değişen üç şey: şeridin
  görünürlüğü, canlı satırın metni ve "yayın kesildi" kuralının öbür platformlara da bakması (§5.4).
  Instagram'ın öbür durum kuralları ve metinleri aynen kalır; mevcut testleri bunu korur.
- **Yenilemenin etkisi platforma göre farklı.** TikTok'ta yapabildiği az; belge ve ipuçları bunu
  açık söyler, düğme "her şeyi düzeltir" izlenimi vermemeli.
