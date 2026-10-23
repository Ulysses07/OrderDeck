using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Customers;

/// <summary>
/// KVKK silme (<c>POST /api/v1/admin/customers/{id}/purge</c>) gerçek SQL
/// Server'da.
///
/// Bu testler neden InMemory'de olamaz: purge, lisansı silip gerisini
/// <b>veritabanı cascade'ine</b> bırakıyor. InMemory'de yabancı anahtar diye
/// bir şey yok — ne cascade çalışır ne de <c>Restrict</c> kısıtı patlar. Yani
/// purge'ün üretimde 500 atıp atmadığı orada <b>hiçbir zaman</b> görünmez.
///
/// Somut risk: <c>StockMovement</c> hem <c>License</c>'a (Cascade) hem
/// <c>Product</c>/<c>ProductVariant</c>'a (Restrict) bağlı. Stok hareketi olan
/// bir müşteri silinirken bu ikisi çarpışırsa purge FK ihlaliyle düşer ve
/// yasal silme talebi karşılanamaz.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CustomerPurgeRelationalTests : IAsyncLifetime
{
    private const string Password = "purge-password-123";

    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public CustomerPurgeRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Stok_hareketi_olan_musteri_silinebilir()
    {
        var (client, customerId, email) = await SeedCustomerAsync();
        await SeedCatalogAndStockAsync(customerId);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "stok defteri olan bir yayıncı da KVKK talebinde silinebilmeli; " +
            "cascade ile Restrict çarpışırsa bu istek FK ihlaliyle 500 döner");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Licenses.AnyAsync(l => l.CustomerId == customerId)).Should().BeFalse();
        // Asıl mesele burası: hareketler PII taşıyan katalogla birlikte gitmeli,
        // yetim kalmamalı. Kalırlarsa silme eksik ve bakiye sorgusu onları toplar.
        (await db.StockMovements.AnyAsync()).Should().BeFalse();
        (await db.Products.AnyAsync()).Should().BeFalse();
        (await db.ProductVariants.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Imha_kaydi_bu_silmenin_sildigini_sayar_okumadan_sonra_eklenen_satiri_saymaz()
    {
        // KVKK Ö-13: imha kaydı kalıcı, yasal kayıttır; silinenden fazlasını göstermemeli. RemoveRange satırları okuduktan SONRA,
        // kayıttan ÖNCE eklenen bir satır (girişin yazdığı yenileme jetonu, herkese açık formun gönderimi) bu silmede gitmez:
        // kayıt onu saymamalı. Yarış zamanlamayla değil sırayla kurulur: jeton silmenin okuyucusu kapanırken eklenir.
        var hook = new InsertRefreshTokenAfterRead(_cs);
        using var factory = new HookedRelationalApiFactory(_cs, hook);
        var (client, customerId, email) = await SeedCustomerAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.RefreshTokens.Add(NewRefreshToken(customerId));
            await db.SaveChangesAsync();
        }
        hook.CustomerId = customerId;

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        hook.Fired.Should().BeTrue("yarış kurulmalı: silmenin okumasından sonra bir jeton eklendi");
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await verifyDb.RefreshTokens.CountAsync(r => r.CustomerId == customerId))
            .Should().Be(1, "arada eklenen jeton bu silmede gitmedi (kimlik nesli ilerlediği için geçersiz)");
        (await verifyDb.DataDestructionRecords.AsNoTracking()
                .SingleAsync(r => r.SubjectId == customerId && r.Target == "RefreshTokens"))
            .RowCount.Should().Be(1, "kayıt bu işlemin sildiğini söyler");
    }

    [Fact]
    public async Task Silmenin_okumasindan_sonra_ilk_kez_yazilan_veri_sorumlusu_bilgisi_de_silinir()
    {
        // KVKK K-3: yayıncı bilgisini silme veri sorumlusu tablosunu okuduktan SONRA, kaydından ÖNCE ilk kez girdi (açık panel:
        // erişim jetonu silmeden sonra da 15 dk geçerli). Okuma boş döndü, satır bu silmenin listesinde yok; müşteri satırı
        // anonimleşip kaldığı için FK geçerli, satır kalırdı. Silme kayıttan sonra müşteriye bağlı satırı yeniden arar, siler ve
        // imha kaydına yazar. Kayıttan sonra gelen yazım ise müşterinin silindiğini görür, yazmaz (DataControllerProfilesTests).
        var hook = new UpsertDataControllerAfterRead(_cs);
        using var factory = new HookedRelationalApiFactory(_cs, hook);
        var (client, customerId, email) = await SeedCustomerAsync(factory);
        hook.CustomerId = customerId;

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        hook.Fired.Should().BeTrue("yarış kurulmalı: silmenin okumasından sonra bilgi ilk kez yazıldı");
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await verifyDb.DataControllerProfiles.AnyAsync(p => p.CustomerId == customerId)).Should().BeFalse();
        (await verifyDb.DataControllerProfileVersions.AnyAsync(v => v.CustomerId == customerId)).Should().BeFalse();
        (await verifyDb.DataDestructionRecords.AsNoTracking()
                .Where(r => r.SubjectId == customerId && r.Target == "DataControllerProfiles").SumAsync(r => r.RowCount))
            .Should().Be(1);
        (await verifyDb.DataDestructionRecords.AsNoTracking()
                .Where(r => r.SubjectId == customerId && r.Target == "DataControllerProfileVersions").SumAsync(r => r.RowCount))
            .Should().Be(1);
    }

    [Fact]
    public async Task Kuyrukta_bekleyen_dekont_yeniden_silinemezse_ikinci_kuyruk_satiri_acilmaz()
    {
        // KVKK karar 5: yayıncı silmesi lisansın dekontlarını siler, silinemeyeni kuyruğa yazar. Kuyruk anahtarı SQL Server'da
        // tekil (InMemory bunu uygulamaz): müşteri silmesinden bekleyen anahtara ikinci satır açılsaydı kuyruk yazımı düşer, silme
        // 500 döner, anahtar lisansla birlikte kaybolurdu. Bekleyen satır önekle (LIKE) bulunup güncellenir.
        using var factory = new MediaRelationalApiFactory(_cs);
        var (client, customerId, email) = await SeedCustomerAsync(factory);
        var licenseId = Guid.NewGuid();
        var stuck = $"{ShopperPaymentKeys.LicensePrefix(licenseId)}{Guid.NewGuid():N}.pdf";
        var deletable = $"{ShopperPaymentKeys.LicensePrefix(licenseId)}{Guid.NewGuid():N}.pdf";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = licenseId, CustomerId = customerId, LicenseKey = "LDK-P-" + Guid.NewGuid().ToString("N")[..12],
                SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            db.OrphanedMediaObjects.Add(new OrphanedMediaObject
            {
                Id = Guid.NewGuid(), ObjectKey = stuck, ShopperId = Guid.NewGuid(), AttemptCount = 1, LastError = "eski hata",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), LastAttemptAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }
        foreach (var key in new[] { stuck, deletable }) await factory.Payments.UploadAsync(key, new byte[] { 1 }, "application/pdf");
        factory.Payments.FailingKeys[stuck] = 0;

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await verifyDb.OrphanedMediaObjects.AsNoTracking().SingleAsync(o => o.ObjectKey == stuck);
        row.AttemptCount.Should().Be(2);
        row.DeletedAt.Should().BeNull();
        factory.Payments.Inner.Contains(deletable).Should().BeFalse();
        (await verifyDb.DataDestructionRecords.AsNoTracking()
                .SingleAsync(r => r.SubjectId == customerId && r.Target == "ShopperPaymentMedia"))
            .Should().BeEquivalentTo(new { LicenseId = (Guid?)licenseId, RowCount = 1 });
    }

    /// <summary>Dekont ve WhatsApp depoları <see cref="CustomerPurgeMediaTests"/>'in hata üretebilen sahteleriyle.</summary>
    private sealed class MediaRelationalApiFactory(string cs) : RelationalApiFactory(cs)
    {
        public CustomerPurgeMediaTests.FlakyPaymentStorage Payments { get; } = new();
        public CustomerPurgeMediaTests.FlakyWhatsAppStore WhatsApp { get; } = new();

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

    /// <summary>Silmenin veri sorumlusu bilgisini okuyan sorgusunun okuyucusu kapanırken AYRI bağlamda o müşteriye ilk kez bilgi
    /// yazar (panelin yolu, <see cref="DataControllerProfiles.UpsertAsync"/>) — bir kez.</summary>
    private sealed class UpsertDataControllerAfterRead(string cs) : DbCommandInterceptor
    {
        private int _fired;
        public Guid? CustomerId { get; set; }
        public bool Fired => _fired == 1;

        public override InterceptionResult DataReaderClosing(DbCommand command, DataReaderClosingEventData eventData,
            InterceptionResult result)
        {
            UpsertIfDue(command);
            return result;
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            UpsertIfDue(command);
            return ValueTask.FromResult(result);
        }

        private void UpsertIfDue(DbCommand command)
        {
            if (CustomerId is not { } customerId
                || !command.CommandText.Contains("FROM [DataControllerProfiles] AS", StringComparison.Ordinal)
                || Interlocked.Exchange(ref _fired, 1) == 1) return;
            using var other = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(cs).Options);
            DataControllerProfiles.UpsertAsync(other, customerId, DataControllerProfilesTests.Valid(), "panel",
                CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static RefreshToken NewRefreshToken(Guid customerId) => new()
    {
        Id = Guid.NewGuid(), CustomerId = customerId, TokenHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
        CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
    };

    private sealed class HookedRelationalApiFactory(string cs, IInterceptor hook) : RelationalApiFactory(cs)
    {
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(hook);
    }

    /// <summary>Silinecek müşterinin yenileme jetonlarını okuyan sorgunun okuyucusu kapanırken (satırlar okundu, okuma kilidi
    /// yok) AYRI bağlantıdan aynı müşteriye bir jeton daha ekler — bir kez. COUNT sorgusu tetiklemez: yarış silmenin
    /// okumasıyla kayıt arasına kurulur. <c>RemoveRange(IQueryable)</c> eşzamanlı (sync) sayımlar, iki kapanma bildirimi de
    /// karşılanır.</summary>
    private sealed class InsertRefreshTokenAfterRead(string cs) : DbCommandInterceptor
    {
        private int _fired;
        public Guid? CustomerId { get; set; }
        public bool Fired => _fired == 1;

        public override InterceptionResult DataReaderClosing(DbCommand command, DataReaderClosingEventData eventData,
            InterceptionResult result)
        {
            InsertIfDue(command);
            return result;
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            InsertIfDue(command);
            return ValueTask.FromResult(result);
        }

        private void InsertIfDue(DbCommand command)
        {
            if (CustomerId is not { } customerId
                || !command.CommandText.Contains("FROM [RefreshTokens]", StringComparison.Ordinal)
                || command.CommandText.Contains("COUNT(", StringComparison.Ordinal)
                || Interlocked.Exchange(ref _fired, 1) == 1) return;
            var late = NewRefreshToken(customerId);
            using var conn = new SqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO RefreshTokens (Id, CustomerId, TokenHash, AuthVersion, CreatedAt, ExpiresAt) "
                + "VALUES (@id, @customer, @hash, 0, @created, @expires)";
            cmd.Parameters.AddWithValue("@id", late.Id);
            cmd.Parameters.AddWithValue("@customer", late.CustomerId);
            cmd.Parameters.AddWithValue("@hash", late.TokenHash);
            cmd.Parameters.AddWithValue("@created", late.CreatedAt);
            cmd.Parameters.AddWithValue("@expires", late.ExpiresAt);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Admin oluşturur, giriş yapar, silinecek müşteriyi açar.</summary>
    private Task<(HttpClient Client, Guid CustomerId, string Email)> SeedCustomerAsync() => SeedCustomerAsync(_factory);

    private static async Task<(HttpClient Client, Guid CustomerId, string Email)> SeedCustomerAsync(RelationalApiFactory factory)
    {
        var (token, _) = await factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: $"pw-{Guid.NewGuid():N}");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var email = $"purge-{Guid.NewGuid():N}@example.com";
        var created = await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Silinecek", initialPassword = Password, autoConfirm = true
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await created.Content.ReadFromJsonAsync<IdBody>())!;

        return (client, body.id, email);
    }

    /// <summary>Lisans + ürün + varyant + her iki hedefe hareket. Varyantlı satır
    /// ayrıca yazılıyor: Restrict FK'lardan biri ürüne, diğeri varyanta bakıyor,
    /// yalnız ürün seviyesinde hareket yazsak ikincisi hiç sınanmazdı.</summary>
    private async Task SeedCatalogAndStockAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            // Kolon dar; InMemory kırpma hatası vermediği için burada kısaltmak şart.
            LicenseKey = "LDK-P-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
        };
        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Code = "A12",
            Name = "Ürün",
            NameSearch = "urun",
            DefaultPrice = 100m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            Axis1Value = "M",
            Axis1ValueNorm = "M",
            Axis2ValueNorm = "",
            Barcode = "BRC-" + Guid.NewGuid().ToString("N")[..12],
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Licenses.Add(license);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
            Quantity = 10, Reason = StockMovementReason.Entry,
            OccurredAt = now, CreatedAt = now,
        });
        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
            ProductVariantId = variant.Id,
            Quantity = -1, Reason = StockMovementReason.Sale,
            OccurredAt = now, CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private sealed record IdBody(Guid id);
}
