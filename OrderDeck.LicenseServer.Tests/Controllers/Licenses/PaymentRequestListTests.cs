using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PaymentRequestTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>GET payment-requests?scopeKey&amp;memberIds</c> — spec 2026-10-13 §4.7, §6.4 "Teslim kayıtlarının okunması"
/// (InMemory). Salt okunur: lisansın, aynı kapsamdaki, üyeleri istenen üyelerle kesişen kayıtları; vazgeçilmişler
/// hariç. Süresi dolmuş canlı kira yazılmadan sonucu bilinmeyen olarak bildirilir. Uzak <c>Covers</c> sipariş kümesiyle
/// cevaplanır (<c>orderIds</c>). İki kipte çalışır, alt sürüme bağlı değildir. Çağıranın kendi kayıtları süzülmez (W3
/// yerel işiyle zaten temsil ettiği kayıtları kendisi düşer).
/// </summary>
public sealed class PaymentRequestListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PaymentRequestListTests(ApiFactory factory) => _factory = factory;

    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddDays(-3);

    /// <summary>
    /// Ortak üyeden bulunur (kayıtların PersonKey'i farklı olsa da), kapsam süzülür, vazgeçilmiş kayıt ve ilgisiz kişinin
    /// kaydı girmez. Öğe bütün alanlarıyla döner; sıra kapanış anına göre.
    /// </summary>
    [Fact]
    public async Task Ortak_uyeden_bulunur_kapsam_suzulur_vazgecilmis_haric()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = Kit.NewId();
        var b = $"yerel-{Guid.NewGuid():N}";
        var replica = Guid.NewGuid();
        var order = Guid.NewGuid();
        var lease = await kit.SeedRecordAsync("c:" + a, [a, Kit.NewId()], PaymentRequestKind.Lease,
            PaymentRequestState.Delivered, T0, finishedAt: T0.AddMinutes(1), netAmount: 300m, replicaId: replica,
            machineName: "PC-A", orderIds: [order], appliedAmount: 50m, fullyFromBalance: false, productTotal: 290m,
            totalBeforeBalance: 350m, version: 1);
        var viaRaw = await kit.SeedRecordAsync("r:" + b, [b], PaymentRequestKind.Record, PaymentRequestState.Unconfirmed,
            T0.AddHours(-5), finishedAt: T0.AddHours(1), netAmount: 120m, machineName: null);
        await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Lease, PaymentRequestState.Delivered, T0,
            finishedAt: T0, scopeKey: Kit.SessionScope(Guid.NewGuid()));
        await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Lease, PaymentRequestState.Abandoned, T0,
            finishedAt: T0);
        var stranger = Kit.NewId();
        await kit.SeedRecordAsync("c:" + stranger, [stranger], PaymentRequestKind.Record, PaymentRequestState.Delivered,
            T0, finishedAt: T0);

        var resp = await kit.ListAsync(Kit.Cumulative, [a, b]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var records = (await Kit.ReadListAsync(resp)).Records;
        records.Select(r => r.Id).Should().Equal(lease.Id, viaRaw.Id);
        records[0].Should().BeEquivalentTo(new Kit.ListItem(
            lease.Id, replica, "PC-A", "lease", "delivered", 300m, 50m, false, 290m, 350m, T0, T0.AddMinutes(1),
            [order]));
        records[1].Should().BeEquivalentTo(new
        {
            Kind = "record",
            State = "unconfirmed",
            Machine = (string?)null,
            NetAmount = (decimal?)120m,
            ProductTotal = (decimal?)null,
            OrderIds = new List<Guid>(),
        });
    }

    /// <summary>
    /// §6.4 "Teslim kaydı olmadan dolan kira": süresi dolmuş canlı kira sonucu bilinmeyen olarak bildirilir, an kiranın
    /// dolduğu an; satır YAZILMAZ (sürüm ve durum aynı). Süresi dolmamış kira canlı (<c>sending</c>) döner ve en yeni
    /// sayılır.
    /// </summary>
    [Fact]
    public async Task Suresi_dolmus_kira_yazilmadan_unconfirmed_bildirilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = Kit.NewId();
        var expiredStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var expired = await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Lease, PaymentRequestState.Sending,
            expiredStart);
        var live = await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Lease, PaymentRequestState.Sending,
            DateTimeOffset.UtcNow.AddSeconds(-5), scopeKey: Kit.Cumulative);
        var delivered = await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Lease,
            PaymentRequestState.Delivered, DateTimeOffset.UtcNow.AddMinutes(-4),
            finishedAt: DateTimeOffset.UtcNow.AddMinutes(-4));

        var resp = await kit.ListAsync(Kit.Cumulative, [a]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var records = (await Kit.ReadListAsync(resp)).Records;
        records.Select(r => (r.Id, r.State)).Should().Equal(
            (expired.Id, "unconfirmed"), (delivered.Id, "delivered"), (live.Id, "sending"));
        records[0].FinishedAt.Should().Be(expiredStart.AddSeconds(120));
        records[2].FinishedAt.Should().BeNull();
        var saved = await kit.RecordAsync(expired.Id);
        saved!.State.Should().Be(PaymentRequestState.Sending, "liste salt okunurdur");
        saved.Version.Should().Be(0);
        saved.FinishedAt.Should().BeNull();
    }

    /// <summary>Başka lisansın kaydı aynı üye ve kapsamla da görünmez.</summary>
    [Fact]
    public async Task Baska_lisansin_kaydi_gorunmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var second = await kit.Sales.SecondLicenseAsync();
        var a = Kit.NewId();
        await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Record, PaymentRequestState.Delivered, T0,
            finishedAt: T0, licenseId: second.LicenseId);

        var resp = await kit.ListAsync(Kit.Cumulative, [a]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadListAsync(resp)).Records.Should().BeEmpty();
    }

    /// <summary>Başka yayıncının lisansı: 404.</summary>
    [Fact]
    public async Task Baska_yayincinin_lisansi_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var stranger = await Kit.CreateAsync(_factory);
        var a = Kit.NewId();

        var resp = await stranger.Sales.Client.GetAsync(kit.ListUrl(Kit.Cumulative, [a]));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>§4.7: mod Off'ta da çalışır; miras kapsamı da okunur (geri doldurulan geçmiş).</summary>
    [Theory]
    [InlineData("legacy")]
    [InlineData("legacy:anahtar-1:9")]
    [InlineData(Kit.Cumulative)]
    public async Task Mod_Offta_da_calisir(string scopeKey)
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var a = Kit.NewId();
        var record = await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Record, PaymentRequestState.Delivered,
            T0, finishedAt: T0, scopeKey: scopeKey);

        var resp = await kit.ListAsync(scopeKey, [a]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadListAsync(resp)).Records.Select(r => r.Id).Should().Equal(record.Id);
    }

    /// <summary>
    /// İstenen GUID'li üye asıl kayda çözülür: istemci bir kopyanın Id'sini gönderse de asıl kayıtla saklanmış kayıt
    /// bulunur (kayıtların üyeleri asıl kayıttır, §6.4-b).
    /// </summary>
    [Fact]
    public async Task Kopya_Idsi_asil_kayda_cozulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var canonical = await kit.SeedProjectionAsync(null);
        var copy = await kit.SeedProjectionAsync(null, mergedInto: Guid.ParseExact(canonical, "N"));
        var record = await kit.SeedRecordAsync("c:" + canonical, [canonical], PaymentRequestKind.Record,
            PaymentRequestState.Delivered, T0, finishedAt: T0);

        var resp = await kit.ListAsync(Kit.Cumulative, [copy.ToUpperInvariant()]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadListAsync(resp)).Records.Select(r => r.Id).Should().Equal(record.Id);
    }

    /// <summary>
    /// Sıra kapanış anı, eşitlikte başlangıç (geri doldurulan kayıtlar aynı partide aynı sunucu anını alır); en yeni 200
    /// kayıt döner.
    /// </summary>
    [Fact]
    public async Task Kapanis_sonra_baslangic_sirasi_en_yeni_200()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = Kit.NewId();
        var sameFinish = T0.AddHours(10);
        var later = await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Record, PaymentRequestState.Delivered,
            T0.AddHours(2), finishedAt: sameFinish);
        var earlier = await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Record, PaymentRequestState.Delivered,
            T0.AddHours(1), finishedAt: sameFinish);
        var oldest = new List<Guid>();
        for (var i = 0; i < 199; i++)
            oldest.Add((await kit.SeedRecordAsync("c:" + a, [a], PaymentRequestKind.Record,
                PaymentRequestState.Delivered, T0, finishedAt: T0.AddMinutes(i))).Id);

        var resp = await kit.ListAsync(Kit.Cumulative, [a]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var ids = (await Kit.ReadListAsync(resp)).Records.Select(r => r.Id).ToList();
        ids.Should().HaveCount(200);
        ids.Should().Equal([.. oldest.Skip(1), earlier.Id, later.Id]);
    }

    public static TheoryData<string?, string[], string> InvalidQueries => new()
    {
        { null, ["a"], "invalid-scope" },
        { "Cumulative", ["a"], "invalid-scope" },
        { "session:yayin", ["a"], "invalid-scope" },
        { "legacy:", ["a"], "invalid-scope" },
        { "baska", ["a"], "invalid-scope" },
        { Kit.Cumulative, [], "invalid-members" },
        { Kit.Cumulative, [" "], "invalid-members" },
        { Kit.Cumulative, [new string('a', 65)], "invalid-members" },
        { Kit.Cumulative, Enumerable.Range(0, 101).Select(i => $"m{i}").ToArray(), "invalid-members" },
    };

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task Gecersiz_sorgu_400(string? scopeKey, string[] memberIds, string code)
    {
        var kit = await Kit.CreateAsync(_factory);

        var resp = await kit.ListAsync(scopeKey, memberIds);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be(code);
    }
}
