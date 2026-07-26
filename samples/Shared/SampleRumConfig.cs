using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Guance.Rum.Windows;

namespace Guance.Rum.Windows.Samples;

internal static class SampleRumConfig
{
    private const string LocalSettingsFileName = "rum.local.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RumConfig Load(string defaultRumAppId, string defaultServiceName, string[]? args = null)
    {
        var settings = new LocalRumSettings
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = defaultRumAppId,
            ServiceName = defaultServiceName,
            Env = "local",
            Version = "1.0.0",
            Debug = true,
            SessionReplayEnabled = true,
            ReplayAndroidCompatibilityMode = false,
            ReplayTextAndInputPrivacy = nameof(SessionReplayTextAndInputPrivacy.MaskSensitiveInputs),
            ReplayTouchPrivacy = nameof(SessionReplayTouchPrivacy.Show),
            ReplayImagePrivacy = nameof(SessionReplayImagePrivacy.MaskNone),
            DiagnosticConsoleEnabled = true,
            DiagnosticFirstChanceExceptions = false
        };

        ApplyJsonSettings(settings);
        ApplyEnvironment(settings);
        ApplyCommandLine(settings, args ?? Environment.GetCommandLineArgs().Skip(1).ToArray());
        ConfigureExceptionDiagnostics(settings);

        return new RumConfig
        {
            DatawayUrl = EmptyToNull(settings.DatawayUrl),
            DatakitUrl = EmptyToNull(settings.DatakitUrl),
            ClientToken = EmptyToNull(settings.ClientToken),
            RumAppId = settings.RumAppId ?? defaultRumAppId,
            ServiceName = settings.ServiceName ?? defaultServiceName,
            Env = settings.Env ?? "local",
            Version = settings.Version ?? "1.0.0",
            Debug = settings.Debug ?? true,
            DiagnosticListener = settings.DiagnosticConsoleEnabled == true ? LogDiagnostic : null,
            SessionReplay = new RumSessionReplayConfig
            {
                Enabled = settings.SessionReplayEnabled ?? true,
                AndroidCompatibilityMode = settings.ReplayAndroidCompatibilityMode ?? false,
                TextAndInputPrivacy = ParseEnum(settings.ReplayTextAndInputPrivacy, SessionReplayTextAndInputPrivacy.MaskSensitiveInputs),
                TouchPrivacy = ParseEnum(settings.ReplayTouchPrivacy, SessionReplayTouchPrivacy.Show),
                ImagePrivacy = ParseEnum(settings.ReplayImagePrivacy, SessionReplayImagePrivacy.MaskNone)
            }
        };
    }

    private static void ApplyJsonSettings(LocalRumSettings settings)
    {
        var file = FindLocalSettingsFile();
        if (file is null)
        {
            return;
        }

        var json = File.ReadAllText(file);
        var local = JsonSerializer.Deserialize<LocalRumSettings>(json, JsonOptions);
        if (local is not null)
        {
            Apply(settings, local);
        }
    }

    private static void ApplyEnvironment(LocalRumSettings settings)
    {
        Apply(settings, new LocalRumSettings
        {
            DatawayUrl = Environment.GetEnvironmentVariable("GUANCE_RUM_DATAWAY_URL"),
            DatakitUrl = Environment.GetEnvironmentVariable("GUANCE_RUM_DATAKIT_URL"),
            ClientToken = Environment.GetEnvironmentVariable("GUANCE_RUM_CLIENT_TOKEN"),
            RumAppId = Environment.GetEnvironmentVariable("GUANCE_RUM_APP_ID"),
            ServiceName = Environment.GetEnvironmentVariable("GUANCE_RUM_SERVICE_NAME"),
            Env = Environment.GetEnvironmentVariable("GUANCE_RUM_ENV"),
            Version = Environment.GetEnvironmentVariable("GUANCE_RUM_VERSION"),
            Debug = ParseBool(Environment.GetEnvironmentVariable("GUANCE_RUM_DEBUG")),
            SessionReplayEnabled = ParseBool(Environment.GetEnvironmentVariable("GUANCE_RUM_SESSION_REPLAY")),
            ReplayAndroidCompatibilityMode = ParseBool(Environment.GetEnvironmentVariable("GUANCE_RUM_REPLAY_ANDROID_COMPATIBILITY")),
            ReplayTextAndInputPrivacy = Environment.GetEnvironmentVariable("GUANCE_RUM_REPLAY_TEXT_AND_INPUT_PRIVACY"),
            ReplayTouchPrivacy = Environment.GetEnvironmentVariable("GUANCE_RUM_REPLAY_TOUCH_PRIVACY"),
            ReplayImagePrivacy = Environment.GetEnvironmentVariable("GUANCE_RUM_REPLAY_IMAGE_PRIVACY"),
            DiagnosticConsoleEnabled = ParseBool(Environment.GetEnvironmentVariable("GUANCE_RUM_DIAGNOSTIC_CONSOLE")),
            DiagnosticFirstChanceExceptions = ParseBool(Environment.GetEnvironmentVariable("GUANCE_RUM_FIRST_CHANCE_EXCEPTIONS"))
        });
    }

    private static void ApplyCommandLine(LocalRumSettings settings, string[] args)
    {
        var commandLine = new LocalRumSettings();
        foreach (var arg in args)
        {
            var (key, value) = SplitArgument(arg);
            switch (key)
            {
                case "--dataway-url":
                    commandLine.DatawayUrl = value;
                    break;
                case "--datakit-url":
                    commandLine.DatakitUrl = value;
                    break;
                case "--client-token":
                    commandLine.ClientToken = value;
                    break;
                case "--rum-app-id":
                    commandLine.RumAppId = value;
                    break;
                case "--service-name":
                    commandLine.ServiceName = value;
                    break;
                case "--env":
                    commandLine.Env = value;
                    break;
                case "--version":
                    commandLine.Version = value;
                    break;
                case "--debug":
                    commandLine.Debug = ParseBool(value);
                    break;
                case "--session-replay":
                    commandLine.SessionReplayEnabled = ParseBool(value);
                    break;
                case "--replay-text-and-input-privacy":
                    commandLine.ReplayTextAndInputPrivacy = value;
                    break;
                case "--replay-touch-privacy":
                    commandLine.ReplayTouchPrivacy = value;
                    break;
                case "--replay-image-privacy":
                    commandLine.ReplayImagePrivacy = value;
                    break;
                case "--diagnostic-console":
                    commandLine.DiagnosticConsoleEnabled = ParseBool(value);
                    break;
                case "--first-chance-exceptions":
                    commandLine.DiagnosticFirstChanceExceptions = ParseBool(value);
                    break;
            }
        }

        Apply(settings, commandLine);
    }

    private static void Apply(LocalRumSettings target, LocalRumSettings source)
    {
        if (!string.IsNullOrWhiteSpace(source.DatawayUrl))
        {
            target.DatawayUrl = source.DatawayUrl;
            target.DatakitUrl = null;
        }

        if (!string.IsNullOrWhiteSpace(source.DatakitUrl))
        {
            target.DatakitUrl = source.DatakitUrl;
            target.DatawayUrl = null;
            target.ClientToken = null;
        }

        target.ClientToken = Coalesce(source.ClientToken, target.ClientToken);
        target.RumAppId = Coalesce(source.RumAppId, target.RumAppId);
        target.ServiceName = Coalesce(source.ServiceName, target.ServiceName);
        target.Env = Coalesce(source.Env, target.Env);
        target.Version = Coalesce(source.Version, target.Version);
        target.Debug = source.Debug ?? target.Debug;
        target.SessionReplayEnabled = source.SessionReplayEnabled ?? target.SessionReplayEnabled;
        target.ReplayAndroidCompatibilityMode = source.ReplayAndroidCompatibilityMode ?? target.ReplayAndroidCompatibilityMode;
        target.ReplayTextAndInputPrivacy = Coalesce(source.ReplayTextAndInputPrivacy, target.ReplayTextAndInputPrivacy);
        target.ReplayTouchPrivacy = Coalesce(source.ReplayTouchPrivacy, target.ReplayTouchPrivacy);
        target.ReplayImagePrivacy = Coalesce(source.ReplayImagePrivacy, target.ReplayImagePrivacy);
        target.DiagnosticConsoleEnabled = source.DiagnosticConsoleEnabled ?? target.DiagnosticConsoleEnabled;
        target.DiagnosticFirstChanceExceptions = source.DiagnosticFirstChanceExceptions ?? target.DiagnosticFirstChanceExceptions;
    }

    private static void ConfigureExceptionDiagnostics(LocalRumSettings settings)
    {
        if (settings.DiagnosticConsoleEnabled != true || settings.DiagnosticFirstChanceExceptions != true)
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException -= LogFirstChanceException;
        AppDomain.CurrentDomain.FirstChanceException += LogFirstChanceException;
    }

    private static void LogDiagnostic(RumDiagnosticEvent item)
    {
        EnsureConsole();
        var status = item.StatusCode is null ? "" : $" status={item.StatusCode}";
        var line = $"[{item.Timestamp:HH:mm:ss.fff}] [Guance.RUM] {item.Level} {item.Source}{status}: {item.Message}";
        Console.WriteLine(line);
        Debug.WriteLine(line);

        if (item.Exception is not null)
        {
            Console.WriteLine(item.Exception);
            Debug.WriteLine(item.Exception);
        }
    }

    private static void LogFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        var exception = args.Exception;
        if (exception is not InvalidOperationException)
        {
            return;
        }

        EnsureConsole();
        var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [FirstChance] {exception.GetType().FullName}: {exception.Message}";
        Console.WriteLine(line);
        Console.WriteLine(exception.StackTrace);
        Debug.WriteLine(line);
        Debug.WriteLine(exception.StackTrace);
    }

    private static void EnsureConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
        {
            return;
        }

        if (AttachConsole(AttachParentProcess) || GetConsoleWindow() != IntPtr.Zero)
        {
            return;
        }

        _ = AllocConsole();
    }

    private static string? FindLocalSettingsFile()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, LocalSettingsFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static (string Key, string? Value) SplitArgument(string arg)
    {
        var index = arg.IndexOf('=');
        if (index < 0)
        {
            return (arg.Trim(), "true");
        }

        return (arg[..index].Trim(), arg[(index + 1)..].Trim());
    }

    private static string? Coalesce(string? first, string? second)
    {
        return string.IsNullOrWhiteSpace(first) ? second : first;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? ParseBool(string? value)
    {
        return bool.TryParse(value, out var result) ? result : null;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        throw new InvalidOperationException($"Invalid {typeof(TEnum).Name} value: {value}");
    }

    private sealed class LocalRumSettings
    {
        public string? DatawayUrl { get; set; }
        public string? DatakitUrl { get; set; }
        public string? ClientToken { get; set; }
        public string? RumAppId { get; set; }
        public string? ServiceName { get; set; }
        public string? Env { get; set; }
        public string? Version { get; set; }
        public bool? Debug { get; set; }
        public bool? SessionReplayEnabled { get; set; }
        public bool? ReplayAndroidCompatibilityMode { get; set; }
        public string? ReplayTextAndInputPrivacy { get; set; }
        public string? ReplayTouchPrivacy { get; set; }
        public string? ReplayImagePrivacy { get; set; }
        public bool? DiagnosticConsoleEnabled { get; set; }
        public bool? DiagnosticFirstChanceExceptions { get; set; }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
