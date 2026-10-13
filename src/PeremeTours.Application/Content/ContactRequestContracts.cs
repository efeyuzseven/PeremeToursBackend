using System.ComponentModel.DataAnnotations;

namespace PeremeTours.Application.Content;

public sealed record ContactRequestSubmission(
    Guid RequestId,
    [Required, StringLength(100)] string Name,
    [Required, EmailAddress, StringLength(200)] string Email,
    [Required, StringLength(200)] string Topic,
    [Required, StringLength(3000)] string Message,
    [Required, RegularExpression("^(tr|en)$")] string Language,
    [StringLength(200)] string? Website = null
);

public sealed record ContactRequestReceipt(Guid RequestId, string Status = "Received");
public sealed class ContactRequestConflictException : Exception;
public sealed class ContactMailUnavailableException : Exception;

public interface IContactRequestService
{
    Task<ContactRequestReceipt> SubmitAsync(ContactRequestSubmission request, CancellationToken cancellationToken);
}
