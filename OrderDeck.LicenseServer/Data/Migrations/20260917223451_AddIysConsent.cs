using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIysConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IysConsentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceTable = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProofIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProofUserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ApiResponseCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ApiResponseBody = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IysConsentEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IysConsents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ChannelType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RecipientType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ConsentDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SourceCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PushState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PushDeadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastVerifiedStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastLocalEventAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastPushedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifyAttempts = table.Column<int>(type: "int", nullable: false),
                    NextVerifyAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IysConsents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IysConsentEvents_Recipient_OccurredAt",
                table: "IysConsentEvents",
                columns: new[] { "Recipient", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IysConsents_BrandCode_ChannelType_RecipientType_Recipient",
                table: "IysConsents",
                columns: new[] { "BrandCode", "ChannelType", "RecipientType", "Recipient" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IysConsents_PushState_NextVerifyAt",
                table: "IysConsents",
                columns: new[] { "PushState", "NextVerifyAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IysConsentEvents");

            migrationBuilder.DropTable(
                name: "IysConsents");
        }
    }
}
