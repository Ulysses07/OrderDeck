using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShopperDeletionRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Shoppers_Phone",
                table: "Shoppers");

            migrationBuilder.CreateTable(
                name: "ShopperDeletionRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShopperId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneAtRequest = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HandledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HandledBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopperDeletionRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Shoppers_Phone",
                table: "Shoppers",
                column: "Phone",
                unique: true,
                filter: "[DeletedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopperDeletionRequests_HandledAt_RequestedAt",
                table: "ShopperDeletionRequests",
                columns: new[] { "HandledAt", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopperDeletionRequests_ShopperId",
                table: "ShopperDeletionRequests",
                column: "ShopperId",
                unique: true,
                filter: "[HandledAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShopperDeletionRequests");

            migrationBuilder.DropIndex(
                name: "IX_Shoppers_Phone",
                table: "Shoppers");

            migrationBuilder.CreateIndex(
                name: "IX_Shoppers_Phone",
                table: "Shoppers",
                column: "Phone",
                unique: true);
        }
    }
}
