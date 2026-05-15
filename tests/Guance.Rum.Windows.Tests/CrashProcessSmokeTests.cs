using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Guance.Rum.Windows.Tests;

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
        var crashSmokeDll = Path.Combine(repoRoot, "tests", "Guance.Rum.CrashSmoke", "bin", "Release", "net8.0", "Guance.Rum.CrashSmoke.dll");
        Assert.True(File.Exists(crashSmokeDll), $"Crash smoke app was not built: {crashSmokeDll}");
        var cacheDir = Path.Combine(Path.GetTempPath(), "guance-rum-crash-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);

        using var listener = new HttpListener();
        var port = GetFreePort();
        var datakitUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(datakitUrl + "/");
        listener.Start();

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
        Assert.Contains("error_source=crash", body, StringComparison.Ordinal);
        Assert.Contains("is_crash=true", body, StringComparison.Ordinal);
        Assert.Contains("crash_source=\"AppDomain.UnhandledException\"", body, StringComparison.Ordinal);
        Assert.Contains("guance crash smoke", body, StringComparison.Ordinal);
    }

    private static async Task<string> ReadRequestAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = 202;
        context.Response.Close();
        return body;
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

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Guance.Rum.Windows.sln")))
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
}
