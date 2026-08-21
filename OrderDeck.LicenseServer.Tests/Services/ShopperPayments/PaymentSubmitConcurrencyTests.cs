using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Push;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.PdfParsing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.ShopperPayments;

/// <summary>
/// Aynı dekontun eşzamanlı iki kez gönderilmesi. Gerçekçi senaryo: mobil
/// uygulama isteği timeout sanıp yeniden deniyor, ilk istek hâlâ sunucuda.
///
/// Neden gerçek SQL Server: mükerrer kontrolü (adım 5) ile insert arasındaki
/// pencerede hakem yalnızca <c>(PdfHash)</c> unique index'i olabilir. InMemory
/// sağlayıcısında unique index yok, iki satır da sessizce yazılır — yani
/// buradaki hata InMemory'de hiç görünmez.
///
/// Neden önemli: PdfHash mükerrer dekont dedektörünün tek dayanağı ve
/// yarışın iki ayrı bedeli vardı. (a) Kullanıcı "bu dekont zaten yüklü"
/// yerine 500 görüyordu — anlaşılır bir hata değil, uygulama tekrar deniyor.
/// (b) PDF R2'ye kaydedilmiş oluyor ama ona işaret eden satır yazılamıyor;
/// nesne YETİM kalıyor ve KVKK silmesinden de kaçıyor, çünkü
/// <c>ShopperPurgeService</c> dekontları <c>Payment.MediaObjectKey</c>
/// üzerinden buluyor — kimsenin bilmediği bir nesneyi kimse silemez.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PaymentSubmitConcurrencyTests : IAsyncLifetime
{
    private static readonly byte[] ValidPdf = { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 };

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    private readonly StubShopperPaymentStorage _storage = new();

    public PaymentSubmitConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ayni_dekont_esazamanli_yollanirsa_ikincisi_duplicate_hatasi_alir()
    {
        var (shopperId, licenseId) = await SeedAsync();

        var results = await Task.WhenAll(
            SubmitInOwnScopeAsync(shopperId, licenseId, pdfHash: "yaris-hash", referansNo: "REF-YARIS"),
            SubmitInOwnScopeAsync(shopperId, licenseId, pdfHash: "yaris-hash", referansNo: "REF-YARIS"));

        results.Count(r => r.Error is null).Should().Be(1,
            "aynı dekonttan yalnız bir ödeme satırı doğmalı");
        results.Should().ContainSingle(r => r.Error == "duplicate-dekont",
            "kaybeden istek anlaşılır bir 409 almalı; ham DbUpdateException 500'e " +
            "dönüşüyor ve istemci onu geçici hata sanıp yeniden deniyor");
    }

    /// <summary>
    /// Kaybeden isteğin R2'ye çoktan yüklediği PDF geri alınmalı. Yetim nesne
    /// hem kovada yer tutar hem de KVKK silmesinin göremeyeceği bir kişisel
    /// veri kopyası bırakır.
    /// </summary>
    [Fact]
    public async Task Kaybeden_istek_yukledigi_pdfi_geri_alir()
    {
        var (shopperId, licenseId) = await SeedAsync();

        await Task.WhenAll(
            SubmitInOwnScopeAsync(shopperId, licenseId, pdfHash: "yetim-hash", referansNo: "REF-YETIM"),
            SubmitInOwnScopeAsync(shopperId, licenseId, pdfHash: "yetim-hash", referansNo: "REF-YETIM"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var keys = await db.Payments.AsNoTracking()
            .Select(p => p.MediaObjectKey!).ToListAsync();

        keys.Should().ContainSingle("tek ödeme satırı yazılmalıydı");
        _storage.Contains(keys[0]).Should().BeTrue("kazanan isteğin nesnesi durmalı");
        _storage.Count.Should().Be(1,
            "kaybeden isteğin nesnesi silinmeliydi; kalırsa hiçbir satırın " +
            "işaret etmediği bir dekont kovada süresiz durur");
    }

    /// <summary>
    /// Referans no'nun mükerrerliği kodda hiç kontrol edilmiyor — tek koruma
    /// <c>(LicenseId, ReferansNo)</c> unique index'i. Aynı referans farklı
    /// PDF'lerle geldiğinde de kullanıcı 500 değil anlaşılır hata görmeli.
    /// </summary>
    [Fact]
    public async Task Ayni_referans_farkli_pdf_ile_gelirse_duplicate_referans_doner()
    {
        var (shopperId, licenseId) = await SeedAsync();

        var first = await SubmitInOwnScopeAsync(
            shopperId, licenseId, pdfHash: "ref-hash-1", referansNo: "REF-AYNI");
        first.Error.Should().BeNull("ilk gönderim ön koşul");

        var second = await SubmitInOwnScopeAsync(
            shopperId, licenseId, pdfHash: "ref-hash-2", referansNo: "REF-AYNI");

        second.Error.Should().Be("duplicate-referans");
        _storage.Count.Should().Be(1, "reddedilen gönderimin PDF'i geri alınmalı");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record SubmitOutcome(string? Error);

    /// <summary>
    /// Servisi KENDİ DbContext'iyle kurar — gerçek istekte de her istek kendi
    /// scope'unu alır. Depo bilerek paylaşılıyor: yetim nesne iddiası ancak
    /// tek bir kova üzerinden ölçülebilir.
    /// </summary>
    private async Task<SubmitOutcome> SubmitInOwnScopeAsync(
        Guid shopperId, Guid licenseId, string pdfHash, string referansNo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = new ShopperPaymentSubmissionService(
            db,
            new ShopperPaymentRateLimiter(db),
            _storage,
            new StaticParser(pdfHash, referansNo),
            new NoopNotificationSender(),
            NullLogger<ShopperPaymentSubmissionService>.Instance);

        var input = new SubmitInput(
            ShopperId: shopperId,
            LicenseId: licenseId,
            PdfBytes: ValidPdf,
            OverrideAmount: null,
            OverridePayerName: null,
            OverridePaidAt: null,
            OverrideReferansNo: null,
            IpAddress: "1.2.3.4",
            UserAgent: "Test/1.0");

        try
        {
            await svc.SubmitAsync(input, default);
            return new SubmitOutcome(null);
        }
        catch (SubmitFailureException ex)
        {
            return new SubmitOutcome(ex.ErrorCode);
        }
    }

    private sealed class StaticParser : IPdfDekontParser
    {
        private readonly string _hash;
        private readonly string _ref;
        public StaticParser(string hash, string referansNo) { _hash = hash; _ref = referansNo; }

        public PdfDekontParser.ParseResult Parse(byte[] pdfBytes) => new(
            PayerName: "AYŞE YILMAZ",
            Amount: 500m,
            PaidAt: new DateTime(2026, 8, 1, 12, 0, 0),
            ReferansNo: _ref,
            PdfHash: _hash,
            RawText: "AYŞE YILMAZ 500,00 TL",
            RecipientIban: "TR330006100519786457841326",
            RecipientName: "TEST ALICI");
    }

    private sealed class NoopNotificationSender : INotificationSender
    {
        public Task SendToCustomerAsync(Guid customerId, string title, string body,
            IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendToShoppersAsync(IReadOnlyCollection<Guid> shopperIds, string title, string body,
            IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private async Task<(Guid ShopperId, Guid LicenseId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"pay-{Guid.NewGuid():N}@x",
            Name = "Yayıncı",
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            PaymentIban = "TR330006100519786457841326",
        };
        db.Licenses.Add(license);

        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Ayşe Yılmaz",
            Phone = "+90500" + Random.Shared.Next(1_000_000, 9_999_999),
            PasswordHash = "hash",
            Address = "Ankara",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);

        await db.SaveChangesAsync();
        return (shopper.Id, license.Id);
    }
}
