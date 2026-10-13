using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Content;

internal sealed class ContactRequestService(PeremeToursDbContext db, IOptions<MailOptions> options, TimeProvider clock) : IContactRequestService
{
    public async Task<ContactRequestReceipt> SubmitAsync(ContactRequestSubmission request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim(); var email = request.Email.Trim().ToLowerInvariant();
        var topic = request.Topic.Trim(); var message = request.Message.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { name, email, topic, message, request.Language }))));
        var existing = await db.ContactRequests.AsNoTracking().Where(item => item.Id == request.RequestId)
            .Select(item => item.PayloadHash).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return Receipt(existing, hash, request.RequestId);
        var recipients = (options.Value.ContactRecipients ?? []).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (!options.Value.Enabled || recipients.Length != 2 || recipients.Any(item => !MimeKit.MailboxAddress.TryParse(item, out _)))
            throw new ContactMailUnavailableException();
        var now = clock.GetUtcNow().UtcDateTime;
        var contact = new ContactRequest { Id = request.RequestId, Name = name, Email = email, Topic = topic,
            Message = message, Language = request.Language, PayloadHash = hash, CreatedAtUtc = now };
        contact.Emails.AddRange(recipients.Select(recipient => new ContactEmail { Id = Guid.NewGuid(), RequestId = contact.Id,
            RecipientEmail = recipient, Status = ContactEmailStatus.Queued, NextAttemptAtUtc = now }));
        db.ContactRequests.Add(contact);
        // One atomic commit persists the request and both independent delivery jobs.
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            var previous = await db.ContactRequests.AsNoTracking().Where(item => item.Id == request.RequestId)
                .Select(item => item.PayloadHash).SingleOrDefaultAsync(cancellationToken);
            if (previous is null) throw;
            return Receipt(previous, hash, request.RequestId);
        }
        return new ContactRequestReceipt(contact.Id);
    }

    private static ContactRequestReceipt Receipt(string stored, string submitted, Guid id)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(submitted)))
            throw new ContactRequestConflictException();
        return new ContactRequestReceipt(id);
    }
}
