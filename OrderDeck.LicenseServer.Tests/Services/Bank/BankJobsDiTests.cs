using System.Collections.Concurrent;
using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class BankJobsDiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BankJobsDiTests(ApiFactory factory) => _factory = factory;

    /// <summary>Banka anahtarını ezen fabrika: anahtarsız/kısa anahtarlı açılışı sınar; uyarı günlüklerini toplar.</summary>
    private sealed class BankKeyApiFactory : ApiFactory
    {
        private readonly string _hashKey;
        public BankKeyApiFactory(string hashKey) => _hashKey = hashKey;
        public WarningRecorder Log { get; } = new();
        protected override IDictionary<string, string?> ExtraConfig
            => new Dictionary<string, string?> { ["OrderDeck:Bank:HashKey"] = _hashKey };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(l => l.AddProvider(Log));
        }
    }

    /// <summary>Uyarı düzeyindeki günlük satırlarını biçimlenmiş metin olarak toplar.</summary>
    private sealed class WarningRecorder : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void Dispose() { }

        private sealed class Recorder(WarningRecorder owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) owner.Warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    [Fact]
    public void Banka_isleri_ve_servisleri_DI_kapsamindan_cozulur()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ObifinPollJob>().Should().NotBeNull();
        sp.GetRequiredService<ObifinAccountRefreshJob>().Should().NotBeNull();
        sp.GetRequiredService<BankDataRetentionJob>().Should().NotBeNull();
        sp.GetRequiredService<ObifinConnectionService>().Should().NotBeNull();
        sp.GetRequiredService<BankHasher>().Should().NotBeNull("Testing'de HashKey ApiFactory'den gelir");
        sp.GetRequiredService<IObifinClient>().Should().BeOfType<NullObifinClient>("test ortamı Obifin'e bağlanmaz");
        sp.GetRequiredService<IBankTransactionSink>().Should().NotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task HashKey_yok_ya_da_kisaysa_sunucu_yine_acilir_BankHasher_Turkce_hatayla_duser(int keyLength)
    {
        // Master'a merge = otomatik prod deploy. .env'de eksik bir satır lisans sunucusunu düşürmemeli: banka modülü
        // kapalı açılır, BankHasher'ı isteyen her şey açık bir Türkçe hatayla düşer (admin sayfaları yakalayıp gösterir).
        using var factory = new BankKeyApiFactory(new string('k', keyLength));

        var health = await factory.CreateClient().GetAsync("/healthz");

        health.IsSuccessStatusCode.Should().BeTrue("banka anahtarı yok diye sunucu kapanmaz");
        using var scope = factory.Services.CreateScope();
        var resolve = () => scope.ServiceProvider.GetRequiredService<ObifinPollJob>();
        resolve.Should().Throw<InvalidOperationException>().WithMessage(BankHasher.DisabledMessage);
        BankHasher.DisabledMessage.Should().Be("Banka modülü kapalı: OrderDeck:Bank:HashKey yok ya da 32 bayttan kısa");
        factory.Log.Warnings.Count(w => w == BankHasher.DisabledMessage).Should().Be(1, "açılışta tek uyarı");
    }

    [Fact]
    public void HashKey_gecerliyse_acilis_uyarisi_yok_BankHasher_cozulur()
    {
        using var factory = new BankKeyApiFactory(new string('k', BankHasher.MinKeyBytes));

        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<BankHasher>().Should().NotBeNull();
        factory.Log.Warnings.Should().NotContain(BankHasher.DisabledMessage);
    }

    [Fact]
    public void Anahtar_gecerliyse_uc_banka_isi_UTC_takvimiyle_kaydolur()
    {
        var storage = new MemoryStorage();

        Program.ScheduleBankJobs(new RecurringJobManager(storage), enabled: true);

        using var connection = storage.GetConnection();
        connection.GetRecurringJobs().Select(j => (j.Id, j.Cron)).Should().BeEquivalentTo(new[]
        {
            ("obifin-poll", "*/5 * * * *"),
            // Çekimle ortak kilit: 5 dakikalık ızgaranın dışında, yoksa her seferinde çekimi bekler.
            ("obifin-accounts", "2 * * * *"),
            // 04:52 eşitlemeden sonra ve 5 dakikalık ızgaranın DIŞINDA: 04:55'te obifin-poll (aynı BankTransactions
            // tablosuna yazar) ve üç */5 iş daha koşar; */15 ise 04:45 ve 05:00'te.
            ("bank-data-retention", "57 4 * * *"),
        });
    }

    [Fact]
    public void Anahtar_gecersizse_banka_isleri_kaydolmaz_onceden_kayitli_olanlar_silinir()
    {
        // Önceki açılışta (anahtar varken) kaydolmuş işler anahtar kalkınca Hangfire'da kalıp her 5 dakikada bir
        // BankHasher hatasıyla düşmesin.
        var storage = new MemoryStorage();
        var manager = new RecurringJobManager(storage);
        Program.ScheduleBankJobs(manager, enabled: true);

        Program.ScheduleBankJobs(manager, enabled: false);

        using var connection = storage.GetConnection();
        connection.GetRecurringJobs().Should().BeEmpty();
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(0, 40)]
    public void Obifin_istemcisi_yonlendirme_izlemez_kimlik_basliklarini_loglamaz_zaman_asimi_ayardan(int configured, int expected)
    {
        // Kimlik (KullaniciAdi/Sifre/APIKey) özel başlıkta gider; .NET yönlendirmede yalnız Authorization'ı düşürür,
        // bunları yönlendirme hedefine taşırdı. Aynı başlıklar HttpClient günlüğüne de düşmemeli. Çerez de tutulmaz:
        // havuzlanan birincil işleyici tek CookieContainer'ı tüm kiracıların istemcileriyle paylaşır.
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ObifinOptions>(o => o.TimeoutSeconds = configured);
        Program.AddObifinHttpClient(services);
        using var sp = services.BuildServiceProvider();
        var name = typeof(IObifinClient).Name;

        sp.GetRequiredService<IObifinClient>().Should().BeOfType<ObifinClient>();
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(name).Timeout.Should().Be(TimeSpan.FromSeconds(expected));
        var options = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(name);
        foreach (var header in new[] { "KullaniciAdi", "Sifre", "APIKey" })
            options.ShouldRedactHeaderValue(header).Should().BeTrue($"{header} günlüğe açık yazılmaz");
        HttpMessageHandler handler = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        var primary = handler.Should().BeOfType<HttpClientHandler>().Which;
        primary.AllowAutoRedirect.Should().BeFalse();
        primary.UseCookies.Should().BeFalse("bir kiracının oturum çerezi sonraki kiracının isteğine taşınmamalı");
    }
}
