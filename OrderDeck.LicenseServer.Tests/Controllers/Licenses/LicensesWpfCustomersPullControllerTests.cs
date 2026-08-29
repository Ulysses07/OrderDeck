using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesWpfCustomersPullControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicensesWpfCustomersPullControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record WpfCustomerPullItem(
        Guid Id, string Platform, string Username,
        string? FullName, string? Phone, string? Address, DateTimeOffset UpdatedAt,
        DateTimeOffset? PurgedAt);

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-PULL-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, customerId, licenseId);
    }

    private async Task SeedProjectionAsync(Guid licenseId, Guid id, string platform, string username,
        string? fullName = null, string? phone = null, string? address = null, DateTimeOffset? updatedAt = null,
        DateTimeOffset? purgedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = id,
            LicenseId = licenseId,
            Platform = platform,
            Username = username,
            FullName = fullName,
            Phone = phone,
            Address = address,
            // Varsayılan damga kararlılık ufkunun gerisinde: uç, son saniyelerde
            // değişen satırları bilerek okumuyor (bkz. ReverseSyncCursor).
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            PurgedAt = purgedAt,
        });
        await db.SaveChangesAsync();
    }

    // ── Happy path: returns projections newer than since cursor ──────────────

    [Fact]
    public async Task Since_returns_projections_newer_than_cursor()
    {
        var (client, _, licenseId) = await SetupAsync();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);
        var t3 = t0.AddMinutes(3);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        await SeedProjectionAsync(licenseId, id1, "youtube", "user1", "Ali", "+905001112233", "Ankara", t1);
        await SeedProjectionAsync(licenseId, id2, "instagram", "user2", null, null, null, t2);
        await SeedProjectionAsync(licenseId, id3, "tiktok", "user3", null, null, null, t3);

        // Bileşik imleç (t1, id1) → yalnız id2 ve id3
        var since = Uri.EscapeDataString(t1.ToString("O"));
        var resp = await client.GetAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}&sinceId={id1:D}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        items.Should().NotBeNull();
        items!.Should().HaveCount(2);
        items.Select(i => i.Id).Should().BeEquivalentTo(new[] { id2, id3 });
    }

    // ── sinceId göndermeyen eski istemci: imleçteki satır tekrar gelir ────────
    // Kayıp değil fazlalık; WPF tarafındaki uygulama idempotent upsert.

    [Fact]
    public async Task Since_without_sinceId_redelivers_the_row_at_the_cursor()
    {
        var (client, _, licenseId) = await SetupAsync();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        await SeedProjectionAsync(licenseId, id1, "youtube", "legacy1", updatedAt: t1);
        await SeedProjectionAsync(licenseId, id2, "youtube", "legacy2", updatedAt: t2);

        var since = Uri.EscapeDataString(t1.ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        items!.Select(i => i.Id).Should().BeEquivalentTo(new[] { id1, id2 },
            "sinceId yoksa boş GUID varsayılır; imleçteki eşitlik kümesi yeniden gönderilir");
    }

    // ── Eşitlik kümesi sayfa sınırında kesilirse hiçbir satır kaybolmaz ──────

    [Fact]
    public async Task Since_tie_group_split_across_pages_loses_nothing()
    {
        var (client, _, licenseId) = await SetupAsync();

        // Tek push'un damgaladığı gibi: 5 satır AYNI UpdatedAt değerinde.
        var tie = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < ids.Count; i++)
            await SeedProjectionAsync(licenseId, ids[i], "youtube", $"tieuser{i}", updatedAt: tie);

        var cursor = tie;
        var cursorId = Guid.Empty;
        var seen = new List<Guid>();

        // take=2 ile sayfala: eşitlik kümesi iki kez ortadan kesiliyor.
        for (var page = 0; page < 5; page++)
        {
            var resp = await client.GetAsync(
                $"/api/v1/licenses/{licenseId}/wpf-customers/since" +
                $"?since={Uri.EscapeDataString(cursor.ToString("O"))}&sinceId={cursorId:D}&take=2");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
            if (items!.Count == 0) break;

            seen.AddRange(items.Select(i => i.Id));
            cursor = items[^1].UpdatedAt;
            cursorId = items[^1].Id;
        }

        seen.Should().BeEquivalentTo(ids,
            "aynı damgayı paylaşan satırlar sayfa sınırında kesilse de hepsi teslim edilmeli");
    }

    // ── Kararlılık ufku: az önce yazılmış satır bu turda okunmaz ─────────────

    [Fact]
    public async Task Since_withholds_rows_written_inside_the_stability_horizon()
    {
        var (client, _, licenseId) = await SetupAsync();

        var settled = Guid.NewGuid();
        var justWritten = Guid.NewGuid();
        await SeedProjectionAsync(licenseId, settled, "youtube", "settled",
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await SeedProjectionAsync(licenseId, justWritten, "youtube", "justwritten",
            updatedAt: DateTimeOffset.UtcNow);

        var since = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}");

        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        items!.Select(i => i.Id).Should().Contain(settled);
        items.Select(i => i.Id).Should().NotContain(justWritten,
            "ufkun içindeki satır okunursa, ondan önce başlamış ama henüz commit etmemiş " +
            "bir işlem imlecin gerisinde kalıp kalıcı olarak kaybolur");
    }

    // ── since=MinValue returns all projections ───────────────────────────────

    [Fact]
    public async Task Since_min_value_returns_all_projections()
    {
        var (client, _, licenseId) = await SetupAsync();

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        await SeedProjectionAsync(licenseId, id1, "youtube", "alluser1");
        await SeedProjectionAsync(licenseId, id2, "twitch", "alluser2");

        var since = Uri.EscapeDataString(DateTimeOffset.MinValue.ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        items!.Select(i => i.Id).Should().Contain(new[] { id1, id2 });
    }

    // ── Empty result when no new projections ─────────────────────────────────

    [Fact]
    public async Task Since_returns_empty_when_no_new_projections()
    {
        var (client, _, licenseId) = await SetupAsync();

        var past = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedProjectionAsync(licenseId, Guid.NewGuid(), "youtube", "olduser", updatedAt: past);

        // Query with a future cursor → nothing
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={future}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        items.Should().BeEmpty();
    }

    // ── take parameter is respected ──────────────────────────────────────────

    [Fact]
    public async Task Since_take_limits_result_count()
    {
        var (client, _, licenseId) = await SetupAsync();

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 5; i++)
            await SeedProjectionAsync(licenseId, Guid.NewGuid(), "youtube", $"takeuser{i}",
                updatedAt: baseTime.AddSeconds(i + 1));

        var since = Uri.EscapeDataString(baseTime.ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}&take=3");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        items!.Should().HaveCount(3);
    }

    // ── Unauthenticated → 401 ────────────────────────────────────────────────

    [Fact]
    public async Task Since_no_auth_returns_401()
    {
        var (_, _, licenseId) = await SetupAsync();
        var anon = _factory.CreateClient();

        var since = Uri.EscapeDataString(DateTimeOffset.MinValue.ToString("O"));
        var resp = await anon.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Different customer → 404 (isolation) ─────────────────────────────────

    [Fact]
    public async Task Since_different_customer_returns_404()
    {
        var (clientA, _, _) = await SetupAsync();
        var (_, _, licenseBId) = await SetupAsync();

        var since = Uri.EscapeDataString(DateTimeOffset.MinValue.ToString("O"));
        var resp = await clientA.GetAsync($"/api/v1/licenses/{licenseBId}/wpf-customers/since?since={since}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Results ordered by UpdatedAt ascending ───────────────────────────────

    [Fact]
    public async Task Since_results_ordered_by_UpdatedAt_ascending()
    {
        var (client, _, licenseId) = await SetupAsync();

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var idFirst = Guid.NewGuid();
        var idMiddle = Guid.NewGuid();
        var idLast = Guid.NewGuid();

        // Seed in reverse order to verify ordering is applied by server
        await SeedProjectionAsync(licenseId, idLast, "youtube", "orderuser3", updatedAt: baseTime.AddSeconds(3));
        await SeedProjectionAsync(licenseId, idFirst, "youtube", "orderuser1", updatedAt: baseTime.AddSeconds(1));
        await SeedProjectionAsync(licenseId, idMiddle, "youtube", "orderuser2", updatedAt: baseTime.AddSeconds(2));

        var since = Uri.EscapeDataString(baseTime.ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        items!.Should().HaveCount(3);
        items[0].Id.Should().Be(idFirst);
        items[1].Id.Should().Be(idMiddle);
        items[2].Id.Should().Be(idLast);
    }

    // ── Data fields are included correctly ───────────────────────────────────

    [Fact]
    public async Task Since_returns_correct_field_values()
    {
        var (client, _, licenseId) = await SetupAsync();

        var id = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SeedProjectionAsync(licenseId, id, "youtube", "fielduser", "Full Name", "+905001112233", "Istanbul", updatedAt);

        var since = Uri.EscapeDataString(updatedAt.AddSeconds(-1).ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        items!.Should().HaveCount(1);
        var item = items[0];
        item.Id.Should().Be(id);
        item.Platform.Should().Be("youtube");
        item.Username.Should().Be("fielduser");
        item.FullName.Should().Be("Full Name");
        item.Phone.Should().Be("+905001112233");
        item.Address.Should().Be("Istanbul");
        item.PurgedAt.Should().BeNull("silinmemiş satırda damga boş olmalı");
    }

    // ── KVKK: silinen satır ELENMİYOR, damgayla gönderiliyor ─────────────────
    //
    // Bu uç, yayıncının kendi bilgisayarındaki üçüncü kopyaya ulaşan TEK yol.
    // Satır yanıttan düşseydi WPF silmeyi hiç duymaz, kişisel veri orada
    // süresiz kalırdı (bkz. ShopperPurgeService adım 3).

    [Fact]
    public async Task Since_purged_row_is_delivered_with_the_stamp()
    {
        var (client, _, licenseId) = await SetupAsync();

        var id = Guid.NewGuid();
        var purgedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        // Purge sonrası satırın hâli: kişisel alanlar zaten boş.
        await SeedProjectionAsync(licenseId, id, "youtube", "silinenkullanici",
            fullName: null, phone: null, address: null,
            updatedAt: purgedAt, purgedAt: purgedAt);

        var since = Uri.EscapeDataString(purgedAt.AddSeconds(-1).ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/wpf-customers/since?since={since}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<WpfCustomerPullItem>>();
        var item = items!.Single(i => i.Id == id);
        item.PurgedAt.Should().NotBeNull("WPF'in yerel kopyayı temizleyebilmesi için işaret şart");
        item.Username.Should().Be("silinenkullanici",
            "kimlik anahtarı kalmalı — WPF eşleşmeyi (Platform, Username) ile yapıyor");
    }
}
