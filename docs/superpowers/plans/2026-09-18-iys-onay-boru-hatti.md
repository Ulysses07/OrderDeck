# İYS onay boru hattı — uygulama planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Toplanan SMS onay/ret olaylarını İYS'ye üç iş günü dolmadan iletmek, sonucu ayrı bir adımda doğrulamak ve gönderimi bu doğrulanmış duruma bağlamak.

**Architecture:** Kaynak tablolar (`IntakeFormSubmission`, `Shopper`) onay yazarken aynı transaction içinde `IysConsent` (durum) + `IysConsentEvent` (ekle-only olay) yazar. Commit sonrası Hangfire `IysConsentPushJob` `/iys/add` çağırır; **ayrı** bir `IysConsentVerifyJob` `/iys/search` ile sonucu doğrular. Gönderim yolu `LastVerifiedStatus == Onay` değilse mesaj atmaz (fail-closed).

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server / InMemory), Hangfire, xUnit + FluentAssertions, Testcontainers (`SqlServerContainerFixture`).

**Spec:** `docs/superpowers/specs/2026-09-18-iys-onay-boru-hatti-design.md`

---

## Dosya yapısı

**Faz 1 — telefon normalizasyonu (önkoşul)**
- Değiştir: `OrderDeck.Core/Customers/PhoneNormalizer.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/Auth/PhoneNormalizer.cs`
- Değiştir: `OrderDeck.Tests/Customers/PhoneNormalizerTests.cs`
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Auth/PhoneNormalizerTests.cs`

**Faz 2 — veri modeli**
- Oluştur: `OrderDeck.LicenseServer/Domain/IysConsent.cs` (entity + 3 enum)
- Oluştur: `OrderDeck.LicenseServer/Domain/IysConsentEvent.cs`
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (DbSet + config)
- Oluştur: `OrderDeck.LicenseServer/Data/Migrations/*_AddIysConsent.cs` (EF üretir)

**Faz 3 — istemci, toplayıcı, işler**
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IysBusinessDays.cs`
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IIysClient.cs` (arayüz + DTO'lar + exception)
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/NetgsmIysClient.cs`
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/NullIysClient.cs` (Netgsm'siz ortam)
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IysConsentCollector.cs`
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs`
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs`
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IysConsentRecoveryJob.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/Sms/NetgsmOptions.cs` (BrandCode)
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (DI + recurring job)
- Değiştir: `OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs`
- Değiştir: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs`
- Değiştir: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs`

**Faz 4 — gönderim kontrolü + görünürlük**
- Değiştir: `OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs`
- Oluştur: `OrderDeck.LicenseServer/Pages/Admin/Iys/Index.cshtml{,.cs}`

**Testler**
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Iys/IysBusinessDaysTests.cs`
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Iys/NetgsmIysClientTests.cs`
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs`
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs`
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs`
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentUniqueIndexTests.cs` (Testcontainers)
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignIysGateTests.cs`

**İsim sözleşmesi (fazlar arası tutarlılık için — değiştirme):**
`IysConsentStatus { Unknown, Onay, Ret }` · `IysPushState { Pending, Pushed, Confirmed, Failed, Expired }` · `IysConsentEventType { LocalConsent, LocalRevoke, PushAttempt, SearchResult }` · `IIysClient.AddAsync/SearchAsync` · `IysConsentCollector.RecordAsync`

---

## Faz 1 — Telefon normalizasyonu (önkoşul)

Bozuk numara `IysConsent` tablosunda yanlış anahtar üretir; bu yüzden veri modelinden **önce** gelir.

### Task 1: Core normalizer — abone numarası `5` ile başlamalı

**Files:**
- Modify: `OrderDeck.Core/Customers/PhoneNormalizer.cs:15-41`
- Test: `OrderDeck.Tests/Customers/PhoneNormalizerTests.cs`

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.Tests/Customers/PhoneNormalizerTests.cs` içindeki geçersiz-girdi `[Theory]`'sine (şu an `[InlineData("+12025551234")]` ile biten blok) bu satırları ekle:

```csharp
    [InlineData("0533466482")]     // 9 hane + baştaki 0 → 10 hane sanılıyordu
    [InlineData("2125551234")]     // sabit hat (2 ile başlar), mobil değil
    [InlineData("05334664821")]    // 11 hane ama abone 3 ile başlıyor
    [InlineData("904334664821")]   // 12 hane ama abone 4 ile başlıyor
```

Ve `IsValidTr` `[Theory]`'sine:

```csharp
    [InlineData("+902125551234", false)]   // sabit hat E.164 uzunluğunda
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~PhoneNormalizerTests`
Expected: FAIL — `NormalizeTr("0533466482")` `"+900533466482"` döndürüyor, `null` bekleniyordu.

- [ ] **Step 3: Normalizer'ı düzelt**

`OrderDeck.Core/Customers/PhoneNormalizer.cs` içindeki `NormalizeTr` ve `IsValidTr` gövdelerini tamamen bununla değiştir:

```csharp
    /// <summary>
    /// "5551234567" / "05551234567" / "+90 555 123 45 67" → "+905551234567".
    /// Geçersiz/null/empty/yurt-dışı/mobil-olmayan → null.
    /// </summary>
    public static string? NormalizeTr(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var digits = new string(input.Where(char.IsDigit).ToArray());

        // Önce 10 haneli abone numarasını ayıkla, sonra TEK yerde doğrula.
        string? subscriber = null;
        if (digits.Length == 12 && digits.StartsWith("90")) subscriber = digits.Substring(2);
        else if (digits.Length == 11 && digits.StartsWith("0")) subscriber = digits.Substring(1);
        else if (digits.Length == 10) subscriber = digits;

        // TR mobil abone numarası daima 5 ile başlar. Bu kural olmadan
        // "0533466482" (9 hane + baştaki 0) 10 hane sayılıp "+900533466482"
        // üretiyordu; prod'da böyle bir kayıt var ve İYS'de geçersiz anahtar.
        if (subscriber is null || subscriber[0] != '5') return null;

        return "+90" + subscriber;
    }

    /// <summary>E.164 TR mobil kontrolü: "+90" + 10 digit, abone "5" ile başlar.</summary>
    public static bool IsValidTr(string? e164)
        => !string.IsNullOrEmpty(e164)
           && e164.StartsWith("+90")
           && e164.Length == 13
           && e164[3] == '5'
           && e164.Substring(1).All(char.IsDigit);
```

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~PhoneNormalizerTests`
Expected: PASS. Mevcut `555...` verili satırların hepsi geçmeye devam etmeli.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Core/Customers/PhoneNormalizer.cs OrderDeck.Tests/Customers/PhoneNormalizerTests.cs
git commit -m "fix(phone): TR mobil abone numarası 5 ile başlamalı (Core)"
```

### Task 2: Auth normalizer — aynı kural, aynı test kümesi

İki normalizer ayrı yazılmış; biri düzelip diğeri kalırsa delik kapanmaz.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Auth/PhoneNormalizer.cs:51-55`
- Test: `OrderDeck.LicenseServer.Tests/Services/Auth/PhoneNormalizerTests.cs`

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Auth/PhoneNormalizerTests.cs` geçersiz-girdi `[Theory]`'sine (şu an `[InlineData("5551112233444")]` ile biten blok) ekle:

```csharp
    [InlineData("0533466482")]     // 9 hane + baştaki 0
    [InlineData("2125551234")]     // sabit hat
    [InlineData("+902125551234")]  // sabit hat, E.164
    [InlineData("05334664821")]    // abone 3 ile başlıyor
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~Services.Auth.PhoneNormalizerTests`
Expected: FAIL — `TryNormalize("0533466482", out _)` `true` dönüyor.

- [ ] **Step 3: Normalizer'ı düzelt**

`OrderDeck.LicenseServer/Services/Auth/PhoneNormalizer.cs` dosyasında 51-55. satırları:

```csharp
        if (cleaned.Length != 10) return false;
        if (!cleaned.All(char.IsDigit)) return false;

        result = "+90" + cleaned;
        return true;
```

bununla değiştir:

```csharp
        if (cleaned.Length != 10) return false;
        if (!cleaned.All(char.IsDigit)) return false;
        // TR mobil abone numarası daima 5 ile başlar. Core'daki ikiziyle
        // (OrderDeck.Core/Customers/PhoneNormalizer.cs) aynı kural — biri
        // değişip diğeri kalmasın diye test kümeleri de eşleniktir.
        if (cleaned[0] != '5') return false;

        result = "+90" + cleaned;
        return true;
```

Ayrıca sınıf XML yorumundaki `XXXXXXXXXX   → +90XXXXXXXXXX (10 hane gönderilirse)` satırının altına ekle:

```csharp
/// Abone numarası "5" ile başlamalıdır (TR mobil); sabit hatlar reddedilir.
```

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PhoneNormalizer`
Expected: PASS (Auth + IntakeForm test sınıflarının ikisi de).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Auth/PhoneNormalizer.cs OrderDeck.LicenseServer.Tests/Services/Auth/PhoneNormalizerTests.cs
git commit -m "fix(phone): TR mobil abone numarası 5 ile başlamalı (Auth)"
```

---

## Faz 2 — Veri modeli

### Task 3: `IysConsent` + `IysConsentEvent` entity'leri

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/IysConsent.cs`
- Create: `OrderDeck.LicenseServer/Domain/IysConsentEvent.cs`

- [ ] **Step 1: Durum entity'sini yaz**

`OrderDeck.LicenseServer/Domain/IysConsent.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>Bir alıcının ticari ileti izni. İYS'nin kendi sözlüğü.</summary>
public enum IysConsentStatus
{
    /// <summary>Henüz bilinmiyor / karar verilemiyor.</summary>
    Unknown = 0,
    Onay = 1,
    Ret = 2
}

/// <summary>Kaydın İYS'ye iletilme yolculuğu.</summary>
public enum IysPushState
{
    /// <summary>Yerelde yazıldı, henüz gönderilmedi.</summary>
    Pending = 0,
    /// <summary>/iys/add çağrıldı ve kuyruğa alındı — <b>kabul edildi demek değil</b>.</summary>
    Pushed = 1,
    /// <summary>/iys/search ONAY döndürdü. Gönderim yalnız bu durumda serbest.</summary>
    Confirmed = 2,
    /// <summary>Geçici hata; recovery deadline içinde yeniden dener.</summary>
    Failed = 3,
    /// <summary>3 iş günü doldu ya da kalıcı-veri hatası. Yeni onay olayı yeni pencere açar.</summary>
    Expired = 4
}

/// <summary>
/// Bir alıcının ticari ileti izninin <b>güncel</b> durumu — "şu an bu numaraya
/// ne yapabilirim?" sorusunun tek cevabı. Gönderim yolunda tek satır okunur.
///
/// <para><b><see cref="Status"/> ile <see cref="LastVerifiedStatus"/> neden ayrı
/// kolonlar.</b> İlki <i>bizim beyanımız</i>, ikincisi <i>İYS'nin cevabı</i>.
/// 2026-09-17'de 284 onayı kaybetmemizin sebebi ikisini bir sanmaktı: Netgsm'in
/// <c>code 0</c> yanıtı "kuyruğa alındı" demek, "kabul edildi" değil. Bu iki
/// alanı birleştiren her değişiklik o hatayı geri getirir.</para>
/// </summary>
public class IysConsent
{
    public Guid Id { get; set; }

    // (BrandCode, ChannelType, RecipientType, Recipient) = İYS'nin kendi
    // anahtarı; tekil index. İzin MARKA BAZINDA ayrıdır — aynı numara
    // 731734'te ONAY, 763208'de RET olabilir (2026-09-17'de ölçüldü).
    public string BrandCode { get; set; } = "";
    public string ChannelType { get; set; } = "MESAJ";
    public string RecipientType { get; set; } = "BIREYSEL";

    /// <summary>Daima E.164: <c>+905XXXXXXXXX</c>.</summary>
    public string Recipient { get; set; } = "";

    /// <summary>Bizim beyanımız — yerel olaylardan türer.</summary>
    public IysConsentStatus Status { get; set; } = IysConsentStatus.Unknown;

    /// <summary>İYS'ye beyan edilen onay tarihi.</summary>
    public DateTimeOffset? ConsentDate { get; set; }

    /// <summary>İYS onay kaynağı kodu: HS_WEB / HS_MOBIL.</summary>
    public string? SourceCode { get; set; }

    public IysPushState PushState { get; set; } = IysPushState.Pending;

    /// <summary>Onay anı + 3 iş günü. Bu tarihten sonra kayıt hukuken geçersiz.</summary>
    public DateTimeOffset? PushDeadline { get; set; }

    /// <summary>İYS'nin cevabı (/iys/search). Gönderim kapısı BUNU okur.</summary>
    public IysConsentStatus? LastVerifiedStatus { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>En güncel yerel olayın zamanı — RET'in diriltilmesini engelleyen sıra damgası.</summary>
    public DateTimeOffset LastLocalEventAt { get; set; }

    public DateTimeOffset? LastPushedAt { get; set; }

    /// <summary>Kaç kez doğrulama denendi (15dk → 1sa → 6sa → 24sa).</summary>
    public int VerifyAttempts { get; set; }
    public DateTimeOffset? NextVerifyAt { get; set; }

    /// <summary>Son hata özeti (admin listesi için; ham yanıt olay tablosunda).</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Olay entity'sini yaz**

`OrderDeck.LicenseServer/Domain/IysConsentEvent.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

public enum IysConsentEventType
{
    /// <summary>Kişi yerelde onay verdi.</summary>
    LocalConsent = 0,
    /// <summary>Kişi yerelde onayı geri çekti.</summary>
    LocalRevoke = 1,
    /// <summary>/iys/add denemesi ve ham yanıtı.</summary>
    PushAttempt = 2,
    /// <summary>/iys/search sonucu.</summary>
    SearchResult = 3
}

/// <summary>
/// İzin geçmişi — <b>ekle-only</b>. Hiç silinmez, hiç güncellenmez.
///
/// <para>İspat burada yaşamak zorunda: <c>/iys/search</c> bize
/// <c>consentDate</c> ve <c>source</c> alanlarını BOŞ döndürüyor
/// (2026-09-17'de ölçüldü). Denetimde "bu kişi izni ne zaman, nereden, hangi
/// IP'den verdi" sorusunun cevabı İYS'den geri okunamaz.</para>
/// </summary>
public class IysConsentEvent
{
    public Guid Id { get; set; }

    /// <summary>E.164. <see cref="IysConsent.Recipient"/> ile eşleşir (FK değil — kayıt satırı silinse bile olay kalır).</summary>
    public string Recipient { get; set; } = "";

    public DateTimeOffset OccurredAt { get; set; }
    public IysConsentEventType EventType { get; set; }

    /// <summary>Olayın taşıdığı durum (push/search için de dolu).</summary>
    public IysConsentStatus Status { get; set; }

    /// <summary>Hangi tablo: "IntakeFormSubmission" / "Shopper" / null (API olayı).</summary>
    public string? SourceTable { get; set; }
    public Guid? SourceId { get; set; }

    // 6563 ispat yükü — yalnız yerel onay olaylarında dolu.
    public string? ProofIp { get; set; }
    public string? ProofUserAgent { get; set; }

    // Ham API yanıtı LOG'A DEĞİL buraya yazılır (telefon log'da maskeli).
    public string? ApiResponseCode { get; set; }
    public string? ApiResponseBody { get; set; }
    public string? ErrorCode { get; set; }
}
```

- [ ] **Step 3: Derle**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/IysConsent.cs OrderDeck.LicenseServer/Domain/IysConsentEvent.cs
git commit -m "feat(iys): onay durumu ve olay entity'leri"
```

### Task 4: DbContext yapılandırması + göç

**Files:**
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (DbSet bloğu ~satır 69 sonrası; `OnModelCreating` içinde `Entity<Shopper>` bloğunun ardına)
- Create: `OrderDeck.LicenseServer/Data/Migrations/*_AddIysConsent.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentUniqueIndexTests.cs`

- [ ] **Step 1: DbSet'leri ekle**

`LicenseDbContext.cs` içinde son DbSet olan `BarcodeCounters` satırının hemen altına:

```csharp
    public DbSet<IysConsent> IysConsents => Set<IysConsent>();
    public DbSet<IysConsentEvent> IysConsentEvents => Set<IysConsentEvent>();
```

- [ ] **Step 2: Entity yapılandırmasını ekle**

`OnModelCreating` içinde, `modelBuilder.Entity<Shopper>(b => { ... });` bloğunun hemen ardına:

```csharp
        modelBuilder.Entity<IysConsent>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.BrandCode).HasMaxLength(16).IsRequired();
            b.Property(c => c.ChannelType).HasMaxLength(16).IsRequired();
            b.Property(c => c.RecipientType).HasMaxLength(16).IsRequired();
            b.Property(c => c.Recipient).HasMaxLength(20).IsRequired();

            // Enum'lar STRING olarak saklanır: admin SQL'inde ve yedek
            // dökümünde "Onay"/"Confirmed" okunur, "1"/"2" değil.
            b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(c => c.LastVerifiedStatus).HasConversion<string>().HasMaxLength(16);
            b.Property(c => c.PushState).HasConversion<string>().HasMaxLength(16);

            b.Property(c => c.SourceCode).HasMaxLength(32);
            b.Property(c => c.LastError).HasMaxLength(500);

            // İYS'nin kendi anahtarı. Aynı numaraya iki eşzamanlı olay
            // gelirse ikinci INSERT burada patlar — tek satır garantisi.
            b.HasIndex(c => new { c.BrandCode, c.ChannelType, c.RecipientType, c.Recipient })
                .IsUnique();

            // Push ve doğrulama işlerinin tarama yolu.
            b.HasIndex(c => new { c.PushState, c.NextVerifyAt });
        });

        modelBuilder.Entity<IysConsentEvent>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Recipient).HasMaxLength(20).IsRequired();
            b.Property(e => e.EventType).HasConversion<string>().HasMaxLength(16);
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(e => e.SourceTable).HasMaxLength(64);
            b.Property(e => e.ProofIp).HasMaxLength(64);
            b.Property(e => e.ProofUserAgent).HasMaxLength(512);
            b.Property(e => e.ApiResponseCode).HasMaxLength(16);
            b.Property(e => e.ApiResponseBody).HasMaxLength(2000);
            b.Property(e => e.ErrorCode).HasMaxLength(32);
            b.HasIndex(e => new { e.Recipient, e.OccurredAt });
        });
```

- [ ] **Step 3: Eşzamanlılık testini yaz (Testcontainers)**

`OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentUniqueIndexTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// Tekil index yalnız GERÇEK SQL Server'da kanıtlanabilir — InMemory
/// sağlayıcısının eşzamanlılık semantiği yok, index ihlali fırlatmaz.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IysConsentUniqueIndexTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public IysConsentUniqueIndexTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ayni_alici_icin_ikinci_satir_reddedilir()
    {
        var phone = "+9053" + Random.Shared.Next(10_000_000, 99_999_999);

        IysConsent Row() => new()
        {
            Id = Guid.NewGuid(),
            BrandCode = "731734",
            ChannelType = "MESAJ",
            RecipientType = "BIREYSEL",
            Recipient = phone,
            Status = IysConsentStatus.Onay,
            LastLocalEventAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsents.Add(Row());
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsents.Add(Row());
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "tekil index aynı markada aynı alıcıya ikinci satır bırakmamalı");
        }
    }
}
```

> **Not:** `RelationalApiFactory` ve `SqlServerCollection` `OrderDeck.LicenseServer.Tests/TestHelpers/` altında zaten var; deseni `Controllers/Auth/CustomerAuthVersionConcurrencyTests.cs:36-53` ile birebir aynı. Docker çalışıyor olmalı.

- [ ] **Step 4: Göçü üret**

Run:
```bash
dotnet ef migrations add AddIysConsent \
  --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj \
  --output-dir Data/Migrations
```
Expected: `Data/Migrations/<timestamp>_AddIysConsent.cs` + `.Designer.cs` + güncellenmiş `LicenseDbContextModelSnapshot.cs`.

Üretilen `Up()` içinde iki `CreateTable` ve üç `CreateIndex` olmalı; `IX_IysConsents_BrandCode_ChannelType_RecipientType_Recipient` **unique: true** olmalı. Değilse Step 2'deki `.IsUnique()` eksik demektir.

- [ ] **Step 5: Testleri çalıştır**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentUniqueIndex`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations \
        OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentUniqueIndexTests.cs
git commit -m "feat(iys): onay tablosu + tekil alıcı index'i (göç)"
```

---

## Faz 3 — İstemci, toplayıcı, işler

### Task 5: `IysBusinessDays` — 3 iş günü hesabı

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Iys/IysBusinessDays.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysBusinessDaysTests.cs`

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/IysBusinessDaysTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Iys;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysBusinessDaysTests
{
    // 2026-09-14 Pazartesi, 2026-09-19 Cumartesi, 2026-09-20 Pazar.
    private static DateTimeOffset Utc(int y, int m, int d)
        => new(y, m, d, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Hafta_ici_uc_is_gunu_dogrudan_eklenir()
    {
        // Pazartesi + 3 iş günü = Perşembe
        IysBusinessDays.Add(Utc(2026, 9, 14), 3).Should().Be(Utc(2026, 9, 17));
    }

    [Fact]
    public void Hafta_sonu_atlanir()
    {
        // Perşembe + 3 iş günü = Salı (Cmt/Paz sayılmaz)
        IysBusinessDays.Add(Utc(2026, 9, 17), 3).Should().Be(Utc(2026, 9, 22));
    }

    [Fact]
    public void Cumartesi_baslangici_once_pazartesiye_tasinir()
    {
        // Cumartesi başlayan sayaç ilk iş gününü Pazartesi sayar → Çarşamba
        IysBusinessDays.Add(Utc(2026, 9, 19), 3).Should().Be(Utc(2026, 9, 23));
    }

    [Fact]
    public void Ilk_dogrulama_randevusu_onbes_dakika_sonra()
    {
        var t = Utc(2026, 9, 14);
        IysVerifySchedule.Next(t, 0).Should().Be(t.AddMinutes(15));
        IysVerifySchedule.Next(t, 1).Should().Be(t.AddHours(1));
        IysVerifySchedule.Next(t, 3).Should().Be(t.AddHours(24));
    }

    [Fact]
    public void Takvim_tukenince_randevu_verilmez()
    {
        IysVerifySchedule.Next(Utc(2026, 9, 14), 4).Should().BeNull();
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysBusinessDaysTests`
Expected: FAIL — `IysBusinessDays` türü yok, derleme hatası.

- [ ] **Step 3: Uygula**

`OrderDeck.LicenseServer/Services/Iys/IysBusinessDays.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Ticari İletişim Yönetmeliği m.7/11-12: İYS dışında alınan onay <b>üç iş
/// günü</b> içinde kaydedilmezse geçersiz. Son tarih hesabı buradan çıkar.
///
/// <para><b>Resmî tatiller modellenmiyor</b> — yalnız Cumartesi/Pazar atlanıyor.
/// Yani hesaplanan son tarih gerçeğinden bir miktar <i>geç</i> olabilir. Bunun
/// telafisi boru hattının kendisi: push, onay yazıldıktan saniyeler sonra
/// çalışıyor ve son tarihe 24 saat kalanlar admin uyarısına düşüyor. Son tarih
/// bir hedef değil, sessiz kalmayı yasaklayan bir alarm eşiği.</para>
/// </summary>
public static class IysBusinessDays
{
    private static bool IsWeekend(DateTimeOffset d)
        => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>
    /// <paramref name="from"/> üstüne <paramref name="businessDays"/> iş günü ekler.
    /// Başlangıç hafta sonuna denk gelirse sayaç ilk iş gününden başlar.
    /// </summary>
    public static DateTimeOffset Add(DateTimeOffset from, int businessDays)
    {
        var cursor = from;
        while (IsWeekend(cursor)) cursor = cursor.AddDays(1);

        var remaining = businessDays;
        while (remaining > 0)
        {
            cursor = cursor.AddDays(1);
            if (!IsWeekend(cursor)) remaining--;
        }
        return cursor;
    }
}

/// <summary>
/// Doğrulama randevu takvimi. İYS işleme anlık değil: push'tan hemen sonra
/// sormak, henüz işlenmemiş kaydı "RET" sanmaya yol açar. İlk deneme 15 dk
/// sonra, sonra artan aralıklarla.
///
/// <para>Push ve doğrulama işleri aynı diziyi okur — takvim TEK yerde durur.</para>
/// </summary>
public static class IysVerifySchedule
{
    public static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    };

    /// <summary>
    /// <paramref name="attempts"/> deneme yapılmışken bir sonraki randevu.
    /// Takvim tükendiyse <c>null</c> — artık beklemenin anlamı yok.
    /// </summary>
    public static DateTimeOffset? Next(DateTimeOffset from, int attempts)
        => attempts < Backoff.Length ? from + Backoff[attempts] : null;
}
```

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysBusinessDaysTests`
Expected: PASS (5 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysBusinessDays.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysBusinessDaysTests.cs
git commit -m "feat(iys): üç iş günü son tarih hesabı + doğrulama takvimi"
```

### Task 6: `IIysClient` sözleşmesi + DTO'lar

Bu task yalnız arayüz ve veri taşıyıcıları tanımlar; HTTP bir sonraki task'ta.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Iys/IIysClient.cs`
- Modify: `OrderDeck.LicenseServer/Services/Sms/NetgsmOptions.cs`

- [ ] **Step 1: Sözleşmeyi yaz**

`OrderDeck.LicenseServer/Services/Iys/IIysClient.cs`:

```csharp
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>İYS'ye tek bir izin satırı bildirimi.</summary>
/// <param name="Recipient">E.164, <c>+905XXXXXXXXX</c>.</param>
/// <param name="RecipientType">BIREYSEL / TACIR.</param>
/// <param name="ChannelType">MESAJ / ARAMA / EPOSTA.</param>
/// <param name="Status">Beyan edilen izin durumu.</param>
/// <param name="ConsentDate">Onayın alındığı an (TR yerel saate çevrilerek gönderilir).</param>
/// <param name="SourceCode">HS_WEB / HS_MOBIL.</param>
/// <param name="RefId">Bizim kayıt kimliğimiz — mükerrer push'u zararsız kılar.</param>
public sealed record IysConsentRecord(
    string Recipient,
    string RecipientType,
    string ChannelType,
    IysConsentStatus Status,
    DateTimeOffset ConsentDate,
    string SourceCode,
    string RefId);

/// <summary>
/// <c>/iys/add</c> yanıtı. <see cref="Queued"/> "kuyruğa alındı" demektir —
/// <b>kabul edildi demek DEĞİL</b>. Kabul yalnız <c>/iys/search</c> ile
/// doğrulanır; 2026-09-17'de 284 kaydı bu ayrımı yapmadığımız için kaybettik.
/// </summary>
public sealed record IysAddResult(string Code, string RawBody, bool Queued);

/// <summary><c>/iys/search</c> yanıtı; <see cref="Statuses"/> alıcı → İYS durumu.</summary>
public sealed record IysSearchResult(
    string Code,
    string RawBody,
    IReadOnlyDictionary<string, IysConsentStatus> Statuses);

/// <summary>
/// Kalıcı <b>yapılandırma</b> hatası: marka kodu (<c>code 60</c>) ya da kimlik
/// (<c>code 30</c>). Boru hattı durur — sessizce devam etmek bekleyen her
/// kaydı sırayla harcar, çünkü hepsi aynı hatayla düşer.
/// </summary>
public sealed class IysConfigurationException : Exception
{
    public string Code { get; }
    public IysConfigurationException(string code, string message) : base(message) => Code = code;
}

public interface IIysClient
{
    /// <summary>Toplu izin bildirimi. Tek HTTP isteği; <paramref name="items"/> en fazla 20 satır.</summary>
    Task<IysAddResult> AddAsync(IReadOnlyList<IysConsentRecord> items, CancellationToken ct = default);

    /// <summary>Toplu izin sorgusu. Yanıtta olmayan alıcı <see cref="IysConsentStatus.Unknown"/> sayılır.</summary>
    Task<IysSearchResult> SearchAsync(IReadOnlyList<string> recipients, CancellationToken ct = default);
}
```

- [ ] **Step 2: Marka kodu ayarını ekle**

`OrderDeck.LicenseServer/Services/Sms/NetgsmOptions.cs` içinde `TimeoutSeconds` özelliğinin **üstüne**:

```csharp
    /// <summary>
    /// İYS marka kodu (İYS'de "brandCode"). Prod: 731734 = EMAR GLOBAL.
    /// Hardcode YOK — marka ileride ORDERDECK olacak ve izinler marka bazında
    /// ayrı tutuluyor (aynı numara bir markada ONAY, diğerinde RET olabilir).
    /// Boş bırakılırsa İYS boru hattı kapalıdır.
    /// </summary>
    public string BrandCode { get; set; } = "";

    /// <summary>İYS onay kaynağı kodu. Web formu ve mobil kayıt için HS_WEB.</summary>
    public string IysSourceCode { get; set; } = "HS_WEB";

```

- [ ] **Step 3: Derle**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IIysClient.cs OrderDeck.LicenseServer/Services/Sms/NetgsmOptions.cs
git commit -m "feat(iys): istemci sözleşmesi + marka kodu ayarı"
```

---

### Task 7: `NetgsmIysClient` — gerçek yanıt şekline göre ayrıştırma

Yanıt şekli 2026-09-17'de canlı çağrıyla ölçüldü, tahmin değil:

```json
{"code":"0","error":"false","query":[
  {"consentDate":"","source":"","recipientType":"BIREYSEL","status":"ONAY",
   "type":"MESAJ","recipient":"+905310826728","transactionId":"6716aee7..."}]}
```

`code` **string** `"0"`; sonuçlar `query` dizisinde; `consentDate`/`source` **boş**;
`transactionId` satır kimliği değil **sorgu** kimliği (20 satırlık sorguda hepsi aynı geldi).

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Iys/NetgsmIysClient.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/NetgsmIysClientTests.cs`

- [ ] **Step 1: Düşen testleri yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/NetgsmIysClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class NetgsmIysClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _respBody;
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }

        public CapturingHandler(string respBody) => _respBody = respBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static NetgsmOptions Opt() => new()
    {
        UserCode = $"user-{Guid.NewGuid():N}",
        Password = $"pw-{Guid.NewGuid():N}",
        BaseUrl = "https://api.netgsm.com.tr",
        BrandCode = "731734",
    };

    private static (NetgsmIysClient Client, CapturingHandler Handler) Build(string respBody)
    {
        var handler = new CapturingHandler(respBody);
        var client = new NetgsmIysClient(new HttpClient(handler), Options.Create(Opt()),
            NullLogger<NetgsmIysClient>.Instance);
        return (client, handler);
    }

    private static IysConsentRecord Rec(string phone) => new(
        phone, "BIREYSEL", "MESAJ", IysConsentStatus.Onay,
        new DateTimeOffset(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(3)),
        "HS_WEB", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AddAsync_kimligi_govdede_yollar_ve_uca_gider()
    {
        var (client, handler) = Build("{\"code\":\"0\"}");

        await client.AddAsync(new[] { Rec("+905551112233") });

        handler.Uri!.ToString().Should().Be("https://api.netgsm.com.tr/iys/add");
        using var doc = JsonDocument.Parse(handler.Body!);
        var header = doc.RootElement.GetProperty("header");
        header.GetProperty("brandCode").GetString().Should().Be("731734");
        header.TryGetProperty("username", out _).Should().BeTrue();

        var row = doc.RootElement.GetProperty("body").GetProperty("data")[0];
        row.GetProperty("recipient").GetString().Should().Be("+905551112233");
        row.GetProperty("status").GetString().Should().Be("ONAY");
        row.GetProperty("type").GetString().Should().Be("MESAJ");
        row.GetProperty("source").GetString().Should().Be("HS_WEB");
        // TR yerel saat, saniye hassasiyetinde
        row.GetProperty("consentDate").GetString().Should().Be("2026-09-18 10:30:00");
    }

    [Fact]
    public async Task AddAsync_code_sifir_yalnizca_kuyruga_alindi_demektir()
    {
        // BU TEST 2026-09-17'de 284 kaydı kaybettiren hatayı koda gömer:
        // "code 0" KABUL DEĞİL, yalnız kuyruğa alındı. Confirmed kararı
        // burada verilemez — yalnız /iys/search verebilir.
        var (client, _) = Build("{\"code\":\"0\",\"error\":\"false\"}");

        var result = await client.AddAsync(new[] { Rec("+905551112233") });

        result.Queued.Should().BeTrue();
        result.Code.Should().Be("0");
        // IysAddResult'ta "Confirmed"/"Accepted" diye bir alan YOKTUR ve olmamalıdır.
        typeof(IysAddResult).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "Confirmed", "Accepted" });
    }

    [Fact]
    public async Task SearchAsync_query_dizisinden_alici_bazinda_durum_cikarir()
    {
        var (client, _) = Build("""
        {"code":"0","error":"false","query":[
          {"consentDate":"","source":"","recipientType":"BIREYSEL","status":"ONAY",
           "type":"MESAJ","recipient":"+905310826728","transactionId":"6716aee7"},
          {"consentDate":"","source":"","recipientType":"BIREYSEL","status":"RET",
           "type":"MESAJ","recipient":"+905000000000","transactionId":"6716aee7"}]}
        """);

        var result = await client.SearchAsync(new[] { "+905310826728", "+905000000000" });

        result.Statuses["+905310826728"].Should().Be(IysConsentStatus.Onay);
        result.Statuses["+905000000000"].Should().Be(IysConsentStatus.Ret);
    }

    [Fact]
    public async Task SearchAsync_yanitta_olmayan_alici_Unknown_kalir()
    {
        var (client, _) = Build("{\"code\":\"0\",\"query\":[]}");

        var result = await client.SearchAsync(new[] { "+905551112233" });

        result.Statuses.Should().NotContainKey("+905551112233");
    }

    [Theory]
    [InlineData("60")]  // marka kodu hatalı
    [InlineData("30")]  // kimlik hatalı
    public async Task Yapilandirma_hatasi_boru_hattini_durdurur(string code)
    {
        var (client, _) = Build($"{{\"code\":\"{code}\"}}");

        var act = async () => await client.AddAsync(new[] { Rec("+905551112233") });

        await act.Should().ThrowAsync<IysConfigurationException>();
    }
}
```

- [ ] **Step 2: Testleri çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~NetgsmIysClientTests`
Expected: FAIL — `NetgsmIysClient` türü yok.

- [ ] **Step 3: İstemciyi yaz**

`OrderDeck.LicenseServer/Services/Iys/NetgsmIysClient.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Netgsm İYS istemcisi. <c>POST {BaseUrl}/iys/add</c> ve <c>/iys/search</c>.
///
/// <para><b>Kimlik gövdede</b>, HTTP header'da değil — SMS ucundaki Basic auth
/// deseni burada geçerli DEĞİL (2026-09-17'de canlı çağrıyla doğrulandı).</para>
///
/// <para>Ham yanıt log'a değil çağırana döner; olay tablosuna orada yazılır.
/// Log'a yalnız maskeli telefon çıkar (KVKK).</para>
/// </summary>
public sealed class NetgsmIysClient : IIysClient
{
    // TR yerel saat: İYS onay tarihini bu ofsette bekliyor.
    private static readonly TimeSpan TrOffset = TimeSpan.FromHours(3);

    private readonly HttpClient _http;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<NetgsmIysClient> _log;

    public NetgsmIysClient(HttpClient http, IOptions<NetgsmOptions> opt, ILogger<NetgsmIysClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<IysAddResult> AddAsync(
        IReadOnlyList<IysConsentRecord> items, CancellationToken ct = default)
    {
        var data = items.Select(i => new Dictionary<string, object?>
        {
            ["type"] = i.ChannelType,
            ["source"] = i.SourceCode,
            ["recipient"] = i.Recipient,
            ["recipientType"] = i.RecipientType,
            ["status"] = i.Status == IysConsentStatus.Onay ? "ONAY" : "RET",
            ["consentDate"] = i.ConsentDate.ToOffset(TrOffset)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            // refid mükerrer push'u zararsız kılar: aynı kaydı iki kez
            // itersek İYS ikinci satırı yeni bir olay saymaz.
            ["refid"] = i.RefId,
        }).ToArray();

        var (code, body) = await PostAsync("add", data, ct);
        return new IysAddResult(code, body, Queued: code == "0");
    }

    public async Task<IysSearchResult> SearchAsync(
        IReadOnlyList<string> recipients, CancellationToken ct = default)
    {
        var data = recipients.Select(r => new Dictionary<string, object?>
        {
            ["type"] = "MESAJ",
            ["recipient"] = r,
            ["recipientType"] = "BIREYSEL",
        }).ToArray();

        var (code, body) = await PostAsync("search", data, ct);

        var statuses = new Dictionary<string, IysConsentStatus>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("query", out var query)
                && query.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in query.EnumerateArray())
                {
                    var recipient = row.TryGetProperty("recipient", out var r) ? r.GetString() : null;
                    var status = row.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (string.IsNullOrWhiteSpace(recipient)) continue;

                    // "Kayıt yok" ile "reddetti" ayırt EDİLEMEZ: İYS ikisine de
                    // RET diyor (2026-09-17'de kayıtsız numarayla doğrulandı) ve
                    // transactionId sorgu kimliği olduğu için ayırt edici değil.
                    // Fail-closed: ONAY olmayan her şey gönderimi keser.
                    statuses[recipient] = status switch
                    {
                        "ONAY" => IysConsentStatus.Onay,
                        "RET" => IysConsentStatus.Ret,
                        _ => IysConsentStatus.Unknown,
                    };
                }
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "İYS search yanıtı ayrıştırılamadı (code={Code})", code);
        }

        return new IysSearchResult(code, body, statuses);
    }

    private async Task<(string Code, string Body)> PostAsync(
        string path, object data, CancellationToken ct)
    {
        var payload = new
        {
            header = new
            {
                username = _opt.UserCode,
                password = _opt.Password,
                brandCode = _opt.BrandCode,
            },
            body = new { data },
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{_opt.BaseUrl.TrimEnd('/')}/iys/{path}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var code = ReadCode(body);

        // Kalıcı YAPILANDIRMA hatası: her kayıt aynı hatayla düşer, sırayla
        // denemek yalnız zaman harcar. Boru hattı durur, alarm çalar.
        if (code is "30" or "60")
            throw new IysConfigurationException(code,
                $"İYS yapılandırma hatası (code={code}). Marka kodu/kimlik kontrol edilmeli.");

        return (code, body.Length > 2000 ? body[..2000] : body);
    }

    // code alanı bazen string ("0"), bazen sayı dönebiliyor; ikisini de kabul et.
    private static string ReadCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var el))
                return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        }
        catch (JsonException) { /* düz metin yanıt */ }
        return body.Trim().Trim('"');
    }
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~NetgsmIysClientTests`
Expected: PASS (6 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/NetgsmIysClient.cs OrderDeck.LicenseServer.Tests/Services/Iys/NetgsmIysClientTests.cs
git commit -m "feat(iys): Netgsm İYS istemcisi (add/search)"
```

---

### Task 8: `IysConsentCollector` — durum geçiş kuralları

> **Spec'ten bilinçli sapma (gözden geçirende görünsün diye yazılı):** spec
> "bozuk numara `Unknown` + `Failed` işaretlenip admin listesine düşer" diyor.
> Uygulama bunu **olay tablosunda** yapıyor (`ErrorCode = "invalid-phone"`),
> `IysConsent` satırı açmıyor. Gerekçe: `IysConsent.Recipient` sözleşmesi
> "daima E.164" ve tekil index İYS'nin kendi anahtarı; bozuk numara oraya
> yazılırsa sözleşme delinir. Spec'in asıl amacı — **sessizce atlanmamak** —
> karşılanıyor: kayıt admin sayfasında "geçersiz numara" olarak listeleniyor.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Iys/IysConsentCollector.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs`

- [ ] **Step 1: Düşen testleri yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysConsentCollectorTests
{
    private const string Phone = "+905551112233";

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-{Guid.NewGuid():N}").Options);

    private static IysConsentCollector Collector(LicenseDbContext db)
        => new(db, Options.Create(new NetgsmOptions { BrandCode = "731734" }),
            NullLogger<IysConsentCollector>.Instance);

    private static Task RecordAsync(
        IysConsentCollector c, bool consented, DateTimeOffset at, string phone = Phone)
        => c.RecordAsync(phone, consented, at, "Shopper", Guid.NewGuid(),
            ip: "203.0.113.7", userAgent: "test-agent");

    [Fact]
    public async Task Yeni_onay_Pending_olarak_yazilir_ve_son_tarih_hesaplanir()
    {
        using var db = NewDb();
        var at = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero); // Pazartesi

        await RecordAsync(Collector(db), consented: true, at);
        await db.SaveChangesAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Onay);
        row.PushState.Should().Be(IysPushState.Pending);
        row.ConsentDate.Should().Be(at);
        row.PushDeadline.Should().Be(new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero));
        row.LastVerifiedStatus.Should().BeNull("İYS henüz sorulmadı");
    }

    [Fact]
    public async Task Ispat_alanlari_olay_tablosunda_yasar()
    {
        using var db = NewDb();
        await RecordAsync(Collector(db), true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var ev = await db.IysConsentEvents.SingleAsync();
        ev.EventType.Should().Be(IysConsentEventType.LocalConsent);
        ev.ProofIp.Should().Be("203.0.113.7");
        ev.ProofUserAgent.Should().Be("test-agent");
        ev.SourceTable.Should().Be("Shopper");
    }

    [Fact]
    public async Task Kural1_eski_onay_olayi_RET_i_diriltmez()
    {
        using var db = NewDb();
        var c = Collector(db);
        var yeni = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
        var eski = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

        await RecordAsync(c, consented: false, yeni);   // önce ret (daha yeni)
        await db.SaveChangesAsync();
        await RecordAsync(c, consented: true, eski);    // sonra ESKİ onay işlenir
        await db.SaveChangesAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Ret, "geç işlenen eski onay reddi ezmez");
        row.LastLocalEventAt.Should().Be(yeni);
        db.IysConsentEvents.Count().Should().Be(2, "olay yine de kaydedilir");
    }

    [Fact]
    public async Task Kural1_daha_yeni_onay_RET_i_yukseltir()
    {
        using var db = NewDb();
        var c = Collector(db);
        var eski = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);
        var yeni = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

        await RecordAsync(c, consented: false, eski);
        await db.SaveChangesAsync();
        await RecordAsync(c, consented: true, yeni);
        await db.SaveChangesAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Onay);
        row.PushState.Should().Be(IysPushState.Pending, "yeni onay yeni push penceresi açar");
    }

    [Fact]
    public async Task Kural4_yerel_ret_dogrulamayi_beklemeden_gonderimi_keser()
    {
        using var db = NewDb();
        var c = Collector(db);
        await RecordAsync(c, true, new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        // İYS onayı gelmiş gibi işaretle
        var row = await db.IysConsents.SingleAsync();
        row.LastVerifiedStatus = IysConsentStatus.Onay;
        row.PushState = IysPushState.Confirmed;
        await db.SaveChangesAsync();

        await RecordAsync(c, false, new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Ret);
        row.LastVerifiedStatus.Should().Be(IysConsentStatus.Onay,
            "İYS'nin cevabı uydurulmaz; kapı iki alanı BİRLİKTE okur");
        IysConsentGate.CanSend(row).Should().BeFalse();
    }

    [Fact]
    public async Task Expired_kayit_yeni_onayla_yeniden_acilir()
    {
        using var db = NewDb();
        var c = Collector(db);
        await RecordAsync(c, true, new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        var row = await db.IysConsents.SingleAsync();
        row.PushState = IysPushState.Expired;
        await db.SaveChangesAsync();

        await RecordAsync(c, true, new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Pending);
    }

    [Fact]
    public async Task Bozuk_numara_sessizce_atlanmaz_ama_kayit_satiri_acmaz()
    {
        using var db = NewDb();
        // 9 hane + baştaki 0 — Faz 1'den sonra normalize edilemez
        await RecordAsync(Collector(db), true, DateTimeOffset.UtcNow, phone: "0533466482");
        await db.SaveChangesAsync();

        db.IysConsents.Should().BeEmpty("E.164 olmayan numara tekil anahtarı kirletmez");
        var ev = await db.IysConsentEvents.SingleAsync();
        ev.ErrorCode.Should().Be("invalid-phone");
        ev.Status.Should().Be(IysConsentStatus.Unknown);
    }
}
```

- [ ] **Step 2: Testleri çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentCollectorTests`
Expected: FAIL — `IysConsentCollector` ve `IysConsentGate` türleri yok.

- [ ] **Step 3: Toplayıcıyı ve gönderim kapısını yaz**

`OrderDeck.LicenseServer/Services/Iys/IysConsentCollector.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Ticari ileti gönderim kapısı. <b>Tek yer</b> — her gönderim yolu buradan
/// geçmeli, yoksa fail-closed kuralı yalnız bir yolda uygulanır.
/// </summary>
public static class IysConsentGate
{
    /// <summary>
    /// Gönderim için İKİ koşul birden gerekir:
    /// <list type="bullet">
    /// <item><c>Status == Onay</c> — kişi bize onay verdi ve geri çekmedi.
    /// Yerel ret doğrulamayı beklemeden gönderimi keser (kural 4).</item>
    /// <item><c>LastVerifiedStatus == Onay</c> — İYS de onaylı diyor. İYS'nin
    /// RET'i yerel onayı EZMEZ (ispat olarak durur) ama gönderimi keser
    /// (kural 2). "Kayıt yok" ile "reddetti" ayırt edilemediği için
    /// fail-closed (kural 3).</item>
    /// </list>
    /// </summary>
    public static bool CanSend(IysConsent? c)
        => c is not null
           && c.Status == IysConsentStatus.Onay
           && c.LastVerifiedStatus == IysConsentStatus.Onay;
}

/// <summary>
/// Kaynak tablolardaki onay/ret olaylarını <see cref="IysConsent"/> durumuna ve
/// <see cref="IysConsentEvent"/> geçmişine çevirir.
///
/// <para><b>SaveChanges ÇAĞIRMAZ.</b> Çağıran kendi transaction'ında kaydeder;
/// onay girip olay kaydı girmemesi o kaydı görünmez yapardı.</para>
/// </summary>
public sealed class IysConsentCollector
{
    /// <summary>Yönetmelik m.7/11-12 — İYS dışı onay için kayıt süresi.</summary>
    public const int PushDeadlineBusinessDays = 3;

    private readonly LicenseDbContext _db;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentCollector> _log;

    public IysConsentCollector(
        LicenseDbContext db, IOptions<NetgsmOptions> opt, ILogger<IysConsentCollector> log)
    {
        _db = db;
        _opt = opt.Value;
        _log = log;
    }

    public async Task RecordAsync(
        string? rawPhone, bool consented, DateTimeOffset occurredAt,
        string sourceTable, Guid sourceId,
        string? ip, string? userAgent, CancellationToken ct = default)
    {
        var status = consented ? IysConsentStatus.Onay : IysConsentStatus.Ret;
        var phone = OrderDeck.Core.Customers.PhoneNormalizer.NormalizeTr(rawPhone);

        if (phone is null)
        {
            // Sessiz atlama, 284 kaydı fark etmeden kaybetme biçimimizdi.
            // Kayıt satırı açmıyoruz (E.164 sözleşmesi) ama olay tablosuna
            // düşüyor ve admin sayfasında görünüyor.
            _db.IysConsentEvents.Add(new IysConsentEvent
            {
                Id = Guid.NewGuid(),
                Recipient = Truncate(rawPhone ?? "", 20),
                OccurredAt = occurredAt,
                EventType = consented ? IysConsentEventType.LocalConsent : IysConsentEventType.LocalRevoke,
                Status = IysConsentStatus.Unknown,
                SourceTable = sourceTable,
                SourceId = sourceId,
                ProofIp = ip,
                ProofUserAgent = Truncate(userAgent, 512),
                ErrorCode = "invalid-phone",
            });
            _log.LogWarning(
                "İYS: normalize edilemeyen numara, kayıt açılmadı ({Phone}, kaynak={Source})",
                PiiMasker.MaskPhone(rawPhone ?? ""), sourceTable);
            return;
        }

        _db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            Recipient = phone,
            OccurredAt = occurredAt,
            EventType = consented ? IysConsentEventType.LocalConsent : IysConsentEventType.LocalRevoke,
            Status = status,
            SourceTable = sourceTable,
            SourceId = sourceId,
            ProofIp = ip,
            ProofUserAgent = Truncate(userAgent, 512),
        });

        var now = DateTimeOffset.UtcNow;
        var row = await _db.IysConsents.FirstOrDefaultAsync(
            c => c.BrandCode == _opt.BrandCode
                 && c.ChannelType == "MESAJ"
                 && c.RecipientType == "BIREYSEL"
                 && c.Recipient == phone, ct);

        if (row is null)
        {
            row = new IysConsent
            {
                Id = Guid.NewGuid(),
                BrandCode = _opt.BrandCode,
                ChannelType = "MESAJ",
                RecipientType = "BIREYSEL",
                Recipient = phone,
                CreatedAt = now,
            };
            _db.IysConsents.Add(row);
        }
        else if (occurredAt <= row.LastLocalEventAt)
        {
            // Kural 1: RET kendiliğinden ONAY'a yükselmez. Durum yalnızca
            // LastLocalEventAt'ten DAHA YENİ bir olayla değişir; geç işlenen
            // eski bir onay reddi ezemez. Olay yine de yazıldı (yukarıda).
            return;
        }

        row.Status = status;
        row.LastLocalEventAt = occurredAt;
        row.UpdatedAt = now;

        if (consented)
        {
            row.ConsentDate = occurredAt;
            row.SourceCode = _opt.IysSourceCode;
            row.PushDeadline = IysBusinessDays.Add(occurredAt, PushDeadlineBusinessDays);
        }

        // Yeni olay yeni push penceresi açar — Expired kalıcı yasak değildir.
        // Ret de itilir (yasal kayıt), ama gönderim bunu beklemez: kapı
        // Status'u de okuduğu için mesaj zaten kesildi.
        row.PushState = IysPushState.Pending;
        row.VerifyAttempts = 0;
        row.NextVerifyAt = null;
        row.LastError = null;
    }

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max];
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentCollectorTests`
Expected: PASS (7 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysConsentCollector.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs
git commit -m "feat(iys): onay toplayıcı + gönderim kapısı (durum geçiş kuralları)"
```

---

### Task 9: `IysConsentPushJob` — `/iys/add`, 20'şerli parti

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs`

- [ ] **Step 1: Düşen testleri yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysConsentPushJobTests
{
    private sealed class FakeIysClient : IIysClient
    {
        public List<IReadOnlyList<IysConsentRecord>> AddCalls { get; } = new();
        public Func<IReadOnlyList<IysConsentRecord>, IysAddResult>? AddBehavior { get; set; }
        public Exception? ThrowOnAdd { get; set; }

        public Task<IysAddResult> AddAsync(IReadOnlyList<IysConsentRecord> items, CancellationToken ct = default)
        {
            AddCalls.Add(items);
            if (ThrowOnAdd is not null) throw ThrowOnAdd;
            return Task.FromResult(AddBehavior?.Invoke(items)
                ?? new IysAddResult("0", "{\"code\":\"0\"}", Queued: true));
        }

        public Task<IysSearchResult> SearchAsync(IReadOnlyList<string> recipients, CancellationToken ct = default)
            => Task.FromResult(new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>()));
    }

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-push-{Guid.NewGuid():N}").Options);

    private static IysConsentPushJob Job(LicenseDbContext db, IIysClient client)
        => new(db, client, Options.Create(new NetgsmOptions { BrandCode = "731734" }),
            NullLogger<IysConsentPushJob>.Instance);

    private static IysConsent Pending(string phone) => new()
    {
        Id = Guid.NewGuid(),
        BrandCode = "731734",
        ChannelType = "MESAJ",
        RecipientType = "BIREYSEL",
        Recipient = phone,
        Status = IysConsentStatus.Onay,
        ConsentDate = DateTimeOffset.UtcNow,
        SourceCode = "HS_WEB",
        PushState = IysPushState.Pending,
        PushDeadline = DateTimeOffset.UtcNow.AddDays(3),
        LastLocalEventAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<LicenseDbContext> SeedAsync(int count)
    {
        var db = NewDb();
        for (var i = 0; i < count; i++)
            db.IysConsents.Add(Pending($"+90555{i:D7}"));
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Code_sifir_Confirmed_YAPMAZ_yalnizca_Pushed()
    {
        // 2026-09-17'de 284 kaydı kaybettiren hata tam olarak buydu:
        // "code 0" kuyruğa alındı demek, kabul edildi değil. Kabul kararını
        // yalnız IysConsentVerifyJob (/iys/search) verebilir.
        using var db = await SeedAsync(1);
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Pushed);
        row.PushState.Should().NotBe(IysPushState.Confirmed);
        row.LastVerifiedStatus.Should().BeNull("İYS'ye henüz sorulmadı");
        row.NextVerifyAt.Should().NotBeNull("doğrulama randevusu alınmalı");
    }

    [Fact]
    public async Task Ham_yanit_olay_tablosuna_yazilir()
    {
        using var db = await SeedAsync(1);

        await Job(db, new FakeIysClient()).RunAsync();

        var ev = await db.IysConsentEvents
            .SingleAsync(e => e.EventType == IysConsentEventType.PushAttempt);
        ev.ApiResponseCode.Should().Be("0");
        ev.ApiResponseBody.Should().Contain("code");
    }

    [Fact]
    public async Task Bekleyenler_yirmiserli_partilenir()
    {
        using var db = await SeedAsync(45);
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddCalls.Select(c => c.Count).Should().Equal(20, 20, 5);
    }

    [Fact]
    public async Task Gecici_hata_Failed_birakir_deadline_icinde_yeniden_denenir()
    {
        using var db = await SeedAsync(1);
        var client = new FakeIysClient { ThrowOnAdd = new HttpRequestException("ağ") };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Failed);
        row.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Son_tarihi_gecmis_kayit_itilmez_Expired_olur()
    {
        using var db = NewDb();
        var row = Pending("+905551112233");
        row.PushDeadline = DateTimeOffset.UtcNow.AddHours(-1);
        db.IysConsents.Add(row);
        await db.SaveChangesAsync();
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddCalls.Should().BeEmpty("süresi dolmuş kayıt İYS'ye gönderilmez");
        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Expired);
    }

    [Fact]
    public async Task Yapilandirma_hatasi_boru_hattini_durdurur()
    {
        using var db = await SeedAsync(45);
        var client = new FakeIysClient
        {
            ThrowOnAdd = new IysConfigurationException("60", "marka kodu"),
        };

        var act = async () => await Job(db, client).RunAsync();

        await act.Should().ThrowAsync<IysConfigurationException>();
        client.AddCalls.Should().HaveCount(1, "ilk partiden sonra durmalı, 45 kaydı harcamamalı");
    }
}
```

- [ ] **Step 2: Testleri çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentPushJobTests`
Expected: FAIL — `IysConsentPushJob` türü yok.

- [ ] **Step 3: İşi yaz**

`OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs`:

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Hangfire işi: <see cref="IysPushState.Pending"/> kayıtları <c>/iys/add</c>
/// ile İYS'ye bildirir.
///
/// <para><b>Bu iş "kabul edildi" kararı VERMEZ.</b> Netgsm'in <c>code 0</c>
/// yanıtı "kuyruğa alındı" demek. Kayıt <see cref="IysPushState.Pushed"/>'e
/// geçer ve doğrulama randevusu alır; kabulü yalnız
/// <see cref="IysConsentVerifyJob"/> yazabilir. 2026-09-17'de 284 onayı bu
/// ayrımı yapmadığımız için kaybettik.</para>
///
/// <para>Süpürme işi olduğu için satır kilidi değil <b>iş kilidi</b> kullanır:
/// <c>[DisableConcurrentExecution]</c>. Aynı anda ikinci bir kopya çalışırsa
/// aynı <c>Pending</c> satırlarını okur ve İYS'ye ikinci kez bildirir — zararsız
/// ama dakikada 10 isteklik kotayı boşa harcar ve olay tablosunu ikizler.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysConsentPushJob
{
    /// <summary>Tek istekte bildirilen kayıt sayısı.</summary>
    public const int BatchSize = 20;

    /// <summary>Netgsm ~10 istek/dk sınırlı; partiler arası bekleme.</summary>
    public static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(6);

    private readonly LicenseDbContext _db;
    private readonly IIysClient _client;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentPushJob> _log;

    public IysConsentPushJob(
        LicenseDbContext db, IIysClient client,
        IOptions<NetgsmOptions> opt, ILogger<IysConsentPushJob> log)
    {
        _db = db;
        _client = client;
        _opt = opt.Value;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.BrandCode))
        {
            _log.LogInformation("İYS push: BrandCode ayarlı değil, boru hattı kapalı");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Süresi dolmuş bekleyenler hiç gönderilmez: 3 iş günü geçtiyse
        // İYS zaten H467 ile reddeder (consent_date çok eski) ve kayıt
        // hukuken geçersiz. Sessizce silmiyoruz — Expired damgası admin
        // listesinde görünür.
        var expired = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Pending
                        && c.PushDeadline != null && c.PushDeadline < now)
            .ToListAsync(ct);
        foreach (var e in expired)
        {
            e.PushState = IysPushState.Expired;
            e.LastError = "push-deadline-passed";
            e.UpdatedAt = now;
        }
        if (expired.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("İYS push: {Count} kaydın 3 iş günü penceresi doldu", expired.Count);
        }

        var pending = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Pending
                        && (c.PushDeadline == null || c.PushDeadline >= now))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var first = true;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            if (!first) await Task.Delay(BatchDelay, ct);
            first = false;
            await PushBatchAsync(batch, ct);
        }
    }

    private async Task PushBatchAsync(IysConsent[] batch, CancellationToken ct)
    {
        var records = batch.Select(c => new IysConsentRecord(
            c.Recipient, c.RecipientType, c.ChannelType, c.Status,
            c.ConsentDate ?? c.LastLocalEventAt,
            c.SourceCode ?? _opt.IysSourceCode,
            c.Id.ToString("N"))).ToArray();

        IysAddResult result;
        try
        {
            result = await _client.AddAsync(records, ct);
        }
        catch (IysConfigurationException cfg)
        {
            // Kalıcı yapılandırma hatası: her kayıt aynı hatayla düşer.
            // Devam etmek bekleyenleri sırayla harcar → boru hattı durur.
            // Kayıtlara DOKUNULMAZ: Failed yazmak, düzeltilebilir bir ayar
            // hatasını kayıt başına kalıcı yara gibi gösterirdi.
            _log.LogError(cfg, "İYS yapılandırma hatası ({Code}) — push boru hattı durdu", cfg.Code);
            throw;
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var c in batch)
            {
                c.PushState = IysPushState.Failed;
                c.LastError = Truncate(ex.Message, 500);
                c.UpdatedAt = now;
                AddEvent(c, IysConsentEventType.PushAttempt, code: null, body: null, error: ex.GetType().Name);
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(ex, "İYS push: {Count} kayıtlık parti başarısız", batch.Length);
            return;
        }

        var stamp = DateTimeOffset.UtcNow;
        foreach (var c in batch)
        {
            AddEvent(c, IysConsentEventType.PushAttempt, result.Code, result.RawBody, error: null);

            if (result.Queued)
            {
                // KUYRUĞA ALINDI — kabul DEĞİL.
                c.PushState = IysPushState.Pushed;
                c.LastPushedAt = stamp;
                c.VerifyAttempts = 0;
                c.NextVerifyAt = IysVerifySchedule.Next(stamp, 0);
                c.LastError = null;
            }
            else
            {
                c.PushState = IysPushState.Failed;
                c.LastError = Truncate($"iys-add code={result.Code}", 500);
            }
            c.UpdatedAt = stamp;
        }
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "İYS push: {Count} kayıt bildirildi (code={Code}) — doğrulama bekliyor",
            batch.Length, result.Code);
    }

    private void AddEvent(
        IysConsent c, IysConsentEventType type, string? code, string? body, string? error)
        => _db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            Recipient = c.Recipient,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = type,
            Status = c.Status,
            ApiResponseCode = code,
            ApiResponseBody = Truncate(body, 2000),
            ErrorCode = error,
        });

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max];
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentPushJobTests`
Expected: PASS (6 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs
git commit -m "feat(iys): onay push işi (20'şerli parti, code 0 ≠ kabul)"
```

---

### Task 10: `IysConsentVerifyJob` — `/iys/search` ile kabulü doğrula

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs`

- [ ] **Step 1: Düşen testleri yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysConsentVerifyJobTests
{
    private const string Phone = "+905551112233";

    private sealed class FakeIysClient : IIysClient
    {
        public Dictionary<string, IysConsentStatus> Answer { get; set; } = new();
        public List<IReadOnlyList<string>> SearchCalls { get; } = new();

        public Task<IysAddResult> AddAsync(IReadOnlyList<IysConsentRecord> items, CancellationToken ct = default)
            => Task.FromResult(new IysAddResult("0", "{}", true));

        public Task<IysSearchResult> SearchAsync(IReadOnlyList<string> recipients, CancellationToken ct = default)
        {
            SearchCalls.Add(recipients);
            return Task.FromResult(new IysSearchResult("0", "{\"code\":\"0\"}", Answer));
        }
    }

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-verify-{Guid.NewGuid():N}").Options);

    private static IysConsentVerifyJob Job(LicenseDbContext db, IIysClient client)
        => new(db, client, Options.Create(new NetgsmOptions { BrandCode = "731734" }),
            NullLogger<IysConsentVerifyJob>.Instance);

    private static async Task<LicenseDbContext> SeedPushedAsync(
        DateTimeOffset? nextVerifyAt = null, int attempts = 0)
    {
        var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(),
            BrandCode = "731734",
            ChannelType = "MESAJ",
            RecipientType = "BIREYSEL",
            Recipient = Phone,
            Status = IysConsentStatus.Onay,
            ConsentDate = now.AddMinutes(-30),
            PushState = IysPushState.Pushed,
            PushDeadline = now.AddDays(3),
            LastPushedAt = now.AddMinutes(-20),
            VerifyAttempts = attempts,
            NextVerifyAt = nextVerifyAt ?? now.AddMinutes(-1),
            LastLocalEventAt = now.AddMinutes(-30),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task ONAY_donerse_Confirmed_olur()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Onay } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.LastVerifiedStatus.Should().Be(IysConsentStatus.Onay);
        row.LastVerifiedAt.Should().NotBeNull();
        row.PushState.Should().Be(IysPushState.Confirmed);
        IysConsentGate.CanSend(row).Should().BeTrue();
    }

    [Fact]
    public async Task RET_donerse_yerel_ONAY_ezilmez_ama_gonderim_kesilir()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Onay, "kişinin bize verdiği onay ispat olarak durur");
        row.LastVerifiedStatus.Should().Be(IysConsentStatus.Ret);
        IysConsentGate.CanSend(row).Should().BeFalse();
    }

    [Fact]
    public async Task Randevusu_gelmemis_kayit_sorgulanmaz()
    {
        using var db = await SeedPushedAsync(nextVerifyAt: DateTimeOffset.UtcNow.AddHours(1));
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.SearchCalls.Should().BeEmpty(
            "İYS işleme anlık değil; erken sorgu işlenmemiş kaydı RET sanar");
    }

    [Fact]
    public async Task ONAY_gelmezse_bir_sonraki_randevu_alinir()
    {
        using var db = await SeedPushedAsync(attempts: 0);
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.VerifyAttempts.Should().Be(1);
        row.NextVerifyAt.Should().NotBeNull();
        row.PushState.Should().Be(IysPushState.Pushed, "takvim bitmeden karar kesinleşmez");
    }

    [Fact]
    public async Task Takvim_tukenince_Failed_olur_ve_admin_listesine_duser()
    {
        using var db = await SeedPushedAsync(attempts: 3);
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Failed);
        row.NextVerifyAt.Should().BeNull();
        row.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sonuc_olay_tablosuna_yazilir()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Onay } };

        await Job(db, client).RunAsync();

        var ev = await db.IysConsentEvents
            .SingleAsync(e => e.EventType == IysConsentEventType.SearchResult);
        ev.Status.Should().Be(IysConsentStatus.Onay);
        ev.ApiResponseCode.Should().Be("0");
    }
}
```

- [ ] **Step 2: Testleri çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentVerifyJobTests`
Expected: FAIL — `IysConsentVerifyJob` türü yok.

- [ ] **Step 3: İşi yaz**

`OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs`:

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Hangfire işi: <c>/iys/search</c> ile İYS'nin gerçek cevabını okur.
///
/// <para><b>Push'tan AYRI bir adım olması bu tasarımın merkezi.</b> Push
/// "kuyruğa alındı" cevabı döner; kabul yalnız burada görülebilir. Sonuç
/// <see cref="IysConsent.LastVerifiedStatus"/>'e yazılır — yerel
/// <see cref="IysConsent.Status"/> asla ezilmez, çünkü o kişinin bize verdiği
/// onayın ispatı (kural 2).</para>
///
/// <para>Randevu takvimi <see cref="IysVerifySchedule"/>: 15dk → 1sa → 6sa →
/// 24sa. Erken sorgu, henüz işlenmemiş kaydı "RET" sanmaya yol açar.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysConsentVerifyJob
{
    /// <summary>Tek sorguda sorulan alıcı sayısı.</summary>
    public const int BatchSize = 20;

    private readonly LicenseDbContext _db;
    private readonly IIysClient _client;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentVerifyJob> _log;

    public IysConsentVerifyJob(
        LicenseDbContext db, IIysClient client,
        IOptions<NetgsmOptions> opt, ILogger<IysConsentVerifyJob> log)
    {
        _db = db;
        _client = client;
        _opt = opt.Value;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.BrandCode)) return;

        var now = DateTimeOffset.UtcNow;
        var due = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Pushed
                        && c.NextVerifyAt != null && c.NextVerifyAt <= now)
            .OrderBy(c => c.NextVerifyAt)
            .Take(BatchSize * 5)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var batch in due.Chunk(BatchSize))
        {
            IysSearchResult result;
            try
            {
                result = await _client.SearchAsync(batch.Select(c => c.Recipient).ToArray(), ct);
            }
            catch (IysConfigurationException cfg)
            {
                // Boru hattı durur. Randevular olduğu yerde kalır: ayar
                // düzeltildiğinde doğrulama kaldığı yerden devam eder.
                _log.LogError(cfg, "İYS yapılandırma hatası ({Code}) — doğrulama durdu", cfg.Code);
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "İYS doğrulama: {Count} kayıtlık sorgu başarısız", batch.Length);
                continue;  // randevu duruyor, bir sonraki koşuda tekrar denenir
            }

            var stamp = DateTimeOffset.UtcNow;
            foreach (var c in batch)
            {
                // Yanıtta hiç görünmeyen alıcı Unknown — fail-closed.
                var status = result.Statuses.TryGetValue(c.Recipient, out var s)
                    ? s : IysConsentStatus.Unknown;

                c.LastVerifiedStatus = status;
                c.LastVerifiedAt = stamp;
                c.VerifyAttempts++;
                c.UpdatedAt = stamp;

                _db.IysConsentEvents.Add(new IysConsentEvent
                {
                    Id = Guid.NewGuid(),
                    Recipient = c.Recipient,
                    OccurredAt = stamp,
                    EventType = IysConsentEventType.SearchResult,
                    Status = status,
                    ApiResponseCode = result.Code,
                    ApiResponseBody = result.RawBody.Length > 2000 ? result.RawBody[..2000] : result.RawBody,
                });

                if (status == IysConsentStatus.Onay)
                {
                    c.PushState = IysPushState.Confirmed;
                    c.NextVerifyAt = null;
                    c.LastError = null;
                    continue;
                }

                // Henüz ONAY değil. "Kayıt yok" ile "reddetti" ayırt
                // edilemediği için beklemekten başka yapacak bir şey yok;
                // takvim tükenene kadar tekrar sorulur.
                var next = IysVerifySchedule.Next(stamp, c.VerifyAttempts);
                c.NextVerifyAt = next;
                if (next is null)
                {
                    c.PushState = IysPushState.Failed;
                    c.LastError = $"iys-not-confirmed status={status}";
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        var confirmed = due.Count(c => c.PushState == IysPushState.Confirmed);
        _log.LogInformation(
            "İYS doğrulama: {Total} kayıt soruldu, {Confirmed} ONAY", due.Count, confirmed);
    }
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsent`
Expected: PASS (Collector + Push + Verify test sınıflarının hepsi).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs
git commit -m "feat(iys): doğrulama işi — kabul kararı yalnız /iys/search ile"
```

---

### Task 11: `IysConsentRecoveryJob` — güvenlik ağı

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Iys/IysConsentRecoveryJob.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentRecoveryJobTests.cs`

Push ve doğrulama işleri periyodik süpürme olduğu için "kayıp enqueue" boşluğunu
kendileri kapatıyor. Bu işin kapattığı boşluk **başka**: `Failed` damgalı kayıtları
kimse geri almıyor. İş onları — son tarih dolmamışsa ve üstünden bir bekleme
geçmişse — `Pending`'e döndürür, ayrıca son tarihi yaklaşan kayıtları günlüğe
yazar (kayıp onay sessizce olmaz).

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentRecoveryJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysConsentRecoveryJobTests
{
    private static LicenseDbContext NewDb() => new(
        new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-rec-{Guid.NewGuid():N}").Options);

    private static IysConsentRecoveryJob Job(LicenseDbContext db)
        => new(db, NullLogger<IysConsentRecoveryJob>.Instance);

    private static IysConsent Row(
        IysPushState state, DateTimeOffset updatedAt, DateTimeOffset? deadline)
        => new()
        {
            Id = Guid.NewGuid(),
            BrandCode = "731734",
            Recipient = "+905551234567",
            Status = IysConsentStatus.Onay,
            ConsentDate = updatedAt,
            LastLocalEventAt = updatedAt,
            PushState = state,
            PushDeadline = deadline,
            LastError = "timeout",
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };

    [Fact]
    public async Task Bekleme_gecmis_Failed_kayit_Pending_e_doner()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        var row = Row(IysPushState.Failed, now - TimeSpan.FromHours(1), now.AddDays(2));
        db.IysConsents.Add(row);
        await db.SaveChangesAsync();

        await Job(db).RunAsync();

        var after = await db.IysConsents.SingleAsync();
        after.PushState.Should().Be(IysPushState.Pending);
        after.LastError.Should().BeNull("yeniden denemeye temiz sayfayla girilir");
    }

    [Fact]
    public async Task Yeni_dusmus_Failed_kayit_hemen_geri_alinmaz()
    {
        // Bekleme olmadan sıcak döngü kurardık: push düşer, recovery anında
        // geri alır, push yine düşer — Netgsm kotası dakikalar içinde biter.
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        db.IysConsents.Add(Row(IysPushState.Failed, now, now.AddDays(2)));
        await db.SaveChangesAsync();

        await Job(db).RunAsync();

        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Failed);
    }

    [Fact]
    public async Task Son_tarihi_gecmis_Failed_kayit_Expired_olur()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        db.IysConsents.Add(Row(
            IysPushState.Failed, now - TimeSpan.FromDays(4), now - TimeSpan.FromHours(1)));
        await db.SaveChangesAsync();

        await Job(db).RunAsync();

        var after = await db.IysConsents.SingleAsync();
        after.PushState.Should().Be(IysPushState.Expired,
            "3 iş günü dolduktan sonra yeniden denemek H467'den başka bir şey getirmez");
    }

    [Fact]
    public async Task Confirmed_kayda_dokunulmaz()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        var row = Row(IysPushState.Confirmed, now - TimeSpan.FromDays(9), now.AddDays(-5));
        db.IysConsents.Add(row);
        await db.SaveChangesAsync();

        await Job(db).RunAsync();

        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Confirmed);
    }

    [Fact]
    public async Task Son_tarihi_yaklasan_dogrulanmamis_kayit_sayilir()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        db.IysConsents.Add(Row(
            IysPushState.Pushed, now - TimeSpan.FromHours(2), now.AddHours(6)));
        db.IysConsents.Add(Row(
            IysPushState.Pushed, now - TimeSpan.FromHours(2), now.AddDays(2)));
        await db.SaveChangesAsync();

        (await Job(db).RunAsync()).Should().Be(1,
            "yalnız 24 saatten az kalmış ve onaylanmamış kayıt uyarı sayılır");
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentRecoveryJobTests`
Expected: FAIL — `IysConsentRecoveryJob` türü yok.

- [ ] **Step 3: İşi yaz**

`OrderDeck.LicenseServer/Services/Iys/IysConsentRecoveryJob.cs`:

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Güvenlik ağı (spec: boru hattı adım 5). İki iş yapar:
///
/// <para>1. <b>Düşmüş kayıtları geri alır.</b> <see cref="IysPushState.Failed"/>
/// damgası geçici hatadan gelir (ağ, zaman aşımı, oran sınırı) ve onu kimse geri
/// almaz — push işi yalnız <c>Pending</c> tarar. Son tarih dolmamışsa ve düşüşün
/// üstünden <see cref="FailedRetryGrace"/> geçmişse kayıt <c>Pending</c>'e döner.
/// Bekleme şart: onsuz push-düşer-recovery-geri-alır sıcak döngüsü Netgsm'in
/// dakikada 10 isteklik kotasını dakikalar içinde tüketir.</para>
///
/// <para>2. <b>Sessiz kalmayı engeller.</b> <see cref="IysConsent.PushDeadline"/>'a
/// 24 saatten az kalmış ve hâlâ onaylanmamış kayıt sayısını günlüğe yazar. Bu
/// kayıtlar son tarihi kaçırırsa onay hukuken geçersiz olur; admin sayfası
/// (çekme) yetmez, günlük de (itme) uyarmalı.</para>
///
/// <para>Son tarihi zaten geçmiş <c>Failed</c> kayıt yeniden denenmez —
/// <see cref="IysPushState.Expired"/> olur. İYS o kayda <c>H467</c>
/// ("consent_date 3 günden eski") döndürmekten başka bir şey yapamaz.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 120)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysConsentRecoveryJob
{
    /// <summary>Düşen kaydın yeniden denenmeden önce bekleyeceği süre.</summary>
    public static readonly TimeSpan FailedRetryGrace = TimeSpan.FromMinutes(20);

    /// <summary>Bu süreden az kalmışsa uyarı verilir.</summary>
    public static readonly TimeSpan DeadlineWarning = TimeSpan.FromHours(24);

    private readonly LicenseDbContext _db;
    private readonly ILogger<IysConsentRecoveryJob> _log;

    public IysConsentRecoveryJob(LicenseDbContext db, ILogger<IysConsentRecoveryJob> log)
    {
        _db = db;
        _log = log;
    }

    /// <returns>Son tarihi yaklaşan, onaylanmamış kayıt sayısı (test için).</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var retryCutoff = now - FailedRetryGrace;

        var failed = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Failed && c.UpdatedAt < retryCutoff)
            .ToListAsync(ct);

        var revived = 0;
        var expired = 0;
        foreach (var c in failed)
        {
            if (c.PushDeadline != null && c.PushDeadline < now)
            {
                c.PushState = IysPushState.Expired;
                c.LastError = "push-deadline-passed";
                expired++;
            }
            else
            {
                c.PushState = IysPushState.Pending;
                c.LastError = null;
                revived++;
            }
            c.UpdatedAt = now;
        }

        var warnCutoff = now + DeadlineWarning;
        var approaching = await _db.IysConsents
            .CountAsync(c => c.PushState != IysPushState.Confirmed
                             && c.PushState != IysPushState.Expired
                             && c.PushDeadline != null
                             && c.PushDeadline > now
                             && c.PushDeadline <= warnCutoff, ct);

        if (failed.Count > 0) await _db.SaveChangesAsync(ct);

        if (revived > 0 || expired > 0)
        {
            _log.LogWarning(
                "İYS kurtarma: {Revived} kayıt yeniden kuyruğa alındı, {Expired} kayıt süresi doldu",
                revived, expired);
        }

        if (approaching > 0)
        {
            _log.LogWarning(
                "İYS son tarih uyarısı: {Count} onayın İYS penceresine 24 saatten az kaldı ve hâlâ doğrulanmadı",
                approaching);
        }

        return approaching;
    }
}
```

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentRecoveryJobTests`
Expected: PASS (5 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysConsentRecoveryJob.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentRecoveryJobTests.cs
git commit -m "feat(iys): kurtarma işi — düşen kayıtları geri al, son tarihi duyur"
```

---

### Task 12: `Program.cs` — DI + periyodik işler

**Files:**
- Modify: `OrderDeck.LicenseServer/Program.cs:163-177` (DI) ve `:862` sonrası (recurring job)

- [ ] **Step 1: Servisleri kaydet**

`Program.cs` içinde `AddScoped<...Sms.SmsCampaignRecoveryJob>();` satırının (satır 177)
hemen ALTINA ekle:

```csharp

        // İYS onay boru hattı. İstemci, SMS sağlayıcısıyla AYNI koşula bağlı:
        // Netgsm yoksa İYS de yok, çünkü İYS'ye erişim Netgsm aracılığıyla.
        // Dev/test'te kayıt yine toplanır ve Pending'de bekler — push işi
        // BrandCode boş olduğu için hiçbir şey göndermez.
        if (smsProvider == "netgsm")
        {
            var iysTimeout = builder.Configuration.GetValue("Netgsm:TimeoutSeconds", 10);
            builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.Iys.IIysClient,
                    OrderDeck.LicenseServer.Services.Iys.NetgsmIysClient>(
                    c => c.Timeout = TimeSpan.FromSeconds(iysTimeout <= 0 ? 10 : iysTimeout));
        }
        else
        {
            builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Iys.IIysClient,
                OrderDeck.LicenseServer.Services.Iys.NullIysClient>();
        }
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Iys.IysConsentCollector>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Iys.IysConsentPushJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Iys.IysConsentVerifyJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Iys.IysConsentRecoveryJob>();
```

- [ ] **Step 2: `NullIysClient`'ı yaz**

Dev ve test ortamında `IIysClient` çözülemezse `IysConsentCollector`'ı kullanan
her uç (kayıt, form gönderimi) DI hatasıyla patlar — onay yolunu ortam ayarı
kırmamalı. Bu istemci hiçbir yere gitmez.

`OrderDeck.LicenseServer/Services/Iys/NullIysClient.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Netgsm yapılandırılmamış ortamlar için istemci. Hiçbir şey göndermez ve
/// <b>hiçbir şeyi onaylamaz</b>: <c>Queued=false</c> döner, sorguya
/// <see cref="IysConsentStatus.Unknown"/> der. Sahte ONAY döndürmek dev'de
/// gerçek gönderim kapısını açar — fail-closed kuralı ortamdan bağımsızdır.
/// </summary>
public sealed class NullIysClient : IIysClient
{
    private readonly ILogger<NullIysClient> _log;

    public NullIysClient(ILogger<NullIysClient> log) => _log = log;

    public Task<IysAddResult> AddAsync(
        IReadOnlyList<IysConsentRecord> items, CancellationToken ct = default)
    {
        _log.LogInformation("İYS yapılandırılmamış: {Count} kayıt gönderilmedi", items.Count);
        return Task.FromResult(new IysAddResult("not-configured", "", Queued: false));
    }

    public Task<IysSearchResult> SearchAsync(
        IReadOnlyList<string> recipients, CancellationToken ct = default)
        => Task.FromResult(new IysSearchResult(
            "not-configured", "", new Dictionary<string, IysConsentStatus>()));
}
```

> `IysAddResult` / `IysSearchResult` / `IysConsentRecord` imzaları Task 6'da
> tanımlandı; buradaki kullanım onlarla birebir aynı.

- [ ] **Step 3: Periyodik işleri kaydet**

`Program.cs` içinde `"sms-campaign-recovery"` bloğunun (satır 859-862) hemen
ALTINA ekle:

```csharp

            // İYS onay push — bekleyen kayıtları süpürür. Onay yazan uçlar
            // ayrıca Enqueue ETMİYOR (spec adım 2'den bilinçli sapma, Task 13):
            // kaçırılan Enqueue kaydı sessizce kaybeder, süpürme kaybetmez ve
            // 3 iş günlük pencerede 5 dakikalık gecikme ölçülemez.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Iys.IysConsentPushJob>(
                "iys-consent-push",
                j => j.RunAsync(CancellationToken.None),
                "*/5 * * * *");  // 5 dakikada bir

            // İYS doğrulama — randevusu gelen kayıtları /iys/search ile sorar.
            // Randevu takvimi kayıt bazında (15dk → 1sa → 6sa → 24sa); bu
            // periyot yalnız "randevusu geçmiş var mı" taraması.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Iys.IysConsentVerifyJob>(
                "iys-consent-verify",
                j => j.RunAsync(CancellationToken.None),
                "*/5 * * * *");  // 5 dakikada bir

            // İYS kurtarma — düşen kayıtları geri alır, son tarihi yaklaşanı
            // günlüğe yazar. Push'tan seyrek koşar: işi bekleme süresi dolmuş
            // kayıtları toplamak, aceleye gerek yok.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Iys.IysConsentRecoveryJob>(
                "iys-consent-recovery",
                j => j.RunAsync(CancellationToken.None),
                "*/15 * * * *");  // 15 dakikada bir
```

- [ ] **Step 4: Derle ve tüm sunucu testlerini çalıştır**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: PASS. Özellikle `ApiFactory` tabanlı testler ayağa kalkmalı — DI
grafiği eksikse hepsi birden düşer, bu adımın asıl kontrolü o.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer/Services/Iys/NullIysClient.cs
git commit -m "feat(iys): DI kayıtları ve periyodik push/doğrulama/kurtarma işleri"
```

---

### Task 13: Onay yazan üç ucu toplayıcıya bağla

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs:13-15, 143-145`
- Modify: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs:58-85, 166-176`
- Modify: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs:20-21, 126-140`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentWiringTests.cs`

Bugün onay üç yerde yazılıyor ve **hiçbiri** İYS'ye haber vermiyor. Üçü de
toplayıcıyı `SaveChangesAsync`'ten **önce** çağırır: aynı transaction, tek
commit. Onay yazılıp olay kaydı yazılmazsa o kayıt görünmez olur (spec adım 1).

> **Spec'ten bilinçli sapma.** Spec adım 2 "commit sonrası Hangfire işi kuyruğa
> alınır" diyor. Enqueue eklemiyoruz: `IysConsentPushJob` zaten 5 dakikada bir
> tüm `Pending` satırları süpürüyor (Task 12). Enqueue üç uca birer bağımlılık
> ve birer "SaveChanges'ten SONRA olmalı" tuzağı ekler; karşılığında 3 iş günlük
> hukuki pencerede ölçülemeyecek bir gecikme kazandırır. Kaçırılan Enqueue
> kaydı sessizce kaybeder — süpürme kaybetmez.

**Kutunun işaretsiz olması ret DEĞİLDİR.** Kayıt formu ve kayıt olma ucu
toplayıcıyı yalnız `smsConsent == true` iken çağırır; profil ucu ise açık
değişimde iki yönde de çağırır, çünkü orada işaretin kaldırılması kişinin
kendi eylemidir (6563: sessizlik ret değildir, ama açık geri çekme rettir).

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentWiringTests.cs`:

```csharp
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// Onayın yazıldığı her uç İYS kaydını da üretmeli. Bu testler boru hattının
/// <b>girişini</b> korur: kaynak uçlardan biri toplayıcıyı çağırmayı bırakırsa
/// o onay hiç İYS'ye gitmez ve kimse fark etmez — 2026-09-17'de tam olarak
/// bu oldu (form onayları hiçbir yerde okunmuyordu).
/// </summary>
public sealed class IysConsentWiringTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IysConsentWiringTests(ApiFactory factory) => _factory = factory;

    private static string NewPhone() => $"+90555{Random.Shared.Next(1000000, 9999999)}";

    [Fact]
    public async Task Form_onayi_IysConsent_satiri_uretir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var phone = NewPhone();

        var config = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Slug = $"iys-{Guid.NewGuid():N}",
            WhatsAppPhone = "+905550000000",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.IntakeFormConfigs.Add(config);
        await db.SaveChangesAsync();

        await svc.SaveSubmissionAsync(
            config.Id,
            youTubeUsername: null, instagramUsername: null,
            facebookUsername: null, tikTokUsername: null,
            legacyUsername: "u", fullName: "Ad Soyad", address: "Adres", phone: phone,
            email: null, tckn: null, whatsAppConsent: false, smsConsent: true,
            ipAddress: "203.0.113.9", userAgent: "wiring-test");

        var row = await db.IysConsents.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Recipient == phone);
        row.Should().NotBeNull();
        row!.Status.Should().Be(IysConsentStatus.Onay);
        row.PushState.Should().Be(IysPushState.Pending);

        var ev = await db.IysConsentEvents.AsNoTracking()
            .SingleAsync(e => e.Recipient == phone);
        ev.SourceTable.Should().Be("IntakeFormSubmission");
        ev.ProofIp.Should().Be("203.0.113.9", "6563 ispat yükü olay tablosunda yaşar");
    }

    [Fact]
    public async Task Form_isaretsiz_kutusu_ret_yazmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var phone = NewPhone();

        var config = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Slug = $"iys-{Guid.NewGuid():N}",
            WhatsAppPhone = "+905550000000",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.IntakeFormConfigs.Add(config);
        await db.SaveChangesAsync();

        await svc.SaveSubmissionAsync(
            config.Id,
            youTubeUsername: null, instagramUsername: null,
            facebookUsername: null, tikTokUsername: null,
            legacyUsername: "u", fullName: "Ad Soyad", address: "Adres", phone: phone,
            email: null, tckn: null, whatsAppConsent: false, smsConsent: false,
            ipAddress: "203.0.113.9", userAgent: "wiring-test");

        (await db.IysConsents.AnyAsync(c => c.Recipient == phone)).Should().BeFalse(
            "6563: sessizlik ret değildir — işaretsiz kutu geri çekme sayılmaz");
    }

    [Fact]
    public async Task Profilden_acik_ret_Ret_yazar()
    {
        var phone = NewPhone();
        var (client, _) = await ShopperTestSetup.RegisterAsync(
            _factory, phone, smsConsent: true);

        var resp = await client.PatchAsJsonAsync(
            "/api/v1/shopper/me", new { smsConsent = false });
        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.IysConsents.AsNoTracking().SingleAsync(c => c.Recipient == phone);
        row.Status.Should().Be(IysConsentStatus.Ret,
            "profilde kutuyu kaldırmak kişinin kendi açık eylemi — bu ret");
    }
}
```

> `ShopperTestSetup.RegisterAsync` bu repoda yok. Adımı uygularken
> `OrderDeck.LicenseServer.Tests/Controllers/Shopper/` altındaki mevcut
> shopper testlerinden birinin kayıt yardımcısını birebir kopyala (aynı
> `RegisterRequest` alanları, aynı lisans/anahtar kurulumu) ve bu test
> dosyasının içine `private static async Task<(HttpClient, Guid)> RegisterAsync(...)`
> olarak yaz. Yeni paylaşılan yardımcı sınıf **oluşturma** — kapsam dışı.

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~IysConsentWiringTests`
Expected: FAIL — `IysConsents` boş; hiçbir uç toplayıcıyı çağırmıyor.

- [ ] **Step 3: `IntakeFormService`'i bağla**

`IntakeFormService.cs` içindeki alan ve kurucuyu (satır 13-15) bununla değiştir:

```csharp
    private readonly LicenseDbContext _db;
    private readonly Iys.IysConsentCollector _iys;

    public IntakeFormService(LicenseDbContext db, Iys.IysConsentCollector iys)
    {
        _db = db;
        _iys = iys;
    }
```

Aynı dosyada `_db.IntakeFormSubmissions.Add(sub);` ile `await _db.SaveChangesAsync(ct);`
arasına (satır 143-144):

```csharp
        _db.IntakeFormSubmissions.Add(sub);

        // İYS onay boru hattı — aynı transaction. Onay yazılıp İYS kaydı
        // yazılmazsa o onay üç iş günü içinde bildirilemez ve hukuken geçersiz
        // olur. İşaretsiz kutu için ÇAĞIRMIYORUZ: 6563'e göre sessizlik ret
        // değildir, kişi önceki onayını geri çekmiş sayılmaz.
        if (smsConsent)
        {
            await _iys.RecordAsync(
                phone, consented: true, occurredAt: sub.SubmittedAt,
                sourceTable: "IntakeFormSubmission", sourceId: sub.Id,
                ip: ipAddress, userAgent: userAgent, ct: ct);
        }

        await _db.SaveChangesAsync(ct);
```

- [ ] **Step 4: `ShopperAuthController`'ı bağla**

Alan listesine (satır 65'ten sonra) ve kurucuya ekle:

```csharp
    private readonly Services.Iys.IysConsentCollector _iys;
```

Kurucu imzasına `ILogger<ShopperAuthController> log` parametresinden **önce**
`Services.Iys.IysConsentCollector iys,` ekle, gövdesine `_iys = iys;`.

`_db.Shoppers.Add(shopper);` satırının hemen altına:

```csharp
            _db.Shoppers.Add(shopper);

            // Kayıt anındaki onay. İşaretsizse çağrılmaz (sessizlik ret değil).
            if (req.SmsConsent)
            {
                await _iys.RecordAsync(
                    shopper.Phone, consented: true, occurredAt: now,
                    sourceTable: "Shopper", sourceId: shopper.Id,
                    ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    userAgent: Request.Headers.UserAgent.ToString(), ct: ct);
            }
```

- [ ] **Step 5: `ShopperMeController`'ı bağla**

Alan ve kurucuyu (satır 20-21) bununla değiştir:

```csharp
    private readonly LicenseDbContext _db;
    private readonly Services.Iys.IysConsentCollector _iys;

    public ShopperMeController(LicenseDbContext db, Services.Iys.IysConsentCollector iys)
    {
        _db = db;
        _iys = iys;
    }
```

Onay bloğunu (satır 126-140) bununla değiştir:

```csharp
        // Yalnızca DEĞİŞİMDE tarih yaz: idempotent PUT aynı değeri tekrar
        // gönderirse onay/ret anı kaymamalı (ispat tarihi; bkz. Shopper).
        if (req.SmsConsent is not null && req.SmsConsent.Value != shopper.SmsConsent)
        {
            var consentAt = DateTimeOffset.UtcNow;
            shopper.SmsConsent = req.SmsConsent.Value;
            if (req.SmsConsent.Value)
            {
                shopper.SmsConsentAt = consentAt;
                shopper.SmsConsentSource = "profile";
            }
            else
            {
                shopper.SmsConsentRevokedAt = consentAt;
            }

            // Burada İKİ YÖNDE de bildiriyoruz: profilde kutuyu kaldırmak
            // kişinin açık geri çekme eylemidir, form kutusunun boş kalması
            // gibi sessizlik değil. Gönderim kapısı yerel Status'u da okuduğu
            // için mesaj İYS'yi beklemeden anında kesilir (kural 4).
            await _iys.RecordAsync(
                shopper.Phone, consented: req.SmsConsent.Value, occurredAt: consentAt,
                sourceTable: "Shopper", sourceId: shopper.Id,
                ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString(), ct: ct);
        }
```

- [ ] **Step 6: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: PASS. Kurucu imzası değişen üç tip de DI'dan çözülüyor; kırılan bir
yer kalırsa derleme hatası olarak çıkar.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentWiringTests.cs
git commit -m "feat(iys): form, kayıt ve profil onaylarını boru hattına bağla"
```

---

## Faz 4 — Gönderim kapısı + görünürlük

### Task 14: Gönderim anında yeniden kontrol (kural 7)

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs:93-113`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignIysGateTests.cs`

Alıcı listesi kampanya **oluşturulurken** dondurulup `SmsCampaignRecipients`'a
yazılıyor. Kampanya kuyrukta beklerken biri onayını geri çekerse ya da İYS o
numaraya RET derse, bugünkü kod yine de mesaj atar. Kapı gönderimin **kendi
anında** yeniden okunmalı.

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignIysGateTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// Kural 7: alıcı listesi kampanya oluşturulurken donuyor, gönderim dakikalar
/// (kurtarma devralırsa saatler) sonra olabiliyor. Aradaki geri çekme ya da
/// İYS RET'i gönderimi kesmeli — aksi hâlde iznini geri çekmiş kişiye ticari
/// SMS gider ve bu 6563 ihlalidir.
/// </summary>
public sealed class SmsCampaignIysGateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public SmsCampaignIysGateTests(ApiFactory factory) => _factory = factory;

    [Theory]
    // (yerel Status, İYS'nin cevabı, gönderilsin mi)
    [InlineData(IysConsentStatus.Onay, IysConsentStatus.Onay, true)]
    [InlineData(IysConsentStatus.Onay, IysConsentStatus.Ret, false)]   // İYS kesti
    [InlineData(IysConsentStatus.Ret, IysConsentStatus.Onay, false)]   // kişi geri çekti
    [InlineData(IysConsentStatus.Unknown, IysConsentStatus.Onay, false)]
    public void Kapi_iki_alani_da_ONAY_gormeden_acilmaz(
        IysConsentStatus local, IysConsentStatus verified, bool expected)
    {
        var consent = new IysConsent { Status = local, LastVerifiedStatus = verified };
        IysConsentGate.CanSend(consent).Should().Be(expected);
    }

    [Fact]
    public void Dogrulanmamis_kayit_gonderime_kapali()
    {
        // Fail-closed: "kayıt yok" ile "reddetti" ayırt edilemiyor (kural 3).
        var consent = new IysConsent
        {
            Status = IysConsentStatus.Onay,
            LastVerifiedStatus = null,
        };
        IysConsentGate.CanSend(consent).Should().BeFalse();
    }

    [Fact]
    public void Kayit_hic_yoksa_gonderime_kapali()
        => IysConsentGate.CanSend(null).Should().BeFalse();

    [Fact]
    public async Task Onayi_geri_cekilmis_alici_gonderilmez_ve_iade_edilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var campaignId = await SeedCampaignAsync(db, phone);

        // İYS kaydı yok → kapı kapalı.
        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("failed");
        recipient.Error.Should().Be("iys-consent-missing");

        var campaign = await db.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        campaign.RefundedCredits.Should().BeGreaterThan(0,
            "gönderilmeyen mesajın kredisi yayıncıda kalmalı");
    }
}
```

> `SeedCampaignAsync` bu dosyada yok. Adımı uygularken
> `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignSendJobTests.cs`
> içindeki mevcut kampanya kurulum yardımcısını (lisans + kredi + kampanya +
> tek alıcı) bu dosyaya `private static async Task<Guid> SeedCampaignAsync(
> LicenseDbContext db, string phone)` olarak kopyala. Paylaşılan yeni yardımcı
> sınıf **oluşturma** — kapsam dışı.

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~SmsCampaignIysGateTests`
Expected: FAIL — `IysConsentGate` türü yok.

- [ ] **Step 3: Kapıyı gönderim işine bağla**

`IysConsentGate` Task 8'de yazıldı (`OrderDeck.LicenseServer/Services/Iys/IysConsentCollector.cs`
dosyasında). Burada yalnız çağrılıyor.

`SmsCampaignSendJob.cs` içinde `recipients` sorgusundan sonra, `foreach`
döngüsünden **önce** ekle (satır 95 ile 97 arası):

```csharp
        // Kural 7: liste kampanya oluşturulurken donduruldu; gönderim şimdi.
        // Aradaki geri çekme ya da İYS RET'i burada okunur. Tek sorguyla
        // çekiliyor — alıcı başına sorgu, bin kişilik kampanyada bin gidiş.
        var phones = recipients.Select(r => r.Phone).Distinct().ToList();
        var consents = await _db.IysConsents
            .Where(c => c.BrandCode == _iysBrandCode && phones.Contains(c.Recipient))
            .ToDictionaryAsync(c => c.Recipient, ct);
```

`foreach` gövdesinin başına, `try` bloğunun **üstüne**:

```csharp
        foreach (var r in recipients)
        {
            consents.TryGetValue(r.Phone, out var consent);
            if (!IysConsentGate.CanSend(consent))
            {
                // "failed" seçilmesi bilinçli: mevcut iade yolu bu durumu
                // sayıyor, yani gönderilmeyen mesajın kredisi kendiliğinden
                // yayıncıya dönüyor. Yeni bir durum eklemek o yolu ikizlerdi.
                r.Status = "failed";
                r.Error = consent is null ? "iys-consent-missing" : "iys-consent-not-onay";
                r.SentAt = null;
                campaign.ClaimedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                continue;
            }

            try
            {
```

Sınıfa marka kodunu ekle: alan listesine

```csharp
    private readonly string _iysBrandCode;
```

ve kurucuya `IOptions<NetgsmOptions> netgsm` parametresi, gövdesine
`_iysBrandCode = netgsm.Value.BrandCode;`. Dosyanın başına
`using Microsoft.Extensions.Options;` ve
`using OrderDeck.LicenseServer.Services.Iys;` ekle.

- [ ] **Step 4: Testleri çalıştır, geçtiğini gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~SmsCampaign`
Expected: PASS — yeni kapı testleri **ve** mevcut `SmsCampaignSendJobTests`.

> **Dikkat:** mevcut gönderim testleri İYS kaydı kurmadığı için kapı onları
> kapatacak ve düşecekler. Bu bir gerileme değil, kapının kanıtı. Düşen her
> testin kurulumuna, o alıcıyı `Status = Onay` + `LastVerifiedStatus = Onay`
> yapan bir `IysConsent` satırı ekle. Test **beklentisini** gevşetme — kapıyı
> devre dışı bırakan bir ayar ekleme.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs OrderDeck.LicenseServer.Tests/Services/Sms/
git commit -m "feat(iys): gönderim anında onay kapısı — donmuş liste yetmez"
```

---

### Task 15: Admin görünürlük sayfası

**Files:**
- Create: `OrderDeck.LicenseServer/Pages/Admin/Iys/Index.cshtml`
- Create: `OrderDeck.LicenseServer/Pages/Admin/Iys/Index.cshtml.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Index.cshtml:51` (uyarı şeridi)

Spec adım 6: son tarihi yaklaşan doğrulanmamış kayıt varsa **sessiz kalmak
yasak**. Kurtarma işi günlüğe yazıyor (itme); bu sayfa insanın bakabileceği
yüzü (çekme). Salt okunur — bu sayfadan İYS'ye hiçbir şey gönderilmez.

- [ ] **Step 1: Sayfa modelini yaz**

`OrderDeck.LicenseServer/Pages/Admin/Iys/Index.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;

namespace OrderDeck.LicenseServer.Pages.Admin.Iys;

/// <summary>
/// İYS onay boru hattının durumu. <b>Salt okunur.</b> Buradan İYS'ye kayıt
/// gönderilmez: elle push düğmesi, tek doğruluk kaynağı olan işlerin yanına
/// ikinci bir yol açar ve iki yolun sırası çakışırsa hangisinin yazdığı
/// belirsizleşir.
/// </summary>
public class IndexModel : PageModel
{
    private const int ListSize = 50;

    private readonly LicenseDbContext _db;
    public IndexModel(LicenseDbContext db) => _db = db;

    public sealed record Row(
        string Recipient,
        IysConsentStatus Status,
        IysPushState PushState,
        IysConsentStatus? Verified,
        DateTimeOffset? Deadline,
        DateTimeOffset? LastPushedAt,
        string? LastError);

    public sealed record BadPhone(string Raw, DateTimeOffset OccurredAt, string? SourceTable);

    public int PendingCount { get; private set; }
    public int PushedCount { get; private set; }
    public int ConfirmedCount { get; private set; }
    public int FailedCount { get; private set; }
    public int ExpiredCount { get; private set; }

    public List<Row> Urgent { get; private set; } = new();
    public List<Row> Problem { get; private set; } = new();
    public List<BadPhone> BadPhones { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var counts = await _db.IysConsents.AsNoTracking()
            .GroupBy(c => c.PushState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(IysPushState s) => counts.FirstOrDefault(c => c.State == s)?.Count ?? 0;
        PendingCount = Count(IysPushState.Pending);
        PushedCount = Count(IysPushState.Pushed);
        ConfirmedCount = Count(IysPushState.Confirmed);
        FailedCount = Count(IysPushState.Failed);
        ExpiredCount = Count(IysPushState.Expired);

        // Acil: 3 iş günlük pencereye 24 saatten az kalmış ve hâlâ
        // onaylanmamış. Bu satırlar son tarihi kaçırırsa onay geçersiz olur.
        var warnCutoff = now + IysConsentRecoveryJob.DeadlineWarning;
        Urgent = await _db.IysConsents.AsNoTracking()
            .Where(c => c.PushState != IysPushState.Confirmed
                        && c.PushState != IysPushState.Expired
                        && c.PushDeadline != null
                        && c.PushDeadline > now
                        && c.PushDeadline <= warnCutoff)
            .OrderBy(c => c.PushDeadline)
            .Take(ListSize)
            .Select(c => new Row(c.Recipient, c.Status, c.PushState, c.LastVerifiedStatus,
                c.PushDeadline, c.LastPushedAt, c.LastError))
            .ToListAsync(ct);

        // Sorunlu: düşmüş ya da penceresi kaçmış. Elle karar gerektirir —
        // kişiden yeni onay istemek dışında yapılabilecek bir şey yok.
        Problem = await _db.IysConsents.AsNoTracking()
            .Where(c => c.PushState == IysPushState.Failed
                        || c.PushState == IysPushState.Expired)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(ListSize)
            .Select(c => new Row(c.Recipient, c.Status, c.PushState, c.LastVerifiedStatus,
                c.PushDeadline, c.LastPushedAt, c.LastError))
            .ToListAsync(ct);

        // E.164'e çevrilemeyen numaralar: kayıt satırı açılmadı, yalnız olay
        // yazıldı. Sessizce atlamak 284 kaydı kaybetme biçimimizdi.
        BadPhones = await _db.IysConsentEvents.AsNoTracking()
            .Where(e => e.ErrorCode == "invalid-phone")
            .OrderByDescending(e => e.OccurredAt)
            .Take(ListSize)
            .Select(e => new BadPhone(e.Recipient, e.OccurredAt, e.SourceTable))
            .ToListAsync(ct);
    }
}
```

- [ ] **Step 2: Görünümü yaz**

`OrderDeck.LicenseServer/Pages/Admin/Iys/Index.cshtml`:

```cshtml
@page "/admin/iys"
@model IndexModel
@{
    ViewData["Title"] = "İYS Onayları";
}
<h1 class="h3 mb-4">İYS Onay Boru Hattı</h1>

<div class="row g-3 mb-4">
    <div class="col-md-2">
        <div class="card text-bg-secondary"><div class="card-body">
            <h6 class="card-subtitle mb-2 text-white-50">Bekleyen</h6>
            <p class="card-text display-6" data-stat="iys-pending">@Model.PendingCount</p>
        </div></div>
    </div>
    <div class="col-md-2">
        <div class="card text-bg-info"><div class="card-body">
            <h6 class="card-subtitle mb-2 text-white-50">Bildirildi</h6>
            <p class="card-text display-6" data-stat="iys-pushed">@Model.PushedCount</p>
        </div></div>
    </div>
    <div class="col-md-2">
        <div class="card text-bg-success"><div class="card-body">
            <h6 class="card-subtitle mb-2 text-white-50">Onaylandı</h6>
            <p class="card-text display-6" data-stat="iys-confirmed">@Model.ConfirmedCount</p>
        </div></div>
    </div>
    <div class="col-md-2">
        <div class="card text-bg-warning"><div class="card-body">
            <h6 class="card-subtitle mb-2 text-white-50">Düştü</h6>
            <p class="card-text display-6" data-stat="iys-failed">@Model.FailedCount</p>
        </div></div>
    </div>
    <div class="col-md-2">
        <div class="card text-bg-danger"><div class="card-body">
            <h6 class="card-subtitle mb-2 text-white-50">Süresi Doldu</h6>
            <p class="card-text display-6" data-stat="iys-expired">@Model.ExpiredCount</p>
        </div></div>
    </div>
</div>

<div class="alert alert-secondary">
    <strong>&quot;Bildirildi&quot; kabul edilmiş demek değildir.</strong>
    Netgsm'in <code>code 0</code> yanıtı yalnızca &quot;kuyruğa alındı&quot; anlamına
    gelir. Gönderim yalnız <strong>Onaylandı</strong> sütunundaki kayıtlara açıktır.
</div>

<h2 class="h5 mt-4">Son tarihi yaklaşanlar (&lt; 24 saat)</h2>
@if (!Model.Urgent.Any())
{
    <p class="text-muted">Son tarihi yaklaşan kayıt yok.</p>
}
else
{
    <div class="alert alert-danger">
        Bu kayıtlar üç iş günlük pencereyi kaçırırsa onay <strong>hukuken
        geçersiz</strong> olur ve kişiden yeniden onay alınması gerekir.
    </div>
    <table class="table table-sm table-hover" data-table="iys-urgent">
        <thead><tr>
            <th>Alıcı</th><th>Yerel</th><th>Durum</th><th>İYS</th>
            <th>Son tarih</th><th>Hata</th>
        </tr></thead>
        <tbody>
        @foreach (var r in Model.Urgent)
        {
            <tr>
                <td>@r.Recipient</td>
                <td>@r.Status</td>
                <td>@r.PushState</td>
                <td>@(r.Verified?.ToString() ?? "—")</td>
                <td>@r.Deadline?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")</td>
                <td class="text-muted small">@r.LastError</td>
            </tr>
        }
        </tbody>
    </table>
}

<h2 class="h5 mt-4">Düşmüş / süresi dolmuş</h2>
@if (!Model.Problem.Any())
{
    <p class="text-muted">Sorunlu kayıt yok.</p>
}
else
{
    <table class="table table-sm table-hover" data-table="iys-problem">
        <thead><tr>
            <th>Alıcı</th><th>Yerel</th><th>Durum</th><th>İYS</th>
            <th>Son push</th><th>Hata</th>
        </tr></thead>
        <tbody>
        @foreach (var r in Model.Problem)
        {
            <tr>
                <td>@r.Recipient</td>
                <td>@r.Status</td>
                <td>@r.PushState</td>
                <td>@(r.Verified?.ToString() ?? "—")</td>
                <td>@r.LastPushedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")</td>
                <td class="text-muted small">@r.LastError</td>
            </tr>
        }
        </tbody>
    </table>
}

<h2 class="h5 mt-4">Çevrilemeyen numaralar</h2>
@if (!Model.BadPhones.Any())
{
    <p class="text-muted">Çevrilemeyen numara yok.</p>
}
else
{
    <p class="text-muted small">
        E.164'e çevrilemediği için kayıt satırı açılmadı. Onay alınmış ama
        İYS'ye bildirilemiyor; numaranın kaynakta düzeltilmesi gerekir.
    </p>
    <table class="table table-sm" data-table="iys-bad-phones">
        <thead><tr><th>Ham değer</th><th>Zaman</th><th>Kaynak</th></tr></thead>
        <tbody>
        @foreach (var b in Model.BadPhones)
        {
            <tr>
                <td><code>@b.Raw</code></td>
                <td>@b.OccurredAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")</td>
                <td>@b.SourceTable</td>
            </tr>
        }
        </tbody>
    </table>
}
```

- [ ] **Step 3: Dashboard'a uyarı şeridi ekle**

`OrderDeck.LicenseServer/Pages/Admin/Index.cshtml.cs` içinde mevcut
`PendingDeletionRequests` özelliğinin yanına `public int IysDeadlineWarnings { get; private set; }`
ekle ve `OnGetAsync` içinde doldur:

```csharp
        var iysNow = DateTimeOffset.UtcNow;
        var iysCutoff = iysNow + OrderDeck.LicenseServer.Services.Iys.IysConsentRecoveryJob.DeadlineWarning;
        IysDeadlineWarnings = await _db.IysConsents
            .CountAsync(c => c.PushState != Domain.IysPushState.Confirmed
                             && c.PushState != Domain.IysPushState.Expired
                             && c.PushDeadline != null
                             && c.PushDeadline > iysNow
                             && c.PushDeadline <= iysCutoff, ct);
```

`Pages/Admin/Index.cshtml` sonuna (satır 51'deki `}` sonrası) ekle:

```cshtml

@if (Model.IysDeadlineWarnings > 0)
{
    <div class="alert alert-warning d-flex justify-content-between align-items-center mt-3">
        <span>
            <strong data-stat="iys-deadline-warnings">@Model.IysDeadlineWarnings</strong>
            onayın İYS bildirim penceresine 24 saatten az kaldı.
        </span>
        <a asp-page="/Admin/Iys/Index" class="btn btn-sm btn-warning">Görüntüle</a>
    </div>
}
```

- [ ] **Step 4: Derle ve testleri çalıştır**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: PASS. Admin sayfaları için yönlendirme/yetki testleri varsa
`/admin/iys` de AdminCookie şemasına bağlı olmalı — `Pages/Admin/` altındaki
klasör kuralı bunu zaten sağlıyor, ek yapılandırma yok.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Admin/Iys/ OrderDeck.LicenseServer/Pages/Admin/Index.cshtml OrderDeck.LicenseServer/Pages/Admin/Index.cshtml.cs
git commit -m "feat(iys): admin görünürlük sayfası ve son tarih uyarısı"
```

---

### Task 16: Marka kodu ayarının varsayılanı

**Files:**
- Modify: `OrderDeck.LicenseServer/appsettings.json:26-34` (`Netgsm` bloğu)

Repodaki `docker-compose.yml` **yerel geliştirme** dosyası; hiçbir `Netgsm__*`
eşlemesi içermiyor ve Netgsm kimliği orada yok. Prod yapılandırması VPS'teki
compose + `.env` ikilisinde yaşıyor ve repoda değil. Bu yüzden repo tarafında
yapılacak tek şey, ayarın **varlığını** varsayılan dosyada ilan etmek.

`BrandCode` boşken push işi hiçbir şey göndermez (Task 9, Step 3'teki ilk
kontrol). Yani bu ayar prod'a girene kadar boru hattı **kapalı** çalışır: kod
güvenlidir, ama kayıtlar üç iş günü `Pending` bekler ve sonra `Expired` olur.

- [ ] **Step 1: `appsettings.json`'a alanı ekle**

`Netgsm` bloğunda `"BaseUrl"` satırının hemen **üstüne**:

```json
    "BrandCode": "",
    "IysSourceCode": "HS_WEB",
```

Boş varsayılan bilinçli: değer yalnız prod'da tanımlı, ve boşluk boru hattını
açmamak demek. Gerçek marka kodunu repoya yazmıyoruz.

- [ ] **Step 2: Doğrula ve commit**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Expected: Build succeeded (JSON bozulmadıysa).

```bash
git add OrderDeck.LicenseServer/appsettings.json
git commit -m "chore(iys): marka kodu ayarının boş varsayılanı"
```

- [ ] **Step 3: VPS'te değeri yaz — ELLE, İNSAN ELİYLE, DEPLOY SONRASI**

> **Bu adımı ajan çalıştırmaz ve bu planın parçası olarak yapılmaz.** Değer
> yazıldığı anda boru hattı gerçek İYS'ye yazmaya başlar.
>
> VPS'teki compose dosyasında license-server servisinin `environment:`
> bloğuna:
> ```yaml
>       Netgsm__BrandCode: "${NETGSM_BRANDCODE}"
> ```
> ve `.env`'e:
> ```
> NETGSM_BRANDCODE=731734
> ```
>
> Açmadan önce: Admin → İYS sayfasındaki **Bekleyen** sayısına bak. Değer
> yazılır yazılmaz o kayıtların tamamı ilk push turunda İYS'ye gider. Sayı
> beklenenden büyükse önce nedenini bul — bu adım geri alınamaz, İYS'ye
> gönderilen kayıt geri çağrılamaz.
>
> Sonra `docker compose up -d license-server`, 20 dakika bekle ve Admin → İYS
> sayfasında **Onaylandı** sayacının arttığını doğrula. Artmıyorsa
> `docker logs orderdeck-license 2>&1 | grep -i iys` çıktısına bak.

---

## Kapsam doğrulaması — spec → görev eşlemesi

| Spec bölümü | Görev |
|---|---|
| Telefon normalizasyonu (önkoşul) | Task 1, 2 |
| Veri modeli — `IysConsent` | Task 3, 4 |
| Veri modeli — `IysConsentEvent` (ekle-only) | Task 3, 4 |
| Boru hattı 1 — olay yakalama, aynı transaction | Task 8 (collector), Task 13 (üç uç) |
| Boru hattı 2 — kuyruk | **bilinçli sapma**, aşağıda |
| Boru hattı 3 — push, 20'şerli parti | Task 9 |
| Boru hattı 4 — ayrı doğrulama adımı, artan aralık | Task 5 (`IysVerifySchedule`), Task 10 |
| Boru hattı 5 — güvenlik ağı | Task 11 |
| Boru hattı 6 — son tarih uyarısı | Task 11 (sayım + log), Task 15 (admin şerit) |
| Ayar `NETGSM_BRANDCODE` | Task 12 (okuma), Task 16 (varsayılan + VPS notu) |
| Kural 1 — RET kendiliğinden ONAY olmaz | Task 8 |
| Kural 2 — İYS RET'i yerel ONAY'ı ezmez, gönderimi keser | Task 8 (`IysConsentGate`), Task 10, Task 14 |
| Kural 3 — "kayıt yok" ≡ "reddetti", fail-closed | Task 8 (`CanSend`), Task 12 (`NullIysClient`) |
| Kural 4 — yerel ret push'u beklemez | Task 8 + Task 14 (kapı `Status`'a da bakar) |
| Kural 5 — marka bazında ayrı | Task 3/4 (tekil index'te `BrandCode`), Task 14 (sorgu filtresi) |
| Kural 6 — backfill yok | Planda backfill görevi **yok**; bu kasıtlı |
| Kural 7 — gönderim anında yeniden kontrol | Task 14 |
| `Expired` kalıcı değil, yeni onay yeni pencere açar | Task 8 |
| Hata: geçici | Task 9 (`Failed`), Task 11 (geri alma) |
| Hata: kalıcı-veri (`H467`) | Task 9, Task 10 (`Expired`/`Failed`, denenmez), Task 15 (liste) |
| Hata: kalıcı-yapılandırma (`code 60`/`30`) | Task 7 (`IysConfigurationException`), Task 9 + 10 (durur + `LogError`) |
| Admin görünürlüğü | Task 15 |
| Loglama: `PiiMasker.MaskPhone`, ham yanıt olay tablosunda | Task 8, Task 9 |
| Test: normalizer | Task 1, 2 |
| Test: 7 geçiş kuralı | Task 8, Task 10, Task 14 |
| Test: `code 0` ≠ `Confirmed` | Task 7 (`AddAsync_code_sifir_yalnizca_kuyruga_alindi_demektir`), Task 9 (`Code_sifir_Confirmed_YAPMAZ_yalnizca_Pushed`) |
| Test: kampanya sonrası ret | Task 14 |
| Test: eşzamanlılık / tekil index (Testcontainers) | Task 4 |

### Bilinçli sapmalar

**1. Commit sonrası `Enqueue` yok (spec boru hattı adımı 2).**
Üç kaynağın üçüne de `IBackgroundJobClient` bağımlılığı eklemek ve "bu satır
`SaveChangesAsync`'ten *sonra* çalışmalı" kuralını üç yerde korumak gerekirdi.
Karşılığında kazanılan şey gecikme: en kötü 5 dakika. Yasal pencere **üç iş
günü**. Ayrıca kaçan bir `Enqueue` kaydı sessizce kaybeder, kaçan bir tarama
turu kaybetmez — bir sonraki tur aynı satırı yine görür. `*/5` periyodik
push işi (Task 12) bu adımın yerine geçer.

**2. Bozuk telefon `IysConsent` satırı yazmaz (spec bunu açıkça söylemiyor).**
E.164'e çevrilemeyen numara için yalnız `IysConsentEvent` yazılır
(`ErrorCode = "invalid-phone"`). Gerekçe: tekil index'in anahtarı
`Recipient`; oraya tahmin edilmiş bir numara yazmak yanlış kişiye ticari
mesaj göndermenin en kısa yolu. Kayıt yine de görünür — Task 15'teki
**Bozuk numara** listesi bu olaylardan doldurulur.

**3. Admin sayfası salt-okunur (spec "admin görünürlüğü" diyor, eylem
istemiyor).** "Elle push et" düğmesi ikinci bir doğruluk kaynağı yaratır:
aynı kaydı hem iş hem operatör iterken `VerifyAttempts` takvimi bozulur.
Sayfanın işi görünürlük.

---

## Yürütme

Plan bittiğinde sıra şu: Task 1 → 16, aralarında atlama yok. Faz sınırları
(1-2 / 3-4 / 5-13 / 14-16) doğal PR sınırlarıdır — Faz 3'ün tamamı tek başına
büyükse Task 5-7 (sözleşme) ve Task 8-13 (davranış) diye ikiye ayrılabilir.

Her görev kendi testini kendi adımında yazar ve kendi commit'ini atar. Bir
görev kırmızı testle bitiyorsa bir sonrakine geçilmez.

**Dikkat — Task 14 mevcut testleri kıracak.** `SmsCampaignSendJobTests`
bugün İYS satırı olmayan alıcılarla mesaj gönderildiğini varsayıyor. Doğru
düzeltme testlere `IysConsent` satırı **eklemektir**; assertion gevşetmek ya
da kapıya "test modunda atla" anahtarı koymak değildir. O anahtar, kapının
var olma sebebini ortadan kaldırır.

**Dikkat — hiçbir görev `tmp/` veya `web/tsconfig.json` dosyasını
stage'lemez.** İkisi de çalışma ağacında duruyor ve bu işle ilgisiz.
