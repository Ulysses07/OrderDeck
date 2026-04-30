using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntakeForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntakeFormConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    WhatsAppPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CustomTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeFormConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntakeFormConfigs_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IntakeFormSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntakeFormConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeFormSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntakeFormSubmissions_IntakeFormConfigs_IntakeFormConfigId",
                        column: x => x.IntakeFormConfigId,
                        principalTable: "IntakeFormConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntakeFormConfigs_CustomerId",
                table: "IntakeFormConfigs",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntakeFormConfigs_Slug",
                table: "IntakeFormConfigs",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntakeFormSubmissions_IntakeFormConfigId_SubmittedAt",
                table: "IntakeFormSubmissions",
                columns: new[] { "IntakeFormConfigId", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntakeFormSubmissions");

            migrationBuilder.DropTable(
                name: "IntakeFormConfigs");
        }
    }
}
