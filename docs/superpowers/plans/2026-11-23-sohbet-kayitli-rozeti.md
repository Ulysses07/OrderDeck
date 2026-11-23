# Sohbette "Kayıtlı" rozeti — uygulama planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Canlı sohbet satırında, telefonu kayıtlı müşterinin (kişi düzeyinde) adının sağında "Kayıtlı" rozeti göstermek.

**Architecture:** Depo, sohbet göndereninin iki işaretini (kara liste + kayıtlı) tek çağrıda verir. Kabuk bunu satır modeline taşır ve iki anda ekrandaki satırları yeniden değerlendirir. Görünüm, ada göre büyüyen ad alanının sağ ucunda bir çip çizer; ad kesilmez, yer mesajdan alınır.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, Dapper + SQLite, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-11-23-sohbet-kayitli-rozeti-design.md`

## Global Constraints

- Kayıtlı kuralı: `Phone IS NOT NULL AND TRIM(Phone) <> ''` (arama çekmecesindeki `CountRegistered` ile aynı).
- Kişi kuralı `CustomerGroup.IsGrouped` / ham `GroupId` eşitliği; boş ya da yalnız boşluk `GroupId` grup DEĞİLDİR.
- Rozet metni tam olarak `Kayıtlı`; ipucu tam olarak `Telefonu kayıtlı müşteri`.
- Ad KESİLMEZ; rozet adın yerinden yemez. Ad alanı ada göre büyür: tabanı `OD.Layout.ChatNameColumn` (184), tavanı `OD.Layout.ChatNameColumnMax` (300). Ada sığan satırlarda mesaj aynı hizada başlar; uzun adda yer mesajdan alınır. (23.11 akşamı Burak'ın kararıyla değişti; ilk hâli "ad sütununun genişliği ve mesajın başladığı x konumu değişmez"di ve adı kırpıyordu. Bkz. tasarım belgesi, "Görünüm kararı".)
- Hareket (animasyon) yok.
- `CustomerRepository.IsBlacklistedAnySpelling` ve çağıranları değişmez.
- Sunucu, yerel şema, uzantı, yazdırma kuyruğu, overlay değişmez. Göç yok.
- Testlerde telefon literal'i yok (`TestPhone.NewE164()`); kullanıcı adları üretilir.
- Commit iletileri Türkçe emir kipi; son satır `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.

## Review Focus

Tasarımın ima ettiği, kolay kaçan beş durum; her birinin testi sahibi olan görevde:

1. Boş / yalnız boşluk `GroupId` taşıyan ilgisiz müşteriler birbirinin telefonunu "miras" almaz (Görev 1).
2. Kullanıcı adını farklı harf büyüklüğüyle yazan kayıtlı müşteri tanınır (Görev 1).
3. Telefonu yalnız öbür "@" yazımındaki ikiz satırda olan kişi kayıtlıdır (Görev 1).
4. Aynı gönderenin ekrandaki birden çok satırı tazelemede birlikte döner; sayaç sıfırlanınca boşuna tazelenmez (Görev 2).
5. Rozet ad alanının dışına taşmaz ve adı kısaltmaz; uzun ad kesilmez, tavana sığmayan ad alt satıra iner ve en dar pencerede de mesaj görünür kalır (Görev 3).

---

### Task 1: Depo — sohbet göndereninin işaretleri

**Files:**
- Create: `OrderDeck.Core/Customers/ChatSenderMarks.cs`
- Modify: `OrderDeck.Core/Storage/Repositories/CustomerRepository.cs` (`IsBlacklistedAnySpelling`'in hemen altı)
- Test: `OrderDeck.Tests/Storage/CustomerRepositoryChatSenderMarksTests.cs` (yeni)

**Interfaces:**
- Produces: `public readonly record struct ChatSenderMarks(bool IsBlacklisted, bool IsRegistered)` (ad alanı `OrderDeck.Core.Customers`); `public ChatSenderMarks GetChatSenderMarks(string platform, string username)` (`CustomerRepository`).

- [ ] **Step 1: Düşen testleri yaz**

Kurulum: `using var db = new InMemorySqlite(); new MigrationRunner(db).Run(); var repo = new CustomerRepository(db);`.
Tohumlama yardımcısı: `new Customer(Guid.NewGuid().ToString("N"), platform, username, username, null, 100, 100, blacklisted, null, null, 0, 0m, null, null, phone, GroupId: groupId)` + `repo.Insert(...)`.

| Test | Kurulum | Beklenen `GetChatSenderMarks` |
|---|---|---|
| `Taninmayan_gonderen_isaretsizdir` | satır yok | `(false, false)` |
| `Telefonu_olan_satir_kayitlidir` | telefonlu satır | `IsRegistered == true` |
| `Telefon_yalniz_bosluksa_kayitli_degildir` | `Phone = "  "` | `false` |
| `Telefon_bagli_hesaptaysa_yazan_hesap_da_kayitlidir` | aynı `GroupId`: instagram telefonlu, tiktok telefonsuz; tiktok sorulur | `true` |
| `Bagli_hesaplarin_hicbirinde_telefon_yoksa_kayitli_degildir` | aynı `GroupId`, iki telefonsuz satır | `false` |
| `Bos_GroupId_grup_sayilmaz` (`[Theory]` `""`, `" "`) | aynı boş `GroupId`: A telefonlu, B telefonsuz; B sorulur | `false` |
| `Obur_at_yazimindaki_ikiz_telefonluysa_kayitlidir` | instagram `"@ad"` telefonlu; `"ad"` sorulur | `true` |
| `Harf_farkiyla_yazan_kayitli_musteri_taninir` | `"ad"` telefonlu; büyük harfli yazımı sorulur | `true` |
| `Kara_liste_ve_kayitli_birbirinden_bagimsizdir` | kara listede + telefonlu / yalnız biri | iki bayrak ayrı ayrı doğru |
| `Kara_liste_sonucu_IsBlacklistedAnySpelling_ile_aynidir` | kara liste yalnız `"@ad"` ikizinde; `"ad"` sorulur | `IsBlacklisted == repo.IsBlacklistedAnySpelling(...) == true` |

- [ ] **Step 2: Koş, düştüğünü gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~CustomerRepositoryChatSenderMarksTests"`
Expected: derleme hatası (`GetChatSenderMarks` yok).

- [ ] **Step 3: `ChatSenderMarks` ve `GetChatSenderMarks`'ı yaz**

Tek bağlantıda: verilen yazımın satırı (`FindSpelling`), varsa öbür "@" yazımının satırı (`CustomerIdentity.AlternateAtSpellingOf`). Kara liste: iki satırdan biri işaretliyse. Kayıtlı: iki satırdan biri için "satırın telefonu dolu, ya da satır gruplu (`CustomerGroup.IsGrouped`) ve o `GroupId`'de telefonu dolu bir satır var" (`SELECT EXISTS(...)`, `IX_Customer_GroupId`).

- [ ] **Step 4: Koş, geçtiğini gör**

Run: aynı komut. Expected: hepsi PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Core/Customers/ChatSenderMarks.cs OrderDeck.Core/Storage/Repositories/CustomerRepository.cs OrderDeck.Tests/Storage/CustomerRepositoryChatSenderMarksTests.cs
git commit -m "feat(chat): sohbet göndereninin kayıtlı işaretini depodan ver"
```

---

### Task 2: Satır modeli ve kabuk

**Files:**
- Modify: `OrderDeck.App/ViewModels/ChatMessageViewModel.cs`
- Modify: `OrderDeck.App/ViewModels/MainShellViewModel.cs` (`DrainPendingChat`, `RefreshHighlights`, `OnNewIntakeSubmissionsCountChanged`)
- Test: `OrderDeck.Tests/App/MainShellChatRegisteredTests.cs` (yeni), `OrderDeck.Tests/ViewModels/ChatMessageViewModelTests.cs`

**Interfaces:**
- Consumes: `CustomerRepository.GetChatSenderMarks(string, string) : ChatSenderMarks`.
- Produces: `ChatMessageViewModel.IsSenderRegistered` (gözlemlenebilir `bool`); kurucu `ChatMessageViewModel(ChatMessage message, bool isSenderBlacklisted, bool isSenderRegistered = false)`; `MainShellViewModel`'de `internal void DrainPendingChat()` (test dikişi; davranış aynı).

- [ ] **Step 1: Düşen testleri yaz**

`MainShellChatRegisteredTests` (`using var h = MainShellTestHarness.Build();`, müşteri `h.CustomerRepo.Insert(...)`, mesaj `h.Bus.Publish(msg); h.Vm.DrainPendingChat();`):

| Test | Adımlar | Beklenen |
|---|---|---|
| `Gelen_mesajin_satiri_kayitli_musteride_isaretli_dogar` | telefonlu müşteri yazar | `ChatMessages.Single().IsSenderRegistered == true` |
| `Taninmayan_gonderenin_satiri_isaretsizdir` | hiç satırı olmayan biri yazar | `false` |
| `Kara_liste_vurgusu_gelen_mesajda_calismaya_devam_eder` | kara listedeki müşteri yazar | `IsSenderBlacklisted == true` |
| `Yeni_form_kaydi_gelince_ayni_gonderenin_butun_satirlari_kayitliya_doner` | telefonsuz müşteri iki mesaj yazar; `h.CustomerRepo.UpdatePhone(id, TestPhone.NewE164())`; `h.Vm.NewIntakeSubmissionsCount += 1` | iki satır da `true` |
| `Sayac_sifirlaninca_satirlar_yeniden_sorgulanmaz` | telefonsuz müşteri yazar; `NewIntakeSubmissionsCount = 1` (satır hâlâ `false`); `UpdatePhone`; `NewIntakeSubmissionsCount = 0` (çekmece açılınca olan sıfırlama, yeni kayıt değil) | satır `false` kalır |

`ChatMessageViewModelTests`: `Kayitli_isareti_kurucudan_gelir_varsayilan_kapali` — üçüncü parametresiz kurulan satırda `false`, `isSenderRegistered: true` ile `true`.

- [ ] **Step 2: Koş, düştüğünü gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~MainShellChatRegisteredTests|FullyQualifiedName~ChatMessageViewModelTests"`
Expected: derleme hatası (`IsSenderRegistered`, `DrainPendingChat` erişimi).

- [ ] **Step 3: Uygula**

- `ChatMessageViewModel`: `[ObservableProperty] private bool _isSenderRegistered;` + kurucunun isteğe bağlı üçüncü parametresi.
- `MainShellViewModel.DrainPendingChat`: `IsBlacklistedAnySpelling` yerine `GetChatSenderMarks`; iki bayrak satıra geçer.
- `private void RefreshChatSenderMarks()`: `ChatMessages`'taki satırları (Platform, Username) çiftine göre gruplar, çift başına TEK depo çağrısıyla iki bayrağı yazar. `RefreshHighlights` sohbet kısmı için bunu çağırır (yazdırma kuyruğu kısmı aynı kalır).
- `OnNewIntakeSubmissionsCountChanged(int value)`: mevcut satıra ek olarak `if (value > 0) RefreshChatSenderMarks();`.
- `DrainPendingChat` `internal` olur (mesaj düzenekteki veri yolundan, `h.Bus.Publish` ile girer; `OnChatMessage` private kalır).

- [ ] **Step 4: Koş, geçtiğini gör**

Run: aynı komut + `FullyQualifiedName~MainShellChatFilterTests`. Expected: hepsi PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.App/ViewModels/ChatMessageViewModel.cs OrderDeck.App/ViewModels/MainShellViewModel.cs OrderDeck.Tests/App/MainShellChatRegisteredTests.cs OrderDeck.Tests/ViewModels/ChatMessageViewModelTests.cs
git commit -m "feat(chat): sohbet satırı kayıtlı işaretini taşısın, form kaydı gelince tazelensin"
```

---

### Task 3: Görünüm

> Bu görev üç kez yazıldı. İlk hâli rozeti sabit 132 px'lik ad sütununun içine koyuyordu ve adı kırpıyordu;
> Burak görüntüyü görünce kararı değiştirdi (tasarım belgesi, "Görünüm kararı"). İkinci hâli ad alanını ayrı
> bir `Auto` kolonda sabit tavana kadar büyütüyordu; yarım ekranda Instagram hesap etiketi taşıyan, uzun adlı
> satırda mesajı aç bırakıyordu (bağımsız incelemede bulundu). Aşağısı son hâlidir.

**Files:**
- Modify: `OrderDeck.App/Views/Shell/ChatPanel.xaml` (satır şablonu: ad alanı, mesaj ve hesap etiketi tek panelde)
- Modify: `OrderDeck.App/Themes/Metrics.xaml` (`OD.Layout.ChatNameColumn`, `OD.Layout.ChatNameColumnMax`, `OD.Pad.ChatMessageReserve`, `OD.Pad.ChatMessageReclaim`, `OD.Pad.Right2`, `OD.Pad.Right3`)
- Test: `OrderDeck.Tests/App/ChatPanelLayoutTests.cs`

**Interfaces:**
- Consumes: `ChatMessageViewModel.IsSenderRegistered`.

- [ ] **Step 1: Düşen testleri yaz**

Yardımcılar: adı verilen, kayıt durumu seçilen satır üreten `Row(string display, bool registered, string? username = null, string? sourceAccount = null)`; satırın rozet yazısını bulan `Badge(panel, row)` (`DataContext` o satır ve `Text == "Kayıtlı"` olan tek `TextBlock`); satırdaki ad yazı tipiyle verilen genişliği aşmayan en uzun adı üreten `NameWithin(width)` (sınır testleri yazı tipi ölçüsüne sayı gömmesin, rastgele ada bağlı kalmasın diye); mesaja ayrılan tabanı belirteçten okuyan `MessageFloor()`; en dar sohbet panelinin genişliğini kabuk belirteçlerinden hesaplayan, kaydırma çubuğunu da düşen `NarrowestPanelWidth()`. Görünürlük `IsVisible` ile değil, çipin kendi `Visibility` değeri ve çizilen genişlikle sınanır: düzenek pencere açmaz.

| Test | Beklenen |
|---|---|
| `Kayitli_satirda_rozet_gorunur_kayitsiz_satirda_gizlidir` | kayıtlıda çip `Visible`, metin `Kayıtlı`, ipucu `Telefonu kayıtlı müşteri`, yazı rengi `OD.Brush.Success`; kayıtsızda çip `Collapsed` |
| `Rozet_adin_yerinden_yemez` | kayıtlı satırda adın başladığı yerden rozete kadar olan yer (adın boşluğu düşülünce) ≥ `OD.Layout.ChatUserMaxWidth` (eski ad sütunu); ad rozetin altına girmez, rozet mesajın üstüne taşmaz |
| `Ada_sigan_satirlarda_mesaj_ayni_hizada_baslar` | kısa adlı kayıtlı, kısa adlı kayıtsız ve eski sütunu tam dolduran adlı kayıtlı satırda mesajın x konumu eşit; ad tek satır ve tamamı çizili; mesaj tam `OD.Layout.ChatNameColumn` kadar ötede başlar |
| `Mesaja_ayrilan_taban_ile_geri_alma_boslugu_ayni_buyukluktedir` | `OD.Pad.ChatMessageReserve` yalnız sağ, `OD.Pad.ChatMessageReclaim` yalnız sol ve onun eksi işaretlisi |
| `Uzun_ad_kesilmez_yeri_mesajdan_alinir` | eski sütundan uzun, rozetiyle tavanın altında kalan (ölçülerek kurulan) adla: ad tek satır ve tamamı çizili, `TextTrimming == None`; rozet adın ardında; mesaj kısa adlı satırdakinden sağda başlar ve satırın içinde kalır; adın ipucu `Username` |
| `Tavana_sigmayan_ad_alt_satira_iner_mesaja_yer_kalir` | 64 karakterlik adla: ad birden çok satır, ad alanı ≤ `OD.Layout.ChatNameColumnMax`, mesaj genişliği > 100 |
| `En_dar_pencerede_de_cok_uzun_adli_satirin_mesaji_gorunur` | en dar sohbet panelinde mesaj genişliği ≥ mesaja ayrılan taban |
| `En_dar_pencerede_hesap_etiketli_uzun_adli_satirda_da_mesaj_gorunur` | en dar panel + en geniş 16 karakterlik hesap etiketi + 30 karakterlik ad: mesaj ≥ taban ve etiketin solunda; etiket kırpılmaz ve satırın sağ kenarında; ad alt satıra iner (kırpılmaz); ad alanı tabanının altına sıkışmaz |
| `Kayitsiz_satirda_ad_rozetin_yerini_de_kullanir` | eski sütundan uzun ama tabana sığan kayıtsız ad: tamamı çizili, mesaj itilmez |
| `Ekrandaki_satir_sonradan_kayitli_olunca_rozet_belirir` | `IsSenderRegistered` sonradan açılınca çip `Visible`, ad alanının içinde; mesajın x konumu değişmez |
| `Uzun_adli_satir_sonradan_kayitli_olunca_ad_yine_kesilmez` | kayıtsızken tabana sığan, rozetle sığmayan ad: işaret açılınca ad tek satır ve tamamı çizili, mesaj en çok rozet kadar sağa kayar |
| `Rozet_satiri_yukseltmez` | kayıtlı ve kayıtsız satırın yüksekliği eşit |

Mevcut `Row(string? sourceAccount)` yardımcısının ürettiği ad 12 karaktere indirilir: artık uzun ad mesajı ittiği için, hesap etiketi testlerindeki "mesajın x konumu" karşılaştırması ada bağlı kalmasın.

- [ ] **Step 2: Koş, düştüğünü gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~ChatPanelLayoutTests"`
Expected: yeni testler FAIL, eskiler PASS.

- [ ] **Step 3: Satırı tek panele al, ad alanını büyüyen yap**

`Metrics.xaml`: `OD.Layout.ChatNameColumn` = 184, `OD.Layout.ChatNameColumnMax` = 300, `OD.Pad.ChatMessageReserve` (sağ 48), `OD.Pad.ChatMessageReclaim` (sol −48), `OD.Pad.Right2` (sağ 4), `OD.Pad.Right3` (sağ 8).

Satırın Grid'i iki kolona iner: platform ikonu (`Auto`) ve geri kalan (`*`). Geri kalan tek `DockPanel`'dir; çocukların yazılış sırası yer önceliğidir:

```xml
<DockPanel Grid.Column="1">
    <!-- 1) Instagram hesap etiketi: sağda, yerini ilk o alır (mevcut TextBlock, değişmedi) -->
    <TextBlock DockPanel.Dock="Right" Text="{Binding AccountTag}" ... />

    <!-- 2) Ad alanı: solda; sağ boşluğu mesaja ayrılan tabandır -->
    <DockPanel DockPanel.Dock="Left"
               MinWidth="{StaticResource OD.Layout.ChatNameColumn}"
               MaxWidth="{StaticResource OD.Layout.ChatNameColumnMax}"
               Margin="{StaticResource OD.Pad.ChatMessageReserve}">
        <Border DockPanel.Dock="Right"
                CornerRadius="{StaticResource OD.Radius.Xs}"
                Padding="{StaticResource OD.Pad.1}"
                Margin="{StaticResource OD.Pad.Right2}"
                Background="{StaticResource OD.Brush.Surface2}"
                VerticalAlignment="Center"
                ToolTip="Telefonu kayıtlı müşteri"
                Visibility="{Binding IsSenderRegistered,
                             Converter={StaticResource BoolToVisibleConverter}}">
            <TextBlock Text="Kayıtlı" Style="{StaticResource OD.Text.Micro}"
                       Foreground="{StaticResource OD.Brush.Success}"/>
        </Border>
        <TextBlock Text="{Binding Display}"
                   FontWeight="SemiBold" TextWrapping="Wrap"
                   VerticalAlignment="Center"
                   Margin="{StaticResource OD.Pad.Right3}"
                   ToolTip="{Binding Username}"/>
    </DockPanel>

    <!-- 3) Mesaj: kalanı doldurur; negatif sol boşlukla ayrılan tabanın üstüne oturur -->
    <TextBlock Text="{Binding Text}"
               TextWrapping="Wrap" VerticalAlignment="Center"
               Margin="{StaticResource OD.Pad.ChatMessageReclaim}"
               Foreground="{StaticResource OD.Brush.Text}"/>
</DockPanel>
```

Neden böyle: ad alanı ayrı bir `Auto` kolonda dursaydı Grid onu sonsuz genişlikle ölçerdi ve satırın genişliğinden habersiz büyürdü. Tek panelde ad alanı sonlu genişlikle ölçülür; sağ boşluğu yüzünden kalan genişliğin tabanı düşülmüş hâlini görür, oraya sığmayan ad alt satıra iner. Mesajın negatif sol boşluğu aynı büyüklüktedir: mesaj tam ad alanının bittiği yerde başlar ve ayrılan tabanı da kullanır. Gerekçeyi şablondaki öbür açıklamaların biçiminde bir XAML yorumuyla yaz.

- [ ] **Step 4: Koş, geçtiğini gör**

Run: aynı komut. Expected: hepsi PASS.

- [ ] **Step 5: Tam doğrulama ve commit**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj` (0 hata), ardından `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` (tam paket yeşil).

```bash
git add OrderDeck.App/Views/Shell/ChatPanel.xaml OrderDeck.App/Themes/Metrics.xaml OrderDeck.Tests/App/ChatPanelLayoutTests.cs
git commit -m "feat(chat): kayıtlı müşterinin adının yanında Kayıtlı rozeti göster, ad kesilmesin"
```
