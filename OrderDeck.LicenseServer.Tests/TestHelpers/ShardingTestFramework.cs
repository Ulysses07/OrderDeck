using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework(
    "OrderDeck.LicenseServer.Tests.TestHelpers.ShardingTestFramework",
    "OrderDeck.LicenseServer.Tests")]

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// CI'da suite'i birden çok işe bölen xUnit test çerçevesi. Parçayı
/// assembly'nin kendisi seçiyor: <c>ORDERDECK_TEST_SHARD</c> ortam değişkenine
/// bakıp keşfedilen vakaların bir alt kümesini koşuyor.
///
/// Neden var: license-server-test tek iş olarak ~36 dk sürüyordu. Kritik yol,
/// seri koşan <c>sqlserver</c> koleksiyonuydu (her test yeni bir DB açıp bütün
/// göçleri baştan koşuyor, ~4 sn/test). InMemory testleri bu zincirle aynı
/// runner'da CPU paylaşınca zincir ~2× yavaşlıyordu (ölçüldü). Bu yüzden iki
/// ayrı eksen var: ilişkisel testler kendi parçalarında, InMemory testleri
/// kendi parçalarında.
///
/// <para><b>Değişken yoksa hiçbir şey değişmez</b>: süzgeç de manifest de yok,
/// yerel ve IDE koşusu eskisiyle birebir aynı. Değer bozuksa koşu düşer;
/// sessizce ya hepsini ya hiçbirini koşmak, eksik koşan bir parçayı yeşil
/// gösterirdi.</para>
///
/// <para>Bir CI parçasını yerelde birebir üretmek için (adet, build-test.yml'deki
/// changes işinin sayısı olmalı):
/// bash <c>ORDERDECK_TEST_SHARD=relational:0/4 dotnet test ...</c>,
/// PowerShell <c>$env:ORDERDECK_TEST_SHARD='relational:0/4'; dotnet test ...</c>
/// (değişken oturumda kalır; sonra <c>Remove-Item Env:ORDERDECK_TEST_SHARD</c>,
/// yoksa bir sonraki "tam" koşu da parça olur). İlişkisel parça tam koşu gibi
/// Docker ister.</para>
///
/// <para>Eksiksizlik CI'da denetlenir: her parça <c>ORDERDECK_TEST_SHARD_MANIFEST</c>
/// yoluna gördüğü ve seçtiği vakaları yazar; toplayıcı iş (license-server-test)
/// birleşimin her vakayı tam bir kez kapsadığını, <c>--list-tests</c>
/// çıktısıyla aynı olduğunu ve her parçanın TRX'inde tam olarak seçtiği
/// metotların koştuğunu doğrular (.github/scripts/check-test-shards.py).</para>
/// </summary>
public sealed class ShardingTestFramework(IMessageSink messageSink) : XunitTestFramework(messageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        => new ShardingTestFrameworkExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
}

/// <summary>
/// Yalnız yürütmeyi süzer; keşif (<c>--list-tests</c> dahil) her zaman bütün
/// vakaları görür. Toplayıcının bağımsız denetimi buna dayanıyor.
/// </summary>
public sealed class ShardingTestFrameworkExecutor(
    AssemblyName assemblyName,
    ISourceInformationProvider sourceInformationProvider,
    IMessageSink diagnosticMessageSink)
    : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
{
    protected override void RunTestCases(
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions)
    {
        // Bozuk değer burada atar: çerçevenin kurucusunda atsaydı xUnit
        // varsayılan çerçeveye sessizce geri düşer ve her şeyi koşardı.
        var shard = TestShard.Parse(Environment.GetEnvironmentVariable(TestShard.EnvVar));
        if (shard is null)
        {
            base.RunTestCases(testCases, executionMessageSink, executionOptions);
            return;
        }

        var all = testCases.Select(tc => (Case: tc, Entry: TestShard.Describe(tc))).ToList();
        var mine = all.Where(x => shard.Owns(x.Entry)).ToList();

        var manifestPath = Environment.GetEnvironmentVariable(TestShard.ManifestEnvVar);
        if (!string.IsNullOrWhiteSpace(manifestPath))
            TestShard.AppendManifest(manifestPath, shard, all.Select(x => x.Entry).ToList(), mine.Select(x => x.Entry).ToList());

        DiagnosticMessageSink.OnMessage(new DiagnosticMessage(
            $"Test parçası {shard}: {mine.Count}/{all.Count} vaka koşuyor."));

        // Manifest seçimi kanıtlar, yürütmeyi değil: buradan sonra eklenen bir
        // süzgeç manifesti tam gösterip testleri sessizce düşürürdü. Toplayıcı
        // bu yüzden TRX'te koşan metotları MINE ile ayrıca karşılaştırır.
        base.RunTestCases(mine.Select(x => x.Case).ToList(), executionMessageSink, executionOptions);
    }
}

public enum TestShardKind
{
    /// <summary>Koleksiyonu bir SQL Server konteyneri paylaşan testler (seri zincir).</summary>
    Relational,

    /// <summary>Geri kalan her şey (InMemory, paralel).</summary>
    InMemory,
}

/// <summary>Bir vakanın parçalama açısından kimliği.</summary>
/// <param name="Kind">Hangi eksende koştuğu.</param>
/// <param name="ClassName">Test sınıfının tam adı (iç içe sınıfta <c>Dış+İç</c>).</param>
/// <param name="MethodName">Test metodunun adı.</param>
/// <param name="DisplayName">Vakanın görünen adı (<c>--list-tests</c>'in bastığı);
/// verilmezse varsayılan biçim, yani <see cref="MethodKey"/>.</param>
public sealed record TestShardEntry(TestShardKind Kind, string ClassName, string MethodName, string? DisplayName = null)
{
    /// <summary>Manifestte parça bölüşümünün ve TRX karşılaştırmasının kimliği.</summary>
    public string MethodKey => ClassName + "." + MethodName;

    /// <summary><c>--list-tests</c> karşılaştırmasının anahtarı; bkz. <see cref="TestShard.DisplayRootOf"/>.</summary>
    public string DisplayRoot => TestShard.DisplayRootOf(DisplayName ?? MethodKey);

    /// <summary>Kovayı belirleyen anahtar. İlişkiselde metot düzeyi (zincir eşit
    /// bölünsün; teori satırları yine aynı parçada kalır), InMemory'de sınıf
    /// düzeyi (<c>IClassFixture&lt;ApiFactory&gt;</c> sınıf başına bir kez
    /// kurulsun).</summary>
    public string BucketKey => Kind == TestShardKind.Relational ? MethodKey : ClassName;
}

/// <summary>Bir parça: tür + sıra + o türün toplam parça sayısı.</summary>
public sealed record TestShard(TestShardKind Kind, int Index, int Count)
{
    public const string EnvVar = "ORDERDECK_TEST_SHARD";
    public const string ManifestEnvVar = "ORDERDECK_TEST_SHARD_MANIFEST";

    private static readonly Regex Pattern = new(
        @"^(?<kind>relational|inmemory):(?<index>\d{1,4})/(?<count>\d{1,4})$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>relational:2/4</c> ya da <c>inmemory:0/2</c>. Boş/yok → <c>null</c>
    /// (parçalama kapalı). Başka her şey <see cref="FormatException"/>.
    /// </summary>
    public static TestShard? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = Pattern.Match(value.Trim());
        if (!match.Success)
            throw new FormatException(
                $"{EnvVar}='{value}' okunamadı; beklenen biçim 'relational:<sıra>/<adet>' ya da 'inmemory:<sıra>/<adet>'.");

        var kind = match.Groups["kind"].Value == "relational" ? TestShardKind.Relational : TestShardKind.InMemory;
        var index = int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var count = int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (count < 1 || index >= count)
            throw new FormatException($"{EnvVar}='{value}': sıra 0 ile adet-1 arasında, adet en az 1 olmalı.");

        return new TestShard(kind, index, count);
    }

    /// <summary>
    /// Anahtarın kararlı özeti: SHA-256'nın ilk 4 baytı (little-endian).
    /// <c>string.GetHashCode()</c> KULLANILMAZ: .NET'te süreç başına rastgele,
    /// yani her parça farklı bir bölüşüm görür ve testler sessizce kaybolur.
    /// Bu özet süreçten, makineden ve yeni eklenen testlerden bağımsız.
    /// </summary>
    public static uint Hash(string key)
        => BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static int Bucket(string key, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return (int)(Hash(key) % (uint)count);
    }

    /// <summary>
    /// Koleksiyon tanımı, <see cref="SqlServerContainerFixture"/>'dan türeyen bir
    /// <c>ICollectionFixture&lt;T&gt;</c> taşıyorsa ilişkisel. Etiket ya da kaynak
    /// taraması yok: yeni bir konteyner fixture'ı da kendiliğinden yakalanır.
    /// Yanlış sınıflama test kaybettirmez (her parça Docker'lı), yalnız dengeyi bozar.
    /// </summary>
    public static TestShardKind Classify(Type? collectionDefinition)
    {
        if (collectionDefinition is null) return TestShardKind.InMemory;

        var relational = collectionDefinition.GetInterfaces().Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)
            && typeof(SqlServerContainerFixture).IsAssignableFrom(i.GetGenericArguments()[0]));
        return relational ? TestShardKind.Relational : TestShardKind.InMemory;
    }

    public static TestShardEntry Describe(IXunitTestCase testCase)
    {
        var method = testCase.TestMethod;
        var definition = method.TestClass.TestCollection.CollectionDefinition;
        Type? definitionType = definition switch
        {
            null => null,
            IReflectionTypeInfo reflection => reflection.Type,
            // Süreç içi koşuda hep yansıma tabanlı; değilse sınıflama tahmin olurdu.
            _ => throw new InvalidOperationException(
                $"Koleksiyon tanımı '{definition.Name}' yansıma tabanlı değil; test parçası belirlenemiyor."),
        };

        return new TestShardEntry(
            Classify(definitionType), method.TestClass.Class.Name, method.Method.Name, testCase.DisplayName);
    }

    /// <summary>
    /// Görünen adın kökü: ilk <c>(</c>'e kadarki kısım, kırpılmış. Teori satırları
    /// (<c>Metot(x: 1)</c>, <c>Metot(x: 2)</c>) aynı kökü paylaşır. <c>--list-tests</c>
    /// görünen adı bastığı için karşılaştırma bununla yapılır, <see cref="TestShardEntry.MethodKey"/>
    /// ile değil: bir teste <c>[Fact(DisplayName = ...)]</c> verilir ya da
    /// xunit.runner.json'a <c>methodDisplay: method</c> girerse görünen ad
    /// <c>Sınıf.Metot</c> olmaktan çıkar. check-test-shards.py, <c>--list-tests</c>
    /// satırlarına aynı kuralı uygular; biri değişirse öbürü de değişmeli.
    /// </summary>
    public static string DisplayRootOf(string displayName)
    {
        var paren = displayName.IndexOf('(', StringComparison.Ordinal);
        return (paren < 0 ? displayName : displayName[..paren]).Trim();
    }

    public bool Owns(TestShardEntry entry)
        => entry.Kind == Kind && Bucket(entry.BucketKey, Count) == Index;

    /// <summary>
    /// Manifest satırları (sekmeyle ayrılmış):
    /// <c>SHARD kind index count vakaların-tümü seçilen-vakalar</c>, her metot (ve
    /// görünen ad kökü) için <c>ALL kind Sınıf.Metot kök</c>, seçilenler için
    /// <c>MINE Sınıf.Metot</c>. Karşılaştırma metot düzeyinde, UniqueID ile değil:
    /// teori verisindeki Guid gibi değerler UniqueID'yi süreçten sürece değiştirebilir.
    /// </summary>
    public static void AppendManifest(
        string path, TestShard shard, IReadOnlyCollection<TestShardEntry> all, IReadOnlyCollection<TestShardEntry> mine)
    {
        var text = new StringBuilder();
        text.Append("SHARD\t").Append(shard.KindLabel).Append('\t').Append(shard.Index).Append('\t').Append(shard.Count)
            .Append('\t').Append(all.Count).Append('\t').Append(mine.Count).Append('\n');
        foreach (var entry in all.DistinctBy(e => (e.Kind, e.MethodKey, e.DisplayRoot))
                     .OrderBy(e => e.MethodKey, StringComparer.Ordinal)
                     .ThenBy(e => e.DisplayRoot, StringComparer.Ordinal))
        {
            text.Append("ALL\t").Append(LabelOf(entry.Kind)).Append('\t').Append(entry.MethodKey)
                .Append('\t').Append(entry.DisplayRoot).Append('\n');
        }
        foreach (var key in mine.Select(e => e.MethodKey).Distinct().Order(StringComparer.Ordinal))
            text.Append("MINE\t").Append(key).Append('\n');

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.AppendAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string KindLabel => LabelOf(Kind);

    public static string LabelOf(TestShardKind kind) => kind == TestShardKind.Relational ? "relational" : "inmemory";

    public override string ToString() => $"{KindLabel}:{Index}/{Count}";
}
