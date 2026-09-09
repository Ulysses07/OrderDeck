using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Ödeme kararının (Pending → Approved/Rejected) <b>tek kazananlı</b>
/// olduğunu doğrular — 2026-09-09 denetimi F04.
///
/// Bu test neden gerçek SQL Server istiyor: InMemory'de eşzamanlılık
/// semantiği yok — read-check-write açığı orada hiçbir zaman görünmez.
/// Jeton öncesi davranış gerçek SQL Server'da tekrar üretilmişti: paralel
/// onay + red ikisi de 204 dönüp birbirini eziyordu (çifte karar; shopper'a
/// hem "onaylandı" hem "reddedildi" push'u gidebilirdi).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PanelPaymentDecisionConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public PanelPaymentDecisionConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Esazamanli_onay_ve_red_isteklerinden_yalnizca_biri_kazanir()
    {
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = await SeedLicenseAsync(customerId);
        var paymentId = await SeedPendingPaymentAsync(licenseId);

        // İstemciler döngüden önce kuruluyor; CreateClient gecikmesi istekleri
        // ayırıp yarış penceresini daraltırdı (bkz. RefreshTokenConcurrencyTests).
        var clients = Enumerable.Range(0, ParallelAttempts)
            .Select(_ =>
            {
                var c = _factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                return c;
            })
            .ToArray();

        // Isınma turu: var olmayan ödemeye istek — yönlendirme, model bağlama
        // ve EF sorgusu JIT edilsin ki gerçek turda istekler ayrışmasın.
        await Task.WhenAll(clients.Select(c =>
            c.PostAsJsonAsync($"/api/panel/payments/{Guid.NewGuid()}/reject", new { reason = "isinma" })));

        // Gerçek tur: yarısı onay, yarısı red — hepsi aynı anda.
        var responses = await Task.WhenAll(clients.Select((c, i) => i % 2 == 0
            ? c.PostAsync($"/api/panel/payments/{paymentId}/approve", null)
            : c.PostAsJsonAsync($"/api/panel/payments/{paymentId}/reject", new { reason = "yaris" })));

        var won = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        won.Should().Be(1,
            "Pending → karar geçişi tek kazananlı olmalı; birden fazla 204, iki kararın " +
            "birbirini ezebildiği (çifte onay/red) anlamına gelir");
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)
            .Should().Be(ParallelAttempts - 1, "kaybedenlerin hepsi 409 almalı");

        // DB'de kararın kendi içinde tutarlı olduğunu doğrula: HTTP kodları
        // doğru olsa bile alanlar karışmış olabilirdi (onay damgası + red
        // sebebi aynı satırda gibi).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
        payment.Status.Should().NotBe(PaymentStatus.Pending);
        if (payment.Status == PaymentStatus.Approved)
        {
            payment.ApprovedAt.Should().NotBeNull();
            payment.RejectedAt.Should().BeNull();
            payment.RejectReason.Should().BeNull();
        }
        else
        {
            payment.RejectedAt.Should().NotBeNull();
            payment.ApprovedAt.Should().BeNull();
        }
    }

    private async Task<Guid> SeedLicenseAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "pmtc-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    private async Task<Guid> SeedPendingPaymentAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            PayerName = "Yarış Testi",
            Amount = 250.50m,
            PaidAt = now.AddHours(-1),
            ReferansNo = Guid.NewGuid().ToString("N"),
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }
}
