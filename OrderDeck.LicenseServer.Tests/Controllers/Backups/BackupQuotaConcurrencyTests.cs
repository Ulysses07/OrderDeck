using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Backups;

/// <summary>
/// Aynı müşterinin iki yüklemesi aynı anda gelirse kota aşılabiliyor muydu?
///
/// <para>Kontrol "mevcut baytları TOPLA → sığıyorsa YAZ" biçiminde ve iki adım
/// arasında pencere var: ikinci istek birincinin satırı yazılmadan önce topluyor,
/// aynı bayat toplamı görüyor, o da geçiyor. Sonuç sessiz: iki blob da diskte
/// kalıyor, kota aşılmış oluyor ve hiçbir arka plan işi bunu geri almıyor —
/// müşteri elle silmeden kapanmıyor.</para>
///
/// <para><b>Neden gerçek SQL Server:</b> hakem bir <c>rowversion</c> damgası ve
/// (ilk satırda) birincil anahtar çakışması. InMemory sağlayıcısında ikisi de
/// yok — damga sessizce yok sayılır, yani bu testin InMemory'deki hâli düzeltme
/// olmadan da yeşil yanar ve hiçbir şey kanıtlamaz.</para>
///
/// <para><b>Neden bariyer:</b> iki isteği paralel atıp yarışın kendiliğinden
/// oluşmasını ummak kararsız (flaky) bir test demek. Bariyer, iki isteğin de
/// toplamı ALDIKTAN sonra (<c>SaveChanges</c> anında) buluşmasını garantiliyor;
/// yani yarış her koşuda kesin oluşuyor.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class BackupQuotaConcurrencyTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private readonly InsertBarrier _barrier = new(participants: 2);
    private TightQuotaRelationalFactory _factory = null!;

    public BackupQuotaConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new TightQuotaRelationalFactory(await _sql.CreateDatabaseAsync(), _barrier);

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Esazamanli_iki_yukleme_kotayi_birlikte_asamaz()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        // Kota 1 MB; her biri tek başına sığıyor, ikisi birlikte sığmıyor.
        var responses = await Task.WhenAll(
            UploadAsync(client, Payload(700 * 1024)),
            UploadAsync(client, Payload(700 * 1024)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1,
            "kota yalnız birine yetiyor");
        responses.Should().ContainSingle(r =>
                r.StatusCode == HttpStatusCode.Conflict ||
                r.StatusCode == HttpStatusCode.InsufficientStorage,
            "kaybeden istek anlaşılır bir yanıt almalı: ya hakemi kaybettiği için " +
            "409 (yeniden dene) ya da güncel toplamı gördüğü için 507");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.CustomerBackups.AsNoTracking()
            .Where(b => b.CustomerId == customerId).ToListAsync();

        rows.Should().ContainSingle("ikinci satır yazılsaydı kota kalıcı olarak aşılmış olurdu");
        rows.Sum(b => b.SizeBytes).Should().BeLessThanOrEqualTo(1024L * 1024L);

        BlobFiles(_factory.BackupRoot).Should().ContainSingle(
            "kaybeden isteğin blob'u silinmeli; kalırsa hiçbir satırın işaret " +
            "etmediği bir dosya kotanın dışında yer tutar");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] payload)
    {
        // Başlıklar istek başına: DefaultRequestHeaders paylaşılan durum, iki
        // eşzamanlı istek onu birbirinin üzerine yazardı.
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/backups") { Content = content };
        req.Headers.Add("X-Backup-Sha256", Sha256Hex(payload));
        return await client.SendAsync(req);
    }

    private static byte[] Payload(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static IReadOnlyList<string> BlobFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// İlk <c>participants</c> katılımcıyı bekletir, hepsi geldiğinde birlikte
    /// bırakır; sonraki çağrılar hiç beklemez. Tavan süre, düzeltme geri
    /// alınırsa testin kilitlenmek yerine düşmesi için.
    /// </summary>
    private sealed class InsertBarrier
    {
        private readonly int _participants;
        private readonly SemaphoreSlim _released = new(0);
        private int _arrived;

        public InsertBarrier(int participants) => _participants = participants;

        public async Task ArriveAsync()
        {
            var n = Interlocked.Increment(ref _arrived);
            if (n > _participants) return;
            if (n == _participants) { _released.Release(_participants); return; }
            await _released.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Yeni bir <see cref="CustomerBackup"/> satırı yazılırken bariyerde
    /// buluşturur. Diğer kaydetmeler (tohumlama, saklama budaması) etkilenmez.</summary>
    private sealed class BarrierOnBackupInsertInterceptor : SaveChangesInterceptor
    {
        private readonly InsertBarrier _barrier;
        public BarrierOnBackupInsertInterceptor(InsertBarrier barrier) => _barrier = barrier;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null &&
                eventData.Context.ChangeTracker.Entries<CustomerBackup>()
                    .Any(e => e.State == EntityState.Added))
            {
                await _barrier.ArriveAsync();
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TightQuotaRelationalFactory : RelationalApiFactory
    {
        private readonly InsertBarrier _barrier;

        public TightQuotaRelationalFactory(string connectionString, InsertBarrier barrier)
            : base(connectionString) => _barrier = barrier;

        protected override IDictionary<string, string?> ExtraConfig =>
            new Dictionary<string, string?> { ["Backup:PerCustomerQuotaMb"] = "1" };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) =>
            opt.AddInterceptors(new BarrierOnBackupInsertInterceptor(_barrier));
    }
}
