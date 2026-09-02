using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Hangfire;
using Hangfire.SqlServer;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Services.Configuration;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderDeck.LicenseServer.Services.Observability;

namespace OrderDeck.LicenseServer;

public class Program
{
    public static async Task Main(string[] args)
    {
        // CLI tool dispatch — short-circuit before the web host is built.
        // Lets us invoke maintenance commands inside the running container
        // (e.g. `docker compose exec license-server dotnet OrderDeck.LicenseServer.dll
        // restore-verify <blob>`) without spinning up Kestrel + SQL + Hangfire.
        if (args.Length > 0 && args[0] == "restore-verify")
        {
            var exit = await OrderDeck.LicenseServer.Tools.RestoreVerify.RunAsync(args);
            Environment.Exit(exit);
            return;
        }

        var builder = WebApplication.CreateBuilder(args);

        // Options binding
        //
        // JWT ayrı duruyor çünkü tek doğrulanan o: yanlış imzalama anahtarıyla
        // açılan sunucu hiçbir belirti vermez, /healthz anonim olduğu için
        // yeşil yanar ve hata ancak sömürüldüğünde görünür. ValidateOnStart
        // konteyneri hiç ayağa kaldırmıyor — kurallar için JwtOptionsValidator.
        builder.Services.AddOptions<JwtOptions>()
            .Bind(builder.Configuration.GetSection("Jwt"))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
        builder.Services.Configure<OrderDeck.LicenseServer.Services.Sms.NetgsmOptions>(
            builder.Configuration.GetSection("Netgsm"));
        builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
        builder.Services.Configure<OrderDeck.LicenseServer.Services.Audit.AuditRetentionOptions>(
            builder.Configuration.GetSection("Audit:Retention"));
        // 90 gün — gizlilik politikasındaki "Güvenlik kayıtları: 90 gün" ile
        // bağlı. Bu değeri değiştiren web/app/(tr)/gizlilik-politikasi ve
        // (en)/privacy-policy metinlerini de değiştirmek zorunda.
        builder.Services.Configure<OrderDeck.LicenseServer.Services.Audit.SecurityDataRetentionOptions>(
            builder.Configuration.GetSection("Audit:SecurityRetention"));

        // DataProtection anahtar halkasının yeri — AÇIKÇA sabitleniyor.
        //
        // Bu çağrı olmadan ASP.NET Core anahtarları `$HOME/.aspnet/DataProtection-Keys`
        // altında tutar. Yani anahtarların yeri, process'in HANGİ KULLANICI olarak
        // koştuğuna bağlıdır. Konteyner root iken bu `/root/...` oluyordu ve compose
        // oraya mount ediyordu; `USER` eklendiği an `$HOME` değişir, uygulama BOŞ bir
        // dizin görür, kendine yeni bir anahtar üretir ve AÇILIŞ BAŞARILI OLUR.
        //
        // Sessiz kaybın bedeli: `WhatsAppAccounts` satırlarındaki erişim token'ı ve
        // iki adımlı doğrulama PIN'i IDataProtector ile şifreli. Anahtar değişince
        // çözülemezler ve `WhatsAppAccountService.TryUnprotect` CryptographicException'ı
        // yutup `null` döndüğü için hiçbir yerde hata görünmez — entegrasyon susar.
        // Yayıncının Embedded Signup'ı baştan yapması gerekir.
        //
        // SetApplicationName ÇAĞIRMA. Uygulama ayracı varsayılan olarak
        // ContentRootPath'tir (konteynerde `/app`) ve amaç zincirinin parçasıdır;
        // değiştirmek yukarıdaki payload'ları aynı şekilde çözülemez hâle getirir.
        //
        // Boş bırakılırsa (dev/test) varsayılan davranış korunur.
        var keysPath = builder.Configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            Directory.CreateDirectory(keysPath);
            EnsureKeyRingUsable(keysPath);
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        // DbContext (primary — used for all writes + reads when no replica configured).
        builder.Services.AddDbContext<LicenseDbContext>(opt =>
            opt.UseSqlServer(builder.Configuration.GetConnectionString("LicenseDb")));

        // Read-only DbContext for HA-aware deployments. Routes to a SQL Server
        // AlwaysOn read replica when ConnectionStrings:LicenseDbReadOnly is set,
        // else falls back to the primary connection string. Read paths
        // (admin list/detail, customer export) can opt in by injecting
        // LicenseReadOnlyDbContext instead of LicenseDbContext.
        var readOnlyConn = builder.Configuration.GetConnectionString("LicenseDbReadOnly")
                           ?? builder.Configuration.GetConnectionString("LicenseDb");
        builder.Services.AddDbContext<LicenseReadOnlyDbContext>(opt =>
            opt.UseSqlServer(readOnlyConn));

        // Services
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddScoped<AdminLoginService>();
        builder.Services.AddScoped<ShopperRefreshTokenService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.ShopperCode.IShopperCodeValidator,
            OrderDeck.LicenseServer.Services.ShopperCode.ShopperCodeValidator>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.ShopperPayments.IShopperPaymentRateLimiter,
            OrderDeck.LicenseServer.Services.ShopperPayments.ShopperPaymentRateLimiter>();
        builder.Services.AddSingleton<OrderDeck.PdfParsing.IPdfDekontParser,
            OrderDeck.PdfParsing.PdfDekontParser>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.ShopperPayments.ShopperPaymentSubmissionService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Shoppers.ShopperPurgeService>();
        builder.Services.AddSingleton<JwtTokenService>();
        builder.Services.AddScoped<RefreshTokenService>();
        builder.Services.AddScoped<EmailConfirmationService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Licensing.LicenseIssuer>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Licensing.LicenseValidator>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Licensing.ActivationManager>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Audit.AuditRetentionJobs>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Audit.SecurityDataRetentionJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Backup.BackupRestoreDrillJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Backup.BackupOrphanCleanupJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.BroadcastPosts.BroadcastPostCleanupJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Catalog.ProductPhotoOrphanCleanupJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Catalog.BarcodeAllocator>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Audit.IAuditService,
                                    OrderDeck.LicenseServer.Services.Audit.AuditService>();

        // Sağlayıcı seçimleri (email / SMS / WhatsApp / push / medya) tek kapıdan:
        // ProviderName.Resolve tanınmayan adı açılışta patlatır. Eskiden bunların
        // bir kısmı sessizce yedeğe düşüyordu ve SMS/WhatsApp'ın yedeği hiçbir şey
        // göndermeyen "log" sağlayıcısıydı — bkz. ProviderName sınıf yorumu.
        //
        // Varsayılanı sahte olanlar ResolveLive'dan geçer: üretimde o varsayılana
        // düşmek yasak. E-posta listede yok çünkü onun varsayılanı ("smtp") zaten
        // gerçek sağlayıcı — yanlış yapılandırılırsa gönderim hata verir, sessiz
        // kalmaz.
        var isProduction = builder.Environment.IsProduction();

        var emailProvider = ProviderName.Resolve(
            builder.Configuration, "Email:Provider", "smtp", "smtp", "disk");
        if (emailProvider == "disk")
            builder.Services.AddSingleton<IEmailSender, DiskEmailSender>();
        else
            builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

        // SMS sender selection (email pattern'iyle aynı). Dev/test → log,
        // prod → Netgsm. Netgsm HttpClient ile typed-client olarak bağlanır.
        var smsProvider = ProviderName.ResolveLive(
            builder.Configuration, isProduction, "Sms:Provider", "log", "log", "netgsm");
        if (smsProvider == "netgsm")
        {
            // Timeout: Netgsm asılı kalırsa forgot-password isteğini default
            // 100sn boyunca bloklamasın (SMS inline await ediliyor).
            var smsTimeout = builder.Configuration.GetValue("Netgsm:TimeoutSeconds", 10);
            builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.Sms.ISmsSender,
                    OrderDeck.LicenseServer.Services.Sms.NetgsmSmsSender>(
                    c => c.Timeout = TimeSpan.FromSeconds(smsTimeout <= 0 ? 10 : smsTimeout));
        }
        else
            builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Sms.ISmsSender,
                OrderDeck.LicenseServer.Services.Sms.LogSmsSender>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.LicenseSmsBalanceService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.SmsCampaignSendJob>();
        builder.Services.AddScoped<PasswordResetCodeService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Auth.PasswordResetCodeCleanupJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.WaSendAttemptCleanupJob>();

        builder.Services.AddSingleton<UnsubscribeTokenSigner>();
        builder.Services.AddScoped<EmailSendCoordinator>();
        builder.Services.AddScoped<ReminderJobs>();
        builder.Services.AddScoped<PasswordResetService>();
        builder.Services.AddScoped<AdminActionEmailService>();
        builder.Services.AddScoped<IntakeFormService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Stock.StockLedgerWriter>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Stock.StockBalanceService>();
        builder.Services.AddSingleton<WhatsAppLinkBuilder>();
        // Tekil: bağımlılıkları (IHttpClientFactory/IMemoryCache/IConfiguration) tekil.
        // Cache'in paylaşılması önemli — istemcinin canlı doğrulaması ile gönderimdeki
        // sunucu doğrulaması aynı 1 saatlik girdiyi kullansın.
        builder.Services.AddSingleton<IYouTubeChannelResolver, YouTubeChannelResolver>();

        // WhatsApp Cloud API — SMS/Push pattern'iyle aynı. Dev/test → log
        // (gerçek çağrı yok), prod → cloud (Graph API, typed HttpClient).
        // Tenant-aware: gönderim kimlikleri çağrı başına WhatsAppSendContext'ten.
        builder.Services.Configure<OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppOptions>(
            builder.Configuration.GetSection("OrderDeck:WhatsApp"));
        var waProvider = ProviderName.ResolveLive(
            builder.Configuration, isProduction, "OrderDeck:WhatsApp:Provider", "log", "log", "cloud");
        if (waProvider == "cloud")
        {
            var waTimeout = builder.Configuration.GetValue("OrderDeck:WhatsApp:TimeoutSeconds", 15);
            builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.WhatsApp.IWhatsAppSender,
                    OrderDeck.LicenseServer.Services.WhatsApp.CloudApiWhatsAppSender>(
                    c => c.Timeout = TimeSpan.FromSeconds(waTimeout <= 0 ? 15 : waTimeout));
        }
        else
            builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.WhatsApp.IWhatsAppSender,
                OrderDeck.LicenseServer.Services.WhatsApp.LogWhatsAppSender>();

        // Embedded Signup Graph istemcisi — gönderenden AYRI kayıtlı: sağlayıcı
        // "log" olsa da (dev) onboarding uçları derlenebilir/test edilebilir olmalı.
        var waOnboardTimeout = builder.Configuration.GetValue("OrderDeck:WhatsApp:TimeoutSeconds", 15);
        builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.WhatsApp.IWhatsAppOnboardingClient,
                OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppOnboardingClient>(
                c => c.Timeout = TimeSpan.FromSeconds(waOnboardTimeout <= 0 ? 15 : waOnboardTimeout));

        // Şablon kataloğu da sağlayıcıdan bağımsız: onboarding istemcisiyle aynı
        // gerekçe. Bağlı hesabı olmayan lisans zaten uçta 503 alıyor, yani "log"
        // modunda Graph'a hiç çıkılmıyor.
        builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.WhatsApp.IWhatsAppTemplateCatalog,
                OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppTemplateCatalog>(
                c => c.Timeout = TimeSpan.FromSeconds(waOnboardTimeout <= 0 ? 15 : waOnboardTimeout));

        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppAccountService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppMessagingService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppInboundJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.LabelRuleApplier>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.WaDekontExtractor>();

        // Medya indirici sağlayıcıdan bağımsız kayıtlı: log modunda da inbound
        // job onu ister, gerçek token olmadığı için Graph çağrısı uyarı loglayıp
        // boş döner (mesaj yine kaydedilir). Timeout sender'la aynı.
        var waMediaTimeout = builder.Configuration.GetValue("OrderDeck:WhatsApp:TimeoutSeconds", 15);
        builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppMediaDownloader>(
            c => c.Timeout = TimeSpan.FromSeconds(waMediaTimeout <= 0 ? 15 : waMediaTimeout));

        // Facebook OAuth (masaüstü sohbet/moderasyon app'i — WhatsApp app'inden
        // AYRI). Yapılandırma yoksa uçlar 503 döner, o yüzden istemci koşulsuz
        // kayıtlı: "log modu" gibi bir alternatif yol yok, takas ya sunucuda
        // yapılır ya hiç yapılmaz.
        builder.Services.Configure<OrderDeck.LicenseServer.Services.Facebook.FacebookOptions>(
            builder.Configuration.GetSection("OrderDeck:Facebook"));
        var fbTimeout = builder.Configuration.GetValue("OrderDeck:Facebook:TimeoutSeconds", 15);
        builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.Facebook.IFacebookOAuthExchanger,
                OrderDeck.LicenseServer.Services.Facebook.FacebookOAuthExchanger>(
                c => c.Timeout = TimeSpan.FromSeconds(fbTimeout <= 0 ? 15 : fbTimeout));

        builder.Services.AddSingleton<BackupStorageService>();
        // Singleton ŞART: süreç başına tek sayaç olmasının bütün amacı bu.
        builder.Services.AddSingleton<BackupUploadThrottle>();
        builder.Services.AddScoped<BackupRetentionService>();
        builder.Services.AddScoped<BackupViewerService>();

        // Push notifications. Provider: "stub" (default) veya "fcm".
        // PR #51 (2026-05-14) — stub log-only fan-out.
        // PR Push Faz 2 (2026-05-15) — gerçek FCM (FirebaseAdmin SDK).
        var pushProvider = ProviderName.ResolveLive(
            builder.Configuration, isProduction, "OrderDeck:Push:Provider", "stub", "stub", "fcm");
        if (pushProvider == "stub")
        {
            builder.Services.AddScoped<
                OrderDeck.LicenseServer.Services.Push.INotificationSender,
                OrderDeck.LicenseServer.Services.Push.StubNotificationSender>();
        }
        else
        {
            // Bind options + initialize FirebaseApp singleton fail-fast.
            var fcmOptions = new OrderDeck.LicenseServer.Services.Push.FcmOptions();
            builder.Configuration.GetSection("OrderDeck:Push:Fcm").Bind(fcmOptions);
            builder.Services.AddSingleton(fcmOptions);

            var messaging = OrderDeck.LicenseServer.Services.Push
                .FcmNotificationSender.InitializeMessaging(fcmOptions);
            builder.Services.AddSingleton(messaging);

            builder.Services.AddScoped<
                OrderDeck.LicenseServer.Services.Push.INotificationSender,
                OrderDeck.LicenseServer.Services.Push.FcmNotificationSender>();
        }

        // Broadcast media storage — provider seçimi (stub | r2)
        // Provider: appsettings.json "OrderDeck:BroadcastMedia:Provider" = "stub" | "r2"
        var bmProvider = ProviderName.ResolveLive(
            builder.Configuration, isProduction, "OrderDeck:BroadcastMedia:Provider", "stub", "stub", "r2");
        if (bmProvider == "stub")
        {
            builder.Services.AddSingleton<
                OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage,
                OrderDeck.LicenseServer.Services.BroadcastPosts.StubBroadcastMediaStorage>();
        }
        else
        {
            var r2Opt = new OrderDeck.LicenseServer.Services.BroadcastPosts.R2Options();
            builder.Configuration.GetSection("R2").Bind(r2Opt);
            builder.Services.AddSingleton(r2Opt);
            builder.Services.AddSingleton<
                OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage,
                OrderDeck.LicenseServer.Services.BroadcastPosts.R2BroadcastMediaStorage>();
        }

        // Shopper payment storage — same provider selection as broadcast media (stub | r2)
        // R2Options already registered as singleton above when bmProvider == "r2".
        if (bmProvider == "stub")
        {
            builder.Services.AddSingleton<
                OrderDeck.LicenseServer.Services.ShopperPayments.IShopperPaymentStorage,
                OrderDeck.LicenseServer.Services.ShopperPayments.StubShopperPaymentStorage>();
        }
        else
        {
            builder.Services.AddSingleton<
                OrderDeck.LicenseServer.Services.ShopperPayments.IShopperPaymentStorage,
                OrderDeck.LicenseServer.Services.ShopperPayments.R2ShopperPaymentStorage>();
        }

        // WhatsApp medyası aynı bucket'ı paylaşır ("wa/" öneki) → aynı provider seçimi.
        if (bmProvider == "stub")
        {
            builder.Services.AddSingleton<
                OrderDeck.LicenseServer.Services.WhatsApp.IWhatsAppMediaStore,
                OrderDeck.LicenseServer.Services.WhatsApp.InMemoryWhatsAppMediaStore>();
        }
        else
        {
            builder.Services.AddSingleton<
                OrderDeck.LicenseServer.Services.WhatsApp.IWhatsAppMediaStore,
                OrderDeck.LicenseServer.Services.WhatsApp.R2WhatsAppMediaStore>();
        }

        // JWT auth — two schemes (use IOptions so tests can override Jwt:SecretKey via config)
        builder.Services.AddAuthentication()
            .AddJwtBearer("Bearer-Customer", _ => { })
            .AddJwtBearer("Bearer-Admin", _ => { })
            .AddJwtBearer("Bearer-Shopper", _ => { })
            .AddCookie("AdminCookie", o =>
            {
                o.LoginPath = "/admin/login";
                o.AccessDeniedPath = "/admin/login";
                o.LogoutPath = "/admin/logout";
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                // Always, SameAsRequest DEĞİL. SameAsRequest çerezin Secure
                // bayrağını Request.Scheme'e bağlar; scheme ise ters vekil
                // arkasında yalnız UseForwardedHeaders varsa doğrudur. Yani
                // oturum çerezinin korunması, pipeline'ın en başındaki başka
                // bir middleware'in varlığına bağlı kalıyordu — o middleware
                // bugüne kadar yoktu ve çerez üretimden Secure'suz çıkıyordu.
                // Always bu bağı tamamen koparır: kimse Secure'u kazara
                // düşüremez.
                o.Cookie.SecurePolicy = AdminCookieSecurePolicy(builder.Environment);
                o.Cookie.Name = "OrderDeckAdmin";
            });

        // CSRF çerezi de aynı kurala tabi. Varsayılanı CookieSecurePolicy.None,
        // yani hiç ayarlanmazsa Secure bayrağı HİÇ çıkmaz — üretimde bugün
        // durum buydu.
        builder.Services.AddAntiforgery(o =>
        {
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = AdminCookieSecurePolicy(builder.Environment);
        });

        builder.Services.AddOptions<JwtBearerOptions>("Bearer-Customer")
            .Configure<IOptions<JwtOptions>>((o, jwtOpts) =>
            {
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.Value.SecretKey));
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwtOpts.Value.Issuer,
                    ValidateAudience = true, ValidAudience = JwtOptions.CustomerAudience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        builder.Services.AddOptions<JwtBearerOptions>("Bearer-Admin")
            .Configure<IOptions<JwtOptions>>((o, jwtOpts) =>
            {
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.Value.SecretKey));
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwtOpts.Value.Issuer,
                    ValidateAudience = true, ValidAudience = JwtOptions.AdminAudience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        builder.Services.AddOptions<JwtBearerOptions>("Bearer-Shopper")
            .Configure<IOptions<JwtOptions>>((o, jwtOpts) =>
            {
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.Value.SecretKey));
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwtOpts.Value.Issuer,
                    ValidateAudience = true, ValidAudience = JwtOptions.ShopperAudience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        builder.Services.AddAuthorization(opt =>
        {
            opt.AddPolicy("AdminOnly", p => p
                .AddAuthenticationSchemes("AdminCookie")
                .RequireAuthenticatedUser());
        });

        // Rate limiting
        builder.Services.AddRateLimiter(opt =>
        {
            opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            opt.AddPolicy("auth-login", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1)
                    }));
            // Razor /admin/login. Kendi politikası var çünkü Razor Pages'te sayfa
            // TEK uç: aynı politika GET'i de sayardı ve giriş formunu birkaç kez
            // yenileyen operatör kendi giriş sayfasından 429 yerdi. Bu yüzden
            // yalnız POST bölümleniyor, GET limitsiz geçiyor.
            //
            // Asıl koruma bu değil, hesap kilidi (AdminLoginService): IP başına
            // sınır IP döndüren saldırganı durdurmuyor. Buradaki yalnız sel kapağı.
            opt.AddPolicy("admin-login", ctx =>
                HttpMethods.IsPost(ctx.Request.Method)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 5,
                            Window = TimeSpan.FromMinutes(1)
                        })
                    : RateLimitPartition.GetNoLimiter<string>("admin-login-get"));
            opt.AddPolicy("auth-register", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(1)
                    }));
            opt.AddPolicy("auth-refresh", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1)
                    }));
            // Shopper parola akışları (unut / sıfırla / değiştir). auth-login
            // kovasını PAYLAŞMIYOR: parolasını unutan kullanıcı önce birkaç kez
            // yanlış girip sonra "parolamı unuttum"a basıyor — aynı kovada
            // olsalardı tam da yardıma ihtiyacı olan kişi 429 yerdi.
            // Asıl korumalar serviste (OTP deneme sayacı, SMS maliyet tavanı);
            // buradaki yalnız sel kapağı, o yüzden cömert.
            opt.AddPolicy("shopper-password", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1)
                    }));
            // Shopper "SMS gelmedi" tırmandırma ucu. Anonim ve her çağrı bağlı
            // her yayıncıya bir destek talebi satırı + bir push demek: tek bir
            // telefon numarası bilinerek döngüye sokulursa yayıncının telefonu
            // bildirime boğulur, tablo şişer. SMS akışının aksine burada servis
            // içinde bir tavan yok, tek koruma bu. Limit dar tutuldu; gerçek
            // kullanımda kişi başı günde bir-iki denemeden fazlası anlamsız.
            opt.AddPolicy("shopper-support-escalate", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromHours(1)
                    }));
            opt.AddPolicy("intake-form-submit", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = int.TryParse(
                            Environment.GetEnvironmentVariable("ORDERDECK_INTAKE_RATELIMIT_PER_HOUR")
                            ?? Environment.GetEnvironmentVariable("LIVEDECK_INTAKE_RATELIMIT_PER_HOUR"),
                            out var n) ? n : 5,
                        Window = TimeSpan.FromHours(1)
                    }));
            // Public YouTube handle doğrulama (intake formu) — IP başına dakikada 30.
            opt.AddPolicy("youtube-verify", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1)
                    }));
            opt.AddPolicy("backup-upload", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                                 ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 6,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));
            opt.AddPolicy("backup-delete", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "anon",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));
            // WhatsApp gönderimi — her istek Meta'da faturalanabilir bir mesaj
            // demek, yani buradaki kaçak doğrudan para kaybı. Bölüm anahtarı IP
            // DEĞİL çağıran müşteri: yayıncılar NAT arkasından aynı IP'yi
            // paylaşabilir ve birinin döngüsü diğerini kilitlememeli.
            // Limit bilerek cömert — operatör yayın sonrası onlarca müşteriye
            // elle hatırlatma gönderiyor; amaç normal kullanımı kesmek değil,
            // kontrolden çıkmış bir döngüyü sınırlamak.
            opt.AddPolicy("whatsapp-send", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                                  ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));
            // Facebook code takası — bu uç bizim App Secret'ımızı kullanıyor,
            // yani kötüye kullanımı doğrudan Meta app'imizin itibarına yazılır.
            // Bölüm anahtarı çağıran müşteri (NAT arkasındaki yayıncılar aynı
            // IP'yi paylaşabilir). Normal kullanım günde bir-iki bağlanma;
            // limit elle deneme-yanılmaya yer bırakacak kadar geniş.
            opt.AddPolicy("facebook-oauth", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                                  ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));
            // Panel CSP ihlal bildirimi — anonim telemetri. Asıl sınırlama
            // İSTEMCİDE: panel sayfa yüklemesi başına en çok birkaç benzersiz
            // ihlal yolluyor. Buradaki limit ona güvenmemek için, çünkü uç
            // anonim. Bilerek düşük tutuldu: aynı ihlali yüz kez görmek sıfır
            // ek bilgi veriyor, ilk birkaçı zaten her şeyi söylüyor.
            //
            // Not: adlandırılmış politika global limiter'ın YERİNE geçmiyor,
            // ona EK olarak işliyor — yani bir ihlal fırtınası kullanıcının
            // kendi 100/dk bütçesini de yerdi. İstemci tarafındaki tavan bu
            // yüzden isteğe bağlı bir iyileştirme değil, gereklilik.
            opt.AddPolicy("csp-report", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));
            opt.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        // CORS — closed by default. Desktop app + Razor admin + public intake form are
        // all same-origin / server-to-server, so no browser cross-origin client exists today.
        // To allow a partner domain later, set Cors:AllowedOrigins (comma-separated) in env.
        var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        builder.Services.AddCors(opt =>
            opt.AddDefaultPolicy(p =>
            {
                if (corsOrigins.Length == 0)
                {
                    // No origins configured → deny all cross-origin (don't call WithOrigins("")).
                    p.SetIsOriginAllowed(_ => false).DisallowCredentials();
                }
                else
                {
                    p.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
                }
            }));

        builder.Services.AddHangfire(cfg => cfg
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(builder.Configuration.GetConnectionString("LicenseDb"),
                new SqlServerStorageOptions
                {
                    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                    QueuePollInterval = TimeSpan.Zero,
                    UseRecommendedIsolationLevel = true,
                    DisableGlobalLocks = true
                }));
        // Background server üretimde job'ları işler. Testte (ApiFactory MemoryStorage)
        // server'ı kaldırıyoruz ki enqueue edilen job'lar otomatik koşmasın —
        // testler job'ı doğrudan RunAsync ile deterministik çalıştırır.
        if (!builder.Environment.IsEnvironment("Testing"))
            builder.Services.AddHangfireServer();

        // Saha dışı yedek replikasyonu artık uygulamada DEĞİL: VPS'te gecelik
        // cron (`deploy/scripts/backup-blobs-to-r2.sh`) yapıyor. Buradaki
        // `Backup:S3` yolu silindi — açılabilir görünüyordu ama R2'de
        // çalışmazdı (AuthenticationRegion + DisablePayloadSigning eksikti) ve
        // `Task.Run` ile ateşle-unut olduğu için hangi blob'un kopyalandığı
        // hiç kayda geçmiyordu; her deploy'un yeniden başlattığı süreçte
        // uçuştaki kopyalama sessizce kayboluyordu. Ayrıntı: HA-PLAYBOOK G6.

        // OpenTelemetry: tracing + metrics. Custom OrderDeckMetrics meter is
        // registered as a singleton so domain code can inject it. AspNetCore +
        // Http + Runtime instrumentations cover request latency, GC, threadpool,
        // outbound HTTP for free. Prometheus exporter exposes /metrics; OTLP
        // exporter pushes to whatever endpoint OTEL_EXPORTER_OTLP_ENDPOINT
        // points at (env var; absent → no push, /metrics still works).
        builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Observability.OrderDeckMetrics>();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: "orderdeck-license-server",
                serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(opt =>
                {
                    // Don't trace the noisy probes — they 200 in <1ms and would
                    // dominate the trace volume.
                    opt.Filter = ctx =>
                        !ctx.Request.Path.StartsWithSegments("/healthz") &&
                        !ctx.Request.Path.StartsWithSegments("/ready") &&
                        !ctx.Request.Path.StartsWithSegments("/metrics");
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporterIfConfigured(builder.Configuration))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(OrderDeck.LicenseServer.Services.Observability.OrderDeckMetrics.MeterName)
                .AddPrometheusExporter()
                .AddOtlpExporterIfConfigured(builder.Configuration));

        // Health checks: /healthz (liveness, no DB) and /ready (readiness with DB ping).
        // Caddy / monitoring polls /healthz every few seconds; deeper checks on /ready.
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<LicenseDbContext>(
                name: "licensedb",
                tags: new[] { "ready", "db" });

        builder.Services.AddControllers(opt =>
            opt.Filters.Add<OrderDeck.LicenseServer.Services.Auth.StockStaffScopeFilter>());
        builder.Services.AddMemoryCache(); // YouTube handle doğrulama cache'i için
        builder.Services.AddHttpClient();  // YouTubeVerifyController için IHttpClientFactory
        builder.Services.AddRazorPages(opt =>
        {
            opt.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
            opt.Conventions.AllowAnonymousToPage("/Admin/Login");
            opt.Conventions.AllowAnonymousToPage("/Admin/Logout");
            opt.Conventions.AllowAnonymousToFolder("/Public");
            // Eski /r/{slug} linkleri çalışmaya devam etsin (yeni URL /musteri-kayit/{slug}).
            opt.Conventions.AddPageRoute("/Public/IntakeForm", "/r/{slug}");
        });
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        // Bootstrap: apply EF migrations on relational stores (prod SQL Server) or
        // EnsureCreated on in-memory test stores (UseInMemoryDatabase doesn't support
        // Migrate). Production must have __EFMigrationsHistory seeded — see
        // deploy/bootstrap-migration-history.sql for the one-time prod backfill.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            if (db.Database.IsRelational())
                db.Database.Migrate();
            else
                db.Database.EnsureCreated();
            await SeedAdminAsync(db, app.Configuration);
        }

        // Hangfire recurring jobs — production only (testte ApiFactory MemoryStorage kullanır, recurring tetiklenmesin)
        if (!app.Environment.IsEnvironment("Testing"))
        {
            using var scope = app.Services.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
            var cron = builder.Configuration["EmailReminder:DailyJobCron"] ?? "0 9 * * *";
            manager.AddOrUpdate<ReminderJobs>("renewal-14d", j => j.SendRenewal14dAsync(CancellationToken.None), cron);
            manager.AddOrUpdate<ReminderJobs>("renewal-7d",  j => j.SendRenewal7dAsync(CancellationToken.None), cron);
            manager.AddOrUpdate<ReminderJobs>("renewal-3d",  j => j.SendRenewal3dAsync(CancellationToken.None), cron);
            manager.AddOrUpdate<ReminderJobs>("renewal-0d",  j => j.SendRenewal0dAsync(CancellationToken.None), cron);
            manager.AddOrUpdate<ReminderJobs>("expired-1d",  j => j.SendExpired1dAsync(CancellationToken.None), cron);

            // Audit log retention — prune rows older than the configured window
            // once a day. Cron different from email reminders to spread DB load.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Audit.AuditRetentionJobs>(
                "audit-retention",
                j => j.PruneAsync(CancellationToken.None),
                "30 3 * * *");  // 03:30 UTC daily

            // Güvenlik kaydı anonimleştirme — 90 günden eski IP/User-Agent
            // alanlarını satırı silmeden boşaltır. Gizlilik politikasındaki
            // "Güvenlik kayıtları: 90 gün" taahhüdünü fiilen uygulayan iş bu.
            // audit-retention'dan SONRA çalışır: o zaten 24 aydan eski satırları
            // sildiği için burada işlenecek satır sayısı azalmış olur.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Audit.SecurityDataRetentionJob>(
                "security-data-retention",
                j => j.AnonymiseAsync(CancellationToken.None),
                "40 3 * * *");  // 03:40 UTC daily

            // Weekly backup-restore drill — proves an actual production blob
            // round-trips through decrypt + ZIP + SQLite integrity. Failures
            // email the Admin:AlertEmail address. See
            // OrderDeck.LicenseServer/Services/Backup/BackupRestoreDrillJob.cs
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Backup.BackupRestoreDrillJob>(
                "backup-restore-drill",
                j => j.RunAsync(CancellationToken.None),
                "30 4 * * MON");  // 04:30 UTC every Monday (~07:30 Türkiye)

            // Broadcast posts cleanup — soft-delete 30-day-expired non-pinned posts
            // and best-effort remove their R2 media. Pinned posts have ExpiresAt
            // sentinel of 9999-12-31 so they're filtered out by the job's query.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.BroadcastPosts.BroadcastPostCleanupJob>(
                "broadcast-posts-cleanup",
                j => j.RunAsync(CancellationToken.None),
                "0 3 * * *");  // 03:00 UTC daily (before audit-retention at 03:30)

            // Parola sıfırlama OTP satırları temizliği — kodlar 10dk'da expire
            // olur; eski satırlar tablo şişmesin diye günlük purge edilir.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Auth.PasswordResetCodeCleanupJob>(
                "otp-code-cleanup",
                j => j.PruneAsync(CancellationToken.None),
                "45 3 * * *");  // 03:45 UTC daily

            // WhatsApp gönderim rezervasyonları — idempotency penceresi dakikalarla
            // ölçülüyor, satırlar yalnız teşhis için saklanıyor. Süresi dolanlar
            // günlük temizlenmezse tablo sınırsız büyür.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.WhatsApp.WaSendAttemptCleanupJob>(
                "wa-send-attempt-cleanup",
                j => j.PruneAsync(CancellationToken.None),
                "0 4 * * *");  // 04:00 UTC daily

            // Ürün fotoğrafı mutabakatı — R2'de kalmış yetim nesneleri süpürür.
            // Ürün silme ucundaki inline silme yetmiyor: Attach edilmeden yüklenen
            // dosyalar DB'ye hiç yazılmıyor, lisans cascade'i o uçtan geçmiyor.
            // Yetim ancak kova listelenerek bulunabilir. 24 saatlik ödemsiz süre
            // henüz iliştirilmemiş yüklemeleri koruyor.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Catalog.ProductPhotoOrphanCleanupJob>(
                "product-photo-orphan-cleanup",
                j => j.RunAsync(CancellationToken.None),
                "15 4 * * *");  // 04:15 UTC daily

            // Yedek blob mutabakatı — depolama kökünde kalmış yetim dosyaları
            // süpürür. Silme yollarındaki sıra artık "önce satır, sonra dosya";
            // bedeli, satır gittikten sonra dosya silinemezse (ya da yükleme
            // ortasında süreç ölürse) geriye yetim dosya kalması. Onu ancak
            // diskle veritabanını karşılaştıran bu iş bulabilir. 24 saatlik
            // ödemsiz süre, sürmekte olan bir yüklemenin blob'unu silmesini
            // engelliyor. Geri yükleme provasından (04:30 MON) ÖNCE çalışır:
            // prova diskteki en yeni blob'u seçiyor, yetime takılmasın.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Backup.BackupOrphanCleanupJob>(
                "backup-orphan-cleanup",
                j => j.RunAsync(CancellationToken.None),
                "20 4 * * *");  // 04:20 UTC daily
        }

        // Ters vekil farkındalığı — pipeline'ın EN BAŞI, çünkü aşağıdaki her
        // şey Request.Scheme ve RemoteIpAddress'i doğru kabul ediyor.
        // Gerekçe ve ayarların tek tek anlamı: CreateForwardedHeadersOptions.
        app.UseForwardedHeaders(CreateForwardedHeadersOptions());

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        // Pin admin UI + API formatting to tr-TR. Razor pages render currency
        // via ToString("N2") which uses the ambient thread culture; on Linux
        // containers and en-US CI runners that produces "150.00" instead of
        // the expected Turkish "150,00". Forcing the request culture here
        // makes admin output deterministic across hosts.
        var trTr = new CultureInfo("tr-TR");
        app.UseRequestLocalization(new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(trTr),
            SupportedCultures = new[] { trTr },
            SupportedUICultures = new[] { trTr },
        });

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        // Rate limiter kimlik doğrulamadan SONRA. Politikaların üçü
        // (backup-upload, backup-delete, whatsapp-send) bölüm anahtarı olarak
        // ctx.User'daki müşteri kimliğini kullanıyor; daha önce burası
        // UseAuthentication'dan önce geldiği için o kimlik her zaman boştu ve
        // anahtar sessizce yedeğe düşüyordu. backup-delete'in yedeği yok
        // ("anon" sabiti), yani limit müşteri başına 30/saat değil TÜM
        // platformda 30/saat olarak işliyordu — bir müşterinin temizliği
        // diğerlerini kilitliyordu. Hata mesajı üretmediği için de yıllarca
        // fark edilmeden durabilirdi; ApiFactory.OnRateLimitPartition kancası
        // ve RateLimiterIdentityTests bunu artık teste bağlıyor.
        //
        // UseAuthorization'dan da sonra olmasının nedeni AddAuthentication()'ın
        // varsayılan şemasının olmaması: UseAuthentication tek başına ctx.User'ı
        // doldurmuyor, doldurma işi politikadaki AddAuthenticationSchemes ile
        // yetkilendirme middleware'inde oluyor.
        //
        // Bedeli: yetkilendirmeden 401/403 ile dönen istekler artık limiter'a
        // hiç uğramıyor, yani geçersiz token'la yapılan seli global limit
        // saymıyor. Kabul edilebilir, çünkü asıl saldırı yüzeyi olan uçların
        // tamamı ([AllowAnonymous] giriş/kayıt/parola/kod-arama) yetkilendirmeden
        // zaten geçiyor ve hem global hem kendi limitine tabi kalıyor.
        app.UseRateLimiter();
        // Dashboard /admin/hangfire altında — Caddy zaten /admin/* proxy'liyor
        // (admin paneli orada) ve AdminCookie path=/ olduğu için burada da geçerli.
        app.UseHangfireDashboard("/admin/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireDashboardAuthFilter() }
        });
        app.MapControllers();
        app.MapRazorPages();

        // Prometheus scrape endpoint at /metrics. Always-on signal regardless of
        // OTLP push state. Restrict via Caddy if exposing to public internet —
        // the exporter itself is unauthenticated by design.
        app.MapPrometheusScrapingEndpoint();

        // Liveness — process up + dispatcher responsive. No deps. Used by orchestrators.
        app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false  // run zero checks → instant 200 if process is alive
        });
        // Readiness — DB reachable. Caddy / load balancers can use this to gate traffic.
        app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });

        app.Run();
    }

    /// <summary>
    /// Caddy'nin arkasında gerçek istemciyi görebilmek için gereken
    /// <see cref="ForwardedHeadersOptions"/>.
    ///
    /// <para>Üretimde Kestrel'e doğrudan kimse ulaşamıyor: compose'ta 8080
    /// <c>expose</c> ediliyor, <c>ports</c> ile yayınlanmıyor; tek giriş Caddy.
    /// Bu middleware olmadan <c>RemoteIpAddress</c> her istekte Caddy
    /// konteynerinin adresi (172.18.0.3) oluyordu ve bunun iki bedeli vardı:</para>
    /// <list type="number">
    /// <item>Rate limit politikalarının tamamı IP'ye göre bölünüyor (auth-login
    /// 5/dk, intake-form-submit 5/saat...). Tek IP = tek kova demek: limitler
    /// kullanıcı başına değil TÜM İNTERNET için geçerliydi. Bir bot herkesi
    /// kilitleyebilir, saldırgan 5 denemesini kendi kotasından değil ortak
    /// kotadan harcardı.</item>
    /// <item>Denetim kayıtlarındaki IP alanı sabit bir konteyner adresiydi,
    /// yani hiçbir şey söylemiyordu.</item>
    /// </list>
    ///
    /// <para><b>Sahtecilik neden mümkün değil:</b> <c>ForwardLimit</c>
    /// varsayılanı 1, yani <c>X-Forwarded-For</c>'un EN SAĞDAKİ girdisi okunur —
    /// o da Caddy'nin kendi eklediği gerçek istemci adresidir. İstemci başlığı
    /// kendi uydurursa Caddy onun SAĞINA ekler, uydurma değer soldaki
    /// girdilerde kalır ve hiç okunmaz. <c>KnownIPNetworks</c>'ün Docker köprü
    /// aralığına daraltılması da başlığın yalnız vekilden geldiğinde dikkate
    /// alınmasını garantiliyor: listedeki ağlardan gelmeyen bir istekte başlık
    /// tamamen yok sayılır.</para>
    ///
    /// <para><b>XForwardedProto neden dahil:</b> Caddy TLS'i sonlandırıp http
    /// olarak proxy'liyor, bu yüzden <c>Request.Scheme</c> "http" görünüyordu ve
    /// admin çerezindeki <see cref="CookieSecurePolicy.SameAsRequest"/> çerezi
    /// <c>Secure</c> bayrağı OLMADAN yazıyordu. <c>UseHttpsRedirection</c>
    /// burada döngü yaratmaz: yapılandırılmış bir HTTPS portu yok, üstelik
    /// scheme artık zaten https.</para>
    /// </summary>
    /// <summary>
    /// Üretimde <see cref="CookieSecurePolicy.Always"/>, başka her yerde
    /// <see cref="CookieSecurePolicy.SameAsRequest"/>.
    ///
    /// Koşul gevşeklik değil zorunluluk: yerel geliştirme ve test sunucusu düz
    /// HTTP konuşuyor. Always orada da geçerli olsaydı tarayıcı/TestServer
    /// çerezi hiç saklamaz, admin paneline giriş yapılamaz ve bunu bir hata
    /// mesajı değil yalnız sonu gelmeyen giriş yönlendirmesi olarak görürdük.
    /// Üretimde ise HTTPS dışında bir şey yok, dolayısıyla Always'in maliyeti
    /// sıfır.
    /// </summary>
    public static CookieSecurePolicy AdminCookieSecurePolicy(IHostEnvironment env) =>
        env.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

    public static ForwardedHeadersOptions CreateForwardedHeadersOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        // Varsayılan liste yalnız loopback'i tanır; konteynerde vekil loopback'te
        // olmadığı için başlık sessizce yok sayılırdı. Listeyi sıfırlayıp
        // bilerek dolduruyoruz.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        // Docker'ın varsayılan köprü havuzu. Ağ yeniden yaratıldığında somut alt
        // ağ kayabildiği için (172.18 → 172.19...) tek adres değil havuzun
        // tamamı güveniliyor; oraya erişebilen zaten compose ağının içinde.
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        // Yerel geliştirme ve konteyner içi çağrılar.
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("127.0.0.0"), 8));
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128));
        return options;
    }

    /// <summary>
    /// Anahtar halkası dizininin gerçekten okunup yazılabildiğini AÇILIŞTA doğrular.
    ///
    /// <para><c>Directory.CreateDirectory</c> bunu kanıtlamaz: dizin zaten varsa
    /// hiçbir şey yapmaz. Konteyner uid 1654 olarak koştuğu için asıl arıza şudur —
    /// host'taki <c>/opt/orderdeck/keys</c> chown'lanmayı unutulursa içindeki
    /// <c>key-*.xml</c> dosyaları <c>0600 root</c> kalır. O zaman dizin listelenir,
    /// açılış sorunsuz görünür, ama ilk <c>Protect/Unprotect</c> çağrısında —
    /// yani ilk WhatsApp gönderiminde — patlar. Deploy çoktan bitmiş, smoke test
    /// yeşil yanmıştır.</para>
    ///
    /// <para>Burada patlamak bunu <b>deploy zamanı</b> arızasına çevirir: konteyner
    /// açılmaz, workflow geri alır. Yanlış alarm riski yok — bu iznler olmadan
    /// uygulama zaten işini yapamaz.</para>
    /// </summary>
    private static void EnsureKeyRingUsable(string keysPath)
    {
        var probe = Path.Combine(keysPath, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            // Var olan anahtarlar da okunabilmeli; yazılabilir dizin + okunamayan
            // dosya tam olarak "chown unutuldu" senaryosu.
            foreach (var key in Directory.EnumerateFiles(keysPath, "key-*.xml"))
                File.OpenRead(key).Dispose();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"DataProtection anahtar halkası '{keysPath}' kullanılamıyor: {ex.Message}. " +
                "Konteyner uid 1654 (app) olarak koşuyor — host'ta " +
                "`chown -R 1654:1654 /opt/orderdeck/keys` çalıştırılmalı " +
                "(bkz. deploy/README.md, denetim O-11).", ex);
        }
    }

    private static async Task SeedAdminAsync(LicenseDbContext db, IConfiguration cfg)
    {
        var username = cfg["Admin:InitialUsername"];
        var hash = cfg["Admin:InitialPasswordHash"];
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(hash)) return;

        var existing = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        if (existing is not null) return;

        db.AdminUsers.Add(new Domain.AdminUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = hash,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
