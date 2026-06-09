using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSmsInfra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SmsConsent",
                table: "WpfCustomerProjections",
                type: "bit",
                nullable: false,
                defaultValue: false);

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
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "SmsCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageBody = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SegmentsPerMessage = table.Column<int>(type: "int", nullable: false),
                    RecipientCount = table.Column<int>(type: "int", nullable: false),
                    ReservedCredits = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedByCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsCampaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SmsCampaigns_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SmsCampaignRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsCampaignRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SmsCampaignRecipients_SmsCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "SmsCampaigns",
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

            migrationBuilder.CreateIndex(
                name: "IX_SmsCampaignRecipients_CampaignId",
                table: "SmsCampaignRecipients",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_SmsCampaigns_LicenseId_CreatedAt",
                table: "SmsCampaigns",
                columns: new[] { "LicenseId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenseSmsBalances");

            migrationBuilder.DropTable(
                name: "LicenseSmsTransactions");

            migrationBuilder.DropTable(
                name: "SmsCampaignRecipients");

            migrationBuilder.DropTable(
                name: "SmsCampaigns");

            migrationBuilder.DropColumn(
                name: "SmsConsent",
                table: "WpfCustomerProjections");
        }
    }
}
