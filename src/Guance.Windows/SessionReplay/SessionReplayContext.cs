namespace Guance.Windows.SessionReplay;

internal sealed record SessionReplayContext(
    string AppId,
    string SessionId,
    string? ViewId,
    string Service,
    string Env,
    string Version,
    string SdkName,
    string SdkVersion);
