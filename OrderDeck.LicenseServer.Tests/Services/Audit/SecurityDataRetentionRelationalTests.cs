using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Audit;

/// <summary>SecurityDataRetentionJob gerçek SQL Server'da: InMemory ExecuteUpdate ve işlem bilmez, orada yükle+yaz dalı koşar.
/// Burada ExecuteUpdate (Take'li parti) ile boşaltma ve imha kaydı aynı açık işlemde, koşu kaydı sonda; silme talebinin telefonu
/// ve banka veri talebinin kısaltılmış IBAN'ı (eşzamanlılık jetonlu tablo) sorgularıyla birlikte sınanır (KVKK D-5, Ö-11, Ö-13).</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SecurityDataRetentionRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public SecurityDataRetentionRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static ShopperDeletionRequest DeletionRequest(DateTimeOffset? handledAt) => new()
    {
        Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
        RequestedAt = DateTimeOffset.UtcNow.AddDays(-60), HandledAt = handledAt, HandledBy = handledAt is null ? null : "admin",
    };

    private static BankSubjectRequest BankRequest(DateTimeOffset? closedAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), Kind = BankSubjectRequestKind.Erasure,
        // Açık talepte özet dolu (CHECK: 64 küçük hex); kapanışta boşalmıştır.
        IbanHash = closedAt is null ? $"{Guid.NewGuid():N}{Guid.NewGuid():N}" : null, IbanMasked = "TR12…345",
        ReceivedAt = DateTimeOffset.UtcNow.AddDays(-60), DueAt = DateTimeOffset.UtcNow.AddDays(-30),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-60), CreatedBy = "admin", ClosedAt = closedAt,
        ClosedBy = closedAt is null ? null : "admin",
        Resolution = closedAt is null ? BankSubjectResolution.None : BankSubjectResolution.Erased,
    };

    private async Task SeedAsync(params object[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Telefon_bosaltmasi_ve_imha_kaydi_gercek_SQL_Serverda_ayni_islemde_yazilir_kosu_kaydi_kalir()
    {
        var handled = DeletionRequest(DateTimeOffset.UtcNow.AddDays(-31));
        var pending = DeletionRequest(null);
        var closed = BankRequest(DateTimeOffset.UtcNow.AddDays(-31));
        var open = BankRequest(null);
        await SeedAsync(handled, pending, closed, open);

        // DI'dan: üretimdeki kurulum (yönetici uyarısı dahil) çözülür.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SecurityDataRetentionJob>().AnonymiseAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.ShopperDeletionRequests.AsNoTracking().SingleAsync(r => r.Id == handled.Id)).PhoneAtRequest.Should().BeEmpty();
        (await vdb.ShopperDeletionRequests.AsNoTracking().SingleAsync(r => r.Id == pending.Id)).PhoneAtRequest
            .Should().Be(pending.PhoneAtRequest);
        var closedAfter = await vdb.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == closed.Id);
        closedAfter.IbanMasked.Should().BeEmpty();
        closedAfter.ClosedAt.Should().Be(closed.ClosedAt, "ExecuteUpdate kapanış anına (eşzamanlılık jetonu) dokunmaz");
        (await vdb.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == open.Id)).IbanMasked.Should().Be("TR12…345");

        var records = await vdb.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().ContainSingle(r => r.Target == "ShopperDeletionRequests.PhoneAtRequest")
            .Which.Should().Match<DataDestructionRecord>(r => r.Action == DataDestruction.Actions.Nulled && r.RowCount == 1
                && r.Trigger == DataDestruction.Triggers.Retention && r.Actor == DataDestruction.SystemActor);
        records.Should().ContainSingle(r => r.Target == "BankSubjectRequests.IbanMasked")
            .Which.Should().Match<DataDestructionRecord>(r => r.Action == DataDestruction.Actions.Nulled && r.RowCount == 1
                && r.Trigger == DataDestruction.Triggers.Retention);
        records.Should().ContainSingle(r => r.Action == DataDestruction.Actions.RunCompleted && r.Target == SecurityDataRetentionJob.JobId);
    }

    [Fact]
    public async Task Parti_parti_bosaltmada_her_parti_kendi_kaydini_yazar_ikinci_kosu_bosaltilmisi_yeniden_saymaz()
    {
        var handled = new[] { DeletionRequest(DateTimeOffset.UtcNow.AddDays(-40)), DeletionRequest(DateTimeOffset.UtcNow.AddDays(-35)) };
        var recent = DeletionRequest(DateTimeOffset.UtcNow.AddDays(-29));
        await SeedAsync(handled[0], handled[1], recent);

        // Parti boyutu 1: ExecuteUpdate'in Take'li çevirisi birden çok tur döner, döngü sonlanır.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            Task Run() => new SecurityDataRetentionJob(db, Options.Create(new SecurityDataRetentionOptions { BatchSize = 1 }),
                scope.ServiceProvider.GetRequiredService<AdminJobAlerts>(), NullLogger<SecurityDataRetentionJob>.Instance)
                .AnonymiseAsync(CancellationToken.None);
            await Run();
            await Run();
        }

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var phones = await vdb.ShopperDeletionRequests.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.PhoneAtRequest);
        phones[handled[0].Id].Should().BeEmpty();
        phones[handled[1].Id].Should().BeEmpty();
        phones[recent.Id].Should().Be(recent.PhoneAtRequest);
        var records = await vdb.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Where(r => r.Target == "ShopperDeletionRequests.PhoneAtRequest").Select(r => r.RowCount)
            .Should().Equal(1, 1);
        records.Count(r => r.Action == DataDestruction.Actions.RunCompleted && r.Target == SecurityDataRetentionJob.JobId)
            .Should().Be(2);
    }
}
