using PeremeTours.Domain.Tickets;

namespace PeremeTours.Application.Tickets;

public sealed record TicketErrorSummary(Guid Id, Guid TicketId, string TicketCode,
    string TourName, DateOnly TourDate, decimal Amount, string Currency,
    TicketErrorStage Stage, string Code, string? ProviderCode, string Message,
    bool IsHistorical, DateTime CreatedAtUtc, TicketPaymentStatus PaymentStatus,
    TicketingStatus TicketingStatus, PaymentEmailStatus? EmailStatus);

public sealed record TicketErrorPage(IReadOnlyList<TicketErrorSummary> Items, int TotalCount, int Page, int PageSize, bool EmailSendingEnabled = false);

public interface ITicketErrorService
{
    Task<TicketErrorPage> ListAsync(string? search, TicketErrorStage? stage, int page, int pageSize, CancellationToken cancellationToken);
}
