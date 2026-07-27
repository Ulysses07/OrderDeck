using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppCloudApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WaConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProfileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PhoneNumberId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastInboundAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UnreadCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AiEnabled = table.Column<bool>(type: "bit", nullable: false),
                    HandedOffAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaConversations_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WhatsAppAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WabaId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PhoneNumberId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayPhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VerifiedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DisconnectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WhatsAppAccounts_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WaMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WamId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    MediaR2Key = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MediaMimeType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    MediaSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    TemplateName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaMessages_WaConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "WaConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaConversations_LicenseId_CustomerPhone",
                table: "WaConversations",
                columns: new[] { "LicenseId", "CustomerPhone" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaConversations_LicenseId_LastMessageAt",
                table: "WaConversations",
                columns: new[] { "LicenseId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WaMessages_ConversationId_Timestamp",
                table: "WaMessages",
                columns: new[] { "ConversationId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_WaMessages_LicenseId_Timestamp",
                table: "WaMessages",
                columns: new[] { "LicenseId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_WaMessages_WamId",
                table: "WaMessages",
                column: "WamId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppAccounts_LicenseId",
                table: "WhatsAppAccounts",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppAccounts_PhoneNumberId",
                table: "WhatsAppAccounts",
                column: "PhoneNumberId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaMessages");

            migrationBuilder.DropTable(
                name: "WhatsAppAccounts");

            migrationBuilder.DropTable(
                name: "WaConversations");
        }
    }
}
