using System;
using System.IO;
using FluentAssertions;
using OrderDeck.App.Services;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// ProductPhotoStore'un dosya sözleşmesi. WPF'e dokunmuyor (Application
/// singleton'ı gerekmez) — ThemeTestHost'a ihtiyaç YOK, düz sınıf testi.
/// </summary>
public class ProductPhotoStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "od-photos-" + Guid.NewGuid().ToString("N"));

    private string MakeSourceFile(string name)
    {
        var dir = Path.Combine(_root, "src");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public void Save_copies_file_and_returns_relative_name()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));
        var src = MakeSourceFile("foto.jpg");

        var stored = store.Save("A100", src);

        stored.Should().Be("a100.jpg");
        File.Exists(Path.Combine(_root, "products", "a100.jpg")).Should().BeTrue();
        // Kaynak dosyaya dokunulmaz — kullanıcının indirilenler klasörü bizim
        // değil.
        File.Exists(src).Should().BeTrue();
    }

    [Fact]
    public void Save_replaces_previous_photo_even_with_different_extension()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));
        store.Save("A100", MakeSourceFile("ilk.jpg"));

        var stored = store.Save("A100", MakeSourceFile("ikinci.png"));

        stored.Should().Be("a100.png");
        // Eski uzantı bırakılırsa klasör her düzenlemede şişer ve
        // ResolveAbsolute hangisini seçeceğini bilemez.
        File.Exists(Path.Combine(_root, "products", "a100.jpg")).Should().BeFalse();
    }

    [Fact]
    public void ResolveAbsolute_returns_null_when_file_is_missing()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));

        // Kayıt var ama dosya elle silinmiş — kart placeholder'a düşmeli,
        // patlamamalı.
        store.ResolveAbsolute("yok.jpg").Should().BeNull();
    }

    [Fact]
    public void ResolveAbsolute_returns_null_for_null_or_blank()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));

        store.ResolveAbsolute(null).Should().BeNull();
        store.ResolveAbsolute("   ").Should().BeNull();
    }

    [Fact]
    public void ResolveAbsolute_rejects_paths_that_escape_the_root()
    {
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));

        // Veritabanı satırı bozulsa bile kök dışına çıkılmamalı.
        store.ResolveAbsolute(@"..\..\windows\win.ini").Should().BeNull();
        store.ResolveAbsolute(@"C:\windows\win.ini").Should().BeNull();
    }

    [Fact]
    public void ResolveAbsolute_rejects_sibling_directory_with_matching_prefix()
    {
        // Güvenlik: kök "products" ise "products-eski\x.jpg" kontrolü
        // geçmemeli. Sıkı ayraç kontrolü olmadan StartsWith yanıltıcı olur.
        var store = new ProductPhotoStore(Path.Combine(_root, "products"));

        store.ResolveAbsolute(@"..\products-eski\x.jpg").Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
