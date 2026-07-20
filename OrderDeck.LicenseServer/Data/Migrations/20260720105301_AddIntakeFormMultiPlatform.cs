using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntakeFormMultiPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "IntakeFormSubmissions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FacebookUsername",
                table: "IntakeFormSubmissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstagramUsername",
                table: "IntakeFormSubmissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SmsConsent",
                table: "IntakeFormSubmissions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Tckn",
                table: "IntakeFormSubmissions",
                type: "nvarchar(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TikTokUsername",
                table: "IntakeFormSubmissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppConsent",
                table: "IntakeFormSubmissions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeUsername",
                table: "IntakeFormSubmissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Email",
                table: "IntakeFormSubmissions");

            migrationBuilder.DropColumn(
                name: "FacebookUsername",
                table: "IntakeFormSubmissions");

            migrationBuilder.DropColumn(
                name: "InstagramUsername",
                table: "IntakeFormSubmissions");

            migrationBuilder.DropColumn(
                name: "SmsConsent",
                table: "IntakeFormSubmissions");

            migrationBuilder.DropColumn(
                name: "Tckn",
                table: "IntakeFormSubmissions");

            migrationBuilder.DropColumn(
                name: "TikTokUsername",
                table: "IntakeFormSubmissions");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsent",
                table: "IntakeFormSubmissions");

            migrationBuilder.DropColumn(
                name: "YouTubeUsername",
                table: "IntakeFormSubmissions");
        }
    }
}
