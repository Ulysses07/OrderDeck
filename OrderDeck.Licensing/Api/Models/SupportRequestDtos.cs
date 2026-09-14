namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// WPF tarafının /api/panel/support-requests endpoint'leri için DTO'ları.
/// Server: PanelSupportRequestsController.SupportRequestDto ile aynı alanlar.
/// </summary>
public sealed record SupportRequestDto(
    Guid Id,
    Guid LicenseId,
    Guid ShopperId,
    string ShopperName,
    string ShopperPhone,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>
/// R7-02 sonrası sözleşme: sunucu parolayı ARTIK DÖNDÜRMEZ; kendisi SMS OTP
/// gönderir ve <c>Status = "verification-sent"</c> döner. TempPassword alanı
/// yalnız eski sunucuyla karşılaşma ihtimaline karşı nullable duruyor —
/// istemci başarıyı yalnız Status üzerinden okur.
/// </summary>
public sealed record IssueTempPasswordResponse(string? TempPassword, string? Status);
