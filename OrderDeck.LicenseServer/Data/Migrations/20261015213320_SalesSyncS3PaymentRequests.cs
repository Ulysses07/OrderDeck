using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Yayın ve etiket senkronu S3 (spec 2026-10-13 §3.1, S3 satırları; §6.4):
    /// bilgisayarlar arası ödeme isteği kirası ve teslim kaydı.
    /// <c>PaymentRequestRecords</c> (PK istemcinin recordId'si), üye kesişimi
    /// için <c>PaymentRequestMembers</c>, isteğin kapsadığı siparişler için
    /// <c>PaymentRequestRecordOrders</c>. DM6'nın hakemi filtreli tekil indeks
    /// <c>UX_PaymentRequestRecords_Sending</c>: (LicenseId, PersonKeyHash,
    /// ScopeKey) WHERE [State] = 0 — kişi×kapsamda tek Sending. Anahtar
    /// PersonKey metninin değil SHA-256 özetinin üzerinde (metin BIN2'de durur).
    ///
    /// <para><b>Yalnız ekleyici (genişlet/daralt).</b> Yalnız yeni tablolar; var
    /// olan tablolara kolon, indeks ya da kısıt eklenmez. S3 otomatik geri
    /// alınırsa tablolar kalır, önceki imaj onları tanımaz ve kira ucu 404 döner
    /// (§8.6). Lisansı silen bugünkü yol için Licenses → kayıt → üye/sipariş
    /// zinciri cascade; alt tablolardan Licenses'a FK yok (SQL Server çoklu
    /// cascade yolunu reddeder), Orders'a da yok (geçmiş sipariş sunucuda
    /// olmayabilir).</para>
    ///
    /// <para><b>ONLINE yok, veri yazımı yok</b>: tablolar boş doğar.
    /// SalesSyncS3PaymentRequestsMigrationScriptTests sabitler.</para>
    /// </summary>
    public partial class SalesSyncS3PaymentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentRequestRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PersonKeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OwnerWpfCustomerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    ProductTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    TotalBeforeBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    NetAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AppliedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    FullyFromBalance = table.Column<bool>(type: "bit", nullable: true),
                    ShippingSettingsVersion = table.Column<int>(type: "int", nullable: true),
                    RequestHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequestRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRequestRecords_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentRequestMembers",
                columns: table => new
                {
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WpfCustomerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequestMembers", x => new { x.RecordId, x.WpfCustomerId });
                    table.ForeignKey(
                        name: "FK_PaymentRequestMembers_PaymentRequestRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "PaymentRequestRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentRequestRecordOrders",
                columns: table => new
                {
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequestRecordOrders", x => new { x.RecordId, x.OrderId });
                    table.ForeignKey(
                        name: "FK_PaymentRequestRecordOrders_PaymentRequestRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "PaymentRequestRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequestMembers_LicenseId_WpfCustomerId_ScopeKey",
                table: "PaymentRequestMembers",
                columns: new[] { "LicenseId", "WpfCustomerId", "ScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequestRecords_LicenseId_PersonKeyHash_ScopeKey_StartedAt",
                table: "PaymentRequestRecords",
                columns: new[] { "LicenseId", "PersonKeyHash", "ScopeKey", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_PaymentRequestRecords_Sending",
                table: "PaymentRequestRecords",
                columns: new[] { "LicenseId", "PersonKeyHash", "ScopeKey" },
                unique: true,
                filter: "[State] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentRequestMembers");

            migrationBuilder.DropTable(
                name: "PaymentRequestRecordOrders");

            migrationBuilder.DropTable(
                name: "PaymentRequestRecords");
        }
    }
}
