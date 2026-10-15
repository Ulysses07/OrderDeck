using System.Data.Common;
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>force-end</c> gerçek SQL Server'da — spec 2026-10-13 §10.1-11 (sahibin kapanışıyla yarış), §10.1-28 (devrediliyor
/// yayın), DM3 (devir siparişleri akışa sokar). Bitirme tek bir koşullu UPDATE'tir; yarışın hakemi o koşuldur ve InMemory
/// onu uygulamadığı için ancak burada kanıtlanır. Yarış sırayla kurulur: kanca, bir isteğin yazımı diske inmeden hemen
/// önce öbür isteği koşturur.
/// </summary>
public sealed partial class SalesCommandsRelationalTests
{
    /// <summary>
    /// §10.1-28 (force-end yarısı), Y8: devrediliyor yayın sahip bir saattir sessizken bile bitirilmez — 409
    /// <c>handing-over</c>, sahibin bildirdiği gönderilmemiş etiket sayısı, makine ve son sinyalle; hiçbir şey yazılmaz.
    /// </summary>
    [Fact]
    public async Task ForceEnd_devrediliyor_yayinda_409_handing_over_hicbir_sey_yazilmaz()
    {
        var session = await _kit.SeedSessionAsync(Kit.Phase.HandingOver, unsentLabelCount: 3);
        var lastSeen = DateTimeOffset.UtcNow.AddHours(-1);
        await _kit.SeedReplicaAsync(_kit.OwnerReplicaId, lastSeen);
        var order = await _kit.SeedOrderAsync(session);

        var resp = await _kit.SendAsync(_kit.ForceEnd(Guid.NewGuid(), session));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("handing-over");
        problem.GetProperty("unsentLabelCount").GetInt32().Should().Be(3);
        problem.GetProperty("machine").GetString().Should().Be("PC-A");
        problem.GetProperty("ownerLastSeenAt").GetDateTimeOffset().Should().Be(lastSeen);

        (await _kit.SessionAsync(session)).Should().BeEquivalentTo(new
        {
            EndedAt = (DateTimeOffset?)Kit.StartedAt.AddHours(2),
            ReleasedAt = (DateTimeOffset?)null,
            ForceEndedBy = (string?)null,
            UnsentLabelCount = (int?)3,
        });
        (await _kit.OrderAsync(order.Id)).SessionReleasedAt.Should().BeNull();
        (await _kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// DM3: zorla bitirilen yayın ve siparişleri akışa girer — devirden önce canlı yayın akışta yoktu (Y1). Siparişlerin
    /// seq'i yayınınkinden büyüktür (önce yayın, sonra siparişler); damga sürüme dokunmaz.
    /// </summary>
    [Fact]
    public async Task ForceEnd_yayini_ve_siparislerini_akisa_sokar()
    {
        var session = await _kit.SeedSessionAsync(Kit.Phase.Live);
        await _kit.SeedReplicaAsync(_kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-20));
        var orders = new[] { await _kit.SeedOrderAsync(session), await _kit.SeedOrderAsync(session) };
        var cursor = (await ChangesAsync(afterSeq: 0)).NextAfterSeq;

        var resp = await _kit.SendAsync(_kit.ForceEnd(Guid.NewGuid(), session));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await _kit.SessionAsync(session);
        var page = await ChangesAsync(cursor);
        var sessionItem = page.Items.Should().ContainSingle(i => i.Kind == "session").Subject;
        sessionItem.Session!.Should().BeEquivalentTo(new
        {
            Id = session, saved.EndedAt, saved.ReleasedAt, ForceEndedBy = _kit.ReplicaId.ToString(),
        });
        var orderItems = page.Items.Where(i => i.Kind == "order").ToList();
        orderItems.Select(i => i.Order!.Id).Should().BeEquivalentTo(orders.Select(o => o.Id));
        orderItems.Should().AllSatisfy(i =>
        {
            i.Order!.SessionReleasedAt.Should().Be(saved.ReleasedAt);
            i.Order.Version.Should().Be(1);
            i.ChangeSeq.Should().BeGreaterThan(sessionItem.ChangeSeq);
        });
    }

    /// <summary>
    /// §10.1-11 (bir yön): zorla bitirme sahibin sessizliğini okuduktan sonra, yazımı diske inmeden hemen önce sahibin
    /// kapanışı (<c>sessions/sync</c>) baştan sona koşar ve commit olur. İlk commit eden kapanıştır: zorla bitirmenin
    /// koşullu UPDATE'i satırı bitmiş bulur ve hiçbir şey yazmaz — <c>EndedAt</c> sahibinki kalır, <c>ForceEndedBy</c>
    /// yazılmaz. Kapanış devrettiyse (<c>Release = true</c>) zorla bitirme etkisiz başarı döner ve tek devir anı
    /// sahibinkidir; etiketleri sahipte bekliyorsa (<c>Release = false</c>) 409 <c>handing-over</c> (Y8).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceEnd_yazimindan_once_sahibin_kapanisi_commit_olursa_kapanis_kazanir(bool release)
    {
        var session = await _kit.SeedSessionAsync(Kit.Phase.Live);
        await _kit.SeedReplicaAsync(_kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-20));
        var order = await _kit.SeedOrderAsync(session);
        var ownerEndedAt = Kit.StartedAt.AddHours(2);
        HttpResponseMessage? close = null;
        _factory.NonQueries.Arm(
            when: sql => sql.Contains("[ForceEndedBy]"),
            before: async () => close = await _kit.SendToAsync(
                _kit.SessionsUrl, _kit.OwnerClose(session, ownerEndedAt, release, release ? null : 2)));

        var forceEnd = await _kit.SendAsync(_kit.ForceEnd(Guid.NewGuid(), session));

        close.Should().NotBeNull("kanca kapanışı zorla bitirmenin yazımından önce koşturmalı");
        close!.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());
        var saved = await _kit.SessionAsync(session);
        saved.EndedAt.Should().Be(ownerEndedAt, "ilk commit eden sahibin kapanışı; EndedAt tekdüze");
        saved.ForceEndedBy.Should().BeNull("zorla bitirme hiçbir şey yazmadı");
        if (release)
        {
            forceEnd.StatusCode.Should().Be(HttpStatusCode.OK, await forceEnd.Content.ReadAsStringAsync());
            var body = await Kit.ReadAsync(forceEnd);
            body.Status.Should().Be("unchanged");
            body.Result.GetProperty("alreadyReleased").GetBoolean().Should().BeTrue();
            body.Sessions.Should().ContainSingle().Which.ReleasedAt.Should().Be(saved.ReleasedAt);
            saved.ReleasedAt.Should().NotBeNull();
            (await _kit.OrderAsync(order.Id)).SessionReleasedAt.Should().Be(saved.ReleasedAt, "tek devir anı");
        }
        else
        {
            forceEnd.StatusCode.Should().Be(HttpStatusCode.Conflict, await forceEnd.Content.ReadAsStringAsync());
            var problem = await Kit.ProblemAsync(forceEnd);
            problem.GetProperty("title").GetString().Should().Be("handing-over");
            problem.GetProperty("unsentLabelCount").GetInt32().Should().Be(2);
            saved.ReleasedAt.Should().BeNull();
            (await _kit.OrderAsync(order.Id)).SessionReleasedAt.Should().BeNull();
            (await _kit.LogsAsync()).Should().BeEmpty();
        }
    }

    /// <summary>
    /// Okuyucusuz komut kancası (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>): <c>when</c>'i tutan ilk komutun hemen
    /// öncesinde <c>before</c>'u bir kez koşturur. Yeniden girmez (kanca gövdesinin komutları onu tetiklemez).
    /// </summary>
    private sealed class NonQueryHook : DbCommandInterceptor
    {
        private Func<string, bool>? _when;
        private Func<Task>? _before;
        private bool _running;

        public void Arm(Func<string, bool> when, Func<Task> before)
        {
            _when = when;
            _before = before;
        }

        public void Disarm() => _before = null;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_before is { } hook && !_running && _when!(command.CommandText))
            {
                _before = null;
                _running = true;
                try { await hook(); }
                finally { _running = false; }
            }
            return result;
        }
    }
}
