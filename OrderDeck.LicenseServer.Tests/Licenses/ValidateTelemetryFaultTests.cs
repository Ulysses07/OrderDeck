using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Licenses;

/// <summary>
/// R9-OPS03: /validate içindeki LastSeen/AppVersion dokunuşu SALT telemetri —
/// lisans kararı ondan önce hesaplanıyor. Dokunuşun DB yazma arızası
/// (izin/disk/timeout) başarılı doğrulamayı 500'e çevirirse istemci bunu ağ
/// hatası saymaz (LicenseApiUnknownException) ve offline grace'e düşmez;
/// yani telemetri uğruna sahada lisans kontrolü kırılmış olur. Bu testler
/// aktivasyon satırına yazmayı bilerek patlatıp cevabın 200 kaldığını kanıtlar.
/// </summary>
public class ValidateTelemetryFaultTests : IClassFixture<ValidateTelemetryFaultTests.FaultFactory>
{
    public sealed class FaultFactory : ApiFactory
    {
        public ActivationWriteFaultInterceptor Fault { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(Fault);
    }

    /// <summary>Silahlıyken, değişiklik kümesinde Modified durumda Activation
    /// bulunan her SaveChanges'i DbUpdateException ile düşürür. Kurulum
    /// (activate) sırasında silahsız tutulur ki yalnız validate'in dokunuşu
    /// vurulsun.</summary>
    public sealed class ActivationWriteFaultInterceptor : SaveChangesInterceptor
    {
        public volatile bool Armed;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfArmed(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            ThrowIfArmed(eventData);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfArmed(DbContextEventData eventData)
        {
            if (!Armed || eventData.Context is null) return;
            if (eventData.Context.ChangeTracker.Entries<Activation>()
                .Any(e => e.State == EntityState.Modified))
            {
                throw new DbUpdateException("simulated activation write failure (R9-OPS03 test)");
            }
        }
    }

    private readonly FaultFactory _factory;

    public ValidateTelemetryFaultTests(FaultFactory factory) => _factory = factory;

    private async Task<(HttpClient client, string licenseKey)> SetupActivatedAsync()
    {
        var (adminToken, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var email = $"fault-{Guid.NewGuid():N}@x.com";
        await adminClient.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Fault", initialPassword = "pw12345678", autoConfirm = true
        });
        var issueResp = await adminClient.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD", slotsOverride = 1
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var client = _factory.CreateClient();
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "pw12345678" });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        var actResp = await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = issued!.licenseKey, hardwareFingerprint = "fp-fault", machineName = "PC"
        });
        actResp.StatusCode.Should().Be(HttpStatusCode.Created, "kurulum silahsızken aktivasyon geçmeli");

        return (client, issued.licenseKey);
    }

    [Fact]
    public async Task Validate_returns_200_active_even_when_telemetry_write_fails()
    {
        var (client, key) = await SetupActivatedAsync();
        _factory.Fault.Armed = true;
        try
        {
            var resp = await client.PostAsJsonAsync("/api/v1/licenses/validate", new
            {
                licenseKey = key, hardwareFingerprint = "fp-fault"
            });

            resp.StatusCode.Should().Be(HttpStatusCode.OK,
                "telemetri yazma arızası lisans kararını düşürmemeli (R9-OPS03)");
            var body = await resp.Content.ReadFromJsonAsync<ValidateBody>();
            body!.status.Should().Be("active");
        }
        finally
        {
            _factory.Fault.Armed = false;
        }

        // Arıza geçince dokunuş normal çalışmaya dönmeli.
        var healthy = await client.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-fault"
        });
        healthy.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Heartbeat_endpoint_still_propagates_write_failure()
    {
        // Karşı kontrol: /heartbeat'te dokunuş asıl işin kendisi (ve legacy
        // fingerprint göçü oradan akıyor) — orada hata YUTULMAMALI. Yalıtım
        // yalnız validate'e ait; genişlerse bu test kırılır.
        //
        // Not: TestHost 500 üretmez, yakalanmayan exception'ı çağırana
        // fırlatır — Kestrel'de bu 500 olurdu. Burada kanıtlanan şey
        // hatanın yutulMAdığı; HTTP kodu değil.
        var (client, key) = await SetupActivatedAsync();
        _factory.Fault.Armed = true;
        try
        {
            var act = async () => await client.PostAsJsonAsync("/api/v1/licenses/heartbeat", new
            {
                licenseKey = key, hardwareFingerprint = "fp-fault"
            });
            await act.Should().ThrowAsync<DbUpdateException>(
                "heartbeat'te yazma arızası yutulmamalı — dokunuş orada asıl iş");
        }
        finally
        {
            _factory.Fault.Armed = false;
        }
    }

    private sealed record IssueBody(string licenseKey, DateTimeOffset expiresAt);
    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record ValidateBody(string status, DateTimeOffset? expiresAt, int? remainingDays);
}
