using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class LicenseMediaPurgeTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Yalnız genişletme: yeni tablo (yayıncı silmesinin lisans medyası görevleri, KVKK). Deploy geri alınırsa eski imaj
            // tabloya bakmaz; açık görevler yeni imaj dönünce tamamlanır.
            migrationBuilder.CreateTable(
                name: "LicenseMediaPurgeTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseMediaPurgeTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseMediaPurgeTasks_CompletedAt_NextAttemptAt",
                table: "LicenseMediaPurgeTasks",
                columns: new[] { "CompletedAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseMediaPurgeTasks_LicenseId",
                table: "LicenseMediaPurgeTasks",
                column: "LicenseId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenseMediaPurgeTasks");
        }
    }
}
