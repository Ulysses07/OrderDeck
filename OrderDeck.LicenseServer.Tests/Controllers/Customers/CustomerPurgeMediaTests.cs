using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;

namespace OrderDeck.LicenseServer.Tests.Controllers.Customers;

/// <summary>
/// Yayıncı silmesi (KVKK, <c>POST /api/v1/admin/customers/{id}/purge</c>) lisansa ait kova nesnelerini de siler (karar 5 ve 6,
/// 23.10.2026): müşterilerin o yayıncıya yüklediği dekontlar (<c>payments/{lisans}/</c>) ve WhatsApp medyası
/// (<c>wa/{lisans}/</c>); yayın paylaşımı medyası ve ürün fotoğrafları (karar 23.10.2026, "silinsin"). Satırları (Payment,
/// WaMessage, BroadcastPost, ProductPhoto) lisansla kaskad gidiyordu, nesneler kovada süresiz kalıyordu.
/// </summary>
public sealed class CustomerPurgeMediaTests : IClassFixture<CustomerPurgeMediaTests.MediaApiFactory>
{
    private readonly MediaApiFactory _factory;
    public CustomerPurgeMediaTests(MediaApiFactory factory) => _factory = factory;

    /// <summary>Dekont ve WhatsApp depoları silinemeyen anahtar ve listelenemeyen lisans taklidi yapabilen sahtelerle.</summary>
    public sealed class MediaApiFactory : ApiFactory
    {
        public FlakyPaymentStorage Payments { get; } = new();
        public FlakyWhatsAppStore WhatsApp { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IShopperPaymentStorage>();
                services.AddSingleton<IShopperPaymentStorage>(Payments);
                services.RemoveAll<IWhatsAppMediaStore>();
                services.AddSingleton<IWhatsAppMediaStore>(WhatsApp);
            });
        }
    }

    /// <summary>Gerçek stub depoyu sarar; anahtar ya da lisans bazında hata üretir (sınıftaki testler birbirini etkilemesin).</summary>
    public sealed class FlakyPaymentStorage : IShopperPaymentStorage
    {
        public StubShopperPaymentStorage Inner { get; } = new();
        public ConcurrentDictionary<string, byte> FailingKeys { get; } = new();
        public ConcurrentDictionary<Guid, byte> UnlistableLicenses { get; } = new();

        public Task<string> UploadAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default)
            => Inner.UploadAsync(objectKey, bytes, contentType, ct);

        public Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default)
            => Inner.CreateDownloadUrlAsync(objectKey, expiry, ct);

        public Task DeleteAsync(string objectKey, CancellationToken ct = default) => Inner.DeleteAsync(objectKey, ct);

        public Task<IReadOnlyList<string>> ListLicenseObjectsAsync(Guid licenseId, CancellationToken ct = default)
            => UnlistableLicenses.ContainsKey(licenseId)
                ? throw new IOException("depo listelenemiyor")
                : Inner.ListLicenseObjectsAsync(licenseId, ct);

        public async Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys,
            CancellationToken ct = default)
        {
            var failing = keys.Where(FailingKeys.ContainsKey).ToList();
            await Inner.DeleteObjectsAsync(keys.Except(failing).ToList(), ct);
            return failing.Select(k => new MediaDeleteFailure(k, "AccessDenied: dekont kilitli")).ToList();
        }
    }

    /// <summary><see cref="FlakyPaymentStorage"/>'ın WhatsApp karşılığı.</summary>
    public sealed class FlakyWhatsAppStore : IWhatsAppMediaStore
    {
        public InMemoryWhatsAppMediaStore Inner { get; } = new();
        public ConcurrentDictionary<string, byte> FailingKeys { get; } = new();

        public Task<string> SaveAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default)
            => Inner.SaveAsync(objectKey, bytes, contentType, ct);

        public Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default)
            => Inner.CreateDownloadUrlAsync(objectKey, expiry, ct);

        public Task DeleteAsync(string objectKey, CancellationToken ct = default) => Inner.DeleteAsync(objectKey, ct);

        public Task<IReadOnlyList<string>> ListLicenseObjectsAsync(Guid licenseId, CancellationToken ct = default)
            => Inner.ListLicenseObjectsAsync(licenseId, ct);

        public async Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys,
            CancellationToken ct = default)
        {
            var failing = keys.Where(FailingKeys.ContainsKey).ToList();
            await Inner.DeleteObjectsAsync(keys.Except(failing).ToList(), ct);
            return failing.Select(k => new MediaDeleteFailure(k, "AccessDenied: medya kilitli")).ToList();
        }
    }

    private static string PaymentKey(Guid license) => $"{ShopperPaymentKeys.LicensePrefix(license)}{Guid.NewGuid():N}.pdf";
    private static string WaKey(Guid license) => $"{WhatsAppMediaKeys.LicensePrefix(license)}2026/10/{Guid.NewGuid():N}.jpg";

    // Yayın kovasının iki biçimi üretimdeki yazanlarla birebir, BİLEREK sabit metin (kovada bu biçimlerde nesneler var; önek
    // sınıfı değişse bile eski nesneler bu biçimde kalır): paylaşım medyası tireli lisans kimliğiyle
    // (PanelBroadcastPostsController), ürün fotoğrafı tiresiz (PanelProductPhotoController).
    private static string PostKey(Guid license) => $"{license}/{Guid.NewGuid()}/media.bin";
    private static string PhotoKey(Guid license) => $"{license:N}/products/{Guid.NewGuid():N}/{Guid.NewGuid():N}.img";

    private static bool IsPaymentKey(string key) => key.StartsWith("payments/", StringComparison.Ordinal);

    private async Task PutAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (LicenseMediaPurge.IsWhatsAppKey(key)) await _factory.WhatsApp.SaveAsync(key, new byte[] { 1 }, "image/jpeg");
            else if (IsPaymentKey(key)) await _factory.Payments.UploadAsync(key, new byte[] { 1 }, "application/pdf");
            else _factory.BroadcastMedia.Seed(key, 1, "image/jpeg");
        }
    }

    private bool Exists(string key) => LicenseMediaPurge.IsWhatsAppKey(key) ? _factory.WhatsApp.Inner.TryGet(key, out _)
        : IsPaymentKey(key) ? _factory.Payments.Inner.Contains(key)
        : _factory.BroadcastMedia.Contains(key);

    /// <summary>Yönetici istemcisi + silinecek yayıncı ve lisansları (üretilmiş kimlik bilgileri; literal parola yok).</summary>
    private async Task<(HttpClient Admin, Guid AdminId, Guid CustomerId, string Email, Guid[] Licenses)> SeedAsync(int licenses)
    {
        var (token, adminId) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: $"pw-{Guid.NewGuid():N}");
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var email = $"purge-{Guid.NewGuid():N}@example.test";
        var created = await admin.PostAsJsonAsync("/api/v1/admin/customers",
            new { email, name = "Silinecek", initialPassword = $"p-{Guid.NewGuid():N}", autoConfirm = true });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var licenseIds = Enumerable.Range(0, licenses).Select(_ => Guid.NewGuid()).ToArray();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        foreach (var id in licenseIds)
            db.Licenses.Add(new License
            {
                Id = id, LicenseKey = $"LDK-M-{Guid.NewGuid():N}"[..18], CustomerId = customerId, SkuCode = "STD",
                ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
        await db.SaveChangesAsync();
        return (admin, adminId, customerId, email, licenseIds);
    }

    private async Task<List<DataDestructionRecord>> RecordsAsync(Guid customerId, string target)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().DataDestructionRecords.AsNoTracking()
            .Where(r => r.SubjectId == customerId && r.Target == target).ToListAsync();
    }

    private async Task<string> PurgeAuditDetailsAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EventType == AuditEvents.CustomerPurged && a.TargetId == customerId.ToString())).Details!;
    }

    [Fact]
    public async Task Purge_lisanslarin_dekont_ve_WhatsApp_dosyalarini_siler_baska_lisansinkiler_kalir()
    {
        var (admin, adminId, customerId, email, licenses) = await SeedAsync(licenses: 2);
        var (l1, l2) = (licenses[0], licenses[1]);
        var otherLicense = Guid.NewGuid();
        var mine = new[] { PaymentKey(l1), PaymentKey(l1), PaymentKey(l2), WaKey(l1), WaKey(l2) };
        var others = new[] { PaymentKey(otherLicense), WaKey(otherLicense) };
        await PutAsync(mine.Concat(others).ToArray());

        var resp = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        mine.Should().OnlyContain(k => !Exists(k), "yayıncının lisanslarındaki dekont ve WhatsApp dosyaları silinir");
        others.Should().OnlyContain(k => Exists(k), "başka lisansın nesneleri kalır");

        // KVKK Ö-13: imha kaydı lisans ve tür başına, yayıncı silmesinin tetikleyicisi ve ölçütüyle, yapan yöneticinin kimliği.
        var receipts = await RecordsAsync(customerId, ShopperPurgeService.MediaTarget);
        var whatsApp = await RecordsAsync(customerId, LicenseMediaPurge.WhatsAppMediaTarget);
        receipts.Select(r => (r.LicenseId, r.RowCount)).Should().BeEquivalentTo(new[] { ((Guid?)l1, 2), ((Guid?)l2, 1) });
        whatsApp.Select(r => (r.LicenseId, r.RowCount)).Should().BeEquivalentTo(new[] { ((Guid?)l1, 1), ((Guid?)l2, 1) });
        receipts.Concat(whatsApp).Should().OnlyContain(r => r.Action == DataDestruction.Actions.Deleted
            && r.Trigger == DataDestruction.Triggers.CustomerPurge && r.Criterion == LicenseMediaPurge.CustomerPurgeCriterion
            && r.Actor == adminId.ToString());

        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().OrphanedMediaObjects.AsNoTracking()
                .AnyAsync(o => mine.Contains(o.ObjectKey))).Should().BeFalse("hepsi silindi, kuyruğa bir şey düşmez");
        var details = await PurgeAuditDetailsAsync(customerId);
        details.Should().Contain("\"deletedMediaObjects\":5").And.Contain("\"pendingMediaObjects\":0");
    }

    [Fact]
    public async Task Purge_silinemeyen_dosyayi_kuyruga_yazar_ve_imha_kaydina_saymaz()
    {
        var (admin, _, customerId, email, licenses) = await SeedAsync(licenses: 1);
        var license = licenses[0];
        var (paymentOk, paymentStuck, waOk, waStuck) = (PaymentKey(license), PaymentKey(license), WaKey(license), WaKey(license));
        await PutAsync(paymentOk, paymentStuck, waOk, waStuck);
        _factory.Payments.FailingKeys[paymentStuck] = 0;
        _factory.WhatsApp.FailingKeys[waStuck] = 0;

        var resp = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent, "tek dosyanın silinememesi yasal silmeyi durdurmaz");
        Exists(paymentOk).Should().BeFalse();
        Exists(waOk).Should().BeFalse();
        // Niyet kaybolmaz: lisans satırı gitti, anahtar yalnız kuyrukta yaşar; OrphanedMediaCleanupJob yeniden dener.
        using (var scope = _factory.Services.CreateScope())
        {
            var queued = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().OrphanedMediaObjects.AsNoTracking()
                .Where(o => o.ObjectKey == paymentStuck || o.ObjectKey == waStuck).ToListAsync();
            queued.Select(o => o.ObjectKey).Should().BeEquivalentTo(new[] { paymentStuck, waStuck });
            queued.Should().OnlyContain(o => o.ShopperId == null && o.PaymentId == null && o.DeletedAt == null
                && o.AttemptCount == 1 && o.LastError!.Contains("kilitli"));
        }
        (await RecordsAsync(customerId, ShopperPurgeService.MediaTarget)).Should().ContainSingle()
            .Which.RowCount.Should().Be(1, "imha kaydı yalnız gerçekten silineni sayar");
        (await RecordsAsync(customerId, LicenseMediaPurge.WhatsAppMediaTarget)).Should().ContainSingle()
            .Which.RowCount.Should().Be(1);
        (await PurgeAuditDetailsAsync(customerId)).Should().Contain("\"deletedMediaObjects\":2")
            .And.Contain("\"pendingMediaObjects\":2");
    }

    [Fact]
    public async Task Purge_kuyrukta_bekleyen_ayni_anahtara_ikinci_satir_acmaz()
    {
        // Müşteri silmesinden (ShopperPurgeService) kuyrukta kalmış dekontlar: kuyruk anahtarı tekil. Yeniden silinemeyen anahtarın
        // satırı güncellenir (ikinci satır SQL Server'da tekil indeksi patlatır, kuyruk yazımı düşerdi); bu silmede silinenin
        // satırı kapanır (yoksa temizlik işi aynı nesneyi bir daha silip imha kaydına ikinci kez yazardı).
        var (admin, _, customerId, email, licenses) = await SeedAsync(licenses: 1);
        var license = licenses[0];
        var (stillStuck, nowDeleted) = (PaymentKey(license), PaymentKey(license));
        await PutAsync(stillStuck, nowDeleted);
        _factory.Payments.FailingKeys[stillStuck] = 0;
        var shopperId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            foreach (var key in new[] { stillStuck, nowDeleted })
                db.OrphanedMediaObjects.Add(new OrphanedMediaObject
                {
                    Id = Guid.NewGuid(), ObjectKey = key, ShopperId = shopperId, AttemptCount = 3, LastError = "eski hata",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), LastAttemptAt = DateTimeOffset.UtcNow.AddDays(-1),
                });
            await db.SaveChangesAsync();
        }

        (await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var verify = _factory.Services.CreateScope();
        var rows = await verify.ServiceProvider.GetRequiredService<LicenseDbContext>().OrphanedMediaObjects.AsNoTracking()
            .Where(o => o.ObjectKey == stillStuck || o.ObjectKey == nowDeleted).ToListAsync();
        rows.Should().HaveCount(2, "aynı anahtara ikinci satır açılmaz");
        var stuck = rows.Single(o => o.ObjectKey == stillStuck);
        stuck.AttemptCount.Should().Be(4);
        stuck.DeletedAt.Should().BeNull();
        stuck.LastError.Should().Contain("kilitli");
        stuck.ShopperId.Should().Be(shopperId, "satırın konusu değişmez");
        var closed = rows.Single(o => o.ObjectKey == nowDeleted);
        closed.DeletedAt.Should().NotBeNull("gerçek silme onayı bu silmeden geldi");
        closed.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Purge_dosyalar_listelenemezse_hicbir_seyi_silmez()
    {
        // Lisans satırı silinince lisansın öneki bir daha bulunamaz: liste alınamıyorsa silme hiç başlamaz, operatör yineler.
        var (admin, _, customerId, email, licenses) = await SeedAsync(licenses: 1);
        var license = licenses[0];
        var receipt = PaymentKey(license);
        await PutAsync(receipt);
        _factory.Payments.UnlistableLicenses[license] = 0;

        var resp = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()
            .Should().Be("media-storage-unavailable");
        Exists(receipt).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId)).Email.Should().Be(email, "hiçbir şey silinmedi");
        (await db.Licenses.AnyAsync(l => l.Id == license)).Should().BeTrue();
        (await db.DataDestructionRecords.AnyAsync(r => r.SubjectId == customerId)).Should().BeFalse();
    }

    [Fact]
    public async Task Purge_lisanslarin_yayin_paylasimi_medyasini_ve_urun_fotograflarini_siler_baska_lisansinkiler_kalir()
    {
        // Karar 23.10.2026 ("silinsin"): BroadcastPost ve ProductPhoto satırları lisansla kaskad gidiyordu, nesneler kovada
        // kalıyordu (ürün fotoğrafı yetim süpürücüsü yalnız VAR OLAN lisansları dolaşır). Kovadan önekle listelenir: hiç ürüne
        // bağlanmamış yükleme ve silinmesi yutulmuş eski paylaşım medyası da gider.
        var (admin, adminId, customerId, email, licenses) = await SeedAsync(licenses: 2);
        var (l1, l2) = (licenses[0], licenses[1]);
        var otherLicense = Guid.NewGuid();
        var posts = new[] { PostKey(l1), PostKey(l1), PostKey(l2) };
        var photos = new[] { PhotoKey(l1), PhotoKey(l2), PhotoKey(l2) };
        var others = new[] { PostKey(otherLicense), PhotoKey(otherLicense) };
        await PutAsync(posts.Concat(photos).Concat(others).ToArray());

        var resp = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        posts.Concat(photos).Should().OnlyContain(k => !Exists(k), "yayıncının paylaşım medyası ve ürün fotoğrafları silinir");
        others.Should().OnlyContain(k => Exists(k), "başka lisansın nesneleri kalır");

        var postRecords = await RecordsAsync(customerId, LicenseMediaPurge.BroadcastPostMediaTarget);
        var photoRecords = await RecordsAsync(customerId, LicenseMediaPurge.ProductPhotoMediaTarget);
        postRecords.Select(r => (r.LicenseId, r.RowCount)).Should().BeEquivalentTo(new[] { ((Guid?)l1, 2), ((Guid?)l2, 1) });
        photoRecords.Select(r => (r.LicenseId, r.RowCount)).Should().BeEquivalentTo(new[] { ((Guid?)l1, 1), ((Guid?)l2, 2) });
        postRecords.Concat(photoRecords).Should().OnlyContain(r => r.Action == DataDestruction.Actions.Deleted
            && r.Trigger == DataDestruction.Triggers.CustomerPurge && r.Criterion == LicenseMediaPurge.CustomerPurgeCriterion
            && r.Actor == adminId.ToString());
        (await PurgeAuditDetailsAsync(customerId)).Should().Contain("\"deletedMediaObjects\":6")
            .And.Contain("\"pendingMediaObjects\":0");
    }

    [Fact]
    public async Task Purge_silinemeyen_yayin_dosyasini_kuyruga_yazar_ve_imha_kaydina_saymaz()
    {
        var (admin, _, customerId, email, licenses) = await SeedAsync(licenses: 1);
        var license = licenses[0];
        var (postOk, postStuck, photoStuck) = (PostKey(license), PostKey(license), PhotoKey(license));
        await PutAsync(postOk, postStuck, photoStuck);
        _factory.BroadcastMedia.FailingKeys[postStuck] = 0;
        _factory.BroadcastMedia.FailingKeys[photoStuck] = 0;

        var resp = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent, "tek dosyanın silinememesi yasal silmeyi durdurmaz");
        Exists(postOk).Should().BeFalse();
        Exists(postStuck).Should().BeTrue();
        Exists(photoStuck).Should().BeTrue();
        using (var scope = _factory.Services.CreateScope())
        {
            var queued = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().OrphanedMediaObjects.AsNoTracking()
                .Where(o => o.ObjectKey == postStuck || o.ObjectKey == photoStuck).ToListAsync();
            queued.Select(o => o.ObjectKey).Should().BeEquivalentTo(new[] { postStuck, photoStuck });
            queued.Should().OnlyContain(o => o.ShopperId == null && o.PaymentId == null && o.DeletedAt == null
                && o.AttemptCount == 1 && o.LastError!.Contains("kilitli"));
        }
        (await RecordsAsync(customerId, LicenseMediaPurge.BroadcastPostMediaTarget)).Should().ContainSingle()
            .Which.RowCount.Should().Be(1, "imha kaydı yalnız gerçekten silineni sayar");
        (await RecordsAsync(customerId, LicenseMediaPurge.ProductPhotoMediaTarget)).Should().BeEmpty(
            "tek ürün fotoğrafı silinemedi: kayıt temizlik işi sildiğinde yazılır");
        (await PurgeAuditDetailsAsync(customerId)).Should().Contain("\"deletedMediaObjects\":1")
            .And.Contain("\"pendingMediaObjects\":2");
    }

    [Fact]
    public async Task Purge_yayin_dosyalari_listelenemezse_hicbir_seyi_silmez()
    {
        var (admin, _, customerId, email, licenses) = await SeedAsync(licenses: 1);
        var license = licenses[0];
        var (receipt, post) = (PaymentKey(license), PostKey(license));
        await PutAsync(receipt, post);
        _factory.BroadcastMedia.UnlistablePrefixes[$"{license:N}/products/"] = 0;

        var resp = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()
            .Should().Be("media-storage-unavailable");
        Exists(receipt).Should().BeTrue();
        Exists(post).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Licenses.AnyAsync(l => l.Id == license)).Should().BeTrue("hiçbir şey silinmedi");
        (await db.DataDestructionRecords.AnyAsync(r => r.SubjectId == customerId)).Should().BeFalse();
    }
}
