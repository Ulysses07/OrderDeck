using System.Text.RegularExpressions;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Ortak bakiye S2'nin deploy yüzeyi — spec 2026-10-11 §7.2, §7.4 (saf, fikstürsüz). Prod'da license-server'ın
/// <c>env_file</c>'ı yok: <c>.env</c>'deki <c>PersonBalance__MinClientVersion</c> container'a yalnız compose'un
/// <c>environment:</c> eşlemesindeki satırla geçer. Satır yoksa Wh yayımlanıp <c>.env</c>'e yazıldıktan sonra da alt
/// sınır boş kalır: <c>person-balance enable --apply</c> hep reddeder, havuz kipindeki lisansta WPF'in preview'ı ve
/// yeni apply'ı (kayıtlı anahtarın oynatması etkilenmez) 409 alır.
/// README'nin <c>.env</c> şablonu ve S2 işletim bloğu (enable/disable komutları) da burada sabitlenir.
/// </summary>
public sealed class PersonBalanceDeployTests
{
    private static readonly string MinClientVersionKey =
        $"{PersonBalanceOptions.Section}__{nameof(PersonBalanceOptions.MinClientVersion)}";

    private static readonly string ActivePcWindowKey =
        $"{PersonBalanceOptions.Section}__{nameof(PersonBalanceOptions.ActivePcWindowDays)}";

    /// <summary>Boş varsayılan (<c>:-</c>): Wh yayımlanana kadar kapı kapalı. <c>:?</c> eksik <c>.env</c> satırında CI'ın
    /// <c>docker compose config -q</c> kapısını düşürüp her deploy'u kilitlerdi.</summary>
    [Fact]
    public void Compose_alt_sinir_satiri_bos_varsayilanla_var()
    {
        var compose = ReadRepoFile("deploy/docker-compose.yml");

        compose.Should().Contain(
            "      PersonBalance__MinClientVersion: \"${PersonBalance__MinClientVersion:-}\"",
            "deploy/docker-compose.yml license-server environment: bloğunda alt sınırın satırı boş varsayılanla olmalı");
        compose.Should().NotContain(
            "${PersonBalance__MinClientVersion:?",
            "eksik .env satırı config -q kapısını düşürüp deploy'u kilitlememeli");
    }

    /// <summary>Etkin PC penceresi compose'da YOK (plan fark 6): prod'da kod varsayılanı 14 gün. Boş varsayılanla
    /// eklenirse int binder'ı boş dizeyi ayrıştıramaz ve sunucu açılmaz.</summary>
    [Fact]
    public void Compose_pencere_ayari_icermez()
    {
        var compose = ReadRepoFile("deploy/docker-compose.yml");

        compose.Should().NotContain(
            ActivePcWindowKey,
            "pencere ayarı yalnız kod varsayılanından gelir; compose satırı boş dizeyle sunucuyu düşürebilirdi");
    }

    /// <summary>Compose'daki anahtar, seçenek sınıfının bağlandığı bölüm + özellik adıyla aynı olmalı: biri yeniden
    /// adlandırılırsa değer sessizce bağlanmaz.</summary>
    [Fact]
    public void Compose_anahtari_secenek_bolumuyle_eslesir()
    {
        var compose = ReadRepoFile("deploy/docker-compose.yml");

        compose.Should().Contain(
            $"{MinClientVersionKey}: \"${{{MinClientVersionKey}:-}}\"",
            "compose anahtarı PersonBalanceOptions.Section + MinClientVersion'dan türemeli");
    }

    /// <summary>.env şablonunda satır başında anahtar: Burak Wh numarasını buraya yazar.</summary>
    [Fact]
    public void README_env_sablonunda_alt_sinir_satiri_var()
    {
        var readme = ReadRepoFile("deploy/README.md");

        Regex.IsMatch(readme, $"^{Regex.Escape(MinClientVersionKey)}=", RegexOptions.Multiline)
            .Should().BeTrue("deploy/README.md .env şablonunda PersonBalance__MinClientVersion= satırı olmalı");
    }

    /// <summary>S2 işletim bloğu: havuz kipini yalnız CLI açar/kapatır; kill switch komutu README'de bulunmalı.</summary>
    [Fact]
    public void README_S2_geri_alma_ve_disable_komutu_var()
    {
        var readme = ReadRepoFile("deploy/README.md");

        readme.Should().Contain("Ortak bakiye S2", "rollback bölümünde S2 bloğu olmalı")
            .And.Contain("person-balance enable --license", "pilot açılışının komutu yazılı olmalı")
            .And.Contain("person-balance disable --license", "kill switch komutu yazılı olmalı");
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(FindRepoFile(relativePath));

    /// <summary>Testler bin klasöründen koşuyor; repo kökünü yukarı yürüyerek buluyoruz (LicensesSalesControllerTests gibi).</summary>
    private static string FindRepoFile(string relativePath)
    {
        var parts = relativePath.Split('/');
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"{relativePath} bulunamadı ({AppContext.BaseDirectory} klasöründen yukarı arandı). " +
            "Dosya taşındıysa bu testi güncelle; testi silme.");
    }
}
