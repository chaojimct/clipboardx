using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClipboardManager;

/// <summary>
/// 剪贴板普通历史（不含快捷短语）的 SQLite 持久化。库路径：%LocalAppData%\ClipboardX\clipboard_history.db
/// </summary>
internal sealed class ClipboardHistoryStore
{
    private static string DbDir => Path.GetDirectoryName(AppPaths.SqliteDbFile)!;
    private static string DbPath => AppPaths.SqliteDbFile;

    private static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public ClipboardHistoryStore()
    {
        try
        {
            Directory.CreateDirectory(DbDir);
            EnsureSchema();
        }
        catch
        {
            // 降级为仅内存历史
        }
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS clipboard_history (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              entry_type INTEGER NOT NULL,
              text_content TEXT,
              image_blob BLOB,
              image_w INTEGER NOT NULL DEFAULT 0,
              image_h INTEGER NOT NULL DEFAULT 0,
              file_paths_json TEXT,
              copied_at_ms INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_clipboard_history_copied ON clipboard_history(copied_at_ms DESC);
            """;
        cmd.ExecuteNonQuery();
        MigrateSchema(conn);
    }

    private static void MigrateSchema(SqliteConnection conn)
    {
        if (HasColumn(conn, "ocr_text")) return;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE clipboard_history ADD COLUMN ocr_text TEXT";
        cmd.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection conn, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(clipboard_history)";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    private static long ToMs(DateTime dt) => new DateTimeOffset(dt).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    /// <summary>按时间从新到旧最多读取 limit 条。</summary>
    public List<ClipboardEntry> LoadNewestFirst(int limit)
    {
        if (limit <= 0) return [];
        var list = new List<ClipboardEntry>(Math.Min(limit, 64));
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, entry_type, text_content, image_blob, image_w, image_h, file_paths_json, copied_at_ms, ocr_text
                FROM clipboard_history
                ORDER BY copied_at_ms DESC
                LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadEntry(r));
        }
        catch
        {
            // ignore
        }
        return list;
    }

    /// <summary>
    /// 轻量加载：不读取 image_blob，仅加载元数据。图片数据按需通过 <see cref="LoadImageData"/> 获取。
    /// 用于启动时大幅降低内存占用——图片条目仅在需要时才从数据库读取。
    /// </summary>
    public List<ClipboardEntry> LoadNewestFirstLite(int limit)
    {
        if (limit <= 0) return [];
        var list = new List<ClipboardEntry>(Math.Min(limit, 64));
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // 不查 image_blob 列，仅取元数据 + 文本/文件路径/OCR
            cmd.CommandText =
                """
                SELECT id, entry_type, text_content, image_w, image_h, file_paths_json, copied_at_ms, ocr_text
                FROM clipboard_history
                ORDER BY copied_at_ms DESC
                LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var entry = new ClipboardEntry
                {
                    PersistedId = r.GetInt64(0),
                    Type = (EntryType)r.GetInt32(1),
                    CopiedAt = FromMs(r.GetInt64(6)),
                    IsQuickPaste = false
                };
                if (!r.IsDBNull(2)) entry.TextContent = r.GetString(2);
                // ImageData 故意不加载——标记为需要按需获取
                entry.ImageData = entry.Type == EntryType.Image ? null : null;
                entry.ImageWidth = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                entry.ImageHeight = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                if (!r.IsDBNull(5))
                {
                    var json = r.GetString(5);
                    entry.FilePaths = JsonSerializer.Deserialize<string[]>(json) ?? [];
                }
                if (r.FieldCount > 7 && !r.IsDBNull(7))
                    entry.OcrText = r.GetString(7);
                list.Add(entry);
            }
        }
        catch
        {
            // ignore
        }
        return list;
    }

    /// <summary>按需加载单条图片的 PNG 字节数据。</summary>
    public byte[]? LoadImageData(long persistedId)
    {
        if (persistedId <= 0) return null;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT image_blob FROM clipboard_history WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", persistedId);
            var result = cmd.ExecuteScalar();
            return result as byte[];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删除超出图片条目专项上限的旧图片记录，返回被删除的 PersistedId 列表。</summary>
    public List<long> PruneExcessImages(int maxImageKeep)
    {
        var deleted = new List<long>();
        if (maxImageKeep < 0) return deleted;
        try
        {
            using var conn = Open();
            // 查出超出上限的图片条目 ID
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT id FROM clipboard_history
                WHERE entry_type = @et
                ORDER BY copied_at_ms DESC
                LIMIT -1 OFFSET @lim
                """;
            cmd.Parameters.AddWithValue("@et", (int)EntryType.Image);
            cmd.Parameters.AddWithValue("@lim", maxImageKeep);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                deleted.Add(r.GetInt64(0));

            if (deleted.Count == 0) return deleted;

            // 批量删除
            using var delCmd = conn.CreateCommand();
            var ids = string.Join(",", deleted);
            delCmd.CommandText = $"DELETE FROM clipboard_history WHERE id IN ({ids})";
            delCmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
        return deleted;
    }

    /// <summary>仅保留按时间最新的 maxKeep 条，删除其余（与设置中的条数上限对齐）。</summary>
    public void PruneExcess(int maxKeep)
    {
        if (maxKeep < 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                DELETE FROM clipboard_history
                WHERE id NOT IN (
                  SELECT id FROM clipboard_history
                  ORDER BY copied_at_ms DESC
                  LIMIT @lim
                );
                """;
            cmd.Parameters.AddWithValue("@lim", maxKeep);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }

    private static ClipboardEntry ReadEntry(SqliteDataReader r)
    {
        var entry = new ClipboardEntry
        {
            PersistedId = r.GetInt64(0),
            Type = (EntryType)r.GetInt32(1),
            CopiedAt = FromMs(r.GetInt64(7)),
            IsQuickPaste = false
        };
        if (!r.IsDBNull(2)) entry.TextContent = r.GetString(2);
        if (!r.IsDBNull(3)) entry.ImageData = (byte[])r.GetValue(3);
        entry.ImageWidth = r.IsDBNull(4) ? 0 : r.GetInt32(4);
        entry.ImageHeight = r.IsDBNull(5) ? 0 : r.GetInt32(5);
        if (!r.IsDBNull(6))
        {
            var json = r.GetString(6);
            entry.FilePaths = JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        if (r.FieldCount > 8 && !r.IsDBNull(8))
            entry.OcrText = r.GetString(8);
        return entry;
    }

    /// <returns>是否成功写入并得到新 id</returns>
    public bool TryInsert(ClipboardEntry entry)
    {
        if (entry.IsQuickPaste) return false;
        try
        {
            var ms = ToMs(entry.CopiedAt);
            string? filesJson = entry.Type == EntryType.Files && entry.FilePaths is { Length: > 0 }
                ? JsonSerializer.Serialize(entry.FilePaths)
                : null;

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO clipboard_history (entry_type, text_content, image_blob, image_w, image_h, file_paths_json, copied_at_ms)
                VALUES (@t, @text, @blob, @w, @h, @files, @ms)
                """;
            cmd.Parameters.AddWithValue("@t", (int)entry.Type);
            cmd.Parameters.AddWithValue("@text", (object?)entry.TextContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@blob", (object?)entry.ImageData ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@w", entry.ImageWidth);
            cmd.Parameters.AddWithValue("@h", entry.ImageHeight);
            cmd.Parameters.AddWithValue("@files", (object?)filesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ms", ms);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT last_insert_rowid()";
            var idObj = cmd.ExecuteScalar();
            if (idObj is long lid) entry.PersistedId = lid;
            else if (idObj != null) entry.PersistedId = Convert.ToInt64(idObj);
            return entry.PersistedId.HasValue;
        }
        catch
        {
            return false;
        }
    }

    public void TryDelete(long? persistedId)
    {
        if (persistedId is not long id || id <= 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_history WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }

    public void TryUpdateCopiedAt(long persistedId, DateTime copiedAt)
    {
        if (persistedId <= 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_history SET copied_at_ms = @ms WHERE id = @id";
            cmd.Parameters.AddWithValue("@ms", ToMs(copiedAt));
            cmd.Parameters.AddWithValue("@id", persistedId);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>就地更新文本条目的内容（entry_type 仍为文本）。</summary>
    public void TryUpdateText(long persistedId, string text)
    {
        if (persistedId <= 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE clipboard_history SET text_content = @t WHERE id = @id AND entry_type = @et";
            cmd.Parameters.AddWithValue("@t", text);
            cmd.Parameters.AddWithValue("@id", persistedId);
            cmd.Parameters.AddWithValue("@et", (int)EntryType.Text);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }

    public void TryUpdateOcrText(long persistedId, string? ocrText)
    {
        if (persistedId <= 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE clipboard_history SET ocr_text = @ocr WHERE id = @id AND entry_type = @et";
            cmd.Parameters.AddWithValue("@ocr", (object?)ocrText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", persistedId);
            cmd.Parameters.AddWithValue("@et", (int)EntryType.Image);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }

    public void DeleteAll()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_history";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }
}
