using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentPages : Migration
    {
        private static readonly string[] PublishedColumns = ["Kind", "IsPublished", "PublishedAtUtc"];
        private static readonly string[] EnglishSlugColumns = ["Kind", "SlugEn"];
        private static readonly string[] TurkishSlugColumns = ["Kind", "SlugTr"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CoverImageUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ShowInFooter = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    TitleTr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SlugTr = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SummaryTr = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BodyTr = table.Column<string>(type: "text", nullable: false),
                    MetaTitleTr = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    MetaDescriptionTr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TitleEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SlugEn = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SummaryEn = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BodyEn = table.Column<string>(type: "text", nullable: true),
                    MetaTitleEn = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    MetaDescriptionEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentPages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentPages_Kind_IsPublished_PublishedAtUtc",
                table: "ContentPages",
                columns: PublishedColumns);

            migrationBuilder.CreateIndex(
                name: "IX_ContentPages_Kind_SlugEn",
                table: "ContentPages",
                columns: EnglishSlugColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentPages_Kind_SlugTr",
                table: "ContentPages",
                columns: TurkishSlugColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentPages");
        }
    }
}
