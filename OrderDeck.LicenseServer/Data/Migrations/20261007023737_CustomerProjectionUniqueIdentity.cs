using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerProjectionUniqueIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // KAPI — indekse dokunmadan ÖNCE: kopyalı asıl kayıt varsa indeks
            // kurulamaz. Yalnız okunur bir mesajla düşer, başka hiçbir şey yapmaz
            // (veriyi burada düzeltmek açılışta gözetimsiz bir birleştirme olurdu).
            // Düşen göç kayda geçmez (göç işlemi geri alınır), açılıştaki Migrate()
            // fırlatır, /ready gelmez ve deploy önceki imaja (PR-1) otomatik döner —
            // veri bozulmaz. Ön koşul: PR-1 prod'da, E2 (merge-customer-identities
            // --all --apply) koşmuş, kuru çalıştırma kopyalı kişi=0, B1 kapısı=0,
            // zincir=0 göstermiş.
            //
            // Sayım CustomerIdentityMergeJob.DuplicateHeadsSql'in (CLI'nin "B1
            // kapısı" satırı) BAYT BAYT kopyası: operatörün gördüğü sayı burada
            // denetlenen sayıdır. Kopya, çünkü göç tarihsel bir belgedir — uygulama
            // sabitine bağlansaydı sabitteki sonraki bir değişiklik bu göçü sessizce
            // değiştirirdi. İkisi EŞİT kalmalı; kaymayı
            // CustomerProjectionUniqueIdentityMigrationTests yakalar.
            //
            // Onarılmamış (NEWID varsayılanlı) anahtar için kapı YOK, bilerek:
            // NEWID benzersizdir, indeksi engelleyemez; açılışta Migrate'ten sonra
            // kuyruğa alınan identity-key-repair onu düzeltir, aynı kişinin asıl
            // kaydıyla çakışırsa o lisansı birleştirip yeniden onarır. Yalnız
            // boşluktan oluşan kullanıcı adının NEWID anahtarı hiç onarılmaz (boş
            // anahtar yazılmaz) ama zararsızdır: benzersiz, kimlik değil. Bir kapı
            // o satıra takılır ve — hiçbir ön denetimde görünmeden — açılışı kalıcı
            // olarak düşürürdü.
            migrationBuilder.Sql("""
                IF (SELECT COUNT(*) FROM (SELECT 1 x FROM WpfCustomerProjections WHERE MergedIntoId IS NULL AND IdentityKey <> N'' GROUP BY LicenseId, Platform, IdentityKey HAVING COUNT(*) > 1) d) > 0
                    THROW 50000, N'B1: kopyalı asıl kayıt var — önce merge-customer-identities --all --apply koşun', 1;
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
