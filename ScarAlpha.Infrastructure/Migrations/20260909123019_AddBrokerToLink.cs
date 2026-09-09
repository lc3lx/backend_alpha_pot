using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScarAlpha.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBrokerToLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Broker",
                table: "binolla_links",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "binolla")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Broker",
                table: "binolla_links");
        }
    }
}
