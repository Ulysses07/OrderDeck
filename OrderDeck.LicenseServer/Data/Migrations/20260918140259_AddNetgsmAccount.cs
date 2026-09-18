using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNetgsmAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetgsmAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PasswordProtected = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Header = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BrandCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetgsmAccounts", x => x.Id);
                    table.CheckConstraint("CK_NetgsmAccounts_BrandCode", "LEN([BrandCode]) > 0");
                    table.ForeignKey(
                        name: "FK_NetgsmAccounts_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts",
                column: "BrandCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetgsmAccounts_LicenseId",
                table: "NetgsmAccounts",
                column: "LicenseId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetgsmAccounts");
        }
    }
}
