using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductBroadcastCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductBroadcastCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerAxisValue = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CodeNormalized = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductBroadcastCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductBroadcastCodes_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductBroadcastCodes_LicenseId_CodeNormalized",
                table: "ProductBroadcastCodes",
                columns: new[] { "LicenseId", "CodeNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductBroadcastCodes_ProductId_CreatedAt",
                table: "ProductBroadcastCodes",
                columns: new[] { "ProductId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductBroadcastCodes");
        }
    }
}
