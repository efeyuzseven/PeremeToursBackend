using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnableTourPayments : Migration
    {
        private static readonly string[] PassengerSequenceColumns = ["TourTicketId", "Sequence"];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalVoucherGuid",
                table: "TourTickets",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PaymentAttemptId",
                table: "TourTickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketingFailureCode",
                table: "TourTickets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketingStatus",
                table: "TourTickets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotRequired");

            migrationBuilder.CreateTable(
                name: "TourPassengers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TourTicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    ExternalPriceId = table.Column<int>(type: "integer", nullable: false),
                    UnitAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LastName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Gender = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Nationality = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IdentityNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExternalTicketGuid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Pnr = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TourPassengers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TourPassengers_TourTickets_TourTicketId",
                        column: x => x.TourTicketId,
                        principalTable: "TourTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TourTickets_PaymentAttemptId",
                table: "TourTickets",
                column: "PaymentAttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TourPassengers_TourTicketId_Sequence",
                table: "TourPassengers",
                columns: PassengerSequenceColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TourPassengers");

            migrationBuilder.DropIndex(
                name: "IX_TourTickets_PaymentAttemptId",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "ExternalVoucherGuid",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "PaymentAttemptId",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "TicketingFailureCode",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "TicketingStatus",
                table: "TourTickets");
        }
    }
}
