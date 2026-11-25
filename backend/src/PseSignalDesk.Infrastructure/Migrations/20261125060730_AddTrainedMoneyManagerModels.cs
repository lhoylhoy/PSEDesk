using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTrainedMoneyManagerModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrainedHorizonModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Horizon = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TrainedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrainStartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TrainEndDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ValidationStartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ValidationEndDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TrainSampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ValidationSampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EmbargoBars = table.Column<int>(type: "INTEGER", nullable: false),
                    BaselineBrierScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    TrainedBrierScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    BaselineLogLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    TrainedLogLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    ValidationWinRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    ValidationExpectancyR = table.Column<decimal>(type: "TEXT", nullable: false),
                    ValidationProfitFactor = table.Column<decimal>(type: "TEXT", nullable: false),
                    OptimalKellyFractionMultiplier = table.Column<decimal>(type: "TEXT", nullable: false),
                    Intercept = table.Column<decimal>(type: "TEXT", nullable: false),
                    WeightsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FeatureStatsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainedHorizonModels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrainedHorizonModels_Horizon",
                table: "TrainedHorizonModels",
                column: "Horizon",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrainedHorizonModels");
        }
    }
}
