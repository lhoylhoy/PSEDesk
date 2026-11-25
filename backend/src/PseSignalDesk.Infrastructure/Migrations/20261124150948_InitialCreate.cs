using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyMarketSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TradeDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PseiClose = table.Column<decimal>(type: "TEXT", nullable: false),
                    PseiSma50 = table.Column<decimal>(type: "TEXT", nullable: false),
                    PseiSma200 = table.Column<decimal>(type: "TEXT", nullable: false),
                    NetForeignFlowPhp = table.Column<decimal>(type: "TEXT", nullable: false),
                    UsdPhp = table.Column<decimal>(type: "TEXT", nullable: false),
                    MarketRegime = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RegimeNote = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyMarketSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SignalRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Horizon = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawModelResponseJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockDailyMetrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TradeDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Sector = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ClosePrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    ChangePct = table.Column<decimal>(type: "TEXT", nullable: false),
                    AvgTurnover20dPhp = table.Column<decimal>(type: "TEXT", nullable: false),
                    VolumeRatioVs20d = table.Column<decimal>(type: "TEXT", nullable: false),
                    Rsi14 = table.Column<decimal>(type: "TEXT", nullable: false),
                    Ema20 = table.Column<decimal>(type: "TEXT", nullable: false),
                    Sma50 = table.Column<decimal>(type: "TEXT", nullable: false),
                    Sma200 = table.Column<decimal>(type: "TEXT", nullable: false),
                    Atr14 = table.Column<decimal>(type: "TEXT", nullable: false),
                    SupportLevel = table.Column<decimal>(type: "TEXT", nullable: false),
                    ResistanceLevel = table.Column<decimal>(type: "TEXT", nullable: false),
                    NetForeignTodayPhp = table.Column<decimal>(type: "TEXT", nullable: false),
                    RecentDisclosuresJson = table.Column<string>(type: "TEXT", nullable: false),
                    QuantPreScore = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockDailyMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SignalDrops",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SignalRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PreviousRank = table.Column<int>(type: "INTEGER", nullable: true),
                    DropCategory = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalDrops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignalDrops_SignalRuns_SignalRunId",
                        column: x => x.SignalRunId,
                        principalTable: "SignalRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignalItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SignalRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DriftStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PreviousRank = table.Column<int>(type: "INTEGER", nullable: true),
                    ConvictionScore = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    StopLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    TargetPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    RiskRewardRatio = table.Column<decimal>(type: "TEXT", nullable: false),
                    Thesis = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    DeviationReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignalItems_SignalRuns_SignalRunId",
                        column: x => x.SignalRunId,
                        principalTable: "SignalRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyMarketSnapshots_TradeDate",
                table: "DailyMarketSnapshots",
                column: "TradeDate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignalDrops_SignalRunId",
                table: "SignalDrops",
                column: "SignalRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SignalDrops_Ticker",
                table: "SignalDrops",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_SignalItems_SignalRunId_Rank",
                table: "SignalItems",
                columns: new[] { "SignalRunId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignalItems_Ticker",
                table: "SignalItems",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_SignalRuns_RunDate_Horizon_CreatedAtUtc",
                table: "SignalRuns",
                columns: new[] { "RunDate", "Horizon", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StockDailyMetrics_Ticker",
                table: "StockDailyMetrics",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_StockDailyMetrics_TradeDate_Ticker",
                table: "StockDailyMetrics",
                columns: new[] { "TradeDate", "Ticker" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyMarketSnapshots");

            migrationBuilder.DropTable(
                name: "SignalDrops");

            migrationBuilder.DropTable(
                name: "SignalItems");

            migrationBuilder.DropTable(
                name: "StockDailyMetrics");

            migrationBuilder.DropTable(
                name: "SignalRuns");
        }
    }
}
