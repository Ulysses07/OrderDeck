using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Hangfire;
using Hangfire.SqlServer;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Backup;
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
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
        builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
        builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
        builder.Services.Configure<OrderDeck.LicenseServer.Services.Audit.AuditRetentionOptions>(
            builder.Configuration.GetSection("Audit:Retention"));

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
        builder.Services.AddSingleton<JwtTokenService>();
        builder.Services.AddScoped<RefreshTokenService>();
        builder.Services.AddScoped<EmailConfirmationService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Licensing.LicenseIssuer>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Licensing.LicenseValidator>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Licensing.ActivationManager>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Audit.AuditRetentionJobs>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Backup.BackupRestoreDrillJob>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Audit.IAuditService,
                                    OrderDeck.LicenseServer.Services.Audit.AuditService>();

        // Email sender selection
        var emailProvider = builder.Configuration["Email:Provider"] ?? "smtp";
        if (emailProvider.Equals("disk", StringComparison.OrdinalIgnoreCase))
            builder.Services.AddSingleton<IEmailSender, DiskEmailSender>();
        else
            builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

        builder.Services.AddSingleton<UnsubscribeTokenSigner>();
        builder.Services.AddScoped<EmailSendCoordinator>();
        builder.Services.AddScoped<ReminderJobs>();
        builder.Services.AddScoped<PasswordResetService>();
        builder.Services.AddScoped<AdminActionEmailService>();
        builder.Services.AddScoped<IntakeFormService>();
        builder.Services.AddSingleton<WhatsAppLinkBuilder>();
        builder.Services.AddSingleton<BackupStorageService>();
        builder.Services.AddScoped<BackupRetentionService>();
        builder.Services.AddScoped<BackupViewerService>();

        // JWT auth — two schemes (use IOptions so tests can override Jwt:SecretKey via config)
        builder.Services.AddAuthentication()
            .AddJwtBearer("Bearer-Customer", _ => { })
            .AddJwtBearer("Bearer-Admin", _ => { })
            .AddCookie("AdminCookie", o =>
            {
                o.LoginPath = "/admin/login";
                o.AccessDeniedPath = "/admin/login";
                o.LogoutPath = "/admin/logout";
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.Cookie.Name = "OrderDeckAdmin";
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
        builder.Services.AddHangfireServer();

        // S3 off-host backup replication. Disabled-by-default; switch to the
        // real implementation only when Backup:S3:Enabled=true. The no-op sink
        // keeps controller code uniform without forcing every prod deployment
        // to provision a bucket up-front.
        var s3Enabled = builder.Configuration.GetValue<bool>("Backup:S3:Enabled");
        if (s3Enabled)
            builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Backup.IS3BackupSink,
                                          OrderDeck.LicenseServer.Services.Backup.S3BackupSink>();
        else
            builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Backup.IS3BackupSink,
                                          OrderDeck.LicenseServer.Services.Backup.NoOpS3BackupSink>();

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

        builder.Services.AddControllers();
        builder.Services.AddRazorPages(opt =>
        {
            opt.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
            opt.Conventions.AllowAnonymousToPage("/Admin/Login");
            opt.Conventions.AllowAnonymousToPage("/Admin/Logout");
            opt.Conventions.AllowAnonymousToFolder("/Public");
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

            // Weekly backup-restore drill — proves an actual production blob
            // round-trips through decrypt + ZIP + SQLite integrity. Failures
            // email the Admin:AlertEmail address. See
            // OrderDeck.LicenseServer/Services/Backup/BackupRestoreDrillJob.cs
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Backup.BackupRestoreDrillJob>(
                "backup-restore-drill",
                j => j.RunAsync(CancellationToken.None),
                "30 4 * * MON");  // 04:30 UTC every Monday (~07:30 Türkiye)
        }

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseCors();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
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
