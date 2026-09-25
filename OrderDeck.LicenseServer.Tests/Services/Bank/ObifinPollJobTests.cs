using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class ObifinPollJobTests
{
    /// <summary>Betikli istemci: hareketler bellekte; sorgu tarih penceresi + sinceId + sayfa ile
    /// gerçek API gibi filtrelenir (Id artan). 31 gün üstü aralık gerçek istemci gibi fırlatır.</summary>
    private sealed class ScriptedObifin : IObifinClient
    {
        public List<ObifinTransactionDto> Transactions { get; } = new();
        public List<(DateOnly From, DateOnly To, long? Since, int Page)> Calls { get; } = new();
        public int FailOnCall { get; set; } = -1;
        /// <summary><see cref="FailOnCall"/> sırasındaki çağrının fırlatacağı istisna; varsayılan Obifin'in kendi mesajı.</summary>
        public Func<Exception> FailWith { get; set; } = () => new ObifinApiException(new[] { "Kullanici Bilgileri Hatali!" });
        /// <summary>Sunucunun GERÇEK sayfa boyutu — istenenden küçük olabilir (Obifin 2000 istenince 1000 döndürür).</summary>
        public int PageCap { get; set; } = 1000;
        /// <summary>Sayfa meta verisinde ToplamSayfaSayisi / ToplamKayitSayisi gelmesin.</summary>
        public bool NullTotalPages { get; set; }
        /// <summary>Her hareket sorgusundan ÖNCE çağrılır (1 tabanlı çağrı sırası): test araya girebilir.</summary>
        public Func<int, Task>? BeforeCall { get; set; }
        /// <summary>Ayarlıysa ilk hareket sorgusu ÇAĞIRANIN jetonunu iptal edip onunla iptal istisnası fırlatır.</summary>
        public CancellationTokenSource? CancelCallerOnCall { get; set; }

        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ObifinAccountDto>>(Transactions.Select(t => t.AccountId).Distinct()
                .Select(id => new ObifinAccountDto(id, "qnb", 1, "1", BankHasherTests.TestIban(), "TL", 0, null, true, null)).ToList());
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>(Array.Empty<ObifinBankConnectionDto>());
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
        {
            Calls.Add((f, t, s, p));
            if (BeforeCall is { } hook) await hook(Calls.Count);
            if (t.DayNumber - f.DayNumber + 1 > 31) throw new ArgumentOutOfRangeException(nameof(t));
            if (CancelCallerOnCall is { } cts)
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
            if (Calls.Count == FailOnCall) throw FailWith();
            var size = Math.Min(ps, PageCap);
            var all = Transactions
                .Where(x => DateOnly.FromDateTime(x.OccurredAtTr) >= f && DateOnly.FromDateTime(x.OccurredAtTr) <= t)
                .Where(x => s is null || x.Id > s)
                .OrderBy(x => x.Id).ToList();
            var items = all.Skip((p - 1) * size).Take(size).ToList();
            var pages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)size));
            return new ObifinPage<ObifinTransactionDto>(items, p, NullTotalPages ? null : pages, NullTotalPages ? null : all.Count, size);
        }
    }

    private sealed class RecordingSink : IBankTransactionSink
    {
        public List<BankTransaction> Received { get; } = new();
        public Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct) { Received.Add(tx); return Task.CompletedTask; }
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    /// <summary>Ortak kök: aynı adla açılan ikinci bağlam (admin isteği / taze okuma) aynı bellek deposunu görür.</summary>
    private static readonly InMemoryDatabaseRoot DbRoot = new();

    private static LicenseDbContext NewDb() => NewDb($"obifin-poll-{Guid.NewGuid():N}");
    private static LicenseDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, DbRoot).Options);

    private static async Task<Guid> SeedVerifiedAsync(LicenseDbContext db, ObifinConnectionService svc, long? cursor = null, bool backfilled = false)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"c-{Guid.NewGuid():N}@x", Name = "C",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();
        var conn = await svc.UpsertAsync(licenseId, "", "api@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}", CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Verified;
        conn.LastObifinTransactionId = cursor;
        conn.BackfillCompletedAt = backfilled ? DateTimeOffset.UtcNow.AddDays(-1) : null;
        await db.SaveChangesAsync();
        return licenseId;
    }

    private static ObifinTransactionDto Tx(long id, DateTime whenTr, decimal signed, string desc = "HAVALE test", string? iban = null)
        => new(id, 6, "qnb", whenTr, signed, "TL", desc, "FT", "EFT", $"ref-{id}", iban, null, null, $"{{\"Id\":\"{id}\"}}");

    private static (ObifinPollJob Job, ObifinConnectionService Svc, RecordingSink Sink) Build(LicenseDbContext db, ScriptedObifin client, DateOnly todayTr)
    {
        var hasher = new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));
        var svc = new ObifinConnectionService(db, client, Protection, hasher, Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);
        var sink = new RecordingSink();
        var job = new ObifinPollJob(db, client, svc, hasher, sink, Options.Create(new ObifinOptions()), NullLogger<ObifinPollJob>.Instance)
        { TodayTr = () => todayTr };
        return (job, svc, sink);
    }

    [Fact]
    public async Task Ilk_cekim_90_gunu_31_gunluk_dilimlerle_alir_ve_imleci_kurar()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        client.Transactions.Add(Tx(100, new DateTime(2026, 7, 1, 10, 0, 0), 50m));   // 86 gün önce
        client.Transactions.Add(Tx(200, new DateTime(2026, 9, 24, 10, 0, 0), 75m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Select(c => (c.From, c.To)).Distinct().Should().HaveCount(3, "90 gün = 3 dilim");
        client.Calls.Should().OnlyContain(c => c.To.DayNumber - c.From.DayNumber + 1 <= 31);
        (await db.BankTransactions.CountAsync()).Should().Be(2);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.BackfillCompletedAt.Should().NotBeNull();
        conn.LastObifinTransactionId.Should().Be(200);
        conn.LastPolledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Artimli_cekim_31_gunluk_pencere_ve_imlecle_yalniz_yeni_hareketleri_yazar()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);
        client.Transactions.Add(Tx(200, new DateTime(2026, 9, 24, 10, 0, 0), 75m));   // zaten görüldü
        client.Transactions.Add(Tx(201, new DateTime(2026, 9, 25, 9, 0, 0), 100m));
        client.Transactions.Add(Tx(202, new DateTime(2026, 9, 25, 9, 5, 0), -30m));  // giden

        await job.RunAsync(CancellationToken.None);

        var call = client.Calls.Should().ContainSingle().Subject;
        call.Since.Should().Be(200);
        (call.To.DayNumber - call.From.DayNumber + 1).Should().Be(31);
        call.To.Should().Be(today);
        (await db.BankTransactions.CountAsync()).Should().Be(2);
        sink.Received.Should().ContainSingle(t => t.ObifinId == 201, "yalnız GELEN hareket eşleştiriciye gider");
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).LastObifinTransactionId.Should().Be(202);
    }

    [Fact]
    public async Task Ayni_hareket_iki_kez_gelirse_tek_satir_kalir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: null, backfilled: true);
        client.Transactions.Add(Tx(300, new DateTime(2026, 9, 20, 10, 0, 0), 10m));

        await job.RunAsync(CancellationToken.None);
        var conn = await db.ObifinConnections.SingleAsync(); conn.LastObifinTransactionId = null; await db.SaveChangesAsync(); // imleç geri sarıldı
        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Obifin_hatasinda_Failed_ve_LastError_yazilir_imlec_ilerlemez_kosu_fail_olmaz()
    {
        // Obifin "hayır" dedi (ör. kimlik hatalı): kendiliğinden düzelmez, admin kimliği düzeltip yeniden doğrular.
        // Failed bağlantı bir sonraki koşuda atlanır; Hangfire panosunda kırmızı koşu biriktirmenin anlamı yok.
        using var db = NewDb(); var client = new ScriptedObifin { FailOnCall = 1 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);
        client.Transactions.Add(Tx(201, new DateTime(2026, 9, 25, 9, 0, 0), 100m));

        await job.RunAsync(CancellationToken.None);

        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastError.Should().Be("Kullanici Bilgileri Hatali!", "Obifin'in kendi metni, ön ek yok");
        conn.LastObifinTransactionId.Should().Be(200);
        conn.LastPolledAt.Should().BeNull();
        (await db.BankTransactions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(ObifinProtocolException))]
    public async Task Gecici_hatada_LastError_yazilir_imlec_ilerlemez_Verified_kalir_kosu_fail_olur(Type exceptionType)
    {
        // Ağ/vekil/zaman aşımı: 5 dakika sonraki koşu aynı imleçten dener. Durum Verified KALIR — Failed yazılsaydı
        // çekim işi bağlantıyı atlar, tek bir 502 saatlik hesap yenilemesi düzeltene dek çekimi durdururdu.
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        Exception error = exceptionType == typeof(ObifinProtocolException)
            ? new ObifinProtocolException("Obifin HTTP 502 /webservis/hesaphareketleri/hesaphareketleriliste/")
            : (Exception)Activator.CreateInstance(exceptionType, "Name or service not known")!;
        var client = new ScriptedObifin { FailOnCall = 1, FailWith = () => error };
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);
        client.Transactions.Add(Tx(201, new DateTime(2026, 9, 25, 9, 0, 0), 100m));

        var act = () => job.RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>("Hangfire panosunda Failed görünmeli")).Which.Should().BeOfType(exceptionType);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Verified);
        conn.LastError.Should().Be($"Obifin'e ulaşılamadı ({exceptionType.Name})");
        conn.LastObifinTransactionId.Should().Be(200);
        conn.LastPolledAt.Should().BeNull();
        (await db.BankTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Isin_kendi_iptali_LastError_yazmaz_istisna_yukari_gider()
    {
        // Sunucu kapanıyor / Hangfire iptal etti: bu Obifin'in suçu değil, bağlantıya hata yazılmaz.
        using var db = NewDb(); using var cts = new CancellationTokenSource(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { CancelCallerOnCall = cts };
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);

        var act = () => job.RunAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Verified);
        conn.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Sayfa_dolu_donerse_ayni_kosuda_drenaj_yapilir()
    {
        using var db = NewDb(); var client = new ScriptedObifin { PageCap = 2 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= 5; i++) client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, i, 0), 10m));

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.CountAsync()).Should().Be(5);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(5);
    }

    [Fact]
    public async Task Son_sayfa_tam_doluysa_ayni_kosuda_imlecle_ikinci_tur_atilir()
    {
        // Pencerenin son sayfası sunucu boyutunu tam doldurdu: arkasında daha var olabilir; aynı koşuda yeni
        // imleçle bir tur daha atılır, boş dönünce durulur.
        using var db = NewDb(); var client = new ScriptedObifin { PageCap = 2 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= 4; i++) client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, i, 0), 10m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Select(c => (c.Since, c.Page)).Should().Equal((0L, 1), (0L, 2), (4L, 1));
        (await db.BankTransactions.CountAsync()).Should().Be(4);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(4);
    }

    [Fact]
    public async Task Toplam_sayfa_bilgisi_yoksa_sunucunun_gercek_sayfa_boyutuna_gore_devam_edilir()
    {
        // Obifin istenen 1000 yerine kendi tavanını uygular; ToplamSayfaSayisi gelmezse döngü İSTENEN boyuta bakıp
        // "tek sayfa" sanmamalı: sayfa gerçek boyutu dolduruyorsa devam, küçükse ya da boşsa dur.
        using var db = NewDb(); var client = new ScriptedObifin { PageCap = 2, NullTotalPages = true }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= 5; i++) client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, i, 0), 10m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Select(c => c.Page).Should().Equal(1, 2, 3);
        (await db.BankTransactions.CountAsync()).Should().Be(5);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(5);
    }

    [Fact]
    public async Task Tr_saat_utc_ye_cevrilir_yon_isaretten_iban_yalniz_hash_ve_maske()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        client.Transactions.Add(Tx(7, new DateTime(2026, 9, 25, 12, 0, 0), 250m, "EFT GELEN kod123", iban));
        client.Transactions.Add(Tx(8, new DateTime(2026, 9, 25, 12, 1, 0), -40m));

        await job.RunAsync(CancellationToken.None);

        var inc = await db.BankTransactions.SingleAsync(t => t.ObifinId == 7);
        inc.Direction.Should().Be(BankTransactionDirection.Incoming);
        inc.Amount.Should().Be(250m);
        inc.OccurredAt.Should().Be(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        inc.CounterpartyIbanHash.Should().HaveLength(64);
        inc.CounterpartyIbanMasked.Should().Be(BankHasher.MaskIban(iban));
        // İzlenen varlık lisans→müşteri→lisanslar döngüsüne bağlanır; yalnız satırın sütunları serileştirilir.
        var row = db.Entry(inc).CurrentValues.ToObject();
        System.Text.Json.JsonSerializer.Serialize(row).Should().NotContain(iban[4..12], "ham IBAN hiçbir alanda yok");
        (await db.BankTransactions.SingleAsync(t => t.ObifinId == 8)).Direction.Should().Be(BankTransactionDirection.Outgoing);
    }

    [Fact]
    public async Task Karsi_IBAN_bos_ya_da_bosluksa_hash_ve_maske_null_kalir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(11, new DateTime(2026, 9, 25, 12, 0, 0), 10m, iban: "   "));
        client.Transactions.Add(Tx(12, new DateTime(2026, 9, 25, 12, 1, 0), 10m, iban: null));

        await job.RunAsync(CancellationToken.None);

        foreach (var tx in await db.BankTransactions.ToListAsync())
        {
            tx.CounterpartyIbanHash.Should().BeNull();
            tx.CounterpartyIbanMasked.Should().BeNull("boş maske (\"\") sütuna yazılmaz");
        }
    }

    [Fact]
    public async Task Ham_json_kimlik_alanlari_redakte_edilerek_saklanir_hash_ve_maske_ham_degerden_uretilir()
    {
        // Spec §7: ham JSON'da IBAN/VKN/TCKN kalmaz; hash + maske DTO'nun ham değerinden (redaksiyondan ÖNCE) çıkar.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        var vkn = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        var raw = $$"""{"Id":"9","KarsiHesapIBAN":"{{iban}}","BorcluVKN":"{{vkn}}","GonderenAdi":"Ad Soyad"}""";
        client.Transactions.Add(new ObifinTransactionDto(9, 6, "qnb", new DateTime(2026, 9, 25, 12, 0, 0), 10m, "TL",
            "EFT", "FT", "EFT", "ref-9", iban, "Ad Soyad", vkn, raw));

        await job.RunAsync(CancellationToken.None);

        var tx = await db.BankTransactions.SingleAsync();
        tx.RawJson.Should().NotContain(iban).And.NotContain(vkn).And.Contain("[redakte]").And.Contain("Ad Soyad");
        tx.CounterpartyIbanHash.Should().HaveLength(64);
        tx.CounterpartyIbanMasked.Should().Be(BankHasher.MaskIban(iban));
        tx.CounterpartyTaxIdHash.Should().HaveLength(64);
        tx.CounterpartyName.Should().Be("Ad Soyad");
    }

    [Fact]
    public async Task Bilinmeyen_hesap_icin_yer_tutucu_acilir_bilinen_hesaba_baglanir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var known = new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 6, BankaKodu = "qnb",
            IbanMasked = "TR12…345", Active = true, RefreshedAt = DateTimeOffset.UtcNow };
        db.BankAccounts.Add(known); await db.SaveChangesAsync();
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));   // hesap 6 = bilinen
        client.Transactions.Add(new ObifinTransactionDto(2, 99, "qnb", new DateTime(2026, 9, 25, 8, 2, 0), 10m, "TL",
            null, null, null, null, null, null, null, "{}"));                       // hesap 99 bilinmiyor

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.SingleAsync(t => t.ObifinId == 1)).BankAccountId.Should().Be(known.Id);
        // Yer tutucu: saatlik hesap yenilemesi (ObifinAccountRefreshJob) IBAN maskesi ve durumu tamamlar.
        var placeholder = await db.BankAccounts.SingleAsync(a => a.ObifinAccountId == 99);
        placeholder.LicenseId.Should().Be(lic);
        placeholder.Active.Should().BeFalse();
        placeholder.IbanMasked.Should().Be("?");
        (await db.BankTransactions.SingleAsync(t => t.ObifinId == 2)).BankAccountId.Should().Be(placeholder.Id);
    }

    [Fact]
    public async Task Kimlik_degisirse_kalan_sayfalar_yazilmaz_imlec_ilerlemez()
    {
        // Admin çekim sürerken kimliği değiştirdi: UpsertAsync imleci sıfırlar, gölge veriyi siler. Eski koşu ne
        // eski hesabın satırlarını ne eski imleci geri yazmalı. Eşzamanlılık jetonu BİLEREK yok; iş her kayıttan
        // önce kimlik alanlarını (UserCode/BaseUrl/UpdatedAt) taze okur.
        var dbName = $"obifin-poll-{Guid.NewGuid():N}";
        using var db = NewDb(dbName); var client = new ScriptedObifin { PageCap = 2 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= 6; i++) client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, i, 0), 10m));
        client.BeforeCall = async call =>
        {
            if (call != 2) return;
            // İkinci bağlam = admin isteği: kimlik değişti, imleç ve gölge veri sıfırlandı (UpsertAsync'in yaptığı).
            using var admin = NewDb(dbName);
            var c = await admin.ObifinConnections.SingleAsync(x => x.LicenseId == lic);
            c.UserCode = "api2@x"; c.UpdatedAt = DateTimeOffset.UtcNow;
            c.Status = ObifinConnectionStatus.Unverified; c.LastObifinTransactionId = null;
            admin.BankTransactions.RemoveRange(await admin.BankTransactions.Where(t => t.LicenseId == lic).ToListAsync());
            await admin.SaveChangesAsync();
        };

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().HaveCount(2, "ikinci sayfanın kaydı öncesinde koşu iptal edildi");
        sink.Received.Should().HaveCount(2, "ilk sayfa değişimden önce yazılmıştı");
        using var fresh = NewDb(dbName);
        (await fresh.BankTransactions.CountAsync()).Should().Be(0, "kimlik değişiminden sonra hiçbir satır yazılmadı");
        var conn = await fresh.ObifinConnections.SingleAsync(x => x.LicenseId == lic);
        conn.LastObifinTransactionId.Should().BeNull("eski imleç geri yazılmadı");
        conn.LastPolledAt.Should().BeNull();
        conn.UserCode.Should().Be("api2@x");
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
    }

    [Fact]
    public async Task Cozulemeyen_kimlik_Failed_yazilir_Obifine_gidilmez()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        // Başka anahtar halkasıyla şifrelenmiş metin = anahtar klasörü kaybı senaryosu.
        conn.PasswordProtected = new EphemeralDataProtectionProvider().CreateProtector("x").Protect($"pw-{Guid.NewGuid():N}");
        await db.SaveChangesAsync();

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().BeEmpty("çözülemeyen kimlikle Obifin'e gidilmez");
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastError.Should().Be(ObifinConnectionService.UndecryptableMessage);
    }

    [Fact]
    public async Task Verified_olmayan_baglanti_atlanir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Failed; await db.SaveChangesAsync();

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().BeEmpty();
    }
}
