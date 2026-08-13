using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using OrderDeck.App.Services;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Katalog kapak fotoğrafı önbelleğinin dosya sözleşmesi. WPF'e dokunmuyor
/// (Application singleton'ı gerekmez) — düz sınıf testi.
/// </summary>
public class CatalogPhotoCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "od-photo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Beklenen dosya adını üretimden BAĞIMSIZ hesaplar: hex'i
    /// <c>Convert.ToHexString</c> ile değil bayt bayt <c>x2</c> ile kuruyor,
    /// böylece test üretim kodunun formatlama tercihini tekrarlamış olmuyor.
    /// </summary>
    private static string ExpectedFileName(string objectKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(objectKey));
        return string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)))
               + ".img";
    }

    private string[] FileNamesInRoot()
        => Directory.Exists(_root)
            ? Directory.GetFiles(_root).Select(f => Path.GetFileName(f)!).Order().ToArray()
            : [];

    [Fact]
    public void Save_then_resolve_round_trips_a_key_with_slashes()
    {
        var cache = new CatalogPhotoCache(_root);
        // Gerçek R2 nesne anahtarı eğik çizgi içerir; doğrudan dosya adı olamaz.
        const string key = "abc/products/def/kapak.img";

        cache.Has(key).Should().BeFalse();
        cache.Save(key, [1, 2, 3]);

        cache.Has(key).Should().BeTrue();
        var path = cache.ResolveAbsolute(key);
        File.ReadAllBytes(path!).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Save_writes_to_the_lowercase_sha256_hex_of_the_key()
    {
        var cache = new CatalogPhotoCache(_root);
        const string key = "abc/products/def/kapak.img";

        cache.Save(key, [1, 2, 3]);

        // NEDEN adı birebir çiviliyoruz: bu KALICI bir disk önbelleği. Adlandırma
        // sessizce değişirse (başka özet, büyük harf hex, "eğik çizgileri alt
        // çizgiye çevir" basitleştirmesi) her kullanıcının diskindeki tüm dosyalar
        // bir uygulama güncellemesinde bir anda yetim kalır ve baştan indirilir.
        // Adı Directory.GetFiles ile okuyoruz: File.Exists Windows'ta büyük/küçük
        // harf ayırmadığı için harf durumunu ancak diskteki gerçek ad kanıtlar.
        FileNamesInRoot().Should().Equal(ExpectedFileName(key));
        File.ReadAllBytes(Path.Combine(_root, ExpectedFileName(key)))
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Save_keeps_a_traversal_shaped_key_inside_the_root()
    {
        var cache = new CatalogPhotoCache(_root);
        // Sınıfın en yüksek sesli iddiası: özet aldığımız için ".." köke
        // kaçamaz. Bozuk/kötü niyetli bir anahtar kök dışına dosya yazmamalı.
        const string key = "../../../../Windows/Temp/pwned";

        cache.Save(key, [42]);

        var written = Directory.GetFiles(_root);
        written.Should().ContainSingle();
        Path.GetFullPath(written[0])
            .Should().StartWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar);
        Path.GetFileName(written[0]).Should().Be(ExpectedFileName(key));
        cache.Has(key).Should().BeTrue();
    }

    [Fact]
    public void Key_to_file_mapping_survives_a_new_cache_instance()
    {
        const string key = "abc/products/def/kapak.img";
        new CatalogPhotoCache(_root).Save(key, [1, 2, 3]);

        // Önbellek uygulama yeniden başlayınca da işe yaramalı: eşleme örneğe
        // değil yalnız anahtara bağlı olmalı (rastgele tuz, oturum sayacı vb. yok).
        var afterRestart = new CatalogPhotoCache(_root);

        afterRestart.Has(key).Should().BeTrue();
        afterRestart.ResolveAbsolute(key).Should().Be(Path.Combine(_root, ExpectedFileName(key)));
    }

    [Fact]
    public void ResolveAbsolute_returns_null_for_unknown_or_empty_keys()
    {
        var cache = new CatalogPhotoCache(_root);

        // Anahtar yoksa/boşsa kart placeholder'a düşmeli, patlamamalı.
        cache.ResolveAbsolute(null).Should().BeNull();
        cache.ResolveAbsolute("").Should().BeNull();
        cache.ResolveAbsolute("   ").Should().BeNull();
        cache.ResolveAbsolute("hic/olmayan.img").Should().BeNull();
    }

    [Fact]
    public void Save_overwrites_previous_bytes_for_the_same_key()
    {
        var cache = new CatalogPhotoCache(_root);
        const string key = "abc/products/def/kapak.img";

        cache.Save(key, [1, 2, 3, 4]);
        cache.Save(key, [9]);

        // Kısalan içerik eskisinin kuyruğunu bırakmamalı.
        File.ReadAllBytes(cache.ResolveAbsolute(key)!).Should().Equal(9);
    }

    [Fact]
    public void Save_consumes_the_temp_file_left_by_a_crashed_write()
    {
        var cache = new CatalogPhotoCache(_root);
        const string key = "abc/products/def/kapak.img";
        Directory.CreateDirectory(_root);
        // Geçici ad anahtar başına sabit olduğu için çökmüş bir turun artığı
        // tam olarak buraya düşer; sıradaki Save aynı yolu kullanacak.
        var temp = Path.Combine(_root, ExpectedFileName(key) + ".tmp");
        File.WriteAllBytes(temp, [66, 66, 66, 66]);

        cache.Save(key, [9, 9]);

        // Save geçiciye yazıp taşıdığı için artık TÜKETİLİR: klasörde tek dosya
        // kalır. Doğrudan hedefe yazan bir uygulamada çöp .tmp yerinde dururdu —
        // yani bu iddia yalnızca yerine-koyma gerçekten taşımayla yapılınca doğru.
        FileNamesInRoot().Should().Equal(ExpectedFileName(key));
        File.ReadAllBytes(Path.Combine(_root, ExpectedFileName(key))).Should().Equal(9, 9);
    }

    [Fact]
    public void Save_rejects_a_blank_key_instead_of_writing_an_orphan()
    {
        var cache = new CatalogPhotoCache(_root);

        // Boş anahtarla yazılan dosyayı Has() asla göremez (ResolveAbsolute
        // boşta null döner) ama Prune onu canlı sayar → ölümsüz yetim: her tur
        // yeniden indirilir, hiç silinmez. Sessiz düzeltme yerine hata.
        var blank = () => cache.Save("   ", [1]);
        var empty = () => cache.Save("", [1]);

        blank.Should().Throw<ArgumentException>();
        empty.Should().Throw<ArgumentException>();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public void Prune_deletes_files_whose_key_is_no_longer_live()
    {
        var cache = new CatalogPhotoCache(_root);
        cache.Save("a/kalan.img", [1]);
        cache.Save("a/giden.img", [2]);

        cache.Prune(["a/kalan.img"]);

        cache.Has("a/kalan.img").Should().BeTrue();
        cache.Has("a/giden.img").Should().BeFalse();
    }

    [Fact]
    public void Prune_ignores_blank_keys_in_the_live_list()
    {
        var cache = new CatalogPhotoCache(_root);
        cache.Save("a/kalan.img", [1]);
        cache.Save("a/giden.img", [2]);

        // Fotoğrafsız ürün olağan; listede null/boş anahtar görülecek. Tek bir
        // null bütün senkron turunu düşürmemeli (özet alma null'da patlar).
        var act = () => cache.Prune(["a/kalan.img", null, "", "   "]);

        act.Should().NotThrow();
        cache.Has("a/kalan.img").Should().BeTrue();
        cache.Has("a/giden.img").Should().BeFalse();
    }

    [Fact]
    public void Prune_sweeps_leftover_temp_files()
    {
        var cache = new CatalogPhotoCache(_root);
        cache.Save("a/kalan.img", [1]);
        // Yazma sırasında çökmüş bir tur böyle bir artık bırakır: canlı
        // listede karşılığı olamaz, temizlikte düşmeli.
        var stray = Path.Combine(_root, "deadbeef.img.tmp");
        File.WriteAllBytes(stray, [7]);

        cache.Prune(["a/kalan.img"]);

        File.Exists(stray).Should().BeFalse();
        cache.Has("a/kalan.img").Should().BeTrue();
    }

    [Fact]
    public void Prune_survives_a_locked_file_and_still_sweeps_the_rest()
    {
        var cache = new CatalogPhotoCache(_root);
        cache.Save("a/kalan.img", [1]);
        cache.Save("a/kilitli.img", [2]);
        cache.Save("a/giden.img", [3]);

        // Kart görseli hâlâ bağlıysa dosya kilitlidir; Windows silme denemesini
        // IOException ile reddeder. Bir dosyanın kilidi bütün turu düşürmemeli.
        using (File.Open(cache.ResolveAbsolute("a/kilitli.img")!,
                         FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => cache.Prune(["a/kalan.img"]);

            act.Should().NotThrow();
            // Kilitli olan bu tur atlanır (sonraki turda düşer), diğerleri düşer.
            cache.Has("a/kilitli.img").Should().BeTrue();
            cache.Has("a/kalan.img").Should().BeTrue();
            cache.Has("a/giden.img").Should().BeFalse();
        }
    }

    [Fact]
    public void Prune_survives_a_read_only_file_and_still_sweeps_the_rest()
    {
        var cache = new CatalogPhotoCache(_root);
        cache.Save("a/kalan.img", [1]);
        cache.Save("a/salt-okunur.img", [2]);
        cache.Save("a/giden.img", [3]);

        var readOnly = cache.ResolveAbsolute("a/salt-okunur.img")!;
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            // Salt-okunur dosyayı Windows IOException ile DEĞİL
            // UnauthorizedAccessException ile reddeder; ikinci catch bunun için.
            var act = () => cache.Prune(["a/kalan.img"]);

            act.Should().NotThrow();
            cache.Has("a/salt-okunur.img").Should().BeTrue();
            cache.Has("a/kalan.img").Should().BeTrue();
            cache.Has("a/giden.img").Should().BeFalse();
        }
        finally
        {
            // Dispose'daki Directory.Delete salt-okunur dosyada patlar; bayrağı
            // testin içinde geri al, yoksa temizlik koşumu düşürür.
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Prune_is_a_no_op_when_the_cache_folder_was_never_created()
    {
        var cache = new CatalogPhotoCache(_root);

        // İlk açılışta senkron turu Save'den önce Prune çağırabilir.
        var act = () => cache.Prune(["a/kalan.img"]);

        act.Should().NotThrow();
    }
}
