# Phase 4g — Broadcaster→Customer WhatsApp Payment Request Design

**Date:** 2026-04-30
**Phase:** 4g (LiveDeck Phase 4 — Licensing System Epic, sub-phase 7/?)
**Status:** Spec — awaiting user review before plan
**Depends on:** Phase 4f (Intake Form) — extends `FormSubmission` and `IntakeForm` Razor page

---

## 1. Goal

Yayıncı (broadcaster), uygulama içinden son yayından alışveriş yapan müşterilere WhatsApp üzerinden hazır ödeme isteme mesajı gönderebilsin. Mesaj, broadcaster'ın yapılandırdığı şablonla pre-fill edilir; **WhatsApp'ta otomatik gönderme yapılmaz** — broadcaster "Gönder" butonuna manuel basar (anti-bot).

---

## 2. User Story

> Yayını bitirdim, son yayında alışveriş yapan müşterilerden ödeme almak istiyorum. CustomerSearchDialog'u açıp "Son yayından alışveriş yapanlar" filter'ını işaretlerim. Listede her müşterinin yanında 📱 ikonu var. Müşteriye tıklarım → WhatsApp Desktop açılır, mesaj zaten yazılı: "Merhaba Ali, 30 Nisan 2026 yayınımızdan toplam 245,50 TL ödemeniz bekleniyor. IBAN: TR12 ... Hesap Sahibi: ..." → Ben sadece Gönder'e basarım.

---

## 3. Architecture

### 3.1 İki giriş noktası

1. **CustomerSearchDialog** (primary)
   - Yeni checkbox: "Son yayından alışveriş yapanlar"
   - İşaretlendiğinde grid kaynağı `CustomerService.GetLastStreamShoppers()` olur
   - Her satırda WhatsApp 📱 buton kolonu

2. **StreamReportDialog** (extra feature)
   - Mevcut TopCustomers DataGrid'inde her satıra WhatsApp 📱 buton kolonu
   - Yayın bitiminde direkt rapor ekranından gönderme imkanı

### 3.2 Telefon toplama stratejisi (hibrit)

- **Customer.Phone** field'ı eklenir (Core migration 007, nullable)
- **Phase 4f IntakeForm**'a zorunlu Phone alanı eklenir (LicenseServer migration 008) → form müşterilerinin telefonu otomatik kaydolur ve broadcaster'ın Customer kaydına sync olur
- **Chat platform müşterileri** (Twitch/YouTube) için: `PhoneEntryDialog` inline popup → broadcaster numarayı bir kez girer, `Customer.Phone` güncellenir, WhatsApp açılır

### 3.3 Telefon formatı

- Standart depolama: **E.164** (`+905XXXXXXXXX`)
- TR otomatik prefix: kullanıcı `5XX...`, `05XX...`, `+90 5XX...` gibi yazsa da `PhoneNormalizer.NormalizeTr()` E.164'e çevirir
- Yurt dışı numara desteği YAGNI (sadece TR)

### 3.4 Mesaj şablonu

- Settings'te **configurable**, multiline textbox
- Placeholder substitution:
  - `{ad}` → `Customer.DisplayName ?? Customer.Username`
  - `{tutar}` → `TotalAmount` TR culture (`1.234,56`)
  - `{tarih}` → `session.EndedAt` `dd MMMM yyyy` (Türkçe ay adı)
  - `{iban}`, `{hesap_sahibi}`, `{papara}` → Settings'ten

### 3.5 wa.me deep-link

```
https://wa.me/{e164DigitsOnly}?text={Uri.EscapeDataString(message)}
```

`Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })` → OS default handler → WhatsApp Desktop varsa onu, yoksa web.whatsapp.com.

---

## 4. Data Model Changes

### 4.1 LiveDeck.Core migration 007

```sql
-- 007_CustomerPhone.sql
ALTER TABLE Customers ADD COLUMN Phone TEXT NULL;
```

```csharp
public sealed record Customer(
    string Id, string Platform, string Username, string? DisplayName,
    string? AvatarUrl, long FirstSeenAt, long LastSeenAt,
    bool IsBlacklisted, string? BlacklistReason, string? Notes,
    int TotalLabelsPrinted, decimal TotalAmount, long? BlacklistedAt,
    string? Address, string? Phone);  // Phase 4g
```

**Etki:** Tüm `new Customer(...)` çağrı yerleri güncellenmeli (Phase 4f Address ekleme deneyimi: ~9 yer).

### 4.2 LiveDeck.LicenseServer migration 008

```sql
-- 008_FormSubmissionPhone.sql
ALTER TABLE FormSubmissions ADD COLUMN Phone NVARCHAR(20) NULL;
```

```csharp
public class FormSubmission
{
    // ... mevcut
    public string? Phone { get; set; }  // Phase 4g — E.164
}
```

### 4.3 AppSettings (JSON, migration yok)

`%AppData%\LiveDeck\settings.json`:

```json
{
  "Payment": {
    "WhatsAppMessageTemplate": "Merhaba {ad}, {tarih} yayınımızdan toplam {tutar} TL ödemeniz bekleniyor.\n\nIBAN: {iban}\nHesap Sahibi: {hesap_sahibi}\nPapara: {papara}\n\nTeşekkürler!",
    "Iban": "",
    "AccountHolder": "",
    "Papara": ""
  }
}
```

`AppSettings` POCO'ya `PaymentSettings` nested class eklenir; ilk yüklemede null ise default değerlerle init.

---

## 5. New Components

### 5.1 LiveDeck.Core

#### `PhoneNormalizer` (static)
- `NormalizeTr(string? input) → string?` — E.164 veya null
- `IsValidTr(string? e164) → bool`

#### `WhatsAppMessageBuilder`
- `BuildMessage(string template, PaymentContext ctx) → string` — placeholder substitution
- `BuildWaMeLink(string e164Phone, string message) → string` — `https://wa.me/{digits}?text={escaped}`

#### `PaymentContext` (record)
```csharp
public sealed record PaymentContext(
    string DisplayName, decimal TotalAmount, DateTime StreamDate,
    string? Iban, string? AccountHolder, string? Papara);
```

#### `IUrlLauncher` (interface)
- Production: `ProcessUrlLauncher : IUrlLauncher` — `Process.Start(new ProcessStartInfo)`
- Tests: `FakeUrlLauncher` — kayıt tutar

#### `LabelRepository.GetLatestEndedSessionId() → string?`
```sql
SELECT Id FROM Sessions WHERE EndedAt IS NOT NULL ORDER BY EndedAt DESC LIMIT 1;
```

#### `CustomerRepository.UpdatePhone(string customerId, string e164Phone)`
```sql
UPDATE Customers SET Phone = @Phone WHERE Id = @Id;
```

#### `CustomerService.GetLastStreamShoppers() → IReadOnlyList<Customer>`
- `_labels.GetLatestEndedSessionId()` → null ise empty
- Aksi halde `_labels.GetTopCustomersBySession(sessionId, int.MaxValue)` → her `TopCustomer` için tam `Customer` hidrate (Phone field'ı için)

### 5.2 LiveDeck.App

#### `PaymentRequestService`
```csharp
public sealed class PaymentRequestService
{
    public PaymentRequestResult OpenWhatsApp(Customer customer, decimal totalAmount, DateTime streamDate);
}

public enum PaymentRequestResult { Opened, PhoneRequired, LaunchFailed }
```

- `customer.Phone` null/invalid → `PhoneRequired`
- Settings yükle → template substitute → wa.me link build → `_launcher.Launch(url)`
- Exception → `LaunchFailed` + log

#### `PhoneEntryDialog` (Window) + `PhoneEntryDialogViewModel`
- `PhoneInput` observable
- `ValidationError` observable
- `SaveCommand`: `PhoneNormalizer.NormalizeTr` → null ise hata, geçerliyse `_customers.UpdatePhone(customerId, normalized)` + `DialogResult=true`

### 5.3 LicenseServer (Phase 4f genişlemesi)

#### `IntakeFormModel` (Razor PageModel)
- Yeni `[Required] [StringLength(20)] public string Phone` property
- Server-side `PhoneNormalizer` (Core'dan shared veya copy) ile normalize
- Invalid → `ModelState.AddModelError("Phone", "Geçersiz telefon numarası")`
- Geçerli → `FormSubmission.Phone = normalized`

#### `IntakeForm.cshtml`
Address field'ının altına Phone input bloğu (Section 4.2 UI Layer'a bkz).

---

## 6. UI Layer

### 6.1 PhoneEntryDialog.xaml
- Modal Window, Owner=MainShell, MinWidth=400
- Başlık: "WhatsApp Numarası Gerekli"
- Açıklama: "Bu müşterinin WhatsApp numarası kayıtlı değil. Lütfen girin:"
- TextBox `Text="{Binding PhoneInput}"`, placeholder "5XX XXX XX XX", MaxLength=20
- Validation error TextBlock (kırmızı, Visibility binding)
- Hint: "+90 otomatik eklenecek"
- Buttons: **Kaydet ve Aç** (primary, IsDefault=True) / **İptal** (IsCancel=True)

### 6.2 CustomerSearchDialog.xaml (genişler)
Mevcut Platform filter'ın yanına:
```xaml
<CheckBox Content="Son yayından alışveriş yapanlar"
          IsChecked="{Binding LastStreamShoppersOnly}"
          Margin="12,0,0,0" VerticalAlignment="Center"/>
```

DataGrid'e yeni kolon (en sağa):
```xaml
<DataGridTemplateColumn Header="" Width="60">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate>
      <Button Content="📱" ToolTip="WhatsApp'tan ödeme iste"
              Command="{Binding DataContext.OpenWhatsAppCommand,
                       RelativeSource={RelativeSource AncestorType=Window}}"
              CommandParameter="{Binding}"
              Background="#25D366" Foreground="White" Padding="6,2" BorderThickness="0"/>
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

`LastStreamShoppersOnly` aktifken `Amount` kolonu görünür yapılır (DataTrigger ile).

Filter aktif + son yayın yok ise InfoBar: "Henüz tamamlanmış yayın yok."

### 6.3 StreamReportDialog.xaml (genişler)
TopCustomers DataGrid'inde aynı pattern WhatsApp kolonu (binding `OpenWhatsAppCommand` StreamReportViewModel'a).

### 6.4 SettingsView.xaml (genişler)
Yeni "WhatsApp Ödeme İsteme" GroupBox:
- Mesaj Şablonu (multiline TextBox, MinHeight=120)
- Placeholder hint: `{ad} {tutar} {tarih} {iban} {hesap_sahibi} {papara}`
- IBAN TextBox
- Hesap Sahibi TextBox
- Papara No TextBox (opsiyonel)

### 6.5 IntakeForm.cshtml (Phase 4f, genişler)
Address field'ının altına:
```html
<div class="mb-3">
  <label class="form-label">WhatsApp Numarası <span class="text-danger">*</span></label>
  <input type="tel" name="Phone" class="form-control"
         placeholder="5XX XXX XX XX" required maxlength="20"
         value="@Model.Form.Phone"/>
  <div class="form-text">+90 otomatik eklenecek</div>
  <span asp-validation-for="Form.Phone" class="text-danger"></span>
</div>
```

---

## 7. ViewModel Changes

### 7.1 CustomerSearchViewModel (mevcut, genişler)
- `[ObservableProperty] bool _lastStreamShoppersOnly`
- `partial void OnLastStreamShoppersOnlyChanged(bool value)` → `RefreshSearch()`
- `ApplySearch(string query)` içinde:
  - `LastStreamShoppersOnly=true` ise kaynak `_customerService.GetLastStreamShoppers()`, query ile in-memory filter
  - Aksi halde mevcut akış
- `[RelayCommand] async Task OpenWhatsAppAsync(Customer customer)`:
  - Last stream session bul (cached)
  - `_paymentService.OpenWhatsApp(customer, customer.TotalAmount, sessionDate)` →
    - `PhoneRequired` → `_dialogService.ShowPhoneEntryDialog(customer)` → success ise tekrar dene
    - `LaunchFailed` → MessageBox "WhatsApp açılamadı"
    - `Opened` → no-op

### 7.2 StreamReportViewModel (mevcut, genişler)
- `[RelayCommand] async Task OpenWhatsAppAsync(TopCustomer topCustomer)`:
  - `_customers.GetByPlatformUsername(topCustomer.Platform, topCustomer.Username)` ile hidrate
  - `_paymentService.OpenWhatsApp(customer, topCustomer.TotalAmount, _currentSessionDate)`
  - PhoneRequired → PhoneEntryDialog → retry

### 7.3 PhoneEntryDialogViewModel (yeni)
- `[ObservableProperty] string _phoneInput = ""`
- `[ObservableProperty] string? _validationError`
- `[RelayCommand] void Save()`:
  - `PhoneNormalizer.NormalizeTr(PhoneInput)` → null ise `ValidationError = "Geçersiz telefon numarası"`
  - Geçerliyse `_customers.UpdatePhone(_customerId, normalized)`, `_window.DialogResult = true`, `_window.Close()`

### 7.4 SettingsViewModel (mevcut, genişler)
- `[ObservableProperty] string _paymentTemplate`
- `[ObservableProperty] string _iban`
- `[ObservableProperty] string _accountHolder`
- `[ObservableProperty] string _papara`
- Save/Load AppSettings.Payment'a serialize/deserialize

---

## 8. Error Handling & Edge Cases

| Senaryo | Davranış |
|---------|----------|
| Phone null + dialog iptal | WhatsApp açılmaz, sessizce kapan |
| Phone invalid (NormalizeTr=null) | Inline error: "Geçersiz telefon numarası" |
| Settings template boş | Hardcoded default template fallback |
| No ended session, filter aktif | Grid boş + InfoBar: "Henüz tamamlanmış yayın yok." |
| WhatsApp Desktop yok | OS default handler → web.whatsapp.com |
| Process.Start exception | try/catch → log + MessageBox "WhatsApp açılamadı" |
| Mesaj çok uzun (>2000 char) | wa.me silently truncate edebilir; normal kullanımda altında |
| TotalAmount 0 | Buton aktif kalır (broadcaster yine de mesaj atabilir, "0,00 TL" yazılır) |
| TR culture eksikse | `new CultureInfo("tr-TR")` explicit (server-independent) |

---

## 9. Test Strategy

### 9.1 Unit Tests (LiveDeck.Tests)

| Test sınıfı | Yeni test | Kapsam |
|------------|-----------|--------|
| `PhoneNormalizerTests` | 8 | E.164 conversion, prefix variants, invalid input |
| `WhatsAppMessageBuilderTests` | 5 | Placeholder substitution, TR culture, EscapeDataString, newline encoding |
| `PaymentRequestServiceTests` | 4 | PhoneRequired/Opened/LaunchFailed paths, FakeUrlLauncher invocation |
| `LabelRepositoryTests` (genişler) | 2 | `GetLatestEndedSessionId` ordering, no-session→null |
| `CustomerRepositoryTests` (genişler) | 2 | `UpdatePhone` E.164 stored, idempotent |
| `CustomerServiceTests` (genişler) | 3 | `GetLastStreamShoppers` no session/with session, hidrate |
| `PhoneEntryDialogViewModelTests` | 4 | Invalid→error, valid→repo update, save command, cancel |
| `CustomerSearchViewModelTests` (genişler) | 3 | LastStreamShoppersOnly toggle, OpenWhatsApp PhoneRequired→dialog |
| `StreamReportViewModelTests` (genişler) | 2 | OpenWhatsApp hidrate + service call |
| `SettingsViewModelTests` (genişler) | 2 | Payment fields persist, defaults on first load |
| `MigrationTests` (genişler) | 2 | 007 column exists, idempotent |

**LiveDeck.Tests yeni test toplamı:** ~37

### 9.2 Integration Tests (LiveDeck.LicenseServer.Tests)

| Test sınıfı | Yeni test |
|------------|-----------|
| `IntakeFormPhoneTests` | 4 (required, normalize TR, invalid format error, persisted) |
| Phase 4f mevcut form testleri | Regression: tüm POST testlerine `Phone` parametresi eklenmesi |

**LicenseServer.Tests yeni test toplamı:** ~4 + regression updates

**Toplam tahmin:** 430 → ~470 tests, hepsi yeşil.

### 9.3 Test Doubles
- `IUrlLauncher` mock (`FakeUrlLauncher` — `LaunchedUrls` list)
- `IDialogService.ShowPhoneEntryDialog(Customer)` mock — test'te direct sequencing
- DB: Phase 4f pattern — `InMemorySqlite()` shared-memory connection

### 9.4 Test count discipline
- xUnit `parallelizeAssembly=false`, `parallelizeTestCollections=false` (Phase 4f deneyiminden)
- Counted with `dotnet test --logger "console;verbosity=detailed"` aggregation

---

## 10. Out of Scope (YAGNI)

- Yurt dışı telefon numarası (sadece TR +90)
- Toplu mesaj (bulk send) — anti-bot riski
- Mesaj geçmişi/log — broadcaster WhatsApp'ta zaten görür
- Otomatik gönderme — tasarım gereği yasak
- Ödeme alındı tracking — Phase 5 (Stripe/PayTR webhook) kapsamında
- WhatsApp Business API — kişisel hesap yeterli
- SMS fallback — YAGNI

---

## 11. File Manifest

**Yeni dosyalar:**
- `LiveDeck.Core/Data/Migrations/007_CustomerPhone.sql`
- `LiveDeck.Core/Services/PhoneNormalizer.cs`
- `LiveDeck.Core/Services/WhatsAppMessageBuilder.cs`
- `LiveDeck.Core/Services/PaymentContext.cs`
- `LiveDeck.Core/Services/IUrlLauncher.cs` + `ProcessUrlLauncher.cs`
- `LiveDeck.App/Services/PaymentRequestService.cs`
- `LiveDeck.App/Views/PhoneEntryDialog.xaml` + `.xaml.cs`
- `LiveDeck.App/ViewModels/PhoneEntryDialogViewModel.cs`
- `LiveDeck.LicenseServer/Migrations/008_FormSubmissionPhone.sql` (+ EF migration)
- `LiveDeck.Tests/Services/PhoneNormalizerTests.cs`
- `LiveDeck.Tests/Services/WhatsAppMessageBuilderTests.cs`
- `LiveDeck.Tests/Services/PaymentRequestServiceTests.cs`
- `LiveDeck.Tests/ViewModels/PhoneEntryDialogViewModelTests.cs`
- `LiveDeck.Tests/Fakes/FakeUrlLauncher.cs`
- `LiveDeck.LicenseServer.Tests/Pages/IntakeFormPhoneTests.cs`

**Değişen dosyalar:**
- `LiveDeck.Core/Models/Customer.cs` — Phone field
- `LiveDeck.Core/Data/CustomerRepository.cs` — Phone column read/write, `UpdatePhone`
- `LiveDeck.Core/Data/LabelRepository.cs` — `GetLatestEndedSessionId`
- `LiveDeck.Core/Services/CustomerService.cs` — `GetLastStreamShoppers`
- `LiveDeck.Core/Models/AppSettings.cs` — `PaymentSettings` nested class
- `LiveDeck.App/ViewModels/CustomerSearchViewModel.cs` — filter + WhatsApp command
- `LiveDeck.App/ViewModels/StreamReportViewModel.cs` — WhatsApp command
- `LiveDeck.App/ViewModels/SettingsViewModel.cs` — payment fields
- `LiveDeck.App/Views/CustomerSearchDialog.xaml` — checkbox + WhatsApp column
- `LiveDeck.App/Views/StreamReportDialog.xaml` — WhatsApp column
- `LiveDeck.App/Views/SettingsView.xaml` — payment GroupBox
- `LiveDeck.App/App.xaml.cs` — DI registrations
- `LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml` + `.cshtml.cs` — Phone field
- `LiveDeck.LicenseServer/Data/Entities/FormSubmission.cs` — Phone property
- 9 test/seed dosyasında `new Customer(...)` çağrısına `Phone: null` ekleme (Phase 4f Address pattern)
- Phase 4f mevcut form testlerinde POST'lara `Phone` parametresi eklenmesi

---

## 12. Migration & Rollout

1. Migration 007 (Core) idempotent — `IF NOT EXISTS` semantics
2. Migration 008 (LicenseServer) EF Core add-migration
3. AppSettings ilk yüklemede `PaymentSettings` null ise default ile seed
4. Phase 4f form deploy edilince frontend Phone field zorunlu — geriye uyumluluk: mevcut FormSubmission kayıtlarında Phone null
5. Customer.Phone backfill yok — broadcaster manuel olarak doldurur (PhoneEntryDialog veya CustomerDetailDialog)

---

## 13. Future Considerations (Phase 5+)

- Phase 5 ödeme entegrasyonu (Stripe/PayTR webhook) — bu Phase 4g manuel WhatsApp akışını otomatize eder ya da onunla yan yana yaşar
- Phase 6 customer 2FA — telefon numarası SMS doğrulama için yeniden kullanılır
- Phase 7 çoklu broadcaster (multi-tenant) — `PaymentSettings` her broadcaster için ayrı

---

**End of Phase 4g Spec.**
