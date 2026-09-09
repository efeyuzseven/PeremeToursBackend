using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTourContents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TourContents",
                columns: table => new
                {
                    ExternalTourId = table.Column<int>(type: "integer", nullable: false),
                    TitleTr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TitleEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DescriptionTr = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: true),
                    DescriptionEn = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: true),
                    BadgeTr = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    BadgeEn = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ImageObjectKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ImageContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: true),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TourContents", x => x.ExternalTourId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TourContents_SortOrder",
                table: "TourContents",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TourContents");
        }
    }
}
