using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// Token demetinin geriye uyum sözleşmesi ve deponun atomik yazımı.
///
/// NEDEN: demet, ek Sayfaları taşıyan isteğe bağlı bir liste kazandı; sürüm alanı
/// ve göç adımı yok. Güncellemede bağlantının düşmemesi iki yöne bağlı: sahadaki
/// sürümün yazdığı blob yeni kayda aynen okunmalı, yeni blob'u da sahadaki sürüm
/// okuyabilmeli (eski sürüme dönüş). İkisi de burada gerçek depo ve DPAPI ile
/// kilitlenir. Her test kendi geçici klasöründe koşar.
/// </summary>
public sealed class FacebookTokenBundleCompatTests : IDisposable
{
    /// <summary>
    /// 0.9.12'nin demeti ve serileştirici seçenekleri. DONDURULMUŞTUR: üretim tipine
    /// bağlanmaz; üretim kaydı değişse de "sahadaki eski sürüm" bu şekliyle kalır.
    /// </summary>
    private sealed record LegacyBundle(
        string UserAccessToken,
        string PageId,
        string PageAccessToken,
        string PageName,
        DateTimeOffset? ExpiresAt);

    private static readonly JsonSerializerOptions LegacyOpts = new() { WriteIndented = false };

    private readonly string _folder;
    private readonly EncryptedFacebookTokenStore _store;

    public FacebookTokenBundleCompatTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"orderdeck-fb-compat-{Guid.NewGuid():N}");
        _store = new EncryptedFacebookTokenStore(_folder);
    }

    public void Dispose()
    {
        // Alt klasörleriyle: bir test geçici dosyanın yoluna klasör koyuyor.
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch { /* best effort */ }
    }

    private string BlobPath => Path.Combine(_folder, "facebook-token.bin");

    private IEnumerable<string?> FileNames() => Directory.GetFiles(_folder).Select(Path.GetFileName);

    /// <summary>Depo PUBLIC: değerler literal yazılmaz, her çağrıda üretilir.</summary>
    private static string NewValue(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static LegacyBundle NewLegacy() => new(
        NewValue("kullanici"),
        NewValue("sayfa"),
        NewValue("erisim"),
        NewValue("ad"),
        DateTimeOffset.UtcNow.AddDays(60));

    /// <summary>Aynı beş alan + üretilmiş ek Sayfalar; 0'da liste hiç kurulmaz (null).</summary>
    private static FacebookTokenBundle WithExtras(LegacyBundle primary, int extraPages) => new(
        primary.UserAccessToken,
        primary.PageId,
        primary.PageAccessToken,
        primary.PageName,
        primary.ExpiresAt,
        extraPages == 0
            ? null
            : Enumerable.Range(0, extraPages)
                .Select(_ => new FacebookLinkedPage(NewValue("ek-sayfa"), NewValue("ek-erisim"), NewValue("ek-ad")))
                .ToList());

    private static byte[] Protect(string json) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(json), optionalEntropy: null,
            DataProtectionScope.CurrentUser);

    private static string Unprotect(byte[] cipher) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, optionalEntropy: null,
            DataProtectionScope.CurrentUser));

    /// <summary>
    /// Beş alan tek tek (demet liste taşıdığı için kayıt eşitliğiyle karşılaştırılmaz).
    /// JSON turu anı korur ama gösterimi değiştirebilir: süre unix saniyesiyle karşılaştırılır.
    /// </summary>
    private static void AssertPrimary(FacebookTokenBundle actual, LegacyBundle expected)
    {
        actual.UserAccessToken.Should().Be(expected.UserAccessToken);
        actual.PageId.Should().Be(expected.PageId);
        actual.PageAccessToken.Should().Be(expected.PageAccessToken);
        actual.PageName.Should().Be(expected.PageName);
        (actual.ExpiresAt?.ToUnixTimeSeconds()).Should().Be(expected.ExpiresAt?.ToUnixTimeSeconds());
    }

    [Fact]
    public void Eski_bicimli_JSON_yeni_kayda_okunur()
    {
        var old = NewLegacy();
        var json = JsonSerializer.Serialize(old, LegacyOpts);

        var bundle = JsonSerializer.Deserialize<FacebookTokenBundle>(json, LegacyOpts)!;

        json.Should().NotContain("ExtraPages");
        AssertPrimary(bundle, old);
        bundle.ExtraPages.Should().BeNull();
    }

    /// <summary>
    /// Eski sürüme dönüş sözleşmesi. 0: tek Sayfalı demet de anahtarı null değerle
    /// yazar; eski sürüm onu da atlar.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Yeni_bicimli_JSON_eski_surumun_bes_alanli_kaydina_okunur(int extraPages)
    {
        var primary = NewLegacy();
        await _store.SaveAsync(WithExtras(primary, extraPages));
        var json = Unprotect(File.ReadAllBytes(BlobPath));   // gerçek SaveAsync çıktısı

        var old = JsonSerializer.Deserialize<LegacyBundle>(json, LegacyOpts)!;   // AYNI seçeneklerle

        json.Should().Contain("ExtraPages");
        (old with { ExpiresAt = null }).Should().Be(primary with { ExpiresAt = null });   // dört dizge alanı
        old.ExpiresAt!.Value.ToUnixTimeSeconds().Should().Be(primary.ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task DPAPI_ile_yazilmis_eski_blob_saglam_okunur_silinmez_baytlari_degismez()
    {
        var old = NewLegacy();
        var bytes = Protect(JsonSerializer.Serialize(old, LegacyOpts));
        File.WriteAllBytes(BlobPath, bytes);

        var bundle = (await _store.LoadAsync())!;

        AssertPrimary(bundle, old);
        bundle.ExtraPages.Should().BeNull();
        File.ReadAllBytes(BlobPath).Should().Equal(bytes);
    }

    [Fact]
    public async Task Ekli_demet_kaydet_yukle_turunda_sira_ve_icerik_korunur()
    {
        var primary = NewLegacy();
        var saved = WithExtras(primary, 3);
        await _store.SaveAsync(saved);

        var loaded = (await _store.LoadAsync())!;

        AssertPrimary(loaded, primary);
        loaded.ExtraPages!.Select(p => (p.PageId, p.PageAccessToken, p.PageName))
            .Should().Equal(saved.ExtraPages!.Select(p => (p.PageId, p.PageAccessToken, p.PageName)));
    }

    [Fact]
    public void ToString_token_basmaz()
    {
        var bundle = WithExtras(NewLegacy(), 1);
        var extra = bundle.ExtraPages![0];

        extra.ToString().Should().Contain(extra.PageId).And.Contain(extra.PageName)
            .And.NotContain(extra.PageAccessToken);
        bundle.ToString().Should().Contain(bundle.PageId).And.Contain(bundle.PageName)
            .And.NotContain(bundle.UserAccessToken).And.NotContain(bundle.PageAccessToken)
            .And.NotContain(extra.PageAccessToken);
    }

    [Fact]
    public async Task Atomik_yazim_gecici_dosya_birakmaz_ve_eskisinin_yerini_alir()
    {
        await _store.SaveAsync(WithExtras(NewLegacy(), 0));
        var next = NewLegacy();

        await _store.SaveAsync(WithExtras(next, 1));

        FileNames().Should().Equal("facebook-token.bin");
        AssertPrimary((await _store.LoadAsync())!, next);
    }

    [Fact]
    public async Task Yazim_gecici_dosyadan_gecer_gecici_yol_yazilamazsa_eski_dosya_ayni_kalir()
    {
        await _store.SaveAsync(WithExtras(NewLegacy(), 0));
        var before = File.ReadAllBytes(BlobPath);
        // Geçici dosyanın yolunu bir klasör tutar: geçiciden geçen yazım burada düşer;
        // hedefe doğrudan yazan kod ise fark etmeden geçerdi.
        Directory.CreateDirectory(BlobPath + ".tmp");

        await FluentActions.Awaiting(() => _store.SaveAsync(WithExtras(NewLegacy(), 1)))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        File.ReadAllBytes(BlobPath).Should().Equal(before);
    }

    [Fact]
    public async Task Yazim_duserse_istisna_cikar_eski_dosya_ayni_kalir_gecici_dosya_kalmaz()
    {
        await _store.SaveAsync(WithExtras(NewLegacy(), 0));
        var before = File.ReadAllBytes(BlobPath);
        // Windows salt okunur hedefin üstüne taşımayı reddeder.
        File.SetAttributes(BlobPath, FileAttributes.ReadOnly);
        try
        {
            await FluentActions.Awaiting(() => _store.SaveAsync(WithExtras(NewLegacy(), 1)))
                .Should().ThrowAsync<UnauthorizedAccessException>();

            File.ReadAllBytes(BlobPath).Should().Equal(before);
            FileNames().Should().Equal("facebook-token.bin");
        }
        finally
        {
            // Yoksa Dispose klasörü silemez.
            File.SetAttributes(BlobPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task ClearAsync_gecici_dosya_artigini_da_siler()
    {
        await _store.SaveAsync(WithExtras(NewLegacy(), 0));
        // Yarım kalmış bir yazımın artığı: süreç geçici dosyayı yazdı, yerine taşıyamadan
        // öldü. Sahada o dosyada şifreli demet durur; bağlantı kaldırılınca o da gitmeli.
        File.WriteAllBytes(BlobPath + ".tmp", Guid.NewGuid().ToByteArray());

        await _store.ClearAsync();

        FileNames().Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAsync_gecici_dosya_silinemezse_de_ana_dosyayi_siler_istisna_cikarmaz()
    {
        await _store.SaveAsync(WithExtras(NewLegacy(), 0));
        // Geçici dosyanın yolunu bir klasör tutar: File.Delete klasörü silemez, istisna verir.
        // Artık silinemedi diye bağlantı kaldırılamaz kalmamalı: ana dosya yine gider.
        Directory.CreateDirectory(BlobPath + ".tmp");

        await FluentActions.Awaiting(() => _store.ClearAsync()).Should().NotThrowAsync();

        File.Exists(BlobPath).Should().BeFalse();
    }
}
