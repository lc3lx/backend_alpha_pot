using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScarAlpha.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BinollaLinkReferralFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BinollaAccountIdentifier",
                table: "binolla_links",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReferralCheckedAt",
                table: "binolla_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReferralStatus",
                table: "binolla_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BinollaAccountIdentifier",
                table: "binolla_links");

            migrationBuilder.DropColumn(
                name: "ReferralCheckedAt",
                table: "binolla_links");

            migrationBuilder.DropColumn(
                name: "ReferralStatus",
                table: "binolla_links");
        }
    }
}
