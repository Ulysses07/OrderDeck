using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class NetgsmBrandCodeFilteredUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts");

            migrationBuilder.CreateIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts",
                column: "BrandCode",
                unique: true,
                filter: "[Status] = 'Verified'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts");

            migrationBuilder.CreateIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts",
                column: "BrandCode",
                unique: true);
        }
    }
}
