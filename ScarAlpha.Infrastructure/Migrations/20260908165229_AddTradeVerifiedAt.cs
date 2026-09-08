using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScarAlpha.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeVerifiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VerifiedAt",
                table: "trades",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "trades");
        }
    }
}
