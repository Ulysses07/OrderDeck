# Müşteriler Liste Ekranı Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mobile panel'de DahaFazlaScreen "Sonraki sürüm > Müşteriler" placeholder'ını gerçekçeye çevir. Yayıncının müşterilerinin filtrelenebilir + sıralanabilir liste view'ı.

**Architecture:** Server'da `PanelCustomersController`'a `[HttpGet] List(...)` action eklenir; aggregate'ler runtime'da `Orders.GroupBy(CustomerId)` ile hesaplanır (mevcut `Get(id)` pattern'iyle tutarlı). Mobile'a yeni `MusterilerScreen.tsx` + composite cursor pagination + sort dropdown + filter modal eklenir. Tenant izolasyonu single LINQ Where ile.

**Tech Stack:** .NET 10 + EF Core (SQL Server) on server. React + Vite + TanStack Query + react-router-dom + Capacitor 6 on mobile.

**Spec:** `docs/superpowers/specs/2026-05-19-musteriler-list-design.md`

**Branches:**
- Server: `feat/musteriler-list-server` in LiveDeck repo (master'dan)
- Mobile: `feat/musteriler-list` in OrderDeck-Mobile repo (main'den)

**Mevcut DB index'leri** (kontrol edildi, eksik yok):
- `Orders(LicenseId, CustomerId)` — GROUP BY için ✓
- `Orders(LicenseId, UpdatedAt)` — identity subquery için ✓
- Migration gerekmiyor.

---

## Server tasks (LiveDeck repo, branch `feat/musteriler-list-server`)

### Task 1: List endpoint scaffold + DTOs + aggregate query + 3 test

**Goal:** Endpoint'i çalıştır + temel happy-path (default sort `lastOrder`) test'leri yeşil.

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelCustomersController.cs`
- Create: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs`

- [ ] **Step 1: Branch aç**

```bash
cd C:/Users/burak/source/repos/LiveDeck
git checkout master && git pull origin master --ff-only
git checkout -b feat/musteriler-list-server
```

- [ ] **Step 2: Controller'a DTO'lar + private aggregate record + endpoint + sort helper'lar ekle**

`OrderDeck.LicenseServer/Controllers/Panel/PanelCustomersController.cs` — mevcut `[HttpGet("{customerId}")] Get` action'ının **ÖNCESİNE** (record'lardan sonra) ekle:

```csharp
public sealed record CustomerListItem(
    string Id,
    string? DisplayName,
    string Username,
    string Platform,
    decimal TotalSpent,
    int OrderCount,
    DateTimeOffset LastOrderAt,
    bool IsActive);

public sealed record CustomerListResponse(
    List<CustomerListItem> Customers,
    string? NextCursor);

// Internal aggregate row — EF projeksiyon hedefi. Anonymous type kullanmıyoruz
// çünkü sort helper'lar IQueryable<...> imzasını sabit tutmak zorunda
// (dynamic IQueryable EF Core'da Where zincirinde bozulur).
private sealed record CustomerAggRow(
    string CustomerId,
    decimal TotalSpent,
    int OrderCount,
    DateTimeOffset LastOrderAt,
    string? DisplayName,
    string Username,
    string Platform);

[HttpGet]
public async Task<IActionResult> List(
    [FromQuery] string? cursor,
    [FromQuery] string? sort,
    [FromQuery] int? activeWithinDays,
    [FromQuery] string? platforms,
    [FromQuery] decimal? minSpent,
    [FromQuery] decimal? maxSpent,
    [FromQuery] int? minOrders,
    [FromQuery] int? maxOrders,
    [FromQuery] int limit = 50,
    CancellationToken ct = default)
{
    if (limit < 1 || limit > 100) limit = 50;

    var sortMode = sort?.ToLowerInvariant() switch
    {
        null or "" or "lastorder" => "lastOrder",
        "totalspent" => "totalSpent",
        "ordercount" => "orderCount",
        "name" => "name",
        _ => null
    };
    if (sortMode is null)
        return Problem(title: "invalid-sort", statusCode: 400);

    var authCustomerId = User.GetTenantCustomerId();
    var licenseIds = await _db.Licenses
        .Where(l => l.CustomerId == authCustomerId)
        .Select(l => l.Id)
        .ToListAsync(ct);

    if (licenseIds.Count == 0)
        return Ok(new CustomerListResponse(new List<CustomerListItem>(), null));

    // Base: tenant'ın tüm siparişleri
    var ordersQuery = _db.Orders.Where(o => licenseIds.Contains(o.LicenseId));

    // Platform filter (varsa) — order level (her sipariş kendi platform'unu taşır)
    var platformList = platforms?
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.ToLowerInvariant())
        .ToList();
    if (platformList is { Count: > 0 })
        ordersQuery = ordersQuery.Where(o => platformList.Contains(o.Platform.ToLower()));

    // Aggregate per customer — typed CustomerAggRow'a projeksiyon
    var aggregatesQuery = ordersQuery
        .GroupBy(o => o.CustomerId)
        .Select(g => new CustomerAggRow(
            g.Key,
            g.Where(o => o.PrintedAt != null && o.CancelledAt == null
                      && !o.IsShippingFee && !o.IsTentativeBackup)
             .Sum(o => (decimal?)o.Price) ?? 0m,
            g.Count(o => o.PrintedAt != null && o.CancelledAt == null
                      && !o.IsShippingFee && !o.IsTentativeBackup),
            g.Max(o => o.AddedAt),
            g.OrderByDescending(o => o.UpdatedAt).Select(o => o.DisplayName).First(),
            g.OrderByDescending(o => o.UpdatedAt).Select(o => o.Username).First(),
            g.OrderByDescending(o => o.UpdatedAt).Select(o => o.Platform).First()));

    // Post-aggregate filters
    if (activeWithinDays.HasValue)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-activeWithinDays.Value);
        aggregatesQuery = aggregatesQuery.Where(c => c.LastOrderAt > cutoff);
    }
    if (minSpent.HasValue)
        aggregatesQuery = aggregatesQuery.Where(c => c.TotalSpent >= minSpent.Value);
    if (maxSpent.HasValue)
        aggregatesQuery = aggregatesQuery.Where(c => c.TotalSpent <= maxSpent.Value);
    if (minOrders.HasValue)
        aggregatesQuery = aggregatesQuery.Where(c => c.OrderCount >= minOrders.Value);
    if (maxOrders.HasValue)
        aggregatesQuery = aggregatesQuery.Where(c => c.OrderCount <= maxOrders.Value);

    // Sort + cursor uygula — her sort'un kendi LINQ branch'i
    var (cursorSortRaw, cursorCustomerId) = ParseCursor(cursor);

    List<CustomerAggRow> rows = sortMode switch
    {
        "lastOrder" => await ApplyLastOrderSort(aggregatesQuery, cursorSortRaw, cursorCustomerId, limit, ct),
        "totalSpent" => await ApplyTotalSpentSort(aggregatesQuery, cursorSortRaw, cursorCustomerId, limit, ct),
        "orderCount" => await ApplyOrderCountSort(aggregatesQuery, cursorSortRaw, cursorCustomerId, limit, ct),
        "name" => await ApplyNameSort(aggregatesQuery, cursorSortRaw, cursorCustomerId, limit, ct),
        _ => throw new InvalidOperationException()
    };

    string? nextCursor = null;
    if (rows.Count > limit)
    {
        var last = rows[limit - 1];
        var lastSortValue = sortMode switch
        {
            "lastOrder" => last.LastOrderAt.ToString("O"),
            "totalSpent" => last.TotalSpent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "orderCount" => last.OrderCount.ToString(),
            "name" => last.DisplayName ?? last.Username ?? "",
            _ => throw new InvalidOperationException()
        };
        nextCursor = $"{lastSortValue}|{last.CustomerId}";
        rows = rows.Take(limit).ToList();
    }

    var activeCutoff = DateTimeOffset.UtcNow.AddDays(-30);
    var items = rows.Select(r => new CustomerListItem(
        r.CustomerId,
        r.DisplayName,
        r.Username,
        r.Platform,
        r.TotalSpent,
        r.OrderCount,
        r.LastOrderAt,
        r.LastOrderAt > activeCutoff)).ToList();

    return Ok(new CustomerListResponse(items, nextCursor));
}

private static (string? sortValue, string? customerId) ParseCursor(string? cursor)
{
    if (string.IsNullOrWhiteSpace(cursor)) return (null, null);
    var parts = cursor.Split('|', 2);
    if (parts.Length != 2) return (null, null);
    return (parts[0], parts[1]);
}

private static async Task<List<CustomerAggRow>> ApplyLastOrderSort(
    IQueryable<CustomerAggRow> q, string? cursorSortRaw, string? cursorCustomerId,
    int limit, CancellationToken ct)
{
    if (cursorSortRaw != null && cursorCustomerId != null
        && DateTimeOffset.TryParse(cursorSortRaw, out var cv))
    {
        q = q.Where(c =>
            c.LastOrderAt < cv ||
            (c.LastOrderAt == cv && string.Compare(c.CustomerId, cursorCustomerId) < 0));
    }
    return await q
        .OrderByDescending(c => c.LastOrderAt)
        .ThenByDescending(c => c.CustomerId)
        .Take(limit + 1)
        .ToListAsync(ct);
}

private static async Task<List<CustomerAggRow>> ApplyTotalSpentSort(
    IQueryable<CustomerAggRow> q, string? cursorSortRaw, string? cursorCustomerId,
    int limit, CancellationToken ct)
{
    if (cursorSortRaw != null && cursorCustomerId != null
        && decimal.TryParse(cursorSortRaw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var cv))
    {
        q = q.Where(c =>
            c.TotalSpent < cv ||
            (c.TotalSpent == cv && string.Compare(c.CustomerId, cursorCustomerId) < 0));
    }
    return await q
        .OrderByDescending(c => c.TotalSpent)
        .ThenByDescending(c => c.CustomerId)
        .Take(limit + 1)
        .ToListAsync(ct);
}

private static async Task<List<CustomerAggRow>> ApplyOrderCountSort(
    IQueryable<CustomerAggRow> q, string? cursorSortRaw, string? cursorCustomerId,
    int limit, CancellationToken ct)
{
    if (cursorSortRaw != null && cursorCustomerId != null
        && int.TryParse(cursorSortRaw, out var cv))
    {
        q = q.Where(c =>
            c.OrderCount < cv ||
            (c.OrderCount == cv && string.Compare(c.CustomerId, cursorCustomerId) < 0));
    }
    return await q
        .OrderByDescending(c => c.OrderCount)
        .ThenByDescending(c => c.CustomerId)
        .Take(limit + 1)
        .ToListAsync(ct);
}

private static async Task<List<CustomerAggRow>> ApplyNameSort(
    IQueryable<CustomerAggRow> q, string? cursorSortRaw, string? cursorCustomerId,
    int limit, CancellationToken ct)
{
    if (cursorSortRaw != null && cursorCustomerId != null)
    {
        var cv = cursorSortRaw;
        q = q.Where(c =>
            string.Compare(c.DisplayName ?? c.Username ?? "", cv) > 0 ||
            (string.Compare(c.DisplayName ?? c.Username ?? "", cv) == 0
                && string.Compare(c.CustomerId, cursorCustomerId) > 0));
    }
    return await q
        .OrderBy(c => c.DisplayName ?? c.Username ?? "")
        .ThenBy(c => c.CustomerId)
        .Take(limit + 1)
        .ToListAsync(ct);
}
```

**Not:** `CustomerAggRow` *private* record olduğu için ortaya çıkan response'lara karışmaz; sadece EF Core projeksiyon ve helper'lar arasında taşınır. `CustomerListItem` (public) ile karıştırma — `CustomerListItem` API contract, `CustomerAggRow` internal storage.

- [ ] **Step 3: Test dosyasını oluştur + 3 ilk test**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs` (yeni):

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

public class PanelCustomersControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelCustomersControllerTests(ApiFactory f) => _factory = f;

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-MUS-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static Order MakeOrder(Guid licenseId, string customerId, string platform,
        string username, string? displayName, decimal price,
        DateTimeOffset addedAt, bool printed = true, bool cancelled = false,
        bool isShippingFee = false, bool isTentativeBackup = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new Order
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            CustomerId = customerId, Platform = platform,
            Username = username, DisplayName = displayName,
            MessageText = "test", Price = price,
            AddedAt = addedAt,
            PrintedAt = printed ? addedAt : null,
            CancelledAt = cancelled ? addedAt : null,
            IsShippingFee = isShippingFee,
            IsTentativeBackup = isTentativeBackup,
            UpdatedAt = addedAt
        };
    }

    [Fact]
    public async Task List_returns_empty_when_no_orders()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"customers\":[]").And.Contain("\"nextCursor\":null");
    }

    [Fact]
    public async Task List_returns_customers_with_aggregate_counts()
    {
        var (client, licenseId) = await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeOrder(licenseId, "alice-ig", "instagram", "@alice", "Alice", 100m, now.AddDays(-1)),
                MakeOrder(licenseId, "alice-ig", "instagram", "@alice", "Alice", 50m,  now.AddDays(-2)),
                MakeOrder(licenseId, "bob-tt",   "tiktok",    "@bob",   "Bob",   200m, now.AddDays(-3)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers.GetArrayLength().Should().Be(2);

        // En son sipariş alice (now-1), bob (now-3) → alice önce
        var first = customers[0];
        first.GetProperty("id").GetString().Should().Be("alice-ig");
        first.GetProperty("totalSpent").GetDecimal().Should().Be(150m);
        first.GetProperty("orderCount").GetInt32().Should().Be(2);
        first.GetProperty("isActive").GetBoolean().Should().BeTrue();

        var second = customers[1];
        second.GetProperty("id").GetString().Should().Be("bob-tt");
        second.GetProperty("orderCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task List_sort_by_lastOrder_desc_default()
    {
        // Aynı veriyle, alice (now-1) en yakın → ilk sırada olmalı (default sort)
        var (client, licenseId) = await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeOrder(licenseId, "carol-ig", "instagram", "@carol", "Carol", 10m, now.AddDays(-5)),
                MakeOrder(licenseId, "dave-ig",  "instagram", "@dave",  "Dave",  10m, now.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers[0].GetProperty("id").GetString().Should().Be("dave-ig");
    }
}
```

- [ ] **Step 4: Build + test**

```bash
cd C:/Users/burak/source/repos/LiveDeck
dotnet build OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelCustomers" --nologo
```

Beklenen: 3/3 pass (List_returns_empty, List_returns_customers_with_aggregate_counts, List_sort_by_lastOrder_desc_default) + mevcut Get tests (varsa).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelCustomersController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs
git commit -m "feat(customers): GET /api/panel/customers list endpoint + aggregate query"
```

---

### Task 2: Alternative sort modes + 3 test

**Files:**
- Modify (already exists): `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs`

Controller değişmiyor (sort branch'ları Task 1'de zaten yazıldı). Sadece test ekliyoruz.

- [ ] **Step 1: 3 sort variant testi ekle**

Test dosyasının sonuna:

```csharp
[Fact]
public async Task List_sort_by_totalSpent_desc()
{
    var (client, licenseId) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Orders.AddRange(
            MakeOrder(licenseId, "low-ig",  "instagram", "@low",  "Low",  100m, now.AddDays(-1)),
            MakeOrder(licenseId, "high-ig", "instagram", "@high", "High", 999m, now.AddDays(-5)));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/customers?sort=totalSpent");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var customers = doc.RootElement.GetProperty("customers");
    customers[0].GetProperty("id").GetString().Should().Be("high-ig");
    customers[1].GetProperty("id").GetString().Should().Be("low-ig");
}

[Fact]
public async Task List_sort_by_orderCount_desc()
{
    var (client, licenseId) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Orders.AddRange(
            MakeOrder(licenseId, "frequent-ig", "instagram", "@f", "Frequent", 10m, now.AddDays(-5)),
            MakeOrder(licenseId, "frequent-ig", "instagram", "@f", "Frequent", 10m, now.AddDays(-4)),
            MakeOrder(licenseId, "frequent-ig", "instagram", "@f", "Frequent", 10m, now.AddDays(-3)),
            MakeOrder(licenseId, "rare-ig",     "instagram", "@r", "Rare",     10m, now.AddDays(-1)));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/customers?sort=orderCount");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var customers = doc.RootElement.GetProperty("customers");
    customers[0].GetProperty("id").GetString().Should().Be("frequent-ig");
    customers[0].GetProperty("orderCount").GetInt32().Should().Be(3);
}

[Fact]
public async Task List_sort_by_name_asc()
{
    var (client, licenseId) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Orders.AddRange(
            MakeOrder(licenseId, "zoe-ig",   "instagram", "@zoe",   "Zoe",    10m, now.AddDays(-1)),
            MakeOrder(licenseId, "anna-ig",  "instagram", "@anna",  "Anna",   10m, now.AddDays(-2)),
            MakeOrder(licenseId, "mike-ig",  "instagram", "@mike",  "Mike",   10m, now.AddDays(-3)));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/customers?sort=name");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var customers = doc.RootElement.GetProperty("customers");
    customers[0].GetProperty("displayName").GetString().Should().Be("Anna");
    customers[1].GetProperty("displayName").GetString().Should().Be("Mike");
    customers[2].GetProperty("displayName").GetString().Should().Be("Zoe");
}
```

- [ ] **Step 2: Test**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelCustomers" --nologo
```

Beklenen: 6/6 pass (3 önceki + 3 yeni).

- [ ] **Step 3: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs
git commit -m "test(customers): cover totalSpent / orderCount / name sort variants"
```

---

### Task 3: Filtre testleri (active, platform, spent range, order count range)

**Files:**
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs`

Controller filtreleri Task 1'de hazır. Sadece test'ler.

- [ ] **Step 1: 4 filter testi ekle**

```csharp
[Fact]
public async Task List_filter_active_within_30_days()
{
    var (client, licenseId) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Orders.AddRange(
            MakeOrder(licenseId, "active-ig",   "instagram", "@a", "Active",   10m, now.AddDays(-5)),
            MakeOrder(licenseId, "inactive-ig", "instagram", "@i", "Inactive", 10m, now.AddDays(-60)));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/customers?activeWithinDays=30");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var customers = doc.RootElement.GetProperty("customers");
    customers.GetArrayLength().Should().Be(1);
    customers[0].GetProperty("id").GetString().Should().Be("active-ig");
    customers[0].GetProperty("isActive").GetBoolean().Should().BeTrue();
}

[Fact]
public async Task List_filter_platform_multi()
{
    var (client, licenseId) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Orders.AddRange(
            MakeOrder(licenseId, "ig-cust", "instagram", "@i", "IG", 10m, now.AddDays(-1)),
            MakeOrder(licenseId, "tt-cust", "tiktok",    "@t", "TT", 10m, now.AddDays(-2)),
            MakeOrder(licenseId, "fb-cust", "facebook",  "@f", "FB", 10m, now.AddDays(-3)));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/customers?platforms=instagram,tiktok");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var customers = doc.RootElement.GetProperty("customers");
    customers.GetArrayLength().Should().Be(2);
    var ids = new[]
    {
        customers[0].GetProperty("id").GetString(),
        customers[1].GetProperty("id").GetString()
    };
    ids.Should().Contain("ig-cust").And.Contain("tt-cust");
    ids.Should().NotContain("fb-cust");
}

[Fact]
public async Task List_filter_spent_range()
{
    var (client, licenseId) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Orders.AddRange(
            MakeOrder(licenseId, "small-ig",  "instagram", "@s",  "S",  50m,    now.AddDays(-1)),
            MakeOrder(licenseId, "medium-ig", "instagram", "@m",  "M",  500m,   now.AddDays(-2)),
            MakeOrder(licenseId, "huge-ig",   "instagram", "@h",  "H",  50000m, now.AddDays(-3)));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/customers?minSpent=100&maxSpent=1000");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var customers = doc.RootElement.GetProperty("customers");
    customers.GetArrayLength().Should().Be(1);
    customers[0].GetProperty("id").GetString().Should().Be("medium-ig");
}

[Fact]
public async Task List_filter_order_count_range()
{
    var (client, licenseId) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // single: 1 order, loyal: 5 orders
        db.Orders.Add(MakeOrder(licenseId, "single-ig", "instagram", "@s", "Single", 10m, now.AddDays(-1)));
        for (int i = 0; i < 5; i++)
            db.Orders.Add(MakeOrder(licenseId, "loyal-ig", "instagram", "@l", "Loyal", 10m, now.AddDays(-i - 2)));
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/customers?minOrders=2");
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var customers = doc.RootElement.GetProperty("customers");
    customers.GetArrayLength().Should().Be(1);
    customers[0].GetProperty("id").GetString().Should().Be("loyal-ig");
    customers[0].GetProperty("orderCount").GetInt32().Should().Be(5);
}
```

- [ ] **Step 2: Test + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelCustomers" --nologo

git add OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs
git commit -m "test(customers): cover active / platform / spent / orderCount filters"
```

Beklenen: 10/10 pass (6 önceki + 4 yeni).

---

### Task 4: Cursor pagination test + cross-tenant test

**Files:**
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs`

- [ ] **Step 1: Cursor + tenant testi ekle**

```csharp
[Fact]
public async Task List_pagination_composite_cursor()
{
    var (client, licenseId) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;

    // 5 müşteri, her birinin lastOrder farklı tarih
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        for (int i = 0; i < 5; i++)
        {
            db.Orders.Add(MakeOrder(
                licenseId, $"cust-{i}-ig", "instagram", $"@c{i}", $"Customer {i}",
                10m, now.AddDays(-i)));
        }
        await db.SaveChangesAsync();
    }

    // Page 1: limit=2 → ilk 2 (cust-0, cust-1)
    var resp1 = await client.GetAsync("/api/panel/customers?limit=2");
    resp1.StatusCode.Should().Be(HttpStatusCode.OK);
    var body1 = await resp1.Content.ReadAsStringAsync();
    using var doc1 = System.Text.Json.JsonDocument.Parse(body1);
    var page1 = doc1.RootElement.GetProperty("customers");
    page1.GetArrayLength().Should().Be(2);
    page1[0].GetProperty("id").GetString().Should().Be("cust-0-ig");
    page1[1].GetProperty("id").GetString().Should().Be("cust-1-ig");
    var cursor = doc1.RootElement.GetProperty("nextCursor").GetString();
    cursor.Should().NotBeNullOrEmpty();

    // Page 2: cursor ile → cust-2, cust-3
    var resp2 = await client.GetAsync($"/api/panel/customers?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
    var body2 = await resp2.Content.ReadAsStringAsync();
    using var doc2 = System.Text.Json.JsonDocument.Parse(body2);
    var page2 = doc2.RootElement.GetProperty("customers");
    page2.GetArrayLength().Should().Be(2);
    page2[0].GetProperty("id").GetString().Should().Be("cust-2-ig");
    page2[1].GetProperty("id").GetString().Should().Be("cust-3-ig");

    // Page 1 ile page 2 arasında duplicate olmamalı
    var page1Ids = new[] { page1[0].GetProperty("id").GetString(), page1[1].GetProperty("id").GetString() };
    var page2Ids = new[] { page2[0].GetProperty("id").GetString(), page2[1].GetProperty("id").GetString() };
    page1Ids.Should().NotIntersectWith(page2Ids);
}

[Fact]
public async Task List_cross_tenant_returns_empty()
{
    var (clientA, licenseA) = await SeedAsync();
    var (clientB, _) = await SeedAsync();
    var now = DateTimeOffset.UtcNow;

    // Tenant A'nın müşterisi
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Orders.Add(MakeOrder(licenseA, "secret-cust", "instagram", "@s", "Secret", 100m, now.AddDays(-1)));
        await db.SaveChangesAsync();
    }

    // Tenant B sorgular — A'nın müşterisini görmemeli
    var resp = await clientB.GetAsync("/api/panel/customers");
    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    doc.RootElement.GetProperty("customers").GetArrayLength().Should().Be(0);
}
```

- [ ] **Step 2: Test + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelCustomers" --nologo

git add OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs
git commit -m "test(customers): cursor pagination + cross-tenant isolation"
```

Beklenen: 12/12 pass (10 önceki + 2 yeni).

---

### Task 5: Server PR aç

- [ ] **Step 1: Push + PR**

```bash
cd C:/Users/burak/source/repos/LiveDeck
git push -u origin feat/musteriler-list-server

gh pr create --title "feat(customers): GET /api/panel/customers list endpoint" --body "$(cat <<'EOF'
## Summary

Mobile panelin yeni \`MusterilerScreen\` için server endpoint'i. Yayıncının
müşterilerinin (Orders.CustomerId distinct değerleri) filtre + sort + cursor
paginated liste view'ı.

- 4 commit, 12 test
- Tenant isolation single-LINQ Where (broadcast posts pattern'i)
- Composite cursor \`{sortValue}|{customerId}\`
- 4 sort (lastOrder default / totalSpent / orderCount / name)
- 5 filter (active / platform / minSpent / maxSpent / minOrders / maxOrders)

Spec: \`docs/superpowers/specs/2026-05-19-musteriler-list-design.md\` (PR'da yok, ayrı branch'te)

## Test plan

- [x] PanelCustomers tests: 12/12 yeşil
- [ ] Tüm server suite yeşil (CI)
- [ ] Mobile companion PR (OrderDeck-Mobile feat/musteriler-list)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

PR URL'ini not al.

---

## Mobile tasks (OrderDeck-Mobile repo, branch `feat/musteriler-list`)

### Task 6: API hooks + types

**Files:**
- Modify: `apps/panel/src/api/queries.ts`

- [ ] **Step 1: Mobile repo'ya geç + branch**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git checkout main && git pull origin main --ff-only
git checkout -b feat/musteriler-list
```

- [ ] **Step 2: queries.ts sonuna ekle**

`apps/panel/src/api/queries.ts`:

```typescript
// ─── Customers list (2026-05-19) ─────────────────────────────────

export type CustomerSort = "lastOrder" | "totalSpent" | "orderCount" | "name";

export type CustomerListItem = {
  id: string;
  displayName: string | null;
  username: string;
  platform: string;
  totalSpent: number;
  orderCount: number;
  lastOrderAt: string;
  isActive: boolean;
};

export type CustomerListResponse = {
  customers: CustomerListItem[];
  nextCursor: string | null;
};

export type CustomerListParams = {
  sort?: CustomerSort;
  activeWithinDays?: number;
  platforms?: string[];
  minSpent?: number;
  maxSpent?: number;
  minOrders?: number;
  maxOrders?: number;
};

export function useCustomersInfinite(params: CustomerListParams) {
  return useInfiniteQuery({
    queryKey: ["customers", params],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam }) => {
      const search = new URLSearchParams();
      if (params.sort) search.set("sort", params.sort);
      if (params.activeWithinDays !== undefined)
        search.set("activeWithinDays", String(params.activeWithinDays));
      if (params.platforms && params.platforms.length > 0)
        search.set("platforms", params.platforms.join(","));
      if (params.minSpent !== undefined) search.set("minSpent", String(params.minSpent));
      if (params.maxSpent !== undefined) search.set("maxSpent", String(params.maxSpent));
      if (params.minOrders !== undefined) search.set("minOrders", String(params.minOrders));
      if (params.maxOrders !== undefined) search.set("maxOrders", String(params.maxOrders));
      search.set("limit", "50");
      if (pageParam) search.set("cursor", pageParam);

      const resp = await apiClient.get<CustomerListResponse>(
        `/api/panel/customers?${search.toString()}`,
      );
      return resp.data;
    },
    getNextPageParam: (last) => last.nextCursor,
    staleTime: 30_000,
  });
}
```

**Not:** Dosyanın başında `useInfiniteQuery` import zaten var mı kontrol et:

```typescript
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
```

Yoksa ekle.

- [ ] **Step 3: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck

cd ../..
git add apps/panel/src/api/queries.ts
git commit -m "feat(panel): customer list API hook + types"
```

Beklenen: typecheck temiz.

---

### Task 7: MusterilerScreen + route + DahaFazla NavRow

**Files:**
- Create: `apps/panel/src/screens/MusterilerScreen.tsx`
- Modify: `apps/panel/src/App.tsx`
- Modify: `apps/panel/src/screens/DahaFazlaScreen.tsx`

Bu task'ta liste + sort dropdown + state'ler kapsanır. Filter modal Task 8'de.

- [ ] **Step 1: MusterilerScreen oluştur**

`apps/panel/src/screens/MusterilerScreen.tsx`:

```typescript
import { useState, useRef, useEffect } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  CustomerListItem,
  CustomerSort,
  useCustomersInfinite,
} from "../api/queries";
import { formatRelative, formatTl } from "../lib/format";

type SortOption = { value: CustomerSort; label: string };
const SORT_OPTIONS: SortOption[] = [
  { value: "lastOrder", label: "Son sipariş" },
  { value: "totalSpent", label: "Toplam harcama" },
  { value: "orderCount", label: "Sipariş sayısı" },
  { value: "name", label: "Ad (A-Z)" },
];

export function MusterilerScreen() {
  const [sort, setSort] = useState<CustomerSort>("lastOrder");
  const [sortOpen, setSortOpen] = useState(false);

  const query = useCustomersInfinite({ sort });
  const allCustomers = query.data?.pages.flatMap((p) => p.customers) ?? [];

  // Infinite scroll trigger
  const sentinelRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const sentinel = sentinelRef.current;
    if (!sentinel) return;
    const obs = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && query.hasNextPage && !query.isFetchingNextPage) {
          void query.fetchNextPage();
        }
      },
      { rootMargin: "200px" },
    );
    obs.observe(sentinel);
    return () => obs.disconnect();
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  return (
    <main className="px-5 pt-6 pb-24">
      <header className="mb-4">
        <Link to="/daha-fazla" className="text-text-muted text-xs hover:text-text">
          ← Geri
        </Link>
        <h1 className="text-2xl font-bold mt-1">Müşteriler</h1>
        <p className="text-text-muted text-sm mt-0.5">
          {query.isLoading ? "Yükleniyor..." : `${allCustomers.length} müşteri`}
        </p>
      </header>

      {/* Sort + Filter bar */}
      <div className="flex gap-2 mb-3 items-center">
        <div className="relative">
          <button
            onClick={() => setSortOpen((v) => !v)}
            className="px-3 py-1.5 rounded-lg bg-bg-surface border border-bg-elevated text-sm flex items-center gap-1"
          >
            Sırala: {SORT_OPTIONS.find((o) => o.value === sort)?.label}
            <span className="text-text-muted">▼</span>
          </button>
          {sortOpen && (
            <div className="absolute top-full left-0 mt-1 bg-bg-surface border border-bg-elevated rounded-lg shadow-lg z-10 min-w-[160px]">
              {SORT_OPTIONS.map((o) => (
                <button
                  key={o.value}
                  onClick={() => {
                    setSort(o.value);
                    setSortOpen(false);
                  }}
                  className={`block w-full text-left px-3 py-2 text-sm hover:bg-bg-elevated ${
                    o.value === sort ? "text-accent" : "text-text"
                  }`}
                >
                  {o.label}
                </button>
              ))}
            </div>
          )}
        </div>
        {/* Filter button — Task 8 */}
      </div>

      {/* States */}
      {query.isLoading ? (
        <div className="space-y-2">
          {[0, 1, 2].map((i) => (
            <div key={i} className="h-16 rounded-xl bg-bg-surface animate-pulse" />
          ))}
        </div>
      ) : query.isError ? (
        <div className="bg-danger/10 border border-danger/30 rounded-xl p-4 text-danger text-sm">
          Yüklenemedi.
          <button onClick={() => void query.refetch()} className="ml-2 underline">
            Tekrar dene
          </button>
        </div>
      ) : allCustomers.length === 0 ? (
        <div className="text-center py-16">
          <p className="text-5xl mb-3">📭</p>
          <p className="text-text font-medium">Henüz müşterin yok</p>
          <p className="text-text-muted text-xs mt-1">
            İlk satıştan sonra burada görünür.
          </p>
        </div>
      ) : (
        <>
          <ul className="space-y-2">
            {allCustomers.map((c) => (
              <CustomerRow key={c.id} c={c} />
            ))}
          </ul>
          {/* Infinite scroll sentinel */}
          <div ref={sentinelRef} className="h-12 flex items-center justify-center">
            {query.isFetchingNextPage && (
              <span className="text-text-muted text-xs">Yükleniyor...</span>
            )}
          </div>
        </>
      )}
    </main>
  );
}

function CustomerRow({ c }: { c: CustomerListItem }) {
  return (
    <li>
      <Link
        to={`/musteriler/${c.id}`}
        className="block bg-bg-surface rounded-xl border border-bg-elevated p-3 hover:bg-bg-elevated transition-colors"
      >
        <div className="flex justify-between items-start gap-3">
          <div className="min-w-0 flex-1">
            <p className="text-text font-medium truncate">
              {c.displayName ?? c.username}
            </p>
            <p className="text-text-muted text-xs mt-0.5">
              {c.orderCount} sipariş · {formatRelative(c.lastOrderAt)} · {platformShort(c.platform)}
            </p>
          </div>
          <p className="text-text font-semibold whitespace-nowrap">
            {formatTl(c.totalSpent)}
          </p>
        </div>
      </Link>
    </li>
  );
}

function platformShort(p: string): string {
  const key = p.toLowerCase();
  if (key === "instagram") return "IG";
  if (key === "tiktok") return "TT";
  if (key === "facebook") return "FB";
  if (key === "youtube") return "YT";
  if (key === "web") return "Web";
  return p;
}
```

- [ ] **Step 2: App.tsx route ekle**

`apps/panel/src/App.tsx` — `import { DuyuruDetayScreen } from "./screens/DuyuruDetayScreen";` satırından sonra:

```typescript
import { MusterilerScreen } from "./screens/MusterilerScreen";
```

router children içinde, `{ path: "/duyurular/:id", element: <DuyuruDetayScreen /> },` satırından sonra:

```typescript
          { path: "/musteriler", element: <MusterilerScreen /> },
```

> Not: `/musteriler/:customerId` route'u (detail) zaten var (CustomerDetailScreen). Onu değiştirme.

- [ ] **Step 3: DahaFazlaScreen güncelle**

`apps/panel/src/screens/DahaFazlaScreen.tsx`:

**3a. "İçerik" section'a NavRow ekle** — mevcut "Duyurular" NavRow'unun altına:

```typescript
        <NavRow
          to="/duyurular"
          label="Duyurular"
          hint="Müşterilere foto, video veya mesaj paylaş"
        />

        <NavRow
          to="/musteriler"
          label="Müşteriler"
          hint="Tüm müşterilerin listesi, sıralama ve filtreleme"
        />
```

**3b. "Sonraki sürüm" section'ından `<DisabledRow label="Müşteriler" hint="Sonraki sürüm" />` satırını sil.**

- [ ] **Step 4: Typecheck**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npm run typecheck
```

Beklenen: temiz.

- [ ] **Step 5: Commit**

```bash
cd ../..
git add apps/panel/src/screens/MusterilerScreen.tsx \
        apps/panel/src/App.tsx \
        apps/panel/src/screens/DahaFazlaScreen.tsx
git commit -m "feat(panel): MusterilerScreen liste + sort dropdown + route + DahaFazla NavRow"
```

---

### Task 8: Filter modal

**Files:**
- Modify: `apps/panel/src/screens/MusterilerScreen.tsx`

- [ ] **Step 1: State + modal component'i**

`MusterilerScreen.tsx`'in başında, mevcut `import` ve `SORT_OPTIONS` arasına filter state types ekle:

```typescript
type Filters = {
  activeOnly: boolean;
  platforms: Set<string>;
  minSpent: string;  // input field — boş = filtresiz
  maxSpent: string;
  minOrders: string;
  maxOrders: string;
};

const EMPTY_FILTERS: Filters = {
  activeOnly: false,
  platforms: new Set(),
  minSpent: "",
  maxSpent: "",
  minOrders: "",
  maxOrders: "",
};

const ALL_PLATFORMS: Array<{ key: string; label: string }> = [
  { key: "instagram", label: "Instagram" },
  { key: "tiktok", label: "TikTok" },
  { key: "facebook", label: "Facebook" },
  { key: "youtube", label: "YouTube" },
  { key: "web", label: "Web" },
];
```

`MusterilerScreen` component'inin başında state ekle ve `useCustomersInfinite` çağrısını filtreyle besle:

```typescript
export function MusterilerScreen() {
  const [sort, setSort] = useState<CustomerSort>("lastOrder");
  const [sortOpen, setSortOpen] = useState(false);

  const [filters, setFilters] = useState<Filters>(EMPTY_FILTERS);
  const [filterOpen, setFilterOpen] = useState(false);

  // Filter → useCustomersInfinite params
  const params = {
    sort,
    activeWithinDays: filters.activeOnly ? 30 : undefined,
    platforms: filters.platforms.size > 0 ? Array.from(filters.platforms) : undefined,
    minSpent: filters.minSpent ? Number(filters.minSpent) : undefined,
    maxSpent: filters.maxSpent ? Number(filters.maxSpent) : undefined,
    minOrders: filters.minOrders ? Number(filters.minOrders) : undefined,
    maxOrders: filters.maxOrders ? Number(filters.maxOrders) : undefined,
  };

  const query = useCustomersInfinite(params);
  const allCustomers = query.data?.pages.flatMap((p) => p.customers) ?? [];

  // Active filter count for badge
  const activeFilterCount =
    (filters.activeOnly ? 1 : 0) +
    (filters.platforms.size > 0 ? 1 : 0) +
    (filters.minSpent || filters.maxSpent ? 1 : 0) +
    (filters.minOrders || filters.maxOrders ? 1 : 0);

  // ... (mevcut infinite scroll effect aynı kalır)
```

- [ ] **Step 2: Filter button + modal JSX**

Filter button — mevcut sort button'unun yanına (sort `</div>` close'undan sonra, yorum satırı `{/* Filter button — Task 8 */}` yerine):

```typescript
        <button
          onClick={() => setFilterOpen(true)}
          className="px-3 py-1.5 rounded-lg bg-bg-surface border border-bg-elevated text-sm flex items-center gap-1.5"
        >
          Filtre 🎚
          {activeFilterCount > 0 && (
            <span className="bg-accent text-white text-[10px] rounded-full w-4 h-4 flex items-center justify-center">
              {activeFilterCount}
            </span>
          )}
        </button>
      </div>
```

Modal — main'in alt sonunda (`</main>` close'undan ÖNCE):

```typescript
      {filterOpen && (
        <div className="fixed inset-0 z-20 bg-black/60 flex items-end" onClick={() => setFilterOpen(false)}>
          <div
            className="w-full bg-bg-surface rounded-t-2xl max-h-[85vh] overflow-y-auto"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="px-5 py-4 border-b border-bg-elevated flex justify-between items-center">
              <h2 className="text-lg font-bold">Filtrele</h2>
              <button onClick={() => setFilterOpen(false)} className="text-text-muted">✕</button>
            </div>

            <div className="p-5 space-y-5">
              {/* Aktiflik */}
              <div>
                <label className="flex items-center gap-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={filters.activeOnly}
                    onChange={(e) => setFilters({ ...filters, activeOnly: e.target.checked })}
                  />
                  <span className="text-text">Sadece aktif (son 30 gün sipariş)</span>
                </label>
              </div>

              {/* Platform */}
              <div>
                <p className="text-text-muted text-xs uppercase tracking-wider mb-2">Platform</p>
                <div className="flex flex-wrap gap-2">
                  {ALL_PLATFORMS.map((p) => {
                    const active = filters.platforms.has(p.key);
                    return (
                      <button
                        key={p.key}
                        onClick={() => {
                          const next = new Set(filters.platforms);
                          if (active) next.delete(p.key);
                          else next.add(p.key);
                          setFilters({ ...filters, platforms: next });
                        }}
                        className={`px-3 py-1.5 rounded-lg text-sm border ${
                          active
                            ? "bg-accent text-white border-accent"
                            : "bg-bg-surface text-text border-bg-elevated"
                        }`}
                      >
                        {p.label}
                      </button>
                    );
                  })}
                </div>
              </div>

              {/* Harcama */}
              <div>
                <p className="text-text-muted text-xs uppercase tracking-wider mb-2">Harcama aralığı (₺)</p>
                <div className="flex gap-2">
                  <input
                    type="number"
                    placeholder="Min"
                    value={filters.minSpent}
                    onChange={(e) => setFilters({ ...filters, minSpent: e.target.value })}
                    className="flex-1 px-3 py-2 rounded-lg bg-bg-elevated border border-bg-elevated text-sm"
                  />
                  <input
                    type="number"
                    placeholder="Max"
                    value={filters.maxSpent}
                    onChange={(e) => setFilters({ ...filters, maxSpent: e.target.value })}
                    className="flex-1 px-3 py-2 rounded-lg bg-bg-elevated border border-bg-elevated text-sm"
                  />
                </div>
              </div>

              {/* Sipariş sayısı */}
              <div>
                <p className="text-text-muted text-xs uppercase tracking-wider mb-2">Sipariş sayısı</p>
                <div className="flex gap-2">
                  <input
                    type="number"
                    placeholder="Min"
                    value={filters.minOrders}
                    onChange={(e) => setFilters({ ...filters, minOrders: e.target.value })}
                    className="flex-1 px-3 py-2 rounded-lg bg-bg-elevated border border-bg-elevated text-sm"
                  />
                  <input
                    type="number"
                    placeholder="Max"
                    value={filters.maxOrders}
                    onChange={(e) => setFilters({ ...filters, maxOrders: e.target.value })}
                    className="flex-1 px-3 py-2 rounded-lg bg-bg-elevated border border-bg-elevated text-sm"
                  />
                </div>
              </div>
            </div>

            {/* Footer */}
            <div className="sticky bottom-0 bg-bg-surface border-t border-bg-elevated p-4 flex gap-2">
              <button
                onClick={() => setFilters(EMPTY_FILTERS)}
                className="flex-1 py-2 rounded-lg bg-bg-elevated text-text-muted text-sm"
              >
                Sıfırla
              </button>
              <button
                onClick={() => setFilterOpen(false)}
                className="flex-1 py-2 rounded-lg bg-accent text-white text-sm font-medium"
              >
                Uygula
              </button>
            </div>
          </div>
        </div>
      )}
    </main>
```

- [ ] **Step 3: Empty state — filtre sonucu yok varyantı ekle**

Mevcut `allCustomers.length === 0` block'unu güncelle — filtre aktifse farklı mesaj:

```typescript
      ) : allCustomers.length === 0 ? (
        <div className="text-center py-16">
          {activeFilterCount > 0 ? (
            <>
              <p className="text-5xl mb-3">🔍</p>
              <p className="text-text font-medium">Eşleşen müşteri yok</p>
              <button
                onClick={() => setFilters(EMPTY_FILTERS)}
                className="text-accent text-sm mt-2 underline"
              >
                Filtreleri sıfırla
              </button>
            </>
          ) : (
            <>
              <p className="text-5xl mb-3">📭</p>
              <p className="text-text font-medium">Henüz müşterin yok</p>
              <p className="text-text-muted text-xs mt-1">
                İlk satıştan sonra burada görünür.
              </p>
            </>
          )}
        </div>
      ) : (
```

- [ ] **Step 4: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck

cd ../..
git add apps/panel/src/screens/MusterilerScreen.tsx
git commit -m "feat(panel): MusterilerScreen filter modal (active / platform / spent / orderCount)"
```

---

### Task 9: Mobile PR aç

- [ ] **Step 1: Push + PR**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git push -u origin feat/musteriler-list

gh pr create --title "feat(panel): MusterilerScreen — müşteri listesi (sort + filter + cursor)" --body "$(cat <<'EOF'
## Summary

Mobile panel'de DahaFazlaScreen "Sonraki sürüm > Müşteriler" placeholder'ı
yerine gerçek liste ekranı. Yayıncının müşterilerinin filtrelenebilir +
sıralanabilir görünümü.

- 3 commit
- Sort dropdown: son sipariş (default) / toplam harcama / sipariş sayısı / ad
- Filter modal: aktiflik / platform (multi) / harcama aralığı / sipariş sayısı aralığı
- Infinite scroll (cursor pagination, server'da bağlantılı PR)
- Empty states: hiç müşteri yok / filtre sonucu yok
- Loading: 3 skeleton row
- Error: tekrar dene butonu
- Mevcut /musteriler/:customerId (CustomerDetailScreen) route'u korunur

## Dependencies

- Server PR (LiveDeck): \`feat(customers): GET /api/panel/customers list endpoint\` — önce merge edilmeli, yoksa 404

## Test plan

- [x] \`npm run typecheck\` temiz
- [ ] Android emulator smoke: liste yükle, sort değiştir, filter ekle, scroll bottom

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

PR URL'ini not al.

---

## End-to-end smoke (her iki PR merge sonrası)

Plan dışı manuel doğrulama:

- [ ] Server PR merge → deploy workflow VPS'i güncelle (broadcast posts pattern'i)
- [ ] Mobile PR merge → APK rebuild + emulator install (broadcast posts pattern'i)
- [ ] Emulator'da:
  - Daha Fazla → Müşteriler tap → liste yüklenir, default sort `Son sipariş`
  - Sort dropdown → `Toplam harcama` seç → sıra değişir
  - Filter 🎚 → "Sadece aktif" toggle → liste daralır
  - Filter → "Instagram" + "TikTok" chip seç → liste daralır
  - Filter → minSpent=500 → "Uygula" → liste daralır
  - Empty state: tüm filtreler ekle, hiç eşleşmeyen → "🔍 Eşleşen müşteri yok"
  - Liste bottom scroll → infinite scroll trigger (yeterli müşteri varsa)
  - Row tap → CustomerDetailScreen açılır

## Out of scope (bu plan dışı)

- Müşteri oluşturma/düzenleme/silme (WPF authoritative)
- Bulk actions (toplu mesaj / etiketleme)
- Favori müşteri / not ekleme
- DahaFazlaScreen'in diğer 2 placeholder'ı (Hızlı istatistik, Yayın Excel raporu) — ayrı spec/plan'lar
- Index migration (mevcut indexler yeterli)
