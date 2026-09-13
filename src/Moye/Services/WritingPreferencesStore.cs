using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moye.Models;

namespace Moye.Services;

public sealed record WritingPreferencesLoadResult(
    WritingPreferences Preferences, string? Warning = null, string? PreservedFilePath = null, bool CanSave = true);

/// <summary>Serializes local writing preferences using an atomic file replacement.</summary>
public sealed class WritingPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<InkTool>() }
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loadAttempted;
    private string? _saveBlockedReason;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moye", "writing-preferences.json");
    public string FilePath { get; }

    public WritingPreferencesStore(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? DefaultPath);

    public Task<WritingPreferencesLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }, cancellationToken);

    public Task SaveAsync(WritingPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.Version != WritingPreferences.CurrentVersion)
            throw new InvalidDataException("This writing-preferences version is not supported.");
        // Capture before queuing: later UI edits must not change this save.
        var snapshot = preferences.Normalize();
        return Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? temporaryPath = null;
            try
            {
                if (!_loadAttempted)
                {
                    var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                    if (loaded.Warning is not null) throw new IOException(loaded.Warning);
                }
                if (_saveBlockedReason is not null) throw new IOException(_saveBlockedReason);
                var directory = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(FilePath)) File.Replace(temporaryPath, FilePath, null);
                else File.Move(temporaryPath, FilePath);
                temporaryPath = null;
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                _gate.Release();
            }
        }, cancellationToken);
    }

    private async Task<WritingPreferencesLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        _loadAttempted = true;
        _saveBlockedReason = null;
        string json;
        try { json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { _loadAttempted = false; throw; }
        catch (FileNotFoundException) { return new(WritingPreferences.CreateDefault()); }
        catch (DirectoryNotFoundException) { return new(WritingPreferences.CreateDefault()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _saveBlockedReason = "Writing preferences could not be read. The existing file was kept. Reload settings before saving. " + exception.Message;
            return new(WritingPreferences.CreateDefault(), _saveBlockedReason, FilePath, false);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<WritingPreferences>(json, JsonOptions)
                ?? throw new JsonException("The settings file contains no preferences.");
            if (loaded.Version != WritingPreferences.CurrentVersion)
            {
                _saveBlockedReason = $"Writing-preferences version {loaded.Version} is not supported. The existing file was kept and will not be overwritten.";
                return new(WritingPreferences.CreateDefault(), _saveBlockedReason, FilePath, false);
            }
            var normalized = loaded.Normalize();
            var changed = JsonSerializer.Serialize(loaded, JsonOptions) != JsonSerializer.Serialize(normalized, JsonOptions);
            return new(normalized, changed ? "Some writing settings were invalid and have been reset to supported values." : null);
        }
        catch (JsonException)
        {
            var preservedPath = FilePath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            try { File.Move(FilePath, preservedPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _saveBlockedReason = "The writing-preferences file is damaged and could not be preserved separately. The original was kept. Reload settings before saving. " + exception.Message;
                return new(WritingPreferences.CreateDefault(), _saveBlockedReason, FilePath, false);
            }
            return new(WritingPreferences.CreateDefault(),
                "The writing-preferences file was damaged. Default tools are available, and the damaged file was preserved at " + preservedPath + ".",
                preservedPath);
        }
    }
}
