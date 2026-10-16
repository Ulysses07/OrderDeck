using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PaymentRequestTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST payment-requests/{id}/finish</c> — spec 2026-10-13 §4.7, §6.4 "Akışın sonu", "Teslim kaydı olmadan dolan
/// kira" (InMemory). Kira kaydının sonucu: Sending ya da Unconfirmed her sonucu alır (geç <c>delivered</c> süre dolumunun
/// Unconfirmed'ını yükseltir); Delivered ve Abandoned son hâldir — aynı sonuç ve tutar 200, farklısı 409
/// <c>content-conflict</c>. Yalnız kaydı yazan kopya ya da onun geri yüklenmiş ardılı bitirebilir. Uç iki kipte çalışır
/// ve alt sürüme bağlı değildir: <c>disable</c>'dan sonra da, eski kurulumda da kira asılı kalmamalı.
///
/// <para>Fabrika <c>SalesSync:MinClientVersion</c>'sız: <c>begin</c> burada 409 alırdı, <c>finish</c> almaz.</para>
/// </summary>
public sealed class PaymentRequestFinishTests : IClassFixture<ApiFactory>, IClassFixture<SalesSyncConfiguredApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly SalesSyncConfiguredApiFactory _configured;

    public PaymentRequestFinishTests(ApiFactory factory, SalesSyncConfiguredApiFactory configured)
    {
        _factory = factory;
        _configured = configured;
    }

    /// <summary>Verilen kopyanın canlı kirası (başlangıç şimdi − <paramref name="age"/>).</summary>
    private static Task<PaymentRequestRecord> LeaseAsync(
        Kit kit, Guid replica, PaymentRequestState state = PaymentRequestState.Sending, TimeSpan? age = null,
        DateTimeOffset? finishedAt = null, decimal? netAmount = null, decimal? appliedAmount = null,
        bool? fullyFromBalance = null, int version = 0)
    {
        var owner = Kit.NewId();
        return kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease, state,
            DateTimeOffset.UtcNow - (age ?? TimeSpan.FromSeconds(10)), finishedAt: finishedAt, netAmount: netAmount,
            appliedAmount: appliedAmount, fullyFromBalance: fullyFromBalance, replicaId: replica, version: version);
    }

    // ── Sonuçlar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// §6.4 "Akışın sonu": canlı kira her sonucu alır — durum, tutarlar, <c>FinishedAt</c> sunucu saatiyle,
    /// <c>Version + 1</c>. İstek kopyanın kalp atışıdır. Mod On ama alt sürüm tanımsız ve istemci eski: <c>finish</c>
    /// sürüme bağlı değil (kira asılı kalmamalı).
    /// </summary>
    [Theory]
    [InlineData("delivered", PaymentRequestState.Delivered)]
    [InlineData("unconfirmed", PaymentRequestState.Unconfirmed)]
    [InlineData("abandoned", PaymentRequestState.Abandoned)]
    public async Task Canli_kira_her_sonucu_alir_tutarlar_FinishedAt_Version(string outcome, PaymentRequestState state)
    {
        var kit = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var lease = await LeaseAsync(kit, replica);
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.FinishAsync(lease.Id,
            Kit.FinishBody(replica, outcome, netAmount: 140.50m, appliedAmount: 59.50m, fullyFromBalance: false),
            userAgent: "OrderDeck-WPF/0.9.10");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.RecordAsync(lease.Id);
        saved!.State.Should().Be(state);
        saved.NetAmount.Should().Be(140.50m);
        saved.AppliedAmount.Should().Be(59.50m);
        saved.FullyFromBalance.Should().BeFalse();
        saved.FinishedAt.Should().BeOnOrAfter(before);
        saved.Version.Should().Be(1);
        var body = await Kit.ReadFinishAsync(resp);
        body.Should().BeEquivalentTo(new Kit.FinishResponse(lease.Id, outcome, saved.FinishedAt));
        (await kit.Sales.ReplicaAsync(replica))!.LastSeenAt.Should().BeOnOrAfter(before, "istek kalp atışıdır");
    }

    /// <summary>§4.7, §15-54: mod Off'ta da çalışır — mod On'da başlamış kiranın sonucu <c>disable</c>'dan sonra
    /// yazılabilmeli.</summary>
    [Fact]
    public async Task Mod_Offta_da_calisir()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var replica = Guid.NewGuid();
        var lease = await LeaseAsync(kit, replica);

        var resp = await kit.FinishAsync(lease.Id,
            Kit.FinishBody(replica, "delivered", netAmount: 0m, appliedAmount: 80m, fullyFromBalance: true));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.RecordAsync(lease.Id);
        saved!.State.Should().Be(PaymentRequestState.Delivered);
        saved.FullyFromBalance.Should().BeTrue();
    }

    // ── Yeniden oynatma ve son hâl ───────────────────────────────────────────

    /// <summary>
    /// Yanıtı kaybolan <c>finish</c> güvenle yinelenir: son hâldeki kayıtta aynı sonuç ve aynı tutarlar 200, hiçbir şey
    /// yazılmaz (sürüm ve an değişmez). <c>140.5</c> ile <c>140.50</c> aynı tutardır.
    /// </summary>
    [Theory]
    [InlineData("delivered")]
    [InlineData("abandoned")]
    [InlineData("unconfirmed")]
    public async Task Ayni_sonuc_ve_tutar_yeniden_200_yazim_yok(string outcome)
    {
        var kit = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var lease = await LeaseAsync(kit, replica);
        (await kit.FinishAsync(lease.Id, Kit.FinishBody(replica, outcome, 140.50m, 10m)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await kit.RecordAsync(lease.Id);

        var again = await kit.FinishAsync(lease.Id, Kit.FinishBody(replica, outcome, 140.5m, 10.00m));

        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        (await Kit.ReadFinishAsync(again)).Should().BeEquivalentTo(
            new Kit.FinishResponse(lease.Id, outcome, first!.FinishedAt));
        var saved = await kit.RecordAsync(lease.Id);
        saved!.Version.Should().Be(first.Version);
        saved.FinishedAt.Should().Be(first.FinishedAt);
    }

    public static TheoryData<PaymentRequestState, string, decimal, decimal?, bool> FinalConflicts => new()
    {
        { PaymentRequestState.Delivered, "abandoned", 140m, 10m, false },
        { PaymentRequestState.Delivered, "unconfirmed", 140m, 10m, false },
        { PaymentRequestState.Delivered, "delivered", 141m, 10m, false },
        { PaymentRequestState.Delivered, "delivered", 140m, 9m, false },
        { PaymentRequestState.Delivered, "delivered", 140m, null, false },
        { PaymentRequestState.Delivered, "delivered", 140m, 10m, true },
        { PaymentRequestState.Abandoned, "delivered", 140m, 10m, false },
        { PaymentRequestState.Abandoned, "abandoned", 0m, 10m, false },
    };

    /// <summary>Delivered ve Abandoned son hâldir: başka sonuç ya da başka tutar 409 <c>content-conflict</c>; kayıt
    /// değişmez.</summary>
    [Theory]
    [MemberData(nameof(FinalConflicts))]
    public async Task Son_hal_farkli_sonuc_ya_da_tutar_409_content_conflict(
        PaymentRequestState final, string outcome, decimal netAmount, decimal? appliedAmount, bool fullyFromBalance)
    {
        var kit = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var finishedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var lease = await LeaseAsync(kit, replica, final, finishedAt: finishedAt, netAmount: 140m, appliedAmount: 10m,
            fullyFromBalance: false, version: 1);

        var resp = await kit.FinishAsync(lease.Id,
            Kit.FinishBody(replica, outcome, netAmount, appliedAmount, fullyFromBalance));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("content-conflict");
        var saved = await kit.RecordAsync(lease.Id);
        saved!.State.Should().Be(final);
        saved.NetAmount.Should().Be(140m);
        saved.AppliedAmount.Should().Be(10m);
        saved.FullyFromBalance.Should().BeFalse();
        saved.FinishedAt.Should().Be(finishedAt);
        saved.Version.Should().Be(1);
    }

    /// <summary>
    /// Unconfirmed son hâl değildir: başka sonuç ya da başka tutar yazılır. Bilgisayar sonucu sonradan öğrenir (ör.
    /// Cloud API "bekliyor" → kesin değil ama tutar düzeldi) — her yazım <c>Version + 1</c>.
    /// </summary>
    [Theory]
    [InlineData("unconfirmed", PaymentRequestState.Unconfirmed)]
    [InlineData("abandoned", PaymentRequestState.Abandoned)]
    public async Task Unconfirmed_kayit_yeni_sonucu_ya_da_tutari_alir(string outcome, PaymentRequestState state)
    {
        var kit = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var lease = await LeaseAsync(kit, replica, PaymentRequestState.Unconfirmed,
            finishedAt: DateTimeOffset.UtcNow.AddMinutes(-5), netAmount: 100m, appliedAmount: 0m, fullyFromBalance: false,
            version: 1);

        var resp = await kit.FinishAsync(lease.Id, Kit.FinishBody(replica, outcome, netAmount: 90m, appliedAmount: 10m));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.RecordAsync(lease.Id);
        saved!.State.Should().Be(state);
        saved.NetAmount.Should().Be(90m);
        saved.AppliedAmount.Should().Be(10m);
        saved.Version.Should().Be(2);
    }

    /// <summary>
    /// §6.4 "Teslim kaydı olmadan dolan kira", §6.8: PC-A'nın kirası doldu; PC-B'nin <c>begin</c>'i onu Unconfirmed yapar
    /// ve <c>already-requested</c> alır. PC-A'nın geç <c>delivered</c>'ı kaydı Delivered'a yükseltir (Version 2).
    /// </summary>
    [Fact]
    public async Task Sure_dolumunun_Unconfirmedi_gec_delivered_ile_Delivered_olur()
    {
        var kit = await Kit.CreateAsync(_configured);
        var pcA = Guid.NewGuid();
        var owner = Kit.NewId();
        var lease = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Sending, DateTimeOffset.UtcNow.AddMinutes(-3), replicaId: pcA, machineName: "PC-A");
        var expire = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));
        expire.StatusCode.Should().Be(HttpStatusCode.Conflict, await expire.Content.ReadAsStringAsync());
        (await kit.RecordAsync(lease.Id))!.State.Should().Be(PaymentRequestState.Unconfirmed);

        var resp = await kit.FinishAsync(lease.Id, Kit.FinishBody(pcA, "delivered", netAmount: 550m));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadFinishAsync(resp)).State.Should().Be("delivered");
        var saved = await kit.RecordAsync(lease.Id);
        saved!.State.Should().Be(PaymentRequestState.Delivered);
        saved.NetAmount.Should().Be(550m);
        saved.Version.Should().Be(2);
    }

    // ── Kimin bitirebileceği ─────────────────────────────────────────────────

    /// <summary>Kaydı yazmamış ve onun soyundan gelmeyen kopya bitiremez: 409 <c>content-conflict</c>, kayıt
    /// değişmez.</summary>
    [Fact]
    public async Task Baska_kopya_409_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        var lease = await LeaseAsync(kit, Guid.NewGuid());

        var resp = await kit.FinishAsync(lease.Id, Kit.FinishBody(Guid.NewGuid(), "delivered", 100m));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("content-conflict");
        var saved = await kit.RecordAsync(lease.Id);
        saved!.State.Should().Be(PaymentRequestState.Sending);
        saved.Version.Should().Be(0);
    }

    /// <summary>
    /// §8.7: yedekten geri yüklenen kopya yeni bir ReplicaId alır; <c>PendingPaymentRecord</c>'da bekleyen
    /// <c>finish</c>'i o gönderir. Kaydı yazan A, A'dan geri yüklenen B, B'den geri yüklenen C — C bitirebilir. Zincirin
    /// dışındaki kardeş (A'nın değil, başka bir kopyanın ardılı) bitiremez.
    /// </summary>
    [Fact]
    public async Task Geri_yuklenmis_ardil_bitirebilir_zincir_disi_bitiremez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        await kit.SeedReplicaAsync(a, "PC-A");
        await kit.SeedReplicaAsync(b, "PC-A", previousReplicaId: a);
        await kit.SeedReplicaAsync(c, "PC-A", previousReplicaId: b);
        await kit.SeedReplicaAsync(stranger, "PC-B", previousReplicaId: Guid.NewGuid());
        var lease = await LeaseAsync(kit, a);
        var other = await LeaseAsync(kit, a);

        var successor = await kit.FinishAsync(lease.Id, Kit.FinishBody(c, "delivered", 75m));
        var outsider = await kit.FinishAsync(other.Id, Kit.FinishBody(stranger, "delivered", 75m));

        successor.StatusCode.Should().Be(HttpStatusCode.OK, await successor.Content.ReadAsStringAsync());
        (await kit.RecordAsync(lease.Id))!.State.Should().Be(PaymentRequestState.Delivered);
        outsider.StatusCode.Should().Be(HttpStatusCode.Conflict, await outsider.Content.ReadAsStringAsync());
        (await kit.RecordAsync(other.Id))!.State.Should().Be(PaymentRequestState.Sending);
    }

    /// <summary>Ata, ardılının kaydını bitiremez: zincir yalnız geriye (önceki kopyalara) izlenir.</summary>
    [Fact]
    public async Task Ata_ardilin_kaydini_bitiremez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await kit.SeedReplicaAsync(a, "PC-A");
        await kit.SeedReplicaAsync(b, "PC-A", previousReplicaId: a);
        var lease = await LeaseAsync(kit, b);

        var resp = await kit.FinishAsync(lease.Id, Kit.FinishBody(a, "delivered", 75m));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await kit.RecordAsync(lease.Id))!.State.Should().Be(PaymentRequestState.Sending);
    }

    /// <summary>Kirasız kayıt (<c>Record</c>, geri doldurma) bitirilmez: 409 <c>content-conflict</c>.</summary>
    [Fact]
    public async Task Kirasiz_kayit_409_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var owner = Kit.NewId();
        var record = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Record,
            PaymentRequestState.Unconfirmed, DateTimeOffset.UtcNow.AddDays(-1), finishedAt: DateTimeOffset.UtcNow,
            netAmount: 50m, replicaId: replica);

        var resp = await kit.FinishAsync(record.Id, Kit.FinishBody(replica, "delivered", 50m));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("content-conflict");
        (await kit.RecordAsync(record.Id))!.State.Should().Be(PaymentRequestState.Unconfirmed);
    }

    /// <summary>Kayıt bu lisansta değil (hiç yok ya da başka lisansın): 404; öbür lisansın kaydı değişmez.</summary>
    [Fact]
    public async Task Lisansta_olmayan_kayit_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var second = await kit.Sales.SecondLicenseAsync();
        var owner = Kit.NewId();
        var foreign = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Sending, DateTimeOffset.UtcNow, replicaId: replica, licenseId: second.LicenseId);

        var missing = await kit.FinishAsync(Guid.NewGuid(), Kit.FinishBody(replica, "delivered", 10m));
        var other = await kit.FinishAsync(foreign.Id, Kit.FinishBody(replica, "delivered", 10m));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await kit.RecordAsync(foreign.Id))!.State.Should().Be(PaymentRequestState.Sending);
    }

    /// <summary>Başka yayıncının lisansı: 404, kayıt değişmez.</summary>
    [Fact]
    public async Task Baska_yayincinin_lisansi_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var stranger = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var lease = await LeaseAsync(kit, replica);

        var resp = await stranger.Sales.SendToAsync(kit.FinishUrl(lease.Id), Kit.FinishBody(replica, "delivered", 1m));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await kit.RecordAsync(lease.Id))!.State.Should().Be(PaymentRequestState.Sending);
    }

    // ── 400 ──────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> InvalidBodies => new()
    {
        { "protokolsuz", "invalid-envelope" },
        { "replicaId-yok", "invalid-envelope" },
        { "seenMode-yok", "invalid-envelope" },
        { "sonuc-yok", "invalid-outcome" },
        { "sonuc-sending", "invalid-outcome" },
        { "sonuc-buyuk-harf", "invalid-outcome" },
        { "sonuc-bozuk", "invalid-outcome" },
        { "net-yok", "invalid-amount" },
        { "net-eksi", "invalid-amount" },
        { "net-3-ondalik", "invalid-amount" },
        { "dusulen-eksi", "invalid-amount" },
        { "bakiye-bayragi-yok", "invalid-amount" },
    };

    /// <summary>Her 400 sınıfı: kayıt değişmez.</summary>
    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Gecersiz_istek_400_kodlu_kayit_degismez(string @case, string code)
    {
        var kit = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var lease = await LeaseAsync(kit, replica);
        var body = Kit.FinishBody(replica, "delivered", 10m);
        switch (@case)
        {
            case "protokolsuz": body.Remove("protocol"); break;
            case "replicaId-yok": body.Remove("replicaId"); break;
            case "seenMode-yok": body.Remove("seenMode"); break;
            case "sonuc-yok": body.Remove("outcome"); break;
            case "sonuc-sending": body["outcome"] = "sending"; break;
            case "sonuc-buyuk-harf": body["outcome"] = "Delivered"; break;
            case "sonuc-bozuk": body["outcome"] = "sent"; break;
            case "net-yok": body.Remove("netAmount"); break;
            case "net-eksi": body["netAmount"] = -1m; break;
            case "net-3-ondalik": body["netAmount"] = 1.005m; break;
            case "dusulen-eksi": body["appliedAmount"] = -0.01m; break;
            case "bakiye-bayragi-yok": body.Remove("fullyFromBalance"); break;
            default: throw new ArgumentOutOfRangeException(nameof(@case), @case, null);
        }

        var resp = await kit.FinishAsync(lease.Id, body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be(code, @case);
        var saved = await kit.RecordAsync(lease.Id);
        saved!.State.Should().Be(PaymentRequestState.Sending);
        saved.Version.Should().Be(0);
    }

    /// <summary>Düşüm bilinmiyorsa (<c>appliedAmount</c> yok) kayıt boş düşümle yazılır.</summary>
    [Fact]
    public async Task Dusum_bos_gecilebilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var replica = Guid.NewGuid();
        var lease = await LeaseAsync(kit, replica);

        var resp = await kit.FinishAsync(lease.Id, Kit.FinishBody(replica, "abandoned", 25m, appliedAmount: null));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.RecordAsync(lease.Id);
        saved!.State.Should().Be(PaymentRequestState.Abandoned);
        saved.AppliedAmount.Should().BeNull();
    }
}
