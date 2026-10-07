namespace PeremeTours.Application.Tickets;

public sealed record CancelTicketCommand(string TicketCode, decimal ExpectedAmount, string Reason);
public sealed record TicketCancellationSummary(Guid TicketId, string Status, decimal Amount, string? BankOperation,
    string? FailureCode, DateTime RequestedAtUtc, DateTime? CompletedAtUtc, bool ProviderCancelled);

public interface ITicketCancellationService
{
    Task<TicketCancellationSummary?> CancelAsync(Guid ticketId, Guid actorUserId,
        CancelTicketCommand command, CancellationToken cancellationToken);
    Task<TicketCancellationSummary?> GetAsync(Guid ticketId, CancellationToken cancellationToken);
}

public sealed class TicketCancellationValidationException(string message) : Exception(message);
