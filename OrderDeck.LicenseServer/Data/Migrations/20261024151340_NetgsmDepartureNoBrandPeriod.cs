using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class NetgsmDepartureNoBrandPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Yalnız genişletme: null = markasız dönem (sahip kararı 10). Eski imaj null satırı
            // 3 yıl boyunca okumaz (Faz 3 yalnız süresi dolmuş satırın Id'sini seçer).
            migrationBuilder.AlterColumn<string>(
                name: "BrandCode",
                table: "NetgsmDepartures",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DİKKAT: markasız dönem satırları "" markaya çevrilir; eski kod onları boş markalı
            // (#472-#473 dönemi) olaylarla eşleyebilir. Geri almadan önce o satırları elle silin.
            migrationBuilder.AlterColumn<string>(
                name: "BrandCode",
                table: "NetgsmDepartures",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16,
                oldNullable: true);
        }
    }
}
