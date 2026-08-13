using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using Guance.Windows.Queue;

namespace Guance.Windows;

internal sealed record AnonymousUserIdResolution(string Value, Exception? PersistenceError);

internal static class AnonymousUserIdStore
{
    private const string Prefix = "ft.rd_";
    private const int IdentifierLength = 32;
    private const int LockAttemptCount = 40;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    public static AnonymousUserIdResolution LoadOrCreate(GuanceConfig config)
    {
        var fallback = CreateIdentifier();

        try
        {
            var path = GetPath(config);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            using var identityLock = AcquireLock(path + ".lock");
            if (TryRead(path, out var stored))
            {
                return new AnonymousUserIdResolution(stored, null);
            }

            if (TryRecoverTemporary(path, out var recovered))
            {
                return new AnonymousUserIdResolution(recovered, null);
            }

            WriteAtomic(path, fallback);
            return new AnonymousUserIdResolution(fallback, null);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return new AnonymousUserIdResolution(fallback, exception);
        }
    }

    internal static string GetPath(GuanceConfig config)
    {
        var scope = ComputeScope(config.RumAppId);
        return Path.Combine(CachePath.GetRoot(config), "identity", scope + ".id");
    }

    internal static bool IsValid(string? value)
    {
        if (value is null || value.Length != Prefix.Length + IdentifierLength ||
            !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = Prefix.Length; index < value.Length; index++)
        {
            var character = value[index];
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static string CreateIdentifier() => Prefix + Guid.NewGuid().ToString("N");

    private static string ComputeScope(string appId)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var value in Encoding.UTF8.GetBytes(appId))
        {
            hash ^= value;
            hash *= prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static FileStream AcquireLock(string path)
    {
        IOException? lastException = null;
        for (var attempt = 0; attempt < LockAttemptCount; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (IOException exception)
            {
                lastException = exception;
                Thread.Sleep(LockRetryDelay);
            }
        }

        throw lastException ?? new IOException("Unable to acquire the anonymous user identity lock.");
    }

    private static bool TryRead(string path, out string value)
    {
        value = string.Empty;
        if (!File.Exists(path) || new FileInfo(path).Length > 128)
        {
            return false;
        }

        var stored = File.ReadAllText(path, Encoding.UTF8);
        if (!IsValid(stored))
        {
            return false;
        }

        value = stored;
        return true;
    }

    private static bool TryRecoverTemporary(string path, out string value)
    {
        value = string.Empty;
        var directory = Path.GetDirectoryName(path)!;
        var pattern = Path.GetFileName(path) + ".*.new";
        foreach (var candidate in Directory.EnumerateFiles(directory, pattern).OrderBy(item => item, StringComparer.Ordinal))
        {
            if (!TryRead(candidate, out var recovered))
            {
                TryDelete(candidate);
                continue;
            }

            File.Move(candidate, path, overwrite: true);
            value = recovered;
            DeleteTemporaryFiles(directory, pattern);
            return true;
        }

        return false;
    }

    private static void WriteAtomic(string path, string value)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void DeleteTemporaryFiles(string directory, string pattern)
    {
        foreach (var path in Directory.EnumerateFiles(directory, pattern))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsPersistenceFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException;
}
