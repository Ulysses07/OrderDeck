using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dapper;
using FluentAssertions;
using Moq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Shared.Sales;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales.Rules;

/// <summary>
/// Eşlik: ortak vektörler masaüstünün BUGÜNKÜ koduna karşı (<see cref="LabelRepository.SameCode"/>,
/// <see cref="LabelService"/>'in planı, kod fiyatı ve iptali, <see cref="LabelRepository.UpdatePriceForCorrection"/>'ın
/// SQL'i). <see cref="LabelRules"/> aynı vektörlerle <see cref="LabelRulesVectorTests"/>'te koşar; ikisi birlikte
/// "ortak kural masaüstüyle aynı" demektir. Masaüstü bu PR'da kendi koduyla kalır (spec 2026-10-13 §4.11, S1);
/// biri değişirse burası kırmızı yanar.
/// </summary>
public sealed class LabelRulesCoreParityTests
{
    private const string SessionId = "s1";

    private sealed class Fx : IDisposable
    {
        private readonly Dictionary<string, string> _shipmentIds = new(StringComparer.Ordinal);
        private int _user;

        public Fx()
        {
            Db = new InMemorySqlite();
            new MigrationRunner(Db).Run();
            var clock = new Mock<IClock>();
            clock.Setup(c => c.UnixNow()).Returns(1000L);
            new SessionRepository(Db).Insert(
                new StreamSession(SessionId, null, 1000, null, new[] { "instagram" }, null));
            Labels = new LabelRepository(Db);
            Customers = new CustomerRepository(Db);
            Shipments = new ShipmentRepository(Db);
            var customerSvc = new CustomerService(Customers, new SessionRepository(Db), Labels, clock.Object);
            Svc = new LabelService(Labels, customerSvc, Db, clock.Object, Shipments);
        }

        public InMemorySqlite Db { get; }
        public LabelService Svc { get; }
        public LabelRepository Labels { get; }
        public CustomerRepository Customers { get; }
        public ShipmentRepository Shipments { get; }

        /// <summary>Vektör etiketini yazılış sırasıyla ekler, gerçek Id'yi döner. Kurala giren alanlar doğrudan
        /// SQL'le yazılır: amaç bir durumu kurmak, onu üreten akışı sınamak değil.</summary>
        public Label Insert(LabelFacts f, IReadOnlyDictionary<string, SalesShipmentStatus> shipments)
        {
            var user = $"@alici{++_user}";
            var label = Svc.Add(SessionId, Msg(user), f.Price, f.Code);
            using var conn = Db.Open();
            conn.Execute(
                @"UPDATE Label SET PrintedAt=@printedAt, CancelledAt=@cancelledAt, CancelReason=@reason,
                         IsTentativeBackup=@tentative, IsShippingFee=@fee, ShipmentId=@shipmentId
                  WHERE Id=@id",
                new
                {
                    id = label.Id,
                    printedAt = f.Printed ? 1500L : (long?)null,
                    cancelledAt = f.Cancelled ? 1600L : (long?)null,
                    reason = f.Cancelled ? CancelReasonCodes.Customer : null,
                    tentative = f.Tentative ? 1 : 0,
                    fee = f.ShippingFee ? 1 : 0,
                    shipmentId = f.ShipmentId is { } sid ? ShipmentIdFor(sid, shipments, label.CustomerId) : null,
                });
            return label;
        }

        /// <summary>Vektördeki dosya Id'sinin gerçek karşılığı. Haritada olmayan dosya hiç yazılmaz: etiket
        /// yerelde bulunmayan bir dosyayı gösterir ("bilinmiyor").</summary>
        private string ShipmentIdFor(
            string vectorId, IReadOnlyDictionary<string, SalesShipmentStatus> shipments, string customerId)
        {
            if (_shipmentIds.TryGetValue(vectorId, out var id)) return id;
            id = Guid.NewGuid().ToString("N");
            _shipmentIds[vectorId] = id;
            if (shipments.TryGetValue(vectorId, out var status))
                Shipments.Insert(new Shipment(
                    id, customerId, Enum.Parse<ShipmentStatus>(status.ToString()), 1000, null, null, 0m));
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static ChatMessage Msg(string user) => new(
        Guid.NewGuid().ToString("N"), "instagram", null, user, user, null, $"{user} aldım", 1000,
        Array.Empty<string>());

    public static IEnumerable<object[]> SameCodeNames() => Names("sameCode");
    public static IEnumerable<object[]> CodeKeyNames() => Names("codeKey");
    public static IEnumerable<object[]> ClassifyNames() => Names("classify");
    public static IEnumerable<object[]> CodePriceNames() => Names("codePrice");
    public static IEnumerable<object[]> ContributionNames() => Names("contribution");
    public static IEnumerable<object[]> PaidPriceNames() => Names("paidPriceOnCorrection");

    private static IEnumerable<object[]> Names(string section)
        => LabelRulesVectors.Names(section).Select(n => new object[] { n });

    [Fact]
    public void Masaustu_dosya_durumu_ortak_durumla_ayni_ad_ve_degerde()
    {
        Enum.GetNames<ShipmentStatus>().Should().Equal(Enum.GetNames<SalesShipmentStatus>());
        foreach (var s in Enum.GetValues<ShipmentStatus>())
            ((int)s).Should().Be((int)Enum.Parse<SalesShipmentStatus>(s.ToString()), $"durum {s}");
    }

    [Theory]
    [MemberData(nameof(SameCodeNames))]
    public void SameCode_LabelRepository_ile_ayni(string name)
    {
        var c = LabelRulesVectors.SameCode().Single(v => v.Name == name);
        LabelRepository.SameCode(c.A, c.B).Should().Be(c.Expected, $"vektör {name}");
    }

    [Theory]
    [MemberData(nameof(CodeKeyNames))]
    public void CodeKey_LabelService_ile_ayni(string name)
    {
        var c = LabelRulesVectors.CodeKey().Single(v => v.Name == name);
        // LabelService.CodeKey özel; eşlik için yansımayla çağrılır. Adı değişirse burası açıkça patlar.
        var codeKey = typeof(LabelService).GetMethod("CodeKey", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LabelService.CodeKey bulunamadı");
        codeKey.Invoke(null, new object?[] { c.Code }).Should().Be(c.Expected, $"vektör {name}");
    }

    // ── Bilinçli tek fark: bağlı dosyası yerelde olmayan etiket (spec 2026-10-13 §7.4, §15-57; W1) ──────────
    // Sunucuda her dosya bilinir; ortak kural bilmediği dosyayı (statusOf null) düzeltilebilir sayar. Masaüstünde
    // dosyası yerelde olmayan etiket başka bilgisayarın dosyasındadır (mod Off'ta disable turunun sildiği karışık
    // dosya): durumu bilinmez — kargoya verilmiş olabilir — düzeltilmez, ayrı sınıfta listelenir ve kod fiyatının
    // kaynağında kargoya verilmiş gibi durur. Eşlik bu farkı açıkça kurar; vektörün geri kalanı birebir aynı.

    /// <summary>Masaüstündeki sınıf: ortak kuralınkiyle aynı, yalnız "başka bilgisayarın dosyasında" eklenir.</summary>
    private const string Elsewhere = "elsewhere";

    private static bool OnMissingShipment(LabelFacts f, IReadOnlyDictionary<string, SalesShipmentStatus> shipments)
        => !f.Cancelled && !f.ShippingFee && f.ShipmentId is { } sid && !shipments.ContainsKey(sid);

    [Theory]
    [MemberData(nameof(ClassifyNames))]
    public void Classify_LabelService_plani_ile_ayni(string name)
    {
        var c = LabelRulesVectors.Classify().Single(v => v.Name == name);
        using var fx = new Fx();
        var realIds = c.Labels.ToDictionary(l => l.Facts.Id, l => fx.Insert(l.Facts, c.Shipments).Id);

        var plan = fx.Svc.PlanPriceCorrection(realIds.Values.ToList());

        foreach (var l in c.Labels)
        {
            var id = realIds[l.Facts.Id];
            var actual = plan.Correctable.Any(x => x.Id == id) ? nameof(LabelPriceClass.Correctable)
                : plan.Shipped.Any(x => x.Id == id) ? nameof(LabelPriceClass.Shipped)
                : plan.Excluded.Any(x => x.Id == id) ? nameof(LabelPriceClass.Excluded)
                : plan.Elsewhere.Any(x => x.Id == id) ? Elsewhere
                : throw new InvalidOperationException($"etiket {l.Facts.Id} planda yok");
            var expected = OnMissingShipment(l.Facts, c.Shipments) ? Elsewhere : l.Expected.ToString();
            actual.Should().Be(expected, $"vektör {name}, etiket {l.Facts.Id}");
        }
    }

    [Fact]
    public void Classify_vektorlerinde_bilinmeyen_dosya_durumu_var_ve_masaustunde_ayri_sinif()
    {
        // Yukarıdaki eşlemenin boş kalmadığının kanıtı: vektör dosyası bu durumu taşıyor, ortak kural onu
        // düzeltilebilir sayıyor (sunucu anlamı), masaüstü değil.
        var unknown = LabelRulesVectors.Classify()
            .SelectMany(c => c.Labels.Where(l => OnMissingShipment(l.Facts, c.Shipments)))
            .ToList();
        unknown.Should().NotBeEmpty();
        unknown.Should().OnlyContain(l => l.Expected == LabelPriceClass.Correctable);
    }

    [Theory]
    [MemberData(nameof(CodePriceNames))]
    public void CodePrice_LabelService_GetCodePrice_ile_ayni(string name)
    {
        var c = LabelRulesVectors.CodePrice().Single(v => v.Name == name);
        using var fx = new Fx();
        foreach (var l in c.Labels) fx.Insert(l, c.Shipments);

        // Dosyası yerelde olmayan etiket kaynakta kargoya verilmiş gibi durur (yukarıdaki not).
        var expected = c.Labels.Any(l => OnMissingShipment(l, c.Shipments))
            ? LabelRules.CodePrice(c.Labels,
                sid => c.Shipments.TryGetValue(sid, out var s) ? s : SalesShipmentStatus.Shipped)
            : c.Expected;

        decimal? actual = fx.Svc.GetCodePrice(SessionId, c.Code)?.Price;
        actual.Should().Be(expected, $"vektör {name}");
    }

    [Theory]
    [MemberData(nameof(ContributionNames))]
    public void Contribution_iptalin_musteri_toplamindan_dustugu_ile_ayni(string name)
    {
        var c = LabelRulesVectors.Contribution().Single(v => v.Name == name);
        using var fx = new Fx();
        var shipments = new Dictionary<string, SalesShipmentStatus>(StringComparer.Ordinal);
        var label = fx.Insert(c.Label, shipments);
        // Insert durumu SQL'le kurar, sayaca dokunmaz. W1-03'ten beri toplam etiketlerden türetilir: önce
        // türetilmiş "önce" değeri kurulur (kaymış 0/0 tohumuyla fark katkıyı değil kaymayı ölçerdi).
        using (var write = DbWrite.Begin(fx.Db))
        {
            CustomerAggregateSql.Recompute(new[] { label.CustomerId }, write);
            write.Commit();
        }
        var before = fx.Customers.GetById(label.CustomerId)!;

        // İptal, etiketin müşteri toplamına katkısını geri alır (zaten iptalse dokunmaz): düşülen = katkı.
        fx.Svc.Cancel(new[] { label.Id }, CancelReasonCodes.Customer);

        var after = fx.Customers.GetById(label.CustomerId)!;
        (before.TotalLabelsPrinted - after.TotalLabelsPrinted, before.TotalAmount - after.TotalAmount)
            .Should().Be((c.ExpectedCount, c.ExpectedAmount), $"vektör {name}");
    }

    [Theory]
    [MemberData(nameof(PaidPriceNames))]
    public void PaidPriceOnCorrection_UpdatePriceForCorrection_SQLi_ile_ayni(string name)
    {
        var c = LabelRulesVectors.PaidPriceOnCorrection().Single(v => v.Name == name);
        using var fx = new Fx();
        var label = fx.Svc.Add(SessionId, Msg("@odeyen"), c.PriceBefore, "A1");
        using (var conn = fx.Db.Open())
            conn.Execute("UPDATE Label SET PaidPrice=@paidPrice, ShipmentId=@shipmentId WHERE Id=@id", new
            {
                id = label.Id,
                paidPrice = c.PaidPrice,
                // Yazım yalnız ShipmentId'nin dolu olmasına bakar; dosyanın kendisi gerekmez.
                shipmentId = c.LinkedToShipment ? Guid.NewGuid().ToString("N") : null,
            });

        using (var write = DbWrite.Begin(fx.Db))
        {
            fx.Labels.UpdatePriceForCorrection(label.Id, c.PriceBefore, expectPrinted: false,
                    expectTentative: false, c.PaidFromBalance, c.PriceBefore + 1m, 2000, write)
                .Should().Be(1, $"vektör {name}: koşullu yazım satırı bulmalı");
            write.Commit();
        }

        using var read = fx.Db.Open();
        read.ExecuteScalar<decimal?>("SELECT PaidPrice FROM Label WHERE Id=@id", new { id = label.Id })
            .Should().Be(c.Expected, $"vektör {name}");
    }
}
