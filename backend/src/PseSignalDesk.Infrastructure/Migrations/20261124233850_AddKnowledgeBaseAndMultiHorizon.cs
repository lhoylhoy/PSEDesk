using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeBaseAndMultiHorizon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Sma150",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Week52High",
                table: "StockDailyMetrics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WeinsteinStage",
                table: "StockDailyMetrics",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BoardLotShares",
                table: "SignalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CitedReferencesJson",
                table: "SignalItems",
                type: "TEXT",
                maxLength: 600,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    HorizonsCsv = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CategoriesCsv = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    IsMandatory = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ChunkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeChunks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KnowledgeDocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    SectionTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    HorizonsCsv = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CategoriesCsv = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    EmbeddingBlob = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EmbeddingDimensions = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_KnowledgeDocuments_KnowledgeDocumentId",
                        column: x => x.KnowledgeDocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_KnowledgeDocumentId",
                table: "KnowledgeChunks",
                column: "KnowledgeDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_Reference",
                table: "KnowledgeChunks",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_Slug",
                table: "KnowledgeDocuments",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_Source",
                table: "KnowledgeDocuments",
                column: "Source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeChunks");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "Sma150",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "Week52High",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "WeinsteinStage",
                table: "StockDailyMetrics");

            migrationBuilder.DropColumn(
                name: "BoardLotShares",
                table: "SignalItems");

            migrationBuilder.DropColumn(
                name: "CitedReferencesJson",
                table: "SignalItems");
        }
    }
}
