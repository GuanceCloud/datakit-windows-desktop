using System.IO;
using Microsoft.Data.Sqlite;

namespace Guance.Rum.Windows.SessionReplay;

internal sealed class SqliteSessionReplayQueue : ISessionReplayQueue
{
    private readonly RumConfig config;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string databasePath;
    private bool initialized;

    public SqliteSessionReplayQueue(RumConfig config)
    {
        this.config = config;
        databasePath = Path.Combine(GetCacheDirectory(config), "session-replay-queue.db");
    }

    public async Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO replay_queue(content_type, body, created_at_unix_ms, size)
                VALUES ($contentType, $body, $createdAt, $size)";
            command.Parameters.AddWithValue("$contentType", contentType);
            command.Parameters.Add("$body", SqliteType.Blob).Value = body;
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$size", body.LongLength);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(int count, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<QueuedSessionReplaySegment>(count);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, content_type, body, created_at_unix_ms, size
                FROM replay_queue
                ORDER BY id ASC
                LIMIT $limit";
            command.Parameters.AddWithValue("$limit", count);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new QueuedSessionReplaySegment(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    (byte[])reader["body"],
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.GetInt64(4)));
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var id in ids)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM replay_queue WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task TrimAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            var replayConfig = config.SessionReplay;
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*), COALESCE(SUM(size), 0) FROM replay_queue";
            await using var reader = await countCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var count = reader.GetInt64(0);
            var bytes = reader.GetInt64(1);
            if (count > replayConfig.MaxQueueItems)
            {
                await DeleteOldestAsync(connection, count - replayConfig.MaxQueueItems, cancellationToken).ConfigureAwait(false);
            }

            if (bytes > replayConfig.MaxQueueBytes)
            {
                await DeleteOldestAsync(connection, Math.Max(1, count / 10), cancellationToken).ConfigureAwait(false);
                await using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM";
                await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
        gate.Dispose();
    }

    private async Task DeleteOldestAsync(SqliteConnection connection, long count, CancellationToken cancellationToken)
    {
        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText = @"
            DELETE FROM replay_queue
            WHERE id IN (
                SELECT id FROM replay_queue ORDER BY id ASC LIMIT $count
            )";
        deleteCommand.Parameters.AddWithValue("$count", count);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS replay_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content_type TEXT NOT NULL,
                    body BLOB NOT NULL,
                    created_at_unix_ms INTEGER NOT NULL,
                    size INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_replay_queue_id ON replay_queue(id);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static string GetCacheDirectory(RumConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CacheDirectory))
        {
            return config.CacheDirectory;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Path.GetTempPath(), "Guance");
        }

        return Path.Combine(root, "Guance", "Rum");
    }
}
