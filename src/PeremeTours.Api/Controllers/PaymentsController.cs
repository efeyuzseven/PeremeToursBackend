using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;
using PeremeTours.Application.Tours;
using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/payments")]
public sealed class PaymentsController(
    ITourPaymentService paymentService,
    ThreeDSecureFrameStore frameStore,
    IOptions<ZiraatPosOptions> options,
    ILogger<PaymentsController> logger
) : ControllerBase
{
    private static readonly Action<ILogger, Exception?> LogInitializeFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(4001, nameof(LogInitializeFailed)),
            "Payment gateway could not initialize a transaction."
        );
    private static readonly Action<ILogger, Exception?> LogCallbackFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(4002, nameof(LogCallbackFailed)),
            "Ziraat payment callback could not be completed."
        );
    private readonly ZiraatPosOptions _options = options.Value;

    [HttpGet("availability")]
    public ActionResult<PaymentAvailability> Availability()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(paymentService.GetAvailability());
    }

    [HttpGet("tour/status")]
    public async Task<ActionResult<TourPaymentStatus>> Status(
        [FromHeader(Name = "X-Payment-Token")] Guid attemptId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var status = await paymentService.GetStatusAsync(attemptId, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpPost("tour/initialize")]
    [RequestSizeLimit(32_768)]
    [EnableRateLimiting("payment-start")]
    [ProducesResponseType<StartTourPaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<StartTourPaymentResponse>> Initialize(
        StartTourPaymentRequest request,
        CancellationToken cancellationToken
    )
    {
        using var logScope = CreatePaymentLogScope();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (request.Tickets.Any(item => item is null) || request.Passengers.Any(item => item is null))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Eksik bilet veya yolcu bilgisi");
        try
        {
            Guid? userId = Guid.TryParse(
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                out var parsedUserId
            ) ? parsedUserId : null;
            var result = await paymentService.StartAsync(
                new StartTourPaymentCommand(
                    request.ExternalTourId,
                    request.ExternalDeparturePortId,
                    request.ExternalDepartureId,
                    request.TourDate,
                    request.Tickets.Select(item => new TourTicketSelection(item.ExternalPriceId, item.Quantity)).ToArray(),
                    request.Passengers.Select(item => new PaymentPassenger(item.ExternalPriceId,
                        item.FirstName, item.LastName, item.Gender, item.Nationality, item.IdentityNumber, item.BirthDate)).ToArray(),
                    request.ExpectedAmount,
                    request.AttemptId,
                    request.PrivacyNoticeAccepted,
                    request.CustomerName,
                    request.CustomerEmail,
                    request.CustomerPhone,
                    request.Language,
                    new PaymentCard(
                        request.Card.HolderName,
                        request.Card.Number,
                        request.Card.SecurityCode,
                        request.Card.ExpiryMonth,
                        request.Card.ExpiryYear
                    ),
                    userId
                ),
                cancellationToken
            );
            var frameToken = frameStore.Publish(result.ThreeDSecureHtml);
            return Ok(new StartTourPaymentResponse(result.TicketId, result.TicketCode, result.Amount, result.Currency,
                $"/api/v1/payments/tour/3d/{frameToken:D}"));
        }
        catch (PaymentConflictException exception)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Ödeme yeniden kontrol edilmeli", detail: exception.Message);
        }
        catch (TourCatalogUnavailableException)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Tur bilgileri alınamıyor",
                detail: "Güncel sefer ve fiyat doğrulanamadı. Ödeme başlatılmadı.");
        }
        catch (PaymentValidationException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ödeme bilgileri geçersiz",
                detail: exception.Message
            );
        }
        catch (PaymentConfigurationException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ödeme sistemi kullanılamıyor",
                detail: exception.Message
            );
        }
        catch (PaymentGatewayException)
        {
            LogInitializeFailed(logger, null);
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Bankaya ulaşılamıyor",
                detail: "Ödeme işlemi şu anda başlatılamıyor. Lütfen kısa süre sonra tekrar deneyin."
            );
        }
    }

    [HttpGet("tour/3d/{token:guid}")]
    [Produces("text/html")]
    public IActionResult BankFrame(Guid token)
    {
        var html = frameStore.Consume(token);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (Uri.TryCreate(_options.FrontendOrigin, UriKind.Absolute, out var origin))
            Response.Headers.ContentSecurityPolicy = $"frame-ancestors {origin.GetLeftPart(UriPartial.Authority)}";
        if (html is null)
        {
            Response.StatusCode = StatusCodes.Status410Gone;
            return Content("<!doctype html><html lang=\"tr\"><meta charset=\"utf-8\"><p>Banka ekranının süresi doldu. İşlem sonucunu rezervasyon ekranından kontrol edin; tekrar ödeme başlatmayın.</p></html>", "text/html; charset=utf-8");
        }
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpPost("ziraat/callback")]
    [Consumes("application/x-www-form-urlencoded")]
    [RequestSizeLimit(65_536)]
    [RequestFormLimits(ValueCountLimit = 100, KeyLengthLimit = 128, ValueLengthLimit = 4096)]
    [Produces("text/html")]
    public async Task<IActionResult> ZiraatCallback(CancellationToken cancellationToken)
    {
        using var logScope = CreatePaymentLogScope();
        CompleteTourPaymentResult result;
        try
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            if (form.Any(field => field.Value.Count != 1))
            {
                result = new CompleteTourPaymentResult(false, null, "Banka yanıtı geçersiz.");
            }
            else
            {
                var fields = form.ToDictionary(
                    field => field.Key,
                    field => field.Value[0] ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase
                );
                result = await paymentService.CompleteAsync(fields, cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is PaymentConfigurationException
                or PaymentValidationException
                or PaymentGatewayException
        )
        {
            LogCallbackFailed(logger, null);
            result = new CompleteTourPaymentResult(
                false,
                null,
                "Ödeme sonucu şu anda doğrulanamıyor. Lütfen destek ekibiyle iletişime geçin."
            );
        }

        return CallbackPage(result);
    }

    private ContentResult CallbackPage(CompleteTourPaymentResult result)
    {
        var frontendOrigin = Uri.TryCreate(
            _options.FrontendOrigin,
            UriKind.Absolute,
            out var configuredOrigin
        ) ? configuredOrigin.GetLeftPart(UriPartial.Authority) : "https://d2bmjk2h6qp4lz.cloudfront.net";
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var payload = JsonSerializer.Serialize(new
        {
            source = "PeremeToursPayment",
            status = result.IsSuccessful ? "success" : "error",
            ticketCode = result.TicketCode,
            message = result.Message,
        });
        var title = result.IsSuccessful ? "Ödeme başarılı" : "Ödeme tamamlanamadı";
        var html = $$"""
            <!doctype html>
            <html lang="tr">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>{{title}}</title>
              <style>
                body{margin:0;display:grid;min-height:100vh;place-items:center;background:#fff;color:#071b35;font:16px system-ui,sans-serif;text-align:center}
                main{padding:32px}h1{margin:0 0 12px;font-size:24px}p{margin:0;color:#1f478c}
              </style>
            </head>
            <body>
              <main><h1>{{title}}</h1><p>{{System.Net.WebUtility.HtmlEncode(result.Message)}}</p></main>
              <script nonce="{{nonce}}">window.parent.postMessage({{payload}},{{JsonSerializer.Serialize(frontendOrigin)}});</script>
            </body>
            </html>
            """;

        Response.Headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; frame-ancestors {frontendOrigin}";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(html, "text/html; charset=utf-8");
    }

    private IDisposable? CreatePaymentLogScope()
    {
        Response.Headers["X-Correlation-ID"] = HttpContext.TraceIdentifier;
        return logger.BeginScope(new Dictionary<string, object>
        {
            ["Application"] = "PeremeTours",
            ["PaymentProvider"] = "Ziraat",
            ["CorrelationId"] = HttpContext.TraceIdentifier,
        });
    }
}

public sealed record StartTourPaymentResponse(Guid TicketId, string TicketCode, decimal Amount, string Currency, string ThreeDSecureUrl);

public sealed class StartTourPaymentRequest
{
    [Range(1, int.MaxValue)]
    public int ExternalTourId { get; init; }

    [Range(1, int.MaxValue)]
    public int ExternalDeparturePortId { get; init; }

    [Range(1, int.MaxValue)]
    public int ExternalDepartureId { get; init; }

    public DateOnly TourDate { get; init; }

    [Required, MinLength(1), MaxLength(12)]
    public required List<PaymentTicketSelectionRequest> Tickets { get; init; }

    [Required, MinLength(1), MaxLength(12)]
    public required List<PaymentPassengerRequest> Passengers { get; init; }

    [Range(typeof(decimal), "0.01", "99999999", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal ExpectedAmount { get; init; }

    public Guid AttemptId { get; init; }

    public bool PrivacyNoticeAccepted { get; init; }

    [Required, StringLength(160, MinimumLength = 2)]
    public required string CustomerName { get; init; }

    [Required, EmailAddress, StringLength(320)]
    public required string CustomerEmail { get; init; }

    [Required, StringLength(32)]
    public required string CustomerPhone { get; init; }

    [RegularExpression("^(tr|en)$")]
    public string Language { get; init; } = "tr";

    [Required]
    public required PaymentCardRequest Card { get; init; }
}

public sealed class PaymentCardRequest
{
    [Required, StringLength(160, MinimumLength = 2)]
    public required string HolderName { get; init; }

    [Required, StringLength(23, MinimumLength = 13)]
    public required string Number { get; init; }

    [Required, StringLength(4, MinimumLength = 3)]
    public required string SecurityCode { get; init; }

    [Range(1, 12)]
    public int ExpiryMonth { get; init; }

    [Range(2026, 2100)]
    public int ExpiryYear { get; init; }
}

public sealed class PaymentTicketSelectionRequest
{
    [Range(1, int.MaxValue)] public int ExternalPriceId { get; init; }
    [Range(1, 12)] public int Quantity { get; init; }
}

public sealed class PaymentPassengerRequest
{
    [Range(1, int.MaxValue)] public int ExternalPriceId { get; init; }
    [Required, StringLength(80, MinimumLength = 2)] public required string FirstName { get; init; }
    [Required, StringLength(80, MinimumLength = 2)] public required string LastName { get; init; }
    [Required, RegularExpression("^(male|female)$")] public required string Gender { get; init; }
    [Required, RegularExpression("^(TR|foreign)$")] public required string Nationality { get; init; }
    [Required, StringLength(30, MinimumLength = 3)] public required string IdentityNumber { get; init; }
    public DateOnly BirthDate { get; init; }
}
