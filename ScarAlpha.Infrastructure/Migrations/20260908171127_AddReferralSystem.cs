using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScarAlpha.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReferralSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                table: "users",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReferredAt",
                table: "users",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReferredByUserId",
                table: "users",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "referral_commissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ReferrerUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ReferredUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TradeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TradeAmount = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    RatePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Tier = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_referral_commissions", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "referral_payouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Amount = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Destination = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    DecidedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AdminNote = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_referral_payouts", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "referral_qualifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ReferredUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ReferrerUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PeakRealBalance = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    DepositMet = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DepositMetAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    AdminDepositOverride = table.Column<bool>(type: "tinyint(1)", nullable: true),
                    AdminDepositNote = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AdminDepositBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AdminDepositAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    WindowStartedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    FirstBotTradeAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    LastBotTradeAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    BotTradeCount = table.Column<int>(type: "int", nullable: false),
                    ActiveDaysCount = table.Column<int>(type: "int", nullable: false),
                    LastActiveDayUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    Qualified = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    QualifiedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_referral_qualifications", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "referral_rewards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Tier = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    PaidBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Note = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_referral_rewards", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_users_ReferralCode",
                table: "users",
                column: "ReferralCode",
                unique: true,
                filter: "`ReferralCode` IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_users_ReferredByUserId",
                table: "users",
                column: "ReferredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_referral_commissions_ReferredUserId",
                table: "referral_commissions",
                column: "ReferredUserId");

            migrationBuilder.CreateIndex(
                name: "IX_referral_commissions_ReferrerUserId",
                table: "referral_commissions",
                column: "ReferrerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_referral_commissions_TradeId",
                table: "referral_commissions",
                column: "TradeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_referral_payouts_Status",
                table: "referral_payouts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_referral_payouts_UserId",
                table: "referral_payouts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_referral_qualifications_Qualified",
                table: "referral_qualifications",
                column: "Qualified");

            migrationBuilder.CreateIndex(
                name: "IX_referral_qualifications_ReferredUserId",
                table: "referral_qualifications",
                column: "ReferredUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_referral_qualifications_ReferrerUserId",
                table: "referral_qualifications",
                column: "ReferrerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_referral_rewards_UserId_Kind_Tier",
                table: "referral_rewards",
                columns: new[] { "UserId", "Kind", "Tier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "referral_commissions");

            migrationBuilder.DropTable(
                name: "referral_payouts");

            migrationBuilder.DropTable(
                name: "referral_qualifications");

            migrationBuilder.DropTable(
                name: "referral_rewards");

            migrationBuilder.DropIndex(
                name: "IX_users_ReferralCode",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_ReferredByUserId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ReferralCode",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ReferredAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ReferredByUserId",
                table: "users");
        }
    }
}
