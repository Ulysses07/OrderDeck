using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>SalesSync:ForceEndSilenceMinutes</c> — spec 2026-10-13 §8.7, §11 Y7, Y8. Kural yalnız doğru olmamalı, ayara
/// <b>bağlı</b> da olmalı: başka makineye geri yüklenen kopyanın canlı oturumu devralma eşiği yapılandırmadan okunur;
/// 0 ya da eksi değerle (her canlı yayın sessizlik beklemeden devredilebilirdi) sunucu açılmaz.
/// </summary>
public sealed class ForceEndSilenceOptionsTests : IClassFixture<ForceEndSilenceOptionsTests.ThirtyMinuteApiFactory>
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow.AddHours(-3);

    private readonly ThirtyMinuteApiFactory _factory;

    public ForceEndSilenceOptionsTests(ThirtyMinuteApiFactory factory) => _factory = factory;

    /// <summary>Eşik 30 dk: varsayılan 15 dk'da devralınacak 20 dk'lık sessizlik yetmez, 40 dk yeter.</summary>
    [Theory]
    [InlineData(20, false)]
    [InlineData(40, true)]
    public async Task Esik_ayardan_okunur(int silentMinutes, bool inherits)
    {
        var (client, licenseId) = await SetupAsync();
        var (previous, restored) = await SeedRestoredAsync(licenseId, silentMinutes);
        var sessionId = await SeedLiveSessionAsync(licenseId, previous);

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/sessions/sync", new
        {
            sessions = new[]
            {
                new
                {
                    id = sessionId, title = "Akşam yayını", startedAt = StartedAt, endedAt = StartedAt.AddHours(2),
                    platforms = "youtube", notes = (string?)null, release = true, machineName = "PC-YENI",
                },
            },
            replicaId = restored,
            protocol = 2,
            seenMode = 0,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.StreamSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        stored.OwnerReplicaId.Should().Be(inherits ? restored : previous);
        (stored.ReleasedAt is not null).Should().Be(inherits);
    }

    /// <summary>§5.7: istemcinin "Yayını bitir…" eylemi sunucunun kararıyla aynı eşiği kullanmalı; eşik
    /// <c>sales/state</c>'te ayardan döner, istemciye sabit 15 yazdırılmaz.</summary>
    [Fact]
    public async Task Esik_sales_state_ile_istemciye_acilir()
    {
        var (client, licenseId) = await SetupAsync();

        var state = await client.GetFromJsonAsync<JsonElement>($"/api/v1/licenses/{licenseId}/sales/state");

        state.GetProperty("forceEndSilenceMinutes").GetInt32().Should().Be(30);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Sifir_ya_da_eksi_esikle_sunucu_acilmaz(string minutes)
    {
        using var factory = new SilenceApiFactory(minutes);

        // İstemci oluşturmak host'u kurar ve başlatır.
        var act = () => factory.CreateClient();

        var failure = act.Should().Throw<Exception>().Which;
        FindOptionsValidationException(failure)
            .Should().NotBeNull("eşik ValidateOnStart ile bağlı olmalı")
            .And.Subject.As<OptionsValidationException>()
            .Failures.Should().ContainMatch("*ForceEndSilenceMinutes*");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary><see cref="ApiFactory"/> + <c>SalesSync:ForceEndSilenceMinutes</c>.</summary>
    public class SilenceApiFactory(string minutes) : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:ForceEndSilenceMinutes"] = minutes,
        };
    }

    public sealed class ThirtyMinuteApiFactory() : SilenceApiFactory("30");

    /// <summary>Önceki kopya <paramref name="silentMinutes"/> önce son kez konuştu; geri yüklenmiş kopya ondan sonra
    /// ilk kez görüldü, başka makinede (farklı, üretilmiş parmak izleri).</summary>
    private async Task<(Guid Previous, Guid Restored)> SeedRestoredAsync(Guid licenseId, int silentMinutes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var previousLastSeen = now.AddMinutes(-silentMinutes);
        var previous = new WpfReplica
        {
            LicenseId = licenseId, ReplicaId = Guid.NewGuid(), Protocol = 2, FirstSeenAt = now.AddDays(-3),
            LastSeenAt = previousLastSeen, HardwareFingerprint = $"fp-{Guid.NewGuid():N}",
        };
        var restoredFirstSeen = previousLastSeen.AddSeconds(silentMinutes * 30);
        var restored = new WpfReplica
        {
            LicenseId = licenseId, ReplicaId = Guid.NewGuid(), Protocol = 2, FirstSeenAt = restoredFirstSeen,
            LastSeenAt = restoredFirstSeen, PreviousReplicaId = previous.ReplicaId,
            HardwareFingerprint = $"fp-{Guid.NewGuid():N}",
        };
        db.WpfReplicas.AddRange(previous, restored);
        await db.SaveChangesAsync();
        return (previous.ReplicaId, restored.ReplicaId);
    }

    private async Task<Guid> SeedLiveSessionAsync(Guid licenseId, Guid owner)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Akşam yayını",
            StartedAt = StartedAt,
            Platforms = "youtube",
            UpdatedAt = StartedAt,
            OwnerReplicaId = owner,
            OwnerMachineName = "PC-ESKI",
        };
        db.StreamSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-SESSIZ-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    /// <summary><c>ValidateOnStart</c> hatayı doğrudan ya da <c>AggregateException</c> içinde fırlatabilir
    /// (<c>JwtStartupValidationTests</c> deseni).</summary>
    private static OptionsValidationException? FindOptionsValidationException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is OptionsValidationException ove) return ove;
            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    var found = FindOptionsValidationException(inner);
                    if (found is not null) return found;
                }
                return null;
            }
            ex = ex.InnerException;
        }
        return null;
    }
}
