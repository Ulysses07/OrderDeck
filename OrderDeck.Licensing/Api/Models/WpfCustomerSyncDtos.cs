namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// WPF → sunucu müşteri gönderimi. Alan adları sunucudaki
/// <c>LicensesWpfCustomersSyncController.SyncItem</c> ile BİREBİR (PR-1). İlk yedi alan
/// eski (biçim 1) sözleşme; konumsal kurucuyla yazılmış çağıranlar derlenmeye devam etsin
/// diye başta. <see cref="Format"/> varsayılanı 2 — bu istemci yalnız biçim 2 gönderir
/// (tam alan + BİRİM damgaları; sunucu biçim 1'i eski sürümler için tanır).
/// Damgalar UTC, ms hassasiyetinde (yerelde unix ms; C6 çevirir). <see cref="Tckn"/> DÜZ
/// metin — sunucu şifreler; 11 karakteri aşarsa birim hiç gelmemiş sayılır.
/// </summary>
public sealed record WpfCustomerSyncItem(
    Guid Id,
    string Platform,
    string Username,
    string? FullName,
    string? Phone,
    string? Address,
    DateTimeOffset UpdatedAt,
    int Format = 2,
    DateTimeOffset? FullNameChangedAt = null,
    string? DisplayName = null,
    DateTimeOffset? DisplayNameChangedAt = null,
    string? GroupId = null,
    DateTimeOffset? GroupIdChangedAt = null,
    string? City = null,
    string? District = null,
    DateTimeOffset? AddressChangedAt = null,
    bool RecipientPaysActive = false,
    DateTimeOffset? RecipientPaysChangedAt = null,
    DateTimeOffset? PhoneChangedAt = null,
    string? Email = null,
    DateTimeOffset? EmailChangedAt = null,
    string? Tckn = null,
    DateTimeOffset? TcknChangedAt = null,
    bool WhatsAppConsent = false,
    DateTimeOffset? WhatsAppConsentChangedAt = null,
    bool SmsConsent = false,
    DateTimeOffset? SmsConsentChangedAt = null,
    bool IsBlacklisted = false,
    string? BlacklistReason = null,
    DateTimeOffset? BlacklistedAt = null,
    DateTimeOffset? BlacklistChangedAt = null,
    string? Notes = null,
    DateTimeOffset? NotesChangedAt = null);

public sealed record WpfCustomerSyncRequest(IReadOnlyList<WpfCustomerSyncItem> Customers);

/// <summary>Kopya olarak bağlanan Id → asıl kayıt (sunucu: <c>SyncRedirect</c>). İstemci
/// yerel satırı asıl kaydın Id'sine taşır (C4 <c>RekeyToLocal</c>).</summary>
public sealed record WpfCustomerRedirect(Guid Id, Guid CanonicalId);

/// <param name="Redirects">PR-1 öncesi sunucu alanı göndermez → null.</param>
public sealed record WpfCustomerSyncResponse(
    int Synced, int RetroactiveMatches, IReadOnlyList<WpfCustomerRedirect>? Redirects = null);

/// <summary>
/// <c>GET …/wpf-customers/changes</c> satırı — sunucudaki <c>WpfCustomerChangeItem</c> ile
/// birebir. Kopya satırı (<see cref="MergedIntoId"/> dolu) YALNIZ yönlendirmedir: öbür her
/// alan ve damga boş/false — <see cref="PurgedAt"/> ve <see cref="CreatedByShopper"/> dahil;
/// asla veri ya da mezar taşı sayılmaz. <see cref="Tckn"/> düz (sunucu çözer; çözülemezse null).
/// </summary>
public sealed record WpfCustomerChangeItem(
    Guid Id, string Platform, string Username, Guid? MergedIntoId, DateTimeOffset? PurgedAt,
    string? FullName, DateTimeOffset? FullNameChangedAt,
    string? DisplayName, DateTimeOffset? DisplayNameChangedAt,
    string? GroupId, DateTimeOffset? GroupIdChangedAt,
    string? Address, string? City, string? District, DateTimeOffset? AddressChangedAt,
    bool RecipientPaysActive, DateTimeOffset? RecipientPaysChangedAt,
    string? Phone, DateTimeOffset? PhoneChangedAt,
    string? Email, DateTimeOffset? EmailChangedAt,
    string? Tckn, DateTimeOffset? TcknChangedAt,
    bool WhatsAppConsent, DateTimeOffset? WhatsAppConsentChangedAt,
    bool SmsConsent, DateTimeOffset? SmsConsentChangedAt,
    bool IsBlacklisted, string? BlacklistReason, DateTimeOffset? BlacklistedAt, DateTimeOffset? BlacklistChangedAt,
    string? Notes, DateTimeOffset? NotesChangedAt,
    long ChangeSeq,
    bool CreatedByShopper = false);

/// <param name="NextAfterSeq">Sonraki istek için imleç (boş sayfada istekteki değer; CursorReset'te 0).</param>
/// <param name="CursorReset">Sunucu imleci geçersiz buldu (eksi ya da ufkun üstü —
/// veritabanı yedekten dönmüş ya da kopyalanmış olabilir) ve sayfayı BAŞTAN verdi.</param>
public sealed record WpfCustomerChangesPage(
    IReadOnlyList<WpfCustomerChangeItem> Items, long NextAfterSeq, bool CursorReset = false);

/// <summary>Single item returned by the server-side /wpf-customers/since pull
/// endpoint (Faz 0c-3). Auto-created on shopper register/join.</summary>
/// <param name="PurgedAt">
/// Doluysa bu kişi KVKK kapsamında silinmiştir ve sunucudaki kişisel alanları
/// zaten boştur. WPF için tek silme sinyali bu: <c>ShopperRegistrationIngestService</c>
/// yerel satırı temizler, yeni satır AÇMAZ.
/// </param>
public sealed record WpfCustomerPullItem(
    Guid Id,
    string Platform,
    string Username,
    string? FullName,
    string? Phone,
    string? Address,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PurgedAt = null);
