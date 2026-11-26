using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Banka havalesi onayı S2a (spec 2026-10-10 §2, §14): <c>Payments</c>, <c>PaymentMatches</c>, <c>Licenses</c> sütunları,
    /// beş yeni tablo, geniş IBAN tekili ve bir veri deyimi.
    ///
    /// <para><b>Yalnız genişletir.</b> Deploy'un otomatik geri alması imajı döndürür, göçü döndürmez: S2a öncesi imaj yazmayı
    /// sürdürür. Var olan tabloya eklenen her NOT NULL sütunun kalıcı DB varsayılanı vardır (eski imajın INSERT'i sütunu
    /// vermez) ve dar <c>IX_CustomerIbanMemories_LicenseId_IbanHash</c> yerinde kalır; onu S3b düşürür. ONLINE yok (prod SQL
    /// Server Express).</para>
    ///
    /// <para><b>Geri doldurma bağdan türetir</b> (<see cref="BackfillPaymentCustomers"/>) ve S2b göçünde yeniden koşar: arada
    /// gönderilen dekont dolar, geri alma penceresinde bayatlayan dolu bağ kapanır.</para>
    ///
    /// <para><b>Birleştirilmiş kopyada kalmış IBAN bağı taşınmaz</b> (S2a planı, Karar 5). Bağ ve onu öğreten eşleşme kararı aynı
    /// kopyadadır; göç bir kez koşar ve yalnız biri taşınırsa IBAN talebiyle silme kararın müşterisini, K-6 kalan doğrulamayı
    /// bulamaz. İkisini birlikte S2b taşır.</para>
    /// </summary>
    public partial class BankDecisionsS2a : Migration
    {
        /// <summary>Dekontun müşterisini bağdan TÜRETİR: boşu da doluyu da yazar, değişmeyen satıra ve shopper'sız dekonta
        /// (bağı yok) dokunmaz (S2a planı, Karar 3; S2b göçü metni kopyalar). Bağ seçimi
        /// <c>PaymentMatchReconciler.ResolveWpfCustomerAsync</c> ile aynı: LeftAt'e bakılmaz, en yeni JoinedAt'li MÜŞTERİLİ bağ
        /// (eşitlikte Id, oradaki sıra belirsiz). Asıl kayda en çok 3 adım (<c>CustomerIdResolver.MaxHops</c>); başka lisansın
        /// kaydından öteye izlenmez (varılan Id yazılır, <c>CustomerIdResolver</c> ile aynı). UpdatedAt yazılmaz
        /// (payments/since imleci). Zinciri bugünkü okuyucular gibi telefon kanıtı aramadan izler; S2b kopyası birleştirilmiş
        /// geçici (<c>CreatedByShopper</c>) kopyadan izlemez (tasarım §2.5). S2a'da sütunu okuyan yok, S2b doluyu da yeniden
        /// yazar.</summary>
        public const string BackfillPaymentCustomers = """
            UPDATE p
            SET p.[WpfCustomerId] = COALESCE(h3.[MergedIntoId], h2.[MergedIntoId], h1.[MergedIntoId], l.[WpfCustomerId])
            FROM [Payments] p
            OUTER APPLY (
                SELECT TOP (1) x.[WpfCustomerId] FROM [ShopperBroadcasterLinks] x
                WHERE x.[ShopperId] = p.[ShopperId] AND x.[LicenseId] = p.[LicenseId] AND x.[WpfCustomerId] IS NOT NULL
                ORDER BY x.[JoinedAt] DESC, x.[Id] DESC) l
            LEFT JOIN [WpfCustomerProjections] h1 ON h1.[Id] = l.[WpfCustomerId] AND h1.[LicenseId] = p.[LicenseId]
            LEFT JOIN [WpfCustomerProjections] h2 ON h2.[Id] = h1.[MergedIntoId] AND h2.[LicenseId] = p.[LicenseId]
            LEFT JOIN [WpfCustomerProjections] h3 ON h3.[Id] = h2.[MergedIntoId] AND h3.[LicenseId] = p.[LicenseId]
            WHERE p.[ShopperId] IS NOT NULL AND NOT EXISTS (
                SELECT p.[WpfCustomerId] INTERSECT
                SELECT COALESCE(h3.[MergedIntoId], h2.[MergedIntoId], h1.[MergedIntoId], l.[WpfCustomerId]));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalSource",
                table: "Payments",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedByOperatorId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BankTransactionId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Payments",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Receipt");

            migrationBuilder.AddColumn<Guid>(
                name: "WpfCustomerId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AskReasons",
                table: "PaymentMatches",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecidedVia",
                table: "PaymentMatches",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Decision",
                table: "PaymentMatches",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<int>(
                name: "DecisionRevision",
                table: "PaymentMatches",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DismissNote",
                table: "PaymentMatches",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DismissReason",
                table: "PaymentMatches",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatcherVersion",
                table: "PaymentMatches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportedByIbanMemory",
                table: "PaymentMatches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BankAutoApproveEnabled",
                table: "Licenses",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BankQuestionsEnabled",
                table: "Licenses",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BankQuestionsSince",
                table: "Licenses",
                type: "datetimeoffset",
                nullable: true);

            // Veri deyimi sütundan sonra, indeksten önce; ayrı komut (DDL ile DML aynı Sql()'e girmez).
            migrationBuilder.Sql(BackfillPaymentCustomers);

            migrationBuilder.CreateTable(
                name: "BankDecisionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TxId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RelatedPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OperatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Surface = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RuleVersion = table.Column<int>(type: "int", nullable: false),
                    WouldAutoApprove = table.Column<bool>(type: "bit", nullable: true),
                    OrderSetReliable = table.Column<bool>(type: "bit", nullable: true),
                    WpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AskReasons = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankDecisionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankDecisionEvents_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BankIbanStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IbanHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Shared = table.Column<bool>(type: "bit", nullable: false),
                    Disputed = table.Column<bool>(type: "bit", nullable: false),
                    LastTransferAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankIbanStates", x => x.Id);
                    table.CheckConstraint("CK_BankIbanStates_IbanHash_Hex64", "LEN([IbanHash]) = 64 AND [IbanHash] NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_BankIbanStates_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentExpectationAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReversedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentExpectationAllocations", x => x.Id);
                    table.CheckConstraint("CK_PaymentExpectationAllocations_Kind_Payment", "([Kind] = N'Payment' AND [PaymentId] IS NOT NULL) OR ([Kind] <> N'Payment' AND [PaymentId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_PaymentExpectationAllocations_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentShipmentSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    ProposedShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    LastRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StateChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentShipmentSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentShipmentSteps_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentShipmentStepOrders",
                columns: table => new
                {
                    StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceAtApproval = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BoundShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BoundSetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentShipmentStepOrders", x => new { x.StepId, x.OrderId });
                    table.ForeignKey(
                        name: "FK_PaymentShipmentStepOrders_PaymentShipmentSteps_StepId",
                        column: x => x.StepId,
                        principalTable: "PaymentShipmentSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LicenseId_BankTransactionId",
                table: "Payments",
                columns: new[] { "LicenseId", "BankTransactionId" },
                filter: "[BankTransactionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LicenseId_WpfCustomerId",
                table: "Payments",
                columns: new[] { "LicenseId", "WpfCustomerId" },
                filter: "[WpfCustomerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatches_LicenseId_Decision",
                table: "PaymentMatches",
                columns: new[] { "LicenseId", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerIbanMemories_LicenseId_IbanHash_WpfCustomerId",
                table: "CustomerIbanMemories",
                columns: new[] { "LicenseId", "IbanHash", "WpfCustomerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankDecisionEvents_LicenseId_CreatedAt",
                table: "BankDecisionEvents",
                columns: new[] { "LicenseId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BankDecisionEvents_LicenseId_RequestId",
                table: "BankDecisionEvents",
                columns: new[] { "LicenseId", "RequestId" },
                filter: "[RequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankDecisionEvents_LicenseId_TxId",
                table: "BankDecisionEvents",
                columns: new[] { "LicenseId", "TxId" },
                filter: "[TxId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankDecisionEvents_PaymentId",
                table: "BankDecisionEvents",
                column: "PaymentId",
                filter: "[PaymentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankDecisionEvents_RelatedPaymentId",
                table: "BankDecisionEvents",
                column: "RelatedPaymentId",
                filter: "[RelatedPaymentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankIbanStates_LastTransferAt",
                table: "BankIbanStates",
                column: "LastTransferAt");

            migrationBuilder.CreateIndex(
                name: "IX_BankIbanStates_LicenseId_IbanHash",
                table: "BankIbanStates",
                columns: new[] { "LicenseId", "IbanHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentExpectationAllocations_LicenseId_ExpectationId",
                table: "PaymentExpectationAllocations",
                columns: new[] { "LicenseId", "ExpectationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentExpectationAllocations_PaymentId",
                table: "PaymentExpectationAllocations",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentShipmentStepOrders_LicenseId_OrderId",
                table: "PaymentShipmentStepOrders",
                columns: new[] { "LicenseId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentShipmentSteps_Due",
                table: "PaymentShipmentSteps",
                column: "NextAttemptAt",
                filter: "[State] IN (N'Waiting', N'Reverting')");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentShipmentSteps_LicenseId_State",
                table: "PaymentShipmentSteps",
                columns: new[] { "LicenseId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentShipmentSteps_PaymentId",
                table: "PaymentShipmentSteps",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentShipmentSteps_Open",
                table: "PaymentShipmentSteps",
                columns: new[] { "PaymentId", "CustomerKey" },
                unique: true,
                filter: "[State] <> N'Closed' AND [State] <> N'Reverting'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankDecisionEvents");

            migrationBuilder.DropTable(
                name: "BankIbanStates");

            migrationBuilder.DropTable(
                name: "PaymentExpectationAllocations");

            migrationBuilder.DropTable(
                name: "PaymentShipmentStepOrders");

            migrationBuilder.DropTable(
                name: "PaymentShipmentSteps");

            migrationBuilder.DropIndex(
                name: "IX_Payments_LicenseId_BankTransactionId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_LicenseId_WpfCustomerId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_PaymentMatches_LicenseId_Decision",
                table: "PaymentMatches");

            migrationBuilder.DropIndex(
                name: "IX_CustomerIbanMemories_LicenseId_IbanHash_WpfCustomerId",
                table: "CustomerIbanMemories");

            migrationBuilder.DropColumn(
                name: "ApprovalSource",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ApprovedByOperatorId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "BankTransactionId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "WpfCustomerId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AskReasons",
                table: "PaymentMatches");

            migrationBuilder.DropColumn(
                name: "DecidedVia",
                table: "PaymentMatches");

            migrationBuilder.DropColumn(
                name: "Decision",
                table: "PaymentMatches");

            migrationBuilder.DropColumn(
                name: "DecisionRevision",
                table: "PaymentMatches");

            migrationBuilder.DropColumn(
                name: "DismissNote",
                table: "PaymentMatches");

            migrationBuilder.DropColumn(
                name: "DismissReason",
                table: "PaymentMatches");

            migrationBuilder.DropColumn(
                name: "MatcherVersion",
                table: "PaymentMatches");

            migrationBuilder.DropColumn(
                name: "SupportedByIbanMemory",
                table: "PaymentMatches");

            migrationBuilder.DropColumn(
                name: "BankAutoApproveEnabled",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "BankQuestionsEnabled",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "BankQuestionsSince",
                table: "Licenses");
        }
    }
}
