namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncı ekibinin ek üyeleri. Owner Customer dışında License sahibinin
/// ekleyebildiği "personel" hesapları için. Faz 1 (2026-05-14): sadece
/// entity + invite/list/delete endpoint'leri.
///
/// Faz 2 (sonraki PR): bu kullanıcılar için auth flow + JWT issuance,
/// claim'lerde "operatorId" + "licenseId" eklenir, tüm panel controller'ları
/// "tenant resolve" helper'ı üzerinden License.Id'yi alır.
///
/// Şu an entity persist edilir ama login akışında kullanılmaz — placeholder.
/// </summary>
public sealed class OperatorUser
{
    public Guid Id { get; set; }

    /// <summary>Bağlı olduğu License (owner Customer'ın lisansı). Her operator
    /// tam olarak bir License'a aittir.</summary>
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";

    /// <summary>"owner" veya "staff". Owner asla bu tabloya kaydedilmez —
    /// owner Customer entity'sinde, bu tablo sadece staff için. Yine de role
    /// alanı ileri faz için tutuluyor.</summary>
    public string Role { get; set; } = "staff";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
