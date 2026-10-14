using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>GET sales/changes</c> — spec 2026-10-13 §4.4, §10.1-1, §10.1-2 (akış kısmı), gerçek SQL Server. rowversion ve
/// <c>MIN_ACTIVE_ROWVERSION()</c> yalnız burada var; InMemory'de ChangeSeq hep 0 ve ufuk sorgusu çalışmaz. Lisans
/// varsayılan kipte (Off): akış moddan bağımsız çalışır. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesChangesFeedRelationalTests : IAsyncLifetime
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddHours(2);

    private readonly SqlServerContainerFixture _sql;
    private readonly Guid _replicaId = Guid.NewGuid();
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _licenseId;

    public SalesChangesFeedRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
        _licenseId = await SeedLicenseAsync(customerId);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── §10.1-2: devir ve görünürlük ─────────────────────────────────────────

    /// <summary>
    /// Devirden sonra oturum ve bütün siparişleri akışta; siparişler oturumdan sonra gelir. Devirden sonraki ekleme
    /// de iner. Canlı ve devrediliyor yayınlar ile siparişleri hiç inmez (Y1); SessionId NULL sipariş hiç inmez.
    /// </summary>
    [Fact]
    public async Task Devredilen_yayin_ve_siparisleri_iner_canli_ve_devrediliyor_inmez()
    {
        var released = Guid.NewGuid();
        var live = Guid.NewGuid();
        var handingOver = Guid.NewGuid();
        await PushSessionAsync(released, endedAt: null, release: false);
        await PushSessionAsync(live, endedAt: null, release: false);
        await PushSessionAsync(handingOver, endedAt: null, release: false);
        var releasedOrders = new[] { await PushOrderAsync(released), await PushOrderAsync(released) };
        var liveOrder = await PushOrderAsync(live);
        var handingOverOrder = await PushOrderAsync(handingOver);
        var sessionless = await PushOrderAsync(sessionId: null, v2: false);
        await PushSessionAsync(handingOver, EndedAt, release: false);

        var before = await GetPageAsync(afterSeq: 0);
        before.Items.Should().BeEmpty("henüz devredilmiş yayın yok: canlı ve devrediliyor yayın başka PC'ye inmez");
        before.NextAfterSeq.Should().Be(0);

        await PushSessionAsync(released, EndedAt, release: true);
        var page = await GetPageAsync(afterSeq: 0);

        page.CursorReset.Should().BeFalse();
        page.Items.Select(i => i.Kind).Should().Equal("session", "order", "order");
        page.Items[0].Session!.Id.Should().Be(released);
        page.Items[0].Session!.ReleasedAt.Should().NotBeNull();
        page.Items[0].Session!.OwnerReplicaId.Should().Be(_replicaId);
        page.Items.Skip(1).Select(i => i.Order!.Id).Should().BeEquivalentTo(releasedOrders);
        page.Items.Skip(1).Should().OnlyContain(i =>
            i.Order!.SessionReleasedAt == page.Items[0].Session!.ReleasedAt && i.Order.SessionId == released);
        page.NextAfterSeq.Should().Be(page.Items[^1].ChangeSeq);
        page.SideSessions.Select(s => s.Id).Should().Equal(released);

        var lateOrder = await PushOrderAsync(released);
        var late = await GetPageAsync(page.NextAfterSeq);
        late.Items.Select(IdOf).Should().Equal(lateOrder);

        var everything = (await GetPageAsync(afterSeq: 0)).Items;
        var deliveredIds = everything.Select(IdOf).ToList();
        deliveredIds.Should().NotContain(new[] { live, handingOver, liveOrder, handingOverOrder, sessionless });
    }

    /// <summary>
    /// SessionId NULL sipariş akışa girmez — süzgeç <c>SessionReleasedAt</c>'e güvenmez: tutarsız bir satır (SessionId
    /// boş ama damgalı) da verilmez. Yerelde SessionId NOT NULL FK; böyle bir öğe istemcide eklenemezdi.
    /// </summary>
    [Fact]
    public async Task SessionId_bos_siparis_damgali_olsa_da_inmez()
    {
        var session = Guid.NewGuid();
        await PushSessionAsync(session, EndedAt, release: true);
        var stray = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.Orders.Add(NewOrder(stray, sessionId: null, sessionReleasedAt: DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        var page = await GetPageAsync(afterSeq: 0);
        page.Items.Select(IdOf).Should().Equal(session);
    }

    // ── §10.1-1: ufuk ────────────────────────────────────────────────────────

    /// <summary>
    /// Üç tabloda <c>MIN_ACTIVE_ROWVERSION</c> ufku: commit olmamış bir işlem tabloya daha KÜÇÜK seq'li bir satır
    /// yazmışken ondan sonra commit olmuş satır verilmez (verilseydi imleç onu geçer, uçuştaki satır commit olunca
    /// imlecin gerisinde kalıp hiç inmezdi). İşlem commit olunca ikisi de sırayla iner.
    ///
    /// <para>Okuyucu kilitli READ COMMITTED: plan uçuştaki satıra dokunursa onun kilidinde bekler. Ufuk bekleme
    /// başlamadan okunmuştur; test bu durumda işlemi commit edip okumayı serbest bırakır ve aynı sayfanın uçuştaki
    /// satırı geçmediğini doğrular. İki yolda da iddia aynı: sayfa uçuştaki satırın ötesine geçmez.</para>
    /// </summary>
    [Theory]
    [InlineData(SalesChangeItem.KindSession)]
    [InlineData(SalesChangeItem.KindOrder)]
    [InlineData(SalesChangeItem.KindShipment)]
    public async Task Ucustaki_yazim_ufku_tutar_commit_olunca_iner(string kind)
    {
        var session = Guid.NewGuid();
        await PushSessionAsync(session, EndedAt, release: true);
        var releasedAt = (await GetPageAsync(afterSeq: 0)).Items.Single().Session!.ReleasedAt;
        var cursor = (await GetPageAsync(afterSeq: 0)).NextAfterSeq;

        await using var inFlightDb = NewDb();
        await using var inFlight = await inFlightDb.Database.BeginTransactionAsync();
        var inFlightId = Guid.NewGuid();
        inFlightDb.Add(NewRow(kind, inFlightId, session, releasedAt));
        await inFlightDb.SaveChangesAsync();

        var laterId = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.Add(NewRow(kind, laterId, session, releasedAt));
            await db.SaveChangesAsync();
        });

        var pageTask = GetPageAsync(cursor);
        var readWhileOpen = await Task.WhenAny(pageTask, Task.Delay(TimeSpan.FromSeconds(5))) == pageTask;
        if (!readWhileOpen) await inFlight.CommitAsync();
        var held = await pageTask;
        held.Items.Should().BeEmpty("uçuştaki satırın seq'i daha küçük; ufuk onun altında kalır");
        held.NextAfterSeq.Should().Be(cursor);

        if (readWhileOpen) await inFlight.CommitAsync();
        var delivered = await GetPageAsync(cursor);
        delivered.Items.Select(IdOf).Should().Equal(inFlightId, laterId);
    }

    // ── İmleç ve sayfa ───────────────────────────────────────────────────────

    /// <summary>Eksi ya da ufkun üstündeki imleç sıfırlanır: sayfa baştan verilir ve bildirilir.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gecersiz_imlec_sifirlanir_bastan_verilir(bool aboveHorizon)
    {
        await PushSessionAsync(Guid.NewGuid(), EndedAt, release: true);
        var fromStart = await GetPageAsync(afterSeq: 0);
        fromStart.CursorReset.Should().BeFalse();
        fromStart.Items.Should().ContainSingle();
        // Ufkun çok üstü: bu veritabanında on milyon yazım olmadı.
        var invalid = aboveHorizon ? fromStart.NextAfterSeq + 10_000_000 : -1;

        var page = await GetPageAsync(invalid);

        page.CursorReset.Should().BeTrue();
        page.Items.Select(i => i.ChangeSeq).Should().Equal(fromStart.Items.Select(i => i.ChangeSeq));
        page.NextAfterSeq.Should().Be(fromStart.NextAfterSeq);
    }

    /// <summary>Boş sayfada <c>next</c> istemcinin imlecidir; imleç sıfırlanmaz.</summary>
    [Fact]
    public async Task Bos_sayfada_next_imlectir()
    {
        await PushSessionAsync(Guid.NewGuid(), EndedAt, release: true);
        var first = await GetPageAsync(afterSeq: 0);

        var empty = await GetPageAsync(first.NextAfterSeq);

        empty.Items.Should().BeEmpty();
        empty.CursorReset.Should().BeFalse();
        empty.NextAfterSeq.Should().Be(first.NextAfterSeq);
    }

    /// <summary><c>take</c> 1–500 aralığına kırpılır; birleşim de bu sınırla kesilir ve sayfalar kayıpsız
    /// devam eder.</summary>
    [Fact]
    public async Task Take_500_ile_sinirlanir()
    {
        await WithDbAsync(async db =>
        {
            for (var i = 0; i < 501; i++) db.StreamSessions.Add(NewReleasedSession(Guid.NewGuid()));
            await db.SaveChangesAsync();
        });

        var capped = await GetPageAsync(afterSeq: 0, take: 1000);
        capped.Items.Should().HaveCount(SalesChangesFeed.MaxTake);
        var rest = await GetPageAsync(capped.NextAfterSeq, take: 1000);
        rest.Items.Should().ContainSingle();
        (await GetPageAsync(afterSeq: 0, take: 0)).Items.Should().ContainSingle("alt sınır 1");
    }

    // ── Yan liste ────────────────────────────────────────────────────────────

    /// <summary>
    /// Uçtan uca yan liste: kopyanın Id'siyle kalmış sipariş asıl kaydın taslağıyla (kopyalar sorgu filtresinden
    /// gizli; okuma onları açıkça ister), GUID olmayan ve projeksiyonu olmayan müşterinin taslağı siparişin kendi
    /// alanlarından gelir; hiçbiri sayfayı bozmaz.
    /// </summary>
    [Fact]
    public async Task Yan_liste_asil_kayit_ve_projeksiyonsuz_taslaklari_tasir()
    {
        var session = Guid.NewGuid();
        await PushSessionAsync(session, EndedAt, release: true);
        var releasedAt = (await GetPageAsync(afterSeq: 0)).Items.Single().Session!.ReleasedAt;
        var canonical = Projection("youtube", $"asil-{Guid.NewGuid():N}", "Asıl Ad");
        var copy = Projection("youtube", $"kopya-{Guid.NewGuid():N}", null);
        copy.MergedIntoId = canonical.Id;
        var nonGuid = $"legacy-{Guid.NewGuid():N}";
        var missingHex = Guid.NewGuid().ToString("N");
        var viaCopy = NewOrder(Guid.NewGuid(), session, releasedAt, customerId: copy.Id.ToString("N"));
        var legacy = NewOrder(Guid.NewGuid(), session, releasedAt, customerId: nonGuid);
        var orphan = NewOrder(Guid.NewGuid(), session, releasedAt, customerId: missingHex);
        await WithDbAsync(async db =>
        {
            db.WpfCustomerProjections.AddRange(canonical, copy);
            db.Orders.AddRange(viaCopy, legacy, orphan);
            await db.SaveChangesAsync();
        });

        var page = await GetPageAsync(afterSeq: 0);

        var orders = page.Items.Where(i => i.Order is not null).ToDictionary(i => i.Order!.Id, i => i.Order!);
        orders[viaCopy.Id].CustomerId.Should().Be(canonical.Id.ToString("N"));
        orders[legacy.Id].CustomerId.Should().Be(nonGuid);
        orders[orphan.Id].CustomerId.Should().Be(missingHex);
        page.SideSessions.Select(s => s.Id).Should().Equal(session);
        page.CustomerDrafts.Should().BeEquivalentTo(new[]
        {
            new SalesCustomerDraft(canonical.Id.ToString("N"), "youtube", canonical.Username, "Asıl Ad", null),
            new SalesCustomerDraft(nonGuid, legacy.Platform, legacy.Username, legacy.DisplayName, null),
            new SalesCustomerDraft(missingHex, orphan.Platform, orphan.Username, orphan.DisplayName, null),
        });
    }

    // ── Sahiplik ve kalp atışı ───────────────────────────────────────────────

    /// <summary>Başka yayıncının lisansı ya da var olmayan lisans 404.</summary>
    [Fact]
    public async Task Baska_lisans_404()
    {
        var (_, otherCustomer, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var foreign = await SeedLicenseAsync(otherCustomer);

        (await _client.GetAsync($"/api/v1/licenses/{foreign}/sales/changes?afterSeq=0"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/v1/licenses/{Guid.NewGuid()}/sales/changes?afterSeq=0"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary><c>replicaId</c> + <c>seenMode</c> kalp atışı sayılır (state ile aynı); yarım zarf 400.</summary>
    [Fact]
    public async Task Kopya_parametreleri_kalp_atisi_sayilir_yarim_zarf_400()
    {
        var replica = Guid.NewGuid();

        var ok = await _client.GetAsync(
            $"/api/v1/licenses/{_licenseId}/sales/changes?afterSeq=0&replicaId={replica}&seenMode=0");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await WithDbAsync(db => db.WpfReplicas.AsNoTracking()
            .SingleOrDefaultAsync(r => r.LicenseId == _licenseId && r.ReplicaId == replica));
        row.Should().NotBeNull();
        row!.SeenMode.Should().Be(SalesSyncMode.Off);
        row.ClientVersion.Should().Be("0.9.11");

        (await _client.GetAsync($"/api/v1/licenses/{_licenseId}/sales/changes?afterSeq=0&replicaId={replica}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Yardımcılar ──────────────────────────────────────────────────────────

    private static Guid IdOf(SalesChangeItem item) =>
        item.Session?.Id ?? item.Order?.Id ?? item.Shipment!.Id;

    private object NewRow(string kind, Guid id, Guid sessionId, DateTimeOffset? releasedAt) => kind switch
    {
        SalesChangeItem.KindSession => NewReleasedSession(id),
        SalesChangeItem.KindOrder => NewOrder(id, sessionId, releasedAt),
        _ => new Shipment
        {
            Id = id, LicenseId = _licenseId, CustomerId = Guid.NewGuid().ToString("N"),
            Status = ShipmentStatus.Pending, CumulativeAmount = 100m,
            CreatedAt = StartedAt, UpdatedAt = DateTimeOffset.UtcNow,
        },
    };

    private StreamSession NewReleasedSession(Guid id) => new()
    {
        Id = id, LicenseId = _licenseId, Title = "Akşam yayını", StartedAt = StartedAt, EndedAt = EndedAt,
        ReleasedAt = DateTimeOffset.UtcNow, Platforms = "youtube", UpdatedAt = DateTimeOffset.UtcNow,
    };

    private Order NewOrder(Guid id, Guid? sessionId, DateTimeOffset? sessionReleasedAt, string? customerId = null) => new()
    {
        Id = id, LicenseId = _licenseId, SessionId = sessionId, SessionReleasedAt = sessionReleasedAt,
        CustomerId = customerId ?? Guid.NewGuid().ToString("N"), Platform = "youtube",
        Username = $"u-{Guid.NewGuid():N}", DisplayName = "Takma ad", MessageText = "A1", Code = "A1",
        Price = 100m, AddedAt = StartedAt.AddMinutes(10), UpdatedAt = DateTimeOffset.UtcNow,
    };

    private WpfCustomerProjection Projection(string platform, string username, string? displayName) => new()
    {
        Id = Guid.NewGuid(), LicenseId = _licenseId, Platform = platform, Username = username,
        DisplayName = displayName, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<SalesChangesPage> GetPageAsync(long afterSeq, int take = 500)
    {
        var resp = await _client.GetAsync(
            $"/api/v1/licenses/{_licenseId}/sales/changes?afterSeq={afterSeq}&take={take}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<SalesChangesPage>())!;
    }

    private async Task PushSessionAsync(Guid sessionId, DateTimeOffset? endedAt, bool release)
    {
        var resp = await _client.PostAsJsonAsync($"/api/v1/licenses/{_licenseId}/sessions/sync", new
        {
            sessions = new[]
            {
                new
                {
                    id = sessionId, title = "Akşam yayını", startedAt = StartedAt, endedAt, platforms = "youtube",
                    notes = (string?)null, release, machineName = "PC-A", unsentLabelCount = (int?)null,
                },
            },
            replicaId = _replicaId,
            protocol = 2,
            seenMode = 0,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    private async Task<Guid> PushOrderAsync(Guid? sessionId, bool v2 = true)
    {
        var orderId = Guid.NewGuid();
        var item = new
        {
            id = orderId, sessionId, customerId = Guid.NewGuid().ToString("N"), platform = "youtube",
            username = $"u-{Guid.NewGuid():N}", displayName = (string?)null, messageText = "A1", code = "A1",
            price = 100m, addedAt = StartedAt.AddMinutes(10), printedAt = (DateTimeOffset?)null,
            cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
            isShippingFee = false, isBackupPromoted = false, isTentativeBackup = false,
        };
        object body = v2
            ? new { orders = new[] { item }, replicaId = _replicaId, protocol = 2, seenMode = 0 }
            : new { orders = new[] { item } };
        var resp = await _client.PostAsJsonAsync($"/api/v1/licenses/{_licenseId}/orders/sync", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return orderId;
    }

    private LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs).Options);

    private async Task WithDbAsync(Func<LicenseDbContext, Task> action)
    {
        await using var db = NewDb();
        await action(db);
    }

    private async Task<T> WithDbAsync<T>(Func<LicenseDbContext, Task<T>> action)
    {
        await using var db = NewDb();
        return await action(db);
    }

    private async Task<Guid> SeedLicenseAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }
}
