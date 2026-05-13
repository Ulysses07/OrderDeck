using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>
/// PR siparis-sync (2026-05-13): SessionOrderSyncService outbox push
/// integration tests.
/// </summary>
public sealed class SessionOrderSyncServiceTests
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = "TEST-KEY-001";
    }

    private sealed class FakeClock : IClock
    {
        private long _now = 1_700_000_000L;
        public long UnixNow() => _now;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(_now);
    }

    private sealed record Fx(SessionOrderSyncService Svc, SessionRepository Sessions,
        LabelRepository Labels, InMemorySqlite Db);

    private static Fx Build()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);

        new CustomerRepository(db).Insert(
            new Customer("c1hex", "instagram", "@alice", "Alice", null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));

        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200,
                    "[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"licenseKey\":\"TEST-KEY-001\"}]");
            if (path.EndsWith("/sessions/sync") || path.EndsWith("/orders/sync"))
                return FakeHttpMessageHandler.Json(200, "[]");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());

        var svc = new SessionOrderSyncService(api, sessions, labels,
            new FakeLicenseProvider(), new FakeClock(),
            NullLogger<SessionOrderSyncService>.Instance);

        return new Fx(svc, sessions, labels, db);
    }

    [Fact]
    public async Task SyncOnceAsync_pushes_unsynced_session_and_marks()
    {
        var fx = Build();
        using var _d = fx.Db;
        var sid = Guid.NewGuid().ToString("N");
        fx.Sessions.Insert(new StreamSession(sid, "Yayın 1", 1700000100L, null,
            new[] { "instagram", "youtube" }, null));

        var result = await fx.Svc.SyncOnceAsync();

        result.SessionsPushed.Should().Be(1);
        fx.Sessions.GetUnsynced().Should().BeEmpty();
        fx.Sessions.GetById(sid)!.SyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncOnceAsync_pushes_unsynced_label_and_marks()
    {
        var fx = Build();
        using var _d = fx.Db;
        var sid = Guid.NewGuid().ToString("N");
        fx.Sessions.Insert(new StreamSession(sid, "S1", 1700000000L, null,
            new[] { "instagram" }, null));

        var lid = Guid.NewGuid().ToString("N");
        fx.Labels.Insert(new Label(lid, sid, "c1hex", "instagram", "@alice",
            "ürün", null, 250m, 1700000200L, null, DisplayName: "Alice"));

        var result = await fx.Svc.SyncOnceAsync();

        result.SessionsPushed.Should().Be(1);
        result.OrdersPushed.Should().Be(1);
        fx.Labels.GetUnsynced().Should().BeEmpty();
        fx.Labels.GetById(lid)!.SyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncOnceAsync_state_change_resets_SyncedAt_for_resync()
    {
        var fx = Build();
        using var _d = fx.Db;
        var sid = Guid.NewGuid().ToString("N");
        fx.Sessions.Insert(new StreamSession(sid, null, 1700000000L, null,
            new[] { "instagram" }, null));

        var lid = Guid.NewGuid().ToString("N");
        fx.Labels.Insert(new Label(lid, sid, "c1hex", "instagram", "@alice",
            "ürün", null, 100m, 1700000200L, null, DisplayName: "Alice"));

        await fx.Svc.SyncOnceAsync();
        fx.Labels.GetById(lid)!.SyncedAt.Should().NotBeNull();

        fx.Labels.MarkPrinted(new[] { lid }, 1700000300L);
        fx.Labels.GetById(lid)!.SyncedAt.Should().BeNull();

        var r2 = await fx.Svc.SyncOnceAsync();
        r2.OrdersPushed.Should().Be(1);
    }

    [Fact]
    public async Task End_session_resets_SyncedAt_for_resync()
    {
        var fx = Build();
        using var _d = fx.Db;
        var sid = Guid.NewGuid().ToString("N");
        fx.Sessions.Insert(new StreamSession(sid, null, 1700000000L, null,
            new[] { "instagram" }, null));

        await fx.Svc.SyncOnceAsync();
        fx.Sessions.GetById(sid)!.SyncedAt.Should().NotBeNull();

        fx.Sessions.End(sid, 1700001000L);
        fx.Sessions.GetById(sid)!.SyncedAt.Should().BeNull();

        var r2 = await fx.Svc.SyncOnceAsync();
        r2.SessionsPushed.Should().Be(1);
    }
}
