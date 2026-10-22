using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK K-3 öneri 3 (karar 23): banka modülü yayıncının veri sorumlusu bilgileri toplanmadan açılmaz — ilk bağlantı
/// kaydı, Doğrula ve Banka ekle reddedilir, Obifin'e gidilmez. Bayrak yalnız pilot geçişi için kapatır; varsayılan açık.</summary>
public sealed class ObifinDataControllerGateTests
{
    /// <summary>Her Obifin çağrısını sayar; hareket sorgusu boş sayfa döner.</summary>
    private sealed class CountingObifin : IObifinClient
    {
        public int Calls { get; private set; }
        public int AccountListCalls { get; private set; }
        public int TransactionCalls { get; private set; }
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
        { Calls++; AccountListCalls++; return Task.FromResult<IReadOnlyList<ObifinAccountDto>>([]); }
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
        { Calls++; return Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>([]); }
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        { Calls++; return Task.CompletedTask; }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default)
        { Calls++; return Task.CompletedTask; }
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
        {
            Calls++; TransactionCalls++;
            return Task.FromResult(new ObifinPage<ObifinTransactionDto>([], p, 0, 0, ps));
        }
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"dc-gate-{Guid.NewGuid():N}").Options);

    private static ObifinConnectionService Service(LicenseDbContext db, IObifinClient client, bool required = true)
        => new(db, client, Protection,
            new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" })),
            Options.Create(new ObifinOptions { RequireDataControllerProfile = required }), NullLogger<ObifinConnectionService>.Instance);

    private static Guid SeedLicense(LicenseDbContext db, bool withProfile)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"g-{Guid.NewGuid():N}@x", Name = "G",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        if (withProfile) TestDataController.Add(db, customerId);
        db.SaveChanges();
        return lic;
    }

    private static Task<ObifinConnection> SaveAsync(ObifinConnectionService svc, Guid lic)
        => svc.UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}",
            CancellationToken.None);

    [Fact]
    public async Task Veri_sorumlusu_bilgisi_yoksa_ilk_kayit_reddedilir_Obifine_gidilmez()
    {
        using var db = NewDb(); var client = new CountingObifin();
        var lic = SeedLicense(db, withProfile: false);

        var save = () => SaveAsync(Service(db, client), lic);

        await save.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.DataControllerMissingMessage);
        (await db.ObifinConnections.CountAsync()).Should().Be(0);
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Bilgisi_olmayan_yayincida_Dogrula_ve_Banka_ekle_reddedilir()
    {
        // Bağlantı P1'den önce kurulmuş (ya da bilgi sonradan silinmiş): yeniden doğrulama ve yeni banka yok. Çekimin ve hesap
        // yenilemenin sürdüğü: Mevcut_Verified_baglantinin_cekimi_ve_hesap_yenilemesi_kapiya_takilmaz.
        using var db = NewDb(); var client = new CountingObifin();
        var lic = SeedLicense(db, withProfile: true);
        var svc = Service(db, client);
        await SaveAsync(svc, lic);
        db.DataControllerProfiles.RemoveRange(db.DataControllerProfiles);
        await db.SaveChangesAsync();

        var verify = () => svc.VerifyAsync(lic, CancellationToken.None);
        var add = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", new Dictionary<string, string>(), CancellationToken.None);

        await verify.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.DataControllerMissingMessage);
        await add.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.DataControllerMissingMessage);
        client.Calls.Should().Be(0);
        (await db.ObifinConnections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Mevcut_Verified_baglantinin_cekimi_ve_hesap_yenilemesi_kapiya_takilmaz()
    {
        // Karar 23'ün deploy için kritik yarısı: P1'de EMAR'ın Verified bağlantısı var ama bilgisi yok (bilgi metin v2'den sonra
        // girilir, Prod adımları 8). Saatlik hesap yenileme (Banka ekle'den hemen sonra da çağrılır) ya da çekim kapıya
        // bağlanırsa banka senkronu sessizce durur: hesap yenileme hatayı yutar, çekim hatayı bağlantıya yazar.
        using var db = NewDb(); var client = new CountingObifin();
        var lic = SeedLicense(db, withProfile: true);
        var svc = Service(db, client);
        var conn = await SaveAsync(svc, lic);
        conn.Status = ObifinConnectionStatus.Verified;
        db.DataControllerProfiles.RemoveRange(db.DataControllerProfiles);
        await db.SaveChangesAsync();

        var refreshed = await new ObifinAccountRefreshJob(db, svc, NullLogger<ObifinAccountRefreshJob>.Instance)
            .RunAsync(CancellationToken.None);

        refreshed.Should().Be(1, "hesap yenileme bilgisiz yayıncıda da başarılı biter");
        client.AccountListCalls.Should().BeGreaterThan(0, "Obifin'e gidildi");
        (await db.ObifinConnections.AsNoTracking().SingleAsync()).Status.Should().Be(ObifinConnectionStatus.Verified);

        var hasher = new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));
        var poll = new ObifinPollJob(db, client, svc, hasher, new NoopBankTransactionSink(),
            Options.Create(new ObifinOptions { RequireDataControllerProfile = true }), NullLogger<ObifinPollJob>.Instance)
        { TodayTr = () => DateOnly.FromDateTime(DateTime.UtcNow) };

        await poll.RunAsync(CancellationToken.None);

        client.TransactionCalls.Should().BeGreaterThan(0, "hareket sorgusu yapıldı");
        var after = await db.ObifinConnections.AsNoTracking().SingleAsync();
        after.LastError.Should().BeNull();
        after.LastPolledAt.Should().NotBeNull();
        after.Status.Should().Be(ObifinConnectionStatus.Verified);
    }

    [Fact]
    public async Task Bayrak_kapatilirsa_bilgi_olmadan_kayit_yapilir_varsayilan_acik()
    {
        new ObifinOptions().RequireDataControllerProfile.Should().BeTrue("fail-closed");
        using var db = NewDb(); var client = new CountingObifin();
        var lic = SeedLicense(db, withProfile: false);

        await SaveAsync(Service(db, client, required: false), lic);

        (await db.ObifinConnections.CountAsync()).Should().Be(1);
    }
}
