using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscordWordleBot.Migrations
{
    /// <inheritdoc />
    public partial class AddHardModeToWordleSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HardMode",
                table: "WordleUserSetting",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HardMode",
                table: "WordleUserSetting");
        }
    }
}
