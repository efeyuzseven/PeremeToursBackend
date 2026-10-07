using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Tickets;

internal sealed record BankCancellationCheck(string TransactionId, string Operation, bool AlreadyReversed);
internal interface IBankCancellationGateway
{
    Task<BankCancellationCheck> CheckAsync(TourTicket ticket, CancellationToken cancellationToken);
    Task<string> ReverseAsync(TourTicket ticket, BankCancellationCheck check, CancellationToken cancellationToken);
}
internal interface IEasyTicketCancellationGateway
{
    Task CancelAsync(TourTicket ticket, CancellationToken cancellationToken);
}
internal sealed class CancellationGatewayException(string code) : Exception("İptal sonucu kontrol edilmeli.")
{
    public string Code { get; } = code;
}
