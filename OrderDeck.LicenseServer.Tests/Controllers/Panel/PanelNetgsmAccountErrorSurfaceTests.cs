using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Controllers.Panel;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panel PUT yolunun HATA YÜZEYİ: yayıncıya ne yazıyoruz ve hangi arıza hangi
/// durum koduna düşüyor (Görev 14).
///
/// <para>Kardeş dosya <c>PanelNetgsmAccountSaveTests</c> kaydetmenin
/// DAVRANIŞINI ölçüyor (satır açıldı mı, dış çağrı yapıldı mı). Burada ölçülen
/// şey yayıncının EKRANDA okuduğu şey: yanlış metin, yanlış işi yaptırır.</para>
/// </summary>
public sealed class PanelNetgsmAccountErrorSurfaceTests : IDisposable
{
    private readonly List<NetgsmApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    /// <summary>Kardeş dosyadaki <c>StubIysClient</c>'in aynısı: doğrulama
    /// çağrısını yönlendirilebilir kılar.</summary>
    private sealed class StubIysClient : IIysClient
    {
        public Func<IysSearchResult> OnSearch { get; set; } =
            () => new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(OnSearch());
    }

    private sealed class NetgsmApiFactory : ApiFactory
    {
        public StubIysClient Iys { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IIysClient>();
                s.AddSingleton<IIysClient>(Iys);
            });
        }
    }

    private async Task<(NetgsmApiFactory Factory, HttpClient Client)> SeedAsync()
    {
        var factory = new NetgsmApiFactory();
        _factories.Add(factory);

        var (client, customerId, _) =
            await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-PNE-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return (factory, client);
    }

    private static object NewBody() => new
    {
        userCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
        password = $"pw-{Guid.NewGuid():N}",
        header = "ORDERDECK",
        brandCode = Random.Shared.Next(100_000, 999_999).ToString(),
    };

    [Fact]
    public async Task Gecici_ariza_panelde_kendi_mesajini_dondurur()
    {
        // Doğrulayıcı yalnız OLGUYU söylüyor ("İYS'ye ulaşılamadı, kurulumunuz
        // doğrulanamadı"); "bundan sonra ne olacak" cümlesi ÇAĞIRANA ait.
        // Günlük işte doğru cevap "kapatılmadı, kendiliğinden tekrar denenecek";
        // panelde ikisi de yalan olurdu — `UpsertAsync` doğrulamadan ÖNCE
        // `Failed` yazıyor (fail-closed, bilinçli) ve günlük iş yalnız
        // `Verified` satırları tarıyor. Yayıncı "bekle, düzelir" diye okuyup
        // beklerse tek çıkış yolunu — formu tekrar kaydetmeyi — hiç denemez.
        var (factory, client) = await SeedAsync();
        factory.Iys.OnSearch = () => throw new HttpRequestException("bağlantı yok");

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var lastError = doc.RootElement.GetProperty("lastError").GetString();

        lastError.Should().NotContain("kendiliğinden tekrar denenecek",
            "panel yolunda kendiliğinden tekrar deneyecek HİÇBİR iş yok");
        lastError.Should().Contain("tekrar kaydedin",
            "yayıncının yapması gereken tek şey bu; yazmazsak süresiz bekler");
    }

    [Fact]
    public async Task Beklenmeyen_yanit_kodu_panelde_KORUNUR()
    {
        // Doğrulayıcı İKİ ayrı `Unavailable` metni üretiyor ve yalnız biri
        // panelde yanlış. Bu ikincisi: İYS cevap VERDİ ama kodu tanımıyoruz.
        // Panel dalı sonucun metnini kökten değiştirirse — ilk uygulamada
        // olduğu gibi — sanitize edilmiş kod da, "Netgsm'e danışın" talimatı da
        // silinir ve yerine "birkaç dakika bekleyip tekrar kaydedin" yazılır:
        // bozuk bir yanıt biçimi için YANLIŞ tavsiye, üstelik destek ekibinin
        // soracağı tek somut bilgi kaybolmuş olur. Bu yüzden panel kendi
        // cümlesini EKLER, doğrulayıcınınkini ezmez.
        var (factory, client) = await SeedAsync();
        factory.Iys.OnSearch = () => new IysSearchResult(
            "42", "{\"code\":\"42\"}", new Dictionary<string, IysConsentStatus>());

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var lastError = doc.RootElement.GetProperty("lastError").GetString();

        lastError.Should().Contain("beklenmeyen yanıt kodu")
            .And.Contain("42", "destek ekibinin isteyeceği tek somut bilgi bu kod")
            .And.Contain("Netgsm'e danışın", "bu arızada doğru adım gerçekten bu");
        lastError.Should().Contain("tekrar kaydedin",
            "panel kendi kurtarma adımını EKLER — doğrulayıcının teşhisini "
            + "silerek değil");
    }

    /// <summary>
    /// Gerçek bir <c>SqlException</c> üretir. Genel ctor'u yok — SQL Server'ın
    /// kendisi dışında kimse kuramasın diye kapalı. Yansımayla kuruyoruz çünkü
    /// alternatifin ikisi de kötü: ya <c>IsUniqueIndexConflict</c>'in ASIL
    /// mantığı (2601/2627 + indeks adı) hiç sınanmaz, ya da üretim kodu yalnız
    /// test uğruna ikinci bir imzaya bölünür.
    ///
    /// <para><b>Neden InMemory'den geçemiyoruz.</b> InMemory sağlayıcı tekil
    /// indeksleri UYGULAMIYOR; aynı lisansa iki satır sessizce yazılır ve
    /// <c>DbUpdateException</c> hiç doğmaz. Yani bu dal HTTP üzerinden
    /// kanıtlanamaz, saf fonksiyon olarak kanıtlanır.</para>
    /// </summary>
    private static SqlException NewSqlException(int number, string message)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var asm = typeof(SqlException).Assembly;

        var errorType = asm.GetType("Microsoft.Data.SqlClient.SqlError")!;
        var error = errorType.GetConstructor(flags, new[]
        {
            typeof(int), typeof(byte), typeof(byte), typeof(string),
            typeof(string), typeof(string), typeof(int), typeof(Exception),
        })!.Invoke(new object?[] { number, (byte)0, (byte)0, "test", message, "", 0, null });

        var collectionType = asm.GetType("Microsoft.Data.SqlClient.SqlErrorCollection")!;
        var collection = collectionType.GetConstructor(flags, Type.EmptyTypes)!.Invoke(null);
        collectionType.GetMethod("Add", flags)!.Invoke(collection, new[] { error });

        return (SqlException)typeof(SqlException).GetMethod(
                "CreateException",
                BindingFlags.Static | BindingFlags.NonPublic,
                new[] { collectionType, typeof(string) })!
            .Invoke(null, new[] { collection, "11.0.0" })!;
    }

    [Fact]
    public void Lisans_tekil_indeks_ihlali_de_catisma_sayilir()
    {
        // Aynı lisans için iki sekmeden eşzamanlı İLK kayıt: kaybeden istek
        // marka indeksinden değil `IX_NetgsmAccounts_LicenseId`'den 2601 alır.
        // Ada göre filtre yalnız "BrandCode" arıyorsa istisna dışarı kaçar ve
        // yayıncı 500 görür.
        PanelNetgsmAccountController.IsUniqueIndexConflict(
            new DbUpdateException("kayıt düştü", NewSqlException(
                2601, "Cannot insert duplicate key row in object 'dbo.NetgsmAccounts' "
                      + "with unique index 'IX_NetgsmAccounts_LicenseId'.")),
            "LicenseId").Should().BeTrue();

        // 2627 = aynı ihlalin CONSTRAINT biçimi; ikisi de tekillik ihlalidir.
        PanelNetgsmAccountController.IsUniqueIndexConflict(
            new DbUpdateException("kayıt düştü", NewSqlException(
                2627, "Violation of UNIQUE KEY constraint 'IX_NetgsmAccounts_LicenseId'.")),
            "LicenseId").Should().BeTrue();

        // Numara filtresi olmasaydı İLGİSİZ her DB arızası — yabancı anahtar
        // ihlali, kopan bağlantı — "kurulumunuz başka sekmede kaydedildi" diye
        // gösterilirdi. 547 = FK ihlali; mesajda indeks adı geçse bile HAYIR.
        PanelNetgsmAccountController.IsUniqueIndexConflict(
            new DbUpdateException("kayıt düştü", NewSqlException(
                547, "The INSERT statement conflicted with the FOREIGN KEY constraint "
                     + "on IX_NetgsmAccounts_LicenseId.")),
            "LicenseId").Should().BeFalse();

        // Ad filtresi iki 409 yolunu birbirinden ayırıyor: marka çakışması
        // yayıncıya "bu kod başkasında" dedirtir, lisans çakışması "sayfayı
        // yenileyin". Karışırlarsa yayıncı yanlış sorunu kovalar.
        PanelNetgsmAccountController.IsUniqueIndexConflict(
            new DbUpdateException("kayıt düştü", NewSqlException(
                2601, "Cannot insert duplicate key row in object 'dbo.NetgsmAccounts' "
                      + "with unique index 'IX_NetgsmAccounts_BrandCode'.")),
            "LicenseId").Should().BeFalse();

        // İç istisna yoksa ya da SQL Server'dan gelmiyorsa hakkında hiçbir şey
        // bilmiyoruz demektir: tekillik çakışması SAYILMAZ, 500'e gitsin.
        PanelNetgsmAccountController.IsUniqueIndexConflict(
            new DbUpdateException("kayıt düştü"), "LicenseId").Should().BeFalse();

        PanelNetgsmAccountController.IsUniqueIndexConflict(
            new DbUpdateException(
                "kayıt düştü", new InvalidOperationException("IX_NetgsmAccounts_LicenseId")),
            "LicenseId").Should().BeFalse();
    }
}
