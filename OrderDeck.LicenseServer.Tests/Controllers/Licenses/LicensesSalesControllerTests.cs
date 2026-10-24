using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary><see cref="ApiFactory"/> + <c>SalesSync</c> ayarları dolu (Ws yayımlanmış, KVKK boşaltması açık).</summary>
public sealed class SalesSyncConfiguredApiFactory : ApiFactory
{
    public const string MinClientVersion = "0.9.11";

    protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
    {
        ["SalesSync:MinClientVersion"] = MinClientVersion,
        ["SalesSync:ScrubOrderTextOnPurge"] = "true",
    };
}

/// <summary>
/// <c>POST sales/hello</c> ve <c>GET sales/state</c> — spec 2026-10-13 §4.5 (InMemory). hello kopya satırını
/// ekler ya da günceller: <c>PreviousReplicaId</c> bir kez yazılır, donanım parmak izinde ilk yazan kazanır, geçiş
/// bayrakları ilk <c>true</c>'da damgalanır ve silinmez. state lisansın kipini, ayarları, devredilmemiş oturumları
/// ve eski sürüm makineleri döner; kalp atışı kopya satırına yazılır, oturum satırına dokunmaz.
/// </summary>
public sealed class LicensesSalesControllerTests
    : IClassFixture<SalesSyncConfiguredApiFactory>, IClassFixture<ApiFactory>
{
    private const string WpfUserAgent = "OrderDeck-WPF/0.9.11";

    private readonly SalesSyncConfiguredApiFactory _configured;
    private readonly ApiFactory _defaults;

    public LicensesSalesControllerTests(SalesSyncConfiguredApiFactory configured, ApiFactory defaults)
    {
        _configured = configured;
        _defaults = defaults;
    }

    // ── hello ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hello_kopyayi_kaydeder_tekrarlari_upsert_eder()
    {
        var (client, licenseId) = await SetupAsync(_configured);
        var replicaId = Guid.NewGuid();
        var firstPrevious = Guid.NewGuid();
        var firstFingerprint = NewFingerprint();
        var startedAt = DateTimeOffset.UtcNow;

        (await HelloAsync(client, licenseId, Hello(replicaId) with
        {
            PreviousReplicaId = firstPrevious,
            MachineName = "PC-A",
            HardwareFingerprint = firstFingerprint,
            FeatureLevel = 1,
            ReportedShipping = new(500m, 60m),
            BackfillCompleted = false,
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var created = await ReplicaAsync(_configured, licenseId, replicaId);
        created.Should().NotBeNull();
        created!.FirstSeenAt.Should().BeOnOrAfter(startedAt);
        created.LastSeenAt.Should().Be(created.FirstSeenAt);
        created.MachineName.Should().Be("PC-A");
        created.HardwareFingerprint.Should().Be(firstFingerprint);
        created.FeatureLevel.Should().Be(1);
        created.Protocol.Should().Be(2);
        created.ClientVersion.Should().Be("0.9.11");
        created.SeenMode.Should().Be(SalesSyncMode.Off);
        created.PreviousReplicaId.Should().Be(firstPrevious);
        created.ReportedThreshold.Should().Be(500m);
        created.ReportedFee.Should().Be(60m);
        created.BackfillCompletedAt.Should().BeNull("false bayrak damgalamaz");
        created.PaymentHistoryCompletedAt.Should().BeNull();

        // İkinci hello: makine adı, yetenek düzeyi ve kargo güncellenir; önceki kimlik ve parmak izi kalır.
        (await HelloAsync(client, licenseId, Hello(replicaId) with
        {
            PreviousReplicaId = Guid.NewGuid(),
            MachineName = "PC-A2",
            HardwareFingerprint = NewFingerprint(),
            FeatureLevel = 3,
            ReportedShipping = new(750m, null),
            BackfillCompleted = true,
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await ReplicaAsync(_configured, licenseId, replicaId);
        second!.FirstSeenAt.Should().Be(created.FirstSeenAt);
        second.PreviousReplicaId.Should().Be(firstPrevious, "geri yükleme zinciri bir kez yazılır");
        second.HardwareFingerprint.Should().Be(firstFingerprint, "parmak izinde ilk yazan kazanır (klon uyarısı)");
        second.MachineName.Should().Be("PC-A2");
        second.FeatureLevel.Should().Be(3);
        second.ReportedThreshold.Should().Be(750m);
        second.ReportedFee.Should().BeNull("kopya ücreti boş bildirdi");
        second.BackfillCompletedAt.Should().NotBeNull();
        var backfillStamp = second.BackfillCompletedAt;

        // Üçüncü: geri doldurma bayrağı false gelse de damga silinmez; kargo verilmezse saklanan kalır.
        (await HelloAsync(client, licenseId, Hello(replicaId) with
        {
            FeatureLevel = 3,
            BackfillCompleted = false,
            PaymentHistoryCompleted = true,
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var third = await ReplicaAsync(_configured, licenseId, replicaId);
        third!.BackfillCompletedAt.Should().Be(backfillStamp);
        third.PaymentHistoryCompletedAt.Should().NotBeNull();
        third.ReportedThreshold.Should().Be(750m);
        third.MachineName.Should().Be("PC-A2", "verilmeyen makine adı saklananı silmez");
        var paymentStamp = third.PaymentHistoryCompletedAt;

        // Dördüncü: bayrak yeniden true — ilk damga yerinde.
        (await HelloAsync(client, licenseId, Hello(replicaId) with
        {
            FeatureLevel = 3,
            BackfillCompleted = true,
            PaymentHistoryCompleted = true,
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var fourth = await ReplicaAsync(_configured, licenseId, replicaId);
        fourth!.BackfillCompletedAt.Should().Be(backfillStamp);
        fourth.PaymentHistoryCompletedAt.Should().Be(paymentStamp);
    }

    /// <summary>Kopya satırı ilk v2 push'uyla açılmış olabilir (yetenek düzeyi 0, parmak izi boş); hello onu
    /// tamamlar, ilk görülme anına dokunmaz.</summary>
    [Fact]
    public async Task Hello_push_ile_acilmis_satiri_tamamlar()
    {
        var (client, licenseId) = await SetupAsync(_configured);
        var replicaId = Guid.NewGuid();
        (await SendAsync(client, HttpMethod.Post, $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = Array.Empty<object>(), replicaId, protocol = 2, seenMode = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var pushed = await ReplicaAsync(_configured, licenseId, replicaId);
        pushed!.FeatureLevel.Should().Be(0);
        pushed.HardwareFingerprint.Should().BeNull();

        var fingerprint = NewFingerprint();
        (await HelloAsync(client, licenseId, Hello(replicaId) with { HardwareFingerprint = fingerprint, FeatureLevel = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var helloed = await ReplicaAsync(_configured, licenseId, replicaId);
        helloed!.FirstSeenAt.Should().Be(pushed.FirstSeenAt);
        helloed.HardwareFingerprint.Should().Be(fingerprint);
        helloed.FeatureLevel.Should().Be(3);
    }

    [Fact]
    public async Task Hello_baskasinin_lisansina_404_ve_kopya_acmaz()
    {
        var (_, foreignLicenseId) = await SetupAsync(_configured);
        var (client, _) = await SetupAsync(_configured);
        var replicaId = Guid.NewGuid();

        (await HelloAsync(client, foreignLicenseId, Hello(replicaId)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await ReplicaAsync(_configured, foreignLicenseId, replicaId)).Should().BeNull();
    }

    public static TheoryData<string> InvalidHellos => new()
    {
        "protokolsuz", "protokol-1", "bos-kopya", "kipsiz", "yetenek-yok", "yetenek-tasma",
        "onceki-kendisi", "onceki-bos", "uzun-makine-adi", "negatif-ucret",
    };

    [Theory]
    [MemberData(nameof(InvalidHellos))]
    public async Task Hello_gecersiz_govde_400_ve_kopya_acmaz(string variant)
    {
        var (client, licenseId) = await SetupAsync(_configured);
        var replicaId = Guid.NewGuid();
        var valid = Hello(replicaId);
        var body = variant switch
        {
            "protokolsuz" => valid with { Protocol = null },
            "protokol-1" => valid with { Protocol = 1 },
            "bos-kopya" => valid with { ReplicaId = Guid.Empty },
            "kipsiz" => valid with { SeenMode = null },
            "yetenek-yok" => valid with { FeatureLevel = null },
            "yetenek-tasma" => valid with { FeatureLevel = 256 },
            "onceki-kendisi" => valid with { PreviousReplicaId = replicaId },
            "onceki-bos" => valid with { PreviousReplicaId = Guid.Empty },
            "uzun-makine-adi" => valid with { MachineName = new string('M', 129) },
            "negatif-ucret" => valid with { ReportedShipping = new(500m, -1m) },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        (await HelloAsync(client, licenseId, body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WpfReplicas.AnyAsync(r => r.LicenseId == licenseId)).Should().BeFalse();
    }

    // ── state ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task State_kipi_ve_ayarlari_doner()
    {
        var (client, licenseId) = await SetupAsync(_configured);

        var state = await StateAsync(client, licenseId);

        state.Mode.Should().Be(0);
        state.ModeChangedAt.Should().BeNull();
        state.MinClientVersion.Should().Be(SalesSyncConfiguredApiFactory.MinClientVersion);
        state.ScrubOrderTextOnPurge.Should().BeTrue();
        state.ForceEndSilenceMinutes.Should().Be(15, "ayar verilmedi: varsayılan Y7 eşiği");
        state.Shipping.Should().BeNull("lisans geneli kargo ayarı hiç yazılmadı (satır yok); dolu hâli ShippingSettingsTests'te");
        state.LiveSessions.Should().BeEmpty();
        state.LegacyMachines.Should().BeEmpty();
    }

    [Fact]
    public async Task State_ayarsiz_sunucuda_alt_surum_tanimsiz_bosaltma_acik()
    {
        var (client, licenseId) = await SetupAsync(_defaults);

        var state = await StateAsync(client, licenseId);

        state.MinClientVersion.Should().BeNull("boş ayar tanımsızdır: kapı kapalı");
        state.ScrubOrderTextOnPurge.Should().BeTrue("KVKK boşaltması varsayılan açık (sahip kararı 8, 2026-10-23)");
    }

    /// <summary>Prod'da ayar compose ortamından gelir: <c>.env</c>'de satır yoksa da açık kalmalı (karar 8). Varsayılan
    /// "true" ZORUNLU, boş dize DEĞİL: bool binder'ı boşu çeviremez.</summary>
    [Fact]
    public void Compose_bosaltma_varsayilani_acik()
    {
        var compose = File.ReadAllText(FindRepoFile("deploy/docker-compose.yml"));

        compose.Should().Contain(
            "SalesSync__ScrubOrderTextOnPurge: \"${SalesSync__ScrubOrderTextOnPurge:-true}\"",
            "deploy/docker-compose.yml license-server environment: bloğunda ayarın varsayılanı açık olmalı");
    }

    /// <summary>Yalnız devredilmemiş oturumlar (<c>ReleasedAt</c> NULL) ve yalnız bu lisansınkiler; hâl
    /// <c>EndedAt</c>'ten, sahibin son görülmesi kopya satırından.</summary>
    [Fact]
    public async Task State_yalniz_devredilmemis_oturumlari_hali_ve_sahip_sinyaliyle_doner()
    {
        var (client, licenseId) = await SetupAsync(_configured);
        var (_, otherLicenseId) = await SetupAsync(_configured);
        var ownerReplica = Guid.NewGuid();
        var ownerSeenAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var now = DateTimeOffset.UtcNow;

        using (var scope = _configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfReplicas.Add(new WpfReplica
            {
                LicenseId = licenseId, ReplicaId = ownerReplica, Protocol = 2,
                FirstSeenAt = ownerSeenAt.AddDays(-1), LastSeenAt = ownerSeenAt,
            });
            await db.SaveChangesAsync();
        }

        var live = await SeedSessionAsync(_configured, licenseId, s =>
        {
            s.StartedAt = now.AddHours(-1);
            s.OwnerReplicaId = ownerReplica;
            s.OwnerMachineName = "PC-A";
        });
        var handingOver = await SeedSessionAsync(_configured, licenseId, s =>
        {
            s.StartedAt = now.AddHours(-5);
            s.EndedAt = now.AddHours(-4);
            s.OwnerReplicaId = Guid.NewGuid();
            s.OwnerMachineName = "PC-B";
            s.UnsentLabelCount = 3;
        });
        await SeedSessionAsync(_configured, licenseId, s =>
        {
            s.StartedAt = now.AddDays(-2);
            s.EndedAt = now.AddDays(-2).AddHours(2);
            s.ReleasedAt = now.AddDays(-2).AddHours(3);
        });
        await SeedSessionAsync(_configured, otherLicenseId, s => s.StartedAt = now.AddMinutes(-30));

        var state = await StateAsync(client, licenseId);

        state.LiveSessions.Select(s => s.Id).Should().Equal(handingOver, live);
        var liveDto = state.LiveSessions.Single(s => s.Id == live);
        liveDto.State.Should().Be("live");
        liveDto.EndedAt.Should().BeNull();
        liveDto.OwnerMachineName.Should().Be("PC-A");
        liveDto.OwnerLastSeenAt.Should().Be(ownerSeenAt);
        liveDto.Title.Should().Be("Yayın");
        var handingOverDto = state.LiveSessions.Single(s => s.Id == handingOver);
        handingOverDto.State.Should().Be("handing-over");
        handingOverDto.UnsentLabelCount.Should().Be(3);
        handingOverDto.OwnerMachineName.Should().Be("PC-B");
        handingOverDto.OwnerLastSeenAt.Should().BeNull("sahibin kopya satırı yok");
    }

    /// <summary>state çağrısı kalp atışıdır: kopya satırı ilerler, oturum satırı değişmez (rowversion ve akış her
    /// 30 sn'de bir ilerlemesin). Yanıttaki sahip sinyali bu isteğin kalp atışını içerir.</summary>
    [Fact]
    public async Task State_kalp_atisi_kopyayi_ilerletir_oturuma_dokunmaz()
    {
        var (client, licenseId) = await SetupAsync(_configured);
        var replicaId = Guid.NewGuid();
        using (var scope = _configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfReplicas.Add(new WpfReplica
            {
                LicenseId = licenseId, ReplicaId = replicaId, Protocol = 2,
                FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-1), LastSeenAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            await db.SaveChangesAsync();
        }
        var sessionUpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var sessionId = await SeedSessionAsync(_configured, licenseId, s =>
        {
            s.OwnerReplicaId = replicaId;
            s.UpdatedAt = sessionUpdatedAt;
        });
        var before = DateTimeOffset.UtcNow;

        var state = await StateAsync(client, licenseId, $"?replicaId={replicaId}&seenMode=0");

        var replica = await ReplicaAsync(_configured, licenseId, replicaId);
        replica!.LastSeenAt.Should().BeOnOrAfter(before);
        replica.SeenMode.Should().Be(SalesSyncMode.Off);
        replica.ClientVersion.Should().Be("0.9.11");
        state.LiveSessions.Single().OwnerLastSeenAt.Should().Be(replica.LastSeenAt);
        using (var scope = _configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.StreamSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId))
                .UpdatedAt.Should().Be(sessionUpdatedAt);
        }
    }

    /// <summary>Kalp atışsız state (kopya kimliği yok) bir kopya satırı açmaz; yarım kalp atışı 400.</summary>
    [Theory]
    [InlineData("", HttpStatusCode.OK)]
    [InlineData("?replicaId=@ID", HttpStatusCode.BadRequest)]
    [InlineData("?seenMode=0", HttpStatusCode.BadRequest)]
    [InlineData("?replicaId=@ID&seenMode=2", HttpStatusCode.BadRequest)]
    public async Task State_kalp_atisi_parametreleri(string query, HttpStatusCode expected)
    {
        var (client, licenseId) = await SetupAsync(_configured);

        var resp = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/licenses/{licenseId}/sales/state{query.Replace("@ID", Guid.NewGuid().ToString())}");

        resp.StatusCode.Should().Be(expected);
        using var scope = _configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WpfReplicas.AnyAsync(r => r.LicenseId == licenseId)).Should().BeFalse();
    }

    [Fact]
    public async Task State_baskasinin_lisansina_404()
    {
        var (_, foreignLicenseId) = await SetupAsync(_configured);
        var (client, _) = await SetupAsync(_configured);

        (await SendAsync(client, HttpMethod.Get, $"/api/v1/licenses/{foreignLicenseId}/sales/state"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Alt sürüm tanımlıyken eski makine: son 14 günde görülmüş, devre dışı olmayan ve sürümü bilinmeyen ya
    /// da alt sınırın altındaki etkinleştirme.</summary>
    [Fact]
    public async Task State_eski_makineler_alt_surumle_ayrilir()
    {
        var (client, licenseId) = await SetupAsync(_configured);
        var now = DateTimeOffset.UtcNow;
        await SeedActivationAsync(_configured, licenseId, "PC-ESKI", "0.9.10", now.AddDays(-1));
        await SeedActivationAsync(_configured, licenseId, "PC-SURUMSUZ", null, now.AddDays(-2));
        await SeedActivationAsync(_configured, licenseId, "PC-WS", "0.9.11", now.AddDays(-1));
        await SeedActivationAsync(_configured, licenseId, "PC-YENI", "0.9.12", now.AddHours(-1));
        await SeedActivationAsync(_configured, licenseId, "PC-KAPALI", "0.9.9", now.AddDays(-1), deactivated: true);
        await SeedActivationAsync(_configured, licenseId, "PC-UZAK", "0.9.9", now.AddDays(-20));

        var state = await StateAsync(client, licenseId);

        state.LegacyMachines.Select(m => m.MachineName).Should().Equal("PC-ESKI", "PC-SURUMSUZ");
        state.LegacyMachines[0].AppVersion.Should().Be("0.9.10");
        state.LegacyMachines[1].AppVersion.Should().BeNull();
    }

    /// <summary>Alt sürüm tanımsızken (Ws yayımlanmadı) sürümle ayrılamaz: aynı parmak izinde v2 konuşmuş kopyası
    /// olmayan etkinleştirme eskidir.</summary>
    [Fact]
    public async Task State_eski_makineler_alt_surum_yokken_v2_kopyayla_ayrilir()
    {
        var (client, licenseId) = await SetupAsync(_defaults);
        var now = DateTimeOffset.UtcNow;
        await SeedActivationAsync(_defaults, licenseId, "PC-ESKI", "0.9.10", now.AddDays(-1));
        var wsFingerprint = await SeedActivationAsync(_defaults, licenseId, "PC-WS", "0.9.11", now.AddHours(-2));
        await SeedActivationAsync(_defaults, licenseId, "PC-KAPALI", "0.9.10", now.AddDays(-1), deactivated: true);
        using (var scope = _defaults.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfReplicas.Add(new WpfReplica
            {
                LicenseId = licenseId, ReplicaId = Guid.NewGuid(), Protocol = 2, HardwareFingerprint = wsFingerprint,
                FirstSeenAt = now.AddDays(-1), LastSeenAt = now.AddHours(-2),
            });
            await db.SaveChangesAsync();
        }

        var state = await StateAsync(client, licenseId);

        state.LegacyMachines.Select(m => m.MachineName).Should().Equal("PC-ESKI");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record HelloBody(
        Guid? ReplicaId,
        Guid? PreviousReplicaId,
        string? MachineName,
        string? HardwareFingerprint,
        int? Protocol,
        int? FeatureLevel,
        int? SeenMode,
        ShippingBody? ReportedShipping,
        bool? BackfillCompleted,
        bool? PaymentHistoryCompleted);

    private sealed record ShippingBody(decimal? Threshold, decimal? Fee);

    private sealed record StateBody(
        int Mode,
        DateTimeOffset? ModeChangedAt,
        string? MinClientVersion,
        List<LiveSessionBody> LiveSessions,
        object? Shipping,
        bool ScrubOrderTextOnPurge,
        int ForceEndSilenceMinutes,
        List<LegacyMachineBody> LegacyMachines);

    private sealed record LiveSessionBody(
        Guid Id, string? Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? OwnerMachineName,
        DateTimeOffset? OwnerLastSeenAt, string State, int? UnsentLabelCount);

    private sealed record LegacyMachineBody(string? MachineName, string? AppVersion, DateTimeOffset LastSeenAt);

    private static HelloBody Hello(Guid replicaId) => new(
        ReplicaId: replicaId,
        PreviousReplicaId: null,
        MachineName: null,
        HardwareFingerprint: null,
        Protocol: 2,
        FeatureLevel: 1,
        SeenMode: 0,
        ReportedShipping: null,
        BackfillCompleted: null,
        PaymentHistoryCompleted: null);

    private static string NewFingerprint() => $"fp-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> HelloAsync(HttpClient client, Guid licenseId, HelloBody body) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/licenses/{licenseId}/sales/hello", body);

    private static async Task<StateBody> StateAsync(HttpClient client, Guid licenseId, string query = "")
    {
        var resp = await SendAsync(client, HttpMethod.Get, $"/api/v1/licenses/{licenseId}/sales/state{query}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<StateBody>())!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        request.Headers.UserAgent.ParseAdd(WpfUserAgent);
        return client.SendAsync(request);
    }

    private static async Task<WpfReplica?> ReplicaAsync(ApiFactory factory, Guid licenseId, Guid replicaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WpfReplicas.AsNoTracking()
            .SingleOrDefaultAsync(r => r.LicenseId == licenseId && r.ReplicaId == replicaId);
    }

    private static async Task<Guid> SeedSessionAsync(ApiFactory factory, Guid licenseId, Action<StreamSession> shape)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Yayın",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Platforms = "youtube",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        shape(session);
        db.StreamSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    /// <returns>Üretilen donanım parmak izi.</returns>
    private static async Task<string> SeedActivationAsync(
        ApiFactory factory, Guid licenseId, string machineName, string? appVersion, DateTimeOffset lastSeenAt,
        bool deactivated = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var fingerprint = NewFingerprint();
        db.Activations.Add(new Activation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            HardwareFingerprint = fingerprint,
            MachineName = machineName,
            AppVersion = appVersion,
            ActivatedAt = lastSeenAt.AddDays(-30),
            LastSeenAt = lastSeenAt,
            DeactivatedAt = deactivated ? lastSeenAt : null,
        });
        await db.SaveChangesAsync();
        return fingerprint;
    }

    private static async Task<(HttpClient Client, Guid LicenseId)> SetupAsync(ApiFactory factory)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-SATIS-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 5,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    /// <summary>Testler bin klasöründen koşuyor; repo kökünü yukarı yürüyerek buluyoruz (ObifinDataControllerGateTests gibi).</summary>
    private static string FindRepoFile(string relativePath)
    {
        var parts = relativePath.Split('/');
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"{relativePath} bulunamadı ({AppContext.BaseDirectory} klasöründen yukarı arandı). " +
            "Dosya taşındıysa bu testi güncelle; testi silme.");
    }
}
