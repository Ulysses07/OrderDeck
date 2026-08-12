namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// <c>OperatorUser.Role</c> sütununun kabul ettiği değerler.
/// <c>owner</c> davetle verilemez — lisans sahibi zaten Customer token'ı taşır,
/// sütunda yalnız tarihsel kayıtlar için durur.
/// </summary>
public static class OperatorRoles
{
    public const string Owner = "owner";
    public const string Staff = "staff";
    public const string Stock = "stock";

    /// <summary>Davetle atanabilir roller.</summary>
    public static bool IsAssignable(string? role) => role is Staff or Stock;
}

/// <summary>
/// Bu uç <c>stock</c> rolündeki operatöre açık. İşaretlenmemiş her uç kapalıdır
/// (<see cref="StockStaffScopeFilter"/>).
///
/// YALNIZ action seviyesinde kullanılabilir; sınıf hedefi bilerek kaldırıldı.
/// Sınıfa yazılan bir izin, o controller'a yarın eklenen action'ı da — kimse
/// bir şey yazmadan, kimse farkına varmadan — açar; yani "varsayılan olarak
/// her uç kapalı" kuralı sınıf granülaritesinde delinir. Bunu bir teste değil
/// derleyiciye bağlamak tek güvenli hâl: sınıfa yazan derlenemez.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AllowStockStaffAttribute : Attribute;
