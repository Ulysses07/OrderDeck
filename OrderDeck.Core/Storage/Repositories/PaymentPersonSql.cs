using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using OrderDeck.Core.Customers;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Ödeme kişisinin üye okuması — TEK tanım (<see cref="CustomerGroup"/>). Hem
/// <see cref="CustomerRepository.GetPaymentPerson"/> (akışın girişindeki anlık görüntü) hem
/// <see cref="PaymentJobRepository.FindOrCreateForPerson"/> (yazma işleminin İÇİNDEKİ doğrulama)
/// bunu çağırır; ikisi farklı normalize etseydi işlem içi doğrulama hiç tutmazdı.
///
/// <para>İki adım bilerek: grup denetimi C#'ta (<see cref="CustomerGroup.IsGrouped"/>) yapılır, çünkü
/// SQLite'ın <c>TRIM</c>'i yalnız boşluk karakterini kırpar; kartın <c>IsNullOrWhiteSpace</c>'iyle
/// birebir aynı sonucu vermezdi.</para>
/// </summary>
internal static class PaymentPersonSql
{
    /// <summary><paramref name="customerId"/>'nin kişisinin bütün üyeleri (satırın kendisi dahil).
    /// Satır yoksa boş liste. Çağıranın bağlantısı ve işlemiyle (U17: açık paket içinde ikinci
    /// bağlantı açılmaz).</summary>
    public static IReadOnlyList<PaymentPersonMember> ReadMembers(
        IDbConnection conn, IDbTransaction? tx, string customerId)
    {
        var groupId = conn.QueryFirstOrDefault<string?>(
            "SELECT GroupId FROM Customer WHERE Id = @customerId", new { customerId }, tx);
        var rows = CustomerGroup.IsGrouped(groupId)
            ? conn.Query<Row>(Columns + " WHERE GroupId = @groupId OR Id = @customerId",
                new { groupId, customerId }, tx)
            : conn.Query<Row>(Columns + " WHERE Id = @customerId", new { customerId }, tx);
        return rows
            .Select(r => new PaymentPersonMember(
                r.Id, r.Phone, r.PurgedAt is not null, r.RecipientPaysActive != 0, r.TotalAmount))
            .ToList();
    }

    private const string Columns =
        "SELECT Id, Phone, PurgedAt, RecipientPaysActive, TotalAmount FROM Customer";

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string? Phone { get; init; }
        public long? PurgedAt { get; init; }
        public int RecipientPaysActive { get; init; }
        public decimal TotalAmount { get; init; }
    }
}
