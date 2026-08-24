using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CloudLight.Presence.RouterCloud.Probe;

internal enum ProbeErrorCategory
{
    AuthenticationExpired,
    NetworkUnavailable,
    CloudUnavailable,
    RateLimited,
    InvalidResponse,
    RouterOffline,
    Unknown
}

internal sealed class ProbeException(
    ProbeErrorCategory category,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public ProbeErrorCategory Category { get; } = category;
}

internal static class ProbeErrorClassifier
{
    public static ProbeException Classify(Exception exception)
    {
        if (exception is ProbeException probeException)
        {
            return probeException;
        }

        if (exception is OperationCanceledException or TimeoutException)
        {
            return new ProbeException(
                ProbeErrorCategory.NetworkUnavailable,
                "The network request timed out.", exception);
        }

        if (exception is HttpRequestException httpException)
        {
            var category = httpException.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    ProbeErrorCategory.AuthenticationExpired,
                HttpStatusCode.TooManyRequests => ProbeErrorCategory.RateLimited,
                _ when (int?)httpException.StatusCode >= 500 => ProbeErrorCategory.CloudUnavailable,
                _ when HasSocketFailure(httpException) => ProbeErrorCategory.NetworkUnavailable,
                _ => ProbeErrorCategory.Unknown
            };
            return new ProbeException(category, httpException.Message, httpException);
        }

        if (exception is JsonException or FormatException or InvalidDataException)
        {
            return new ProbeException(
                ProbeErrorCategory.InvalidResponse,
                "Xiaomi returned an invalid or unsupported response.", exception);
        }

        return new ProbeException(ProbeErrorCategory.Unknown, exception.Message, exception);
    }

    private static bool HasSocketFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException)
            {
                return true;
            }
        }
        return false;
    }
}
