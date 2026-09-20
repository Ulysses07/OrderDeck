using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class NetgsmBrandCodeFilteredUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts");

            migrationBuilder.CreateIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts",
                column: "BrandCode",
                unique: true,
                filter: "[Status] = 'Verified'");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Filtresiz unique, <c>Up</c>'tan önceki şemanın aynısı — burası doğru
        /// yazılmış. Ama <c>Up</c>'tan sonra iki doğrulanmamış hesap aynı marka
        /// kodunu tutabildiği için bu geri dönüş 1505 ile DÜŞEBİLİR. Kaçınılmaz:
        /// gevşek kısıt altında yasallaşmış veriyle daha KATI bir kısıta dönmek
        /// tanımı gereği mümkün değil. Tek alternatif çakışan satırları burada
        /// silmek olurdu — sessiz veri kaybı, hatadan çok daha kötü. EF her göçü
        /// transaction'a sardığı için düşüş temiz: yukarıdaki DropIndex de geri
        /// alınır, şema bozuk bir ara hâlde kalmaz.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts");

            migrationBuilder.CreateIndex(
                name: "IX_NetgsmAccounts_BrandCode",
                table: "NetgsmAccounts",
                column: "BrandCode",
                unique: true);
        }
    }
}
