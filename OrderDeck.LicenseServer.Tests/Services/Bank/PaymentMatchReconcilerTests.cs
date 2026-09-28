using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class PaymentMatchReconcilerTests
{
    /// <summary>Yalnız testte hash üretmek için; bağdaştırıcı hasher istemez (saklı hash'leri karşılaştırır). Anahtar üretilir.</summary>
    private static readonly BankHasher Hasher = new(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"recon-{Guid.NewGuid():N}").Options);

    private static PaymentMatchReconciler Recon(LicenseDbContext db, ILogger<PaymentMatchReconciler>? log = null)
        => new(db, new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance),
            log ?? NullLogger<PaymentMatchReconciler>.Instance);

    private sealed record Seed(Guid LicenseId, Guid ShopperId, Guid WpfCustomerId);

    private static Seed SeedShopper(LicenseDbContext db, string username = "ayse_gul34")
    {
        var lic = Guid.NewGuid(); var shopperId = Guid.NewGuid();
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username, FullName = "Ayse Gul", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(wpf);
        db.Shoppers.Add(new Shopper { Id = shopperId, FullName = "Ayse Gul", Phone = $"+9050{Random.Shared.Next(10000000, 99999999)}", PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-" });
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink { Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = lic, Platform = "youtube", Username = username, WpfCustomerId = wpf.Id, JoinedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        return new Seed(lic, shopperId, wpf.Id);
    }

    private static WpfCustomerProjection OtherCustomer(LicenseDbContext db, Guid lic, string username)
    {
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username, UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(c); db.SaveChanges();
        return c;
    }

    private static BankTransaction Tx(LicenseDbContext db, Guid lic, decimal amount, DateTimeOffset when, string desc, string? ibanHash = null,
        string? code = null)
    {
        var t = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = amount, Currency = "TL", OccurredAt = when, Description = desc, CounterpartyIbanHash = ibanHash, FetchedAt = when,
            TransactionCode = code };
        db.BankTransactions.Add(t); db.SaveChanges();
        return t;
    }

    private static Payment Approved(LicenseDbContext db, Guid lic, Guid shopperId, decimal amount, DateTimeOffset paidAt, string payer = "AYSE GUL")
    {
        var p = new Payment { Id = Guid.NewGuid(), LicenseId = lic, ShopperId = shopperId, PayerName = payer, Amount = amount, PaidAt = paidAt, ReferansNo = $"r-{Guid.NewGuid():N}",
            Status = PaymentStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Payments.Add(p); db.SaveChanges();
        return p;
    }

    [Fact]
    public async Task Oneri_insan_karariyla_ayni_musteriyse_ConfirmedByHuman_ve_iban_ogrenilir()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var when = DateTimeOffset.UtcNow.AddHours(-3);
        var tx = Tx(db, s.LicenseId, 500m, when, "HAVALE ayse_gul34", hash);
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 500m, when.AddMinutes(10));

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        var m = await db.PaymentMatches.SingleAsync();
        m.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        m.PaymentId.Should().Be(payment.Id); m.ActualWpfCustomerId.Should().Be(s.WpfCustomerId); m.DecidedAt.Should().NotBeNull();
        var mem = await db.CustomerIbanMemories.SingleAsync();
        mem.LearnedFrom.Should().Be(IbanMemorySource.HumanApproval); mem.SourceBankTransactionId.Should().Be(tx.Id);
    }

    [Fact]
    public async Task Oneri_farkli_musteriyse_Contradicted()
    {
        using var db = NewDb(); var s = SeedShopper(db, "ayse_gul34");
        OtherCustomer(db, s.LicenseId, "mehmet_k");
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var tx = Tx(db, s.LicenseId, 250m, when, "HAVALE mehmet_k");
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 250m, when);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        (await db.PaymentMatches.SingleAsync()).Status.Should().Be(PaymentMatchStatus.Contradicted);
    }

    [Fact]
    public async Task Oneri_yokken_insan_eslerse_ManualOnly()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var tx = Tx(db, s.LicenseId, 300m, when, "EFT GELEN aciklamasiz");
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 300m, when);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        (await db.PaymentMatches.SingleAsync()).Status.Should().Be(PaymentMatchStatus.ManualOnly);
    }

    [Fact]
    public async Task Aday_hareket_yoksa_gap_yazilir_ve_tekrar_cagri_ikinci_gap_yazmaz()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 999m, DateTimeOffset.UtcNow);
        var recon = Recon(db);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        var gap = await db.PaymentMatchGaps.SingleAsync();
        gap.PaymentId.Should().Be(payment.Id); gap.Reason.Should().Be(PaymentMatchGapReason.NoCandidate); gap.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task Ayni_tutarli_iki_aday_gonderen_adiyla_ayrilir_ayrilamazsa_gap()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var when = DateTimeOffset.UtcNow.AddHours(-2);
        var a = Tx(db, s.LicenseId, 100m, when, "HAVALE AYSE GUL odeme");
        var b = Tx(db, s.LicenseId, 100m, when.AddMinutes(5), "HAVALE MEHMET KAYA odeme");
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(a, CancellationToken.None); await recon.Matcher.MatchAsync(b, CancellationToken.None);

        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 100m, when, payer: "AYSE GUL"), CancellationToken.None);
        (await db.PaymentMatches.SingleAsync(m => m.BankTransactionId == a.Id)).PaymentId.Should().NotBeNull();
        (await db.PaymentMatches.SingleAsync(m => m.BankTransactionId == b.Id)).PaymentId.Should().BeNull();

        var c = Tx(db, s.LicenseId, 200m, when, "EFT 1"); var d = Tx(db, s.LicenseId, 200m, when, "EFT 2");
        await recon.Matcher.MatchAsync(c, CancellationToken.None); await recon.Matcher.MatchAsync(d, CancellationToken.None);
        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 200m, when, payer: "BILINMEYEN"), CancellationToken.None);
        (await db.PaymentMatchGaps.SingleAsync()).Reason.Should().Be(PaymentMatchGapReason.AmbiguousCandidates);
    }

    [Fact]
    public async Task Gecikmeli_gelen_hareket_acik_gap_i_cozer()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-5);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 750m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        (await db.PaymentMatchGaps.SingleAsync()).ResolvedAt.Should().BeNull();

        var late = Tx(db, s.LicenseId, 750m, paidAt.AddHours(4), "HAVALE ayse_gul34");
        await recon.Matcher.MatchAsync(late, CancellationToken.None);
        await recon.TryResolveGapAsync(late, CancellationToken.None);

        var gap = await db.PaymentMatchGaps.SingleAsync();
        gap.ResolvedAt.Should().NotBeNull(); gap.ResolvedBankTransactionId.Should().Be(late.Id);
        var m = await db.PaymentMatches.SingleAsync(x => x.BankTransactionId == late.Id);
        m.PaymentId.Should().Be(payment.Id); m.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
    }

    [Fact]
    public async Task Elle_esleme_ogretir_kaldirma_hafiza_satirini_siler()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var tx = Tx(db, s.LicenseId, 400m, DateTimeOffset.UtcNow, "EFT GELEN", hash);
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);

        await recon.ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);
        (await db.PaymentMatches.SingleAsync()).Status.Should().Be(PaymentMatchStatus.ManualOnly);
        (await db.CustomerIbanMemories.SingleAsync()).LearnedFrom.Should().Be(IbanMemorySource.ManualMatch);

        await recon.UnmatchAsync(s.LicenseId, tx.Id, CancellationToken.None);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0, "geri alma satırı SİLER");
        var m = await db.PaymentMatches.SingleAsync();
        m.Status.Should().Be(PaymentMatchStatus.NoProposal); m.ActualWpfCustomerId.Should().BeNull(); m.DecidedAt.Should().BeNull();
    }

    [Fact]
    public async Task Onayla_baglanan_satir_kaldirilinca_dekont_UnlinkedByAdmin_gap_ine_duser()
    {
        // Onay hareketi dekonta bağladı; admin bağı kaldırır (yanlış hareket). Dekont ölçümden sessizce düşmez: açık gap'e
        // döner. Onayın öğrettiği IBAN silinir; bağdaştırmanın yeniden koşusu (Hangfire tekrarı) hareketi yeniden bağlamaz.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var when = DateTimeOffset.UtcNow.AddHours(-2);
        var tx = Tx(db, s.LicenseId, 410m, when, "HAVALE ayse_gul34", hash);
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 410m, when);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        (await db.PaymentMatches.AsNoTracking().SingleAsync()).PaymentId.Should().Be(payment.Id);

        await recon.UnmatchAsync(s.LicenseId, tx.Id, CancellationToken.None);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.PaymentId.Should().BeNull(); m.ActualWpfCustomerId.Should().BeNull(); m.DecidedAt.Should().BeNull();
        m.Status.Should().Be(PaymentMatchStatus.Proposed, "insan kararı kalkınca öneri yeniden hesaplanır");
        var gap = await db.PaymentMatchGaps.AsNoTracking().SingleAsync();
        gap.PaymentId.Should().Be(payment.Id);
        gap.Reason.Should().Be(PaymentMatchGapReason.UnlinkedByAdmin);
        gap.ResolvedAt.Should().BeNull(); gap.ResolvedBankTransactionId.Should().BeNull();
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0, "onayın öğrettiği IBAN geri alınır");
    }

    [Fact]
    public async Task Gap_cozumuyle_baglanan_satir_kaldirilinca_gap_yeniden_acilir()
    {
        // Onayda hareket yoktu (NoCandidate); geç gelen hareket gap'i çözdü. Admin bu bağı kaldırınca çözülmüş gap bağı
        // kalkmış hareketi göstermeye devam etmez: yeniden açılır, onay anındaki nedeni korunur.
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-5);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 760m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        var late = Tx(db, s.LicenseId, 760m, paidAt.AddHours(4), "HAVALE ayse_gul34");
        await recon.MatchAndResolveGapAsync(late, CancellationToken.None);
        (await db.PaymentMatchGaps.AsNoTracking().SingleAsync()).ResolvedBankTransactionId.Should().Be(late.Id);

        await recon.UnmatchAsync(s.LicenseId, late.Id, CancellationToken.None);

        var gap = await db.PaymentMatchGaps.AsNoTracking().SingleAsync();
        gap.PaymentId.Should().Be(payment.Id);
        gap.ResolvedAt.Should().BeNull(); gap.ResolvedBankTransactionId.Should().BeNull();
        gap.Reason.Should().Be(PaymentMatchGapReason.NoCandidate, "onay anındaki neden korunur");
        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.PaymentId.Should().BeNull(); m.ActualWpfCustomerId.Should().BeNull(); m.DecidedAt.Should().BeNull();
    }

    [Fact]
    public async Task Baska_musteriye_ait_iban_hafizasi_onayla_ezilmez()
    {
        // Bağ doğrulanmış (gönderen adı açıklamada geçiyor): onay IBAN'ı öğretmeye kalkar, ama o IBAN'ın hafızası başka
        // müşterinin. Hafıza ezilmez, çelişki uyarıyla loglanır. Öneri hafızadaki müşteriyi gösterir: karşılaştırma çelişti.
        using var db = NewDb(); var s = SeedShopper(db);
        var other = OtherCustomer(db, s.LicenseId, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = other.Id, IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        var when = DateTimeOffset.UtcNow;
        var tx = Tx(db, s.LicenseId, 50m, when, "EFT GELEN AYSE GUL", hash);
        var log = new LogRecorder<PaymentMatchReconciler>();
        var recon = Recon(db, log); await recon.Matcher.MatchAsync(tx, CancellationToken.None);

        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 50m, when, payer: "AYSE GUL"), CancellationToken.None);

        var mem = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        mem.WpfCustomerId.Should().Be(other.Id, "başka müşterinin hafızası sessizce ezilmez");
        mem.LearnedFrom.Should().Be(IbanMemorySource.ManualMatch); mem.SourceBankTransactionId.Should().BeNull();
        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.ProposedWpfCustomerId.Should().Be(other.Id); m.Status.Should().Be(PaymentMatchStatus.Contradicted);
        log.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("IBAN hafızası çelişkisi");
    }

    [Fact]
    public async Task Baska_musteriye_ait_iban_hafizasi_elle_eslemeyle_ezilmez()
    {
        // Elle eşleme doğrulama aramadan öğretir; IBAN'ın hafızası başka müşterinin ise yine ezilmez, çelişki loglanır.
        // Admin'in kararı satıra yazılır.
        using var db = NewDb(); var s = SeedShopper(db);
        var other = OtherCustomer(db, s.LicenseId, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = other.Id, IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        var tx = Tx(db, s.LicenseId, 60m, DateTimeOffset.UtcNow, "EFT GELEN", hash);
        var log = new LogRecorder<PaymentMatchReconciler>();
        var recon = Recon(db, log); await recon.Matcher.MatchAsync(tx, CancellationToken.None);

        await recon.ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);

        var mem = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        mem.WpfCustomerId.Should().Be(other.Id, "başka müşterinin hafızası sessizce ezilmez");
        mem.LearnedFrom.Should().Be(IbanMemorySource.HumanApproval); mem.SourceBankTransactionId.Should().BeNull();
        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.ActualWpfCustomerId.Should().Be(s.WpfCustomerId); m.Status.Should().Be(PaymentMatchStatus.ManualOnly);
        log.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("IBAN hafızası çelişkisi");
    }

    [Fact]
    public async Task Dogrulanmamis_tek_aday_baglanir_ama_iban_ogretmez()
    {
        // A'nın dekontu onaylandığında A'nın havalesi henüz yok; aynı tutarlı başka bir havale tek aday. Bağ yalnız tutar
        // ve zamana dayanır: öneri A'yı göstermiyor (başka müşteri ya da öneri yok), gönderen adı açıklamada geçmiyor.
        // Hareketin IBAN'ı A'ya öğretilseydi, bağ yanlışsa, o IBAN'ın sonraki havaleleri A'ya önerilirdi.
        using var db = NewDb(); var s = SeedShopper(db);
        OtherCustomer(db, s.LicenseId, "mehmet_k");
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var othersTx = Tx(db, s.LicenseId, 250m, when, "HAVALE mehmet_k", Hasher.HashIban(BankHasherTests.TestIban()));
        var anonymousTx = Tx(db, s.LicenseId, 260m, when, "EFT GELEN", Hasher.HashIban(BankHasherTests.TestIban()));
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(othersTx, CancellationToken.None); await recon.Matcher.MatchAsync(anonymousTx, CancellationToken.None);
        var p1 = Approved(db, s.LicenseId, s.ShopperId, 250m, when);
        var p2 = Approved(db, s.LicenseId, s.ShopperId, 260m, when);

        await recon.ReconcileApprovalAsync(p1, CancellationToken.None);
        await recon.ReconcileApprovalAsync(p2, CancellationToken.None);

        var rows = await db.PaymentMatches.AsNoTracking().ToListAsync();
        var contradicted = rows.Single(m => m.BankTransactionId == othersTx.Id);
        contradicted.PaymentId.Should().Be(p1.Id); contradicted.Status.Should().Be(PaymentMatchStatus.Contradicted);
        var manual = rows.Single(m => m.BankTransactionId == anonymousTx.Id);
        manual.PaymentId.Should().Be(p2.Id); manual.Status.Should().Be(PaymentMatchStatus.ManualOnly);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0, "doğrulanmamış bağ IBAN öğretmez");
    }

    [Fact]
    public async Task Gonderen_adi_aciklamada_gecen_bag_oneri_farkliyken_de_iban_ogretir()
    {
        // Açıklama başka müşterinin kullanıcı adını taşıyor (öneri onu gösterir) ama gönderen adı dekonttakiyle aynı: IBAN
        // dekontun sahibinindir, öğrenilir. Karşılaştırma yine Contradicted.
        using var db = NewDb(); var s = SeedShopper(db);
        OtherCustomer(db, s.LicenseId, "mehmet_k");
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var tx = Tx(db, s.LicenseId, 270m, when, "FAST AYSE GUL mehmet_k", Hasher.HashIban(BankHasherTests.TestIban()));
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);

        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 270m, when, payer: "AYSE GUL"), CancellationToken.None);

        (await db.PaymentMatches.AsNoTracking().SingleAsync()).Status.Should().Be(PaymentMatchStatus.Contradicted);
        var mem = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        mem.WpfCustomerId.Should().Be(s.WpfCustomerId); mem.LearnedFrom.Should().Be(IbanMemorySource.HumanApproval);
    }

    [Fact]
    public async Task Gap_cozumunde_dogrulanmamis_bag_iban_ogretmez()
    {
        // Onayda hareket yoktu; sonradan gelen, aynı tutarlı, gönderen adı ve önerisi olmayan tek hareket gap'i çözer ama
        // IBAN'ı dekontun sahibine öğretmez: başka bir müşterinin havalesi olabilir.
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-5);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 280m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        var late = Tx(db, s.LicenseId, 280m, paidAt.AddHours(1), "EFT GELEN", Hasher.HashIban(BankHasherTests.TestIban()));

        await recon.MatchAndResolveGapAsync(late, CancellationToken.None);

        (await db.PaymentMatchGaps.AsNoTracking().SingleAsync()).ResolvedBankTransactionId.Should().Be(late.Id);
        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.PaymentId.Should().Be(payment.Id); m.Status.Should().Be(PaymentMatchStatus.ManualOnly);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0, "doğrulanmamış bağ IBAN öğretmez");
    }

    [Fact]
    public async Task Elle_baska_musteriye_verilen_hareket_onayda_aday_sayilmaz()
    {
        // Admin hareketi elle X'e verdi; sonra aynı tutarlı, Y'nin dekontu onaylanır. Hareket Y'ye bağlanıp X kararı
        // sessizce ezilmez (IBAN hafızası da X'i gösterirdi): aday değildir, dekont gap'e düşer.
        using var db = NewDb(); var s = SeedShopper(db);
        var other = OtherCustomer(db, s.LicenseId, "mehmet_k");
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var tx = Tx(db, s.LicenseId, 350m, when, "EFT GELEN");
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        await recon.ManualMatchAsync(s.LicenseId, tx.Id, other.Id, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 350m, when);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.ActualWpfCustomerId.Should().Be(other.Id, "admin'in kararı korunur");
        m.PaymentId.Should().BeNull();
        m.Status.Should().Be(PaymentMatchStatus.ManualOnly);
        (await db.PaymentMatchGaps.AsNoTracking().SingleAsync()).Reason.Should().Be(PaymentMatchGapReason.NoCandidate);
    }

    [Fact]
    public async Task Elle_ayni_musteriye_verilen_harekete_onay_dekontu_ekler_karari_korur()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var tx = Tx(db, s.LicenseId, 360m, when, "EFT GELEN");
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        await recon.ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);
        var decidedAt = (await db.PaymentMatches.AsNoTracking().SingleAsync()).DecidedAt;
        var payment = Approved(db, s.LicenseId, s.ShopperId, 360m, when);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.PaymentId.Should().Be(payment.Id);
        m.ActualWpfCustomerId.Should().Be(s.WpfCustomerId);
        m.Status.Should().Be(PaymentMatchStatus.ManualOnly, "elle verilen kararın durumu korunur");
        m.DecidedAt.Should().Be(decidedAt, "karar anı admin'inki kalır");
        (await db.PaymentMatchGaps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Karara_baglanmis_harekete_elle_esleme_reddedilir()
    {
        // Onay hareketi Y'nin dekontuna bağladı: admin onu X'e veremez (satır "Y'nin ödemesi X'in" derdi). Elle verilmiş
        // karar da üstüne yazılmaz. Yeniden karar önce kaldırmadan geçer.
        using var db = NewDb(); var s = SeedShopper(db);
        var other = OtherCustomer(db, s.LicenseId, "mehmet_k");
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var approved = Tx(db, s.LicenseId, 370m, when, "HAVALE ayse_gul34");
        var manual = Tx(db, s.LicenseId, 380m, when, "EFT GELEN");
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(approved, CancellationToken.None); await recon.Matcher.MatchAsync(manual, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 370m, when);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        await recon.ManualMatchAsync(s.LicenseId, manual.Id, s.WpfCustomerId, CancellationToken.None);

        var overApproval = () => recon.ManualMatchAsync(s.LicenseId, approved.Id, other.Id, CancellationToken.None);
        var overManual = () => recon.ManualMatchAsync(s.LicenseId, manual.Id, other.Id, CancellationToken.None);

        await overApproval.Should().ThrowAsync<ObifinValidationException>().WithMessage(PaymentMatchReconciler.AlreadyDecidedMessage);
        await overManual.Should().ThrowAsync<ObifinValidationException>().WithMessage(PaymentMatchReconciler.AlreadyDecidedMessage);
        var rows = await db.PaymentMatches.AsNoTracking().ToListAsync();
        var a = rows.Single(m => m.BankTransactionId == approved.Id);
        a.PaymentId.Should().Be(payment.Id); a.ActualWpfCustomerId.Should().Be(s.WpfCustomerId);
        a.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        var b = rows.Single(m => m.BankTransactionId == manual.Id);
        b.ActualWpfCustomerId.Should().Be(s.WpfCustomerId); b.Status.Should().Be(PaymentMatchStatus.ManualOnly);
    }

    [Fact]
    public async Task Belirsiz_gap_sonradan_gelen_ucuncu_hareketle_cozulmez()
    {
        // Onayda aynı tutarlı iki bağsız aday vardı (AmbiguousCandidates). Sonradan gelen üçüncü hareket gönderen adını
        // taşımıyor: gerçek hareket büyük olasılıkla ilk ikisinden biri, belirsizlik tahminle çözülmez.
        using var db = NewDb(); var s = SeedShopper(db);
        var when = DateTimeOffset.UtcNow.AddHours(-3);
        var a = Tx(db, s.LicenseId, 210m, when, "EFT 1"); var b = Tx(db, s.LicenseId, 210m, when.AddMinutes(5), "EFT 2");
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(a, CancellationToken.None); await recon.Matcher.MatchAsync(b, CancellationToken.None);
        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 210m, when, payer: "BILINMEYEN"), CancellationToken.None);
        (await db.PaymentMatchGaps.SingleAsync()).Reason.Should().Be(PaymentMatchGapReason.AmbiguousCandidates);
        var c = Tx(db, s.LicenseId, 210m, when.AddHours(2), "EFT 3");

        await recon.MatchAndResolveGapAsync(c, CancellationToken.None);

        (await db.PaymentMatchGaps.AsNoTracking().SingleAsync()).ResolvedAt.Should().BeNull();
        (await db.PaymentMatches.AsNoTracking().CountAsync(m => m.PaymentId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Acik_gap_e_ayni_tutarli_iki_gec_hareket_gelirse_gap_acik_kalir()
    {
        // Onayda hareket yoktu (NoCandidate); aynı çekim sayfasında aynı tutarlı iki hareket geldi, ikisi de kayıtlıyken
        // sink onları sırayla işler. Hangisinin dekontun hareketi olduğu bilinmez: ilk işleneni seçmek tahmin olurdu.
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-5);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 220m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        var c = Tx(db, s.LicenseId, 220m, paidAt.AddHours(1), "EFT GELEN");
        var d = Tx(db, s.LicenseId, 220m, paidAt.AddHours(2), "EFT GELEN");

        await recon.MatchAndResolveGapAsync(c, CancellationToken.None);
        await recon.MatchAndResolveGapAsync(d, CancellationToken.None);

        (await db.PaymentMatchGaps.AsNoTracking().SingleAsync()).ResolvedAt.Should().BeNull();
        (await db.PaymentMatches.AsNoTracking().CountAsync(m => m.PaymentId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Elle_esleme_baska_lisansin_hareketini_ve_musterisini_kabul_etmez()
    {
        // Admin sayfası kimlikleri formdan alır: başka lisansın hareketi ya da müşterisi, silinmiş müşteri reddedilir;
        // mesaj admin'e olduğu gibi gösterilebilir (ObifinValidationException).
        using var db = NewDb(); var s = SeedShopper(db);
        var foreign = SeedShopper(db, "baska_yayinci");
        var tx = Tx(db, s.LicenseId, 400m, DateTimeOffset.UtcNow, "EFT GELEN");
        var purged = OtherCustomer(db, s.LicenseId, "silinen_musteri");
        purged.PurgedAt = DateTimeOffset.UtcNow; db.SaveChanges();
        var recon = Recon(db);

        var wrongLicense = () => recon.ManualMatchAsync(foreign.LicenseId, tx.Id, foreign.WpfCustomerId, CancellationToken.None);
        var wrongCustomer = () => recon.ManualMatchAsync(s.LicenseId, tx.Id, foreign.WpfCustomerId, CancellationToken.None);
        var purgedCustomer = () => recon.ManualMatchAsync(s.LicenseId, tx.Id, purged.Id, CancellationToken.None);
        var unmatchWrongLicense = () => recon.UnmatchAsync(foreign.LicenseId, tx.Id, CancellationToken.None);

        await wrongLicense.Should().ThrowAsync<ObifinValidationException>().WithMessage("Hareket bulunamadı.");
        await wrongCustomer.Should().ThrowAsync<ObifinValidationException>().WithMessage("Müşteri bulunamadı.");
        await purgedCustomer.Should().ThrowAsync<ObifinValidationException>().WithMessage("Müşteri bulunamadı.");
        await unmatchWrongLicense.Should().ThrowAsync<ObifinValidationException>().WithMessage("Hareket bulunamadı.");
        (await db.PaymentMatches.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Eslestir_ve_gap_coz_tek_cagrida_oneriyi_yazar_ve_acik_gap_i_kapatir()
    {
        // Sink ve telafi taramasının ortak yolu: öneri yazılır, sonra aynı tutarlı açık gap çözülür.
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-2);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 120m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        var late = Tx(db, s.LicenseId, 120m, paidAt.AddHours(1), "HAVALE ayse_gul34");

        await recon.MatchAndResolveGapAsync(late, CancellationToken.None);

        var m = await db.PaymentMatches.SingleAsync();
        m.ProposedWpfCustomerId.Should().Be(s.WpfCustomerId);
        m.PaymentId.Should().Be(payment.Id);
        (await db.PaymentMatchGaps.SingleAsync()).ResolvedBankTransactionId.Should().Be(late.Id);
    }

    [Fact]
    public async Task Ayni_tutarli_POS_tahsilati_tek_basina_aday_sayilmaz_dekont_gap_e_duser()
    {
        // POS tahsilatı (varsayılan dışlanan kod CCP) müşteri havalesi değildir; eşleştirici ona öneri üretmez. Bağdaştırıcı
        // da onu aday saymaz: tek aday kalsaydı yalnız tutar ve zamanla dekonta bağlanır, ölçümü kirletirdi.
        using var db = NewDb(); var s = SeedShopper(db);
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var pos = Tx(db, s.LicenseId, 330m, when, "POS TAHSILAT", code: "CCP");
        var recon = Recon(db); await recon.Matcher.MatchAsync(pos, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 330m, when);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        (await db.PaymentMatches.AsNoTracking().SingleAsync()).PaymentId.Should().BeNull();
        (await db.PaymentMatchGaps.AsNoTracking().SingleAsync()).Reason.Should().Be(PaymentMatchGapReason.NoCandidate);
    }

    [Fact]
    public async Task Havale_ile_ayni_tutarli_POS_tahsilati_varken_havale_gonderen_adi_gerekmeden_baglanir()
    {
        // POS tahsilatı aday sayılsaydı iki aday kalır, gönderen adı açıklamada geçmediği için daraltılamaz, dekont
        // AmbiguousCandidates gap'ine düşerdi.
        using var db = NewDb(); var s = SeedShopper(db);
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var transfer = Tx(db, s.LicenseId, 340m, when, "EFT GELEN");
        var pos = Tx(db, s.LicenseId, 340m, when.AddMinutes(5), "POS TAHSILAT", code: "CCP");
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(transfer, CancellationToken.None); await recon.Matcher.MatchAsync(pos, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 340m, when, payer: "BILINMEYEN");

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        var rows = await db.PaymentMatches.AsNoTracking().ToListAsync();
        rows.Single(m => m.BankTransactionId == transfer.Id).PaymentId.Should().Be(payment.Id);
        rows.Single(m => m.BankTransactionId == pos.Id).PaymentId.Should().BeNull();
        (await db.PaymentMatchGaps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sonradan_gelen_POS_tahsilati_acik_gap_i_cozmez()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-5);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 350m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        var pos = Tx(db, s.LicenseId, 350m, paidAt.AddHours(1), "POS TAHSILAT", code: "CCP");

        await recon.MatchAndResolveGapAsync(pos, CancellationToken.None);

        var gap = await db.PaymentMatchGaps.AsNoTracking().SingleAsync();
        gap.ResolvedAt.Should().BeNull(); gap.ResolvedBankTransactionId.Should().BeNull();
        (await db.PaymentMatches.AsNoTracking().SingleAsync()).PaymentId.Should().BeNull();
    }

    [Fact]
    public async Task POS_tahsilati_elle_eslenebilir()
    {
        // Dışlama yalnız otomatik aday seçimi içindir; admin'in açık kararı POS tahsilatını da bir müşteriye verebilir.
        // Karşı IBAN'ı ise öğretilmez: POS mutabakatında o genellikle üye iş yeri havuz hesabıdır, müşterinin değil.
        using var db = NewDb(); var s = SeedShopper(db);
        var pos = Tx(db, s.LicenseId, 360m, DateTimeOffset.UtcNow, "POS TAHSILAT", Hasher.HashIban(BankHasherTests.TestIban()), code: "CCP");
        var recon = Recon(db); await recon.Matcher.MatchAsync(pos, CancellationToken.None);

        await recon.ManualMatchAsync(s.LicenseId, pos.Id, s.WpfCustomerId, CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.ActualWpfCustomerId.Should().Be(s.WpfCustomerId); m.Status.Should().Be(PaymentMatchStatus.ManualOnly);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0, "dışlanan hareketin karşı IBAN'ı müşteriye öğretilmez");
    }
}
