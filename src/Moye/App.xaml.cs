using System.IO;
using System.Windows;
using Moye.Services;

namespace Moye;

/// <summary>One resolved library location for storage, preferences, and the single-instance lock.</summary>
public sealed record LibraryLocation(string DatabasePath, string PreferencesPath, string MutexName)
{
    public static LibraryLocation Resolve(string? dataDirectory = null, string? localApplicationData = null)
    {
        var directory = dataDirectory ?? Path.Combine(
            localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moye");
        var databasePath = Path.GetFullPath(Path.Combine(Path.GetFullPath(directory), "moye.db"));
        // Windows path spelling and trailing separators must not create another writer for the same library.
        var identity = databasePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        return new LibraryLocation(databasePath,
            Path.Combine(Path.GetDirectoryName(databasePath)!, "writing-preferences.json"), "Local\\Moye-" + hash[..24]);
    }
}

public partial class App : Application
{
    private System.Threading.Mutex? _instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        // UI language is English; document text and the user's number/date formats are preserved.
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        base.OnStartup(e);
        // Keep one writer/UI per library. Test launches can use an isolated directory.
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        var location = LibraryLocation.Resolve(dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1] : null);
        _instanceMutex = new(true, location.MutexName, out bool first);
        if (!first) { MessageBox.Show("Moye is already open. Please use the existing window.", "Moye"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moye")); File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moye", "error.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n"); } catch { }
            MessageBox.Show("The operation could not be completed. Previously saved notes are unaffected.\n\n" + args.Exception.Message, "Moye", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(new SqliteNotebookRepository(location.DatabasePath), new WritingPreferencesStore(location.PreferencesPath));
        MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instanceMutex?.Dispose(); base.OnExit(e); }
}
