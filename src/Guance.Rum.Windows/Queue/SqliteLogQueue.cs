using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Guance.Rum.Windows.Queue;

internal sealed class SqliteLogQueue : ILogQueue
{
    private readonly RumConfig config;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string databasePath;
    private bool initialized;

    public SqliteLogQueue(RumConfig config)
    {
        this.config = config;
        databasePath = Path.Combine(GetCacheDirectory(config), "logging-queue.db");
    }

    public async Task<bool> EnqueueAsync(string line, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var incomingBytes = Encoding.UTF8.GetByteCount(line);
            var (count, bytes) = await GetSizeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            if (config.Logging.DiscardStrategy == RumLogDiscardStrategy.DiscardNew &&
                (count >= config.Logging.MaxQueueItems || bytes + incomingBytes > config.Logging.MaxQueueBytes))
            {
                transaction.Commit();
                return false;
            }

            while (count >= config.Logging.MaxQueueItems || bytes + incomingBytes > config.Logging.MaxQueueBytes)
            {
                var removedBytes = await DeleteOldestAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (removedBytes < 0)
                {
                    transaction.Commit();
                    return false;
                }

                count--;
                bytes -= removedBytes;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO log_queue(line, created_at_unix_ms)
                VALUES ($line, $createdAt)";
            command.Parameters.AddWithValue("$line", line);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<QueuedLogEvent>> PeekAsync(int count, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<QueuedLogEvent>(count);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, line, created_at_unix_ms
                FROM log_queue
                ORDER BY id ASC
                LIMIT $limit";
            command.Parameters.AddWithValue("$limit", count);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new QueuedLogEvent(
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
                command.CommandText = "DELETE FROM log_queue WHERE id = $id";
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
                CREATE TABLE IF NOT EXISTS log_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    line TEXT NOT NULL,
                    created_at_unix_ms INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_log_queue_created ON log_queue(id);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(long Count, long Bytes)> GetSizeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(LENGTH(CAST(line AS BLOB))), 0) FROM log_queue";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<long> DeleteOldestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        long id;
        long bytes;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, LENGTH(CAST(line AS BLOB)) FROM log_queue ORDER BY id ASC LIMIT 1";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return -1;
            }

            id = reader.GetInt64(0);
            bytes = reader.GetInt64(1);
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM log_queue WHERE id = $id";
        delete.Parameters.AddWithValue("$id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return bytes;
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
