using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesWpfCustomersSyncControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicensesWpfCustomersSyncControllerTests(ApiFactory factory) => _factory = factory;

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
                LicenseKey = "LDK-WCS-" + Guid.NewGuid().ToString("N"),
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

    private sealed record SyncResponse(int Synced, int RetroactiveMatches);

    private static object MakeSyncItem(Guid id, string platform = "youtube", string username = "testuser",
        string? fullName = null, string? phone = null, string? address = null)
        => new
        {
            id,
            platform,
            username,
            fullName,
            phone,
            address,
            updatedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task Happy_path_inserts_new_projection_rows()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    MakeSyncItem(id1, "youtube", "user1", "Ali Veli", "+905001112233"),
                    MakeSyncItem(id2, "instagram", "user2"),
                    MakeSyncItem(id3, "tiktok", "user3"),
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.Synced.Should().Be(3);
        body.RetroactiveMatches.Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.WpfCustomerProjections.CountAsync(p => p.LicenseId == licenseId);
        count.Should().Be(3);

        var proj1 = await db.WpfCustomerProjections.FirstAsync(p => p.Id == id1);
        proj1.FullName.Should().Be("Ali Veli");
        proj1.Phone.Should().Be("+905001112233");
        proj1.Platform.Should().Be("youtube");
    }

    [Fact]
    public async Task Upsert_updates_existing_rows()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Initial sync
        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    MakeSyncItem(id1, "youtube", "olduser1", "Old Name"),
                    MakeSyncItem(id2, "instagram", "olduser2"),
                }
            });

        // Update sync — same IDs, different data. Eski istemci (format 1,
        // damgasız) kuralı: mevcut Id'nin kullanıcı adı GÜNCELLENMEZ (WPF bir
        // satırın kullanıcı adını hiç değiştirmiyor; değişseydi kimlik anahtarı
        // başka bir asıl kaydın üstüne kayabilirdi); ad yalnız boşsa doldurulur
        // (eski sürüm gerçek ad yoksa takma adı gönderiyor); dolu telefon/adres
        // son gönderimle yazılır, boş değer silmez.
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new { id = id1, platform = "youtube", username = "newuser1", fullName = "New Name",
                          phone = "+905559998877", address = "New Address", updatedAt = DateTimeOffset.UtcNow },
                    new { id = id2, platform = "instagram", username = "newuser2", fullName = (string?)null,
                          phone = (string?)null, address = (string?)null, updatedAt = DateTimeOffset.UtcNow },
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.Synced.Should().Be(2);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var proj1 = await db.WpfCustomerProjections.FirstAsync(p => p.Id == id1);
        proj1.Username.Should().Be("olduser1"); // mevcut Id'de kullanıcı adı değişmez
        proj1.FullName.Should().Be("Old Name"); // damgasız ad yalnız boşu doldurur
        // Telefon boştu: dolu değer yazılır.
        proj1.Phone.Should().Be("+905559998877");

        // No duplicate rows
        var totalCount = await db.WpfCustomerProjections.CountAsync(p => p.LicenseId == licenseId);
        totalCount.Should().Be(2);
    }

    [Fact]
    public async Task Empty_batch_returns_200_with_zero()
    {
        var (client, _, licenseId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = Array.Empty<object>() });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.Synced.Should().Be(0);
        body.RetroactiveMatches.Should().Be(0);
    }

    [Fact]
    public async Task Batch_too_large_returns_400()
    {
        var (client, _, licenseId) = await SetupAsync();

        var huge = Enumerable.Range(0, 501)
            .Select(i => MakeSyncItem(Guid.NewGuid(), "youtube", $"user{i}"))
            .ToArray();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = huge });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Bozuk öğe partiyi 400'le REDDETMEZ: reddetseydi istemci aynı partiyi
    /// sonsuza kadar yeniden gönderir, arkasındaki müşteriler rehin kalırdı
    /// (2026-08-14 olayı). Öğe yazılmadan sayılır ki istemcinin imleci ilerlesin.
    /// </summary>
    [Fact]
    public async Task Invalid_platform_item_is_counted_but_not_stored()
    {
        var (client, _, licenseId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new { id = Guid.NewGuid(), platform = "", username = "testuser",
                          fullName = (string?)null, phone = (string?)null, address = (string?)null,
                          updatedAt = DateTimeOffset.UtcNow }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SyncResponse>())!.Synced.Should().Be(1);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WpfCustomerProjections.IgnoreQueryFilters().CountAsync(p => p.LicenseId == licenseId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Different_customer_returns_404()
    {
        var (clientA, _, _) = await SetupAsync();
        var (_, _, licenseB) = await SetupAsync();

        var resp = await clientA.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseB}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(Guid.NewGuid()) } });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retroactive_match_links_existing_ShopperBroadcasterLink()
    {
        var (client, _, licenseId) = await SetupAsync();

        // Pre-seed a Shopper and a ShopperBroadcasterLink with WpfCustomerId = null.
        // Geriye dönük eşleştirme artık telefon kanıtı istiyor (bkz.
        // WpfCustomerLinkMatcher), o yüzden aşağıdaki sync payload'ı aynı
        // numarayı taşıyor.
        var phone = "+90500" + Random.Shared.Next(1_000_000, 9_999_999);
        Guid shopperId;
        Guid linkId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = "Test Shopper",
                Phone = phone,
                PhoneVerifiedAt = DateTimeOffset.UtcNow,
                PasswordHash = $"hash-{Guid.NewGuid():N}",
                Address = "Some address",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Shoppers.Add(shopper);
            shopperId = shopper.Id;

            var link = new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(),
                ShopperId = shopperId,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "matchme",
                WpfCustomerId = null,
                JoinedAt = DateTimeOffset.UtcNow
            };
            db.ShopperBroadcasterLinks.Add(link);
            linkId = link.Id;
            await db.SaveChangesAsync();
        }

        var wpfCustomerId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new { id = wpfCustomerId, platform = "youtube", username = "matchme",
                          fullName = (string?)null, phone = (string?)phone, address = (string?)null,
                          updatedAt = DateTimeOffset.UtcNow }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.Synced.Should().Be(1);
        body.RetroactiveMatches.Should().Be(1);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var link2 = await verifyDb.ShopperBroadcasterLinks.FirstAsync(l => l.Id == linkId);
        link2.WpfCustomerId.Should().Be(wpfCustomerId);
    }

    [Fact]
    public async Task Already_matched_link_not_touched()
    {
        var (client, _, licenseId) = await SetupAsync();

        var existingWpfId = Guid.NewGuid();
        Guid linkId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = "Already Matched Shopper",
                Phone = "+90500" + Guid.NewGuid().ToString("N")[..7],
                PasswordHash = "hash",
                Address = "Some address",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Shoppers.Add(shopper);

            var link = new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(),
                ShopperId = shopper.Id,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "already-matched",
                WpfCustomerId = existingWpfId,   // already has a value
                JoinedAt = DateTimeOffset.UtcNow
            };
            db.ShopperBroadcasterLinks.Add(link);
            linkId = link.Id;
            await db.SaveChangesAsync();
        }

        var newWpfCustomerId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new { id = newWpfCustomerId, platform = "youtube", username = "already-matched",
                          fullName = (string?)null, phone = (string?)null, address = (string?)null,
                          updatedAt = DateTimeOffset.UtcNow }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.RetroactiveMatches.Should().Be(0);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var link2 = await verifyDb.ShopperBroadcasterLinks.FirstAsync(l => l.Id == linkId);
        link2.WpfCustomerId.Should().Be(existingWpfId, "pre-existing WpfCustomerId must not be overwritten");
    }

    /// <summary>
    /// KVKK silmesinin kalıcılığı bu teste bağlı. Yayıncının kendi
    /// bilgisayarındaki kopya silme sırasında temizlenmiyor (WPF ingest'i var
    /// olan satırı atlıyor), yani sahadaki uygulama silinen kişinin adını ve
    /// telefonunu hâlâ taşıyor ve her push'ta buraya yolluyor. Kalkan
    /// olmasaydı, kişi yayında bir yorum yazdığı anda LastSeenAt ilerler,
    /// satır yeniden push edilir ve az önce silinen veri geri gelirdi.
    /// </summary>
    [Fact]
    public async Task Silinmis_projeksiyona_kisisel_veri_geri_yazilmaz()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(id, "youtube", "silinen", "Ayşe Yılmaz", "+905001112233", "Kadıköy") } });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var proj = await db.WpfCustomerProjections.FirstAsync(p => p.Id == id);
            proj.FullName = null;
            proj.Phone = null;
            proj.Address = null;
            proj.PurgedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(id, "youtube", "silinen", "Ayşe Yılmaz", "+905001112233", "Kadıköy") } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // Sayılıyor: istemcinin watermark'ı ilerlemezse aynı parti sonsuza
        // kadar yeniden gönderilir ve arkasındaki müşteriler rehin kalır.
        (await resp.Content.ReadFromJsonAsync<SyncResponse>())!.Synced.Should().Be(1);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var after = await verifyDb.WpfCustomerProjections.FirstAsync(p => p.Id == id);
        after.FullName.Should().BeNull();
        after.Phone.Should().BeNull();
        after.Address.Should().BeNull();
    }

    [Fact]
    public async Task Silinmis_projeksiyon_ham_payload_ile_pending_linke_baglanmaz()
    {
        var (client, _, licenseId) = await SetupAsync();
        var projectionId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = "Doğrulanmış shopper",
                Phone = phone,
                PhoneVerifiedAt = DateTimeOffset.UtcNow,
                PasswordHash = $"hash-{Guid.NewGuid():N}",
                Address = "Adres",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Shoppers.Add(shopper);
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = projectionId,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "silinmis-link",
                FullName = null,
                Phone = null,
                Address = null,
                PurgedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = linkId,
                ShopperId = shopper.Id,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "silinmis-link",
                WpfCustomerId = null,
                JoinedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    MakeSyncItem(
                        projectionId,
                        "youtube",
                        "silinmis-link",
                        "Bayat ad",
                        phone,
                        "Bayat adres"),
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SyncResponse>())!
            .RetroactiveMatches.Should().Be(0);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await verifyDb.ShopperBroadcasterLinks.FindAsync(linkId))!
            .WpfCustomerId.Should().BeNull();
        var projection = await verifyDb.WpfCustomerProjections.FindAsync(projectionId);
        projection!.FullName.Should().BeNull();
        projection.Phone.Should().BeNull();
        projection.Address.Should().BeNull();
    }

    [Fact]
    public async Task No_auth_returns_401()
    {
        var (_, _, licenseId) = await SetupAsync();
        var anon = _factory.CreateClient();

        var resp = await anon.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(Guid.NewGuid()) } });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── format 2: tam alanlar, kimlik araması, yönlendirme ──────────────────

    private sealed record Redirect(Guid Id, Guid CanonicalId);
    private sealed record SyncResponseV2(int Synced, int RetroactiveMatches, List<Redirect> Redirects);

    private static object V2Item(Guid id, string username, string? fullName = null,
        string? city = null, DateTimeOffset? addressAt = null, bool blacklisted = false,
        DateTimeOffset? blacklistAt = null, DateTimeOffset? fullNameAt = null)
        => new
        {
            id, platform = "tiktok", username, fullName, phone = (string?)null, address = (string?)null,
            updatedAt = DateTimeOffset.UtcNow, format = 2,
            fullNameChangedAt = fullNameAt,
            city, addressChangedAt = addressAt,
            isBlacklisted = blacklisted, blacklistChangedAt = blacklistAt,
        };

    [Fact]
    public async Task V2_gonderim_il_ve_kara_listeyi_tasir()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { V2Item(id, "ayse", city: "İzmir", addressAt: t, blacklisted: true, blacklistAt: t) } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = await db.WpfCustomerProjections.SingleAsync(x => x.Id == id);
        p.City.Should().Be("İzmir");
        p.IsBlacklisted.Should().BeTrue();
    }

    [Fact]
    public async Task Ayni_kimlik_farkli_Id_ile_gelirse_yonlendirme_doner_ve_kopya_baglanir()
    {
        var (client, _, licenseId) = await SetupAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { V2Item(a, "ayse", fullName: "Ayşe Kaya", fullNameAt: t) } });

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { V2Item(b, "AYSE", fullName: null, city: "İzmir", addressAt: t.AddSeconds(5)) } });
        var body = await resp.Content.ReadFromJsonAsync<SyncResponseV2>();

        body!.Redirects.Should().ContainSingle(r => r.Id == b && r.CanonicalId == a);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var canonical = await db.WpfCustomerProjections.SingleAsync(x => x.Id == a);
        canonical.FullName.Should().Be("Ayşe Kaya"); // kopyanın boş adı damgasız → dokunmaz
        canonical.City.Should().Be("İzmir");          // kopyanın damgalı adres birimi yazıldı
        // Kopya varsayılan sorgulardan gizli (A5b) — burada açıkça istenir.
        var alias = await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(x => x.Id == b);
        alias.MergedIntoId.Should().Be(a);
        alias.City.Should().BeNull();
    }

    [Fact]
    public async Task Kopyanin_daha_once_gonderilmis_siparisi_asil_kayda_tasinir()
    {
        var (client, _, licenseId) = await SetupAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { V2Item(a, "mehmet") } });
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.Add(new Order { Id = Guid.NewGuid(), LicenseId = licenseId, CustomerId = b.ToString("N"), Platform = "tiktok", Username = "mehmet", MessageText = "A1", Price = 10, AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { V2Item(b, "mehmet") } });

        using var check = _factory.Services.CreateScope();
        var db2 = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db2.Orders.SingleAsync(o => o.LicenseId == licenseId)).CustomerId.Should().Be(a.ToString("N"));
    }

    [Fact]
    public async Task Eski_istemci_yeni_surumun_damgali_adresini_ezemez()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id, platform = "tiktok", username = "zeynep", fullName = (string?)null, phone = (string?)null, address = "yeni adres", updatedAt = DateTimeOffset.UtcNow, format = 2, addressChangedAt = DateTimeOffset.UtcNow } } });

        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(id, "tiktok", "zeynep", address: "eski adres") } });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WpfCustomerProjections.SingleAsync(x => x.Id == id)).Address.Should().Be("yeni adres");
    }

    // ── TCKN: her zaman şifreli, asla kırpılmaz (PR-0 kuralı) ────────────────

    [Fact]
    public async Task Senkronlanan_TCKN_veritabaninda_sifreli_ve_cozulebilir()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id = Guid.NewGuid();
        var tc = TestTckn.NewValid();
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id, platform = "tiktok", username = "tckn-sifreli", updatedAt = DateTimeOffset.UtcNow, format = 2, tckn = tc, tcknChangedAt = DateTimeOffset.UtcNow } } });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = (await db.WpfCustomerProjections.SingleAsync(x => x.Id == id)).TcknProtected!;
        stored.Should().StartWith("CfDJ8").And.NotContain(tc);
        stored.Length.Should().BeGreaterThan(11); // kırpılmadı
        scope.ServiceProvider.GetRequiredService<TcknProtector>().Unprotect(stored).Should().Be(tc);
    }

    [Fact]
    public async Task On_bir_karakteri_asan_TCKN_mevcut_degeri_silmez()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id = Guid.NewGuid();
        var tc = TestTckn.NewValid();
        var t = DateTimeOffset.UtcNow;
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id, platform = "tiktok", username = "tckn-koru", updatedAt = t, format = 2, tckn = tc, tcknChangedAt = t } } });
        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { new { id, platform = "tiktok", username = "tckn-koru", updatedAt = t, format = 2, tckn = tc + tc, tcknChangedAt = t.AddMinutes(1) } } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK); // parti reddedilmez

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.WpfCustomerProjections.SingleAsync(x => x.Id == id);
        scope.ServiceProvider.GetRequiredService<TcknProtector>().Unprotect(row.TcknProtected).Should().Be(tc);
        // Bozuk değer birimi HİÇ gelmemiş sayılır: damga da ilerlemez. İlerleseydi
        // sunucu ile istemci aynı damgada farklı değerde kalır, hiç eşitlenmezdi.
        row.TcknChangedAt.Should().BeCloseTo(t, TimeSpan.FromMilliseconds(1));
    }

    // ── kopya ve eski sürüm kuralları (A3 kalite incelemesi) ────────────────

    [Fact]
    public async Task Bilinen_kopyanin_daha_yeni_damgali_degeri_asil_kayda_yazilir()
    {
        var (client, _, licenseId) = await SetupAsync();
        var url = $"/api/v1/licenses/{licenseId}/wpf-customers/sync";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await client.PostAsJsonAsync(url, new { customers = new[] { V2Item(a, "ayse", city: "İzmir", addressAt: t) } });
        await client.PostAsJsonAsync(url, new { customers = new[] { V2Item(b, "Ayse") } }); // b kopya olur
        await client.PostAsJsonAsync(url, new { customers = new[] { V2Item(b, "Ayse", city: "Ankara", addressAt: t.AddMinutes(1)) } });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var canonical = await db.WpfCustomerProjections.SingleAsync(x => x.Id == a);
        canonical.City.Should().Be("Ankara"); // eski FillEmpty bunu yutup yalnız damgayı ilerletirdi
        canonical.AddressChangedAt.Should().BeCloseTo(t.AddMinutes(1), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Eski_surum_takma_ad_ve_bos_alanla_gercek_veriyi_ezmez()
    {
        var (client, _, licenseId) = await SetupAsync();
        var url = $"/api/v1/licenses/{licenseId}/wpf-customers/sync";
        var id = Guid.NewGuid();
        var tel = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(id, "tiktok", "ayse_tt", "Ayşe Yılmaz", tel, "adres") } });
        // Eski sürüm gerçek ad yoksa takma adı gönderir; telefon/adres boş.
        await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(id, "tiktok", "ayse_tt", "ayse_tt") } });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = await db.WpfCustomerProjections.SingleAsync(x => x.Id == id);
        p.FullName.Should().Be("Ayşe Yılmaz");
        p.Phone.Should().Be(tel);
        p.Address.Should().Be("adres");
    }

    [Fact]
    public async Task Iki_eski_surum_telefonu_gidip_getirmez_kopya_yalniz_doldurur()
    {
        var (client, _, licenseId) = await SetupAsync();
        var url = $"/api/v1/licenses/{licenseId}/wpf-customers/sync";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        string Tel() => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var telA = Tel();
        await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(a, "tiktok", "mehmet", null, telA) } });
        // B bilgisayarı aynı kişiyi kendi Id'siyle gönderir → b kopya olur; telefonu
        // asıl kaydı EZMEZ (yalnız boşu doldururdu).
        await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(b, "tiktok", "Mehmet", null, Tel()) } });
        await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(b, "tiktok", "Mehmet", null, Tel()) } });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.WpfCustomerProjections.SingleAsync(x => x.Id == a)).Phone.Should().Be(telA);
        }

        // Asıl kaydın sahibi bilgisayarın yeni dolu telefonu eskisi gibi yazılır.
        var telA2 = Tel();
        await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(a, "tiktok", "mehmet", null, telA2) } });
        using var check = _factory.Services.CreateScope();
        var db2 = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db2.WpfCustomerProjections.SingleAsync(x => x.Id == a)).Phone.Should().Be(telA2);
    }

    /// <summary>
    /// Savunma: bilinen bir kopyanın asıl kaydı bulunamıyorsa (silinmiş satır;
    /// ya da hedef kendisi bir kopya — zinciri birleştirme işi düzleştirir)
    /// veri kopya satırına ASLA yazılmaz, yönlendirme de dönmez; öğe yine
    /// sayılır ki istemcinin imleci ilerlesin.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Asil_kaydi_bulunamayan_kopyaya_veri_yazilmaz(bool chainedTarget)
    {
        var (client, _, licenseId) = await SetupAsync();
        var alias = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var root = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            WpfCustomerProjection Row(Guid id, Guid? into) => new()
            {
                Id = id, LicenseId = licenseId, Platform = "tiktok", Username = "kayip-asil",
                MergedIntoId = into, UpdatedAt = DateTimeOffset.UtcNow,
            };
            if (chainedTarget)
                db.WpfCustomerProjections.AddRange(Row(root, null), Row(middle, root), Row(alias, middle));
            else
                db.WpfCustomerProjections.Add(Row(alias, Guid.NewGuid())); // hedef yok
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { V2Item(alias, "kayip-asil", fullName: "Gerçek Ad", fullNameAt: DateTimeOffset.UtcNow,
                city: "İzmir", addressAt: DateTimeOffset.UtcNow) } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponseV2>();
        body!.Synced.Should().Be(1);
        body.Redirects.Should().BeEmpty();

        using var check = _factory.Services.CreateScope();
        var vdb = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Kopyalar varsayılan sorgulardan gizli (A5b): zincirin TÜM halkaları görünsün.
        var rows = await vdb.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.LicenseId == licenseId).ToListAsync();
        rows.Should().OnlyContain(p => p.FullName == null && p.City == null,
            "kopya satırına da, zincirin herhangi bir halkasına da veri yazılmadı");
        rows.Single(p => p.Id == alias).MergedIntoId.Should().NotBeNull("kopya kopya olarak kalır");
    }

    // ── istemci kuyruğunu kalıcı kilitleyen yollar (A5 kalite incelemesi) ────
    // Her biri eskiden HER denemede aynı hatayı veriyordu: istemcinin imleci
    // ilerlemez, aynı parti sonsuza kadar yeniden gönderilir.

    private static string NewPhone() => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private async Task<List<WpfCustomerProjection>> RowsAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Kopyalar varsayılan sorgulardan gizli (A5b) — burada hepsi görünsün.
        return await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.LicenseId == licenseId).ToListAsync();
    }

    private async Task PurgeAsync(Guid projectionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WpfCustomerProjections.SingleAsync(p => p.Id == projectionId)).MarkPurged(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Ayni_Id_partide_iki_kez_gelirse_son_giris_kazanir()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id = Guid.NewGuid();

        // Eskiden ikinci giriş aynı yeni satırı ikinci kez izlemeye ekliyordu →
        // izleme istisnası → 500.
        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[]
            {
                MakeSyncItem(id, "tiktok", "ciftgelen", "İlk Ad"),
                MakeSyncItem(id, "tiktok", "ciftgelen", "Son Ad"),
            } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SyncResponseV2>())!.Synced.Should().Be(1, "tekilleştirilmiş öğe sayısı");
        (await RowsAsync(licenseId)).Should().ContainSingle()
            .Which.FullName.Should().Be("Son Ad", "payload sırası istemcinin niyet sırası — son giriş kazanır");
    }

    [Fact]
    public async Task Baska_lisansta_kayitli_Id_yazilmaz_ve_sayilir()
    {
        var (clientA, _, licenseA) = await SetupAsync();
        var (clientB, _, licenseB) = await SetupAsync();
        var id = Guid.NewGuid();
        var telA = NewPhone();
        (await clientA.PostAsJsonAsync($"/api/v1/licenses/{licenseA}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(id, "tiktok", "a-musterisi", "A Müşterisi", telA) } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Ör. yeniden verilen bir lisansın bilgisayarı aynı yerel Id'leri yeniden
        // gönderir. Eskiden lisansa göre aranan Id "yeni" sanılıyor, INSERT
        // birincil anahtara çarpıp 500 veriyordu.
        var resp = await clientB.PostAsJsonAsync($"/api/v1/licenses/{licenseB}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(id, "tiktok", "b-musterisi", "B Müşterisi") } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponseV2>();
        body!.Synced.Should().Be(1, "yazılmadan sayılır — istemcinin imleci ilerlemeli");
        body.Redirects.Should().BeEmpty();
        (await RowsAsync(licenseB)).Should().BeEmpty();
        var row = (await RowsAsync(licenseA)).Should().ContainSingle().Which;
        row.Id.Should().Be(id);
        row.Username.Should().Be("a-musterisi");
        row.FullName.Should().Be("A Müşterisi");
        row.Phone.Should().Be(telA);
    }

    [Fact]
    public async Task Gecersiz_kullanici_adi_partiyi_reddetmez_gecerli_oge_yazilir()
    {
        var (client, _, licenseId) = await SetupAsync();
        var valid = Guid.NewGuid();
        var invalid = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[]
            {
                MakeSyncItem(invalid, "tiktok", new string('x', 129), "Uzun Adlı"),
                MakeSyncItem(valid, "tiktok", "gecerli", "Geçerli Müşteri"),
            } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SyncResponseV2>())!.Synced.Should().Be(2,
            "bozuk öğe yazılmadan sayılır, geçerli öğe yazılır");
        (await RowsAsync(licenseId)).Should().ContainSingle()
            .Which.Should().Match<WpfCustomerProjection>(p => p.Id == valid && p.FullName == "Geçerli Müşteri");
    }

    // ── silinmiş (KVKK) asıl kayda bağlanan kopya ───────────────────────────

    [Fact]
    public async Task Yeni_kopya_silinmis_asil_kayda_baglanir_asil_kayda_yazilmaz()
    {
        var (client, _, licenseId) = await SetupAsync();
        var url = $"/api/v1/licenses/{licenseId}/wpf-customers/sync";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(a, "tiktok", "silinen-kisi", "Silinen Kişi") } });
        await PurgeAsync(a);

        // Başka bilgisayar aynı kişiyi kendi Id'siyle, damgalı verisiyle gönderir.
        var t = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync(url, new { customers = new[]
        {
            new { id = b, platform = "tiktok", username = "Silinen-Kisi", fullName = "Geri Gelen Ad",
                  phone = NewPhone(), address = (string?)null, updatedAt = t, format = 2,
                  fullNameChangedAt = t, phoneChangedAt = t },
        } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SyncResponseV2>())!.Redirects
            .Should().ContainSingle(r => r.Id == b && r.CanonicalId == a);
        var rows = await RowsAsync(licenseId);
        var canonical = rows.Single(p => p.Id == a);
        canonical.PurgedAt.Should().NotBeNull();
        canonical.FullName.Should().BeNull("silme bir başka bilgisayarın gönderimiyle geri alınmamalı");
        canonical.Phone.Should().BeNull();
        var alias = rows.Single(p => p.Id == b);
        alias.MergedIntoId.Should().Be(a);
        alias.FullName.Should().BeNull();
        alias.Phone.Should().BeNull();
    }

    [Fact]
    public async Task Bilinen_kopya_silinmis_asil_kayda_hicbir_sey_yazmaz()
    {
        var (client, _, licenseId) = await SetupAsync();
        var url = $"/api/v1/licenses/{licenseId}/wpf-customers/sync";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await client.PostAsJsonAsync(url, new { customers = new[] { V2Item(a, "silinecek") } });
        await client.PostAsJsonAsync(url, new { customers = new[] { V2Item(b, "Silinecek") } }); // b kopya olur
        await PurgeAsync(a);

        var t = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync(url, new { customers = new[]
        {
            V2Item(b, "Silinecek", fullName: "Bayat Ad", fullNameAt: t, city: "İzmir", addressAt: t),
        } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponseV2>();
        body!.Synced.Should().Be(1);
        body.Redirects.Should().ContainSingle(r => r.Id == b && r.CanonicalId == a);
        (await RowsAsync(licenseId)).Should().OnlyContain(p => p.FullName == null && p.City == null,
            "silinmiş asıl kayda da kopya satırına da hiçbir şey yazılmadı");
    }

    // ── aynı kişinin tek partideki biçimleri ─────────────────────────────────

    [Fact]
    public async Task Ayni_partide_bilinen_kopya_ve_asil_kaydin_kendi_ogesi()
    {
        var (client, _, licenseId) = await SetupAsync();
        var url = $"/api/v1/licenses/{licenseId}/wpf-customers/sync";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        await client.PostAsJsonAsync(url, new { customers = new[] { V2Item(a, "hatice") } });
        await client.PostAsJsonAsync(url, new { customers = new[] { V2Item(b, "Hatice") } }); // b kopya olur

        // Aynı partide asıl kaydın kendi öğesi (daha yeni damga) ÖNCE, kopyanınki
        // (daha eski damga) SONRA: sıra değil damga kazanır.
        var resp = await client.PostAsJsonAsync(url, new { customers = new[]
        {
            V2Item(a, "hatice", city: "İzmir", addressAt: t.AddMinutes(2)),
            V2Item(b, "Hatice", city: "Ankara", addressAt: t.AddMinutes(1)),
        } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponseV2>();
        body!.Synced.Should().Be(2);
        body.Redirects.Should().ContainSingle(r => r.Id == b && r.CanonicalId == a);
        var rows = await RowsAsync(licenseId);
        rows.Should().HaveCount(2);
        var canonical = rows.Single(p => p.Id == a);
        canonical.City.Should().Be("İzmir");
        canonical.AddressChangedAt.Should().BeCloseTo(t.AddMinutes(2), TimeSpan.FromMilliseconds(1));
        rows.Single(p => p.Id == b).City.Should().BeNull();
    }

    [Fact]
    public async Task Ayni_partide_ayni_kimligin_iki_yeni_Idsi_ikincisi_birincinin_kopyasi_olur()
    {
        var (client, _, licenseId) = await SetupAsync();
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        // y'nin bu bilgisayardan daha önce gönderilmiş bir siparişi var.
        var orderId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.Add(new Order { Id = orderId, LicenseId = licenseId, CustomerId = y.ToString("N"), Platform = "tiktok", Username = "ZEHRA", MessageText = "B7", Price = 10, AddedAt = t, UpdatedAt = t });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { V2Item(x, "zehra"), V2Item(y, "ZEHRA", city: "Bursa", addressAt: t) } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponseV2>();
        body!.Synced.Should().Be(2);
        body.Redirects.Should().ContainSingle(r => r.Id == y && r.CanonicalId == x);
        var rows = await RowsAsync(licenseId);
        rows.Single(p => p.Id == x).Should().Match<WpfCustomerProjection>(p => p.MergedIntoId == null && p.City == "Bursa");
        rows.Single(p => p.Id == y).MergedIntoId.Should().Be(x);
        using var check = _factory.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<LicenseDbContext>().Orders.SingleAsync(o => o.Id == orderId))
            .CustomerId.Should().Be(x.ToString("N"), "aynı partide açılan asıl kayda taşındı");
    }

    // ── geriye dönük eşleştirme yönlendirme hedefinde de çalışır ────────────

    [Fact]
    public async Task Kopya_gonderimi_asil_kaydin_telefonunu_doldurursa_bekleyen_baglanti_ayni_istekte_kurulur()
    {
        var (client, _, licenseId) = await SetupAsync();
        var url = $"/api/v1/licenses/{licenseId}/wpf-customers/sync";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var phone = NewPhone();
        // Asıl kayıt telefonsuz → kanıt yok → bağlantı beklemede.
        await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(a, "tiktok", "kemal") } });
        Guid linkId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = "Kemal Shopper",
                Phone = phone,
                PhoneVerifiedAt = DateTimeOffset.UtcNow,
                PasswordHash = $"hash-{Guid.NewGuid():N}",
                Address = "Adres",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Shoppers.Add(shopper);
            var link = new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(),
                ShopperId = shopper.Id,
                LicenseId = licenseId,
                Platform = "tiktok",
                Username = "kemal",
                WpfCustomerId = null,
                JoinedAt = DateTimeOffset.UtcNow,
            };
            db.ShopperBroadcasterLinks.Add(link);
            linkId = link.Id;
            await db.SaveChangesAsync();
        }

        // B bilgisayarı aynı kişiyi kendi Id'siyle ve telefonuyla gönderir: b
        // kopya olur, telefon asıl kayda dolar. Eşleştirme asıl kaydın kendi
        // bilgisayarının gönderimini beklememeli.
        var resp = await client.PostAsJsonAsync(url, new { customers = new[] { MakeSyncItem(b, "tiktok", "Kemal", null, phone) } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponseV2>();
        body!.Redirects.Should().ContainSingle(r => r.Id == b && r.CanonicalId == a);
        body.RetroactiveMatches.Should().Be(1);
        using var check = _factory.Services.CreateScope();
        var cdb = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await cdb.ShopperBroadcasterLinks.SingleAsync(l => l.Id == linkId)).WpfCustomerId.Should().Be(a);
    }

    // ── Bölüm B'deki tekil indeksten önce: birden çok asıl kayıt ─────────────

    [Fact]
    public async Task Ayni_kimligin_birden_cok_asil_kaydi_varsa_secim_belirleyici()
    {
        var (client, _, licenseId) = await SetupAsync();
        var sorted = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderBy(g => g).ToArray();
        var (small, large) = (sorted[0], sorted[1]);
        var t = DateTimeOffset.UtcNow;
        // Aynı UpdatedAt; BÜYÜK Id önce eklenir — seçim sorgu sırasına kalmasın.
        foreach (var id in new[] { large, small })
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = id, LicenseId = licenseId, Platform = "tiktok", Username = "ikiz", UpdatedAt = t,
            });
            await db.SaveChangesAsync();
        }

        var n = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { V2Item(n, "ikiz") } });

        (await resp.Content.ReadFromJsonAsync<SyncResponseV2>())!.Redirects
            .Should().ContainSingle(r => r.Id == n && r.CanonicalId == small,
                "eşit UpdatedAt'te küçük Id seçilir — her istek aynı asıl kaydı seçmeli");
    }
}
