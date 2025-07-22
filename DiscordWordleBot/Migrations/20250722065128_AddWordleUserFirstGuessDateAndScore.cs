using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscordWordleBot.Migrations
{
    /// <inheritdoc />
    public partial class AddWordleUserFirstGuessDateAndScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstGuessDate",
                table: "WordleUserSetting",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Score",
                table: "WordleUserSetting",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstGuessDate",
                table: "WordleUserSetting");

            migrationBuilder.DropColumn(
                name: "Score",
                table: "WordleUserSetting");
        }
    }
}
