using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// IdentityKeyRepairJob: <c>CustomerProjectionFullSync</c> göçünün SQL
/// backfill'i ile <see cref="WpfCustomerProjection.IdentityKeyOf"/>'un
/// ayrıştığı satırları (rollback NEWID varsayılanı, kenar boşlukları,
/// "exotic" harfler) .NET tarafında yeniden hesaplayıp düzeltir; B1 tekil
/// indeksiyle bir ÇAKIŞMA olursa satırı atlayıp devam eder, geçişin sonunda o
/// lisansı birleştirip onarımı bir kez daha yapar.
///
/// <para>Gerçek SQL Server gerekir: satır başına CAS <c>ExecuteUpdateAsync</c>
/// kullanıyor — InMemory sağlayıcıda desteklenmiyor (bkz.
/// <c>SqlServerContainerFixture</c> sınıf dokümanı). <see cref="IAsyncLifetime"/>
/// her test METODUNA kendi veritabanını veriyor — bu yüzden ilk koşu
/// sayıları ALT SINIR değil TAM sayı (bkz. <c>TcknBackfillJobTests</c>,
/// aynı desen).</para>
///
/// <para>Bozuk anahtarlar entity setter'ı ATLAYARAK ham SQL ile yazılıyor:
/// <c>Username</c> setter'ı her zaman doğru <c>IdentityKey</c>'i türetir,
/// "yanlış" bir anahtar üretmenin tek yolu bu.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class IdentityKeyRepairJobTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public IdentityKeyRepairJobTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<Guid> SeedLicenseAsync(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"musteri-{Guid.NewGuid():N}@example.test",
            Name = "Onarım Testi Müşteri",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "ikr-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    /// <summary>Ham SQL'le bir satırın IdentityKey'ini zorla değiştirir —
    /// Username setter'ını atlamanın (ve böylece "yanlış" bir anahtar
    /// üretebilmenin) tek yolu.</summary>
    private static async Task ForceKeyAsync(string cs, Guid projectionId, string key)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE WpfCustomerProjections SET IdentityKey = @key WHERE Id = @id";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@id", projectionId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>"Eski imaj" satırı: IdentityKey kolonu HİÇ verilmeden INSERT
    /// — göçteki gerçek <c>DEFAULT (CONVERT(nvarchar(128), NEWID()))</c>
    /// tetiklenir. Entity setter'ını atlamanın tek yolu bu.</summary>
    private static async Task InsertWithoutIdentityKeyAsync(
        string cs, Guid id, Guid licenseId, string platform, string username, DateTimeOffset now)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO WpfCustomerProjections (Id, LicenseId, Platform, Username, UpdatedAt)
            VALUES (@id, @licenseId, @platform, @username, @now)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@licenseId", licenseId);
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Göçteki SQL backfill'inin AYNI ifadesini
    /// (<c>LOWER(LTRIM(RTRIM(...)))</c>) gerçek SQL Server'da çalıştırıp
    /// sonucu döner. "SQL'in ne üreteceğini" varsaymak yerine veritabanının
    /// kendisine soruyoruz — test, collation/versiyon ayrıntılarına değil
    /// gerçek davranışa dayanır.</summary>
    private static async Task<string> SqlBackfillKeyAsync(string cs, string username)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LOWER(LTRIM(RTRIM(@username)))";
        cmd.Parameters.AddWithValue("@username", username);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> ReadIdentityKeyAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Kopya satırlar da okunabilsin (A5b: varsayılan sorgulardan gizliler).
        return await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Id == id).Select(p => p.IdentityKey).SingleAsync();
    }

    [Fact]
    public async Task Yanlis_anahtarli_satirlari_duzeltir_dogru_satira_dokunmaz_ikinci_kosu_sifir()
    {
        // Kaçış dizisi değil AÇIK kod noktası kullanılıyor ki kaynak kodda
        // görünür/okunur kalsın: NBSP görsel olarak normal boşlukla, Kelvin
        // işareti (U+212A) ASCII 'K' ile AYNI görünür — bir \u kaçışı da
        // okunurdu ama açık (char) dönüşümü yanlış okumaya hiç yer bırakmıyor.
        var tab = (char)0x0009;
        var nbsp = (char)0x00A0;
        var kelvinSign = (char)0x212A;
        var whitespaceEdgeUsername = tab + "veli" + nbsp; // TAB + "veli" + NBSP — SQL RTRIM/LTRIM bunları kırpmaz.
        var exoticUsername = kelvinSign + "ELVIN"; // Kelvin işareti + "ELVIN".

        var now = DateTimeOffset.UtcNow;
        Guid licenseId, whitespaceEdgeRowId, exoticRowId, correctRowId;
        long correctRowChangeSeqBefore;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            licenseId = await SeedLicenseAsync(db);

            var whitespaceRow = new WpfCustomerProjection
            { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = whitespaceEdgeUsername, UpdatedAt = now };
            var exoticRow = new WpfCustomerProjection
            { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = exoticUsername, UpdatedAt = now };
            var correctRow = new WpfCustomerProjection
            { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "dogruanahtar", UpdatedAt = now };
            db.WpfCustomerProjections.AddRange(whitespaceRow, exoticRow, correctRow);
            await db.SaveChangesAsync();

            whitespaceEdgeRowId = whitespaceRow.Id;
            exoticRowId = exoticRow.Id;
            correctRowId = correctRow.Id;
            correctRowChangeSeqBefore = correctRow.ChangeSeq;
        }

        // "Eski imaj" satırı: gerçek NEWID() DEFAULT'u tetikler.
        var newidRowId = Guid.NewGuid();
        await InsertWithoutIdentityKeyAsync(_cs, newidRowId, licenseId, "tiktok", "newid-sonrasi", now);
        (await ReadIdentityKeyAsync(newidRowId)).Should().NotBe(WpfCustomerProjection.IdentityKeyOf("newid-sonrasi"),
            "gerçek NEWID DEFAULT'u kullanıcı adıyla eşleşmemeli — test genuine bir uyuşmazlık sınamalı");

        // Kenar boşluğu uyuşmazlığı — SQL'in ürettiği anahtarı GERÇEKTEN
        // sorup kullanıyoruz; önce bunun .NET'ten FARKLI olduğunu doğruluyoruz.
        var whitespaceSqlKey = await SqlBackfillKeyAsync(_cs, whitespaceEdgeUsername);
        whitespaceSqlKey.Should().NotBe(WpfCustomerProjection.IdentityKeyOf(whitespaceEdgeUsername),
            "SQL RTRIM/LTRIM TAB/NBSP kırpmaz, .NET Trim() kırpar — ikisi FARKLI üretmeli");
        await ForceKeyAsync(_cs, whitespaceEdgeRowId, whitespaceSqlKey);

        // "Exotic" harf uyuşmazlığı — aynı şekilde SQL'e soruyoruz.
        var exoticSqlKey = await SqlBackfillKeyAsync(_cs, exoticUsername);
        exoticSqlKey.Should().NotBe(WpfCustomerProjection.IdentityKeyOf(exoticUsername),
            "Kelvin işareti (U+212A) SQL Server'ın koleksiyonu ile .NET arasında FARKLI küçülmeli");
        await ForceKeyAsync(_cs, exoticRowId, exoticSqlKey);

        // ── 1. koşu: üç satır düzelir ────────────────────────────────────
        int firstRunCount;
        using (var scope = _factory.Services.CreateScope())
        {
            firstRunCount = await scope.ServiceProvider.GetRequiredService<IdentityKeyRepairJob>()
                .RunAsync(CancellationToken.None);
        }
        firstRunCount.Should().Be(3, "üç satır yanlış anahtarlıydı");

        // ── 2. koşu: idempotent — artık hepsi doğru, sıfır ────────────────
        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IdentityKeyRepairJob>()
                .RunAsync(CancellationToken.None)).Should().Be(0, "tüm satırlar artık doğru anahtarlı");
        }

        // ── Doğrulama ──────────────────────────────────────────────────
        (await ReadIdentityKeyAsync(newidRowId)).Should().Be(WpfCustomerProjection.IdentityKeyOf("newid-sonrasi"));
        (await ReadIdentityKeyAsync(whitespaceEdgeRowId)).Should().Be("veli", "NBSP/TAB .NET Trim() ile kırpılmalı");
        (await ReadIdentityKeyAsync(exoticRowId)).Should().Be(WpfCustomerProjection.IdentityKeyOf(exoticUsername));

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await verifyDb.WpfCustomerProjections.AsNoTracking().SingleAsync(p => p.Id == correctRowId))
            .ChangeSeq.Should().Be(correctRowChangeSeqBefore,
                "doğru satıra hiç YAZILMAMALI — dokunulmazsa rowversion ilerlemez");
    }

    [Fact]
    public async Task CAS_arada_degisen_kullanici_adini_BIN2_ile_yakalar_bayat_anahtari_korur()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = await SeedLicenseAsync(db);
        var job = scope.ServiceProvider.GetRequiredService<IdentityKeyRepairJob>();

        var row = new WpfCustomerProjection
        { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "ayşe", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(row);
        await db.SaveChangesAsync();

        // Okunan anahtar BİLEREK YANLIŞ ("eski") — IdentityKeyOf("ayşe")
        // zaten "ayşe" olurdu (eşleşirdi, hiç yazma denenmezdi); test
        // genuine bir "onarım" senaryosunu taklit etsin diye zorlanıyor.
        const string readUsername = "ayşe";
        const string readKey = "eski";
        await ForceKeyAsync(_cs, row.Id, readKey);

        // Araya (WPF sync'in yazacağı gibi) bir kullanıcı adı değişimi
        // giriyor — ham SQL ile, SADECE Username değişsin, IdentityKey
        // okunan "eski" değerinde bilerek kalsın.
        await using (var conn = new SqlConnection(_cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE WpfCustomerProjections SET Username = @u WHERE Id = @id";
            cmd.Parameters.AddWithValue("@u", "ayşe🌸");
            cmd.Parameters.AddWithValue("@id", row.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        var changed = await job.RepairIfUnchangedAsync(row.Id, readUsername, readKey, CancellationToken.None);

        // Asıl zarar: COLLATE olmasaydı CI_AS altında 'ayşe' ile 'ayşe🌸'
        // eşit sayılabilir, WHERE eşleşir ve IdentityKey "ayşe" yazılırdı —
        // oysa satırın GERÇEK (güncel) kullanıcı adı artık "ayşe🌸".
        changed.Should().Be(0,
            "BIN2 COLLATE olmadan WHERE 'ayşe🌸'yi 'ayşe' ile eşit sayıp bayat 'ayşe' anahtarını yazardı");
        (await db.WpfCustomerProjections.AsNoTracking().SingleAsync(p => p.Id == row.Id))
            .IdentityKey.Should().Be(readKey, "CAS eşleşmediği için YAZMAMALI — bayat anahtar olduğu gibi kalmalı");
    }

    [Fact]
    public async Task Bos_anahtar_uretecek_satira_dokunmaz_RunAsync_patlamaz()
    {
        const string forcedWrongKey = "eski-anahtar";
        Guid blankRowId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var licenseId = await SeedLicenseAsync(db);
            var row = new WpfCustomerProjection
            { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "gecici", UpdatedAt = DateTimeOffset.UtcNow };
            db.WpfCustomerProjections.Add(row);
            await db.SaveChangesAsync();
            blankRowId = row.Id;
        }

        // Kullanıcı adının kendisi ham SQL'le yalnız boşluğa çevriliyor (çok
        // eski/bozuk bir satırı taklit eder) ve anahtar bilerek "yanlış" (ama
        // BOŞ OLMAYAN) bırakılıyor — amaç, işin bunu düzeltmeye kalkışıp BOŞ
        // anahtar YAZMADIĞINI kanıtlamak.
        await using (var conn = new SqlConnection(_cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE WpfCustomerProjections SET Username = @u, IdentityKey = @k WHERE Id = @id";
            cmd.Parameters.AddWithValue("@u", "   ");
            cmd.Parameters.AddWithValue("@k", forcedWrongKey);
            cmd.Parameters.AddWithValue("@id", blankRowId);
            await cmd.ExecuteNonQueryAsync();
        }

        Func<Task> act = async () =>
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IdentityKeyRepairJob>().RunAsync(CancellationToken.None);
        };
        await act.Should().NotThrowAsync();

        (await ReadIdentityKeyAsync(blankRowId)).Should().Be(forcedWrongKey,
            "boş anahtar üretecekti — ASLA yazılmamalı, satır olduğu gibi kalmalı");
    }

    /// <summary>RunAsync'in aday sorgusu (<c>SELECT ... FROM [WpfCustomerProjections] ...</c>)
    /// kapanırken, AYRI bir SQL bağlantısından satırın Username'ini değiştirir
    /// — bkz. <c>TcknBackfillJobTests.PurgeAfterShopperCandidateRead</c>
    /// (aynı desen, aynı gerekçe: <c>DataReaderClosingAsync</c>, çünkü aday
    /// sorgusu <c>ToListAsync</c> ile TÜM satırları belleğe alıyor — okuma
    /// kilitleri kapanmadan ikinci bağlantıdan yazmak deadlock riski taşır).</summary>
    private sealed class RenameAfterCandidateReadInterceptor(string cs, Guid id, string newUsername) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains("FROM [WpfCustomerProjections]", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE WpfCustomerProjections SET Username = @u WHERE Id = @id";
                cmd.Parameters.AddWithValue("@u", newUsername);
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }
            return result;
        }
    }

    [Fact]
    public async Task RunAsync_ucdan_uca_CAS_kullanir_arada_degisen_kullanici_adini_atlar()
    {
        Guid rowId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var licenseId = await SeedLicenseAsync(db);
            var row = new WpfCustomerProjection
            { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "ayşe", UpdatedAt = DateTimeOffset.UtcNow };
            db.WpfCustomerProjections.Add(row);
            await db.SaveChangesAsync();
            rowId = row.Id;

            // Anahtarı bilerek "bozuyoruz" ki RunAsync bu satırı GERÇEKTEN
            // onarmaya kalksın (zaten doğruysa hiç CAS denenmez).
            await ForceKeyAsync(_cs, rowId, "eski");
        }

        // RunAsync'i DI dışında, interceptor'lı ÖZEL bir LicenseDbContext'le
        // çağırıyoruz — bkz. interceptor sınıfı dokümanı.
        using var interceptedDb = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>()
                .UseSqlServer(_cs)
                .AddInterceptors(new RenameAfterCandidateReadInterceptor(_cs, rowId, "ayşe🌸"))
                .Options);
        var job = new IdentityKeyRepairJob(
            interceptedDb, new CustomerIdentityMergeJob(interceptedDb, new CustomerIdentityMerger(interceptedDb)),
            NullLogger<IdentityKeyRepairJob>.Instance);

        (await job.RunAsync(default)).Should().Be(0,
            "satır aday olarak okunduktan sonra kullanıcı adı değişti — RunAsync bu satırı ATLAMALI (CAS ıskalar)");

        (await ReadIdentityKeyAsync(rowId)).Should().Be("eski", "CAS ıskaladığı için YAZMAMALI");
    }

    /// <summary>
    /// B1 çakışma politikası, GERÇEK indeksle (göç zinciri test veritabanına
    /// B1'i de kurar): asıl kayıt C doğru anahtarda; R aynı kişinin satırı, geri
    /// alma penceresinde kolonu tanımayan imajın açtığı (anahtarı NEWID). Onarım
    /// R'yi C'nin anahtarına yazarken indekse çarpar: çakışmayı sayıp geçer,
    /// geçişin SONUNDA o lisansın birleştirmesini koşar (gruplama HESAPLANAN
    /// anahtarla — R ile C aynı gruba düşer, biri kopyaya döner ve indeksin
    /// dışına çıkar), ardından onarımı BİR kez daha yapar.
    /// </summary>
    [Fact]
    public async Task B1_cakismasinda_lisans_birlestirilir_onarim_bir_kez_daha_gecer()
    {
        Guid licenseId, canonicalId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            licenseId = await SeedLicenseAsync(db);
            var canonical = new WpfCustomerProjection
            { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "kullanici", UpdatedAt = DateTimeOffset.UtcNow };
            db.WpfCustomerProjections.Add(canonical);
            await db.SaveChangesAsync();
            canonicalId = canonical.Id;
        }

        // Eski imaj satırları: IdentityKey kolonu HİÇ verilmeden INSERT —
        // NEWID() DEFAULT'u tetikler (benzersiz, indekse çarpmaz; çarpma ancak
        // ONARIM C'nin anahtarına yazmaya kalkınca). Id'ler BİLEREK sabit:
        // RunAsync satırları Id sırasıyla tarar ve SQL Server uniqueidentifier'ı
        // son baytlardan karşılaştırır — 01, 02'den önce gelir. Böylece çakışan R
        // her koşuda ilgisiz U'dan ÖNCE taranır ve "çakışmadan sonra diğer
        // satırlarla devam edilir" yolu deterministik sınanır.
        var duplicateId = Guid.Parse("00000000-0000-0000-0000-000000000001"); // R: C ile AYNI kişi
        var unrelatedId = Guid.Parse("00000000-0000-0000-0000-000000000002"); // U: ilgisiz kimlik
        var now = DateTimeOffset.UtcNow;
        await InsertWithoutIdentityKeyAsync(_cs, duplicateId, licenseId, "tiktok", "Kullanici", now);
        await InsertWithoutIdentityKeyAsync(_cs, unrelatedId, licenseId, "tiktok", "baskakullanici", now);
        (await ReadIdentityKeyAsync(duplicateId)).Should().NotBe("kullanici", "test GERÇEK bir çakışma sınamalı");

        var log = new LogRecorder<IdentityKeyRepairJob>();
        int fixedCount;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var job = new IdentityKeyRepairJob(
                db, scope.ServiceProvider.GetRequiredService<CustomerIdentityMergeJob>(), log);
            fixedCount = await job.RunAsync(CancellationToken.None);
        }

        fixedCount.Should().Be(2, "U ilk geçişte, R birleştirmeden sonraki geçişte düzelir");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var pair = await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == duplicateId || p.Id == canonicalId).ToListAsync();
            var head = pair.Should().ContainSingle(p => p.MergedIntoId == null, "R ile C birleşti").Subject;
            pair.Single(p => p.Id != head.Id).MergedIntoId.Should().Be(head.Id);
            pair.Should().OnlyContain(p => p.IdentityKey == "kullanici");
            (await ReadIdentityKeyAsync(unrelatedId)).Should().Be("baskakullanici");

            var merge = scope.ServiceProvider.GetRequiredService<CustomerIdentityMergeJob>();
            (await merge.CountMismatchedKeysAsync(default)).Should().Be(0);
            (await merge.CountDuplicateHeadsAsync(default)).Should().Be(0);
        }

        log.Entries.Should().Contain(e => e.Message.Contains(licenseId.ToString()),
            "çakışan lisans günlükte Id'siyle görünür");
        log.Entries.Should().NotContain(e => e.Message.Contains("kullanici", StringComparison.OrdinalIgnoreCase),
            "günlükte yalnız sayılar ve Id'ler — kullanıcı adı asla");
    }

    /// <summary>
    /// A5b: kopyalar (MergedIntoId dolu) varsayılan sorgulardan gizli. Sınıf
    /// dokümanının "kopyalar da onarılır" sözü hem taramada hem CAS'ta
    /// geçerli kalmalı — biri filtreli kalsa satır ya hiç görülmez ya da CAS
    /// 0 satır etkileyip "ıskaladı" sayılır.
    /// </summary>
    [Fact]
    public async Task Kopya_satirin_anahtari_da_onarilir()
    {
        Guid aliasId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var licenseId = await SeedLicenseAsync(db);
            var canonical = new WpfCustomerProjection
            { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "kopya-onarim", UpdatedAt = DateTimeOffset.UtcNow };
            var alias = new WpfCustomerProjection
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "Kopya-Onarim",
                MergedIntoId = canonical.Id, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WpfCustomerProjections.AddRange(canonical, alias);
            await db.SaveChangesAsync();
            aliasId = alias.Id;
        }
        await ForceKeyAsync(_cs, aliasId, "bayat-anahtar");

        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IdentityKeyRepairJob>().RunAsync(CancellationToken.None))
                .Should().Be(1, "kopya satır da taranır ve CAS ile düzeltilir");
        }
        (await ReadIdentityKeyAsync(aliasId)).Should().Be("kopya-onarim");
    }

    [Fact]
    public async Task Is_DI_zincirinden_cozulur_ve_bos_veritabaninda_guvenlidir()
    {
        using var scope = _factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<IdentityKeyRepairJob>();
        job.Should().NotBeNull();

        (await job.RunAsync(CancellationToken.None)).Should().Be(0, "bu testin veritabanı boş");
    }
}
