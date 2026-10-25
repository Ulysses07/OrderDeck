using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Testler için tek bir SQL Server container'ı ayağa kaldırır.
///
/// Neden var: suite'in geri kalanı InMemory üzerinde koşuyor ve bu bilinçli
/// bir tercih — hızlı, Docker istemiyor. Ama InMemory'de <b>eşzamanlılık yok</b>:
/// aynı satıra iki eşzamanlı yazma yarışmıyor, <c>ExecuteUpdate/Delete</c> hiç
/// çalışmıyor. Bu ikisine dayanan düzeltmeler InMemory'de "test edilirse" CI
/// yeşil yanar ama düzeltmenin çalıştığına dair hiçbir kanıt üretmez. Bu
/// fixture o boşluğu kapatır.
///
/// Container koleksiyon başına <b>bir kez</b> başlar (başlangıç ~30-60 sn);
/// izolasyon container değil <b>veritabanı</b> düzeyinde sağlanır:
/// <see cref="CreateDatabaseAsync"/> her test için ayrı bir DB açar ve test
/// bittikten sonra o DB arka planda silinir (bkz. <see cref="DropTestDatabasesAttribute"/>).
///
/// Motor bilerek SQL Server: prod bugün bu. Postgres göçünde bu dosyada
/// <c>MsSqlBuilder</c> yerine <c>PostgreSqlBuilder</c> kullanılacak ve testler
/// göçün güvenlik ağı olacak.
/// </summary>
public class SqlServerContainerFixture : IAsyncLifetime
{
    // İmaj sabitleniyor: parametresiz kurucu artık kullanımdan kalktı ve
    // "latest" etiketi CI'ı bizden habersiz kırabilir.
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    // SQL Server Linux'ta varsayılan olarak makinenin RAM'inin %80'ini hedefler
    // (memory.memorylimitmb) ve tampon havuzunu geri vermez. CI runner'ı 15,6 GB:
    // Developer konteyneri ~12,5 GB'a kadar büyümeye izinliydi, testhost da
    // 7 GB'a çıkınca VM swap'te boğuldu ve runner "shutdown signal" ile kapandı
    // (2026-10-22, üç koşu). Test DB'leri küçük; 2 GB (SQL Server'ın alt sınırı
    // ~2000 MB) bol bol yetiyor. Express sürümü tampon havuzunu zaten ~1,4 GB ile
    // sınırlıyor, ama bu değer sürecin tamamını (tampon havuzu dışındaki
    // bellek dahil) sınırlar — o yüzden iki konteynere de verilir.
    private const string MemoryLimitMb = "2048";

    // DisposeAsync kuyruktaki silmeleri en çok bu kadar bekler. Kuyrukta normalde
    // en çok bir testin DB'leri durur, silme başına birkaç saniye; süre dolarsa
    // kalanlar konteynerle gider ve yalnız tanılama iletisine yazılır.
    private static readonly TimeSpan DropDrainTimeout = TimeSpan.FromSeconds(60);

    // Koleksiyon başına tek örnek; DropTestDatabasesAttribute onu buradan bulur.
    private static readonly ConcurrentDictionary<Type, SqlServerContainerFixture> Running = new();

    // Yalnız CreateDatabaseAsync'in ürettiği adlar silinebilir — DropDatabaseAsync
    // dışarıdan bir bağlantı dizesi alıyor, master'ı ya da başka bir DB'yi
    // düşürmesin.
    private static readonly Regex TestDatabaseName = new("^t[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private readonly MsSqlContainer _container;
    private readonly IMessageSink _diagnostics;

    private readonly object _gate = new();
    private readonly List<string> _open = new();     // açıldı, testi sürüyor olabilir
    private readonly List<string> _finished = new(); // testi bitti, sıradaki CreateDatabaseAsync kuyruğa verir

    // Arka plan silme kuyruğu: tek okuyucu, sırayla siler (aynı anda tek DROP).
    private readonly Channel<string> _dropQueue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _dropStop = new();
    private Task _dropWorker = Task.CompletedTask;
    private int _queuedDrops; // kuyruğa girdi, silme denemesi henüz bitmedi

    private int _created;
    private int _dropped;
    private int _dropFailures;

    public SqlServerContainerFixture(IMessageSink diagnostics) : this(diagnostics, builder => builder) { }

    /// <summary>Aynı imajın başka bir yapılandırması için (bkz.
    /// <see cref="SqlServerExpressContainerFixture"/>).</summary>
    protected SqlServerContainerFixture(IMessageSink diagnostics, Func<MsSqlBuilder, MsSqlBuilder> configure)
    {
        _diagnostics = diagnostics;
        _container = configure(new MsSqlBuilder(Image)
                .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", MemoryLimitMb))
            .Build();
    }

    public async Task InitializeAsync()
    {
        // Kayıt fixture TÜRÜNE göre: bir tür ikinci bir koleksiyonda da kullanılırsa
        // ikinci örnek birincinin kaydını ezer, After() başka koleksiyonun koşan testinin
        // DB'sini "bitti"ye taşır ve birinci örneğin DB'leri hiç silinmez (sızıntı sessizce
        // döner). Bunu sessiz bırakmak yerine kurulumda düşür.
        await _container.StartAsync();
        if (!Running.TryAdd(GetType(), this))
            throw new InvalidOperationException(
                $"{GetType().Name} yalnız tek bir test koleksiyonunda kullanılabilir (DB silme kaydı türe bağlı).");
        _dropWorker = Task.Run(() => DrainDropQueueAsync(_dropStop.Token));
    }

    public async Task DisposeAsync()
    {
        Running.TryRemove(new KeyValuePair<Type, SqlServerContainerFixture>(GetType(), this));

        // Kuyruktaki silmeler bitsin: sayaçlar doğru kalsın, konteyner yarım bir
        // DROP'un altından çekilmesin. Son testin DB'leri kuyruğa hiç girmez
        // (ardından CreateDatabaseAsync yok), konteynerle gider. Bekleme başarısız
        // olursa koleksiyon düşmez, yalnız tanılamaya yazılır.
        _dropQueue.Writer.TryComplete();
        try
        {
            await _dropWorker.WaitAsync(DropDrainTimeout);
        }
        catch (TimeoutException)
        {
            _dropStop.Cancel();
            Log($"{GetType().Name}: kuyruktaki DB silmeleri {DropDrainTimeout.TotalSeconds:0} sn içinde bitmedi, " +
                "kalanlar konteynerle gidiyor.");
        }
        catch (Exception ex)
        {
            Log($"{GetType().Name}: DB silme kuyruğu durdu: {ex.GetType().Name}: {ex.Message}");
        }

        int remaining;
        lock (_gate) remaining = _open.Count + _finished.Count;
        remaining += Volatile.Read(ref _queuedDrops);
        Log($"{GetType().Name}: {_created} test DB'si açıldı, {Volatile.Read(ref _dropped)} silindi, " +
            $"{Volatile.Read(ref _dropFailures)} silinemedi, {remaining} konteynerle birlikte gidiyor.");
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Test'e özel boş bir veritabanı açar ve ona bağlanan connection string'i
    /// döner. Testler arası izolasyon buradan gelir — aynı container'ı paylaşan
    /// iki test birbirinin satırlarını görmez.
    ///
    /// DB açılınca, testi bitmiş önceki DB'leri arka plan silme kuyruğuna verir;
    /// silinmelerini BEKLEMEZ. Kuyruğa burada veriliyor, testin bittiği anda
    /// değil: xUnit'te <see cref="BeforeAfterTestAttribute.After"/> test
    /// sınıfının <c>DisposeAsync</c>'inden ÖNCE çalışır, yani o an fabrikanın
    /// host'u hâlâ ayaktadır ve DB'yi kullanıyor olabilir. Koleksiyon seri
    /// koştuğu için bir sonraki testin açılışı, öncekinin bütünüyle kapandığı ilk
    /// güvenli andır; silme bu andan önce başlamaz.
    ///
    /// Neden beklemiyor: silme DB başına ~3 sn sürüyor ve neredeyse tamamı
    /// SINGLE_USER ... ROLLBACK IMMEDIATE'in DB'de kalan oturumu kesip beklemesi
    /// (PRINT_ROLLBACK_PROGRESS, CPU ~5 ms; yerelde ölçüldü). Bunu her testin
    /// açılışında beklemek seri koşan koleksiyonun kritik yolunu uzattı: #539
    /// sonrası master'da license-server-test 60 dk sınırında iptal edildi. (CI'da
    /// silme başına süre ölçülmedi; zincir farkını ~384 DB'ye bölmek ~2,4 sn
    /// veriyor.) Kuyrukta silme sıradaki testin göçleri ve gövdesiyle aynı anda
    /// yürür; sınır korunur, konteynerde aynı anda yalnız birkaç test DB'si durur.
    ///
    /// Neden CREATE'ten sonra: CREATE DATABASE, eşzamanlı bir ALTER/DROP DATABASE
    /// bitene kadar bekliyor. Kuyruk CREATE'ten önce dolunca kazanç CREATE'te geri
    /// gitti (yerel ölçüm: CREATE ~1 sn → ~3,6 sn). CREATE'ten sonra verilse de
    /// hızlı biten bir testin ardından ya da ikinci DB açan bir testte CREATE
    /// arka plandaki silmeye denk gelebilir; o durumda yalnız o silmeyi bekler.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var dbName = "t" + Guid.NewGuid().ToString("N");

        try
        {
            await using var conn = new SqlConnection(_container.GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // dbName tamamen bizim ürettiğimiz sabit alfabe (harf + hex), dışarıdan
            // veri almıyor; yine de köşeli parantezle sarılıyor.
            cmd.CommandText = $"CREATE DATABASE [{dbName}]";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            QueueFinishedDatabasesForDrop();
        }

        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = dbName,
        }.ConnectionString;

        lock (_gate)
        {
            _open.Add(connectionString);
            _created++;
        }

        return connectionString;
    }

    /// <summary>
    /// <see cref="CreateDatabaseAsync"/>'in açtığı bir DB'yi siler. Normalde
    /// çağırmaya gerek yok — testi biten DB'ler kendiliğinden, arka planda
    /// silinir; bir testin içinde çok sayıda DB açılıp hemen bırakılıyorsa elle
    /// çağrılabilir.
    ///
    /// Asla atmaz: silinemeyen bir DB testi düşürmemeli, yalnız tanılama
    /// iletisine yazılır (<c>-- xUnit.DiagnosticMessages=true</c> ile görünür)
    /// ve koleksiyon sonunda konteynerle birlikte gider.
    /// </summary>
    public async Task DropDatabaseAsync(string connectionString)
    {
        string dbName;
        try
        {
            dbName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _dropFailures);
            Log($"Test DB'si silinemedi, bağlantı dizesi okunamadı: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!TestDatabaseName.IsMatch(dbName))
        {
            Interlocked.Increment(ref _dropFailures);
            Log($"'{dbName}' bir test DB'si değil, silinmedi.");
            return;
        }

        try
        {
            // Her test DB'sinin kendi SqlClient havuzu var (havuz anahtarı bağlantı
            // dizesinin kendisi) ve havuzdaki boşta bağlantılar dakikalarca açık
            // kalıyor: hem testhost'ta bellek tutuyor hem DROP'u "kullanımda" diye
            // reddettiriyor. Önce havuz boşaltılır; dizeyi değiştirip ayrı bir
            // havuz açmış biri kaldıysa SINGLE_USER ... ROLLBACK IMMEDIATE onu
            // sunucu tarafında keser.
            using (var pooled = new SqlConnection(connectionString))
            {
                SqlConnection.ClearPool(pooled);
            }

            await using var conn = new SqlConnection(_container.GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // dbName yukarıdaki desenle doğrulandı: yalnız 't' + 32 hex.
            cmd.CommandText = $"""
                IF DB_ID(N'{dbName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{dbName}];
                END
                """;
            await cmd.ExecuteNonQueryAsync();
            Interlocked.Increment(ref _dropped);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _dropFailures);
            Log($"Test DB'si silinemedi ({dbName}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Bir test bitti: o ana kadar açılan DB'lerin hepsi artık
    /// silinebilir (bkz. <see cref="CreateDatabaseAsync"/>).</summary>
    internal static void MarkTestFinished(Type fixtureType)
    {
        if (!Running.TryGetValue(fixtureType, out var fixture)) return;
        lock (fixture._gate)
        {
            fixture._finished.AddRange(fixture._open);
            fixture._open.Clear();
        }
    }

    // Yalnız "bitti" listesindekiler kuyruğa girer; açık (testi sürüyor olabilecek)
    // DB'ye dokunulmaz.
    private void QueueFinishedDatabasesForDrop()
    {
        string[] batch;
        lock (_gate)
        {
            batch = _finished.ToArray();
            _finished.Clear();
        }

        foreach (var connectionString in batch)
        {
            Interlocked.Increment(ref _queuedDrops);
            if (_dropQueue.Writer.TryWrite(connectionString)) continue;

            // Kuyruk kapandı (DisposeAsync başladı): DB konteynerle gider, sayımda kalsın.
            Interlocked.Decrement(ref _queuedDrops);
            lock (_gate) _finished.Add(connectionString);
        }
    }

    private async Task DrainDropQueueAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var connectionString in _dropQueue.Reader.ReadAllAsync(stop))
            {
                await DropDatabaseAsync(connectionString); // atmaz
                Interlocked.Decrement(ref _queuedDrops);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // DisposeAsync beklemeyi bıraktı; kuyrukta kalanlar konteynerle gider.
        }
    }

    private void Log(string message) => _diagnostics.OnMessage(new DiagnosticMessage(message));
}

/// <summary>
/// Prod'un sürümü SQL Server Express: ONLINE indeks işlemi yok, tampon havuzu
/// sınırlı. Yalnız prod sürümüne özgü bir davranışı kanıtlayan testler kullanır
/// (göçlerin Express'te koşması: satış senkronu S1, spec 2026-10-13 §10.1-12;
/// ortak bakiye S1, spec 2026-10-11 §8.2-11). Ayrı bir container olduğu için
/// genel koleksiyona girmez; kendi koleksiyonunda
/// (<see cref="SqlServerExpressCollection"/>) bir kez başlar. Sınıf başına
/// <c>IClassFixture</c> her sınıfa ayrı bir Express container'ı açardı.
/// </summary>
public sealed class SqlServerExpressContainerFixture : SqlServerContainerFixture
{
    public SqlServerExpressContainerFixture(IMessageSink diagnostics)
        : base(diagnostics, builder => builder.WithEnvironment("MSSQL_PID", "Express")) { }
}

/// <summary>
/// Koleksiyondaki her testin sonunda, o teste kadar açılmış test DB'lerini
/// "bitti" diye işaretler; sıradaki <see cref="SqlServerContainerFixture.CreateDatabaseAsync"/>
/// onları arka plan silme kuyruğuna verir.
///
/// Neden test başına bir dispose çağrısı değil: ~80 sınıf DB'yi dört farklı
/// biçimde açıyor (InitializeAsync'te, test gövdesinde, yardımcı kit içinde;
/// bazıları aynı DB'ye ikinci bir fabrika bağlıyor). Fabrika dispose'una
/// bağlamak ikinci fabrikanın DB'yi erkenden düşürmesi demek; her sınıfa elle
/// eklemek de yeni bir testin unutması demek. Koleksiyon tanımındaki bu öznitelik
/// koleksiyona giren her testi kendiliğinden kapsar.
///
/// Dayandığı iki xUnit garantisi: (1) bir koleksiyonun testleri seri koşar;
/// (2) <see cref="After"/> her testten sonra, başarısız olsa bile çağrılır.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DropTestDatabasesAttribute(Type fixtureType) : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest) => SqlServerContainerFixture.MarkTestFinished(fixtureType);
}

/// <summary>
/// Container'ı paylaşan testler bu koleksiyona girer; böylece container test
/// sınıfı başına değil, koleksiyon başına bir kez ayağa kalkar.
/// </summary>
[CollectionDefinition(Name)]
[DropTestDatabases(typeof(SqlServerContainerFixture))]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture>
{
    public const string Name = "sqlserver";
}

/// <summary>
/// Express container'ını paylaşan testler (<see cref="SqlServerExpressContainerFixture"/>):
/// container koleksiyon başına bir kez kalkar, izolasyon yine veritabanı düzeyinde.
/// </summary>
[CollectionDefinition(Name)]
[DropTestDatabases(typeof(SqlServerExpressContainerFixture))]
public sealed class SqlServerExpressCollection : ICollectionFixture<SqlServerExpressContainerFixture>
{
    public const string Name = "sqlserver-express";
}
