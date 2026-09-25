using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

public sealed class BankModelTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"bank-model-{Guid.NewGuid():N}").Options);

    [Fact]
    public void BankTransaction_lisans_icinde_ObifinId_tekil()
    {
        using var db = NewDb();
        var et = db.Model.FindEntityType(typeof(BankTransaction))!;
        et.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "ObifinId" }),
            "aynı hareket iki kez yazılamaz — imleç geri sarsa bile");
    }

    [Fact]
    public void ObifinConnection_lisans_basina_tek()
    {
        using var db = NewDb();
        var et = db.Model.FindEntityType(typeof(ObifinConnection))!;
        et.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId" }));
    }

    [Fact]
    public void CustomerIbanMemory_lisans_icinde_iban_hash_tekil()
    {
        using var db = NewDb();
        var et = db.Model.FindEntityType(typeof(CustomerIbanMemory))!;
        et.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "IbanHash" }),
            "bir IBAN aynı anda tek müşteriye ait olabilir");
    }

    [Fact]
    public void Ham_iban_alani_yok_yalniz_hash_ve_maske()
    {
        using var db = NewDb();
        var props = db.Model.FindEntityType(typeof(BankTransaction))!.GetProperties().Select(p => p.Name).ToList();
        props.Should().Contain("CounterpartyIbanHash").And.Contain("CounterpartyIbanMasked");
        props.Should().NotContain("CounterpartyIban", "spec §7: IBAN düz saklanmaz");
    }

    [Fact]
    public void PaymentMatch_hareket_basina_tek()
    {
        using var db = NewDb();
        var et = db.Model.FindEntityType(typeof(PaymentMatch))!;
        et.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "BankTransactionId" }));
    }

    [Fact]
    public void Status_alanlari_string_saklanir()
    {
        using var db = NewDb();
        var p = db.Model.FindEntityType(typeof(PaymentMatch))!.FindProperty("Status")!;
        // HasConversion<string>() dönüştürücü nesnesi değil yalnız sağlayıcı tipini kaydeder —
        // GetValueConverter() bu yüzden null döner. Saklanan tip doğrudan sağlayıcı tipidir.
        p.GetProviderClrType().Should().Be(typeof(string), "admin SQL'inde Proposed okunur, 0 değil");
    }
}
