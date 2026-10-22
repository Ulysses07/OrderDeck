using System.Reflection;
using Microsoft.Data.SqlClient;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Gerçek bir <see cref="SqlException"/> üretir. Genel ctor'u yok — SQL Server'ın kendisi dışında kimse kuramasın diye
/// kapalı. Yansımayla kuruyoruz çünkü alternatifin ikisi de kötü: ya hata tanıyan süzgeçlerin ASIL mantığı (hata
/// numarası, indeks adı) hiç sınanmaz, ya da üretim kodu yalnız test uğruna ikinci bir imzaya bölünür.
///
/// <para>Numara <see cref="SqlException.Errors"/>'taki tek <c>SqlError</c>'a yazılır; <see cref="SqlException.Number"/>
/// ve EF'in geçici hata dedektörü (<c>SqlServerTransientExceptionDetector</c>) onu oradan okur. Bu yüzden komut
/// kancasından fırlatılan bir örnek EF hattında gerçeğiyle aynı biçimde sarılır.</para>
/// </summary>
internal static class SqlExceptionFactory
{
    public static SqlException Create(int number, string message)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var asm = typeof(SqlException).Assembly;

        var errorType = asm.GetType("Microsoft.Data.SqlClient.SqlError")!;
        var error = errorType.GetConstructor(flags, new[]
        {
            typeof(int), typeof(byte), typeof(byte), typeof(string),
            typeof(string), typeof(string), typeof(int), typeof(Exception),
        })!.Invoke(new object?[] { number, (byte)0, (byte)0, "test", message, "", 0, null });

        var collectionType = asm.GetType("Microsoft.Data.SqlClient.SqlErrorCollection")!;
        var collection = collectionType.GetConstructor(flags, Type.EmptyTypes)!.Invoke(null);
        collectionType.GetMethod("Add", flags)!.Invoke(collection, new[] { error });

        return (SqlException)typeof(SqlException).GetMethod(
                "CreateException",
                BindingFlags.Static | BindingFlags.NonPublic,
                new[] { collectionType, typeof(string) })!
            .Invoke(null, new[] { collection, "11.0.0" })!;
    }
}
