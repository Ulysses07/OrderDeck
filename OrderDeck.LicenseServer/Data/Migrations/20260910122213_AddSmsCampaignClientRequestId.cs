using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSmsCampaignClientRequestId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "SmsCampaigns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SmsCampaigns_LicenseId_ClientRequestId",
                table: "SmsCampaigns",
                columns: new[] { "LicenseId", "ClientRequestId" },
                unique: true,
                filter: "[ClientRequestId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SmsCampaigns_LicenseId_ClientRequestId",
                table: "SmsCampaigns");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "SmsCampaigns");
        }
    }
}
