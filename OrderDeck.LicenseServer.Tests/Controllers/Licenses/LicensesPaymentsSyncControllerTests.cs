using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesPaymentsSyncControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicensesPaymentsSyncControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-SYNC-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, customerId, licenseId);
    }

    /// <summary>
    /// Kararlılık ufkunun gerisinde kalan bir damga: <c>since</c> ucu son
    /// saniyelerde değişen satırları bilerek okumuyor (bkz. ReverseSyncCursor).
    /// </summary>
    private static DateTimeOffset Settled => DateTimeOffset.UtcNow.AddMinutes(-1);

    private sealed record SyncedPaymentDto(
        Guid Id, string Status, DateTimeOffset? ApprovedAt,
        DateTimeOffset? RejectedAt, string? RejectReason, DateTimeOffset UpdatedAt);

    [Fact]
    public async Task Sync_inserts_new_payments_and_returns_echoed_status()
    {
        var (client, _, licenseId) = await SetupAsync();
        var paymentId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new
                    {
                        id = paymentId,
                        payerName = "Örnek Müşteri",
                        amount = 250.75m,
                        paidAt = DateTimeOffset.UtcNow.AddHours(-1),
                        referansNo = "REF-001",
                        pdfHash = (string?)null
                    }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
        body.Should().HaveCount(1);
        body![0].Id.Should().Be(paymentId);
        body[0].Status.Should().Be("pending");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.Payments.FirstAsync(p => p.Id == paymentId);
        stored.PayerName.Should().Be("Örnek Müşteri");
        stored.Amount.Should().Be(250.75m);
        stored.LicenseId.Should().Be(licenseId);
    }

    [Fact]
    public async Task Sync_is_idempotent_by_payment_id()
    {
        var (client, _, licenseId) = await SetupAsync();
        var paymentId = Guid.NewGuid();

        async Task<HttpResponseMessage> Push() => await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = paymentId, payerName = "Foo", amount = 100m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-IDEM", pdfHash = (string?)null }
                }
            });

        (await Push()).EnsureSuccessStatusCode();
        (await Push()).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.Payments.CountAsync(p => p.LicenseId == licenseId);
        count.Should().Be(1, "same Id should upsert, not insert duplicate");
    }

    [Fact]
    public async Task Sync_updates_mutable_fields_on_existing_payment()
    {
        var (client, _, licenseId) = await SetupAsync();
        var paymentId = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = paymentId, payerName = "ilk isim", amount = 100m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-MUT", pdfHash = (string?)null }
                }
            });

        // Re-push with refined name (e.g. better PDF parse)
        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = paymentId, payerName = "DOGRU ISIM", amount = 150m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-MUT", pdfHash = "abc123" }
                }
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.Payments.FirstAsync(p => p.Id == paymentId);
        stored.PayerName.Should().Be("DOGRU ISIM");
        stored.Amount.Should().Be(150m);
        stored.PdfHash.Should().Be("abc123");
    }

    [Fact]
    public async Task Sync_other_customers_license_returns_404()
    {
        var (clientA, _, _) = await SetupAsync();
        var (_, _, licenseB) = await SetupAsync();

        var resp = await clientA.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseB}/payments/sync",
            new { payments = Array.Empty<object>() });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sync_empty_batch_returns_200_with_empty_array()
    {
        var (client, _, licenseId) = await SetupAsync();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new { payments = Array.Empty<object>() });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_rejects_batch_over_200()
    {
        var (client, _, licenseId) = await SetupAsync();
        var huge = Enumerable.Range(0, 201).Select(i => new
        {
            id = Guid.NewGuid(),
            payerName = $"Payer {i}",
            amount = 10m,
            paidAt = DateTimeOffset.UtcNow,
            referansNo = $"R-{i}",
            pdfHash = (string?)null
        }).ToArray();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new { payments = huge });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sync_without_auth_returns_401()
    {
        var (_, _, licenseId) = await SetupAsync();
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new { payments = Array.Empty<object>() });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Since_returns_updated_payments_after_cursor()
    {
        var (client, _, licenseId) = await SetupAsync();
        var pid1 = Guid.NewGuid();
        var pid2 = Guid.NewGuid();
        var cursor = DateTimeOffset.UtcNow.AddMinutes(-10);

        // Two payments with UpdatedAt in the future, then one in the past
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Payments.AddRange(
                new Payment
                {
                    Id = pid1, LicenseId = licenseId, PayerName = "A", Amount = 1,
                    PaidAt = DateTimeOffset.UtcNow, ReferansNo = "S1",
                    Status = PaymentStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = Settled
                },
                new Payment
                {
                    Id = pid2, LicenseId = licenseId, PayerName = "B", Amount = 2,
                    PaidAt = DateTimeOffset.UtcNow, ReferansNo = "S2",
                    Status = PaymentStatus.Approved,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = Settled,
                    ApprovedAt = DateTimeOffset.UtcNow
                },
                new Payment
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId, PayerName = "C", Amount = 3,
                    PaidAt = DateTimeOffset.UtcNow, ReferansNo = "S3",
                    Status = PaymentStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                    UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
                });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync(
            $"/api/v1/licenses/{licenseId}/payments/since?since={Uri.EscapeDataString(cursor.ToString("O"))}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
        body.Should().HaveCount(2, "only two updated after cursor");
        body!.Should().Contain(d => d.Id == pid1);
        body.Should().Contain(d => d.Id == pid2);
    }

    [Fact]
    public async Task Since_isolates_by_license()
    {
        var (clientA, _, licenseA) = await SetupAsync();
        var (_, _, licenseB) = await SetupAsync();
        var cursor = DateTimeOffset.UtcNow.AddMinutes(-10);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Payments.AddRange(
                new Payment
                {
                    Id = Guid.NewGuid(), LicenseId = licenseA, PayerName = "A1",
                    Amount = 1, PaidAt = DateTimeOffset.UtcNow, ReferansNo = "AL1",
                    Status = PaymentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = Settled
                },
                new Payment
                {
                    Id = Guid.NewGuid(), LicenseId = licenseB, PayerName = "B1",
                    Amount = 1, PaidAt = DateTimeOffset.UtcNow, ReferansNo = "BL1",
                    Status = PaymentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var resp = await clientA.GetAsync(
            $"/api/v1/licenses/{licenseA}/payments/since?since={Uri.EscapeDataString(cursor.ToString("O"))}");
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
        body!.Should().HaveCount(1, "client A sees only A's license payments");
    }

    // ── Eşitlik kümesi sayfa sınırında kesilirse hiçbir dekont kaybolmaz ─────
    // WPF tek push'ta 200 dekonta aynı UpdatedAt damgasını basıyor; istemcinin
    // sayfa boyutu da 200. Eşitlik kümesi gerçek, kuramsal değil.

    [Fact]
    public async Task Since_tie_group_split_across_pages_loses_no_payment()
    {
        var (client, _, licenseId) = await SetupAsync();

        var tie = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            for (var i = 0; i < ids.Count; i++)
                db.Payments.Add(new Payment
                {
                    Id = ids[i], LicenseId = licenseId, PayerName = $"Tie {i}", Amount = i + 1,
                    PaidAt = tie, ReferansNo = $"TIE-{i}", Status = PaymentStatus.Pending,
                    CreatedAt = tie, UpdatedAt = tie
                });
            await db.SaveChangesAsync();
        }

        var cursor = tie;
        var cursorId = Guid.Empty;
        var seen = new List<Guid>();

        for (var page = 0; page < 5; page++)
        {
            var resp = await client.GetAsync(
                $"/api/v1/licenses/{licenseId}/payments/since" +
                $"?since={Uri.EscapeDataString(cursor.ToString("O"))}&sinceId={cursorId:D}&take=2");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var items = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
            if (items!.Count == 0) break;

            seen.AddRange(items.Select(i => i.Id));
            cursor = items[^1].UpdatedAt;
            cursorId = items[^1].Id;
        }

        seen.Should().BeEquivalentTo(ids,
            "aynı damgayı paylaşan dekontlar sayfa sınırında kesilse de hepsi teslim edilmeli");
    }

    // ── Kararlılık ufku: az önce yazılmış dekont bu turda okunmaz ───────────

    [Fact]
    public async Task Since_withholds_payments_written_inside_the_stability_horizon()
    {
        var (client, _, licenseId) = await SetupAsync();

        var settled = Guid.NewGuid();
        var justWritten = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Payments.AddRange(
                new Payment
                {
                    Id = settled, LicenseId = licenseId, PayerName = "Yerleşmiş", Amount = 10,
                    PaidAt = Settled, ReferansNo = "H-SETTLED", Status = PaymentStatus.Approved,
                    CreatedAt = Settled, UpdatedAt = Settled, ApprovedAt = Settled
                },
                new Payment
                {
                    Id = justWritten, LicenseId = licenseId, PayerName = "Yeni", Amount = 20,
                    PaidAt = DateTimeOffset.UtcNow, ReferansNo = "H-FRESH",
                    Status = PaymentStatus.Approved,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    ApprovedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var since = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O"));
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/payments/since?since={since}");
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();

        body!.Select(p => p.Id).Should().Contain(settled);
        body.Select(p => p.Id).Should().NotContain(justWritten,
            "ufkun içindeki satır okunup imleç oraya taşınırsa, damgası daha eski olduğu " +
            "hâlde henüz commit etmemiş bir onay imlecin gerisinde kalıp kalıcı olarak kaybolur");
    }

    // ── Onaylı dekont ve banka satırı (S2b; tasarım §2.3): senkron dört alanı ezmez ─────

    /// <summary>Satırı doğrudan yazar (WPF'in göndermediği yollar: panel onayı, banka onayı). Değerler üretilir; banka satırı
    /// tasarım §2.3'ün biçiminde (adsız, PDF'siz, referansı <c>bank:</c>).</summary>
    private async Task<Payment> SeedPaymentAsync(Guid licenseId, PaymentStatus status, PaymentSource source = PaymentSource.Receipt)
    {
        var bank = source == PaymentSource.Bank;
        var at = Settled;
        var txId = Guid.NewGuid();
        var p = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Source = source, Status = status,
            PayerName = bank ? "" : $"p-{Guid.NewGuid():N}", Amount = Random.Shared.Next(100, 10_000), PaidAt = at.AddHours(-1),
            ReferansNo = bank ? $"bank:{txId:N}:1" : $"r-{Guid.NewGuid():N}",
            PdfHash = bank ? null : $"{Guid.NewGuid():N}{Guid.NewGuid():N}", BankTransactionId = bank ? txId : null,
            ApprovedAt = status == PaymentStatus.Approved ? at : null, RejectedAt = status == PaymentStatus.Rejected ? at : null,
            CreatedAt = at, UpdatedAt = at,
        };
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Payments.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>WPF'in yeniden gönderimi: aynı Id ve referans, dört alan yeni üretilmiş değerlerle, kargo yönergesi "hold".</summary>
    private sealed record PushedPayment(Guid Id, string PayerName, decimal Amount, DateTimeOffset PaidAt, string ReferansNo,
        string PdfHash, string ShipmentDirective);

    private static PushedPayment Repush(Payment p) => new(p.Id, $"n-{Guid.NewGuid():N}", p.Amount + 1m, p.PaidAt.AddMinutes(-7),
        p.ReferansNo, $"{Guid.NewGuid():N}{Guid.NewGuid():N}", "hold");

    private static (string PayerName, decimal Amount, DateTimeOffset PaidAt, string? PdfHash) Fields(Payment p)
        => (p.PayerName, p.Amount, p.PaidAt, p.PdfHash);

    private async Task<Dictionary<Guid, Payment>> StoredAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Payments.AsNoTracking().Where(p => p.LicenseId == licenseId).ToDictionaryAsync(p => p.Id);
    }

    [Fact]
    public async Task Sync_onayli_dekontun_dort_alanini_ezmez_yonergeyi_gunceller()
    {
        // Onaylı dekontun adı, tutarı, tarihi ve PDF özeti onaylanmış kayıttır (tasarım §2.3): WPF'in sonraki gönderimi onları ezmez.
        // Kargo yönergesi ve UpdatedAt bugünkü gibi yazılır (plan Karar 12), yanıt sunucunun durumunu yankılar. Reddedilen ve bekleyen
        // dekont bugünkü gibi güncellenir.
        var (client, _, licenseId) = await SetupAsync();
        var approved = await SeedPaymentAsync(licenseId, PaymentStatus.Approved);
        var rejected = await SeedPaymentAsync(licenseId, PaymentStatus.Rejected);
        var pending = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);
        var pushed = new[] { approved, rejected, pending }.Select(Repush).ToArray();

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/payments/sync", new { payments = pushed });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>())!.Single(d => d.Id == approved.Id).Status
            .Should().Be("approved");
        var stored = await StoredAsync(licenseId);
        Fields(stored[approved.Id]).Should().Be(Fields(approved), "onaylı dekontun dört alanı ezilmez");
        stored[approved.Id].ShipmentDirective.Should().Be(ShipmentDirective.Hold, "yönerge bugünkü gibi güncellenir");
        stored[approved.Id].UpdatedAt.Should().BeAfter(approved.UpdatedAt, "UpdatedAt bugünkü gibi ilerler (payments/since imleci)");
        foreach (var (seeded, sent) in new[] { (rejected, pushed[1]), (pending, pushed[2]) })
        {
            Fields(stored[seeded.Id]).Should().Be((sent.PayerName, sent.Amount, sent.PaidAt, (string?)sent.PdfHash),
                $"{seeded.Status} dekont bugünkü gibi güncellenir");
            stored[seeded.Id].ShipmentDirective.Should().Be(ShipmentDirective.Hold);
        }
    }

    [Fact]
    public async Task Sync_banka_satirinin_alanlarini_ezmez()
    {
        // Banka onayının satırı (Source = Bank; S3b yazar, okuyucu önce) hareketin tutarını ve anını taşır, adı ve PDF'i yoktur
        // (tasarım §2.3). WPF onu payments/since'ten alır; yeniden gönderirse dört alan ezilmez. Geri alınan banka onayı Rejected kalır:
        // onu durum değil kaynak korur. Yönerge bugünkü gibi yazılır (plan Karar 12, 14).
        var (client, _, licenseId) = await SetupAsync();
        var approved = await SeedPaymentAsync(licenseId, PaymentStatus.Approved, PaymentSource.Bank);
        var reverted = await SeedPaymentAsync(licenseId, PaymentStatus.Rejected, PaymentSource.Bank);

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/payments/sync",
            new { payments = new[] { approved, reverted }.Select(Repush).ToArray() });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await StoredAsync(licenseId);
        foreach (var seeded in new[] { approved, reverted })
        {
            Fields(stored[seeded.Id]).Should().Be(Fields(seeded), $"{seeded.Status} banka satırının dört alanı ezilmez");
            stored[seeded.Id].ShipmentDirective.Should().Be(ShipmentDirective.Hold, "yönerge bugünkü gibi");
        }
    }
}
