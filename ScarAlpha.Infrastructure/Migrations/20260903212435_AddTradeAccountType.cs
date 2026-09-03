using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScarAlpha.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeAccountType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountType",
                table: "trades",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_trades_UserId_AccountType",
                table: "trades",
                columns: new[] { "UserId", "AccountType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_trades_UserId_AccountType",
                table: "trades");

            migrationBuilder.DropColumn(
                name: "AccountType",
                table: "trades");
        }
    }
}
