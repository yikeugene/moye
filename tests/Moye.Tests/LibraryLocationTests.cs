using System.IO;
using Moye.Services;

namespace Moye.Tests;

public sealed class LibraryLocationTests
{
    [Fact]
    public void DefaultDatabasePathRemainsCompatibleWithExistingRepository()
    {
        using var repository = new SqliteNotebookRepository();
        var location = LibraryLocation.Resolve();
        Assert.Equal(repository.DatabasePath, location.DatabasePath);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(repository.DatabasePath)!, "writing-preferences.json"), location.PreferencesPath);
    }

    [Fact]
    public void DefaultAndExplicitDefaultDirectoryShareTheSameLiveMutex()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "Moye-library-identity", Guid.NewGuid().ToString("N"));
        var defaultLocation = LibraryLocation.Resolve(localApplicationData: localAppData);
        var explicitLocation = LibraryLocation.Resolve(Path.Combine(localAppData, "Moye") + Path.DirectorySeparatorChar);
        Assert.Equal(defaultLocation.DatabasePath, explicitLocation.DatabasePath);
        Assert.Equal(defaultLocation.PreferencesPath, explicitLocation.PreferencesPath);
        Assert.Equal(defaultLocation.MutexName, explicitLocation.MutexName);

        using var first = new Mutex(true, defaultLocation.MutexName, out var firstCreated);
        try
        {
            Assert.True(firstCreated);
            using var second = new Mutex(false, explicitLocation.MutexName, out var secondCreated);
            Assert.False(secondCreated);
        }
        finally { first.ReleaseMutex(); }
    }

    [Fact]
    public void CaseSlashesAndDotSegmentsDoNotCreateAnotherWriterIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Moye-library-identity", Guid.NewGuid().ToString("N"), "MixedCase");
        var baseline = LibraryLocation.Resolve(directory);
        var upperCase = LibraryLocation.Resolve(directory.ToUpperInvariant());
        var slashVariant = LibraryLocation.Resolve(directory.Replace('\\', '/') + "/");
        var dotVariant = LibraryLocation.Resolve(Path.Combine(directory, "temporary", "..", "."));
        Assert.Equal(baseline.MutexName, upperCase.MutexName);
        Assert.Equal(baseline.MutexName, slashVariant.MutexName);
        Assert.Equal(baseline.MutexName, dotVariant.MutexName);
        Assert.Equal(baseline.DatabasePath, dotVariant.DatabasePath);
    }

    [Fact]
    public void ExplicitLibrariesIsolateTheirDatabasePreferencesAndMutex()
    {
        var root = Path.Combine(Path.GetTempPath(), "Moye-library-identity", Guid.NewGuid().ToString("N"));
        var first = LibraryLocation.Resolve(Path.Combine(root, "first"));
        var second = LibraryLocation.Resolve(Path.Combine(root, "second"));
        Assert.NotEqual(first.DatabasePath, second.DatabasePath);
        Assert.NotEqual(first.PreferencesPath, second.PreferencesPath);
        Assert.NotEqual(first.MutexName, second.MutexName);
        Assert.Equal(Path.GetDirectoryName(first.DatabasePath), Path.GetDirectoryName(first.PreferencesPath));
        Assert.Equal(Path.GetDirectoryName(second.DatabasePath), Path.GetDirectoryName(second.PreferencesPath));
    }
}
