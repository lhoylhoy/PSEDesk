using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalRankingProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletionTokens",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EngineName",
                table: "SignalRuns",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "FastExtractionApplied",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ModelLatencyMs",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModelName",
                table: "SignalRuns",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ModelRetryCount",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ModelSeed",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ModelTemperature",
                table: "SignalRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "PromptCharacters",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PromptHash",
                table: "SignalRuns",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SchemaConstrained",
                table: "SignalRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "QuantPreScore",
                table: "SignalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QuantRank",
                table: "SignalItems",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "EngineName",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "FastExtractionApplied",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "ModelLatencyMs",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "ModelName",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "ModelRetryCount",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "ModelSeed",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "ModelTemperature",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "PromptCharacters",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "PromptHash",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "SchemaConstrained",
                table: "SignalRuns");

            migrationBuilder.DropColumn(
                name: "QuantPreScore",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "QuantRank",
                table: "SignalItems");
        }
    }
}
