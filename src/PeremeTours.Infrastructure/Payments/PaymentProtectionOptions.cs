namespace PeremeTours.Infrastructure.Payments;

public sealed class PaymentProtectionOptions
{
    public const string SectionName = "Payments:Protection";

    // Stays off until a reCAPTCHA key pair for pereme.com.tr is provisioned; the frontend only renders
    // the widget when VITE_RECAPTCHA_SITE_KEY is set, so both sides must be enabled together.
    public bool RecaptchaEnabled { get; init; }

    public string RecaptchaSecretKey { get; init; } = string.Empty;

    public int AttemptsPerIdentity { get; init; } = 3;

    public int IdentityWindowMinutes { get; init; } = 30;

    // Site-wide circuit breaker. Normal traffic is a handful of payments per day.
    public int PaymentsPerHour { get; init; } = 40;
}
