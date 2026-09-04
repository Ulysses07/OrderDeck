using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.Instagram;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Instagram;

public sealed class InstagramAccountServiceTests
{
    private static readonly string UserTok = $"usertok-{Guid.NewGuid():N}";
    private static readonly string PageTok = $"pagetok-{Guid.NewGuid():N}";

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body));
            return Respond(request);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid CustomerId, Guid LicenseId)> SeedAsync(
        LicenseDbContext db, bool botEnabled)
    {
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"c-{Guid.NewGuid():N}@x.tr", Name = "T" };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id,
            LicenseKey = $"lic-{Guid.NewGuid():N}",
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddYears(1)
        };
        var config = new IntakeFormConfig
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, Slug = $"s{Guid.NewGuid():N}"[..10],
            InstagramDmBotEnabled = botEnabled, IsActive = true
        };
        db.AddRange(customer, license, config);
        await db.SaveChangesAsync();
        return (customer.Id, license.Id);
    }

    private static InstagramAccountService NewService(LicenseDbContext db, StubHandler handler)
        => new(db, new HttpClient(handler), Options.Create(new FacebookOptions()),
            new EphemeralDataProtectionProvider(), NullLogger<InstagramAccountService>.Instance);

    [Fact]
    public async Task Bayrak_kapaliysa_hicbir_graph_cagrisi_yapilmaz()
    {
        using var db = NewDb();
        var (customerId, _) = await SeedAsync(db, botEnabled: false);
        var handler = new StubHandler();

        await NewService(db, handler).TryConnectAsync(customerId, UserTok, CancellationToken.None);

        handler.Requests.Should().BeEmpty();
        db.InstagramAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Bayrak_acikken_hesap_kaydedilir_ve_abonelik_yapilir()
    {
        using var db = NewDb();
        var (customerId, licenseId) = await SeedAsync(db, botEnabled: true);
        var pagesJson = $$$"""{"data":[{"id":"page-9","access_token":"{{{PageTok}}}","instagram_business_account":{"id":"ig-77","username":"royal.mezat"}}]}""";
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("/me/accounts")
                ? Json(pagesJson)
                : Json("""{"success":true}""")
        };

        await NewService(db, handler).TryConnectAsync(customerId, UserTok, CancellationToken.None);

        var acc = db.InstagramAccounts.Single();
        acc.LicenseId.Should().Be(licenseId);
        acc.PageId.Should().Be("page-9");
        acc.IgUserId.Should().Be("ig-77");
        acc.IgUsername.Should().Be("royal.mezat");
        acc.PageTokenProtected.Should().NotBeEmpty().And.NotBe(PageTok, "şifreli saklanmalı");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Uri.AbsolutePath.Should().EndWith("/page-9/subscribed_apps");
        handler.Requests[1].Body.Should().Contain("live_comments");
    }

    [Fact]
    public async Task Ig_hesabi_olmayan_sayfa_atlanir_kayit_olusmaz()
    {
        using var db = NewDb();
        var (customerId, _) = await SeedAsync(db, botEnabled: true);
        var handler = new StubHandler
        {
            Respond = _ => Json("""{"data":[{"id":"page-1","access_token":"t"}]}""")
        };

        await NewService(db, handler).TryConnectAsync(customerId, UserTok, CancellationToken.None);

        db.InstagramAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Ayni_ig_hesabi_ikinci_baglamada_guncellenir_cogalmaz()
    {
        using var db = NewDb();
        var (customerId, _) = await SeedAsync(db, botEnabled: true);
        var pagesJson2 = $$$"""{"data":[{"id":"page-9","access_token":"{{{PageTok}}}","instagram_business_account":{"id":"ig-77","username":"royal.mezat"}}]}""";
        var handler = new StubHandler
        {
            Respond = req => req.RequestUri!.AbsolutePath.Contains("/me/accounts")
                ? Json(pagesJson2)
                : Json("""{"success":true}""")
        };
        var svc = NewService(db, handler);

        await svc.TryConnectAsync(customerId, UserTok, CancellationToken.None);
        await svc.TryConnectAsync(customerId, UserTok, CancellationToken.None);

        db.InstagramAccounts.Should().HaveCount(1);
    }
}
