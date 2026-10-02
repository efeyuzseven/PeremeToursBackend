using System.Net.Mail;
using PeremeTours.Application.Payments;

namespace PeremeTours.Infrastructure.Payments;

internal static class PaymentValidation
{
    public static void Validate(StartTourPaymentCommand command, DateOnly today)
    {
        if (command.AttemptId == Guid.Empty || !command.PrivacyNoticeAccepted
            || command.Passengers is null || command.Passengers.Count is < 1 or > 12
            || command.Passengers.Any(item => item is null)
            || command.ExpectedAmount <= 0 || command.Language is not ("tr" or "en"))
        {
            throw new PaymentValidationException("Rezervasyon bilgilerini ve KVKK onayını kontrol edin.");
        }
        if (!ValidName(command.CustomerName, 160) || command.CustomerEmail.Length > 320
            || !MailAddress.TryCreate(command.CustomerEmail, out var address)
            || !string.Equals(address.Address, command.CustomerEmail, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(command.CustomerPhone) || command.CustomerPhone.Length > 32
            || command.CustomerPhone.Count(char.IsAsciiDigit) is < 7 or > 16)
        {
            throw new PaymentValidationException("İletişim bilgilerini kontrol edin.");
        }
        foreach (var passenger in command.Passengers)
        {
            var identity = passenger.IdentityNumber.Trim();
            if (!ValidName(passenger.FirstName, 80) || !ValidName(passenger.LastName, 80)
                || identity.Any(char.IsControl)
                || passenger.Gender is not ("male" or "female")
                || passenger.Nationality is not ("TR" or "foreign")
                || passenger.BirthDate < new DateOnly(1900, 1, 1) || passenger.BirthDate > today
                || (passenger.Nationality == "TR"
                    ? identity.Length != 11 || identity[0] == '0' || !identity.All(char.IsAsciiDigit)
                    : identity.Length is < 3 or > 30))
            {
                throw new PaymentValidationException("Yolcu bilgilerini kontrol edin.");
            }
        }
        var card = command.Card;
        var number = new string(card.Number.Where(char.IsAsciiDigit).ToArray());
        if (!ValidName(card.HolderName, 160) || card.Number.Length > 23
            || card.Number.Any(character => !char.IsAsciiDigit(character) && character is not (' ' or '-'))
            || number.Length is < 13 or > 19 || !PassesLuhn(number)
            || card.SecurityCode.Length is < 3 or > 4 || !card.SecurityCode.All(char.IsAsciiDigit)
            || card.ExpiryMonth is < 1 or > 12 || card.ExpiryYear < today.Year || card.ExpiryYear > today.Year + 20
            || (card.ExpiryYear == today.Year && card.ExpiryMonth < today.Month))
        {
            throw new PaymentValidationException("Kart bilgileri geçersiz.");
        }
    }

    private static bool ValidName(string value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Trim().Length >= 2 && value.Trim().Length <= max && !value.Any(char.IsControl);

    private static bool PassesLuhn(string number)
    {
        var sum = 0;
        var doubleDigit = false;
        for (var index = number.Length - 1; index >= 0; index--)
        {
            var digit = number[index] - '0';
            if (doubleDigit) { digit *= 2; if (digit > 9) digit -= 9; }
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }
}
