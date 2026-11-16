# Instagram çoklu hesap sohbeti — Tasarım

**Tarih:** 16.11.2026 · **Durum:** TASLAK, sahip onayı bekliyor (öneriler §3.2'de, açık sorular §11.1'de); bağımsız doğrulamanın olgu düzeltmeleri işlendi (§13 sonu) · **İlgili:** `2026-08-04-instagram-live-graph-api-design.md` (tek hesaplı resmi API yolu; bu belge onun üzerine çoklu hesabı ekler)

Kaynaklar: LiveDeck `origin/master` 7960eba3 (dal `feat/instagram-coklu-hesap`). Satır numaraları bu kesite göredir. Sahada v0.9.12. Dört kollu keşif (WPF, sohbet tüketicileri, sunucu, Meta belgeleri) yapıldı; üç aday tasarım iki hakemle karşılaştırıldı. İki hakem de "en küçük güvenli değişiklik" tasarımını seçti. Bu belge onu temel alır, diğer iki tasarımdan hakemlerin istediği parçaları aşılar ve hakemlerin işaretlediği hataları koda bakarak düzeltir (eşleme §13).

## 1. Özet

Bugün OrderDeck tek bir Facebook Sayfası, dolayısıyla tek bir Instagram hesabı tutabiliyor. Bu tasarım token deposuna yalnız eklemeli bir "ek Sayfalar" listesi koyar; birincil Sayfa bugünkü alanlarda kalır, Facebook canlı yorum ve moderasyon yolu satır satır aynı kalır. Instagram servisi bağlı her Sayfa için ayrı bir yoklama döngüsü çalıştırır; bir hesabın hatası yalnız o hesabı bekletir. İki ve üzeri hesapta her Instagram yorumu hangi hesaptan geldiğini taşır ve sohbet satırında küçük bir etiketle görünür; hesap başına sağlık durumu sohbet panelinin başındaki şeritte görünür, üst bardaki sohbet noktası ve kenar çubuğundaki Instagram noktası da bu durumdan beslenir. Hesaplar arası sıra iki adımda gelir: ilk PR'da varış sırası ve ölçüm, ikinci PR'da ortak tık + 400 ms bariyer + Meta damgasına göre birleştirme. Tek hesaplı kurulumda yoklama yolu, istek sayısı ve gecikme bugünküyle aynıdır. Sunucu, yerel SQLite, overlay ve uzantı değişmez.

## 2. Niyet ve neden

Burak aynı anda iki farklı Instagram hesabından canlı yayın açıp iki hesabın da sohbetini OrderDeck'e çekmek istiyor; iş acil. Bugün ikinci hesap bağlanamıyor: bağlıyken "Facebook'a Bağlan" düğmesi kapalı (`OrderDeck.App/ViewModels/SettingsViewModel.cs:105`), bağlantıyı kaldırıp yeniden bağlanmak ise eski Sayfanın üzerine yazıyor (`OrderDeck.Chat/Facebook/FacebookOAuthService.cs:217-225`).

Bu belge yalnız bu ihtiyacı karşılar. Burak'ın söylemediği hiçbir ihtiyaç (rapor kırılımı, ikinci Facebook kullanıcısı, "!kayıt") kapsama alınmadı.

## 3. Söylenenler ve öneriler

### 3.1 Burak'ın söyledikleri (KESİN)

- Aynı anda iki farklı Instagram hesabından canlı yayın açıp iki hesabın da sohbetini OrderDeck'e çekmek istiyor. Acil.
- "!kayıt → DM" özelliğini kullanmıyor (hesabı sıkıntıya sokabiliyor). Sonuç: sunucu (`OrderDeck.LicenseServer`) kapsam dışı, dokunulmaz.
- İki Instagram hesabı da aynı işletme portföyünde. Biri bir Facebook Sayfasına bağlı; diğeri hiçbir Facebook Sayfasına bağlı değil. Burak yalnız bu bugünkü durumu söyledi; ikinci hesabı bir Sayfaya bağlayacağını ya da iki Sayfayı kimin yöneteceğini söylemedi (bkz. Ö6).

### 3.2 Tasarımcının önerileri (ÖNERİ — Burak henüz açıkça onaylamadı)

- **Ö1.** İki hesapta aynı mezat yayınlanır; iki sohbet tek akışta birleşir; her Instagram yorumunda hangi hesaptan geldiğini gösteren küçük bir etiket olur.
- **Ö2.** Sıra: iki hesap aynı anda sorgulanır, yeni yorumlar Meta zaman damgasına göre sıralanıp yayımlanır. Meta damgası saniye çözünürlüklüdür: aynı saniyedeki iki yorumun hesaplar arası sırası kesin bilinemez (§8.3.6'da dürüstçe belgelendi).
- **Ö3.** Hesaplardan biri koparsa (kalıcı hata, hız sınırı, uzun süre başarısız yoklama) operatör bunu ana ekranda görür. Bugün Instagram API yolunda hiç sağlık göstergesi yok.
- **Ö4.** Ayarlar'da tek Facebook girişiyle birden çok Sayfa seçilir; bağlı Instagram hesapları liste olarak görünür; hesap başına kaldırma vardır.
- **Ö5.** Tasarım N hesaba geneldir; doğrulama ve pilot 2 hesapla yapılır.
- **Ö6 (varsayım; tasarımın temeli).** Burak ikinci hesap için ayrı bir Facebook Sayfası açıp Instagram hesabını o Sayfaya bağlar ve iki Sayfayı aynı Facebook kullanıcısı yönetir (tek Facebook girişi). Burak bunu söylemedi; §5, §7.1 ve §8'in tamamı buna dayanır. "İki hesap = iki Sayfa" Meta'nın genel kuralı değil, OrderDeck'in kullandığı giriş türünün (Facebook Login) sonucudur (§5 madde 1–2). Burak ikinci hesabı Sayfasız tutmak isterse bu tasarım o hesabı okuyamaz: Sayfasız yolda canlı yorum okuma belgeli değildir, ayrı araştırma ve büyük olasılıkla ayrı App Review gerekir (§7.1 D). Teyidi ve alternatifi §11.1 soru 1.

Bu önerilerin dışında tasarımcının sahibe sormadan verdiği kararlar metinde **(TK1)**, **(TK2)** … biçiminde işaretlidir; listesi §11.2'de.

## 4. Hedefler, kapsam dışı, anti-hedefler

Bu bölümdeki hedefler, kapsam dışı maddeler ve anti-hedefler tasarımcının önerisidir; Burak'ın kendi cümleleri yalnız §3.1'dedir. Belge onaylanırken bunlar da onaylanmış olur.

**Hedefler**
1. İki (genelde N, en çok 4) Instagram hesabının canlı yorumları aynı anda OrderDeck sohbetine akar.
2. Operatör her Instagram yorumunun hangi hesaptan geldiğini satırda görür.
3. Canlı bir hesap sustuğunda operatör bunu ana ekranda en geç yaklaşık 15 saniye içinde görür; hiç akmamış hesap için ayrı kural vardır (§8.5).
4. İkinci hesap Ayarlar'dan, mevcut Facebook girişi bozulmadan eklenir ve tek başına kaldırılır.
5. Hesaplar arası sıra Meta damgasına göre kurulur (ikinci PR); sınırları belgelidir.

**Kapsam dışı**
- Facebook canlısını iki Sayfadan açmak. Facebook yorumları ve moderasyon bugünkü gibi tek "birincil" Sayfayla çalışır.
- Raporda, etikette (basılı) ya da siparişte hesap kırılımı. Yerel SQLite göçü ve sunucu şeması değişmez; kaynak hesap yalnız bellekteki sohbet mesajında durur.
- İkinci bir Facebook kullanıcısıyla giriş (Ö6 varsayımına bağlı).
- Sayfasız yol (Instagram Login / Unified Login). Ö6 varsayımına bağlıdır: Burak ikinci hesabı Sayfasız tutmak isterse bu madde yeniden açılır (§11.1 soru 1).
- Webhook.
- "!kayıt → DM".
- Instagram izleyici sayısı.

**Anti-hedefler** (ihlali kabul edilmez)
- **AH1.** Güncellemede mevcut kurulumların Facebook/Instagram bağlantısı düşmez. Token deposu geriye uyumludur; eski sürüme dönüşte ne olacağı belgelidir (§8.1, §10).
- **AH2.** Bir hesabın hatası öbürünün sohbetini kesmez.
- **AH3.** Bir hesabın sessizce susması operatörden gizli kalmaz.
- **AH4.** Tek hesaplı kullanıcıların bugünkü davranışı değişmez: aynı gecikme, aynı yük. **Bu belgenin okuması (TK19; tasarımcının kararı, §11.2):** yoklama yolu, sohbet akışı, gecikme ve Graph yükü tek hesapta bugünküyle aynıdır (§8.3.2 "Tek hesapta korunanlar"). Buna karşın tek hesaplı kurulumda da görünen yedi davranış bilerek değişir; hepsi §8.3.2 "Bilerek değişen davranışlar"da sayılıdır. Burak dar okumayı onaylamazsa reddettiği maddeler 2 ve üzeri Sayfa koşuluna bağlanır (Ayarlar'daki iki yeni düğme hariç; ikinci hesap onlarla eklenir).
- **AH5.** Facebook canlı yorum ve moderasyon yolu bozulmaz.

## 5. Ön koşullar (Meta tarafı — Burak yapar)

Sıra önemlidir; 1–3 OrderDeck'e dokunmadan önce yapılır. Madde 2 ve 3, Ö6 varsayımının gereğidir; Burak bunları yapacağını söylemedi (§11.1 soru 1).

1. **İkinci Instagram hesabı profesyonel olmalı** (işletme ya da içerik üretici). Facebook Login yolunda hesabın bir Facebook Sayfasına bağlı olması şart. Kaynak: <https://developers.facebook.com/documentation/instagram-platform/overview.md>.
2. **İkinci hesap için ayrı bir Facebook Sayfası açılır ve Instagram hesabı o Sayfaya bağlanır.** API'de bir Sayfa tek Instagram hesabı döndürür (`instagram_business_account` tekil nesne). Kaynak: <https://developers.facebook.com/documentation/instagram-platform/instagram-graph-api/reference/page.md>. Açık bir "1 Sayfa = 1 Instagram hesabı" cümlesi resmi kaynakta **doğrulanamadı** (yardım sayfalarının gövdesi okunamadı); dayanak API şemasıdır.
3. **Aynı Facebook kullanıcısı iki Sayfada da yetkili olmalı** (yönetici eşdeğeri görev). Kaynak: aynı `overview.md`. Tek kullanıcı token'ıyla `GET /me/accounts` kullanıcının görev yapabildiği bütün Sayfaları, her birinin token'ı ve bağlı Instagram hesabıyla döndürür. Kaynak: <https://developers.facebook.com/documentation/instagram-platform/instagram-api-with-facebook-login/business-login-for-instagram.md>.
4. **OrderDeck'te ekleme** (§8.6.1): önce "Instagram hesabı ekle" (tarayıcı açılmaz). Yeni Sayfa listede yoksa "Facebook izinlerini güncelle"; açılan Facebook onay ekranında **iki Sayfa ve iki Instagram hesabı da işaretli** bırakılır. Girişte çoklu varlık seçimi resmi belgede anlatılmıyor. Şu ikisi **doğrulanmadı**: (a) ilk onayda yalnız tek Sayfa seçildiyse ya da Sayfa sonradan açıldıysa saklı girişin yeni Sayfayı görüp görmeyeceği; (b) yeniden onayda yalnız yeni Sayfa işaretlenirse eski Sayfanın erişiminin düşüp düşmeyeceği. Tasarım ikisine de hazırlıklı (§8.2).
5. **İkinci hesap canlı yayın açabilmeli.** İkincil kaynaklara göre (Ağustos 2025 haberleri) canlı yayın için hesabın herkese açık ve en az 1.000 takipçili olması gerekiyor; resmi Yardım Merkezi sayfası okunamadı, **doğrulanmadı**.
6. **İki hesaba aynı anda yayın verme yöntemi OrderDeck dışıdır.** Resmi bir kural ya da yasak bulunamadı. Eski tarihli tek resmi metin (02.11.2022) Live Producer'da eşzamanlı yayının yerel desteklenmediğini, üçüncü parti yazılımla yapılabildiğini söylüyor: <https://about.instagram.com/blog/tips-and-tricks/instagram-live-producer>.
7. **Pilot öncesi teyit:** sunucudaki "!kayıt" bot bayrağı (`InstagramDmBotEnabled`) Burak'ın müşteri kaydında kapalı olmalı. Tarayıcılı her onay sunucudaki code takasını çağırır ve takas sırasında bot bağlama denemesi koşar (`OrderDeck.LicenseServer/Controllers/Facebook/FacebookOAuthController.cs:100-103`); bayrak kapalıyken hiçbir şey saklanmaz (`OrderDeck.LicenseServer/Services/Instagram/InstagramAccountService.cs:58-60`), açıkken Instagram'lı ilk Sayfa bota bağlanır (`:89-97`). Bu bugünkü "Facebook'a Bağlan" akışında da böyledir; yeni bir yol açılmıyor. Prod'daki değer doğrulanmadı; pilotun ilk adımıdır (§9.5): sorguyu tasarımcı yazar, Burak çalıştırır.

**Bilinen belge/kod farkları (değiştirilmez)**
- Kota: 24 saatte 4800 × gösterim, uygulama + uygulama kullanıcısı çiftine özel. Kaynak: <https://developers.facebook.com/docs/graph-api/overview/rate-limiting/>. İki hesabın aynı sayacı paylaşıp paylaşmadığı belgeden kesinleşmiyor; pilotta `X-Business-Use-Case-Usage` başlığıyla ölçülür (§9.5). Formül gösterime bağlı olduğundan yeni ya da az gösterimli ikinci hesapta sınır düşük olabilir. **Birim uyarısı:** aynı sayfaya göre başlıktaki `call_count` çağrı sayısı değildir; izin verilen çağrıların **yüzdesidir** ve **kayan 1 saatlik** pencereyi anlatır. `total_cputime` ve `total_time` da yüzdedir ve onlar da kısıtlamaya yol açabilir (formülleri yayımlanmamış); `estimated_time_to_regain_access` dakikadır. Yani başlıktaki rakam 24 saatlik "4800 × gösterim" formülüyle doğrudan karşılaştırılamaz (16.11.2026'da sayfadan yeniden okundu).
- `live_media` belgesi "User" token diyor; kod Sayfa token'ı kullanıyor (`OrderDeck.Chat/Ingestors/Instagram/InstagramLiveCommentsPoller.cs:59`, `:140`). Çalışıyor; ikinci hesapta da aynı belgesiz davranışa yaslanılır. Kaynak: <https://developers.facebook.com/documentation/instagram-platform/instagram-graph-api/reference/ig-user/live_media.md>.
- Graph API sürümü v22.0 (`OrderDeck.Chat/Facebook/FacebookOAuthDefaults.cs:22`), 20.05.2027'ye kadar geçerli. Kaynak: <https://developers.facebook.com/docs/graph-api/changelog/versions>.

## 6. Bugünkü yapı

### 6.1 Harita

| Parça | Yer | Ne yapıyor |
|---|---|---|
| Token demeti | `OrderDeck.Chat/Facebook/FacebookTokenBundle.cs:25-30` | Beş alan: kullanıcı token'ı, tek `PageId`, tek Sayfa token'ı, Sayfa adı, `ExpiresAt` |
| Token deposu | `OrderDeck.Chat/Facebook/EncryptedFacebookTokenStore.cs:33`, `:36-44`, `:46-72` | Tek dosya `facebook-token.bin`, DPAPI; `File.WriteAllBytes` ile üzerine yazar; çözülemeyen şifre ya da bozuk JSON'da dosyayı siler |
| OAuth servisi | `OrderDeck.Chat/Facebook/FacebookOAuthService.cs:75`, `:114-119`, `:140-234`, `:239-245` | Tek `_bundle`; `GetPageCredentialsAsync` tek çift döner; `ConnectAsync` tek Sayfa seçtirir; `DisconnectAsync` her şeyi siler |
| Sayfa listeleme / token türetme | `FacebookOAuthService.cs:339-357`, `:359-371` | `/me/accounts?fields=id,name` (sayfalama izlenmez); `/{page-id}?fields=access_token` herhangi bir Sayfa için genel |
| Instagram servisi | `OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs:94-202` | Tur başına tek kimlik (`:112`), tek hesap çözümü (`:126-127`), tek poller (`:144-150`), tek `_pollerCts` (`:44`, `:153`) |
| Poller | `OrderDeck.Chat/Ingestors/Instagram/InstagramLiveCommentsPoller.cs:135-217` | `GET /{ig-user-id}/live_media`; 1 sn taban, taşmada 0,3 sn'ye kadar (`:32-38`, `:123-133`); yayın yokken 10 sn (`:41`) |
| Filigran | `OrderDeck.Chat/Ingestors/Instagram/InstagramCommentWatermark.cs:54-98` | Poller örneğine özel; ilk çekimi yutar (`:68-73`); hesap içi sıra damga + kimlik (`:123-127`) |
| Hesap çözümleyici | `OrderDeck.Chat/Ingestors/Instagram/InstagramAccountResolver.cs:44-101` | Tek girdilik önbellek (`:34-35`); "hesap yok" ve "çağrı düştü" aynı `null` (`:59-65`, `:68-76`, `:96-100`) |
| Sohbet mesajı | `OrderDeck.Core/Chat/ChatMessage.cs:5-14` | Dokuz alan; kaynak hesap alanı yok |
| DI kaydı | `OrderDeck.App/AppHost.cs:254-266`, `:268-282`, `:303-310` | `instagram-graph` havuzu (`MaxConnectionsPerServer = 4`), tek depo, tek OAuth servisi, tek Instagram hosted service |
| Başlatma | `OrderDeck.App/Startup/WpfStartupEnvironment.cs:189-197` | Kayıtlı bütün `IHostedService`'ler genel döngüyle başlar |
| Facebook canlı yorum | `OrderDeck.Chat/Ingestors/Facebook/FacebookChatHostedService.cs:200` | Tekil `GetPageCredentialsAsync` |
| Facebook moderasyon | `OrderDeck.Chat/Facebook/FacebookModerationService.cs:116-123` | Her çağrıda tekil `GetPageCredentialsAsync` (sil `:60-62`, banla `:81-84`) |
| Sohbet satırı | `OrderDeck.App/Views/Shell/ChatPanel.xaml:73-123` | Rozet + ad + mesaj; hesap bilgisi yok |
| Kenar çubuğu bağlantıları | `OrderDeck.App/ViewModels/MainShellViewModel.cs:164-171`, `:196-206` | Dört sabit satır; yalnız `ViewerCountTracker`'dan beslenir. Instagram API yolu hiç rapor etmediği için Instagram noktası bugün hiç yanmıyor |
| Ayarlar | `OrderDeck.App/ViewModels/SettingsViewModel.cs:102-118`, `:362-414`, `:416-477`; `OrderDeck.App/Views/Pages/SettingsPage.xaml:279-324` | Tek durum metni, tek Instagram satırı |
| Sayfa seçici | `OrderDeck.App/Views/Drawers/FacebookPagePickerDrawer.xaml:57`, `.xaml.cs:21-43` | Tek seçimli |

### 6.2 Tek hesap varsayımları

1. Demet tek Sayfa tutar; depo tek blob'dur.
2. OAuth servisinde tek `_bundle`, tekil kimlik okuma; bağlıyken "Bağlan" kapalı (`SettingsViewModel.cs:105`).
3. Instagram servisi tur başına tek poller kurar; `SessionEnded` yalnız onu keser (`InstagramChatHostedService.cs:66-70`).
4. Kalıcı hata beklemesi (5 dk) ve çöküş sayacı servis genelidir (`InstagramChatHostedService.cs:101`, `:182-192`).
5. Çözümleyici önbelleği tek Sayfalıktır.
6. Sohbet mesajı hesap taşımaz; satırda hesap görünmez.
7. Instagram API yolunun ana ekranda hiçbir sağlık göstergesi yoktur; poller'ın `FatalReason`'ı yalnız loglanır (`InstagramChatHostedService.cs:182-187`).
8. Ayarlar tek satır gösterir.

### 6.3 Zaten uygun olanlar (değişmez)

- Poller, filigran, ayrıştırıcı ve hata sınıflandırıcı örnek bazlıdır; statik değişken durum yok (`InstagramLiveCommentsPoller.cs:58-69`).
- `ChatBus.Publish` ve `SpamFilter` eşzamanlı yayımcılara dayanıklıdır (`OrderDeck.Core/Chat/SpamFilter.cs:46`, `:94-104`).
- Müşteri kimliği (Platform, Username) çiftidir; Instagram kullanıcı adı hesaplar arası aynı olduğundan aynı izleyici iki yayında da tek müşteri satırına düşer.
- Sohbet abonesi yalnız kuyruğa ekler (`MainShellViewModel.cs:1257-1265`); overlay kendi tel modelini elle kurar (`OrderDeck.Overlay/OverlayHost.cs:423-437`), yani yeni alan OBS'e sızmaz.
- Sipariş operatörün sohbet satırına tıklamasıyla yazılır; hesap bilgisine ihtiyaç duymaz.

## 7. Yaklaşımlar

### 7.1 Bağlantı modeli

| Yaklaşım | Bedel | Sonuç |
|---|---|---|
| **A. Tek giriş + birincil Sayfa + ek Sayfa listesi** | Demete bir isteğe bağlı liste; OAuth servisine eklemeler; Facebook yolu ve depo biçiminin eski kısmı aynen | **SEÇİLDİ** |
| B. İkinci bağımsız bağlantı yuvası (ikinci blob, ikinci giriş) | İkinci OAuth turu, iki depo; N'e genellenmez. Aynı Facebook kullanıcısının ikinci onayının ilk Sayfanın erişimini düşürebilmesi riski keşifte "doğrulanmadı" olarak işaretli | Elendi |
| C. Giriş başına gruplu genel model (giriş listesi, her girişte kullanıcı token'ı + Sayfalar) | Birincil alanların yeri değişir: sürüm alanı + göç + eski sürüme dönüşte "bağlı ama kimlik null" tuzağı (`FacebookOAuthService.cs:94-96`, `:118`); Facebook canlı ve moderasyon tüketicileri de değişir. İkinci Facebook kullanıcısı zaten kapsam dışı | Elendi; gerekirse ileride A'nın üstüne eklenir |
| D. Sayfasız yol (Instagram Login / Unified Login) | `live_media` yalnız `graph.facebook.com` için belgeli; Sayfasız yolda canlı yorum okuma belgelenmemiş, yalnız webhook var. Webhook sunucu → masaüstü kanalı ve ayrı App Review ister | Elendi (kapsam dışı); Ö6 varsayımı düşerse yeniden ele alınır |

**Neden A:** AH1 ve AH5'i yapısal olarak korur. Birincil Sayfa bugünkü alanlarında kaldığı için eski sürüm aynı dosyayı okur, Facebook canlı yorum ve moderasyon kodu ile okudukları yöntem hiç değişmez. **Varsayım (Ö6; Burak onaylamadı, §11.1 soru 1):** ikinci hesap ayrı bir Sayfaya bağlanır ve iki Sayfayı aynı Facebook kullanıcısı yönetirse Burak'ın durumu tam bu modeldir. Varsayım tutmazsa (ikinci hesap Sayfasız kalır ya da ikinci Sayfayı başka bir Facebook kullanıcısı yönetir) A ikinci hesabı okuyamaz.

### 7.2 Instagram toplama

| Yaklaşım | Bedel | Sonuç |
|---|---|---|
| **Hesap başına bağımsız döngü, poller'a yalnız isteğe bağlı kancalar** | Poller'da yaklaşık 25 satır ekleme; tek hesap yolu bugünkü döngü | **SEÇİLDİ** |
| Tek "pompa": poller'ın döngüsü yeni bir sınıfa taşınır | Tek hesaplı kurulumlar da yeni koddan geçer; mevcut dört döngü testi (`OrderDeck.Tests/Chat/Instagram/InstagramLiveCommentsPollerTests.cs:85-180`) yeniden yazılır | Elendi (AH4) |
| Tek hesapta eski döngü, çok hesapta ayrı koordinatör | Hesap yaşam döngüsü (çözümleme, kalıcı hata, geri çekilme) iki yerde yazılır ve zamanla ayrışır | Elendi |

### 7.3 Hesaplar arası sıra

| Yaklaşım | Ek gecikme | Sonuç |
|---|---|---|
| Varış sırası (bugün platformlar arası olan) | Sıfır | İlk PR (PR-A), ölçümle birlikte |
| Tutma tamponu: bir yorum, öteki canlı hesap o yorumdan sonra başlamış bir turu bitirene dek tutulur | Ortalama yaklaşık 1 sn, tavan 2,5 sn (hesap, ölçülmedi). Güvencesi en güçlüsü; ama bütün Instagram yorumlarını TikTok/YouTube yorumlarına göre de sistematik geciktirir | Elendi |
| **Ortak tık + bariyer + Meta damgasına göre birleştirme** | İki yanıt arasındaki fark; tavan 400 ms | **SEÇİLDİ**, ikinci PR (PR-B) |

Hakemler bu noktada ayrıştı: biri tutma tamponunun güvencesini öne çıkardı, öbürü gecikme maliyeti yüzünden 400 ms'lik bariyeri istedi. Karar gerekçesi: `MainShellViewModel.cs:1241-1245` yorumu yaklaşık 120 ms üstündeki gecikmenin mezatçı tarafından fark edildiğini söylüyor; 1 saniyelik sistematik gecikme "ilk yazan alır"da Instagram izleyicisini diğer platformlara karşı da geri düşürür. İki hesap da sağlıklıyken ortak tıkın kaçırdığı sınıf, iki anlık görüntü arasındaki farkta yazılmış yorumlardır (fark ölçülmedi, bariyerle sınırlı; bütün sınırlar §8.3.6'da) **(TK7)**.

## 8. Tasarım

### 8.1 Token deposu (a)

**Biçim.** Aynı dosya (`facebook-token.bin`, `EncryptedFacebookTokenStore.cs:33`), aynı DPAPI kapsamı, aynı JSON. Tek fark: kök nesnede isteğe bağlı yeni özellik.

```csharp
public sealed record FacebookLinkedPage(string PageId, string PageAccessToken, string PageName)
{
    public override string ToString();   // token basmaz
}

public sealed record FacebookTokenBundle(
    string UserAccessToken,
    string PageId,               // birincil Sayfa (bugünkü anlam)
    string PageAccessToken,      // birincil Sayfa
    string PageName,             // birincil Sayfa
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<FacebookLinkedPage>? ExtraPages = null)   // yalnız EK Sayfalar
{
    public override string ToString();   // token basmaz
}
```

- Birincil Sayfa yalnız üst düzey alanlarda durur; `ExtraPages` birincili içermez. Aynı Sayfa iki yerde tutulmadığı için tutarsızlık sınıfı baştan yoktur.
- Kayıtların otomatik `ToString`'i bütün alanları yazar (token dahil; çalıştırılarak doğrulandı). İki kayıtta da override edilir. Demet bugün hiçbir yerde loglanmıyor; bu önlem dokunulan kayıtta sızıntı yolunu kapatır.
- Mevcut beş argümanlı kurucu çağrıları (üretimde `FacebookOAuthService.cs:217-222`, testlerde `OrderDeck.Tests/Chat/Facebook/EncryptedFacebookTokenStoreTests.cs`) derlenmeye devam eder.

**Sürümleme (TK1).** Sürüm numarası alanı eklenmez. Değişiklik salt eklemelidir ve hiçbir okuma dalı sürüme bakmak zorunda değildir. Kalıcı sözleşme: "üst düzey beş alan = birincil Sayfa". Bir gün bu alanların yeri ya da anlamı değişirse sürüm alanı o zaman eklenir.

**Eski blob'dan geçiş.** Göç adımı yoktur. Eski blob'da `ExtraPages` olmadığı için alan `null` gelir ve tek hesap olarak okunur. Dosya güncellemede yeniden yazılmaz; yalnız Bağlan / Instagram hesabı ekle / Kaldır / Facebook izinlerini güncelle anında yazılır. Güncellemeden hemen sonra dosya bayt bayt aynıdır (AH1).

**Eski sürüme dönüş (yeni blob → 0.9.12).** Depo ve demet dosyaları 25.06.2026'dan (2ea906ed) beri değişmedi; sahadaki sürümler aynı serileştirici seçeneklerini kullanıyor (yalnız `WriteIndented = false`, `EncryptedFacebookTokenStore.cs:23-26`). Eski sürüm bilinmeyen `ExtraPages` özelliğini atlar ve birincil Sayfayı görür:
- Facebook canlı yorum, moderasyon ve birincil Sayfanın Instagram'ı bugünkü gibi çalışır. Ek hesaplar yoklanmaz ve eski sürümde bunun göstergesi yoktur.
- Eski sürüm dosyaya dokunmadıkça yeniden yükseltmede ek hesaplar geri gelir.
- Eski sürümde "Bağlantıyı Kaldır" dosyayı siler (ekler de gider). Eski sürümde yeniden "Bağlan" dosyayı eksiz yazar; yükseltince ek hesap "Instagram hesabı ekle" ile yeniden eklenir.

**Doğrulama durumu.** Şu davranışlar .NET 10.0.400 üzerinde, aynı serileştirici seçenekleriyle, repo dışı bir betikle çalıştırılarak doğrulandı: eski beş alanlı JSON yeni kayda okunuyor (`ExtraPages` null); yeni JSON eski beş alanlı kayda okunuyor, birincil alanlar tam; `IReadOnlyList<kayıt>` `List` olarak geliyor; eksik alan istisna atmıyor, null geliyor. Aynı denemede tek Sayfalı demet de `"ExtraPages":null` anahtarıyla yazıldı; eski sürüm bu anahtarı da atladığı için zararsızdır, yani "tek hesapta dosya bugünküyle aynı şekil" iddiası kurulmaz, kurulan iddia "eski sürüm okur"dur. Bu deneme gerçek depo sınıfı ve DPAPI ile yapılmadı; ilk görev bu davranışları CI testiyle kilitlemektir (§9.1).

**Sağlamlık.**
- **Atomik yazım (TK2).** `SaveAsync` şifreli baytları aynı klasörde geçici bir dosyaya yazar, sonra `File.Move(geçici, hedef, overwrite: true)` ile yerine koyar. Bugün `File.WriteAllBytes` (`:42`); yarım yazım bir sonraki açılışta `CryptographicException` dalına düşer, dosya silinir (`:60-65`) ve bütün hesaplar kopar. Ekle/kaldır ile yazım sıklaşacağı için bu risk büyüyor. Depo sınıfındaki tek değişiklik budur; `LoadAsync` aynen kalır.
- Çözülemeyen şifre ya da bozuk JSON'da bugünkü davranış (dosya silinir, "bağlı değil") değişmez.
- Ek girdilerden biri eksik alanlıysa `GetConnectedPagesAsync` yalnız onu atlar (§8.2); birincil düşmez.

### 8.2 OAuth servisi ve Sayfa seçimi (b)

**Değişmeyen üyeler** (imza ve davranış aynı; hepsi birincil Sayfayı döner): `IsConnectedAsync` (`FacebookOAuthService.cs:92`), `GetConnectedPageNameAsync` (`:102`), `GetPageCredentialsAsync` (`:114`), `GetUserAccessTokenAsync` (`:128`), `ConnectAsync` (`:140`), `DisconnectAsync` (`:239`). Tek istisna: `ConnectAsync`'in tarayıcı beklemesi artık süresiz değildir (aşağıda "Tarayıcı beklemesi", TK17). Mevcut private `ListPagesAsync` (`:339-357`) aynen kalır: ilk bağlantının isteği değişmez.

**Birincil Sayfa kuralı (TK3).** Birincil Sayfa ilk "Facebook'a Bağlan"da seçilen Sayfadır. Facebook canlı yorum (`FacebookChatHostedService.cs:200`) ve moderasyon (`FacebookModerationService.cs:118`) hep onu okur. Birincil Sayfa tek başına kaldırılamaz ve yerinde değiştirilemez; değiştirmenin yolu bugünkü gibi "Bağlantıyı Kaldır" + "Facebook'a Bağlan"dır. Gerekçe: moderasyon kimliği her çağrıda yeniden okur, canlı akış ise bağlandığı Sayfada sürer; yayın ortasında birincil değişirse sil/banla yanlış Sayfanın token'ıyla giderdi. `AppSettings.FacebookPageId` (yalnız yazılıyor: `FacebookOAuthService.cs:230`, `:243`) birincili yansıtmaya devam eder.

**Eklenen üyeler**

```csharp
public const int MaxInstagramPages = 4;   // birincil dahil

// Birincil önce, sonra ekler (saklanan sırayla). PageId'ye göre tekil; boş kimlik/token'lı ek girdi atlanır.
// Bağlı değilse boş liste.
public Task<IReadOnlyList<FacebookLinkedPage>> GetConnectedPagesAsync(CancellationToken ct = default);

public sealed record FacebookAddablePage(
    string PageId, string PageName, string? IgUserId, string? IgUsername, bool AlreadyConnected);
public sealed record FacebookPageListing(IReadOnlyList<FacebookAddablePage> Pages, bool NeedsReauthorize);

// SAKLI kullanıcı token'ıyla, tarayıcı açmadan.
public Task<FacebookPageListing> ListAddablePagesAsync(CancellationToken ct = default);

// Saklı kullanıcı token'ıyla Sayfa token'ı türetir, ExtraPages'e ekler, kaydeder.
// Birincil ya da zaten ekli Sayfa için yazmadan mevcut girdiyi döner. Sınır aşılırsa InvalidOperationException.
public Task<FacebookLinkedPage> AddExtraPageAsync(string pageId, string pageName, CancellationToken ct = default);

// Yalnız ek listeden çıkarır. Birincil ya da bilinmeyen kimlikte false.
public Task<bool> RemoveExtraPageAsync(string pageId, CancellationToken ct = default);

public sealed record FacebookReauthorizeResult(
    int RefreshedPages, IReadOnlyList<FacebookLinkedPage> MissingPages);

// Tarayıcı onayını yeniden çalıştırır; ekleri korur, birincili değiştirmez.
public Task<FacebookReauthorizeResult> ReauthorizeAsync(
    Func<IReadOnlyList<FacebookLinkedPage>, CancellationToken, Task<bool>> confirmMissing,
    CancellationToken ct = default);
```

**Tarayıcısız listeleme.** `ListAddablePagesAsync` kendi isteğini kurar: `GET /me/accounts?fields=id,name,instagram_business_account{id,username}&limit=100` ve `paging.next` izlenir. Sunucu aynı alanı kullanıcı token'ıyla zaten çağırıyor (`InstagramAccountService.cs:75-77`). Listeleme tek bir private yardımcıdır (sayfalamalı); `ListAddablePagesAsync` ve `ReauthorizeAsync` onu paylaşır.

Hata eşlemesi bu uca özeldir. Graph hata kodu 190/463 (token), 200/10 (izin) **ve 100** (geçersiz alan/parametre) gelirse `NeedsReauthorize = true` ve boş liste döner. 100'ün ayrıca sayılmasının nedeni: mevcut `InstagramGraphError.Classify` 100'ü "yayın bitti" diye sınıflandırır (`InstagramGraphError.cs:71`); o anlam yalnız `live_media` için doğrudur, `/me/accounts`'ta alanın reddedildiğini gösterir. Sınıflandırıcının kendisi değişmez; bu uç `Classify` sonucuna ek olarak 100'ü kendisi eşler. Diğer hatalar (ağ, 5xx, kota) "Sayfalar okunamadı" metnine düşer.

Instagram izni olmayan eski oturumda Graph'ın alanı hata vermeden **yanıttan düşürdüğü** kodda kayıtlıdır (`InstagramPermissionProbe.cs:15-20`). O durumda bütün Sayfalar Instagram'sız görünür ve `NeedsReauthorize` kurulmaz; yönlendirmeyi arayüz yapar: mevcut izin sondası (`InstagramNeedsReconnect`, `SettingsViewModel.cs:386-395`) "izin yok" diyorsa "Instagram hesabı ekle" çekmece açmadan "Facebook izinlerini güncelle" metnini gösterir (§8.6.1). `/me/accounts` için bu davranışın kendisi doğrulanmadı; iki yol da (hata ya da sessiz düşme) aynı metne varır.

**Tarayıcısız ekleme.** `AddExtraPageAsync` mevcut private `GetPageAccessTokenAsync`'i (`:359-371`) çağırır. Sunucunun takas ucu bu yolda hiç çağrılmaz **(TK5)**.

**Yeniden onay (TK4).** `ReauthorizeAsync` sırasıyla:
1. Kullanıcı token'ını alır. `ConnectAsync`'in 0–5. adımları (`:144-193`: yapılandırma, URL, dinleyici, tarayıcı, state denetimi, code takası) private bir `AcquireUserTokenAsync` yardımcısına çıkarılır; `ConnectAsync` ve `ReauthorizeAsync` onu paylaşır. `ConnectAsync`'in imzası aynı kalır; dış davranışındaki tek fark aşağıdaki "Tarayıcı beklemesi" kurallarıdır.
2. Yeni token'la `/me/accounts` listesini okur. Yukarıdaki sayfalamalı listeleme yardımcısı kullanılır; sayfalamasız `ListPagesAsync` (`:339-357`) kullanılmaz, yoksa ilk yanıt sayfasına sığmayan bağlı bir Sayfa "eksik" sayılıp gereksiz onay çekmecesi açılırdı.
3. Bağlı Sayfalardan (birincil + ekler) yeni listede olmayanları bulur. Varsa `confirmMissing` çağrılır; `false` dönerse **diske hiçbir şey yazılmaz** ve `OperationCanceledException` fırlatılır.
4. Yeni listede görünen her bağlı Sayfanın token'ını yeniden türetir. Türetilemeyen Sayfanın eski token'ı kalır.
5. Yeni kullanıcı token'ı, yeni `ExpiresAt` ve yenilenen Sayfa token'larıyla tek yazım yapar. Listede görünmeyen Sayfanın girdisi **silinmez**: Meta erişimi gerçekten düşürdüyse o hesap sağlık şeridinde görünür (AH3); sessizce kaybolmaz. Hangi metinle görüneceği hatanın yakalandığı yere bağlıdır: poller çalışırken düşerse "bağlantı yenilenmeli" (`Faulted`); hesap çözümlemesi sırasında düşerse (uygulama açıldıktan sonraki ilk çözümleme aynı Sayfa token'ıyla yapılır ve çözümleyici her başarısız yanıtı tek sonuca indirir, `InstagramAccountResolver.cs:59-65`) "hesap bilgisi okunamadı" (`ResolveFailed`). İki ve üzeri Sayfada ikisi de sarıdır (§8.5).
6. Sayfa seçici açılmaz, otomatik birincil seçimi yapılmaz. `ConnectAsync`'teki "tek Sayfa dönerse onu seç" dalı (`:201-202`) bu yolda yoktur; birincil kayamaz.

Yalnız bağlıyken çağrılır. `confirmMissing` "Vazgeç" dese bile Meta tarafındaki izin o anda değişmiş olabilir; bu doğrulanmadı ve arayüz metni bunu söyler.

**Tarayıcı beklemesi (TK17).** Bugünkü bekleme yalnız geri çağrı ya da iptal belirteciyle biter; zaman aşımı yoktur (`WaitForCallbackAsync`, `:286-307`) ve Ayarlar belirteç vermez (`SettingsViewModel.cs:431-443`). Kullanıcı tarayıcı sekmesini kapatırsa uygulama bunu **algılayamaz**: komut süresiz "onay bekleniyor"da kalır ve 4849 dinleyicisi açık kalır (`:163-167`); bu sırada başlatılan ikinci bir akış "Failed to bind" ile düşer (`:169-175`). Bu bugünkü "Facebook'a Bağlan"da da böyledir (düğme meşgul kaldığı için uygulama yeniden başlatılana dek bağlanılamaz, `SettingsViewModel.cs:105`, `:422`); ama bu tasarım yeniden onayı ikinci hesabı eklemenin asıl yolu yapıyor ve akış bağlıyken, yayın öncesinde kullanılacak. Paylaşılan `AcquireUserTokenAsync` bu yüzden üç kural taşır:
- **Zaman aşımı.** Geri çağrı 5 dakika içinde gelmezse dinleyici kapatılır ve Türkçe mesajlı bir `TimeoutException` fırlatılır. Diske hiçbir şey yazılmamıştır. Süre servis içinde tek sabittir; testte kısaltılabilir (§11.3).
- **İptal.** Çağıranın verdiği belirteç bugünkü gibi dinleyiciyi kapatır (`:290-293`) ve `OperationCanceledException` üretir. `ReauthorizeFacebookCommand` beklerken görünen bir "İptal" düğmesiyle bu belirteci tetikler (§8.6.1). `ConnectFacebook` komutu değişmez; o yol zaman aşımıyla korunur.
- **Aynı anda tek tarayıcı akışı.** Bekleyen bir akış varken ikinci `ConnectAsync` / `ReauthorizeAsync` çağrısı dinleyiciyi açmayı denemeden, anlaşılır bir `InvalidOperationException` ile reddedilir. Arayüzde akış beklerken "Facebook'a Bağlan", "Facebook izinlerini güncelle" ve "Instagram hesabı ekle" pasiftir.

Facebook onay ekranında "İptal"e basmak bir geri çağrı üretir: `error=access_denied` gelir ve bugünkü kod bunu `InvalidOperationException` yapar (`:330-332`). Bu eşleme değiştirilmez; arayüzde "iptal" değil "başarısız" metnine düşer (§8.6.1 "Facebook izinlerini güncelle" madde 6).

**İlk bağlantı ve tümünü kaldırma.** `ConnectAsync` yalnız bağlı değilken erişilebilir (`SettingsViewModel.cs:105`) ve demeti eksiz kurar (`FacebookOAuthService.cs:217-222`). "Bağlantıyı Kaldır" bugünkü gibi her şeyi siler (ekler dahil).

**Eşzamanlılık.** Demeti oku–değiştir–yaz yapan bölümler (`AddExtraPageAsync`, `RemoveExtraPageAsync`, `ReauthorizeAsync`'in kaydetme adımı, `ConnectAsync`'in kaydetme adımı, `DisconnectAsync`) tek bir `SemaphoreSlim(1, 1)` altında çalışır. Kilit tarayıcı beklemesini kapsamaz. `_bundle` değişmez kayıt olarak tek atamayla değiştirilir; okuyanlar kilitsizdir.

**Hata metni düzeltmesi.** `GetJsonAsync` boş gövde istisnasının mesajına URL'yi koyuyor (`:384-385`) ve URL `access_token` taşıyor. Yeni tarayıcısız yollar bu yardımcıyı kullanacağı ve hata metni Ayarlar durum satırına taşındığı için (`SettingsViewModel.cs:452-455` deseni) URL mesajdan çıkarılır.

**Üst sınır (TK13).** En çok 4 Sayfa. Gerekçe: `instagram-graph` havuzu `MaxConnectionsPerServer = 4` (`AppHost.cs:265`) ve her poller aynı anda tek istek yapar.

### 8.3 Instagram toplama (c)

#### 8.3.1 Poller'daki değişiklik

Poller'ın döngüsü, aralık mantığı (`InstagramLiveCommentsPoller.cs:32-41`, `:123-133`), filigranı, ayrıştırıcısı, hata sınıflandırıcısı ve `Publish` eşlemesi (`:295-323`) değişmez. Eklenenler isteğe bağlı kurucu parametreleridir; hepsi verilmediğinde davranış bugünküyle özdeştir.

```csharp
public enum InstagramPollKind { Ok, NoBroadcast, Transient, RateLimited, Fatal }   // bugünkü private PollKind (:219)

public interface IInstagramPollObserver
{
    void RoundStarting();
    void RoundCompleted(InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader);
}

public interface IInstagramPollPacer          // PR-B
{
    Task WaitAsync(TimeSpan interval, CancellationToken ct);
}

public InstagramLiveCommentsPoller(
    string igUserId, string pageAccessToken, IChatBus bus, HttpClient http,
    ILogger<InstagramLiveCommentsPoller> log, SpamFilter? spamFilter = null,
    IInstagramPollObserver? observer = null,
    InstagramCommentWatermark? watermark = null,
    IInstagramPollPacer? pacer = null);         // PR-B
```

- `RunAsync` (`:135-217`) içinde üç gözlemci çağrısı: `PollOnceAsync`'ten (`:152`) önce `RoundStarting`; Ok dışı sonuçta hemen `RoundCompleted`; Ok'te yayım döngüsünden (`:198-199`) sonra `RoundCompleted`. Gözlemci istisnası yutulur ve Debug düzeyinde loglanır; döngüyü durduramaz.
- `PollOutcome`'a (`:221-226`) isteğe bağlı kota alanı eklenir; `PollOnceAsync` her yanıtta `X-Business-Use-Case-Usage` başlığını (bugün yalnız kota hatasında okunuyor, `:251`) ham metin olarak taşır.
- `watermark` verilirse poller onu kullanır (bugün `new()`, `:64`). Hesap döngüsü her poller kurulumunda taze bir filigran verir (bugünküyle aynı davranış) ve başvurusunu tutar; yalnız tek → çok geçişinde, eski poller durduktan sonra aynı filigranı yeni poller'a aktarır (§8.3.3).
- PR-B: Ok dalındaki `Task.Delay(interval, ct)` (`:202`) `pacer` verildiyse `pacer.WaitAsync(interval, ct)` olur. Başka hiçbir bekleme değişmez.
- Mevcut testlerdeki `Make(handler, bus)` (`InstagramLiveCommentsPollerTests.cs:40-42`) beş argümanlı kurucuyu kullanıyor; derlenmeye devam eder.

#### 8.3.2 Hosted service: üst döngü + hesap başına döngü

`InstagramChatHostedService` kurucusuna iki isteğe bağlı parametre eklenir: `InstagramHealthMonitor? health = null`, `TimeProvider? time = null` (repoda emsal: `OrderDeck.App/Services/Sync/SessionOrderSyncHostedService.cs:52-77`). Sabitler (`:27-30`) ve `ComputeBackoff` (`:205-212`) aynen kalır.

**Üst döngü** (bugünkü `RunAsync`'in kapıları, tek yerde):
1. `GetConnectedPagesAsync` boşsa: sağlık temizlenir, 60 sn beklenir (bugünkü `IdleWhenOffline`, `:112-117`). Bu kapı döngünün başındadır; oturum açıkken listenin boşalması 4. adımda işlenir.
2. Aktif oturum yoksa: 60 sn beklenir (bugünkü `:120-124`). Oturum kapısı yalnız burada durur; hesap döngüleri kapıyı ayrı ayrı beklemez. Böylece bütün hesaplar aynı anda başlar.
3. Oturum açıksa: sağlık izleyicisi güncel Sayfa listesiyle yapılandırılır ve oturumu sıfırlanır; **bağlı Sayfa sayısı** 2 ve üzeriyse bir birleştirme kapısı (§8.3.5) kurulur; her Sayfa için bir hesap döngüsü görevi başlatılır. Ölçüt bilerek Sayfa sayısıdır, çözülmüş Instagram hesabı sayısı değil: Sayfa sayısı oturum başında bellidir, hesap çözümlemesi ise hesap döngüsünün içinde ve gecikmeli olur. Sonucu: birincil Sayfası Instagram'sız olan ve tek Instagram hesabını ek Sayfadan okuyan kurulumda (bugün yok, §12 R16) tek hesabın satırları da etiket taşır; tek kulvarlı kapı gecikme eklemez.
4. İzleme: 2 saniyede bir, bellekten `GetConnectedPagesAsync` yeniden okunur ve PageId kümesi karşılaştırılır (§8.3.3). Oturum açıkken liste **boşalırsa** (yayın ortasında "Bağlantıyı Kaldır"): bütün hesap döngüleri durur, kapı kapanır, sağlık temizlenir; üst döngü 60 sn'lik beklemeye **düşmez**, 2 sn'lik izlemede kalır (yalnız bellek okuması, ağ yok). Liste yeniden dolunca 3. adım baştan uygulanır (Sayfa sayısına göre kapı kurulur ya da kurulmaz; filigranlar tazedir).
5. `SessionEnded` (`:66-70`) oturum kapsamlı iptal kaynağını tetikler: bütün hesap döngüleri durur, kulvarlar kapanır (tutulan mesaj varsa basılır), sağlık "kapalı"ya döner. Ardından 30 sn (`IdleAfterPollerExit`) beklenir ve döngü başa döner. Tek `_pollerCts` (`:44`) yerine oturum kapsamlı tek kaynak + hesap başına bağlı kaynaklar vardır.

**Hesap döngüsü** (bugünkü `:126-192` gövdesi, hesap başına bir kopya). Her hesabın kendi çözümleyicisi, poller'ı, filigranı, çöküş sayacı ve bekleme süresi vardır. Çözümleyiciler PageId başına servis ömrünce yaşar; başarılı çözümleme önbelleği oturumlar arasında korunur. Bugün de böyledir: çözümleyici servis başına bir kez kurulur (`:96-99`) ve önbelleği hiçbir üretim yolunda boşaltılmaz; aksi hâlde tek hesapta oturum başına bir Graph isteği eklenirdi (AH4).
1. Kimlik: o PageId'nin güncel token'ı `GetConnectedPagesAsync`'ten okunur; Sayfa listede yoksa döngü biter.
2. Çözümleme: `ResolveDetailedAsync` (§8.3.4). `NotLinked` ya da `Failed` ise sağlık bildirilir ve 5 dk beklenir. Süre bugünkü `IdleAfterFatal` ile aynıdır (`:128-135`) **(TK15)**.
3. Aynı `IgUserId` başka bir hesap döngüsünde zaten okunuyorsa bu Sayfa atlanır (çift yorum olmasın): Warning düzeyinde loglanır, bu Sayfanın sağlık girdisi silinir (aynı hesap öteki Sayfa üzerinden şeritte zaten görünür) ve 5 dk sonra yeniden bakılır.
4. Poller kurulur. Tek hesapta bus olarak bugünkü `_bus`, gözlemci olarak yalnız sağlık verilir; çok hesapta bus olarak kulvar, gözlemci olarak sağlık + kulvar verilir. Kulvar hesap döngüsüne aittir: ilk başarılı çözümlemeden sonra bir kez açılır, poller yeniden kurulurken aynı kulvar kullanılır, döngü çıkarken `Complete()` çağrılır. Kulvar etiketi gösterime hazır metindir: kullanıcı adı çözüldüyse `@kullanıcıadı`, çözülemediyse (`InstagramAccount.Username` null olabilir, `InstagramAccountResolver.cs:81-82`) Sayfa adı, "@"sız.
5. Poller bitince: `FatalReason` varsa 5 dk (bugünkü `:182-189`); çöküşte `ComputeBackoff`; aksi hâlde 30 sn (bugünkü `:191-192`). Sonra 1. adıma dönülür.
6. Kalıcı hata beklemesi, o Sayfanın saklı token'ı poller'ın kullandığından farklı hâle gelirse kesilir **(TK16)**: "Facebook izinlerini güncelle"den sonra 5 dakika beklenmez.

**Tek hesapta korunanlar (AH4, TK6).** Bağlı Sayfa sayısı 1 iken birleştirme kapısı kurulmaz; poller'a bugünkü `_bus` örneği verilir (`:144-150` ile aynı argümanlar), `pacer` verilmez, filigran her kurulumda tazedir, `SourceAccount` null kalır. Graph çağrıları, 1 sn / 0,3 sn aralık, yayın yokken 10 sn, kota beklemesi, 5 ardışık geçici hatada çıkış + 30 sn, kalıcı hatada 5 dk, ilk çekimin yutulması, oturum başında 60 sn'ye kadar geç başlama (bugün `SessionStarted` aboneliği yok; eklenmez) birebir aynıdır. Eklenen yük: bellek içi gözlemci çağrıları, 2 saniyede bir bellekten liste karşılaştırması, dakikada bir kota log satırı.

**Bilerek değişen davranışlar (tek hesapta da görünür; AH4'ün dar okuması, TK19).** Yoklama yolu, gecikme ve yük aynı kalır; aşağıdaki yedi davranış tek hesaplı kurulumda da değişir. Bu tasarımcının kararıdır (TK19); Burak'a teslim özetinde gösterilir, itiraz ettiği madde 2 ve üzeri Sayfa koşuluna bağlanır (5. madde hariç).
1. **Yayın ortasında "Bağlantıyı Kaldır" ya da hesap kaldırma, o hesabın yoklamasını en geç 2 saniyede durdurur (TK14).** Bugün çalışan poller eski token'la oturum sonuna kadar sürüyor (`:144-161`). Sonucu: yayın ortasında "Bağlantıyı Kaldır" + "Facebook'a Bağlan" yapılırsa bugün Instagram sohbeti kesilmezken, yeni tasarımda tarayıcı onayı boyunca kesilir. Yeniden bağlanınca hesap en geç 2 sn'de yeniden kurulur (üst döngü 4. adım) ama yeni poller'ın ilk çekimi yutulur; aradaki yorumlar gelmez. Bugünkü arayüz bu adımı Instagram izni için öneriyor (`SettingsPage.xaml:317`); yeni metin "Facebook izinlerini güncelle"ye yönlenir (6. madde) ve o yol bağlantıyı kaldırmaz, sağlıklı poller'ı da yeniden başlatmaz (§8.3.3). **Facebook tarafı farklı davranır:** açık canlı yorum akışı kurulurken aldığı Sayfa token'ıyla sürer (`FacebookChatHostedService.cs:257-264`); yalnız sil/banla düşer, çünkü moderasyon kimliği her çağrıda yeniden okur (`FacebookModerationService.cs:116-123`). Yani yayın ortasında bağlantı kaldırılınca Instagram yorumları 2 sn içinde kesilir, Facebook yorumları akmaya devam eder.
2. Kalıcı hata beklemesi token yenilenince kesilir (TK16).
3. Kenar çubuğundaki Instagram noktası yayında yanar (§8.6.4); bugün hiç yanmıyor.
4. Hesap o oturumda canlıyken koparsa sohbet panelinde sağlık şeridi ve sarı üst nokta görünür (§8.6.3–4).
5. Ayarlar'daki Instagram kartında iki yeni düğme görünür (§8.6.1). İkinci hesap bu düğmelerle eklendiği için 2 ve üzeri Sayfa koşuluna bağlanamaz.
6. `InstagramNeedsReconnect` uyarı metni "Bağlantıyı Kaldır + Facebook'a Bağlan" yerine "Facebook izinlerini güncelle"ye yönlenir (§8.6.1).
7. Tarayıcı onayı beklemesi ("Facebook'a Bağlan" dahil) 5 dakikada zaman aşımına uğrar; bugün süresizdir (§8.2, TK17).

#### 8.3.3 Yayın ortasında hesap ekleme, çıkarma, token değişimi (TK14)

- **Ekleme (N ≥ 2 iken):** yalnız eklenen hesabın döngüsü başlar; diğer hesapların poller'ına, filigranına ve temposuna dokunulmaz. Eklenen hesabın ilk çekimi bugünkü gibi yutulur (`InstagramCommentWatermark.cs:68-73`).
- **Çıkarma:** yalnız o hesabın döngüsü iptal edilir; kulvarı kapanır, sağlık girdisi silinir.
- **Tekten çoka geçiş (1 → 2):** çalışan hesabın poller'ı bir kulvarla yeniden kurulur. Filigranı yeni poller'a devredilir; ilk çekim yutulmaz, yorum kaybı olmaz.
- **Çoktan teke geçiş:** oturum içinde yapılmaz; kalan hesap kulvarıyla sürer (tek kulvarlı kapı gecikme eklemez, etiket basılmaya devam eder). Tek hesap kipine bir sonraki oturumda dönülür.
- **Tümünün kalkması (N → 0, "Bağlantıyı Kaldır"):** bütün döngüler durur, kapı kapanır; üst döngü 2 sn'lik izlemede kalır ve yeniden bağlanılınca kurulum baştan yapılır (§8.3.2 üst döngü 4. adım). Bu tam bir söküm olduğu için "çoktan teke" kuralının istisnası değildir.
- **Token değişimi (aynı PageId):** çalışan sağlıklı poller yeniden başlatılmaz; eski Sayfa token'ı geçerli olduğu sürece sürer. Yeni token bir sonraki doğal yeniden kurulumda kullanılır. Gerekçe: bozuk hesabı düzeltmek için yapılan izin güncellemesi sağlam hesabın sohbetini kesmemeli (AH2). Yeniden onaydan sonra eski Sayfa token'ının geçerli kalıp kalmadığı doğrulanmadı; kalmazsa poller kalıcı hatayla çıkar ve TK16 gereği yeni token'la hemen yeniden kurulur.

#### 8.3.4 Yardımcı eklemeler

```csharp
public enum InstagramResolveStatus { Found, NotLinked, Failed }

// ResolveAsync (:44) imzası ve davranışı aynen kalır; mevcut testleri değişmez.
public Task<(InstagramAccount? Account, InstagramResolveStatus Status)> ResolveDetailedAsync(
    string pageId, string pageAccessToken, CancellationToken ct);

// Üç oran da YÜZDEDİR (0–100+), çağrı sayısı değil; pencere kayan 1 saattir.
public sealed record InstagramBucUsage(
    string BusinessId,
    double CallCountPercent, double TotalCpuTimePercent, double TotalTimePercent,
    double? MinutesToRegainAccess);

// X-Business-Use-Case-Usage başlığını ayrıştırır. Yalnız ölçüm ve log; otomatik fren yok.
public static bool TryGetUsage(string? headerValue, out IReadOnlyList<InstagramBucUsage> usage);
```

Birim: başlıktaki `call_count`, `total_cputime` ve `total_time` sayaç değil yüzdedir; `call_count` kayan 1 saatlik pencereyi anlatır (§5 "Bilinen belge/kod farkları"). Alan adları bu yüzden `...Percent` taşır. `BusinessId` başlığın üst düzey anahtarıdır; belge onu "çağrıyı yapan token'la ilişkili işletmenin kimliği" diye tanımlar. `MinutesToRegainAccess` başlıktaki `estimated_time_to_regain_access` alanıdır; mevcut kod başlıktan yalnız bunu okuyor (`InstagramGraphError.cs:101-107`) ve `TryGetRetryAfter` aynen kalır.

Gerekçe: bugün "Sayfada Instagram hesabı yok" ile "çağrı düştü" aynı `null`'dur (`InstagramAccountResolver.cs:59-65`, `:68-76`, `:96-100`). Sağlık göstergesi yalnız Facebook kullanan kuruluma yanlış alarm vermemeli, yayın başındaki ağ kesintisine de yanlış sebep söylememelidir.

#### 8.3.5 Birleştirme kapısı: PR-A (etiket + ölçüm, düz geçiş)

Yalnız Sayfa sayısı 2 ve üzeriyken kurulur.

```csharp
public sealed class InstagramMergeGate : IDisposable
{
    internal static readonly TimeSpan HoldBudget = TimeSpan.FromMilliseconds(400);   // PR-B; PR-A'da sıfır

    public InstagramMergeGate(IChatBus inner, TimeProvider time, ILogger log, TimeSpan? holdBudget = null);
    public Lane AddLane(string accountKey, string label);

    public sealed class Lane : IChatBus, IInstagramPollObserver, IInstagramPollPacer   // tempo arayüzü PR-B
    {
        public void Complete();
    }
}
```

- Poller'a `IChatBus` olarak kulvar verilir; poller bunu bilmez. `Publish(m)` mesajı `m with { SourceAccount = label }` yapar; `Subscribe` ve `RecentMessages` iç bus'a devredilir.
- PR-A'da tutma yoktur: mesaj etiketlenip hemen iç bus'a basılır. Hesaplar arası sıra varış sırasıdır (bugün platformlar arasında olduğu gibi).
- **Ölçüm** (PR-B'nin gerekip gerekmediğini ve bariyer süresini rakamla belirler). Dakikada bir ve oturum sonunda tek Information satırı: hesap başına tur sayısı ve yanıt süresi p50/p95 (`RoundStarting` → `RoundCompleted`); **ters sıra sayacı** (iç bus'a basılan bir Instagram mesajının damgası, daha önce basılmış en büyük Instagram damgasından küçükse +1) ve en büyük fark (sn). Hesap içi damga hiç geri gitmediği için (`InstagramCommentWatermark.cs:76-83`) sayacın her artışı hesaplar arası ters sıradır.
- Kapı içindeki her hata yolunda mesaj doğrudan iç bus'a basılır: kapı mesaj kaybedemez.
- İç bus'a yayım kapının kilidi altında yapılabilir; aboneler yalnız kuyruğa ekliyor (`MainShellViewModel.cs:1257-1265`, `OverlayHost.cs:433-445`).

#### 8.3.6 Birleştirme kapısı: PR-B (ortak tık + bariyer + birleştirme)

Hesap döngüleri bağımsız kalır; kapı yalnız iki şeyi yönetir: Ok turundan sonraki beklemenin ne zaman biteceğini ve mesajların ne zaman basılacağını. Poller'daki tek ek, §8.3.1'deki tempo kancasıdır.

**Tanımlar.** *Tur:* bir tıkta birlikte serbest bırakılan kulvarların istekleri. *Üye:* o turda isteği süren kulvar.

**Kurallar**
- **K1 Ortak tık.** Ok turunu bitiren poller `WaitAsync(interval)` içinde bekler. Tık anı, bekleyen kulvarların (beklemeye giriş anı + istediği aralık) değerlerinin en küçüğüdür; açık tur varsa kapanışından önce atılmaz. Tıkta bekleyen bütün kulvarlar aynı anda serbest kalır ve yeni turun üyeleri olur.
- **K2 Bariyer ve birleştirme.** Üyelerin `Publish` ettiği mesajlar tutulur. Bariyer saati turda ilk mesaj tutulduğunda ya da ilk üye bitirdiğinde kurulur. Tur, üyelerin hepsi bitirince ya da bariyer saati `HoldBudget` (400 ms) dolunca kapanır. Kapanışta tutulanlar birleştirilip iç bus'a basılır.
- **K3 Geç kalan.** Kapanışta turu bitmemiş üye turdan düşer. Mesajları geldiğinde tur dışı sayılır (K4). Beklemeye girdiğinde sıradaki tıka katılır.
- **K4 Tur dışı.** Üye olmayan kulvardan gelen mesaj, açık tur varsa onun tamponuna girer ve kapanışta birleşir; yoksa hemen basılır. Ok dışı sonuçlar (yayın yok, kota, hata) poller'ın kendi beklemesiyle sürer; kimse onları beklemez.
- **K5 Emniyet.** Açık turda henüz hiçbir üye bitirmediyse, bekleyen kulvar kendi süresi dolunca tek başına serbest kalır ve açık tura üye olur. Sonuç: hiçbir kulvar başka bir kulvar yüzünden kendi aralığı + `HoldBudget`'tan uzun bekletilmez; hiçbir mesaj `HoldBudget`'tan uzun tutulmaz.
- **K6 Yavaş kulvar.** Art arda 2 turda bariyeri kaçıran kulvar "yavaş" sayılır: turlara katılır (aynı anda sorgulanır) ama kapanış onu beklemez. Bariyer süresi içinde bitirdiği ilk turda işaret kalkar.
- **K7 Kapanış.** `Complete()` kulvarı üyelikten düşürür ve o kulvarın tutulan mesajlarını hemen basar; `Dispose()` tutulan her şeyi basar.
- **K8 Kapalı kip (TK18).** `HoldBudget = 0` iken kapı iki işini de bırakır: (a) hiçbir mesaj tutulmaz, her mesaj etiketlenip hemen iç bus'a basılır; (b) `WaitAsync(interval)` düz beklemeye iner (yalnız `interval` kadar bekler; ortak tık, tur ve üyelik kurulmaz), yani her hesap kendi aralığıyla yoklanır ve TK8'in yük bağı da kalkar. Yalnız (a) olsaydı tempo kancası ve K1 sürer, "PR-A davranışı" iddiası tutmazdı. Bu kipte davranış PR-A ile aynıdır; sıralamayı ve ortak tempoyu birlikte kapatmanın tek sabitlik yolu budur. Tutmayı açık bırakıp yalnız ortak tempoyu kapatmak bu sabitle yapılamaz; ayrı bir değişikliktir (§12 R3).

**Ortak tık en hızlı isteyeni izler (TK8).** K1 gereği bir hesap taşıp aralığını 0,5 sn'ye indirirse (`NextInterval`, `InstagramLiveCommentsPoller.cs:123-133`) öteki hesap da o tempoyla yoklanır. Gerekçe: taşma tam da çok kişinin aynı anda kod yazdığı andır; sıra en çok o an önemlidir ve aralıklar ayrışırsa farklı saniyelerde ters sıra oluşur. Bedel: tipik bir taşma patlamasında (0,5 → 1,0 sn'ye beş turda gevşer) sakin hesap yaklaşık 1–2 fazladan istek yapar; taşma sürekli tabanda (0,3 sn) kalırsa sakin hesabın yükü yaklaşık 3 katına çıkar. Hakemlerden biri bu bağı kota belirsizken yanlış yönde buldu; itiraz kayıtlıdır (§12, R3) ve pilotta taşma anlarının kota başlığı ayrıca okunur. Kota sorunu bu bağdan çıkarsa tek sabitlik geri dönüş K8'dir (sıralama da kapanır). Her durumda hesap başına hız "aralık başına bir istek"i aşmaz; bu bugünkü tavanla aynı sınıftır.

**Birleştirici (saf).**

```csharp
internal static class InstagramCommentMerger
{
    // Girdi: kulvar sırasına göre partiler; her parti kendi içinde hesap içi sırada.
    public static IReadOnlyList<ChatMessage> Merge(IReadOnlyList<IReadOnlyList<ChatMessage>> batchesByLane);
}
```

Anahtar yalnız Meta damgasıdır (`ChatMessage.ReceivedAt`; poller Meta saniyesini yazar, `InstagramLiveCommentsPoller.cs:321`). Hesap içi sıra korunur. Aynı saniyede hesaplar arası dizilim dönüşümlüdür; başlangıç kulvarı saniyeye göre döner (`saniye mod kulvar sayısı`). Belirlenimcidir ve uzun vadede hiçbir hesabı sistematik öne almaz **(TK21)**; "şu hesap her zaman önce" kuralı seçilmedi, çünkü aynı saniyedeki gerçek sıra bilinemezken bir hesabın izleyicisini kalıcı olarak öne alırdı. Tek girdide çıktı girdinin aynısıdır.

**Garantiler ve dürüst sınırlar**
- **G1.** Aynı turda dönen, farklı Meta saniyelerinde yazılmış yorumlar hesaplar arasında damga sırasıyla basılır.
- **G2.** Aynı saniyede yazılmış iki yorumun hesaplar arası gerçek sırası **bilinemez**: Meta damgası saniye çözünürlüklüdür (`InstagramLiveMediaParser.cs:114`). Dönüşümlü dizilir. Operatörün tek dayanağı etiket ipucundaki Meta saatidir (§8.6.2).
- **G3.** Ters sıra şu durumlarda kalabilir: (a) iki anlık görüntü arasındaki farkta yazılmış ve saniye sınırını aşmış iki yorum (fark ölçülmedi; `HoldBudget` ile sınırlı); (b) bir hesap geç, yavaş ya da kopukken (sağlık şeridi o sırada bunu gösterir); (c) Meta bir yorumu API'ye geç yansıtırsa (doğrulanmadı; mevcut filigran da "çekim o ana dek yazılmış her yorumu döndürür" varsayımına dayanıyor ve eski damgalı geç görünen yorumu zaten düşürüyor, `InstagramCommentWatermark.cs:76-83`).
- **G4.** Sıra yalnız Instagram hesapları arasında kurulur. YouTube, TikTok ve Facebook ile karşılıklı sıra bugünkü gibi varış sırasıdır.
- Ters sıra sayacı PR-B'de de sürer; kalan ters sıralar rakamla izlenir.

**Gecikme bütçesi.** Yalnız bir hesap canlıysa sıfır (tek üyeli tur, bitince hemen basılır). İki hesap sağlıklıyken iki yanıt arasındaki fark kadar (tavan 400 ms; tipik değer ölçülmedi). Takılan hesap sağlam hesabı bir kez 400 ms bekletir (K3); sürekli yavaş hesap en çok iki tur bekletir (K6).

**Bağlama notu.** Hakemlerden biri ortak tıkın poller'a "tek adım" dikişiyle (`RunAsync` = adım → yayımla → bekle), ayrı bir koordinatörle bağlanmasını önerdi. Bu belge aynı kuralları tempo kancasıyla kurar: poller'ın döngüsü tek kod yolu olarak kalır, hesap yaşam döngüsü iki yerde yazılmaz, mevcut dört döngü testi olduğu gibi durur. K1–K8 kuralları iki bağlamada da aynıdır; kapının iç yapısı inşaatçıdadır (§11.3).

#### 8.3.7 Oturum başlat / bitir

- **Başlat:** üst döngü oturumu en geç 60 sn içinde görür (bugünkü gecikme); bütün hesap döngüleri aynı anda başlar. Her hesap önce çözülür, ilk çekimini yutar, sonra ortak tıka girer (PR-B).
- **Bitir:** `SessionEnded` bütün hesapları keser; kapıda tutulan (en çok 400 ms'lik) mesajlar basılır; sağlık "kapalı"ya döner.

### 8.4 ChatMessage kaynak hesap alanı (d)

Konumsal kaydın (`OrderDeck.Core/Chat/ChatMessage.cs:5-14`) **sonuna** varsayılanlı alan eklenir:

```csharp
string? SourceAccount = null
```

- İçerik: gösterime hazır hesap etiketi, yani kulvar etiketinin aynısı (§8.3.2 hesap döngüsü 4. adım): `@kullanıcıadı`; kullanıcı adı çözülemediyse Sayfa adı ("@"sız). "@" kulvarda eklenir, arayüzde değil; böylece Sayfa adının başına yanlışlıkla "@" gelmez. Yalnız birleştirme kapısı doldurur, yani yalnız 2 ve üzeri Sayfa bağlıyken (§8.3.2 üst döngü 3. adım).
- Etkilenen kurucular: beş üretim çağrısı da dokuz argümanla derlenmeye devam eder ve alanı doldurmaz: `OrderDeck.Chat/Bridge/ExtensionBridgeServer.cs:658`, `OrderDeck.Chat/Ingestors/Facebook/FacebookLiveCommentsStream.cs:283`, `OrderDeck.Chat/Ingestors/Instagram/InstagramLiveCommentsPoller.cs:313`, `OrderDeck.Chat/Ingestors/YouTube/YouTubeOfficialChatIngestor.cs:247`, `OrderDeck.Core/Sales/LabelService.cs:518`. Testlerdeki çağrılar da değişmez.
- Kalıcılaşmaz: overlay tel modeli alanları elle kopyalar (`OverlayHost.cs:428-429`, `:436-437`; `OrderDeck.Overlay/Models/OverlayEvent.cs:6-8`), etiket ve sipariş yalnız Platform + Username taşır. SQLite, sunucu DTO'ları ve OBS görünümü değişmez. Müşteri anahtarı (Platform, Username) aynıdır.

### 8.5 Hata yalıtımı ve sağlık modeli (e)

**Yalıtım (AH2).** Her hesap kendi döngüsünde koşar. Bugün servis geneli olan çöküş sayacı ve kalıcı hata beklemesi (`InstagramChatHostedService.cs:101`, `:182-192`) hesap başına iner; bir döngüdeki istisna o döngünün kendi yakalamasında kalır. Paylaşılanlar: ChatBus ve SpamFilter (ikisi de kilitli), adlandırılmış HttpClient havuzu ve birleştirme kapısı (K4–K6 gereği sorunlu hesap kimseyi bekletmez). Facebook canlı yorum ve moderasyon ayrı servislerdedir ve okudukları yöntem değişmediği için hata davranışları da aynıdır (AH5).

**Sağlık izleyicisi** (olguları tutar; yorumu arayüz tarafındaki saf sunucu yapar).

```csharp
public enum InstagramAccountState
{
    Stopped, Starting, NotLinked, ResolveFailed,
    WaitingForBroadcast, Live, BroadcastEnded, RateLimited, Faulted
}

public sealed record InstagramAccountHealth(
    string PageId, string PageName, bool IsPrimary, string? Username,
    InstagramAccountState State, string? Detail,
    DateTimeOffset Since, DateTimeOffset? LastContactAt, DateTimeOffset? RetryAt,
    bool WasLiveThisSession);

public sealed class InstagramHealthMonitor
{
    public InstagramHealthMonitor(TimeProvider? time = null);
    public void Configure(IReadOnlyList<FacebookLinkedPage> pages);   // eksiği ekler, fazlayı siler; ilk girdi birincil
    public void ResetSession();                                       // WasLiveThisSession sıfırlanır
    public void Clear();
    public void SetAccount(string pageId, string? username);
    public void Report(string pageId, InstagramAccountState state, string? detail = null, DateTimeOffset? retryAt = null);
    public IInstagramPollObserver ObserverFor(string pageId);
    public IReadOnlyList<InstagramAccountHealth> Snapshot();          // kopya
}
```

Yer: `OrderDeck.Chat/Ingestors/Instagram/InstagramHealthMonitor.cs` (yeni). Tekil, kilitli.

**Geçişler**

| Olay | Sonuç |
|---|---|
| Tur Ok | `Live`; `WasLiveThisSession = true`; `LastContactAt = şimdi` |
| Tur NoBroadcast | `LastContactAt = şimdi`; daha önce canlıydıysa `BroadcastEnded`, değilse `WaitingForBroadcast` |
| Tur RateLimited | `RateLimited`; `RetryAt = şimdi + süre`; `LastContactAt = şimdi` |
| Tur Fatal | `Faulted`; `Detail` = poller'ın Türkçe mesajı; hosted service `RetryAt`'i 5 dk sonrasına yazar |
| Tur Transient | Durum değişmez (yanıtsızlık kuralı yakalar) |
| Hesap döngüsü oturumda ilk kez başladı | `Starting` (poller yeniden kurulurken durum değişmez) |
| Çözümleme `NotLinked` / `Failed` | `NotLinked` / `ResolveFailed` (+ `RetryAt`) |
| Oturum bitti | `Stopped` |

**Görünüm kuralları** (saf sunucu `InstagramHealthPresenter`, §8.6.3; her kural test edilir)
- **Yanıtsızlık okuma anında türetilir (TK10).** `Live` iken son temas 10 sn'den, `WaitingForBroadcast` / `BroadcastEnded` / `Starting` iken 45 sn'den eskiyse hesap "yanıt yok" sayılır (temas yoksa `Since` esas alınır). Böylece takılı istek (zaman aşımı 15 sn, `InstagramLiveCommentsPoller.cs:43`) ve 5 ardışık hatadan sonraki 30 sn'lik bekleme de görünür; döngü asılı kalsa bile alarm çıkar.
- **Uyarı kapısı (TK9).** Sorun durumları şunlardır: yanıt yok, hız sınırı, kalıcı hata, hesap bilgisi okunamadı, Sayfada Instagram hesabı yok (şeride girdiği durumlarda; aşağıdaki `NotLinked` kuralı). Bir sorun durumu yalnız şu koşulda sarı gösterilir: hesap bu oturumda canlıydı **veya** başka bir Instagram hesabı şu an canlı. Aksi hâlde aynı metin nötr gösterilir. Instagram'da yayın yapılmayan gecede gürültü olmaz. Aşağıdaki iki durum sorun durumu sayılmaz; kendi kurallarıyla sararır.
- **Çok hesapta kapı muafiyeti (TK9, AH3).** Bağlı Sayfa sayısı 2 ve üzeriyken "kalıcı hata" ve "hesap bilgisi okunamadı" uyarı kapısına bakılmadan **her zaman sarıdır**. Gerekçe: kapı tek başına bırakılırsa iki hesap da oturum başından bozukken (ortak neden: tek kullanıcı token'ından türeyen iki Sayfa token'ı birlikte geçersizleşir) hiçbir hesap canlı olmadığı için hiçbir satır ve üst nokta sararmaz, şerit yalnız nötr metin gösterirdi. "Hesap bilgisi okunamadı" da kapsamdadır, çünkü uygulama açıldıktan sonraki ilk oturumda geçersiz token poller'a varmadan çözümlemede düşer (§8.2 yeniden onay 5. adım). Bedeli: iki hesap bağlı ama o gece Instagram'da yayın yapılmıyorsa, bozuk bir hesap yine de sarı görünür. Tek Sayfalı kurulumda kapı aynen geçerlidir.
- **"Canlı yayın bulunamadı" (TK10).** Başka bir hesap canlıyken bu hesap 90 sn'dir canlı değilse (süre, bu hesabın beklemeye başladığı an ile öteki hesabın canlı olduğu andan geç olanından ölçülür) `WaitingForBroadcast` sarı olur; aksi hâlde nötrdür ("yayın bekleniyor"). Bu, ikinci hesabın hiç akmadığı sessiz durumu yakalar. **Bu durum yalnız şeritte sarıdır; üst bardaki sohbet noktasını sarartmaz (TK20).** Gerekçe: Burak bazı geceler yalnız tek hesaptan yayın yaparsa öteki hesap bütün gece bu durumda kalır; üst nokta da sararsaydı gece boyu yanan bir alarm, aynı noktanın taşıdığı "60 saniyedir hiç mesaj yok" işaretini (`MainShellViewModel.cs:1354-1385`) ve gerçek kopmaları örterdi. Şerit satırı sarı kalır; hesap o gece bilerek kapalıysa zararsız bir hatırlatmadır.
- **Yayın bitti.** `BroadcastEnded` yalnız başka bir hesap hâlâ canlıyken sarıdır ("yayın kesildi"); iki yayın da bittiğinde nötrdür ("yayın bitti").
- **`NotLinked` (Sayfada Instagram hesabı yok).** Birincil Sayfada `NotLinked` yalnız **ek Sayfa yokken** şeride girmez: yalnız Facebook kullanan tek Sayfalı kurulum (Sayfasında Instagram hesabı yok) bugünkü gibi hiçbir şey görmez. Ek Sayfa varken birincil `NotLinked` şeride girer ve ek Sayfalarla aynı uyarı kapısına tabidir ("ek Sayfa var" ve "2 ve üzeri Sayfa" bilgisi anlık görüntüden okunur: birincil olmayan en az bir girdi). Gerekçe (AH3): çözümleyici "hesap gerçekten yok" ile "alan yanıtta gelmedi"yi ayıramaz (`InstagramAccountResolver.cs:68-76`); yeniden onayda birincilin Instagram hesabının işareti kaldırılırsa ya da Sayfa–Instagram bağı koparsa birincil hesap tam bu yola düşer (Meta'nın bu durumda alanı düşürüp düşürmediği doğrulanmadı). Kural koşulsuz olsaydı iki hesaplı kurulumda birincilin Instagram'ı sessizce kaybolur, şeride giren hesap sayısı 1'e inince şerit de gizlenirdi. Ek Sayfada `NotLinked` her zaman şeride girer ve uyarı kapısına tabidir. Bu kuralın bedeli ve kalan boşluğu §12 R16'dadır.
- Tespit gecikmesi: arayüz 5 sn'lik nabızla okur (`MainShellViewModel.cs:1253`, `:1349`); en geç yaklaşık 5 sn + ilgili eşik.

**Hesap başına durumlar (özet)**
- Kalıcı hata (token 190/463, izin 200/10, `comments` alanı yok; `InstagramGraphError.cs:64-72`, `InstagramLiveCommentsPoller.cs:243-248`, `:274-280`): yalnız o hesap 5 dk bekler.
- Hız sınırı: yalnız o hesap başlıktaki süre ya da 1 dk bekler (`:249-260`, `:162-167`). Sayaç ortaksa iki hesap ayrı ayrı sınıra düşer ve ikisi de görünür.
- Geçici hata / zaman aşımı: 5 ardışıkta poller çıkar (`:47`, `:170-178`), hesap 30 sn sonra yeniden kurulur; yanıtsızlık kuralıyla görünür.
- Beklenmeyen istisna: hesap başına `ComputeBackoff`.

### 8.6 Arayüz (f)

Açılır pencere yoktur; çekmece + satır içi metin kalıbı kullanılır. Örnek adlar temsilidir.

#### 8.6.1 Ayarlar > Facebook sekmesi

Facebook kartı (`SettingsPage.xaml:279-304`) ve `ConnectFacebook` / `DisconnectFacebook` komutları (`SettingsViewModel.cs:416-477`) değişmez. İki ek: bağlan ve kaldır sonrasında `RefreshInstagramStatusAsync` de çağrılır (bugün yalnız kurucuda, `:209`); "Facebook'a Bağlan" beklemesi servis tarafındaki 5 dakikalık zaman aşımına tabidir (§8.2 TK17) ve süre dolunca mevcut hata dalı (`:452-455`) `Bağlantı başarısız: Facebook onayı 5 dakika içinde tamamlanmadı. Yeniden dene.` yazar (komut kodu değişmeden).

Instagram kartı (`SettingsPage.xaml:306-324`):

Bağlı değil (bugünkü metin):
```
Instagram canlı yorumlar
Önce Facebook'a bağlan.
[Instagram hesabı ekle: pasif]   [Facebook izinlerini güncelle: pasif]
```

Tek Sayfa (mevcut kurulumlar güncellemeden sonra bunu görür; durum satırı bugünkü metin, `SettingsViewModel.cs:405-407`):
```
Instagram canlı yorumlar
Bağlı Instagram hesabı: @mezatdunyasi
[Instagram hesabı ekle]   [Facebook izinlerini güncelle]
```

İki Sayfa:
```
Instagram canlı yorumlar
2 Instagram hesabı bağlı
  @mezatdunyasi     Sayfa: Mezat Dünyası · birincil (Facebook yorumları ve moderasyon bu Sayfadan)
  @mezatdunyasi2    Sayfa: Mezat Dünyası 2                                            [Kaldır]
[Instagram hesabı ekle]   [Facebook izinlerini güncelle]
İki hesap bağlıyken yorumlar tek akışta birleşir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar.
```

- Satır durumu: `@ad` | `Bu Sayfaya bağlı Instagram profesyonel hesabı yok` (tek Sayfalı kurulumda nötr; iki ve üzeri Sayfada, birincil satır dahil, sarı) | `Durum okunamadı`.
- Birincil satırda "Kaldır" yoktur; ipucu: `Birincil Sayfayı değiştirmek için "Bağlantıyı Kaldır" + "Facebook'a Bağlan"`.
- Kartın mevcut ipucu metni (`:318-322`) aynen kalır.
- İzin uyarısı (`InstagramNeedsReconnect`, `:313-317`) aynen çalışır (tek kullanıcı token'ı bütün hesapları kapsar; izin sondası bir kez). Metni yeni düğmeye bağlanır: `Instagram yorumları için Facebook izinlerini güncellemen gerekiyor: aşağıdaki "Facebook izinlerini güncelle"ye bas.`

**"Instagram hesabı ekle"** (yeni tarayıcı girişi yok):
0. İzin sondası "izin yok" diyorsa (`InstagramNeedsReconnect` true; değer Ayarlar açılırken hesaplanmıştır, yeni istek atılmaz) listeleme yapılmaz ve çekmece açılmaz; sonuç metni: `Facebook oturumunda Instagram izni yok. "Facebook izinlerini güncelle"ye bas.` Gerekçe: izin eksikken Graph Instagram alanını hata vermeden düşürür (§8.2); listeleme yapılsaydı bütün Sayfalar "Instagram hesabı bağlı değil" görünür ve kullanıcı yanlış yere (Sayfa bağına) bakardı.
1. Durum satırı: `Sayfalar okunuyor...`
2. Eklenebilecek hesap varsa yeni çekmece açılır (`InstagramAccountAddDrawer`, başlık `Instagram hesabı ekle`). Mevcut `FacebookPagePickerDrawer`'a dokunulmaz (TK5).
```
Yorumlarını da okumak istediğin hesabı seç:
  (•) @mezatdunyasi2 — Sayfa: Mezat Dünyası 2
  ( ) Butik Sayfam — Instagram hesabı bağlı değil          (pasif)
[Ekle]
[İptal]
Aradığın hesap listede yok mu? Önce Instagram hesabını bir Facebook Sayfasına bağla.
Sayfa yine görünmüyorsa çekmeceyi kapatıp "Facebook izinlerini güncelle"ye bas.
```
   Tek seçimlidir; zaten bağlı Sayfalar listelenmez; Instagram hesabı olmayan Sayfa pasiftir; seçim yokken "Ekle" kapatmaz.
3. Sonuç metinleri (kart içi satır, "Tamam" ile kapanır; `ShippingNotice` kalıbı, `SettingsViewModel.cs:66-68`):
   - `@mezatdunyasi2 eklendi. Yayın açıksa birkaç saniye içinde okunmaya başlar.`
   - `Eklenebilecek başka hesap bulunamadı. Yeni hesabı önce bir Facebook Sayfasına bağla; Sayfa hazırsa "Facebook izinlerini güncelle"ye bas ve açılan Facebook ekranında TÜM Sayfalarını ve Instagram hesaplarını işaretle.`
   - `Facebook oturumunun süresi dolmuş. "Facebook izinlerini güncelle"ye bas.`
   - `En çok 4 Instagram hesabı bağlanabilir.`
   - `"{Sayfa}" için yetki alınamadı: bu Sayfada yönetici olduğundan emin ol.`
   - `Sayfalar okunamadı, internetini kontrol edip yeniden dene.`

**"Kaldır":** satır anında kalkar; metin: `@mezatdunyasi2 kaldırıldı.`

**"Facebook izinlerini güncelle":**
1. Durum: `Tarayıcıdan onay bekleniyor... Açılan Facebook ekranında TÜM Sayfalarını ve Instagram hesaplarını işaretli bırak; yalnız yenisini işaretlersen eski hesabın bağlantısı düşer.` Düğmenin ipucu: `Yayın sırasında basma: Instagram ve Facebook yorumlarında kısa bir kesinti olabilir.` (gerekçe §12 R7). Beklerken düğmenin yanında `[İptal]` görünür; "Facebook'a Bağlan", "Facebook izinlerini güncelle" ve "Instagram hesabı ekle" pasiftir (aynı anda tek tarayıcı akışı, §8.2 TK17).
2. Bağlı Sayfaların hepsi yeni girişte varsa: `Facebook izinleri güncellendi: 2 Sayfa yenilendi.`
3. Eksik Sayfa varsa kaydetmeden önce onay çekmecesi (`MessageDrawer.ForConfirm`, `OrderDeck.App/Views/Drawers/MessageDrawer.xaml.cs:33`; Evet / Vazgeç): `"Mezat Dünyası" bu girişte paylaşılmadı. Kaydedersen bu Sayfanın yorumları kesilebilir (Facebook yorumları dahil). Vazgeçersen OrderDeck'te hiçbir şey değişmez; "Facebook izinlerini güncelle"ye yeniden basıp tüm Sayfalarını işaretle. Yine de kaydedilsin mi?`
4. İptal (`OperationCanceledException`): eksik Sayfa onayında "Vazgeç" ya da beklerken `[İptal]`: `Güncelleme iptal edildi; bağlantı değişmedi.`
5. Zaman aşımı (`TimeoutException`): `Facebook onayı 5 dakika içinde tamamlanmadı; bağlantı değişmedi. Yeniden dene.` Tarayıcı sekmesinin kapatılması algılanamaz; o durumda kullanıcı `[İptal]`e basar ya da bu metni 5 dakika sonra görür.
6. Diğer hatalar: `Güncelleme başarısız: {kısa sebep}. Bağlantı değişmedi.` Facebook onay ekranında "İptal"e basmak da buraya düşer (`error=access_denied`, `FacebookOAuthService.cs:330-332`); 4. maddeye değil. Kaydetme tek yazım olduğu için (§8.2 5. adım) bu üç sonuçta da diskteki bağlantı değişmemiştir.

ViewModel eklemeleri: `ObservableCollection<InstagramPageRow> InstagramPages` (PageId, PageName, InstagramText, IsPrimary, CanRemove), `string? InstagramNotice`, `AddInstagramAccountCommand`, `RemoveInstagramAccountCommand`, `ReauthorizeFacebookCommand`, `CancelReauthorizeCommand` (bekleyen onayın belirtecini tetikler), `DismissInstagramNoticeCommand`. `RefreshInstagramStatusAsync` (`:362-414`) tek Sayfa yerine `GetConnectedPagesAsync` üzerinde döner (Sayfa başına `ResolveDetailedAsync`; açılışta N istek, bugün 1).

#### 8.6.2 Sohbet satırı etiketi (Ö1, TK11)

```
[IG] ayse_y       A12 alıyorum                                 @mezatdunyasi
[IG] veli.k       A12                                         @mezatdunyasi2
[TT] zeynep       b7
```

- `ChatMessageViewModel` (`OrderDeck.App/ViewModels/ChatMessageViewModel.cs:10-34`): `public string? AccountTag` (`SourceAccount` boşsa null, doluysa `SourceAccount`'un kendisi; "@" eklenmez, §8.4) ve `public string? AccountTooltip` (`Instagram: @mezatdunyasi2 · 21:14:07`, yani `"Instagram: " + SourceAccount + " · " + saat`; saat `ReceivedAt`'in, yani Meta damgasının yerel saat karşılığıdır).
- `ChatPanel.xaml`: mesaj kolonundaki `TextBlock` (`:118-120`) bir `DockPanel`'e alınır. Etiket sağa yaslı, küçük ve soluk bir `TextBlock`'tur (genişlik sınırı yaklaşık 110, kırpmalı, `AccountTag` null iken gizli). Rozet ve ad kolonları (`:76-117`) değişmez.
- Etiket sağda olduğu için kodun başladığı x konumu hiçbir satırda kaymaz. Tek hesapta ve diğer platformlarda etiket yoktur; satır bugünküyle aynıdır. OBS overlay'e gitmez.

#### 8.6.3 Ana ekran: sohbet paneli sağlık şeridi (Ö3, TK9)

```
Canlı Sohbet   [Kullanıcı veya mesaj ara…]                          [Sadece A12]
Instagram   ● @mezatdunyasi canlı      ▲ @mezatdunyasi2 yanıt yok (18 sn)
────────────────────────────────────────────────────────────────────────────────
```

- Yer: `ChatPanel.xaml`'de başlık ile liste arasına `Auto` yükseklikli yeni satır (bugün iki satır, `:6-9`). `ItemsControl` + `WrapPanel`; şablon kenar çubuğundaki nokta + metin desenini kullanır (`ShellSidebar.xaml:192-225`).
- Neden burada: sohbet paneli dar kipte de görünür; kenar çubuğu bölümü dar kipte gizlidir (`ShellSidebar.xaml:183-185`). `ShellBanners`'a şerit eklenmez: geçici şeritler tek hücreyi paylaşır ve üst üste binen iki yarı saydam şerit okunmaz; uzun ömürlü band bu yüzden ayrı satırdadır (`ShellBanners.xaml:4-7`, `:14-19`; `OrderDeck.Tests/App/ShellBannersLayoutTests.cs:14-20`). Sağlık durumu dakikalarca sürebilir.
- **Görünürlük:** aktif oturum VE (şeride giren hesap sayısı 2 ve üzeri VEYA herhangi biri sarı). Tek hesap + sorun yok = panel bugünküyle aynı. Ek Sayfa varken birincil Sayfa `NotLinked` olsa da şeride girer (§8.5); yani iki Sayfalı kurulumda birincilin Instagram'ı düşünce şerit gizlenmez.
- Etiket: `@kullanıcıadı`; bilinmiyorsa Sayfa adı. Nokta: yeşil (canlı), soluk (nötr), sarı (uyarı).

| Durum | Metin | İpucu |
|---|---|---|
| Canlı | `@hesap canlı` | `Son yanıt 1 sn önce` |
| Yayın bekleniyor (nötr) | `@hesap yayın bekleniyor` | `Bu hesapta canlı yayın açılınca yorumlar akmaya başlar.` |
| Canlı yayın bulunamadı (şeritte sarı; üst noktayı sarartmaz, TK20) | `@hesap canlı yayın bulunamadı` | `Diğer hesap yayında ama bu hesapta canlı yayın görünmüyor. Bu hesapta yayını açtın mı?` |
| Yayın bitti (nötr) | `@hesap yayın bitti (23:41)` | — |
| Yayın kesildi (sarı) | `@hesap yayın kesildi (21:14)` | `Bu hesabın yayını kapandı; yorumları gelmiyor. Diğer hesap yayında.` |
| Yanıt yok | `@hesap yanıt yok (18 sn)` | `Instagram'dan yanıt alınamıyor, yeniden deneniyor. Yorumlar gecikiyor olabilir.` |
| Hız sınırı | `@hesap hız sınırı, 21:20'de dener` | `Instagram istek sınırına takıldı. Bu hesabın yorumları o saate kadar gelmeyecek.` |
| Kalıcı hata | `@hesap bağlantı yenilenmeli` | Poller'ın mesajı + `Ayarlar > Facebook > "Facebook izinlerini güncelle". 21:19'da yeniden denenecek.` |
| Hesap bilgisi okunamadı | `{Sayfa adı}: hesap bilgisi okunamadı, 21:19'da dener` | `Sayfaya bağlı Instagram hesabı okunamadı. Sürerse: Ayarlar > Facebook > "Facebook izinlerini güncelle".` |
| Instagram hesabı yok (ek Sayfa) | `{Sayfa adı}: Instagram hesabı bağlı değil` | `Bu Sayfaya bir Instagram profesyonel hesabı bağla ya da Sayfayı Ayarlar'dan kaldır.` |
| Instagram hesabı yok (birincil Sayfa; yalnız ek Sayfa varken şeride girer) | `{Sayfa adı}: Instagram hesabı bağlı değil` | `Birincil Sayfaya bağlı Instagram hesabı görünmüyor. Sayfa ile Instagram hesabının bağını kontrol et; bağ duruyorsa Ayarlar > Facebook > "Facebook izinlerini güncelle"ye basıp tüm hesapları işaretle.` |
| Bağlanıyor | `@hesap bağlanıyor` | — |

Tabloda "(sarı)" ya da "(nötr)" yazmayan satırların rengi §8.5'teki uyarı kapısına ve çok hesaptaki kapı muafiyetine göre belirlenir.

- `MainShellViewModel` kurucusuna isteğe bağlı `InstagramHealthMonitor? instagramHealth = null` eklenir (`viewers` ile aynı desen, `:1118`; `AddSingleton<MainShellViewModel>()` olduğu için kendiliğinden çözülür, `AppHost.cs:341`; test koşumları etkilenmez). Yeni üyeler: `ObservableCollection<InstagramAccountStatusViewModel> InstagramAccounts` (Label, StateText, Severity, Tooltip) ve `bool ShowInstagramStatus`. Mevcut 5 sn'lik nabızdan (`UpdateChatHealth` içinde, `RefreshConnections`'ın yanında, `:1349`) tazelenir.
- Saf sunucu: `InstagramHealthPresenter.Evaluate(snapshot, now, sessionActive)` satırları, şeridin görünürlüğünü, "en az bir hesap canlı" bilgisini ve üst nokta ipucunu döner (§8.5 kuralları burada yaşar). Üst nokta bilgisi, sarı satırlardan "canlı yayın bulunamadı" dışındakilere göre kurulur (TK20).
- DI: `services.AddSingleton<InstagramHealthMonitor>()` ve Instagram hosted service kaydına (`AppHost.cs:303-310`) `health:` argümanı. Yeni hosted service yoktur; genel başlatma döngüsü (`WpfStartupEnvironment.cs:189-197`) yeter.

#### 8.6.4 Üst bar noktası ve kenar çubuğu

- **Üst bardaki sohbet sağlık noktası** (`OrderDeck.App/Views/Shell/ShellTopBar.xaml:119-136`; XAML değişmez). `UpdateChatHealth` (`MainShellViewModel.cs:1323-1386`) Instagram'da sarı durum varken `ChatHealthState = "idle"` (sarı) yapar ve ipucunu hesabı adıyla yazar. "Canlı yayın bulunamadı" bu kuralın dışındadır: şeritte sarıdır ama üst noktayı sarartmaz (TK20, §8.5). Örnek ipucu: `Instagram @mezatdunyasi2: yanıt yok (18 sn). Diğer kaynaklar akıyor.` Birden çok hesapta: `Instagram: 2 hesapta sorun (@mezatdunyasi, @mezatdunyasi2).` Dar kipte kalıcı ikinci işarettir.
- **Kenar çubuğu Instagram satırı.** `RefreshConnections` (`:196-206`) Instagram satırında `IsConnected` değerini "en az bir hesap canlı" bilgisinden alır (sağlık izleyicisi verilmediyse, yani mevcut test koşumlarında, bugünkü davranış sürer). Bugün bu nokta Instagram API yolunda hiç yanmıyor; sohbet şeridi "canlı" derken kenar çubuğunun gri kalması çelişki olurdu. İzleyici sayısı kapsam dışı olduğu için Instagram satırında sayı gösterilmez: `PlatformConnectionViewModel`'e `bool ShowViewerCount` eklenir; sayı sütununun görünürlük bağı (`ShellSidebar.xaml:220-224`) `IsConnected` yerine buna bağlanır. Diğer üç satırda `ShowViewerCount` bugünkü `IsConnected` ile aynı değeri alır, yani görünümleri değişmez. Dört satır kuralı korunur (`OrderDeck.Tests/App/MainShellConnectionsTests.cs:9-20`). Kenar çubuğunda sarı durum ya da hesap alt satırı yoktur; uyarı yüzeyleri şerit ve üst noktadır.

```
BAĞLANTILAR
● YouTube        1.240
● Instagram                  (en az bir hesap canlıyken yeşil; sayı yok)
● TikTok           310
○ Facebook
```

### 8.7 Tekrar süzgeci (SpamFilter) kararı (g)

**Karar (TK12): süzgece dokunulmaz.**

- Tekrar kuralı (kullanıcı adı, metin) anahtarlıdır, penceresi 2 sn'dir ve hesap bilmez (`OrderDeck.Core/Chat/SpamFilter.cs:34-35`, `:84-105`). Poller yorumu bus'a (çok hesapta kulvara) vermeden önce süzgece sorar (`InstagramLiveCommentsPoller.cs:303-311`); bu sıra değişmez.
- **Keşif düzeltmesi:** keşif raporu süzgecin varsayılan olarak kapalı olduğunu yazmıştı. Koddaki varsayılanlar açıktır: `Enabled = true`, `DropDuplicates = true` (`OrderDeck.Core/Chat/SpamFilterSettings.cs:14`, `:19`). `SpamFilter.cs:14`'teki "Disabled by default" yorumu bayattır. Yani bu etkileşim, ayar değiştirilmemiş her kurulumda geçerlidir.
- Sonuç: aynı izleyici aynı metni 2 sn içinde iki hesaba da yazarsa sohbete **tek satır** düşer. Bu, tek yayında aynı satırı iki kez yazan izleyiciye bugün uygulanan kuralın aynısıdır ve müşteri kimliğiyle tutarlıdır (iki yayındaki aynı kullanıcı adı tek müşteridir). Operatörün aynı kişiye çift sipariş yazma riskini de azaltır.
- Bilinen yan etkiler: (1) hangi hesabın satırının kaldığı poller'ların işleme sırasına bağlıdır, damgaya değil; aynı kişi olduğu için adalet etkisi yoktur, yalnız etiket öteki hesabı gösterebilir. (2) Pencere "şimdi" olarak yorumun Meta damgasını alır (`:305`) ve "ilk öğe en eski" varsayar (`SpamFilter.cs:111-118`); iki poller farklı sırayla beslediğinde pencere nadiren biraz geniş ya da dar çalışabilir. Etkisi ölçülmedi.
- Burak iki satırı da görmek isterse süzgeç anahtarına hesap eklenir; bu ayrı ve küçük bir değişikliktir (TK12; teslim özetinde gösterilir).

### 8.8 Günlükleme (h)

- **Kişisel veri yok.** İzleyici kullanıcı adı ve yorum metni Information ve üstü düzeyde loglanmaz (bugünkü gibi; süzgeçten düşen yorum yalnız kimliğiyle Debug'da, `InstagramLiveCommentsPoller.cs:308`). Yeni satırlarda yalnız şunlar geçer: PageId, Instagram hesap kimliği, yayıncının kendi hesap kullanıcı adı (bugün de loglanıyor: `InstagramChatHostedService.cs:137-139`, `InstagramAccountResolver.cs:87-89`), durum adları, sayılar.
- **Token yok.** Token taşıyan kayıtlar `ToString`'te token basmaz (§8.1). `access_token` içeren URL hiçbir log ya da istisna mesajına yazılmaz (§8.2 düzeltmesi); `paging.next` adresi de token taşır, loglanmaz.
- **Yeni satırlar (Information):** hesap durum geçişleri (hesap, eski → yeni durum, varsa yeniden deneme saati); hesap başına dakikada en çok bir kota satırı (`TryGetUsage` çıktısı: business-id, call_count, total_cputime, total_time, estimated_time_to_regain_access; ilk üçü yüzdedir ve satırda `%` ile yazılır, sonuncusu dakikadır; başlık yanıtta yoksa satır bunu söyler); çok hesapta dakikalık kapı özeti (hesap başına tur sayısı ve yanıt süresi p50/p95, ters sıra sayısı ve en büyük fark; PR-B'de ayrıca bariyer aşımı, geç kalan tur ve yavaş kulvar sayıları); hesap ekleme / kaldırma / izin güncelleme sonuçları (PageId ve Sayfa adı).
- Filtre: `grep -iE "InstagramChatHostedService|InstagramMergeGate|InstagramHealth"`.

### 8.9 Bilerek dokunulmayanlar

`OrderDeck.Chat/Facebook/FacebookModerationService.cs`, `OrderDeck.Chat/Ingestors/Facebook/*`, `OrderDeck.App/Views/Drawers/FacebookPagePickerDrawer.*`, `InstagramCommentWatermark.cs` (içerik), `InstagramLiveMediaParser.cs`, `InstagramPermissionProbe.cs`, `OrderDeck.Core/Chat/ChatBus.cs`, `SpamFilter.cs`, `ViewerCountTracker.cs`, `OrderDeck.Core/Settings/AppSettings.cs`, `OrderDeck.App/Views/Shell/ShellBanners.xaml`, `ShellTopBar.xaml`, `OrderDeck.Overlay/**`, `OrderDeck.LicenseServer/**`, `Extension/**`, SQLite göçleri.

## 9. Test planı

Kural: testlerde token ve kimlik değeri literal yazılmaz, üretilir (`$"{önek}-{Guid.NewGuid():N}"`; bkz. `AdminLoginLockoutTests.NewCredential`). Mevcut `EncryptedFacebookTokenStoreTests.cs:36-41`'deki literal desen kopyalanmaz. Demet artık liste taşıdığı için kayıt eşitliğiyle değil alan alan karşılaştırılır.

### 9.1 Birim testleri

**Mevcut hâlleriyle, değiştirilmeden geçmesi gerekenler (regresyon kilidi; bazılarına aşağıda yeni testler eklenir):** `EncryptedFacebookTokenStoreTests`, `InstagramLiveCommentsPollerTests` (üç `NextInterval` testi ve dört döngü testi, `:85-180`), `InstagramCommentWatermarkTests`, `InstagramLiveMediaParserTests`, `InstagramGraphErrorTests`, `InstagramAccountResolverTests`, `InstagramPermissionProbeTests`, `InstagramChatHostedServiceTests` (`ComputeBackoff`), `FacebookChatHostedServiceTests`, `FacebookModerationServiceTests`, `FacebookSeenCommentsTests`, `SpamFilterTests`, `MainShellConnectionsTests`, `ShellBannersLayoutTests`, `MainShellViewCompositionTests` ve `OrderDeck.Tests/ViewModels/SettingsViewModelTests.cs` dosyasındaki iki sınıf (`SettingsViewModel_PaymentTests`, `SettingsViewModel_LicenseShippingTests`; `SettingsViewModelTests` adında bir sınıf yoktur, sınıf adıyla süzen test komutunda bu iki ad kullanılır).

**Geriye uyum (ilk görev) — `OrderDeck.Tests/Chat/Facebook/FacebookTokenBundleCompatTests` (yeni)**
- Eski biçimli JSON (`ExtraPages` yok) yeni kayda okunur; birincil alanlar tam, `ExtraPages` null.
- Yeni biçimli JSON, test içinde dondurulmuş beş alanlı "eski sürüm" ayna kaydına aynı seçeneklerle okunur; birincil alanlar tam (eski sürüme dönüş sözleşmesi).
- DPAPI ile yazılmış eski biçimli blob `LoadAsync`'ten sağlam çıkar; dosya silinmez ve baytları değişmez.
- Ekli demet kaydet / yükle turunda sıra ve içerik korunur.
- `FacebookLinkedPage.ToString()` ve `FacebookTokenBundle.ToString()` token içermez.
- Atomik yazım: kayıttan sonra klasörde geçici dosya kalmaz; hedef dosya varken yazım eskisinin yerini alır.

**`OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests` (yeni; bu servisin bugün hiç testi yok).** Geçici klasörde gerçek depo + betikli `HttpMessageHandler` + sahte `IFacebookOAuthBroker`.
- `GetConnectedPagesAsync`: birincil önce; PageId'ye göre tekil; bozuk ek girdi atlanır; bağlı değilken boş.
- Ekler varken `GetPageCredentialsAsync` hâlâ birincili döner (AH5 kilidi).
- `AddExtraPageAsync`: tarayıcı ve sunucu takası çağrılmadan token türetir; diske yazar (aynı depo üstünde ikinci servis örneği görür); birincil ve yinelenen için yazmaz; sınırda reddeder.
- `RemoveExtraPageAsync`: yalnız o girdiyi siler; birincil kimlikte false.
- `ListAddablePagesAsync`: `paging.next` izlenir; bağlı olan işaretlenir; Instagram alanı olmayan Sayfa `IgUserId = null`; 190, 200 ve 100'de `NeedsReauthorize` (100 bu uçta "yayın bitti" sayılmaz); 5xx'te `NeedsReauthorize` kurulmaz, hata yukarı çıkar.
- `ReauthorizeAsync`: ikinci yanıt sayfasında gelen bağlı Sayfa "eksik" sayılmaz (sayfalamalı listeleme); bütün bağlı Sayfaların token'ı yenilenir; birincil değişmez; yeni girişte olmayan Sayfa için `confirmMissing` çağrılır; `false`'ta diske hiçbir şey yazılmaz; `true`'da eksik Sayfanın girdisi silinmez.
- Eşzamanlı ekle / kaldır kayıp güncelleme üretmez. `DisconnectAsync` her şeyi siler. Boş gövde hatasının mesajında token geçmez.
- Tarayıcı beklemesi (TK17; dinleyici ve tarayıcı açma dikişiyle, §11.3): geri çağrı gelmezse süre dolunca `TimeoutException` fırlar, diske hiçbir şey yazılmaz ve dinleyici bırakılır (hemen ardından başlatılan akış bağlanabilir); belirteç iptali `OperationCanceledException` üretir ve dinleyiciyi bırakır; bekleyen akış varken ikinci `ConnectAsync` / `ReauthorizeAsync` dinleyiciye dokunmadan reddedilir; `error=access_denied` geri çağrısı `InvalidOperationException` olarak kalır.

**`InstagramLiveCommentsPollerTests` (ek)**
- Gözlemci `RoundStarting` → `RoundCompleted` sırasını alır; Ok bildirimi yayımlardan sonra gelir.
- 190 → Fatal + mesaj; kota → RateLimited + süre; boş `data` → NoBroadcast.
- Kota başlığı ham metin olarak taşınır. Fırlatan gözlemci döngüyü durdurmaz.
- Devredilen filigranla ilk çekim yutulmaz.
- PR-B: tempo verilince Ok dalında `WaitAsync` çağrılır; verilmeyince davranış aynıdır.

**`InstagramAccountResolverDetailedTests` (yeni):** Found / NotLinked / Failed ayrımı. **`InstagramGraphErrorTests` (ek):** `TryGetUsage` çok kovalı başlık, kesirli sayı, bozuk JSON; `estimated_time_to_regain_access` varken ve yokken `MinutesToRegainAccess`.

**`InstagramHealthMonitorTests` (yeni; elle ilerletilen `TimeProvider`):** bütün geçişler; `BroadcastEnded` yalnız canlıdan sonra; `RetryAt`; `Configure` eksiği ekler, fazlayı siler, kalanın durumuna dokunmaz; `ResetSession`; `Snapshot` kopyadır; eşzamanlı yazım.

### 9.2 Sıralama testleri (sahte saatle)

Zaman `TimeProvider` üzerinden yürür; testler elle ilerletilen bir `TimeProvider` kullanır (emsal: `OrderDeck.Tests/Sync/SessionOrderSyncCadenceTests.cs:41` `ManualTime`). Ağ yoktur; kapının yöntemleri doğrudan çağrılır.

**`InstagramMergeGateTests` (yeni)**
- PR-A: `SourceAccount` basılır, diğer alanlar aynı; mesaj hemen iç bus'a geçer; ters sıra sayacı yalnız geri giden damgada artar; iç bus fırlatsa da sonraki mesajlar akar.
- PR-B, K1: iki kulvar farklı anlarda beklemeye girer, aynı tıkta serbest kalır; tık, ilk dolan bekleme süresinde atılır.
- K2: iki üyenin mesajları tur kapanana dek tutulur; tek yayımda damga sıralı çıkar; damgası küçük olan sonradan gelse de önce basılır.
- K2 / K3: B yanıt vermezken A'nın mesajı tam 400 ms'de çıkar; B'nin mesajı sonradan çıkar ve "geç" sayılır.
- K4: canlı olmayan kulvar (her sonuç türü için) kimseyi bekletmez; tek canlı kulvarda mesaj tutulmaz.
- K5: açık turda kimse bitirmemişken bekleyen kulvar kendi süresinde serbest kalır.
- K6: B art arda iki turda bariyeri kaçırınca A'nın mesajı artık B için bekletilmez; B bariyer süresi içinde bir tur bitirince yeniden beklenir.
- K7: `Complete` ve `Dispose` tutulanları eksiksiz basar.
- TK8: aralıkları farklı iki kulvarda tık kısa olanı izler.
- K8: `HoldBudget = 0` iken mesaj tutulmaz **ve** `WaitAsync` düz beklemedir: aralıkları farklı iki kulvar birbirini beklemez, her biri yalnız kendi aralığı kadar bekler (ortak tık yok, TK8 bağı yok).

**`InstagramCommentMergerTests` (yeni, saf):** saniyeye göre sıralı; hesap içi sıra korunur; aynı saniye dönüşümlü ve belirlenimci (aynı girdi → aynı çıktı); başlangıç kulvarı saniyeyle döner; tek girdi özdeşlik; boş partiler.

### 9.3 Hata yalıtımı testleri

**`InstagramMultiAccountHostedServiceTests` (yeni).** Gerçek OAuth servisi + tohumlanmış depo + URL'deki Instagram kimliğine göre yanıt veren işleyici; süreler kısa bir iç seçenek kaydıyla verilir.
- İki Sayfa → iki poller; iki hesabın yorumu farklı `SourceAccount` ile bus'ta.
- B 190 dönerken A akmaya devam eder; sağlıkta B `Faulted`, A `Live` (AH2, AH3). B hız sınırı yerken A etkilenmez. B'nin isteği istisna atarken A akar.
- Tek Sayfa → `SourceAccount` null ve kapı kurulmamış (AH4).
- `RemoveExtraPageAsync` sonrası o hesabın istekleri kesilir; öteki hesabın istekleri kesintisiz sürer.
- Yayın ortasında üçüncü Sayfa eklenince ilk iki hesabın filigranı bozulmaz (ilk çekimleri yeniden yutulmaz).
- Tekten çoka geçişte çalışan hesap yorum kaybetmez (filigran devri).
- `SessionEnded` bütün poller'ları durdurur. Instagram hesabı olmayan Sayfa ötekini engellemez. Aynı Instagram kimliğine çözülen iki Sayfa → tek poller.
- Kalıcı hata beklemesi token değişince kesilir.
- Oturum açıkken liste boşalınca bütün istekler kesilir; liste yeniden dolunca hesap 60 sn beklemeden, izleme aralığı içinde yeniden başlar (üst döngü 4. adım).
- Çözümleyici önbelleği oturumlar arasında korunur: ikinci oturumda çözümleme isteği yinelenmez (tek hesapta istek sayısı kilidi, AH4).

### 9.4 Arayüz testleri

- `InstagramHealthPresenterTests` (yeni, saf): her durum için Türkçe metin; yanıtsızlık eşikleri (10 sn / 45 sn) zamana göre türetilir; uyarı kapısı (canlıydı ya da kardeş canlı); 90 sn kuralı ve ölçüm başlangıcı; `BroadcastEnded` yalnız kardeş canlıyken sarı; tek Sayfa + birincil `NotLinked` → şeride girmez, şerit gizli; iki Sayfa + birincil `NotLinked` → satır ve şerit görünür, öteki hesap canlıyken sarı; iki Sayfa, hiçbiri canlı olmamış, ikisi de kalıcı hatada (ya da "hesap bilgisi okunamadı"da) → iki satır da sarı ve üst nokta bilgisi sarı (kapı muafiyeti); bir hesap canlı, öteki 90 sn'dir "canlı yayın bulunamadı" → satır sarı ama üst nokta bilgisi sarı değil (TK20); aynı anda başka bir sarı durum da varsa üst nokta bilgisi sarı; tek Sayfa + hiç canlı olmamış + kalıcı hata → nötr (kapı geçerli); tek hesap sağlıklı → şerit gizli; tek hesap sarı → görünür; iki hesap → görünür; oturum yok → gizli.
- `MainShellInstagramHealthTests` (yeni, `MainShellTestHarness`): izleyici verilmezse şerit boş ve dört satır aynı; sarı durumda üst nokta sarı ve ipucu hesabı adıyla söyler; düzelince geri döner; Instagram satırı canlıyken yeşil ve sayısız; diğer üç satır aynı.
- `ChatMessageViewModelTests` (yeni): `SourceAccount` boşken `AccountTag` null; `@x` iken `@x` (ikinci "@" eklenmez); Sayfa adı iken Sayfa adı ("@"sız); ipucunda Meta saati.
- `SettingsViewModel_InstagramAccountsTests` (yeni sınıf; aynı dosyaya ya da yanına): tek Sayfada durum metni bugünkü; iki Sayfada iki satır, birincilde `CanRemove = false`; ekle / kaldır / izin güncelle komutları; aday yokken yönlendirme metni; eksik Sayfa onayı reddedilince hiçbir şey değişmez; izin güncellemede iptal, zaman aşımı ve diğer hata üç ayrı metne düşer; onay beklerken üç düğme pasif ve `[İptal]` görünür; izin sondası "izin yok" iken "Instagram hesabı ekle" listeleme yapmadan izin metnini gösterir. `SettingsPage`'in çözülmesi mevcut `Render` yardımcısıyla sınanıyor (`SettingsViewModelTests.cs:666`, `SettingsViewModel_LicenseShippingTests` içinde).
- `MainShellViewCompositionTests` (ek): `ChatPanel` yeni bağlamalarla yüklenir (`:51`); `InstagramAccountAddDrawer` kurulur (mevcut seçici örneği `:140-143`). `UnstyledControlGuardTests` yeni düğme ve seçim öğelerinin stilli olduğunu kendiliğinden denetler.

### 9.5 Saha / pilot kontrol listesi (otomatik değil)

Önce: sunucu bayrağı teyidi (§5 madde 7). Sorguyu tasarımcı yazar, Burak çalıştırır; bayrak açıksa "Facebook izinlerini güncelle"ye basılmadan önce kapatılır.

1. **Tek hesap regresyon yayını** (yeni sürüm, ikinci hesap eklenmeden): sohbet bugünkü gibi akar; şerit görünmez; kenar çubuğunda Instagram noktası yanar. Loglardaki kota satırları birinci hesabın olağan yüzdesini verir (karşılaştırma tabanı).
2. **Ekleme:** "Instagram hesabı ekle" listesi yeni Sayfayı gösteriyor mu; göstermiyorsa "Facebook izinlerini güncelle" sonrası gösteriyor mu; Facebook onay ekranında ne göründüğü not edilir (§5 madde 4'teki iki bilinmeyen). Bir kez de onay sekmesi kapatılır: `[İptal]` akışı bırakıyor mu, ardından yeni akış açılabiliyor mu.
3. **İkinci hesapta kısa deneme yayını (TK22; mezat dışı, 10–15 dk, gerçek mezattan önce):** yorumlar etiketle geliyor mu; yeni hesabın kota yüzdesi ne hızla yükseliyor (5. maddedeki okuma kurallarıyla). Yüzde bu tempoda bir saat dolmadan %100'e varacak gibiyse ikinci hesap gerçek mezatta dakikalarca susar; o zaman gerçek mezattan önce o hesabın yoklama aralığı yükseltilir (ayrı, küçük bir değişiklik; bu belgede tasarlanmadı). Gerekçe: ikinci hesap yeni ya da az gösterimliyse sınırı düşük olabilir (§12 R2) ve bunu ilk kez müşterili bir mezatta öğrenmek pahalıdır.
4. **İki hesapla gerçek yayın:**
   - Sıra: iki hesaba art arda ve aynı anda aynı kod yazılır; ekrandaki sıra ile gerçek sıra karşılaştırılır; loglardaki ters sıra sayısı okunur.
   - Etiket: her Instagram satırında doğru hesap; ipucunda saat.
   - Bir hesabın yayını kapatılır: şeritte "yayın kesildi", üst nokta sarı; öteki hesabın sohbeti kesintisiz.
   - Bir hesabın yayını hiç açılmaz: 90 sn sonra "canlı yayın bulunamadı".
   - Facebook canlı yorum ve sil/banla birincil Sayfada bugünkü gibi çalışır (AH5).
5. **Kota ölçümü:** loglardaki `X-Business-Use-Case-Usage` satırlarından iki hesabın business-id, call_count, total_cputime, total_time ve estimated_time_to_regain_access değerleri okunur. Okuma kuralları:
   - `call_count` çağrı sayısı **değil yüzdedir** ve kayan 1 saatlik pencereyi anlatır; 24 saatlik "4800 × gösterim" formülüyle doğrudan karşılaştırılmaz. `total_cputime` ve `total_time` da yüzdedir (sorgu işleme için ayrılan payın yüzdesi); üç değer de ayrı ayrı not edilir, çünkü kısıtlama çağrı sayısından değil bu ikisinden de gelebilir.
   - **Sayaç ortak mı:** iki hesabın satırlarındaki business-id aynı mı; yalnız bir hesabın yükü artarken (ör. taşma) öteki hesabın yüzdesi de birlikte artıyor mu. İkisi de evetse kova büyük olasılıkla ortaktır; business-id aynı ama yüzdeler bağımsız hareket ediyorsa ortak değildir.
   - **Yeni hesabın sınırı düşük mü:** aynı istek temposunda yeni hesabın yüzdesi eskisinden belirgin hızlı mı yükseliyor.
   - Başlık her yanıtta geliyor mu; taşma anlarında (TK8) yüzde nasıl değişiyor.
6. **Yanıt süresi:** hesap başına p50/p95; `HoldBudget` (400 ms) bu farkı kapsıyor mu. PR-B'nin varsayılanı bu rakamla kesinleşir.
7. **Bilgisayar sayısı:** yayın sırasında Facebook'a bağlı ve "Yayın Başlat" basılı kaç bilgisayar olduğu not edilir (§11.1 soru 3). Her bilgisayar her hesabı ayrı yoklar; kota yükü bilgisayar sayısıyla katlanır ve ikinci hesap her bilgisayarda ayrı eklenir (§12 R19).

## 10. Sürüm, PR planı ve geri dönüş

- **Sürüm:** yeni bir WPF Velopack sürümü gerekir. Sunucu değişmez, deploy gerekmez. Yerel SQLite göçü yoktur. Uzantı ve overlay değişmez. Panel ve Shopper etkilenmez.
- **Güncelleme:** göç adımı yok; token dosyası yeniden yazılmaz; mevcut bağlantılar düşmez (AH1).
- **Eski sürüme dönüş:** token deposu davranışı §8.1'dedir. Özet: eski sürüm birincil Sayfayla bugünkü gibi çalışır; ek hesaplar o sürümde yoklanmaz ve göstergesi yoktur; eski sürümde bağlantı kaldırılmadıkça yeniden yükseltmede ek hesaplar geri gelir. Sürüm notuna yazılır.
- **Sıralamayı kapatma:** `HoldBudget = 0` (tek sabit) PR-B'yi PR-A davranışına indirir: mesaj tutma da ortak tık da kapanır (§8.3.6 K8). Yeni sürüm gerektirir. Yalnız ortak tempoyu kapatıp tutmayı açık bırakmak bu sabitle yapılamaz.

**PR planı** (yığılmış değil; her biri sırayla master'a; merge ve sürüm yayın saatleri dışında):

- **PR-A — iki hesabın sohbeti + etiket + sağlık** (hesaplar arası sıra: varış sırası). Acil ihtiyacı tek başına karşılar.
  1. Geriye uyum testleri + demet (`ExtraPages`, `ToString`) + atomik yazım.
  2. OAuth servisi eklemeleri (listeleme, ekleme, kaldırma, yeniden onay, kilit, hata metni; tarayıcı beklemesinde zaman aşımı, iptal ve tek akış).
  3. `ResolveDetailedAsync`, `TryGetUsage`.
  4. Poller: gözlemci, kota başlığı, filigran parametresi.
  5. `InstagramHealthMonitor`.
  6. `ChatMessage.SourceAccount` alanı (kapı bu alanı yazdığı için kapıdan önce).
  7. `InstagramMergeGate` (etiket + ölçüm, düz geçiş).
  8. Hosted service: üst döngü, hesap başına döngü, PageId farkı.
  9. Sohbet satırı etiketi; ana ekran: sağlık şeridi, üst nokta, kenar çubuğu noktası; `AppHost` kayıtları.
  10. Ayarlar: liste, ekleme çekmecesi, kaldırma, izin güncelleme.
  11. Pilot (§9.5).
- **PR-B — hesaplar arası sıra.** Poller tempo kancası, kapıda K1–K8, `InstagramCommentMerger`. `HoldBudget` varsayılanı pilot rakamına göre. PR-B'nin uygulama planı PR-A'nın pilot rakamları geldikten sonra ayrıca yazılır; K1–K8 o plandan önce bağımsız bir gözden geçirmeden geçer (§13: bu bağlama hakem incelemesinden geçmedi). Onaydan sonra yazılacak ilk plan yalnız PR-A'yı kapsar.

PR-A inceleme için büyük gelirse "motor" (1–8) ve "arayüz" (9–10) olarak ikiye bölünebilir; motor tek başına birleşirse ikinci hesap eklenemediği için çok hesap yolu uykuda kalır.

## 11. Açık sorular ve bırakılanlar

### 11.1 Sahibe sorular (yalnız Burak cevaplayabilir)

1. **(Ö6; tasarımın temeli.)** Söylediğin: ikinci Instagram hesabı şu an hiçbir Facebook Sayfasına bağlı değil. Bu tasarım o hesabı ancak bir Sayfaya bağlanırsa okuyabilir. İki yoldan hangisi?
   - **(a) Sayfa aç ve bağla:** ikinci hesap için ayrı bir Facebook Sayfası açıp Instagram hesabını ona bağlarsın ve iki Sayfayı aynı Facebook kullanıcısıyla yönetirsin (tek Facebook girişi). Bu belge bu yolu varsayar.
   - **(b) İkinci hesap Sayfasız kalsın:** o zaman bu tasarım geçersizdir. Sayfasız yolda canlı yorum okuma Meta belgesinde yok; yalnız webhook belgeli. Ayrı bir araştırma, sunucu → masaüstü kanalı ve büyük olasılıkla ayrı App Review gerekir; bu iş kısa sürede çıkmaz.
   - Ara durum: Sayfa açarsın ama ikinci Sayfayı **başka bir Facebook kullanıcısı** yönetir. Bu da kapsam dışıdır (ikinci Facebook girişi); söylersen tasarım ona göre genişletilir.
2. İki hesapta **aynı mezat** mı yayınlanacak (aynı ürün, aynı kod, tek stok)? Evetse iki sohbet tek akışta birleşir ve "ilk yazan" iki hesap arasında ortaktır (Ö1). Farklı ürünlerse birleşik sıra anlamsızlaşır ve tasarım değişir.
3. Yayın gecesinde Instagram sohbeti **kaç bilgisayarda** görünmeli; bugün Facebook'a bağlı olup "Yayın Başlat" basılan kaç bilgisayar var? Ek hesap listesi her bilgisayarın kendi şifreli dosyasında durur ve her bilgisayar her hesabı saniyede yaklaşık bir istekle ayrı yoklar; otomatik fren yoktur.
   - **Tek bilgisayar:** plan bu belgedeki gibidir.
   - **Birden çok:** ikinci hesap her bilgisayarda ayrı eklenir ve Instagram'a giden istek sayısı bilgisayar sayısıyla katlanır; az gösterimli ikinci hesap yayın ortasında hız sınırına girebilir. O zaman pilottan önce plana bir önlem görevi girer (yoklamayı tek bilgisayarla sınırlamak ya da aralığı yükseltmek) ve kurulum/pilot adımları bilgisayar başına yazılır (§12 R19).

Bu üç soru dışında tasarımın ilk taslağında sahibe sorulan altı konu, sahibin cevabı yapılacak işi değiştirmediği ya da sonradan küçük bir değişiklikle çevrilebildiği için tasarımcı kararına çevrildi; §11.2'de "eski soru" notuyla işaretlidir (TK7, TK11, TK12, TK19, TK20, TK21). Yedinci konu olan sunucu bot bayrağının teyidi bir tasarım sorusu değil pilot ön koşuludur (§5 madde 7, §9.5).

### 11.2 Tasarımcının sahibe sormadan verdiği kararlar

| No | Karar | Yer |
|---|---|---|
| TK1 | Token deposuna sürüm alanı eklenmez; biçim salt eklemelidir (`ExtraPages`), birincil Sayfa üst düzey alanlarda kalır | §8.1 |
| TK2 | Depo yazımı atomik yapılır (depo sınıfındaki tek değişiklik); token taşıyan iki kayıtta `ToString` token basmaz | §8.1 |
| TK3 | Birincil Sayfa yerinde değiştirilemez ve tek başına kaldırılamaz; Facebook yorum ve moderasyon hep birincilden çalışır | §8.2 |
| TK4 | Yeniden onay ayrı bir akıştır: ekleri korur, birincili değiştirmez, eksik Sayfada kaydetmeden önce onay ister; `ConnectAsync` dışarıdan aynı kalır (tek fark TK17'deki zaman aşımı) | §8.2 |
| TK5 | Hesap ekleme tarayıcısızdır ve ayrı bir çekmeceyle yapılır; mevcut Sayfa seçiciye dokunulmaz | §8.2, §8.6.1 |
| TK6 | Tek hesapta poller doğrudan ChatBus'a yazar; kapı kurulmaz, etiket basılmaz | §8.3.2 |
| TK7 | Sıra iki PR'da gelir: önce varış sırası + ölçüm, sonra ortak tık + 400 ms bariyer; 2,5 sn'lik tutma tamponu seçilmedi. **Eski soru:** ilk sürüm sıralamasız çıkar (iş acil; iki hesap arasında yaklaşık 1 sn'ye kadar ters sıra olabilir, bugün Instagram ile TikTok/YouTube arasında da böyledir); sıralamanın gerekip gerekmediği ve 0,4 sn'lik bekletme ilk yayınların ölçümüyle kesinleşir | §7.3, §8.3.5–6, §10 |
| TK8 | Ortak tık en hızlı isteyen hesabı izler (taşmada iki hesap da sıklaşır) | §8.3.6 |
| TK9 | Sağlık yüzeyi sohbet paneli şeridi + üst nokta + kenar çubuğu noktasıdır; `ShellBanners`'a şerit eklenmez. Uyarı yalnız hesap canlıydıysa ya da başka hesap canlıysa sarı olur. **Doğrulama düzeltmesi (AH3):** 2 ve üzeri Sayfada "kalıcı hata" ve "hesap bilgisi okunamadı" bu kapıdan muaftır (her zaman sarı); birincil `NotLinked` yalnız ek Sayfa yokken şeritten gizlenir | §8.5, §8.6.3–4 |
| TK10 | Eşikler: yanıtsızlık 10 sn (canlıyken) / 45 sn (beklerken); "canlı yayın bulunamadı" 90 sn | §8.5 |
| TK11 | Etiket tam Instagram kullanıcı adıdır, satırın sağına yaslıdır; ipucunda Meta saati vardır. **Eski soru:** hesap başına kısa takma ad ("Ana", "Yedek") eklenmedi; yeni bir ayar alanı gerektirir, istenirse sonradan eklenir | §8.6.2 |
| TK12 | Tekrar süzgecine dokunulmaz: aynı izleyici + aynı metin 2 sn içinde iki hesapta tek satırdır. **Eski soru:** iki satır istenirse süzgeç anahtarına hesap eklenir (küçük, ayrı değişiklik) | §8.7 |
| TK13 | En çok 4 Sayfa | §8.2 |
| TK14 | Hesap listesi değişimi PageId farkıyla işlenir; tekten çoka geçişte filigran devredilir; hesap kaldırma yoklamayı en geç 2 sn'de durdurur. **Doğrulama düzeltmesi:** oturum açıkken liste boşalırsa üst döngü 60 sn'ye düşmez, 2 sn'lik izlemede kalır | §8.3.2–3 |
| TK15 | Hesap çözülemediğinde ya da yokken bekleme bugünkü 5 dk kalır | §8.3.2 |
| TK16 | Kalıcı hata beklemesi o Sayfanın token'ı değişince kesilir | §8.3.2 |
| TK17 | Tarayıcı onayı beklemesi 5 dakikada zaman aşımına uğrar ("Facebook'a Bağlan" dahil); izin güncellemede arayüzden iptal vardır; aynı anda tek tarayıcı akışı çalışır. `error=access_denied` eşlemesi değiştirilmez | §8.2, §8.6.1 |
| TK18 | `HoldBudget = 0` yalnız tutmayı değil ortak tıkı da kapatır (`WaitAsync` düz bekleme); PR-B'den PR-A davranışına tek sabitle dönülür | §8.3.6 K8, §10 |
| TK19 | AH4 "yoklama yolu, sohbet akışı, gecikme ve Graph yükü değişmez" diye okunur; tek hesapta da görünen yedi davranış değişikliği sayılıdır. **Eski soru:** AH4 Burak'ın değil tasarımcının koyduğu bir anti-hedeftir, okumasını da tasarımcı yapar; yedi madde teslim özetinde gösterilir, Burak'ın itiraz ettiği madde 2 ve üzeri Sayfa koşuluna bağlanır | §4, §8.3.2, §12 R13 |
| TK20 | "Canlı yayın bulunamadı" (öteki hesap canlıyken bu hesapta 90 sn'dir yayın yok) yalnız şeritte sarıdır; üst bardaki sohbet noktasını sarartmaz. **Eski soru** (iki yayın aynı anda mı açılıyor, tek hesaplı gece olacak mı): cevap ne olursa olsun çalışan kural seçildi; tek hesaplı gecede gece boyu yanan alarm olmaz | §8.5, §8.6.3–4 |
| TK21 | Aynı saniyede iki hesaptan gelen yorumlar dönüşümlü dizilir (PR-B); "şu hesap her zaman önce" seçilmedi. **Eski soru** | §8.3.6 |
| TK22 | İlk gerçek iki hesaplı mezattan önce ikinci hesapta 10–15 dakikalık deneme yayını yapılır; kota yüzdesinin eğimi orada okunur | §9.5 madde 3 |

### 11.3 İnşaatçıya bırakılanlar

- Birleştirme kapısının iç yapısı (kilit, zamanlayıcı, veri yapıları); K1–K8 ve §9.2 testleri bağlayıcıdır.
- `AcquireUserTokenAsync`'in test dikişi (dinleyici ve tarayıcı açma soyutlaması, zaman aşımı süresinin testte kısaltılması) ve "tek tarayıcı akışı" korumasının biçimi; §8.2'deki üç kural ve §9.1 testleri bağlayıcıdır.
- Üst döngünün hesap döngülerine "liste değişti / token değişti" bilgisini nasıl ilettiği (ortak iptal kaynağı, sinyal ya da 2 sn'lik yoklama).
- Sağlık ve kulvar gözlemcilerinin poller'a tek gözlemci olarak nasıl birleştirildiği.
- Hosted service'in test dikişi (süreleri taşıyan iç seçenek kaydı).
- `ListAddablePagesAsync`'te izlenecek en çok sayfa sayısı ve Graph hata gövdesinin okunma biçimi.
- Yeni dosyaların klasörleri ve küçük ViewModel adları; XAML'de kesin ölçüler (etiket genişliği, şerit boşlukları) mevcut tema belirteçleriyle.
- Sağlık şeridindeki uyarı işaretinin biçimi (renkli nokta ya da simge); renkler mevcut `OD.Brush.Success` / `OD.Brush.Amber` / `OD.Brush.TextMute`.
- PR-A'nın "motor" ve "arayüz" olarak bölünüp bölünmeyeceği.

## 12. Riskler ve bilerek yapılmayanlar

- **R1. Meta onay kapsamı (doğrulanmadı).** Sonradan açılan Sayfa saklı girişle görünmeyebilir; o zaman tarayıcılı izin güncellemesi şarttır. O ekranda eski Sayfanın işareti kaldırılırsa erişimi düşebilir. Önlemler: uyarı metinleri, kaydetmeden önce eksik Sayfa onayı, birincilin kaymaması, eksik girdinin silinmeyip sağlıkta görünmesi. "Vazgeç" Meta'daki izni geri getirmez.
- **R2. Kota.** Sayaç uygulama + kullanıcı çiftine özelse iki hesap aynı kovayı tüketir. Formül gösterime bağlı olduğundan yeni ikinci hesapta sınır düşük olabilir. Otomatik fren yoktur; sınıra giren hesap dakikalarca susar ve bu görünür. Başlığın her yanıtta gelip gelmediği doğrulanmadı. Tek önlem pilot ölçümüdür ve ölçüm yanlış birimle okunursa sonuç da yanlış çıkar: başlıktaki `call_count` sayaç değil, kayan 1 saatlik pencerenin yüzdesidir; kısıtlama `total_cputime` / `total_time` üzerinden de gelebilir (okuma kuralları §9.5 madde 5).
- **R3. Ortak tıkın yük bağı (TK8).** Sürekli taşmada sakin hesabın istek sayısı yaklaşık 3 katına çıkar. Hakemlerden biri buna itiraz etti. Pilotta ölçülür. Sorun çıkarsa iki yol vardır: (a) tek sabitle tam geri dönüş: `HoldBudget = 0` hem tutmayı hem ortak tıkı kapatır (§8.3.6 K8), sıralama da gider; (b) tutmayı açık bırakıp kulvarları kendi aralığıyla yoklamak: bu ayrı bir değişikliktir (tek sabitle yapılamaz) ve bedeli taşma sırasında sıra güvencesinin gevşemesidir. PR-A'da ortak tempo hiç yoktur.
- **R4. Sıra kesinliği.** §8.3.6 G2–G3. Aynı saniyede iki hesaptan gelen aynı kodda operatör yanlış kişiye satabilir; elindeki tek bilgi ipucundaki saattir. PR-A'da hesaplar arası sıra hiç kurulmaz.
- **R5. `HoldBudget` tahmindir.** Gerçek yanıt süreleri ölçülmedi; pilot rakamına göre ayarlanır.
- **R6. Eski sürüme dönüş.** Ek hesaplar sessizce yoklanmaz. Serileştirici davranışı repo dışı betikle doğrulandı; CI testi yazılmadan birleştirilmez.
- **R7. Yeniden onaydan sonra eski Sayfa token'ı (doğrulanmadı).** Geçersizleşirse sağlam hesap bir kez kalıcı hatayla çıkar ve yeni token'la hemen yeniden kurulur; yeni poller'ın ilk çekimi yutulur (birkaç saniyelik yorum kaybolabilir). **Facebook tarafı da etkilenebilir (AH5):** Facebook canlı yorum akışı birincil Sayfa token'ını bağlanırken alır ve akış boyunca onu kullanır (`FacebookChatHostedService.cs:200`, `:257-264`); eski token düşerse akış kopar ve geri çekilmeyle yeniden bağlanır (yeni kimliği `:200`'de okur), yani yayın ortasında "Facebook izinlerini güncelle" Facebook yorumlarında da kısa bir kesinti yapabilir. Bu tasarımın Facebook koduna dokunmasından değil, Meta'nın eski token'ı düşürmesinden doğar ve bugünkü "Bağlantıyı Kaldır + Facebook'a Bağlan" yolunda da vardır. Önlem: düğmenin ipucu yayın sırasında basılmamasını söyler (§8.6.1).
- **R8. Çözümleme hatasında 5 dk (TK15).** Yayın başındaki tek bir ağ hatası o hesabı 5 dk bekletir. Tek hesap davranışı aynı kalsın diye değiştirilmedi. İki ve üzeri Sayfada şeritte sarı görünür (§8.5 kapı muafiyeti); tek Sayfalı kurulumda uyarı kapısı gereği (hesap o oturumda henüz canlı olmadı) şerit çıkmaz, bugünkü gibi yalnız loglanır.
- **R9. Oturum başında 60 sn'ye kadar geç başlama.** Mevcut davranış; iki hesap birlikte başlar ama yayının ilk dakikasındaki yorumlar kaçabilir. Bu tasarım değiştirmiyor.
- **R10. Kullanıcı token'ı yaklaşık 60 günde ölür ve `ExpiresAt` hiçbir yerde okunmuyor.** Tarayıcısız ekleme o zaman "izinleri güncelle"ye düşer. Sayfa token'ları çalışmayı sürdürdüğü için önceden fark edilmeyebilir. Süre dolmadan uyarı bu tasarımda yok.
- **R11. Sohbet hacmi iki katına çıkar.** Liste 500 satır (`MainShellViewModel.cs:102`), bekleyen kuyruk 1000'de en eskiyi atar (`:1240`, `:1263`). Sınırlar değişmiyor.
- **R12. `live_media` için Sayfa token'ı belgeli değil.** İkinci Sayfada çalışmazsa o hesap "bağlantı yenilenmeli" olarak görünür; öteki etkilenmez.
- **R13. Tek hesaplı kurulumlarda görünür değişiklikler** (yoklama temposu ve yükü aynı kalır; AH4'ün dar okuması, TK19). Tam liste §8.3.2 "Bilerek değişen davranışlar"dadır, yedi madde: yayın ortasında bağlantı kaldırma yoklamayı 2 sn'de durdurur; kalıcı hata beklemesi token yenilenince kesilir; kenar çubuğunda Instagram noktası yayında yanar; canlıyken kopan hesap için şerit ve sarı üst nokta görünür; Ayarlar'da iki yeni düğme çıkar; izin uyarısının metni yeni düğmeye yönlenir; tarayıcı onayı 5 dakikada zaman aşımına uğrar. En ağır sonuç ilkindedir: yayın ortasında "Bağlantıyı Kaldır" + "Facebook'a Bağlan" yapan operatör bugün Instagram sohbetini kaybetmezken yeni sürümde tarayıcı onayı boyunca kaybeder ve yeni poller'ın ilk çekimi yutulur; Facebook yorumları ise akmaya devam eder (iki platform farklı davranır). Tasarımcının kararıdır (TK19); teslim özetinde Burak'a gösterilir.
- **R14. N > 2 yalnız kâğıt üzerinde.** Tasarım 4 hesaba kadar geneldir ama yalnız 2 hesapla doğrulanacak.
- **R15. OrderDeck dışı.** İkinci hesabın canlı yayın açma koşulları ve aynı içeriğin iki hesaba aynı anda verilmesi Meta tarafında doğrulanmadı.
- **R16. Birincil `NotLinked` kuralının bedeli ve kalan boşluk.** Ek Sayfa varken birincil `NotLinked` şeride girer (§8.5, AH3). Bedeli: birincil Sayfası bilerek Instagram'sız olan ve Instagram'ı yalnız ek Sayfadan okuyan bir kurulumda bu satır, ek hesap canlıyken her yayında sarı görünür (yanlış alarm). Çözümleyici "hiç bağlı olmadı" ile "bağ düştü"yü ayıramadığı ve depo biçimi değişmediği için bu ayrım yapılamıyor. Böyle bir kurulum bugün yok (Instagram bugün yalnız birincil Sayfadan okunuyor) ve Burak'ın durumu değil; ortaya çıkarsa "bu Sayfada Instagram beklenmiyor" işareti ayrı bir iştir. Kalan boşluk: iki Sayfa da oturum başından `NotLinked` ise hiçbir hesap canlı olmadığı için satırlar sararmaz; şerit iki nötr satırla görünür.
- **R17. Ö6 varsayımı.** Tasarımın tamamı, ikinci hesabın ayrı bir Sayfaya bağlanacağına ve iki Sayfayı aynı Facebook kullanıcısının yöneteceğine dayanır; Burak bunu söylemedi (§3.1, §11.1 soru 1). Varsayım tutmazsa bu belgenin §7.1 kararı ve ona bağlı §8 geçersizdir.
- **R18. Tarayıcı sekmesinin kapatılması algılanamaz.** Kullanıcı Facebook onay sekmesini kapatırsa OrderDeck bunu bilemez; akış `[İptal]`e basılana ya da 5 dakikalık zaman aşımına kadar bekler ve o sürede yeni bir akış başlatılamaz (§8.2 TK17). "Facebook'a Bağlan"da arayüzden iptal yoktur; yalnız zaman aşımı vardır.
- **R19. Birden çok bilgisayar (§11.1 soru 3).** Sohbet bilgisayarlar arasında eşitlenmez; her bilgisayar kendi Facebook bağlantısıyla her hesabı ayrı yoklar. Token dosyası bilgisayara (Windows kullanıcısına) özeldir, yani ek hesap her bilgisayarda ayrı eklenir. Hesap başına Instagram'a giden istek sayısı "bilgisayar sayısı × saniyede yaklaşık bir" olur; bu bugün birinci hesap için de böyledir, yeni olan aynı yükün az gösterimli ikinci hesaba da binmesidir. Bu belgede önlem yoktur; Burak birden çok bilgisayar derse plan aşamasında bir önlem görevi eklenir.

## 13. Tasarım incelemesi — hakem bulguları eşlemesi

İki hakem de "en küçük güvenli değişiklik" tasarımını seçti (78 ve 84 puan). Öbür iki tasarım: "sıra ve doğruluk önce" (tek pompa) ve "operatör deneyimi önce" (çoklu seçim + koordinatör).

**Aşılananlar**

| Aşı | Kaynak | Yer |
|---|---|---|
| `ResolveDetailedAsync`; birincilde "hesap yok" sessiz (doğrulamada daraltıldı: yalnız ek Sayfa yokken) | sıra | §8.3.4, §8.5 |
| Uyarı kapısı; 90 sn "canlı yayın bulunamadı"; `WasLiveThisSession` | operatör | §8.5 |
| "Yayın bitti" yalnız kardeş canlıyken sarı | sıra | §8.5 |
| Ekleri koruyan yeniden onay + eksik Sayfa onayı | sıra + operatör | §8.2 |
| Atomik yazım; değiştiriciler tek kilit altında | sıra + operatör | §8.1, §8.2 |
| Ortak tık + 400 ms bariyer + saf birleştirici (PR-B) | sıra | §8.3.6 |
| PR-A'da ölçüm (ters sıra sayacı, yanıt süresi) | sıra | §8.3.5 |
| PageId farkı; filigran devri; token değişiminde yeniden başlatmama | sıra + operatör | §8.3.3 |
| Oturum kapısı tek yerde, hesaplar aynı anda başlar | sıra | §8.3.2 |
| Boş gövde hatasından URL'nin çıkarılması | operatör | §8.2 |
| Ayrı listeleme: Instagram alanı + `limit=100` + sayfalama | sıra + operatör | §8.2 |
| Aynı Instagram kimliğine çözülen Sayfa atlanır; üst sınır 4 | sıra | §8.2, §8.3.2 |
| Kota başlığının ayrıştırılması (business-id) | sıra | §8.3.4, §8.8 |
| Kenar çubuğu Instagram noktası sağlıktan; sayı gizli | sıra + operatör | §8.6.4 (dar biçimiyle) |
| Üst bar sohbet noktası | operatör | §8.6.4 |
| Etiket ipucunda Meta saati | operatör | §8.6.2 |
| Meta onay ekranı uyarı metinleri | operatör | §8.6.1 |
| Sunucu bot bayrağı: soru + pilot ön koşulu | sıra + operatör | §5, §11.1 |

**İşaretlenen hatalar ve sonuçları** (kodda doğrulanarak)

| Bulgu | Sonuç |
|---|---|
| Kazanan: yalnız Facebook kullanan kuruluma her yayında sarı şerit | Düzeltildi: tek Sayfalı kurulumda birincil `NotLinked` şeride girmez (ek Sayfa varken girer, §8.5); çözümleyicinin üç durumu ayrıldı |
| Kazanan: "hesap yok" ile "çağrı düştü" aynı metin | Düzeltildi: `ResolveFailed` ayrı durum, ayrı metin |
| Kazanan: hiç canlı olmamış ikinci hesap nötr kalıyor | Düzeltildi: 90 sn kuralı |
| Kazanan: yeniden bağlanma ekleri siliyor; tek Sayfa otomatik seçimi (`FacebookOAuthService.cs:201-202`) birincili kaydırabiliyor | Düzeltildi: `ReauthorizeAsync` o dalı kullanmaz; `ConnectAsync` yalnız bağlı değilken erişilir |
| Kazanan: oturum kapısı hesap başına, iki hesap 60 sn'ye kadar farklı anda başlıyor | Düzeltildi: kapı üst döngüde |
| Kazanan: liste değişince bütün döngüler yeniden kurulup ilk çekim yutuluyor | Düzeltildi: PageId farkı + filigran devri |
| Kazanan: 2,5 sn'lik tutma tamponu bütün Instagram yorumlarını geciktiriyor | Değiştirildi: 400 ms bariyer (TK7) |
| Kazanan: yarım yazım riski açık | Düzeltildi: atomik yazım |
| Kazanan: `MainShellViewCompositionTests`'te `ChatPanel` `:53` yazılmış | Doğrusu `:51` (`:53` `PrintQueuePanel`); bu belgede düzeltildi |
| Sıra: "tek hesapta JSON bugünküyle aynı" iddiası mevcut seçeneklerle tutmuyor | Doğrulandı (`"ExtraPages":null` yazılıyor); iddia bu belgede kurulmadı |
| Sıra: kalıcı sağlık şeridi geçici şerit hücresinde | Alınmadı; şerit sohbet panelinde |
| Sıra: tek hesap yolu da yeni pompadan geçiyor | Alınmadı; poller döngüsü yerinde |
| Sıra: etiket mesajın başında, kod sütunu kayıyor | Alınmadı; etiket sağa yaslı |
| Sıra: taşmada iki hesap da sıklaşıyor (yük bağı) | Bilerek alındı (TK8); itiraz §12 R3'te |
| Sıra: kenar çubuğunun dört satırına dokunuş | Daraltıldı: yalnız sayı sütununun görünürlük bağı |
| Operatör: birincil Sayfanın kaldırılması / değiştirilmesi moderasyonu yanlış token'a yollar (`FacebookModerationService.cs:116-123`) | Alınmadı (TK3) |
| Operatör: `ConnectAsync` imzası ve Sayfa seçici değişiyor | Alınmadı; ikisi de aynen |
| Operatör: birincil Sayfa depoda iki yerde | Alınmadı; ekler yalnız ek listede |
| Operatör: çoktan çoka yeniden kurulumda filigran devri tanımsız | Giderildi: token değişimi sağlıklı poller'ı yeniden başlatmaz |
| Operatör: aralıklar ayrışınca farklı saniyelerde ters sıra | Giderildi: ortak tık en hızlıyı izler (TK8) |
| Sıra + operatör: token taşıyan kayıtlarda `ToString` | Düzeltildi: iki kayıtta override |
| Ortak: yeni Sayfa saklı girişle görünmeyebilir | Yeniden onay asıl yol gibi tasarlandı (§8.2, §8.6.1) |
| Keşif: tekrar süzgeci "varsayılan kapalı" | Yanlış; varsayılan açık (`SpamFilterSettings.cs:14`, `:19`). §8.7 buna göre yazıldı |

**Hakemlerin ayrıştığı ve bu belgenin karar verdiği yerler:** sıralama mekanizması (TK7), taşmada ortak tempo (TK8), kenar çubuğuna dokunup dokunmamak (§8.6.4'teki dar biçim). Ortak tıkın poller'a tempo kancasıyla bağlanması (§8.3.6 bağlama notu) hakemlerin önerdiği iki bağlamadan farklıdır ve hakem incelemesinden geçmemiştir; plan aşamasında gözden geçirilmelidir.

**Doğrulama düzeltmeleri (16.11.2026).** Bağımsız bir doğrulayıcının altı bulgusu (ikincisi iki parça; aşağıda yedi satır) kodda ve Meta belgesinde yeniden bakılarak işlendi; hepsi gerçek çıktı, reddedilen yok.

| Bulgu | Sonuç |
|---|---|
| "KESİN" başlığı altında Burak'ın söylemediği plan (ayrı Sayfa + tek Facebook kullanıcısı) | Düzeltildi: §3.1'de yalnız söylenen kaldı; plan Ö6 varsayımı olarak §3.2'ye taşındı; §4, §5, §7.1 işaretlendi; soru 1 alternatifiyle soruyor; R17 |
| AH3: iki Sayfalı kurulumda birincil `NotLinked` şeritten gizleniyor, şerit de kapanıyor | Düzeltildi: gizleme yalnız ek Sayfa yokken (§8.5, §8.6.1, §8.6.3); test eklendi (§9.4); bedeli R16 |
| AH3: iki hesap da oturum başından bozuksa hiçbir şey sararmıyor | Düzeltildi: 2 ve üzeri Sayfada "kalıcı hata" ve "hesap bilgisi okunamadı" kapıdan muaf (§8.5). Kodda bakınca geçersiz token'ın oturum başında poller'a değil çözümlemeye takıldığı görüldü; muafiyet bu yüzden ikisini de kapsar, §8.2 5. adımdaki metin iddiası da düzeltildi |
| AH4: "davranış değişmez" derken tek hesapta altı davranış değişiyor; sahibe yalnız biri soruluyor | Gecikme ve yük iddiası doğru. Metin düzeltildi: AH4'ün dar okuması açıkça yazıldı (TK19), değişiklikler tek listede (§8.3.2; zaman aşımıyla yedi), o günkü soru 9 hepsini soruyordu (bugün TK19, aşağıdaki "Teslim öncesi düzeltmeler"), R13 tam. Liste boşalınca üst döngünün davranışı ve Facebook–Instagram farkı yazıldı |
| `HoldBudget = 0` "PR-A davranışı" iddiası tutmuyor (ortak tık ve TK8 bağı sürüyor) | Düzeltildi: K8 eklendi (kapalı kipte `WaitAsync` düz bekleme), §9.2 testi, §10 ve R3 buna göre (TK18) |
| Tarayıcının kapandığı algılanamıyor; bekleme süresiz; `access_denied` "iptal"e değil "hata"ya düşüyor | Düzeltildi: zaman aşımı + arayüzden iptal + tek tarayıcı akışı (§8.2 TK17); arayüz metinleri üç ayrı sonuca ayrıldı (§8.6.1); testler (§9.1, §9.4); R18 |
| Kota başlığındaki `call_count` sayaç değil, 1 saatlik pencerenin yüzdesi | Düzeltildi: §5, §8.3.4 (alan adları `...Percent`, `MinutesToRegainAccess`), §8.8, §9.5 kota maddesi (bugünkü numarasıyla madde 5), R2. Meta sayfası 16.11.2026'da yeniden okundu: üç oran da yüzde |

Aynı okumada bulunan ek düzeltme: hesap çözümleyicisinin önbelleği bugün oturumlar arasında korunuyor (`InstagramChatHostedService.cs:96-99`); hesap başına çözümleyici oturum başına yeniden kurulsaydı tek hesapta oturum başına bir Graph isteği eklenirdi. §8.3.2'ye "çözümleyiciler servis ömrünce yaşar" kuralı ve §9.3'e testi eklendi.

**Teslim öncesi düzeltmeler (16.11.2026).** Doğrulayıcının "küçük" diye işaretlediği altı bulgu yukarıdaki turda işlenmemişti; koda yeniden bakılarak işlendi. Ayrıca inşaatçı kontrolünün ve ilk taslağın sahibe yönelttiği sorular ayıklandı.

| Konu | Sonuç |
|---|---|
| Etiket "@" + Sayfa adı olabiliyordu (kullanıcı adı null gelebilir, `InstagramAccountResolver.cs:81-82`) | "@" kulvar etiketinde eklenir; `SourceAccount` gösterime hazır metindir, `AccountTag` onu aynen kullanır (§8.3.2, §8.4, §8.6.2, §9.4) |
| Kapı ölçütü bir yerde "Sayfa sayısı", başka yerde "Instagram hesabı sayısı" | Ölçüt bağlı Sayfa sayısıdır; gerekçesi ve tek sonucu yazıldı (§8.3.2 üst döngü 3. adım, §8.4) |
| `SettingsViewModelTests` adında sınıf yok | Gerçek sınıf adları yazıldı; yeni testler `SettingsViewModel_InstagramAccountsTests` (§9.1, §9.4) |
| PR-A'da kapı adımı `SourceAccount` alanından önceydi (derlenmez); TK6 gövdede işaretsizdi; bölüm numarası 2'den başlıyordu | Adımlar yeniden sıralandı (§10); TK6 işaretlendi (§8.3.2); Özet §1 oldu |
| Tarayıcısız listelemede "izin yoksa yönlendirilir" iddiasının karşılığı yoktu (kod 100 "yayın bitti" sayılıyor, alan sessizce düşebiliyor); yeniden onayın hangi listelemeyi kullandığı yazılmamıştı | Bu uca özel hata eşlemesi (100 dahil), izin sondasına bağlı arayüz kuralı ve sayfalamalı ortak listeleme yazıldı (§8.2, §8.6.1, §9.1) |
| Yeniden onaydan sonra Facebook canlı akışı da eski token'la kopabilir | R7'ye ve düğme ipucuna yazıldı (§8.6.1, §12) |
| İlk taslakta sahibe on soru vardı | Üçe indi (§11.1: Sayfa + aynı Facebook kullanıcısı, aynı mezat, bilgisayar sayısı). Kalanlar tasarımcı kararı oldu (§11.2'de "eski soru"): TK7, TK11, TK12, TK19, TK20, TK21. Bot bayrağı pilot ön koşuludur. Yukarıdaki doğrulama tablosundaki "soru 9" ifadesi o günkü numaradır; konu bugün TK19'dur |
| "Canlı yayın bulunamadı" üst noktayı da sarartıyordu | Yalnız şeritte sarı (TK20): tek hesaplı gecede gece boyu yanan alarm, aynı noktanın "60 sn'dir mesaj yok" işaretini örterdi (`MainShellViewModel.cs:1354-1385`) |
| Kota riski ilk kez müşterili mezatta ölçülecekti | Önce ikinci hesapta kısa deneme yayını (TK22, §9.5 madde 3) |
| Birden çok bilgisayarın yükü yalnız bir pilot maddesiydi | Sahibe soru 3 ve R19 |
