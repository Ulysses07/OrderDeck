using System.Net.Http.Json;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.MemoryStorage;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Services.Push;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly string _backupRoot = Path.Combine(Path.GetTempPath(), $"orderdeck-backup-{Guid.NewGuid():N}");

    public TestEmailSender Email { get; } = new();

    public RecordingNotificationSender Push { get; } = new();

    public RecordingSmsSender Sms { get; } = new();

    public FakeBroadcastMediaStorage BroadcastMedia { get; } = new();

    public string BackupRoot => _backupRoot;

    /// <summary>Override in derived test fixture to inject extra in-memory config
    /// keys (e.g. tighter rate limits, quota caps). Default: no overrides.</summary>
    protected virtual IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>();

    /// <summary>Override in derived test fixture to add DbContext-level options
    /// (e.g. a fault-injecting <c>SaveChanges</c> interceptor). Runs on the same
    /// options builder as the InMemory provider swap, so the shared DB name is
    /// preserved. Default: no extra options.</summary>
    protected virtual void ConfigureDbContextOptions(DbContextOptionsBuilder opt) { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "test-secret-key-must-be-at-least-32-bytes-long-for-hs256",
                ["Jwt:Issuer"] = "orderdeck-license-server",
                ["Email:Provider"] = "disk",
                ["App:PublicBaseUrl"] = "https://test.local",
                ["Backup:MasterKeyHex"] = new string('a', 64),
                ["Backup:StorageRoot"] = _backupRoot,
                ["Backup:MaxBlobSizeMb"] = "200",
                // Disable per-customer quota in tests so existing roundtrip
                // assertions don't accidentally trip on it; quota path tested
                // separately by setting this to a tiny value in the relevant test.
                ["Backup:PerCustomerQuotaMb"] = "0",
            });
            // Per-fixture overrides — applied AFTER defaults so they win.
            cfg.AddInMemoryCollection(ExtraConfig);
        });

        builder.ConfigureServices(services =>
        {
            // Precise removal — only the three descriptors that conflict for LicenseDbContext.
            // IDbContextOptionsConfiguration<T> holds the provider-specific config callback
            // (e.g. UseSqlServer); without removing it, both SqlServer and InMemory callbacks
            // are applied when EF Core builds the context options, triggering EF Core 9's
            // "two providers registered" guard.
            services.RemoveAll<IDbContextOptionsConfiguration<LicenseDbContext>>();

            var optionsDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<LicenseDbContext>));
            if (optionsDescriptor is not null) services.Remove(optionsDescriptor);

            var ctxDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(LicenseDbContext));
            if (ctxDescriptor is not null) services.Remove(ctxDescriptor);

            services.AddDbContext<LicenseDbContext>(opt =>
            {
                opt.UseInMemoryDatabase(_dbName);
                ConfigureDbContextOptions(opt);
            });

            // Phase 5e: read-only DbContext shares the same in-memory DB in tests
            // so any code that injects LicenseReadOnlyDbContext sees the seed data
            // committed via LicenseDbContext. Production split (replica vs primary)
            // is the operator's concern; tests intentionally collapse both onto one
            // store.
            var roOptionsDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<LicenseReadOnlyDbContext>));
            if (roOptionsDescriptor is not null) services.Remove(roOptionsDescriptor);
            var roCtxDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(LicenseReadOnlyDbContext));
            if (roCtxDescriptor is not null) services.Remove(roCtxDescriptor);
            services.RemoveAll<IDbContextOptionsConfiguration<LicenseReadOnlyDbContext>>();
            services.AddDbContext<LicenseReadOnlyDbContext>(opt =>
                opt.UseInMemoryDatabase(_dbName));

            var emailDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEmailSender));
            if (emailDescriptor is not null) services.Remove(emailDescriptor);
            services.AddSingleton<IEmailSender>(Email);

            // Push sender override — RecordingNotificationSender tüm bildirimi yakalar.
            services.RemoveAll<INotificationSender>();
            services.AddSingleton<INotificationSender>(Push);

            // SMS sender override — RecordingSmsSender gönderilen mesajları yakalar
            // (gerçek Netgsm çağrısı yok).
            services.RemoveAll<OrderDeck.LicenseServer.Services.Sms.ISmsSender>();
            services.AddSingleton<OrderDeck.LicenseServer.Services.Sms.ISmsSender>(Sms);

            // Broadcast media storage override — FakeBroadcastMediaStorage UploadCalls'ı kaydeder.
            services.RemoveAll<OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage>();
            services.AddSingleton<OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage>(BroadcastMedia);

            // Hangfire — production SQL Server yerine InMemory storage (test isolation)
            services.AddHangfire(cfg => cfg
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseMemoryStorage());

            // Disable rate limiting in tests — remove all IConfigureOptions<RateLimiterOptions>
            // registrations (added by AddRateLimiter in Program.cs) and register a fresh
            // unlimited configuration so auth tests don't get 429.
            //
            // DİKKAT: bu liste Program.cs'teki politika adlarını ELLE yansıtıyor ve
            // eksiği sessiz kalmıyor — [EnableRateLimiting("x")] taşıyan bir ucun
            // karşılığı burada yoksa istek 429 değil 500 oluyor ("no such policy
            // exists") ve o ucun BÜTÜN testleri düşüyor. Program.cs'e yeni politika
            // eklerken buraya da ekle.
            services.RemoveAll<Microsoft.Extensions.Options.IConfigureOptions<RateLimiterOptions>>();
            services.RemoveAll<Microsoft.Extensions.Options.IPostConfigureOptions<RateLimiterOptions>>();
            services.AddRateLimiter(opts =>
            {
                opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                foreach (var policy in new[]
                {
                    "auth-register", "auth-login", "auth-refresh",
                    "intake-form-submit", "youtube-verify",
                    "backup-upload", "backup-delete",
                    "whatsapp-send",
                })
                {
                    opts.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter(string.Empty));
                }
                opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // EnsureCreated must run after the host is built — calling it inside
        // ConfigureServices triggers EF Core 9's "providers SqlServer + InMemory
        // both registered" guard before the provider swap is finalized.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Database.EnsureCreated();

        return host;
    }

    public async Task<(string Token, Guid AdminId)> SeedAdminAndLoginAsync(
        string username = "admin", string password = "admin-password")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<OrderDeck.LicenseServer.Services.Auth.PasswordHasher>();

        var existing = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        Guid id;
        if (existing is not null)
        {
            id = existing.Id;
        }
        else
        {
            id = Guid.NewGuid();
            db.AdminUsers.Add(new OrderDeck.LicenseServer.Domain.AdminUser
            {
                Id = id,
                Username = username,
                PasswordHash = hasher.Hash(password),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/auth/login", new
        {
            username, password
        });
        var body = await resp.Content.ReadFromJsonAsync<LoginBody>();
        return (body!.Token, id);
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
}
