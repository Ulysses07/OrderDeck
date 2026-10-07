using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Dekont onayından sonra kuyruktan koşan gölge bağdaştırma işi. Üretim DI'ından çözülür (kayıt da sınanır).</summary>
public sealed class PaymentMatchReconcileJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PaymentMatchReconcileJobTests(ApiFactory factory) => _factory = factory;

    private sealed record Seeded(Guid LicenseId, Guid WpfCustomerId, BankTransaction Tx, Guid PaymentId);

    /// <summary>Lisans (istenirse Obifin bağlantılı), müşteri + shopper bağı, açıklamasında kullanıcı adı geçen gelen hareket
    /// ve onun önerisi, aynı tutarlı ödeme. Kimlik alanları üretilir.</summary>
    private async Task<Seeded> SeedAsync(PaymentStatus status, bool connected = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = Guid.NewGuid(); var shopperId = Guid.NewGuid();
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        if (connected)
            db.ObifinConnections.Add(new ObifinConnection
            {
                Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://obifin.invalid", UserCode = $"u-{Guid.NewGuid():N}",
                Status = ObifinConnectionStatus.Verified, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ornek_musteri34", UpdatedAt = DateTimeOffset.UtcNow };
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 275m, Currency = "TL", OccurredAt = when, Description = "HAVALE ornek_musteri34",
            FetchedAt = when,
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = lic, ShopperId = shopperId, PayerName = "ORNEK MUSTERI", Amount = 275m, PaidAt = when,
            ReferansNo = $"r-{Guid.NewGuid():N}", Status = status, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ApprovedAt = status == PaymentStatus.Approved ? DateTimeOffset.UtcNow : null,
        };
        db.WpfCustomerProjections.Add(wpf);
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = lic, Platform = "youtube", Username = "ornek_musteri34",
            WpfCustomerId = wpf.Id, JoinedAt = DateTimeOffset.UtcNow,
        });
        db.BankTransactions.Add(tx);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PaymentMatcher>().MatchAsync(tx, CancellationToken.None);
        return new Seeded(lic, wpf.Id, tx, payment.Id);
    }

    private async Task RunAsync(Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PaymentMatchReconcileJob>().RunAsync(paymentId, CancellationToken.None);
    }

    private async Task<(PaymentMatch Match, int Gaps)> StateAsync(Seeded s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == s.Tx.Id),
            await db.PaymentMatchGaps.CountAsync(g => g.PaymentId == s.PaymentId));
    }

    [Fact]
    public async Task Is_onayli_odemeyi_golge_oneriyle_bagdastirir_yeniden_kosu_bir_sey_yazmaz()
    {
        var s = await SeedAsync(PaymentStatus.Approved);

        await RunAsync(s.PaymentId);
        await RunAsync(s.PaymentId); // Hangfire yeniden denemesi / çift teslim

        var (match, gaps) = await StateAsync(s);
        match.PaymentId.Should().Be(s.PaymentId);
        match.ActualWpfCustomerId.Should().Be(s.WpfCustomerId);
        match.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        gaps.Should().Be(0);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Rejected)]
    public async Task Is_onayli_olmayan_odemeyi_atlar(PaymentStatus status)
    {
        var s = await SeedAsync(status);

        await RunAsync(s.PaymentId);

        var (match, gaps) = await StateAsync(s);
        match.PaymentId.Should().BeNull("ret öğretmez ve bağlanmaz");
        match.Status.Should().Be(PaymentMatchStatus.Proposed);
        gaps.Should().Be(0);
    }

    [Fact]
    public async Task Is_Obifin_baglantisi_olmayan_lisansta_gap_yazmaz()
    {
        // Bağlantısız lisansta banka hareketi yoktur: her onay anlamsız bir NoCandidate gap'i yazıp ölçümü kirletirdi.
        var s = await SeedAsync(PaymentStatus.Approved, connected: false);

        await RunAsync(s.PaymentId);

        var (match, gaps) = await StateAsync(s);
        match.PaymentId.Should().BeNull();
        gaps.Should().Be(0);
    }

    [Fact]
    public async Task Is_bilinmeyen_odemede_sessizce_biter()
    {
        var act = () => RunAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }
}
