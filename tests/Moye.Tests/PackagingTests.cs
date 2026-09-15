using System.IO;
using System.Reflection;
using System.Text.Json;
using Moye.Services;

namespace Moye.Tests;

public sealed class PackagingTests
{
    [Fact]
    public async Task PackagedStorageCheckSavesAndReopensSyntheticData()
    {
        using var directory = new StorageTestDirectory();
        var checkDirectory = Path.Combine(directory.Root, "Synthetic library 測試");
        await StorageSelfCheck.RunAsync(checkDirectory);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(checkDirectory, "storage-check.json")));
        Assert.True(report.RootElement.GetProperty("success").GetBoolean());
        Assert.True(Version.Parse(report.RootElement.GetProperty("sqliteVersion").GetString()!) >= new Version(3, 50, 2));
    }

    [Fact]
    public async Task PackagedStorageCheckRefusesExistingDirectoriesWithoutWriting()
    {
        using var directory = new StorageTestDirectory();
        var sentinel = Path.Combine(directory.Root, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "Existing library");
        await Assert.ThrowsAsync<IOException>(() => StorageSelfCheck.RunAsync(directory.Root));
        Assert.Equal("Existing library", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(sentinel, Assert.Single(Directory.GetFiles(directory.Root)));
    }

    [Fact]
    public void DependencyFailureShowsInnerCauseAndWritesFullException()
    {
        using var directory = new StorageTestDirectory();
        var path = Path.Combine(directory.Root, "error.log");
        var cause = new FileLoadException("A required SQLite component was blocked.");
        cause.HResult = unchecked((int)0x800711C7);
        var error = new TypeInitializationException("Microsoft.Data.Sqlite.SqliteConnection", new TargetInvocationException(cause));
        var message = new ErrorReporter(path).Report(error);
        Assert.Contains("Windows application control", message);
        Assert.Contains(cause.Message, message);
        Assert.Contains(path, message);
        Assert.Contains(error.ToString(), File.ReadAllText(path));
    }

    [Fact]
    public void UnwritableLogPreservesOriginalErrorWithoutClaimingLogWasSaved()
    {
        using var directory = new StorageTestDirectory();
        var error = new IOException("The disk is full.");
        Assert.Equal(error.Message, new ErrorReporter(directory.Root).Report(error));
    }

    [Fact]
    public void GenericWrapperIncludesUnderlyingDependencyFailure()
    {
        var cause = new DllNotFoundException("Missing e_sqlite3.dll");
        var wrapper = new TypeInitializationException("Microsoft.Data.Sqlite.SqliteConnection", cause);
        var message = ErrorReporter.Describe(wrapper);
        Assert.Contains(wrapper.Message, message);
        Assert.Contains(cause.Message, message);
    }
}
