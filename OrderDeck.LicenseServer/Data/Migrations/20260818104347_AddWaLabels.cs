using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWaLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WaDekontExtractions",
                columns: table => new
                {
                    WaMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReferansNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PdfHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParserConfidence = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaDekontExtractions", x => x.WaMessageId);
                    table.ForeignKey(
                        name: "FK_WaDekontExtractions_WaMessages_WaMessageId",
                        column: x => x.WaMessageId,
                        principalTable: "WaMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WaLabels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaLabels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaLabels_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WaConversationLabels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WaLabelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaConversationLabels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaConversationLabels_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WaConversationLabels_WaConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "WaConversations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WaConversationLabels_WaLabels_WaLabelId",
                        column: x => x.WaLabelId,
                        principalTable: "WaLabels",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WaLabelRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventKey = table.Column<int>(type: "int", nullable: false),
                    WaLabelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaLabelRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaLabelRules_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WaLabelRules_WaLabels_WaLabelId",
                        column: x => x.WaLabelId,
                        principalTable: "WaLabels",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaConversationLabels_ConversationId_WaLabelId",
                table: "WaConversationLabels",
                columns: new[] { "ConversationId", "WaLabelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaConversationLabels_LicenseId_WaLabelId",
                table: "WaConversationLabels",
                columns: new[] { "LicenseId", "WaLabelId" });

            migrationBuilder.CreateIndex(
                name: "IX_WaConversationLabels_WaLabelId",
                table: "WaConversationLabels",
                column: "WaLabelId");

            migrationBuilder.CreateIndex(
                name: "IX_WaDekontExtractions_LicenseId",
                table: "WaDekontExtractions",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_WaLabelRules_LicenseId_EventKey",
                table: "WaLabelRules",
                columns: new[] { "LicenseId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaLabelRules_WaLabelId",
                table: "WaLabelRules",
                column: "WaLabelId");

            migrationBuilder.CreateIndex(
                name: "IX_WaLabels_LicenseId_Name",
                table: "WaLabels",
                columns: new[] { "LicenseId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaConversationLabels");

            migrationBuilder.DropTable(
                name: "WaDekontExtractions");

            migrationBuilder.DropTable(
                name: "WaLabelRules");

            migrationBuilder.DropTable(
                name: "WaLabels");
        }
    }
}
