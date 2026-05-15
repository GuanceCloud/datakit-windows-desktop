namespace Guance.Rum.Windows;

public sealed class RumPrivacyConfig
{
    public bool CaptureHttpHeaders { get; init; } = true;
    public bool CaptureUrlQueryString { get; init; } = true;
    public bool RedactAllUrlQueryValues { get; init; }
    public string RedactedValue { get; init; } = "<redacted>";

    public IReadOnlyCollection<string> RedactedHeaderNames { get; init; } = new[]
    {
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "Proxy-Authorization",
        "X-Api-Key",
        "X-Auth-Token",
        "X-Datakit-Token"
    };

    public IReadOnlyCollection<string> RedactedQueryParameterNames { get; init; } = new[]
    {
        "token",
        "access_token",
        "refresh_token",
        "client_secret",
        "password",
        "secret",
        "api_key",
        "apikey",
        "auth",
        "authorization"
    };
}
