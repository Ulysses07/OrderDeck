using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Bilerek boş. <c>WaSendAttempt.Status</c> ve <c>StartedAt</c> alanlarına
    /// eklenen eşzamanlılık belirteçleri (bkz. <c>LicenseDbContext</c>) yalnızca
    /// EF'in ürettiği UPDATE cümlesini değiştirir; kolon ve indeks şeması aynı
    /// kalır. Yine de gerekli: uygulama açılışta <c>Migrate()</c> çağırıyor ve
    /// model son migration'ın anlık görüntüsüyle uyuşmazsa EF bekleyen model
    /// değişikliği hatası verir. Boş diye silmeyin.
    /// </summary>
    public partial class WaSendAttemptConcurrencyTokens : Migration
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
