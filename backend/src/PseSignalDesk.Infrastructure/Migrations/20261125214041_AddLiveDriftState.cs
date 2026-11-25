using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveDriftState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LiveDrift",
                table: "TrainedHorizonModels",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "STABLE");

            migrationBuilder.AddColumn<int>(
                name: "LiveDriftGradedPicks",
                table: "TrainedHorizonModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LiveDrift",
                table: "TrainedHorizonModels");

            migrationBuilder.DropColumn(
                name: "LiveDriftGradedPicks",
                table: "TrainedHorizonModels");
        }
    }
}
