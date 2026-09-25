using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddZiraatPaymentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BankAuthCode",
                table: "TourTickets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankHostReference",
                table: "TourTickets",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerPhone",
                table: "TourTickets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalDepartureId",
                table: "TourTickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalDeparturePortId",
                table: "TourTickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalPriceId",
                table: "TourTickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalTourId",
                table: "TourTickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalTripId",
                table: "TourTickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAtUtc",
                table: "TourTickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentFailureCode",
                table: "TourTickets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentFailureMessage",
                table: "TourTickets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentProvider",
                table: "TourTickets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentStatus",
                table: "TourTickets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotRequired");

            migrationBuilder.CreateIndex(
                name: "IX_TourTickets_BankHostReference",
                table: "TourTickets",
                column: "BankHostReference");

            migrationBuilder.CreateIndex(
                name: "IX_TourTickets_PaymentStatus",
                table: "TourTickets",
                column: "PaymentStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TourTickets_BankHostReference",
                table: "TourTickets");

            migrationBuilder.DropIndex(
                name: "IX_TourTickets_PaymentStatus",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "BankAuthCode",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "BankHostReference",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "CustomerPhone",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "ExternalDepartureId",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "ExternalDeparturePortId",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "ExternalPriceId",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "ExternalTourId",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "ExternalTripId",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "PaidAtUtc",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "PaymentFailureCode",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "PaymentFailureMessage",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "PaymentProvider",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "PaymentStatus",
                table: "TourTickets");
        }
    }
}
