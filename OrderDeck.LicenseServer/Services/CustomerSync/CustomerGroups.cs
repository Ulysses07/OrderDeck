using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

// Beş alan grubu — çakışma birimi. Bir grubun alanları birlikte yazılır ve
// tek damga taşır; farklı gruplar birbirini ezmez (A adresi, B kara listeyi
// değiştirirse ikisi de korunur). Grup sınırları spec'te.
public sealed record IdentityGroup(string? FullName, string? DisplayName, string? GroupId, DateTimeOffset? ChangedAt);
public sealed record AddressGroup(string? Address, string? City, string? District, bool RecipientPaysActive, DateTimeOffset? ChangedAt);
/// <summary>TcknProtected HER ZAMAN şifreli metindir (TcknProtector) ve asla kırpılmaz.</summary>
public sealed record ContactGroup(string? Phone, string? Email, string? TcknProtected, bool WhatsAppConsent, bool SmsConsent, DateTimeOffset? ChangedAt);
public sealed record BlacklistGroup(bool IsBlacklisted, string? Reason, DateTimeOffset? BlacklistedAt, DateTimeOffset? ChangedAt);
public sealed record NotesGroup(string? Notes, DateTimeOffset? ChangedAt);

public sealed record CustomerGroups(
    IdentityGroup Identity,
    AddressGroup Address,
    ContactGroup Contact,
    BlacklistGroup Blacklist,
    NotesGroup Notes)
{
    public static CustomerGroups From(WpfCustomerProjection p) => new(
        new IdentityGroup(p.FullName, p.DisplayName, p.GroupId, p.IdentityChangedAt),
        new AddressGroup(p.Address, p.City, p.District, p.RecipientPaysActive, p.AddressChangedAt),
        new ContactGroup(p.Phone, p.Email, p.TcknProtected, p.WhatsAppConsent, p.SmsConsent, p.ContactChangedAt),
        new BlacklistGroup(p.IsBlacklisted, p.BlacklistReason, p.BlacklistedAt, p.BlacklistChangedAt),
        new NotesGroup(p.Notes, p.NotesChangedAt));
}
