using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWaDroppedInbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WaDroppedInbounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BsuId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PhoneNumberId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MessageCount = table.Column<int>(type: "int", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaDroppedInbounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaDroppedInbounds_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaDroppedInbounds_LicenseId_BsuId",
                table: "WaDroppedInbounds",
                columns: new[] { "LicenseId", "BsuId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaDroppedInbounds");
        }
    }
}
