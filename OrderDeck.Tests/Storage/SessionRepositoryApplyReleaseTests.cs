using Dapper;
using FluentAssertions;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// <see cref="SessionRepository.ApplyRelease"/> (spec 2026-10-13 §5.2, §2 "Yerel otorite", §5.4, §8.7; W1-07):
/// eski sunucu bugünkü gibi onaylanır; devir anı ve onay tek işlemde, F05 koşuluyla; <c>Authority = 'server'</c>
/// yalnız mod On ∧ yerelde bitmiş oturumda; aktif oturuma gelen devir anı yalnız saklanır; bitmiş ama devredilmemiş
/// oturum bekletilir; sahip olmayanın kapanışı tek iz bırakan son durumdur. Ayrıca oturum satırının yeni alanları.
/// </summary>
public sealed class SessionRepositoryApplyReleaseTests : IDisposable
{
    private const long Now = 1_760_005_000L;
    private const long ReleasedAt = 1_760_004_000L;

    private readonly InMemorySqlite _db = new();
    private readonly SessionRepository _sessions;

    public SessionRepositoryApplyReleaseTests()
    {
        new MigrationRunner(_db).Run();
        _sessions = new SessionRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private static readonly SessionPushResult Applied = new(2, null, "applied", null);

    private static SessionPushResult ReleasedResult(long at = ReleasedAt) => new(2, at, "applied", null);

    private string NewSession(long? endedAt)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(id, "Örnek yayın", 1_760_000_000L, endedAt, new[] { "tiktok" }, null));
        return id;
    }

    private void SetMode(int mode)
    {
        using var c = _db.Open();
        c.Execute("UPDATE SalesSyncState SET Mode = @mode WHERE Id = 1", new { mode });
    }

    private int ConflictRows()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM SalesConflictLog");
    }

    private StreamSession Row(string id) => _sessions.GetById(id)!;

    // ── eski sunucu ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Protokol_2_degilse_bugunku_gibi_onaylanir_bitmis_oturum_dahil(int? protocol)
    {
        var id = NewSession(endedAt: 1_760_000_900L);
        var revision = Row(id).Revision;

        _sessions.ApplyRelease(id, revision, new SessionPushResult(protocol, ReleasedAt, "applied", null), Now)
            .Should().Be(SessionAckOutcome.Synced);

        Row(id).SyncedAt.Should().Be(Now);
        Row(id).ReleasedAt.Should().BeNull("protokolsüz yanıtın alanlarına güvenilmez");
    }

    [Fact]
    public void Eski_sunucu_onayi_da_F05_kosuluyla_yazilir()
    {
        var id = NewSession(endedAt: null);
        var revision = Row(id).Revision;
        _sessions.End(id, 1_760_000_900L);                                       // uçuşta değişti

        _sessions.ApplyRelease(id, revision, SessionPushResult.Legacy, Now).Should().Be(SessionAckOutcome.Stale);
        Row(id).SyncedAt.Should().BeNull();
    }

    // ── devir anı ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, StreamSessionAuthority.Local)]
    [InlineData(1, StreamSessionAuthority.Server)]
    public void Bitmis_oturuma_devir_ani_ve_onay_yazilir_Authority_yalniz_mod_Onda_server(int mode, string authority)
    {
        SetMode(mode);
        var id = NewSession(endedAt: 1_760_000_900L);
        var before = Row(id);

        _sessions.ApplyRelease(id, before.Revision, ReleasedResult(), Now).Should().Be(SessionAckOutcome.Released);

        var after = Row(id);
        after.ReleasedAt.Should().Be(ReleasedAt);
        after.SyncedAt.Should().Be(Now);
        after.Authority.Should().Be(authority);
        after.Revision.Should().Be(before.Revision, "devir yazımı kuyruğa almaz (tetikleyici listesi dışında)");
        after.EndedAt.Should().Be(1_760_000_900L);
        _sessions.HasUnsynced().Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Aktif_oturuma_gelen_devir_ani_yalniz_saklanir_yayin_acik_kalir(int mode)
    {
        SetMode(mode);
        var id = NewSession(endedAt: null);

        _sessions.ApplyRelease(id, Row(id).Revision, ReleasedResult(), Now).Should().Be(SessionAckOutcome.Released);

        var after = Row(id);
        after.ReleasedAt.Should().Be(ReleasedAt);
        after.EndedAt.Should().BeNull("force-end aktif yayını kesmez (M18, §5.4)");
        after.Authority.Should().Be(StreamSessionAuthority.Local);
        after.SyncedAt.Should().Be(Now, "normal onay");
        _sessions.GetActive()!.Id.Should().Be(id);
    }

    [Fact]
    public void Ucusta_degisen_oturumda_devir_ani_saklanir_onay_yazilmaz()
    {
        var id = NewSession(endedAt: null);
        var revision = Row(id).Revision;
        _sessions.End(id, 1_760_000_900L);

        _sessions.ApplyRelease(id, revision, ReleasedResult(), Now).Should().Be(SessionAckOutcome.Stale);

        var after = Row(id);
        after.ReleasedAt.Should().Be(ReleasedAt, "sunucunun olgusu");
        after.SyncedAt.Should().BeNull("kapanış henüz gitmedi: yeniden gönderilir");
    }

    [Fact]
    public void Devir_ani_yazimi_SyncApplyGuard_birakmaz()
    {
        var id = NewSession(endedAt: 1_760_000_900L);

        _sessions.ApplyRelease(id, Row(id).Revision, ReleasedResult(), Now);

        using var c = _db.Open();
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
    }

    // ── devir yok ─────────────────────────────────────────────────────────

    [Fact]
    public void Bitmis_ama_devredilmemis_oturum_bekletilir_hicbir_sey_yazilmaz()
    {
        var id = NewSession(endedAt: 1_760_000_900L);
        var before = Row(id);

        _sessions.ApplyRelease(id, before.Revision, Applied, Now).Should().Be(SessionAckOutcome.Held);

        Row(id).Should().BeEquivalentTo(before, "onay yazılmaz, kuyrukta kalır (M2)");
        _sessions.GetUnsynced().Select(s => s.Id).Should().Contain(id);
    }

    [Fact]
    public void Bekletilen_oturum_ucusta_degistiyse_bayat_sayilir()
    {
        var id = NewSession(endedAt: null);
        var revision = Row(id).Revision;
        _sessions.End(id, 1_760_000_900L);

        _sessions.ApplyRelease(id, revision, Applied, Now).Should().Be(SessionAckOutcome.Stale,
            "gönderilen satır aktifti; yeniden gidecek kapanış 'gönderilen kayıt' sayılır");
    }

    [Fact]
    public void Aktif_oturum_devir_beklemeden_onaylanir()
    {
        var id = NewSession(endedAt: null);

        _sessions.ApplyRelease(id, Row(id).Revision, Applied, Now).Should().Be(SessionAckOutcome.Synced);
        Row(id).SyncedAt.Should().Be(Now);
    }

    // ── sahip olmayanın kapanışı ──────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Sahip_olmayanin_kapanisi_tek_iz_ve_onay_Authority_dokunulmaz(int mode)
    {
        SetMode(mode);
        var id = NewSession(endedAt: 1_760_000_900L);

        _sessions.ApplyRelease(id, Row(id).Revision,
                new SessionPushResult(2, null, "ignored", SalesConflictLog.SessionOwnedElsewhere), Now)
            .Should().Be(SessionAckOutcome.OwnedElsewhere);

        var after = Row(id);
        after.SyncedAt.Should().Be(Now, "son durum: yeniden gönderilmez");
        after.Authority.Should().Be(StreamSessionAuthority.Local);
        after.ReleasedAt.Should().BeNull();
        using var c = _db.Open();
        var row = c.QuerySingle<(long At, string? LabelId, string Kind, string? Reason)>(
            "SELECT At, LabelId, Kind, Reason FROM SalesConflictLog");
        row.Should().Be((Now, (string?)null, SalesConflictLog.SessionOwnedElsewhere, (string?)$"session:{id}"));
    }

    [Fact]
    public void Baska_nedenle_yok_sayilan_bitmis_oturum_bekletilir_iz_yazilmaz()
    {
        var id = NewSession(endedAt: 1_760_000_900L);

        _sessions.ApplyRelease(id, Row(id).Revision, new SessionPushResult(2, null, "ignored", "baska-neden"), Now)
            .Should().Be(SessionAckOutcome.Held);
        ConflictRows().Should().Be(0);
    }

    // ── satırın yeni alanları ─────────────────────────────────────────────

    [Fact]
    public void Oturum_satiri_ReleasedAt_ve_Authorityyi_tasir_her_okuyucuda()
    {
        var id = NewSession(endedAt: 1_760_000_900L);
        using (var c = _db.Open())
            c.Execute("UPDATE StreamSession SET ReleasedAt = @ReleasedAt, Authority = 'server' WHERE Id = @id",
                new { ReleasedAt, id });

        foreach (var row in new[]
                 {
                     _sessions.GetById(id)!, _sessions.GetUnsynced().Single(), _sessions.GetLatestEnded()!,
                     _sessions.GetAllEnded(10).Single(),
                 })
        {
            row.ReleasedAt.Should().Be(ReleasedAt);
            row.Authority.Should().Be(StreamSessionAuthority.Server);
        }

        var fresh = NewSession(endedAt: null);
        _sessions.GetActive()!.Should().Match<StreamSession>(s =>
            s.Id == fresh && s.ReleasedAt == null && s.Authority == StreamSessionAuthority.Local);
    }
}
