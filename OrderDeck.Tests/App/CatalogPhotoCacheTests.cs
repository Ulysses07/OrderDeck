using System;
using System.IO;
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
    public void ResolveAbsolute_returns_null_for_unknown_or_empty_keys()
    {
        var cache = new CatalogPhotoCache(_root);

        // Anahtar yoksa/boşsa kart placeholder'a düşmeli, patlamamalı.
        cache.ResolveAbsolute(null).Should().BeNull();
        cache.ResolveAbsolute("").Should().BeNull();
        cache.ResolveAbsolute("hic/olmayan.img").Should().BeNull();
    }

    [Fact]
    public void Save_overwrites_previous_bytes_for_the_same_key()
    {
        var cache = new CatalogPhotoCache(_root);
        const string key = "abc/products/def/kapak.img";

        cache.Save(key, [1, 2, 3, 4]);
        cache.Save(key, [9]);

        // Kısalan içerik eskisinin kuyruğunu bırakmamalı — yerine koyma
        // atomik olduğu için dosya ya tamamen eski ya tamamen yeni.
        File.ReadAllBytes(cache.ResolveAbsolute(key)!).Should().Equal(9);
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
    public void Prune_is_a_no_op_when_the_cache_folder_was_never_created()
    {
        var cache = new CatalogPhotoCache(_root);

        // İlk açılışta senkron turu Save'den önce Prune çağırabilir.
        var act = () => cache.Prune(["a/kalan.img"]);

        act.Should().NotThrow();
    }
}
