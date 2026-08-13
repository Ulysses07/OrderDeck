using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// Şema iddialarını EF model metadata'sından doğrular. Gerekçe: testler
/// InMemory üstünde koşuyor ve InMemory HasMaxLength'i de indeksleri de yok
/// sayıyor — davranışsal test bu ayrımı gösteremez, metadata gösterir.
/// </summary>
public class StockModelTests
{
    private static IModel Model()
    {
        var opts = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new LicenseDbContext(opts);
        return db.Model;
    }

    [Fact]
    public void Note_max_length_matches_CatalogLimits()
    {
        var prop = Model().FindEntityType(typeof(StockMovement))!
            .FindProperty(nameof(StockMovement.Note))!;
        prop.GetMaxLength().Should().Be(CatalogLimits.MovementNote);
    }

    [Fact]
    public void Cursor_index_on_license_and_created_at_exists()
    {
        var indexes = Model().FindEntityType(typeof(StockMovement))!.GetIndexes();
        indexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties[0].Name == nameof(StockMovement.LicenseId) &&
            i.Properties[1].Name == nameof(StockMovement.CreatedAt));
    }

    [Fact]
    public void Balance_index_on_license_product_variant_exists()
    {
        var indexes = Model().FindEntityType(typeof(StockMovement))!.GetIndexes();
        indexes.Should().ContainSingle(i =>
            i.Properties.Count == 3 &&
            i.Properties[0].Name == nameof(StockMovement.LicenseId) &&
            i.Properties[1].Name == nameof(StockMovement.ProductId) &&
            i.Properties[2].Name == nameof(StockMovement.ProductVariantId));
    }

    [Fact]
    public void Order_index_exists_for_reconciliation_lookup()
    {
        var indexes = Model().FindEntityType(typeof(StockMovement))!.GetIndexes();
        var index = indexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties[0].Name == nameof(StockMovement.LicenseId) &&
            i.Properties[1].Name == nameof(StockMovement.OrderId)).Which;

        // Mutabakat sorgusunun okuduğu sütunlar yaprak sayfada duruyor mu?
        // Duruyorsa sorgu covering olur ve ana tabloya key lookup yapmaz.
        index.FindAnnotation("SqlServer:Include")!.Value
            .Should().BeEquivalentTo(new[]
            {
                nameof(StockMovement.ProductId),
                nameof(StockMovement.ProductVariantId),
                nameof(StockMovement.Quantity)
            });
    }

    [Fact]
    public void Product_and_variant_deletes_are_restricted()
    {
        var entity = Model().FindEntityType(typeof(StockMovement))!;
        foreach (var fkName in new[]
                 { nameof(StockMovement.ProductId), nameof(StockMovement.ProductVariantId) })
        {
            var fk = entity.GetForeignKeys()
                .Single(f => f.Properties.Count == 1 && f.Properties[0].Name == fkName);
            fk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
        }
    }
}
