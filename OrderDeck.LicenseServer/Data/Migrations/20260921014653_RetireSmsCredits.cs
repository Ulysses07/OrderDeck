using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Prod'da her iki tablo 0 satır (spec §1.4 ölçümü) — göç değil temiz
    /// silme. Down tabloları boş şemayla geri kurar; veri geri gelmez
    /// (gelecek veri yok).
    /// </remarks>
    public partial class RetireSmsCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenseSmsBalances");

            migrationBuilder.DropTable(
                name: "LicenseSmsTransactions");

            migrationBuilder.DropColumn(
                name: "RefundedCredits",
                table: "SmsCampaigns");

            migrationBuilder.DropColumn(
                name: "ReservedCredits",
                table: "SmsCampaigns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RefundedCredits",
                table: "SmsCampaigns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReservedCredits",
                table: "SmsCampaigns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LicenseSmsBalances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreditsRemaining = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseSmsBalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LicenseSmsBalances_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LicenseSmsTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseSmsTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LicenseSmsTransactions_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseSmsBalances_LicenseId",
                table: "LicenseSmsBalances",
                column: "LicenseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LicenseSmsTransactions_LicenseId_CreatedAt",
                table: "LicenseSmsTransactions",
                columns: new[] { "LicenseId", "CreatedAt" });
        }
    }
}
