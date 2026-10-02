using PeremeTours.Application.Tours;

namespace PeremeTours.Application.Payments;

public sealed record PaymentCard(
    string HolderName,
    string Number,
    string SecurityCode,
    int ExpiryMonth,
    int ExpiryYear
)
{
    public override string ToString() => "PaymentCard [REDACTED]";
}

public sealed record PaymentPassenger(
    int ExternalPriceId, string FirstName, string LastName, string Gender,
    string Nationality, string IdentityNumber, DateOnly BirthDate
)
{
    public override string ToString() => "PaymentPassenger [REDACTED]";
}

public sealed record StartTourPaymentCommand(
    int ExternalTourId,
    int ExternalDeparturePortId,
    int ExternalDepartureId,
    DateOnly TourDate,
    IReadOnlyList<TourTicketSelection> Tickets,
    IReadOnlyList<PaymentPassenger> Passengers,
    decimal ExpectedAmount,
    Guid AttemptId,
    bool PrivacyNoticeAccepted,
    string CustomerName,
    string CustomerEmail,
    string? CustomerPhone,
    string Language,
    PaymentCard Card,
    Guid? UserId
);

public sealed record StartTourPaymentResult(
    Guid TicketId,
    string TicketCode,
    decimal Amount,
    string Currency,
    string ThreeDSecureHtml
);

public sealed record CompleteTourPaymentResult(
    bool IsSuccessful,
    string? TicketCode,
    string Message
);

public sealed record PaymentAvailability(bool Enabled, string Provider);
public sealed record IssuedTourTicket(string? Pnr, string? TicketGuid);
public sealed record TourPaymentStatus(
    string TicketCode, decimal Amount, string Currency, string PaymentStatus,
    string TicketingStatus, IReadOnlyList<IssuedTourTicket> Tickets
);

public interface ITourPaymentService
{
    PaymentAvailability GetAvailability();
    Task<TourPaymentStatus?> GetStatusAsync(Guid attemptId, CancellationToken cancellationToken);

    Task<StartTourPaymentResult> StartAsync(
        StartTourPaymentCommand command,
        CancellationToken cancellationToken
    );

    Task<CompleteTourPaymentResult> CompleteAsync(
        IReadOnlyDictionary<string, string> formFields,
        CancellationToken cancellationToken
    );
}

public sealed class PaymentConfigurationException(string message)
    : Exception(message);

public sealed class PaymentValidationException(string message)
    : Exception(message);

public sealed class PaymentConflictException(string message) : Exception(message);

public sealed class PaymentGatewayException(
    string message,
    Exception? innerException = null
) : Exception(message, innerException);
