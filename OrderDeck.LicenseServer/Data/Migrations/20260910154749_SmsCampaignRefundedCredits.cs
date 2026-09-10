using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class SmsCampaignRefundedCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RefundedCredits",
                table: "SmsCampaigns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill: tamamlanmış kampanyalarda job iadeyi tam
            // failed × segment olarak yapmıştı — geçmiş için hesap ile
            // gerçekleşen aynı; sürmekte olanlar 0 kalır (henüz iade yok).
            migrationBuilder.Sql("""
                UPDATE c SET c.RefundedCredits = f.FailedCount * c.SegmentsPerMessage
                FROM SmsCampaigns c
                JOIN (
                    SELECT CampaignId, COUNT(*) AS FailedCount
                    FROM SmsCampaignRecipients
                    WHERE Status = 'failed'
                    GROUP BY CampaignId
                ) f ON f.CampaignId = c.Id
                WHERE c.Status = 'completed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefundedCredits",
                table: "SmsCampaigns");
        }
    }
}
