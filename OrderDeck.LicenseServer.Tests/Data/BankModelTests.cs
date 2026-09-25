using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

public sealed class BankModelTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"bank-model-{Guid.NewGuid():N}").Options);

    /// <summary>Tasarım zamanı modeli. Index filtresi ve CHECK kısıtı gibi ilişkisel üst veri
    /// çalışma zamanı modeline (<c>db.Model</c>) taşınmaz; orada okunamaz.</summary>
    private static IModel DesignModel()
    {
        using var db = NewDb();
        return db.GetService<IDesignTimeModel>().Model;
    }

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
    public void PaymentMatch_bir_dekont_iki_harekete_baglanamaz()
    {
        var index = DesignModel().FindEntityType(typeof(PaymentMatch))!.GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "PaymentId" }));
        index.IsUnique.Should().BeTrue("bir dekont iki harekete bağlanamaz");
        index.GetFilter().Should().Be("[PaymentId] IS NOT NULL", "kararsız (null) öneriler birbirine çarpmaz");
    }

    /// <summary>KVKK purge (<c>AdminCustomersController.Purge</c>) lisansı siler ve gerisini DB
    /// kaskadına bırakır. Lisansa doğrudan bağlı her banka tablosu Cascade FK taşımalı; FK'sız
    /// bir tablo müşteri silindikten sonra IBAN hash'ini ve karşı taraf adını yetim tutardı.</summary>
    [Theory]
    [InlineData(typeof(ObifinConnection))]
    [InlineData(typeof(BankAccount))]
    [InlineData(typeof(BankTransaction))]
    [InlineData(typeof(CustomerIbanMemory))]
    [InlineData(typeof(PaymentMatchGap))]
    public void Lisansa_dogrudan_bagli_tablo_lisansla_kaskadla_silinir(Type entity)
    {
        using var db = NewDb();
        var fk = db.Model.FindEntityType(entity)!.GetForeignKeys()
            .Should().ContainSingle(f => f.PrincipalEntityType.ClrType == typeof(License),
                "KVKK purge lisansı siler, bağlı satırlar DB kaskadıyla gider").Subject;
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        fk.Properties.Select(p => p.Name).Should().Equal("LicenseId");
    }

    /// <summary>Bu iki tablo lisansa ebeveynleri üzerinden kaskadlanır. Birisi "tutarlılık" diye
    /// doğrudan Licenses FK'sı eklerse SQL Server çoklu kaskad yolu (hata 1785) nedeniyle göçü
    /// reddeder — bu test o eklemeyi daha modelde yakalar.</summary>
    [Theory]
    [InlineData(typeof(PaymentMatch), typeof(BankTransaction))]
    [InlineData(typeof(BankConnection), typeof(ObifinConnection))]
    public void Ebeveyn_uzerinden_kaskadlanan_tablonun_lisansa_FKsi_yok_ebeveyn_FKsi_kaskad(Type entity, Type parent)
    {
        using var db = NewDb();
        var fks = db.Model.FindEntityType(entity)!.GetForeignKeys().ToList();
        fks.Should().NotContain(f => f.PrincipalEntityType.ClrType == typeof(License),
            "ikinci bir Licenses yolu SQL Server'da hata 1785 verir");
        // Yalnız ebeveyn FK'sı sabitlenir; ileride eklenecek NoAction bir FK bu kararı bozmaz.
        var fk = fks.Single(f => f.PrincipalEntityType.ClrType == parent);
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    [Theory]
    [InlineData(typeof(BankTransaction), "BankTransactions", nameof(BankTransaction.CounterpartyIbanHash))]
    [InlineData(typeof(BankTransaction), "BankTransactions", nameof(BankTransaction.CounterpartyTaxIdHash))]
    [InlineData(typeof(CustomerIbanMemory), "CustomerIbanMemories", nameof(CustomerIbanMemory.IbanHash))]
    public void Hash_sutununa_yalniz_64_hex_yazilabilir(Type entity, string table, string column)
    {
        DesignModel().FindEntityType(entity)!.GetCheckConstraints()
            .Should().ContainSingle(c => c.Name == $"CK_{table}_{column}_Hex64")
            .Which.Sql.Should().Be($"LEN([{column}]) = 64 AND [{column}] NOT LIKE '%[^0-9a-f]%'",
                "ham IBAN/VKN hash sütununa yazılamaz");
    }

    [Theory]
    [InlineData(typeof(ObifinConnection), nameof(ObifinConnection.Status))]
    [InlineData(typeof(BankConnection), nameof(BankConnection.Status))]
    [InlineData(typeof(BankTransaction), nameof(BankTransaction.Direction))]
    [InlineData(typeof(PaymentMatch), nameof(PaymentMatch.Layer))]
    [InlineData(typeof(PaymentMatch), nameof(PaymentMatch.Status))]
    [InlineData(typeof(CustomerIbanMemory), nameof(CustomerIbanMemory.LearnedFrom))]
    [InlineData(typeof(PaymentMatchGap), nameof(PaymentMatchGap.Reason))]
    public void Enum_alanlari_string_saklanir(Type entity, string property)
    {
        using var db = NewDb();
        var p = db.Model.FindEntityType(entity)!.FindProperty(property)!;
        // HasConversion<string>() dönüştürücü nesnesi değil yalnız sağlayıcı tipini kaydeder —
        // GetValueConverter() bu yüzden null döner. Saklanan tip doğrudan sağlayıcı tipidir.
        p.GetProviderClrType().Should().Be(typeof(string), "admin SQL'inde Proposed okunur, 0 değil");
    }

    [Theory]
    [InlineData(typeof(BankTransaction), nameof(BankTransaction.CounterpartyIbanHash), 64)]
    [InlineData(typeof(BankTransaction), nameof(BankTransaction.CounterpartyTaxIdHash), 64)]
    [InlineData(typeof(BankTransaction), nameof(BankTransaction.CounterpartyIbanMasked), 40)]
    [InlineData(typeof(BankTransaction), nameof(BankTransaction.Description), 512)]
    [InlineData(typeof(BankAccount), nameof(BankAccount.IbanHash), 64)]
    [InlineData(typeof(BankAccount), nameof(BankAccount.IbanMasked), 40)]
    [InlineData(typeof(CustomerIbanMemory), nameof(CustomerIbanMemory.IbanHash), 64)]
    [InlineData(typeof(CustomerIbanMemory), nameof(CustomerIbanMemory.IbanMasked), 40)]
    public void Hash_maske_ve_aciklama_sutun_uzunluklari(Type entity, string property, int maxLength)
    {
        using var db = NewDb();
        db.Model.FindEntityType(entity)!.FindProperty(property)!.GetMaxLength().Should().Be(maxLength);
    }
}
