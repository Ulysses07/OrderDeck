using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PaymentRequestTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST payment-requests/backfill</c> — spec 2026-10-13 §4.7, §6.4 "Ödeme geçmişi (mod Off'ta da)", §8.2-6, §10.2,
/// M17 (InMemory). Kirasız kayıt (<c>Kind = Record</c>): PersonKey ve üyeler <c>begin</c>'deki yoldan, kilit ve
/// denetim yok; <c>recordId</c> ile idempotent, aynı Id'de farklı içerik öğe başına <c>content-conflict</c>; yeniden
/// oynatma yeni üyeleri ekler. İki kipte çalışır, alt sürüme bağlı değildir. Çapraz denetim (§10.4-9): geri
/// doldurulmuş kayıt mod On'da öbür bilgisayarın <c>begin</c>'inde <c>already-requested</c> olarak döner.
///
/// <para>Fabrika <c>SalesSync:MinClientVersion</c>'sız (<c>backfill</c> sürüme bağlı değil); çapraz denetimin
/// <c>begin</c>'i yapılandırılmış fabrikada.</para>
/// </summary>
public sealed class PaymentRequestBackfillTests : IClassFixture<ApiFactory>, IClassFixture<SalesSyncConfiguredApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly SalesSyncConfiguredApiFactory _configured;

    public PaymentRequestBackfillTests(ApiFactory factory, SalesSyncConfiguredApiFactory configured)
    {
        _factory = factory;
        _configured = configured;
    }

    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow.AddDays(-20);

    /// <summary>Kaydın yazılabilir bütün alanları, üye ve sipariş kümeleri sıralı metin olarak (değer eşitliği).</summary>
    private static string Snapshot(PaymentRequestRecord r) => string.Join("|",
        r.LicenseId, r.PersonKey, Convert.ToHexString(r.PersonKeyHash), r.ScopeKey, r.OwnerWpfCustomerId, r.ReplicaId,
        r.MachineName, r.Kind, r.State, r.ProductTotal, r.TotalBeforeBalance, r.NetAmount, r.AppliedAmount,
        r.FullyFromBalance, r.ShippingSettingsVersion, Convert.ToHexString(r.RequestHash), r.StartedAt.UtcTicks,
        r.LeaseUntil?.UtcTicks, r.FinishedAt?.UtcTicks, r.Version,
        string.Join(",", r.Members.Select(m => m.WpfCustomerId).Order(StringComparer.Ordinal)),
        string.Join(",", r.RecordOrders.Select(o => o.OrderId).Order()));

    // ── Yazım ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kayıt yazılır: tür Record, durum ve tutarlar istemcinin; PersonKey grubun anahtarı (begin'in yolu), sahip asıl
    /// kayıt, üyeler = istemcinin kümesi asıl kayda çözülmüş ∪ sunucunun grubu; siparişler; kopya ve makine adı;
    /// <c>StartedAt</c> istemcinin anı, <c>FinishedAt</c> sunucunun alış anı; kira alanları ve toplamlar boş. İstek kopyanın
    /// kalp atışıdır. Mod Off'ta ve On'da, eski sürüm ve alt sürüm tanımsızken de.
    /// </summary>
    [Theory]
    [InlineData(SalesSyncMode.Off)]
    [InlineData(SalesSyncMode.On)]
    public async Task Kayit_yazilir_begin_yoluyla_kisi_ve_uyeler(SalesSyncMode mode)
    {
        var kit = await Kit.CreateAsync(_factory, mode);
        var group = $"grp-{Guid.NewGuid():N}";
        var canonical = await kit.SeedProjectionAsync(group);
        var copy = await kit.SeedProjectionAsync(null, mergedInto: Guid.ParseExact(canonical, "N"));
        var sibling = await kit.SeedProjectionAsync(group);
        var raw = $"yerel-{Guid.NewGuid():N}";
        var replica = Guid.NewGuid();
        await kit.SeedReplicaAsync(replica, "PC-A");
        var recordId = Guid.NewGuid();
        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.BackfillAsync(
            Kit.BackfillBody(replica, Kit.BackfillItem(recordId, copy, [copy, raw], At, orderIds: [order2, order1, order2],
                state: "unconfirmed", netAmount: 140.50m, appliedAmount: 59.50m, fullyFromBalance: false)),
            userAgent: "OrderDeck-WPF/0.9.10");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(new Kit.BackfillResult(recordId, "applied"));
        var saved = await kit.RecordAsync(recordId);
        saved!.Kind.Should().Be(PaymentRequestKind.Record);
        saved.State.Should().Be(PaymentRequestState.Unconfirmed);
        saved.LicenseId.Should().Be(kit.LicenseId);
        saved.PersonKey.Should().Be("g:" + group);
        saved.PersonKeyHash.Should().Equal(PaymentPersonKey.Hash("g:" + group));
        saved.ScopeKey.Should().Be(Kit.Cumulative);
        saved.OwnerWpfCustomerId.Should().Be(canonical);
        saved.ReplicaId.Should().Be(replica);
        saved.MachineName.Should().Be("PC-A");
        saved.NetAmount.Should().Be(140.50m);
        saved.AppliedAmount.Should().Be(59.50m);
        saved.FullyFromBalance.Should().BeFalse();
        saved.ProductTotal.Should().BeNull();
        saved.TotalBeforeBalance.Should().BeNull();
        saved.ShippingSettingsVersion.Should().BeNull();
        saved.LeaseUntil.Should().BeNull();
        saved.StartedAt.Should().Be(At);
        saved.FinishedAt.Should().BeOnOrAfter(before);
        saved.Version.Should().Be(0);
        saved.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo([canonical, sibling, raw]);
        saved.Members.Should().OnlyContain(m => m.ScopeKey == Kit.Cumulative && m.LicenseId == kit.LicenseId);
        saved.RecordOrders.Select(o => o.OrderId).Should().BeEquivalentTo([order1, order2]);
        (await kit.Sales.ReplicaAsync(replica))!.LastSeenAt.Should().BeOnOrAfter(before, "istek kalp atışıdır");
    }

    /// <summary>Miras kapsamı ve yayın kapsamı da yazılır; GUID'siz sahibin anahtarı <c>r:</c> + ham Id.</summary>
    [Theory]
    [InlineData("legacy")]
    [InlineData("legacy:anahtar-1:9")]
    [InlineData("session")]
    public async Task Miras_ve_yayin_kapsami_GUIDsiz_sahip(string scope)
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var scopeKey = scope == "session" ? Kit.SessionScope(Guid.NewGuid()) : scope;
        var raw = $"yerel-{Guid.NewGuid():N}";
        var recordId = Guid.NewGuid();

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, raw, [raw], At, scopeKey: scopeKey, netAmount: 10m)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.RecordAsync(recordId);
        saved!.ScopeKey.Should().Be(scopeKey);
        saved.PersonKey.Should().Be("r:" + raw);
        saved.OwnerWpfCustomerId.Should().Be(raw);
        saved.Members.Should().ContainSingle().Which.ScopeKey.Should().Be(scopeKey);
    }

    /// <summary>İstemcinin anı gelecekteyse şimdiye kırpılır (bilgi ve gösterim; sıralama sunucu saatiyle).</summary>
    [Fact]
    public async Task Gelecekteki_an_simdiye_kirpilir()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var owner = Kit.NewId();
        var recordId = Guid.NewGuid();

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, owner, [owner], DateTimeOffset.UtcNow.AddDays(2), netAmount: 10m)));
        var after = DateTimeOffset.UtcNow;

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.RecordAsync(recordId);
        saved!.StartedAt.Should().BeOnOrBefore(after);
        saved.StartedAt.Should().Be(saved.FinishedAt!.Value);
    }

    // ── Idempotency (§10.2) ──────────────────────────────────────────────────

    /// <summary>
    /// §10.2: aynı Id ve aynı içerik <c>unchanged</c>, hiçbir şey yazılmaz. İçerik özeti normalleştirilmiştir: tutarın
    /// yazımı (<c>140.5</c> / <c>140.50</c>), siparişlerin sırası ve tekrarı, istemcinin anı ve kopya özete girmez —
    /// geri yüklenmiş kopya aynı geçmişi yeniden gönderebilir. Sahip özete girmez, asıl kayıt düzeyinde denetlenir:
    /// kopyanın Id'siyle gelen aynı iş aynı kayıttır.
    /// </summary>
    [Fact]
    public async Task Ayni_icerik_unchanged_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var canonical = await kit.SeedProjectionAsync(null);
        var copy = await kit.SeedProjectionAsync(null, mergedInto: Guid.ParseExact(canonical, "N"));
        var recordId = Guid.NewGuid();
        var o1 = Guid.NewGuid();
        var o2 = Guid.NewGuid();
        (await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
                Kit.BackfillItem(recordId, canonical, [canonical], At, orderIds: [o1, o2], netAmount: 140.50m,
                    appliedAmount: 10m))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await kit.RecordAsync(recordId);

        var again = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, copy, [copy], At.AddHours(3), orderIds: [o2, o1, o2], netAmount: 140.5m,
                appliedAmount: 10.00m)));

        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(again)).Records.Should().Equal(new Kit.BackfillResult(recordId, "unchanged"));
        Snapshot((await kit.RecordAsync(recordId))!).Should().Be(Snapshot(first!));
        (await kit.RecordsAsync()).Should().ContainSingle();
    }

    public static TheoryData<string> ContentChanges => new()
    {
        "sahip", "kapsam", "durum", "net", "dusulen", "dusulen-bos", "bakiye-bayragi", "siparis-eksik", "siparis-fazla",
    };

    /// <summary>§10.2: aynı Id'ye farklı içerik öğe başına <c>content-conflict</c>; kayıt değişmez, parti 200.</summary>
    [Theory]
    [MemberData(nameof(ContentChanges))]
    public async Task Ayni_Id_farkli_icerik_content_conflict(string change)
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var owner = Kit.NewId();
        var other = Kit.NewId();
        var recordId = Guid.NewGuid();
        var order = Guid.NewGuid();
        Dictionary<string, object?> Item() => Kit.BackfillItem(recordId, owner, [owner, other], At, orderIds: [order],
            netAmount: 100m, appliedAmount: 5m);
        (await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(), Item()))).StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await kit.RecordAsync(recordId);
        var item = Item();
        switch (change)
        {
            case "sahip": item["ownerId"] = other; break;
            case "kapsam": item["scopeKey"] = "legacy"; break;
            case "durum": item["state"] = "unconfirmed"; break;
            case "net": item["netAmount"] = 100.01m; break;
            case "dusulen": item["appliedAmount"] = 6m; break;
            case "dusulen-bos": item["appliedAmount"] = null; break;
            case "bakiye-bayragi": item["fullyFromBalance"] = true; break;
            case "siparis-eksik": item["orderIds"] = Array.Empty<Guid>(); break;
            case "siparis-fazla": item["orderIds"] = new[] { order, Guid.NewGuid() }; break;
            default: throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(), item));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(
            new Kit.BackfillResult(recordId, "content-conflict"));
        Snapshot((await kit.RecordAsync(recordId))!).Should().Be(Snapshot(first!));
    }

    /// <summary>
    /// Yeniden oynatma yeni üyeleri ekler: grup senkronu iki deneme arasında kişiyi büyütmüş olabilir. Hem istemcinin
    /// yeni üyesi hem sunucu grubuna sonradan katılan üye eklenir; var olan üye yinelenmez. Sonuç <c>unchanged</c>.
    /// </summary>
    [Fact]
    public async Task Yeniden_oynatma_yeni_uyeleri_ekler()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var group = $"grp-{Guid.NewGuid():N}";
        var owner = await kit.SeedProjectionAsync(group);
        var recordId = Guid.NewGuid();
        (await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
                Kit.BackfillItem(recordId, owner, [owner], At, netAmount: 10m))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var joined = await kit.SeedProjectionAsync(group);
        var raw = $"yerel-{Guid.NewGuid():N}";

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, owner, [owner, raw], At, netAmount: 10m)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(new Kit.BackfillResult(recordId, "unchanged"));
        var saved = await kit.RecordAsync(recordId);
        saved!.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo([owner, joined, raw]);
        saved.Members.Should().OnlyContain(m => m.ScopeKey == Kit.Cumulative && m.LicenseId == kit.LicenseId);
    }

    /// <summary>
    /// Sahip özete girmez; asıl kayıt düzeyinde ayrıca denetlenir (bakiye apply'ın A5b deseni). Kayıt t1 sahibiyle yazılır,
    /// sonra t1 t2'ye birleştirilir (kimlik onarımı ya da "@" ikizi). Aynı iş yeniden gönderilir: yanıtı kaybolmuş parti ya
    /// da yedekten dönen kopyanın geçmişi. İstemci t1'i ya da yerel birleştirmeden sonra t2'yi taşıyabilir; ikisi de bugün
    /// aynı asıl kayıttır. Sonuç <c>unchanged</c>, yeniden gönderimin yeni üyeleri eklenir, kayıt yazıldığı andaki sahibi
    /// taşımaya devam eder. Sahip özete girseydi özet zamana bağlı olurdu ve <c>content-conflict</c> dönerdi; bu son
    /// durumdur, istemci öğeyi bir daha göndermez.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sahip_birlestirildikten_sonra_ayni_is_unchanged_yeni_uyeler_eklenir(bool sentAsSurvivor)
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var t1 = await kit.SeedProjectionAsync(null);
        var t2 = await kit.SeedProjectionAsync(null);
        var recordId = Guid.NewGuid();
        var order = Guid.NewGuid();
        (await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
                Kit.BackfillItem(recordId, t1, [t1], At, orderIds: [order], netAmount: 10m, appliedAmount: 2m))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await kit.RecordAsync(recordId);
        await kit.MergeAsync(t1, t2);
        var raw = $"yerel-{Guid.NewGuid():N}";
        var owner = sentAsSurvivor ? t2 : t1;

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, owner, [owner, raw], At, orderIds: [order], netAmount: 10m, appliedAmount: 2m)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(new Kit.BackfillResult(recordId, "unchanged"));
        var saved = await kit.RecordAsync(recordId);
        saved!.OwnerWpfCustomerId.Should().Be(t1, "kayıt yazıldığı andaki asıl kaydı taşır; birleştirme yeniden anahtarlamaz");
        saved.PersonKey.Should().Be(first!.PersonKey);
        saved.RequestHash.Should().Equal(first.RequestHash);
        saved.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo([t1, t2, raw]);
        (await kit.RecordsAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// Sahip denetimi birleştirmeden sonra da gevşemez: kaydın sahibi t1 t2'ye birleştirildi; aynı Id'yi bu kişiyle ilgisi
    /// olmayan bir sahiple getiren öğe <c>content-conflict</c>, kayda üye eklenmez.
    /// </summary>
    [Fact]
    public async Task Sahip_birlestirildikten_sonra_baska_kisi_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var t1 = await kit.SeedProjectionAsync(null);
        var t2 = await kit.SeedProjectionAsync(null);
        var stranger = await kit.SeedProjectionAsync(null);
        var recordId = Guid.NewGuid();
        (await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
                Kit.BackfillItem(recordId, t1, [t1], At, netAmount: 10m))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await kit.RecordAsync(recordId);
        await kit.MergeAsync(t1, t2);

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, stranger, [stranger, t2], At, netAmount: 10m)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(new Kit.BackfillResult(recordId, "content-conflict"));
        Snapshot((await kit.RecordAsync(recordId))!).Should().Be(Snapshot(first!));
    }

    /// <summary>Başka lisansın Id'si ve kiranın Id'si öğe başına <c>content-conflict</c>; o kayıtlar değişmez.</summary>
    [Fact]
    public async Task Baska_lisansin_ve_kiranin_Idsi_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var second = await kit.Sales.SecondLicenseAsync(SalesSyncMode.Off);
        var owner = Kit.NewId();
        var foreign = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Record,
            PaymentRequestState.Delivered, At, finishedAt: At, netAmount: 10m, licenseId: second.LicenseId);
        var lease = await kit.SeedRecordAsync("c:" + owner, [owner], PaymentRequestKind.Lease,
            PaymentRequestState.Delivered, At, finishedAt: At, netAmount: 10m);
        var fresh = Guid.NewGuid();

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(foreign.Id, owner, [owner], At, netAmount: 10m),
            Kit.BackfillItem(fresh, owner, [owner], At, netAmount: 10m),
            Kit.BackfillItem(lease.Id, owner, [owner], At, netAmount: 10m)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(
            new Kit.BackfillResult(foreign.Id, "content-conflict"),
            new Kit.BackfillResult(fresh, "applied"),
            new Kit.BackfillResult(lease.Id, "content-conflict"));
        (await kit.RecordAsync(foreign.Id))!.LicenseId.Should().Be(second.LicenseId);
        (await kit.RecordAsync(lease.Id))!.Kind.Should().Be(PaymentRequestKind.Lease);
        (await kit.RecordsAsync()).Select(r => r.Id).Should().BeEquivalentTo([lease.Id, fresh]);
    }

    /// <summary>
    /// §4.3: lisans değiştiren bilgisayar eski geçmişini yeni lisansa da gönderir. <c>recordId</c> deterministiktir (iş
    /// Id'si ve teslim sayısı). Sahip, projeksiyonu olmayan lisansta kendisine çözülür. Bu yüzden öğe öbür lisansta
    /// AYNI özetle kayıtlıdır ve başka kiracının kaydına karşı tek koruma lisans denetimidir: <c>content-conflict</c>.
    /// Öbür lisansın kaydına üye eklenmez, bu lisansa kayıt yazılmaz.
    /// </summary>
    [Fact]
    public async Task Baska_lisansta_ayni_ozetle_kayitli_Id_content_conflict_uye_eklenmez()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var second = await kit.SecondLicenseAsync(SalesSyncMode.Off);
        var owner = Kit.NewId();
        var extra = Kit.NewId();
        var recordId = Guid.NewGuid();
        var foreign = await second.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, owner, [owner], At, netAmount: 10m)));
        (await Kit.ReadBackfillAsync(foreign)).Records.Should().Equal(new Kit.BackfillResult(recordId, "applied"));

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, owner, [owner, extra], At, netAmount: 10m)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(new Kit.BackfillResult(recordId, "content-conflict"));
        var stored = await kit.RecordAsync(recordId);
        stored!.LicenseId.Should().Be(second.LicenseId);
        stored.Members.Select(m => m.WpfCustomerId).Should().Equal(owner);
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>Aynı partide aynı Id iki kez: ikincisi ilkiyle karşılaştırılır (aynı içerik <c>unchanged</c>, farklısı
    /// <c>content-conflict</c>); tek satır yazılır.</summary>
    [Fact]
    public async Task Ayni_partide_ayni_Id_ilkiyle_karsilastirilir()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var owner = Kit.NewId();
        var extra = Kit.NewId();
        var recordId = Guid.NewGuid();

        var resp = await kit.BackfillAsync(Kit.BackfillBody(Guid.NewGuid(),
            Kit.BackfillItem(recordId, owner, [owner], At, netAmount: 10m),
            Kit.BackfillItem(recordId, owner, [owner, extra], At, netAmount: 10m),
            Kit.BackfillItem(recordId, owner, [owner], At, netAmount: 11m)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(
            new Kit.BackfillResult(recordId, "applied"),
            new Kit.BackfillResult(recordId, "unchanged"),
            new Kit.BackfillResult(recordId, "content-conflict"));
        var saved = (await kit.RecordsAsync()).Should().ContainSingle().Subject;
        saved.NetAmount.Should().Be(10m);
        saved.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo([owner, extra]);
    }

    // ── Kapılar ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Baska_yayincinin_lisansi_404_hicbir_sey_yazilmaz()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var stranger = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var owner = Kit.NewId();

        var resp = await stranger.Sales.SendToAsync(kit.BackfillUrl,
            Kit.BackfillBody(Guid.NewGuid(), Kit.BackfillItem(Guid.NewGuid(), owner, [owner], At)));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    public static TheoryData<string, string> InvalidBodies => new()
    {
        { "protokolsuz", "invalid-envelope" },
        { "replicaId-yok", "invalid-envelope" },
        { "kayitlar-yok", "invalid-batch" },
        { "kayitlar-bos", "invalid-batch" },
        { "kayitlar-201", "invalid-batch" },
        { "kayit-null", "invalid-record" },
        { "recordId-yok", "invalid-record" },
        { "recordId-bos", "invalid-record" },
        { "uyeler-yok", "invalid-members" },
        { "uyeler-101", "invalid-members" },
        { "uye-bos-metin", "invalid-members" },
        { "uye-65-karakter", "invalid-members" },
        { "sahip-uyelerde-yok", "invalid-members" },
        { "kapsam-yok", "invalid-scope" },
        { "kapsam-buyuk-harf", "invalid-scope" },
        { "kapsam-bozuk", "invalid-scope" },
        { "kapsam-legacy-bos", "invalid-scope" },
        { "kapsam-129", "invalid-scope" },
        { "siparisler-yok", "invalid-orders" },
        { "siparis-bos-guid", "invalid-orders" },
        { "siparisler-10001", "invalid-orders" },
        { "durum-yok", "invalid-state" },
        { "durum-abandoned", "invalid-state" },
        { "durum-sending", "invalid-state" },
        { "durum-buyuk-harf", "invalid-state" },
        { "net-yok", "invalid-amount" },
        { "net-eksi", "invalid-amount" },
        { "net-3-ondalik", "invalid-amount" },
        { "dusulen-eksi", "invalid-amount" },
        { "bakiye-bayragi-yok", "invalid-amount" },
        { "an-yok", "invalid-at" },
    };

    /// <summary>Her 400 sınıfı, partinin geçerli öğeleri dahil hiçbir şey yazılmaz.</summary>
    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Gecersiz_istek_400_kodlu_hicbir_sey_yazilmaz(string @case, string code)
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var owner = Kit.NewId();
        var valid = Kit.BackfillItem(Guid.NewGuid(), owner, [owner], At, netAmount: 10m);
        var item = Kit.BackfillItem(Guid.NewGuid(), owner, [owner, Kit.NewId()], At, orderIds: [Guid.NewGuid()]);
        var body = Kit.BackfillBody(Guid.NewGuid(), valid, item);
        switch (@case)
        {
            case "protokolsuz": body.Remove("protocol"); break;
            case "replicaId-yok": body.Remove("replicaId"); break;
            case "kayitlar-yok": body.Remove("records"); break;
            case "kayitlar-bos": body["records"] = Array.Empty<object>(); break;
            case "kayitlar-201":
                body["records"] = Enumerable.Range(0, 201)
                    .Select(_ => Kit.BackfillItem(Guid.NewGuid(), owner, [owner], At))
                    .ToArray();
                break;
            case "kayit-null": body["records"] = new object?[] { valid, null }; break;
            case "recordId-yok": item.Remove("recordId"); break;
            case "recordId-bos": item["recordId"] = Guid.Empty; break;
            case "uyeler-yok": item.Remove("memberIds"); break;
            case "uyeler-101":
                item["memberIds"] = Enumerable.Range(0, 100).Select(_ => Kit.NewId()).Append(owner).ToArray();
                break;
            case "uye-bos-metin": item["memberIds"] = new[] { owner, " " }; break;
            case "uye-65-karakter": item["memberIds"] = new[] { owner, new string('a', 65) }; break;
            case "sahip-uyelerde-yok": item["ownerId"] = Kit.NewId(); break;
            case "kapsam-yok": item.Remove("scopeKey"); break;
            case "kapsam-buyuk-harf": item["scopeKey"] = "Legacy"; break;
            case "kapsam-bozuk": item["scopeKey"] = "session:yayin-1"; break;
            case "kapsam-legacy-bos": item["scopeKey"] = "legacy:"; break;
            case "kapsam-129": item["scopeKey"] = "legacy:" + new string('k', 122); break;
            case "siparisler-yok": item.Remove("orderIds"); break;
            case "siparis-bos-guid": item["orderIds"] = new[] { Guid.Empty }; break;
            case "siparisler-10001":
                item["orderIds"] = Enumerable.Range(0, 10_001).Select(_ => Guid.NewGuid()).ToArray();
                break;
            case "durum-yok": item.Remove("state"); break;
            case "durum-abandoned": item["state"] = "abandoned"; break;
            case "durum-sending": item["state"] = "sending"; break;
            case "durum-buyuk-harf": item["state"] = "Delivered"; break;
            case "net-yok": item.Remove("netAmount"); break;
            case "net-eksi": item["netAmount"] = -1m; break;
            case "net-3-ondalik": item["netAmount"] = 1.005m; break;
            case "dusulen-eksi": item["appliedAmount"] = -0.01m; break;
            case "bakiye-bayragi-yok": item.Remove("fullyFromBalance"); break;
            case "an-yok": item.Remove("at"); break;
            default: throw new ArgumentOutOfRangeException(nameof(@case), @case, null);
        }

        var resp = await kit.BackfillAsync(body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be(code, @case);
        (await kit.RecordsAsync()).Should().BeEmpty();
    }

    // ── Çapraz denetim (§10.4-9) ─────────────────────────────────────────────

    /// <summary>
    /// §10.4-9, §6.8: mod açılmadan önce PC-A'dan ödeme istenen kişi. Ws'nin ilk açılışında PC-A'nın geçmişi mod Off'ta
    /// geri doldurulur (teslim edilmiş ve sonucu bilinmeyen birer kayıt; kişinin farklı üyelerinden). Mod açılınca PC-B
    /// aynı kişiye (grubun öbür üyesini sahip yaparak) kümülatif ister: <c>begin</c> iki kaydı PC-A'nın makine adı,
    /// tutarı ve anıyla <c>already-requested</c> sorusuna koyar. Onaylanınca kira açılır. Liste kayıtları sipariş
    /// kümeleriyle döner.
    /// </summary>
    [Fact]
    public async Task Geri_doldurulmus_kayit_mod_Onda_obur_kopyanin_begininde_already_requested()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.Off);
        var group = $"grp-{Guid.NewGuid():N}";
        var tiktok = await kit.SeedProjectionAsync(group);
        var instagram = await kit.SeedProjectionAsync(group);
        var pcA = Guid.NewGuid();
        var pcB = Guid.NewGuid();
        await kit.SeedReplicaAsync(pcA, "PC-A");
        await kit.SeedReplicaAsync(pcB, "PC-B");
        var delivered = Guid.NewGuid();
        var unconfirmed = Guid.NewGuid();
        var k1 = Guid.NewGuid();
        var k2 = Guid.NewGuid();
        var backfill = await kit.BackfillAsync(Kit.BackfillBody(pcA,
            Kit.BackfillItem(delivered, tiktok, [tiktok], At, orderIds: [k1], netAmount: 350m),
            Kit.BackfillItem(unconfirmed, instagram, [instagram], At.AddDays(1), orderIds: [k2], state: "unconfirmed",
                netAmount: 200m)));
        backfill.StatusCode.Should().Be(HttpStatusCode.OK, await backfill.Content.ReadAsStringAsync());
        await kit.Sales.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId);
            license.SalesSyncMode = SalesSyncMode.On;
            license.SalesSyncModeChangedAt = DateTimeOffset.UtcNow;
        });

        var begin = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), pcB, instagram, [tiktok, instagram]));

        begin.StatusCode.Should().Be(HttpStatusCode.Conflict, await begin.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(begin);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        var priors = problem.GetProperty("priors").EnumerateArray()
            .Select(p => (
                p.GetProperty("id").GetGuid(),
                p.GetProperty("machine").GetString(),
                p.GetProperty("netAmount").GetDecimal(),
                p.GetProperty("at").GetDateTimeOffset(),
                p.GetProperty("unconfirmed").GetBoolean()))
            .ToList();
        priors.Should().Equal(
            (delivered, "PC-A", 350m, At, false),
            (unconfirmed, "PC-A", 200m, At.AddDays(1), true));

        var confirmed = await kit.BeginAsync(Kit.Body(Guid.NewGuid(), pcB, instagram, [tiktok, instagram],
            confirmedPriorIds: [delivered, unconfirmed]));
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());

        var list = await kit.ListAsync(Kit.Cumulative, [instagram]);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        var records = (await Kit.ReadListAsync(list)).Records;
        records.Where(r => r.Kind == "record").Select(r => (r.Id, r.State, r.OrderIds.Single()))
            .Should().BeEquivalentTo([(delivered, "delivered", k1), (unconfirmed, "unconfirmed", k2)]);
    }
}
