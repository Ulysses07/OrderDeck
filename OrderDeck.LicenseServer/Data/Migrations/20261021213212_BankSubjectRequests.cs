using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class BankSubjectRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankSubjectRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IbanHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IbanMasked = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Verification = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankSubjectRequests", x => x.Id);
                    table.CheckConstraint("CK_BankSubjectRequests_IbanHash_Hex64", "LEN([IbanHash]) = 64 AND [IbanHash] NOT LIKE '%[^0-9a-f]%'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankSubjectRequests_ClosedAt_DueAt",
                table: "BankSubjectRequests",
                columns: new[] { "ClosedAt", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BankSubjectRequests_LicenseId",
                table: "BankSubjectRequests",
                column: "LicenseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankSubjectRequests");
        }
    }
}
