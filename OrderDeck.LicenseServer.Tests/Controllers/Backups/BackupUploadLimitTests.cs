using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Backups;

/// <summary>
/// Yükleme tavanı iki ayrı sebeple var ve ikisi de ölçülüyor:
///
/// <list type="number">
/// <item><b>Tavan gerçekten uygulanıyor mu.</b> Önceki hâlde
/// <c>MaxBlobSizeMb</c> hiçbir şeyi bağlamıyordu: Kestrel'in kendi varsayılanı
/// (30.000.000 bayt) daha önce devreye giriyordu, dolayısıyla ayarı büyütmek
/// de küçültmek de sonucu değiştirmiyordu. Aşağıdaki fixture tavanı 1 MB'a
/// çekiyor; ayar bağlayıcı değilse sınır testi yeşil kalamaz.</item>
///
/// <item><b>Reddetme gövdeyi saklamadan önce mi oluyor.</b> Yalnız durum koduna
/// bakmak yetmez — 413 dönüp blob'u yine de diske yazan bir kod da o testi
/// geçerdi. Bu yüzden her reddetme, depolama kökünde <b>hiç dosya kalmadığı</b>
/// ile birlikte doğrulanıyor.</item>
/// </list>
/// </summary>
public sealed class TightSizeApiFactory : ApiFactory
{
    protected override IDictionary<string, string?> ExtraConfig =>
        new Dictionary<string, string?>
        {
            ["Backup:MaxBlobSizeMb"] = "1",
            // Sıra kapısı testleri beklemesin: dolu olduğunda anında 503.
            ["Backup:UploadQueueWaitSeconds"] = "0",
            ["Backup:MaxConcurrentUploads"] = "1"
        };
}

public class BackupUploadLimitTests : IClassFixture<TightSizeApiFactory>
{
    private readonly TightSizeApiFactory _factory;
    public BackupUploadLimitTests(TightSizeApiFactory factory) => _factory = factory;

    private static byte[] Payload(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static IReadOnlyList<string> BlobFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).ToList()
            : Array.Empty<string>();

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Remove("X-Backup-Sha256");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha(payload));
        return await client.PostAsync("/api/v1/me/backups", content);
    }

    [Fact]
    public async Task Tavani_asan_yukleme_413_donuyor_ve_diske_yazilmiyor()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var before = BlobFiles(_factory.BackupRoot).Count;

        var resp = await UploadAsync(client, Payload(2 * 1024 * 1024));

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body!["error"].Should().Contain("1 MB",
            "mesaj yapılandırılan tavanı söylemeli — Kestrel'in varsayılanını değil");

        BlobFiles(_factory.BackupRoot).Count.Should().Be(before,
            "reddedilen gövde depolamaya hiç ulaşmamalı");
    }

    /// <summary>
    /// Sınırın tam üstü kabul edilmeli. Yalnız "büyük olan reddediliyor mu"
    /// testi olsaydı, tavanı yanlışlıkla çok aşağı çeken bir değişiklik
    /// (ya da bayt/MB çevriminde bir hata) fark edilmeden geçerdi.
    /// </summary>
    [Fact]
    public async Task Tam_sinirdaki_yukleme_kabul_ediliyor()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var resp = await UploadAsync(client, Payload(1024 * 1024));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>
    /// <b>Bu PR'ın asıl meselesi.</b> Content-Length istemcinin yazdığı bir sayı;
    /// gövde okunurken o sayı kadar yer ayrılıyor. Tavan kontrolü okumadan ÖNCE
    /// yapılmazsa, tek satırlık bir başlık sunucuya istediği büyüklükte tahsis
    /// yaptırabilir — gövdeyi göndermeye bile gerek kalmadan.
    ///
    /// <para>Ölçüm buna göre kurulu: başlıkta 100 MB yazıyor (tavan 1 MB),
    /// telde 64 bayt var. Kontrol yerindeyse yanıt <b>413</b> ve gövde hiç
    /// okunmuyor. Kontrol kalkarsa akış farklı: önce 100 MB ayrılıyor, sonra
    /// gövde beyandan kısa geldiği için <b>400</b> dönüyor — yani durum kodu
    /// değişiyor ve test kırmızıya düşüyor.</para>
    /// </summary>
    [Fact]
    public async Task Beyan_edilen_uzunluk_tahsise_donusmuyor()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var payload = Payload(64);

        var content = new LyingLengthContent(100L * 1024 * 1024, payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Remove("X-Backup-Sha256");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha(payload));

        var resp = await client.PostAsync("/api/v1/me/backups", content);

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge,
            "beyan edilen boyut tavanı aşıyorsa gövde hiç okunmamalı");
    }

    /// <summary>
    /// Content-Length göndermeyen (chunked) istemci, boyut kontrolünü atlatmanın
    /// en bariz yolu: beyan yoksa beyana bakan kontrol de çalışmaz. Bu yolda
    /// tavanı uygulayan tek şey gövde okunduktan sonraki ölçüm — ve o ölçüm
    /// olmasaydı chunked göndermek sınırı tamamen kaldırırdı.
    /// </summary>
    [Fact]
    public async Task Uzunluk_bildirmeyen_asiri_govde_de_reddediliyor()
    {
        var (_, _, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        // Varsayılan test istemcisi yönlendirmeleri izleyebilmek için gövdeyi
        // tamponluyor ve bunu yaparken Content-Length ÜRETİYOR — yani "uzunluk
        // bildirmeyen istemci" senaryosu o istemciyle hiç kurulamıyor.
        // AllowAutoRedirect=false o katmanı devreden çıkarıyor.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var payload = Payload(2 * 1024 * 1024);
        var before = BlobFiles(_factory.BackupRoot).Count;

        var content = new UnknownLengthContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Remove("X-Backup-Sha256");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha(payload));

        var resp = await client.PostAsync("/api/v1/me/backups", content);

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        BlobFiles(_factory.BackupRoot).Count.Should().Be(before);
    }

    /// <summary>Content-Length bildirmeyen gövde (chunked aktarım).</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _actual;
        public UnknownLengthContent(byte[] actual) => _actual = actual;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_actual, 0, _actual.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Content-Length'i olduğundan büyük bildiren gövde.</summary>
    private sealed class LyingLengthContent : HttpContent
    {
        private readonly long _declared;
        private readonly byte[] _actual;

        public LyingLengthContent(long declared, byte[] actual)
        {
            _declared = declared;
            _actual = actual;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_actual, 0, _actual.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _declared;
            return true;
        }
    }

    /// <summary>
    /// Kapı doluyken gelen istek beklemek yerine 503 + Retry-After almalı.
    /// Yarış kurmak yerine tek sıra doğrudan tutuluyor: eşzamanlılığı zamanlama
    /// ile kurmaya çalışan test, kapı tamamen kaldırılsa bile yeşil kalabilirdi.
    /// </summary>
    [Fact]
    public async Task Kapi_doluyken_yukleme_503_donuyor()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var throttle = _factory.Services.GetRequiredService<BackupUploadThrottle>();

        (await throttle.TryEnterAsync(default)).Should().BeTrue("testin tek sırayı tutması gerekiyor");
        try
        {
            var before = BlobFiles(_factory.BackupRoot).Count;

            var resp = await UploadAsync(client, Payload(4096));

            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            resp.Headers.RetryAfter.Should().NotBeNull("istemci ne zaman yeniden deneyeceğini bilmeli");
            BlobFiles(_factory.BackupRoot).Count.Should().Be(before);
        }
        finally
        {
            throttle.Exit();
        }
    }

    /// <summary>Kapı bırakıldıktan sonra yükleme yine çalışmalı — 503 kalıcı
    /// bir bozulma değil, geçici sırt sırta gelme durumu.</summary>
    [Fact]
    public async Task Kapi_birakilinca_yukleme_yeniden_calisiyor()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var throttle = _factory.Services.GetRequiredService<BackupUploadThrottle>();

        (await throttle.TryEnterAsync(default)).Should().BeTrue();
        (await UploadAsync(client, Payload(4096))).StatusCode
            .Should().Be(HttpStatusCode.ServiceUnavailable);
        throttle.Exit();

        (await UploadAsync(client, Payload(4096))).StatusCode
            .Should().Be(HttpStatusCode.Created);
    }
}
