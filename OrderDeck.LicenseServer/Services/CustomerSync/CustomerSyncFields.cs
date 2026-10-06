using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Bir müşterinin senkronlanan alanları ve her BİRİMİN damgası (istemcide
/// düzenleme anı). Birim = tek alan, ya da ayrılırsa anlamsızlaşan blok:
/// adres (Address+City+District, <see cref="AddressChangedAt"/>) ve kara
/// liste (IsBlacklisted+BlacklistReason+BlacklistedAt,
/// <see cref="BlacklistChangedAt"/>).
///
/// <para>Neden grup değil birim (2026-10-06, A3 kalite incelemesi): grup
/// başına tek damgada, güncel veriyi henüz almamış bir bilgisayardaki TEK
/// alan değişikliği (ör. dekont girilince otomatik "alıcı ödemeli") grubun
/// hiç dokunulmamış alanlarını da "şimdi değişti" diye gönderip başka
/// bilgisayarın girdiği adresi/TCKN'yi boşla siliyordu.</para>
///
/// <para><see cref="TcknProtected"/> HER ZAMAN şifreli metindir
/// (TcknProtector) ve asla kırpılmaz.</para>
/// </summary>
public sealed record CustomerSyncFields
{
    public string? FullName { get; init; }
    public DateTimeOffset? FullNameChangedAt { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset? DisplayNameChangedAt { get; init; }
    public string? GroupId { get; init; }
    public DateTimeOffset? GroupIdChangedAt { get; init; }

    public string? Address { get; init; }
    public string? City { get; init; }
    public string? District { get; init; }
    public DateTimeOffset? AddressChangedAt { get; init; }
    public bool RecipientPaysActive { get; init; }
    public DateTimeOffset? RecipientPaysChangedAt { get; init; }

    public string? Phone { get; init; }
    public DateTimeOffset? PhoneChangedAt { get; init; }
    public string? Email { get; init; }
    public DateTimeOffset? EmailChangedAt { get; init; }
    public string? TcknProtected { get; init; }
    public DateTimeOffset? TcknChangedAt { get; init; }
    public bool WhatsAppConsent { get; init; }
    public DateTimeOffset? WhatsAppConsentChangedAt { get; init; }
    public bool SmsConsent { get; init; }
    public DateTimeOffset? SmsConsentChangedAt { get; init; }

    public bool IsBlacklisted { get; init; }
    public string? BlacklistReason { get; init; }
    public DateTimeOffset? BlacklistedAt { get; init; }
    public DateTimeOffset? BlacklistChangedAt { get; init; }

    public string? Notes { get; init; }
    public DateTimeOffset? NotesChangedAt { get; init; }

    public static CustomerSyncFields From(WpfCustomerProjection p) => new()
    {
        FullName = p.FullName, FullNameChangedAt = p.FullNameChangedAt,
        DisplayName = p.DisplayName, DisplayNameChangedAt = p.DisplayNameChangedAt,
        GroupId = p.GroupId, GroupIdChangedAt = p.GroupIdChangedAt,
        Address = p.Address, City = p.City, District = p.District, AddressChangedAt = p.AddressChangedAt,
        RecipientPaysActive = p.RecipientPaysActive, RecipientPaysChangedAt = p.RecipientPaysChangedAt,
        Phone = p.Phone, PhoneChangedAt = p.PhoneChangedAt,
        Email = p.Email, EmailChangedAt = p.EmailChangedAt,
        TcknProtected = p.TcknProtected, TcknChangedAt = p.TcknChangedAt,
        WhatsAppConsent = p.WhatsAppConsent, WhatsAppConsentChangedAt = p.WhatsAppConsentChangedAt,
        SmsConsent = p.SmsConsent, SmsConsentChangedAt = p.SmsConsentChangedAt,
        IsBlacklisted = p.IsBlacklisted, BlacklistReason = p.BlacklistReason,
        BlacklistedAt = p.BlacklistedAt, BlacklistChangedAt = p.BlacklistChangedAt,
        Notes = p.Notes, NotesChangedAt = p.NotesChangedAt,
    };
}
