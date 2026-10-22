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
    /// <summary>Her Obifin çağrısını sayar.</summary>
    private sealed class CountingObifin : IObifinClient
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
        { Calls++; return Task.FromResult<IReadOnlyList<ObifinAccountDto>>([]); }
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
        { Calls++; return Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>([]); }
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        { Calls++; return Task.CompletedTask; }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default)
        { Calls++; return Task.CompletedTask; }
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => throw new NotSupportedException();
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
    public async Task Bilgisi_olmayan_yayincida_Dogrula_ve_Banka_ekle_reddedilir_mevcut_baglanti_durur()
    {
        // Bağlantı P1'den önce kurulmuş (ya da bilgi sonradan silinmiş): satır ve çekim kalır, ama yeniden doğrulama ve yeni banka
        // yok.
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
    public async Task Bayrak_kapatilirsa_bilgi_olmadan_kayit_yapilir_varsayilan_acik()
    {
        new ObifinOptions().RequireDataControllerProfile.Should().BeTrue("fail-closed");
        using var db = NewDb(); var client = new CountingObifin();
        var lic = SeedLicense(db, withProfile: false);

        await SaveAsync(Service(db, client, required: false), lic);

        (await db.ObifinConnections.CountAsync()).Should().Be(1);
    }
}
