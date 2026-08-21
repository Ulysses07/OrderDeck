using System;
using System.IO;
using FluentAssertions;
using OrderDeck.Core.Settings;
using Xunit;

namespace OrderDeck.Tests.Settings;

public class SettingsStoreTests
{
    private string CreateTempPath() =>
        Path.Combine(Path.GetTempPath(), $"orderdeck-test-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var store = new SettingsStore(CreateTempPath());
        var settings = store.Load();

        settings.OverlayPort.Should().Be(4747);
        settings.LabelWidthMm.Should().Be(60);
        settings.LabelHeightMm.Should().Be(30);
        settings.LabelFontFamily.Should().Be("Arial");
        settings.PrinterName.Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var path = CreateTempPath();
        var store = new SettingsStore(path);
        var original = new AppSettings
        {
            OverlayPort = 5000,
            ChatTheme = "neon",
            PrinterName = "Zebra ZD220",
            LabelWidthMm = 75,
            LabelHeightMm = 40,
            LabelGapMm = 3,
            LabelFontFamily = "Segoe UI",
            LabelUserFontSize = 16,
            LabelMessageFontSize = 13
        };

        store.Save(original);
        var reloaded = store.Load();

        reloaded.OverlayPort.Should().Be(5000);
        reloaded.ChatTheme.Should().Be("neon");
        reloaded.PrinterName.Should().Be("Zebra ZD220");
        reloaded.LabelWidthMm.Should().Be(75);
        reloaded.LabelHeightMm.Should().Be(40);
        reloaded.LabelGapMm.Should().Be(3);
        reloaded.LabelFontFamily.Should().Be("Segoe UI");
        reloaded.LabelUserFontSize.Should().Be(16);
        reloaded.LabelMessageFontSize.Should().Be(13);

        File.Delete(path);
    }

    /// <summary>
    /// Instagram kazıyıcısı uzantıdan kaldırıldı; tek yol resmi Graph API.
    /// C# varsayılanını çevirmek sahadaki kurulumlar için YETMEZ: settings.json
    /// tüm nesneyi yazdığı için bir kez "Kaydet"e basmış her makinede alan
    /// dosyada duruyor ve deserialize'da diskteki değer varsayılanı ezer.
    /// Göç olmasa o yayıncıların Instagram'ı yeni uzantı sürümüyle birlikte
    /// sessizce kararırdı — uzantı IG göndermiyor, resmi ingestor da kipe
    /// bakıp kendini kapatıyor.
    /// </summary>
    [Fact]
    public void Load_migrates_a_persisted_Scraper_mode_to_the_official_api()
    {
        var path = CreateTempPath();
        File.WriteAllText(path, """
            {
              "overlayPort": 4747,
              "instagramIngestMode": "Scraper",
              "printerName": "Zebra ZD220"
            }
            """);

        var loaded = new SettingsStore(path).Load();

        loaded.InstagramIngestMode.Should().Be(OrderDeck.Core.Chat.InstagramIngestMode.OfficialApi);
        loaded.PrinterName.Should().Be("Zebra ZD220", "göç yalnız IG kipine dokunmalı");

        File.Delete(path);
    }

    /// <summary>
    /// Göç ana dosyayla sınırlı kalmamalı: bozuk settings.json'da Load yedeğe
    /// düşüyor ve yedek de aynı eski değeri taşıyor.
    /// </summary>
    [Fact]
    public void Load_migrates_the_backup_copy_too()
    {
        var path = CreateTempPath();
        File.WriteAllText(path, "{ bozuk json");
        File.WriteAllText(path + ".bak", """{ "instagramIngestMode": "Scraper" }""");

        var loaded = new SettingsStore(path).Load();

        loaded.InstagramIngestMode.Should().Be(OrderDeck.Core.Chat.InstagramIngestMode.OfficialApi);

        File.Delete(path + ".bak");
        foreach (var f in Directory.GetFiles(Path.GetTempPath(),
                     Path.GetFileName(path) + ".corrupt-*"))
            File.Delete(f);
    }
}

public class SettingsStore_PaymentTests
{
    [Fact]
    public void Save_Then_Load_RoundTripsPaymentSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orderdeck-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var s = new AppSettings();
            s.Payment.WhatsAppMessageTemplate = "Hi {ad}, pay {tutar}!";
            s.Payment.Iban = "TR12";
            s.Payment.AccountHolder = "Burak";
            s.Payment.Papara = "1234567";
            store.Save(s);

            var loaded = store.Load();
            loaded.Payment.WhatsAppMessageTemplate.Should().Be("Hi {ad}, pay {tutar}!");
            loaded.Payment.Iban.Should().Be("TR12");
            loaded.Payment.AccountHolder.Should().Be("Burak");
            loaded.Payment.Papara.Should().Be("1234567");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_FreshFile_HasDefaultPaymentTemplate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orderdeck-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var loaded = store.Load();
            loaded.Payment.Should().NotBeNull();
            loaded.Payment.WhatsAppMessageTemplate.Should().Contain("{ad}");
            loaded.Payment.WhatsAppMessageTemplate.Should().Contain("{tutar}");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
