using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioResearchAndDividends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastExDividendDate",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TrailingDividendPerShare",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TrailingDividendYieldPct",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PortfolioAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartingCapitalPhp = table.Column<decimal>(type: "TEXT", nullable: false, defaultValue: 100000m),
                    CashBalancePhp = table.Column<decimal>(type: "TEXT", nullable: false, defaultValue: 100000m),
                    MaxRiskPerTradePct = table.Column<decimal>(type: "TEXT", nullable: false, defaultValue: 1.0m),
                    SmallSizeRiskMultiplier = table.Column<decimal>(type: "TEXT", nullable: false, defaultValue: 0.5m),
                    MaxPositionPct = table.Column<decimal>(type: "TEXT", nullable: false, defaultValue: 20.0m),
                    MaxPortfolioHeatPct = table.Column<decimal>(type: "TEXT", nullable: false, defaultValue: 6.0m),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioTrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Sector = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Horizon = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EdgeVerdict = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SignalItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OpenedDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Shares = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    InitialStopLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentStopLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    TargetPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    BuyCostPhp = table.Column<decimal>(type: "TEXT", nullable: false),
                    InitialNetRiskPhp = table.Column<decimal>(type: "TEXT", nullable: false),
                    ClosedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    SellProceedsPhp = table.Column<decimal>(type: "TEXT", nullable: true),
                    RealizedNetPnlPhp = table.Column<decimal>(type: "TEXT", nullable: true),
                    RealizedNetR = table.Column<decimal>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioTrades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioTrades_Status",
                table: "PortfolioTrades",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioTrades_Ticker",
                table: "PortfolioTrades",
                column: "Ticker");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortfolioAccounts");

            migrationBuilder.DropTable(
                name: "PortfolioTrades");

            migrationBuilder.DropColumn(
                name: "LastExDividendDate",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "TrailingDividendPerShare",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "TrailingDividendYieldPct",
                table: "StockDailyMetrics");
        }
    }
}
