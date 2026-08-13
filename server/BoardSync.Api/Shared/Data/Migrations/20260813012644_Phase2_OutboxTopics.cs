using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_OutboxTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "Topics",
                schema: "kernel",
                table: "OutboxMessages",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Topics",
                schema: "kernel",
                table: "OutboxMessages",
                column: "Topics")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Topics",
                schema: "kernel",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Topics",
                schema: "kernel",
                table: "OutboxMessages");
        }
    }
}
