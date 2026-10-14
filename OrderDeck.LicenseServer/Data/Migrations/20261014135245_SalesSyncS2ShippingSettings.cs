using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Yayın ve etiket senkronu S2 (spec 2026-10-13 §3.1, S2 satırı): lisans
    /// geneli kargo ayarı tablosu <c>LicenseShippingSettings</c> (sahip kararı
    /// Y4, §4.8, §6.2).
    ///
    /// <para><b>Yalnız ekleyici (genişlet/daralt).</b> Yalnız yeni bir tablo;
    /// var olan tablolara kolon ya da indeks eklenmez. S2 otomatik geri
    /// alınırsa S1 imajı bu tabloyu tanımaz ve yazımları değişmez. Lisansı
    /// silen bugünkü yol için FK cascade: eski imaj ayar satırı olan lisansı da
    /// silebilir.</para>
    ///
    /// <para><b>ONLINE yok, veri yazımı yok</b>: tablo boş doğar; ayarı mod On'da
    /// WPF'in PUT'u, her kipte <c>sales-sync shipping set</c> yazar.
    /// SalesSyncS2MigrationScriptTests sabitler.</para>
    /// </summary>
    public partial class SalesSyncS2ShippingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LicenseShippingSettings",
                columns: table => new
                {
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FreeShippingThreshold = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ShippingFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseShippingSettings", x => x.LicenseId);
                    table.ForeignKey(
                        name: "FK_LicenseShippingSettings_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenseShippingSettings");
        }
    }
}
