using System.Data;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Tickets;

internal sealed class TicketCancellationService(PeremeToursDbContext db, IBankCancellationGateway bank,
    IEasyTicketCancellationGateway provider, TimeProvider clock) : ITicketCancellationService
{
    public async Task<TicketCancellationSummary?> GetAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var record = await db.TicketCancellations.AsNoTracking().SingleOrDefaultAsync(item => item.TicketId == ticketId, cancellationToken);
        return record is null ? null : Map(record);
    }

    public async Task<TicketCancellationSummary?> CancelAsync(Guid ticketId, Guid actorUserId,
        CancelTicketCommand command, CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 300)
            throw new TicketCancellationValidationException("İptal nedeni 1–300 karakter olmalıdır.");
        TourTicket? ticket;
        TicketCancellation record;
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            ticket = await db.TourTickets.Include(item => item.Passengers).Include(item => item.Cancellation)
                .SingleOrDefaultAsync(item => item.Id == ticketId, cancellationToken);
            if (ticket is null) return null;
            if (command.TicketCode != ticket.TicketCode || command.ExpectedAmount != ticket.Amount)
                throw new TicketCancellationValidationException("Bilet numarası veya tutar değişti. Listeyi yenileyip tekrar kontrol edin.");
            if (ticket.Cancellation is not null) return Map(ticket.Cancellation); // Never replay provider or bank mutations.
            if (ticket.PaymentProvider != "Ziraat" || ticket.Currency != "TRY" || ticket.Amount <= 0
                || ticket.Status != TicketStatus.Confirmed || ticket.PaymentStatus != TicketPaymentStatus.Paid
                || ticket.TicketingStatus != TicketingStatus.Issued || !Guid.TryParse(ticket.ExternalVoucherGuid, out _)
                || ticket.Passengers.Count != ticket.GuestCount || ticket.Passengers.Count == 0
                || ticket.Passengers.Any(item => !Guid.TryParse(item.ExternalTicketGuid, out _) || string.IsNullOrWhiteSpace(item.Pnr)))
                throw new TicketCancellationValidationException("Yalnızca ödemesi ve EasyTicket bileti doğrulanmış, kullanılmamış rezervasyonlar iptal edilebilir.");
            var now = clock.GetUtcNow();
            // Serialize with the Used action: claiming a cancellation and marking a ticket Used cannot both win.
            var locked = await db.TourTickets.Where(item => item.Id == ticketId && item.Status == TicketStatus.Confirmed
                    && item.PaymentStatus == TicketPaymentStatus.Paid && item.TicketingStatus == TicketingStatus.Issued
                    && !db.TicketCancellations.Any(cancel => cancel.TicketId == item.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, now), cancellationToken);
            if (locked != 1) throw new TicketCancellationValidationException("Bilet başka bir işlem tarafından değiştirildi. Listeyi yenileyin.");
            record = new TicketCancellation { TicketId = ticketId, ActorUserId = actorUserId, Reason = command.Reason.Trim(),
                Amount = ticket.Amount, Status = TicketCancellationStatus.Processing, RequestedAtUtc = now.UtcDateTime, UpdatedAtUtc = now.UtcDateTime };
            db.TicketCancellations.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Once claimed, a browser disconnect does not interrupt a financial operation halfway through.
        // A process crash still leaves a durable, non-replayable claim that becomes ReviewRequired.
        using var operationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var check = await bank.CheckAsync(ticket, operationTimeout.Token);
            record.BankOperation = check.Operation;
            record.BankTransactionId = check.TransactionId;
            await SaveStageAsync(record, TicketCancellationStatus.Processing);
            await provider.CancelAsync(ticket, operationTimeout.Token);
            record.ProviderCancelledAtUtc = clock.GetUtcNow().UtcDateTime;
            ticket.Status = TicketStatus.Cancelled;
            await SaveStageAsync(record, TicketCancellationStatus.ProviderCancelled);
            if (!check.AlreadyReversed)
            {
                // Persist BEFORE the reversal request; a timeout or crash must never cause a second refund.
                await SaveStageAsync(record, TicketCancellationStatus.BankReversalStarted);
                record.BankReversalTransactionId = await bank.ReverseAsync(ticket, check, operationTimeout.Token);
            }
            ticket.PaymentStatus = TicketPaymentStatus.Refunded;
            ticket.PaymentFailureCode = null;
            ticket.PaymentFailureMessage = null;
            record.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
            await SaveStageAsync(record, TicketCancellationStatus.Completed);
        }
        catch (Exception exception)
        {
            record.FailureCode = exception is CancellationGatewayException known
                ? PaymentDiagnostics.SafeCode(known.Code, "CANCELLATION_RESULT_UNKNOWN") : "CANCELLATION_RESULT_UNKNOWN";
            PaymentDiagnostics.Add(db, ticketId, TicketErrorStage.Cancellation, record.FailureCode,
                "İptal/iade sonucu kesinleşmedi. Yeni iptal veya iade başlatmayın; sipariş koduyla banka ve EasyTicket kaydını kontrol edin.", clock.GetUtcNow());
            await SaveStageAsync(record, TicketCancellationStatus.ReviewRequired);
        }
        return Map(record);
    }

    private async Task SaveStageAsync(TicketCancellation record, TicketCancellationStatus status)
    {
        record.Status = status;
        record.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        if (record.Ticket is not null) record.Ticket.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private TicketCancellationSummary Map(TicketCancellation record)
    {
        var status = record.DisplayStatus(clock.GetUtcNow().UtcDateTime);
        return new(record.TicketId, status.ToString(), record.Amount, record.BankOperation,
            status != record.Status ? "CANCELLATION_RESULT_UNKNOWN" : record.FailureCode,
            record.RequestedAtUtc, record.CompletedAtUtc, record.ProviderCancelledAtUtc.HasValue);
    }
}
