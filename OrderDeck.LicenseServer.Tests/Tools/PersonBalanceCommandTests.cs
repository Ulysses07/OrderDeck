using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary><c>person-balance verify</c> (spec 2026-10-11 §7.3): rapor salt okunur, taban yakalama ve inceleme
/// işareti yalnız <c>--apply</c> ile ve yalnız <c>PersonBalanceBaselines</c>'a yazar. Kanca (<see cref="HookedApiFactory"/>)
/// yalnız yazım hatası testlerinde dolu; öbür testler için fabrika <see cref="ApiFactory"/>'yle aynı.</summary>
public sealed class PersonBalanceCommandTests : IClassFixture<HookedApiFactory>
{
    private readonly HookedApiFactory _factory;

    public PersonBalanceCommandTests(HookedApiFactory factory) => _factory = factory;

    /// <summary>Satırdaki <c>{L}</c> her koşuda üretilen bir lisans GUID'iyle değişir (literal GUID yok).</summary>
    private static string[] Args(string line) => line.Replace("{L}", Guid.NewGuid().ToString()).Split(' ');

    [Theory]
    [InlineData("person-balance verify")]
    [InlineData("person-balance verify --license {L} --details")]
    [InlineData("person-balance verify --capture-baseline")]
    [InlineData("person-balance verify --capture-baseline --apply")]
    [InlineData("person-balance verify --mark-reviewed --license {L} --apply")]
    public void Gecerli_argumanlar(string line) =>
        PersonBalanceCommand.TryParseArgs(Args(line), out _).Should().BeTrue();

    [Theory]
    [InlineData("person-balance")]
    [InlineData("person-balance enable --license {L}")]
    [InlineData("person-balance verify --apply")]
    [InlineData("person-balance verify --capture-baseline --mark-reviewed --license {L}")]
    [InlineData("person-balance verify --mark-reviewed --apply")]
    [InlineData("person-balance verify --license yok")]
    [InlineData("person-balance verify --license --details")]
    [InlineData("person-balance verify --license {L} --license {L}")]
    [InlineData("person-balance verify --details --details")]
    [InlineData("person-balance verify --bilinmeyen")]
    public void Gecersiz_argumanlar(string line) =>
        PersonBalanceCommand.TryParseArgs(Args(line), out _).Should().BeFalse();

    [Fact]
    public void Ayristirilan_degerler()
    {
        var license = Guid.NewGuid();

        PersonBalanceCommand.TryParseArgs(
                ["person-balance", "verify", "--apply", "--license", license.ToString("N"), "--mark-reviewed"], out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new PersonBalanceCommand.VerifyArgs(
            license, Details: false, CaptureBaseline: false, MarkReviewed: true, Apply: true));
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(string line)
    {
        PersonBalanceCommand.TryParseArgs(line.Split(' '), out var parsed).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PersonBalanceCommand.RunAsync(db, parsed!, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<int> BaselineCountAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines
            .CountAsync(b => b.LicenseId == licenseId);
    }

    /// <summary>
    /// S1 öncesinden kalan lisans (bir yıl önce verilmiş). Sınıfın testleri aynı veritabanını paylaşır; başka bir testin
    /// yakalaması bu lisanstan önce koşmuşsa, <c>IssuedAt = şimdi</c> olan lisans "ilk yakalamadan sonra verilmiş"
    /// sayılır ve tabanı yalnız işaret olur (plan fark 7, 20). Geri tarihleme testleri sıradan bağımsız kılar.
    /// </summary>
    private async Task<Kit> OldLicenseAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.Sales.WithDbAsync(async db =>
            (await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId)).IssuedAt = DateTimeOffset.UtcNow.AddYears(-1));
        return kit;
    }

    [Fact]
    public async Task Rapor_salt_okunur_sayi_basar_hicbir_sey_yazmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        await kit.MemberAsync(g, 30m);
        await kit.MemberAsync(g, 20m);

        var (exit, output, _) = await RunAsync($"person-balance verify --license {kit.LicenseId}");

        exit.Should().Be(0);
        output.Should().Contain("RAPOR").And.Contain($"Lisans {kit.LicenseId}")
            .And.Contain("1. Davranışı değişecek kişi: 1, havuz toplamı 50.00")
            .And.Contain("10. Satır sayıları:");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(0);
    }

    [Fact]
    public async Task Taban_yakalama_kuru_kosuda_yazmaz_applyda_yazar_ikinci_kosu_bir_sey_eklemez()
    {
        var kit = await OldLicenseAsync();
        var negative = await kit.MemberAsync(null, -10m);

        var dry = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline");
        dry.Exit.Should().Be(0);
        dry.Output.Should().Contain("KURU ÇALIŞTIRMA")
            .And.Contain("eklenecek 1 satır (eksi 1, D1 0, kopya 0)").And.Contain("işaret eklenecek");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(0);

        var apply = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply");
        apply.Exit.Should().Be(0);
        apply.Output.Should().Contain("UYGULANDI: 2 taban satırı");
        using (var scope = _factory.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines
                .AsNoTracking().Where(b => b.LicenseId == kit.LicenseId).ToListAsync();
            rows.Should().ContainSingle(b => b.WpfCustomerId == negative)
                .Which.Should().BeEquivalentTo(new { Balance = -10m, LedgerSum = -10m, ReviewedAt = (DateTimeOffset?)null });
            rows.Should().ContainSingle(b => b.WpfCustomerId == PersonBalanceVerifier.LicenseMarker);
        }

        var again = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply");
        again.Output.Should().Contain("Yazılacak satır yok");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(2);
    }

    [Fact]
    public async Task Yeniden_yakalama_yeni_sapmayi_tabana_almaz()
    {
        var kit = await OldLicenseAsync();
        await kit.MemberAsync(null, -10m);
        await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply");
        await kit.MemberAsync(null, -3m);

        var dry = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline");

        dry.Output.Should().Contain("eklenecek 0 satır (eksi 0, D1 0, kopya 0), işaret zaten var")
            .And.Contain("taban var; yeni sapma tabana alınmaz (taban dışı: eksi 1, D1 0, kopya 0)")
            .And.Contain("YENİ SAPMA");
        var apply = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply");
        apply.Exit.Should().Be(0);
        apply.Output.Should().Contain("Yazılacak satır yok");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(2);
    }

    [Fact]
    public async Task Ilk_yakalamadan_sonra_verilen_lisansa_yalniz_isaret_yazilir()
    {
        var old = await OldLicenseAsync();
        await RunAsync($"person-balance verify --license {old.LicenseId} --capture-baseline --apply");
        var kit = await Kit.CreateAsync(_factory);
        await kit.MemberAsync(null, -7m);

        var dry = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline");

        dry.Output.Should().Contain("taban: yeni lisans (boş taban)")
            .And.Contain("eklenecek 0 satır (eksi 0, D1 0, kopya 0), işaret eklenecek")
            .And.Contain("taban var; yeni sapma tabana alınmaz (taban dışı: eksi 1, D1 0, kopya 0)");
        (await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply")).Output
            .Should().Contain("UYGULANDI: 1 taban satırı");
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines.AsNoTracking()
                .Where(b => b.LicenseId == kit.LicenseId).ToListAsync())
            .Should().ContainSingle().Which.WpfCustomerId.Should().Be(PersonBalanceVerifier.LicenseMarker);
    }

    [Fact]
    public async Task Inceleme_isareti_ReviewedAt_doldurur_tabansiz_lisansta_1()
    {
        var kit = await OldLicenseAsync();
        await kit.MemberAsync(null, -4m);

        var refused = await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed --apply");
        refused.Exit.Should().Be(1, "önce taban yakalanmalı");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(0);

        await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply");
        var dry = await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed");
        dry.Output.Should().Contain("incelenmemiş 2 taban satırı");
        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines.AsNoTracking()
                    .Where(b => b.LicenseId == kit.LicenseId).ToListAsync())
                .Should().OnlyContain(b => b.ReviewedAt == null, "kuru koşu yazmaz");
        }

        var apply = await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed --apply");
        apply.Exit.Should().Be(0);

        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines.AsNoTracking()
                    .Where(b => b.LicenseId == kit.LicenseId).ToListAsync())
                .Should().HaveCount(2).And.OnlyContain(b => b.ReviewedAt != null);
        }
    }

    [Fact]
    public async Task Details_yalniz_GUID_basar_kullanici_adi_basmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var name = $"gizli{Guid.NewGuid():N}";
        var negative = await kit.MemberAsync(null, -6m, username: name);

        var (_, output, _) = await RunAsync($"person-balance verify --license {kit.LicenseId} --details");

        output.Should().Contain(negative.ToString()).And.NotContain(name);
    }

    /// <summary>Taban yazımı sırasında SaveChanges'i verilen SQL hatasıyla düşürür (komut kancası yok, InMemory).</summary>
    private async Task<(int Exit, string Output, string Error)> CaptureFailingWithAsync(Guid licenseId, int number, string message)
    {
        _factory.Hook.BeforeSave = () => throw new DbUpdateException("test", SqlExceptionFactory.Create(number, message));
        try
        {
            return await RunAsync($"person-balance verify --license {licenseId} --capture-baseline --apply");
        }
        finally
        {
            _factory.Hook.Reset();
        }
    }

    [Fact]
    public async Task Eszamanli_taban_yazimi_birincil_anahtar_ihlalinde_1_doner()
    {
        var kit = await OldLicenseAsync();
        await kit.MemberAsync(null, -10m);

        var (exit, _, error) = await CaptureFailingWithAsync(kit.LicenseId, 2627,
            "Violation of PRIMARY KEY constraint 'PK_PersonBalanceBaselines'. Cannot insert duplicate key.");

        exit.Should().Be(1);
        error.Should().Contain("başka bir koşuyla yazıldı");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(0);
    }

    [Theory]
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_PersonBalanceBaselines_Licenses_LicenseId\".")]
    [InlineData(8115, "Arithmetic overflow error converting numeric to data type numeric.")]
    [InlineData(2627, "Violation of PRIMARY KEY constraint 'PK_Baska'. Cannot insert duplicate key.")]
    public async Task Taban_yaziminda_baska_veritabani_hatasi_eszamanli_yazim_diye_yutulmaz(int number, string message)
    {
        var kit = await OldLicenseAsync();
        await kit.MemberAsync(null, -10m);

        var act = () => CaptureFailingWithAsync(kit.LicenseId, number, message);

        (await act.Should().ThrowAsync<DbUpdateException>(
                "yalnız taban anahtarının yarışı 1'dir; öbür hatanın teşhisi operatöre ulaşmalı"))
            .Which.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().Be(number);
    }

    [Fact]
    public async Task Inceleme_yazimindan_once_taban_satirlari_silindiyse_1_doner()
    {
        var kit = await OldLicenseAsync();
        await kit.MemberAsync(null, -4m);
        (await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply")).Exit.Should().Be(0);
        // Okuma ile yazma arasında lisans silinir (taban satırları cascade ile gider).
        _factory.Hook.BeforeSave = async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.PersonBalanceBaselines.RemoveRange(
                await db.PersonBalanceBaselines.Where(b => b.LicenseId == kit.LicenseId).ToListAsync());
            await db.SaveChangesAsync();
        };

        (int Exit, string Output, string Error) result;
        try
        {
            result = await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed --apply");
        }
        finally
        {
            _factory.Hook.Reset();
        }

        result.Exit.Should().Be(1, "çıkış kodu sözleşmesi: eşzamanlı değişiklik, hiçbir şey yazılmadı");
        result.Error.Should().Contain("bu arada");
    }

    /// <summary>Gövde public: elle kurulmuş, ayrıştırıcının reddedeceği kayıt veritabanına gitmeden reddedilir.</summary>
    [Theory]
    [InlineData(false, false, true, false)]   // lisanssız inceleme
    [InlineData(true, false, false, true)]    // rapora --apply
    [InlineData(true, true, true, false)]     // yakalama ve inceleme birlikte
    public async Task Govde_ayristiricinin_reddettigi_argumanlari_reddeder(bool license, bool capture, bool mark, bool apply)
    {
        var args = new PersonBalanceCommand.VerifyArgs(
            license ? Guid.NewGuid() : null, Details: false, CaptureBaseline: capture, MarkReviewed: mark, Apply: apply);
        using var scope = _factory.Services.CreateScope();
        var output = new StringWriter();

        var act = () => PersonBalanceCommand.RunAsync(
            scope.ServiceProvider.GetRequiredService<LicenseDbContext>(), args, output, new StringWriter(), default);

        await act.Should().ThrowAsync<ArgumentException>();
        output.ToString().Should().BeEmpty("rapor basılmadan, veritabanı okunmadan");
    }

    /// <summary>Kopya satır: bakiyeli (hareketli) bir projeksiyon, sonra başka bir asıl kayda birleştirilmiş.</summary>
    private static async Task<Guid> CopyAsync(Kit kit, decimal balance)
    {
        var head = await kit.MemberAsync(null);
        var copy = await kit.MemberAsync(null, balance);
        await kit.Sales.WithDbAsync(async db =>
            (await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == copy)).MergedIntoId = head);
        return copy;
    }

    [Fact]
    public async Task Details_tabana_alinmis_kopya_satirini_basar()
    {
        var kit = await OldLicenseAsync();
        var copy = await CopyAsync(kit, 2m);
        (await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply")).Exit.Should().Be(0);

        var (_, output, _) = await RunAsync($"person-balance verify --license {kit.LicenseId} --details");

        output.Should().Contain("8. Kopya satır: hareketli 1 (1 hareket), bakiyeli 1; tabanda 1, taban dışı 0")
            .And.Contain($"kopya (tabanda): {copy}", "yakalamadan sonra tabandaki kopya da CLI'dan görünmeli");
    }

    [Fact]
    public async Task Inceleme_details_incelenecek_taban_satirlarini_listeler_isaret_haric()
    {
        var kit = await OldLicenseAsync();
        var negative = await kit.MemberAsync(null, -4m);
        var copy = await CopyAsync(kit, 3m);
        (await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply")).Exit.Should().Be(0);

        var dry = await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed --details");

        dry.Exit.Should().Be(0);
        var listed = dry.Output.Split('\n').Should()
            .ContainSingle(l => l.Contains("incelenecek taban satırı (işaret hariç)")).Subject;
        listed.Should().Contain(negative.ToString()).And.Contain(copy.ToString())
            .And.NotContain(PersonBalanceVerifier.LicenseMarker.ToString());

        (await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed --apply")).Exit.Should().Be(0);
        (await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed --details")).Output
            .Should().Contain("incelenmemiş 0 taban satırı").And.NotContain("incelenecek taban satırı");
    }

    [Fact]
    public async Task Bilinmeyen_lisans_2_doner()
    {
        var (exit, _, error) = await RunAsync($"person-balance verify --license {Guid.NewGuid()}");

        exit.Should().Be(2);
        error.Should().Contain("Lisans bulunamadı");
    }
}
