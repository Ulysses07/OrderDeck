using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>person-balance enable|disable</c> (spec 2026-10-11 §7.2, §7.4) — InMemory'de sınanabilen yollar: ayrıştırma,
/// alt komuta göre dağıtım, geçersiz pencere, bilinmeyen lisans, zaten açık/kapalı, kuru koşu, koşulu tutmayan
/// <c>--apply</c>. Hepsi
/// <c>ExecuteUpdateAsync</c>'e varmadan döner (InMemory onu desteklemez); yazan yollar
/// <see cref="PersonBalanceToggleCommandRelationalTests"/>'te. Çıktıda ad, kullanıcı adı, makine adı ve parmak izi yok.
/// </summary>
public sealed class PersonBalanceToggleCommandTests : IClassFixture<ApiFactory>
{
    private const string Min = "0.9.11";

    private readonly ApiFactory _factory;

    public PersonBalanceToggleCommandTests(ApiFactory factory) => _factory = factory;

    /// <summary>Satırdaki <c>{L}</c> her koşuda üretilen bir lisans GUID'iyle değişir (literal GUID yok).</summary>
    private static string[] Args(string line) => line.Replace("{L}", Guid.NewGuid().ToString()).Split(' ');

    private static PersonBalanceOptions Pb(string? min = Min) => new() { MinClientVersion = min };

    private static SalesSyncOptions Ss(string? min = Min) => new() { MinClientVersion = min };

    // ── Ayrıştırma ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("person-balance enable --license {L}")]
    [InlineData("person-balance enable --license {L} --apply")]
    [InlineData("person-balance disable --apply --license {L}")]
    public void Gecerli_argumanlar(string line) =>
        PersonBalanceCommand.TryParseToggleArgs(Args(line), out _).Should().BeTrue();

    [Theory]
    [InlineData("person-balance enable")]
    [InlineData("person-balance enable --apply")]
    [InlineData("person-balance enable --license yok")]
    [InlineData("person-balance enable --license {L} --license {L}")]
    [InlineData("person-balance enable --license {L} --apply --apply")]
    [InlineData("person-balance enable --license {L} --details")]
    [InlineData("person-balance disable --license")]
    [InlineData("person-balance toggle --license {L}")]
    public void Gecersiz_argumanlar(string line) =>
        PersonBalanceCommand.TryParseToggleArgs(Args(line), out _).Should().BeFalse();

    [Fact]
    public void Ayristirilan_degerler()
    {
        var license = Guid.NewGuid();

        PersonBalanceCommand.TryParseToggleArgs(
                ["person-balance", "disable", "--apply", "--license", license.ToString("N")], out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new PersonBalanceCommand.ToggleArgs(Enable: false, License: license, Apply: true));
    }

    /// <summary>S1 sözleşmesi (S2 planı fark 13): verify'ın ayrıştırıcısı enable'ı tanımaz; dağıtım <c>args[1]</c>'e göre.</summary>
    [Fact]
    public void Verify_ayristiricisi_enable_i_hala_reddeder() =>
        PersonBalanceCommand.TryParseArgs(["person-balance", "enable", "--license", Guid.NewGuid().ToString()], out _)
            .Should().BeFalse();

    /// <summary>
    /// Dağıtım (S2 planı fark 13): geçerli bir enable/disable satırı <see cref="PersonBalanceCommand.TryParseToggleArgs"/>'a
    /// gider ve <c>ToggleArgs</c> verir. Yanlış dağıtım (ör. her satırın verify ayrıştırıcısına gitmesi) prod'da geçerli
    /// <c>enable --license X</c>'i Usage + 2'ye düşürürdü.
    /// </summary>
    [Theory]
    [InlineData("person-balance enable --license {L}", true, false)]
    [InlineData("person-balance enable --license {L} --apply", true, true)]
    [InlineData("person-balance disable --apply --license {L}", false, true)]
    public void Dagitim_toggle_satirini_toggle_ayristiricisina_verir(string line, bool enable, bool apply)
    {
        PersonBalanceCommand.TryParseAnyArgs(Args(line), out var verify, out var toggle).Should().BeTrue();

        verify.Should().BeNull();
        toggle.Should().NotBeNull();
        (toggle!.Enable, toggle.Apply).Should().Be((enable, apply));
    }

    /// <summary>Dağıtım: verify satırı S1'in ayrıştırıcısına gider, <c>VerifyArgs</c> verir (S1 sözleşmesi aynen).</summary>
    [Theory]
    [InlineData("person-balance verify")]
    [InlineData("person-balance verify --license {L} --details")]
    [InlineData("person-balance verify --capture-baseline --apply")]
    public void Dagitim_verify_satirini_verify_ayristiricisina_verir(string line)
    {
        PersonBalanceCommand.TryParseAnyArgs(Args(line), out var verify, out var toggle).Should().BeTrue();

        verify.Should().NotBeNull();
        toggle.Should().BeNull();
    }

    /// <summary>Dağıtım: iki ayrıştırıcının da reddettiği satır false (RunAsync Usage + 2), iki çıktı da boş.</summary>
    [Theory]
    [InlineData("person-balance")]
    [InlineData("person-balance enable --apply")]
    [InlineData("person-balance disable --license {L} --details")]
    [InlineData("person-balance verify --apply")]
    [InlineData("person-balance toggle --license {L}")]
    public void Dagitim_gecersiz_satiri_reddeder(string line)
    {
        PersonBalanceCommand.TryParseAnyArgs(Args(line), out var verify, out var toggle).Should().BeFalse();

        verify.Should().BeNull();
        toggle.Should().BeNull();
    }

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>
    /// S1 öncesinden kalan (geri tarihli) lisans ve incelenmiş taban işareti: 3. koşul tutar. Sınıfın testleri aynı
    /// veritabanını paylaşır; geri tarihleme "ilk yakalamadan sonra verilmiş" kuralını sıradan bağımsız kılar
    /// (emsal <c>PersonBalanceCommandTests.OldLicenseAsync</c>).
    /// </summary>
    private async Task<Kit> ReviewedLicenseAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await kit.Sales.WithDbAsync(async db =>
        {
            (await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId)).IssuedAt = DateTimeOffset.UtcNow.AddYears(-1);
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = PersonBalanceVerifier.LicenseMarker,
                CapturedAt = at, ReviewedAt = at,
            });
        });
        return kit;
    }

    /// <summary>Son görülmesi şimdi olan etkin bir etkinleştirme. Döner: Id, parmak izi ve makine adı (çıktıda
    /// geçmediklerini sınamak için).</summary>
    private static async Task<(Guid Id, string Fingerprint, string Machine)> ActivationAsync(Kit kit, string? version)
    {
        var activation = new Activation
        {
            Id = Guid.NewGuid(), LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
            MachineName = $"m-{Guid.NewGuid():N}", ActivatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            LastSeenAt = DateTimeOffset.UtcNow, AppVersion = version,
        };
        await kit.Sales.WithDbAsync(db =>
        {
            db.Activations.Add(activation);
            return Task.CompletedTask;
        });
        return (activation.Id, activation.HardwareFingerprint, activation.MachineName!);
    }

    private static Task<DateTimeOffset?> PoolSinceAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.Licenses.AsNoTracking()
            .Where(l => l.Id == kit.LicenseId)
            .Select(l => l.PersonBalancePoolSince)
            .SingleAsync());

    private async Task<(int Exit, string Output, string Error)> EnableAsync(
        Guid licenseId, bool apply, PersonBalanceOptions? personBalance = null, SalesSyncOptions? salesSync = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PersonBalanceCommand.RunEnableAsync(
            db, new PersonBalanceCommand.ToggleArgs(Enable: true, licenseId, apply), personBalance ?? Pb(),
            salesSync ?? Ss(), output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(int Exit, string Output, string Error)> DisableAsync(Guid licenseId, bool apply)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PersonBalanceCommand.RunDisableAsync(
            db, new PersonBalanceCommand.ToggleArgs(Enable: false, licenseId, apply), output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private static string U(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    // ── Gövde ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bilinmeyen_lisans_2(bool enable)
    {
        var unknown = Guid.NewGuid();

        var (exit, output, error) = enable ? await EnableAsync(unknown, apply: true) : await DisableAsync(unknown, apply: true);

        exit.Should().Be(2);
        error.Should().Contain($"Lisans bulunamadı: {unknown}");
        output.Should().BeEmpty();
    }

    /// <summary>
    /// Pencere 0 ya da eksiyse kapı (Görev 11) 1. koşulu kapalı-hata ile tutmaz sayar; CLI ayrıca lisans okunmadan
    /// anlamlı bir yapılandırma hatasıyla 2 döner (ek güvence, operatöre "koşul 1 tutmuyor" yerine asıl sebebi
    /// gösterir). Denetim gövdede (CLI giriş noktasında değil), lisans okunmadan ve kapı değerlendirilmeden yapılır → 2,
    /// hiçbir şey basılmaz ve yazılmaz. Lisans dört koşulu tutacak biçimde kurulsa bile.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Pencere_gecersizken_enable_2_yazmaz(int days)
    {
        var kit = await ReviewedLicenseAsync();

        var (exit, output, error) = await EnableAsync(kit.LicenseId, apply: true,
            personBalance: new PersonBalanceOptions { MinClientVersion = Min, ActivePcWindowDays = days });

        exit.Should().Be(2);
        error.Should().Contain("PersonBalance:ActivePcWindowDays 0'dan büyük olmalı.");
        output.Should().BeEmpty();
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    /// <summary>S2 planı fark 18e: zaten açıkken enable (--apply dahil) kapıyı değerlendirmez, yazmaz, 0; tarih korunur.</summary>
    [Fact]
    public async Task Zaten_acikken_enable_0_yazmaz()
    {
        var kit = await ReviewedLicenseAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-3);
        await kit.EnablePoolAsync(since);

        var (exit, output, error) = await EnableAsync(kit.LicenseId, apply: true);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA — person-balance enable").And.Contain($"Lisans: {kit.LicenseId}")
            .And.Contain($"Havuz kipi zaten açık (PersonBalancePoolSince={U(since)}); hiçbir şey yazılmadı.");
        output.Should().NotContain("Koşul 1").And.NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().Be(since);
    }

    [Fact]
    public async Task Zaten_kapaliyken_disable_0_yazmaz()
    {
        var kit = await ReviewedLicenseAsync();

        var (exit, output, error) = await DisableAsync(kit.LicenseId, apply: true);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA — person-balance disable")
            .And.Contain("Havuz kipi zaten kapalı; hiçbir şey yazılmadı.");
        output.Should().NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    /// <summary>
    /// Kuru koşu dört koşulu sayılarla basar ve hiçbir şey yazmaz (R59). Alt sınırın altındaki PC'nin yalnız Id'si ve
    /// sürümü görünür; kullanıcı adı, parmak izi ve makine adı görünmez. Koşul tutmasa da kuru koşu 0 döner.
    /// </summary>
    [Fact]
    public async Task Kuru_enable_dort_kosulu_basar_yazmaz_0()
    {
        var kit = await ReviewedLicenseAsync();
        var g = Kit.NewGroup();
        var nameA = $"p{Guid.NewGuid():N}";
        var nameB = $"p{Guid.NewGuid():N}";
        await kit.MemberAsync(g, 30m, username: nameA);
        await kit.MemberAsync(g, 20m, username: nameB);
        var current = await ActivationAsync(kit, Min);
        var old = await ActivationAsync(kit, "0.9.10");

        var (exit, output, error) = await EnableAsync(kit.LicenseId, apply: false);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("KURU ÇALIŞTIRMA — person-balance enable").And.Contain($"Lisans: {kit.LicenseId}");
        output.Should().Contain("Koşul 1 — istemci sürümü (son 14 gün): TUTMUYOR")
            .And.Contain($"PersonBalance:MinClientVersion: {Min}")
            .And.Contain("görülen etkin etkinleştirme: 2; alt sınırın altında ya da sürümsüz: 1")
            .And.Contain($"{old.Id} (sürüm=0.9.10)");
        output.Should().Contain("Koşul 2 — verify: D4=0 D5=0 D6=0 yetim=0 kırpma=0 kontrol karakteri=0: TUTUYOR");
        output.Should().Contain("Koşul 3 — taban: var; incelenmemiş 0; taban dışı eksi 0; daha da eksiye inen 0; "
            + "taban dışı D1 0; farkı değişen D1 0; taban dışı kopya 0: TUTUYOR");
        output.Should().Contain($"Koşul 4 — Wh ⊇ Ws: PersonBalance:MinClientVersion={Min} SalesSync:MinClientVersion={Min}: TUTUYOR");
        output.Should().Contain("Sonuç: koşul 1 tutmuyor")
            .And.Contain("Kuru çalıştırma: hiçbir şey yazılmadı. Açmak için --apply ekle.");
        output.Should().NotContain(current.Id.ToString(), "alt sınırı geçen PC listelenmez");
        foreach (var secret in new[] { nameA, nameB, current.Fingerprint, current.Machine, old.Fingerprint, old.Machine })
            output.Should().NotContain(secret);
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    /// <summary>R06: alt sınır boş ya da bozukken 1. ve 4. koşul tutmaz; kuru koşu 0 ile basar, <c>--apply</c> 1 ile
    /// reddeder ve bayrak boş kalır. Bozuk değer 1. koşulda ham hâliyle ve "(ayrıştırılamıyor)" ekiyle görünür; 4.
    /// koşulun satırıyla çelişmez.</summary>
    [Theory]
    [InlineData(null, "tanımsız", "tanımsız")]
    [InlineData("abc", "abc (ayrıştırılamıyor)", "abc")]
    public async Task Kosul_tutmazken_enable_apply_1_yazmaz(string? min, string shownLimit, string shownSuperset)
    {
        var kit = await ReviewedLicenseAsync();
        var broken = Pb(min);

        var dry = await EnableAsync(kit.LicenseId, apply: false, personBalance: broken);
        dry.Exit.Should().Be(0, dry.Error);
        dry.Output.Should().Contain("Koşul 1 — istemci sürümü (son 14 gün): TUTMUYOR")
            .And.Contain($"PersonBalance:MinClientVersion: {shownLimit}")
            .And.Contain("alt sınırın altında ya da sürümsüz: karşılaştırılamadı")
            .And.Contain($"Koşul 4 — Wh ⊇ Ws: PersonBalance:MinClientVersion={shownSuperset} SalesSync:MinClientVersion={Min}: TUTMUYOR")
            .And.Contain("Sonuç: koşul 1, 4 tutmuyor");

        var (exit, output, error) = await EnableAsync(kit.LicenseId, apply: true, personBalance: broken);

        exit.Should().Be(1);
        error.Should().Contain("Havuz kipi açılmadı: koşul 1, 4 tutmuyor; hiçbir şey yazılmadı.");
        output.Should().Contain("UYGULAMA — person-balance enable").And.Contain("Sonuç: koşul 1, 4 tutmuyor");
        output.Should().NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    [Fact]
    public async Task Kuru_disable_veri_islemi_yok_der_0()
    {
        var kit = await ReviewedLicenseAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-2);
        await kit.EnablePoolAsync(since);

        var (exit, output, error) = await DisableAsync(kit.LicenseId, apply: false);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("KURU ÇALIŞTIRMA — person-balance disable").And.Contain($"Lisans: {kit.LicenseId}")
            .And.Contain($"Kuru çalıştırma: havuz kipi kapatılacak (PersonBalancePoolSince={U(since)} → boş); "
                + "veri işlemi yok. Kapatmak için --apply ekle.");
        (await PoolSinceAsync(kit)).Should().Be(since);
    }
}
