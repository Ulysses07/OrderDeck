# Sohbette "Kayıtlı" rozeti — kısa tasarım

Tarih: 2026-11-23. Onay: Burak, aynı gün ("Evet, başla"). Görünüm aynı akşam, gerçek temayla çizilmiş
görüntü üzerinden yeniden kararlaştırıldı (aşağıda "Görünüm kararı"). Küçük değişiklik; yalnız WPF istemcisi.

## Amaç

Yayında biri sohbete yazdığında operatör, o kişinin kaydı olup olmadığını satıra bakarak görsün.
Bugün bunu öğrenmek için müşteri kartını açmak gerekiyor.

## Kural

"Kayıtlı" = telefonu kayıtlı müşteri. Müşteri arama çekmecesindeki "Kayıtlı" rozeti ve sayacıyla aynı
kural (`Phone` dolu ve yalnız boşluk değil); ikinci bir tanım yok.

Kişi düzeyinde bakılır: yazan hesabın satırında telefon yoksa ama aynı kişinin (aynı `GroupId`) başka
bir hesabında varsa satır yine "Kayıtlı"dır. Aynı tanıtıcının öbür "@" yazımındaki ikiz satır da
sayılır (kara liste vurgusuyla aynı arama kuralı). Müşteri hiç tanınmıyorsa rozet yoktur.

## Görünüm

Kullanıcı adının yanında, ad alanının sağ ucunda küçük yeşil "Kayıtlı" yazısı; arama çekmecesindeki
rozetle aynı stil (ince zeminli çip, mikro yazı, başarı rengi). İpucu: "Telefonu kayıtlı müşteri".

```
[IG] izleyici_bir              Kayıtlı  A12 alıyorum
[TT] izleyici.iki                       A12
[IG] cok_uzun_bir_kullanici_adi_ornegi  Kayıtlı  B7
[YT] Uzun Görünen Kanal Adı Örneği  B7 bana
```

- **Ad kesilmez.** Rozet adın yerinden yemez; ad hiçbir satırda kırpılmaz.
- Ad alanı ada göre büyür. Tabanı `OD.Layout.ChatNameColumn` (184 = eski ad sütunu 132 + boşluk +
  rozet): ada sığan satırlarda mesaj aynı hizada başlar ve eski sütuna sığan her ad, rozetle birlikte
  de sığar.
- Tabana sığmayan uzun adda yer mesajdan alınır: yalnız o satırın mesajı daha sağdan başlar; sığmayan
  mesaj zaten alt satıra iner.
- Tavan `OD.Layout.ChatNameColumnMax` (300): geniş pencerede çok uzun bir ad satırın çoğunu kaplamasın.
  Tavana sığmayan ad alt satıra iner (yine kesilmez).
- Mesaja ayrılan taban (48, `OD.Pad.ChatMessageReserve`): ad alanı ne kadar büyürse büyüsün mesaja en az
  bu kadar yer kalır. Dar pencerede ad alanı tavana varmadan durur; oraya sığmayan ad alt satıra iner.
  Instagram hesap etiketi önceliğini ve genişliğini korur (16 karakterlik etiket kırpılmaz).
- Kayıtsız satırda rozet yer kaplamaz; ad o yeri de kullanır.
- Ad ipucu eskisi gibi ham kullanıcı adıdır. Hareket yok: akan sohbette sabit bir işaret.

### Görünüm kararı (23.11 akşamı)

İlk onaylanan görünümde rozet, 132 px'lik sabit ad sütununun içinden yer alıyordu; kayıtlı müşterinin
adı 9–10 karakterde "…" ile kırpılıyordu. Burak gerçek temayla çizilmiş görüntüyü görünce kararı
değiştirdi: yerleşim aynı kalsın, ama "kullanıcı adı ile mesaj kesilmesin; mesaj zaten alt satıra
geçiyor, mesajın aşağı inmesi çok sorun olmaz". Yukarıdaki kurallar bu kararın uygulamasıdır.

Bedeli:

- Mesajın başladığı yer her satırda 52 px sağa kaydı (132 → 184); tabana sığmayan adlı satırlarda
  mesaj hizası o satır için bozulur.
- Yarım ekranda (960'lık pencere, sohbet satırı yaklaşık 400 px; operatörün olağan çalışma biçimi)
  mesaja kalan yer 226'dan 174'e iner. Instagram hesap etiketi taşıyan satırda (iki ve üzeri Sayfa
  bağlıyken) yaklaşık 105–127'den 53–75'e, adı tabana sığmayan etiketli satırda 48–53'e iner; mesaj
  daha çok satıra bölünür, satır yükselir. 48 px'e beş karakterlik bir kod tek satırda sığar.
- Uzun adlı bir satır sonradan kayıtlı olunca o satırın mesajı en çok rozet kadar sağa kayar.

İlk yazımda ad alanı sabit tavana kadar büyüyordu; yarım ekranda hesap etiketli, uzun adlı satırda
mesaja bir harflik yer bile kalmıyordu, metin harf harf alt alta bölünüp görünmez oluyordu (bağımsız
incelemede bulundu, çizimle doğrulandı). Mesaja ayrılan taban bunun düzeltmesidir: üçü birlikte
sığmayınca yer açan addır, alt satıra iner.

## Güncellenme

- Mesaj listeye girerken bakılır (kara liste vurgusuyla aynı anda, tek depo çağrısıyla).
- Ekrandaki satırlar şu anlarda yeniden değerlendirilir: yeni form kaydı eşitlendiğinde (yeni kayıt
  sayacı arttığında) ve kara liste vurgusunun tazelendiği yerlerde. Yayın sırasında form dolduran
  kişinin eski satırları böylece "Kayıtlı"ya döner.

## Bilinen sınır

Rozet, uygulamanın o göndereni kayıtlı müşteriyle eşleştirip eşleştirmediğini gösterir. Sohbet kimliği
formda yazılanla aynı değilse (YouTube'da kanal kimliği, Facebook'ta sayfa kapsamlı kimlik, TikTok'ta
ekrandaki ad) kayıtlı bir kişi rozetsiz görünebilir. Rozetin varlığı güvenilirdir; yokluğu bu
platformlarda "kayıtsız" kanıtı değildir.

## Kapsam dışı

- Yazdırma kuyruğu, overlay, müşteri kartı ve raporlar değişmez.
- "Kayıtsız" için ayrı bir işaret yok.
- Başka bilgisayardan müşteri çekimiyle gelen telefon, ekrandaki eski satırları kendiliğinden
  tazelemez; o kişinin sonraki mesajında rozet çıkar.
- Sunucu, yerel şema, uzantı değişmez; göç yok.

## Yaklaşımlar

1. **Seçilen: ad alanı ada göre büyür (taban + tavan), rozet alanın sağ ucunda.** Ad kesilmez, yer
   mesajdan alınır.
2. İlk onaylanan: rozet sabit 132 px'lik ad sütununun içinde. Mesaj hizası hiç oynamıyordu ama adı
   9–10 karaktere kırpıyordu; Burak görüntüyü görünce vazgeçti.
3. Sabit ama daha geniş ad sütunu, sığmayan ad alt satıra: hiza hiç bozulmaz, ama her satırda mesajdan
   daha çok yer alır ve sınırı bir harf aşan ad tek harfini alt satıra atar. Çizilip denendi, elendi.
4. Rozeti satırın sağ ucuna ya da adın altına koymak, yazı yerine ✓ ya da yalnız yeşil ad: soruldu;
   Burak yazının adın yanında kalmasını istedi.

rareui.com incelendi (Burak'ın önerisi): yalnız React bileşenleri, rozet türü bir bileşen yok ve
lisansı (MIT + Commons Clause + atıf, "taşınmış hâli" dâhil yeniden yayımlama yasağı) herkese açık
depoyla uyuşmayabilir. Kullanılmadı.

## Uygulayıcıya bırakılanlar

- Depo yönteminin adı ve dönüş tipi; mevcut `IsBlacklistedAnySpelling` çağıranları değişmez.
- Çipin tam iç boşluğu, tema belirteçleriyle.

## Test

- Depo: tanınmayan kişi, telefonlu satır, telefonu bağlı hesapta olan kişi, telefonsuz grup, "@" ikizi,
  yalnız boşluk telefon, kara liste ile birlikte.
- Kabuk: gelen mesajın satırı işaretli doğar; yeni form kaydı sayacı artınca ekrandaki satır tazelenir.
- Yerleşim: rozet görünür / gizli; rozet adın yerinden yemez; ada sığan satırlarda mesaj aynı hizada;
  uzun ad kesilmez, yeri mesajdan alınır; tavana sığmayan ad alt satıra iner; en dar pencerede mesaj
  tabanını korur, hesap etiketli uzun adlı satırda da (etiket kırpılmaz, ad alt satıra iner); kayıtsız
  satırda ad rozetin yerini de kullanır; ekrandaki satır sonradan kayıtlı olunca rozet belirir, uzun
  adda ad yine kesilmez; rozet satırı yükseltmez.
