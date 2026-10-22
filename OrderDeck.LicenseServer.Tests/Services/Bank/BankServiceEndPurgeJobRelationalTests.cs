using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary><see cref="BankServiceEndPurgeJob"/> gerçek SQL Server'da (göçlerle kurulmuş şema), DI'dan çözülerek: işin kendi
/// sorgularının çevirisi — tarihsiz Disabled damgası, lisans yenilenmesinin alt sorgusu (<c>IN</c>), silinecek bağlantının
/// "yönetici durdurdu YA DA lisans geçerli değil" koşulu (<c>NOT IN</c>), hatırlatma listelerinin izdüşümleri. Yerel silmenin
/// kendisi <see cref="BankConnectionTerminatorRelationalTests"/>'te. Korunan alanlar sahte: iş kimlik çözmez, Obifin'e gitmez.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankServiceEndPurgeJobRelationalTests : IAsyncLifetime
{
    /// <summary>Uyarı adresi dolu: işin e-postaları fabrikanın <see cref="ApiFactory.Email"/>'ine düşer.</summary>
    private sealed class AlertingFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            [AdminJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
        };
    }

    private readonly SqlServerContainerFixture _sql;
    private AlertingFactory _factory = null!;
    public BankServiceEndPurgeJobRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new AlertingFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Lisans + bağlantı + yerel banka bağlantısı (#501) + onaylı hesap 6 + bir hareket.</summary>
    private async Task<Guid> SeedAsync(ObifinConnectionStatus status, DateTimeOffset? disabledAt, bool licenseActive,
        bool disabledByAdmin = false, DateTimeOffset? purgedAt = null, DateOnly? anchor = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"se-{Guid.NewGuid():N}@example.test", Name = "Se",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now };
        var lic = Guid.NewGuid();
        db.Customers.Add(customer);
        db.Licenses.Add(new License { Id = lic, CustomerId = customer.Id, LicenseKey = "se-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = now.AddYears(-1),
            ExpiresAt = licenseActive ? now.AddDays(30) : now.AddDays(-40) });
        var connId = Guid.NewGuid();
        db.ObifinConnections.Add(new ObifinConnection { Id = connId, LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"u-{Guid.NewGuid():N}@x", PasswordProtected = $"p-{Guid.NewGuid():N}", ApiKeyProtected = $"a-{Guid.NewGuid():N}",
            Status = status, DisabledAt = disabledAt, DisabledByAdmin = disabledByAdmin, BankDataPurgedAt = purgedAt,
            InitialCollectFrom = anchor, CreatedAt = now, UpdatedAt = now });
        var bankConnId = Guid.NewGuid();
        db.BankConnections.Add(new BankConnection { Id = bankConnId, LicenseId = lic, ObifinConnectionId = connId, BankaKodu = "qnb",
            BankaApiId = 501, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now });
        db.BankAccounts.Add(new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, BankConnectionId = bankConnId, ObifinAccountId = 6,
            BankaKodu = "qnb", IbanMasked = "TR12…345", IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            NotificationNote = "bildirim", Currency = "TL", RefreshedAt = now, ApprovedAt = now, ApprovedBy = "yonetici-1" });
        db.BankTransactions.Add(new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 1, ObifinAccountId = 6,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now,
            FetchedAt = now, Description = "aciklama" });
        await db.SaveChangesAsync();
        return lic;
    }

    [Fact]
    public async Task Hizmet_sonu_isi_gercek_SQL_Serverda_damgalar_yeniler_durdurur_yerelde_siler_ve_hatirlatir()
    {
        var now = DateTimeOffset.UtcNow;
        var oldAnchor = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-60);
        var ended = await SeedAsync(ObifinConnectionStatus.Disabled, now.AddDays(-31), licenseActive: false);
        var adminStopped = await SeedAsync(ObifinConnectionStatus.Disabled, now.AddDays(-31), licenseActive: true,
            disabledByAdmin: true);
        var renewedPurged = await SeedAsync(ObifinConnectionStatus.Disabled, now.AddDays(-45), licenseActive: true,
            purgedAt: now.AddDays(-15), anchor: oldAnchor);
        var waiting = await SeedAsync(ObifinConnectionStatus.Disabled, now.AddDays(-29), licenseActive: false);
        var undated = await SeedAsync(ObifinConnectionStatus.Disabled, null, licenseActive: false);
        var endedVerified = await SeedAsync(ObifinConnectionStatus.Verified, null, licenseActive: false);
        var awaitingRemoval = await SeedAsync(ObifinConnectionStatus.Disabled, now.AddDays(-50), licenseActive: false,
            purgedAt: now.AddDays(-20));
        var copy = new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), RequestedAt = now.AddDays(-2),
            RequestedBy = "yonetici-1", ManualAction = "Obifin'e ulaşılamadı: Obifin panelinden bu lisansın banka bağlantılarını elle kaldırın." };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.ObifinCopyDeletions.Add(copy);
            await db.SaveChangesAsync();
        }
        var anchorBefore = BankDataCleanupJob.AnchorFor(DateTimeOffset.UtcNow);

        int deleted;
        using (var scope = _factory.Services.CreateScope())
            deleted = await scope.ServiceProvider.GetRequiredService<BankServiceEndPurgeJob>().RunAsync(CancellationToken.None);

        var anchorAfter = BankDataCleanupJob.AnchorFor(DateTimeOffset.UtcNow);
        deleted.Should().Be(2);
        using var check = _factory.Services.CreateScope();
        var read = check.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var conns = await read.ObifinConnections.AsNoTracking().ToDictionaryAsync(c => c.LicenseId);
        var withTransactions = await read.BankTransactions.AsNoTracking().Select(t => t.LicenseId).Distinct().ToListAsync();

        foreach (var lic in new[] { ended, adminStopped })
        {
            conns[lic].Status.Should().Be(ObifinConnectionStatus.Disabled);
            conns[lic].BankDataPurgedAt.Should().NotBeNull();
            withTransactions.Should().NotContain(lic);
        }
        conns[renewedPurged].Status.Should().Be(ObifinConnectionStatus.Unverified);
        conns[renewedPurged].BankDataPurgedAt.Should().BeNull();
        conns[renewedPurged].InitialCollectFrom.Should().BeOneOf(new DateOnly?[] { anchorBefore, anchorAfter }, "karar 28");
        conns[waiting].BankDataPurgedAt.Should().BeNull("30 günü dolmadı");
        (conns[undated].DisabledByAdmin, conns[undated].DisabledAt.HasValue).Should().Be((true, true));
        conns[endedVerified].Status.Should().Be(ObifinConnectionStatus.Disabled);
        conns[endedVerified].DisabledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        withTransactions.Should().Contain(new[] { renewedPurged, waiting, undated, endedVerified, awaitingRemoval });

        (await read.DataDestructionRecords.AsNoTracking()
                .Where(r => r.Trigger == DataDestruction.Triggers.ServiceEnd && r.Target == "BankTransactions")
                .Select(r => r.LicenseId).ToListAsync())
            .Should().BeEquivalentTo(new Guid?[] { ended, adminStopped });
        (await read.DataDestructionRecords.AsNoTracking().CountAsync(r => r.Target == BankServiceEndPurgeJob.JobId
            && r.Action == DataDestruction.Actions.RunCompleted)).Should().Be(1);

        var mails = _factory.Email.Sent.ToList();
        mails.Select(m => m.Subject).Should().BeEquivalentTo(new[]
        {
            BankServiceEndPurgeJob.RenewedSubject, BankServiceEndPurgeJob.DoneSubject,
            BankServiceEndPurgeJob.ObifinOverdueSubject,
        });
        var reminder = mails.Single(m => m.Subject == BankServiceEndPurgeJob.ObifinOverdueSubject).PlainBody;
        reminder.Should().Contain(awaitingRemoval.ToString()).And.Contain(copy.LicenseId.ToString()).And.Contain(copy.ManualAction!)
            .And.NotContain(ended.ToString(), "bu koşuda silinenler 'yapıldı' e-postasında")
            .And.NotContain(renewedPurged.ToString(), "yenilenen lisansın işareti boşaldı");
    }
}
