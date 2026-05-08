# OrderDeck Kurulum Rehberi

Bu rehber OrderDeck'i sıfırdan kurup yayına hazır hâle getirir. Yaklaşık **10 dakika** sürer.

## 📋 Sistem Gereksinimleri

- Windows 10 (22H2 veya sonrası) **veya** Windows 11
- 64-bit işlemci (Intel/AMD veya ARM64)
- ~500 MB boş disk alanı
- İnternet bağlantısı (lisans aktivasyonu için)
- Google Chrome (canlı yayın chat'i için)

## 1. İndirme

[orderdeckapp.com/indir](https://orderdeckapp.com/indir) adresinden son versiyonu indir.

İndirilen dosya: `OrderDeck-X.Y.Z-setup.exe` (~50-80 MB)

## 2. Kurulum

İndirilen `.exe`'ye **çift tıkla**.

### ⚠️ SmartScreen Uyarısı (Faz 1)

> "Windows PC'nizi korudu" uyarısı çıkarsa:
>
> 1. **Daha fazla bilgi** linkine tıkla
> 2. **Yine de çalıştır** butonu görünecek, ona bas
>
> _(OrderDeck'in kod imzalama sertifikası alındığında bu uyarı kalkacak — 2026 Q3.)_

Inno Setup sihirbazı açılır:

1. **Türkçe** seçeneğini onayla → **Tamam**
2. Lisans sözleşmesini kabul et
3. **Masaüstüne kısayol oluştur** seçeneğini işaretli bırak (default)
4. **Yükle** butonuna bas
5. Kurulum ~30 saniye sürer (bilgisayarın hızına göre)
6. Sonunda **OrderDeck'i şimdi başlat** seçeneği işaretli — **Bitir**

OrderDeck otomatik açılır.

## 3. İlk Açılış Sihirbazı

İlk başlatmada 6 adımlı kurulum sihirbazı açılır:

### Adım 1 — Hoş Geldin
İlerle.

### Adım 2 — Lisans
**Lisansı etkinleştir** butonuna bas. Açılan dialog'da:
- **Hesabın varsa**: Email + şifre ile giriş yap
- **Hesabın yoksa**: "Hesap Oluştur" sekmesinden kayıt ol → email doğrulaması → giriş yap
- **Lisans anahtarı**: hesabınla eşleşen anahtarı gir (faturadan veya sipariş emailinden)

### Adım 3 — YouTube Kanal Bağlantısı _(opsiyonel)_

YouTube canlı yayınlarındaki chat mesajlarını OrderDeck'e otomatik çekmek için kanal handle'ını gir:
- Örnek: `@orderdeck`
- Veya tam URL: `https://youtube.com/@orderdeck`

YouTube kullanmıyorsan bu adımı atla — Instagram/TikTok/Facebook için Chrome eklentisi yeterli.

### Adım 4 — Yazıcı Ayarları _(opsiyonel)_

Yazıcı seçimi + etiket boyutu daha sonra **Ayarlar** menüsünden yapılır. Bu adımı atla.

### Adım 5 — Chrome Eklentisi Kurulumu ⭐

Bu en kritik adım. Sihirbazda 4 alt-adım göreceksin:

#### 1. Eklenti klasörünü hazırla
Sihirbazdaki **Klasörü Aç** butonuna bas. Explorer'da `Extension` klasörü açılır. Bu pencereyi açık bırak.

#### 2. Chrome'da `chrome://extensions` sayfasını aç
Sihirbazdaki **Chrome'da Eklentiler Sayfasını Aç** butonuna bas. Chrome açılır ve doğru sayfaya gider.

#### 3. Eklentiyi yükle
Açılan sayfada:

1. Sağ üstte **Geliştirici modu** anahtarını **Aç** (gri → mavi)
2. Sol üstte **Paketlenmemiş öğe yükle** butonu çıkar — ona bas
3. Açılan klasör seçicide **adım 1'de açılan Extension klasörünü** seç → **Klasör Seç**
4. Eklenti listesinde **OrderDeck Chat Bridge** kartı görünür ✓

#### 4. Bağlantıyı doğrula
Sihirbazda **Doğrula** butonuna bas. ~2 saniye içinde sonuç:
- 🟢 **Eklenti bağlı ✓** → tamam
- 🟡 **Eklenti henüz bağlanmadı** → Chrome'u açık bıraktığından emin ol, **Doğrula**'ya tekrar bas. Hâlâ kırmızıysa adım 3'ü tekrar yap.

### Adım 6 — Hazırsın 🎉

**Bitir** butonuna bas. OrderDeck ana ekranı açılır.

## 4. OBS Browser Source Ayarları

OBS'de iki tane **Browser Source** ekle:

| Source Adı | URL | Genişlik | Yükseklik |
|---|---|---|---|
| Chat | `http://localhost:4747/overlay/chat` | 1920 | 1080 |
| Çekiliş | `http://localhost:4747/overlay/giveaway` | 1920 | 1080 |

**Custom CSS**: ekleme — overlay'in kendisi transparan arka planlı.

## 5. Yayın Başlatma

1. OrderDeck → **Yayın Başlat** butonuna bas (ana ekran üst kısmı)
2. Chrome'da Instagram/TikTok/Facebook canlı yayın sayfanı aç → eklenti otomatik bağlanır
3. YouTube live'sini başlat (handle eşleştiyse otomatik bulunur)
4. OBS'de **Yayını Başlat**

Chat overlay'inde mesajlar görünmeye başladığında her şey çalışıyordur.

## 6. Sorun Giderme

### Logları görüntüleme
**Ayarlar** dialog'unda **Logları Aç** butonu Explorer'da log klasörünü açar:
`%USERPROFILE%\Documents\OrderDeck\Logs`

### Chat boş gözüküyor
1. Yayın aktif mi? Üst sağdaki **chat health** noktasına bak: 🟢 yeşil = chat geliyor, 🟡 sarı = sessizlik, ⚪ gri = yayın yok
2. Eklenti bağlı mı? `http://localhost:4748/_health` URL'sine git → `{"connected": true}` görmeli
3. YouTube handle doğru mu? **Ayarlar → YouTube** sekmesinden kontrol et

### "Port 4747 zaten kullanımda" hatası
Başka bir OrderDeck instance açık. Görev Yöneticisi'nden `OrderDeck.App.exe` process'lerini kapat, tekrar başlat.

### Lisans sunucusu erişilemiyor
1 saatten uzun sürerse ana ekranda sarı banner çıkar. Çevrimdışı modda 14 gün boyunca yayın yapabilirsin; sürec içinde lisans sunucusuyla bağlantı kurulduğunda otomatik düzelir.

## 📞 Destek

- **Email**: destek@orderdeckapp.com
- **GitHub Issues**: [github.com/Ulysses07/OrderDeck/issues](https://github.com/Ulysses07/OrderDeck/issues)

Sorun bildirirken loglarını ekle (`%USERPROFILE%\Documents\OrderDeck\Logs` klasöründen son `log-*.txt`).
