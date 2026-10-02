using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Email;

namespace PeremeTours.Infrastructure.Tickets;

internal sealed class TicketErrorService(PeremeToursDbContext db, IOptions<MailOptions>? mailOptions = null) : ITicketErrorService
{
    public async Task<TicketErrorPage> ListAsync(string? search, TicketErrorStage? stage, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.TicketErrorRecords.AsNoTracking();
        if (stage.HasValue) query = query.Where(item => item.Stage == stage.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var upper = search.Trim().ToUpperInvariant();
            var lower = search.Trim().ToLowerInvariant();
            // SQL LOWER uses database collation; culture/StringComparison overloads cannot be translated by EF.
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(item => item.Ticket.TicketCode.Contains(upper)
                || item.Code.Contains(upper) || (item.ProviderCode != null && item.ProviderCode.Contains(upper))
                || item.Ticket.TourName.ToLower().Contains(lower));
#pragma warning restore CA1304, CA1311, CA1862
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new TicketErrorSummary(item.Id, item.TicketId, item.Ticket.TicketCode,
                item.Ticket.TourName, item.Ticket.TourDate, item.Ticket.Amount, item.Ticket.Currency,
                item.Stage, item.Code, item.ProviderCode, item.Message, item.IsHistorical, item.CreatedAtUtc,
                item.Ticket.PaymentStatus, item.Ticket.TicketingStatus,
                item.Ticket.PaymentEmail == null ? null : (PaymentEmailStatus?)item.Ticket.PaymentEmail.Status))
            .ToListAsync(cancellationToken);
        return new TicketErrorPage(items, total, page, pageSize, mailOptions?.Value.Enabled ?? false);
    }
}
