using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-sync force-end</c> (spec 2026-10-13 §2 "Devir" b, §4.10, §4.1, Y7, Y8, §12 risk 4, §15-43) gerçek SQL
/// Server'da. Sahibi hiç dönmeyecek CANLI ya da DEVREDİLİYOR yayını Burak bakarak kapatır; sessizlik şartı yok. Canlı
/// yayında <c>EndedAt</c> devir anıyla aynı yazılır; devrediliyor yayında <c>EndedAt</c> sahibin kapanış anı olarak
/// kalır — <see cref="SalesReplicaService.PhaseAtForceEnd"/> hâli bu iki alandan okur (K5 istisnası i, §8.7). İkisinde
/// de <c>ForceEndedBy = cli</c> ve yayının bütün siparişlerine devir anı basılır (DM3). Devredilmiş yayın etkisizdir (0);
/// bilinmeyen yayın 2. Bitirme koşullu bir UPDATE'tir: sahibin kapanışı okuma ile yazma arasına girerse hiçbir şey
/// yazılmaz, 1 döner; CLI'dan sonra gelen kapanış <c>EndedAt</c>'i ezmez ve ikinci bir devir yazmaz.
/// </summary>
public sealed partial class SalesSyncRelationalTests
{
    [Fact]
    public async Task ForceEnd_kuru_kosu_hicbir_seye_yazmaz_hali_sahibi_ve_sayilari_gosterir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ownerLastSeen = Seconds(DateTimeOffset.UtcNow.AddHours(-3));
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, ownerLastSeen);
        var live = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.SeedOrdersAsync(live, 2);
        var handingOver = await kit.SeedSessionAsync(Kit.Phase.HandingOver, unsentLabelCount: 3);
        await kit.SeedOrdersAsync(handingOver, 4);
        var legacy = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.WithDbAsync(async db =>
        {
            var row = await db.StreamSessions.FindAsync(legacy);
            row!.OwnerReplicaId = null;
        });
        var before = await TableFingerprintsAsync();

        var (exit, output, error) = await ForceEndAsync(new SalesSync.ForceEndArgs(live, Apply: false));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("KURU ÇALIŞTIRMA").And.Contain($"yayın {live}");
        output.Should().Contain($"Lisans: {kit.LicenseId}, mod: On");
        output.Should().Contain("Hâl: canlı");
        output.Should().Contain($"Sahip kopya: {kit.OwnerReplicaId}, sahibin son sinyali: {U(ownerLastSeen)}");
        output.Should().Contain("Gönderilmemiş etiket (sahibin bildirdiği): bildirilmedi");
        output.Should().Contain("Sipariş: 2");
        output.Should().Contain("Yapılacak: EndedAt = ReleasedAt = şimdi");
        output.Should().Contain("hiçbir şey yazılmadı");
        output.Should().NotContain("PC-A", "çıktıda makine adı yok");

        (exit, output, error) = await ForceEndAsync(new SalesSync.ForceEndArgs(handingOver, Apply: false));
        exit.Should().Be(0, error);
        output.Should().Contain("Hâl: devrediliyor");
        output.Should().Contain($"bitiş (EndedAt)={U(Kit.StartedAt.AddHours(2))}");
        output.Should().Contain("Gönderilmemiş etiket (sahibin bildirdiği): 3");
        output.Should().Contain("Sipariş: 4");
        output.Should().Contain("Yapılacak: EndedAt değişmez");
        output.Should().NotContain("PC-A");

        (exit, output, error) = await ForceEndAsync(new SalesSync.ForceEndArgs(legacy, Apply: false));
        exit.Should().Be(0, error);
        output.Should().Contain("Sahip kopya: yok (miras)");

        (await TableFingerprintsAsync()).Should().Equal(before, "kuru koşu hiçbir satırı, rowversion dahil, değiştirmez");
    }

    /// <summary>Canlı yayın: <c>EndedAt</c> = <c>ReleasedAt</c> = şimdi, <c>ForceEndedBy = cli</c>, bütün siparişlere devir
    /// anı; sürümlere dokunulmaz. Sessizlik şartı yok: sahip bir dakika önce görülmüş olsa da Burak bakarak kapatır.
    /// Hâl canlı okunur (Y7).</summary>
    [Fact]
    public async Task ForceEnd_canli_yayini_bitirir_EndedAt_devir_anina_esit()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-1));
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        var orders = await kit.SeedOrdersAsync(session, 3);
        var other = await kit.SeedSessionAsync(Kit.Phase.Live);
        var otherOrder = await kit.SeedOrderAsync(other);
        var started = DateTimeOffset.UtcNow;

        var (exit, output, error) = await ForceEndAsync(new SalesSync.ForceEndArgs(session, Apply: true));

        exit.Should().Be(0, error);
        var saved = await kit.SessionAsync(session);
        saved.EndedAt.Should().NotBeNull().And.BeOnOrAfter(started.AddSeconds(-1));
        saved.ReleasedAt.Should().Be(saved.EndedAt, "canlı yayının zorla bitirilmesi devri aynı anla yazar");
        saved.ForceEndedBy.Should().Be(StreamSession.ForceEndedByCli);
        saved.UpdatedAt.Should().Be(saved.EndedAt!.Value);
        SalesReplicaService.PhaseAtForceEnd(saved.EndedAt, saved.ReleasedAt).Should().Be(TakeoverPhase.Live);
        foreach (var order in orders)
        {
            var row = await kit.OrderAsync(order.Id);
            row.SessionReleasedAt.Should().Be(saved.ReleasedAt);
            row.SyncVersion.Should().Be(order.SyncVersion, "devir birimlere ve sürüme dokunmaz");
        }
        output.Should().Contain("UYGULAMA").And.Contain(
            $"UYGULANDI: EndedAt={U(saved.EndedAt)}, ReleasedAt={U(saved.ReleasedAt)}, ForceEndedBy=cli; "
            + "3 siparişe devir anı basıldı");

        (await kit.SessionAsync(other)).Should().BeEquivalentTo(
            new { EndedAt = (DateTimeOffset?)null, ReleasedAt = (DateTimeOffset?)null, ForceEndedBy = (string?)null },
            "başka yayına dokunulmaz");
        (await kit.OrderAsync(otherOrder.Id)).SessionReleasedAt.Should().BeNull();
    }

    /// <summary>Devrediliyor yayın (Y8, §15-43): <c>EndedAt</c> sahibin kapanış anı olarak kalır ve devir anından farklıdır;
    /// <c>ForceEndedBy = cli</c>, siparişlere devir anı. Hâl devrediliyor okunur: başka makineye geri yüklenen kopya bu
    /// yayını hiç devralmaz.</summary>
    [Fact]
    public async Task ForceEnd_devrediliyor_yayinda_EndedAt_sahibinki_kalir_hal_devrediliyor()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddDays(-2));
        var session = await kit.SeedSessionAsync(Kit.Phase.HandingOver, unsentLabelCount: 5);
        var orders = await kit.SeedOrdersAsync(session, 2);
        var ownerEndedAt = Kit.StartedAt.AddHours(2);

        var (exit, output, error) = await ForceEndAsync(new SalesSync.ForceEndArgs(session, Apply: true));

        exit.Should().Be(0, error);
        var saved = await kit.SessionAsync(session);
        saved.EndedAt.Should().Be(ownerEndedAt, "devrediliyor yayında EndedAt sahibin kapanış anıdır");
        saved.ReleasedAt.Should().NotBeNull().And.NotBe(saved.EndedAt);
        saved.ForceEndedBy.Should().Be(StreamSession.ForceEndedByCli);
        saved.UnsentLabelCount.Should().Be(5);
        saved.UpdatedAt.Should().Be(Kit.StartedAt, "yalnız ForceEndedBy ve devir yazılır");
        SalesReplicaService.PhaseAtForceEnd(saved.EndedAt, saved.ReleasedAt).Should().Be(TakeoverPhase.HandingOver);
        foreach (var order in orders)
            (await kit.OrderAsync(order.Id)).SessionReleasedAt.Should().Be(saved.ReleasedAt);
        output.Should().Contain($"UYGULANDI: EndedAt={U(ownerEndedAt)}, ReleasedAt={U(saved.ReleasedAt)}, ForceEndedBy=cli");
    }

    /// <summary>Devredilmiş yayın: kuru koşuda da <c>--apply</c>'da da etkisiz, 0 — hiçbir şey yazılmaz. Bilinmeyen yayın
    /// (yazım hatası) 2, hiçbir şey yazılmaz.</summary>
    [Fact]
    public async Task ForceEnd_devredilmis_yayinda_etkisiz_bilinmeyen_yayin_2()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released, forceEndedBy: kit.ReplicaId.ToString());
        await kit.SeedOrderAsync(session);
        var before = await TableFingerprintsAsync();

        foreach (var apply in new[] { false, true })
        {
            var (exit, output, error) = await ForceEndAsync(new SalesSync.ForceEndArgs(session, apply));
            exit.Should().Be(0, error);
            output.Should().Contain("Hâl: devredilmiş");
            output.Should().Contain($"zorla bitiren={kit.ReplicaId}");
            output.Should().Contain("Yayın zaten devredilmiş");
        }

        var unknown = await ForceEndAsync(new SalesSync.ForceEndArgs(Guid.NewGuid(), Apply: true));
        unknown.Exit.Should().Be(2);
        unknown.Error.Should().Contain("Yayın bulunamadı");
        unknown.Output.Should().BeEmpty();

        (await TableFingerprintsAsync()).Should().Equal(before);
    }

    /// <summary>
    /// Sahibin kapanışı (<c>sessions/sync</c>) CLI'ın okuması ile yazması arasına girer ve commit olur: koşullu UPDATE
    /// satırı canlı bulmaz, hiçbir şey yazılmaz, yeni hâl basılır ve 1 döner — <c>EndedAt</c> sahibinki, tek devir
    /// (varsa) sahibinki. Yeniden çalıştırınca: sahip devrettiyse etkisiz (0); etiketleri sahipte bekliyorsa devrediliyor
    /// yayın olarak kapanır, <c>EndedAt</c> yine sahibinki (tekdüze) ve hâl devrediliyor.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceEnd_yazimindan_once_sahibin_kapanisi_gelirse_hicbir_sey_yazmaz_1_doner(bool release)
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddHours(-1));
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        var order = await kit.SeedOrderAsync(session);
        var ownerEndedAt = Kit.StartedAt.AddHours(2);
        HttpResponseMessage? close = null;
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[ForceEndedBy]", StringComparison.Ordinal),
            before: async () => close = await kit.SendToAsync(
                kit.SessionsUrl, kit.OwnerClose(session, ownerEndedAt, release, release ? null : 2)));
        var output = new StringWriter();
        var error = new StringWriter();
        int exit;
        await using (var db = HookedDb(hook))
            exit = await SalesSync.RunAsync(db, new SalesSync.ForceEndArgs(session, Apply: true), output, error, default);

        close.Should().NotBeNull("kanca kapanışı CLI'ın yazımından önce koşturmalı");
        close!.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());
        exit.Should().Be(1);
        error.ToString().Should().Contain(release ? "şimdi devredilmiş" : "şimdi devrediliyor")
            .And.Contain("yeniden çalıştır");
        var saved = await kit.SessionAsync(session);
        saved.EndedAt.Should().Be(ownerEndedAt, "ilk commit eden sahibin kapanışı");
        saved.ForceEndedBy.Should().BeNull("CLI hiçbir şey yazmadı");
        if (release)
        {
            saved.ReleasedAt.Should().NotBeNull();
            (await kit.OrderAsync(order.Id)).SessionReleasedAt.Should().Be(saved.ReleasedAt, "tek devir anı");
        }
        else
        {
            saved.ReleasedAt.Should().BeNull();
            (await kit.OrderAsync(order.Id)).SessionReleasedAt.Should().BeNull();
        }

        var rerun = await ForceEndAsync(new SalesSync.ForceEndArgs(session, Apply: true));
        rerun.Exit.Should().Be(0, rerun.Error);
        var final = await kit.SessionAsync(session);
        final.EndedAt.Should().Be(ownerEndedAt, "EndedAt tekdüze");
        if (release)
        {
            rerun.Output.Should().Contain("Yayın zaten devredilmiş");
            final.Should().BeEquivalentTo(saved, o => o.Excluding(s => s.License), "etkisiz yeniden koşu");
        }
        else
        {
            final.ForceEndedBy.Should().Be(StreamSession.ForceEndedByCli);
            final.ReleasedAt.Should().NotBeNull().And.NotBe(final.EndedAt);
            SalesReplicaService.PhaseAtForceEnd(final.EndedAt, final.ReleasedAt).Should().Be(TakeoverPhase.HandingOver);
            (await kit.OrderAsync(order.Id)).SessionReleasedAt.Should().Be(final.ReleasedAt);
        }
    }

    /// <summary>
    /// CLI canlı yayını bitirdikten sonra sahibin kapanışı gelir (devretmek istesin ya da etiketleri beklesin): kapanış
    /// <c>EndedAt</c>'i yalnız boşsa yazar (§4.1 tekdüzelik) ve ikinci bir devir yazmaz — <c>EndedAt</c> = <c>ReleasedAt</c>
    /// CLI'ınki kalır, hâl canlı okunmaya devam eder, siparişin devir anı değişmez.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceEnd_sonrasi_sahibin_kapanisi_EndedAti_ezmez_ikinci_devir_yazmaz(bool release)
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddHours(-1));
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        var order = await kit.SeedOrderAsync(session);
        (await ForceEndAsync(new SalesSync.ForceEndArgs(session, Apply: true))).Exit.Should().Be(0);
        var ended = await kit.SessionAsync(session);

        var close = await kit.SendToAsync(
            kit.SessionsUrl, kit.OwnerClose(session, Kit.StartedAt.AddHours(2), release, release ? null : 2));

        close.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());
        var saved = await kit.SessionAsync(session);
        saved.Should().BeEquivalentTo(new
        {
            ended.EndedAt,
            ended.ReleasedAt,
            ForceEndedBy = StreamSession.ForceEndedByCli,
        }, "ilk commit eden CLI; kapanış EndedAt'i ezmez, devir bir kez yazılır");
        saved.ReleasedAt.Should().Be(saved.EndedAt);
        SalesReplicaService.PhaseAtForceEnd(saved.EndedAt, saved.ReleasedAt).Should().Be(TakeoverPhase.Live);
        (await kit.OrderAsync(order.Id)).SessionReleasedAt.Should().Be(ended.ReleasedAt, "tek devir anı");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private async Task<(int Exit, string Output, string Error)> ForceEndAsync(SalesSync.ForceEndArgs args)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesSync.RunAsync(db, args, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }
}
