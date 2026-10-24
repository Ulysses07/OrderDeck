using System.Net.Http.Json;
using System.Reflection;
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
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    // InMemory deposu bu fabrikanın kökünde durur, sürecin ortak önbelleğinde
    // değil. Kök verilmezse EF adlandırılmış depoyu statik ServiceProviderCache'in
    // tuttuğu iç servis sağlayıcısında saklar ve süreç bitene kadar bırakmaz:
    // fabrika dispose edilse de verisi kalır. Binlerce fabrika açan bu pakette
    // testhost'un 0,7 → 7 GB büyümesinin bir kaynağı buydu (CI runner'ı
    // 2026-10-22'de bellekten düştü). Yerel ölçüm (EF 10.0.10, 200 host ×
    // ~0,4 MB veri): köksüz 83 MB kalıyor, kökle 1 MB; iç sağlayıcı sayısı
    // artmıyor, iki kök aynı adı paylaşmıyor. Ad + kök ikisi de
    // örnek alanı: LicenseDbContext ile LicenseReadOnlyDbContext aynı depoyu
    // görmeye devam eder, başka bir fabrika görmez.
    private readonly InMemoryDatabaseRoot _dbRoot = new();

    private readonly string _backupRoot = Path.Combine(Path.GetTempPath(), $"orderdeck-backup-{Guid.NewGuid():N}");

    public TestEmailSender Email { get; } = new();

    public RecordingNotificationSender Push { get; } = new();

    public RecordingSmsSender Sms { get; } = new();

    public FakeTenantSmsSender TenantSms { get; } = new();

    public FakeBroadcastMediaStorage BroadcastMedia { get; } = new();

    public string BackupRoot => _backupRoot;

    /// <summary>Override in derived test fixture to inject extra in-memory config
    /// keys (e.g. tighter rate limits, quota caps). Default: no overrides.</summary>
    protected virtual IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>();

    /// <summary>
    /// Doldurulursa, bir politika kararı verilirken istek bağlamıyla çağrılır.
    /// Limitler testte kapalı olduğu için sayıları değil, kararın hangi bilgiyle
    /// verildiğini gözlemlemeye yarar — özellikle <c>ctx.User</c>'ın dolu olup
    /// olmadığını. Kullanıcıya göre bölünen politikalar (backup-upload,
    /// backup-delete, whatsapp-send) rate limiter middleware'i kimlik
    /// doğrulamadan önce çalışırsa sessizce yanlış anahtara düşer: hata vermez,
    /// yalnız herkesi tek kovaya toplar. Bunu ancak buradan görebiliriz.
    /// </summary>
    protected virtual Action<string, HttpContext>? OnRateLimitPartition => null;

    /// <summary>Override in derived test fixture to add DbContext-level options
    /// (e.g. a fault-injecting <c>SaveChanges</c> interceptor). Runs on the same
    /// options builder as the InMemory provider swap, so the shared DB name is
    /// preserved. Default: no extra options.</summary>
    protected virtual void ConfigureDbContextOptions(DbContextOptionsBuilder opt) { }

    /// <summary>
    /// Testlerin koştuğu veritabanı sağlayıcısını seçer. Varsayılan InMemory:
    /// hızlı ve Docker istemiyor, suite'in ezici çoğunluğu için doğru tercih.
    ///
    /// Gerçek eşzamanlılık semantiği (aynı satıra iki eşzamanlı yazma) ve
    /// <c>ExecuteUpdate/Delete</c> InMemory'de YOK — bunlara dayanan bir
    /// düzeltme InMemory'de test edilirse CI yeşil yanar ama hiçbir şey
    /// kanıtlamaz. O testler <see cref="RelationalApiFactory"/> ile gerçek
    /// SQL Server container'ına karşı koşar.
    /// </summary>
    protected virtual void ConfigureDatabase(DbContextOptionsBuilder opt)
        => opt.UseInMemoryDatabase(_dbName, _dbRoot);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Banka HMAC anahtarı (BankHasher, 32+ bayt): her fabrikaya üretilmiş, repo public — literal anahtar yazılmaz.
        builder.UseSetting("OrderDeck:Bank:HashKey", $"test-{Guid.NewGuid():N}{Guid.NewGuid():N}");

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
                ConfigureDatabase(opt);
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
            services.AddDbContext<LicenseReadOnlyDbContext>(ConfigureDatabase);

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

            // Kiracı SMS göndericisi override — kampanya gönderimi kiracı
            // kimlikleriyle gider (§1.2); FakeTenantSmsSender kimlikleri de kaydeder.
            services.RemoveAll<OrderDeck.LicenseServer.Services.Sms.ITenantSmsSender>();
            services.AddSingleton<OrderDeck.LicenseServer.Services.Sms.ITenantSmsSender>(TenantSms);

            // Broadcast media storage override — FakeBroadcastMediaStorage UploadCalls'ı kaydeder.
            services.RemoveAll<OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage>();
            services.AddSingleton<OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage>(BroadcastMedia);

            // Hangfire — production SQL Server yerine InMemory storage (test isolation)
            services.AddHangfire(cfg => cfg
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseMemoryStorage()
                // Hangfire'ın günlük sağlayıcısı SÜREÇ GENELİ statik
                // (LogProvider.CurrentLogProvider), oysa her test sınıfı
                // IClassFixture<ApiFactory> ile KENDİ host'unu kuruyor ve xUnit
                // sınıfları paralel koşturuyor. AddHangfire, bu geri çağrıdan
                // hemen ÖNCE statiğe o host'un ILoggerFactory'sine bağlı bir
                // sağlayıcı yazıyor; host kapanınca statikte kapatılmış bir
                // fabrika kalıyor ve hâlâ koşan başka bir sınıf Hangfire'a
                // dokunduğunda ObjectDisposedException alıyor — CI'da rastgele
                // testler düşüyordu, hep farklı testler, ürün kodu suçsuz.
                //
                // NullLoggerFactory.Instance süreç ömürlü ve Dispose'u no-op:
                // hiçbir host'a bağlı olmadığı için bayatlayamaz. Bedeli,
                // Hangfire'ın kendi iç günlüklerinin testlerde susması — bunlara
                // bakmak gerekirse buraya geçici olarak gerçek bir fabrika ver.
                .UseLogProvider(new Hangfire.AspNetCore.AspNetCoreLogProvider(
                    Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)));

            // Disable rate limiting in tests — remove all IConfigureOptions<RateLimiterOptions>
            // registrations (added by AddRateLimiter in Program.cs) and register a fresh
            // unlimited configuration so auth tests don't get 429.
            //
            // Politika adları ELLE yazılmıyor: [EnableRateLimiting("x")] taşıyan
            // her uç taranıp adı buradan toplanıyor. Elle tutulan bir liste
            // Program.cs'ten sessizce ayrılıyordu ve bedeli ağırdı — karşılığı
            // olmayan bir politika 429 değil 500 ("no such policy exists")
            // üretiyor, yani o ucun BÜTÜN testleri, üstelik ilgisiz bir hatayla
            // düşüyor. Kaynağı kullanımın kendisi yapınca liste güncel kalmak
            // zorunda değil, zaten hep güncel.
            services.RemoveAll<Microsoft.Extensions.Options.IConfigureOptions<RateLimiterOptions>>();
            services.RemoveAll<Microsoft.Extensions.Options.IPostConfigureOptions<RateLimiterOptions>>();
            services.AddRateLimiter(opts =>
            {
                opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                foreach (var policy in DiscoverRateLimitPolicyNames())
                {
                    var name = policy;
                    opts.AddPolicy(name, ctx =>
                    {
                        OnRateLimitPartition?.Invoke(name, ctx);
                        return RateLimitPartition.GetNoLimiter(string.Empty);
                    });
                }
                opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
            });
        });
    }

    /// <summary>
    /// Sunucu derlemesindeki her <c>[EnableRateLimiting("...")]</c> kullanımının
    /// politika adını toplar. Testlerde limitleri kapatabilmek için gereken tek
    /// bilgi bu: ada karşılık bir politika kayıtlı olmalı, ne yaptığı önemli
    /// değil. Hiç kullanılmayan bir politikanın burada olmaması da sorun değil.
    /// </summary>
    private static IEnumerable<string> DiscoverRateLimitPolicyNames() =>
        typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
                .Concat(t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .SelectMany(m => m.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true))))
            .Select(a => a.PolicyName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        TrackRateLimiters(host);

        // EnsureCreated must run after the host is built — calling it inside
        // ConfigureServices triggers EF Core 9's "providers SqlServer + InMemory
        // both registered" guard before the provider swap is finalized.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Database.EnsureCreated();

        return host;
    }

    // Bu fabrikanın (ve WithWebHostBuilder ile türettiklerinin) host'larındaki
    // hız sınırlayıcılar; host kapandıktan sonra DisposeAsync'te bırakılır.
    private readonly List<IDisposable> _rateLimiters = new();

    /// <summary>
    /// ASP.NET Core'un <c>RateLimitingMiddleware</c>'i iki
    /// <see cref="PartitionedRateLimiter{TResource}"/> tutar (uç başına ve
    /// global) ve onları hiç dispose etmez. İkisi de 100 ms'lik bir zamanlayıcı
    /// çalıştırır; statik <c>TimerQueue</c> zamanlayıcıyı, zamanlayıcı
    /// sınırlayıcıyı, sınırlayıcı ara katmanı, ara katman da <c>_next</c>
    /// zinciriyle tüm boru hattını ve host'un servis sağlayıcısını kök tutar.
    /// Prod'da zararsız (süreç başına bir host), ama testte her fabrika host'u
    /// kapandıktan sonra da bellekte kalıyordu: 40 fabrikalık yerel prob, 40'ı
    /// da canlı, fabrika başına ~8,5 MB (2026-10-23). Tam pakette binlerce
    /// fabrika × 8,5 MB = CI runner'ının bellekten düşmesi.
    ///
    /// Ara katman örneği dışarıya açık değil; boru hattının delege zinciri
    /// (TestServer → uygulama → <c>_next</c> ...) izlenerek bulunur. Alan adına
    /// bağlı değil: ara katmanın <see cref="PartitionedRateLimiter{TResource}"/>
    /// türündeki her alanı alınır. Bulunamazsa testler yine koşar, yalnız sızıntı
    /// geri gelir — onu <c>ApiFactoryReleaseTests</c> yakalar.
    /// </summary>
    private void TrackRateLimiters(IHost host)
    {
        if (host.Services.GetService<IServer>() is not { } server) return;

        foreach (var middleware in FindInPipeline(server, "Microsoft.AspNetCore.RateLimiting.RateLimitingMiddleware"))
        {
            for (var type = middleware.GetType(); type is not null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.GetValue(middleware) is PartitionedRateLimiter<HttpContext> limiter)
                        lock (_rateLimiters) _rateLimiters.Add(limiter);
                }
            }
        }
    }

    /// <summary>
    /// Sunucudan başlayıp boru hattının delege zincirini gezer ve adı verilen
    /// türdeki nesneleri döner. İzlenen kenarlar: delege → hedefi; her nesneden
    /// delege ya da <c>IHttpApplication&lt;&gt;</c> değerli alanlar; derleyicinin
    /// ürettiği kapanışlardan ise her alan (ek parametreli <c>Invoke</c>'u olan
    /// ara katmanlar, örneği bir kapanışta tutar ve zincir o örneğin
    /// <c>_next</c>'inden sürer). Servis sağlayıcının içine dağılmaz; birkaç
    /// yüz düğümde biter.
    /// </summary>
    private static List<object> FindInPipeline(object start, string typeFullName)
    {
        var found = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { start };
        var queue = new Queue<object>();
        queue.Enqueue(start);

        while (queue.Count > 0 && seen.Count < 5_000)
        {
            var current = queue.Dequeue();
            if (current is Delegate d)
            {
                foreach (var inner in d.GetInvocationList()) Visit(inner.Target, expand: true);
                continue;
            }

            var closure = current.GetType().Name.StartsWith('<');
            for (var type = current.GetType(); type is not null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsValueType || field.FieldType == typeof(string)) continue;
                    object? value;
                    try { value = field.GetValue(current); } catch { continue; }
                    var link = value is Delegate || (value is not null && IsHttpApplication(value.GetType()));
                    Visit(value, expand: link || closure);
                }
            }
        }

        return found;

        void Visit(object? value, bool expand)
        {
            if (value is null) return;
            if (value.GetType().FullName == typeFullName)
            {
                if (seen.Add(value)) found.Add(value);
                return;
            }
            if (expand && seen.Add(value)) queue.Enqueue(value);
        }

        static bool IsHttpApplication(Type type) =>
            type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHttpApplication<>));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // Host'lar kapandıktan SONRA: kapanırken gelen bir istek dispose edilmiş
        // sınırlayıcıya çarpmasın. WithWebHostBuilder ile türetilen fabrikalar
        // base.DisposeAsync içinde kapanır ve host'ları da bu listeye
        // CreateHost üzerinden (bu örnek) girmiştir.
        IDisposable[] limiters;
        lock (_rateLimiters)
        {
            limiters = _rateLimiters.ToArray();
            _rateLimiters.Clear();
        }
        foreach (var limiter in limiters) limiter.Dispose();
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
