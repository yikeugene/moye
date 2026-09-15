using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moye.Models;

namespace Moye.Services;

/// <summary>Exercises the shipped executable with a new, synthetic library only.</summary>
public static class StorageSelfCheck
{
    public static async Task RunAsync(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        directory = Path.GetFullPath(directory);
        if (Path.Exists(directory)) throw new IOException("Storage checks require a new directory; existing libraries must not be used.");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "moye.db");
        var document = new NotebookDocument
        {
            Title = "Package check — 測試筆記",
            Pages = [new NotePage { Template = PaperTemplate.Grid, InkData = [1, 3, 5], Texts = [new NoteText { Text = "Saved text 測試" }] }]
        };
        string assetId;
        byte[] assetBytes = [2, 4, 6, 8];
        using (var repository = new SqliteNotebookRepository(databasePath))
        {
            await repository.InitializeAsync();
            assetId = (await repository.PutAssetAsync("sample.bin", "application/octet-stream", assetBytes)).Id;
            document.Pages[0].Images.Add(new NoteImage { AssetId = assetId });
            await repository.SaveAsync(document);
        }
        using (var reopened = new SqliteNotebookRepository(databasePath))
        {
            await reopened.InitializeAsync();
            var saved = await reopened.LoadAsync(document.Id);
            if (saved is null || saved.Title != document.Title || saved.Pages.Count != 1 ||
                saved.Pages[0].Template != document.Pages[0].Template ||
                saved.Pages[0].Texts.Single().Text != document.Pages[0].Texts.Single().Text ||
                !saved.Pages[0].InkData.SequenceEqual(document.Pages[0].InkData) ||
                saved.Pages[0].Images.Single().AssetId != assetId ||
                !(await reopened.GetAssetAsync(assetId)).Bytes.SequenceEqual(assetBytes) ||
                (await reopened.ListAsync()).Single().Id != document.Id)
                throw new InvalidDataException("The packaged application did not preserve the test notebook and asset.");
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if (!Equals(command.ExecuteScalar(), "ok")) throw new InvalidDataException("SQLite integrity check failed.");
        command.CommandText = "SELECT sqlite_version()";
        var sqliteVersion = (string)command.ExecuteScalar()!;
        var report = new { success = true, sqliteVersion, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), notebookRoundTrip = true, assetRoundTrip = true };
        await File.WriteAllTextAsync(Path.Combine(directory, "storage-check.json"), JsonSerializer.Serialize(report));
    }
}
