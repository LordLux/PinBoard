using Microsoft.Data.Sqlite;
using PinBoard.Helpers;
using PinBoard.Models;
using Windows.Storage;

// BundleSerializer is no longer used directly — BundleStorage wraps it with DPAPI.

namespace PinBoard.Services;

public sealed class SqliteHistoryStore : IHistoryStore
{
    private SqliteConnection? _db;
    private string _payloadsDir = "";

    public async Task InitializeAsync()
    {
        var localPath = ApplicationData.Current.LocalFolder.Path;
        var dbPath    = Path.Combine(localPath, "pinboard.db");
        _payloadsDir  = Path.Combine(localPath, "payloads");
        Directory.CreateDirectory(_payloadsDir);

        _db = new SqliteConnection($"Data Source={dbPath}");
        await _db.OpenAsync();

        await Exec("PRAGMA journal_mode=WAL;");
        await Exec("PRAGMA synchronous=NORMAL;");
        await Exec("PRAGMA foreign_keys=ON;");

        await Exec("""
            CREATE TABLE IF NOT EXISTS items (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc     INTEGER NOT NULL,
                kind            TEXT    NOT NULL,
                preview         TEXT,
                source_app      TEXT,
                source_app_path TEXT,
                payload_path    TEXT,
                hash            BLOB    UNIQUE,
                pinned          INTEGER NOT NULL DEFAULT 0,
                sensitive       INTEGER NOT NULL DEFAULT 0
            );
            """);

        await Exec("""
            CREATE VIRTUAL TABLE IF NOT EXISTS items_fts
            USING fts5(preview, source_app, content='items', content_rowid='id');
            """);

        await Exec("""
            CREATE INDEX IF NOT EXISTS idx_created ON items(created_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_pinned  ON items(pinned DESC, created_utc DESC);
            """);

        // FTS5 content-table triggers
        await Exec("""
            CREATE TRIGGER IF NOT EXISTS items_ai AFTER INSERT ON items BEGIN
                INSERT INTO items_fts(rowid, preview, source_app)
                VALUES (new.id, new.preview, new.source_app);
            END;
            CREATE TRIGGER IF NOT EXISTS items_ad AFTER DELETE ON items BEGIN
                INSERT INTO items_fts(items_fts, rowid, preview, source_app)
                VALUES ('delete', old.id, old.preview, old.source_app);
            END;
            CREATE TRIGGER IF NOT EXISTS items_au AFTER UPDATE ON items BEGIN
                INSERT INTO items_fts(items_fts, rowid, preview, source_app)
                VALUES ('delete', old.id, old.preview, old.source_app);
                INSERT INTO items_fts(rowid, preview, source_app)
                VALUES (new.id, new.preview, new.source_app);
            END;
            """);
    }

    public async Task<long> AddAsync(ClipItem item)
    {
        // Deduplication: bump timestamp instead of inserting a duplicate.
        if (item.Hash is { Length: > 0 })
        {
            var existingId = await ScalarAsync<long>(
                "SELECT id FROM items WHERE hash = @h LIMIT 1",
                ("@h", item.Hash));

            if (existingId > 0)
            {
                await Exec("UPDATE items SET created_utc = @t WHERE id = @id",
                    ("@t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    ("@id", existingId));
                return existingId;
            }
        }

        // Persist the FormatBundle to disk (DPAPI-encrypted via BundleStorage).
        string? payloadPath = null;
        if (item.Formats is not null)
        {
            var hashHex = item.Hash is { Length: > 0 }
                ? Convert.ToHexString(item.Hash)
                : Guid.NewGuid().ToString("N");

            payloadPath = Path.Combine(_payloadsDir, $"{hashHex}.bundle");
            await BundleStorage.WriteAsync(payloadPath, item.Formats);
        }

        await Exec("""
            INSERT INTO items (created_utc, kind, preview, source_app, source_app_path,
                               payload_path, hash, pinned, sensitive)
            VALUES (@t, @k, @p, @sa, @sap, @pp, @h, @pi, @se);
            """,
            ("@t",   item.CreatedAt.ToUnixTimeMilliseconds()),
            ("@k",   item.Kind.ToString()),
            ("@p",   (object?)item.Preview   ?? DBNull.Value),
            ("@sa",  (object?)item.SourceApp ?? DBNull.Value),
            ("@sap", (object?)item.SourceAppPath ?? DBNull.Value),
            ("@pp",  (object?)payloadPath    ?? DBNull.Value),
            ("@h",   (object?)item.Hash      ?? DBNull.Value),
            ("@pi",  item.Pinned  ? 1 : 0),
            ("@se",  item.Sensitive ? 1 : 0));

        return await ScalarAsync<long>("SELECT last_insert_rowid();");
    }

    public async Task UpdatePinnedAsync(long id, bool pinned) =>
        await Exec("UPDATE items SET pinned = @p WHERE id = @id",
            ("@p", pinned ? 1 : 0), ("@id", id));

    public async Task DeleteAsync(long id)
    {
        // Remove the payload file first.
        var path = await ScalarAsync<string?>(
            "SELECT payload_path FROM items WHERE id = @id", ("@id", id));

        if (path is not null && File.Exists(path))
            File.Delete(path);

        await Exec("DELETE FROM items WHERE id = @id", ("@id", id));
    }

    public async Task<IReadOnlyList<ClipItem>> GetRecentAsync(int limit = 100) =>
        await QueryItems(
            "SELECT * FROM items ORDER BY pinned DESC, created_utc DESC LIMIT @lim",
            ("@lim", limit));

    public async Task<IReadOnlyList<ClipItem>> SearchAsync(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query)) return await GetRecentAsync(limit);

        // Escape FTS5 special characters.
        var safeQuery = query.Replace("\"", "\"\"");
        return await QueryItems("""
            SELECT items.* FROM items
            JOIN items_fts ON items.id = items_fts.rowid
            WHERE items_fts MATCH @q
            ORDER BY items_fts.rank
            LIMIT @lim
            """,
            ("@q", $"\"{safeQuery}\""), ("@lim", limit));
    }

    public async Task ClearUnpinnedAsync()
    {
        // Delete payload files for unpinned items.
        var paths = await QueryColumnAsync<string>("SELECT payload_path FROM items WHERE pinned = 0");
        foreach (var p in paths)
            if (p is not null && File.Exists(p)) File.Delete(p);

        await Exec("DELETE FROM items WHERE pinned = 0");
    }

    public async Task EvictOldestAsync(int keepCount)
    {
        var paths = await QueryColumnAsync<string>("""
            SELECT payload_path FROM items
            WHERE pinned = 0 AND id NOT IN (
                SELECT id FROM items WHERE pinned = 0
                ORDER BY created_utc DESC LIMIT @k)
            """, ("@k", keepCount));

        foreach (var p in paths)
            if (p is not null && File.Exists(p)) File.Delete(p);

        await Exec("""
            DELETE FROM items WHERE pinned = 0 AND id NOT IN (
                SELECT id FROM items WHERE pinned = 0
                ORDER BY created_utc DESC LIMIT @k)
            """, ("@k", keepCount));
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.CloseAsync();
            await _db.DisposeAsync();
        }
    }

    // ── SQLite helpers ────────────────────────────────────────────────────────

    private Task Exec(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, val) in p) cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);
        return cmd.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, val) in p) cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);
        var raw = await cmd.ExecuteScalarAsync();
        if (raw is null or DBNull) return default;
        return (T)Convert.ChangeType(raw, typeof(T));
    }

    private async Task<IReadOnlyList<ClipItem>> QueryItems(
        string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, val) in p) cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);

        var results = new List<ClipItem>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(MapRow(reader));

        return results;
    }

    private async Task<IReadOnlyList<T?>> QueryColumnAsync<T>(
        string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, val) in p) cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);

        var results = new List<T?>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var v = reader.IsDBNull(0) ? default : (T?)Convert.ChangeType(reader.GetValue(0), typeof(T));
            results.Add(v);
        }

        return results;
    }

    private static ClipItem MapRow(SqliteDataReader r)
    {
        Enum.TryParse<ClipItemKind>(r.GetString(r.GetOrdinal("kind")), out var kind);
        return new ClipItem
        {
            Id            = r.GetInt64(r.GetOrdinal("id")),
            CreatedAt     = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_utc"))),
            Kind          = kind,
            Preview       = r.IsDBNull(r.GetOrdinal("preview"))       ? null : r.GetString(r.GetOrdinal("preview")),
            SourceApp     = r.IsDBNull(r.GetOrdinal("source_app"))     ? null : r.GetString(r.GetOrdinal("source_app")),
            SourceAppPath = r.IsDBNull(r.GetOrdinal("source_app_path"))? null : r.GetString(r.GetOrdinal("source_app_path")),
            PayloadPath   = r.IsDBNull(r.GetOrdinal("payload_path"))   ? null : r.GetString(r.GetOrdinal("payload_path")),
            Hash          = r.IsDBNull(r.GetOrdinal("hash"))           ? null : (byte[])r.GetValue(r.GetOrdinal("hash")),
            Pinned        = r.GetInt32(r.GetOrdinal("pinned"))  == 1,
            Sensitive     = r.GetInt32(r.GetOrdinal("sensitive")) == 1,
        };
    }
}
