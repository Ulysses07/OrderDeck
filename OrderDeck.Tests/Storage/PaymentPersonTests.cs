using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// <see cref="CustomerRepository.GetPaymentPerson"/>: ödeme kişisi = satırın grubunun bütün üyeleri;
/// grup eşitliği kart (<see cref="CustomerGroup.PersonKey"/>) ve <see cref="CustomerRepository.CompleteGroups"/>
/// ile AYNI tanımdan. Ayrışsalardı kart ile ödeme işi farklı kişileri görürdü.
/// </summary>
public sealed class PaymentPersonTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;

    public PaymentPersonTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private string Insert(string? phone = null, string? groupId = null, string platform = "instagram")
    {
        var id = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(id, platform, "u" + id[..8], null, null, 1, 1, false, null, null,
            0, 0m, null, null, phone, GroupId: groupId));
        return id;
    }

    [Fact]
    public void Grubun_butun_uyelerini_doner_hangi_uyeden_cagrilirsa_cagrilsin_sahip_ayni()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(groupId: group);
        var b = Insert(phone: TestPhone.NewE164(), groupId: group, platform: "tiktok");
        var c = Insert(groupId: group, platform: "youtube");
        Insert(phone: TestPhone.NewE164());                        // başka kişi

        foreach (var from in new[] { a, b, c })
        {
            var person = _customers.GetPaymentPerson(from);
            person.LeasedId.Should().Be(from);
            person.MemberIds.Should().BeEquivalentTo(new[] { a, b, c });
            person.OwnerId.Should().Be(b, "telefonlu üye önce — hangi üyeden girilirse girilsin");
        }
    }

    [Fact]
    public void GroupId_bossa_yalniz_satirin_kendisi()
    {
        var a = Insert(phone: TestPhone.NewE164());
        Insert(phone: TestPhone.NewE164());

        var person = _customers.GetPaymentPerson(a);
        person.MemberIds.Should().Equal(a);
        person.OwnerId.Should().Be(a);
    }

    [Fact]
    public void Silinmis_uye_sahip_secilirken_sona_duser()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(phone: TestPhone.NewE164(), groupId: group);
        var b = Insert(groupId: group, platform: "tiktok");
        _customers.ScrubPersonalData(a).Should().Be(1);

        var person = _customers.GetPaymentPerson(b);
        person.OwnerId.Should().Be(b, "KVKK ile silinmiş satır yeni işin sahibi olmaz");
        person.Members.Single(m => m.Id == a).Purged.Should().BeTrue();
    }

    [Fact]
    public void Tasinmis_Id_guncel_satirin_kisisine_cozulur()
    {
        var group = Guid.NewGuid().ToString("N");
        var canonical = Insert(groupId: group);
        var other = Insert(groupId: group, platform: "tiktok");
        var moved = Guid.NewGuid().ToString("N");
        using (var c = _db.Open())
            c.Execute("INSERT INTO CustomerRedirect (FromId, ToId, At) VALUES (@moved, @canonical, 1)", new { moved, canonical });

        var person = _customers.GetPaymentPerson(moved);
        person.LeasedId.Should().Be(canonical);
        person.MemberIds.Should().BeEquivalentTo(new[] { canonical, other });
    }

    // Tek grup eşitliği tanımı: boşluklu/boş GroupId kartta, grup tamamlamada ve ödeme kişisinde
    // aynı sonucu verir. " g" ile "g" ayrı gruptur (kırpma yok — SQL'in ham GroupId eşitliğiyle aynı).
    [Fact]
    public void Bosluklu_ve_bos_GroupId_kart_grup_tamamlama_ve_odeme_kisisinde_ayni_kisiyi_verir()
    {
        var blankA = Insert(groupId: "   ");
        var blankB = Insert(groupId: "   ", platform: "tiktok");
        var empty = Insert(groupId: "");
        var padded = Insert(groupId: " g1", platform: "youtube");
        var plain = Insert(groupId: "g1", platform: "facebook");
        var plain2 = Insert(groupId: "g1", platform: "tiktok");

        var all = new[] { blankA, blankB, empty, padded, plain, plain2 }
            .Select(id => _customers.GetById(id)!).ToList();

        foreach (var row in all)
        {
            var person = _customers.GetPaymentPerson(row.Id);

            // Kartın kişisi: aynı PersonKey'i taşıyan satırlar.
            var cardMembers = all.Where(o => CustomerGroup.PersonKey(o.Id, o.GroupId)
                                             == CustomerGroup.PersonKey(row.Id, row.GroupId))
                                 .Select(o => o.Id);
            person.MemberIds.Should().BeEquivalentTo(cardMembers, $"kart ile ödeme kişisi aynı ({row.GroupId ?? "null"})");

            // Grup tamamlamanın kişisi: tek satırdan tamamlanan küme.
            var completed = _customers.CompleteGroups(new[] { row }).Select(c => c.Id);
            person.MemberIds.Should().BeEquivalentTo(completed, $"grup tamamlama ile ödeme kişisi aynı ({row.GroupId ?? "null"})");
        }

        _customers.GetPaymentPerson(blankA).MemberIds.Should().Equal(new[] { blankA }, "yalnız boşluk = gruplanmamış");
        _customers.GetPaymentPerson(plain).MemberIds.Should().BeEquivalentTo(new[] { plain, plain2 },
            "' g1' ayrı grup — kırpılmaz");
    }
}
