# Instagram Çoklu Hesap Sohbeti (PR-A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** İki (genelde N, en çok 4) Instagram hesabının canlı yorumlarını aynı anda OrderDeck sohbetine akıtmak: ikinci hesap Ayarlar'dan mevcut Facebook girişi bozulmadan eklenir ve tek başına kaldırılır, her Instagram yorumu hangi hesaptan geldiğini satırda gösterir, susan hesap ana ekranda görünür; hesaplar arası sıra bu PR'da varış sırasıdır.

**Architecture:** Token demeti yalnız eklemeli bir `ExtraPages` listesi kazanır; birincil Sayfa bugünkü beş alanda kalır, bu yüzden Facebook canlı yorum ve moderasyon yolu ile eski sürümün okuduğu dosya biçimi değişmez. `InstagramChatHostedService` bağlı her Sayfa için bağımsız bir hesap döngüsü (`InstagramAccountLoop`: kendi çözümleyicisi, poller'ı, filigranı, beklemeleri) çalıştırır; iki ve üzeri Sayfada poller'lar `InstagramMergeGate` kulvarına yazar (mesaj `SourceAccount` ile etiketlenip hemen basılır, tur süresi ve ters sıra ölçülür), tek Sayfada yoklama yolu bugünküyle aynıdır. Hesap başına durum tekil `InstagramHealthMonitor`'da tutulur; saf `InstagramHealthPresenter` onu sohbet şeridine, üst nokta ipucuna ve kenar çubuğu noktasına çevirir; Ayarlar hesapları listeler, tarayıcı açmadan ekler, kaldırır ve tarayıcılı izin güncellemesini ekleri koruyarak yürütür.

**Tech Stack:** .NET 10 (`net10.0` / `net10.0-windows`), WPF + CommunityToolkit.Mvvm, Meta Graph API v22.0 (Facebook Login; `HttpClient` + `System.Text.Json`), DPAPI (`ProtectedData`), `HttpListener` (loopback OAuth geri çağrısı), `TimeProvider`, `Microsoft.Extensions.Logging` (Serilog); test: xunit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-11-16-instagram-coklu-hesap-design.md` (plan spec'ten yola çıkar; uygulayıcı ikisini de okur)

## Global Constraints

Her görevin gereklilikleri bu bölümü örtük olarak içerir.

- **AH1:** Güncellemede mevcut Facebook / Instagram bağlantısı düşmez: token dosyası güncellemede yeniden yazılmaz (yalnız Bağlan / Instagram hesabı ekle / Kaldır / Facebook izinlerini güncelle anında yazılır); sürüm alanı ve göç adımı yok.
- **AH2:** Bir hesabın hatası öbürünün sohbetini kesmez: çöküş sayacı ve beklemeler hesap başınadır.
- **AH3:** Bir hesabın sessizce susması operatörden gizli kalmaz.
- **AH4 (TK19 dar okuma):** Tek Sayfada yoklama yolu, sohbet akışı, gecikme ve Graph yükü aynıdır: 1 sn taban / 0,3 sn taşma tabanı, yayın yokken 10 sn, 5 ardışık geçici hatada çıkış + 30 sn, kalıcı hatada 5 dk, oturumu en geç 60 sn'de görme, ilk çekim yutulur; kapı kurulmaz, `SourceAccount` null. Tek hesapta bilerek değişenler yalnız spec §8.3.2'deki yedi madde + dört yan farktır.
- **AH5 (TK3):** Facebook canlı yorum ve moderasyon yolu değişmez; `GetPageCredentialsAsync` hep birincil Sayfayı döner; birincil yerinde değiştirilemez, tek başına kaldırılamaz.
- **Dokunulmayanlar (§8.9):** `OrderDeck.Chat/Facebook/FacebookModerationService.cs`, `OrderDeck.Chat/Ingestors/Facebook/*`, `OrderDeck.App/Views/Drawers/FacebookPagePickerDrawer.*`, `InstagramCommentWatermark.cs` (içerik), `InstagramLiveMediaParser.cs`, `InstagramPermissionProbe.cs`, `OrderDeck.Core/Chat/ChatBus.cs`, `SpamFilter.cs` (TK12), `ViewerCountTracker.cs`, `OrderDeck.Core/Settings/AppSettings.cs`, `OrderDeck.App/Views/Shell/ShellBanners.xaml`, `ShellTopBar.xaml`, `OrderDeck.Overlay/**`, `OrderDeck.LicenseServer/**`, `Extension/**`, SQLite göçleri.
- **Sayılar:** en çok 4 Sayfa (birincil dahil); izleme 2 sn; yanıtsızlık 10 sn (canlıyken) / 45 sn (beklerken); "canlı yayın bulunamadı" 90 sn; "yayın kesildi" üst noktada 2 dakika; tarayıcı onayı 5 dakika; kota satırı hesap başına dakikada en çok 1; kapı özeti dakikada 1; Graph API v22.0.
- **PR-B dışarıda:** `IInstagramPollPacer`, poller'ın `pacer` parametresi, K1–K8, `InstagramCommentMerger`, tutma ve bariyer; hesaplar arası sıra varış sırasıdır; `HoldBudget` sıfırdır (ayrıntı: aşağıda "PR-A / PR-B imza sınırı").
- **Kapsam:** yalnız `OrderDeck.Chat`, `OrderDeck.Core`, `OrderDeck.App`, `OrderDeck.Tests` (+ bir belge); yeni NuGet paketi, csproj değişikliği, sunucu deploy'u, SQLite göçü ve yeni hosted service YOK; "birden çok bilgisayar" önlem görevi YOK (spec §11.1 soru 3).
- **PUBLIC depo:** testte ve doc yorumunda token, kimlik, kullanıcı adı, Sayfa adı literal yazılmaz, üretilir (`$"{önek}-{Guid.NewGuid():N}"` ya da kit'in `NewId()` / `NewToken()` yardımcıları); değişken adı nötr; `EncryptedFacebookTokenStoreTests.cs:36-41` deseni kopyalanmaz; belgeye lisans, müşteri ya da hesap kimliği girmez.
- **Günlük (§8.8):** Information ve üstünde izleyici kullanıcı adı ve yorum metni yok; token, `access_token` içeren URL ve `paging.next` hiçbir log ya da istisna mesajında yok; yeni satırların önekleri `[InstagramChatHostedService]`, `[InstagramMergeGate]`, `Facebook OAuth …`.
- **Arayüz:** metinler Türkçe ve spec §8.6'dan noktalamasıyla BİREBİR; testler metni literal doğrular (üretim sabitine başvurmaz); açılır pencere yok (çekmece + satır içi metin); renkler `OD.Brush.Success` / `OD.Brush.Amber` / `OD.Brush.TextMute`, ölçüler tema belirteçleriyle.
- **Adlar:** kod adları İngilizce ve spec'tekiyle birebir; yorumlar ve yeni test sınıflarının test adları Türkçe (snake_case); İngilizce adlı mevcut sınıfa eklenen test o sınıfın diline uyar.
- **Zaman (G07, G09–G12: sağlık izleyicisi, kapı, hosted service, hesap döngüsü):** üretim kodu saati `TimeProvider`'dan okur ve `Task.Delay(süre, time, ct)` ile bekler; testlerin `ManualTime`'ı yalnız saati ilerletir, zamanlayıcı sürmez: kalan süre saat okunarak değil adım toplanarak ölçülür (G11), `ManualTime` verilen bekleme GERÇEK süre bekler. OAuth servisi (G02–G04), poller (G06) ve arayüz (G14, G15) kendi görevlerinde yazılan saat kaynağını kullanır; G08 saat okumaz (`now` parametredir).
- **Testte bekleme:** gerçek süre bekleyen test koşulu yoklar, üst sınır `TestPatience.Upper` (30 sn); "yeterince bekle" amaçlı sabit `Task.Delay` yok.
- **Her görev sonunda:** çözüm 0 uyarıyla derlenir, `OrderDeck.Tests` yeşildir; spec §9.1 "değiştirilmeden geçmesi gerekenler" listesindeki sınıfların mevcut testleri DEĞİŞTİRİLMEZ (yalnız "ek" denenler yeni test alır).
- **Demet karşılaştırması:** `FacebookTokenBundle` kayıt eşitliğiyle değil alan alan karşılaştırılır (liste taşıyor).
- **Sürüm:** yeni WPF Velopack sürümü gerekir; PR DRAFT kalır, merge ve sürüm sahibindedir ve yayın penceresi dışında yapılır.

## Review Focus

Gözden geçirenin önce bakacağı beş yer; numaralar görev metinlerindeki "Review Focus n" atıflarıyla aynıdır.

1. **Onay beklenirken bağlantı değişir.** `ReauthorizeAsync` tarayıcıdayken "Bağlantıyı Kaldır" ya da "Kaldır" basılır (kilit tarayıcı beklemesini kapsamaz). Beklenti: kayıt adımı güncel demeti kilit altında yeniden okur; kaldırılmış bağlantı dirilmez, kaldırılan ek Sayfa geri gelmez, beklerken eklenen korunur. Sahip: G04 (arayüz ucu G18). Testler: `Onay_beklenirken_baglanti_kaldirilirsa_geri_cagri_gelince_baglanti_dirilmez`, `Tokenlar_turetilirken_baglanti_kaldirilirsa_kayit_adiminda_baglanti_dirilmez`, `Tokenlar_turetilirken_kaldirilan_ek_sayfa_geri_gelmez_eklenen_korunur` (G04); `Beklerken_baglanti_kaldirilirsa_guncelleme_basarisiz_olur_baglanti_dirilmez` (G18).
2. **DI kaydı eksik ya da iki ayrı izleyici örneği.** İsteğe bağlı kurucu parametresi sessizce null düşer; şerit, üst nokta ve kenar çubuğu boş kalır, başka hiçbir test düşmez. Beklenti: kabuk ve hosted service AYNI `InstagramHealthMonitor` örneğini alır. Sahip: G15. Test: `InstagramHealthDiTests.Izleyici_tekil_hosted_service_ayni_ornegi_tutar_kabuk_istege_bagli_parametreyle_alir`.
3. **Yanlış yazılmış XAML bağ yolu.** WPF hata vermez, alan boş görünür; örnekleme testi yakalamaz. Beklenti: satır etiketi, şerit, kenar çubuğu sayı sütunu, Ayarlar satırları, ekleme çekmecesi ve `[İptal]` düğmesi gerçek ViewModel tipleriyle çizilip görünen metin ve görünürlük doğrulanır. Sahip ve testler: G14 `ChatPanelLayoutTests.Etiketli_satirda_etiket_ve_ipucu_gorunur_etiketsiz_satir_bugunku_gibi`; G15 `ChatPanelLayoutTests.Saglik_seridi_gercek_satirlari_cizer_gizliyken_yer_kaplamaz`, `MainShellInstagramHealthTests.Kenar_cubugunda_sayi_sutunu_ShowViewerCount_ile_gorunur`; G16 `Kart_satirlari_yalniz_iki_Sayfada_cizer_Kaldir_yalniz_ek_satirda`; G17 `Cekmece_secenekleri_cizer_hesapsiz_Sayfa_pasif_secim_yokken_Ekle_kapatmaz`, `Kartta_Instagram_hesabi_ekle_dugmesi_bagli_degilken_pasif`; G18 `Kart_beklerken_Iptali_gosterir_uc_dugmeyi_kapatir_bitince_Iptal_gizlenir` (son dördü `SettingsViewModel_InstagramAccountsTests` içinde).
4. **Yerel saat çevrimi.** `yayın bitti (23:41)`, `21:20'de dener` ve etiket ipucundaki saat UTC'den çevrilmezse Türkiye'de 3 saat kayar; CI UTC'de koştuğu için test yine geçer. Beklenti: saat üreten saf işlevler UTC olmayan sabit ofsetle sınanır; aşağıdaki iki test +05:30 kullanır (CI'nın UTC'si de geliştirme makinesinin +03:00'ü de değil: hata iki ortamda da düşer). Sahip ve testler: G08 `InstagramHealthPresenterTests.Saat_metni_now_ofsetine_cevrilir` (sınıfın öbür testleri +03:00 ofsetli `Now` ile koşar); G14 `ChatMessageViewModelTests.Ipucu_Meta_saatini_verilen_ofsette_yazar`.
5. **Ortak önekli uzun hesap adları** (`@marka` / `@marka2` türü; Instagram adı 30 karaktere kadar). Sondan kırpma ayırt edici eki gizler, iki etiket aynı görünür. Beklenti: "@" dahil 16 karaktere kadar etiket kırpılmadan sığar (genişlik belirteci buna göre seçilir); daha uzununda ipucu tam adı verir. Sahip: G14. Test: `ChatPanelLayoutTests.Son_karakteri_farkli_16_karakterlik_iki_etiket_kirpilmaz_uzun_etikette_ipucu_tam_adi_verir`.

---

## Yürütme notları

- **Dal ve worktree:** `C:/Users/burak/source/repos/LiveDeck/.claude/worktrees/ig-kesif`, dal `feat/instagram-coklu-hesap`; ürün kodu `origin/master` `7960eba3` ile aynıdır. Bütün komutlar bu kökten koşulur; ana checkout'a `cd` edilmez; `git stash` kullanılmaz.
- **Satır numaraları:** görevlerdeki "bugünkü satırlar" `7960eba3` kesitine göredir. Önceki görevler satırları kaydırır: verilen çapa adla (yöntem, alan, XAML öğesi) bul.
- **Sıra:** G01 → G19, sırayla. Motor G01–G13, arayüz G14–G18, kapanış G19. Tek PR varsayılır; inceleme için bölünürse kesim G13 | G14'tür (motor tek başına birleşirse çok hesap yolu uykuda kalır).
- **Tek sınıf (git-bash):** `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~<Sınıf>"`; birden çok sınıf `"FullyQualifiedName~A|FullyQualifiedName~B"`. `~` sınıf adına eşleşir; `partial` dosyalar tek süzgeçle gelir. PowerShell'de önce `$env:DOTNET_CLI_UI_LANGUAGE='en'`.
- **Tüm testler:** `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q`. **WPF derleme:** `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`.
- **CI eşdeğeri:** `dotnet build OrderDeck.sln -c Release` + `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj -c Release --no-build -- xUnit.ParallelizeTestCollections=false` (CI'da koleksiyonlar seri koşar, `--blame-hang-timeout 3m`).
- **Commit:** `git add <dosyalar>` + `git commit -F - <<'MSG' … MSG`; başlık `tür(kapsam): özet`, Türkçe emir kipi; gövde nedeni söyler; son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`. Her görev kendi commit'iyle biter.
- **Push ve PR:** yalnız sahibin (Burak) açık onayıyla. PR DRAFT açılır ve DRAFT kalır; merge ve sürüm sahibindedir ve yayın penceresi (Paz/Pzt/Çar/Per 20:00–01:00 TR) dışında yapılır.
- **Kaynak düzenleme:** yalnız Edit / Write araçlarıyla (PowerShell 5.1 UTF-8'i bozar). Prod, SSH ve sır dosyalarına dokunulmaz.
- **Test çerçevesi:** xunit + FluentAssertions + Moq; test projesi `net10.0-windows` + `UseWPF`; global using `Xunit`, `System.Net.Http`, `System.IO`; `OrderDeck.Chat` ve `OrderDeck.App` iç (`internal`) üyelerini teste açar.
- **Uyarı = hata:** `OrderDeck.Chat` / `Core` / `App` `TreatWarningsAsErrors` ile derlenir. Atanıp okunmayan özel alan ve kullanılmayan yerel derlemeyi kırar; bir görevin eklediği üyeyi kullanan kod aynı görevde olmalıdır.
- **CA1416:** `OrderDeck.Chat` `net10.0` hedefler; DPAPI'ye dayanan sınıflar `[SupportedOSPlatform("windows")]` taşır (`EncryptedFacebookTokenStore`, `FacebookOAuthService`, `InstagramChatHostedService`). Onları çağıran yeni Chat sınıfı da taşımazsa derleme kırılır.
- **Sahte HTTP:** OAuth servisi, hosted service ve Ayarlar testlerinde (G02–G04, G10–G13, G16–G18) Graph'a giden her istek G02'nin kit'indeki `FakeGraph`'tan geçer (eşzamanlı güvenli); G05 ve G06 kit'e bağımlı değildir, kendi sınıfa özel işleyicilerini kullanır. Ortak `FakeHttpMessageHandler`'ın `Requests` listesi kilitsizdir; eşzamanlı poller testinde kullanılmaz.
- **Saat ve bekleme:** `Microsoft.Extensions.TimeProvider.Testing` paketi depoda YOK, eklenmez; G07'nin `ManualTime`'ı kullanılır. Poller'da süre dikişi yoktur: yorum akıtan testler poller'ın gerçek 1 sn'lik aralığıyla koşar.
- **Logger:** `NullLogger<T>.Instance`; içerik denetimi `CapturingLogger<T>` (`Entries`, `Warnings`, `Full`); `ILoggerFactory` gereken yerde G10'un `CapturingLoggerFactory`'si.
- **ViewModel testleri:** xunit iş parçacığında, STA'sız kurulur. Kabuk `MainShellTestHarness.Build(...)` ile ve HEP `using` ile kurulur (kurucu üç `DispatcherTimer` başlatır). Çekmece sahtesi içerik fabrikasını ÇAĞIRMAZ; seçim ViewModel'in tuttuğu modelden okunur. `Application.Current.Dispatcher`'a post eden kod testte asılır.
- **XAML testleri:** derleme zamanı bağ denetimi yoktur. WPF'e dokunan HER test `ThemeTestHost.RunOnSta` içinden geçer (`Pump()` + `Measure` / `Arrange`). `UnstyledControlGuardTests` stilsiz `Button` / `ListBox` / `RadioButton`'ı yakalar. `MainShellViewModel`'in TEK public kurucusu kalır; `SettingsViewModel` `IDialogService` alamaz.
- **Kod bloklarının gösterimi:** gövdesi yazılmamış (`;` ile biten) yardımcılar İMZADIR, gövdeyi uygulayıcı yazar; test gövdeleri ise yazıldığı gibi uygulanır. G01–G04'te test gövdeleri süslü parantezsiz, girintiyle gösterilir (her blokta "Gösterim" notu var).
- **Diğer:** PR-A yeni hosted service eklemez; kayıtlı her `IHostedService` `OrderDeck.App/Startup/WpfStartupEnvironment.cs:189-197`'de başlar.

## PR-A / PR-B imza sınırı

Spec'in kendi işaretlerinden; her görev aynen uygular.

- `IInstagramPollPacer` arayüzü ve poller kurucusunun `pacer` parametresi: PR-A'da YOK (§8.3.1 kod bloğunda "// PR-B"; §10 PR-B "Poller tempo kancası"). PR-A kurucusu `observer` ve `watermark` ile biter.
- Poller kurucusunun `InstagramCommentWatermark? watermark` parametresi: PR-A'da VAR (§10 adım 4 "filigran parametresi").
- `InstagramMergeGate.Lane`: PR-A'da `IChatBus, IInstagramPollObserver` uygular; `IInstagramPollPacer` YOK (§8.3.5 "tempo arayüzü PR-B").
- `InstagramMergeGate.HoldBudget`: PR-A'da VAR ve değeri `TimeSpan.Zero` (§8.3.5 "PR-B; PR-A'da sıfır"); 400 ms'yi PR-B yazar (§10 "`HoldBudget` varsayılanı pilot rakamına göre").
- Kapı kurucusunun `TimeSpan? holdBudget = null` parametresi: imzada VAR (§8.3.5 bloğunda PR-B işareti taşımıyor; imza spec'le birebir tutulur), PR-A'da hiçbir davranışı değiştirmez; tutma, tur ve üyelik kodu YOK (§8.3.5 "PR-A'da tutma yoktur").
- K1–K8, `InstagramCommentMerger`, kapı özetindeki bariyer aşımı / geç kalan tur / yavaş kulvar sayaçları (§8.8): PR-A'da YOK. TK8, TK18 ve TK21 yalnız PR-B'yi bağlar.

## Plan kararları

Spec'in açık bıraktığı ya da inşaatçıya bıraktığı (§11.3) yerlerde bu planın seçtikleri; sahip itiraz ederse yalnız anılan görev değişir.

- Ayarlar'ın çok Sayfalı başlığındaki sayı (`N Instagram hesabı bağlı`) çözülen Instagram hesabı (`@ad` satırı) sayısıdır, bağlı Sayfa sayısı değil (G16).
- Onay beklenirken bağlantı kaldırılırsa `ReauthorizeAsync` hiçbir şey yazmaz ve `InvalidOperationException` fırlatır; arayüzde "Güncelleme başarısız" metnine düşer, bağlantı dirilmez (G04, G18).
- "Facebook izinlerini güncelle" bekleme metni ve `Sayfalar okunuyor...` kartın durum satırına (`InstagramAccountStatus`) yazılır; işlem bitince durum satırı hâlâ o metinse önceki metin geri konur (G17, G18).
- `Facebook izinleri güncellendi: N Sayfa yenilendi.` metnindeki N, kaydedilen demete yeni türetilmiş token'ı yazılan Sayfa sayısıdır (birincil dahil; türetilip de kaydedilmeyen Sayfa sayılmaz; G04).
- "Instagram hesabı ekle"de listeleme sırasındaki her hata (`NeedsReauthorize` dışında) "Sayfalar okunamadı" metnidir; ekleme sırasında 5xx, kota ve ağ dışındaki her hata "yetki alınamadı" metnidir (G17).
- "Kaldır" yazımı düşerse metin `Hesap kaldırılamadı, yeniden dene.` olur, satır kalır (spec metin vermiyor; G16).
- İzin güncellemede `{kısa sebep}` istisna mesajıdır (sondaki nokta kırpılır); Graph hatasında ham gövde yerine `Facebook hatası (kod N)` yazılır; Facebook ekranındaki "İptal"de mesaj bugünkü İngilizce metindir (`Facebook OAuth error: access_denied — …`; eşleme spec gereği değişmez) (G18).
- İzin güncellemede "iptal edildi" metni yalnız `[İptal]` ve onay reddinde yazılır; belirteç iptal edilmemişken gelen `TaskCanceledException` (HttpClient zaman aşımı) kullanıcı iptali değildir: arayüzde "Güncelleme başarısız"a düşer (G18), token türetmede yalnız o Sayfayı atlar (G04).
- Birden çok eksik Sayfada onay sorusu adları virgülle sayar ve `bu Sayfaların` der; sonuç cümlesi adları aynı biçimde sayar (`"A", "B" bu girişte yoktu; yorumları kesilebilir.`); onay çekmecesinin başlığı `Facebook izinlerini güncelle`dir (spec yalnız tekili verir; G18).
- Ayarlar'da hesap etiketi `"@" + (kullanıcı adı ?? Instagram kimliği)` kuralıyla yazılır (bugünkü durum metninin kuralı; G16).
- Sohbet satırı etiketinin genişlik sınırı 112 DIP'tir (spec "yaklaşık 110"; 16 karakterlik etiket kırpılmadan sığsın diye; G14).
- Şeritte `{Sayfa adı}: …` diye yazılan iki satırda kullanıcı adı bilinse de Sayfa adı yazılır; `yanıt yok (N sn)` saniyesi sessizlikten (yeniden deneme saati geçmiş durumlarda o saatten) ölçülür; eşiklerde aşım `>`'dir; yeniden deneme saati bilinmiyorsa metnin saat parçası yazılmaz (G08).
- Üst nokta ipucu, mevcut sonuç zaten sarıysa `Instagram cümlesi + satır sonu + mevcut ipucu` olarak birleşir; şeritteki uyarı işareti simge değil renkli noktadır (G15).
- Kota günlük satırı geçici hatalı turda yazılmaz; başlık yoksa `X-Business-Use-Case-Usage header absent`, okunamıyorsa `… header unreadable` yazar (G10). Kapı özeti satırının sayıları pencere başınadır: her satır son bir dakikayı anlatır (G09).
- Kalıcı hatanın geçiş satırı (`… -> Faulted`) tur olayında değil, yeniden deneme saati yazılınca (poller bittikten sonraki `Report`) ve `retry at` ekiyle yazılır (G10).
- Çözümleyici önbelleği tek yerde boşaltılır: tur başında okunan token, o Sayfanın çözümleyicisine en son verilen token'dan farklıysa (PageId → token sözlüğü hosted service'te, çözümleyicilerle aynı ömürde). Token beklemeye girilmeyen yollarda değişse de hesap bir kez yeniden çözülür (G11).
- Oturum içinde "ekleme" ile "tekten çoka geçiş" ayrımı kapının kurulu olup olmamasına göredir: kapı kuruluyken gelen ekleme çalışan poller'ları yeniden kurmaz (G12).
- Bağlı değilken `ListAddablePagesAsync` / `AddExtraPageAsync` / `ReauthorizeAsync` `InvalidOperationException` fırlatır (arayüzde düğmeler zaten pasiftir; G02, G04).
- Atlanan (aynı Instagram hesabına çözülen) Sayfanın girdisi 5 dakikada bir `Starting`'e dönüp yanıp sönmez: `SetAccount` sahiplik denetiminden sonra çağrılır (G10).
- Sürüm notu metni pilot belgesine ve PR gövdesine yazılır (depoda sürüm notu dosyası yok); sunucu bot bayrağını kapatmanın arayüz yolu bu depoda bulunamadığı için pilot belgesi "kapatma sorgusu ayrıca istenir" der (G19).

## Dosya haritası

| Yol | İşlem | Sorumluluk | Görev |
|---|---|---|---|
| `OrderDeck.Chat/Facebook/FacebookTokenBundle.cs` | Modify | `FacebookLinkedPage`, `ExtraPages`, iki `ToString` | G01 |
| `OrderDeck.Chat/Facebook/EncryptedFacebookTokenStore.cs` | Modify | atomik `SaveAsync` | G01 |
| `OrderDeck.Chat/Facebook/FacebookOAuthService.cs` | Modify | bağlı Sayfalar, listeleme / ekleme / kaldırma, kilit (G02); tarayıcı beklemesi (G03); yeniden onay (G04) | G02, G03, G04 |
| `OrderDeck.Chat/Facebook/FacebookGraphException.cs` | Create | `FacebookGraphException`, `FacebookPageLimitException` | G02 |
| `OrderDeck.Chat/Facebook/FacebookOAuthBrowser.cs` | Create | tarayıcı / dinleyici dikişi + üretim uygulaması | G03 |
| `OrderDeck.Chat/Ingestors/Instagram/InstagramAccountResolver.cs` | Modify | `ResolveDetailedAsync`, `InstagramResolveStatus` | G05 |
| `OrderDeck.Chat/Ingestors/Instagram/InstagramGraphError.cs` | Modify | `TryGetUsage`, `InstagramBucUsage` | G05 |
| `OrderDeck.Chat/Ingestors/Instagram/IInstagramPollObserver.cs` | Create | `IInstagramPollObserver`, `InstagramPollKind` | G06 |
| `OrderDeck.Chat/Ingestors/Instagram/InstagramLiveCommentsPoller.cs` | Modify | gözlemci çağrıları, kota başlığı, filigran parametresi | G06 |
| `OrderDeck.Chat/Ingestors/Instagram/InstagramHealthMonitor.cs` | Create | durum enum'u, sağlık kaydı, izleyici | G07 |
| `OrderDeck.App/Services/Instagram/InstagramHealthPresenter.cs` | Create | saf görünüm kuralları, `InstagramHealthView`, `InstagramStatusSeverity` | G08 |
| `OrderDeck.App/ViewModels/InstagramAccountStatusViewModel.cs` | Create | şerit satırı kaydı | G08 |
| `OrderDeck.Core/Chat/ChatMessage.cs` | Modify | `SourceAccount` | G09 |
| `OrderDeck.Chat/Ingestors/Instagram/InstagramMergeGate.cs` | Create | kapı + `Lane` (etiket, ölçüm) | G09 |
| `OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs` | Modify | üst döngü, kurucular, `InstagramChatTimings` (G10); çözümleme token'ı sözlüğü (G11); izleme ve oturum içi değişiklik (G12) | G10, G11, G12 |
| `OrderDeck.Chat/Ingestors/Instagram/InstagramAccountLoop.cs` | Create | hesap döngüsü, `InstagramLoopServices` (G10); token'a bakan beklemeler ve önbellek boşaltma (G11); kulvara geçiş (G12) | G10, G11, G12 |
| `OrderDeck.App/ViewModels/ChatMessageViewModel.cs` | Modify | `AccountTag`, `AccountTooltip` | G14 |
| `OrderDeck.App/Themes/Metrics.xaml` | Modify | `OD.Layout.ChatAccountTagMaxWidth` | G14 |
| `OrderDeck.App/Views/Shell/ChatPanel.xaml` | Modify | satır etiketi (G14); sağlık şeridi satırı (G15) | G14, G15 |
| `OrderDeck.App/ViewModels/MainShellViewModel.InstagramHealth.cs` | Create | kabuğun sağlık üyeleri (`partial`; emsal `MainShellViewModel.SalesFeed.cs`) | G15 |
| `OrderDeck.App/ViewModels/MainShellViewModel.cs` | Modify | izleyici parametresi, nabız, üst nokta, kenar çubuğu | G15 |
| `OrderDeck.App/ViewModels/PlatformConnectionViewModel.cs` | Modify | `ShowViewerCount` | G15 |
| `OrderDeck.App/Views/Shell/ShellSidebar.xaml` | Modify | sayı sütunu görünürlük bağı | G15 |
| `OrderDeck.App/AppHost.cs` | Modify | izleyicinin tekil kaydı, `health:` | G15 |
| `OrderDeck.App/Services/Instagram/InstagramSettingsTexts.cs` | Create | §8.6.1 metinleri | G16 (G17, G18 ekler) |
| `OrderDeck.App/ViewModels/InstagramPageRow.cs` | Create | Ayarlar satırı kaydı | G16 |
| `OrderDeck.App/ViewModels/SettingsViewModel.cs` | Modify | liste / kaldır / tazeleme (G16); ekleme (G17); izin güncelleme + iptal (G18) | G16, G17, G18 |
| `OrderDeck.App/Views/Pages/SettingsPage.xaml` | Modify | Instagram kartı | G16, G17, G18 |
| `OrderDeck.App/ViewModels/InstagramAccountPickerViewModel.cs` | Create | ekleme çekmecesi modeli, `InstagramAccountOption` | G17 |
| `OrderDeck.App/Views/Drawers/InstagramAccountAddDrawer.xaml` + `.xaml.cs` | Create | ekleme çekmecesi | G17 |
| `docs/manual-smoke/2026-11-17-instagram-coklu-hesap-pilot.md` | Create | pilot el listesi, sürüm notu, teslim özeti | G19 |
| `OrderDeck.Tests/Chat/Facebook/FacebookTokenBundleCompatTests.cs` | Create | geriye uyum + atomik yazım | G01 |
| `OrderDeck.Tests/TestHelpers/FacebookOAuthTestKit.cs` | Create | ortak OAuth test düzeneği (G02); sahte tarayıcı (G03); token yenileme yardımcısı (G04) | G02, G03, G04 |
| `OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.cs`, `.BrowserWait.cs`, `.Reauthorize.cs` | Create | `partial` test sınıfı | G02, G03, G04 |
| `OrderDeck.Tests/Chat/Instagram/InstagramAccountResolverDetailedTests.cs` | Create | Found / NotLinked / Failed | G05 |
| `OrderDeck.Tests/Chat/Instagram/InstagramGraphErrorTests.cs` | Modify | `TryGetUsage` ekleri | G05 |
| `OrderDeck.Tests/Chat/Instagram/InstagramLiveCommentsPollerTests.cs` | Modify | gözlemci / kota / filigran ekleri | G06 |
| `OrderDeck.Tests/TestHelpers/ManualTime.cs` | Create | elle ilerletilen `TimeProvider` | G07 |
| `OrderDeck.Tests/Chat/Instagram/InstagramHealthMonitorTests.cs` | Create | geçişler | G07 |
| `OrderDeck.Tests/Services/Instagram/InstagramHealthPresenterTests.cs` | Create | görünüm kuralları | G08 |
| `OrderDeck.Tests/Chat/Instagram/InstagramMergeGateTests.cs` | Create | PR-A kapı testleri | G09 |
| `OrderDeck.Tests/TestHelpers/CapturingLoggerFactory.cs` | Create | günlük yakalayan `ILoggerFactory` | G10 |
| `OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.cs`, `.TokenChange.cs`, `.MidSession.cs` | Create | `partial` test sınıfı; ilk dosyada `InstagramLiveFeed` (G13 de kullanır) | G10, G11, G12 |
| `OrderDeck.Tests/Chat/Instagram/InstagramLoggingAuditTests.cs` | Create | günlük denetimi | G13 |
| `OrderDeck.Tests/ViewModels/ChatMessageViewModelTests.cs` | Create | etiket ve ipucu | G14 |
| `OrderDeck.Tests/App/ChatPanelLayoutTests.cs` | Create | gerçek bağ testleri: etiket (G14), şerit (G15) | G14, G15 |
| `OrderDeck.Tests/App/MainShellTestHarness.cs` | Modify | izleyici, izleyici sayacı ve bus girişleri | G15 |
| `OrderDeck.Tests/App/MainShellInstagramHealthTests.cs` | Create | kabuğun sağlık yüzeyleri | G15 |
| `OrderDeck.Tests/App/InstagramHealthDiTests.cs` | Create | DI kaydı | G15 |
| `OrderDeck.Tests/ViewModels/SettingsViewModelInstagramAccountsTests.cs` | Create | `SettingsViewModel_InstagramAccountsTests` sınıfı | G16, G17, G18 |
| `OrderDeck.Tests/App/MainShellViewCompositionTests.cs` | Modify | `InstagramAccountAddDrawer` örneklemesi | G17 |

## Görevler

### Görev G01: Token demeti: `ExtraPages`, `FacebookLinkedPage`, `ToString`, atomik yazım

**Spec:** §8.1 (biçim, TK1, "Eski blob'dan geçiş", "Eski sürüme dönüş", "Sağlamlık" / TK2); §9 girişi; §9.1 "Geriye uyum"; §10 adım 1; AH1, R6.
**Bağımlı:** yok.

**Files:**
- Modify: `OrderDeck.Chat/Facebook/FacebookTokenBundle.cs:25-30` (kayıt bildirimi)
- Modify: `OrderDeck.Chat/Facebook/EncryptedFacebookTokenStore.cs:36-44` (`SaveAsync`)
- Test: `OrderDeck.Tests/Chat/Facebook/FacebookTokenBundleCompatTests.cs` (yeni)

**Interfaces:**
- Consumes: yok.
- Produces:
  - `public sealed record FacebookLinkedPage(string PageId, string PageAccessToken, string PageName) { public override string ToString(); }` (`FacebookTokenBundle.cs` içinde, demetin yanında)
  - `public sealed record FacebookTokenBundle(string UserAccessToken, string PageId, string PageAccessToken, string PageName, DateTimeOffset? ExpiresAt, IReadOnlyList<FacebookLinkedPage>? ExtraPages = null) { public override string ToString(); }`
  - `EncryptedFacebookTokenStore.SaveAsync(FacebookTokenBundle bundle, CancellationToken ct = default)`: imza aynı, yazım atomik.

**Kurallar:**
- Birincil Sayfa yalnız üst düzey beş alanda durur; `ExtraPages` yalnız EK Sayfaları taşır, birincili içermez (listeyi G02 yönetir). Sürüm alanı, göç adımı, açılışta yeniden yazım YOK (TK1, AH1): eski blob olduğu gibi okunur, dosya bayt bayt aynı kalır.
- İki `ToString` yalnız `PageId` ve `PageName` yazar (demet ayrıca `ExpiresAt` ve ek Sayfa sayısını); hiçbir token yazılmaz.
- `SaveAsync`: şifreli baytlar aynı klasörde `facebook-token.bin.tmp` dosyasına yazılır, sonra `File.Move(geçici, hedef, overwrite: true)` ile yerine konur; yazım ya da taşıma fırlatırsa geçici dosya silinir (best effort) ve istisna yükselir. Emsal: `OrderDeck.App/Services/CatalogPhotoCache.cs:87-99`.
- Depo sınıfında başka değişiklik yok: `LoadAsync` (`:46-72`), `JsonOpts` (`:23-26`), dosya adı ve DPAPI kapsamı aynen. Beş argümanlı kurucu çağrıları (`FacebookOAuthService.cs:217-222`, `EncryptedFacebookTokenStoreTests`) değişmeden derlenir; `EncryptedFacebookTokenStoreTests` DEĞİŞTİRİLMEZ.
- Depo PUBLIC: testte ve belge yorumunda token, kimlik, Sayfa adı literal yazılmaz, üretilir (`EncryptedFacebookTokenStoreTests.cs:36-41` deseni kopyalanmaz). Demet kayıt eşitliğiyle değil alan alan karşılaştırılır (liste taşıyor).

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// OrderDeck.Tests/Chat/Facebook/FacebookTokenBundleCompatTests.cs — namespace OrderDeck.Tests.Chat.Facebook; public sealed class FacebookTokenBundleCompatTests : IDisposable
// Gösterim: her [Fact] / [Theory] yönteminin altındaki girintili satırlar o yöntemin gövdesidir (süslü parantezler yazılmadı).
// 0.9.12'nin demeti ve serileştirici seçenekleri (EncryptedFacebookTokenStore.cs:23-26). DONDURULMUŞTUR: üretim tipine bağlanmaz.
private sealed record LegacyBundle(string UserAccessToken, string PageId, string PageAccessToken, string PageName, DateTimeOffset? ExpiresAt);
private static readonly JsonSerializerOptions LegacyOpts = new() { WriteIndented = false };
private readonly string _folder;                      // Path.GetTempPath() altında "orderdeck-fb-compat-<guid>"; Dispose alt klasörleriyle siler
private readonly EncryptedFacebookTokenStore _store;  // new(_folder)
private string BlobPath => Path.Combine(_folder, "facebook-token.bin");
private IEnumerable<string?> FileNames() => Directory.GetFiles(_folder).Select(Path.GetFileName);
private static LegacyBundle NewLegacy();                                               // beş değer $"{onek}-{Guid.NewGuid():N}" ile; ExpiresAt = UtcNow + 60 gün
private static FacebookTokenBundle WithExtras(LegacyBundle primary, int extraPages);   // aynı beş alan + N üretilmiş ek (0 → ExtraPages null)
private static byte[] Protect(string json);      // ProtectedData.Protect(UTF-8 baytları, null, DataProtectionScope.CurrentUser)
private static string Unprotect(byte[] cipher);  // tersi
private static void AssertPrimary(FacebookTokenBundle actual, LegacyBundle expected);  // beş alan tek tek; ExpiresAt unix saniyesiyle

[Fact] public void Eski_bicimli_JSON_yeni_kayda_okunur()
    var old = NewLegacy();
    var json = JsonSerializer.Serialize(old, LegacyOpts);
    var bundle = JsonSerializer.Deserialize<FacebookTokenBundle>(json, LegacyOpts)!;
    json.Should().NotContain("ExtraPages");
    AssertPrimary(bundle, old);
    bundle.ExtraPages.Should().BeNull();
[Theory] [InlineData(0)] [InlineData(2)]             // eski sürüme dönüş sözleşmesi; 0: tek Sayfalı demet de "ExtraPages":null anahtarını yazar, eski sürüm onu da atlar
public async Task Yeni_bicimli_JSON_eski_surumun_bes_alanli_kaydina_okunur(int extraPages)
    var primary = NewLegacy();
    await _store.SaveAsync(WithExtras(primary, extraPages));
    var json = Unprotect(File.ReadAllBytes(BlobPath));                                 // gerçek SaveAsync çıktısı
    var old = JsonSerializer.Deserialize<LegacyBundle>(json, LegacyOpts)!;             // AYNI seçeneklerle
    json.Should().Contain("ExtraPages");
    (old with { ExpiresAt = null }).Should().Be(primary with { ExpiresAt = null });    // dört dizge alanı
    old.ExpiresAt!.Value.ToUnixTimeSeconds().Should().Be(primary.ExpiresAt!.Value.ToUnixTimeSeconds());
[Fact] public async Task DPAPI_ile_yazilmis_eski_blob_saglam_okunur_silinmez_baytlari_degismez()
    var old = NewLegacy();
    var bytes = Protect(JsonSerializer.Serialize(old, LegacyOpts));
    File.WriteAllBytes(BlobPath, bytes);
    var bundle = (await _store.LoadAsync())!;
    AssertPrimary(bundle, old);
    bundle.ExtraPages.Should().BeNull();
    File.ReadAllBytes(BlobPath).Should().Equal(bytes);
[Fact] public async Task Ekli_demet_kaydet_yukle_turunda_sira_ve_icerik_korunur()
    var primary = NewLegacy();
    var saved = WithExtras(primary, 3);
    await _store.SaveAsync(saved);
    var loaded = (await _store.LoadAsync())!;
    AssertPrimary(loaded, primary);
    loaded.ExtraPages!.Select(p => (p.PageId, p.PageAccessToken, p.PageName))
        .Should().Equal(saved.ExtraPages!.Select(p => (p.PageId, p.PageAccessToken, p.PageName)));
[Fact] public void ToString_token_basmaz()
    var bundle = WithExtras(NewLegacy(), 1);
    var extra = bundle.ExtraPages![0];
    extra.ToString().Should().Contain(extra.PageId).And.Contain(extra.PageName).And.NotContain(extra.PageAccessToken);
    bundle.ToString().Should().Contain(bundle.PageId).And.Contain(bundle.PageName)
        .And.NotContain(bundle.UserAccessToken).And.NotContain(bundle.PageAccessToken).And.NotContain(extra.PageAccessToken);
[Fact] public async Task Atomik_yazim_gecici_dosya_birakmaz_ve_eskisinin_yerini_alir()
    await _store.SaveAsync(WithExtras(NewLegacy(), 0));
    var next = NewLegacy();
    await _store.SaveAsync(WithExtras(next, 1));
    FileNames().Should().Equal("facebook-token.bin");
    AssertPrimary((await _store.LoadAsync())!, next);
[Fact] public async Task Yazim_gecici_dosyadan_gecer_gecici_yol_yazilamazsa_eski_dosya_ayni_kalir()   // atomik yazımın (Adım 5) düşen testi
    await _store.SaveAsync(WithExtras(NewLegacy(), 0));
    var before = File.ReadAllBytes(BlobPath);
    Directory.CreateDirectory(BlobPath + ".tmp");                                      // geçici dosyanın yolunu bir klasör tutar: yeni kod yazamaz; eski kod hedefe doğrudan yazıp geçerdi
    await FluentActions.Awaiting(() => _store.SaveAsync(WithExtras(NewLegacy(), 1))).Should().ThrowAsync<UnauthorizedAccessException>();
    File.ReadAllBytes(BlobPath).Should().Equal(before);
[Fact] public async Task Yazim_duserse_istisna_cikar_eski_dosya_ayni_kalir_gecici_dosya_kalmaz()
    await _store.SaveAsync(WithExtras(NewLegacy(), 0));
    var before = File.ReadAllBytes(BlobPath);
    File.SetAttributes(BlobPath, FileAttributes.ReadOnly);                             // Windows salt okunur hedefin üstüne taşımayı reddeder
    try
    {
        await FluentActions.Awaiting(() => _store.SaveAsync(WithExtras(NewLegacy(), 1))).Should().ThrowAsync<UnauthorizedAccessException>();
        File.ReadAllBytes(BlobPath).Should().Equal(before);
        FileNames().Should().Equal("facebook-token.bin");
    }
    finally { File.SetAttributes(BlobPath, FileAttributes.Normal); }                   // yoksa Dispose klasörü silemez
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FacebookTokenBundleCompatTests"`
Beklenen: FAIL (derleme hatası: `FacebookLinkedPage` türü ve `ExtraPages` üyesi yok)

- [ ] **Adım 3: `FacebookLinkedPage`, `ExtraPages` ve iki `ToString`'i uygula (`OrderDeck.Chat/Facebook/FacebookTokenBundle.cs`)**

`ExtraPages` konumsal kaydın SON parametresidir ve varsayılanı null'dır; `<param name="ExtraPages">` belge satırı "yalnız EK Sayfalar, birincil burada tekrarlanmaz" der.

- [ ] **Adım 4: Atomik yazım testinin düştüğünü gör**

Koş: Adım 2'deki komut
Beklenen: FAIL (yalnız `Yazim_gecici_dosyadan_gecer_…`: `UnauthorizedAccessException` beklenirken istisna fırlamadı; bugünkü `File.WriteAllBytes` hedefe doğrudan yazıyor). Öbür sekiz test geçer.

- [ ] **Adım 5: `SaveAsync`'i atomik yap (`OrderDeck.Chat/Facebook/EncryptedFacebookTokenStore.cs`)**

Geçici dosyanın yolu `_path + ".tmp"`; serileştirme ve şifreleme satırları (`:38-41`) aynen kalır, yalnız `File.WriteAllBytes(_path, cipher)` (`:42`) Kurallar'daki yaz, taşı, hata olursa temizle üçlüsüyle değişir.

- [ ] **Adım 6: Testlerin geçtiğini gör**

Koş: Adım 2'deki komut
Beklenen: PASS (9 test). Not: `Atomik_yazim_…` ve `Yazim_duserse_…` eski `File.WriteAllBytes` ile de geçiyordu (Adım 4'te kırmızı görmediler); ikisi Adım 5'in bekçisidir (geçici dosya artığı, yerine koyma, hata yolunda temizlik).

- [ ] **Adım 7: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FacebookTokenBundleCompatTests|FullyQualifiedName~EncryptedFacebookTokenStoreTests"`
Beklenen: PASS (14 test; `EncryptedFacebookTokenStoreTests` değiştirilmeden). `OrderDeck.Chat` 0 uyarıyla derlenir (uyarı = hata); test projesinde yeni uyarı yok.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.Chat/Facebook/FacebookTokenBundle.cs OrderDeck.Chat/Facebook/EncryptedFacebookTokenStore.cs OrderDeck.Tests/Chat/Facebook/FacebookTokenBundleCompatTests.cs
git commit -F - <<'MSG'
feat(facebook-oauth): token demetine ek Sayfa listesini ekle, depoyu atomik yaz

İkinci Instagram hesabı ayrı bir Facebook Sayfasından okunacak. Birincil Sayfa bugünkü
beş alanda kalır, ekler isteğe bağlı ExtraPages'te durur: eski blob aynen okunur, eski
sürüm yeni blob'u okur (ikisi de testle kilitli). Ekle ve kaldır yazımı sıklaştıracağı
için depo geçici dosya + taşıma ile yazar; token taşıyan kayıtlar ToString'te token basmaz.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G02: OAuth: bağlı Sayfalar, tarayıcısız listeleme / ekleme / kaldırma, kilit, hata metni

**Spec:** §8.2 ("Değişmeyen üyeler", TK3, "Eklenen üyeler", "Tarayıcısız listeleme", "Tarayıcısız ekleme", "İlk bağlantı ve tümünü kaldırma", "Eşzamanlılık", "Hata metni düzeltmesi", TK13); §8.1 "Sağlamlık" son madde; §8.8; §9.1 `FacebookOAuthServiceMultiPageTests` (`ReauthorizeAsync` ve tarayıcı beklemesi maddeleri hariç); §11.3; AH5, TK5.
**Bağımlı:** G01.

**Files:**
- Modify: `OrderDeck.Chat/Facebook/FacebookOAuthService.cs` (alanlar `:66-75`; `ConnectAsync` kaydetme adımı `:212-233`; `DisconnectAsync` `:239-245`; `GetJsonAsync` `:373-387`; tel sınıfları `:398-412`; yeni kayıtlar `:417`'deki `FacebookPageCandidate`'in yanına)
- Create: `OrderDeck.Chat/Facebook/FacebookGraphException.cs`
- Test: `OrderDeck.Tests/TestHelpers/FacebookOAuthTestKit.cs` (yeni), `OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.cs` (yeni, `partial`)

**Interfaces:**
- Consumes: G01 `FacebookLinkedPage(string PageId, string PageAccessToken, string PageName)`, `FacebookTokenBundle(string UserAccessToken, string PageId, string PageAccessToken, string PageName, DateTimeOffset? ExpiresAt, IReadOnlyList<FacebookLinkedPage>? ExtraPages = null)`; mevcut `EncryptedFacebookTokenStore.SaveAsync / LoadAsync / ClearAsync`, private `GetPageAccessTokenAsync(string pageId, string userAccessToken, CancellationToken ct)` (`:359-371`), `IFacebookOAuthBroker`; test: `CapturingLogger<T>` (`Entries` → `Level`, `Message`, `Full`), `TestPatience.Upper`.
- Produces (servis üyeleri; kayıtlar ve istisnalar `OrderDeck.Chat.Facebook` ad alanında üst düzey; sonra test kiti):

```csharp
public const int MaxInstagramPages = 4;                                      // birincil dahil
public Task<IReadOnlyList<FacebookLinkedPage>> GetConnectedPagesAsync(CancellationToken ct = default);
public Task<FacebookPageListing> ListAddablePagesAsync(CancellationToken ct = default);
public Task<FacebookLinkedPage> AddExtraPageAsync(string pageId, string pageName, CancellationToken ct = default);
public Task<bool> RemoveExtraPageAsync(string pageId, CancellationToken ct = default);
private Task<IReadOnlyList<FacebookAddablePage>> ListAccountsPagedAsync(string userAccessToken, CancellationToken ct);   // G04 de kullanır
private readonly SemaphoreSlim _mutate = new(1, 1);                          // G03 ve G04 de kullanır
public sealed record FacebookAddablePage(string PageId, string PageName, string? IgUserId, string? IgUsername, bool AlreadyConnected);
public sealed record FacebookPageListing(IReadOnlyList<FacebookAddablePage> Pages, bool NeedsReauthorize);
public sealed class FacebookGraphException : HttpRequestException            // Code / Subcode = Graph error.code / error.error_subcode; HTTP durumu taban sınıfın StatusCode'unda
{ public FacebookGraphException(string message, HttpStatusCode statusCode, int? code, int? subcode); public int? Code { get; } public int? Subcode { get; } }
public sealed class FacebookPageLimitException : InvalidOperationException { public FacebookPageLimitException(int limit); }
// Test kiti (ad alanı OrderDeck.Tests.TestHelpers). G03, G04, G10–G13 ve G15–G18 de kullanır; imzalar BAĞLAYICIDIR.
internal sealed class FacebookOAuthTestKit : IDisposable    // parametresiz kurucu (new()); geçici klasörde gerçek depo; Dispose klasörü siler
{
    public EncryptedFacebookTokenStore Store { get; }   public string TokenFilePath { get; }   // <klasör>/facebook-token.bin
    public AppSettings Settings { get; }   public FakeGraph Graph { get; }   public FakeFacebookOAuthBroker Broker { get; }
    public IHttpClientFactory HttpFactory { get; }          // her ad için Graph'a bağlı HttpClient (disposeHandler: false)
    public FacebookOAuthService CreateService(ILogger<FacebookOAuthService>? log = null);   // aynı depo üstünde yeni servis örneği
    public Task<IReadOnlyList<SeededPage>> SeedAsync(int extraPages = 0);                   // birincil önce
    public static string NewId();   public static string NewToken();   // $"id-{Guid.NewGuid():N}", $"t-{Guid.NewGuid():N}"
}
internal sealed record SeededPage(FacebookLinkedPage Page, string? IgUserId, string? IgUsername);
internal sealed class FakeFacebookOAuthBroker : IFacebookOAuthBroker { public int ConfigCalls { get; } public int ExchangeCalls { get; } public string? LastIssuedToken { get; } }
internal sealed class FakeGraph : HttpMessageHandler       // eşzamanlı güvenli
{
    public void AddPage(string pageId, string pageName, string? igUserId = null, string? igUsername = null);
    public void Map(Func<Uri, bool> match, Func<Uri, HttpResponseMessage> respond);
    public IReadOnlyList<Uri> Requests { get; }             // anlık kopya
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? usageHeader = null);
    public static HttpResponseMessage Error(int code, HttpStatusCode status = HttpStatusCode.BadRequest, int? subcode = null);
}
```

**Kurallar:**
- Değişmeyen üyeler (`IsConnectedAsync`, `GetConnectedPageNameAsync`, `GetPageCredentialsAsync`, `GetUserAccessTokenAsync`, private `ListPagesAsync`) imza ve davranışça aynı, hep birincili döner (AH5, TK3). `AppSettings.FacebookPageId` yalnız birincili yansıtır: ekleme ve kaldırma ona dokunmaz.
- `GetConnectedPagesAsync`: birincil önce, ekler saklanan sırayla; PageId'ye göre tekil (ilk görülen kalır); null girdi ile boş `PageId` ya da boş `PageAccessToken` taşıyan ek atlanır; bağlı değilse boş liste. Önce `IsConnectedAsync` çağrılır (bellekte demet yoksa token dosyasından yükler; ağ yok), sonra `_bundle` yerel değişkene BİR KEZ okunur: G12 bu üyeyi 2 saniyede bir çağıracak, araya `DisconnectAsync` girerse null görülüp boş liste dönülür (istisna çıkmaz).
- Kilit: `AddExtraPageAsync` ve `RemoveExtraPageAsync` gövdelerinin tamamı, `ConnectAsync`'in kaydetme adımı (`SaveAsync` + `_bundle` + `FacebookPageId`) ve `DisconnectAsync` `_mutate` altındadır. Güncel demet kilit ALTINDA okunur (`IsConnectedAsync` + `_bundle`). Önce diske yazılır, sonra `_bundle` TEK atamayla değişir (yazım düşerse bellek değişmez). Okuyan üyeler kilitsizdir.
- `AddExtraPageAsync`: bağlı değilse `InvalidOperationException("Facebook bağlı değil.")`; Sayfa birincilse ya da zaten ekliyse (sınır dolu olsa da) Graph'a gitmeden ve yazmadan mevcut girdiyi döner; değilse `GetConnectedPagesAsync` sayısı `MaxInstagramPages`'e ulaştıysa Graph'a gitmeden `FacebookPageLimitException`; token SAKLI kullanıcı token'ı + mevcut `GetPageAccessTokenAsync` ile türetilir (tarayıcı ve sunucu takası YOK, TK5); yeni girdi `ExtraPages`'in SONUNA eklenir. `RemoveExtraPageAsync`: bağlı değilken, birincil ya da bilinmeyen kimlikte yazmadan false.
- `ListAccountsPagedAsync`: ilk istek `GET {GraphBase}/me/accounts?fields=id,name,instagram_business_account{id,username}&limit=100&access_token=…` (alan listesi `Uri.EscapeDataString` ile; emsal `InstagramAccountResolver.cs:50-52`); yanıttaki `paging.next` adresi AYNEN izlenir, en çok 10 yanıt sayfası; kimliği ya da adı boş satır atlanır; PageId'ye göre tekil; `AlreadyConnected` hep false; hata eşlemez.
- `ListAddablePagesAsync`: bağlı değilse `InvalidOperationException`; `AlreadyConnected`'ı `GetConnectedPagesAsync` kümesine göre kurar; `FacebookGraphException.Code` 190 / 463 / 200 / 10 / 100 ise `NeedsReauthorize = true` + boş liste, diğer her hata (5xx, kota, ağ) yukarı çıkar. `InstagramGraphError.Classify` DEĞİŞMEZ (100'ü "yayın bitti" sayar; o anlam yalnız `live_media` içindir).
- `GetJsonAsync`: 2xx dışı her yanıtta `FacebookGraphException` (mesaj bugünkü `Graph API {durum} {neden}: {gövde}`; `Code` / `Subcode` gövdeden, okunamazsa null); boş gövde dalının mesajı URL'siz `Graph API returned empty body.` olur. Hiçbir istisna mesajı ve günlük satırı token, `access_token` içeren URL ya da `paging.next` taşımaz.
- Günlük (Information, mevcut `Facebook OAuth …` öneki): `Facebook OAuth extra Page added: {PageId} ({PageName})` ve `Facebook OAuth extra Page removed: {PageId} ({PageName})`. Yeni `await`'ler mevcut üslupla `ConfigureAwait(false)` taşır. Kit ve testlerde token, kimlik, Sayfa adı, kullanıcı adı literal yazılmaz (`Kit.NewId()` / `Kit.NewToken()`).

- [ ] **Adım 1: Test kitini ve düşen testleri yaz**

Kit `OrderDeck.Tests/TestHelpers/FacebookOAuthTestKit.cs`'e Produces'taki imzalarla yazılır. Davranış:
- `CreateService` public kurucuyla kurar (`() => Settings`, `Store`, `Broker`, `Graph`'a bağlı yeni `HttpClient`, `log ?? NullLogger`). `SeedAsync(n)`: birincil + n ek Sayfa üretir (kimlik `NewId()`, token `NewToken()`; ad ve Instagram hesabı da üretilmiş: her Sayfada dolu `IgUserId` ve "@"sız ham `IgUsername`), demeti `Store.SaveAsync` ile yazar (`ExpiresAt` null; n = 0 ise `ExtraPages` null), `Settings.FacebookPageId`'yi birincile kurar, her Sayfayı `Graph.AddPage`'e tanıtır.
- `Broker`: yapılandırma üretilmiş `AppId` / `LoginConfigId` ve `https://example.invalid/facebook/callback` döner; her takas `NewToken()` ile yeni bir kullanıcı token'ı (`ExpiresInSeconds = 5_184_000`) döner ve onu `LastIssuedToken`'a yazar; sayaçlar çağrı başına artar.
- `FakeGraph.SendAsync`: adresi `Requests`'e yazar ve yanıtı `Task.Run` içinde üretir (her çağrı gerçek bir bekleme noktasıdır; bekleyen bir `respond` çağıranın iş parçacığını tutmaz; `respond` aynı anda birden çok iş parçacığından çağrılabilir). Sıra: `Map` kuralları (son eklenen önce; `respond` fırlatırsa istisna isteği yapana çıkar) → Sayfa varsayılanları → boş gövdeli 404. `Json`: `application/json` gövde, `usageHeader` verilirse `X-Business-Use-Case-Usage` yanıt başlığı. `Error`: `{"error":{"message":…,"type":"OAuthException","code":<code>,"error_subcode":<subcode>}}` (alt kod verilmediyse alan yazılmaz).
- Sayfa varsayılanları (en az bir Sayfa kayıtlıyken; yola ve çözülmüş `fields` parametresine göre): `…/me/accounts` → kayıt sırasıyla `{"data":[{"id":…,"name":…,"instagram_business_account":{"id":…,"username":…}}]}` (Instagram nesnesi yalnız `igUserId` verilmiş Sayfada; `igUsername` null ise bu yanıtta ve çözümleme yanıtında `username` alanı YAZILMAZ; `paging` yok; `fields=id,name` isteği de bu yanıtı alır) · `…/me/permissions` → `instagram_basic` ve `instagram_manage_comments` `granted` · `…/{pageId}` + `fields=access_token` → `{"access_token":"<her çağrıda NewToken()>"}` · `…/{pageId}` + `fields` içinde `instagram_business_account` → `{"instagram_business_account":{"id":…,"username":…},"id":"<pageId>"}`, Instagram'sız Sayfada `{"id":"<pageId>"}`. `AddPage` aynı `pageId` ile yeniden çağrılırsa sırayı koruyarak öncekinin yerini alır.

```csharp
// OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.cs — namespace OrderDeck.Tests.Chat.Facebook; using Kit = OrderDeck.Tests.TestHelpers.FacebookOAuthTestKit;
// public sealed partial class FacebookOAuthServiceMultiPageTests : IDisposable. Gösterim: her [Fact] / [Theory] satırının altındaki girintili satırlar o yöntemin gövdesidir (süslü parantezler yazılmadı).
private readonly Kit _kit = new();                                   // Dispose → _kit.Dispose()
private async Task<(FacebookOAuthService Service, IReadOnlyList<SeededPage> Pages)> ConnectedAsync(int extraPages = 0, ILogger<FacebookOAuthService>? log = null);   // _kit.SeedAsync + _kit.CreateService(log)
private static string NewName() => "sayfa-" + Kit.NewId();   private static FacebookLinkedPage NewPage() => new(Kit.NewId(), Kit.NewToken(), NewName());
private (string PageId, string PageName) NewGraphPage();             // üretir ve Instagram hesabıyla _kit.Graph.AddPage'e tanıtır
private static FacebookAddablePage NewRow(bool igAccount);           // üretilmiş /me/accounts satırı (igAccount false → IgUserId ve IgUsername null); AlreadyConnected false
private static FacebookAddablePage Row(SeededPage s);                // tohumlanmış Sayfanın satırı; AlreadyConnected false
private static string AccountsJson(string? next, params FacebookAddablePage[] rows);   // {"data":[…],"paging":{"next":…}}; next null → paging yok
private static string NextUrl() => $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}/me/accounts?after={Kit.NewId()}";
private static bool IsAccounts(Uri u) => u.AbsolutePath.EndsWith("/me/accounts", StringComparison.Ordinal);
private static bool IsPageToken(Uri u, string pageId) => u.AbsolutePath.EndsWith("/" + pageId, StringComparison.Ordinal) && u.Query.Contains("fields=access_token");
private async Task<string[]> IdsAsync(FacebookOAuthService? service = null);   // GetConnectedPagesAsync PageId'leri; service verilmezse yeni servis örneği (diskten okur)
private byte[] Blob() => File.ReadAllBytes(_kit.TokenFilePath);
private static void AssertLogged(CapturingLogger<FacebookOAuthService> log, string prefix, params string[] parts);   // prefix ile başlayan ve parçaların hepsini içeren bir Information satırı var

[Fact] public async Task Ekler_varken_liste_birincil_once_doner_GetPageCredentialsAsync_birincilde_kalir()   // AH5 kilidi
    var (service, seeded) = await ConnectedAsync(extraPages: 2);
    (await service.GetConnectedPagesAsync()).Select(p => (p.PageId, p.PageAccessToken, p.PageName))
        .Should().Equal(seeded.Select(s => (s.Page.PageId, s.Page.PageAccessToken, s.Page.PageName)));
    (await service.GetPageCredentialsAsync())!.Value.Should().Be((seeded[0].Page.PageId, seeded[0].Page.PageAccessToken));
[Fact] public async Task Bagli_sayfalar_PageId_ye_gore_tekildir_bozuk_ek_girdi_atlanir()
    var (primary, good) = (NewPage(), NewPage());
    await _kit.Store.SaveAsync(new FacebookTokenBundle(Kit.NewToken(), primary.PageId, primary.PageAccessToken, primary.PageName, null, new FacebookLinkedPage[]
    {
        good, good with { PageAccessToken = Kit.NewToken() }, primary with { PageAccessToken = Kit.NewToken() },   // yinelenenler
        NewPage() with { PageId = "" }, NewPage() with { PageAccessToken = null! }, null!,                        // bozuk girdiler
    }));
    (await _kit.CreateService().GetConnectedPagesAsync()).Select(p => (p.PageId, p.PageAccessToken))
        .Should().Equal((primary.PageId, primary.PageAccessToken), (good.PageId, good.PageAccessToken));
[Fact] public async Task Bagli_degilken_liste_bostur_degistiren_uyeler_reddeder()
    var service = _kit.CreateService();
    (await service.GetConnectedPagesAsync()).Should().BeEmpty();
    (await service.RemoveExtraPageAsync(Kit.NewId())).Should().BeFalse();
    await FluentActions.Awaiting(() => service.ListAddablePagesAsync()).Should().ThrowAsync<InvalidOperationException>();
    await FluentActions.Awaiting(() => service.AddExtraPageAsync(Kit.NewId(), NewName())).Should().ThrowAsync<InvalidOperationException>();
    _kit.Graph.Requests.Should().BeEmpty();
[Fact] public async Task Ek_sayfa_tarayici_ve_sunucu_takasi_olmadan_eklenir_diske_yazilir()                // TK5
    var log = new CapturingLogger<FacebookOAuthService>();
    var (service, seeded) = await ConnectedAsync(log: log);
    var fresh = NewGraphPage();
    var added = await service.AddExtraPageAsync(fresh.PageId, fresh.PageName);
    (added.PageId, added.PageName).Should().Be(fresh);
    (_kit.Broker.ConfigCalls, _kit.Broker.ExchangeCalls, _kit.Settings.FacebookPageId).Should().Be((0, 0, seeded[0].Page.PageId));
    (await IdsAsync()).Should().Equal(seeded[0].Page.PageId, fresh.PageId);                                // ikinci servis örneği diskten görür
    AssertLogged(log, "Facebook OAuth", fresh.PageId, fresh.PageName);
    log.Entries.Should().NotContain(e => e.Full.Contains(added.PageAccessToken));
[Fact] public async Task Zaten_bagli_sayfa_yazmadan_doner_sinir_dolunca_yeni_sayfa_Graph_a_gitmeden_reddedilir()   // TK13
    FacebookOAuthService.MaxInstagramPages.Should().Be(4);
    var (service, seeded) = await ConnectedAsync(extraPages: 3);
    var before = Blob();
    (await service.AddExtraPageAsync(seeded[0].Page.PageId, NewName())).Should().Be(seeded[0].Page);       // birincil: mevcut girdi
    (await service.AddExtraPageAsync(seeded[2].Page.PageId, NewName())).Should().Be(seeded[2].Page);       // zaten ekli: sınır dolu olsa da mevcut girdi
    await FluentActions.Awaiting(() => service.AddExtraPageAsync(Kit.NewId(), NewName())).Should().ThrowAsync<FacebookPageLimitException>();
    (_kit.Graph.Requests.Count, Blob().SequenceEqual(before)).Should().Be((0, true));                      // Graph'a gidilmedi, dosya yazılmadı
[Fact] public async Task Kaldirma_yalniz_o_ek_girdiyi_siler_birincil_ve_bilinmeyen_kimlikte_false_doner()
    var log = new CapturingLogger<FacebookOAuthService>();
    var (service, seeded) = await ConnectedAsync(2, log);
    var before = Blob();
    (await service.RemoveExtraPageAsync(seeded[0].Page.PageId) || await service.RemoveExtraPageAsync(Kit.NewId())).Should().BeFalse();
    Blob().Should().Equal(before);                                                                         // false yazmaz
    (await service.RemoveExtraPageAsync(seeded[1].Page.PageId)).Should().BeTrue();
    (await IdsAsync()).Should().Equal(seeded[0].Page.PageId, seeded[2].Page.PageId);
    AssertLogged(log, "Facebook OAuth", seeded[1].Page.PageId, seeded[1].Page.PageName);
[Fact] public async Task Listeleme_paging_next_i_izler_bagli_olani_isaretler_Instagramsiz_sayfada_IgUserId_bostur()
    var log = new CapturingLogger<FacebookOAuthService>();
    var (service, seeded) = await ConnectedAsync(log: log);
    var (noIg, withIg, next) = (NewRow(igAccount: false), NewRow(igAccount: true), NextUrl());
    _kit.Graph.Map(IsAccounts, u => FakeGraph.Json(u.Query.Contains("after=")
        ? AccountsJson(null, withIg, Row(seeded[0])) : AccountsJson(next, Row(seeded[0]), noIg)));         // ikinci yanıt sayfasında birincil yinelenir
    var listing = await service.ListAddablePagesAsync();
    listing.NeedsReauthorize.Should().BeFalse();
    listing.Pages.Should().Equal(Row(seeded[0]) with { AlreadyConnected = true }, noIg, withIg);           // PageId'ye göre tekil; noIg.IgUserId null
    Uri.UnescapeDataString(_kit.Graph.Requests.First(IsAccounts).Query).Should()
        .Contain("fields=id,name,instagram_business_account{id,username}").And.Contain("limit=100");
    log.Entries.Should().NotContain(e => e.Full.Contains(next));                                           // paging.next loglanmaz
[Fact] public async Task Listeleme_en_cok_on_yanit_sayfasi_izler()
    var (service, _) = await ConnectedAsync();
    _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json(AccountsJson(NextUrl(), NewRow(igAccount: true))));     // bitmeyen paging.next
    (await service.ListAddablePagesAsync()).Pages.Should().HaveCount(10);
    _kit.Graph.Requests.Count(IsAccounts).Should().Be(10);
[Theory] [InlineData(190)] [InlineData(463)] [InlineData(200)] [InlineData(10)] [InlineData(100)]
public async Task Token_izin_ve_100_hatasinda_NeedsReauthorize_ve_bos_liste_doner(int code)                // 100 bu uçta "yayın bitti" değildir
    var (service, _) = await ConnectedAsync();
    _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(code));
    var listing = await service.ListAddablePagesAsync();
    (listing.NeedsReauthorize, listing.Pages.Count).Should().Be((true, 0));
[Fact] public async Task Sunucu_ve_kota_hatasinda_NeedsReauthorize_kurulmaz_hata_yukari_cikar()
    var (service, _) = await ConnectedAsync();
    var list = FluentActions.Awaiting(() => service.ListAddablePagesAsync());
    _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json("{}", HttpStatusCode.InternalServerError));
    (await list.Should().ThrowAsync<FacebookGraphException>()).Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(4, subcode: 2446079));                                 // kota
    var quota = (await list.Should().ThrowAsync<FacebookGraphException>()).Which;
    quota.Code.Should().Be(4); quota.Subcode.Should().Be(2446079); quota.StatusCode.Should().Be(HttpStatusCode.BadRequest);
[Fact] public async Task Bos_govde_hatasinin_mesajinda_token_ve_adres_gecmez()
    var (service, _) = await ConnectedAsync();
    var (userToken, pageId) = ((await service.GetUserAccessTokenAsync())!, Kit.NewId());
    _kit.Graph.Map(u => IsPageToken(u, pageId), _ => FakeGraph.Json("null"));                              // boş gövde
    (await FluentActions.Awaiting(() => service.AddExtraPageAsync(pageId, NewName())).Should().ThrowAsync<InvalidOperationException>())
        .Which.Message.Should().NotContain(userToken).And.NotContain("http");
    (await IdsAsync(service)).Should().HaveCount(1);
[Fact] public async Task Depo_yazimi_duserse_ekleme_istisna_verir_bagli_sayfalar_degismez()
    var (service, seeded) = await ConnectedAsync();
    var fresh = NewGraphPage();
    File.SetAttributes(_kit.TokenFilePath, FileAttributes.ReadOnly);                                       // hedef salt okunur: G01'in taşıma adımı düşer
    try { await FluentActions.Awaiting(() => service.AddExtraPageAsync(fresh.PageId, fresh.PageName)).Should().ThrowAsync<UnauthorizedAccessException>(); }
    finally { File.SetAttributes(_kit.TokenFilePath, FileAttributes.Normal); }
    (await IdsAsync(service)).Should().Equal(seeded[0].Page.PageId);
[Fact] public async Task Eszamanli_ekle_kaldir_kayip_guncelleme_uretmez()
    var (service, seeded) = await ConnectedAsync(extraPages: 1);
    var (a, b) = (NewGraphPage(), NewGraphPage());
    await Task.WhenAll(Task.Run(() => service.AddExtraPageAsync(a.PageId, a.PageName)), Task.Run(() => service.AddExtraPageAsync(b.PageId, b.PageName)),
        Task.Run(() => service.RemoveExtraPageAsync(seeded[1].Page.PageId))).WaitAsync(TestPatience.Upper);
    var expected = new[] { seeded[0].Page.PageId, a.PageId, b.PageId };
    (await IdsAsync(service)).Should().BeEquivalentTo(expected);
    (await IdsAsync()).Should().BeEquivalentTo(expected);                                                  // disk de aynı
[Fact] public async Task DisconnectAsync_ekler_dahil_her_seyi_siler()
    var (service, _) = await ConnectedAsync(extraPages: 2);
    await service.DisconnectAsync();
    (await service.GetConnectedPagesAsync()).Should().BeEmpty();
    (await service.IsConnectedAsync(), File.Exists(_kit.TokenFilePath), _kit.Settings.FacebookPageId).Should().Be((false, false, (string?)null));
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FacebookOAuthServiceMultiPageTests"`
Beklenen: FAIL (derleme hatası: `GetConnectedPagesAsync`, `AddExtraPageAsync`, `FacebookAddablePage`, `FacebookGraphException` ve ötekiler tanımsız)

- [ ] **Adım 3: `FacebookGraphException` ve `FacebookPageLimitException`'ı ekle (`OrderDeck.Chat/Facebook/FacebookGraphException.cs`), `GetJsonAsync`'i değiştir (`FacebookOAuthService.cs`)**

`GetJsonAsync` hata gövdesinden `error.code` ve `error.error_subcode`'u `JsonDocument` ile okur (emsal `InstagramGraphError.cs:46-62`) ve `FacebookGraphException` fırlatır. `FacebookPageLimitException(limit)` mesajı: `En çok {limit} Sayfa bağlanabilir.`

- [ ] **Adım 4: Yeni üyeleri ve kilidi uygula (`FacebookOAuthService.cs`): `GetConnectedPagesAsync`, `AddExtraPageAsync`, `RemoveExtraPageAsync`, `ListAccountsPagedAsync`, `ListAddablePagesAsync`, iki kayıt; `ConnectAsync`'in kaydetme adımı ve `DisconnectAsync` `_mutate` altına**

Değiştiren dört bölüm `await _mutate.WaitAsync(ct)` + `try / finally` ile sarılır; yeni demet güncel demetin `with { ExtraPages = … }` kopyasıdır ve `SaveAsync` döndükten SONRA `_bundle`'a atanır. Listeleme için mevcut `MeAccountsResponse` / `MeAccount` tel sınıfları `instagram_business_account` (`id`, `username`) ve `paging.next` alanlarıyla genişletilir; `ListPagesAsync`'in isteği ve sonucu değişmez.

- [ ] **Adım 5: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FacebookOAuthServiceMultiPageTests"`
Beklenen: PASS (18 test)

- [ ] **Adım 6: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~OrderDeck.Tests.Chat.Facebook|FullyQualifiedName~InstagramGraphErrorTests"`
Beklenen: PASS (`FacebookChatHostedServiceTests`, `FacebookModerationServiceTests`, `InstagramGraphErrorTests` ve G01 sınıfları değiştirilmeden). Test komutu `OrderDeck.App`'i de derler; `OrderDeck.Chat` 0 uyarı, test projesinde yeni uyarı yok.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.Chat/Facebook/FacebookOAuthService.cs OrderDeck.Chat/Facebook/FacebookGraphException.cs OrderDeck.Tests/TestHelpers/FacebookOAuthTestKit.cs OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.cs
git commit -F - <<'MSG'
feat(facebook-oauth): bağlı Sayfaları listele, ek Sayfayı tarayıcısız ekle ve kaldır

Saklı kullanıcı token'ıyla /me/accounts sayfalamalı okunur ve seçilen Sayfanın token'ı türetilir;
tarayıcı ve sunucu takası çağrılmaz. Demeti değiştiren bölümler tek kilit altında, önce diske sonra
belleğe yazar. Graph hataları kod taşır; boş gövde mesajından token taşıyan URL kaldırıldı. Test kiti eklendi.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G03: OAuth tarayıcı beklemesi: `AcquireUserTokenAsync`, 5 dk zaman aşımı, iptal, tek akış

**Spec:** §8.2 "Yeniden onay" 1. adım, "Tarayıcı beklemesi (TK17)", `access_denied` paragrafı; §8.6.1 ilk paragraf ("Facebook'a Bağlan" zaman aşımı metni); §8.3.2 değişen davranış 7; §9.1 "Tarayıcı beklemesi" maddesi; §11.3; R18.
**Bağımlı:** G02.

**Files:**
- Modify: `OrderDeck.Chat/Facebook/FacebookOAuthService.cs` (7960eba3 satırlarıyla: kurucu `:77-89`; `ConnectAsync` 0–5. adım `:144-193`; `OpenBrowser` `:275-284`; `WaitForCallbackAsync` `:286-337`. G02 satırları kaydırmıştır, bu adlarla bul)
- Create: `OrderDeck.Chat/Facebook/FacebookOAuthBrowser.cs`
- Test: `OrderDeck.Tests/TestHelpers/FacebookOAuthTestKit.cs` (değişir), `OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.BrowserWait.cs` (yeni)

**Interfaces:**
- Consumes: G02 `_mutate`, kit (`FacebookOAuthTestKit`: `Store`, `TokenFilePath`, `Settings`, `Graph`, `Broker` (`ConfigCalls`, `ExchangeCalls`, `LastIssuedToken`), `SeedAsync`, `NewId`), test sınıfının yardımcıları (`_kit`, `NewGraphPage`, `ConnectedAsync`); mevcut `IFacebookOAuthBroker.GetFacebookOAuthConfigAsync / ExchangeFacebookCodeAsync`, `FacebookLongLivedToken(string AccessToken, long ExpiresInSeconds)`, private static `GenerateState()`, `BuildAuthorizeUrl(string appId, string configId, string redirectUri, string state)`.
- Produces:

```csharp
// OrderDeck.Chat/Facebook/FacebookOAuthBrowser.cs (yeni)
internal readonly record struct FacebookOAuthCallback(string? Code, string? State, string? Error, string? ErrorDescription);
internal interface IFacebookOAuthBrowser { IFacebookOAuthCallbackWait Open(string authorizeUrl); }                // önce dinleyici, sonra tarayıcı
internal interface IFacebookOAuthCallbackWait : IDisposable { Task<FacebookOAuthCallback> WaitAsync(CancellationToken ct); }   // Dispose dinleyiciyi bırakır
[SupportedOSPlatform("windows")] internal sealed class LoopbackFacebookOAuthBrowser : IFacebookOAuthBrowser
{
    internal LoopbackFacebookOAuthBrowser();                                              // üretim: FacebookOAuthService.LoopbackPrefix + Process.Start(UseShellExecute)
    internal LoopbackFacebookOAuthBrowser(string prefix, Action<string> launchBrowser);   // test girişi
}
// FacebookOAuthService
internal static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);
internal FacebookOAuthService(Func<AppSettings> settings, EncryptedFacebookTokenStore tokenStore, IFacebookOAuthBroker broker,
    HttpClient http, ILogger<FacebookOAuthService> log, IFacebookOAuthBrowser browser, TimeSpan callbackTimeout);   // public kurucu buna zincirlenir
private Task<FacebookLongLivedToken> AcquireUserTokenAsync(CancellationToken ct);                                  // G04 de kullanır
// kit eki (BAĞLAYICI; G04, G12, G13 ve G18 de kullanır)
public FakeFacebookOAuthBrowser Browser { get; }
public FacebookOAuthService CreateService(ILogger<FacebookOAuthService>? log = null, TimeSpan? callbackTimeout = null);   // internal kurucu + Browser; süre verilmezse CallbackTimeout
internal sealed class FakeFacebookOAuthBrowser : IFacebookOAuthBrowser
{
    public IReadOnlyList<string> OpenedUrls { get; }      // Open sırasıyla, anlık kopya
    public int PendingWaits { get; }                      // Open ile +1, beklemenin Dispose'u ile -1 (dinleyici hâlâ tutuluyor mu)
    public Task WaitUntilOpenedAsync();                   // AÇIK (tamamlanmamış ve Dispose edilmemiş) bir bekleme oluşana dek yoklar; önceki akışın bitmiş beklemesi sayılmaz; TestPatience.Upper dolarsa TimeoutException
    public void Complete();                               // açık beklemeyi üretilmiş code + açılan URL'nin state parametresiyle tamamlar
    public void CompleteWithError(string error, string? description = null);   // (Code: null, State: null, error, description)
    public void CompleteWithState(string state);          // üretilmiş code + verilen state
}   // Open: adresi kaydeder, yeni bekleme döner. Bekleme: WaitAsync(ct) tamamlanınca geri çağrıyı döner, ct iptalinde OperationCanceledException. Complete*: açık bekleme yoksa InvalidOperationException
```

**Kurallar:**
- `ConnectAsync`'in imzası, Sayfa listeleme ve seçimi, "tek Sayfa dönerse seç" dalı (7960eba3'te `:196-205`) ve kaydetme adımı aynı; yalnız 0–5. adımları `AcquireUserTokenAsync`'e taşınır: yapılandırma + boş `AppId` hatası, `GenerateState`, `BuildAuthorizeUrl`, tarayıcı, geri çağrı, takas (metinler aynen).
- Geri çağrı denetimi serviste, bugünkü sırayla ve bugünkü metinlerle: `Error` doluysa `InvalidOperationException($"Facebook OAuth error: {error} — {description ?? "(no description)"}")` (`access_denied` dahil; `OperationCanceledException` DEĞİL, arayüzde "başarısız"a düşer), `Code` boşsa `Facebook OAuth callback missing ?code`, state `FixedTimeEquals` ile tutmuyorsa `OAuth state mismatch — possible CSRF.`; üçünde de takas çağrılmaz.
- Zaman aşımı: geri çağrı `callbackTimeout` içinde gelmezse dinleyici bırakılır ve `TimeoutException` fırlatılır; mesaj süreden bağımsız TAM `Facebook onayı 5 dakika içinde tamamlanmadı. Yeniden dene.`; diske yazılmaz. Ayarlar'daki mevcut dal (`SettingsViewModel.cs:452-455`) bunu `Bağlantı başarısız: ` önekiyle yazar; `:447-451` yalnız `OperationCanceledException` içindir, zaman aşımı oraya DÜŞMEMELİDİR.
- İptal: çağıranın belirteci beklemeyi keser, dinleyici bırakılır, `OperationCanceledException` yükselir.
- Tek akış: bekleyen akış varken ikinci `ConnectAsync` (G04'ten sonra `ReauthorizeAsync` de) dinleyiciye dokunmadan `InvalidOperationException` ile reddedilir; mesaj TAM `Bekleyen bir Facebook onayı var. Tarayıcıdaki onayı tamamla ya da birkaç dakika sonra yeniden dene.` Koruma `AcquireUserTokenAsync`'in İLK işidir (yapılandırma isteğinden önce) ve her çıkış yolunda bırakılır. `_mutate` tarayıcı beklemesini kapsamaz: `AcquireUserTokenAsync` kilit almaz.
- `LoopbackFacebookOAuthBrowser`: `Open` önce dinleyiciyi başlatır, sonra tarayıcıyı açar; bağlanamazsa bugünkü `Failed to bind {önek} — another process is using the port, or you need to run as administrator once to grant URL ACL.` `InvalidOperationException`'ı; tarayıcı açılamazsa dinleyici kapatılıp istisna yükselir. `WaitAsync` bugünkü `WaitForCallbackAsync` gövdesidir (HTML yanıtları AYNEN; iptalde dinleyici durur ve `OperationCanceledException`), ama hata ve eksik code için fırlatmaz: dört alanı `FacebookOAuthCallback` olarak döner. Tek ek: `WaitAsync` ilk iş `ct.ThrowIfCancellationRequested()` çağırır. Belirteç çağrıdan ÖNCE iptal edilmişse bugünkü gövde `OperationCanceledException` üretmez (kayıt geri çağrısı dinleyiciyi hemen durdurur, `GetContextAsync` durmuş dinleyicide `InvalidOperationException` fırlatır, iki süzgeç onu yakalamaz); `Open` ile `WaitAsync` arasına düşen iptal (G18'in `[İptal]`i) arayüzde "başarısız" görünürdü. Sınıf `[SupportedOSPlatform("windows")]` taşır (servisin `LoopbackPrefix`'ine başvurur, CA1416).
- Public kurucunun imzası aynı; `AppHost.cs:274-282` ve `SettingsViewModel.cs` DEĞİŞMEZ.

- [ ] **Adım 1: Kit ekini ve düşen testleri yaz**

Kit eki Produces bloğundaki imzalarla `FacebookOAuthTestKit.cs`'e yazılır (`CreateService` artık `internal` kurucuyu ve `Browser`'ı kullanır; G02 testleri değişmez).

```csharp
// OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.BrowserWait.cs — public sealed partial class FacebookOAuthServiceMultiPageTests
// Gösterim: her [Fact] satırının altındaki girintili satırlar o yöntemin gövdesidir (süslü parantezler yazılmadı). Takma ad dosyaya özeldir, burada da yazılır: using Kit = OrderDeck.Tests.TestHelpers.FacebookOAuthTestKit;
private const string PendingFlowMessage = "Bekleyen bir Facebook onayı var. Tarayıcıdaki onayı tamamla ya da birkaç dakika sonra yeniden dene.";
private static Task<FacebookPageCandidate?> NoPicker(IReadOnlyList<FacebookPageCandidate> pages, CancellationToken ct);   // null döner (tek Sayfada çağrılmaz)
private static int FreePort();                    // TcpListener(IPAddress.Loopback, 0); emsal ExtensionBridgeServer.cs:722-729
private async Task CompleteAsync(Task flow);      // _kit.Browser.WaitUntilOpenedAsync() + Complete() + flow.WaitAsync(TestPatience.Upper)

[Fact] public async Task Tek_sayfada_ConnectAsync_secici_sormadan_eksiz_demet_yazar()
    var page = NewGraphPage();
    await CompleteAsync(_kit.CreateService().ConnectAsync(NoPicker));
    var bundle = (await _kit.Store.LoadAsync())!;
    (bundle.PageId, bundle.PageName, bundle.UserAccessToken).Should().Be((page.PageId, page.PageName, _kit.Broker.LastIssuedToken));
    bundle.ExtraPages.Should().BeNull();
    _kit.Settings.FacebookPageId.Should().Be(page.PageId);
    _kit.Browser.OpenedUrls.Single().Should().Contain("response_type=code").And.Contain("state=");
    _kit.Browser.PendingWaits.Should().Be(0);
[Fact] public async Task Geri_cagri_gelmezse_sure_dolunca_TimeoutException_firlar_diske_yazilmaz_dinleyici_birakilir()
    FacebookOAuthService.CallbackTimeout.Should().Be(TimeSpan.FromMinutes(5));          // spec'in süresi (TK17); akışın kendisi kısaltılmış süreyle koşar
    var service = _kit.CreateService(callbackTimeout: TimeSpan.FromMilliseconds(200));
    var connect = FluentActions.Awaiting(() => service.ConnectAsync(NoPicker).WaitAsync(TestPatience.Upper));   // servis zaman aşımı hiç çalışmazsa test asılmaz: üst sınırın kendi TimeoutException'ı aşağıdaki mesajı taşımaz, doğrulama düşer
    (await connect.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Be("Facebook onayı 5 dakika içinde tamamlanmadı. Yeniden dene.");
    (File.Exists(_kit.TokenFilePath), _kit.Browser.PendingWaits).Should().Be((false, 0));
    (await connect.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().StartWith("Facebook onayı");   // hemen ardından başlatılan akış reddedilmez, yeniden bağlanır
    _kit.Browser.OpenedUrls.Should().HaveCount(2);
[Fact] public async Task Belirtec_iptali_OperationCanceledException_uretir_ve_dinleyiciyi_birakir()
    using var cts = new CancellationTokenSource();
    var connecting = _kit.CreateService().ConnectAsync(NoPicker, cts.Token);
    await _kit.Browser.WaitUntilOpenedAsync();
    cts.Cancel();
    await FluentActions.Awaiting(() => connecting.WaitAsync(TestPatience.Upper)).Should().ThrowAsync<OperationCanceledException>();
    (File.Exists(_kit.TokenFilePath), _kit.Browser.PendingWaits).Should().Be((false, 0));
[Fact] public async Task Bekleyen_akis_varken_ikinci_ConnectAsync_dinleyiciye_dokunmadan_reddedilir()
    var service = _kit.CreateService();
    using var cts = new CancellationTokenSource();
    var first = service.ConnectAsync(NoPicker, cts.Token);
    await _kit.Browser.WaitUntilOpenedAsync();
    (await FluentActions.Awaiting(() => service.ConnectAsync(NoPicker).WaitAsync(TestPatience.Upper)).Should().ThrowAsync<InvalidOperationException>())
        .Which.Message.Should().Be(PendingFlowMessage);
    (_kit.Browser.OpenedUrls.Count, _kit.Broker.ConfigCalls).Should().Be((1, 1));
    await service.DisconnectAsync().WaitAsync(TestPatience.Upper);  // _mutate tarayıcı beklemesini kapsamaz
    cts.Cancel();
    await FluentActions.Awaiting(() => first.WaitAsync(TestPatience.Upper)).Should().ThrowAsync<OperationCanceledException>();
[Fact] public async Task Access_denied_ve_state_uyusmazligi_InvalidOperationException_olarak_kalir()
    var (service, aciklama) = (_kit.CreateService(), Kit.NewId());
    var denied = service.ConnectAsync(NoPicker);
    await _kit.Browser.WaitUntilOpenedAsync();
    _kit.Browser.CompleteWithError("access_denied", aciklama);
    (await FluentActions.Awaiting(() => denied.WaitAsync(TestPatience.Upper)).Should().ThrowAsync<InvalidOperationException>())
        .Which.Message.Should().Be($"Facebook OAuth error: access_denied — {aciklama}");
    var forged = service.ConnectAsync(NoPicker);                    // koruma bırakılmış: yeni akış açılır
    await _kit.Browser.WaitUntilOpenedAsync();
    _kit.Browser.CompleteWithState(Kit.NewId());
    (await FluentActions.Awaiting(() => forged.WaitAsync(TestPatience.Upper)).Should().ThrowAsync<InvalidOperationException>())
        .Which.Message.Should().Be("OAuth state mismatch — possible CSRF.");
    _kit.Broker.ExchangeCalls.Should().Be(0);
[Fact] public async Task Gercek_dinleyici_iptalden_sonra_ayni_oneki_yeniden_baglar_ve_geri_cagriya_HTML_doner()
    var (launched, code, state, url) = (new List<string>(), Kit.NewId(), Kit.NewId(), "https://example.invalid/" + Kit.NewId());
    var prefix = $"http://localhost:{FreePort()}/facebook/callback/";
    var browser = new LoopbackFacebookOAuthBrowser(prefix, launched.Add);
    using var cts = new CancellationTokenSource();
    var canceled = browser.Open(url);
    var waiting = canceled.WaitAsync(cts.Token);
    cts.Cancel();
    await FluentActions.Awaiting(() => waiting.WaitAsync(TestPatience.Upper)).Should().ThrowAsync<OperationCanceledException>();
    canceled.Dispose();
    using (var late = browser.Open(url))                            // belirteç WaitAsync'ten ÖNCE iptal edilmiş: yine OperationCanceledException
        await FluentActions.Awaiting(() => late.WaitAsync(new CancellationToken(canceled: true))).Should().ThrowAsync<OperationCanceledException>();
    using var wait = browser.Open(url);                             // iptalden sonra aynı önek yeniden bağlanır
    var callback = wait.WaitAsync(CancellationToken.None);
    using var http = new HttpClient();
    var html = await http.GetStringAsync($"{prefix}?code={code}&state={state}");
    (await callback.WaitAsync(TestPatience.Upper)).Should().Be(new FacebookOAuthCallback(code, state, null, null));
    html.Should().Contain("Facebook bağlantısı tamamlandı.");
    launched.Should().Equal(url, url, url);
[Fact] public async Task Tarayicisiz_ekleme_tarayici_acmaz()                                       // TK5
    var (service, _) = await ConnectedAsync();
    var fresh = NewGraphPage();
    await service.AddExtraPageAsync(fresh.PageId, fresh.PageName);
    _kit.Browser.OpenedUrls.Should().BeEmpty();
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FacebookOAuthServiceMultiPageTests"`
Beklenen: FAIL (derleme hatası: `IFacebookOAuthBrowser`, `FacebookOAuthCallback`, `LoopbackFacebookOAuthBrowser`, `CallbackTimeout` ve yedi parametreli kurucu yok)

- [ ] **Adım 3: Tarayıcı dikişini ve `LoopbackFacebookOAuthBrowser`'ı yaz (`OrderDeck.Chat/Facebook/FacebookOAuthBrowser.cs`)**

`OpenBrowser` ve `WaitForCallbackAsync` gövdeleri servisten buraya TAŞINIR (serviste kalmaz); dinleyici beklemenin içinde yaşar ve `Dispose` ile kapanır.

- [ ] **Adım 4: `CallbackTimeout`, `internal` kurucu ve `AcquireUserTokenAsync`'i uygula; `ConnectAsync` onu çağırsın (`FacebookOAuthService.cs`)**

Tek akış koruması `Interlocked.CompareExchange` ile tutulan bir `int` alandır. Zaman aşımı, çağıranın `ct`'sine bağlı ve `callbackTimeout` sonra iptal olan bir `CancellationTokenSource` ile kurulur; iptalin kaynağı çağıranın `ct`'si değilse `TimeoutException`'a çevrilir. Bekleme nesnesi `using` ile geri çağrı alınır alınmaz (takastan önce) bırakılır.

- [ ] **Adım 5: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FacebookOAuthServiceMultiPageTests"`
Beklenen: PASS (25 test: G02'nin 18'i + 7)

- [ ] **Adım 6: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~OrderDeck.Tests.Chat.Facebook"`
Beklenen: PASS. Test komutu `OrderDeck.App`'i de derler (`AppHost.cs:274-282` beş argümanlı public kurucuyla, değişmeden); `OrderDeck.Chat` 0 uyarı, test projesinde yeni uyarı yok.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.Chat/Facebook/FacebookOAuthService.cs OrderDeck.Chat/Facebook/FacebookOAuthBrowser.cs OrderDeck.Tests/TestHelpers/FacebookOAuthTestKit.cs OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.BrowserWait.cs
git commit -F - <<'MSG'
feat(facebook-oauth): tarayıcı onayına zaman aşımı ve tek akış koruması ekle

Onay sekmesi kapatılınca bekleme süresiz kalıyor, 4849 dinleyicisi açık kaldığı için
sonraki deneme "Failed to bind" ile düşüyordu. Bekleme artık 5 dakikada TimeoutException
ile biter; bekleyen akış varken ikinci deneme dinleyiciye dokunmadan reddedilir.
Kullanıcı token'ını alan adımlar AcquireUserTokenAsync'e çıkarıldı (yeniden onay da kullanacak).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G04: `ReauthorizeAsync` (ekleri korur, birincil değişmez, `confirmMissing`)

**Spec:** §8.2 "Yeniden onay (TK4)" adım 1–6, "Eşzamanlılık"; §8.8; §9.1 `ReauthorizeAsync` maddesi ve "Tarayıcı beklemesi" maddesinin `ReauthorizeAsync` yarısı; R1, R7. Plan kararı (spec susuyor; başlıkta "Plan kararları"): onay beklenirken bağlantı kaldırılırsa hiçbir şey yazılmaz, `InvalidOperationException` fırlatılır, bağlantı dirilmez.
**Bağımlı:** G02, G03.

**Files:**
- Modify: `OrderDeck.Chat/Facebook/FacebookOAuthService.cs` (yeni public yöntem `RemoveExtraPageAsync`'in ardına; `FacebookReauthorizeResult` kaydı dosya sonundaki kayıtların yanına)
- Test: `OrderDeck.Tests/TestHelpers/FacebookOAuthTestKit.cs` (değişir), `OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.Reauthorize.cs` (yeni)

**Interfaces:**
- Consumes: G03 `private Task<FacebookLongLivedToken> AcquireUserTokenAsync(CancellationToken ct)`, kit `Browser` (`WaitUntilOpenedAsync`, `Complete`, `OpenedUrls`), `CreateService(ILogger<FacebookOAuthService>? log = null, TimeSpan? callbackTimeout = null)`, test sınıfındaki `PendingFlowMessage` ve `CompleteAsync(Task flow)`; G02 `private Task<IReadOnlyList<FacebookAddablePage>> ListAccountsPagedAsync(string userAccessToken, CancellationToken ct)`, `_mutate`, `GetConnectedPagesAsync(CancellationToken ct = default)`, `AddExtraPageAsync`, `RemoveExtraPageAsync`, `FacebookGraphException` (`Code`), kit (`SeedAsync`, `Store`, `TokenFilePath`, `Graph.Map`, `FakeGraph.Json / Error`, `Broker.LastIssuedToken / ExchangeCalls`, `NewToken`), test sınıfının yardımcıları (`ConnectedAsync`, `IsAccounts`, `IsPageToken`, `AccountsJson`, `Row`, `NextUrl`, `NewGraphPage`, `IdsAsync`, `Blob`, `AssertLogged`); mevcut private `GetPageAccessTokenAsync(string pageId, string userAccessToken, CancellationToken ct)`.
- Produces:
  - `public sealed record FacebookReauthorizeResult(int RefreshedPages, IReadOnlyList<FacebookLinkedPage> MissingPages);`
  - `public Task<FacebookReauthorizeResult> ReauthorizeAsync(Func<IReadOnlyList<FacebookLinkedPage>, CancellationToken, Task<bool>> confirmMissing, CancellationToken ct = default);`
  - Çağırana çıkan istisnalar (G18 metinlerini bunlara göre seçer): `OperationCanceledException` (onay reddi ya da belirteç iptali), `TimeoutException` (G03), `InvalidOperationException` (bağlı değil; bekleyen akış; `access_denied`; bekleme sırasında bağlantı kaldırıldıysa mesaj TAM `Onay beklenirken Facebook bağlantısı kaldırıldı.`), `FacebookGraphException` / `HttpRequestException` (listeleme), `TaskCanceledException` (listelemede HttpClient zaman aşımı; çağıranın belirteci iptal edilmemiştir, G18 bunu kullanıcı iptali saymaz). Hepsinde diskteki demet değişmemiştir.
  - Kit eki (BAĞLAYICI; G11, G12, G13 ve G18 "token değişimi"ni bununla üretir): `public Task<FacebookReauthorizeResult> ReauthorizeAllAsync(FacebookOAuthService service);`

**Kurallar:** `ReauthorizeAsync` sırasıyla (spec §8.2 "Yeniden onay" 1–6):
1. Bağlı değilse tarayıcı açmadan `InvalidOperationException("Facebook bağlı değil.")`. Sonra `AcquireUserTokenAsync(ct)`: tek akış, zaman aşımı ve iptal kuralları oradan gelir, sarılmaz.
2. Yeni token'la `ListAccountsPagedAsync` (sayfalamasız `ListPagesAsync` DEĞİL). Hata (190 / 463 / 200 / 10 / 100 dahil) eşlenmeden yükselir: boş liste sayılmaz, `confirmMissing` çağrılmaz, yazılmaz.
3. Bağlı Sayfalar `GetConnectedPagesAsync` ile O ANDA okunur; boşsa (bekleme sırasında bağlantı kaldırılmış) `InvalidOperationException("Onay beklenirken Facebook bağlantısı kaldırıldı.")`. Eksik = bağlı olup listede olmayan (birincil dahil, bağlı sırasıyla). Eksik varsa `confirmMissing(eksik, ct)` kilit DIŞINDA çağrılır; false dönerse hiçbir şey yazılmaz ve `OperationCanceledException` fırlatılır.
4. Listede görünen her bağlı Sayfanın token'ı bağlı sırasıyla (birincil önce), kilit DIŞINDA `GetPageAccessTokenAsync(pageId, yeniToken, ct)` ile yeniden türetilir. İptal dışındaki hata yalnız o Sayfayı atlar: Warning loglanır (`Facebook OAuth` öneki, PageId; token ve URL yok), eski token'ı kalır. İptal = çağıranın `ct`'sinin iptal edilmiş olmasıdır (emsal süzgeç `catch (OperationCanceledException) when (ct.IsCancellationRequested)`, `InstagramAccountResolver.cs:92-95`); `ct` iptal edilmemişken gelen `TaskCanceledException` (HttpClient zaman aşımı; `AppHost.cs:281`'de 30 sn) hatadır, o Sayfa atlanır.
5. Tek yazım `_mutate` altında: güncel demet YENİDEN okunur; yoksa 3. adımdaki `InvalidOperationException` fırlatılır, hiçbir şey yazılmaz (bağlantı dirilmez). Yeni demet güncel demetin `with` kopyasıdır: `UserAccessToken` ve `ExpiresAt` (`ConnectAsync`'teki hesapla) değişir; token'ı 4. adımda türetilmiş Sayfaların (PageId eşleşmesiyle, birincil dahil) yalnız `PageAccessToken`'ı değişir. Sayfa eklenmez, çıkarılmaz: eksik Sayfanın girdisi SİLİNMEZ, bekleme sırasında kaldırılan ek geri gelmez, eklenen ek korunur. Önce disk, sonra `_bundle`.
6. Sayfa seçici yok, birincil kaymaz (`ConnectAsync`'in "tek Sayfa dönerse seç" dalı bu yolda YOK). `RefreshedPages` = kaydedilen demete 4. adımda türetilmiş token'ı YAZILAN Sayfa sayısı (birincil dahil; türetilip de kaydedilmeyen, ör. o arada kaldırılmış Sayfa sayılmaz; değer eskisiyle aynı çıksa da sayılır); `MissingPages` = 3. adımın eksik listesi. Günlük (Information): `Facebook OAuth reauthorized for Page {PageId} ({PageName}): {Refreshed} refreshed, {Missing} missing [{MissingPageIds}]` (birincilin kimliği ve adı; eksik kimlikler virgülle).

- [ ] **Adım 1: Kit ekini ve düşen testleri yaz**

Kit eki (`FacebookOAuthTestKit.cs`): `ReauthorizeAllAsync(service)` `service.ReauthorizeAsync((_, _) => Task.FromResult(true))`'yi başlatır, `Browser.WaitUntilOpenedAsync()` sonrası `Browser.Complete()` eder ve sonucu `TestPatience.Upper` içinde bekler. Ön koşul: bağlı Sayfalar `Graph`'ta kayıtlıdır (`SeedAsync` / `Graph.AddPage`); o zaman hepsi listelenir ve her birine yeni token gelir.

```csharp
// OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.Reauthorize.cs — public sealed partial class FacebookOAuthServiceMultiPageTests
// Gösterim: her [Fact] / [Theory] satırının altındaki girintili satırlar o yöntemin gövdesidir (süslü parantezler yazılmadı). Dosya başında using Kit = OrderDeck.Tests.TestHelpers.FacebookOAuthTestKit; ve:
using Confirm = System.Func<System.Collections.Generic.IReadOnlyList<OrderDeck.Chat.Facebook.FacebookLinkedPage>, System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>>;
private static Confirm Answer(bool answer, List<IReadOnlyList<FacebookLinkedPage>>? asked = null);   // (missing, _) => { asked?.Add(missing); return Task.FromResult(answer); }
private async Task<FacebookReauthorizeResult> RunAsync(FacebookOAuthService service, Confirm confirm);   // ReauthorizeAsync'i başlatır, CompleteAsync ile onayı tamamlar, sonucu döner
// Birincilin token isteğini tutar (_kit.Graph.Map): istek gelince Reached tamamlanır; Release() çağrılana dek (en çok TestPatience.Upper) bekler, sonra yeni bir token döner.
private (Task Reached, Action Release) HoldPrimaryTokenRequest(SeededPage primary);

[Fact] public async Task Butun_bagli_sayfalarin_tokeni_yenilenir_birincil_degismez()
    var log = new CapturingLogger<FacebookOAuthService>();
    var (service, seeded) = await ConnectedAsync(2, log);
    var result = await _kit.ReauthorizeAllAsync(service);
    (result.RefreshedPages, result.MissingPages.Count).Should().Be((3, 0));
    var pages = await service.GetConnectedPagesAsync();
    pages.Select(p => p.PageId).Should().Equal(seeded.Select(s => s.Page.PageId));              // sıra ve birincil aynı
    pages.Select(p => p.PageAccessToken).Should().NotIntersectWith(seeded.Select(s => s.Page.PageAccessToken));
    var bundle = (await _kit.Store.LoadAsync())!;                                               // diskteki demet
    (bundle.UserAccessToken, bundle.PageAccessToken).Should().Be((_kit.Broker.LastIssuedToken, pages[0].PageAccessToken));
    bundle.ExpiresAt.Should().NotBeNull();
    _kit.Broker.ExchangeCalls.Should().Be(1);
    AssertLogged(log, "Facebook OAuth reauthorized", seeded[0].Page.PageId, seeded[0].Page.PageName);
    log.Entries.Should().NotContain(e => e.Full.Contains(bundle.UserAccessToken) || pages.Any(p => e.Full.Contains(p.PageAccessToken)));
[Fact] public async Task Ikinci_yanit_sayfasinda_gelen_bagli_sayfa_eksik_sayilmaz()             // sayfalamalı listeleme
    var (service, seeded) = await ConnectedAsync(1);
    var (next, asked) = (NextUrl(), new List<IReadOnlyList<FacebookLinkedPage>>());
    _kit.Graph.Map(IsAccounts, u => FakeGraph.Json(u.Query.Contains("after=") ? AccountsJson(null, Row(seeded[1])) : AccountsJson(next, Row(seeded[0]))));
    var result = await RunAsync(service, Answer(true, asked));
    asked.Should().BeEmpty();
    (result.RefreshedPages, result.MissingPages.Count).Should().Be((2, 0));
[Fact] public async Task Yeni_giriste_olmayan_sayfa_icin_onay_sorulur_hayirda_diske_hicbir_sey_yazilmaz()
    var (service, seeded) = await ConnectedAsync(1);
    _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json(AccountsJson(null, Row(seeded[0]))));        // ek Sayfa yeni girişte yok
    var (asked, before) = (new List<IReadOnlyList<FacebookLinkedPage>>(), Blob());
    await FluentActions.Awaiting(() => RunAsync(service, Answer(false, asked))).Should().ThrowAsync<OperationCanceledException>();
    asked.Single().Select(p => p.PageId).Should().Equal(seeded[1].Page.PageId);
    Blob().Should().Equal(before);
    (await service.GetUserAccessTokenAsync()).Should().NotBe(_kit.Broker.LastIssuedToken);      // bellek de değişmedi
[Theory] [InlineData(0)] [InlineData(1)]                                                        // eksik olan: birincil / ek
public async Task Onay_evetse_eksik_sayfanin_girdisi_silinmez_eski_tokeni_kalir_birincil_kaymaz(int missing)
    var log = new CapturingLogger<FacebookOAuthService>();
    var (service, seeded) = await ConnectedAsync(1, log);
    var (gone, kept) = (seeded[missing].Page, seeded[1 - missing].Page);
    _kit.Graph.Map(IsAccounts, _ => FakeGraph.Json(AccountsJson(null, Row(seeded[1 - missing]))));   // yeni girişte tek Sayfa
    var result = await RunAsync(service, Answer(true));
    (result.RefreshedPages, result.MissingPages.Single().PageId).Should().Be((1, gone.PageId));
    var pages = await service.GetConnectedPagesAsync();
    pages.Select(p => p.PageId).Should().Equal(seeded.Select(s => s.Page.PageId));              // girdi silinmez, birincil kaymaz
    pages.Single(p => p.PageId == gone.PageId).PageAccessToken.Should().Be(gone.PageAccessToken);
    pages.Single(p => p.PageId == kept.PageId).PageAccessToken.Should().NotBe(kept.PageAccessToken);
    AssertLogged(log, "Facebook OAuth reauthorized", gone.PageId);
[Theory] [InlineData(200)] [InlineData(100)] [InlineData(190)]
public async Task Listeleme_hata_verirse_istisna_cikar_onay_sorulmaz_diske_yazilmaz(int code)
    var (service, _) = await ConnectedAsync(1);
    _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(code));
    var (asked, before) = (new List<IReadOnlyList<FacebookLinkedPage>>(), Blob());
    (await FluentActions.Awaiting(() => RunAsync(service, Answer(true, asked))).Should().ThrowAsync<FacebookGraphException>()).Which.Code.Should().Be(code);
    asked.Should().BeEmpty();
    Blob().Should().Equal(before);
[Theory] [InlineData(false)] [InlineData(true)]                                                 // Graph hatası / ağ zaman aşımı (belirteç iptal edilmedi: iptal sayılmaz, Sayfa atlanır)
public async Task Tokeni_turetilemeyen_sayfanin_eski_tokeni_kalir(bool timedOut)
    var (service, seeded) = await ConnectedAsync(1);
    _kit.Graph.Map(u => IsPageToken(u, seeded[1].Page.PageId), _ => timedOut ? throw new TaskCanceledException() : FakeGraph.Error(200));
    var result = await RunAsync(service, Answer(true));
    (result.RefreshedPages, result.MissingPages.Count).Should().Be((1, 0));
    (await service.GetConnectedPagesAsync())[1].PageAccessToken.Should().Be(seeded[1].Page.PageAccessToken);
[Fact] public async Task Bagli_degilken_ReauthorizeAsync_tarayici_acmadan_reddedilir()
    await FluentActions.Awaiting(() => _kit.CreateService().ReauthorizeAsync(Answer(true))).Should().ThrowAsync<InvalidOperationException>();
    _kit.Browser.OpenedUrls.Should().BeEmpty();
[Fact] public async Task Bekleyen_akis_varken_ReauthorizeAsync_dinleyiciye_dokunmadan_reddedilir()   // TK17
    var (service, _) = await ConnectedAsync();
    var before = Blob();
    using var cts = new CancellationTokenSource();
    var first = service.ReauthorizeAsync(Answer(true), cts.Token);
    await _kit.Browser.WaitUntilOpenedAsync();
    (await FluentActions.Awaiting(() => service.ReauthorizeAsync(Answer(true)).WaitAsync(TestPatience.Upper)).Should().ThrowAsync<InvalidOperationException>())
        .Which.Message.Should().Be(PendingFlowMessage);
    _kit.Browser.OpenedUrls.Should().HaveCount(1);
    cts.Cancel();
    await FluentActions.Awaiting(() => first.WaitAsync(TestPatience.Upper)).Should().ThrowAsync<OperationCanceledException>();
    Blob().Should().Equal(before);
[Fact] public async Task Zaman_asiminda_TimeoutException_cikar_diske_yazilmaz()
    await _kit.SeedAsync();
    var (service, before) = (_kit.CreateService(callbackTimeout: TimeSpan.FromMilliseconds(200)), Blob());
    (await FluentActions.Awaiting(() => service.ReauthorizeAsync(Answer(true)).WaitAsync(TestPatience.Upper)).Should().ThrowAsync<TimeoutException>())
        .Which.Message.Should().StartWith("Facebook onayı");                                    // servisin zaman aşımı (G03); üst sınırın kendi TimeoutException'ı bu mesajı taşımaz: test asılmaz, düşer
    Blob().Should().Equal(before);
[Fact] public async Task Onay_beklenirken_baglanti_kaldirilirsa_geri_cagri_gelince_baglanti_dirilmez()   // Review Focus 1: hata 3. adımda çıkar (bağlı Sayfa kalmadı)
    var (service, _) = await ConnectedAsync(1);
    var pending = service.ReauthorizeAsync(Answer(true));
    await _kit.Browser.WaitUntilOpenedAsync();
    await service.DisconnectAsync().WaitAsync(TestPatience.Upper);                              // kilit tarayıcı beklemesini kapsamaz
    _kit.Browser.Complete();
    (await FluentActions.Awaiting(() => pending.WaitAsync(TestPatience.Upper)).Should().ThrowAsync<InvalidOperationException>())
        .Which.Message.Should().Be("Onay beklenirken Facebook bağlantısı kaldırıldı.");
    (File.Exists(_kit.TokenFilePath), await service.IsConnectedAsync()).Should().Be((false, false));
[Fact] public async Task Tokenlar_turetilirken_baglanti_kaldirilirsa_kayit_adiminda_baglanti_dirilmez()   // Review Focus 1: hata 5. adımda çıkar (kilit altı yeniden okuma)
    var (service, seeded) = await ConnectedAsync(1);
    var (reached, release) = HoldPrimaryTokenRequest(seeded[0]);
    var pending = RunAsync(service, Answer(true));
    await reached.WaitAsync(TestPatience.Upper);                                                // 3. adım geçildi; 4. adım birincilin token isteğinde bekliyor
    await service.DisconnectAsync().WaitAsync(TestPatience.Upper);                              // kilit türetmeyi kapsamaz
    release();
    (await FluentActions.Awaiting(() => pending).Should().ThrowAsync<InvalidOperationException>())
        .Which.Message.Should().Be("Onay beklenirken Facebook bağlantısı kaldırıldı.");
    (File.Exists(_kit.TokenFilePath), await service.IsConnectedAsync()).Should().Be((false, false));
[Fact] public async Task Tokenlar_turetilirken_kaldirilan_ek_sayfa_geri_gelmez_eklenen_korunur()    // Review Focus 1
    var (service, seeded) = await ConnectedAsync(1);
    var (fresh, (reached, release)) = (NewGraphPage(), HoldPrimaryTokenRequest(seeded[0]));
    var pending = RunAsync(service, Answer(true));
    await reached.WaitAsync(TestPatience.Upper);
    (await service.RemoveExtraPageAsync(seeded[1].Page.PageId).WaitAsync(TestPatience.Upper)).Should().BeTrue();   // kilit türetmeyi kapsamaz
    await service.AddExtraPageAsync(fresh.PageId, fresh.PageName).WaitAsync(TestPatience.Upper);
    release();
    (await pending).RefreshedPages.Should().Be(1);
    (await IdsAsync(service)).Should().Equal(seeded[0].Page.PageId, fresh.PageId);
    (await IdsAsync()).Should().Equal(seeded[0].Page.PageId, fresh.PageId);
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FacebookOAuthServiceMultiPageTests"`
Beklenen: FAIL (derleme hatası: `ReauthorizeAsync` ve `FacebookReauthorizeResult` tanımsız)

- [ ] **Adım 3: `FacebookReauthorizeResult` ve `ReauthorizeAsync`'i uygula (`OrderDeck.Chat/Facebook/FacebookOAuthService.cs`)**

Kurallar'daki altı adım sırayla yazılır; 2–4. adımlar kilit dışında, yalnız 5. adım `_mutate` altındadır. Türetilen token'lar PageId anahtarlı bir sözlükte toplanır ve 5. adımda güncel demete uygulanır.

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FacebookOAuthServiceMultiPageTests"`
Beklenen: PASS (41 test: G02'nin 18'i + G03'ün 7'si + 16)

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~OrderDeck.Tests.Chat.Facebook|FullyQualifiedName~InstagramGraphErrorTests"`
Beklenen: PASS (`FacebookOAuthServiceMultiPageTests` üç dosyasıyla; öteki sınıflar değiştirilmeden). Test komutu `OrderDeck.App`'i de derler; `OrderDeck.Chat` 0 uyarı, test projesinde yeni uyarı yok.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.Chat/Facebook/FacebookOAuthService.cs OrderDeck.Tests/TestHelpers/FacebookOAuthTestKit.cs OrderDeck.Tests/Chat/Facebook/FacebookOAuthServiceMultiPageTests.Reauthorize.cs
git commit -F - <<'MSG'
feat(facebook-oauth): ekleri koruyan yeniden onay akışını ekle

Yeni Sayfa saklı girişle görünmüyorsa tek yol tarayıcılı izin güncellemesidir.
ReauthorizeAsync kullanıcı token'ını ve bağlı bütün Sayfaların token'larını yeniler;
birincili değiştirmez, eksik Sayfada kaydetmeden önce onay ister ve girdisini silmez.
Kayıt adımı güncel demeti kilit altında yeniden okur: onay beklenirken kaldırılan
bağlantı dirilmez, kaldırılan ek Sayfa geri gelmez.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G05: `ResolveDetailedAsync` + `TryGetUsage`

**Spec:** §8.3.4; §5 "Birim uyarısı"; §9.1 `InstagramAccountResolverDetailedTests` ve "`InstagramGraphErrorTests` (ek)"; §10 adım 3.
**Bağımlı:** yok.

**Files:**
- Modify: `OrderDeck.Chat/Ingestors/Instagram/InstagramAccountResolver.cs:12` (yanına enum), `:44-101` (`ResolveAsync`) (bugünkü satırlar)
- Modify: `OrderDeck.Chat/Ingestors/Instagram/InstagramGraphError.cs:76-116` (`TryGetRetryAfter`'ın yanına ekleme)
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramAccountResolverDetailedTests.cs` (yeni), `OrderDeck.Tests/Chat/Instagram/InstagramGraphErrorTests.cs` (sınıfın sonuna ek)

**Interfaces:**
- Consumes: yok (mevcut `public readonly record struct InstagramAccount(string IgUserId, string? Username)`).
- Produces: `public enum InstagramResolveStatus { Found, NotLinked, Failed }` · `public Task<(InstagramAccount? Account, InstagramResolveStatus Status)> ResolveDetailedAsync(string pageId, string pageAccessToken, CancellationToken ct);` (`InstagramAccountResolver` üyesi; `Account` yalnız `Found`'da dolu) · `public sealed record InstagramBucUsage(string BusinessId, double CallCountPercent, double TotalCpuTimePercent, double TotalTimePercent, double? MinutesToRegainAccess);` · `public static bool TryGetUsage(string? headerValue, out IReadOnlyList<InstagramBucUsage> usage);` (`InstagramGraphError` üyesi; `usage` hiç null olmaz).

**Kurallar:**
- `ResolveAsync` (`:44`) imza, davranış ve günlük satırlarıyla aynı kalır; `InstagramAccountResolverTests` DEĞİŞTİRİLMEZ. `Invalidate()` (`:104-108`), `TryGetRetryAfter` ve `Classify` aynen.
- İki yöntem TEK önbelleği paylaşır. Yalnız `Found` önbelleklenir ve önbellekten dönen `Found` istek atmaz; `NotLinked` ve `Failed` her çağrıda yeniden sorar.
- `NotLinked` = 2xx yanıtta `instagram_business_account` nesnesi ya da dolu `id` yok. `Failed` = 2xx dışı yanıt, ağ hatası, ayrıştırılamayan gövde. Çağıranın iptali yutulmaz, yükselir (bugünkü `:92-95` süzgeciyle; yöntemin başına erken iptal denetimi EKLENMEZ, gövde bugünküdür).
- `TryGetUsage`: başlığın üst düzey her anahtarı bir `BusinessId`, altındaki dizinin her nesnesi bir kayıttır (belge sırasıyla). `call_count` / `total_cputime` / `total_time` YÜZDEDİR ve aynen taşınır (kesirli olabilir; alan yoksa 0, karar). `estimated_time_to_regain_access` varsa aynen (0 dahil, yuvarlanmaz), yoksa null. En az bir kova okunduysa true; boş, bozuk ya da kovasız başlıkta false ve boş liste (karar). Otomatik fren yok: çıktı yalnız ölçüm ve günlük içindir.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// ── InstagramAccountResolverDetailedTests (yeni sınıf). Kimlik, token ve kullanıcı adı üretilir. Yardımcılar (imza; gövdeyi uygulayıcı yazar):
// CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler { int Calls } (SendAsync ÖNCE cancellationToken.ThrowIfCancellationRequested() çağırır: gerçek soket
// işleyicisi iptali böyle yükseltir, HttpClient .NET 10'da belirteci kendisi denetlemez; sonra Calls++ ve respond(); respond fırlatırsa istek düşer);
// New(prefix) => $"{prefix}-{Guid.NewGuid():N}"; Reply(HttpStatusCode status, string body); Linked(InstagramAccount) =>
// {"instagram_business_account":{"id":…,"username":…}}; Make(CountingHandler) => çözümleyici (NullLogger);
// Resolve(resolver, pageId) => resolver.ResolveDetailedAsync(pageId, New("t"), CancellationToken.None). ──
[Theory]
[InlineData(HttpStatusCode.OK, "{}", InstagramResolveStatus.NotLinked)]
[InlineData(HttpStatusCode.OK, "{\"instagram_business_account\":{\"id\":\"\"}}", InstagramResolveStatus.NotLinked)]
[InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":190}}", InstagramResolveStatus.Failed)]
[InlineData(HttpStatusCode.OK, "<html>", InstagramResolveStatus.Failed)]
[InlineData(null, "", InstagramResolveStatus.Failed)]                    // reply null: istek ağ hatasıyla düşer
public async Task Hesap_yoksa_NotLinked_cagri_dustuyse_Failed_doner_ikisi_de_onbelleklenmez(HttpStatusCode? reply, string body, InstagramResolveStatus expected)
{
    var handler = new CountingHandler(() => reply is { } code ? Reply(code, body) : throw new HttpRequestException("ağ yok"));
    var (resolver, pageId) = (Make(handler), New("p"));
    for (var call = 1; call <= 2; call++)
    {
        var (account, status) = await Resolve(resolver, pageId);
        account.Should().BeNull(); status.Should().Be(expected); handler.Calls.Should().Be(call);
    }
}
[Fact] public async Task Bagli_hesap_Found_doner_onbellekten_gelir_ResolveAsync_ile_ortaktir_Invalidate_bosaltir()
{
    var expected = new InstagramAccount(New("ig"), New("u"));
    var handler = new CountingHandler(() => Reply(HttpStatusCode.OK, Linked(expected))); var (resolver, pageId) = (Make(handler), New("p"));
    var (first, second) = (await Resolve(resolver, pageId), await Resolve(resolver, pageId));
    first.Account.Should().Be(expected); first.Status.Should().Be(InstagramResolveStatus.Found);
    second.Account.Should().Be(expected); second.Status.Should().Be(InstagramResolveStatus.Found);
    (await resolver.ResolveAsync(pageId, New("t"), CancellationToken.None)).Should().Be(expected);
    handler.Calls.Should().Be(1);                                        // üçü de tek istek: ortak önbellek
    resolver.Invalidate(); await Resolve(resolver, pageId); handler.Calls.Should().Be(2);
    await FluentActions.Awaiting(() => resolver.ResolveDetailedAsync(New("p"), New("t"), new CancellationToken(canceled: true)))
        .Should().ThrowAsync<OperationCanceledException>();              // önbellekte olmayan Sayfa: iptal yükselir
    handler.Calls.Should().Be(2);                                        // iptal edilen çağrı işleyicide sayılmadı
}
// ── InstagramGraphErrorTests'e eklenir (sınıfın İngilizce ad diliyle). NewBusinessId() => $"biz-{Guid.NewGuid():N}" ──
[Fact] public void TryGetUsage_returns_one_entry_per_bucket_in_document_order()
{
    var (first, second) = (NewBusinessId(), NewBusinessId());
    var header =
        $"{{\"{first}\":[{{\"type\":\"instagram\",\"call_count\":28,\"total_cputime\":25,\"total_time\":30,\"estimated_time_to_regain_access\":0}}," +
        $"{{\"type\":\"pages\",\"call_count\":12.5,\"total_cputime\":0.25,\"total_time\":99.9,\"estimated_time_to_regain_access\":3.5}}]," +
        $"\"{second}\":[{{\"type\":\"instagram\",\"call_count\":100,\"total_cputime\":7}}]}}";
    InstagramGraphError.TryGetUsage(header, out var usage).Should().BeTrue();
    usage.Should().Equal(new InstagramBucUsage(first, 28, 25, 30, 0),    // süre alanı varsa aynen (0 dahil)
        new InstagramBucUsage(first, 12.5, 0.25, 99.9, 3.5),             // kesirli; yuvarlanmaz
        new InstagramBucUsage(second, 100, 7, 0, null));                 // eksik oran 0, eksik süre null
}
[Theory, InlineData(null), InlineData(""), InlineData("not json"), InlineData("[]"), InlineData("{}")]
public void TryGetUsage_returns_false_and_an_empty_list_when_no_bucket_can_be_read(string? header)
{ InstagramGraphError.TryGetUsage(header, out var usage).Should().BeFalse(); usage.Should().BeEmpty(); }
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramAccountResolverDetailedTests|FullyQualifiedName~InstagramGraphErrorTests"`
Beklenen: FAIL (derleme hatası: `ResolveDetailedAsync`, `InstagramResolveStatus`, `TryGetUsage`, `InstagramBucUsage` tanımlı değil)

- [ ] **Adım 3: `ResolveDetailedAsync` (`InstagramAccountResolver.cs`) ve `TryGetUsage` (`InstagramGraphError.cs`) uygula**

`ResolveDetailedAsync`: bugünkü `ResolveAsync` gövdesi (`:47-100`) yeni yönteme taşınır ve her çıkış durumuyla döner (önbellek isabeti ve başarı → `Found`; alan yok `:68-76` ve boş `id` `:79` → `NotLinked`; 2xx dışı `:59-65` ve genel `catch` `:96-100` → `Failed`); `ResolveAsync` tek satırla ona devredip `.Account`'u döner; günlük satırları yerinde kalır.
`TryGetUsage`: `TryGetRetryAfter`'ın yürüyüşüyle aynı (kök nesne → anahtar başına dizi → kova nesnesi; nesne ya da dizi olmayan öğe atlanır); sayılar `TryGetDouble` ile okunur; `JsonException` false döner.

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramAccountResolverDetailedTests|FullyQualifiedName~InstagramGraphErrorTests"`
Beklenen: PASS

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramAccountResolverDetailedTests|FullyQualifiedName~InstagramGraphErrorTests|FullyQualifiedName~InstagramAccountResolverTests"`
Beklenen: PASS, 0 uyarı (`InstagramAccountResolverTests` ve `InstagramGraphErrorTests`'in mevcut testleri değiştirilmeden)

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramAccountResolver.cs OrderDeck.Chat/Ingestors/Instagram/InstagramGraphError.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramAccountResolverDetailedTests.cs OrderDeck.Tests/Chat/Instagram/InstagramGraphErrorTests.cs
git commit -F - <<'MSG'
feat(instagram): çözümleyiciye üç durumlu sonucu ve kota başlığı ayrıştırıcısını ekle

"Sayfada Instagram hesabı yok" ile "çağrı düştü" bugün aynı null'dur; sağlık
göstergesi ikisini ayırmak zorunda (çoklu hesap §8.3.4). TryGetUsage kota
başlığını yalnız ölçüm ve günlük için okur; otomatik fren yok.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G06: Poller: gözlemci, kota başlığı, filigran parametresi

**Spec:** §8.3.1 (PR-B satırları hariç); §9.1 "`InstagramLiveCommentsPollerTests` (ek)" ilk dört madde; §10 adım 4; AH4.
**Bağımlı:** yok.

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/IInstagramPollObserver.cs`
- Modify: `OrderDeck.Chat/Ingestors/Instagram/InstagramLiveCommentsPoller.cs:58-64` (alanlar), `:82-96` (kurucu), `:135-217` (`RunAsync`), `:219-226` (`PollKind`, `PollOutcome`), `:228-293` (`PollOnceAsync`) (bugünkü satırlar)
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramLiveCommentsPollerTests.cs` (ek; `ScriptedHandler` `:18-38`, `Make` `:40-42` ve mevcut yedi test DEĞİŞMEZ)

**Interfaces:**
- Consumes: mevcut `InstagramCommentWatermark` (parametresiz kurucu; `Advance(string mediaId, IReadOnlyList<InstagramComment> page)`; içeriği DEĞİŞMEZ).
- Produces: `public enum InstagramPollKind { Ok, NoBroadcast, Transient, RateLimited, Fatal }` (private `PollKind`'ın yerine) · `public interface IInstagramPollObserver { void RoundStarting(); void RoundCompleted(InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader); }` · `public InstagramLiveCommentsPoller(string igUserId, string pageAccessToken, IChatBus bus, HttpClient http, ILogger<InstagramLiveCommentsPoller> log, SpamFilter? spamFilter = null, IInstagramPollObserver? observer = null, InstagramCommentWatermark? watermark = null);`
- Çağrı sözleşmesi (Produces): her turda bir `RoundStarting`, ardından en çok bir `RoundCompleted` (Ok'te o turun yayımlarından SONRA; Ok dışında beklemeden ÖNCE); `message` yalnız Fatal'da, `retryAfter` yalnız RateLimited'da dolu; `usageHeader` yanıtın `X-Business-Use-Case-Usage` başlığının ham metni ya da null.

**Kurallar:**
- PR-A sınırı: `IInstagramPollPacer` arayüzü ve `pacer` parametresi YOK (spec §8.3.1 kod bloğundaki iki "// PR-B" satırı uygulanmaz); kurucu `watermark` ile biter.
- Döngü, aralıklar (`:32-41`, `:123-133`), ayrıştırıcı, hata sınıflandırma, `Publish` (`:295-323`), bütün beklemeler ve mevcut günlük satırları aynen; `RunAsync`'in ilk satırı olan `[InstagramLiveCommentsPoller] polling live_media for IG user {IgUserId}` (Information, `:142-143`) metniyle kalır: G10–G12 testleri poller kurulumlarını bu satırı sayarak ölçer. Yeni parametreler verilmezse davranış bugünküyle özdeş; `InstagramChatHostedService.cs:144-150` altı argümanla derlenmeye devam eder.
- `RoundStarting` her turda `PollOnceAsync`'ten (`:152`) önce. Ok dışı sonuçta `RoundCompleted` `switch`'ten önce (bekleme ve `return`'den önce); Ok'te `Publish` döngüsünden (`:198-199`) sonra, `NextInterval`'dan önce.
- `message` = Fatal sonucun Türkçe mesajı (`FatalReason` ile aynı metin), öbür türlerde null. `retryAfter` = kota beklemesi, öbür türlerde `TimeSpan.Zero`. `usageHeader` HER yanıtta taşınır (bugün yalnız kota hatasında okunuyor, `:251`); başlık yoksa ya da istek düştüyse null; birden çok başlık değeri `,` ile birleştirilir (karar). Poller başlığı kota beklemesi dışında ayrıştırmaz; kota beklemesi birleşik metinden DEĞİL, bugünkü gibi başlık değerleri tek tek denenerek okunur (`:251-259` döngüsü aynen: birleşik metin birden çok değerde geçerli JSON olmaz, bekleme başlıktaki süre yerine 1 dakikaya düşerdi).
- Gözlemcinin fırlattığı her istisna yutulur ve Debug düzeyinde loglanır (Warning değil: saniyede bir tur); döngü sürer. `watermark` verilirse o örnek kullanılır, verilmezse bugünkü gibi yeni bir filigran.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// InstagramLiveCommentsPollerTests'e eklenir (yeni using'ler: System.Collections.Concurrent, Microsoft.Extensions.Logging, OrderDeck.Tests.TestHelpers).
// Yeni testlerde kimlik, token ve izleyici adı üretilir. Yardımcılar (gövdesi yazılmamış olanlar imzadır; gövdeyi uygulayıcı yazar):
private sealed record Round(InstagramPollKind Kind, string? Message, TimeSpan RetryAfter, string? UsageHeader);
/// <summary>Log'a "start" / "done:{Kind}", Completed'a tur sonucu yazar; Throws true ise iki yöntem de kayıttan sonra InvalidOperationException fırlatır.</summary>
private sealed class RecordingObserver(ConcurrentQueue<string>? log = null) : IInstagramPollObserver
{
    public ConcurrentQueue<string> Log { get; } = log ?? new();
    public ConcurrentQueue<Round> Completed { get; } = new();
    public bool Throws { get; init; }
}
/// <summary>ScriptedHandler'ın başlık taşıyan eşi: sıradaki yanıtı döner, bitince sonuncuyu yineler; Usage null ise X-Business-Use-Case-Usage eklenmez.</summary>
private sealed class UsageHandler : HttpMessageHandler { public UsageHandler(params (HttpStatusCode Status, string Body, string? Usage)[] responses); }
private static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
private static string UsageHeader(int minutes);                          // tek kovalı başlık; business-id üretilir; minutes = estimated_time_to_regain_access
private static string MediaJsonFor(string mediaId, params string[] comments);            // MediaJson'ın media kimliği verilen eşi
private static ScriptedHandler PrimeThenNew(string oldId, string newId, string user);     // 1. çekim: eski yorum (12:00:00); sonrakiler: yeni (12:00:05) + eski
private static InstagramLiveCommentsPoller MakeObserved(HttpMessageHandler handler, IChatBus bus, IInstagramPollObserver? observer = null,
    InstagramCommentWatermark? watermark = null, ILogger<InstagramLiveCommentsPoller>? log = null);   // kimliği ve token'ı üretilmiş poller
private static Task RunUntil(InstagramLiveCommentsPoller poller, Func<bool> until);      // başlat; mevcut WaitUntil(until, TestPatience.Upper); durdur

[Fact] public async Task Observer_sees_start_then_completion_and_ok_arrives_after_the_publishes()
{
    var order = new ConcurrentQueue<string>(); var bus = new ChatBus(ringBufferSize: 50);
    using var sub = bus.Subscribe(_ => order.Enqueue("pub"));
    using var poller = MakeObserved(PrimeThenNew(New("c"), New("c"), New("u")), bus, new RecordingObserver(order));
    await RunUntil(poller, () => order.Count >= 5);
    order.Take(5).Should().Equal("start", "done:Ok", "start", "pub", "done:Ok");         // 1. tur ilk çekim: yayım yok
}
[Theory]
[InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":190}}", InstagramPollKind.Fatal, "Instagram bağlantını yenilemen gerekiyor.")]
[InlineData(HttpStatusCode.OK, "{\"data\":[]}", InstagramPollKind.NoBroadcast, null)]
public async Task Observer_gets_fatal_with_the_operator_message_and_no_broadcast_on_empty_data(HttpStatusCode status, string body, InstagramPollKind kind, string? message)
{
    var observer = new RecordingObserver();
    using var poller = MakeObserved(new ScriptedHandler((status, body)), new ChatBus(), observer);
    await RunUntil(poller, () => !observer.Completed.IsEmpty);
    observer.Log.Take(2).Should().Equal("start", $"done:{kind}");
    observer.Completed.First().Should().Be(new Round(kind, message, TimeSpan.Zero, null));
}
[Fact] public async Task Observer_gets_rate_limited_with_the_wait_before_the_poller_sleeps()
{
    var observer = new RecordingObserver(); var header = UsageHeader(minutes: 7);
    var handler = new UsageHandler((HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":4}}", header));
    using var poller = MakeObserved(handler, new ChatBus(), observer);
    await RunUntil(poller, () => !observer.Completed.IsEmpty);           // bildirim 7 dk'lık beklemeden önce gelir
    observer.Completed.First().Should().Be(new Round(InstagramPollKind.RateLimited, null, TimeSpan.FromMinutes(7), header));
}
[Fact] public async Task Usage_header_is_carried_raw_on_every_response()
{
    var observer = new RecordingObserver(); var header = UsageHeader(minutes: 0);
    var body = MediaJson(Comment(New("c"), "x", "2026-08-05T12:00:00+0000", New("u")));
    using var poller = MakeObserved(new UsageHandler((HttpStatusCode.OK, body, header), (HttpStatusCode.OK, body, null)), new ChatBus(), observer);
    await RunUntil(poller, () => observer.Completed.Count >= 2);
    observer.Completed.Take(2).Should().Equal(new Round(InstagramPollKind.Ok, null, TimeSpan.Zero, header),
        new Round(InstagramPollKind.Ok, null, TimeSpan.Zero, null));                     // başlık yoksa null
}
[Fact] public async Task Throwing_observer_does_not_stop_the_loop()
{
    var log = new CapturingLogger<InstagramLiveCommentsPoller>(); var bus = new ChatBus(ringBufferSize: 50);
    var received = new ConcurrentQueue<ChatMessage>(); using var sub = bus.Subscribe(received.Enqueue);
    var newId = New("c");
    using var poller = MakeObserved(PrimeThenNew(New("c"), newId, New("u")), bus, new RecordingObserver { Throws = true }, log: log);
    await RunUntil(poller, () => !received.IsEmpty);
    received.Should().ContainSingle().Which.ExternalId.Should().Be(newId);               // iki tur da tamamlandı
    log.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Exception != null);
    log.Warnings.Should().BeEmpty(); poller.FatalReason.Should().BeNull();
}
[Fact] public async Task Handed_over_watermark_publishes_the_first_fetch()
{
    var (media, oldId, newId, user) = (New("m"), New("c"), New("c"), New("u"));
    var at = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    var watermark = new InstagramCommentWatermark();                     // önceki poller'ın filigranı
    watermark.Advance(media, new[] { new InstagramComment(oldId, "eski", at, user) });
    var bus = new ChatBus(ringBufferSize: 50); var received = new ConcurrentQueue<ChatMessage>(); using var sub = bus.Subscribe(received.Enqueue);
    var handler = new ScriptedHandler((HttpStatusCode.OK, MediaJsonFor(media,
        Comment(newId, "yeni", "2026-08-05T12:00:05+0000", user), Comment(oldId, "eski", "2026-08-05T12:00:00+0000", user))));
    using var poller = MakeObserved(handler, bus, watermark: watermark);
    await RunUntil(poller, () => !received.IsEmpty);
    received.Should().ContainSingle().Which.ExternalId.Should().Be(newId);               // İLK çekimde basıldı, yutulmadı
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramLiveCommentsPollerTests"`
Beklenen: FAIL (derleme hatası: `IInstagramPollObserver`, `InstagramPollKind` tanımlı değil; kurucuda `observer` / `watermark` parametresi yok)

- [ ] **Adım 3: `InstagramPollKind` + `IInstagramPollObserver` ekle (`IInstagramPollObserver.cs`) ve poller'ı güncelle (`InstagramLiveCommentsPoller.cs`)**

Yeni dosyada yalnız bu iki tip durur (ad alanı `OrderDeck.Chat.Ingestors.Instagram`). Poller'da private `PollKind` silinir, her kullanım `InstagramPollKind` olur; kurucunun sonuna iki parametre eklenir ve `_watermark` alan başlatıcısı yerine kurucuda atanır (`watermark ?? new()`). `PollOutcome`'a isteğe bağlı `string? UsageHeader = null` eklenir: `PollOnceAsync` yanıtı alır almaz başlığı bir kez okur ve `,` ile birleştirilmiş ham metni o yanıttan kurduğu HER sonuca koyar (metin yalnız gözlemciye gider); kota dalındaki değer başına `TryGetRetryAfter` döngüsü (`:251-259`) AYNEN kalır. `RunAsync`'teki üç gözlemci çağrısı, istisnayı yutup Debug yazan tek bir özel yardımcıdan geçer (ileti öneki bugünkü `[InstagramLiveCommentsPoller]`).

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramLiveCommentsPollerTests"`
Beklenen: PASS (mevcut yedi test + altı yeni test yöntemi)

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramLiveCommentsPollerTests|FullyQualifiedName~InstagramChatHostedServiceTests|FullyQualifiedName~InstagramCommentWatermarkTests"`
Beklenen: PASS, 0 uyarı (`InstagramChatHostedService.cs` değişmeden derlenir)

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/IInstagramPollObserver.cs OrderDeck.Chat/Ingestors/Instagram/InstagramLiveCommentsPoller.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramLiveCommentsPollerTests.cs
git commit -F - <<'MSG'
feat(instagram): poller'a tur gözlemcisini, kota başlığını ve filigran parametresini ekle

Hesap başına sağlık ve kota ölçümü turun sonucunu poller'ın dışından görmek
zorunda; tekten çoka geçişte filigran yeni poller'a devredilecek (çoklu hesap
§8.3.1). Parametreler verilmezse döngü bugünküyle özdeştir; pacer PR-B'dedir.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G07: `InstagramHealthMonitor`

**Spec:** §8.5 "Sağlık izleyicisi" ve "Geçişler" tablosu; §9.1 `InstagramHealthMonitorTests`; §10 adım 5; AH3.
**Bağımlı:** G01, G06.

**Files:**
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramHealthMonitor.cs`
- Test: `OrderDeck.Tests/TestHelpers/ManualTime.cs` (yeni), `OrderDeck.Tests/Chat/Instagram/InstagramHealthMonitorTests.cs` (yeni)

**Interfaces:**
- Consumes: G01 `public sealed record FacebookLinkedPage(string PageId, string PageAccessToken, string PageName)` (`OrderDeck.Chat.Facebook`); G06 `public enum InstagramPollKind { Ok, NoBroadcast, Transient, RateLimited, Fatal }`, `public interface IInstagramPollObserver { void RoundStarting(); void RoundCompleted(InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader); }`.
- Produces (üçü de `InstagramHealthMonitor.cs`'te, ad alanı `OrderDeck.Chat.Ingestors.Instagram`):
  - `public enum InstagramAccountState { Stopped, Starting, NotLinked, ResolveFailed, Skipped, WaitingForBroadcast, Live, BroadcastEnded, RateLimited, Faulted }`
  - `public sealed record InstagramAccountHealth(string PageId, string PageName, bool IsPrimary, string? Username, InstagramAccountState State, string? Detail, DateTimeOffset Since, DateTimeOffset? LastContactAt, DateTimeOffset? RetryAt, bool WasLiveThisSession);`
  - `public sealed class InstagramHealthMonitor { public InstagramHealthMonitor(TimeProvider? time = null); public void Configure(IReadOnlyList<FacebookLinkedPage> pages); public void ResetSession(); public void StopAll(); public void Clear(); public void SetAccount(string pageId, string? username); public void Report(string pageId, InstagramAccountState state, string? detail = null, DateTimeOffset? retryAt = null); public IInstagramPollObserver ObserverFor(string pageId); public IReadOnlyList<InstagramAccountHealth> Snapshot(); }` (çağıran için: geç gelen tur olayı `Stopped` girdide de işlenir; `StopAll`'dan önce poller'lar durdurulur)
  - (karar) `internal sealed class ManualTime : TimeProvider { public void Advance(TimeSpan by); }` (`OrderDeck.Tests.TestHelpers`; başlangıç anı sabit; `CreateTimer` ezilmez, yani `Task.Delay(süre, time, ct)` GERÇEK süre bekler). G09–G13 ve G15 de kullanır.

**Kurallar:**
- Bütün üyeler tek kilit altındadır; izleyici log YAZMAZ (`ILogger` almaz). "Şimdi" = `(time ?? TimeProvider.System).GetUtcNow()`. Bilinmeyen `pageId` her üyede yok sayılır, fırlatmaz (karar).
- `Since` yalnız `State` değişince "şimdi" olur (aynı durumu yineleyen bildirim değiştirmez); tek istisna `ResetSession` (hep "şimdi").
- `Configure(pages)`: sıra, `IsPrimary` (ilk girdi) ve `PageName` her çağrıda listeden yazılır; listede olmayan girdi silinir; yeni girdi `Stopped`, `Since` = şimdi, öbür alanları boş; kalan girdinin öbür alanlarına dokunulmaz. `Snapshot()` bu sırada, o anın kopyasıdır.
- `ResetSession()`: her girdi `Stopped`, `Since` = şimdi; `WasLiveThisSession`, `LastContactAt`, `RetryAt`, `Detail` temizlenir; `Username` kalır. `StopAll()`: her girdi `Stopped` olur; durumu değişen girdide `Since` genel kural gereği şimdi olur, başka HİÇBİR alan değişmez (karar: `WasLiveThisSession`'ın yanında `LastContactAt`, `Detail`, `RetryAt` da kalır). `Clear()`: bütün girdiler silinir.
- `Report(pageId, state, detail, retryAt)`: `State` yazılır; `Detail` ve `RetryAt` HER çağrıda verilen değerle yazılır (verilmediyse temizlenir). (karar) `state == Starting` yalnız girdi `Stopped` ise uygulanır; değilse çağrı tümüyle yok sayılır (poller yeniden kurulurken durum değişmez).
- `SetAccount(pageId, username)` = "çözümleme `Found`" olayı (karar): `Username` yazılır (null dahil); girdi `NotLinked` / `ResolveFailed` / `Skipped` ise ayrıca `Starting` olur, `Detail` ve `RetryAt` temizlenir; öbür durumlarda başka alan değişmez.
- `ObserverFor(pageId)` gözlemcisi §8.5 "Geçişler" tablosunun tur satırlarını uygular: Ok → `Live`, `WasLiveThisSession = true`, `LastContactAt` = şimdi; NoBroadcast → `LastContactAt` = şimdi, `WasLiveThisSession` ise `BroadcastEnded`, değilse `WaitingForBroadcast`; RateLimited → `RateLimited`, `RetryAt` = şimdi + `retryAfter`, `LastContactAt` = şimdi; Fatal → `Faulted`, `Detail = message` (`LastContactAt` değişmez; `RetryAt`'i sonradan çağıran `Report` ile yazar); Transient ve `RoundStarting` → hiçbir alan değişmez. Transient dışındaki tur olayları `Detail` ve `RetryAt`'i de yeniden yazar (vermediğini temizler); `usageHeader` kullanılmaz.

- [ ] **Adım 1: `ManualTime`'ı ekle ve düşen testleri yaz**

`OrderDeck.Tests/TestHelpers/ManualTime.cs`: `OrderDeck.Tests/Sync/SessionOrderSyncCadenceTests.cs:41-48`'deki iç sınıfın gövdesi AYNEN (başlangıç `TimeSpan.FromDays(1).Ticks`; `TimestampFrequency`, `GetTimestamp`, `GetUtcNow` ezilir; `Advance(TimeSpan by)`), üst düzey `internal sealed class`, ad alanı `OrderDeck.Tests.TestHelpers`. `SessionOrderSyncCadenceTests` ve `SessionOrderSyncHandoverTests` içindeki aynı adlı iç sınıflara DOKUNULMAZ (iç tip önceliklidir, çakışmaz).

```csharp
// InstagramHealthMonitorTests (yeni). Alanlar: ManualTime _time = new(); InstagramHealthMonitor _monitor = new(_time). Kimlik, token ve ad üretilir.
// Yardımcılardan gövdesi yazılmamış olanlar imzadır (gövdeyi uygulayıcı yazar).
using State = OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;
using Kind = OrderDeck.Chat.Ingestors.Instagram.InstagramPollKind;
private DateTimeOffset Now => _time.GetUtcNow();
private void Tick(double seconds);                                       // _time.Advance
private static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
private static FacebookLinkedPage NewPage() => new(New("p"), New("t"), New("Sayfa"));
private FacebookLinkedPage[] Configure(int n);                           // n Sayfa üretir, _monitor.Configure'a verir; ilki birincil
private string Started();                                                // tek Sayfa yapılandırır + Report(Starting); PageId döner
private InstagramAccountHealth Entry(string pageId);                     // Snapshot().Single(h => h.PageId == pageId)
private InstagramAccountHealth Fresh(FacebookLinkedPage page, bool primary)                             // yeni eklenmiş girdi
    => new(page.PageId, page.PageName, primary, null, State.Stopped, null, Now, null, null, false);
private void Round(string pageId, Kind kind, string? message = null, TimeSpan retryAfter = default);   // ObserverFor(pageId).RoundCompleted(kind, message, retryAfter, null)

[Fact] public void Configure_eksigi_Stopped_ekler_fazlayi_siler_kalanin_durumuna_dokunmaz_ilk_girdi_birincildir()
{
    var pages = Configure(2);
    _monitor.Snapshot().Should().Equal(Fresh(pages[0], primary: true), Fresh(pages[1], primary: false));
    _monitor.Report(pages[1].PageId, State.Starting); Round(pages[1].PageId, Kind.Ok);
    var kept = Entry(pages[1].PageId); Tick(7); var added = NewPage();
    _monitor.Configure(new[] { pages[1], added });                       // pages[0] çıktı, sıra değişti, yeni Sayfa girdi
    _monitor.Snapshot().Should().Equal(kept with { IsPrimary = true }, Fresh(added, primary: false));
}
[Fact] public void Report_Starting_yalniz_Stopped_girdide_etkilidir()
{
    var id = Configure(1)[0].PageId; Tick(3);
    _monitor.Report(id, State.Starting);
    Entry(id).Should().BeEquivalentTo(new { State = State.Starting, Since = Now });
    Round(id, Kind.Ok); var live = Entry(id); Tick(3);
    _monitor.Report(id, State.Starting);                                 // poller yeniden kurulurken
    Entry(id).Should().Be(live);
}
[Fact] public void Tur_Ok_Live_yapar_NoBroadcast_canliliga_gore_ayrilir_Since_yalniz_durum_degisince_guncellenir()
{
    var id = Started();
    Round(id, Kind.NoBroadcast);                                         // henüz canlı olmadı
    Entry(id).Should().BeEquivalentTo(new { State = State.WaitingForBroadcast, LastContactAt = Now, WasLiveThisSession = false });
    Tick(10); Round(id, Kind.Ok); var liveAt = Now; Tick(30); Round(id, Kind.Ok);          // aynı durum: Since kalır, temas yenilenir
    Entry(id).Should().BeEquivalentTo(new { State = State.Live, Since = liveAt, LastContactAt = Now, WasLiveThisSession = true });
    var before = Entry(id); Tick(5);
    _monitor.ObserverFor(id).RoundStarting(); Round(id, Kind.Transient);                   // hiçbir alan değişmez
    Entry(id).Should().Be(before);
    Round(id, Kind.NoBroadcast);                                         // BroadcastEnded yalnız canlıdan sonra
    Entry(id).Should().BeEquivalentTo(new { State = State.BroadcastEnded, Since = Now, LastContactAt = Now, WasLiveThisSession = true });
}
[Fact] public void Tur_RateLimited_RetryAt_Fatal_Detail_yazar_ayni_durumu_yineleyen_Report_Since_i_degistirmez()
{
    var id = Started(); const string reason = "Instagram bağlantını yenilemen gerekiyor.";
    Round(id, Kind.RateLimited, retryAfter: TimeSpan.FromMinutes(7));
    Entry(id).Should().BeEquivalentTo(new { State = State.RateLimited, RetryAt = Now.AddMinutes(7), LastContactAt = Now });
    Tick(1); Round(id, Kind.Fatal, reason); var faultedAt = Now;         // tur olayı vermediği RetryAt'i temizler
    Entry(id).Should().BeEquivalentTo(new { State = State.Faulted, Since = faultedAt, Detail = reason, RetryAt = (DateTimeOffset?)null });
    Tick(1); _monitor.Report(id, State.Faulted, reason, Now.AddMinutes(5));               // çağıran RetryAt'i yazar
    Entry(id).Should().BeEquivalentTo(new { Since = faultedAt, Detail = reason, RetryAt = Now.AddMinutes(5) });
}
[Theory, InlineData(State.NotLinked), InlineData(State.ResolveFailed), InlineData(State.Skipped)]
public void Cozumleme_sonucu_Report_ile_yazilir_Found_gelince_SetAccount_Starting_e_alir(State state)
{
    var id = Started(); var username = New("u");
    _monitor.Report(id, state, detail: "sebep", retryAt: Now.AddMinutes(5));
    Entry(id).Should().BeEquivalentTo(new { State = state, Detail = "sebep", RetryAt = Now.AddMinutes(5) });
    Tick(300); _monitor.SetAccount(id, username);
    Entry(id).Should().BeEquivalentTo(new { State = State.Starting, Since = Now, Username = username, Detail = (string?)null, RetryAt = (DateTimeOffset?)null });
}
[Fact] public void SetAccount_oteki_durumlarda_yalniz_kullanici_adini_yazar()
{
    var id = Started(); Round(id, Kind.Ok); var before = Entry(id); var username = New("u");
    _monitor.SetAccount(id, username); Entry(id).Should().Be(before with { Username = username });
    _monitor.SetAccount(id, null); Entry(id).Should().Be(before);
}
[Fact] public void StopAll_silmeden_Stopped_yapar_oteki_alanlara_dokunmaz()
{
    var id = Configure(2)[0].PageId;
    _monitor.Report(id, State.Starting); Round(id, Kind.Ok); _monitor.Report(id, State.Faulted, "sebep", Now.AddMinutes(5));
    var before = Entry(id); Tick(20); _monitor.StopAll();
    _monitor.Snapshot().Should().HaveCount(2).And.OnlyContain(h => h.State == State.Stopped);
    Entry(id).Should().Be(before with { State = State.Stopped, Since = Now });            // canlıydı bilgisi, son temas, Detail, RetryAt aynen
    _monitor.Report(id, State.Starting);                                 // liste oturum içinde yeniden doldu; Report vermediği alanı temizler
    Entry(id).Should().Be(before with { State = State.Starting, Since = Now, Detail = null, RetryAt = null });
}
[Fact] public void ResetSession_girdileri_Stopped_yapar_ve_dort_oturum_alanini_temizler()
{
    var id = Started(); var username = New("u");
    _monitor.SetAccount(id, username); Round(id, Kind.Ok); _monitor.Report(id, State.Faulted, "sebep", Now.AddMinutes(5));
    var before = Entry(id); Tick(60); _monitor.ResetSession();
    Entry(id).Should().Be(before with { State = State.Stopped, Since = Now, WasLiveThisSession = false, LastContactAt = null, RetryAt = null, Detail = null });
}
[Fact] public void Snapshot_kopyadir_Clear_siler_bilinmeyen_Sayfa_yok_sayilir()
{
    var id = Configure(2)[0].PageId; var first = _monitor.Snapshot();
    _monitor.Report(id, State.Starting);
    first.Should().OnlyContain(h => h.State == State.Stopped);           // eski anlık görüntü değişmedi
    _monitor.Clear();
    _monitor.Report(id, State.Starting); _monitor.SetAccount(id, New("u")); Round(id, Kind.Ok);
    _monitor.Snapshot().Should().BeEmpty();
}
[Fact] public void Eszamanli_yazimda_girdi_kaybolmaz_istisna_cikmaz()
{
    var pages = Configure(4);
    Parallel.For(0, 4000, i =>
    {
        var id = pages[i % 4].PageId; _monitor.Report(id, State.Starting); Round(id, Kind.Ok); _monitor.SetAccount(id, null);
        _monitor.Snapshot().Should().HaveCount(4); if (i % 50 == 0) _monitor.Configure(pages);
    });
    _monitor.Snapshot().Should().OnlyContain(h => h.WasLiveThisSession);
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramHealthMonitorTests"`
Beklenen: FAIL (derleme hatası: `InstagramHealthMonitor`, `InstagramAccountState`, `InstagramAccountHealth` tanımlı değil)

- [ ] **Adım 3: `InstagramAccountState`, `InstagramAccountHealth` ve `InstagramHealthMonitor`'ı uygula (`InstagramHealthMonitor.cs`)**

Girdiler tek kilit altında, `Configure` sırasını koruyan bir listede değişmez kayıt olarak tutulur; her değişiklik `with` ile yeni kayıt yazar, `Snapshot()` listeyi kopyalar. `ObserverFor` izleyiciye ve `pageId`'ye bağlı özel bir iç sınıf döner (girdi o an yoksa da döner).

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramHealthMonitorTests"`
Beklenen: PASS

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramHealthMonitorTests|FullyQualifiedName~SessionOrderSyncCadenceTests|FullyQualifiedName~SessionOrderSyncHandoverTests"`
Beklenen: PASS, 0 uyarı (yeni üst düzey `ManualTime`, iki mevcut iç `ManualTime` ile çakışmadan derlenir)

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramHealthMonitor.cs OrderDeck.Tests/TestHelpers/ManualTime.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramHealthMonitorTests.cs
git commit -F - <<'MSG'
feat(instagram): hesap başına sağlık izleyicisini ekle

Bugün Instagram API yolunun ana ekranda hiçbir sağlık göstergesi yok. İzleyici
Sayfa başına durumu, son teması ve yeniden deneme saatini tutar; yorumu saf
sunucuya bırakır ve log yazmaz (çoklu hesap §8.5). Ortak ManualTime eklenir.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G08: `InstagramHealthPresenter` (saf görünüm kuralları)

**Spec:** §8.5 "Görünüm kuralları" (tamamı); §8.6.3 (görünürlük, durum / metin / ipucu tablosu); §8.6.4 (üst nokta ipucu biçimleri); §9.4 `InstagramHealthPresenterTests`; TK9, TK10, TK20; R8, R16.
**Bağımlı:** G07.

**Files:**
- Create: `OrderDeck.App/Services/Instagram/InstagramHealthPresenter.cs`
- Create: `OrderDeck.App/ViewModels/InstagramAccountStatusViewModel.cs`
- Test: `OrderDeck.Tests/Services/Instagram/InstagramHealthPresenterTests.cs` (yeni)

**Interfaces:**
- Consumes: G07 `public sealed record InstagramAccountHealth(string PageId, string PageName, bool IsPrimary, string? Username, InstagramAccountState State, string? Detail, DateTimeOffset Since, DateTimeOffset? LastContactAt, DateTimeOffset? RetryAt, bool WasLiveThisSession);`, `public enum InstagramAccountState { Stopped, Starting, NotLinked, ResolveFailed, Skipped, WaitingForBroadcast, Live, BroadcastEnded, RateLimited, Faulted }`; mevcut `internal static string PaymentResultPresenter.Locative(DateTimeOffset at)` (`OrderDeck.App/Services/PaymentResultPresenter.cs:252`; DEĞİŞMEZ).
- Produces:
  - `public enum InstagramStatusSeverity { Live, Neutral, Warning }` (yeşil / soluk / sarı) ve `public sealed record InstagramHealthView(IReadOnlyList<InstagramAccountStatusViewModel> Rows, bool ShowStrip, bool AnyAccountLive, string? TopDotTooltip);` (`TopDotTooltip` null = üst noktayı sarartan durum yok): `InstagramHealthPresenter.cs`'te, ad alanı `OrderDeck.App.Services.Instagram`.
  - `public sealed record InstagramAccountStatusViewModel(string PageId, string Label, string StateText, string Text, InstagramStatusSeverity Severity, string? Tooltip);` (ad alanı `OrderDeck.App.ViewModels`; `StateText` = etiketsiz durum metni, ör. `yanıt yok (18 sn)`; `Text` = şeritte görünen tam metin; `Tooltip` null = ipucu yok).
  - `public static class InstagramHealthPresenter { public static InstagramHealthView Evaluate(IReadOnlyList<InstagramAccountHealth> snapshot, DateTimeOffset now, bool sessionActive); }` (çağıran yerel saatli `now` verir; `sessionActive` yalnız `ShowStrip`'i kapılar).

**Kurallar:**
- Saf ve durumsuz: saat okumaz, log yazmaz. Kuralların gerekçeli metni §8.5 "Görünüm kuralları"ndadır; metinler §8.6.3 tablosundan ve §8.6.4'ten BİREBİR alınır (Adım 1'deki literal'ler bağlayıcıdır).
- Eşikler adlandırılmış sabittir: canlıyken yanıtsızlık 10 sn; beklerken 45 sn; `RetryAt` aşımı 45 sn; "canlı yayın bulunamadı" 90 sn; "yayın kesildi"nin üst noktadaki ömrü 2 dk. Bütün eşiklerde aşım `>`'dir (eşiğin kendisi aşım değildir, karar).
- Bağlı Sayfa sayısı = `snapshot.Count` (durumdan bağımsız). Satır üretmeyenler: `Stopped`, `Skipped`; ayrıca Sayfa sayısı 1 iken ve `WasLiveThisSession` false iken `NotLinked` (bu gizleme "yanıt yok" türetmesinden ÖNCE uygulanır, karar).
- Yanıtsız: `Live` iken sessizlik > 10 sn; `WaitingForBroadcast` / `BroadcastEnded` / `Starting` iken > 45 sn (sessizlik = `now` − `LastContactAt` ile `Since`'ten geç olanı; temas yoksa `Since`). `RateLimited` / `Faulted` / `ResolveFailed` / `NotLinked` iken `RetryAt` dolu ve `now − RetryAt` > 45 sn. Yanıtsız girdinin satırı taban durumunki değil `yanıt yok (N sn)`'dir; N tam saniyedir: ilk iki grupta sessizlik, `RetryAt` grubunda `now − RetryAt` (karar).
- Canlı = `Live` ve yanıtsız değil; satırı `canlı` (`Live`), ipucundaki saniye son temastan beri geçen tam saniyedir. `AnyAccountLive` = en az bir canlı girdi.
- Sorun durumları (yanıt yok, hız sınırı, kalıcı hata, hesap bilgisi okunamadı, Instagram hesabı bağlı değil): Sayfa sayısı 2 ve üzeriyse `Warning`; 1 ise yalnız `WasLiveThisSession` iken `Warning`, değilse aynı metinle `Neutral` (TK9).
- `WaitingForBroadcast`: başka bir canlı girdi varsa ve `now` − (bu girdinin `Since`'i ile canlı girdilerin en erken `Since`'inden geç olanı) > 90 sn ise `canlı yayın bulunamadı` (`Warning`); değilse `yayın bekleniyor` (`Neutral`). `BroadcastEnded`: başka bir canlı girdi varsa `yayın kesildi (SS:DD)` (`Warning`), yoksa `yayın bitti (SS:DD)` (`Neutral`); saat girdinin `Since`'idir. `Starting` → `bağlanıyor` (`Neutral`).
- Etiket ve metin: `Label` = `@` + `Username`, kullanıcı adı boşsa `PageName`; `Text` = `{Label} {StateText}`. "hesap bilgisi okunamadı" ve "Instagram hesabı bağlı değil" satırlarında kullanıcı adı bilinse de `Label` = `PageName` ve `Text` = `{Label}: {StateText}` (§8.6.3 tablosu bu iki satırı `{Sayfa adı}: …` yazar); ikincisinin ipucu `IsPrimary`'ye göre iki metinden biridir.
- Saat (`SS:DD`): zaman `now.Offset`'e çevrilip `HH:mm` (değişmez kültür) yazılır (karar; Review Focus 4). `RetryAt` null ise metnin ve ipucunun saat parçası yazılmaz (karar). Kalıcı hata ipucu = `Detail` + boşluk + sabit cümle + (saat varsa) boşluk + saat cümlesi; `Detail` boşsa sabit cümleyle başlar.
- `ShowStrip` = `sessionActive` VE (satır sayısı ≥ 2 VEYA `Warning` satır var). (karar) `Rows`, `AnyAccountLive` ve `TopDotTooltip` oturumdan bağımsız hesaplanır.
- `TopDotTooltip` (TK20): `Warning` satırlardan `canlı yayın bulunamadı` olanlar ve `Since`'i 2 dakikadan eski `yayın kesildi` satırları dışındakilerle kurulur. Kalan yoksa null; biri varsa `Instagram {Label}: {StateText}.`; birden çoksa `Instagram: N hesapta sorun ({Label}, {Label}).` (anlık görüntü sırasıyla).

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// InstagramHealthPresenterTests (yeni). Kimlik, Sayfa adı ve kullanıcı adı üretilir; arayüz metinleri literal'dir. Yardımcılar:
using State = OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;
using Severity = OrderDeck.App.Services.Instagram.InstagramStatusSeverity;
private static readonly DateTimeOffset Now = new(2026, 11, 17, 21, 14, 7, TimeSpan.FromHours(3));   // Review Focus 4: +03:00; girdilerin saatleri UTC
private const string Fatal = "Instagram bağlantını yenilemen gerekiyor.";
private const string Reauth = "Ayarlar > Facebook > \"Facebook izinlerini güncelle\".";
private const string NoAnswer = "Instagram'dan yanıt alınamıyor, yeniden deneniyor. Yorumlar gecikiyor olabilir.";
// H imzadır (gövdeyi uygulayıcı yazar): girdi kurar; named false ise Username null. Süreler saniyedir: sinceAgo ve contactAgo Now'dan geriye,
// retryIn Now'dan ileriye (eksi = geçmiş); saat alanları UTC'ye çevrilir.
private static InstagramAccountHealth H(State state, double sinceAgo = 30, double? contactAgo = null, double? retryIn = null,
    bool wasLive = false, bool primary = true, string? detail = null, bool named = true);
private static InstagramAccountHealth Healthy(double sinceAgo = 300, bool primary = true) => H(State.Live, sinceAgo, contactAgo: 1, wasLive: true, primary: primary);
private static InstagramHealthView Eval(params InstagramAccountHealth[] snapshot) => InstagramHealthPresenter.Evaluate(snapshot, Now, sessionActive: true);
private static InstagramAccountStatusViewModel Row(params InstagramAccountHealth[] snapshot) => Eval(snapshot).Rows.Single();
private static InstagramAccountStatusViewModel At(InstagramAccountHealth h, string state, Severity severity, string? tip)        // beklenen satır, `@ad` etiketli
    => new(h.PageId, $"@{h.Username}", state, $"@{h.Username} {state}", severity, tip);
private static InstagramAccountStatusViewModel Paged(InstagramAccountHealth h, string state, Severity severity, string? tip)     // beklenen satır, Sayfa adı etiketli
    => new(h.PageId, h.PageName, state, $"{h.PageName}: {state}", severity, tip);

[Fact] public void Canli_bekleyen_baglanan_ve_yayini_biten_hesabin_satiri()
{
    var (live, waiting) = (Healthy(), H(State.WaitingForBroadcast, contactAgo: 5));
    var (starting, ended) = (H(State.Starting, sinceAgo: 5), H(State.BroadcastEnded, sinceAgo: 240, contactAgo: 5, wasLive: true));
    Row(live).Should().Be(At(live, "canlı", Severity.Live, "Son yanıt 1 sn önce"));
    Row(waiting).Should().Be(At(waiting, "yayın bekleniyor", Severity.Neutral, "Bu hesapta canlı yayın açılınca yorumlar akmaya başlar."));
    Row(starting).Should().Be(At(starting, "bağlanıyor", Severity.Neutral, null));
    Row(ended).Should().Be(At(ended, "yayın bitti (21:10)", Severity.Neutral, null));
}
[Fact] public void Sorun_durumlarinin_satiri_ve_ipucu()                  // tek Sayfa + bu oturumda canlıydı → sarı
{
    var silent = H(State.Live, sinceAgo: 300, contactAgo: 18, wasLive: true);
    var limited = H(State.RateLimited, contactAgo: 30, retryIn: 353, wasLive: true);               // 21:20:00
    var faulted = H(State.Faulted, retryIn: 293, wasLive: true, detail: Fatal);                     // 21:19:00
    var unresolved = H(State.ResolveFailed, retryIn: 293, wasLive: true);
    Row(silent).Should().Be(At(silent, "yanıt yok (18 sn)", Severity.Warning, NoAnswer));
    Row(limited).Should().Be(At(limited, "hız sınırı, 21:20'de dener", Severity.Warning,
        "Instagram istek sınırına takıldı. Bu hesabın yorumları o saate kadar gelmeyecek."));
    Row(faulted).Should().Be(At(faulted, "bağlantı yenilenmeli", Severity.Warning, $"{Fatal} {Reauth} 21:19'da yeniden denenecek."));
    Row(unresolved).Should().Be(Paged(unresolved, "hesap bilgisi okunamadı, 21:19'da dener", Severity.Warning,
        $"Sayfaya bağlı Instagram hesabı okunamadı. Sürerse: {Reauth}"));
}
[Fact] public void RetryAt_yoksa_saat_parcasi_yazilmaz_kullanici_adi_yoksa_etiket_Sayfa_adidir()
{
    Row(H(State.RateLimited, contactAgo: 5, wasLive: true)).StateText.Should().Be("hız sınırı");
    Row(H(State.ResolveFailed, wasLive: true)).StateText.Should().Be("hesap bilgisi okunamadı");
    Row(H(State.Faulted, wasLive: true, detail: Fatal)).Tooltip.Should().Be($"{Fatal} {Reauth}");
    var unnamed = H(State.Starting, sinceAgo: 5, named: false);
    Row(unnamed).Should().Be(new InstagramAccountStatusViewModel(unnamed.PageId, unnamed.PageName, "bağlanıyor", $"{unnamed.PageName} bağlanıyor", Severity.Neutral, null));
}
[Fact] public void Saat_metni_now_ofsetine_cevrilir()                    // Review Focus 4: makinenin saat dilimi değil, now'ın ofseti; ek de o duvar saatinden
{
    var now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(5.5)); var at = now.AddSeconds(-30).ToUniversalTime();
    var limited = H(State.RateLimited, wasLive: true) with { Since = at, LastContactAt = at, RetryAt = now.AddMinutes(10).ToUniversalTime() };
    InstagramHealthPresenter.Evaluate(new[] { limited }, now, sessionActive: true).Rows.Single().StateText.Should().Be("hız sınırı, 09:10'da dener");   // an Locative'e doğrudan verilirse UTC'de de +03:00'te de dakika 40 okunur: 'ta
}
[Fact] public void Live_ama_10_sn_den_eski_temasli_hesap_canli_sayilmaz()
{
    Eval(H(State.Live, sinceAgo: 300, contactAgo: 10, wasLive: true)).AnyAccountLive.Should().BeTrue();
    var stale = Eval(H(State.Live, sinceAgo: 300, contactAgo: 11, wasLive: true));
    stale.AnyAccountLive.Should().BeFalse(); stale.Rows.Single().StateText.Should().Be("yanıt yok (11 sn)");
}
[Theory, InlineData(State.WaitingForBroadcast), InlineData(State.BroadcastEnded), InlineData(State.Starting)]
public void Bekleyen_durum_45_sn_den_uzun_sessizlikte_yanit_yok_olur_sure_son_temas_ile_Since_ten_gec_olandan_olculur(State state)
{
    Row(H(state, sinceAgo: 300, contactAgo: 45, wasLive: true)).StateText.Should().NotStartWith("yanıt yok");
    Row(H(state, sinceAgo: 300, contactAgo: 46, wasLive: true)).StateText.Should().Be("yanıt yok (46 sn)");
    Row(H(state, sinceAgo: 44, contactAgo: 600, wasLive: true)).StateText.Should().NotStartWith("yanıt yok");   // duruma yeni geçildi: eski temas sayılmaz
    Row(H(state, sinceAgo: 46, contactAgo: 600, wasLive: true)).StateText.Should().Be("yanıt yok (46 sn)");
}
[Theory, InlineData(State.RateLimited), InlineData(State.Faulted), InlineData(State.ResolveFailed), InlineData(State.NotLinked)]
public void RetryAt_i_45_sn_den_fazla_gecilmis_hesap_yanit_yok_olur(State state)
{
    Row(H(state, sinceAgo: 400, retryIn: -45, wasLive: true)).StateText.Should().NotStartWith("yanıt yok");
    var late = H(state, sinceAgo: 400, retryIn: -46, wasLive: true);
    Row(late).Should().Be(At(late, "yanıt yok (46 sn)", Severity.Warning, NoAnswer));
}
[Fact] public void Tek_Sayfada_sorun_durumu_yalniz_hesap_bu_oturumda_canliydiysa_saridir()
{
    var wasLive = Eval(H(State.Faulted, retryIn: 270, wasLive: true, detail: Fatal));
    wasLive.Rows.Single().Severity.Should().Be(Severity.Warning);
    wasLive.Should().BeEquivalentTo(new { ShowStrip = true, TopDotTooltip = $"Instagram {wasLive.Rows[0].Label}: bağlantı yenilenmeli." });
    var never = Eval(H(State.Faulted, retryIn: 270, detail: Fatal));             // hiç canlı olmadı: aynı metin, nötr, şerit gizli
    never.Rows.Single().Should().BeEquivalentTo(new { StateText = "bağlantı yenilenmeli", Severity = Severity.Neutral });
    never.Should().BeEquivalentTo(new { ShowStrip = false, TopDotTooltip = (string?)null });
}
[Theory, InlineData(State.RateLimited, 30.0), InlineData(State.Starting, 46.0), InlineData(State.Faulted, 30.0), InlineData(State.ResolveFailed, 30.0), InlineData(State.NotLinked, 30.0)]
public void Iki_Sayfada_sorun_durumu_hic_canli_olmamis_hesapta_da_saridir(State state, double sinceAgo)   // kapı yok; Starting 46 sn temassız → "yanıt yok"
{
    var view = Eval(H(state, sinceAgo, retryIn: 270), H(state, sinceAgo, retryIn: 270, primary: false));
    view.Rows.Should().HaveCount(2).And.OnlyContain(r => r.Severity == Severity.Warning);
    view.Should().BeEquivalentTo(new { ShowStrip = true, TopDotTooltip = $"Instagram: 2 hesapta sorun ({view.Rows[0].Label}, {view.Rows[1].Label})." });
}
[Fact] public void Canli_yayin_bulunamadi_90_sn_bekleme_ile_oteki_hesabin_canliligindan_gec_olandan_olculur()
{
    InstagramAccountStatusViewModel Waiting(double waitingFor, double otherLiveFor)        // öteki hesap otherLiveFor sn'dir canlı
        => Eval(Healthy(otherLiveFor), H(State.WaitingForBroadcast, waitingFor, contactAgo: 5, primary: false)).Rows[1];
    Waiting(89, 300).StateText.Should().Be("yayın bekleniyor");
    Waiting(300, 89).StateText.Should().Be("yayın bekleniyor");
    Waiting(300, 91).StateText.Should().Be("canlı yayın bulunamadı");
    Waiting(91, 300).Should().BeEquivalentTo(new { StateText = "canlı yayın bulunamadı", Severity = Severity.Warning,
        Tooltip = "Diğer hesap yayında ama bu hesapta canlı yayın görünmüyor. Bu hesapta yayını açtın mı?" });
    Eval(Healthy(200), Healthy(50, primary: false), H(State.WaitingForBroadcast, sinceAgo: 300, contactAgo: 5, primary: false))
        .Rows[2].StateText.Should().Be("canlı yayın bulunamadı");        // üç hesap: canlılardan EN ERKEN canlı olan (200 sn'lik) esas alınır
}
[Fact] public void Yayin_kesildi_yalniz_baska_hesap_canliyken_saridir_ust_noktayi_ilk_2_dakika_sarartir()
{
    var early = Eval(Healthy(), H(State.BroadcastEnded, sinceAgo: 119, contactAgo: 5, wasLive: true, primary: false));
    early.Rows[1].Should().BeEquivalentTo(new { StateText = "yayın kesildi (21:12)", Severity = Severity.Warning,
        Tooltip = "Bu hesabın yayını kapandı; yorumları gelmiyor. Diğer hesap yayında." });
    early.TopDotTooltip.Should().Be($"Instagram {early.Rows[1].Label}: yayın kesildi (21:12).");
    var late = Eval(Healthy(), H(State.BroadcastEnded, sinceAgo: 121, contactAgo: 5, wasLive: true, primary: false));
    late.Rows[1].Severity.Should().Be(Severity.Warning); late.TopDotTooltip.Should().BeNull();       // satır sarı kalır
}
[Fact] public void Canli_yayin_bulunamadi_ust_noktayi_sarartmaz_baska_sari_durum_sarartir()           // TK20
{
    var missing = H(State.WaitingForBroadcast, sinceAgo: 120, contactAgo: 5, primary: false);
    var only = Eval(Healthy(), missing);
    only.Rows[1].Severity.Should().Be(Severity.Warning);
    only.Should().BeEquivalentTo(new { ShowStrip = true, TopDotTooltip = (string?)null });
    var limited = H(State.RateLimited, contactAgo: 30, retryIn: 300, primary: false);                // 21:19:07
    Eval(Healthy(), missing, limited).TopDotTooltip.Should().Be($"Instagram @{limited.Username}: hız sınırı, 21:19'da dener.");
}
[Fact] public void NotLinked_tek_Sayfada_canli_olmadiysa_gizlidir_canliydiysa_ve_iki_Sayfada_saridir_ipucu_birincilde_ve_ekte_ayridir()
{
    var hidden = Eval(H(State.NotLinked, retryIn: 270)); hidden.Rows.Should().BeEmpty(); hidden.ShowStrip.Should().BeFalse();
    Eval(H(State.NotLinked, sinceAgo: 400, retryIn: -46)).Rows.Should().BeEmpty();                   // gizleme "yanıt yok"tan önce
    var wasLive = Eval(H(State.NotLinked, retryIn: 270, wasLive: true));
    wasLive.Rows.Single().Severity.Should().Be(Severity.Warning); wasLive.ShowStrip.Should().BeTrue();
    var (primary, extra) = (H(State.NotLinked, retryIn: 270), H(State.NotLinked, retryIn: 270, primary: false));   // iki Sayfa: birincil dahil sarı
    Eval(primary, extra).Rows.Should().Equal(
        Paged(primary, "Instagram hesabı bağlı değil", Severity.Warning, "Birincil Sayfaya bağlı Instagram hesabı görünmüyor. Sayfa ile Instagram " +
            "hesabının bağını kontrol et; bağ duruyorsa Ayarlar > Facebook > \"Facebook izinlerini güncelle\"ye basıp tüm hesapları işaretle."),
        Paged(extra, "Instagram hesabı bağlı değil", Severity.Warning, "Bu Sayfaya bir Instagram profesyonel hesabı bağla ya da Sayfayı Ayarlar'dan kaldır."));
}
[Theory, InlineData(State.WaitingForBroadcast), InlineData(State.BroadcastEnded)]
public void Yanit_yok_taban_durumun_satirindan_once_gelir(State state)
{
    var silent = H(state, sinceAgo: 300, contactAgo: 46, wasLive: true, primary: false);
    var view = Eval(Healthy(), silent);
    view.Rows[1].StateText.Should().Be("yanıt yok (46 sn)");
    view.TopDotTooltip.Should().Be($"Instagram @{silent.Username}: yanıt yok (46 sn).");
}
[Fact] public void Oteki_hesap_yanitsizken_bagimli_satirlar_notre_doner_donunce_sure_yeniden_sayilmaz()
{
    var ended = H(State.BroadcastEnded, sinceAgo: 60, contactAgo: 5, wasLive: true, primary: false);
    var waiting = H(State.WaitingForBroadcast, sinceAgo: 200, contactAgo: 5, primary: false);
    var stalled = Eval(H(State.Live, sinceAgo: 300, contactAgo: 11, wasLive: true), ended, waiting);
    stalled.Rows.Select(r => r.StateText).Should().Equal("yanıt yok (11 sn)", "yayın bitti (21:13)", "yayın bekleniyor");
    stalled.Rows.Select(r => r.Severity).Should().Equal(Severity.Warning, Severity.Neutral, Severity.Neutral);
    Eval(Healthy(300), ended, waiting).Rows.Select(r => r.StateText)                                 // aynı Since: süre baştan sayılmaz
        .Should().Equal("canlı", "yayın kesildi (21:13)", "canlı yayın bulunamadı");
}
[Fact] public void Skipped_ve_Stopped_seride_girmez_ama_Sayfa_sayisina_sayilir()
{
    Row(H(State.Faulted, retryIn: 270, detail: Fatal), H(State.Skipped, primary: false)).Severity.Should().Be(Severity.Warning);   // 2 Sayfa: kapı yok
    var healthy = Eval(Healthy(), H(State.Skipped, primary: false));
    healthy.Rows.Should().ContainSingle(); healthy.ShowStrip.Should().BeFalse();                     // şeride giren hesap 1, sarı yok
    var stopped = Eval(H(State.Stopped), H(State.Stopped, primary: false));
    stopped.Rows.Should().BeEmpty(); stopped.Should().BeEquivalentTo(new { ShowStrip = false, AnyAccountLive = false });
}
[Fact] public void Serit_oturum_acikken_iki_hesapta_ya_da_sari_satirda_gorunur()
{
    Eval(Healthy()).ShowStrip.Should().BeFalse();                                                     // tek hesap sağlıklı
    Eval(H(State.Live, sinceAgo: 300, contactAgo: 11, wasLive: true)).ShowStrip.Should().BeTrue();    // tek hesap sarı
    Eval(Healthy(), Healthy(primary: false)).ShowStrip.Should().BeTrue();                             // iki hesap
    var noSession = InstagramHealthPresenter.Evaluate(new[] { Healthy(), Healthy(primary: false) }, Now, sessionActive: false);
    noSession.Rows.Should().HaveCount(2);                                                             // oturum yalnız şeridi kapılar
    noSession.Should().BeEquivalentTo(new { ShowStrip = false, AnyAccountLive = true });
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramHealthPresenterTests"`
Beklenen: FAIL (derleme hatası: `InstagramHealthPresenter`, `InstagramHealthView`, `InstagramStatusSeverity`, `InstagramAccountStatusViewModel` tanımlı değil)

- [ ] **Adım 3: Üç veri tipini ekle ve `Evaluate(IReadOnlyList<InstagramAccountHealth> snapshot, DateTimeOffset now, bool sessionActive)` uygula (`InstagramHealthPresenter.cs`, `InstagramAccountStatusViewModel.cs`)**

İki geçiş: önce her girdi için yanıtsız ve canlı bayrakları türetilir (bağımlı kurallar canlı kümesine bakar), sonra girdiler anlık görüntü sırasıyla satıra çevrilir; şerit ve üst nokta satırlardan hesaplanır. Beş eşik `private static readonly TimeSpan` sabitidir (`LiveSilence`, `IdleSilence`, `RetryOverdue`, `MissingBroadcastAfter`, `EndedTopDotFor`). Saatin bulunma eki mevcut `Locative`'ten alınır; `Locative` zamanı makinenin yerel saatine çevirdiği için `now.Offset`'teki duvar saati yerel ofsetle yeniden kurulup verilir: `var wall = at.ToOffset(now.Offset).DateTime;` → `PaymentResultPresenter.Locative(new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)))`.

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramHealthPresenterTests"`
Beklenen: PASS

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramHealthPresenterTests|FullyQualifiedName~PaymentResultPresenterTests"` ve `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q`
Beklenen: PASS; derleme 0 uyarı (`PaymentResultPresenter.cs` değişmedi). Sunucu henüz arayüze bağlı değildir (G15 bağlar).

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.App/Services/Instagram/InstagramHealthPresenter.cs OrderDeck.App/ViewModels/InstagramAccountStatusViewModel.cs \
  OrderDeck.Tests/Services/Instagram/InstagramHealthPresenterTests.cs
git commit -F - <<'MSG'
feat(instagram): sağlık şeridinin görünüm kurallarını saf sunucu olarak ekle

Yanıtsızlık eşikleri, tek Sayfadaki uyarı kapısı, 90 saniye kuralı ve üst
nokta ipucu tek yerde dursun ve saatten bağımsız sınanabilsin diye durumsuz
bir işlevde toplanır (çoklu hesap §8.5, §8.6.3-4). Arayüze henüz bağlı değil.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G09: `ChatMessage.SourceAccount` + `InstagramMergeGate` (PR-A: etiket + ölçüm, düz geçiş)

**Spec:** §8.4; §8.3.5; §8.8 (kapı özeti satırı); §9.2 `InstagramMergeGateTests` "PR-A" maddesi; §10 adım 6–7; TK6, TK7.
**Bağımlı:** G06, G07 (yalnız `ManualTime`).

**Files:**
- Modify: `OrderDeck.Core/Chat/ChatMessage.cs:5-14` (bugünkü satırlar)
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramMergeGate.cs`
- Test: `OrderDeck.Tests/Chat/Instagram/InstagramMergeGateTests.cs` (yeni)

**Interfaces:**
- Consumes: G06 `public interface IInstagramPollObserver { void RoundStarting(); void RoundCompleted(InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader); }`, `public enum InstagramPollKind { Ok, NoBroadcast, Transient, RateLimited, Fatal }`; mevcut `public interface IChatBus { IDisposable Subscribe(Action<ChatMessage> handler); void Publish(ChatMessage message); IReadOnlyList<ChatMessage> RecentMessages(); }`; test için G07 `ManualTime` (`Advance(TimeSpan by)`) ve mevcut `CapturingLogger<T>` (`Entries`: `Level`, `Message`).
- Produces:
  - `public sealed record ChatMessage(string Id, string Platform, string? ExternalId, string Username, string? DisplayName, string? AvatarUrl, string Text, long ReceivedAt, IReadOnlyList<string> Badges, string? SourceAccount = null);`
  - `public sealed class InstagramMergeGate : IDisposable { internal static readonly TimeSpan HoldBudget = TimeSpan.Zero; internal static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(1); public InstagramMergeGate(IChatBus inner, TimeProvider time, ILogger log, TimeSpan? holdBudget = null); public Lane AddLane(string accountKey, string label); public void Dispose(); public sealed class Lane : IChatBus, IInstagramPollObserver { public void Complete(); } }` (`SummaryInterval` karar; `label` gösterime hazır metindir, kapı "@" eklemez; `Lane` poller'a hem bus hem gözlemci olarak verilebilir).
  - Özet satırı (Information; biçim BİREBİR, karar): `[InstagramMergeGate] summary: inversions={n} maxGapSeconds={sn} lanes: {kulvarlar}`; kulvarlar `AddLane` sırasıyla ve `; ` ile ayrılmış `{label} rounds={n} p50Ms={ms} p95Ms={ms}`. Sayılar pencere başınadır (her özetten sonra sıfırlanır).

**Kurallar:**
- `SourceAccount` konumsal kaydın SONUNA eklenir, varsayılanı null. Beş üretim çağrısı (`ExtensionBridgeServer.cs:658`, `FacebookLiveCommentsStream.cs:283`, `YouTubeOfficialChatIngestor.cs:247`, `LabelService.cs:518`, poller'ın `Publish`'i) ve testlerdeki dokuz argümanlı çağrılar DEĞİŞMEDEN derlenir. Alan kalıcılaşmaz: overlay (`OverlayHost.cs:428-437` alanları elle kopyalar), SQLite ve DTO'lar değişmez. Alanı yalnız kapı doldurur.
- PR-A sınırı: `HoldBudget` VAR ve değeri `TimeSpan.Zero` (400 ms'yi PR-B yazar). `holdBudget` parametresi imzada durur ama okunmaz ve alana yazılmaz (okunmayan özel alan derlemeyi kırar). Tutma, tur, üyelik kodu ve `IInstagramPollPacer` YOK (spec §8.3.5 bloğundaki "PR-B" işaretli parçalar uygulanmaz).
- `Lane.Publish(m)`: `m with { SourceAccount = label }` hemen iç bus'a basılır (hesaplar arası sıra varış sırasıdır). `Subscribe` ve `RecentMessages` iç bus'a devredilir. `accountKey` = PageId; özete girmez.
- Mesaj kaybı yok: kapının kendi adımı (etiketleme, ölçüm, özet) fırlatırsa mesaj yine iç bus'a basılır (etiketsiz de olsa); `Lane`'in gözlemci yöntemleri ve `Dispose` da kendi adımı yüzünden fırlatmaz. İç bus fırlatırsa istisna çağırana çıkar (yutulmaz); kilit ve sayaçlar bozulmaz, sonraki mesajlar akar. `Complete()` ve `Dispose()`'tan sonra gelen `Publish` de etiketlenip basılır.
- Ölçüm: tur süresi `RoundStarting` → `RoundCompleted` arasıdır ve her sonuç türü sayılır; süreler `time.GetTimestamp()` / `time.GetElapsedTime(...)` ile ölçülür. Ters sıra: basılan mesajın `ReceivedAt`'i kapının o ana dek bastığı en büyük `ReceivedAt`'ten küçükse sayaç +1; en büyük fark bu farkların en büyüğüdür (sn). Eşit damga ters sıra değildir.
- Özet (karar): zamanlayıcı YOK. Bir olay (`Publish`, `RoundStarting`, `RoundCompleted`) işlendikten sonra pencere `SummaryInterval` kadar dolmuşsa tek satır yazılır ve pencere sayaçları sıfırlanır (en büyük damga sıfırlanmaz). `Dispose()` son pencereyi (pencere boş ya da kulvar hiç açılmamış olsa da) bir kez yazar; ikinci `Dispose` yazmaz, sonrasında ölçüm ve özet yoktur. Kapıya verilen `ILogger`'ı çağıran seçer (G10 `ILogger<InstagramMergeGate>` verir; G13 kategoriye göre önek denetler). Satırda izleyici verisi (kullanıcı adı, yorum metni) bulunmaz. `Complete()` idempotenttir: kulvar bir sonraki özette son kez yer alır, sonra listeden düşer.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// InstagramMergeGateTests (yeni). Alanlar: ManualTime _time = new(); ChatBus _inner = new(ringBufferSize: 50); List<ChatMessage> _received (kurucuda
// _inner.Subscribe(_received.Add)); CapturingLogger<InstagramMergeGate> _log = new(). Yardımcılar (gövdesi yazılmamış olanlar imzadır; gövdeyi uygulayıcı yazar):
private static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
private static ChatMessage Msg(long receivedAt);                         // dokuz argümanlı Instagram mesajı; kimlikler ve izleyici adı üretilir; receivedAt = Meta damgası (sn)
private InstagramMergeGate NewGate(IChatBus? inner = null, TimeProvider? time = null, TimeSpan? holdBudget = null) => new(inner ?? _inner, time ?? _time, _log, holdBudget);
private string[] Summaries();                                            // iletisi "[InstagramMergeGate] summary" ile başlayan Information kayıtları, sırayla
private sealed class FaultyTime : TimeProvider { public bool Broken; }   // Broken iken GetTimestamp ve GetUtcNow InvalidOperationException fırlatır
private sealed class FlakyBus : IChatBus { public bool Broken; public List<ChatMessage> Published { get; } = new(); }   // Broken iken Publish InvalidOperationException fırlatır

[Theory, InlineData(null), InlineData(400)]                              // PR-A'da tutma yok: holdBudget verilse de davranış aynı
public void Publish_etiketi_basar_diger_alanlar_ayni_kalir_mesaj_hemen_gecer(int? holdMs)
{
    using var gate = NewGate(holdBudget: holdMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
    var (handle, pageName, first, second) = (New("@u"), New("Sayfa"), Msg(1_000), Msg(1_001));
    gate.AddLane(New("p"), handle).Publish(first);
    gate.AddLane(New("p"), pageName).Publish(second);                    // etiket gösterime hazır: kapı "@" eklemez
    first.SourceAccount.Should().BeNull();
    _received.Should().Equal(first with { SourceAccount = handle }, second with { SourceAccount = pageName });
    InstagramMergeGate.HoldBudget.Should().Be(TimeSpan.Zero);
}
[Fact] public void Subscribe_ve_RecentMessages_ic_bus_a_devredilir()
{
    using var gate = NewGate(); var lane = gate.AddLane(New("p"), New("@u"));
    var seen = new List<ChatMessage>(); using var sub = lane.Subscribe(seen.Add);
    var direct = Msg(1_000); _inner.Publish(direct);                     // kulvardan geçmeyen mesaj
    seen.Should().Equal(direct); lane.RecentMessages().Should().Equal(_inner.RecentMessages());
}
[Fact] public void Ters_sira_sayaci_yalniz_geri_giden_damgada_artar()
{
    var gate = NewGate(); var (a, b) = (gate.AddLane(New("p"), New("@u")), gate.AddLane(New("p"), New("@u")));
    a.Publish(Msg(100)); b.Publish(Msg(100));                            // eşit damga: ters sıra değil
    a.Publish(Msg(105)); b.Publish(Msg(102));                            // 3 sn geri
    b.Publish(Msg(104)); a.Publish(Msg(106));                            // 1 sn geri (en büyük damga 105), sonra ileri
    gate.Dispose();
    Summaries().Should().ContainSingle().Which.Should().Contain("inversions=2 maxGapSeconds=3 ");
}
[Fact] public void Olcum_adimi_firlatsa_da_mesaj_ic_bus_a_ulasir()
{
    var time = new FaultyTime(); using var gate = NewGate(time: time);   // Dispose da fırlatmamalı
    var lane = gate.AddLane(New("p"), New("@u")); var m = Msg(1_000);
    time.Broken = true;
    lane.Publish(m);
    FluentActions.Invoking(() => { lane.RoundStarting(); lane.RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null); }).Should().NotThrow();
    _received.Should().ContainSingle().Which.Id.Should().Be(m.Id);
}
[Fact] public void Ic_bus_firlatirsa_istisna_cagirana_cikar_sonraki_mesajlar_akar()
{
    var inner = new FlakyBus { Broken = true }; using var gate = NewGate(inner);
    var (a, b) = (gate.AddLane(New("p"), New("@u")), gate.AddLane(New("p"), New("@u")));
    FluentActions.Invoking(() => a.Publish(Msg(100))).Should().Throw<InvalidOperationException>();
    inner.Broken = false; b.Publish(Msg(101)); a.Publish(Msg(102));
    inner.Published.Select(m => m.ReceivedAt).Should().Equal(101, 102);
}
[Fact] public void Ozet_dakikada_bir_olay_gelince_yazilir_Dispose_son_pencereyi_bir_kez_yazar()
{
    var gate = NewGate(); var label = New("@u"); var lane = gate.AddLane(New("p"), label);
    foreach (var ms in new[] { 400, 100, 300, 200 })
    {
        lane.RoundStarting(); _time.Advance(TimeSpan.FromMilliseconds(ms)); lane.RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
    }
    Summaries().Should().BeEmpty();                                      // dakika dolmadı
    _time.Advance(TimeSpan.FromMinutes(1)); lane.Publish(Msg(100));      // saat denetimi olay gelince yapılır
    Summaries().Should().Equal($"[InstagramMergeGate] summary: inversions=0 maxGapSeconds=0 lanes: {label} rounds=4 p50Ms=200 p95Ms=400");
    gate.Dispose(); gate.Dispose();
    Summaries().Should().HaveCount(2);                                   // Dispose bir kez yazar
    Summaries()[1].Should().EndWith($"lanes: {label} rounds=0 p50Ms=0 p95Ms=0");          // pencere sıfırlandı
    lane.Publish(Msg(101));
    _received.Should().HaveCount(2); Summaries().Should().HaveCount(2);                   // Dispose'tan sonra da düz geçer, özet yazılmaz
}
[Fact] public void Complete_sonrasi_mesaj_yine_etiketlenir_kulvar_son_ozetten_sonra_duser()
{
    var gate = NewGate(); var (gone, stays) = (New("@u"), New("@u"));
    var closing = gate.AddLane(New("p"), gone); gate.AddLane(New("p"), stays);
    closing.Complete(); closing.Complete(); closing.Publish(Msg(100));
    _received.Should().ContainSingle().Which.SourceAccount.Should().Be(gone);
    _time.Advance(TimeSpan.FromMinutes(1)); closing.Publish(Msg(101));   // 1. özet: kapanan kulvar son kez
    Summaries().Should().ContainSingle().Which.Should().Contain(gone).And.Contain(stays);
    gate.Dispose();                                                      // 2. özet: yalnız açık kulvar
    Summaries()[1].Should().Contain(stays).And.NotContain(gone);
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMergeGateTests"`
Beklenen: FAIL (derleme hatası: `InstagramMergeGate` tanımlı değil; `ChatMessage`'da `SourceAccount` yok)

- [ ] **Adım 3: `ChatMessage`'a `string? SourceAccount = null` ekle (`ChatMessage.cs`) ve `InstagramMergeGate` + `Lane`'i uygula (`InstagramMergeGate.cs`)**

`SourceAccount` konumsal listenin SON öğesidir (`Badges`'tan sonra); başka hiçbir mevcut dosya değişmez. Kapıda tek kilit vardır: kulvar listesi, pencere sayaçları ve iç bus'a yayım aynı kilit altındadır (ölçülen sıra basılan sıradır). `Publish`'te etiketleme ve ölçüm bir `try/catch` içinde, `inner.Publish` onun DIŞINDA durur. Yüzdelik en yakın sıra (nearest-rank) yöntemiyledir: o kulvarın penceredeki süreleri küçükten büyüğe sıralanır, `p = sıralı[ceil(q × n) − 1]` (q = 0,50 ve 0,95), n = 0 ise 0; değer tam milisaniyeye yuvarlanır.

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMergeGateTests"`
Beklenen: PASS

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `dotnet build OrderDeck.sln --nologo -v q` ve `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMergeGateTests|FullyQualifiedName~ChatBusTests|FullyQualifiedName~SpamFilterTests|FullyQualifiedName~ExtensionBridgeServerTests"`
Beklenen: çözümün TAMAMI 0 uyarıyla derlenir (Core değişti); testler PASS, üç mevcut sınıf değiştirilmeden. `SourceAccount`'u henüz kimse doldurmaz (G10 doldurur).

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.Core/Chat/ChatMessage.cs OrderDeck.Chat/Ingestors/Instagram/InstagramMergeGate.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramMergeGateTests.cs
git commit -F - <<'MSG'
feat(instagram): sohbet mesajına kaynak hesabı ve etiketleyen birleştirme kapısını ekle

İki ve üzeri Sayfada her Instagram yorumu hangi hesaptan geldiğini taşımalı.
Kapı PR-A'da mesajı etiketleyip hemen basar ve PR-B kararını rakama bağlamak
için tur süresini ve ters sıra sayısını dakikada bir özetler (çoklu hesap
§8.3.5, §8.4). Alanı henüz kimse doldurmaz.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G10: Hosted service: süre kaydı, üst döngü ve hesap döngüsü

**Spec:** §8.3.2 (üst döngü 1, 2, 3, 5; hesap döngüsü 1–5; "Tek hesapta korunanlar"; yan fark b), §8.3.7, §8.5 ("Yalıtım", "Geçişler", "Hesap başına durumlar"), §8.8, §9.3, §11.3; AH2, AH4, TK6, TK15. Hesap döngüsünün 6. adımı (TK16) G11'de, üst döngünün 4. adımı (TK14) G12'dedir.
**Bağımlı:** G02, G05, G06, G07, G09.

**Files:**
- Modify: `OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs` (bugünkü satırlar: sabitler `:27-30`, alanlar ve kurucu `:34-64`, `OnSessionEnded` `:66-70`, `RunAsync` `:94-202` yeniden yazılır, `Dispose` `:214-220`; `ComputeBackoff` `:205-212` AYNEN kalır)
- Create: `OrderDeck.Chat/Ingestors/Instagram/InstagramAccountLoop.cs`
- Test: Create `OrderDeck.Tests/TestHelpers/CapturingLoggerFactory.cs`, `OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.cs` (`partial`; G11 ve G12 dosya ekler)

**Interfaces:**
- Consumes:
  - G01 / G02: `FacebookLinkedPage(string PageId, string PageAccessToken, string PageName)`; `Task<IReadOnlyList<FacebookLinkedPage>> FacebookOAuthService.GetConnectedPagesAsync(CancellationToken ct = default)` (birincil önce; bağlı değilse boş; bellekte demet yokken token dosyasına bakar). Kit (`OrderDeck.Tests.TestHelpers`; imzalar G02'de, `CreateService`'in ikinci parametresi G03'te): `FacebookOAuthTestKit : IDisposable` (parametresiz kurucu, `Store`, `Graph`, `HttpFactory`, `FacebookOAuthService CreateService(ILogger<FacebookOAuthService>? log = null, TimeSpan? callbackTimeout = null)`, `Task<IReadOnlyList<SeededPage>> SeedAsync(int extraPages = 0)`, `static string NewId()`, `static string NewToken()`); `SeededPage(FacebookLinkedPage Page, string? IgUserId, string? IgUsername)`; `FakeGraph` (`void AddPage(string pageId, string pageName, string? igUserId = null, string? igUsername = null)`, `void Map(Func<Uri, bool> match, Func<Uri, HttpResponseMessage> respond)`, `IReadOnlyList<Uri> Requests`, `static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? usageHeader = null)`, `static HttpResponseMessage Error(int code, HttpStatusCode status = HttpStatusCode.BadRequest, int? subcode = null)`).
  - G05: `Task<(InstagramAccount? Account, InstagramResolveStatus Status)> InstagramAccountResolver.ResolveDetailedAsync(string pageId, string pageAccessToken, CancellationToken ct)`; `enum InstagramResolveStatus { Found, NotLinked, Failed }`; `static bool InstagramGraphError.TryGetUsage(string? headerValue, out IReadOnlyList<InstagramBucUsage> usage)`; `InstagramBucUsage(string BusinessId, double CallCountPercent, double TotalCpuTimePercent, double TotalTimePercent, double? MinutesToRegainAccess)`.
  - G06: `InstagramLiveCommentsPoller(string igUserId, string pageAccessToken, IChatBus bus, HttpClient http, ILogger<InstagramLiveCommentsPoller> log, SpamFilter? spamFilter = null, IInstagramPollObserver? observer = null, InstagramCommentWatermark? watermark = null)`; `IInstagramPollObserver { void RoundStarting(); void RoundCompleted(InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader); }`; `enum InstagramPollKind { Ok, NoBroadcast, Transient, RateLimited, Fatal }`.
  - G07: `InstagramHealthMonitor(TimeProvider? time = null)` ve üyeleri `void Configure(IReadOnlyList<FacebookLinkedPage> pages)`, `void ResetSession()`, `void StopAll()`, `void Clear()`, `void SetAccount(string pageId, string? username)`, `void Report(string pageId, InstagramAccountState state, string? detail = null, DateTimeOffset? retryAt = null)`, `IInstagramPollObserver ObserverFor(string pageId)`, `IReadOnlyList<InstagramAccountHealth> Snapshot()`. Anlamlar: `Report(id, Starting)` yalnız `Stopped` girdide etkilidir; `SetAccount` "çözümleme `Found`" olayıdır; `Report` `Detail` ile `RetryAt`'i her çağrıda yeniden yazar. `ManualTime : TimeProvider` (`void Advance(TimeSpan by)`; zamanlayıcı sürmez: `Task.Delay(süre, time, ct)` gerçek süre bekler).
  - G09: `InstagramMergeGate(IChatBus inner, TimeProvider time, ILogger log, TimeSpan? holdBudget = null) : IDisposable`, `Lane AddLane(string accountKey, string label)`, `Lane : IChatBus, IInstagramPollObserver { void Complete(); }`; `Dispose` `[InstagramMergeGate]` önekli tek Information özet satırı yazar.
- Produces:
  - `public InstagramChatHostedService(FacebookOAuthService oauth, IChatBus bus, ILoggerFactory loggerFactory, SpamFilter? spamFilter = null, StreamSessionService? sessions = null, IHttpClientFactory? httpFactory = null, InstagramHealthMonitor? health = null, TimeProvider? time = null);` · `internal` kurucu: aynı sekiz parametre varsayılansız + sonda `InstagramChatTimings timings`. İzleyici `_health` adlı özel alanda tutulur ve sınıfta `InstagramHealthMonitor` tipinde başka örnek alanı OLMAZ: G15'in DI testi alanı tipinden yansımayla okur ve tek alan bekler.
  - `internal sealed record InstagramChatTimings(TimeSpan IdleWhenOffline, TimeSpan IdleAfterPollerExit, TimeSpan IdleAfterFatal, TimeSpan WatchInterval, TimeSpan UsageLogInterval) { public static InstagramChatTimings Default { get; } }` (hosted service'in dosyasında; `Default` = 60 sn / 30 sn / 5 dk / 2 sn / 1 dk)
  - `[SupportedOSPlatform("windows")] internal sealed class InstagramAccountLoop { internal InstagramAccountLoop(string pageId, InstagramLoopServices services, InstagramAccountResolver resolver, ConcurrentDictionary<string, string> owners, InstagramMergeGate? gate); internal Task RunAsync(CancellationToken ct); }` (`owners`: IgUserId → sahip PageId) · aynı dosyada, aynı öznitelikle `internal sealed record InstagramLoopServices(FacebookOAuthService OAuth, IChatBus Bus, ILoggerFactory LoggerFactory, ILogger Log, SpamFilter? SpamFilter, IHttpClientFactory? HttpFactory, InstagramHealthMonitor Health, TimeProvider Time, InstagramChatTimings Timings);` (`Log` hosted service'in `ILogger<InstagramChatHostedService>`'ıdır)
  - Günlük şablonları (karar; G13 biçimi doğrular): `[InstagramChatHostedService] account {Account} (page {PageId}): {From} -> {To}` · yeni durumun `RetryAt`'i doluysa sonuna `, retry at {RetryAt:HH:mm:ss}` (yerel saat) · `[InstagramChatHostedService] usage {Account} (page {PageId}): {Usage}`. `{Account}` = kullanıcı adı biliniyorsa `@kullanıcıadı`, yoksa Sayfa adı. `{Usage}` = kova başına `business {BusinessId} call_count {a}% total_cputime {b}% total_time {c}% regain {m} min`, kovalar `; ` ile; sayılar `CultureInfo.InvariantCulture` ve `0.##`, `MinutesToRegainAccess` null ise `0`; başlık yoksa `X-Business-Use-Case-Usage header absent`, `TryGetUsage` false dönerse `X-Business-Use-Case-Usage header unreadable`.
  - Test: `internal sealed class CapturingLoggerFactory : ILoggerFactory`, `internal sealed class InstagramLiveFeed` (imzalar Adım 1'de; ikisini G13 de kullanır).

**Kurallar:**
- **Üst döngü** (§8.3.2 "Üst döngü" 1, 2, 3, 5), her turda sırayla: (karar) oturum kapsamlı iptal kaynağı oturum kapısı okunmadan ÖNCE kurulur ve `OnSessionEnded` onu iptal eder (bugünkü `:120` ile `:153` arasına düşen `SessionEnded` kaçmasın; `_pollerCts` alanının yerini alır) → `GetConnectedPagesAsync` boşsa `Clear()` + `IdleWhenOffline` → `sessions` verildiyse ve `GetActive()` null ise `IdleWhenOffline` (oturum kapısı yalnız burada; `sessions` null ise oturum hep açıktır, `ResetSession` hiç çağrılmaz) → `Configure(pages)`; etkin oturumun `Id`'si son görülenden farklıysa `ResetSession()` → Sayfa sayısı ≥ 2 ise kapı: `new InstagramMergeGate(bus, time, loggerFactory.CreateLogger<InstagramMergeGate>())` → her Sayfa için bir `InstagramAccountLoop`, hepsi birlikte başlar → oturum kaynağı iptal olana dek beklenir (4. adım yok: liste oturum başında okunur).
- **Oturum sonu sırası** (testler buna yaslanır): hesap döngülerinin BİTMESİ beklenir → kapı `Dispose()` → `StopAll()` → `IdleAfterPollerExit` → başa. Servis durdurulurken (servis belirteci iptal) de aynı toplama koşar ve `StopAsync` o bitmeden dönmez: kapının özet satırı yazılmış olur (son bekleme atlanır; G13 buna yaslanır). `SessionStarted` aboneliği EKLENMEZ (oturum en geç 60 sn'de görülür, AH4).
- **Hesap döngüsü** bugünkü `:126-192` gövdesinin hesap başına kopyasıdır (§8.3.2 "Hesap döngüsü" 1–5); çöküş sayacı ve beklemeler döngüye özeldir. Bir tur: (1) Sayfa `GetConnectedPagesAsync`'te yoksa döngü biter; varsa `Report(pageId, Starting)`. (2) `ResolveDetailedAsync`: `NotLinked` / `Failed` → `Report(NotLinked | ResolveFailed, retryAt: şimdi + IdleAfterFatal)` + `IdleAfterFatal` (TK15). (3) `owners.GetOrAdd(IgUserId, pageId)` başka Sayfayı dönerse Warning (`[InstagramChatHostedService]` öneki, atlanan PageId) + `Report(Skipped, retryAt: şimdi + IdleAfterFatal)` + `IdleAfterFatal`; sahiplik döngü yaşadıkça (beklemeleri dahil) sürer, döngü çıkarken bırakılır. `SetAccount(pageId, username)` sahiplik alındıktan SONRA çağrılır (atlanan Sayfa `Skipped` ↔ `Starting` yanıp sönmesin). (4) Poller bugünkü argümanlarla (`:144-150`) + `observer` + her kurulumda taze `watermark`; `gate` null ise bus `Bus` (`SourceAccount` null kalır), değilse kulvar: `AddLane(pageId, etiket)` ilk başarılı çözümlemede bir kez açılır, poller yeniden kurulurken aynısı kullanılır, döngü çıkarken `Complete()`; etiket `@kullanıcıadı`, kullanıcı adı yoksa Sayfa adı ("@"sız). (5) Poller bitince `FatalReason` varsa `Report(Faulted, FatalReason, şimdi + IdleAfterFatal)` + `IdleAfterFatal`; yoksa `IdleAfterPollerExit`; `ComputeBackoff` yalnız `StartAsync` fırlatırsa. Döngü çıkarken poller'ını `StopAsync` ile durdurup BİTMESİNİ bekler (bugünkü `:173-177`). Bütün beklemeler `Task.Delay(süre, time, ct)` ve oturum belirteciyledir (yan fark b); `şimdi` = `Time.GetUtcNow()`. 5 dk'lık beklemeler bu görevde düzdür (token'a bakış G11).
- **Yalıtım** (§8.5): tur gövdesi kendi `try / catch`'indedir; beklenmeyen istisna Error (`[InstagramChatHostedService]` öneki + PageId), `IdleAfterPollerExit`, döngü sürer. İstisna üst döngüye ve öbür hesaplara çıkmaz; üst döngünün kendi yakalaması (bugünkü `:194-200`) korunur.
- Çözümleyici PageId başına bir kez, hosted service'te kurulur (HttpClient'ı döngü başlamadan alınır) ve servis ömrünce yaşar: önbellek oturumlar arasında korunur (AH4). Poller'ın HttpClient'ı hesap döngüsünde her kurulumda alınır (bugünkü `:141-142`). Sahiplik sözlüğü hosted service'in alanıdır. `health` null ise servis kendi izleyicisini kurar (`new InstagramHealthMonitor(time)`; tek kod yolu), `time` null ise `TimeProvider.System`.
- **Günlük** (§8.8): izleyiciyi değiştiren her çağrı (`Report`, `SetAccount`, gözlemcinin tur olayları) ortak bir yardımcıdan geçer; yardımcı anlık görüntüdeki durumu çağrıdan önce ve sonra okur, değiştiyse geçiş satırını Information yazar. Tek istisna Fatal tur olayıdır: izleyici o an `RetryAt`'i temizler (G07), bu yüzden satır gözlemci çağrısında YAZILMAZ; olaydan önceki durum saklanır ve satır, poller bittikten sonraki `Report(pageId, Faulted, FatalReason, şimdi + IdleAfterFatal)` çağrısında `{From}` = saklanan durum ve `, retry at …` ekiyle yazılır (saklanan durum zaten `Faulted` ise satır yok). Kota satırı `Transient` dışındaki tur sonunda, hesap başına `UsageLogInterval`'da en çok bir kez yazılır (ilk tur hemen; sonra `Time.GetElapsedTime(sonYazım) >= UsageLogInterval` olunca), tek Sayfada da. Bugünkü `:137-139`, `:169-171`, `:184-186` satırları metinleriyle hesap döngüsüne taşınır, `:130-132`'nin yerini geçiş satırı alır; başka hiçbir satır `account ` ya da `usage ` ile başlamaz. Token, izleyici adı ve yorum metni hiçbir satırda yok.
- Poller gözlemcisi döngüye özel iç sınıftır (§11.3): tur olayını önce `Health.ObserverFor(pageId)`'ye, kulvar varsa sonra kulvara iletir. Poller'a süre dikişi EKLENMEZ (§8.3.1): yorum akıtan testler gerçek 1 sn'lik taban aralıkla koşar (test başına 1–3 sn).

- [ ] **Adım 1: Düzeneği ve düşen testleri yaz**

```csharp
// OrderDeck.Tests/TestHelpers/CapturingLoggerFactory.cs (ad alanı OrderDeck.Tests.TestHelpers): her kategoriye yazılanı tek listede toplar
internal sealed class CapturingLoggerFactory : ILoggerFactory   // CreateLogger: bütün düzeyler açık; AddProvider ve Dispose boş; eşzamanlı güvenli
{
    public sealed record Entry(string Category, LogLevel Level, string Message, string? Exception) { public string Full => Exception is null ? Message : Message + Environment.NewLine + Exception; }
    public IReadOnlyList<Entry> Entries { get; }   // anlık kopya; Message biçimlenmiş ileti, Exception istisnanın ToString'i
}

// OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.cs; using static OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;
/// <summary>Bir Instagram hesabının live_media ucunu betikler; eşzamanlı güvenli. Bekleyen üyelerin üst sınırı TestPatience.Upper.</summary>
internal sealed class InstagramLiveFeed
{
    public sealed record Comment(string Id, string Viewer, string Text);
    // graph.Map ile yolu "/{igUserId}/live_media" ile biten isteği üstlenir. Yanıt {"data":[{"id":<medya>,"comments":{"data":[…]}}]}, yorum damgası "yyyy-MM-ddTHH:mm:ss+0000". Başta tek tohum yorumu vardır (ilk çekimde yutulur; sonraki sayfa taşma sayılmaz).
    public InstagramLiveFeed(FakeGraph graph, string igUserId);
    public int Served { get; }   public int ServedFor(string accessToken);     // karşılanan istek sayısı (Intercept çağrılmadan ÖNCE artar; yanıtın yorum listesi sayaçla AYNI kilit altında dondurulur: sayaç arttıktan sonra Add edilen yorum o yanıta girmez) / o token'la gelenler
    public IReadOnlyCollection<string> Tokens { get; }                         // görülen farklı token'lar
    public Comment Add(string? viewer = null, string? text = null);            // kimlik, izleyici adı, metin (noktasız) üretilir; damga önceki yorum + 1 sn
    public string? UsageHeader { get; set; }   public bool NoBroadcast { get; set; }   // doluysa her olağan yanıta X-Business-Use-Case-Usage / true: {"data":[]}
    public Func<string, HttpResponseMessage?>? Intercept { get; set; }         // (istekteki token) → null değilse yanıt budur; fırlatırsa istisna poller'a çıkar
    public Task PrimedAsync();   public Task NextAsync();                      // Served ≥ 1 (ilk çekim yutuldu) / çağrıdan sonra bir istek daha karşılanana dek
    public static string TokenIn(Uri request);                                 // sorgudaki access_token ("" = yok)
    // {"<businessId>":[{"type":"instagram","call_count":…,"total_cputime":…,"total_time":…[,"estimated_time_to_regain_access":…]}]}; sayılar CultureInfo.InvariantCulture ile yazılır (ToString("0.##", CultureInfo.InvariantCulture) ya da JsonSerializer), enterpolasyonla DEĞİL: geliştirme makinesi tr-TR, "12,5" JSON'u bozar ve satır "header unreadable" olur
    public static string Usage(string businessId, double callCount, double cpuTime, double totalTime, double? regainMinutes = null);
}

public sealed partial class InstagramMultiAccountHostedServiceTests : IAsyncLifetime   // DisposeAsync: _service StopAsync + Dispose; sonra _db, _kit
{
    private const string AccountPrefix = "[InstagramChatHostedService] account ", UsagePrefix = "[InstagramChatHostedService] usage ";
    private static readonly TimeSpan Long = TestPatience.Upper * 4;    // dolması beklenmeyen süre: "iptal / kesildi" ile "süre doldu"yu ayırır
    private static readonly InstagramChatTimings Fast = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(1));
    // Alanlar: _kit (FacebookOAuthTestKit), _time (ManualTime), _logs (CapturingLoggerFactory), _bus (ChatBus; kurucuda _received'e abone), _received (ConcurrentQueue<ChatMessage>), _db (InMemorySqlite + MigrationRunner),
    // _sessions (StreamSessionService: SessionRepository(_db) + Mock<IClock>; MainShellTestHarness.cs:123-135 kalıbı), _health (InstagramHealthMonitor(_time): servisle AYNI saat), _oauth (_kit.CreateService()), _service (InstagramChatHostedService?).
    private async Task<(SeededPage Page, InstagramLiveFeed Feed)> OneAsync();                                       // SeedAsync() + besleme
    private async Task<(SeededPage A, SeededPage B, InstagramLiveFeed FeedA, InstagramLiveFeed FeedB)> TwoAsync();   // SeedAsync(1) + iki besleme
    // Oturum açar (kimliğini döner), servisi internal kurucuyla kurup başlatır: (_oauth, _bus, _logs, spamFilter: null, _sessions, httpFactory ?? _kit.HttpFactory, _health, _time, timings ?? Fast)
    private async Task<string> RunAsync(InstagramChatTimings? timings = null, IHttpClientFactory? httpFactory = null);
    private string StartSession();   private void EndSession(string id);      // _sessions.Start("Test", ["instagram"]).Id / _sessions.End(id)
    private static Task Until(Func<bool> condition);                          // 20 ms'de bir yoklar; TestPatience.Upper'da TimeoutException
    private async Task<InstagramLiveFeed.Comment> FlowAsync(InstagramLiveFeed feed);   // NextAsync (poller ilk çekimini yutmuş olur) + Add + bus'a varana dek bekler; yorumu döner
    private InstagramAccountHealth? Health(SeededPage page);   private InstagramAccountState? State(SeededPage page);   // _health.Snapshot() içinde PageId ile; yoksa null
    private bool Got(InstagramLiveFeed.Comment c);   private ChatMessage Message(InstagramLiveFeed.Comment c);          // _received içinde ExternalId == c.Id
    private int PollerStarts(SeededPage page);       // "polling live_media for IG user <IgUserId>" satırı sayısı (poller'ın RunAsync'inin ilk satırı; bugünkü InstagramLiveCommentsPoller.cs:142-143)
    private static bool IsResolve(Uri request, SeededPage page);   private int ResolveRequests(SeededPage page);   // yol "/<PageId>" ile biter, sorguda instagram_business_account geçer / _kit.Graph.Requests içindeki sayısı
    private IReadOnlyList<string> Lines(string prefix);            // _logs: Information ve üstü, iletisi prefix ile başlayanlar (yazım sırasıyla)
    private sealed class FailingOnce(IHttpClientFactory inner, int failCall) : IHttpClientFactory;   // failCall'uncu CreateClient'ta InvalidOperationException
    // TwoAsync; B'nin beslemesine intercept; RunAsync(Fast with { IdleAfterFatal = Long }); A'dan bir yorum akıtır (FlowAsync), settled(B) sağlanana dek bekler, A'nın Live olduğunu doğrular.
    private async Task<(SeededPage B, InstagramLiveFeed FeedB)> WithFaultyBAsync(Func<string, HttpResponseMessage?> intercept, Func<SeededPage, bool> settled);

    [Fact] public async Task Acik_kurucu_varsayilan_surelerle_izleyici_ve_oturum_servisi_olmadan_yoklar() {
        InstagramChatTimings.Default.Should().Be(new InstagramChatTimings(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)));
        var (_, feed) = await OneAsync();
        _service = new InstagramChatHostedService(_oauth, _bus, _logs, httpFactory: _kit.HttpFactory);
        await _service.StartAsync(default);
        await feed.PrimedAsync();                                                  // sessions null: oturum hep açık; izleyiciyi servis kurdu
    }
    [Fact] public async Task Baglanti_yokken_saglik_temizlenir_ve_graph_cagrilmaz() {
        _health.Configure([new FacebookLinkedPage(FacebookOAuthTestKit.NewId(), FacebookOAuthTestKit.NewToken(), FacebookOAuthTestKit.NewId())]);
        await RunAsync();
        await Until(() => _health.Snapshot().Count == 0);
        _kit.Graph.Requests.Should().BeEmpty();
    }
    [Fact] public async Task Iki_sayfa_iki_poller_ve_kaynak_hesap_etiketi_kullanici_adi_ya_da_sayfa_adi() {
        var (a, b, feedA, feedB) = await TwoAsync();
        _kit.Graph.AddPage(b.Page.PageId, b.Page.PageName, b.IgUserId);            // B'nin kullanıcı adı yanıtta yok
        await RunAsync();
        var c = await Task.WhenAll(FlowAsync(feedA), FlowAsync(feedB));
        Message(c[0]).SourceAccount.Should().Be("@" + a.IgUsername);
        Message(c[1]).SourceAccount.Should().Be(b.Page.PageName);                  // "@"sız
        (PollerStarts(a), PollerStarts(b)).Should().Be((1, 1));
    }
    [Fact] public async Task Tek_sayfada_kaynak_hesap_bos_kapi_yok_gozlemci_gecis_ve_dakikada_en_cok_bir_kota_satiri_yazar() {   // AH4, TK6
        var (page, feed) = await OneAsync();
        var (business, who) = (FacebookOAuthTestKit.NewId(), $"@{page.IgUsername} (page {page.Page.PageId}): ");
        feed.UsageHeader = InstagramLiveFeed.Usage(business, callCount: 12.5, cpuTime: 3, totalTime: 4);
        var session = await RunAsync();
        Message(await FlowAsync(feed)).SourceAccount.Should().BeNull();
        await feed.NextAsync();                                                                 // en az iki tur tamamlandı
        Lines(AccountPrefix).Should().Contain(AccountPrefix + who + "Starting -> Live");        // durum izleyiciye ulaştı (tek Sayfada da)
        Lines(UsagePrefix).Should().Equal(UsagePrefix + who + $"business {business} call_count 12.5% total_cputime 3% total_time 4% regain 0 min");   // satır tek
        _time.Advance(TimeSpan.FromMinutes(1));
        await Until(() => Lines(UsagePrefix).Count == 2);                                       // aralık dolunca ikincisi
        EndSession(session);
        await Until(() => State(page) == Stopped);
        Lines("[InstagramMergeGate]").Should().BeEmpty();
    }
    [Fact] public async Task Oturum_bitince_butun_hesaplar_durur_ve_arada_yazilan_yorum_basilmaz() {
        var (a, _, feedA, feedB) = await TwoAsync();
        var session = await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());
        EndSession(session);
        await Until(() => _health.Snapshot().All(h => h.State == Stopped));
        Lines("[InstagramMergeGate]").Should().NotBeEmpty();                       // kapı kapandı: özet satırı
        var gap = feedA.Add();                                                     // oturum yokken yazıldı
        StartSession();
        await Until(() => PollerStarts(a) == 2);
        await FlowAsync(feedA);                                                    // yeni poller bir tam aralık çalıştı
        Got(gap).Should().BeFalse();                                               // eski poller sürseydi bu arada basardı
    }
    [Fact] public async Task Bir_hesap_190_donerken_oteki_akar() {                        // AH2, AH3
        var (b, _) = await WithFaultyBAsync(_ => FakeGraph.Error(190), x => Health(x)?.RetryAt is not null);
        (Health(b)!.State, Health(b)!.Detail).Should().Be((Faulted, "Instagram bağlantını yenilemen gerekiyor."));
        Health(b)!.RetryAt.Should().Be(_time.GetUtcNow() + Long);
        await Until(() => Lines(AccountPrefix).Any(l => Regex.IsMatch(l, @"-> Faulted, retry at \d\d:\d\d:\d\d$")));   // kalıcı hatanın geçiş satırı yeniden deneme saatini taşır (§8.8)
    }
    [Fact] public async Task Bir_hesap_hiz_sinirindayken_oteki_etkilenmez() {
        var (b, _) = await WithFaultyBAsync(_ => FakeGraph.Error(4), x => State(x) == RateLimited);
        Health(b)!.RetryAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromMinutes(1));   // başlık yokken poller'ın varsayılan beklemesi
    }
    [Fact] public async Task Bir_hesabin_istegi_istisna_atarken_oteki_akar() {
        var (b, feedB) = await WithFaultyBAsync(_ => throw new HttpRequestException(), _ => true);
        await Until(() => feedB.Served >= 2);                                      // B yeniden deniyor
        State(b).Should().Be(Starting);                                            // geçici hata durumu değiştirmez
    }
    [Fact] public async Task Instagram_hesabi_olmayan_sayfa_otekini_engellemez() {
        var (a, b, feedA, _) = await TwoAsync();
        _kit.Graph.AddPage(b.Page.PageId, b.Page.PageName);                        // ek Sayfa Instagram'sız
        await RunAsync(Fast with { IdleAfterFatal = Long });
        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername);   // iki Sayfa + tek hesap: etiket dolu
        await Until(() => State(b) == NotLinked);
        Health(b)!.RetryAt.Should().Be(_time.GetUtcNow() + Long);                  // TK15
        Lines(UsagePrefix).Should().Equal($"{UsagePrefix}@{a.IgUsername} (page {a.Page.PageId}): X-Business-Use-Case-Usage header absent");
        Lines(AccountPrefix).Should().Contain(l => Regex.IsMatch(l, @"^\[InstagramChatHostedService\] account .+ \(page " + Regex.Escape(b.Page.PageId) + @"\): Starting -> NotLinked, retry at \d\d:\d\d:\d\d$"));
    }
    [Fact] public async Task Ayni_instagram_hesabina_cozulen_iki_sayfada_tek_poller_ve_atlanan_girdi() {
        var (a, b, feedA, _) = await TwoAsync();
        _kit.Graph.AddPage(b.Page.PageId, b.Page.PageName, a.IgUserId, a.IgUsername);
        await RunAsync(Fast with { IdleAfterFatal = Long });
        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername);
        await Until(() => _health.Snapshot().Count(h => h.State == Skipped) == 1);
        _health.Snapshot().Should().HaveCount(2);
        feedA.Tokens.Should().ContainSingle();                                     // tek poller: tek Sayfa token'ı
        var skipped = _health.Snapshot().Single(h => h.State == Skipped);
        skipped.RetryAt.Should().Be(_time.GetUtcNow() + Long);
        _logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.StartsWith("[InstagramChatHostedService]") && e.Message.Contains(skipped.PageId));
    }
    [Fact] public async Task Oturum_bitince_bes_dakikalik_bekleme_iptal_olur_yeni_oturum_hemen_dener_ve_cozumleme_yinelenmez() {
        var (page, feed) = await OneAsync();
        var session = await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => State(page) == Live);
        feed.Intercept = _ => FakeGraph.Error(190);
        await Until(() => Health(page)?.RetryAt is not null);                      // kalıcı hata beklemesinde (Long)
        EndSession(session);
        await Until(() => State(page) == Stopped);                                 // bekleme oturumla iptal oldu
        (feed.Intercept, feed.NoBroadcast) = (null, true);
        StartSession();
        await Until(() => State(page) == WaitingForBroadcast);                     // Long dolmadan denedi; BroadcastEnded değil: ResetSession çağrıldı
        ResolveRequests(page).Should().Be(1);                                      // AH4: önbellek oturumlar arasında korunur
    }
    [Fact] public async Task Hesap_dongusundeki_beklenmeyen_istisna_dongude_kalir_ve_dongu_surer() {
        var (page, _) = await OneAsync();
        await RunAsync(httpFactory: new FailingOnce(_kit.HttpFactory, failCall: 2));   // 1: çözümleyici (hosted service), 2: poller (hesap döngüsü)
        await Until(() => State(page) == Live);
        _logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Message.StartsWith("[InstagramChatHostedService]") && e.Message.Contains(page.Page.PageId));
    }
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMultiAccountHostedServiceTests"`
Beklenen: FAIL (derleme hatası: `InstagramChatTimings` yok; `InstagramChatHostedService`'in `health` / `time` parametreleri ve dokuz parametreli kurucusu yok)

- [ ] **Adım 3: `InstagramChatTimings`, iki kurucu ve üst döngüyü uygula (`InstagramChatHostedService.cs`)**

Public kurucu `InstagramChatTimings.Default` ile `internal` kurucuya zincirlenir; `Default` bugünkü üç sabitten (`:27-29`) ve iki yeni sabitten (2 sn, 1 dk) kurulur. `RunAsync` "Kurallar"daki üst döngü ve oturum sonu sırasıyla yeniden yazılır.

- [ ] **Adım 4: `InstagramAccountLoop` ve `InstagramLoopServices` uygula (`InstagramAccountLoop.cs`)**

"Kurallar"daki hesap döngüsü, yalıtım ve günlük maddeleri; geçiş ve kota satırları Produces'taki üç şablonla yazılır.

- [ ] **Adım 5: Testlerin geçtiğini gör**

Koş: Adım 2'deki komut
Beklenen: PASS

- [ ] **Adım 6: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMultiAccountHostedServiceTests|FullyQualifiedName~InstagramChatHostedServiceTests|FullyQualifiedName~InstagramLiveCommentsPollerTests|FullyQualifiedName~InstagramMergeGateTests"`
Beklenen: PASS, 0 uyarı (`InstagramChatHostedServiceTests` DEĞİŞMEDEN; `AppHost.cs:303-310` değişmeden derlenir)

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs OrderDeck.Chat/Ingestors/Instagram/InstagramAccountLoop.cs OrderDeck.Tests/TestHelpers/CapturingLoggerFactory.cs OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.cs
git commit -F - <<'MSG'
feat(instagram): bağlı her Sayfayı kendi hesap döngüsünde yokla

Bir hesabın kalıcı hatası, hız sınırı ya da çökmesi yalnız o hesabı bekletsin.
İki ve üzeri Sayfada yorumlar kaynak hesabını taşır; tek Sayfada yol aynıdır.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G11: Hosted service: token'a bakan 5 dakikalık beklemeler (TK16)

**Spec:** §8.3.2 hesap döngüsü girişi (önbellek kuralı) ve 6. adım, "Bilerek değişen davranışlar" 2, yan fark d; §8.3.3 "Token değişimi" son cümle; §8.5 `NotLinked` maddesi ("token değiştikten sonraki ilk çözümleme"); §9.3 (iki madde); §11.3 ("token değişti" bilgisinin iletimi); TK16; R7, R8.
**Bağımlı:** G10, G04.

**Files:**
- Modify: `OrderDeck.Chat/Ingestors/Instagram/InstagramAccountLoop.cs` (kurucu; G10'un `RunAsync`'inde 1. adımın ardı ve iki `IdleAfterFatal` beklemesi: çözümleme sonrası ve `FatalReason` sonrası; `Skipped` beklemesi DEĞİL)
- Modify: `OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs` (sahiplik sözlüğü alanının yanına `resolvedTokens` alanı; `InstagramAccountLoop` kurma çağrısı)
- Test: Create `OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.TokenChange.cs`

**Interfaces:**
- Consumes: G10 `InstagramAccountLoop(string pageId, InstagramLoopServices services, InstagramAccountResolver resolver, ConcurrentDictionary<string, string> owners, InstagramMergeGate? gate)` (bu görev imzayı genişletir; `InstagramLoopServices.OAuth`, `.Time`, `.Timings`), hosted service'in sahiplik sözlüğü alanı ve döngü kurma çağrısı, `InstagramChatTimings.IdleAfterFatal` / `.WatchInterval`, test düzeneği (`OneAsync`, `RunAsync`, `StartSession`, `EndSession`, `Until`, `State`, `Health`, `IsResolve`, `ResolveRequests`, `Fast`, `Long`; `InstagramLiveFeed.Intercept`, `ServedFor`, `NextAsync`, `TokenIn`); G02 `Task<IReadOnlyList<FacebookLinkedPage>> GetConnectedPagesAsync(CancellationToken ct = default)`; G04 kiti `Task<FacebookReauthorizeResult> ReauthorizeAllAsync(FacebookOAuthService service)` (bağlı her Sayfaya yeni token yazar); mevcut `void InstagramAccountResolver.Invalidate()` (bugünkü `InstagramAccountResolver.cs:104-108`; çağıranı yok).
- Produces: yeni dış imza yok. `InstagramAccountLoop` kurucusu `owners`'tan sonra bir parametre kazanır (G12 bu imzayı tüketir): `internal InstagramAccountLoop(string pageId, InstagramLoopServices services, InstagramAccountResolver resolver, ConcurrentDictionary<string, string> owners, ConcurrentDictionary<string, string> resolvedTokens, InstagramMergeGate? gate)` (`resolvedTokens`: PageId → o Sayfanın çözümleyicisine en son verilen Sayfa token'ı; hosted service'in alanıdır, çözümleyicilerle aynı ömürde yaşar; hiçbir yere loglanmaz). İç yardımcı: `private Task<bool> WaitUnlessTokenChangedAsync(TimeSpan wait, string usedToken, CancellationToken ct)` (`true` = bekleme atlandı ya da kesildi).

**Kurallar:**
- Yalnız İKİ bekleme token'a bakar: çözümleme beklemesi (`NotLinked` / `Failed`) ve kalıcı hata beklemesi. `Skipped` beklemesi, `IdleAfterPollerExit` ve `ComputeBackoff` beklemeleri DEĞİŞMEZ.
- Karşılaştırma tabanı (`usedToken`) o turun 1. adımında okunup çözümlemeye ya da poller'a verilen token'dır. Bekleme başlarken saklı token ondan farklıysa bekleme ATLANIR; bekleme sırasında farklılaşırsa KESİLİR (§8.3.2 6. adım).
- (karar, §11.3) Ayrı sinyal yok: bekleme `WatchInterval` adımlarına bölünür ve her adımdan önce saklı token `GetConnectedPagesAsync`'ten (bellek okuması) yeniden okunur. Kalan süre adımlar toplanarak sayılır, saat okunarak değil: testteki `ManualTime` kendiliğinden ilerlemez, saatle ölçülen bekleme hiç bitmez. Son adım kalan süre kadardır.
- Sayfa o sırada listeden çıkmışsa yardımcı `true` döner; döngü 1. adımda biter. `true` dönünce döngü 1. adıma döner; yardımcı önbelleğe dokunmaz.
- **Önbelleği boşaltan TEK kural** (bekleme yolu da buna iner): tur, 1. adımda okuduğu token'ı `resolvedTokens[pageId]` ile karşılaştırır. Kayıt var ve farklıysa çözümlemeden ÖNCE `resolver.Invalidate()` çağrılır; her çözümleme çağrısından önce kayıt o turun token'ıyla yazılır (kayıt yoksa, yani ilk çözümlemede, boşaltma yok). Sözlük oturumlar ve döngü örnekleri arasında korunduğu için token beklemeye hiç girilmeyen yollarda değişse de (poller sağlıklıyken ya da oturum dışında değişip sonraki oturumda, 5 geçici hata + `IdleAfterPollerExit` sonrasında, G12'nin devrinde ya da liste yeniden dolunca kurulum) yeni token'la ilk iş hesabın BİR KEZ yeniden çözülmesidir (tek Graph isteği, yan fark d; izin güncellemede Instagram hesabının işareti kalktıysa hesap poller hatasıyla değil `NotLinked` ile görünür, §8.5). Token aynıysa istek eklenmez (AH4; G10'un `…cozumleme_yinelenmez` kilidi aynen geçer).
- Çalışan sağlıklı poller'a dokunulmaz. Ağın düzelmesi beklemeyi kesmez (R8). Oturum belirteci beklemeyi G10'daki gibi iptal eder.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.TokenChange.cs; using static OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState; (yönerge dosyaya özeldir, burada da yazılır)
public sealed partial class InstagramMultiAccountHostedServiceTests
{
    private async Task<string> StoredTokenAsync(SeededPage page);   // _oauth.GetConnectedPagesAsync() içinde o PageId'nin PageAccessToken'ı

    [Fact] public async Task Kalici_hata_beklemesi_token_degisince_kesilir_ve_hesap_yeniden_cozulur() {
        var (page, feed) = await OneAsync();
        var old = page.Page.PageAccessToken;
        feed.Intercept = token => token == old ? FakeGraph.Error(190) : null;
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => Health(page)?.RetryAt is not null);                      // kalıcı hata beklemesinde
        ResolveRequests(page).Should().Be(1);
        await _kit.ReauthorizeAllAsync(_oauth);
        var fresh = await StoredTokenAsync(page);
        fresh.Should().NotBe(old);
        await Until(() => feed.ServedFor(fresh) >= 1);                             // Long dolmadan yeni token'la kuruldu
        ResolveRequests(page).Should().Be(2);                                      // önbellek boşaltıldı: tek ek çözümleme
        await Until(() => State(page) == Live);
    }
    [Fact] public async Task Cozumleme_beklemesi_token_degisince_kesilir() {
        var (page, _) = await OneAsync();
        var old = page.Page.PageAccessToken;
        _kit.Graph.Map(u => IsResolve(u, page) && InstagramLiveFeed.TokenIn(u) == old, _ => FakeGraph.Error(1, HttpStatusCode.InternalServerError));
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => State(page) == ResolveFailed);
        await _kit.ReauthorizeAllAsync(_oauth);
        await Until(() => State(page) == Live);                                    // Long dolmadan çözüldü ve yoklanıyor
    }
    [Fact] public async Task Poller_calisirken_token_degisir_sonra_eski_token_duserse_bekleme_atlanir() {
        var (page, feed) = await OneAsync();
        var old = page.Page.PageAccessToken;
        await RunAsync(Fast with { IdleAfterFatal = Long, WatchInterval = Long });  // adım da uzun: "kesildi" değil "atlandı"
        await Until(() => State(page) == Live);
        await _kit.ReauthorizeAllAsync(_oauth);
        var fresh = await StoredTokenAsync(page);
        await feed.NextAsync();
        feed.ServedFor(fresh).Should().Be(0);                                      // sağlıklı poller eski token'la sürüyor
        feed.Intercept = token => token == old ? FakeGraph.Error(190) : null;      // eski token düştü
        await Until(() => feed.ServedFor(fresh) >= 1);                             // ne 5 dk ne bir adım beklendi
        await Until(() => State(page) == Live);
    }
    [Fact] public async Task Token_bekleme_disinda_degisince_sonraki_kurulumda_hesap_bir_kez_yeniden_cozulur() {
        var (page, feed) = await OneAsync();
        var session = await RunAsync();
        await Until(() => State(page) == Live);
        await _kit.ReauthorizeAllAsync(_oauth);                                    // poller sağlıklı: iki beklemeye de girilmedi
        var fresh = await StoredTokenAsync(page);
        EndSession(session);
        await Until(() => State(page) == Stopped);
        StartSession();
        await Until(() => feed.ServedFor(fresh) >= 1);                             // yeni oturum yeni token'la kuruldu
        ResolveRequests(page).Should().Be(2);                                      // token değişti: hesap bir kez yeniden çözüldü (token aynıyken 1 kalır, G10)
    }
    [Fact] public async Task Token_degismezse_bekleme_suresi_dolunca_yeniden_denenir() {
        var (page, _) = await OneAsync();
        _kit.Graph.AddPage(page.Page.PageId, page.Page.PageName);                  // Instagram'sız
        await RunAsync();                                                          // IdleAfterFatal 200 ms, adım 50 ms; ManualTime ilerletilmez
        await Until(() => State(page) == NotLinked);
        _kit.Graph.AddPage(page.Page.PageId, page.Page.PageName, page.IgUserId, page.IgUsername);
        await Until(() => State(page) == Live);                                    // bekleme adım toplamıyla bitti
    }
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMultiAccountHostedServiceTests.Kalici_hata_beklemesi"`
Beklenen: FAIL (`TimeoutException: koşul 30 sn'de sağlanmadı`: bekleme token değişimini görmüyor). İlk üç test böyle düşer (her biri 30 sn bekler, tek test yeter); `Token_bekleme_disinda_…` birkaç saniyede doğrulamada düşer (çözümleme isteği 2 beklenirken 1: önbellek boşaltılmıyor); sonuncusu (`Token_degismezse…`) G10 kodunda da geçer ve adımlı beklemenin süre dolunca bittiğini kilitler.

- [ ] **Adım 3: `WaitUnlessTokenChangedAsync(TimeSpan wait, string usedToken, CancellationToken ct)` uygula ve iki beklemeye bağla; `resolvedTokens`'ı ve tur başı karşılaştırmasını ekle (`InstagramAccountLoop.cs`, `InstagramChatHostedService.cs`)**

Yardımcı "Kurallar"daki adımlı beklemedir; çözümleme beklemesi ile kalıcı hata beklemesi onu o turun token'ıyla çağırır, `true` dönerse tur baştan başlar. Önbelleği yalnız 1. adımın ardına eklenen karşılaştırma boşaltır ("Kurallar"daki tek kural). `resolvedTokens` hosted service'te sahiplik sözlüğünün yanında `ConcurrentDictionary<string, string>` alanıdır ve döngü kurulurken `owners`'tan sonra verilir.

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMultiAccountHostedServiceTests"`
Beklenen: PASS (iki dosya)

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMultiAccountHostedServiceTests|FullyQualifiedName~InstagramChatHostedServiceTests|FullyQualifiedName~InstagramAccountResolverTests"`
Beklenen: PASS, 0 uyarı

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramAccountLoop.cs OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.TokenChange.cs
git commit -F - <<'MSG'
feat(instagram): beş dakikalık beklemeleri token değişince kes

İzin güncellemesinden sonra bozuk hesap 5 dakika beklemesin: çözümleme ve
kalıcı hata beklemeleri saklı token'a bakar, token o turda kullanılandan
farklıysa bekleme atlanır ya da kesilir. Token hangi yolda değişirse değişsin
sonraki kurulumda hesap bir kez yeniden çözülür (çözümleyici önbelleği boşaltılır).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G12: Hosted service: 2 saniyelik izleme, oturum içi ekleme / çıkarma, tekten çoka devir

**Spec:** §8.3.2 üst döngü 4. adım, "Bilerek değişen davranışlar" 1; §8.3.3 (altı madde); §8.3.1 filigran maddesi; §9.3 (yedi madde); TK14; R7, R13.
**Bağımlı:** G11, G08 (yalnız test).

**Files:**
- Modify: `OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs` (G10'un `RunAsync`'indeki "oturum kaynağı iptal olana dek beklenir" bölgesi ve oturum sonu toplaması), `OrderDeck.Chat/Ingestors/Instagram/InstagramAccountLoop.cs` (`AttachGate`; `RunAsync`'te poller kurulumu ve 5. adımın beklemesi)
- Test: Create `OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.MidSession.cs`

**Interfaces:**
- Consumes: G10 / G11 `InstagramAccountLoop(string pageId, InstagramLoopServices services, InstagramAccountResolver resolver, ConcurrentDictionary<string, string> owners, ConcurrentDictionary<string, string> resolvedTokens, InstagramMergeGate? gate)` (son iki sözlük hosted service'in alanlarıdır; G11: tur, 1. adımda okuduğu token `resolvedTokens`'takinden farklıysa hesabı bir kez yeniden çözer), `Task RunAsync(CancellationToken ct)`, `InstagramChatTimings.WatchInterval`, test düzeneği (`ResolveRequests`, `StartSession`, `EndSession` dahil); G02 `Task<FacebookLinkedPage> AddExtraPageAsync(string pageId, string pageName, CancellationToken ct = default)`, `Task<bool> RemoveExtraPageAsync(string pageId, CancellationToken ct = default)`, `GetConnectedPagesAsync`; mevcut `Task DisconnectAsync(CancellationToken ct = default)`, `EncryptedFacebookTokenStore.LoadAsync` / `SaveAsync` (kit `Store`); G04 kiti `ReauthorizeAllAsync(FacebookOAuthService service)`; G06 poller'ın `watermark` parametresi; G07 `Configure`, `StopAll`; G09 `AddLane(string accountKey, string label)`, `Lane.Complete()`, `Dispose()`; G08 (test) `static InstagramHealthView InstagramHealthPresenter.Evaluate(IReadOnlyList<InstagramAccountHealth> snapshot, DateTimeOffset now, bool sessionActive)`, `InstagramHealthView(IReadOnlyList<InstagramAccountStatusViewModel> Rows, bool ShowStrip, bool AnyAccountLive, string? TopDotTooltip)`, satırın `StateText`'i.
- Produces: yeni dış imza yok. İç: `internal void InstagramAccountLoop.AttachGate(InstagramMergeGate gate)`.

**Kurallar:**
- **İzleme:** oturum açıkken `WatchInterval`'da bir `GetConnectedPagesAsync` okunur (ağ yok) ve PageId kümesi çalışan döngülerle karşılaştırılır; aynı turda hem çıkan hem eklenen varsa önce çıkarmalar işlenir. Her döngünün kendi iptal kaynağı vardır, oturum kaynağına bağlıdır (§8.3.2 üst döngü 5. adım). Güncel liste boş değilken küme her değiştiğinde, çıkarmalar bittikten SONRA ve yeni döngüler başlamadan ÖNCE `Configure(güncel liste)` bir kez çağrılır (çıkarma, ekleme, 1 → 2 ve yeniden dolma; liste boşaldıysa çağrılmaz, aşağıda).
- **Çıkarma:** yalnız o döngü iptal edilir ve BİTMESİ beklenir (kulvarı `Complete()` olur, sahipliği bırakılır), sonra `Configure(güncel liste)` girdiyi siler. Testler, girdi silindiğinde o hesabın isteğinin kalmadığına yaslanır.
- **Ekleme** (kapı kuruluyken): `Configure(güncel liste)`, yalnız yeni döngü başlar; ötekilerin poller'ına ve filigranına dokunulmaz.
- **1 → 2** (karar: ayrım kapının kurulu olmamasına göredir; kapı kurulu değil ve güncel Sayfa sayısı ≥ 2), sırayla: `Configure(güncel liste)` (yeni Sayfanın girdisi döngüsü başlamadan eklenir; izleyici bilinmeyen `pageId`'yi sessizce yok saydığı için girdisiz başlayan hesap şeritte, üst noktada ve günlükte hiç görünmezdi) → kapı kurulur → çalışan her döngüye `AttachGate(gate)` → yeni döngüler kapıyla başlar. Kapı kuruluyken gelen her ekleme "ekleme" kuralıyla işlenir (kulvarlı poller yeniden kurulmaz).
- **`AttachGate`** (üst döngünün iş parçacığından çağrılır; eşzamanlı güvenli): poller çalışıyorsa durdurulur ve döngü 5. adımın beklemesini ATLAYARAK aynı filigranla, kulvar üstünden yeniden kurar (30 sn uygulanmaz; ilk çekim yutulmaz); poller çalışmıyorsa (çözümleme ya da bekleme) yalnız sonraki kurulum kulvarı kullanır. Filigran yalnız bu devirde aktarılır, eski poller durduktan sonra; öbür her kurulumda tazedir.
- **Çoktan teke dönülmez:** kapı liste boşalana ya da oturum bitene dek yaşar; kalan hesap kulvarıyla, etiketli sürer. Kapı oturumu aşmaz: sonraki oturum Sayfa sayısına yeniden bakar, tek Sayfaysa kapısız ve etiketsiz kurulur (§8.3.3 "tek hesap kipine bir sonraki oturumda dönülür"; TK6).
- **Liste boşalırsa:** bütün döngüler iptal edilir ve beklenir → kapı `Dispose()` → `StopAll()` (`Configure` ve `Clear` YOK: girdiler silinmez). Üst döngü `IdleWhenOffline`'a DÜŞMEZ, izlemede kalır. Yeniden dolunca `Configure(liste)` (`ResetSession` YOK: aynı Sayfa "bu oturumda canlıydı"yı korur, farklı Sayfanın girdisi yenidir), Sayfa sayısına göre kapı, döngüler.
- **Token değişimi** (PageId kümesi aynı): izleme hiçbir şey yapmaz; sağlıklı poller eski token'la sürer, yeni token sonraki doğal kurulumda kullanılır (o kurulumda G11'in tur başı kuralı hesabı bir kez yeniden çözer; bu görev ona dokunmaz). Oturum sonu G10'daki sırayla.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.MidSession.cs; using OrderDeck.App.Services.Instagram; (G08'in ad alanı) ve
// using static OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState; (yönerge dosyaya özeldir, burada da yazılır)
public sealed partial class InstagramMultiAccountHostedServiceTests
{
    // Kimlik ve adları üretir; Graph'a Sayfa + Instagram hesabı tanıtır, beslemesini bağlar, SONRA _oauth.AddExtraPageAsync ile ekler.
    private async Task<(SeededPage Page, InstagramLiveFeed Feed)> AddPageAsync();
    // Aynı Sayfayı yeniden bağlar: demeti (pageToken verilirse o Sayfa token'ıyla) _kit.Store'a yazar; bağlı olmayan servis dosyayı izleme turunda görür.
    private Task ReconnectAsync(FacebookTokenBundle saved, string? pageToken = null);
    private InstagramHealthView View();   // InstagramHealthPresenter.Evaluate(_health.Snapshot(), _time.GetUtcNow(), sessionActive: true)

    [Fact] public async Task Kaldirilan_hesabin_istekleri_kesilir_oteki_kesintisiz_surer() {
        var (a, b, feedA, feedB) = await TwoAsync();
        await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());
        (await _oauth.RemoveExtraPageAsync(b.Page.PageId)).Should().BeTrue();
        await Until(() => Health(b) is null);                                      // döngü bitti, girdi silindi
        var served = feedB.Served;
        await FlowAsync(feedA); await FlowAsync(feedA);                            // A en az iki tur daha attı, yorumları aktı
        feedB.Served.Should().Be(served);
        PollerStarts(a).Should().Be(1);
    }
    [Fact] public async Task Ucuncu_sayfa_eklenince_ilk_iki_hesabin_filigrani_bozulmaz() {
        var (a, b, feedA, feedB) = await TwoAsync();
        await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());
        var (_, feedC) = await AddPageAsync();
        await feedC.PrimedAsync();                                                 // yalnız yeni döngü başladı
        await Task.WhenAll(FlowAsync(feedA), FlowAsync(feedB));
        (PollerStarts(a), PollerStarts(b)).Should().Be((1, 1));                    // ilk çekimleri yeniden yutulmadı
        _health.Snapshot().Should().HaveCount(3);
    }
    [Fact] public async Task Tekten_coka_geciste_yorum_kaybolmaz_ve_otuz_saniye_beklenmez() {
        var (a, feedA) = await OneAsync();
        await RunAsync(Fast with { IdleAfterPollerExit = Long });
        await feedA.PrimedAsync();
        await feedA.NextAsync();                                                   // eski poller az önce yanıt aldı; sıradaki isteğine ~1 sn var
        var during = feedA.Add();                                                  // ilk kez yeni poller'ın ilk çekiminde görünür
        var (b, _) = await AddPageAsync();
        await Until(() => PollerStarts(a) == 2 && Got(during));                    // Long beklenmedi; devredilen filigranla ilk çekim yutulmadı
        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername);   // artık kulvardan
        _received.Count(m => m.ExternalId == during.Id).Should().Be(1);
        await Until(() => State(b) == Live);                                       // 1 → 2'de de Configure çağrıldı: yeni Sayfanın girdisi var ve canlı
        _health.Snapshot().Select(h => h.PageId).Should().Equal(a.Page.PageId, b.Page.PageId);
    }
    [Fact] public async Task Liste_bosalip_dolunca_girdi_starting_olur_ve_yanit_yok_45_saniye_sonra_gorunur() {
        var (page, feed) = await OneAsync();
        await RunAsync();
        await Until(() => State(page) == Live);                                    // son temas şimdi
        var saved = (await _kit.Store.LoadAsync())!;
        await _oauth.DisconnectAsync();
        await Until(() => State(page) == Stopped);
        _time.Advance(TimeSpan.FromSeconds(60));                                   // önceki temas 60 sn eskidi
        feed.Intercept = _ => FakeGraph.Error(1, HttpStatusCode.InternalServerError);   // yeni bağlantı yalnız geçici hata veriyor
        await ReconnectAsync(saved);
        await Until(() => State(page) == Starting);
        View().Rows.Single().StateText.Should().Be("bağlanıyor");                  // önceki temas yüzünden anında "yanıt yok" değil
        _time.Advance(TimeSpan.FromSeconds(46));
        View().Rows.Single().StateText.Should().Be("yanıt yok (46 sn)");
        View().ShowStrip.Should().BeTrue();                                        // "bu oturumda canlıydı" korundu: tek Sayfada da şerit çıkar (R13)
    }
    [Fact] public async Task Token_degisince_saglikli_hesabin_istekleri_kesilmez_ve_ilk_cekimi_yeniden_yutulmaz() {
        var (page, feed) = await OneAsync();
        await RunAsync();
        await feed.PrimedAsync();
        await _kit.ReauthorizeAllAsync(_oauth);
        await FlowAsync(feed);
        PollerStarts(page).Should().Be(1);
        feed.Tokens.Should().Equal(page.Page.PageAccessToken);                     // eski token'la sürüyor
    }
    [Fact] public async Task Liste_bosalinca_istekler_kesilir_dolunca_izleme_araliginda_baslar() {
        var (page, feed) = await OneAsync();
        var old = page.Page.PageAccessToken;
        await RunAsync(Fast with { IdleWhenOffline = Long });
        await Until(() => State(page) == Live);
        var saved = (await _kit.Store.LoadAsync())!;
        await _oauth.DisconnectAsync();
        await Until(() => State(page) == Stopped);                                 // girdi silinmedi
        var served = feed.ServedFor(old);
        feed.NoBroadcast = true;
        var fresh = FacebookOAuthTestKit.NewToken();
        await ReconnectAsync(saved, fresh);
        await Until(() => State(page) == BroadcastEnded);                          // Long beklenmedi; "bu oturumda canlıydı" korundu (ResetSession yok)
        feed.ServedFor(fresh).Should().BeGreaterThan(0);
        feed.ServedFor(old).Should().Be(served);                                   // boşalmadan sonra eski bağlantıyla tek istek yok
        ResolveRequests(page).Should().Be(2);                                      // yeni token: hesap bir kez yeniden çözüldü (G11'in tur başı kuralı)
    }
    [Fact] public async Task Ek_sayfa_kaldirilinca_kalan_hesap_etiketli_surer_ve_yeniden_eklemede_yeniden_kurulmaz() {
        var (a, b, feedA, feedB) = await TwoAsync();
        await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());
        await _oauth.RemoveExtraPageAsync(b.Page.PageId);
        await Until(() => Health(b) is null);
        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername);   // çoktan teke dönülmez
        var served = feedB.Served;
        await _oauth.AddExtraPageAsync(b.Page.PageId, b.Page.PageName);            // kapı kuruluyken ekleme
        await Until(() => feedB.Served > served);
        PollerStarts(a).Should().Be(1);                                            // kulvarlı poller yeniden kurulmadı
    }
    [Fact] public async Task Ek_sayfa_kaldirildiktan_sonraki_oturum_tek_hesap_kipinde_etiketsiz_kurulur() {   // §8.3.3 "çoktan teke" ikinci yarı; TK6
        var (a, b, feedA, feedB) = await TwoAsync();
        var session = await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());
        await _oauth.RemoveExtraPageAsync(b.Page.PageId);
        await Until(() => Health(b) is null);
        EndSession(session);
        await Until(() => State(a) == Stopped);
        StartSession();
        Message(await FlowAsync(feedA)).SourceAccount.Should().BeNull();           // kapı önceki oturumdan taşınmadı: tek Sayfa, etiket yok
    }
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMultiAccountHostedServiceTests.Kaldirilan_hesabin"`
Beklenen: FAIL (`TimeoutException: koşul 30 sn'de sağlanmadı`: üst döngü listeyi izlemiyor). `Token_degisince…` dışındaki yedi test bu görevden önce düşer; her biri 30 sn beklediği için tek test yeter.

- [ ] **Adım 3: `internal void AttachGate(InstagramMergeGate gate)` ve filigran devrini uygula (`InstagramAccountLoop.cs`)**

Poller döngüye özel bir iptal kaynağıyla (hesap kaynağına bağlı) kurulur; `AttachGate` kapıyı saklar, poller çalışıyorsa "devir" işaretini koyup o kaynağı iptal eder; döngü işareti görünce filigranı saklayıp beklemeden tura baştan başlar.

- [ ] **Adım 4: İzleme döngüsünü uygula (`InstagramChatHostedService.cs`)**

G10'un "oturum kaynağı iptal olana dek beklenir" bölgesi `Task.Delay(WatchInterval, time, oturum belirteci)` döngüsü olur; çalışan döngüler PageId → (döngü, iptal kaynağı, görev) sözlüğünde tutulur, fark "Kurallar"daki dallara göre işlenir.

- [ ] **Adım 5: Testlerin geçtiğini gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMultiAccountHostedServiceTests"`
Beklenen: PASS (üç dosya)

- [ ] **Adım 6: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramMultiAccountHostedServiceTests|FullyQualifiedName~InstagramChatHostedServiceTests|FullyQualifiedName~InstagramMergeGateTests|FullyQualifiedName~InstagramHealthPresenterTests"`
Beklenen: PASS, 0 uyarı

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.Chat/Ingestors/Instagram/InstagramChatHostedService.cs OrderDeck.Chat/Ingestors/Instagram/InstagramAccountLoop.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramMultiAccountHostedServiceTests.MidSession.cs
git commit -F - <<'MSG'
feat(instagram): oturum içinde hesap ekleme ve çıkarmayı izle

Bağlı Sayfa listesi 2 saniyede bir bellekten okunur: eklenen hesap birkaç
saniyede yoklanır, kaldırılanın yoklaması durur, ötekilere dokunulmaz. Tekten
çoka geçişte çalışan hesap filigranıyla ve beklemeden kulvara taşınır.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G13: Günlük denetimi (§8.8)

**Spec:** §8.8 ("Kişisel veri yok", "Token yok", yeni satırlar, önekler); §8.1 (`ToString`); §8.2 "Hata metni düzeltmesi"; CLAUDE.md (depo PUBLIC, sır hijyeni).
**Bağımlı:** G04, G09, G12.

**Files:**
- Test: Create `OrderDeck.Tests/Chat/Instagram/InstagramLoggingAuditTests.cs`
- Modify: yalnız denetim bir satır yakalarsa, o satırı yazan üretim dosyası (düzeltme bu görevin diff'inde görünür)

**Interfaces:**
- Consumes: G10 `CapturingLoggerFactory` (`Entries`; `Entry(Category, Level, Message, Exception)`, `Full`), `InstagramLiveFeed`, `InstagramChatTimings`, hosted service'in `internal` kurucusu, günlük şablonları; kit (`SeedAsync`, `CreateService(ILogger<FacebookOAuthService>? log = null, TimeSpan? callbackTimeout = null)`, `Graph`, `Store`, `HttpFactory`, `ReauthorizeAllAsync(FacebookOAuthService service)`, `NewId`, `NewToken`); G02 `AddExtraPageAsync(string pageId, string pageName, CancellationToken ct = default)`, `RemoveExtraPageAsync(string pageId, CancellationToken ct = default)`; G04 `ReauthorizeAsync(Func<IReadOnlyList<FacebookLinkedPage>, CancellationToken, Task<bool>> confirmMissing, CancellationToken ct = default)`; G03 zaman aşımının `TimeoutException`'ı; G01 iki `ToString`; G07 `ManualTime`.
- Produces: yok (denetim testi).

**Kurallar:**
- Hiçbir düzeyde token ve `access_token=` taşıyan adres (`paging.next` dahil) yok: ileti ve istisna metni birlikte taranır (`Entry.Full`).
- Information ve üstünde izleyici kullanıcı adı ve yorum metni yok; süzgeçten düşen yorum yalnız kimliğiyle ve Debug'da (poller'ın `Publish`'i; bugünkü `InstagramLiveCommentsPoller.cs:308`).
- Information ve üstündeki satırlar kategorilerine göre önek taşır: hosted service ve hesap döngüsü `[InstagramChatHostedService]`, kapı `[InstagramMergeGate]`, OAuth servisi `Facebook OAuth`. Geçiş ve kota satırları G10'un şablonlarına uyar.
- Aranan izleyici adı, yorum metni ve token'lar testin ürettiği ya da kitten okuduğu değerlerdir; literal yok.
- Denetim bir satır yakalarsa düzeltme yalnız o satırın şablonunda yapılır (token ya da adres çıkarılır; izleyici verisi Debug'a iner ya da kimliğe çevrilir; önek eklenir); davranış değişmez.

- [ ] **Adım 1: Düşen testleri yaz (`Violations` gövdesi şimdilik `return [];`)**

```csharp
// OrderDeck.Tests/Chat/Instagram/InstagramLoggingAuditTests.cs; using static OrderDeck.Tests.TestHelpers.FacebookOAuthTestKit; (NewId, NewToken)
public sealed class InstagramLoggingAuditTests : IDisposable   // Dispose: _kit
{
    private const string AccountPrefix = "[InstagramChatHostedService] account ", UsagePrefix = "[InstagramChatHostedService] usage ";
    private const string TransitionLine = @"^\[InstagramChatHostedService\] account .+ \(page [^)]+\): \w+ -> \w+(, retry at \d\d:\d\d:\d\d)?$";
    private const string Bucket = @"business \S+ call_count [\d.]+% total_cputime [\d.]+% total_time [\d.]+% regain [\d.]+ min";
    private const string UsageLine = @"^\[InstagramChatHostedService\] usage .+ \(page [^)]+\): (" + Bucket + "(; " + Bucket + @")*|X-Business-Use-Case-Usage header (absent|unreadable))$";
    // Alanlar: _kit (FacebookOAuthTestKit), _time (ManualTime), _logs (CapturingLoggerFactory), _bus (ChatBus; kurucuda _received'e abone), _received (ConcurrentQueue<ChatMessage>).
    // İhlalleri "kural: kategori: ileti" biçiminde döner (boş = temiz; bir satır bir kuralı en çok bir kez ihlal eder):
    //  1. her düzeyde Full içinde tokens'tan biri ya da "access_token=" geçiyorsa;
    //  2. Information ve üstünde Full içinde viewerData'dan biri geçiyorsa;
    //  3. Information ve üstünde, kategorisi InstagramChatHostedService / InstagramMergeGate / FacebookOAuthService ile biten satırın iletisi
    //     sırasıyla "[InstagramChatHostedService]" / "[InstagramMergeGate]" / "Facebook OAuth" ile başlamıyorsa.
    private static IReadOnlyList<string> Violations(IEnumerable<CapturingLoggerFactory.Entry> entries, IReadOnlyCollection<string> tokens, IReadOnlyCollection<string> viewerData);
    private async Task<HashSet<string>> TokensAsync();   // FakeGraph'a giden her isteğin access_token'ı + depodaki demetin kullanıcı ve Sayfa token'ları (ekler dahil)
    private IReadOnlyList<string> Info(string prefix);   private static Task Until(Func<bool> condition);   // Information ve üstü, iletisi prefix ile başlayanlar / 20 ms'de bir yoklar, TestPatience.Upper'da TimeoutException
    private static async Task<Exception> ThrownBy(Func<Task> act);   // fırlayan istisnayı döner; fırlamazsa testi düşürür

    [Fact] public void Tarayici_sizintiyi_ve_yanlis_oneki_yakalar() {
        var (token, viewer) = (NewToken(), NewId());
        var log = _logs.CreateLogger("denetim");
        log.LogDebug("istek {Url}", $"https://example.test/x?access_token={token}");      // her düzeyde yasak
        log.LogInformation("yorum {User}", viewer);                                        // Information'da yasak
        log.LogDebug("yorum {User}", viewer);                                              // Debug'da serbest
        _logs.CreateLogger(typeof(InstagramChatHostedService).FullName!).LogInformation("öneksiz satır");
        Violations(_logs.Entries, [token], [viewer]).Should().HaveCount(3);
    }
    [Fact] public async Task Iki_hesapli_oturumun_gunlugunde_token_adres_ve_izleyici_verisi_yok() {
        var p = await _kit.SeedAsync(extraPages: 1);
        var business = NewId();
        var a = new InstagramLiveFeed(_kit.Graph, p[0].IgUserId!) { UsageHeader = InstagramLiveFeed.Usage(business, 12.5, 3, 4) };
        var b = new InstagramLiveFeed(_kit.Graph, p[1].IgUserId!);                         // başlıksız
        var oauth = _kit.CreateService(_logs.CreateLogger<FacebookOAuthService>());
        var timings = new InstagramChatTimings(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(1));
        using var service = new InstagramChatHostedService(oauth, _bus, _logs, new SpamFilter(() => new SpamFilterSettings()), sessions: null, _kit.HttpFactory, new InstagramHealthMonitor(_time), _time, timings);
        await service.StartAsync(default);
        await Task.WhenAll(a.PrimedAsync(), b.PrimedAsync());
        var first = a.Add(); var repeat = a.Add(first.Viewer, first.Text); var other = b.Add();   // yineleme süzgeçten düşer
        await Until(() => _received.Any(m => m.ExternalId == other.Id) && _logs.Entries.Any(e => e.Message.Contains(repeat.Id)));
        var (extraId, extraName, extraIg) = (NewId(), NewId(), NewId());                    // üçüncü Sayfa: ekle, yoklansın, kaldır
        _kit.Graph.AddPage(extraId, extraName, extraIg, NewId());
        var c = new InstagramLiveFeed(_kit.Graph, extraIg);
        await oauth.AddExtraPageAsync(extraId, extraName);
        await c.PrimedAsync();
        await oauth.RemoveExtraPageAsync(extraId);
        await _kit.ReauthorizeAllAsync(oauth);                                              // izin güncelleme
        b.Intercept = _ => FakeGraph.Error(4);                                              // hız sınırı: "retry at" taşıyan geçiş
        a.Intercept = _ => FakeGraph.Error(190);                                            // kalıcı hata
        await Until(() => Info(AccountPrefix).Any(l => l.Contains("-> RateLimited, retry at")) && Info(AccountPrefix).Any(l => l.Contains("-> Faulted, retry at")));   // kalıcı hatanın satırı da saat taşır
        await service.StopAsync(default);                                                   // kapı özeti Dispose'ta yazılır

        Violations(_logs.Entries, await TokensAsync(), [first.Viewer, first.Text, other.Viewer, other.Text]).Should().BeEmpty();
        _logs.Entries.Single(e => e.Message.Contains(repeat.Id)).Level.Should().Be(LogLevel.Debug);   // düşen yorum yalnız kimliğiyle
        Info("Facebook OAuth").Count(l => l.Contains(extraId) && l.Contains(extraName)).Should().BeGreaterThanOrEqualTo(2);   // ekleme + kaldırma
        Info("[InstagramMergeGate]").Should().NotBeEmpty();
        Info(AccountPrefix).Should().OnlyContain(l => Regex.IsMatch(l, TransitionLine));
        Info(UsagePrefix).Should().OnlyContain(l => Regex.IsMatch(l, UsageLine))
            .And.Contain(l => l.Contains($"business {business} call_count 12.5% total_cputime 3% total_time 4% regain 0 min"))
            .And.Contain(l => l.EndsWith("X-Business-Use-Case-Usage header absent"));
    }
    [Fact] public void Token_tasiyan_kayitlar_sablona_verilse_de_token_basmaz() {
        var (user, page, extra) = (NewToken(), NewToken(), NewToken());
        var linked = new FacebookLinkedPage(NewId(), extra, NewId());
        _logs.CreateLogger("denetim").LogInformation("{Page} {Bundle}", linked, new FacebookTokenBundle(user, NewId(), page, NewId(), null, [linked]));
        _logs.Entries.Should().ContainSingle();
        Violations(_logs.Entries, [user, page, extra], []).Should().BeEmpty();
    }
    [Fact] public async Task OAuth_hata_yollari_istisna_mesajinda_token_ve_adres_tasimaz() {
        await _kit.SeedAsync();
        var oauth = _kit.CreateService(_logs.CreateLogger<FacebookOAuthService>(), callbackTimeout: TimeSpan.FromMilliseconds(50));
        var (pageId, name) = (NewId(), NewId());
        _kit.Graph.Map(u => u.AbsolutePath.EndsWith("/" + pageId), _ => FakeGraph.Json("null"));         // boş gövde
        var errors = new List<Exception> { await ThrownBy(() => oauth.AddExtraPageAsync(pageId, name)) };
        _kit.Graph.Map(u => u.AbsolutePath.EndsWith("/" + pageId), _ => FakeGraph.Error(190));           // Graph hatası
        errors.Add(await ThrownBy(() => oauth.AddExtraPageAsync(pageId, name)));
        errors.Add(await ThrownBy(() => oauth.ReauthorizeAsync((_, _) => Task.FromResult(true))));       // zaman aşımı
        errors[2].Should().BeOfType<TimeoutException>();
        var tokens = await TokensAsync();
        errors.Should().OnlyContain(e => !e.ToString().Contains("access_token=") && !e.ToString().Contains("graph.facebook.com") && tokens.All(s => !e.ToString().Contains(s)));
        Violations(_logs.Entries, tokens, []).Should().BeEmpty();
    }
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramLoggingAuditTests"`
Beklenen: FAIL (`Tarayici_sizintiyi_ve_yanlis_oneki_yakalar`: 3 ihlal beklenirken 0 bulundu; tarayıcı boş)

- [ ] **Adım 3: `Violations(IEnumerable<CapturingLoggerFactory.Entry> entries, IReadOnlyCollection<string> tokens, IReadOnlyCollection<string> viewerData)` uygula (`InstagramLoggingAuditTests.cs`)**

Üç kural imzanın üstündeki yorumdaki gibi; her ihlal tek dizedir (düşen test hangi satırın hangi kuralla yakalandığını söylesin).

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: Adım 2'deki komut
Beklenen: PASS. Denetim testi bir satır yakalarsa (çıktı satırı ve kuralı verir) düzeltme o satırı yazan üretim dosyasında "Kurallar"ın son maddesine göre yapılır ve komut yeniden koşulur.

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~InstagramLoggingAuditTests|FullyQualifiedName~InstagramMultiAccountHostedServiceTests|FullyQualifiedName~FacebookOAuthServiceMultiPageTests|FullyQualifiedName~InstagramMergeGateTests|FullyQualifiedName~InstagramLiveCommentsPollerTests"`
Beklenen: PASS, 0 uyarı. Motor tamam: G01–G13 tek başına birleşebilir.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.Tests/Chat/Instagram/InstagramLoggingAuditTests.cs   # denetim bir satır yakaladıysa düzeltilen üretim dosyaları da
git commit -F - <<'MSG'
test(instagram): günlükte token ve izleyici verisi denetimi ekle

Depo herkese açık ve günlükler destek için paylaşılıyor: iki hesaplı bir
oturumun bütün satırları taranır. Hiçbir düzeyde token ya da access_token
taşıyan adres, Information ve üstünde izleyici adı ya da yorum metni yok.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G14: Sohbet satırı hesap etiketi ve ipucu

**Spec:** §8.6.2; §8.4; §9.4 `ChatMessageViewModelTests`; Ö1, TK11; Review Focus 3, 4, 5.
**Bağımlı:** G09.

**Files:**
- Modify: `OrderDeck.App/ViewModels/ChatMessageViewModel.cs:10-34`
- Modify: `OrderDeck.App/Themes/Metrics.xaml:159-160` (`OD.Layout.ChatUserMaxWidth`'in altına yeni belirteç)
- Modify: `OrderDeck.App/Views/Shell/ChatPanel.xaml:118-120` (mesaj `TextBlock`'u; rozet ve ad kolonları `:76-117` DEĞİŞMEZ)
- Test: `OrderDeck.Tests/ViewModels/ChatMessageViewModelTests.cs` (yeni), `OrderDeck.Tests/App/ChatPanelLayoutTests.cs` (yeni; G15 şerit testini buraya ekler)

**Interfaces:**
- Consumes: G09 `ChatMessage(string Id, string Platform, string? ExternalId, string Username, string? DisplayName, string? AvatarUrl, string Text, long ReceivedAt, IReadOnlyList<string> Badges, string? SourceAccount = null)`.
- Produces: `ChatMessageViewModel` üyeleri `public string? AccountTag { get; }`, `public string? AccountTooltip { get; }`, `internal static string FormatAccountTooltip(string sourceAccount, long unixSeconds, TimeSpan utcOffset)`; tema belirteci `OD.Layout.ChatAccountTagMaxWidth` (`sys:Double`, değeri 112).

**Kurallar:**
- `AccountTag`: `SourceAccount` null, boş ya da yalnız boşluksa null; doluysa AYNEN. Arayüz "@" eklemez ("@" kulvarda eklenir, §8.4): Sayfa adının başına "@" gelmemeli.
- `AccountTooltip`: etiket yoksa null; varsa `"Instagram: " + SourceAccount + " · " + saat` (ayraç: boşluk, orta nokta U+00B7, boşluk). Saat `ReceivedAt`'in (Meta unix SANİYESİ) `HH:mm:ss` yerel karşılığıdır; ofset o anın yerel ofsetidir (`TimeZoneInfo.Local.GetUtcOffset`), sabit bir sayı değil.
- XAML'de yalnız mesaj kolonu değişir. Etiket satırın sağına yaslıdır: kodun başladığı x konumu etiketli ve etiketsiz satırda aynıdır. `AccountTag` null iken etiket `Collapsed` (tek Sayfada ve öteki platformlarda satır bugünküyle aynı). Alan overlay'e, SQLite'a ve DTO'lara gitmez; `OrderDeck.Overlay/**` dokunulmaz.
- Genişlik belirteci 112 DIP (spec "yaklaşık 110"): onaltılık rakamlardan üretilen 16 karakterlik etiket IBM Plex Sans 11'de en çok 108,8 DIP tutar (`@` 891, rakam 600 birim / 1000; `IBMPlexSans-Regular.ttf` ölçüldü), kalan 3 DIP ölçüm payıdır. `m` / `w` ağırlıklı adlar 16 karakterde de kırpılabilir; o durumda tam adı ipucu verir.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// OrderDeck.Tests/ViewModels/ChatMessageViewModelTests.cs — namespace OrderDeck.Tests.ViewModels
public class ChatMessageViewModelTests
{
    private static string NewTag() => $"@hesap-{Guid.NewGuid():N}";
    private static ChatMessageViewModel Vm(string? sourceAccount) => new(
        new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, $"izleyici-{Guid.NewGuid():N}", null, null,
            "A12", 1_800_000_000, Array.Empty<string>(), sourceAccount), isSenderBlacklisted: false);

    [Theory, InlineData(null), InlineData(""), InlineData("  ")]
    public void SourceAccount_bosken_etiket_ve_ipucu_yok(string? source) =>
        (Vm(source).AccountTag, Vm(source).AccountTooltip).Should().Be(((string?)null, (string?)null));

    [Fact]
    public void Etiket_aynen_gosterilir_ikinci_at_eklenmez_Sayfa_adi_at_almaz()
    {
        var (tag, pageName) = (NewTag(), $"Sayfa {Guid.NewGuid():N}");
        (Vm(tag).AccountTag, Vm(pageName).AccountTag).Should().Be((tag, pageName));
    }

    [Fact]                                                             // Review Focus 4: UTC de geliştirme makinesinin +03:00'ü de olmayan sabit ofset
    public void Ipucu_Meta_saatini_verilen_ofsette_yazar()
    {
        var tag = NewTag();
        var meta = new DateTimeOffset(2026, 11, 17, 18, 14, 7, TimeSpan.Zero).ToUnixTimeSeconds();
        ChatMessageViewModel.FormatAccountTooltip(tag, meta, TimeSpan.FromHours(5.5)).Should().Be($"Instagram: {tag} · 23:44:07");
    }

    [Fact]
    public void Ipucu_mesajin_damgasini_o_anin_yerel_ofsetiyle_verir()
    {
        var vm = Vm(NewTag());
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(vm.Message.ReceivedAt));
        vm.AccountTooltip.Should().Be(ChatMessageViewModel.FormatAccountTooltip(vm.AccountTag!, vm.Message.ReceivedAt, offset));
    }
}

// OrderDeck.Tests/App/ChatPanelLayoutTests.cs — namespace OrderDeck.Tests.App
public class ChatPanelLayoutTests
{
    public sealed class ChatStub     // panelin bağlandığı kabuk yüzeyi (bağlamaların okuduğu adlarla); WPF bağlaması public tip ister
    {
        public ObservableCollection<ChatMessageViewModel> ChatView { get; } = new();
        public string ChatSearchText { get; set; } = "";
        public string ActiveCode { get; set; } = "";
        public bool OnlyActiveCode { get; set; }
    }

    private static void OnSta(Action body) => ThemeTestHost.RunOnSta(body).Should().BeNull();
    private static ChatMessageViewModel Row(string? sourceAccount);             // üretilmiş izleyici ve metin, platform "instagram"
    private static ChatPanel Layout(ChatStub context);                          // 700x400; ShellBannersLayoutTests.Layout kalıbı (ölçüm, Pump, yeniden ölçüm)
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root);    // görsel ağaç
    private static double NaturalWidth(TextBlock t);                            // aynı metin ve yazı tipiyle, genişlik sınırı olmadan ölçüm
    private static TextBlock Bound(ChatPanel panel, ChatMessageViewModel row, string path) =>      // satırın, Text bağı o yola giden tek TextBlock'u
        Descendants(panel).OfType<TextBlock>().Single(t => ReferenceEquals(t.DataContext, row)
            && BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == path);

    [Fact]                                                             // Review Focus 3
    public void Etiketli_satirda_etiket_ve_ipucu_gorunur_etiketsiz_satir_bugunku_gibi() => OnSta(() =>
    {
        var (tagged, plain) = (Row("@" + Guid.NewGuid().ToString("N")[..12]), Row(null));
        var panel = Layout(new ChatStub { ChatView = { tagged, plain } });
        var tag = Bound(panel, tagged, nameof(ChatMessageViewModel.AccountTag));
        tag.Text.Should().Be(tagged.AccountTag);
        tag.Visibility.Should().Be(Visibility.Visible);
        tag.ToolTip.Should().Be(tagged.AccountTooltip);
        BindingOperations.GetBinding(tag, FrameworkElement.ToolTipProperty)!.Path.Path.Should().Be(nameof(ChatMessageViewModel.AccountTooltip));
        Bound(panel, plain, nameof(ChatMessageViewModel.AccountTag)).Visibility.Should().Be(Visibility.Collapsed);
        double MessageX(ChatMessageViewModel row) => Bound(panel, row, nameof(ChatMessageViewModel.Text))
            .TransformToAncestor(panel).Transform(new Point(0, 0)).X;
        MessageX(tagged).Should().Be(MessageX(plain), "etiket sağda: kodun başladığı x konumu kaymaz");
    });

    [Fact]                                                             // Review Focus 5
    public void Son_karakteri_farkli_16_karakterlik_iki_etiket_kirpilmaz_uzun_etikette_ipucu_tam_adi_verir() => OnSta(() =>
    {
        var stem = "@" + Guid.NewGuid().ToString("N")[..14];                    // 15 karakter
        var (first, second, longer) = (Row(stem + "1"), Row(stem + "2"), Row("@" + Guid.NewGuid().ToString("N")[..30]));
        var panel = Layout(new ChatStub { ChatView = { first, second, longer } });
        foreach (var row in new[] { first, second })
        {
            var tag = Bound(panel, row, nameof(ChatMessageViewModel.AccountTag));
            tag.Text.Should().HaveLength(16);
            tag.ActualWidth.Should().BeGreaterThanOrEqualTo(NaturalWidth(tag) - 0.5, "ayırt edici son karakter görünmeli");
        }
        var trimmed = Bound(panel, longer, nameof(ChatMessageViewModel.AccountTag));        // Instagram adı 30 karaktere kadar
        trimmed.ActualWidth.Should().BeLessThanOrEqualTo((double)panel.FindResource("OD.Layout.ChatAccountTagMaxWidth"));
        trimmed.TextTrimming.Should().Be(TextTrimming.CharacterEllipsis);
        ((string)trimmed.ToolTip).Should().Contain(longer.AccountTag);
    });
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ChatMessageViewModelTests|FullyQualifiedName~ChatPanelLayoutTests"`
Beklenen: FAIL (derleme hatası: `ChatMessageViewModel`'de `AccountTag`, `AccountTooltip`, `FormatAccountTooltip` yok)

- [ ] **Adım 3: `AccountTag`, `AccountTooltip`, `FormatAccountTooltip`, belirteç ve satır etiketini uygula (`ChatMessageViewModel.cs`, `Metrics.xaml`, `ChatPanel.xaml`)**

Üç üye de `Message`'dan hesaplanır, alan eklenmez; `FormatAccountTooltip` saati `DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(utcOffset)` üzerinden `HH:mm:ss` (InvariantCulture) ile yazar. `Metrics.xaml`: `OD.Layout.ChatUserMaxWidth`'in altına `<sys:Double x:Key="OD.Layout.ChatAccountTagMaxWidth">112</sys:Double>`. `ChatPanel.xaml`: mesaj `TextBlock`'u aynı kolonda (`Grid.Column="2"`) bir `DockPanel`'e alınır ve son çocuk olarak öznitelikleriyle aynen kalır. Önüne `DockPanel.Dock="Right"` etiket `TextBlock`'u gelir: `Text="{Binding AccountTag}"`, `ToolTip="{Binding AccountTooltip}"`, `FontSize` `OD.Font.F0`, `Foreground` `OD.Brush.TextMute`, `MaxWidth` `OD.Layout.ChatAccountTagMaxWidth`, `TextTrimming="CharacterEllipsis"`, `VerticalAlignment="Center"`, `Margin` `OD.Pad.Left4`, `Visibility` = `AccountTag` + `NullToCollapsedConverter`.

- [ ] **Adım 4: Testlerin geçtiğini gör**

Koş: Adım 2'deki komut
Beklenen: PASS

- [ ] **Adım 5: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q && DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ChatMessageViewModelTests|FullyQualifiedName~ChatPanelLayoutTests|FullyQualifiedName~MainShellViewCompositionTests|FullyQualifiedName~MainShellChatFilterTests|FullyQualifiedName~UnstyledControlGuardTests|FullyQualifiedName~ThemeMetricsTests"`
Beklenen: PASS, 0 uyarı

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.App/ViewModels/ChatMessageViewModel.cs OrderDeck.App/Themes/Metrics.xaml OrderDeck.App/Views/Shell/ChatPanel.xaml \
  OrderDeck.Tests/ViewModels/ChatMessageViewModelTests.cs OrderDeck.Tests/App/ChatPanelLayoutTests.cs
git commit -F - <<'MSG'
feat(instagram): sohbet satırına hesap etiketi ve ipucu ekle

İki hesap bağlıyken operatör yorumun hangi hesaptan geldiğini satırda görsün;
etiket sağda durur (kod sütunu kaymaz), ipucu Meta saatini verir.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G15: Sağlık şeridi, üst bar noktası, kenar çubuğu noktası ve `AppHost` kayıtları

**Spec:** §8.6.3 (yer, görünürlük, VM üyeleri, DI); §8.6.4; §8.3.2 değişen davranış 3–4; §9.4 `MainShellInstagramHealthTests` ve `MainShellViewCompositionTests` (ek) ilk cümlesi; TK9, TK20; AH3; Review Focus 2, 3.
**Bağımlı:** G07, G08, G10, G14.

**Files:**
- Create: `OrderDeck.App/ViewModels/MainShellViewModel.InstagramHealth.cs` (yeni üyeler; emsal `MainShellViewModel.SalesFeed.cs`)
- Modify: `OrderDeck.App/ViewModels/MainShellViewModel.cs` (`RefreshConnections` `:196-206`; kurucu imzası `:1099-1131`; `_viewers = viewers;` `:1164`; `UpdateChatHealth` `:1323-1386`)
- Modify: `OrderDeck.App/ViewModels/PlatformConnectionViewModel.cs:30-31`, `OrderDeck.App/Views/Shell/ShellSidebar.xaml:220-224`, `OrderDeck.App/AppHost.cs:303-310`
- Modify: `OrderDeck.App/Views/Shell/ChatPanel.xaml` (bugünkü satırlar: satır tanımları `:6-9`, `ChatList` `:50`; G14 kaydırdı)
- Test: `OrderDeck.Tests/App/MainShellTestHarness.cs` (Modify: `Harness` `:71-87`, `Build` `:106-121`, `:178-188`), `OrderDeck.Tests/App/ChatPanelLayoutTests.cs` (Modify)
- Test: `OrderDeck.Tests/App/MainShellInstagramHealthTests.cs` (yeni), `OrderDeck.Tests/App/InstagramHealthDiTests.cs` (yeni)

**Interfaces:**
- Consumes: G07 `InstagramHealthMonitor(TimeProvider? time = null)`, `void Configure(IReadOnlyList<FacebookLinkedPage> pages)`, `void SetAccount(string pageId, string? username)`, `void Report(string pageId, InstagramAccountState state, string? detail = null, DateTimeOffset? retryAt = null)`, `IInstagramPollObserver ObserverFor(string pageId)`, `IReadOnlyList<InstagramAccountHealth> Snapshot()`; `ManualTime` (`Advance(TimeSpan)`, `GetUtcNow()`). G06 `void RoundCompleted(InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader)`. G08 `InstagramHealthPresenter.Evaluate(IReadOnlyList<InstagramAccountHealth> snapshot, DateTimeOffset now, bool sessionActive)` → `InstagramHealthView(IReadOnlyList<InstagramAccountStatusViewModel> Rows, bool ShowStrip, bool AnyAccountLive, string? TopDotTooltip)`; `InstagramAccountStatusViewModel(string PageId, string Label, string StateText, string Text, InstagramStatusSeverity Severity, string? Tooltip)`; `InstagramStatusSeverity { Live, Neutral, Warning }`. G10 hosted service kurucusunun `InstagramHealthMonitor? health = null` parametresi. G01 `FacebookLinkedPage(string PageId, string PageAccessToken, string PageName)`; G02 kit `FacebookOAuthTestKit.NewId()`, `NewToken()`.
- Produces: `MainShellViewModel` kurucusunun SONUNA `InstagramHealthMonitor? instagramHealth = null` (tek public kurucu kalır) · `public ObservableCollection<InstagramAccountStatusViewModel> InstagramAccounts { get; }` · `public bool ShowInstagramStatus` (gözlenebilir) · `internal void UpdateChatHealth()` (bugün private) · `internal Func<DateTimeOffset> InstagramNow { get; set; } = () => DateTimeOffset.Now;` · `PlatformConnectionViewModel.ShowViewerCount` (gözlenebilir `bool`) · harness: `Build(…, InstagramHealthMonitor? instagramHealth = null, ViewerCountTracker? viewers = null)`, `public IChatBus Bus { get; init; }` (`Harness`).

**Kurallar:**
- Nabız: `UpdateChatHealth`, ayar okumasından sonra ve `RefreshConnections()`'tan (`:1349`) ÖNCE `Evaluate(izleyici.Snapshot(), InstagramNow(), hasActiveSession)`'ı bir kez koşar ve sonucu alanda tutar. `RefreshConnections` (public; `MainShellConnectionsTests` doğrudan çağırır) Instagram satırını bu SON sonuçtan okur, kendisi `Evaluate` çağırmaz. İzleyici null ise hiçbiri koşmaz: şerit boş, dört satır ve üst nokta bugünkü gibi.
- Alan ataması `_viewers = viewers;` satırının yanında olmalı: kurucu daha sonra `EnsureChatFlushTimer()` üzerinden `UpdateChatHealth`'i eşzamanlı çağırır.
- `InstagramAccounts` = `Rows`, `ShowInstagramStatus` = `ShowStrip`. Satırlar öncekiyle sırayla eşitse (kayıt eşitliği) koleksiyona dokunulmaz, değilse boşaltılıp yeniden doldurulur (değişmeyen satırlarda açık ipucu 5 saniyede bir kapanmasın). Bilinen kozmetik sonuç: canlı satırın ipucu (`Son yanıt N sn önce`) ve `yanıt yok (N sn)` metni nabızdan nabza değişebilir; o satırlar değiştikçe koleksiyon yine yenilenir.
- Üst nokta: mevcut mantık "off" ile dönüyorsa (`:1362-1367`, oturum yok) Instagram kuralı uygulanmaz. `TopDotTooltip` doluysa mevcut "ok" / "idle" dallarından SONRA: mevcut sonuç "ok" idiyse `ChatHealthState = "idle"` ve ipucu YALNIZ Instagram cümlesi; "idle" idiyse durum aynı, ipucu `TopDotTooltip + "\n" + mevcut ipucu`. "Diğer kaynaklar akıyor" eki yazılmaz. `ShellTopBar.xaml` DEĞİŞMEZ.
- Kenar çubuğu: izleyici verildiyse Instagram satırında `IsConnected = AnyAccountLive`, `ShowViewerCount = false`; öteki üç satırda (ve izleyici yokken Instagram'da) `IsConnected` ve `ViewerCount` bugünkü gibi, `ShowViewerCount = IsConnected`. Dört satır kuralı korunur.
- Şerit `ChatPanel`'de başlık ile liste arasında `Auto` satırdır; `ShellBanners.xaml`'e şerit EKLENMEZ. Uyarı işareti simge değil renkli noktadır. `ChatPanel`'in yeni bağlarla yüklendiğini mevcut `MainShellViewCompositionTests.cs:51` satırı sınar (dosya değişmez).
- DI: izleyici tekil; hosted service ve kabuk AYNI örneği alır. Yeni hosted service yok.

- [ ] **Adım 1: Harness'i genişlet, düşen testleri yaz**

`MainShellTestHarness.cs`: `Harness` kaydının gövdesine `public IChatBus Bus { get; init; } = null!;`; `Build`'in parametre listesinin sonuna `InstagramHealthMonitor? instagramHealth = null, ViewerCountTracker? viewers = null`; kabuk kurucusu çağrısına `viewers: viewers, instagramHealth: instagramHealth`; dönüşte `{ Bus = bus }`.

```csharp
// OrderDeck.Tests/App/MainShellInstagramHealthTests.cs — namespace OrderDeck.Tests.App
public class MainShellInstagramHealthTests
{
    /// <summary>Kenar çubuğunun yalnız bağlantı listesi (WPF bağlaması public tip ister).</summary>
    public sealed class SidebarStub { public ObservableCollection<PlatformConnectionViewModel> Connections { get; } = new(); }

    private readonly ManualTime _time = new();

    // Tek Sayfalı izleyici (kimlik / token kit'in NewId / NewToken'ı; ad ve kullanıcı adı üretilmiş); hesap Live, son teması _time'ın
    // şimdisi: Configure → Report(Starting) → SetAccount → ObserverFor(id).RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null).
    private (InstagramHealthMonitor Monitor, string PageId, string Username) LiveMonitor();
    // MainShellTestHarness.Build(instagramHealth: monitor, viewers: viewers) + InstagramNow = () => _time.GetUtcNow() + bir UpdateChatHealth().
    private MainShellTestHarness.Harness Build(InstagramHealthMonitor? monitor, ViewerCountTracker? viewers = null);
    private static ViewerCountTracker Viewers(params (string Platform, int Count)[] reports);       // Report edilmiş sayaç
    private static void Chat(MainShellTestHarness.Harness h);           // h.Bus'a üretilmiş bir yorum basar: mevcut sonuç "ok" olur
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root);                // görsel ağaç
    private static PlatformConnectionViewModel Row(MainShellTestHarness.Harness h, string platform) =>
        h.Vm.Connections.Single(c => c.Platform == platform);

    [Fact]
    public void Izleyici_verilmezse_serit_bos_ve_dort_satir_bugunku_gibi()
    {
        using var h = Build(null, Viewers(("instagram", 55)));
        h.Vm.InstagramAccounts.Should().BeEmpty();
        h.Vm.ShowInstagramStatus.Should().BeFalse();
        h.Vm.Connections.Select(c => c.Platform).Should().Equal("youtube", "instagram", "tiktok", "facebook");
        (Row(h, "instagram").IsConnected, Row(h, "instagram").ShowViewerCount, Row(h, "instagram").ViewerCount).Should().Be((true, true, 55));
        Row(h, "youtube").ShowViewerCount.Should().BeFalse();
    }

    [Fact]
    public void Canli_hesapta_Instagram_satiri_yesil_ve_sayisiz_diger_uc_satir_ayni()
    {
        using var h = Build(LiveMonitor().Monitor, Viewers(("youtube", 1240), ("instagram", 55)));
        (Row(h, "instagram").IsConnected, Row(h, "instagram").ShowViewerCount).Should().Be((true, false));
        (Row(h, "youtube").IsConnected, Row(h, "youtube").ShowViewerCount, Row(h, "youtube").ViewerCount).Should().Be((true, true, 1240));
        h.Vm.Connections.Where(c => c.Platform is "tiktok" or "facebook").Should().OnlyContain(c => !c.IsConnected && !c.ShowViewerCount);
        h.Vm.ShowInstagramStatus.Should().BeFalse("tek hesap sağlıklı: şerit yok");
        _time.Advance(TimeSpan.FromSeconds(11));                        // Live ama yanıtsız: canlı sayılmaz
        h.Vm.RefreshConnections();
        Row(h, "instagram").IsConnected.Should().BeTrue("RefreshConnections son nabzın sonucunu okur");
        h.Vm.UpdateChatHealth();
        Row(h, "instagram").IsConnected.Should().BeFalse();
    }

    [Fact]
    public void Sari_durumda_ust_nokta_sarar_ipucu_hesabi_adiyla_soyler_duzelince_geri_doner()
    {
        var (monitor, pageId, username) = LiveMonitor();
        using var h = Build(monitor);
        Chat(h);                                                        // mevcut sonuç "ok"
        _time.Advance(TimeSpan.FromSeconds(18));
        h.Vm.UpdateChatHealth();
        (h.Vm.ChatHealthState, h.Vm.ChatHealthTooltip).Should().Be(("idle", $"Instagram @{username}: yanıt yok (18 sn)."));
        h.Vm.ShowInstagramStatus.Should().BeTrue();
        h.Vm.InstagramAccounts.Should().ContainSingle().Which.Text.Should().Be($"@{username} yanıt yok (18 sn)");
        var changes = 0;
        h.Vm.InstagramAccounts.CollectionChanged += (_, _) => changes++;
        h.Vm.UpdateChatHealth();
        changes.Should().Be(0, "satırlar değişmediyse koleksiyona dokunulmaz");
        monitor.ObserverFor(pageId).RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);    // düzeldi
        h.Vm.UpdateChatHealth();
        h.Vm.ChatHealthState.Should().Be("ok");
        h.Vm.ChatHealthTooltip.Should().StartWith("Chat aktif");
        h.Vm.ShowInstagramStatus.Should().BeFalse();
    }

    [Fact]
    public void Mevcut_sonuc_idle_iken_Instagram_ipucu_mevcut_ipucuyla_birlikte_yazilir()
    {
        var (monitor, _, username) = LiveMonitor();
        using var h = Build(monitor);                                   // hiç yorum yok: mevcut sonuç "idle"
        _time.Advance(TimeSpan.FromSeconds(18));
        h.Vm.UpdateChatHealth();
        h.Vm.ChatHealthState.Should().Be("idle");
        h.Vm.ChatHealthTooltip.Should().StartWith($"Instagram @{username}: yanıt yok (18 sn).\nChat takibi açık").And.EndWith("henüz mesaj yok");
    }

    [Fact]
    public void Oturum_yokken_Instagram_kurali_uygulanmaz()
    {
        using var h = Build(LiveMonitor().Monitor);
        h.Sessions.End(h.Sessions.GetActive()!.Id);
        _time.Advance(TimeSpan.FromSeconds(18));
        h.Vm.UpdateChatHealth();
        (h.Vm.ChatHealthState, h.Vm.ChatHealthTooltip).Should().Be(("off", "Chat takibi kapalı (yayın aktif değil)"));
        h.Vm.ShowInstagramStatus.Should().BeFalse();
    }

    [Fact]                                                              // Review Focus 3
    public void Kenar_cubugunda_sayi_sutunu_ShowViewerCount_ile_gorunur() => ThemeTestHost.RunOnSta(() =>
    {
        var youtube = new PlatformConnectionViewModel("youtube") { IsConnected = true, ShowViewerCount = true, ViewerCount = 1240 };
        var instagram = new PlatformConnectionViewModel("instagram") { IsConnected = true, ShowViewerCount = false };
        var sidebar = new ShellSidebar { DataContext = new SidebarStub { Connections = { youtube, instagram } } };
        ThemeTestHost.Pump();
        sidebar.Measure(new Size(400, 900));
        sidebar.Arrange(new Rect(0, 0, 400, 900));
        sidebar.UpdateLayout();
        Visibility CountOf(PlatformConnectionViewModel row) => Descendants(sidebar).OfType<TextBlock>().Single(t => ReferenceEquals(t.DataContext, row)
            && BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == nameof(PlatformConnectionViewModel.ViewerCount)).Visibility;
        CountOf(youtube).Should().Be(Visibility.Visible);
        CountOf(instagram).Should().Be(Visibility.Collapsed, "Instagram satırı yeşil ama sayısız");
    }).Should().BeNull();
}

// OrderDeck.Tests/App/InstagramHealthDiTests.cs — namespace OrderDeck.Tests.App
public sealed class InstagramHealthDiTests
{
    [Fact]                                                              // Review Focus 2
    public void Izleyici_tekil_hosted_service_ayni_ornegi_tutar_kabuk_istege_bagli_parametreyle_alir()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var monitor = host.Services.GetRequiredService<InstagramHealthMonitor>();
        var hosted = host.Services.GetServices<IHostedService>().OfType<InstagramChatHostedService>().Single();
        host.Services.GetRequiredService<InstagramHealthMonitor>().Should().BeSameAs(monitor);
        hosted.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(InstagramHealthMonitor)).Select(f => f.GetValue(hosted))
            .Should().ContainSingle().Which.Should().BeSameAs(monitor, "şerit, hosted service'in yazdığı AYNI izleyiciyi okumalı");
        typeof(MainShellViewModel).GetConstructors().Single().GetParameters()
            .Should().ContainSingle(p => p.IsOptional && p.ParameterType == typeof(InstagramHealthMonitor));
    }
}

// OrderDeck.Tests/App/ChatPanelLayoutTests.cs — ChatStub'a iki üye, sınıfa iki yardımcı ve bir test eklenir
public ObservableCollection<InstagramAccountStatusViewModel> InstagramAccounts { get; } = new();     // ChatStub
public bool ShowInstagramStatus { get; set; }                                                        // ChatStub

private static InstagramAccountStatusViewModel Status(InstagramStatusSeverity severity);    // üretilmiş PageId, etiket, metin, (null olmayan) ipucu
private static FrameworkElement Strip(ChatPanel panel) => Descendants(panel).OfType<FrameworkElement>().Single(e =>     // şeridin kökü
    BindingOperations.GetBinding(e, UIElement.VisibilityProperty)?.Path.Path == nameof(MainShellViewModel.ShowInstagramStatus));

[Fact]                                                                  // Review Focus 3
public void Saglik_seridi_gercek_satirlari_cizer_gizliyken_yer_kaplamaz() => OnSta(() =>
{
    var rows = new[] { Status(InstagramStatusSeverity.Live), Status(InstagramStatusSeverity.Warning), Status(InstagramStatusSeverity.Neutral) };
    Strip(Layout(new ChatStub { InstagramAccounts = { rows[0], rows[1], rows[2] } })).ActualHeight.Should().Be(0);

    var panel = Layout(new ChatStub { ShowInstagramStatus = true, InstagramAccounts = { rows[0], rows[1], rows[2] } });
    var strip = Strip(panel);
    var items = Descendants(strip).OfType<ItemsControl>().Single();
    BindingOperations.GetBinding(items, ItemsControl.ItemsSourceProperty)!.Path.Path.Should().Be(nameof(MainShellViewModel.InstagramAccounts));
    foreach (var (row, brush) in rows.Zip(new[] { "OD.Brush.Success", "OD.Brush.Amber", "OD.Brush.TextMute" }))
    {
        var host = Descendants(items).OfType<ContentPresenter>().Single(p => ReferenceEquals(p.Content, row));
        Descendants(host).OfType<TextBlock>().Single().Text.Should().Be(row.Text);
        Descendants(host).OfType<Ellipse>().Single().Fill.Should().BeSameAs(panel.FindResource(brush));
        Descendants(host).OfType<FrameworkElement>().Should().Contain(e => Equals(e.ToolTip, row.Tooltip));
    }
    var list = (ListBox)panel.FindName("ChatList");
    list.ActualHeight.Should().BeGreaterThan(0);
    strip.TransformToAncestor(panel).Transform(new Point(0, strip.ActualHeight)).Y.Should().BeLessThanOrEqualTo(
        list.TransformToAncestor(panel).Transform(new Point(0, 0)).Y, "şerit başlık ile liste arasında");
});
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~MainShellInstagramHealthTests|FullyQualifiedName~InstagramHealthDiTests|FullyQualifiedName~ChatPanelLayoutTests"`
Beklenen: FAIL (derleme hatası: kabuk kurucusunda `instagramHealth` yok; `InstagramAccounts`, `ShowInstagramStatus`, `InstagramNow`, `ShowViewerCount` yok; `UpdateChatHealth` erişilemez)

- [ ] **Adım 3: Kabuk üyelerini, nabzı, `ShowViewerCount`'u ve DI kayıtlarını uygula (`MainShellViewModel.InstagramHealth.cs`, `MainShellViewModel.cs`, `PlatformConnectionViewModel.cs`, `AppHost.cs`)**

Yeni `partial` dosya: izleyici alanı, son `InstagramHealthView?` alanı, `InstagramAccounts`, `[ObservableProperty] private bool _showInstagramStatus;`, `InstagramNow` ve `private void EvaluateInstagramHealth(bool sessionActive)` (izleyici null ise döner; sonucu alana, satırları ve görünürlüğü Kurallar'daki gibi yazar). `MainShellViewModel.cs`: kurucunun son parametresi ve `_viewers = viewers;` yanındaki atama; `UpdateChatHealth` `internal` olur, `RefreshConnections();` satırından hemen önce `EvaluateInstagramHealth(hasActiveSession);` çağırır ve mevcut "ok" / "idle" dallarından sonra üst nokta kuralını uygular; `RefreshConnections` döngüsü her satıra `ShowViewerCount = IsConnected` yazar, izleyici verildiyse Instagram satırında `IsConnected` = son sonucun `AnyAccountLive`'ı (sonuç yoksa false), `ShowViewerCount = false`. `PlatformConnectionViewModel`: `[ObservableProperty] private bool _showViewerCount;`. `AppHost.cs`: Instagram hosted service kaydının hemen üstüne `services.AddSingleton<OrderDeck.Chat.Ingestors.Instagram.InstagramHealthMonitor>();`, o kaydın argümanlarına `health: sp.GetRequiredService<OrderDeck.Chat.Ingestors.Instagram.InstagramHealthMonitor>()`.

- [ ] **Adım 4: Şeridi ve sayı sütununun bağını uygula (`ChatPanel.xaml`, `ShellSidebar.xaml`)**

`ShellSidebar.xaml`: sayı `TextBlock`'unun `Visibility` bağı `IsConnected` yerine `ShowViewerCount` olur (dönüştürücü aynı). `ChatPanel.xaml`: satır tanımları üç olur (`Auto`, `Auto`, `*`); `ChatList` `Grid.Row="2"`. `Grid.Row="1"`'e `Border`: `Visibility` = `ShowInstagramStatus` + `BoolToVisibleConverter`, alt çizgi başlık şeridiyle aynı (`BorderBrush` `OD.Brush.Border`, `BorderThickness="0,0,0,1"`), `Padding` `OD.Pad.3`. İçinde `DockPanel`: solda `Text="Instagram"` (`OD.Text.Micro`, `VerticalAlignment="Center"`), yanında `ItemsControl` (`ItemsSource="{Binding InstagramAccounts}"`, `ItemsPanel` = `WrapPanel`). Öğe şablonu `ShellSidebar.xaml:192-225` deseni: yatay `StackPanel` (`ToolTip="{Binding Tooltip}"`, `Margin` `OD.Pad.Left4`) içinde 8x8 `Ellipse` ve TEK `TextBlock` (`Text="{Binding Text}"`, `FontSize` `OD.Font.F1`, `Foreground` `OD.Brush.TextDim`, `VerticalAlignment="Center"`). Nokta rengi `Severity` üzerinde `DataTrigger`: varsayılan `OD.Brush.TextMute`, `Live` → `OD.Brush.Success`, `Warning` → `OD.Brush.Amber`.

- [ ] **Adım 5: Testlerin geçtiğini gör**

Koş: Adım 2'deki komut
Beklenen: PASS

- [ ] **Adım 6: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q && DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~MainShellInstagramHealthTests|FullyQualifiedName~InstagramHealthDiTests|FullyQualifiedName~ChatPanelLayoutTests|FullyQualifiedName~MainShellConnectionsTests|FullyQualifiedName~ShellBannersLayoutTests|FullyQualifiedName~MainShellViewCompositionTests|FullyQualifiedName~CustomerSyncDiTests|FullyQualifiedName~ShellSidebarSyncDotTests"`
Beklenen: PASS, 0 uyarı

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.App/ViewModels/MainShellViewModel.InstagramHealth.cs OrderDeck.App/ViewModels/MainShellViewModel.cs \
  OrderDeck.App/ViewModels/PlatformConnectionViewModel.cs OrderDeck.App/Views/Shell/ShellSidebar.xaml \
  OrderDeck.App/Views/Shell/ChatPanel.xaml OrderDeck.App/AppHost.cs OrderDeck.Tests/App/MainShellTestHarness.cs \
  OrderDeck.Tests/App/MainShellInstagramHealthTests.cs OrderDeck.Tests/App/InstagramHealthDiTests.cs \
  OrderDeck.Tests/App/ChatPanelLayoutTests.cs
git commit -F - <<'MSG'
feat(instagram): hesap sağlığını sohbet şeridinde, üst noktada ve kenar çubuğunda göster

Bir hesabın sessizce susması operatörden gizli kalmasın (AH3): durum sohbet
panelinin başındaki şeritte, sarı durumlar üst bardaki sohbet noktasında
görünür; kenar çubuğundaki Instagram noktası en az bir hesap canlıyken yanar.
İzleyici tekildir; hosted service ve kabuk aynı örneği okur.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G16: Ayarlar: hesap listesi, "Kaldır", tazeleme

**Spec:** §8.6.1 (kart görünümleri, satır durumu maddeleri, "Kaldır", "ViewModel eklemeleri", iki ek tazeleme); §8.3.2 yan fark a, c; §9.4 `SettingsViewModel_InstagramAccountsTests`; Review Focus 3.
**Bağımlı:** G02, G05.

**Files:**
- Create: `OrderDeck.App/Services/Instagram/InstagramSettingsTexts.cs`, `OrderDeck.App/ViewModels/InstagramPageRow.cs`
- Modify: `OrderDeck.App/ViewModels/SettingsViewModel.cs` (alanlar `:102-118`; `RefreshInstagramStatusAsync` `:362-414`; `ConnectFacebook` `:445`; `DisconnectFacebook` `:470-471`)
- Modify: `OrderDeck.App/Views/Pages/SettingsPage.xaml` (ad alanları `:1-5`; Instagram kartı `:306-324`; Facebook kartı `:279-304` DEĞİŞMEZ)
- Test: `OrderDeck.Tests/ViewModels/SettingsViewModelInstagramAccountsTests.cs` (yeni; sınıf `SettingsViewModel_InstagramAccountsTests`)

**Interfaces:**
- Consumes: G02 `Task<IReadOnlyList<FacebookLinkedPage>> GetConnectedPagesAsync(CancellationToken ct = default)`, `Task<bool> RemoveExtraPageAsync(string pageId, CancellationToken ct = default)`; kit `FacebookOAuthTestKit` (`CreateService(ILogger<FacebookOAuthService>? log = null)`, `Settings`, `HttpFactory`, `Graph`, `Task<IReadOnlyList<SeededPage>> SeedAsync(int extraPages = 0)`, `static string NewId()`), `SeededPage(FacebookLinkedPage Page, string? IgUserId, string? IgUsername)`, `FakeGraph.AddPage(string pageId, string pageName, string? igUserId = null, string? igUsername = null)`, `FakeGraph.Map(Func<Uri, bool> match, Func<Uri, HttpResponseMessage> respond)`, `FakeGraph.Requests`, `FakeGraph.Error(int code, HttpStatusCode status = HttpStatusCode.BadRequest, int? subcode = null)`. G05 `Task<(InstagramAccount? Account, InstagramResolveStatus Status)> ResolveDetailedAsync(string pageId, string pageAccessToken, CancellationToken ct)`, `InstagramResolveStatus { Found, NotLinked, Failed }`. Mevcut `InstagramPermissionProbe.HasInstagramPermissionsAsync(string userAccessToken, CancellationToken ct = default)`.
- Produces: `public sealed record InstagramPageRow(string PageId, string PageName, string InstagramText, bool IsPrimary, bool CanRemove, bool IsWarning);` · `public ObservableCollection<InstagramPageRow> InstagramPages { get; }` · `public string? InstagramNotice` (gözlenebilir) · `RemoveInstagramAccountCommand` (`IAsyncRelayCommand<InstagramPageRow?>`) · `DismissInstagramNoticeCommand` · `public bool ShowInstagramPageRows` · `public bool CanManageInstagram` · `public static class InstagramSettingsTexts { public static string AccountLabel(string igUserId, string? username); … }` (G17 ve G18 ekler).

**Kurallar:**
- Tazeleme `GetConnectedPagesAsync` üzerinde döner. Servis yoksa ya da liste boşsa: satırlar boşalır, durum `Önce Facebook'a bağlan.`, `InstagramNeedsReconnect = false`. Doluysa izin sondası BİR kez koşar (bugünkü `:386-395` mantığı), sonra Sayfa başına `ResolveDetailedAsync`. Genel hata dalı (`:409-413`) aynen kalır.
- Satır: ilk Sayfa birincildir (`IsPrimary`, `CanRemove = false`). `InstagramText`: `Found` → `AccountLabel(IgUserId, Username)` = `"@" + (Username ?? IgUserId)` (bugünkü `:407` kuralı); `NotLinked` → `Bu Sayfaya bağlı Instagram profesyonel hesabı yok`, `IsWarning` = Sayfa sayısı ≥ 2 (birincil satır dahil); `Failed` → `Durum okunamadı`, nötr.
- Durum satırı (`InstagramAccountStatus`): tek Sayfada bugünkü tam metinler (`Bağlı Instagram hesabı: {etiket}` / `Bu Sayfa'ya bağlı Instagram profesyonel hesabı yok.`; yalnız `Failed` ise `Durum okunamadı.`); 2 ve üzeri Sayfada `{N} Instagram hesabı bağlı`, N = `Found` satırı sayısı (bağlı Sayfa sayısı değil).
- Tazelemeler sıraya sokulur (kurucu `:209`'da bir tane başlatır). Satırlar yerel listede kurulur ve koleksiyona tek seferde yerleştirilir: araya giren ikinci tazeleme çift satır üretemez.
- "Kaldır" yalnız `CanRemove` satırda çalışır: `RemoveExtraPageAsync` (dönüş değeri akışı değiştirmez) → satır koleksiyondan HEMEN çıkar (tazelemenin Graph istekleri beklenmez) → `{etiket} kaldırıldı.` → tazeleme. Etiket: satır metni `@` ile başlıyorsa o, değilse Sayfa adı. Yazım düşerse metin `Hesap kaldırılamadı, yeniden dene.` olur (karar; spec metin vermiyor), satır kalır.
- `ConnectFacebook` ve `DisconnectFacebook`: `RefreshFacebookConnectionStatusAsync` çağrısının ardından `RefreshInstagramStatusAsync` de beklenir; komutların başka kodu değişmez. Bağlan tarafının testi G18'dedir (tarayıcı dikişi G03'te gelir).
- `ShowInstagramPageRows` = satır sayısı ≥ 2 (koleksiyon değişince bildirilir). `CanManageInstagram` = `IsFacebookConnected && !IsFacebookBusy` (ikisi değişince bildirilir).
- Sonuç metni kart içi satırdadır, "Tamam" kapatır (`ShippingNotice` kalıbı: `SettingsViewModel.cs:66-68`, `:644-645`, `SettingsPage.xaml:62-82`). Satır `InstagramNotice` null iken `Collapsed` olmalı: mevcut `SettingsViewModel_LicenseShippingTests.NoticeStripVisible` görünür her "Tamam"lı şeridi sayar. Açılır pencere ve `IDialogService` YOK. Kartın mevcut ipucu (`:318-322`) ve izin uyarısı aynen kalır (uyarı metnini G18 değiştirir).
- Metinler `InstagramSettingsTexts`'te sabit ya da işlev olarak durur (emsal `SalesCommandMessages`); `AccountLabel` dışındaki adları uygulayıcı seçer, XAML onlara `x:Static` ile bağlanır (emsal `SettingsPage.xaml:548`). Testte geçmeyen tek metin birincil satırın ekidir: ` · birincil (Facebook yorumları ve moderasyon bu Sayfadan)`.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// OrderDeck.Tests/ViewModels/SettingsViewModelInstagramAccountsTests.cs — namespace OrderDeck.Tests.ViewModels
public class SettingsViewModel_InstagramAccountsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"orderdeck-svm-ig-{Guid.NewGuid():N}.json");
    private readonly FacebookOAuthTestKit _kit = new();
    private IReadOnlyList<SeededPage> _pages = Array.Empty<SeededPage>();
    private FacebookOAuthService _service = null!;

    public void Dispose() { _kit.Dispose(); if (File.Exists(_path)) File.Delete(_path); }

    // SettingsViewModel_PaymentTests.CreateVm'in kopyası (NotFoundHandler dahil): ayarlar _kit.Settings, depo _path; ek argümanlar
    // facebookOAuth: oauth, httpFactory: _kit.HttpFactory.
    private SettingsViewModel CreateVm(FacebookOAuthService? oauth);
    private async Task SeedAsync(int extraPages = 0) => _pages = await _kit.SeedAsync(extraPages);
    // _service = _kit.CreateService(); CreateVm(_service); RefreshFacebookConnectionStatusAsync ve RefreshInstagramStatusAsync beklenir
    // (kurucunun başlattığı tazeleme de bitmiş olur).
    private async Task<SettingsViewModel> OpenAsync();
    private void MakeBare(int index) => _kit.Graph.AddPage(_pages[index].Page.PageId, _pages[index].Page.PageName);   // o Sayfa Instagram'sız olur
    private static (string Text, bool Warning) RowAt(SettingsViewModel vm, int i) => (vm.InstagramPages[i].InstagramText, vm.InstagramPages[i].IsWarning);
    private static bool IsResolve(Uri u, string pageId) => u.AbsolutePath.EndsWith("/" + pageId, StringComparison.Ordinal)
        && u.Query.Contains("instagram_business_account", StringComparison.Ordinal);
    private static bool IsPermissions(Uri u) => u.AbsolutePath.EndsWith("/me/permissions", StringComparison.Ordinal);
    private static void OnSta(Action body) => ThemeTestHost.RunOnSta(body).Should().BeNull();
    // SettingsViewModel_LicenseShippingTests.Render'ın kopyası + "Facebook" sekmesini seçer, Pump() ve yeniden yerleşim.
    private static SettingsPage RenderFacebookTab(SettingsViewModel vm);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root);        // görsel ağaç
    private static bool Shown(FrameworkElement e) => e.ActualWidth > 0 && e.ActualHeight > 0;
    private static IEnumerable<TextBlock> ShownTexts(DependencyObject root) => Descendants(root).OfType<TextBlock>().Where(Shown);

    [Fact]
    public async Task Tek_Sayfada_durum_metni_bugunku_satirlar_gizli()
    {
        await SeedAsync();
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be($"Bağlı Instagram hesabı: @{_pages[0].IgUsername}");
        (vm.InstagramPages.Count, vm.ShowInstagramPageRows, vm.CanManageInstagram).Should().Be((1, false, true));
    }

    [Theory]                                                            // hesap yok: bugünkü metin; çözümleme düştü: "Durum okunamadı."
    [InlineData(false, "Bu Sayfa'ya bağlı Instagram profesyonel hesabı yok.", "Bu Sayfaya bağlı Instagram profesyonel hesabı yok")]
    [InlineData(true, "Durum okunamadı.", "Durum okunamadı")]
    public async Task Tek_Sayfada_hesap_yoksa_ya_da_cozumleme_duserse_metin_ayri_satir_notr(bool failed, string status, string row)
    {
        await SeedAsync();
        if (failed) _kit.Graph.Map(u => IsResolve(u, _pages[0].Page.PageId), _ => FakeGraph.Error(2, HttpStatusCode.InternalServerError));
        else MakeBare(0);
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be(status);
        RowAt(vm, 0).Should().Be((row, false));
    }

    [Fact]
    public async Task Iki_Sayfada_iki_satir_birincil_kaldirilamaz_izin_sondasi_bir_kez()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        var probes = _kit.Graph.Requests.Count(IsPermissions);
        await vm.RefreshInstagramStatusAsync();
        _kit.Graph.Requests.Count(IsPermissions).Should().Be(probes + 1, "izin sondası tazeleme başına bir kez");
        (vm.InstagramAccountStatus, vm.ShowInstagramPageRows).Should().Be(("2 Instagram hesabı bağlı", true));
        vm.InstagramPages.Should().Equal(
            new InstagramPageRow(_pages[0].Page.PageId, _pages[0].Page.PageName, $"@{_pages[0].IgUsername}", true, false, false),
            new InstagramPageRow(_pages[1].Page.PageId, _pages[1].Page.PageName, $"@{_pages[1].IgUsername}", false, true, false));
        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[0]);         // birincil kaldırılamaz
        vm.InstagramPages.Should().HaveCount(2);
        vm.InstagramNotice.Should().BeNull();
    }

    [Theory, InlineData(0), InlineData(1)]
    public async Task Iki_Sayfada_hesabi_olmayan_satir_uyarir_baslik_cozulen_hesabi_sayar(int bare)
    {
        await SeedAsync(extraPages: 1);
        MakeBare(bare);
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be("1 Instagram hesabı bağlı");
        RowAt(vm, bare).Should().Be(("Bu Sayfaya bağlı Instagram profesyonel hesabı yok", true));
        vm.InstagramPages[1 - bare].IsWarning.Should().BeFalse();
        if (bare == 0) return;                                          // hesapsız ek Sayfa kaldırılınca metin Sayfa adını söyler
        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);
        vm.InstagramNotice.Should().Be($"{_pages[1].Page.PageName} kaldırıldı.");
    }

    [Fact]
    public async Task Kaldir_satiri_aninda_kaldirir_metni_yazar_karti_tazeler_Tamam_kapatir()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        var before = _kit.Graph.Requests.Count;
        int? atRemoval = null;
        vm.InstagramPages.CollectionChanged += (_, e) =>
        { if (e.Action == NotifyCollectionChangedAction.Remove) atRemoval ??= _kit.Graph.Requests.Count; };
        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);
        atRemoval.Should().Be(before, "satır tazelemenin Graph isteklerini beklemeden kalkar");
        vm.InstagramNotice.Should().Be($"@{_pages[1].IgUsername} kaldırıldı.");
        (await _service.GetConnectedPagesAsync()).Select(p => p.PageId).Should().Equal(_pages[0].Page.PageId);
        vm.InstagramAccountStatus.Should().Be($"Bağlı Instagram hesabı: @{_pages[0].IgUsername}");
        vm.DismissInstagramNoticeCommand.Execute(null);
        vm.InstagramNotice.Should().BeNull();
    }

    [Fact]
    public async Task Baglanti_kaldirilinca_kart_hemen_tazelenir()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        await vm.DisconnectFacebookCommand.ExecuteAsync(null);
        (vm.InstagramPages.Count, vm.InstagramAccountStatus).Should().Be((0, "Önce Facebook'a bağlan."));
        (vm.ShowInstagramPageRows, vm.CanManageInstagram).Should().Be((false, false));
    }

    [Fact]
    public async Task Es_zamanli_tazelemeler_cift_satir_uretmez()
    {
        await SeedAsync(extraPages: 1);
        var vm = CreateVm(_kit.CreateService());                        // kurucu bir tazeleme başlattı
        await Task.WhenAll(vm.RefreshInstagramStatusAsync(), vm.RefreshInstagramStatusAsync());
        vm.InstagramPages.Should().HaveCount(2);
    }

    [Fact]                                                              // Review Focus 3
    public void Kart_satirlari_yalniz_iki_Sayfada_cizer_Kaldir_yalniz_ek_satirda() => OnSta(() =>
    {
        var primary = new InstagramPageRow(FacebookOAuthTestKit.NewId(), $"Sayfa {Guid.NewGuid():N}", $"@hesap-{Guid.NewGuid():N}", true, false, false);
        var extra = new InstagramPageRow(FacebookOAuthTestKit.NewId(), $"Sayfa {Guid.NewGuid():N}", "Bu Sayfaya bağlı Instagram profesyonel hesabı yok", false, true, true);
        var single = CreateVm(oauth: null);
        single.InstagramPages.Add(primary);
        ShownTexts(RenderFacebookTab(single)).Should().NotContain(t => t.Text == primary.InstagramText, "tek Sayfada satır yok");

        var vm = CreateVm(oauth: null);
        vm.InstagramPages.Add(primary);
        vm.InstagramPages.Add(extra);
        vm.InstagramNotice = $"sonuç {Guid.NewGuid():N}";
        var page = RenderFacebookTab(vm);
        foreach (var row in new[] { primary, extra })
            ShownTexts(page).Where(t => ReferenceEquals(t.DataContext, row)).Select(t => t.Text)
                .Should().Contain(new[] { row.InstagramText, $"Sayfa: {row.PageName}" });
        var remove = Descendants(page).OfType<Button>().Where(b => Equals(b.Content, "Kaldır") && Shown(b)).Should().ContainSingle().Subject;
        remove.DataContext.Should().BeSameAs(extra);
        remove.CommandParameter.Should().BeSameAs(extra);
        remove.Command.Should().BeSameAs(vm.RemoveInstagramAccountCommand);
        ShownTexts(page).Single(t => t.Text == extra.InstagramText).Foreground.Should().BeSameAs(page.FindResource("OD.Brush.Amber"));
        Descendants(page).OfType<FrameworkElement>().Should().Contain(e => ReferenceEquals(e.DataContext, primary)
            && Equals(e.ToolTip, "Birincil Sayfayı değiştirmek için \"Bağlantıyı Kaldır\" + \"Facebook'a Bağlan\""));
        ShownTexts(page).Select(t => t.Text).Should().Contain(new[] { vm.InstagramNotice!,
            "İki hesap bağlıyken yorumlar tek akışta birleşir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar." });
        Descendants(page).OfType<Button>().Should().Contain(b => Equals(b.Content, "Tamam") && Shown(b)
            && ReferenceEquals(b.Command, vm.DismissInstagramNoticeCommand));
    });
}
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SettingsViewModel_InstagramAccountsTests"`
Beklenen: FAIL (derleme hatası: `InstagramPageRow`, `InstagramPages`, `InstagramNotice`, `ShowInstagramPageRows`, `CanManageInstagram`, iki komut yok)

- [ ] **Adım 3: Metin sınıfını, satır kaydını, listeyi, tazelemeyi ve iki komutu uygula (`InstagramSettingsTexts.cs`, `InstagramPageRow.cs`, `SettingsViewModel.cs`)**

`InstagramPageRow` Produces'taki kayıttır (ad alanı `OrderDeck.App.ViewModels`); metin sınıfının ad alanı `OrderDeck.App.Services.Instagram`. `SettingsViewModel`'e eklenenler: `InstagramPages`, `[ObservableProperty] private string? _instagramNotice;`, hesaplanan `ShowInstagramPageRows` (kurucuda `InstagramPages.CollectionChanged` aboneliğiyle bildirilir) ve `CanManageInstagram` (`OnIsFacebookBusyChanged` / `OnIsFacebookConnectedChanged` içinden bildirilir), `[RelayCommand] private async Task RemoveInstagramAccount(InstagramPageRow? row)`, `[RelayCommand] private void DismissInstagramNotice()`. `RefreshInstagramStatusAsync` Kurallar'daki sırayla yeniden yazılır; sıraya sokma tek bir `SemaphoreSlim(1, 1)` iledir, beklemeler `ConfigureAwait(true)` taşır. `:445` ve `:471` satırlarının ardına `await RefreshInstagramStatusAsync().ConfigureAwait(true);`.

- [ ] **Adım 4: Kartı uygula (`SettingsPage.xaml`)**

Kök öğeye `xmlns:ig="clr-namespace:OrderDeck.App.Services.Instagram"`. Instagram kartına, izin uyarısı ile mevcut ipucu arasına sırayla: (1) satır listesi: `ItemsControl` (`ItemsSource` `InstagramPages`, `Visibility` = `ShowInstagramPageRows` + `BoolToVisibleConverter`); satır şablonu `DockPanel` (`IsPrimary` iken `ToolTip` = birincil ipucu): sağda `Kaldır` düğmesi (`OD.Button.Ghost`, `Visibility` = `CanRemove`, `Command` = sayfanın `RemoveInstagramAccountCommand`'ı (`RelativeSource AncestorType=ItemsControl` üzerinden `DataContext`), `CommandParameter="{Binding}"`), solda üç ayrı `TextBlock`: `InstagramText` (`IsWarning` iken `Foreground` `OD.Brush.Amber`, değilken `OD.Brush.Text`), `PageName` (`StringFormat` `Sayfa: {0}`), birincil eki (yalnız `IsPrimary` iken görünür). (2) sonuç satırı: `ShippingNotice` şeridinin (`:62-82`) kart içi eşi; metin `InstagramNotice`, `Tamam` → `DismissInstagramNoticeCommand`, `Visibility` = `InstagramNotice` + `NullToCollapsedConverter`. (3) iki hesap altyazısı (`OD.Text.Hint`), `Visibility` = `ShowInstagramPageRows`.

- [ ] **Adım 5: Testlerin geçtiğini gör**

Koş: Adım 2'deki komut
Beklenen: PASS

- [ ] **Adım 6: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q && DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SettingsViewModel_InstagramAccountsTests|FullyQualifiedName~SettingsViewModel_PaymentTests|FullyQualifiedName~SettingsViewModel_LicenseShippingTests|FullyQualifiedName~UnstyledControlGuardTests"`
Beklenen: PASS, 0 uyarı

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.App/Services/Instagram/InstagramSettingsTexts.cs OrderDeck.App/ViewModels/InstagramPageRow.cs \
  OrderDeck.App/ViewModels/SettingsViewModel.cs OrderDeck.App/Views/Pages/SettingsPage.xaml \
  OrderDeck.Tests/ViewModels/SettingsViewModelInstagramAccountsTests.cs
git commit -F - <<'MSG'
feat(instagram): Ayarlar'da bağlı hesapları listele ve ek hesabı kaldır

Kart bağlı her Sayfanın hesabını gösterir; ek hesap tek başına kaldırılır,
birincil kaldırılamaz. Kart bağlan ve kaldır sonrasında hemen tazelenir;
tek Sayfalı kurulumun durum metni bugünküyle aynıdır.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G17: Ayarlar: "Instagram hesabı ekle" (çekmece)

**Spec:** §8.6.1 "Instagram hesabı ekle" (adım 0–3, altı sonuç metni); §8.2 "Tarayıcısız listeleme" (izin sondası kuralı); §9.4 `SettingsViewModel_InstagramAccountsTests` ve `MainShellViewCompositionTests` (ek); TK5; Review Focus 3.
**Bağımlı:** G16.

**Files:**
- Create: `OrderDeck.App/ViewModels/InstagramAccountPickerViewModel.cs` (seçici + `InstagramAccountOption`), `OrderDeck.App/Views/Drawers/InstagramAccountAddDrawer.xaml`, `OrderDeck.App/Views/Drawers/InstagramAccountAddDrawer.xaml.cs`
- Modify: `OrderDeck.App/Services/Instagram/InstagramSettingsTexts.cs`, `OrderDeck.App/ViewModels/SettingsViewModel.cs` (bugünkü `:416-477` komut bölgesinin yanı; çapa `RemoveInstagramAccount`), `OrderDeck.App/Views/Pages/SettingsPage.xaml` (Instagram kartı; çapa `InstagramPages` listesi)
- Test: `OrderDeck.Tests/ViewModels/SettingsViewModelInstagramAccountsTests.cs` (Modify), `OrderDeck.Tests/App/MainShellViewCompositionTests.cs` (Modify: `:140-143` bloğunun altı)

**Interfaces:**
- Consumes: G02 `Task<FacebookPageListing> ListAddablePagesAsync(CancellationToken ct = default)`, `FacebookPageListing(IReadOnlyList<FacebookAddablePage> Pages, bool NeedsReauthorize)`, `FacebookAddablePage(string PageId, string PageName, string? IgUserId, string? IgUsername, bool AlreadyConnected)`, `Task<FacebookLinkedPage> AddExtraPageAsync(string pageId, string pageName, CancellationToken ct = default)`, `FacebookPageLimitException : InvalidOperationException`, `FacebookGraphException : HttpRequestException` (`int? Code`, `int? Subcode`, taban `StatusCode`), `FacebookOAuthService.MaxInstagramPages`; kit `Broker.ConfigCalls` / `ExchangeCalls`, `FakeGraph.Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? usageHeader = null)`. G16 `InstagramNotice`, `InstagramAccountStatus`, `CanManageInstagram`, `RefreshInstagramStatusAsync()`, `InstagramSettingsTexts.AccountLabel(string igUserId, string? username)` ve test sınıfının yardımcıları. Mevcut `IDrawerService.ShowAsync(string title, Func<Drawer, object> buildContent)`, `InstagramNeedsReconnect`, `_drawers` (`SettingsViewModel.cs:159`).
- Produces: `AddInstagramAccountCommand` (`IAsyncRelayCommand`) · `public InstagramAccountPickerViewModel? ActiveInstagramPicker { get; private set; }` (çekmece açıkken dolu; emsal `ActiveVariantPicker`, `MainShellViewModel.cs:1814`) · `public sealed record InstagramAccountOption(string PageId, string PageName, string Display, bool CanSelect);` · `public sealed partial class InstagramAccountPickerViewModel : ObservableObject { public InstagramAccountPickerViewModel(IReadOnlyList<FacebookAddablePage> pages); public IReadOnlyList<InstagramAccountOption> Options { get; } public InstagramAccountOption? Selected { get; set; } }` (`Selected` gözlenebilir) · `public static InstagramAccountAddDrawer Create(Drawer drawer, InstagramAccountPickerViewModel picker)`.

**Kurallar:**
- Adım 0: `InstagramNeedsReconnect` true ise listeleme, çekmece ve yeni Graph isteği YOK; metin `Facebook oturumunun yenilenmesi gerekiyor. "Facebook izinlerini güncelle"ye bas.`
- Adım 1: `Sayfalar okunuyor...` kartın durum satırına (`InstagramAccountStatus`) yazılır. Listeleme bitince (her sonuçta, çekmece açılmadan ÖNCE) durum satırı hâlâ bu metinse önceki metin geri konur; yeni Graph isteği atılmaz.
- Listeleme sonucu: istisna (HER tür) → `Sayfalar okunamadı, internetini kontrol edip yeniden dene.`; `NeedsReauthorize` → adım 0'ın metni; bağlı olmayan ve Instagram hesabı olan Sayfa yoksa → "Eklenebilecek başka hesap bulunamadı…" metni (tamamı testte), çekmece açılmaz (Instagram'sız aday tek başına çekmece açtırmaz).
- Çekmece: başlık `Instagram hesabı ekle`; seçenekler `AlreadyConnected` olmayan Sayfalardır, listeleme sırasıyla; `IgUserId` null ise `CanSelect = false`; ilk seçilebilir seçenek önceden seçilidir; seçim yokken "Ekle" kapatmaz. Seçenek metni: hesap varsa `{AccountLabel} — Sayfa: {Sayfa adı}`, yoksa `{Sayfa adı} — Instagram hesabı bağlı değil` (ayraç U+2014). `_drawers` null ise ya da çekmece "İptal" ile kapanırsa hiçbir şey yazılmaz.
- Çekmecenin testte geçmeyen metinleri: üstte `Yorumlarını da okumak istediğin hesabı seç:`; düğmeler `Ekle` ve `İptal`; altta iki satır: `Aradığın hesap listede yok mu? Önce Instagram hesabını bir Facebook Sayfasına bağla.` ve `Sayfa yine görünmüyorsa çekmeceyi kapatıp "Facebook izinlerini güncelle"ye bas.`
- Ekleme sonucu: başarı → `{AccountLabel} eklendi. Yayın açıksa birkaç saniye içinde okunmaya başlar.` + tazeleme; `FacebookPageLimitException` → `En çok 4 Instagram hesabı bağlanabilir.` (sayı `MaxInstagramPages`'ten); geçici hata → "Sayfalar okunamadı…"; başka HER hata → `"{Sayfa adı}" için yetki alınamadı: bu Sayfada yönetici olduğundan emin ol.` Geçici hata: `FacebookGraphException` ve (`StatusCode` ≥ 500 ya da `Code` 4 / 17 / 32 / 613 / 80002 ya da `Subcode` 2446079: `InstagramGraphError.Classify`'ın kota kümesi); `FacebookGraphException` OLMAYAN `HttpRequestException` (ağ); `TaskCanceledException`; `TimeoutException`.
- Sonuç metinleri `InstagramNotice`'e yazılır. Tarayıcı ve sunucu takası çağrılmaz (TK5). Komut arayüz iş parçacığında kalır (`ConfigureAwait(true)`); `Application.Current.Dispatcher` KULLANILMAZ (testte asılır).
- Düğme `CanManageInstagram`'a bağlıdır. `FacebookPagePickerDrawer.*` DEĞİŞMEZ. Seçim öğesi `ListBox` + kap stilidir (`FacebookPagePickerDrawer.xaml:14-41` kalıbı); ham `RadioButton` `UnstyledControlGuardTests`'e takılır.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// SettingsViewModel_InstagramAccountsTests — G17 ekleri. CreateVm artık drawers: _drawers de verir; OpenAsync _drawers.Vm'i doldurur.
private sealed class FakeDrawers : IDrawerService             // içerik fabrikasını ÇAĞIRMAZ (VariantPickerFlowTests.FakeDrawerService kalıbı)
{
    public Func<SettingsViewModel, bool> Decide { get; set; } = _ => false;         // varsayılan: İptal / Vazgeç
    public SettingsViewModel? Vm { get; set; }
    public List<string> Titles { get; } = new();
    public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent) { Titles.Add(title); return Task.FromResult(Decide(Vm!)); }
    public bool CloseTop() => false;
}
private readonly FakeDrawers _drawers = new();
private const string ReconnectText = "Facebook oturumunun yenilenmesi gerekiyor. \"Facebook izinlerini güncelle\"ye bas.";
private (string PageId, string PageName, string? IgUsername) AddCandidate(bool withInstagram = true);   // Graph'a bağlı olmayan yeni Sayfa (üretilmiş)
private static FacebookAddablePage NewAddable(bool withInstagram, bool connected = false);                // üretilmiş listeleme satırı
private static bool Pick(SettingsViewModel vm, string pageId)                                             // çekmecede o Sayfayı seçip "Ekle"
{
    vm.ActiveInstagramPicker!.Selected = vm.ActiveInstagramPicker.Options.Single(o => o.PageId == pageId);
    return true;
}
private static bool IsAccounts(Uri u) => u.AbsolutePath.EndsWith("/me/accounts", StringComparison.Ordinal);
private static bool IsPageToken(Uri u, string pageId) => u.AbsolutePath.EndsWith("/" + pageId, StringComparison.Ordinal)
    && u.Query.Contains("fields=access_token", StringComparison.Ordinal);

[Fact]
public async Task Ekle_secilen_hesabi_tarayicisiz_ekler_ve_karti_tazeler()
{
    await SeedAsync();
    var candidate = AddCandidate();
    var vm = await OpenAsync();
    var statuses = new List<string>();
    vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.InstagramAccountStatus)) statuses.Add(vm.InstagramAccountStatus); };
    string? statusAtDrawer = null;
    _drawers.Decide = v => { statusAtDrawer = v.InstagramAccountStatus; return Pick(v, candidate.PageId); };
    await vm.AddInstagramAccountCommand.ExecuteAsync(null);
    _drawers.Titles.Should().Equal("Instagram hesabı ekle");
    statuses.Should().Contain("Sayfalar okunuyor...");
    statusAtDrawer.Should().Be($"Bağlı Instagram hesabı: @{_pages[0].IgUsername}", "çekmeceden önce önceki metin geri konur");
    vm.InstagramNotice.Should().Be($"@{candidate.IgUsername} eklendi. Yayın açıksa birkaç saniye içinde okunmaya başlar.");
    vm.InstagramPages.Select(r => r.PageId).Should().Equal(_pages[0].Page.PageId, candidate.PageId);
    vm.InstagramAccountStatus.Should().Be("2 Instagram hesabı bağlı");
    vm.ActiveInstagramPicker.Should().BeNull();
    (_kit.Broker.ConfigCalls, _kit.Broker.ExchangeCalls).Should().Be((0, 0), "TK5: tarayıcı ve sunucu takası yok");
}

[Fact]
public async Task Eklenebilecek_hesap_yoksa_cekmece_acilmaz_yonlendirme_metni_yazilir()
{
    await SeedAsync();
    AddCandidate(withInstagram: false);
    var vm = await OpenAsync();
    await vm.AddInstagramAccountCommand.ExecuteAsync(null);
    _drawers.Titles.Should().BeEmpty();
    vm.InstagramNotice.Should().Be("Eklenebilecek başka hesap bulunamadı. Yeni hesabı önce bir Facebook Sayfasına bağla; Sayfa hazırsa \"Facebook izinlerini güncelle\"ye bas ve açılan Facebook ekranında TÜM Sayfalarını ve Instagram hesaplarını işaretle.");
}

[Theory, InlineData(true), InlineData(false)]      // true: izin sondası "izin yok" dedi (0. adım); false: listeleme 190 döndü (NeedsReauthorize)
public async Task Oturum_yenilenmeliyse_cekmece_acilmaz_iki_yol_ayni_metne_varir(bool probeSaysNo)
{
    await SeedAsync();
    AddCandidate();
    if (probeSaysNo) _kit.Graph.Map(IsPermissions, _ => FakeGraph.Json("""{"data":[]}"""));
    else _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(190));
    var vm = await OpenAsync();
    var listings = _kit.Graph.Requests.Count(IsAccounts);
    await vm.AddInstagramAccountCommand.ExecuteAsync(null);
    vm.InstagramNeedsReconnect.Should().Be(probeSaysNo);
    _kit.Graph.Requests.Count(IsAccounts).Should().Be(probeSaysNo ? listings : listings + 1, "0. adımda listeleme yapılmaz");
    (_drawers.Titles.Count, vm.InstagramNotice).Should().Be((0, ReconnectText));
}

[Theory]                                                                // karar eki: sınır, yetki ve geçici hata metinleri
[InlineData("sinir"), InlineData("yetki"), InlineData("listeleme-5xx"), InlineData("listeleme-ag"), InlineData("ekleme-5xx"), InlineData("ekleme-kota")]
public async Task Ekleme_hatalari_kendi_metnine_duser_hicbir_sey_eklenmez_durum_metni_geri_konur(string fault)
{
    await SeedAsync(extraPages: fault == "sinir" ? 3 : 0);              // "sinir": 4 Sayfa bağlı, AddExtraPageAsync reddeder
    var candidate = AddCandidate();
    var vm = await OpenAsync();
    _drawers.Decide = v => Pick(v, candidate.PageId);
    var (status, rows) = (vm.InstagramAccountStatus, vm.InstagramPages.Count);
    if (fault == "yetki") _kit.Graph.Map(u => IsPageToken(u, candidate.PageId), _ => FakeGraph.Json("{}"));     // Sayfa yetkisi türetilemez
    if (fault == "listeleme-5xx") _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(2, HttpStatusCode.ServiceUnavailable));
    if (fault == "listeleme-ag") _kit.Graph.Map(IsAccounts, _ => throw new HttpRequestException("ağ yok"));
    if (fault == "ekleme-5xx") _kit.Graph.Map(u => IsPageToken(u, candidate.PageId), _ => FakeGraph.Error(2, HttpStatusCode.ServiceUnavailable));
    if (fault == "ekleme-kota") _kit.Graph.Map(u => IsPageToken(u, candidate.PageId), _ => FakeGraph.Error(613));
    await vm.AddInstagramAccountCommand.ExecuteAsync(null);
    vm.InstagramNotice.Should().Be(fault switch
    {
        "sinir" => "En çok 4 Instagram hesabı bağlanabilir.",
        "yetki" => $"\"{candidate.PageName}\" için yetki alınamadı: bu Sayfada yönetici olduğundan emin ol.",
        _ => "Sayfalar okunamadı, internetini kontrol edip yeniden dene.",
    });
    (vm.InstagramAccountStatus, vm.InstagramPages.Count).Should().Be((status, rows));
}

[Fact]
public async Task Cekmecede_vazgecilirse_hicbir_sey_degismez()
{
    await SeedAsync();
    AddCandidate();
    var vm = await OpenAsync();                                         // Decide varsayılanı: İptal
    await vm.AddInstagramAccountCommand.ExecuteAsync(null);
    _drawers.Titles.Should().ContainSingle();
    vm.InstagramNotice.Should().BeNull();
    vm.InstagramPages.Should().ContainSingle();
}

[Fact]
public void Secici_bagli_Sayfalari_eler_hesapsiz_Sayfayi_pasif_gosterir_ilk_secilebiliri_secer()
{
    var (connected, bare, addable) = (NewAddable(true, connected: true), NewAddable(false), NewAddable(true));
    var picker = new InstagramAccountPickerViewModel(new[] { connected, bare, addable });
    picker.Options.Should().Equal(
        new InstagramAccountOption(bare.PageId, bare.PageName, $"{bare.PageName} — Instagram hesabı bağlı değil", false),
        new InstagramAccountOption(addable.PageId, addable.PageName, $"@{addable.IgUsername} — Sayfa: {addable.PageName}", true));
    picker.Selected.Should().Be(picker.Options[1]);
}

[Fact]                                                                  // Review Focus 3
public void Cekmece_secenekleri_cizer_hesapsiz_Sayfa_pasif_secim_yokken_Ekle_kapatmaz() => OnSta(() =>
{
    var picker = new InstagramAccountPickerViewModel(new[] { NewAddable(false), NewAddable(true) });
    var stack = new DrawerStack();
    var closed = stack.ShowAsync("Instagram hesabı ekle", d => InstagramAccountAddDrawer.Create(d, picker));
    var view = (InstagramAccountAddDrawer)stack.Top!.Content!;
    view.Measure(new Size(400, 700));
    view.Arrange(new Rect(0, 0, 400, 700));
    ThemeTestHost.Pump();
    view.UpdateLayout();
    Descendants(view).OfType<ListBoxItem>().Select(i => i.IsEnabled).Should().Equal(picker.Options.Select(o => o.CanSelect));
    ShownTexts(view).Select(t => t.Text).Should().Contain(picker.Options.Select(o => o.Display));
    var add = Descendants(view).OfType<Button>().Single(b => Equals(b.Content, "Ekle"));
    picker.Selected = null;
    add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    closed.IsCompleted.Should().BeFalse("seçim yokken Ekle kapatmaz");
    picker.Selected = picker.Options.Single(o => o.CanSelect);
    add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    (closed.IsCompletedSuccessfully && closed.Result).Should().BeTrue();
});

[Fact]                                                                  // Review Focus 3
public void Kartta_Instagram_hesabi_ekle_dugmesi_bagli_degilken_pasif() => OnSta(() =>
{
    Button Add(SettingsViewModel vm) => Descendants(RenderFacebookTab(vm)).OfType<Button>().Single(b => Equals(b.Content, "Instagram hesabı ekle"));
    var offline = CreateVm(oauth: null);
    var button = Add(offline);
    button.IsEnabled.Should().BeFalse();
    button.Command.Should().BeSameAs(offline.AddInstagramAccountCommand);
    var connected = CreateVm(oauth: null);
    connected.IsFacebookConnected = true;
    Add(connected).IsEnabled.Should().BeTrue();
});

// MainShellViewCompositionTests.Shell_controls_and_main_shell_resolve — pagePicker bloğunun (:140-143) altına
var instagramAdd = new DrawerStack();
instagramAdd.ShowAsync("Instagram hesabı ekle", d => InstagramAccountAddDrawer.Create(d, BuildInstagramPicker()));
Assert.IsType<InstagramAccountAddDrawer>(instagramAdd.Top!.Content);
// Biri Instagram hesaplı, biri hesapsız iki aday; değerler üretilir (BuildPageCandidates'in literal deseni kopyalanmaz).
private static InstagramAccountPickerViewModel BuildInstagramPicker();
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SettingsViewModel_InstagramAccountsTests|FullyQualifiedName~MainShellViewCompositionTests"`
Beklenen: FAIL (derleme hatası: `AddInstagramAccountCommand`, `ActiveInstagramPicker`, `InstagramAccountPickerViewModel`, `InstagramAccountOption`, `InstagramAccountAddDrawer` yok)

- [ ] **Adım 3: Metinleri, seçici modeli ve komutu uygula (`InstagramSettingsTexts.cs`, `InstagramAccountPickerViewModel.cs`, `SettingsViewModel.cs`)**

Seçici kurucusu `AlreadyConnected` Sayfaları eler, `Display` ve `CanSelect`'i Kurallar'daki gibi kurar, `Selected`'ı (`[ObservableProperty]`) ilk seçilebilir seçeneğe koyar. `[RelayCommand] private async Task AddInstagramAccount()` Kurallar'daki sırayı izler: adım 0 → durum metni → `ListAddablePagesAsync` → (aday varsa) `ActiveInstagramPicker` dolu iken `_drawers.ShowAsync("Instagram hesabı ekle", d => InstagramAccountAddDrawer.Create(d, picker))` → `AddExtraPageAsync(seçilen.PageId, seçilen.PageName)` → sonuç metni → başarıda `RefreshInstagramStatusAsync`. Başarı etiketi listeleme satırından `AccountLabel(IgUserId, IgUsername)` ile kurulur.

- [ ] **Adım 4: Çekmeceyi ve düğmeyi uygula (`InstagramAccountAddDrawer.xaml`, `.xaml.cs`, `SettingsPage.xaml`)**

Çekmece kalıbı `VariantPickerDrawer` (private kurucu + `Create`, `DataContext = picker`). Gövde üstten alta: seçim cümlesi (`OD.Text.Label`); `ListBox` (`OD.ListBox`, `ItemsSource` `Options`, `SelectedItem` `Selected` iki yönlü, `SelectionMode="Single"`, öğe şablonu `Display`'i gösteren `TextBlock`); kap stili `FacebookPagePickerDrawer.xaml:14-41`'in kopyası + `IsEnabled` = `CanSelect` ve pasifken `Opacity` 0.5; `Ekle` (`OD.Button.Primary`, `IsDefault`); `İptal` (`OD.Button.Ghost`); iki yardım satırı (`OD.Text.Hint`). Arka kod: "Ekle" yalnız `Selected` seçilebilir bir seçenekse `_drawer.Close(true)`; "İptal" `_drawer.Close(false)`. `SettingsPage.xaml`: Instagram kartında satır listesinin altına yatay `StackPanel`; içinde `Instagram hesabı ekle` düğmesi (`OD.Button.Secondary`, `Command` `AddInstagramAccountCommand`, `IsEnabled` `CanManageInstagram`, `Margin` `OD.Pad.X2`). G18 aynı satıra iki düğme ekler.

- [ ] **Adım 5: Testlerin geçtiğini gör**

Koş: Adım 2'deki komut
Beklenen: PASS

- [ ] **Adım 6: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q && DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SettingsViewModel_InstagramAccountsTests|FullyQualifiedName~MainShellViewCompositionTests|FullyQualifiedName~UnstyledControlGuardTests|FullyQualifiedName~SettingsViewModel_PaymentTests|FullyQualifiedName~SettingsViewModel_LicenseShippingTests"`
Beklenen: PASS, 0 uyarı

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.App/ViewModels/InstagramAccountPickerViewModel.cs OrderDeck.App/Views/Drawers/InstagramAccountAddDrawer.xaml \
  OrderDeck.App/Views/Drawers/InstagramAccountAddDrawer.xaml.cs OrderDeck.App/Services/Instagram/InstagramSettingsTexts.cs \
  OrderDeck.App/ViewModels/SettingsViewModel.cs OrderDeck.App/Views/Pages/SettingsPage.xaml \
  OrderDeck.Tests/ViewModels/SettingsViewModelInstagramAccountsTests.cs OrderDeck.Tests/App/MainShellViewCompositionTests.cs
git commit -F - <<'MSG'
feat(instagram): Ayarlar'dan tarayıcı açmadan ikinci Instagram hesabını ekle

"Instagram hesabı ekle" saklı Facebook girişiyle Sayfaları listeler ve seçileni
ek hesap olarak bağlar (TK5). İzin eksikse ya da oturum yenilenmeliyse
listelemeden "Facebook izinlerini güncelle"ye yönlendirir.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G18: Ayarlar: "Facebook izinlerini güncelle" ve "İptal"

**Spec:** §8.6.1 "Facebook izinlerini güncelle" (madde 1–6) ve izin uyarısı metni; §8.2 TK17 (arayüz kısmı); §8.3.2 değişen davranış 5–6; §9.4 `SettingsViewModel_InstagramAccountsTests`; TK4, TK17; R7, R18; Review Focus 1, 3.
**Bağımlı:** G03, G04, G17.

**Files:**
- Modify: `OrderDeck.App/ViewModels/SettingsViewModel.cs` (`IsFacebookIdle` `:105`; kurucu `:174-209`: bağlam yakalama; komut bölgesi, çapa `AddInstagramAccount`)
- Modify: `OrderDeck.App/Views/Pages/SettingsPage.xaml` (Instagram kartı: düğme satırı, çapa `AddInstagramAccountCommand`; izin uyarısı metni bugünkü `:313-317`)
- Modify: `OrderDeck.App/Services/Instagram/InstagramSettingsTexts.cs`
- Test: `OrderDeck.Tests/ViewModels/SettingsViewModelInstagramAccountsTests.cs` (Modify)

**Interfaces:**
- Consumes: G04 `Task<FacebookReauthorizeResult> ReauthorizeAsync(Func<IReadOnlyList<FacebookLinkedPage>, CancellationToken, Task<bool>> confirmMissing, CancellationToken ct = default)`, `FacebookReauthorizeResult(int RefreshedPages, IReadOnlyList<FacebookLinkedPage> MissingPages)`. G03 zaman aşımının `TimeoutException`'ı, tek akış reddinin `InvalidOperationException`'ı (mesaj `Bekleyen bir Facebook onayı var. Tarayıcıdaki onayı tamamla ya da birkaç dakika sonra yeniden dene.`), kit `Browser` (`Task WaitUntilOpenedAsync()`, `void Complete()`, `void CompleteWithError(string error, string? description = null)`), `CreateService(ILogger<FacebookOAuthService>? log = null, TimeSpan? callbackTimeout = null)`. G02 `FacebookGraphException` (`Code`, `StatusCode`), `GetConnectedPagesAsync`; mevcut `GetUserAccessTokenAsync`, `IsConnectedAsync`. G16 / G17 üyeleri (`InstagramNotice`, `InstagramAccountStatus`, `CanManageInstagram`, `RefreshInstagramStatusAsync()`, `AddInstagramAccountCommand`) ve test yardımcıları (`SeedAsync`, `OpenAsync`, `CreateVm`, `_drawers`, `AddCandidate`, `IsAccounts`, `IsPermissions`, `OnSta`, `RenderFacebookTab`, `Descendants`, `Shown`, `ShownTexts`). Mevcut `MessageDrawer.ForConfirm(Drawer drawer, string message)` (`MessageDrawer.xaml.cs:33`).
- Produces: `ReauthorizeFacebookCommand` (`IAsyncRelayCommand`) · `CancelReauthorizeCommand` · `public bool IsReauthorizePending` (gözlenebilir; `[İptal]` görünürlüğü ve mevcut `IsFacebookIdle`'ın üçüncü koşulu) · `public string? ActiveReauthorizeQuestion { get; private set; }` (onay çekmecesi açıkken soru metni; emsal `ActiveInstagramPicker`).

**Kurallar:**
- Beklerken `IsFacebookBusy = true` ve `IsReauthorizePending = true`; kartın durum satırına (`InstagramAccountStatus`) bekleme metni yazılır. Bitince (her sonuçta) iki bayrak false olur ve durum satırı hâlâ bekleme metniyse önceki metin geri konur (G17'deki kural). "Facebook izinlerini güncelle" ve "Instagram hesabı ekle" (`CanManageInstagram`) `IsFacebookBusy` üzerinden pasifleşir; "Facebook'a Bağlan" (`IsFacebookIdle`) ayrıca `IsReauthorizePending`'e bakar: `IsFacebookIdle` = `!IsFacebookBusy && !IsFacebookConnected && !IsReauthorizePending` olur ve `OnIsReauthorizePendingChanged` onu bildirir (komut kodu ve Facebook kartının XAML'i değişmez). `[İptal]` yalnız `IsReauthorizePending` iken görünür.
- `CancelReauthorizeCommand` bekleyen çağrının `CancellationTokenSource`'unu iptal eder; bekleyen yoksa etkisizdir.
- Sonuçlar `InstagramNotice`'e yazılır (metinler testlerde). Eksik Sayfa yoksa "…güncellendi: N Sayfa yenilendi."; varsa (onayda "Evet") aynı cümle + eksik Sayfa cümlesi. `catch` sırası: süzgeçli `OperationCanceledException` → iptal metni; süzgeç yalnız iki durumda geçer: bekleyen çağrının belirteci iptal edilmiş (`[İptal]`) ya da onay işlevi false dönmüş ("Vazgeç"; komut bunu yerel bir bayrakta tutar), yani `when (belirteç.IsCancellationRequested || onayReddedildi)`; `TimeoutException` → zaman aşımı metni; `FacebookGraphException` → `Güncelleme başarısız: Facebook hatası (kod {Code}). Bağlantı değişmedi.` (`Code` null ise `(HTTP {durum})`; ham Graph gövdesi yazılmaz; karar); `Exception` → `Güncelleme başarısız: {ex.Message, sondaki nokta ve boşluk kırpılmış}. Bağlantı değişmedi.` Facebook ekranındaki "İptal" (`access_denied`), tek akış reddi, bekleme sırasında bağlantının kaldırılması (G04'ün `InvalidOperationException`'ı) ve süzgece girmeyen `OperationCanceledException` (HttpClient zaman aşımının `TaskCanceledException`'ı: kullanıcı iptal etmedi) son dala düşer.
- Başarıdan sonra (eksik Sayfa "Evet" dahil) `RefreshInstagramStatusAsync`: izin sondası yeniden koşar, izin uyarısı ve eklemenin 0. adımı kalkar.
- Eksik Sayfa onayı kaydetmeden ÖNCE `_drawers.ShowAsync("Facebook izinlerini güncelle", d => MessageDrawer.ForConfirm(d, soru))` ile sorulur; "(Facebook yorumları dahil)" yalnız eksiklerden biri birincilse yazılır (birincil kimliği komut başlarken `GetConnectedPagesAsync`'in ilk girdisinden okunur). Birden çok eksik Sayfada (karar; spec yalnız tekili verir) adlar bağlı sırasıyla, tırnak içinde `", "` ile birleşir ve soru `bu Sayfanın` yerine `bu Sayfaların` der; sonuç cümlesi adları aynı biçimde sayar, başka sözcüğü değişmez (`"A", "B" bu girişte yoktu; yorumları kesilebilir.`). `ActiveReauthorizeQuestion` çekmece açıkken soruyu taşır, kapanınca null olur. `_drawers` null ise onay false döner.
- `confirmMissing` OAuth servisinin iş parçacığından çağrılır (servis içi `ConfigureAwait(false)`). Çekmeceye geçiş kurucuda yakalanan `SynchronizationContext.Current` ile yapılır (`Post` + `TaskCompletionSource<bool>`); yakalanan bağlam null ise doğrudan çağrılır. `Application.Current.Dispatcher` KULLANILMAZ (testte asılır).
- Kenar: bekleme sırasında "Bağlantıyı Kaldır" basılırsa `DisconnectFacebook`'un `finally`'si `IsFacebookBusy`'yi false yapar; `IsFacebookIdle` `IsReauthorizePending`'e de baktığı için "Facebook'a Bağlan" akış bitene dek pasif kalır (§8.2 TK17: akış beklerken üç düğme de pasiftir). `[İptal]` görünür kalır.
- İzin uyarısının metni yeni düğmeye yönlenir (metin render testinde). `ConnectFacebook` / `DisconnectFacebook` komutlarına, `FacebookConnectionStatus`'a ve Facebook kartına dokunulmaz.

- [ ] **Adım 1: Düşen testleri yaz**

```csharp
// SettingsViewModel_InstagramAccountsTests — G18 ekleri. OpenAsync'e TimeSpan? callbackTimeout = null eklenir
// (_service = _kit.CreateService(callbackTimeout: callbackTimeout)).
private const string WaitingText = "Tarayıcıdan onay bekleniyor... Açılan Facebook ekranında TÜM Sayfalarını ve Instagram hesaplarını işaretli bırak; yalnız yenisini işaretlersen eski hesabın bağlantısı düşer.";
private const string CanceledText = "Güncelleme iptal edildi; bağlantı değişmedi.";
// Komutu başlatır ve servis tarayıcıyı açana dek bekler (_kit.Browser.WaitUntilOpenedAsync); dönen görev komutun kendisidir.
private async Task<Task> BeginReauthorizeAsync(SettingsViewModel vm);
// BeginReauthorizeAsync + finish(_kit.Browser) + komutun bitmesini bekler (üst sınır TestPatience.Upper).
private async Task ReauthorizeAsync(SettingsViewModel vm, Action<FakeFacebookOAuthBrowser> finish);
// /me/accounts yanıtı: yalnız verilen Sayfalar ({"data":[{"id":…,"name":…,"instagram_business_account":{"id":…,"username":…}}]}).
private static HttpResponseMessage AccountsOf(params SeededPage[] pages);
private sealed class MarkingContext : SynchronizationContext       // Post'u çağıranın iş parçacığında, işaret koyarak koşturur
{
    [ThreadStatic] public static bool InPost;
    public override void Post(SendOrPostCallback d, object? state) { var was = InPost; InPost = true; try { d(state); } finally { InPost = was; } }
}

[Fact]
public async Task Izin_guncelle_beklerken_dugmeleri_kapatir_bitince_sonucu_yazar_karti_tazeler()
{
    await SeedAsync(extraPages: 1);
    var vm = await OpenAsync();
    var run = await BeginReauthorizeAsync(vm);
    vm.InstagramAccountStatus.Should().Be(WaitingText);
    (vm.IsReauthorizePending, vm.IsFacebookBusy, vm.CanManageInstagram).Should().Be((true, true, false));
    var reopened = CreateVm(_service);                                  // R18: Ayarlar kapatılıp açıldı; yeni ViewModel, aynı servis
    await reopened.ReauthorizeFacebookCommand.ExecuteAsync(null).WaitAsync(TestPatience.Upper);
    reopened.InstagramNotice.Should().Be("Güncelleme başarısız: Bekleyen bir Facebook onayı var. Tarayıcıdaki onayı tamamla ya da birkaç dakika sonra yeniden dene. Bağlantı değişmedi.");
    _kit.Browser.Complete(); await run.WaitAsync(TestPatience.Upper);
    vm.InstagramNotice.Should().Be("Facebook izinleri güncellendi: 2 Sayfa yenilendi.");
    vm.InstagramAccountStatus.Should().Be("2 Instagram hesabı bağlı");
    (vm.IsReauthorizePending, vm.IsFacebookBusy, vm.CanManageInstagram).Should().Be((false, false, true));
}

[Theory, InlineData("0"), InlineData("1"), InlineData("02")]           // eksik Sayfaların sıra numaraları: birincil (0) eksikse parantez yazılır; iki eksik çoğuldur (karar)
public async Task Eksik_Sayfa_kabul_edilince_soru_ve_sonuc_Sayfayi_adiyla_soyler(string missing)
{
    await SeedAsync(extraPages: 2);
    var gone = missing.Select(c => _pages[c - '0']).ToArray();
    var names = string.Join(", ", gone.Select(p => $"\"{p.Page.PageName}\""));          // "A" ya da "A", "B"
    _kit.Graph.Map(IsAccounts, _ => AccountsOf(_pages.Except(gone).ToArray()));
    var vm = await OpenAsync();
    string? question = null;
    _drawers.Decide = v => { question = v.ActiveReauthorizeQuestion; return true; };
    await ReauthorizeAsync(vm, b => b.Complete());
    question.Should().Be($"{names} bu girişte paylaşılmadı. Kaydedersen bu {(gone.Length == 1 ? "Sayfanın" : "Sayfaların")} yorumları kesilebilir"
        + (missing.Contains('0') ? " (Facebook yorumları dahil)" : "")
        + ". Vazgeçersen OrderDeck'te hiçbir şey değişmez; \"Facebook izinlerini güncelle\"ye yeniden basıp tüm Sayfalarını işaretle. Yine de kaydedilsin mi?");
    vm.InstagramNotice.Should().Be($"Facebook izinleri güncellendi: {3 - gone.Length} Sayfa yenilendi. {names} bu girişte yoktu; yorumları kesilebilir.");
    _drawers.Titles.Should().Equal("Facebook izinlerini güncelle");
    vm.ActiveReauthorizeQuestion.Should().BeNull();
}

[Fact]
public async Task Eksik_Sayfa_onayi_yakalanan_baglamda_sorulur_reddedilince_hicbir_sey_degismez()
{
    await SeedAsync(extraPages: 1);
    _kit.Graph.Map(IsAccounts, _ => AccountsOf(_pages[0]));
    var viaContext = false;
    _drawers.Decide = _ => { viaContext = MarkingContext.InPost; return false; };          // Vazgeç
    var previous = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(new MarkingContext());
    SettingsViewModel vm;
    try { vm = CreateVm(_service = _kit.CreateService()); }
    finally { SynchronizationContext.SetSynchronizationContext(previous); }
    await vm.RefreshInstagramStatusAsync();
    var before = await _service.GetUserAccessTokenAsync();
    await ReauthorizeAsync(vm, b => b.Complete());
    viaContext.Should().BeTrue("confirmMissing OAuth servisinin iş parçacığından gelir; çekmece arayüz bağlamında açılmalı");
    vm.InstagramNotice.Should().Be(CanceledText);
    (await _kit.CreateService().GetUserAccessTokenAsync()).Should().Be(before, "diske hiçbir şey yazılmadı");
    vm.InstagramPages.Should().HaveCount(2);
}

[Fact]
public async Task Iptal_zaman_asimi_ve_diger_hatalar_ayri_metinlere_duser()
{
    await SeedAsync();
    var vm = await OpenAsync(); var status = vm.InstagramAccountStatus;
    await ReauthorizeAsync(vm, _ => vm.CancelReauthorizeCommand.Execute(null));            // [İptal]
    vm.InstagramNotice.Should().Be(CanceledText);
    (vm.IsReauthorizePending, vm.IsFacebookBusy, vm.InstagramAccountStatus).Should().Be((false, false, status));
    await ReauthorizeAsync(vm, b => b.CompleteWithError("access_denied"));                 // Facebook ekranındaki "İptal": başarısız
    vm.InstagramNotice.Should().StartWith("Güncelleme başarısız: ").And.Contain("access_denied").And.EndWith(". Bağlantı değişmedi.");
    _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(200));                                 // Graph hatası: ham gövde yazılmaz
    await ReauthorizeAsync(vm, b => b.Complete());
    vm.InstagramNotice.Should().Be("Güncelleme başarısız: Facebook hatası (kod 200). Bağlantı değişmedi.");
    _kit.Graph.Map(IsAccounts, _ => throw new TaskCanceledException());                    // ağ zaman aşımı: kullanıcı iptal etmedi, "iptal edildi" DEĞİL
    await ReauthorizeAsync(vm, b => b.Complete());
    vm.InstagramNotice.Should().StartWith("Güncelleme başarısız: ").And.EndWith(". Bağlantı değişmedi.");
    var slow = await OpenAsync(callbackTimeout: TimeSpan.FromMilliseconds(50));            // zaman aşımı
    await slow.ReauthorizeFacebookCommand.ExecuteAsync(null).WaitAsync(TestPatience.Upper);
    slow.InstagramNotice.Should().Be("Facebook onayı 5 dakika içinde tamamlanmadı; bağlantı değişmedi. Yeniden dene.");
}

[Fact]                                                                  // Review Focus 1
public async Task Beklerken_baglanti_kaldirilirsa_guncelleme_basarisiz_olur_baglanti_dirilmez()
{
    await SeedAsync(extraPages: 1);
    var vm = await OpenAsync();
    var run = await BeginReauthorizeAsync(vm);
    await vm.DisconnectFacebookCommand.ExecuteAsync(null).WaitAsync(TestPatience.Upper);
    (vm.IsFacebookBusy, vm.IsFacebookIdle).Should().Be((false, false), "akış beklerken \"Facebook'a Bağlan\" pasif kalır (TK17)");
    _kit.Browser.Complete();
    await run.WaitAsync(TestPatience.Upper);
    vm.InstagramNotice.Should().StartWith("Güncelleme başarısız: ").And.EndWith(". Bağlantı değişmedi.");
    (await _kit.CreateService().IsConnectedAsync()).Should().BeFalse("kaldırılmış bağlantı dirilmez");
    (vm.InstagramPages.Count, vm.InstagramAccountStatus, vm.IsFacebookIdle).Should().Be((0, "Önce Facebook'a bağlan.", true), "bekleme metni artık durmuyor: eski metin geri konmaz; akış bitti, \"Facebook'a Bağlan\" açılır");
}

[Fact]
public async Task Basarili_guncellemeden_sonra_izin_uyarisi_kalkar_ekleme_listelemeye_gecer()
{
    await SeedAsync();
    var granted = false;
    _kit.Graph.Map(IsPermissions, _ => FakeGraph.Json(granted
        ? """{"data":[{"permission":"instagram_basic","status":"granted"},{"permission":"instagram_manage_comments","status":"granted"}]}"""
        : """{"data":[]}"""));
    var vm = await OpenAsync();
    vm.InstagramNeedsReconnect.Should().BeTrue();
    await ReauthorizeAsync(vm, b => { granted = true; b.Complete(); });
    vm.InstagramNeedsReconnect.Should().BeFalse();
    var listings = _kit.Graph.Requests.Count(IsAccounts);
    await vm.AddInstagramAccountCommand.ExecuteAsync(null);
    _kit.Graph.Requests.Count(IsAccounts).Should().Be(listings + 1, "0. adım kalktı: listeleme yapılır");
}

[Fact]                                                                  // G16 kuralı; tarayıcı dikişi G03'te geldi
public async Task Baglan_tamamlaninca_Instagram_karti_da_tazelenir()
{
    var candidate = AddCandidate();                                     // depo boş: Graph tek Sayfa listeler, Sayfa seçici açılmaz
    var vm = await OpenAsync();
    var run = vm.ConnectFacebookCommand.ExecuteAsync(null);
    await _kit.Browser.WaitUntilOpenedAsync();
    _kit.Browser.Complete();
    await run.WaitAsync(TestPatience.Upper);
    (vm.InstagramAccountStatus, vm.CanManageInstagram).Should().Be(($"Bağlı Instagram hesabı: @{candidate.IgUsername}", true));
}

[Fact]                                                                  // Review Focus 3
public void Kart_beklerken_Iptali_gosterir_uc_dugmeyi_kapatir_bitince_Iptal_gizlenir() => OnSta(() =>
{
    Button Named(DependencyObject root, string content) => Descendants(root).OfType<Button>().Single(b => Equals(b.Content, content));
    var waiting = CreateVm(oauth: null);
    waiting.IsFacebookConnected = waiting.IsFacebookBusy = waiting.IsReauthorizePending = waiting.InstagramNeedsReconnect = true;
    var page = RenderFacebookTab(waiting);
    var cancel = Descendants(page).OfType<Button>().Single(b => ReferenceEquals(b.Command, waiting.CancelReauthorizeCommand));
    cancel.Content.Should().Be("İptal");
    Shown(cancel).Should().BeTrue();
    new[] { "Facebook'a Bağlan", "Facebook izinlerini güncelle", "Instagram hesabı ekle" }
        .Select(name => Named(page, name).IsEnabled).Should().OnlyContain(enabled => !enabled);
    var reauthorize = Named(page, "Facebook izinlerini güncelle");
    reauthorize.Command.Should().BeSameAs(waiting.ReauthorizeFacebookCommand);
    reauthorize.ToolTip.Should().Be("Yayın sırasında yalnız kopan bir hesabı düzeltmek için bas: çalışan Instagram ve Facebook yorumlarında kısa bir kesinti olabilir.");
    ShownTexts(page).Select(t => t.Text).Should().Contain("Instagram yorumları için Facebook izinlerini güncellemen gerekiyor: aşağıdaki \"Facebook izinlerini güncelle\"ye bas.");

    var idle = CreateVm(oauth: null);
    idle.IsFacebookConnected = true;
    var after = RenderFacebookTab(idle);
    Shown(Descendants(after).OfType<Button>().Single(b => ReferenceEquals(b.Command, idle.CancelReauthorizeCommand))).Should().BeFalse();
    Named(after, "Facebook izinlerini güncelle").IsEnabled.Should().BeTrue();
});
```

- [ ] **Adım 2: Testlerin düştüğünü gör**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SettingsViewModel_InstagramAccountsTests"`
Beklenen: FAIL (derleme hatası: `ReauthorizeFacebookCommand`, `CancelReauthorizeCommand`, `IsReauthorizePending`, `ActiveReauthorizeQuestion` yok)

- [ ] **Adım 3: Metinleri, iki komutu ve onay geçişini uygula (`InstagramSettingsTexts.cs`, `SettingsViewModel.cs`)**

Kurucunun ilk satırında `SynchronizationContext.Current` özel bir alana yakalanır. Eklenenler: `[ObservableProperty] private bool _isReauthorizePending;` (`IsFacebookIdle`'a `&& !IsReauthorizePending` ve `partial void OnIsReauthorizePendingChanged(bool value) => OnPropertyChanged(nameof(IsFacebookIdle));`), `ActiveReauthorizeQuestion`, bekleyen çağrının `CancellationTokenSource?` alanı, `[RelayCommand] private async Task ReauthorizeFacebook()`, `[RelayCommand] private void CancelReauthorize()`. `ReauthorizeFacebook`: bayraklar ve bekleme metni → `ReauthorizeAsync(onay, belirteç).ConfigureAwait(true)` → sonuç metni → başarıda tazeleme; `catch` sırası Kurallar'daki gibi; `finally` bayrakları indirir, belirteç kaynağını bırakır ve bekleme metni hâlâ duruyorsa önceki metni geri koyar. Onay işlevi soruyu `ActiveReauthorizeQuestion`'a yazar, çekmeceyi yakalanan bağlam üzerinden açar ve false dönerken ret bayrağını kurar (iptal süzgeci onu okur).

- [ ] **Adım 4: Düğmeleri ve uyarı metnini uygula (`SettingsPage.xaml`)**

G17'nin düğme satırına: `Facebook izinlerini güncelle` düğmesi (`OD.Button.Secondary`, `Command` `ReauthorizeFacebookCommand`, `IsEnabled` `CanManageInstagram`, `ToolTip` = düğme ipucu metni, `Margin` `OD.Pad.X2`) ve `İptal` düğmesi (`OD.Button.Ghost`, `Command` `CancelReauthorizeCommand`, `Visibility` = `IsReauthorizePending` + `BoolToVisibleConverter`). İzin uyarısının (`:313-317`) `Text`'i yeni metin olur; öbür öznitelikleri aynen kalır.

- [ ] **Adım 5: Testlerin geçtiğini gör**

Koş: Adım 2'deki komut
Beklenen: PASS

- [ ] **Adım 6: Regresyon ve derleme**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo -v q && DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SettingsViewModel_InstagramAccountsTests|FullyQualifiedName~SettingsViewModel_LicenseShippingTests|FullyQualifiedName~SettingsViewModel_PaymentTests|FullyQualifiedName~UnstyledControlGuardTests"`
Beklenen: PASS, 0 uyarı

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.App/ViewModels/SettingsViewModel.cs OrderDeck.App/Views/Pages/SettingsPage.xaml \
  OrderDeck.App/Services/Instagram/InstagramSettingsTexts.cs OrderDeck.Tests/ViewModels/SettingsViewModelInstagramAccountsTests.cs
git commit -F - <<'MSG'
feat(instagram): Ayarlar'a "Facebook izinlerini güncelle" ve iptal ekle

Yeni Sayfa saklı girişle görünmüyorsa tarayıcı onayı bağlantıyı kaldırmadan
yenilenir: ekler korunur, birincil değişmez, eksik Sayfa için kaydetmeden önce
onay sorulur. Bekleme iptal edilebilir; izin uyarısı bu düğmeye yönlenir.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

### Görev G19: Kapanış: pilot el listesi, sürüm notu, teslim özeti, tam koşu, DRAFT PR

**Spec:** §9.5 (tamamı); §10 (sürüm, eski sürüme dönüş, PR planı, adım 11); §5 madde 7; §9.1 regresyon listesi; §8.3.2 "Bilerek değişen davranışlar" (TK19); §8.7 (TK12); §8.8 (günlük süzgeci); §8.9; TK22; R7, R13.
**Bağımlı:** G01–G18.

**Files:**
- Create: `docs/manual-smoke/2026-11-17-instagram-coklu-hesap-pilot.md`

**Interfaces:**
- Consumes: ürün kodu ve test değişmez; belge şu günlük satırlarını anar: G09 kapı özeti `[InstagramMergeGate] summary: inversions={n} maxGapSeconds={sn} lanes: {hesap} rounds={n} p50Ms={ms} p95Ms={ms}; …` (sayılar pencere başına: her satır son bir dakikayı, son satır oturum sonuna kalan kısmı anlatır); G10 durum geçişi `[InstagramChatHostedService] account {hesap} (page {PageId}): {eski} -> {yeni}` (+ `, retry at SS:DD:ss`) ve kota satırı `[InstagramChatHostedService] usage {hesap} (page {PageId}): business {id} call_count {a}% total_cputime {b}% total_time {c}% regain {m} min` (hesap başına dakikada en çok bir; başlık yoksa `X-Business-Use-Case-Usage header absent`, okunamıyorsa `… header unreadable`; geçici hatalı turda satır yazılmaz); G02 / G04 `Facebook OAuth …` satırları.
- Produces: yok (belge). §10 adım 11 (pilot) otomatik değildir; bu görevin teslimatı el listesidir.

**Kurallar:**
- Belge Türkçedir ve operatörün okuyacağı dille yazılır; adımlar onay kutuludur (`- [ ]`). Depo PUBLIC: lisans, müşteri, Sayfa ya da hesap kimliği, e-posta ve token YAZILMAZ; yer tutucu `<…>` kullanılır.
- Prod sorgusunu Burak çalıştırır; uygulayıcı prod'a, SSH'a ve sır dosyalarına dokunmaz.
- Bu görev ürün kodu ve test değiştirmez. Tam koşuda bir test düşerse düzeltme o davranışı yazan görevin dosyasında, ayrı commit'le yapılır.
- Push ve PR yalnız Burak açıkça isterse; PR DRAFT açılır ve DRAFT kalır. Merge ve sürüm Burak'tadır, yayın penceresinde (Paz/Pzt/Çar/Per 20:00–01:00) yapılmaz. Sunucu deploy'u yok; yeni WPF Velopack sürümü gerekir.
- PR-B (hesaplar arası sıra) bu PR'da yoktur: belge "hesaplar arası sıra varış sırasıdır" der; 6. bölümün rakamı PR-B'nin girdisidir.

- [ ] **Adım 1: Pilot belgesini yaz (`docs/manual-smoke/2026-11-17-instagram-coklu-hesap-pilot.md`)**

Bölümler bu sırayla ve bu başlıklarla; içerik spec'in anılan maddesinden, madde atlamadan:

| Başlık | İçerik |
|---|---|
| `# Instagram çoklu hesap: pilot el listesi (PR-A)` | Tek paragraf: kapsam, sürüm, "sıra varış sırasıdır" |
| `## 0. Önce: sunucu bayrağı` | §9.5 "Önce" + §5 madde 7; aşağıdaki sorgu. Satır dönmezse ya da değer 0 ise devam. Değer 1 ise "Facebook izinlerini güncelle"ye BASILMAZ; kapatma sorgusu ayrıca istenir |
| `## 1. Tek hesap regresyon yayını` | §9.5 madde 1; ek satır: kenar çubuğunda Instagram noktası yeşil ve sayısız |
| `## 2. Ekleme` | §9.5 madde 2 (iki bilinmeyenin notu, onay sekmesini kapatıp `[İptal]` denemesi dahil) |
| `## 3. İkinci hesapta kısa deneme yayını` | §9.5 madde 3 (TK22; yüzde bir saat dolmadan %100'e varacaksa gerçek mezattan önce dur) |
| `## 4. İki hesapla gerçek yayın` | §9.5 madde 4'ün beş alt maddesi, her biri ayrı onay kutusu; "ters sıra sayısı" = yayın boyunca kapı özeti satırlarındaki `inversions=` değerlerinin TOPLAMI, en büyük fark = `maxGapSeconds=` değerlerinin en büyüğü |
| `## 5. Kota ölçümü` | §9.5 madde 5'in dört okuma kuralı; "yüzdedir, çağrı sayısı değil" cümlesi kalın; kota satırının biçimi ve `header absent` / `header unreadable` ayrımı (Interfaces); "başlık her yanıtta geliyor mu" sorusu `header absent` satırlarından okunur |
| `## 6. Yanıt süresi` | §9.5 madde 6; kapı özeti satırlarından hesap başına `p50Ms` / `p95Ms` not edilir (her satır bir dakikalık penceredir; en yüksek `p95Ms` de ayrıca yazılır) |
| `## 7. Bilgisayar sayısı` | §9.5 madde 7 |
| `## Günlük süzgeci` | Aşağıdaki komut; hangi satırın hangi önekle geldiği (§8.8 son madde) |
| `## Sürüm notu` | Aşağıdaki metin, aynen |
| `## Teslim özeti` | §8.3.2'nin yedi maddesi ve dört yan farkı (sırayla, birer cümle; "itiraz edilen madde 2 ve üzeri Sayfa koşuluna bağlanır, 5. madde hariç" notuyla); TK12 (aynı izleyici + aynı metin 2 sn içinde iki hesapta tek satır; iki satır istenirse ayrı küçük iş); R7 (yayın sırasında izin güncelleme Instagram ve Facebook yorumlarında kısa kesinti yapabilir); R13'ün "en ağır sonuç" cümlesi |

Sorgu (SQL Server; tablo adları `LicenseDbContext.cs:48`, `:57`; satır yoksa bayrak kapalıdır, `InstagramAccountService.cs:58-60`):

```sql
SELECT c.Email, f.InstagramDmBotEnabled
FROM IntakeFormConfigs AS f
JOIN Customers AS c ON c.Id = f.CustomerId
WHERE c.Email = N'<hesabın e-postası>';
```

Günlük süzgeci (`~/Documents/OrderDeck/Logs/`, günlük dosya):

```bash
grep -iE "InstagramChatHostedService|InstagramMergeGate|Facebook OAuth" ~/Documents/OrderDeck/Logs/log-<YYYYMMDD>.txt
```

Sürüm notu:

```text
Instagram: aynı anda birden çok Instagram hesabının canlı yorumları okunur (en çok 4). İkinci hesap Ayarlar > Facebook > "Instagram hesabı ekle" ile eklenir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar, kopan hesap sohbet panelinin başında görünür.
Eski sürüme dönülürse: uygulama ilk bağlanan (birincil) Sayfayla bugünkü gibi çalışır; eklenen hesaplar o sürümde okunmaz ve bunun göstergesi yoktur. Eski sürümde "Bağlantıyı Kaldır" ya da "Facebook'a Bağlan" yapılmadıkça yeniden yükseltmede eklenen hesaplar geri gelir; yapıldıysa hesap "Instagram hesabı ekle" ile yeniden eklenir.
```

- [ ] **Adım 2: Regresyon listesinin ve dokunulmayanların değişmediğini doğrula**

Koş:
```bash
git diff --numstat 7960eba3 -- OrderDeck.Tests/Chat/Facebook/EncryptedFacebookTokenStoreTests.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramCommentWatermarkTests.cs OrderDeck.Tests/Chat/Instagram/InstagramLiveMediaParserTests.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramAccountResolverTests.cs OrderDeck.Tests/Chat/Instagram/InstagramPermissionProbeTests.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramChatHostedServiceTests.cs OrderDeck.Tests/Chat/Facebook/FacebookChatHostedServiceTests.cs \
  OrderDeck.Tests/Chat/Facebook/FacebookModerationServiceTests.cs OrderDeck.Tests/Chat/Facebook/FacebookSeenCommentsTests.cs \
  OrderDeck.Tests/Chat/SpamFilterTests.cs OrderDeck.Tests/App/MainShellConnectionsTests.cs \
  OrderDeck.Tests/App/ShellBannersLayoutTests.cs OrderDeck.Tests/ViewModels/SettingsViewModelTests.cs \
  OrderDeck.Chat/Facebook/FacebookModerationService.cs OrderDeck.Chat/Ingestors/Facebook \
  OrderDeck.App/Views/Drawers/FacebookPagePickerDrawer.xaml OrderDeck.App/Views/Drawers/FacebookPagePickerDrawer.xaml.cs \
  OrderDeck.Chat/Ingestors/Instagram/InstagramCommentWatermark.cs OrderDeck.Chat/Ingestors/Instagram/InstagramLiveMediaParser.cs \
  OrderDeck.Chat/Ingestors/Instagram/InstagramPermissionProbe.cs OrderDeck.Core/Chat/ChatBus.cs OrderDeck.Core/Chat/SpamFilter.cs \
  OrderDeck.Core/Chat/ViewerCountTracker.cs OrderDeck.Core/Settings/AppSettings.cs OrderDeck.App/Views/Shell/ShellBanners.xaml \
  OrderDeck.App/Views/Shell/ShellTopBar.xaml OrderDeck.Overlay OrderDeck.LicenseServer Extension
git diff --numstat 7960eba3 -- OrderDeck.Tests/Chat/Instagram/InstagramLiveCommentsPollerTests.cs \
  OrderDeck.Tests/Chat/Instagram/InstagramGraphErrorTests.cs OrderDeck.Tests/App/MainShellViewCompositionTests.cs
```
Beklenen: ilk komutun çıktısı BOŞ (§9.1 "değiştirilmeden geçmesi gerekenler" ve §8.9); ikinci komutta üç satırın ikinci sütunu (silinen satır) 0 (yalnız ek almış sınıflar).

- [ ] **Adım 3: Tam koşu (CI eşdeğeri)**

Koş: `DOTNET_CLI_UI_LANGUAGE=en dotnet build OrderDeck.sln -c Release --nologo -v q && DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj -c Release --no-build --nologo -v q -- xUnit.ParallelizeTestCollections=false`
Beklenen: derleme `0 Warning(s)`, `0 Error(s)`; test özeti `Failed: 0`

- [ ] **Adım 4: Commit**

```bash
git add docs/manual-smoke/2026-11-17-instagram-coklu-hesap-pilot.md
git commit -F - <<'MSG'
docs(instagram): çoklu hesap pilot el listesini, sürüm notunu ve teslim özetini ekle

Pilot otomatik değildir: sunucu bayrağı teyidi, tek hesap regresyonu, ekleme,
deneme yayını, iki hesaplı yayın ve kota ölçümü elle yürütülür. Tek hesaplı
kurulumda bilerek değişen davranışlar teslim özetinde tek listede durur.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

- [ ] **Adım 5: (Yalnız Burak isterse) dalı gönder ve DRAFT PR aç**

PR gövdesi depo dışındaki geçici bir dosyaya yazılır: `## Özet` (iki hesabın sohbeti, etiket, sağlık; sıra varış sırası, PR-B ayrı), `## Sürüm notu` (belgeden aynen), `## Tek hesaplı kurulumda değişenler` (belgenin teslim özeti), `## Pilot` (belgenin yolu), `## Test` (Adım 3'ün sonucu); son satır `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

```bash
git push -u origin feat/instagram-coklu-hesap
gh pr create --draft --base master --head feat/instagram-coklu-hesap \
  --title "feat(instagram): iki Instagram hesabının canlı sohbetini aynı anda oku (PR-A)" --body-file <gövde dosyası>
```
Beklenen: PR DRAFT durumunda açılır. Açıldıktan sonra dala commit eklenirse Burak'a ayrıca haber verilir; merge ve sürüm Burak'tadır.

---

## Kapsam eşlemesi

Spec bölümü → sahibi görev (yalnız PR-A). Madde düzeyindeki eşleme görevlerin **Spec** satırlarındadır.

| Spec | Konu | Görev |
|---|---|---|
| §8.1 | `FacebookLinkedPage`, `ExtraPages`, iki `ToString`; sürüm alanı ve göç yok (TK1); atomik yazım (TK2) | G01 |
| §8.1 | eksik alanlı ek girdi yalnız kendisi atlanır | G02 |
| §8.2 | değişmeyen üyeler, birincil kuralı (TK3), en çok 4 Sayfa (TK13), bağlı Sayfalar, tarayıcısız listeleme / ekleme (TK5) / kaldırma, kilit, hata metni düzeltmesi | G02 |
| §8.2 | `AcquireUserTokenAsync`; tarayıcı beklemesi (TK17): 5 dk zaman aşımı, iptal, tek akış; `access_denied` eşlemesi | G03 |
| §8.2 | yeniden onay (TK4) adım 1–6 | G04 |
| §8.3.1 | poller: gözlemci, kota başlığı, filigran parametresi | G06 |
| §8.3.2 | kurucular; üst döngü 1–3 ve 5; hesap döngüsü 1–5; tek hesapta korunanlar (TK6, TK15); yalıtım | G10 |
| §8.3.2 | hesap döngüsü 6: token'a bakan beklemeler; token değişince önbellek boşaltma (TK16) | G11 |
| §8.3.2, §8.3.3 | üst döngü 4: 2 sn izleme; ekleme, çıkarma, tekten çoka devir, boşalma, token değişimi (TK14) | G12 |
| §8.3.2 | bilerek değişen davranışlar 1–7 (TK19) | 1: G12 · 2: G11 · 3–4: G15 · 5: G17, G18 · 6: G18 · 7: G03 |
| §8.3.2 | küçük yan farklar a–d (TK19) | a, c: G16 · b: G10 · d: G11 |
| §8.3.4 | `ResolveDetailedAsync`, `TryGetUsage` | G05 |
| §8.3.5 | `InstagramMergeGate`: etiket + ölçüm, düz geçiş (TK7'nin PR-A yarısı) | G09 |
| §8.3.7 | oturum başlat / bitir | G10 |
| §8.4 | `ChatMessage.SourceAccount` | G09 (dolduran G10) |
| §8.5 | sağlık izleyicisi ve geçişler | G07 (çağıran G10, G12) |
| §8.5 | görünüm kuralları (TK9, TK10, TK20) | G08 |
| §8.6.1 | hesap listesi, satır durumu, "Kaldır", tazelemeler | G16 |
| §8.6.1 | "Instagram hesabı ekle" (adım 0–3, altı sonuç metni), ekleme çekmecesi | G17 |
| §8.6.1 | "Facebook izinlerini güncelle" (madde 1–6), `[İptal]`, izin uyarısı metni | G18 |
| §8.6.2 | sohbet satırı etiketi ve ipucu (TK11) | G14 |
| §8.6.3 | şerit: görünürlük kuralı, durum / metin / ipucu tablosu | G08 |
| §8.6.3 | şerit: yer, bağlar, kabuk üyeleri, DI kaydı | G15 |
| §8.6.4 | üst nokta ve kenar çubuğu noktası | G08 (metin), G15 (bağlama) |
| §8.7 | tekrar süzgecine dokunulmaz (TK12) | Global Constraints; teslim özeti G19 |
| §8.8 | durum geçişi ve kota satırları | G10 |
| §8.8 | dakikalık kapı özeti | G09 |
| §8.8 | ekleme / kaldırma / izin güncelleme sonuç satırları | G02, G04 |
| §8.8 | kişisel veri yok, token yok, önekler (denetim testi) | G13 |
| §8.9 | bilerek dokunulmayanlar | Global Constraints; doğrulama G19 |
| §9.1 | `FacebookTokenBundleCompatTests` | G01 |
| §9.1 | `FacebookOAuthServiceMultiPageTests` | G02; tarayıcı beklemesi G03; `ReauthorizeAsync` G04 |
| §9.1 | `InstagramLiveCommentsPollerTests` (ek; "PR-B" maddesi hariç) | G06 |
| §9.1 | `InstagramAccountResolverDetailedTests`, `InstagramGraphErrorTests` (ek) | G05 |
| §9.1 | `InstagramHealthMonitorTests` | G07 |
| §9.1 | değiştirilmeden geçmesi gereken sınıflar | her görevin regresyon adımı; toplu doğrulama G19 |
| §9.2 | `InstagramMergeGateTests` "PR-A" maddesi | G09 |
| §9.3 | `InstagramMultiAccountHostedServiceTests` | G10, G11, G12 |
| §9.4 | `InstagramHealthPresenterTests` | G08 |
| §9.4 | `MainShellInstagramHealthTests`; `MainShellViewCompositionTests` (ek, `ChatPanel`) | G15 |
| §9.4 | `ChatMessageViewModelTests` | G14 |
| §9.4 | `SettingsViewModel_InstagramAccountsTests` | G16, G17, G18 |
| §9.4 | `MainShellViewCompositionTests` (ek, `InstagramAccountAddDrawer`) | G17 |
| §9.5 | pilot el listesi (TK22) | G19 |
| §10 adım 1 | geriye uyum testleri + demet + atomik yazım | G01 |
| §10 adım 2 | OAuth servisi eklemeleri | G02, G03, G04 |
| §10 adım 3–5 | `ResolveDetailedAsync`, `TryGetUsage`; poller; `InstagramHealthMonitor` | G05; G06; G07 |
| §10 adım 6–7 | `SourceAccount`, `InstagramMergeGate` | G09 |
| §10 adım 8 | hosted service | G10, G11, G12 |
| §10 adım 9 | satır etiketi; şerit, üst nokta, kenar çubuğu; `AppHost` | G08, G14, G15 |
| §10 adım 10–11 | Ayarlar; pilot, sürüm ve eski sürüme dönüş notu | G16, G17, G18; G19 |
| §11.3 | inşaatçıya bırakılanlar | "Plan kararları"; G02, G03, G09, G10, G11, G15 |

## Plan dışı (PR-B ve sonrası)

- **PR-B (hesaplar arası sıra):** `IInstagramPollPacer` ve poller'ın `pacer` parametresi; kapıda K1–K8 (ortak tık, 400 ms bariyer, tutma); `InstagramCommentMerger`; `HoldBudget` = 400 ms; kapı özetinin bariyer aşımı / geç kalan tur / yavaş kulvar sayaçları; TK8, TK18, TK21; spec §9.1'in "PR-B" maddesi, §9.2'nin K1–K8 / TK8 testleri ve `InstagramCommentMergerTests`. PR-B'nin planı bu PR'ın pilot rakamları (ters sıra sayısı, yanıt süresi p50 / p95) geldikten sonra ayrıca yazılır; K1–K8 önce bağımsız bir gözden geçirmeden geçer (spec §10, §13).
- **Yoklama aralığını yükseltme:** deneme yayınında ikinci hesabın kota yüzdesi bir saat dolmadan %100'e varacak gibiyse gerekir; spec bunu "ayrı, küçük bir değişiklik; bu belgede tasarlanmadı" diye bırakır (§9.5 madde 3).
- **Tekrar süzgecine hesap anahtarı** (aynı izleyicinin iki hesaba yazdığı aynı satırın ikisini de göstermek; TK12) ve **hesap başına kısa takma ad** (TK11): istenirse ayrı, küçük işler.
- **"Bu Sayfada Instagram beklenmiyor" işareti** (birincil Sayfası bilerek Instagram'sız kurulumda her oturumda sarı satır; R16) ve **kullanıcı token'ı ölmeden önce uyarı** (`ExpiresAt` hiçbir yerde okunmuyor; R10).
- **İkinci Facebook girişi** (ikinci Sayfayı başka bir Facebook kullanıcısı yönetirse; R17) ve **birden çok bilgisayar önlemi** (R19): sahibin 17.11.2026 cevabı tek bilgisayar olduğu için plana girmedi.
- **Spec §4 "Kapsam dışı":** Facebook canlısını iki Sayfadan açmak; raporda, basılı etikette ya da siparişte hesap kırılımı; Sayfasız yol (Instagram Login); webhook; "!kayıt → DM"; Instagram izleyici sayısı.
- **Değişmeyenler:** sunucu (`OrderDeck.LicenseServer`), yerel SQLite şeması, overlay, uzantı, panel ve Shopper (spec §10).

## Uygulama sapmaları

Görev metinleri yukarıda yazıldığı gibi kaldı; bu bölüm uygulamanın plandan nerede ayrıldığını söyler. Spec bu sapmalarla eşitlendi (`docs(instagram): tasarım belgesini uygulanan kodla eşitle`); ayrıntı ilgili commit gövdelerindedir.

### a. Plan dışı sağlamlaştırma görevleri

Denetçilerin küçük bulguları kol başına tek bir "b" görevinde toplandı; son iki görev (G18c, G18d) bütün dal incelemesinin bulgularını giderdi. Her biri taze bir uygulayıcıyla yapıldı ve spec / kalite (gerekince eşzamanlılık) denetiminden geçti.

| Görev | Ne yaptı |
|---|---|
| G01b | Token deposu geçici dosyayı taşımadan önce diske indirir (güç kesintisinde hedef ad boş dosyayı göstermesin; emsal `SettingsStore.WriteThrough`); `ClearAsync` geçici dosyanın artığını da siler ("Bağlantıyı Kaldır"dan sonra diskte token kalmasın). Spec §8.1 ve TK2 buna göre güncellendi. |
| G03b | `IsConnectedAsync`'in tembel yüklemesi demeti yalnız alan boşken yayımlar (`Interlocked.CompareExchange`), kilit altında yazılmış taze demeti ezmez; `GetJsonAsync` hata iletisinde isteğin kendi token'ını `***` ile maskeler; Sayfa listesi izin yönlendirmesine dönerken Graph kodunu günlüğe yazar. Sabitleme testleri eklendi; spec §8.2 / §8.8 güncellendi. |
| G05b | Yeniden onayın kayıt adımı, kullanıcı token'ı arada değiştiyse (bağlantı kaldırılıp yeniden kurulmuş) hiçbir şey yazmaz; token'ı türetilemeyen Sayfanın Warning satırı HTTP durumunu da yazar (JSON olmayan 5xx / 429 gövdesinde Graph kodu okunamaz). İki test sıkılaştırıldı, JSON olmayan gövde testi eklendi. |
| G06b | Poller'ın kota başlığını `NoBroadcast`, `Fatal` ve `Transient` sonuçlarında ve çok değerli başlıkta da taşıdığı testle sabitlendi; üretim kodu değişmedi. |
| G08b | Sağlık ipucunda eksi saniye sıfıra kırpılır (`WholeSeconds`; sistem saati geri alınınca "Son yanıt -3 sn önce" yazıyordu); `StopAll` özeti düzeltildi; poller'ın üç seyrek dalında (izin hatası, yayın bitti, `comments` alanı olmayan media) kota başlığı, izleyicide alan korunumu ve sunucunun birkaç kuralı testle sabitlendi. |
| G09b | Sağlık izleyicisi ve görünüm kuralları testlerindeki iki boşluk kapandı (Ok turunda kullanıcı adının korunumu; duran girdide geçmiş yeniden deneme saatinin "yanıt yok" üretmemesi); birleştirme kapısının testsiz kalan özet ve kilit kuralları sabitlendi. Yalnız test. |
| G11b | Kalıcı hata beklemesi token değişince atlanır ya da kesilirse girdinin yeniden deneme saati "şimdi"ye çekilir (yoksa satır 5 dakikaya kadar bayat saat gösterir, "yanıt yok" gecikirdi); `Invalidate()` özeti düzeltildi. Beş sabitleme testi: oturum sonu sırası, sahipliğin bırakılması, token kaydı, kota satırının kültürü, süresi dolan beklemede saate dokunulmaması. |
| G16b | Yeniden onayın kullanıcı token'ı çapası akışın BAŞINDA alınır, 3. ve 5. adımda karşılaştırılır (Sayfalar listelenirken yeniden kurulan bağlantı da ezilmez); "aynı anda iki izin güncellemesinde ilk kaydeden kazanır" belgelendi ve testlendi. Ayarlar kartı: tazeleme sonucu yazmadan önce bağlı Sayfaları yeniden okur, liste değiştiyse sonucu atıp yineler; `SettingsViewModel` kurucusuna isteğe bağlı son parametre `ILogger<SettingsViewModel>? log` eklendi, düşen "Kaldır" sebebi Warning olarak yazar. Üç yorum düzeltildi. |
| G18b | "Instagram hesabı ekle" 0. adımdan önce süren tazelemeyi (ilk izin sondası) bekler; kota kodu kümesi tek yerde (`InstagramGraphError.IsQuotaError`), `Classify` ve ViewModel onu çağırır; oturum içi izleme testlerinin yüke duyarlı iki penceresi kapatıldı, test özetleri tanığın gösterdiğine indirildi; yorum düzeltmeleri. |
| G18c | Motor: atlanan Sayfanın beklemesi sahiplik kaydını izler, sahip bırakınca hesabı hemen devralır; tekten çoka devir turunda yeniden çözümleme atlanır (ağ isteği yok); `InstagramAccountResolver` çözümleme hatasında Warning yazar, ham gövde hiçbir düzeyde yazılmaz, poller'ın geçici hata Debug satırı da gövde yerine durum ve kod yazar; `ExtraPagesOf` adı boş ek girdiyi atlar, ek girdinin disk biçimi testle donduruldu; devir testleri sağlamlaştırıldı (yorumun kulvardan geldiği doğrulanır, eski poller "yayın yok" ile susturulur). |
| G18d | Arayüz: Ayarlar durum satırında gerçek metin ile bekleme metni ayrıldı; `IsInstagramAddBusy` eklendi (ekleme sürerken iki düğme pasif); izin güncellemesinin başarı dalı tazelemeden önce `[İptal]`i ve bekleme metnini kaldırır; "Kaldır"dan sonra durum `Kontrol ediliyor...`; ekleme disk hatası `Hesap eklenemedi, yeniden dene.`, JSON olmayan yanıt geçici hata; sağlık şeridinde yalnız değişen satır yenilenir; test yorumlarındaki plan atıfları kuralın kendisiyle değiştirildi. |

### b. Orkestratörün eklediği bağlayıcı kurallar

Görev metinlerinde yoktu; uygulayıcılara görev dışı not olarak verildi.

1. **Ayarlar ViewModel'inin günlük kuralı (G17, G18):** bir istisnayı, sebebini tam göstermeyen bir operatör metnine çeviren `catch` dalı istisnayı Warning düzeyinde günlüğe yazar. Satırlar öneksizdir (kategori `SettingsViewModel`): `Instagram account listing failed`, `Instagram account add failed for Page {PageId}`, `Instagram account removal failed for Page {PageId}`, `Facebook permission update failed`. İptal, vazgeçme, onay zaman aşımı, 4 hesap sınırı ve `NeedsReauthorize` yazılmaz. Neden: pilotta "Sayfalar okunamadı" / "yetki alınamadı" / "Güncelleme başarısız" çıkarsa sebep günlükten okunabilsin.
2. **Ayarlar tazeleme kuralı (G16b'den sonra):** süren bir tazeleme, o sırada değişen bağlantının eski hâlini karta yazamaz. G17 ve G18 buna dayanır ve her başarılı değişiklikten sonra tazeleme ister.
3. **Görev sırası:** görevler iki paralel kolda (iki worktree) yürüdü; her görev yalnız "Bağımlı" satırındaki görevler dalda varken başladı (aşağıda e).

### c. Öne çıkan uygulayıcı sapmaları

Denetçiler hepsini haklı buldu.

- **G10:** `InstagramChatTimings` `[SupportedOSPlatform("windows")]` taşır; üst döngünün oturum gövdesi `RunSessionAsync` yardımcısındadır; hesap adı kuralı `IsNullOrWhiteSpace`; sahiplik döngü çıkarken değere göre toplu bırakılır.
- **G11:** önbellek boşaltma özel bir yöntemdedir (`ForgetAccountIfTokenChanged`); bekleme yardımcısı bool döner (atlandı / kesildi), G11b bunu kalıcı hata dalında kullanır.
- **G12:** kendi kendine bitmiş hesap döngüsü izleme turunda "çalışan" sayılmaz, Sayfası listedeyse aynı turda yeniden başlatılır; kapıya devir, kalıcı hatayla biten ya da başlatılamayan poller'da uygulanmaz (beklemesi geçerli kalır); devredilen filigran yalnız devri izleyen ilk kurulumda geçerlidir; oturum sonu toplaması döngüleri açıkça iptal edip bekler.
- **G14:** mesaj `TextBlock`'unun `Grid.Column` özniteliği onu saran `DockPanel`'e taşındı; ek testler (sağa yaslanma, 112 DIP belirteci, ipucu ofseti, kültürden bağımsız saat).
- **G16:** satır şablonunda `WrapPanel`, saydam zemin, düğmede dolgu / yazı boyu; tazelemenin genel hata metni (`Durum okunamadı: …`) ViewModel'de literal kaldı. Durum satırının ilk değeri de literaldi; G18d onu `InstagramSettingsTexts.Checking` sabitine bağladı.
- **G17:** ek günlük testleri özellik commit'inde; test yardımcıları genişledi.
- **G18:** izin uyarısı metni `InstagramSettingsTexts.PermissionsMissingWarning`'e taşındı (`x:Static`); test yardımcısı `BeginReauthorizeAsync`, komut tarayıcı açılmadan biterse hemen düşer.
- Test sayıları görevlerdeki "beklenen sayı" satırlarından fazladır (ek sabitleme testleri).

### d. Geçersiz kalan ya da daralan plan kararları

- **Durum satırında "önceki metin geri konur"** ("Plan kararları": bekleme metni ve `Sayfalar okunuyor...` için "işlem bitince durum satırı hâlâ o metinse önceki metin geri konur"): GEÇERSİZ. Kalıp üç açık bırakıyordu (bekleme sırasında biten tazeleme tarayıcı talimatını siliyordu; ekleme sürerken başlayan izin güncellemesi eklemenin geçici metnini "önceki metin" diye geri yazabiliyordu; başarılı güncellemeden sonraki tazeleme boyunca bekleme metni ve etkisiz `[İptal]` ekranda kalıyordu). Yerine (G18d): gerçek durum metni ayrı tutulur, tazeleme ve "Kaldır" onu yazar; satıra ancak bekleme metni görünmüyorsa yansır; bekleme metni kalkınca satır o anki gerçek metne döner. Ek olarak `IsInstagramAddBusy` ile iki akış aynı anda yürümez.
- **Eklemede "5xx, kota ve ağ dışındaki her hata → yetki alınamadı"** ("Plan kararları"): DARALDI. Token türetildikten sonra demet yazımı düşerse (`IOException`, `UnauthorizedAccessException`, `CryptographicException`) metin `Hesap eklenemedi, yeniden dene.` olur; JSON olmayan yanıt (`JsonException`, ör. ağ geçidinin HTML sayfası) ve zaman aşımı geçici hata sayılır (`Sayfalar okunamadı…`). "Yetki alınamadı" geri kalan hatalarda kalır (token türetilemedi; token / izin hataları da, aşağıda "Bilinen sınırlar"). Spec §8.6.1 o metni zaten yalnız token türetilemediğinde istiyordu.
- **Global Constraints AH4'teki "yedi madde + dört yan fark":** yan farklar beşe çıktı; spec §8.3.2'ye (e) eklendi ("Facebook'a Bağlan"ın üç hata metni: bekleyen onay mesajı, maskelenmiş Graph gövdesi, adressiz boş gövde iletisi).

### e. Yürütme sırası ve son inceleme

Görevler plandaki G01 → G19 sırasıyla değil, iki paralel kolda yürüdü; her görev yalnız "Bağımlı" satırındaki görevler dalda varken başladı (G15 ikinci koldaydı). Plan dosyası uygulama boyunca değişmedi. Son olarak dalın tamamı yedi mercekle incelendi: eşzamanlılık, OAuth / sır hijyeni, spec uyumu (motor), spec uyumu (arayüz), tek hesap regresyonu, test kalitesi, hata yutma. Kritik ya da önemli bulgu çıkmadı; küçük bulgular G18c (motor) ve G18d (arayüz) görevlerinde giderildi.

## Bilinen sınırlar

Kodda doğrulandı; düzelenler yazılmadı. PR gövdesine de girer.

1. Token değişince Sayfa BAŞKA bir Instagram hesabına çözülürse eski hesabın sahiplik kaydı ve kulvar etiketi oturum sonuna dek kalır (kulvar döngüde bir kez açılır; sahiplik döngü bitince bırakılır).
2. Kota satırı: çok değerli `X-Business-Use-Case-Usage` başlığında satır `header unreadable` yazar (değerler virgülle birleşir, JSON olarak okunamaz). Başlıkta `estimated_time_to_regain_access` alanı yoksa satır `regain 0 min` yazar: "alan yok" ile "0 dakika" günlükte ayırt edilemez (Meta alanı olağan durumda gönderir).
3. Kota satırının "hesap başına dakikada en çok bir" sınırlayıcısı döngü başınadır: döngü yeniden kurulunca (yeni oturum, Sayfanın yeniden eklenmesi) sıfırlanır.
4. Çözümleme token kaydı (`resolvedTokens`) Sayfanın son token'ını servis ömrünce süreç belleğinde tutar (diskteki demette olandan fazlası değil).
5. Dar panelde etiketli sohbet satırında etiket en çok 112 DIP yer kaplar, mesaja kalan genişlik buna göre daralır; uzun yorum daha çok satıra kırılır (spec §8.6.2 / TK11 tasarımının sonucu; pilotta gözle bakılır).
6. Ayarlar kartının sonuç şeridi kargo şeridinin eşidir: başarı metni de sarı zeminde çıkar (pilotta gözle bakılır; istenirse ayrı küçük iş).
7. Ayarlar tazelemesi 1 + N sıralı Graph isteğidir (N en çok 4, istek başına 15 sn zaman aşımı); Graph'a ulaşılamazsa bir tazeleme ~75 saniye sürebilir. Tazelemeyi bekleyen komutlar ("Kaldır", "Facebook'a Bağlan", "Bağlantıyı Kaldır", izin güncellemesi, ekleme) o sürede meşgul kalır.
8. İzleme turunda beklenmeyen bir istisna oturumun bütün hesap döngülerini toplar; üst döngü 30 saniye sonra yeniden kurar (sağlam hesaplar da 30 sn + ilk çekimin yutulmasını yaşar). Tekten çoka devir, hız sınırı ya da "yayın yok" beklemesindeki poller'ı da durdurur: hesap devirde bir istek daha atar.
9. Kozmetik: oturum iptali döngü başı denetimi ile poller kurulumu arasına düşerse poller başlangıç satırlarını yazar, istek atmaz.
10. Ekleme akışında token / izin hatası (190 / 200) da "yetki alınamadı" metnine düşer; sebep günlükteki `Instagram account add failed for Page …` Warning satırından okunur.
11. `HttpIOException` (yanıt gövdesi okunurken kopan bağlantı) `IOException`'dan türediği için ekleme sırasında `Sayfalar okunamadı…` yerine `Hesap eklenemedi, yeniden dene.` metnine düşer; iki metin de yeniden denemeyi söyler.
12. Eksik Sayfa onay çekmecesi açıkken izin güncellemesi, çekmecede karar verilene dek bitmez (onay işlevi iptal belirtecini izlemez).
13. Testler: sohbet etiketi ipucunun "ofset mesajın anından alınır" testi yalnız UTC olmayan makinede ayırt eder (CI UTC'dir; üretim kodu doğru). Tekten çoka devir testlerinde eski poller "yayın yok" yanıtıyla susturulduğu için A hesabının sağlık durumu devre dek `BroadcastEnded` görünür; testler buna bakmaz.
14. Bu PR'ın değiştirmediği, önceden var olan davranış (ayrı iş): ana token dosyası silinemezse `ClearAsync` hatayı yutar; `DisconnectAsync` "kaldırıldı" der ama sonraki açılışta bağlantı geri gelir.
