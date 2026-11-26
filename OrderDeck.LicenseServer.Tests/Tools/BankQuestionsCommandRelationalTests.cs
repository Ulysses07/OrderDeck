using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using ToggleArgs = OrderDeck.LicenseServer.Tools.BankQuestionsCommand.ToggleArgs;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>bank-questions enable|disable --apply</c> (spec 2026-10-10 §2.7, §3) gerçek SQL Server'da: tek koşullu UPDATE
/// bayrağı açar ya da kapatır, <c>BankQuestionsSince</c> bir kez yazılır (COALESCE: kapatıp açmak sıfırlamaz), ikinci
/// <c>enable</c> etkisizdir. Eşzamanlı bir rakip lisansı UPDATE'ten hemen önce değiştirirse 0 satır → 1, rakibin değeri
/// kalır. Otomatik onay bayrağına iki komut da dokunmaz. Hepsi <c>decisionLayerReady: true</c>: S2a'da kod sabiti
/// kapalı, yazan yol ancak böyle sınanır. CLI'ın bağlamı fabrikanınkinden ayrı bir bağlantıdır (komutun gerçek kurulumu
/// gibi); kanca yalnız ona takılır. InMemory'de <c>ExecuteUpdateAsync</c> ve atomik koşullu yazım yok.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankQuestionsCommandRelationalTests : IAsyncLifetime
{
    private const string ChangedEnable =
        "Lisans bu arada değişti (başka bir enable, kapanan satış senkronu ya da silinen lisans); hiçbir şey yazılmadı, "
        + "yeniden çalıştır.";

    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public BankQuestionsCommandRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>Kipi <paramref name="mode"/> olan bir lisans; üç banka alanı doğrudan yazılır.</summary>
    private async Task<SalesCommandTestKit> LicenseAsync(
        SalesSyncMode mode, bool enabled = false, DateTimeOffset? since = null, bool autoApprove = false)
    {
        var kit = await SalesCommandTestKit.CreateAsync(_factory, mode);
        await kit.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId);
            license.BankQuestionsEnabled = enabled;
            license.BankQuestionsSince = since;
            license.BankAutoApproveEnabled = autoApprove;
        });
        return kit;
    }

    private static async Task<(bool Enabled, DateTimeOffset? Since, bool AutoApprove)> FlagsAsync(SalesCommandTestKit kit)
    {
        var l = await kit.ReadDbAsync(db => db.Licenses.AsNoTracking().SingleAsync(x => x.Id == kit.LicenseId));
        return (l.BankQuestionsEnabled, l.BankQuestionsSince, l.BankAutoApproveEnabled);
    }

    private static ToggleArgs Enable(SalesCommandTestKit kit) => new(Enable: true, kit.LicenseId, Apply: true);

    private static ToggleArgs Disable(SalesCommandTestKit kit) => new(Enable: false, kit.LicenseId, Apply: true);

    /// <summary>CLI'ın kendi bağlamı (<c>RunAsync(string[])</c> gibi doğrudan <c>UseSqlServer</c>), isteğe bağlı kancayla.</summary>
    private async Task<(int Exit, string Output, string Error)> RunAsync(ToggleArgs args, IInterceptor? hook = null)
    {
        var builder = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs);
        if (hook is not null) builder.AddInterceptors(hook);
        await using var db = new LicenseDbContext(builder.Options);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = args.Enable
            ? await BankQuestionsCommand.RunEnableAsync(db, args, decisionLayerReady: true, output, error, default)
            : await BankQuestionsCommand.RunDisableAsync(db, args, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Komutun UPDATE'inden hemen önce lisansın satırını fabrikanın bağlamından (kancasız) değiştiren kanca.</summary>
    private static BeforeNonQueryHook Rival(SalesCommandTestKit kit, Func<IQueryable<License>, Task> write) =>
        new(when: sql => sql.Contains("[BankQuestionsEnabled]", StringComparison.Ordinal),
            before: () => kit.WithDbAsync(db => write(db.Licenses.Where(l => l.Id == kit.LicenseId))));

    /// <summary>Başlıktan sonraki satırlar. Başlık anı taşır: kalıpla denetlenir.</summary>
    private static string[] Body(string output, string command)
    {
        var lines = output.Split(Environment.NewLine);
        lines[^1].Should().BeEmpty("her satır WriteLine ile biter");
        lines[0].Should().MatchRegex(
            $"^UYGULAMA — bank-questions {command} — \\d{{4}}-\\d{{2}}-\\d{{2}} \\d{{2}}:\\d{{2}}:\\d{{2}}Z$");
        return lines[1..^1];
    }

    private static string Line(string text) => text + Environment.NewLine;

    private static string U(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    // ── enable ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_enable_bayragi_ve_Since_yazar_ikincisi_etkisiz()
    {
        var kit = await LicenseAsync(SalesSyncMode.On);

        var (exit, output, error) = await RunAsync(Enable(kit));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        var (enabled, since, autoApprove) = await FlagsAsync(kit);
        (enabled, autoApprove).Should().Be((true, false));
        since.Should().NotBeNull();
        since!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        Body(output, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            "Koşul 1 — satış senkronu modu On: TUTUYOR (On)",
            "Koşul 2 — karar katmanı bu sürümde: TUTUYOR",
            "BankQuestionsSince: boş, açılışta yazılacak; otomatik onay bayrağı: kapalı",
            "Sonuç: bütün koşullar tutuyor",
            $"UYGULANDI: havale soruları açık, BankQuestionsSince={U(since.Value)}");

        // İkinci koşu etkisiz: koşullar basılmaz, Since korunur.
        var again = await RunAsync(Enable(kit));

        again.Exit.Should().Be(0, again.Error);
        again.Error.Should().BeEmpty();
        Body(again.Output, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            $"Havale soruları zaten açık (BankQuestionsSince={U(since.Value)}); hiçbir şey yazılmadı.");
        (await FlagsAsync(kit)).Should().Be((true, since, false));
    }

    /// <summary>§3: kapatıp açmak <c>BankQuestionsSince</c>'i sıfırlamaz. İlk açılış anı geri tarihlenir: ikinci
    /// <c>enable</c> onu şimdiyle ezseydi fark günlerce olurdu. Yazılan değer yeniden okunup basılır.</summary>
    [Fact]
    public async Task Kapat_ac_BankQuestionsSince_korur()
    {
        var kit = await LicenseAsync(SalesSyncMode.On);
        var opened = await RunAsync(Enable(kit));
        opened.Exit.Should().Be(0, opened.Error);
        var first = DateTimeOffset.UtcNow.AddDays(-30);
        await kit.WithDbAsync(db => db.Licenses.Where(l => l.Id == kit.LicenseId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.BankQuestionsSince, first)));

        var off = await RunAsync(Disable(kit));

        off.Exit.Should().Be(0, off.Error);
        off.Error.Should().BeEmpty();
        Body(off.Output, "disable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            "UYGULANDI: havale soruları kapalı; verilmiş kararların geri alınması açık kalır, veri işlemi yapılmadı.");
        (await FlagsAsync(kit)).Should().Be((false, (DateTimeOffset?)first, false));

        var on = await RunAsync(Enable(kit));

        on.Exit.Should().Be(0, on.Error);
        on.Error.Should().BeEmpty();
        Body(on.Output, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            "Koşul 1 — satış senkronu modu On: TUTUYOR (On)",
            "Koşul 2 — karar katmanı bu sürümde: TUTUYOR",
            $"BankQuestionsSince: {U(first)}, korunur; otomatik onay bayrağı: kapalı",
            "Sonuç: bütün koşullar tutuyor",
            $"UYGULANDI: havale soruları açık, BankQuestionsSince={U(first)}");
        (await FlagsAsync(kit)).Should().Be((true, (DateTimeOffset?)first, false));
    }

    /// <summary>Koşulların değerlendirmesinden sonra, UPDATE'ten hemen önce rakip bir enable bayrağı ve Since'i yazar:
    /// UPDATE 0 satır günceller → 1, rakibin Since'i kalır.</summary>
    [Fact]
    public async Task Rakip_enable_once_yazarsa_1_rakibin_Since_i_kalir()
    {
        var kit = await LicenseAsync(SalesSyncMode.On);
        var rival = DateTimeOffset.UtcNow.AddDays(-2);
        var hook = Rival(kit, q => q.ExecuteUpdateAsync(s => s
            .SetProperty(l => l.BankQuestionsEnabled, true)
            .SetProperty(l => l.BankQuestionsSince, rival)));

        var (exit, output, error) = await RunAsync(Enable(kit), hook);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1, output);
        error.Should().Be(Line(ChangedEnable));
        Body(output, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            "Koşul 1 — satış senkronu modu On: TUTUYOR (On)",
            "Koşul 2 — karar katmanı bu sürümde: TUTUYOR",
            "BankQuestionsSince: boş, açılışta yazılacak; otomatik onay bayrağı: kapalı",
            "Sonuç: bütün koşullar tutuyor");
        (await FlagsAsync(kit)).Should().Be((true, (DateTimeOffset?)rival, false), "rakibin yazdığı değer korunur");
    }

    /// <summary>Kip koşulu WHERE'de de: değerlendirmeden sonra <c>sales-sync disable</c> kipi Off yaparsa 0 satır → 1,
    /// bayrak kapalı ve Since boş kalır.</summary>
    [Fact]
    public async Task UPDATEten_hemen_once_mod_Off_olursa_1_yazmaz()
    {
        var kit = await LicenseAsync(SalesSyncMode.On);
        var hook = Rival(kit, q => q.ExecuteUpdateAsync(s => s.SetProperty(l => l.SalesSyncMode, SalesSyncMode.Off)));

        var (exit, output, error) = await RunAsync(Enable(kit), hook);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1, output);
        error.Should().Be(Line(ChangedEnable));
        Body(output, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            "Koşul 1 — satış senkronu modu On: TUTUYOR (On)",
            "Koşul 2 — karar katmanı bu sürümde: TUTUYOR",
            "BankQuestionsSince: boş, açılışta yazılacak; otomatik onay bayrağı: kapalı",
            "Sonuç: bütün koşullar tutuyor");
        (await FlagsAsync(kit)).Should().Be((false, (DateTimeOffset?)null, false));
    }

    // ── disable ───────────────────────────────────────────────────────────────

    /// <summary>Değerlendirmeden sonra rakip bir disable bayrağı kapatır: UPDATE 0 satır günceller → 1; Since korunur.</summary>
    [Fact]
    public async Task Rakip_disable_1()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-5);
        var kit = await LicenseAsync(SalesSyncMode.On, enabled: true, since: since);
        var hook = Rival(kit, q => q.ExecuteUpdateAsync(s => s.SetProperty(l => l.BankQuestionsEnabled, false)));

        var (exit, output, error) = await RunAsync(Disable(kit), hook);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1, output);
        error.Should().Be(Line("Havale soruları bu arada değişti (başka bir disable?); hiçbir şey yazılmadı, yeniden çalıştır."));
        Body(output, "disable").Should().Equal($"Lisans: {kit.LicenseId}");
        (await FlagsAsync(kit)).Should().Be((false, (DateTimeOffset?)since, false));
    }

    /// <summary>Otomatik onay bayrağının komutu S6'da: iki komut da onu yazmaz. İki değerle: ne kuran ne sıfırlayan bir
    /// SET gözden kaçmasın.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Enable_ve_disable_otomatik_onay_bayragina_dokunmaz(bool autoApprove)
    {
        var kit = await LicenseAsync(SalesSyncMode.On, autoApprove: autoApprove);

        var on = await RunAsync(Enable(kit));

        on.Exit.Should().Be(0, on.Error);
        Body(on.Output, "enable").Should().Contain(
            $"BankQuestionsSince: boş, açılışta yazılacak; otomatik onay bayrağı: {(autoApprove ? "açık" : "kapalı")}");
        var opened = await FlagsAsync(kit);
        (opened.Enabled, opened.AutoApprove).Should().Be((true, autoApprove), "enable dokunmaz");

        var off = await RunAsync(Disable(kit));

        off.Exit.Should().Be(0, off.Error);
        (await FlagsAsync(kit)).Should().Be((false, opened.Since, autoApprove), "disable dokunmaz");
    }

    // ── Kanca ─────────────────────────────────────────────────────────────────

    /// <summary>Okuyucusuz komut kancası: <c>when</c>'i tutan ilk komutun hemen öncesinde <c>before</c>'u bir kez
    /// koşturur (<see cref="PersonBalanceToggleCommandRelationalTests"/>'teki kancanın kopyası; o sınıf içi özel).</summary>
    private sealed class BeforeNonQueryHook(Func<string, bool> when, Func<Task> before) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Ran && when(command.CommandText))
            {
                Ran = true;
                await before();
            }
            return result;
        }
    }
}
