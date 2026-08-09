namespace Guance.Windows;

/// <summary>Configures collection and redaction of URLs and HTTP headers.</summary>
public sealed class RumPrivacyConfig
{
    /// <summary>Enables capture of request and response headers after redaction.</summary>
    public bool CaptureHttpHeaders { get; init; } = true;
    /// <summary>Preserves URL query strings after applying query-value redaction.</summary>
    public bool CaptureUrlQueryString { get; init; } = true;
    /// <summary>Redacts every URL query value instead of only known sensitive names.</summary>
    public bool RedactAllUrlQueryValues { get; init; }
    /// <summary>Gets the replacement text used for redacted values.</summary>
    public string RedactedValue { get; init; } = "<redacted>";

    /// <summary>Gets case-insensitive HTTP header names whose values are redacted.</summary>
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

    /// <summary>Gets case-insensitive URL query parameter names whose values are redacted.</summary>
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
