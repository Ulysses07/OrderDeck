using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Customers;

/// <summary>
/// Ödeme işinin sahip satırı (<see cref="PaymentOwnerRule"/>). Vektörler ortak dosyadan
/// (<c>PaymentOwnerRuleVectors.json</c>): sunucu ileride aynı kuralı uygularsa aynı küme ona da koşar.
/// Her vektör üyelerin BÜTÜN sıralamalarında ve bilgisayara özgü alanlar (ciro, alıcı ödemeli)
/// değiştirilerek de koşar — iki bilgisayar aynı kişi için aynı sahibi seçmeli.
/// </summary>
public sealed class PaymentOwnerRuleTests
{
    private sealed record VectorMember(string Id, string Phone, bool Purged);
    private sealed record Vector(string Name, IReadOnlyList<VectorMember> Members, string? Owner);

    private static IReadOnlyList<Vector> Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Customers", "PaymentOwnerRuleVectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
            v.GetProperty("name").GetString()!,
            v.GetProperty("members").EnumerateArray().Select(m => new VectorMember(
                m.GetProperty("id").GetString()!,
                m.GetProperty("phone").GetString()!,
                m.TryGetProperty("purged", out var p) && p.GetBoolean())).ToList(),
            v.GetProperty("owner").ValueKind == JsonValueKind.Null ? null : v.GetProperty("owner").GetString()))
            .ToList();
    }

    public static IEnumerable<object[]> VectorNames() => Vectors().Select(v => new object[] { v.Name });

    private static string? PhoneOf(string kind) => kind switch
    {
        "valid" => TestPhone.NewE164(),
        "invalid" => TestPhone.Digits(7),
        "none" => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "bilinmeyen telefon türü"),
    };

    private static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, j) => j != i).ToList();
            foreach (var p in Permutations(rest))
                yield return new[] { items[i] }.Concat(p).ToList();
        }
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Vektor_her_siralamada_ve_bilgisayara_ozgu_alanlardan_bagimsiz_ayni_sahibi_secer(string name)
    {
        var vector = Vectors().Single(v => v.Name == name);
        var members = vector.Members
            .Select(m => new PaymentPersonMember(m.Id, PhoneOf(m.Phone), m.Purged))
            .ToList();

        var random = new Random(name.Length);
        foreach (var order in Permutations(members))
        {
            // Ciro ve alıcı ödemeli bilgisayara özgü / sahip kuralı dışı: rastgele değerler sonucu değiştirmez.
            var noisy = order.Select(m => m with
            {
                TotalAmount = random.Next(0, 10_000),
                RecipientPaysActive = random.Next(2) == 1,
            }).ToList();

            PaymentOwnerRule.PickOwner(noisy).Should().Be(vector.Owner, $"vektör {name}");
            new PaymentPerson(noisy[0].Id, noisy).OwnerId.Should().Be(vector.Owner);
        }
    }

    [Fact]
    public void Kisi_anahtari_uye_sirasindan_bagimsizdir()
    {
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        var ab = new PaymentPerson(a, new[] { new PaymentPersonMember(a, null, false), new PaymentPersonMember(b, null, false) });
        var ba = new PaymentPerson(b, new[] { new PaymentPersonMember(b, null, false), new PaymentPersonMember(a, null, false) });

        ab.MemberKey.Should().Be(ba.MemberKey);
        ab.OwnerId.Should().Be(ba.OwnerId);
    }

    [Fact]
    public void Alici_odemeli_herhangi_bir_uyede_aciksa_kisi_alici_odemelidir()
    {
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        var kapali = new PaymentPersonMember(a, null, false, RecipientPaysActive: false);
        var acik = new PaymentPersonMember(b, null, false, RecipientPaysActive: true);

        new PaymentPerson(a, new[] { kapali, acik }).RecipientPaysActive.Should().BeTrue("kart girişi");
        new PaymentPerson(b, new[] { acik, kapali }).RecipientPaysActive.Should().BeTrue("rapor girişi");
        new PaymentPerson(a, new[] { kapali }).RecipientPaysActive.Should().BeFalse();
    }

    [Fact]
    public void Iletisim_telefonu_tiklanan_satirin_yoksa_kural_sirasindaki_ilk_gecerli_telefon()
    {
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        var aPhone = TestPhone.NewE164();
        var bPhone = TestPhone.NewE164();

        new PaymentPerson(a, new[] { new PaymentPersonMember(a, aPhone, false), new PaymentPersonMember(b, bPhone, false) })
            .ContactPhone.Should().Be(aPhone, "tıklanan satırın kendi geçerli telefonu önce");
        new PaymentPerson(a, new[] { new PaymentPersonMember(a, null, false), new PaymentPersonMember(b, bPhone, false) })
            .ContactPhone.Should().Be(bPhone, "telefonsuz üyeye tıklanınca kişinin telefonu kullanılır");
        new PaymentPerson(a, new[] { new PaymentPersonMember(a, TestPhone.Digits(7), false), new PaymentPersonMember(b, null, false) })
            .ContactPhone.Should().BeNull("geçersiz numara telefon sayılmaz");
    }

    [Fact]
    public void Silinmis_satira_tiklaninca_baska_uyenin_telefonuna_dusulmez()
    {
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        new PaymentPerson(a, new[] { new PaymentPersonMember(a, null, Purged: true), new PaymentPersonMember(b, TestPhone.NewE164(), false) })
            .ContactPhone.Should().BeNull("KVKK ile silinmiş kaydın tıklaması PhoneRequired kalır");
        new PaymentPerson(b, new[] { new PaymentPersonMember(a, TestPhone.NewE164(), Purged: true), new PaymentPersonMember(b, null, false) })
            .ContactPhone.Should().BeNull("silinmiş üyenin (boşaltılmamış görünse de) numarasına düşülmez");
    }
}
