using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBroadcastPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BroadcastPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    MediaObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    MediaContentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MediaSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    MediaDurationSec = table.Column<int>(type: "int", nullable: true),
                    MediaWidth = table.Column<int>(type: "int", nullable: true),
                    MediaHeight = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BroadcastPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BroadcastPosts_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BroadcastPosts_ExpiresAt_IsPinned",
                table: "BroadcastPosts",
                columns: new[] { "ExpiresAt", "IsPinned" });

            migrationBuilder.CreateIndex(
                name: "IX_BroadcastPosts_LicenseId_CreatedAt",
                table: "BroadcastPosts",
                columns: new[] { "LicenseId", "CreatedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BroadcastPosts");
        }
    }
}
