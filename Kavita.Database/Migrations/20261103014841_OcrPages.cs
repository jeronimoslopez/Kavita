using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kavita.Database.Migrations
{
    /// <inheritdoc />
    public partial class OcrPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OcrPriority",
                table: "MangaFile",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "OcrRequestedUtc",
                table: "MangaFile",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OcrSourceWriteTimeUtc",
                table: "MangaFile",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OcrStatus",
                table: "MangaFile",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "EnableOcr",
                table: "Library",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OcrPage",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PageNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Lines = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    Engine = table.Column<string>(type: "TEXT", nullable: true),
                    EngineVersion = table.Column<string>(type: "TEXT", nullable: true),
                    ImageWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastModified = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MangaFileId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OcrPage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OcrPage_MangaFile_MangaFileId",
                        column: x => x.MangaFileId,
                        principalTable: "MangaFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MangaFile_OcrStatus",
                table: "MangaFile",
                column: "OcrStatus");

            migrationBuilder.CreateIndex(
                name: "IX_OcrPage_MangaFileId_PageNumber",
                table: "OcrPage",
                columns: new[] { "MangaFileId", "PageNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OcrPage");

            migrationBuilder.DropIndex(
                name: "IX_MangaFile_OcrStatus",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "OcrPriority",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "OcrRequestedUtc",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "OcrSourceWriteTimeUtc",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "OcrStatus",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "EnableOcr",
                table: "Library");
        }
    }
}
