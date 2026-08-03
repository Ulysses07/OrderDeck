using FluentAssertions;
using OrderDeck.Core.Sales;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// Kişi birleştirme kuralı: aynı kişinin farklı platform hesapları tek satırda
/// toplanmalı, yoksa muhasebe aynı kişiye iki fatura keser.
/// </summary>
public class PeriodReportBuilderTests
{
    private static PeriodAccountRow Row(
        string customerId, string platform, string username,
        string? groupId = null, string? fullName = null, string? phone = null,
        string? address = null, string? email = null, string? tckn = null,
        string? displayName = null, long lastSeenAt = 100,
        string day = "2026-07-01", long lastPrintedAt = 1_782_000_000,
        int orderCount = 1, decimal totalAmount = 100m) =>
        new(customerId, groupId, platform, username, displayName, fullName, tckn,
            phone, address, email, lastSeenAt, day, lastPrintedAt, orderCount, totalAmount);

    [Fact]
    public void Accounts_sharing_a_group_collapse_into_one_person()
    {
        var result = PeriodReportBuilder.Build(new[]
        {
            Row("c1", "instagram", "ayse", groupId: "g1", orderCount: 3, totalAmount: 300m),
            Row("c2", "youtube", "UCxx", groupId: "g1", orderCount: 2, totalAmount: 200m),
        });

        result.Should().ContainSingle();
        result[0].AccountCount.Should().Be(2);
        result[0].OrderCount.Should().Be(5);
        result[0].TotalAmount.Should().Be(500m);
        result[0].Accounts.Should().Be("instagram/ayse, youtube/UCxx");
    }

    [Fact]
    public void Accounts_without_a_group_stay_separate_people()
    {
        var result = PeriodReportBuilder.Build(new[]
        {
            Row("c1", "instagram", "ayse", groupId: null),
            Row("c2", "instagram", "mehmet", groupId: "   "),
        });

        result.Should().HaveCount(2);
        result.Should().OnlyContain(p => p.AccountCount == 1);
    }

    [Fact]
    public void Identity_fields_come_from_the_most_recently_seen_account()
    {
        var result = PeriodReportBuilder.Build(new[]
        {
            Row("c1", "instagram", "ayse", groupId: "g1",
                fullName: "Ayşe Y.", address: "Eski Adres", lastSeenAt: 100),
            Row("c2", "youtube", "UCxx", groupId: "g1",
                fullName: "Ayşe Yılmaz", address: "Yeni Adres", lastSeenAt: 900),
        });

        result[0].FullName.Should().Be("Ayşe Yılmaz");
        result[0].Address.Should().Be("Yeni Adres");
    }

    [Fact]
    public void Empty_fields_fall_back_to_the_other_account_in_the_group()
    {
        // En güncel hesapta adres boş, eski hesapta dolu → boş bırakılmamalı.
        var result = PeriodReportBuilder.Build(new[]
        {
            Row("c1", "instagram", "ayse", groupId: "g1",
                fullName: "Ayşe", address: "Moda Cad. 5", phone: "+905551112233", lastSeenAt: 100),
            Row("c2", "youtube", "UCxx", groupId: "g1",
                fullName: null, address: "  ", phone: null, lastSeenAt: 900),
        });

        result[0].FullName.Should().Be("Ayşe");
        result[0].Address.Should().Be("Moda Cad. 5");
        result[0].Phone.Should().Be("+905551112233");
    }

    [Fact]
    public void HasInvoiceInfo_only_requires_a_name()
    {
        // e-Arşiv şablonunda adres/telefon sütunları boş bırakılıyor, TCKN de
        // dummy'leniyor — fatura kesmeye engel olan tek eksik isim.
        var nameOnly = PeriodReportBuilder.Build(new[]
        {
            Row("c1", "instagram", "a", fullName: "Ayşe Yılmaz")
        })[0];
        var chatOnly = PeriodReportBuilder.Build(new[] { Row("c3", "instagram", "c") })[0];

        nameOnly.HasInvoiceInfo.Should().BeTrue();
        nameOnly.InvoiceStatusLabel.Should().Be("Tam");
        chatOnly.HasInvoiceInfo.Should().BeFalse();
        chatOnly.InvoiceStatusLabel.Should().Be("Eksik");
    }

    [Fact]
    public void A_person_buying_on_two_days_gets_two_invoices()
    {
        // Fatura günlük kesiliyor: aynı kişinin farklı günlerdeki alımları
        // birleştirilmemeli, yoksa muhasebe tek fatura keser.
        var rows = new[]
        {
            Row("c1", "instagram", "ayse", fullName: "Ayşe Yılmaz",
                day: "2026-07-03", lastPrintedAt: 1_000, totalAmount: 300m, orderCount: 2),
            Row("c1", "instagram", "ayse", fullName: "Ayşe Yılmaz",
                day: "2026-07-09", lastPrintedAt: 9_000, totalAmount: 120m, orderCount: 1),
        };

        var invoices = PeriodReportBuilder.BuildInvoices(rows);

        invoices.Should().HaveCount(2);
        invoices.Select(i => i.Day).Should().ContainInOrder("2026-07-03", "2026-07-09");
        invoices[0].TotalAmount.Should().Be(300m);
        invoices[1].TotalAmount.Should().Be(120m);

        // Detay sayfası aynı kişiyi tek satırda toplamayı sürdürür.
        var people = PeriodReportBuilder.Build(rows);
        people.Should().ContainSingle();
        people[0].DayCount.Should().Be(2);
        people[0].TotalAmount.Should().Be(420m);
        people[0].Accounts.Should().Be("instagram/ayse");
    }

    [Fact]
    public void Same_person_same_day_across_platforms_is_a_single_invoice()
    {
        var invoices = PeriodReportBuilder.BuildInvoices(new[]
        {
            Row("c1", "instagram", "ayse", groupId: "g1", fullName: "Ayşe Yılmaz",
                day: "2026-07-03", lastPrintedAt: 1_000, totalAmount: 300m),
            Row("c2", "youtube", "UCxx", groupId: "g1", fullName: "Ayşe Yılmaz",
                day: "2026-07-03", lastPrintedAt: 5_000, totalAmount: 200m),
        });

        invoices.Should().ContainSingle();
        invoices[0].TotalAmount.Should().Be(500m);
        // Fatura saati o günün SON etiketinden gelir.
        invoices[0].IssuedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(5_000).ToLocalTime());
    }

    [Fact]
    public void Invoices_skip_people_without_a_name()
    {
        var invoices = PeriodReportBuilder.BuildInvoices(new[]
        {
            Row("c1", "instagram", "ayse", fullName: "Ayşe Yılmaz"),
            Row("c2", "instagram", "nickonly", displayName: "@nick"),
        });

        invoices.Should().ContainSingle();
        invoices[0].FullName.Should().Be("Ayşe Yılmaz");
    }

    [Theory]
    [InlineData("Ayşe Yılmaz", "Ayşe", "Yılmaz")]
    [InlineData("Mehmet Oğuzhan Tanrıverdi", "Mehmet Oğuzhan", "Tanrıverdi")]
    [InlineData("Cher", "Cher", "")]
    public void Name_splits_on_the_last_space(string full, string first, string last)
    {
        var invoice = PeriodReportBuilder.BuildInvoices(new[]
        {
            Row("c1", "instagram", "a", fullName: full)
        })[0];

        invoice.FirstName.Should().Be(first);
        invoice.LastName.Should().Be(last);
    }

    [Fact]
    public void DisplayLabel_prefers_full_name_then_display_name_then_username()
    {
        var named = PeriodReportBuilder.Build(new[] {
            Row("c1", "youtube", "UCxx", fullName: "Ayşe Yılmaz", displayName: "@ayse") })[0];
        var nickOnly = PeriodReportBuilder.Build(new[] {
            Row("c2", "youtube", "UCyy", displayName: "@mehmet") })[0];
        var raw = PeriodReportBuilder.Build(new[] { Row("c3", "youtube", "UCzz") })[0];

        named.DisplayLabel.Should().Be("Ayşe Yılmaz");
        nickOnly.DisplayLabel.Should().Be("@mehmet");
        raw.DisplayLabel.Should().Be("UCzz");
    }

    [Fact]
    public void People_are_ordered_by_total_amount_descending()
    {
        var result = PeriodReportBuilder.Build(new[]
        {
            Row("c1", "instagram", "kucuk", totalAmount: 50m),
            Row("c2", "instagram", "buyuk", totalAmount: 900m),
            Row("c3", "instagram", "orta", totalAmount: 300m),
        });

        result.Select(p => p.DisplayLabel).Should().ContainInOrder("buyuk", "orta", "kucuk");
    }

    [Fact]
    public void Empty_input_produces_an_empty_report()
    {
        PeriodReportBuilder.Build(Array.Empty<PeriodAccountRow>()).Should().BeEmpty();
    }
}
