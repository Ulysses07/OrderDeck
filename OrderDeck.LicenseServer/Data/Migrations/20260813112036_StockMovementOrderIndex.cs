using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class StockMovementOrderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockMovements_OrderId",
                table: "StockMovements");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_LicenseId_OrderId",
                table: "StockMovements",
                columns: new[] { "LicenseId", "OrderId" })
                .Annotation("SqlServer:Include", new[] { "ProductId", "ProductVariantId", "Quantity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockMovements_LicenseId_OrderId",
                table: "StockMovements");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_OrderId",
                table: "StockMovements",
                column: "OrderId");
        }
    }
}
