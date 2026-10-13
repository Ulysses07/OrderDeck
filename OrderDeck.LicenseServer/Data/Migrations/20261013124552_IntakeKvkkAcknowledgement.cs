using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntakeKvkkAcknowledgement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "KvkkAcknowledgedAt",
                table: "IntakeFormSubmissions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KvkkTextVersion",
                table: "IntakeFormSubmissions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KvkkAcknowledgedAt",
                table: "IntakeFormSubmissions");

            migrationBuilder.DropColumn(
                name: "KvkkTextVersion",
                table: "IntakeFormSubmissions");
        }
    }
}
