using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Shoppers;

/// <summary>
/// KVKK silmesinin eşleşme kanıtı boşaltması ile gölge eşleştirmenin eşzamanlı yazısı — GERÇEK SQL Server'da.
/// <see cref="PaymentMatch.UpdatedAt"/> eşzamanlılık jetonudur: silme satırı okuduktan sonra gölge eşleştirme (çekimin
/// eşleştiricisi, telafi taraması, dekont bağdaştırması, admin kararı) ona yazarsa boşaltmanın UPDATE'i 0 satır etkiler ve
/// bütün silme geri alınır. Silme eşleşmeleri tazeleyip boşaltmayı bir kez yeniden uygular. InMemory'de SaveChanges işlem
/// değildir (çakışmadan önceki yazılar kalır, yeniden deneme onlara takılır); geri almanın kendisi burada gerçek.
/// Aynısı, <c>SalesSync:ScrubOrderTextOnPurge</c> açıkken sipariş metninin boşaltması için: <see cref="Order.SyncVersion"/>
/// jetonu eşzamanlı bir <c>orders/sync</c> güncellemesiyle çakışır (spec 2026-10-13 §9).
/// Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class ShopperPurgeServiceConcurrencyTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public ShopperPurgeServiceConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb(interceptor: null);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Eşleşme satırını değiştiren ilk <paramref name="times"/> SaveChanges'ten hemen önce öteki bağlamın yazısını
    /// koşturur: silmenin okuduğu satır kaydetmeden önce değişmiş olur.</summary>
    private sealed class BeforeMatchUpdate(Func<Task> interleave, int times = 1) : SaveChangesInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired < times && eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Modified))
            {
                Fired++;
                await interleave();
            }
            return result;
        }
    }

    private sealed record Seeded(Guid ShopperId, Guid LicenseId, Guid WpfCustomerId, Guid MatchId, string Marker);

    [Fact]
    public async Task Eszamanli_eslestirme_yazisiyla_carpisan_silme_tazeleyip_kaniti_yine_bosaltir()
    {
        // Silme satırı okuduktan sonra dekont onayı aynı satırı müşteriye bağlar (jeton ilerler). Silme 500 ile geri
        // alınmaz: satır tazelenir, bağdaştırmanın yazdığı karar korunur, kanıt yine boşaltılır, kişisel veri silinir.
        var s = await SeedAsync();
        var paymentId = Guid.NewGuid();
        var link = new BeforeMatchUpdate(() => LinkConcurrentlyAsync(s, paymentId));
        await using var db = NewDb(link);

        var result = await Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        result.Should().NotBeNull();
        link.Fired.Should().Be(1, "yeniden deneme eşzamanlı yazıyla bir kez daha karşılaşmadı");
        await using var verify = NewDb(interceptor: null);
        var row = await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId);
        row.Evidence.Should().BeNull("tazelenen satırın kanıtı yine boşaltılır");
        row.PaymentId.Should().Be(paymentId, "eşzamanlı yazanın kararı ezilmez");
        row.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        (await verify.Shoppers.AsNoTracking().SingleAsync(x => x.Id == s.ShopperId)).FullName.Should().Be("[Silindi]");
        (await verify.ShopperBroadcasterLinks.AsNoTracking().CountAsync(l => l.ShopperId == s.ShopperId)).Should().Be(0);
        (await verify.WpfCustomerProjections.AsNoTracking().SingleAsync(c => c.Id == s.WpfCustomerId)).PurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Yeniden_denemede_ikinci_kez_carpisirsa_istisna_yukari_gider_hicbir_sey_yazilmaz()
    {
        var s = await SeedAsync();
        var link = new BeforeMatchUpdate(() => LinkConcurrentlyAsync(s, Guid.NewGuid()), times: 2);
        await using var db = NewDb(link);

        var act = () => Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        link.Fired.Should().Be(2, "yalnız bir kez yeniden denenir");
        await using var verify = NewDb(interceptor: null);
        (await verify.Shoppers.AsNoTracking().SingleAsync(x => x.Id == s.ShopperId)).FullName.Should().NotBe("[Silindi]",
            "silme tek işlemdir, geri alındı");
        (await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId)).Evidence.Should().Be(s.Marker);
    }

    [Fact]
    public async Task Eszamanli_silinen_hareketle_carpisan_silme_tazeleyip_kisisel_veriyi_yine_siler()
    {
        // KVKK silmesi kişiye bağlı hareketin metnini de boşaltır (K-7), yani hareket satırı da kaydedilir. Silme hareketi
        // okuduktan sonra hareket başka bir yoldan silinirse (saklama işi, hesap reddi, Kaldır ve sil; eşleşme FK kaskadıyla
        // gider) eşleşmenin ve hareketin UPDATE'i 0 satır etkiler. Silme 500 ile geri alınmaz: tazelenen satırlar ayrılır,
        // kişisel verinin geri kalanı yazılır.
        var s = await SeedAsync();
        var remove = new BeforeMatchUpdate(async () =>
        {
            await using var other = NewDb(interceptor: null);
            await other.BankTransactions.Where(t => t.LicenseId == s.LicenseId).ExecuteDeleteAsync();
        });
        await using var db = NewDb(remove);

        var result = await Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        result.Should().NotBeNull();
        remove.Fired.Should().Be(1);
        await using var verify = NewDb(interceptor: null);
        (await verify.Shoppers.AsNoTracking().SingleAsync(x => x.Id == s.ShopperId)).FullName.Should().Be("[Silindi]");
        (await verify.PaymentMatches.AsNoTracking().AnyAsync(m => m.Id == s.MatchId)).Should().BeFalse();
        (await verify.WpfCustomerProjections.AsNoTracking().SingleAsync(c => c.Id == s.WpfCustomerId)).PurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Eszamanli_silinen_iban_hafizasiyla_carpisan_silme_tazeleyip_tamamlanir()
    {
        // Hareketi silen yollar (saklama işi, hesap reddi, Kaldır ve sil) kişinin IBAN hafızasını da siler. Silme hafızayı
        // okuduktan sonra o satır başka yoldan silinirse DELETE 0 satır etkiler. Silme 500 ile geri alınmaz: tazelenen hafıza
        // ayrılır, kişisel verinin geri kalanı yazılır.
        var s = await SeedAsync();
        await SeedIbanMemoryAsync(s, NewIbanHash());
        var remove = new BeforeMatchUpdate(async () =>
        {
            await using var other = NewDb(interceptor: null);
            await other.CustomerIbanMemories.Where(m => m.LicenseId == s.LicenseId).ExecuteDeleteAsync();
        });
        await using var db = NewDb(remove);

        var result = await Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        result.Should().NotBeNull();
        remove.Fired.Should().Be(1);
        await using var verify = NewDb(interceptor: null);
        (await verify.Shoppers.AsNoTracking().SingleAsync(x => x.Id == s.ShopperId)).FullName.Should().Be("[Silindi]");
        (await verify.CustomerIbanMemories.AsNoTracking().CountAsync(m => m.LicenseId == s.LicenseId)).Should().Be(0);
        (await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId)).Evidence.Should().BeNull();
    }

    [Fact]
    public async Task Eszamanli_baglama_silinen_kisiye_iban_ogretirse_o_bag_ve_ozetinden_gelen_hareketin_metni_de_silinir()
    {
        // Silme satırı okuduktan sonra dekont onayı (ya da elle eşleme) satırı silinen kişiye bağlar ve havalenin IBAN'ını öğretir
        // (LearnIbanAsync). Yeniden deneme tazelenen satırın kimliğini boşaltır; öğrenilen bağ da silinmeli, yoksa IBAN ile kişi
        // arasındaki bağ KVKK silmesinden sonra kalırdı. Bağın özetinden gelen öteki hareketin metni de silinir (3b kuralı) ve
        // ikisi de imha kaydına girer.
        var s = await SeedAsync();
        var hash = NewIbanHash();
        var sameIbanTxId = await SeedIncomingAsync(s, hash);
        var paymentId = Guid.NewGuid();
        var link = new BeforeMatchUpdate(async () =>
        {
            await LinkConcurrentlyAsync(s, paymentId);
            await SeedIbanMemoryAsync(s, hash);
        });
        await using var db = NewDb(link);

        var result = await Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        result.Should().NotBeNull();
        link.Fired.Should().Be(1);
        await using var verify = NewDb(interceptor: null);
        (await verify.CustomerIbanMemories.AsNoTracking().CountAsync(m => m.WpfCustomerId == s.WpfCustomerId)).Should().Be(0,
            "eşzamanlı yazanın öğrettiği bağ da silinen kişinindir");
        var row = await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId);
        row.ActualWpfCustomerId.Should().BeNull("tazelenen satırda silinen kişinin kimliği boşaltılır");
        row.PaymentId.Should().Be(paymentId, "eşzamanlı yazanın dekont bağı ezilmez");
        var sameIbanTx = await verify.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == sameIbanTxId);
        (sameIbanTx.Description, sameIbanTx.CounterpartyName).Should().Be(((string?)null, (string?)null));
        sameIbanTx.CounterpartyIbanHash.Should().Be(hash, "tanıma kodu kalır (plan karar 6)");
        var records = await verify.DataDestructionRecords.AsNoTracking().Where(r => r.SubjectId == s.ShopperId).ToListAsync();
        records.Where(r => r.Target == "CustomerIbanMemories").Sum(r => r.RowCount).Should().Be(1);
        records.Where(r => r.Target == "BankTransactions.Description+CounterpartyName+RawJson").Sum(r => r.RowCount)
            .Should().Be(2, "eşleşmenin hareketi ve aynı IBAN'dan gelen öteki hareket");
        result!.BankTransactionsScrubbed.Should().Be(2);
    }

    [Fact]
    public async Task Eszamanli_budanan_denetim_satiriyla_carpisan_silme_tazeleyip_tamamlanir()
    {
        // KVKK silmesi elle eşleme denetiminde kişinin kimliğini de boşaltır (Ö-14), yani denetim satırı da kaydedilir. Silme
        // satırı okuduktan sonra 24 aylık denetim budaması (audit-retention) onu silerse UPDATE 0 satır etkiler. Silme 500 ile
        // geri alınmaz: tazelenen satır ayrılır, kişisel verinin geri kalanı yazılır.
        var s = await SeedAsync();
        var auditId = await SeedManualMatchAuditAsync(s);
        var prune = new BeforeMatchUpdate(async () =>
        {
            await using var other = NewDb(interceptor: null);
            await other.AuditLogs.Where(a => a.Id == auditId).ExecuteDeleteAsync();
        });
        await using var db = NewDb(prune);

        var result = await Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        result.Should().NotBeNull();
        prune.Fired.Should().Be(1);
        await using var verify = NewDb(interceptor: null);
        (await verify.Shoppers.AsNoTracking().SingleAsync(x => x.Id == s.ShopperId)).FullName.Should().Be("[Silindi]");
        (await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId)).Evidence.Should().BeNull();
        (await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Id == auditId)).Should().BeFalse();
    }

    [Fact]
    public async Task Eszamanli_elle_esleme_silinen_kisiye_denetim_yazarsa_yeniden_deneme_onu_da_bosaltir()
    {
        // Silme satırı okuduktan sonra elle eşleme satırı silinen kişiye bağlar, sayfa da kararın ardından denetim satırını yazar.
        // Yeniden deneme tazelenen satırın kimliğini boşaltır; arada yazılmış denetim satırı da kişinin kimliğini taşımamalı.
        // İlk okumanın satırı ve sonradan bulunan satır ayrı kayıt satırlarıyla imha kaydına girer.
        var s = await SeedAsync();
        var earlyId = await SeedManualMatchAuditAsync(s);
        var lateId = Guid.Empty;
        var manual = new BeforeMatchUpdate(async () => lateId = await ManualMatchConcurrentlyAsync(s));
        await using var db = NewDb(manual);

        var result = await Service(db).PurgeAsync(s.ShopperId, CancellationToken.None);

        result.Should().NotBeNull();
        manual.Fired.Should().Be(1);
        await using var verify = NewDb(interceptor: null);
        (await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId)).ActualWpfCustomerId
            .Should().BeNull("tazelenen satırda silinen kişinin kimliği boşaltılır");
        var audits = await verify.AuditLogs.AsNoTracking().Where(a => a.Id == earlyId || a.Id == lateId)
            .ToDictionaryAsync(a => a.Id, a => a.Details);
        audits.Should().HaveCount(2);
        audits[earlyId].Should().BeNull();
        audits[lateId].Should().BeNull("eşzamanlı elle eşlemenin denetim satırı da silinen kişinindir");
        (await verify.DataDestructionRecords.AsNoTracking()
                .Where(r => r.SubjectId == s.ShopperId && r.Target == "AuditLogs.Details").Select(r => r.RowCount).ToListAsync())
            .Should().BeEquivalentTo(new[] { 1, 1 }, "ilk okuma ve yeniden denemenin bulduğu ayrı satırlarda");
    }

    /// <summary>Elle eşleme denetim satırı (Görev 16'dan sonraki biçim: sayfanın yazdığı ayrıntı) eşleşmenin hareketine; ayrı
    /// bağlamda yazılır.</summary>
    private async Task<Guid> SeedManualMatchAuditAsync(Seeded s)
    {
        await using var db = NewDb(interceptor: null);
        var txId = await db.PaymentMatches.Where(m => m.Id == s.MatchId).Select(m => m.BankTransactionId).SingleAsync();
        var row = new AuditLogEntry
        {
            Id = Guid.NewGuid(), EventType = AuditEvents.BankMatchManual, TargetType = AuditTargets.BankTransaction,
            TargetId = txId.ToString(), Details = $"{{\"wpfCustomerId\":\"{s.WpfCustomerId}\"}}", OccurredAt = DateTimeOffset.UtcNow,
            AdminId = Guid.NewGuid(), AdminUsername = "admin",
        };
        db.AuditLogs.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Elle eşleme gibi: satırı ayrı bir bağlamda silinen kişiye bağlar (jeton ilerler), ardından sayfanın denetim
    /// satırını yazar.</summary>
    private async Task<Guid> ManualMatchConcurrentlyAsync(Seeded s)
    {
        await using (var other = NewDb(interceptor: null))
        {
            var m = await other.PaymentMatches.SingleAsync(x => x.Id == s.MatchId);
            m.ActualWpfCustomerId = s.WpfCustomerId;
            m.DecidedAt = DateTimeOffset.UtcNow;
            m.Status = PaymentMatchStatus.ConfirmedByHuman;
            m.UpdatedAt = DateTimeOffset.UtcNow;
            await other.SaveChangesAsync();
        }
        return await SeedManualMatchAuditAsync(s);
    }

    private static string NewIbanHash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    /// <summary>Silinen kişinin bu lisanstaki IBAN hafızası (öğrenilmiş bağ); ayrı bağlamda yazılır.</summary>
    private async Task SeedIbanMemoryAsync(Seeded s, string hash)
    {
        await using var db = NewDb(interceptor: null);
        db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = s.WpfCustomerId, IbanHash = hash, IbanMasked = "TR12…345",
            LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Eşleşmesi olmayan, verilen IBAN özetinden gelen bir havale.</summary>
    private async Task<Guid> SeedIncomingAsync(Seeded s, string hash)
    {
        await using var db = NewDb(interceptor: null);
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = s.LicenseId, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 120m, Currency = "TL",
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-3), Description = "HAVALE", CounterpartyName = "Ad Soyad",
            CounterpartyIbanHash = hash, CounterpartyIbanMasked = "TR12…345", TransactionCode = "FT", FetchedAt = DateTimeOffset.UtcNow,
        };
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return tx.Id;
    }

    /// <summary>Sipariş satırını değiştiren ilk SaveChanges'ten hemen önce öteki bağlamın yazısını koşturur.</summary>
    private sealed class BeforeOrderUpdate(Func<Task> interleave) : SaveChangesInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired == 0 && eventData.Context!.ChangeTracker.Entries<Order>().Any(e => e.State == EntityState.Modified))
            {
                Fired++;
                await interleave();
            }
            return result;
        }
    }

    /// <summary>
    /// Ayar açıkken silme, okuduğu siparişe eşzamanlı bir <c>orders/sync</c> güncellemesi (birim + jeton) yazıldıktan
    /// sonra kaydederse jeton çakışır. Silme 500 ile geri alınmaz: satır tazelenir, eşzamanlı yazanın birimi korunur,
    /// metin yine boşaltılır ve silmenin geri kalanı yazılır.
    /// </summary>
    [Fact]
    public async Task Eszamanli_siparis_gonderimiyle_carpisan_metin_bosaltmasi_tazeleyip_yine_bosaltir()
    {
        var s = await SeedAsync();
        var orderId = Guid.NewGuid();
        await using (var seed = NewDb(interceptor: null))
        {
            seed.Orders.Add(new Order
            {
                Id = orderId, LicenseId = s.LicenseId, CustomerId = s.WpfCustomerId.ToString("N"),
                Platform = "instagram", Username = "ornek_m", DisplayName = "Takma ad", MessageText = "A1 alırım",
                Code = "A1", Price = 150m, AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        var cancelledAt = DateTimeOffset.UtcNow;
        var push = new BeforeOrderUpdate(async () =>
        {
            await using var other = NewDb(interceptor: null);
            var o = await other.Orders.SingleAsync(x => x.Id == orderId);
            o.CancelledAt = cancelledAt;
            o.SyncVersion++;
            await other.SaveChangesAsync();
        });
        await using var db = NewDb(push);

        var result = await Service(db, scrubOrderText: true).PurgeAsync(s.ShopperId, CancellationToken.None);

        result.Should().NotBeNull();
        push.Fired.Should().Be(1);
        await using var verify = NewDb(interceptor: null);
        var row = await verify.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        row.DisplayName.Should().BeNull();
        row.MessageText.Should().BeEmpty();
        row.TextScrubbedAt.Should().NotBeNull();
        row.CancelledAt.Should().Be(cancelledAt, "eşzamanlı yazanın birimi ezilmez");
        row.SyncVersion.Should().Be(1);
        (await verify.Shoppers.AsNoTracking().SingleAsync(x => x.Id == s.ShopperId)).FullName.Should().Be("[Silindi]");
        (await verify.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == s.MatchId)).Evidence.Should().BeNull();
    }

    /// <summary>Dekont onayının bağdaştırması gibi: satırı ayrı bir bağlamda müşteriye bağlar, jetonu ilerletir.</summary>
    private async Task LinkConcurrentlyAsync(Seeded s, Guid paymentId)
    {
        await using var other = NewDb(interceptor: null);
        var m = await other.PaymentMatches.SingleAsync(x => x.Id == s.MatchId);
        m.PaymentId = paymentId;
        m.ActualWpfCustomerId = s.WpfCustomerId;
        m.DecidedAt = DateTimeOffset.UtcNow;
        m.Status = PaymentMatchStatus.ConfirmedByHuman;
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await other.SaveChangesAsync();
    }

    /// <summary>Shopper, lisans, sahipliği kanıtlanmış WPF müşterisi ve ona kullanıcı adı katmanıyla önerilmiş bir hareket.
    /// Kimlik alanları üretilir.</summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var db = NewDb(interceptor: null);
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"p-{Guid.NewGuid():N}@example.com", Name = "Yayinci",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = $"purge-{Guid.NewGuid():N}", SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(), FullName = "Ornek Musteri", Phone = $"+9055{Random.Shared.Next(10000000, 99999999)}",
            PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-", CreatedAt = now, UpdatedAt = now,
        };
        var wpf = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "instagram", Username = "ornek_m", UpdatedAt = now,
        };
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 300m, Currency = "TL",
            OccurredAt = now.AddHours(-1), Description = "HAVALE ornek_m", TransactionCode = "FT", FetchedAt = now,
        };
        var marker = "username=" + BankTextNormalizer.UsernameKey("ornek_m");
        var match = new PaymentMatch
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, BankTransactionId = tx.Id, ProposedWpfCustomerId = wpf.Id,
            Layer = PaymentMatchLayer.UsernameInDescription, Confidence = PaymentMatcher.UsernameConfidence, Evidence = marker,
            Status = PaymentMatchStatus.Proposed, CreatedAt = now, UpdatedAt = now,
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        db.Shoppers.Add(shopper);
        db.WpfCustomerProjections.Add(wpf);
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = license.Id, Platform = "instagram", Username = "ornek_m",
            WpfCustomerId = wpf.Id, JoinedAt = now,
        });
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(match);
        await db.SaveChangesAsync();
        return new Seeded(shopper.Id, license.Id, wpf.Id, match.Id, marker);
    }

    private static ShopperPurgeService Service(LicenseDbContext db, bool scrubOrderText = false)
        => new(db, new StubShopperPaymentStorage(),
            Options.Create(new SalesSyncOptions { ScrubOrderTextOnPurge = scrubOrderText }),
            NullLogger<ShopperPurgeService>.Instance);

    private LicenseDbContext NewDb(IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr);
        if (interceptor is not null) opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }
}
