using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentEmailAndTicketErrorRecords : Migration
    {
        private static readonly string[] EmailQueueColumns = ["Status", "NextAttemptAtUtc"];
        private static readonly string[] TicketErrorColumns = ["TicketId", "CreatedAtUtc"];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerLanguage",
                table: "TourTickets",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "tr");

            migrationBuilder.AddColumn<string>(
                name: "DeparturePortName",
                table: "TourTickets",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "PaymentEmails",
                columns: table => new
                {
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LockToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEmails", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_PaymentEmails_TourTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "TourTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketErrorRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsHistorical = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketErrorRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketErrorRecords_TourTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "TourTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEmails_Status_NextAttemptAtUtc",
                table: "PaymentEmails",
                columns: EmailQueueColumns);

            migrationBuilder.CreateIndex(
                name: "IX_TicketErrorRecords_CreatedAtUtc",
                table: "TicketErrorRecords",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TicketErrorRecords_TicketId_CreatedAtUtc",
                table: "TicketErrorRecords",
                columns: TicketErrorColumns);

            // Historical failures are visible, but never invent unavailable provider messages or send old customers emails.
            migrationBuilder.Sql("""
                INSERT INTO "TicketErrorRecords" ("Id", "TicketId", "Stage", "Code", "Message", "IsHistorical", "CreatedAtUtc")
                SELECT gen_random_uuid(), "Id", 'Payment',
                    CASE WHEN "PaymentFailureCode" ~ '^[0-9]{2,4}$'
                        OR "PaymentFailureCode" IN ('GATEWAY_START_FAILED', 'CALLBACK_VALIDATION_FAILED', 'BANK_RESULT_UNKNOWN')
                        THEN "PaymentFailureCode" ELSE 'HISTORICAL_PAYMENT_FAILED' END,
                    'Geçmiş başarısız veya belirsiz ödeme kaydı. Orijinal banka hata açıklaması saklanmamış olabilir. Sipariş koduyla banka panelinden kontrol edin.',
                    TRUE, "UpdatedAtUtc"
                FROM "TourTickets" WHERE "PaymentStatus" IN ('Failed', 'ReviewRequired');

                INSERT INTO "TicketErrorRecords" ("Id", "TicketId", "Stage", "Code", "Message", "IsHistorical", "CreatedAtUtc")
                SELECT gen_random_uuid(), "Id", 'Ticketing',
                    CASE WHEN "TicketingFailureCode" IN ('PROVIDER_RESULT_UNKNOWN', 'PROVIDER_RESULT_INCOMPLETE')
                        THEN "TicketingFailureCode" ELSE 'HISTORICAL_TICKETING_FAILED' END,
                    'Geçmiş bilet kesim kontrol kaydı. EasyTicket kaydı doğrulanmalı; ödeme alındıysa tekrar tahsilat yapılmamalı.',
                    TRUE, "UpdatedAtUtc"
                FROM "TourTickets" WHERE "TicketingStatus" = 'ReviewRequired';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentEmails");

            migrationBuilder.DropTable(
                name: "TicketErrorRecords");

            migrationBuilder.DropColumn(
                name: "CustomerLanguage",
                table: "TourTickets");

            migrationBuilder.DropColumn(
                name: "DeparturePortName",
                table: "TourTickets");
        }
    }
}
