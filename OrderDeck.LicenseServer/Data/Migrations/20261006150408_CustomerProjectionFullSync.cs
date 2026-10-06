using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerProjectionFullSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AddressChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BlacklistChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlacklistReason",
                table: "WpfCustomerProjections",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BlacklistedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            // DEFAULT kısıtlaması rowversion kolonunda YASAK ("Defaults cannot
            // be created on columns of data type timestamp") — SQL Server bu
            // tür bir kolonu var olan satırlar için kendisi otomatik dolduruyor,
            // defaultValue parametresi bilerek verilmedi.
            migrationBuilder.AddColumn<byte[]>(
                name: "ChangeSeq",
                table: "WpfCustomerProjections",
                type: "rowversion",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "WpfCustomerProjections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreatedByShopper",
                table: "WpfCustomerProjections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "WpfCustomerProjections",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisplayNameChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "District",
                table: "WpfCustomerProjections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "WpfCustomerProjections",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FullNameChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupId",
                table: "WpfCustomerProjections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GroupIdChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            // Geri alma güvenliği: deploy workflow'u göç UYGULANMIŞ hâlde
            // ÖNCEKİ imaja otomatik geri dönebiliyor (/ready 60 sn'de gelmezse).
            // Eski imaj bu kolonu HİÇ tanımadığı için INSERT'lerinde DEFAULT'a
            // düşer. defaultValue:"" verseydik, o pencerede yazılan TÜM satırlar
            // AYNI boş anahtara ('') düşer — birleştirme işi onları birbirine
            // AİT sanıp İLGİSİZ müşterileri tek kimlikte toplardı. NEWID() her
            // satıra BENZERSİZ (ve bu yüzden hiçbir satırla eşleşmeyen) bir
            // anahtar verir; bu satırlar IdentityKeyRepairJob (Hangfire:
            // identity-key-repair) gerçek IdentityKey'lerini hesaplayana
            // kadar "kendi başına" kalır — yanlış ama en azından GÜVENLİ
            // (collision yok).
            migrationBuilder.AddColumn<string>(
                name: "IdentityKey",
                table: "WpfCustomerProjections",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValueSql: "CONVERT(nvarchar(128), NEWID())",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<bool>(
                name: "IsBlacklisted",
                table: "WpfCustomerProjections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoId",
                table: "WpfCustomerProjections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "WpfCustomerProjections",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotesChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PhoneChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecipientPaysActive",
                table: "WpfCustomerProjections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecipientPaysChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SmsConsent",
                table: "WpfCustomerProjections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SmsConsentChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tckn",
                table: "WpfCustomerProjections",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TcknChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppConsent",
                table: "WpfCustomerProjections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WhatsAppConsentChangedAt",
                table: "WpfCustomerProjections",
                type: "datetimeoffset",
                nullable: true);

            // Var olan satırlar: anahtar Username'den SQL ile türetiliyor.
            // SQL LOWER Türkçe büyük 'İ'yi HER collation'da 'i' yapar — bu,
            // WpfCustomerProjection.IdentityKeyOf'un Replace('İ','i') ile
            // yaptığıyla ÖRTÜŞÜYOR, yani bu göçten sonra yazılan yeni
            // satırlarla burada geriye dönük doldurulan eski satırlar en
            // azından Latin-1 aralığında (her Türkçe harf dahil) aynı
            // anahtara düşer. Açık kalan fark: kenarlardaki U+0020 DIŞI
            // boşluklar (SQL'in RTRIM/LTRIM'i yalnız U+0020'yi kırpar) ve
            // Türkçe/Latin-1 dışındaki bazı harfler (Latin Ext-B/D,
            // Yunanca/Kiril ekleri, Gürcüce, Cherokee, letterlike semboller,
            // ek düzlem harfleri) — bu satırlar NEWID() ile açılmış
            // benzersiz anahtarını (yukarıdaki AddColumn) burada da
            // KORUYAMAZ, çünkü UPDATE hepsini Username'den yeniden hesaplar;
            // IdentityKeyRepairJob (Hangfire: identity-key-repair) bu
            // satırları .NET'te yeniden hesaplayıp düzeltir — SQL backfill'i
            // tekrar koşturmak aynı ayrışmayı yine üretir.
            migrationBuilder.Sql(
                "UPDATE WpfCustomerProjections SET IdentityKey = LOWER(LTRIM(RTRIM(Username)));");

            migrationBuilder.CreateIndex(
                name: "IX_WpfCustomerProjections_LicenseId_ChangeSeq",
                table: "WpfCustomerProjections",
                columns: new[] { "LicenseId", "ChangeSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_WpfCustomerProjections_LicenseId_Platform_IdentityKey",
                table: "WpfCustomerProjections",
                columns: new[] { "LicenseId", "Platform", "IdentityKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WpfCustomerProjections_LicenseId_ChangeSeq",
                table: "WpfCustomerProjections");

            migrationBuilder.DropIndex(
                name: "IX_WpfCustomerProjections_LicenseId_Platform_IdentityKey",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "AddressChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "BlacklistChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "BlacklistReason",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "BlacklistedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "ChangeSeq",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "City",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "CreatedByShopper",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "DisplayNameChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "District",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "EmailChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "FullNameChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "GroupIdChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "IdentityKey",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "IsBlacklisted",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "MergedIntoId",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "NotesChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "PhoneChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "RecipientPaysActive",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "RecipientPaysChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "SmsConsent",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "SmsConsentChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "Tckn",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "TcknChangedAt",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsent",
                table: "WpfCustomerProjections");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsentChangedAt",
                table: "WpfCustomerProjections");
        }
    }
}
