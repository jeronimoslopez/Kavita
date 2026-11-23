using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kavita.Database.Migrations
{
    /// <inheritdoc />
    public partial class DictionaryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableDictionary",
                table: "Library",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BookReaderDictionaryEnabled",
                table: "AppUserReadingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableDictionary",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "BookReaderDictionaryEnabled",
                table: "AppUserReadingProfiles");
        }
    }
}
