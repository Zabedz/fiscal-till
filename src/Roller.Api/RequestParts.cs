using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Amazon.Lambda.APIGatewayEvents;

namespace Roller.Api;

internal static partial class RequestParts
{
    public static string? Header(APIGatewayProxyRequest request, string name) =>
        request.Headers?.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public static bool IsValidIdempotencyKey([NotNullWhen(true)] string? key) => key is not null && IdempotencyKeyPattern().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex IdempotencyKeyPattern();
}
