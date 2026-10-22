using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class ObifinConnectionServiceEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BankDataPurgedAt",
                table: "ObifinConnections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByAdmin",
                table: "ObifinConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ObifinCopyDeletions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObifinCopyDeletions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObifinCopyDeletions_LicenseId",
                table: "ObifinCopyDeletions",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_ObifinCopyDeletions_Pending",
                table: "ObifinCopyDeletions",
                column: "RequestedAt",
                filter: "[ConfirmedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObifinCopyDeletions");

            migrationBuilder.DropColumn(
                name: "BankDataPurgedAt",
                table: "ObifinConnections");

            migrationBuilder.DropColumn(
                name: "DisabledByAdmin",
                table: "ObifinConnections");
        }
    }
}
