using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShopperRefreshTokenAuthVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthVersion",
                table: "ShopperRefreshTokens",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // R10-S01 backfill: mevcut token'lar sahibinin GÜNCEL nesliyle
            // damgalanır. Varsayılan 0'da bırakılsaydı, geçmişte parola
            // değiştirmiş (AuthVersion>0) kullanıcıların meşru oturumları ilk
            // yenilemede topluca düşerdi; geçmiş değişikliklerin iptali zaten
            // süpürmeyle yapılmıştı — bu damga yalnız bundan sonraki yarışları
            // yakalamak için var.
            migrationBuilder.Sql("""
                UPDATE t SET t.AuthVersion = s.AuthVersion
                FROM ShopperRefreshTokens t
                JOIN Shoppers s ON s.Id = t.ShopperId;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthVersion",
                table: "ShopperRefreshTokens");
        }
    }
}
