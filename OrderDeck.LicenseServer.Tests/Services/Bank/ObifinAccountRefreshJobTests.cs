using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class ObifinAccountRefreshJobTests
{
    /// <summary>Kullanıcı koduna göre betiklenen istemci: hesap listesi ya da fırlatılacak istisna.</summary>
    private sealed class PerUserObifin : IObifinClient
    {
        public Dictionary<string, Func<IReadOnlyList<ObifinAccountDto>>> Accounts { get; } = new();
        public List<string> Calls { get; } = new();

        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
        {
            Calls.Add(c.UserCode);
            return Task.FromResult(Accounts[c.UserCode]());
        }
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"obifin-refresh-{Guid.NewGuid():N}").Options);

    private static async Task<Guid> SeedVerifiedAsync(LicenseDbContext db, ObifinConnectionService svc, string userCode)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"c-{Guid.NewGuid():N}@x", Name = "C",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();
        var conn = await svc.UpsertAsync(licenseId, "", userCode, $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}", CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Verified;
        await db.SaveChangesAsync();
        return licenseId;
    }

    private static (ObifinAccountRefreshJob Job, ObifinConnectionService Svc) Build(LicenseDbContext db, IObifinClient client)
    {
        var hasher = new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));
        var svc = new ObifinConnectionService(db, client, Protection, hasher, Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);
        return (new ObifinAccountRefreshJob(db, svc, NullLogger<ObifinAccountRefreshJob>.Instance), svc);
    }

    private static ObifinAccountDto Account(long id)
        => new(id, "qnb", 1, "1", BankHasherTests.TestIban(), "TL", 0, null, true, null);

    [Fact]
    public async Task Bir_baglantinin_hatasi_digerlerini_durdurmaz_hata_o_baglantiya_yazilir()
    {
        // Servis istemci hatasını Failed + LastError olarak yazıp yeniden fırlatır; iş yalnız loglar ve sıradakine
        // geçer. Aksi hâlde ilk lisansın bozuk kimliği herkesin hesap yenilemesini durdururdu.
        using var db = NewDb(); var client = new PerUserObifin();
        var (job, svc) = Build(db, client);
        var licA = await SeedVerifiedAsync(db, svc, "api-a@x");
        var licB = await SeedVerifiedAsync(db, svc, "api-b@x");
        client.Accounts["api-a@x"] = () => throw new ObifinApiException(new[] { "Kullanici Bilgileri Hatali!" });
        client.Accounts["api-b@x"] = () => new[] { Account(9298) };

        var ok = await job.RunAsync(CancellationToken.None);

        ok.Should().Be(1);
        client.Calls.Should().BeEquivalentTo(new[] { "api-a@x", "api-b@x" });
        var connA = await db.ObifinConnections.SingleAsync(c => c.LicenseId == licA);
        connA.Status.Should().Be(ObifinConnectionStatus.Failed);
        connA.LastError.Should().Be("Kullanici Bilgileri Hatali!");
        var connB = await db.ObifinConnections.SingleAsync(c => c.LicenseId == licB);
        connB.Status.Should().Be(ObifinConnectionStatus.Verified);
        connB.LastError.Should().BeNull();
        (await db.BankAccounts.CountAsync(a => a.LicenseId == licB)).Should().Be(1);
    }

    [Fact]
    public async Task Cozulemeyen_kimlik_de_digerlerini_durdurmaz()
    {
        // Servis burada sınıflandırılmış istemci hatası değil InvalidOperationException fırlatır; dar bir catch
        // koşuyu keserdi.
        using var db = NewDb(); var client = new PerUserObifin();
        var (job, svc) = Build(db, client);
        var licA = await SeedVerifiedAsync(db, svc, "api-a@x");
        var licB = await SeedVerifiedAsync(db, svc, "api-b@x");
        var connA = await db.ObifinConnections.SingleAsync(c => c.LicenseId == licA);
        connA.PasswordProtected = new EphemeralDataProtectionProvider().CreateProtector("x").Protect($"pw-{Guid.NewGuid():N}");
        await db.SaveChangesAsync();
        client.Accounts["api-b@x"] = () => new[] { Account(9298) };

        var ok = await job.RunAsync(CancellationToken.None);

        ok.Should().Be(1);
        client.Calls.Should().Equal("api-b@x");
        (await db.BankAccounts.CountAsync(a => a.LicenseId == licB)).Should().Be(1);
    }

    [Fact]
    public async Task Verified_olmayan_baglanti_yenilenmez()
    {
        using var db = NewDb(); var client = new PerUserObifin();
        var (job, svc) = Build(db, client);
        var lic = await SeedVerifiedAsync(db, svc, "api-a@x");
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Disabled; await db.SaveChangesAsync();
        client.Accounts["api-a@x"] = () => new[] { Account(9298) };

        var ok = await job.RunAsync(CancellationToken.None);

        ok.Should().Be(0);
        client.Calls.Should().BeEmpty();
    }
}
