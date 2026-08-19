using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppTwoStepPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TwoStepPinProtected",
                table: "WhatsAppAccounts",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TwoStepPinProtected",
                table: "WhatsAppAccounts");
        }
    }
}
