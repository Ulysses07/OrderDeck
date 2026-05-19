# Hızlı İstatistik (PIN Korumalı) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mobile panele PIN/biyometrik korumalı bir performans dashboard'u ekle (bugün/hafta/ay ciro + sipariş + AOV + iptal oranı + aktif yayın + bekleyen dekont/kargo) ve AnaScreen totalAmount card'ını aynı koruma altına al.

**Architecture:** Server'da yeni `PanelStatsController` runtime aggregate ile metrikleri döner (broadcast posts/müşteriler pattern'i). Mobile'da `capacitor-native-biometric` plugin + `lib/pin.ts` SHA-256+salt hash + `Capacitor Preferences` ile PIN persist + 5 dk in-memory unlock window. `PinGate` reusable component iki variant (full-screen `screen` ve `inline blurContent`) sağlar.

**Tech Stack:** .NET 10 + EF Core + SQL Server (server). React + Vite + TanStack Query + Capacitor 6 + `capacitor-native-biometric@^5` + Web Crypto API (mobile).

**Spec:** `docs/superpowers/specs/2026-05-19-hizli-istatistik-design.md`

**Branches:**
- Server: `feat/hizli-istatistik-server` in LiveDeck (master'dan)
- Mobile: `feat/hizli-istatistik` in OrderDeck-Mobile (main'den)

**Mevcut DB index'leri** — endpoint sadece okuma yapacak; Orders ve StreamSessions üzerinde gerekli indexler (LicenseId + tarih) zaten mevcut (broadcast posts/müşteriler için aynı pattern). Migration gerekmez.

---

## Server tasks (LiveDeck repo, branch `feat/hizli-istatistik-server`)

### Task 1: Stats endpoint + DTOs + 3 baseline test

**Goal:** Endpoint'i çalıştır + happy path (`today` range), `cross-tenant`, `activeStream null` testleri yeşil.

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelStatsController.cs`
- Create: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStatsControllerTests.cs`

- [ ] **Step 1: Branch aç**

```bash
cd C:/Users/burak/source/repos/LiveDeck
git checkout master && git pull origin master --ff-only
git checkout -b feat/hizli-istatistik-server
```

- [ ] **Step 2: Controller'ı oluştur**

`OrderDeck.LicenseServer/Controllers/Panel/PanelStatsController.cs` (yeni dosya):

```csharp
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Mobile Panel "Hızlı istatistik" dashboard endpoint'i.
/// Range bazlı (today/week/month) aggregate metrikler:
/// ciro, sipariş sayısı, AOV, iptal oranı, aktif yayın, bekleyen dekont/kargo.
/// "Real sale" tanımı PanelCustomersController.Get(id) ile aynı:
/// PrintedAt != null && CancelledAt == null && !IsShippingFee && !IsTentativeBackup.
/// </summary>
[ApiController]
[Route("api/panel/stats")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelStatsController : ControllerBase
{
    private static readonly TimeZoneInfo TurkeyTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Turkey Standard Time" : "Europe/Istanbul");

    private readonly LicenseDbContext _db;

    public PanelStatsController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record StatsResponse(
        string Range,
        DateTimeOffset RangeStart,
        DateTimeOffset RangeEnd,
        decimal Revenue,
        int OrderCount,
        decimal AverageOrderValue,
        decimal CancelRate,
        Guid? ActiveStreamId,
        string? ActiveStreamTitle,
        int PendingPaymentCount,
        int PendingShipmentCount,
        DateTimeOffset LastUpdatedAt);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? range, CancellationToken ct)
    {
        var rangeMode = range?.ToLowerInvariant() switch
        {
            null or "" or "today" => "today",
            "week" => "week",
            "month" => "month",
            _ => null
        };
        if (rangeMode is null)
            return Problem(title: "invalid-range", statusCode: 400);

        // TR timezone'a göre range cutoff hesapla, sonra UTC'ye çevirip DB filter
        var utcNow = DateTimeOffset.UtcNow;
        var trNow = TimeZoneInfo.ConvertTime(utcNow, TurkeyTz);
        var (rangeStartTr, rangeEndTr) = ComputeRange(rangeMode, trNow);
        var rangeStartUtc = rangeStartTr.ToUniversalTime();
        var rangeEndUtc = rangeEndTr.ToUniversalTime();

        var authCustomerId = User.GetTenantCustomerId();
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == authCustomerId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        if (licenseIds.Count == 0)
            return Ok(EmptyResponse(rangeMode, rangeStartTr, rangeEndTr, utcNow));

        // Aggregate orders for range
        var ordersInRange = _db.Orders
            .Where(o => licenseIds.Contains(o.LicenseId)
                     && o.AddedAt >= rangeStartUtc
                     && o.AddedAt < rangeEndUtc);

        var totalRowsInRange = await ordersInRange.CountAsync(ct);
        var cancelledInRange = await ordersInRange.CountAsync(o => o.CancelledAt != null, ct);

        var realSales = ordersInRange.Where(o => o.PrintedAt != null
                                              && o.CancelledAt == null
                                              && !o.IsShippingFee
                                              && !o.IsTentativeBackup);
        var revenue = await realSales.SumAsync(o => (decimal?)o.Price, ct) ?? 0m;
        var orderCount = await realSales.CountAsync(ct);

        var aov = orderCount > 0 ? Math.Round(revenue / orderCount, 2) : 0m;
        var cancelRate = totalRowsInRange > 0
            ? Math.Round((decimal)cancelledInRange / totalRowsInRange, 4)
            : 0m;

        // Active stream (any session for this tenant where EndedAt is null)
        var activeStream = await _db.StreamSessions
            .Where(s => licenseIds.Contains(s.LicenseId) && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => new { s.Id, s.Title })
            .FirstOrDefaultAsync(ct);

        // Pending counts — range etkilemez, all-time
        var pendingPaymentCount = await _db.Payments
            .Where(p => licenseIds.Contains(p.LicenseId)
                     && p.Status == Domain.PaymentStatus.Pending)
            .CountAsync(ct);

        var pendingShipmentCount = await _db.Shipments
            .Where(s => licenseIds.Contains(s.LicenseId)
                     && (s.Status == Domain.ShipmentStatus.Held
                         || s.Status == Domain.ShipmentStatus.RecipientPays))
            .CountAsync(ct);

        return Ok(new StatsResponse(
            Range: rangeMode,
            RangeStart: rangeStartTr,
            RangeEnd: rangeEndTr,
            Revenue: revenue,
            OrderCount: orderCount,
            AverageOrderValue: aov,
            CancelRate: cancelRate,
            ActiveStreamId: activeStream?.Id,
            ActiveStreamTitle: activeStream?.Title,
            PendingPaymentCount: pendingPaymentCount,
            PendingShipmentCount: pendingShipmentCount,
            LastUpdatedAt: utcNow));
    }

    private static StatsResponse EmptyResponse(string rangeMode, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
        => new(rangeMode, start, end, 0m, 0, 0m, 0m, null, null, 0, 0, now);

    private static (DateTimeOffset start, DateTimeOffset end) ComputeRange(string mode, DateTimeOffset trNow)
    {
        // trNow's offset = TR offset (+03:00, no DST)
        var todayStart = new DateTimeOffset(trNow.Year, trNow.Month, trNow.Day, 0, 0, 0, trNow.Offset);
        return mode switch
        {
            "today" => (todayStart, trNow),
            "week"  => (todayStart.AddDays(-((int)(trNow.DayOfWeek == DayOfWeek.Sunday ? 6 : trNow.DayOfWeek - DayOfWeek.Monday))), trNow),
            "month" => (new DateTimeOffset(trNow.Year, trNow.Month, 1, 0, 0, 0, trNow.Offset), trNow),
            _ => throw new InvalidOperationException()
        };
    }
}
```

**Notlar:**
- `Domain.PaymentStatus` enum mevcut (PanelPaymentsController kullanıyor); aynı şekilde `Domain.ShipmentStatus` mevcut (PanelShipmentsController).
- `User.GetTenantCustomerId()` extension method'u mevcut (broadcast posts ve müşteriler kullanıyor).
- Turkey timezone'u Linux'ta (CI/prod docker) "Europe/Istanbul", Windows'ta "Turkey Standard Time" olarak adlanıyor — `OperatingSystem.IsWindows()` ile seçim.
- Cancel rate `totalRowsInRange` denominator'lı: range içindeki TÜM satır (gerçek satış değil) içinde iptaller. Spec'le tutarlı.

- [ ] **Step 3: Test dosyasını oluştur**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStatsControllerTests.cs` (yeni):

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelStatsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelStatsControllerTests(ApiFactory f) => _factory = f;

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-STAT-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static Order MakeOrder(Guid licenseId, decimal price, DateTimeOffset addedAt,
        bool printed = true, bool cancelled = false, bool shippingFee = false, bool tentative = false)
    {
        return new Order
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            CustomerId = "test-cust", Platform = "instagram",
            Username = "@test", DisplayName = "Test",
            MessageText = "x", Price = price,
            AddedAt = addedAt,
            PrintedAt = printed ? addedAt : null,
            CancelledAt = cancelled ? addedAt : null,
            IsShippingFee = shippingFee,
            IsTentativeBackup = tentative,
            UpdatedAt = addedAt
        };
    }

    [Fact]
    public async Task Stats_today_revenue_orderCount_correct()
    {
        var (client, licenseId) = await SeedAsync();
        var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
        // 3 satış bugün, 1 dün (range dışı)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeOrder(licenseId, 100m, trNow.AddHours(-2).UtcDateTime),
                MakeOrder(licenseId, 250m, trNow.AddHours(-3).UtcDateTime),
                MakeOrder(licenseId, 150m, trNow.AddHours(-1).UtcDateTime),
                MakeOrder(licenseId, 999m, trNow.AddDays(-2).UtcDateTime)); // dün, range dışı
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/stats?range=today");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(500m);
        doc.RootElement.GetProperty("orderCount").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("range").GetString().Should().Be("today");
    }

    [Fact]
    public async Task Stats_activeStream_null_when_no_live_session()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/stats?range=today");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var activeId = doc.RootElement.GetProperty("activeStreamId");
        activeId.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Stats_cross_tenant_returns_zero()
    {
        var (clientA, licenseA) = await SeedAsync();
        var (clientB, _) = await SeedAsync();
        var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));

        // Tenant A için sipariş
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.Add(MakeOrder(licenseA, 500m, trNow.AddHours(-1).UtcDateTime));
            await db.SaveChangesAsync();
        }

        // Tenant B sorgular — A'nın satışını görmemeli
        var resp = await clientB.GetAsync("/api/panel/stats?range=today");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(0m);
        doc.RootElement.GetProperty("orderCount").GetInt32().Should().Be(0);
    }
}
```

- [ ] **Step 4: Build + test**

```bash
cd C:/Users/burak/source/repos/LiveDeck
dotnet build OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj -nologo -clp:NoSummary
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelStats" --nologo
```

Beklenen: 3/3 PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelStatsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStatsControllerTests.cs
git commit -m "feat(stats): GET /api/panel/stats endpoint + range aggregate"
```

---

### Task 2: Real-sale filter + cancel rate + week/month range tests

**Files:**
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStatsControllerTests.cs`

Controller değişmiyor — sadece test ekliyoruz.

- [ ] **Step 1: 3 yeni test ekle (test dosyasının class'ının sonuna, kapanış `}` ÖNCESİNE)**

```csharp
[Fact]
public async Task Stats_today_excludes_cancelled_and_shipping_fees()
{
    var (client, licenseId) = await SeedAsync();
    var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Orders.AddRange(
            // Real sale → sayılmalı
            MakeOrder(licenseId, 100m, trNow.AddHours(-1).UtcDateTime),
            // Cancelled → sayılmamalı
            MakeOrder(licenseId, 500m, trNow.AddHours(-2).UtcDateTime, cancelled: true),
            // Shipping fee → sayılmamalı
            MakeOrder(licenseId, 50m,  trNow.AddHours(-3).UtcDateTime, shippingFee: true),
            // Tentative backup → sayılmamalı
            MakeOrder(licenseId, 999m, trNow.AddHours(-4).UtcDateTime, tentative: true));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/stats?range=today");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);

    doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(100m);
    doc.RootElement.GetProperty("orderCount").GetInt32().Should().Be(1);
    doc.RootElement.GetProperty("averageOrderValue").GetDecimal().Should().Be(100m);
}

[Fact]
public async Task Stats_cancelRate_calculation()
{
    var (client, licenseId) = await SeedAsync();
    var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // 10 toplam satır: 2 cancelled, 8 normal → cancelRate = 0.2
        for (int i = 0; i < 8; i++)
            db.Orders.Add(MakeOrder(licenseId, 100m, trNow.AddHours(-i).UtcDateTime));
        for (int i = 0; i < 2; i++)
            db.Orders.Add(MakeOrder(licenseId, 100m, trNow.AddHours(-i - 10).UtcDateTime, cancelled: true));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/stats?range=today");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);

    doc.RootElement.GetProperty("cancelRate").GetDecimal().Should().Be(0.2m);
}

[Fact]
public async Task Stats_month_includes_orders_since_first_of_month()
{
    var (client, licenseId) = await SeedAsync();
    var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Bu ayın 1'i ile bugün arası → sayılmalı
        var firstOfMonthTr = new DateTimeOffset(trNow.Year, trNow.Month, 1, 12, 0, 0, trNow.Offset);
        db.Orders.Add(MakeOrder(licenseId, 100m, firstOfMonthTr.UtcDateTime));
        // Önceki ay → sayılmamalı
        var prevMonth = firstOfMonthTr.AddMonths(-1);
        db.Orders.Add(MakeOrder(licenseId, 999m, prevMonth.UtcDateTime));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/stats?range=month");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);

    doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(100m);
    doc.RootElement.GetProperty("range").GetString().Should().Be("month");
}
```

- [ ] **Step 2: Test + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelStats" --nologo

git add OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStatsControllerTests.cs
git commit -m "test(stats): real-sale filter + cancel rate + month range"
```

Beklenen: 6/6 PASS (3 önceki + 3 yeni).

---

### Task 3: Server PR aç

- [ ] **Step 1: Push + PR**

```bash
cd C:/Users/burak/source/repos/LiveDeck
git push -u origin feat/hizli-istatistik-server

gh pr create --title "feat(stats): GET /api/panel/stats endpoint (hızlı istatistik dashboard)" --body "$(cat <<'EOF'
## Summary

Mobile panel \`Hızlı istatistik\` ekranı için server endpoint'i. Range bazlı (today/week/month) aggregate metrikler.

- 2 commit, 6 test
- TR timezone-aware range cutoffs (today=00:00 TR / week=Pzt 00:00 / month=ayın 1'i 00:00)
- "Real sale" filter: PanelCustomersController.Get(id) pattern'iyle aynı (PrintedAt+!Cancelled+!ShippingFee+!Tentative)
- Cancel rate: range içindeki tüm satır denominator'lı
- Tenant isolation single-LINQ Where (broadcast posts pattern'i)
- Pending payment/shipment counts mevcut Panel*Controller kurallarıyla aynı (all-time, range etkilemez)

Spec: \`docs/superpowers/specs/2026-05-19-hizli-istatistik-design.md\`

## Test plan

- [x] PanelStats tests: 6/6 green
- [ ] Full server suite green on CI

## Dependencies

- Mobile companion PR (OrderDeck-Mobile feat/hizli-istatistik) bu PR merge'den sonra hayata geçer

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

PR URL'ini not al.

---

## Mobile tasks (OrderDeck-Mobile repo, branch `feat/hizli-istatistik`)

### Task 4: Plugin install + AndroidManifest + lib/pin.ts

**Files:**
- Modify: `apps/panel/package.json` (capacitor-native-biometric ekle)
- Modify: `apps/panel/android/app/src/main/AndroidManifest.xml` (USE_BIOMETRIC permission)
- Create: `apps/panel/src/lib/pin.ts`

- [ ] **Step 1: Branch aç + plugin install**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git checkout main && git pull origin main --ff-only
git checkout -b feat/hizli-istatistik

cd apps/panel
npm install capacitor-native-biometric@^5
```

Eğer peer dep conflict olursa (Capacitor 6 ile uyumsuzluk):
```bash
npm install capacitor-native-biometric@^5 --legacy-peer-deps
```

(Broadcast posts'taki @capacitor/camera install ile aynı pattern.)

- [ ] **Step 2: Capacitor sync (Android plugin entegrasyonu için)**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npx cap sync android
```

- [ ] **Step 3: AndroidManifest'e USE_BIOMETRIC permission ekle**

`apps/panel/android/app/src/main/AndroidManifest.xml`'a mevcut `<uses-permission ...INTERNET />` satırının altına ekle:

```xml
    <uses-permission android:name="android.permission.USE_BIOMETRIC" />
```

(Mevcut `CAMERA`, `READ_MEDIA_IMAGES` ve `READ_MEDIA_VIDEO` izinleri zaten orada — broadcast posts'tan.)

- [ ] **Step 4: lib/pin.ts oluştur**

`apps/panel/src/lib/pin.ts` (yeni):

```typescript
import { Preferences } from "@capacitor/preferences";
import { NativeBiometric } from "capacitor-native-biometric";

const PIN_KEY = "orderdeck.pin.v1";
const UNLOCK_TIMEOUT_MS = 5 * 60_000; // 5 minutes

type PinRecord = { saltHex: string; hashHex: string };
let lastUnlockAt = 0;

// ─── Status ──────────────────────────────────────────────

export async function isPinSet(): Promise<boolean> {
  const { value } = await Preferences.get({ key: PIN_KEY });
  return value !== null;
}

export function isUnlocked(): boolean {
  return Date.now() - lastUnlockAt < UNLOCK_TIMEOUT_MS;
}

export function markUnlocked() {
  lastUnlockAt = Date.now();
}

export function lock() {
  lastUnlockAt = 0;
}

// ─── Set / verify ────────────────────────────────────────

export async function setPin(plaintext: string): Promise<void> {
  if (!/^\d{4}$/.test(plaintext)) throw new Error("PIN must be 4 digits");
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const saltHex = bytesToHex(salt);
  const hashHex = await hashPin(saltHex, plaintext);
  await Preferences.set({
    key: PIN_KEY,
    value: JSON.stringify({ saltHex, hashHex } satisfies PinRecord),
  });
  markUnlocked();
}

export async function verifyPin(plaintext: string): Promise<boolean> {
  const { value } = await Preferences.get({ key: PIN_KEY });
  if (!value) return false;
  const rec = JSON.parse(value) as PinRecord;
  const candidate = await hashPin(rec.saltHex, plaintext);
  const ok = constantTimeEqual(candidate, rec.hashHex);
  if (ok) markUnlocked();
  return ok;
}

export async function clearPin(): Promise<void> {
  await Preferences.remove({ key: PIN_KEY });
  lock();
}

// ─── Biometric ───────────────────────────────────────────

export async function biometricAvailable(): Promise<boolean> {
  try {
    const result = await NativeBiometric.isAvailable();
    return result.isAvailable;
  } catch {
    return false;
  }
}

export async function biometricPrompt(): Promise<boolean> {
  try {
    await NativeBiometric.verifyIdentity({
      reason: "Hızlı istatistiği görüntülemek için",
      title: "OrderDeck",
      subtitle: "Kimliğini doğrula",
    });
    markUnlocked();
    return true;
  } catch {
    return false;
  }
}

// ─── Helpers ─────────────────────────────────────────────

async function hashPin(saltHex: string, plaintext: string): Promise<string> {
  const data = new TextEncoder().encode(saltHex + plaintext);
  const hash = await crypto.subtle.digest("SHA-256", data);
  return bytesToHex(new Uint8Array(hash));
}

function bytesToHex(arr: Uint8Array): string {
  return Array.from(arr).map((b) => b.toString(16).padStart(2, "0")).join("");
}

function constantTimeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
```

- [ ] **Step 5: Typecheck**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npm run typecheck
```

Beklenen: clean.

- [ ] **Step 6: Commit**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git add apps/panel/package.json package-lock.json \
        apps/panel/android/app/src/main/AndroidManifest.xml \
        apps/panel/src/lib/pin.ts
git commit -m "feat(panel): PIN + biometric library + capacitor-native-biometric plugin"
```

(Eğer `cap sync` android/ altında iOS pod gibi dosyaları değiştirdiyse onları da add'le; yalnızca Android tarafında çalışıyoruz.)

---

### Task 5: PinGate component (screen + inline variants + numeric pad + setup wizard)

**Files:**
- Create: `apps/panel/src/components/PinGate.tsx`

- [ ] **Step 1: PinGate component'i oluştur**

`apps/panel/src/components/PinGate.tsx` (yeni):

```typescript
import { useEffect, useState, ReactNode } from "react";
import {
  biometricAvailable,
  biometricPrompt,
  isPinSet,
  isUnlocked,
  markUnlocked,
  setPin,
  verifyPin,
} from "../lib/pin";

type PinGateProps = {
  children: ReactNode;
  /** "screen" = full-screen overlay; "inline" = wrap child in blur+overlay */
  variant: "screen" | "inline";
  /** Inline'da child'a blur+tap-to-unlock uygula (default true for inline) */
  blurContent?: boolean;
};

type State =
  | { kind: "loading" }
  | { kind: "pinNotSet" } // setup wizard
  | { kind: "locked" }    // unlock prompt
  | { kind: "unlocked" };

export function PinGate({ children, variant, blurContent = true }: PinGateProps) {
  const [state, setState] = useState<State>({ kind: "loading" });

  // İlk açılışta status check
  useEffect(() => {
    void (async () => {
      const pinSet = await isPinSet();
      if (!pinSet) {
        setState({ kind: "pinNotSet" });
        return;
      }
      if (isUnlocked()) {
        setState({ kind: "unlocked" });
        return;
      }
      setState({ kind: "locked" });
    })();
  }, []);

  // Locked & screen variant → biometric prompt otomatik
  useEffect(() => {
    if (state.kind !== "locked" || variant !== "screen") return;
    void (async () => {
      if (await biometricAvailable()) {
        const ok = await biometricPrompt();
        if (ok) setState({ kind: "unlocked" });
      }
    })();
  }, [state.kind, variant]);

  if (state.kind === "loading") {
    return variant === "inline" ? <>{children}</> : <></>;
  }

  // PIN not set → setup wizard (only for screen variant; inline just renders raw)
  if (state.kind === "pinNotSet") {
    if (variant === "inline") return <>{children}</>;
    return <SetupWizard onDone={() => setState({ kind: "unlocked" })} />;
  }

  if (state.kind === "unlocked") {
    return <>{children}</>;
  }

  // locked
  if (variant === "screen") {
    return <UnlockScreen onUnlocked={() => setState({ kind: "unlocked" })} />;
  }

  // inline locked → blurred child + tap overlay
  return (
    <InlineLockedChrome onUnlocked={() => setState({ kind: "unlocked" })}>
      {blurContent ? <span style={{ filter: "blur(8px)" }}>{children}</span> : null}
    </InlineLockedChrome>
  );
}

// ─── Setup wizard ─────────────────────────────────────

function SetupWizard({ onDone }: { onDone: () => void }) {
  const [step, setStep] = useState<"first" | "confirm" | "biometric">("first");
  const [firstPin, setFirstPin] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [bioAvail, setBioAvail] = useState(false);

  useEffect(() => {
    void biometricAvailable().then(setBioAvail);
  }, []);

  async function handleFirst(pin: string) {
    setFirstPin(pin);
    setError(null);
    setStep("confirm");
  }

  async function handleConfirm(pin: string) {
    if (pin !== firstPin) {
      setError("PIN'ler eşleşmedi, tekrar başla.");
      setFirstPin("");
      setStep("first");
      return;
    }
    await setPin(firstPin);
    if (bioAvail) {
      setStep("biometric");
    } else {
      onDone();
    }
  }

  return (
    <div className="fixed inset-0 z-30 bg-bg flex flex-col items-center justify-center p-6">
      <h1 className="text-2xl font-bold mb-2">PIN belirle</h1>
      {step === "first" && (
        <>
          <p className="text-text-muted text-sm mb-6 text-center">
            Hassas datayı görüntülemek için 4 haneli bir PIN belirle.
          </p>
          <NumericPad onComplete={handleFirst} />
        </>
      )}
      {step === "confirm" && (
        <>
          <p className="text-text-muted text-sm mb-6 text-center">
            PIN'i tekrar gir.
          </p>
          <NumericPad onComplete={handleConfirm} />
          {error && <p className="text-danger text-sm mt-4">{error}</p>}
        </>
      )}
      {step === "biometric" && (
        <>
          <p className="text-5xl mb-3">👆</p>
          <p className="text-text font-medium mb-2">Biyometrik de kullan?</p>
          <p className="text-text-muted text-xs mb-6 text-center">
            Parmak izi veya yüz tanıma ile de açabilirsin. PIN her zaman fallback olarak çalışır.
          </p>
          <div className="flex gap-3">
            <button
              onClick={onDone}
              className="px-6 py-2 rounded-lg bg-bg-surface border border-bg-elevated text-text-muted"
            >
              Atla
            </button>
            <button
              onClick={onDone}
              className="px-6 py-2 rounded-lg bg-accent text-white font-medium"
            >
              Aç
            </button>
          </div>
        </>
      )}
    </div>
  );
}

// ─── Unlock screen (full-screen variant) ─────────────

function UnlockScreen({ onUnlocked }: { onUnlocked: () => void }) {
  const [error, setError] = useState<string | null>(null);

  async function handlePin(pin: string) {
    const ok = await verifyPin(pin);
    if (ok) {
      onUnlocked();
    } else {
      setError("PIN yanlış, tekrar dene.");
    }
  }

  async function retryBiometric() {
    setError(null);
    const ok = await biometricPrompt();
    if (ok) onUnlocked();
  }

  return (
    <div className="fixed inset-0 z-30 bg-bg flex flex-col items-center justify-center p-6">
      <p className="text-5xl mb-3">🔒</p>
      <h1 className="text-xl font-bold mb-1">Kilitli</h1>
      <p className="text-text-muted text-sm mb-6">PIN gir veya biyometrik dene.</p>
      <NumericPad onComplete={handlePin} />
      {error && <p className="text-danger text-sm mt-4">{error}</p>}
      <button
        onClick={() => void retryBiometric()}
        className="mt-6 text-text-muted text-xs underline"
      >
        Biyometrik tekrar dene
      </button>
    </div>
  );
}

// ─── Inline locked chrome (blur child + tap overlay) ──

function InlineLockedChrome({
  children,
  onUnlocked,
}: {
  children: ReactNode;
  onUnlocked: () => void;
}) {
  const [showModal, setShowModal] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function tryUnlock() {
    setError(null);
    if (await biometricAvailable()) {
      const ok = await biometricPrompt();
      if (ok) {
        onUnlocked();
        return;
      }
    }
    setShowModal(true);
  }

  async function handlePin(pin: string) {
    const ok = await verifyPin(pin);
    if (ok) {
      setShowModal(false);
      onUnlocked();
    } else {
      setError("PIN yanlış.");
    }
  }

  return (
    <span className="relative inline-block">
      {children}
      <button
        onClick={() => void tryUnlock()}
        className="absolute inset-0 flex items-center justify-center text-text-muted text-xs bg-bg-surface/50 rounded"
      >
        🔒 Göster
      </button>
      {showModal && (
        <div
          className="fixed inset-0 z-30 bg-black/60 flex items-center justify-center p-6"
          onClick={() => setShowModal(false)}
        >
          <div
            className="bg-bg-surface rounded-2xl p-6 w-full max-w-sm"
            onClick={(e) => e.stopPropagation()}
          >
            <p className="text-5xl text-center mb-3">🔒</p>
            <p className="text-center mb-4">PIN gir</p>
            <NumericPad onComplete={handlePin} />
            {error && <p className="text-danger text-sm mt-3 text-center">{error}</p>}
          </div>
        </div>
      )}
    </span>
  );
}

// ─── Numeric pad ──────────────────────────────────────

function NumericPad({ onComplete }: { onComplete: (pin: string) => void }) {
  const [pin, setPin] = useState("");

  function push(d: string) {
    if (pin.length >= 4) return;
    const next = pin + d;
    setPin(next);
    if (next.length === 4) {
      // ufak gecikme: kullanıcı son haneyi görsün
      setTimeout(() => {
        onComplete(next);
        setPin("");
      }, 100);
    }
  }

  function backspace() {
    setPin((p) => p.slice(0, -1));
  }

  return (
    <div className="flex flex-col items-center">
      {/* 4-hane göstergeleri */}
      <div className="flex gap-3 mb-6">
        {[0, 1, 2, 3].map((i) => (
          <div
            key={i}
            className={`w-3 h-3 rounded-full ${
              i < pin.length ? "bg-accent" : "bg-bg-elevated"
            }`}
          />
        ))}
      </div>

      {/* 3x4 grid: 1-9, _, 0, ⌫ */}
      <div className="grid grid-cols-3 gap-3">
        {["1", "2", "3", "4", "5", "6", "7", "8", "9"].map((d) => (
          <button
            key={d}
            onClick={() => push(d)}
            className="w-16 h-16 rounded-full bg-bg-surface border border-bg-elevated text-2xl font-medium hover:bg-bg-elevated"
          >
            {d}
          </button>
        ))}
        <div /> {/* boş hücre */}
        <button
          onClick={() => push("0")}
          className="w-16 h-16 rounded-full bg-bg-surface border border-bg-elevated text-2xl font-medium hover:bg-bg-elevated"
        >
          0
        </button>
        <button
          onClick={backspace}
          className="w-16 h-16 rounded-full bg-bg-surface border border-bg-elevated text-xl hover:bg-bg-elevated"
        >
          ⌫
        </button>
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Typecheck**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npm run typecheck
```

Beklenen: clean.

- [ ] **Step 3: Commit**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git add apps/panel/src/components/PinGate.tsx
git commit -m "feat(panel): PinGate component (setup wizard + screen/inline variants + numeric pad)"
```

---

### Task 6: API hook + HizliIstatistikScreen + route + DahaFazla NavRow + logout integration

**Files:**
- Modify: `apps/panel/src/api/queries.ts` (useStats hook + types)
- Create: `apps/panel/src/screens/HizliIstatistikScreen.tsx`
- Modify: `apps/panel/src/App.tsx` (route)
- Modify: `apps/panel/src/screens/DahaFazlaScreen.tsx` (NavRow + placeholder kaldır)
- Modify: `apps/panel/src/auth/store.ts` (logout'a clearPin)

- [ ] **Step 1: queries.ts'a stats hook + types ekle**

`apps/panel/src/api/queries.ts`'in sonuna ekle:

```typescript

// ─── Hızlı istatistik (2026-05-19) ───────────────────────────────

export type StatsRange = "today" | "week" | "month";

export type StatsResponse = {
  range: StatsRange;
  rangeStart: string;
  rangeEnd: string;
  revenue: number;
  orderCount: number;
  averageOrderValue: number;
  cancelRate: number;
  activeStreamId: string | null;
  activeStreamTitle: string | null;
  pendingPaymentCount: number;
  pendingShipmentCount: number;
  lastUpdatedAt: string;
};

export function useStats(range: StatsRange) {
  return useQuery({
    queryKey: ["stats", range],
    queryFn: async () => {
      const resp = await apiClient.get<StatsResponse>(`/api/panel/stats?range=${range}`);
      return resp.data;
    },
    staleTime: 60_000,
  });
}
```

- [ ] **Step 2: HizliIstatistikScreen oluştur**

`apps/panel/src/screens/HizliIstatistikScreen.tsx` (yeni):

```typescript
import { useState } from "react";
import { Link } from "react-router-dom";
import { PinGate } from "../components/PinGate";
import { StatsRange, useStats } from "../api/queries";
import { formatTl } from "../lib/format";

const RANGE_TABS: Array<{ value: StatsRange; label: string }> = [
  { value: "today", label: "Bugün" },
  { value: "week", label: "Bu hafta" },
  { value: "month", label: "Bu ay" },
];

export function HizliIstatistikScreen() {
  return (
    <PinGate variant="screen">
      <Dashboard />
    </PinGate>
  );
}

function Dashboard() {
  const [range, setRange] = useState<StatsRange>("today");
  const { data, isLoading, isError, refetch, isFetching } = useStats(range);

  return (
    <main className="px-5 pt-6 pb-24">
      <header className="mb-4">
        <Link to="/daha-fazla" className="text-text-muted text-xs hover:text-text">
          ← Geri
        </Link>
        <h1 className="text-2xl font-bold mt-1">Hızlı istatistik</h1>
      </header>

      {/* Range tabs */}
      <div className="flex gap-2 mb-4">
        {RANGE_TABS.map((t) => (
          <button
            key={t.value}
            onClick={() => setRange(t.value)}
            className={`flex-1 py-2 rounded-lg text-sm font-medium ${
              range === t.value
                ? "bg-accent text-white"
                : "bg-bg-surface text-text-muted border border-bg-elevated"
            }`}
          >
            {t.label}
          </button>
        ))}
      </div>

      {isLoading ? (
        <div className="grid grid-cols-2 gap-3">
          {[0, 1, 2, 3].map((i) => (
            <div key={i} className="h-24 rounded-xl bg-bg-surface animate-pulse" />
          ))}
        </div>
      ) : isError ? (
        <div className="bg-danger/10 border border-danger/30 rounded-xl p-4 text-danger text-sm">
          Yüklenemedi.
          <button onClick={() => void refetch()} className="ml-2 underline">
            Tekrar dene
          </button>
        </div>
      ) : data ? (
        <>
          <div className="grid grid-cols-2 gap-3 mb-3">
            <MetricCard label="Ciro" value={formatTl(data.revenue)} />
            <MetricCard label="Sipariş" value={`${data.orderCount} adet`} />
            <MetricCard label="Ort. tutar" value={formatTl(data.averageOrderValue)} />
            <MetricCard label="İptal oranı" value={`%${(data.cancelRate * 100).toFixed(1)}`} />
          </div>

          {/* Active stream — span 2 */}
          {data.activeStreamId ? (
            <Link
              to={`/siparisler/${data.activeStreamId}`}
              className="block bg-bg-surface rounded-xl border border-bg-elevated p-4 mb-3 hover:bg-bg-elevated"
            >
              <div className="flex items-center gap-2">
                <span className="px-2 py-0.5 rounded-full text-[10px] font-bold bg-danger/15 text-danger animate-pulse">
                  CANLI
                </span>
                <p className="text-text font-medium">{data.activeStreamTitle ?? "Yayın"}</p>
              </div>
            </Link>
          ) : (
            <div className="bg-bg-surface rounded-xl border border-bg-elevated p-4 mb-3 opacity-60">
              <p className="text-text-muted text-xs uppercase tracking-wider">Aktif yayın</p>
              <p className="text-text-muted text-sm mt-1">
                WPF'te yayın başlatınca burada görünür.
              </p>
            </div>
          )}

          <div className="grid grid-cols-2 gap-3 mb-3">
            <MetricCard label="Bekleyen dekont" value={`${data.pendingPaymentCount} adet`} />
            <MetricCard label="Bekleyen kargo" value={`${data.pendingShipmentCount} adet`} />
          </div>

          <div className="flex justify-between items-center mt-4 text-text-muted text-xs">
            <span>Son güncelleme: {formatTime(data.lastUpdatedAt)}</span>
            <button
              onClick={() => void refetch()}
              disabled={isFetching}
              className="underline disabled:opacity-50"
            >
              {isFetching ? "..." : "↻ Yenile"}
            </button>
          </div>
        </>
      ) : null}
    </main>
  );
}

function MetricCard({ label, value }: { label: string; value: string }) {
  return (
    <div className="bg-bg-surface rounded-xl border border-bg-elevated p-4">
      <p className="text-text-muted text-xs uppercase tracking-wider">{label}</p>
      <p className="text-2xl font-bold mt-1">{value}</p>
    </div>
  );
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" });
}
```

- [ ] **Step 3: App.tsx'a route ekle**

`apps/panel/src/App.tsx` — `import { MusterilerScreen } ...` satırının altına ekle:

```typescript
import { HizliIstatistikScreen } from "./screens/HizliIstatistikScreen";
```

Route'lar arasına ekle, `{ path: "/musteriler", element: <MusterilerScreen /> },` satırının altına:

```typescript
          { path: "/hizli-istatistik", element: <HizliIstatistikScreen /> },
```

- [ ] **Step 4: DahaFazlaScreen güncelle**

`apps/panel/src/screens/DahaFazlaScreen.tsx`:

**4a. "İçerik" section'da, "Müşteriler" NavRow'unun altına yeni NavRow ekle:**

```typescript
        <NavRow
          to="/musteriler"
          label="Müşteriler"
          hint="Tüm müşterilerin listesi, sıralama ve filtreleme"
        />

        <NavRow
          to="/hizli-istatistik"
          label="Hızlı istatistik"
          hint="Bugünkü ciro, sipariş ve performans — PIN korumalı"
        />
```

**4b. "SONRAKİ SÜRÜM" section'ından şu satırı sil:**

```typescript
<DisabledRow label="Hızlı istatistik" hint="PIN ile korumalı, sonraki sürüm" />
```

(Section'da `<DisabledRow label="Yayın Excel raporu" hint="Sonraki sürüm" />` kalsın.)

- [ ] **Step 5: auth/store.ts'da logout'a clearPin ekle**

`apps/panel/src/auth/store.ts` dosyasını oku ve `logout` fonksiyonunu bul. Logout'un başına `clearPin()` çağrısı ekle:

```typescript
import { clearPin } from "../lib/pin";

// logout fonksiyonu içinde, en başta:
logout: async () => {
  await clearPin();
  // ... mevcut logout kodu (token clear, Preferences remove, vs.)
},
```

(Tam imza dosyada görüldükten sonra adapt edilir.)

- [ ] **Step 6: Typecheck**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npm run typecheck
```

Beklenen: clean.

- [ ] **Step 7: Commit**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git add apps/panel/src/api/queries.ts \
        apps/panel/src/screens/HizliIstatistikScreen.tsx \
        apps/panel/src/App.tsx \
        apps/panel/src/screens/DahaFazlaScreen.tsx \
        apps/panel/src/auth/store.ts
git commit -m "feat(panel): HizliIstatistikScreen + route + DahaFazla NavRow + logout clearPin"
```

---

### Task 7: AnaScreen totalAmount integration (PinGate inline)

**Files:**
- Modify: `apps/panel/src/screens/AnaScreen.tsx`

- [ ] **Step 1: AnaScreen'de totalAmount'u PinGate ile sar**

`apps/panel/src/screens/AnaScreen.tsx`'in üstüne import ekle:

```typescript
import { PinGate } from "../components/PinGate";
```

Mevcut son yayın card'ında `<p className="text-base font-bold text-success">{formatTl(latest.totalAmount)}</p>` kısmını PinGate ile sar:

```typescript
              <PinGate variant="inline" blurContent>
                <p className="text-base font-bold text-success">
                  {formatTl(latest.totalAmount)}
                </p>
              </PinGate>
```

(`isLive` block + `endedAt` block ikisinde de aynı totalAmount görüntülenmesi — eğer iki yer varsa ikisini de sar. AnaScreen.tsx'i oku, totalAmount referansını bul ve sar.)

- [ ] **Step 2: Typecheck**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npm run typecheck
```

Beklenen: clean.

- [ ] **Step 3: Commit**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git add apps/panel/src/screens/AnaScreen.tsx
git commit -m "feat(panel): AnaScreen totalAmount PinGate inline lock"
```

---

### Task 8: Mobile PR aç

- [ ] **Step 1: Push + PR**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git push -u origin feat/hizli-istatistik

gh pr create --title "feat(panel): Hızlı istatistik (PIN + biyometrik korumalı dashboard)" --body "$(cat <<'EOF'
## Summary

DahaFazlaScreen "Sonraki sürüm > Hızlı istatistik" placeholder'ı yerine gerçek dashboard + AnaScreen totalAmount'u aynı PIN gate'in arkasına aldı.

- **4 commit**:
  - PIN + biometric library + plugin install
  - PinGate component (setup wizard + screen/inline + numeric pad)
  - HizliIstatistikScreen + route + DahaFazla + logout integration
  - AnaScreen totalAmount lock
- 8 metrik: ciro / sipariş / AOV / iptal oranı / aktif yayın / bekleyen dekont / bekleyen kargo (range tabs: Bugün/Hafta/Ay)
- PIN: 4-digit + SHA-256+salt + Capacitor Preferences + 5 dk unlock window (in-memory)
- Biometric: \`capacitor-native-biometric@^5\`, ilk öncelik fail/cancel → PIN fallback
- PIN unutuldu UX: logout → relogin → ilk Hızlı istatistik'te setup wizard tekrar

## Dependencies

- Server PR LiveDeck \`feat(stats): GET /api/panel/stats endpoint\` — önce merge edilmeli, yoksa 404

## Test plan

- [x] \`npm run typecheck\` clean
- [ ] Android emulator smoke:
  - DahaFazla > Hızlı istatistik tap → setup wizard (PIN + biometric offer)
  - Tab değiştir (Bugün/Hafta/Ay) — her birinde fresh data
  - App background → 5 dk bekle → foreground → locked
  - AnaScreen totalAmount = blur + "Göster" → unlock
  - Logout → relogin → Hızlı istatistik → setup wizard tekrar

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

PR URL'ini not al.

---

## End-to-end smoke (her iki PR merge sonrası)

Plan dışı manuel doğrulama:

- [ ] Server PR merge → VPS pull + restart (broadcast posts pattern'i)
- [ ] Mobile PR merge → APK rebuild + emulator install
- [ ] Emulator'da:
  - DahaFazla > Hızlı istatistik → setup wizard çıkıyor
  - PIN belirle (4 hane) → onay → biometric offer → atla → dashboard görünür
  - Bugün tab default; Hafta + Ay tab'lerine geç, data değişiyor
  - Yenile butonu çalışıyor
  - App'i kapatıp yeniden aç → Hızlı istatistik'e bas → locked, PIN sor
  - PIN doğru gir → unlocked
  - AnaScreen → totalAmount blur + "Göster" → PIN modal → unlock → görünür
  - Logout → relogin → Hızlı istatistik → setup wizard tekrar (PIN sıfırlandı)

## Out of scope (bu plan dışı)

- PIN rate limit (5 yanlış sonrası bekleme)
- 6+ digit PIN
- Server-side PIN sync (cross-device)
- Email-based PIN reset
- DahaFazla'nın 3. placeholder'ı (Yayın Excel raporu) — ayrı spec
- Customers/Dekontlar/Siparişler screen'lerinde finansal data lock (kapsam dışı)
- Müşteriler endpoint follow-up (two-pass IQueryable refactor)
