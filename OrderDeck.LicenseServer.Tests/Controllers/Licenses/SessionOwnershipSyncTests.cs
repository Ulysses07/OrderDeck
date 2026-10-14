using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>sessions/sync</c>'te yayın sahipliği ve devir — spec 2026-10-13 §2, §4.1, §8.7, M2. InMemory: devrin siparişleri
/// damgalaması burada izlenen varlıklarla yürür; aynı işlem ve rowversion sırası
/// <see cref="SessionReleaseRelationalTests"/>'te gerçek SQL Server'da.
/// </summary>
public sealed class SessionOwnershipSyncTests : IClassFixture<SessionOwnershipSyncTests.LogApiFactory>
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow.AddHours(-3);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddHours(2);

    private readonly LogApiFactory _factory;

    public SessionOwnershipSyncTests(LogApiFactory factory) => _factory = factory;

    // ── Ekleme ────────────────────────────────────────────────────────────────

    /// <summary>§4.1: v2 eklemesinde çağıran kopya sahip olur ve makine adı yazılır; v1 eklemesinde ikisi de boş
    /// kalır (miras). Yanıt sahibi, devir anını ve protokolü taşır.</summary>
    [Fact]
    public async Task V2_ekleme_sahibi_yazar_v1_ekleme_bos_birakir()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var v2Id = Guid.NewGuid();
        var v1Id = Guid.NewGuid();

        var v2 = await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(v2Id, machineName: "PC-A")));
        var v1 = await PushSessionsAsync(client, licenseId, V1Body(V1Session(v1Id)));

        var v2Echo = v2.Single();
        v2Echo.OwnerReplicaId.Should().Be(replicaId);
        v2Echo.ReleasedAt.Should().BeNull();
        v2Echo.Protocol.Should().Be(2);
        v2Echo.Status.Should().Be("applied");
        v1.Single().OwnerReplicaId.Should().BeNull();

        var stored = await SessionAsync(v2Id);
        stored.OwnerReplicaId.Should().Be(replicaId);
        stored.OwnerMachineName.Should().Be("PC-A");
        var legacy = await SessionAsync(v1Id);
        legacy.OwnerReplicaId.Should().BeNull();
        legacy.OwnerMachineName.Should().BeNull();
    }

    // ── Sahibin kapanışı ──────────────────────────────────────────────────────

    /// <summary>M2: kuyruğu boşalmamış sahip <c>Release = false</c> gönderir. Yayın biter ama devredilmez
    /// (devrediliyor hâli); gönderilmemiş etiket sayısı saklanır, siparişler akışa girmez.</summary>
    [Fact]
    public async Task Sahibin_Release_false_kapanisi_devretmez_ve_gonderilmemis_sayiyi_saklar()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(sessionId)));
        var orderId = await PushOrderAsync(client, licenseId, sessionId);

        var echo = (await PushSessionsAsync(client, licenseId,
            V2Body(replicaId, V2Session(sessionId, EndedAt, release: false, unsent: 3)))).Single();

        echo.ReleasedAt.Should().BeNull();
        echo.EndedAt.Should().Be(EndedAt);
        var stored = await SessionAsync(sessionId);
        stored.ReleasedAt.Should().BeNull();
        stored.UnsentLabelCount.Should().Be(3);
        (await OrderAsync(orderId)).SessionReleasedAt.Should().BeNull();

        // Sayı her kapanış gönderiminde tazelenir.
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(sessionId, EndedAt, release: false, unsent: 1)));
        (await SessionAsync(sessionId)).UnsentLabelCount.Should().Be(1);
    }

    /// <summary>§4.1: devrediliyor hâlinde EndedAt tekdüze. Sahibin kuyruğundaki bayat oturum satırı (EndedAt boş
    /// ya da farklı) kapanışı silmez ya da kaydırmaz; oturum devrediliyor kalır, sayı tazelenir.</summary>
    [Fact]
    public async Task Devrediliyor_oturumda_sahibin_bayat_gonderimi_EndedAt_i_degistirmez()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(sessionId)));
        await PushSessionsAsync(client, licenseId,
            V2Body(replicaId, V2Session(sessionId, EndedAt, release: false, unsent: 3)));

        var cleared = (await PushSessionsAsync(client, licenseId,
            V2Body(replicaId, V2Session(sessionId, endedAt: null, release: false, unsent: 2)))).Single();
        var shifted = (await PushSessionsAsync(client, licenseId,
            V2Body(replicaId, V2Session(sessionId, EndedAt.AddMinutes(40), release: false, unsent: 1)))).Single();

        cleared.EndedAt.Should().Be(EndedAt, "boş EndedAt kapanışı silmez");
        shifted.EndedAt.Should().Be(EndedAt, "farklı EndedAt kapanışı kaydırmaz");
        var stored = await SessionAsync(sessionId);
        stored.EndedAt.Should().Be(EndedAt);
        stored.ReleasedAt.Should().BeNull("Release = false: devrediliyor kalır (M2)");
        stored.UnsentLabelCount.Should().Be(1, "sahip hâlâ sahip; sayı her kapanış gönderiminde tazelenir");
    }

    /// <summary>§4.1, DM3: kuyruğu boşalmış sahibin <c>Release = true</c> kapanışı yayını devreder ve yayının bütün
    /// siparişlerine aynı devir anını basar.</summary>
    [Fact]
    public async Task Sahibin_Release_true_kapanisi_devreder_ve_siparisleri_damgalar()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(sessionId)));
        var first = await PushOrderAsync(client, licenseId, sessionId);
        var second = await PushOrderAsync(client, licenseId, sessionId);
        var otherSession = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(otherSession)));
        var unrelated = await PushOrderAsync(client, licenseId, otherSession);
        var before = DateTimeOffset.UtcNow;

        var echo = (await PushSessionsAsync(client, licenseId,
            V2Body(replicaId, V2Session(sessionId, EndedAt, release: true)))).Single();

        echo.ReleasedAt.Should().NotBeNull().And.BeOnOrAfter(before);
        echo.Status.Should().Be("applied");
        var stored = await SessionAsync(sessionId);
        stored.ReleasedAt.Should().Be(echo.ReleasedAt);
        (await OrderAsync(first)).SessionReleasedAt.Should().Be(stored.ReleasedAt);
        (await OrderAsync(second)).SessionReleasedAt.Should().Be(stored.ReleasedAt);
        (await OrderAsync(unrelated)).SessionReleasedAt.Should().BeNull("başka yayının siparişi damgalanmaz");
    }

    /// <summary>v2 eklemesi kapanmış ve kuyruğu boş yayını da (çevrimdışı açılıp kapanmış) tek gönderimde devreder:
    /// ekleyen kopya sahip olarak yargılanır.</summary>
    [Fact]
    public async Task Kapanmis_yayinin_v2_eklemesi_Release_ile_devredilir()
    {
        var (client, licenseId) = await SetupAsync();
        var sessionId = Guid.NewGuid();

        var echo = (await PushSessionsAsync(client, licenseId,
            V2Body(Guid.NewGuid(), V2Session(sessionId, EndedAt, release: true)))).Single();

        echo.ReleasedAt.Should().NotBeNull();
        (await SessionAsync(sessionId)).ReleasedAt.Should().Be(echo.ReleasedAt);
    }

    // ── Sahip olmayan ─────────────────────────────────────────────────────────

    /// <summary>§4.1: v2'de sahip olmayan kopyanın EndedAt'i yok sayılır ve öğe <c>ignored</c> döner; yayını
    /// kapatmanın tek başka yolu force-end. Başlık, platform ve not bugünkü gibi yazılır.</summary>
    [Fact]
    public async Task Sahip_olmayan_kopyanin_EndedAt_i_yok_sayilir()
    {
        var (client, licenseId) = await SetupAsync();
        var owner = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V2Body(owner, V2Session(sessionId)));

        var echo = (await PushSessionsAsync(client, licenseId,
            V2Body(Guid.NewGuid(), V2Session(sessionId, EndedAt, release: true, title: "Yeni başlık", unsent: 4)))).Single();

        echo.Status.Should().Be("ignored");
        echo.Reason.Should().Be("session-owned-elsewhere");
        echo.EndedAt.Should().BeNull();
        echo.OwnerReplicaId.Should().Be(owner);
        var stored = await SessionAsync(sessionId);
        stored.EndedAt.Should().BeNull();
        stored.ReleasedAt.Should().BeNull();
        stored.UnsentLabelCount.Should().BeNull();
        stored.Title.Should().Be("Yeni başlık");
        stored.OwnerReplicaId.Should().Be(owner);
    }

    // ── Miras yol: v1 ve sahipsiz oturum ──────────────────────────────────────

    /// <summary>§4.1: protokolsüz kapanış bugünkü sırayla devreder (EndedAt dolu gelmesi yeter); siparişler
    /// damgalanır. Kapanmış yayının v1 eklemesi de devredilir.</summary>
    [Fact]
    public async Task V1_kapanisi_devreder()
    {
        var (client, licenseId) = await SetupAsync();
        var sessionId = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V1Body(V1Session(sessionId)));
        var orderId = await PushOrderAsync(client, licenseId, sessionId);

        var echo = (await PushSessionsAsync(client, licenseId, V1Body(V1Session(sessionId, EndedAt)))).Single();

        echo.ReleasedAt.Should().NotBeNull();
        (await SessionAsync(sessionId)).ReleasedAt.Should().Be(echo.ReleasedAt);
        (await OrderAsync(orderId)).SessionReleasedAt.Should().Be(echo.ReleasedAt);

        var endedOnInsert = Guid.NewGuid();
        (await PushSessionsAsync(client, licenseId, V1Body(V1Session(endedOnInsert, EndedAt))))
            .Single().ReleasedAt.Should().NotBeNull();
    }

    /// <summary>§4.1: sahibi boş (eski sürümde açılmış) oturumda v2 kapanışı <c>Release</c>'e bakmadan devreder —
    /// miras yol, bugünkü sıra.</summary>
    [Fact]
    public async Task Sahipsiz_oturumun_v2_kapanisi_devreder()
    {
        var (client, licenseId) = await SetupAsync();
        var sessionId = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V1Body(V1Session(sessionId)));

        var echo = (await PushSessionsAsync(client, licenseId,
            V2Body(Guid.NewGuid(), V2Session(sessionId, EndedAt, release: false, unsent: 2)))).Single();

        echo.ReleasedAt.Should().NotBeNull();
        echo.OwnerReplicaId.Should().BeNull("sessions/sync sahipsiz oturumu sahiplenmez");
        (await SessionAsync(sessionId)).ReleasedAt.Should().Be(echo.ReleasedAt);
    }

    /// <summary>§4.1, mod On: miras yoldan devir izi günlüğe düşer (yalnız kimlik); mod Off'ta düşmez.</summary>
    [Theory]
    [InlineData(SalesSyncMode.On, true)]
    [InlineData(SalesSyncMode.Off, false)]
    public async Task Mod_On_da_miras_devir_gunluge_yazilir(SalesSyncMode mode, bool logged)
    {
        var (client, licenseId) = await SetupAsync(mode);
        var sessionId = Guid.NewGuid();

        (await PushSessionsAsync(client, licenseId, V1Body(V1Session(sessionId, EndedAt))))
            .Single().ReleasedAt.Should().NotBeNull();

        _factory.Log.Messages.Any(m => m.Contains(licenseId.ToString()) && m.Contains("miras yoldan"))
            .Should().Be(logged);
    }

    // ── Devredilmiş oturum ────────────────────────────────────────────────────

    /// <summary>§4.1: devredilmiş oturumda başlık, platform ve not yok sayılır (her biçimde); satıra hiç yazılmaz,
    /// akış yankısı doğmaz.</summary>
    [Fact]
    public async Task Devredilmis_oturumda_baslik_degisikligi_yok_sayilir()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(sessionId, EndedAt, release: true)));
        var released = await SessionAsync(sessionId);

        var v2 = (await PushSessionsAsync(client, licenseId,
            V2Body(replicaId, V2Session(sessionId, EndedAt, release: true, title: "Değişti")))).Single();
        var v1 = (await PushSessionsAsync(client, licenseId,
            V1Body(V1Session(sessionId, EndedAt, title: "Yine değişti")))).Single();

        v2.Status.Should().Be("ignored");
        v2.Reason.Should().Be("session-released");
        v1.Status.Should().Be("ignored");
        var stored = await SessionAsync(sessionId);
        stored.Title.Should().Be(released.Title);
        stored.UpdatedAt.Should().Be(released.UpdatedAt, "devredilmiş satıra yazılmaz");
        stored.ReleasedAt.Should().Be(released.ReleasedAt);

        // Aynı değerin yeniden gönderimi (W1 devri görene kadar oturumu yeniden yollar) yok sayma değildir.
        (await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(sessionId, EndedAt, release: true))))
            .Single().Status.Should().Be("applied");
    }

    /// <summary>
    /// S1 öncesi imaja dönüş kalıntısı (deploy/README, §8.6): eski kod (<c>current.EndedAt = item.EndedAt</c>)
    /// v0.9.10'un bayat gönderimiyle devredilmiş oturumun EndedAt'ini NULL'layabilir. Dolu EndedAt yeniden gelirse
    /// devredilmiş oturum onu da alır — tekdüzelik: yalnız boş doldurulur, sonra kaymaz. Boş gelen gönderim satıra
    /// yazmaz; devir anı değişmez.
    /// </summary>
    [Fact]
    public async Task Devredilmis_oturumun_bos_EndedAt_i_dolu_gonderimle_onarilir()
    {
        var (client, licenseId) = await SetupAsync();
        var releasedAt = EndedAt.AddMinutes(1);
        var sessionId = await SeedSessionAsync(licenseId, owner: null, machineName: null, releasedAt: releasedAt);
        var seeded = await SessionAsync(sessionId);

        var empty = (await PushSessionsAsync(client, licenseId, V1Body(V1Session(sessionId)))).Single();

        empty.EndedAt.Should().BeNull();
        (await SessionAsync(sessionId)).UpdatedAt.Should().Be(seeded.UpdatedAt, "boş gönderim yazmaz");

        var echo = (await PushSessionsAsync(client, licenseId, V1Body(V1Session(sessionId, EndedAt)))).Single();

        echo.Status.Should().Be("applied");
        echo.EndedAt.Should().Be(EndedAt);
        var stored = await SessionAsync(sessionId);
        stored.EndedAt.Should().Be(EndedAt);
        stored.ReleasedAt.Should().Be(releasedAt, "devir anı değişmez");

        await PushSessionsAsync(client, licenseId, V1Body(V1Session(sessionId, EndedAt.AddMinutes(40))));
        (await SessionAsync(sessionId)).EndedAt.Should().Be(EndedAt, "doldurulan EndedAt da tekdüze");
    }

    // ── Geri yüklenmiş kopya (§8.7) ───────────────────────────────────────────

    /// <summary>§8.7: geri yüklenmiş kopya, önceki kopyanın devredilmemiş oturumunu devralır — önceki kopya,
    /// yenisinin ilk görüldüğü andan beri hiç konuşmadıysa. Sahiplik geçer, geçiş günlüğe yazılır ve çağıran sahip
    /// olarak yargılanır.</summary>
    [Fact]
    public async Task Geri_yuklenmis_kopya_sessiz_onceki_kopyanin_oturumunu_devralir()
    {
        var (client, licenseId) = await SetupAsync();
        var previous = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var restoredFirstSeen = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedReplicaAsync(licenseId, previous, lastSeenAt: restoredFirstSeen.AddMinutes(-5));
        await SeedReplicaAsync(licenseId, restored, firstSeenAt: restoredFirstSeen, previousReplicaId: previous);
        var sessionId = await SeedSessionAsync(licenseId, owner: previous, machineName: "PC-ESKI");

        var echo = (await PushSessionsAsync(client, licenseId,
            V2Body(restored, V2Session(sessionId, EndedAt, release: true, machineName: "PC-YENI")))).Single();

        echo.Status.Should().Be("applied");
        echo.OwnerReplicaId.Should().Be(restored);
        echo.ReleasedAt.Should().NotBeNull();
        var stored = await SessionAsync(sessionId);
        stored.OwnerReplicaId.Should().Be(restored);
        stored.OwnerMachineName.Should().Be("PC-YENI");
        _factory.Log.Messages.Should().Contain(m => m.Contains(sessionId.ToString())
            && m.Contains(previous.ToString()) && m.Contains(restored.ToString()));
    }

    /// <summary>§8.7: önceki kopya yenisinin ilk görüldüğü andan sonra konuştuysa (iki veritabanı birden açık)
    /// sahiplik geçmez; çağıran sahip olmayan kopyadır, EndedAt'i yok sayılır.</summary>
    [Fact]
    public async Task Onceki_kopya_konustuysa_devralma_yok()
    {
        var (client, licenseId) = await SetupAsync();
        var previous = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var restoredFirstSeen = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedReplicaAsync(licenseId, previous, lastSeenAt: restoredFirstSeen.AddMinutes(5));
        await SeedReplicaAsync(licenseId, restored, firstSeenAt: restoredFirstSeen, previousReplicaId: previous);
        var sessionId = await SeedSessionAsync(licenseId, owner: previous, machineName: "PC-ESKI");

        var echo = (await PushSessionsAsync(client, licenseId,
            V2Body(restored, V2Session(sessionId, EndedAt, release: true)))).Single();

        echo.Status.Should().Be("ignored");
        echo.OwnerReplicaId.Should().Be(previous);
        var stored = await SessionAsync(sessionId);
        stored.OwnerReplicaId.Should().Be(previous);
        stored.EndedAt.Should().BeNull();
        stored.ReleasedAt.Should().BeNull();
    }

    /// <summary>§8.7: zincir boyunca (iki kez geri yükleme) devralma, aradaki bütün kopyalar sessizse olur; aradaki
    /// kopya konuşuyorsa daha eskisinin oturumu ona aittir, en yeniye geçmez.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Zincirde_devralma_aradaki_kopyalar_sessizse(bool middleActive, bool handedOver)
    {
        var (client, licenseId) = await SetupAsync();
        var oldest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var newestFirstSeen = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedReplicaAsync(licenseId, oldest, lastSeenAt: newestFirstSeen.AddDays(-2));
        await SeedReplicaAsync(licenseId, middle, firstSeenAt: newestFirstSeen.AddDays(-1), previousReplicaId: oldest,
            lastSeenAt: middleActive ? newestFirstSeen.AddMinutes(10) : newestFirstSeen.AddMinutes(-10));
        await SeedReplicaAsync(licenseId, newest, firstSeenAt: newestFirstSeen, previousReplicaId: middle);
        var sessionId = await SeedSessionAsync(licenseId, owner: oldest, machineName: "PC-ESKI");

        await PushSessionsAsync(client, licenseId, V2Body(newest, V2Session(sessionId, EndedAt, release: true)));

        var stored = await SessionAsync(sessionId);
        stored.OwnerReplicaId.Should().Be(handedOver ? newest : oldest);
        (stored.ReleasedAt is not null).Should().Be(handedOver);
    }

    /// <summary>Devralma yalnız devredilmemiş oturumda: devredilmiş oturumun sahibi değişmez.</summary>
    [Fact]
    public async Task Devredilmis_oturumun_sahibi_devralinmaz()
    {
        var (client, licenseId) = await SetupAsync();
        var previous = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var restoredFirstSeen = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedReplicaAsync(licenseId, previous, lastSeenAt: restoredFirstSeen.AddMinutes(-5));
        await SeedReplicaAsync(licenseId, restored, firstSeenAt: restoredFirstSeen, previousReplicaId: previous);
        var sessionId = await SeedSessionAsync(licenseId, owner: previous, machineName: "PC-ESKI",
            endedAt: EndedAt, releasedAt: EndedAt.AddMinutes(1));

        await PushSessionsAsync(client, licenseId, V2Body(restored, V2Session(sessionId, EndedAt, release: true)));

        (await SessionAsync(sessionId)).OwnerReplicaId.Should().Be(previous);
    }

    // ── Sipariş ekleme (DM3) ──────────────────────────────────────────────────

    /// <summary>DM3: devirden sonra gelen sipariş eklemesi (force-end sonrası satış, geç gönderim) oturumun devir
    /// anını kopyalar ve hemen akışa girer — v1'de de v2 zarfında da. Devredilmemiş oturumun eklemesi boş kalır.</summary>
    [Fact]
    public async Task Devirden_sonra_eklenen_siparis_devir_anini_kopyalar()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var liveSession = Guid.NewGuid();
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(sessionId, EndedAt, release: true)));
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(liveSession)));
        var releasedAt = (await SessionAsync(sessionId)).ReleasedAt;
        releasedAt.Should().NotBeNull();

        var v1Order = await PushOrderAsync(client, licenseId, sessionId);
        var v2Order = await PushOrderAsync(client, licenseId, sessionId, replicaId);
        var liveOrder = await PushOrderAsync(client, licenseId, liveSession, replicaId);

        (await OrderAsync(v1Order)).SessionReleasedAt.Should().Be(releasedAt);
        (await OrderAsync(v2Order)).SessionReleasedAt.Should().Be(releasedAt);
        (await OrderAsync(liveOrder)).SessionReleasedAt.Should().BeNull();
    }

    // ── Bildirim ──────────────────────────────────────────────────────────────

    /// <summary>§4.1: "Yayın başladı" bildirimi değişmez: yalnız yeni ve canlı oturumda, v2'de de. Kapanış ve devir
    /// bildirim üretmez.</summary>
    [Fact]
    public async Task Yayin_basladi_bildirimi_yalniz_yeni_canli_oturumda()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var live = Guid.NewGuid();

        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(live)));
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(live, EndedAt, release: true)));
        await PushSessionsAsync(client, licenseId, V2Body(replicaId, V2Session(Guid.NewGuid(), EndedAt, release: true)));

        _factory.Push.Sent.Where(n => n.Data is { } d && d.TryGetValue("licenseId", out var l) && l == licenseId.ToString())
            .Should().ContainSingle().Which.Data!["type"].Should().Be("session-started");
    }

    // ── Doğrulama ─────────────────────────────────────────────────────────────

    /// <summary>v2 öğe alanları kolon sınırını ya da anlamını aşarsa görünür 400 (sessiz kırpma ya da 500 değil);
    /// hiçbir şey yazılmaz.</summary>
    [Theory]
    [InlineData(129, 0)]
    [InlineData(10, -1)]
    public async Task Gecersiz_v2_oge_alani_400(int machineNameLength, int unsent)
    {
        var (client, licenseId) = await SetupAsync();
        var sessionId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/sessions/sync",
            V2Body(Guid.NewGuid(), V2Session(sessionId, EndedAt, machineName: new string('p', machineNameLength), unsent: unsent)));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid-session");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.StreamSessions.AnyAsync(s => s.Id == sessionId)).Should().BeFalse();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record SessionEcho(
        Guid Id,
        string? Title,
        DateTimeOffset? EndedAt,
        DateTimeOffset UpdatedAt,
        Guid? OwnerReplicaId,
        DateTimeOffset? ReleasedAt,
        int Protocol,
        string? Status,
        string? Reason);

    private static object V1Session(Guid id, DateTimeOffset? endedAt = null, string title = "Akşam yayını") =>
        new { id, title, startedAt = StartedAt, endedAt, platforms = "youtube", notes = (string?)null };

    private static object V2Session(
        Guid id,
        DateTimeOffset? endedAt = null,
        bool release = false,
        string title = "Akşam yayını",
        string? machineName = "PC-A",
        int? unsent = null) =>
        new
        {
            id, title, startedAt = StartedAt, endedAt, platforms = "youtube", notes = (string?)null,
            release, machineName, unsentLabelCount = unsent,
        };

    private static object V1Body(params object[] sessions) => new { sessions };

    private static object V2Body(Guid replicaId, params object[] sessions) =>
        new { sessions, replicaId, protocol = 2, seenMode = 0 };

    private static async Task<List<SessionEcho>> PushSessionsAsync(HttpClient client, Guid licenseId, object body)
    {
        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/sessions/sync", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<List<SessionEcho>>())!;
    }

    /// <summary>Basılmamış bir sipariş ekler (bildirim üretmez). <paramref name="replicaId"/> verilirse v2 zarfıyla.</summary>
    private static async Task<Guid> PushOrderAsync(HttpClient client, Guid licenseId, Guid sessionId, Guid? replicaId = null)
    {
        var orderId = Guid.NewGuid();
        var item = new
        {
            id = orderId, sessionId = (Guid?)sessionId, customerId = $"c-{Guid.NewGuid():N}", platform = "youtube",
            username = $"u-{Guid.NewGuid():N}", displayName = (string?)null, messageText = "A1", code = "A1",
            price = 100m, addedAt = StartedAt.AddMinutes(10), printedAt = (DateTimeOffset?)null,
            cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
            isShippingFee = false, isBackupPromoted = false, isTentativeBackup = false,
        };
        object body = replicaId is { } r
            ? new { orders = new[] { item }, replicaId = r, protocol = 2, seenMode = 0 }
            : new { orders = new[] { item } };
        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/orders/sync", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return orderId;
    }

    private async Task<StreamSession> SessionAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StreamSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
    }

    private async Task<Order> OrderAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task SeedReplicaAsync(
        Guid licenseId,
        Guid replicaId,
        DateTimeOffset? firstSeenAt = null,
        DateTimeOffset? lastSeenAt = null,
        Guid? previousReplicaId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var first = firstSeenAt ?? DateTimeOffset.UtcNow.AddDays(-3);
        db.WpfReplicas.Add(new WpfReplica
        {
            LicenseId = licenseId,
            ReplicaId = replicaId,
            Protocol = 2,
            FirstSeenAt = first,
            LastSeenAt = lastSeenAt ?? first,
            PreviousReplicaId = previousReplicaId,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedSessionAsync(
        Guid licenseId,
        Guid? owner,
        string? machineName,
        DateTimeOffset? endedAt = null,
        DateTimeOffset? releasedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Akşam yayını",
            StartedAt = StartedAt,
            EndedAt = endedAt,
            Platforms = "youtube",
            UpdatedAt = StartedAt,
            OwnerReplicaId = owner,
            OwnerMachineName = machineName,
            ReleasedAt = releasedAt,
        };
        db.StreamSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync(SalesSyncMode mode = SalesSyncMode.Off)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-SAHIP-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? DateTimeOffset.UtcNow.AddDays(-1) : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    /// <summary><see cref="ApiFactory"/> + Information ve üstü günlük satırlarının toplayıcısı.</summary>
    public sealed class LogApiFactory : ApiFactory
    {
        public MessageRecorder Log { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(l => l.AddProvider(Log));
        }
    }

    public sealed class MessageRecorder : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void Dispose() { }

        private sealed class Recorder(MessageRecorder owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Information) owner._messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
