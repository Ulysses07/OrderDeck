using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropProductPhotoColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhotoContentType",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PhotoHeight",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PhotoObjectKey",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PhotoSizeBytes",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PhotoWidth",
                table: "Products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PhotoContentType",
                table: "Products",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhotoHeight",
                table: "Products",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoObjectKey",
                table: "Products",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PhotoSizeBytes",
                table: "Products",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhotoWidth",
                table: "Products",
                type: "int",
                nullable: true);
        }
    }
}
