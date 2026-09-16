using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// R12-S01: ŞEMA DEĞİŞİKLİĞİ YOK — bilerek boş. <c>Customer.AuthVersion</c>
    /// artık satırın eşzamanlılık jetonu; bu yalnız bir model açıklaması,
    /// sütunun tipi/kısıtı değişmiyor. Migration'ın varlık sebebi model
    /// anlık görüntüsünü (<c>LicenseDbContextModelSnapshot</c>) modelle
    /// hizada tutmak: aksi hâlde bir sonraki gerçek göç, bu açıklamayı
    /// "bekleyen değişiklik" sanıp kendi DDL'ine karıştırırdı.
    /// </summary>
    public partial class R12S01CustomerAuthVersionConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
