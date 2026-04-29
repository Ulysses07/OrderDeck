using System.Text;
using System.Threading.RateLimiting;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace LiveDeck.LicenseServer;

public class Program
{
    public static void Main(string[] args)
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

        // Email sender selection
        var emailProvider = builder.Configuration["Email:Provider"] ?? "smtp";
        if (emailProvider.Equals("disk", StringComparison.OrdinalIgnoreCase))
            builder.Services.AddSingleton<IEmailSender, DiskEmailSender>();
        else
            builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

        // JWT auth — two schemes
        var jwtSecret = builder.Configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey missing");
        var jwtIssuer = builder.Configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Jwt:Issuer missing");
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

        builder.Services.AddAuthentication()
            .AddJwtBearer("Bearer-Customer", o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwtIssuer,
                    ValidateAudience = true, ValidAudience = JwtOptions.CustomerAudience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            })
            .AddJwtBearer("Bearer-Admin", o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwtIssuer,
                    ValidateAudience = true, ValidAudience = JwtOptions.AdminAudience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey,
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
}
