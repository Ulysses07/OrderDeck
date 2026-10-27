using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class PaymentMatchReconcilerTests
{
    /// <summary>Yalnız testte hash üretmek için; bağdaştırıcı hasher istemez (saklı hash'leri karşılaştırır). Anahtar üretilir.</summary>
    private static readonly BankHasher Hasher = new(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"recon-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    private static PaymentMatchReconciler Recon(LicenseDbContext db, ILogger<PaymentMatchReconciler>? log = null)
        => new(db, new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance),
            Options.Create(new BankOptions()), log ?? NullLogger<PaymentMatchReconciler>.Instance);

    private sealed record Seed(Guid LicenseId, Guid ShopperId, Guid WpfCustomerId);

    private static Seed SeedShopper(LicenseDbContext db, string username = "ornek_musteri34", Guid? licenseId = null)
    {
        var lic = licenseId ?? Guid.NewGuid(); var shopperId = Guid.NewGuid();
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username, FullName = "Ornek Musteri", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(wpf);
        db.Shoppers.Add(new Shopper { Id = shopperId, FullName = "Ornek Musteri", Phone = $"+9050{Random.Shared.Next(10000000, 99999999)}", PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-" });
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
            TransactionCode = code,
            // Testlerin hareketleri Görev 11'den sonraki çekimi temsil eder: özet geçerli IBAN'dan (KVKK Ö-7).
            CounterpartyIbanHashValid = ibanHash is not null };
        db.BankTransactions.Add(t); db.SaveChanges();
        return t;
    }

    private static Payment Approved(LicenseDbContext db, Guid lic, Guid shopperId, decimal amount, DateTimeOffset paidAt, string payer = "ORNEK MUSTERI")
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
        var tx = Tx(db, s.LicenseId, 500m, when, "HAVALE ornek_musteri34", hash);
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
        using var db = NewDb(); var s = SeedShopper(db, "ornek_musteri34");
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
        var a = Tx(db, s.LicenseId, 100m, when, "HAVALE ORNEK MUSTERI odeme");
        var b = Tx(db, s.LicenseId, 100m, when.AddMinutes(5), "HAVALE DENEME ALICI odeme");
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(a, CancellationToken.None); await recon.Matcher.MatchAsync(b, CancellationToken.None);

        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 100m, when, payer: "ORNEK MUSTERI"), CancellationToken.None);
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

        var late = Tx(db, s.LicenseId, 750m, paidAt.AddHours(4), "HAVALE ornek_musteri34");
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
        var tx = Tx(db, s.LicenseId, 410m, when, "HAVALE ornek_musteri34", hash);
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
        var late = Tx(db, s.LicenseId, 760m, paidAt.AddHours(4), "HAVALE ornek_musteri34");
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
        var tx = Tx(db, s.LicenseId, 50m, when, "EFT GELEN ORNEK MUSTERI", hash);
        var log = new LogRecorder<PaymentMatchReconciler>();
        var recon = Recon(db, log); await recon.Matcher.MatchAsync(tx, CancellationToken.None);

        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 50m, when, payer: "ORNEK MUSTERI"), CancellationToken.None);

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
        var tx = Tx(db, s.LicenseId, 270m, when, "FAST ORNEK MUSTERI mehmet_k", Hasher.HashIban(BankHasherTests.TestIban()));
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);

        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 270m, when, payer: "ORNEK MUSTERI"), CancellationToken.None);

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
    public async Task KVKK_silmesiyle_musterisi_bosalan_karar_baska_musterinin_dekontuna_aday_sayilmaz()
    {
        // KVKK silmesi (BankSubjectScrub.MatchForCustomer) sayfada X'e verilmiş kararın müşterisini boşaltır; kararın anı ve
        // sonucu ölçüm için kalır. Karar kilidi DecidedAt'tir: Y'nin aynı tutarlı dekontu onaylanınca (Y'nin havalesi henüz
        // gelmemiş) tek aday X'in havalesi olurdu; bağlansaydı silinen kişinin havalesi Y'nin ödemesi sayılır (m. 4/2-b
        // doğruluk), ölçümün sonucu ezilir, Y'nin gerçek havalesi de dekontu dolu olduğu için bir daha bağlanamazdı. Sabit
        // fiyatlı satışta aynı tutar yaygın. Gap çözümü de o havaleyle Y'nin açık gap'ini çözmez.
        using var db = NewDb(); var x = SeedShopper(db);
        var y = SeedShopper(db, "mehmet_k", x.LicenseId);
        var when = DateTimeOffset.UtcNow.AddHours(-3);
        var xTx = Tx(db, x.LicenseId, 250m, when, "HAVALE ornek_musteri34");
        var recon = Recon(db); await recon.Matcher.MatchAsync(xTx, CancellationToken.None);
        await recon.ManualMatchAsync(x.LicenseId, xTx.Id, x.WpfCustomerId, CancellationToken.None);
        await new ShopperPurgeService(db, new StubShopperPaymentStorage(), Options.Create(new SalesSyncOptions()),
            NullLogger<ShopperPurgeService>.Instance).PurgeAsync(x.ShopperId, CancellationToken.None);
        var purged = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (purged.ActualWpfCustomerId, purged.Status, purged.PaymentId).Should().Be(
            ((Guid?)null, PaymentMatchStatus.ConfirmedByHuman, (Guid?)null), "ön koşul: silme müşteriyi boşalttı, sonucu bıraktı");
        purged.DecidedAt.Should().NotBeNull();
        var payment = Approved(db, x.LicenseId, y.ShopperId, 250m, when.AddMinutes(20), "DENEME ALICI");

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        await recon.TryResolveGapAsync(xTx, CancellationToken.None);

        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.PaymentId.Should().BeNull("silinen kişinin havalesi başka müşterinin ödemesi sayılmaz");
        m.ActualWpfCustomerId.Should().BeNull();
        m.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman, "ölçümün sonucu kalır");
        m.DecidedAt.Should().Be(purged.DecidedAt);
        m.UpdatedAt.Should().Be(purged.UpdatedAt, "satıra yazılmaz");
        var gap = await db.PaymentMatchGaps.AsNoTracking().SingleAsync();
        (gap.PaymentId, gap.Reason, gap.ResolvedAt).Should().Be(
            (payment.Id, PaymentMatchGapReason.NoCandidate, (DateTimeOffset?)null), "Y'nin dekontu gap'te bekler");
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
        var approved = Tx(db, s.LicenseId, 370m, when, "HAVALE ornek_musteri34");
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
        var late = Tx(db, s.LicenseId, 120m, paidAt.AddHours(1), "HAVALE ornek_musteri34");

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

    [Fact]
    public async Task Ayni_musteriye_yeniden_dogrulanan_IBAN_baginin_LastConfirmedAt_tazelenir()
    {
        // KVKK K-6: IBAN bağı o IBAN'dan gelen son doğrulanmış havaleden 12 ay yaşar; süreler havalenin işlem tarihinden işler
        // (politika Bölüm 5). Her doğrulama süreyi doğrulanan havalenin işlem tarihine taşır.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var first = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(-2), "EFT GELEN", hash);
        var second = Tx(db, s.LicenseId, 120m, DateTimeOffset.UtcNow.AddHours(-1), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, first.Id, s.WpfCustomerId, CancellationToken.None);
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt
            .Should().Be(first.OccurredAt, "öğrenen doğrulama da süreyi havalenin işlem tarihinden başlatır, öğrenme anından değil");

        await recon.ManualMatchAsync(s.LicenseId, second.Id, s.WpfCustomerId, CancellationToken.None);

        var memory = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        memory.LastConfirmedAt.Should().Be(second.OccurredAt);
        memory.SourceBankTransactionId.Should().Be(first.Id, "ilk öğreten hareket değişmez");
    }

    [Fact]
    public async Task Daha_eski_havalenin_gec_dogrulanmasi_IBAN_baginin_suresini_geri_almaz_ne_uzatir()
    {
        // Yönetici eski bir havaleyi sonradan elle eşler: bağın süresi o havalenin işlem tarihine geri çekilmez (daha yeni
        // doğrulanmış havale var), eşleme anına da uzamaz (metin "son doğrulanmış havaleden 12 ay" diyor).
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var newer = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(-3), "EFT GELEN", hash);
        var older = Tx(db, s.LicenseId, 120m, DateTimeOffset.UtcNow.AddDays(-40), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, newer.Id, s.WpfCustomerId, CancellationToken.None);

        await recon.ManualMatchAsync(s.LicenseId, older.Id, s.WpfCustomerId, CancellationToken.None);

        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(newer.OccurredAt);
    }

    [Fact]
    public async Task Dekont_onayiyla_dogrulanan_havale_de_IBAN_bagini_tazeler()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var learned = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(-20), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, learned.Id, s.WpfCustomerId, CancellationToken.None);
        var when = DateTimeOffset.UtcNow.AddHours(-3);
        var tx = Tx(db, s.LicenseId, 450m, when, "EFT GELEN", hash);
        await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 450m, when.AddMinutes(10));

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == tx.Id)).PaymentId.Should().Be(payment.Id);
        var memory = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        memory.LastConfirmedAt.Should().Be(tx.OccurredAt, "IBAN hafızasının önerdiği müşteriye onaylanan dekont bağı doğrular");
        memory.SourceBankTransactionId.Should().Be(learned.Id);
    }

    [Fact]
    public async Task Ileri_tarihli_havale_IBAN_baginin_suresini_simdiden_ileri_tasimaz()
    {
        // Valör tarihi ileri bir hareket: bağın saklama başlangıcı şimdiye kırpılır, yoksa 12 ay işlem tarihi kadar geleceğe
        // uzardı. Öğrenmede de tazelemede de.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var future = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(2), "EFT GELEN", hash);
        var later = Tx(db, s.LicenseId, 120m, DateTimeOffset.UtcNow.AddDays(5), "EFT GELEN", hash);
        var recon = Recon(db);
        var before = DateTimeOffset.UtcNow;

        await recon.ManualMatchAsync(s.LicenseId, future.Id, s.WpfCustomerId, CancellationToken.None);

        var learned = (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt;
        learned.Should().NotBeNull();
        learned!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow, "öğrenme anına kırpılır");
        await Task.Delay(20);

        await recon.ManualMatchAsync(s.LicenseId, later.Id, s.WpfCustomerId, CancellationToken.None);

        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt!.Value
            .Should().BeAfter(learned.Value, "ileri tarihli havale de bağı tazeler")
            .And.BeOnOrBefore(DateTimeOffset.UtcNow, "tazeleme de şimdiye kırpılır");
    }

    [Fact]
    public async Task Bagi_tazeleyen_eslemenin_kaldirilmasi_sureyi_kalan_son_dogrulamaya_geri_ceker()
    {
        // KVKK K-6 ("son doğrulanmış ödemeden 12 ay"): son havalenin eşlemesi kaldırılınca o artık doğrulanmış havale değildir;
        // bağın süresi onda kalmaz, kalan en yeni insan kararlı havaleye döner. Son doğrulama olmayan eşlemenin kaldırılması
        // süreyi değiştirmez.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var first = Tx(db, s.LicenseId, 100m, now.AddDays(-40), "EFT GELEN", hash);
        var middle = Tx(db, s.LicenseId, 110m, now.AddDays(-20), "EFT GELEN", hash);
        var last = Tx(db, s.LicenseId, 120m, now.AddDays(-3), "EFT GELEN", hash);
        var recon = Recon(db);
        foreach (var tx in new[] { first, middle, last })
            await recon.ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(last.OccurredAt);

        await recon.UnmatchAsync(s.LicenseId, middle.Id, CancellationToken.None);
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt
            .Should().Be(last.OccurredAt, "kaldırılan son doğrulama değildi");

        await recon.UnmatchAsync(s.LicenseId, last.Id, CancellationToken.None);

        var memory = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        memory.LastConfirmedAt.Should().Be(first.OccurredAt, "kalan tek insan kararı ilk havale; ortadakinin kararı da kalktı");
        memory.SourceBankTransactionId.Should().Be(first.Id);
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0, "geri çekme veri silmez");
    }

    [Fact]
    public async Task Kalan_dogrulanmis_havale_yoksa_bagi_tazeleyen_eslemenin_kaldirilmasi_bagi_siler()
    {
        // Öğreten havale 180 günlük saklama kuralıyla silinmiş; bağın süresini yalnız tazeleyen havale taşıyor. Onun eşlemesi
        // kaldırılınca öğreten havalenin tarihi bilinmez: bağ öğrenme anıyla ya da tahmini bir tarihle tutulursa süre "son
        // doğrulanmış ödemeden 12 ay"ı aşabilir. Bağ silinir.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var source = Tx(db, s.LicenseId, 100m, now.AddDays(-200), "EFT GELEN", hash);
        var refreshing = Tx(db, s.LicenseId, 120m, now.AddDays(-3), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, source.Id, s.WpfCustomerId, CancellationToken.None);
        await recon.ManualMatchAsync(s.LicenseId, refreshing.Id, s.WpfCustomerId, CancellationToken.None);
        db.PaymentMatches.RemoveRange(await db.PaymentMatches.Where(m => m.BankTransactionId == source.Id).ToListAsync());
        db.BankTransactions.Remove(source);
        await db.SaveChangesAsync(); // saklama işinin 180 günlük silmesi

        await recon.UnmatchAsync(s.LicenseId, refreshing.Id, CancellationToken.None);

        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
        // Bu silme bir düzeltme değil, saklama gerekçeli imha: başlangıç artık belirlenemiyor. Silme Yönetmeliği md. 7/3 her
        // silmenin kaydını ister; yöneticinin kimliği gelmediyse yapan "admin".
        var record = await db.DataDestructionRecords.AsNoTracking().SingleAsync();
        (record.LicenseId, record.Target, record.Action, record.RowCount, record.Trigger, record.Actor).Should().Be(
            ((Guid?)s.LicenseId, "CustomerIbanMemories", DataDestruction.Actions.Deleted, 1, DataDestruction.Triggers.Retention,
                DataDestruction.AdminActor));
        record.Criterion.Should().Be("LastConfirmedAt+12ay, kaldırma, kalan doğrulama yok");
    }

    [Fact]
    public async Task Dekonta_bagli_dogrulamanin_kaldirilmasi_da_bag_suresini_geri_ceker()
    {
        // Dekont onayı IBAN hafızasının önerdiği müşteriye bağladı (doğrulanmış): bağın süresi bu havaleye taşındı. Kaldırma
        // dekontu gap'e döndürür ve süreyi kalan doğrulamaya (öğreten elle eşleme) geri çeker.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var learned = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(-20), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, learned.Id, s.WpfCustomerId, CancellationToken.None);
        var when = DateTimeOffset.UtcNow.AddHours(-3);
        var tx = Tx(db, s.LicenseId, 450m, when, "EFT GELEN", hash);
        await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 450m, when.AddMinutes(10));
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        var linked = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == tx.Id);
        (linked.PaymentId, linked.ActualWpfCustomerId).Should().Be(((Guid?)payment.Id, (Guid?)s.WpfCustomerId));
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(tx.OccurredAt);

        await recon.UnmatchAsync(s.LicenseId, tx.Id, CancellationToken.None);

        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(learned.OccurredAt);
        (await db.PaymentMatchGaps.AsNoTracking().SingleAsync()).Reason.Should().Be(PaymentMatchGapReason.UnlinkedByAdmin);
    }

    [Fact]
    public async Task Dislanan_hareketin_eslemesini_kaldirmak_IBAN_bagina_dokunmaz()
    {
        // POS tahsilatı (dışlanan kod) elle eşlenir ama IBAN öğretmez, bağı tazelemez; kaldırılması da bağın süresine
        // dokunmaz. Bağın son doğrulaması 180 günlük kuralla silinmiş öğreten havale: dışlanan hareket son doğrulama sayılsaydı
        // kalan doğrulama bulunamaz, hâlâ geçerli bağ silinirdi.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var source = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(-200), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, source.Id, s.WpfCustomerId, CancellationToken.None);
        db.PaymentMatches.RemoveRange(await db.PaymentMatches.Where(m => m.BankTransactionId == source.Id).ToListAsync());
        db.BankTransactions.Remove(source);
        await db.SaveChangesAsync(); // saklama işinin 180 günlük silmesi
        var pos = Tx(db, s.LicenseId, 130m, DateTimeOffset.UtcNow.AddDays(-1), "POS TAHSILAT", hash, code: "CCP");
        await recon.ManualMatchAsync(s.LicenseId, pos.Id, s.WpfCustomerId, CancellationToken.None);
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(source.OccurredAt);

        await recon.UnmatchAsync(s.LicenseId, pos.Id, CancellationToken.None);

        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(source.OccurredAt);
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0);
    }

    /// <summary>Lisansta başka müşterinin kullanıcı adını taşıyan, bu müşterinin IBAN'ından gelen havaleyi dekonta yalnız tutar ve
    /// zamanla bağlar: kullanıcı adı katmanı IBAN hafızasıyla çatışır (öneri yok), gönderen adı açıklamada geçmez. Bağ kurulur
    /// ve müşteriye verilir, ama IBAN bağını doğrulamaz.</summary>
    private static async Task<BankTransaction> UnconfirmedReceiptLinkAsync(LicenseDbContext db, PaymentMatchReconciler recon, Seed s,
        string hash, decimal amount, DateTimeOffset when)
    {
        var tx = Tx(db, s.LicenseId, amount, when, "HAVALE mehmet_k", hash);
        await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, amount, when, payer: "BILINMEYEN"), CancellationToken.None);
        var m = await db.PaymentMatches.AsNoTracking().SingleAsync(x => x.BankTransactionId == tx.Id);
        (m.ActualWpfCustomerId, m.ProposedWpfCustomerId, m.PaymentId is null).Should().Be(((Guid?)s.WpfCustomerId, (Guid?)null, false),
            "dekont bağı kuruldu, öneri çatışmadan yok");
        return tx;
    }

    [Fact]
    public async Task Dogrulamayan_dekont_bagi_kaldirmada_kalan_dogrulama_sayilmaz()
    {
        // KVKK K-6 "son doğrulanmış ödemeden 12 ay": A bağı öğretti, N dekontla yalnız tutar ve zamanla bağlandı (doğrulamadı,
        // tazelemedi), B elle eşlendi ve bağın başlangıcı oldu. B'nin eşlemesi kaldırılınca başlangıç son gerçek doğrulamaya
        // (A) döner. N insan kararlı olduğu için kalan doğrulama sayılsaydı bağ A'dan ~90 gün fazla yaşardı.
        using var db = NewDb(); var s = SeedShopper(db);
        OtherCustomer(db, s.LicenseId, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var a = Tx(db, s.LicenseId, 100m, now.AddDays(-100), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, a.Id, s.WpfCustomerId, CancellationToken.None);
        var n = await UnconfirmedReceiptLinkAsync(db, recon, s, hash, 210m, now.AddDays(-10));
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(a.OccurredAt, "N tazelemedi");
        var b = Tx(db, s.LicenseId, 120m, now.AddDays(-3), "EFT GELEN", hash);
        await recon.ManualMatchAsync(s.LicenseId, b.Id, s.WpfCustomerId, CancellationToken.None);
        async Task<DateTimeOffset?> MarkOf(BankTransaction t)
            => (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == t.Id)).IbanConfirmedAt;
        (await MarkOf(a)).Should().Be(a.OccurredAt, "öğreten karar işaretlenir");
        (await MarkOf(n)).Should().BeNull("doğrulamayan karar işaretlenmez");
        (await MarkOf(b)).Should().Be(b.OccurredAt, "tazeleyen karar işaretlenir");

        await recon.UnmatchAsync(s.LicenseId, b.Id, CancellationToken.None);

        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(a.OccurredAt);
        (await MarkOf(b)).Should().BeNull("kaldırma işareti boşaltır");
    }

    [Fact]
    public async Task Dogrulamayan_dekont_baginin_kaldirilmasi_gecerli_bagi_silmez()
    {
        // A bağı öğretti, 180 günlük kuralla silindi; bağın başlangıcı A. Daha yeni N dekontla yalnız tutar ve zamanla bağlandı:
        // bağı doğrulamadı. N'nin eşlemesi kaldırılınca N son doğrulama sanılsaydı kalan doğrulama boş çıkar, hâlâ geçerli bağ
        // silinirdi.
        using var db = NewDb(); var s = SeedShopper(db);
        OtherCustomer(db, s.LicenseId, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var a = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(-200), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, a.Id, s.WpfCustomerId, CancellationToken.None);
        db.PaymentMatches.RemoveRange(await db.PaymentMatches.Where(m => m.BankTransactionId == a.Id).ToListAsync());
        db.BankTransactions.Remove(a);
        await db.SaveChangesAsync(); // saklama işinin 180 günlük silmesi
        var n = await UnconfirmedReceiptLinkAsync(db, recon, s, hash, 210m, DateTimeOffset.UtcNow.AddDays(-3));

        await recon.UnmatchAsync(s.LicenseId, n.Id, CancellationToken.None);

        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(a.OccurredAt);
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task P1_oncesi_ogreten_havale_isaretsiz_de_kalan_dogrulama_sayilir()
    {
        // P1'den önce öğrenilmiş bağ: LastConfirmedAt boş, öğreten kararın doğrulama işareti yok (sütun yoktu). Öğreten havale
        // tanımı gereği doğrulamadır. P1'den sonra B bağı tazeledi; B'nin eşlemesi kaldırılınca bağ silinmez, başlangıç öğreten
        // havaleye (işlem tarihi, öğrenme anına kırpılmış) döner.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var source = Tx(db, s.LicenseId, 100m, now.AddDays(-60), "EFT GELEN", hash);
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BankTransactionId = source.Id,
            ActualWpfCustomerId = s.WpfCustomerId, DecidedAt = now.AddDays(-30), Status = PaymentMatchStatus.ManualOnly,
            CreatedAt = now.AddDays(-60), UpdatedAt = now.AddDays(-30) });
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = s.WpfCustomerId,
            IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = source.Id,
            CreatedAt = now.AddDays(-30) });
        db.SaveChanges();
        var b = Tx(db, s.LicenseId, 120m, now.AddDays(-3), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, b.Id, s.WpfCustomerId, CancellationToken.None);
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(b.OccurredAt);

        await recon.UnmatchAsync(s.LicenseId, b.Id, CancellationToken.None);

        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(source.OccurredAt);
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Baska_musteriye_kayitli_bagin_suresi_kaldirmayla_degismez()
    {
        // IBAN'ın bağı başka müşterinin: elle eşleme onu ezmez, tazelemez (çelişki). Kaldırma da o bağa dokunmaz; bağ yalnız
        // IBAN'la aransaydı başka müşterinin bağı geri çekilir ya da silinirdi.
        using var db = NewDb(); var s = SeedShopper(db);
        var other = OtherCustomer(db, s.LicenseId, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var confirmedAt = DateTimeOffset.UtcNow.AddDays(-50);
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = other.Id,
            IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = confirmedAt,
            LastConfirmedAt = confirmedAt });
        db.SaveChanges();
        var tx = Tx(db, s.LicenseId, 140m, DateTimeOffset.UtcNow.AddDays(-3), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);

        await recon.UnmatchAsync(s.LicenseId, tx.Id, CancellationToken.None);

        var memory = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        (memory.WpfCustomerId, memory.LastConfirmedAt).Should().Be((other.Id, (DateTimeOffset?)confirmedAt));
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Kesme_anindan_once_cekilmis_hareketin_ozeti_IBAN_ogretmez()
    {
        // KVKK Ö-7: Görev 11'den önce çekilen hareketin özeti maskeli IBAN'dan üretilmiş olabilir; --reset-iban-memory'den sonra
        // ondan yeniden öğrenilen bağ farklı gönderenleri tek müşteriye bağlardı.
        using var db = NewDb(); var s = SeedShopper(db);
        var tx = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddHours(-1), "EFT GELEN", Hasher.HashIban(BankHasherTests.TestIban()));
        tx.CounterpartyIbanHashValid = false;
        db.SaveChanges();

        await Recon(db).ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);

        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
        (await db.PaymentMatches.AsNoTracking().SingleAsync()).IbanConfirmedAt.Should().BeNull("doğrulama sayılmaz");
    }

    [Fact]
    public async Task Kesme_anindan_once_cekilmis_hareket_mevcut_bagi_tazelemez()
    {
        // Aynı kapı tazelemeyi de keser: eski hareketin özeti maskeli kaynaklı olabilir, "aynı IBAN'dan yeni doğrulama" sayılmaz.
        // Hata yönü erken silmedir (bağ son geçerli doğrulamadan 12 ay yaşar).
        using var db = NewDb(); var s = SeedShopper(db);
        var iban = BankHasherTests.TestIban();
        var hash = Hasher.HashIban(iban)!;
        var confirmedAt = DateTimeOffset.UtcNow.AddDays(-50);
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = s.WpfCustomerId,
            IbanHash = hash, IbanMasked = BankHasher.MaskIban(iban), LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = confirmedAt,
            LastConfirmedAt = confirmedAt });
        db.SaveChanges();
        var tx = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(-3), "EFT GELEN", hash);
        tx.CounterpartyIbanHashValid = false;
        db.SaveChanges();

        await Recon(db).ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);

        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt.Should().Be(confirmedAt);
        (await db.PaymentMatches.AsNoTracking().SingleAsync()).IbanConfirmedAt.Should().BeNull("doğrulama sayılmaz");
    }

    [Fact]
    public async Task Durdurulmus_baglantida_gap_cozulmez_onay_bagdastirilmaz_elle_esleme_reddedilir()
    {
        // KVKK K-6: durdurulmuş bağlantının saklı hareketi 30 günlük beklemede işlenmez: gap çözümü dekontu bağlamaz, yeni onay gap
        // yazmaz, elle eşleme IBAN öğretmez ve LastConfirmedAt'i tazelemez. Bağlantısı hiç olmayan lisans bugünkü gibi (ilk onay).
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-5);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 750m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);   // bağlantı yok: NoCandidate gap'i (bugünkü davranış)
        (await db.PaymentMatchGaps.SingleAsync()).PaymentId.Should().Be(payment.Id);
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BaseUrl = "https://obifin.invalid",
            UserCode = $"u-{Guid.NewGuid():N}", Status = ObifinConnectionStatus.Disabled, DisabledAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        var late = Tx(db, s.LicenseId, 750m, paidAt.AddHours(4), "HAVALE ornek_musteri34", Hasher.HashIban(BankHasherTests.TestIban()));
        var second = Approved(db, s.LicenseId, s.ShopperId, 120m, DateTimeOffset.UtcNow.AddHours(-1));

        await recon.TryResolveGapAsync(late, CancellationToken.None);
        await recon.ReconcileApprovalAsync(second, CancellationToken.None);
        var manual = () => recon.ManualMatchAsync(s.LicenseId, late.Id, s.WpfCustomerId, CancellationToken.None);

        await manual.Should().ThrowAsync<ObifinValidationException>().WithMessage(PaymentMatchReconciler.MatchingStoppedMessage);
        (await db.PaymentMatchGaps.SingleAsync()).ResolvedAt.Should().BeNull("gap çözülmedi");
        (await db.PaymentMatchGaps.AnyAsync(g => g.PaymentId == second.Id)).Should().BeFalse("durdurulmuş bağlantıda yeni gap yok");
        (await db.PaymentMatches.AnyAsync(m => m.PaymentId != null || m.ActualWpfCustomerId != null)).Should().BeFalse();
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Durdurulmus_baglantida_kaldirma_acik_kalir_oneri_yeniden_hesaplanmaz()
    {
        // KVKK K-6: kapı yazan yolları kapatır; kaldırma bağ siler, durdurulmuş bağlantıda da çalışır. Ardından yeniden hesap
        // koşmaz: saklı hareketin açıklamasından ve IBAN hafızasından yeni öneri yazmak durdurulmuş bağlantının verisini işlemek
        // olurdu. Satır öneri yok kalır, UnmatchAsync false döner (sayfa "öneri yeniden hesaplanmadı" der).
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var tx = Tx(db, s.LicenseId, 500m, DateTimeOffset.UtcNow.AddHours(-3), "HAVALE ornek_musteri34", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);   // bağlantısız: bugünkü gibi
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BaseUrl = "https://obifin.invalid",
            UserCode = $"u-{Guid.NewGuid():N}", Status = ObifinConnectionStatus.Disabled, DisabledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(1);

        var recomputed = await recon.UnmatchAsync(s.LicenseId, tx.Id, CancellationToken.None);

        recomputed.Should().BeFalse("durdurulmuş bağlantıda yeniden hesap bilerek atlanır");
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0, "kaldırma öğrettiği bağı siler");
        var row = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (row.ActualWpfCustomerId, row.Status, row.ProposedWpfCustomerId)
            .Should().Be(((Guid?)null, PaymentMatchStatus.NoProposal, (Guid?)null), "açıklamadaki kullanıcı adından öneri yazılmaz");
    }

    [Fact]
    public async Task Kaldirmadan_sonra_yeniden_hesap_etkin_baglantida_oneri_yazar()
    {
        // Kapının karşı yüzü: bağlantı eşleştirmeye açıksa kaldırma öneriyi yeniden hesaplar (açıklamadaki kullanıcı adı).
        using var db = NewDb(); var s = SeedShopper(db);
        var tx = Tx(db, s.LicenseId, 500m, DateTimeOffset.UtcNow.AddHours(-3), "HAVALE ornek_musteri34");
        var recon = Recon(db);
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BaseUrl = "https://obifin.invalid",
            UserCode = $"u-{Guid.NewGuid():N}", Status = ObifinConnectionStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        await recon.ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);

        (await recon.UnmatchAsync(s.LicenseId, tx.Id, CancellationToken.None)).Should().BeTrue();

        var row = await db.PaymentMatches.AsNoTracking().SingleAsync();
        (row.Status, row.ProposedWpfCustomerId).Should().Be((PaymentMatchStatus.Proposed, (Guid?)s.WpfCustomerId));
    }

    // ---- IBAN hafızası değişince aynı IBAN'ın kararsız satırları yeniden değerlendirilir (27.10) ----

    /// <summary>Eşleştiricinin IBAN hafızası yokken hesapladığı kararsız satır. <paramref name="hashValid"/> false: özet KVKK Ö-7
    /// kesme anından (22.10) önce çekilmiş.</summary>
    private static async Task<BankTransaction> UndecidedAsync(LicenseDbContext db, PaymentMatchReconciler recon, Guid lic, string? hash,
        DateTimeOffset when, string desc = "EFT GELEN", bool hashValid = true)
    {
        var tx = Tx(db, lic, 100m, when, desc, hash);
        if (!hashValid) { tx.CounterpartyIbanHashValid = false; db.SaveChanges(); }
        await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        return tx;
    }

    private static Task<PaymentMatch> RowAsync(LicenseDbContext db, BankTransaction tx)
        => db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == tx.Id);

    [Fact]
    public async Task Elle_esleme_IBAN_ogrenince_ayni_IBANdan_gelen_eski_kararsiz_havaleler_bu_musteriye_onerilir()
    {
        // Burak 27.10: "bir ödemeyi eşleştiriyorum ama aynı kişinin diğer ödemeleri eşleşmiyor". Eşleştirici hafızayı yalnız hesap
        // anında okuyordu; bağ öğrenilince aynı IBAN'ın kararsız satırları yeniden değerlendirilir. Kesmeden önce çekilmiş havale de
        // (özeti HashValid=false): geçerli bağın özetine eşitlik aynı tam IBAN demektir. Öneri alır, öğretmez.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var recon = Recon(db);
        var older = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-6));
        var legacy = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-9), hashValid: false);
        (await RowAsync(db, older)).Status.Should().Be(PaymentMatchStatus.NoProposal, "ön koşul: hafıza yokken öneri yok");
        var target = Tx(db, s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", hash);

        var result = await recon.ManualMatchAsync(s.LicenseId, target.Id, s.WpfCustomerId, CancellationToken.None);

        result.Should().Be(new ManualMatchResult(ManualMatchIbanOutcome.Learned, Reevaluated: 2, ProposedToCustomer: 2));
        foreach (var tx in new[] { older, legacy })
        {
            var row = await RowAsync(db, tx);
            (row.Status, row.Layer, row.Confidence, row.ProposedWpfCustomerId).Should().Be(
                (PaymentMatchStatus.Proposed, PaymentMatchLayer.IbanMemory, PaymentMatcher.IbanConfidence, (Guid?)s.WpfCustomerId));
            row.Evidence.Should().Be("iban-memory" + PaymentMatcher.ReevaluationEvidenceSuffix, "sonradan üretilen öneri ayrışır");
            (row.ActualWpfCustomerId, row.DecidedAt, row.PaymentId).Should().Be(
                ((Guid?)null, (DateTimeOffset?)null, (Guid?)null), "otomatik karar yok, yalnız öneri");
        }
        var memory = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        (memory.WpfCustomerId, memory.SourceBankTransactionId).Should().Be((s.WpfCustomerId, (Guid?)target.Id),
            "yeniden değerlendirme öğretmez, zincirleme öğrenme yok");
    }

    [Fact]
    public async Task Yeniden_degerlendirmede_aciklamadaki_kullanici_adi_ayni_musteriyse_guven_artar_baskasiysa_oneri_duser()
    {
        // Eşleştiricinin kuralı aynen: kullanıcı adı + hafıza aynı müşteri → 0.98; çelişirse öneri yok (belirsizlik tahminle
        // çözülmez). Var olan bir kullanıcı adı önerisi bilinçli olarak "öneri yok"a inebilir.
        using var db = NewDb(); var s = SeedShopper(db);
        OtherCustomer(db, s.LicenseId, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var recon = Recon(db);
        var same = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-2), "HAVALE ornek_musteri34");
        var conflicting = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-3), "HAVALE mehmet_k");
        var target = Tx(db, s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", hash);

        var result = await recon.ManualMatchAsync(s.LicenseId, target.Id, s.WpfCustomerId, CancellationToken.None);

        result.Should().Be(new ManualMatchResult(ManualMatchIbanOutcome.Learned, Reevaluated: 2, ProposedToCustomer: 1));
        var both = await RowAsync(db, same);
        (both.Status, both.Confidence, both.Evidence).Should().Be((PaymentMatchStatus.Proposed, PaymentMatcher.BothConfidence,
            "username=ornekmusteri34;iban-memory" + PaymentMatcher.ReevaluationEvidenceSuffix));
        var conflict = await RowAsync(db, conflicting);
        (conflict.Status, conflict.ProposedWpfCustomerId, conflict.Evidence).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null,
            "conflict:username=mehmetk,iban-memory" + PaymentMatcher.ReevaluationEvidenceSuffix));
    }

    [Fact]
    public async Task Yeniden_degerlendirme_insan_kararina_dekonta_baska_lisansa_aciklamasi_silinmise_ve_pencere_disina_dokunmaz()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var other = OtherCustomer(db, s.LicenseId, "mehmet_k");
        var foreign = SeedShopper(db, "baska_yayinci");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var stamp = now.AddDays(-1);
        var recon = Recon(db);
        BankTransaction Decided(PaymentMatchStatus status, Guid? actual, Guid? paymentId, Guid? proposed = null)
        {
            var tx = Tx(db, s.LicenseId, 100m, now.AddDays(-4), "EFT GELEN", hash);
            db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BankTransactionId = tx.Id, Status = status,
                ProposedWpfCustomerId = proposed, ActualWpfCustomerId = actual, DecidedAt = stamp, PaymentId = paymentId,
                Evidence = "no-signal", CreatedAt = stamp, UpdatedAt = stamp });
            db.SaveChanges();
            return tx;
        }
        var untouched = new[]
        {
            Decided(PaymentMatchStatus.ManualOnly, other.Id, null),
            Decided(PaymentMatchStatus.Contradicted, s.WpfCustomerId, null, proposed: other.Id),
            Decided(PaymentMatchStatus.ConfirmedByHuman, s.WpfCustomerId, Guid.NewGuid()),
            // KVKK silmesinin müşterisini boşalttığı karar: kilit DecidedAt'tir.
            Decided(PaymentMatchStatus.ConfirmedByHuman, null, null),
            await UndecidedAsync(db, recon, foreign.LicenseId, hash, now.AddDays(-2)),
            await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-(BankMatchSweepJob.LookbackDays + 1))),
        }.ToList();
        var purged = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-2), "HAVALE ornek_musteri34");
        BankSubjectScrub.Transaction(purged, now); db.SaveChanges(); // saklama ya da kişi silmesi açıklamayı boşalttı
        untouched.Add(purged);
        var before = await db.PaymentMatches.AsNoTracking().ToDictionaryAsync(m => m.BankTransactionId);
        var target = Tx(db, s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", hash);

        var result = await recon.ManualMatchAsync(s.LicenseId, target.Id, s.WpfCustomerId, CancellationToken.None);

        result.Should().Be(new ManualMatchResult(ManualMatchIbanOutcome.Learned));
        foreach (var tx in untouched)
            (await RowAsync(db, tx)).Should().BeEquivalentTo(before[tx.Id], o => o.Excluding(m => m.BankTransaction), tx.Id.ToString());
    }

    [Theory]
    [InlineData(ManualMatchIbanOutcome.LegacyHash)]
    [InlineData(ManualMatchIbanOutcome.Excluded)]
    [InlineData(ManualMatchIbanOutcome.NoValidIban)]
    [InlineData(ManualMatchIbanOutcome.ConflictOtherCustomer)]
    public async Task IBAN_ogrenilmeyen_elle_esleme_yeniden_degerlendirme_tetiklemez_ve_nedenini_soyler(ManualMatchIbanOutcome outcome)
    {
        // Kesmeden önce çekilmiş özet (KVKK Ö-7), dışlanan kod (POS), IBAN'sız ya da maskeli havale (özet yok) ve başka müşteriye
        // kayıtlı IBAN hiçbir şey öğretmez: aynı IBAN'ın satırlarına dokunulmaz. Sayfa nedeni gösterir.
        using var db = NewDb(); var s = SeedShopper(db);
        var other = OtherCustomer(db, s.LicenseId, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var recon = Recon(db);
        var older = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-5));
        var before = await RowAsync(db, older);
        var target = Tx(db, s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", outcome == ManualMatchIbanOutcome.NoValidIban ? null : hash,
            code: outcome == ManualMatchIbanOutcome.Excluded ? "CCP" : null);
        if (outcome == ManualMatchIbanOutcome.LegacyHash) { target.CounterpartyIbanHashValid = false; db.SaveChanges(); }
        if (outcome == ManualMatchIbanOutcome.ConflictOtherCustomer)
        {
            db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = other.Id,
                IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = now });
            db.SaveChanges();
        }

        var result = await recon.ManualMatchAsync(s.LicenseId, target.Id, s.WpfCustomerId, CancellationToken.None);

        result.Should().Be(new ManualMatchResult(outcome));
        (await RowAsync(db, target)).ActualWpfCustomerId.Should().Be(s.WpfCustomerId, "karar her durumda yazılır");
        (await RowAsync(db, older)).Should().BeEquivalentTo(before, o => o.Excluding(m => m.BankTransaction));
        (await db.CustomerIbanMemories.CountAsync(m => m.WpfCustomerId == s.WpfCustomerId)).Should().Be(0);
    }

    [Fact]
    public async Task Bagi_tazeleyen_elle_esleme_IBAN_zaten_kayitli_der_satirlari_yeniden_degerlendirmez()
    {
        // Tazeleme bağı değiştirmez: bağdan sonra hesaplanan satırlar onu zaten gördü, öncekileri telafi taraması yakalar.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var recon = Recon(db);
        var first = Tx(db, s.LicenseId, 120m, now.AddDays(-3), "EFT GELEN", hash);
        await recon.ManualMatchAsync(s.LicenseId, first.Id, s.WpfCustomerId, CancellationToken.None);
        var second = Tx(db, s.LicenseId, 130m, now.AddDays(-1), "EFT GELEN", hash);

        var result = await recon.ManualMatchAsync(s.LicenseId, second.Id, s.WpfCustomerId, CancellationToken.None);

        result.Should().Be(new ManualMatchResult(ManualMatchIbanOutcome.Refreshed));
        (await RowAsync(db, second)).Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman, "satırı hafızayla hesaplanmıştı");
    }

    [Fact]
    public async Task Bagi_silen_kaldirma_o_bagdan_gelen_onerileri_geri_alir()
    {
        // Hatalı bir elle eşleme yanlış önerileri yayar; kaldırma bağı silince o bağdan gelen öneriler de geri alınır. Kullanıcı
        // adı önerisi kalır, yalnız hafıza payı düşer.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var recon = Recon(db);
        var plain = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-5));
        var named = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-4), "HAVALE ornek_musteri34");
        var target = Tx(db, s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", hash);
        await recon.ManualMatchAsync(s.LicenseId, target.Id, s.WpfCustomerId, CancellationToken.None);
        (await RowAsync(db, plain)).Layer.Should().Be(PaymentMatchLayer.IbanMemory, "ön koşul: bağdan öneri aldı");

        (await recon.UnmatchAsync(s.LicenseId, target.Id, CancellationToken.None)).Should().BeTrue();

        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
        var p = await RowAsync(db, plain);
        (p.Status, p.ProposedWpfCustomerId, p.Evidence).Should().Be(
            (PaymentMatchStatus.NoProposal, (Guid?)null, "no-signal" + PaymentMatcher.ReevaluationEvidenceSuffix));
        var n = await RowAsync(db, named);
        (n.Status, n.Confidence, n.Evidence).Should().Be((PaymentMatchStatus.Proposed, PaymentMatcher.UsernameConfidence,
            "username=ornekmusteri34" + PaymentMatcher.ReevaluationEvidenceSuffix));
    }

    [Fact]
    public async Task Durdurulmus_baglantida_kaldirma_ayni_IBANin_satirlarini_yeniden_degerlendirmez()
    {
        // KVKK K-6: durdurulmuş bağlantının saklı verisi işlenmez. Bağ silinir, öbür satırlar olduğu gibi kalır; bağlantı yeniden
        // etkinleşince telafi taraması ("iban-memory" kanıtlı, bağı olmayan satır) onları düzeltir.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var recon = Recon(db);
        var older = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-5));
        var target = Tx(db, s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", hash);
        await recon.ManualMatchAsync(s.LicenseId, target.Id, s.WpfCustomerId, CancellationToken.None);
        var before = await RowAsync(db, older);
        before.Layer.Should().Be(PaymentMatchLayer.IbanMemory);
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BaseUrl = "https://obifin.invalid",
            UserCode = $"u-{Guid.NewGuid():N}", Status = ObifinConnectionStatus.Disabled, DisabledAt = now,
            CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();

        (await recon.UnmatchAsync(s.LicenseId, target.Id, CancellationToken.None)).Should().BeFalse();

        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0, "kaldırma bağı siler");
        (await RowAsync(db, older)).Should().BeEquivalentTo(before, o => o.Excluding(m => m.BankTransaction));
    }

    [Fact]
    public async Task Yeniden_degerlendirme_duserse_karar_ve_ogrenilen_bag_kalir_sonuc_bunu_soyler()
    {
        // Karar kaydedildikten SONRA, denemenin dışında koşar (kaldırmanın yeniden hesabıyla aynı kalıp): düşerse karar geri
        // alınmaz, "çakışma" diye raporlanmaz; günlüğe yalnız kimlik ve istisna türü. Telafi taraması tamamlar.
        var failure = new ReevaluationFailure();
        using var db = NewDb(failure); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var log = new LogRecorder<PaymentMatchReconciler>();
        var recon = Recon(db, log);
        var older = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-5), "EFT GELEN gizli_metin");
        var target = Tx(db, s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", hash);
        await recon.Matcher.MatchAsync(target, CancellationToken.None);
        failure.Armed = true;

        var result = await recon.ManualMatchAsync(s.LicenseId, target.Id, s.WpfCustomerId, CancellationToken.None);

        failure.Fired.Should().Be(1);
        result.Should().Be(new ManualMatchResult(ManualMatchIbanOutcome.Learned, ReevaluationFailed: true));
        (await RowAsync(db, target)).ActualWpfCustomerId.Should().Be(s.WpfCustomerId);
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).SourceBankTransactionId.Should().Be(target.Id);
        (await RowAsync(db, older)).Status.Should().Be(PaymentMatchStatus.NoProposal);
        db.ChangeTracker.Entries<PaymentMatch>().Should().NotContain(e => e.State != EntityState.Unchanged, "düşen yazı izde kalmaz");
        var warning = log.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle().Subject.Message;
        warning.Should().Contain(target.Id.ToString()).And.Contain(nameof(DbUpdateException)).And.NotContain("gizli_metin");
    }

    [Fact]
    public async Task Secimden_sonra_aciklamasi_silinen_hareket_yeniden_degerlendirilmez()
    {
        // Seçim izlenmeden yapılır; tarama yüzlerce satırı sırayla işlerken saklama işi ya da KVKK silmesi açıklamayı boşaltabilir.
        // Seçimdeki açıklamayla hesaplanan öneri silinmiş metnin izini (kullanıcı adı anahtarı) kanıta geri taşırdı.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var recon = Recon(db);
        var tx = await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-2), "HAVALE ornek_musteri34");
        var snapshot = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == tx.Id);
        var before = await RowAsync(db, tx);
        BankSubjectScrub.Transaction(tx, now);
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = s.WpfCustomerId,
            IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = now });
        db.SaveChanges();

        (await recon.ReevaluateUndecidedAsync(snapshot, CancellationToken.None)).Should().BeNull();

        (await RowAsync(db, tx)).Should().BeEquivalentTo(before, o => o.Excluding(m => m.BankTransaction));
    }

    [Fact]
    public async Task Tetik_basina_en_fazla_100_satir_yeniden_degerlendirilir_kalani_taramaya_kalir()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var now = DateTimeOffset.UtcNow;
        var recon = Recon(db);
        for (var i = 0; i <= PaymentMatchReconciler.MaxReevaluationsPerTrigger; i++)
            await UndecidedAsync(db, recon, s.LicenseId, hash, now.AddDays(-5).AddMinutes(i));
        var target = Tx(db, s.LicenseId, 140m, now.AddHours(-2), "EFT GELEN", hash);

        var result = await recon.ManualMatchAsync(s.LicenseId, target.Id, s.WpfCustomerId, CancellationToken.None);

        result.Should().Be(new ManualMatchResult(ManualMatchIbanOutcome.Learned, PaymentMatchReconciler.MaxReevaluationsPerTrigger,
            PaymentMatchReconciler.MaxReevaluationsPerTrigger, More: true));
        (await db.PaymentMatches.CountAsync(m => m.Layer == PaymentMatchLayer.IbanMemory))
            .Should().Be(PaymentMatchReconciler.MaxReevaluationsPerTrigger);
    }

    /// <summary>Kararsız satırın yeniden hesap kaydını düşürür (DB kesintisi benzetimi): satır kayıttan önce de sonra da
    /// kararsız. Elle eşlemenin kendi kaydı kararsız satıra karar yazar, buna uymaz.</summary>
    private sealed class ReevaluationFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Modified
                    && e.Entity.DecidedAt is null && e.OriginalValues.GetValue<DateTimeOffset?>(nameof(PaymentMatch.DecidedAt)) is null))
            {
                Fired++;
                throw new DbUpdateException("DB kesintisi benzetimi");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
