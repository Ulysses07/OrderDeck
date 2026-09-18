# Çok yayıncılı SMS + İYS — Plan 1: kiracı izolasyonu

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** İYS onay boru hattındaki her kayıt, çağrı ve olay bir yayıncıya (lisansa) bağlansın; bir yayıncının onayı, kimliği veya hatası bir başkasının altına karışamasın.

**Architecture:** Yeni `NetgsmAccount` tablosu lisans → (abone no, şifre, başlık, marka kodu) eşlemesini tutar; şifre `IDataProtector` ile şifreli. Marka artık global `NetgsmOptions.BrandCode`'dan değil bu tablodan çözülür. `IysConsentCollector.RecordAsync` `licenseId` alır, marka çözülemezse **satır açmaz**. `IIysClient` her çağrıda değişmez bir `IysAccountContext` alır — yanlış marka altında sorgu yapmak yapısal olarak imkânsız hâle gelir. Push ve verify işleri marka başına döner, marka başına try/catch ve marka başına parti sınırı uygular.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server / InMemory), `Microsoft.AspNetCore.DataProtection`, Hangfire, xUnit + FluentAssertions, Testcontainers (`SqlServerContainerFixture`).

**Spec:** `docs/superpowers/specs/2026-09-18-coklu-yayinci-sms-iys-design.md` (§1.3, §4, §4.1, §4.2, §5.1, §5.1b, §5.2, §10)

**Bu plan neyi AÇMAZ:** Boru hattı kapalı kalır. Global `Netgsm__BrandCode` ayarı Faz 6'da **tamamen silindiği** için kapıyı artık yapılandırma değil veri tutuyor: `NetgsmAccount` tablosunda `Status = "verified"` bir satır yoksa hiçbir iş bir şey göndermez. Bu plan prod'a **hiç satır yazmaz**. Boru hattını açmak spec §10'a göre ayrı ve geri alınamaz bir adımdır; bu plan onun **ön koşulunu** karşılar, adımı atmaz.

---

## Kapsam sınırı (bilerek dışarıda)

Bu plan yalnız **kiracı izolasyonunu** kapsar. Spec'in geri kalanı üç ayrı plana bırakıldı — her biri kendi başına çalışan yazılım üretir:

- **Plan 2 — kurulum yaşam döngüsü:** panelden kimlik girişi, `/iys/search` doğrulama kapısı, günlük yeniden doğrulama, kill switch, `verified`/`failed`/`disabled` durum→yetki tablosu (spec §2).
- **Plan 3 — kredi emekliliği + gönderim hataları:** `LicenseSmsBalance*` silinmesi, WPF uyumu, `NetgsmSmsException(code)`, `skipped` durumu, bakiye bitince `paused`, `ITenantSmsSender` (spec §1.1, §1.2, §1.4, §3).
- **Plan 4 — onay toplama incelikleri + ayrılış:** shopper çok-marka onay verme yolu (§5.2b), yayıncı ayrılışı/aynalama/30 gün silme (§6).

Bu planda `NetgsmAccount` satırları **elle** (admin SQL) oluşturulur; panel ekranı Plan 2'de gelir.

---

## Dosya yapısı

**Faz 1 — `NetgsmAccount` veri modeli**
- Oluştur: `OrderDeck.LicenseServer/Domain/NetgsmAccount.cs`
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (DbSet + config)
- Oluştur: `OrderDeck.LicenseServer/Data/Migrations/*_AddNetgsmAccount.cs` (EF üretir)
- Oluştur: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs`
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (DI)

**Faz 2 — olay şeması kiracı taşır**
- Değiştir: `OrderDeck.LicenseServer/Domain/IysConsentEvent.cs`
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Oluştur: `OrderDeck.LicenseServer/Data/Migrations/*_AddIysConsentEventTenant.cs`

**Faz 3 — toplayıcı markayı lisanstan çözer**
- Değiştir: `OrderDeck.LicenseServer/Services/Iys/IysConsentCollector.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs`
- Değiştir: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs`
- Değiştir: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs`

**Faz 4 — İYS istemcisi hesap bağlamı alır**
- Değiştir: `OrderDeck.LicenseServer/Services/Iys/IIysClient.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/Iys/NetgsmIysClient.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/Iys/NullIysClient.cs`

**Faz 5 — marka başına işler**
- Değiştir: `OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs`

**Faz 6 — gönderim kapısı kampanyanın markasını okur**
- Değiştir: `OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/Sms/NetgsmOptions.cs` (`BrandCode` SİLİNİR)

**Testler**
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountUniqueIndexTests.cs` *(Testcontainers)*
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountServiceTests.cs`
- Oluştur: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentTenantIsolationTests.cs` *(Testcontainers)*
- Oluştur: `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperMeConsentRevokeTests.cs`
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs`
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs`
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs`
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Iys/NetgsmIysClientTests.cs`
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignIysGateTests.cs`
- Değiştir: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/SmsCampaignTests.cs`

**İsim sözleşmesi (fazlar arası tutarlılık için — değiştirme):**
`NetgsmAccount` · `NetgsmAccountService.GetVerifiedByLicenseAsync` / `.ListVerifiedAsync` / `.GetBrandCodeAsync` / `.ProtectPassword` / `.TryUnprotectPassword` · `IysAccountContext(LicenseId, UserCode, Password, BrandCode)` · `IIysClient.AddAsync(account, items, ct)` / `.SearchAsync(account, recipients, ct)` · `IysConsentCollector.RecordAsync(licenseId, rawPhone, …)` · olay hata kodu `"no-brand"`

**Test verisi sözleşmesi:** marka kodu A = `"731734"`, marka kodu B = `"763208"` (gerçek ölçüm 2026-09-17'de bu iki markayla yapıldı). Netgsm şifresi testlerde **asla sabit yazılmaz** — `$"pw-{Guid.NewGuid():N}"` üretilir (repo public, tarayıcı fixture ile gerçeği ayırt edemez).

---

## Faz 1 — `NetgsmAccount` veri modeli

### Task 1: `NetgsmAccount` tablosu

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/NetgsmAccount.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs:52` (DbSet) ve `:809` sonrası (config)
- Create: `OrderDeck.LicenseServer/Data/Migrations/*_AddNetgsmAccount.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountUniqueIndexTests.cs`

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountUniqueIndexTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// Tekil index yalnız GERÇEK SQL Server'da kanıtlanabilir — InMemory
/// sağlayıcısı index ihlali fırlatmaz (bkz. IysConsentUniqueIndexTests).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class NetgsmAccountUniqueIndexTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public NetgsmAccountUniqueIndexTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static NetgsmAccount Row(Guid licenseId, string brandCode) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        UserCode = "8503021111",
        PasswordProtected = $"pw-{Guid.NewGuid():N}",
        Header = "ORDERDECK",
        BrandCode = brandCode,
        Status = "verified",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Boş bir yayıncı lisansı açar. Ortak bir <c>TestData</c> yardımcısı
    /// bu repoda YOK — mevcut testler (ör. <c>BroadcastPostCleanupJobTests</c>)
    /// aynı gövdeyi kendi içlerinde tutuyor; kalıbı bozmuyoruz.</summary>
    private async Task<Guid> NewLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Netgsm-IX",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    [Fact]
    public async Task Ayni_lisansa_ikinci_hesap_reddedilir()
    {
        var licenseId = await NewLicenseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(licenseId, "731734"));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(licenseId, "763208"));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "bir lisansın tek Netgsm hesabı olur");
        }
    }

    [Fact]
    public async Task Ayni_marka_kodu_iki_lisansa_verilemez()
    {
        var a = await NewLicenseAsync();
        var b = await NewLicenseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(a, "731734"));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(b, "731734"));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "marka→hesap araması tek satır dönmeli; iki lisans aynı markayı " +
                "paylaşırsa push işi hangi kimlikle gideceğini bilemez");
        }
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Docker açık olmalı (yerelde `DOCKER_HOST` gerekebilir — bkz. hafıza notu).

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter NetgsmAccountUniqueIndexTests`
Beklenen: DERLEME HATASI — `NetgsmAccount` ve `db.NetgsmAccounts` yok.

- [ ] **Step 3: Entity'yi yaz**

`OrderDeck.LicenseServer/Domain/NetgsmAccount.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir yayıncının (lisansın) kendi Netgsm aboneliği: abone no, API şifresi,
/// onaylı gönderici başlığı ve İYS marka kodu.
///
/// <para><b>Neden kiracı başına:</b> İYS onayı MARKA başına tutulur. 2026-09-17'de
/// ölçüldü — aynı numara marka <c>731734</c> altında ONAY, <c>763208</c> altında
/// RET. Merkezî tek marka altında tüm yayıncıların listelerini toplamak, onayı
/// hukuken sahibi olmayan tarafa yazmak olurdu.</para>
///
/// <para><b>Satırın yokluğu "hiç girilmemiş" demektir</b> — ayrı bir
/// <c>pending</c> durumu yok. Yayıncı ayrılırsa satır silinmez,
/// <see cref="Status"/> <c>disabled</c> olur.</para>
/// </summary>
public sealed class NetgsmAccount
{
    public Guid Id { get; set; }

    /// <summary>Kiracı anahtarı. Bir lisansın tek hesabı olur (tekil index).</summary>
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Netgsm abone numarası. Gizli değil — panelde açık gösterilir.</summary>
    public string UserCode { get; set; } = "";

    /// <summary>Netgsm API şifresi — <c>IDataProtector</c> ile şifreli saklanır,
    /// asla düz metin dönmez ve asla log'lanmaz.</summary>
    public string PasswordProtected { get; set; } = "";

    /// <summary>Netgsm'de onaylı gönderici başlığı (sender ID).</summary>
    public string Header { get; set; } = "";

    /// <summary>İYS marka kodu. <b>Global tekil</b>: push/verify işleri markadan
    /// hesaba geri dönüyor; iki lisans aynı markayı paylaşırsa hangi kimlikle
    /// gidileceği belirsizleşir.</summary>
    public string BrandCode { get; set; } = "";

    /// <summary>"verified" | "failed" | "disabled". Yalnız <c>verified</c> hesap
    /// onay toplar ve SMS gönderir (fail-closed).</summary>
    public string Status { get; set; } = "failed";

    /// <summary>Panelde gösterilen son hata metni (ör. yanlış şifre).</summary>
    public string? LastError { get; set; }

    /// <summary>Son başarılı <c>/iys/search</c> doğrulaması.</summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 4: DbSet ve config ekle**

`LicenseDbContext.cs:52` civarındaki DbSet bloğuna, `WhatsAppAccounts` satırının yanına:

```csharp
    public DbSet<NetgsmAccount> NetgsmAccounts => Set<NetgsmAccount>();
```

`OnModelCreating` içinde, `mb.Entity<WhatsAppAccount>(...)` bloğunun (`:793-809`) hemen ardına:

```csharp
        mb.Entity<NetgsmAccount>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasOne(a => a.License).WithMany().HasForeignKey(a => a.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(a => a.UserCode).HasMaxLength(32).IsRequired();
            b.Property(a => a.PasswordProtected).HasMaxLength(4000).IsRequired();
            b.Property(a => a.Header).HasMaxLength(32).IsRequired();
            // IysConsent.BrandCode ile AYNI uzunluk — ikisi eşleştiriliyor.
            b.Property(a => a.BrandCode).HasMaxLength(16).IsRequired();
            b.Property(a => a.Status).HasMaxLength(16).IsRequired();
            b.Property(a => a.LastError).HasMaxLength(500);
            // Bir lisans = bir hesap.
            b.HasIndex(a => a.LicenseId).IsUnique();
            // Marka → hesap araması tek satır dönmeli (push/verify işleri).
            b.HasIndex(a => a.BrandCode).IsUnique();
        });
```

- [ ] **Step 5: Göçü üret**

```bash
dotnet ef migrations add AddNetgsmAccount \
  --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj \
  --output-dir Data/Migrations
```

Üretilen dosyayı aç ve iki `CreateIndex` çağrısının da `unique: true` taşıdığını gözle doğrula.

- [ ] **Step 6: Testi çalıştır, geçtiğini gör**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter NetgsmAccountUniqueIndexTests`
Beklenen: 2 test PASS.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/NetgsmAccount.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountUniqueIndexTests.cs
git commit -m "feat(netgsm): yayıncı başına Netgsm hesabı tablosu"
```

### Task 2: `NetgsmAccountService` — şifre koruma ve hesap çözümleme

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs:195` civarı (DI kaydı)
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountServiceTests.cs`

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountServiceTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public class NetgsmAccountServiceTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"netgsm-{Guid.NewGuid():N}").Options);

    private static NetgsmAccountService Service(LicenseDbContext db)
        => new(db, new EphemeralDataProtectionProvider());

    private static NetgsmAccount Seed(
        LicenseDbContext db, Guid licenseId, string brandCode, string status)
    {
        var row = new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = "8503021111",
            PasswordProtected = "not-a-real-ciphertext",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.NetgsmAccounts.Add(row);
        db.SaveChanges();
        return row;
    }

    [Fact]
    public void Sifre_gidis_donus_ayni_metni_verir()
    {
        using var db = NewDb();
        var svc = Service(db);
        var raw = $"pw-{Guid.NewGuid():N}";

        var protectedPw = svc.ProtectPassword(raw);

        protectedPw.Should().NotBe(raw, "düz metin saklanmamalı");
        svc.TryUnprotectPassword(protectedPw).Should().Be(raw);
    }

    [Fact]
    public void Bozuk_sifreli_metin_null_doner()
    {
        using var db = NewDb();
        Service(db).TryUnprotectPassword("bu-gecerli-bir-payload-degil")
            .Should().BeNull("anahtar döndüyse çağıran hesabı disabled yapmalı, patlamamalı");
    }

    [Fact]
    public async Task Dogrulanmamis_hesabin_markasi_cozulmez()
    {
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        Seed(db, licenseId, "731734", status: "failed");

        var brand = await Service(db).GetBrandCodeAsync(licenseId, default);

        brand.Should().BeNull("fail-closed: doğrulanmamış hesap onay toplayamaz");
    }

    [Fact]
    public async Task Dogrulanmis_hesabin_markasi_cozulur()
    {
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        Seed(db, licenseId, "731734", status: "verified");

        (await Service(db).GetBrandCodeAsync(licenseId, default)).Should().Be("731734");
    }

    [Fact]
    public async Task Hesabi_olmayan_lisansin_markasi_null()
    {
        using var db = NewDb();
        (await Service(db).GetBrandCodeAsync(Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task ListVerifiedAsync_yalniz_dogrulanmis_hesaplari_doner()
    {
        using var db = NewDb();
        Seed(db, Guid.NewGuid(), "731734", "verified");
        Seed(db, Guid.NewGuid(), "763208", "disabled");

        var rows = await Service(db).ListVerifiedAsync(default);

        rows.Select(r => r.BrandCode).Should().Equal("731734");
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter NetgsmAccountServiceTests`
Beklenen: DERLEME HATASI — `NetgsmAccountService` yok.

- [ ] **Step 3: Servisi yaz**

`OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Lisans → Netgsm hesabı çözümlemesi ve API şifresinin şifrelenmesi.
///
/// <para><b>Anahtar kaybı:</b> <c>IDataProtection</c> anahtarları prod'da
/// <c>.\keys:/app/keys</c> volume'unda kalıcıdır (bkz. WhatsAppAccountService).
/// Klasör kaybolursa şifreler çözülemez ve yayıncıların kimliklerini yeniden
/// girmesi gerekir — bu klasör yedek kapsamında olmalı.</para>
///
/// <para><b>Fail-closed:</b> yalnız <c>Status == "verified"</c> hesap iş yapar.
/// Doğrulanmamış hesap için marka çözülmez → onay satırı açılmaz (spec §5.1).</para>
/// </summary>
public sealed class NetgsmAccountService
{
    /// <summary>Şifreleme amacı — değiştirilirse mevcut şifreler çözülemez.</summary>
    private const string PasswordProtectorPurpose = "OrderDeck.Netgsm.Password.v1";

    private readonly LicenseDbContext _db;
    private readonly IDataProtector _protector;

    public NetgsmAccountService(LicenseDbContext db, IDataProtectionProvider protection)
    {
        _db = db;
        _protector = protection.CreateProtector(PasswordProtectorPurpose);
    }

    public string ProtectPassword(string rawPassword) => _protector.Protect(rawPassword);

    /// <summary>Şifreli metni çözer. Anahtar döndüyse/bozuksa <c>null</c> döner —
    /// çağıran hesabı <c>disabled</c> işaretler, sessizce göndermez.</summary>
    public string? TryUnprotectPassword(string protectedPassword)
    {
        try { return _protector.Unprotect(protectedPassword); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public Task<NetgsmAccount?> GetVerifiedByLicenseAsync(Guid licenseId, CancellationToken ct)
        => _db.NetgsmAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId && a.Status == "verified", ct);

    public async Task<IReadOnlyList<NetgsmAccount>> ListVerifiedAsync(CancellationToken ct)
        => await _db.NetgsmAccounts
            .Where(a => a.Status == "verified")
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Onay toplama yolunun ihtiyacı: yalnız marka kodu, şifre değil.</summary>
    public async Task<string?> GetBrandCodeAsync(Guid licenseId, CancellationToken ct)
        => await _db.NetgsmAccounts
            .Where(a => a.LicenseId == licenseId && a.Status == "verified")
            .Select(a => a.BrandCode)
            .FirstOrDefaultAsync(ct);
}
```

- [ ] **Step 4: DI kaydı ekle**

`Program.cs`, `builder.Services.AddScoped<...IysConsentCollector>();` satırının (`:195`) HEMEN ÜSTÜNE:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountService>();
```

- [ ] **Step 5: Testi çalıştır, geçtiğini gör**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter NetgsmAccountServiceTests`
Beklenen: 6 test PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountServiceTests.cs
git commit -m "feat(netgsm): hesap çözümleme servisi + IDataProtector şifre saklama"
```

---

## Faz 2 — Olay şeması kiracı taşır

Spec §5.1b: `RecordAsync`'e `licenseId` eklemek yetmiyor, **kanıtın kendisi kiracısız**. Bugün aynı telefonun A markasındaki ONAY'ı ile B markasındaki RET'i aynı numaraya asılı iki olay olarak duruyor; denetimde ayrıştırılamıyor. Bu tablonun tek varlık sebebi ispat olduğu için bu sıradan bir eksik değil.

### Task 3: `IysConsentEvent` üç kiracı sütunu kazanır

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/IysConsentEvent.cs:44-47`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs:541-554`
- Create: `OrderDeck.LicenseServer/Data/Migrations/*_AddIysConsentEventTenant.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs`

- [ ] **Step 1: Düşen testi yaz**

`IysConsentCollectorTests.cs` içine, dosyanın sonundaki son `[Fact]`'ten sonra ekle:

```csharp
    [Fact]
    public async Task Olay_kiraci_sutunlarini_tasir()
    {
        using var db = NewDb();
        await RecordAsync(Collector(db), true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var ev = await db.IysConsentEvents.SingleAsync();
        var row = await db.IysConsents.SingleAsync();

        ev.BrandCode.Should().Be("731734");
        ev.LicenseId.Should().NotBeNull("olay hangi yayıncıya ait olduğunu taşımalı");
        ev.IysConsentId.Should().Be(row.Id, "olay durum satırına bağlanabilmeli");
    }
```

Bu test Task 4 bitene kadar **tam geçmez** (collector henüz `licenseId` almıyor); şimdilik yalnız derlemeyi ve sütunların varlığını sürüyor.

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter IysConsentCollectorTests`
Beklenen: DERLEME HATASI — `IysConsentEvent` üzerinde `BrandCode` / `LicenseId` / `IysConsentId` yok.

- [ ] **Step 3: Entity'ye sütunları ekle**

`IysConsentEvent.cs`, `public string Recipient` özelliğinin HEMEN ÜSTÜNE:

```csharp
    /// <summary>Olayın ait olduğu yayıncı. Kanıtın kiracısı — <see cref="Recipient"/>
    /// tek başına yetmez: aynı telefon A markasında ONAY, B markasında RET olabilir
    /// (2026-09-17'de ölçüldü) ve denetimde ikisi ayrıştırılabilmeli.
    /// Marka çözülemeyen olaylarda (<c>ErrorCode="no-brand"</c>) null.</summary>
    public Guid? LicenseId { get; set; }

    /// <summary>Olayın ait olduğu İYS markası. Marka çözülemediyse null.</summary>
    public string? BrandCode { get; set; }

    /// <summary>Durum satırına bağ. <b>FK DEĞİL</b> — satır silinse bile olay
    /// kalmalı (tablo ekle-only ve hiç silinmez). Markasız olaylarda null.</summary>
    public Guid? IysConsentId { get; set; }
```

- [ ] **Step 4: DbContext config'ini güncelle**

`LicenseDbContext.cs`, `mb.Entity<IysConsentEvent>(b => { ... })` bloğunda `b.HasIndex(e => new { e.Recipient, e.OccurredAt });` satırının ÜSTÜNE:

```csharp
            b.Property(e => e.BrandCode).HasMaxLength(16);
```

ve aynı bloğun sonuna, mevcut index'ten SONRA:

```csharp
            // Denetim sorgusu "şu yayıncının şu numaraya ait olayları" —
            // kiracı ayrıştırması bu index olmadan tablo taraması olur.
            b.HasIndex(e => new { e.LicenseId, e.Recipient, e.OccurredAt });
```

- [ ] **Step 5: Göçü üret**

```bash
dotnet ef migrations add AddIysConsentEventTenant \
  --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj \
  --output-dir Data/Migrations
```

Üretilen dosyada üç `AddColumn` (`LicenseId` uniqueidentifier null, `BrandCode` nvarchar(16) null, `IysConsentId` uniqueidentifier null) ve bir `CreateIndex` olmalı. **`nullable: true` olduklarını doğrula** — mevcut satırlar geriye dönük doldurulamaz, prod'daki eski olaylar `null` kalacak ve bu kabul edilmiş durum.

- [ ] **Step 6: Derlemeyi doğrula**

Çalıştır: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Beklenen: başarılı. (Step 1'deki test hâlâ düşüyor — `LicenseId` null geliyor; Task 4 dolduracak.)

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/IysConsentEvent.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations \
        OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs
git commit -m "feat(iys): onay olayları kiracı sütunları kazanır (LicenseId/BrandCode/IysConsentId)"
```

---

## Faz 3 — Toplayıcı markayı lisanstan çözer

Spec §5.1: `IysConsent` tekil index'i `(BrandCode, ChannelType, RecipientType, Recipient)`. `BrandCode=""` yazılırsa **kurulumu bitmemiş tüm yayıncıların aynı numaraya ait onayı tek satıra çakışır** — B'nin RET'i A'nın ONAY'ını sessizce ezer, hata çıkmaz.

### Task 4: `RecordAsync` `licenseId` alır, markasız satır AÇMAZ

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Iys/IysConsentCollector.cs:41-155`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs`

- [ ] **Step 1: Test kurulumunu yeni imzaya taşı ve düşen testleri yaz**

`IysConsentCollectorTests.cs` başındaki yardımcıları TAMAMEN şununla değiştir (mevcut `NewDb` / `Collector` / `RecordAsync` gövdeleri):

```csharp
    private const string Phone = "+905551112233";
    private const string BrandA = "731734";
    private static readonly Guid LicenseA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-{Guid.NewGuid():N}").Options);

    /// <summary>Doğrulanmış bir Netgsm hesabı tohumlar — markanın kaynağı artık bu.</summary>
    private static void SeedAccount(LicenseDbContext db, Guid licenseId, string brandCode)
    {
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = "8503021111",
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = "verified",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static IysConsentCollector Collector(LicenseDbContext db)
        => new(db,
            new NetgsmAccountService(db, new EphemeralDataProtectionProvider()),
            Options.Create(new NetgsmOptions()),
            NullLogger<IysConsentCollector>.Instance);

    private static Task RecordAsync(
        IysConsentCollector c, bool consented, DateTimeOffset at,
        string phone = Phone, Guid? licenseId = null)
        => c.RecordAsync(licenseId ?? LicenseA, phone, consented, at, "Shopper", Guid.NewGuid(),
            ip: "203.0.113.7", userAgent: "test-agent");
```

Gerekli ek `using`: `Microsoft.AspNetCore.DataProtection;`

Mevcut her `[Fact]`'in içinde, `using var db = NewDb();` satırının HEMEN ARDINA şunu ekle:

```csharp
        SeedAccount(db, LicenseA, BrandA);
```

Ardından dosyanın sonuna iki yeni test ekle:

```csharp
    [Fact]
    public async Task Hesabi_olmayan_yayincida_satir_ACILMAZ()
    {
        using var db = NewDb();   // SeedAccount YOK — kurulumu bitmemiş yayıncı

        await RecordAsync(Collector(db), consented: true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        db.IysConsents.Should().BeEmpty(
            "markasız satır, kurulumu bitmemiş TÜM yayıncıların onayını tek satırda " +
            "çakıştırır — B'nin RET'i A'nın ONAY'ını sessizce ezerdi");

        var ev = await db.IysConsentEvents.SingleAsync();
        ev.ErrorCode.Should().Be("no-brand");
        ev.BrandCode.Should().BeNull();
        ev.LicenseId.Should().Be(LicenseA, "olay yine de kime ait olduğunu taşımalı");
        ev.ProofIp.Should().Be("203.0.113.7", "ispat kaybolmamalı");
    }

    [Fact]
    public async Task Dogrulanmamis_hesap_da_satir_ACMAZ()
    {
        using var db = NewDb();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = LicenseA,
            UserCode = "8503021111",
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = BrandA,
            Status = "failed",          // doğrulama düşmüş
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await RecordAsync(Collector(db), consented: true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        db.IysConsents.Should().BeEmpty("fail-closed: onay toplama verified'a bağlı");
        (await db.IysConsentEvents.SingleAsync()).ErrorCode.Should().Be("no-brand");
    }
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter IysConsentCollectorTests`
Beklenen: DERLEME HATASI — `RecordAsync` `licenseId` parametresi almıyor, ctor `NetgsmAccountService` almıyor.

- [ ] **Step 3: Collector'ın alanlarını ve imzasını değiştir**

`IysConsentCollector.cs:46-56` (alanlar + ctor) yerine:

```csharp
    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentCollector> _log;

    public IysConsentCollector(
        LicenseDbContext db, NetgsmAccountService accounts,
        IOptions<NetgsmOptions> opt, ILogger<IysConsentCollector> log)
    {
        _db = db;
        _accounts = accounts;
        _opt = opt.Value;
        _log = log;
    }
```

`using OrderDeck.LicenseServer.Services.Sms;` dosyada zaten var.

Metot imzası (`:58-61`) yerine:

```csharp
    /// <param name="licenseId">Onayın ait olduğu yayıncı. Marka BUNDAN çözülür;
    /// çözülemezse satır açılmaz (spec §5.1).</param>
    public async Task RecordAsync(
        Guid licenseId, string? rawPhone, bool consented, DateTimeOffset occurredAt,
        string sourceTable, Guid sourceId,
        string? ip, string? userAgent, CancellationToken ct = default)
```

Geçersiz telefon dalındaki olay nesnesinde (`:76-88`) `Recipient = ...` satırının ÜSTÜNE:

```csharp
                LicenseId = licenseId,
```

- [ ] **Step 4: Markayı çöz, markasız dalı ekle**

`:95` ile başlayan blok (`_db.IysConsentEvents.Add(new IysConsentEvent {...})`) ile `:135`'teki kural-1 `return;`'ü arasındaki HER ŞEYİ şununla değiştir:

```csharp
        // Marka artık global ayardan değil yayıncının hesabından geliyor.
        // Çözülemezse satır AÇMIYORUZ: BrandCode="" yazmak, kurulumu bitmemiş
        // tüm yayıncıların aynı numaraya ait onayını tekil index yüzünden TEK
        // satıra çakıştırır ve B'nin RET'i A'nın ONAY'ını sessizce ezer.
        var brandCode = await _accounts.GetBrandCodeAsync(licenseId, ct);

        var ev = new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            BrandCode = brandCode,
            Recipient = phone,
            OccurredAt = occurredAt,
            EventType = consented ? IysConsentEventType.LocalConsent : IysConsentEventType.LocalRevoke,
            Status = status,
            SourceTable = sourceTable,
            SourceId = sourceId,
            ProofIp = ip,
            ProofUserAgent = Truncate(userAgent, 512),
        };
        _db.IysConsentEvents.Add(ev);

        if (brandCode is null)
        {
            // Bozuk telefon dalının aynısı: kayıt satırı yok, ispat olayı var.
            // Yayıncı kurulumunu bitirince bu olaylar admin sayfasında görünür.
            ev.ErrorCode = "no-brand";
            _log.LogWarning(
                "İYS: lisans {LicenseId} için doğrulanmış marka yok, kayıt açılmadı (kaynak={Source})",
                licenseId, sourceTable);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var row = await _db.IysConsents.FirstOrDefaultAsync(
            c => c.BrandCode == brandCode
                 && c.ChannelType == "MESAJ"
                 && c.RecipientType == "BIREYSEL"
                 && c.Recipient == phone, ct);

        if (row is null)
        {
            row = new IysConsent
            {
                Id = Guid.NewGuid(),
                BrandCode = brandCode,
                ChannelType = "MESAJ",
                RecipientType = "BIREYSEL",
                Recipient = phone,
                CreatedAt = now,
            };
            _db.IysConsents.Add(row);
        }

        // Olay hangi satıra ait — denetimde ONAY/RET karışmasın diye.
        ev.IysConsentId = row.Id;

        if (row.LastLocalEventAt != default && occurredAt <= row.LastLocalEventAt)
        {
            // Kural 1: RET kendiliğinden ONAY'a yükselmez. Durum yalnızca
            // LastLocalEventAt'ten DAHA YENİ bir olayla değişir; geç işlenen
            // eski bir onay reddi ezemez. Olay yine de yazıldı (yukarıda).
            return;
        }
```

`row.Status = status;` ile başlayan geri kalan blok **olduğu gibi kalır** — `row.SourceCode = _opt.IysSourceCode;` dahil. Kaynak kodu (`HS_WEB`) markaya değil kanala ait, global kalıyor.

> **Neden `else if` → ayrı `if`:** eski kod kural-1 kontrolünü `row is null` ile `else if` zincirine bağlamıştı. `ev.IysConsentId` ataması araya girince zincir bozuluyor. `row.LastLocalEventAt != default` koruması bu yüzden eklendi: yeni satırda `LastLocalEventAt` varsayılan değerde olur ve kıyas yanlışlıkla `return` etmemeli.

- [ ] **Step 5: Testi çalıştır, geçtiğini gör**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter IysConsentCollectorTests`
Beklenen: TÜM testler PASS — Task 3'teki `Olay_kiraci_sutunlarini_tasir` dahil.

(`Program.cs` DI'ında değişiklik gerekmez — `NetgsmAccountService` Task 2'de kaydedildi ve ikisi de `Scoped`.)

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysConsentCollector.cs \
        OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentCollectorTests.cs
git commit -m "feat(iys): toplayıcı markayı lisanstan çözer, markasız satır açmaz"
```

### Task 5: Üç çağrı yeri lisansı geçirir

Üçünün de lisansı farklı yoldan bulması gerekiyor ve **`ShopperMeController` aynı zamanda bir davranış hatası taşıyor** (spec §5.2): profil kutusu tek boolean, ama shopper birden fazla yayıncıya bağlı olabiliyor.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs:154-160`
- Modify: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs:182-189`
- Modify: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs:133-157`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentTenantIsolationTests.cs` (yeni)
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperMeConsentRevokeTests.cs` (yeni)

- [ ] **Step 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentTenantIsolationTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// Kiracı izolasyonunun tekil-index tarafı GERÇEK SQL Server ister: InMemory
/// unique index uygulamıyor, iki markanın aynı numarası orada zaten çakışmaz
/// ve test yanlış yere yeşil yanar.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IysConsentTenantIsolationTests : IAsyncLifetime
{
    private const string Phone = "+905551112233";
    private const string BrandA = "731734";
    private const string BrandB = "763208";

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public IysConsentTenantIsolationTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> SeedBroadcasterAsync(string brandCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Kiracı-" + brandCode,
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = "8503021111",
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = "verified",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return licenseId;
    }

    private async Task RecordAsync(Guid licenseId, bool consented, DateTimeOffset at)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var collector = new IysConsentCollector(
            db, new NetgsmAccountService(db, new EphemeralDataProtectionProvider()),
            Options.Create(new NetgsmOptions()),
            NullLogger<IysConsentCollector>.Instance);

        await collector.RecordAsync(
            licenseId, Phone, consented, at, "Shopper", Guid.NewGuid(),
            ip: "203.0.113.7", userAgent: "test-agent");
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Iki_yayinci_ayni_telefon_iki_ayri_satir()
    {
        var a = await SeedBroadcasterAsync(BrandA);
        var b = await SeedBroadcasterAsync(BrandB);
        var at = DateTimeOffset.UtcNow;

        await RecordAsync(a, consented: true, at);
        await RecordAsync(b, consented: false, at);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.IysConsents.Where(c => c.Recipient == Phone).ToListAsync();

        rows.Should().HaveCount(2, "onay MARKA başına tutulur; tek satır B'nin RET'ini " +
            "A'nın ONAY'ının üzerine yazardı");
        rows.Single(r => r.BrandCode == BrandA).Status.Should().Be(IysConsentStatus.Onay);
        rows.Single(r => r.BrandCode == BrandB).Status.Should().Be(IysConsentStatus.Ret);
    }

    [Fact]
    public async Task Olaylar_yayinciya_gore_ayristirilabilir()
    {
        var a = await SeedBroadcasterAsync(BrandA);
        var b = await SeedBroadcasterAsync(BrandB);
        var at = DateTimeOffset.UtcNow;

        await RecordAsync(a, consented: true, at);
        await RecordAsync(b, consented: false, at);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var events = await db.IysConsentEvents
            .Where(e => e.Recipient == Phone).ToListAsync();

        events.Should().HaveCount(2);
        events.Single(e => e.LicenseId == a).Status.Should().Be(IysConsentStatus.Onay);
        events.Single(e => e.LicenseId == b).Status.Should().Be(IysConsentStatus.Ret);
        events.Single(e => e.LicenseId == a).BrandCode.Should().Be(BrandA);
    }
}
```

- [ ] **Step 2: Testi çalıştır, düştüğünü gör**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter IysConsentTenantIsolationTests`
Beklenen: 2 test PASS **olabilir** — collector Task 4'te düzeldi. Geçiyorsa bu bir regresyon kilidi; bu adımda beklenen sonucu not et ve devam et. Geçmiyorsa `RelationalApiFactory` içinde `NetgsmAccountService` kaydının eksik olması muhtemel — `Program.cs` DI'ını kontrol et.

- [ ] **Step 3: `IntakeFormService` lisansı çözsün**

`IntakeFormService.cs`, `if (smsConsent) { ... }` bloğunu (`:154-160`) şununla değiştir:

```csharp
        if (smsConsent)
        {
            // Form müşteriye (CustomerId) bağlı, İYS markası lisansa. Aradaki
            // eşlemeyi panel tarafındaki mevcut çözümleyici yapıyor; müşterinin
            // aktif lisansı yoksa marka da yoktur ve collector "no-brand"
            // olayı yazıp satır açmaz — bu yol bilinçli olarak fail-closed.
            var licenseId = await Controllers.Panel.PanelLicenseScope.ResolveAsync(
                _db, config.CustomerId, ct);

            await _iys.RecordAsync(
                licenseId ?? Guid.Empty, phone, consented: true, occurredAt: sub.SubmittedAt,
                sourceTable: "IntakeFormSubmission", sourceId: sub.Id,
                ip: ipAddress, userAgent: userAgent, ct: ct);
        }
```

Bu metot `config` nesnesine sahip değil — yalnız `configId` alıyor. Metodun başına, `var sub = new IntakeFormSubmission` satırının ÜSTÜNE ekle:

```csharp
        var config = await _db.IntakeFormConfigs
            .FirstOrDefaultAsync(c => c.Id == configId, ct)
            ?? throw new InvalidOperationException($"Intake form config {configId} bulunamadı");
```

> **Neden `Guid.Empty` fallback:** lisans çözülemediğinde çağrıyı ATLAMAK yanlış olurdu — o zaman ispat olayı da yazılmaz ve onay sessizce kaybolur; 284 onayı tam böyle kaybettik. `Guid.Empty` ile çağırmak collector'ı `no-brand` yoluna sokar: satır açılmaz ama ispat (IP, UA, zaman) tabloya düşer.

- [ ] **Step 4: `ShopperAuthController` lisansı geçirsin**

`ShopperAuthController.cs:184-188` çağrısında ilk argüman olarak `license.Id` ekle:

```csharp
                await _iys.RecordAsync(
                    license.Id, shopper.Phone, consented: true, occurredAt: now,
                    sourceTable: "Shopper", sourceId: shopper.Id,
                    ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    userAgent: Request.Headers.UserAgent.ToString(), ct: ct);
```

`license` değişkeni `:131`'de zaten çözülmüş durumda.

- [ ] **Step 5: Geri çekmenin TÜM markalara gittiğini kanıtlayan düşen testi yaz**

Spec §5.2 iki ayrı şey söylüyor ve ikisi de test edilmeden geçilemez: geri çekme
**bağlı tüm markalara** gider (sözleşme 8) ve bunu boolean'ın **değişmesine**
bağlamaz (sözleşme 15). İkincisi mevcut kodun sessiz kaçağı: kişi bir kez geri
çekip sonra B'nin formundan onay verirse `Shopper.SmsConsent` `false` kalır,
profilden gelen ikinci geri çekme "değer zaten false" diye hiçbir RET üretmez.

`OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperMeConsentRevokeTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// Spec §5.2 — profil kutusu tek boolean ama İYS onayı marka başına. Geri çekme
/// bağlı TÜM markalara RET yazar; onay verme profilden HİÇ yazılmaz.
///
/// InMemory burada yeterli: iki satırın markası farklı olduğu için
/// `(BrandCode, ChannelType, RecipientType, Recipient)` tekil indeksi zaten
/// devreye girmiyor — kanıtlanan şey indeks değil, kaç satır yazıldığı.
/// </summary>
public sealed class ShopperMeConsentRevokeTests : IClassFixture<ApiFactory>
{
    private const string BrandA = "731734";
    private const string BrandB = "763208";

    private readonly ApiFactory _factory;
    public ShopperMeConsentRevokeTests(ApiFactory factory) => _factory = factory;

    private sealed record RegisterRequest(
        string BroadcasterCode, string FullName, string Phone, string Password,
        string Address, string Platform, string Username,
        string? Email = null, string? Tc = null, bool SmsConsent = false);

    private sealed record AuthResponse(
        string AccessToken, DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken, DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId, object[] Broadcasters);

    private sealed record PatchMeRequest(bool? SmsConsent = null);

    private static string UniquePhone()
        => "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private static string UniqueCode()
        => ("revoke" + Guid.NewGuid().ToString("N"))[..16];

    /// <summary>Doğrulanmış Netgsm hesabı olan bir yayıncı açar ve
    /// (lisans kimliği, shopper kodu) döner.</summary>
    private async Task<(Guid LicenseId, string ShopperCode)> SeedBroadcasterAsync(string brandCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Yayıncı-" + brandCode,
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = UniqueCode();
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });

        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = "8503021111",
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = "verified",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return (licenseId, code);
    }

    private static async Task LinkAsync(LicenseDbContext db, Guid shopperId, Guid licenseId)
    {
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "revokeuser",
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Geri_cekme_bagli_TUM_markalara_RET_yazar()
    {
        var (licenseA, codeA) = await SeedBroadcasterAsync(BrandA);
        var (licenseB, _) = await SeedBroadcasterAsync(BrandB);

        var client = _factory.CreateClient();
        var phone = UniquePhone();

        // A'ya onay VEREREK kaydol: shopper.SmsConsent = true.
        var reg = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(codeA, "Revoke User", phone, "Pass1234!",
                "Ankara", "youtube", "revokeuser", SmsConsent: true));
        reg.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        // İkinci yayıncıya da bağlı.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            await LinkAsync(db, auth.ShopperId, licenseB);
        }

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(SmsConsent: false));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = _factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await checkDb.IysConsents
            .Where(c => c.Recipient == phone)
            .ToListAsync();

        rows.Should().HaveCount(2, "kişi iki yayıncıya bağlı, geri çekme ikisine de gitmeli");
        rows.Should().OnlyContain(c => c.Status == IysConsentStatus.Ret);
        rows.Select(c => c.BrandCode).Should().BeEquivalentTo(new[] { BrandA, BrandB });
    }

    [Fact]
    public async Task Boolean_zaten_false_iken_de_RET_uretilir()
    {
        var (_, codeA) = await SeedBroadcasterAsync(BrandA);

        var client = _factory.CreateClient();
        var phone = UniquePhone();

        // Onay VERMEDEN kaydol → shopper.SmsConsent zaten false.
        var reg = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(codeA, "Revoke User", phone, "Pass1234!",
                "Ankara", "youtube", "revokeuser", SmsConsent: false));
        reg.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(SmsConsent: false));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = _factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await checkDb.IysConsents.SingleOrDefaultAsync(c => c.Recipient == phone);

        row.Should().NotBeNull(
            "kişi başka bir yayıncının formundan onay vermiş olabilir; geri çekme " +
            "boolean'ın DEĞİŞMESİNE bağlanırsa o onay yerinde kalır");
        row!.Status.Should().Be(IysConsentStatus.Ret);
        row.BrandCode.Should().Be(BrandA);
    }

    [Fact]
    public async Task Profilden_onay_ISYS_e_yazilmaz()
    {
        var (_, codeA) = await SeedBroadcasterAsync(BrandA);

        var client = _factory.CreateClient();
        var phone = UniquePhone();

        var reg = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(codeA, "Revoke User", phone, "Pass1234!",
                "Ankara", "youtube", "revokeuser", SmsConsent: false));
        var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var resp = await client.PatchAsJsonAsync("/api/v1/shopper/me",
            new PatchMeRequest(SmsConsent: true));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = _factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await checkDb.IysConsents.AnyAsync(c => c.Recipient == phone))
            .Should().BeFalse("profildeki tek kutu 'hangi yayıncıya izin veriyorum' " +
                              "sorusunu cevaplayamaz; onay yalnız toplama noktasında alınır");

        var shopper = (await checkDb.Shoppers.FindAsync(auth.ShopperId))!;
        shopper.SmsConsent.Should().BeTrue("yerel bayrak yine de açılır");
        shopper.SmsConsentSource.Should().Be("profile");
    }
}
```

> **Neden `Profilden_onay_ISYS_e_yazilmaz` de burada:** Step 6 profilden onay
> yolunu kaldırıyor. O davranış testsiz kalırsa biri ileride "kutu açılınca
> neden İYS'ye gitmiyor" diye bakıp geri ekler — ve merkezî markaya yazmaya
> başlar. Testin adı, kaldırmanın bilinçli olduğunu söylüyor.

- [ ] **Step 6: Testi çalıştır, düştüğünü gör**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperMeConsentRevoke"`

Beklenen: üçü de FAIL.
- `Geri_cekme_bagli_TUM_markalara_RET_yazar` → 1 satır bulur, 2 bekliyordu.
- `Boolean_zaten_false_iken_de_RET_uretilir` → hiç satır yok (mevcut kod değer
  değişmediği için olayı hiç üretmiyor).
- `Profilden_onay_ISYS_e_yazilmaz` → satır var (mevcut kod profilden onay yazıyor).

Üçünün de **bu sebeplerle** düştüğünü doğrula. Farklı bir hata (ör. derleme,
`ShopperCode` çakışması) çıkıyorsa önce onu gider — yanlış sebeple düşen test
yeşile döndüğünde hiçbir şey kanıtlamaz.

- [ ] **Step 7: `ShopperMeController` — geri çekme TÜM markalara, onay HİÇBİRİNE**

`ShopperMeController.cs:133-157` bloğunu şununla değiştir:

```csharp
        // Onay kutusu tek boolean, ama shopper birden fazla yayıncıya bağlı
        // olabiliyor (ShopperBroadcasterLink) ve İYS onayı MARKA başına tutuluyor.
        // Bu yüzden iki yön simetrik DEĞİL:
        //
        //  - GERİ ÇEKME: kişinin açık eylemi → bağlı TÜM markalara RET gider.
        //    Aşırı geniş olması bilinçli; 6563'te fazla susmak hatadır, fazla
        //    susturmak değil.
        //  - ONAY VERME: burada YAPILMAZ. Profildeki tek kutu "hangi yayıncıya
        //    izin veriyorum" sorusunu cevaplayamaz; onay yalnız toplama
        //    noktasında (kayıt formu / kayıt akışı) alınır.
        //
        // Geri çekme boolean'ın DEĞİŞMESİNE de bağlanamaz: kişi profil kutusu
        // kapalıyken B'nin formundan onay vermiş olabilir. O durumda değer
        // zaten false'tur, "değişmedi" denip geçilirse RET hiç üretilmez.
        if (req.SmsConsent is false)
        {
            var revokedAt = DateTimeOffset.UtcNow;
            if (shopper.SmsConsent)
            {
                shopper.SmsConsent = false;
                shopper.SmsConsentRevokedAt = revokedAt;
            }

            var linkedLicenseIds = await _db.ShopperBroadcasterLinks
                .Where(l => l.ShopperId == shopper.Id && l.LeftAt == null)
                .Select(l => l.LicenseId)
                .ToListAsync(ct);

            foreach (var licenseId in linkedLicenseIds)
            {
                await _iys.RecordAsync(
                    licenseId, shopper.Phone, consented: false, occurredAt: revokedAt,
                    sourceTable: "Shopper", sourceId: shopper.Id,
                    ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    userAgent: Request.Headers.UserAgent.ToString(), ct: ct);
            }
        }
        else if (req.SmsConsent is true && !shopper.SmsConsent)
        {
            // Yalnız yerel bayrak ve ispat tarihi. İYS'ye HİÇBİR ONAY gitmez.
            shopper.SmsConsent = true;
            shopper.SmsConsentAt = DateTimeOffset.UtcNow;
            shopper.SmsConsentSource = "profile";
        }
```

- [ ] **Step 8: Derle ve tüm İYS testlerini çalıştır**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Iys|FullyQualifiedName~Shopper|FullyQualifiedName~IntakeForm"`
Beklenen: PASS. Düşen shopper/intake testleri varsa çağrı imzası yüzündendir — lisans argümanını ekleyerek düzelt, davranış beklentisini değiştirme.

Profil kutusunun eski davranışını (açarken `RecordAsync` çağrılıyordu) bekleyen bir test varsa, o test **silinmeli değil güncellenmeli**: yeni beklenti "kutu açılınca İYS'ye hiçbir şey gitmez, yalnız `Shopper.SmsConsent` true olur".

- [ ] **Step 9: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs \
        OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs \
        OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs \
        OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentTenantIsolationTests.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperMeConsentRevokeTests.cs
git commit -m "feat(iys): onay çağrı yerleri lisansı taşır; profilden geri çekme tüm markalara gider"
```

---
## Faz 4 — İYS istemcisi hesap bağlamı alır

### Task 6: `IIysClient` hangi yayıncı adına konuştuğunu parametreden öğrenir

Bugün `NetgsmIysClient.PostAsync` (`:106-115`) her isteğe **global** kimlikleri
koyuyor. Faz 5'te marka döngüsü yazsak bile bu tek başına yetmez: B yayıncısı
için dönülen tur merkezî markaya sorar, gelen cevap B'nin satırına yazılır.
Sessiz veri bozulması — en kötü türü, çünkü hiçbir hata fırlatmaz.

Bu görev, "yanlış markaya sorma" hatasını **derleme zamanında imkânsız** hâle
getiriyor: istemcinin global kimliğe erişimi kalmıyor, çağıran vermek zorunda.

Spec test sözleşmesi **#11** burada kilitleniyor: *"istek markası = yazılan
satırın markası."*

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Iys/IIysClient.cs` (kayıt tipi ekle + iki imza)
- Modify: `OrderDeck.LicenseServer/Services/Iys/NetgsmIysClient.cs:36-38,57-59,103-118`
- Modify: `OrderDeck.LicenseServer/Services/Iys/NullIysClient.cs:18-29`
- Modify: `OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs:51-96,98-109`
- Modify: `OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs:45-64`
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/NetgsmIysClientTests.cs`
- Test (derleme düzeltmesi): `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs:15-31`
- Test (derleme düzeltmesi): `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs:17-34`

> **İki işin sınırı:** bu görev yalnız **imzayı** değiştirir. İşler bu turda
> hesabı hâlâ `_opt`'tan kurar — davranış birebir aynı kalır, testler yeşil
> kalır. Hesabı `NetgsmAccountService`'ten çözmek Faz 5'in işi. Tek commit'te
> hem imza hem davranış değiştirmek, bir regresyon çıktığında hangisinin
> kırdığını belirsiz bırakırdı.

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Iys/NetgsmIysClientTests.cs` içinde
önce yardımcıları değiştir — `Opt()` **`BrandCode`'u koruyor** (silmiyoruz;
testin tüm anlamı "global dolu ama kullanılmıyor" demek):

```csharp
    private static NetgsmOptions Opt() => new()
    {
        UserCode = $"user-{Guid.NewGuid():N}",
        Password = $"pw-{Guid.NewGuid():N}",
        BaseUrl = "https://api.netgsm.com.tr",
        // Bilerek DOLU: aşağıdaki test bu değerin isteğe SIZMADIĞINI kanıtlıyor.
        // Faz 6 Task 9 bu özelliği tamamen siler; o zaman bu satır da gider.
        BrandCode = "731734",
    };

    private static IysAccountContext Account(string brandCode) => new(
        Guid.NewGuid(), $"user-{Guid.NewGuid():N}", $"pw-{Guid.NewGuid():N}", brandCode);
```

Sonra yeni testi ekle:

```csharp
    [Fact]
    public async Task Istek_markasi_parametreden_gelir_global_configten_DEGIL()
    {
        // Çok kiracılılığın kalbi: istemci global kimliğe BAKMAZ. Bakarsa,
        // B yayıncısı için dönülen tur merkezî markaya sorar ve gelen cevap
        // B'nin satırına yazılır — hiçbir hata fırlatmadan veri bozulur.
        var (client, handler) = Build("{\"code\":\"0\"}");
        var account = Account("763208");   // Opt().BrandCode = "731734"

        await client.AddAsync(account, new[] { Rec("+905551112233") });

        using var doc = JsonDocument.Parse(handler.Body!);
        var header = doc.RootElement.GetProperty("header");
        header.GetProperty("brandCode").GetString().Should().Be("763208");
        header.GetProperty("username").GetString().Should().Be(account.UserCode);
        header.GetProperty("password").GetString().Should().Be(account.Password);
    }

    [Fact]
    public async Task SearchAsync_de_hesap_baglamini_kullanir()
    {
        var (client, handler) = Build("{\"code\":\"0\",\"query\":[]}");
        var account = Account("763208");

        await client.SearchAsync(account, new[] { "+905551112233" });

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("header").GetProperty("brandCode")
            .GetString().Should().Be("763208");
    }
```

Mevcut dört testin çağrılarını da hesapla güncelle:

```csharp
        await client.AddAsync(Account("731734"), new[] { Rec("+905551112233") });
```
```csharp
        var result = await client.AddAsync(Account("731734"), new[] { Rec("+905551112233") });
```
```csharp
        var result = await client.SearchAsync(
            Account("731734"), new[] { "+905310826728", "+905000000000" });
```
```csharp
        var result = await client.SearchAsync(Account("731734"), new[] { "+905551112233" });
```
```csharp
        var act = async () => await client.AddAsync(
            Account("731734"), new[] { Rec("+905551112233") });
```

Ayrıca `AddAsync_kimligi_govdede_yollar_ve_uca_gider` içindeki
`header.GetProperty("brandCode").GetString().Should().Be("731734");` satırı
artık hesaptan gelen değeri doğruluyor — testi şu hâle getir:

```csharp
    [Fact]
    public async Task AddAsync_kimligi_govdede_yollar_ve_uca_gider()
    {
        var (client, handler) = Build("{\"code\":\"0\"}");
        var account = Account("731734");

        await client.AddAsync(account, new[] { Rec("+905551112233") });

        handler.Uri!.ToString().Should().Be("https://api.netgsm.com.tr/iys/add");
        using var doc = JsonDocument.Parse(handler.Body!);
        var header = doc.RootElement.GetProperty("header");
        header.GetProperty("brandCode").GetString().Should().Be("731734");
        header.GetProperty("username").GetString().Should().Be(account.UserCode);

        var row = doc.RootElement.GetProperty("body").GetProperty("data")[0];
        row.GetProperty("recipient").GetString().Should().Be("+905551112233");
        row.GetProperty("status").GetString().Should().Be("ONAY");
        row.GetProperty("type").GetString().Should().Be("MESAJ");
        row.GetProperty("source").GetString().Should().Be("HS_WEB");
        // TR yerel saat, saniye hassasiyetinde
        row.GetProperty("consentDate").GetString().Should().Be("2026-09-18 10:30:00");
    }
```

- [ ] **Step 2: Derlenmediğini gör**

```bash
dotnet build OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: `CS0246: The type or namespace name 'IysAccountContext' could not be
found` ve `CS1501: No overload for method 'AddAsync' takes 2 arguments`.

- [ ] **Step 3: `IysAccountContext`'i ekle ve arayüzü değiştir**

`OrderDeck.LicenseServer/Services/Iys/IIysClient.cs` — `IysConfigurationException`
tanımından hemen sonra kaydı ekle:

```csharp
/// <summary>
/// Tek bir İYS çağrısının <b>hangi yayıncı adına</b> yapıldığı.
///
/// <para>İstemci kimliği global config'ten OKUMAZ; çağıran vermek zorundadır.
/// "Yanlış markaya sorma" hatası böylece derleme zamanında imkânsız olur:
/// hesabı elde etmenin tek yolu <c>NetgsmAccountService</c>, o da her zaman
/// tek bir lisansa bağlı satır döner.</para>
///
/// <para><see cref="LicenseId"/> çağrının kendisinde kullanılmaz — log ve
/// olay satırlarının hangi kiracıya ait olduğunu yazabilmek için taşınır.</para>
/// </summary>
public sealed record IysAccountContext(
    Guid LicenseId,
    string UserCode,
    string Password,
    string BrandCode);
```

Arayüzü değiştir:

```csharp
public interface IIysClient
{
    /// <summary>
    /// Toplu izin bildirimi. Tek HTTP isteği; <paramref name="items"/> en fazla 20 satır.
    /// İstek <paramref name="account"/> markası altında yapılır.
    /// </summary>
    Task<IysAddResult> AddAsync(
        IysAccountContext account,
        IReadOnlyList<IysConsentRecord> items,
        CancellationToken ct = default);

    /// <summary>
    /// Toplu izin sorgusu. Yanıtta olmayan alıcı <see cref="IysConsentStatus.Unknown"/> sayılır.
    /// Sorgu <paramref name="account"/> markası altında yapılır — onay marka başına
    /// tutulduğu için başka markaya sormak anlamsız bir cevap döndürür.
    /// </summary>
    Task<IysSearchResult> SearchAsync(
        IysAccountContext account,
        IReadOnlyList<string> recipients,
        CancellationToken ct = default);
}
```

- [ ] **Step 4: `NetgsmIysClient`'i hesap bağlamına çevir**

`OrderDeck.LicenseServer/Services/Iys/NetgsmIysClient.cs` — sınıf yorumuna
bir paragraf ekle, iki imzayı ve `PostAsync`'i değiştir. `_opt` **duruyor**:
`BaseUrl` ve `IysSourceCode` gerçekten global (Netgsm'in API adresi herkes
için aynı), kimlik değil.

Sınıf yorumunun sonuna:

```csharp
/// <para><b>Kimlik global DEĞİL.</b> Her çağrı bir
/// <see cref="IysAccountContext"/> alır; <c>_opt</c>'tan yalnız
/// <see cref="NetgsmOptions.BaseUrl"/> okunur, çünkü Netgsm'in API adresi
/// tüm yayıncılar için aynıdır.</para>
```

İmzalar:

```csharp
    public async Task<IysAddResult> AddAsync(
        IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
        CancellationToken ct = default)
    {
```
```csharp
        var (code, body) = await PostAsync(account, "add", data, ct);
        return new IysAddResult(code, body, Queued: code == "0");
```
```csharp
    public async Task<IysSearchResult> SearchAsync(
        IysAccountContext account, IReadOnlyList<string> recipients,
        CancellationToken ct = default)
    {
```
```csharp
        var (code, body) = await PostAsync(account, "search", data, ct);
```

`PostAsync` (`:103-135`) tamamen:

```csharp
    private async Task<(string Code, string Body)> PostAsync(
        IysAccountContext account, string path, object data, CancellationToken ct)
    {
        var payload = new
        {
            header = new
            {
                username = account.UserCode,
                password = account.Password,
                brandCode = account.BrandCode,
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
        // denemek yalnız zaman harcar. Hangi markanın düştüğü mesaja yazılır:
        // çok kiracıda "İYS ayarı bozuk" tek başına eyleme geçirilebilir değil.
        if (code is "30" or "60")
            throw new IysConfigurationException(code,
                $"İYS yapılandırma hatası (code={code}, brand={account.BrandCode}). "
                + "Marka kodu/kimlik kontrol edilmeli.");

        return (code, body.Length > 2000 ? body[..2000] : body);
    }
```

- [ ] **Step 5: `NullIysClient`'i güncelle**

`OrderDeck.LicenseServer/Services/Iys/NullIysClient.cs:18-29`:

```csharp
    public Task<IysAddResult> AddAsync(
        IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
        CancellationToken ct = default)
    {
        _log.LogInformation(
            "İYS yapılandırılmamış: {Count} kayıt gönderilmedi (brand={Brand})",
            items.Count, account.BrandCode);
        return Task.FromResult(new IysAddResult("not-configured", "", Queued: false));
    }

    public Task<IysSearchResult> SearchAsync(
        IysAccountContext account, IReadOnlyList<string> recipients,
        CancellationToken ct = default)
        => Task.FromResult(new IysSearchResult(
            "not-configured", "", new Dictionary<string, IysConsentStatus>()));
```

- [ ] **Step 6: İki işi derlenir hâle getir (davranış DEĞİŞMEDEN)**

`IysConsentPushJob.cs` — `RunAsync` içinde `pending.Count == 0` kontrolünden
sonra hesabı kur ve partiye geçir:

```csharp
        if (pending.Count == 0) return;

        // Faz 5 Task 7 bunu marka başına döngüyle değiştiriyor. Şimdilik
        // global kimlik: davranış bu commit'te birebir aynı kalsın diye.
        var account = new IysAccountContext(
            Guid.Empty, _opt.UserCode, _opt.Password, _opt.BrandCode);

        var first = true;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            if (!first) await Task.Delay(BatchDelay, ct);
            first = false;
            await PushBatchAsync(account, batch, ct);
        }
```

`PushBatchAsync` imzası ve çağrısı:

```csharp
    private async Task PushBatchAsync(
        IysAccountContext account, IysConsent[] batch, CancellationToken ct)
```
```csharp
            result = await _client.AddAsync(account, records, ct);
```

`IysConsentVerifyJob.cs` — `due.Count == 0` kontrolünden sonra:

```csharp
        if (due.Count == 0) return;

        // Faz 5 Task 8 bunu marka başına döngüyle değiştiriyor.
        var account = new IysAccountContext(
            Guid.Empty, _opt.UserCode, _opt.Password, _opt.BrandCode);

        foreach (var batch in due.Chunk(BatchSize))
```
```csharp
                result = await _client.SearchAsync(
                    account, batch.Select(c => c.Recipient).ToArray(), ct);
```

- [ ] **Step 7: İki test sahtesini derlenir hâle getir**

`IysConsentPushJobTests.cs:15-31` içindeki `FakeIysClient` — hesabı da
kaydediyor, çünkü Faz 5'in testleri tam olarak bunu okuyacak:

```csharp
    private sealed class FakeIysClient : IIysClient
    {
        public List<IReadOnlyList<IysConsentRecord>> AddCalls { get; } = new();
        public List<IysAccountContext> AddAccounts { get; } = new();
        public Func<IReadOnlyList<IysConsentRecord>, IysAddResult>? AddBehavior { get; set; }
        public Exception? ThrowOnAdd { get; set; }

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
        {
            AddAccounts.Add(account);
            AddCalls.Add(items);
            if (ThrowOnAdd is not null) throw ThrowOnAdd;
            return Task.FromResult(AddBehavior?.Invoke(items)
                ?? new IysAddResult("0", "{\"code\":\"0\"}", Queued: true));
        }

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>()));
    }
```

`IysConsentVerifyJobTests.cs:17-34` içindeki `FakeIysClient` — aynı desen;
`SearchAsync` imzasına `IysAccountContext account` başa eklenir, gövdenin ilk
satırına `SearchAccounts.Add(account);` konur ve sınıfa
`public List<IysAccountContext> SearchAccounts { get; } = new();` eklenir.
`AddAsync` imzası da başa `IysAccountContext account` alır; gövdesi
değişmez.

- [ ] **Step 8: Testleri koştur**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~Services.Iys"
```

Beklenen: hepsi PASS. Özellikle
`Istek_markasi_parametreden_gelir_global_configten_DEGIL` ve
`SearchAsync_de_hesap_baglamini_kullanir` yeşil; push/verify iş testleri
davranış değişmediği için aynen geçiyor.

- [ ] **Step 9: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IIysClient.cs \
        OrderDeck.LicenseServer/Services/Iys/NetgsmIysClient.cs \
        OrderDeck.LicenseServer/Services/Iys/NullIysClient.cs \
        OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs \
        OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs \
        OrderDeck.LicenseServer.Tests/Services/Iys/NetgsmIysClientTests.cs \
        OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs \
        OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs
git commit -m "refactor(iys): istemci kimliği global configten değil hesap bağlamından alır"
```

---
## Faz 5 — İşler marka başına döner

### Task 7: `IysConsentPushJob` marka başına döngü + marka başına yalıtım

Bugünkü iş (`IysConsentPushJob.cs:53-57, :81-85, :111-119`) üç ayrı yerden
tek kiracılı:

1. Global `_opt.BrandCode` boşsa boru hattı komple kapalı.
2. Bekleyen sorgusunda **marka filtresi yok** — A'nın ve B'nin kayıtları aynı
   partide, aynı kimlikle itiliyor.
3. `IysConfigurationException` yeniden fırlatılıyor — A'nın yanlış yazılmış
   marka kodu B'nin, C'nin, herkesin push'unu durduruyor. Tek kiracıda bu
   bilinçli ve doğruydu: tek marka vardı, devam etmek bekleyenleri sırayla
   harcardı. Çok kiracıda aynı karar platformu susturuyor.

Spec test sözleşmesi **#3** burada kilitleniyor: *"bir yayıncının bozuk ayarı
diğerinin push'unu durdurmaz."*

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs` (`:36-49` ctor, `:51-96` RunAsync, `:98-109` parti, `:163-175` AddEvent)
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs`

- [ ] **Step 1: Test dosyasının iskeletini çok kiracılıya çevir**

`IysConsentPushJobTests.cs` — `using` satırlarına ekle:

```csharp
using Microsoft.AspNetCore.DataProtection;
```

`FakeIysClient`'a marka başına hata yeteneği ekle (mevcut `ThrowOnAdd`
duruyor — tek markalı testler onu kullanmaya devam ediyor):

```csharp
        /// <summary>Marka kodu → o markada fırlatılacak hata. Marka yalıtımı testleri için.</summary>
        public Dictionary<string, Exception> ThrowByBrand { get; } = new();
```

`AddAsync` gövdesinin başına, `AddCalls.Add(items);` satırından sonra:

```csharp
            if (ThrowByBrand.TryGetValue(account.BrandCode, out var brandEx)) throw brandEx;
```

Yardımcıları değiştir — `Job` artık `NetgsmAccountService` alıyor ve
`NetgsmOptions` içinde `BrandCode` yok:

```csharp
    private static readonly Guid LicenseA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LicenseB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string BrandA = "731734";
    private const string BrandB = "763208";

    // Tek sağlayıcı: ProtectPassword ile korunan metni aynı anahtarla çözebilmek
    // için testler boyunca paylaşılıyor. Her yeni Ephemeral örneği yeni anahtar üretir.
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static NetgsmAccountService Accounts(LicenseDbContext db) => new(db, Protection);

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-push-{Guid.NewGuid():N}").Options);

    private static IysConsentPushJob Job(LicenseDbContext db, IIysClient client)
        => new(db, client, Accounts(db), Options.Create(new NetgsmOptions()),
            NullLogger<IysConsentPushJob>.Instance);

    private static void SeedAccount(LicenseDbContext db, Guid licenseId, string brandCode)
    {
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = Accounts(db).ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = "verified",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static IysConsent Pending(string phone, string brandCode = BrandA) => new()
    {
        Id = Guid.NewGuid(),
        BrandCode = brandCode,
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
        SeedAccount(db, LicenseA, BrandA);
        for (var i = 0; i < count; i++)
            db.IysConsents.Add(Pending($"+90555{i:D7}"));
        await db.SaveChangesAsync();
        return db;
    }
```

`Son_tarihi_gecmis_kayit_itilmez_Expired_olur` testi `NewDb()`'yi doğrudan
kullanıyor; oraya da hesap tohumu gerekiyor:

```csharp
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var row = Pending("+905551112233");
```

- [ ] **Step 2: Marka yalıtımı testlerini yaz**

Mevcut `Yapilandirma_hatasi_boru_hattini_durdurur` testini **sil** ve yerine
şu üçünü koy:

```csharp
    [Fact]
    public async Task Yapilandirma_hatasi_yalniz_o_markanin_kalan_partilerini_durdurur()
    {
        // Tek markanın 45 kaydı: ilk parti yapılandırma hatasıyla düşünce o
        // markanın kalan partileri denenmez (hepsi aynı hatayla düşecek),
        // ama iş ARTIK FIRLATMIYOR — döngü diğer markalara devam etmeli.
        using var db = await SeedAsync(45);
        var client = new FakeIysClient();
        client.ThrowByBrand[BrandA] = new IysConfigurationException("60", "marka kodu");

        await Job(db, client).RunAsync();

        client.AddCalls.Should().HaveCount(1, "ilk partiden sonra bu marka için durmalı");
        (await db.IysConsents.CountAsync(c => c.PushState == IysPushState.Pending))
            .Should().Be(45, "ayar hatası kayıt başına kalıcı yara olarak yazılmaz");
    }

    [Fact]
    public async Task Bir_yayincinin_bozuk_ayari_digerinin_pushunu_durdurmaz()
    {
        // Spec sözleşme #3. Bu test bugünkü davranışın TERSİNİ istiyor.
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pending("+905551110001", BrandA));
        db.IysConsents.Add(Pending("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();
        client.ThrowByBrand[BrandA] = new IysConfigurationException("60", "marka kodu");

        await Job(db, client).RunAsync();

        var a = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandA);
        var b = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandB);
        a.PushState.Should().Be(IysPushState.Pending, "A'nın ayarı bozuk, kaydı bekliyor");
        b.PushState.Should().Be(IysPushState.Pushed, "B, A'nın hatasından etkilenmemeli");
    }

    [Fact]
    public async Task Her_parti_kendi_markasinin_kimligiyle_gider()
    {
        // Spec sözleşme #11'in push ucu: istek markası = itilen satırın markası.
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pending("+905551110001", BrandA));
        db.IysConsents.Add(Pending("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddAccounts.Should().HaveCount(2);
        for (var i = 0; i < client.AddCalls.Count; i++)
        {
            var brand = client.AddAccounts[i].BrandCode;
            var expectedPhone = brand == BrandA ? "+905551110001" : "+905551110002";
            client.AddCalls[i].Select(r => r.Recipient).Should().Equal(expectedPhone);
        }
    }

    [Fact]
    public async Task Dogrulanmamis_hesabin_kayitlari_itilmez()
    {
        // Fail-closed: hesap "verified" değilse o markanın adına konuşamayız.
        using var db = NewDb();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = LicenseA,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = Accounts(db).ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = BrandA,
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.IysConsents.Add(Pending("+905551110001", BrandA));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddCalls.Should().BeEmpty();
        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Pending);
    }
```

- [ ] **Step 3: Testleri koştur, düştüklerini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter IysConsentPushJobTests
```

Beklenen: DERLEME HATASI — `IysConsentPushJob` yapıcısı 4 argüman almıyor.

- [ ] **Step 4: İşi marka başına döngüye çevir**

`IysConsentPushJob.cs` — sınıf yorumunun sonuna ekle:

```csharp
/// <para><b>Marka başına yalıtım:</b> döngü doğrulanmış her Netgsm hesabı için
/// ayrı döner ve her tur kendi <c>try/catch</c>'i içindedir. Bir yayıncının
/// yanlış marka kodu yalnız kendi turunu bitirir; diğerleri etkilenmez.
/// Tek kiracıda <c>throw</c> etmek doğruydu — çok kiracıda platformu susturur.</para>
```

Alanlar ve yapıcı (`:30-49`):

```csharp
    /// <summary>Tek istekte bildirilen kayıt sayısı.</summary>
    public const int BatchSize = 20;

    /// <summary>
    /// Tek koşuda tek markadan alınacak azami kayıt. Sınır olmazsa 10.000
    /// bekleyeni olan bir yayıncı, 6 saniyelik parti gecikmesiyle koşuyu
    /// saatlerce meşgul eder ve sıradaki markalar hiç sıra alamaz.
    /// 5 dakikada bir × 100 = günde 28.800 kayıt/marka, 3 iş günü penceresine
    /// rahat sığıyor.
    /// </summary>
    public const int MaxPerBrandPerRun = BatchSize * 5;

    /// <summary>Netgsm ~10 istek/dk sınırlı; partiler arası bekleme.</summary>
    public static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(6);

    private readonly LicenseDbContext _db;
    private readonly IIysClient _client;
    private readonly NetgsmAccountService _accounts;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentPushJob> _log;

    public IysConsentPushJob(
        LicenseDbContext db, IIysClient client, NetgsmAccountService accounts,
        IOptions<NetgsmOptions> opt, ILogger<IysConsentPushJob> log)
    {
        _db = db;
        _client = client;
        _accounts = accounts;
        _opt = opt.Value;
        _log = log;
    }
```

`RunAsync` (`:51-96`) tamamen:

```csharp
    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Süresi dolmuş bekleyenler hiç gönderilmez: 3 iş günü geçtiyse
        // İYS zaten H467 ile reddeder (consent_date çok eski) ve kayıt
        // hukuken geçersiz. Sessizce silmiyoruz — Expired damgası admin
        // listesinde görünür. Bu süpürme marka bağımsız: süre dolmuşsa
        // hangi yayıncıya ait olduğu sonucu değiştirmez.
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

        var accounts = await _accounts.ListVerifiedAsync(ct);
        if (accounts.Count == 0)
        {
            _log.LogInformation("İYS push: doğrulanmış Netgsm hesabı yok, boru hattı kapalı");
            return;
        }

        foreach (var acct in accounts)
        {
            try
            {
                await PushBrandAsync(acct, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // koşu iptal edildi; sıradaki markaya geçmek anlamsız
            }
            catch (Exception ex)
            {
                // Marka başına yalıtım (spec §4, sözleşme #3).
                _log.LogError(ex,
                    "İYS push: {Brand} markası atlandı (lisans {LicenseId})",
                    acct.BrandCode, acct.LicenseId);
            }
        }
    }

    private async Task PushBrandAsync(
        NetgsmAccount acct, DateTimeOffset now, CancellationToken ct)
    {
        var password = _accounts.TryUnprotectPassword(acct.PasswordProtected);
        if (password is null)
        {
            // Anahtar döndü ya da şifreli metin bozuk. Patlamak yerine bu
            // markayı atlıyoruz; diğer yayıncıların push'u devam etsin.
            _log.LogError(
                "İYS push: {Brand} markasının şifresi çözülemedi, tur atlandı", acct.BrandCode);
            return;
        }

        var account = new IysAccountContext(
            acct.LicenseId, acct.UserCode, password, acct.BrandCode);

        var pending = await _db.IysConsents
            .Where(c => c.BrandCode == acct.BrandCode
                        && c.PushState == IysPushState.Pending
                        && (c.PushDeadline == null || c.PushDeadline >= now))
            .OrderBy(c => c.CreatedAt)
            .Take(MaxPerBrandPerRun)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var first = true;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            if (!first) await Task.Delay(BatchDelay, ct);
            first = false;
            await PushBatchAsync(account, batch, ct);
        }
    }
```

- [ ] **Step 5: Parti ve olay yazımını hesaba bağla**

`PushBatchAsync` imzası zaten Task 6'da `account` aldı. `AddEvent`
çağrılarına hesabı geçir ve `AddEvent`'i kiracı sütunlarını yazacak hâle
getir:

```csharp
                AddEvent(c, account, IysConsentEventType.PushAttempt,
                    code: null, body: null, error: ex.GetType().Name);
```
```csharp
            AddEvent(c, account, IysConsentEventType.PushAttempt,
                result.Code, result.RawBody, error: null);
```
```csharp
    private void AddEvent(
        IysConsent c, IysAccountContext account, IysConsentEventType type,
        string? code, string? body, string? error)
        => _db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            LicenseId = account.LicenseId,
            BrandCode = account.BrandCode,
            IysConsentId = c.Id,
            Recipient = c.Recipient,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = type,
            Status = c.Status,
            ApiResponseCode = code,
            ApiResponseBody = Truncate(body, 2000),
            ErrorCode = error,
        });
```

`PushBatchAsync` içindeki `catch (IysConfigurationException cfg)` bloğunun
yorumunu güncelle — `throw` **duruyor**, artık marka döngüsü yakalıyor:

```csharp
        catch (IysConfigurationException cfg)
        {
            // Kalıcı yapılandırma hatası: bu markanın her kaydı aynı hatayla
            // düşer, kalan partileri denemek zaman harcar. Fırlatılan istisnayı
            // RunAsync'teki marka döngüsü yakalar → yalnız BU marka atlanır.
            // Kayıtlara DOKUNULMAZ: Failed yazmak, düzeltilebilir bir ayar
            // hatasını kayıt başına kalıcı yara gibi gösterirdi.
            _log.LogError(cfg,
                "İYS yapılandırma hatası ({Code}) — {Brand} markasının turu durdu",
                cfg.Code, account.BrandCode);
            throw;
        }
```

- [ ] **Step 6: DI kaydını kontrol et**

`Program.cs:195-198` — `IysConsentPushJob` zaten `AddScoped` ile kayıtlı ve
`NetgsmAccountService` Task 2'de scoped olarak eklendi; DI otomatik çözer.
Değişiklik gerekmiyor, sadece **doğrula**:

```bash
grep -n "IysConsentPushJob\|NetgsmAccountService" OrderDeck.LicenseServer/Program.cs
```

Beklenen: ikisi de `AddScoped` satırlarında görünüyor.

- [ ] **Step 7: Testleri koştur**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter IysConsentPushJobTests
```

Beklenen: hepsi PASS. (Bu sınıf yavaş — `Bekleyenler_yirmiserli_partilenir`
iki kez 6 saniye bekliyor, bu mevcut davranış.)

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysConsentPushJob.cs \
        OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentPushJobTests.cs
git commit -m "fix(iys): push işi marka başına döner, bir yayıncının hatası diğerlerini durdurmaz"
```

---
### Task 8: `IysConsentVerifyJob` marka başına sorar, hat başı tıkanmasını çözer

Bu iş bugün iki ayrı şekilde kiracı-kör:

1. `:47` global `_opt.BrandCode` gate'i; `:64` sorguyu **global kimlikle**
   yapıyor — B'nin kaydı merkezî markaya sorulup cevabı B'nin satırına
   yazılıyor. Tamamen sessiz veri bozulması.
2. `:54` `Take(BatchSize * 5)` sınırını **markadan ÖNCE** uyguluyor ve
   `:73-77` geçici hatada `NextVerifyAt`'e **dokunmuyor**. Sonuç: A'da
   sürekli hata veren 100 kayıt her koşuda ilk 100 sırayı kapıyor, B'nin tek
   hazır kaydı sonsuza dek sıra bekliyor.

Spec test sözleşmesi **#12** burada kilitleniyor: *"A'da 100 eski hatalı kayıt
varken B'nin tek hazır kaydı yine doğrulanır."*

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs` (`:25-128` gövdenin tamamı)
- Test: `OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs`

- [ ] **Step 1: Test iskeletini çok kiracılıya çevir**

`IysConsentVerifyJobTests.cs` — `using` satırlarına ekle:

```csharp
using Microsoft.AspNetCore.DataProtection;
```

`FakeIysClient`'ı hesap bağlamını kaydedecek ve marka başına hata
fırlatabilecek hâle getir (Task 6'da imzalar zaten değişmişti):

```csharp
    private sealed class FakeIysClient : IIysClient
    {
        public Dictionary<string, IysConsentStatus> Answer { get; set; } = new();
        public List<IReadOnlyList<string>> SearchCalls { get; } = new();
        public List<IysAccountContext> SearchAccounts { get; } = new();

        /// <summary>Marka kodu → o markada fırlatılacak hata.</summary>
        public Dictionary<string, Exception> ThrowByBrand { get; } = new();

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => Task.FromResult(new IysAddResult("0", "{}", true));

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
        {
            SearchAccounts.Add(account);
            SearchCalls.Add(recipients);
            if (ThrowByBrand.TryGetValue(account.BrandCode, out var ex)) throw ex;
            return Task.FromResult(new IysSearchResult("0", "{\"code\":\"0\"}", Answer));
        }
    }
```

Yardımcılar — `Job` artık `NetgsmAccountService` alıyor, `IOptions<NetgsmOptions>`
**almıyor**:

```csharp
    private const string Phone = "+905551112233";
    private static readonly Guid LicenseA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LicenseB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string BrandA = "731734";
    private const string BrandB = "763208";

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static NetgsmAccountService Accounts(LicenseDbContext db) => new(db, Protection);

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-verify-{Guid.NewGuid():N}").Options);

    private static IysConsentVerifyJob Job(LicenseDbContext db, IIysClient client)
        => new(db, client, Accounts(db), NullLogger<IysConsentVerifyJob>.Instance);

    private static void SeedAccount(LicenseDbContext db, Guid licenseId, string brandCode)
    {
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = Accounts(db).ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = "verified",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static IysConsent Pushed(
        string phone, string brandCode, DateTimeOffset? nextVerifyAt = null, int attempts = 0)
    {
        var now = DateTimeOffset.UtcNow;
        return new IysConsent
        {
            Id = Guid.NewGuid(),
            BrandCode = brandCode,
            ChannelType = "MESAJ",
            RecipientType = "BIREYSEL",
            Recipient = phone,
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
        };
    }

    private static async Task<LicenseDbContext> SeedPushedAsync(
        DateTimeOffset? nextVerifyAt = null, int attempts = 0)
    {
        var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        db.IysConsents.Add(Pushed(Phone, BrandA, nextVerifyAt, attempts));
        await db.SaveChangesAsync();
        return db;
    }
```

Mevcut altı test gövdesi **değişmiyor** — hepsi `SeedPushedAsync` ve `Job`
üzerinden geçiyor. `Sonuc_olay_tablosuna_yazilir` testine iki satır ekle:

```csharp
        ev.Status.Should().Be(IysConsentStatus.Onay);
        ev.ApiResponseCode.Should().Be("0");
        ev.LicenseId.Should().Be(LicenseA, "kanıt kiracıya bağlanabilmeli");
        ev.BrandCode.Should().Be(BrandA);
```

- [ ] **Step 2: Hat başı tıkanması ve marka yalıtımı testlerini yaz**

```csharp
    [Fact]
    public async Task Bir_markanin_yuz_tikali_kaydi_digerini_ac_birakmaz()
    {
        // Spec sözleşme #12. Eski kod sınırı markadan ÖNCE uyguluyordu:
        // ilk 100 sıra A'nın kayıtlarıyla doluyor, B hiç sıra alamıyordu.
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);

        var old = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i < 100; i++)
            db.IysConsents.Add(Pushed($"+90555{i:D7}", BrandA, nextVerifyAt: old));
        db.IysConsents.Add(Pushed("+905559999999", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.SearchAccounts.Select(a => a.BrandCode).Should().Contain(BrandB,
            "B'nin hazır kaydı A'nın kuyruğunun arkasında beklememeli");
    }

    [Fact]
    public async Task Gecici_hata_randevuyu_ILERI_alir_ama_takvimi_tuketmez()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient();
        client.ThrowByBrand[BrandA] = new HttpRequestException("ağ");

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.NextVerifyAt.Should().BeAfter(DateTimeOffset.UtcNow,
            "randevu olduğu yerde kalırsa aynı kayıt her koşuda ilk sırayı kapar");
        row.VerifyAttempts.Should().Be(0,
            "VerifyAttempts İYS cevabını bekleme takvimidir; ağ hatası onu tüketmemeli");
        row.PushState.Should().Be(IysPushState.Pushed);
    }

    [Fact]
    public async Task Sorgu_kendi_markasinin_kimligiyle_gider()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pushed("+905551110001", BrandA));
        db.IysConsents.Add(Pushed("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.SearchAccounts.Should().HaveCount(2);
        for (var i = 0; i < client.SearchCalls.Count; i++)
        {
            var expectedPhone = client.SearchAccounts[i].BrandCode == BrandA
                ? "+905551110001" : "+905551110002";
            client.SearchCalls[i].Should().Equal(expectedPhone);
        }
    }

    [Fact]
    public async Task Bir_yayincinin_yapilandirma_hatasi_digerini_durdurmaz()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pushed("+905551110001", BrandA));
        db.IysConsents.Add(Pushed("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient { Answer = { ["+905551110002"] = IysConsentStatus.Onay } };
        client.ThrowByBrand[BrandA] = new IysConfigurationException("60", "marka kodu");

        await Job(db, client).RunAsync();

        var b = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandB);
        b.PushState.Should().Be(IysPushState.Confirmed, "B, A'nın bozuk ayarından etkilenmemeli");
    }
```

- [ ] **Step 3: Testleri koştur, düştüklerini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter IysConsentVerifyJobTests
```

Beklenen: DERLEME HATASI — `IysConsentVerifyJob` yapıcısı `NetgsmAccountService`
almıyor.

- [ ] **Step 4: İşi yeniden yaz**

`OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs` — sınıf
yorumunun sonuna ekle:

```csharp
/// <para><b>Marka başına adalet:</b> hem sorgu hem koşu sınırı marka başına
/// uygulanır. Sınır markadan önce uygulanırsa, kalıcı hata veren bir
/// yayıncının kayıtları her koşuda ilk sıraları kapar ve diğer yayıncılar
/// sonsuza dek doğrulanmadan bekler (hat başı tıkanması).</para>
```

`using` satırlarından `Microsoft.Extensions.Options`'ı **sil**;
`OrderDeck.LicenseServer.Services.Sms` duruyor (`NetgsmAccountService` orada).

Alanlar ve yapıcı (`:27-43`):

```csharp
    /// <summary>Tek sorguda sorulan alıcı sayısı.</summary>
    public const int BatchSize = 20;

    /// <summary>Tek koşuda tek markadan doğrulanacak azami kayıt.</summary>
    public const int MaxPerBrandPerRun = BatchSize * 5;

    /// <summary>Ağ/geçici hata sonrası aynı kaydı yeniden denemeden önceki bekleme.</summary>
    public static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMinutes(5);

    private readonly LicenseDbContext _db;
    private readonly IIysClient _client;
    private readonly NetgsmAccountService _accounts;
    private readonly ILogger<IysConsentVerifyJob> _log;

    public IysConsentVerifyJob(
        LicenseDbContext db, IIysClient client, NetgsmAccountService accounts,
        ILogger<IysConsentVerifyJob> log)
    {
        _db = db;
        _client = client;
        _accounts = accounts;
        _log = log;
    }
```

`RunAsync` (`:45-128`) tamamen:

```csharp
    public async Task RunAsync(CancellationToken ct = default)
    {
        var accounts = await _accounts.ListVerifiedAsync(ct);
        if (accounts.Count == 0) return;

        foreach (var acct in accounts)
        {
            try
            {
                await VerifyBrandAsync(acct, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Marka başına yalıtım: A'nın bozuk ayarı B'nin doğrulamasını
                // durdurmaz. Randevular olduğu yerde kalır; ayar düzeltilince
                // kaldığı yerden devam eder.
                _log.LogError(ex,
                    "İYS doğrulama: {Brand} markası atlandı (lisans {LicenseId})",
                    acct.BrandCode, acct.LicenseId);
            }
        }
    }

    private async Task VerifyBrandAsync(NetgsmAccount acct, CancellationToken ct)
    {
        var password = _accounts.TryUnprotectPassword(acct.PasswordProtected);
        if (password is null)
        {
            _log.LogError(
                "İYS doğrulama: {Brand} markasının şifresi çözülemedi, tur atlandı",
                acct.BrandCode);
            return;
        }

        var account = new IysAccountContext(
            acct.LicenseId, acct.UserCode, password, acct.BrandCode);

        var now = DateTimeOffset.UtcNow;
        var due = await _db.IysConsents
            .Where(c => c.BrandCode == acct.BrandCode
                        && c.PushState == IysPushState.Pushed
                        && c.NextVerifyAt != null && c.NextVerifyAt <= now)
            .OrderBy(c => c.NextVerifyAt)
            .Take(MaxPerBrandPerRun)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var batch in due.Chunk(BatchSize))
        {
            IysSearchResult result;
            try
            {
                result = await _client.SearchAsync(
                    account, batch.Select(c => c.Recipient).ToArray(), ct);
            }
            catch (IysConfigurationException cfg)
            {
                // Bu markanın turu durur — RunAsync'teki döngü yakalar.
                // Randevular olduğu yerde kalır: ayar düzeltildiğinde
                // doğrulama kaldığı yerden devam eder.
                _log.LogError(cfg,
                    "İYS yapılandırma hatası ({Code}) — {Brand} markasının doğrulaması durdu",
                    cfg.Code, account.BrandCode);
                throw;
            }
            catch (Exception ex)
            {
                // Randevuyu İLERİ AL. Eskiden randevu olduğu yerde kalıyordu:
                // kalıcı hata veren kayıtlar her koşuda yeniden ilk sıraları
                // kapıyor, aynı markanın diğer kayıtları hiç sıra alamıyordu.
                // VerifyAttempts'e DOKUNULMAZ — o sayaç İYS'nin cevabını
                // bekleme takvimidir; bir ağ hatası onu tüketip kaydı erken
                // Failed yapmamalı.
                var retryAt = DateTimeOffset.UtcNow + TransientRetryDelay;
                foreach (var c in batch)
                {
                    c.NextVerifyAt = retryAt;
                    c.UpdatedAt = retryAt;
                }
                await _db.SaveChangesAsync(ct);
                _log.LogWarning(ex,
                    "İYS doğrulama: {Brand} markasında {Count} kayıtlık sorgu başarısız",
                    account.BrandCode, batch.Length);
                continue;
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
                    LicenseId = account.LicenseId,
                    BrandCode = account.BrandCode,
                    IysConsentId = c.Id,
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
            "İYS doğrulama ({Brand}): {Total} kayıt soruldu, {Confirmed} ONAY",
            account.BrandCode, due.Count, confirmed);
    }
```

- [ ] **Step 5: Testleri koştur**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter IysConsentVerifyJobTests
```

Beklenen: hepsi PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysConsentVerifyJob.cs \
        OrderDeck.LicenseServer.Tests/Services/Iys/IysConsentVerifyJobTests.cs
git commit -m "fix(iys): doğrulama marka başına sorar, hat başı tıkanması giderildi"
```

---
## Faz 6 — Gönderim kapısı kampanyanın markasını okur

### Task 9: `SmsCampaignSendJob` markayı lisanstan çözer, `NetgsmOptions.BrandCode` silinir

`SmsCampaignSendJob.cs:107` kapıyı **global** markayla açıyor:

```csharp
.Where(c => c.BrandCode == _iysBrandCode && phones.Contains(c.Recipient))
```

Çok kiracıda bu, A yayıncısının kampanyasını B'nin onay listesine karşı
denetlemek demek. İki yönde de yanlış: A'nın gerçek onayları görünmez (herkes
`iys-consent-missing` düşer) ya da — daha kötüsü — B'nin onayı A'nın
kampanyasının kapısını açar ve kişiye hiç izin vermediği bir yayıncıdan SMS
gider. İkincisi 6563 ihlali.

Bu görev aynı zamanda `NetgsmOptions.BrandCode`'u **siliyor**. Geriye dönük
bir kabuk bırakmıyoruz: özellik durduğu sürece biri onu okumaya devam eder ve
tek kiracılı davranış sessizce geri gelir. Derleyici hatası, yorum satırından
daha güvenilir bir bekçi.

Spec test sözleşmesi **#4** burada kilitleniyor: *"kampanya yalnız kendi
markasının onaylarını görür."*

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs:36-54,102-124`
- Modify: `OrderDeck.LicenseServer/Services/Sms/NetgsmOptions.cs:29-36` (sil)
- Modify: `OrderDeck.LicenseServer/appsettings.json:30` (`"BrandCode"` anahtarı silinir)
- Modify: `OrderDeck.LicenseServer/Program.cs:179-182` (yorum)
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignIysGateTests.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/SmsCampaignTests.cs`

> **`IysConsentRecoveryJob` bilerek dokunulmuyor.** Yaptığı iki iş de marka
> bağımsız: `Failed → Pending` geri alma ve son tarih uyarısı. Marka başına
> ayırmak yalnız günlük çıktısını böler, davranışı değiştirmez.

- [ ] **Step 1: Kapı testini çok kiracılıya çevir**

`SmsCampaignIysGateTests.cs` — `SeedCampaignAsync` artık markayı da kuruyor ve
geri döndürüyor. İmzayı ve gövdenin ilgili kısmını değiştir:

```csharp
    /// <summary>
    /// Lisans + doğrulanmış Netgsm hesabı + kredi + kampanya + tek alıcı.
    /// Marka kodu lisansa özel üretiliyor: kapı artık kampanyanın lisansından
    /// markayı çözüyor, global yapılandırmadan DEĞİL.
    /// </summary>
    private static async Task<(Guid CampaignId, string BrandCode)> SeedCampaignAsync(
        LicenseDbContext db, NetgsmAccountService accounts, string phone)
    {
```

`db.Licenses.Add(license);` satırından hemen sonra ekle:

```csharp
        var brandCode = Random.Shared.Next(100000, 999999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = "verified",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
```

Metodun son satırını değiştir:

```csharp
        await db.SaveChangesAsync();
        return (campaign.Id, brandCode);
    }
```

`SeedConsentAsync` marka alır:

```csharp
    /// <summary>
    /// Kapının okuduğu satırı kurar. <c>brandCode</c> kampanyanın lisansına ait
    /// markadır — başka bir markanın satırı bu kampanyayı AÇMAMALI.
    /// </summary>
    private static async Task SeedConsentAsync(
        LicenseDbContext db, string brandCode, string phone,
        IysConsentStatus status, IysConsentStatus? verified)
    {
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(),
            BrandCode = brandCode,
            ChannelType = "MESAJ",
            RecipientType = "BIREYSEL",
            Recipient = phone,
            Status = status,
            LastVerifiedStatus = verified,
            LastVerifiedAt = verified is null ? null : now,
            PushState = verified == IysConsentStatus.Onay
                ? IysPushState.Confirmed
                : IysPushState.Pushed,
            LastLocalEventAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }
```

Üç testin kurulum satırlarını güncelle — her birinde scope'tan servis çek:

```csharp
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, brandCode) = await SeedCampaignAsync(db, accounts, phone);
```

`Onayi_geri_cekilmis_alici_gonderilmez_ve_iade_edilir` içinde `brandCode`
kullanılmıyor (kayıt bilerek yok) — satırı `var (campaignId, _) = ...` yap.
Diğer ikisinde onay tohumu markayı alır:

```csharp
        await SeedConsentAsync(db, brandCode, phone, IysConsentStatus.Onay, IysConsentStatus.Onay);
```
```csharp
        await SeedConsentAsync(db, brandCode, phone, IysConsentStatus.Onay, IysConsentStatus.Ret);
```

- [ ] **Step 2: Sızıntı testini yaz**

Aynı dosyaya ekle — bu test bugünkü kodda **geçer gibi görünmez**, çünkü
bugün her iki satır da aynı (global) markaya yazılır:

```csharp
    [Fact]
    public async Task Baska_yayincinin_onayi_bu_kampanyanin_kapisini_ACMAZ()
    {
        // Spec sözleşme #4. İzin marka başına: kişi B yayıncısına onay verdiyse
        // A'nın kampanyası o onayı kullanamaz. Kullanırsa kişiye hiç izin
        // vermediği bir yayıncıdan ticari SMS gider — 6563 ihlali.
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, _) = await SeedCampaignAsync(db, accounts, phone);

        // Onay BAŞKA bir markaya ait — kampanyanın lisansıyla ilgisi yok.
        var otherBrand = Random.Shared.Next(100000, 999999).ToString();
        await SeedConsentAsync(db, otherBrand, phone, IysConsentStatus.Onay, IysConsentStatus.Onay);

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("failed");
        recipient.Error.Should().Be("iys-consent-missing");
        _factory.Sms.Sent.Should().NotContain(m => m.Phone == phone);
    }

    [Fact]
    public async Task Dogrulanmis_Netgsm_hesabi_olmayan_lisans_hic_gonderemez()
    {
        // Fail-closed: marka çözülemiyorsa hiçbir onay geçerli sayılamaz.
        // Hata kodu ayrı: "kayıt yok" ile "yayıncı kurulumunu bitirmemiş"
        // farklı sorunlar, admin ekranında ayrışmalı.
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, brandCode) = await SeedCampaignAsync(db, accounts, phone);
        await SeedConsentAsync(db, brandCode, phone, IysConsentStatus.Onay, IysConsentStatus.Onay);

        // Hesabı doğrulanmamış hâle getir: marka artık çözülmemeli.
        var account = await db.NetgsmAccounts.SingleAsync(a => a.BrandCode == brandCode);
        account.Status = "disabled";
        await db.SaveChangesAsync();

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("failed");
        recipient.Error.Should().Be("iys-brand-missing");
        _factory.Sms.Sent.Should().NotContain(m => m.Phone == phone);
    }
```

- [ ] **Step 3: Testleri koştur, düştüklerini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter SmsCampaignIysGateTests
```

Beklenen: DERLEME HATASI — `NetgsmAccountService` `SeedCampaignAsync`
imzasında yok / `SmsCampaignSendJob` hâlâ `NetgsmOptions` istiyor.

- [ ] **Step 4: İşi markayı lisanstan çözecek hâle getir**

`SmsCampaignSendJob.cs` — alanlar ve yapıcı (`:36-54`):

```csharp
    private readonly LicenseDbContext _db;
    private readonly ISmsSender _sms;
    private readonly LicenseSmsBalanceService _balance;
    private readonly NetgsmAccountService _accounts;
    private readonly ILogger<SmsCampaignSendJob> _log;

    public SmsCampaignSendJob(
        LicenseDbContext db,
        ISmsSender sms,
        LicenseSmsBalanceService balance,
        NetgsmAccountService accounts,
        ILogger<SmsCampaignSendJob> log)
    {
        _db = db;
        _sms = sms;
        _balance = balance;
        _accounts = accounts;
        _log = log;
    }
```

`using Microsoft.Extensions.Options;` satırı başka bir şey için kullanılmıyorsa
**sil** (derleyici uyarısı vermez, ama ölü `using` bırakmayalım):

```bash
grep -n "Options\." OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs
```

Boş dönerse `using Microsoft.Extensions.Options;` satırını sil.

Onay sorgusu (`:102-108`) tamamen:

```csharp
        // Kural 7: liste kampanya oluşturulurken donduruldu; gönderim şimdi.
        // Aradaki geri çekme ya da İYS RET'i burada okunur. Tek sorguyla
        // çekiliyor — alıcı başına sorgu, bin kişilik kampanyada bin gidiş.
        var phones = recipients.Select(r => r.Phone).Distinct().ToList();

        // Marka KAMPANYANIN LİSANSINDAN gelir, global yapılandırmadan değil.
        // İzin marka başına tutulur: başka bir markanın ONAY'ıyla bu kampanyanın
        // kapısını açmak, kişiye hiç izin vermediği bir yayıncıdan ticari SMS
        // göndermek demektir (6563 ihlali).
        var brandCode = await _accounts.GetBrandCodeAsync(campaign.LicenseId, ct);
        if (brandCode is null)
        {
            _log.LogWarning(
                "SmsCampaignSendJob: campaign {Id} lisansının doğrulanmış İYS markası yok; "
                + "tüm alıcılar kapıda kalacak", campaignId);
        }

        var consents = brandCode is null
            ? new Dictionary<string, IysConsent>()
            : await _db.IysConsents
                .Where(c => c.BrandCode == brandCode && phones.Contains(c.Recipient))
                .ToDictionaryAsync(c => c.Recipient, ct);
```

Kapı dalındaki hata kodunu üç sınıfa ayır (`:119`):

```csharp
                r.Status = "failed";
                r.Error = brandCode is null
                    ? "iys-brand-missing"
                    : consent is null ? "iys-consent-missing" : "iys-consent-not-onay";
                r.SentAt = null;
```

- [ ] **Step 5: `NetgsmOptions.BrandCode`'u sil**

`OrderDeck.LicenseServer/Services/Sms/NetgsmOptions.cs:29-36` — bu bloğun
**tamamını sil**:

```csharp
    /// <summary>
    /// İYS marka kodu (İYS'de "brandCode"). Değer yalnız yapılandırmadan gelir
    /// ...
    /// </summary>
    public string BrandCode { get; set; } = "";
```

Yerine, sınıf yorumunun sonuna bir not bırak (`:3-6`):

```csharp
/// <summary>
/// Netgsm REST API kimlik bilgileri. Prod'da VPS .env'den bind edilir
/// (<c>Netgsm__UserCode</c> vb.); dev'de boş kalır ve Sms:Provider=log olur.
///
/// <para><b>Burada marka kodu YOKTUR.</b> İYS markası yayıncı başınadır ve
/// <c>NetgsmAccount.BrandCode</c> sütununda durur. Global bir <c>BrandCode</c>
/// özelliği, tüm yayıncıların onaylarını tek markaya yazan eski tek kiracılı
/// davranışı sessizce geri getirirdi.</para>
/// </summary>
```

`Program.cs:179-182` yorumunu düzelt:

```csharp
        // İYS onay boru hattı. İstemci, SMS sağlayıcısıyla AYNI koşula bağlı:
        // Netgsm yoksa İYS de yok, çünkü İYS'ye erişim Netgsm aracılığıyla.
        // Dev/test'te kayıt yine toplanır ve Pending'de bekler — işler
        // doğrulanmış NetgsmAccount satırı olmadığı için hiçbir şey göndermez.
```

Son olarak `OrderDeck.LicenseServer/appsettings.json:30` satırını **sil**:

```json
    "BrandCode": "",
```

Özellik gidince bu anahtarın bağlanacağı bir yer kalmıyor; ASP.NET Core
eşleşmeyen anahtarı sessizce yok sayar. Bırakılırsa dosyayı okuyan biri hâlâ
çalışan bir anahtar sanır ve prod `.env`'e `Netgsm__BrandCode=731734` yazıp
"boru hattını açtım" der — hiçbir şey olmaz, üstelik neden olmadığı da
görünmez. Yanlış anlaşılan bir ayar, olmayan bir ayardan tehlikelidir.

> **Prod notu (Task 10 Step 6'da tekrar geçecek):** VPS `.env` dosyasında
> `Netgsm__BrandCode` satırı varsa deploy sonrası silinmeli. Zararsızdır
> (bağlanacak özellik yok) ama aynı yanılgıyı sunucu tarafında üretir.

- [ ] **Step 6: `SmsCampaignTests` kurulumunu markaya bağla**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/SmsCampaignTests.cs` —
`licenseId = license.Id;` satırından hemen sonra ekle:

```csharp
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var brandCode = Random.Shared.Next(100000, 999999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = "verified",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
```

`AddShopperLink` içindeki İYS satırının yorumunu ve markasını değiştir
(`:67-76`):

```csharp
            // Gönderim kapısı (kural 7) gönderim ANINDA İYS satırını okur:
            // yerel onay TEK BAŞINA yetmez, İYS'nin de ONAY demiş olması
            // gerekir. Bu testler gönderimin gerçekleştiğini ölçüyor, o yüzden
            // izinli shopper'ın doğrulanmış kaydı da kurulmalı. BrandCode
            // yukarıda bu lisans için üretilen marka — kapı artık kampanyanın
            // lisansından markayı çözüyor.
            var now = DateTimeOffset.UtcNow;
            db.IysConsents.Add(new IysConsent
            {
                Id = Guid.NewGuid(),
                BrandCode = brandCode,
```

- [ ] **Step 7: Tüm sunucu testlerini koştur**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: hepsi PASS. `NetgsmOptions.BrandCode` silindiği için geride kalan
her kullanım derleme hatası verir — çıktıda `CS0117` görürsen o dosya bu
planda atlanmış demektir, düzelt ve nedenini not al.

Docker açıksa Testcontainers testleri de koşar
(`NetgsmAccountUniqueIndexTests`, `IysConsentTenantIsolationTests`). Docker
kapalıysa onlar düşer; yerelde `DOCKER_HOST=npipe://./pipe/dockerDesktopLinuxEngine`
(PowerShell'den) gerekebilir.

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs \
        OrderDeck.LicenseServer/Services/Sms/NetgsmOptions.cs \
        OrderDeck.LicenseServer/appsettings.json \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignIysGateTests.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/SmsCampaignTests.cs
git commit -m "fix(sms): gönderim kapısı kampanyanın kendi markasını okur; global BrandCode silindi"
```

---
## Faz 7 — bütünün doğrulanması

### Task 10: Uçtan uca doğrulama ve yayına alma kontrol listesi

Bu görevde yeni kod yazılmıyor; önceki dokuz görevin birlikte doğru davrandığı
doğrulanıyor ve değişiklik prod'a **boru hattı kapalı** hâlde çıkarılıyor.

**Files:**
- Modify: yok (yalnız doğrulama)

- [ ] **Step 1: Tam test koşusu, Docker AÇIK**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: tümü PASS. Testcontainers testleri (`NetgsmAccountUniqueIndexTests`,
`IysConsentTenantIsolationTests`) **atlanmamalı** — atlanırsa B1 çakışma
korumasının kanıtı yok demektir. InMemory tekil indeksi uygulamıyor: o iki
test InMemory'de yeşil yanar ama prod'u bozar.

Docker kapalıysa yerelde şunu dene (PowerShell'den, git-bash değeri bozuyor):

```
$env:DOCKER_HOST="npipe://./pipe/dockerDesktopLinuxEngine"
```

- [ ] **Step 2: Göç zincirini kontrol et**

```bash
dotnet ef migrations list --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Beklenen: `AddNetgsmAccount` ve `AddIysConsentEventTenant` listede, `(Pending)`
olarak. İkisi de **eklemeli** (yalnız `CreateTable` / `AddColumn` / `CreateIndex`)
— hiçbir `DropColumn` ya da `AlterColumn` olmamalı. Varsa dur ve nedenini bul:
`IysConsent` şeması bu planda değişmiyor.

- [ ] **Step 3: Global marka kalıntısı kalmadığını doğrula**

```bash
grep -rn "NetgsmOptions" --include=*.cs OrderDeck.LicenseServer OrderDeck.LicenseServer.Tests | grep -i brand
```

Beklenen: **boş çıktı**. Bir şey çıkarsa o çağrı yeri planda atlanmış demektir.

```bash
grep -rn "_opt.BrandCode\|netgsm.Value.BrandCode" --include=*.cs .
```

Beklenen: **boş çıktı**.

```bash
grep -n "BrandCode" OrderDeck.LicenseServer/appsettings.json
```

Beklenen: **boş çıktı**. Kalan `IysConsent.BrandCode` / `NetgsmAccount.BrandCode`
eşleşmeleri **normaldir** — silinen şey global ayar, sütun değil.

- [ ] **Step 4: Fail-closed davranışı elle doğrula (yerel)**

Sunucuyu yerelde çalıştır ve Hangfire işlerini bir kez tetikle. Beklenen
günlük satırları:

```
İYS push: doğrulanmış Netgsm hesabı yok, boru hattı kapalı
```

`NetgsmAccounts` tablosu boşken push ve verify işlerinin **hiç HTTP isteği
yapmaması** gerekiyor. Bir istek görünüyorsa Faz 5 eksik uygulanmış.

- [ ] **Step 5: PR aç ve CI'yı bekle**

```bash
git push -u origin feat/coklu-yayinci-kiraci-izolasyonu
gh pr create --title "feat(iys): çok yayıncılı kiracı izolasyonu" --body "$(cat <<'EOF'
## Özet
- `NetgsmAccount` tablosu: lisans → Netgsm kimliği + İYS markası (şifre `IDataProtector` ile şifreli)
- `IysConsentCollector.RecordAsync` artık `licenseId` alır; marka çözülemezse **satır açmaz**, yalnız `no-brand` olayı yazar
- `IysConsentEvent` kiracı sütunları kazandı (`LicenseId`, `BrandCode`, `IysConsentId`)
- `IIysClient` her çağrıda `IysAccountContext` alır — yanlış marka altında sorgu yapısal olarak imkânsız
- Push ve verify işleri marka başına döner; marka başına try/catch + marka başına parti sınırı (hat başı tıkanması giderildi)
- Gönderim kapısı markayı kampanyanın lisansından çözer; global `NetgsmOptions.BrandCode` **silindi**

## Boru hattı hâlâ KAPALI
Bu PR prod'a hiçbir `NetgsmAccount` satırı yazmaz. Doğrulanmış satır olmadan hiçbir iş İYS'ye istek yapmaz.

## Test planı
- [ ] `dotnet test OrderDeck.LicenseServer.Tests` (Docker açık — Testcontainers testleri atlanmamalı)
- [ ] `NetgsmAccountUniqueIndexTests` yeşil (B1 çakışma koruması)
- [ ] `IysConsentTenantIsolationTests` yeşil (iki yayıncı, aynı telefon, iki ayrı satır)
- [ ] `ShopperMeConsentRevokeTests` yeşil (geri çekme bağlı tüm markalara gider)
- [ ] Göç listesinde yalnız iki yeni eklemeli göç var
EOF
)"
```

- [ ] **Step 6: Merge ve prod göçü**

> **Yayın penceresi:** 20:00–01:00 (TR) arası merge YOK — master'a merge
> otomatik prod deploy'u tetikliyor ve o saatler canlı yayın saatleri.

Merge sonrası VPS `.env` dosyasında artık bağlanacak yeri olmayan satırı temizle:

```bash
ssh root@72.62.53.86 "grep -n 'Netgsm__BrandCode' /srv/orderdeck/.env"
```

Satır varsa **elle sil** ve konteyneri yeniden başlat. Zararsızdır — özellik
silindiği için hiçbir yere bağlanmaz — ama duran bir satır ileride "marka
ayarlıydı, neden göndermiyor" sorusunu doğurur. Cevap `NetgsmAccounts`
tablosunda, `.env`'de değil.

Sonra prod'da doğrula:

```bash
ssh root@72.62.53.86 "docker logs orderdeck-license --tail 100 | grep -i 'iys\|netgsm'"
```

Beklenen: `doğrulanmış Netgsm hesabı yok, boru hattı kapalı` benzeri satırlar;
hiçbir İYS HTTP hatası yok.

Tabloların oluştuğunu doğrula:

```sql
SELECT COUNT(*) FROM NetgsmAccounts;          -- 0 olmalı
SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID('NetgsmAccounts');
SELECT TOP 1 LicenseId, BrandCode, IysConsentId FROM IysConsentEvents ORDER BY OccurredAt DESC;
```

`IysConsentEvents` üzerindeki üç yeni sütun eski satırlarda `NULL` — bu
**beklenen**: geriye dönük doldurulamıyorlar, çünkü o olayların hangi kiracıya
ait olduğu bilgisi hiç kaydedilmemişti.

- [ ] **Step 7: Yedek kapsamını doğrula**

`IDataProtection` anahtarları olmadan `NetgsmAccount.PasswordProtected`
çözülemez ve **her yayıncının kimliğini yeniden girmesi gerekir**. Anahtar
klasörünün gecelik yedek zincirinde olduğunu doğrula:

```bash
ssh root@72.62.53.86 "ls -la /srv/orderdeck/keys && crontab -l | grep -i keys"
```

Beklenen: `keys` klasörü dolu ve gecelik `keys` yedeği cron'da mevcut.
(Bu zincir zaten WhatsApp hesapları için kurulmuştu; burada yalnız
**doğrulanıyor** — yoksa bu planın devamı riskli.)

---

## Plan bittiğinde ne doğru olur

| Sorun | Önce | Sonra |
|---|---|---|
| B1 — tekil indeks çakışması | Marka boşken tüm yayıncıların onayı TEK satıra çakışır; B'nin RET'i A'nın ONAY'ını ezer | Marka çözülemezse satır **hiç açılmaz**; yalnız `no-brand` olayı yazılır |
| B2 — push durması | Bir yayıncının bozuk ayarı TÜM markaların push'unu durdurur | Marka başına try/catch; yalnız o markanın turu atlanır |
| İstek/satır marka uyuşmazlığı | İstek global markaya gider, cevap yayıncının satırına yazılır | `IysAccountContext` zorunlu; yanlış marka yapısal olarak imkânsız |
| Hat başı tıkanması | Sınır markadan ÖNCE; hatada randevu ilerlemiyor → B sonsuza dek bekler | Sınır marka başına; geçici hatada randevu 5 dk ileri alınır, takvim tüketilmez |
| Kanıt kiracısız | `IysConsentEvent` yalnız telefon taşır; A'daki ONAY ile B'deki RET ayrışmaz | `LicenseId` + `BrandCode` + `IysConsentId` |
| Gönderim kapısı | Kampanya global markanın onaylarını okur | Kampanya **kendi lisansının** markasını okur |

## Ne hâlâ BLOKE

- **Boru hattını açmak.** Spec §10: `NetgsmAccount` satırı yazmak geri alınamaz
  bir adım. Bu plan ön koşulu karşılar, adımı atmaz.
- **`skipped` durumu ve bakiye bitince `paused`.** Bugün İYS kapısında elenen
  alıcı hâlâ `"failed"` yazılıyor; ayrı bir durum Plan 3'ün işi (kredi iadesi
  gerekçesi kredi sistemiyle birlikte ölüyor).
- **WPF uyumu.** `BulkSmsViewModel.CanSend()` kredi alanına bağlı ve Velopack
  yüzünden sahada eski sürümler kalıyor — Plan 3.
- **Panelden kimlik girişi ve doğrulama kapısı.** Plan 2. O gelene kadar
  `NetgsmAccount` satırı elle açılır.
- **Ayrılan yayıncının verisinin silinmesi.** `IysConsentEvent` ekle-only ve
  FK'siz; "tamamen sildik" demek bugün doğru değil — Plan 4.

---

## Uygulama notları

**TDD sırası değiştirilemez.** Her görevde önce test yazılır, düştüğü görülür,
sonra kod yazılır. Özellikle Faz 1 ve Faz 3'te testin **doğru sebeple**
düştüğünü doğrulamak kritik: `NetgsmAccountUniqueIndexTests` InMemory'de
sahte bir yeşil verir, bu yüzden Testcontainers şart.

**Commit sıklığı:** her görev sonunda bir commit. Görevler sırayla uygulanmalı —
Faz 3 Faz 1'in servisine, Faz 5 Faz 4'ün imzasına bağlı.

**Derleme kırılması beklenen yerler:** Task 6 (arayüz imzası) ve Task 9
(`NetgsmOptions.BrandCode` silinmesi) geniş derleme hatası üretir. İkisinde de
hata listesi **tam olarak planda sayılan dosyalarla** sınırlı olmalı; fazlası
çıkarsa dur ve nedenini not al.
