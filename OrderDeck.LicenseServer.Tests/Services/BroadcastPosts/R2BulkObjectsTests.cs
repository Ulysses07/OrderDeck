using System.Net;
using System.Reflection;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Tests.Services.BroadcastPosts;

/// <summary>
/// Yayıncı silmesinin (KVKK) R2 yarısı: önek listeleme sayfa sayfa, toplu silme 1000'lik parçalarla; silinemeyen anahtar hata
/// metniyle döner, kaybolmaz. Gerçek R2'ye çıkılmaz: <see cref="IAmazonS3"/>'ün yalnız kullanılan üç çağrısını taklit eden bir
/// vekil (<see cref="DispatchProxy"/>) bellekteki kovayı yönetir.
/// </summary>
public sealed class R2BulkObjectsTests
{
    private const string Bucket = "kova";

    /// <summary>Bellekteki kova. Sayfa boyu küçük tutulur ki sayfalama gerçekten sınansın.</summary>
    public class FakeS3 : DispatchProxy
    {
        public SortedSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public int PageSize { get; set; } = 2;
        public List<int> DeleteBatchSizes { get; } = new();
        public HashSet<string> RefusedKeys { get; } = new(StringComparer.Ordinal);
        public Exception? BatchFailure { get; set; }
        public int SingleDeletes { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IAmazonS3.ListObjectsV2Async) => Task.FromResult(List((ListObjectsV2Request)args![0]!)),
            nameof(IAmazonS3.DeleteObjectsAsync) when args![0] is DeleteObjectsRequest r => DeleteMany(r),
            nameof(IAmazonS3.DeleteObjectAsync) when args![0] is DeleteObjectRequest r => DeleteOne(r),
            _ => throw new NotSupportedException(method.Name),
        };

        private ListObjectsV2Response List(ListObjectsV2Request r)
        {
            var after = Keys.Where(k => k.StartsWith(r.Prefix, StringComparison.Ordinal)
                && (r.ContinuationToken is null || string.CompareOrdinal(k, r.ContinuationToken) > 0)).ToList();
            var page = after.Take(PageSize).ToList();
            var truncated = after.Count > page.Count;
            return new ListObjectsV2Response
            {
                S3Objects = page.Count == 0 ? null : page.Select(k => new S3Object { Key = k }).ToList(),
                IsTruncated = truncated,
                NextContinuationToken = truncated ? page[^1] : null,
            };
        }

        private Task<DeleteObjectsResponse> DeleteMany(DeleteObjectsRequest r)
        {
            DeleteBatchSizes.Add(r.Objects.Count);
            if (BatchFailure is not null) return Task.FromException<DeleteObjectsResponse>(BatchFailure);
            var errors = new List<DeleteError>();
            foreach (var o in r.Objects)
            {
                if (RefusedKeys.Contains(o.Key))
                    errors.Add(new DeleteError { Key = o.Key, Code = "AccessDenied", Message = "nesne kilitli" });
                else
                    Keys.Remove(o.Key);
            }
            var resp = new DeleteObjectsResponse { DeleteErrors = errors.Count == 0 ? null : errors };
            // SDK, yanıtta hata varsa DeleteObjectsException fırlatır; gerçek davranış bu.
            return errors.Count == 0
                ? Task.FromResult(resp)
                : Task.FromException<DeleteObjectsResponse>(new DeleteObjectsException(resp));
        }

        private Task<DeleteObjectResponse> DeleteOne(DeleteObjectRequest r)
        {
            SingleDeletes++;
            if (RefusedKeys.Contains(r.Key))
                return Task.FromException<DeleteObjectResponse>(new AmazonS3Exception("nesne kilitli"));
            Keys.Remove(r.Key);
            return Task.FromResult(new DeleteObjectResponse());
        }
    }

    private static (IAmazonS3 S3, FakeS3 Fake) NewS3()
    {
        var s3 = DispatchProxy.Create<IAmazonS3, FakeS3>();
        return (s3, (FakeS3)(object)s3);
    }

    private static List<string> Seed(FakeS3 fake, string prefix, int count)
    {
        var keys = Enumerable.Range(0, count).Select(i => $"{prefix}{i:D5}.pdf").ToList();
        foreach (var k in keys) fake.Keys.Add(k);
        return keys;
    }

    [Fact]
    public async Task Onek_altindaki_tum_anahtarlari_sayfa_sayfa_listeler_komsu_oneke_tasmaz()
    {
        var (s3, fake) = NewS3();
        var mine = Seed(fake, "payments/aaaa/", 5);
        Seed(fake, "payments/aaaab/", 2);
        Seed(fake, "wa/aaaa/", 1);

        var listed = await R2BulkObjects.ListKeysAsync(s3, Bucket, "payments/aaaa/", default);

        listed.Should().BeEquivalentTo(mine, "beş anahtar iki sayfalık boyu aşar: devam jetonu izlenmeli");
    }

    [Fact]
    public async Task Bin_anahtari_asan_silme_parcalanir_reddedilen_anahtar_hatasiyla_doner()
    {
        var (s3, fake) = NewS3();
        var keys = Seed(fake, "wa/bbbb/", 1001);
        fake.RefusedKeys.Add(keys[7]);

        var failures = await R2BulkObjects.DeleteKeysAsync(s3, Bucket, keys, default);

        fake.DeleteBatchSizes.Should().Equal(1000, 1);
        failures.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Key = keys[7] });
        failures[0].Error.Should().Contain("AccessDenied");
        fake.Keys.Should().Equal(keys[7]);
    }

    [Fact]
    public async Task Toplu_silme_desteklenmiyorsa_tek_tek_siler()
    {
        // R2, AWS SDK'nın yeni varsayılan CRC32 sağlama başlığını bir dönem DeleteObjects'te 501 NotImplemented ile reddetti.
        // Tek nesne silme (DeleteObject) üretimde zaten kullanılıyor: toplu yol desteklenmezse ona düşülür, sonraki parçalar da.
        var (s3, fake) = NewS3();
        var keys = Seed(fake, "payments/cccc/", 1001);
        fake.RefusedKeys.Add(keys[1000]);
        fake.BatchFailure = new AmazonS3Exception("Header 'x-amz-sdk-checksum-algorithm' not implemented", ErrorType.Sender,
            "NotImplemented", "istek-1", HttpStatusCode.NotImplemented);

        var failures = await R2BulkObjects.DeleteKeysAsync(s3, Bucket, keys, default);

        fake.DeleteBatchSizes.Should().Equal(new[] { 1000 }, "desteklenmediği bir kez görüldükten sonra toplu istek yinelenmez");
        fake.SingleDeletes.Should().Be(1001);
        failures.Should().ContainSingle().Which.Key.Should().Be(keys[1000]);
        fake.Keys.Should().Equal(keys[1000]);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Toplu_istek_duserse_parcadaki_her_anahtar_hata_olarak_doner(HttpStatusCode status)
    {
        // Kesinti ya da hız sınırı: her anahtarı tek tek denemek kesintiyi bin kez beklemek olurdu. Parça silinemedi sayılır,
        // anahtarların hepsi döner (çağıran sonraki turda yeniden dener).
        var (s3, fake) = NewS3();
        var keys = Seed(fake, "payments/dddd/", 3);
        fake.BatchFailure = new AmazonS3Exception("hizmet kullanılamıyor", ErrorType.Receiver, "ServiceUnavailable", "istek-2",
            status);

        var failures = await R2BulkObjects.DeleteKeysAsync(s3, Bucket, keys, default);

        failures.Select(f => f.Key).Should().BeEquivalentTo(keys);
        failures.Should().OnlyContain(f => f.Error.Contains("hizmet kullanılamıyor"));
        fake.SingleDeletes.Should().Be(0);
        fake.Keys.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "InvalidRequest")]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch")]
    public async Task Toplu_istek_501_disinda_bir_istemci_hatasiyla_reddedilirse_tek_tek_siler(HttpStatusCode status, string code)
    {
        // R2 toplu silmeyi 501 dışında bir istemci hatasıyla da reddedebilir (sağlama ya da imza başlığı; bu SDK sürümüyle canlıda
        // denenmedi). Ret isteğin biçimine: her yeniden deneme aynı nedenle düşer, bu yoldaki nesneler hiç silinmezdi. Tek nesne
        // silme üretimde müşteri silmesinin yolu; ona düşülür.
        var (s3, fake) = NewS3();
        var keys = Seed(fake, "wa/eeee/", 3);
        fake.RefusedKeys.Add(keys[2]);
        fake.BatchFailure = new AmazonS3Exception("istek reddedildi", ErrorType.Sender, code, "istek-3", status);

        var failures = await R2BulkObjects.DeleteKeysAsync(s3, Bucket, keys, default);

        fake.SingleDeletes.Should().Be(3);
        failures.Should().ContainSingle().Which.Key.Should().Be(keys[2]);
        fake.Keys.Should().Equal(keys[2]);
    }
}
