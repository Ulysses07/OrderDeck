using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Havuzlu apply ∥ üyelik değişimi gerçek SQL Server'da (spec 2026-10-11 §8.2-6, -7, -8; §4.2-2, §4.11, M6): ayırma
/// senkronu apply'ın bakiye yüklemesinin önünde, iki üyelik okumasının arasında ve ikinci okumadan sonra commit eder —
/// ayrılan para tutan satırdan ayırmadan sonra çekim yok; apply önce commit ederse senkron taze okumayla yeniden uygular.
/// Kural 3b taze veriyle: W dışındaki eksi üye iki bakiye okuması arasında artıya geçerse 409, çekim yok.
/// Kimlik birleşmesi ("@" ikizi birliği ve olağan kip) ve <c>group-customers</c> apply'ın kaydında commit eder; apply birleşmenin ya
/// da gruplama bileşeninin kaydında commit ederse iş grubu/bileşeni geri alır ve sayar. Yarış sırayla kurulur: okuma konumları <see cref="ReadQueryHook"/>
/// zinciriyle, kayıt <see cref="RaceSaveHook"/> ile (üretim kodunda test kancası yok). InMemory'de komut kancası, atomik
/// kayıt ve işlem yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceDrawMembershipRaceTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private PersonBalanceRelationalApiFactory _factory = null!;

    public PersonBalanceDrawMembershipRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() =>
        _factory = new PersonBalanceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Queries.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static bool IsBalanceRead(string sql) => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal);

    private static bool IsProjectionRead(string sql) =>
        sql.Contains("FROM [WpfCustomerProjections]", StringComparison.Ordinal);

    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    /// <summary>Apply'ın sonucu: durum, (başarısızsa) problem başlığı, (başarılıysa) uygulanan tutar.</summary>
    private sealed record ApplyOutcome(HttpStatusCode Status, string? Title, decimal? Applied);

    /// <summary>
    /// TestServer, sunucu tarafında yakalanmayan istisnayı istemciye fırlatır; prod'da 500 olurdu — başlık yerine en
    /// içteki istisna, düşen koşunun mesajı nedeni göstersin (emsal <c>PersonBalanceReversalRaceTests</c>).
    /// </summary>
    private static async Task<ApplyOutcome> StatusAsync(Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await call();
        }
        catch (Exception e)
        {
            var root = e.GetBaseException();
            return new ApplyOutcome(HttpStatusCode.InternalServerError, $"{root.GetType().Name}: {root.Message}", null);
        }
        if (!resp.IsSuccessStatusCode) return new ApplyOutcome(resp.StatusCode, await Kit.TitleAsync(resp), null);
        var body = await resp.Content.ReadFromJsonAsync<ApplyDto>();
        return new ApplyOutcome(resp.StatusCode, null, body!.AppliedAmount);
    }

    // ── TC6: ayırma senkronu ∥ apply (§8.2-6) ─────────────────────────────────

    /// <summary>
    /// TC6'nın ortak kurulumu: kişi g = {A (çapa, 0), B (100)}, havuz kipi; apply A'dan 50, <c>W</c> = {A, B}, anahtar
    /// K. Rakip: B'yi ayıran WPF senkronu (<c>GroupId = NULL</c> partisi).
    /// </summary>
    private sealed class Separation
    {
        public Separation(Kit kit, Guid a, Guid b)
        {
            Kit = kit;
            A = a;
            B = b;
        }

        public Kit Kit { get; }
        public Guid A { get; }
        public Guid B { get; }
        public Guid Key { get; } = Guid.NewGuid();
        public bool Separated { get; private set; }

        public Task<HttpResponseMessage> ApplyAsync() => Kit.ApplyAsync(A, 50m, 50m, Key, null, [A, B]);

        public async Task SeparateAsync()
        {
            (await Kit.SyncGroupAsync(B, null)).EnsureSuccessStatusCode();
            Separated = true;
        }
    }

    private async Task<Separation> SeparationAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 100m);
        await kit.EnablePoolAsync();
        return new Separation(kit, a, b);
    }

    /// <summary>
    /// Ayırma apply'ın planından önce commit etti: taze denemede B kişide değil ama <c>W</c>'de → kural 3a, 409
    /// <c>customer-not-synced</c>. Ayrılan para tutan satırdan ayırmadan sonra çekim yok.
    /// </summary>
    private static async Task ShouldNotHaveDrawnFromSeparatedAsync(Separation s, ApplyOutcome result)
    {
        s.Separated.Should().BeTrue("rakip ayırma kancanın konumunda commit etmeli");
        result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
            "taze denemede B W'de ama S(A)'da değil (kural 3a)");
        (await s.Kit.BalanceAsync(s.B)).Should().Be(100m, "ayrılan satırdan ayırmadan sonra çekim yok");
        (await s.Kit.BalanceAsync(s.A)).Should().Be(0m);
        (await s.Kit.GroupRowsAsync(s.Key)).Should().BeEmpty("K anahtarlı hiçbir satır yazılmadı");
        (await s.Kit.ProjectionAsync(s.B)).GroupId.Should().BeNull("ayırma kaydedildi");
        foreach (var row in new[] { s.A, s.B })
            (await s.Kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>(i) Ayırma izlenen bakiye yüklemesinin hemen önünde: ikinci üyelik okuması farkı görür (§4.2-2).</summary>
    [Fact]
    public async Task Tc6_i_ayirma_bakiye_yuklemesinden_once()
    {
        var s = await SeparationAsync();
        // 1. bakiye okuması (üyeliğin izlemesiz bakiyeleri) zinciri kurar; 2.'si (izlenen yükleme) ayırmayı önüne alır.
        _factory.Queries.Arm(IsBalanceRead, () =>
        {
            _factory.Queries.Arm(IsBalanceRead, s.SeparateAsync);
            return Task.CompletedTask;
        });

        var result = await StatusAsync(s.ApplyAsync);
        _factory.Queries.Disarm();

        await ShouldNotHaveDrawnFromSeparatedAsync(s, result);
        _factory.Hook.ConcurrencyFailures.Should().Be(0,
            "fark üyeliğin ikinci okumasında görüldü; ilk deneme kayda varmadı");
    }

    /// <summary>
    /// (ii) Ayırma izlenen yüklemeden SONRA, üyeliğin ikinci okumasından hemen önce: izlenen jeton eskidir, yine de
    /// ikinci okuma farkı görür ve plan kayda varmadan baştan kurulur.
    /// </summary>
    [Fact]
    public async Task Tc6_ii_ayirma_iki_uyelik_okumasi_arasinda()
    {
        var s = await SeparationAsync();
        // 1. bakiye okuması → 2. bakiye okuması (izlenen yükleme) → sonraki projeksiyon okuması = ikinci üyelik okumasının
        // başı; ayırma onun hemen önünde commit eder.
        _factory.Queries.Arm(IsBalanceRead, () =>
        {
            _factory.Queries.Arm(IsBalanceRead, () =>
            {
                _factory.Queries.Arm(IsProjectionRead, s.SeparateAsync);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });

        var result = await StatusAsync(s.ApplyAsync);
        _factory.Queries.Disarm();

        await ShouldNotHaveDrawnFromSeparatedAsync(s, result);
        _factory.Hook.ConcurrencyFailures.Should().Be(0,
            "ikinci okuma olmasaydı kayıtta jeton çakışması yakalardı; burada fark kayıttan önce görülmeli");
    }

    /// <summary>
    /// (iii) Ayırma ikinci okumadan sonra, apply'ın kaydında: senkronun kancası B'nin bakiye jetonunu ilerletir, apply'ın
    /// UPDATE'i tutmaz ve plan baştan kurulur (§4.11, M6).
    /// </summary>
    [Fact]
    public async Task Tc6_iii_ayirma_ikinci_okumadan_sonra_kayitta()
    {
        var s = await SeparationAsync();
        _factory.Hook.Once(() => s.SeparateAsync());

        var result = await StatusAsync(s.ApplyAsync);
        _factory.Hook.Disarm();

        await ShouldNotHaveDrawnFromSeparatedAsync(s, result);
        _factory.Hook.ConcurrencyFailures.Should().Be(1,
            "apply'ın ilk kaydı ilerlemiş jetona çarptı; ikinci deneme kural 3a'da yazmadan döner");
    }

    /// <summary>
    /// Ters sıra: apply, senkronun kancasının bakiye yüklemesiyle senkronun kaydı arasında commit eder. Sıra "apply
    /// ayırmadan önce oldu"dur (B o an kişideydi, çekim meşru); senkron B'nin jetonuna çarpar ve temizle-yeniden-uygula
    /// döngüsüyle taze okumayla tamamlar (§4.11).
    /// </summary>
    [Fact]
    public async Task Tc6_senkron_cakisirsa_taze_okumayla_yeniden_uygular()
    {
        var s = await SeparationAsync();
        var atSyncSave = false;
        ApplyOutcome? apply = null;
        _factory.Hook.Once(async saving =>
        {
            // Kanca senkronun kaydında olmalı: kaydedilen bağlamda B'nin ayrılan projeksiyonu var.
            atSyncSave = saving.ChangeTracker.Entries<WpfCustomerProjection>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Id == s.B && e.Entity.GroupId == null);
            apply = await StatusAsync(s.ApplyAsync);
        });

        var sync = await s.Kit.SyncGroupAsync(s.B, null);
        _factory.Hook.Disarm();

        atSyncSave.Should().BeTrue("apply senkronun SaveChanges'inde commit etmeli");
        apply.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 50m), "apply ayırmadan önce commit etti");
        sync.StatusCode.Should().Be(HttpStatusCode.OK, await sync.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().Be(1,
            "senkronun ilk kaydı apply'ın ilerlettiği B jetonuna çarptı; ikinci deneme taze okumayla tuttu");
        (await s.Kit.ProjectionAsync(s.B)).GroupId.Should().BeNull("senkron partiyi taze okumayla yeniden uyguladı");
        (await s.Kit.BalanceAsync(s.B)).Should().Be(50m);
        (await s.Kit.BalanceAsync(s.A)).Should().Be(0m);
        (await s.Kit.GroupRowsAsync(s.Key)).Select(r => (r.WpfCustomerId, r.Amount))
            .Should().Equal((s.A, 0m), (s.B, -50m));
        foreach (var row in new[] { s.A, s.B })
            (await s.Kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    // ── Kural 3b taze veriyle (§4.0-3b; Görev 4 Adım 5 — DrawAsync'in nowPositive denetimi) ──

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.CountAsync(t => t.LicenseId == kit.LicenseId));

    /// <summary>
    /// g = {A (çapa, 0), B (100), N (−30)}, havuz kipi; apply A'dan 50, <c>W</c> = {A, B}. Kümenin izlemesiz okumasında
    /// (denemenin 1. bakiye okuması) N eksidir: <c>S∖P</c>'nin kilitlenen eksisi, kural 3b'yi tetiklemez. N'ye 80'lik iade
    /// izlenen yüklemenin (2. bakiye okuması) hemen önünde commit eder: izlenen N = 50, W'de görülmeyen para. Üyelik
    /// değişmediği için ikinci okuma baştan kurmaz; 409'u yalnız <c>nowPositive</c> denetimi verir. Denetim olmasaydı
    /// Σ = 100 + min(0, 50) = 100 → 200 ve B = 50.
    /// </summary>
    [Fact]
    public async Task Kural3b_W_disi_eksi_uye_izlenen_yuklemede_artiya_gecerse_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 100m);
        var n = await kit.MemberAsync(g, -30m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        var before = await TransactionCountAsync(kit);
        var funded = false;
        // 1. bakiye okuması (izlemesiz, N = −30) zinciri kurar; 2.'si (izlenen yükleme) iadeyi önüne alır.
        _factory.Queries.Arm(IsBalanceRead, () =>
        {
            _factory.Queries.Arm(IsBalanceRead, async () =>
            {
                await kit.FundAsync(n, 80m);
                funded = true;
            });
            return Task.CompletedTask;
        });

        var result = await StatusAsync(() => kit.ApplyAsync(a, 50m, 50m, key, null, [a, b]));
        _factory.Queries.Disarm();

        funded.Should().BeTrue("iade izlenen yüklemenin önünde commit etmeli");
        result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
            "izlenen yüklemede N > 0: S'de olup W'de olmayan pozitif canlı üye (kural 3b)");
        _factory.Hook.ConcurrencyFailures.Should().Be(0, "fark kayda varılmadan, izlenen satırlarda görüldü");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(100m, "W'de görülmeyen para varken çekim yok");
        (await kit.BalanceAsync(n)).Should().Be(50m);
        (await kit.GroupRowsAsync(key)).Should().BeEmpty("K anahtarlı hiçbir satır yazılmadı");
        (await TransactionCountAsync(kit)).Should().Be(before + 1, "yalnız iadenin hareketi eklendi");
        foreach (var row in new[] { a, b, n })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    // ── TC7: apply ∥ kimlik birleşmesi ("@" ikizi birliği) (§8.2-7) ────────────

    /// <summary>
    /// Spec Örnek 1'in "@" ikizi hâli: kişi g = {T (çapa, 0), I ("@ad", instagram, 100)}; grupsuz asıl kayıt X ("ad",
    /// instagram, bakiye satırı yok). Birleşmede I, X'in kopyası olur; I'nin parası ve hareketleri X'e taşınır, X
    /// birlikle g'ye katılır (<c>UnionGroupsAsync</c>).
    /// </summary>
    private sealed record Twins(Kit Kit, string Group, Guid T, Guid I, Guid X);

    private async Task<Twins> TwinsAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var name = $"u{Guid.NewGuid():N}"[..16];
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram", username: "@" + name);
        var x = await kit.MemberAsync(null, balanceRow: false, platform: "instagram", username: name);
        await kit.EnablePoolAsync();
        return new Twins(kit, g, t, i, x);
    }

    private async Task<CustomerIdentityMergeJob.Report> MergeTwinsAsync(Kit kit, bool atTwins = true)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CustomerIdentityMergeJob>()
            .RunAsync(kit.LicenseId, apply: true, default, atTwins: atTwins);
    }

    /// <summary>
    /// Birleşme apply'ın kaydında commit eder: birleştirici I'nin bakiye satırını 0'lar (jeton ilerler), apply'ın
    /// UPDATE'i tutmaz; taze denemede I (kopya) asıl kayıt X'e çözülür. X birlikle kişideyse para X'ten çekilir; değilse
    /// kural 3a. İki dalda da kopyaya K grubunun satırı yazılmaz.
    /// </summary>
    [Fact]
    public async Task Tc7_kimlik_birlesmesi_kayit_aninda_apply_parayi_asil_kayittan_ceker()
    {
        var tw = await TwinsAsync();
        var key = Guid.NewGuid();
        _factory.Hook.Once(async () => (await MergeTwinsAsync(tw.Kit)).FailedGroups.Should().Be(0));

        var result = await StatusAsync(() => tw.Kit.ApplyAsync(tw.T, 50m, 50m, key, null, [tw.T, tw.I]));
        _factory.Hook.Disarm();

        (await tw.Kit.ProjectionAsync(tw.I)).MergedIntoId.Should().Be(tw.X, "'@'lı satır '@'sız asıl kayda birleşti");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0,
            "birleştirici I'nin bakiye satırını 0'ladı; apply'ın ilk UPDATE'i jetona çarptı");
        (await tw.Kit.BalanceAsync(tw.I)).Should().Be(0m, "para görünmez kopyada kalmaz");
        var rows = await tw.Kit.GroupRowsAsync(key);
        rows.Should().NotContain(r => r.WpfCustomerId == tw.I, "kopyaya K grubunun satırı yazılmaz");
        if ((await tw.Kit.ProjectionAsync(tw.X)).GroupId == tw.Group)
        {
            result.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 50m), "X birlikle S(T)'de; W'nin I'si X'e çözüldü");
            (await tw.Kit.BalanceAsync(tw.X)).Should().Be(50m, "100 birleşmeyle X'e geçti, 50 çekildi");
            rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((tw.T, 0m), (tw.X, -50m));
        }
        else
        {
            result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
                "X S(T)'de değil ama W'de (kural 3a)");
            (await tw.Kit.BalanceAsync(tw.X)).Should().Be(100m);
            rows.Should().BeEmpty();
        }
        foreach (var row in new[] { tw.T, tw.I, tw.X })
            (await tw.Kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// Olağan kip (<c>merge-customer-identities</c>, <c>atTwins: false</c>; grup birliği yok): kişi g = {T (çapa, 0),
    /// I (instagram, 100)}; asıl kayıt X (instagram, bakiye satırı yok) BAŞKA kişide, g2'de. I'nin adı X'inkiyle aynı,
    /// saklı <c>IdentityKey</c>'i bayat (B1): tekil indeks saklı anahtara bakar, iş hesaplanan anahtarla gruplar
    /// (<c>CustomerIdentityMergeJob.cs:352</c>). X önce kurulur ve <c>UpdatedAt</c>'i geriye çekilir: sıralamada asıl kayıt
    /// (<c>:399-400</c>). X'in numarası dolu olduğu için alan aktarımı onu ezmez (<c>CustomerFieldMerge.cs:54-56</c>).
    /// Birleşme apply'ın kaydında commit eder; taze denemede W'nin I'si X'e çözülür, X S(T)'de değil → kural 3a, 409.
    /// Başka kişinin parasından (X'e taşınan 100) çekim yok.
    /// </summary>
    [Fact]
    public async Task Tc7_olagan_kip_kopyanin_asil_kaydi_baska_kisideyse_kural3a_cekim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var name = $"u{Guid.NewGuid():N}"[..16];
        var g = Kit.NewGroup();
        var g2 = Kit.NewGroup();
        var x = await kit.MemberAsync(g2, balanceRow: false, platform: "instagram", username: name);
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram", username: $"v{Guid.NewGuid():N}"[..16]);
        // Ham SQL: Username setter'ı IdentityKey'i yeniden türetirdi (WpfCustomerProjection.cs:20-27) ve tekil indeks
        // (LicenseId, Platform, IdentityKey) X ile çakışırdı. Emsal IdentityKeyRepairJobTests.
        (await kit.Sales.ReadDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE WpfCustomerProjections SET Username = {name} WHERE Id = {i}"))).Should().Be(1);
        var earlier = DateTimeOffset.UtcNow.AddHours(-1);
        (await kit.Sales.ReadDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE WpfCustomerProjections SET UpdatedAt = {earlier} WHERE Id = {x}"))).Should().Be(1);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        _factory.Hook.Once(async () => (await MergeTwinsAsync(kit, atTwins: false)).FailedGroups.Should().Be(0));

        var result = await StatusAsync(() => kit.ApplyAsync(t, 50m, 50m, key, null, [t, i]));
        _factory.Hook.Disarm();

        (await kit.ProjectionAsync(i)).MergedIntoId.Should().Be(x, "aynı ad, olağan kipte X'e birleşti");
        (await kit.ProjectionAsync(x)).GroupId.Should().Be(g2, "olağan kipte grup birliği yok; dolu numara ezilmez");
        result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
            "W'nin I'si X'e çözüldü; X S(T)'de değil (kural 3a)");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0,
            "birleştirici I'nin bakiye satırını 0'ladı; apply'ın ilk UPDATE'i jetona çarptı");
        (await kit.BalanceAsync(x)).Should().Be(100m, "başka kişinin parasından çekim yok");
        (await kit.BalanceAsync(i)).Should().Be(0m, "para görünmez kopyada kalmaz");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.GroupRowsAsync(key)).Should().BeEmpty("K anahtarlı hiçbir satır yazılmadı");
        foreach (var row in new[] { t, i, x })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// Ters sıra: apply birleşmenin kaydında commit eder (I o an asıl kayıt, çekim meşru). Birleşmenin kaydı I'nin
    /// apply'ın ilerlettiği jetonuna çarpar; iş grubu geri alır ve sayar (<c>CustomerIdentityMergeJob.cs:297-309</c>),
    /// birleşme ve grup birliği olmaz. Yeniden koşu kalanı bulur.
    /// </summary>
    [Fact]
    public async Task Tc7_apply_once_commit_ederse_birlesme_grubu_atlanir_ve_sayilir()
    {
        var tw = await TwinsAsync();
        var key = Guid.NewGuid();
        var atMergeSave = false;
        ApplyOutcome? apply = null;
        _factory.Hook.Once(async saving =>
        {
            // Kanca birleşmenin kaydında olmalı: kaydedilen bağlamda I, X'e yönlenmiş.
            atMergeSave = saving.ChangeTracker.Entries<WpfCustomerProjection>()
                .Any(e => e.Entity.Id == tw.I && e.Entity.MergedIntoId == tw.X);
            apply = await StatusAsync(() => tw.Kit.ApplyAsync(tw.T, 50m, 50m, key, null, [tw.T, tw.I]));
        });

        var report = await MergeTwinsAsync(tw.Kit);
        _factory.Hook.Disarm();

        atMergeSave.Should().BeTrue("apply birleşme grubunun SaveChanges'inde commit etmeli");
        apply.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 50m), "apply birleşmeden önce commit etti");
        report.Groups.Should().Be(1);
        report.FailedGroups.Should().Be(1, "grup I'nin ilerlemiş jetonuna çarptı; geri alındı ve sayıldı");
        _factory.Hook.ConcurrencyFailures.Should().Be(1, "yalnız birleşmenin kaydı çakıştı");
        (await tw.Kit.ProjectionAsync(tw.I)).MergedIntoId.Should().BeNull("birleşme geri alındı");
        (await tw.Kit.ProjectionAsync(tw.X)).GroupId.Should().BeNull("grup birliği aynı işlemde geri alındı");
        (await tw.Kit.BalanceAsync(tw.I)).Should().Be(50m);
        (await tw.Kit.BalanceRowAsync(tw.X)).Should().BeNull("birleşmenin açtığı satır geri alındı");
        (await tw.Kit.GroupRowsAsync(key)).Select(r => (r.WpfCustomerId, r.Amount))
            .Should().Equal((tw.T, 0m), (tw.I, -50m));
        foreach (var row in new[] { tw.T, tw.I, tw.X })
            (await tw.Kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    // ── TC8: apply ∥ group-customers (§8.2-8) ────────────────────────────────

    /// <summary>
    /// g1 = {A (çapa, 0), B (40)}, g0 = {C (100)}; apply A'dan 30, <c>W</c> = {A, B}. <c>group-customers</c> (A, C)
    /// apply'ın kaydında commit eder. Apply ya B'den 30 çeker (200) ya da birleşen kişide <c>W</c> dışında pozitif C
    /// görür (kural 3b, 409); hangisi olursa olsun <c>W</c> dışındaki C'den ASLA çekilmez.
    /// </summary>
    [Fact]
    public async Task Tc8_group_customers_kayit_aninda_W_disindaki_uyeden_cekim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a = await kit.MemberAsync(g1);
        var b = await kit.MemberAsync(g1, 40m);
        var c = await kit.MemberAsync(g0, 100m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        _factory.Hook.Once(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var report = await new CustomerGroupingJob(db)
                .RunAsync(kit.LicenseId, [new CustomerGroupingJob.Pair(a, c)], apply: true, default);
            report.FailedComponents.Should().Be(0);
        });

        var result = await StatusAsync(() => kit.ApplyAsync(a, 30m, 30m, key, null, [a, b]));
        _factory.Hook.Disarm();

        (await kit.ProjectionAsync(a)).GroupId.Should().Be((await kit.ProjectionAsync(c)).GroupId, "gruplama kaydedildi");
        (await kit.BalanceAsync(c)).Should().Be(100m, "W dışındaki üyeden ASLA çekilmez");
        var rows = await kit.GroupRowsAsync(key);
        rows.Should().NotContain(r => r.WpfCustomerId == c);
        // Dal veriden seçilir: A ve B g0'a taşındıysa jetonları ilerledi, apply'ın ilk kaydı tutmamalıydı.
        var moved = (await kit.ProjectionAsync(b)).GroupId == g0;
        if (!moved)
        {
            (await kit.ProjectionAsync(c)).GroupId.Should().Be(g1, "A ve B yerinde kaldıysa C g1'e geldi");
            result.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 30m),
                "A ve B'nin jetonu değişmedi; P = W ∩ S(A) = {A, B}'den çekim meşru");
            (await kit.BalanceAsync(b)).Should().Be(10m);
            rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, 0m), (b, -30m));
        }
        else
        {
            result.Should().Be(new ApplyOutcome(HttpStatusCode.Conflict, PersonBalance.CodeCustomerNotSynced, null),
                "birleşen kişide W dışında pozitif C (kural 3b)");
            _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0,
                "A ve B taşındı; jetonları ilerledi, apply baştan kurdu");
            (await kit.BalanceAsync(b)).Should().Be(40m);
            rows.Should().BeEmpty();
        }
        foreach (var row in new[] { a, b, c })
        {
            (await kit.BalanceAsync(row)).Should().BeGreaterThanOrEqualTo(0m, "hiçbir satır eksiye inmez");
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        }
    }

    /// <summary>
    /// Ters sıra: apply gruplama bileşeninin kaydında commit eder (A ve B o an g1'de, çekim meşru). Gruplamanın
    /// <c>PersonGroupSaveHook</c>'u A ve B'nin bakiye satırlarını apply'dan önce yüklemişti; gruplamanın UPDATE'i
    /// apply'ın ilerlettiği jetona çarpar, bileşen geri alınır ve sayılır (<c>CustomerGroupingJob.cs:166-189</c>).
    /// Yeniden koşu kalanı bulur.
    /// </summary>
    [Fact]
    public async Task Tc8_apply_once_commit_ederse_gruplama_bileseni_geri_alinir_ve_sayilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a = await kit.MemberAsync(g1);
        var b = await kit.MemberAsync(g1, 40m);
        var c = await kit.MemberAsync(g0, 100m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        var atGroupingSave = false;
        ApplyOutcome? apply = null;
        _factory.Hook.Once(async saving =>
        {
            // Kanca gruplamanın kaydında olmalı: kaydedilen bağlamda A g0'a taşınmış.
            atGroupingSave = saving.ChangeTracker.Entries<WpfCustomerProjection>()
                .Any(e => e.Entity.Id == a && e.Entity.GroupId == g0);
            apply = await StatusAsync(() => kit.ApplyAsync(a, 30m, 30m, key, null, [a, b]));
        });

        CustomerGroupingJob.Report report;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            report = await new CustomerGroupingJob(db)
                .RunAsync(kit.LicenseId, [new CustomerGroupingJob.Pair(a, c)], apply: true, default);
        }
        _factory.Hook.Disarm();

        atGroupingSave.Should().BeTrue("apply gruplama bileşeninin SaveChanges'inde commit etmeli");
        apply.Should().Be(new ApplyOutcome(HttpStatusCode.OK, null, 30m), "apply gruplamadan önce commit etti");
        report.Components.Should().Be(1);
        report.FailedComponents.Should().Be(1, "bileşen A/B'nin ilerlemiş jetonuna çarptı; geri alındı ve sayıldı");
        _factory.Hook.ConcurrencyFailures.Should().Be(1, "yalnız gruplamanın kaydı çakıştı");
        (await kit.ProjectionAsync(a)).GroupId.Should().Be(g1, "gruplama geri alındı");
        (await kit.ProjectionAsync(b)).GroupId.Should().Be(g1);
        (await kit.ProjectionAsync(c)).GroupId.Should().Be(g0);
        (await kit.BalanceAsync(b)).Should().Be(10m);
        (await kit.BalanceAsync(c)).Should().Be(100m, "W dışındaki üyeden ASLA çekilmez");
        (await kit.GroupRowsAsync(key)).Select(r => (r.WpfCustomerId, r.Amount))
            .Should().Equal((a, 0m), (b, -30m));
        foreach (var row in new[] { a, b, c })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }
}
