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
/// (<see cref="StockStaffScopeFilter"/>). Controller ya da action seviyesinde
/// kullanılabilir.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AllowStockStaffAttribute : Attribute;
