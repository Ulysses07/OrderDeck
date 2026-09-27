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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

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

    private static async Task<(HttpClient Client, Guid PaymentId)> SeedAsync(ApiFactory factory)
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
        var payment = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, PayerName = "Test Payer", Amount = 250.50m, PaidAt = now.AddHours(-1),
            ReferansNo = Guid.NewGuid().ToString("N"), Status = PaymentStatus.Pending, CreatedAt = now, UpdatedAt = now,
        };
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
}
