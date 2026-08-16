using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ScarAlpha.Infrastructure.Persistence;

#nullable disable

namespace ScarAlpha.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(AppDbContext))]
[Migration("20260816210000_BinollaStoredCredentials")]
public partial class BinollaStoredCredentials : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "EncryptedBinollaEmail",
            table: "binolla_links",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EncryptedBinollaPassword",
            table: "binolla_links",
            type: "text",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "EncryptedBinollaEmail",
            table: "binolla_links");

        migrationBuilder.DropColumn(
            name: "EncryptedBinollaPassword",
            table: "binolla_links");
    }
}
