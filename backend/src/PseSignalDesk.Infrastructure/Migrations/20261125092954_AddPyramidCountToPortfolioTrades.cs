using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PseSignalDesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPyramidCountToPortfolioTrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PyramidCount",
                table: "PortfolioTrades",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PyramidCount",
                table: "PortfolioTrades");
        }
    }
}
