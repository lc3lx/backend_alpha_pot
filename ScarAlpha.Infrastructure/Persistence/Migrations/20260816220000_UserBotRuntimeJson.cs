using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ScarAlpha.Infrastructure.Persistence;

#nullable disable

namespace ScarAlpha.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260816220000_UserBotRuntimeJson")]
public partial class UserBotRuntimeJson : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BotRuntimeJson",
            table: "users",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BotRuntimeJson",
            table: "users");
    }
}
