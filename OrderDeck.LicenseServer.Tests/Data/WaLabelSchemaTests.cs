using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

public sealed class WaLabelSchemaTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"walabel-schema-{Guid.NewGuid():N}").Options);

    [Fact]
    public void Model_exposes_the_four_label_tables()
    {
        using var db = NewDb();
        var model = db.Model;

        model.FindEntityType(typeof(WaLabel)).Should().NotBeNull();
        model.FindEntityType(typeof(WaLabelRule)).Should().NotBeNull();
        model.FindEntityType(typeof(WaConversationLabel)).Should().NotBeNull();
        model.FindEntityType(typeof(WaDekontExtraction)).Should().NotBeNull();
    }

    [Fact]
    public void Label_name_is_unique_per_license()
    {
        using var db = NewDb();
        var idx = db.Model.FindEntityType(typeof(WaLabel))!
            .GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToList();

        idx.Should().Contain("LicenseId,Name");
    }

    [Fact]
    public void One_event_maps_to_at_most_one_label_per_license()
    {
        using var db = NewDb();
        var idx = db.Model.FindEntityType(typeof(WaLabelRule))!
            .GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToList();

        idx.Should().Contain("LicenseId,EventKey");
    }

    [Fact]
    public void The_same_label_cannot_be_attached_twice_to_one_conversation()
    {
        using var db = NewDb();
        var idx = db.Model.FindEntityType(typeof(WaConversationLabel))!
            .GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToList();

        idx.Should().Contain("ConversationId,WaLabelId");
    }

    /// <summary>
    /// License silinirken SQL Server'a tek bir cascade yolu görünmeli. İki yol
    /// (sohbet üzerinden + etiket üzerinden) şema oluşturmayı patlatır.
    /// </summary>
    [Fact]
    public void Join_rows_cascade_only_from_license()
    {
        using var db = NewDb();
        var fks = db.Model.FindEntityType(typeof(WaConversationLabel))!
            .GetForeignKeys()
            .ToDictionary(
                fk => fk.PrincipalEntityType.ClrType.Name,
                fk => fk.DeleteBehavior);

        fks[nameof(License)].Should().Be(DeleteBehavior.Cascade);
        fks[nameof(WaConversation)].Should().Be(DeleteBehavior.NoAction);
        fks[nameof(WaLabel)].Should().Be(DeleteBehavior.NoAction);
    }

    /// <summary>
    /// Kural satırında da aynı kısıt var: License'tan cascade, etiketten
    /// NoAction. Biri "düzeltip" etikete cascade verirse SQL Server şemayı
    /// reddeder — bunu göç üretilirken değil, burada yakalayalım.
    /// </summary>
    [Fact]
    public void Rules_cascade_only_from_license()
    {
        using var db = NewDb();
        var fks = db.Model.FindEntityType(typeof(WaLabelRule))!
            .GetForeignKeys()
            .ToDictionary(
                fk => fk.PrincipalEntityType.ClrType.Name,
                fk => fk.DeleteBehavior);

        fks[nameof(License)].Should().Be(DeleteBehavior.Cascade);
        fks[nameof(WaLabel)].Should().Be(DeleteBehavior.NoAction);
    }
}
