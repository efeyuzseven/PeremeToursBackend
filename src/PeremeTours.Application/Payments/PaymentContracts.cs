namespace PeremeTours.Application.Payments;

public sealed record PaymentCard(
    string HolderName,
    string Number,
    string SecurityCode,
    int ExpiryMonth,
    int ExpiryYear
);

public sealed record StartTourPaymentCommand(
    int ExternalTourId,
    int ExternalDeparturePortId,
    int ExternalDepartureId,
    int ExternalTripId,
    int ExternalPriceId,
    DateOnly TourDate,
    int GuestCount,
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

public interface ITourPaymentService
{
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

public sealed class PaymentGatewayException(
    string message,
    Exception? innerException = null
) : Exception(message, innerException);
