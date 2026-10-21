using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Tek seferlik KVKK temizliği (bank-data-cleanup): kuru koşu yazmaz ve uygulamanın sayılarını verir, uygulama yeni
/// kurala indirir, ikinci uygulama hiçbir şeyi değiştirmez. Değerler üretilir.</summary>
public sealed class BankDataCleanupJobTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-cleanup-{Guid.NewGuid():N}").Options);

    private static BankDataCleanupJob Job(LicenseDbContext db) => new(db, NullLogger<BankDataCleanupJob>.Instance);

    private static BankTransaction Tx(Guid lic, decimal amount, DateTimeOffset? at = null,
        BankTransactionDirection direction = BankTransactionDirection.Incoming, string? description = null,
        string? rawJson = null, string? taxIdHash = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 6,
        BankaKodu = "qnb", Direction = direction, Amount = amount, Currency = "TL", OccurredAt = at ?? DateTimeOffset.UtcNow,
        FetchedAt = at ?? DateTimeOffset.UtcNow, Description = description, RawJson = rawJson, CounterpartyTaxIdHash = taxIdHash,
    };

    private static PaymentMatch Match(BankTransaction tx, Guid? actual = null, DateTimeOffset? decidedAt = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, ActualWpfCustomerId = actual, DecidedAt = decidedAt,
        Status = actual is null ? PaymentMatchStatus.NoProposal : PaymentMatchStatus.ManualOnly,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ObifinConnection Connection(Guid lic, DateTimeOffset createdAt, DateTimeOffset? backfillCompletedAt,
        DateOnly? initialCollectFrom = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://example.invalid", UserCode = $"u-{Guid.NewGuid():N}@x",
        Status = ObifinConnectionStatus.Verified, BackfillCompletedAt = backfillCompletedAt, InitialCollectFrom = initialCollectFrom,
        CreatedAt = createdAt, UpdatedAt = createdAt,
    };

    private static string Hex64() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    private static CustomerIbanMemory Memory(Guid lic, string hash, Guid? sourceId = null, string mask = "TR12…345") => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(), IbanHash = hash, IbanMasked = mask,
        LearnedFrom = IbanMemorySource.HumanApproval, SourceBankTransactionId = sourceId, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Kuru_kosu_sayar_hicbir_sey_yazmaz()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var outgoing = Tx(lic, 30m, direction: BankTransactionDirection.Outgoing);
        db.BankTransactions.AddRange(outgoing, Tx(lic, 0m), Tx(lic, 10m, taxIdHash: Hex64()));
        db.PaymentMatches.Add(Match(outgoing));
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(licenseId: null, apply: false, CancellationToken.None);

        r.OutgoingOrZeroDeleted.Should().Be(2);
        r.TaxIdHashesCleared.Should().Be(1);
        (await db.BankTransactions.CountAsync()).Should().Be(3);
        (await db.PaymentMatches.CountAsync()).Should().Be(1);
        (await db.BankTransactions.CountAsync(t => t.CounterpartyTaxIdHash != null)).Should().Be(1);
    }

    [Fact]
    public async Task Uygulama_gideni_ve_sifiri_eslesmesiyle_siler_insan_kararina_bagliyi_atlar()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var outgoing = Tx(lic, 30m, direction: BankTransactionDirection.Outgoing);
        var zero = Tx(lic, 0m);
        var decidedOutgoing = Tx(lic, 15m, direction: BankTransactionDirection.Outgoing);
        var incoming = Tx(lic, 100m);
        db.BankTransactions.AddRange(outgoing, zero, decidedOutgoing, incoming);
        db.PaymentMatches.AddRange(Match(outgoing), Match(decidedOutgoing, actual: Guid.NewGuid(), decidedAt: DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.OutgoingOrZeroDeleted.Should().Be(2);
        r.OutgoingOrZeroSkippedDecided.Should().Be(1);
        r.MatchesDeleted.Should().Be(1);
        (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { decidedOutgoing.Id, incoming.Id });
        (await db.PaymentMatches.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Capa_yazilir_capadan_eski_kararsiz_satir_silinir_bagli_olan_kalir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        // İlk çekim 20.09 12:00 UTC (TR 15:00) → çapa 18.09 (TR); 18.09 00:00 TR = 17.09 21:00 UTC.
        var backfill = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        db.ObifinConnections.Add(Connection(lic, backfill, backfill));
        var old = Tx(lic, 10m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var justBefore = Tx(lic, 11m, new DateTimeOffset(2026, 9, 17, 20, 59, 0, TimeSpan.Zero));
        var onAnchor = Tx(lic, 12m, new DateTimeOffset(2026, 9, 17, 21, 0, 0, TimeSpan.Zero));
        var oldDecided = Tx(lic, 13m, new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero));
        var oldTaughtIban = Tx(lic, 14m, new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));
        var oldResolvedGap = Tx(lic, 15m, new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero));
        db.BankTransactions.AddRange(old, justBefore, onAnchor, oldDecided, oldTaughtIban, oldResolvedGap);
        db.PaymentMatches.AddRange(Match(old), Match(oldDecided, actual: Guid.NewGuid(), decidedAt: DateTimeOffset.UtcNow));
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = Hex64(), IbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban()), LearnedFrom = IbanMemorySource.ManualMatch,
            SourceBankTransactionId = oldTaughtIban.Id, CreatedAt = DateTimeOffset.UtcNow });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, ResolvedBankTransactionId = oldResolvedGap.Id, ResolvedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.AnchorsSet.Should().Be(1);
        r.PreAnchorDeleted.Should().Be(2);
        r.PreAnchorSkippedLinked.Should().Be(3);
        r.MatchesDeleted.Should().Be(1);
        (await db.BankTransactions.Select(t => t.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { onAnchor.Id, oldDecided.Id, oldTaughtIban.Id, oldResolvedGap.Id });
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync();
        conn.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 18));
        conn.UpdatedAt.Should().Be(backfill, "çekim işinin kimlik denetimi UpdatedAt'e bakar; değişmemeli");
    }

    [Fact]
    public void Capa_ilk_cekimin_TR_gunu_eksi_pay()
    {
        // 19.09 22:30 UTC = 20.09 01:30 TR: TR günü 20.09 → çapa 18.09 (UTC günü alınsaydı 17.09 olurdu).
        BankDataCleanupJob.AnchorFor(new DateTimeOffset(2026, 9, 19, 22, 30, 0, TimeSpan.Zero))
            .Should().Be(new DateOnly(2026, 9, 20).AddDays(-ObifinPollJob.InitialGraceDays));
    }

    [Fact]
    public async Task Capasi_olan_her_baglantida_capadan_eski_kararsiz_satir_silinir_capa_degismez()
    {
        // P0 anında eski kodun 90 günlük ilk çekimi yarıda kalmış bağlantı: BackfillCompletedAt boş, sayfa sayfa kaydedilmiş
        // eski satırları var; yeni çekim işi ilk turda çapayı yazdı (18.09). Komut çapa yazmaz ama çapadan eski kararsız
        // satırı siler; ikinci koşu silecek bir şey bulmaz.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        db.ObifinConnections.Add(Connection(lic, created, backfillCompletedAt: null, initialCollectFrom: new DateOnly(2026, 9, 18)));
        var old = Tx(lic, 10m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var oldDecided = Tx(lic, 13m, new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero));
        var onAnchor = Tx(lic, 12m, new DateTimeOffset(2026, 9, 17, 21, 0, 0, TimeSpan.Zero)); // 18.09 00:00 TR
        db.BankTransactions.AddRange(old, oldDecided, onAnchor);
        db.PaymentMatches.AddRange(Match(old), Match(oldDecided, actual: Guid.NewGuid(), decidedAt: DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        (dry.AnchorsSet, dry.PreAnchorDeleted, dry.PreAnchorSkippedLinked, dry.MatchesDeleted).Should().Be((0, 1, 1, 1));

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        (r.AnchorsSet, r.PreAnchorDeleted, r.PreAnchorSkippedLinked, r.MatchesDeleted).Should().Be((0, 1, 1, 1));
        (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { oldDecided.Id, onAnchor.Id });
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync();
        conn.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 18));
        conn.BackfillCompletedAt.Should().BeNull();
        conn.UpdatedAt.Should().Be(created);
        (await Job(db).RunAsync(null, apply: true, CancellationToken.None)).PreAnchorDeleted.Should().Be(0);
    }

    [Fact]
    public async Task Capasi_da_ilk_cekimi_de_olmayan_baglanti_atlanir()
    {
        // Verified olmayan, çekim işinin çapa yazmadığı bağlantı: komut çapasını bilemez (Prod adımlarındaki ön denetim sayar).
        using var db = NewDb(); var lic = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var conn = Connection(lic, created, backfillCompletedAt: null);
        conn.Status = ObifinConnectionStatus.Failed;
        db.ObifinConnections.Add(conn);
        db.BankTransactions.Add(Tx(lic, 10m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        (r.AnchorsSet, r.PreAnchorDeleted, r.PreAnchorSkippedLinked).Should().Be((0, 0, 0));
        (await db.BankTransactions.CountAsync()).Should().Be(1);
        (await db.ObifinConnections.AsNoTracking().SingleAsync()).InitialCollectFrom.Should().BeNull();
    }

    [Fact]
    public async Task Vkn_kodu_bosaltilir_ham_kayit_daraltilir_aciklama_maskelenir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var iban = BankHasherTests.TestIban();
        var tckn = TestTckn.NewValid();
        var tx = Tx(lic, 10m, description: $"HAVALE {iban} {tckn}", taxIdHash: Hex64(),
            rawJson: $$"""{"Aciklama":"HAVALE {{iban}}","KarsiHesapIBAN":"[redakte]","Bakiye":"10.00","GonderenAdi":"Ad Soyad"}""");
        var broken = Tx(lic, 11m, rawJson: "{bozuk");
        db.BankTransactions.AddRange(tx, broken);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.TaxIdHashesCleared.Should().Be(1);
        r.RawJsonRewritten.Should().Be(1);
        r.RawJsonUnreadableCleared.Should().Be(1);
        r.DescriptionsMasked.Should().Be(1);
        var row = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == tx.Id);
        row.CounterpartyTaxIdHash.Should().BeNull();
        row.RawJson.Should().Be($$"""{"Aciklama":"HAVALE {{BankHasher.MaskIban(iban)}}","GonderenAdi":"Ad Soyad"}""");
        row.Description.Should().Be($"HAVALE {BankHasher.MaskIban(iban)} {BankDescriptionMasker.ElevenDigitMask}");
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == broken.Id)).RawJson.Should().BeNull();
    }

    [Fact]
    public async Task Kisa_ya_da_eski_bicimli_maske_ShortValueMask_olur_IBAN_maskesi_ve_yer_tutucu_kalir()
    {
        // KVKK Ö-6: eski MaskIban kısa değeri ham, 8–14 karakteri "1234…890" yazdı (10 hanenin 7'si açık).
        using var db = NewDb(); var lic = Guid.NewGuid();
        var account10 = TestPhone.Digits(10);
        var oldFormat = account10[..4] + "…" + account10[^3..];
        var shortRaw = Tx(lic, 10m); shortRaw.CounterpartyIbanMasked = TestPhone.Digits(7);
        var oldMasked = Tx(lic, 11m); oldMasked.CounterpartyIbanMasked = oldFormat;
        var ibanMasked = Tx(lic, 12m); ibanMasked.CounterpartyIbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban());
        db.BankTransactions.AddRange(shortRaw, oldMasked, ibanMasked);
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = Hex64(), IbanMasked = oldFormat, LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow });
        var placeholder = new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 1, BankaKodu = "qnb",
            IbanMasked = "?", Currency = "TL", RefreshedAt = DateTimeOffset.UtcNow };
        var oldAccount = new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 2, BankaKodu = "qnb",
            IbanMasked = oldFormat, Currency = "TL", RefreshedAt = DateTimeOffset.UtcNow };
        db.BankAccounts.AddRange(placeholder, oldAccount);
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        dry.ShortMasksReplaced.Should().Be(4);
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == shortRaw.Id)).CounterpartyIbanMasked
            .Should().HaveLength(7, "kuru koşu yazmaz");
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).IbanMasked.Should().Be(oldFormat, "kuru koşu yazmaz");

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.ShortMasksReplaced.Should().Be(4);
        var masks = await db.BankTransactions.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.CounterpartyIbanMasked);
        masks[shortRaw.Id].Should().Be(BankHasher.ShortValueMask);
        masks[oldMasked.Id].Should().Be(BankHasher.ShortValueMask);
        masks[ibanMasked.Id].Should().Be(ibanMasked.CounterpartyIbanMasked, "IBAN maskesi biçimi kalır");
        // Eski biçimli maske IBAN olmayan bir değerden (hesap numarası): bağın özeti de oradan. Önce maskesi çevrilir (Ö-6),
        // sonra bağ maskeli kaynaklı olarak silinir (Ö-7; Maskeli_kaynakli_… testleri).
        r.MaskedSourceMemoriesDeleted.Should().Be(1);
        (await db.CustomerIbanMemories.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.DataDestructionRecords.AsNoTracking().Select(x => x.Target).ToListAsync())
            .Should().Contain(new[] { "CustomerIbanMemories.IbanMasked", "CustomerIbanMemories" });
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == placeholder.Id)).IbanMasked.Should().Be("?");
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == oldAccount.Id)).IbanMasked.Should().Be(BankHasher.ShortValueMask);
    }

    [Theory]
    [InlineData("TR12…345", false)]
    [InlineData("DE89…000", false)]
    [InlineData("…", false)]
    [InlineData("?", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("1234…890", true)]
    [InlineData("TR12", true)]
    [InlineData("1234567", true)]
    [InlineData("tr12…345", true)]
    public void IBAN_maskesi_bicimi_disindaki_maske_yeniden_maskelenir(string? mask, bool expected)
        => BankDataCleanupJob.NeedsRemask(mask).Should().Be(expected);

    [Fact]
    public async Task Kuru_kosu_uygulamanin_sayilarini_verir_silinecek_satir_sonraki_adimlarda_sayilmaz()
    {
        // Uygulama önce giden ve çapa öncesi satırı siler; onların VKN kodu, ham kaydı, açıklaması ve maskesi sonraki adımlarda
        // sayılmaz. Tutanağa önce kuru koşunun çıktısı girer: kuru koşu aynı sayıları vermeli.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var backfill = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        db.ObifinConnections.Add(Connection(lic, backfill, backfill));
        var oldAt = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        var recentAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        BankTransaction Dirty(decimal amount, DateTimeOffset at, BankTransactionDirection direction = BankTransactionDirection.Incoming)
        {
            var t = Tx(lic, amount, at, direction, description: $"EFT {TestTckn.NewValid()}",
                rawJson: """{"Aciklama":"x","Bakiye":"1"}""", taxIdHash: Hex64());
            // Hesap numarası: eski kod 8+ karakteri özetliyordu, eski maske ham kısa değer (Ö-6, Ö-7).
            t.CounterpartyIbanMasked = TestPhone.Digits(7);
            t.CounterpartyIbanHash = Hex64();
            return t;
        }
        var outgoingRecent = Dirty(30m, recentAt, BankTransactionDirection.Outgoing);
        var outgoingOld = Dirty(31m, oldAt, BankTransactionDirection.Outgoing);
        var preAnchor = Dirty(10m, oldAt);
        var oldDecided = Dirty(11m, oldAt);
        var kept = Dirty(12m, recentAt);
        db.BankTransactions.AddRange(outgoingRecent, outgoingOld, preAnchor, oldDecided, kept);
        db.PaymentMatches.AddRange(Match(outgoingRecent), Match(outgoingOld), Match(preAnchor), Match(kept),
            Match(oldDecided, actual: Guid.NewGuid(), decidedAt: DateTimeOffset.UtcNow));
        // kept'in öğrettiği bağ maskeli kaynaklı silinir: P1 öncesi başlangıcı da yazılmaz. Silinecek preAnchor'ın özetini
        // taşıyan bağın maskesi temiz: şüpheyi yalnız silinen satır taşıyor, bağ kalır (kuru koşu da saymaz).
        db.CustomerIbanMemories.AddRange(Memory(lic, kept.CounterpartyIbanHash!, sourceId: kept.Id),
            Memory(lic, preAnchor.CounterpartyIbanHash!));
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        var applied = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        (applied.OutgoingOrZeroDeleted, applied.AnchorsSet, applied.PreAnchorDeleted, applied.PreAnchorSkippedLinked,
            applied.MatchesDeleted).Should().Be((2, 1, 1, 1, 3));
        (applied.TaxIdHashesCleared, applied.RawJsonRewritten, applied.DescriptionsMasked, applied.ShortMasksReplaced,
                applied.MaskedSourceHashesCleared)
            .Should().Be((2, 2, 2, 2, 2), "yalnız kalan iki satır (karar verilmiş eski ve çapadan sonraki) daraltılır");
        (applied.MaskedSourceMemoriesDeleted, applied.IbanConfirmationsBackfilled).Should().Be((1, 0));
        dry.Should().BeEquivalentTo(applied);
        (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { oldDecided.Id, kept.Id });
    }

    [Fact]
    public async Task Ikinci_uygulama_hicbir_satiri_degistirmez()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var backfill = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        db.ObifinConnections.Add(Connection(lic, backfill, backfill));
        var withShortMask = Tx(lic, 12m); withShortMask.CounterpartyIbanMasked = TestPhone.Digits(7);
        withShortMask.CounterpartyIbanHash = Hex64();
        db.BankTransactions.AddRange(
            Tx(lic, 30m, direction: BankTransactionDirection.Outgoing),
            Tx(lic, 10m, description: $"EFT {TestTckn.NewValid()}", rawJson: """{"Aciklama":"x","Bakiye":"1"}""", taxIdHash: Hex64()),
            Tx(lic, 9m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero)),
            withShortMask);
        db.CustomerIbanMemories.Add(Memory(lic, withShortMask.CounterpartyIbanHash));
        await db.SaveChangesAsync();
        var first = await Job(db).RunAsync(null, apply: true, CancellationToken.None);
        (first.MaskedSourceHashesCleared, first.MaskedSourceMemoriesDeleted).Should().Be((1, 1));

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        (r.OutgoingOrZeroDeleted, r.TaxIdHashesCleared, r.RawJsonRewritten, r.DescriptionsMasked, r.PreAnchorDeleted, r.AnchorsSet,
            r.ShortMasksReplaced, r.MatchesDeleted, r.FailedBatches).Should().Be((0, 0, 0, 0, 0, 0, 0, 0, 0));
        (r.MaskedSourceHashesCleared, r.MaskedSourceMemoriesDeleted, r.IbanMemoriesReset).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task Lisans_verilirse_yalniz_o_lisans_islenir()
    {
        using var db = NewDb(); var target = Guid.NewGuid(); var other = Guid.NewGuid();
        var backfill = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        db.ObifinConnections.AddRange(Connection(target, backfill, backfill), Connection(other, backfill, backfill));
        var otherShortMask = Tx(other, 12m); otherShortMask.CounterpartyIbanMasked = TestPhone.Digits(7);
        db.BankTransactions.AddRange(Tx(target, 0m), Tx(other, 0m), otherShortMask,
            Tx(other, 9m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(target, apply: true, CancellationToken.None);

        (await db.BankTransactions.CountAsync(t => t.LicenseId == target)).Should().Be(0);
        (await db.BankTransactions.CountAsync(t => t.LicenseId == other)).Should().Be(3);
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == otherShortMask.Id)).CounterpartyIbanMasked
            .Should().Be(otherShortMask.CounterpartyIbanMasked);
        (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == other)).InitialCollectFrom.Should().BeNull();
        (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == target)).InitialCollectFrom
            .Should().Be(new DateOnly(2026, 9, 18));
    }

    [Fact]
    public async Task Uygulama_her_partide_imha_kaydi_yazar_kuru_kosu_yazmaz()
    {
        // KVKK Ö-13: tek seferlik temizlik de kalıcı kayda geçer (tetikleyici cleanup, yapan cli), silmeyle aynı işlemde.
        using var db = NewDb(); var lic = Guid.NewGuid();
        db.BankTransactions.AddRange(Tx(lic, 30m, direction: BankTransactionDirection.Outgoing),
            Tx(lic, 10m, rawJson: """{"Aciklama":"x","Bakiye":"1"}""", taxIdHash: Hex64()));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0, "kuru koşu hiçbir şey yazmaz");

        await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.Cleanup && x.Actor == DataDestruction.CliActor);
        records.Should().Contain(x => x.Target == "BankTransactions" && x.Action == DataDestruction.Actions.Deleted && x.RowCount == 1);
        records.Should().Contain(x => x.Target == "BankTransactions.CounterpartyTaxIdHash" && x.Action == DataDestruction.Actions.Nulled);
        records.Should().Contain(x => x.Target == "BankTransactions.RawJson" && x.Action == DataDestruction.Actions.Masked);
    }

    [Fact]
    public async Task Lisans_verilmeden_kosan_uygulama_imha_kaydini_satirin_lisansi_basina_yazar()
    {
        // Yayıncı kendi verisinin imha kanıtını isteyebilir: lisansları karıştıran parti lisans başına ayrı kayıt yazar,
        // lisanssız (null) kayıt kalmaz.
        using var db = NewDb(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var outgoingA = Tx(a, 30m, direction: BankTransactionDirection.Outgoing);
        db.BankTransactions.AddRange(outgoingA, Tx(a, 0m), Tx(b, 15m, direction: BankTransactionDirection.Outgoing),
            Tx(a, 10m, taxIdHash: Hex64()), Tx(b, 10m, taxIdHash: Hex64()), Tx(b, 11m, taxIdHash: Hex64()),
            Tx(b, 12m, description: $"EFT {TestTckn.NewValid()}"));
        db.PaymentMatches.Add(Match(outgoingA));
        db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = b, WpfCustomerId = Guid.NewGuid(), IbanHash = Hex64(), IbanMasked = "1234…890",
            LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().OnlyContain(x => x.LicenseId == a || x.LicenseId == b);
        IEnumerable<(Guid?, int)> Of(string target) => records.Where(x => x.Target == target).Select(x => (x.LicenseId, x.RowCount));
        Of("BankTransactions").Should().BeEquivalentTo(new[] { ((Guid?)a, 2), ((Guid?)b, 1) });
        Of("PaymentMatches").Should().BeEquivalentTo(new[] { ((Guid?)a, 1) });
        Of("BankTransactions.CounterpartyTaxIdHash").Should().BeEquivalentTo(new[] { ((Guid?)a, 1), ((Guid?)b, 2) });
        Of("BankTransactions.Description").Should().BeEquivalentTo(new[] { ((Guid?)b, 1) });
        Of("CustomerIbanMemories.IbanMasked").Should().BeEquivalentTo(new[] { ((Guid?)b, 1) });
    }

    [Fact]
    public async Task P1_oncesi_IBAN_baginin_baslangici_ogreten_havalenin_islem_tarihine_cekilir()
    {
        // KVKK K-6: P1'den önce öğrenilmiş bağın (LastConfirmedAt boş) süresi öğrenme anından işliyordu. Öğrenme havaleden sonra
        // olur (90 günlük ilk çekim, geç elle eşleme): bağ "son doğrulanmış ödemeden 12 ay"ı aşabilirdi. P1 öncesi kod bağı
        // tazelemiyordu, öğreten havale bilinen tek doğrulama: başlangıç onun işlem tarihi, öğrenme anına kırpılmış (süre hiç
        // uzamaz). Öğreten havale yoksa CreatedAt kalır; tazelenmiş bağa dokunulmaz. Veri silinmez: imha kaydı yazılmaz.
        using var db = NewDb(); var lic = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var source = Tx(lic, 100m, now.AddDays(-80));
        var valor = Tx(lic, 110m, now.AddDays(5)); // ileri tarihli (valör) öğreten havale
        var refreshedSource = Tx(lic, 120m, now.AddDays(-70));
        db.BankTransactions.AddRange(source, valor, refreshedSource);
        CustomerIbanMemory Memory(Guid? sourceId, DateTimeOffset createdAt, DateTimeOffset? lastConfirmedAt = null) => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(), IbanHash = Hex64(), IbanMasked = "TR12…345",
            LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = sourceId, CreatedAt = createdAt,
            LastConfirmedAt = lastConfirmedAt,
        };
        var learnedLate = Memory(source.Id, now.AddDays(-10));
        var learnedFromValor = Memory(valor.Id, now.AddDays(-1));
        var sourceGone = Memory(Guid.NewGuid(), now.AddDays(-10));
        var refreshed = Memory(refreshedSource.Id, now.AddDays(-60), now.AddDays(-5));
        db.CustomerIbanMemories.AddRange(learnedLate, learnedFromValor, sourceGone, refreshed);
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        dry.IbanConfirmationsBackfilled.Should().Be(2);
        (await db.CustomerIbanMemories.AsNoTracking().CountAsync(m => m.LastConfirmedAt == null)).Should().Be(3, "kuru koşu yazmaz");

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.IbanConfirmationsBackfilled.Should().Be(2);
        var starts = await db.CustomerIbanMemories.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.LastConfirmedAt);
        starts[learnedLate.Id].Should().Be(source.OccurredAt);
        starts[learnedFromValor.Id].Should().Be(learnedFromValor.CreatedAt, "ileri tarih öğrenme anına kırpılır");
        starts[sourceGone.Id].Should().BeNull("öğreten havale yoksa öğrenme anı kalır");
        starts[refreshed.Id].Should().Be(refreshed.LastConfirmedAt, "P1'de tazelenmiş bağa dokunulmaz");
        (await db.DataDestructionRecords.AsNoTracking().CountAsync(x => x.Target.StartsWith("CustomerIbanMemories")))
            .Should().Be(0, "veri silinmez");
        (await Job(db).RunAsync(null, apply: true, CancellationToken.None)).IbanConfirmationsBackfilled
            .Should().Be(0, "yeniden koşu idempotent");
    }

    [Fact]
    public async Task Maskeli_kaynakli_ozet_ve_ona_bagli_IBAN_hafizasi_temizlenir()
    {
        // KVKK Ö-7: maskeli IBAN'ın özeti aynı ilk/son karakterleri taşıyan farklı gönderenleri tek özete düşürür. Maskesi
        // maskeli kaynağı ele veren özet, aynı özeti taşıyan bütün hareketlerde boşaltılır; o özetle öğrenilmiş bağ silinir.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var suspect = Hex64(); var clean = Hex64();
        BankTransaction Hashed(string hash, string mask) { var t = Tx(lic, 10m); t.CounterpartyIbanHash = hash; t.CounterpartyIbanMasked = mask; return t; }
        var maskedTx = Hashed(suspect, "TR12…**3");
        var sameHash = Hashed(suspect, "TR12…**3");
        var cleanTx = Hashed(clean, BankHasher.MaskIban(BankHasherTests.TestIban()));
        db.BankTransactions.AddRange(maskedTx, sameHash, cleanTx);
        var cleanMemory = Memory(lic, clean);
        db.CustomerIbanMemories.AddRange(Memory(lic, suspect), cleanMemory);
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        (dry.MaskedSourceHashesCleared, dry.MaskedSourceMemoriesDeleted).Should().Be((2, 1));
        (await db.BankTransactions.CountAsync(t => t.CounterpartyIbanHash != null)).Should().Be(3, "kuru koşu yazmaz");
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(2, "kuru koşu yazmaz");

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.MaskedSourceHashesCleared.Should().Be(2);
        r.MaskedSourceMemoriesDeleted.Should().Be(1);
        dry.Should().BeEquivalentTo(r);
        (await db.BankTransactions.Where(t => t.CounterpartyIbanHash != null).Select(t => t.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { cleanTx.Id });
        (await db.BankTransactions.AsNoTracking().CountAsync(t => t.CounterpartyIbanMasked == BankHasher.ShortValueMask))
            .Should().Be(2, "maske satırda kalır, IBAN maskesi biçiminde olmadığı için bir önceki adım (Ö-6) çevirir");
        (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync()).Should().BeEquivalentTo(new[] { cleanMemory.Id });
        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().ContainSingle(x => x.Target == "BankTransactions.CounterpartyIbanHash").Which
            .Should().Match<DataDestructionRecord>(x => x.Action == DataDestruction.Actions.Nulled && x.RowCount == 2
                && x.LicenseId == lic && x.Trigger == DataDestruction.Triggers.Cleanup);
        records.Should().ContainSingle(x => x.Target == "CustomerIbanMemories").Which
            .Should().Match<DataDestructionRecord>(x => x.Action == DataDestruction.Actions.Deleted && x.RowCount == 1
                && x.LicenseId == lic);
        records.Should().OnlyContain(x => !x.Criterion.Contains(';'));
    }

    [Fact]
    public async Task Kendi_maskesi_maskeli_kaynagi_ele_veren_bag_ogreten_havale_silinmis_olsa_da_silinir()
    {
        // Bağ, öğreten havaleden 12 ay yaşar; havale 180 günde gider. Şüphe yalnız hareketin maskesinden okunsaydı öğreten
        // havalesi silinmiş maskeli kaynaklı bağ kalırdı. Bağın kendi maskesi de aynı değerden: o da şüpheyi taşır. Yer tutucu
        // "?" maske kaynağı bilinmez: dokunulmaz (--reset-iban-memory'ye kalır).
        using var db = NewDb(); var lic = Guid.NewGuid();
        var masked = Memory(lic, Hex64(), mask: "TR**…345");
        var accountNumber = Memory(lic, Hex64(), mask: BankHasher.ShortValueMask);
        var unknown = Memory(lic, Hex64(), mask: "?");
        var clean = Memory(lic, Hex64());
        db.CustomerIbanMemories.AddRange(masked, accountNumber, unknown, clean);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.MaskedSourceMemoriesDeleted.Should().Be(2);
        (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync()).Should().BeEquivalentTo(new[] { unknown.Id, clean.Id });
    }

    [Fact]
    public async Task Reset_iban_memory_yalniz_hafizayi_siler_hareket_ozeti_kalir_kuru_kosuda_yalniz_sayar()
    {
        // KVKK K-7: hareketin özeti kalmalı — IBAN talep aracı (Görev 17) hareketleri yalnız özetle bulur; boşaltılsaydı 180 güne
        // kadar duran veri "yok" görünürdü. Yeniden öğrenmeyi LearnIbanAsync'in CounterpartyIbanHashValid kapısı engeller.
        // Silinecek bağın P1 öncesi başlangıcı yazılmaz: kuru koşu da saymaz (sayılar uygulamanınkiler).
        using var db = NewDb(); var lic = Guid.NewGuid();
        var hash = Hex64();
        var t = Tx(lic, 10m); t.CounterpartyIbanHash = hash; t.CounterpartyIbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban());
        db.BankTransactions.Add(t);
        db.CustomerIbanMemories.Add(Memory(lic, hash, sourceId: t.Id));
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None, resetIbanMemory: true);
        (dry.IbanMemoriesReset, dry.IbanConfirmationsBackfilled).Should().Be((1, 0));
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(1);

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None, resetIbanMemory: true);

        r.IbanMemoriesReset.Should().Be(1);
        dry.Should().BeEquivalentTo(r);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
        (await db.BankTransactions.SingleAsync()).CounterpartyIbanHash.Should().Be(hash, "talep aracı hareketi özetle bulur");
        (await db.BankTransactions.SingleAsync()).CounterpartyIbanMasked.Should().NotBeNull("maske kalır");
        (await db.DataDestructionRecords.AsNoTracking().SingleAsync(x => x.Target == "CustomerIbanMemories"))
            .Should().Match<DataDestructionRecord>(x => x.Action == DataDestruction.Actions.Deleted && x.RowCount == 1
                && x.LicenseId == lic && x.Trigger == DataDestruction.Triggers.Cleanup && x.Actor == DataDestruction.CliActor);
    }

    [Fact]
    public async Task Reset_iban_memory_lisans_verilirse_yalniz_o_lisansin_hafizasini_siler()
    {
        using var db = NewDb(); var target = Guid.NewGuid(); var other = Guid.NewGuid();
        var kept = Memory(other, Hex64());
        db.CustomerIbanMemories.AddRange(Memory(target, Hex64()), kept);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(target, apply: true, CancellationToken.None, resetIbanMemory: true);

        r.IbanMemoriesReset.Should().Be(1);
        (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync()).Should().BeEquivalentTo(new[] { kept.Id });
    }

    [Theory]
    [InlineData("TR12…345", false)]
    [InlineData("DE89…000", false)]
    [InlineData("TR12…**3", true)]
    [InlineData("TR**…345", true)]
    [InlineData("…", true)]
    [InlineData("1234…567", true)]
    [InlineData("1234567", true)]
    [InlineData("?", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Maske_maskeli_kaynagi_ele_verir(string? mask, bool expected)
        => BankDataCleanupJob.LooksMasked(mask).Should().Be(expected);
}
