using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using PanelBalanceController = OrderDeck.LicenseServer.Controllers.Panel.PanelCustomerBalanceController;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// K5 panel eksi işlemleri ∥ havuzlu apply gerçek SQL Server'da (ortak bakiye spec 2026-10-11 §8.2-9, §4.13, K5; plan
/// farkı 12). Panelin elle eksi ayarı ve iade iptali kişinin havuzundan çeker (<c>P = S</c>), apply aynı havuzdan
/// (<c>P ⊆ S</c>). İki yazım da S'nin bakiye satırı olan bütün üyelerinin jetonuna dokunur (kişi kilidi): biri commit
/// edince öbürünün UPDATE'i tutmaz, plan taze okumayla baştan kurulur. Havuz hiçbir an eksiye inmez, D1 her satırda
/// tutar; üç denemede de çakışan panel eksi ayarı 503 <c>balance-busy</c> alır, hiçbir şey yazmaz ve sayaç
/// <c>endpoint=panel/manual-adjustment</c> ile bir artar (§8.6). Ortak satırdan çeken yazımları satırın kendi jetonu da
/// yakalar; kişi kilidini (D3, §8.1) ortak satırı olmayan, eksi üyeli §4.13 örneği kanıtlar. Aynı artı hareketin
/// (iade) eşzamanlı iki iptalinde hakem N01'dir (§4.9, §4.13): kaybeden 409 <c>already-reversed</c> alır —
/// <c>insufficient-balance</c> değil (Örnek 4; plan farkı 18k, Görev 10 Not 4). Havuz kipinde iptalin üyelik okuması
/// kilitlenme kurbanı (1205) olursa 503 <c>balance-busy</c> döner, hiçbir şey yazılmaz (Görev 10 Not 6).
///
/// <para>Yarış sırayla kurulur (<see cref="RaceSaveHook"/>: rakip yazım, dış isteğin <c>SaveChanges</c>'inde SQL
/// gitmeden commit eder; <see cref="ReadQueryHook"/>: rakip, dış isteğin belirli bir okumasından hemen önce commit
/// eder), zamanlamayla değil. InMemory'de <c>SaveChanges</c> atomik değil, N01 indeksi ve komut kancası yok; bu
/// iddialar orada kanıtlanamaz. Açıklamalar ve Id'ler üretilir.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalancePanelDrawRaceTests : IAsyncLifetime
{
    private const int Parallel = 8;
    private const decimal Step = 20m;

    private readonly SqlServerContainerFixture _sql;
    private PersonBalanceRelationalApiFactory _factory = null!;

    public PersonBalancePanelDrawRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() =>
        _factory = new PersonBalanceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Queries.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── TC9: K5 panel eksi işlemi ∥ apply ────────────────────────────────────

    /// <summary>
    /// §8.2-9, elle eksi ayar: apply'ın çekim kaydında (SQL gitmeden) panel B'den −70 commit eder (B 60 + C 10).
    /// Apply'ın ilk planı (B'den 60) panelin yazdığı B'nin kendi jetonuna çarpar; taze planda Σ = 30 → 30 çekilir.
    /// İki yazım ortak satırdan (B) çektiği için bu test K5'in çakışma ve taze plan yolunu sınar, kişi kilidini DEĞİL:
    /// kişi kilidini (D3) ortak satırı olmayan <see cref="Tc9_ortak_satiri_olmayan_panel_eksi_ayar_apply_kisi_kilidiyle_cakisir_D3"/> kanıtlar.
    /// </summary>
    [Fact]
    public async Task Tc9_panel_eksi_ayar_apply_kaydinda_havuz_eksiye_inmez()
    {
        var (kit, a, b, c) = await PersonAsync();
        var key = Guid.NewGuid();
        var atApplySave = false;
        _factory.Hook.Once(async saving =>
        {
            // Kanca apply'ın çekim kaydında olmalı: çapa (Id = anahtar) eklenmek üzere.
            atApplySave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == key);
            (await kit.PanelManualAdjustmentAsync(b, -70m, NewReason())).EnsureSuccessStatusCode();
        });

        var resp = await kit.ApplyAsync(a, 60m, 60m, key, null, [a, b, c]);

        atApplySave.Should().BeTrue("panel ayarı apply'ın çekim kaydında, SQL gitmeden commit etmeli");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1, "ilk plan panelin ilerlettiği jetonlara çarpmalı");
        var applied = await ReadApplyAsync(resp);
        applied.TransactionId.Should().Be(key);
        applied.AppliedAmount.Should().Be(30m, "taze plan: panel 70 çekti, havuzda 30 kaldı");
        applied.RemainingBalance.Should().Be(0m);
        (await kit.GroupRowsAsync(key)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().Equal((a, 0m), (c, -30m));
        (await PoolAsync(kit, a, b, c)).Should().Be(0m);
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b, c);
    }

    /// <summary>
    /// §8.2-9, iade iptali: A'ya 40'lık iade R. Apply'ın çekim kaydında panel R'yi iptal eder (çekim türü iptal, çapa
    /// A'da, 40 — Görev 10). Apply'ın ilk planı (A 40 + B 10) A'nın jetonuna çarpar; taze planda Σ = 50 → B'den 50.
    /// Ortak satır (A) çakışmayı kendi jetonuyla yakalar; bu test iade iptalinin K5 yolunu sınar, kişi kilidini değil.
    /// </summary>
    [Fact]
    public async Task Tc9_iade_iptali_apply_kaydinda_havuz_eksiye_inmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 50m);
        var refund = await kit.FundAsync(a, 40m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        var atApplySave = false;
        _factory.Hook.Once(async saving =>
        {
            atApplySave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == key);
            (await kit.PanelReverseAsync(b, refund)).EnsureSuccessStatusCode();
        });

        var resp = await kit.ApplyAsync(a, 50m, 50m, key, null, [a, b]);

        atApplySave.Should().BeTrue("iade iptali apply'ın çekim kaydında, SQL gitmeden commit etmeli");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1, "ilk plan iptalin ilerlettiği jetona çarpmalı");
        var applied = await ReadApplyAsync(resp);
        applied.AppliedAmount.Should().Be(50m, "iptal A'nın 40'ını aldı, havuzda B'nin 50'si kaldı");
        applied.RemainingBalance.Should().Be(0m);
        (await kit.GroupRowsAsync(key)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().Equal((a, 0m), (b, -50m));
        var cancel = (await kit.ReversalsOfAsync([refund])).Should().ContainSingle("iptal çapası tek; bacaklarda ReversesTransactionId yok").Which;
        cancel.WpfCustomerId.Should().Be(a);
        cancel.Amount.Should().Be(-40m);
        cancel.Kind.Should().Be(Kit.KindReversal);
        cancel.DrawGroupId.Should().Be(cancel.Id);
        (await PoolAsync(kit, a, b)).Should().Be(0m);
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b);
    }

    /// <summary>
    /// §8.1 (D3 — kişi kilidi ve Testcontainers 9 onu kanıtlar), §4.13'ün örneği: kişi {A 100, B −60, C 50} (havuz 90).
    /// Apply A'dan 90 ister; planı yalnız A'dan çeker. Apply'ın çekim kaydında (SQL gitmeden) panel C rotasından −50
    /// commit eder; planı yalnız C'den çeker. İki yazımın ORTAK SATIRI YOK: çakışmayı yalnız kişi kilidi doğurur (ikisi de
    /// S'nin bakiye satırı olan bütün üyelerinin jetonuna dokunur; apply'ın A UPDATE'i panelin ilerlettiği A jetonuna
    /// çarpar). Taze planda Σ = 100 − 60 + 0 = 40 → 40 çekilir, havuz 0. Kişi kilidi olmasaydı çakışma olmaz, A 10'a iner,
    /// havuz 10 − 60 + 0 = −50 olurdu. B kurulumdan eksi: eksi olmama iddiası yok, eksi üye çekilmez.
    /// </summary>
    [Fact]
    public async Task Tc9_ortak_satiri_olmayan_panel_eksi_ayar_apply_kisi_kilidiyle_cakisir_D3()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);
        var c = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        var atApplySave = false;
        _factory.Hook.Once(async saving =>
        {
            atApplySave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == key);
            (await kit.PanelManualAdjustmentAsync(c, -50m, NewReason())).EnsureSuccessStatusCode();
        });

        var resp = await kit.ApplyAsync(a, 90m, 90m, key, null, [a, b, c]);

        atApplySave.Should().BeTrue("panel ayarı apply'ın çekim kaydında, SQL gitmeden commit etmeli");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1,
            "ortak satır yok: yalnız kişi kilidi (A'nın jetonuna dokunuş) çakıştırır");
        var applied = await ReadApplyAsync(resp);
        applied.TransactionId.Should().Be(key);
        applied.AppliedAmount.Should().Be(40m, "taze plan: Σ = 100 − 60 + 0");
        applied.RemainingBalance.Should().Be(0m);
        (await kit.GroupRowsAsync(key)).Select(t => (t.WpfCustomerId, t.Amount)).Should().Equal((a, -40m));
        (await kit.BalanceAsync(a)).Should().Be(60m);
        (await kit.BalanceAsync(b)).Should().Be(-60m, "eksi üye çekilmez");
        (await kit.BalanceAsync(c)).Should().Be(0m);
        (await PoolAsync(kit, a, b, c)).Should().Be(0m, "D3: kişi kilidi olmasaydı −50");
        foreach (var row in new[] { a, b, c })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// §8.2-9, paralel: kişi {A 0, B 60, C 40, N −30} (havuz 70); 4 panel −20 (rota B) ve 4 apply 20 (çapa A) aynı anda.
    /// Toplam istek 160 &gt; 70: bir kısmı reddedilir. Havuz ≥ 0, D1, ve havuz = 70 − Σ(başarılı çekim): kayıp ya da çift
    /// yazım yok. Eksi üye N, eşzamanlı çekimler ayrık satırlardan geçerse (panel B'den, apply C'den) kişi kilidi olmadan
    /// havuzu eksiye düşürebilsin diye var; kişi kilidini belirlenimli olarak ortak satırı olmayan D3 testi kanıtlar,
    /// burada korunum ve izinli yanıtlar sınanır. Üç denemesi de çakışan istek 503 <c>balance-busy</c> alabilir (plan
    /// farkı 12, §4.13); 500 almaz.
    /// </summary>
    [Fact]
    public async Task Tc9_paralel_panel_eksi_ve_apply_havuzu_eksiye_dusurmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 60m);
        var c = await kit.MemberAsync(g, 40m);
        var n = await kit.MemberAsync(g, -30m);
        await kit.EnablePoolAsync();
        Guid[] members = [a, b, c, n];
        // Isınma: rota + EF JIT (bilinmeyen satır → panel 404, apply 409 no-balance), gerçek turda istekler ayrışmasın.
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => i % 2 == 0
            ? kit.PanelManualAdjustmentAsync(Guid.NewGuid(), -Step, NewReason())
            : kit.ApplyAsync(Guid.NewGuid(), Step, Step, Guid.NewGuid(), null, [Guid.NewGuid()])));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => i % 2 == 0
            ? OutcomeAsync(true, () => kit.PanelManualAdjustmentAsync(b, -Step, NewReason()))
            : OutcomeAsync(false, () => kit.ApplyAsync(a, Step, Step, Guid.NewGuid(), null, members))));

        var seen = string.Join(", ", results.Select(r => $"{(r.Panel ? "panel" : "apply")} {(int)r.Status} {r.Title}"));
        results.Should().OnlyContain(r => Allowed(r), "beklenmeyen yanıt ({0})", seen);
        results.Should().Contain(r => r.Status == HttpStatusCode.OK, "en az bir çekim geçmeli ({0})", seen);
        var pool = await PoolAsync(kit, a, b, c, n);
        pool.Should().BeGreaterThanOrEqualTo(0m, "havuz hiçbir an eksiye inmez ({0})", seen);
        pool.Should().Be(70m - results.Sum(r => r.Drawn),
            "her başarılı yazım havuzdan tam yazdığı kadar düşer ({0})", seen);
        (await kit.BalanceAsync(n)).Should().Be(-30m, "eksi üye çekilmez");
        (await kit.LedgerHoldsAsync(n)).Should().BeTrue("D1");
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b, c);
    }

    // ── K5: üç denemede de çakışma ───────────────────────────────────────────

    /// <summary>
    /// §4.13 + plan farkı 12: panel eksi ayarının her kaydından hemen önce B'nin jetonu ilerletilir; üç deneme de
    /// çakışır → 503 <c>balance-busy</c>. <c>SaveChanges</c> tek işlem: hiçbir hareket ve bakiye değişikliği kalmaz.
    /// Sayaç <c>orderdeck.balance.busy_total</c> panel önekiyle tek ölçüm alır (§8.6, plan farkı 18h; Görev 13'ün panel
    /// <c>BalanceBusy</c> yardımcısı — Görev 14 yalnız <c>endpoint=apply</c>'ı ölçer).
    /// </summary>
    [Fact]
    public async Task K5_uc_denemede_de_cakisirsa_503_balance_busy_hicbir_sey_yazmaz()
    {
        var (kit, a, b, c) = await PersonAsync();
        var before = await TransactionCountAsync(kit);
        var metrics = _factory.Services.GetRequiredService<OrderDeckMetrics>();
        var olcumler = new ConcurrentQueue<(long Value, string? Endpoint)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Paralel sınıflar aynı Meter adını paylaşır: yalnız bu fabrikanın sayacı.
            if (ReferenceEquals(instrument, metrics.BalanceBusy)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!ReferenceEquals(instrument, metrics.BalanceBusy)) return;
            string? endpoint = null;
            foreach (var tag in tags)
                if (tag.Key == "endpoint") endpoint = tag.Value as string;
            olcumler.Enqueue((value, endpoint));
        });
        listener.Start();
        _factory.Hook.Always(() => TouchBalanceAsync(b));

        var resp = await kit.PanelManualAdjustmentAsync(b, -30m, NewReason());
        _factory.Hook.Disarm();

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await resp.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(resp)).Should().Be("balance-busy");
        _factory.Hook.ConcurrencyFailures.Should().Be(PersonBalance.MaxAttempts);
        (await TransactionCountAsync(kit)).Should().Be(before, "yeni hareket yok");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(60m);
        (await kit.BalanceAsync(c)).Should().Be(40m);
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b, c);
        olcumler.Should().ContainSingle("tek 503, tek ölçüm").Which.Should().Be((1L, "panel/manual-adjustment"));
    }

    // ── K5: havuz kipinde eşzamanlı iade iptali (N01 hakemi) ─────────────────

    /// <summary>
    /// §4.9 "Hakem: N01", §4.13 (panel eksi işlemler: N01'de 409 <c>already-reversed</c>), Örnek 4. Dış iptalin (rota B)
    /// çekim kaydında (SQL gitmeden) rakip iptal (rota A) commit eder. Dış istek ya N01 ihlaline çarpar
    /// (<c>IsDuplicateReversal</c> → <c>AlreadyReversed</c>, Görev 4) ya da önce A'nın jetonuna (2. denemenin
    /// <c>beforeAttempt</c>'i iptali bulur → <c>Stopped</c>, plan farkı 18k (a)); hangisi önce gelirse gelsin yanıt
    /// 409 <c>already-reversed</c>'tır — taze planda havuz 10 &lt; 80 olduğu hâlde <c>insufficient-balance</c> değil.
    /// İşlem bütünüyle geri alınır: tek iptal grubu.
    /// </summary>
    [Fact]
    public async Task K5_iade_iptali_kayitta_rakip_iptal_commit_ederse_409_already_reversed()
    {
        var (kit, a, b, r) = await Ornek4Async();
        var before = await TransactionCountAsync(kit);
        var atCancelSave = false;
        (HttpStatusCode Status, string? Title)? rakip = null;
        _factory.Hook.Once(async saving =>
        {
            // Kanca dış iptalin çekim kaydında olmalı: R'yi iptal eden çapa eklenmek üzere.
            atCancelSave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.ReversesTransactionId == r);
            rakip = await StatusAsync(() => kit.PanelReverseAsync(a, r));
        });

        var dis = await StatusAsync(() => kit.PanelReverseAsync(b, r));
        _factory.Hook.Disarm();

        atCancelSave.Should().BeTrue("rakip iptal dış iptalin çekim kaydında, SQL gitmeden commit etmeli");
        rakip.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.OK, null), "rakip iptal kazanır");
        dis.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.Conflict, "already-reversed"),
            "N01 ya da jeton → 2. denemenin 'geri alındı mı'sı; insufficient-balance ya da 500 değil");
        await ShouldHaveSingleCancelAsync(kit, a, b, r, before);
    }

    /// <summary>
    /// Plan farkı 18k (b), Görev 10 Not 4: rakip iptal, dış iptalin "geri alındı mı" sorusundan SONRA ama 1. denemenin
    /// ilk bakiye okumasından ÖNCE commit eder. Dış istek commit sonrası bakiyeleri okur (A 0, B 10, havuz 10 &lt; 80):
    /// jeton çakışması yok, 2. deneme gelmez, <c>DrawAsync</c> <c>Insufficient</c> döner. Denetleyicinin yeniden sorusu
    /// iptali görür → 409 <c>already-reversed</c>. Yeniden soru olmasaydı 409 <c>insufficient-balance</c> dönerdi.
    /// Zincirin ilk halkası "geri alındı mı" sorgusudur: dış isteğin ilk <c>FROM [CustomerBalances]</c> okuması
    /// <c>ReverseInPoolAsync</c>'in başındaki <c>MembersAsync</c>'tir ve <c>beforeAttempt</c>'ten önce gelir.
    /// </summary>
    [Fact]
    public async Task K5_iade_iptali_bakiye_okumasindan_once_rakip_commit_ederse_409_already_reversed()
    {
        var (kit, a, b, r) = await Ornek4Async();
        var before = await TransactionCountAsync(kit);
        var soruldu = false;
        var rakipler = new List<(HttpStatusCode Status, string? Title)>();
        // 1. halka: dış isteğin beforeAttempt'i (deneme 1). Gövde yalnız 2. halkayı kurar; sorgu ondan sonra koşar ve
        // henüz iptal görmez (false).
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal)
                && sql.Contains("[ReversesTransactionId] = @", StringComparison.Ordinal),
            () =>
            {
                soruldu = true;
                // 2. halka: 1. denemenin ilk bakiye okuması (PersonDrawSetAsync → MembersAsync'in bakiye sorgusu).
                _factory.Queries.Arm(
                    sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
                    async () => rakipler.Add(await StatusAsync(() => kit.PanelReverseAsync(a, r))));
                return Task.CompletedTask;
            });

        var dis = await StatusAsync(() => kit.PanelReverseAsync(b, r));
        _factory.Queries.Disarm();

        soruldu.Should().BeTrue("dış istek 1. denemenin başında 'geri alındı mı' diye sormalı (beforeAttempt)");
        rakipler.Should().ContainSingle("rakip dış isteğin 1. denemesinin bakiye okumasından önce koşmalı")
            .Which.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.OK, null));
        dis.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.Conflict, "already-reversed"),
            "Insufficient sonrası yeniden soru iptali bulur; insufficient-balance değil");
        _factory.Hook.ConcurrencyFailures.Should().Be(0,
            "kazanan okumalardan önce commit etti: jeton çakışması yok, 2. deneme gelmez (pencere b)");
        await ShouldHaveSingleCancelAsync(kit, a, b, r, before);
    }

    /// <summary>
    /// §4.9, §4.13: aynı iadenin 8 paralel iptali (rota A ve B karışık). Tam biri 200; gerisi 409
    /// <c>already-reversed</c> (hızlı yol, N01, jetondan sonraki soru ya da <c>Insufficient</c> sonrası soru).
    /// Kilitlenmeler denemeleri tüketirse 503 <c>balance-busy</c> da olabilir (Görev 14 Not 1'in gerekçesi; yalnız tek
    /// commit olduğu için jeton çakışması bir isteğe en çok bir kez düşer). <c>insufficient-balance</c> ve 500 kabul
    /// edilmez. Tek iptal grubu, A 0, B 10, D1.
    /// </summary>
    [Fact]
    public async Task K5_iade_iptali_paralel_sekiz_tek_iptal()
    {
        var (kit, a, b, r) = await Ornek4Async();
        var before = await TransactionCountAsync(kit);
        // Isınma: rota + EF JIT (var olmayan hareket → 404), gerçek turda istekler ayrışmasın.
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => kit.PanelReverseAsync(i % 2 == 0 ? a : b, Guid.NewGuid())));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel)
            .Select(i => StatusAsync(() => kit.PanelReverseAsync(i % 2 == 0 ? a : b, r))));

        var seen = string.Join(", ", results.Select(x => $"{(int)x.Status} {x.Title}"));
        results.Should().ContainSingle(x => x.Status == HttpStatusCode.OK, "tam bir iptal kazanır ({0})", seen);
        results.Where(x => x.Status != HttpStatusCode.OK).Should().OnlyContain(
            x => (x.Status == HttpStatusCode.Conflict && x.Title == "already-reversed")
                || (x.Status == HttpStatusCode.ServiceUnavailable && x.Title == "balance-busy"),
            "kaybeden already-reversed alır; insufficient-balance ya da 500 yok ({0})", seen);
        await ShouldHaveSingleCancelAsync(kit, a, b, r, before);
    }

    // ── Havuz kipinde iptal: üyelik okumasının kilitlenmesi (Görev 10 Not 6) ─

    public const string InPoolPath = "eski-satır";
    public const string DrawGroupPath = "çekim-grubu";

    /// <summary>
    /// Görev 10 Not 6 (spec 2026-10-11 §4.2-6 "S2 uygulama notu"): havuz kipinde panel iptalinin üyelik okuması
    /// (<c>MembersAsync</c> — <c>ReverseInPoolAsync</c>'in başında ve <c>ReverseDrawGroupAsync</c>'in havuz dalında)
    /// servislerin korunan denemesinin dışındadır ve <c>CustomerBalances</c>'ı da okur: yazarların X kilidinin
    /// arkasında 1205 kurbanı olabilir. Kanca isteğin ilk <c>FROM [CustomerBalances]</c> okumasında BİR KEZ
    /// <c>SqlException(1205)</c> fırlatır, komut sunucuya gitmez. İki yolda da o okuma üyelik okumasıdır: öncesinde
    /// yalnız hedef, kip ve (grup yolunda) grubun hareketleri okunur. Yanıt 503 <c>balance-busy</c> (500 değil),
    /// hiçbir şey yazılmaz, istemcinin yeniden denemesi başarır. Sarmal kaldırılırsa istisna TestServer'dan istemciye
    /// düşer (<see cref="StatusAsync"/> → 500). InMemory'de komut kancası yok.
    /// </summary>
    [Theory]
    [InlineData(InPoolPath)]
    [InlineData(DrawGroupPath)]
    public async Task Havuz_kipinde_iptalin_uyelik_okumasi_kilitlenme_kurbani_olursa_503_balance_busy(string path)
    {
        Kit kit;
        Guid route, target;
        (Guid A, Guid B, Guid R) ornek4 = default;
        Kit.Example1? ornek1 = null;
        if (path == InPoolPath)
        {
            // DrawGroupId boş artı hedef (iade R, A'da), rota B → ReverseInPoolAsync.
            var (k, a, b, r) = await Ornek4Async();
            (kit, ornek4, route, target) = (k, (a, b, r), b, r);
        }
        else
        {
            // Çekim grubu K (T, I, Y), rota T → ReverseDrawGroupAsync'in havuz dalı.
            kit = await Kit.CreateAsync(_factory);
            ornek1 = await kit.Example1Async();
            await kit.EnablePoolAsync();
            (route, target) = (ornek1.T, ornek1.K);
        }
        var before = await TransactionCountAsync(kit);
        var injected = 0;
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
            () =>
            {
                injected++;
                return Task.FromException(SqlExceptionFactory.Create(1205,
                    "Transaction was deadlocked and has been chosen as the deadlock victim."));
            });

        var busy = await StatusAsync(() => kit.PanelReverseAsync(route, target));
        _factory.Queries.Disarm();

        injected.Should().Be(1, "kanca üyelik okumasında ({0}) bir kez fırlatmalı", path);
        busy.Should().Be(((HttpStatusCode, string?))(HttpStatusCode.ServiceUnavailable, "balance-busy"),
            "üyelik okumasının 1205'i yeniden denensin (503); 500 değil");
        _factory.Log.Entries.Count(e => e.Category == typeof(PanelBalanceController).FullName
                && e.Message.Contains("üyelik okuması kilitlenme kurbanı (1205)", StringComparison.Ordinal)
                && e.Message.Contains(target.ToString(), StringComparison.Ordinal))
            .Should().Be(1, "servis bu kaynakta yazmaz; kaynağı ayıran tek satır denetleyicinin ({0})", path);
        (await TransactionCountAsync(kit)).Should().Be(before, "okuma her yazımdan önce: hiçbir şey yazılmadı");

        (await kit.PanelReverseAsync(route, target)).StatusCode.Should().Be(HttpStatusCode.OK,
            "istemcinin yeniden denemesi başarır");
        if (path == InPoolPath)
            await ShouldHaveSingleCancelAsync(kit, ornek4.A, ornek4.B, ornek4.R, before);
        else
        {
            var ex = ornek1!;
            (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3, "tek grup geri alması, bir kez");
            (await kit.BalanceAsync(ex.T)).Should().Be(0m);
            (await kit.BalanceAsync(ex.I)).Should().Be(100m);
            (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
            await ShouldHoldD1AndNonNegativeAsync(kit, ex.T, ex.I, ex.Y);
        }
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record Outcome(bool Panel, HttpStatusCode Status, string? Title, decimal Drawn);

    private static string NewReason() => $"r-{Guid.NewGuid():N}";

    /// <summary>
    /// Spec Örnek 4'ün başı: kişi {A, B}; A'ya iade R (+80), sonra eski tek bacaklı düşüm A'dan 50 → A = 30; B = 60;
    /// havuz 90; lisans havuz kipinde. Döner: (kit, A, B, R).
    /// </summary>
    private async Task<(Kit kit, Guid a, Guid b, Guid r)> Ornek4Async()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var r = await kit.FundAsync(a, 80m);
        await kit.SeedLegacyDeductionAsync(a, 50m, 50m);
        var b = await kit.MemberAsync(g, 60m);
        await kit.EnablePoolAsync();
        return (kit, a, b, r);
    }

    /// <summary>
    /// Örnek 4'ün sonu, tek kez: R'nin tek iptal çapası (A, −30, <c>reversal</c>, Reverses R, DrawGroupId = kendi Id'si)
    /// ve tek bacağı (B, −50); kurulumdan bu yana tam iki yeni hareket; A 0, B 10; D1 iki satırda.
    /// </summary>
    private static async Task ShouldHaveSingleCancelAsync(Kit kit, Guid a, Guid b, Guid r, int before)
    {
        var cancel = (await kit.ReversalsOfAsync([r])).Should()
            .ContainSingle("N01: hareket başına tek iptal; bacaklarda ReversesTransactionId yok").Which;
        cancel.WpfCustomerId.Should().Be(a, "çapa hedefin kendi satırında (Görev 10)");
        cancel.Amount.Should().Be(-30m);
        cancel.Kind.Should().Be(Kit.KindReversal);
        cancel.DrawGroupId.Should().Be(cancel.Id);
        (await kit.GroupRowsAsync(cancel.Id)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().Equal((a, -30m), (b, -50m));
        (await TransactionCountAsync(kit)).Should().Be(before + 2, "kaybedenler hiçbir şey yazmaz");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(10m);
        await ShouldHoldD1AndNonNegativeAsync(kit, a, b);
    }

    /// <summary>
    /// Durum ve (başarısızsa) problem başlığı. TestServer sunucuda yakalanmayan istisnayı istemciye fırlatır; prod'da
    /// 500 olurdu — başlık yerine en içteki istisna, düşen koşu nedeni göstersin.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string? Title)> StatusAsync(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            var resp = await call();
            return (resp.StatusCode, resp.IsSuccessStatusCode ? null : await Kit.TitleAsync(resp));
        }
        catch (Exception e)
        {
            var root = e.GetBaseException();
            return (HttpStatusCode.InternalServerError, $"{root.GetType().Name}: {root.Message}");
        }
    }

    /// <summary>Kişi {A (0, bakiye satırı var), B (60), C (40)} — aynı grup, havuz 100; lisans havuz kipinde.</summary>
    private async Task<(Kit kit, Guid a, Guid b, Guid c)> PersonAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 60m);
        var c = await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();
        return (kit, a, b, c);
    }

    /// <summary>
    /// Durum, (başarısızsa) problem başlığı ve (başarılıysa) havuzdan çekilen tutar. TestServer, sunucuda yakalanmayan
    /// istisnayı istemciye fırlatır; prod'da 500 olurdu — başlık yerine en içteki istisna, düşen koşu nedeni göstersin.
    /// </summary>
    private static async Task<Outcome> OutcomeAsync(bool panel, Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await call();
        }
        catch (Exception e)
        {
            var root = e.GetBaseException();
            return new Outcome(panel, HttpStatusCode.InternalServerError, $"{root.GetType().Name}: {root.Message}", 0m);
        }
        if (!resp.IsSuccessStatusCode) return new Outcome(panel, resp.StatusCode, await Kit.TitleAsync(resp), 0m);
        // Panel eksi ayarı hep-ya-hiç (tam Step); apply UpToPool (kısmi olabilir) — yanıtın AppliedAmount'u.
        var drawn = panel ? Step : (await ReadApplyAsync(resp)).AppliedAmount;
        return new Outcome(panel, resp.StatusCode, null, drawn);
    }

    /// <summary>
    /// İzinli yanıtlar: 200; panel 409 <c>insufficient-balance</c>, apply 409 <c>no-balance</c> (taze planda havuz
    /// yetmedi); ikisi de 503 <c>balance-busy</c> (üç denemede de çakıştı). 500 yok.
    /// </summary>
    private static bool Allowed(Outcome r) => r.Status switch
    {
        HttpStatusCode.OK => true,
        HttpStatusCode.Conflict => r.Title == (r.Panel ? "insufficient-balance" : PersonBalance.CodeNoBalance),
        HttpStatusCode.ServiceUnavailable => r.Title == "balance-busy",
        _ => false,
    };

    private static async Task<LicensesCustomerBalanceApplyController.ApplyResponse> ReadApplyAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.ApplyResponse>())!;

    private static async Task<decimal> PoolAsync(Kit kit, params Guid[] rows)
    {
        var sum = 0m;
        foreach (var row in rows) sum += await kit.BalanceAsync(row);
        return sum;
    }

    private static async Task ShouldHoldD1AndNonNegativeAsync(Kit kit, params Guid[] rows)
    {
        foreach (var row in rows)
        {
            (await kit.BalanceAsync(row)).Should().BeGreaterThanOrEqualTo(0m, "hiçbir satır eksiye inmez");
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        }
    }

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId));

    /// <summary>Kilit dokunuşu: satıra yalnız "değişti" işareti, jetonu StampVersions ilerletir.</summary>
    private async Task TouchBalanceAsync(Guid row)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == row);
        db.Entry(balance).Property(b => b.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync();
    }
}
