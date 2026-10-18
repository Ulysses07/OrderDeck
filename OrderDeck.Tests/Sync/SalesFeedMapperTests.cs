using System;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Sales;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// <see cref="SalesFeedMapper"/> (spec 2026-10-13 §4.4, §5.4; W2-01): akışın tel kayıtları Core'un yerel biçimine —
/// GUID'ler "N" metin, zamanlar unix saniye, boşlar boş, platformlar liste, dosya durumu yerel enum; müşteri Id'si ham.
/// </summary>
public sealed class SalesFeedMapperTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_791_000_000L);

    private static SalesOrderItem WireOrder(Guid id, Guid? sessionId, string customerId) => new(
        Id: id, SessionId: sessionId, CustomerId: customerId, Platform: "tiktok", Username: "alici_ornek",
        DisplayName: null, MessageText: "MAVI XL aldım", Code: null, Price: 150.5m, AddedAt: T0,
        PrintedAt: null, CancelledAt: null, CancelReason: null, IsShippingFee: false, IsBackupPromoted: true,
        IsTentativeBackup: true, ProductId: null, ProductVariantId: null, UpdatedAt: T0, Version: 7,
        ParentOrderId: null, ShipmentId: null, OriginReplicaId: null, PrintBatchId: null, PrintSeq: null,
        PriceCorrectedAt: null, PaidPrice: null, SessionReleasedAt: null, PriceSetAt: null, CancelSetAt: null,
        BackupSetAt: null, ShipmentSetAt: null, TextScrubbedAt: null, ChangeSeq: 42);

    [Fact]
    public void Siparis_kimlikleri_N_bicimine_zamanlari_unix_saniyeye_cevirir()
    {
        var id = Guid.NewGuid();
        var session = Guid.NewGuid();
        var product = Guid.NewGuid();
        var variant = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var shipment = Guid.NewGuid();
        var wire = WireOrder(id, session, "musteri-ham-id") with
        {
            ProductId = product, ProductVariantId = variant, ParentOrderId = parent, ShipmentId = shipment,
            PrintedAt = T0.AddSeconds(60), CancelledAt = T0.AddSeconds(120), CancelReason = "customer",
            PriceCorrectedAt = T0.AddSeconds(180), PaidPrice = 140m, PrintBatchId = "b-0001", PrintSeq = 3,
            Code = "A1", DisplayName = "Takma Ad",
        };

        var o = SalesFeedMapper.ToCore(wire);

        o.Id.Should().Be(id.ToString("N"));
        o.SessionId.Should().Be(session.ToString("N"));
        o.CustomerId.Should().Be("musteri-ham-id", "müşteri Id'si ham geçer (GUID olmayan değer dahil)");
        o.ProductId.Should().Be(product.ToString("N"));
        o.ProductVariantId.Should().Be(variant.ToString("N"));
        o.ParentLabelId.Should().Be(parent.ToString("N"));
        o.ShipmentId.Should().Be(shipment.ToString("N"));
        o.AddedAt.Should().Be(1_791_000_000L);
        o.PrintedAt.Should().Be(1_791_000_060L);
        o.CancelledAt.Should().Be(1_791_000_120L);
        o.PriceCorrectedAt.Should().Be(1_791_000_180L);
        (o.Price, o.PaidPrice, o.Version, o.PrintSeq).Should().Be((150.5m, (decimal?)140m, 7, (long?)3));
        (o.CancelReason, o.PrintBatchId, o.Code, o.DisplayName, o.MessageText)
            .Should().Be(("customer", "b-0001", "A1", "Takma Ad", "MAVI XL aldım"));
        (o.IsBackupPromoted, o.IsTentativeBackup, o.IsShippingFee).Should().Be((true, true, false));
    }

    [Fact]
    public void Siparisin_bos_alanlari_bos_kalir()
    {
        var o = SalesFeedMapper.ToCore(WireOrder(Guid.NewGuid(), sessionId: null, "c"));

        o.SessionId.Should().BeNull();
        o.PrintedAt.Should().BeNull();
        o.CancelledAt.Should().BeNull();
        o.PriceCorrectedAt.Should().BeNull();
        o.ProductId.Should().BeNull();
        o.ProductVariantId.Should().BeNull();
        o.ParentLabelId.Should().BeNull();
        o.ShipmentId.Should().BeNull();
        o.PaidPrice.Should().BeNull();
        o.PrintSeq.Should().BeNull();
        o.DisplayName.Should().BeNull();
    }

    [Fact]
    public void Oturum_platformlari_listeye_zamanlari_unix_saniyeye_cevirir()
    {
        var id = Guid.NewGuid();
        var wire = new SalesSessionItem(id, "Akşam yayını", T0, T0.AddSeconds(3600), " tiktok, instagram,,", null, T0,
            Guid.NewGuid(), "PC-B", null, T0.AddSeconds(3700), null, 9);

        var s = SalesFeedMapper.ToCore(wire);

        s.Id.Should().Be(id.ToString("N"));
        s.StartedAt.Should().Be(1_791_000_000L);
        s.EndedAt.Should().Be(1_791_003_600L);
        s.ReleasedAt.Should().Be(1_791_003_700L);
        s.Platforms.Should().Equal("tiktok", "instagram");
        s.OwnerMachineName.Should().Be("PC-B");
        s.Notes.Should().BeNull();

        var open = SalesFeedMapper.ToCore(wire with { EndedAt = null, ReleasedAt = null, Platforms = "", Title = null });
        (open.EndedAt, open.ReleasedAt, open.Title).Should().Be(((long?)null, (long?)null, (string?)null));
        open.Platforms.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pending", ShipmentStatus.Pending)]
    [InlineData("held", ShipmentStatus.Held)]
    [InlineData("recipientpays", ShipmentStatus.RecipientPays)]
    [InlineData("shipped", ShipmentStatus.Shipped)]
    public void Dosya_durumu_yerel_enuma_kimlikler_N_bicimine_cevrilir(string wireStatus, ShipmentStatus expected)
    {
        var id = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var wire = new SalesShipmentItem(id, "musteri-ham-id", wireStatus, 550m, T0, T0.AddSeconds(10), null, T0, 4,
            T0, null, origin, 77);

        var s = SalesFeedMapper.ToCore(wire);

        s.Status.Should().Be(expected);
        s.Id.Should().Be(id.ToString("N"));
        s.OriginReplicaId.Should().Be(origin.ToString("N"));
        s.CustomerId.Should().Be("musteri-ham-id");
        (s.CreatedAt, s.HeldAt, s.ShippedAt, s.Version, s.CumulativeAmount)
            .Should().Be((1_791_000_000L, (long?)1_791_000_010L, (long?)null, 4, 550m));
        SalesFeedMapper.ToCore(wire with { OriginReplicaId = null }).OriginReplicaId.Should().BeNull();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("1")]
    [InlineData("")]
    public void Bilinmeyen_dosya_durumu_ogeye_ozgu_hata_atar(string wireStatus)
    {
        var wire = new SalesShipmentItem(Guid.NewGuid(), "c", wireStatus, 0m, T0, null, null, T0, 1, null, null, null, 5);

        FluentActions.Invoking(() => SalesFeedMapper.ToCore(wire)).Should().Throw<FormatException>();
    }

    [Fact]
    public void Taslak_ham_Id_ve_silinme_ani()
    {
        var d = SalesFeedMapper.ToCore(new SalesCustomerDraft("musteri-ham-id", "tiktok", "alici_ornek", null, T0));
        (d.Id, d.Platform, d.Username, d.DisplayName, d.PurgedAt)
            .Should().Be(("musteri-ham-id", "tiktok", "alici_ornek", (string?)null, (long?)1_791_000_000L));
        SalesFeedMapper.ToCore(new SalesCustomerDraft("x", "tiktok", "u", "Takma Ad", null)).PurgedAt.Should().BeNull();
    }

    [Fact]
    public void Ogeler_turune_gore_cevrilir_bilinmeyen_tur_ya_da_eksik_satir_hata_atar()
    {
        var order = WireOrder(Guid.NewGuid(), Guid.NewGuid(), "c");

        var item = SalesFeedMapper.ToCore(new SalesChangeItem(SalesChangeItem.KindOrder, 42, null, order, null));

        (item.Kind, item.ChangeSeq, item.ItemId).Should().Be((SalesFeedItemKind.Order, 42L, order.Id.ToString("N")));
        item.Session.Should().BeNull();
        item.Shipment.Should().BeNull();
        FluentActions.Invoking(() => SalesFeedMapper.ToCore(new SalesChangeItem("payment", 1, null, order, null)))
            .Should().Throw<FormatException>();
        FluentActions.Invoking(() => SalesFeedMapper.ToCore(new SalesChangeItem(SalesChangeItem.KindSession, 1, null, order, null)))
            .Should().Throw<FormatException>();
    }

    [Fact]
    public void Yan_listeler_bossa_bos_doner()
    {
        var page = new SalesChangesPage(null, 0, false, null, null);

        SalesFeedMapper.SideSessions(page).Should().BeEmpty();
        SalesFeedMapper.Drafts(page).Should().BeEmpty();
    }
}
