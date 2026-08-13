using System.IO;
using System.Text.RegularExpressions;
using Guance.Windows.Queue;
using Guance.Windows.Transport;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class AnonymousUserIdTests
{
    [Fact]
    public void Store_PersistsAndroidCompatibleIdentifier()
    {
        var config = CreateConfig();

        var first = AnonymousUserIdStore.LoadOrCreate(config);
        var second = AnonymousUserIdStore.LoadOrCreate(config);

        Assert.Null(first.PersistenceError);
        Assert.Equal(first.Value, second.Value);
        Assert.Matches("^ft\\.rd_[0-9a-f]{32}$", first.Value);
        Assert.Equal("e74fa719056a838c.id", Path.GetFileName(AnonymousUserIdStore.GetPath(config)));
        Assert.Equal(first.Value, File.ReadAllText(AnonymousUserIdStore.GetPath(config)));
    }

    [Fact]
    public void Store_IsolatesIdentifiersByRumApplication()
    {
        var cacheDirectory = CreateCacheDirectory();
        var first = AnonymousUserIdStore.LoadOrCreate(CreateConfig(cacheDirectory, "app-one"));
        var second = AnonymousUserIdStore.LoadOrCreate(CreateConfig(cacheDirectory, "app-two"));

        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void Store_ReplacesCorruptIdentity()
    {
        var config = CreateConfig();
        var path = AnonymousUserIdStore.GetPath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "invalid");

        var result = AnonymousUserIdStore.LoadOrCreate(config);

        Assert.Null(result.PersistenceError);
        Assert.True(AnonymousUserIdStore.IsValid(result.Value));
        Assert.Equal(result.Value, File.ReadAllText(path));
    }

    [Fact]
    public void Store_RecoversDurableTemporaryIdentity()
    {
        var config = CreateConfig();
        var path = AnonymousUserIdStore.GetPath(config);
        var recovered = "ft.rd_" + Guid.NewGuid().ToString("N");
        var temporaryPath = path + ".recovery.new";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temporaryPath, recovered);

        var result = AnonymousUserIdStore.LoadOrCreate(config);

        Assert.Null(result.PersistenceError);
        Assert.Equal(recovered, result.Value);
        Assert.Equal(recovered, File.ReadAllText(path));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task Store_ConcurrentInitializationConvergesOnOneIdentifier()
    {
        var config = CreateConfig();

        var resolutions = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => AnonymousUserIdStore.LoadOrCreate(config))));

        Assert.All(resolutions, item => Assert.Null(item.PersistenceError));
        Assert.Single(resolutions.Select(item => item.Value).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RumEvents_ReuseAnonymousIdentifierAcrossClientRestart()
    {
        var config = CreateConfig();
        var firstQueue = new MemoryRumQueue();
        string firstUserId;
        string firstSessionId;
        await using (var firstClient = CreateClient(config, firstQueue))
        {
            firstSessionId = firstClient.GetDiagnosticsSnapshot().SessionId;
            firstClient.AddAction("first", "custom", TimeSpan.FromMilliseconds(1));
            await firstClient.FlushAsync();
            firstUserId = GetTag(Assert.Single(await ReadItems(firstQueue)).Line, RumConstants.UserId);
        }

        var secondQueue = new MemoryRumQueue();
        await using var secondClient = CreateClient(config, secondQueue);
        var secondSessionId = secondClient.GetDiagnosticsSnapshot().SessionId;
        secondClient.AddAction("second", "custom", TimeSpan.FromMilliseconds(1));
        await secondClient.FlushAsync();
        var secondUserId = GetTag(Assert.Single(await ReadItems(secondQueue)).Line, RumConstants.UserId);

        Assert.Equal(firstUserId, secondUserId);
        Assert.NotEqual(firstSessionId, secondSessionId);
        Assert.NotEqual(firstSessionId, firstUserId);
        Assert.NotEqual(secondSessionId, secondUserId);
    }

    [Fact]
    public async Task ClearUser_RestoresOriginalAnonymousIdentifier()
    {
        var queue = new MemoryRumQueue();
        await using var client = CreateClient(CreateConfig(), queue);

        client.AddAction("anonymous-before", "custom", TimeSpan.FromMilliseconds(1));
        client.SetUser("user-1");
        client.AddAction("signed-in", "custom", TimeSpan.FromMilliseconds(1));
        client.ClearUser();
        client.AddAction("anonymous-after", "custom", TimeSpan.FromMilliseconds(1));
        await client.FlushAsync();

        var items = await ReadItems(queue, 3);
        Assert.Equal(3, items.Count);
        var before = GetTag(items[0].Line, RumConstants.UserId);
        var signedIn = GetTag(items[1].Line, RumConstants.UserId);
        var after = GetTag(items[2].Line, RumConstants.UserId);
        Assert.Equal(before, after);
        Assert.Equal("user-1", signedIn);
        Assert.Contains("is_signin=F", items[0].Line, StringComparison.Ordinal);
        Assert.Contains("is_signin=T", items[1].Line, StringComparison.Ordinal);
        Assert.Contains("is_signin=F", items[2].Line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityFile_IsExcludedFromTelemetryCacheDiagnostics()
    {
        var config = CreateConfig();
        await using var client = new GuanceClient(config);

        var snapshot = client.GetCacheDiagnosticsSnapshot();

        Assert.True(File.Exists(AnonymousUserIdStore.GetPath(config)));
        Assert.Equal(0, snapshot.AllocatedBytes);
        Assert.Equal(0, snapshot.FileCount);
    }

    [Fact]
    public async Task PersistenceFailure_UsesEphemeralIdentifierAndReportsDiagnostic()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "guance-anonymous-user-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(cachePath, "not-a-directory");
        var diagnostics = new List<RumDiagnosticEvent>();
        var config = CreateConfig(cachePath, diagnosticListener: diagnostics.Add);
        var queue = new MemoryRumQueue();

        await using var client = CreateClient(config, queue);
        client.AddAction("ephemeral", "custom", TimeSpan.FromMilliseconds(1));
        await client.FlushAsync();

        Assert.True(AnonymousUserIdStore.IsValid(GetTag(Assert.Single(await ReadItems(queue)).Line, RumConstants.UserId)));
        Assert.Contains(diagnostics, item => item.Source == "identity" && item.Level == RumDiagnosticLevel.Warning);
    }

    private static GuanceClient CreateClient(GuanceConfig config, MemoryRumQueue queue) =>
        new(config, queue, new HoldingRumTransport());

    private static Task<IReadOnlyList<QueuedRumEvent>> ReadItems(MemoryRumQueue queue, int count = 1) =>
        queue.PeekAsync(count, CancellationToken.None);

    private static GuanceConfig CreateConfig(
        string? cacheDirectory = null,
        string rumAppId = "app",
        Action<RumDiagnosticEvent>? diagnosticListener = null) => new()
    {
        DatakitUrl = "http://127.0.0.1:9529",
        RumAppId = rumAppId,
        Env = "local",
        CacheDirectory = cacheDirectory ?? CreateCacheDirectory(),
        FlushInterval = TimeSpan.FromMinutes(5),
        DiagnosticListener = diagnosticListener
    };

    private static string CreateCacheDirectory() =>
        Path.Combine(Path.GetTempPath(), "guance-anonymous-user-tests", Guid.NewGuid().ToString("N"));

    private static string GetTag(string line, string name)
    {
        var match = Regex.Match(line, "(?:^|,)" + Regex.Escape(name) + "=(?<value>[^, ]+)");
        Assert.True(match.Success, $"Tag '{name}' was not found in: {line}");
        return match.Groups["value"].Value;
    }

    private sealed class HoldingRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(
            IReadOnlyList<QueuedRumEvent> events,
            CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));

        public void Dispose()
        {
        }
    }
}
