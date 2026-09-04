using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.Instagram;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Instagram;

public sealed class InstagramLiveCommentJobTests
{
    // Payload yardımcı sabiti
    private static string Payload(string igUserId, string commentId, string fromId,
        string fromUsername, string text)
        => "{\"object\":\"instagram\",\"entry\":[{\"id\":\"" + igUserId + "\",\"time\":1725400000," +
           "\"changes\":[{\"field\":\"live_comments\",\"value\":{\"id\":\"" + commentId + "\"," +
           "\"from\":{\"id\":\"" + fromId + "\",\"username\":\"" + fromUsername + "\"}," +
           "\"text\":\"" + text + "\",\"media\":{\"id\":\"m1\",\"media_product_type\":\"LIVE\"}}}]}]}";

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body, string? AuthHeader)> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"recipient_id":"1","message_id":"m1"}""",
                    Encoding.UTF8, "application/json")
            };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            return Respond(request);
        }
    }

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions());

    private static async Task<(string IgUserId, string PageId, string Slug, Guid LicenseId)> SeedAsync(
        LicenseDbContext db,
        EphemeralDataProtectionProvider dpProvider,
        bool isActive = true,
        bool botEnabled = true)
    {
        var igUserId = $"ig-{Guid.NewGuid():N}"[..12];
        var pageId = $"page-{Guid.NewGuid():N}"[..10];
        var slug = $"sl{Guid.NewGuid():N}"[..10];
        var rawPageToken = $"pagetok-{Guid.NewGuid():N}";

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"c-{Guid.NewGuid():N}@x.tr",
            Name = "Test"
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = $"lic-{Guid.NewGuid():N}",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1)
        };
        var config = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            IsActive = isActive,
            InstagramDmBotEnabled = botEnabled,
            WhatsAppPhone = "+905550000000"
        };
        var protector = dpProvider.CreateProtector(InstagramAccountService.ProtectorPurpose);
        var acc = new InstagramAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            PageId = pageId,
            IgUserId = igUserId,
            IgUsername = "testuser",
            PageTokenProtected = protector.Protect(rawPageToken),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow
        };

        db.AddRange(customer, license, config, acc);
        await db.SaveChangesAsync();
        return (igUserId, pageId, slug, license.Id);
    }

    private static (InstagramLiveCommentJob Job, StubHandler Handler) NewJob(
        LicenseDbContext db,
        EphemeralDataProtectionProvider dpProvider,
        IMemoryCache? cache = null)
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var replyClient = new InstagramPrivateReplyClient(
            http, Options.Create(new FacebookOptions()),
            NullLogger<InstagramPrivateReplyClient>.Instance);
        var accountService = new InstagramAccountService(
            db, new HttpClient(new StubHandler()), Options.Create(new FacebookOptions()),
            dpProvider, NullLogger<InstagramAccountService>.Instance);
        var tokenService = new IntakeIgTokenService(dpProvider);
        var job = new InstagramLiveCommentJob(
            db, accountService, replyClient, tokenService,
            cache ?? NewCache(),
            NullLogger<InstagramLiveCommentJob>.Instance);
        return (job, handler);
    }

    [Fact]
    public async Task Kayit_yazinca_dm_gider_ve_linkte_gecerli_token_var()
    {
        var dp = new EphemeralDataProtectionProvider();
        using var db = NewDb();
        var (igUserId, pageId, slug, _) = await SeedAsync(db, dp);
        var (job, handler) = NewJob(db, dp);

        await job.ProcessAsync(
            Payload(igUserId, "cmt-1", "viewer-1", "izleyici", "!kayıt"),
            CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var req = handler.Requests.Single();
        req.Uri.AbsolutePath.Should().EndWith($"/{pageId}/messages");

        var link = ExtractLink(req.Body);
        link.Should().StartWith($"https://orderdeckapp.com/musteri-kayit/{slug}?ig=");

        var tokenPart = link[(link.IndexOf("?ig=") + 4)..];
        var tokenSvc = new IntakeIgTokenService(dp);
        var read = tokenSvc.TryRead(tokenPart);
        read.Should().Be((slug, "izleyici"));
    }

    [Fact]
    public async Task Buyuk_harf_ve_noktasiz_i_varyantlari_tetikler()
    {
        var dp = new EphemeralDataProtectionProvider();
        using var db = NewDb();
        var (igUserId, pageId, _, _) = await SeedAsync(db, dp);
        var (job, handler) = NewJob(db, dp);

        await job.ProcessAsync(
            Payload(igUserId, "cmt-A", "viewer-A", "a1", "!KAYIT"),
            CancellationToken.None);
        await job.ProcessAsync(
            Payload(igUserId, "cmt-B", "viewer-B", "b1", "!Kayit"),
            CancellationToken.None);
        await job.ProcessAsync(
            Payload(igUserId, "cmt-C", "viewer-C", "c1", "!kayit"),
            CancellationToken.None);

        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Alakasiz_yorum_dm_uretmez()
    {
        var dp = new EphemeralDataProtectionProvider();
        using var db = NewDb();
        var (igUserId, _, _, _) = await SeedAsync(db, dp);
        var (job, handler) = NewJob(db, dp);

        await job.ProcessAsync(
            Payload(igUserId, "cmt-1", "viewer-1", "biri", "merhaba 105 yazdım"),
            CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Bilinmeyen_ig_hesabi_sessizce_atlanir()
    {
        var dp = new EphemeralDataProtectionProvider();
        using var db = NewDb();
        await SeedAsync(db, dp);
        var (job, handler) = NewJob(db, dp);

        // DB'de kayıtlı olmayan bir igUserId
        await job.ProcessAsync(
            Payload("ig-bilinmeyen-999", "cmt-1", "viewer-1", "biri", "!kayıt"),
            CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Ayni_izleyiciye_bir_saat_icinde_ikinci_dm_gitmez()
    {
        var dp = new EphemeralDataProtectionProvider();
        using var db = NewDb();
        var (igUserId, _, _, _) = await SeedAsync(db, dp);
        var cache = NewCache();
        var (job, handler) = NewJob(db, dp, cache);

        await job.ProcessAsync(
            Payload(igUserId, "cmt-1", "viewer-same", "izleyici", "!kayıt"),
            CancellationToken.None);
        await job.ProcessAsync(
            Payload(igUserId, "cmt-2", "viewer-same", "izleyici", "!kayıt"),
            CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Pasif_form_dm_uretmez()
    {
        var dp = new EphemeralDataProtectionProvider();
        using var db = NewDb();
        var (igUserId, _, _, _) = await SeedAsync(db, dp, isActive: false);
        var (job, handler) = NewJob(db, dp);

        await job.ProcessAsync(
            Payload(igUserId, "cmt-1", "viewer-1", "biri", "!kayıt"),
            CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Bot_kapali_config_dm_uretmez()
    {
        var dp = new EphemeralDataProtectionProvider();
        using var db = NewDb();
        var (igUserId, _, _, _) = await SeedAsync(db, dp, botEnabled: false);
        var (job, handler) = NewJob(db, dp);

        await job.ProcessAsync(
            Payload(igUserId, "cmt-1", "viewer-1", "biri", "!kayıt"),
            CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    // DM gövdesindeki linki çıkarmak için yardımcı
    private static string ExtractLink(string body)
    {
        // Gövde JSON — "message":"..." içinde link var
        var marker = "orderdeckapp.com/musteri-kayit/";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        // https:// 8 karakter geri
        start -= 8;
        var end = body.IndexOf('"', start);
        return end < 0 ? body[start..] : body[start..end];
    }
}
