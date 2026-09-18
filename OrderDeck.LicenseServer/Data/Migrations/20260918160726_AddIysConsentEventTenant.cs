using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIysConsentEventTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrandCode",
                table: "IysConsentEvents",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IysConsentId",
                table: "IysConsentEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LicenseId",
                table: "IysConsentEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IysConsentEvents_LicenseId_Recipient_OccurredAt",
                table: "IysConsentEvents",
                columns: new[] { "LicenseId", "Recipient", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IysConsentEvents_LicenseId_Recipient_OccurredAt",
                table: "IysConsentEvents");

            migrationBuilder.DropColumn(
                name: "BrandCode",
                table: "IysConsentEvents");

            migrationBuilder.DropColumn(
                name: "IysConsentId",
                table: "IysConsentEvents");

            migrationBuilder.DropColumn(
                name: "LicenseId",
                table: "IysConsentEvents");
        }
    }
}
