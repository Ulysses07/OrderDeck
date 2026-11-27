using FluentAssertions;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>
/// Banka havalesi onayının deploy yüzeyi (spec 2026-10-10 §14, §15 S2a; saf, fikstürsüz; desen
/// <c>PersonBalanceDeployTests</c>). Prod'da license-server'ın <c>env_file</c>'ı yok: <c>.env</c>'deki anahtar
/// container'a yalnız compose'un <c>environment:</c> eşlemesindeki satırla geçer. README'nin <c>.env</c> şablonu da
/// burada sabitlenir.
/// </summary>
public sealed class BankQuestionsDeployTests
{
    /// <summary>Varsayılan SAYI: int binder'ı boş dizeyi ayrıştıramaz ve <c>BankOptions</c> açılışta okunur, boş
    /// varsayılan sunucuyu düşürürdü. <c>:?</c> eksik <c>.env</c> satırında CI'ın <c>docker compose config -q</c>
    /// kapısını düşürüp her deploy'u kilitlerdi.</summary>
    [Fact]
    public void Compose_iki_ayari_sayisal_varsayilanla_gecirir()
    {
        var compose = ReadRepoFile("deploy/docker-compose.yml");

        compose.Should().Contain("      OrderDeck__Bank__ExpectationTtlDays: \"${OrderDeck__Bank__ExpectationTtlDays:-14}\"")
            .And.Contain("      OrderDeck__Bank__StaleFeedAfterMinutes: \"${OrderDeck__Bank__StaleFeedAfterMinutes:-120}\"")
            .And.NotContain("${OrderDeck__Bank__ExpectationTtlDays:?").And.NotContain("${OrderDeck__Bank__StaleFeedAfterMinutes:?");
    }

    /// <summary>Şablonda iki satır da kapalı (<c># </c>): değer compose varsayılanından gelir, yalnız değiştirmek için
    /// açılır.</summary>
    [Fact]
    public void README_env_sablonunda_iki_satir_var()
    {
        var readme = ReadRepoFile("deploy/README.md");

        readme.Should().Contain("# OrderDeck__Bank__ExpectationTtlDays=14")
            .And.Contain("# OrderDeck__Bank__StaleFeedAfterMinutes=120");
    }

    /// <summary>Havale sorularını yalnız CLI açar ve kapatır; açılışın ve kill switch'in komutu ile S2a'nın geri alma
    /// uyarısı (eski imajda komut ikinci bir sunucu süreci başlatır, pencerenin iki kalıntısı) README'de bulunmalı. Deploy
    /// geri alması veritabanını geri yüklemez: pencerede silinen shopper'ların listesi imha kaydından çıkar (özet
    /// e-postaları yedekten dönüşün dayanağıdır, satır atlamaya açık).</summary>
    [Fact]
    public void README_komutlari_ve_geri_alma_uyarisi_var()
    {
        var readme = ReadRepoFile("deploy/README.md");

        readme.Should().Contain("bank-questions enable --license", "açılışın komutu yazılı olmalı")
            .And.Contain("bank-questions disable --license", "kill switch komutu yazılı olmalı")
            .And.Contain("Banka Faz 2 S2a tabanı", "geri alma bölümünde S2a bloğu olmalı")
            .And.Contain("Banka Faz 2 S2b tabanı", "geri alma bölümünde S2b bloğu olmalı")
            .And.Contain(
                "`SELECT DISTINCT SubjectId FROM DataDestructionRecords WHERE [Trigger] = 'shopper-purge' AND OccurredAt >= '<geri alma anı>'`",
                "pencerede silinenler imha kaydından listelenmeli");
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(FindRepoFile(relativePath));

    /// <summary>Testler bin klasöründen koşuyor; repo kökünü yukarı yürüyerek buluyoruz (PersonBalanceDeployTests gibi).</summary>
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
