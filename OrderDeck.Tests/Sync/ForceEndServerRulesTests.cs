using System.Text.Json;
using FluentAssertions;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Sahte sunucunun <c>force-end</c> komutu (W2-09; spec 2026-10-13 §4.6, Y7, Y8, M18) — tel üzerinden, sunucunun
/// <c>ForceEndCommandHandler</c>'ıyla aynı kurallar: yalnız canlı yayın, sahibin sessizliği sunucunun eşiğiyle
/// (<c>ForceEndSilenceMinutes</c>, sabit 15 değil), devrediliyor yayında ne kadar sessiz olursa olsun
/// <c>handing-over</c>, devredilmişte etkisiz başarı. Çip ve onay şeridinin testleri bu ikize güveniyor.
/// </summary>
public sealed class ForceEndServerRulesTests : IDisposable
{
    private static readonly DateTimeOffset T0 = SalesEditTestBed.T0;

    private readonly SalesEditTestBed _bed = new();
    private readonly Guid _owner = Guid.NewGuid();

    public void Dispose() => _bed.Dispose();

    private FakeSalesServer Server => _bed.Server;

    private Guid Me => _bed.Envelope.ReplicaId!.Value;

    private Guid LiveSession(Guid? owner, int? unsent = null, bool ended = false)
    {
        var id = Guid.NewGuid();
        Server.SeedSession(id, T0, ended ? T0.AddHours(1) : null, owner: owner, ownerMachineName: "PC-A");
        if (unsent is not null) Server.SetUnsentLabelCount(id, unsent.Value);
        return id;
    }

    private Task<SalesCommandHttpResult> SendAsync(Guid sessionId, Guid? commandId = null)
    {
        using var doc = JsonDocument.Parse(new SalesForceEndPayload(sessionId).ToWireJson());
        return _bed.Api.PostSalesCommandAsync(Server.LicenseId, new SalesCommandRequest(commandId ?? Guid.NewGuid(), Me, 2,
            1, SalesCommandKinds.ForceEnd, doc.RootElement.Clone(), []));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(20)]
    public async Task Canli_yayin_sahibi_esik_kadar_sessizse_bitirilir_ve_devredilir(int threshold)
    {
        Server.ForceEndSilenceMinutes = threshold;
        var session = LiveSession(_owner);
        var order = Guid.NewGuid();
        Server.SeedOrder(new FakeSalesServer.OrderBody(order, session, Guid.NewGuid().ToString("N"), "tiktok",
            "alici_ornek", null, "aldım", "A1", 100m, T0.AddMinutes(1), null, null, null, false, false, false, null,
            null, null, null, null, null, null, null, null), version: 1, origin: _owner);
        Server.SetReplicaLastSeen(_owner, DateTimeOffset.UtcNow.AddMinutes(-threshold).AddSeconds(-5));

        var http = await SendAsync(session);

        http.Transport.Should().Be(SalesCommandTransport.Ok, string.Join(" | ", Server.Faults));
        http.Response!.Status.Should().Be(SalesCommandResponse.StatusApplied);
        http.Response.Result.AlreadyReleased.Should().NotBe(true);
        var row = Server.Session(session)!;
        row.EndedAt.Should().NotBeNull();
        row.ReleasedAt.Should().Be(row.EndedAt, "bitiş ve devir aynı işlemde, aynı an");
        row.ForceEndedBy.Should().Be(Me.ToString());
        Server.Order(order)!.SessionReleasedAt.Should().Be(row.ReleasedAt, "devir siparişleri akışa sokar (DM3)");
        http.Response.Sessions.Should().ContainSingle(s => s.Id == session && s.ReleasedAt != null);
        Server.Faults.Should().BeEmpty();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(20)]
    public async Task Sahibi_esikten_yeni_sinyal_verdiyse_owner_active_ve_son_sinyal(int threshold)
    {
        Server.ForceEndSilenceMinutes = threshold;
        var session = LiveSession(_owner);
        var lastSeen = DateTimeOffset.UtcNow.AddMinutes(-threshold).AddSeconds(30);
        Server.SetReplicaLastSeen(_owner, lastSeen);

        var http = await SendAsync(session);

        http.Transport.Should().Be(SalesCommandTransport.Problem);
        http.Problem!.Code.Should().Be(SalesCommandProblemCodes.OwnerActive);
        http.Problem.Machine.Should().Be("PC-A");
        http.Problem.LastSeenAt.Should().Be(lastSeen);
        Server.Session(session)!.EndedAt.Should().BeNull("hiçbir şey yazılmaz");
    }

    [Fact]
    public async Task Devrediliyor_yayin_sahip_saatlerdir_sessiz_olsa_da_handing_over()
    {
        Server.ForceEndSilenceMinutes = 7;
        var session = LiveSession(_owner, unsent: 4, ended: true);
        var lastSeen = DateTimeOffset.UtcNow.AddHours(-5);
        Server.SetReplicaLastSeen(_owner, lastSeen);

        var http = await SendAsync(session);

        http.Transport.Should().Be(SalesCommandTransport.Problem);
        http.Problem!.Code.Should().Be(SalesCommandProblemCodes.HandingOver, "Y8: devrediliyor yayın devralınamaz");
        http.Problem.UnsentLabelCount.Should().Be(4);
        http.Problem.Machine.Should().Be("PC-A");
        http.Problem.OwnerLastSeenAt.Should().Be(lastSeen);
        Server.Session(session)!.ReleasedAt.Should().BeNull();
    }

    [Fact]
    public async Task Sahipsiz_miras_canli_yayin_owner_active_bos_son_sinyalle()
    {
        var session = LiveSession(owner: null);

        var http = await SendAsync(session);

        http.Problem!.Code.Should().Be(SalesCommandProblemCodes.OwnerActive);
        http.Problem.LastSeenAt.Should().BeNull();
        Server.Session(session)!.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task Devredilmis_yayin_etkisiz_basari_devir_ani_degismez_yeniden_oynatma_ayni_sonuc()
    {
        var session = LiveSession(_owner);
        Server.ReleaseSession(session);
        var before = Server.Session(session)!;
        var commandId = Guid.NewGuid();

        var first = await SendAsync(session, commandId);
        var replay = await SendAsync(session, commandId);

        first.Response!.Status.Should().Be(SalesCommandResponse.StatusUnchanged);
        first.Response.Result.AlreadyReleased.Should().BeTrue();
        Server.Session(session)!.Should().Be(before, "devir anı ve ForceEndedBy değişmez");
        replay.Response!.Status.Should().Be(SalesCommandResponse.StatusUnchanged);
        replay.Response.Sessions.Should().ContainSingle(s => s.Id == session);
        Server.CommandLog(commandId)!.Replays.Should().Be(1);
    }

    [Fact]
    public async Task Sahibin_kopya_satiri_yoksa_sessiz_sayilir()
    {
        var session = LiveSession(_owner);

        var http = await SendAsync(session);

        http.Transport.Should().Be(SalesCommandTransport.Ok);
        Server.Session(session)!.ReleasedAt.Should().NotBeNull();
    }
}
