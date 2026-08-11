using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class CrashProcessSmokeTests
{
    [Fact]
    public async Task UnhandledExceptionProcess_SynchronouslyUploadsCrashEventBeforeExit()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("GUANCE_RUM_SKIP_CRASH_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var crashSmokeDll = Path.Combine(repoRoot, "tests", "Guance.Windows.CrashSmoke", "bin", BuildConfiguration, "net8.0", "Guance.Windows.CrashSmoke.dll");
        Assert.True(File.Exists(crashSmokeDll), $"Crash smoke app was not built: {crashSmokeDll}");
        var cacheDir = Path.Combine(Path.GetTempPath(), "guance-rum-crash-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var datakitUrl = $"http://127.0.0.1:{port}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var requestTask = ReadRequestAsync(listener, cts.Token);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{Quote(crashSmokeDll)} {Quote(datakitUrl)} {Quote(cacheDir)}",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Failed to start crash smoke process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(30));
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        var output = await stdoutTask;
        var error = await stderrTask;
        Assert.True(exited, $"Crash smoke process timed out. stdout={output} stderr={error}");
        Assert.NotEqual(0, process.ExitCode);

        var body = await requestTask;
        Assert.Contains("error,", body, StringComparison.Ordinal);
        Assert.Contains("error_source=logger", body, StringComparison.Ordinal);
        Assert.Contains("error_type=windows_crash", body, StringComparison.Ordinal);
        Assert.DoesNotContain("is_crash", body, StringComparison.Ordinal);
        Assert.DoesNotContain("crash_source", body, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", body, StringComparison.Ordinal);
        Assert.Contains("guance crash smoke", body, StringComparison.Ordinal);
    }

    private static async Task<string> ReadRequestAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var headerBytes = new List<byte>();
        uint tail = 0;
        while (tail != 0x0d0a0d0aU)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                throw new EndOfStreamException("HTTP request ended before the headers were complete.");
            }

            headerBytes.Add((byte)value);
            tail = (tail << 8) | (uint)value;
            if (headerBytes.Count > 64 * 1024)
            {
                throw new InvalidDataException("HTTP request headers exceeded the smoke-test limit.");
            }
        }

        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLengthHeader = headers
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var contentLength = int.Parse(contentLengthHeader[(contentLengthHeader.IndexOf(':') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var bodyBytes = new byte[contentLength];
        var totalRead = 0;
        while (totalRead < bodyBytes.Length)
        {
            var read = await stream.ReadAsync(bodyBytes.AsMemory(totalRead), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("HTTP request ended before the body was complete.");
            }

            totalRead += read;
        }

        var response = Encoding.ASCII.GetBytes("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return Encoding.UTF8.GetString(bodyBytes);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Guance.Windows.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
}
