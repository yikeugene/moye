using Moye.Models;
using Moye.Services;

namespace Moye.Tests;

public sealed class WritingPreferencesTests
{
    [Fact]
    public async Task MissingSettingsReturnIndependentStableDefaultsWithoutCreatingAFile()
    {
        using var directory = new PreferencesDirectory();
        var store = new WritingPreferencesStore(directory.FilePath);
        var first = await store.LoadAsync();
        var second = await store.LoadAsync();
        Assert.Null(first.Warning);
        Assert.True(first.CanSave);
        Assert.False(File.Exists(directory.FilePath));
        Assert.Equal(4, first.Preferences.Presets.Count);
        Assert.Equal(first.Preferences.Presets.Select(p => p.Id), second.Preferences.Presets.Select(p => p.Id));
        Assert.Equal(InkTool.Highlighter, first.Preferences.Presets[3].Tool);
        Assert.Equal(11.3386, first.Preferences.Presets[3].Width);
        Assert.Equal(.5, first.Preferences.Presets[3].Opacity);
        Assert.True(first.Preferences.HoldToStraightenEnabled);
        Assert.False(first.Preferences.EraseHighlightOnly);
        first.Preferences.Presets[0].Name = "Changed";
        Assert.Equal("Black Pen", second.Preferences.Presets[0].Name);
    }

    [Fact]
    public async Task SaveReopenPreservesOrderSettingsAndCapturesTheSubmittedSnapshot()
    {
        using var directory = new PreferencesDirectory();
        var preferences = WritingPreferences.CreateDefault();
        var blue = preferences.Presets[1];
        blue.Name = "Blue notes 筆記";
        blue.Color = "#AA1234AB";
        blue.Width = 3.75;
        blue.Opacity = .65;
        blue.PressureSensitivity = false;
        blue.Smoothing = false;
        blue.IsFavorite = false;
        preferences.Presets.Remove(blue);
        preferences.Presets.Insert(0, blue);
        preferences.LastPresetId = blue.Id;
        preferences.EraserTool = InkTool.StrokeEraser;
        preferences.EraserSize = 48;
        preferences.EraseHighlightOnly = true;
        preferences.HoldToStraightenEnabled = false;
        var submitted = preferences.Snapshot();
        var store = new WritingPreferencesStore(directory.FilePath);
        var save = store.SaveAsync(preferences);
        blue.Name = "Later unsaved name";
        preferences.Presets.Clear();
        await save;

        var loaded = await new WritingPreferencesStore(directory.FilePath).LoadAsync();
        Assert.Null(loaded.Warning);
        Assert.Equal(submitted.Presets.Select(p => p.Id), loaded.Preferences.Presets.Select(p => p.Id));
        var saved = loaded.Preferences.Presets[0];
        Assert.Equal("Blue notes 筆記", saved.Name);
        Assert.Equal("#AA1234AB", saved.Color);
        Assert.Equal(3.75, saved.Width);
        Assert.Equal(.65, saved.Opacity);
        Assert.False(saved.PressureSensitivity);
        Assert.False(saved.Smoothing);
        Assert.False(saved.IsFavorite);
        Assert.Equal(saved.Id, loaded.Preferences.LastPresetId);
        Assert.Equal(InkTool.StrokeEraser, loaded.Preferences.EraserTool);
        Assert.Equal(48, loaded.Preferences.EraserSize);
        Assert.True(loaded.Preferences.EraseHighlightOnly);
        Assert.False(loaded.Preferences.HoldToStraightenEnabled);
    }

    [Fact]
    public async Task InvalidValuesAreNormalizedWithAWarningAndOriginalFileIsNotChangedByLoading()
    {
        using var directory = new PreferencesDirectory();
        const string json = """
            {"version":1,"presets":[
              {"id":"duplicate","name":" ","tool":"Lasso","color":"invalid","width":99,"opacity":0},
              {"id":"duplicate","name":"Marker","tool":"Highlighter","color":"#abc123","width":-5,"opacity":0.2}
            ],"lastPresetId":"missing","eraserTool":"Text","eraserSize":999}
            """;
        await File.WriteAllTextAsync(directory.FilePath, json);
        var result = await new WritingPreferencesStore(directory.FilePath).LoadAsync();
        Assert.NotNull(result.Warning);
        Assert.True(result.CanSave);
        Assert.Equal(json, await File.ReadAllTextAsync(directory.FilePath));
        var first = result.Preferences.Presets[0];
        var second = result.Preferences.Presets[1];
        Assert.Equal("Pen", first.Name);
        Assert.Equal(InkTool.Pen, first.Tool);
        Assert.Equal("#FF25334A", first.Color);
        Assert.Equal(24, first.Width);
        Assert.Equal(.1, first.Opacity);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("#FFABC123", second.Color);
        Assert.Equal(.5, second.Width);
        Assert.Equal(.5, second.Opacity);
        Assert.Equal(first.Id, result.Preferences.LastPresetId);
        Assert.Equal(InkTool.PointEraser, result.Preferences.EraserTool);
        Assert.Equal(WritingPreferences.MaximumEraserSize, result.Preferences.EraserSize);
    }

    [Fact]
    public void NormalizationRepairsEmptyAndNonFiniteSettingsWithoutMutatingTheSource()
    {
        var source = new WritingPreferences
        {
            Presets = [new() { Id = "", Width = double.NaN, Opacity = double.PositiveInfinity }],
            EraserSize = double.NegativeInfinity
        };
        var normalized = source.Normalize();
        Assert.True(double.IsNaN(source.Presets[0].Width));
        Assert.Equal("", source.Presets[0].Id);
        Assert.NotEmpty(normalized.Presets[0].Id);
        Assert.Equal(1.7008, normalized.Presets[0].Width);
        Assert.Equal(1, normalized.Presets[0].Opacity);
        Assert.Equal(20, normalized.EraserSize);
        Assert.Equal(4, new WritingPreferences { Presets = null! }.Normalize().Presets.Count);
    }

    [Fact]
    public async Task CorruptJsonIsPreservedAndDefaultsCanBeSavedWithoutDestroyingTheBadBytes()
    {
        using var directory = new PreferencesDirectory();
        byte[] damaged = "{\"presets\":[broken"u8.ToArray();
        await File.WriteAllBytesAsync(directory.FilePath, damaged);
        var store = new WritingPreferencesStore(directory.FilePath);
        var result = await store.LoadAsync();
        Assert.NotNull(result.Warning);
        Assert.True(result.CanSave);
        Assert.NotNull(result.PreservedFilePath);
        Assert.False(File.Exists(directory.FilePath));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(result.PreservedFilePath));
        await store.SaveAsync(result.Preferences);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(result.PreservedFilePath));
        Assert.Null((await new WritingPreferencesStore(directory.FilePath).LoadAsync()).Warning);
    }

    [Fact]
    public async Task FutureVersionRemainsUntouchedAndCannotBeOverwrittenWithDefaults()
    {
        using var directory = new PreferencesDirectory();
        const string future = "{\"version\":200,\"presets\":[],\"futureData\":\"keep me\"}";
        await File.WriteAllTextAsync(directory.FilePath, future);
        var store = new WritingPreferencesStore(directory.FilePath);
        var result = await store.LoadAsync();
        Assert.False(result.CanSave);
        Assert.Contains("200", result.Warning);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(result.Preferences));
        Assert.Equal(future, await File.ReadAllTextAsync(directory.FilePath));
    }

    [Fact]
    public async Task AtomicReplaceFailureKeepsCommittedSettingsAndPendingEditsCanBeRetried()
    {
        using var directory = new PreferencesDirectory();
        var store = new WritingPreferencesStore(directory.FilePath);
        await store.SaveAsync(WritingPreferences.CreateDefault());
        var committed = await File.ReadAllBytesAsync(directory.FilePath);
        var pending = WritingPreferences.CreateDefault();
        pending.Presets[0].Name = "Pending rename";
        using (var locked = new FileStream(directory.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(pending));
            Assert.Equal(committed, await File.ReadAllBytesAsync(directory.FilePath));
        }
        Assert.Equal("Pending rename", pending.Presets[0].Name);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        await store.SaveAsync(pending);
        Assert.Equal("Pending rename", (await new WritingPreferencesStore(directory.FilePath).LoadAsync()).Preferences.Presets[0].Name);
    }

    [Fact]
    public async Task UnreadableSettingsBlockSavesUntilAnExplicitSuccessfulReload()
    {
        using var directory = new PreferencesDirectory();
        var initial = WritingPreferences.CreateDefault();
        initial.Presets[0].Name = "Keep this preset";
        await new WritingPreferencesStore(directory.FilePath).SaveAsync(initial);
        var store = new WritingPreferencesStore(directory.FilePath);
        using (var locked = new FileStream(directory.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unavailable = await store.LoadAsync();
            Assert.False(unavailable.CanSave);
            Assert.NotNull(unavailable.Warning);
        }
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(WritingPreferences.CreateDefault()));
        var reloaded = await store.LoadAsync();
        Assert.True(reloaded.CanSave);
        Assert.Equal("Keep this preset", reloaded.Preferences.Presets[0].Name);
        await store.SaveAsync(reloaded.Preferences);
    }

    private sealed class PreferencesDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Moye-preferences-tests-" + Guid.NewGuid().ToString("N"));
        public string FilePath => System.IO.Path.Combine(Path, "writing-preferences.json");
        public PreferencesDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
