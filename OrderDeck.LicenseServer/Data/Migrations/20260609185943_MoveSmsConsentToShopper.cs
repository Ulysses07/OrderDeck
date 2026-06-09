using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveSmsConsentToShopper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmsConsent",
                table: "WpfCustomerProjections");

            migrationBuilder.AddColumn<bool>(
                name: "SmsConsent",
                table: "Shoppers",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmsConsent",
                table: "Shoppers");

            migrationBuilder.AddColumn<bool>(
                name: "SmsConsent",
                table: "WpfCustomerProjections",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
