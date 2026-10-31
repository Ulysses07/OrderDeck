using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using ApplyController = OrderDeck.LicenseServer.Controllers.Licenses.LicensesCustomerBalanceApplyController;
using ApplyResponse = OrderDeck.LicenseServer.Controllers.Licenses.LicensesCustomerBalanceApplyController.ApplyResponse;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Havuzlu apply'ın eşzamanlılık sözleşmesi gerçek SQL Server'da (spec 2026-10-11 §8.2-1, -2, -10; §4.2 adım 6;
/// §4.13): kişi kilidi havuzu eksiye düşürmez (D3), aynı anahtarla yarışta tek bacak kümesi yazılır ve kaybeden
/// kazananı oynatır, her denemenin başında Lookup koşar, 1. denemede havuzu tüketen aynı anahtarlı rakip
/// <c>no-balance</c> yerine oynatılır (no-balance öncesi son Lookup), üç çakışmadan sonra 503 <c>balance-busy</c> (hiçbir şey
/// yazılmaz), çapanın bakiye satırı açma yarışı baştan kurulur. Bütün testler satış senkronu mod Off'ta: kapsam
/// kuralının kilidi okuma kancasındaki rakibi bekletirdi (§8.2-15 ayrı sınıfta). InMemory'de tekil indeks ve atomik
/// kayıt yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceDrawRaceTests : IAsyncLifetime
{
    private const int Parallel = 8;

    private readonly SqlServerContainerFixture _sql;
    private PersonBalanceRelationalApiFactory _factory = null!;

    public PersonBalanceDrawRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() =>
        _factory = new PersonBalanceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Queries.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Bir apply isteğinin sonucu; başarısızsa yalnız durum ve problem başlığı.</summary>
    private sealed record Outcome(
        HttpStatusCode Status, string? Title, Guid TransactionId = default, decimal Applied = 0m, decimal Remaining = 0m);

    /// <summary>
    /// Durum, (başarısızsa) problem başlığı, (başarılıysa) yanıt gövdesi. TestServer, sunucu tarafında yakalanmayan
    /// istisnayı istemciye fırlatır; prod'da 500 olurdu — başlık yerine en içteki istisna, düşen koşunun mesajı
    /// nedeni göstersin (emsal <c>PersonBalanceReversalRaceTests.StatusAsync</c>).
    /// </summary>
    private static async Task<Outcome> StatusAsync(Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await call();
        }
        catch (Exception e)
        {
            var root = e.GetBaseException();
            return new Outcome(HttpStatusCode.InternalServerError, $"{root.GetType().Name}: {root.Message}");
        }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) return new Outcome(resp.StatusCode, await Kit.TitleAsync(resp));
            var body = (await resp.Content.ReadFromJsonAsync<ApplyResponse>())!;
            return new Outcome(resp.StatusCode, null, body.TransactionId, body.AppliedAmount, body.RemainingBalance);
        }
    }

    /// <summary>Kişi {A = 100, B = −60, C = 50} (havuz 90), havuz kipinde — §4.13'ün kişi kilidi örneği.</summary>
    private static async Task<(Guid A, Guid B, Guid C)> AbcAsync(Kit kit)
    {
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);
        var c = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        return (a, b, c);
    }

    /// <summary>Spec Örnek 1'in ÖNCESİ (T = 0, I = 100, Y = 30; havuz 130), havuz kipinde.</summary>
    private static async Task<(Guid T, Guid I, Guid Y)> Ornek1OncesiAsync(Kit kit)
    {
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        await kit.EnablePoolAsync();
        return (t, i, y);
    }

    /// <summary>Verilen satırların bakiyeleri toplamı (kişinin havuzu).</summary>
    private static async Task<decimal> PoolAsync(Kit kit, params Guid[] ids)
    {
        var sum = 0m;
        foreach (var id in ids) sum += await kit.BalanceAsync(id);
        return sum;
    }

    /// <summary>D1: her satırda bakiye = defter toplamı.</summary>
    private static async Task ShouldHoldLedgerAsync(Kit kit, params Guid[] ids)
    {
        foreach (var id in ids)
            (await kit.LedgerHoldsAsync(id)).Should().BeTrue("D1 ({0})", id);
    }

    /// <summary>Lisansın bütün düşüm satırları (çapalar ve bacaklar).</summary>
    private static Task<List<CustomerBalanceTransaction>> DeductionsAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindDeduction)
            .ToListAsync());

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId));

    /// <summary>
    /// Denetleyicinin bu anahtar için yazdığı oynatma satırları: <c>LookupAsync</c> her oynatmada bir
    /// <c>"tekrar oynatıldı"</c> Information satırı yazar, çağıran sonucu kullansa da kullanmasa da — yok sayılan bir
    /// Lookup sonucu satır sayısında görünür.
    /// </summary>
    private int ReplayLogCount(Guid key) =>
        _factory.Log.Entries.Count(e => e.Category == typeof(ApplyController).FullName
            && e.Message.Contains("tekrar oynatıldı", StringComparison.Ordinal)
            && e.Message.Contains(key.ToString(), StringComparison.Ordinal));

    /// <summary>Denetleyicinin <c>DuplicateKey</c> dalının uyarısı (PK yarışında kazanan oynatıldı), bu anahtar için.</summary>
    private int DuplicateKeyWarningCount(Guid key) =>
        _factory.Log.Entries.Count(e => e.Category == typeof(ApplyController).FullName
            && e.Level == LogLevel.Warning
            && e.Message.Contains("Bakiye uygulama yarışı", StringComparison.Ordinal)
            && e.Message.Contains(key.ToString(), StringComparison.Ordinal));

    /// <summary><see cref="PersonBalance"/>'ın baştan kurma satırları (<c>"… baştan kuruluyor"</c>), bu çapa için.</summary>
    private List<LogEntry> RetryLogs(Guid anchor) =>
        _factory.Log.Entries
            .Where(e => e.Category == typeof(PersonBalance).FullName
                && e.Level == LogLevel.Information
                && e.Message.Contains("baştan kuruluyor", StringComparison.Ordinal)
                && e.Message.Contains(anchor.ToString(), StringComparison.Ordinal))
            .ToList();

    /// <summary>Örnek 1'in yazımı tam bir kez: tek bacak kümesi, başka düşüm yok, T 0, I 0, Y 10, D1.</summary>
    private static async Task ShouldBeOrnek1OnceAsync(Kit kit, Guid k, Guid t, Guid i, Guid y)
    {
        var rows = await kit.GroupRowsAsync(k);
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -100m), (y, -20m) }, "tek bacak kümesi: Örnek 1");
        rows[0].Id.Should().Be(k, "çapa = anahtar, ilk satır");
        (await DeductionsAsync(kit)).Should().HaveCount(3, "aynı anahtarla ikinci bir grup ya da bacak yazılmadı");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m);
        (await kit.BalanceAsync(y)).Should().Be(10m, "Y'den yalnız bir kez 20 düştü");
        await ShouldHoldLedgerAsync(kit, t, i, y);
    }

    /// <summary>
    /// §8.2-1, belirlenimli (§4.13'ün örneği birebir): C'den 50'lik apply'ın (dış istek) kaydı SQL gitmeden önce A'dan
    /// farklı anahtarlı 90'lık apply (rakip) commit eder. Çapa A'da 100 olduğu için rakip yalnız A'yı çeker; dış istek de
    /// çapa C'deki 50 ile yalnız C'yi. Çekilen satırlar ayrıktır: kişi kilidi (jeton dokunuşu) olmasaydı iki yazım da
    /// commit eder, havuz 90 − 90 − 50 = −50'ye inerdi (D3 ihlali). Rakip B ile C'ye, dış istek A ile B'ye dokunduğu
    /// için dış isteğin UPDATE'i tutmaz; taze planda Σ = 10 − 60 + 50 = 0 → 409 <c>no-balance</c>.
    /// </summary>
    [Fact]
    public async Task Tc1_iki_uyeden_farkli_anahtarli_apply_taze_planla_no_balance()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (a, b, c) = await AbcAsync(kit);
        var k1 = Guid.NewGuid();
        var k2 = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        var atDrawSave = false;
        _factory.Hook.Once(async saving =>
        {
            // Kanca dış isteğin çekim kaydında tetiklenmeli (istekte daha erken bir SaveChanges yok).
            atDrawSave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == k1);
            rakipler.Add(await StatusAsync(() => kit.ApplyAsync(a, 90m, 90m, k2, null, [a, b, c])));
        });

        var dis = await StatusAsync(() => kit.ApplyAsync(c, 50m, 50m, k1, null, [a, b, c]));
        _factory.Hook.Disarm();

        atDrawSave.Should().BeTrue("rakip, dış apply'ın çekim kaydında commit etmeli");
        var rakip = rakipler.Should().ContainSingle("kanca bir kez koşar").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k2, 90m, 0m), "rakip A'dan 90 çeker, Σ 90 − 90");
        (dis.Status, dis.Title).Should().Be((HttpStatusCode.Conflict, PersonBalance.CodeNoBalance),
            "taze planda Σ = 10 − 60 + 50 = 0 (§4.13 kişi kilidi)");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1,
            "dış istek yalnız C'yi çekti; rakip C'ye, dış istek A'ya dokunduğu için rakibin commit'ine çarptı. "
            + "Dokunuş olmasaydı ayrık satırlar commit eder, havuz −50 olurdu");

        (await kit.GroupRowsAsync(k2)).Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (a, -90m) }, "rakip tek bacak: A; B ile C'ye yalnız dokundu");
        (await kit.GroupRowsAsync(k1)).Should().BeEmpty("kaybeden hiçbir şey yazmadı");
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(-60m, "eksi satır kilitlenir, çekilmez");
        (await kit.BalanceAsync(c)).Should().Be(50m, "dış isteğin C çekimi geri alındı");
        (await PoolAsync(kit, a, b, c)).Should().Be(0m, "havuz hiçbir an < 0 değil (D3)");
        await ShouldHoldLedgerAsync(kit, a, b, c);
    }

    /// <summary>
    /// §8.2-1, paralel: aynı kişinin iki üyesinden (A ve C sırayla) farklı anahtarlı sekiz apply, her biri 30. Havuz 90
    /// olduğu için en çok üç commit olur; kaybedenler taze planda 409 <c>no-balance</c> alır ya da üç çakışmanın üçünü
    /// de yitirip 503 <c>balance-busy</c> (hiçbir şey yazmaz). Haritadaki "paralel apply için gerçek SQL testi yok"
    /// boşluğunu kapatır.
    /// </summary>
    [Fact]
    public async Task Tc1_paralel_sekiz_apply_havuzu_eksiye_dusurmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (a, b, c) = await AbcAsync(kit);
        // Isınma: yazımsız havuzlu preview — rota, EF JIT ve kümenin sorguları; gerçek turda istekler ayrışmasın.
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => kit.PreviewAsync(a, [a, b, c])));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(n =>
            StatusAsync(() => kit.ApplyAsync(n % 2 == 0 ? a : c, 30m, 30m, Guid.NewGuid(), null, [a, b, c]))));

        var seen = string.Join(", ", results.Select(r => $"{(int)r.Status} {r.Title} {r.Applied}"));
        results.Should().OnlyContain(r => r.Status == HttpStatusCode.OK
                || (r.Status == HttpStatusCode.Conflict && r.Title == PersonBalance.CodeNoBalance)
                || (r.Status == HttpStatusCode.ServiceUnavailable && r.Title == "balance-busy"),
            "500 ya da başka bir ret kişi kilidi/kilitlenme yolunun eşleşmediğini gösterir ({0})", seen);
        var winners = results.Where(r => r.Status == HttpStatusCode.OK).ToList();
        winners.Should().NotBeEmpty("ilk commit eden kazanır ({0})", seen)
            .And.OnlyContain(r => r.Applied == 30m, "havuz 30'un katı, kısmi çekim yok ({0})", seen);
        var drawn = winners.Sum(r => r.Applied);
        drawn.Should().BeLessThanOrEqualTo(90m, "havuzdan fazlası çekilmez ({0})", seen);

        (await kit.BalanceAsync(a)).Should().BeGreaterThanOrEqualTo(0m);
        (await kit.BalanceAsync(c)).Should().BeGreaterThanOrEqualTo(0m);
        (await kit.BalanceAsync(b)).Should().Be(-60m, "eksi satır kilitlenir, çekilmez");
        (await PoolAsync(kit, a, b, c)).Should().Be(90m - drawn, "havuz = 90 − Σ AppliedAmount")
            .And.BeGreaterThanOrEqualTo(0m, "havuz hiçbir an < 0 değil (D3)");
        (await DeductionsAsync(kit)).Count(t => t.Id == t.DrawGroupId).Should()
            .Be(winners.Count, "yalnız kazananlar çapa yazar; 409 ve 503 hiçbir şey yazmaz ({0})", seen);
        await ShouldHoldLedgerAsync(kit, a, b, c);
    }

    /// <summary>
    /// §8.2-2, belirlenimli: aynı anahtarla (K) çok bacaklı apply yarışı. Rakip aynı istek dış kaydın içinde, SQL gitmeden
    /// commit eder; dış istek PK'ya (→ <c>DuplicateKey</c> → Lookup) ya da jetona (→ 2. denemenin Lookup'ı) çarpar —
    /// ikisinde de bütün bacaklar geri alınır ve kazananın sonucu oynatılır.
    /// </summary>
    [Fact]
    public async Task Tc2_ayni_anahtarla_cok_bacakli_yaris_tek_grup_kazanan_oynatilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        var atDrawSave = false;
        _factory.Hook.Once(async saving =>
        {
            atDrawSave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == k);
            rakipler.Add(await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y])));
        });

        var dis = await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]));
        _factory.Hook.Disarm();

        atDrawSave.Should().BeTrue("rakip, dış apply'ın çekim kaydında commit etmeli");
        var rakip = rakipler.Should().ContainSingle("kanca bir kez koşar").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 120m, 10m), "rakip Örnek 1'i yazar: (K, 120, 10)");
        dis.Status.Should().Be(HttpStatusCode.OK, "kaybeden kazananı oynatır ({0})", dis.Title);
        (dis.TransactionId, dis.Applied).Should().Be((rakip.TransactionId, rakip.Applied),
            "oynatılan sonuç kazananınki: çapa K, tutar −Σ(grup)");
        await ShouldBeOrnek1OnceAsync(kit, k, t, i, y);
    }

    /// <summary>§8.2-2, paralel: aynı istek (aynı anahtar K) sekiz kez birlikte — hepsi 200 ve aynı sonuç, tek grup.</summary>
    [Fact]
    public async Task Tc2_paralel_sekiz_ayni_anahtar_tek_grup()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => kit.PreviewAsync(t, [t, i, y])));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel)
            .Select(_ => StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]))));

        var seen = string.Join(", ", results.Select(r => $"{(int)r.Status} {r.Title} {r.TransactionId:N} {r.Applied}"));
        results.Should().OnlyContain(r => r.Status == HttpStatusCode.OK && r.TransactionId == k && r.Applied == 120m,
            "tek kazanan yazar, kaybedenler PK ya da 2. denemenin Lookup'ıyla onu oynatır ({0})", seen);
        await ShouldBeOrnek1OnceAsync(kit, k, t, i, y);
    }

    /// <summary>
    /// §8.2-10 (§4.2 adım 6 "2. ve sonraki her denemenin başında Lookup"): dış istek X (anahtar K, tutar 230) kaydında
    /// araya üçüncü bir yazım girer — panel iadesi I'ye +100, I'nin jetonunu ilerletir, X'in 1. denemesi (130'luk plan)
    /// geri alınır. X'in 2. denemesinin başındaki Lookup sorgusundan hemen önce aynı anahtarlı rakip commit eder ve
    /// iadeden sonraki havuzun tamamını (230) çekerek havuzu SIFIRLAR (zincir jetona dayanır, PK'ya değil). X'in Lookup'ı
    /// K'yı bulur: <c>no-balance</c> değil, ikinci düşüm değil — kazananın sonucu. Lookup'sız bir döngüde okuma kancası
    /// tetiklenmez, X 2. denemede havuzdan kendi 230'unu çekerdi (§14-8): <c>rakipler</c> boş kalır. Lookup koşup sonucu
    /// yok sayılsaydı yanıt YİNE 200 olurdu — Görev 7'nin <c>no-balance</c> öncesi son Lookup'ı Σ = 0'dan sonra K'yı
    /// oynatır (Görev 7 Not 6) — bu yüzden ayırt eden iddia denetleyicinin oynatma günlüğüdür: tek
    /// <c>"tekrar oynatıldı"</c> satırı (yok sayılan Lookup + son Lookup = iki satır).
    /// </summary>
    [Fact]
    public async Task Tc10_araya_giren_ucuncu_yazimla_ikinci_istek_kazanani_oynatir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        var atDrawSave = false;
        _factory.Hook.Once(async saving =>
        {
            atDrawSave = saving.ChangeTracker.Entries<CustomerBalanceTransaction>()
                .Any(e => e.State == EntityState.Added && e.Entity.Id == k);
            // Üçüncü yazım: I'ye +100 → X'in 1. denemesi (T 0, I −100, Y −30) I'nin jetonuna çarpar.
            (await kit.PanelRefundAsync(i, 100m)).EnsureSuccessStatusCode();
            // X'in 2. denemesi önce Lookup'ı koşar (defter okuması); rakip ondan hemen önce commit eder ve
            // iadeden sonraki havuzun tamamını (0 + 200 + 30) çeker.
            _factory.Queries.Arm(
                sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal),
                async () => rakipler.Add(
                    await StatusAsync(() => kit.ApplyAsync(t, 230m, 230m, k, Kit.Session42, [t, i, y]))));
        });

        // 1. denemede applied = min(230, 130, 230) = 130.
        var dis = await StatusAsync(() => kit.ApplyAsync(t, 230m, 230m, k, Kit.Session42, [t, i, y]));
        _factory.Hook.Disarm();
        _factory.Queries.Disarm();

        atDrawSave.Should().BeTrue("üçüncü yazım X'in çekim kaydında girmeli");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1,
            "X'in 1. denemesi iadenin ilerlettiği jetonla geri alınmalı");
        var rakip = rakipler.Should().ContainSingle("rakip X'in 2. denemesinin Lookup'ından önce koşmalı").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 230m, 0m),
            "rakip iadeden sonraki havuzun tamamını çeker: (K, 230, 0)");
        dis.Status.Should().Be(HttpStatusCode.OK,
            "no-balance değil: 2. denemenin başındaki Lookup kazananı bulur, havuz artık 0 ({0})", dis.Title);
        (dis.TransactionId, dis.Applied).Should().Be((rakip.TransactionId, rakip.Applied),
            "X kazananın sonucunu oynatır");
        // Yanıt, sonucu yok sayılan bir Lookup'ı ayırt etmez (Görev 7'nin no-balance öncesi son Lookup'ı kurtarır);
        // günlük ayırır. Ön oynatma rakipten önce koştu (K yoktu), rakip ilk yazan: oynatma yalnız X'in 2. denemesinde.
        ReplayLogCount(k).Should().Be(1,
            "tek oynatma: X'in 2. denemesinin başındaki Lookup. İki satır = o Lookup'ın sonucu yok sayıldı ve kazananı "
            + "Görev 7'nin no-balance öncesi son Lookup'ı oynattı (R33, §4.2-6)");
        DuplicateKeyWarningCount(k).Should().Be(0, "PK yolu değil: X'in 2. denemesi yazıma varmadı");
        RetryLogs(k).Should().ContainSingle("tek baştan kurma: 1. deneme jetona çarptı, 2. deneme Lookup'ta durdu")
            .Which.Message.Should().Contain("jeton çakışması");

        // İadeden sonra I = 200: rakibin taze planı çapa T (0) → I'den 200 → Y'den 30.
        (await kit.GroupRowsAsync(k)).Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -200m), (y, -30m) }, "tek grup, rakibin taze planı");
        (await DeductionsAsync(kit)).Should().HaveCount(3)
            .And.OnlyContain(r => r.Id == k || r.DrawGroupId == k, "ikinci düşüm yok: K'dan başka grup yazılmadı");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m, "100 + 100 iade − 200, bir kez");
        (await kit.BalanceAsync(y)).Should().Be(0m, "30 − 30, bir kez");
        await ShouldHoldLedgerAsync(kit, t, i, y);
    }

    /// <summary>
    /// §4.2 adım 6'nın amacı 1. denemede (Görev 7 Not 6, iskelet farkı "no-balance'tan önce son bir Lookup"): mod Off,
    /// kapsamsız istek — 1. denemenin tek Lookup'ı kilitsiz ön oynatmadır. X'in (anahtar K, 130) ön oynatmasından SONRA,
    /// <c>DrawAsync</c>'in 1. denemesindeki ilk bakiye okumasından (çekim kümesinin izlemesiz okuması) hemen ÖNCE aynı
    /// anahtarlı rakip commit eder ve havuzun tamamını (130) çeker. X Σ = 0 görür; ne jeton çakışması ne PK ihlali olur
    /// (yazıma varmaz), <c>DrawAsync</c> <c>NoBalance</c> döner. Beklenen: 409 <c>no-balance</c> değil, kazananın sonucu —
    /// <c>ApplyPoolAsync</c>'in <c>NoBalance</c> dalındaki son Lookup K'yı bulur. O Lookup olmasaydı WPF kesin
    /// "bakiye yok" yazardı (<c>MarkNoBalance</c>), oysa anahtar defterde ve para düşülmüş.
    /// </summary>
    [Fact]
    public async Task Ilk_denemede_havuzu_tuketen_ayni_anahtarli_rakip_oynatilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        var onOynatmaGecti = false;
        // 1. halka: X'in ön oynatması (Apply'ın kilitsiz LookupAsync'i, isteğin ilk defter okuması). K henüz yok;
        // halka yalnız bir sonrakini kurar.
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal),
            () =>
            {
                onOynatmaGecti = true;
                // 2. halka: X'in bundan sonraki ilk bakiye okuması = DrawAsync'in 1. denemesinde çekim kümesinin
                // izlemesiz okuması (ResolveDrawSetAsync → MembersAsync). Rakip ondan hemen önce aynı anahtarla commit
                // eder ve havuzun tamamını (0 + 100 + 30) çeker.
                _factory.Queries.Arm(
                    sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
                    async () => rakipler.Add(
                        await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y]))));
                return Task.CompletedTask;
            });

        var dis = await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y]));
        _factory.Queries.Disarm();

        onOynatmaGecti.Should().BeTrue("1. halka X'in ön oynatmasında kurulmalı");
        var rakip = rakipler.Should().ContainSingle("rakip X'in 1. deneme bakiye okumasından önce koşmalı").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 130m, 0m),
            "rakip ilk yazan, havuzun tamamını çeker: (K, 130, 0)");
        dis.Should().Be(rakip,
            "no-balance değil: NoBalance dalındaki son Lookup kazananı oynatır, havuz artık 0 ({0})", dis.Title);
        _factory.Hook.ConcurrencyFailures.Should().Be(0, "X hiç yazmadı: Σ = 0 planı jetona varmaz");
        RetryLogs(k).Should().BeEmpty("X 1. denemede NoBalance aldı; baştan kurma yok, 2. deneme Lookup'ı koşmadı");
        DuplicateKeyWarningCount(k).Should().Be(0, "PK yolu değil: havuzun tamamı tükendi, X çapa yazmaya varmadı");
        ReplayLogCount(k).Should().Be(1,
            "tek oynatma: NoBalance dalının son Lookup'ı (ön oynatma rakipten önce koştu, kapsamsız 1. denemede geri "
            + "çağrı Lookup koşmaz)");

        (await kit.GroupRowsAsync(k)).Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -100m), (y, -30m) }, "tek grup, rakibin planı");
        (await DeductionsAsync(kit)).Should().HaveCount(3)
            .And.OnlyContain(r => r.Id == k || r.DrawGroupId == k, "ikinci düşüm yok: K'dan başka grup yazılmadı");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m, "100 − 100, bir kez");
        (await kit.BalanceAsync(y)).Should().Be(0m, "30 − 30, bir kez");
        await ShouldHoldLedgerAsync(kit, t, i, y);
    }

    /// <summary>
    /// §4.2 "kayıtlı anahtar hiçbir zaman customer-not-synced almaz", 1. denemede: yukarıdaki pencerenin aynısı, ret
    /// kural 3'ten. Kişide WPF'in bilmediği artı bir üye Z var; X (anahtar K) <c>memberIds</c>'te Z'yi göndermez, tek
    /// başına 409 <c>customer-not-synced</c> alırdı. Aynı anahtarlı rakip Z'yi de göndererek ön oynatmadan SONRA commit
    /// eder. Beklenen: X ret değil, kazananın sonucunu alır — <c>CustomerNotSynced</c> dalındaki son Lookup K'yı bulur.
    /// </summary>
    [Fact]
    public async Task Ilk_denemede_ayni_anahtarli_rakip_yazdiysa_customer_not_synced_yerine_oynatilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        // Rakibin çekiminden sonra da artıda kalır (130 < 500): X'in kural 3 reddi rakibin planına bağlı değil.
        var z = await kit.MemberAsync(g, 500m);
        await kit.EnablePoolAsync();
        var k = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal),
            () =>
            {
                _factory.Queries.Arm(
                    sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
                    async () => rakipler.Add(
                        await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y, z]))));
                return Task.CompletedTask;
            });

        var dis = await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y]));
        _factory.Queries.Disarm();

        var rakip = rakipler.Should().ContainSingle("rakip X'in 1. deneme bakiye okumasından önce koşmalı").Subject;
        rakip.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 130m, 500m),
            "rakip ilk yazan, Z'yi de bilir: havuz 630, kalan 500 ({0})", rakip.Title);
        dis.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 130m, await PoolAsync(kit, t, i, y)),
            "customer-not-synced değil: dalın son Lookup'ı kazananı oynatır, kalan X'in W'sinin o anki havuzu ({0})",
            dis.Title);
        // Dal sabitleyicileri (kardeş testteki gibi): oynatma DuplicateKey'in Lookup'ından da gelebilirdi; X kural
        // 3'te reddedildi, yazıma varmadı.
        DuplicateKeyWarningCount(k).Should().Be(0, "PK yolu değil: X kural 3'te reddedildi, çapa yazmaya varmadı");
        _factory.Hook.ConcurrencyFailures.Should().Be(0, "X hiç yazmadı: ret jetona varmaz");
        RetryLogs(k).Should().BeEmpty("X 1. denemede reddedildi; baştan kurma yok");
        ReplayLogCount(k).Should().Be(1, "tek oynatma: CustomerNotSynced dalının son Lookup'ı");
        (await DeductionsAsync(kit)).Should().OnlyContain(r => r.Id == k || r.DrawGroupId == k,
            "ikinci düşüm yok: K'dan başka grup yazılmadı");
        (await PoolAsync(kit, t, i, y, z)).Should().Be(500m, "630 − 130, bir kez");
        await ShouldHoldLedgerAsync(kit, t, i, y, z);
    }

    /// <summary>
    /// Plan fark 18n (emsal 18k), çekimden sonraki Lookup: yukarıdaki <c>NoBalance</c> penceresinde rakip commit ettikten sonra
    /// X'in son Lookup'ı kilitlenme kurbanı olur (kanca o komutta bir kez <c>SqlException(1205)</c> fırlatır). Lookup
    /// <c>DrawAsync</c>'in korunan denemesinin dışında; X hiçbir şey yazmadı. Beklenen 503 <c>balance-busy</c> (WPF aynı
    /// anahtarla yeniden dener ve oynatılır), 500 değil.
    /// </summary>
    [Fact]
    public async Task Cekimden_sonraki_Lookup_kilitlenme_kurbani_olursa_503_balance_busy()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        var rakipler = new List<Outcome>();
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal),
            () =>
            {
                _factory.Queries.Arm(
                    sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
                    async () =>
                    {
                        rakipler.Add(await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y])));
                        // 3. halka: X'in bundan sonraki ilk defter okuması = NoBalance dalındaki son Lookup (kapsamsız
                        // 1. denemede DrawAsync defter okumaz, geri çağrı Lookup koşmaz).
                        _factory.Queries.Arm(
                            sql => sql.Contains("FROM [CustomerBalanceTransactions]", StringComparison.Ordinal),
                            () => throw SqlExceptionFactory.Create(
                                1205, "Transaction was deadlocked and has been chosen as the deadlock victim."));
                    });
                return Task.CompletedTask;
            });

        var dis = await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y]));
        _factory.Queries.Disarm();

        rakipler.Should().ContainSingle().Which.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 130m, 0m));
        dis.Should().Be(new Outcome(HttpStatusCode.ServiceUnavailable, "balance-busy"),
            "son Lookup'ın 1205'i 500 değil, yeniden denenecek 503 ({0})", dis.Title);
        _factory.Log.Entries.Count(e => e.Category == typeof(ApplyController).FullName
                && e.Message.Contains("kilitlenme kurbanı (1205)", StringComparison.Ordinal)
                && e.Message.Contains(k.ToString(), StringComparison.Ordinal))
            .Should().Be(1, "kaynağı ayıran tek satır");
        ReplayLogCount(k).Should().Be(0, "kurban Lookup oynatmaya varmadı");
        (await kit.GroupRowsAsync(k)).Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -100m), (y, -30m) }, "yalnız rakibin grubu");
        await ShouldHoldLedgerAsync(kit, t, i, y);

        (await StatusAsync(() => kit.ApplyAsync(t, 130m, 130m, k, null, [t, i, y]))).Should()
            .Be(new Outcome(HttpStatusCode.OK, null, k, 130m, 0m), "WPF'in yeniden denemesi kazananı oynatır");
    }

    /// <summary>
    /// Aynı karar Apply'ın ön oynatmasında: havuz kipinde kayıtlı anahtarın oynatması <c>PoolBalanceAsync</c> ile
    /// CustomerBalances'ı okur. WPF yanıtı kaybolmuş bir apply'ı aynı anahtarla yeniden dener, o okuma kilitlenme
    /// kurbanı olur (kanca bir kez <c>SqlException(1205)</c> fırlatır). İstekte ondan önce bakiye okuması yok
    /// (sahiplik, asıl kayıt ve defter okumaları). Beklenen 503 <c>balance-busy</c>, 500 değil; sonraki deneme oynatılır.
    /// </summary>
    [Fact]
    public async Task On_oynatmanin_bakiye_okumasi_kilitlenme_kurbani_olursa_503_balance_busy()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var k = Guid.NewGuid();
        var ilk = await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]));
        ilk.Should().Be(new Outcome(HttpStatusCode.OK, null, k, 120m, 10m), "Örnek 1 ({0})", ilk.Title);
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
            () => throw SqlExceptionFactory.Create(
                1205, "Transaction was deadlocked and has been chosen as the deadlock victim."));

        var tekrar = await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]));
        _factory.Queries.Disarm();

        tekrar.Should().Be(new Outcome(HttpStatusCode.ServiceUnavailable, "balance-busy"),
            "ön oynatmanın 1205'i 500 değil, yeniden denenecek 503 ({0})", tekrar.Title);
        _factory.Log.Entries.Count(e => e.Category == typeof(ApplyController).FullName
                && e.Message.Contains("kilitlenme kurbanı (1205)", StringComparison.Ordinal)
                && e.Message.Contains(k.ToString(), StringComparison.Ordinal))
            .Should().Be(1, "kaynağı ayıran tek satır");
        ReplayLogCount(k).Should().Be(0, "kurban Lookup oynatmaya varmadı");

        (await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]))).Should()
            .Be(new Outcome(HttpStatusCode.OK, null, k, 120m, 10m), "sonraki deneme oynatılır");
        await ShouldBeOrnek1OnceAsync(kit, k, t, i, y);
    }

    /// <summary>
    /// §4.2 adım 6 / §4.13: üç denemede de jeton çakışması → 503 <c>balance-busy</c>; SaveChanges tek işlem olduğu için
    /// hiçbir şey yazılmaz. Servis tek uyarı yazar, sayaç <c>endpoint=apply</c> ile bir artar (Görev 13; InMemory'de üç
    /// çakışma kurulamadığı için sayaç burada doğrulanır).
    /// </summary>
    [Fact]
    public async Task Uc_denemede_de_cakisan_apply_503_balance_busy_hicbir_sey_yazmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (t, i, y) = await Ornek1OncesiAsync(kit);
        var oncekiHareketler = await TransactionCountAsync(kit);
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
        var k = Guid.NewGuid();
        _factory.Hook.Always(() => TouchBalanceAsync(i));

        var sonuc = await StatusAsync(() => kit.ApplyAsync(t, 120m, 120m, k, null, [t, i, y]));
        _factory.Hook.Disarm();

        (sonuc.Status, sonuc.Title).Should().Be((HttpStatusCode.ServiceUnavailable, "balance-busy"));
        _factory.Hook.ConcurrencyFailures.Should().Be(PersonBalance.MaxAttempts);
        (await TransactionCountAsync(kit)).Should().Be(oncekiHareketler, "SaveChanges tek işlem: yeni hareket yok");
        (await kit.GroupRowsAsync(k)).Should().BeEmpty();
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(100m);
        (await kit.BalanceAsync(y)).Should().Be(30m);
        _factory.Log.Entries
            .Where(e => e.Category == typeof(PersonBalance).FullName && e.Level == LogLevel.Warning)
            .Should().ContainSingle("tükenişte tek uyarı").Which.Message.Should().Contain("balance-busy");
        olcumler.Should().ContainSingle("tek 503, tek ölçüm").Which.Should().Be((1L, "apply"));
    }

    /// <summary>
    /// §4.2 adım 5–6 (R30/R34): çapa T'nin bakiye satırı yok; apply onu 0 ile açarken kaydından hemen önce panel iadesi
    /// T'nin satırını açar. Tekil <c>(LicenseId, WpfCustomerId)</c> ihlali "bakiye satırı açma yarışı" olarak baştan
    /// kurulur (PK oynatmasına gitmez — fark 9); taze planda çapa T'nin 5'i önce kullanılır.
    /// </summary>
    [Fact]
    public async Task Capanin_bakiye_satiri_acma_yarisi_bastan_kurulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g, balanceRow: false);
        var i = await kit.MemberAsync(g, 100m);
        await kit.EnablePoolAsync();
        var atAnchorRowOpen = false;
        _factory.Hook.Once(async saving =>
        {
            atAnchorRowOpen = saving.ChangeTracker.Entries<CustomerBalance>()
                .Any(e => e.State == EntityState.Added && e.Entity.WpfCustomerId == t);
            (await kit.PanelRefundAsync(t, 5m)).EnsureSuccessStatusCode();
        });

        var sonuc = await StatusAsync(() => kit.ApplyAsync(t, 50m, 50m, Guid.NewGuid(), null, [t, i]));
        _factory.Hook.Disarm();

        atAnchorRowOpen.Should().BeTrue("kanca çapanın satırını açan çekim kaydında tetiklenmeli");
        sonuc.Status.Should().Be(HttpStatusCode.OK, "satır açma yarışı baştan kurulur ({0})", sonuc.Title);
        sonuc.Applied.Should().Be(50m);
        (await kit.Sales.ReadDbAsync(db => db.CustomerBalances.AsNoTracking()
                .CountAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == t)))
            .Should().Be(1, "T'nin tek bakiye satırı var");
        var tBalance = await kit.BalanceAsync(t);
        tBalance.Should().BeGreaterThanOrEqualTo(0m);
        (tBalance + await kit.BalanceAsync(i)).Should().Be(55m, "105 − 50");
        await ShouldHoldLedgerAsync(kit, t, i);
        _factory.Log.Entries
            .Where(e => e.Category == typeof(PersonBalance).FullName
                && e.Message.Contains("bakiye satırı açma yarışı", StringComparison.Ordinal))
            .Should().ContainSingle("tek yeniden deneme, nedeni satır açma yarışı")
            .Which.Level.Should().Be(LogLevel.Information);
    }

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
