using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Yayın ve etiket senkronu S2 (spec 2026-10-13 §3.1, S2 satırı): satış
    /// komutu günlüğü <c>SalesCommandLogs</c> — <c>POST sales/commands</c>'ın
    /// idempotency kaydı ve denetim izi (§4.6). Birincil anahtar istemcinin
    /// CommandId'si; indeks (LicenseId, CreatedAt) lisansın denetim sorgusu ve
    /// 90 günlük saklama (§4.9) için.
    ///
    /// <para><b>Yalnız ekleyici (genişlet/daralt).</b> Yalnız yeni bir tablo;
    /// var olan tablolara kolon ya da indeks eklenmez. S2 otomatik geri alınırsa
    /// önceki imaj bu tabloyu tanımaz, yazımları değişmez; komut ucu 404 döner
    /// (§8.6). Lisansı silen bugünkü yol için FK cascade.</para>
    ///
    /// <para><b>ONLINE yok, veri yazımı yok</b>: tablo boş doğar.
    /// SalesSyncS2CommandLogMigrationScriptTests sabitler.</para>
    /// </summary>
    public partial class SalesSyncS2CommandLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesCommandLogs",
                columns: table => new
                {
                    CommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequestHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesCommandLogs", x => x.CommandId);
                    table.ForeignKey(
                        name: "FK_SalesCommandLogs_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesCommandLogs_LicenseId_CreatedAt",
                table: "SalesCommandLogs",
                columns: new[] { "LicenseId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesCommandLogs");
        }
    }
}
