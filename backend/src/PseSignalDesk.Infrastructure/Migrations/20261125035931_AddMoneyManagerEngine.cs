using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMoneyManagerEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Beta252d",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChaikinMoneyFlow20d",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EmpiricalWinRateDay",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EmpiricalWinRateMonth",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EmpiricalWinRateYear",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxDrawdown252dPct",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TrendEfficiency63d",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UpDownVolumeRatio20d",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HardDrawdownThresholdPct",
                table: "PortfolioAccounts",
                type: "TEXT",
                nullable: false,
                defaultValue: 10m);

            migrationBuilder.AddColumn<int>(
                name: "MaxPositionsPerSector",
                table: "PortfolioAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxSectorExposurePct",
                table: "PortfolioAccounts",
                type: "TEXT",
                nullable: false,
                defaultValue: 35m);

            migrationBuilder.AddColumn<decimal>(
                name: "PeakEquityPhp",
                table: "PortfolioAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SoftDrawdownThresholdPct",
                table: "PortfolioAccounts",
                type: "TEXT",
                nullable: false,
                defaultValue: 5m);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetAnnualVolPct",
                table: "PortfolioAccounts",
                type: "TEXT",
                nullable: false,
                defaultValue: 18m);

            migrationBuilder.AddColumn<decimal>(
                name: "PseiRealizedVol20dPct",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SectorRotationJson",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UsdPhpChange20dPct",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UsdPhpSma50",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Beta252d",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "ChaikinMoneyFlow20d",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "EmpiricalWinRateDay",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "EmpiricalWinRateMonth",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "EmpiricalWinRateYear",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "MaxDrawdown252dPct",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "TrendEfficiency63d",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "UpDownVolumeRatio20d",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "HardDrawdownThresholdPct",
                table: "PortfolioAccounts");

            migrationBuilder.DropColumn(
                name: "MaxPositionsPerSector",
                table: "PortfolioAccounts");

            migrationBuilder.DropColumn(
                name: "MaxSectorExposurePct",
                table: "PortfolioAccounts");

            migrationBuilder.DropColumn(
                name: "PeakEquityPhp",
                table: "PortfolioAccounts");

            migrationBuilder.DropColumn(
                name: "SoftDrawdownThresholdPct",
                table: "PortfolioAccounts");

            migrationBuilder.DropColumn(
                name: "TargetAnnualVolPct",
                table: "PortfolioAccounts");

            migrationBuilder.DropColumn(
                name: "PseiRealizedVol20dPct",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "SectorRotationJson",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "UsdPhpChange20dPct",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "UsdPhpSma50",
                table: "DailyMarketSnapshots");
        }
    }
}
