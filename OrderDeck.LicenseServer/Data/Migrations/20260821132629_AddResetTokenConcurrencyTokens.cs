using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// KASITLI OLARAK BOŞ — silmeyin. ShopperPasswordResetCode.AttemptCount ve
    /// PasswordResetToken.UsedAt eşzamanlılık jetonu yapıldı; jeton var olan
    /// sütunları kullandığı için şemada karşılığı yok, yalnızca EF'in ürettiği
    /// UPDATE'e WHERE koşulu ekliyor. Yine de gerekiyor: model anlık görüntüsü
    /// bu değişikliği içeriyor, göç olmasaydı fark bir sonraki göçe sızardı.
    /// </summary>
    public partial class AddResetTokenConcurrencyTokens : Migration
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
