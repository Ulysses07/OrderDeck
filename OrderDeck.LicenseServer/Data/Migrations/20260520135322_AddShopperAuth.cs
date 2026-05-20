using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShopperAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShopperRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShopperId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReplacedByTokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedByIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopperRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopperRefreshTokens_Shoppers_ShopperId",
                        column: x => x.ShopperId,
                        principalTable: "Shoppers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopperSupportRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShopperId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopperSupportRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopperSupportRequests_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShopperSupportRequests_Shoppers_ShopperId",
                        column: x => x.ShopperId,
                        principalTable: "Shoppers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopperRefreshTokens_ShopperId",
                table: "ShopperRefreshTokens",
                column: "ShopperId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopperRefreshTokens_TokenHash",
                table: "ShopperRefreshTokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_ShopperSupportRequests_LicenseId_ResolvedAt_CreatedAt",
                table: "ShopperSupportRequests",
                columns: new[] { "LicenseId", "ResolvedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopperSupportRequests_ShopperId",
                table: "ShopperSupportRequests",
                column: "ShopperId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShopperRefreshTokens");

            migrationBuilder.DropTable(
                name: "ShopperSupportRequests");
        }
    }
}
