using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentSubmissionAuditOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PaymentId",
                table: "PaymentSubmissionAudits",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "PaymentSubmissionAudits",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            // Bu tabloya bugüne dek YALNIZCA başarılı gönderimler yazıldı;
            // dolayısıyla mevcut her satırın sonucu "ok". Boş bırakılsaydı
            // geçmiş satırlar "sonucu bilinmiyor" gibi görünür, oran sınırı /
            // dolandırıcılık incelemesinde reddedilen denemelerle karışırdı.
            migrationBuilder.Sql(
                "UPDATE [PaymentSubmissionAudits] SET [Outcome] = 'ok' WHERE [Outcome] = '';");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSubmissionAudits_ShopperId_CreatedAt",
                table: "PaymentSubmissionAudits",
                columns: new[] { "ShopperId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentSubmissionAudits_ShopperId_CreatedAt",
                table: "PaymentSubmissionAudits");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "PaymentSubmissionAudits");

            migrationBuilder.AlterColumn<Guid>(
                name: "PaymentId",
                table: "PaymentSubmissionAudits",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
