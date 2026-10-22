using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class DataControllerProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DataControllerVersion",
                table: "IntakeFormSubmissions",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DataControllerProfiles",
                columns: table => new
                {
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PostalAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    KvkkEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    KepAddress = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataControllerProfiles", x => x.CustomerId);
                    table.ForeignKey(
                        name: "FK_DataControllerProfiles_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataControllerProfileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PostalAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    KvkkEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    KepAddress = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataControllerProfileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataControllerProfileVersions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataControllerProfileVersions_CustomerId_Version",
                table: "DataControllerProfileVersions",
                columns: new[] { "CustomerId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataControllerProfiles");

            migrationBuilder.DropTable(
                name: "DataControllerProfileVersions");

            migrationBuilder.DropColumn(
                name: "DataControllerVersion",
                table: "IntakeFormSubmissions");
        }
    }
}
