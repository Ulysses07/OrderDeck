using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShopperBroadcasterLinkFilteredIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShopperBroadcasterLinks_ShopperId_LicenseId",
                table: "ShopperBroadcasterLinks");

            migrationBuilder.CreateIndex(
                name: "IX_ShopperBroadcasterLinks_ShopperId_LicenseId",
                table: "ShopperBroadcasterLinks",
                columns: new[] { "ShopperId", "LicenseId" },
                unique: true,
                filter: "[LeftAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShopperBroadcasterLinks_ShopperId_LicenseId",
                table: "ShopperBroadcasterLinks");

            migrationBuilder.CreateIndex(
                name: "IX_ShopperBroadcasterLinks_ShopperId_LicenseId",
                table: "ShopperBroadcasterLinks",
                columns: new[] { "ShopperId", "LicenseId" },
                unique: true);
        }
    }
}
