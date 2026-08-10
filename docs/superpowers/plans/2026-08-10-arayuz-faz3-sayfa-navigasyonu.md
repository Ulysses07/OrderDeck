# Arayüz Yenilemesi Faz 3 — Sayfa Navigasyonu Implementasyon Planı

**Tarih:** 2026-08-10
**Spec:** `docs/superpowers/specs/2026-08-07-arayuz-yenileme-design.md` §6.1, §9
**Önceki fazlar:** Faz 0 (#238) · Faz 1 (#239, #240) · Faz 2a (#241) · Faz 2b (#244)

---

## Bağlam — bunu okumadan başlama

Spec §6 tek cümleyle özetlenir: **hiçbir şey pop-up olarak açılmayacak.**
Faz 2 çekmeceleri getirdi (yayın sırasında açılanlar, sağ sütunu örter,
sohbet solda kalır). Faz 3 ikinci kalıbı getiriyor: **sayfa** — yayın
*dışında* bakılan, tüm içerik alanını kaplayan görünümler.

### Bugünkü durum (2026-08-10'da tek tek ölçüldü, tahmin değil)

Sol nav **navigasyon değil**. `ShellSidebar`'daki 5 düğme ve taşma
menüsündeki 4 madde doğrudan `ShowDialog()` çağırıyor
([MainShellViewModel.cs:1014-1059](../../../OrderDeck.App/ViewModels/MainShellViewModel.cs)).
Kabukta ne seçili-nav durumu var, ne de bir sayfa yuvası.

`MainShellView` ızgarası:

```
┌──────────┬──────────────────────────────────────┐
│          │  satır 0: ShellTopBar                │
│ Shell    ├──────────────────────────────────────┤
│ Sidebar  │  satır 1: ShellBanners               │
│          ├──────────────────────────────────────┤
│          │  satır 2: ChatPanel │ sağ sütun      │
│          │                     │ (+ DrawerHost) │
└──────────┴──────────────────────────────────────┘
```

### `DialogResult` korkusu asılsız çıktı

Faz 2b sonrası "dört diyalog `DialogResult` sözleşmesine bağlı, sayfa bunu
karşılayamaz" diye bir endişe not edilmişti. **Yanlış.** Her çağrı yeri
okundu:

| View | Çağıran | Sonucu kullanıyor mu? |
|---|---|---|
| Settings, StreamHistory, PeriodReport, Blacklist, SupportRequests, BulkSms, Account, ShortcutHelp, StreamReport | `MainShellViewModel` | **hayır** — düz `dlg.ShowDialog();` |
| BackupTransfer | `CustomerDetailViewModel:299` | hayır |
| **Restore** | `App.xaml.cs:131` — `if (ok == true) → yeniden başlat` | **evet** |

Tek gerçek tüketici `RestoreDialog` ve o **kabuk daha doğmadan** çalışıyor
(veritabanı boşsa açılışta sorulur). Yani sayfa olamaz → **Faz 4'e**,
`LoginDialog` + `FirstRunWizard` ile birlikte (spec §6.4, kabuk-öncesi
görünümler).

Sonucu kullanmayan çağrılarda bile **kapanış SONRASI iş** var:
`OpenSettings` ve `OpenBlacklist` dönüşte `RefreshHighlights()` çağırıyor.
Bu yüzden sayfa servisi `await` edilebilir olmalı (aşağıda).

### Spec §6.1 düzeltmeleri (bu planla birlikte spec'e işlenecek)

Spec 12 sayfa sayıyor. İkisi düştü:

- `ShipmentThresholdDialog` — **Faz 2b'de çekmece oldu** (#244), zincirin
  üçüncü seviyesi. Sayfa listesinden çıkar.
- `RestoreDialog` — yukarıdaki gerekçeyle **Faz 4** (kabuk-öncesi).

→ **Faz 3 = 10 sayfa.**

---

## Kabul edilen kararlar (kullanıcı onayı 2026-08-10)

1. **Üst bar sayfada da görünür kalır.** Sayfa yalnız satır 1-2'yi örter.
   Gerekçe: yayın sırasında Ayarlar'a giren operatör *Yayını Bitir*
   düğmesini ve izleyici sayısını kaybetmemeli. Bugünkü modal pencere bunu
   kaybettiriyor — sayfa bu yönüyle **düzeltme**.
2. **Sayfa geri yığını olacak.** `StreamHistory → StreamReport` bugün iç içe
   modal; yığın olmazsa rapordan listeye dönüş yolu kalmaz.
3. **Onu da sayfa yap** (`ShortcutHelpDialog`). Kural basit kalsın: 10'u da
   sayfa. Rahatsız ederse sonradan ucuz.
4. **PR bölünmesi bana bırakıldı** → 4 PR (aşağıda).

---

## Mimari

Çekmece altyapısının (`Services/Drawers/`) **birebir kardeşi**. Aynı kalıp
iki kez yazılmış olacak; ortak bir taban sınıfa soyutlanmayacak, çünkü iki
katmanın davranışı üç yerde ayrışıyor (sonuç sözleşmesi, yığın görünürlüğü,
kapladığı alan) ve ortak taban her seferinde `if (isDrawer)` üretirdi.

### `Services/Pages/Page.cs`

Tek bir açık sayfanın canlı örneği.

```csharp
public sealed class Page
{
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Page(string key, string title) { Key = key; Title = title; }

    /// <summary>Nav vurgusu için kimlik ("history", "settings"…).</summary>
    public string Key { get; }
    public string Title { get; }
    public object? Content { get; internal set; }

    /// <summary>Kapanınca tamamlanır. Çekmecenin aksine BOOL YOK —
    /// sayfada "onayla/iptal" diye bir şey yok, çıkış tek türlü.</summary>
    public Task Completion => _completion.Task;

    public void Close() { /* çift çağrı sessiz; Closed → TrySetResult */ }

    internal event Action<Page>? Closed;
}
```

**Neden `Task<bool>` değil `Task`:** `Drawer` bool taşıyor çünkü
`ShowDialog() == true` sözleşmesinin karşılığı olması gerekiyordu (26 çağrı
yeri). Sayfada öyle bir sözleşme yok — yukarıdaki tabloda görüldüğü gibi
**hiçbir çağıran sonucu okumuyor**. Olmayan bir sözleşmeyi taklit etmek
her sayfaya anlamsız bir `return true` ekletirdi.

### `Services/Pages/PageStack.cs` + `IPageService`

`DrawerStack` ile aynı iskelet: `ObservableCollection<Page>`, `Top`,
`IsOpen`, kapanınca üsttekileri iptal etme.

```csharp
public interface IPageService
{
    /// <summary>Sayfayı açar ve kapanmasını bekler.</summary>
    Task ShowAsync(string key, string title, Func<Page, object> buildContent);

    /// <summary>En üstteki sayfayı kapatır. Açık sayfa yoksa false —
    /// ESC işleyicisi tuşu tüketmesin.</summary>
    bool Back();
}
```

`PageStack` ayrıca `public string? CurrentKey => Top?.Key;` yayınlar; nav
vurgusu buna bakar.

**Kayıt (AppHost):** çekmecedeki gibi **tek örnek iki rolde** —
`AddSingleton<PageStack>()` + `AddSingleton<IPageService>(sp => sp.Get…<PageStack>())`.
İki ayrı kayıt olsaydı host boş bir yığına bakardı (bu tuzağa Faz 2a'da
düşülmüştü, yorumu `AppHost.cs:335`'te duruyor).

### `Views/Shell/PageHost.xaml`

Çekmeceden **üç farkı** var:

| | DrawerHost | PageHost |
|---|---|---|
| Kapladığı alan | yalnız sağ sütun | satır 1-2 (üst bar hariç) |
| Yığındaki alttakiler | soluk görünür (Opacity .35) | **hiç çizilmez** |
| Modal mı | hayır (sohbet tıklanabilir) | evet (altındaki her şeyi örter) |

Alttakilerin çizilmemesi bilinçli: sayfa zaten tam örtüyor, iki sayfayı üst
üste ölçmek boşuna yerleşim maliyeti. Bu yüzden `ItemsControl` değil
**`ContentControl Content="{Binding Top.Content}"`**.

```xml
<Border Background="{StaticResource OD.Brush.Bg}"
        Visibility="{Binding IsOpen, Converter={StaticResource BoolToVisibleConverter}}">
    <Grid>
        <!-- satır 0: geri + başlık  |  satır 1: içerik -->
        <Button Click="OnBackClick" ToolTip="Geri (ESC)">   <!-- OD.Path.ChevronLeft -->
        <TextBlock Text="{Binding Top.Title}"/>
        <ContentControl Content="{Binding Top.Content}"/>
    </Grid>
</Border>
```

Tek "geri" düğmesi, iki anlam: yığında tek sayfa varsa kabuğa döner, iki
sayfa varsa altındakine. Ayrı bir "kapat" düğmesi eklenmeyecek — operatöre
iki farklı çıkış sunmak, ikisinin farklı şey yaptığını sandırır.

**Zemin `OD.Brush.Bg`, `Surface` değil:** sayfa bir kart değil, ekranın
kendisi. Çekmece `Surface` çünkü o gerçekten kabuğun üstünde duran bir
panel.

### `MainShellView` yerleşimi

```xml
<shell:ShellTopBar  Grid.Row="0"/>
<shell:ShellBanners Grid.Row="1" .../>
<Grid Grid.Row="2" ...>  <!-- sohbet | sağ sütun -->
   …
</Grid>

<!-- Sayfa katmanı: satır 1-2'yi örter, üst bar açıkta kalır (karar 1).
     Kardeşlerinden SONRA yazılı olması z-düzenini veriyor — DrawerHost'ta
     da kullanılan kalıp. -->
<shell:PageHost Grid.Row="1" Grid.RowSpan="2"/>
```

`PageHost`'un `DataContext`'i `PageStack`; `MainShellViewModel` **değil**
(yine `DrawerHost` kalıbı).

### ESC zinciri

`MainShellView.OnWindowPreviewKeyDown` bugün: çekmece → yedek seçim modu.
Araya sayfa girer:

```
ESC → 1) açık çekmece varsa en üsttekini kapat
      2) yoksa açık sayfa varsa geri git
      3) o da yoksa yedek seçim modundan çık
```

Sıra tartışmasız: **operatörün ESC'ye bastığında kastettiği şey her zaman
ekranın en üstündeki katmandır.** Çekmece sayfanın üstünde çizilir mi?
Hayır — sayfa satır 1-2'yi, çekmece sağ sütunu örtüyor ve sayfa katmanı
sonra yazılı, yani sayfa üstte. **Ama** sayfa açıkken çekmece açılması
mümkün değil (sayfadan çekmece açan bir akış yok, kontrol edildi), o yüzden
sıra pratikte hiç çakışmıyor. Yine de çekmece önce denenmeli: ileride
böyle bir akış eklenirse doğru davranış kendiliğinden gelir.

### Nav vurgusu

`ShellSidebar`'ın `NavButton` stiline tek tetikleyici:

```xml
<DataTrigger Binding="{Binding Pages.CurrentKey}" Value="history">
    <Setter Property="Background" Value="{StaticResource OD.Brush.Surface2}"/>
    <Setter Property="Foreground" Value="{StaticResource OD.Brush.Text}"/>
</DataTrigger>
```

Tetikleyici düğmenin kendi stilinde değil, **her nav düğmesinde ayrı**
tanımlanır (`Value` sabit olmak zorunda). `MainShellViewModel` yeni bir
`public PageStack Pages { get; }` özelliği yayınlar — böylece kenar çubuğu
`{Binding Pages.CurrentKey}` diyebilir ve ViewModel'de aynalama kodu
yazmaya gerek kalmaz.

---

## Window → sayfa dönüşüm reçetesi

Her view için mekanik, 5 adım:

1. **XAML kökü:** `<Window …>` → `<UserControl …>`. Şu öznitelikler
   **silinir** (çerçeveyi artık `PageHost` veriyor): `Title`, `Width`,
   `Height`, `SizeToContent`, `WindowStartupLocation`, `ResizeMode`,
   `Background`, `Style`, `WindowStyle`.
2. **Code-behind:** `: Window` → `: UserControl`. `DialogResult = …`
   satırları silinir. `Close()` → `_page.Close()`.
3. **Kurucu:** `Page`'i alan bir fabrika eklenir — `GiveawayDrawer.Create`
   kalıbı. DI'dan gelen ViewModel'ler kuruculara aynen geçer.
4. **AppHost:** `AddTransient<Views.XDialog>()` kaydı silinir; ViewModel
   kaydı **kalır** (fabrika onu DI'dan alacak).
5. **Çağrı yeri:**
   ```csharp
   // önce
   var dlg = App.Host.Services.GetRequiredService<BlacklistDialog>();
   dlg.Owner = Application.Current?.MainWindow;
   dlg.ShowDialog();
   RefreshHighlights();

   // sonra
   await _pages.ShowAsync("blacklist", "Kara Liste",
       p => BlacklistPage.Create(p, App.Host.Services.GetRequiredService<BlacklistViewModel>()));
   RefreshHighlights();
   ```
   Komut `[RelayCommand] private async Task OpenBlacklistAsync()` olur.
   **Üretilen komut adı değişmez** (toolkit "Async" ekini atıyor) → hem
   `ShellSidebar` bağları hem `ShortcutBinder`'daki dört giriş
   ([ShortcutBinder.cs:59-62](../../../OrderDeck.App/Shortcuts/ShortcutBinder.cs))
   olduğu gibi kalır. Faz 2b'de aynısı `StartGiveawayCommand` için
   doğrulandı.

**Dosya adları:** `Views/Pages/` altına, `XDialog` → `XPage`
(`Views/Drawers/` + `XDrawer` kalıbıyla simetrik).

---

## PR bölünmesi

### PR-1 — `feat(shell): Faz 3a — sayfa altyapısı + altı sayfa`

Altyapı + kolay dönüşümler. Toplam ~515 satır XAML dönüşüyor.

| # | Görev |
|---|---|
| 1 | `Services/Pages/Page.cs`, `PageStack.cs`, `IPageService.cs` |
| 2 | `Views/Shell/PageHost.xaml(.cs)` |
| 3 | `MainShellView` yuvası + ESC zinciri + `MainShellViewModel.Pages` |
| 4 | `ShellSidebar` nav vurgusu (5 düğme + taşma menüsü) |
| 5 | `BlacklistPage` (66) |
| 6 | `StreamHistoryPage` (44) + `StreamReportPage` (150) — **geri yığınının gerçek sınavı** |
| 7 | `PeriodReportPage` (116) |
| 8 | `AccountPage` (98) |
| 9 | `ShortcutHelpPage` (41) |
| 10 | Testler: `PageStackTests` (yığın/geri/kapanış), `PageHost` kompozisyon dumanı |

**Görev 6'nın ayrıntısı:** `StreamHistoryDialog.xaml.cs:20-23` bugün
`StreamReportDialog`'u iç içe `ShowDialog()` ile açıyor. Sayfaya dönünce
`_pages.ShowAsync("stream-report", …)` olur ve yığın iki seviye olur —
geri düğmesi listeye döner. Ayrıca rapor **yayın bitince kendiliğinden de**
açılıyor ([MainShellViewModel.cs:849](../../../OrderDeck.App/ViewModels/MainShellViewModel.cs));
orada yığın tek seviye kalır, geri kabuğa döner.

### PR-2 — `feat(shell): Faz 3b — Ayarlar sayfası`

Tek başına bir PR, çünkü tek başına Faz 3'ün yarısı:

- `SettingsDialog.xaml` **561 satır** → `SettingsPage`
- `Themes/SettingsTheme.xaml` **434 satır** silinir (spec §9: o sözlük
  yalnız bu pencere için vardı). İçindeki stiller `Controls.xaml`
  tokenlarına çevrilir; sabit hex'ler `OD.Brush.*`'a döner.
- Spec §10 sayaçlarına ciddi etki: bu PR'dan sonra hardcoded hex ve
  benzersiz `FontSize` sayıları ölçülüp spec'e yazılır.

### PR-3 — `feat(shell): Faz 3c — destek talepleri + toplu SMS`

`SupportRequestsDialog` (127) ve `BulkSmsDialog` (139). İkisinin ortak
farkı: kendi code-behind'larında bir `.Open()` metodu var ve `ShowDialog()`
oradan çağrılıyor ([SupportRequestsDialog.xaml.cs:26](../../../OrderDeck.App/Views/SupportRequestsDialog.xaml.cs),
[BulkSmsDialog.xaml.cs:25](../../../OrderDeck.App/Views/BulkSmsDialog.xaml.cs)).
`.Open()` düşer, açma işi çağırana (`MainShellViewModel`) taşınır.

### PR-4 — `feat(shell): Faz 2b kalanı + yedek aktarma sayfası`

**Sıra zorunlu, tercih değil.** `BackupTransferDialog` (98) sayfa olacak
ama onu açan `CustomerDetailViewModel:299`; `CustomerDetailDialog` hâlâ
modal `Window`. Modal pencerenin içinden açılan bir sayfa pencerenin
ARKASINA çizilir ve `await` hiç dönmez — bu tuzak Faz 2b'de ölçüldü. Yani
önce Faz 2b'nin kalanı:

| # | Görev |
|---|---|
| 1 | `CustomerSearchDrawer` (çağıran: nav + `MainShellViewModel:725`) |
| 2 | `CustomerDetailDrawer` (+ `AddBalanceDrawer`, `CancelLabelDrawer`) |
| 3 | `PhoneEntryDrawer`, `FacebookPagePickerDrawer`, `AddToBlacklistDrawer` |
| 4 | `BackupTransferPage` |

**Açık soru, PR-4'e girerken karara bağlanacak:** çekmeceden sayfa açmak.
`CustomerDetail` bir çekmece (sağ sütun), `BackupTransfer` bir sayfa
(satır 1-2) → sayfa çekmecenin üstünü örter, çekmece altında açık kalır.
Teknik olarak çalışır ama tuhaf. Alternatif: `BackupTransfer`'ı da çekmece
yapmak (spec §6.1'den §6.2'ye taşımak). PR-4 yazılırken ekranda görülüp
karar verilecek; şimdiden tahmin edilmeyecek.

---

## Doğrulama

Her PR için:

1. `dotnet build OrderDeck.App/OrderDeck.App.csproj` → 0 hata, 0 yeni uyarı.
2. `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` → hepsi geçer
   (Faz 2b sonrası taban: 895).
3. **Kompozisyon dumanı:** her yeni sayfa `UserControl` olarak örneklenip
   ölçülür. Faz 1-2'deki tuzak burada da geçerli — bozuk bir `StaticResource`
   derlemede değil, ilk gösterimde patlar.
4. **Görsel:** offscreen render + `OD.Brush.Bg` zeminli PNG. (Faz 2b dersi:
   host zemini için var olmayan bir token verilirse PNG saydam çıkar ve
   "yazı soluk" diye yanlış teşhis kurarsın. `OD.Brush.Surface1` **yoktur**.)
5. **Elle:** nav'dan her sayfaya gir-çık, ESC ile çık, `StreamHistory →
   StreamReport → geri → geri` zincirini yürü, sayfadayken üst bardan
   yayın başlat/bitir denenir (karar 1'in sınavı).

---

## Faz 3 kapsamı DIŞI

- `RestoreDialog`, `LoginDialog`, `FirstRunWizard` → **Faz 4**
  (kabuk-öncesi, spec §6.4).
- `DarkControls.xaml`'in silinmesi → **Faz 4**.
- Faz 1'in gerçek-yayın denemesi (spec §10, 10 maddelik liste) — hâlâ
  yapılmadı, Faz 3'ü beklemiyor.
- Stok sistemi ve Postgres göçü — arayüz bitmeden başlanmayacak.
