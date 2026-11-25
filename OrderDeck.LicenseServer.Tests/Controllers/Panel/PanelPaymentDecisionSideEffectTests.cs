using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Push;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using DomainShopper = OrderDeck.LicenseServer.Domain.Shopper;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panelden verilen ödeme kararının (onay/ret) sırası, tek kazananı ve arıza yalıtımı. Sözleşme:
/// <list type="number">
/// <item>Karar <c>Payment.Status</c> jetonuyla kaydedilir. Karara bağlanmış ya da yarışı kaybetmiş istek 409
/// <c>not-pending</c> alır; hiçbir yan etki üretmez.</item>
/// <item>Kayıttan sonra, yalnız onayda: <see cref="PaymentMatchReconcileJob"/> gecikmeli zamanlanır.</item>
/// <item>Sonra müşterinin sohbetine olayın etiketi yazılır.</item>
/// <item>En son shopper'a bildirim gider.</item>
/// </list>
/// Kuyruk, etiket kaydı ve bildirim arızası yalnız uyarı yazar: karar yerinde kalır, sonraki adımlar çalışır.
/// <para>Sıra zamanlamayla değil durumla sınanır: zamanlama ve bildirim anlarında DB'de commit edilmiş olan okunur
/// (<see cref="Step"/>). Yarış da sıralı kurulur; <c>Payment.Status</c> jetonunu InMemory de denetler, gerçek SQL Server
/// yarışı <see cref="PanelPaymentDecisionConcurrencyTests"/>'tedir.</para>
/// </summary>
public sealed class PanelPaymentDecisionSideEffectTests
    : IClassFixture<PanelPaymentDecisionSideEffectTests.StepApiFactory>
{
    private const string ApprovedLabel = "onay etiketi";
    private const string RejectedLabel = "ret etiketi";

    private readonly StepApiFactory _factory;

    public PanelPaymentDecisionSideEffectTests(StepApiFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    /// <summary>Bir yan etki adımı ve o an DB'de commit edilmiş görünen karar ile sohbet etiketi.</summary>
    public sealed record Step(string Name, PaymentStatus Status, string? Label);

    /// <summary>Gölge bağdaştırmanın zamanlanmasını ve shopper bildirimini adım olarak kaydeden fabrika; ikisini ve kayıt
    /// anını test bozabilir.</summary>
    public sealed class StepApiFactory : ApiFactory
    {
        private readonly List<Step> _steps = new();
        private Guid _paymentId;
        private Guid _conversationId;

        public RaceSaveHook Save { get; } = new();
        public LogRecorderProvider Log { get; } = new();
        public Exception? ScheduleFailure { get; set; }
        public Exception? PushFailure { get; set; }

        public IReadOnlyList<Step> Steps
        {
            get { lock (_steps) return _steps.ToArray(); }
        }

        public void Reset()
        {
            lock (_steps) _steps.Clear();
            Save.Disarm();
            ScheduleFailure = null;
            PushFailure = null;
            Push.Clear();
        }

        public void Watch(Guid paymentId, Guid conversationId) => (_paymentId, _conversationId) = (paymentId, conversationId);

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Save);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(l => l.AddProvider(Log));
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IBackgroundJobClient>();
                s.AddSingleton<IBackgroundJobClient>(new StepJobClient(this));
                s.RemoveAll<INotificationSender>();
                s.AddSingleton<INotificationSender>(new StepPush(this));
            });
        }

        private void Observe(string name)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var status = db.Payments.AsNoTracking().Single(p => p.Id == _paymentId).Status;
            var label = db.WaConversationLabels.AsNoTracking()
                .Where(x => x.ConversationId == _conversationId).Select(x => x.WaLabel.Name).SingleOrDefault();
            lock (_steps) _steps.Add(new Step(name, status, label));
        }

        private sealed class StepJobClient(StepApiFactory owner) : IBackgroundJobClient
        {
            public string Create(Job job, IState state)
            {
                if (job.Type == typeof(PaymentMatchReconcileJob))
                {
                    owner.Observe("bağdaştırma");
                    if (owner.ScheduleFailure is { } failure) throw failure;
                }
                return Guid.NewGuid().ToString("N");
            }

            public bool ChangeState(string jobId, IState state, string expectedState) => true;
        }

        private sealed class StepPush(StepApiFactory owner) : INotificationSender
        {
            public Task SendToCustomerAsync(Guid customerId, string title, string body,
                IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default)
                => owner.Push.SendToCustomerAsync(customerId, title, body, data, ct);

            public Task SendToShoppersAsync(IReadOnlyCollection<Guid> shopperIds, string title, string body,
                IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default)
            {
                owner.Observe("bildirim");
                if (owner.PushFailure is { } failure) throw failure;
                return owner.Push.SendToShoppersAsync(shopperIds, title, body, data, ct);
            }
        }
    }

    private sealed record Seeded(HttpClient Client, string BroadcasterName, Guid LicenseId, Guid ShopperId, Guid PaymentId,
        Guid ConversationId);

    /// <summary>Shopper'dan gelmiş dekont, shopper'ın WhatsApp sohbeti ve iki karara ayrı etiket kuralı.</summary>
    private async Task<Seeded> SeedAsync(PaymentStatus status = PaymentStatus.Pending)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var phone = TestPhone.NewE164();
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = "LDK-DEC-" + Guid.NewGuid().ToString("N"), CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var shopper = new DomainShopper
        {
            Id = Guid.NewGuid(), FullName = "Test Payer", Phone = phone, PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-",
            CreatedAt = now, UpdatedAt = now,
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ShopperId = shopper.Id, PayerName = "Test Payer", Amount = 250.50m,
            PaidAt = now.AddHours(-1), ReferansNo = Guid.NewGuid().ToString("N"), Status = status, CreatedAt = now, UpdatedAt = now,
        };
        var conversation = new WaConversation
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, CustomerPhone = phone[1..], PhoneNumberId = "PNID_1", Status = "open",
            CreatedAt = now,
        };
        db.Licenses.Add(license);
        db.Shoppers.Add(shopper);
        db.Payments.Add(payment);
        db.WaConversations.Add(conversation);
        foreach (var (name, eventKey) in new[]
                 {
                     (ApprovedLabel, WaLabelEvent.PaymentApproved), (RejectedLabel, WaLabelEvent.PaymentRejected),
                 })
        {
            var label = new WaLabel
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, Name = name, Color = WaLabelColors.Palette[0], CreatedAt = now,
            };
            db.WaLabels.Add(label);
            db.WaLabelRules.Add(new WaLabelRule
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, EventKey = eventKey, WaLabelId = label.Id, CreatedAt = now,
            });
        }
        await db.SaveChangesAsync();

        var broadcasterName = (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId)).Name;
        _factory.Watch(payment.Id, conversation.Id);
        return new Seeded(client, broadcasterName, license.Id, shopper.Id, payment.Id, conversation.Id);
    }

    private static Task<HttpResponseMessage> DecideAsync(Seeded s, string action) => action == "approve"
        ? s.Client.PostAsync($"/api/panel/payments/{s.PaymentId}/approve", null)
        : s.Client.PostAsJsonAsync($"/api/panel/payments/{s.PaymentId}/reject", new { reason = "okunmuyor" });

    private async Task<(Payment Payment, string? Label)> ReadAsync(Seeded s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == s.PaymentId),
            await db.WaConversationLabels.AsNoTracking().Where(x => x.ConversationId == s.ConversationId)
                .Select(x => x.WaLabel.Name).SingleOrDefaultAsync());
    }

    /// <summary>Bu ödemenin adı geçen uyarılar (ortak günlükte ilgisiz satırlar da var). İstisna da tutulur: kuyruk
    /// uyarısının onu EKLEMEDİĞİ sınanır.</summary>
    private IEnumerable<(string Message, Exception? Exception)> WarningsFor(Guid paymentId) => _factory.Log.Entries
        .Where(e => e.Level == LogLevel.Warning && e.Message.Contains(paymentId.ToString(), StringComparison.Ordinal))
        .Select(e => (e.Message, e.Exception));

    private static async Task ShouldBeNotPendingAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("title").GetString().Should().Be("not-pending");
        problem.RootElement.GetProperty("detail").GetString().Should().Be("Bu ödeme zaten karara bağlanmış.");
    }

    [Fact]
    public async Task Onay_kaydedilir_sonra_bagdastirma_etiket_ve_bildirim_bu_sirayla_gelir()
    {
        var s = await SeedAsync();

        var resp = await DecideAsync(s, "approve");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Steps.Should().Equal(
            new Step("bağdaştırma", PaymentStatus.Approved, Label: null),
            new Step("bildirim", PaymentStatus.Approved, ApprovedLabel));
    }

    [Fact]
    public async Task Ret_kaydedilir_sonra_etiket_ve_bildirim_bu_sirayla_gelir_bagdastirma_yok()
    {
        var s = await SeedAsync();

        var resp = await DecideAsync(s, "reject");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Steps.Should().Equal(new Step("bildirim", PaymentStatus.Rejected, RejectedLabel));
    }

    /// <summary>Metinler burada harfi harfine yazılı: <c>ShopperOrderPaymentPushTests</c> gövdeyi üretimin kendi sabitiyle
    /// karşılaştırır, sabit değişse de geçerdi.</summary>
    [Theory]
    [InlineData("approve", "payment-approved", "Dekontun onaylandı.")]
    [InlineData("reject", "payment-rejected", "Dekontun reddedildi. Ayrıntıyı uygulamada görebilirsin.")]
    public async Task Bildirimin_metni_ve_verisi_sabittir(string action, string type, string body)
    {
        var s = await SeedAsync();

        var resp = await DecideAsync(s, action);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var push = _factory.Push.SentToShoppers.Should().ContainSingle().Which;
        push.ShopperIds.Should().Equal(s.ShopperId);
        push.Title.Should().Be(s.BroadcasterName);
        push.Body.Should().Be(body);
        push.Data.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["type"] = type, ["paymentId"] = s.PaymentId.ToString(), ["licenseId"] = s.LicenseId.ToString(),
        });
    }

    [Theory]
    [InlineData("approve", PaymentStatus.Approved)]
    [InlineData("approve", PaymentStatus.Rejected)]
    [InlineData("reject", PaymentStatus.Approved)]
    [InlineData("reject", PaymentStatus.Rejected)]
    public async Task Karara_baglanmis_odeme_yan_etki_uretmez(string action, PaymentStatus decided)
    {
        var s = await SeedAsync(decided);

        var resp = await DecideAsync(s, action);

        await ShouldBeNotPendingAsync(resp);
        _factory.Steps.Should().BeEmpty();
        var (payment, label) = await ReadAsync(s);
        payment.Status.Should().Be(decided);
        label.Should().BeNull();
    }

    /// <summary>Rakip karar, istek ödemeyi bekliyor okuduktan sonra ve kendi kararını kaydetmeden hemen önce yazar: bekliyor
    /// denetimi geçilmiştir, kaybettiren yalnız jetondur.</summary>
    [Theory]
    [InlineData("approve", PaymentStatus.Rejected)]
    [InlineData("reject", PaymentStatus.Approved)]
    public async Task Yarisi_kaybeden_karar_ayni_409_u_alir_ve_yan_etki_uretmez(string action, PaymentStatus rivalStatus)
    {
        var s = await SeedAsync();
        var savedDecision = false;
        _factory.Save.Once(async saving =>
        {
            savedDecision = saving.ChangeTracker.Entries<Payment>()
                .Any(e => e.Entity.Id == s.PaymentId && e.State == EntityState.Modified);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.Payments.SingleAsync(p => p.Id == s.PaymentId)).Status = rivalStatus;
            await db.SaveChangesAsync();
        });

        var resp = await DecideAsync(s, action);

        savedDecision.Should().BeTrue("rakip, kararın kendi kaydının önüne girdi");
        await ShouldBeNotPendingAsync(resp);
        _factory.Steps.Should().BeEmpty("etiket, bildirim ve bağdaştırma yalnız kazanandan gider");
        var (payment, label) = await ReadAsync(s);
        payment.Status.Should().Be(rivalStatus);
        (payment.ApprovedAt, payment.RejectedAt, payment.RejectReason).Should().Be((null, null, null), "kaybeden hiçbir alan yazmadı");
        label.Should().BeNull();
    }

    [Fact]
    public async Task Kuyruk_arizasi_yalniz_tur_adiyla_uyarilir_etiket_ve_bildirim_yine_gelir()
    {
        var s = await SeedAsync();
        _factory.ScheduleFailure = new InvalidOperationException("kuyruk arızası benzetimi");

        var resp = await DecideAsync(s, "approve");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Steps.Should().Equal(
            new Step("bağdaştırma", PaymentStatus.Approved, Label: null),
            new Step("bildirim", PaymentStatus.Approved, ApprovedLabel));
        WarningsFor(s.PaymentId).Should().Equal(
            ($"Gölge eşleştirme işi zamanlanamadı (ödeme {s.PaymentId}): InvalidOperationException", null));
    }

    [Fact]
    public async Task Etiket_kaydinin_arizasi_karari_ve_bildirimi_durdurmaz()
    {
        var s = await SeedAsync();
        // İlk kayıt karardır; düşen, onun ardındaki etiket kaydı.
        _factory.Save.Once(_ =>
        {
            _factory.Save.Once(_ => throw new InvalidOperationException("etiket kaydı arızası benzetimi"));
            return Task.CompletedTask;
        });

        var resp = await DecideAsync(s, "approve");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Steps.Should().Equal(
            new Step("bağdaştırma", PaymentStatus.Approved, Label: null),
            new Step("bildirim", PaymentStatus.Approved, Label: null));
    }

    [Theory]
    [InlineData("approve", PaymentStatus.Approved, ApprovedLabel)]
    [InlineData("reject", PaymentStatus.Rejected, RejectedLabel)]
    public async Task Bildirim_arizasi_istisnasiyla_uyarilir_karari_dusurmez(string action, PaymentStatus status, string label)
    {
        var s = await SeedAsync();
        var failure = new InvalidOperationException("bildirim arızası benzetimi");
        _factory.PushFailure = failure;

        var resp = await DecideAsync(s, action);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var (payment, written) = await ReadAsync(s);
        payment.Status.Should().Be(status);
        written.Should().Be(label);
        WarningsFor(s.PaymentId).Should().Equal(
            ($"Shopper payment decision push failed for payment={s.PaymentId}", failure));
    }
}
