using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// §6 dönüş yolu — İYS'den ayna içe aktarım. Sahte istemcinin AddAsync'i
/// KASITLI patlar: ayna işi bir OKUMA işidir, beyan (add) ÇAĞIRMAMALI.
/// Testler ≤20 telefonla tek parça kalır (BatchDelay'e girmez).
/// </summary>
public sealed class IysMirrorImportJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IysMirrorImportJobTests(ApiFactory factory) => _factory = factory;

    private sealed class FakeIysClient : IIysClient
    {
        public Dictionary<string, IysConsentStatus> Statuses { get; } = new();
        public List<string[]> SearchCalls { get; } = new();

        public Task<IysAddResult> AddAsync(IysAccountContext account,
            IReadOnlyList<IysConsentRecord> items, CancellationToken ct = default)
            => throw new NotSupportedException(
                "Ayna işi /iys/add ÇAĞIRMAMALI — beyan değil okuma.");

        public Task<IysSearchResult> SearchAsync(IysAccountContext account,
            IReadOnlyList<string> recipients, CancellationToken ct = default)
        {
            SearchCalls.Add(recipients.ToArray());
            var found = recipients
                .Where(Statuses.ContainsKey)
                .ToDictionary(r => r, r => Statuses[r]);
            return Task.FromResult(new IysSearchResult("0", "{}", found));
        }
    }

    private static string NewPhone()
        => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task<(Guid LicenseId, string BrandCode)> SeedAsync(
        LicenseDbContext db, NetgsmAccountService accounts,
        NetgsmAccountStatus status, params string[] phones)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Donen",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-{Guid.NewGuid():N}"[..24],
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        });
        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}"[..32],
            PasswordProtected = accounts.ProtectPassword($"p-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        foreach (var p in phones)
        {
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                Platform = "youtube",
                Username = $"u{Guid.NewGuid():N}"[..12],
                FullName = "Musteri",
                Phone = p,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (licenseId, brandCode);
    }

    [Fact]
    public async Task Onay_ve_ret_terminal_satir_olarak_yazilir_unknown_atlanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var onayPhone = NewPhone();
        var retPhone = NewPhone();
        var unknownPhone = NewPhone();
        var (licenseId, brand) = await SeedAsync(db, accounts,
            NetgsmAccountStatus.Verified, onayPhone, retPhone, unknownPhone);

        var fake = new FakeIysClient();
        fake.Statuses[onayPhone] = IysConsentStatus.Onay;
        fake.Statuses[retPhone] = IysConsentStatus.Ret;
        fake.Statuses[unknownPhone] = IysConsentStatus.Unknown;

        var job = new IysMirrorImportJob(db, accounts, fake,
            NullLogger<IysMirrorImportJob>.Instance);
        await job.RunAsync(licenseId, CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await vdb.IysConsents.AsNoTracking()
            .Where(c => c.BrandCode == brand).ToListAsync();
        rows.Should().HaveCount(2, "Unknown = İYS'de kayıt yok → satır yazılmaz");

        var onay = rows.Single(r => r.Recipient == onayPhone);
        onay.Status.Should().Be(IysConsentStatus.Onay);
        onay.SourceCode.Should().Be(IysMirrorImportJob.SourceCodeMirror);
        onay.PushState.Should().Be(IysPushState.Confirmed,
            "terminal — push işi Pending'i tarar, ayna yeniden beyan ETMEMELİ");
        onay.ConsentDate.Should().BeNull("/iys/search consentDate döndürmüyor");
        onay.NextVerifyAt.Should().BeNull("verify işi Pushed'ı tarar — ayna dışında");
        onay.LastVerifiedStatus.Should().Be(IysConsentStatus.Onay);

        rows.Single(r => r.Recipient == retPhone).Status.Should().Be(IysConsentStatus.Ret);

        var events = await vdb.IysConsentEvents.AsNoTracking()
            .Where(e => e.BrandCode == brand).ToListAsync();
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e =>
            e.EventType == IysConsentEventType.SearchResult && e.LicenseId == licenseId);
    }

    [Fact]
    public async Task Yerel_satiri_olan_numara_sorgulanmaz_ve_ezilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var localPhone = NewPhone();
        var newPhone = NewPhone();
        var (licenseId, brand) = await SeedAsync(db, accounts,
            NetgsmAccountStatus.Verified, localPhone, newPhone);

        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(), BrandCode = brand, ChannelType = "MESAJ",
            RecipientType = "BIREYSEL", Recipient = localPhone,
            Status = IysConsentStatus.Onay, SourceCode = "HS_WEB",
            PushState = IysPushState.Confirmed,
            LastLocalEventAt = now, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var fake = new FakeIysClient();
        fake.Statuses[localPhone] = IysConsentStatus.Ret; // ezmeye çalışsa bile
        fake.Statuses[newPhone] = IysConsentStatus.Onay;

        var job = new IysMirrorImportJob(db, accounts, fake,
            NullLogger<IysMirrorImportJob>.Instance);
        await job.RunAsync(licenseId, CancellationToken.None);

        fake.SearchCalls.Should().HaveCount(1);
        fake.SearchCalls[0].Should().NotContain(localPhone,
            "yerel beyan varken İYS'ye sorulmaz — yerel kayıt EZİLMEZ");
        fake.SearchCalls[0].Should().Contain(newPhone);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var local = await vdb.IysConsents.AsNoTracking()
            .SingleAsync(c => c.BrandCode == brand && c.Recipient == localPhone);
        local.Status.Should().Be(IysConsentStatus.Onay);
        local.SourceCode.Should().Be("HS_WEB");
    }

    [Fact]
    public async Task Dogrulanmamis_hesapta_sessizce_cikar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (licenseId, brand) = await SeedAsync(db, accounts,
            NetgsmAccountStatus.Failed, NewPhone());

        var fake = new FakeIysClient();
        var job = new IysMirrorImportJob(db, accounts, fake,
            NullLogger<IysMirrorImportJob>.Instance);
        await job.RunAsync(licenseId, CancellationToken.None);

        fake.SearchCalls.Should().BeEmpty();
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeFalse();
    }

    [Fact]
    public async Task Ikinci_kosu_idempotent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var phone = NewPhone();
        var (licenseId, brand) = await SeedAsync(db, accounts,
            NetgsmAccountStatus.Verified, phone);

        var fake = new FakeIysClient();
        fake.Statuses[phone] = IysConsentStatus.Onay;

        var job = new IysMirrorImportJob(db, accounts, fake,
            NullLogger<IysMirrorImportJob>.Instance);
        await job.RunAsync(licenseId, CancellationToken.None);
        var callsAfterFirst = fake.SearchCalls.Count;
        await job.RunAsync(licenseId, CancellationToken.None);

        fake.SearchCalls.Count.Should().Be(callsAfterFirst,
            "ilk koşu satırı yazdı — ikinci koşuda eksik numara kalmadı");
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.CountAsync(c => c.BrandCode == brand)).Should().Be(1);
        (await vdb.IysConsentEvents.CountAsync(e => e.BrandCode == brand)).Should().Be(1);
    }
}
