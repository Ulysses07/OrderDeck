using Microsoft.EntityFrameworkCore.Migrations;
using OrderDeck.LicenseServer.Services.CustomerSync;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerProjectionUniqueIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // KAPILAR — indekse dokunmadan ÖNCE. İkisi de yalnız okunur bir mesajla
            // düşer, başka hiçbir şey yapmaz (veriyi burada düzeltmeye kalkmak,
            // açılışta, gözetimsiz bir birleştirme olurdu). Düşen göç kayda
            // geçmez (göç işlemi geri alınır), açılıştaki Migrate() fırlatır,
            // /ready gelmez ve deploy önceki imaja (PR-1) otomatik döner — veri
            // bozulmaz. Ön koşul: PR-1 prod'da, E2 (merge-customer-identities
            // --all --apply) koşmuş, kuru çalıştırma kopyalı kişi=0, B1 kapısı=0,
            // zincir=0 göstermiş.
            //
            // (1) Kopyalı asıl kayıt: indeks bu hâlde kurulamaz. Sayım CLI'nin
            // "B1 kapısı" satırının KENDİSİ (sabit kopyalanmadı, kullanıldı):
            // operatörün gördüğü sayı burada denetlenen sayıdır. Bu göç bir kez
            // koştuktan sonra sabitin değişmesi uygulanmış veritabanını etkilemez;
            // boş (yeni) veritabanında sayım zaten 0.
            migrationBuilder.Sql($"""
                IF ({CustomerIdentityMergeJob.DuplicateHeadsSql}) > 0
                    THROW 50000, N'B1: kopyalı asıl kayıt var — önce merge-customer-identities --all --apply koşun', 1;
                """);

            // (2) Onarılmamış kimlik anahtarı: geri alma penceresinde kolonu
            // tanımayan imajın açtığı satırda NEWID() varsayılanı kalmıştır.
            // Benzersiz olduğu için indeksi engellemez, ama aynı kişinin asıl
            // kaydıyla buluşmamış bir kopyadır — indeks kurulunca onarım işi o
            // satırı düzeltirken çakışır. Kolon BIN2: [A-Z] yalnız büyük ASCII
            // harfi yakalar; IdentityKeyOf ve SQL LOWER onu asla üretmez, NEWID
            // üretir (onaltılık, büyük harf).
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM WpfCustomerProjections WHERE IdentityKey LIKE N'%[A-Z]%')
                    THROW 50000, N'B1: onarılmamış kimlik anahtarı var — önce identity-key-repair koşun (PR-1 imajı açılışta koşar)', 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_WpfCustomerProjections_LicenseId_Platform_IdentityKey",
                table: "WpfCustomerProjections");

            migrationBuilder.CreateIndex(
                name: "UX_WpfCustomerProjections_Identity",
                table: "WpfCustomerProjections",
                columns: new[] { "LicenseId", "Platform", "IdentityKey" },
                unique: true,
                filter: "[MergedIntoId] IS NULL AND [IdentityKey] <> N''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_WpfCustomerProjections_Identity",
                table: "WpfCustomerProjections");

            migrationBuilder.CreateIndex(
                name: "IX_WpfCustomerProjections_LicenseId_Platform_IdentityKey",
                table: "WpfCustomerProjections",
                columns: new[] { "LicenseId", "Platform", "IdentityKey" });
        }
    }
}
