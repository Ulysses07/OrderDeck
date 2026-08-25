using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWaMarketingPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WaMarketingPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BsuId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Preference = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PreferenceAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaMarketingPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaMarketingPreferences_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaMarketingPreferences_LicenseId_Category_BsuId",
                table: "WaMarketingPreferences",
                columns: new[] { "LicenseId", "Category", "BsuId" },
                unique: true,
                filter: "[BsuId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WaMarketingPreferences_LicenseId_Category_CustomerPhone",
                table: "WaMarketingPreferences",
                columns: new[] { "LicenseId", "Category", "CustomerPhone" },
                unique: true,
                filter: "[CustomerPhone] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaMarketingPreferences");
        }
    }
}
