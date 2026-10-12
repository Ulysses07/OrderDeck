using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

/// <summary>
/// Fiyat düzeltmesinin ödeme tarafı (K5): teslim edilmiş isteğinin tutarı yeni toplamdan farklı olanlar
/// KİŞİ düzeyinde listelenir (#506), ödemesi alınmışa otomatik bir şey gitmez, "düzeltilmiş isteği gönder"
/// ek soru gerektireni atlar.
/// </summary>
public sealed class CorrectedPaymentRequestsTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly LabelService _labels;
    private readonly LabelRepository _labelRepo;
    private readonly CustomerRepository _customers;
    private readonly PaymentJobRepository _jobs;
    private readonly ShipmentRepository _shipments;

    public CorrectedPaymentRequestsTests()
    {
        new MigrationRunner(_db).Run();
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);
        new SessionRepository(_db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
        _customers = new CustomerRepository(_db);
        _labelRepo = new LabelRepository(_db);
        _shipments = new ShipmentRepository(_db);
        _jobs = new PaymentJobRepository(_db);
        _labels = new LabelService(_labelRepo,
            new CustomerService(_customers, new SessionRepository(_db), _labelRepo, clock.Object), _db, clock.Object,
            _shipments);
    }

    public void Dispose() => _db.Dispose();

    private Label Add(string platform, string user, decimal price, string code = "A12")
        => _labels.Add("s1", new ChatMessage(Guid.NewGuid().ToString("N"), platform, null, user, user, null,
            $"{user} aldım", 1000, Array.Empty<string>()), price, code);

    private CorrectedPaymentRequests Sut(CorrectedPaymentRequests.RequestPayment? request = null)
        => new(_customers, _labelRepo, _jobs, () => new AppSettings(),
            request ?? ((_, _, _, _, _) => throw new InvalidOperationException("gönderim beklenmiyordu")));

    /// <summary>Kişiye bu kapsamda teslim edilmiş (mesajı ulaşmış) bir istek: sonucu kesin (bakiye yok)
    /// ve teslim kaydı yazılmış iş.</summary>
    private PaymentJob Delivered(string ownerId, string scope, decimal net)
    {
        var job = _jobs.FindOrCreate(ownerId, scope, net);
        _jobs.MarkNoBalance(job.Id, null, job.Revision).Should().BeTrue();
        _jobs.MarkDelivered(job.Id, net);
        return _jobs.Get(job.Id)!;
    }

    private (Label A1, Label A2, Label B, PaymentPerson Person) GroupedPersonWithRequest()
    {
        var a1 = Add("instagram", "@ayse", 250m);
        var a2 = Add("instagram", "@ayse", 250m);
        var b = Add("tiktok", "@ayse_tt", 100m, "B7");
        _labels.MarkPrintedAndRecord(new[] { a1.Id, a2.Id, b.Id });
        _customers.MergeIntoGroup(new[] { a1.CustomerId, b.CustomerId });
        var person = _customers.GetPaymentPerson(a1.CustomerId);
        Delivered(person.OwnerId!, "session:s1", 600m);
        return (a1, a2, b, person);
    }

    [Fact]
    public void Changed_request_is_listed_once_per_person_with_the_person_total()
    {
        var (a1, a2, _, person) = GroupedPersonWithRequest();
        var result = _labels.CorrectPrices(new[] { a1.Id, a2.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.RequestedPersons.Should().Be(1);
        var row = plan.ToResend.Should().ContainSingle("iki satır tek kişi — kişi düzeyi (#506)").Subject;
        row.ScopeKey.Should().Be("session:s1");
        row.PriorAmount.Should().Be(600m);
        row.NewTotal.Should().Be(510m, "kişinin yayın toplamı: 2 × 205 + 100 (öbür satırı dahil)");
        person.MemberIds.Should().Contain(row.CustomerId);
        plan.Paid.Should().BeEmpty();
    }

    [Fact]
    public void Unchanged_total_is_not_listed()
    {
        var (a1, _, _, _) = GroupedPersonWithRequest();

        // Düzeltilmemiş (fark 0) — istek hâlâ doğru tutarda.
        var plan = Sut().Plan("s1", new[] { (_labelRepo.GetById(a1.Id)!, 0m) });

        plan.RequestedPersons.Should().Be(1);
        plan.ToResend.Should().BeEmpty();
    }

    [Fact]
    public void Person_without_a_delivered_request_is_not_listed()
    {
        var l = Add("instagram", "@yeni", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.RequestedPersons.Should().Be(0);
        plan.ToResend.Should().BeEmpty("isteği gitmemiş kişinin sıradaki isteği zaten yeni tutarı ister");
    }

    [Fact]
    public void Dekont_paid_person_gets_nothing_automatic_and_shows_the_difference()
    {
        var l = Add("instagram", "@odedi", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, () => new AppSettings());
        var open = shipmentSvc.GetOrCreateOpenShipment(l.CustomerId);
        shipmentSvc.AttachLabels(open.Id, new[] { l.Id });   // dekont kaydı etiketleri dosyaya bağlar
        Delivered(l.CustomerId, "session:s1", 250m);
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.ToResend.Should().BeEmpty("ödemesi alınmış kişiye otomatik istek gitmez");
        var paid = plan.Paid.Should().ContainSingle().Subject;
        paid.ByDekont.Should().BeTrue();
        paid.Delta.Should().Be(-45m);
    }

    [Fact]
    public void Balance_paid_person_gets_nothing_automatic()
    {
        var l = Add("instagram", "@bakiye", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var job = _jobs.FindOrCreate(l.CustomerId, "session:s1", 250m);
        var key = Guid.NewGuid();
        _jobs.BeginApply(job.Id, key).Should().BeTrue();
        _jobs.MarkApplied(job.Id, key, job.Revision, 50m).Should().BeTrue();
        _jobs.MarkDelivered(job.Id, 200m);
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.ToResend.Should().BeEmpty();
        plan.Paid.Should().ContainSingle().Which.ByBalance.Should().BeTrue();
    }

    [Fact]
    public void Customers_with_delivered_request_are_flagged_person_wide()
    {
        var (a1, _, b, _) = GroupedPersonWithRequest();
        var other = Add("instagram", "@baska", 100m);

        var flagged = Sut().CustomersWithDeliveredRequest("s1", new[] { a1.CustomerId, b.CustomerId, other.CustomerId });

        flagged.Should().BeEquivalentTo(new[] { a1.CustomerId, b.CustomerId });
    }

    // ── Düzeltilmiş isteği gönder ─────────────────────────────────────

    private static CorrectedRequestCandidate Candidate(string customerId, decimal prior = 600m)
        => new(customerId, "Ayşe", "session:s1", prior, null, false, 510m);

    private static PaymentRequestOutcome Asked(string scope, decimal amount)
        => new(PaymentRequestResult.AlreadyRequested,
            new PaymentPrompt(PersonScopePromptKind.Resend, scope,
                new[] { new PriorPaymentRequest(amount, null) }, "belirtec", NewTotal: 510m));

    [Fact]
    public async Task Resend_confirms_the_listed_request_once_and_sends_through_the_payment_flow()
    {
        var (_, _, _, person) = GroupedPersonWithRequest();
        var calls = new List<(IReadOnlySet<string>? Tokens, decimal? Total)>();
        var sut = Sut((c, total, _, scope, tokens) =>
        {
            calls.Add((tokens, total(_customers.GetPaymentPerson(c.Id))));
            return Task.FromResult(tokens is null ? Asked(scope, 600m) : new PaymentRequestOutcome(PaymentRequestResult.Sent));
        });

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        outcomes.Should().ContainSingle().Which.Status.Should().Be(ResendStatus.Sent);
        calls.Should().HaveCount(2);
        calls[0].Tokens.Should().BeNull("önce soru gelir");
        calls[1].Tokens.Should().BeEquivalentTo(new[] { "belirtec" }, "operatörün tek onayı listedeki isteğin sorusu");
        calls.Should().OnlyContain(c => c.Total == 600m, "kişinin yayın toplamı (düzeltilmemiş burada: 250+250+100)");
    }

    [Theory]
    [InlineData(PaymentRequestResult.MergeRequired)]
    [InlineData(PaymentRequestResult.PhoneRequired)]
    [InlineData(PaymentRequestResult.BalanceUncertain)]
    [InlineData(PaymentRequestResult.PaymentInProgress)]
    [InlineData(PaymentRequestResult.ListStale)]
    public async Task Resend_skips_whoever_needs_an_extra_question(PaymentRequestResult result)
    {
        var (_, _, _, person) = GroupedPersonWithRequest();
        var calls = 0;
        var sut = Sut((_, _, _, scope, _) =>
        {
            calls++;
            return Task.FromResult(result == PaymentRequestResult.MergeRequired
                ? new PaymentRequestOutcome(result, new PaymentPrompt(PersonScopePromptKind.Merge, scope,
                    Array.Empty<PriorPaymentRequest>(), "birlestir"))
                : new PaymentRequestOutcome(result));
        });

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        outcomes.Should().ContainSingle().Which.Status.Should().Be(ResendStatus.Manual, "elle bakılmalı");
        calls.Should().Be(1, "ek soru onaylanmaz — ikinci çağrı yok");
    }

    [Fact]
    public async Task Resend_does_not_confirm_a_question_about_a_different_request()
    {
        var (_, _, _, person) = GroupedPersonWithRequest();
        var calls = 0;
        var sut = Sut((_, _, _, scope, _) =>
        {
            calls++;
            return Task.FromResult(Asked(scope, 700m));   // listede 600 gösterildi; arada başka istek gitmiş
        });

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        outcomes.Single().Status.Should().Be(ResendStatus.Manual);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Resend_against_the_real_payment_service_revises_and_sends_the_new_amount()
    {
        // Uçtan uca: gerçek PaymentRequestService (lisanssız — bakiye akışı kapalı, iş açılmaz) wa.me açar.
        var l = Add("instagram", "@uctan", 250m);
        _customers.UpdatePhone(l.CustomerId, TestPhone.NewE164());
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        _labels.CorrectPrices(new[] { l.Id }, 205m);
        var launcher = new OrderDeck.Tests.Fakes.FakeUrlLauncher();
        var settingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(settingsPath);
            var s = new AppSettings();
            s.Payment.WhatsAppMessageTemplate = "Ödeme: {tutar} TL";
            store.Save(s);
            var payments = new PaymentRequestService(store, new WhatsAppMessageBuilder(), launcher,
                PaymentRequestServiceTestHelpers.StubApiClient(), new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
                _jobs, customers: _customers);
            var sut = new CorrectedPaymentRequests(_customers, _labelRepo, _jobs, store, payments);

            var outcomes = await sut.ResendAsync("s1", DateTime.Now,
                new[] { new CorrectedRequestCandidate(l.CustomerId, "Uçtan", "session:s1", 250m, null, false, 205m) });

            outcomes.Single().Status.Should().Be(ResendStatus.Sent);
            launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("205");
        }
        finally
        {
            if (System.IO.File.Exists(settingsPath)) System.IO.File.Delete(settingsPath);
        }
    }
}
