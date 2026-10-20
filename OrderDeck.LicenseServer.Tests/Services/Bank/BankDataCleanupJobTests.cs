using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
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
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).IbanMasked.Should().Be(BankHasher.ShortValueMask);
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
            t.CounterpartyIbanMasked = TestPhone.Digits(7);
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
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        var applied = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        (applied.OutgoingOrZeroDeleted, applied.AnchorsSet, applied.PreAnchorDeleted, applied.PreAnchorSkippedLinked,
            applied.MatchesDeleted).Should().Be((2, 1, 1, 1, 3));
        (applied.TaxIdHashesCleared, applied.RawJsonRewritten, applied.DescriptionsMasked, applied.ShortMasksReplaced)
            .Should().Be((2, 2, 2, 2), "yalnız kalan iki satır (karar verilmiş eski ve çapadan sonraki) daraltılır");
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
        db.BankTransactions.AddRange(
            Tx(lic, 30m, direction: BankTransactionDirection.Outgoing),
            Tx(lic, 10m, description: $"EFT {TestTckn.NewValid()}", rawJson: """{"Aciklama":"x","Bakiye":"1"}""", taxIdHash: Hex64()),
            Tx(lic, 9m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero)),
            withShortMask);
        await db.SaveChangesAsync();
        await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        (r.OutgoingOrZeroDeleted, r.TaxIdHashesCleared, r.RawJsonRewritten, r.DescriptionsMasked, r.PreAnchorDeleted, r.AnchorsSet,
            r.ShortMasksReplaced, r.MatchesDeleted, r.FailedBatches).Should().Be((0, 0, 0, 0, 0, 0, 0, 0, 0));
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
}
