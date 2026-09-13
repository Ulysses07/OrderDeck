using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWpfCustomerPurgeConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Metadata-only migration: PurgedAt artık EF concurrency token'ı.
            // SQL şeması değişmez; snapshot üretimdeki koşullu UPDATE'i belgeler.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Metadata-only migration.
        }
    }
}
