using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentShipmentDirective : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ShipmentDirective",
                table: "Payments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LicenseId_ShipmentDirective_Status",
                table: "Payments",
                columns: new[] { "LicenseId", "ShipmentDirective", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_LicenseId_ShipmentDirective_Status",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ShipmentDirective",
                table: "Payments");
        }
    }
}
