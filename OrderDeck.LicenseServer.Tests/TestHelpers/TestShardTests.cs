using FluentAssertions;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// <see cref="ShardingTestFramework"/>'ün saf parçaları. Bölüşümün kendisi
/// (her vakanın tam bir parçada koşması) CI'daki toplayıcı işte, gerçek
/// manifestlerle denetleniyor; burası onun dayandığı sözleşmeleri sabitler.
/// </summary>
public sealed class TestShardTests
{
    [Fact]
    public void Degisken_yoksa_parcalama_kapali()
    {
        TestShard.Parse(null).Should().BeNull();
        TestShard.Parse("").Should().BeNull();
        TestShard.Parse("   ").Should().BeNull();
    }

    [Theory]
    [InlineData("relational:2/4", TestShardKind.Relational, 2, 4)]
    [InlineData("inmemory:0/2", TestShardKind.InMemory, 0, 2)]
    [InlineData("inmemory:0/1", TestShardKind.InMemory, 0, 1)]
    [InlineData(" relational:3/4 ", TestShardKind.Relational, 3, 4)]
    public void Gecerli_deger_okunur(string value, TestShardKind kind, int index, int count)
        => TestShard.Parse(value).Should().Be(new TestShard(kind, index, count));

    [Theory]
    [InlineData("relational")]
    [InlineData("relational:4/4")]
    [InlineData("relational:5/4")]
    [InlineData("relational:0/0")]
    [InlineData("relational:-1/4")]
    [InlineData("Relational:0/4")]
    [InlineData("sqlserver:0/4")]
    [InlineData("inmemory:0")]
    [InlineData("inmemory:a/2")]
    [InlineData("inmemory:0/2/3")]
    [InlineData("0/2")]
    public void Bozuk_deger_sessizce_yutulmaz(string value)
    {
        // Bozuk bir değer ya her şeyi ya hiçbir şeyi koşturursa parça yeşil
        // görünür ama yanlış alt kümeyi koşmuş olur.
        var act = () => TestShard.Parse(value);
        act.Should().Throw<FormatException>().WithMessage($"*{TestShard.EnvVar}*");
    }

    [Theory]
    // Altın vektörler: Python'da hashlib.sha256(utf8).digest()[:4] little-endian ile
    // bağımsız hesaplandı. Biri özeti string.GetHashCode()'a (süreç başına rastgele)
    // ya da başka bir algoritmaya çevirirse burası kırılır; o hâlde parçalar aynı
    // vakayı farklı kovaya koyup testleri kaybederdi.
    [InlineData("OrderDeck.Shard.Golden.Alpha", 2979493695u, 3, 1)]
    [InlineData("OrderDeck.Shard.Golden.Beta.Method", 610842674u, 2, 0)]
    [InlineData("Şğüİöç-key", 2662907475u, 3, 1)]
    public void Ozet_kararli_ve_dile_bagli_degil(string key, uint hash, int bucketOf4, int bucketOf2)
    {
        TestShard.Hash(key).Should().Be(hash);
        TestShard.Bucket(key, 4).Should().Be(bucketOf4);
        TestShard.Bucket(key, 2).Should().Be(bucketOf2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void Her_anahtar_tam_bir_parcaya_duser(int count)
    {
        var keys = Enumerable.Range(0, 2000).Select(i => $"Ns.Class{i % 97}.Method{i}").ToList();
        var shards = Enumerable.Range(0, count).Select(i => new TestShard(TestShardKind.Relational, i, count)).ToList();

        foreach (var key in keys)
        {
            var entry = new TestShardEntry(TestShardKind.Relational, key[..key.LastIndexOf('.')], key[(key.LastIndexOf('.') + 1)..]);
            TestShard.Bucket(entry.BucketKey, count).Should().BeInRange(0, count - 1);
            shards.Count(s => s.Owns(entry)).Should().Be(1, $"'{key}' tam bir parçada koşmalı");
        }
    }

    [Fact]
    public void Parca_yalniz_kendi_turunu_alir()
    {
        var relational = new TestShardEntry(TestShardKind.Relational, "Ns.C", "M");
        var inMemory = new TestShardEntry(TestShardKind.InMemory, "Ns.C", "M");

        new TestShard(TestShardKind.InMemory, 0, 1).Owns(relational).Should().BeFalse();
        new TestShard(TestShardKind.Relational, 0, 1).Owns(inMemory).Should().BeFalse();
        new TestShard(TestShardKind.Relational, 0, 1).Owns(relational).Should().BeTrue();
        new TestShard(TestShardKind.InMemory, 0, 1).Owns(inMemory).Should().BeTrue();
    }

    [Fact]
    public void Iliskiselde_metot_InMemoryde_sinif_duzeyinde_bolunur()
    {
        // İlişkisel zincir metot düzeyinde bölünür (ağır sınıflar tek kovaya yığılmasın);
        // InMemory sınıf düzeyinde (IClassFixture<ApiFactory> sınıf başına bir kez kurulsun).
        new TestShardEntry(TestShardKind.Relational, "Ns.C", "M").BucketKey.Should().Be("Ns.C.M");
        new TestShardEntry(TestShardKind.InMemory, "Ns.C", "M").BucketKey.Should().Be("Ns.C");
    }

    [Fact]
    public void Konteyner_koleksiyonlari_iliskisel_sayilir()
    {
        TestShard.Classify(typeof(SqlServerCollection)).Should().Be(TestShardKind.Relational);
        TestShard.Classify(typeof(SqlServerExpressCollection)).Should().Be(TestShardKind.Relational);
    }

    [Fact]
    public void Koleksiyon_tanimi_olmayan_ya_da_baska_fixture_tasiyan_sinif_InMemory()
    {
        TestShard.Classify(null).Should().Be(TestShardKind.InMemory);
        TestShard.Classify(typeof(UnrelatedFixtureCollection)).Should().Be(TestShardKind.InMemory);
    }

    [Fact]
    public void Manifest_eklenerek_yazilir_ve_metot_duzeyinde_tekillesir()
    {
        var path = Path.Combine(Path.GetTempPath(), "orderdeck-shard-" + Guid.NewGuid().ToString("N"), "m.manifest");
        try
        {
            var shard = new TestShard(TestShardKind.InMemory, 0, 1);
            var row1 = new TestShardEntry(TestShardKind.InMemory, "Ns.C", "Theory");
            var row2 = new TestShardEntry(TestShardKind.InMemory, "Ns.C", "Theory");
            var rel = new TestShardEntry(TestShardKind.Relational, "Ns.R", "M");

            TestShard.AppendManifest(path, shard, [row1, row2, rel], [row1, row2]);

            File.ReadAllLines(path).Should().Equal(
                "SHARD\tinmemory\t0\t1\t3\t2",
                "ALL\tinmemory\tNs.C.Theory",
                "ALL\trelational\tNs.R.M",
                "MINE\tNs.C.Theory");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private sealed class UnrelatedFixture;

    private sealed class UnrelatedFixtureCollection : ICollectionFixture<UnrelatedFixture>;
}
