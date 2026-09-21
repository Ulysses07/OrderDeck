# Onay Toplama Noktası + Ayrılış Yaşam Döngüsü (Plan 4) Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** İYS onayını toplama noktasına bağla (§5.2b), yayıncı ayrılışında veriyi iki kademeli sakla/imha et (§6 + m.13) ve İYS'den dönüş yolunu (ayna içe aktarım + CSV dışa aktarım) kur.

**Architecture:** Onay yalnız kayıt anında (register) yazılır; profil PATCH'inden "aç" reddedilir. Ayrılış `NetgsmAccount.DisabledAt` damgasıyla başlar; günlük Hangfire işi 30. günde `IysConsent`+hesabı siler ve `NetgsmDeparture` takvim kaydı açar, 3. yılda (m.13) dönem ispatını (`IysConsentEvent`) ve kampanya kayıtlarını imha eder. Dönüş yolu: admin CSV dışa aktarımı + `/iys/search` tabanlı `IysMirrorImportJob` (`SourceCode="IYS_MIRROR"`, terminal PushState).

**Tech Stack:** ASP.NET Core 10, EF Core 10 (testlerde InMemory), Hangfire (MemoryStorage testte), xUnit + FluentAssertions.

---

## Kapsam notları (spec: `docs/superpowers/specs/2026-09-18-coklu-yayinci-sms-iys-design.md`)

- **§5.2 (geri çekme → bağlı TÜM markalara RET) ZATEN master'da** — PR #473 ile girdi. Bu planda görev YOK; sadece bilgi.
- Bu plan: **§5.2b** (onay toplama noktasına bağlı; profil PATCH "aç" reddi; reuse dalında onay kaydı) + **§6** (ayrılış yaşam döngüsü, saklama, dönüş yolu).
- Onaylanan saklama kuralları (kullanıcı, 2026-09-21):
  - Ayrılış + 30 gün → `IysConsent` satırları + `NetgsmAccount` satırı silinir.
  - Ayrılış + 3 yıl (6563 m.13) → `IysConsentEvent` (dönem ispatı) + `SmsCampaign`/`SmsCampaignRecipient` imha edilir.
  - Aydınlatma metni İKİ kademeyi de söyler; **asla "tamamen sildik" iddiası yok**.
- Devralınan "yetim onay satırı" maddesi bu planın Görev 6 / Faz 2'siyle KAPSANIR (ayrı iş yok).
- Shopper mobil UI'daki onay kutusu ayrı repo (OrderDeck-Shopper) — kapsam dışı, takip işi.
- `Register`'da atomiklik açığı YOK: `RecordAsync` save etmez, tek `SaveChangesAsync(ct)` (satır ~251) her şeyi birlikte yazar. Bu düzen korunacak.

## Dosya haritası

| Dosya | İş |
|---|---|
| `OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs` | Görev 1 — onay bloğunu iki dal için ortaklaştır |
| `OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs` | Görev 2 — profilden "aç" → 400 |
| `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperAuthRegisterConsentTests.cs` | Görev 1 — YENİ test dosyası |
| `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperMeConsentRevokeTests.cs` | Görev 2 — mevcut testi yeniden yaz + no-op testi |
| `OrderDeck.LicenseServer/Domain/NetgsmAccount.cs` | Görev 3 — `DisabledAt` alanı |
| `OrderDeck.LicenseServer/Domain/NetgsmDeparture.cs` | Görev 3 — YENİ varlık (FK'sız) |
| `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` | Görev 3 — DbSet + konfigürasyon |
| `OrderDeck.LicenseServer/Data/Migrations/*NetgsmDepartureRetention*` | Görev 3 — göç |
| `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs` | Görev 4 — Disabled geçişinde damga |
| `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs` | Görev 4 — "Aç"ta damga temizliği; Görev 5 — Export handler |
| `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml` | Görev 5 — İYS CSV butonu |
| `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs` | Görev 5 — `NetgsmAccountExport` sabiti |
| `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs` | Görev 4+5 — test genişletme |
| `OrderDeck.LicenseServer/Services/Iys/IysDepartureRetentionJob.cs` | Görev 6 — YENİ saklama işi (3 faz) |
| `OrderDeck.LicenseServer/Domain/IysConsentEvent.cs` | Görev 6 — doc düzeltmesi (m.13 istisnası) |
| `OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs` | Görev 6 — YENİ, 6 test |
| `OrderDeck.LicenseServer/Program.cs` | Görev 7 — DI + recurring 04:45 UTC |
| `OrderDeck.LicenseServer/Services/Iys/IysMirrorImportJob.cs` | Görev 8 — YENİ ayna işi |
| `OrderDeck.LicenseServer.Tests/Services/Iys/IysMirrorImportJobTests.cs` | Görev 8 — YENİ, 4 test |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs` | Görev 9 — `POST iys-mirror` |
| `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountControllerTests.cs` | Görev 9 — 3 test |
| `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml` | Görev 10 — KVKK saklama cümleleri |

Test komutu (hepsi InMemory, Docker gerekmez):
`dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~<TestSınıfı>"`

Commit'ler: Türkçe emir kipi + `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`. `tmp/` ASLA stage edilmez.

---

### Görev 1: Kayıtta onay — iki dal için ortak (§5.2b)

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs` (yaratma dalı ~158-190, existingLink ~192-196)
- Test (YENİ): `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperAuthRegisterConsentTests.cs`

Bugün: yaratma dalı onayı yazıyor, **reuse dalı (mevcut shopper ikinci yayıncıya kaydolunca) onayı DÜŞÜRÜYOR**. İYS onayı marka başına — reuse dalında da yazılmalı.

- [ ] **Adım 1: Başarısız testleri yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperAuthRegisterConsentTests.cs` (yeni dosya). Yardımcılar `ShopperMeConsentRevokeTests.cs`'ten uyarlanır (marka kodları ÜRETİLİR — sabit yazma):

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// §5.2b — SMS onayı TOPLAMA NOKTASINDA (kayıt) alınır ve İYS kaydı MARKA
/// başına düşer. Kritik delik: mevcut shopper ikinci yayıncıya kaydolurken
/// (reuse dalı) onay kutusu işaretliyse eski kod hiçbir şey yazmıyordu.
/// </summary>
public sealed class ShopperAuthRegisterConsentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthRegisterConsentTests(ApiFactory factory) => _factory = factory;

    private sealed record RegisterRequest(
        string BroadcasterCode, string FullName, string Phone, string Password,
        string Address, string Platform, string Username,
        string? Email = null, string? Tc = null, bool SmsConsent = false);

    private static string UniquePhone()
        => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
    private static string NewPassword() => $"Pw-{Guid.NewGuid():N}";
    private static string UniqueCode() => ("rc" + Guid.NewGuid().ToString("N"))[..16];
    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();
    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();

    private async Task<(Guid LicenseId, string ShopperCode, string BrandCode)>
        SeedBroadcasterAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Yayinci",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        var code = UniqueCode();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-{Guid.NewGuid():N}"[..24],
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });

        var brandCode = NewBrandCode();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = NewUserCode(),
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return (licenseId, code, brandCode);
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string code, string phone, string password, bool consent)
        => client.PostAsJsonAsync("/api/v1/shopper/auth/register", new RegisterRequest(
            code, "Test Alici", phone, password, "Adres 1", "youtube",
            $"u{Guid.NewGuid():N}"[..12], SmsConsent: consent));

    [Fact]
    public async Task Yeni_shopper_kaydinda_onay_ve_ispat_alanlari_yazilir()
    {
        var (_, code, brand) = await SeedBroadcasterAsync();
        var client = _factory.CreateClient();
        var phone = UniquePhone();

        var resp = await RegisterAsync(client, code, phone, NewPassword(), consent: true);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var consentRow = await db.IysConsents.AsNoTracking()
            .SingleAsync(c => c.BrandCode == brand && c.Recipient == phone);
        consentRow.Status.Should().Be(IysConsentStatus.Onay);

        var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Phone == phone);
        shopper.SmsConsent.Should().BeTrue();
        shopper.SmsConsentSource.Should().Be("register");
    }

    [Fact]
    public async Task Mevcut_shopper_ikinci_yayinciya_kaydolunca_onay_o_markaya_da_yazilir()
    {
        var (_, codeA, _) = await SeedBroadcasterAsync();
        var (_, codeB, brandB) = await SeedBroadcasterAsync();
        var client = _factory.CreateClient();
        var phone = UniquePhone();
        var password = NewPassword();

        (await RegisterAsync(client, codeA, phone, password, consent: false))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        // Reuse dalı: aynı telefon + aynı parola, ikinci yayıncı, onay işaretli.
        (await RegisterAsync(client, codeB, phone, password, consent: true))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var consentRow = await db.IysConsents.AsNoTracking()
            .SingleAsync(c => c.BrandCode == brandB && c.Recipient == phone);
        consentRow.Status.Should().Be(IysConsentStatus.Onay);

        var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Phone == phone);
        shopper.SmsConsent.Should().BeTrue();
        shopper.SmsConsentSource.Should().Be("register");
    }

    [Fact]
    public async Task Onay_kutusu_isaretsizse_yeniden_kayitta_onay_yazilmaz()
    {
        var (_, codeA, brandA) = await SeedBroadcasterAsync();
        var (_, codeB, brandB) = await SeedBroadcasterAsync();
        var client = _factory.CreateClient();
        var phone = UniquePhone();
        var password = NewPassword();

        (await RegisterAsync(client, codeA, phone, password, consent: false))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await RegisterAsync(client, codeB, phone, password, consent: false))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.IysConsents.AsNoTracking()
                .Where(c => (c.BrandCode == brandA || c.BrandCode == brandB)
                            && c.Recipient == phone)
                .AnyAsync())
            .Should().BeFalse("sessizlik ret değildir ama onay hiç verilmedi");
    }
}
```

- [ ] **Adım 2: Testleri koştur — 2. test KIRMIZI olmalı**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperAuthRegisterConsentTests"`
Beklenen: `Mevcut_shopper_ikinci_yayinciya...` FAIL (İYS satırı yok — reuse dalı onayı düşürüyor). 1. ve 3. test geçebilir.

- [ ] **Adım 3: ShopperAuthController'ı düzelt**

`ShopperAuthController.cs` yaratma dalında (satır ~158-190):
1. Shopper nesnesi başlatıcısından `SmsConsent = req.SmsConsent`, `SmsConsentAt = ...`, `SmsConsentSource = ...` satırlarını SİL (kalan alanlar: Id/FullName/Phone/PasswordHash/Address/Email/Tc/CreatedAt/UpdatedAt).
2. Yaratma dalındaki `if (req.SmsConsent) { await _iys.RecordAsync(...); }` bloğunu SİL.
3. existingLink 409 kontrolünden SONRA (satır ~196'nın altına, "7." adım yorumundan önce) ortak bloğu ekle:

```csharp
// 6a. Kayıt anındaki SMS onayı — HER İKİ dal için (§5.2b): İYS onayı MARKA
// başına tutulur; kişi mevcut bir shopper olsa ve boolean zaten true olsa
// bile BU yayıncının markasına ayrıca kayıt düşülmeli. İşaretsizse çağrılmaz
// (sessizlik ret değil).
if (req.SmsConsent)
{
    var consentAt = DateTimeOffset.UtcNow;
    if (!shopper.SmsConsent)
    {
        shopper.SmsConsent = true;
        shopper.SmsConsentAt = consentAt;
        shopper.SmsConsentSource = "register";
        shopper.UpdatedAt = consentAt;
    }
    await _iys.RecordAsync(
        license.Id, shopper.Phone, consented: true, occurredAt: consentAt,
        sourceTable: "Shopper", sourceId: shopper.Id,
        ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
        userAgent: Request.Headers.UserAgent.ToString(), ct: ct);
}
```

Tek `SaveChangesAsync(ct)` düzenine DOKUNMA — `RecordAsync` save etmez, her şey birlikte yazılır.

- [ ] **Adım 4: Testleri koştur — hepsi YEŞİL**

Çalıştır: aynı filter komutu. Beklenen: 3/3 PASS.

- [ ] **Adım 5: Regresyon — mevcut shopper auth testleri**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Shopper"`
Beklenen: tümü PASS (Görev 2 henüz yapılmadığı için `ShopperMeConsentRevokeTests` de mevcut haliyle geçer).

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperAuthRegisterConsentTests.cs
git commit -m "feat(iys): kayıtta SMS onayı iki dal için ortak — reuse dalı markaya onay düşürür (§5.2b)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 2: Profilden "aç" reddi (§5.2b)

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs` (else-if ~169-175)
- Değiştir: `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperMeConsentRevokeTests.cs`

Bugün PATCH `/api/v1/shopper/me` ile `SmsConsent:true` bayrağı açıyor (`SmsConsentSource="profile"`, İYS'ye yazmadan). §5.2b: onay yalnız toplama noktasında — profilden AÇILAMAZ (kapatma/geri çekme davranışı korunur).

- [ ] **Adım 1: Mevcut testi yeniden yaz + no-op testi ekle (kırmızı)**

`ShopperMeConsentRevokeTests.cs` içindeki `Profilden_onay_ISYS_e_yazilmaz` testini SİL ve yerine şu ikisini yaz (dosyanın mevcut yardımcılarını kullan — `SeedBroadcasterAsync`, `LinkAsync`, `RegisterRequest`, `PatchMeRequest`, `AuthResponse`):

```csharp
[Fact]
public async Task Profilden_onay_verilemez_400()
{
    var (licenseId, code) = await SeedBroadcasterAsync(BrandA);
    var client = _factory.CreateClient();
    var phone = UniquePhone();

    // Onay İŞARETSİZ kayıt.
    var reg = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
        new RegisterRequest(code, "Test Alici", phone, NewPassword(),
            "Adres 1", "youtube", "revokeuser", SmsConsent: false));
    reg.EnsureSuccessStatusCode();
    var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;

    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
    var patch = await client.PatchAsJsonAsync("/api/v1/shopper/me",
        new PatchMeRequest(SmsConsent: true));

    // §5.2b — onay toplama noktasında alınır; profilden açma REDDEDİLİR.
    patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);

    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
    (await db.IysConsents.AsNoTracking().AnyAsync(c => c.Recipient == phone))
        .Should().BeFalse("reddedilen istek İYS'ye iz bırakmamalı");
    var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Id == auth.ShopperId);
    shopper.SmsConsent.Should().BeFalse();
    shopper.SmsConsentSource.Should().BeNull();
}

[Fact]
public async Task Onay_zaten_acikken_true_gondermek_no_op()
{
    var (licenseId, code) = await SeedBroadcasterAsync(BrandA);
    var client = _factory.CreateClient();
    var phone = UniquePhone();

    // Onay İŞARETLİ kayıt — meşru toplama noktası.
    var reg = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
        new RegisterRequest(code, "Test Alici", phone, NewPassword(),
            "Adres 1", "youtube", "revokeuser", SmsConsent: true));
    reg.EnsureSuccessStatusCode();
    var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;

    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
    var patch = await client.PatchAsJsonAsync("/api/v1/shopper/me",
        new PatchMeRequest(SmsConsent: true));

    // true→true idempotent — istemciyi kırmamak için 200.
    patch.StatusCode.Should().Be(HttpStatusCode.OK);

    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
    var rows = await db.IysConsents.AsNoTracking()
        .Where(c => c.BrandCode == BrandA && c.Recipient == phone).ToListAsync();
    rows.Should().HaveCount(1, "no-op yeni İYS kaydı üretmemeli");
    rows[0].Status.Should().Be(IysConsentStatus.Onay);
    var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Id == auth.ShopperId);
    shopper.SmsConsentSource.Should().Be("register", "profil kaynağa dokunmamalı");
}
```

- [ ] **Adım 2: Koştur — KIRMIZI**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperMeConsentRevokeTests"`
Beklenen: `Profilden_onay_verilemez_400` FAIL (bugün 200 dönüyor ve bayrağı açıyor). No-op testi de bugünkü kodda `SmsConsentSource`'u "profile"a EZMEDİĞİ sürece davranışa göre değerlendir — mevcut kod `!shopper.SmsConsent` koşullu olduğundan no-op testi geçebilir; kritik kırmızı ilk test.

- [ ] **Adım 3: ShopperMeController'ı düzelt**

`ShopperMeController.cs` ~169-175'teki else-if'i (bayrak açan dal) şununla değiştir:

```csharp
else if (req.SmsConsent is true && !shopper.SmsConsent)
{
    // §5.2b — onay profilden VERİLEMEZ; yalnız toplama noktasında alınır.
    // true→true no-op koşula takılmaz: idempotent isteği reddetmek istemcileri kırar.
    return Problem(
        title: "sms-consent-profile-enable-rejected",
        detail: "SMS izni profilden açılamaz; izin yalnız kayıt sırasında "
              + "veya yayıncının kayıt formundan verilebilir.",
        statusCode: 400);
}
```

Geri çekme (false) dalına ve devamındaki `UpdatedAt` + SaveChanges/`ShopperSaveConflict.DeletedWonAsync` düzenine DOKUNMA.

- [ ] **Adım 4: Koştur — YEŞİL**

Çalıştır: aynı filter. Beklenen: sınıftaki TÜM testler PASS (geri çekme testleri dahil).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperMeConsentRevokeTests.cs
git commit -m "feat(iys): profilden SMS onayı açma reddedilir — onay yalnız toplama noktasında (§5.2b)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 3: `DisabledAt` + `NetgsmDeparture` varlığı + göç

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Domain/NetgsmAccount.cs`
- Oluştur: `OrderDeck.LicenseServer/Domain/NetgsmDeparture.cs`
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (DbSet ~51 civarı; konfig ~878 sonrası)
- Oluştur: göç `NetgsmDepartureRetention`
- Test (YENİ): `OrderDeck.LicenseServer.Tests/Data/NetgsmDepartureModelTests.cs`

- [ ] **Adım 1: Model testini yaz (kırmızı — derlenmez)**

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>NetgsmDeparture BİLİNÇLİ FK'sız (IysConsentEvent'teki kararın
/// aynısı): lisans KVKK ile silinse bile 3 yıllık imha randevusu yaşamalı.
/// Birisi "iyileştirme" diye navigasyon eklerse bu test kırılır.</summary>
public sealed class NetgsmDepartureModelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public NetgsmDepartureModelTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void NetgsmDeparture_hicbir_foreign_key_tasimaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var entityType = db.Model.FindEntityType(typeof(NetgsmDeparture))!;
        entityType.GetForeignKeys().Should().BeEmpty(
            "lisans silinse bile imha randevusu yaşamalı — FK cascade bunu bozar");
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~NetgsmDepartureModelTests"`
Beklenen: FAIL — `NetgsmDeparture` tipi yok.

- [ ] **Adım 3: Varlıkları yaz**

`OrderDeck.LicenseServer/Domain/NetgsmDeparture.cs` (yeni dosya):

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>Ayrılan yayıncının saklama takvimi (§6 + 6563 m.13). Kişisel veri
/// İÇERMEZ — telefon/isim yok, yalnız marka ve tarihler.
/// LicenseId FK DEĞİL ve navigasyon eklemeyin (IysConsentEvent'teki kararın
/// aynısı): lisans KVKK ile silinse bile 3 yıllık imha randevusu yaşamalı.</summary>
public sealed class NetgsmDeparture
{
    public Guid Id { get; set; }

    /// <summary>FK'sız referans; Faz 3'te kampanya imhası için. Lisans
    /// silinmişse null kalabilir — o durumda yalnız olay imhası yapılır.</summary>
    public Guid? LicenseId { get; set; }

    public string BrandCode { get; set; } = "";

    /// <summary>Ayrılış anı = hesabın Disabled'a geçtiği an (DisabledAt).
    /// m.13 3 yıl BU tarihten sayılır.</summary>
    public DateTimeOffset DepartedAt { get; set; }

    /// <summary>30. gün temizliğinin gerçekleştiği an (IysConsent + hesap silindi).</summary>
    public DateTimeOffset ConsentsDeletedAt { get; set; }

    /// <summary>3 yıllık imha tamamlandığında damgalanır; null = imha bekliyor.</summary>
    public DateTimeOffset? PurgedAt { get; set; }
}
```

`NetgsmAccount.cs` — `LastVerifiedAt`'ten sonra ekle:

```csharp
/// <summary>Hesabın Disabled durumuna GEÇTİĞİ an. Saklama işinin saati:
/// ayrılıştan 30 gün sonra IysConsent + hesap satırı silinir (§6).
/// Yalnız geçişte damgalanır; admin "Aç" geri aldığında temizlenir.</summary>
public DateTimeOffset? DisabledAt { get; set; }
```

Aynı dosyadaki "Satırın iki ayrı çıkış yolu var" doc yorumunu (satır ~38-47) ÜÇ yola çıkar: ilk maddedeki "satır SİLİNMEZ" ifadesini güncelle — "Disabled satır 30 gün sonra `IysDepartureRetentionJob` tarafından silinir ve `NetgsmDeparture` takvim kaydı açılır".

- [ ] **Adım 4: DbContext'e ekle**

`LicenseDbContext.cs` — `NetgsmAccounts` DbSet'inin (satır ~51) hemen altına:

```csharp
public DbSet<NetgsmDeparture> NetgsmDepartures => Set<NetgsmDeparture>();
```

NetgsmAccount konfigürasyonunun bittiği yerin (satır ~878) sonrasına:

```csharp
mb.Entity<NetgsmDeparture>(b =>
{
    b.HasKey(d => d.Id);
    // LicenseId bilinçli FK'sız — IysConsentEvent'teki kararın aynısı: lisans
    // KVKK ile silinse bile 3 yıllık imha randevusu yaşamalı.
    b.Property(d => d.BrandCode).HasMaxLength(16).IsRequired();
    b.HasIndex(d => new { d.PurgedAt, d.DepartedAt });
    b.HasIndex(d => d.BrandCode);
});
```

- [ ] **Adım 5: Koştur — YEŞİL**

Çalıştır: aynı filter. Beklenen: PASS.

- [ ] **Adım 6: Göçü üret**

Çalıştır (repo kökünden):
```bash
dotnet ef migrations add NetgsmDepartureRetention --project OrderDeck.LicenseServer -o Data/Migrations
```
Beklenen: yeni göç hem `NetgsmAccounts.DisabledAt` sütununu hem `NetgsmDepartures` tablosunu içerir (en son mevcut göç `20260921014653_RetireSmsCredits`). Göç dosyasını AÇ ve doğrula: `AddColumn<DateTimeOffset>(name: "DisabledAt", nullable: true)` + `CreateTable(name: "NetgsmDepartures", ...)` ve `ForeignKey` YOK.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/NetgsmAccount.cs OrderDeck.LicenseServer/Domain/NetgsmDeparture.cs OrderDeck.LicenseServer/Data/LicenseDbContext.cs OrderDeck.LicenseServer/Data/Migrations/ OrderDeck.LicenseServer.Tests/Data/NetgsmDepartureModelTests.cs
git commit -m "feat(iys): DisabledAt damgası + FK'sız NetgsmDeparture saklama takvimi (§6)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 4: Disabled geçişinde damga, "Aç"ta temizlik

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs` (`CloseAccountAndPauseCampaignsAsync`, ~satır 340 öncesi)
- Değiştir: `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs` (`OnPostEnableAsync`, ~satır 150)
- Değiştir: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs`

- [ ] **Adım 1: Testleri genişlet (kırmızı)**

`AdminNetgsmPageTests.cs`:
1. `SeedAccountAsync` yardımcısına başlatıcı içinde ekle:
```csharp
DisabledAt = status == NetgsmAccountStatus.Disabled ? DateTimeOffset.UtcNow : null,
```
2. `Kapatma_hesabi_disabled_yapar_ve_kampanyalari_duraklatir` testinin hesap assert'lerine ekle:
```csharp
acc.DisabledAt.Should().NotBeNull("saklama saati ayrılış anından sayılır (§6)");
```
3. `Acma_Verified_degil_Failed_yazar` testinin hesap assert'lerine ekle:
```csharp
acc.DisabledAt.Should().BeNull("admin geri açınca 30 günlük sayaç iptal olur");
```

- [ ] **Adım 2: Koştur — KIRMIZI**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~AdminNetgsmPageTests"`
Beklenen: iki test FAIL (damga yazılmıyor/temizlenmiyor).

- [ ] **Adım 3: Damgayı yaz**

`NetgsmAccountService.cs` — `CloseAccountAndPauseCampaignsAsync` içinde, Failed bayatlık kapısından (satır ~335-338) sonra, `account.Status = status;` satırından ÖNCE:

```csharp
if (status == NetgsmAccountStatus.Disabled
    && account.Status != NetgsmAccountStatus.Disabled)
{
    // Saklama saati ayrılış ANINDAN sayılır (§6: 30 gün). Yalnız GEÇİŞTE
    // damgala: zaten Disabled hesabı tekrar kapatmak saati ilerletirdi.
    account.DisabledAt = DateTimeOffset.UtcNow;
}
```

`Index.cshtml.cs` — `OnPostEnableAsync` içinde `acc.LastError = null;` satırının (satır ~150) hemen altına:

```csharp
acc.DisabledAt = null;
```

`UpdatedAt`'i ELLE damgalama — `StampNetgsmAccountVersions` (LicenseDbContext:116) merkezî damgalıyor.

- [ ] **Adım 4: Koştur — YEŞİL**

Çalıştır: aynı filter. Beklenen: tüm sınıf PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs
git commit -m "feat(iys): Disabled geçişinde DisabledAt damgası, admin açınca sayaç iptali

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 5: Admin İYS CSV dışa aktarımı (§6 dönüş yolunun taşıyıcısı)

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs` (~satır 31 sonrası)
- Değiştir: `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs` (yeni handler)
- Değiştir: `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml` (buton)
- Değiştir: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs`

İYS bir markanın onay listesini geri VERMEZ; `/iys/search` numara listesi ister. Ayrılan yayıncıya verilecek tek eksiksiz kopya bizdeki tablodur → admin CSV.

- [ ] **Adım 1: Testi yaz (kırmızı)**

`AdminNetgsmPageTests.cs`'e ekle (mevcut `PostAsync(client, handler, accountId)` + `SeedAccountAsync` + `CreateLoggedInAdminClientAsync` yardımcılarını kullan):

```csharp
[Fact]
public async Task Export_yalniz_kendi_markasinin_onaylarini_dondurur()
{
    // SeedAccountAsync (Guid AccountId, Guid LicenseId) döndürür.
    var (accId, _) = await SeedAccountAsync(NetgsmAccountStatus.Verified);
    var (otherAccId, _) = await SeedAccountAsync(NetgsmAccountStatus.Verified);

    string ownBrand, otherBrand;
    var ownPhone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
    var otherPhone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        ownBrand = (await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accId)).BrandCode;
        otherBrand = (await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == otherAccId)).BrandCode;
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.AddRange(
            new IysConsent
            {
                Id = Guid.NewGuid(), BrandCode = ownBrand, ChannelType = "MESAJ",
                RecipientType = "BIREYSEL", Recipient = ownPhone,
                Status = IysConsentStatus.Onay, PushState = IysPushState.Confirmed,
                LastLocalEventAt = now, CreatedAt = now, UpdatedAt = now,
            },
            new IysConsent
            {
                Id = Guid.NewGuid(), BrandCode = otherBrand, ChannelType = "MESAJ",
                RecipientType = "BIREYSEL", Recipient = otherPhone,
                Status = IysConsentStatus.Onay, PushState = IysPushState.Confirmed,
                LastLocalEventAt = now, CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();
    }

    var client = await CreateLoggedInAdminClientAsync();
    var resp = await PostAsync(client, "Export", accId);

    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    resp.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
    var body = await resp.Content.ReadAsStringAsync();
    body.Should().Contain(ownPhone, "kendi markasının onayı listede olmalı");
    body.Should().NotContain(otherPhone, "başka markanın verisi SIZMAMALI");

    using var verify = _factory.Services.CreateScope();
    var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
    (await vdb.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EventType == AuditEvents.NetgsmAccountExport
                             && a.TargetId == accId.ToString()))
        .Should().Be(1, "dışa aktarım denetim izine düşmeli");
}
```

- [ ] **Adım 2: Koştur — KIRMIZI**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~AdminNetgsmPageTests.Export_yalniz"`
Beklenen: FAIL — `AuditEvents.NetgsmAccountExport` yok (derleme hatası).

- [ ] **Adım 3: Sabiti + handler'ı + butonu yaz**

`AuditEvents.cs` — `NetgsmAccountEnable` sabitinin (satır ~31) altına:

```csharp
// §6 — ayrılışta İYS onay listesi dışa aktarımı. Dönüş yolunun taşıyıcısı:
// İYS bir markanın onay listesini vermez, /iys/search numara listesi ister.
public const string NetgsmAccountExport = "netgsm.account.export";
```

`Index.cshtml.cs` — mevcut handler'ların yanına yeni handler:

```csharp
public async Task<IActionResult> OnPostExportAsync(CancellationToken ct)
{
    var acc = await _db.NetgsmAccounts.AsNoTracking()
        .FirstOrDefaultAsync(a => a.Id == AccountId, ct);
    if (acc is null) return NotFound();

    var rows = await _db.IysConsents.AsNoTracking()
        .Where(c => c.BrandCode == acc.BrandCode)
        .OrderBy(c => c.Recipient).ToListAsync(ct);

    // CSV enjeksiyonuna kapalı: alanlar E.164 / enum / ISO-8601 biçimli,
    // serbest metin sütunu yok.
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Recipient;Status;LastVerifiedStatus;ConsentDate;SourceCode;LastVerifiedAt");
    foreach (var c in rows)
        sb.AppendLine(string.Join(';',
            c.Recipient, c.Status, c.LastVerifiedStatus?.ToString() ?? "",
            c.ConsentDate?.ToString("O") ?? "", c.SourceCode ?? "",
            c.LastVerifiedAt?.ToString("O") ?? ""));

    await _audit.LogAsync(AuditEvents.NetgsmAccountExport, AuditTargets.NetgsmAccount,
        AccountId.ToString(), new { acc.LicenseId, acc.BrandCode, rowCount = rows.Count }, ct);

    return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
        $"iys-consents-{acc.BrandCode}.csv");
}
```

`Index.cshtml` — satır başına düşen `<form method="post">` içinde, mevcut if/else (Aç/Kapat butonları) bloğunun DIŞINA, form kapanmadan önce:

```html
<button class="btn btn-sm btn-outline-secondary" asp-page-handler="Export">İYS CSV</button>
```

- [ ] **Adım 4: Koştur — YEŞİL**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~AdminNetgsmPageTests"`
Beklenen: tüm sınıf PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs
git commit -m "feat(iys): ayrılan yayıncı için admin İYS onay CSV dışa aktarımı + denetim izi (§6)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 6: `IysDepartureRetentionJob` — 3 fazlı saklama işi

**Dosyalar:**
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IysDepartureRetentionJob.cs`
- Değiştir: `OrderDeck.LicenseServer/Domain/IysConsentEvent.cs` (satır ~16 doc)
- Test (YENİ): `OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs`

Fazlar: (1) 30 günü dolan Disabled hesap → marka başka canlı hesapta yaşamıyorsa `IysConsent` sil + `NetgsmDeparture` aç; hesap satırı her durumda silinir. (2) Hesabı hiç kalmamış yetim markaların onayları → sil + takvim kaydı (devralınan yetim-onay maddesini kapatır). (3) 3 yılı (m.13) dolan ayrılışlar → dönem ispatı (`OccurredAt ≤ DepartedAt` olayları) + kampanya kayıtları imha, `PurgedAt` damgası.

- [ ] **Adım 1: Testleri yaz (kırmızı — derlenmez)**

`IysDepartureRetentionJobTests.cs` (yeni dosya). ApiFactory paylaşımlı DB — iş TÜM tabloyu tarar, diğer testlerin artığına dokunabilir; bu yüzden **her assert kendi marka/id'siyle sınırlı** (xunit sınıf içinde sıralı koşar, güvenli):

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// §6 + m.13 saklama takvimi. Paylaşımlı ApiFactory DB'sinde iş bütün
/// tabloyu tarar — assert'ler KENDİ marka/id'leriyle sınırlı tutulmalı.
/// </summary>
public sealed class IysDepartureRetentionJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IysDepartureRetentionJobTests(ApiFactory factory) => _factory = factory;

    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();
    private static string NewPhone()
        => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task<(Guid LicenseId, Guid AccountId, string BrandCode)>
        SeedDepartedAsync(LicenseDbContext db, NetgsmAccountService accounts,
            DateTimeOffset disabledAt)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Ayrilan",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-{Guid.NewGuid():N}"[..24],
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        });
        var brandCode = NewBrandCode();
        var accountId = Guid.NewGuid();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = accountId,
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}"[..32],
            PasswordProtected = accounts.ProtectPassword($"p-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Disabled,
            DisabledAt = disabledAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, accountId, brandCode);
    }

    private static void AddConsent(LicenseDbContext db, string brand, string phone)
    {
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(), BrandCode = brand, ChannelType = "MESAJ",
            RecipientType = "BIREYSEL", Recipient = phone,
            Status = IysConsentStatus.Onay, PushState = IysPushState.Confirmed,
            LastLocalEventAt = now, CreatedAt = now, UpdatedAt = now,
        });
    }

    [Fact]
    public async Task Otuz_gun_dolunca_onaylar_ve_hesap_silinir_ayrilis_kaydi_acilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var disabledAt = DateTimeOffset.UtcNow.AddDays(-31);
        var (licenseId, accountId, brand) = await SeedDepartedAsync(db, accounts, disabledAt);
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeFalse();
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == accountId)).Should().BeFalse();
        var dep = await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand);
        dep.LicenseId.Should().Be(licenseId);
        dep.DepartedAt.Should().BeCloseTo(disabledAt, TimeSpan.FromSeconds(1),
            "m.13 3 yıl ayrılış ANINDAN sayılır, silme anından değil");
        dep.PurgedAt.Should().BeNull();
    }

    [Fact]
    public async Task Otuz_gun_dolmadan_hicbir_sey_silinmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (_, accountId, brand) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow.AddDays(-29));
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeTrue();
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == accountId)).Should().BeTrue();
        (await vdb.NetgsmDepartures.AnyAsync(d => d.BrandCode == brand)).Should().BeFalse();
    }

    [Fact]
    public async Task Marka_baska_canli_hesapta_yasiyorsa_onaylar_kalir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (_, accountId, brand) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow.AddDays(-31));
        // Aynı markayı taşıyan CANLI ikinci hesap (başka lisans).
        var (_, aliveAccountId, _) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow); // seed Disabled açar, aşağıda düzeltilir
        var alive = await db.NetgsmAccounts.SingleAsync(a => a.Id == aliveAccountId);
        alive.BrandCode = brand;
        alive.Status = NetgsmAccountStatus.Verified;
        alive.DisabledAt = null;
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand))
            .Should().BeTrue("marka canlı hesapta yaşıyor — onaylar ONUN");
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == accountId))
            .Should().BeFalse("ayrılan hesabın satırı yine silinir");
        (await vdb.NetgsmDepartures.AnyAsync(d => d.BrandCode == brand))
            .Should().BeFalse("marka ölmedi — imha takvimi açılmaz");
    }

    [Fact]
    public async Task Yetim_marka_onaylari_silinir_ve_ayrilis_kaydi_acilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var brand = NewBrandCode();
        var licenseId = Guid.NewGuid();
        var phone = NewPhone();
        AddConsent(db, brand, phone);
        // LicenseId geri kazanımı için olay izi (hesap yok, olay var).
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            Recipient = phone, OccurredAt = DateTimeOffset.UtcNow.AddDays(-5),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeFalse();
        var dep = await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand);
        dep.LicenseId.Should().Be(licenseId, "olay izinden geri kazanılır");
        dep.PurgedAt.Should().BeNull();
    }

    [Fact]
    public async Task Uc_yil_dolunca_donem_ispati_ve_kampanyalar_imha_edilir_yeni_donem_yasar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var departedAt = DateTimeOffset.UtcNow - (IysDepartureRetentionJob.ProofRetention
            + TimeSpan.FromDays(1));
        var (licenseId, accountId, brand) = await SeedDepartedAsync(db, accounts, departedAt);
        // 30-gün fazı bu hesabı işlemesin: hesabı elle kaldırıp takvim kaydını
        // doğrudan açıyoruz (Faz 3'ü tek başına test etmek için).
        db.NetgsmAccounts.Remove(await db.NetgsmAccounts.SingleAsync(a => a.Id == accountId));
        db.NetgsmDepartures.Add(new NetgsmDeparture
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            DepartedAt = departedAt,
            ConsentsDeletedAt = departedAt.AddDays(30),
        });
        var phone = NewPhone();
        // Dönem İÇİ olay (ayrılıştan önce) → imha edilecek.
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            Recipient = phone, OccurredAt = departedAt.AddDays(-10),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        // Dönem DIŞI olay (ayrılıştan sonra — markayı devralan yeni dönem) → yaşar.
        var newEraEventId = Guid.NewGuid();
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = newEraEventId, LicenseId = Guid.NewGuid(), BrandCode = brand,
            Recipient = NewPhone(), OccurredAt = departedAt.AddDays(10),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        // Dönem içi kampanya + alıcısı → imha edilecek.
        var campaignId = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = campaignId, LicenseId = licenseId, MessageBody = "Eski donem",
            Status = "sent", SegmentsPerMessage = 1, RecipientCount = 1,
            CreatedAt = departedAt.AddDays(-20),
        });
        db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Phone = phone, Status = "sent",
        });
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsentEvents.AnyAsync(
                e => e.BrandCode == brand && e.OccurredAt <= departedAt))
            .Should().BeFalse("dönem ispatı m.13 süresi dolunca imha edilir");
        (await vdb.IysConsentEvents.AnyAsync(e => e.Id == newEraEventId))
            .Should().BeTrue("yeni dönemin ispatı ESKİ ayrılışın imhasına karışmaz");
        (await vdb.SmsCampaigns.AnyAsync(c => c.Id == campaignId)).Should().BeFalse();
        (await vdb.SmsCampaignRecipients.AnyAsync(r => r.CampaignId == campaignId))
            .Should().BeFalse();
        (await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand))
            .PurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Uc_yil_dolmadan_ispat_yasar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var brand = NewBrandCode();
        var licenseId = Guid.NewGuid();
        var departedAt = DateTimeOffset.UtcNow.AddYears(-2);
        db.NetgsmDepartures.Add(new NetgsmDeparture
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            DepartedAt = departedAt, ConsentsDeletedAt = departedAt.AddDays(30),
        });
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            Recipient = NewPhone(), OccurredAt = departedAt.AddDays(-1),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsentEvents.AnyAsync(e => e.BrandCode == brand))
            .Should().BeTrue("m.13: 3 yıl dolmadan ispat İMHA EDİLEMEZ");
        (await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand))
            .PurgedAt.Should().BeNull();
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~IysDepartureRetentionJobTests"`
Beklenen: FAIL — `IysDepartureRetentionJob` yok.

- [ ] **Adım 3: İşi yaz**

`OrderDeck.LicenseServer/Services/Iys/IysDepartureRetentionJob.cs` (yeni dosya):

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Ayrılış saklama takvimi (§6 + 6563 m.13), günde bir koşar:
///
/// Faz 1 — 30 günü dolan Disabled hesaplar: marka başka canlı hesapta
/// yaşamıyorsa IysConsent satırları silinir ve NetgsmDeparture takvim kaydı
/// açılır; hesap satırı her durumda silinir.
/// Faz 2 — hesabı hiç kalmamış YETİM markaların onayları: silinir + takvim
/// kaydı (LicenseId olay izinden best-effort geri kazanılır).
/// Faz 3 — ayrılış + 3 yılı dolanlar: DÖNEM ispatı (OccurredAt ≤ DepartedAt
/// olayları) ve o lisansın dönem kampanyaları imha edilir, PurgedAt damgalanır.
/// Aydınlatma metni iki kademeyi de söyler; "tamamen sildik" İDDİA EDİLMEZ.
/// </summary>
public sealed class IysDepartureRetentionJob
{
    internal static readonly TimeSpan ConsentRetention = TimeSpan.FromDays(30);

    // m.13: 3 yıl. FromDays(3*365)=1095 gün artık yıllarda 3 takvim yılının
    // GERİSİNDE kalabilir; +2 gün pay süreyi uzatır, asla kısaltmaz.
    internal static readonly TimeSpan ProofRetention = TimeSpan.FromDays(3 * 365 + 2);

    private readonly LicenseDbContext _db;
    private readonly ILogger<IysDepartureRetentionJob> _log;

    public IysDepartureRetentionJob(LicenseDbContext db,
        ILogger<IysDepartureRetentionJob> log)
    {
        _db = db;
        _log = log;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await DeleteDepartedConsentsAsync(now, ct);
        await DeleteOrphanConsentsAsync(now, ct);
        await PurgeExpiredProofAsync(now, ct);
    }

    /// <summary>Faz 1 — 30 günü dolan Disabled hesaplar.</summary>
    private async Task DeleteDepartedConsentsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var threshold = now - ConsentRetention;
        var ids = await _db.NetgsmAccounts.AsNoTracking()
            .Where(a => a.Status == NetgsmAccountStatus.Disabled
                        && a.DisabledAt != null && a.DisabledAt <= threshold)
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            try
            {
                // Her yinelemede taze oku: önceki yinelemenin hata temizliği
                // (ChangeTracker.Clear) izlenen nesneleri koparmış olabilir.
                var acc = await _db.NetgsmAccounts
                    .FirstOrDefaultAsync(a => a.Id == id, ct);
                if (acc is null || acc.Status != NetgsmAccountStatus.Disabled
                    || acc.DisabledAt is null)
                    continue; // admin bu arada geri açtı — sayaç iptal.

                // Marka canlı başka hesapta yaşıyor mu? (BrandCode'un tekil
                // indeksi yalnız Verified'ı kapsar — aynı marka Disabled
                // kopyalarda da durabilir.)
                var brandAlive = await _db.NetgsmAccounts.AnyAsync(
                    b => b.Id != acc.Id && b.BrandCode == acc.BrandCode
                         && b.Status != NetgsmAccountStatus.Disabled, ct);

                if (!brandAlive)
                {
                    var consents = await _db.IysConsents
                        .Where(c => c.BrandCode == acc.BrandCode).ToListAsync(ct);
                    _db.IysConsents.RemoveRange(consents);
                    _db.NetgsmDepartures.Add(new NetgsmDeparture
                    {
                        Id = Guid.NewGuid(),
                        LicenseId = acc.LicenseId,
                        BrandCode = acc.BrandCode,
                        DepartedAt = acc.DisabledAt.Value,
                        ConsentsDeletedAt = now,
                    });
                }

                _db.NetgsmAccounts.Remove(acc);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Ayrılış temizliği başarısız (hesap {AccountId})", id);
            }
        }
    }

    /// <summary>Faz 2 — hesabı hiç kalmamış yetim markalar.</summary>
    private async Task DeleteOrphanConsentsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var orphanBrands = await _db.IysConsents.AsNoTracking()
            .Select(c => c.BrandCode).Distinct()
            .Where(bc => !_db.NetgsmAccounts.Any(a => a.BrandCode == bc))
            .ToListAsync(ct);

        foreach (var brand in orphanBrands)
        {
            try
            {
                // LicenseId best-effort: hesap yok ama olay izi kalmış olabilir.
                var licenseId = await _db.IysConsentEvents.AsNoTracking()
                    .Where(e => e.BrandCode == brand && e.LicenseId != null)
                    .OrderByDescending(e => e.OccurredAt)
                    .Select(e => e.LicenseId)
                    .FirstOrDefaultAsync(ct);

                var hasSchedule = await _db.NetgsmDepartures
                    .AnyAsync(d => d.BrandCode == brand && d.PurgedAt == null, ct);
                if (!hasSchedule)
                {
                    _db.NetgsmDepartures.Add(new NetgsmDeparture
                    {
                        Id = Guid.NewGuid(),
                        LicenseId = licenseId,
                        BrandCode = brand,
                        DepartedAt = now, // gerçek ayrılış bilinmiyor — tespit anı
                        ConsentsDeletedAt = now,
                    });
                }

                var consents = await _db.IysConsents
                    .Where(c => c.BrandCode == brand).ToListAsync(ct);
                _db.IysConsents.RemoveRange(consents);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Yetim marka temizliği başarısız (marka {Brand})", brand);
            }
        }
    }

    /// <summary>Faz 3 — m.13 süresi dolan dönem ispatının imhası.</summary>
    private async Task PurgeExpiredProofAsync(DateTimeOffset now, CancellationToken ct)
    {
        var threshold = now - ProofRetention;
        var ids = await _db.NetgsmDepartures.AsNoTracking()
            .Where(d => d.PurgedAt == null && d.DepartedAt <= threshold)
            .Select(d => d.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            try
            {
                var dep = await _db.NetgsmDepartures
                    .FirstOrDefaultAsync(d => d.Id == id, ct);
                if (dep is null || dep.PurgedAt is not null) continue;

                // DÖNEM sınırı: yalnız ayrılış ANINA KADAR olan olaylar.
                // Marka devredildiyse yeni dönemin ispatı bu imhaya KARIŞMAZ.
                var events = await _db.IysConsentEvents
                    .Where(e => e.BrandCode == dep.BrandCode
                                && e.OccurredAt <= dep.DepartedAt)
                    .ToListAsync(ct);
                _db.IysConsentEvents.RemoveRange(events);

                if (dep.LicenseId is { } licenseId)
                {
                    var campaigns = await _db.SmsCampaigns
                        .Where(c => c.LicenseId == licenseId
                                    && c.CreatedAt <= dep.DepartedAt)
                        .ToListAsync(ct);
                    var campaignIds = campaigns.Select(c => c.Id).ToList();
                    // InMemory'de ExecuteDeleteAsync yok — açık RemoveRange.
                    var recipients = await _db.SmsCampaignRecipients
                        .Where(r => campaignIds.Contains(r.CampaignId))
                        .ToListAsync(ct);
                    _db.SmsCampaignRecipients.RemoveRange(recipients);
                    _db.SmsCampaigns.RemoveRange(campaigns);
                }

                dep.PurgedAt = now;
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "m.13 imhası başarısız (ayrılış {DepartureId})", id);
            }
        }
    }
}
```

`IysConsentEvent.cs` satır ~16'daki "Hiç silinmez, hiç güncellenmez." cümlesine istisna ekle:

```
Hiç güncellenmez; tek silinme yolu m.13 imhasıdır — IysDepartureRetentionJob
ayrılan markanın DÖNEM olaylarını (OccurredAt ≤ ayrılış) 3 yıl dolunca imha eder.
```

- [ ] **Adım 4: Koştur — YEŞİL**

Çalıştır: aynı filter. Beklenen: 6/6 PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysDepartureRetentionJob.cs OrderDeck.LicenseServer/Domain/IysConsentEvent.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs
git commit -m "feat(iys): 3 fazlı ayrılış saklama işi — 30 günde onaylar, 3 yılda (m.13) dönem ispatı

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 7: DI + Hangfire zamanlaması (04:45 UTC)

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (DI ~209; recurring blok ~950-954)
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs` (DI testi)

- [ ] **Adım 1: DI testini yaz (kırmızı)**

`IysDepartureRetentionJobTests.cs`'e ekle:

```csharp
[Fact]
public void Job_DI_kapsamindan_cozulur()
{
    using var scope = _factory.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<IysDepartureRetentionJob>()
        .Should().NotBeNull();
    scope.ServiceProvider.GetRequiredService<IysMirrorImportJob>()
        .Should().NotBeNull("Görev 8'in işi de aynı yerde kayıtlı olmalı");
}
```

NOT: `IysMirrorImportJob` Görev 8'de yazılacak — bu görevde derleme hatası vermemesi için iki seçenek: (a) Görev 8 tamamlanana kadar o satırı yorumda tut, (b) görevleri sırayla koşuyorsan Görev 8'i önce yazıp DI'yi burada bağla. **Subagent akışında (a) uygula:** satırı `// Görev 8'de açılacak:` yorumuyla ekle, Görev 8 Adım 5'te aç.

- [ ] **Adım 2: Koştur — KIRMIZI**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Job_DI_kapsamindan_cozulur"`
Beklenen: FAIL — kayıt yok.

- [ ] **Adım 3: Program.cs'i düzenle**

DI — satır ~209'daki `AddScoped<...IysConsentRecoveryJob>()` kaydının altına:

```csharp
builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Iys.IysDepartureRetentionJob>();
builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Iys.IysMirrorImportJob>();
```

(`IysMirrorImportJob` satırı Görev 8'in dosyası yazılmadan derlenmez — Görev 8'den önce koşuluyorsa şimdilik ekleme, Görev 8 Adım 5'te ekle. Subagent akışında: bu görevde YALNIZ `IysDepartureRetentionJob` satırını ekle.)

Recurring — backup-orphan-cleanup bloğundan ("20 4 * * *", satır ~950-953) sonra, non-Testing bloğunun kapanışından önce:

```csharp
// §6 — ayrılış saklama takvimi: 30. günde onay satırları + hesap,
// 3. yılda (m.13) dönem ispatı ve kampanya kayıtları. 04:45 UTC —
// yayın penceresi (TR 20:00-01:00) dışında.
manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Iys.IysDepartureRetentionJob>(
    "iys-departure-retention",
    j => j.RunAsync(CancellationToken.None),
    "45 4 * * *");
```

(Dolu slotlar: 04:00, */5×3, */15, 04:15, 04:20, 04:30 MON, 04:35 — 04:45 boş.)

- [ ] **Adım 4: Koştur — YEŞİL**

Çalıştır: aynı filter + tüm `IysDepartureRetentionJobTests`. Beklenen: PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs
git commit -m "feat(iys): saklama işi DI kaydı + günlük 04:45 UTC zamanlaması

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 8: `IysMirrorImportJob` — İYS'den ayna içe aktarım (§6 dönüş yolu)

**Dosyalar:**
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IysMirrorImportJob.cs`
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (Görev 7'de ertelenen `IysMirrorImportJob` DI satırı)
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs` (DI testindeki yorum satırını aç)
- Test (YENİ): `OrderDeck.LicenseServer.Tests/Services/Iys/IysMirrorImportJobTests.cs`

Geri dönen (veya markası devralınan) yayıncı için: WPF müşteri projeksiyonundaki telefonları `/iys/search` ile sorgula, İYS'de kaydı OLANLARI yerel tabloya AYNA satırı olarak yaz. Ayna satırı: `SourceCode="IYS_MIRROR"`, `PushState=Confirmed` (TERMİNAL — push işi Pending'i, verify işi Pushed'ı tarar; Pending yazılsaydı yeniden beyan edilip consentDate kayardı), `ConsentDate=null` (`/iys/search` consentDate DÖNMÜYOR — 2026-09-17 ölçümü). `Unknown` = İYS'de kayıt yok → satır YAZILMAZ.

- [ ] **Adım 1: Testleri yaz (kırmızı — derlenmez)**

`IysMirrorImportJobTests.cs` (yeni dosya):

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// §6 dönüş yolu — İYS'den ayna içe aktarım. Sahte istemcinin AddAsync'i
/// KASITLI patlar: ayna işi bir OKUMA işidir, beyan (add) ÇAĞIRMAMALI.
/// Testler ≤20 telefonla tek parça kalır (BatchDelay'e girmez).
/// </summary>
public sealed class IysMirrorImportJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IysMirrorImportJobTests(ApiFactory factory) => _factory = factory;

    private sealed class FakeIysClient : IIysClient
    {
        public Dictionary<string, IysConsentStatus> Statuses { get; } = new();
        public List<string[]> SearchCalls { get; } = new();

        public Task<IysAddResult> AddAsync(IysAccountContext account,
            IReadOnlyList<IysConsentRecord> items, CancellationToken ct = default)
            => throw new NotSupportedException(
                "Ayna işi /iys/add ÇAĞIRMAMALI — beyan değil okuma.");

        public Task<IysSearchResult> SearchAsync(IysAccountContext account,
            IReadOnlyList<string> recipients, CancellationToken ct = default)
        {
            SearchCalls.Add(recipients.ToArray());
            var found = recipients
                .Where(Statuses.ContainsKey)
                .ToDictionary(r => r, r => Statuses[r]);
            return Task.FromResult(new IysSearchResult("0", "{}", found));
        }
    }

    private static string NewPhone()
        => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task<(Guid LicenseId, string BrandCode)> SeedAsync(
        LicenseDbContext db, NetgsmAccountService accounts,
        NetgsmAccountStatus status, params string[] phones)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Donen",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-{Guid.NewGuid():N}"[..24],
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        });
        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}"[..32],
            PasswordProtected = accounts.ProtectPassword($"p-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        foreach (var p in phones)
        {
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                Platform = "youtube",
                Username = $"u{Guid.NewGuid():N}"[..12],
                FullName = "Musteri",
                Phone = p,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (licenseId, brandCode);
    }

    [Fact]
    public async Task Onay_ve_ret_terminal_satir_olarak_yazilir_unknown_atlanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var onayPhone = NewPhone();
        var retPhone = NewPhone();
        var unknownPhone = NewPhone();
        var (licenseId, brand) = await SeedAsync(db, accounts,
            NetgsmAccountStatus.Verified, onayPhone, retPhone, unknownPhone);

        var fake = new FakeIysClient();
        fake.Statuses[onayPhone] = IysConsentStatus.Onay;
        fake.Statuses[retPhone] = IysConsentStatus.Ret;
        fake.Statuses[unknownPhone] = IysConsentStatus.Unknown;

        var job = new IysMirrorImportJob(db, accounts, fake,
            NullLogger<IysMirrorImportJob>.Instance);
        await job.RunAsync(licenseId, CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await vdb.IysConsents.AsNoTracking()
            .Where(c => c.BrandCode == brand).ToListAsync();
        rows.Should().HaveCount(2, "Unknown = İYS'de kayıt yok → satır yazılmaz");

        var onay = rows.Single(r => r.Recipient == onayPhone);
        onay.Status.Should().Be(IysConsentStatus.Onay);
        onay.SourceCode.Should().Be(IysMirrorImportJob.SourceCodeMirror);
        onay.PushState.Should().Be(IysPushState.Confirmed,
            "terminal — push işi Pending'i tarar, ayna yeniden beyan ETMEMELİ");
        onay.ConsentDate.Should().BeNull("/iys/search consentDate döndürmüyor");
        onay.NextVerifyAt.Should().BeNull("verify işi Pushed'ı tarar — ayna dışında");
        onay.LastVerifiedStatus.Should().Be(IysConsentStatus.Onay);

        rows.Single(r => r.Recipient == retPhone).Status.Should().Be(IysConsentStatus.Ret);

        var events = await vdb.IysConsentEvents.AsNoTracking()
            .Where(e => e.BrandCode == brand).ToListAsync();
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e =>
            e.EventType == IysConsentEventType.SearchResult && e.LicenseId == licenseId);
    }

    [Fact]
    public async Task Yerel_satiri_olan_numara_sorgulanmaz_ve_ezilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var localPhone = NewPhone();
        var newPhone = NewPhone();
        var (licenseId, brand) = await SeedAsync(db, accounts,
            NetgsmAccountStatus.Verified, localPhone, newPhone);

        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(), BrandCode = brand, ChannelType = "MESAJ",
            RecipientType = "BIREYSEL", Recipient = localPhone,
            Status = IysConsentStatus.Onay, SourceCode = "HS_WEB",
            PushState = IysPushState.Confirmed,
            LastLocalEventAt = now, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var fake = new FakeIysClient();
        fake.Statuses[localPhone] = IysConsentStatus.Ret; // ezmeye çalışsa bile
        fake.Statuses[newPhone] = IysConsentStatus.Onay;

        var job = new IysMirrorImportJob(db, accounts, fake,
            NullLogger<IysMirrorImportJob>.Instance);
        await job.RunAsync(licenseId, CancellationToken.None);

        fake.SearchCalls.Should().HaveCount(1);
        fake.SearchCalls[0].Should().NotContain(localPhone,
            "yerel beyan varken İYS'ye sorulmaz — yerel kayıt EZİLMEZ");
        fake.SearchCalls[0].Should().Contain(newPhone);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var local = await vdb.IysConsents.AsNoTracking()
            .SingleAsync(c => c.BrandCode == brand && c.Recipient == localPhone);
        local.Status.Should().Be(IysConsentStatus.Onay);
        local.SourceCode.Should().Be("HS_WEB");
    }

    [Fact]
    public async Task Dogrulanmamis_hesapta_sessizce_cikar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (licenseId, brand) = await SeedAsync(db, accounts,
            NetgsmAccountStatus.Failed, NewPhone());

        var fake = new FakeIysClient();
        var job = new IysMirrorImportJob(db, accounts, fake,
            NullLogger<IysMirrorImportJob>.Instance);
        await job.RunAsync(licenseId, CancellationToken.None);

        fake.SearchCalls.Should().BeEmpty();
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeFalse();
    }

    [Fact]
    public async Task Ikinci_kosu_idempotent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var phone = NewPhone();
        var (licenseId, brand) = await SeedAsync(db, accounts,
            NetgsmAccountStatus.Verified, phone);

        var fake = new FakeIysClient();
        fake.Statuses[phone] = IysConsentStatus.Onay;

        var job = new IysMirrorImportJob(db, accounts, fake,
            NullLogger<IysMirrorImportJob>.Instance);
        await job.RunAsync(licenseId, CancellationToken.None);
        var callsAfterFirst = fake.SearchCalls.Count;
        await job.RunAsync(licenseId, CancellationToken.None);

        fake.SearchCalls.Count.Should().Be(callsAfterFirst,
            "ilk koşu satırı yazdı — ikinci koşuda eksik numara kalmadı");
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.CountAsync(c => c.BrandCode == brand)).Should().Be(1);
        (await vdb.IysConsentEvents.CountAsync(e => e.BrandCode == brand)).Should().Be(1);
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~IysMirrorImportJobTests"`
Beklenen: FAIL — `IysMirrorImportJob` yok.

- [ ] **Adım 3: İşi yaz**

`OrderDeck.LicenseServer/Services/Iys/IysMirrorImportJob.cs` (yeni dosya):

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// §6 dönüş yolu — İYS'den AYNA içe aktarım. Geri dönen yayıncının WPF
/// müşteri telefonlarını /iys/search ile sorgular; İYS'de kaydı OLANLARI
/// terminal ayna satırı olarak yazar. Yerel satırı olan numaraya DOKUNMAZ
/// (yerel beyan her zaman kazanır). Unknown = İYS'de kayıt yok → satır
/// yazılmaz ("kayıt yok"u satıra çevirmek yanlış sinyal olur).
/// DisableConcurrentExecution YOK: iş idempotent — eş zamanlı iki koşu en
/// kötü tekil indeks yarışında düşer, sonraki koşu tamamlar.
/// </summary>
public sealed class IysMirrorImportJob
{
    public const string SourceCodeMirror = "IYS_MIRROR";

    internal const int BatchSize = 20;
    // Netgsm ~10 istek/dk — partiler arası 6 sn (ilk partide bekleme yok).
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(6);

    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly IIysClient _client;
    private readonly ILogger<IysMirrorImportJob> _log;

    public IysMirrorImportJob(LicenseDbContext db, NetgsmAccountService accounts,
        IIysClient client, ILogger<IysMirrorImportJob> log)
    {
        _db = db;
        _accounts = accounts;
        _client = client;
        _log = log;
    }

    public async Task RunAsync(Guid licenseId, CancellationToken ct)
    {
        var account = await _accounts.GetVerifiedByLicenseAsync(licenseId, ct);
        if (account is null)
        {
            _log.LogWarning("Ayna: doğrulanmış Netgsm hesabı yok (lisans {LicenseId})",
                licenseId);
            return;
        }

        var password = _accounts.TryUnprotectPassword(account.PasswordProtected);
        if (password is null)
        {
            _log.LogError("Ayna: parola çözülemedi (lisans {LicenseId})", licenseId);
            return;
        }

        var ctx = new IysAccountContext(
            licenseId, account.UserCode, password, account.BrandCode);

        var rawPhones = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.PurgedAt == null
                        && p.Phone != null && p.Phone != "")
            .Select(p => p.Phone!)
            .ToListAsync(ct);

        var phones = new List<string>();
        foreach (var raw in rawPhones)
            if (Auth.PhoneNormalizer.TryNormalize(raw, out var e164))
                phones.Add(e164);
        phones = phones.Distinct().ToList();

        var known = (await _db.IysConsents.AsNoTracking()
                .Where(c => c.BrandCode == account.BrandCode
                            && c.ChannelType == "MESAJ"
                            && c.RecipientType == "BIREYSEL")
                .Select(c => c.Recipient)
                .ToListAsync(ct))
            .ToHashSet();

        var missing = phones.Where(p => !known.Contains(p)).ToList();
        if (missing.Count == 0) return;

        var first = true;
        foreach (var chunk in missing.Chunk(BatchSize))
        {
            if (!first) await Task.Delay(BatchDelay, ct);
            first = false;

            IysSearchResult result;
            try
            {
                result = await _client.SearchAsync(ctx, chunk, ct);
            }
            catch (IysConfigurationException ex)
            {
                _log.LogError(ex, "Ayna: kalıcı yapılandırma hatası (marka {Brand})",
                    account.BrandCode);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning(ex,
                    "Ayna: geçici ağ hatası — kalan partiler sonraki koşuya "
                    + "(marka {Brand})", account.BrandCode);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var phone in chunk)
            {
                if (!result.Statuses.TryGetValue(phone, out var status)
                    || status == IysConsentStatus.Unknown)
                    continue; // İYS'de kayıt yok — satır yazılmaz.

                var consent = new IysConsent
                {
                    Id = Guid.NewGuid(),
                    BrandCode = account.BrandCode,
                    ChannelType = "MESAJ",
                    RecipientType = "BIREYSEL",
                    Recipient = phone,
                    Status = status,
                    ConsentDate = null,          // /iys/search consentDate DÖNMÜYOR (2026-09-17 ölçümü)
                    SourceCode = SourceCodeMirror,
                    PushState = IysPushState.Confirmed, // TERMİNAL: push Pending'i, verify Pushed'ı tarar
                    LastVerifiedStatus = status,
                    LastVerifiedAt = now,
                    LastLocalEventAt = now,
                    NextVerifyAt = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.IysConsents.Add(consent);
                _db.IysConsentEvents.Add(new IysConsentEvent
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    BrandCode = account.BrandCode,
                    IysConsentId = consent.Id,
                    Recipient = phone,
                    OccurredAt = now,
                    EventType = IysConsentEventType.SearchResult,
                    Status = status,
                    ApiResponseCode = result.Code,
                });
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogWarning(ex,
                    "Ayna: yazım çakışması (tekil indeks yarışı) — idempotent, "
                    + "sonraki koşu tamamlar (marka {Brand})", account.BrandCode);
            }
        }
    }
}
```

(`Auth.PhoneNormalizer` — `IysConsentCollector.cs:77` ile aynı çözümleme: `Services.Iys` isim alanından `OrderDeck.LicenseServer.Services.Auth.PhoneNormalizer`'a görece erişim.)

- [ ] **Adım 4: Koştur — YEŞİL**

Çalıştır: aynı filter. Beklenen: 4/4 PASS.

- [ ] **Adım 5: Görev 7'de ertelenen DI satırını aç**

`Program.cs`'e ekle (Görev 7'de eklenen `IysDepartureRetentionJob` satırının altına):

```csharp
builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Iys.IysMirrorImportJob>();
```

`IysDepartureRetentionJobTests.Job_DI_kapsamindan_cozulur` içindeki yorumlu `IysMirrorImportJob` satırını aç. Koştur:
`dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Job_DI_kapsamindan_cozulur"`
Beklenen: PASS.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysMirrorImportJob.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysMirrorImportJobTests.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs
git commit -m "feat(iys): İYS ayna içe aktarım işi — dönüş yolunda terminal satırlar (§6)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 9: Panel `POST /api/panel/netgsm/account/iys-mirror`

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs`
- Değiştir: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountControllerTests.cs`

Yayıncı (owner) aynayı panelden tetikler; iş Hangfire kuyruğuna atılır (202). ApiFactory Hangfire MemoryStorage kullanıyor → `Enqueue` testte çalışır ama iş KOŞMAZ — 202 assert edilebilir.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`PanelNetgsmAccountControllerTests.cs`'e ekle (dosyanın mevcut kalıpları: per-test `NewFactory()`, `SeedTenantAsync(factory)`, `SeedAccountAsync(factory, licenseId, status, ...)`, `PanelOperatorHelper.StaffClientAsync(factory, seed.Client)`):

```csharp
[Fact]
public async Task Ayna_baslatma_dogrulanmis_hesapta_202()
{
    var factory = NewFactory();
    var seed = await SeedTenantAsync(factory);
    await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Verified);

    var resp = await seed.Client.PostAsync("/api/panel/netgsm/account/iys-mirror", null);

    resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
}

[Fact]
public async Task Ayna_dogrulanmamis_hesapta_409()
{
    var factory = NewFactory();
    var seed = await SeedTenantAsync(factory);
    await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Failed);

    var resp = await seed.Client.PostAsync("/api/panel/netgsm/account/iys-mirror", null);

    resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
}

[Fact]
public async Task Ayna_staff_operatore_kapali_403()
{
    var factory = NewFactory();
    var seed = await SeedTenantAsync(factory);
    await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Verified);
    var staff = await PanelOperatorHelper.StaffClientAsync(factory, seed.Client);

    var resp = await staff.PostAsync("/api/panel/netgsm/account/iys-mirror", null);

    resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
}
```

- [ ] **Adım 2: Koştur — KIRMIZI**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelNetgsmAccountControllerTests.Ayna"`
Beklenen: 3 test FAIL (404 — endpoint yok).

- [ ] **Adım 3: Endpoint'i yaz**

`PanelNetgsmAccountController.cs`:
1. `using Hangfire;` ekle.
2. Ctor'a `IBackgroundJobClient jobs` parametresi + `private readonly IBackgroundJobClient _jobs;` alanı ekle (kalıp: `LicensesSmsCampaignsController` ctor'u).
3. Yeni action:

```csharp
/// <summary>§6 dönüş yolu — İYS ayna içe aktarımını kuyruğa atar.
/// Yalnız owner; doğrulanmış Netgsm hesabı şart.</summary>
[HttpPost("iys-mirror")]
public async Task<IActionResult> StartIysMirrorAsync(CancellationToken ct)
{
    if (OwnerOnly() is { } forbidden) return forbidden;

    var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
    if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

    var acc = await _accounts.GetVerifiedByLicenseAsync(licenseId.Value, ct);
    if (acc is null)
        return Problem(title: "netgsm-not-verified",
            detail: "Ayna için önce Netgsm kurulumunun doğrulanması gerekir.",
            statusCode: 409);

    _jobs.Enqueue<Services.Iys.IysMirrorImportJob>(
        j => j.RunAsync(licenseId.Value, CancellationToken.None));
    return Accepted();
}
```

- [ ] **Adım 4: Koştur — YEŞİL**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelNetgsmAccountControllerTests"`
Beklenen: tüm sınıf PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountControllerTests.cs
git commit -m "feat(iys): panelden İYS ayna içe aktarımı tetikleme (owner, 202)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 10: Aydınlatma metni — iki kademeli saklama

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml` (KVKK paragrafı ~397-403)

- [ ] **Adım 1: Metni ekle**

`<p class="kvkk">` paragrafındaki mevcut metnin ("...hesabına başka erişimimiz olmaz.") SONUNA şu cümleleri ekle:

```
SMS izniniz yayıncının markası adına İleti Yönetim Sistemi'ne (İYS) bildirilir.
Yayıncı platformdan ayrılırsa güncel izin kayıtlarınız 30 gün içinde silinir;
izin işlemlerine ait ispat kayıtları ise mevzuat gereği (6563 sayılı Kanun,
m.13) ayrılıştan itibaren 3 yıl saklanır ve süre sonunda imha edilir.
```

ASLA "tamamen sildik / hiçbir veri kalmaz" ifadesi kullanma — m.13 ispatı 3 yıl yaşar.

- [ ] **Adım 2: Derle**

Çalıştır: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Beklenen: hatasız (Razor değişikliği için derleme doğrulaması yeter; sayfa testi mevcut değil, davranış değişmedi).

- [ ] **Adım 3: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml
git commit -m "docs(iys): aydınlatma metnine iki kademeli saklama takvimi eklendi (§6 + m.13)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 11: Tam paket + dal kapanışı

- [ ] **Adım 1: Sunucu test paketinin tamamını koştur**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Beklenen: tümü PASS. NOT: Testcontainers testleri için Docker açık olmalı; kapalıysa yalnız `SqlServerCollection` testleri düşer — bu plandaki işler InMemory, ama TAM paket yeşil görülmeden bitmiş sayılmaz.

- [ ] **Adım 2: finishing-a-development-branch skill'ini çağır**

Skill 4 seçeneği sunar (merge / PR / beklet / at). Standart akış: **kullanıcı PR'ları kendisi merge eder, master'a push YOK** (otomatik prod deploy). Yayın penceresi 20:00–01:00 TR'de merge önerme.
