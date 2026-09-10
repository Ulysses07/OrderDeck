using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Bilerek boş: CustomerBalance.UpdatedAt + LicenseSmsBalance.UpdatedAt
    /// IsConcurrencyToken() yalnız EF metadata'sı — DDL değişikliği yok.
    /// Migration, model snapshot'ını senkron tutmak için var (F02/F03,
    /// 2026-09-09 denetimi).
    /// </remarks>
    public partial class AddBalanceConcurrencyTokens : Migration
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
