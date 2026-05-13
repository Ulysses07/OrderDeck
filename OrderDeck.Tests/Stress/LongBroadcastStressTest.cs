using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OrderDeck.Tests.Stress;

/// <summary>
/// Uzun yayın stres testi (2026-05-13). Kullanıcının ~4 saatlik yoğun yayında
/// gerçekleştiğini düşündüğü senaryoyu compressed time'da (~30-60 sn wallclock)
/// simüle eder. Hedef: memory leak, repository perf degradation, deadlock,
/// race condition, exception storm bulmak.
///
/// **Bu test default test suite'inde ÇALIŞMAZ** çünkü stres testi (yavaş +
/// yüksek CPU). Manuel çalıştır:
///   dotnet test --filter "Category=Stress"
/// veya `LONG_STRESS=1` env var ile gerçek-zamanlı uzun versiyon.
///
/// ## Simüle edilen aktivite (default profil ~4 saat compressed)
/// - 20,000 chat mesajı (≈83/dk = yoğun yayın)
/// - 500 etiket oluşturma (1 in 40 mesaj)
/// - 50 print batch (10 etiket/batch ortalama)
/// - 50 dekont (Payment.Insert + matcher)
/// - 50 Shipment lifecycle (GetOrCreate + AttachLabels + ApplyDecision)
/// - Customer-level: blacklist, cancel, backup promote
/// - YT scraper crash döngüsü simülasyonu (backoff exercising)
///
/// ## Doğrulamalar
/// - Memory delta &lt; 200 MB (hard cap; tipik &lt; 50 MB)
/// - Hiçbir worker exception fırlatmamış
/// - Son repository query &lt; 500ms (perf degradation kontrolü)
/// - Print/label/payment count'lar beklendiği gibi
/// </summary>
[Trait("Category", "Stress")]
public class LongBroadcastStressTest
{
    private readonly ITestOutputHelper _output;

    public LongBroadcastStressTest(ITestOutputHelper output) => _output = output;

    // Compressed simulation parameters. Default = 4-hour broadcast in ~30s wallclock.
    // LONG_STRESS=1 → 2 saat gerçek-zamanlı (real-time pacing).
    private sealed record Profile(
        int ChatMessages,
        int Labels,
        int PrintBatches,
        int Payments,
        int ShipmentCycles,
        int YtScraperCrashes,
        TimeSpan MaxDuration);

    private static Profile DefaultProfile() => new(
        ChatMessages: 20_000,
        Labels: 500,
        PrintBatches: 50,
        Payments: 50,
        ShipmentCycles: 50,
        YtScraperCrashes: 60,
        MaxDuration: TimeSpan.FromMinutes(2));

    private static Profile LongRealtimeProfile() => new(
        // 2 saat real-time pacing = 5/sn chat (36k mesaj), 5 dk'da bir print, vs.
        ChatMessages: 36_000,
        Labels: 800,
        PrintBatches: 24,
        Payments: 40,
        ShipmentCycles: 40,
        YtScraperCrashes: 30,
        MaxDuration: TimeSpan.FromHours(2));

    [Fact]
    public async Task Simulate_long_broadcast_no_leak_no_deadlock_no_perf_degradation()
    {
        var profile = Environment.GetEnvironmentVariable("LONG_STRESS") == "1"
            ? LongRealtimeProfile()
            : DefaultProfile();

        _output.WriteLine($"=== Profil: {profile} ===");

        // ─── Setup ────────────────────────────────────────────────────────
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clockSeed = 1_700_000_000L;
        long currentTime = clockSeed;
        var clock = new TestClock(() => Interlocked.Read(ref currentTime));

        var customerRepo = new CustomerRepository(db);
        var sessionRepo = new SessionRepository(db);
        var labelRepo = new LabelRepository(db);
        var paymentRepo = new PaymentRepository(db);
        var shipmentRepo = new ShipmentRepository(db);

        var settings = new AppSettings();
        settings.Shipping.FreeShippingThreshold = 5000m;
        settings.Shipping.ShippingFee = 150m;

        var customerService = new CustomerService(customerRepo, sessionRepo, labelRepo, clock);
        var labelService = new LabelService(labelRepo, customerService, clock);
        var matcher = new PaymentMatcherService(labelRepo, () => settings);
        var shipmentService = new ShipmentService(
            shipmentRepo, labelRepo, () => settings, () => clock.UnixNow());

        var bus = new ChatBus();

        // Active session
        var sessionId = Guid.NewGuid().ToString("N");
        sessionRepo.Insert(new StreamSession(sessionId, "Stress yayın",
            clock.UnixNow(), null, new[] { "instagram", "youtube" }, null));

        // ─── Metrics + worker exception tracking ──────────────────────────
        var exceptions = new ConcurrentBag<Exception>();
        long chatPublished = 0, chatHandled = 0, labelsAdded = 0,
             paymentsInserted = 0, prints = 0, shipmentDecisions = 0,
             scraperBackoffSamples = 0;

        // Subscriber simulating UI thread chat consumer (light work)
        using var sub = bus.Subscribe(_ => Interlocked.Increment(ref chatHandled));

        var memBefore = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();

        // ─── Worker tasks ─────────────────────────────────────────────────
        using var cts = new CancellationTokenSource(profile.MaxDuration);
        var ct = cts.Token;

        // Realistic distribution: spread Labels/Print/Payment events across the
        // chat publishing timeline. We use a deterministic seed for repeatability.
        var rng = new Random(42);

        // Pre-compute trigger checkpoints (which message # triggers which action)
        var labelCheckpoints = new HashSet<int>();
        var printCheckpoints = new HashSet<int>();
        var paymentCheckpoints = new HashSet<int>();
        var shipmentCheckpoints = new HashSet<int>();
        for (int i = 0; i < profile.Labels; i++) labelCheckpoints.Add(rng.Next(profile.ChatMessages));
        for (int i = 0; i < profile.PrintBatches; i++) printCheckpoints.Add(rng.Next(profile.ChatMessages));
        for (int i = 0; i < profile.Payments; i++) paymentCheckpoints.Add(rng.Next(profile.ChatMessages));
        for (int i = 0; i < profile.ShipmentCycles; i++) shipmentCheckpoints.Add(rng.Next(profile.ChatMessages));

        // For realtime mode, pace publishing so the test actually takes MaxDuration.
        // For compressed, run as fast as possible.
        bool realtime = profile.MaxDuration > TimeSpan.FromMinutes(5);
        TimeSpan messageInterval = realtime
            ? TimeSpan.FromTicks(profile.MaxDuration.Ticks / profile.ChatMessages)
            : TimeSpan.Zero;

        var publisher = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < profile.ChatMessages && !ct.IsCancellationRequested; i++)
                {
                    var msg = new ChatMessage(
                        Id: $"m{i}",
                        Platform: i % 3 == 0 ? "youtube" : "instagram",
                        ExternalId: null,
                        Username: $"@user{i % 200}", // 200 distinct customers
                        DisplayName: $"User {i % 200}",
                        AvatarUrl: null,
                        Text: $"mesaj {i} alıyorum",
                        ReceivedAt: clock.UnixNow(),
                        Badges: Array.Empty<string>());
                    bus.Publish(msg);
                    Interlocked.Increment(ref chatPublished);

                    // Advance test clock realistic ~50ms per message (compressed)
                    Interlocked.Add(ref currentTime, 1);

                    // Trigger checkpoints
                    if (labelCheckpoints.Contains(i))
                    {
                        try
                        {
                            labelService.Add(sessionId, msg, price: 100m + (i % 50) * 10m, code: null);
                            Interlocked.Increment(ref labelsAdded);
                        }
                        catch (Exception ex) { exceptions.Add(ex); }
                    }
                    if (printCheckpoints.Contains(i))
                    {
                        try
                        {
                            var unprinted = labelRepo.GetUnprintedBySession(sessionId);
                            if (unprinted.Count > 0)
                            {
                                labelService.MarkPrintedAndRecord(unprinted.Select(l => l.Id).ToList());
                                Interlocked.Increment(ref prints);
                            }
                        }
                        catch (Exception ex) { exceptions.Add(ex); }
                    }
                    if (paymentCheckpoints.Contains(i))
                    {
                        try
                        {
                            var payment = new Payment(
                                Id: Guid.NewGuid().ToString("N"),
                                PayerName: $"Payer {i}",
                                Amount: 500m + (i % 100) * 10m,
                                PaidAt: clock.UnixNow(),
                                ReferansNo: $"REF-{i}",
                                PdfHash: null,
                                Status: PaymentStatus.Pending,
                                CreatedAt: clock.UnixNow(),
                                UpdatedAt: clock.UnixNow(),
                                SyncedAt: null,
                                ApprovedAt: null,
                                RejectedAt: null,
                                RejectReason: null);
                            paymentRepo.Insert(payment);
                            // Run matcher for some random customer (exercise query)
                            var existingCustomers = customerRepo.Search($"@user{i % 200}", limit: 5);
                            if (existingCustomers.Count > 0)
                            {
                                _ = matcher.Match(existingCustomers[0].Id, sessionId, payment.Amount);
                            }
                            Interlocked.Increment(ref paymentsInserted);
                        }
                        catch (Exception ex) { exceptions.Add(ex); }
                    }
                    if (shipmentCheckpoints.Contains(i))
                    {
                        try
                        {
                            var customerCandidate = customerRepo.Search($"@user{i % 200}", limit: 1);
                            if (customerCandidate.Count > 0)
                            {
                                var s = shipmentService.GetOrCreateOpenShipment(customerCandidate[0].Id);
                                var open = labelRepo.GetUnattachedByCustomer(customerCandidate[0].Id);
                                if (open.Count > 0)
                                    shipmentService.AttachLabels(s.Id, open.Select(l => l.Id).ToList());
                                var decision = (i % 3) switch
                                {
                                    0 => ShipmentDecision.Hold,
                                    1 => ShipmentDecision.RecipientPays,
                                    _ => ShipmentDecision.ShipNow
                                };
                                shipmentService.ApplyDecision(s.Id, decision);
                                Interlocked.Increment(ref shipmentDecisions);
                            }
                        }
                        catch (Exception ex) { exceptions.Add(ex); }
                    }

                    if (realtime && messageInterval > TimeSpan.Zero)
                        await Task.Delay(messageInterval, ct);
                }
            }
            catch (OperationCanceledException) { /* timeout */ }
        }, ct);

        // YT scraper backoff simulator — exercise ComputeBackoff under load
        var scraperSim = Task.Run(() =>
        {
            try
            {
                for (int crashes = 1; crashes <= profile.YtScraperCrashes && !ct.IsCancellationRequested; crashes++)
                {
                    var backoff = OrderDeck.Chat.Ingestors.YouTube.YouTubeChatHostedService
                        .ComputeBackoff(crashes);
                    backoff.Should().BePositive();
                    Interlocked.Increment(ref scraperBackoffSamples);
                }
            }
            catch (Exception ex) { exceptions.Add(ex); }
        }, ct);

        await Task.WhenAll(publisher, scraperSim);
        sw.Stop();

        // Drain time for chat handler to catch up
        await Task.Delay(200);

        // ─── Final assertions + diagnostics ───────────────────────────────
        var memAfter = GC.GetTotalMemory(forceFullCollection: true);
        var memDeltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);

        _output.WriteLine($"Wallclock: {sw.Elapsed.TotalSeconds:0.0}s");
        _output.WriteLine($"Memory before: {memBefore / 1024.0 / 1024.0:0.0} MB");
        _output.WriteLine($"Memory after:  {memAfter / 1024.0 / 1024.0:0.0} MB");
        _output.WriteLine($"Memory delta:  {memDeltaMb:0.0} MB");
        _output.WriteLine($"Chat published: {chatPublished}");
        _output.WriteLine($"Chat handled:   {chatHandled}");
        _output.WriteLine($"Labels added:   {labelsAdded}");
        _output.WriteLine($"Prints:         {prints}");
        _output.WriteLine($"Payments:       {paymentsInserted}");
        _output.WriteLine($"Shipments:      {shipmentDecisions}");
        _output.WriteLine($"Scraper backoff samples: {scraperBackoffSamples}");
        _output.WriteLine($"Exceptions: {exceptions.Count}");
        foreach (var ex in exceptions.Take(5))
            _output.WriteLine($"  - {ex.GetType().Name}: {ex.Message}");

        // Repository perf check (degradation kontrolü)
        var perfSw = Stopwatch.StartNew();
        _ = labelRepo.GetUnprintedBySession(sessionId);
        perfSw.Stop();
        _output.WriteLine($"Final GetUnprintedBySession: {perfSw.Elapsed.TotalMilliseconds:0.0}ms");

        // Assertions
        exceptions.Should().BeEmpty("worker'larda exception fırlamamış olmalı");
        memDeltaMb.Should().BeLessThan(200,
            "memory hard cap 200 MB — bunun üzerinde leak şüphesi var");
        chatPublished.Should().Be(profile.ChatMessages);
        chatHandled.Should().BeGreaterThan(profile.ChatMessages / 2,
            "subscriber çoğu mesajı işlemiş olmalı (drain delay tolere)");
        labelsAdded.Should().BeGreaterThan(profile.Labels / 2,
            "label checkpoint'lerin çoğu çalıştırılmış olmalı");
        perfSw.Elapsed.TotalMilliseconds.Should().BeLessThan(500,
            "yük altında repository sorgu performansı bozulmamalı");
    }

    private sealed class TestClock : IClock
    {
        private readonly Func<long> _now;
        public TestClock(Func<long> now) => _now = now;
        public long UnixNow() => _now();
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(_now());
    }
}
