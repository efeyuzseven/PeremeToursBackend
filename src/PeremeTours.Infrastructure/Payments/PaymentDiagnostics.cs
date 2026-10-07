using System.Text.RegularExpressions;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Payments;

internal static partial class PaymentDiagnostics
{
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "UNKNOWN_ERROR", "BANK_DECLINED", "GATEWAY_START_FAILED", "CALLBACK_VALIDATION_FAILED", "BANK_RESULT_UNKNOWN",
        "PROVIDER_RESULT_UNKNOWN", "PROVIDER_RESULT_INCOMPLETE", "HISTORICAL_PAYMENT_FAILED", "HISTORICAL_TICKETING_FAILED",
        "SMTP_CONFIG_MISSING", "EMAIL_ADDRESS_INVALID", "SMTP_TLS_FAILED", "SMTP_AUTH_FAILED", "SMTP_CONNECT_FAILED",
        "SMTP_SEND_REJECTED", "SMTP_RESULT_UNKNOWN",
        "CANCELLATION_RESULT_UNKNOWN", "BANK_CANCELLATION_CHECK_FAILED", "BANK_REVERSAL_REJECTED", "BANK_REVERSAL_UNKNOWN",
        "PROVIDER_CANCELLATION_CHECK_FAILED", "PROVIDER_CANCELLATION_REJECTED", "PROVIDER_CANCELLATION_UNKNOWN",
    };
    // Never persist arbitrary provider messages: they can contain PAN, tokens, credentials or passenger data.
    public static string SafeCode(string? value, string fallback) => value is not null && (KnownCodes.Contains(value) || CodePattern().IsMatch(value))
        ? value : fallback;

    public static string? SafeProviderCode(string? value) => value is not null && ProviderCodePattern().IsMatch(value)
        ? value : null;

    public static string BankMessage(ZiraatFinalizationResult result) =>
        result.ErrorDetailCode == "CORE-2201" || string.Equals(result.ErrorMessage?.Trim(),
            "User is not authenticated to perform this process.", StringComparison.OrdinalIgnoreCase)
            ? "Sanal POS API kullanıcısı doğrulanamadı. Banka panelinde kullanıcı tanımı, onayı ve giriş bilgileri kontrol edilmeli."
            : "Banka ödeme isteğini onaylamadı. Ayrıntılı red nedeni sipariş koduyla banka panelinden kontrol edilmeli.";

    public static void Add(PeremeToursDbContext db, Guid ticketId, TicketErrorStage stage,
        string code, string message, DateTimeOffset now, string? providerCode = null)
    {
        db.TicketErrorRecords.Add(new TicketErrorRecord
        {
            Id = Guid.NewGuid(), TicketId = ticketId, Stage = stage,
            Code = SafeCode(code, "UNKNOWN_ERROR"), ProviderCode = SafeProviderCode(providerCode),
            Message = message, CreatedAtUtc = now.UtcDateTime,
        });
    }

    [GeneratedRegex(@"\A[0-9]{2,4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\A(?:CORE|HOST|BM|MPI)-[0-9]{2,6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderCodePattern();
}
