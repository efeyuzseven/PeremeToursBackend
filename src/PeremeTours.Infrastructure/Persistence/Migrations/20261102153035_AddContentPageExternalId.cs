using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentPageExternalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "ContentPages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentPages_ExternalId",
                table: "ContentPages",
                column: "ExternalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContentPages_ExternalId",
                table: "ContentPages");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "ContentPages");
        }
    }
}
