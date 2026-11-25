using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCrossAssetMacroAndForeignFlowDqr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BrentChange20dPct",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BrentUsd",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EpheChange20dPct",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EpheCloseUsd",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForeignFlowSource",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Us10YChange20dBps",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Us10YClosePct",
                table: "DailyMarketSnapshots",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrentChange20dPct",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "BrentUsd",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "EpheChange20dPct",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "EpheCloseUsd",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "ForeignFlowSource",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "Us10YChange20dBps",
                table: "DailyMarketSnapshots");

            migrationBuilder.DropColumn(
                name: "Us10YClosePct",
                table: "DailyMarketSnapshots");
        }
    }
}
