using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PeremeTours.Infrastructure.Payments;

public static class ZiraatPosHash
{
    private static readonly HashSet<string> ExcludedFields = new(
        ["hash", "encoding", "countdown"],
        StringComparer.OrdinalIgnoreCase
    );

    public static string Create(
        IEnumerable<KeyValuePair<string, string>> fields,
        string storeKey
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);

        var values = fields
            .Where(field => !ExcludedFields.Contains(field.Key))
            .OrderBy(
                field => field.Key,
                StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), true)
            )
            .Select(field => Escape(field.Value ?? string.Empty))
            .Append(Escape(storeKey));

        var payload = string.Join('|', values);
        return Convert.ToBase64String(
            SHA512.HashData(Encoding.UTF8.GetBytes(payload))
        );
    }

    public static bool Verify(
        IReadOnlyDictionary<string, string> fields,
        string storeKey
    )
    {
        if (!TryGet(fields, "HASH", out var receivedHash))
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(Create(fields, storeKey));
            var received = Convert.FromBase64String(receivedHash);
            return expected.Length == received.Length
                && CryptographicOperations.FixedTimeEquals(expected, received);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryGet(
        IReadOnlyDictionary<string, string> fields,
        string name,
        out string value
    )
    {
        foreach (var field in fields)
        {
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal);
}
