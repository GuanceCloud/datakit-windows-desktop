using System.IO;
using Microsoft.Data.Sqlite;

namespace Guance.Rum.Windows.Queue;

internal sealed class SqliteRumQueue : IRumQueue
{
    private readonly RumConfig config;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string databasePath;
    private bool initialized;

    public SqliteRumQueue(RumConfig config)
    {
        this.config = config;
        databasePath = Path.Combine(GetCacheDirectory(config), "rum-queue.db");
    }

    public async Task EnqueueAsync(string line, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO rum_queue(line, created_at_unix_ms)
                VALUES ($line, $createdAt)";
            command.Parameters.AddWithValue("$line", line);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<QueuedRumEvent>> PeekAsync(int count, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<QueuedRumEvent>(count);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, line, created_at_unix_ms
                FROM rum_queue
                ORDER BY id ASC
                LIMIT $limit";
            command.Parameters.AddWithValue("$limit", count);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new QueuedRumEvent(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
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
                command.CommandText = "DELETE FROM rum_queue WHERE id = $id";
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
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM rum_queue";
            var count = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (count > config.MaxQueueItems)
            {
                var deleteCount = count - config.MaxQueueItems;
                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = @"
                    DELETE FROM rum_queue
                    WHERE id IN (
                        SELECT id FROM rum_queue ORDER BY id ASC LIMIT $deleteCount
                    )";
                deleteCommand.Parameters.AddWithValue("$deleteCount", deleteCount);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var file = new FileInfo(databasePath);
            if (file.Exists && file.Length > config.MaxQueueBytes)
            {
                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = @"
                    DELETE FROM rum_queue
                    WHERE id IN (
                        SELECT id FROM rum_queue ORDER BY id ASC LIMIT (
                            SELECT MAX(1, COUNT(*) / 10) FROM rum_queue
                        )
                    )";
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                CREATE TABLE IF NOT EXISTS rum_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    line TEXT NOT NULL,
                    created_at_unix_ms INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_rum_queue_created ON rum_queue(id);";
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
