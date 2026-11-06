using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEInvoiceRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoiceAddress",
                table: "TourTickets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceEmail",
                table: "TourTickets",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceTaxNumber",
                table: "TourTickets",
                type: "character varying(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceTaxOffice",
                table: "TourTickets",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceTitle",
                table: "TourTickets",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceType",
                table: "TourTickets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WantsEInvoice",
                table: "TourTickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceAddress",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "InvoiceEmail",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "InvoiceTaxNumber",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "InvoiceTaxOffice",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "InvoiceTitle",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "InvoiceType",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "WantsEInvoice",
                table: "TourTickets");
        }
    }
}
