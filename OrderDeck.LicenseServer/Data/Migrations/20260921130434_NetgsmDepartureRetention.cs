using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class NetgsmDepartureRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisabledAt",
                table: "NetgsmAccounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NetgsmDepartures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DepartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsentsDeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PurgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetgsmDepartures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetgsmDepartures_BrandCode",
                table: "NetgsmDepartures",
                column: "BrandCode");

            migrationBuilder.CreateIndex(
                name: "IX_NetgsmDepartures_PurgedAt_DepartedAt",
                table: "NetgsmDepartures",
                columns: new[] { "PurgedAt", "DepartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetgsmDepartures");

            migrationBuilder.DropColumn(
                name: "DisabledAt",
                table: "NetgsmAccounts");
        }
    }
}
