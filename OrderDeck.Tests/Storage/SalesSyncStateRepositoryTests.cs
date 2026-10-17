using Dapper;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Yerel satış durumu (spec 2026-10-13 §2, §3.2, §8.5): görülen kip kuralı ve sunucu durumunun saklanması.
/// W1 kipi (Mode, ModeSeenAt, FirstCatchUpAt) hiç yazmaz.
/// </summary>
public sealed class SalesSyncStateRepositoryTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly SalesSyncStateRepository _repo;

    public SalesSyncStateRepositoryTests()
    {
        new MigrationRunner(_db).Run();
        _repo = new SalesSyncStateRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private void Exec(string sql)
    {
        using var c = _db.Open();
        c.Execute(sql);
    }

    [Fact]
    public void Gorulen_kip_yalniz_mod_0_ve_bekleyen_komut_yokken_0()
    {
        _repo.GetMode().Should().Be(0, "049 tohumu: mod Off");
        _repo.HasPendingSalesCommands().Should().BeFalse();
        _repo.GetSeenMode().Should().Be(0);

        Exec("INSERT INTO PendingSalesCommand (CommandId, Kind, PayloadJson, CreatedAt) VALUES ('k1', 'cancel', '{}', 1)");
        _repo.HasPendingSalesCommands().Should().BeTrue();
        _repo.GetSeenMode().Should().Be(1, "Off turu bekleyen komut çevrilene kadar bitmiş sayılmaz (§8.5)");

        Exec("DELETE FROM PendingSalesCommand");
        _repo.GetSeenMode().Should().Be(0);

        Exec("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        _repo.GetMode().Should().Be(1);
        _repo.GetSeenMode().Should().Be(1);
    }

    [Fact]
    public void Sunucu_durumu_saklanir_ayniysa_yazilmaz_kip_alanlarina_dokunulmaz()
    {
        Exec("UPDATE SalesSyncState SET ModeSeenAt = 77, FirstCatchUpAt = 88 WHERE Id = 1");

        _repo.SaveServerState(3, 1500m, 89.90m, scrubOrderText: true).Should().BeTrue();
        var row = _repo.Get();
        row.ShippingVersion.Should().Be(3);
        row.FreeShippingThreshold.Should().Be(1500m);
        row.ShippingFee.Should().Be(89.90m);
        row.ScrubOrderText.Should().BeTrue();

        _repo.SaveServerState(3, 1500m, 89.90m, scrubOrderText: true).Should().BeFalse("aynı değerler yazma kilidi almaz");

        // Ayar hiç yazılmamış lisans: üçü de boş.
        _repo.SaveServerState(null, null, null, scrubOrderText: false).Should().BeTrue();
        row = _repo.Get();
        row.ShippingVersion.Should().BeNull();
        row.FreeShippingThreshold.Should().BeNull();
        row.ShippingFee.Should().BeNull();
        row.ScrubOrderText.Should().BeFalse();

        row.Mode.Should().Be(0);
        row.ModeSeenAt.Should().Be(77);
        row.FirstCatchUpAt.Should().Be(88);
    }
}
