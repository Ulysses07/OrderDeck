using System.Net;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PaymentRequestTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST payment-requests/begin</c>'in eşzamanlılık ve harman sözleşmesi gerçek SQL Server'da — spec 2026-10-13 §6.4,
/// DM6, M10, §10.1-8, -15, -16, -17, -18, -28 (begin). Kişi×kapsam ve üye×kapsam uygulama kilitleri, filtreli tekil
/// indeks (yarışın hakemi) ve SQL'in harf duyarsız, sondaki boşluğu yok sayan karşılaştırması InMemory'de yoktur.
///
/// <para>Yarış sırayla kurulur (zamanlamayla değil): bir isteğin kira kaydı diske inmeden hemen önce kanca rakip isteği
/// BAŞLATIR ama sonunu beklemez — rakip ilk isteğin kilidinde bekliyor olabilir. Kanca rakip kira yazımına gelene, ya da
/// rakibin kilitte beklediği SQL Server'da görülene (<see cref="AppLockWaiterProbe"/>) kadar bekler; üst sınır kilit
/// bekleme sınırının altındadır. Bekleme sabit bir süreyle varsayılsaydı tam paketin yükü altında rakip kilit sınırını
/// aşıp zaman aşımı yolunu koşardı. Kilit olmasaydı rakip ilk isteğin yazımından önce bütün denetimlerini geçerdi.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PaymentRequestBeginRelationalTests : IAsyncLifetime
{
    /// <summary>Kancanın rakibi bekleme üst sınırı; kilit bekleme sınırının (<see cref="SalesCommandLock.DefaultTimeout"/>,
    /// 5 sn) altında. Olağan durumda kanca rakibin kilitte beklediğini görür görmez bırakır.</summary>
    private static readonly TimeSpan RivalWait = TimeSpan.FromSeconds(4);

    private readonly SqlServerContainerFixture _sql;
    private string _connectionString = null!;
    private HookFactory _factory = null!;
    private Kit _kit = null!;
    private readonly Guid _pcA = Guid.NewGuid();
    private readonly Guid _pcB = Guid.NewGuid();

    public PaymentRequestBeginRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connectionString = await _sql.CreateDatabaseAsync();
        _factory = new HookFactory(_connectionString);
        _kit = await Kit.CreateAsync(_factory);
        await _kit.SeedReplicaAsync(_pcA, "PC-A");
        await _kit.SeedReplicaAsync(_pcB, "PC-B");
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _kit.Sales.Client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── §10.1-8: iki kopyadan aynı kişi×kapsama eşzamanlı begin ───────────────

    /// <summary>
    /// §10.1-8, DM6: iki kopya aynı kişi×kapsama eşzamanlı <c>begin</c> gönderir. Tam olarak biri 200, öbürü 409
    /// <c>in-progress-elsewhere</c> (kazananın makinesi ve kaydıyla); tek Sending satırı, kaybedenin satırı yok.
    /// </summary>
    [Fact]
    public async Task Ayni_kisi_kapsama_eszamanli_iki_begin_biri_200_obur_409_in_progress_elsewhere()
    {
        var owner = Kit.NewId();
        var aRecord = Guid.NewGuid();
        var bRecord = Guid.NewGuid();
        _factory.Hook.Arm(() => _kit.BeginAsync(Kit.Body(bRecord, _pcB, owner, [owner])), RivalWait);

        var first = await _kit.BeginAsync(Kit.Body(aRecord, _pcA, owner, [owner]));
        var rival = await _factory.Hook.RivalAsync();

        await ShouldBeOneLeaseAndOneElsewhereAsync(first, aRecord, "PC-A", rival, bRecord, "PC-B");
    }

    /// <summary>
    /// §10.1-8, M10: grup senkronu gecikmiş — PC-A paylaşılan üyeyi G1 grubunda, PC-B G2 grubundaki başka bir sahiple
    /// görüyor. PersonKey'ler farklı (iki ayrı indeks satırı, iki ayrı kişi kilidi); ortak üyenin kilidi
    /// (<c>payreq-m</c>) iki isteği sıraya sokar ve ikincisi kirayı üye kesişimiyle bulur: tam olarak biri 200, öbürü 409
    /// <c>in-progress-elsewhere</c>, tek Sending satırı.
    /// </summary>
    [Fact]
    public async Task Farkli_GroupId_ortak_uye_eszamanli_iki_begin_uye_kilidiyle_biri_409()
    {
        var g1 = $"grp-{Guid.NewGuid():N}";
        var ownerA = await _kit.SeedProjectionAsync(g1);
        var shared = await _kit.SeedProjectionAsync(g1);
        var ownerB = await _kit.SeedProjectionAsync($"grp-{Guid.NewGuid():N}");
        var aRecord = Guid.NewGuid();
        var bRecord = Guid.NewGuid();
        _factory.Hook.Arm(() => _kit.BeginAsync(Kit.Body(bRecord, _pcB, ownerB, [ownerB, shared])), RivalWait);

        var first = await _kit.BeginAsync(Kit.Body(aRecord, _pcA, ownerA, [ownerA, shared]));
        var rival = await _factory.Hook.RivalAsync();

        await ShouldBeOneLeaseAndOneElsewhereAsync(first, aRecord, "PC-A", rival, bRecord, "PC-B");
    }

    // ── §10.1-15: süresi dolan kira ──────────────────────────────────────────

    /// <summary>
    /// §10.1-15: kira doldu (LeaseUntil SQL ile geçmişe alındı), <c>finish</c> gelmedi. Öbür kopyanın <c>begin</c>'i kaydı
    /// Unconfirmed yapar (Version + 1, FinishedAt = kira sonu, koşullu UPDATE'te) ve <c>already-requested</c>'ı sonucu
    /// bilinmeyen olarak alır: istek anı ve istenen tutarla. Yazım retten sonra da kalır.
    /// </summary>
    [Fact]
    public async Task Suresi_dolan_kira_obur_kopyanin_begininde_Unconfirmed_ve_already_requested()
    {
        var owner = Kit.NewId();
        var order = await _kit.SeedOrderAsync(owner, 550m);
        var lease = await BeginOkAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner], orderIds: [order.Id],
            productTotal: 550m));
        var until = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _kit.Sales.ReadDbAsync(db => db.PaymentRequestRecords
            .Where(r => r.Id == lease.RecordId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseUntil, until)));

        var resp = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcB, owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        var prior = problem.GetProperty("priors").EnumerateArray().Should().ContainSingle().Subject;
        prior.GetProperty("id").GetGuid().Should().Be(lease.RecordId);
        prior.GetProperty("unconfirmed").GetBoolean().Should().BeTrue();
        prior.GetProperty("machine").GetString().Should().Be("PC-A");
        var saved = await _kit.RecordAsync(lease.RecordId);
        saved!.State.Should().Be(PaymentRequestState.Unconfirmed);
        saved.Version.Should().Be(1);
        saved.FinishedAt.Should().Be(until);
        prior.GetProperty("at").GetDateTimeOffset().Should().Be(saved.StartedAt);
        prior.GetProperty("finishedAt").GetDateTimeOffset().Should().Be(until);
        prior.GetProperty("totalBeforeBalance").GetDecimal().Should().Be(550m);
        prior.GetProperty("netAmount").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        (await _kit.RecordsAsync()).Should().ContainSingle("ret kira yazmaz");
    }

    // ── §10.1-16: yeniden oynatma, aynı kopyanın yeni isteği, tekil ihlal, özet ─

    /// <summary>§10.1-16: aynı <c>recordId</c> ve içerik kayıtlı sonucu döner; ikinci satır yok.</summary>
    [Fact]
    public async Task Yeniden_oynatma_kayitli_sonucu_doner()
    {
        var owner = Kit.NewId();
        var order = await _kit.SeedOrderAsync(owner, 100m);
        var body = Kit.Body(Guid.NewGuid(), _pcA, owner, [owner], orderIds: [order.Id], productTotal: 100m);
        var first = await BeginOkAsync(body);

        var again = await BeginOkAsync(body);

        again.Should().BeEquivalentTo(first);
        (await _kit.RecordsAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// §10.1-16, §6.4-c: aynı kopya aynı kişi×kapsamda yeni bir <c>recordId</c> ile başlar — eski Sending Unconfirmed olur
    /// ve <c>already-requested</c> sorulur; o önceki kayıt onaylanınca yeni kira yazılır. Tek Sending satırı kalır.
    /// </summary>
    [Fact]
    public async Task Ayni_kopyanin_yeni_istegi_eskiyi_Unconfirmed_yapar_onaylaninca_200()
    {
        var owner = Kit.NewId();
        var old = await BeginOkAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner]));

        var resp = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        problem.GetProperty("priors").EnumerateArray().Select(p => p.GetProperty("id").GetGuid())
            .Should().Equal(old.RecordId);
        var closed = await _kit.RecordAsync(old.RecordId);
        closed!.State.Should().Be(PaymentRequestState.Unconfirmed);
        closed.FinishedAt.Should().BeBefore(closed.LeaseUntil!.Value, "süresi dolmamış kira yeniden başlama anında kapanır");

        var renewed = await BeginOkAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner], confirmedPriorIds: [old.RecordId]));

        var sending = (await _kit.RecordsAsync()).Where(r => r.State == PaymentRequestState.Sending).ToList();
        sending.Should().ContainSingle().Which.Id.Should().Be(renewed.RecordId);
    }

    /// <summary>
    /// §10.1-16, §6.4-h, DM6: kira yazımı diske inmeden hemen önce aynı kişi×kapsama doğrudan SQL ile bir Sending satırı
    /// eklenir (kilidi atlayan bir yazıcı). Filtreli tekil indeks yazımı reddeder: 500 değil 409
    /// <c>in-progress-elsewhere</c>, kazananın Id'si ve makinesiyle; işlem tümüyle geri alınır — kaybedenin kaydı, üye ve
    /// sipariş satırları kalmaz.
    /// </summary>
    [Fact]
    public async Task Tekil_indeks_ihlali_409_in_progress_elsewhere_yarim_yazim_kalmaz()
    {
        var owner = Kit.NewId();
        var recordId = Guid.NewGuid();
        var injected = Guid.NewGuid();
        var order = await _kit.SeedOrderAsync(owner, 100m);
        _factory.Hook.ArmInjection(() => InsertSendingAsync(injected, "c:" + owner, Kit.Cumulative, "PC-X"));

        var resp = await _kit.BeginAsync(Kit.Body(recordId, _pcA, owner, [owner], orderIds: [order.Id],
            productTotal: 100m));

        _factory.Hook.Injected.Should().BeTrue("kanca satırı kira yazımından önce eklemeli");
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("in-progress-elsewhere");
        problem.GetProperty("recordId").GetGuid().Should().Be(injected);
        problem.GetProperty("machine").GetString().Should().Be("PC-X");
        (await _kit.RecordAsync(recordId)).Should().BeNull();
        (await _kit.ChildRowsAsync(recordId)).Should().Be((0, 0), "yarım üye ya da sipariş satırı kalmaz");
        (await _kit.RecordsAsync()).Should().ContainSingle().Which.Id.Should().Be(injected);
    }

    /// <summary>
    /// §10.1-16, §15-20: PersonKey'leri yalnız harf ya da sondaki boşlukla ayrılan kişiler (gruplu: GroupId; GUID'siz: ham
    /// Id) birbirini engellemez — dördü de 200, dört Sending satırı. SQL'in harf duyarsız ve sondaki boşluğu yok sayan
    /// karşılaştırması ne grubu (yanlış <c>members-stale</c>), ne üye kesişimini (yanlış <c>in-progress-elsewhere</c>), ne
    /// tekil indeksi karıştırır.
    /// </summary>
    [Fact]
    public async Task Harf_ya_da_sondaki_boslukla_ayrilan_kisiler_birbirini_engellemez()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var people = new List<(string Owner, string Key)>();
        foreach (var group in new[] { $"{prefix}-Ab", $"{prefix}-ab", $"{prefix}-x", $"{prefix}-x " })
            people.Add((await _kit.SeedProjectionAsync(group), "g:" + group));
        foreach (var raw in new[] { $"{prefix}-Cd", $"{prefix}-cd", $"{prefix}-y", $"{prefix}-y " })
            people.Add((raw, "r:" + raw));

        foreach (var (owner, _) in people)
            await BeginOkAsync(Kit.Body(Guid.NewGuid(), Guid.NewGuid(), owner, [owner]));

        var records = await _kit.RecordsAsync();
        records.Should().HaveCount(people.Count).And.OnlyContain(r => r.State == PaymentRequestState.Sending);
        records.Select(r => r.PersonKey).Should().BeEquivalentTo(people.Select(p => p.Key));
    }

    /// <summary>
    /// Aynı <c>recordId</c> iki ayrı kişi için eşzamanlı (kilitleri farklı, ikisi de denetimleri geçer): ikinci ekleme
    /// birincil anahtara çarpar — 500 değil, yeniden okunur ve içerik farklı olduğu için 409 <c>content-conflict</c>. Tek
    /// satır.
    /// </summary>
    [Fact]
    public async Task Ayni_recordId_eszamanli_farkli_icerik_biri_409_content_conflict()
    {
        var recordId = Guid.NewGuid();
        var ownerB = Kit.NewId();
        _factory.Hook.Arm(() => _kit.BeginAsync(Kit.Body(recordId, _pcB, ownerB, [ownerB])), RivalWait);
        var ownerA = Kit.NewId();

        var first = await _kit.BeginAsync(Kit.Body(recordId, _pcA, ownerA, [ownerA]));
        var rival = await _factory.Hook.RivalAsync();

        var statuses = new[] { first.StatusCode, rival.StatusCode };
        var bodies = $"{await first.Content.ReadAsStringAsync()} | {await rival.Content.ReadAsStringAsync()}";
        statuses.Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], bodies);
        var loser = first.StatusCode == HttpStatusCode.Conflict ? first : rival;
        (await SalesKit.ProblemAsync(loser)).GetProperty("title").GetString().Should().Be("content-conflict");
        (await _kit.RecordsAsync()).Should().ContainSingle().Which.Id.Should().Be(recordId);
    }

    /// <summary>
    /// Bir kişinin iki GUID'siz üyesi yalnız harfle ya da sondaki boşlukla ayrılıyor: ordinal iki ayrı üye, ama üye
    /// tablosunun birincil anahtarı varsayılan harmanda onları eşit sayar. İstek 500 değil 200 alır.
    /// </summary>
    [Fact]
    public async Task Harmanda_esit_iki_GUIDsiz_uye_500_vermez()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var owner = $"{prefix}-Ab";

        var lease = await BeginOkAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner, $"{prefix}-ab", $"{prefix}-Ab "]));

        (await _kit.RecordAsync(lease.RecordId))!.Members.Should().ContainSingle();
    }

    // ── §10.1-17: members-stale ve üye kesişimiyle önceki kayıt ───────────────

    /// <summary>§10.1-17: <c>memberIds</c> sunucu grubunun bir üyesini içermiyor — 409 <c>members-stale</c>; kira yok.</summary>
    [Fact]
    public async Task Sunucu_grubunun_uyesini_icermeyen_istek_409_members_stale()
    {
        var group = $"grp-{Guid.NewGuid():N}";
        var owner = await _kit.SeedProjectionAsync(group);
        var missing = await _kit.SeedProjectionAsync(group);

        var resp = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("members-stale");
        problem.GetProperty("members").EnumerateArray().Select(m => m.GetString()).Should().Contain(missing);
        (await _kit.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// §10.1-17, §6.4-e: başka bir PersonKey ile yazılmış teslim kaydı (grup senkronu gecikmişken başka bilgisayarın
    /// kurduğu kişi) paylaşılan üye üzerinden bulunur — 409 <c>already-requested</c>.
    /// </summary>
    [Fact]
    public async Task Baska_PersonKey_ile_teslim_kaydi_ortak_uyeyle_bulunur()
    {
        var owner = Kit.NewId();
        var shared = Kit.NewId();
        var otherOwner = Kit.NewId();
        var delivered = await _kit.SeedRecordAsync("c:" + otherOwner, [otherOwner, shared], PaymentRequestKind.Lease,
            PaymentRequestState.Delivered, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-59),
            netAmount: 550m, machineName: "PC-A");

        var resp = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcB, owner, [owner, shared]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        var prior = problem.GetProperty("priors").EnumerateArray().Should().ContainSingle().Subject;
        prior.GetProperty("id").GetGuid().Should().Be(delivered.Id);
        prior.GetProperty("netAmount").GetDecimal().Should().Be(550m);
        prior.GetProperty("unconfirmed").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// §6.4-e: birleştirmeden önce yazılmış teslim kaydı (PersonKey <c>c:t1</c>, üye <c>t1</c>) t1 t2'ye
    /// birleştirildikten sonra SQL Server'da da bulunur: t2 ile gelen begin 409 <c>already-requested</c>, liste kaydı
    /// döner.
    /// </summary>
    [Fact]
    public async Task Birlesmeden_once_yazilmis_kayit_asil_kayitla_bulunur()
    {
        var t1 = await _kit.SeedProjectionAsync(groupId: null);
        var t2 = await _kit.SeedProjectionAsync(groupId: null);
        var delivered = await _kit.SeedRecordAsync("c:" + t1, [t1], PaymentRequestKind.Lease,
            PaymentRequestState.Delivered, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-59),
            netAmount: 550m, machineName: "PC-A");
        await _kit.MergeAsync(t1, into: t2);

        var resp = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcB, t2, [t2]));
        var list = await _kit.ListAsync(Kit.Cumulative, [t2]);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        problem.GetProperty("priors").EnumerateArray().Select(p => p.GetProperty("id").GetGuid())
            .Should().Equal(delivered.Id);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        (await Kit.ReadListAsync(list)).Records.Select(r => r.Id).Should().Equal(delivered.Id);
    }

    /// <summary>
    /// §6.4-d, e: kayıtlar SQL Server'da PersonKey özetiyle de bulunur (üye kümesi ayrık): canlı kira 409
    /// <c>in-progress-elsewhere</c>, teslim kaydı 409 <c>already-requested</c>.
    /// </summary>
    [Fact]
    public async Task Uye_kumesi_ayrik_kayitlar_PersonKey_ozetiyle_bulunur()
    {
        var group = $"grp-{Guid.NewGuid():N}";
        var owner = await _kit.SeedProjectionAsync(group);
        var sending = await _kit.SeedRecordAsync("g:" + group, [$"yerel-{Guid.NewGuid():N}"], PaymentRequestKind.Lease,
            PaymentRequestState.Sending, DateTimeOffset.UtcNow, machineName: "PC-A");

        var live = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcB, owner, [owner]));

        live.StatusCode.Should().Be(HttpStatusCode.Conflict, await live.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(live);
        problem.GetProperty("title").GetString().Should().Be("in-progress-elsewhere");
        problem.GetProperty("recordId").GetGuid().Should().Be(sending.Id);

        await _kit.Sales.ReadDbAsync(db => db.PaymentRequestRecords.Where(r => r.Id == sending.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, PaymentRequestState.Delivered)));
        var delivered = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcB, owner, [owner]));

        delivered.StatusCode.Should().Be(HttpStatusCode.Conflict, await delivered.Content.ReadAsStringAsync());
        var asked = await SalesKit.ProblemAsync(delivered);
        asked.GetProperty("title").GetString().Should().Be("already-requested");
        asked.GetProperty("priors").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).Should().Equal(sending.Id);
    }

    // ── §10.1-18: stale-total küme karşılaştırması ve üye eşlemesi ──────────

    /// <summary>
    /// §10.1-18, §6.4-f: toplamlar eşit, kümeler farklı — istemcinin saydığı etiket sunucuda iptal, sunucuda istemcinin
    /// görmediği yeni etiket var. 409 <c>stale-total</c> (sunucu toplamı aynı, <c>liveElsewhere</c> boş); kira yok.
    /// </summary>
    [Fact]
    public async Task Esit_toplam_farkli_kume_409_stale_total()
    {
        var owner = Kit.NewId();
        var kept = await _kit.SeedOrderAsync(owner, 100m);
        var cancelled = await _kit.SeedOrderAsync(owner, 100m, shape: o => o.CancelledAt = SalesKit.StartedAt.AddHours(1));
        await _kit.SeedOrderAsync(owner, 100m);

        var resp = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner],
            orderIds: [kept.Id, cancelled.Id], productTotal: 200m));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("stale-total");
        problem.GetProperty("serverTotal").GetDecimal().Should().Be(200m);
        problem.GetProperty("liveElsewhere").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        (await _kit.RecordsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// §10.1-18, §6.4-f, §15-10: GUID'siz üyenin siparişi ham <c>CustomerId</c> ile sayılır; ham Id'si yalnız harfle ya
    /// da sondaki boşlukla ayrılan sipariş (SQL'in harmanında eşit) sayılmaz. GUID'li sahibin kopyasının Id'siyle
    /// saklanmış sipariş asıl kayıt üzerinden sayılır (kopya araması SQL'e çevrilir). Onları sayan istek 409
    /// <c>stale-total</c>, saymayan 200.
    /// </summary>
    [Fact]
    public async Task GUIDsiz_uyenin_siparisi_ham_Idyle_sayilir_harfi_ya_da_boslugu_farkli_sayilmaz()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var owner = await _kit.SeedProjectionAsync(null);
        var copy = await _kit.SeedProjectionAsync(null, mergedInto: Guid.ParseExact(owner, "N"));
        var raw = $"{prefix}-Ab";
        var counted = await _kit.SeedOrderAsync(raw, 70m);
        var viaCopy = await _kit.SeedOrderAsync(copy, 50m);
        var otherCase = await _kit.SeedOrderAsync($"{prefix}-ab", 30m);
        var trailing = await _kit.SeedOrderAsync($"{prefix}-Ab ", 20m);

        var resp = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner, raw],
            orderIds: [counted.Id, viaCopy.Id, otherCase.Id, trailing.Id], productTotal: 170m));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("stale-total");
        problem.GetProperty("serverTotal").GetDecimal().Should().Be(120m);

        var lease = await BeginOkAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner, raw],
            orderIds: [counted.Id, viaCopy.Id], productTotal: 120m));
        (await _kit.RecordAsync(lease.RecordId))!.RecordOrders.Select(o => o.OrderId)
            .Should().BeEquivalentTo([counted.Id, viaCopy.Id]);
    }

    // ── §10.1-28 (begin): mod Off ─────────────────────────────────────────────

    /// <summary>§10.1-28, §15-54: mod Off'ta <c>begin</c> SQL Server'da da 409 <c>mode-off</c>; kayıt yok, kalp atışı
    /// yazılır.</summary>
    [Fact]
    public async Task Mod_Off_409_mode_off()
    {
        var off = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var replica = Guid.NewGuid();
        var owner = Kit.NewId();

        var resp = await off.BeginAsync(Kit.Body(Guid.NewGuid(), replica, owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("mode-off");
        (await off.RecordsAsync()).Should().BeEmpty();
        (await off.Sales.ReplicaAsync(replica)).Should().NotBeNull("istek kopyanın kalp atışıdır");
    }

    // ── Kilit zaman aşımı ────────────────────────────────────────────────────

    /// <summary>
    /// Kişi×kapsam kilidi başka bir işlemde tutulurken gelen <c>begin</c> kilit bekleme sınırında 409
    /// <c>in-progress-elsewhere</c> alır (makine bilinmiyor); hiçbir şey yazılmaz.
    /// </summary>
    [Fact]
    public async Task Kilit_zaman_asimi_409_in_progress_elsewhere_makine_bos()
    {
        var owner = $"yerel-{Guid.NewGuid():N}";
        await using var holder = NewDb();
        await using var held = await holder.Database.BeginTransactionAsync();
        await SalesCommandLock.AcquireAsync(
            holder,
            PaymentRequestLeaseService.PersonResource(_kit.LicenseId, PaymentPersonKey.Hash("r:" + owner), Kit.Cumulative),
            TimeSpan.FromSeconds(1),
            default);

        var resp = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcA, owner, [owner]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("in-progress-elsewhere");
        problem.GetProperty("machine").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        (await _kit.RecordsAsync()).Should().BeEmpty();
        await held.RollbackAsync();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private async Task<Kit.BeginResponse> BeginOkAsync(object body)
    {
        var resp = await _kit.BeginAsync(body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return await Kit.ReadAsync(resp);
    }

    /// <summary>İki istekten tam olarak biri kirayı yazdı, öbürü 409 <c>in-progress-elsewhere</c> ile kazananı gördü; tek
    /// Sending satırı.</summary>
    private async Task ShouldBeOneLeaseAndOneElsewhereAsync(
        HttpResponseMessage first, Guid firstRecord, string firstMachine,
        HttpResponseMessage rival, Guid rivalRecord, string rivalMachine)
    {
        var bodies = $"{await first.Content.ReadAsStringAsync()} | {await rival.Content.ReadAsStringAsync()}";
        new[] { first.StatusCode, rival.StatusCode }
            .Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], bodies);
        var firstWon = first.StatusCode == HttpStatusCode.OK;
        var (winnerRecord, winnerMachine) = firstWon ? (firstRecord, firstMachine) : (rivalRecord, rivalMachine);
        var problem = await SalesKit.ProblemAsync(firstWon ? rival : first);
        problem.GetProperty("title").GetString().Should().Be("in-progress-elsewhere");
        problem.GetProperty("recordId").GetGuid().Should().Be(winnerRecord);
        problem.GetProperty("machine").GetString().Should().Be(winnerMachine);

        var records = await _kit.RecordsAsync();
        records.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Id = winnerRecord, State = PaymentRequestState.Sending });
    }

    /// <summary>Kilidi atlayan bir yazıcının Sending satırı (ham SQL, kendi bağlantısında, hemen commit).</summary>
    private async Task InsertSendingAsync(Guid id, string personKey, string scopeKey, string machine)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PaymentRequestRecords
                (Id, LicenseId, PersonKey, PersonKeyHash, ScopeKey, OwnerWpfCustomerId, ReplicaId, MachineName, Kind, State,
                 RequestHash, StartedAt, LeaseUntil)
            VALUES (@id, @license, @key, @hash, @scope, @owner, @replica, @machine, 0, 0, @request, @now, @until)
            """;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@license", _kit.LicenseId);
        cmd.Parameters.AddWithValue("@key", personKey);
        cmd.Parameters.AddWithValue("@hash", PaymentPersonKey.Hash(personKey));
        cmd.Parameters.AddWithValue("@scope", scopeKey);
        cmd.Parameters.AddWithValue("@owner", personKey[2..]);
        cmd.Parameters.AddWithValue("@replica", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@machine", machine);
        cmd.Parameters.AddWithValue("@request", PaymentPersonKey.Hash($"istek-{id:N}"));
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@until", now.AddSeconds(120));
        await cmd.ExecuteNonQueryAsync();
    }

    private LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connectionString).Options);

    /// <summary>
    /// Kira yazımı kancası: yalnız kurulduğunda ve yalnız kira kaydı ekleyen kaydetmede tetiklenir. İlk tetiklenişte
    /// ya rakip isteği başlatır (sonunu beklemez) ve rakip kira yazımına gelene, kilitte beklediği görülene ya da süre
    /// dolana kadar bekler, ya da verilen yazımı (ör. doğrudan SQL) koşturur.
    /// </summary>
    private sealed class RivalHook(string connectionString) : SaveChangesInterceptor
    {
        private readonly object _gate = new();
        private bool _armed;
        private int _arrivals;
        private TimeSpan _wait;
        private Func<Task<HttpResponseMessage>>? _rival;
        private Func<Task>? _inject;
        private Task<HttpResponseMessage>? _rivalTask;
        private TaskCompletionSource _rivalArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Injected { get; private set; }

        public void Arm(Func<Task<HttpResponseMessage>> rival, TimeSpan wait)
        {
            lock (_gate)
            {
                _arrivals = 0;
                _wait = wait;
                _rival = rival;
                _inject = null;
                _rivalTask = null;
                _rivalArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _armed = true;
            }
        }

        public void ArmInjection(Func<Task> inject)
        {
            lock (_gate)
            {
                _arrivals = 0;
                _rival = null;
                _inject = inject;
                Injected = false;
                _armed = true;
            }
        }

        public void Disarm()
        {
            lock (_gate) _armed = false;
        }

        /// <summary>Kancanın başlattığı rakip isteğin yanıtı.</summary>
        public async Task<HttpResponseMessage> RivalAsync()
        {
            Task<HttpResponseMessage>? task;
            lock (_gate) task = _rivalTask;
            task.Should().NotBeNull("kanca rakibi ilk isteğin kira yazımından önce başlatmalı");
            return await task!;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not { } db
                || !db.ChangeTracker.Entries<PaymentRequestRecord>().Any(e => e.State == EntityState.Added))
                return result;

            Func<Task<HttpResponseMessage>>? rival;
            Func<Task>? inject;
            int arrival;
            lock (_gate)
            {
                if (!_armed) return result;
                arrival = ++_arrivals;
                rival = arrival == 1 ? _rival : null;
                inject = arrival == 1 ? _inject : null;
                if (rival is not null) _rivalTask = Task.Run(rival);
            }

            if (inject is not null)
            {
                await inject();
                Injected = true;
            }
            else if (rival is not null)
            {
                await Task.WhenAny(
                    _rivalArrived.Task,
                    AppLockWaiterProbe.WaitForWaiterAsync(connectionString, _wait, cancellationToken));
            }
            else if (arrival == 2)
            {
                _rivalArrived.TrySetResult();
            }
            return result;
        }
    }

    private sealed class HookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public RivalHook Hook { get; } = new(connectionString);

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:MinClientVersion"] = SalesSyncConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
