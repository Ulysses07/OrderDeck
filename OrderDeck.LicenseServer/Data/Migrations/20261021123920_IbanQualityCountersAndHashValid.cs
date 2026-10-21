using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class IbanQualityCountersAndHashValid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CounterpartyIbanHashValid",
                table: "BankTransactions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "IbanEmptyCount",
                table: "BankAccounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IbanFullCount",
                table: "BankAccounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IbanMaskedCount",
                table: "BankAccounts",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CounterpartyIbanHashValid",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "IbanEmptyCount",
                table: "BankAccounts");

            migrationBuilder.DropColumn(
                name: "IbanFullCount",
                table: "BankAccounts");

            migrationBuilder.DropColumn(
                name: "IbanMaskedCount",
                table: "BankAccounts");
        }
    }
}
