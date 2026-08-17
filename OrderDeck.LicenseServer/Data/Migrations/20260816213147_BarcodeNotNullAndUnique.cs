using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class BarcodeNotNullAndUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Aşağıdaki doldurma KOŞULSUZ yazıyor; dolu bir barkodu ezerdi.
            // Bunu bugün imkânsız kılan şey kod değil YAYIN SIRASI: barkod
            // yazan ilk kod (varyant yollarındaki doldurma) bu göçle aynı
            // sürümde çıkıyor ve göçler uygulama trafiğe açılmadan önce
            // koşuyor. Sıra bozulursa bedel geri alınamaz — basılı etiket
            // sessizce geçersizleşir. Bu yüzden varsayımı doğrulukta değil
            // ÇALIŞMA ZAMANINDA çiviliyoruz: bir tek dolu barkod bile varsa
            // göç kendini geri sarar ve sorunu insana devreder.
            //
            // Mesaj bilerek ASCII: Sql() metni N'' öneki olmadan gönderiyor.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM ProductVariants
                           WHERE Barcode IS NOT NULL AND Barcode <> '')
                    THROW 50000, 'Barkodu dolu varyant var; goc mevcut barkodlari ezerdi.', 1;
                """);

            // Mevcut satırların hepsinin barkodu NULL (yukarıdaki bekçi bunu
            // garanti etti); lisans içinde 1'den başlayarak numaralıyoruz.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT Id,
                           ROW_NUMBER() OVER (PARTITION BY LicenseId ORDER BY CreatedAt, Id) AS rn
                    FROM ProductVariants
                )
                UPDATE pv
                SET pv.Barcode = RIGHT('0000000000' + CAST(n.rn AS varchar(10)), 10)
                FROM ProductVariants pv
                JOIN numbered n ON n.Id = pv.Id;
                """);

            // Sayacı doldurma sonrasına kur: bir sonraki numara, o lisansta
            // kullanılan en büyük sıranın bir fazlası olmalı. Kurulmasaydı ayırıcı
            // 1'den başlar ve ilk ayırmalar tek tek atlanarak ilerlerdi.
            migrationBuilder.Sql("""
                INSERT INTO BarcodeCounters (LicenseId, [Next])
                SELECT LicenseId, COUNT(*) + 1
                FROM ProductVariants
                GROUP BY LicenseId;
                """);

            // `defaultValue` BİLEREK verilmedi (üretilen hâlinde ""  vardı).
            // Verilseydi EF iki şey daha yazardı: kalan NULL'ları sessizce ''
            // yapan bir UPDATE ve sütunda kalıcı bir DEFAULT '' kısıtı. İkisi de
            // "barkodsuz varyant olamaz" değişmezini deler — biri boş barkodu
            // veriye sokar, öteki barkodu unutan bir INSERT'i patlatmak yerine
            // ödüllendirir. Varsayılansız hâlde yukarıdaki doldurma bir satırı
            // atlarsa ALTER gürültüyle başarısız olur; istediğimiz de bu.
            migrationBuilder.AlterColumn<string>(
                name: "Barcode",
                table: "ProductVariants",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_LicenseId_Barcode",
                table: "ProductVariants",
                columns: new[] { "LicenseId", "Barcode" },
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <b>TEK YÖNLÜ:</b> geri sarma sütunu nullable'a döndürür ama ne
        /// barkodları ne de tohumlanan sayaç satırlarını siler — hangilerinin
        /// NULL olduğu bilinemez, sayaçları silmek de uygulamanın kendi
        /// ayırdıklarını yok ederdi (numaralar 1'den başlar, çakışırdı).
        /// Sonuç: <c>Down</c>'dan sonra <c>Up</c> yeniden koşturulamaz —
        /// hem dolu-barkod bekçisi hem sayaç eklemesi patlar. Prod yalnız
        /// ileri yönde <c>Migrate()</c> çağırıyor.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductVariants_LicenseId_Barcode",
                table: "ProductVariants");

            migrationBuilder.AlterColumn<string>(
                name: "Barcode",
                table: "ProductVariants",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);
        }
    }
}
