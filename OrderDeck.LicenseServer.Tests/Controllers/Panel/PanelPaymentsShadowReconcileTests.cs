using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using DomainShopper = OrderDeck.LicenseServer.Domain.Shopper;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>Dekont onayı gölge bağdaştırmayı GECİKMELİ zamanlar, kendisi koşturmaz: onay yolu eşleştirmeyle yavaşlamaz ve
/// onun hatasıyla düşmez; iş dekontun kendi hareketinin çekilmesini bekler (bkz. <see cref="PaymentMatchReconcileJob.ApprovalDelay"/>).
/// Ret hiçbir şey zamanlamaz.</summary>
public sealed class PanelPaymentsShadowReconcileTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelPaymentsShadowReconcileTests(ApiFactory factory) => _factory = factory;

    private sealed class ThrowingJobClient : IBackgroundJobClient
    {
        public string Create(Job job, IState state) => throw new InvalidOperationException("kuyruk arızası benzetimi");
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    /// <summary>Kuyruğa atmada patlayan fabrika. <c>CustomerAuthHelper</c> <see cref="ApiFactory"/> istediği için türetilir
    /// (<c>WithWebHostBuilder</c> taban tipi döndürür).</summary>
    private sealed class ThrowingJobsApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IBackgroundJobClient>();
                s.AddSingleton<IBackgroundJobClient, ThrowingJobClient>();
            });
        }
    }

    /// <summary>Onay kaydı commit edildikten hemen sonra istemci kopar: onayı yazan SaveChanges tamamlanınca istemcinin
    /// isteğini iptal eder (TestServer bunu isteğin <c>RequestAborted</c>'ına taşır). Bir kez.</summary>
    private sealed class DisconnectAfterApprovalCommit : SaveChangesInterceptor
    {
        public Guid PaymentId { get; set; }
        public CancellationTokenSource? Client { get; set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Client is { } client && eventData.Context!.ChangeTracker.Entries<Payment>()
                    .Any(e => e.Entity.Id == PaymentId && e.Entity.Status == PaymentStatus.Approved))
            {
                Client = null;
                client.Cancel();
            }
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class DisconnectingApiFactory : ApiFactory
    {
        public DisconnectAfterApprovalCommit Disconnect { get; } = new();
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Disconnect);
    }

    /// <param name="withShopper">Dekont bir shopper'dan: onay sonrası etiket adımı telefonu istek jetonuyla sorgular.</param>
    private static async Task<(HttpClient Client, Guid PaymentId)> SeedAsync(ApiFactory factory, bool withShopper = false)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = "LDK-REC-" + Guid.NewGuid().ToString("N"), CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        DomainShopper? shopper = withShopper
            ? new DomainShopper
            {
                Id = Guid.NewGuid(), FullName = "Test Payer", Phone = $"+9055{Random.Shared.Next(10000000, 99999999)}",
                PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-", CreatedAt = now, UpdatedAt = now,
            }
            : null;
        var payment = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ShopperId = shopper?.Id, PayerName = "Test Payer", Amount = 250.50m,
            PaidAt = now.AddHours(-1), ReferansNo = Guid.NewGuid().ToString("N"), Status = PaymentStatus.Pending, CreatedAt = now,
            UpdatedAt = now,
        };
        if (shopper is not null) db.Shoppers.Add(shopper);
        db.Licenses.Add(license);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return (client, payment.Id);
    }

    private static int ReconcileEnqueueCount(ApiFactory factory, Guid paymentId)
        => factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi().EnqueuedJobs("default", 0, 1000).Count(j =>
            j.Value.Job.Type == typeof(PaymentMatchReconcileJob) && j.Value.Job.Args.Contains((object)paymentId));

    private static List<ScheduledJobDto> ReconcileSchedules(ApiFactory factory, Guid paymentId)
        => factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi().ScheduledJobs(0, 1000)
            .Where(j => j.Value.Job.Type == typeof(PaymentMatchReconcileJob) && j.Value.Job.Args.Contains((object)paymentId))
            .Select(j => j.Value).ToList();

    [Fact]
    public async Task Onay_golge_bagdastirma_isini_gecikmeli_bir_kez_zamanlar()
    {
        var (client, paymentId) = await SeedAsync(_factory);
        var before = DateTime.UtcNow;

        var resp = await client.PostAsync($"/api/panel/payments/{paymentId}/approve", null);
        var again = await client.PostAsync($"/api/panel/payments/{paymentId}/approve", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var scheduled = ReconcileSchedules(_factory, paymentId);
        scheduled.Should().ContainSingle("karar yalnız kazanan onayda bir kez zamanlanır");
        scheduled[0].EnqueueAt.Should().BeOnOrAfter(before + PaymentMatchReconcileJob.ApprovalDelay,
            "dekontun kendi hareketi onay anında çoğu zaman henüz çekilmemiştir");
        scheduled[0].EnqueueAt.Should().BeBefore(DateTime.UtcNow + PaymentMatchReconcileJob.ApprovalDelay + TimeSpan.FromMinutes(1));
        ReconcileEnqueueCount(_factory, paymentId).Should().Be(0, "onay anında koşmaz");
    }

    [Fact]
    public async Task Ret_golge_bagdastirma_isini_zamanlamaz()
    {
        var (client, paymentId) = await SeedAsync(_factory);

        var resp = await client.PostAsJsonAsync($"/api/panel/payments/{paymentId}/reject", new { reason = "okunmuyor" });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ReconcileSchedules(_factory, paymentId).Should().BeEmpty("ret öğretmez ve bağlanmaz");
        ReconcileEnqueueCount(_factory, paymentId).Should().Be(0, "ret öğretmez ve bağlanmaz");
    }

    [Fact]
    public async Task Kuyruk_arizasi_onayi_dusurmez()
    {
        await using var factory = new ThrowingJobsApiFactory();
        var (client, paymentId) = await SeedAsync(factory);

        var resp = await client.PostAsync($"/api/panel/payments/{paymentId}/approve", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent, "onay kaydedildi; ölçüm kuyruğu onu geri almaz");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId)).Status.Should().Be(PaymentStatus.Approved);
    }

    [Fact]
    public async Task Istemci_onay_kaydindan_hemen_sonra_koparsa_bagdastirma_yine_bir_kez_zamanlanir()
    {
        // Onay commit edildi; istemci hemen ardından koptu. Etiket adımının telefon sorgusu istek jetonunu kullanır ve iptali
        // görür. Zamanlama o adımlara bağlı kalsaydı onay Faz 2 ölçümünden sessizce düşerdi (ne iş ne gap ne günlük).
        await using var factory = new DisconnectingApiFactory();
        var (client, paymentId) = await SeedAsync(factory, withShopper: true);
        using var disconnect = new CancellationTokenSource();
        factory.Disconnect.PaymentId = paymentId;
        factory.Disconnect.Client = disconnect;

        var act = () => client.PostAsync($"/api/panel/payments/{paymentId}/approve", null, disconnect.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("istemci isteği iptal etti");
        factory.Disconnect.Client.Should().BeNull("onay kaydı commit edildi, ardından istemci koptu");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId)).Status.Should().Be(PaymentStatus.Approved);
        }
        ReconcileSchedules(factory, paymentId).Should().ContainSingle("ölçüm etiket ve bildirim adımlarının kaderine bağlı değil");
        ReconcileEnqueueCount(factory, paymentId).Should().Be(0, "onay anında koşmaz");
    }
}
