using System;
using System.Collections.Generic;
using System.Linq;

namespace OrderDeck.Core.Customers;

/// <summary>Ödeme kişisinin bir üyesi (Customer satırı) — sahip kuralının ve kişi düzeyindeki
/// iletişim/kargo kararlarının girdisi. Yalnız senkronlanan ve yakınsayan alanlar: liste sırası,
/// <c>LastSeenAt</c> gibi bilgisayara özgü veriler bilerek YOK (iki bilgisayar aynı sahibi seçsin).
/// <see cref="TotalAmount"/> yalnız kartın kümülatif tutarı içindir, sahip kuralına girmez.</summary>
public sealed record PaymentPersonMember(
    string Id,
    string? Phone,
    bool Purged,
    bool RecipientPaysActive = false,
    decimal TotalAmount = 0m);

/// <summary>
/// "Ödeme iste"nin kişisi: tıklanan (kiralanan) satır + aynı kişinin bütün satırları
/// (<see cref="CustomerGroup"/>). Ödeme işinin mantıksal kimliği (kişi, kapsam)'tır; işin
/// <c>CustomerId</c>'si yalnız sahip satırdır (para anahtarı). Birincil kart satırı ne sebeple
/// değişirse değişsin (telefon, arama sırası, birleştirme) kişi aynı kaldıkça aynı iş bulunur.
///
/// <para>Üye kümesi çağrı anının anlık görüntüsüdür; <see cref="MemberKey"/> ödeme işinin
/// bulunduğu/açıldığı yazma işleminin İÇİNDE yeniden doğrulanır (uyuşmazsa akış yeniden okur).</para>
/// </summary>
public sealed class PaymentPerson
{
    public PaymentPerson(string leasedId, IReadOnlyCollection<PaymentPersonMember> members)
    {
        LeasedId = leasedId;
        Members = PaymentOwnerRule.Order(members);
        MemberIds = Members.Select(m => m.Id).ToList();
        MemberKey = KeyOf(MemberIds);
        OwnerId = PaymentOwnerRule.PickOwner(Members);
    }

    /// <summary>Tıklanan ve kiralanan satırın güncel Id'si (iletişim satırı).</summary>
    public string LeasedId { get; }

    /// <summary>Üyeler, sahip kuralı sırasıyla (<see cref="PaymentOwnerRule.Order"/>).</summary>
    public IReadOnlyList<PaymentPersonMember> Members { get; }

    public IReadOnlyList<string> MemberIds { get; }

    /// <summary>Üye kümesinin sıradan bağımsız anahtarı — işlem içi doğrulama bununla yapılır.</summary>
    public string MemberKey { get; }

    /// <summary>Yeni bir iş açılacaksa sahibi (<see cref="PaymentOwnerRule.PickOwner"/>); GUID'li
    /// üye yoksa null — o kişide bakiye işi açılmaz (sunucu GUID olmayan Id'ye düşüm yazamaz).</summary>
    public string? OwnerId { get; }

    /// <summary>Kargo kararı kişi düzeyinde: üyelerden HERHANGİ birinde "alıcı ödemeli" açıksa kişi
    /// alıcı ödemelidir (sahip kararı, 2026-10-10). Kiralanan satırdan okunsaydı kart ile rapor farklı
    /// satırları kiraladığında toplam iki giriş arasında gidip gelir, aynı iş her tıklamada revize
    /// edilirdi.</summary>
    public bool RecipientPaysActive => Members.Any(m => m.RecipientPaysActive);

    /// <summary>Mesajın gideceği numara: tıklanan satırın geçerli telefonu; yoksa sahip kuralı
    /// sırasındaki ilk silinmemiş üyenin geçerli telefonu. Tıklanan satır KVKK ile silinmişse
    /// null — silinmiş kaydın tıklamasıyla başka satırın numarasına mesaj gitmez (bugünkü davranış:
    /// <c>PhoneRequired</c>).</summary>
    public string? ContactPhone
    {
        get
        {
            var leased = Members.FirstOrDefault(m => string.Equals(m.Id, LeasedId, StringComparison.Ordinal));
            if (leased is not null)
            {
                if (PhoneNormalizer.IsValidTr(leased.Phone)) return leased.Phone;
                if (leased.Purged) return null;
            }
            return Members.FirstOrDefault(m => !m.Purged && PhoneNormalizer.IsValidTr(m.Phone))?.Phone;
        }
    }

    /// <summary>Tek satırlık kişi (grup okunamadığında ya da test kurulumunda).</summary>
    public static PaymentPerson Single(Customer customer)
        => new(customer.Id, new[]
        {
            new PaymentPersonMember(customer.Id, customer.Phone, Purged: false,
                customer.RecipientPaysActive, customer.TotalAmount),
        });

    /// <summary>Üye Id'lerinin sıradan bağımsız anahtarı.</summary>
    public static string KeyOf(IEnumerable<string> memberIds)
        => string.Join("|", memberIds.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal));
}

/// <summary>
/// Yeni ödeme işinin sahip satırı — saf ve belirlenimli. Sıra:
/// <list type="number">
/// <item>GUID "N" biçimli Id (sunucu yalnız böyle Id'ye düşüm yazar; GUID'siz üye hiç sahip olmaz),</item>
/// <item>silinmemiş (<c>PurgedAt IS NULL</c>) önce,</item>
/// <item>geçerli TR telefonu olan önce (<see cref="PhoneNormalizer.IsValidTr"/> — <c>PhoneRequired</c>
///   kapısıyla aynı kural),</item>
/// <item>Id (küçük harfe indirilmiş, ordinal), eşitlikte ham Id (ordinal).</item>
/// </list>
/// <para>Liste sırası, <c>TotalAmount</c>, <c>LastSeenAt</c> KULLANILMAZ: senkron yakınsadıktan sonra
/// iki bilgisayar aynı kişi ve kapsam için aynı (WpfCustomerId, ScopeKey) çiftini seçer. Telefon
/// önceliği tek telefonlu grupta bugünkü kart birincilini korur.</para>
/// <para>Kural yalnız YENİ işin sahibini seçer: telefonu sonradan değişen bir üye var olan işin
/// sahibini değiştirmez (iş üye taramasıyla bulunur).</para>
/// </summary>
public static class PaymentOwnerRule
{
    /// <summary>Id sunucuya müşteri kimliği (WpfCustomerId) olarak gidebilir mi.</summary>
    public static bool IsOwnerCapable(string id) => Guid.TryParseExact(id, "N", out _);

    /// <summary>Üyeleri sahip kuralı sırasına dizer (GUID'siz üyeler sona; iletişim için yine
    /// listede kalırlar).</summary>
    public static IReadOnlyList<PaymentPersonMember> Order(IEnumerable<PaymentPersonMember> members)
        => members
            .GroupBy(m => m.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(m => IsOwnerCapable(m.Id) ? 0 : 1)
            .ThenBy(m => m.Purged ? 1 : 0)
            .ThenBy(m => PhoneNormalizer.IsValidTr(m.Phone) ? 0 : 1)
            .ThenBy(m => m.Id.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>Sahip satırın Id'si; GUID'li üye yoksa null.</summary>
    public static string? PickOwner(IEnumerable<PaymentPersonMember> members)
        => Order(members).FirstOrDefault(m => IsOwnerCapable(m.Id))?.Id;
}
