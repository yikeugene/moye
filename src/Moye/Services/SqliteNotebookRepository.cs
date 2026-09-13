using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moye.Models;

namespace Moye.Services;

/// <summary>Local, transactional notebook storage. All SQLite work runs off the UI thread.</summary>
public sealed class SqliteNotebookRepository : INotebookRepository
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private volatile bool _disposed;

    public SqliteNotebookRepository(string? databasePath = null)
    {
        _path = Path.GetFullPath(databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moye", "moye.db"));
    }

    public string DatabasePath => _path;

    public Task InitializeAsync() => RunAsync(connection => true);

    public Task<IReadOnlyList<NotebookSummary>> ListAsync() => RunAsync<IReadOnlyList<NotebookSummary>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id,n.title,n.folder,n.modified_utc,COUNT(p.id)
            FROM notebooks n LEFT JOIN pages p ON p.notebook_id=n.id
            GROUP BY n.id ORDER BY n.modified_utc DESC,n.id
            """;
        using var reader = command.ExecuteReader();
        var result = new List<NotebookSummary>();
        while (reader.Read()) result.Add(new NotebookSummary
        {
            Id = reader.GetString(0), Title = reader.GetString(1), Folder = reader.GetString(2),
            ModifiedUtc = ParseDate(reader.GetString(3)), PageCount = reader.GetInt32(4)
        });
        return result;
    });

    public Task<NotebookDocument?> LoadAsync(string id) => RunAsync<NotebookDocument?>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title,folder,created_utc,modified_utc FROM notebooks WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        NotebookDocument document;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            document = new NotebookDocument
            {
                Id = id, Title = reader.GetString(0), Folder = reader.GetString(1),
                CreatedUtc = ParseDate(reader.GetString(2)), ModifiedUtc = ParseDate(reader.GetString(3))
            };
        }
        command.CommandText = "SELECT metadata_json,ink FROM pages WHERE notebook_id=$id ORDER BY ordinal";
        using var pages = command.ExecuteReader();
        while (pages.Read())
        {
            var page = JsonSerializer.Deserialize<NotePage>(pages.GetString(0), DocumentJson.Options)
                ?? throw new InvalidDataException("The page data is corrupted.");
            page.InkData = pages.GetFieldValue<byte[]>(1);
            document.Pages.Add(page);
        }
        return document;
    });

    public Task SaveAsync(NotebookDocument document)
    {
        // Snapshot before dispatch, while the caller still owns the editable model.
        var snapshot = document.Snapshot();
        foreach (var page in snapshot.Pages) page.InkData = page.InkData.ToArray();
        return RunAsync(connection =>
        {
            if (string.IsNullOrWhiteSpace(snapshot.Id) || snapshot.Pages.Select(p => p.Id).Distinct().Count() != snapshot.Pages.Count)
                throw new InvalidDataException("Invalid notebook or page ID.");
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO notebooks(id,title,folder,created_utc,modified_utc) VALUES($id,$title,$folder,$created,$modified)
                ON CONFLICT(id) DO UPDATE SET title=excluded.title,folder=excluded.folder,modified_utc=excluded.modified_utc
                """;
            command.Parameters.AddWithValue("$id", snapshot.Id);
            command.Parameters.AddWithValue("$title", snapshot.Title);
            command.Parameters.AddWithValue("$folder", snapshot.Folder);
            command.Parameters.AddWithValue("$created", FormatDate(snapshot.CreatedUtc));
            command.Parameters.AddWithValue("$modified", FormatDate(snapshot.ModifiedUtc));
            command.ExecuteNonQuery();

            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            command.CommandText = "SELECT id,content_hash FROM pages WHERE notebook_id=$id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", snapshot.Id);
            using (var reader = command.ExecuteReader()) while (reader.Read()) existing.Add(reader.GetString(0), reader.GetString(1));

            for (var index = 0; index < snapshot.Pages.Count; index++)
            {
                var page = snapshot.Pages[index];
                var ink = page.InkData;
                page.InkData = [];
                var json = JsonSerializer.Serialize(page, DocumentJson.Options);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(Encoding.UTF8.GetBytes(json));
                hash.AppendData(ink);
                var contentHash = Convert.ToHexStringLower(hash.GetHashAndReset());
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$note", snapshot.Id);
                command.Parameters.AddWithValue("$page", page.Id);
                command.Parameters.AddWithValue("$ordinal", index);
                if (existing.TryGetValue(page.Id, out var previous) && previous == contentHash)
                {
                    command.CommandText = "UPDATE pages SET ordinal=$ordinal WHERE notebook_id=$note AND id=$page AND ordinal<>$ordinal";
                }
                else
                {
                    command.CommandText = """
                        INSERT INTO pages(notebook_id,id,ordinal,metadata_json,ink,content_hash) VALUES($note,$page,$ordinal,$json,$ink,$hash)
                        ON CONFLICT(notebook_id,id) DO UPDATE SET ordinal=excluded.ordinal,metadata_json=excluded.metadata_json,ink=excluded.ink,content_hash=excluded.content_hash
                        """;
                    command.Parameters.AddWithValue("$json", json);
                    command.Parameters.Add("$ink", SqliteType.Blob).Value = ink;
                    command.Parameters.AddWithValue("$hash", contentHash);
                }
                command.ExecuteNonQuery();
                existing.Remove(page.Id);
            }
            foreach (var removed in existing.Keys)
            {
                command.Parameters.Clear();
                command.CommandText = "DELETE FROM pages WHERE notebook_id=$note AND id=$page";
                command.Parameters.AddWithValue("$note", snapshot.Id);
                command.Parameters.AddWithValue("$page", removed);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        });
    }

    public Task DeleteAsync(string id) => RunAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notebooks WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        return true;
    });

    public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes)
    {
        var snapshot = bytes.ToArray();
        return RunAsync(connection =>
        {
            var id = Convert.ToHexStringLower(SHA256.HashData(snapshot));
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO assets(id,file_name,content_type,data) VALUES($id,$name,$type,$data)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", Path.GetFileName(fileName));
            command.Parameters.AddWithValue("$type", contentType);
            command.Parameters.Add("$data", SqliteType.Blob).Value = snapshot;
            command.ExecuteNonQuery();
            return new AssetData(id, Path.GetFileName(fileName), contentType, snapshot);
        });
    }

    public Task<AssetData> GetAssetAsync(string id) => RunAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_name,content_type,data FROM assets WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new FileNotFoundException("An image or PDF used by this notebook could not be found.", id);
        return new AssetData(id, reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2));
    });

    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
                    ForeignKeys = true, DefaultTimeout = 15
                }.ToString());
                connection.Open();
                using var setup = connection.CreateCommand();
                setup.CommandText = "PRAGMA synchronous=FULL;";
                setup.ExecuteNonQuery();
                if (!_initialized)
                {
                    setup.CommandText = "PRAGMA user_version;";
                    if (Convert.ToInt32(setup.ExecuteScalar(), CultureInfo.InvariantCulture) > 1)
                        throw new InvalidDataException("This database was created by a newer version of Moye. Update the app before opening it.");
                    setup.CommandText = """
                        PRAGMA journal_mode=WAL;
                        CREATE TABLE IF NOT EXISTS notebooks(id TEXT PRIMARY KEY,title TEXT NOT NULL,folder TEXT NOT NULL,created_utc TEXT NOT NULL,modified_utc TEXT NOT NULL);
                        CREATE TABLE IF NOT EXISTS pages(notebook_id TEXT NOT NULL REFERENCES notebooks(id) ON DELETE CASCADE,id TEXT NOT NULL,ordinal INTEGER NOT NULL,metadata_json TEXT NOT NULL,ink BLOB NOT NULL,content_hash TEXT NOT NULL,PRIMARY KEY(notebook_id,id));
                        CREATE INDEX IF NOT EXISTS ix_pages_order ON pages(notebook_id,ordinal);
                        CREATE TABLE IF NOT EXISTS assets(id TEXT PRIMARY KEY,file_name TEXT NOT NULL,content_type TEXT NOT NULL,data BLOB NOT NULL);
                        PRAGMA user_version=1;
                        """;
                    setup.ExecuteNonQuery();
                    _initialized = true;
                }
                return action(connection);
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static string FormatDate(DateTimeOffset date) => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose()
    {
        // Connections are scoped to each operation; wait for the active transaction to finish.
        _disposed = true;
        _gate.Wait();
        _gate.Release();
    }
}
