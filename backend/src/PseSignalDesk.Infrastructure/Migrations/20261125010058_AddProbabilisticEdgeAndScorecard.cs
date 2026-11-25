using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProbabilisticEdgeAndScorecard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AtrPct",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EmpiricalWinRate",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HighPrice",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LowPrice",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OpenPrice",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RelativeStrength63d",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VolatilityContractionRatio",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BreadthAboveSma200Pct",
                table: "SignalRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BreadthAboveSma50Pct",
                table: "SignalRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EvaluatedStockCount",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RecommendedCashPct",
                table: "SignalRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Stage2Count",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BarsHeld",
                table: "SignalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BearCase",
                table: "SignalItems",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "BreakEvenWinRate",
                table: "SignalItems",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "EdgeVerdict",
                table: "SignalItems",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "WATCH_ONLY");

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedValueR",
                table: "SignalItems",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "NetRiskRewardRatio",
                table: "SignalItems",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "OutcomeDate",
                table: "SignalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutcomeExitPrice",
                table: "SignalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeStatus",
                table: "SignalItems",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "OPEN");

            migrationBuilder.AddColumn<decimal>(
                name: "QuarterKellyFraction",
                table: "SignalItems",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RealizedNetR",
                table: "SignalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WinProbability",
                table: "SignalItems",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BreadthAboveSma200Pct",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BreadthAboveSma50Pct",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EvaluatedStockCount",
                table: "DailyMarketSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Stage2Count",
                table: "DailyMarketSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignalItems_OutcomeStatus",
                table: "SignalItems",
                column: "OutcomeStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SignalItems_OutcomeStatus",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "AtrPct",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "EmpiricalWinRate",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "HighPrice",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "LowPrice",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "OpenPrice",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "RelativeStrength63d",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "VolatilityContractionRatio",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "BreadthAboveSma200Pct",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "BreadthAboveSma50Pct",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "EvaluatedStockCount",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "RecommendedCashPct",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "Stage2Count",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "BarsHeld",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "BearCase",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "BreakEvenWinRate",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "EdgeVerdict",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "ExpectedValueR",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "NetRiskRewardRatio",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "OutcomeDate",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "OutcomeExitPrice",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "OutcomeStatus",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "QuarterKellyFraction",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "RealizedNetR",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "WinProbability",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "BreadthAboveSma200Pct",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "BreadthAboveSma50Pct",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "EvaluatedStockCount",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "Stage2Count",
                table: "DailyMarketSnapshots");
        }
    }
}
