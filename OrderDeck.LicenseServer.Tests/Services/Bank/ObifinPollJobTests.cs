using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
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

public sealed class ObifinPollJobTests
{
    /// <summary>Betikli istemci: hareketler bellekte; sorgu tarih penceresi + sinceId + sayfa ile
    /// gerçek API gibi filtrelenir (Id artan). Ters ya da 31 gün üstü aralık gerçek istemci gibi fırlatır.</summary>
    private sealed class ScriptedObifin : IObifinClient
    {
        public List<ObifinTransactionDto> Transactions { get; } = new();
        public List<(DateOnly From, DateOnly To, long? Since, int Page)> Calls { get; } = new();
        /// <summary>Her hareket sorgusunda istenen sayfa boyutu.</summary>
        public List<int> RequestedPageSizes { get; } = new();
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
        /// <summary>Sunucu SayfaNo'yu yok sayar: her istekte ilk sayfanın içeriğini verir.</summary>
        public bool IgnorePageNo { get; set; }
        /// <summary><see cref="IgnorePageNo"/> iken yanıttaki sayfa numarası: true = gerçekte verilen (1), false = istenen
        /// (sunucu isteği yankılar ya da SayfaNo hiç dönmez, istemci istenene düşer).</summary>
        public bool ReportServedPageNo { get; set; } = true;
        /// <summary>Gerçek Obifin davranışı (2026-09-26 ölçümü): ToplamSayfaSayisi'nin ötesindeki bir SayfaNo boş sayfa
        /// DEĞİL, yeniden dolu bir sayfa döndürür (burada ilk sayfanın içeriği, istenen numarayla). Açıkken son sayfanın
        /// ötesini isteyen döngü yinelenen veri alır.</summary>
        public bool BeyondLastPageRepeatsFirstPage { get; set; }
        /// <summary>Sunucu tarih penceresini hareketin işlem anına (<c>IslemZamaniDT</c>) göre süzmez: Obifin'in
        /// <c>BaslangicTarihi</c>'nin hangi tarihe baktığı belgelenmemiş, pencere dışı hareket de gelebilir.</summary>
        public bool IgnoreDateWindow { get; set; }

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
            RequestedPageSizes.Add(ps);
            if (BeforeCall is { } hook) await hook(Calls.Count);
            if (t < f) throw new ArgumentOutOfRangeException(nameof(t));
            if (t.DayNumber - f.DayNumber + 1 > 31) throw new ArgumentOutOfRangeException(nameof(t));
            if (CancelCallerOnCall is { } cts)
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
            if (Calls.Count == FailOnCall) throw FailWith();
            var size = Math.Min(ps, PageCap);
            var all = Transactions
                .Where(x => IgnoreDateWindow || (DateOnly.FromDateTime(x.OccurredAtTr) >= f && DateOnly.FromDateTime(x.OccurredAtTr) <= t))
                .Where(x => s is null || x.Id > s)
                .OrderBy(x => x.Id).ToList();
            // Gerçek Obifin: boş pencere ToplamSayfaSayisi 0 döndürür.
            var pages = (int)Math.Ceiling(all.Count / (double)size);
            var served = IgnorePageNo || (BeyondLastPageRepeatsFirstPage && p > pages) ? 1 : p;
            var items = all.Skip((served - 1) * size).Take(size).ToList();
            var reported = IgnorePageNo && ReportServedPageNo ? served : p;
            return new ObifinPage<ObifinTransactionDto>(items, reported, NullTotalPages ? null : pages, NullTotalPages ? null : all.Count, size);
        }
    }

    private sealed class RecordingSink : IBankTransactionSink
    {
        public List<BankTransaction> Received { get; } = new();
        /// <summary>Çağrı anında hareketin izleyicideki durumu — sink kaydedilmiş (Unchanged) varlık almalı.</summary>
        public List<EntityState> StatesAtCall { get; } = new();
        public LicenseDbContext? Db { get; set; }
        /// <summary>Kayıttan sonra çağrılır: test araya girebilir ya da eşleştirici hatası taklit edebilir.</summary>
        public Func<BankTransaction, Task>? OnReceive { get; set; }
        public Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct)
        {
            Received.Add(tx);
            if (Db is not null) StatesAtCall.Add(Db.Entry(tx).State);
            return OnReceive?.Invoke(tx) ?? Task.CompletedTask;
        }
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    /// <summary>Ortak kök: aynı adla açılan ikinci bağlam (admin isteği / taze okuma) aynı bellek deposunu görür.</summary>
    private static readonly InMemoryDatabaseRoot DbRoot = new();

    private static LicenseDbContext NewDb() => NewDb($"obifin-poll-{Guid.NewGuid():N}");
    private static LicenseDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, DbRoot).Options);
    /// <summary>Kaydetme kancalı bağlam: test bir SaveChanges'i düşürebilir.</summary>
    private static LicenseDbContext NewDb(string name, IInterceptor interceptor)
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, DbRoot).AddInterceptors(interceptor).Options);

    /// <summary>Uyarı düzeyindeki günlük satırlarını biçimlenmiş metin olarak toplar.</summary>
    private sealed class WarningLog : ILogger<ObifinPollJob>
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

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
        // Tx yardımcısı hesap 6'yı kullanır: yönetici onaylı (KVKK Ö-10 — onaysız hesabın hareketi yazılmaz).
        db.BankAccounts.Add(new BankAccount { Id = Guid.NewGuid(), LicenseId = licenseId, ObifinAccountId = 6, BankaKodu = "qnb",
            IbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban()), Currency = "TL", Active = true,
            RefreshedAt = DateTimeOffset.UtcNow, ApprovedAt = DateTimeOffset.UtcNow, ApprovedBy = "test" });
        await db.SaveChangesAsync();
        return licenseId;
    }

    private static ObifinTransactionDto Tx(long id, DateTime whenTr, decimal signed, string desc = "HAVALE test", string? iban = null)
        => new(id, 6, "qnb", whenTr, signed, "TL", desc, "FT", "EFT", $"ref-{id}", iban, null, $"{{\"Id\":\"{id}\"}}");

    private static (ObifinPollJob Job, ObifinConnectionService Svc, RecordingSink Sink) Build(LicenseDbContext db, ScriptedObifin client,
        DateOnly todayTr, ObifinOptions? jobOptions = null, ILogger<ObifinPollJob>? log = null)
    {
        var hasher = new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));
        var svc = new ObifinConnectionService(db, client, Protection, hasher, Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);
        var sink = new RecordingSink();
        var job = new ObifinPollJob(db, client, svc, hasher, sink, Options.Create(jobOptions ?? new ObifinOptions()),
            log ?? NullLogger<ObifinPollJob>.Instance)
        { TodayTr = () => todayTr };
        return (job, svc, sink);
    }

    [Fact]
    public async Task Ilk_cekim_baglanti_aninin_iki_gun_gerisinden_baslar_capa_yazilir()
    {
        // KVKK Ö-5: 90 günlük geçmiş çekilmez. Çapa = ilk turun TR günü − 2 (dekont bağdaştırmasının aday penceresi).
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        client.Transactions.Add(Tx(100, new DateTime(2026, 7, 1, 10, 0, 0), 50m));    // 86 gün önce
        client.Transactions.Add(Tx(150, new DateTime(2026, 9, 22, 10, 0, 0), 60m));   // çapadan bir gün önce
        client.Transactions.Add(Tx(200, new DateTime(2026, 9, 23, 0, 30, 0), 75m));  // çapa günü

        await job.RunAsync(CancellationToken.None);

        var call = client.Calls.Should().ContainSingle().Subject;
        (call.From, call.To, call.Since).Should().Be((new DateOnly(2026, 9, 23), today, (long?)null));
        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 200L });
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 23));
        conn.BackfillCompletedAt.Should().NotBeNull();
        conn.LastObifinTransactionId.Should().Be(200);
        conn.LastPolledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Ilk_cekim_bos_donerse_sonraki_artimli_tur_capadan_eskiyi_istemez_yazmaz()
    {
        // İlk turda hareket yoksa imleç boş kalır ve artımlı tur 31 günü geriye sorgulardı: çapa olmasa bağlantı öncesi
        // yazılırdı. Çapadan eskisi Obifin'den istenmez bile.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        await job.RunAsync(CancellationToken.None);
        client.Transactions.Add(Tx(300, new DateTime(2026, 9, 10, 10, 0, 0), 40m));
        client.Transactions.Add(Tx(301, new DateTime(2026, 9, 25, 10, 0, 0), 45m));

        await job.RunAsync(CancellationToken.None);

        var last = client.Calls.Last();
        last.Since.Should().BeNull("ilk turda imleç kurulamadı");
        (last.From, last.To).Should().Be((new DateOnly(2026, 9, 23), today), "artımlı pencere çapada başlar");
        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 301L });
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).LastObifinTransactionId.Should().Be(301);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(-31)]
    public async Task Eski_capa_artimli_pencereyi_degistirmez(int anchorOffsetDays)
    {
        // Çapa artımlı pencereyi yalnız pencerenin içindeyse daraltır. Bağlantıdan 30 gün sonra (ya da bank-data-cleanup'ın
        // eski bağlantıya yazdığı çapada) pencere çapaya genişleseydi 31 günü aşardı: gerçek istemci ArgumentOutOfRange
        // fırlatır, çekim her turda düşer, banka çekimi tamamen durur. −31: pencerenin bir gün dışı (sınır).
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var seeded = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        seeded.InitialCollectFrom = today.AddDays(anchorOffsetDays);
        await db.SaveChangesAsync();
        client.Transactions.Add(Tx(501, new DateTime(2026, 9, 25, 10, 0, 0), 45m));

        await job.RunAsync(CancellationToken.None);

        var call = client.Calls.Should().ContainSingle().Subject;
        (call.From, call.To, call.Since).Should().Be((today.AddDays(-30), today, (long?)0));
        sink.Received.Should().ContainSingle().Which.ObifinId.Should().Be(501);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.LastObifinTransactionId.Should().Be(501);
        conn.LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saat_geri_gidip_capa_bugunu_asarsa_Obifine_gidilmez_imlec_yerinde_kalir(bool incremental)
    {
        // Çapa bugünden ilerideyse (saat geri gitti) bugüne kadarki her gün çapadan eskidir: istenecek gün yok. Pencere
        // çapaya kaysaydı başlangıç bitişi aşardı (gerçek istemci ArgumentOutOfRange fırlatır); [bugün−30, bugün] kalsaydı
        // çapadan eski 31 gün Obifin'den istenirdi. Tur başarılı biter, imleç yerinde kalır: saat düzelince çapadan sonraki
        // hareketler imleçten alınır.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: incremental ? 0 : null, backfilled: incremental);
        var seeded = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        seeded.InitialCollectFrom = today.AddDays(1);
        await db.SaveChangesAsync();
        client.Transactions.Add(Tx(601, new DateTime(2026, 9, 25, 10, 0, 0), 45m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().BeEmpty("bugüne kadarki her gün çapadan eski");
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        sink.Received.Should().BeEmpty();
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.LastObifinTransactionId.Should().Be(incremental ? 0L : (long?)null);
        conn.LastError.Should().BeNull();
        conn.LastPolledAt.Should().NotBeNull("istenecek gün olmayan tur da başarılı turdur");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sunucu_capadan_eski_hareket_dondururse_yazilmaz_imlec_yine_ilerler(bool incremental)
    {
        // Obifin'in tarih süzgecinin hangi tarihe baktığı belgelenmemiş: istenen pencerenin dışından gelen çapa öncesi
        // hareket (ör. geç işlenmiş, Id'si büyük) kayda girmez, eşleştiriciye gitmez; imleç sıralı listeden yine ilerler.
        // Artımlı yol: çapası sonradan yazılmış eski bağlantı (bank-data-cleanup), pencere 31 gün geriye bakar.
        using var db = NewDb(); var client = new ScriptedObifin { IgnoreDateWindow = true }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: incremental ? 0 : null, backfilled: incremental);
        if (incremental)
        {
            var seeded = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
            seeded.InitialCollectFrom = new DateOnly(2026, 9, 23);
            await db.SaveChangesAsync();
        }
        client.Transactions.Add(Tx(401, new DateTime(2026, 9, 23, 0, 0, 0), 45m));    // çapa günü başı
        client.Transactions.Add(Tx(402, new DateTime(2026, 9, 22, 23, 59, 0), 40m));  // çapadan bir dakika önce

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 401L });
        sink.Received.Should().ContainSingle().Which.ObifinId.Should().Be(401);
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).LastObifinTransactionId
            .Should().Be(402, "atlanan hareket de görüldü");
    }

    [Fact]
    public async Task Yarim_kalan_ilk_cekim_sonraki_gun_ayni_capayla_surer()
    {
        // Çapa ilk turun başında kaydedilir. Tur geçici hatayla yarıda kalırsa ertesi günün turu çapayı yeniden
        // hesaplamaz: aradaki günler kaybolmaz, bağlantı anı ilk turun günü kalır.
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { PageCap = 1, FailOnCall = 2, FailWith = () => new HttpRequestException("Name or service not known") };
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        client.Transactions.Add(Tx(10, new DateTime(2026, 9, 24, 10, 0, 0), 20m));
        client.Transactions.Add(Tx(11, new DateTime(2026, 9, 25, 10, 0, 0), 30m));

        var first = () => job.RunAsync(CancellationToken.None);
        await first.Should().ThrowAsync<AggregateException>();
        var afterFailure = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        afterFailure.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 23), "turun başında kaydedildi");
        afterFailure.BackfillCompletedAt.Should().BeNull();

        client.FailOnCall = -1;
        job.TodayTr = () => new DateOnly(2026, 9, 27);
        await job.RunAsync(CancellationToken.None);

        client.Calls.Skip(2).Should().NotBeEmpty().And.OnlyContain(c => c.From == new DateOnly(2026, 9, 23));
        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 10L, 11L });
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 23));
        conn.BackfillCompletedAt.Should().NotBeNull();
        conn.LastObifinTransactionId.Should().Be(11);
    }

    [Fact]
    public async Task Capasi_31_gunden_eski_ilk_cekim_bitisik_31_gunluk_dilimlerle_surer()
    {
        // İlk çekim bitmeden çapa 31 günden eskiye düşebilir: ilk tur çapayı yazdı, bağlantı Obifin reddiyle Failed'a düştü,
        // admin 40 gün sonra aynı kimlikle yeniden doğruladı (kimlik değişmedi, çapa kalır). İlk çekim çapadan bugüne
        // dilimlerle sürer: her dilim en çok 31 gün (gerçek istemci üstünü reddeder), dilimler arasında boşluk yok.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        var anchor = today.AddDays(-40);
        var seeded = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        seeded.InitialCollectFrom = anchor;
        await db.SaveChangesAsync();
        client.Transactions.Add(Tx(19, new DateTime(2026, 8, 15, 23, 0, 0), 10m));  // çapadan önce
        client.Transactions.Add(Tx(20, new DateTime(2026, 8, 16, 9, 0, 0), 11m));   // çapa günü, ilk dilim
        client.Transactions.Add(Tx(21, new DateTime(2026, 9, 15, 23, 0, 0), 12m));  // ilk dilimin son günü
        client.Transactions.Add(Tx(22, new DateTime(2026, 9, 16, 0, 30, 0), 13m));  // ikinci dilimin ilk günü
        client.Transactions.Add(Tx(23, new DateTime(2026, 9, 25, 10, 0, 0), 14m));  // bugün

        await job.RunAsync(CancellationToken.None);

        var windows = client.Calls.Select(c => (c.From, c.To)).Distinct().ToList();
        windows.Should().HaveCount(2);
        windows[0].From.Should().Be(anchor);
        windows[^1].To.Should().Be(today);
        windows.Should().OnlyContain(w => w.To.DayNumber - w.From.DayNumber + 1 <= ObifinPollJob.WindowDays);
        windows.Zip(windows.Skip(1)).Should().OnlyContain(p => p.Second.From == p.First.To.AddDays(1), "dilimler bitişik");
        client.Calls.Should().OnlyContain(c => c.Since == null);
        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 20L, 21L, 22L, 23L });
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.InitialCollectFrom.Should().Be(anchor);
        conn.BackfillCompletedAt.Should().NotBeNull();
        conn.LastObifinTransactionId.Should().Be(23);
    }

    [Theory]
    [InlineData("gecici")]
    [InlineData("red")]
    [InlineData("beklenmeyen")]
    public async Task Ilk_tur_hic_kayit_yapmadan_duserse_capa_kalir_sonraki_tur_ayni_capadan_surer(string failure)
    {
        // Çapa ilk turun başında, Obifin'e gidilmeden kaydedilir. Tur ilk istekte düşse de (geçici hata, Obifin reddi,
        // beklenmeyen hata) sonraki tur çapayı kendi gününden yeniden hesaplamaz. Hesaplasaydı Obifin ilk bağlantıda
        // InitialGraceDays'ten uzun kapalı kaldığında bağlantıdan SONRA gelen havaleler hiç alınmazdı (26.09'daki hareket).
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { FailOnCall = 1 };
        client.FailWith = failure switch
        {
            "gecici" => () => new HttpRequestException("Name or service not known"),
            "beklenmeyen" => () => new InvalidOperationException("beklenmeyen"),
            _ => client.FailWith, // Obifin'in kendi mesajı: Failed
        };
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        client.Transactions.Add(Tx(9, new DateTime(2026, 9, 22, 10, 0, 0), 15m));   // çapadan önce
        client.Transactions.Add(Tx(10, new DateTime(2026, 9, 24, 10, 0, 0), 20m));
        client.Transactions.Add(Tx(11, new DateTime(2026, 9, 26, 10, 0, 0), 30m));  // bağlantıdan sonra, Obifin kapalıyken

        var first = () => job.RunAsync(CancellationToken.None);
        if (failure == "red") await first.Should().NotThrowAsync("Obifin reddi koşuyu düşürmez");
        else await first.Should().ThrowAsync<AggregateException>();
        var afterFailure = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        afterFailure.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 23), "tur Obifin'e gitmeden kaydetti");
        afterFailure.BackfillCompletedAt.Should().BeNull();
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        if (failure == "red")
        {
            // Admin aynı kimlikle yeniden doğruladı: kimlik değişmedi, çapa kalır.
            var failed = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
            failed.Status.Should().Be(ObifinConnectionStatus.Failed);
            failed.Status = ObifinConnectionStatus.Verified; failed.LastError = null;
            await db.SaveChangesAsync();
        }

        client.FailOnCall = -1;
        job.TodayTr = () => new DateOnly(2026, 9, 29);
        await job.RunAsync(CancellationToken.None);

        client.Calls.Skip(1).Should().NotBeEmpty().And.OnlyContain(c => c.From == new DateOnly(2026, 9, 23));
        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 10L, 11L });
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 23));
        conn.BackfillCompletedAt.Should().NotBeNull();
        conn.LastObifinTransactionId.Should().Be(11);
    }

    [Fact]
    public async Task Kimlik_degisince_ilk_cekim_capasi_bosalir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        await job.RunAsync(CancellationToken.None);
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).InitialCollectFrom.Should().NotBeNull();

        await svc.UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", null, null, CancellationToken.None, allowShadowReset: true);

        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).InitialCollectFrom.Should().BeNull("yeni hesap, yeni bağlantı anı");
    }

    [Fact]
    public void Ilk_cekim_payi_dekont_aday_penceresine_esit()
        => ObifinPollJob.InitialGraceDays.Should().Be((int)PaymentMatchReconciler.CandidateWindow.TotalDays);

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
        (await db.BankTransactions.CountAsync()).Should().Be(1, "giden hareket (202) kaydedilmeden atılır");
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
        // hem çekim hem saatlik hesap yenileme bağlantıyı atlar; tek bir 502 admin "Doğrula"ya basana dek çekimi
        // durdururdu (Failed'ı kendiliğinden Verified'a çeviren otomatik yol yok — bilerek).
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        Exception error = exceptionType == typeof(ObifinProtocolException)
            ? new ObifinProtocolException("Obifin HTTP 502 /webservis/hesaphareketleri/hesaphareketleriliste/")
            : (Exception)Activator.CreateInstance(exceptionType, "Name or service not known")!;
        var client = new ScriptedObifin { FailOnCall = 1, FailWith = () => error };
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);
        client.Transactions.Add(Tx(201, new DateTime(2026, 9, 25, 9, 0, 0), 100m));

        var act = () => job.RunAsync(CancellationToken.None);

        // Koşu bağlantı hatalarını toplayıp sonda tek AggregateException'la fırlatır (bkz. RunAsync).
        (await act.Should().ThrowAsync<AggregateException>("Hangfire panosunda Failed görünmeli"))
            .Which.InnerExceptions.Should().ContainSingle().Which.Should().BeOfType(exceptionType);
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
    public async Task Tr_saat_utc_ye_cevrilir_iban_yalniz_hash_ve_maske_giden_atilir()
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
        (await db.BankTransactions.AnyAsync(t => t.ObifinId == 8)).Should().BeFalse("giden hareket kaydedilmeden atılır (KVKK K-4)");
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
    public async Task Ham_kayit_beyaz_listeyle_saklanir_hash_ve_maske_ham_degerden_uretilir()
    {
        // KVKK Ö-2: ham kayıt beyaz listeyle saklanır; özet + maske DTO'nun ham değerinden (redaksiyondan ÖNCE) çıkar.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        var vkn = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        var raw = $$"""{"Id":"9","KarsiHesapIBAN":"{{iban}}","BorcluVKN":"{{vkn}}","GonderenAdi":"Ad Soyad"}""";
        client.Transactions.Add(new ObifinTransactionDto(9, 6, "qnb", new DateTime(2026, 9, 25, 12, 0, 0), 10m, "TL",
            "EFT", "FT", "EFT", "ref-9", iban, "Ad Soyad", raw));

        await job.RunAsync(CancellationToken.None);

        var tx = await db.BankTransactions.SingleAsync();
        tx.RawJson.Should().Be("""{"GonderenAdi":"Ad Soyad"}""", "yalnız beyaz listedeki alanlar kalır (KVKK Ö-2)");
        tx.CounterpartyIbanHash.Should().HaveLength(64);
        tx.CounterpartyIbanMasked.Should().Be(BankHasher.MaskIban(iban));
        tx.CounterpartyTaxIdHash.Should().BeNull("VKN/TCKN kodu amaçsız: yazılmaz (KVKK Ö-2)");
        tx.CounterpartyName.Should().Be("Ad Soyad");
    }

    [Fact]
    public async Task Onaysiz_ya_da_bilinmeyen_hesabin_hareketi_yazilmaz_yer_tutucu_acilir_sayac_artar()
    {
        // KVKK Ö-10: Obifin panelinden eklenen hesap kendiliğinden lisansa yazılmaz; yönetici onaylayana kadar hareketi atılır.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var approved = await db.BankAccounts.SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 6);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));   // hesap 6 = onaylı
        client.Transactions.Add(new ObifinTransactionDto(2, 99, "qnb", new DateTime(2026, 9, 25, 8, 2, 0), 10m, "TL",
            null, null, null, null, null, null, "{}"));                       // hesap 99 bilinmiyor

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.SingleAsync()).BankAccountId.Should().Be(approved.Id);
        sink.Received.Should().ContainSingle().Which.ObifinId.Should().Be(1);
        // Yer tutucu: saatlik hesap yenilemesi IBAN maskesini ve durumu tamamlar; onayı yalnız yönetici verir.
        var placeholder = await db.BankAccounts.SingleAsync(a => a.ObifinAccountId == 99);
        placeholder.LicenseId.Should().Be(lic);
        placeholder.Active.Should().BeFalse();
        placeholder.IbanMasked.Should().Be("?");
        placeholder.ApprovedAt.Should().BeNull();
        placeholder.UnapprovedSkippedCount.Should().Be(1);
        // Görev 11 kuralı 1: onay süzgeci IBAN niteliği sayacından ÖNCE — sayaç yalnız yazılan hareketi sayar.
        (placeholder.IbanFullCount, placeholder.IbanMaskedCount, placeholder.IbanEmptyCount).Should().Be((0, 0, 0));
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(2, "atlanan hareket de görüldü");
    }

    [Fact]
    public async Task Yalniz_onaysiz_hareket_gelen_turda_sayac_ve_imlec_turun_sonundaki_kayitla_kalici_olur()
    {
        // Sayfada yazılacak hareket yoksa UpsertAsync erken döner (fresh boş): yer tutucu ve sayaç izleyicide kalır, turun
        // sonundaki kimlik korumalı kayıt imleçle birlikte yazar. Kayıt atlanırsa uyarı sayısı sessizce sıfır kalırdı.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var log = new WarningLog();
        var (job, svc, sink) = Build(db, client, today, log: log);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(new ObifinTransactionDto(2, 99, "qnb", new DateTime(2026, 9, 25, 8, 2, 0), 10m, "TL",
            null, null, null, null, null, null, "{}"));
        client.Transactions.Add(new ObifinTransactionDto(3, 99, "qnb", new DateTime(2026, 9, 25, 8, 3, 0), 12m, "TL",
            null, null, null, null, null, null, "{}"));

        await job.RunAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        (await db.BankTransactions.CountAsync()).Should().Be(0);
        sink.Received.Should().BeEmpty();
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.ObifinAccountId == 99)).UnapprovedSkippedCount.Should().Be(2);
        (await db.ObifinConnections.AsNoTracking().SingleAsync()).LastObifinTransactionId.Should().Be(3);
        log.Warnings.Should().ContainSingle(w => w.Contains("onaysız hesap")).Which.Should().Contain("2 hareketi yazılmadı");
    }

    [Fact]
    public async Task Onaysiz_hesabin_birden_cok_sayfadaki_hareketi_tek_yer_tutucuda_sayilir()
    {
        // Yer tutucu yalnız izleyicide (sayfada yazılacak hareket yok, sayfa kaydı yok); sonraki sayfanın hesap sorgusu onu
        // döndürmez. Yeniden açılsaydı aynı (LicenseId, ObifinAccountId) için ikinci satır eklenir, SQL Server'da tekil indekse
        // çarpan tur her seferinde düşer, imleç hiç ilerlemezdi (InMemory indeksi denetlemez: satır sayısı sınanır).
        using var db = NewDb(); var client = new ScriptedObifin { PageCap = 2 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= 5; i++)
            client.Transactions.Add(new ObifinTransactionDto(i, 99, "qnb", new DateTime(2026, 9, 25, 8, i, 0), 10m, "TL",
                null, null, null, null, null, null, "{}"));

        await job.RunAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        (await db.BankAccounts.AsNoTracking().Where(a => a.ObifinAccountId == 99).ToListAsync())
            .Should().ContainSingle().Which.UnapprovedSkippedCount.Should().Be(5);
        (await db.ObifinConnections.AsNoTracking().SingleAsync()).LastObifinTransactionId.Should().Be(5);
    }

    [Fact]
    public async Task Yarida_kalan_turdan_sonra_onaysiz_hareket_iki_kez_sayilmaz()
    {
        // Atlanan hareket imleç geçince "görülmüş" olur; imleç yalnız tam başarılı turda yazılır. Sayaç sayfa kaydıyla yazılsaydı
        // yarıda kalan turun kaydedilmiş sayfasındaki onaysız hareket, aynı imleçten yeniden çeken sonraki turda bir daha sayılırdı
        // (yazılan hareket "existing" denetiminde atlanır, atılan hareketin böyle bir izi yok).
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { PageCap = 2, FailOnCall = 2, FailWith = () => new HttpRequestException("ağ yok") };
        var (job, svc, sink) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));   // onaylı: 1. sayfa kaydedilir
        client.Transactions.Add(new ObifinTransactionDto(2, 99, "qnb", new DateTime(2026, 9, 25, 8, 2, 0), 10m, "TL",
            null, null, null, null, null, null, "{}"));
        client.Transactions.Add(new ObifinTransactionDto(3, 99, "qnb", new DateTime(2026, 9, 25, 8, 3, 0), 10m, "TL",
            null, null, null, null, null, null, "{}"));

        var first = () => job.RunAsync(CancellationToken.None);
        await first.Should().ThrowAsync<AggregateException>();
        client.FailOnCall = -1;
        await job.RunAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        (await db.BankTransactions.AsNoTracking().Select(t => t.ObifinId).ToListAsync()).Should().Equal(1);
        sink.Received.Should().ContainSingle();
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.ObifinAccountId == 99)).UnapprovedSkippedCount.Should().Be(2);
        (await db.ObifinConnections.AsNoTracking().SingleAsync()).LastObifinTransactionId.Should().Be(3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Onay_tur_ortasinda_degisirse_sonraki_sayfa_yeni_durumla_suzulur(bool approvedMidTour)
    {
        // Hesap satırı tur boyunca izlenir; EF kimlik çözümü sonraki sayfanın sorgusunda izlenen örneği döndürür, değerleri
        // tazelemez. Onay durumu sayfa başına taze okunur: tur ortasında verilen onaydan sonraki hareket yazılır, kaldırılan onaydan
        // (Görev 13'ün "Reddet ve verisini sil"i) sonraki hareket yazılmaz. Çekimin kaydı yalnız sayaçları yazar, onayı geri yazmaz.
        var dbName = $"obifin-poll-{Guid.NewGuid():N}";
        using var db = NewDb(dbName); var client = new ScriptedObifin { PageCap = 2 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var account7 = new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 7, BankaKodu = "qnb",
            IbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban()), Currency = "TL", Active = true,
            RefreshedAt = DateTimeOffset.UtcNow,
            ApprovedAt = approvedMidTour ? null : DateTimeOffset.UtcNow, ApprovedBy = approvedMidTour ? null : "test" };
        db.BankAccounts.Add(account7); await db.SaveChangesAsync();
        for (var i = 1; i <= 4; i++)
            client.Transactions.Add(new ObifinTransactionDto(i, 7, "qnb", new DateTime(2026, 9, 25, 8, i, 0), 10m, "TL",
                null, null, null, null, null, null, "{}"));
        client.BeforeCall = async call =>
        {
            if (call != 2) return;
            // İkinci bağlam = yönetici isteği, iki sayfa arasında.
            using var admin = NewDb(dbName);
            var a = await admin.BankAccounts.SingleAsync(x => x.Id == account7.Id);
            a.ApprovedAt = approvedMidTour ? DateTimeOffset.UtcNow : null;
            a.ApprovedBy = approvedMidTour ? "test" : null;
            await admin.SaveChangesAsync();
        };

        await job.RunAsync(CancellationToken.None);

        using var fresh = NewDb(dbName);
        (await fresh.BankTransactions.Select(t => t.ObifinId).OrderBy(id => id).ToListAsync())
            .Should().Equal(approvedMidTour ? new long[] { 3, 4 } : new long[] { 1, 2 });
        sink.Received.Should().HaveCount(2);
        var after = await fresh.BankAccounts.SingleAsync(x => x.Id == account7.Id);
        after.UnapprovedSkippedCount.Should().Be(2);
        (after.ApprovedAt is not null).Should().Be(approvedMidTour, "çekimin kaydı yöneticinin kararını geri yazmaz");
        (await fresh.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(4);
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
    public async Task Bir_baglantinin_gecici_hatasi_digerlerinin_cekimini_durdurmaz_kosu_yine_fail_olur()
    {
        // Kiracı yalıtımı: ilk çekilen bağlantının hatası (hangisi önce gelirse) sıradakini bekletmez; koşu yine
        // Failed biter. Aksi hâlde sürekli düşen bir bağlantı, sırada ondan sonra gelen yayıncıları her koşuda aç bırakırdı.
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { FailOnCall = 1, FailWith = () => new HttpRequestException("Name or service not known") };
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));

        var act = () => job.RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<AggregateException>("Hangfire panosunda Failed görünmeli"))
            .Which.InnerExceptions.Should().ContainSingle().Which.Should().BeOfType<HttpRequestException>();
        var conns = await db.ObifinConnections.AsNoTracking().ToListAsync();
        var failed = conns.Should().ContainSingle(c => c.LastError != null).Subject;
        failed.LastError.Should().Be("Obifin'e ulaşılamadı (HttpRequestException)");
        failed.Status.Should().Be(ObifinConnectionStatus.Verified);
        failed.LastObifinTransactionId.Should().Be(0);
        (await db.BankTransactions.CountAsync(t => t.LicenseId == failed.LicenseId)).Should().Be(0);
        var polled = conns.Should().ContainSingle(c => c.LastError == null).Subject;
        polled.LastObifinTransactionId.Should().Be(1, "hatalı bağlantıdan sonra gelen yine çekildi");
        polled.LastPolledAt.Should().NotBeNull();
        (await db.BankTransactions.CountAsync(t => t.LicenseId == polled.LicenseId)).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sunucu_sayfa_numarasini_yok_sayarsa_dongu_biter_imlecli_drenaj_yine_ilerler(bool reportServedPageNo)
    {
        // ToplamSayfaSayisi yok ve sunucu hep ilk sayfayı veriyor: "sayfa dolu → devam" kuralı tek başına sonsuz döngü
        // olurdu (kilit tutulur, sonraki her koşu kilit zaman aşımıyla düşer). Sayfa numarası tutmuyorsa ya da sayfa
        // öncekine göre yeni Id getirmiyorsa pencere biter; BaslangicHareketId'li drenaj turu kalanı alır.
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { PageCap = 2, NullTotalPages = true, IgnorePageNo = true, ReportServedPageNo = reportServedPageNo };
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= 5; i++) client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, i, 0), 10m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Select(c => (c.Since, c.Page)).Should().Equal((0L, 1), (0L, 2), (2L, 1), (2L, 2), (4L, 1));
        (await db.BankTransactions.CountAsync()).Should().Be(5);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(5);
    }

    [Fact]
    public async Task Pencere_sayfa_tavanini_asarsa_protokol_hatasi_imlec_ilerlemez()
    {
        // Her sayfa dolu, her biri yeni Id getiriyor, bitmiyor: sert tavan. Geçici sınıf (ObifinProtocolException) —
        // imleç yerinde, LastError yazılır, durum Verified, koşu Failed.
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { PageCap = 1, NullTotalPages = true };
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= ObifinPollJob.MaxPagesPerWindow + 1; i++)
            client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, 0, 0).AddSeconds(i), 10m));

        var act = () => job.RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<AggregateException>())
            .Which.InnerExceptions.Should().ContainSingle().Which.Should().BeOfType<ObifinProtocolException>();
        client.Calls.Should().HaveCount(ObifinPollJob.MaxPagesPerWindow);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.LastObifinTransactionId.Should().Be(0);
        conn.Status.Should().Be(ObifinConnectionStatus.Verified);
        conn.LastError.Should().Be("Obifin'e ulaşılamadı (ObifinProtocolException)");
    }

    [Fact]
    public async Task Kaydedilen_hareketler_izleyicide_birikmez_sink_kayitli_varlik_alir()
    {
        // Yoğun bir hesabın penceresi binlerce satır olabilir: her sayfa kaydedilip sink'e verildikten sonra izleyiciden
        // ayrılır; SaveChanges'in DetectChanges'i koşu boyunca büyümez.
        using var db = NewDb(); var client = new ScriptedObifin { PageCap = 2 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        sink.Db = db;
        await SeedVerifiedAsync(db, svc);
        for (var i = 1; i <= 5; i++) client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, i, 0), 10m));

        await job.RunAsync(CancellationToken.None);

        sink.Received.Should().HaveCount(5);
        sink.StatesAtCall.Should().OnlyContain(s => s == EntityState.Unchanged, "sink kaydedilmiş varlık alır");
        db.ChangeTracker.Entries<BankTransaction>().Should().BeEmpty();
        (await db.BankTransactions.CountAsync()).Should().Be(5);
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

    [Fact]
    public async Task Giden_ve_sifir_tutarli_hareket_yazilmaz_imlec_yine_ilerler()
    {
        // KVKK K-4: giden hareketin ve sıfır tutarlı satırın (Obifin demo verisinde TutarEksiArti "0.00") amacı yok.
        // Obifin listesinde gelir, kaydedilmeden atılır; imleç sıralı listeden hesaplandığı için yine ilerler.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 0m));
        client.Transactions.Add(Tx(2, new DateTime(2026, 9, 25, 8, 2, 0), 25m));
        client.Transactions.Add(Tx(3, new DateTime(2026, 9, 25, 8, 3, 0), -40m));

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 2L });
        (await db.BankTransactions.SingleAsync()).Direction.Should().Be(BankTransactionDirection.Incoming);
        sink.Received.Should().ContainSingle().Which.ObifinId.Should().Be(2);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(3, "atlanan hareketler de görüldü");
    }

    [Fact]
    public async Task Toplam_sayfa_sayisi_biliniyorsa_son_sayfanin_otesi_istenmez()
    {
        // Gerçek Obifin (2026-09-26 ölçümü): ToplamSayfaSayisi'nin ötesindeki SayfaNo boş değil, yeniden DOLU bir sayfa
        // döndürür — istenseydi yinelenen veri gelirdi. Son sayfa tam dolu olsa da döngü sayfa sayısını aşmaz; arkadan
        // gelen olursa imleçli drenaj turu (BaslangicHareketId) alır.
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { PageCap = 2, BeyondLastPageRepeatsFirstPage = true };
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= 4; i++) client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, i, 0), 10m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Select(c => (c.Since, c.Page)).Should().Equal((0L, 1), (0L, 2), (4L, 1));
        (await db.BankTransactions.CountAsync()).Should().Be(4);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(4);
    }

    [Fact]
    public async Task Bos_pencerede_toplam_sayfa_sifir_tek_cagri_yapilir_satir_yazilmaz()
    {
        // Gerçek Obifin: boş pencere HTTP 200, Liste:[], ToplamSayfaSayisi:0 döndürür. 1. sayfadan sonrası istenmez.
        using var db = NewDb(); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { BeyondLastPageRepeatsFirstPage = true };
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 50, backfilled: true);

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().ContainSingle().Which.Page.Should().Be(1);
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.LastObifinTransactionId.Should().Be(50);
        conn.LastPolledAt.Should().NotBeNull("boş pencere de başarılı turdur");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hata_kaydi_da_kimlik_korumali_kimlik_degistiyse_yazilmaz(bool transient)
    {
        // Admin çekim sürerken kimliği düzeltti (ör. yanlış parolayı değiştirdi: UpsertAsync UpdatedAt'i yazar, durumu
        // Unverified'a çeker). Eski kimlikle giden istek hata döndü; bu hata yeni kaydın üstüne yazılsaydı düzeltilmiş
        // bağlantı Failed'a düşer (Obifin reddi) ya da yabancı bir LastError taşırdı (geçici hata).
        var dbName = $"obifin-poll-{Guid.NewGuid():N}";
        using var db = NewDb(dbName); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { FailOnCall = 1 };
        if (transient) client.FailWith = () => new HttpRequestException("Name or service not known");
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);
        client.BeforeCall = async _ =>
        {
            using var admin = NewDb(dbName);
            var c = await admin.ObifinConnections.SingleAsync(x => x.LicenseId == lic);
            c.UpdatedAt = c.UpdatedAt.AddSeconds(1); c.Status = ObifinConnectionStatus.Unverified;
            await admin.SaveChangesAsync();
        };

        var act = () => job.RunAsync(CancellationToken.None);

        if (transient) await act.Should().ThrowAsync<AggregateException>("geçici hata Hangfire panosunda yine görünür");
        else await act.Should().NotThrowAsync();
        using var fresh = NewDb(dbName);
        var conn = await fresh.ObifinConnections.SingleAsync(x => x.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified, "admin'in kaydı ezilmedi");
        conn.LastError.Should().BeNull("eski kimliğin hatası yeni kaydın üstüne yazılmadı");
        conn.LastObifinTransactionId.Should().Be(200);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hata_kaydi_duserse_asil_istisnanin_sinifi_korunur_uyari_kayittan_once_yazilir(bool transient)
    {
        // Hata kaydının kendisi düşerse (ör. DB o an gitti) asıl sınıflandırma kaybolmasın: geçici hata yine KENDİ türüyle
        // yukarı çıkar (Hangfire panosunda Failed), Obifin reddi yine koşuyu düşürmez. Uyarı kayıttan ÖNCE yazılır: kayıt
        // düşse de hatanın ne olduğu günlükte kalır.
        var dbName = $"obifin-poll-{Guid.NewGuid():N}";
        var hook = new SaveHookInterceptor();
        using var db = NewDb(dbName, hook); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { FailOnCall = 1 };
        if (transient) client.FailWith = () => new HttpRequestException("Name or service not known");
        var log = new WarningLog();
        var (job, svc, _) = Build(db, client, today, log: log);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);
        List<string>? warningsAtSave = null;
        hook.BeforeSave = () =>
        {
            warningsAtSave = log.Warnings.ToList();
            throw new DbUpdateException("hata kaydı düştü");
        };

        var act = () => job.RunAsync(CancellationToken.None);

        if (transient)
            (await act.Should().ThrowAsync<AggregateException>())
                .Which.InnerExceptions.Should().ContainSingle().Which.Should().BeOfType<HttpRequestException>("asıl istisna, kayıt hatası değil");
        else
            await act.Should().NotThrowAsync("Obifin reddi koşuyu düşürmez; kayıt hatası bunu değiştirmez");
        var expected = transient ? "Obifin'e ulaşılamadı (HttpRequestException)" : "Kullanici Bilgileri Hatali!";
        warningsAtSave.Should().NotBeNull("hata kaydı denendi").And.Contain(w => w.Contains(expected), "uyarı kayıttan önce yazıldı");
        hook.BeforeSave = null;
        using var fresh = NewDb(dbName);
        var conn = await fresh.ObifinConnections.SingleAsync(x => x.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Verified, "kayıt düştü, hiçbir şey yazılmadı");
        conn.LastError.Should().BeNull();
        conn.LastObifinTransactionId.Should().Be(200);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Red_hatasinin_dusen_kaydi_siradaki_baglantinin_kaydiyla_yazilmaz(bool adminChangedIdentity)
    {
        // Obifin reddinde hata kaydı düşerse (ör. kısa DB kesintisi) koşu yine başarılı sürer; ama düşen kayıt izleyicide
        // Modified kalırsa sıradaki bağlantının ilk SaveChanges'i onu da yazar — ve o kaydın kimlik denetimi yalnız
        // SIRADAKİ bağlantıyı sınar. Admin bu arada A'nın kimliğini düzelttiyse (UpdatedAt + Unverified) eski kimliğin
        // hatası yeni kaydı ezerdi: kiracılar arası, kimlik korumasını atlayan yazım.
        var dbName = $"obifin-poll-{Guid.NewGuid():N}";
        var hook = new SaveHookInterceptor();
        using var db = NewDb(dbName, hook); var today = new DateOnly(2026, 9, 25);
        var client = new ScriptedObifin { FailOnCall = 1 }; // ilk çekilen bağlantı (A) Obifin reddi alır
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));
        Guid? failedId = null;
        hook.BeforeSave = async () =>
        {
            hook.BeforeSave = null; // yalnız A'nın hata kaydı düşer
            // Hata kaydı izleyiciyi boşaltıp bağlantıyı taze okur: izlenen tek bağlantı A'dır.
            failedId = db.ChangeTracker.Entries<ObifinConnection>().Single().Entity.Id;
            if (adminChangedIdentity)
            {
                using var admin = NewDb(dbName);
                var c = await admin.ObifinConnections.SingleAsync(x => x.Id == failedId);
                c.UpdatedAt = c.UpdatedAt.AddSeconds(1); c.Status = ObifinConnectionStatus.Unverified;
                await admin.SaveChangesAsync();
            }
            throw new DbUpdateException("hata kaydı düştü");
        };

        var act = () => job.RunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("Obifin reddi koşuyu düşürmez; kayıt hatası bunu değiştirmez");
        failedId.Should().NotBeNull("A'nın hata kaydı denendi");
        var aId = failedId!.Value;
        using var fresh = NewDb(dbName);
        var a = await fresh.ObifinConnections.SingleAsync(c => c.Id == aId);
        a.Status.Should().Be(adminChangedIdentity ? ObifinConnectionStatus.Unverified : ObifinConnectionStatus.Verified,
            "düşen hata kaydı sıradaki bağlantının SaveChanges'iyle yeniden yazılmadı");
        a.LastError.Should().BeNull();
        a.LastObifinTransactionId.Should().Be(0);
        var b = await fresh.ObifinConnections.SingleAsync(c => c.Id != aId);
        b.LastObifinTransactionId.Should().Be(1, "A'dan sonra gelen B yine çekildi");
        b.LastPolledAt.Should().NotBeNull();
        b.LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Sayfa_boyutu_sifir_ya_da_negatifse_1000_istenir(int configured)
    {
        // Yapılandırma hatası (0/negatif) Obifin'e anlamsız bir sayfa boyutu göndermesin: TimeoutSeconds'taki gibi
        // varsayılana düşer.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today, new ObifinOptions { PageSize = configured });
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));

        await job.RunAsync(CancellationToken.None);

        client.RequestedPageSizes.Should().NotBeEmpty().And.OnlyContain(ps => ps == ObifinOptions.DefaultPageSize);
        ObifinOptions.DefaultPageSize.Should().Be(1000);
        (await db.BankTransactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Beklenmeyen_hatada_LastError_yalniz_tur_adini_tasir_imlec_ilerlemez_kosu_fail_olur()
    {
        // Sınıf dışı hata (DB kısıtı, eşleştirici hatası…): admin ekranı sessiz kalmasın diye LastError yazılır, ama
        // yalnız tür adı — istisna mesajı veri (IBAN, ad) taşıyabilir. Durum Verified kalır: kimlik aleyhine kanıt değil.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        sink.OnReceive = _ => throw new DbUpdateException($"kısıt ihlali {iban}");
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));

        var act = () => job.RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<AggregateException>("Hangfire panosunda Failed görünmeli"))
            .Which.InnerExceptions.Should().ContainSingle().Which.Should().BeOfType<DbUpdateException>();
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.LastError.Should().Be("Beklenmeyen hata (DbUpdateException)").And.NotContain(iban);
        conn.Status.Should().Be(ObifinConnectionStatus.Verified);
        conn.LastObifinTransactionId.Should().Be(0);
        conn.LastPolledAt.Should().BeNull();
    }

    [Fact]
    public async Task Beklenmeyen_hata_kaydi_da_kimlik_degistiyse_yazilmaz()
    {
        var dbName = $"obifin-poll-{Guid.NewGuid():N}";
        using var db = NewDb(dbName); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        sink.OnReceive = async _ =>
        {
            using var admin = NewDb(dbName);
            var c = await admin.ObifinConnections.SingleAsync(x => x.LicenseId == lic);
            c.UpdatedAt = c.UpdatedAt.AddSeconds(1); c.Status = ObifinConnectionStatus.Unverified;
            await admin.SaveChangesAsync();
            throw new InvalidOperationException("eşleştirici düştü");
        };
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));

        var act = () => job.RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<AggregateException>())
            .Which.InnerExceptions.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
        using var fresh = NewDb(dbName);
        var conn = await fresh.ObifinConnections.SingleAsync(x => x.LicenseId == lic);
        conn.LastError.Should().BeNull("eski koşunun hatası yeni kaydın üstüne yazılmadı");
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
    }

    [Fact]
    public async Task Imlec_geri_sarilinca_yeniden_gorulen_hareket_eslestiriciye_ikinci_kez_gitmez()
    {
        // Sink sözleşmesi: yalnız YENİ yazılan satır verilir. İmleç geri sarılıp aynı hareket yeniden görülürse satır zaten
        // var — eşleştirici aynı ödemeyi ikinci kez almaz. İkinci koşunun yeni hareketi ise sink'e ulaşır.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));

        await job.RunAsync(CancellationToken.None);
        var conn = await db.ObifinConnections.SingleAsync(); conn.LastObifinTransactionId = 0; await db.SaveChangesAsync(); // imleç geri sarıldı
        client.Transactions.Add(Tx(2, new DateTime(2026, 9, 25, 8, 2, 0), 20m));
        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().HaveCount(2).And.OnlyContain(c => c.Since == 0, "ikinci koşu 1 numaralı hareketi yeniden gördü");
        sink.Received.Select(t => t.ObifinId).Should().Equal(1L, 2L);
        (await db.BankTransactions.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(true, ObifinConnectionStatus.Verified)]
    [InlineData(false, ObifinConnectionStatus.Verified)]
    // Süzgeç bilerek "Disabled değil", "Verified" değil: lisansı biten Failed/Unverified bağlantı da durur ve tarih alır.
    // Daraltılsaydı Failed bağlantı hiç DisabledAt almaz, hizmet sonu silmesine (30 gün) hiç girmezdi.
    [InlineData(true, ObifinConnectionStatus.Failed)]
    [InlineData(false, ObifinConnectionStatus.Failed)]
    [InlineData(true, ObifinConnectionStatus.Unverified)]
    [InlineData(false, ObifinConnectionStatus.Unverified)]
    public async Task Lisansi_biten_baglanti_cekilmez_Disabled_ve_DisabledAt_yazilir(bool expired, ObifinConnectionStatus start)
    {
        // KVKK K-6: hizmeti bitmiş yayıncının hesabından hareket toplanmaz. Lisans bitince bağlantı Disabled'a çekilir,
        // DisabledAt hizmet sonu silmesinin (30 gün) sayacıdır.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var seeded = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        seeded.Status = start;
        if (start == ObifinConnectionStatus.Failed) seeded.LastError = $"onceki-hata-{Guid.NewGuid():N}";
        var license = await db.Licenses.SingleAsync(l => l.Id == lic);
        if (expired) license.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        else license.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 9, 0, 0), 100m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().BeEmpty("lisansı biten yayıncının hesabı Obifin'den sorgulanmaz");
        sink.Received.Should().BeEmpty();
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        conn.LastError.Should().Be(ObifinConnectionService.LicenseEndedMessage);
    }

    [Fact]
    public async Task Lisansi_biten_baglanti_durur_gecerli_lisansin_cekimi_surer()
    {
        // Süzgeç bağlantı başınadır: bir yayıncının lisansının bitmesi başka yayıncının çekimini durdurmaz.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var ended = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var active = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        (await db.Licenses.SingleAsync(l => l.Id == ended)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 9, 0, 0), 100m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().ContainSingle("yalnız lisansı geçerli bağlantı çekildi");
        sink.Received.Should().ContainSingle(t => t.LicenseId == active);
        (await db.BankTransactions.CountAsync(t => t.LicenseId == ended)).Should().Be(0);
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == ended)).Status.Should().Be(ObifinConnectionStatus.Disabled);
        var stillOn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == active);
        stillOn.Status.Should().Be(ObifinConnectionStatus.Verified);
        stillOn.DisabledAt.Should().BeNull();
    }

    [Fact]
    public async Task Simdi_cek_yolu_da_lisansi_biten_baglantiyi_cekmez()
    {
        // Admin'in "Şimdi çek"i PollConnectionAsync'i doğrudan kuyruğa atar; RunAsync'in başındaki denetimi atlamaz.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        (await db.Licenses.SingleAsync(l => l.Id == lic)).RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var connId = (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).Id;

        await job.PollConnectionAsync(connId, CancellationToken.None);

        client.Calls.Should().BeEmpty();
        var conn = await db.ObifinConnections.SingleAsync(c => c.Id == connId);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Simdi_cek_yalniz_kendi_baglantisini_denetler_baska_lisansin_satirina_yazmaz()
    {
        // Kiracı yalıtımı: tek bağlantılık çağrı başka yayıncının bitmiş bağlantısına yazmaz — o satırdaki bir kayıt hatası
        // (ör. arada silinen müşteri) bu bağlantının "Şimdi çek"ini düşürmesin. Onu zamanlanmış koşu ve hesap yenileme durdurur.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var other = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var own = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        (await db.Licenses.SingleAsync(l => l.Id == other)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 9, 0, 0), 100m));
        var ownId = (await db.ObifinConnections.SingleAsync(c => c.LicenseId == own)).Id;

        await job.PollConnectionAsync(ownId, CancellationToken.None);

        sink.Received.Should().ContainSingle(t => t.LicenseId == own);
        var untouched = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == other);
        untouched.Status.Should().Be(ObifinConnectionStatus.Verified, "başka lisansın bağlantısını zamanlanmış koşu durdurur");
        untouched.DisabledAt.Should().BeNull();
    }

    [Fact]
    public async Task Zaten_Disabled_baglantinin_DisabledAt_tarihi_degismez()
    {
        // 30 günlük sayaç her koşuda yeniden başlamasın.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var first = DateTimeOffset.UtcNow.AddDays(-10);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Disabled; conn.DisabledAt = first;
        (await db.Licenses.SingleAsync(l => l.Id == lic)).ExpiresAt = DateTimeOffset.UtcNow.AddDays(-11);
        await db.SaveChangesAsync();

        await job.RunAsync(CancellationToken.None);

        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).DisabledAt.Should().Be(first);
    }

    [Fact]
    public async Task Aciklamadaki_IBAN_ve_on_bir_haneli_numara_maskelenerek_yazilir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        var tckn = TestTckn.NewValid();
        client.Transactions.Add(Tx(21, new DateTime(2026, 9, 25, 10, 0, 0), 50m, $"HAVALE {iban} {tckn} ornek_musteri34"));

        await job.RunAsync(CancellationToken.None);

        var tx = await db.BankTransactions.SingleAsync();
        tx.Description.Should().Be($"HAVALE {BankHasher.MaskIban(iban)} {BankDescriptionMasker.ElevenDigitMask} ornek_musteri34");
    }

    [Fact]
    public async Task Kirpilan_uzun_aciklamada_IBAN_kirpmadan_once_maskelenir()
    {
        // 512 karakter sınırı IBAN'ın ortasına düşerse önce kırpmak IBAN'ın başını (TR + 9 rakam) düz bırakırdı: ne IBAN ne
        // 11 hane kalıbına uyar. Maske önce uygulanır, kırpma maskelenmiş metne.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        var filler = new string('x', 500);
        client.Transactions.Add(Tx(22, new DateTime(2026, 9, 25, 10, 0, 0), 50m, $"{filler} {iban}"));

        await job.RunAsync(CancellationToken.None);

        var tx = await db.BankTransactions.SingleAsync();
        tx.Description.Should().Be($"{filler} {BankHasher.MaskIban(iban)}").And.NotContain(iban[..11]);
    }

    [Fact]
    public async Task Kimlik_degisince_silinen_golge_veri_imha_kaydina_yazilir()
    {
        // KVKK Ö-13: ResetShadowData'nın toplu silmesi de kalıcı kayda geçer. Yapan verilmezse "admin"; yönetici sayfası kimliği
        // geçirir (AdminObifinPageTests.Kimlik_degisiminin_imha_kaydinda_yapan_yoneticinin_kimligidir).
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 9, 0, 0), 100m));
        await job.RunAsync(CancellationToken.None);

        await svc.UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", null, null, CancellationToken.None, allowShadowReset: true);

        (await db.DataDestructionRecords.AsNoTracking().Where(r => r.LicenseId == lic).ToListAsync())
            .Should().Contain(r => r.Target == "BankTransactions" && r.RowCount == 1 && r.Action == DataDestruction.Actions.Deleted
                && r.Trigger == DataDestruction.Triggers.CredentialChange && r.Actor == DataDestruction.AdminActor);
    }

    [Fact]
    public async Task Maskeli_IBAN_ozetlenmez_hesapta_tam_maskeli_bos_sayilir()
    {
        // KVKK Ö-7: maskeli IBAN özetlenseydi farklı gönderenler aynı özete düşerdi. Hesap başına nitelik sayacı (değer yazılmaz);
        // yalnız yazılan hareket sayılır: giden hareket atılır, sayılmaz.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        var masked = iban[..6] + "************" + iban[18..];
        client.Transactions.Add(Tx(31, new DateTime(2026, 9, 25, 9, 0, 0), 10m, iban: iban));
        client.Transactions.Add(Tx(32, new DateTime(2026, 9, 25, 9, 1, 0), 10m, iban: masked));
        client.Transactions.Add(Tx(33, new DateTime(2026, 9, 25, 9, 2, 0), 10m, iban: null));
        client.Transactions.Add(Tx(34, new DateTime(2026, 9, 25, 9, 3, 0), -10m, iban: iban));

        await job.RunAsync(CancellationToken.None);

        var full = await db.BankTransactions.SingleAsync(t => t.ObifinId == 31);
        full.CounterpartyIbanHash.Should().HaveLength(64);
        full.CounterpartyIbanHashValid.Should().BeTrue("bu özet geçerli IBAN'dan: IBAN öğretebilir");
        var m = await db.BankTransactions.SingleAsync(t => t.ObifinId == 32);
        m.CounterpartyIbanHash.Should().BeNull("maskeli IBAN özetlenmez (KVKK Ö-7)");
        m.CounterpartyIbanHashValid.Should().BeFalse();
        m.CounterpartyIbanMasked.Should().Be(BankHasher.ShortValueMask, "IBAN olmayan değerin maskesi karakter göstermez (Ö-6)");
        var empty = await db.BankTransactions.SingleAsync(t => t.ObifinId == 33);
        empty.CounterpartyIbanHash.Should().BeNull();
        empty.CounterpartyIbanHashValid.Should().BeFalse();
        empty.CounterpartyIbanMasked.Should().BeNull("boş değer görüntüde yer tutmaz");
        db.ChangeTracker.Clear();
        var account = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.ObifinAccountId == 6);
        (account.IbanFullCount, account.IbanMaskedCount, account.IbanEmptyCount).Should().Be((1, 1, 1));
    }

    [Fact]
    public async Task Harfle_maskelenmis_kontrol_hanesi_tutan_IBAN_ozetlenmez_maskeli_sayilir()
    {
        // KVKK Ö-7: X ile maskelenen değer ~1/97 olasılıkla mod-97'yi tutar (burada kontrol haneleri ona göre hesaplandı). Tutan
        // değer de özetlenmez, IBAN öğretmez (bayrak false) ve maskeli sayılır; maskesi karakter göstermez.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var masked = BankHasherTests.WithCheckDigits("TR", "000120" + new string('X', 12) + TestPhone.Digits(4));
        client.Transactions.Add(Tx(41, new DateTime(2026, 9, 25, 9, 0, 0), 10m, iban: masked));

        await job.RunAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var tx = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.ObifinId == 41);
        tx.CounterpartyIbanHash.Should().BeNull("harfle maskelenmiş değer özetlenmez");
        tx.CounterpartyIbanHashValid.Should().BeFalse();
        tx.CounterpartyIbanMasked.Should().Be(BankHasher.ShortValueMask);
        var account = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.ObifinAccountId == 6);
        (account.IbanFullCount, account.IbanMaskedCount, account.IbanEmptyCount).Should().Be((0, 1, 0));
    }
}
