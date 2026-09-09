using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShopperSmsConsentAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SmsConsentAt",
                table: "Shoppers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SmsConsentRevokedAt",
                table: "Shoppers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmsConsentSource",
                table: "Shoppers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmsConsentAt",
                table: "Shoppers");

            migrationBuilder.DropColumn(
                name: "SmsConsentRevokedAt",
                table: "Shoppers");

            migrationBuilder.DropColumn(
                name: "SmsConsentSource",
                table: "Shoppers");
        }
    }
}
