using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentSubmissionAuditLicenseId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LicenseId",
                table: "PaymentSubmissionAudits",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSubmissionAudits_LicenseId_CreatedAt",
                table: "PaymentSubmissionAudits",
                columns: new[] { "LicenseId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentSubmissionAudits_LicenseId_CreatedAt",
                table: "PaymentSubmissionAudits");

            migrationBuilder.DropColumn(
                name: "LicenseId",
                table: "PaymentSubmissionAudits");
        }
    }
}
