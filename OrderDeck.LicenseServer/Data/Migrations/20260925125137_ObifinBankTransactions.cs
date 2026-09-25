using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class ObifinBankTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObifinAccountId = table.Column<long>(type: "bigint", nullable: false),
                    BankaKodu = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IbanMasked = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IbanHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    LastBankSyncAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NotificationNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RefreshedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankAccounts_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BankTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObifinId = table.Column<long>(type: "bigint", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObifinAccountId = table.Column<long>(type: "bigint", nullable: false),
                    BankaKodu = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    TransactionCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CommonType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BankReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CounterpartyIbanHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CounterpartyIbanMasked = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CounterpartyName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    CounterpartyTaxIdHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RawJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DescriptionPurgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankTransactions", x => x.Id);
                    table.CheckConstraint("CK_BankTransactions_CounterpartyIbanHash_Hex64", "LEN([CounterpartyIbanHash]) = 64 AND [CounterpartyIbanHash] NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_BankTransactions_CounterpartyTaxIdHash_Hex64", "LEN([CounterpartyTaxIdHash]) = 64 AND [CounterpartyTaxIdHash] NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_BankTransactions_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerIbanMemories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IbanHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IbanMasked = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LearnedFrom = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceBankTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerIbanMemories", x => x.Id);
                    table.CheckConstraint("CK_CustomerIbanMemories_IbanHash_Hex64", "LEN([IbanHash]) = 64 AND [IbanHash] NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_CustomerIbanMemories_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ObifinConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UserCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PasswordProtected = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ApiKeyProtected = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastPolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastObifinTransactionId = table.Column<long>(type: "bigint", nullable: true),
                    BackfillCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObifinConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObifinConnections_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMatchGaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedBankTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMatchGaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentMatchGaps_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposedWpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Layer = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(4,3)", precision: 4, scale: 3, nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActualWpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentMatches_BankTransactions_BankTransactionId",
                        column: x => x.BankTransactionId,
                        principalTable: "BankTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BankConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObifinConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankaKodu = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BankaApiId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankConnections_ObifinConnections_ObifinConnectionId",
                        column: x => x.ObifinConnectionId,
                        principalTable: "ObifinConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_LicenseId_ObifinAccountId",
                table: "BankAccounts",
                columns: new[] { "LicenseId", "ObifinAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_LicenseId_BankaApiId",
                table: "BankConnections",
                columns: new[] { "LicenseId", "BankaApiId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_ObifinConnectionId",
                table: "BankConnections",
                column: "ObifinConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_LicenseId_Direction_OccurredAt",
                table: "BankTransactions",
                columns: new[] { "LicenseId", "Direction", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_LicenseId_ObifinId",
                table: "BankTransactions",
                columns: new[] { "LicenseId", "ObifinId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerIbanMemories_LicenseId_IbanHash",
                table: "CustomerIbanMemories",
                columns: new[] { "LicenseId", "IbanHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerIbanMemories_LicenseId_WpfCustomerId",
                table: "CustomerIbanMemories",
                columns: new[] { "LicenseId", "WpfCustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ObifinConnections_LicenseId",
                table: "ObifinConnections",
                column: "LicenseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatches_BankTransactionId",
                table: "PaymentMatches",
                column: "BankTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatches_LicenseId_Status",
                table: "PaymentMatches",
                columns: new[] { "LicenseId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatches_PaymentId",
                table: "PaymentMatches",
                column: "PaymentId",
                unique: true,
                filter: "[PaymentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatchGaps_LicenseId",
                table: "PaymentMatchGaps",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatchGaps_PaymentId",
                table: "PaymentMatchGaps",
                column: "PaymentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankAccounts");

            migrationBuilder.DropTable(
                name: "BankConnections");

            migrationBuilder.DropTable(
                name: "CustomerIbanMemories");

            migrationBuilder.DropTable(
                name: "PaymentMatches");

            migrationBuilder.DropTable(
                name: "PaymentMatchGaps");

            migrationBuilder.DropTable(
                name: "ObifinConnections");

            migrationBuilder.DropTable(
                name: "BankTransactions");
        }
    }
}
