using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerBalanceTransactionSaleScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SaleScope",
                table: "CustomerBalanceTransactions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerBalanceTransactions_LicenseId_WpfCustomerId_SaleScope",
                table: "CustomerBalanceTransactions",
                columns: new[] { "LicenseId", "WpfCustomerId", "SaleScope" },
                filter: "[SaleScope] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerBalanceTransactions_LicenseId_WpfCustomerId_SaleScope",
                table: "CustomerBalanceTransactions");

            migrationBuilder.DropColumn(
                name: "SaleScope",
                table: "CustomerBalanceTransactions");
        }
    }
}
