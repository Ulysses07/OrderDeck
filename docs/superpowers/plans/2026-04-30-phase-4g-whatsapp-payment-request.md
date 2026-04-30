# Phase 4g — Broadcaster→Customer WhatsApp Payment Request Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Yayıncı LiveDeck.App içinden son yayından alışveriş yapan müşterilere WhatsApp deep-link ile pre-filled ödeme isteme mesajı gönderebilsin (anti-bot: göndere kullanıcı manuel basar).

**Architecture:** `LiveDeck.Core`'a `Phone` field (Customer migration 007) + 3 yeni servis (`PhoneNormalizer`, `WhatsAppMessageBuilder`, `IUrlLauncher`). `LiveDeck.App`'a `PaymentRequestService` + `PhoneEntryDialog` + `CustomerSearchDialog`/`StreamReportDialog`/`SettingsDialog` genişlemeleri. `LiveDeck.LicenseServer` Phase 4f form'una zorunlu Phone alanı + EF migration 008. wa.me deep-link `Process.Start` ile OS handler'ına devredilir.

**Tech Stack:** .NET 10 / WPF + CommunityToolkit.Mvvm / SQLite + Dapper / ASP.NET Core 10 Razor Pages / EF Core 9 / `Process.Start` + `Uri.EscapeDataString` / xUnit + FluentAssertions + AngleSharp.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-4g state:** Phase 4f master `b28c54a` (Phase 4g spec commit). 430/430 test, 0 build errors.

**Spec reference:** `docs/superpowers/specs/2026-04-30-phase-4g-whatsapp-payment-request-design.md`

---

## Task Index

**Core domain (1-2):** Customer.Phone migration 007 + record + repo + 9 call sites · CustomerRepository.UpdatePhone
**Core helpers (3-5):** PhoneNormalizer · WhatsAppMessageBuilder + PaymentContext · IUrlLauncher + ProcessUrlLauncher + FakeUrlLauncher
**Core data extensions (6-7):** SessionRepository.GetLatestEnded · CustomerService.GetLastStreamShoppers
**Core settings (8):** AppSettings.PaymentSettings nested class
**App service (9):** PaymentRequestService
**App ViewModels (10-13):** PhoneEntryDialogViewModel · SettingsViewModel payment fields · CustomerSearchViewModel filter+command · StreamReportViewModel command
**App Views (14-17):** PhoneEntryDialog · SettingsDialog GroupBox · CustomerSearchDialog checkbox+column · StreamReportDialog column
**Wiring (18):** App.xaml.cs DI + IntakeFormSyncService Phone propagation
**LicenseServer (19-20):** EF migration 008 (FormSubmission.Phone) + IntakeForm.cshtml Phone field · Phase 4f regression test updates
**Final (21):** Verification + manual smoke

**Toplam test hedefi:** 430 baseline → ~470 (+40 yeni).

---

### Task 1: Customer.Phone migration 007 + record + repository

**Files:**
- Create: `LiveDeck.Core/Storage/Migrations/007_customer_phone.sql`
- Modify: `LiveDeck.Core/Customers/Customer.cs`
- Modify: `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`
- Modify: `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`
- Modify: `LiveDeck.Tests/Storage/MigrationTests.cs` (or wherever schema migration tests live — `MigrationRunnerTests`)

**Context:** Phase 4f Address pattern aynen tekrarlanıyor — yeni nullable `Phone` kolonu, idempotent migration, schema version 7. Customer record'a `string? Phone` parametresi en sona eklenir. Repository'nin `Insert`, `Map`, `Row`, `UpsertFromIntakeForm` metodları + Phase 4f `UpsertFromIntakeForm` imzası `phone` parametresi alır. Migration runner şu an migration dosyalarını otomatik discover ediyor — yeni dosya eklemek yeterli.

- [ ] **Step 1: Migration dosyası oluştur**

`LiveDeck.Core/Storage/Migrations/007_customer_phone.sql`:

```sql
-- Phase 4g: WhatsApp phone for payment requests (E.164 format)
ALTER TABLE Customer ADD COLUMN Phone TEXT;

UPDATE _meta SET SchemaVersion = 7 WHERE Id = 1;
```

Csproj `EmbeddedResource` glob mevcut migration'ları otomatik dahil ediyor — manual entry gerekmez (Phase 4f'de doğrulandı).

- [ ] **Step 2: Migration smoke test yaz (FAIL beklenir)**

`LiveDeck.Tests/Storage/MigrationRunnerTests.cs` dosyasını aç. Mevcut `Run_AppliesAllMigrations_FromZero` benzeri testin yanına ekle:

```csharp
[Fact]
public void Run_AppliesMigration007_AddsPhoneColumn()
{
    using var db = TestDb.Create();
    var conn = db.Open();

    var cols = conn.Query<string>("PRAGMA table_info(Customer)")
        .ToList();

    // PRAGMA table_info dönüşü tek string değil — column metadata projection.
    // Aşağıdaki query'yi kullan:
    var hasPhone = conn.QueryFirstOrDefault<long>(
        "SELECT COUNT(*) FROM pragma_table_info('Customer') WHERE name = 'Phone'");
    hasPhone.Should().Be(1);

    var version = conn.QueryFirstOrDefault<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
    version.Should().BeGreaterThanOrEqualTo(7);
}
```

`TestDb.Create()` mevcut helper — `MigrationRunner` çalıştırarak in-memory SQLite döndürür.

- [ ] **Step 3: Test FAIL doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~Run_AppliesMigration007"
```

Beklenen: FAIL (`hasPhone.Should().Be(1)` → 0 actual, çünkü migration henüz yok).

- [ ] **Step 4: Customer record Phone parametresi ekle**

`LiveDeck.Core/Customers/Customer.cs`:

```csharp
namespace LiveDeck.Core.Customers;

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
    long? BlacklistedAt,
    string? Address,
    string? Phone);   // Phase 4g
```

- [ ] **Step 5: CustomerRepository — Insert, Map, Row, UpsertFromIntakeForm güncelle**

`LiveDeck.Core/Storage/Repositories/CustomerRepository.cs` içinde dört yer:

**5a. `Insert` metodu** — SQL ve anonymous object'e Phone ekle:

```csharp
public void Insert(Customer c)
{
    using var conn = _factory.Open();
    conn.Execute(
        @"INSERT INTO Customer
          (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
           IsBlacklisted, BlacklistReason, Notes,
           TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone)
          VALUES
          (@Id, @Platform, @Username, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
           @IsBlacklisted, @BlacklistReason, @Notes,
           @TotalLabelsPrinted, @TotalAmount, @BlacklistedAt, @Address, @Phone)",
        new
        {
            c.Id, c.Platform, c.Username, c.DisplayName, c.AvatarUrl,
            c.FirstSeenAt, c.LastSeenAt,
            IsBlacklisted = c.IsBlacklisted ? 1 : 0,
            c.BlacklistReason, c.Notes,
            c.TotalLabelsPrinted, c.TotalAmount, c.BlacklistedAt, c.Address, c.Phone
        });
}
```

**5b. `Map` metodu** — yeni parametre:

```csharp
private static Customer Map(Row r) => new(
    r.Id, r.Platform, r.Username, r.DisplayName, r.AvatarUrl,
    r.FirstSeenAt, r.LastSeenAt,
    r.IsBlacklisted == 1, r.BlacklistReason, r.Notes,
    r.TotalLabelsPrinted, r.TotalAmount, r.BlacklistedAt, r.Address, r.Phone);
```

**5c. `Row` private class** — Phone property:

```csharp
private sealed class Row
{
    public string Id { get; init; } = "";
    public string Platform { get; init; } = "";
    public string Username { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
    public long FirstSeenAt { get; init; }
    public long LastSeenAt { get; init; }
    public int IsBlacklisted { get; init; }
    public string? BlacklistReason { get; init; }
    public string? Notes { get; init; }
    public int TotalLabelsPrinted { get; init; }
    public decimal TotalAmount { get; init; }
    public long? BlacklistedAt { get; init; }
    public string? Address { get; init; }
    public string? Phone { get; init; }
}
```

**5d. `UpsertFromIntakeForm` metodu** — `phone` parametresi en sona eklenir:

```csharp
public Customer UpsertFromIntakeForm(string username, string fullName, string address, string? phone, long nowUnix)
{
    const string platform = "form";
    using var conn = _factory.Open();

    var existing = conn.QueryFirstOrDefault<Row>(@"
        SELECT Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
               IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
               BlacklistedAt, Address, Phone
        FROM Customer
        WHERE Platform = @platform AND Username = @username",
        new { platform, username });

    if (existing is not null)
    {
        conn.Execute(@"
            UPDATE Customer
            SET DisplayName = @fullName,
                Address = @address,
                Phone = @phone,
                LastSeenAt = @nowUnix
            WHERE Id = @id",
            new { fullName, address, phone, nowUnix, id = existing.Id });
        var updated = Map(existing);
        return updated with { DisplayName = fullName, Address = address, Phone = phone, LastSeenAt = nowUnix };
    }

    var id = Guid.NewGuid().ToString("N");
    conn.Execute(@"
        INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                              IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                              BlacklistedAt, Address, Phone)
        VALUES (@id, @platform, @username, @fullName, NULL, @nowUnix, @nowUnix,
                0, NULL, NULL, 0, 0, NULL, @address, @phone)",
        new { id, platform, username, fullName, nowUnix, address, phone });

    return new Customer(id, platform, username, fullName, null, nowUnix, nowUnix,
        false, null, null, 0, 0m, null, address, phone);
}
```

- [ ] **Step 6: CustomerService.cs `new Customer(...)` çağrısını güncelle**

`LiveDeck.Core/Customers/CustomerService.cs` dosyasını aç, tüm `new Customer(...)` ifadelerini bul (genellikle 1 tane: `EnsureExists` veya benzer factory'de). Her constructor çağrısının sonuna `Phone: null` ekle:

```csharp
return new Customer(
    id, platform, username, displayName, avatarUrl,
    nowUnix, nowUnix, false, null, null, 0, 0m, null,
    Address: null,
    Phone: null);   // Phase 4g
```

(Eğer kod positional argument kullanıyorsa, sondaki `null`'a `, null` ekle.)

- [ ] **Step 7: Test dosyalarındaki `new Customer(...)` çağrılarını güncelle**

Şu dosyalarda her `new Customer(...)` constructor'unun sonuna `null` (Phone) ekle:

- `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`
- `LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs`
- `LiveDeck.Tests/Storage/LabelRepositoryTests.cs`
- `LiveDeck.Tests/Sales/GiveawayServiceTests.cs`

Her test fixture customer'ında pattern:

```csharp
var c = new Customer(
    "id1", "twitch", "alice", "Alice", null,
    1000, 1000, false, null, null,
    0, 0m, null,
    null,    // Address (Phase 4f)
    null);   // Phone (Phase 4g)
```

- [ ] **Step 8: Phase 4f IntakeFormSyncService Phone propagation update**

`LiveDeck.App/Services/IntakeFormSyncService.cs` (veya benzer Phase 4f sync dosyası) içinde `UpsertFromIntakeForm` çağrısını bul. Yeni signature `(username, fullName, address, phone, nowUnix)` olduğu için çağrıya phone parametresini ekle:

```csharp
_customers.UpsertFromIntakeForm(
    submission.Username,
    submission.FullName,
    submission.Address,
    submission.Phone,    // Phase 4g — DTO'ya bu adımda Phone eklenecek (Task 19'da)
    nowUnix);
```

**Not:** `IntakeFormSubmissionDto.Phone` alanı henüz Task 19'da eklenecek. Şimdilik `null` ile çağır:

```csharp
_customers.UpsertFromIntakeForm(
    submission.Username,
    submission.FullName,
    submission.Address,
    null,    // Phase 4g — Task 19'da DTO'ya Phone eklenince burası submission.Phone olur
    nowUnix);
```

- [ ] **Step 9: Yeni `UpdatePhone` metodu yaz (FAIL test ile)**

`LiveDeck.Tests/Storage/CustomerRepositoryTests.cs` sonuna:

```csharp
[Fact]
public void UpdatePhone_PersistsE164ValueAndCanBeReadBack()
{
    using var db = TestDb.Create();
    var repo = new CustomerRepository(db);
    var c = new Customer("id1", "twitch", "alice", "Alice", null,
        1000, 1000, false, null, null, 0, 0m, null, null, null);
    repo.Insert(c);

    repo.UpdatePhone("id1", "+905551234567");

    var loaded = repo.GetById("id1");
    loaded!.Phone.Should().Be("+905551234567");
}

[Fact]
public void UpdatePhone_OnNonExistentId_DoesNotThrow()
{
    using var db = TestDb.Create();
    var repo = new CustomerRepository(db);
    Action act = () => repo.UpdatePhone("nonexistent-id", "+905551234567");
    act.Should().NotThrow();
}
```

- [ ] **Step 10: Test FAIL doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~UpdatePhone"
```

Beklenen: FAIL ("'CustomerRepository' does not contain a definition for 'UpdatePhone'").

- [ ] **Step 11: `UpdatePhone` metodunu ekle**

`LiveDeck.Core/Storage/Repositories/CustomerRepository.cs` içinde `UpdateNotes` metodunun yanına ekle:

```csharp
/// <summary>Phase 4g: WhatsApp E.164 telefonu güncelle. Geçersiz id no-op.</summary>
public void UpdatePhone(string customerId, string e164Phone)
{
    using var conn = _factory.Open();
    conn.Execute(
        "UPDATE Customer SET Phone=@phone WHERE Id=@id",
        new { phone = e164Phone, id = customerId });
}
```

- [ ] **Step 12: Tüm Core testleri çalıştır**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj
```

Beklenen: tüm testler PASS, hiçbir derleme hatası yok. Mevcut 130 LiveDeck.Tests + 4 yeni test = ~134.

- [ ] **Step 13: Commit**

```bash
git add LiveDeck.Core/Storage/Migrations/007_customer_phone.sql \
        LiveDeck.Core/Customers/Customer.cs \
        LiveDeck.Core/Customers/CustomerService.cs \
        LiveDeck.Core/Storage/Repositories/CustomerRepository.cs \
        LiveDeck.App/Services/IntakeFormSyncService.cs \
        LiveDeck.Tests/Storage/CustomerRepositoryTests.cs \
        LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs \
        LiveDeck.Tests/Storage/LabelRepositoryTests.cs \
        LiveDeck.Tests/Storage/MigrationRunnerTests.cs \
        LiveDeck.Tests/Sales/GiveawayServiceTests.cs

git commit -m "feat(core): Phase 4g — Customer.Phone field + migration 007 + UpdatePhone

- Migration 007: add Phone TEXT NULL column to Customer
- Customer record: + Phone parameter (positional last)
- CustomerRepository: Insert/Map/Row/UpsertFromIntakeForm threaded Phone
- CustomerRepository.UpdatePhone(id, e164) new method
- 9 call sites updated (CustomerService + 4 test files)
- IntakeFormSyncService: pass-through null for Phone (Task 19 will wire)"
```

---

### Task 2: PhoneNormalizer (Core static helper)

**Files:**
- Create: `LiveDeck.Core/Customers/PhoneNormalizer.cs`
- Create: `LiveDeck.Tests/Customers/PhoneNormalizerTests.cs`

**Context:** Pure function. TR otomatik +90 prefix uygular: `5XX...` (10), `05XX...` (11), `+90 5XX...`, `+905XX...` (12) hepsi `+905XXXXXXXXX` (E.164, 13 char) çıkarır. Boşluk/dash/parantez stripped.

- [ ] **Step 1: Test dosyası oluştur (FAIL beklenir)**

`LiveDeck.Tests/Customers/PhoneNormalizerTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Customers;
using Xunit;

namespace LiveDeck.Tests.Customers;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("5551234567", "+905551234567")]            // 10 digit, no prefix
    [InlineData("05551234567", "+905551234567")]           // 11 digit, leading 0
    [InlineData("905551234567", "+905551234567")]          // 12 digit, no plus
    [InlineData("+905551234567", "+905551234567")]         // already E.164
    [InlineData("+90 555 123 45 67", "+905551234567")]     // spaces
    [InlineData("0 555 123-45-67", "+905551234567")]       // mixed spacing
    [InlineData("(0555) 123 45 67", "+905551234567")]      // parens
    public void NormalizeTr_AcceptsCommonFormats(string input, string expected)
    {
        PhoneNormalizer.NormalizeTr(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("123")]            // too short
    [InlineData("12345678901234")] // too long
    [InlineData("+12025551234")]   // non-TR country code
    public void NormalizeTr_RejectsInvalidInput(string? input)
    {
        PhoneNormalizer.NormalizeTr(input).Should().BeNull();
    }

    [Theory]
    [InlineData("+905551234567", true)]
    [InlineData("+9055512345670", false)]   // 14 chars
    [InlineData("+9055512345", false)]      // 12 chars
    [InlineData("+15551234567", false)]     // not TR
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsValidTr_ChecksE164TrFormat(string? input, bool expected)
    {
        PhoneNormalizer.IsValidTr(input).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Test FAIL doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~PhoneNormalizer"
```

Beklenen: FAIL (`PhoneNormalizer` tipi yok).

- [ ] **Step 3: `PhoneNormalizer` implementasyonu**

`LiveDeck.Core/Customers/PhoneNormalizer.cs`:

```csharp
using System.Linq;

namespace LiveDeck.Core.Customers;

/// <summary>
/// Phase 4g: TR mobil telefon numaralarını E.164 (+90...) formatına normalize eder.
/// Pure function — no side effects.
/// </summary>
public static class PhoneNormalizer
{
    /// <summary>
    /// "5551234567" / "05551234567" / "+90 555 123 45 67" → "+905551234567".
    /// Geçersiz/null/empty/yurt-dışı → null.
    /// </summary>
    public static string? NormalizeTr(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var digits = new string(input.Where(char.IsDigit).ToArray());

        // 12 digits starting with 90 → already has TR prefix
        if (digits.Length == 12 && digits.StartsWith("90"))
            return "+" + digits;

        // 11 digits starting with 0 → drop leading 0, prepend +90
        if (digits.Length == 11 && digits.StartsWith("0"))
            return "+90" + digits.Substring(1);

        // 10 digits → prepend +90
        if (digits.Length == 10)
            return "+90" + digits;

        return null;
    }

    /// <summary>E.164 TR format kontrolü: "+90" + 10 digit (toplam 13 karakter).</summary>
    public static bool IsValidTr(string? e164)
        => !string.IsNullOrEmpty(e164)
           && e164.StartsWith("+90")
           && e164.Length == 13
           && e164.Substring(1).All(char.IsDigit);
}
```

- [ ] **Step 4: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~PhoneNormalizer"
```

Beklenen: 18 test PASS (7 NormalizeTr_AcceptsCommonFormats + 7 NormalizeTr_RejectsInvalidInput + 5 IsValidTr).

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.Core/Customers/PhoneNormalizer.cs \
        LiveDeck.Tests/Customers/PhoneNormalizerTests.cs

git commit -m "feat(core): Phase 4g — PhoneNormalizer (TR E.164 helper)

Pure static helper. NormalizeTr accepts 10/11/12-digit forms with optional +,
spaces, dashes, parens. Returns +90... E.164 or null. IsValidTr checks E.164
TR format (13 chars, +90 prefix, all digits). 18 unit tests."
```

---

### Task 3: WhatsAppMessageBuilder + PaymentContext (Core)

**Files:**
- Create: `LiveDeck.Core/Customers/PaymentContext.cs`
- Create: `LiveDeck.Core/Customers/WhatsAppMessageBuilder.cs`
- Create: `LiveDeck.Tests/Customers/WhatsAppMessageBuilderTests.cs`

**Context:** Template placeholder substitution + wa.me link build. TR culture (`tr-TR`) decimal `1.234,56` ve ay adı (`30 Nisan 2026`). Newline `\n` `Uri.EscapeDataString` tarafından `%0A` olur.

- [ ] **Step 1: PaymentContext record oluştur**

`LiveDeck.Core/Customers/PaymentContext.cs`:

```csharp
using System;

namespace LiveDeck.Core.Customers;

/// <summary>Phase 4g: WhatsApp ödeme isteme mesajı için template input.</summary>
public sealed record PaymentContext(
    string DisplayName,
    decimal TotalAmount,
    DateTime StreamDate,
    string? Iban,
    string? AccountHolder,
    string? Papara);
```

- [ ] **Step 2: Test dosyası yaz (FAIL beklenir)**

`LiveDeck.Tests/Customers/WhatsAppMessageBuilderTests.cs`:

```csharp
using System;
using FluentAssertions;
using LiveDeck.Core.Customers;
using Xunit;

namespace LiveDeck.Tests.Customers;

public class WhatsAppMessageBuilderTests
{
    private readonly WhatsAppMessageBuilder _sut = new();

    [Fact]
    public void BuildMessage_SubstitutesAllPlaceholders()
    {
        var ctx = new PaymentContext(
            DisplayName: "Ali Veli",
            TotalAmount: 245.50m,
            StreamDate: new DateTime(2026, 4, 30),
            Iban: "TR12 0000 0000 0000",
            AccountHolder: "Burak Y",
            Papara: "1234567");

        var template = "Merhaba {ad}, {tarih} yayınımızdan {tutar} TL bekleniyor. IBAN: {iban} ({hesap_sahibi}). Papara: {papara}";

        var result = _sut.BuildMessage(template, ctx);

        result.Should().Be(
            "Merhaba Ali Veli, 30 Nisan 2026 yayınımızdan 245,50 TL bekleniyor. IBAN: TR12 0000 0000 0000 (Burak Y). Papara: 1234567");
    }

    [Fact]
    public void BuildMessage_TrCultureDecimalFormatting()
    {
        var ctx = new PaymentContext("X", 1234.56m, new DateTime(2026, 1, 1), null, null, null);
        var result = _sut.BuildMessage("{tutar}", ctx);
        result.Should().Be("1.234,56");
    }

    [Fact]
    public void BuildMessage_NullPaymentFieldsRenderAsEmpty()
    {
        var ctx = new PaymentContext("X", 0m, new DateTime(2026, 1, 1), null, null, null);
        var result = _sut.BuildMessage("[{iban}][{hesap_sahibi}][{papara}]", ctx);
        result.Should().Be("[][][]");
    }

    [Fact]
    public void BuildWaMeLink_StripsPlusAndEscapesMessage()
    {
        var link = _sut.BuildWaMeLink("+905551234567", "Hello\nWorld");
        link.Should().Be("https://wa.me/905551234567?text=Hello%0AWorld");
    }

    [Fact]
    public void BuildWaMeLink_EscapesTurkishCharsAndSpaces()
    {
        var link = _sut.BuildWaMeLink("+905551234567", "Merhaba Ali, ödeme bekleniyor");
        link.Should().StartWith("https://wa.me/905551234567?text=");
        // Uri.EscapeDataString encodes Turkish chars and spaces
        link.Should().Contain("Merhaba%20Ali");
        link.Should().NotContain(" ");
    }
}
```

- [ ] **Step 3: Test FAIL doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~WhatsAppMessageBuilder"
```

Beklenen: FAIL (`WhatsAppMessageBuilder` tipi yok).

- [ ] **Step 4: WhatsAppMessageBuilder implementasyonu**

`LiveDeck.Core/Customers/WhatsAppMessageBuilder.cs`:

```csharp
using System;
using System.Globalization;

namespace LiveDeck.Core.Customers;

/// <summary>
/// Phase 4g: Settings template'ini PaymentContext ile substitute eder
/// ve wa.me deep-link inşa eder. TR culture decimal/tarih formatlama.
/// </summary>
public sealed class WhatsAppMessageBuilder
{
    private static readonly CultureInfo Tr = new("tr-TR");

    public string BuildMessage(string template, PaymentContext ctx)
    {
        return template
            .Replace("{ad}", ctx.DisplayName)
            .Replace("{tutar}", ctx.TotalAmount.ToString("N2", Tr))
            .Replace("{tarih}", ctx.StreamDate.ToString("dd MMMM yyyy", Tr))
            .Replace("{iban}", ctx.Iban ?? "")
            .Replace("{hesap_sahibi}", ctx.AccountHolder ?? "")
            .Replace("{papara}", ctx.Papara ?? "");
    }

    /// <summary>"+905551234567" + "Hello" → "https://wa.me/905551234567?text=Hello".</summary>
    public string BuildWaMeLink(string e164Phone, string message)
    {
        var phone = e164Phone.TrimStart('+');
        return $"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}";
    }
}
```

- [ ] **Step 5: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~WhatsAppMessageBuilder"
```

Beklenen: 5 test PASS.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.Core/Customers/PaymentContext.cs \
        LiveDeck.Core/Customers/WhatsAppMessageBuilder.cs \
        LiveDeck.Tests/Customers/WhatsAppMessageBuilderTests.cs

git commit -m "feat(core): Phase 4g — WhatsAppMessageBuilder + PaymentContext

Template placeholder substitution: {ad} {tutar} {tarih} {iban} {hesap_sahibi}
{papara}. TR culture decimal (1.234,56) and date (30 Nisan 2026). BuildWaMeLink
strips + and Uri.EscapeDataString-encodes message (\\n → %0A). 5 unit tests."
```

---

### Task 4: IUrlLauncher abstraction + ProcessUrlLauncher + FakeUrlLauncher

**Files:**
- Create: `LiveDeck.Core/Customers/IUrlLauncher.cs`
- Create: `LiveDeck.Core/Customers/ProcessUrlLauncher.cs`
- Create: `LiveDeck.Tests/Fakes/FakeUrlLauncher.cs`

**Context:** `Process.Start` mock'lamak için interface. Production = `ProcessUrlLauncher`, test = `FakeUrlLauncher` (recorded URLs). LiveDeck.Tests'in `Fakes/` klasörü mevcut Phase 4f'de oluşturulmuş — yoksa oluştur.

- [ ] **Step 1: IUrlLauncher interface oluştur**

`LiveDeck.Core/Customers/IUrlLauncher.cs`:

```csharp
namespace LiveDeck.Core.Customers;

/// <summary>Phase 4g: OS handler'a URL gönderme abstraction'ı (Process.Start için mock noktası).</summary>
public interface IUrlLauncher
{
    /// <summary>URL'i OS default handler ile aç. Exception fırlatabilir.</summary>
    void Launch(string url);
}
```

- [ ] **Step 2: ProcessUrlLauncher production implementasyonu**

`LiveDeck.Core/Customers/ProcessUrlLauncher.cs`:

```csharp
using System.Diagnostics;

namespace LiveDeck.Core.Customers;

/// <summary>Default IUrlLauncher: <c>Process.Start</c> + <c>UseShellExecute=true</c>.</summary>
public sealed class ProcessUrlLauncher : IUrlLauncher
{
    public void Launch(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
```

- [ ] **Step 3: FakeUrlLauncher test double**

`LiveDeck.Tests/Fakes/FakeUrlLauncher.cs`:

```csharp
using System;
using System.Collections.Generic;
using LiveDeck.Core.Customers;

namespace LiveDeck.Tests.Fakes;

/// <summary>Test double — kayıt tutar, optionally throws.</summary>
public sealed class FakeUrlLauncher : IUrlLauncher
{
    public List<string> LaunchedUrls { get; } = new();
    public Exception? ThrowOnLaunch { get; set; }

    public void Launch(string url)
    {
        if (ThrowOnLaunch is not null) throw ThrowOnLaunch;
        LaunchedUrls.Add(url);
    }
}
```

- [ ] **Step 4: Build doğrula**

```bash
dotnet build LiveDeck.Tests/LiveDeck.Tests.csproj
```

Beklenen: 0 error.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.Core/Customers/IUrlLauncher.cs \
        LiveDeck.Core/Customers/ProcessUrlLauncher.cs \
        LiveDeck.Tests/Fakes/FakeUrlLauncher.cs

git commit -m "feat(core): Phase 4g — IUrlLauncher + ProcessUrlLauncher + FakeUrlLauncher

Abstraction over Process.Start for testability. Production wraps
ProcessStartInfo{UseShellExecute=true}. FakeUrlLauncher records LaunchedUrls
and supports ThrowOnLaunch for failure-path tests."
```

---

### Task 5: SessionRepository.GetLatestEnded()

**Files:**
- Modify: `LiveDeck.Core/Storage/Repositories/SessionRepository.cs`
- Modify: `LiveDeck.Tests/Storage/SessionRepositoryTests.cs`

**Context:** Phase 4g `CustomerService.GetLastStreamShoppers` ve mesaj template'inde `{tarih}` placeholder için `EndedAt` lazım. Mevcut `GetAllEnded(1)` yerine açıkça `GetLatestEnded()` helper.

- [ ] **Step 1: Test ekle (FAIL beklenir)**

`LiveDeck.Tests/Storage/SessionRepositoryTests.cs` dosyasını aç. Sonuna ekle:

```csharp
[Fact]
public void GetLatestEnded_NoSessions_ReturnsNull()
{
    using var db = TestDb.Create();
    var repo = new SessionRepository(db);
    repo.GetLatestEnded().Should().BeNull();
}

[Fact]
public void GetLatestEnded_OnlyActiveSession_ReturnsNull()
{
    using var db = TestDb.Create();
    var repo = new SessionRepository(db);
    repo.Insert(new StreamSession("s1", "Live", 1000, null, Array.Empty<string>(), null));
    repo.GetLatestEnded().Should().BeNull();
}

[Fact]
public void GetLatestEnded_ReturnsMostRecentlyEndedByEndedAt()
{
    using var db = TestDb.Create();
    var repo = new SessionRepository(db);
    repo.Insert(new StreamSession("s1", "Old", 100, null, Array.Empty<string>(), null));
    repo.End("s1", 200);
    repo.Insert(new StreamSession("s2", "New", 300, null, Array.Empty<string>(), null));
    repo.End("s2", 400);

    var latest = repo.GetLatestEnded();
    latest!.Id.Should().Be("s2");
    latest.EndedAt.Should().Be(400);
}
```

(`StreamSession` constructor parametrelerini mevcut `SessionRepositoryTests.cs`'den birebir kopyala — değişmiş olabilir.)

- [ ] **Step 2: Test FAIL doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~GetLatestEnded"
```

Beklenen: FAIL (`GetLatestEnded` metod yok).

- [ ] **Step 3: SessionRepository.GetLatestEnded ekle**

`LiveDeck.Core/Storage/Repositories/SessionRepository.cs` içinde `GetAllEnded` metodunun yanına:

```csharp
/// <summary>Phase 4g: en son tamamlanmış (EndedAt dolu) session'ı döndürür.</summary>
public StreamSession? GetLatestEnded()
{
    using var conn = _factory.Open();
    var row = conn.QueryFirstOrDefault<Row>(
        @"SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes
          FROM StreamSession
          WHERE EndedAt IS NOT NULL
          ORDER BY EndedAt DESC
          LIMIT 1");
    return row is null ? null : Map(row);
}
```

- [ ] **Step 4: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~GetLatestEnded"
```

Beklenen: 3 test PASS.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.Core/Storage/Repositories/SessionRepository.cs \
        LiveDeck.Tests/Storage/SessionRepositoryTests.cs

git commit -m "feat(core): Phase 4g — SessionRepository.GetLatestEnded()

Returns most-recently-ended session by EndedAt DESC, or null if no ended
sessions exist. Used by CustomerService.GetLastStreamShoppers and message
template {tarih} placeholder. 3 unit tests."
```

---

### Task 6: CustomerService.GetLastStreamShoppers()

**Files:**
- Modify: `LiveDeck.Core/Customers/CustomerService.cs`
- Modify: `LiveDeck.App/App.xaml.cs` (DI registration eğer ctor değişirse)
- Modify: `LiveDeck.Tests/Customers/CustomerServiceTests.cs` (yoksa oluştur)

**Context:** Birleştirici servis. `SessionRepository.GetLatestEnded()` → null ise empty. Aksi halde `LabelRepository.GetTopCustomersBySession(sessionId, int.MaxValue)` → her `TopCustomer` için `CustomerRepository.FindByPlatformAndUsername` ile hidrate. `Phone` field'ına ihtiyaç var, bu yüzden TopCustomer projection'ı yetmez.

**ÖNEMLİ:** Mevcut `CustomerService` ctor'ı sadece `CustomerRepository` alıyorsa, ctor'a `SessionRepository` + `LabelRepository` eklemek `App.xaml.cs` DI'yı kıracak. Önce mevcut ctor'a bak (`Read LiveDeck.Core/Customers/CustomerService.cs`); eğer Sessions/Labels zaten injecte edilmişse Step 4 atlanır.

- [ ] **Step 1: Mevcut CustomerService ctor signature'ını kontrol et**

```bash
grep -n "public CustomerService" LiveDeck.Core/Customers/CustomerService.cs
```

Eğer ctor zaten `SessionRepository` + `LabelRepository` alıyorsa Step 4'ü atla. Almıyorsa devam.

- [ ] **Step 2: CustomerServiceTests'a test ekle (FAIL beklenir)**

`LiveDeck.Tests/Customers/CustomerServiceTests.cs` (yoksa oluştur):

```csharp
using System;
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.Storage;
using Xunit;

namespace LiveDeck.Tests.Customers;

public class CustomerService_GetLastStreamShoppersTests
{
    [Fact]
    public void GetLastStreamShoppers_NoEndedSession_ReturnsEmpty()
    {
        using var db = TestDb.Create();
        var sut = new CustomerService(
            new CustomerRepository(db),
            new SessionRepository(db),
            new LabelRepository(db));

        sut.GetLastStreamShoppers().Should().BeEmpty();
    }

    [Fact]
    public void GetLastStreamShoppers_HydratesCustomersFromLatestEndedSession()
    {
        using var db = TestDb.Create();
        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);

        var alice = new Customer("c1", "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, "+905551111111");
        customers.Insert(alice);

        sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
        // Label printed (PrintedAt non-null) — GetTopCustomersBySession filters to printed
        labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
        sessions.End("s1", 200);

        var sut = new CustomerService(customers, sessions, labels);
        var result = sut.GetLastStreamShoppers();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
        result[0].Phone.Should().Be("+905551111111");
    }

    [Fact]
    public void GetLastStreamShoppers_UnprintedLabelsExcluded()
    {
        using var db = TestDb.Create();
        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);

        customers.Insert(new Customer("c1", "twitch", "bob", "Bob", null,
            100, 100, false, null, null, 0, 0m, null, null, null));
        sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
        labels.Insert(new Label("l1", "s1", "c1", "twitch", "bob", "Apple", null, 50m, 110, PrintedAt: null));
        sessions.End("s1", 200);

        var sut = new CustomerService(customers, sessions, labels);
        sut.GetLastStreamShoppers().Should().BeEmpty();
    }
}
```

**Not:** `Label` constructor pozisyonel parametrelerini mevcut `LabelRepositoryTests.cs`'den kopyala (signature değişmiş olabilir).

- [ ] **Step 3: Test FAIL doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~GetLastStreamShoppers"
```

Beklenen: FAIL (derleme hatası — metod yok ya da ctor uyumsuz).

- [ ] **Step 4: CustomerService ctor + GetLastStreamShoppers ekle**

`LiveDeck.Core/Customers/CustomerService.cs`:

```csharp
using System;
using System.Collections.Generic;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.Core.Customers;

public sealed class CustomerService
{
    private readonly CustomerRepository _customers;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;

    public CustomerService(
        CustomerRepository customers,
        SessionRepository sessions,
        LabelRepository labels)
    {
        _customers = customers;
        _sessions = sessions;
        _labels = labels;
    }

    // ... mevcut public metodları (EnsureExists vb.) burada koruyun.
    // Sadece ctor'a Sessions/Labels parametreleri eklendi.

    /// <summary>
    /// Phase 4g: en son tamamlanmış yayında alışveriş yapan müşteriler
    /// (printed label'ları olanlar), tutar DESC sıralı. Yayın yoksa empty.
    /// </summary>
    public IReadOnlyList<Customer> GetLastStreamShoppers()
    {
        var session = _sessions.GetLatestEnded();
        if (session is null) return Array.Empty<Customer>();

        var top = _labels.GetTopCustomersBySession(session.Id, int.MaxValue);
        var result = new List<Customer>(top.Count);
        foreach (var t in top)
        {
            var c = _customers.FindByPlatformAndUsername(t.Platform, t.Username);
            if (c is not null) result.Add(c);
        }
        return result;
    }
}
```

**Mevcut metodları korumak:** Bu adım `CustomerService.cs` dosyasını **tamamen yeniden yazmıyor** — sadece ctor signature'ına Sessions/Labels eklenir, `GetLastStreamShoppers` eklenir. Mevcut `EnsureExists`, `Upsert` vb. metodlar olduğu gibi kalır. Eğer mevcut metodlar `_customers` dışında bir şey kullanmıyorsa, yeni alanları kullanmazlar; sorun yok.

- [ ] **Step 5: App.xaml.cs DI registration güncelle**

`LiveDeck.App/App.xaml.cs` (veya `Bootstrap.cs` / `AppHost.cs`) içinde `CustomerService` registration:

```csharp
services.AddSingleton<CustomerService>(sp => new CustomerService(
    sp.GetRequiredService<CustomerRepository>(),
    sp.GetRequiredService<SessionRepository>(),
    sp.GetRequiredService<LabelRepository>()));
```

Eğer mevcut registration `services.AddSingleton<CustomerService>()` (auto-wire) kullanıyorsa, bu adım atlanabilir — DI container ctor'u otomatik resolve eder.

- [ ] **Step 6: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj
```

Beklenen: tüm testler PASS.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.Core/Customers/CustomerService.cs \
        LiveDeck.App/App.xaml.cs \
        LiveDeck.Tests/Customers/CustomerServiceTests.cs

git commit -m "feat(core): Phase 4g — CustomerService.GetLastStreamShoppers()

Joins SessionRepository.GetLatestEnded + LabelRepository.GetTopCustomersBySession
+ CustomerRepository.FindByPlatformAndUsername. Empty if no ended session.
Hydrates full Customer (Phone field needed for downstream WhatsApp flow).
3 unit tests. CustomerService ctor expanded; DI updated."
```

---

### Task 7: AppSettings.PaymentSettings nested class

**Files:**
- Modify: `LiveDeck.Core/Settings/AppSettings.cs`
- Modify or Create: `LiveDeck.Tests/Settings/SettingsStoreTests.cs`

**Context:** AppSettings'e `Payment` nested class eklenir. Default'lar ilk yüklemede new'lenir. Settings JSON `camelCase` policy kullanıyor.

- [ ] **Step 1: AppSettings.cs değiştir**

`LiveDeck.Core/Settings/AppSettings.cs`:

```csharp
namespace LiveDeck.Core.Settings;

public sealed class AppSettings
{
    public int OverlayPort { get; set; } = 4747;
    public string ChatTheme { get; set; } = "minimal";

    // Printing
    public string? PrinterName { get; set; }
    public int LabelWidthMm  { get; set; } = 60;
    public int LabelHeightMm { get; set; } = 30;
    public int LabelGapMm    { get; set; } = 5;
    public string LabelFontFamily { get; set; } = "Arial";
    public int   LabelUserFontSize  { get; set; } = 14;
    public int   LabelMessageFontSize { get; set; } = 12;

    // Shortcuts (Phase 3b-1)
    public bool UseCustomShortcuts { get; set; } = false;
    public System.Collections.Generic.Dictionary<string, string>? CustomShortcuts { get; set; }

    /// <summary>Phase 4f: last intake form submission cursor (max SubmittedAt synced).</summary>
    public DateTimeOffset? LastIntakeFormSync { get; set; }

    /// <summary>Phase 4g: WhatsApp ödeme isteme yapılandırması.</summary>
    public PaymentSettings Payment { get; set; } = new();
}

/// <summary>Phase 4g: WhatsApp ödeme istemleri için Settings bloğu.</summary>
public sealed class PaymentSettings
{
    public string WhatsAppMessageTemplate { get; set; } =
        "Merhaba {ad}, {tarih} yayınımızdan toplam {tutar} TL ödemeniz bekleniyor.\n\n" +
        "IBAN: {iban}\nHesap Sahibi: {hesap_sahibi}\nPapara: {papara}\n\nTeşekkürler!";

    public string Iban { get; set; } = "";
    public string AccountHolder { get; set; } = "";
    public string Papara { get; set; } = "";
}
```

- [ ] **Step 2: SettingsStore round-trip testi ekle**

`LiveDeck.Tests/Settings/SettingsStoreTests.cs` (yoksa oluştur, mevcut benzer testleri varsa yanına):

```csharp
[Fact]
public void Save_Then_Load_RoundTripsPaymentSettings()
{
    var path = Path.Combine(Path.GetTempPath(), $"livedeck-test-{Guid.NewGuid():N}.json");
    try
    {
        var store = new SettingsStore(path);
        var s = new AppSettings();
        s.Payment.WhatsAppMessageTemplate = "Hi {ad}, pay {tutar}!";
        s.Payment.Iban = "TR12";
        s.Payment.AccountHolder = "Burak";
        s.Payment.Papara = "1234567";
        store.Save(s);

        var loaded = store.Load();
        loaded.Payment.WhatsAppMessageTemplate.Should().Be("Hi {ad}, pay {tutar}!");
        loaded.Payment.Iban.Should().Be("TR12");
        loaded.Payment.AccountHolder.Should().Be("Burak");
        loaded.Payment.Papara.Should().Be("1234567");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

[Fact]
public void Load_FreshFile_HasDefaultPaymentTemplate()
{
    var path = Path.Combine(Path.GetTempPath(), $"livedeck-test-{Guid.NewGuid():N}.json");
    try
    {
        var store = new SettingsStore(path);
        var loaded = store.Load();
        loaded.Payment.Should().NotBeNull();
        loaded.Payment.WhatsAppMessageTemplate.Should().Contain("{ad}");
        loaded.Payment.WhatsAppMessageTemplate.Should().Contain("{tutar}");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
```

- [ ] **Step 3: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~Payment"
```

Beklenen: 2 test PASS.

- [ ] **Step 4: Commit**

```bash
git add LiveDeck.Core/Settings/AppSettings.cs \
        LiveDeck.Tests/Settings/SettingsStoreTests.cs

git commit -m "feat(core): Phase 4g — AppSettings.Payment nested class

PaymentSettings: WhatsAppMessageTemplate (default TR template), Iban,
AccountHolder, Papara. Default Payment instance auto-created. Round-trip
JSON serialization tests pass."
```

---

### Task 8: PaymentRequestService (LiveDeck.App)

**Files:**
- Create: `LiveDeck.App/Services/PaymentRequestService.cs`
- Create: `LiveDeck.Tests/App/PaymentRequestServiceTests.cs` (LiveDeck.Tests yapısına uygun klasör yoksa `LiveDeck.Tests/Services/` kullan)

**Context:** Orchestration servisi. `Customer.Phone` validate → null/invalid ise `PhoneRequired`. Settings template'ini `WhatsAppMessageBuilder` ile substitute → `IUrlLauncher.Launch`. Exception → `LaunchFailed` + log. Settings'e `SettingsStore` üzerinden erişir (her çağrıda Load yapar, basit; cache yok — YAGNI).

- [ ] **Step 1: PaymentRequestResult enum ve test dosyası yaz (FAIL beklenir)**

`LiveDeck.Tests/Services/PaymentRequestServiceTests.cs`:

```csharp
using System;
using System.IO;
using FluentAssertions;
using LiveDeck.App.Services;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Settings;
using LiveDeck.Tests.Fakes;
using Xunit;

namespace LiveDeck.Tests.Services;

public class PaymentRequestServiceTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly SettingsStore _store;
    private readonly FakeUrlLauncher _launcher;

    public PaymentRequestServiceTests()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), $"livedeck-pr-{Guid.NewGuid():N}.json");
        _store = new SettingsStore(_settingsPath);
        _launcher = new FakeUrlLauncher();
    }

    public void Dispose()
    {
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    private static Customer MakeCustomer(string? phone) =>
        new("c1", "twitch", "alice", "Alice", null, 100, 100,
            false, null, null, 0, 0m, null, null, phone);

    [Fact]
    public void OpenWhatsApp_PhoneNull_ReturnsPhoneRequired()
    {
        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer(null), 100m, new DateTime(2026, 4, 30));
        result.Should().Be(PaymentRequestResult.PhoneRequired);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public void OpenWhatsApp_PhoneInvalid_ReturnsPhoneRequired()
    {
        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer("not-a-phone"), 100m, new DateTime(2026, 4, 30));
        result.Should().Be(PaymentRequestResult.PhoneRequired);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public void OpenWhatsApp_ValidPhone_LaunchesWaMeUrl()
    {
        var settings = new AppSettings();
        settings.Payment.WhatsAppMessageTemplate = "Pay {tutar}";
        settings.Payment.Iban = "TR12";
        _store.Save(settings);

        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer("+905551234567"), 100m, new DateTime(2026, 4, 30));

        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().HaveCount(1);
        _launcher.LaunchedUrls[0].Should().StartWith("https://wa.me/905551234567?text=");
        _launcher.LaunchedUrls[0].Should().Contain("Pay%20100%2C00");
    }

    [Fact]
    public void OpenWhatsApp_LauncherThrows_ReturnsLaunchFailed()
    {
        _launcher.ThrowOnLaunch = new InvalidOperationException("no handler");
        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer("+905551234567"), 100m, new DateTime(2026, 4, 30));
        result.Should().Be(PaymentRequestResult.LaunchFailed);
    }
}
```

- [ ] **Step 2: Test FAIL doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~PaymentRequestService"
```

Beklenen: FAIL (`PaymentRequestService` ve `PaymentRequestResult` yok).

- [ ] **Step 3: PaymentRequestService implementasyonu**

`LiveDeck.App/Services/PaymentRequestService.cs`:

```csharp
using System;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Settings;

namespace LiveDeck.App.Services;

public enum PaymentRequestResult
{
    Opened,
    PhoneRequired,
    LaunchFailed
}

/// <summary>
/// Phase 4g: Customer + tutar + tarih → Settings template substitution + wa.me launch.
/// Phone null/invalid ise PhoneRequired (caller PhoneEntryDialog açar).
/// </summary>
public sealed class PaymentRequestService
{
    private readonly SettingsStore _settingsStore;
    private readonly WhatsAppMessageBuilder _messageBuilder;
    private readonly IUrlLauncher _launcher;

    public PaymentRequestService(
        SettingsStore settingsStore,
        WhatsAppMessageBuilder messageBuilder,
        IUrlLauncher launcher)
    {
        _settingsStore = settingsStore;
        _messageBuilder = messageBuilder;
        _launcher = launcher;
    }

    public PaymentRequestResult OpenWhatsApp(Customer customer, decimal totalAmount, DateTime streamDate)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var ctx = new PaymentContext(
            DisplayName: customer.DisplayName ?? customer.Username,
            TotalAmount: totalAmount,
            StreamDate: streamDate,
            Iban: settings.Payment.Iban,
            AccountHolder: settings.Payment.AccountHolder,
            Papara: settings.Payment.Papara);

        var message = _messageBuilder.BuildMessage(settings.Payment.WhatsAppMessageTemplate, ctx);
        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            return PaymentRequestResult.LaunchFailed;
        }
    }
}
```

- [ ] **Step 4: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~PaymentRequestService"
```

Beklenen: 4 test PASS.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.App/Services/PaymentRequestService.cs \
        LiveDeck.Tests/Services/PaymentRequestServiceTests.cs

git commit -m "feat(app): Phase 4g — PaymentRequestService

Orchestration: Customer.Phone validate → Settings template substitute →
wa.me launch via IUrlLauncher. Returns enum (Opened/PhoneRequired/LaunchFailed).
4 unit tests using FakeUrlLauncher + temp SettingsStore."
```

---

### Task 9: PhoneEntryDialogViewModel + PhoneEntryDialog View

**Files:**
- Create: `LiveDeck.App/ViewModels/PhoneEntryDialogViewModel.cs`
- Create: `LiveDeck.App/Views/PhoneEntryDialog.xaml`
- Create: `LiveDeck.App/Views/PhoneEntryDialog.xaml.cs`
- Create: `LiveDeck.Tests/ViewModels/PhoneEntryDialogViewModelTests.cs`

**Context:** Modal dialog. Kullanıcı numarayı girer → `PhoneNormalizer` ile validate → invalid ise inline error → valid ise `CustomerRepository.UpdatePhone` + `DialogResult=true`. ViewModel `Window` referansını içermez; close action callback ile dispatch edilir (test edilebilirlik için).

- [ ] **Step 1: ViewModel test dosyası yaz (FAIL beklenir)**

`LiveDeck.Tests/ViewModels/PhoneEntryDialogViewModelTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.Storage;
using Xunit;

namespace LiveDeck.Tests.ViewModels;

public class PhoneEntryDialogViewModelTests
{
    [Fact]
    public void Save_InvalidPhone_SetsValidationErrorAndDoesNotClose()
    {
        using var db = TestDb.Create();
        var customers = new CustomerRepository(db);
        var c = new Customer("c1", "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, null);
        customers.Insert(c);

        var closed = false;
        var sut = new PhoneEntryDialogViewModel(customers, "c1", () => closed = true);
        sut.PhoneInput = "abc";

        sut.SaveCommand.Execute(null);

        sut.ValidationError.Should().NotBeNullOrEmpty();
        closed.Should().BeFalse();
        customers.GetById("c1")!.Phone.Should().BeNull();
    }

    [Fact]
    public void Save_ValidPhone_PersistsE164AndCloses()
    {
        using var db = TestDb.Create();
        var customers = new CustomerRepository(db);
        customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, null));

        var closed = false;
        var sut = new PhoneEntryDialogViewModel(customers, "c1", () => closed = true);
        sut.PhoneInput = "5551234567";

        sut.SaveCommand.Execute(null);

        sut.ValidationError.Should().BeNull();
        closed.Should().BeTrue();
        customers.GetById("c1")!.Phone.Should().Be("+905551234567");
    }

    [Fact]
    public void Save_EmptyInput_SetsValidationError()
    {
        using var db = TestDb.Create();
        var customers = new CustomerRepository(db);
        customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, null));

        var closed = false;
        var sut = new PhoneEntryDialogViewModel(customers, "c1", () => closed = true);
        sut.PhoneInput = "";

        sut.SaveCommand.Execute(null);

        sut.ValidationError.Should().NotBeNullOrEmpty();
        closed.Should().BeFalse();
    }
}
```

- [ ] **Step 2: ViewModel implementasyonu**

`LiveDeck.App/ViewModels/PhoneEntryDialogViewModel.cs`:

```csharp
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.ViewModels;

/// <summary>
/// Phase 4g: müşterinin telefonu yokken inline collect.
/// Save → PhoneNormalizer → invalid:error / valid: UpdatePhone + close callback.
/// </summary>
public sealed partial class PhoneEntryDialogViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;
    private readonly string _customerId;
    private readonly Action _closeAction;

    [ObservableProperty]
    private string _phoneInput = "";

    [ObservableProperty]
    private string? _validationError;

    public PhoneEntryDialogViewModel(
        CustomerRepository customers,
        string customerId,
        Action closeAction)
    {
        _customers = customers;
        _customerId = customerId;
        _closeAction = closeAction;
    }

    [RelayCommand]
    private void Save()
    {
        var normalized = PhoneNormalizer.NormalizeTr(PhoneInput);
        if (normalized is null)
        {
            ValidationError = "Geçersiz telefon numarası. 10 haneli TR mobil numara girin.";
            return;
        }

        ValidationError = null;
        _customers.UpdatePhone(_customerId, normalized);
        _closeAction();
    }
}
```

- [ ] **Step 3: ViewModel testleri PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~PhoneEntryDialogViewModel"
```

Beklenen: 3 test PASS.

- [ ] **Step 4: PhoneEntryDialog.xaml oluştur**

`LiveDeck.App/Views/PhoneEntryDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.PhoneEntryDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="WhatsApp Numarası Gerekli"
        Width="400" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner">
    <StackPanel Margin="16">
        <TextBlock Text="Bu müşterinin WhatsApp numarası kayıtlı değil. Lütfen girin:"
                   TextWrapping="Wrap" Margin="0,0,0,8"/>
        <TextBox Text="{Binding PhoneInput, UpdateSourceTrigger=PropertyChanged}"
                 MaxLength="20" FontSize="14" Padding="6"/>
        <TextBlock Text="+90 otomatik eklenecek. Örn: 555 123 45 67"
                   FontSize="11" Foreground="#666" Margin="0,4,0,0"/>
        <TextBlock Text="{Binding ValidationError}"
                   Foreground="Red" FontSize="11" Margin="0,4,0,0"
                   Visibility="{Binding ValidationError, Converter={StaticResource NullToCollapsedConverter}}"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="İptal" IsCancel="True" MinWidth="80" Margin="0,0,8,0"/>
            <Button Content="Kaydet ve Aç" IsDefault="True"
                    Command="{Binding SaveCommand}" MinWidth="120"
                    Background="#25D366" Foreground="White" BorderThickness="0" Padding="8,4"/>
        </StackPanel>
    </StackPanel>
</Window>
```

**Not:** `NullToCollapsedConverter` mevcut App.xaml resources'da yoksa, `Visibility="{Binding HasValidationError, Converter={StaticResource BoolToVisibleConverter}}"` pattern'i kullan ve VM'a `public bool HasValidationError => !string.IsNullOrEmpty(ValidationError)` getter ekle. Mevcut converter ismini doğrulamak için `App.xaml`'a bak (Phase 4f'de `BoolToVisibleConverter` kullanılmıştı). Pratik: HasValidationError computed property + BoolToVisibleConverter pattern güvenli.

**Daha güvenli alternatif (App.xaml converter ismi belirsizse):**

```xml
<TextBlock Text="{Binding ValidationError}"
           Foreground="Red" FontSize="11" Margin="0,4,0,0"
           Visibility="{Binding HasValidationError, Converter={StaticResource BoolToVisibleConverter}}"/>
```

Ve `PhoneEntryDialogViewModel` içine ekle:

```csharp
public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

partial void OnValidationErrorChanged(string? value) => OnPropertyChanged(nameof(HasValidationError));
```

- [ ] **Step 5: PhoneEntryDialog.xaml.cs code-behind**

`LiveDeck.App/Views/PhoneEntryDialog.xaml.cs`:

```csharp
using System.Windows;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.Views;

public partial class PhoneEntryDialog : Window
{
    public PhoneEntryDialog(CustomerRepository customers, string customerId)
    {
        InitializeComponent();
        var vm = new PhoneEntryDialogViewModel(customers, customerId, () =>
        {
            DialogResult = true;
            Close();
        });
        DataContext = vm;
    }
}
```

- [ ] **Step 6: Build doğrula**

```bash
dotnet build LiveDeck.App/LiveDeck.App.csproj
```

Beklenen: 0 error.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.App/ViewModels/PhoneEntryDialogViewModel.cs \
        LiveDeck.App/Views/PhoneEntryDialog.xaml \
        LiveDeck.App/Views/PhoneEntryDialog.xaml.cs \
        LiveDeck.Tests/ViewModels/PhoneEntryDialogViewModelTests.cs

git commit -m "feat(app): Phase 4g — PhoneEntryDialog (inline phone capture)

Modal dialog for chat-platform customers without saved phone. Save command:
PhoneNormalizer.NormalizeTr → on invalid sets ValidationError; on valid
UpdatePhone(customerId) and triggers close callback. ViewModel decoupled
from Window via Action callback for testability. 3 unit tests."
```

---

### Task 10: SettingsViewModel — Payment fields

**Files:**
- Modify: `LiveDeck.App/ViewModels/SettingsViewModel.cs`
- Modify or Create: `LiveDeck.Tests/ViewModels/SettingsViewModelTests.cs`

**Context:** Mevcut Settings VM'a 4 yeni `[ObservableProperty]`. Save'de `_settings.Payment.X = X` güncellenir, `_settingsStore.Save(_settings)` çağrılır. Load'da VM property'ler `_settings.Payment.X` ile init.

- [ ] **Step 1: Mevcut SettingsViewModel pattern'ini incele**

```bash
head -50 LiveDeck.App/ViewModels/SettingsViewModel.cs
```

Mevcut fields nasıl init oluyor (ctor'da load), Save komutu nasıl tanımlı görmek için.

- [ ] **Step 2: Payment fields VM'a ekle**

`LiveDeck.App/ViewModels/SettingsViewModel.cs` içine (mevcut alanların yanına):

```csharp
// Phase 4g — Payment settings
[ObservableProperty]
private string _paymentTemplate = "";

[ObservableProperty]
private string _iban = "";

[ObservableProperty]
private string _accountHolder = "";

[ObservableProperty]
private string _papara = "";
```

Mevcut ctor'da (`Load` çağrısı veya field init noktasında) `_settings` AppSettings instance'ından init et:

```csharp
// ctor sonunda veya Load metodunda
PaymentTemplate = _settings.Payment.WhatsAppMessageTemplate;
Iban = _settings.Payment.Iban;
AccountHolder = _settings.Payment.AccountHolder;
Papara = _settings.Payment.Papara;
```

Mevcut `Save` metodunda (veya `[RelayCommand] Save`) AppSettings'e geri yaz:

```csharp
_settings.Payment.WhatsAppMessageTemplate = PaymentTemplate;
_settings.Payment.Iban = Iban;
_settings.Payment.AccountHolder = AccountHolder;
_settings.Payment.Papara = Papara;
_settingsStore.Save(_settings);
```

**Not:** Mevcut `SettingsViewModel`'in field naming convention'ı farklı olabilir (örn. `_overlayPort`). Aynı pattern'i izle (CommunityToolkit.Mvvm `[ObservableProperty]` + private field underscore).

- [ ] **Step 3: Test ekle**

`LiveDeck.Tests/ViewModels/SettingsViewModelTests.cs`:

```csharp
using System;
using System.IO;
using FluentAssertions;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Settings;
using Xunit;

namespace LiveDeck.Tests.ViewModels;

public class SettingsViewModel_PaymentTests : IDisposable
{
    private readonly string _path;

    public SettingsViewModel_PaymentTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"livedeck-svm-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_PopulatesPaymentFieldsFromSettings()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings();
        s.Payment.WhatsAppMessageTemplate = "Hi {ad}";
        s.Payment.Iban = "TR12";
        s.Payment.AccountHolder = "Burak";
        s.Payment.Papara = "1234567";
        store.Save(s);

        var sut = new SettingsViewModel(store);   // mevcut ctor signature'a uygula
        sut.PaymentTemplate.Should().Be("Hi {ad}");
        sut.Iban.Should().Be("TR12");
        sut.AccountHolder.Should().Be("Burak");
        sut.Papara.Should().Be("1234567");
    }

    [Fact]
    public void Save_PersistsPaymentFields()
    {
        var store = new SettingsStore(_path);
        store.Save(new AppSettings());

        var sut = new SettingsViewModel(store);
        sut.PaymentTemplate = "New {tutar}";
        sut.Iban = "TR99";
        sut.AccountHolder = "X";
        sut.Papara = "9";
        sut.SaveCommand.Execute(null);

        var loaded = store.Load();
        loaded.Payment.WhatsAppMessageTemplate.Should().Be("New {tutar}");
        loaded.Payment.Iban.Should().Be("TR99");
        loaded.Payment.AccountHolder.Should().Be("X");
        loaded.Payment.Papara.Should().Be("9");
    }
}
```

**Not:** Eğer `SettingsViewModel` ctor `(SettingsStore, ...)` dışında başka şeyler alıyorsa (ör. printer service), mevcut ctor signature'a uygun şekilde test setup'ını genişlet. Mevcut testler (varsa) referans olarak kullanılabilir.

- [ ] **Step 4: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~SettingsViewModel_Payment"
```

Beklenen: 2 test PASS.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.App/ViewModels/SettingsViewModel.cs \
        LiveDeck.Tests/ViewModels/SettingsViewModelTests.cs

git commit -m "feat(app): Phase 4g — SettingsViewModel payment fields

PaymentTemplate, Iban, AccountHolder, Papara observable properties.
Load from AppSettings.Payment in ctor; Save persists back via SettingsStore.
2 unit tests."
```

---

### Task 11: SettingsDialog.xaml — Payment GroupBox

**Files:**
- Modify: `LiveDeck.App/Views/SettingsDialog.xaml`

**Context:** Mevcut Settings dialog'a yeni "WhatsApp Ödeme İsteme" GroupBox. UI sadece — VM bindings Task 10'da hazır.

- [ ] **Step 1: SettingsDialog.xaml'a GroupBox ekle**

`LiveDeck.App/Views/SettingsDialog.xaml` dosyasını aç. Mevcut son GroupBox/StackPanel'in altına (form'un altına, kaydet butonunun üstüne) ekle:

```xml
<GroupBox Header="WhatsApp Ödeme İsteme" Margin="0,12,0,0" Padding="8">
    <StackPanel>
        <Label Content="Mesaj Şablonu" Margin="0,0,0,2"/>
        <TextBox Text="{Binding PaymentTemplate, UpdateSourceTrigger=PropertyChanged}"
                 AcceptsReturn="True" TextWrapping="Wrap"
                 MinHeight="120" MaxHeight="200"
                 VerticalScrollBarVisibility="Auto"
                 FontFamily="Consolas" FontSize="12"/>
        <TextBlock FontSize="11" Foreground="#666" Margin="0,2,0,8"
                   TextWrapping="Wrap">
            Placeholder: {ad} {tutar} {tarih} {iban} {hesap_sahibi} {papara}
        </TextBlock>

        <Label Content="IBAN" Margin="0,4,0,2"/>
        <TextBox Text="{Binding Iban, UpdateSourceTrigger=PropertyChanged}"/>

        <Label Content="Hesap Sahibi" Margin="0,4,0,2"/>
        <TextBox Text="{Binding AccountHolder, UpdateSourceTrigger=PropertyChanged}"/>

        <Label Content="Papara/Ziraat Numarası (opsiyonel)" Margin="0,4,0,2"/>
        <TextBox Text="{Binding Papara, UpdateSourceTrigger=PropertyChanged}"/>
    </StackPanel>
</GroupBox>
```

- [ ] **Step 2: Build doğrula**

```bash
dotnet build LiveDeck.App/LiveDeck.App.csproj
```

Beklenen: 0 error. XAML binding'ler VM'da var (Task 10).

- [ ] **Step 3: Commit**

```bash
git add LiveDeck.App/Views/SettingsDialog.xaml

git commit -m "feat(app): Phase 4g — SettingsDialog Payment GroupBox

Multi-line message template + IBAN + AccountHolder + Papara fields with
placeholder hint. Bindings to SettingsViewModel.PaymentTemplate/Iban/etc."
```

---

### Task 12: CustomerSearchViewModel — LastStreamShoppersOnly filter + OpenWhatsApp command

**Files:**
- Modify: `LiveDeck.App/ViewModels/CustomerSearchViewModel.cs`
- Modify: `LiveDeck.Tests/ViewModels/CustomerSearchViewModelTests.cs`

**Context:** Mevcut Phase 3a/4f search VM'a iki ekleme:
1. `LastStreamShoppersOnly` checkbox → değiştiğinde `RefreshSearch()` (Phase 4f Task 11 deneyimi: VM'da `ApplySearch` ya da `RefreshSearch` helper var; mevcut pattern'e uy).
2. `[RelayCommand] OpenWhatsAppAsync(Customer)` → `PaymentRequestService.OpenWhatsApp` çağırır; `PhoneRequired` ise `IDialogService` (yeni interface) üzerinden PhoneEntryDialog açar; success ise retry. `LaunchFailed` ise MessageBox.

**ÖNEMLİ — IDialogService:** Test edilebilirlik için VM `Window.ShowDialog` doğrudan çağırmaz. `IDialogService.ShowPhoneEntryDialog(string customerId) → bool` interface'i tanımla. Production = `WpfDialogService` (App'te), test = `FakeDialogService`. Eğer codebase'de zaten bir `IDialogService` varsa onu genişlet.

- [ ] **Step 1: IDialogService interface oluştur**

`LiveDeck.App/Services/IDialogService.cs` (yoksa yeni; varsa metod ekle):

```csharp
namespace LiveDeck.App.Services;

public interface IDialogService
{
    /// <summary>Phase 4g: PhoneEntryDialog modal göster. true = kaydedildi.</summary>
    bool ShowPhoneEntryDialog(string customerId);

    /// <summary>Phase 4g: hata MessageBox.</summary>
    void ShowError(string message);
}
```

- [ ] **Step 2: WpfDialogService production implementasyonu**

`LiveDeck.App/Services/WpfDialogService.cs`:

```csharp
using System.Windows;
using LiveDeck.App.Views;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.Services;

public sealed class WpfDialogService : IDialogService
{
    private readonly CustomerRepository _customers;

    public WpfDialogService(CustomerRepository customers)
    {
        _customers = customers;
    }

    public bool ShowPhoneEntryDialog(string customerId)
    {
        var dlg = new PhoneEntryDialog(_customers, customerId)
        {
            Owner = Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true;
    }

    public void ShowError(string message)
    {
        MessageBox.Show(message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

- [ ] **Step 3: FakeDialogService test double**

`LiveDeck.Tests/Fakes/FakeDialogService.cs`:

```csharp
using System;
using System.Collections.Generic;
using LiveDeck.App.Services;

namespace LiveDeck.Tests.Fakes;

public sealed class FakeDialogService : IDialogService
{
    public List<string> PhoneEntryShownFor { get; } = new();
    public List<string> ErrorsShown { get; } = new();
    public Func<string, bool> PhoneEntryResult { get; set; } = _ => false;

    public bool ShowPhoneEntryDialog(string customerId)
    {
        PhoneEntryShownFor.Add(customerId);
        return PhoneEntryResult(customerId);
    }

    public void ShowError(string message) => ErrorsShown.Add(message);
}
```

- [ ] **Step 4: CustomerSearchViewModel test ekle (FAIL beklenir)**

`LiveDeck.Tests/ViewModels/CustomerSearchViewModelTests.cs` sonuna ekle (mevcut Phase 3a/4f testlerinin yanına):

```csharp
[Fact]
public void LastStreamShoppersOnly_True_UsesGetLastStreamShoppersSource()
{
    using var db = TestDb.Create();
    var customers = new CustomerRepository(db);
    var sessions = new SessionRepository(db);
    var labels = new LabelRepository(db);

    var alice = new Customer("c1", "twitch", "alice", "Alice", null,
        100, 100, false, null, null, 0, 0m, null, null, "+905551111111");
    customers.Insert(alice);
    sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
    labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
    sessions.End("s1", 200);

    var customerService = new CustomerService(customers, sessions, labels);
    var paymentService = new PaymentRequestService(
        new SettingsStore(Path.GetTempFileName()),
        new WhatsAppMessageBuilder(),
        new FakeUrlLauncher());
    var dialogs = new FakeDialogService();
    var sut = new CustomerSearchViewModel(customers, customerService, paymentService, dialogs);

    sut.LastStreamShoppersOnly = true;

    sut.Results.Should().HaveCount(1);
    sut.Results[0].Username.Should().Be("alice");
}

[Fact]
public async Task OpenWhatsApp_PhoneRequired_ShowsDialogThenRetries()
{
    using var db = TestDb.Create();
    var customers = new CustomerRepository(db);
    var sessions = new SessionRepository(db);
    var labels = new LabelRepository(db);

    var alice = new Customer("c1", "twitch", "alice", "Alice", null,
        100, 100, false, null, null, 0, 0m, null, null, null);   // no phone
    customers.Insert(alice);
    sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
    labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
    sessions.End("s1", 200);

    var customerService = new CustomerService(customers, sessions, labels);
    var settingsPath = Path.GetTempFileName();
    var settingsStore = new SettingsStore(settingsPath);
    settingsStore.Save(new AppSettings());
    var launcher = new FakeUrlLauncher();
    var paymentService = new PaymentRequestService(
        settingsStore, new WhatsAppMessageBuilder(), launcher);
    var dialogs = new FakeDialogService
    {
        PhoneEntryResult = id =>
        {
            customers.UpdatePhone(id, "+905551111111");
            return true;
        }
    };

    var sut = new CustomerSearchViewModel(customers, customerService, paymentService, dialogs);

    await sut.OpenWhatsAppCommand.ExecuteAsync(alice);

    dialogs.PhoneEntryShownFor.Should().ContainSingle().Which.Should().Be("c1");
    launcher.LaunchedUrls.Should().HaveCount(1); // retry succeeded
    File.Delete(settingsPath);
}

[Fact]
public async Task OpenWhatsApp_PhoneAlreadyValid_LaunchesDirectly()
{
    using var db = TestDb.Create();
    var customers = new CustomerRepository(db);
    var sessions = new SessionRepository(db);
    var labels = new LabelRepository(db);
    var alice = new Customer("c1", "twitch", "alice", "Alice", null,
        100, 100, false, null, null, 0, 0m, null, null, "+905551111111");
    customers.Insert(alice);
    sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
    labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
    sessions.End("s1", 200);

    var customerService = new CustomerService(customers, sessions, labels);
    var settingsPath = Path.GetTempFileName();
    var settingsStore = new SettingsStore(settingsPath);
    settingsStore.Save(new AppSettings());
    var launcher = new FakeUrlLauncher();
    var paymentService = new PaymentRequestService(
        settingsStore, new WhatsAppMessageBuilder(), launcher);
    var dialogs = new FakeDialogService();

    var sut = new CustomerSearchViewModel(customers, customerService, paymentService, dialogs);

    await sut.OpenWhatsAppCommand.ExecuteAsync(alice);

    dialogs.PhoneEntryShownFor.Should().BeEmpty();
    launcher.LaunchedUrls.Should().HaveCount(1);
    File.Delete(settingsPath);
}
```

**ÖNEMLİ:** Mevcut `CustomerSearchViewModel` ctor signature `(CustomerRepository, ...)`. Yukarıdaki testler 4 paramlı ctor varsayar; mevcut ctor'u step 5'te bu signature'a getireceğiz. Mevcut ctor parametreleri (Phase 3a'dan kalan: belki `CustomerService` vs `CustomerRepository`) test'e uyumlu değilse, mevcut paramları koru ve yeni paramları SONA ekle.

- [ ] **Step 5: CustomerSearchViewModel'a alanlar ve komut ekle**

`LiveDeck.App/ViewModels/CustomerSearchViewModel.cs` dosyasını aç. Mevcut yapıya:

**5a. ctor'a inject:**

```csharp
private readonly CustomerService _customerService;
private readonly PaymentRequestService _paymentService;
private readonly IDialogService _dialogService;

public CustomerSearchViewModel(
    CustomerRepository customers,
    CustomerService customerService,
    PaymentRequestService paymentService,
    IDialogService dialogService /* + mevcut paramlar */)
{
    _customers = customers;
    _customerService = customerService;
    _paymentService = paymentService;
    _dialogService = dialogService;
    // ... mevcut init
}
```

**5b. Filter property:**

```csharp
[ObservableProperty]
private bool _lastStreamShoppersOnly;

partial void OnLastStreamShoppersOnlyChanged(bool value) => RefreshSearch();
```

**5c. ApplySearch (veya RefreshSearch) içinde dallanma:**

Mevcut `ApplySearch(string query)` metodunu bul. Başına ekle:

```csharp
private void ApplySearch(string query)
{
    Results.Clear();
    IReadOnlyList<Customer> source;

    if (LastStreamShoppersOnly)
    {
        source = _customerService.GetLastStreamShoppers();
        // in-memory query filter
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = source.Where(c =>
                c.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (c.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }
    else
    {
        // mevcut Phase 3a/4f search akışı (repo.SearchByUsername / similar)
        source = _customers.SearchByUsernameContains(query, limit: 100);
        // Mevcut Platform filter mantığı korunur
    }

    foreach (var c in source) Results.Add(c);
}
```

**Not:** Mevcut `ApplySearch` farklı isimde olabilir (`RefreshSearch`, `OnQueryChanged` vb.); mevcut metoda `LastStreamShoppersOnly` dallanmasını ekle. Mevcut Platform filter'ı dallanma içinde KORU.

**5d. RefreshSearch helper:**

```csharp
private void RefreshSearch() => ApplySearch(Query ?? "");
```

(Mevcut isimde varsa onu kullan.)

**5e. OpenWhatsApp command:**

```csharp
[RelayCommand]
private async Task OpenWhatsAppAsync(Customer? customer)
{
    if (customer is null) return;

    var session = _sessions.GetLatestEnded();   // SessionRepository injecte gerekir, OR:
    // alternatif: customerService'e GetLastStreamDate() helper ekle
    var streamDate = session is null
        ? DateTime.Now
        : DateTimeOffset.FromUnixTimeSeconds(session.EndedAt!.Value).LocalDateTime;

    // amount: customer.TotalAmount cumulative — son yayın için per-session amount istiyoruz.
    // GetLastStreamShoppers + per-customer TotalAmount eşleşmesi için TopCustomer projection lazım.
    // Pratik: TotalAmount cumulative customer.TotalAmount kullan (session-spesifik amount için Task 13'te StreamReport pattern uygulanır).
    var result = _paymentService.OpenWhatsApp(customer, customer.TotalAmount, streamDate);

    if (result == PaymentRequestResult.PhoneRequired)
    {
        var saved = _dialogService.ShowPhoneEntryDialog(customer.Id);
        if (saved)
        {
            // Refresh customer with new phone
            var updated = _customers.GetById(customer.Id);
            if (updated is not null)
                _paymentService.OpenWhatsApp(updated, updated.TotalAmount, streamDate);
        }
    }
    else if (result == PaymentRequestResult.LaunchFailed)
    {
        _dialogService.ShowError("WhatsApp açılamadı. WhatsApp Desktop kurulu mu?");
    }
}
```

**Karar (per-stream amount):** CustomerSearch akışında `customer.TotalAmount` cumulative değerini kullanıyoruz. Per-stream amount sadece StreamReport'ta gösterilir (Task 13). Bu pragma; broadcaster CustomerSearch'te tüm bakiyeyi ister.

`SessionRepository`'nin VM'a injecte edilmesi gerek — ctor'a ekle:

```csharp
private readonly SessionRepository _sessions;
// ctor parametre listesine ekle
```

App.xaml.cs DI registration:

```csharp
services.AddSingleton<CustomerSearchViewModel>(sp => new CustomerSearchViewModel(
    sp.GetRequiredService<CustomerRepository>(),
    sp.GetRequiredService<CustomerService>(),
    sp.GetRequiredService<PaymentRequestService>(),
    sp.GetRequiredService<IDialogService>(),
    sp.GetRequiredService<SessionRepository>()
    /* + mevcut paramlar */));
```

- [ ] **Step 6: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~CustomerSearchViewModel"
```

Beklenen: 3 yeni test PASS + mevcut Phase 3a/4f testleri PASS.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.App/Services/IDialogService.cs \
        LiveDeck.App/Services/WpfDialogService.cs \
        LiveDeck.App/ViewModels/CustomerSearchViewModel.cs \
        LiveDeck.App/App.xaml.cs \
        LiveDeck.Tests/Fakes/FakeDialogService.cs \
        LiveDeck.Tests/ViewModels/CustomerSearchViewModelTests.cs

git commit -m "feat(app): Phase 4g — CustomerSearchViewModel filter + WhatsApp command

- IDialogService + WpfDialogService production / FakeDialogService test
- LastStreamShoppersOnly checkbox observable → RefreshSearch on toggle
- ApplySearch branches to CustomerService.GetLastStreamShoppers when filter on
- OpenWhatsAppAsync: PaymentRequestService → PhoneRequired flow opens
  PhoneEntryDialog and retries; LaunchFailed shows error.
- 3 new VM tests."
```

---

### Task 13: CustomerSearchDialog.xaml — Checkbox + WhatsApp column

**Files:**
- Modify: `LiveDeck.App/Views/CustomerSearchDialog.xaml`

**Context:** Mevcut Platform filter'ın yanına checkbox + DataGrid'e en sağa WhatsApp kolonu. `Amount` kolonu eklenmediyse opsiyonel olarak filter aktifken görünür yap.

- [ ] **Step 1: Mevcut filter satırını bul ve checkbox ekle**

`LiveDeck.App/Views/CustomerSearchDialog.xaml` içinde mevcut Platform filter ComboBox/RadioButton'ların yanına ekle:

```xml
<CheckBox Content="Son yayından alışveriş yapanlar"
          IsChecked="{Binding LastStreamShoppersOnly}"
          Margin="12,0,0,0" VerticalAlignment="Center"/>
```

Filter row genelde StackPanel `Orientation="Horizontal"` içinde — aynı StackPanel'e en sağa eklenebilir.

- [ ] **Step 2: DataGrid sonuna WhatsApp kolonu ekle**

Mevcut DataGrid `<DataGrid.Columns>` bloğu içinde, son `DataGridTextColumn`'un altına:

```xml
<DataGridTemplateColumn Header="WhatsApp" Width="80">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <Button Content="📱"
                    ToolTip="WhatsApp'tan ödeme iste"
                    Command="{Binding DataContext.OpenWhatsAppCommand,
                             RelativeSource={RelativeSource AncestorType=Window}}"
                    CommandParameter="{Binding}"
                    Background="#25D366" Foreground="White"
                    BorderThickness="0" Padding="6,2"
                    HorizontalAlignment="Center"/>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

`RelativeSource={RelativeSource AncestorType=Window}` Phase 4f'de kullanıldı; `Window` yerine eğer dialog `Page` veya başka tip ise uygun ancestor seç.

**Not:** Mevcut DataGrid'in `ItemsSource="{Binding Results}"` ve her satır `Customer` bind ediyor olduğunu doğrula. `CommandParameter="{Binding}"` o satırın `Customer` instance'ını VM'ya geçer.

- [ ] **Step 3: Build doğrula**

```bash
dotnet build LiveDeck.App/LiveDeck.App.csproj
```

Beklenen: 0 error.

- [ ] **Step 4: Commit**

```bash
git add LiveDeck.App/Views/CustomerSearchDialog.xaml

git commit -m "feat(app): Phase 4g — CustomerSearchDialog filter + WhatsApp column

CheckBox 'Son yayından alışveriş yapanlar' bound to LastStreamShoppersOnly.
DataGrid template column with green WhatsApp button per row, bound to
OpenWhatsAppCommand on the Window's DataContext (CustomerSearchViewModel)."
```

---

### Task 14: StreamReportViewModel — OpenWhatsApp command

**Files:**
- Modify: `LiveDeck.App/ViewModels/StreamReportViewModel.cs`
- Modify or Create: `LiveDeck.Tests/ViewModels/StreamReportViewModelTests.cs`

**Context:** `TopCustomer` (Username, Platform, LabelCount, TotalAmount) için per-stream amount HAZIR. Customer hidrate gerekli (Phone field için). Service çağrısı + dialog akışı CustomerSearchViewModel ile aynı pattern.

- [ ] **Step 1: Test ekle (FAIL beklenir)**

`LiveDeck.Tests/ViewModels/StreamReportViewModelTests.cs` (yoksa oluştur):

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiveDeck.App.Services;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.Fakes;
using LiveDeck.Tests.Storage;
using Xunit;

namespace LiveDeck.Tests.ViewModels;

public class StreamReportViewModel_OpenWhatsAppTests
{
    [Fact]
    public async Task OpenWhatsApp_ValidPhone_LaunchesWithPerStreamAmount()
    {
        using var db = TestDb.Create();
        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);
        var giveaways = new GiveawayRepository(db);

        var alice = new Customer("c1", "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, "+905551111111");
        customers.Insert(alice);
        sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
        labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 75m, 110, 120));
        sessions.End("s1", 200);

        var settingsPath = Path.GetTempFileName();
        var settingsStore = new SettingsStore(settingsPath);
        settingsStore.Save(new AppSettings());
        var launcher = new FakeUrlLauncher();
        var paymentService = new PaymentRequestService(
            settingsStore, new WhatsAppMessageBuilder(), launcher);
        var dialogs = new FakeDialogService();

        var sut = new StreamReportViewModel(labels, sessions, giveaways, customers,
            paymentService, dialogs);
        sut.Load("s1");

        var topCustomer = sut.TopCustomers[0];
        await sut.OpenWhatsAppCommand.ExecuteAsync(topCustomer);

        launcher.LaunchedUrls.Should().HaveCount(1);
        // Per-stream amount 75 TL → URL'de "75%2C00" (decimal comma URL-encoded)
        launcher.LaunchedUrls[0].Should().Contain("75%2C00");

        File.Delete(settingsPath);
    }

    [Fact]
    public async Task OpenWhatsApp_PhoneRequired_OpensDialog()
    {
        using var db = TestDb.Create();
        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);
        var giveaways = new GiveawayRepository(db);

        customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, null));
        sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
        labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 75m, 110, 120));
        sessions.End("s1", 200);

        var settingsPath = Path.GetTempFileName();
        var settingsStore = new SettingsStore(settingsPath);
        settingsStore.Save(new AppSettings());
        var paymentService = new PaymentRequestService(
            settingsStore, new WhatsAppMessageBuilder(), new FakeUrlLauncher());
        var dialogs = new FakeDialogService { PhoneEntryResult = _ => false };

        var sut = new StreamReportViewModel(labels, sessions, giveaways, customers,
            paymentService, dialogs);
        sut.Load("s1");

        await sut.OpenWhatsAppCommand.ExecuteAsync(sut.TopCustomers[0]);

        dialogs.PhoneEntryShownFor.Should().ContainSingle().Which.Should().Be("c1");
        File.Delete(settingsPath);
    }
}
```

**Not:** Mevcut `StreamReportViewModel` ctor signature `(LabelRepository, SessionRepository, GiveawayRepository)` (özet'te referans verilmişti). Yeni paramlar (`CustomerRepository`, `PaymentRequestService`, `IDialogService`) sona eklenir.

- [ ] **Step 2: Test FAIL doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~StreamReportViewModel_OpenWhatsApp"
```

Beklenen: FAIL (derleme — yeni paramlar yok).

- [ ] **Step 3: ViewModel'e command ekle**

`LiveDeck.App/ViewModels/StreamReportViewModel.cs`:

```csharp
private readonly CustomerRepository _customers;
private readonly PaymentRequestService _paymentService;
private readonly IDialogService _dialogService;

public StreamReportViewModel(
    LabelRepository labels,
    SessionRepository sessions,
    GiveawayRepository giveaways,
    CustomerRepository customers,
    PaymentRequestService paymentService,
    IDialogService dialogService)
{
    _labels = labels;
    _sessions = sessions;
    _giveaways = giveaways;
    _customers = customers;
    _paymentService = paymentService;
    _dialogService = dialogService;
}

private string? _currentSessionId;
private DateTime _currentSessionDate;

public void Load(string sessionId)
{
    _currentSessionId = sessionId;
    var session = _sessions.GetById(sessionId);
    _currentSessionDate = session?.EndedAt is long ended
        ? DateTimeOffset.FromUnixTimeSeconds(ended).LocalDateTime
        : DateTime.Now;

    // ... mevcut Load logic (totals + TopCustomers populate)
}

[RelayCommand]
private async Task OpenWhatsAppAsync(TopCustomer? topCustomer)
{
    if (topCustomer is null) return;

    var customer = _customers.FindByPlatformAndUsername(
        topCustomer.Platform, topCustomer.Username);
    if (customer is null)
    {
        _dialogService.ShowError("Müşteri kaydı bulunamadı.");
        return;
    }

    var result = _paymentService.OpenWhatsApp(
        customer, topCustomer.TotalAmount, _currentSessionDate);

    if (result == PaymentRequestResult.PhoneRequired)
    {
        var saved = _dialogService.ShowPhoneEntryDialog(customer.Id);
        if (saved)
        {
            var updated = _customers.GetById(customer.Id);
            if (updated is not null)
                _paymentService.OpenWhatsApp(
                    updated, topCustomer.TotalAmount, _currentSessionDate);
        }
    }
    else if (result == PaymentRequestResult.LaunchFailed)
    {
        _dialogService.ShowError("WhatsApp açılamadı. WhatsApp Desktop kurulu mu?");
    }
}
```

**StreamReport per-stream amount:** `TopCustomer.TotalAmount` zaten o session'a özel (LabelRepository.GetTopCustomersBySession join+sum) — CustomerSearch'tekinden farklı olarak burada doğru per-stream amount kullanılıyor.

`Load` metodu zaten varsa, sadece `_currentSessionId` + `_currentSessionDate` field set'lerini ekle (mevcut `Load` logic'ini koru).

- [ ] **Step 4: App.xaml.cs DI güncelle**

`StreamReportViewModel` registration:

```csharp
services.AddTransient<StreamReportViewModel>();
```

(DI auto-resolve ctor — yeni paramlar otomatik resolve olur, manuel factory gerekmez.)

- [ ] **Step 5: Test PASS doğrula**

```bash
dotnet test LiveDeck.Tests/LiveDeck.Tests.csproj --filter "FullyQualifiedName~StreamReportViewModel"
```

Beklenen: 2 yeni test PASS.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.App/ViewModels/StreamReportViewModel.cs \
        LiveDeck.App/App.xaml.cs \
        LiveDeck.Tests/ViewModels/StreamReportViewModelTests.cs

git commit -m "feat(app): Phase 4g — StreamReportViewModel WhatsApp command

OpenWhatsAppAsync(TopCustomer): hydrate Customer via CustomerRepository,
call PaymentRequestService with per-stream amount (TopCustomer.TotalAmount).
PhoneRequired flow opens PhoneEntryDialog and retries. 2 unit tests."
```

---

### Task 15: StreamReportDialog.xaml — WhatsApp column

**Files:**
- Modify: `LiveDeck.App/Views/StreamReportDialog.xaml`

**Context:** TopCustomers DataGrid'ine satır başına WhatsApp ikonu kolonu — Task 13 ile aynı pattern.

- [ ] **Step 1: TopCustomers DataGrid sonuna kolon ekle**

`LiveDeck.App/Views/StreamReportDialog.xaml` içinde TopCustomers DataGrid'in `<DataGrid.Columns>` bloğuna en sona ekle:

```xml
<DataGridTemplateColumn Header="WhatsApp" Width="80">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <Button Content="📱"
                    ToolTip="WhatsApp'tan ödeme iste"
                    Command="{Binding DataContext.OpenWhatsAppCommand,
                             RelativeSource={RelativeSource AncestorType=Window}}"
                    CommandParameter="{Binding}"
                    Background="#25D366" Foreground="White"
                    BorderThickness="0" Padding="6,2"
                    HorizontalAlignment="Center"/>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

`CommandParameter="{Binding}"` her satırın `TopCustomer` instance'ını VM'a geçer.

- [ ] **Step 2: Build doğrula**

```bash
dotnet build LiveDeck.App/LiveDeck.App.csproj
```

Beklenen: 0 error.

- [ ] **Step 3: Commit**

```bash
git add LiveDeck.App/Views/StreamReportDialog.xaml

git commit -m "feat(app): Phase 4g — StreamReportDialog WhatsApp column

DataGridTemplateColumn with green WhatsApp button bound to OpenWhatsAppCommand.
Per-row CommandParameter passes TopCustomer to VM."
```

---

### Task 16: LicenseServer FormSubmission.Phone + EF migration 008

**Files:**
- Modify: `LiveDeck.LicenseServer/Domain/IntakeFormSubmission.cs`
- Modify: `LiveDeck.LicenseServer/Data/LicenseDbContext.cs` (Fluent config — opsiyonel: MaxLength)
- Create (auto): `LiveDeck.LicenseServer/Data/Migrations/{ts}_AddSubmissionPhone.cs` — `dotnet ef migrations add` ile

**Context:** EF Core `Add-Migration AddSubmissionPhone -StartupProject LiveDeck.LicenseServer` komutuyla auto-generated. NULL kolonu (mevcut kayıtlarda phone yok); yeni kayıtlarda zorunlu (Razor PageModel `[Required]`). Prod runtime'da migration apply Phase 4e/4f pattern: `dbContext.Database.Migrate()` Program.cs'de hazır.

- [ ] **Step 1: IntakeFormSubmission entity'ye Phone ekle**

`LiveDeck.LicenseServer/Domain/IntakeFormSubmission.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

public sealed class IntakeFormSubmission
{
    public Guid Id { get; set; }
    public Guid IntakeFormConfigId { get; set; }
    public IntakeFormConfig Config { get; set; } = null!;
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Phone { get; set; }   // Phase 4g — E.164 format
    public DateTimeOffset SubmittedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
```

- [ ] **Step 2: LicenseDbContext fluent config (opsiyonel — MaxLength)**

`LiveDeck.LicenseServer/Data/LicenseDbContext.cs` `OnModelCreating` içinde `IntakeFormSubmission` config'ine ekle:

```csharp
modelBuilder.Entity<IntakeFormSubmission>(e =>
{
    // ... mevcut config (Username, FullName, Address)
    e.Property(x => x.Phone).HasMaxLength(20);   // Phase 4g
});
```

- [ ] **Step 3: EF migration generate**

```bash
cd LiveDeck.LicenseServer
dotnet ef migrations add AddSubmissionPhone -o Data/Migrations
cd ..
```

Beklenen: `LiveDeck.LicenseServer/Data/Migrations/{timestamp}_AddSubmissionPhone.cs` + `Designer.cs` + güncellenmiş `LicenseDbContextModelSnapshot.cs`. Migration `Up` `AddColumn<string>("Phone", "FormSubmissions", maxLength: 20, nullable: true)` içerecek.

- [ ] **Step 4: Migration smoke test**

`LiveDeck.LicenseServer.Tests/Data/MigrationTests.cs` (yoksa Phase 4f mevcut helper'ı kullan — Phase 4f'de benzer test var):

```csharp
[Fact]
public async Task Migration_AddSubmissionPhone_AddsPhoneColumn()
{
    using var factory = new LicenseServerWebApplicationFactory();
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

    // EF Core InMemory provider migration'ları çalıştırmaz — model snapshot kontrolü:
    var entityType = db.Model.FindEntityType(typeof(IntakeFormSubmission));
    var phoneProperty = entityType!.FindProperty("Phone");
    phoneProperty.Should().NotBeNull();
    phoneProperty!.IsNullable.Should().BeTrue();
    phoneProperty.GetMaxLength().Should().Be(20);
}
```

(Eğer Phase 4f testleri SQL Server LocalDB'de migration çalıştırıyorsa, o pattern'i izle. InMemory için yukarıdaki model assertion yeterli.)

- [ ] **Step 5: Test PASS doğrula**

```bash
dotnet test LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~AddSubmissionPhone"
```

Beklenen: 1 test PASS.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.LicenseServer/Domain/IntakeFormSubmission.cs \
        LiveDeck.LicenseServer/Data/LicenseDbContext.cs \
        LiveDeck.LicenseServer/Data/Migrations/ \
        LiveDeck.LicenseServer.Tests/Data/MigrationTests.cs

git commit -m "feat(license-server): Phase 4g — IntakeFormSubmission.Phone + EF migration 008

- Phone string? property (E.164 format, MaxLength 20, nullable)
- EF migration AddSubmissionPhone (auto-generated)
- Model snapshot updated
- 1 unit test asserting model has Phone property"
```

---

### Task 17: IntakeForm.cshtml — Phone field + validation

**Files:**
- Modify: `LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml`
- Modify: `LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs`
- Modify: `LiveDeck.LicenseServer/Services/IntakeForm/PhoneNormalizer.cs` (yeni — Core'dan port veya yeniden yaz)
- Modify: Phase 4f mevcut form REST endpoint DTO (`IntakeFormSubmitDto` veya benzer)

**Context:** Razor Page'e zorunlu Phone alanı. Server-side `PhoneNormalizer.NormalizeTr` validate; null ise ModelState error. Geçerliyse `IntakeFormSubmission.Phone = normalized`.

**ÖNEMLİ — Code reuse:** `LiveDeck.Core.Customers.PhoneNormalizer` LicenseServer'a referans veremez (LicenseServer Core'a bağımlı değil). Pratik: Aynı static class'ı `LiveDeck.LicenseServer/Services/IntakeForm/PhoneNormalizer.cs` olarak duplicate et (DRY ihlali kabul; cross-project shared lib YAGNI).

- [ ] **Step 1: LicenseServer'da PhoneNormalizer port et**

`LiveDeck.LicenseServer/Services/IntakeForm/PhoneNormalizer.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Phase 4g: TR mobil telefonu E.164 formatına normalize eder.
/// (LiveDeck.Core.Customers.PhoneNormalizer kopyası — projeler arası shared lib YAGNI.)
/// </summary>
public static class PhoneNormalizer
{
    public static string? NormalizeTr(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length == 12 && digits.StartsWith("90")) return "+" + digits;
        if (digits.Length == 11 && digits.StartsWith("0")) return "+90" + digits.Substring(1);
        if (digits.Length == 10) return "+90" + digits;
        return null;
    }

    public static bool IsValidTr(string? e164)
        => !string.IsNullOrEmpty(e164)
           && e164.StartsWith("+90")
           && e164.Length == 13
           && e164.Substring(1).All(char.IsDigit);
}
```

- [ ] **Step 2: PageModel binding model'a Phone ekle**

`LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs` içinde `IntakeFormInput` (veya mevcut adı) record/class'a:

```csharp
public sealed class IntakeFormInput
{
    [Required(ErrorMessage = "Kullanıcı adı zorunlu.")]
    [StringLength(50)]
    public string Username { get; set; } = "";

    [Required(ErrorMessage = "Ad Soyad zorunlu.")]
    [StringLength(100)]
    public string FullName { get; set; } = "";

    [Required(ErrorMessage = "Adres zorunlu.")]
    [StringLength(500)]
    public string Address { get; set; } = "";

    [Required(ErrorMessage = "WhatsApp numarası zorunlu.")]
    [StringLength(20)]
    public string Phone { get; set; } = "";   // Phase 4g
}
```

`OnPostAsync` içinde validation + service çağrısı:

```csharp
public async Task<IActionResult> OnPostAsync(string slug)
{
    if (!ModelState.IsValid)
    {
        // re-render form with errors
        return await OnGetAsync(slug);
    }

    // Phase 4g — phone normalize
    var normalizedPhone = PhoneNormalizer.NormalizeTr(Form.Phone);
    if (normalizedPhone is null)
    {
        ModelState.AddModelError("Form.Phone", "Geçersiz telefon numarası. 10 haneli TR mobil numara girin.");
        return await OnGetAsync(slug);
    }

    var result = await _intakeFormService.SubmitAsync(
        slug, Form.Username, Form.FullName, Form.Address, normalizedPhone,
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString());

    // ... mevcut redirect/success logic
}
```

- [ ] **Step 3: IntakeFormService SubmitAsync signature'ına Phone ekle**

`LiveDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs`:

```csharp
public async Task<IntakeFormSubmitResult> SubmitAsync(
    string slug, string username, string fullName, string address,
    string normalizedPhone,   // Phase 4g
    string? ipAddress, string? userAgent)
{
    // ... mevcut config lookup, honeypot/rate-limit
    var submission = new IntakeFormSubmission
    {
        Id = Guid.NewGuid(),
        IntakeFormConfigId = config.Id,
        Username = username,
        FullName = fullName,
        Address = address,
        Phone = normalizedPhone,   // Phase 4g
        SubmittedAt = DateTimeOffset.UtcNow,
        IpAddress = ipAddress,
        UserAgent = userAgent
    };
    _db.FormSubmissions.Add(submission);
    await _db.SaveChangesAsync();
    return ...;
}
```

- [ ] **Step 4: WhatsAppLinkBuilder mesajına Phone ekleme YOK**

`LiveDeck.LicenseServer/Services/IntakeForm/WhatsAppLinkBuilder.cs` mevcut `Build(phone, username, fullName, address)` çağrısı kalır — broadcaster'a giden mesajda phone GEREKLI değil (broadcaster zaten kendi numarası ile alır). Sadece DB'ye kaydet.

Ancak mesaj içeriğine eklemek istenirse template'i güncelle:

```csharp
var message = $"Kullanıcı adı: {username}\nAd Soyad: {fullName}\nAdres: {address}\nTelefon: {phone}";
```

(Karar: ekle — broadcaster mesajda görsün.) Builder `Build` imzasına `phone` ekle:

```csharp
public string Build(string e164PhoneTo, string username, string fullName, string address, string phoneFromCustomer)
{
    var phone = e164PhoneTo.Replace("+", "").Replace(" ", "").Replace("-", "");
    var message = $"Kullanıcı adı: {username}\nAd Soyad: {fullName}\nAdres: {address}\nTelefon: {phoneFromCustomer}";
    var encoded = Uri.EscapeDataString(message);
    return $"https://wa.me/{phone}?text={encoded}";
}
```

PageModel'da mesaj build'inde phone parametresini geç.

- [ ] **Step 5: Razor view'a Phone input ekle**

`LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml` içinde Address input'ının altına:

```html
<div class="mb-3">
    <label class="form-label">WhatsApp Numarası <span class="text-danger">*</span></label>
    <input type="tel" name="Form.Phone" class="form-control"
           placeholder="555 123 45 67" required maxlength="20"
           value="@Model.Form.Phone"/>
    <div class="form-text">+90 otomatik eklenecek. 10 haneli mobil numarayı girin.</div>
    <span asp-validation-for="Form.Phone" class="text-danger"></span>
</div>
```

- [ ] **Step 6: Phone validation testleri**

`LiveDeck.LicenseServer.Tests/Pages/IntakeFormPhoneTests.cs`:

```csharp
using System.Net;
using System.Net.Http;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.Infrastructure;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public class IntakeFormPhoneTests : IClassFixture<LicenseServerWebApplicationFactory>
{
    private readonly LicenseServerWebApplicationFactory _factory;

    public IntakeFormPhoneTests(LicenseServerWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Post_PhoneRequired_ReturnsBadRequestWithError()
    {
        var client = _factory.CreateClient();
        var slug = await IntakeFormTestHelper.SeedActiveConfigAsync(_factory);

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Form.Username", "alice"),
            new KeyValuePair<string, string>("Form.FullName", "Alice X"),
            new KeyValuePair<string, string>("Form.Address", "Adres 1"),
            // Phone missing
        });
        var response = await client.PostAsync($"/r/{slug}", content);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("WhatsApp numarası zorunlu");
    }

    [Fact]
    public async Task Post_InvalidPhone_ReturnsValidationError()
    {
        var client = _factory.CreateClient();
        var slug = await IntakeFormTestHelper.SeedActiveConfigAsync(_factory);

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Form.Username", "alice"),
            new KeyValuePair<string, string>("Form.FullName", "Alice X"),
            new KeyValuePair<string, string>("Form.Address", "Adres 1"),
            new KeyValuePair<string, string>("Form.Phone", "abc"),
        });
        var response = await client.PostAsync($"/r/{slug}", content);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Geçersiz telefon numarası");
    }

    [Fact]
    public async Task Post_ValidPhone_NormalizesAndPersistsE164()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", "test");
        var slug = await IntakeFormTestHelper.SeedActiveConfigAsync(_factory);

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Form.Username", "alice"),
            new KeyValuePair<string, string>("Form.FullName", "Alice X"),
            new KeyValuePair<string, string>("Form.Address", "Adres 1"),
            new KeyValuePair<string, string>("Form.Phone", "5551234567"),
        });
        var response = await client.PostAsync($"/r/{slug}", content);
        // 302 redirect veya 200 OK + WhatsApp link page (Phase 4f pattern)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Redirect, HttpStatusCode.Found);

        // DB'de E.164 olarak kaydedilmeli
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var submission = db.FormSubmissions.OrderByDescending(s => s.SubmittedAt).First();
        submission.Phone.Should().Be("+905551234567");
    }
}
```

- [ ] **Step 7: PhoneNormalizer unit testleri (LicenseServer.Tests)**

`LiveDeck.LicenseServer.Tests/Services/IntakeForm/PhoneNormalizerTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services.IntakeForm;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("5551234567", "+905551234567")]
    [InlineData("05551234567", "+905551234567")]
    [InlineData("+905551234567", "+905551234567")]
    [InlineData("0 555 123-45-67", "+905551234567")]
    public void NormalizeTr_AcceptsCommonFormats(string input, string expected)
        => PhoneNormalizer.NormalizeTr(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("123")]
    public void NormalizeTr_RejectsInvalid(string? input)
        => PhoneNormalizer.NormalizeTr(input).Should().BeNull();
}
```

- [ ] **Step 8: Tüm LicenseServer testleri PASS**

```bash
dotnet test LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj
```

Beklenen: 3 + 6 = 9 yeni test PASS, mevcut Phase 4f testleri Task 18'de güncellenecek.

- [ ] **Step 9: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml \
        LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs \
        LiveDeck.LicenseServer/Services/IntakeForm/PhoneNormalizer.cs \
        LiveDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs \
        LiveDeck.LicenseServer/Services/IntakeForm/WhatsAppLinkBuilder.cs \
        LiveDeck.LicenseServer.Tests/Pages/IntakeFormPhoneTests.cs \
        LiveDeck.LicenseServer.Tests/Services/IntakeForm/PhoneNormalizerTests.cs

git commit -m "feat(license-server): Phase 4g — IntakeForm Phone field + validation

- IntakeFormInput.Phone [Required] field
- Server-side PhoneNormalizer.NormalizeTr (port from Core; cross-project shared lib YAGNI)
- ModelState error on null normalization
- IntakeFormSubmission.Phone persisted as E.164
- WhatsAppLinkBuilder includes Telefon line in broadcaster message
- Razor input with placeholder hint
- 9 new tests (3 page POST tests + 6 PhoneNormalizer)"
```

---

### Task 18: Phase 4f mevcut form testlerine Phone parametresi ekle

**Files:**
- Modify: `LiveDeck.LicenseServer.Tests/Pages/IntakeFormTests.cs` (mevcut Phase 4f testleri)
- Modify: `LiveDeck.LicenseServer.Tests/Services/IntakeForm/IntakeFormServiceTests.cs` (mevcut)

**Context:** Phase 4f form testleri POST body'sinde `Phone` parametresi yok — yeni `[Required]` ekleyince hepsi 400 dönecek. Her POST helper'a `Form.Phone=5551234567` ekle.

- [ ] **Step 1: Mevcut form POST testlerini bul**

```bash
grep -n "Form.Username\|Form.FullName\|Form.Address" LiveDeck.LicenseServer.Tests/Pages/IntakeFormTests.cs
```

Mevcut tüm `FormUrlEncodedContent` builder'larına `("Form.Phone", "5551234567")` ekle.

- [ ] **Step 2: Her POST testindeki body'ye Phone ekle**

Pattern (her test için):

```csharp
var content = new FormUrlEncodedContent(new[]
{
    new KeyValuePair<string, string>("Form.Username", "alice"),
    new KeyValuePair<string, string>("Form.FullName", "Alice X"),
    new KeyValuePair<string, string>("Form.Address", "Adres 1"),
    new KeyValuePair<string, string>("Form.Phone", "5551234567"),   // Phase 4g — required
});
```

Phase 4f IntakeFormTests'te yaklaşık 10-15 POST testi olabilir (success, honeypot, rate-limit, slug invalid, vb.).

- [ ] **Step 3: IntakeFormService.SubmitAsync test'lerinde Phone ekle**

`IntakeFormServiceTests.cs` içinde `SubmitAsync` çağrılarına `"+905551234567"` ekle (Service signature Task 17'de değişti).

- [ ] **Step 4: Tüm LicenseServer testleri PASS**

```bash
dotnet test LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj
```

Beklenen: tüm Phase 4f testleri yeniden PASS + Phase 4g 9 test PASS.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer.Tests/Pages/IntakeFormTests.cs \
        LiveDeck.LicenseServer.Tests/Services/IntakeForm/IntakeFormServiceTests.cs

git commit -m "test(license-server): Phase 4g — Phase 4f form tests Phone regression

All Phase 4f form POST tests get Form.Phone=5551234567 in body.
SubmitAsync test calls receive normalized E.164 phone parameter."
```

---

### Task 19: IntakeFormSyncService — Propagate Phone to Customer.Phone

**Files:**
- Modify: `LiveDeck.Licensing` SDK — `IntakeFormSubmissionDto` (yeni Phone alanı)
- Modify: `LiveDeck.App/Services/IntakeFormSyncService.cs` (UpsertFromIntakeForm çağrısında Phone geç)
- Modify: `LiveDeck.Licensing/LicenseApiClient.cs` (polling endpoint cevabını Phone'lu okur — JSON deserialize otomatik)
- Modify: `LiveDeck.Licensing.Tests/LicenseApiClient_IntakeFormTests.cs` (polling cevabına Phone alanı)

**Context:** Phase 4f sync `2dk PeriodicTimer` ile cursor-based polling yapıyor. DTO'ya Phone alanı eklenir (LicenseServer JSON yanıtında zaten var olacak — controller `IntakeFormSubmission` direkt veya AutoMapper). Sync service `UpsertFromIntakeForm` çağrısında `submission.Phone` geçer.

- [ ] **Step 1: IntakeFormSubmissionDto'ya Phone ekle**

`LiveDeck.Licensing/Models/IntakeFormSubmissionDto.cs` (mevcut Phase 4f):

```csharp
public sealed record IntakeFormSubmissionDto(
    Guid Id,
    string Username,
    string FullName,
    string Address,
    string? Phone,    // Phase 4g
    DateTimeOffset SubmittedAt);
```

(Eğer mevcut DTO `class` ise property ekle; mevcut serialize convention'a uy.)

- [ ] **Step 2: LicenseServer controller cevabına Phone ekle**

`LiveDeck.LicenseServer/Controllers/IntakeFormController.cs` (veya `Endpoints/IntakeFormEndpoints.cs`) içinde polling endpoint cevabını Map et:

```csharp
[HttpGet("/api/intake-forms/submissions")]
public async Task<IActionResult> GetSince(DateTimeOffset? since)
{
    var customerId = /* mevcut auth claim */;
    var query = _db.FormSubmissions
        .Include(s => s.Config)
        .Where(s => s.Config.CustomerId == customerId);
    if (since.HasValue) query = query.Where(s => s.SubmittedAt > since.Value);

    var submissions = await query
        .OrderBy(s => s.SubmittedAt)
        .Select(s => new
        {
            id = s.Id,
            username = s.Username,
            fullName = s.FullName,
            address = s.Address,
            phone = s.Phone,    // Phase 4g
            submittedAt = s.SubmittedAt
        })
        .ToListAsync();

    return Ok(submissions);
}
```

(Mevcut endpoint Phase 4f'de implementte; sadece projection'a `phone` field ekle.)

- [ ] **Step 3: LicenseApiClient polling testi güncelle**

`LiveDeck.Licensing.Tests/LicenseApiClient_IntakeFormTests.cs` mevcut polling testlerinde mock JSON cevabına `phone` alanı ekle:

```csharp
var mockJson = """
[
  {
    "id": "00000000-0000-0000-0000-000000000001",
    "username": "alice",
    "fullName": "Alice X",
    "address": "Adres 1",
    "phone": "+905551111111",
    "submittedAt": "2026-04-30T10:00:00+03:00"
  }
]
""";
// ... mevcut test setup
var dtos = await client.GetIntakeFormSubmissionsAsync(since: null);
dtos.Should().HaveCount(1);
dtos[0].Phone.Should().Be("+905551111111");
```

- [ ] **Step 4: IntakeFormSyncService Phone'u Upsert'a geçir**

`LiveDeck.App/Services/IntakeFormSyncService.cs` içinde:

```csharp
foreach (var dto in submissions)
{
    _customers.UpsertFromIntakeForm(
        dto.Username,
        dto.FullName,
        dto.Address,
        dto.Phone,        // Phase 4g — Task 1'de null geçiliyordu, şimdi propagate
        nowUnix);
}
```

(Task 1 Step 8'de `null` geçen yer şimdi `dto.Phone` olarak değişir.)

- [ ] **Step 5: Sync end-to-end smoke test**

`LiveDeck.Tests/Services/IntakeFormSyncServiceTests.cs` mevcut Phase 4f testine ek:

```csharp
[Fact]
public async Task Sync_PropagatesPhoneFromDtoToCustomer()
{
    using var db = TestDb.Create();
    var customers = new CustomerRepository(db);

    var fakeApi = new FakeLicenseApiClient();
    fakeApi.SubmissionsToReturn = new List<IntakeFormSubmissionDto>
    {
        new(Guid.NewGuid(), "alice", "Alice X", "Adres 1", "+905551111111",
            DateTimeOffset.UtcNow)
    };
    var settingsStore = new SettingsStore(Path.GetTempFileName());
    settingsStore.Save(new AppSettings());

    var sut = new IntakeFormSyncService(fakeApi, customers, settingsStore, new TestClock());
    await sut.SyncAsync();

    var customer = customers.FindByPlatformAndUsername("form", "alice");
    customer!.Phone.Should().Be("+905551111111");
}
```

(Mevcut `FakeLicenseApiClient` Phase 4f test infrastructure'ında var — `SubmissionsToReturn` field ekle.)

- [ ] **Step 6: Tüm testler PASS**

```bash
dotnet test
```

Beklenen: tüm projeler 0 fail.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.Licensing/Models/IntakeFormSubmissionDto.cs \
        LiveDeck.LicenseServer/Controllers/IntakeFormController.cs \
        LiveDeck.App/Services/IntakeFormSyncService.cs \
        LiveDeck.Licensing.Tests/LicenseApiClient_IntakeFormTests.cs \
        LiveDeck.Tests/Services/IntakeFormSyncServiceTests.cs

git commit -m "feat(sync): Phase 4g — propagate intake form Phone to Customer.Phone

DTO + endpoint projection + sync upsert thread Phone end-to-end. Form-platform
customers' WhatsApp phone auto-populates on next 2-min poll."
```

---

### Task 20: App.xaml.cs DI registrations + final wiring

**Files:**
- Modify: `LiveDeck.App/App.xaml.cs`

**Context:** Yeni servisler/VM'ler için DI registration. Phase 4g'de eklenenler: `WhatsAppMessageBuilder`, `IUrlLauncher`/`ProcessUrlLauncher`, `PaymentRequestService`, `IDialogService`/`WpfDialogService`, `PhoneEntryDialogViewModel` (transient — her dialog için yeni).

- [ ] **Step 1: DI registrations ekle**

`LiveDeck.App/App.xaml.cs` içinde `ConfigureServices` veya equivalent:

```csharp
// Phase 4g — payment request infrastructure
services.AddSingleton<WhatsAppMessageBuilder>();
services.AddSingleton<IUrlLauncher, ProcessUrlLauncher>();
services.AddSingleton<PaymentRequestService>();
services.AddSingleton<IDialogService, WpfDialogService>();
```

- [ ] **Step 2: CustomerSearchViewModel ve StreamReportViewModel registration kontrolü**

Bu VM'lar Task 12 ve Task 14'te DI'ya yeni paramlar aldı. Eğer `services.AddTransient<CustomerSearchViewModel>()` auto-resolve kullanılıyorsa, yeni paramlar otomatik resolve olur. Manuel factory varsa güncelle:

```csharp
services.AddTransient<CustomerSearchViewModel>();
services.AddTransient<StreamReportViewModel>();
services.AddTransient<SettingsViewModel>();
```

- [ ] **Step 3: PhoneEntryDialog factory (gerekirse)**

`PhoneEntryDialog` `new` ile `WpfDialogService` içinde yaratılıyor — DI'ya gerek yok (Task 9 Step 5 pattern).

- [ ] **Step 4: Tüm projelerde build doğrula**

```bash
dotnet build
```

Beklenen: 0 error, 0 warning (Phase 4f baseline).

- [ ] **Step 5: Tüm testler PASS**

```bash
dotnet test
```

Beklenen: ~470 test PASS, 0 fail.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.App/App.xaml.cs

git commit -m "feat(app): Phase 4g — DI registrations for payment infrastructure

Singletons: WhatsAppMessageBuilder, IUrlLauncher (ProcessUrlLauncher),
PaymentRequestService, IDialogService (WpfDialogService).
ViewModels auto-resolve through ctor injection."
```

---

### Task 21: Final verification + manual smoke test

**Files:** none (validation only)

**Context:** Spec section 9 (Test Strategy) ile aktüel test sayısı karşılaştırması. Manual smoke flow document'a yazılır (broadcaster perspective).

- [ ] **Step 1: Test count baseline ölçümü**

```bash
dotnet test --logger "console;verbosity=detailed" 2>&1 | grep -E "Passed|Failed|Skipped|total" | tail -10
```

Beklenen: 470 ± 5 test passed, 0 failed.

xUnit parallel mode tutarsızlığı varsa Phase 4f deneyimi:

```bash
dotnet test -- xunit.parallelizeAssembly=false xunit.parallelizeTestCollections=false
```

- [ ] **Step 2: Manual smoke senaryosu (release notu için)**

Aşağıdaki manuel akışları belgele (`docs/manual-smoke/2026-04-30-phase-4g.md`):

```markdown
# Phase 4g Manuel Smoke Akışı

## 1. Settings yapılandırma
- App'i aç → Ayarlar → "WhatsApp Ödeme İsteme" GroupBox
- Mesaj şablonunu varsayılan TR template'i ile bırak
- IBAN: TR12 0000 1111 2222 3333 (test)
- Hesap Sahibi: Test Kullanıcı
- Papara: 1234567
- Kaydet

## 2. Form müşterisi senaryosu (auto-phone)
- Phase 4f form linkine git: https://license.livedeck.app/r/{slug}
- Username: alice / FullName: Alice X / Address: Adres 1 / Phone: 555 123 45 67
- Tamamla
- 2 dakika bekle (sync poll)
- App'te CustomerSearch → "Son yayından alışveriş yapanlar" işaretle
- Alice satırında 📱 → tıkla
- WhatsApp Desktop açılmalı, mesaj pre-filled

## 3. Chat müşterisi senaryosu (manuel phone)
- Twitch yayınında "alice" alışveriş yaptı (label print)
- Yayın bitir → StreamReport
- TopCustomers'da alice → 📱 → tıkla
- PhoneEntryDialog açılır → "5551234567" → Kaydet ve Aç
- WhatsApp Desktop açılır

## 4. Hata akışları
- Geçersiz telefon ("abc") → inline error
- WhatsApp Desktop kurulu değil → web.whatsapp.com fallback (OS handler)
- Settings template boş → default template fallback
```

- [ ] **Step 3: Phase 4g spec coverage check**

Spec sections (1-13) ile plan tasks eşleşmesini doğrula:

| Spec section | Karşılayan task |
|--------------|-----------------|
| §3.1 İki giriş noktası | Task 12-13 (CustomerSearch) + Task 14-15 (StreamReport) |
| §3.2 Phone hibrit | Task 1 (field) + Task 17 (form) + Task 9 (PhoneEntryDialog) + Task 19 (sync) |
| §3.3 TR otomatik prefix | Task 2 (PhoneNormalizer) |
| §3.4 Mesaj şablonu | Task 3 (Builder) + Task 7 (Settings) |
| §3.5 wa.me deep-link | Task 3 (BuildWaMeLink) + Task 4 (IUrlLauncher) |
| §4.1 Migration 007 | Task 1 |
| §4.2 Migration 008 | Task 16 |
| §4.3 AppSettings | Task 7 |
| §5 New components | Tasks 2-9 |
| §6 UI Layer | Tasks 11, 13, 15, 17 |
| §7 ViewModel changes | Tasks 9, 10, 12, 14 |
| §8 Error handling | Task 8 (LaunchFailed catch), Task 9 (validation), Task 12 (LaunchFailed → MessageBox) |
| §9 Test strategy | Tüm tasklarda inline TDD |
| §10 YAGNI | None negative — sadece kapsam dışı |
| §11 File manifest | Tüm tasklarda Files: section |
| §12 Migration & rollout | Task 1 (idempotent), Task 16 (EF), Task 19 (sync propagation) |

- [ ] **Step 4: Final test sayım raporu**

Plan implementation tamamlandığında subagent şu mesaj ile rapor verir:

```
Phase 4g implementation complete.

Test counts:
- LiveDeck.Tests: 130 → ~163 (+33)
- LiveDeck.LicenseServer.Tests: 130 → ~140 (+10)
- LiveDeck.Licensing.Tests: 104 → ~105 (+1)
- TOTAL: 430 → ~470, 0 failing

Manual smoke documented: docs/manual-smoke/2026-04-30-phase-4g.md
```

- [ ] **Step 5: Phase 4g merge commit (optional)**

```bash
git log --oneline -25
git status
```

Tüm Phase 4g commit'leri master'da çizgisel; ek bir merge commit gerekli değil. (Worktree kullanılıyorsa branch merge yapılır.)

---

## Spec Coverage Summary

Tüm spec gereksinimleri tasklara bire-bir map edildi (Task 21 Step 3 tablosu). Hiçbir gap yok.

## Final Test Hedefi

| Proje | Baseline | Yeni | Toplam |
|-------|----------|------|--------|
| LiveDeck.Tests | 130 | +33 | 163 |
| LiveDeck.LicenseServer.Tests | 130 | +10 | 140 |
| LiveDeck.Licensing.Tests | 104 | +1 | 105 |
| **TOPLAM** | **430** | **+44** | **~470** |

---

**End of Phase 4g Implementation Plan.**

