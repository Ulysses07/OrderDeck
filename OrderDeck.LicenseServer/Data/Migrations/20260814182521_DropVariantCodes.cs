using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropVariantCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductVariants_LicenseId_VariantCode",
                table: "ProductVariants");

            migrationBuilder.DropIndex(
                name: "IX_ProductVariants_ProductId_VariantCode",
                table: "ProductVariants");

            migrationBuilder.AddColumn<string>(
                name: "Axis1ValueNorm",
                table: "ProductVariants",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Axis2ValueNorm",
                table: "ProductVariants",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            // Geri doldurma: yeni kolonlar SearchNormalizer'ın yaptığını yapmalı —
            // Türkçe harfleri ASCII'ye katla, büyüt, kırp. Sıra önemli: benzersiz
            // indeks bu doldurmadan SONRA kurulmalı, yoksa boş dizelerle dolu
            // kolonlarda ürün başına tek satıra düşer ve göç patlar.
            //
            // Bu dağıtımda tablo boş (2026-08-13: SELECT COUNT(*) FROM Products = 0),
            // yani pratikte no-op. Yine de veri varmış gibi yazıldı: bir sonraki
            // ortamda (staging, yeniden kurulan prod) boş olmayabilir.
            //
            // SearchNormalizer'dan tek farkı: kelime ARASI çoklu boşluğu teke
            // indirmiyor. Kabul edildi — eksen değerinde ("Siyah", "M") çift boşluk
            // gerçekçi değil, ve eşleşmeyen tek satır yalnız o varyantı yeniden
            // kaydetmeyi gerektirir.
            migrationBuilder.Sql(@"
UPDATE [ProductVariants]
SET [Axis1ValueNorm] = UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                       REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                         LTRIM(RTRIM(ISNULL([Axis1Value], N'')))
                       , N'ç', N'c'), N'Ç', N'C')
                       , N'ğ', N'g'), N'Ğ', N'G')
                       , N'ı', N'i'), N'İ', N'I')
                       , N'ö', N'o'), N'Ö', N'O')
                       , N'ş', N's'), N'Ş', N'S')
                       , N'ü', N'u'), N'Ü', N'U')),
    [Axis2ValueNorm] = UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                       REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                         LTRIM(RTRIM(ISNULL([Axis2Value], N'')))
                       , N'ç', N'c'), N'Ç', N'C')
                       , N'ğ', N'g'), N'Ğ', N'G')
                       , N'ı', N'i'), N'İ', N'I')
                       , N'ö', N'o'), N'Ö', N'O')
                       , N'ş', N's'), N'Ş', N'S')
                       , N'ü', N'u'), N'Ü', N'U'));
");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_ProductId_Axis1ValueNorm_Axis2ValueNorm",
                table: "ProductVariants",
                columns: new[] { "ProductId", "Axis1ValueNorm", "Axis2ValueNorm" },
                unique: true);

            // Eski kolonlar en sona bırakıldı: geri doldurma yalnız Axis1Value/
            // Axis2Value'yu okuyor, ama kolonları düşürmek geri dönülmez adım —
            // ondan önceki her şeyin (doldurma + benzersiz indeks) başarılı
            // olduğu bilinmeden atılmamalı.
            migrationBuilder.DropColumn(
                name: "Axis1Code",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "Axis2Code",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "VariantCode",
                table: "ProductVariants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Geri alma tek yönlü DEĞİL ama veri geri gelmez: VariantCode/Axis*Code
            // türetilmiş kolonlardı ve kaynakları (VariantCodeBuilder,
            // AxisCodeDeriver) bu sürümde silindi. Bu Down yalnız şemayı geri
            // kurar; birden fazla varyantı olan bir üründe eski benzersiz indeks
            // boş kodlar yüzünden kurulamaz. Gerçek geri dönüş yolu: yedekten
            // geri yükleme.
            migrationBuilder.DropIndex(
                name: "IX_ProductVariants_ProductId_Axis1ValueNorm_Axis2ValueNorm",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "Axis1ValueNorm",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "Axis2ValueNorm",
                table: "ProductVariants");

            migrationBuilder.AddColumn<string>(
                name: "Axis1Code",
                table: "ProductVariants",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Axis2Code",
                table: "ProductVariants",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VariantCode",
                table: "ProductVariants",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_LicenseId_VariantCode",
                table: "ProductVariants",
                columns: new[] { "LicenseId", "VariantCode" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_ProductId_VariantCode",
                table: "ProductVariants",
                columns: new[] { "ProductId", "VariantCode" },
                unique: true);
        }
    }
}
