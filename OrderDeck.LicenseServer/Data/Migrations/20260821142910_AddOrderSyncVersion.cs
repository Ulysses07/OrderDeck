using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// <c>Orders.SyncVersion</c> — sipariş senkron ucundaki eşzamanlılık jetonu
    /// (bkz. <c>Order.SyncVersion</c>). Ekleme: NOT NULL DEFAULT 0, yani var
    /// olan satırlar 0'dan başlar ve tablo yeniden yazılmaz.
    /// </summary>
    public partial class AddOrderSyncVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SyncVersion",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "Orders");
        }
    }
}
