using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Tickets;

internal sealed class TicketService(PeremeToursDbContext dbContext)
    : ITicketService
{
    public async Task<IReadOnlyList<TicketSummary>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        var tickets = await dbContext.TourTickets
            .AsNoTracking()
            .OrderByDescending(ticket => ticket.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return tickets.Select(Map).ToList();
    }

    public async Task<TicketSummary> CreateAsync(
        Guid actorUserId,
        CreateTicketCommand command,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var ticket = new TourTicket
        {
            Id = Guid.NewGuid(),
            TicketCode = CreateTicketCode(now),
            TourName = command.TourName.Trim(),
            TourDate = command.TourDate,
            DepartureTime = command.DepartureTime,
            CustomerName = command.CustomerName.Trim(),
            CustomerEmail = command.CustomerEmail.Trim().ToLowerInvariant(),
            GuestCount = command.GuestCount,
            Amount = command.Amount,
            Currency = "TRY",
            Status = command.Status,
            Channel = command.Channel,
            PaymentStatus = TicketPaymentStatus.NotRequired,
            UserId = actorUserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        dbContext.TourTickets.Add(ticket);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(ticket);
    }

    public async Task<TicketSummary?> UpdateAsync(
        Guid ticketId,
        UpdateTicketCommand command,
        CancellationToken cancellationToken
    )
    {
        var ticket = await dbContext.TourTickets.SingleOrDefaultAsync(
            candidate => candidate.Id == ticketId,
            cancellationToken
        );
        if (ticket is null)
        {
            return null;
        }

        if (command.TourName is not null)
        {
            ticket.TourName = command.TourName.Trim();
        }
        if (command.TourDate.HasValue)
        {
            ticket.TourDate = command.TourDate.Value;
        }
        if (command.DepartureTime.HasValue)
        {
            ticket.DepartureTime = command.DepartureTime.Value;
        }
        if (command.CustomerName is not null)
        {
            ticket.CustomerName = command.CustomerName.Trim();
        }
        if (command.CustomerEmail is not null)
        {
            ticket.CustomerEmail = command.CustomerEmail.Trim().ToLowerInvariant();
        }
        if (command.GuestCount.HasValue)
        {
            ticket.GuestCount = command.GuestCount.Value;
        }
        if (command.Amount.HasValue)
        {
            ticket.Amount = command.Amount.Value;
        }
        if (command.Status.HasValue)
        {
            ticket.Status = command.Status.Value;
        }
        ticket.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(ticket);
    }

    private static TicketSummary Map(TourTicket ticket) =>
        new(
            ticket.Id,
            ticket.TicketCode,
            ticket.TourName,
            ticket.TourDate,
            ticket.DepartureTime,
            ticket.CustomerName,
            ticket.CustomerEmail,
            ticket.GuestCount,
            ticket.Amount,
            ticket.Currency,
            ticket.Status,
            ticket.Channel,
            ticket.PaymentStatus,
            ticket.CreatedAtUtc,
            ticket.UpdatedAtUtc
        );

    private static string CreateTicketCode(DateTimeOffset now) =>
        $"PRM-{now:yyyyMMdd}-{RandomNumberGenerator.GetInt32(100000, 999999)}";
}
