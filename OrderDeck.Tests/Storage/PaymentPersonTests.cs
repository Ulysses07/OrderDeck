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

    private string Insert(string? phone = null, string? groupId = null, string platform = "instagram",
        string? username = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(id, platform, username ?? "u" + id[..8], null, null, 1, 1, false, null, null,
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

    // Ortak bakiye §6.1-a: üye, sahip kuralının girdisi olan Platform/Username'i satırdan taşır
    // (senkronlanan, yakınsayan alanlar — iki bilgisayar aynı girdiyi görür).
    [Fact]
    public void Uyeler_platform_ve_kullanici_adini_tasir()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(groupId: group);
        var b = Insert(groupId: group, platform: "tiktok");

        var person = _customers.GetPaymentPerson(a);

        foreach (var id in new[] { a, b })
        {
            var row = _customers.GetById(id)!;
            var member = person.Members.Single(m => m.Id == id);
            member.Platform.Should().Be(row.Platform);
            member.Username.Should().Be(row.Username);
        }
    }

    [Fact] // §6.1-a: tek satırlık kişi de gönderilebilirliği taşır — yoksa GUID'li tek satır bile sahipsiz kalırdı
    public void Tek_satirlik_kisi_platform_ve_kullanici_adini_tasir()
    {
        var id = Insert(phone: TestPhone.NewE164());
        var customer = _customers.GetById(id)!;

        var person = PaymentPerson.Single(customer);

        person.Members.Should().ContainSingle();
        person.Members[0].Platform.Should().Be(customer.Platform);
        person.Members[0].Username.Should().Be(customer.Username);
        person.OwnerId.Should().Be(customer.Id, "GUID'li ve gönderilebilir tek satır sahiptir");
    }

    // Ortak bakiye §6.1-a, M14: GUID'li ama sunucunun reddedeceği satır (Username boş) sahip seçilmez —
    // telefon önceliği onu öne alsa da. Gönderilebilir öbür GUID'li üye sahip olur; kişiden çıkan satır yok.
    [Fact]
    public void Kullanici_adi_bos_guidli_satir_sahip_secilmez_gonderilebilir_uye_secilir()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(phone: TestPhone.NewE164(), groupId: group, username: "");
        var b = Insert(groupId: group, platform: "tiktok");

        var person = _customers.GetPaymentPerson(a);

        person.OwnerId.Should().Be(b, "eski kural telefonlu A'yı seçerdi; A sunucuya hiç ulaşmaz");
        person.MemberIds.Should().BeEquivalentTo(new[] { a, b }, "gönderilemeyen satır kişiden çıkmaz");
    }

    [Fact] // §6.1-a: hiçbir GUID'li üye gönderilemiyorsa sahip yok — o kişide bakiye işi açılmaz
    public void Hicbir_uye_gonderilemezse_sahip_yok()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(phone: TestPhone.NewE164(), groupId: group, username: "");
        var b = Insert(groupId: group, platform: new string('p', 33));

        var person = _customers.GetPaymentPerson(a);

        person.OwnerId.Should().BeNull();
        person.MemberIds.Should().BeEquivalentTo(new[] { a, b });
    }

    // §8.4-3: kural yalnız YENİ işin sahibini seçer. Sahibi sonradan gönderilemez olan kişinin var olan işi
    // üye taramasıyla bulunur; yeni sahip onu taşımaz, ikinci iş açılmaz. BİLEREK kilitlenen sınır: o iş
    // gönderilemeyen satırda kalır (satır kipinde MarkNoBalance, havuz kipinde düşüm yazılamaz) — Sahip
    // sorusu 2 (Görev 1-a) = (A), 16.11.2026: iş taşınmaz.
    [Fact]
    public void Var_olan_isin_sahibi_kural_degisse_de_degismez()
    {
        var group = Guid.NewGuid().ToString("N");
        var a = Insert(phone: TestPhone.NewE164(), groupId: group);
        var b = Insert(groupId: group, platform: "tiktok");
        var jobs = new PaymentJobRepository(_db);

        var once = _customers.GetPaymentPerson(a);
        once.OwnerId.Should().Be(a, "ön koşul: telefonlu ve gönderilebilir A sahip");
        var ilk = jobs.FindOrCreateForPerson(new PersonJobRequest(
            once.LeasedId, once.MemberKey, once.MemberIds, once.OwnerId!, "session:s1", 100m));
        ilk.Status.Should().Be(PersonJobStatus.Ready);
        ilk.Job!.CustomerId.Should().Be(a);

        using (var conn = _db.Open())
            conn.Execute("UPDATE Customer SET Username = '' WHERE Id = @a", new { a });

        var sonra = _customers.GetPaymentPerson(a);
        sonra.OwnerId.Should().Be(b, "yeni işin sahibi artık gönderilebilir B");
        var ikinci = jobs.FindOrCreateForPerson(new PersonJobRequest(
            sonra.LeasedId, sonra.MemberKey, sonra.MemberIds, sonra.OwnerId!, "session:s1", 100m));

        ikinci.Status.Should().Be(PersonJobStatus.Ready);
        ikinci.Job!.Id.Should().Be(ilk.Job!.Id, "iş üye taramasıyla bulunur — ikinci iş açılmaz");
        ikinci.Job!.CustomerId.Should().Be(a, "var olan işin sahibi değişmez");
    }
}
