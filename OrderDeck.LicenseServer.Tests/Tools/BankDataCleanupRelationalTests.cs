using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>bank-data-cleanup gerçek SQL Server'da (göçlerle kurulmuş şema, FK'ler, CHECK kısıtları, eşzamanlılık jetonu):
/// kuru koşu yazmaz ve uygulamanın sayılarını verir; uygulama gideni ve çapa öncesi kararsız satırı eşleşmesiyle siler, çapayı
/// yazar, VKN kodunu boşaltır, ham kaydı ve açıklamayı daraltır; çıktıda kişisel veri yok. Gövde, bağlantısı ve yazıcısı
/// verilmiş <see cref="BankDataCleanup.RunAsync(LicenseDbContext, BankDataCleanup.Options, TextWriter, Microsoft.Extensions.Logging.ILoggerFactory, CancellationToken)"/>'ten
/// girilir; ayrıştırma <see cref="BankDataCleanupTests"/>'te.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankDataCleanupRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public BankDataCleanupRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

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

    private sealed record Seed(Guid LicenseId, Guid OutgoingId, Guid PreAnchorId, Guid KeptId, string Iban, string Tckn);

    /// <summary>Bir lisans, ilk çekimi 20.09'da bitmiş (çapasız) bir bağlantı; giden, çapa öncesi ve çapadan sonraki birer gelen
    /// hareket, hepsinin eşleşme satırı. Değerler üretilir.</summary>
    private async Task<Seed> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"bc-{Guid.NewGuid():N}@example.test", Name = "Temizlik",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow };
        var license = new License { Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = "bc-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddYears(1) };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        var backfill = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = license.Id, BaseUrl = "https://example.invalid",
            UserCode = $"u-{Guid.NewGuid():N}@x", Status = ObifinConnectionStatus.Verified, BackfillCompletedAt = backfill, CreatedAt = backfill, UpdatedAt = backfill });
        var iban = BankHasherTests.TestIban();
        var tckn = TestTckn.NewValid();
        BankTransaction Tx(BankTransactionDirection d, decimal amount, DateTimeOffset at) => new()
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 6,
            BankaKodu = "qnb", Direction = d, Amount = amount, Currency = "TL", OccurredAt = at, FetchedAt = at,
        };
        var outgoing = Tx(BankTransactionDirection.Outgoing, 30m, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var preAnchor = Tx(BankTransactionDirection.Incoming, 10m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var kept = Tx(BankTransactionDirection.Incoming, 100m, new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        kept.Description = $"HAVALE {iban} {tckn}";
        kept.CounterpartyTaxIdHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        kept.CounterpartyIbanMasked = TestPhone.Digits(7);
        kept.RawJson = $$"""{"Aciklama":"HAVALE {{iban}}","Bakiye":"10.00"}""";
        db.BankTransactions.AddRange(outgoing, preAnchor, kept);
        foreach (var t in new[] { outgoing, preAnchor, kept })
            db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = license.Id, BankTransactionId = t.Id,
                Status = PaymentMatchStatus.NoProposal, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        // P1 öncesi öğrenilmiş bağ (LastConfirmedAt boş): öğreten havale kept, öğrenme ondan 10 gün sonra.
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = license.Id, WpfCustomerId = Guid.NewGuid(),
            IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch,
            SourceBankTransactionId = kept.Id, CreatedAt = kept.OccurredAt.AddDays(10) });
        await db.SaveChangesAsync();
        return new Seed(license.Id, outgoing.Id, preAnchor.Id, kept.Id, iban, tckn);
    }

    [Fact]
    public async Task Kuru_kosu_yazmaz_uygulama_yeni_kurala_indirir_cikti_kisisel_veri_icermez()
    {
        var seed = await SeedAsync();
        string dryLine, appliedLine;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var dry = new StringWriter();
            (await BankDataCleanup.RunAsync(db, new BankDataCleanup.Options(null, Apply: false), dry, NullLoggerFactory.Instance,
                CancellationToken.None)).Should().Be(0);
            dryLine = dry.ToString();
            dryLine.Should().StartWith("KURU ÇALIŞTIRMA").And.Contain("giden/sıfır silinen=1").And.Contain("çapa yazılan=1")
                .And.Contain("çapa öncesi silinen=1").And.Contain("eşleşme silinen=2").And.Contain("IBAN bağı başlangıcı yazılan=1");
            (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().BeNull("kuru koşu yazmaz");
            (await db.BankTransactions.CountAsync()).Should().Be(3);
            (await db.ObifinConnections.AsNoTracking().SingleAsync()).InitialCollectFrom.Should().BeNull();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var applied = new StringWriter();
            (await BankDataCleanup.RunAsync(db, new BankDataCleanup.Options(seed.LicenseId, Apply: true), applied,
                NullLoggerFactory.Instance, CancellationToken.None)).Should().Be(0);
            appliedLine = applied.ToString();
            appliedLine.Should().StartWith("UYGULANDI").And.Contain($"lisans={seed.LicenseId}");
        }
        // Kuru koşunun sayıları uygulamanınkilerle aynı (tutanağa ikisi de girer).
        dryLine[dryLine.IndexOf(':')..].Should().Be(appliedLine[appliedLine.IndexOf(':')..]);
        foreach (var line in new[] { dryLine, appliedLine })
        {
            line.Should().NotContain(seed.Iban).And.NotContain(seed.Tckn);
            line.Should().NotContain(seed.Iban[..4] + "…", "IBAN'ın maskesi de çıktıya girmez");
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { seed.KeptId });
            (await db.PaymentMatches.Select(m => m.BankTransactionId).ToListAsync()).Should().BeEquivalentTo(new[] { seed.KeptId });
            (await db.ObifinConnections.AsNoTracking().SingleAsync()).InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 18));
            var kept = await db.BankTransactions.AsNoTracking().SingleAsync();
            kept.CounterpartyTaxIdHash.Should().BeNull();
            kept.CounterpartyIbanMasked.Should().Be(BankHasher.ShortValueMask);
            kept.Description.Should().Be($"HAVALE {BankHasher.MaskIban(seed.Iban)} {BankDescriptionMasker.ElevenDigitMask}");
            kept.RawJson.Should().Be($$"""{"Aciklama":"HAVALE {{BankHasher.MaskIban(seed.Iban)}}"}""");
            (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt
                .Should().Be(kept.OccurredAt, "P1 öncesi bağın başlangıcı öğreten havalenin işlem tarihi");

            var again = new StringWriter();
            (await BankDataCleanup.RunAsync(db, new BankDataCleanup.Options(null, Apply: true), again, NullLoggerFactory.Instance,
                CancellationToken.None)).Should().Be(0);
            again.ToString().Should().Contain("giden/sıfır silinen=0").And.Contain("çapa yazılan=0").And.Contain("çapa öncesi silinen=0")
                .And.Contain("VKN/TCKN kodu boşaltılan=0").And.Contain("açıklama maskelenen=0").And.Contain("kısa maske çevrilen=0")
                .And.Contain("IBAN bağı başlangıcı yazılan=0");
        }
    }

    [Fact]
    public async Task Maskeli_kaynakli_ozet_bosaltilir_hafiza_sifirlanir_hareket_ozeti_kalir()
    {
        // KVKK Ö-7 gerçek SQL Server'da (CHECK kısıtları, parti sorguları): maskeli kaynaklı özet boşaltılır, o özetle öğrenilmiş
        // bağ silinir; --reset-iban-memory geçerli IBAN'lı (CounterpartyIbanHashValid) bir harekette görünmeyen bütün bağları
        // siler, görüneni tutar; hareketlerin özeti talep aracı için kalır. Silinen bağın P1 öncesi başlangıcı yazılmaz; kuru
        // koşunun sayıları uygulamanınkiler.
        var seed = await SeedAsync();
        var suspect = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var clean = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var valid = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        Guid maskedTxId, cleanTxId, validMemoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            BankTransaction Hashed(string hash, string mask, int day) => new()
            {
                Id = Guid.NewGuid(), LicenseId = seed.LicenseId, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 6,
                BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 50m + day, Currency = "TL",
                OccurredAt = new DateTimeOffset(2026, 10, day, 9, 0, 0, TimeSpan.Zero), FetchedAt = DateTimeOffset.UtcNow,
                CounterpartyIbanHash = hash, CounterpartyIbanMasked = mask,
            };
            var maskedTx = Hashed(suspect, "TR12…**3", 3);
            var cleanTx = Hashed(clean, BankHasher.MaskIban(seed.Iban), 4);
            var validTx = Hashed(valid, BankHasher.MaskIban(BankHasherTests.TestIban()), 5);
            validTx.CounterpartyIbanHashValid = true; // Görev 11'den sonra çekilmiş, geçerli IBAN'lı hareket
            db.BankTransactions.AddRange(maskedTx, cleanTx, validTx);
            CustomerIbanMemory Memory(string hash, Guid source) => new()
            {
                Id = Guid.NewGuid(), LicenseId = seed.LicenseId, WpfCustomerId = Guid.NewGuid(), IbanHash = hash, IbanMasked = "TR12…345",
                LearnedFrom = IbanMemorySource.HumanApproval, SourceBankTransactionId = source, CreatedAt = DateTimeOffset.UtcNow,
            };
            var validMemory = Memory(valid, validTx.Id);
            validMemory.LastConfirmedAt = validTx.OccurredAt; // P1'den sonra öğrenilmiş: başlangıç öğrenirken yazıldı
            db.CustomerIbanMemories.AddRange(Memory(suspect, maskedTx.Id), Memory(clean, cleanTx.Id), validMemory);
            await db.SaveChangesAsync();
            (maskedTxId, cleanTxId, validMemoryId) = (maskedTx.Id, cleanTx.Id, validMemory.Id);
        }

        string dryLine, appliedLine;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var dry = new StringWriter();
            (await BankDataCleanup.RunAsync(db, new BankDataCleanup.Options(null, Apply: false, ResetIbanMemory: true), dry,
                NullLoggerFactory.Instance, CancellationToken.None)).Should().Be(0);
            dryLine = dry.ToString();
            (await db.CustomerIbanMemories.CountAsync()).Should().Be(4, "kuru koşu yazmaz");
            var applied = new StringWriter();
            (await BankDataCleanup.RunAsync(db, new BankDataCleanup.Options(null, Apply: true, ResetIbanMemory: true), applied,
                NullLoggerFactory.Instance, CancellationToken.None)).Should().Be(0);
            appliedLine = applied.ToString();
        }
        // Tohumun bağı (öğreten havale kept) ve temiz bağ sıfırlanır; şüpheli bağ maskeli kaynaklı olarak gider; geçerli IBAN'lı
        // harekette görünen bağ kalır.
        appliedLine.Should().Contain("maskeli kaynaklı özet boşaltılan=1 (hafıza silinen=1)")
            .And.Contain("sıfırlanan IBAN hafızası=2 (geçerli IBAN'lı kalan=1)").And.Contain("IBAN bağı başlangıcı yazılan=0");
        dryLine[dryLine.IndexOf(':')..].Should().Be(appliedLine[appliedLine.IndexOf(':')..]);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync()).Should().BeEquivalentTo(new[] { validMemoryId });
            var hashes = await db.BankTransactions.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.CounterpartyIbanHash);
            hashes[maskedTxId].Should().BeNull();
            hashes[cleanTxId].Should().Be(clean, "talep aracı hareketi özetle bulur (K-7)");
            var records = await db.DataDestructionRecords.AsNoTracking().Where(r => r.LicenseId == seed.LicenseId).ToListAsync();
            records.Where(r => r.Target == "BankTransactions.CounterpartyIbanHash").Sum(r => r.RowCount).Should().Be(1);
            records.Where(r => r.Target == "CustomerIbanMemories").Sum(r => r.RowCount).Should().Be(3);
        }
    }

    [Fact]
    public async Task Eszamanli_eslestirme_yazisi_partiyi_geri_aldirir_cikis_1_yeniden_kosu_tamamlar()
    {
        // Kural: silme partisi eşleşme satırını okuduğu jetonla siler. Arada eşleştirme satırı güncellenirse (bir insan kararı
        // ya da yeniden hesap) parti BÜTÜNÜYLE geri alınır: hareket de silinmez. Çıkış 1; yeniden koşu güncel satıra göre karar
        // verir — burada güncelleme bir dekont bağı, satır kalır.
        var seed = await SeedAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var interceptorFired = false;
        db.SavingChanges += (_, _) =>
        {
            if (interceptorFired || !db.ChangeTracker.Entries<BankTransaction>().Any(e => e.State == EntityState.Deleted)) return;
            interceptorFired = true;
            // Ayrı bağlantıdan, partinin okumasından sonra ve silmesinden önce: dekont bağı.
            using var other = _factory.Services.CreateScope();
            var otherDb = other.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var match = otherDb.PaymentMatches.Single(m => m.BankTransactionId == seed.OutgoingId);
            match.PaymentId = Guid.NewGuid();
            otherDb.SaveChanges();
        };

        var output = new StringWriter();
        (await BankDataCleanup.RunAsync(db, new BankDataCleanup.Options(null, Apply: true), output, NullLoggerFactory.Instance,
            CancellationToken.None)).Should().Be(1);
        interceptorFired.Should().BeTrue();
        output.ToString().Should().Contain("yeniden çalıştırın");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.BankTransactions.AnyAsync(t => t.Id == seed.OutgoingId)).Should().BeTrue("parti geri alındı");
        (await vdb.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == seed.OutgoingId)).PaymentId
            .Should().NotBeNull("insan kararı ezilmedi");

        var rerun = new StringWriter();
        (await BankDataCleanup.RunAsync(vdb, new BankDataCleanup.Options(null, Apply: true), rerun, NullLoggerFactory.Instance,
            CancellationToken.None)).Should().Be(0);
        rerun.ToString().Should().Contain("kararlı atlanan=1");
        (await vdb.BankTransactions.AnyAsync(t => t.Id == seed.OutgoingId)).Should().BeTrue("dekonta bağlı giden insan kararı bekler");
    }
}
