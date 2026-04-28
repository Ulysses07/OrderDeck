# Faz 3a — Müşteri Merkezi (Tasarım)

**Hedef:** Müşterinin geçmişini (etiketler + çekiliş katılımları) tek bir dialog'da göster, schema'dan ölü `TrustScore`/sipariş alanlarını kaldır, müşterileri arayabilir hale getir.

**Kapsam:** 1 migration (005), 1 entity refactor (Customer), 3 yeni repo metodu, 1 detay dialog (3 tab), 1 search dialog, 4 erişim noktası.

**Pre-Faz-3a state:** Faz 3d HEAD `155c061`, 83/83 test geçer.

---

## 1. Bağlam

**Sorun 1 — ölü alanlar:** `Customer` entity'sinde `TrustScore`, `TotalOrders`, `CompletedOrders`, `CancelledOrders` alanları orijinal tasarımdan kalma. P1b "etiket workflow"una pivot ettiğinde sipariş kavramı kalktı; bu alanlar artık güncellenmiyor. Schema'yı temizliyoruz.

**Sorun 2 — müşteri görünürlüğü yok:** DB'de zengin veri (etiketler, çekiliş katılımları, kara liste sebebi, ilk/son görülme) tutuluyor ama yayıncı bunu görmenin yolu yok. Ekledikten sonra "bu kullanıcı kim, daha önce ne aldı?" sorusu cevapsız kalıyor.

**Sorun 3 — müşteri arama yok:** Kara liste dialog'u dışında müşteri listesini gezinmek için UI yok.

---

## 2. Mimari Etkisi

Mimari değişiklik yok. Yeni proje yok, yeni katman yok. Mevcut `StreamReportDialog`/`SettingsDialog` modal-dialog pattern'ini takip ediyoruz.

**Etkilenen dosyalar:**

| Katman | Dosya | Eylem |
|---|---|---|
| Schema | `LiveDeck.Core/Storage/Migrations/005_drop_legacy_customer_metrics.sql` | Yeni |
| Entity | `LiveDeck.Core/Customers/Customer.cs` | Modify (17 → 13 alan) |
| Repo (Customer) | `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs` | Modify (4 alan kaldır + `UpdateNotes` + `Search`) |
| Repo (Customer) — DTO | aynı dosya | Yeni record `CustomerLabelRow`, `CustomerGiveawayRow` |
| Repo (Label) | `LiveDeck.Core/Storage/Repositories/LabelRepository.cs` | Modify (`GetByCustomer`) |
| Repo (Giveaway) | `LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs` | Modify (`GetParticipationsByCustomer`) |
| Service | `LiveDeck.Core/Customers/CustomerService.cs` | Modify (`Insert` çağrısı 13 alan; TrustScore/Orders init kalkar) |
| ViewModel | `LiveDeck.App/ViewModels/CustomerDetailViewModel.cs` | Yeni |
| ViewModel | `LiveDeck.App/ViewModels/CustomerSearchViewModel.cs` | Yeni |
| View | `LiveDeck.App/Views/CustomerDetailDialog.xaml` + `.xaml.cs` | Yeni |
| View | `LiveDeck.App/Views/CustomerSearchDialog.xaml` + `.xaml.cs` | Yeni |
| AppHost | `LiveDeck.App/AppHost.cs` | Modify (DI kayıtları) |
| Erişim — chat + kuyruk | `LiveDeck.App/Views/MainShellView.xaml` | Modify (ContextMenu MenuItem) |
| Erişim — ana menü | `LiveDeck.App/Views/MainShellView.xaml` | Modify (⋮ → "Müşteriler") |
| Erişim — kara liste | `LiveDeck.App/Views/BlacklistDialog.xaml` | Modify (DataGrid button kolonu) |
| Erişim — kara liste VM | `LiveDeck.App/ViewModels/BlacklistViewModel.cs` | Modify (`OpenCustomerDetail` command) |
| MainShell VM | `LiveDeck.App/ViewModels/MainShellViewModel.cs` | Modify (3 yeni RelayCommand) |
| Tests | `LiveDeck.Tests/Storage/MigrationRunnerTests.cs` | Modify |
| Tests | `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs` | Modify (fixture + 2 yeni test) |
| Tests | `LiveDeck.Tests/Storage/LabelRepositoryTests.cs` | Extend |
| Tests | `LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs` | Extend |
| Tests — Customer fixture | `LiveDeck.Tests/Sales/GiveawayServiceTests.cs` + `GiveawayServicePreventRewinningCacheTests.cs` | Modify (Customer ctor 13 alan) |

---

## 3. Detaylar

### 3.1 Schema migration 005

```sql
-- 005_drop_legacy_customer_metrics.sql
-- Phase 3a: drop unused TrustScore + order-tracking columns. P1b's pivot to
-- label workflow eliminated the order-completion lifecycle; these columns
-- were never updated and made the entity heavier than it needs to be.

ALTER TABLE Customer DROP COLUMN TrustScore;
ALTER TABLE Customer DROP COLUMN TotalOrders;
ALTER TABLE Customer DROP COLUMN CompletedOrders;
ALTER TABLE Customer DROP COLUMN CancelledOrders;

UPDATE _meta SET SchemaVersion = 5 WHERE Id = 1;
```

`ALTER TABLE ... DROP COLUMN` SQLite ≥ 3.35 gerektirir. Microsoft.Data.Sqlite 9 paketi 3.45+ ile gelir; risk yok.

Migration runner version-gated olduğu için idempotent — ikinci `Run()` çağrısı no-op.

### 3.2 Customer entity

13 alanlı son hal:

```csharp
public sealed record Customer(
    string Id,
    string Platform,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    long FirstSeenAt,
    long LastSeenAt,
    bool IsBlacklisted,
    string? BlacklistReason,
    string? Notes,
    int TotalLabelsPrinted,
    decimal TotalAmount,
    long? BlacklistedAt);
```

`TotalLabelsPrinted` ve `TotalAmount` mevcut güncellenen alanlar (LabelService.MarkPrintedAndRecord güncellemesi). Schema'dan kalkmıyorlar.

### 3.3 Repo metod imzaları

**CustomerRepository:**

```csharp
/// <summary>Updates only the Notes column. Whitespace input normalizes to NULL.</summary>
public void UpdateNotes(string customerId, string? notes);

/// <summary>Case-insensitive substring search on Username. Returns up to <paramref name="limit"/> results,
/// ordered by LastSeenAt DESC.</summary>
public IReadOnlyList<Customer> Search(string usernameContains, int limit = 50);
```

**LabelRepository:**

```csharp
/// <summary>Returns labels for a customer, most recent first. Empty list if none.</summary>
public IReadOnlyList<CustomerLabelRow> GetByCustomer(string customerId);

public sealed record CustomerLabelRow(
    string Id, string SessionId, string MessageText, string? Code,
    decimal Price, long EnteredAt, bool IsPrinted);
```

**GiveawayRepository:**

```csharp
/// <summary>Returns the customer's giveaway participation history (joined with Giveaway), most recent first.
/// Includes cancelled giveaways for audit visibility — UI can show status.</summary>
public IReadOnlyList<CustomerGiveawayRow> GetParticipationsByCustomer(string customerId);

public sealed record CustomerGiveawayRow(
    string GiveawayId, string Keyword, long EnteredAt, bool IsWinner,
    long? GiveawayEndedAt, long? GiveawayCancelledAt);
```

UI bu üç durumu (kazandı / katıldı / iptal edildi) `IsWinner` + `GiveawayCancelledAt` üzerinden hesaplar.

### 3.4 CustomerDetailViewModel

```csharp
public sealed partial class CustomerDetailViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;
    private readonly LabelRepository _labels;
    private readonly GiveawayRepository _giveaways;
    private string? _customerId;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _platform = "";
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string _firstSeenLabel = "";
    [ObservableProperty] private string _lastSeenLabel  = "";
    [ObservableProperty] private int    _totalLabelsPrinted;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private bool   _isBlacklisted;
    [ObservableProperty] private string? _blacklistReason;
    [ObservableProperty] private string _blacklistedAtLabel = "";
    [ObservableProperty] private string _notesEdit = "";

    public ObservableCollection<CustomerLabelRow>    Labels    { get; } = new();
    public ObservableCollection<CustomerGiveawayRow> Giveaways { get; } = new();

    public CustomerDetailViewModel(
        CustomerRepository customers, LabelRepository labels, GiveawayRepository giveaways);

    /// <summary>Loads customer summary + label/giveaway history. Returns false if customer not found.</summary>
    public bool Load(string customerId);

    [RelayCommand] private void SaveNotes();
}
```

Tarih formatlama `TrFormats.DateTime(unixSeconds)` ile. `BlacklistedAtLabel` boş string'e set edilir kara listede değilse.

### 3.5 CustomerDetailDialog.xaml (yapı)

```
Window 720x600, dark theme, WindowStartupLocation=CenterOwner

Grid rows: Auto (özet) | * (TabControl) | Auto (kapat butonu)

Özet kartı: Grid 2 kolon
  └─ Sol: @username (büyük), platform, ilk/son görülme tarihleri
  └─ Sağ: 📦 N etiket · 💰 totalAmount TL · 🚫 kara liste durumu

TabControl 3 tab:
  ├─ "Etiket Geçmişi" → DataGrid bound to Labels
  │     Sütunlar: Tarih | Kod | Mesaj | Fiyat | Yazdırıldı
  ├─ "Çekiliş Geçmişi" → DataGrid bound to Giveaways
  │     Sütunlar: Tarih | Anahtar Kelime | Durum
  └─ "Notlar" → TextBox (multiline) bound to NotesEdit + Kaydet button

Boş list state'i: DataGrid altında küçük TextBlock ("Henüz etiket yok" / "Henüz çekilişe katılmadı"),
Visibility CountToVisibleConverter inverse.

Kapat butonu: IsCancel=True, IsDefault=True
```

`Status` sütunu için converter (`GiveawayParticipationStatusConverter`):
- `IsWinner=true` → "🏆 Kazandı"
- `GiveawayCancelledAt is not null` → "İptal edildi"
- aksi → "Katıldı"

### 3.6 CustomerSearchViewModel + Dialog

```csharp
public sealed partial class CustomerSearchViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;

    [ObservableProperty] private string _query = "";
    public ObservableCollection<Customer> Results { get; } = new();

    public CustomerSearchViewModel(CustomerRepository customers);

    partial void OnQueryChanged(string value)
    {
        Results.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var c in _customers.Search(value.Trim(), limit: 50))
            Results.Add(c);
    }
}
```

Synchronous arama (50 limit; in-memory SQLite, mikrosaniye seviyesi). Debounce yok.

Dialog:
```
Window 480x520
Grid: TextBox (search) | ListBox (results, double-click → açar CustomerDetailDialog) | Kapat
```

ListBox item template: `@username · platform · son görülme TrFormats.DateTime`.

### 3.7 4 erişim noktası

#### Chat ContextMenu (MainShellView.xaml)

Mevcut "Kara Listeye Al…" yanına:
```xml
<MenuItem Header="Müşteri Detayı"
          Command="{Binding PlacementTarget.DataContext.OpenCustomerDetailFromChatCommand,
                            RelativeSource={RelativeSource Self}}"
          CommandParameter="{Binding PlacementTarget.SelectedItem,
                                     RelativeSource={RelativeSource Self}}"/>
```

#### Kuyruk ContextMenu (MainShellView.xaml)

Aynı pattern; command: `OpenCustomerDetailFromQueueCommand`, parameter: seçili `LabelViewModel`.

#### Ana menü (⋮ → "Müşteriler")

```xml
<MenuItem Header="Müşteriler"
          Command="{Binding OpenCustomerSearchCommand}"/>
```

#### Kara liste dialog'u — DataGrid button kolonu

`BlacklistDialog`'da yeni `DataGridTemplateColumn`:
```xml
<DataGridTemplateColumn Header="" Width="40">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <Button Content="…" Padding="4,0"
                    Command="{Binding DataContext.OpenCustomerDetailCommand,
                                      RelativeSource={RelativeSource AncestorType=Window}}"
                    CommandParameter="{Binding CustomerId}"/>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

`BlacklistViewModel`'e `[RelayCommand] OpenCustomerDetail(string customerId)` eklenir; aynı `ShowDetail` helper'ını çağırır.

#### MainShellViewModel komutları

```csharp
[RelayCommand]
private void OpenCustomerDetailFromChat(ChatMessageViewModel? msg)
{
    if (msg is null) return;
    var customer = _customerRepo.FindByPlatformAndUsername(msg.Platform, msg.Username);
    if (customer is null)
    {
        MessageBox.Show("Bu kullanıcı henüz kayıtlı değil.", "Müşteri yok",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return;
    }
    ShowCustomerDetail(customer.Id);
}

[RelayCommand]
private void OpenCustomerDetailFromQueue(LabelViewModel? row)
{
    if (row is null) return;
    ShowCustomerDetail(row.CustomerId);   // already a real customer
}

[RelayCommand]
private void OpenCustomerSearch()
{
    var dlg = App.Host.Services.GetRequiredService<CustomerSearchDialog>();
    dlg.Owner = Application.Current?.MainWindow;
    dlg.ShowDialog();
}

private static void ShowCustomerDetail(string customerId)
{
    var dlg = App.Host.Services.GetRequiredService<CustomerDetailDialog>();
    dlg.Open(customerId);  // calls vm.Load + Owner + ShowDialog
}
```

`CustomerDetailDialog` ve `CustomerSearchDialog` AppHost'ta `AddTransient` olarak kayıtlı; her açılış taze instance.

### 3.8 AppHost değişiklikleri

```csharp
// Customer detail (Phase 3a)
services.AddTransient<CustomerDetailViewModel>();
services.AddTransient<CustomerDetailDialog>();
services.AddTransient<CustomerSearchViewModel>();
services.AddTransient<CustomerSearchDialog>();
```

---

## 4. Hata Yönetimi

| Senaryo | Davranış |
|---|---|
| Migration: SQLite < 3.35 | Exception; uygulama açılmaz; log'da görünür. (Pratikte olmayacak — Microsoft.Data.Sqlite 9 SQLite 3.45+ paketler.) |
| `LabelRepository.GetByCustomer` — bilinmeyen customerId | Boş liste döner |
| `GiveawayRepository.GetParticipationsByCustomer` — bilinmeyen customerId | Boş liste döner |
| `CustomerRepository.UpdateNotes` — bilinmeyen customerId | UPDATE 0 satır etkiler; sessiz no-op |
| `CustomerRepository.Search` — boş query | `OnQueryChanged` zaten temizler; metoda boş query gelmez (defansif: boş list döner) |
| `Search` — SQL injection | Dapper parametresi (`@q = "%" + input + "%"`); no-risk |
| Chat → Müşteri Detayı, kullanıcı henüz `GetOrCreate`'lenmemiş | MessageBox bilgi |
| Kuyruk → Müşteri Detayı | `LabelViewModel.CustomerId` zaten gerçek (etiket eklendiğinde Customer auto-create); fallback yok |
| Detail dialog `Load` null Customer | `Open` başarısız döner; caller MessageBox gösterir (chat path zaten guard'lıyor; arama path'inde sonuç listesinden geldiği için olmaz) |
| `SaveNotes` exception | MessageBox warning |
| Detay'da Customer hiç etiket yazdırmamış | Boş DataGrid + "Henüz etiket yok" mesajı |
| Detay'da Customer hiç çekilişe katılmamış | Boş DataGrid + "Henüz çekilişe katılmadı" mesajı |

---

## 5. Test Stratejisi

### 5.1 Yeni testler

| Test | Dosya |
|---|---|
| `Run_creates_all_tables_at_version_5_with_dropped_legacy_columns` | `MigrationRunnerTests.cs` (modify) |
| `UpdateNotes_sets_notes_or_normalizes_whitespace_to_null` | `CustomerRepositoryTests.cs` (extend) |
| `Search_returns_matching_customers_ordered_by_last_seen` | `CustomerRepositoryTests.cs` (extend) |
| `GetByCustomer_returns_labels_ordered_by_recent_for_only_that_customer` | `LabelRepositoryTests.cs` (extend) |
| `GetParticipationsByCustomer_returns_history_with_status_flags` | `GiveawayRepositoryTests.cs` (extend) |
| `GetParticipationsByCustomer_includes_cancelled_giveaways` | `GiveawayRepositoryTests.cs` (extend) |

### 5.2 Compile-time enforcement

`Customer` record positional alanları **arada** kaldırıyoruz (`TrustScore` 11. argümandı, `TotalLabelsPrinted/TotalAmount/BlacklistedAt` 15-16-17. argümanlardı). Bu hizalama bozulması her caller'da compile error verir. Hiçbir caller miss edilmesin diye istenen davranış. Etkilenen call siteleri:
- `CustomerRepositoryTests.cs` fixture (~5 yer)
- `GiveawayServiceTests.cs` fixture (1 yer)
- `GiveawayServicePreventRewinningCacheTests.cs` (Mock üzerinden değil; fixture içinde Customer ctor varsa)
- `GiveawayServiceTurkishKeywordTests.cs` (yok — Mock kullanmıyor)
- `CustomerService.cs` GetOrCreate içinde Customer ctor

Plan her birini açık açık göstermeli.

### 5.3 Manuel kabul testi

| # | Senaryo |
|---|---|
| 1 | Stream başlat, chat'e bir kullanıcı yaz, sağ tık → "Müşteri Detayı" → dialog açılır, özet doğru, etiket geçmişi boş, çekiliş geçmişi boş |
| 2 | O kullanıcıdan etiket yazdır, kuyrukta sağ tık → "Müşteri Detayı" → etiket geçmişinde 1 satır görünür |
| 3 | O kullanıcıyla bir çekilişe katıl ve kazandır → detay'da çekiliş geçmişinde "🏆 Kazandı" |
| 4 | Notlar tabına metin yaz → Kaydet → kapat → tekrar aç → metin korunur |
| 5 | ⋮ → Müşteriler → @user yaz → çift tık → detay açılır |
| 6 | Kara liste dialog'unda satır → "…" → detay açılır, kara liste sebebi+tarihi görünür |

### 5.4 Test scope dışı

- WPF DataGrid sıralama/filtreleme UX
- Search async/debounce (yapmıyoruz)
- Notes timestamp/history (sadece text)

---

## 6. YAGNI (yapılmayanlar)

- Customer foto upload / avatar override
- Notes timestamp/history (sadece tek `Notes` field)
- Müşteri export to Excel (Phase 5'e)
- Search async/debounce
- Detail'den doğrudan kara listeye al butonu (mevcut Add To Blacklist dialog'u zaten var, gerekirse ileride eklenir)
- Detail'de username/platform düzenleme (immutable identity)
- Müşteri silme UI'ı (FK cascade riskli, audit yararı yok)

---

## 7. Phase 3a Sonrası Açık Bırakılanlar

- **Phase 3b:** Klavye kısayolları, mini overlay panel, kuyruk toplu işlemleri
- **Phase 3c:** Stream Deck SDK
- **Phase 5:** Excel export, foto/avatar yönetimi
- **İleride:** Notes timestamp/history, müşteri silme (audit ile)

---

## 8. Kabul Kriterleri

- ✅ Migration 005 başarılı, schema version 5
- ✅ Customer entity 13 alan, hiçbir caller başvurmuyor (compile error → fix)
- ✅ ~89/89 test pass (83 baseline + 6 new)
- ✅ Build temiz, 0 warning, 0 error
- ✅ 4 erişim noktasından detay açılıyor (manuel smoke 6/6)
- ✅ Notes editleme ve persistence çalışıyor
- ✅ Faz 3d davranışları regrese olmamış
