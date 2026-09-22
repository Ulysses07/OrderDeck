# Panel Netgsm/İYS Kurulum Ekranı + Otomatik İYS Aynası — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Yayıncı Netgsm hesabını ve İYS marka kodunu panelden girip doğrulatsın; doğrulama başarısında ve her gece İYS'deki mevcut onaylar kimse düğmeye basmadan yerel tabloya aynalansın.

**Architecture:** Sunucu (LiveDeck): `PUT /api/panel/netgsm/account` doğrulama başarısında `IysMirrorImportJob`'u kuyruğa atar; yeni günlük `IysMirrorSyncJob` (04:52 UTC) her Verified hesap için aynı işi kuyruğa atar. Panel (OrderDeck-Mobile): `api/netgsmAccount.ts` (TanStack Query) + `NetgsmKurulumScreen` (durum kartı, form, rehber) + "Daha Fazla" menü girişi; WhatsApp Bağla ekranının kalıpları birebir.

**Tech Stack:** ASP.NET Core 10 + EF Core 10 (InMemory testte) + Hangfire (MemoryStorage testte), xUnit + FluentAssertions; React 18 + Vite + TypeScript + TanStack Query + Tailwind, Vitest + Testing Library.

**Spec:** `docs/superpowers/specs/2026-09-22-panel-netgsm-kurulum-ekrani-design.md`

---

## Genel kurallar

- İki repo, iki dal: LiveDeck `feat/iys-mirror-auto-sync` (Görev 1-4), OrderDeck-Mobile `feat/netgsm-kurulum-ekrani` (Görev 5-8). Sunucu PR'ı önce merge/deploy edilir.
- LiveDeck **public**: testlerde literal kimlik bilgisi YOK (`$"pw-{Guid.NewGuid():N}"`, üretilen abone numarası).
- Sunucu yorumları/testleri Türkçe; commit mesajı Türkçe emir kipi + `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.
- Komutlar LiveDeck kökünden: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "..."`. Panel komutları `apps/panel` içinden: `npx vitest run <dosya>`, `npm run -s typecheck`, `npx eslint <dosya>`.
- Yayın penceresi (Paz/Pzt/Çar/Per 20:00–01:00 TR) içinde master merge YOK.

## Dosya haritası

| Repo | Dosya | Görev |
|---|---|---|
| LiveDeck | `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs` | 1 — doğrulama başarısında ayna kuyruğu |
| LiveDeck | `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountMirrorEnqueueTests.cs` (YENİ) | 1 |
| LiveDeck | `OrderDeck.LicenseServer/Services/Iys/IysMirrorSyncJob.cs` (YENİ) | 2 |
| LiveDeck | `OrderDeck.LicenseServer.Tests/Services/Iys/IysMirrorSyncJobTests.cs` (YENİ) | 2 |
| LiveDeck | `OrderDeck.LicenseServer/Program.cs` (DI ~211, recurring ~965) | 3 |
| LiveDeck | `OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs` (DI testi) | 3 |
| Mobile | `apps/panel/src/api/netgsmAccount.ts` (YENİ) + `netgsmAccount.test.tsx` (YENİ) | 5 |
| Mobile | `apps/panel/src/screens/NetgsmKurulumScreen.tsx` (YENİ) + `.test.tsx` (YENİ) | 6 |
| Mobile | `apps/panel/src/router.tsx`, `apps/panel/src/screens/DahaFazlaScreen.tsx` + `.test.tsx` | 7 |

---

### Görev 1: Doğrulama başarısında ayna işi kuyruğa girer (sunucu)

**Files:**
- Test (YENİ): `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountMirrorEnqueueTests.cs`
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs` (`SaveAsync`, `await _db.SaveChangesAsync(ct);` ile `return Ok(ToView(account));` arası, ~satır 290-292)

Ön bilgi: test host'ta `IIysClient` = `NullIysClient` (`SearchAsync` `"not-configured"` döner) → doğrulayıcı `Unavailable` → hesap `Failed`. Doğrulama başarısı için `IIysClient` `"0"` döndüren bir stub ile değiştirilir (kalıp: `IysNoBrandReplayTests.ReplayApiFactory`). `PUT`, doğrulama sonucu ne olursa olsun `200 + AccountView` döner (`status: "verified" | "failed"`).

- [ ] **Adım 1: Dalı aç**

```bash
git fetch origin master
git worktree add .claude/worktrees/iys-mirror-auto-sync -b feat/iys-mirror-auto-sync origin/master
```
(Bundan sonraki sunucu komutları bu worktree içinden.)

- [ ] **Adım 2: Testi yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountMirrorEnqueueTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Doğrulama başarılı olunca İYS aynası kimse düğmeye basmadan kuyruğa girmeli
/// (spec §2.1): başka sağlayıcıdan geçen, elle yükleyen ya da geri dönen
/// yayıncının İYS'deki onayları yerel tabloya gelsin. Başarısız doğrulamada
/// KUYRUĞA GİRMEMELİ — iş zaten "doğrulanmış hesap yok" diye çıkardı, boşa çağrı.
/// </summary>
public sealed class PanelNetgsmAccountMirrorEnqueueTests : IDisposable
{
    private readonly List<StubApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    /// <summary>Doğrulayıcı yalnız <c>code "0"</c>'ı Ok sayar; başka her kod
    /// Unavailable → hesap Failed. Tek ayarla iki senaryo.</summary>
    private sealed class StubIysClient : IIysClient
    {
        public string SearchCode { get; set; } = "0";

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException("Bu testte push beklenmiyor.");

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(new IysSearchResult(
                SearchCode, "{}", new Dictionary<string, IysConsentStatus>()));
    }

    private sealed class StubApiFactory : ApiFactory
    {
        public StubIysClient Iys { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IIysClient>();
                s.AddSingleton<IIysClient>(Iys);
            });
        }
    }

    private StubApiFactory NewFactory(string searchCode)
    {
        var f = new StubApiFactory();
        f.Iys.SearchCode = searchCode;
        _factories.Add(f);
        return f;
    }

    /// <summary>Netgsm abone numarası ÜRETİLİR: depo public, sabit bir değer
    /// gerçek bir aboneye ait olabilir.</summary>
    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();

    private static object Body(string userCode, string brandCode) => new
    {
        userCode,
        password = $"pw-{Guid.NewGuid():N}",
        header = "ORDERDECK",
        brandCode,
    };

    private static async Task<(HttpClient Client, Guid LicenseId)> SeedTenantAsync(
        ApiFactory factory)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-MIR-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    /// <summary>ApiFactory Hangfire MemoryStorage kullanır: Enqueue çalışır ama iş
    /// KOŞMAZ — kuyruk monitoring API'den okunabilir.</summary>
    private static bool MirrorEnqueued(ApiFactory factory, Guid licenseId)
    {
        var monitoring = factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi();
        return monitoring.EnqueuedJobs("default", 0, 1000).Any(j =>
            j.Value.Job.Type == typeof(IysMirrorImportJob)
            && j.Value.Job.Args.Contains((object)licenseId));
    }

    [Fact]
    public async Task Dogrulama_basarili_PUT_ayna_isini_kuyruga_atar()
    {
        var factory = NewFactory(searchCode: "0");
        var (client, licenseId) = await SeedTenantAsync(factory);

        var resp = await client.PutAsJsonAsync(
            "/api/panel/netgsm/account", Body(NewUserCode(), NewBrandCode()));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("verified",
            "ön koşul: stub İYS 'code 0' döndürdü, doğrulama başarılı");

        MirrorEnqueued(factory, licenseId).Should().BeTrue(
            "marka doğrulanınca İYS'deki mevcut onaylar kendiliğinden aynalanmalı — düğme yok");
    }

    [Fact]
    public async Task Dogrulama_basarisiz_PUT_ayna_isi_kuyruga_atmaz()
    {
        var factory = NewFactory(searchCode: "not-configured");
        var (client, licenseId) = await SeedTenantAsync(factory);

        var resp = await client.PutAsJsonAsync(
            "/api/panel/netgsm/account", Body(NewUserCode(), NewBrandCode()));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "PUT sonucu görünümle döner, doğrulama düşse de");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("failed");

        MirrorEnqueued(factory, licenseId).Should().BeFalse(
            "doğrulanmamış hesap için ayna işi 'hesap yok' diye çıkar — boşa kuyruk");
    }
}
```

- [ ] **Adım 3: Koştur — KIRMIZI**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelNetgsmAccountMirrorEnqueueTests"`
Beklenen: `Dogrulama_basarili_PUT_ayna_isini_kuyruga_atar` FAIL (`MirrorEnqueued` false); `…kuyruga_atmaz` PASS.

- [ ] **Adım 4: Kuyruğa atmayı yaz**

`PanelNetgsmAccountController.cs`, `SaveAsync` içinde — mevcut:

```csharp
            _db.Entry(account).Property(a => a.UpdatedAt).IsModified = true;
            await _db.SaveChangesAsync(ct);

            return Ok(ToView(account));
```

şöyle olsun:

```csharp
            _db.Entry(account).Property(a => a.UpdatedAt).IsModified = true;
            await _db.SaveChangesAsync(ct);

            if (result.Outcome == NetgsmVerifyOutcome.Ok)
            {
                // Marka az önce doğrulandı: İYS'de zaten var olan onaylar (başka
                // sağlayıcıdan geçen, elle yükleyen, geri dönen yayıncı) kimse
                // düğmeye basmadan yerel tabloya gelsin (spec §2.1). SaveChanges'ten
                // SONRA: commit olmamış hesap için koşan iş "doğrulanmış hesap
                // yok" diye çıkardı. Lisans başına kilit ve yeniden deneme işin
                // kendisinde; mükerrer kayıt `known` kümesi sayesinde no-op'a yakın.
                _jobs.Enqueue<Services.Iys.IysMirrorImportJob>(
                    j => j.RunAsync(account.LicenseId, CancellationToken.None));
            }

            return Ok(ToView(account));
```

(`result` aynı metotta yukarıda tanımlı `NetgsmVerifyResult`; `_jobs` alanı Görev 9/#477 ile zaten var.)

- [ ] **Adım 5: Koştur — YEŞİL**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelNetgsmAccountMirrorEnqueueTests|FullyQualifiedName~PanelNetgsmAccountControllerTests|FullyQualifiedName~IysNoBrandReplayTests"`
Beklenen: tümü PASS (2 + mevcut sınıflar).

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountMirrorEnqueueTests.cs
git commit -m "feat(iys): Netgsm kurulumu doğrulanınca İYS ayna işi kendiliğinden kuyruğa girer

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 2: `IysMirrorSyncJob` — günlük eşitleme (sunucu)

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Iys/IysMirrorSyncJob.cs`
- Test (YENİ): `OrderDeck.LicenseServer.Tests/Services/Iys/IysMirrorSyncJobTests.cs`

- [ ] **Adım 1: Testi yaz (kırmızı — derlenmez)**

```csharp
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>Günlük eşitleme yalnız DOĞRULANMIŞ hesaplar için ayna işi kuyruğa
/// atar; kendisi ayna koşturmaz (kilit ve kota temposu ayna işinde).</summary>
public sealed class IysMirrorSyncJobTests
{
    /// <summary>Enqueue çağrılarını kaydeden IBackgroundJobClient — gerçek
    /// Hangfire storage'a yazmadan hangi lisansların kuyruğa alındığını
    /// doğrulamak için (kalıp: SmsCampaignRecoveryJobTests).</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = new();

        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return Guid.NewGuid().ToString("N");
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-sync-{Guid.NewGuid():N}").Options);

    private static NetgsmAccountService Accounts(LicenseDbContext db) => new(db, Protection);

    /// <summary>Abone numarası ve parola ÜRETİLİR (depo public).</summary>
    private static Guid SeedAccount(LicenseDbContext db, NetgsmAccountStatus status)
    {
        var licenseId = Guid.NewGuid();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            PasswordProtected = Accounts(db).ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return licenseId;
    }

    private static IysMirrorSyncJob Job(LicenseDbContext db, IBackgroundJobClient jobs)
        => new(Accounts(db), jobs, NullLogger<IysMirrorSyncJob>.Instance);

    [Fact]
    public async Task Yalniz_dogrulanmis_hesaplar_icin_ayna_isi_kuyruga_girer()
    {
        using var db = NewDb();
        var a = SeedAccount(db, NetgsmAccountStatus.Verified);
        var b = SeedAccount(db, NetgsmAccountStatus.Verified);
        SeedAccount(db, NetgsmAccountStatus.Failed);
        SeedAccount(db, NetgsmAccountStatus.Disabled);
        var jobs = new RecordingJobClient();

        var count = await Job(db, jobs).RunAsync();

        count.Should().Be(2);
        jobs.Created.Should().HaveCount(2);
        jobs.Created.Should().OnlyContain(j => j.Type == typeof(IysMirrorImportJob),
            "eşitleme işi ayna koşturmaz, ayna İŞİNİ kuyruğa atar");
        jobs.Created.Select(j => (Guid)j.Args[0]).Should().BeEquivalentTo(new[] { a, b },
            "Failed ve Disabled hesaplar için ayna anlamsız — iş 'hesap yok' diye çıkardı");
    }

    [Fact]
    public async Task Dogrulanmis_hesap_yoksa_hicbir_is_kuyruga_girmez()
    {
        using var db = NewDb();
        SeedAccount(db, NetgsmAccountStatus.Failed);
        var jobs = new RecordingJobClient();

        (await Job(db, jobs).RunAsync()).Should().Be(0);
        jobs.Created.Should().BeEmpty();
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~IysMirrorSyncJobTests"`
Beklenen: FAIL — `IysMirrorSyncJob` tipi yok.

- [ ] **Adım 3: İşi yaz**

`OrderDeck.LicenseServer/Services/Iys/IysMirrorSyncJob.cs`:

```csharp
using Hangfire;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Günlük İYS eşitlemesi (spec §2.2): her DOĞRULANMIŞ hesap için
/// <see cref="IysMirrorImportJob"/>'u kuyruğa atar. Kendisi ayna KOŞTURMAZ —
/// lisans başına kilit, yeniden deneme ve Netgsm kota temposu (20'lik parti,
/// 6 sn) ayna işinde kalır. Maliyet artımlı DEĞİL: ayna yalnız ONAY satırı
/// yazar; İYS satırı (yerel beyanı da) olmayan numaralar her gece yeniden
/// sorulur; günlük yük ≈ (ONAY'sız müşteri / 20) `/iys/search` çağrısı,
/// yayıncının kendi Netgsm kotasından. Kuyruğa atma arızası bilerek
/// yakalanmaz: recurring koşu Hangfire panosunda Failed görünsün.
/// Doğrulama anındaki tetik (PanelNetgsmAccountController) ilk yüklemeyi
/// yapar; bu iş sonradan İYS'ye başka yoldan giren onayları getirir.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 60)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysMirrorSyncJob
{
    private readonly NetgsmAccountService _accounts;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<IysMirrorSyncJob> _log;

    public IysMirrorSyncJob(
        NetgsmAccountService accounts,
        IBackgroundJobClient jobs,
        ILogger<IysMirrorSyncJob> log)
    {
        _accounts = accounts;
        _jobs = jobs;
        _log = log;
    }

    /// <returns>Kuyruğa atılan ayna işi sayısı (test için).</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var verified = await _accounts.ListVerifiedAsync(ct);
        foreach (var acc in verified)
        {
            var licenseId = acc.LicenseId;
            _jobs.Enqueue<IysMirrorImportJob>(
                j => j.RunAsync(licenseId, CancellationToken.None));
        }

        if (verified.Count > 0)
        {
            _log.LogInformation(
                "İYS eşitleme: {Count} doğrulanmış hesap için ayna işi kuyruğa alındı",
                verified.Count);
        }

        return verified.Count;
    }
}
```

- [ ] **Adım 4: Koştur — YEŞİL**

Aynı filter. Beklenen: 2/2 PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Iys/IysMirrorSyncJob.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysMirrorSyncJobTests.cs
git commit -m "feat(iys): günlük İYS eşitleme işi — doğrulanmış her hesap için ayna kuyruğu

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 3: DI + Hangfire zamanlaması (04:52 UTC)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (DI ~satır 211; recurring blok ~satır 962-965)
- Değiştir: `OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs` (`Job_DI_kapsamindan_cozulur`)

- [ ] **Adım 1: DI testini genişlet (kırmızı)**

`Job_DI_kapsamindan_cozulur` testine, `IysMirrorImportJob` satırının altına ekle:

```csharp
        scope.ServiceProvider.GetRequiredService<IysMirrorSyncJob>()
            .Should().NotBeNull("günlük eşitleme işi de aynı yerde kayıtlı olmalı");
```

- [ ] **Adım 2: Koştur — KIRMIZI**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Job_DI_kapsamindan_cozulur"`
Beklenen: FAIL — `No service for type 'OrderDeck.LicenseServer.Services.Iys.IysMirrorSyncJob'`.

- [ ] **Adım 3: Program.cs**

DI — `AddScoped<...IysMirrorImportJob>()` satırının (≈211) hemen altına:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Iys.IysMirrorSyncJob>();
```

Recurring — `iys-departure-retention` bloğunun (`"47 4 * * *"`) hemen altına, non-Testing bloğu kapanmadan önce:

```csharp
            // Günlük İYS eşitlemesi: doğrulanmış her hesap için ayna işini kuyruğa
            // atar; ayna yalnız ONAY satırı yazar, İYS satırı olmayan numaralar her
            // gece yeniden sorulur (maliyet artımlı DEĞİL). 04:52 UTC — 5 dakikalık
            // ızgara dışı, saklama işinden (04:47) sonra.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Iys.IysMirrorSyncJob>(
                "iys-mirror-sync",
                j => j.RunAsync(CancellationToken.None),
                "52 4 * * *");  // 04:52 UTC daily
```

Önce `grep -n "52 4\|iys-mirror-sync" OrderDeck.LicenseServer/Program.cs` ile slot ve id'nin boş olduğunu doğrula (dolu slotlar: 04:00, 04:15, 04:20, 04:30 MON, 04:35, 04:47, */5, */15).

- [ ] **Adım 4: Koştur — YEŞİL**

Çalıştır: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~IysDepartureRetentionJobTests|FullyQualifiedName~IysMirrorSyncJobTests"` → PASS; `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj` → 0 hata.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Iys/IysDepartureRetentionJobTests.cs
git commit -m "feat(iys): eşitleme işi DI kaydı + günlük 04:52 UTC zamanlaması

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 4: Sunucu tam paket + PR

- [ ] **Adım 1: Tam paket** (Docker açık; PowerShell'den `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'`):

`dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj` → tümü PASS.

- [ ] **Adım 2: Push + PR** (`gh pr create --base master`), gövdede: doğrulama anında ayna kuyruğu, `iys-mirror-sync` 04:52 UTC, kota notu, "panel PR'ı bu deploy'dan sonra". Merge Burak'ta, yayın penceresi dışında.

---

### Görev 5: Panel API modülü `netgsmAccount.ts`

**Files:**
- Oluştur: `apps/panel/src/api/netgsmAccount.ts`
- Test (YENİ): `apps/panel/src/api/netgsmAccount.test.tsx`

Kalıp: `apps/panel/src/api/whatsappAccount.ts` + `.test.tsx`. Hata yardımcıları `apps/panel/src/lib/apiError.ts` (`problemMessage`, `problemTitle`).

- [ ] **Adım 1: Dalı aç** (OrderDeck-Mobile kökünde)

```bash
git fetch origin main
git switch -c feat/netgsm-kurulum-ekrani origin/main
```
(OrderDeck-Mobile'ın varsayılan dalı `main`; LiveDeck'inki `master`.)

- [ ] **Adım 2: Testi yaz (kırmızı — modül yok)**

`apps/panel/src/api/netgsmAccount.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { apiClient } from "./client";
import {
  NETGSM_ACCOUNT_KEY,
  useNetgsmAccount,
  useSaveNetgsmAccount,
  type NetgsmAccountView,
} from "./netgsmAccount";

function makeWrapper(qc: QueryClient) {
  return function wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

function newClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

/** axios'un fırlattığı hatanın test içindeki karşılığı. */
function axiosError(status: number, data: unknown = {}) {
  return Object.assign(new Error("request failed"), {
    isAxiosError: true,
    response: { status, data },
  });
}

/** Sabit parola metni yok; her koşuda üretilir. */
const PASSWORD = `pw-${crypto.randomUUID().replaceAll("-", "")}`;

const VERIFIED: NetgsmAccountView = {
  status: "verified",
  smsEnabled: true,
  userCode: "8501234567",
  header: "ORDERDECK",
  brandCode: "731734",
  passwordSet: true,
  lastError: null,
  lastVerifiedAt: "2026-09-22T09:00:00+00:00",
};

describe("useNetgsmAccount", () => {
  it("200'de görünümü döndürür", async () => {
    vi.spyOn(apiClient, "get").mockResolvedValue({ data: VERIFIED } as never);

    const { result } = renderHook(() => useNetgsmAccount(), { wrapper: makeWrapper(newClient()) });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(VERIFIED);
  });

  // Sunucu personele 403 dönüyor; yeniden denemek yalnız ekranı geciktirir.
  it("403'te hata verir ve yeniden denemez", async () => {
    const get = vi.spyOn(apiClient, "get").mockRejectedValue(axiosError(403, { title: "owner-only" }));

    const { result } = renderHook(() => useNetgsmAccount(), { wrapper: makeWrapper(newClient()) });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(get).toHaveBeenCalledTimes(1);
  });
});

describe("useSaveNetgsmAccount", () => {
  it("başarıda görünümü cache'e yazar", async () => {
    vi.spyOn(apiClient, "put").mockResolvedValue({ data: VERIFIED } as never);
    const qc = newClient();

    const { result } = renderHook(() => useSaveNetgsmAccount(), { wrapper: makeWrapper(qc) });
    await result.current.mutateAsync({
      userCode: "8501234567",
      password: PASSWORD,
      header: "ORDERDECK",
      brandCode: "731734",
    });

    expect(qc.getQueryData(NETGSM_ACCOUNT_KEY)).toEqual(VERIFIED);
  });

  // Sunucu `password: null` = "değiştirme". Boş string göndermek sunucuda
  // `password-required`/geçersiz şifre olarak okunurdu.
  it("boş şifre gövdeye YAZILMAZ", async () => {
    const put = vi.spyOn(apiClient, "put").mockResolvedValue({ data: VERIFIED } as never);

    const { result } = renderHook(() => useSaveNetgsmAccount(), { wrapper: makeWrapper(newClient()) });
    await result.current.mutateAsync({
      userCode: "8501234567",
      password: "",
      header: "ORDERDECK",
      brandCode: "731734",
    });

    expect(put).toHaveBeenCalledWith("/api/panel/netgsm/account", {
      userCode: "8501234567",
      header: "ORDERDECK",
      brandCode: "731734",
    });
  });
});
```

- [ ] **Adım 3: Koştur — KIRMIZI**

`cd apps/panel && npx vitest run src/api/netgsmAccount.test.tsx` → FAIL (modül çözülemez).

- [ ] **Adım 4: Modülü yaz**

`apps/panel/src/api/netgsmAccount.ts`:

```ts
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import axios from "axios";
import { apiClient } from "./client";
import { problemTitle } from "../lib/apiError";

/** Sunucu görünümü (PanelNetgsmAccountController.AccountView). Şifre asla
 *  gelmez; yalnız `passwordSet`. */
export type NetgsmAccountStatus = "none" | "failed" | "verified" | "disabled";

export type NetgsmAccountView = {
  status: NetgsmAccountStatus;
  /** Yetki tablosunun tek cevabı: kampanya gönderimi yalnız bu true iken açık. */
  smsEnabled: boolean;
  userCode: string | null;
  header: string | null;
  brandCode: string | null;
  passwordSet: boolean;
  lastError: string | null;
  lastVerifiedAt: string | null;
};

export type NetgsmSaveInput = {
  userCode: string;
  /** İlk kayıtta zorunlu; sonra boş bırakılırsa mevcut şifre korunur. */
  password?: string;
  header: string;
  brandCode: string;
};

export const NETGSM_ACCOUNT_KEY = ["netgsm-account"] as const;

function statusOf(error: unknown): number | null {
  return axios.isAxiosError(error) ? (error.response?.status ?? null) : null;
}

/** 400 (lisans yok) ve 403 (personel) kalıcı — tekrar denemek yalnız ekranı geciktirir. */
function retryTransientOnly(failureCount: number, error: unknown): boolean {
  const status = statusOf(error);
  if (status !== null && status < 500 && status !== 429) return false;
  return failureCount < 2;
}

export function useNetgsmAccount() {
  return useQuery({
    queryKey: NETGSM_ACCOUNT_KEY,
    queryFn: async (): Promise<NetgsmAccountView> => {
      const resp = await apiClient.get<NetgsmAccountView>("/api/panel/netgsm/account");
      return resp.data;
    },
    staleTime: 60_000,
    retry: retryTransientOnly,
  });
}

/**
 * PUT senkron doğrular (İYS'ye üç salt-okuma çağrısı) ve sonucu görünümle döner;
 * doğrulama düşse de 200 gelir, `status: "failed"` + `lastError` ile.
 */
export function useSaveNetgsmAccount() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (input: NetgsmSaveInput): Promise<NetgsmAccountView> => {
      const body: Record<string, string> = {
        userCode: input.userCode,
        header: input.header,
        brandCode: input.brandCode,
      };
      // Sunucu `password` yoksa mevcut şifreyi korur; boş string göndermek
      // "şifreyi boşa çevir" diye okunurdu.
      if (input.password) body.password = input.password;
      const resp = await apiClient.put<NetgsmAccountView>("/api/panel/netgsm/account", body);
      return resp.data;
    },
    // Sunucu güncel görünümü zaten döndürüyor; ikinci bir GET gereksiz tur.
    onSuccess: (account) => {
      qc.setQueryData(NETGSM_ACCOUNT_KEY, account);
    },
    // Yarış cevapları: başka sekme kaydetti / doğrulama sürerken değişti —
    // ekran güncel hâli çeksin, kullanıcı ona göre tekrar kaydetsin.
    onError: (error) => {
      const title = problemTitle(error);
      if (title === "verification-superseded" || title === "netgsm-account-concurrent-create") {
        void qc.invalidateQueries({ queryKey: NETGSM_ACCOUNT_KEY });
      }
    },
  });
}
```

- [ ] **Adım 5: Koştur — YEŞİL**

`npx vitest run src/api/netgsmAccount.test.tsx` → 4/4 PASS.

- [ ] **Adım 6: Commit**

```bash
git add apps/panel/src/api/netgsmAccount.ts apps/panel/src/api/netgsmAccount.test.tsx
git commit -m "feat(panel-netgsm): Netgsm hesabı API modülü (GET/PUT, kalıcı hatalarda retry yok)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 6: `NetgsmKurulumScreen` — durum kartı, form, rehber

**Files:**
- Oluştur: `apps/panel/src/screens/NetgsmKurulumScreen.tsx`
- Test (YENİ): `apps/panel/src/screens/NetgsmKurulumScreen.test.tsx`

Kalıp: `WhatsAppBaglaScreen.tsx` (başlık/geri bağlantısı, `Notice`, hata kutusu sınıfları) ve `WhatsAppMesajSablonScreen.tsx` (`Field`, input sınıfları).

- [ ] **Adım 1: Testi yaz (kırmızı — ekran yok)**

`apps/panel/src/screens/NetgsmKurulumScreen.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { NetgsmAccountView } from "../api/netgsmAccount";
import { NetgsmKurulumScreen } from "./NetgsmKurulumScreen";

// `data`/`error` test içinde yeniden atanıyor; tip açık yazılmazsa TS ilk
// değerden literal çıkarır ve atamalar patlar.
const api = vi.hoisted(() => ({
  account: {
    data: undefined as NetgsmAccountView | undefined,
    isLoading: false,
    isError: false,
    error: null as unknown,
    refetch: vi.fn(),
  },
  save: { mutateAsync: vi.fn(), isPending: false },
}));
vi.mock("../api/netgsmAccount", () => ({
  useNetgsmAccount: () => api.account,
  useSaveNetgsmAccount: () => api.save,
}));

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <NetgsmKurulumScreen />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function view(overrides: Partial<NetgsmAccountView>): NetgsmAccountView {
  return {
    status: "none",
    smsEnabled: false,
    userCode: null,
    header: null,
    brandCode: null,
    passwordSet: false,
    lastError: null,
    lastVerifiedAt: null,
    ...overrides,
  };
}

/** Sabit parola metni yok; her koşuda üretilir. */
const PASSWORD = `pw-${crypto.randomUUID().replaceAll("-", "")}`;

const VERIFIED = view({
  status: "verified",
  smsEnabled: true,
  userCode: "8501234567",
  header: "ORDERDECK",
  brandCode: "731734",
  passwordSet: true,
  lastVerifiedAt: "2026-09-22T09:00:00+00:00",
});

async function fillForm(u: ReturnType<typeof userEvent.setup>, password: string | null) {
  await u.clear(screen.getByLabelText("Netgsm abone numarası"));
  await u.type(screen.getByLabelText("Netgsm abone numarası"), "8501234567");
  if (password !== null) {
    await u.clear(screen.getByLabelText("Netgsm API şifresi"));
    if (password) await u.type(screen.getByLabelText("Netgsm API şifresi"), password);
  }
  await u.clear(screen.getByLabelText("Gönderici başlığı"));
  await u.type(screen.getByLabelText("Gönderici başlığı"), "ORDERDECK");
  await u.clear(screen.getByLabelText("İYS marka kodu"));
  await u.type(screen.getByLabelText("İYS marka kodu"), "731734");
}

describe("NetgsmKurulumScreen", () => {
  beforeEach(() => {
    api.account.data = view({});
    api.account.isLoading = false;
    api.account.isError = false;
    api.account.error = null;
    api.save.mutateAsync = vi.fn().mockResolvedValue(VERIFIED);
    api.save.isPending = false;
  });

  it("kurulum yokken nötr kart ve boş form", () => {
    renderScreen();
    expect(screen.getByText("Henüz kurulum yok")).toBeInTheDocument();
    expect(screen.getByLabelText("Netgsm abone numarası")).toHaveValue("");
    expect(screen.getByRole("button", { name: "Kaydet ve doğrula" })).toBeInTheDocument();
  });

  it("doğrulanmamış kurulumda sarı kart ve sunucunun hata metni", () => {
    api.account.data = view({
      status: "failed",
      userCode: "8501234567",
      header: "ORDERDECK",
      brandCode: "731734",
      passwordSet: true,
      lastError: "İYS beklenmeyen yanıt kodu döndürdü (70).",
    });
    renderScreen();
    expect(screen.getByText("Doğrulanmadı")).toBeInTheDocument();
    expect(screen.getByText(/beklenmeyen yanıt kodu döndürdü \(70\)/)).toBeInTheDocument();
    expect(screen.getByLabelText("Netgsm abone numarası")).toHaveValue("8501234567");
  });

  it("doğrulanmış kurulumda yeşil kart, tarih ve 'Bilgileri güncelle'", () => {
    api.account.data = VERIFIED;
    renderScreen();
    expect(screen.getByText(/Doğrulandı — kampanya gönderimi açık/)).toBeInTheDocument();
    expect(screen.getByText(/731734/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Bilgileri güncelle" })).toBeInTheDocument();
  });

  it("kapatılmış kurulumda kırmızı kart, form yok", () => {
    api.account.data = view({ status: "disabled", userCode: "8501234567", passwordSet: true });
    renderScreen();
    expect(screen.getByText(/yönetici tarafından kapatıldı/)).toBeInTheDocument();
    expect(screen.queryByLabelText("Netgsm abone numarası")).toBeNull();
  });

  it("kaydet → PUT gövdesi dört alan", async () => {
    const u = userEvent.setup();
    renderScreen();
    await fillForm(u, PASSWORD);
    await u.click(screen.getByRole("button", { name: "Kaydet ve doğrula" }));

    await waitFor(() =>
      expect(api.save.mutateAsync).toHaveBeenCalledWith({
        userCode: "8501234567",
        password: PASSWORD,
        header: "ORDERDECK",
        brandCode: "731734",
      }),
    );
  });

  // İlk kayıtta şifre zorunlu (sunucu da `password-required` ile reddeder);
  // istemcide kesmek gereksiz bir doğrulama turunu (üç İYS çağrısı) önler.
  it("ilk kayıtta boş şifre → istemci hatası, PUT çağrılmaz", async () => {
    const u = userEvent.setup();
    renderScreen();
    await fillForm(u, "");
    await u.click(screen.getByRole("button", { name: "Kaydet ve doğrula" }));

    expect(await screen.findByText(/İlk kayıtta Netgsm API şifresi zorunlu/)).toBeInTheDocument();
    expect(api.save.mutateAsync).not.toHaveBeenCalled();
  });

  it("şifre kayıtlıyken boş bırakılırsa gövdede şifre olmaz", async () => {
    api.account.data = VERIFIED;
    const u = userEvent.setup();
    renderScreen();
    await u.click(screen.getByRole("button", { name: "Bilgileri güncelle" }));

    await waitFor(() =>
      expect(api.save.mutateAsync).toHaveBeenCalledWith({
        userCode: "8501234567",
        password: undefined,
        header: "ORDERDECK",
        brandCode: "731734",
      }),
    );
  });

  it("sunucu hatasında detail metni gösterilir", async () => {
    api.save.mutateAsync = vi.fn().mockRejectedValue(
      Object.assign(new Error("request failed"), {
        isAxiosError: true,
        response: {
          status: 409,
          data: { title: "brand-code-taken", detail: "Bu İYS marka kodu başka bir hesapta doğrulanmış durumda." },
        },
      }),
    );
    const u = userEvent.setup();
    renderScreen();
    await fillForm(u, PASSWORD);
    await u.click(screen.getByRole("button", { name: "Kaydet ve doğrula" }));

    expect(
      await screen.findByText("Bu İYS marka kodu başka bir hesapta doğrulanmış durumda."),
    ).toBeInTheDocument();
  });

  it("kaydetme sürerken düğme 'Doğrulanıyor…' ve devre dışı", () => {
    api.save.isPending = true;
    renderScreen();
    expect(screen.getByRole("button", { name: "Doğrulanıyor…" })).toBeDisabled();
  });

  it("okuma hatasında ErrorView", () => {
    api.account.data = undefined;
    api.account.isError = true;
    api.account.error = Object.assign(new Error("x"), {
      response: { status: 403, data: { title: "owner-only", detail: "Yalnız hesap sahibi." } },
    });
    renderScreen();
    expect(screen.getByText("Yalnız hesap sahibi.")).toBeInTheDocument();
  });
});
```

- [ ] **Adım 2: Koştur — KIRMIZI**

`npx vitest run src/screens/NetgsmKurulumScreen.test.tsx` → FAIL (ekran yok).

- [ ] **Adım 3: Ekranı yaz**

`apps/panel/src/screens/NetgsmKurulumScreen.tsx`:

```tsx
import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { ErrorView, LoadingView } from "@orderdeck/shared-ui";
import {
  useNetgsmAccount,
  useSaveNetgsmAccount,
  type NetgsmAccountView,
} from "../api/netgsmAccount";
import { problemMessage } from "../lib/apiError";

type FormState = { userCode: string; password: string; header: string; brandCode: string };

const INPUT =
  "w-full rounded-xl border border-bg-elevated bg-bg-surface px-3 py-2.5 outline-none focus:border-accent";

export function NetgsmKurulumScreen() {
  const account = useNetgsmAccount();
  const save = useSaveNetgsmAccount();
  const [form, setForm] = useState<FormState | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Formu sunucudan gelen değerlerle BİR KEZ doldur; arka plandaki yenileme
  // yayıncının yazmakta olduğu alanları ezmesin.
  useEffect(() => {
    if (account.data && form === null) {
      setForm({
        userCode: account.data.userCode ?? "",
        password: "",
        header: account.data.header ?? "",
        brandCode: account.data.brandCode ?? "",
      });
    }
  }, [account.data, form]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!form || save.isPending) return;
    setError(null);

    const passwordSet = account.data?.passwordSet ?? false;
    const password = form.password.trim();
    if (!passwordSet && password === "") {
      // Sunucu da `password-required` ile reddeder; burada kesmek gereksiz bir
      // doğrulama turunu (üç İYS çağrısı) önler.
      setError("İlk kayıtta Netgsm API şifresi zorunlu.");
      return;
    }

    try {
      await save.mutateAsync({
        userCode: form.userCode.trim(),
        password: password || undefined,
        header: form.header.trim(),
        brandCode: form.brandCode.trim(),
      });
      // Şifre alanı bellekte kalmasın; diğer alanlar sunucunun döndürdüğü
      // görünümle zaten aynı.
      setForm((f) => (f ? { ...f, password: "" } : f));
    } catch (err) {
      // Sunucu Türkçe `detail` yazıyor (brand-code-taken, invalid-header, ...);
      // slug→metin sözlüğü tutmak o bilgiyi kaybederdi.
      setError(problemMessage(err, "Kurulum kaydedilemedi."));
    }
  }

  const data = account.data;
  const verified = data?.status === "verified";

  return (
    <main className="px-5 pt-6 pb-12">
      <header className="mb-5">
        <Link to="/daha-fazla" className="text-xs text-text-muted hover:text-text">
          ← Geri
        </Link>
        <h1 className="mt-1 text-2xl font-bold">SMS ve İYS Kurulumu</h1>
        <p className="mt-0.5 text-sm text-text-muted">
          Kampanya SMS'leri senin Netgsm hesabından, senin başlığınla gider; onaylar senin İYS
          markana yazılır.
        </p>
      </header>

      {account.isLoading ? (
        <LoadingView />
      ) : account.isError ? (
        <ErrorView
          message={problemMessage(account.error, "Kurulum bilgisi alınamadı.")}
          onRetry={() => void account.refetch()}
        />
      ) : data && form ? (
        <>
          <StatusCard account={data} />

          {data.status !== "disabled" && (
            <form onSubmit={(e) => void submit(e)} className="mt-4">
              <Field label="Netgsm abone numarası" hint="Netgsm panelindeki abone (kullanıcı) numaran.">
                <input
                  aria-label="Netgsm abone numarası"
                  value={form.userCode}
                  maxLength={32}
                  inputMode="numeric"
                  autoComplete="off"
                  onChange={(e) => setForm({ ...form, userCode: e.target.value })}
                  className={INPUT}
                />
              </Field>
              <Field
                label="Netgsm API şifresi"
                hint={
                  data.passwordSet
                    ? "Kayıtlı. Değiştirmek için doldur; boş bırakırsan aynı kalır."
                    : "İlk kayıtta zorunlu. Panelde saklanmaz, yalnız sunucuya gider."
                }
              >
                <input
                  aria-label="Netgsm API şifresi"
                  type="password"
                  value={form.password}
                  autoComplete="off"
                  placeholder={data.passwordSet ? "Değiştirmek için doldur" : ""}
                  onChange={(e) => setForm({ ...form, password: e.target.value })}
                  className={INPUT}
                />
              </Field>
              <Field label="Gönderici başlığı" hint="Netgsm'de onaylı başlığın (en fazla 11 karakter).">
                <input
                  aria-label="Gönderici başlığı"
                  value={form.header}
                  maxLength={11}
                  autoComplete="off"
                  onChange={(e) => setForm({ ...form, header: e.target.value })}
                  className={INPUT}
                />
              </Field>
              <Field label="İYS marka kodu" hint="6 haneli İYS marka kodun; yalnız rakam.">
                <input
                  aria-label="İYS marka kodu"
                  value={form.brandCode}
                  inputMode="numeric"
                  pattern="[0-9]*"
                  autoComplete="off"
                  onChange={(e) => setForm({ ...form, brandCode: e.target.value })}
                  className={INPUT}
                />
              </Field>

              <button
                type="submit"
                disabled={save.isPending}
                className="flex w-full items-center justify-center rounded-[14px] bg-accent py-3 text-sm font-semibold text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
              >
                {save.isPending ? "Doğrulanıyor…" : verified ? "Bilgileri güncelle" : "Kaydet ve doğrula"}
              </button>
              <p className="mt-2 px-1 text-xs text-text-muted">
                Kaydedince bilgiler İYS'de anında doğrulanır; birkaç saniye sürebilir.
              </p>
            </form>
          )}

          <Guide />
        </>
      ) : null}

      {error && (
        <div className="mt-4 rounded-xl border border-danger/30 bg-danger/10 p-3 text-sm text-danger">
          {error}
        </div>
      )}
    </main>
  );
}

function StatusCard({ account }: { account: NetgsmAccountView }) {
  switch (account.status) {
    case "verified":
      return (
        <section className="rounded-2xl border border-success/30 bg-success/[0.08] p-4">
          <p className="text-[11px] font-semibold uppercase tracking-[0.08em] text-success">
            Doğrulandı — kampanya gönderimi açık
          </p>
          <p className="mt-1 text-sm">
            Abone {account.userCode} · Başlık {account.header} · Marka {account.brandCode}
          </p>
          {account.lastVerifiedAt && (
            <p className="mt-2 text-[11px] text-text-muted">
              {new Date(account.lastVerifiedAt).toLocaleString("tr-TR")} tarihinde doğrulandı
            </p>
          )}
        </section>
      );
    case "failed":
      return (
        <Notice tone="warning">
          <p className="font-semibold">Doğrulanmadı</p>
          {account.lastError && <p className="mt-1">{account.lastError}</p>}
          <p className="mt-1">Bilgileri düzeltip tekrar kaydet.</p>
        </Notice>
      );
    case "disabled":
      return (
        <Notice tone="danger">
          Kurulum yönetici tarafından kapatıldı. Destekle iletişime geç.
        </Notice>
      );
    default:
      return (
        <section className="rounded-2xl border border-border bg-bg-surface p-4">
          <p className="text-sm font-semibold">Henüz kurulum yok</p>
          <p className="mt-1 text-sm text-text-muted">
            Aşağıdaki bilgileri girip kaydet; doğrulama başarılı olunca kampanya SMS'leri açılır.
          </p>
        </section>
      );
  }
}

function Guide() {
  return (
    <details className="mt-6 rounded-2xl border border-border bg-bg-surface p-4">
      <summary className="cursor-pointer text-sm font-semibold">Bilgileri nereden alırım?</summary>
      <ol className="mt-3 list-decimal space-y-2 pl-5 text-sm text-text-muted">
        <li>
          <b>Netgsm aboneliği:</b> abone numarası ve API şifresi Netgsm panelinden alınır; API
          erişimi açık olmalı.
        </li>
        <li>
          <b>Gönderici başlığı:</b> Netgsm'de onaylanmış başlık (en fazla 11 karakter). Marka
          tescili şart değil; Netgsm başvuruda kabul ettiği belgeleri listeler.
        </li>
        <li>
          <b>İYS marka kodu:</b> iys.org.tr üzerinde markanı kaydet ve marka kodunu buraya yaz;
          Netgsm'in İYS entegrasyonu bu markaya bağlanır.
        </li>
      </ol>
      <p className="mt-3 text-sm text-text-muted">
        Doğrulama başarılı olunca İYS'deki mevcut onayların otomatik olarak eşitlenir; ayrıca her
        gece yeni müşteriler için tekrar sorgulanır.
      </p>
    </details>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="mb-4">
      <p className="mb-1 text-[13px] font-medium">{label}</p>
      {children}
      {hint && <p className="mt-1 text-xs text-text-muted">{hint}</p>}
    </div>
  );
}

/** Sarı = koşul (doğrulanmadı), kırmızı = kapalı. `ErrorView` "istek düştü"
 *  anlamı taşıdığı için burada kullanılmıyor. */
function Notice({ tone, children }: { tone: "warning" | "danger"; children: ReactNode }) {
  const cls =
    tone === "warning"
      ? "border-warning/30 bg-warning/10 text-warning"
      : "border-danger/30 bg-danger/10 text-danger";
  return <div className={`rounded-xl border p-3 text-sm ${cls}`}>{children}</div>;
}
```

- [ ] **Adım 4: Koştur — YEŞİL**

`npx vitest run src/screens/NetgsmKurulumScreen.test.tsx` → 10/10 PASS. Ardından `npm run -s typecheck` → temiz; `npx eslint src/screens/NetgsmKurulumScreen.tsx src/screens/NetgsmKurulumScreen.test.tsx` → hata yok.

- [ ] **Adım 5: Commit**

```bash
git add apps/panel/src/screens/NetgsmKurulumScreen.tsx apps/panel/src/screens/NetgsmKurulumScreen.test.tsx
git commit -m "feat(panel-netgsm): SMS ve İYS kurulum ekranı — durum kartı, doğrulamalı form, rehber

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 7: Rota + "Daha Fazla" menü girişi (yalnız owner)

**Files:**
- Değiştir: `apps/panel/src/router.tsx` (import listesi; rota listesi `/whatsapp-bagla` satırının altı)
- Değiştir: `apps/panel/src/screens/DahaFazlaScreen.tsx` (lucide import; `WhatsApp Bağla` NavRow bloğunun altı)
- Değiştir: `apps/panel/src/screens/DahaFazlaScreen.test.tsx`

- [ ] **Adım 1: Menü testini yaz (kırmızı)**

`DahaFazlaScreen.test.tsx` dosyasının sonuna yeni `describe`:

```tsx
describe("DahaFazlaScreen — SMS ve İYS kurulum girişi", () => {
  beforeEach(() => {
    tokenMock.current = { email: "sahip@example.com", principal: undefined };
  });

  it("hesap sahibine kurulum satırını gösterir", () => {
    renderScreen();
    const link = screen.getByRole("link", { name: /SMS ve İYS Kurulumu/i });
    expect(link).toHaveAttribute("href", "/netgsm-kurulum");
  });

  // Sunucu operatöre 403 dönüyor; satırı göstermek tıklayınca duvara toslayan
  // bir menü öğesi sunmak olurdu.
  it("personele göstermez", () => {
    tokenMock.current = { email: "personel@example.com", principal: "operator" };
    renderScreen();
    expect(screen.queryByRole("link", { name: /SMS ve İYS Kurulumu/i })).toBeNull();
  });
});
```

- [ ] **Adım 2: Koştur — KIRMIZI**

`npx vitest run src/screens/DahaFazlaScreen.test.tsx` → ilk yeni test FAIL (satır yok).

- [ ] **Adım 3: Menü + rota**

`DahaFazlaScreen.tsx` — lucide import listesine `Send` ekle (alfabetik yer fark etmez, mevcut listeye bir satır):

```tsx
  Send,
```

`WhatsApp Bağla` NavRow bloğunun (`{principal !== "operator" && ( <NavRow to="/whatsapp-bagla" ... /> )}`) hemen altına:

```tsx
        {principal !== "operator" && (
          <NavRow
            to="/netgsm-kurulum"
            label="SMS ve İYS Kurulumu"
            hint="Netgsm hesabını bağla, kampanya SMS'leri senden gitsin"
            Icon={Send}
          />
        )}
```

`router.tsx` — import listesine (`WhatsAppBaglaScreen` satırının altına):

```tsx
import { NetgsmKurulumScreen } from "./screens/NetgsmKurulumScreen";
```

rota listesine (`{ path: "/whatsapp-bagla", element: <WhatsAppBaglaScreen /> },` satırının altına):

```tsx
          { path: "/netgsm-kurulum", element: <NetgsmKurulumScreen /> },
```

- [ ] **Adım 4: Koştur — YEŞİL**

`npx vitest run src/screens/DahaFazlaScreen.test.tsx` → PASS; `npm run -s typecheck` → temiz.

- [ ] **Adım 5: Commit**

```bash
git add apps/panel/src/router.tsx apps/panel/src/screens/DahaFazlaScreen.tsx apps/panel/src/screens/DahaFazlaScreen.test.tsx
git commit -m "feat(panel-netgsm): /netgsm-kurulum rotası ve Daha Fazla menüsünde owner-only giriş

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 8: Panel tam paket + PR

- [ ] **Adım 1:** `cd apps/panel && npx vitest run && npm run -s typecheck` → temiz; ardından repo kökünden `npm run -s lint` (`eslint .`; panel paketinde ayrı lint script'i yok) → temiz.
- [ ] **Adım 2:** Push + PR (`gh pr create --base main`; Mobile'ın varsayılanı `main`): ekran görüntüsü yerine durumların listesi, "sunucu #… deploy'undan sonra merge" notu, cihazda doğrulama maddeleri (kapalı/doğrulanmamış/doğrulanmış kart; ilk kayıtta boş şifre hatası; operatörde menü yok).

---

## Öz-inceleme (plan yazıldıktan sonra yapıldı)

- **Spec kapsaması:** §2.1 → Görev 1; §2.2 → Görev 2-3; §2.3 → Görev 1-3 testleri; §3.1 → Görev 5; §3.2 (durum kartı, form kuralları, rehber, erişim) → Görev 6-7; §3.3 → Görev 5-7 testleri; §4 → Görev 4 ve 8.
- **Yer tutucu taraması:** yok.
- **Tip tutarlılığı:** `NetgsmAccountView`/`NetgsmSaveInput`/`NETGSM_ACCOUNT_KEY` Görev 5'te tanımlı, 6'da aynı adlarla kullanılıyor; `IysMirrorSyncJob.RunAsync(CancellationToken)` Görev 2'de tanımlı, 3'te aynı imzayla zamanlanıyor; test etiketleri (`aria-label`) ile ekran birebir.
