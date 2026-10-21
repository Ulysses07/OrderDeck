using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>BankConnectionTerminator gerçek SQL Server'da (göçlerle kurulmuş şema, FK'ler, CHECK kısıtları): partili yerel silme,
/// bağlantının en son silinmesi (<c>BankConnections</c> → <c>ObifinConnections</c> FK sırası), eşleşme jetonu çakışmasında bir kez
/// yeniden deneme, imha kaydının partiyle aynı işlemde yazılması ve hesap reddinin IBAN bağı süre kuralı (sorgu çevirisi).
/// Korunan alanlar sahte: kimlik çözülmez, Obifin'e gidilmez ("ulaşılamadı").</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankConnectionTerminatorRelationalTests : IAsyncLifetime
{
    private sealed class HookedFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public SaveHookInterceptor Hook { get; } = new();
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }

    private readonly SqlServerContainerFixture _sql;
    private HookedFactory _factory = null!;
    public BankConnectionTerminatorRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

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

    /// <summary>64 hex karakterlik sahte özet (DB CHECK kısıtının biçimi); IBAN değil.</summary>
    private static string NewHash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    /// <summary>Lisans + bağlantı + yerel banka bağlantısı (#501) + onaylı hesap 6 + onaysız hesap 9 + hesap 6'da
    /// <paramref name="transactions"/> hareket (her birinin eşleşmesi) + IBAN bağı + gap. İlk eşleşmenin kimliğini de döner.</summary>
    private async Task<(Guid License, Guid FirstMatchId)> SeedAsync(int transactions)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"bt-{Guid.NewGuid():N}@example.test", Name = "Bt",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now };
        var lic = Guid.NewGuid();
        db.Customers.Add(customer);
        db.Licenses.Add(new License { Id = lic, CustomerId = customer.Id, LicenseKey = "bt-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1) });
        var connId = Guid.NewGuid();
        db.ObifinConnections.Add(new ObifinConnection { Id = connId, LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"u-{Guid.NewGuid():N}@x", PasswordProtected = $"p-{Guid.NewGuid():N}", ApiKeyProtected = $"a-{Guid.NewGuid():N}",
            Status = ObifinConnectionStatus.Verified, CreatedAt = now, UpdatedAt = now });
        var bankConnId = Guid.NewGuid();
        db.BankConnections.Add(new BankConnection { Id = bankConnId, LicenseId = lic, ObifinConnectionId = connId, BankaKodu = "qnb",
            BankaApiId = 501, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now });
        db.BankAccounts.Add(new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, BankConnectionId = bankConnId, ObifinAccountId = 6,
            BankaKodu = "qnb", IbanMasked = "TR12…345", IbanHash = NewHash(), NotificationNote = "bildirim", Currency = "TL",
            RefreshedAt = now, ApprovedAt = now, ApprovedBy = "test" });
        db.BankAccounts.Add(new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 9, BankaKodu = "garanti",
            IbanMasked = "?", Currency = "TL", RefreshedAt = now });
        var firstMatch = Guid.Empty;
        for (var i = 0; i < transactions; i++)
        {
            var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = i + 1, ObifinAccountId = 6, BankaKodu = "qnb",
                Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now,
                Description = "aciklama" };
            var match = new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id,
                Status = PaymentMatchStatus.NoProposal, CreatedAt = now, UpdatedAt = now };
            if (i == 0) firstMatch = match.Id;
            db.BankTransactions.Add(tx);
            db.PaymentMatches.Add(match);
        }
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = NewHash(), IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = now });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(), CreatedAt = now });
        await db.SaveChangesAsync();
        return (lic, firstMatch);
    }

    /// <summary>Yerel silme yalnız durdurulmuş bağlantıda koşar (hizmet sonu işinin seçimi; <c>PurgeLocalDataAsync</c> denetler).</summary>
    private async Task StopAsync(Guid lic)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BankConnectionTerminator>().StopAsync(lic, CancellationToken.None);
    }

    [Fact]
    public async Task Kaldir_ve_sil_gercek_SQL_Serverda_parti_parti_siler_baglanti_en_son_gider_teyit_kaydi_kalir()
    {
        var total = BankConnectionTerminator.BatchSize + 1;
        var (lic, _) = await SeedAsync(total);
        var (other, _) = await SeedAsync(1);

        BankRemovalResult r;
        using (var scope = _factory.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<BankConnectionTerminator>()
                .RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1", CancellationToken.None);

        r.ObifinUnreachable.Should().BeTrue("korunan alanlar sahte: Obifin'e gidilmedi");
        (r.Transactions, r.Matches, r.Memories, r.Gaps, r.Accounts, r.BankConnections).Should().Be((total, total, 1, 1, 2, 1));
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankTransactions.CountAsync(t => t.LicenseId == lic)).Should().Be(0);
        (await db.PaymentMatches.CountAsync(m => m.LicenseId == lic)).Should().Be(0);
        (await db.BankAccounts.CountAsync(a => a.LicenseId == lic)).Should().Be(0);
        (await db.BankConnections.CountAsync(b => b.LicenseId == lic)).Should().Be(0);
        (await db.ObifinConnections.CountAsync(c => c.LicenseId == lic)).Should().Be(0);
        (await db.BankTransactions.CountAsync(t => t.LicenseId == other)).Should().Be(1, "başka lisansa dokunulmaz");
        (await db.ObifinConnections.CountAsync(c => c.LicenseId == other)).Should().Be(1);
        var copy = await db.ObifinCopyDeletions.AsNoTracking().SingleAsync(d => d.LicenseId == lic);
        (copy.Id, copy.RequestedBy, copy.ConfirmedAt).Should().Be((r.ObifinCopyDeletionId!.Value, "yonetici-1", (DateTimeOffset?)null));
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync();
        var txRecords = records.Where(x => x.Target == "BankTransactions").ToList();
        txRecords.Should().HaveCount(2, "iki parti, her biri kendi kaydıyla");
        txRecords.Sum(x => x.RowCount).Should().Be(total);
        records.Where(x => x.Target == "PaymentMatches").Sum(x => x.RowCount).Should().Be(total);
        records.Should().Contain(x => x.Target == "ObifinConnections" && x.RowCount == 1);
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.AdminRemove && x.Actor == "yonetici-1");
    }

    [Fact]
    public async Task Yerel_silme_gercek_SQL_Serverda_kimligi_banka_baglantisini_ve_onayli_hesabi_IBANsiz_tutar_isaret_yazar()
    {
        var (lic, _) = await SeedAsync(3);
        await StopAsync(lic);

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BankConnectionTerminator>()
                .PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankTransactions.CountAsync(t => t.LicenseId == lic)).Should().Be(0);
        (await db.PaymentMatches.CountAsync(m => m.LicenseId == lic)).Should().Be(0);
        var kept = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic);
        (kept.ObifinAccountId, kept.ApprovedAt is not null, kept.IbanMasked, kept.IbanHash, kept.NotificationNote)
            .Should().Be((6L, true, "", (string?)null, (string?)null), "onaylı hesap Kaldır ve sil'in sahiplik kanıtı, IBAN'sız");
        (await db.BankConnections.CountAsync(b => b.LicenseId == lic)).Should().Be(1, "yönetici Obifin tarafını sonra kapatır");
        (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic)).BankDataPurgedAt.Should().NotBeNull();
        (await db.ObifinCopyDeletions.AnyAsync(d => d.LicenseId == lic)).Should().BeFalse("teyit kaydını yalnız Kaldır ve sil açar");
    }

    [Fact]
    public async Task Eszamanli_eslestirme_yazisiyla_carpisan_silme_bir_kez_yeniden_denenir_sayilar_birikir()
    {
        var (lic, firstMatch) = await SeedAsync(2);
        await StopAsync(lic);
        _factory.Hook.BeforeSave = async () =>
        {
            _factory.Hook.BeforeSave = null;   // yalnız ilk parti çakışsın
            using var scope = _factory.Services.CreateScope();
            var other = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var m = await other.PaymentMatches.SingleAsync(x => x.Id == firstMatch);
            m.Evidence = "eşzamanlı yazı";
            m.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await other.SaveChangesAsync();
        };

        BankRemovalResult r;
        try
        {
            using var scope = _factory.Services.CreateScope();
            r = await scope.ServiceProvider.GetRequiredService<BankConnectionTerminator>()
                .PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, CancellationToken.None);
        }
        finally
        {
            _factory.Hook.Reset();
        }

        (r.Transactions, r.Matches).Should().Be((2, 2));
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.PaymentMatches.CountAsync(m => m.LicenseId == lic)).Should().Be(0);
        (await db.DataDestructionRecords.AsNoTracking()
                .Where(x => x.LicenseId == lic && x.Target == "BankTransactions").SumAsync(x => x.RowCount))
            .Should().Be(2, "düşen partinin kaydı işlemle birlikte geri alındı, iki kez sayılmadı");
    }

    [Fact]
    public async Task Hesap_reddi_gercek_SQL_Serverda_parti_parti_siler_dekont_gapi_acar_IBAN_bagi_suresini_kalan_dogrulamaya_ceker()
    {
        // Görev 10 kuralının sorgu çevirisi: silinen küme (reddedilen hesabın bütün hareketleri) kalan doğrulamadan ve öğreten
        // havale denetiminden ifadeyle dışlanır.
        var (lic, _) = await SeedAsync(1);
        var now = DateTimeOffset.UtcNow;
        var customer = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var refreshedHash = NewHash();
        var orphanHash = NewHash();
        var taughtHash = NewHash();
        var total = BankConnectionTerminator.BatchSize + 2;
        Guid account9;
        Guid teacherId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            account9 = (await db.BankAccounts.SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 9)).Id;
            BankTransaction Add(long obifinId, long account, string? hash, DateTimeOffset at, Guid? decidedFor,
                DateTimeOffset? confirmed, Guid? payment = null)
            {
                var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = obifinId, ObifinAccountId = account,
                    BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL",
                    OccurredAt = at, FetchedAt = at, CounterpartyIbanHash = hash, CounterpartyIbanHashValid = hash is not null };
                db.BankTransactions.Add(tx);
                db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id,
                    Status = decidedFor is null ? PaymentMatchStatus.NoProposal : PaymentMatchStatus.ManualOnly,
                    ActualWpfCustomerId = decidedFor, DecidedAt = decidedFor is null ? null : at, IbanConfirmedAt = confirmed,
                    PaymentId = payment, CreatedAt = at, UpdatedAt = at });
                return tx;
            }
            // Hesap 9: iki partiyi dolduran hareketler; biri dekonta bağlı, biri bağ öğretmiş, ikisi başka bağları tazelemiş.
            for (var i = 0; i < total - 4; i++) Add(10_000 + i, 9, null, now, null, null);
            Add(20_001, 9, null, now, customer, null, paymentId);
            var taught = Add(20_002, 9, taughtHash, now.AddDays(-4), customer, now.AddDays(-4));
            Add(20_003, 9, refreshedHash, now.AddDays(-5), customer, now.AddDays(-5));
            Add(20_004, 9, orphanHash, now.AddDays(-3), customer, now.AddDays(-3));
            // Hesap 6: refreshedHash bağını 20 gün önce öğreten havale (kalan doğrulama).
            var teacher = Add(30_001, 6, refreshedHash, now.AddDays(-20), customer, now.AddDays(-20));
            teacherId = teacher.Id;
            db.CustomerIbanMemories.AddRange(
                new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = refreshedHash,
                    IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = teacher.Id,
                    CreatedAt = now.AddDays(-20), LastConfirmedAt = now.AddDays(-5) },
                new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = orphanHash,
                    IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = Guid.NewGuid(),
                    CreatedAt = now.AddDays(-200), LastConfirmedAt = now.AddDays(-3) },
                new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = taughtHash,
                    IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval, SourceBankTransactionId = taught.Id,
                    CreatedAt = now.AddDays(-4), LastConfirmedAt = now.AddDays(-4) });
            await db.SaveChangesAsync();
        }

        BankAccountRejectionResult r;
        using (var scope = _factory.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<BankConnectionTerminator>()
                .RejectAccountAsync(account9, "yonetici-1", CancellationToken.None);

        (r.Transactions, r.Matches, r.Memories, r.GapsReopened).Should().Be((total, total, 2, 1));
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.BankTransactions.CountAsync(t => t.LicenseId == lic && t.ObifinAccountId == 9)).Should().Be(0);
        (await vdb.BankTransactions.CountAsync(t => t.LicenseId == lic && t.ObifinAccountId == 6)).Should().Be(2);
        (await vdb.BankTransactions.AnyAsync(t => t.Id == teacherId)).Should().BeTrue();
        var memories = await vdb.CustomerIbanMemories.AsNoTracking().Where(m => m.LicenseId == lic && m.WpfCustomerId == customer)
            .ToListAsync();
        memories.Should().ContainSingle("öğrettiği bağ ve kalan doğrulaması olmayan bağ silindi")
            .Which.IbanHash.Should().Be(refreshedHash);
        memories[0].LastConfirmedAt!.Value.Should().BeCloseTo(now.AddDays(-20), TimeSpan.FromMilliseconds(1),
            "başlangıç kalan doğrulamaya (hesap 6'nın öğreten havalesi) döner");
        var gap = await vdb.PaymentMatchGaps.AsNoTracking().SingleAsync(g => g.PaymentId == paymentId);
        (gap.Reason, gap.ResolvedAt).Should().Be((PaymentMatchGapReason.UnlinkedByAdmin, (DateTimeOffset?)null));
        var records = await vdb.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync();
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.AccountRejected && x.Actor == "yonetici-1");
        records.Where(x => x.Target == "BankTransactions").Sum(x => x.RowCount).Should().Be(total);
        records.Should().ContainSingle(x => x.Target == "CustomerIbanMemories" && x.Criterion.Contains("kalan doğrulama yok"));
        (await vdb.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == account9)).ApprovedAt.Should().BeNull();
    }
}
