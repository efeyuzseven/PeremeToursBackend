using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeparateBankRefundAndProviderCancellation : Migration
    {
        private static readonly string[] ProviderQueueColumns = ["ProviderStatus", "ProviderNextAttemptAtUtc"];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BankReversalStartedAtUtc",
                table: "TicketCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProviderAttemptCount",
                table: "TicketCancellations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProviderFailureCode",
                table: "TicketCancellations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderLockedUntilUtc",
                table: "TicketCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderNextAttemptAtUtc",
                table: "TicketCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProviderStatus",
                table: "TicketCancellations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_TicketCancellations_ProviderStatus_ProviderNextAttemptAtUtc",
                table: "TicketCancellations",
                columns: ProviderQueueColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TicketCancellations_ProviderStatus_ProviderNextAttemptAtUtc",
                table: "TicketCancellations");

            migrationBuilder.DropColumn(
                name: "BankReversalStartedAtUtc",
                table: "TicketCancellations");

            migrationBuilder.DropColumn(
                name: "ProviderAttemptCount",
                table: "TicketCancellations");

            migrationBuilder.DropColumn(
                name: "ProviderFailureCode",
                table: "TicketCancellations");

            migrationBuilder.DropColumn(
                name: "ProviderLockedUntilUtc",
                table: "TicketCancellations");

            migrationBuilder.DropColumn(
                name: "ProviderNextAttemptAtUtc",
                table: "TicketCancellations");

            migrationBuilder.DropColumn(
                name: "ProviderStatus",
                table: "TicketCancellations");
        }
    }
}
