using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// Değişiklik akışı sayfasının kurulumu — spec 2026-10-13 §4.4, §10.2 ("akış sayfası kurulumu"). Veritabanı yok:
/// üç kaynağın birleşimi ve yan liste (oturumlar, müşteri taslakları) saf fonksiyonda. Ufuk, kaynak süzgeçleri ve
/// imleç sıfırlaması gerçek SQL Server'da (<c>SalesChangesFeedRelationalTests</c>).
/// </summary>
public sealed class SalesChangesPageBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid LicenseId = Guid.NewGuid();

    // ── Birleşim ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Üç kaynak ChangeSeq sırasıyla birleşir, ilk <c>take</c> öğe döner ve <c>next</c> sonuncunun seq'idir. Her kaynak
    /// kendi içinde <c>take</c> ile kırpılmış gelir (sorgu böyle okur); bu yeterli: bir kaynağın ilk <c>take</c>
    /// satırının dışındaki satır birleşimin ilk <c>take</c> öğesine giremez.
    /// </summary>
    [Fact]
    public void Uc_kaynak_seq_sirasiyla_birlesir_ilk_take_oge_doner()
    {
        var session = Session(seq: 1);
        var orders = new[] { Order(session, seq: 2), Order(session, seq: 3), Order(session, seq: 4) };
        var shipments = new[] { Shipment(seq: 6), Shipment(seq: 7), Shipment(seq: 8) };
        var sessions = new[] { session, Session(seq: 5), Session(seq: 9) };

        var page = Build(afterSeq: 0, sessions, orders, shipments, take: 3);
        page.Items.Select(i => i.ChangeSeq).Should().Equal(1, 2, 3);
        page.Items.Select(i => i.Kind).Should().Equal(
            SalesChangeItem.KindSession, SalesChangeItem.KindOrder, SalesChangeItem.KindOrder);
        page.NextAfterSeq.Should().Be(3);

        var wide = Build(afterSeq: 0, sessions, orders, shipments, take: 7);
        wide.Items.Select(i => i.ChangeSeq).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        wide.Items.Select(i => i.Kind).Should().Equal(
            "session", "order", "order", "order", "session", "shipment", "shipment");
        wide.NextAfterSeq.Should().Be(7);
    }

    /// <summary>Kaynak sırasız gelse de sayfa seq sırasıyla kurulur; her öğede tam olarak bir satır dolu.</summary>
    [Fact]
    public void Ogeler_seq_sirasinda_ve_tek_satirli()
    {
        var session = Session(seq: 30);
        var page = Build(afterSeq: 10,
            [session],
            [Order(session, seq: 20), Order(session, seq: 40)],
            [Shipment(seq: 35), Shipment(seq: 15)],
            take: 500);

        page.Items.Select(i => i.ChangeSeq).Should().BeInAscendingOrder();
        page.Items.Should().OnlyContain(i =>
            (i.Session != null ? 1 : 0) + (i.Order != null ? 1 : 0) + (i.Shipment != null ? 1 : 0) == 1);
        page.Items.Single(i => i.Kind == "session").Session!.Id.Should().Be(session.Id);
        page.NextAfterSeq.Should().Be(40);
    }

    /// <summary>Boş sayfada <c>next</c> istemcinin imlecidir; sıfırlama bayrağı aynen geçer.</summary>
    [Fact]
    public void Bos_sayfada_next_imlectir()
    {
        var page = Build(afterSeq: 1234, [], [], [], take: 500);
        page.Items.Should().BeEmpty();
        page.NextAfterSeq.Should().Be(1234);
        page.CursorReset.Should().BeFalse();
        page.SideSessions.Should().BeEmpty();
        page.CustomerDrafts.Should().BeEmpty();

        var reset = SalesChangesPageBuilder.Assemble(
            0, cursorReset: true, [], new Dictionary<Guid, StreamSession>(), SalesCustomerDirectory.Empty);
        reset.CursorReset.Should().BeTrue();
        reset.NextAfterSeq.Should().Be(0);
    }

    // ── Yan liste: oturumlar ─────────────────────────────────────────────────

    /// <summary>
    /// Sayfadaki her siparişin oturumu yan listede bir kez, güncel hâliyle; ana listede de olsa yan listede yine
    /// vardır (istemci FK'yı sayfa içinde yan listeden sağlar). Oturumu bulunamayan sipariş yine verilir; kargo yan
    /// listeye bir şey eklemez. Yan liste imleci etkilemez.
    /// </summary>
    [Fact]
    public void Yan_liste_siparislerin_oturumlarini_bir_kez_tasir()
    {
        var a = Session(seq: 1);
        var b = Session(seq: 900);   // sayfanın dışında (siparişlerinden sonra değişmiş)
        var lost = Session(seq: 0);  // yan okumada bulunamadı
        var orders = new[]
        {
            Order(a, seq: 2), Order(a, seq: 3), Order(b, seq: 4), Order(b, seq: 5), Order(lost, seq: 6),
        };
        var selected = SalesChangesPageBuilder.Select([a], orders, [Shipment(seq: 7)], take: 500);

        var page = SalesChangesPageBuilder.Assemble(
            0, false, selected,
            new Dictionary<Guid, StreamSession> { [a.Id] = a, [b.Id] = b },
            SalesCustomerDirectory.Empty);

        page.SideSessions.Select(s => s.Id).Should().Equal(a.Id, b.Id);
        page.SideSessions.Single(s => s.Id == b.Id).ChangeSeq.Should().Be(900);
        page.Items.Count(i => i.Kind == "order").Should().Be(5, "oturumu bulunamayan sipariş zehirlenmez");
        page.NextAfterSeq.Should().Be(7, "yan liste imleci etkilemez");
    }

    /// <summary>Yan liste yalnız sayfaya giren siparişler için okunur.</summary>
    [Fact]
    public void Yan_liste_yalniz_sayfaya_giren_siparisler_icin()
    {
        var a = Session(seq: 1);
        var b = Session(seq: 1);
        var selected = SalesChangesPageBuilder.Select([], [Order(a, seq: 2), Order(b, seq: 3)], [], take: 1);

        SalesChangesPageBuilder.OrdersOf(selected).Select(o => o.SessionId).Should().Equal(a.Id);
    }

    // ── Yan liste: müşteri taslakları ────────────────────────────────────────

    /// <summary>
    /// Kopyanın Id'siyle kalmış sipariş asıl kayda çözülür: öğenin CustomerId'si ve taslağın Id'si asıl kaydın
    /// hex'idir; taslak asıl kaydın kimliğini taşır. Zincir (kopyanın kopyası) de çözülür.
    /// </summary>
    [Fact]
    public void Kopyadaki_siparis_asil_kaydin_taslagiyla_gelir()
    {
        var canonical = Projection("youtube", "asil-kisi", "Asıl Ad");
        var copy = Projection("youtube", "eski-ad", null) with { MergedIntoId = canonical.Id };
        var copyOfCopy = Projection("youtube", "daha-eski", null) with { MergedIntoId = copy.Id };
        var session = Session(seq: 1);
        var direct = Order(session, seq: 2, customerId: Hex(copy.Id));
        var chained = Order(session, seq: 3, customerId: Hex(copyOfCopy.Id));

        var page = Build(0, [], [direct, chained], [], take: 500,
            customers: new SalesCustomerDirectory([canonical, copy, copyOfCopy]));

        page.Items.Select(i => i.Order!.CustomerId).Should().Equal(Hex(canonical.Id), Hex(canonical.Id));
        var draft = page.CustomerDrafts.Should().ContainSingle().Subject;
        draft.Should().Be(new SalesCustomerDraft(Hex(canonical.Id), "youtube", "asil-kisi", "Asıl Ad", null));
    }

    /// <summary>
    /// CustomerId'si GUID olmayan siparişin projeksiyonu yoktur: taslak siparişin kendi alanlarından, Id ham değer.
    /// Aynı müşterinin sayfada birden çok siparişi varsa taslak bir kez, sonuncusundan.
    /// </summary>
    [Fact]
    public void Guid_olmayan_musteri_taslagi_siparisten_kurulur()
    {
        var session = Session(seq: 1);
        var raw = $"legacy-{Guid.NewGuid():N}";
        var first = Order(session, seq: 2, customerId: raw);
        first.DisplayName = "Eski takma ad";
        var last = Order(session, seq: 3, customerId: raw);
        last.DisplayName = "Yeni takma ad";

        var page = Build(0, [], [first, last], [], take: 500);

        page.Items.Select(i => i.Order!.CustomerId).Should().Equal(raw, raw);
        page.CustomerDrafts.Should().ContainSingle().Which.Should().Be(
            new SalesCustomerDraft(raw, last.Platform, last.Username, "Yeni takma ad", null));
    }

    /// <summary>
    /// Eksik müşteri öğeyi zehirlemez (2026-08-14 dersi): GUID'li ama projeksiyonu sunucuya hiç gelmemiş müşterinin
    /// siparişi verilir, taslağı siparişten kurulur; aynı sayfadaki öbür öğeler etkilenmez.
    /// </summary>
    [Fact]
    public void Projeksiyonu_olmayan_musteri_sayfayi_bozmaz()
    {
        var known = Projection("tiktok", "bilinen", "Bilinen");
        var session = Session(seq: 1);
        var missingHex = Hex(Guid.NewGuid());
        var orphan = Order(session, seq: 2, customerId: missingHex);
        var fine = Order(session, seq: 3, customerId: Hex(known.Id));

        var page = Build(0, [session], [orphan, fine], [Shipment(seq: 4)], take: 500,
            customers: new SalesCustomerDirectory([known]));

        page.Items.Select(i => i.ChangeSeq).Should().Equal(1, 2, 3, 4);
        page.Items.Single(i => i.Order?.Id == orphan.Id).Order!.CustomerId.Should().Be(missingHex);
        page.CustomerDrafts.Should().BeEquivalentTo(new[]
        {
            new SalesCustomerDraft(missingHex, orphan.Platform, orphan.Username, orphan.DisplayName, null),
            new SalesCustomerDraft(Hex(known.Id), "tiktok", "bilinen", "Bilinen", null),
        });
    }

    /// <summary>Asıl kaydı silinmiş müşterinin taslağı <c>PurgedAt</c>'i taşır (istemci mezar taşı boşaltmasından
    /// geçirir, §9).</summary>
    [Fact]
    public void Silinmis_asil_kaydin_taslagi_PurgedAt_tasir()
    {
        var purgedAt = T0.AddDays(-1);
        var purged = Projection("youtube", "silinen", null) with { PurgedAt = purgedAt };
        var session = Session(seq: 1);

        var page = Build(0, [], [Order(session, seq: 2, customerId: Hex(purged.Id))], [], take: 500,
            customers: new SalesCustomerDirectory([purged]));

        page.CustomerDrafts.Should().ContainSingle().Which.PurgedAt.Should().Be(purgedAt);
    }

    // ── KVKK okuma maskesi (§9) ──────────────────────────────────────────────

    /// <summary>
    /// Asıl projeksiyonu silinmiş müşterinin siparişi okuma anında metinsiz iner: <c>DisplayName = null</c>,
    /// <c>MessageText = ""</c>. Bu bir yazım değildir (<c>TextScrubbedAt</c> boş kalır, satır değişmez); mali kayıt
    /// alanları (kullanıcı adı, kod, fiyat) kalır. Kopyanın Id'siyle kalmış sipariş de asıl kayda göre maskelenir;
    /// taslak, projeksiyonda takma ad kalmış olsa bile takma adsız gider. Silinmemiş müşterinin metni aynen gider.
    /// </summary>
    [Fact]
    public void Silinmis_musterinin_siparisi_metinsiz_iner()
    {
        var purgedAt = T0.AddDays(-1);
        var purged = Projection("youtube", "silinen", "Kalmış takma ad") with { PurgedAt = purgedAt };
        var copy = Projection("youtube", "eski", null) with { MergedIntoId = purged.Id };
        var alive = Projection("youtube", "yasayan", "Yaşayan");
        var session = Session(seq: 1);
        var direct = Order(session, seq: 2, customerId: Hex(purged.Id));
        var viaCopy = Order(session, seq: 3, customerId: Hex(copy.Id));
        var kept = Order(session, seq: 4, customerId: Hex(alive.Id));

        var page = Build(0, [], [direct, viaCopy, kept], [], take: 500,
            customers: new SalesCustomerDirectory([purged, copy, alive]));

        var items = page.Items.Select(i => i.Order!).ToDictionary(o => o.Id);
        foreach (var masked in new[] { items[direct.Id], items[viaCopy.Id] })
        {
            masked.DisplayName.Should().BeNull();
            masked.MessageText.Should().BeEmpty();
            masked.TextScrubbedAt.Should().BeNull("okuma anı maskesi satıra yazmaz");
            masked.Username.Should().NotBeNullOrEmpty();
            masked.Code.Should().Be("A1");
            masked.Price.Should().Be(100m);
        }
        direct.DisplayName.Should().Be("Takma ad", "sunucu satırı değişmez");
        items[kept.Id].DisplayName.Should().Be("Takma ad");
        items[kept.Id].MessageText.Should().Be("A1");
        page.CustomerDrafts.Single(d => d.Id == Hex(purged.Id)).DisplayName.Should().BeNull();
        page.CustomerDrafts.Single(d => d.Id == Hex(alive.Id)).DisplayName.Should().Be("Yaşayan");
    }

    // ── Asıl kayıt çözümü ─────────────────────────────────────────────────────

    /// <summary>Asıl kayıt Id'si değişmiyorsa ham yazım korunur (CustomerIdResolver ile aynı); zincir en çok
    /// <see cref="SalesCustomerDirectory.MaxHops"/> adım izlenir.</summary>
    [Fact]
    public void Cozum_ham_yazimi_korur_ve_adim_sinirina_uyar()
    {
        var self = Projection("youtube", "kendisi", null);
        var upper = self.Id.ToString("N").ToUpperInvariant();
        var directory = new SalesCustomerDirectory([self]);
        directory.Resolve(upper).Id.Should().Be(upper);
        directory.Resolve(upper).Canonical.Should().Be(self);

        // 5 halkalı zincir: c0 → c1 → c2 → c3 → c4. Üç adım c3'te durur.
        var chain = Enumerable.Range(0, 5).Select(_ => Projection("youtube", $"z-{Guid.NewGuid():N}", null)).ToList();
        for (var i = 0; i < 4; i++) chain[i] = chain[i] with { MergedIntoId = chain[i + 1].Id };
        new SalesCustomerDirectory(chain).Resolve(Hex(chain[0].Id)).Id
            .Should().Be(Hex(chain[SalesCustomerDirectory.MaxHops].Id));
    }

    /// <summary>
    /// Yükleme kopyaları açıkça ister (varsayılan sorgu filtresi onları gizler) ve zincirin sonundaki asıl kaydı da
    /// getirir; başka lisansın satırı yüklenmez. InMemory sorgu filtresini uygular, bu yüzden burada sınanır.
    /// </summary>
    [Fact]
    public async Task Yukleme_kopyalari_ve_asil_kaydi_getirir_lisans_sinirini_korur()
    {
        await using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"sales-dir-{Guid.NewGuid():N}").Options);
        var otherLicense = Guid.NewGuid();
        var canonical = Entity(LicenseId, "youtube", "asil", "Asıl");
        var copy = Entity(LicenseId, "youtube", "kopya", null, mergedInto: canonical.Id);
        var copyOfCopy = Entity(LicenseId, "youtube", "kopya2", null, mergedInto: copy.Id);
        var foreign = Entity(otherLicense, "youtube", "yabanci", "Yabancı");
        db.WpfCustomerProjections.AddRange(canonical, copy, copyOfCopy, foreign);
        await db.SaveChangesAsync();

        var directory = await SalesCustomerDirectory.LoadAsync(db, LicenseId,
            [Hex(copyOfCopy.Id), Hex(foreign.Id), "guid-degil", Hex(copyOfCopy.Id)], CancellationToken.None);

        var resolved = directory.Resolve(Hex(copyOfCopy.Id));
        resolved.Id.Should().Be(Hex(canonical.Id));
        resolved.Canonical!.Username.Should().Be("asil");
        directory.Resolve(Hex(foreign.Id)).Canonical.Should().BeNull("başka lisansın satırı yüklenmez");
        directory.Resolve("guid-degil").Should().Be(new SalesCustomerResolution("guid-degil", null));
    }

    // ── Yardımcılar ──────────────────────────────────────────────────────────

    private static SalesChangesPage Build(
        long afterSeq,
        IReadOnlyList<StreamSession> sessions,
        IReadOnlyList<Order> orders,
        IReadOnlyList<Shipment> shipments,
        int take,
        SalesCustomerDirectory? customers = null)
    {
        var selected = SalesChangesPageBuilder.Select(sessions, orders, shipments, take);
        var orderSessions = sessions.ToDictionary(s => s.Id);
        return SalesChangesPageBuilder.Assemble(
            afterSeq, false, selected, orderSessions, customers ?? SalesCustomerDirectory.Empty);
    }

    private static string Hex(Guid id) => id.ToString("N");

    private static SalesCustomerRow Projection(string platform, string username, string? displayName) =>
        new(Guid.NewGuid(), null, platform, username, displayName, null);

    private static WpfCustomerProjection Entity(
        Guid licenseId, string platform, string username, string? displayName, Guid? mergedInto = null) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        Platform = platform,
        Username = username,
        DisplayName = displayName,
        MergedIntoId = mergedInto,
        UpdatedAt = T0,
    };

    private static StreamSession Session(long seq) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = LicenseId,
        Title = "Akşam yayını",
        StartedAt = T0,
        EndedAt = T0.AddHours(2),
        ReleasedAt = T0.AddHours(2),
        Platforms = "youtube",
        UpdatedAt = T0.AddHours(2),
        ChangeSeq = seq,
    };

    private static Order Order(StreamSession session, long seq, string? customerId = null) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = LicenseId,
        SessionId = session.Id,
        SessionReleasedAt = session.ReleasedAt,
        CustomerId = customerId ?? Hex(Guid.NewGuid()),
        Platform = "youtube",
        Username = $"u-{Guid.NewGuid():N}",
        DisplayName = "Takma ad",
        MessageText = "A1",
        Code = "A1",
        Price = 100m,
        AddedAt = T0.AddMinutes(5),
        UpdatedAt = T0.AddMinutes(5),
        ChangeSeq = seq,
    };

    private static Shipment Shipment(long seq) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = LicenseId,
        CustomerId = Hex(Guid.NewGuid()),
        Status = ShipmentStatus.Held,
        CumulativeAmount = 250m,
        CreatedAt = T0,
        UpdatedAt = T0,
        ChangeSeq = seq,
    };
}
