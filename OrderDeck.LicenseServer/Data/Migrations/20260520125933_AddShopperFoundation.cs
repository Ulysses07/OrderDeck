using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShopperFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FraudFlags",
                table: "Payments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MediaContentType",
                table: "Payments",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaObjectKey",
                table: "Payments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataHash",
                table: "Payments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParserConfidence",
                table: "Payments",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PdfPurgedAt",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientIban",
                table: "Payments",
                type: "nvarchar(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientName",
                table: "Payments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShopperId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentAccountHolder",
                table: "Licenses",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentIban",
                table: "Licenses",
                type: "nvarchar(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShopperAppEnabled",
                table: "Licenses",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShopperCode",
                table: "Licenses",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShopperCodeUpdatedAt",
                table: "Licenses",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentSubmissionAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShopperId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    FraudFlags = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ParserConfidence = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ParserRawText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSubmissionAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shoppers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Tc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    NotificationsEnabledBroadcast = table.Column<bool>(type: "bit", nullable: false),
                    NotificationsEnabledOrders = table.Column<bool>(type: "bit", nullable: false),
                    NotificationsEnabledPayments = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shoppers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WpfCustomerProjections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WpfCustomerProjections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WpfCustomerProjections_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopperBroadcasterLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShopperId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeftAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopperBroadcasterLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopperBroadcasterLinks_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShopperBroadcasterLinks_Shoppers_ShopperId",
                        column: x => x.ShopperId,
                        principalTable: "Shoppers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopperPushDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShopperId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PushToken = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopperPushDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopperPushDevices_Shoppers_ShopperId",
                        column: x => x.ShopperId,
                        principalTable: "Shoppers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_MetadataHash",
                table: "Payments",
                column: "MetadataHash");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PdfHash",
                table: "Payments",
                column: "PdfHash",
                unique: true,
                filter: "[PdfHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ShopperId",
                table: "Payments",
                column: "ShopperId");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_ShopperCode",
                table: "Licenses",
                column: "ShopperCode",
                unique: true,
                filter: "[ShopperCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSubmissionAudits_CreatedAt",
                table: "PaymentSubmissionAudits",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSubmissionAudits_PaymentId",
                table: "PaymentSubmissionAudits",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopperBroadcasterLinks_LicenseId_JoinedAt",
                table: "ShopperBroadcasterLinks",
                columns: new[] { "LicenseId", "JoinedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopperBroadcasterLinks_ShopperId_LicenseId",
                table: "ShopperBroadcasterLinks",
                columns: new[] { "ShopperId", "LicenseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopperPushDevices_ShopperId_DeviceId",
                table: "ShopperPushDevices",
                columns: new[] { "ShopperId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shoppers_Phone",
                table: "Shoppers",
                column: "Phone",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WpfCustomerProjections_LicenseId_Platform_Username",
                table: "WpfCustomerProjections",
                columns: new[] { "LicenseId", "Platform", "Username" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentSubmissionAudits");

            migrationBuilder.DropTable(
                name: "ShopperBroadcasterLinks");

            migrationBuilder.DropTable(
                name: "ShopperPushDevices");

            migrationBuilder.DropTable(
                name: "WpfCustomerProjections");

            migrationBuilder.DropTable(
                name: "Shoppers");

            migrationBuilder.DropIndex(
                name: "IX_Payments_MetadataHash",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_PdfHash",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ShopperId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Licenses_ShopperCode",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "FraudFlags",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "MediaContentType",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "MediaObjectKey",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "MetadataHash",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ParserConfidence",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PdfPurgedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RecipientIban",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RecipientName",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ShopperId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PaymentAccountHolder",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "PaymentIban",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "ShopperAppEnabled",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "ShopperCode",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "ShopperCodeUpdatedAt",
                table: "Licenses");
        }
    }
}
