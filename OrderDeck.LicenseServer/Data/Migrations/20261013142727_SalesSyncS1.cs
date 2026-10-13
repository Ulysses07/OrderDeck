using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Yayın ve etiket senkronu S1 (spec 2026-10-13 §3.1, yalnız S1 satırları):
    /// sahiplik ve devir kolonları, birim damgaları, akış imleci (ChangeSeq),
    /// mod kapısı ve kopya izi (WpfReplicas).
    ///
    /// <para><b>Yalnız ekleyici (genişlet/daralt).</b> Master'a merge otomatik
    /// deploy'dur ve /ready 60 sn'de gelmezse imaj S1 öncesine döner ama bu göç
    /// KALIR (spec §8.6). Eski imaj yeni kolonları tanımaz: yeni kolonların
    /// hepsi ya boş olabilir ya da DEFAULT taşır (Shipments.Version ve
    /// Licenses.SalesSyncMode 0; ChangeSeq'i motor doldurur). Eski imajın
    /// yazımı ChangeSeq'i ilerletir, onarım işi (§4.9) o satırları bulur.</para>
    ///
    /// <para><b>ONLINE yok</b>: prod SQL Server Express'te ONLINE indeks işlemi
    /// yok. <b>Veri geri doldurması yok</b>: onu sales-release-repair işi yapar
    /// (§4.9). İkisini SalesSyncS1MigrationScriptTests sabitler.</para>
    /// </summary>
    public partial class SalesSyncS1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DEFAULT kısıtlaması rowversion kolonunda YASAK ("Defaults cannot
            // be created on columns of data type timestamp") — SQL Server bu
            // tür bir kolonu var olan satırlar için kendisi otomatik dolduruyor,
            // defaultValue parametresi bilerek verilmedi (CustomerProjectionFullSync
            // ile aynı). Bu ekleme tablonun HER satırını yazar: süre
            // SalesSyncS1MigrationTests'te ölçülür (spec §3.1 göç notları).
            migrationBuilder.AddColumn<byte[]>(
                name: "ChangeSeq",
                table: "StreamSessions",
                type: "rowversion",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "ForceEndedBy",
                table: "StreamSessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerMachineName",
                table: "StreamSessions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerReplicaId",
                table: "StreamSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleasedAt",
                table: "StreamSessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnsentLabelCount",
                table: "StreamSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AmountDerivedAt",
                table: "Shipments",
                type: "datetimeoffset",
                nullable: true);

            // DEFAULT kısıtlaması rowversion kolonunda YASAK ("Defaults cannot
            // be created on columns of data type timestamp") — SQL Server bu
            // tür bir kolonu var olan satırlar için kendisi otomatik dolduruyor,
            // defaultValue parametresi bilerek verilmedi (CustomerProjectionFullSync
            // ile aynı). Bu ekleme tablonun HER satırını yazar: süre
            // SalesSyncS1MigrationTests'te ölçülür (spec §3.1 göç notları).
            migrationBuilder.AddColumn<byte[]>(
                name: "ChangeSeq",
                table: "Shipments",
                type: "rowversion",
                nullable: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CommandedAt",
                table: "Shipments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginReplicaId",
                table: "Shipments",
                type: "uniqueidentifier",
                nullable: true);

            // DEFAULT 0 kalıcı bir kısıtlama: S1 öncesi imajın INSERT'i bu
            // kolonu vermez ve ona düşer (geri alma penceresi, spec §8.6).
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Shipments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BackupSetAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelSetAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            // DEFAULT kısıtlaması rowversion kolonunda YASAK ("Defaults cannot
            // be created on columns of data type timestamp") — SQL Server bu
            // tür bir kolonu var olan satırlar için kendisi otomatik dolduruyor,
            // defaultValue parametresi bilerek verilmedi (CustomerProjectionFullSync
            // ile aynı). Bu ekleme tablonun HER satırını yazar: süre
            // SalesSyncS1MigrationTests'te ölçülür (spec §3.1 göç notları).
            migrationBuilder.AddColumn<byte[]>(
                name: "ChangeSeq",
                table: "Orders",
                type: "rowversion",
                nullable: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginReplicaId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PaidPrice",
                table: "Orders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentOrderId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PriceCorrectedAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PriceSetAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrintBatchId",
                table: "Orders",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrintSeq",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SessionReleasedAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShipmentId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShipmentSetAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TextScrubbedAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLegacySalesPushAt",
                table: "Licenses",
                type: "datetimeoffset",
                nullable: true);

            // DEFAULT 0 (Off) kalıcı bir kısıtlama: var olan ve S1 öncesi
            // imajın açtığı lisans mod Off doğar.
            migrationBuilder.AddColumn<byte>(
                name: "SalesSyncMode",
                table: "Licenses",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SalesSyncModeChangedAt",
                table: "Licenses",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WpfReplicas",
                columns: table => new
                {
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HardwareFingerprint = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ClientVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Protocol = table.Column<byte>(type: "tinyint", nullable: false),
                    FeatureLevel = table.Column<byte>(type: "tinyint", nullable: false),
                    SeenMode = table.Column<byte>(type: "tinyint", nullable: true),
                    SeenModeAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SeenModeOnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PreviousReplicaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BackfillCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PaymentHistoryCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BackfillDivergedLocal = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    BackfillDivergedServer = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ReportedThreshold = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ReportedFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WpfReplicas", x => new { x.LicenseId, x.ReplicaId });
                    table.ForeignKey(
                        name: "FK_WpfReplicas_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_LicenseId_ChangeSeq",
                table: "StreamSessions",
                columns: new[] { "LicenseId", "ChangeSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_LicenseId_Unreleased",
                table: "StreamSessions",
                column: "LicenseId",
                filter: "[ReleasedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_LicenseId_ChangeSeq",
                table: "Shipments",
                columns: new[] { "LicenseId", "ChangeSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_LicenseId_ChangeSeq",
                table: "Orders",
                columns: new[] { "LicenseId", "ChangeSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_LicenseId_ShipmentId",
                table: "Orders",
                columns: new[] { "LicenseId", "ShipmentId" },
                filter: "[ShipmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WpfReplicas_LicenseId_LastSeenAt",
                table: "WpfReplicas",
                columns: new[] { "LicenseId", "LastSeenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WpfReplicas");

            migrationBuilder.DropIndex(
                name: "IX_StreamSessions_LicenseId_ChangeSeq",
                table: "StreamSessions");

            migrationBuilder.DropIndex(
                name: "IX_StreamSessions_LicenseId_Unreleased",
                table: "StreamSessions");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_LicenseId_ChangeSeq",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_Orders_LicenseId_ChangeSeq",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_LicenseId_ShipmentId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ChangeSeq",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "ForceEndedBy",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "OwnerMachineName",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "OwnerReplicaId",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "ReleasedAt",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "UnsentLabelCount",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "AmountDerivedAt",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "ChangeSeq",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "CommandedAt",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "OriginReplicaId",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "BackupSetAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CancelSetAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ChangeSeq",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OriginReplicaId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PaidPrice",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ParentOrderId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PriceCorrectedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PriceSetAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PrintBatchId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PrintSeq",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SessionReleasedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShipmentId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShipmentSetAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TextScrubbedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LastLegacySalesPushAt",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "SalesSyncMode",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "SalesSyncModeChangedAt",
                table: "Licenses");
        }
    }
}
