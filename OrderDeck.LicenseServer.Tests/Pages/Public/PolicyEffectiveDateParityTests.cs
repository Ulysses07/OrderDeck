using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// KVKK Ö-17 (P1-22): İngilizce gizlilik politikası Türkçeyle aynı yürürlük tarihini taşır ya da Türkçe metnin geçerli olduğunu,
/// Türkçenin GÜNCEL yürürlük tarihiyle söyler (<c>TURKISH_EFFECTIVE_DATE</c>). Türkçe metin yeni tarihle yayımlanıp İngilizce
/// unutulursa kırmızı. Not duruyorsa, çeviri yetişmiş olsa bile, gösterdiği tarih Türkçenin güncel tarihidir. Web kaynağı okunur
/// (web test altyapısı yok; <see cref="IntakeFormKvkkTests"/> gibi).
/// </summary>
public sealed class PolicyEffectiveDateParityTests
{
    private const string TrPath = "web/app/(tr)/gizlilik-politikasi/page.tsx";
    private const string EnPath = "web/app/(en)/en/privacy-policy/page.tsx";

    [Fact]
    public void Ingilizce_sayfa_Turkceyle_ayni_tarihte_ya_da_Turkce_metnin_gecerli_oldugunu_soyler()
    {
        var tr = Required(File.ReadAllText(FindRepoFile(TrPath)), "EFFECTIVE_DATE", TrPath);
        var enSource = File.ReadAllText(FindRepoFile(EnPath));
        var en = Required(enSource, "EFFECTIVE_DATE", EnPath);
        var turkish = Date(enSource, "TURKISH_EFFECTIVE_DATE");

        if (turkish is not null)
            turkish.Should().Be(tr,
                $"İngilizce sayfadaki 'Türkçe metin geçerlidir' notu Türkçenin GÜNCEL yürürlük tarihini göstermeli — {EnPath} " +
                "içindeki TURKISH_EFFECTIVE_DATE'i güncelle; çeviri aynı tarihle yayımlandıysa sabiti ve notu sil");
        if (en == tr) return;

        turkish.Should().NotBeNull(
            $"İngilizce sayfa ({en}) Türkçeden ({tr}) geride: TURKISH_EFFECTIVE_DATE sabiti bulunamadı — {EnPath} içine " +
            "Türkçenin tarihini taşıyan sabiti ve 'Türkçe metin geçerlidir' notunu ekle ya da çeviriyi aynı tarihle yayımla");

        var body = Regex.Match(enSource, @"<LegalLayout\b[^>]*>(.*)</LegalLayout>", RegexOptions.Singleline);
        body.Success.Should().BeTrue($"{EnPath} içinde LegalLayout gövdesi yok");
        body.Groups[1].Value.Should().Contain("prevails", "sayfada Türkçe metnin geçerli olduğunu söyleyen not çizilmeli")
            .And.Contain("{TURKISH_EFFECTIVE_DATE}", "notun tarihi sabitten basılmalı, elle yazılmamalı");
    }

    private static string Required(string source, string name, string path)
    {
        var date = Date(source, name);
        date.Should().NotBeNull($"{path} içinde {name} sabiti bulunamadı");
        return date!;
    }

    private static string? Date(string source, string name)
    {
        var m = Regex.Match(source, $@"\bconst {name}(?::\s*string)?\s*=\s*['""](\d{{4}}-\d{{2}}-\d{{2}})['""];");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Testler bin klasöründen koşuyor; repo kökünü yukarı yürüyerek buluyoruz. Bulunamazsa patlar.</summary>
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
