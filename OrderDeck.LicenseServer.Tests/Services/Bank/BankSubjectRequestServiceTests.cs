using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK K-7: IBAN ile banka verisi talebi. IBAN düz saklanmaz; son tarih alış + 30 gün; dışa aktarımda müşteri
/// kimliği yok; silme kişinin bağlarını, hareket metinlerini ve elle eşleme denetimindeki kimliğini siler, talebi kapatır.
/// Talebin kapanışı tekildir (<c>ClosedAt</c> jetonu): aynı anda iki silme ya da kapatma ikinci kez kayıt yazmaz.</summary>
public sealed class BankSubjectRequestServiceTests
{
    private static readonly BankHasher Hasher = new(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-subject-{Guid.NewGuid():N}").Options);

    /// <summary>Kancalı bağlam + aynı InMemory veritabanına kancasız ikinci bağlam üreten fabrika: eşzamanlı yazanı (ikinci
    /// yönetici, çift tık) sıraya dayalı olarak taklit eder.</summary>
    private static (LicenseDbContext Db, Func<LicenseDbContext> Other) SharedDb(IInterceptor interceptor)
    {
        var name = $"bank-subject-{Guid.NewGuid():N}";
        var root = new InMemoryDatabaseRoot();
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, root)
            .AddInterceptors(interceptor).Options);
        return (db, () => new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, root)
            .Options));
    }

    /// <summary>Talep satırını değiştiren İLK kayıttan hemen önce bir kez <see cref="Before"/>'u koşturur (eşzamanlı yazan).</summary>
    private sealed class BeforeRequestSave : SaveChangesInterceptor
    {
        public Func<Task>? Before { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Before is { } before
                && eventData.Context!.ChangeTracker.Entries<BankSubjectRequest>().Any(e => e.State == EntityState.Modified))
            {
                Before = null;
                await before();
            }
            return result;
        }
    }

    /// <summary>Talep satırını değiştiren HER kayıtta eşzamanlı bir yazıyı taklit eder: yeniden deneme de çakışır.</summary>
    private sealed class RequestSaveConflict : SaveChangesInterceptor
    {
        public int Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<BankSubjectRequest>().Any(e => e.State == EntityState.Modified))
            {
                Thrown++;
                throw new DbUpdateConcurrencyException("eşzamanlı yazı (test)");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static Guid SeedLicense(LicenseDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"s-{Guid.NewGuid():N}@x", Name = "S",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        db.SaveChanges();
        return lic;
    }

    private static BankTransaction Tx(Guid lic, string iban, string description = "HAVALE ornek") => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 6,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 125.5m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow.AddDays(-3), FetchedAt = DateTimeOffset.UtcNow.AddDays(-3),
        Description = description, CounterpartyName = "Ad Soyad", RawJson = """{"Aciklama":"HAVALE ornek"}""",
        CounterpartyIbanHash = Hasher.HashIban(iban), CounterpartyIbanMasked = BankHasher.MaskIban(iban),
        CounterpartyIbanHashValid = true,
    };

    private static PaymentMatch Match(BankTransaction tx, Guid? actual = null, Guid? proposed = null, string? evidence = null,
        PaymentMatchStatus status = PaymentMatchStatus.ManualOnly) => new()
    {
        Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, ActualWpfCustomerId = actual,
        ProposedWpfCustomerId = proposed, Evidence = evidence, Status = status,
        DecidedAt = actual is null ? null : DateTimeOffset.UtcNow.AddDays(-2),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-3), UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2),
    };

    private static CustomerIbanMemory Memory(Guid lic, Guid customer, string iban) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = Hasher.HashIban(iban)!,
        IbanMasked = BankHasher.MaskIban(iban), LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Banka Eşleştirme sayfasının elle eşleme denetim satırı (Görev 16 biçimi).</summary>
    private static AuditLogEntry ManualMatchAudit(Guid txId, Guid wpfCustomerId) => new()
    {
        Id = Guid.NewGuid(), EventType = AuditEvents.BankMatchManual, TargetType = AuditTargets.BankTransaction,
        TargetId = txId.ToString(), Details = $"{{\"wpfCustomerId\":\"{wpfCustomerId}\"}}", OccurredAt = DateTimeOffset.UtcNow,
        AdminId = Guid.NewGuid(), AdminUsername = "admin",
    };

    private static BankSubjectRequestService Service(LicenseDbContext db) => new(db);

    /// <summary>Doğrulaması kayıtlı silme talebi.</summary>
    private static async Task<BankSubjectRequest> VerifiedErasureAsync(LicenseDbContext db, Guid lic, string iban)
    {
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Erasure, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);
        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);
        return r;
    }

    [Fact]
    public async Task Maskeli_IBAN_ile_talep_acilmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();

        var act = () => Service(db).CreateAsync(Hasher, lic, iban[..10] + "******" + iban[16..], BankSubjectRequestKind.Access,
            new DateOnly(2026, 10, 19), "admin", CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.InvalidIbanMessage);
    }

    [Fact]
    public async Task Talep_IBANi_duz_saklamaz_son_tarih_alistan_30_gun_sonra()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();

        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Erasure, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);

        r.IbanHash.Should().Be(Hasher.HashIban(iban));
        r.IbanMasked.Should().Be(BankHasher.MaskIban(iban));
        r.ReceivedAt.Should().Be(new DateTimeOffset(2026, 10, 18, 21, 0, 0, TimeSpan.Zero), "TR gün başı");
        r.DueAt.Should().Be(r.ReceivedAt.AddDays(30));
        var json = System.Text.Json.JsonSerializer.Serialize(db.Entry(r).CurrentValues.ToObject());
        json.Should().NotContain(iban[4..20], "IBAN düz hiçbir alanda yok");
    }

    [Fact]
    public async Task Alis_tarihi_ileride_olan_talep_acilmaz()
    {
        // Son tarih alıştan sayılır: ileri bir alış tarihi gecikmeyi gizlerdi.
        using var db = NewDb(); var lic = SeedLicense(db);
        var tomorrowTr = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ObifinConnectionService.TrZone)
            .DateTime).AddDays(1);

        var act = () => Service(db).CreateAsync(Hasher, lic, BankHasherTests.TestIban(), BankSubjectRequestKind.Access, tomorrowTr,
            "admin", CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.FutureReceivedMessage);
        (await db.BankSubjectRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Arama_ayni_lisanstaki_ayni_IBANi_bulur_baska_lisansinkini_bulmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var other = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var mine = Tx(lic, iban);
        db.BankTransactions.AddRange(mine, Tx(other, iban), Tx(lic, BankHasherTests.TestIban()));
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Access, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);

        var lookup = await Service(db).FindAsync(r.Id, CancellationToken.None);

        lookup!.Transactions.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task Disa_aktarim_tam_IBAN_ve_musteri_kimligi_icermez_formul_hucresi_kacislanir()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var customer = Guid.NewGuid();
        var tx = Tx(lic, iban, description: "=HYPERLINK(\"x\")");
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id,
            ActualWpfCustomerId = customer, Status = PaymentMatchStatus.ManualOnly, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Access, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);
        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);

        var csv = Encoding.UTF8.GetString(await Service(db).ExportAsync(r.Id, CancellationToken.None));

        csv.Should().Contain(BankHasher.MaskIban(iban)).And.Contain("125.50").And.Contain("bir müşteriyle eşleşti");
        csv.Should().NotContain(iban[4..20]).And.NotContain(customer.ToString());
        csv.Should().Contain("\"'=HYPERLINK(\"\"x\"\")\"", "formül enjeksiyonu kesme işaretiyle etkisiz");
    }

    [Fact]
    public async Task Dogrulama_kaydedilmeden_disa_aktarma_ve_silme_yapilmaz_kayit_kisisel_veri_icermez()
    {
        // KVKK Ö-11/K-7: başkasının havale bilgisini açıklamamanın ve yanlış kişinin talebiyle silmemenin ispatı veri
        // sorumlusunda: yöntem kaydı zorunlu, serbest metin yok.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        db.BankTransactions.Add(Tx(lic, iban));
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Erasure, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);
        // Dışa aktarım yalnız erişim talebinde: doğrulama denetimi orada sınanır.
        var access = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Access, new DateOnly(2026, 10, 19),
            "admin", CancellationToken.None);

        var export = () => Service(db).ExportAsync(access.Id, CancellationToken.None);
        var erase = () => Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);
        var noMethod = () => Service(db).VerifyAsync(r.Id, BankSubjectVerification.None, "admin", CancellationToken.None);

        await export.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.NotVerifiedMessage);
        await erase.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.NotVerifiedMessage);
        await noMethod.Should().ThrowAsync<ObifinValidationException>();
        (await db.BankTransactions.AsNoTracking().SingleAsync()).Description.Should().NotBeNull("silme yapılmadı");

        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.ElectronicSignatureAndBankDocument, "admin", CancellationToken.None);

        var verified = await db.BankSubjectRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id);
        (verified.Verification, verified.VerifiedBy).Should().Be((BankSubjectVerification.ElectronicSignatureAndBankDocument, "admin"));
        verified.VerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Silme_metni_ve_IBAN_bagini_siler_talebi_kapatir_ozeti_bosaltir_imha_kaydi_yazar()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var customer = Guid.NewGuid();
        var tx = Tx(lic, iban);
        db.BankTransactions.Add(tx);
        var match = new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, ActualWpfCustomerId = customer,
            ProposedWpfCustomerId = customer, Status = PaymentMatchStatus.ConfirmedByHuman, Evidence = "iban-memory",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.PaymentMatches.Add(match);
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer,
            IbanHash = Hasher.HashIban(iban)!, IbanMasked = BankHasher.MaskIban(iban), LearnedFrom = IbanMemorySource.HumanApproval,
            CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Erasure, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);
        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        (result.Transactions, result.IbanMemories, result.Matches).Should().Be((1, 1, 1));
        var t = await db.BankTransactions.AsNoTracking().SingleAsync();
        t.Description.Should().BeNull(); t.CounterpartyName.Should().BeNull(); t.RawJson.Should().BeNull();
        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.ActualWpfCustomerId.Should().BeNull(); m.ProposedWpfCustomerId.Should().BeNull(); m.Evidence.Should().BeNull();
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
        var closed = await db.BankSubjectRequests.AsNoTracking().SingleAsync();
        closed.ClosedAt.Should().NotBeNull(); closed.IbanHash.Should().BeNull(); closed.ClosedBy.Should().Be("admin");
        closed.Resolution.Should().Be(BankSubjectResolution.Erased);
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == r.Id).ToListAsync();
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.SubjectRequest).And.HaveCount(4,
            "IBAN bağı, hareket metni, eşleşme ve kapanışta boşalan talep özeti");
        // Özet takma adlı kişisel veri; talep kapanınca amacı biter (Silme Yönetmeliği md. 7/1): boşaltması da kayda girer.
        records.Should().ContainSingle(x => x.Target == BankSubjectRequestService.IbanHashTarget).Which.Should().BeEquivalentTo(new
        {
            Action = DataDestruction.Actions.Nulled, RowCount = 1, LicenseId = (Guid?)lic, Actor = "admin",
            Criterion = BankSubjectRequestService.ClosedCriterion(BankSubjectResolution.Erased),
        });

        var again = () => Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);
        await again.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.ClosedMessage);
    }

    [Fact]
    public async Task Havaleye_bagli_dekont_kaydi_aramada_sayilir_disa_aktarimda_ve_silme_sonucunda_anilir()
    {
        // KVKK Ö-3, K-7: müşteri adına ödeyen üçüncü kişinin adı ve açık IBAN'ı harekete bağlı dekontta da durur (Payments.PayerName,
        // dekont dosyası). Araç o kopyayı silmez ve dışa aktarmaz; yönetici bunu görmeli ve başvurana gerekçesiyle bildirmeli
        // (Silme Yönetmeliği md. 12/1-c kısmi ret; gerekçe avukat kararı, plan karar 33). Sayı kişisel veri değil.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var paid = Tx(lic, iban); var unpaid = Tx(lic, iban);
        db.BankTransactions.AddRange(paid, unpaid);
        var receipt = Match(paid, actual: Guid.NewGuid());
        receipt.PaymentId = Guid.NewGuid();
        db.PaymentMatches.AddRange(receipt, Match(unpaid));
        await db.SaveChangesAsync();
        var access = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Access, new DateOnly(2026, 10, 19),
            "admin", CancellationToken.None);
        await Service(db).VerifyAsync(access.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);

        (await Service(db).FindAsync(access.Id, CancellationToken.None))!.LinkedReceipts.Should().Be(1);
        var csv = Encoding.UTF8.GetString(await Service(db).ExportAsync(access.Id, CancellationToken.None));
        csv.Should().Contain("# Bu havalelere bağlı dekont kaydı sayısı: 1");

        var erasure = await VerifiedErasureAsync(db, lic, iban);
        var result = await Service(db).EraseAsync(erasure.Id, "admin", CancellationToken.None);

        result.LinkedReceipts.Should().Be(1);
        (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == receipt.Id)).PaymentId.Should().Be(receipt.PaymentId,
            "dekont bağı silinmez");
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync(x => x.Id == erasure.Id)).Outcome
            .Should().Contain("1 bağlı dekont kaydı");
        BankSubjectRequestService.OtherCopiesNotice.Should().Contain("dekont");
    }

    [Fact]
    public async Task Silme_IBAN_bagi_olmayan_kararin_musterisini_birakir_yalniz_kaniti_siler_kayitlar_ikiye_ayrilir()
    {
        // Karar 7: IBAN hafızası yoksa havaleyi gönderen ile ödemesi sayılan müşteri farklı kişi olabilir; karar ve müşteri
        // kalır, kanıt ve hareket metni silinir. Kayıt kişiye bağlı satırı yalnız kanıtı boşalandan ayırır (Görev 15 kuralı).
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var subject = Guid.NewGuid(); var other = Guid.NewGuid();
        var linkedTx = Tx(lic, iban); var otherTx = Tx(lic, iban);
        db.BankTransactions.AddRange(linkedTx, otherTx);
        db.PaymentMatches.AddRange(Match(linkedTx, actual: subject, evidence: "iban-memory"),
            Match(otherTx, actual: other, proposed: other, evidence: "name-amount", status: PaymentMatchStatus.ConfirmedByHuman));
        db.CustomerIbanMemories.Add(Memory(lic, subject, iban));
        await db.SaveChangesAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var matches = await db.PaymentMatches.AsNoTracking().ToDictionaryAsync(m => m.BankTransactionId);
        matches[linkedTx.Id].ActualWpfCustomerId.Should().BeNull();
        matches[otherTx.Id].Should().Match<PaymentMatch>(m => m.ActualWpfCustomerId == other && m.ProposedWpfCustomerId == other
            && m.Status == PaymentMatchStatus.ConfirmedByHuman && m.Evidence == null, "IBAN bağı olmayan müşterinin kararı kalır");
        (result.Transactions, result.Matches).Should().Be((2, 2));
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == r.Id)
            .ToDictionaryAsync(x => x.Target, x => (x.Action, x.RowCount, x.LicenseId, x.Criterion, x.Actor));
        records.Should().BeEquivalentTo(new Dictionary<string, (string, int, Guid?, string, string)>
        {
            [BankSubjectScrub.IbanMemoryTarget] = (DataDestruction.Actions.Deleted, 1, lic, BankSubjectRequestService.EraseCriterion, "admin"),
            [BankSubjectScrub.TransactionTextTarget] = (DataDestruction.Actions.Nulled, 2, lic, BankSubjectRequestService.EraseCriterion, "admin"),
            [BankSubjectScrub.LinkedMatchTarget] = (DataDestruction.Actions.Nulled, 1, lic, BankSubjectRequestService.EraseCriterion, "admin"),
            [BankSubjectScrub.MatchEvidenceTarget] = (DataDestruction.Actions.Nulled, 1, lic, BankSubjectRequestService.EraseCriterion, "admin"),
            [BankSubjectRequestService.IbanHashTarget] = (DataDestruction.Actions.Nulled, 1, lic,
                BankSubjectRequestService.ClosedCriterion(BankSubjectResolution.Erased), "admin"),
        });
    }

    [Fact]
    public async Task Silme_saklama_kuralinin_bosalttigi_hareketi_ve_kanitsiz_eslesmeyi_saymaz_ona_yazmaz()
    {
        // Görev 15 kuralı: yalnız gerçekten silineni say. Saklama işi açıklamayı çoktan boşaltmışsa (ve kanıtı da) hareket
        // "silindi" diye sayılmaz, eşleşme satırına boşuna yazılmaz (jeton ilerlemez).
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var purged = Tx(lic, iban);
        purged.Description = null; purged.CounterpartyName = null; purged.RawJson = null;
        purged.DescriptionPurgedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var withText = Tx(lic, iban);
        db.BankTransactions.AddRange(purged, withText);
        var silentMatch = Match(purged, status: PaymentMatchStatus.NoProposal);
        db.PaymentMatches.AddRange(silentMatch, Match(withText, evidence: "username=ornek", status: PaymentMatchStatus.NoProposal));
        await db.SaveChangesAsync();
        var untouchedStamp = (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == silentMatch.Id)).UpdatedAt;
        var r = await VerifiedErasureAsync(db, lic, iban);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        (result.Transactions, result.IbanMemories, result.Matches, result.ManualMatchAudits).Should().Be((1, 0, 1, 0));
        (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == silentMatch.Id)).UpdatedAt.Should().Be(untouchedStamp);
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == purged.Id)).DescriptionPurgedAt
            .Should().Be(purged.DescriptionPurgedAt, "ilk silmenin anı kalır");
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == r.Id).Select(x => new { x.Target, x.RowCount })
                .ToListAsync())
            .Should().BeEquivalentTo(new[]
            {
                new { Target = BankSubjectScrub.TransactionTextTarget, RowCount = 1 },
                new { Target = BankSubjectScrub.MatchEvidenceTarget, RowCount = 1 },
                new { Target = BankSubjectRequestService.IbanHashTarget, RowCount = 1 },
            });
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync()).Outcome.Should().StartWith("Silindi: 1 hareketin");
    }

    [Fact]
    public async Task Silme_elle_esleme_denetiminde_kararin_musterisini_ve_kopyasini_bosaltir_baska_satira_dokunmaz()
    {
        // Görev 16 kuralı 1 (KVKK Ö-14, K-7): silme kararın müşterisini boşaltıyorsa o havalenin elle eşleme denetim satırı da
        // müşteriyi göstermesin, yoksa "bu kişi bu havaleyi ödedi" bağı 24 ay denetimde yaşardı. Elle eşleme o günkü asıl kaydı
        // yazar: kayıt sonradan birleştirildiyse kopyanın kimliği de kişinindir. Kişinin başka havalesinin satırı (talep o
        // IBAN'ın verisidir), başka müşterinin satırı ve IBAN bağı olmayan kararın satırı kalır.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var subject = Guid.NewGuid(); var copy = Guid.NewGuid(); var stranger = Guid.NewGuid(); var unbound = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = copy, LicenseId = lic, Platform = "instagram",
            Username = "ornek_m", MergedIntoId = subject, UpdatedAt = DateTimeOffset.UtcNow.AddDays(-5) });
        var decided = Tx(lic, iban); var unboundTx = Tx(lic, iban); var elsewhere = Tx(lic, BankHasherTests.TestIban());
        db.BankTransactions.AddRange(decided, unboundTx, elsewhere);
        db.PaymentMatches.AddRange(Match(decided, actual: subject), Match(unboundTx, actual: unbound), Match(elsewhere, actual: subject));
        db.CustomerIbanMemories.Add(Memory(lic, subject, iban));
        var ofSubject = ManualMatchAudit(decided.Id, subject);
        var ofCopy = ManualMatchAudit(decided.Id, copy);
        var ofStranger = ManualMatchAudit(decided.Id, stranger);
        var ofUnbound = ManualMatchAudit(unboundTx.Id, unbound);
        var ofElsewhere = ManualMatchAudit(elsewhere.Id, subject);
        db.AuditLogs.AddRange(ofSubject, ofCopy, ofStranger, ofUnbound, ofElsewhere);
        await db.SaveChangesAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var audits = await db.AuditLogs.AsNoTracking().ToDictionaryAsync(a => a.Id);
        audits[ofSubject.Id].Details.Should().BeNull();
        audits[ofSubject.Id].TargetId.Should().Be(decided.Id.ToString(), "hangi havalenin elle eşlendiği kalır");
        audits[ofCopy.Id].Details.Should().BeNull("birleştirilmiş kopyanın kimliği de kişinindir");
        audits[ofStranger.Id].Details.Should().Be(ofStranger.Details, "başka müşterinin satırı kalır");
        audits[ofUnbound.Id].Details.Should().Be(ofUnbound.Details, "IBAN bağı olmayan kararın müşterisi silinmedi");
        audits[ofElsewhere.Id].Details.Should().Be(ofElsewhere.Details, "talep yalnız bu IBAN'ın havalelerini kapsar");
        result.ManualMatchAudits.Should().Be(2);
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.Target == BankSubjectScrub.ManualMatchAuditTarget).ToListAsync())
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                LicenseId = (Guid?)lic, Action = DataDestruction.Actions.Nulled, RowCount = 2,
                Trigger = DataDestruction.Triggers.SubjectRequest, Criterion = BankSubjectRequestService.EraseCriterion,
                RequestId = (Guid?)r.Id, Actor = "admin",
            });
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync()).Outcome.Should().Contain("2 elle eşleme denetim satırının");
    }

    // ── S2b planı Karar 7 (sahip kararı 27.11.2026): kişi düzeyi eşleştirici bağı kişinin öbür satırına ve kopyadaki bağı asıl
    //    kayda sayar. Talebin silmesi bu IBAN'dan türeyen öneriyi de boşaltır (kararsız ve kararlı satırda; karar katmanı
    //    hariç); talep özetin bütün bağlarını siler.

    private static PaymentMatcher Matcher(LicenseDbContext db)
        => new(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance);

    /// <summary>Ölçümün iki kutusu (güncel kişi düzeyi ve satır düzeyi eski): kararlı satırın önerisini boşaltan silme ikisini de
    /// değiştirmemeli.</summary>
    private static async Task<(PaymentMatchSummary Current, PaymentMatchSummary Legacy)> MetricsAsync(LicenseDbContext db, Guid lic)
    {
        var metrics = new PaymentMatchMetrics(db, Matcher(db));
        return (await metrics.ComputeAsync(lic, 30, PaymentMatcher.PersonLevelVersion, CancellationToken.None),
            await metrics.ComputeAsync(lic, 30, null, CancellationToken.None));
    }

    private static WpfCustomerProjection Projection(LicenseDbContext db, Guid lic, string platform, string username,
        string? groupId = null, Guid? mergedInto = null)
    {
        var row = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = lic, Platform = platform, Username = username, GroupId = groupId,
            MergedIntoId = mergedInto, UpdatedAt = DateTimeOffset.UtcNow.AddDays(-5),
        };
        db.WpfCustomerProjections.Add(row);
        return row;
    }

    /// <summary>Eşleştiricinin hareket için yazdığı satır; silme ayrı istekte koşsun diye izleyici boşaltılır.</summary>
    private static async Task<PaymentMatch> MatchedAsync(LicenseDbContext db, BankTransaction tx)
    {
        var row = await Matcher(db).MatchAsync(tx, CancellationToken.None);
        db.ChangeTracker.Clear();
        return row;
    }

    [Fact]
    public async Task Silme_kisinin_obur_satirina_dogan_IBAN_destekli_kullanici_adi_onerisini_de_bosaltir()
    {
        // (i) Bağ kişinin TT satırında, açıklamadaki kullanıcı adı aynı kişinin IG satırının: v2 önerisi IG, IBAN destekli. Müşteri
        // kümesi bağların Id'leridir (TT); satır onu göstermediği için yalnız kanıtı boşalır, öneri "IBAN destekli" kalırdı. Talep
        // özetin bütün hareketlerinin açıklamasını siler: satır yeniden hesaplanamaz, öneri yok kalır.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var group = $"G-{Guid.NewGuid():N}";
        var ig = Projection(db, lic, "instagram", "ornek_musteri34", group);
        var tt = Projection(db, lic, "tiktok", "baska_hesap77", group);
        db.CustomerIbanMemories.Add(Memory(lic, tt.Id, iban));
        var tx = Tx(lic, iban, description: "HAVALE ornek_musteri34");
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        var before = await MatchedAsync(db, tx);
        (before.Status, before.ProposedWpfCustomerId, before.Layer, before.SupportedByIbanMemory, before.Evidence).Should().Be(
            (PaymentMatchStatus.Proposed, (Guid?)ig.Id, PaymentMatchLayer.UsernameInDescription, true,
                "username=ornekmusteri34;iban-memory"), "ön koşul: öneri bağın kişisinin öbür satırına");
        var r = await VerifiedErasureAsync(db, lic, iban);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.SupportedByIbanMemory, m.Evidence).Should().Be(
            (PaymentMatchStatus.NoProposal, (Guid?)null, PaymentMatchLayer.None, 0m, false, (string?)null),
            "bağdan türeyen öneri bağla birlikte gider, kanıtta 'iban-memory' kalmaz");
        m.MatcherVersion.Should().Be(PaymentMatcher.PersonLevelVersion, "silme yeniden hesap değildir, sürüm kalır");
        (await db.BankTransactions.AsNoTracking().SingleAsync()).DescriptionPurgedAt.Should().NotBeNull(
            "açıklama silindi: kullanıcı adı önerisi IBAN'sız yeniden hesaplanamaz");
        result.Matches.Should().Be(1);
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == r.Id).ToDictionaryAsync(x => x.Target, x => x.RowCount))
            .Should().BeEquivalentTo(new Dictionary<string, int>
            {
                [BankSubjectScrub.IbanMemoryTarget] = 1, [BankSubjectScrub.TransactionTextTarget] = 1,
                [BankSubjectScrub.LinkedMatchTarget] = 1, [BankSubjectRequestService.IbanHashTarget] = 1,
            }, "önerisi boşalan satır kimlik ve kanıt kaydında, bir kez sayılır");
    }

    [Fact]
    public async Task Silme_kopyadaki_bagin_asil_kayda_dogurdugu_IBAN_onerisini_de_bosaltir()
    {
        // (ii) Bağ birleştirilmiş kopyada (K → D) kalmış: okuyucu onu asıl kayda sayar, v2 önerisi D (IBAN hafızası). Müşteri
        // kümesi bağın Id'si (K) olduğu için satır yalnız kanıtı boşalan satır sayılır, öneri D'de kalırdı.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var d = Projection(db, lic, "tiktok", "ornek_musteri34");
        var k = Projection(db, lic, "tiktok", "eski_hesap55", mergedInto: d.Id);
        db.CustomerIbanMemories.Add(Memory(lic, k.Id, iban));
        var tx = Tx(lic, iban, description: "EFT GELEN aciklamasiz");
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        var before = await MatchedAsync(db, tx);
        (before.ProposedWpfCustomerId, before.Layer, before.SupportedByIbanMemory).Should().Be(
            ((Guid?)d.Id, PaymentMatchLayer.IbanMemory, true), "ön koşul: kopyadaki bağ asıl kayda sayıldı");
        var r = await VerifiedErasureAsync(db, lic, iban);

        await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.SupportedByIbanMemory, m.Evidence).Should().Be(
            (PaymentMatchStatus.NoProposal, (Guid?)null, PaymentMatchLayer.None, 0m, false, (string?)null));
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == r.Id).Select(x => x.Target).ToListAsync())
            .Should().Contain(BankSubjectScrub.LinkedMatchTarget).And.NotContain(BankSubjectScrub.MatchEvidenceTarget);
    }

    [Fact]
    public async Task Silme_IBAN_izi_olmayan_kullanici_adi_onerisinin_yalniz_kanitini_bosaltir()
    {
        // (koruma) Öneri bağdan türemiyorsa (katman kullanıcı adı, IBAN desteği yok, kanıtta "iban-memory" yok) bugünkü kural
        // kalır: gönderen ile önerilen müşteri farklı kişi olabilir (karar 7), yalnız kanıt boşalır.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var proposed = Guid.NewGuid();
        var tx = Tx(lic, iban);
        db.BankTransactions.Add(tx);
        var row = Match(tx, proposed: proposed, evidence: "username=ornekmusteri34", status: PaymentMatchStatus.Proposed);
        row.Layer = PaymentMatchLayer.UsernameInDescription;
        row.Confidence = PaymentMatcher.UsernameConfidence;
        row.MatcherVersion = PaymentMatcher.PersonLevelVersion;
        db.PaymentMatches.Add(row);
        db.CustomerIbanMemories.Add(Memory(lic, Guid.NewGuid(), iban));
        await db.SaveChangesAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);

        await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.Evidence).Should().Be((PaymentMatchStatus.Proposed,
            (Guid?)proposed, PaymentMatchLayer.UsernameInDescription, PaymentMatcher.UsernameConfidence, (string?)null));
    }

    [Fact]
    public async Task Silme_IBAN_izini_kanitin_okundugu_halinden_okur_bagi_once_silinmis_oneriyi_de_bosaltir()
    {
        // Satır düzeyi eşleştiricinin (S2b öncesi: sürüm boş, IBAN desteği hep false) "kullanıcı adı + IBAN" satırı: öneri X, 0,98,
        // kanıt "username=…;iban-memory". X'in bağı sonradan saklamayla silinmiş, satır yeniden hesaplanmamış. IBAN izi yalnız
        // kanıttadır ve kimlik kuralı (özette bağ yok: yalnız kanıt) kanıtı önce boşaltır. İz okunduğu hâlinden okunmasaydı öneri
        // "IBAN destekli" güveniyle kalırdı; shopper silmesi aynı satırı aynı kuralla boşaltır.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var tx = Tx(lic, iban, description: "HAVALE ornek_musteri34");
        db.BankTransactions.Add(tx);
        var row = Match(tx, proposed: Guid.NewGuid(), evidence: "username=ornekmusteri34;iban-memory",
            status: PaymentMatchStatus.Proposed);
        row.Layer = PaymentMatchLayer.UsernameInDescription;
        row.Confidence = PaymentMatcher.BothConfidence;
        db.PaymentMatches.Add(row);
        await db.SaveChangesAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.SupportedByIbanMemory, m.Evidence, m.MatcherVersion).Should().Be(
            (PaymentMatchStatus.NoProposal, (Guid?)null, PaymentMatchLayer.None, 0m, false, (string?)null, (int?)null),
            "bağdan türeyen öneri gider, sürüm kalır");
        result.Matches.Should().Be(1);
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == r.Id).Select(x => x.Target).ToListAsync())
            .Should().Contain(BankSubjectScrub.LinkedMatchTarget).And.NotContain(BankSubjectScrub.MatchEvidenceTarget,
                "önerisi boşalan satır kimlik ve kanıt kaydında, bir kez");
    }

    [Theory]
    // S2b öncesi IBAN hafızası satırı (sürüm boş, IBAN desteği false): iz yalnız katmanda.
    [InlineData(PaymentMatchLayer.IbanMemory, false)]
    // v2 IBAN destekli kullanıcı adı önerisi: iz yalnız IBAN desteğinde.
    [InlineData(PaymentMatchLayer.UsernameInDescription, true)]
    public async Task Silme_IBAN_izini_katmandan_ve_destekten_de_okur(PaymentMatchLayer layer, bool supported)
    {
        // (koruma) Kanıtı saklama 30. günde açıklamayla birlikte boşaltmış satırın izi yalnız katmanda ya da IBAN desteğindedir.
        // Önerinin bağı sonradan silinmiş, özetin bağı artık başka müşterinin: öneri o bağın müşterisine gitmiyor, boşalır.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var tx = Tx(lic, iban);
        (tx.Description, tx.CounterpartyName, tx.RawJson) = (null, null, null);
        tx.DescriptionPurgedAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.BankTransactions.Add(tx);
        var version = supported ? PaymentMatcher.PersonLevelVersion : (int?)null;
        var row = Match(tx, proposed: Guid.NewGuid(), status: PaymentMatchStatus.Proposed);
        row.Layer = layer;
        row.Confidence = supported ? PaymentMatcher.BothConfidence : PaymentMatcher.IbanConfidence;
        row.SupportedByIbanMemory = supported;
        row.MatcherVersion = version;
        db.PaymentMatches.Add(row);
        db.CustomerIbanMemories.Add(Memory(lic, Guid.NewGuid(), iban));
        await db.SaveChangesAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);

        await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.SupportedByIbanMemory, m.MatcherVersion).Should().Be(
            (PaymentMatchStatus.NoProposal, (Guid?)null, PaymentMatchLayer.None, 0m, false, version));
        (await db.DataDestructionRecords.AsNoTracking().SingleAsync(x => x.RequestId == r.Id
            && x.Target == BankSubjectScrub.LinkedMatchTarget)).RowCount.Should().Be(1);
    }

    [Fact]
    public async Task Silme_kararinin_musterisi_bosalan_satirin_IBAN_destekli_onerisini_de_bosaltir_karar_olcumde_kalir()
    {
        // Kimlik kuralı kararın müşterisi bağın müşterisiyse onu boşaltır; durum, katman, güven, karar anı ve dekont bağı ölçüm
        // için kalır. Önerisi bağın kişisinin öbür satırına giden IBAN destekli öneriydi (S2b'den önce satır düzeyinde "çelişki,
        // öneri yok" olurdu): kararın müşterisi gidince havaleyi kişiye bağlayan tek iz o kalırdı, o da boşalır. Öneriden farklı
        // müşteriye elle verilmiş karar çelişki olarak kalır (kimlik kuralının kararlı kolunun eşi).
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var bonded = Guid.NewGuid(); var sibling = Guid.NewGuid();
        var linkedTx = Tx(lic, iban); var manualTx = Tx(lic, iban);
        db.BankTransactions.AddRange(linkedTx, manualTx);
        var linked = Match(linkedTx, actual: bonded, proposed: sibling, evidence: "username=ornekmusteri34;iban-memory",
            status: PaymentMatchStatus.ConfirmedByHuman);
        linked.Layer = PaymentMatchLayer.UsernameInDescription;
        linked.Confidence = PaymentMatcher.BothConfidence;
        linked.PaymentId = Guid.NewGuid();
        var manual = Match(manualTx, actual: bonded, proposed: sibling, evidence: "iban-memory", status: PaymentMatchStatus.ManualOnly);
        manual.Layer = PaymentMatchLayer.IbanMemory;
        manual.Confidence = PaymentMatcher.IbanConfidence;
        foreach (var row in new[] { linked, manual })
        {
            row.SupportedByIbanMemory = true;
            row.MatcherVersion = PaymentMatcher.PersonLevelVersion;
        }
        db.PaymentMatches.AddRange(linked, manual);
        db.CustomerIbanMemories.Add(Memory(lic, bonded, iban));
        await db.SaveChangesAsync();
        var before = await db.PaymentMatches.AsNoTracking().ToDictionaryAsync(m => m.Id);
        var r = await VerifiedErasureAsync(db, lic, iban);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var after = await db.PaymentMatches.AsNoTracking().ToDictionaryAsync(m => m.Id);
        foreach (var (id, status) in new[] { (linked.Id, PaymentMatchStatus.ConfirmedByHuman), (manual.Id, PaymentMatchStatus.Contradicted) })
        {
            var (m, b) = (after[id], before[id]);
            (m.ActualWpfCustomerId, m.ProposedWpfCustomerId, m.SupportedByIbanMemory, m.Evidence).Should().Be(
                ((Guid?)null, (Guid?)null, false, (string?)null), "kararın müşterisi de ondan türeyen öneri de gider");
            (m.Status, m.Layer, m.Confidence, m.DecidedAt, m.PaymentId, m.MatcherVersion).Should().Be(
                (status, b.Layer, b.Confidence, b.DecidedAt, b.PaymentId, b.MatcherVersion), "ölçüm durumdan ve katmandan okunur");
        }
        result.Matches.Should().Be(2);
        (await db.DataDestructionRecords.AsNoTracking().SingleAsync(x => x.RequestId == r.Id
            && x.Target == BankSubjectScrub.LinkedMatchTarget)).RowCount.Should().Be(2, "satır bir kez sayılır");
    }

    [Fact]
    public async Task Silme_kisinin_obur_satirina_kararli_satirin_IBAN_destekli_onerisini_de_bosaltir_karar_ve_olcum_kalir()
    {
        // (i) durumunda kişinin dekontu havaleyi IG satırına bağlamış (öneriyi doğrulayan karar). Bağ TT'de: kararın müşterisi
        // silinmez, kimlik kuralı satırın yalnız kanıtını boşaltır, IG'ye giden IBAN destekli öneri silinen bağın izi olarak
        // kalırdı. Sahip kararı (27.11.2026) bunu da kapsar: öneri ve IBAN desteği de gider; gerçek müşteri, durum, katman, güven,
        // karar anı, dekont bağı ve IBAN doğrulama işareti kalır, ölçümün iki kutusu silmeden önceki gibidir. İşaret dolu
        // tohumlanır: boş olsaydı onu boşaltan bir kural da aynı satırı bırakırdı.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var group = $"G-{Guid.NewGuid():N}";
        var ig = Projection(db, lic, "instagram", "ornek_musteri34", group);
        var tt = Projection(db, lic, "tiktok", "baska_hesap77", group);
        db.CustomerIbanMemories.Add(Memory(lic, tt.Id, iban));
        var tx = Tx(lic, iban, description: "HAVALE ornek_musteri34");
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        var row = await Matcher(db).MatchAsync(tx, CancellationToken.None);
        (row.ProposedWpfCustomerId, row.SupportedByIbanMemory, row.Evidence).Should().Be(
            ((Guid?)ig.Id, true, "username=ornekmusteri34;iban-memory"), "ön koşul: öneri bağın kişisinin öbür satırına");
        (row.ActualWpfCustomerId, row.Status, row.DecidedAt, row.PaymentId, row.IbanConfirmedAt) =
            (ig.Id, PaymentMatchStatus.ConfirmedByHuman, DateTimeOffset.UtcNow.AddHours(-1), Guid.NewGuid(), tx.OccurredAt);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.PaymentMatches.AsNoTracking().SingleAsync();
        var metricsBefore = await MetricsAsync(db, lic);
        var r = await VerifiedErasureAsync(db, lic, iban);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (m.ProposedWpfCustomerId, m.SupportedByIbanMemory, m.Evidence).Should().Be(((Guid?)null, false, (string?)null),
            "silinen bağdan türeyen öneri yaşayan kararda da gider");
        (m.ActualWpfCustomerId, m.IbanConfirmedAt).Should().Be(((Guid?)ig.Id, (DateTimeOffset?)tx.OccurredAt),
            "kararı boşaltmayan yol IBAN doğrulama işaretini de bırakır");
        m.Should().BeEquivalentTo(before, o => o.Excluding(x => x.ProposedWpfCustomerId).Excluding(x => x.SupportedByIbanMemory)
            .Excluding(x => x.Evidence).Excluding(x => x.UpdatedAt),
            "gerçek müşteri, durum, karar anı, dekont bağı ve IBAN doğrulama işareti kalır");
        var metricsAfter = await MetricsAsync(db, lic);
        metricsAfter.Current.Should().BeEquivalentTo(metricsBefore.Current, "ölçüm durumdan, katmandan ve gerçek müşteriden okunur");
        metricsAfter.Legacy.Should().BeEquivalentTo(metricsBefore.Legacy);
        result.Matches.Should().Be(1);
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == r.Id).ToDictionaryAsync(x => x.Target, x => x.RowCount))
            .Should().BeEquivalentTo(new Dictionary<string, int>
            {
                [BankSubjectScrub.IbanMemoryTarget] = 1, [BankSubjectScrub.TransactionTextTarget] = 1,
                [BankSubjectScrub.LinkedMatchTarget] = 1, [BankSubjectRequestService.IbanHashTarget] = 1,
            }, "önerisi boşalan satır kimlik ve kanıt kaydında, bir kez sayılır");
    }

    [Fact]
    public async Task Silme_baska_kayda_kararli_satirin_IBAN_destekli_onerisini_de_bosaltir_durum_ve_olcum_kalir()
    {
        // Kararlı kol bağı olmayan başka bir kayda verilmiş kararda da aynıdır: v2 satırında dekont öneriyle çelişmiş, S2b öncesi
        // (sürüm boş) satırda öneriden farklı müşteriye elle eşleme var (öneri o zamanki bağın müşterisine). İkisinde de öneri
        // gider; çelişki çelişki kalır, elle eşleme + öneri çelişkiye çevrilir (ölçüm ikisini aynı sayar). İki kutu aynı kalır.
        // Dekontlu kararın IBAN doğrulama işareti (kararın müşterisinin o zamanki bağı) dolu tohumlanır, kalır.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var bonded = Guid.NewGuid(); var other = Guid.NewGuid();
        var contradictedTx = Tx(lic, iban); var manualTx = Tx(lic, iban);
        db.BankTransactions.AddRange(contradictedTx, manualTx);
        var contradicted = Match(contradictedTx, actual: other, proposed: Guid.NewGuid(),
            evidence: "username=ornekmusteri34;iban-memory", status: PaymentMatchStatus.Contradicted);
        contradicted.Layer = PaymentMatchLayer.UsernameInDescription;
        contradicted.Confidence = PaymentMatcher.BothConfidence;
        contradicted.SupportedByIbanMemory = true;
        contradicted.MatcherVersion = PaymentMatcher.PersonLevelVersion;
        contradicted.PaymentId = Guid.NewGuid();
        contradicted.IbanConfirmedAt = contradictedTx.OccurredAt;
        var manual = Match(manualTx, actual: other, proposed: Guid.NewGuid(), evidence: "iban-memory",
            status: PaymentMatchStatus.ManualOnly);
        manual.Layer = PaymentMatchLayer.IbanMemory;
        manual.Confidence = PaymentMatcher.IbanConfidence;
        db.PaymentMatches.AddRange(contradicted, manual);
        db.CustomerIbanMemories.Add(Memory(lic, bonded, iban));
        await db.SaveChangesAsync();
        var before = await db.PaymentMatches.AsNoTracking().ToDictionaryAsync(m => m.Id);
        var metricsBefore = await MetricsAsync(db, lic);
        var r = await VerifiedErasureAsync(db, lic, iban);

        await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var after = await db.PaymentMatches.AsNoTracking().ToDictionaryAsync(m => m.Id);
        foreach (var id in new[] { contradicted.Id, manual.Id })
        {
            (after[id].ProposedWpfCustomerId, after[id].SupportedByIbanMemory, after[id].Evidence, after[id].Status).Should().Be(
                ((Guid?)null, false, (string?)null, PaymentMatchStatus.Contradicted), "öneri gider, öneriden farklı karar çelişkidir");
            after[id].Should().BeEquivalentTo(before[id], o => o.Excluding(x => x.ProposedWpfCustomerId)
                .Excluding(x => x.SupportedByIbanMemory).Excluding(x => x.Evidence).Excluding(x => x.Status)
                .Excluding(x => x.UpdatedAt),
                "gerçek müşteri, katman, güven, karar anı, dekont bağı, IBAN doğrulama işareti ve sürüm kalır");
        }
        after[contradicted.Id].IbanConfirmedAt.Should().Be(contradictedTx.OccurredAt);
        var metricsAfter = await MetricsAsync(db, lic);
        metricsAfter.Current.Should().BeEquivalentTo(metricsBefore.Current);
        metricsAfter.Legacy.Should().BeEquivalentTo(metricsBefore.Legacy, "elle eşleme + öneri de çelişki sayılıyordu");
    }

    [Fact]
    public async Task Silme_karar_katmanindaki_satirin_IBAN_destekli_onerisine_dokunmaz()
    {
        // (koruma) Kural karar katmanına dokunmaz (S2b planı Karar 7): o sorunun IBAN destekli önerisi yayıncının önündedir, silmede
        // ne olacağı S3a'da kararlaştırılır. Kimlik kuralı satırın yalnız kanıtını boşaltır (karar 7).
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var askedTx = Tx(lic, iban);
        db.BankTransactions.Add(askedTx);
        var asked = Match(askedTx, proposed: Guid.NewGuid(), evidence: "iban-memory", status: PaymentMatchStatus.Proposed);
        asked.Layer = PaymentMatchLayer.IbanMemory;
        asked.Confidence = PaymentMatcher.IbanConfidence;
        asked.Decision = PaymentMatchDecision.Awaiting;
        asked.SupportedByIbanMemory = true;
        asked.MatcherVersion = PaymentMatcher.PersonLevelVersion;
        db.PaymentMatches.Add(asked);
        db.CustomerIbanMemories.Add(Memory(lic, Guid.NewGuid(), iban));
        await db.SaveChangesAsync();
        var before = await db.PaymentMatches.AsNoTracking().SingleAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);

        await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        var after = await db.PaymentMatches.AsNoTracking().SingleAsync();
        after.Evidence.Should().BeNull();
        after.Should().BeEquivalentTo(before, o => o.Excluding(m => m.Evidence).Excluding(m => m.UpdatedAt), "yalnız kanıt boşalır");
    }

    [Fact]
    public async Task Silme_baska_ozetin_hareketinin_IBAN_onerisine_dokunmaz()
    {
        // (koruma) Kural yalnız talebin özetinden gelen hareketlerin satırlarına: başka IBAN'ın bağı ve ondan doğan öneri kalır,
        // satıra yazılmaz.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban(); var otherIban = BankHasherTests.TestIban();
        var stranger = Guid.NewGuid();
        var otherTx = Tx(lic, otherIban);
        db.BankTransactions.AddRange(Tx(lic, iban), otherTx);
        var row = Match(otherTx, proposed: stranger, evidence: "iban-memory", status: PaymentMatchStatus.Proposed);
        row.Layer = PaymentMatchLayer.IbanMemory;
        row.Confidence = PaymentMatcher.IbanConfidence;
        row.SupportedByIbanMemory = true;
        db.PaymentMatches.Add(row);
        db.CustomerIbanMemories.AddRange(Memory(lic, Guid.NewGuid(), iban), Memory(lic, stranger, otherIban));
        await db.SaveChangesAsync();
        var before = await db.PaymentMatches.AsNoTracking().SingleAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);

        await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        (await db.PaymentMatches.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(before);
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).WpfCustomerId.Should().Be(stranger);
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == otherTx.Id)).Description.Should().NotBeNull();
    }

    [Fact]
    public async Task Ayni_anda_iki_silme_ikincisi_talebi_kapanmis_bulur_kaydi_ezmez()
    {
        // Çift tık / iki yönetici: ikinci silme talebi açık okuduysa kapanışı (sonuç, kapatan, sayılar) ezip ikinci imha kaydı
        // kümesini yazardı. ClosedAt jetondur: ikinci kayıt düşer, yeniden deneme talebi kapanmış bulur.
        var interceptor = new BeforeRequestSave();
        var (db, other) = SharedDb(interceptor);
        using var _ = db;
        var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        db.BankTransactions.Add(Tx(lic, iban));
        await db.SaveChangesAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);
        db.ChangeTracker.Clear();
        interceptor.Before = async () =>
        {
            await using var first = other();
            await Service(first).EraseAsync(r.Id, "ilk", CancellationToken.None);
        };

        var second = () => Service(db).EraseAsync(r.Id, "ikinci", CancellationToken.None);

        await second.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.ClosedMessage);
        await using var verify = other();
        var closed = await verify.BankSubjectRequests.AsNoTracking().SingleAsync();
        (closed.ClosedBy, closed.Resolution).Should().Be(("ilk", BankSubjectResolution.Erased));
        closed.Outcome.Should().StartWith("Silindi: 1 hareketin");
    }

    [Fact]
    public async Task Ayni_anda_kapatma_ikincisi_talebi_kapanmis_bulur()
    {
        var interceptor = new BeforeRequestSave();
        var (db, other) = SharedDb(interceptor);
        using var _ = db;
        var lic = SeedLicense(db);
        var r = await Service(db).CreateAsync(Hasher, lic, BankHasherTests.TestIban(), BankSubjectRequestKind.Access,
            new DateOnly(2026, 10, 19), "admin", CancellationToken.None);
        db.ChangeTracker.Clear();
        interceptor.Before = async () =>
        {
            await using var first = other();
            await Service(first).CloseAsync(r.Id, BankSubjectResolution.Withdrawn, "ilk", CancellationToken.None);
        };

        var second = () => Service(db).CloseAsync(r.Id, BankSubjectResolution.RejectedNotVerified, "ikinci", CancellationToken.None);

        await second.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.ClosedMessage);
        await using var verify = other();
        (await verify.BankSubjectRequests.AsNoTracking().SingleAsync()).Should().Match<BankSubjectRequest>(x =>
            x.ClosedBy == "ilk" && x.Resolution == BankSubjectResolution.Withdrawn);
    }

    [Fact]
    public async Task Silme_yeniden_denemede_de_cakisirsa_yoneticiye_gosterilebilir_hata_doner()
    {
        // Repo kalıbı (BankConnectionTerminator, ObifinConnectionService): ikinci çakışma 500 değil, sayfanın gösterdiği mesaj.
        var conflict = new RequestSaveConflict();
        var (db, other) = SharedDb(conflict);
        using var _ = db;
        var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        db.BankTransactions.Add(Tx(lic, iban));
        await db.SaveChangesAsync();
        BankSubjectRequest r;
        await using (var setup = other()) r = await VerifiedErasureAsync(setup, lic, iban);

        var erase = () => Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        await erase.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.EraseConflictMessage);
        conflict.Thrown.Should().Be(2, "bir kez yeniden denendi");
        db.ChangeTracker.Entries().Should().BeEmpty("izleyici boşaldı");
    }

    [Fact]
    public async Task Lisansin_acik_talepleri_kapatilir_ozet_bosalir_kapali_talebe_dokunulmaz()
    {
        // Bağlantı kaldırma, hizmet sonu yerel silmesi ve yayıncı silmesi bu yardımcıyı çağırır: veri yokken açık talep ve
        // özeti kalmasın.
        using var db = NewDb(); var lic = SeedLicense(db); var other = SeedLicense(db);
        var open = await Service(db).CreateAsync(Hasher, lic, BankHasherTests.TestIban(), BankSubjectRequestKind.Access,
            new DateOnly(2026, 10, 19), "admin", CancellationToken.None);
        var answered = await Service(db).CreateAsync(Hasher, lic, BankHasherTests.TestIban(), BankSubjectRequestKind.Access,
            new DateOnly(2026, 10, 19), "admin", CancellationToken.None);
        await Service(db).VerifyAsync(answered.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);
        await Service(db).CloseAsync(answered.Id, BankSubjectResolution.Answered, "admin", CancellationToken.None);
        var otherOpen = await Service(db).CreateAsync(Hasher, other, BankHasherTests.TestIban(), BankSubjectRequestKind.Access,
            new DateOnly(2026, 10, 19), "admin", CancellationToken.None);

        var closed = await BankSubjectRequestService.CloseOpenForLicensesAsync(db, [lic], "Lisansın banka verisi silindi.",
            DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, DateTimeOffset.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();

        closed.Should().Be(1);
        var o = await db.BankSubjectRequests.AsNoTracking().SingleAsync(x => x.Id == open.Id);
        (o.ClosedAt is not null, o.IbanHash, o.Outcome).Should().Be((true, (string?)null, "Lisansın banka verisi silindi."));
        o.Resolution.Should().Be(BankSubjectResolution.DataDeleted);
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync(x => x.Id == answered.Id)).Should().Match<BankSubjectRequest>(x =>
            x.Resolution == BankSubjectResolution.Answered && x.Outcome == BankSubjectRequestService.OutcomeText(BankSubjectResolution.Answered));
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync(x => x.Id == otherOpen.Id)).ClosedAt.Should().BeNull();
        // Özetin boşalması talep başına bir imha kaydı; tetikleyici çağıranın (kaldırma, hizmet sonu, yayıncı silmesi).
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.Target == BankSubjectRequestService.IbanHashTarget)
            .ToListAsync();
        records.Should().ContainSingle(x => x.RequestId == open.Id).Which.Should().BeEquivalentTo(new
        {
            LicenseId = (Guid?)lic, Action = DataDestruction.Actions.Nulled, RowCount = 1, Trigger = DataDestruction.Triggers.ServiceEnd,
            Actor = DataDestruction.SystemActor, Criterion = BankSubjectRequestService.ClosedCriterion(BankSubjectResolution.DataDeleted),
        });
        records.Should().ContainSingle(x => x.RequestId == answered.Id, "kapanmış talebin kaydı kendi kapanışından, ikincisi yok")
            .Which.Trigger.Should().Be(DataDestruction.Triggers.SubjectRequest);
        records.Should().NotContain(x => x.RequestId == otherOpen.Id);
    }

    [Fact]
    public async Task Kapatma_yalniz_kurala_uygun_kategoriyle_yapilir_sonuc_metni_kategoriden_gelir()
    {
        // KVKK m. 13/3, Silme Yönetmeliği md. 12/1-c: gerekçeli ret ayrı bir sonuç. Talep kaydına yanlış sonuç düşmesin:
        // doğrulanmamış erişim talebi "yanıtlandı", doğrulanmış talep "doğrulanamadı" kapanamaz; silme yalnız EraseAsync'te.
        using var db = NewDb(); var lic = SeedLicense(db);
        async Task<BankSubjectRequest> Open(BankSubjectRequestKind kind, bool verified)
        {
            var r = await Service(db).CreateAsync(Hasher, lic, BankHasherTests.TestIban(), kind, new DateOnly(2026, 10, 19), "admin",
                CancellationToken.None);
            if (verified)
                await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);
            return r;
        }
        var unverifiedAccess = await Open(BankSubjectRequestKind.Access, verified: false);
        var verifiedAccess = await Open(BankSubjectRequestKind.Access, verified: true);
        var verifiedErasure = await Open(BankSubjectRequestKind.Erasure, verified: true);
        Func<Task> Close(BankSubjectRequest r, BankSubjectResolution resolution)
            => () => Service(db).CloseAsync(r.Id, resolution, "admin", CancellationToken.None);

        await Close(unverifiedAccess, BankSubjectResolution.Answered).Should().ThrowAsync<ObifinValidationException>()
            .WithMessage(BankSubjectRequestService.ResolutionNotAllowedMessage);
        await Close(verifiedErasure, BankSubjectResolution.Answered).Should().ThrowAsync<ObifinValidationException>();
        await Close(verifiedAccess, BankSubjectResolution.RejectedNotVerified).Should().ThrowAsync<ObifinValidationException>();
        await Close(verifiedErasure, BankSubjectResolution.Erased).Should().ThrowAsync<ObifinValidationException>();
        await Close(verifiedErasure, BankSubjectResolution.DataDeleted).Should().ThrowAsync<ObifinValidationException>();
        await Close(verifiedErasure, BankSubjectResolution.None).Should().ThrowAsync<ObifinValidationException>();

        await Close(unverifiedAccess, BankSubjectResolution.RejectedNotVerified)();
        await Close(verifiedAccess, BankSubjectResolution.Answered)();
        await Close(verifiedErasure, BankSubjectResolution.RejectedLegalGround)();

        var rows = await db.BankSubjectRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        rows[unverifiedAccess.Id].Should().Match<BankSubjectRequest>(r => r.Resolution == BankSubjectResolution.RejectedNotVerified
            && r.Outcome == BankSubjectRequestService.OutcomeText(BankSubjectResolution.RejectedNotVerified)
            && r.ClosedAt != null && r.IbanHash == null);
        rows[verifiedAccess.Id].Resolution.Should().Be(BankSubjectResolution.Answered);
        rows[verifiedErasure.Id].Resolution.Should().Be(BankSubjectResolution.RejectedLegalGround);
        // Silme Yönetmeliği md. 7/1: kapanışta boşalan özet imha kaydına girer, talep başına bir satır; ölçüt sonucu taşır (geri
        // yüklenen açık talep aynı sonuçla yeniden kapatılır). Reddedilen kapatma denemeleri kayıt yazmadı.
        (await db.DataDestructionRecords.AsNoTracking().Select(x => new { x.RequestId, x.Target, x.Action, x.RowCount, x.Trigger,
                x.Criterion, x.Actor, x.LicenseId }).ToListAsync())
            .Should().BeEquivalentTo(new[]
            {
                (unverifiedAccess.Id, BankSubjectResolution.RejectedNotVerified),
                (verifiedAccess.Id, BankSubjectResolution.Answered),
                (verifiedErasure.Id, BankSubjectResolution.RejectedLegalGround),
            }.Select(e => new
            {
                RequestId = (Guid?)e.Item1, Target = BankSubjectRequestService.IbanHashTarget, Action = DataDestruction.Actions.Nulled,
                RowCount = 1, Trigger = DataDestruction.Triggers.SubjectRequest,
                Criterion = BankSubjectRequestService.ClosedCriterion(e.Item2), Actor = "admin", LicenseId = (Guid?)lic,
            }));
    }

    [Fact]
    public async Task Erisim_talebiyle_silme_yapilmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        db.BankTransactions.Add(Tx(lic, iban));
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Access, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);
        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);

        var erase = () => Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        await erase.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.WrongKindMessage);
        (await db.BankTransactions.AsNoTracking().SingleAsync()).Description.Should().NotBeNull("silme yapılmadı");
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync()).ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task Silme_talebinde_disa_aktarim_yapilmaz()
    {
        // KVKK m. 4/2-ç: başvuran yalnız silme istedi. Silmeden önce alınan CSV sistem dışında imha kaydına girmeyen bir kopya
        // bırakırdı; kopya isteyen başvurana ayrı bir erişim talebi açılır. Yönetici veriyi sayfada görür (FindAsync).
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        db.BankTransactions.Add(Tx(lic, iban));
        await db.SaveChangesAsync();
        var r = await VerifiedErasureAsync(db, lic, iban);

        var export = () => Service(db).ExportAsync(r.Id, CancellationToken.None);

        await export.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.ExportWrongKindMessage);
        (await Service(db).FindAsync(r.Id, CancellationToken.None))!.Transactions.Should().ContainSingle("arama yine çalışır");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=1+1")]
    [InlineData("\r=1+1")]
    public void Formul_sayilabilecek_hucre_kesme_isaretiyle_baslar(string description)
    {
        // OWASP CSV injection: '=', '+', '-', '@', sekme ve satır başı ile başlayan hücre formül sayılabilir.
        var request = new BankSubjectRequest { Id = Guid.NewGuid(), IbanMasked = "TR12…345" };
        var row = new BankSubjectRequestService.TransactionRow(Guid.NewGuid(), DateTimeOffset.UtcNow, 1m, "TL", "qnb", "TR12…345",
            description, null, null, null, null, false);

        var csv = Encoding.UTF8.GetString(BankSubjectRequestService.ToCsv(new BankSubjectRequestService.Lookup(request, [row], 0)));

        csv.Should().Contain("\"'" + description + "\"");
    }
}
