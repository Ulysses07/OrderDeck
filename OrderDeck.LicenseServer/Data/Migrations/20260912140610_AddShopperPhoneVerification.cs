using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShopperPhoneVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthVersion",
                table: "Shoppers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PhoneVerifiedAt",
                table: "Shoppers",
                type: "datetimeoffset",
                nullable: true);

            // Eski bağlantıların hangi sahiplik kanıtıyla kurulduğunu ayırt
            // edecek provenance yok. Hesapları topluca doğrulanmış saymak ilk
            // kayıt açığını kalıcılaştırır; aktif yayıncı ilişkisini koruyup
            // yalnız tarihsel WPF eşlemesini OTP doğrulamasına kadar kaldır.
            migrationBuilder.Sql(
                "UPDATE [ShopperBroadcasterLinks] SET [WpfCustomerId] = NULL " +
                "WHERE [WpfCustomerId] IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthVersion",
                table: "Shoppers");

            migrationBuilder.DropColumn(
                name: "PhoneVerifiedAt",
                table: "Shoppers");
        }
    }
}
