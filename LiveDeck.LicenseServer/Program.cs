using System.Text;
using System.Threading.RateLimiting;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LiveDeck.LicenseServer;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Options binding
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
        builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));

        // DbContext
        builder.Services.AddDbContext<LicenseDbContext>(opt =>
            opt.UseSqlServer(builder.Configuration.GetConnectionString("LicenseDb")));

        // Services
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<JwtTokenService>();
        builder.Services.AddScoped<EmailConfirmationService>();
        builder.Services.AddScoped<LiveDeck.LicenseServer.Services.Licensing.LicenseIssuer>();
        builder.Services.AddScoped<LiveDeck.LicenseServer.Services.Licensing.LicenseValidator>();
        builder.Services.AddScoped<LiveDeck.LicenseServer.Services.Licensing.ActivationManager>();

        // Email sender selection
        var emailProvider = builder.Configuration["Email:Provider"] ?? "smtp";
        if (emailProvider.Equals("disk", StringComparison.OrdinalIgnoreCase))
            builder.Services.AddSingleton<IEmailSender, DiskEmailSender>();
        else
            builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

        // JWT auth — two schemes (use IOptions so tests can override Jwt:SecretKey via config)
        builder.Services.AddAuthentication()
            .AddJwtBearer("Bearer-Customer", _ => { })
            .AddJwtBearer("Bearer-Admin", _ => { });

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

        builder.Services.AddAuthorization();

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
            opt.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        // CORS — open for now (4d sıkılaştırılır)
        builder.Services.AddCors(opt =>
            opt.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        // Bootstrap: ensure DB created + seed admin user if config has hash
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Database.EnsureCreated();
            await SeedAdminAsync(db, app.Configuration);
        }

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseCors();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

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
