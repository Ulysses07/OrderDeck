using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>IBAN ile banka veri talebi gerçek SQL Server'da (göçlerle kurulmuş şema, CHECK kısıtı): özet kısıttan geçer, silmenin
/// sorguları (özetli hareketler, eşleşmeler, kopyalar dahil elle eşleme denetimi) çevrilir, imha kayıtları silmeyle aynı işlemde
/// yazılır. <c>ClosedAt</c> jetonu UPDATE ... WHERE ClosedAt IS NULL'a çevrilir: aynı anda ikinci silme işlemiyle birlikte geri
/// alınır, ikinci bir imha kaydı kümesi kalmaz.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankSubjectRequestRelationalTests : IAsyncLifetime
{
    private sealed class HookedFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public SaveHookInterceptor Hook { get; } = new();
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }

    private readonly SqlServerContainerFixture _sql;
    private HookedFactory _factory = null!;
    public BankSubjectRequestRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new HookedFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static AuditLogEntry ManualMatchAudit(Guid txId, Guid wpfCustomerId) => new()
    {
        Id = Guid.NewGuid(), EventType = AuditEvents.BankMatchManual, TargetType = AuditTargets.BankTransaction,
        TargetId = txId.ToString(), Details = BankSubjectScrub.ManualMatchAuditDetails(wpfCustomerId), OccurredAt = DateTimeOffset.UtcNow,
        AdminId = Guid.NewGuid(), AdminUsername = "admin",
    };

    [Fact]
    public async Task Silme_gercek_SQL_Serverda_cevrilir_ayni_anda_ikinci_silme_geri_alinir_tek_kayit_kumesi_kalir()
    {
        var iban = BankHasherTests.TestIban();
        var subject = Guid.NewGuid(); var copy = Guid.NewGuid(); var other = Guid.NewGuid();
        Guid lic, decidedTx, evidenceTx, otherTx;
        AuditLogEntry ofSubject, ofCopy, ofOther;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<BankHasher>();
            var now = DateTimeOffset.UtcNow;
            var customer = new Customer { Id = Guid.NewGuid(), Email = $"bs-{Guid.NewGuid():N}@example.test", Name = "Bs",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now };
            lic = Guid.NewGuid();
            db.Customers.Add(customer);
            db.Licenses.Add(new License { Id = lic, CustomerId = customer.Id, LicenseKey = "bs-" + Guid.NewGuid().ToString("N")[..12],
                SkuCode = "STD", ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1) });
            db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = copy, LicenseId = lic, Platform = "instagram",
                Username = "ornek_m", MergedIntoId = subject, UpdatedAt = now.AddDays(-5) });
            BankTransaction Tx(int obifinId, string ibanOf) => new()
            {
                Id = Guid.NewGuid(), LicenseId = lic, ObifinId = obifinId, ObifinAccountId = 6, BankaKodu = "qnb",
                Direction = BankTransactionDirection.Incoming, Amount = 125.5m, Currency = "TL", OccurredAt = now.AddDays(-3),
                FetchedAt = now.AddDays(-3), Description = "HAVALE ornek", CounterpartyName = "Ad Soyad", RawJson = "{}",
                CounterpartyIbanHash = hasher.HashIban(ibanOf), CounterpartyIbanMasked = BankHasher.MaskIban(ibanOf),
                CounterpartyIbanHashValid = true,
            };
            var decided = Tx(1, iban); var evidence = Tx(2, iban); var unrelated = Tx(3, BankHasherTests.TestIban());
            (decidedTx, evidenceTx, otherTx) = (decided.Id, evidence.Id, unrelated.Id);
            db.BankTransactions.AddRange(decided, evidence, unrelated);
            db.PaymentMatches.AddRange(
                new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = decided.Id, ActualWpfCustomerId = subject,
                    Status = PaymentMatchStatus.ManualOnly, DecidedAt = now.AddDays(-2), IbanConfirmedAt = decided.OccurredAt,
                    Evidence = "iban-memory", CreatedAt = now, UpdatedAt = now },
                new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = evidence.Id, ActualWpfCustomerId = other,
                    Status = PaymentMatchStatus.ManualOnly, DecidedAt = now.AddDays(-2), Evidence = "name-amount", CreatedAt = now,
                    UpdatedAt = now });
            db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = subject,
                IbanHash = hasher.HashIban(iban)!, IbanMasked = BankHasher.MaskIban(iban), LearnedFrom = IbanMemorySource.ManualMatch,
                SourceBankTransactionId = decided.Id, CreatedAt = now, LastConfirmedAt = decided.OccurredAt });
            ofSubject = ManualMatchAudit(decided.Id, subject);
            ofCopy = ManualMatchAudit(decided.Id, copy);
            ofOther = ManualMatchAudit(evidence.Id, other);
            db.AuditLogs.AddRange(ofSubject, ofCopy, ofOther);
            await db.SaveChangesAsync();
        }
        Guid requestId;
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<BankSubjectRequestService>();
            // Özet DB'nin CHECK kısıtından (64 küçük harf hex) geçer; IBAN boşluklu yazılabilir.
            var spaced = string.Join(" ", Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));
            var request = await service.CreateAsync(scope.ServiceProvider.GetRequiredService<BankHasher>(), lic, spaced,
                BankSubjectRequestKind.Erasure, new DateOnly(2026, 10, 19), "yonetici-1", CancellationToken.None);
            await service.VerifyAsync(request.Id, BankSubjectVerification.RegisteredContactAndBankDocument, "yonetici-1",
                CancellationToken.None);
            requestId = request.Id;
        }
        _factory.Hook.BeforeSave = async () =>
        {
            _factory.Hook.BeforeSave = null;   // öbür yöneticinin silmesi önce iner
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BankSubjectRequestService>()
                .EraseAsync(requestId, "yonetici-2", CancellationToken.None);
        };

        try
        {
            using var scope = _factory.Services.CreateScope();
            var act = () => scope.ServiceProvider.GetRequiredService<BankSubjectRequestService>()
                .EraseAsync(requestId, "yonetici-1", CancellationToken.None);
            await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.ClosedMessage);
        }
        finally
        {
            _factory.Hook.Reset();
        }

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var closed = await vdb.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        (closed.ClosedBy, closed.Resolution, closed.IbanHash).Should().Be(("yonetici-2", BankSubjectResolution.Erased, (string?)null));
        (await vdb.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == requestId)
                .Select(x => new { x.Target, x.Action, x.RowCount, x.Actor, x.Trigger, x.LicenseId }).ToListAsync())
            .Should().BeEquivalentTo(new[]
            {
                new { Target = BankSubjectScrub.IbanMemoryTarget, Action = DataDestruction.Actions.Deleted, RowCount = 1 },
                new { Target = BankSubjectScrub.TransactionTextTarget, Action = DataDestruction.Actions.Nulled, RowCount = 2 },
                new { Target = BankSubjectScrub.LinkedMatchTarget, Action = DataDestruction.Actions.Nulled, RowCount = 1 },
                new { Target = BankSubjectScrub.MatchEvidenceTarget, Action = DataDestruction.Actions.Nulled, RowCount = 1 },
                new { Target = BankSubjectScrub.ManualMatchAuditTarget, Action = DataDestruction.Actions.Nulled, RowCount = 2 },
            }.Select(e => new { e.Target, e.Action, e.RowCount, Actor = "yonetici-2", Trigger = DataDestruction.Triggers.SubjectRequest,
                LicenseId = (Guid?)lic }), "ikinci silmenin kayıtları işlemle birlikte geri alındı");
        var matches = await vdb.PaymentMatches.AsNoTracking().Where(m => m.LicenseId == lic).ToDictionaryAsync(m => m.BankTransactionId);
        matches[decidedTx].Should().Match<PaymentMatch>(m => m.ActualWpfCustomerId == null && m.IbanConfirmedAt == null
            && m.Evidence == null && m.DecidedAt != null);
        matches[evidenceTx].Should().Match<PaymentMatch>(m => m.ActualWpfCustomerId == other && m.Evidence == null);
        (await vdb.CustomerIbanMemories.CountAsync(m => m.LicenseId == lic)).Should().Be(0);
        var texts = await vdb.BankTransactions.AsNoTracking().Where(t => t.LicenseId == lic)
            .ToDictionaryAsync(t => t.Id, t => t.Description);
        (texts[decidedTx], texts[evidenceTx], texts[otherTx]).Should().Be(((string?)null, (string?)null, "HAVALE ornek"));
        var audits = await vdb.AuditLogs.AsNoTracking()
            .Where(a => a.Id == ofSubject.Id || a.Id == ofCopy.Id || a.Id == ofOther.Id).ToDictionaryAsync(a => a.Id, a => a.Details);
        (audits[ofSubject.Id], audits[ofCopy.Id], audits[ofOther.Id]).Should().Be(((string?)null, (string?)null, ofOther.Details));
    }
}
