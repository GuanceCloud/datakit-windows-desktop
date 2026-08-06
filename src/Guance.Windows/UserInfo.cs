namespace Guance.Windows;

public sealed record UserInfo(string Id, string? Name, string? Email, IReadOnlyDictionary<string, object?> Extra);
