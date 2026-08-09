namespace Guance.Windows;

/// <summary>Represents user identity and custom attributes attached to subsequent telemetry.</summary>
/// <param name="Id">Required user identifier.</param>
/// <param name="Name">Optional display name.</param>
/// <param name="Email">Optional email address.</param>
/// <param name="Extra">Additional user attributes.</param>
public sealed record UserInfo(string Id, string? Name, string? Email, IReadOnlyDictionary<string, object?> Extra);
