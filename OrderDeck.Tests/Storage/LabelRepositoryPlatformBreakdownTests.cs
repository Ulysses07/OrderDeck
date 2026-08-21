using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// GetPlatformBreakdownBySession — yayın raporundaki platform kırılımı.
/// Kritik değişmez: kırılım satırlarının toplamı GetSessionTotals ile
/// birebir aynı olmalı (aynı filtre kuralları: iptal + tentative hariç).
/// </summary>
public class LabelRepositoryPlatformBreakdownTests
{
    private static (LabelService Svc, LabelRepository Labels, InMemorySqlite Db, string SessionId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 1000, null, new[] { "instagram", "tiktok" }, null));

        var customerRepo = new CustomerRepository(db);
        var labelRepo = new LabelRepository(db);
        var customerSvc = new CustomerService(customerRepo, new SessionRepository(db), labelRepo, clock);
        var svc = new LabelService(labelRepo, customerSvc, db, clock);
        return (svc, labelRepo, db, "s1");
    }

    private static ChatMessage Msg(string platform, string username, string text = "MAVI XL aldım") =>
        new(System.Guid.NewGuid().ToString("N"),
            platform, null, username, null, null, text, 1000,
            System.Array.Empty<string>());

    [Fact]
    public void Breakdown_groups_printed_labels_by_platform_ordered_by_revenue()
    {
        var (svc, labels, db, sid) = Fx();
        using var _ = db;

        var ig1 = svc.Add(sid, Msg("instagram", "@ayse_y"), 100m, null);
        var ig2 = svc.Add(sid, Msg("instagram", "@fatma_k"), 150m, null);
        var tt1 = svc.Add(sid, Msg("tiktok", "@mehmet"), 400m, null);
        svc.MarkPrintedAndRecord(new[] { ig1.Id, ig2.Id, tt1.Id });

        var breakdown = labels.GetPlatformBreakdownBySession(sid);

        breakdown.Should().HaveCount(2);
        // Ciroya göre azalan: tiktok (400) > instagram (250)
        breakdown[0].Platform.Should().Be("tiktok");
        breakdown[0].LabelCount.Should().Be(1);
        breakdown[0].TotalAmount.Should().Be(400m);
        breakdown[0].UniqueCustomers.Should().Be(1);

        breakdown[1].Platform.Should().Be("instagram");
        breakdown[1].LabelCount.Should().Be(2);
        breakdown[1].TotalAmount.Should().Be(250m);
        breakdown[1].UniqueCustomers.Should().Be(2);
    }

    [Fact]
    public void Breakdown_excludes_unprinted_and_cancelled_labels()
    {
        var (svc, labels, db, sid) = Fx();
        using var _ = db;

        var printed   = svc.Add(sid, Msg("instagram", "@ayse_y"), 100m, null);
        var cancelled = svc.Add(sid, Msg("instagram", "@fatma_k"), 999m, null);
        svc.Add(sid, Msg("instagram", "@zeynep"), 500m, null); // hiç basılmadı

        svc.MarkPrintedAndRecord(new[] { printed.Id, cancelled.Id });
        svc.Cancel(new[] { cancelled.Id }, "duplicate");

        var breakdown = labels.GetPlatformBreakdownBySession(sid);

        breakdown.Should().HaveCount(1);
        breakdown[0].LabelCount.Should().Be(1);
        breakdown[0].TotalAmount.Should().Be(100m);
    }

    [Fact]
    public void Breakdown_sums_match_session_totals()
    {
        var (svc, labels, db, sid) = Fx();
        using var _ = db;

        var l1 = svc.Add(sid, Msg("instagram", "@ayse_y"), 100m, null);
        var l2 = svc.Add(sid, Msg("tiktok", "@mehmet"), 250m, null);
        var l3 = svc.Add(sid, Msg("tiktok", "@ali"), 50m, null);
        svc.MarkPrintedAndRecord(new[] { l1.Id, l2.Id, l3.Id });
        svc.Cancel(new[] { l3.Id }, "customer");

        var totals = labels.GetSessionTotals(sid);
        var breakdown = labels.GetPlatformBreakdownBySession(sid);

        breakdown.Sum(p => p.LabelCount).Should().Be(totals.PrintedCount);
        breakdown.Sum(p => p.TotalAmount).Should().Be(totals.TotalAmount);
    }

    [Fact]
    public void Breakdown_is_empty_for_session_without_printed_labels()
    {
        var (svc, labels, db, sid) = Fx();
        using var _ = db;

        svc.Add(sid, Msg("instagram", "@ayse_y"), 100m, null); // basılmadı

        labels.GetPlatformBreakdownBySession(sid).Should().BeEmpty();
    }

    [Fact]
    public void PlatformBreakdown_display_name_maps_known_platforms()
    {
        new PlatformBreakdown("instagram", 1, 1m, 1).DisplayName.Should().Be("Instagram");
        new PlatformBreakdown("tiktok", 1, 1m, 1).DisplayName.Should().Be("TikTok");
        new PlatformBreakdown("youtube", 1, 1m, 1).DisplayName.Should().Be("YouTube");
        new PlatformBreakdown("bilinmeyen", 1, 1m, 1).DisplayName.Should().Be("bilinmeyen");
    }
}
