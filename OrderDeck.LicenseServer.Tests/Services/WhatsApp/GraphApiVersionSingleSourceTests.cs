using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

/// <summary>
/// Graph API sürümünün tek bir yerde yaşadığını garanti eder:
/// <see cref="WhatsAppOptions.GraphApiVersion"/> varsayılanı.
///
/// <para><b>Neden bu test var (2026-08-25):</b> PR #343 sürümü v25.0'dan v26.0'a
/// çekti, testler geçti, deploy yeşil oldu — ama prod v25.0'a çağrı atmaya devam
/// etti. Çünkü <c>appsettings.json</c> aynı anahtarı açıkça yazıyordu ve
/// yapılandırma bağlama C# varsayılanını ezer. Yükseltme bir dağıtım döngüsü
/// boyunca <b>ölü koddu</b> ve hiçbir hata vermedi: eski sürüm de geçerli
/// olduğu için Graph 200 dönüyordu. Yanlışı yakalayacak tek belirti, birinin
/// gidip giden isteğin URL'sine bakmasıydı.</para>
///
/// <para>Bu yüzden düzeltme "appsettings.json'ı da v26 yap" değil, anahtarı
/// oradan <b>kaldırmak</b> oldu — iki kaynak varken ayrışma er geç tekrarlar.
/// Prod'da sürümü sabitlemek gerekirse yol hâlâ açık:
/// <c>OrderDeck__WhatsApp__GraphApiVersion</c> ortam değişkeni. Fark şu ki o
/// bilinçli ve geçici bir ezme; repodaki JSON ise sessiz ve kalıcıydı.</para>
/// </summary>
public sealed class GraphApiVersionSingleSourceTests
{
    [Fact]
    public void Appsettings_GraphApiVersionu_ezmemeli()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindAppSettings()));

        var pinned =
            doc.RootElement.TryGetProperty("OrderDeck", out var orderDeck) &&
            orderDeck.TryGetProperty("WhatsApp", out var whatsApp) &&
            whatsApp.TryGetProperty("GraphApiVersion", out _);

        pinned.Should().BeFalse(
            "appsettings.json Graph sürümünü sabitlerse WhatsAppOptions varsayılanı ölü koda " +
            "döner: sürüm yükseltmesi derlenir, testler geçer, deploy yeşil olur ve prod eski " +
            "sürüme çağrı atmaya devam eder — hem de hata vermeden. Sürümü değiştirmek için " +
            "WhatsAppOptions.GraphApiVersion'ı düzenle; prod'da geçici sabitleme gerekiyorsa " +
            "OrderDeck__WhatsApp__GraphApiVersion ortam değişkenini kullan.");
    }

    /// <summary>
    /// Sürüm etiketi Meta'nın kabul ettiği biçimde olmalı. Biçim bozuksa Graph
    /// "Unknown path components" döndürür ve bu, gönderim anına kadar fark
    /// edilmez — çünkü sürüm dizgesi URL'ye yorumlanmadan konuyor.
    /// </summary>
    [Fact]
    public void Varsayilan_surum_etiketi_gecerli_bicimde()
    {
        new WhatsAppOptions().GraphApiVersion.Should().MatchRegex(@"^v\d+\.\d+$");
    }

    /// <summary>
    /// Testler bin klasöründen koşuyor; repo kökünü yukarı yürüyerek buluyoruz.
    /// Bulunamazsa <b>sessizce geçmek yerine patlıyor</b> — bulunamayan dosya
    /// yüzünden yeşil kalan bir test, testin hiç olmamasından beterdir.
    /// </summary>
    private static string FindAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "OrderDeck.LicenseServer", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"OrderDeck.LicenseServer/appsettings.json bulunamadı ({AppContext.BaseDirectory} " +
            "klasöründen yukarı arandı). Repo düzeni değiştiyse bu testi güncelle; testi silme.");
    }
}
