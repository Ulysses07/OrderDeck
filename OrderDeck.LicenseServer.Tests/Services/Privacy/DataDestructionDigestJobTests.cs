using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>KVKK Ö-12/Ö-13: imha kayıtlarının veritabanı dışındaki kopyası. Durum tutar (<c>DigestedAt</c>): gönderim düşerse
/// kayıt bir sonraki koşuda gider, kesintide biriken saatler tek koşuda gider; kişisel veri yok.</summary>
public sealed class DataDestructionDigestJobTests
{
    /// <summary>Sabit saat. Yerel saat dilimi BİLEREK UTC+3: yerel saatle yuvarlama hatası makinenin diliminden bağımsız
    /// yakalanır (CI ubuntu'da UTC).</summary>
    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        private static readonly TimeZoneInfo Plus3 = TimeZoneInfo.CreateCustomTimeZone("test+03", TimeSpan.FromHours(3),
            "test+03", "test+03");
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => Plus3;
    }

    private static LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"destruction-digest-{Guid.NewGuid():N}").Options);

    private static AdminJobAlerts Alerts(TestEmailSender email, bool withAddress = true)
        => new(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AdminJobAlerts.AlertEmailKey] = withAddress ? $"ops-{Guid.NewGuid():N}@example.test" : "",
        }).Build(), NullLogger<AdminJobAlerts>.Instance);

    private static DataDestructionDigestJob Job(LicenseDbContext db, TestEmailSender email, TimeProvider clock,
        bool withAddress = true)
        => new(db, Alerts(email, withAddress), clock);

    /// <summary>09:13:27 UTC (TR 12:13): saat başı 09:00 UTC.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 13, 27, TimeSpan.Zero);
    private static readonly DateTimeOffset HourStart = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static DataDestructionRecord Record(DateTimeOffset at, string target, DateTimeOffset? digestedAt = null) => new()
    {
        Id = Guid.NewGuid(), OccurredAt = at, LicenseId = Guid.NewGuid(), SubjectId = Guid.NewGuid(), Target = target,
        Action = DataDestruction.Actions.Deleted, RowCount = 3, Criterion = "OccurredAt+30g;eşleşmeyen",
        Trigger = DataDestruction.Triggers.ShopperPurge, Actor = DataDestruction.SystemActor, DigestedAt = digestedAt,
    };

    private static async Task<List<Guid>> UndigestedAsync(LicenseDbContext db)
        => await db.DataDestructionRecords.AsNoTracking().Where(r => r.DigestedAt == null).Select(r => r.Id).ToListAsync();

    [Fact]
    public async Task UTC_saat_basindan_onceki_gonderilmemis_kayitlar_CSV_olarak_gider_ve_isaretlenir()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var inside = Record(HourStart.AddMinutes(-59), "Shoppers.FullName+Phone+Address+Email+TcProtected");
        var justBefore = Record(HourStart.AddTicks(-1), "PaymentMatches.WpfCustomerId+Evidence");
        var atHourStart = Record(HourStart, "BankTransactions");
        var thisHour = Record(Now.AddMinutes(-1), "BankAccounts");
        var alreadySent = Record(HourStart.AddHours(-3), "CustomerIbanMemories", digestedAt: HourStart.AddHours(-2));
        db.DataDestructionRecords.AddRange(inside, justBefore, atHourStart, thisHour, alreadySent);
        await db.SaveChangesAsync();

        var sent = await Job(db, email, new FixedClock(Now)).RunAsync(CancellationToken.None);

        sent.Should().Be(2, "UTC saat başından (09:00Z) önceki ve henüz gönderilmemiş kayıtlar; yerel saat (12:00) değil");
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(DataDestructionDigestJob.Subject);
        mail.PlainBody.Should().Contain(inside.SubjectId.ToString()).And.Contain(justBefore.Target).And.Contain("shopper-purge")
            .And.NotContain(atHourStart.Target).And.NotContain(thisHour.Target).And.NotContain(alreadySent.Target);
        mail.PlainBody.Should().Contain("OccurredAt+30g,eşleşmeyen", "ayraç ölçüt içinde kaçışlanır");
        (await UndigestedAsync(db)).Should().BeEquivalentTo(new[] { atHourStart.Id, thisHour.Id });
        (await db.DataDestructionRecords.AsNoTracking().SingleAsync(r => r.Id == inside.Id)).DigestedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Gonderim_duserse_isaret_yazilmaz_is_duser_sonraki_kosu_gonderir()
    {
        // SMTP hatası yutulursa iş Succeeded görünür ve o saatin kayıtları bir daha gitmez: pencere sessizce kaybolurdu.
        using var db = NewDb(); var email = new TestEmailSender { FailWith = _ => new InvalidOperationException("smtp kapalı") };
        var record = Record(HourStart.AddMinutes(-30), "BankTransactions");
        db.DataDestructionRecords.Add(record);
        await db.SaveChangesAsync();
        var clock = new FixedClock(Now);

        var act = () => Job(db, email, clock).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("iş Hangfire'da Failed görünmeli");
        email.Sent.Should().BeEmpty();
        (await UndigestedAsync(db)).Should().BeEquivalentTo(new[] { record.Id });

        email.FailWith = null;
        clock.UtcNow = Now.AddHours(1);
        (await Job(db, email, clock).RunAsync(CancellationToken.None)).Should().Be(1);

        email.Sent.Should().ContainSingle().Which.PlainBody.Should().Contain(record.SubjectId.ToString());
        (await UndigestedAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Uyari_adresi_bossa_is_duser_isaret_yazilmaz()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        db.DataDestructionRecords.Add(Record(HourStart.AddMinutes(-30), "BankTransactions"));
        await db.SaveChangesAsync();

        var act = () => Job(db, email, new FixedClock(Now), withAddress: false).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{AdminJobAlerts.AlertEmailKey}*");
        (await UndigestedAsync(db)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Kesintide_biriken_saatler_tek_kosuda_gider()
    {
        // Saatlerce süren kesinti ya da kuyruk gecikmesi: Hangfire işi bir kez koşar, aradaki saatler kaybolmamalı.
        using var db = NewDb(); var email = new TestEmailSender();
        var threeHoursAgo = Record(HourStart.AddHours(-3).AddMinutes(10), "Shoppers.FullName+Phone+Address+Email+TcProtected");
        var twoHoursAgo = Record(HourStart.AddHours(-2).AddMinutes(20), "PaymentMatches.WpfCustomerId+Evidence");
        var lastHour = Record(HourStart.AddMinutes(-5), "CustomerIbanMemories");
        db.DataDestructionRecords.AddRange(lastHour, twoHoursAgo, threeHoursAgo);
        await db.SaveChangesAsync();

        (await Job(db, email, new FixedClock(Now)).RunAsync(CancellationToken.None)).Should().Be(3);

        var body = email.Sent.Should().ContainSingle().Subject.PlainBody;
        body.Should().Contain(threeHoursAgo.Target).And.Contain(twoHoursAgo.Target).And.Contain(lastHour.Target);
        body.IndexOf(threeHoursAgo.Target, StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf(lastHour.Target, StringComparison.Ordinal), "satırlar zamana göre sıralı");
        (await UndigestedAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Cok_kayit_birden_cok_e_postaya_bolunur()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var total = DataDestructionDigestJob.MaxRowsPerEmail + 1;
        db.DataDestructionRecords.AddRange(Enumerable.Range(0, total)
            .Select(i => Record(HourStart.AddHours(-5).AddSeconds(i), "BankTransactions")));
        await db.SaveChangesAsync();

        (await Job(db, email, new FixedClock(Now)).RunAsync(CancellationToken.None)).Should().Be(total);

        email.Sent.Should().HaveCount(2);
        (await UndigestedAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public void Dis_kopyadaki_olcut_ve_hedef_tablodakiyle_birebir_aynidir()
    {
        // Geri yüklemede e-postadaki satır tabloyla birebir karşılaştırılabilmeli: CSV ayracı (';') ve satır sonu tabloya hiç
        // girmez (DataDestruction yazarken çevirir); ToCsv'nin kaçışlaması yalnız savunma.
        using var db = NewDb();
        DataDestruction.Record(db, null, "AuditLogs;x", DataDestruction.Actions.Deleted, 1, "a+24ay; b +36ay\nc",
            DataDestruction.Triggers.Retention, DataDestruction.SystemActor);
        DataDestruction.RecordRun(db, "test-job", "a+24ay;b\r\n+36ay");
        var rows = db.ChangeTracker.Entries<DataDestructionRecord>().Select(e => e.Entity).OrderBy(r => r.Target).ToList();

        rows.Should().OnlyContain(r => r.Criterion.IndexOfAny(new[] { ';', '\r', '\n' }) < 0
            && r.Target.IndexOfAny(new[] { ';', '\r', '\n' }) < 0);
        var lines = DataDestructionDigestJob.ToCsv(rows).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var header = lines[1].Split(';');
        var data = lines.Skip(2).Select(l => l.Split(';')).ToList();
        data.Should().HaveCount(2).And.OnlyContain(cols => cols.Length == header.Length);
        data.Select(c => (c[Array.IndexOf(header, "hedef")], c[Array.IndexOf(header, "olcut")]))
            .Should().Equal(rows.Select(r => (r.Target, r.Criterion)));
    }

    [Fact]
    public async Task Kayit_yoksa_ya_da_hepsi_gonderilmisse_e_posta_gitmez()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var clock = new FixedClock(Now);

        (await Job(db, email, clock).RunAsync(CancellationToken.None)).Should().Be(0);

        db.DataDestructionRecords.Add(Record(HourStart.AddMinutes(-30), "BankTransactions"));
        await db.SaveChangesAsync();
        (await Job(db, email, clock).RunAsync(CancellationToken.None)).Should().Be(1);
        (await Job(db, email, clock).RunAsync(CancellationToken.None)).Should().Be(0, "gönderilen kayıt yeniden gitmez");
        email.Sent.Should().ContainSingle();
    }
}
