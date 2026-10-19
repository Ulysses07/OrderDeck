using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Ortak bakiye S1 (spec 2026-10-11 §3.1). Yalnız ekleyici: <c>CustomerBalanceTransactions.DrawGroupId</c> +
    /// filtreli tekil OLMAYAN indeks; <c>WpfCustomerProjections.GroupId</c> BIN2 harmanı + (LicenseId, GroupId)
    /// filtreli indeks; <c>Licenses.PersonBalancePoolSince</c>; <c>PersonBalanceBaselines</c> tablosu.
    ///
    /// <para><b>ONLINE yok, veri yazımı yok</b> (prod SQL Server Express; geri doldurma yok — eski satırların
    /// DrawGroupId'si NULL kalır). <b>İstatistik:</b> GroupId'ye bağlı, indekse ait olmayan istatistik (otomatik
    /// <c>_WA_Sys_*</c> ya da kullanıcı) harman değişimini engeller; önce düşürülür, motor gerekirse yeniden açar.
    /// PersonBalanceS1MigrationTests sabitler (Developer + Express).</para>
    /// </summary>
    public partial class PersonBalanceS1 : Migration
    {
        /// <summary>GroupId'ye bağlı, indekse ait olmayan bütün istatistikleri düşürür (yoksa hiçbir şey yapmaz).</summary>
        internal const string DropGroupIdStatistics = """
            DECLARE @drop nvarchar(max);
            SELECT @drop = STRING_AGG(CONVERT(nvarchar(max),
                       N'DROP STATISTICS [dbo].[WpfCustomerProjections].' + QUOTENAME(s.name)), N'; ')
            FROM sys.stats AS s
            WHERE s.object_id = OBJECT_ID(N'[dbo].[WpfCustomerProjections]')
              AND (s.auto_created = 1 OR s.user_created = 1)
              AND EXISTS (SELECT 1 FROM sys.stats_columns AS sc
                          JOIN sys.columns AS c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
                          WHERE sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND c.name = N'GroupId');
            IF @drop IS NOT NULL EXEC sp_executesql @drop;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PersonBalancePoolSince",
                table: "Licenses",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DrawGroupId",
                table: "CustomerBalanceTransactions",
                type: "uniqueidentifier",
                nullable: true);

            // Harman değişiminden ÖNCE: GroupId'deki istatistik ALTER COLUMN'u engeller.
            migrationBuilder.Sql(DropGroupIdStatistics);

            migrationBuilder.AlterColumn<string>(
                name: "GroupId",
                table: "WpfCustomerProjections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "PersonBalanceBaselines",
                columns: table => new
                {
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LedgerSum = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonBalanceBaselines", x => new { x.LicenseId, x.WpfCustomerId });
                    table.ForeignKey(
                        name: "FK_PersonBalanceBaselines_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WpfCustomerProjections_LicenseId_GroupId",
                table: "WpfCustomerProjections",
                columns: new[] { "LicenseId", "GroupId" },
                filter: "[MergedIntoId] IS NULL AND [GroupId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerBalanceTransactions_DrawGroupId",
                table: "CustomerBalanceTransactions",
                column: "DrawGroupId",
                filter: "[DrawGroupId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersonBalanceBaselines");

            migrationBuilder.DropIndex(
                name: "IX_WpfCustomerProjections_LicenseId_GroupId",
                table: "WpfCustomerProjections");

            migrationBuilder.DropIndex(
                name: "IX_CustomerBalanceTransactions_DrawGroupId",
                table: "CustomerBalanceTransactions");

            migrationBuilder.DropColumn(
                name: "PersonBalancePoolSince",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "DrawGroupId",
                table: "CustomerBalanceTransactions");

            migrationBuilder.Sql(DropGroupIdStatistics);

            migrationBuilder.AlterColumn<string>(
                name: "GroupId",
                table: "WpfCustomerProjections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");
        }
    }
}
