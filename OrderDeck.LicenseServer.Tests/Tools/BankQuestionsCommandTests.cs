using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using ToggleArgs = OrderDeck.LicenseServer.Tools.BankQuestionsCommand.ToggleArgs;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>bank-questions enable|disable</c> (spec 2026-10-10 §2.7, §14; S2a planı Karar 8) — InMemory'de sınanabilen yollar:
/// ayrıştırma, bilinmeyen lisans, zaten açık/kapalı, kuru koşu, koşulu tutmayan <c>--apply</c>. Hepsi
/// <c>ExecuteUpdateAsync</c>'e varmadan döner (InMemory onu desteklemez); yazan yollar
/// <see cref="BankQuestionsCommandRelationalTests"/>'te. Çıktı satırları birebir sabitlenir; başlığın anı kalıpla.
/// </summary>
public sealed class BankQuestionsCommandTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public BankQuestionsCommandTests(ApiFactory factory) => _factory = factory;

    /// <summary>Satırdaki <c>{L}</c> her koşuda üretilen bir lisans GUID'iyle değişir (literal GUID yok).</summary>
    private static string[] Args(string line) => line.Replace("{L}", Guid.NewGuid().ToString()).Split(' ');

    // ── Ayrıştırma ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("bank-questions enable --license {L}", true, false)]
    [InlineData("bank-questions enable --license {L} --apply", true, true)]
    [InlineData("bank-questions disable --apply --license {L}", false, true)]
    public void Gecerli_argumanlar(string line, bool enable, bool apply)
    {
        var args = Args(line);
        var license = Guid.Parse(args[Array.IndexOf(args, "--license") + 1]);

        BankQuestionsCommand.TryParseArgs(args, out var parsed).Should().BeTrue();

        parsed.Should().Be(new ToggleArgs(enable, license, apply));
    }

    [Theory]
    [InlineData("bank-questions")]
    [InlineData("bank-questions enable")]
    [InlineData("bank-questions disable --apply")]
    [InlineData("bank-questions enable --license yok")]
    [InlineData("bank-questions disable --license")]
    [InlineData("bank-questions enable --license {L} --license {L}")]
    [InlineData("bank-questions enable --license {L} --apply --apply")]
    [InlineData("bank-questions enable --license {L} --details")]
    [InlineData("bank-questions status --license {L}")]
    [InlineData("bank-questions toggle --license {L}")]
    public void Gecersiz_argumanlar(string line) =>
        BankQuestionsCommand.TryParseArgs(Args(line), out _).Should().BeFalse();

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

    private async Task<(int Exit, string Output, string Error)> RunAsync(ToggleArgs args, bool decisionLayerReady = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = args.Enable
            ? await BankQuestionsCommand.RunEnableAsync(db, args, decisionLayerReady, output, error, default)
            : await BankQuestionsCommand.RunDisableAsync(db, args, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Başlıktan sonraki satırlar. Başlık anı taşır: kalıpla denetlenir.</summary>
    private static string[] Body(string output, bool apply, string command)
    {
        var lines = output.Split(Environment.NewLine);
        lines[^1].Should().BeEmpty("her satır WriteLine ile biter");
        lines[0].Should().MatchRegex(
            $"^{(apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — bank-questions {command} — \\d{{4}}-\\d{{2}}-\\d{{2}} \\d{{2}}:\\d{{2}}:\\d{{2}}Z$");
        return lines[1..^1];
    }

    private static string Line(string text) => text + Environment.NewLine;

    private static string U(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    // ── Gövde ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bilinmeyen_lisans_2_stdout_bos()
    {
        var unknown = Guid.NewGuid();

        foreach (var enable in new[] { true, false })
        {
            var (exit, output, error) = await RunAsync(new ToggleArgs(enable, unknown, Apply: true));

            exit.Should().Be(2);
            error.Should().Be(Line($"Lisans bulunamadı: {unknown}. Hiçbir şey yazılmadı."));
            output.Should().BeEmpty();
        }
    }

    /// <summary>Kuru koşu iki koşulu basar, koşul tutmasa da 0 döner, hiçbir şey yazmaz.</summary>
    [Fact]
    public async Task Kuru_enable_mod_Off_TUTMUYOR_basar_0_doner_yazmaz()
    {
        var kit = await LicenseAsync(SalesSyncMode.Off);

        var (exit, output, error) = await RunAsync(
            new ToggleArgs(Enable: true, kit.LicenseId, Apply: false), decisionLayerReady: false);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        Body(output, apply: false, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            "Koşul 1 — satış senkronu modu On: TUTMUYOR (Off)",
            "Koşul 2 — karar katmanı bu sürümde: TUTMUYOR",
            "BankQuestionsSince: boş, açılışta yazılacak; otomatik onay bayrağı: kapalı",
            "Sonuç: koşul 1, 2 tutmuyor",
            "Kuru çalıştırma: hiçbir şey yazılmadı. Açmak için --apply ekle.");
        (await FlagsAsync(kit)).Should().Be((false, (DateTimeOffset?)null, false));
    }

    /// <summary>Kip Off iken <c>--apply</c> reddedilir; karar katmanı da yoksa (S2a'nın gerçek hâli) iki koşul birlikte
    /// sayılır.</summary>
    [Theory]
    [InlineData(true, "TUTUYOR", "1")]
    [InlineData(false, "TUTMUYOR", "1, 2")]
    public async Task Mod_Off_iken_apply_1_yazmaz(bool decisionLayerReady, string layer, string failing)
    {
        var kit = await LicenseAsync(SalesSyncMode.Off);

        var (exit, output, error) = await RunAsync(
            new ToggleArgs(Enable: true, kit.LicenseId, Apply: true), decisionLayerReady);

        exit.Should().Be(1, output);
        error.Should().Be(Line($"Havale soruları açılmadı: koşul {failing} tutmuyor; hiçbir şey yazılmadı."));
        Body(output, apply: true, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            "Koşul 1 — satış senkronu modu On: TUTMUYOR (Off)",
            $"Koşul 2 — karar katmanı bu sürümde: {layer}",
            "BankQuestionsSince: boş, açılışta yazılacak; otomatik onay bayrağı: kapalı",
            $"Sonuç: koşul {failing} tutmuyor");
        (await FlagsAsync(kit)).Should().Be((false, (DateTimeOffset?)null, false));
    }

    /// <summary>Karar 8: S2a'da karar katmanı yok. Erken bir <c>--apply</c> geri alınamayan <c>BankQuestionsSince</c>'i
    /// yazar, S3a çıkınca o andan sonraki havaleler sorulurdu; kip On olsa da reddedilir.</summary>
    [Fact]
    public async Task Karar_katmani_hazir_degilken_apply_reddeder()
    {
        var kit = await LicenseAsync(SalesSyncMode.On);

        var (exit, output, error) = await RunAsync(
            new ToggleArgs(Enable: true, kit.LicenseId, Apply: true), decisionLayerReady: false);

        exit.Should().Be(1, output);
        error.Should().Be(Line("Havale soruları açılmadı: koşul 2 tutmuyor; hiçbir şey yazılmadı."));
        Body(output, apply: true, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            "Koşul 1 — satış senkronu modu On: TUTUYOR (On)",
            "Koşul 2 — karar katmanı bu sürümde: TUTMUYOR",
            "BankQuestionsSince: boş, açılışta yazılacak; otomatik onay bayrağı: kapalı",
            "Sonuç: koşul 2 tutmuyor");
        (await FlagsAsync(kit)).Should().Be((false, (DateTimeOffset?)null, false), "bayrak kapalı, Since boş kalır");
    }

    [Fact]
    public void S2a_surumunde_karar_katmani_kapali() => BankQuestionsCommand.DecisionLayerReady.Should().BeFalse("S4 açar");

    /// <summary>Zaten açıkken enable (<c>--apply</c> dahil) koşulları değerlendirmez ve basmaz, yazmaz, 0: kip Off ve
    /// karar katmanı yokken de. Since korunur.</summary>
    [Fact]
    public async Task Zaten_acik_0_kosul_basmaz()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-3);
        var kit = await LicenseAsync(SalesSyncMode.Off, enabled: true, since: since);

        var (exit, output, error) = await RunAsync(
            new ToggleArgs(Enable: true, kit.LicenseId, Apply: true), decisionLayerReady: false);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        Body(output, apply: true, "enable").Should().Equal(
            $"Lisans: {kit.LicenseId}",
            $"Havale soruları zaten açık (BankQuestionsSince={U(since)}); hiçbir şey yazılmadı.");
        (await FlagsAsync(kit)).Should().Be((true, (DateTimeOffset?)since, false));
    }

    /// <summary>Kill switch'in kuru koşusu yazmaz ve Since'in korunacağını söyler; zaten kapalıyken <c>--apply</c> 0.</summary>
    [Fact]
    public async Task Disable_kuru_kosuda_yazmaz_kapaliyken_0()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-2);
        var open = await LicenseAsync(SalesSyncMode.On, enabled: true, since: since, autoApprove: true);

        var dry = await RunAsync(new ToggleArgs(Enable: false, open.LicenseId, Apply: false));

        dry.Exit.Should().Be(0, dry.Error);
        dry.Error.Should().BeEmpty();
        Body(dry.Output, apply: false, "disable").Should().Equal(
            $"Lisans: {open.LicenseId}",
            $"Kuru çalıştırma: havale soruları kapatılacak (BankQuestionsSince={U(since)} korunur); veri işlemi yok. "
            + "Kapatmak için --apply ekle.");
        (await FlagsAsync(open)).Should().Be((true, (DateTimeOffset?)since, true));

        var closed = await LicenseAsync(SalesSyncMode.On);

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: false, closed.LicenseId, Apply: true));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        Body(output, apply: true, "disable").Should().Equal(
            $"Lisans: {closed.LicenseId}",
            "Havale soruları zaten kapalı; hiçbir şey yazılmadı.");
        (await FlagsAsync(closed)).Should().Be((false, (DateTimeOffset?)null, false));
    }
}
