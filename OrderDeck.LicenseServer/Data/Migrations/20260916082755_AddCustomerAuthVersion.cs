using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// R11-S01: yayıncı/Customer kolunda parola nesli damgası.
    ///
    /// Backfill YOK ve gerekli değil — Shopper eşinden (20260915172225) farklı
    /// olarak <c>Customer.AuthVersion</c> da bu göçle DOĞUYOR. İki kolon da 0
    /// ile açılır, yani mevcut her token sahibinin güncel nesliyle zaten
    /// eşleşir; sahadaki meşru oturumların hiçbiri düşmez. Shopper tarafında
    /// backfill gerekiyordu, çünkü orada nesil sütunu token sütunundan önce
    /// vardı ve bazı hesaplarda çoktan ilerlemişti.
    /// </summary>
    public partial class AddCustomerAuthVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthVersion",
                table: "RefreshTokens",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthVersion",
                table: "Customers",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthVersion",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "AuthVersion",
                table: "Customers");
        }
    }
}
