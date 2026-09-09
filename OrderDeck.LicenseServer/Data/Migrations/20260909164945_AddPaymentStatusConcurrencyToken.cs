using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Payment.Status eşzamanlılık jetonu yapıldı (F04). Jeton yalnız model
    /// metadata'sı — UPDATE'in WHERE'ine Status koşulu ekler, şemada DDL
    /// üretmez. Migration snapshot'ı senkron tutmak için boş gövdeyle duruyor.
    /// </summary>
    public partial class AddPaymentStatusConcurrencyToken : Migration
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
