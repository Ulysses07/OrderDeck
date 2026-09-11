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
    /// <c>instagramIngestMode</c> alanı şemadan kaldırıldı (uzantıdan Instagram
    /// çıkınca seçilecek ikinci kip kalmadı). Sahadaki her settings.json'da o
    /// anahtar hâlâ duruyor; okuma bunun üstünde patlarsa uygulama bir daha hiç
    /// açılmaz. System.Text.Json eşleşmeyen üyeyi varsayılan olarak atlar —
    /// bu test o varsayılana bağımlılığımızı kilitliyor.
    /// </summary>
    /// <summary>
    /// N04 sözleşmesi: Update, çağıranın elindeki (muhtemelen bayat) kopyayı
    /// değil diskteki EN GÜNCEL hâli değiştirir. İki farklı bileşen ayrı
    /// alanlara yazarsa ikisi de kalıcı olmalı — bütün-nesne Save'de ikinci
    /// yazan birincinin alanını ezerdi.
    /// </summary>
    [Fact]
    public void Update_merges_into_latest_disk_state()
    {
        var path = CreateTempPath();
        var store = new SettingsStore(path);

        // Bileşen A: yazıcı adını yazar.
        store.Update(s => s.PrinterName = "Zebra ZD220");
        // Bileşen B: (A'nın yazdığından habersiz) sync imlecini yazar.
        store.Update(s => s.LastCustomerProjectionSyncAt = 1234);

        var reloaded = store.Load();
        reloaded.PrinterName.Should().Be("Zebra ZD220",
            "ikinci Update birincinin alanını ezmemeli");
        reloaded.LastCustomerProjectionSyncAt.Should().Be(1234);

        File.Delete(path);
    }

    [Fact]
    public void Update_creates_file_from_defaults_when_missing()
    {
        var path = CreateTempPath();
        var store = new SettingsStore(path);

        var result = store.Update(s => s.ChatTheme = "neon");

        result.ChatTheme.Should().Be("neon");
        result.OverlayPort.Should().Be(4747, "eksik dosya varsayılanlardan tohumlanmalı");
        store.Load().ChatTheme.Should().Be("neon");

        File.Delete(path);
    }

    [Fact]
    public void Update_returns_the_persisted_object()
    {
        var path = CreateTempPath();
        var store = new SettingsStore(path);
        store.Save(new AppSettings { PrinterName = "Önceden" });

        var result = store.Update(s => s.LabelWidthMm = 80);

        result.PrinterName.Should().Be("Önceden", "dönen nesne diskteki birleşik hâl olmalı");
        result.LabelWidthMm.Should().Be(80);

        File.Delete(path);
    }

    [Fact]
    public void Load_ignores_the_retired_instagram_mode_field()
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

        loaded.OverlayPort.Should().Be(4747);
        loaded.PrinterName.Should().Be("Zebra ZD220",
            "artık tanınmayan tek alan, dosyanın geri kalanını götürmemeli");

        File.Delete(path);
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
