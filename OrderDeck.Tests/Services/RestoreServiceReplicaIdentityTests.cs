using System.Data;
using System.IO.Compression;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Services;

/// <summary>
/// Geri yüklemede yeni kopya kimliği (spec 2026-10-13 §8.7). Göç 049 sonrası yedek: kimlik geri yüklemede,
/// değiştirmeden önce döner; önceki kimlik yedeğinkidir. 049 öncesi yedek: değiştirilen veritabanının kimliği
/// işarete yazılır, açılış göçünden sonra (AppHost) eklenir. Gerçek göçlerle kurulmuş veritabanları; açılış
/// <see cref="SqliteConnectionFactory"/> + <see cref="MigrationRunner"/> + <see cref="RestoreReplicaMarker.ApplyAtBoot"/>
/// sırasıyla taklit edilir.
/// </summary>
public sealed class RestoreServiceReplicaIdentityTests : IDisposable
{
    /// <summary>Havuzsuz dosya bağlantısı: tohum ve okuma bağlantıları dosyayı tutmasın.</summary>
    private sealed class FileDb(string path) : IDbConnectionFactory
    {
        public IDbConnection Open()
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            }.ToString());
            conn.Open();
            SqliteSearchFunctions.Register(conn);
            return conn;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orderdeck-restore-replica-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public RestoreServiceReplicaIdentityTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "orderdeck.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* en iyi çaba */ }
    }

    /// <summary>Açılıştaki gibi: gerçek fabrika (havuzlu, WAL) ve tam göç; kopya kimliğini döner.</summary>
    private Guid BootCurrentDatabase()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        new MigrationRunner(factory).Run();
        return new ReplicaIdentityRepository(factory).GetReplicaId()!.Value;
    }

    /// <summary>Gerçek göçlerle kurulmuş yedek veritabanının baytları (<paramref name="upTo"/> verilirse o şemaya kadar).</summary>
    private byte[] BuildBackup(int? upTo, Action<IDbConnectionFactory>? seed = null)
    {
        var path = Path.Combine(_dir, $"seed-{Guid.NewGuid():N}.db");
        var db = new FileDb(path);
        (upTo is { } v ? new MigrationRunner(db, EmbeddedMigrationScripts.UpTo(v)) : new MigrationRunner(db)).Run();
        seed?.Invoke(db);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static byte[] Zip(byte[] db)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = archive.CreateEntry("orderdeck.db").Open();
            s.Write(db, 0, db.Length);
        }
        return ms.ToArray();
    }

    private Task<RestoreResult> RestoreAsync(byte[] backup) =>
        new RestoreService(_dbPath, new FakeBackupClientWithDownload(Zip(backup)), NullLogger<RestoreService>.Instance)
            .RestoreAsync(Guid.NewGuid());

    private int SchemaVersion()
    {
        using var c = new FileDb(_dbPath).Open();
        return c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
    }

    private static Guid? ReplicaOf(IDbConnectionFactory db) => new ReplicaIdentityRepository(db).GetReplicaId();

    [Fact]
    public async Task Gocu_049_sonrasi_yedekte_kimlik_doner_onceki_kimlik_yedeginki()
    {
        var currentId = BootCurrentDatabase();
        Guid backupId = default;
        var backup = BuildBackup(upTo: null, db => backupId = ReplicaOf(db)!.Value);

        var result = await RestoreAsync(backup);

        result.Success.Should().BeTrue(result.Error);
        var restored = new ReplicaIdentityRepository(new FileDb(_dbPath));
        var newId = restored.GetReplicaId();
        newId.Should().NotBeNull();
        newId.Should().NotBe(backupId, "yedeğin kimliğiyle çalışmak yedeği alan veritabanının klonu olmak demek");
        newId.Should().NotBe(currentId);
        restored.GetPendingPreviousReplicaId().Should().Be(backupId);
        SchemaVersion().Should().Be(49);
        File.Exists(RestoreReplicaMarker.PathFor(_dbPath)).Should().BeFalse("049 sonrası yedekte işaret gerekmez");
        File.Exists(_dbPath + "-wal").Should().BeFalse();
        File.Exists(_dbPath + ".restoring").Should().BeFalse();
    }

    [Fact]
    public async Task Gocu_049_sonrasi_yedegin_kimligi_bozuksa_onceki_kimlik_degistirilen_veritabanininki()
    {
        var currentId = BootCurrentDatabase();
        var backup = BuildBackup(upTo: null, db =>
        {
            using var c = db.Open();
            c.Execute("UPDATE _meta SET ReplicaId = 'bozuk' WHERE Id = 1");
        });

        (await RestoreAsync(backup)).Success.Should().BeTrue();

        var restored = new ReplicaIdentityRepository(new FileDb(_dbPath));
        restored.GetReplicaId().Should().NotBeNull().And.NotBe(currentId);
        restored.GetPendingPreviousReplicaId().Should().Be(currentId);
    }

    [Fact]
    public async Task Gocu_049_oncesi_yedek_acilis_gocunden_sonra_sema_49_ve_onceki_kimlik_degistirilen_veritabanininki()
    {
        var currentId = BootCurrentDatabase();

        var result = await RestoreAsync(BuildBackup(upTo: 48));

        result.Success.Should().BeTrue(result.Error);
        SchemaVersion().Should().Be(48,
            "geri yükleme göç koşmaz; işi uygulamanın yeniden açılışı yapar");
        File.ReadAllText(RestoreReplicaMarker.PathFor(_dbPath)).Should().Be(currentId.ToString("N"));

        // Yeniden açılış (AppHost): göç, sonra işaret.
        var factory = new SqliteConnectionFactory(_dbPath);
        new MigrationRunner(factory).Run();
        var identity = new ReplicaIdentityRepository(factory);
        RestoreReplicaMarker.ApplyAtBoot(_dbPath, identity, NullLogger.Instance)
            .Should().Be(new RestoreMarkerOutcome(currentId, Unresolved: false));

        SchemaVersion().Should().Be(49);
        identity.GetReplicaId().Should().NotBeNull().And.NotBe(currentId);
        identity.GetPendingPreviousReplicaId().Should().Be(currentId);
        File.Exists(RestoreReplicaMarker.PathFor(_dbPath)).Should().BeFalse();

        // İkinci açılış: işaret yok, hiçbir şey değişmez.
        var replica = identity.GetReplicaId();
        RestoreReplicaMarker.ApplyAtBoot(_dbPath, identity, NullLogger.Instance)
            .Should().Be(new RestoreMarkerOutcome(null, Unresolved: false));
        identity.GetReplicaId().Should().Be(replica);
        identity.GetPendingPreviousReplicaId().Should().Be(currentId);
    }

    /// <summary>Geri yüklemenin sık nedeni bozuk dosyadır: değiştirilen veritabanının <c>_meta</c>'sı okunamaz.</summary>
    private void CorruptCurrentDatabase()
    {
        SqliteConnection.ClearAllPools();
        var garbage = new byte[8192];
        new Random(7).NextBytes(garbage);
        File.WriteAllBytes(_dbPath, garbage);
        foreach (var sidecar in new[] { _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(sidecar)) File.Delete(sidecar);
    }

    [Fact]
    public async Task Gocu_049_oncesi_yedek_degistirilen_veritabaninin_kimligi_okunamasa_da_kopya_geri_yuklenmis_bildirilir()
    {
        // M20, §4.3, §8.7: geri yüklenmiş kopyada yerel kazanmaz. Önceki kimlik bilinmese de işaret yazılır (rastgele
        // nöbetçi); yoksa açılışta PreviousReplicaId boş kalır, sunucu kopyayı özgün sanır ve geri doldurmada
        // localWins = true ile yedeğin bayat (iptalsiz, eski fiyatlı) hâlini sunucuya yazdırırdı.
        BootCurrentDatabase();
        CorruptCurrentDatabase();

        (await RestoreAsync(BuildBackup(upTo: 48))).Success.Should().BeTrue();

        var factory = new SqliteConnectionFactory(_dbPath);
        new MigrationRunner(factory).Run();
        var identity = new ReplicaIdentityRepository(factory);
        var outcome = RestoreReplicaMarker.ApplyAtBoot(_dbPath, identity, NullLogger.Instance);

        outcome.Unresolved.Should().BeFalse();
        outcome.Previous.Should().NotBeNull();
        identity.GetPendingPreviousReplicaId().Should().Be(outcome.Previous,
            "ilk sales/hello previousReplicaId taşır: sunucu kopyayı geri yüklenmiş sayar");
        identity.GetReplicaId().Should().NotBeNull().And.NotBe(outcome.Previous!.Value);
    }

    [Fact]
    public async Task Gocu_049_sonrasi_yedekte_iki_kimlik_de_bilinmese_de_kopya_geri_yuklenmis_bildirilir()
    {
        BootCurrentDatabase();
        var backup = BuildBackup(upTo: null, db =>
        {
            using var c = db.Open();
            c.Execute("UPDATE _meta SET ReplicaId = 'bozuk', PreviousReplicaId = NULL WHERE Id = 1");
        });
        CorruptCurrentDatabase();

        (await RestoreAsync(backup)).Success.Should().BeTrue();

        var restored = new ReplicaIdentityRepository(new FileDb(_dbPath));
        var replica = restored.GetReplicaId();
        replica.Should().NotBeNull();
        restored.GetPendingPreviousReplicaId().Should().NotBeNull().And.NotBe(replica!.Value);
    }

    [Fact]
    public async Task Gocu_049_sonrasi_yedegin_bekleyen_onceki_kimligi_iki_kimlik_de_bilinmezse_korunur()
    {
        BootCurrentDatabase();
        var pending = Guid.NewGuid();
        var backup = BuildBackup(upTo: null, db =>
        {
            using var c = db.Open();
            c.Execute("UPDATE _meta SET ReplicaId = 'bozuk', PreviousReplicaId = @pending WHERE Id = 1",
                new { pending = pending.ToString("N") });
        });
        CorruptCurrentDatabase();

        (await RestoreAsync(backup)).Success.Should().BeTrue();

        new ReplicaIdentityRepository(new FileDb(_dbPath)).GetPendingPreviousReplicaId().Should().Be(pending,
            "yedeğin henüz bildirilmemiş önceki kimliği gerçek bir kimliktir; nöbetçi onu ezmez");
    }

    [Fact]
    public void Acilista_okunamayan_isaret_bu_acilista_cozulmemis_sayilir_isaret_kalir_hicbir_sey_yazilmaz()
    {
        var currentId = BootCurrentDatabase();
        var identity = new ReplicaIdentityRepository(new SqliteConnectionFactory(_dbPath));
        var marker = RestoreReplicaMarker.PathFor(_dbPath);
        File.WriteAllText(marker, Guid.NewGuid().ToString("N"));

        RestoreMarkerOutcome outcome;
        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))   // ör. virüs tarayıcı kilidi
            outcome = RestoreReplicaMarker.ApplyAtBoot(_dbPath, identity, NullLogger.Instance);

        outcome.Should().Be(new RestoreMarkerOutcome(null, Unresolved: true),
            "geri yükleme oldu ama sunucuya bildirilemiyor: çağıran bu süreçte v2 isteklerini kapatır");
        File.Exists(marker).Should().BeTrue("sonraki açılışta yeniden denenir");
        identity.GetReplicaId().Should().Be(currentId);
        identity.GetPendingPreviousReplicaId().Should().BeNull();
    }

    [Fact]
    public void Acilista_uygulanamayan_isaret_bu_acilista_cozulmemis_sayilir_isaret_kalir()
    {
        BootCurrentDatabase();
        var marker = RestoreReplicaMarker.PathFor(_dbPath);
        File.WriteAllText(marker, Guid.NewGuid().ToString("N"));
        var empty = Path.Combine(_dir, "bos.db");                          // _meta yok: kimlik yazımı patlar

        var outcome = RestoreReplicaMarker.ApplyAtBoot(
            _dbPath, new ReplicaIdentityRepository(new FileDb(empty)), NullLogger.Instance);

        outcome.Should().Be(new RestoreMarkerOutcome(null, Unresolved: true));
        File.Exists(marker).Should().BeTrue();
    }

    [Fact]
    public async Task Onceki_denemenin_isareti_basarisiz_ve_basarili_geri_yuklemede_kalmaz()
    {
        BootCurrentDatabase();
        var marker = RestoreReplicaMarker.PathFor(_dbPath);

        File.WriteAllText(marker, Guid.NewGuid().ToString("N"));
        var foreign = BuildBackup(upTo: 48, db =>
        {
            using var c = db.Open();
            c.Execute("DROP TABLE Label");                                // OrderDeck kimlik denetimine takılır
        });
        (await RestoreAsync(foreign)).Success.Should().BeFalse();
        File.Exists(marker).Should().BeFalse("başarısız geri yükleme eski işareti bırakmaz");

        File.WriteAllText(marker, Guid.NewGuid().ToString("N"));
        (await RestoreAsync(BuildBackup(upTo: null))).Success.Should().BeTrue();
        File.Exists(marker).Should().BeFalse("049 sonrası yedek kendi kimliğini döndürür; artık işaret silinir");
    }

    [Fact]
    public void Acilis_isareti_bozuksa_kopya_yine_geri_yuklenmis_bildirilir_kimlik_onceki_kimlikle_ayniysa_doner()
    {
        var currentId = BootCurrentDatabase();
        var factory = new SqliteConnectionFactory(_dbPath);
        var identity = new ReplicaIdentityRepository(factory);
        var marker = RestoreReplicaMarker.PathFor(_dbPath);

        // İşaretin varlığı geri yüklemenin kanıtı: içeriği bozuk olsa da kopya geri yüklenmiş bildirilir (§8.7, M20)
        // — önceki kimlik bilinmediği için rastgele bir nöbetçiyle. Sunucu yalnız alanın dolu olmasına bakar.
        File.WriteAllText(marker, "bozuk");
        var outcome = RestoreReplicaMarker.ApplyAtBoot(_dbPath, identity, NullLogger.Instance);
        outcome.Unresolved.Should().BeFalse();
        outcome.Previous.Should().NotBeNull().And.NotBe(currentId);
        File.Exists(marker).Should().BeFalse();
        identity.GetReplicaId().Should().Be(currentId);
        identity.GetPendingPreviousReplicaId().Should().Be(outcome.Previous);

        // Beklenmez ama korunur: işaretteki kimlik bu veritabanınınkiyle aynıysa yeni kimlik üretilir.
        File.WriteAllText(marker, currentId.ToString("N"));
        RestoreReplicaMarker.ApplyAtBoot(_dbPath, identity, NullLogger.Instance)
            .Should().Be(new RestoreMarkerOutcome(currentId, Unresolved: false));
        identity.GetReplicaId().Should().NotBe(currentId);
        identity.GetPendingPreviousReplicaId().Should().Be(currentId);
    }
}
