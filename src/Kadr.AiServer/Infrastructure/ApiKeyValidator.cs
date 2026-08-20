using System.Security.Cryptography;
using System.Text;

namespace KadrStudio.AiServer.Infrastructure;

public static class ApiKeyValidator
{
    public static bool IsValidBearerHeader(string? authorizationHeader, string expectedApiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedApiKey);

        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var supplied = authorizationHeader["Bearer ".Length..].Trim();
        if (supplied.Length == 0)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expectedApiKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
