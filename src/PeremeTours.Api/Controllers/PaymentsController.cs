using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;
using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/payments")]
public sealed class PaymentsController(
    ITourPaymentService paymentService,
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

    [HttpPost("tour/initialize")]
    [EnableRateLimiting("payment-start")]
    [ProducesResponseType<StartTourPaymentResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<StartTourPaymentResult>> Initialize(
        StartTourPaymentRequest request,
        CancellationToken cancellationToken
    )
    {
        using var logScope = CreatePaymentLogScope();
        try
        {
            Guid? userId = Guid.TryParse(
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                out var parsedUserId
            ) ? parsedUserId : null;
            return Ok(await paymentService.StartAsync(
                new StartTourPaymentCommand(
                    request.ExternalTourId,
                    request.ExternalDeparturePortId,
                    request.ExternalDepartureId,
                    request.ExternalTripId,
                    request.ExternalPriceId,
                    request.TourDate,
                    request.GuestCount,
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
            ));
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
        catch (PaymentGatewayException exception)
        {
            LogInitializeFailed(logger, exception);
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Bankaya ulaşılamıyor",
                detail: "Ödeme işlemi şu anda başlatılamıyor. Lütfen kısa süre sonra tekrar deneyin."
            );
        }
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
            LogCallbackFailed(logger, exception);
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

public sealed class StartTourPaymentRequest
{
    [Range(1, int.MaxValue)]
    public int ExternalTourId { get; init; }

    [Range(1, int.MaxValue)]
    public int ExternalDeparturePortId { get; init; }

    [Range(1, int.MaxValue)]
    public int ExternalDepartureId { get; init; }

    [Range(1, int.MaxValue)]
    public int ExternalTripId { get; init; }

    [Range(1, int.MaxValue)]
    public int ExternalPriceId { get; init; }

    public DateOnly TourDate { get; init; }

    [Range(1, 12)]
    public int GuestCount { get; init; }

    [Required, StringLength(160, MinimumLength = 2)]
    public required string CustomerName { get; init; }

    [Required, EmailAddress, StringLength(320)]
    public required string CustomerEmail { get; init; }

    [Phone, StringLength(32)]
    public string? CustomerPhone { get; init; }

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
