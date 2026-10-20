using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>KVKK Ö-12/Ö-13: imha kayıtlarının veritabanı dışındaki kopyası — saatlik e-posta, kişisel veri yok, pencereler
/// örtüşmez.</summary>
public sealed class DataDestructionDigestJobTests
{
    private static LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"destruction-digest-{Guid.NewGuid():N}").Options);

    private static DataDestructionDigestJob Job(LicenseDbContext db, TestEmailSender email)
        => new(db, new BankJobAlerts(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BankJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
            }).Build(), NullLogger<BankJobAlerts>.Instance));

    private static DataDestructionRecord Record(DateTimeOffset at, string target) => new()
    {
        Id = Guid.NewGuid(), OccurredAt = at, LicenseId = Guid.NewGuid(), SubjectId = Guid.NewGuid(), Target = target,
        Action = DataDestruction.Actions.Deleted, RowCount = 3, Criterion = "OccurredAt+30g;eşleşmeyen",
        Trigger = DataDestruction.Triggers.ShopperPurge, Actor = DataDestruction.SystemActor,
    };

    [Fact]
    public async Task Onceki_tam_saatin_kayitlari_CSV_olarak_gider_pencere_disindakiler_gitmez()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var end = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var inside = Record(end.AddMinutes(-59), "Shoppers.FullName+Phone+Address+Email+TcProtected");
        var atStart = Record(end.AddHours(-1), "PaymentMatches.WpfCustomerId+Evidence");
        var atEnd = Record(end, "BankTransactions");
        var beforeStart = Record(end.AddHours(-1).AddTicks(-1), "CustomerIbanMemories");
        db.DataDestructionRecords.AddRange(inside, atStart, atEnd, beforeStart);
        await db.SaveChangesAsync();

        var sent = await Job(db, email).RunForWindowEndingAsync(end, CancellationToken.None);

        sent.Should().Be(2, "pencere [end − 1 saat, end)");
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(DataDestructionDigestJob.Subject);
        mail.PlainBody.Should().Contain(inside.SubjectId.ToString()).And.Contain(atStart.Target).And.Contain("shopper-purge")
            .And.NotContain(atEnd.Target).And.NotContain(beforeStart.Target);
        mail.PlainBody.Should().Contain("OccurredAt+30g,eşleşmeyen", "ayraç ölçüt içinde kaçışlanır");
    }

    [Fact]
    public async Task Kayit_yoksa_e_posta_gitmez()
    {
        using var db = NewDb(); var email = new TestEmailSender();

        (await Job(db, email).RunForWindowEndingAsync(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero),
            CancellationToken.None)).Should().Be(0);
        email.Sent.Should().BeEmpty();
    }
}
