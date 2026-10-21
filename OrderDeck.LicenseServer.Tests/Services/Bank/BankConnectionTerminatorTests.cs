using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK K-6/Ö-9: bağlantıyı durdurma, etkinleştirme, kaldırıp silme. Obifin tarafı lisansa özgü: paylaşılan Obifin
/// kullanıcısında Obifin'e hiç gidilmez, yalnız bu lisansa ait bağlantılar kaldırılır. Otomatik yerel silme Obifin'e gitmez ve
/// "Kaldır ve sil"in sahiplik kanıtını (yerel banka bağlantısı, IBAN'sız onaylı hesap) bırakır. Obifin'e ulaşılamasa da yerel
/// silme yapılır. Yerel silme partilidir; "Kaldır ve sil" Obifin'in yazılı silme teyidini bekleyen
/// kayıt açar; onaysız hesap reddedilip verisi silinir (Ö-10), reddedilen hesabın tazelediği IBAN bağının süresi kalan
/// doğrulamalara geri çekilir (Görev 10 kuralı).</summary>
public sealed class BankConnectionTerminatorTests
{
    /// <summary>Her SaveChanges'in içeriği: silinen hareket sayısı (parti sınırı kanıtı), silinen satır sayısı ve o kayıtta
    /// değişen Obifin bağlantısının durumu ("Kaldır ve sil"in bağlantıyı silmeden önce durdurduğunun kanıtı).</summary>
    private sealed class SaveLogInterceptor : SaveChangesInterceptor
    {
        public List<int> DeletedTransactionsPerSave { get; } = new();
        public List<(int DeletedRows, ObifinConnectionStatus? ConnectionStatus)> Saves { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var tracker = eventData.Context!.ChangeTracker;
            DeletedTransactionsPerSave.Add(tracker.Entries<BankTransaction>().Count(e => e.State == EntityState.Deleted));
            Saves.Add((tracker.Entries().Count(e => e.State == EntityState.Deleted),
                tracker.Entries<ObifinConnection>().Where(e => e.State == EntityState.Modified)
                    .Select(e => (ObifinConnectionStatus?)e.Entity.Status).FirstOrDefault()));
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Hareket silen HER kayıtta eşzamanlı bir eşleştirme yazısını taklit eder (jeton çakışması): yeniden deneme de
    /// çakışır. Sayfa testi de kullanır.</summary>
    internal sealed class TransactionDeleteConflict : SaveChangesInterceptor
    {
        public int Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<BankTransaction>().Any(e => e.State == EntityState.Deleted))
            {
                Thrown++;
                throw new DbUpdateConcurrencyException("eşzamanlı eşleştirme yazısı (test)");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TerminatorObifin : IObifinClient
    {
        public List<ObifinBankConnectionDto> Connections { get; } = new();
        public List<ObifinAccountDto> Accounts { get; } = new();
        public Func<Exception>? ListFails { get; set; }
        /// <summary>Bağlantı listesi istenmeden hemen önce: Obifin çağrısı anındaki yerel durumu okumak için.</summary>
        public Func<Task>? BeforeList { get; set; }
        public HashSet<long> RemoveFails { get; } = new();
        public List<long> Removed { get; } = new();
        /// <summary>Obifin'e giden her çağrı (bağlantı listesi, hesap listesi, kaldırma).</summary>
        public int Calls { get; private set; }

        public async Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c,
            CancellationToken ct = default)
        {
            Calls++;
            if (BeforeList is { } hook) await hook();
            if (ListFails is { } fail) throw fail();
            return Connections.ToList();
        }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default)
        {
            Calls++;
            if (RemoveFails.Contains(id)) return Task.FromException(new ObifinApiException(new[] { "Silinemedi" }));
            Removed.Add(id);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ObifinAccountDto>>(Accounts.ToList());
        }
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        {
            Calls++;
            throw new NotSupportedException();
        }
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-term-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    /// <summary>Kancalı bağlam + aynı InMemory veritabanına kancasız ikinci bağlam üreten fabrika: eşzamanlı yazanı (yönetici
    /// sayfası, hesap yenilemesi) sıraya dayalı olarak taklit eder.</summary>
    private static (LicenseDbContext Db, Func<LicenseDbContext> Other) SharedDb(IInterceptor interceptor)
    {
        var name = $"bank-term-{Guid.NewGuid():N}";
        var root = new InMemoryDatabaseRoot();
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, root)
            .AddInterceptors(interceptor).Options);
        return (db, () => new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, root)
            .Options));
    }

    /// <summary>Üretimdeki gibi: sonlandırıcı ve bağdaştırıcı (IBAN bağı süre kuralı) aynı kapsamlı bağlamı paylaşır.</summary>
    internal static BankConnectionTerminator Terminator(LicenseDbContext db, IObifinClient client)
        => new(db, client, Protection,
            new PaymentMatchReconciler(db,
                new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance),
                Options.Create(new BankOptions()), NullLogger<PaymentMatchReconciler>.Instance),
            NullLogger<BankConnectionTerminator>.Instance);

    private static ObifinConnectionService Service(LicenseDbContext db, IObifinClient client)
        => new(db, client, Protection,
            new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" })),
            Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);

    /// <summary>Obifin hesap listesindeki hesap: <paramref name="id"/> = ObifinAccountId, bağlı olduğu banka bağlantısı
    /// <paramref name="bankaApiId"/>.</summary>
    private static ObifinAccountDto Account(long id, long bankaApiId) => new(id, "qnb", bankaApiId, null, null, "TL", null, null, true, null);

    /// <summary>Gerçek şifreli kimlikle bağlantı (servis üzerinden) + her banka tablosundan birer satır. Hesap 6 ONAYLI (bu
    /// lisansın tahsilat hesabı), hesap 9 onaysız (Obifin panelinden eklenmiş, sahibi belli değil); yerel banka bağlantısı
    /// #501. <paramref name="userCode"/> verilirse o Obifin kullanıcısıyla (paylaşılan kullanıcı senaryosu).</summary>
    private static async Task<Guid> SeedAsync(LicenseDbContext db, IObifinClient client, bool licenseActive = true,
        string? userCode = null)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"t-{Guid.NewGuid():N}@x", Name = "T",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = licenseActive ? DateTimeOffset.UtcNow.AddDays(30) : DateTimeOffset.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var conn = await Service(db, client).UpsertAsync(lic, "", userCode ?? $"api-{Guid.NewGuid():N}@x",
            $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}", CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 1, ObifinAccountId = 6, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now,
            Description = "aciklama" };
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id,
            Status = PaymentMatchStatus.NoProposal, CreatedAt = now, UpdatedAt = now });
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = NewHash(), IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = now });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(), CreatedAt = now });
        db.BankAccounts.AddRange(
            new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 6, BankaKodu = "qnb",
                IbanMasked = "TR12…345", IbanHash = NewHash(), NotificationNote = "bildirim",
                Currency = "TL", RefreshedAt = now, ApprovedAt = now, ApprovedBy = "test" },
            new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 9, BankaKodu = "garanti",
                IbanMasked = "?", Currency = "TL", RefreshedAt = now });
        db.BankConnections.Add(new BankConnection { Id = Guid.NewGuid(), LicenseId = lic, ObifinConnectionId = conn.Id,
            BankaKodu = "qnb", BankaApiId = 501, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return lic;
    }

    /// <summary>64 hex karakterlik sahte özet (DB kısıtının biçimi); IBAN değil.</summary>
    private static string NewHash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    /// <summary>İnsan kararlı (müşteriye verilmiş) havale; <paramref name="ibanConfirmedAt"/> doluysa karar IBAN bağını
    /// doğrulamış (<see cref="PaymentMatch.IbanConfirmedAt"/>). Kaydetmez.</summary>
    private static BankTransaction DecidedTx(LicenseDbContext db, Guid lic, long obifinAccountId, string ibanHash, Guid customer,
        DateTimeOffset occurredAt, DateTimeOffset? ibanConfirmedAt)
    {
        var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1_000, 1_000_000_000),
            ObifinAccountId = obifinAccountId, BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 50m,
            Currency = "TL", OccurredAt = occurredAt, FetchedAt = occurredAt, Description = "havale",
            CounterpartyIbanHash = ibanHash, CounterpartyIbanHashValid = true };
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id,
            Status = PaymentMatchStatus.ManualOnly, ActualWpfCustomerId = customer, DecidedAt = occurredAt,
            IbanConfirmedAt = ibanConfirmedAt, CreatedAt = occurredAt, UpdatedAt = occurredAt });
        return tx;
    }

    [Fact]
    public async Task Durdur_Disabled_ve_DisabledAt_yazar_ikinci_durdurma_tarihi_degistirmez()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);

        await t.StopAsync(lic, CancellationToken.None);
        var first = (await db.ObifinConnections.AsNoTracking().SingleAsync()).DisabledAt;
        await t.StopAsync(lic, CancellationToken.None);

        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledAt.Should().NotBeNull().And.Be(first);
        conn.DisabledByAdmin.Should().BeTrue();
        conn.LastError.Should().Be(BankConnectionTerminator.StoppedByAdminMessage);
    }

    [Fact]
    public async Task Lisans_bitisiyle_durmus_baglantiyi_Durdur_yonetici_kararina_cevirir_tarih_degismez()
    {
        // Lisans yenilenince kendiliğinden açılan bağlantı (Görev 14) yöneticinin durdurduğu bağlantı olmamalı.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client, licenseActive: false);
        var disabledAt = DateTimeOffset.UtcNow.AddDays(-3);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Disabled; conn.DisabledAt = disabledAt;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        await Terminator(db, client).StopAsync(lic, CancellationToken.None);

        var after = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        after.DisabledByAdmin.Should().BeTrue();
        after.DisabledAt.Should().Be(disabledAt, "30 günlük sayaç baştan başlamaz");
    }

    [Fact]
    public async Task Etkinlestir_lisans_gecerliyse_Unverified_yapar_bitmisse_reddeder()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        var active = await SeedAsync(db, client);
        var ended = await SeedAsync(db, client, licenseActive: false);
        var t = Terminator(db, client);
        await t.StopAsync(active, CancellationToken.None);
        await t.StopAsync(ended, CancellationToken.None);

        await t.ResumeAsync(active, CancellationToken.None);
        var act = () => t.ResumeAsync(ended, CancellationToken.None);

        var resumed = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == active);
        resumed.Status.Should().Be(ObifinConnectionStatus.Unverified);
        resumed.DisabledAt.Should().BeNull();
        resumed.BankDataPurgedAt.Should().BeNull();
        resumed.DisabledByAdmin.Should().BeFalse();
        resumed.LastError.Should().BeNull();
        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.LicenseStillEndedMessage);
        (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == ended)).Status
            .Should().Be(ObifinConnectionStatus.Disabled);
    }

    [Fact]
    public async Task Kaldir_yalniz_bu_lisansa_ait_baglantilari_kaldirir_digerini_elle_kontrole_birakir_yerel_veriyi_siler()
    {
        // KVKK K-6/Ö-9 + Faz 2 spec §13: Obifin kullanıcısının altında başka lisansın (ya da sahibi belirsiz) bankası olabilir.
        // Yalnız yerel BankConnections (#501) ve ONAYLI hesabın bankası (#600) kaldırılır; onaysız hesabın bankası (#777)
        // Obifin'de kalır ve elle kontrole bırakılır.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        client.Connections.Add(new ObifinBankConnectionDto(600, "qnb", "Panelden", true));
        client.Connections.Add(new ObifinBankConnectionDto(777, "garanti", "Bilinmeyen", true));
        client.Accounts.Add(Account(6, 600));
        client.Accounts.Add(Account(9, 777));
        var lic = await SeedAsync(db, client);
        var other = await SeedAsync(db, client);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin",
            CancellationToken.None);

        client.Removed.Should().BeEquivalentTo(new[] { 501L, 600L });
        r.ObifinRemoved.Should().BeEquivalentTo(new[] { 501L, 600L });
        r.ObifinUnowned.Should().BeEquivalentTo(new[] { 777L }, "bu lisansa ait olduğu doğrulanamayan bağlantıya dokunulmaz");
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("#777").And.Contain("elle kontrol");
        (r.Transactions, r.Matches, r.Memories, r.Gaps, r.Accounts, r.BankConnections).Should().Be((1, 1, 1, 1, 2, 1));
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse("Obifin kimliği de silinir");
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse();
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == other)).Should().BeTrue("başka lisansa dokunulmaz");
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == other)).Should().BeTrue();
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync();
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.AdminRemove && x.Actor == "admin");
        records.Select(x => x.Target).Should().Contain(new[] { "BankTransactions", "PaymentMatches", "CustomerIbanMemories",
            "PaymentMatchGaps", "BankAccounts", "BankConnections", "ObifinConnections" });
    }

    [Fact]
    public async Task Paylasilan_Obifin_kullanicisinda_Obifine_hic_gidilmez_baska_lisansa_dokunulmaz()
    {
        // Faz 2 spec §13 (A): tek paylaşılan Obifin kullanıcısı. Bir lisansın (test/demo ya da başka yayıncı) kaldırılması
        // EMAR'ın QNB bağlantısını Obifin'den silmemeli: QNB API'si için yeniden başvuru haftalar sürer.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        client.Accounts.Add(Account(6, 501));
        var sharedUser = $"api-{Guid.NewGuid():N}@x";
        var lic = await SeedAsync(db, client, userCode: sharedUser);
        var emar = await SeedAsync(db, client, userCode: sharedUser);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin",
            CancellationToken.None);

        client.Calls.Should().Be(0, "paylaşılan Obifin kullanıcısında Obifin'e hiç gidilmez");
        client.Removed.Should().BeEmpty();
        r.SharedObifinUser.Should().BeTrue();
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("başka bir lisansta da kayıtlı");
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse("yerel silme yine yapılır");
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse();
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == emar)).Should().BeTrue();
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == emar)).Should().BeTrue();
        (await db.BankConnections.AnyAsync(b => b.LicenseId == emar && b.BankaApiId == 501)).Should().BeTrue();
    }

    [Fact]
    public async Task Obifine_ulasilamazsa_yerel_silme_yine_yapilir_elle_islem_istenir()
    {
        // "Kaldır ve sil" yalnız yöneticinin eylemi (karar 12): tetikleyici admin-remove, yapan yöneticinin kimliği.
        using var db = NewDb(); var client = new TerminatorObifin { ListFails = () => new HttpRequestException("ağ") };
        var lic = await SeedAsync(db, client);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1",
            CancellationToken.None);

        r.ObifinUnreachable.Should().BeTrue();
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("ulaşılamadı");
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse();
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse();
    }

    [Fact]
    public async Task Kaldirilamayan_baglanti_sonucta_listelenir()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        client.Connections.Add(new ObifinBankConnectionDto(502, "qnb", "QNB-2", true));
        client.Accounts.Add(Account(6, 502));   // onaylı hesabın bankası: bu lisansın
        client.RemoveFails.Add(502);
        var lic = await SeedAsync(db, client);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin",
            CancellationToken.None);

        r.ObifinRemoved.Should().BeEquivalentTo(new[] { 501L });
        r.ObifinFailed.Should().BeEquivalentTo(new[] { 502L });
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("#502");
        // Elle iş bildirimde ve 24 aylık denetim ayrıntısında kalmaz: Obifin kimliği ve sahiplik kanıtı silindi, bildirimi kaçıran
        // yönetici bekleyenler tablosunda ve hatırlatmada Obifin'de hâlâ çekim yapan bağlantıyı görür (karar 21).
        (await db.ObifinCopyDeletions.AsNoTracking().SingleAsync(d => d.LicenseId == lic)).ManualAction
            .Should().Be(r.ManualActionText());
    }

    [Fact]
    public async Task Elle_is_yoksa_teyit_kaydinin_elle_is_metni_bos_kalir()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        var lic = await SeedAsync(db, client);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1",
            CancellationToken.None);

        r.NeedsManualObifinAction.Should().BeFalse();
        (await db.ObifinCopyDeletions.AsNoTracking().SingleAsync(d => d.LicenseId == lic)).ManualAction
            .Should().BeNull("yalnız yazılı teyit bekleniyor");
    }

    [Fact]
    public async Task Kaldir_ve_sil_once_baglantiyi_tek_kayitla_durdurur_sonra_Obifine_gider_ve_siler()
    {
        // Partili silme saniyeler sürer, yönetici sayfası obifin-poll kilidini almaz. Bağlantı en son adıma kadar Verified
        // kalsaydı arada koşan çekim turunun kaydı kimlik korumasından geçer (UpdatedAt değişmemiş), sink ve telafi taraması
        // yazar; döngüden sonra yazılan satır bağlantısız lisansta, imha kaydı dışında kalırdı. İlk kayıt yalnız durdurmadır,
        // Obifin çağrısı anında depodaki bağlantı durdurulmuştur.
        var log = new SaveLogInterceptor();
        using var db = NewDb(log); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        var lic = await SeedAsync(db, client);
        var seeded = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        seeded.Status = ObifinConnectionStatus.Verified;
        seeded.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        ObifinConnection? atObifinCall = null;
        client.BeforeList = async () =>
            atObifinCall = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        log.Saves.Clear();

        await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin",
            CancellationToken.None);

        log.Saves[0].Should().Be((0, (ObifinConnectionStatus?)ObifinConnectionStatus.Disabled),
            "ilk kayıt yalnız durdurma: hiçbir satır silinmeden");
        atObifinCall.Should().NotBeNull();
        (atObifinCall!.Status, atObifinCall.DisabledByAdmin).Should().Be((ObifinConnectionStatus.Disabled, true));
        atObifinCall.DisabledAt.Should().NotBeNull("silme yarıda düşerse hizmet sonu sayacı işler");
        atObifinCall.UpdatedAt.Should().BeAfter(before.UpdatedAt, "yarıdaki çekim turunun kimlik koruması kaydını düşürsün");
        log.Saves.Skip(1).Should().Contain(s => s.DeletedRows > 0, "silme durdurmadan sonra");
        client.Removed.Should().Equal(501L);
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse();
    }

    [Fact]
    public async Task Yerel_silme_Obifine_gitmez_kimligi_banka_baglantisini_ve_onayli_hesabi_IBANsiz_tutar_isaret_yazar()
    {
        // Otomatik hizmet sonu işi (Görev 14) bankaapi/sil çağırmaz: Obifin tarafını yönetici "Kaldır ve sil" ile kapatır. O da
        // bu lisansa ait bağlantıyı yerel BankConnections'tan ve ONAYLI hesaptan bilir: ikisi kalır. Onaylı hesabın kişisel veri
        // taşıyabilen alanları boşalır; onaysız hesap (9) kanıt değildir, silinir.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        var lic = await SeedAsync(db, client);
        await Terminator(db, client).StopAsync(lic, CancellationToken.None);   // iş yalnız durdurulmuş bağlantıyı siler

        var r = await Terminator(db, client).PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd,
            DataDestruction.SystemActor, CancellationToken.None);

        client.Calls.Should().Be(0);
        r.ObifinNotAttempted.Should().BeTrue();
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("Kaldır ve sil");
        r.ObifinCopyDeletionId.Should().BeNull("teyit kaydını yalnız Kaldır ve sil açar");
        (r.Transactions, r.Matches, r.Memories, r.Gaps, r.Accounts, r.AccountsScrubbed, r.BankConnections)
            .Should().Be((1, 1, 1, 1, 1, 1, 0));
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.BankDataPurgedAt.Should().NotBeNull();
        (await db.BankConnections.AnyAsync(b => b.LicenseId == lic && b.BankaApiId == 501)).Should().BeTrue("yönetici sonra kaldırır");
        var kept = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic);
        (kept.ObifinAccountId, kept.ApprovedAt is not null, kept.IbanMasked, kept.IbanHash, kept.NotificationNote)
            .Should().Be((6L, true, "", (string?)null, (string?)null), "onaylı hesap Kaldır ve sil'in sahiplik kanıtı; IBAN'ı kalmaz");
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse();
        (await db.ObifinCopyDeletions.AnyAsync()).Should().BeFalse();
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync();
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.ServiceEnd)
            .And.NotContain(x => x.Target == "ObifinConnections" || x.Target == "BankConnections");
        records.Should().ContainSingle(x => x.Target == "BankAccounts").Subject.RowCount.Should().Be(1, "yalnız onaysız hesap silindi");
        var scrubbed = records.Should().ContainSingle(x => x.Target == BankConnectionTerminator.ScrubbedAccountTarget).Subject;
        (scrubbed.Action, scrubbed.RowCount).Should().Be((DataDestruction.Actions.Nulled, 1));

        // İkinci koşu (iş ertesi gün yeniden denerse) boşaltılmış hesabı yeniden saymaz.
        var again = await Terminator(db, client).PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd,
            DataDestruction.SystemActor, CancellationToken.None);
        (again.Transactions, again.Accounts, again.AccountsScrubbed).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task Yerel_silme_durdurulmamis_baglantida_bir_sey_silmez_isaret_yazmaz()
    {
        // Hizmet sonu işi Disabled ve 30 günü geçmiş bağlantıyı seçer; yönetici sayfası obifin-poll kilidini almaz, arada
        // "Etkinleştir"e basılabilir. Yerel silme durumu kendisi denetlemeseydi yeni açılmış bağlantının bütün banka verisini
        // siler, Unverified bağlantıya BankDataPurgedAt yazardı.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);   // Unverified

        var act = () => Terminator(db, client).PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd,
            DataDestruction.SystemActor, CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.NotStoppedMessage);
        db.ChangeTracker.Clear();
        (await db.BankTransactions.CountAsync(x => x.LicenseId == lic)).Should().Be(1, "hiçbir şey silinmez");
        (await db.BankAccounts.CountAsync(a => a.LicenseId == lic)).Should().Be(2);
        (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic)).BankDataPurgedAt.Should().BeNull();
        (await db.DataDestructionRecords.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Yerel_silme_surerken_etkinlestirilen_baglantiya_isaret_yazilmaz_kanit_hesap_bosaltilmaz()
    {
        // Son adımdaki işaret yazımı ve onaylı hesabın boşaltılması da aynı denetimden geçer: partiler sürerken yönetici
        // "Etkinleştir"e basarsa açılmış bağlantı "yerel verisi silindi" diye işaretlenmez (eşleştirmeye kapanır, şerit ve
        // hatırlatma yanlış olurdu), onaylı hesabın IBAN'ı boşaltılmaz. Kaydedilmiş partiler gitti; imha kayıtları onları sayar.
        var hook = new SaveHookInterceptor();
        var (db, other) = SharedDb(hook);
        using var _ = db;
        var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);
        await t.StopAsync(lic, CancellationToken.None);
        hook.AfterSave = async () =>
        {
            hook.AfterSave = null;   // yalnız ilk partiden sonra
            await using var admin = other();
            await Terminator(admin, client).ResumeAsync(lic, CancellationToken.None);
        };

        var act = () => t.PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor,
            CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.NotStoppedMessage);
        db.ChangeTracker.Entries().Should().BeEmpty();
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        (conn.Status, conn.BankDataPurgedAt).Should().Be((ObifinConnectionStatus.Unverified, (DateTimeOffset?)null));
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 6)).IbanMasked
            .Should().Be("TR12…345", "açılmış bağlantının onaylı hesabı boşaltılmaz");
        (await db.DataDestructionRecords.AnyAsync(x => x.Target == BankConnectionTerminator.ScrubbedAccountTarget))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Yerel_silmeden_sonra_Kaldir_ve_sil_panelden_eklenmis_baglantiyi_onayli_hesaptan_tanir()
    {
        // KVKK K-6: Obifin panelinden eklenmiş bağlantının (#600; pilottaki QNB gibi) yerelde satırı yok, bu lisansa ait olduğu
        // yalnız onaylı hesaptan (6 → 600) bilinir. Hizmet sonu silmesi hesabı silseydi #600 "sahibi doğrulanamadı" diye
        // Obifin'de kalır, Obifin hizmet bittiği halde bankadan çekmeyi sürdürürdü.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        client.Connections.Add(new ObifinBankConnectionDto(600, "qnb", "Panelden", true));
        client.Accounts.Add(Account(6, 600));
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);
        await t.StopAsync(lic, CancellationToken.None);
        await t.PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, CancellationToken.None);

        var r = await t.RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin", CancellationToken.None);

        client.Removed.Should().BeEquivalentTo(new[] { 501L, 600L });
        r.ObifinUnowned.Should().BeEmpty();
        r.NeedsManualObifinAction.Should().BeFalse();
        (r.Accounts, r.BankConnections).Should().Be((1, 1), "yerel silmenin bıraktığı onaylı hesap ve banka bağlantısı şimdi gider");
        (await db.BankAccounts.AnyAsync(a => a.LicenseId == lic)).Should().BeFalse();
        (await db.BankConnections.AnyAsync(b => b.LicenseId == lic)).Should().BeFalse();
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse();
    }

    [Fact]
    public async Task Lisansi_bitmis_baglantida_Dogrula_ve_Banka_ekle_Obifine_gitmez()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client, licenseActive: false);
        var svc = Service(db, client);

        var verify = () => svc.VerifyAsync(lic, CancellationToken.None);
        var add = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", new Dictionary<string, string>(), CancellationToken.None);

        await verify.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.LicenseEndedMessage);
        await add.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.LicenseEndedMessage);
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Durdurulmus_ve_yerel_verisi_silinmis_baglantida_Dogrula_ve_Banka_ekle_Obifine_gitmez_kanit_hesap_bos_kalir()
    {
        // KVKK K-6 / karar 21(a): durdurma işlemeyi de durdurur. Lisans geçerli, yalnız yönetici durdurdu; 30. günde yerel silme
        // onaylı hesabı (6) IBAN'sız kanıt olarak bıraktı. Doğrula Obifin'in hesap listesiyle o hesabın IBAN'ını, özetini ve
        // bildirim notunu yeniden doldurur, yeni (onaysız) hesap satırı eklerdi; hizmet sonu işi bağlantıyı bir daha işlemediği
        // için yeniden toplanan veri "Kaldır ve sil"e kadar dururdu. Banka ekle de durdurulmuş bağlantı için Obifin'de yeni bir
        // banka çekimi başlatırdı. Yol: önce Etkinleştir (Unverified), sonra Doğrula.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Accounts.Add(new ObifinAccountDto(6, "qnb", 501, null, BankHasherTests.TestIban(), "TL", null, null, true, "bildirim"));
        client.Accounts.Add(Account(12, 501));
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);
        await t.StopAsync(lic, CancellationToken.None);
        await t.PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, CancellationToken.None);
        var svc = Service(db, client);

        var verify = () => svc.VerifyAsync(lic, CancellationToken.None);
        var add = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", new Dictionary<string, string>(), CancellationToken.None);

        await verify.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.StoppedConnectionMessage);
        await add.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.StoppedConnectionMessage);
        client.Calls.Should().Be(0, "durdurulmuş bağlantı için Obifin'e gidilmez");
        var kept = await db.BankAccounts.AsNoTracking().Where(a => a.LicenseId == lic).ToListAsync();
        kept.Should().ContainSingle("yeni hesap satırı eklenmez");
        (kept[0].ObifinAccountId, kept[0].IbanMasked, kept[0].IbanHash, kept[0].NotificationNote)
            .Should().Be((6L, "", (string?)null, (string?)null), "boşaltılmış kanıt hesap boş kalır");
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        (conn.Status, conn.BankDataPurgedAt is not null, conn.LastVerifiedAt).Should()
            .Be((ObifinConnectionStatus.Disabled, true, (DateTimeOffset?)null));
    }

    [Fact]
    public async Task Basarili_dogrulama_Disabled_olmayan_baglantinin_bayat_hizmet_sonu_alanlarini_bosaltir()
    {
        // DisabledAt/BankDataPurgedAt Disabled dışında anlamsız: durum hücresi ve hizmet sonu işi bayat tarih görmesin (elle SQL
        // düzeltmesinden kalmış satır). Disabled bağlantı doğrulamayla açılmaz, alanları da kalır.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Unverified; conn.DisabledAt = DateTimeOffset.UtcNow.AddDays(-2);
        conn.BankDataPurgedAt = DateTimeOffset.UtcNow.AddDays(-1); conn.DisabledByAdmin = true;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        (await Service(db, client).VerifyAsync(lic, CancellationToken.None)).Ok.Should().BeTrue();

        var after = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        (after.Status, after.DisabledAt, after.BankDataPurgedAt, after.DisabledByAdmin)
            .Should().Be((ObifinConnectionStatus.Verified, (DateTimeOffset?)null, (DateTimeOffset?)null, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Durdurulmus_baglantida_kimlik_degisince_lisans_gecerliyse_Unverified_sayac_bosalir_bitmisse_Disabled_kalir(
        bool licenseActive)
    {
        // KVKK K-6: hizmet sonu sayacı kimlik düzenlemesiyle sıfırlanmaz; Disabled'dan çıkan bağlantının tarihi kalmaz.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client, licenseActive);
        var disabledAt = DateTimeOffset.UtcNow.AddDays(-5);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Disabled; conn.DisabledAt = disabledAt; conn.DisabledByAdmin = true;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        await Service(db, client).UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", null, null, CancellationToken.None,
            allowShadowReset: true);

        var after = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        if (licenseActive)
        {
            after.Status.Should().Be(ObifinConnectionStatus.Unverified);
            after.DisabledAt.Should().BeNull();
            after.DisabledByAdmin.Should().BeFalse();
        }
        else
        {
            after.Status.Should().Be(ObifinConnectionStatus.Disabled);
            after.DisabledAt.Should().Be(disabledAt);
            after.DisabledByAdmin.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Yerel_verisi_silinmis_baglantida_kimlik_degisimi_reddedilir_Kaldir_ve_sil_kaniti_ve_isareti_kalir(
        bool licenseActive)
    {
        // Karar 21(a): yerel silme "Kaldır ve sil"in sahiplik kanıtını (yerel banka bağlantısı #501, IBAN'sız onaylı hesap 6)
        // bırakır, BankDataPurgedAt sayfanın şeridini ve hizmet sonu işinin hatırlatmasını tutar. Kimlik değişimi gölge veriyle
        // kanıtı silerdi, lisans geçerliyse işareti de boşaltırdı: takip kaybolur, eski Obifin kullanıcısındaki banka bağlantısı
        // izlenmeden çekmeyi sürdürürdü. Lisans bitmişken de kanıt silinirdi. Önce "Kaldır ve sil".
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client, licenseActive);
        var t = Terminator(db, client);
        await t.StopAsync(lic, CancellationToken.None);
        await t.PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, CancellationToken.None);
        var before = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        var svc = Service(db, client);

        var change = () => svc.UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", null, null, CancellationToken.None,
            allowShadowReset: true);

        await change.Should().ThrowAsync<ObifinValidationException>()
            .WithMessage(ObifinConnectionService.PurgedCredentialChangeMessage);
        db.ChangeTracker.Clear();
        var after = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        (after.UserCode, after.Status, after.BankDataPurgedAt, after.DisabledByAdmin)
            .Should().Be((before.UserCode, ObifinConnectionStatus.Disabled, before.BankDataPurgedAt, true));
        (await db.BankConnections.AnyAsync(b => b.LicenseId == lic && b.BankaApiId == 501)).Should().BeTrue();
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic)).ObifinAccountId.Should().Be(6);
        (await db.DataDestructionRecords.AnyAsync(x => x.Trigger == DataDestruction.Triggers.CredentialChange)).Should().BeFalse();

        // Aynı kimliğe yeni parola kimlik değişimi değildir: kaydedilir, bağlantı durdurulmuş ve işaretli kalır.
        await svc.UpsertAsync(lic, "", before.UserCode, $"pw-{Guid.NewGuid():N}", null, CancellationToken.None);
        var saved = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        (saved.Status, saved.BankDataPurgedAt).Should().Be((ObifinConnectionStatus.Disabled, before.BankDataPurgedAt));
    }

    [Fact]
    public async Task Kaldir_ve_sil_hesap_partisinden_sonra_yazilan_hesap_ve_banka_baglantisi_satirini_da_siler()
    {
        // Saatlik hesap yenilemesi bağlantıyı durdurmadan önce Verified okumuş, Obifin çağrısı uzun sürmüş olsun: kaydı hesap
        // partisinden sonra ama bağlantı silinmeden önce düşerse eklediği hesap satırı (IBAN, özeti, bildirim notu dolu)
        // bağlantısız lisansta kalırdı — BankAccounts'un ObifinConnection'a FK'si yok, saklama işi hesaplara dokunmuyor, sayfa
        // bağlantısız lisansı göstermiyor. Bağlantı silindikten sonraki kayıt ise bağlantının UPDATE'i 0 satır etkilediği için
        // bütünüyle düşer.
        var hook = new SaveHookInterceptor();
        var (db, other) = SharedDb(hook);
        using var _ = db;
        var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var connId = (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic)).Id;
        var late = false;
        hook.AfterSave = async () =>
        {
            if (late) return;
            await using var refresh = other();
            // Hesap ve banka bağlantısı partileri bitti, bağlantı satırı duruyor: son adımdan hemen önce.
            if (await refresh.BankAccounts.AnyAsync(a => a.LicenseId == lic)
                || await refresh.BankConnections.AnyAsync(b => b.LicenseId == lic)
                || !await refresh.ObifinConnections.AnyAsync(c => c.LicenseId == lic)) return;
            late = true;
            var now = DateTimeOffset.UtcNow;
            refresh.BankAccounts.Add(new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 6, BankaKodu = "qnb",
                IbanMasked = "TR12…345", IbanHash = NewHash(), NotificationNote = "bildirim", Currency = "TL", RefreshedAt = now });
            refresh.BankConnections.Add(new BankConnection { Id = Guid.NewGuid(), LicenseId = lic, ObifinConnectionId = connId,
                BankaKodu = "qnb", BankaApiId = 502, Label = "QNB-2", Status = BankConnectionStatus.Active, CreatedAt = now });
            await refresh.SaveChangesAsync();
        };

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1",
            CancellationToken.None);

        late.Should().BeTrue("yarış kuruldu");
        (await db.BankAccounts.AnyAsync(a => a.LicenseId == lic)).Should().BeFalse("geç yazılan hesap satırı da gider");
        (await db.BankConnections.AnyAsync(b => b.LicenseId == lic)).Should().BeFalse();
        (r.Accounts, r.BankConnections).Should().Be((3, 2));
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync();
        records.Where(x => x.Target == "BankAccounts").Sum(x => x.RowCount).Should().Be(3);
        records.Where(x => x.Target == "BankConnections").Sum(x => x.RowCount).Should().Be(2);
    }

    [Fact]
    public async Task Kaldir_ve_sil_yeniden_denemede_de_cakisirsa_yoneticiye_gosterilebilir_hata_baglanti_durdurulmus_kalir()
    {
        // İkinci jeton çakışması ham DbUpdateConcurrencyException olarak yukarı çıksaydı sayfa yalnız ObifinValidationException
        // yakaladığı için yönetici 500 görürdü; durdurma kaydedilmiş, denetim satırı yazılmamış olurdu. Repo kalıbı
        // (ObifinConnectionService.SaveAsync, bağdaştırıcı): ikinci çakışma gösterilebilir hataya döner, izleyici boşalır.
        var conflict = new TransactionDeleteConflict();
        using var db = NewDb(conflict); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);

        var act = () => Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1",
            CancellationToken.None);

        (await act.Should().ThrowAsync<BankDeletionIncompleteException>())
            .WithMessage(BankConnectionTerminator.DeleteIncompleteMessage);
        conflict.Thrown.Should().Be(2, "bir kez yeniden denendi");
        db.ChangeTracker.Entries().Should().BeEmpty("düşen denemenin izi kapsamın sonraki kaydına taşınmasın");
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        (conn.Status, conn.DisabledByAdmin).Should().Be((ObifinConnectionStatus.Disabled, true),
            "ilk adımın durdurması kalır (güvenli yön)");
        (await db.ObifinCopyDeletions.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Hesap_reddi_yeniden_denemede_de_cakisirsa_yoneticiye_gosterilebilir_hata_onay_kaldirilmis_kalir()
    {
        var conflict = new TransactionDeleteConflict();
        using var db = NewDb(conflict); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var account6 = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 6);

        var act = () => Terminator(db, client).RejectAccountAsync(account6.Id, "yonetici-1", CancellationToken.None);

        (await act.Should().ThrowAsync<BankDeletionIncompleteException>())
            .WithMessage(BankConnectionTerminator.RejectIncompleteMessage);
        conflict.Thrown.Should().Be(2);
        db.ChangeTracker.Entries().Should().BeEmpty();
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == account6.Id)).ApprovedAt
            .Should().BeNull("onay en önce kaldırıldı: çekim o hesabın yeni hareketini yazmaz");
    }

    [Fact]
    public async Task Yerel_silme_parti_parti_yapilir_izleyici_birikmez_kayit_toplami_tutar()
    {
        // Saklama işinin kalıbı: lisansın bütün satırları bir kerede yüklenmez; her parti kaydını kendi SaveChanges'inde yazar.
        var counter = new SaveLogInterceptor();
        using var db = NewDb(counter); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);   // 1 hareket
        var now = DateTimeOffset.UtcNow;
        db.BankTransactions.AddRange(Enumerable.Range(0, BankConnectionTerminator.BatchSize * 2).Select(i => new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 100 + i, ObifinAccountId = 6, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now,
        }));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Terminator(db, client).StopAsync(lic, CancellationToken.None);
        counter.DeletedTransactionsPerSave.Clear();
        var total = BankConnectionTerminator.BatchSize * 2 + 1;

        var r = await Terminator(db, client).PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd,
            DataDestruction.SystemActor, CancellationToken.None);

        r.Transactions.Should().Be(total);
        counter.DeletedTransactionsPerSave.Where(n => n > 0).Should().HaveCount(3)
            .And.OnlyContain(n => n <= BankConnectionTerminator.BatchSize);
        db.ChangeTracker.Entries().Should().BeEmpty("partiler arasında ve sonda izleyici boşaltılır");
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic && x.Target == "BankTransactions")
            .SumAsync(x => x.RowCount)).Should().Be(total);
    }

    [Fact]
    public async Task Kaldir_ve_sil_Obifin_kopyasi_icin_teyit_bekleyen_kayit_acar_yerel_silme_acmaz()
    {
        // KVKK Ö-9 öneri 3: Obifin'den yazılı silme teyidi, bağlantı satırı silindikten sonra da izlenebilmeli.
        using var db = NewDb(); var client = new TerminatorObifin();
        var removed = await SeedAsync(db, client);
        var purged = await SeedAsync(db, client);
        var t = Terminator(db, client);

        var r = await t.RemoveAndDeleteAsync(removed, DataDestruction.Triggers.AdminRemove, "yonetici-1", CancellationToken.None);
        await t.StopAsync(purged, CancellationToken.None);
        await t.PurgeLocalDataAsync(purged, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor,
            CancellationToken.None);

        var pending = await db.ObifinCopyDeletions.AsNoTracking().SingleAsync();
        (pending.Id, pending.LicenseId, pending.RequestedBy, pending.ConfirmedAt)
            .Should().Be((r.ObifinCopyDeletionId!.Value, removed, "yonetici-1", (DateTimeOffset?)null));
        pending.RequestedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == removed)).Should().BeFalse("kayıt bağlantıdan bağımsız kalır");
    }

    [Fact]
    public async Task Teyit_alindi_kaydi_kapatir_imha_kaydi_yazar_ikinci_teyit_reddedilir()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);
        var r = await t.RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1", CancellationToken.None);

        (await t.ConfirmObifinCopyDeletedAsync(r.ObifinCopyDeletionId!.Value, "yonetici-2", CancellationToken.None))
            .Should().Be(lic);
        var again = () => t.ConfirmObifinCopyDeletedAsync(r.ObifinCopyDeletionId!.Value, "yonetici-2", CancellationToken.None);
        var unknown = () => t.ConfirmObifinCopyDeletedAsync(Guid.NewGuid(), "yonetici-2", CancellationToken.None);

        var row = await db.ObifinCopyDeletions.AsNoTracking().SingleAsync();
        row.ConfirmedAt.Should().NotBeNull();
        row.ConfirmedBy.Should().Be("yonetici-2");
        (await db.DataDestructionRecords.AsNoTracking()
                .SingleAsync(x => x.Trigger == DataDestruction.Triggers.ProcessorConfirmation))
            .Should().Match<DataDestructionRecord>(x => x.LicenseId == lic
                && x.Target == BankConnectionTerminator.ObifinCopyTarget && x.Actor == "yonetici-2" && x.RowCount == 1);
        await again.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.AlreadyConfirmedMessage);
        await unknown.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.CopyDeletionNotFoundMessage);
    }

    [Fact]
    public async Task Teyit_alindi_ayni_anda_iki_kez_basilinca_ikincisi_reddedilir_tek_imha_kaydi_yazilir()
    {
        // Çift tık ya da iki yönetici: ikisi de kaydı açık okur. ConfirmedAt eşzamanlılık jetonu olmasaydı ikincisi ilkinin
        // teyidinin üstüne yazar, ikinci imha kaydı ve denetim satırı açılırdı.
        var hook = new SaveHookInterceptor();
        var (db, other) = SharedDb(hook);
        using var _ = db;
        var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);
        var id = (await t.RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1", CancellationToken.None))
            .ObifinCopyDeletionId!.Value;
        hook.BeforeSave = async () =>
        {
            hook.BeforeSave = null;
            await using var second = other();
            await Terminator(second, client).ConfirmObifinCopyDeletedAsync(id, "yonetici-2", CancellationToken.None);
        };

        var act = () => t.ConfirmObifinCopyDeletedAsync(id, "yonetici-1", CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.AlreadyConfirmedMessage);
        db.ChangeTracker.Entries().Should().BeEmpty();
        (await db.ObifinCopyDeletions.AsNoTracking().SingleAsync()).ConfirmedBy.Should().Be("yonetici-2", "ilk teyit kalır");
        // İkinci teyidin imha kaydının teyitle birlikte geri alındığını gerçek SQL Server sınar: InMemory'de işlem yok
        // (BankConnectionTerminatorRelationalTests.Teyit_alindi_ayni_anda_…).
    }

    [Fact]
    public async Task Ayni_anda_iki_Kaldir_ve_sil_ikinci_teyit_kaydi_acmaz()
    {
        // İki yönetici aynı anda basar: ikisi de bağlantıyı bulur. Önce biten bağlantıyı siler ve teyit kaydını açar; ikincinin son
        // adımı bağlantıyı bulamadığı halde ikinci bir ObifinCopyDeletions kaydı açardı (hatırlatma iki kez, iki "Teyit alındı").
        var (db, other) = SharedDb(new SaveHookInterceptor());
        using var _ = db;
        var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        var lic = await SeedAsync(db, client);
        client.BeforeList = async () =>
        {
            client.BeforeList = null;
            await using var first = other();   // öbür yöneticinin "Kaldır ve sil"i bu anda bitti
            first.ObifinConnections.Remove(await first.ObifinConnections.SingleAsync(c => c.LicenseId == lic));
            first.ObifinCopyDeletions.Add(new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = lic,
                RequestedAt = DateTimeOffset.UtcNow, RequestedBy = "yonetici-2" });
            await first.SaveChangesAsync();
        };

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1",
            CancellationToken.None);

        r.ObifinCopyDeletionId.Should().BeNull("teyit kaydını bağlantıyı silen deneme açtı");
        (await db.ObifinCopyDeletions.AsNoTracking().SingleAsync(d => d.LicenseId == lic)).RequestedBy.Should().Be("yonetici-2");
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse("yerel silme yine tamamlanır");
    }

    [Fact]
    public async Task Hesap_reddedilince_hareketleri_eslesmeleri_ogrettigi_IBAN_bagi_silinir_dekont_gapi_acilir_onay_geri_alinir()
    {
        // KVKK Ö-10: Görev 12'nin kapısı ileriye dönük; P1'den önce onaysız hesaptan yazılmış veri ancak bu eylemle gider.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);   // hesap 6 onaylı (1 hareket), hesap 9 onaysız
        var now = DateTimeOffset.UtcNow;
        var foreign = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 900, ObifinAccountId = 9,
            BankaKodu = "garanti", Direction = BankTransactionDirection.Incoming, Amount = 20m, Currency = "TL", OccurredAt = now,
            FetchedAt = now, Description = "yabanci" };
        var paymentId = Guid.NewGuid();
        var resolvedPaymentId = Guid.NewGuid();
        var resolvedTx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 901, ObifinAccountId = 9,
            BankaKodu = "garanti", Direction = BankTransactionDirection.Incoming, Amount = 30m, Currency = "TL", OccurredAt = now,
            FetchedAt = now, Description = "yabanci-2" };
        db.BankTransactions.AddRange(foreign, resolvedTx);
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = foreign.Id,
            PaymentId = paymentId, Status = PaymentMatchStatus.ConfirmedByHuman, CreatedAt = now, UpdatedAt = now });
        // Gap çözümüyle bağlanmış dekont: gap'i kapalı, ResolvedBankTransactionId bu hareket.
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = resolvedTx.Id,
            PaymentId = resolvedPaymentId, Status = PaymentMatchStatus.ManualOnly, CreatedAt = now, UpdatedAt = now });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = resolvedPaymentId,
            Reason = PaymentMatchGapReason.NoCandidate, CreatedAt = now.AddHours(-1), ResolvedAt = now,
            ResolvedBankTransactionId = resolvedTx.Id });
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = NewHash(), IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval,
            SourceBankTransactionId = foreign.Id, CreatedAt = now });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var account9 = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 9);
        var account6 = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 6);
        var t = Terminator(db, client);

        var r = await t.RejectAccountAsync(account9.Id, "yonetici-1", CancellationToken.None);

        (r.Transactions, r.Matches, r.Memories, r.GapsReopened).Should().Be((2, 2, 1, 2));
        (await db.BankTransactions.Where(x => x.LicenseId == lic).Select(x => x.ObifinAccountId).ToListAsync())
            .Should().Equal(6L);
        (await db.CustomerIbanMemories.CountAsync(m => m.LicenseId == lic)).Should().Be(1, "başka hareketten gelen bağ kalır");
        var gap = await db.PaymentMatchGaps.AsNoTracking().SingleAsync(g => g.PaymentId == paymentId);
        (gap.Reason, gap.ResolvedAt).Should().Be((PaymentMatchGapReason.UnlinkedByAdmin, (DateTimeOffset?)null),
            "dekont ölçümden düşmez (bağdaştırıcının kaldırma kuralı)");
        var reopened = await db.PaymentMatchGaps.AsNoTracking().SingleAsync(g => g.PaymentId == resolvedPaymentId);
        (reopened.Reason, reopened.ResolvedAt, reopened.ResolvedBankTransactionId)
            .Should().Be((PaymentMatchGapReason.NoCandidate, (DateTimeOffset?)null, (Guid?)null), "çözülmüş gap yeniden açılır");
        // Ölçüt reddedilen hesabın Obifin numarasını taşır: geri yüklemeden sonra ret, imha kaydının veritabanı dışındaki
        // kopyasından (saatlik özet) tek başına yeniden uygulanabilsin; yedek onayı da geri getirir (Görev 23 prosedürü).
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync())
            .Should().NotBeEmpty().And.OnlyContain(x => x.Trigger == DataDestruction.Triggers.AccountRejected
                && x.Actor == "yonetici-1" && x.Criterion == "reddedilen hesap #9 (Ö-10)");
        var rejected = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == account9.Id);
        (rejected.ApprovedAt, rejected.ObifinAccountId).Should().Be(((DateTimeOffset?)null, 9L), "satır yer tutucu olarak kalır");

        await t.RejectAccountAsync(account6.Id, "yonetici-1", CancellationToken.None);   // onaylı hesabın onayı geri alınır

        var revoked = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == account6.Id);
        (revoked.ApprovedAt, revoked.ApprovedBy).Should().Be(((DateTimeOffset?)null, (string?)null));
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse();
        var unknown = () => t.RejectAccountAsync(Guid.NewGuid(), "yonetici-1", CancellationToken.None);
        await unknown.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.AccountNotFoundMessage);
    }

    [Fact]
    public async Task Hesap_reddi_baska_havalenin_ogrettigi_IBAN_bagini_kalan_dogrulamaya_ceker_kalan_yoksa_imha_kaydiyla_siler()
    {
        // Görev 10 kuralı: bir havaleyi kararıyla birlikte silen yol, o havalenin tazelediği ama başka havalenin öğrettiği bağın
        // başlangıcını kalan doğrulamalara geri çeker; yoksa süre artık silinmiş bir doğrulamada kalırdı. Yalnız işaretli kararlar
        // (IbanConfirmedAt) süreyi taşır. Kalan yoksa bağ silinir ve imha kaydına girer (tetikleyici ve yapan reddin kendisi).
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var now = DateTimeOffset.UtcNow;
        var customer = Guid.NewGuid();

        // (a) Bağ hesap 6'nın havalesinden öğrenildi (20 gün önce), hesap 9'un havalesi 5 gün önce tazeledi: başlangıç 20 güne döner.
        var hashA = NewHash();
        var teacherA = DecidedTx(db, lic, 6, hashA, customer, now.AddDays(-20), now.AddDays(-20));
        DecidedTx(db, lic, 9, hashA, customer, now.AddDays(-5), now.AddDays(-5));
        var memoryA = new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = hashA,
            IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = teacherA.Id,
            CreatedAt = now.AddDays(-20), LastConfirmedAt = now.AddDays(-5) };
        // (b) Öğreten havale 180 günlük kuralla silinmiş; tek doğrulama hesap 9'un havalesi: kalan yok, bağ imha kaydıyla silinir.
        var hashB = NewHash();
        DecidedTx(db, lic, 9, hashB, customer, now.AddDays(-3), now.AddDays(-3));
        var memoryB = new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = hashB,
            IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = Guid.NewGuid(),
            CreatedAt = now.AddDays(-200), LastConfirmedAt = now.AddDays(-3) };
        // (c) Hesap 9'un havalesi bağı doğruladı ama son doğrulama hesap 6'nınki (daha yeni): dokunulmaz.
        var hashC = NewHash();
        var teacherC = DecidedTx(db, lic, 6, hashC, customer, now.AddDays(-30), now.AddDays(-30));
        DecidedTx(db, lic, 9, hashC, customer, now.AddDays(-10), now.AddDays(-10));
        DecidedTx(db, lic, 6, hashC, customer, now.AddDays(-2), now.AddDays(-2));
        var memoryC = new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = hashC,
            IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = teacherC.Id,
            CreatedAt = now.AddDays(-30), LastConfirmedAt = now.AddDays(-2) };
        // (d) İşaretsiz karar (yalnız tutar ve zamanla kurulmuş dekont bağı) süreyi taşımadı: bağ yerinde kalır.
        var hashD = NewHash();
        DecidedTx(db, lic, 9, hashD, customer, now.AddDays(-1), ibanConfirmedAt: null);
        var memoryD = new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = hashD,
            IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = Guid.NewGuid(),
            CreatedAt = now.AddDays(-40), LastConfirmedAt = now.AddDays(-40) };
        db.CustomerIbanMemories.AddRange(memoryA, memoryB, memoryC, memoryD);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var account9 = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 9);

        var r = await Terminator(db, client).RejectAccountAsync(account9.Id, "yonetici-1", CancellationToken.None);

        r.Memories.Should().Be(1, "kalan doğrulaması olmayan bağ silindi");
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync(m => m.Id == memoryA.Id)).LastConfirmedAt
            .Should().Be(now.AddDays(-20), "başlangıç kalan doğrulamaya (öğreten havale) döner");
        (await db.CustomerIbanMemories.AnyAsync(m => m.Id == memoryB.Id)).Should().BeFalse();
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync(m => m.Id == memoryC.Id)).LastConfirmedAt
            .Should().Be(now.AddDays(-2), "son doğrulama reddedilen hesabın değildi");
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync(m => m.Id == memoryD.Id)).LastConfirmedAt
            .Should().Be(now.AddDays(-40), "işaretsiz karar süreyi taşımadı");
        var record = await db.DataDestructionRecords.AsNoTracking()
            .SingleAsync(x => x.LicenseId == lic && x.Target == "CustomerIbanMemories");
        (record.RowCount, record.Criterion, record.Trigger, record.Actor).Should().Be((1,
            $"LastConfirmedAt+{new BankOptions().IbanMemoryRetentionMonths}ay, reddedilen hesap #9 (Ö-10), kalan doğrulama yok",
            DataDestruction.Triggers.AccountRejected, "yonetici-1"), "IBAN bağı satırı da reddedilen hesabı gösterir");
    }

    [Fact]
    public async Task Hesap_reddi_parti_parti_siler_sayilar_ve_kayit_toplami_tutar()
    {
        var counter = new SaveLogInterceptor();
        using var db = NewDb(counter); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var now = DateTimeOffset.UtcNow;
        var total = BankConnectionTerminator.BatchSize + 3;
        db.BankTransactions.AddRange(Enumerable.Range(0, total).Select(i => new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 5_000 + i, ObifinAccountId = 9, BankaKodu = "garanti",
            Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now,
        }));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        counter.DeletedTransactionsPerSave.Clear();
        var account9 = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 9);

        var r = await Terminator(db, client).RejectAccountAsync(account9.Id, "yonetici-1", CancellationToken.None);

        r.Transactions.Should().Be(total);
        counter.DeletedTransactionsPerSave.Where(n => n > 0).Should().HaveCount(2)
            .And.OnlyContain(n => n <= BankConnectionTerminator.BatchSize);
        db.ChangeTracker.Entries().Should().BeEmpty();
        (await db.BankTransactions.CountAsync(x => x.LicenseId == lic)).Should().Be(1, "onaylı hesabın hareketi kalır");
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic && x.Target == "BankTransactions")
            .SumAsync(x => x.RowCount)).Should().Be(total);
    }
}
