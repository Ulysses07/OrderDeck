# Faz 4 — Açılış durumları: son pencereler shell'in içine

**Tarih:** 2026-08-10
**Üst spec:** `2026-08-07-arayuz-yenileme-design.md` §6, §9, §10 (Faz 4 satır 461-475)
**Durum:** tasarım onaylandı, plan yazılacak

---

## 1. Neden

Faz 1-3 sonunda uygulamanın tamamı tek `Window` içinde: sol ray, sayfa yığını,
çekmece yığını. Geriye **üç pencere** kaldı ve üçü de kullanıcının gördüğü **ilk**
ekranlar:

- `LoginDialog` (420×500) — lisans yoksa açılışta, ayrıca çalışırken hesap
  değiştirirken ve sihirbazın 2. adımından
- `RestoreDialog` (500×auto) — veritabanı yokken bulut yedeği varsa
- `FirstRunWizard` (6 adım) — ilk kurulumda

Üçü de eski `DarkControls.xaml` temasında; markaya uymuyorlar. Dahası hepsi
`App.OnStartup` içinde, `MainWindow` daha doğmadan `ShowDialog()` ile açılıyor —
yani "tek pencere" hedefi kâğıt üstünde kalıyor.

## 2. Karar: gerçekten shell'in içine

İki seçenek tartışıldı:

1. Pencere olarak kalsınlar, sadece yeni tasarım sistemine boyansınlar. Ucuz,
   risksiz — ama spec §10'un "`Window` kökü 1" hedefi kapanmaz.
2. Tam-ekran shell durumlarına dönsünler; açılış sırası tersine dönsün.

**Seçilen: 2.**

## 3. Mimari

### 3.1 Barındırma

`MainWindow.Content` yeni bir `AppRootView` olur:

```xml
<Grid>
  <ContentControl x:Name="ShellHost"/>   <!-- gate'ler geçilene kadar boş -->
  <ContentControl x:Name="GateHost"/>    <!-- tam ekran, opak, üstte -->
</Grid>
```

Tek barındırıcı, iki kat:

- **Açılışta** `ShellHost` boştur — `MainShellView` **kurulmaz**. Bu bir konfor
  tercihi değil, zorunluluk: geri yükleme durumu tam da veritabanı yokken
  çalışıyor, shell'in ViewModel'i o anda kurulamaz.
- Gate'ler geçilince `ShellHost`'a `MainShellView` girer, `GateHost` boşalır.
- **Çalışırken** gate gerekirse (hesap sayfasından giriş) `GateHost` yeniden
  dolar; shell altta yüklü kalır, `Unloaded` olmaz — yayın sürerken sohbet
  paneli, sayaçlar, çekmece yığını yerinde durur.

### 3.2 `AppGateStack`

`DrawerStack`'in kardeşi, aynı kalıp:

- `Task<bool> ShowAsync(string title, Func<Gate, object> buildContent)`
- Yığın — sihirbazın 2. adımı giriş gate'ini üstüne itebilsin diye
- `Gate.Close(bool)` → `ShowAsync`'in `Task<bool>`'unu tamamlar
- `IsOpen`, `Top` — `AppRootView` bunlara bağlanır

Çekmecelerden farkı: gate'ler **modal** — altındaki hiçbir şey tıklanamaz,
şerit/çarpı yok, ESC kapatmaz. Kapanış yalnız gate'in kendi düğmeleriyle; bu
yüzden her gate kendi çıkış yolunu **açıkça** taşımak zorunda:

| Gate | Çıkış düğmesi |
|---|---|
| `BootGate` | yok — kendiliğinden geçer |
| `LoginGate` (açılışta) | **Çıkış** → `Shutdown()` |
| `LoginGate` (çalışırken / sihirbazdan) | **Vazgeç** → `Gate.Close(false)` |
| `RestoreGate` | **Atla, yeni başlat** |
| `FirstRunGate` | **Daha sonra** |
| `SessionRecoveryGate` | **Çıkış** → `Shutdown()` |

`LoginGate` çıkış düğmesinin metni ve davranışı barındırma bağlamına göre
değişir; view'a bir `IsStartupGate` bayrağı geçirilir, iki ayrı view yazılmaz.

### 3.3 `StartupFlow`

Açılış sırası `App.OnStartup`'tan çıkıp ayrı bir async orkestratöre taşınır.
`OnStartup`'ta kalanlar: hata yakalayıcılar, kültür kilidi,
`AppDataMigrator.MigrateIfNeeded()`, `AppHost` kurulumu, `MainWindow.Show()`.

Pencere açıldıktan sonra `StartupFlow` sırayla:

| # | Durum | Koşul | Sonuç |
|---|---|---|---|
| 1 | **Boot** | her zaman | `licenseService.InitializeAsync()` beklenir |
| 2 | **Giriş** | `CurrentStatus == NoLicense` | iptal → `Shutdown()` |
| 3 | **Geri yükleme** | DB yok/10KB altı **ve** yedek var | başarı → yeniden başlat |
| 4 | **İlk kurulum** | `!HasCompletedFirstRun` | her hâlde devam |
| 5 | **Yayın kurtarma** | aktif oturum var | Devam / Bitir / Çıkış |
| 6 | **Shell** | — | overlay + ingestor + hosted service'ler, sonra `ShellHost` dolar |

`licenseService.InitializeAsync()` artık `Task.Run(...).GetAwaiter().GetResult()`
sarmalayıcısı olmadan, düz `await` ile çağrılır — kilitlenmeyi önlemek için
konulan o hack'e gerek kalmaz, çünkü akış zaten dispatcher'ı bloke etmiyor.

## 4. Ekranlar

Beşi de `OrderDeck.App/Views/Gates/` altında `UserControl`. Ortak kap:
`OD.Brush.Bg` zemin, ortalanmış ~440px sütun, üstte marka işareti — sol raydaki
accent kare + "OD" (`OD.Font.Display`) işaretinin büyütülmüş hâli.

### 4.1 `BootGate` *(yeni)*

Marka işareti + belirsiz ince ilerleme çubuğu + "Hazırlanıyor". Lisans kontrolü
bitene kadar görünür. Bugün bu aralıkta ekranda hiçbir şey yok.

### 4.2 `LoginGate`

`LoginDialog.xaml`'in dört modu (giriş / kayıt / e-posta onayı / lisans seçimi)
aynen taşınır. `LoginDialogViewModel` değişmez; `RequestClose` artık
`Gate.Close(true)` çağırır.

Değişenler:

- Mavi degrade "OD" kutusu (`#FF5B8DEF` / `#FF4A77D4`) → accent marka işareti
- Hata rengi `#FFF87171` → `OD.Brush.Accent`
- `Padding="34,7,8,7"`, `FontSize="17"`, `Margin="0,0,0,12"` gibi ham sayılar →
  `OD.Pad.*` / `OD.Font.F*`
- E-posta ve kilit `Path` ikonları kalır; `Stroke` token'a bağlanır
- Lisans seçimi `ListBox` → `OD.Card` kart listesi

**İki barındırma yolu, tek view:** aynı `LoginGate` hem açılışta (`ShellHost`
boşken) hem çalışırken (`AccountPage` → "giriş yap", shell altta) kullanılır.
`AccountDialogViewModel.OpenLogin()` `ShowDialog()` yerine `AppGateStack`
üzerinden `await` eder.

### 4.3 `RestoreGate`

Yedek listesi kart listesine döner: tarih (kalın), boyut, makine adı.

- 📅 emojisi düşer (spec §10: emoji ikon 0) → "Aylık" `OD.Chip`
- `#25D366`, `DarkBlue`, `Gray` → token
- Üç eylem korunur: **Atla, yeni başlat** / **Seçileni geri yükle** /
  **En son yedeği kullan**

Geri yükleme başarılıysa **aynı gate** "tamamlandı" durumuna geçer; tek düğme
**Yeniden Başlat**, basınca `Process.Start(Environment.ProcessPath)` + `Shutdown()`.
Bugünkü "MessageBox göster, kapan, kullanıcı elle açsın" adımı kalkar.

### 4.4 `FirstRunGate`

Altı adım (`FirstRunWizardViewModel` değişmeden) aynen; adım göstergesi
("Adım 2 / 6") üstte. 🎉 emojisi ve `#FFAAAAAA` / `#FF4ADE80` sabit renkleri düşer.

2. adımdaki **Etkinleştir**, `AppGateStack`'e `LoginGate` iter; kapanınca
sihirbaz aynı adımda geri gelir ve `UpdateLicenseStepStatus()` tazelenir.

### 4.5 `SessionRecoveryGate` *(yeni)*

Bugün `MessageBox.Show(..., YesNoCancel)`. Gate'e dönüşür: "Yarım kalmış yayın
bulundu" + yayın özeti (başlangıç zamanı, etiket sayısı) + üç düğme:

- **Devam et** → oturum aktif bırakılır
- **Yayını bitir** → `sessionService.End(...)`
- **Çıkış** → `Shutdown()`

## 5. Kapsam dışı (bilerek)

- **Port hatası / güncelleme MessageBox'ları.** Port çakışması uygulamanın
  kapanmadan önceki son sözü; orada shell kurmanın anlamı yok. Güncelleme
  bildirimi de açılışa özgü değil.
- **Akışın yeniden tasarlanması.** Sıra, koşullar ve düğme anlamları bugünkiyle
  birebir aynı kalır; değişen yalnız *nerede* göründükleri (artı geri yüklemedeki
  otomatik yeniden başlatma, çünkü oradaki MessageBox zaten kalkmak zorundaydı).

## 6. Faz 4a / 4b bölünmesi

`DarkControls.xaml`'in silinmesi ölçüldü ve üç gate ekranından büyük çıktı.
`Controls.xaml`'de anahtarlı karşılığı **olmayan**, hâlâ `DarkControls`'ün örtük
stillerine yaslanan kontroller ve görünümlerdeki kullanım sayıları:

| Kontrol | Kullanım |
|---|---|
| `ListBox` / `ListBoxItem` | 22 |
| `MenuItem` / `ContextMenu` | 16 / 4 |
| `CheckBox` | 15 |
| `ComboBox` (örtük kullanımlar) | 9 |
| `RadioButton` | 7 |
| `PasswordBox` | 3 |
| `TabControl` / `TabItem` | 2 |
| `TextBlock`, `ScrollBar`, `ToolTip`, `Separator` | uygulama geneli |

Örtük `TextBlock` stili ön plan rengini veriyor: dosya düz silinirse uygulamanın
her yerinde koyu zemin üstünde koyu yazı kalır. Risk profili gate'lerden tamamen
farklı — gate hatası açılışta görünür, bu hata *her yerde* görünür.

- **Faz 4a** *(bu spec)* — açılış sırasının tersine dönmesi + beş gate ekranı.
  `DarkControls.xaml` yerinde kalır.
- **Faz 4b** *(ayrı spec/plan)* — `Themes/Base.xaml`: yukarıdaki örtük stiller
  `OD.*` token'larıyla yeniden yazılır, `DarkControls.xaml` silinir,
  `ThemeMergeTests` güncellenir.

Spec §10'un "`Window` kökü 1" ve "sabit hex 0" ölçümleri **4b sonunda** kapanır.
4a tek başına da sevk edilebilir: üç pencere kalkar, açılış markaya uyar.

## 7. Testler

### 7.1 `StartupFlow` — headless

Akış `App.xaml.cs`'ten ayrı bir sınıfa çıkar, gate gösterimi `IAppGateService`
arkasında durur; testte sahte bir uygulama kullanılır.

- `NoLicense` → giriş gate'i istendi; iptal → kapanma istendi, shell hiç kurulmadı
- DB yok + yedek var → geri yükleme gate'i; başarı → yeniden başlatma istendi
- `!HasCompletedFirstRun` → sihirbaz; "daha sonra" → bayrak yazılmadı, akış devam etti
- Aktif oturum → kurtarma gate'i; üç düğme üç ayrı sonuç
- Hepsi geçildi → `ShellHost` doldu, gate katmanı boşaldı

### 7.2 `GateCompositionTests` — render

Beş gate STA altında gerçekten çizilir (Faz 3'teki `MainShellViewCompositionTests`
kalıbı). Kaynak çözümlemesi kırıksa test patlar.

### 7.3 CI tuzağı

PR #247'de "testlerde `App.OnStartup` koşuyor" kilitlenmesi vardı. `StartupFlow`
`App` kurulunca **kendiliğinden tetiklenmemeli**; tek tetikleyici `OnStartup`.

### 7.4 Elle doğrulama

1. Temiz kurulum — boot → giriş → sihirbaz → shell
2. Lisanslı, DB silinmiş — geri yükleme → yeniden başlat
3. Yayın açıkken uygulamayı öldür, tekrar aç — kurtarma ekranı
4. Çalışırken hesap sayfasından çıkış + giriş — shell altta duruyor, sohbet
   kaybolmuyor

## 8. Dokunulan dosyalar (beklenen)

**Yeni:** `Views/AppRootView.xaml(.cs)`, `Views/Gates/BootGate`, `LoginGate`,
`RestoreGate`, `FirstRunGate`, `SessionRecoveryGate`, `Services/AppGateStack.cs`,
`Services/IAppGateService.cs`, `Startup/StartupFlow.cs`

**Silinen:** `Views/LoginDialog.xaml(.cs)`, `Views/RestoreDialog.xaml(.cs)`,
`Views/FirstRunWizard.xaml(.cs)`

**Değişen:** `App.xaml.cs` (açılış akışı çıkar), `MainWindow.xaml`,
`ViewModels/AccountDialogViewModel.cs`, `ViewModels/FirstRunWizardViewModel.cs`,
`ViewModels/RestoreDialogViewModel.cs` (yeniden başlatma sonucu),
`AppHost.cs` (DI kayıtları)
