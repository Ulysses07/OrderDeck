using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Zarf biçimi sütunu. <c>defaultValue: 0</c> tesadüf değil: mevcut her blob
    /// eski tek-atış zarfıyla yazıldı ve biçim 0 tam olarak o demek. Varsayılan
    /// başka bir şey olsaydı geçmiş bloblar parçalı zarf sanılır ve çözülemezdi —
    /// yani göç, müşterinin tek felaket kurtarma kopyasını okunmaz hâle getirirdi.
    /// Yeni yüklemeler değeri kodda açıkça 2 yazıyor.
    /// </summary>
    public partial class AddBackupEnvelopeFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnvelopeFormat",
                table: "CustomerBackups",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvelopeFormat",
                table: "CustomerBackups");
        }
    }
}
